using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Tecnología con la que se instaló un programa (según su clave de desinstalación).</summary>
    public enum InstallTech { Desconocida, Msi, Inno, Nsis, Exe }

    /// <summary>Cómo desinstalar un programa: ejecutable, argumentos y qué tecnología se usa.</summary>
    public sealed class UninstallPlan
    {
        /// <summary>Ruta completa del desinstalador, o «msiexec.exe» (se ejecuta desde System32).</summary>
        public string Exe = "";
        public string Args = "";
        public bool UsesMsiexec;
        /// <summary>Para el registro y los mensajes: «Windows Installer {…}», «Inno Setup»…</summary>
        public string Description = "";
    }

    /// <summary>
    /// Lo que se deduce de la clave de desinstalación de un programa para actualizarlo sin romperlo:
    /// la arquitectura real (TeamViewer de 32 bits en un Windows de 64: winget instalaba el de 64 y
    /// el instalador abortaba con el código 2), la tecnología de instalación (Double Commander
    /// instalado con su .exe de Inno Setup, cuando winget sólo publica el MSI) y cómo desinstalarlo
    /// en silencio. Sin dependencias de Windows: se prueba en Linux (el lector PE se inyecta).
    /// </summary>
    public static class ProgramRules
    {
        // ------------------------------------------------------------------ Actualizador mismo

        /// <summary>Nombre y editor con que Instalar.cmd registra Actualizador en «Aplicaciones instaladas».</summary>
        public const string SelfDisplayName = "Actualizador", SelfPublisher = "Fede";

        /// <summary>
        /// ¿Es la entrada que deja Instalar.cmd? Por nombre y editor del registro, o por el
        /// identificador que le da winget a esa clave («ARP\Machine\X64\Actualizador»).
        /// </summary>
        public static bool IsSelf(string name, string publisher, string wingetId)
        {
            if (!string.Equals((name ?? "").Trim(), SelfDisplayName, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals((publisher ?? "").Trim(), SelfPublisher, StringComparison.OrdinalIgnoreCase)) return true;
            string id = (wingetId ?? "").Trim();
            return id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase) && id.EndsWith("\\" + SelfDisplayName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// La fila de Actualizador: no está en winget (se instala con Instalar.cmd), así que en vez de
        /// «No verificable» se compara con la versión que está corriendo.
        /// </summary>
        public static void MarkSelf(UpdateCandidate c, string installedVersion, string runningVersion)
        {
            c.Installable = false;
            c.Recommended = false;
            c.Source = SelfDisplayName;
            string inst = string.IsNullOrEmpty(installedVersion) || installedVersion == "Desconocida" ? "" : installedVersion.Trim();
            if (inst.Length > 0 && VersionUtil.IsNewer(runningVersion, inst))
            {
                c.Status = UpdateStatus.Disponible;
                c.AvailableVersion = runningVersion;
                c.Detail = "Es este programa: la copia instalada es la " + inst + " y esta es la " + runningVersion +
                           ". Para actualizarla, ejecutá como administrador el Instalar.cmd de la " + runningVersion + ".";
                return;
            }
            c.Status = UpdateStatus.AlDia;
            c.AvailableVersion = inst.Length > 0 ? inst : runningVersion;
            c.Detail = "Es este programa (" + SelfDisplayName + " " + (inst.Length > 0 ? inst : runningVersion) + "). No está en winget: una versión nueva se instala " +
                       "con el Instalar.cmd de esa versión, y se desinstala desde Configuración › Aplicaciones › Aplicaciones instaladas.";
        }

        const RegexOptions RX = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        static readonly Regex GuidRx = new Regex(@"\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}", RX);
        static readonly Regex AbsolutePath = new Regex(@"^[A-Za-z]:\\[^""<>|*?\r\n\t]+$", RegexOptions.Compiled);
        static readonly Regex InnoUninstaller = new Regex(@"^unins\d{3}\.exe$", RX);
        static readonly Regex NsisUninstaller = new Regex(@"^(?:uninst|uninstall)\.exe$", RX);
        /// <summary>Desinstaladores y auxiliares: su arquitectura no dice nada del programa (los de Inno y NSIS son de 32 bits siempre).</summary>
        static readonly Regex HelperExe = new Regex(@"^(?:unins\d{3}|uninst|uninstall|uninstaller|au_|helper|setup|install|installer|update|updater)\.exe$|^uninstall", RX);
        /// <summary>Instaladores de paquetes de runtimes (vc_redist.x64.exe, windowsdesktop-runtime-8.0.8-win-x64.exe…).</summary>
        static readonly Regex BundleExe = new Regex(@"^(?:vc_?redist|vcredist|windowsdesktop-runtime|dotnet-runtime|aspnetcore-runtime|dotnet-hosting|ndp\d)", RX);

        // ================================================================ Líneas de comando

        /// <summary>Nombre del archivo de una ruta de Windows (también en Linux, donde «\» no separa).</summary>
        public static string FileNameOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int i = path.LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        /// <summary>Carpeta de una ruta de Windows, sin la barra final.</summary>
        public static string DirectoryOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int i = path.LastIndexOfAny(new[] { '\\', '/' });
            return i > 0 ? path.Substring(0, i) : "";
        }

        public static bool IsAbsoluteWindowsPath(string path)
        {
            return !string.IsNullOrEmpty(path) && AbsolutePath.IsMatch(path) && path.IndexOf(@"\..\", StringComparison.Ordinal) < 0;
        }

        /// <summary>
        /// Separa «"C:\Ruta con espacios\unins000.exe" /SILENT» o «C:\Ruta con espacios\unins000.exe /SILENT»
        /// en ejecutable y argumentos. Sólo acepta un .exe con ruta absoluta de disco (nada de rutas
        /// relativas, que se buscarían en el PATH, ni de recursos de red).
        /// </summary>
        public static bool SplitCommand(string command, out string exe, out string args)
        {
            exe = null; args = "";
            if (string.IsNullOrEmpty(command)) return false;
            string c = command.Trim();
            if (c.StartsWith("\""))
            {
                int q = c.IndexOf('"', 1);
                if (q <= 1) return false;
                exe = c.Substring(1, q - 1).Trim();
                args = c.Substring(q + 1).Trim();
            }
            else
            {
                int e = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (e <= 0) return false;
                int end = e + 4;
                if (end < c.Length && c[end] != ' ' && c[end] != '\t') return false;   // «.exe» dentro de otra palabra
                exe = c.Substring(0, end).Trim();
                args = c.Substring(end).Trim();
            }
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !IsAbsoluteWindowsPath(exe)) { exe = null; args = ""; return false; }
            return true;
        }

        /// <summary>El ejecutable de una línea de comandos del registro, o "" si no se reconoce.</summary>
        public static string ExeOf(string command)
        {
            string exe, args;
            return SplitCommand(command, out exe, out args) ? exe : "";
        }

        /// <summary>UninstallString tal cual; si no se cargó, el que haya.</summary>
        static string Uninstaller(InstalledProgram p)
        {
            if (!string.IsNullOrEmpty(p.UninstallCommand)) return p.UninstallCommand;
            return p.UninstallString ?? "";
        }

        static string LastSegment(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            int i = key.LastIndexOf('\\');
            return i >= 0 ? key.Substring(i + 1) : key;
        }

        /// <summary>Código de producto MSI: el nombre de la clave ({GUID}) o el de «MsiExec.exe /X{GUID}».</summary>
        public static string MsiProductCode(InstalledProgram p)
        {
            if (p == null) return null;
            string key = LastSegment(p.RegistryKey);
            var km = GuidRx.Match(key);
            if (km.Success && km.Value.Length == key.Length) return key.ToUpperInvariant();
            foreach (string cmd in new[] { p.UninstallCommand, p.QuietUninstallCommand, p.UninstallString })
            {
                if (string.IsNullOrEmpty(cmd) || cmd.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var m = GuidRx.Match(cmd);
                if (m.Success) return m.Value.ToUpperInvariant();
            }
            return null;
        }

        // ================================================================ Tecnología

        /// <summary>
        /// MSI (WindowsInstaller = 1, o la clave es un código de producto y se desinstala con msiexec),
        /// Inno Setup (clave «…_is1» o desinstalador «unins000.exe»), NSIS (desinstalador «uninst.exe»
        /// o «uninstall.exe») u otro ejecutable.
        /// </summary>
        public static InstallTech Technology(InstalledProgram p)
        {
            if (p == null) return InstallTech.Desconocida;
            string key = LastSegment(p.RegistryKey);
            bool guidKey = GuidRx.IsMatch(key) && GuidRx.Match(key).Value.Length == key.Length;
            string command = Uninstaller(p);
            bool msiexec = command.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0;
            if (p.IsWindowsInstaller || (msiexec && MsiProductCode(p) != null) || (guidKey && msiexec)) return InstallTech.Msi;
            string un = FileNameOf(ExeOf(command));
            if (key.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) || InnoUninstaller.IsMatch(un)) return InstallTech.Inno;
            if (NsisUninstaller.IsMatch(un)) return InstallTech.Nsis;
            if (un.Length > 0) return InstallTech.Exe;
            return InstallTech.Desconocida;
        }

        public static string TechText(InstallTech t)
        {
            switch (t)
            {
                case InstallTech.Msi: return "MSI";
                case InstallTech.Inno: return "EXE (Inno Setup)";
                case InstallTech.Nsis: return "EXE (NSIS)";
                case InstallTech.Exe: return "EXE";
                default: return "desconocido";
            }
        }

        /// <summary>Tipo de instalador del manifiesto de winget, en palabras.</summary>
        public static string WingetTypeText(string type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "msi":
                case "wix": return "MSI";
                case "inno": return "EXE (Inno Setup)";
                case "nullsoft": return "EXE (NSIS)";
                case "burn": return "EXE (WiX Burn)";
                case "exe": return "EXE";
                default: return type ?? "";
            }
        }

        // ================================================================ Desinstalación

        /// <summary>
        /// Cómo desinstalar el programa. MSI: «msiexec /x {código}». Si el programa registra una
        /// desinstalación silenciosa (QuietUninstallString), ésa. Inno Setup: su desinstalador con
        /// /VERYSILENT /SUPPRESSMSGBOXES /NORESTART. NSIS: /S. En modo interactivo, el desinstalador
        /// tal cual, con sus ventanas. null (con el motivo) si no hay forma segura.
        /// </summary>
        public static UninstallPlan PlanUninstall(InstalledProgram p, bool interactive, out string why)
        {
            why = null;
            if (p == null) { why = "no se encontró el programa en el registro"; return null; }
            var tech = Technology(p);
            if (tech == InstallTech.Msi)
            {
                string code = MsiProductCode(p);
                if (code != null)
                {
                    var m = new UninstallPlan();
                    m.UsesMsiexec = true;
                    m.Exe = "msiexec.exe";
                    m.Args = "/x " + code + (interactive ? "" : " /qn") + " /norestart";
                    m.Description = "Windows Installer " + code;
                    return m;
                }
            }
            string exe, args;
            if (!interactive && !string.IsNullOrEmpty(p.QuietUninstallCommand) && SplitCommand(p.QuietUninstallCommand, out exe, out args))
                return Plan(exe, tech == InstallTech.Inno ? InnoSilent(args) : args, "desinstalación silenciosa que registra el programa");
            if (!SplitCommand(Uninstaller(p), out exe, out args))
            {
                why = "el programa no registra un desinstalador reconocible";
                return null;
            }
            if (interactive) return Plan(exe, args, "desinstalador del programa (con sus ventanas)");
            if (tech == InstallTech.Inno) return Plan(exe, InnoSilent(args), "Inno Setup");
            if (tech == InstallTech.Nsis) return Plan(exe, HasSwitch(args, "/S") ? args : (args + " /S").Trim(), "NSIS");
            why = "el programa no registra cómo desinstalarse en silencio (marcá «Mostrar la ventana de cada instalador» para hacerlo con su ventana)";
            return null;
        }

        static UninstallPlan Plan(string exe, string args, string description)
        {
            var u = new UninstallPlan();
            u.Exe = exe;
            u.Args = (args ?? "").Trim();
            u.Description = description;
            return u;
        }

        static bool HasSwitch(string args, string sw)
        {
            foreach (string part in (args ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (part.Equals(sw, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Argumentos de Inno Setup sin ventanas ni preguntas (sin repetir los que ya vengan).</summary>
        public static string InnoSilent(string args)
        {
            var kept = new List<string>();
            foreach (string part in (args ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string u = part.ToUpperInvariant();
                if (u == "/SILENT" || u == "/VERYSILENT" || u == "/SUPPRESSMSGBOXES" || u == "/NORESTART") continue;
                kept.Add(part);
            }
            kept.Add("/VERYSILENT");
            kept.Add("/SUPPRESSMSGBOXES");
            kept.Add("/NORESTART");
            return string.Join(" ", kept.ToArray());
        }

        // ================================================================ Arquitectura

        /// <summary>«"C:\X\app.exe",0» → «C:\X\app.exe».</summary>
        public static string CleanIconPath(string icon)
        {
            string s = (icon ?? "").Trim();
            int comma = s.LastIndexOf(',');
            if (comma > 0 && Regex.IsMatch(s.Substring(comma + 1).Trim(), @"^-?\d+$")) s = s.Substring(0, comma);
            return s.Trim().Trim('"').Trim();
        }

        static bool Under(string path, string folderName)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.TrimEnd('\\') + "\\";
            return p.IndexOf(":\\" + folderName + "\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Arquitectura del programa instalado: la del ejecutable principal (DisplayIcon) según su
        /// encabezado PE; si no, la carpeta («Program Files (x86)» es de 32 bits; «Program Files» en un
        /// Windows x64, de 64). "" si no se puede saber. La vista del registro NO sirve: Chrome y muchos
        /// instaladores de 32 bits registran programas de 64 bits en WOW6432Node.
        /// </summary>
        public static string DetectArchitecture(InstalledProgram p, string nativeArch, Func<string, string> peArch)
        {
            if (p == null) return "";
            string native = BrowserCatalog.NormalizeArch(nativeArch);
            if (native.Length == 0) native = "x64";
            if (native == "x86") return "x86";
            string icon = CleanIconPath(p.DisplayIcon);
            // Los paquetes WiX Burn (Visual C++, .NET) usan de ícono el instalador guardado en
            // ProgramData\Package Cache, que es de 32 bits sea cual sea el producto: no dice nada.
            bool bootstrapper = icon.IndexOf(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) >= 0 || BundleExe.IsMatch(FileNameOf(icon));
            if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsAbsoluteWindowsPath(icon) && !HelperExe.IsMatch(FileNameOf(icon)) && !bootstrapper && peArch != null)
            {
                string a = "";
                try { a = BrowserCatalog.NormalizeArch(peArch(icon)); } catch (Exception qx) { Log.Quiet("Arquitectura del programa (ícono)", qx); }
                if (a.Length > 0) return a;
            }
            string uninstaller = ExeOf(Uninstaller(p));
            foreach (string path in new[] { p.InstallLocation, icon, uninstaller })
                if (Under(path, "Program Files (x86)")) return "x86";
            if (native == "x64")
                foreach (string path in new[] { p.InstallLocation, icon, uninstaller })
                    if (Under(path, "Program Files")) return "x64";
            return "";
        }

        /// <summary>
        /// Valor para «winget --architecture»: la arquitectura instalada, si se conoce y NO es la que
        /// winget elegiría sola (la nativa). null si no hace falta.
        /// </summary>
        public static string WingetArchitecture(string installedArch, string nativeArch)
        {
            string a = BrowserCatalog.NormalizeArch(installedArch);
            string n = BrowserCatalog.NormalizeArch(nativeArch);
            if (n.Length == 0) n = "x64";
            if (a.Length == 0 || a == n || n == "x86") return null;
            if (n == "x64" && a == "arm64") return null;       // no se ejecuta en x64: dato raro, no se fuerza
            return a;
        }

        /// <summary>
        /// ¿Se puede pasar a OTRA arquitectura (la que elija winget) sin dejar dos copias? Sólo si lo
        /// instalado es MSI: el paquete nuevo lo reemplaza por su código de actualización, sea de 32 o
        /// de 64 bits (Epic Games Launcher). Un instalador EXE de 64 bits sobre uno de 32 instala otra
        /// copia en otra carpeta o aborta (TeamViewer, código 2); y con winget eligiendo «la última que
        /// tenga instalador de 32 bits» llegó a instalar WinRAR 7.01 en vez de la 7.23 ofrecida.
        /// </summary>
        public static bool CanSwitchArchitecture(InstalledProgram p)
        {
            return p != null && Technology(p) == InstallTech.Msi;
        }

        static string Bits(string arch)
        {
            switch (BrowserCatalog.NormalizeArch(arch))
            {
                case "x86": return "32 bits";
                case "x64": return "64 bits";
                case "arm64": return "ARM64";
                default: return arch ?? "";
            }
        }

        /// <summary>Por qué no se actualiza cuando la versión nueva no existe para la arquitectura instalada.</summary>
        public static string ArchitectureBlockedText(string version, string installedArch, string nativeArch, string id)
        {
            string inst = Bits(installedArch), nat = Bits(nativeArch.Length > 0 ? nativeArch : "x64");
            return "winget no publica la " + (string.IsNullOrEmpty(version) ? "versión nueva" : version) + " para " + inst + ", que es lo que está instalado. " +
                   "Instalar la de " + nat + " encima dejaría dos copias del programa (este instalador no reemplaza la de otra arquitectura), así que no se hizo nada. " +
                   "Para pasar a " + nat + ": desinstalá la versión de " + inst + " desde Configuración › Aplicaciones e instalala con «winget install --id " + id + "»";
        }

        // ================================================================ Versión instalada que winget no conoce

        /// <summary>
        /// El ejecutable principal (DisplayIcon) si es un .exe con ruta absoluta que no es un
        /// desinstalador ni un auxiliar; "" si no.
        /// </summary>
        public static string MainExe(InstalledProgram p)
        {
            if (p == null) return "";
            string icon = CleanIconPath(p.DisplayIcon);
            if (!icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !IsAbsoluteWindowsPath(icon) || HelperExe.IsMatch(FileNameOf(icon))) return "";
            return icon;
        }

        /// <summary>Qué se concluye de una fila que winget lista con versión instalada «Desconocida».</summary>
        public enum UnknownVerdict { SinDatos, Desactualizada, AlDia, MasNueva }

        /// <summary>
        /// winget dice «versión desconocida» (y con --include-unknown ofrece CUALQUIER versión como
        /// actualización, aunque sea más vieja). Se usa la versión del registro y, si falta, la del
        /// ejecutable principal; la del ejecutable sólo si tiene la misma versión mayor que la ofrecida
        /// (los ejecutables a veces numeran distinto que el producto).
        /// </summary>
        public static UnknownVerdict ResolveUnknownVersion(string available, string registryVersion, string exeVersion, out string installed, out string source)
        {
            installed = ""; source = "";
            // Recortada con «…» no es la versión ofrecida: compararla podía esconder una actualización.
            if (WingetTable.IsTruncated(available)) return UnknownVerdict.SinDatos;
            long[] avail = VersionUtil.Parse(available);
            if (avail == null) return UnknownVerdict.SinDatos;
            if (VersionUtil.Parse(registryVersion) != null) { installed = registryVersion.Trim(); source = "el registro de Windows"; }
            else
            {
                long[] exe = VersionUtil.Parse(exeVersion);
                if (exe == null || exe[0] != avail[0]) return UnknownVerdict.SinDatos;
                installed = VersionUtil.Normalize(exeVersion.Trim());
                source = "el ejecutable del programa";
            }
            int cmp = VersionUtil.Compare(available, installed) ?? 0;
            if (cmp > 0) return UnknownVerdict.Desactualizada;
            return cmp == 0 ? UnknownVerdict.AlDia : UnknownVerdict.MasNueva;
        }

        // ================================================================ Versión real (archivos) frente al registro

        static readonly Regex SimpleName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9 ._\-]{0,60}$", RegexOptions.Compiled);

        /// <summary>
        /// Versión que tienen de verdad los archivos del programa, que puede no ser la que figura en el
        /// registro (y que winget compara). Pasa con los programas que se actualizan solos:
        ///  - TeraBox se registra para todo el equipo (HKLM) pero vive en la carpeta del usuario y su
        ///    actualizador corre sin privilegios: actualiza los archivos y NO puede tocar la entrada de
        ///    «Aplicaciones», que queda con la versión vieja para siempre (winget la ofrece una y otra vez);
        ///  - Mullvad Browser (y Tor Browser) guardan su versión en «version.json»; su número de
        ///    ejecutable es el de Firefox ESR (140.x), que no sirve para comparar.
        /// Se lee primero «version.json» de la carpeta del programa y si no, el ejecutable principal (o
        /// «&lt;Nombre&gt;.exe» de su carpeta). Sólo se acepta una versión con la MISMA versión mayor que
        /// <paramref name="reference"/> (la del registro o la ofrecida): un ejecutable que numera distinto que
        /// el producto no dice nada. "" si no se puede saber.
        /// </summary>
        public static string ActualVersion(InstalledProgram p, string reference, Func<string, string> exeVersion, Func<string, string> readSmallText, out string source)
        {
            source = "";
            if (p == null) return "";
            long[] refParts = VersionUtil.Parse(reference);
            if (refParts == null) return "";
            string folder = WingetDirect.AppFolder(p).Trim().Trim('"').TrimEnd('\\');
            if (folder.Length > 3 && IsAbsoluteWindowsPath(folder) && readSmallText != null)
            {
                string json = null;
                try { json = readSmallText(folder + @"\version.json"); } catch { json = null; }
                string v = VersionFromJson(json);
                if (SameMajor(v, refParts)) { source = "su archivo «version.json»"; return VersionUtil.Normalize(v); }
            }
            // 2.0.20: programas que se actualizan con Squirrel (Electron: Claude, Discord, Slack…). Cada versión queda en una
            // carpeta «app-6.28899.2» junto a Update.exe; la más nueva es la que se usa. Su entrada en «Aplicaciones» puede quedar
            // atrás (Claude seguía «6.6395.0.0» con la 6.28899.2 abierta) y winget la ofrecía para siempre: «no cambió nada».
            if (folder.Length > 3 && IsAbsoluteWindowsPath(folder) && SquirrelVersionsReader != null)
            {
                string root = SquirrelRoot(folder);
                List<string> found = null;
                try { found = SquirrelVersionsReader(root); } catch { found = null; }
                string best = BestSquirrelVersion(found);
                if (SameMajor(best, refParts)) { source = "su carpeta de versión («app-" + best + "»)"; return VersionUtil.Normalize(best); }
            }
            if (exeVersion == null) return "";
            var exes = new List<string>();
            string main = MainExe(p);
            if (main.Length > 0) exes.Add(main);
            string name = (p.Name ?? "").Trim();
            if (folder.Length > 3 && IsAbsoluteWindowsPath(folder) && SimpleName.IsMatch(name))
            {
                string guess = folder + @"\" + name + ".exe";
                if (!exes.Exists(delegate(string e) { return e.Equals(guess, StringComparison.OrdinalIgnoreCase); })) exes.Add(guess);
            }
            foreach (string exe in exes)
            {
                string v = "";
                try { v = exeVersion(exe) ?? ""; } catch { v = ""; }
                if (SameMajor(v, refParts)) { source = "su ejecutable (" + FileNameOf(exe) + ")"; return VersionUtil.Normalize(v); }
            }
            return "";
        }

        /// <summary>
        /// Nombres de las carpetas «app-X.Y.Z» de una instalación de Squirrel (sólo si junto a ellas está Update.exe), sin el «app-».
        /// Reemplazable en las pruebas.
        /// </summary>
        internal static Func<string, List<string>> SquirrelVersionsReader = ReadSquirrelVersions;

        static List<string> ReadSquirrelVersions(string root)
        {
            var list = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(root) || !System.IO.File.Exists(root + @"\Update.exe")) return list;
                foreach (string d in System.IO.Directory.GetDirectories(root, "app-*"))
                {
                    string n = System.IO.Path.GetFileName(d);
                    if (n.Length > 4) list.Add(n.Substring(4));
                }
            }
            catch { }
            return list;
        }

        /// <summary>La carpeta raíz de Squirrel: si la del programa es «…\app-1.2.3», la de arriba. Pública para las pruebas.</summary>
        public static string SquirrelRoot(string folder)
        {
            string f = (folder ?? "").TrimEnd('\\');
            int i = f.LastIndexOf('\\');
            if (i > 2 && f.Substring(i + 1).StartsWith("app-", StringComparison.OrdinalIgnoreCase)) return f.Substring(0, i);
            return f;
        }

        /// <summary>La versión más nueva de una lista de carpetas de Squirrel («6.28899.2», «6.6395.0»), o "". Pública para las pruebas.</summary>
        public static string BestSquirrelVersion(List<string> versions)
        {
            string best = "";
            if (versions == null) return best;
            foreach (string v in versions)
            {
                string t = (v ?? "").Trim();
                if (VersionUtil.Parse(t) == null || t.IndexOfAny(new[] { ' ', '\\', '/' }) >= 0) continue;
                if (best.Length == 0 || VersionUtil.IsNewer(t, best)) best = t;
            }
            return best;
        }

        static bool SameMajor(string v, long[] reference)
        {
            long[] x = VersionUtil.Parse(v);
            return x != null && x.Length >= 2 && reference != null && x[0] == reference[0];
        }

        static readonly Regex JsonVersion = new Regex(@"""version""\s*:\s*""(?<v>[0-9][0-9A-Za-z.\-]{0,40})""", RegexOptions.Compiled);

        /// <summary>«{"version":"15.0.24",…}» → «15.0.24» (o null).</summary>
        public static string VersionFromJson(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 4096) return null;
            var m = JsonVersion.Match(json);
            return m.Success && VersionUtil.Parse(m.Groups["v"].Value) != null ? m.Groups["v"].Value : null;
        }

        /// <summary>La versión recortada a <paramref name="parts"/> componentes («1.49.5.183» con 3 → «1.49.5»).</summary>
        public static string Truncate(string version, int parts)
        {
            long[] v = VersionUtil.Parse(version);
            if (v == null || parts <= 0) return version ?? "";
            int n = Math.Min(parts, v.Length);
            var s = new string[n];
            for (int i = 0; i < n; i++) s[i] = v[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.Join(".", s);
        }

        /// <summary>
        /// ¿Los archivos del programa ya están en la versión ofrecida aunque el registro diga una anterior?
        /// Se compara con la misma cantidad de componentes que la ofrecida (el ejecutable de TeraBox dice
        /// 1.49.5.183 y winget publica 1.49.5) y tiene que ser MÁS NUEVA que la del registro: es la señal de
        /// que el programa se actualizó solo y su entrada en «Aplicaciones» quedó atrás.
        /// </summary>
        public static bool FilesAlreadyAt(string actual, string registry, string offered, bool strong)
        {
            long[] off = VersionUtil.Parse(offered);
            if (off == null || VersionUtil.Parse(actual) == null || VersionUtil.Parse(registry) == null) return false;
            string a = Truncate(actual, Math.Max(off.Length, 1));
            if (VersionUtil.IsNewer(offered, a) || !VersionUtil.IsNewer(actual, registry)) return false;
            // Un ejecutable puede numerar distinto que el producto («8.69» frente a «8.6.9»): sólo cuenta si
            // coincide exactamente con la ofrecida. «version.json» es la versión del producto: alcanza con no ser menor.
            return strong || VersionUtil.Compare(a, offered) == 0;
        }

        /// <summary>¿La versión vino de «version.json» (la del producto) y no de un ejecutable?</summary>
        public static bool IsProductVersionSource(string source)
        {
            return !string.IsNullOrEmpty(source) && (source.IndexOf("version.json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                    source.IndexOf("carpeta de versión", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// ¿Sirve la versión de los archivos para informarla junto a la ofrecida? La de un ejecutable, sólo si no
        /// pasa de la ofrecida (si la pasa, numera distinto que el producto y confundiría).
        /// </summary>
        public static bool PlausibleFilesVersion(string actual, string offered, bool strong)
        {
            long[] off = VersionUtil.Parse(offered);
            if (off == null || VersionUtil.Parse(actual) == null) return false;
            return strong || !VersionUtil.IsNewer(Truncate(actual, off.Length), offered);
        }

        // ================================================================ Procesos abiertos

        /// <summary>¿El ejecutable está dentro de la carpeta (o de una subcarpeta)?</summary>
        public static bool IsInFolder(string exePath, string folder)
        {
            if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(folder)) return false;
            string f = folder.Trim().Trim('"').TrimEnd('\\') + "\\";
            if (f.Length < 4) return false;                    // «C:\»: sería todo el disco
            return exePath.StartsWith(f, StringComparison.OrdinalIgnoreCase);
        }
    }
}
