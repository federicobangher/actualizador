// 2.0.8: lo que quedaba «No verificable» y se puede comparar sin internet: componentes de Windows, juegos de Steam y los programas
// propios cuyas versiones nuevas se dejan en una carpeta.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Qué hace «Actualizar» con una fila de <see cref="ExternalProvider"/>.</summary>
    public sealed class ExternalAction
    {
        public enum ActionKind { SteamValidate, ShowFile }
        public ActionKind Kind;
        /// <summary>Steam: el número de la app. Carpeta propia: la ruta del archivo nuevo.</summary>
        public string Target = "";
    }

    /// <summary>Estado de un juego según su archivo appmanifest_N.acf de Steam.</summary>
    public sealed class SteamManifest
    {
        public string AppId = "", BuildId = "", TargetBuildId = "", Name = "";
        public int StateFlags;
        public DateTime? LastUpdated;

        // StateFlags de Steam: 2 falta actualizar, 4 instalado completo, 256 actualizando, 512 pausada, 1024 empezada.
        public bool UpdatePending
        {
            get
            {
                if ((StateFlags & (2 | 256 | 512 | 1024)) != 0) return true;
                return TargetBuildId.Length > 0 && TargetBuildId != "0" && BuildId.Length > 0 && TargetBuildId != BuildId;
            }
        }

        public bool FullyInstalled { get { return (StateFlags & 4) != 0; } }

        static readonly Regex Field = new Regex(@"""(?<k>[A-Za-z]+)""\s+""(?<v>[^""]*)""", RegexOptions.Compiled);

        public static SteamManifest Parse(string acf)
        {
            if (string.IsNullOrEmpty(acf)) return null;
            var m = new SteamManifest();
            bool any = false;
            foreach (Match f in Field.Matches(acf))
            {
                string k = f.Groups["k"].Value, v = f.Groups["v"].Value;
                switch (k.ToLowerInvariant())
                {
                    case "appid": if (m.AppId.Length == 0) { m.AppId = v; any = true; } break;
                    case "name": if (m.Name.Length == 0) m.Name = v; break;
                    case "stateflags": { int n; if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { m.StateFlags = n; any = true; } break; }
                    case "buildid": if (m.BuildId.Length == 0) m.BuildId = v; break;
                    case "targetbuildid": if (m.TargetBuildId.Length == 0) m.TargetBuildId = v; break;
                    case "lastupdated":
                    {
                        long t;
                        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out t) && t > 0)
                            m.LastUpdated = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t).ToLocalTime();
                        break;
                    }
                }
            }
            return any ? m : null;
        }
    }

    /// <summary>Una versión de un programa propio encontrada en la carpeta de versiones.</summary>
    public sealed class OwnBuild
    {
        public string Key = "", Version = "", Path = "";
        /// <summary>2.0.16: la clave con todas las palabras del nombre del archivo (sin sacar «portable», «win», «final»…).</summary>
        public string FullKey = "";
    }

    public static class LocalChecks
    {
        const RegexOptions RX = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        /// <summary>Componentes de Windows que figuran en «Aplicaciones»: se actualizan con el sistema (Windows Update).</summary>
        static readonly Regex WindowsPart = new Regex(
            @"^(?:Internet Explorer|Microsoft Windows Application Compatibility Fix Database|Windows Media Player|" +
            @"Sistema operativo Microsoft(?:®|\(R\))? Windows(?:®|\(R\))?|Microsoft(?:®|\(R\))? Windows(?:®|\(R\))? Operating System)$", RX);

        public static bool IsWindowsComponent(string name) { return !string.IsNullOrEmpty(name) && WindowsPart.IsMatch(name.Trim()); }

        /// <summary>Sólo para las pruebas: reemplaza la lectura de archivos (ruta → contenido o null).</summary>
        internal static Func<string, string> ReadForTests;
        /// <summary>Sólo para las pruebas: reemplaza la lista de archivos de la carpeta de versiones.</summary>
        internal static Func<string, IEnumerable<string>> ListForTests;

        /// <summary>
        /// Recorre las filas de programas «No verificable» y resuelve las que se pueden saber acá. Devuelve cuántas cambiaron.
        /// </summary>
        public static int Resolve(UpdateContext ctx, List<UpdateCandidate> all, List<InstalledProgram> programs, List<string> notes)
        {
            return Resolve(ctx, all, programs, notes, true);
        }

        /// <param name="external">false si la política apagó la fuente «externo» (Steam y la carpeta de versiones).</param>
        public static int Resolve(UpdateContext ctx, List<UpdateCandidate> all, List<InstalledProgram> programs, List<string> notes, bool external)
        {
            if (all == null) return 0;
            int n = 0;
            string folder = ctx != null && ctx.Options != null ? (ctx.Options.OwnBuildsFolder ?? "").Trim() : "";
            List<OwnBuild> builds = null;
            if (folder.Length > 0 && external)
            {
                builds = ListOwnBuilds(folder);
                if (builds == null && notes != null) notes.Add("Carpeta de versiones de tus programas: no se puede leer «" + folder + "».");
            }
            foreach (var c in all)
            {
                if (c == null || c.Kind != UpdateKind.Programa || c.Status != UpdateStatus.NoVerificable) continue;
                var p = Find(programs, c.Name);
                try
                {
                    if (IsWindowsComponent(c.Name)) { WindowsComponent(ctx, c); n++; continue; }
                    if (builds != null && p != null && OwnBuildRow(c, p, builds, folder)) { n++; continue; }
                    if (external && p != null && SteamRow(c, p)) { n++; continue; }
                }
                catch (Exception ex) { Log.Warn("Comprobación local de " + c.Name + ": " + ex.Message); }
            }
            return n;
        }

        static InstalledProgram Find(List<InstalledProgram> programs, string name)
        {
            if (programs == null || string.IsNullOrEmpty(name)) return null;
            foreach (var p in programs) if (p != null && p.Name != null && p.Name.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        // ------------------------------------------------------------------ Componentes de Windows

        static void WindowsComponent(UpdateContext ctx, UpdateCandidate c)
        {
            var os = ctx != null ? ctx.Os : null;
            string build = os != null && os.Build > 0 ? os.Build + (os.Ubr > 0 ? "." + os.Ubr : "") : "";
            c.Status = UpdateStatus.AlDia;
            c.AvailableVersion = "";
            c.Installable = false;
            c.Recommended = false;
            c.Source = "Windows Update";
            c.Detail = "Es parte de Windows: no tiene actualizaciones propias, se actualiza con el sistema" +
                       (build.Length > 0 ? " (compilación " + build + ")" : "") + ". Lo pendiente de Windows aparece en las filas de «Windows Update».";
        }

        // ------------------------------------------------------------------ Steam

        static readonly Regex SteamUninstall = new Regex(@"steam://uninstall/(?<id>\d{1,10})", RX);
        static readonly Regex SteamKey = new Regex(@"Steam App (?<id>\d{1,10})\s*$", RX);

        /// <summary>Número de la app de Steam de un programa instalado, o null.</summary>
        public static string SteamAppId(InstalledProgram p)
        {
            if (p == null) return null;
            var m = SteamUninstall.Match(p.UninstallString ?? "");
            if (m.Success) return m.Groups["id"].Value;
            m = SteamKey.Match(p.RegistryKey ?? "");
            return m.Success ? m.Groups["id"].Value : null;
        }

        /// <summary>La carpeta «steamapps» de la biblioteca donde está instalado el juego (subiendo desde su carpeta).</summary>
        public static string SteamAppsFolder(string installLocation)
        {
            string d = (installLocation ?? "").Trim().Trim('"').TrimEnd('\\', '/');
            for (int i = 0; i < 6 && d.Length > 3; i++)
            {
                int cut = Math.Max(d.LastIndexOf('\\'), d.LastIndexOf('/'));
                string leaf = cut >= 0 ? d.Substring(cut + 1) : d;
                if (leaf.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return d;
                if (cut <= 0) break;
                d = d.Substring(0, cut);
            }
            return null;
        }

        static string SteamPathFromRegistry()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string s = k != null ? k.GetValue("SteamPath") as string : null;
                    if (!string.IsNullOrEmpty(s)) return s.Replace('/', '\\');
                }
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    string s = k != null ? k.GetValue("InstallPath") as string : null;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch (Exception ex) { Log.Quiet("Carpeta de Steam", ex); }
            return null;
        }

        static string Read(string path)
        {
            if (ReadForTests != null) return ReadForTests(path);
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > 1024 * 1024) return null;
            return File.ReadAllText(path, Encoding.UTF8);
        }

        static string Join(string folder, string file)
        {
            return folder.TrimEnd('\\', '/') + (folder.IndexOf('/') >= 0 && folder.IndexOf('\\') < 0 ? "/" : "\\") + file;
        }

        /// <summary>
        /// Un juego de Steam: Steam guarda en appmanifest_N.acf si el juego está al día o si tiene una actualización pendiente.
        /// «Actualizar» le pide a Steam que verifique el juego (steam://validate/N), que baja lo que falte.
        /// </summary>
        static bool SteamRow(UpdateCandidate c, InstalledProgram p)
        {
            string id = SteamAppId(p);
            if (id == null) return false;
            var folders = new List<string>();
            string apps = SteamAppsFolder(p.InstallLocation);
            if (apps != null) folders.Add(apps);
            string steam = SteamPathFromRegistry();
            if (steam != null) folders.Add(Join(steam, "steamapps"));
            SteamManifest m = null;
            foreach (string f in folders)
            {
                string text = null;
                try { text = Read(Join(f, "appmanifest_" + id + ".acf")); } catch { text = null; }
                m = SteamManifest.Parse(text);
                if (m != null) break;
            }
            if (m == null) return false;
            c.Source = "Steam";
            c.InfoUrl = "https://store.steampowered.com/app/" + id + "/";
            if (VersionUtil.Parse(c.InstalledVersion) == null && m.BuildId.Length > 0) c.InstalledVersion = "compilación " + m.BuildId;
            string when = m.LastUpdated.HasValue ? " Última actualización: " + Fmt.Date(m.LastUpdated.Value) + "." : "";
            if (m.UpdatePending)
            {
                c.Status = UpdateStatus.Disponible;
                c.AvailableVersion = m.TargetBuildId.Length > 0 && m.TargetBuildId != "0" && m.TargetBuildId != m.BuildId ? "compilación " + m.TargetBuildId : "actualización de Steam";
                c.ProviderName = ExternalProvider.ProviderId;
                c.ProviderTag = new ExternalAction { Kind = ExternalAction.ActionKind.SteamValidate, Target = id };
                c.Identifier = "steam:" + id;
                c.Installable = true;
                c.Recommended = true;
                c.Detail = "Steam tiene una actualización pendiente para este juego." + when + " Al actualizar se le pide a Steam que lo verifique " +
                           "y baje lo que falta (Steam tiene que tener la sesión iniciada).";
            }
            else if (m.FullyInstalled)
            {
                c.Status = UpdateStatus.AlDia;
                c.AvailableVersion = "";
                c.Detail = "Según Steam está al día" + (m.BuildId.Length > 0 ? " (compilación " + m.BuildId + ")" : "") + "." + when +
                           " Steam lo revisa cada vez que está abierto.";
            }
            else
            {
                c.Detail = "Steam no lo tiene como instalado por completo (estado " + m.StateFlags + "): abrí Steam › Biblioteca para terminarlo.";
                return true;
            }
            return true;
        }

        // ------------------------------------------------------------------ Carpeta de versiones de programas propios

        static readonly Regex VersionInName = new Regex(@"(?<![\d.])[vV]?(?<v>\d{1,4}(?:\.\d{1,5}){1,3})(?![\d.])", RegexOptions.Compiled);
        static readonly Regex Extensions = new Regex(@"(?:\.(?:7z|zip|rar|exe|msi|msix|appx|tar|gz))+$", RX);
        static readonly Regex Filler = new Regex(@"\b(?:setup|instalador|installer|install|portable|release|final|win(?:dows)?|win64|win32|x64|x86|amd64|full)\b", RX);

        /// <summary>Clave de comparación: sin acentos, sin espacios ni signos, en minúsculas («Ríos y Clima» → «riosyclima»).</summary>
        public static string KeyOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string d = name.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char ch in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>«RelojMonitor_1.9.0.7z», «Virtual Pet 2.0.2 Setup.exe» → programa y versión; null si el nombre no tiene versión.</summary>
        public static OwnBuild ParseBuildName(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string file = path;
            int cut = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
            if (cut >= 0) file = file.Substring(cut + 1);
            string stem = Extensions.Replace(file, "");
            if (stem == file) return null;      // sin extensión conocida (instalador o archivo comprimido)
            Match last = null;
            foreach (Match m in VersionInName.Matches(stem)) last = m;
            if (last == null) return null;
            string prefix = stem.Substring(0, last.Index).Replace('_', ' ').Replace('-', ' ');
            string key = KeyOf(Filler.Replace(prefix, " "));
            string full = KeyOf(prefix);
            if (key.Length < 3) key = full;      // el nombre entero era «relleno» («Portable Win 1.2.zip»): vale tal cual
            if (key.Length < 3) return null;
            return new OwnBuild { Key = key, FullKey = full, Version = last.Groups["v"].Value, Path = path };
        }

        /// <summary>Los archivos de la carpeta y de sus subcarpetas directas que dicen programa y versión. null si no se puede leer.</summary>
        public static List<OwnBuild> ListOwnBuilds(string folder)
        {
            IEnumerable<string> files;
            try
            {
                if (ListForTests != null) files = ListForTests(folder);
                else
                {
                    if (!Directory.Exists(folder)) return null;
                    var l = new List<string>();
                    l.AddRange(Directory.GetFiles(folder));
                    foreach (string sub in Directory.GetDirectories(folder))
                    {
                        try { l.AddRange(Directory.GetFiles(sub)); } catch (Exception ex) { Log.Quiet("Carpeta de versiones", ex); }
                        if (l.Count > 5000) break;
                    }
                    files = l;
                }
            }
            catch (Exception ex) { Log.Warn("Carpeta de versiones " + folder + ": " + ex.Message); return null; }
            var list = new List<OwnBuild>();
            if (files == null) return list;
            foreach (string f in files)
            {
                var b = ParseBuildName(f);
                if (b != null) list.Add(b);
            }
            return list;
        }

        /// <summary>La versión más nueva de ese programa en la carpeta, o null.</summary>
        public static OwnBuild NewestFor(string programName, List<OwnBuild> builds)
        {
            string key = KeyOf(programName);
            if (key.Length < 3 || builds == null) return null;
            // 2.0.16: un programa cuyo nombre lleva una palabra de relleno («Windows Monitor», «Reloj Portable») no coincidía nunca con
            // su archivo, porque al archivo se le sacaba esa palabra y al programa no. Ahora se compara de las dos maneras.
            string stripped = KeyOf(Filler.Replace((programName ?? "").Replace('_', ' ').Replace('-', ' '), " "));
            OwnBuild best = null;
            foreach (var b in builds)
            {
                bool same = b.Key == key || (b.FullKey.Length >= 3 && b.FullKey == key) || (stripped.Length >= 3 && b.Key == stripped);
                if (same && (best == null || VersionUtil.IsNewer(b.Version, best.Version))) best = b;
            }
            return best;
        }

        static bool OwnBuildRow(UpdateCandidate c, InstalledProgram p, List<OwnBuild> builds, string folder)
        {
            if (ProgramRules.IsSelf(p.Name, p.Publisher, "")) return false;
            var b = NewestFor(p.Name, builds);
            if (b == null || VersionUtil.Parse(p.Version) == null) return false;
            string file = b.Path;
            int cut = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
            if (cut >= 0) file = file.Substring(cut + 1);
            c.Source = "Tu carpeta de versiones";
            c.AvailableVersion = VersionUtil.Normalize(b.Version);
            c.InfoUrl = null;
            if (!VersionUtil.IsNewer(b.Version, p.Version))
            {
                c.Status = UpdateStatus.AlDia;
                c.Detail = "La versión más nueva en " + folder + " es la " + b.Version + " («" + file + "»): está al día.";
                return true;
            }
            c.Status = UpdateStatus.Disponible;
            c.Identifier = "propio:" + KeyOf(p.Name);
            c.ProviderName = ExternalProvider.ProviderId;
            c.ProviderTag = new ExternalAction { Kind = ExternalAction.ActionKind.ShowFile, Target = b.Path };
            c.Installable = true;
            c.Recommended = true;
            c.Detail = "En " + folder + " está la " + b.Version + " («" + file + "»). Al actualizar se abre la carpeta con ese archivo marcado, " +
                       "para que lo instales vos (Actualizador no ejecuta instaladores sin firma digital).";
            return true;
        }
    }

    /// <summary>
    /// 2.0.8: «Actualizar» de lo que actualiza otro programa (Steam) o el propio usuario (un archivo de su carpeta de versiones):
    /// se le pasa la tarea a Steam o se abre la carpeta. La fila queda «Pendiente» hasta la próxima búsqueda, que lo confirma.
    /// </summary>
    public sealed class ExternalProvider : IUpdateProvider
    {
        public const string ProviderId = "externo";
        public string Name { get { return ProviderId; } }

        /// <summary>Sólo para las pruebas: (acción, destino) en vez de abrir algo.</summary>
        internal static Action<string, string> OpenForTests;

        public string Unavailable(UpdateContext ctx) { return null; }

        public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes) { }

        static readonly Regex Digits = new Regex(@"^\d{1,10}$", RegexOptions.Compiled);

        public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed)
        {
            foreach (var c in items)
            {
                if (ctx.CancelRequested) { c.Status = UpdateStatus.Pendiente; c.Result = "Cancelada antes de empezar"; changed(c); continue; }
                var a = c.ProviderTag as ExternalAction;
                try
                {
                    if (a != null && a.Kind == ExternalAction.ActionKind.SteamValidate && Digits.IsMatch(a.Target ?? ""))
                    {
                        string url = "steam://validate/" + a.Target;
                        if (OpenForTests != null) OpenForTests("steam", url); else ProcessRunner.OpenForUser(url);
                        c.Status = UpdateStatus.Pendiente;
                        c.Result = "Se le pidió a Steam que verifique el juego y baje la actualización (se ve en Steam › Descargas); después buscá de nuevo para confirmarlo";
                    }
                    else if (a != null && a.Kind == ExternalAction.ActionKind.ShowFile && !string.IsNullOrEmpty(a.Target) && (OpenForTests != null || File.Exists(a.Target)))
                    {
                        if (OpenForTests != null) OpenForTests("archivo", a.Target); else ProcessRunner.ShowInFolderForUser(a.Target);
                        c.Status = UpdateStatus.Pendiente;
                        c.Result = "Se abrió la carpeta con «" + Path.GetFileName(a.Target) + "»: instalalo desde ahí y después buscá de nuevo para confirmarlo";
                    }
                    else InstallerSupport.Fail(c, a != null && a.Kind == ExternalAction.ActionKind.ShowFile ? "Ya no está el archivo " + a.Target : "No se sabe cómo actualizarlo");
                }
                catch (Exception ex) { InstallerSupport.Fail(c, "No se pudo abrir: " + ex.Message); }
                changed(c);
            }
        }
    }
}
