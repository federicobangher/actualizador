// Roblox Player: versión real (su carpeta de versiones) e instalación con el instalador oficial, como el usuario.
using System;
using System.Collections.Generic;
using System.IO;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    /// <summary>
    /// 2.0.20 (captura del usuario, 21/11/2026): «Roblox Player · Desconocida → 0.747 · Falló la instalación · winget no tiene una
    /// actualización aplicable». Dos causas:
    ///  1. El paquete Roblox.Roblox de winget-pkgs era de tipo «exe» (RobloxPlayerInstaller.exe con «-install -silent») hasta la
    ///     0.70x; desde entonces se publica como «portable». winget no puede pasar un programa instalado con su instalador a un
    ///     «portable» y responde 0x8A15002B siempre.
    ///  2. Roblox no registra su versión en «Aplicaciones» (winget dice «Desconocida»): vive en
    ///     %LOCALAPPDATA%\Roblox\Versions\version-&lt;código&gt;\RobloxPlayerBeta.exe, y es la versión de ese ejecutable.
    /// Ahora la versión sale de ese ejecutable, y la actualización la hace el instalador oficial que publica el mismo manifiesto
    /// (setup.rbxcdn.com, con el SHA-256 del catálogo y la firma de Roblox Corporation), ejecutado como el usuario (sin elevar):
    /// Roblox se instala en la carpeta del usuario, y como administrador se instalaría en otra parte (Program Files).
    /// </summary>
    public static class RobloxPlayer
    {
        public const string WingetId = "Roblox.Roblox";
        public const string Exe = "RobloxPlayerBeta.exe";
        public const string SilentArgs = "-install -silent";
        static readonly string[] Signers = { "Roblox Corporation" };

        public static bool Handles(WingetRow w)
        {
            return w != null && !w.MachineScope && string.Equals(w.Id, WingetId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Sólo para las pruebas: reemplaza la lectura de la carpeta de versiones (carpeta raíz → ejecutable → versión).</summary>
        internal static Func<string, Dictionary<string, string>> VersionsForTests;
        /// <summary>Sólo para las pruebas: reemplaza descarga + verificación + ejecución (dirección → código de salida, o null si falla la verificación).</summary>
        internal static Func<string, bool, int?> InstallForTests;

        /// <summary>La carpeta de Roblox («…\AppData\Local\Roblox») según su entrada en «Aplicaciones», o la del usuario.</summary>
        public static string RootOf(InstalledProgram p, string localAppData)
        {
            foreach (string raw in new[] { p != null ? p.InstallLocation : null, p != null ? p.DisplayIcon : null, p != null ? p.UninstallString : null })
            {
                string s = (raw ?? "").Trim().Trim('"');
                int i = s.IndexOf(@"\Roblox\", StringComparison.OrdinalIgnoreCase);
                if (i > 2 && ProgramRules.IsAbsoluteWindowsPath(s)) return s.Substring(0, i + @"\Roblox".Length);
                if (s.EndsWith(@"\Roblox", StringComparison.OrdinalIgnoreCase) && ProgramRules.IsAbsoluteWindowsPath(s)) return s;
            }
            return string.IsNullOrEmpty(localAppData) ? "" : Path.Combine(localAppData, "Roblox");
        }

        /// <summary>
        /// 2.0.22: todas las carpetas donde puede estar Roblox: la de su entrada en «Aplicaciones», la del usuario y las de Program Files
        /// (instalado alguna vez como administrador queda ahí, y el instalador como usuario va a la del usuario). Pública para las pruebas.
        /// </summary>
        public static List<string> Roots(InstalledProgram p, string localAppData, string programFiles, string programFilesX86)
        {
            var list = new List<string>();
            Action<string> add = delegate(string r)
            {
                if (string.IsNullOrEmpty(r) || r.Length < 4) return;
                string t = r.TrimEnd('\\');
                foreach (string x in list) if (string.Equals(x, t, StringComparison.OrdinalIgnoreCase)) return;
                list.Add(t);
            };
            add(RootOf(p, localAppData));
            if (!string.IsNullOrEmpty(localAppData)) add(localAppData.TrimEnd('\\') + @"\Roblox");
            if (!string.IsNullOrEmpty(programFilesX86)) add(programFilesX86.TrimEnd('\\') + @"\Roblox");
            if (!string.IsNullOrEmpty(programFiles)) add(programFiles.TrimEnd('\\') + @"\Roblox");
            return list;
        }

        static string Folder(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f); } catch { return ""; }
        }

        static List<string> RootsNow(InstalledProgram p)
        {
            return Roots(p, Folder(Environment.SpecialFolder.LocalApplicationData), Folder(Environment.SpecialFolder.ProgramFiles), Folder(Environment.SpecialFolder.ProgramFilesX86));
        }

        /// <summary>La versión más nueva entre todas las carpetas (y en cuál está), o "".</summary>
        public static string InstalledVersion(List<string> roots, out string where)
        {
            where = null;
            string best = "";
            foreach (string r in roots ?? new List<string>())
            {
                string v = InstalledVersion(r);
                if (v.Length > 0 && (best.Length == 0 || VersionUtil.IsNewer(v, best))) { best = v; where = r; }
            }
            return best;
        }

        /// <summary>¿Está en Program Files (instalado para todo el equipo)? Entonces el instalador va como administrador. Pública para las pruebas.</summary>
        public static bool IsMachineRoot(string root, string programFiles, string programFilesX86)
        {
            Func<string, bool> under = delegate(string b) { return !string.IsNullOrEmpty(b) && !string.IsNullOrEmpty(root) && root.StartsWith(b.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase); };
            return under(programFiles) || under(programFilesX86);
        }

        /// <summary>«0, 747, 0, 7470829» o «0.747.0.7470829» → «0.747.0.7470829» (o "" si no es una versión).</summary>
        public static string NormalizeVersion(string v)
        {
            string s = (v ?? "").Replace(", ", ".").Replace(',', '.').Trim();
            return VersionUtil.Parse(s) != null ? VersionUtil.Normalize(s) : "";
        }

        /// <summary>La versión instalada más nueva (de los RobloxPlayerBeta.exe de su carpeta de versiones), o "".</summary>
        public static string InstalledVersion(string root)
        {
            var found = VersionsForTests != null ? VersionsForTests(root) : ReadVersions(root);
            string best = "";
            if (found == null) return best;
            foreach (var kv in found)
            {
                string v = NormalizeVersion(kv.Value);
                if (v.Length > 0 && (best.Length == 0 || VersionUtil.IsNewer(v, best))) best = v;
            }
            return best;
        }

        static Dictionary<string, string> ReadVersions(string root)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string versions = Path.Combine(root ?? "", "Versions");
                if (root == null || root.Length < 4 || !Directory.Exists(versions)) return d;
                foreach (string dir in Directory.GetDirectories(versions, "version-*"))
                {
                    string exe = Path.Combine(dir, Exe);
                    if (!File.Exists(exe)) continue;
                    var fi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                    d[exe] = !string.IsNullOrEmpty(fi.FileVersion) ? fi.FileVersion : fi.ProductVersion;
                }
            }
            catch (Exception ex) { Log.Quiet("Carpeta de versiones de Roblox", ex); }
            return d;
        }

        /// <summary>¿La instalada ya es la publicada? Se compara con la precisión de la publicada (winget dice «0.747»).</summary>
        public static bool UpToDate(string installed, string available)
        {
            long[] a = VersionUtil.Parse(available);
            if (a == null || VersionUtil.Parse(installed) == null) return false;
            return !VersionUtil.IsNewer(available, ProgramRules.Truncate(installed, a.Length));
        }

        /// <summary>Completa la fila de winget de Roblox con la versión real y cómo se actualiza.</summary>
        public static void Resolve(UpdateCandidate c, WingetRow w, List<InstalledProgram> programs)
        {
            var p = WingetProvider.FindProgram(programs, w.Name) ?? WingetProvider.FindProgram(programs, "Roblox Player");
            string where;
            string installed = InstalledVersion(RootsNow(p), out where);
            string available = WingetTable.IsTruncated(w.Available) ? "" : w.Available;
            if (available.Length > 0) c.AvailableVersion = available;
            c.Publisher = string.IsNullOrEmpty(c.Publisher) ? "Roblox Corporation" : c.Publisher;
            if (installed.Length == 0)
            {
                c.Status = UpdateStatus.Disponible;
                c.Installable = available.Length > 0;
                c.Recommended = false;
                c.Detail = "No se encontró " + Exe + " en la carpeta de versiones de Roblox, así que no se sabe qué versión tenés. Roblox se actualiza solo " +
                           "cada vez que lo abrís; si lo marcás, se ejecuta su instalador oficial (verificado) como tu usuario.";
                return;
            }
            c.InstalledVersion = installed;
            if (available.Length > 0 && UpToDate(installed, available))
            {
                c.Status = UpdateStatus.AlDia;
                c.Installable = false;
                c.Recommended = false;
                c.AvailableVersion = available;
                c.Detail = "Ya está en la última (" + installed + ", de " + Exe + "; winget publica la " + available + "). Roblox además se actualiza solo al abrirlo.";
                return;
            }
            c.Status = UpdateStatus.Disponible;
            c.Installable = available.Length > 0;
            c.Recommended = c.Installable;
            c.Detail = "Instalada la " + installed + " (de " + Exe + "), publicada la " + available + ". winget ya no la puede actualizar (su paquete pasó a " +
                       "«portable»): se ejecuta el instalador oficial de Roblox (setup.rbxcdn.com, SHA-256 del catálogo de winget y firma de Roblox Corporation) " +
                       "como tu usuario, en silencio.";
        }

        /// <summary>Instala la versión publicada con el instalador oficial. El resultado queda en la fila.</summary>
        public static void Install(UpdateContext ctx, UpdateCandidate c, WingetRow w, bool interactive, Action<UpdateCandidate> changed)
        {
            InstallerSupport.Progress(c, changed, "Leyendo el instalador oficial de Roblox…");
            WingetManifest m;
            try { m = WingetDirect.FetchManifest(WingetId, c.AvailableVersion); }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception ex) { InstallerSupport.Fail(c, "No se pudo leer el instalador oficial en github.com/microsoft/winget-pkgs (" + Http.Describe(ex) + "). Abrí Roblox: se actualiza solo al iniciarse"); return; }
            WingetInstaller inst = null;
            foreach (var i in m.Installers) if (IsOfficialUrl(i.Url) && (i.Sha256 ?? "").Length == 64) { inst = i; if (i.Architecture == "x64") break; }
            if (inst == null) { InstallerSupport.Fail(c, "El manifiesto de Roblox no publica un instalador de setup.rbxcdn.com con su SHA-256. Abrí Roblox: se actualiza solo al iniciarse"); return; }

            var p = WingetProvider.FindProgram(ctx.Programs, w.Name) ?? WingetProvider.FindProgram(ctx.Programs, "Roblox Player");
            var roots = RootsNow(p);
            string where;
            string before = InstalledVersion(roots, out where);
            string target = m.Version.Length > 0 ? m.Version : c.AvailableVersion;
            // 2.0.22: instalado en Program Files (alguna vez como administrador): el instalador va como administrador, si no se
            // instalaría aparte en la carpeta del usuario y lo de Program Files seguiría viejo.
            bool machine = where != null && IsMachineRoot(where, Folder(Environment.SpecialFolder.ProgramFiles), Folder(Environment.SpecialFolder.ProgramFilesX86));

            // Primero en silencio; si no deja la versión nueva, con la ventana del propio instalador de Roblox (lo que hace al abrir
            // RobloxPlayerInstaller.exe: baja la versión, se cierra solo y, a lo sumo, abre Roblox). No se abre ninguna página.
            string lastLog = null;
            int lastExit = 0;
            foreach (bool visible in new[] { interactive, true })
            {
                long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5;
                int? exit;
                string why = null;
                if (InstallForTests != null) exit = InstallForTests(inst.Url, visible);
                else exit = DownloadVerifyRun(ctx, c, changed, inst, visible, machine, out why);
                if (exit == null) { InstallerSupport.Fail(c, why ?? "No se pudo verificar el instalador oficial: no se instala"); return; }
                lastExit = exit.Value;
                // 2.0.21: el instalador se reabre a sí mismo («Restarted» en su InstallerInfo.log) y el primer proceso termina enseguida.
                InstallerSupport.Progress(c, changed, visible ? "Instalando: el instalador de Roblox muestra su ventana y se cierra solo…" : "Instalando (el instalador de Roblox sigue en segundo plano)…");
                lastLog = WaitForInstaller(ctx, roots, target, since);
                string now = InstalledVersion(roots, out where);
                if (now.Length > 0 && UpToDate(now, target))
                {
                    c.Status = UpdateStatus.Instalada;
                    c.InstalledVersion = now;
                    c.Result = "Actualizada correctamente (instalador oficial de Roblox" + (visible && !interactive ? ", con su ventana: en silencio no había terminado" : "") +
                               (machine ? ", para todo el equipo)" : ", como tu usuario)");
                    return;
                }
                Log.Warn("Roblox: después del instalador" + (visible ? " con ventana" : " en silencio") + " sigue la " + (now.Length > 0 ? now : "(sin encontrar)") +
                         " (código " + exit.Value + ", registro: " + (lastLog ?? "sin novedades") + ", " + LogTail(roots) + ")");
                if (visible == interactive && interactive) break;      // ya fue con ventana
                if (ctx.CancelRequested) break;
            }
            string still = InstalledVersion(roots, out where);
            string reason = lastLog != null && lastLog.StartsWith("ERR:") ? "su instalador informó: " + lastLog.Substring(4).Trim()
                          : "su instalador terminó" + (lastExit == Unelevated.UnknownExit ? "" : " (código " + lastExit + ")") + " sin dejarla";
            InstallerSupport.Fail(c, "No se pudo dejar la " + target + " (sigue " + (still.Length > 0 ? "la " + still : (before.Length > 0 ? "la " + before : "sin encontrarse " + Exe)) +
                                     "): " + reason + ", ni en silencio ni con su ventana. " + LogTail(roots) + ". Abrí Roblox: se actualiza solo al iniciarse");
        }

        /// <summary>La última línea del registro de su instalador (para la fila y el diagnóstico).</summary>
        static string LogTail(List<string> roots)
        {
            foreach (string r in roots ?? new List<string>())
            {
                try
                {
                    string log = Path.Combine(r, @"RobloxPlayerInstaller\InstallerInfo.log");
                    if (!File.Exists(log)) continue;
                    string text;
                    using (var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var rd = new StreamReader(f)) text = rd.ReadToEnd();
                    var lines = text.Trim().Split('\n');
                    if (lines.Length > 0 && lines[lines.Length - 1].Trim().Length > 0) return "Último paso de su instalador: «" + Fmt.Truncate(lines[lines.Length - 1].Trim(), 120) + "»";
                }
                catch (Exception ex) { Log.Quiet("Registro del instalador de Roblox", ex); }
            }
            return "Su instalador no dejó registro";
        }

        /// <summary>Sólo para las pruebas: reemplaza la espera (devuelve lo mismo que <see cref="WaitForInstaller"/>).</summary>
        internal static Func<string> WaitForTests;
        /// <summary>Cuánto se espera al instalador que se reabre (lo normal: menos de un minuto).</summary>
        public static int WaitLimitMs = 10 * 60 * 1000;

        /// <summary>
        /// Espera a que el instalador termine: hasta que aparece la versión nueva, su registro dice «SUCCESS» o un error, o ya no queda
        /// ningún RobloxPlayerInstaller.exe abierto (con unos segundos de margen). Devuelve "OK", "ERR: motivo" o null (no se supo).
        /// </summary>
        static string WaitForInstaller(UpdateContext ctx, List<string> roots, string target, long since)
        {
            if (WaitForTests != null) return WaitForTests();
            string where;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastRunning = 0;
            while (sw.ElapsedMilliseconds < WaitLimitMs && !ctx.CancelRequested)
            {
                string v = InstalledVersion(roots, out where);
                if (v.Length > 0 && UpToDate(v, target)) return "OK";
                string verdict = null;
                foreach (string root in roots)
                {
                    string log = Path.Combine(root, @"RobloxPlayerInstaller\InstallerInfo.log");
                    string text = null;
                    try { if (File.Exists(log)) using (var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var r = new StreamReader(f)) text = r.ReadToEnd(); }
                    catch (Exception ex) { Log.Quiet("Registro del instalador de Roblox", ex); }
                    verdict = verdict ?? LogVerdict(text, since);
                }
                if (verdict != null) { System.Threading.Thread.Sleep(1500); return verdict; }
                if (InstallerRunning()) lastRunning = sw.ElapsedMilliseconds;
                else if (sw.ElapsedMilliseconds - lastRunning > 20000) return null;      // ya no corre ninguno
                System.Threading.Thread.Sleep(2000);
            }
            return null;
        }

        static bool InstallerRunning()
        {
            try
            {
                foreach (var e in ProcessList.Snapshot(false))
                    if (string.Equals(e.Name, "RobloxPlayerInstaller.exe", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception ex) { Log.Quiet("Procesos del instalador de Roblox", ex); }
            return false;
        }

        /// <summary>
        /// Lo que dice InstallerInfo.log desde <paramref name="since"/> (segundos Unix): líneas «1762114132 SUCCESS 18820 Installer success».
        /// "OK" si terminó bien, "ERR: …" si informó un error, null si sigue o no dice nada. Pública para las pruebas.
        /// </summary>
        public static string LogVerdict(string text, long since)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string result = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                var parts = line.Split(new[] { ' ' }, 4, StringSplitOptions.RemoveEmptyEntries);
                long t;
                if (parts.Length < 2 || !long.TryParse(parts[0], out t) || t < since) continue;
                string status = parts[1].ToUpperInvariant();
                if (status == "SUCCESS") result = "OK";
                else if (status == "FAILED" || status == "FAILURE" || status == "ERROR") result = "ERR: " + (parts.Length > 3 ? parts[3] : status);
                else if (status == "IN_PROGRESS") result = null;
            }
            return result;
        }

        /// <summary>Sólo lo que sirve el propio Roblox, por HTTPS.</summary>
        public static bool IsOfficialUrl(string url)
        {
            Uri u;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) && Http.IsHttps(u) &&
                   (u.Host.Equals("setup.rbxcdn.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase)) &&
                   u.AbsolutePath.EndsWith("-RobloxPlayerInstaller.exe", StringComparison.OrdinalIgnoreCase);
        }

        static int? DownloadVerifyRun(UpdateContext ctx, UpdateCandidate c, Action<UpdateCandidate> changed, WingetInstaller inst, bool interactive, bool machine, out string why)
        {
            why = null;
            string dir = BrowserProvider.NewDownloadDir();
            string userDir = null;
            try
            {
                string file = Path.Combine(dir, "RobloxPlayerInstaller.exe");
                InstallerSupport.Progress(c, changed, "Descargando el instalador oficial de Roblox…");
                ctx.Report("Descargando el instalador de Roblox de setup.rbxcdn.com…");
                Http.Download(inst.Url, file, 20 * 60 * 1000, ctx, delegate(Uri u) { return IsOfficialUrl(u.AbsoluteUri) || (Http.IsHttps(u) && u.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase)); });
                byte[] bytes;
                using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string got = InstallerSupport.Sha256(hold);
                    if (!got.Equals(inst.Sha256, StringComparison.OrdinalIgnoreCase)) { why = "El SHA-256 del instalador de Roblox no coincide con el del catálogo de winget: no se instala"; return null; }
                    string note;
                    int sig = Native.VerifyAuthenticodeStrict(file, out note);
                    if (sig != 0) { why = "El instalador de Roblox no tiene una firma digital válida (" + Fmt.Hex(sig) + "): no se instala"; return null; }
                    string subject = Native.SignerSubject(file) ?? "";
                    if (!InstallerRules.SignerAccepted(subject, Signers)) { why = "El instalador no está firmado por Roblox Corporation: no se instala"; return null; }
                    bytes = new byte[hold.Length];
                    hold.Position = 0;
                    int read = 0;
                    while (read < bytes.Length) { int n = hold.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                }
                ctx.Report("Instalando Roblox " + c.AvailableVersion + " (instalador oficial, como tu usuario)…");
                InstallerSupport.Progress(c, changed, "Instalando (instalador oficial de Roblox)…");
                ProcResult r;
                if (ctx.IsAdmin && !machine)
                {
                    // Como el usuario: la carpeta privada de descargas es sólo de administradores, así que va a una carpeta del usuario.
                    // Se ejecuta sin privilegios: aunque otro programa del usuario lo cambiara, no ganaría nada que no tenga.
                    userDir = Path.Combine(Path.GetTempPath(), "Actualizador-roblox-" + Guid.NewGuid().ToString("N").Substring(0, 12));
                    Directory.CreateDirectory(userDir);
                    string copy = Path.Combine(userDir, "RobloxPlayerInstaller.exe");
                    File.WriteAllBytes(copy, bytes);
                    r = Unelevated.Run(copy, interactive ? "" : SilentArgs, 20 * 60 * 1000, interactive);
                }
                else r = ProcessRunner.Run(file, interactive ? "" : SilentArgs, 20 * 60 * 1000);
                Log.Info("Instalador de Roblox terminó: " + r.Describe());
                if (!r.Started) { why = "No se pudo ejecutar el instalador de Roblox: " + r.Describe(); return null; }
                if (r.TimedOut) { why = "El instalador de Roblox no terminó en 20 minutos"; return null; }
                return r.ExitCode;
            }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception ex) { why = "No se pudo descargar el instalador de Roblox (" + Http.Describe(ex) + ")"; return null; }
            finally
            {
                BrowserProvider.TryDeleteDir(dir);
                if (userDir != null) { try { Directory.Delete(userDir, true); } catch (Exception qx) { Log.Quiet("Borrar la copia del instalador de Roblox", qx); } }
            }
        }
    }
}
