using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Navegadores (Chrome, Edge y Firefox). Se actualizan solos, y winget muchas veces no los
    /// reconoce o publica la versión días después, así que quedaban como «No verificable» o
    /// «Al día» mientras el propio navegador ya estaba bajando la nueva. Acá la versión disponible
    /// sale del servicio oficial de cada fabricante y la instalación usa su instalador oficial,
    /// verificado con la firma digital (y el hash SHA-256 publicado, en Edge) antes de ejecutarlo.
    /// </summary>
    public sealed class BrowserProvider : IUpdateProvider
    {
        public const string ProviderId = "navegadores";

        public bool Interactive;

        public string Name { get { return ProviderId; } }

        sealed class Plan
        {
            public BrowserInfo Browser;
            public string Latest;
            public BrowserCatalog.EdgeRelease Edge;
            public string Method;   // msi, firefox, acerca
        }

        public string Unavailable(UpdateContext ctx)
        {
            if (ctx.Os == null) return "La verificación de navegadores sólo funciona en Windows.";
            return null;
        }

        public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes)
        {
            var browsers = BrowserCatalog.Detect(ctx.Programs, ctx.Os.NativeArchitecture);
            if (browsers.Count == 0) return;
            ctx.Report("Navegadores: consultando la última versión con cada fabricante…");
            Http.EnableModernTls();

            string chromeJson = null, edgeJson = null, firefoxJson = null;
            var errors = new Dictionary<string, string>();
            foreach (var b in browsers)
            {
                ctx.ThrowIfCancelled();
                Refine(b);
                var c = new UpdateCandidate();
                c.Kind = UpdateKind.Programa;
                // El mismo nombre que en «Programas instalados» (p. ej. «Mozilla Firefox (x64 es-AR)»).
                c.Name = (b.RegistryNames.Count > 0 ? b.RegistryNames[0] : b.Name) + (b.PerUser ? " (usuario)" : "");
                c.Publisher = b.Publisher;
                c.InstalledVersion = b.Version.Length > 0 ? b.Version : "Desconocida";
                c.Identifier = "navegador:" + b.Key + (b.Esr ? "-esr" : "") + (b.PerUser ? ":usuario" : "");
                c.ProviderName = ProviderId;
                var plan = new Plan();
                plan.Browser = b;
                c.ProviderTag = plan;
                string error = null;
                try
                {
                    if (b.Key == "chrome")
                    {
                        string url = BrowserCatalog.ChromeReleasesUrl(b.Arch);
                        if (chromeJson == null) chromeJson = RecentPages.Get("json " + url, delegate { return Http.GetString(url, 20000); });
                        plan.Latest = BrowserCatalog.ParseChromeLatest(chromeJson);
                        c.Source = "Google (oficial)";
                    }
                    else if (b.Key == "edge")
                    {
                        if (edgeJson == null) edgeJson = RecentPages.Get("json " + BrowserCatalog.EdgeProductsUrl, delegate { return Http.GetString(BrowserCatalog.EdgeProductsUrl, 20000); });
                        plan.Edge = BrowserCatalog.ParseEdgeLatest(edgeJson, b.Arch);
                        plan.Latest = plan.Edge != null ? plan.Edge.Version : null;
                        c.Source = "Microsoft (oficial)";
                    }
                    else
                    {
                        if (firefoxJson == null) firefoxJson = RecentPages.Get("json " + BrowserCatalog.FirefoxVersionsUrl, delegate { return Http.GetString(BrowserCatalog.FirefoxVersionsUrl, 20000); });
                        plan.Latest = BrowserCatalog.ParseFirefoxLatest(firefoxJson, b.Esr);
                        c.Source = "Mozilla (oficial)";
                    }
                    if (plan.Latest == null) error = "la respuesta del fabricante no trae una versión reconocible";
                }
                catch (Exception ex) { error = Http.Describe(ex); }

                if (error != null)
                {
                    c.Status = UpdateStatus.Error;
                    c.Detail = "No se pudo consultar la última versión: " + error + ".";
                    errors[b.Name] = error;
                    output.Add(c);
                    Log.Warn("Navegador " + b.Name + ": " + error);
                    continue;
                }

                c.AvailableVersion = plan.Latest;
                c.Status = BrowserCatalog.Evaluate(b.Version, b.PendingVersion, plan.Latest);
                ChooseMethod(ctx, plan);
                string who = b.Key == "chrome" ? "Chrome" : b.Key == "edge" ? "Edge" : "Firefox";
                switch (c.Status)
                {
                    case UpdateStatus.AlDia:
                        c.Detail = "Coincide con la última versión estable publicada por " + Vendor(b) + ".";
                        break;
                    case UpdateStatus.RequiereReinicio:
                        c.Detail = "La versión " + b.PendingVersion + " ya está descargada: se aplica cuando cierres " + who + " por completo y lo vuelvas a abrir.";
                        break;
                    default:
                        c.Detail = "Versión estable más nueva publicada por " + Vendor(b) + ". " + MethodText(plan, who);
                        break;
                }
                c.Installable = c.Status == UpdateStatus.Disponible && plan.Method != null;
                c.Recommended = c.Installable && plan.Method != "acerca";
                output.Add(c);
            }
            if (errors.Count > 0)
                notes.Add("No se pudo verificar " + Fmt.JoinY(errors.Keys) + " con su fabricante: " + Fmt.JoinDistinct(errors.Values) +
                          ". Se muestra lo que informa winget.");
            else
                notes.Add("Navegadores verificados con los servicios oficiales de versiones de Google, Microsoft y Mozilla.");
        }

        static string Vendor(BrowserInfo b) { return b.Key == "chrome" ? "Google" : b.Key == "edge" ? "Microsoft" : "Mozilla"; }

        static string MethodText(Plan plan, string who)
        {
            switch (plan.Method)
            {
                case "msi": return "Se instala con el paquete MSI oficial, verificado con su firma digital; si " + who + " está abierto, la versión nueva se aplica al reabrirlo.";
                case "firefox": return "Se instala con el instalador oficial de Mozilla, verificado con su firma digital. Cerrá Firefox antes de actualizar.";
                case "acerca": return "Se abre «Acerca de " + who + "» para que el navegador se actualice solo; al terminar, tocá «Reiniciar».";
                default: return "Abrí " + who + ", menú › Ayuda › Acerca de, y el navegador se actualiza solo.";
            }
        }

        /// <summary>
        /// Instalador oficial en silencio cuando se puede (administrador e instalación del equipo);
        /// si no, la página «Acerca de» del propio navegador, que dispara su actualizador. Nunca se
        /// abre el navegador con privilegios de administrador.
        /// </summary>
        static void ChooseMethod(UpdateContext ctx, Plan plan)
        {
            var b = plan.Browser;
            bool admin = ctx.IsAdmin;
            string exe = ExePath(b);
            if (b.Key == "firefox")
            {
                bool writable = b.PerUser || SafePaths.IsUserWritableLocation(b.InstallDir);
                if (admin || writable) plan.Method = "firefox";
                else if (exe != null) plan.Method = "acerca";
                return;
            }
            if (!b.PerUser && admin && (b.Key == "chrome" || (plan.Edge != null && !string.IsNullOrEmpty(plan.Edge.MsiUrl))))
                plan.Method = "msi";
            else if (!admin && exe != null)
                plan.Method = "acerca";
        }

        static string ExePath(BrowserInfo b)
        {
            if (string.IsNullOrEmpty(b.InstallDir)) return null;
            try
            {
                string p = Path.Combine(b.InstallDir, b.ExeName);
                return File.Exists(p) ? p : null;
            }
            catch { return null; }
        }

        /// <summary>Completa con lo que dice el propio ejecutable (versión real, actualización descargada, arquitectura).</summary>
        static void Refine(BrowserInfo b)
        {
            try
            {
                if (b.InstallDir.Length == 0) b.InstallDir = GuessDir(b);
                string exe = ExePath(b);
                if (exe == null) return;
                string v = FileVersion(exe);
                if (VersionUtil.Parse(v) != null && b.Key != "firefox") b.Version = v;   // Firefox: la del registro es la «de producto»
                string arch = PeArch(exe);
                if (arch.Length > 0) b.Arch = arch;
                if (b.Key != "firefox")
                {
                    string staged = Path.Combine(b.InstallDir, "new_" + b.ExeName);
                    if (File.Exists(staged))
                    {
                        string sv = FileVersion(staged);
                        if (VersionUtil.IsNewer(sv, b.Version)) b.PendingVersion = sv;
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Navegador " + b.Name + ": " + ex.Message); }
        }

        static string GuessDir(BrowserInfo b)
        {
            var bases = new List<string>();
            string pf = SafePaths.ProgramFiles;
            bases.Add(pf);
            bases.Add(pf.Replace(@"\Program Files", @"\Program Files (x86)"));
            string rel = b.Key == "chrome" ? @"Google\Chrome\Application" : b.Key == "edge" ? @"Microsoft\Edge\Application" : "Mozilla Firefox";
            if (b.PerUser)
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                bases.Clear();
                if (!string.IsNullOrEmpty(local)) bases.Add(local);
            }
            foreach (string d in bases)
            {
                string dir = Path.Combine(d, rel);
                if (File.Exists(Path.Combine(dir, b.ExeName))) return dir;
            }
            return "";
        }

        static string FileVersion(string path)
        {
            var fi = FileVersionInfo.GetVersionInfo(path);
            string v = fi.ProductVersion;
            if (VersionUtil.Parse(v) == null) v = fi.FileVersion;
            return v == null ? "" : v.Trim();
        }

        /// <summary>Arquitectura del ejecutable según su encabezado PE.</summary>
        public static string PeArch(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return "";
                    fs.Position = 0x3C;
                    int pe = br.ReadInt32();
                    if (pe <= 0 || pe > fs.Length - 6) return "";
                    fs.Position = pe;
                    if (br.ReadUInt32() != 0x00004550) return "";
                    ushort machine = br.ReadUInt16();
                    if (machine == 0x8664) return "x64";
                    if (machine == 0x014C) return "x86";
                    if (machine == 0xAA64) return "arm64";
                }
            }
            catch (Exception qx) { Log.Quiet("Arquitectura del ejecutable (PE)", qx); }
            return "";
        }

        // ------------------------------------------------------------------ Instalación

        public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed)
        {
            Http.EnableModernTls();
            foreach (var c in items)
            {
                if (ctx.CancelRequested) { c.Status = UpdateStatus.Pendiente; c.Result = "Cancelada antes de empezar"; changed(c); continue; }
                var plan = c.ProviderTag as Plan;
                if (plan == null || plan.Method == null) { c.Status = UpdateStatus.Fallida; c.Result = "No hay un método de actualización para este navegador"; changed(c); continue; }
                c.Status = UpdateStatus.Instalando;
                c.Result = "";
                changed(c);
                try
                {
                    switch (plan.Method)
                    {
                        case "msi": InstallMsi(ctx, c, plan); break;
                        case "firefox": InstallFirefox(ctx, c, plan); break;
                        default: OpenAbout(ctx, c, plan); break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Actualización de " + c.Name, ex);
                    c.Status = UpdateStatus.Fallida;
                    c.Result = Http.Describe(ex);
                }
                changed(c);
            }
        }

        void InstallMsi(UpdateContext ctx, UpdateCandidate c, Plan plan)
        {
            var b = plan.Browser;
            string url = b.Key == "chrome" ? BrowserCatalog.ChromeMsiUrl(b.Arch) : plan.Edge.MsiUrl;
            string signer = b.Key == "chrome" ? BrowserCatalog.ChromeSigner : BrowserCatalog.EdgeSigner;
            string expectedHash = b.Key == "edge" ? plan.Edge.Sha256 : null;
            string dir = NewDownloadDir();
            string file = Path.Combine(dir, b.Key + "-" + b.Arch + ".msi");
            try
            {
                ctx.Report("Descargando " + b.Name + " " + plan.Latest + " desde " + new Uri(url).Host + "…");
                Http.Download(url, file, 30 * 60 * 1000, ctx);
                using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string why = Verify(hold, file, signer, expectedHash);
                    if (why != null) { c.Status = UpdateStatus.Fallida; c.Result = why; Log.Warn(c.Name + ": " + why); return; }
                    ctx.Report("Instalando " + b.Name + " " + plan.Latest + " (paquete oficial verificado)…");
                    string args = "/i " + ProcessRunner.QuoteArg(file) + (Interactive ? " /passive" : " /qn") + " /norestart";
                    string log = InstallerSupport.MsiLogPath(b.Key, null);
                    if (log != null) args += " /l*v " + ProcessRunner.QuoteArg(log);
                    string msiexec = Path.Combine(SafePaths.System32, "msiexec.exe");
                    Log.Info("msiexec " + args);
                    var r = ProcessRunner.RunInstaller(msiexec, args, 45 * 60 * 1000, InstallerEnvironment(dir), dir, hold);
                    Log.Info("msiexec terminó: " + r.Describe());
                    ApplyMsiResult(c, r, b, plan.Latest, log);
                }
            }
            finally { TryDeleteDir(dir); }
        }

        static void ApplyMsiResult(UpdateCandidate c, ProcResult r, BrowserInfo b, string latest, string log)
        {
            if (!r.Started) { c.Status = UpdateStatus.Fallida; c.Result = r.Describe(); return; }
            if (r.TimedOut) { c.Status = UpdateStatus.Fallida; c.Result = "El instalador no terminó en 45 minutos"; return; }
            switch (r.ExitCode)
            {
                case 0:
                case 3010:
                case 1641:
                    AfterInstall(c, b, latest, r.ExitCode != 0);
                    return;
                case 1602: c.Status = UpdateStatus.Fallida; c.Result = "Cancelada en el instalador"; return;
                case 1618: c.Status = UpdateStatus.Fallida; c.Result = "Hay otra instalación en curso; esperá a que termine y reintentá"; return;
                case 1625: c.Status = UpdateStatus.Fallida; c.Result = "Una directiva del equipo bloquea la instalación"; return;
                case 1638: c.Status = UpdateStatus.Fallida; c.Result = "Windows Installer informa otra versión instalada; actualizalo desde «Acerca de» del navegador"; return;
                default:
                    c.Status = UpdateStatus.Fallida;
                    // La causa, leída del registro de Windows Installer (como en las demás instalaciones MSI).
                    string why = log != null ? MsiLogAnalyzer.ExplainFile(log) : "";
                    c.Result = "msiexec devolvió " + r.ExitCode + (why.Length > 0 ? ". Según el registro del instalador: " + why
                                                                                  : log != null ? "; el detalle está en " + Path.GetFileName(log) + " (carpeta de registros)" : "");
                    return;
            }
        }

        /// <summary>Mira qué quedó realmente instalado (o descargado a la espera de reabrir el navegador).</summary>
        static void AfterInstall(UpdateCandidate c, BrowserInfo b, string latest, bool reboot)
        {
            string before = b.Version;
            b.PendingVersion = null;
            Refine(b);
            string now = b.Version;
            string who = b.Key == "chrome" ? "Chrome" : b.Key == "edge" ? "Edge" : "Firefox";
            if (!string.IsNullOrEmpty(b.PendingVersion) && !VersionUtil.IsNewer(latest, b.PendingVersion))
            {
                c.Status = UpdateStatus.Instalada;
                c.Result = "Descargada la " + b.PendingVersion + ": se aplica al cerrar y volver a abrir " + who;
                return;
            }
            if (VersionUtil.Parse(now) != null && !VersionUtil.IsNewer(latest, now))
            {
                c.InstalledVersion = now;
                c.Status = reboot ? UpdateStatus.RequiereReinicio : UpdateStatus.Instalada;
                c.Result = reboot ? "Actualizada; requiere reiniciar" : "Actualizada correctamente";
                return;
            }
            if (VersionUtil.IsNewer(now, before)) c.InstalledVersion = now;
            c.Status = UpdateStatus.Instalada;
            c.Result = "El instalador terminó bien; si " + who + " sigue en la " + (now ?? before) + ", cerralo por completo y volvé a abrirlo";
        }

        void InstallFirefox(UpdateContext ctx, UpdateCandidate c, Plan plan)
        {
            var b = plan.Browser;
            if (!Interactive && IsRunning("firefox"))
            {
                c.Status = UpdateStatus.Fallida;
                c.Result = "Firefox está abierto: cerralo y reintentá (o marcá «Mostrar la ventana de cada instalador»)";
                return;
            }
            string url = BrowserCatalog.FirefoxInstallerUrl(b.Esr, b.Arch, b.Lang);
            string dir = NewDownloadDir();
            string file = Path.Combine(dir, "firefox-setup-" + b.Arch + ".exe");
            try
            {
                ctx.Report("Descargando " + b.Name + " " + plan.Latest + " (" + b.Lang + ") desde download.mozilla.org…");
                Http.Download(url, file, 30 * 60 * 1000, ctx);
                using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string why = Verify(hold, file, BrowserCatalog.FirefoxSigner, null);
                    if (why != null) { c.Status = UpdateStatus.Fallida; c.Result = why; Log.Warn(c.Name + ": " + why); return; }
                    // No se usa la versión del archivo: el instalador es un autoextraíble de 7-Zip y
                    // su versión es la de 7-Zip («18.05»), no la de Firefox.
                    ctx.Report("Instalando " + b.Name + " " + plan.Latest + " (instalador oficial verificado)…");
                    // Sin /InstallDirectoryPath: el instalador encuentra y actualiza la instalación existente.
                    string args = Interactive ? "" : "/S";
                    Log.Info("Instalador de Firefox" + (args.Length > 0 ? " " + args : " (interactivo: con sus ventanas)"));
                    var r = ProcessRunner.RunInstaller(file, args, 45 * 60 * 1000, InstallerEnvironment(dir), dir, hold);
                    Log.Info("Instalador de Firefox terminó: " + r.Describe());
                    if (!r.Started) { c.Status = UpdateStatus.Fallida; c.Result = r.Describe(); return; }
                    if (r.TimedOut) { c.Status = UpdateStatus.Fallida; c.Result = "El instalador no terminó en 45 minutos"; return; }
                    if (r.ExitCode != 0) { c.Status = UpdateStatus.Fallida; c.Result = "El instalador de Firefox devolvió " + r.ExitCode; return; }
                    string reg = FirefoxRegistryVersion(b);
                    if (VersionUtil.Parse(reg) != null) b.Version = reg;
                    if (VersionUtil.Parse(b.Version) != null && !VersionUtil.IsNewer(plan.Latest, b.Version))
                    {
                        c.InstalledVersion = b.Version;
                        c.Status = UpdateStatus.Instalada;
                        c.Result = "Actualizada correctamente";
                    }
                    else
                    {
                        c.Status = UpdateStatus.Instalada;
                        c.Result = "El instalador terminó bien; volvé a buscar actualizaciones para confirmar la versión";
                    }
                }
            }
            finally { TryDeleteDir(dir); }
        }

        static string FirefoxRegistryVersion(BrowserInfo b)
        {
            try
            {
                var hive = b.PerUser ? Microsoft.Win32.RegistryHive.CurrentUser : Microsoft.Win32.RegistryHive.LocalMachine;
                string key = b.Esr ? @"SOFTWARE\Mozilla\Mozilla Firefox ESR" : @"SOFTWARE\Mozilla\Mozilla Firefox";
                string cur = Reg.Str(hive, key, "CurrentVersion");
                if (string.IsNullOrEmpty(cur)) return null;
                int sp = cur.IndexOf(' ');
                return sp > 0 ? cur.Substring(0, sp) : cur;
            }
            catch { return null; }
        }

        void OpenAbout(UpdateContext ctx, UpdateCandidate c, Plan plan)
        {
            var b = plan.Browser;
            string exe = ExePath(b);
            if (exe == null || ctx.IsAdmin)
            {
                c.Status = UpdateStatus.Fallida;
                c.Result = "Abrí el navegador y andá a menú › Ayuda › Acerca de: se actualiza solo";
                return;
            }
            string page = b.Key == "chrome" ? "chrome://settings/help" : b.Key == "edge" ? "edge://settings/help" : "about:preferences#general";
            var psi = new ProcessStartInfo(exe, page);
            psi.UseShellExecute = false;
            try { psi.WorkingDirectory = b.InstallDir; } catch { }
            using (Process.Start(psi)) { }
            string who = b.Key == "chrome" ? "Chrome" : b.Key == "edge" ? "Edge" : "Firefox";
            ctx.Report("Se abrió «Acerca de " + who + "»: el navegador está buscando e instalando la " + plan.Latest + "…");
            // Se espera un rato a que el propio navegador deje descargada la versión nueva.
            var until = DateTime.UtcNow.AddMinutes(4);
            while (DateTime.UtcNow < until && !ctx.CancelRequested)
            {
                System.Threading.Thread.Sleep(5000);
                b.PendingVersion = null;
                Refine(b);
                if ((b.PendingVersion != null && !VersionUtil.IsNewer(plan.Latest, b.PendingVersion)) ||
                    !VersionUtil.IsNewer(plan.Latest, b.Version))
                    break;
            }
            c.Status = UpdateStatus.Instalada;
            if (b.PendingVersion != null)
                c.Result = "Descargada la " + b.PendingVersion + ": tocá «Reiniciar» en " + who + " para aplicarla";
            else if (!VersionUtil.IsNewer(plan.Latest, b.Version))
            { c.InstalledVersion = b.Version; c.Result = "Actualizada correctamente"; }
            else
                c.Result = who + " sigue actualizándose en su ventana; cuando termine, tocá «Reiniciar»";
        }

        static bool IsRunning(string processName)
        {
            try
            {
                var ps = Process.GetProcessesByName(processName);
                bool any = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                return any;
            }
            catch { return false; }
        }

        /// <summary>
        /// Verifica el archivo mientras está abierto sin permitir escritura ni borrado (así nadie lo
        /// puede cambiar entre la verificación y la ejecución). null si está todo bien; si no, el motivo.
        /// </summary>
        static string Verify(FileStream hold, string file, string signer, string expectedSha256)
        {
            if (!string.IsNullOrEmpty(expectedSha256))
            {
                hold.Position = 0;
                string got;
                using (var sha = SHA256.Create()) got = BitConverter.ToString(sha.ComputeHash(hold)).Replace("-", "");
                hold.Position = 0;
                if (!got.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    return "El hash SHA-256 de la descarga no coincide con el publicado por el fabricante: no se instala";
            }
            string note;
            int rc = Native.VerifyAuthenticodeStrict(file, out note);
            if (note != null) Log.Warn(Path.GetFileName(file) + ": " + note);
            if (rc == Native.CERT_E_REVOKED) return "El certificado que firma el instalador fue REVOCADO por su emisor: no se instala";
            if (rc != 0) return "La firma digital del instalador no es válida (" + Fmt.Hex(rc) + "): no se instala";
            string subject;
            try { subject = Native.SignerSubject(file); }
            catch (Exception ex) { return "No se pudo leer el firmante del instalador (" + ex.Message + "): no se instala"; }
            if (!BrowserCatalog.SubjectIsFrom(subject, signer))
                return "El instalador está firmado por «" + subject + "», no por " + signer + ": no se instala";
            return null;
        }

        /// <summary>Carpeta privada para la descarga (ver <see cref="SafePaths.CreatePrivateDirectory"/>).</summary>
        internal static string NewDownloadDir()
        {
            return SafePaths.CreatePrivateDirectory(Path.Combine(AppInfo.DataDirectory, "descargas"));
        }

        /// <summary>
        /// TEMP y TMP del instalador dentro de la carpeta privada: los instaladores se extraen en
        /// la carpeta temporal y ejecutan desde ahí; la del usuario la puede escribir cualquier
        /// proceso sin privilegios.
        /// </summary>
        internal static Dictionary<string, string> InstallerEnvironment(string privateDir)
        {
            string tmp = Path.Combine(privateDir, "tmp");
            Directory.CreateDirectory(tmp);
            var env = new Dictionary<string, string>();
            env["TEMP"] = tmp;
            env["TMP"] = tmp;
            return env;
        }

        /// <summary>
        /// Borra la carpeta privada de una descarga. Algunos instaladores dejan un auxiliar que se borra
        /// solo un momento después (AIMP: «delA016.tmp»): se reintenta unos segundos y, si sigue en uso,
        /// queda para <see cref="CleanupStaleDownloadDirs"/> en la próxima actualización.
        /// </summary>
        internal static void TryDeleteDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            Exception last = null;
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    if (!Directory.Exists(dir)) return;
                    Directory.Delete(dir, true);
                    return;
                }
                catch (Exception ex) { last = ex; }
                System.Threading.Thread.Sleep(1000);
            }
            Log.Info("La carpeta temporal " + dir + " todavía tiene un archivo en uso (" + (last != null ? last.Message : "") + "); se borra en la próxima actualización.");
        }

        static readonly System.Text.RegularExpressions.Regex DownloadDirName =
            new System.Text.RegularExpressions.Regex(@"^(?:Actualizador|SuperProgram)-[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Borra las carpetas privadas de descargas que quedaron de antes (más de 2 horas). Elevado, en
        /// C:\Windows\Temp sólo se borran las que tienen como dueño a Administradores o SYSTEM (como las
        /// crea Actualizador) y no son enlaces: una carpeta con ese nombre creada por un usuario común
        /// no se toca.
        /// </summary>
        internal static void CleanupStaleDownloadDirs()
        {
            try
            {
                string root = SafePaths.Elevated ? Path.Combine(SafePaths.Windows, "Temp") : Path.Combine(AppInfo.DataDirectory, "descargas");
                if (!Directory.Exists(root)) return;
                var limit = DateTime.UtcNow.AddHours(-2);
                var stale = new List<string>(Directory.GetDirectories(root, AppInfo.Name + "-*"));
                stale.AddRange(Directory.GetDirectories(root, AppInfo.LegacyName + "-*"));   // 2.0.11: las del nombre viejo
                foreach (string d in stale)
                {
                    try
                    {
                        if (!DownloadDirName.IsMatch(Path.GetFileName(d))) continue;
                        var info = new DirectoryInfo(d);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.CreationTimeUtc > limit) continue;
                        if (SafePaths.Elevated)
                        {
                            var owner = info.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
                            if (owner == null || !(owner.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) ||
                                                   owner.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid))) continue;
                        }
                        Directory.Delete(d, true);
                        Log.Info("Se borró la carpeta temporal que había quedado: " + d);
                    }
                    catch (Exception ex) { Log.Info("Carpeta temporal " + d + " todavía en uso: " + ex.Message); }
                }
            }
            catch (Exception ex) { Log.Warn("Limpieza de carpetas temporales: " + ex.Message); }
        }
    }
}
