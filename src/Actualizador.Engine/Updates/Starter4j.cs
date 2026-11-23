// 2.0.19: actualizar un programa que se actualiza con el arranque «starter4j» (TLauncher) haciendo lo mismo que hace ese arranque,
// sin abrir su página ni su ventana de actualización (que trae una «oferta opcional» de programas de terceros).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Un archivo que el arranque necesita para la versión nueva: dónde va, con qué SHA-1 y tamaño, y de dónde se baja.</summary>
    public sealed class StarterFile
    {
        public string Path = "";          // relativo a la carpeta de trabajo del arranque («original-TLauncher-2.9382.jar»)
        public string Sha1 = "";          // 40 hexadecimales en minúscula
        public long Size;
        public readonly List<string> Urls = new List<string>();   // en orden; sólo HTTPS y del fabricante
        /// <summary>El ejecutable de la aplicación (appFileRepo): sin él verificado no se cambia nada.</summary>
        public bool Main;
    }

    /// <summary>Lo que dice «appConfig.json»: la versión y los repositorios con sus archivos.</summary>
    public sealed class StarterConfig
    {
        public string Version = "";
        /// <summary>appFileRepo: los archivos van en el propio appConfig.json.</summary>
        public readonly List<StarterFile> MainFiles = new List<StarterFile>();
        /// <summary>appDependencies y appResources: índices («2.9382/dependencies.json») con más archivos. Direcciones ya resueltas.</summary>
        public readonly List<List<string>> Indexes = new List<List<string>>();
    }

    /// <summary>
    /// 2.0.19: TLauncher usa el arranque de código abierto starter4j (github.com/gdevby/starter4j, paquete «by.gdev»). Al abrirse baja
    /// «appConfig.json» y, si su «appVersion» es más nueva que «currentAppVersion» de «starter.json», muestra una ventana para elegir
    /// «Actualizar» u «Omitir»: «Actualizar» sólo anota la versión nueva en «starter.json»; después el arranque baja los archivos que
    /// faltan a su carpeta de trabajo (%APPDATA%\.tlauncher\starter), verificando el SHA-1 y el tamaño que publica la misma
    /// configuración, y arranca esa versión. Actualizador hace exactamente eso, sin abrir nada:
    /// <list type="number">
    /// <item>lee «appConfig.json» del servidor oficial (HTTPS; si no responde, del alternativo que usa el propio TLauncher);</item>
    /// <item>baja los archivos que faltan o cambiaron, y sólo los guarda si el SHA-1 y el tamaño coinciden;</item>
    /// <item>anota la versión nueva en «starter.json» (con una copia del anterior). La próxima vez que se abre, TLauncher ya está en la
    /// versión nueva y no pregunta nada.</item>
    /// </list>
    /// No se baja Java (el arranque lo mantiene aparte y no cambia con cada versión).
    /// </summary>
    public static class Starter4j
    {
        /// <summary>Sólo para las pruebas: reemplaza la descarga de un archivo (dirección, ruta destino).</summary>
        internal static Action<string, string> DownloadForTests;
        /// <summary>Sólo para las pruebas: reemplaza la descarga de los índices (dirección → contenido).</summary>
        internal static Func<string, string> FetchForTests;

        /// <summary>Topes: el ejecutable de TLauncher pesa unos 9 MB y sus dependencias unas decenas.</summary>
        internal const int MaxFiles = 5000;
        internal const long MaxTotalBytes = 1536L * 1024 * 1024;
        internal const long MaxFileBytes = 512L * 1024 * 1024;

        static readonly Regex Sha1Shape = new Regex(@"^[0-9a-f]{40}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        static readonly Regex VersionShape = new Regex(@"^\d{1,4}(?:\.\d{1,6}){1,3}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        /// <summary>Tramos de una ruta relativa: letras, números y «. _ - + ( ) espacio», sin empezar con punto ni espacio.</summary>
        static readonly Regex SafeSegment = new Regex(@"^[A-Za-z0-9_\-+(][A-Za-z0-9 ._\-+()]{0,150}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

        // ------------------------------------------------------------------ appConfig.json

        /// <summary>
        /// Lee «appConfig.json». <paramref name="configUrl"/> es la dirección de donde salió (los repositorios relativos se resuelven
        /// contra ella). Sólo quedan direcciones HTTPS que <paramref name="allowed"/> acepta. null con el motivo si no sirve.
        /// </summary>
        public static StarterConfig ParseConfig(string json, string configUrl, Func<Uri, bool> allowed, out string error)
        {
            error = null;
            object root;
            try { root = Json.Parse(json ?? ""); }
            catch (Exception ex) { error = "appConfig.json no es un JSON válido (" + ex.Message + ")"; return null; }
            if (!(root is Dictionary<string, object>)) { error = "appConfig.json no tiene el formato esperado"; return null; }
            var cfg = new StarterConfig();
            cfg.Version = (Json.Str(root, "appVersion") ?? "").Trim();
            if (!VersionShape.IsMatch(cfg.Version)) { error = "appConfig.json no dice una versión válida («" + Fmt.Truncate(cfg.Version, 30) + "»)"; return null; }
            var fileRepo = Json.Path(root, "appFileRepo");
            cfg.MainFiles.AddRange(FilesOf(fileRepo, configUrl, allowed, true));
            if (cfg.MainFiles.Count == 0) { error = "appConfig.json no publica el archivo de la aplicación con su SHA-1 en un servidor oficial"; return null; }
            foreach (string key in new[] { "appDependencies", "appResources" })
            {
                var repo = Json.Path(root, key);
                if (repo == null) continue;
                var bases = Repositories(repo, configUrl, allowed);
                foreach (var res in Json.List(repo, "resources"))
                {
                    var urls = new List<string>();
                    string rel = Json.Str(res, "relativeUrl");
                    foreach (string b in bases) { string u = Resolve(b, rel, allowed); if (u != null && !urls.Contains(u)) urls.Add(u); }
                    if (urls.Count > 0) cfg.Indexes.Add(urls);
                }
            }
            return cfg;
        }

        /// <summary>Los archivos de un «Repo» de starter4j (repositories + resources con sha1, size, path, relativeUrl y urls).</summary>
        public static List<StarterFile> FilesOf(object repo, string baseUrl, Func<Uri, bool> allowed, bool main)
        {
            var list = new List<StarterFile>();
            if (repo == null) return list;
            var bases = Repositories(repo, baseUrl, allowed);
            foreach (var res in Json.List(repo, "resources"))
            {
                if (!(res is Dictionary<string, object>)) continue;
                // «link»: un enlace simbólico que crea el arranque (no se baja ni se verifica). No se toca.
                if (!string.IsNullOrEmpty(Json.Str(res, "link"))) continue;
                var f = new StarterFile();
                f.Main = main;
                f.Path = (Json.Str(res, "path") ?? "").Trim();
                f.Sha1 = (Json.Str(res, "sha1") ?? "").Trim().ToLowerInvariant();
                long size;
                if (!long.TryParse(Json.Str(res, "size") ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out size)) size = -1;
                f.Size = size;
                if (RelativeParts(f.Path) == null || !Sha1Shape.IsMatch(f.Sha1) || f.Size < 0 || f.Size > MaxFileBytes) continue;
                // Primero las direcciones completas que trae el archivo («urls»), después cada repositorio con «relativeUrl».
                foreach (var o in Json.List(res, "urls"))
                {
                    string u = Resolve(null, o as string, allowed);
                    if (u != null && !f.Urls.Contains(u)) f.Urls.Add(u);
                }
                string rel = Json.Str(res, "relativeUrl");
                foreach (string b in bases)
                {
                    string u = Resolve(b, rel, allowed);
                    if (u != null && !f.Urls.Contains(u)) f.Urls.Add(u);
                }
                if (f.Urls.Count > 0) list.Add(f);
            }
            return list;
        }

        /// <summary>Los repositorios de un «Repo» (terminados en «/»), sólo HTTPS del fabricante. Sin repositorios: la carpeta de <paramref name="baseUrl"/>.</summary>
        static List<string> Repositories(object repo, string baseUrl, Func<Uri, bool> allowed)
        {
            var l = new List<string>();
            var listed = Json.List(repo, "repositories");
            foreach (var o in listed)
            {
                string s = (o as string ?? "").Trim();
                if (s.Length == 0) continue;
                if (!s.EndsWith("/", StringComparison.Ordinal)) s += "/";
                Uri u;
                if (!Uri.TryCreate(s, UriKind.Absolute, out u) || !Allowed(u, allowed) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) continue;
                if (!l.Contains(u.AbsoluteUri)) l.Add(u.AbsoluteUri);
            }
            // Sin repositorios (no con repositorios de otros servidores): la carpeta de donde salió la configuración.
            if (l.Count == 0 && listed.Count == 0 && !string.IsNullOrEmpty(baseUrl))
            {
                Uri b;
                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out b) && Allowed(b, allowed))
                    l.Add(new Uri(b, "./").AbsoluteUri);
            }
            return l;
        }

        static bool Allowed(Uri u, Func<Uri, bool> allowed)
        {
            return u != null && u.IsAbsoluteUri && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo) && allowed != null && allowed(u);
        }

        /// <summary>
        /// La dirección de un archivo: <paramref name="rel"/> («2.9382/original-TLauncher-2.9382.jar») dentro de <paramref name="baseUrl"/>, o
        /// completa si <paramref name="baseUrl"/> es null. Nunca fuera del repositorio («..», «/», otro servidor), siempre HTTPS del fabricante.
        /// </summary>
        public static string Resolve(string baseUrl, string rel, Func<Uri, bool> allowed)
        {
            rel = (rel ?? "").Trim();
            if (rel.Length == 0 || rel.Length > 600 || rel.IndexOf('\\') >= 0 || rel.IndexOf("..", StringComparison.Ordinal) >= 0) return null;
            Uri u;
            if (baseUrl == null)
            {
                if (!Uri.TryCreate(rel, UriKind.Absolute, out u)) return null;
            }
            else
            {
                if (rel.StartsWith("/", StringComparison.Ordinal) || rel.IndexOf(':') >= 0 || rel.IndexOf('?') >= 0 || rel.IndexOf('#') >= 0) return null;
                Uri b;
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out b) || !Uri.TryCreate(b, rel, out u)) return null;
                if (!u.AbsoluteUri.StartsWith(b.AbsoluteUri, StringComparison.Ordinal)) return null;
            }
            return Allowed(u, allowed) ? u.AbsoluteUri : null;
        }

        // ------------------------------------------------------------------ Carpeta de trabajo

        /// <summary>«libs/a/b.jar» → {"libs","a","b.jar"}; null si no es una ruta relativa segura (absoluta, «..», «:», tramos raros).</summary>
        public static string[] RelativeParts(string rel)
        {
            if (string.IsNullOrEmpty(rel) || rel.Length > 400) return null;
            if (rel[0] == '/' || rel[0] == '\\' || rel.IndexOf(':') >= 0) return null;
            var parts = rel.Split(new[] { '/', '\\' });
            if (parts.Length > 12) return null;
            foreach (string s in parts)
            {
                if (s.Length == 0 || s == "." || s == ".." || !SafeSegment.IsMatch(s) || s.EndsWith(".", StringComparison.Ordinal) || s.EndsWith(" ", StringComparison.Ordinal)) return null;
                // Nombres reservados de Windows («con», «nul.txt»…).
                string stem = s.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || Regex.IsMatch(stem, @"^(?:COM|LPT)\d$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))) return null;
            }
            // Nunca «starter.json» ni la copia que deja Actualizador: eso lo escribe sólo el paso final.
            if (parts.Length == 1 && (parts[0].Equals("starter.json", StringComparison.OrdinalIgnoreCase) || parts[0].StartsWith("starter.json.", StringComparison.OrdinalIgnoreCase))) return null;
            return parts;
        }

        /// <summary>La ruta completa de un archivo dentro de la carpeta de trabajo, o null si saldría de ella.</summary>
        public static string LocalPath(string workDir, string rel)
        {
            var parts = RelativeParts(rel);
            if (parts == null || string.IsNullOrEmpty(workDir)) return null;
            string root = System.IO.Path.GetFullPath(workDir).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, string.Join(System.IO.Path.DirectorySeparatorChar.ToString(), parts)));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.Length > root.Length ? full : null;
        }

        /// <summary>
        /// ¿Hay un enlace (unión, enlace simbólico) entre la carpeta de trabajo y el archivo, o en la propia carpeta? Actualizador corre
        /// como administrador y escribe en el perfil del usuario: un enlace puesto ahí lo haría escribir en otra parte (C:\Windows).
        /// </summary>
        internal static bool CrossesLink(string workDir, string full)
        {
            try
            {
                string root = System.IO.Path.GetFullPath(workDir).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                // La carpeta de trabajo y la de arriba (.tlauncher).
                var rootDi = new DirectoryInfo(root);
                if (rootDi.Exists && (rootDi.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                if (rootDi.Parent != null && rootDi.Parent.Exists && (rootDi.Parent.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                string cur = System.IO.Path.GetDirectoryName(full);
                while (!string.IsNullOrEmpty(cur) && cur.Length > root.Length)
                {
                    var di = new DirectoryInfo(cur);
                    if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                    cur = System.IO.Path.GetDirectoryName(cur);
                }
                var fi = new FileInfo(full);
                if (fi.Exists && (fi.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                return false;
            }
            catch (Exception ex) { Log.Quiet("Enlaces en " + workDir, ex); return true; }
        }

        /// <summary>SHA-1 en hexadecimal minúscula (el que publica starter4j).</summary>
        public static string Sha1Of(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>¿El archivo ya está y es exactamente el publicado (tamaño y SHA-1)?</summary>
        public static bool AlreadyThere(string full, StarterFile f)
        {
            try
            {
                var fi = new FileInfo(full);
                return fi.Exists && fi.Length == f.Size && Sha1Of(full) == f.Sha1;
            }
            catch (Exception ex) { Log.Quiet("Comparar " + full, ex); return false; }
        }

        // ------------------------------------------------------------------ starter.json

        /// <summary>
        /// «starter.json» con la versión nueva: lo mismo que escribe el arranque al tocar «Actualizar» en su ventana (currentAppVersion;
        /// si esa versión estaba «omitida», deja de estarlo). Los demás datos quedan como estaban. null si el archivo no se entiende.
        /// </summary>
        public static string WithVersion(string starterJson, string version)
        {
            object root;
            try { root = Json.Parse(starterJson ?? ""); }
            catch { return null; }
            var d = root as Dictionary<string, object>;
            if (d == null || !VersionShape.IsMatch(version ?? "")) return null;
            string key = "currentAppVersion", skip = "skipUpdateVersion";
            foreach (var k in new List<string>(d.Keys))
            {
                if (k.Equals("currentAppVersion", StringComparison.OrdinalIgnoreCase)) key = k;
                if (k.Equals("skipUpdateVersion", StringComparison.OrdinalIgnoreCase)) skip = k;
            }
            d[key] = version;
            object skipped;
            if (d.TryGetValue(skip, out skipped) && skipped is string && VersionUtil.Parse((string)skipped) != null && !VersionUtil.IsNewer((string)skipped, version))
                d[skip] = null;
            return Json.Serialize(d, false);
        }

        // ------------------------------------------------------------------ Aplicar

        /// <summary>Lo que pasó al aplicar la versión nueva.</summary>
        public sealed class Outcome
        {
            public bool Ok;
            public string Error;
            public int Downloaded, AlreadyOk, LeftForStarter;
            public long Bytes;
        }

        /// <summary>
        /// Baja y verifica los archivos de la versión de <paramref name="cfg"/> en <paramref name="workDir"/> y, si el de la aplicación
        /// quedó verificado, anota la versión en <paramref name="starterFile"/>. Lo que no se pudo bajar de un índice (dependencias,
        /// imágenes) lo completa el propio arranque al abrirse, verificándolo igual; el de la aplicación no: sin él no se cambia nada.
        /// </summary>
        public static Outcome Apply(StarterConfig cfg, string workDir, string starterFile, Func<Uri, bool> allowed, UpdateContext ctx, Action<string> progress)
        {
            var o = new Outcome();
            if (cfg == null) { o.Error = "no se pudo leer la configuración publicada"; return o; }
            if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir)) { o.Error = "no se encontró la carpeta de " + (workDir ?? "trabajo"); return o; }
            if (CrossesLink(workDir, System.IO.Path.Combine(workDir, "x")))
            { o.Error = "la carpeta " + workDir + " es un enlace a otra parte: por seguridad no se escribe en ella"; return o; }

            // Los archivos de los índices (dependencias, recursos): si un índice no se puede leer, esos archivos los baja el arranque.
            var files = new List<StarterFile>(cfg.MainFiles);
            foreach (var urls in cfg.Indexes)
            {
                string text = null;
                foreach (string u in urls)
                {
                    if (ctx != null) ctx.ThrowIfCancelled();
                    try
                    {
                        text = FetchForTests != null ? FetchForTests(u) : Http.GetString(u, 30000, allowed, "application/json");
                        if (!string.IsNullOrEmpty(text)) break;
                    }
                    catch (OperationCanceledByUserException) { throw; }
                    catch (Exception ex) { Log.Warn("Índice " + u + ": " + Http.Describe(ex)); text = null; }
                }
                if (string.IsNullOrEmpty(text)) { o.LeftForStarter++; continue; }
                try { files.AddRange(FilesOf(Json.Parse(text), urls[0], allowed, false)); }
                catch (Exception ex) { Log.Warn("Índice " + urls[0] + " no se entiende: " + ex.Message); o.LeftForStarter++; }
            }
            if (files.Count > MaxFiles) { o.Error = "la configuración publicada lista " + files.Count + " archivos (más de lo razonable): no se sigue"; return o; }
            long total = 0;
            foreach (var f in files) total += f.Size;
            if (total > MaxTotalBytes) { o.Error = "la configuración publicada pide " + Fmt.Bytes(total) + " (más de lo razonable): no se sigue"; return o; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var f in files)
            {
                n++;
                if (ctx != null) ctx.ThrowIfCancelled();
                string full = LocalPath(workDir, f.Path);
                if (full == null || !seen.Add(full)) { if (f.Main) { o.Error = "la ruta del archivo de la aplicación no es válida («" + Fmt.Truncate(f.Path, 80) + "»)"; return o; } continue; }
                if (AlreadyThere(full, f)) { o.AlreadyOk++; continue; }
                string err = DownloadOne(f, full, workDir, allowed, ctx, progress, n, files.Count);
                if (err == null) { o.Downloaded++; o.Bytes += f.Size; continue; }
                if (f.Main) { o.Error = err; return o; }
                Log.Warn(System.IO.Path.GetFileName(full) + ": " + err + " (lo baja el propio arranque al abrirse)");
                o.LeftForStarter++;
            }

            // Recién ahora, con la aplicación verificada en su lugar: la versión nueva en starter.json (con copia del anterior).
            string err2 = WriteVersion(workDir, starterFile, cfg.Version);
            if (err2 != null) { o.Error = err2; return o; }
            o.Ok = true;
            return o;
        }

        static string DownloadOne(StarterFile f, string full, string workDir, Func<Uri, bool> allowed, UpdateContext ctx, Action<string> progress, int n, int count)
        {
            string dir = System.IO.Path.GetDirectoryName(full);
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { return "no se pudo crear " + dir + " (" + ex.Message + ")"; }
            if (CrossesLink(workDir, full)) return "la carpeta " + dir + " es un enlace a otra parte: no se escribe";
            string tmp = full + ".actualizador.part";
            string last = "no se pudo descargar";
            string mismatch = null;
            foreach (string url in f.Urls)
            {
                if (ctx != null) ctx.ThrowIfCancelled();
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    if (progress != null) progress("Descargando " + System.IO.Path.GetFileName(full) + " (" + n + " de " + count + ", " + Fmt.Bytes(f.Size) + ") de " + new Uri(url).Host + "…");
                    if (DownloadForTests != null) DownloadForTests(url, tmp); else Http.Download(url, tmp, 20 * 60 * 1000, ctx, allowed);
                    var fi = new FileInfo(tmp);
                    if (!fi.Exists) { last = "la descarga no dejó el archivo"; continue; }
                    if (fi.Length != f.Size) { mismatch = last = "el tamaño de la descarga (" + fi.Length + ") no es el publicado (" + f.Size + "): el SHA-1 no se pudo verificar"; Log.Warn(url + ": " + last); continue; }
                    string got = Sha1Of(tmp);
                    if (got != f.Sha1) { mismatch = last = "el SHA-1 de la descarga no coincide con el publicado"; Log.Warn(url + ": SHA-1 " + got + ", publicado " + f.Sha1); continue; }
                    if (CrossesLink(workDir, full)) { last = "la carpeta " + dir + " es un enlace a otra parte: no se escribe"; break; }
                    if (File.Exists(full)) File.Delete(full);
                    File.Move(tmp, full);
                    Log.Info("starter4j: " + f.Path + " verificado (SHA-1 " + got + ", " + f.Size + " bytes) desde " + new Uri(url).Host);
                    return null;
                }
                catch (OperationCanceledByUserException) { throw; }
                catch (Exception ex) { last = Http.Describe(ex); Log.Warn(url + ": " + last); }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            }
            // Si algún servidor mandó otra cosa, eso es lo importante (no el error del último).
            return mismatch ?? last;
        }

        /// <summary>Anota la versión en starter.json (escribe a un temporal y lo pone en su lugar, guardando el anterior). null si salió bien.</summary>
        internal static string WriteVersion(string workDir, string starterFile, string version)
        {
            try
            {
                string full = System.IO.Path.GetFullPath(starterFile);
                if (!System.IO.Path.GetDirectoryName(full).TrimEnd(System.IO.Path.DirectorySeparatorChar)
                        .Equals(System.IO.Path.GetFullPath(workDir).TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    return "starter.json no está en la carpeta de trabajo";
                if (CrossesLink(workDir, full)) return "starter.json es un enlace a otra parte: no se escribe";
                string old = File.ReadAllText(full);
                string now = WithVersion(old, version);
                if (now == null) return "no se entiende " + full;
                string tmp = full + ".actualizador.part";
                File.WriteAllText(tmp, now, new UTF8Encoding(false));
                string backup = full + ".actualizador.bak";
                try { File.Copy(full, backup, true); } catch (Exception ex) { Log.Quiet("Copia de starter.json", ex); }
                File.Copy(tmp, full, true);
                File.Delete(tmp);
                return null;
            }
            catch (Exception ex) { return "no se pudo anotar la versión en starter.json (" + ex.Message + ")"; }
        }
    }
}
