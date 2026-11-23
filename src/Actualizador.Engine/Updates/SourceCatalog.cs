// Catálogo de fuentes oficiales en un archivo JSON, ampliable sin recompilar.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Lo que hacen los actualizadores serios (Patch My PC, SUMo): una lista mantenida de dónde sale la versión de cada
    /// programa y de dónde se baja su instalador, en vez de adivinar leyendo cualquier página. Formato:
    /// <code>
    /// { "programas": [ {
    ///     "id": "usb-disk-security", "producto": "USB Disk Security",
    ///     "nombre": "^USB Disk Security$",                       expresión sobre el nombre en «Aplicaciones instaladas»
    ///     "pagina": "https://www.ejemplo.com/descargas",          página con la versión (HTTPS)
    ///     "version": "USB Disk Security (?&lt;v&gt;\d+\.\d+\.\d+)",     expresión con el grupo «v» (y opcional «url»)
    ///     "instalador": "https://www.ejemplo.com/setup.exe",      opcional: dirección fija que siempre trae la última
    ///     "dominios": ["cdn-ejemplo.net"],                         opcional: otros dominios para la descarga
    ///     "silencioso": "/S", "procesos": ["USBGuard"], "nota": "…" } ] }
    /// </code>
    /// Un archivo así no puede elegir quién firma el instalador: se ejecuta sólo si está firmado por el MISMO firmante que el
    /// programa instalado, siempre por HTTPS y desde el dominio de la página o los indicados. Las expresiones tienen como mucho
    /// 400 caracteres y la página se lee con el mismo límite de tamaño que las demás. Las entradas con el «id» de una incorporada se ignoran.
    /// </summary>
    public static class SourceCatalog
    {
        public const string FileName = "fuentes-oficiales.json";
        public const int MaxEntries = 2000;

        /// <summary>Sólo para las pruebas: rutas de los archivos a leer (null: las de siempre).</summary>
        internal static string[] PathsForTests;

        static readonly object gate = new object();
        static string stamp = null;
        static List<VendorCheck> merged;

        public static string LocalPath { get { return Path.Combine(AppInfo.DataDirectory, FileName); } }
        public static string ProgramPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); } }

        static string[] Paths() { return PathsForTests ?? new[] { ProgramPath, LocalPath }; }

        /// <summary>Las incorporadas más las del catálogo (cacheado mientras los archivos no cambien).</summary>
        public static List<VendorCheck> Merge(List<VendorCheck> builtIn)
        {
            string now = Stamp();
            lock (gate)
            {
                if (merged != null && now == stamp) return merged;
                var list = new List<VendorCheck>(builtIn);
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in builtIn) ids.Add(b.Id);
                foreach (string path in Paths())
                {
                    string error;
                    foreach (var c in Load(path, out error))
                        if (ids.Add(c.Id)) list.Add(c);
                    if (error != null) Log.Warn("Catálogo de fuentes " + path + ": " + error);
                }
                merged = list;
                stamp = now;
                return merged;
            }
        }

        static string Stamp()
        {
            var sb = new StringBuilder();
            foreach (string p in Paths())
            {
                try { var fi = new FileInfo(p); sb.Append(p).Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : "-").Append('|'); }
                catch { sb.Append(p).Append("?|"); }
            }
            return sb.ToString();
        }

        /// <summary>Lee un archivo del catálogo. Las entradas inválidas se saltean (el motivo de la primera queda en <paramref name="error"/>).</summary>
        public static List<VendorCheck> Load(string path, out string error)
        {
            error = null;
            var list = new List<VendorCheck>();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return list;
                var fi = new FileInfo(path);
                if (fi.Length > 4 * 1024 * 1024) { error = "el archivo es demasiado grande"; return list; }
                return Parse(File.ReadAllText(path, Encoding.UTF8), out error);
            }
            catch (Exception ex) { error = ex.Message; return list; }
        }

        public static List<VendorCheck> Parse(string json, out string error)
        {
            error = null;
            var list = new List<VendorCheck>();
            object root;
            try { root = Json.Parse(json ?? ""); }
            catch (Exception ex) { error = "JSON inválido: " + ex.Message; return list; }
            foreach (object x in Json.List(root, "programas"))
            {
                if (list.Count >= MaxEntries) break;
                string why;
                var c = Entry(x, out why);
                if (c != null) list.Add(c);
                else if (error == null) error = why;
            }
            return list;
        }

        static readonly Regex IdRx = new Regex(@"^[a-z0-9][a-z0-9._-]{1,63}$", RegexOptions.Compiled);
        static readonly Regex DomainRx = new Regex(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}$", RegexOptions.Compiled);

        /// <summary>Una entrada del catálogo, o null con el motivo.</summary>
        public static VendorCheck Entry(object x, out string why)
        {
            why = null;
            string id = (Json.Str(x, "id") ?? "").Trim().ToLowerInvariant();
            string product = (Json.Str(x, "producto") ?? "").Trim();
            string name = Json.Str(x, "nombre") ?? "";
            string page = Json.Str(x, "pagina") ?? "";
            string version = Json.Str(x, "version") ?? "";
            if (!IdRx.IsMatch(id)) { why = "«id» inválido: " + Fmt.Truncate(id, 40); return null; }
            if (product.Length == 0 || product.Length > 100) { why = id + ": falta «producto»"; return null; }
            Uri pageUri;
            if (!Uri.TryCreate(page, UriKind.Absolute, out pageUri) || pageUri.Scheme != Uri.UriSchemeHttps || !SiteVersions.IsPublicPage(pageUri))
            { why = id + ": «pagina» tiene que ser una dirección HTTPS pública"; return null; }
            Regex nameRx, versionRx;
            try
            {
                nameRx = new Regex(name, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                versionRx = new Regex(version, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
            }
            catch (ArgumentException ex) { why = id + ": expresión inválida (" + ex.Message + ")"; return null; }
            if (name.Trim().Length < 3) { why = id + ": «nombre» vacío"; return null; }
            if (name.Length > 400 || version.Length > 400) { why = id + ": expresión demasiado larga"; return null; }
            if (Array.IndexOf(versionRx.GetGroupNames(), "v") < 0) { why = id + ": «version» no tiene el grupo «v»"; return null; }

            var c = new VendorCheck();
            c.Id = "cat-" + id;
            c.Product = product;
            c.Vendor = product + " (" + pageUri.Host + ")";
            c.Name = nameRx;
            c.Url = pageUri.AbsoluteUri;
            c.InfoUrl = Fmt.WebUrl(Json.Str(x, "sitio")) ?? c.Url;
            string host = pageUri.Host.ToLowerInvariant();
            c.Domain = host.StartsWith("www.") ? host.Substring(4) : host;
            var also = new List<string>();
            foreach (object d in Json.List(x, "dominios"))
            {
                string dn = (d as string ?? "").Trim().ToLowerInvariant();
                if (DomainRx.IsMatch(dn) && dn.Split('.').Length >= 2) also.Add(dn);
            }
            c.AlsoDomains = also.ToArray();
            c.Feed = VendorFeed.PageRegex;
            c.Version = versionRx;
            c.FromCatalogFile = true;
            string installer = Json.Str(x, "instalador");
            Uri inst;
            if (!string.IsNullOrEmpty(installer))
            {
                if (!Uri.TryCreate(installer, UriKind.Absolute, out inst) || !c.Allows(inst)) { why = id + ": «instalador» tiene que ser HTTPS y del dominio de la página o de «dominios»"; return null; }
                c.LatestInstallerUrl = inst.AbsoluteUri;
            }
            bool hasUrlGroup = Array.IndexOf(versionRx.GetGroupNames(), "url") >= 0;
            c.Install = c.LatestInstallerUrl != null || hasUrlGroup ? VendorInstall.SignedInstaller : VendorInstall.None;
            string silent = Json.Str(x, "silencioso");
            if (silent != null)
            {
                if (!InstallerRules.IsSafeArgs(silent)) { why = id + ": «silencioso» tiene caracteres no permitidos"; return null; }
                c.SilentArgs = silent;
            }
            var procs = new List<string>();
            foreach (object p in Json.List(x, "procesos"))
            {
                string pn = (p as string ?? "").Trim();
                if (Regex.IsMatch(pn, @"^[A-Za-z0-9 ._+\-]{1,60}$")) procs.Add(pn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pn.Substring(0, pn.Length - 4) : pn);
            }
            c.Processes = procs.ToArray();
            c.Signers = new string[0];      // nunca del archivo: el firmante tiene que ser el del programa instalado
            c.Recommended = true;
            c.Note = Fmt.Truncate((Json.Str(x, "nota") ?? "").Trim(), 300);
            c.HowToUpdate = "Descargala de su página oficial (clic derecho › «Abrir sitio del fabricante»).";
            return c;
        }

        // ================================================================== Catálogo descargado

        /// <summary>
        /// Baja el catálogo de la dirección configurada (HTTPS) a %LOCALAPPDATA%\Actualizador, como mucho una vez cada 12 horas.
        /// Sólo lo reemplaza si se puede leer y tiene al menos una entrada válida. Devuelve una nota para la búsqueda, o null.
        /// </summary>
        public static string Refresh(string url)
        {
            string u = Fmt.WebUrl(url);
            Uri parsed;
            if (u == null || !Uri.TryCreate(u, UriKind.Absolute, out parsed) || parsed.Scheme != Uri.UriSchemeHttps) return null;
            try
            {
                var fi = new FileInfo(LocalPath);
                if (fi.Exists && DateTime.UtcNow - fi.LastWriteTimeUtc < TimeSpan.FromHours(12)) return null;
                string json = Http.GetString(u, 30000, delegate(Uri x) { return x.Scheme == Uri.UriSchemeHttps && SiteVersions.IsPublicPage(x); }, "application/json");
                string error;
                var entries = Parse(json, out error);
                if (entries.Count == 0) return "El catálogo de fuentes de " + parsed.Host + " no tiene entradas válidas" + (error != null ? " (" + error + ")" : "") + ": se sigue con el anterior.";
                if (!SafePaths.EnsureDataDirectory(AppInfo.DataDirectory)) return null;
                AtomicFile.WriteAllText(LocalPath, json, new UTF8Encoding(false));
                return "Catálogo de fuentes oficiales actualizado desde " + parsed.Host + ": " + Fmt.Plural(entries.Count, "programa", "programas") + ".";
            }
            catch (Exception ex) { return "No se pudo bajar el catálogo de fuentes de " + parsed.Host + ": " + Http.Describe(ex) + ". Se usa el anterior."; }
        }
    }
}
