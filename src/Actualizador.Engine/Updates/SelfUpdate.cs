// ¿Hay una versión nueva de Actualizador?
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Core.Security;

namespace Actualizador.Updates
{
    /// <summary>
    /// Avisa si hay una versión más nueva de Actualizador. Sólo AVISA: nunca descarga ni instala nada (la versión
    /// nueva se instala con Instalar.cmd de esa entrega). La fuente la elige quien distribuye el
    /// programa, en Opciones › «Dónde se publican las versiones nuevas»:
    /// <list type="bullet">
    /// <item>un proyecto de GitHub («propietario/repositorio» o su dirección): su última versión estable publicada;</item>
    /// <item>una carpeta (local o de red, \\servidor\Actualizador): la entrega más nueva que dejó Empaquetar.ps1
    /// (Actualizador-2.1.0.zip, .7z o la carpeta Actualizador-2.1.0).</item>
    /// </list>
    /// Vacía (lo predeterminado): no se consulta nada.
    /// </summary>
    public static class SelfUpdate
    {
        public sealed class Result
        {
            public string Latest = "";
            /// <summary>Página de la versión (GitHub) o archivo/carpeta de la entrega.</summary>
            public string Where = "";
            public bool Newer;
            public string Error;
            public DateTime Checked;
            /// <summary>2.0.32: la entrega para instalarse sola (Actualizador-x.y.z.zip de la versión de GitHub) y su SHA-256; "" si no hay.</summary>
            public string AssetUrl = "", AssetSha256 = "", AssetName = "";
            /// <summary>
            /// 2.0.46: la firma propia de la entrega es válida («Actualizador-x.y.z.zip.firma», ver <see cref="VersionSignature"/>). Si el
            /// programa trae claves de firma, sin esto NO se instala sola (sólo se avisa). <see cref="SignatureProblem"/>: por qué no.
            /// </summary>
            public bool SignatureOk;
            public string SignatureProblem = "";
            /// <summary>2.0.32: se puede instalar sola: GitHub publica la entrega .zip con su SHA-256 (2.0.46: y firmada, si hace falta).</summary>
            public bool CanInstall { get { return Newer && AssetUrl.Length > 0 && AssetSha256.Length == 64 && (SignatureOk || !VersionSignature.Required); } }
        }

        /// <summary>
        /// 2.0.32 (pedido del usuario, 23/11/2026: «¿podemos hacer que tenga actualización como hicimos con Virtual Pet? es cansador estar
        /// borrando e instalando cada vez»): dónde se publican las versiones nuevas si no se configuró otra cosa en Opciones. Las publica
        /// Publicar.cmd (build\Publicar.ps1) con «gh release create».
        /// </summary>
        public const string DefaultSource = "federicobangher/actualizador";

        /// <summary>La fuente que se usa: la de Opciones o, vacía, <see cref="DefaultSource"/>.</summary>
        public static string EffectiveSource(string configured)
        {
            string s = (configured ?? "").Trim();
            return s.Length > 0 ? s : DefaultSource;
        }

        /// <summary>El nombre de la entrega que se instala sola: «Actualizador-2.0.32.zip».</summary>
        public static string AssetNameFor(string version) { return AppInfo.Name + "-" + version + ".zip"; }

        /// <summary>
        /// 2.0.37 (pedido del usuario, 23/11/2026: «en el portable se puede hacer que se actualice solo sin que se abra la página»): la
        /// portable se actualiza con su propia entrega, «Actualizador-2.0.37-portable.zip» (la publica Publicar.ps1 al lado de la otra).
        /// </summary>
        public static string AssetNameFor(string version, bool portable) { return AppInfo.Name + "-" + version + (portable ? "-portable" : "") + ".zip"; }

        /// <summary>
        /// 2.0.32: la entrega .zip de esa versión entre los archivos publicados, con su SHA-256: el que informa GitHub («digest») o, si no,
        /// el de «Actualizador-x.y.z.zip.sha256» (lo publica Publicar.ps1). <paramref name="shaFiles"/>: el contenido de esos .sha256, por URL.
        /// </summary>
        public static void PickAsset(GitHubRelease rel, Result r, Func<string, string> readSha)
        {
            if (rel == null || r == null) return;
            string want = AssetNameFor(rel.Version);
            GitHubAsset zip = null, sha = null;
            foreach (var a in rel.Assets)
            {
                if (a.Name.Equals(want, StringComparison.OrdinalIgnoreCase)) zip = a;
                else if (a.Name.Equals(want + ".sha256", StringComparison.OrdinalIgnoreCase)) sha = a;
            }
            if (zip == null) return;
            string hash = zip.Sha256 ?? "";
            if (hash.Length != 64 && sha != null && readSha != null)
            {
                try
                {
                    var m = Regex.Match(readSha(sha.Url) ?? "", @"^\s*(?<h>[0-9a-fA-F]{64})\b");
                    if (m.Success) hash = m.Groups["h"].Value.ToLowerInvariant();
                }
                catch (Exception ex) { Log.Warn("Versión nueva: no se pudo leer " + sha.Name + ": " + Http.Describe(ex)); }
            }
            if (hash.Length != 64) return;
            r.AssetUrl = zip.Url;
            r.AssetName = zip.Name;
            r.AssetSha256 = hash.ToLowerInvariant();
        }

        public enum SourceKind { None, GitHub, Folder }

        static readonly Regex OwnerRepo = new Regex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant);
        // 2.0.10: también la portable (Actualizador-2.1.0-portable.7z o su carpeta).
        static readonly Regex Delivery = new Regex(@"^(?:Actualizador|SuperProgram)[-_ ]v?(?<v>\d+\.\d+\.\d+)(?<p>[-_ ]portable)?(?:\.(?:7z|zip))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Qué es la fuente: «propietario/repo» o https://github.com/… → GitHub; una ruta → carpeta.</summary>
        public static SourceKind Classify(string source, out string value)
        {
            value = (source ?? "").Trim();
            if (value.Length == 0) return SourceKind.None;
            string repo = GitHubRules.RepoOf(value);
            if (repo != null) { value = repo; return SourceKind.GitHub; }
            if (OwnerRepo.IsMatch(value) && value.IndexOf('\\') < 0) return SourceKind.GitHub;
            if (value.StartsWith("\\\\") || (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/')) || value.StartsWith("/"))
                return SourceKind.Folder;
            value = "";
            return SourceKind.None;
        }

        static readonly Regex VersionInTag = new Regex(@"\d+(?:\.\d+){1,3}", RegexOptions.CultureInvariant);
        static readonly Regex TagInUrl = new Regex(@"/releases/tag/(?<t>[^/?#]+)/?$", RegexOptions.CultureInvariant);

        /// <summary>Sólo para las pruebas: la dirección final de «/releases/latest» (repo → dirección), y el contenido de un .sha256.</summary>
        internal static Func<string, string> LatestForTests, ShaForTests;
        /// <summary>Sólo para las pruebas: el contenido de un .firma (dirección → texto; null = no está publicado).</summary>
        internal static Func<string, string> FirmaForTests;

        /// <summary>
        /// 2.0.46: lee «&lt;entrega&gt;.firma» y la verifica contra ese archivo, esa versión y ese SHA-256. true si es válida; si no, el
        /// motivo. Se usa al consultar y otra vez justo antes de instalar (con el SHA-256 de lo bajado), así que lo guardado en
        /// version_nueva.json no decide nada.
        /// </summary>
        public static bool CheckSignature(string assetUrl, string asset, string version, string sha256, out string why)
        {
            why = null;
            string text;
            try { text = FirmaForTests != null ? FirmaForTests(assetUrl + VersionSignature.Extension) : Http.GetString(assetUrl + VersionSignature.Extension, 20000, GitHubRules.IsDownload, "*/*"); }
            catch (HttpFailureException hf)
            {
                if (hf.StatusCode == 404) { why = "todavía no está firmada (se firma con Firmar.cmd)"; return false; }
                why = "no se pudo leer su firma (" + Http.Describe(hf) + ")";
                return false;
            }
            catch (Exception ex) { why = "no se pudo leer su firma (" + Http.Describe(ex) + ")"; return false; }
            if (text == null) { why = "todavía no está firmada (se firma con Firmar.cmd)"; return false; }
            return VersionSignature.Verify(text, asset, version, sha256, out why);
        }

        /// <summary>
        /// 2.0.34: la etiqueta de la última versión, por la redirección de «github.com/REPO/releases/latest» a «…/releases/tag/ETIQUETA» (sin
        /// la API). null si el proyecto no tiene versiones publicadas.
        /// </summary>
        public static string LatestTag(string repo)
        {
            string final;
            if (LatestForTests != null) final = LatestForTests(repo);
            else
            {
                try { final = Http.GetRange("https://github.com/" + repo + "/releases/latest", 0, 1024, 20000, GitHubRules.IsDownload).Final.AbsoluteUri; }
                catch (HttpFailureException hf) { if (hf.StatusCode == 404) return null; throw; }
            }
            return TagFromReleaseUrl(final);
        }

        /// <summary>«https://github.com/x/y/releases/tag/v2.0.34» → «v2.0.34»; null si no es una página de versión. Pública para las pruebas.</summary>
        public static string TagFromReleaseUrl(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return null;
            var m = TagInUrl.Match(u.AbsolutePath);
            return m.Success ? Uri.UnescapeDataString(m.Groups["t"].Value) : null;
        }

        /// <summary>La entrega más nueva entre esos nombres de archivo o carpeta (null si no hay ninguna).</summary>
        public static string NewestDelivery(IEnumerable<string> names, out string bestName)
        {
            return NewestDelivery(names, AppInfo.IsPortable, out bestName);
        }

        /// <summary>
        /// 2.0.10: con la misma versión en las dos entregas, se elige la que corresponde a esta copia (la portable para la portable,
        /// la que se instala para la instalada); si sólo está la otra, también sirve para avisar.
        /// </summary>
        public static string NewestDelivery(IEnumerable<string> names, bool preferPortable, out string bestName)
        {
            string best = null;
            bool bestMatches = false;
            bestName = null;
            if (names == null) return null;
            foreach (string n in names)
            {
                string file = (n ?? "").TrimEnd('\\', '/');
                file = file.Substring(file.LastIndexOfAny(new[] { '\\', '/' }) + 1);
                var m = Delivery.Match(file);
                if (!m.Success) continue;
                string v = m.Groups["v"].Value;
                bool matchesKind = m.Groups["p"].Success == preferPortable;
                if (best == null || VersionUtil.IsNewer(v, best) || (!VersionUtil.IsNewer(best, v) && matchesKind && !bestMatches))
                { best = v; bestName = n; bestMatches = matchesKind; }
            }
            return best;
        }

        public static Result Check(string source, string current)
        {
            return Check(source, current, AppInfo.IsPortable);
        }

        /// <summary>Como <see cref="Check(string, string)"/>, buscando la entrega de la portable o la de la instalada.</summary>
        public static Result Check(string source, string current, bool portable)
        {
            var r = new Result();
            r.Checked = DateTime.Now;
            string value;
            var kind = Classify(source, out value);
            try
            {
                if (kind == SourceKind.GitHub)
                {
                    // 2.0.34: por las direcciones comunes de GitHub, no por su API. La API permite 60 consultas por hora sin clave POR
                    // CONEXIÓN a internet, y todos los equipos de una red comparten la misma (el 23/11/2026 la del usuario ya respondía
                    // «API rate limit exceeded»): la consulta de versión nueva fallaba justo donde más equipos hay.
                    Http.EnableModernTls();
                    string tag = LatestTag(value);
                    if (tag == null) { r.Error = "todavía no hay versiones publicadas en " + value + " (se publican con Publicar.cmd o solas desde GitHub)"; return r; }
                    var m = VersionInTag.Match(tag);
                    if (!m.Success) { r.Error = "la última versión de " + value + " no tiene número («" + tag + "»)"; return r; }
                    r.Latest = m.Value;
                    r.Where = "https://github.com/" + value + "/releases/tag/" + Uri.EscapeDataString(tag);
                    string asset = AssetNameFor(r.Latest, portable);
                    string assetUrl = "https://github.com/" + value + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + asset;
                    try
                    {
                        string shaText = ShaForTests != null ? ShaForTests(assetUrl + ".sha256") : Http.GetString(assetUrl + ".sha256", 20000, GitHubRules.IsDownload, "*/*");
                        var h = Regex.Match(shaText ?? "", @"^\s*(?<h>[0-9a-fA-F]{64})\b");
                        if (h.Success) { r.AssetUrl = assetUrl; r.AssetName = asset; r.AssetSha256 = h.Groups["h"].Value.ToLowerInvariant(); }
                    }
                    catch (Exception ex) { Log.Info("Versión nueva: sin " + asset + ".sha256 (" + Http.Describe(ex) + "): sólo se avisa"); }
                    if (r.AssetSha256.Length == 64 && VersionSignature.Required && VersionUtil.IsNewer(r.Latest, current))
                    {
                        string why;
                        r.SignatureOk = CheckSignature(assetUrl, asset, r.Latest, r.AssetSha256, out why);
                        r.SignatureProblem = r.SignatureOk ? "" : why ?? "";
                        if (!r.SignatureOk) Log.Warn("Versión nueva " + r.Latest + ": no se instala sola: " + r.SignatureProblem);
                    }
                }
                else if (kind == SourceKind.Folder)
                {
                    if (!Directory.Exists(value)) { r.Error = "no se encuentra la carpeta " + value; return r; }
                    var names = new List<string>();
                    foreach (string prefix in new[] { AppInfo.Name, AppInfo.LegacyName })   // 2.0.11: también las entregas con el nombre viejo
                    {
                        names.AddRange(Directory.GetFiles(value, prefix + "*"));
                        names.AddRange(Directory.GetDirectories(value, prefix + "*"));
                    }
                    string name;
                    r.Latest = NewestDelivery(names, portable, out name) ?? "";
                    r.Where = name ?? value;
                    if (r.Latest.Length == 0) { r.Error = "no hay entregas de Actualizador (Actualizador-x.y.z.7z) en " + value; return r; }
                }
                else { r.Error = "no hay una fuente configurada"; return r; }
                r.Newer = VersionUtil.IsNewer(r.Latest, current);
            }
            catch (Exception ex) { r.Error = Http.Describe(ex); }
            return r;
        }

        public static string StatePath { get { return Path.Combine(AppInfo.DataDirectory, "version_nueva.json"); } }

        /// <summary>
        /// Cada cuánto se consulta (al abrir el programa). 2.0.32: una hora (antes 20): con la instalación sola, un arreglo publicado tiene que
        /// llegar a los equipos el mismo día. GitHub permite 60 consultas por hora sin clave: sobra.
        /// </summary>
        public static bool ShouldCheck(DateTime? last, DateTime now)
        {
            return !last.HasValue || last.Value > now || (now - last.Value).TotalMinutes >= 60;
        }

        /// <summary>Quién guarda la consulta: «2.0.38» o «2.0.38 portable».</summary>
        static string Consultant { get { return AppInfo.Version + (AppInfo.IsPortable ? " portable" : ""); } }

        public static void Save(string path, Result r, string source)
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["fecha"] = r.Checked.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                d["fuente"] = source ?? "";
                d["version"] = r.Latest ?? "";
                d["donde"] = r.Where ?? "";
                d["archivo"] = r.AssetUrl ?? "";
                d["nombreArchivo"] = r.AssetName ?? "";
                d["sha256"] = r.AssetSha256 ?? "";
                d["firmada"] = r.SignatureOk;
                d["problemaFirma"] = r.SignatureProblem ?? "";
                // 2.0.38: quién consultó (versión y si es la portable): la consulta de otra versión no se reusa (ver Load).
                d["consultadaPor"] = Consultant;
                if (r.Error != null) d["error"] = r.Error;
                if (!SafePaths.EnsureDataDirectory(Path.GetDirectoryName(path))) return;
                AtomicFile.WriteAllText(path, Json.Serialize(d, true), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("Versión nueva: no se pudo guardar el resultado: " + ex.Message); }
        }

        /// <summary>La última consulta guardada (si fue con la misma fuente), o null.</summary>
        public static Result Load(string path, string source, string current)
        {
            try
            {
                if (!File.Exists(path)) return null;
                object o = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (!string.Equals((Json.Str(o, "fuente") ?? "").Trim(), (source ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return null;
                // 2.0.38 (relevamiento): la última consulta la guardó otra versión (la de antes de actualizarse, a mano o sola) o la otra
                // clase (instalada / portable): se vuelve a consultar. La 2.0.35 portable guardaba la entrega de la instalada; al pasar a la
                // 2.0.37 a mano, durante una hora habría intentado instalarse con esa (y fallado). Y recién actualizada, se consulta enseguida.
                if (!string.Equals(Json.Str(o, "consultadaPor") ?? "", Consultant, StringComparison.Ordinal)) return null;
                var r = new Result();
                DateTime d;
                if (!DateTime.TryParse(Json.Str(o, "fecha"), CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return null;
                r.Checked = d;
                r.Latest = Json.Str(o, "version") ?? "";
                r.Where = Json.Str(o, "donde") ?? "";
                r.AssetUrl = Json.Str(o, "archivo") ?? "";
                r.AssetName = Json.Str(o, "nombreArchivo") ?? "";
                r.AssetSha256 = Json.Str(o, "sha256") ?? "";
                // 2.0.46: sólo para mostrar; antes de instalar se vuelve a bajar y verificar la firma (SelfInstall.Stage).
                r.SignatureOk = string.Equals(Json.Str(o, "firmada") ?? "", "true", StringComparison.OrdinalIgnoreCase);
                r.SignatureProblem = Json.Str(o, "problemaFirma") ?? "";
                r.Error = Json.Str(o, "error");
                r.Newer = r.Latest.Length > 0 && VersionUtil.IsNewer(r.Latest, current);
                return r;
            }
            catch { return null; }
        }

        /// <summary>Consulta si corresponde (o devuelve la última consulta). null si no hay fuente configurada.</summary>
        public static Result CheckIfDue(string source, string current)
        {
            string v;
            if (Classify(source, out v) == SourceKind.None) return null;
            var last = Load(StatePath, source, current);
            if (last != null && !ShouldCheck(last.Checked, DateTime.Now)) return last;
            var r = Check(source, current);
            Save(StatePath, r, source);
            if (r.Error != null) Log.Warn("Versión nueva de Actualizador: " + r.Error);
            else Log.Info("Versión nueva de Actualizador: la más nueva publicada es la " + r.Latest + (r.Newer ? " (más nueva que esta, " + current + ")" : " (no hay una más nueva)"));
            return r;
        }

        public static string Message(Result r)
        {
            if (r == null || !r.Newer) return null;
            return "Hay una versión nueva de Actualizador: la " + r.Latest + " (esta es la " + AppInfo.Version + "). Está en " + r.Where + ". " +
                   (!string.IsNullOrEmpty(r.SignatureProblem) && !r.SignatureOk ? "No se instala sola: " + r.SignatureProblem + ". " : "") +
                   HowToUpdate(AppInfo.IsPortable, r.Where);
        }

        /// <summary>2.0.10: cómo se pasa a la versión nueva: la instalada con Instalar.cmd; la portable, reemplazando la carpeta.</summary>
        public static string HowToUpdate(bool portable, string where)
        {
            if (!portable) return "Para instalarla, descomprimila y ejecutá su Instalar.cmd como administrador.";
            bool isPortableDelivery = (where ?? "").IndexOf("portable", StringComparison.OrdinalIgnoreCase) >= 0;
            return (isPortableDelivery ? "Es portable: cerrá Actualizador y descomprimila en lugar de esta carpeta"
                                       : "Esa entrega es la que se instala; para la portable, armala con Portable.cmd y descomprimila en lugar de esta carpeta") +
                   " (podés conservar la carpeta «" + PortableLayout.DataFolder + "», con las opciones y el historial de cada equipo).";
        }
    }
}
