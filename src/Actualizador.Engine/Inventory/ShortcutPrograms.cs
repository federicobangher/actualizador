// Programas sin entrada de desinstalación, encontrados por sus accesos directos.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Microsoft.Win32;

namespace Actualizador.Inventory
{
    /// <summary>Lo que dice el recurso de versión de un ejecutable.</summary>
    public sealed class ExeInfo
    {
        public string Product = "", Description = "", Company = "", Version = "";
    }

    /// <summary>
    /// Programas que Windows no lista en «Aplicaciones instaladas»: portables (VLC o GOM en una carpeta copiada), los que
    /// perdieron su entrada de desinstalación, y los instalados para OTRA cuenta del equipo que no tiene la sesión abierta
    /// (su registro no está cargado, pero sus accesos directos sí se pueden leer). Antes no aparecían en ningún lado
    /// (grabación del usuario: «no reconoce GOM Player ni VLC»). Se buscan los accesos directos (.lnk) del menú Inicio y
    /// del escritorio de todos los perfiles y de «App Paths», y se agrega cada ejecutable que no pertenezca a un programa
    /// ya registrado. Sólo se lee: nunca se ejecuta nada.
    /// </summary>
    public static class ShortcutPrograms
    {
        public const string ScopeText = "Sin desinstalador (acceso directo)";

        /// <summary>Sólo para las pruebas: lee la versión de un .exe sin abrirlo.</summary>
        internal static Func<string, ExeInfo> ExeInfoForTests;

        const int MaxShortcuts = 3000;

        // ================================================================== Lectura de .lnk (MS-SHLLINK)

        const uint HasLinkTargetIdList = 0x1, HasLinkInfo = 0x2, HasName = 0x4, HasRelativePath = 0x8, HasWorkingDir = 0x10,
                   HasArguments = 0x20, HasIconLocation = 0x40, IsUnicode = 0x80, HasExpString = 0x200, HasDarwinId = 0x1000;

        /// <summary>
        /// Destino de un acceso directo: la ruta local de LinkInfo o, si no hay, la de las variables de entorno o la relativa
        /// a la carpeta del acceso directo. null si no es un .lnk válido, si es de Windows Installer (anunciado: esos programas
        /// sí tienen entrada de desinstalación) o si no apunta a una ruta local.
        /// </summary>
        public static string ParseTarget(byte[] data, string lnkFolder)
        {
            try { return ParseTargetCore(data, lnkFolder); }
            catch (Exception ex) { Log.Quiet("Acceso directo ilegible", ex); return null; }
        }

        static string ParseTargetCore(byte[] d, string lnkFolder)
        {
            if (d == null || d.Length < 0x4C || U32(d, 0) != 0x4C) return null;
            // LinkCLSID {00021401-0000-0000-C000-000000000046}
            if (d[4] != 0x01 || d[5] != 0x14 || d[6] != 0x02 || d[7] != 0x00 || d[0x0C] != 0xC0 || d[0x13] != 0x46) return null;
            uint flags = U32(d, 0x14);
            if ((flags & HasDarwinId) != 0) return null;
            int pos = 0x4C;
            if ((flags & HasLinkTargetIdList) != 0)
            {
                if (pos + 2 > d.Length) return null;
                pos += 2 + U16(d, pos);
            }
            string local = null;
            if ((flags & HasLinkInfo) != 0)
            {
                if (pos + 0x1C > d.Length) return null;
                int start = pos;
                int size = (int)U32(d, pos);
                int headerSize = (int)U32(d, pos + 4);
                uint infoFlags = U32(d, pos + 8);
                if ((infoFlags & 1) != 0)
                {
                    string basePath = null, suffix = null;
                    if (headerSize >= 0x24 && pos + 0x24 <= d.Length)
                    {
                        int uBase = (int)U32(d, pos + 0x1C), uSuffix = (int)U32(d, pos + 0x20);
                        if (uBase > 0) basePath = Utf16Z(d, start + uBase);
                        if (uSuffix > 0) suffix = Utf16Z(d, start + uSuffix);
                    }
                    if (string.IsNullOrEmpty(basePath)) basePath = AnsiZ(d, start + (int)U32(d, pos + 0x10));
                    if (suffix == null) suffix = AnsiZ(d, start + (int)U32(d, pos + 0x18));
                    if (!string.IsNullOrEmpty(basePath))
                        local = basePath + (string.IsNullOrEmpty(suffix) ? "" : (basePath.EndsWith("\\") ? "" : "\\") + suffix);
                }
                if (size <= 0) return null;
                pos = start + size;
            }
            bool unicode = (flags & IsUnicode) != 0;
            string relative = null;
            foreach (uint f in new[] { HasName, HasRelativePath, HasWorkingDir, HasArguments, HasIconLocation })
            {
                if ((flags & f) == 0) continue;
                if (pos + 2 > d.Length) return local;
                int chars = U16(d, pos);
                int bytes = unicode ? chars * 2 : chars;
                if (pos + 2 + bytes > d.Length) return local;
                string text = unicode ? Encoding.Unicode.GetString(d, pos + 2, bytes) : TextEncodings.Ansi.GetString(d, pos + 2, bytes);
                if (f == HasRelativePath) relative = text;
                pos += 2 + bytes;
            }
            if (!string.IsNullOrEmpty(local)) return local;
            // Bloques extra: EnvironmentVariableDataBlock (0xA0000001), con el destino como «%ProgramFiles%\…».
            if ((flags & HasExpString) != 0)
            {
                while (pos + 8 <= d.Length)
                {
                    int blockSize = (int)U32(d, pos);
                    if (blockSize < 8 || pos + blockSize > d.Length) break;
                    uint sig = U32(d, pos + 4);
                    if (sig == 0xA0000001 && blockSize >= 0x314)
                    {
                        string target = Utf16Z(d, pos + 8 + 260, 520);
                        if (string.IsNullOrEmpty(target)) target = AnsiZ(d, pos + 8, 260);
                        if (!string.IsNullOrEmpty(target)) return Environment.ExpandEnvironmentVariables(target);
                    }
                    pos += blockSize;
                }
            }
            if (!string.IsNullOrEmpty(relative) && !string.IsNullOrEmpty(lnkFolder))
            {
                try { return Path.GetFullPath(Path.Combine(lnkFolder, relative)); }
                catch { return null; }
            }
            return null;
        }

        /// <summary>Carpeta de una ruta de Windows (sin depender del sistema donde corre: las pruebas corren también en Linux).</summary>
        public static string WinDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int i = path.LastIndexOfAny(new[] { '\\', '/' });
            return i <= 0 ? null : path.Substring(0, i == 2 && path[1] == ':' ? 3 : i);
        }

        /// <summary>Nombre de archivo de una ruta de Windows.</summary>
        public static string WinFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int i = path.LastIndexOfAny(new[] { '\\', '/' });
            return i < 0 ? path : path.Substring(i + 1);
        }

        static uint U32(byte[] d, int i) { return i < 0 || i + 4 > d.Length ? 0 : (uint)(d[i] | d[i + 1] << 8 | d[i + 2] << 16 | d[i + 3] << 24); }
        static int U16(byte[] d, int i) { return i < 0 || i + 2 > d.Length ? 0 : d[i] | d[i + 1] << 8; }

        static string AnsiZ(byte[] d, int start, int max = 1024)
        {
            if (start <= 0 || start >= d.Length) return null;
            int end = start;
            while (end < d.Length && end - start < max && d[end] != 0) end++;
            return TextEncodings.Ansi.GetString(d, start, end - start);
        }

        static string Utf16Z(byte[] d, int start, int maxBytes = 2048)
        {
            if (start <= 0 || start + 1 >= d.Length) return null;
            int end = start;
            while (end + 1 < d.Length && end - start < maxBytes && (d[end] != 0 || d[end + 1] != 0)) end += 2;
            return Encoding.Unicode.GetString(d, start, end - start);
        }

        // ================================================================== Qué se agrega

        static readonly Regex NotAProgram = new Regex(
            @"^(?:unins\d*|uninst(?:all)?\w*|.*[-_ ]uninst(?:all)?\w*|setup\w*|install(?:er)?|update(?:r)?\w*|.*updater|crashreporter|.*[-_]crash\w*|repair|helper|maintenancetool)\.exe$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Normalizado para comparar nombres: sólo letras y dígitos, en minúsculas.</summary>
        public static string Key(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>
        /// ¿El nombre corresponde a un programa ya registrado? «VLC media player» con «VLC media player 3.0.21»; «GOM Player»
        /// con «GOM Player». Se compara el comienzo, con al menos 4 letras.
        /// </summary>
        public static bool SameProgram(string a, string b)
        {
            string x = Key(a), y = Key(b);
            if (x.Length < 4 || y.Length < 4) return x.Length > 0 && x == y;
            return x.StartsWith(y) || y.StartsWith(x);
        }

        /// <summary>Carpeta de un ejecutable que aparece en un valor del registro («"C:\…\x.exe",0», «C:\…\unins000.exe /S»).</summary>
        public static string FolderOfCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            string c = command.Trim();
            string path;
            if (c.StartsWith("\""))
            {
                int end = c.IndexOf('"', 1);
                if (end <= 1) return null;
                path = c.Substring(1, end - 1);
            }
            else
            {
                int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                path = exe > 0 ? c.Substring(0, exe + 4) : c.Split(',')[0];
            }
            if (path.IndexOf(':') != 1) return null;
            return WinDir(path);
        }

        /// <summary>
        /// Carpetas que pertenecen a programas registrados (su ubicación, la de su ícono y la de su desinstalador), sin las
        /// carpetas generales (Program Files, ProgramData, Windows, la raíz de una unidad o de un perfil).
        /// </summary>
        public static List<string> CoveredFolders(IEnumerable<InstalledProgram> registered, ICollection<string> generic)
        {
            var list = new List<string>();
            foreach (var p in registered)
            {
                if (p == null) continue;
                foreach (string dir in new[] { (p.InstallLocation ?? "").Trim().Trim('"'), FolderOfCommand(p.DisplayIcon), FolderOfCommand(p.UninstallCommand), FolderOfCommand(p.UninstallString) })
                {
                    string n = NormalizeDir(dir);
                    if (n == null || n.Length <= 3 || generic.Contains(n) || n.Split('\\').Length < 3) continue;
                    if (!list.Contains(n)) list.Add(n);
                }
            }
            return list;
        }

        static string NormalizeDir(string dir)
        {
            if (string.IsNullOrEmpty(dir) || dir.IndexOf(':') != 1) return null;
            string d = dir.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
            return d + "\\";
        }

        /// <summary>¿El ejecutable está dentro de la carpeta de un programa registrado?</summary>
        public static bool IsCovered(string exe, List<string> covered)
        {
            string n = NormalizeDir(WinDir(exe) ?? "");
            if (n == null) return false;
            foreach (string c in covered) if (n.StartsWith(c, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// El programa que representa el ejecutable, o null si no corresponde agregarlo: no existe, es de Windows, es un
        /// desinstalador o instalador, está en la carpeta de un programa registrado o tiene el mismo nombre que uno.
        /// </summary>
        public static InstalledProgram Candidate(string exe, List<string> covered, IEnumerable<InstalledProgram> registered, string windowsDir, Func<string, ExeInfo> read)
        {
            if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || exe.IndexOf(':') != 1) return null;
            string file = WinFile(exe);
            if (NotAProgram.IsMatch(file)) return null;
            string win = NormalizeDir(windowsDir);
            string dir = NormalizeDir(WinDir(exe) ?? "");
            if (dir == null || (win != null && dir.StartsWith(win, StringComparison.Ordinal))) return null;
            if (IsCovered(exe, covered)) return null;
            var info = read(exe);
            if (info == null) return null;
            // Componente de un programa instalado del mismo fabricante (ACDSync junto a ACDSee, en «ACD Systems\»): no es
            // otro programa sin desinstalador (figuraba «portable o copiado a mano»).
            if (IsComponentOf(exe, info.Company, registered, windowsDir)) return null;
            string name = FirstNonEmpty(info.Product, info.Description, file.Substring(0, file.Length - 4)).Trim();
            if (name.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0 && (info.Company ?? "").IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0
                && name.IndexOf("Operating System", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            foreach (var p in registered) if (p != null && (SameProgram(p.Name, name) || (info.Description.Length > 0 && SameProgram(p.Name, info.Description)))) return null;
            var prog = new InstalledProgram();
            prog.Name = name;
            prog.Version = CleanVersion(info.Version);
            prog.Publisher = (info.Company ?? "").Trim();
            prog.InstallLocation = WinDir(exe);
            prog.DisplayIcon = exe;
            prog.UninstallString = prog.UninstallCommand = prog.QuietUninstallCommand = "";
            prog.Scope = ScopeText;
            prog.Architecture = "";
            prog.RegistryKey = "";
            prog.InfoUrl = prog.UpdateUrl = prog.HelpLink = "";
            prog.Unregistered = true;
            return prog;
        }

        /// <summary>Forma comparable del nombre de una empresa: sin forma jurídica ni signos.</summary>
        static string CompanyKey(string company)
        {
            string c = Key(Regex.Replace(company ?? "", @"\b(?:inc|ltd|llc|gmbh|corp|corporation|co|company|limited|s\.?a|s\.?r\.?l|ag|b\.?v|plc|pty|software|systems)\b\.?", " ", RegexOptions.IgnoreCase));
            return c;
        }

        /// <summary>
        /// ¿El ejecutable está en la carpeta del fabricante de un programa registrado de la misma empresa? Se mira la carpeta
        /// que contiene la del programa («C:\Program Files (x86)\ACD Systems\» para «…\ACD Systems\ACDSee\9.0\»), si no es
        /// una carpeta general (Program Files, la raíz de una unidad).
        /// </summary>
        public static bool IsComponentOf(string exe, string company, IEnumerable<InstalledProgram> registered, string windowsDir)
        {
            string ck = CompanyKey(company);
            if (ck.Length < 3 || registered == null) return false;
            string dir = NormalizeDir(WinDir(exe) ?? "");
            if (dir == null) return false;
            foreach (var p in registered)
            {
                if (p == null || p.Unregistered || CompanyKey(p.Publisher) != ck) continue;
                foreach (string d in new[] { (p.InstallLocation ?? "").Trim().Trim('"'), FolderOfCommand(p.DisplayIcon), FolderOfCommand(p.UninstallString) })
                {
                    string n = NormalizeDir(d);
                    // Hasta tres carpetas hacia arriba («…\ACD Systems\ACDSee\9.0\» → «…\ACD Systems\»), sin llegar a una general.
                    for (int up = 0; up < 3 && n != null; up++)
                    {
                        string parent = NormalizeDir(WinDir(n.TrimEnd('\\')) ?? "");
                        if (parent == null || parent.Split('\\').Length < 4) break;   // «c:\program files\» (3 partes con la final vacía) no
                        string pf = parent.ToLowerInvariant();
                        if (pf.EndsWith("\\program files\\") || pf.EndsWith("\\program files (x86)\\") || pf.EndsWith("\\programdata\\") ||
                            pf.EndsWith("\\appdata\\local\\programs\\") || pf.EndsWith("\\appdata\\local\\") || pf.EndsWith("\\appdata\\roaming\\")) break;
                        if (dir.StartsWith(parent, StringComparison.Ordinal)) return true;
                        n = parent;
                    }
                }
            }
            return false;
        }

        static readonly Regex LeadingVersion = new Regex(@"\d+(?:[.,]\d+){0,3}", RegexOptions.Compiled);

        /// <summary>«3, 0, 21, 0» → «3.0.21.0»; «2.3.98.5371 (x64)» → «2.3.98.5371».</summary>
        public static string CleanVersion(string v)
        {
            var m = LeadingVersion.Match((v ?? "").Replace(", ", ","));
            return m.Success ? m.Value.Replace(',', '.') : "";
        }

        static string FirstNonEmpty(params string[] values)
        {
            foreach (string s in values) if (!string.IsNullOrEmpty(s) && s.Trim().Length > 0) return s;
            return "";
        }

        // ================================================================== Búsqueda en el equipo

        /// <summary>Los programas sin entrada de desinstalación (vacío fuera de Windows o si algo falla).</summary>
        public static List<InstalledProgram> Find(List<InstalledProgram> registered)
        {
            var found = new List<InstalledProgram>();
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT && ExeInfoForTests == null) return found;
                var generic = GenericFolders();
                var covered = CoveredFolders(registered, generic);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Func<string, ExeInfo> read = ExeInfoForTests ?? ReadExeInfo;
                string windows = SafePaths.Windows;
                var profiles = Profiles();
                string current = null;
                try { current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }
                foreach (string exe in Targets())
                {
                    if (!seen.Add(exe)) continue;
                    bool exists;
                    try { exists = File.Exists(exe); } catch { exists = false; }
                    if (!exists) continue;
                    var all = new List<InstalledProgram>(registered);
                    all.AddRange(found);
                    var p = Candidate(exe, covered, all, windows, read);
                    if (p == null) continue;
                    // En la carpeta de otra cuenta: lo instaló ese usuario y su registro no está cargado (no tiene la sesión abierta).
                    string owner = ProfileOwner(exe, profiles, current);
                    if (owner != null) p.Scope = "Usuario " + owner + " (sin sesión abierta)";
                    found.Add(p);
                    Log.Info("Programa sin desinstalador visible: " + p.Name + " " + p.Version + " (" + exe + ")");
                }
            }
            catch (Exception ex) { Log.Warn("Accesos directos: " + ex.Message); }
            return found;
        }

        /// <summary>Nombre de la cuenta cuyo perfil contiene el ejecutable, si no es la actual; null si no.</summary>
        public static string ProfileOwner(string exe, List<string> profiles, string currentProfile)
        {
            string e = NormalizeDir(WinDir(exe) ?? "");
            string cur = NormalizeDir(currentProfile);
            if (e == null) return null;
            foreach (string profile in profiles)
            {
                string n = NormalizeDir(profile);
                if (n == null || !e.StartsWith(n, StringComparison.Ordinal)) continue;
                return n == cur ? null : WinFile(profile.TrimEnd('\\'));
            }
            return null;
        }

        static ExeInfo ReadExeInfo(string exe)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exe);
                var i = new ExeInfo();
                i.Product = (v.ProductName ?? "").Trim();
                i.Description = (v.FileDescription ?? "").Trim();
                i.Company = (v.CompanyName ?? "").Trim();
                i.Version = (v.ProductVersion ?? "").Trim();
                if (CleanVersion(i.Version).Length == 0) i.Version = (v.FileVersion ?? "").Trim();
                return i;
            }
            catch (Exception ex) { Log.Quiet("Versión de " + exe, ex); return null; }
        }

        static HashSet<string> GenericFolders()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string d in new[] { SafePaths.ProgramFiles, SafePaths.ProgramFiles + " (x86)", SafePaths.ProgramData, SafePaths.Windows, SafePaths.SystemDrive + "\\",
                                         SafePaths.ProgramData == null ? null : Path.Combine(SafePaths.ProgramData, "Package Cache") })
            {
                string n = NormalizeDir(d);
                if (n != null) set.Add(n);
            }
            foreach (string profile in Profiles())
                foreach (string sub in new[] { "", "AppData", @"AppData\Local", @"AppData\Roaming", @"AppData\Local\Programs", @"AppData\Local\Temp", "Desktop", "Downloads", "Documents" })
                {
                    string n = NormalizeDir(sub.Length == 0 ? profile : Path.Combine(profile, sub));
                    if (n != null) set.Add(n);
                }
            return set;
        }

        /// <summary>Carpetas de perfil de las cuentas del equipo (ProfileList, que sólo escribe un administrador).</summary>
        internal static List<string> Profiles()
        {
            var list = new List<string>();
            using (var k = Reg.Open(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", Reg.Native))
                foreach (string sid in Reg.SubKeys(k))
                {
                    if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue;
                    using (var p = Reg.Open(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid, Reg.Native))
                    {
                        string path = Reg.Str(p, "ProfileImagePath");
                        if (string.IsNullOrEmpty(path)) continue;
                        path = Environment.ExpandEnvironmentVariables(path);
                        try { if (Directory.Exists(path) && !list.Contains(path)) list.Add(path); } catch { }
                    }
                }
            return list;
        }

        /// <summary>Destinos .exe de los accesos directos del menú Inicio y del escritorio (de todos) y de App Paths.</summary>
        static IEnumerable<string> Targets()
        {
            var roots = new List<string>();
            if (SafePaths.ProgramData != null) roots.Add(Path.Combine(SafePaths.ProgramData, @"Microsoft\Windows\Start Menu\Programs"));
            try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)); } catch { }
            foreach (string profile in Profiles())
            {
                roots.Add(Path.Combine(profile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"));
                roots.Add(Path.Combine(profile, "Desktop"));
            }
            int count = 0;
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (string lnk in Shortcuts(root, 0))
                {
                    if (++count > MaxShortcuts) yield break;
                    byte[] data = null;
                    try
                    {
                        var fi = new FileInfo(lnk);
                        if (fi.Length > 0 && fi.Length < 64 * 1024) data = File.ReadAllBytes(lnk);
                    }
                    catch { }
                    string target = data == null ? null : ParseTarget(data, Path.GetDirectoryName(lnk));
                    if (!string.IsNullOrEmpty(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) yield return target;
                }
            }
            // App Paths: «vlc.exe» → ruta; los programas que se registran ahí sin entrada de desinstalación.
            foreach (RegistryView view in Reg.Views())
                using (var k = Reg.Open(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", view))
                    foreach (string name in Reg.SubKeys(k))
                    {
                        string path = null;
                        try
                        {
                            using (var sk = k.OpenSubKey(name, false))
                                if (sk != null) path = (sk.GetValue("") as string ?? "").Trim().Trim('"');
                        }
                        catch { }
                        if (!string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            yield return Environment.ExpandEnvironmentVariables(path);
                    }
        }

        static IEnumerable<string> Shortcuts(string dir, int depth)
        {
            string[] files = null, dirs = null;
            try
            {
                if (!Directory.Exists(dir)) yield break;
                // Sin seguir enlaces (junctions): sólo carpetas comunes.
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0 && depth > 0) yield break;
                files = Directory.GetFiles(dir, "*.lnk");
                if (depth < 4) dirs = Directory.GetDirectories(dir);
            }
            catch { }
            if (files != null) foreach (string f in files) yield return f;
            if (dirs != null) foreach (string d in dirs) foreach (string f in Shortcuts(d, depth + 1)) yield return f;
        }
    }
}
