// 2.0.17: el instalador oficial de una versión que sólo se leyó en la página del programa (antes, «Actualizar» abría esa página).
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    /// <summary>Un instalador encontrado para la versión que publica la página oficial de un programa.</summary>
    public sealed class InstallerLead
    {
        /// <summary>El manifiesto de winget-pkgs de ESA versión (trae su SHA-256): el catálogo local de winget todavía no la mostraba.</summary>
        public const string Catalog = "catalogo";
        /// <summary>La dirección del instalador de la versión anterior en winget-pkgs, con la versión nueva en el lugar de la vieja.</summary>
        public const string Previous = "anterior";
        /// <summary>Un enlace de la página oficial del programa o de su página de descargas.</summary>
        public const string Page = "pagina";

        public string Url = "", FileName = "", Origin = "", Sha256 = "", WingetId = "";
        /// <summary>Para el enlace de una página: la página donde estaba.</summary>
        public string FoundAt = "";
        /// <summary>Tipo e interruptores del manifiesto de winget (los mismos que usaría winget), o null.</summary>
        public WingetInstaller Winget;
        public bool IsMsi;
        /// <summary>El nombre del archivo, su dirección o sus recursos dicen que es de la versión publicada.</summary>
        public bool VersionConfirmed;

        /// <summary>De dónde salió, para el detalle de la fila.</summary>
        public string Describe()
        {
            string host;
            try { host = new Uri(Url).Host; } catch { host = ""; }
            string file = FileName.Length > 0 ? "«" + Fmt.Truncate(FileName, 60) + "»" : "el instalador";
            switch (Origin)
            {
                case Catalog:
                    return "El repositorio oficial de winget (winget-pkgs) ya publica esa versión de " + WingetId + ", aunque el catálogo de este equipo todavía no la muestra: " +
                           "se instala con el mismo instalador y la misma comprobación (SHA-256 del catálogo) que usaría winget.";
                case Previous:
                    return "Se encontró su instalador (" + file + ", en " + host + ") en la misma dirección de donde winget baja la versión anterior de " + WingetId +
                           ": se descarga por HTTPS y sólo se instala si su firma digital es válida y del mismo fabricante que el programa instalado, y es de este programa.";
                default:
                    string where = "";
                    try { if (FoundAt.Length > 0) where = " (enlazado desde " + new Uri(FoundAt).Host + ")"; } catch { }
                    return "Se encontró su instalador (" + file + where + "): se descarga por HTTPS y sólo se instala si su firma digital es válida y del mismo " +
                           "fabricante que el programa instalado, y es de este programa.";
            }
        }
    }

    /// <summary>
    /// Busca el instalador de la versión que publica la página oficial de un programa, para instalarla desde acá en vez de abrir la
    /// página (pedido del usuario, 15/11/2026: CrystalDiskInfo 9.9.4 con la 9.9.5 publicada ese día y «Se abrió su página…»).
    /// <list type="number">
    /// <item>winget-pkgs: si winget conoce el programa pero su catálogo va atrasado, el manifiesto de esa versión (con su SHA-256) si ya
    /// está en el repositorio; si no, la dirección del instalador de la versión anterior con la versión nueva en su lugar
    /// («…/9.9.4/CrystalDiskInfo9_9_4.exe» → «…/9.9.5/CrystalDiskInfo9_9_5.exe»), comprobando que exista y sea un instalador.</item>
    /// <item>La página oficial: enlaces a un instalador de este programa, también los que pasan por una redirección
    /// («redirect.php?product=CrystalDiskInfoInstaller») y los de su página de descargas.</item>
    /// </list>
    /// Acá no se ejecuta ni se verifica nada: sólo se ubica el archivo (se piden sus primeros bytes). La verificación (firma del mismo
    /// fabricante que lo instalado, o el SHA-256 del catálogo de winget, y que sea de este programa) se hace al instalar.
    /// </summary>
    public static class SiteInstallers
    {
        /// <summary>Tiempo total para ubicar el instalador de un programa.</summary>
        public static TimeSpan Budget = TimeSpan.FromSeconds(40);
        /// <summary>Plazo de cada pedido.</summary>
        public const int ProbeTimeoutMs = 12000;
        /// <summary>Páginas de descarga que se siguen desde la página oficial, y enlaces que se prueban.</summary>
        public const int MaxSubPages = 3, MaxProbes = 5;

        const RegexOptions RX = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        // ------------------------------------------------------------------ Dirección con la versión nueva

        /// <summary>
        /// La dirección del instalador de <paramref name="oldVersion"/> con <paramref name="newVersion"/> en su lugar, en todas las formas
        /// en que aparezca: «9.9.4», «9_9_4», «9-9-4» y, sólo en el nombre del archivo y si no apareció de otra forma, sin separadores
        /// («winrar-x64-701.exe»). null si la dirección no lleva la versión anterior (o las versiones no sirven).
        /// </summary>
        public static string TemplateUrl(string url, string oldVersion, string newVersion)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string[] o = Parts(oldVersion), n = Parts(newVersion);
            if (o == null || n == null) return null;
            string result = url;
            bool any = false;
            // Con todas las partes y, si la versión termina en «.0», también sin ellas («9.9.4.0» en el manifiesto y «9.9.4» en la dirección).
            foreach (var pair in Pairs(o, n))
            {
                foreach (char sep in new[] { '.', '_', '-' })
                {
                    string from = string.Join(sep.ToString(), pair.Key), to = string.Join(sep.ToString(), pair.Value);
                    if (pair.Key.Length < 2 || from == to) continue;
                    // Que no sea un pedazo de otro número: ni «19.9.4» ni «9.9.41» ni «9.9.4.1».
                    string rx = sep == '.' ? @"(?<![\d]|\d\.)" + Regex.Escape(from) + @"(?!\d|\.\d)"
                                           : @"(?<![\d]|\d" + Regex.Escape(sep.ToString()) + ")" + Regex.Escape(from) + @"(?!\d|" + Regex.Escape(sep.ToString()) + @"\d)";
                    string next = Regex.Replace(result, rx, to.Replace("$", "$$"), RegexOptions.CultureInvariant);
                    if (next != result) { result = next; any = true; }
                }
                if (any) break;
            }
            if (!any)
            {
                // Sin separadores, sólo en el nombre del archivo: «701» → «710», «2408» → «2409».
                string from = string.Concat(o), to = string.Concat(n);
                int slash = result.LastIndexOf('/');
                int q = result.IndexOf('?');
                if (from.Length >= 3 && from != to && slash >= 0 && (q < 0 || slash < q))
                {
                    string head = result.Substring(0, slash + 1), file = q < 0 ? result.Substring(slash + 1) : result.Substring(slash + 1, q - slash - 1), tail = q < 0 ? "" : result.Substring(q);
                    var m = Regex.Matches(file, @"(?<!\d)" + Regex.Escape(from) + @"(?!\d)");
                    if (m.Count == 1) { result = head + file.Substring(0, m[0].Index) + to + file.Substring(m[0].Index + from.Length) + tail; any = true; }
                }
            }
            return any ? result : null;
        }

        /// <summary>Las partes de una versión tal como están escritas (con sus ceros: «7.01» → «7», «01»); null si no es sólo números y puntos.</summary>
        static string[] Parts(string version)
        {
            string v = (version ?? "").Trim().TrimStart('v', 'V');
            if (!Regex.IsMatch(v, @"^\d{1,9}(?:\.\d{1,9}){1,5}$")) return null;
            return v.Split('.');
        }

        static List<KeyValuePair<string[], string[]>> Pairs(string[] o, string[] n)
        {
            var list = new List<KeyValuePair<string[], string[]>>();
            list.Add(new KeyValuePair<string[], string[]>(o, n));
            string[] ot = TrimZeros(o), nt = TrimZeros(n);
            if (ot.Length != o.Length) list.Add(new KeyValuePair<string[], string[]>(ot, nt.Length <= ot.Length ? nt : n));
            return list;
        }

        static string[] TrimZeros(string[] parts)
        {
            int len = parts.Length;
            while (len > 2 && Regex.IsMatch(parts[len - 1], "^0+$")) len--;
            var r = new string[len];
            Array.Copy(parts, r, len);
            return r;
        }

        static readonly Regex SourceForgeFile = new Regex(@"^https://(?:www\.)?sourceforge\.net/projects/(?<p>[A-Za-z0-9._\-]+)/files/(?<f>[^?#]+?)/download/?(?:[?#].*)?$", RX);

        /// <summary>
        /// «sourceforge.net/projects/X/files/RUTA/download» muestra a los navegadores una página de espera; el mismo archivo se baja
        /// directo de «downloads.sourceforge.net/project/X/RUTA» (que redirige al servidor más cercano). Las demás direcciones, igual.
        /// </summary>
        public static string SourceForgeDirect(string url)
        {
            var m = SourceForgeFile.Match(url ?? "");
            return m.Success ? "https://downloads.sourceforge.net/project/" + m.Groups["p"].Value + "/" + m.Groups["f"].Value : url;
        }

        /// <summary>¿El texto (nombre o ruta del archivo) lleva esa versión? «CrystalDiskInfo9_9_5.exe», «npp.8.6.9.Installer.exe», «winrar-x64-710.exe».</summary>
        public static bool NameHasVersion(string text, string version)
        {
            string[] p = Parts(version);
            if (p == null || string.IsNullOrEmpty(text)) return false;
            foreach (string[] parts in new[] { p, TrimZeros(p) })
            {
                foreach (char sep in new[] { '.', '_', '-' })
                {
                    string v = string.Join(sep.ToString(), parts);
                    if (Regex.IsMatch(text, @"(?<!\d)" + Regex.Escape(v) + @"(?!\d|" + Regex.Escape(sep.ToString()) + @"\d)", RegexOptions.CultureInvariant)) return true;
                }
            }
            string compact = string.Concat(p);
            return compact.Length >= 3 && Regex.IsMatch(text, @"(?<!\d)" + Regex.Escape(compact) + @"(?!\d)", RegexOptions.CultureInvariant);
        }

        // ------------------------------------------------------------------ Lo que se averigua del archivo sin bajarlo

        /// <summary>El archivo al que lleva una dirección: dónde termina, cómo se llama y si es un instalador.</summary>
        public sealed class Probed
        {
            public string FinalUrl = "", FileName = "", Version = "";
            /// <summary>La dirección que trae el archivo directamente: la pedida o, si mostraba una página de espera, la que esa página indica.</summary>
            public string DownloadUrl = "";
            public bool IsMsi, VersionConfirmed;
        }

        static readonly byte[] Ole = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        static readonly Regex MetaRefresh = new Regex(@"<meta\b[^>]*http-equiv\s*=\s*[""']?refresh[""']?[^>]*content\s*=\s*[""'][^""']*?url\s*=\s*(?<u>[^""'>\s]{4,600})", RX);
        static readonly Regex JsRedirect = new Regex(@"(?:window\.|document\.)?location(?:\.href)?\s*(?:=|\.replace\(|\.assign\()\s*[""'](?<u>https?:[^""'<>\s]{4,600})[""']", RX);
        static readonly Regex MirrorChoices = new Regex(@"mirror_choices\?(?<q>[^""'<>\s]{10,600})", RX);
        static readonly Regex SafeSfName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,62}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex SafeSfPath = new Regex(@"^[A-Za-z0-9][A-Za-z0-9 ._+()\-/]{0,400}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// De la página de espera de SourceForge: la dirección del archivo en el servidor espejo que eligió
        /// («https://ESPEJO.dl.sourceforge.net/project/PROYECTO/RUTA?viasf=1»), o null. Sólo nombres y rutas simples.
        /// </summary>
        public static string SourceForgeMirror(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            foreach (Match m in MirrorChoices.Matches(html))
            {
                string project = null, file = null, mirror = null;
                foreach (string pair in WebUtility.HtmlDecode(m.Groups["q"].Value).Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = pair.Substring(0, eq), v;
                    try { v = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' ')); } catch { continue; }
                    if (k == "projectname") project = v; else if (k == "filename") file = v; else if (k == "selected") mirror = v;
                }
                if (project == null || file == null || mirror == null) continue;
                if (!SafeSfName.IsMatch(project) || !SafeSfName.IsMatch(mirror) || mirror.IndexOf('.') >= 0) continue;
                file = file.TrimStart('/');
                if (!SafeSfPath.IsMatch(file) || file.Contains("..") || file.Contains("//")) continue;
                var parts = file.Split('/');
                var esc = new List<string>();
                foreach (string seg in parts) esc.Add(Uri.EscapeDataString(seg));
                return "https://" + mirror.ToLowerInvariant() + ".dl.sourceforge.net/project/" + project + "/" + string.Join("/", esc.ToArray()) + "?viasf=1";
            }
            return null;
        }

        static readonly Regex DispositionName = new Regex(@"filename\*?\s*=\s*(?:UTF-8'')?[""']?(?<n>[^""';]{1,200})", RX);

        /// <summary>
        /// En las pruebas (las páginas están simuladas) nunca se sale a la red: lo que no tenga su propia simulación (manifiestos de
        /// winget, pedidos por partes) no se consulta.
        /// </summary>
        static bool Simulated { get { return SiteVersions.FetchForTests != null; } }

        static Http.RangeResult Range(string url, long from, int count)
        {
            if (OfficialInstaller.RangeForTests != null) return OfficialInstaller.RangeForTests(url, from, count);
            if (Simulated) throw new InvalidOperationException("sin red en las pruebas");
            return Http.GetRange(url, from, count, ProbeTimeoutMs, SiteVersions.IsSecureDownload);
        }

        /// <summary>
        /// Sigue la dirección (cada salto por HTTPS) y pide sólo el principio del archivo: tiene que ser un ejecutable de Windows o un
        /// paquete MSI. Si responde una página de espera («tu descarga empieza en unos segundos»), se sigue una vez el enlace o la
        /// redirección que trae. null y el motivo si no es un instalador.
        /// </summary>
        public static Probed ProbeFile(string url, string latest, SiteVersions.NameMatcher name, DateTime deadlineUtc, out string why)
        {
            return ProbeFile(url, latest, name, deadlineUtc, 0, new HashSet<string>(StringComparer.Ordinal), out why);
        }

        /// <summary>Páginas intermedias que se siguen como mucho («Redirecting…» del fabricante → espera de SourceForge → espejo).</summary>
        public const int MaxHops = 3;

        static Probed ProbeFile(string url, string latest, SiteVersions.NameMatcher name, DateTime deadlineUtc, int depth, HashSet<string> visited, out string why)
        {
            why = null;
            if (!visited.Add(url ?? "")) { why = "la página de descarga vuelve sobre sí misma"; return null; }
            if (DateTime.UtcNow > deadlineUtc) { why = "se agotó el tiempo para ubicar el instalador"; return null; }
            Uri start;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out start) || !SiteVersions.IsSecureDownload(start)) { why = "el instalador no se publica por HTTPS"; return null; }
            Http.RangeResult r;
            try { r = Range(url, 0, 4096); }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception ex) { why = Http.Describe(ex); return null; }
            if (r == null || r.Final == null) { why = "sin respuesta"; return null; }
            byte[] h = r.Data ?? new byte[0];
            bool mz = h.Length >= 2 && h[0] == (byte)'M' && h[1] == (byte)'Z';
            bool ole = h.Length >= 8;
            for (int i = 0; ole && i < 8; i++) if (h[i] != Ole[i]) ole = false;
            string file = FileNameOf(r);
            if (!mz && !ole)
            {
                if (depth < MaxHops)
                {
                    // Una página intermedia: la de SourceForge (el mismo archivo, directo) o la de espera del fabricante.
                    string next = SourceForgeDirect(r.Final.AbsoluteUri);
                    if (next == r.Final.AbsoluteUri || visited.Contains(next)) next = null;
                    if (next == null && LooksLikeHtml(h))
                    {
                        try { next = InterstitialTarget(SiteVersions.Fetch(r.Final.AbsoluteUri, new Dictionary<string, string>()), r.Final.AbsoluteUri, name); }
                        catch (OperationCanceledByUserException) { throw; }
                        catch (Exception ex) { Log.Quiet("Página intermedia de descarga", ex); }
                        if (next != null) next = SourceForgeDirect(next);
                        if (next != null && visited.Contains(next)) next = null;
                    }
                    if (next != null)
                    {
                        Log.Info("Instalador: " + r.Final.Host + " respondió una página; se sigue a " + SafeHost(next));
                        var p2 = ProbeFile(next, latest, name, deadlineUtc, depth + 1, visited, out why);
                        if (p2 != null) return p2;
                        why = "«" + r.Final.Host + "» lleva a una página en vez del archivo, y de ahí: " + why;
                        return null;
                    }
                }
                why = "«" + Fmt.Truncate(file.Length > 0 ? file : r.Final.Host, 60) + "» no es un instalador de Windows (" + r.Final.Host + " respondió una página)";
                return null;
            }
            if (r.Total >= 0 && r.Total < 16 * 1024) { why = "«" + Fmt.Truncate(file, 60) + "» es demasiado chico para ser un instalador"; return null; }
            var p = new Probed();
            p.FinalUrl = r.Final.AbsoluteUri;
            p.DownloadUrl = start.AbsoluteUri;
            p.FileName = file;
            p.IsMsi = ole;
            string path;
            try { path = Uri.UnescapeDataString(r.Final.AbsolutePath); } catch { path = r.Final.AbsolutePath; }
            p.VersionConfirmed = NameHasVersion(file, latest) || NameHasVersion(path, latest);
            if (!p.VersionConfirmed && mz && DateTime.UtcNow <= deadlineUtc)
            {
                // La versión de sus recursos (lo que Windows muestra en Propiedades › Detalles), pidiendo sólo los pedazos que hacen falta.
                try
                {
                    string err;
                    var probe = OfficialInstaller.Probe(p.FinalUrl, SiteVersions.IsSecureDownload, null, null, ProbeTimeoutMs, out err);
                    if (probe != null && VersionUtil.Parse(probe.Version) != null)
                    {
                        p.Version = probe.Version;
                        p.VersionConfirmed = VersionUtil.Compare(probe.Version, latest) == 0;
                    }
                }
                catch (OperationCanceledByUserException) { throw; }
                catch (Exception ex) { Log.Quiet("Versión del instalador oficial", ex); }
            }
            return p;
        }

        static string SafeHost(string url)
        {
            try { return new Uri(url).Host; } catch { return "?"; }
        }

        static string FileNameOf(Http.RangeResult r)
        {
            string name = "";
            var m = DispositionName.Match(r.Disposition ?? "");
            if (m.Success) { try { name = Uri.UnescapeDataString(m.Groups["n"].Value.Trim()); } catch { name = m.Groups["n"].Value.Trim(); } }
            if (name.Length == 0 && r.Final != null && r.Final.Segments.Length > 0)
            {
                try { name = Uri.UnescapeDataString(r.Final.Segments[r.Final.Segments.Length - 1]); } catch { name = r.Final.Segments[r.Final.Segments.Length - 1]; }
            }
            int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            return slash >= 0 ? name.Substring(slash + 1) : name;
        }

        static bool LooksLikeHtml(byte[] h)
        {
            string s = Encoding.ASCII.GetString(h, 0, Math.Min(h.Length, 512)).TrimStart('﻿', ' ', '\r', '\n', '\t', '?').ToLowerInvariant();
            return s.StartsWith("<!doctype") || s.StartsWith("<html") || s.StartsWith("<head") || s.StartsWith("<?xml") || s.StartsWith("<meta") || s.StartsWith("<script");
        }

        /// <summary>De una página de espera: la redirección «meta refresh» o el único enlace a un instalador de este programa. null si no hay.</summary>
        public static string InterstitialTarget(string html, string pageUrl, SiteVersions.NameMatcher name)
        {
            Uri page;
            if (string.IsNullOrEmpty(html) || !Uri.TryCreate(pageUrl ?? "", UriKind.Absolute, out page)) return null;
            var m = MetaRefresh.Match(html);
            if (m.Success)
            {
                Uri u;
                if (Uri.TryCreate(page, WebUtility.HtmlDecode(m.Groups["u"].Value), out u) && SiteVersions.IsSecureDownload(u) && !SameResource(u, page)) return u.AbsoluteUri;
            }
            // 2.0.18: la página de espera de SourceForge no tiene ni redirección ni enlace al archivo (lo baja con JavaScript), pero
            // nombra el servidor espejo elegido («mirror_choices?projectname=X&filename=RUTA&selected=ESPEJO»): el archivo está en
            // https://ESPEJO.dl.sourceforge.net/project/X/RUTA (lo mismo que hace el JavaScript de la página).
            string mirror = SourceForgeMirror(html);
            if (mirror != null) return mirror;
            // Una redirección por JavaScript («location.href = "https://…"»).
            foreach (Match j in JsRedirect.Matches(html))
            {
                Uri u;
                if (Uri.TryCreate(page, WebUtility.HtmlDecode(j.Groups["u"].Value.Replace("\\/", "/")), out u) && SiteVersions.IsSecureDownload(u) && !SameResource(u, page) &&
                    (IsInstallerFile(LinkFileName(u)) || SourceForgeFile.IsMatch(u.AbsoluteUri))) return u.AbsoluteUri;
            }
            string only = null;
            foreach (var l in Links(html, pageUrl))
            {
                if (!IsInstallerFile(l.FileName) || BadFile.IsMatch(l.FileName.ToLowerInvariant())) continue;
                if (name != null && !name.InFileName(l.FileName)) continue;
                if (only != null && only != l.Url) return null;      // más de uno: no se adivina
                only = l.Url;
            }
            return only;
        }

        // ------------------------------------------------------------------ Enlaces de una página

        /// <summary>Un enlace de una página, ya resuelto contra su dirección.</summary>
        public sealed class PageLink
        {
            public string Url = "", Text = "", FileName = "";
            public Uri Uri;
        }

        /// <summary>Un enlace que puede llevar al instalador, con qué tan claro es que es el de este programa.</summary>
        public sealed class LinkCandidate
        {
            public string Url = "", FoundAt = "";
            public int Score;
            /// <summary>Termina en .exe o .msi (si no, es una redirección que hay que seguir para saberlo).</summary>
            public bool Direct;
        }

        static readonly Regex AnchorRx = new Regex(@"<a\b[^>]*?\bhref\s*=\s*(?:""(?<u>[^""<>]{1,600})""|'(?<u>[^'<>]{1,600})')[^>]*>(?<t>.{0,300}?)</a>", RX | RegexOptions.Singleline);
        static readonly Regex BadFile = new Regex(@"portable|source|(?<![a-z])src(?![a-z])|debug|symbols|(?<![a-z])beta|(?<![a-z])alpha|[._-]rc\d|nightly|preview|uninst|(?<![a-z])(?:mac|macos|osx|linux|android|ios)(?![a-z])", RX);
        /// <summary>Palabras del nombre de un instalador que no dicen de qué programa es.</summary>
        static readonly HashSet<string> InstallerWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "setup", "install", "installer", "instalador", "download", "descarga", "full", "latest", "release", "final", "win", "windows", "win32", "win64",
            "x64", "x86", "amd64", "x32", "64bit", "32bit", "64", "32", "bit", "bits", "en", "us", "es", "multi", "multilang", "exe", "msi", "redirect", "get", "file", "dl",
            "x", "amd", "arm", "arm64", "aarch64", "v", "ver", "version", "for", "para", "php", "asp", "aspx", "html", "htm", "product", "id", "name", "app"
        };
        /// <summary>Las que también se reconocen pegadas a otra palabra («installershizuku»), de la más larga a la más corta.</summary>
        static readonly string[] GluedWords = { "instalador", "installer", "download", "descarga", "redirect", "install", "windows", "release", "latest", "setup" };
        static readonly Regex InstallerMark = new Regex(@"installer|instalador|setup|install", RX);
        static readonly Regex DownloadMark = new Regex(@"download|descarg|redirect|getfile|get-file|/dl/|/get/", RX);
        static readonly Regex DownloadText = new Regex(@"\bdownloads?\b|descarg|herunterladen|t[ée]l[ée]charg|\binstaller\b|\binstalador\b|\bget it\b|\bobtener\b", RX);

        /// <summary>Los enlaces de una página (http o https), sin repetir, en el orden en que aparecen.</summary>
        public static List<PageLink> Links(string html, string pageUrl)
        {
            var list = new List<PageLink>();
            Uri page;
            if (string.IsNullOrEmpty(html) || !Uri.TryCreate(pageUrl ?? "", UriKind.Absolute, out page)) return list;
            string t = html.Length > VendorCatalog.MaxPage ? html.Substring(0, VendorCatalog.MaxPage) : html;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in AnchorRx.Matches(t))
            {
                string raw = WebUtility.HtmlDecode(m.Groups["u"].Value).Trim();
                if (raw.Length == 0 || raw.StartsWith("#") || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                Uri u;
                if (!Uri.TryCreate(page, raw, out u) || !SiteVersions.IsPublicPage(u)) continue;
                if (!seen.Add(u.AbsoluteUri)) continue;
                var l = new PageLink();
                l.Uri = u;
                l.Url = u.AbsoluteUri;
                l.Text = SiteVersions.Norm(WebUtility.HtmlDecode(VendorCatalog.StripTags(m.Groups["t"].Value)));
                l.FileName = LinkFileName(u);
                list.Add(l);
                if (list.Count >= 2000) break;
            }
            return list;
        }

        /// <summary>
        /// El nombre del archivo al que lleva una dirección: el último tramo o, en las de SourceForge («…/CrystalDiskInfo9_9_5.exe/download»),
        /// el anterior a «download». Vacío si no se puede leer.
        /// </summary>
        public static string LinkFileName(Uri u)
        {
            if (u == null || u.Segments.Length == 0) return "";
            try
            {
                string last = Uri.UnescapeDataString(u.Segments[u.Segments.Length - 1]).TrimEnd('/');
                if (last.Equals("download", StringComparison.OrdinalIgnoreCase) && u.Segments.Length >= 2)
                {
                    string prev = Uri.UnescapeDataString(u.Segments[u.Segments.Length - 2]).TrimEnd('/');
                    if (IsInstallerFile(prev)) return prev;
                }
                return last;
            }
            catch { return ""; }
        }

        static bool IsInstallerFile(string fileName)
        {
            string f = (fileName ?? "").ToLowerInvariant();
            return f.EndsWith(".exe") || f.EndsWith(".msi");
        }

        /// <summary>
        /// Lo que queda del nombre de un enlace después de sacar el programa, la versión y las palabras de instalador: si queda algo
        /// («ads», «shizuku», «pro», «server») es OTRA edición u otro producto, salvo que el programa instalado se llame así.
        /// </summary>
        public static string Leftover(string token, SiteVersions.NameMatcher name, string programName)
        {
            // Palabras: por los separadores y por los cambios de mayúsculas y de letras a números («CrystalDiskInfoInstallerAds», «x64»).
            string s = Regex.Replace(token ?? "", @"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=\d)|(?<=\d)(?=[A-Za-z])", " ");
            var words = new List<string>();
            foreach (string w in Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+")) if (w.Length > 0) words.Add(w);
            string text = "|" + string.Join("|", words.ToArray()) + "|";
            // El nombre del programa, aunque haya quedado partido en palabras.
            var own = new List<string>();
            own.Add(WingetProvider.NameKey(name.Phrase));
            foreach (string t in name.Own) own.Add(WingetProvider.NameKey(t));
            foreach (string t in SiteVersions.Tokens(programName)) own.Add(WingetProvider.NameKey(t));
            own.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
            foreach (string k in own)
            {
                if (k.Length < 3) continue;
                var rx = new StringBuilder();
                foreach (char ch in k) { if (rx.Length > 0) rx.Append(@"\|?"); rx.Append(Regex.Escape(ch.ToString())); }
                text = Regex.Replace(text, rx.ToString(), "|");
            }
            var left = new StringBuilder();
            foreach (string w in text.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Regex.IsMatch(w, @"^\d+$") || InstallerWords.Contains(w)) continue;
                string rest = w;
                foreach (string glued in GluedWords) rest = rest.Replace(glued, "");
                if (rest.Length == 0 || InstallerWords.Contains(rest)) continue;
                left.Append(rest);
            }
            return left.ToString();
        }

        /// <summary>
        /// Enlaces de la página que pueden ser el instalador de este programa, del más claro al menos claro:
        /// un archivo .exe/.msi con el nombre del programa; uno de nombre genérico («setup.exe») si la página es de este programa; y
        /// las redirecciones que nombran el programa («redirect.php?product=CrystalDiskInfoInstaller»). Nunca portables, código
        /// fuente, betas ni otras ediciones del programa («…InstallerShizuku», «…InstallerAds»).
        /// </summary>
        public static List<LinkCandidate> Candidates(string html, string pageUrl, string programName, string publisher, string latest, bool pageIsAbout, string nativeArch)
        {
            var result = new List<LinkCandidate>();
            var name = new SiteVersions.NameMatcher(programName, publisher);
            bool arm = BrowserCatalog.NormalizeArch(nativeArch) == "arm64";
            foreach (var l in Links(html, pageUrl))
            {
                if (!SiteVersions.IsSecureDownload(l.Uri)) continue;
                string f = l.FileName.ToLowerInvariant();
                string whole;
                try { whole = Uri.UnescapeDataString(l.Uri.PathAndQuery); } catch { whole = l.Uri.PathAndQuery; }
                if (BadFile.IsMatch(f)) continue;
                var c = new LinkCandidate();
                c.Url = l.Url;
                c.FoundAt = pageUrl;
                if (IsInstallerFile(f))
                {
                    c.Direct = true;
                    string stem = f.Substring(0, f.Length - 4);
                    if (name.InFileName(f))
                    {
                        // Con algo más en el nombre («…-gpl-setup.msi», «…Shizuku.exe») puede ser otra edición: después del que no lo tiene.
                        c.Score = Leftover(stem, name, programName).Length >= 2 ? 70 : 100;
                    }
                    else if (pageIsAbout && Leftover(stem, name, programName).Length == 0) c.Score = 60;      // «setup.exe», «installer-x64.exe»
                    else continue;
                    if (NameHasVersion(f, latest)) c.Score += 20;
                    if (Regex.IsMatch(f, @"arm64|aarch64")) c.Score += arm ? 6 : -40;
                    else if (Regex.IsMatch(f, @"x64|amd64|win64|64[-_]?bit")) c.Score += 4;
                    if (f.EndsWith(".exe")) c.Score += 1;
                }
                else
                {
                    // Una redirección: algún pedazo de la dirección (un valor de la consulta o un tramo de la ruta) nombra el programa.
                    if (BadFile.IsMatch(whole.ToLowerInvariant())) continue;
                    if (Regex.IsMatch(f, @"\.(?:zip|7z|rar|gz|xz|bz2|tar|tgz|dmg|pkg|deb|rpm|apk|appimage|pdf|txt|md|png|jpe?g|gif|svg|css|js|json|xml|rss|iso|img)$")) continue;
                    string token = NamedToken(l.Uri, name);
                    if (token == null) continue;
                    if (Leftover(token, name, programName).Length >= 2) continue;           // otra edición u otro producto
                    bool inst = InstallerMark.IsMatch(token) || InstallerMark.IsMatch(l.Text);
                    bool down = DownloadMark.IsMatch(whole) || DownloadText.IsMatch(l.Text);
                    if (!inst && !down) continue;
                    c.Score = inst ? 50 : 30;
                    if (NameHasVersion(whole, latest)) c.Score += 20;
                }
                result.Add(c);
            }
            result.Sort(delegate(LinkCandidate a, LinkCandidate b) { return b.Score.CompareTo(a.Score); });
            return result;
        }

        /// <summary>El valor de la consulta o el tramo de la ruta que nombra el programa (el último que lo haga), o null.</summary>
        static string NamedToken(Uri u, SiteVersions.NameMatcher name)
        {
            var tokens = new List<string>();
            foreach (string seg in u.Segments) { string s = seg.Trim('/'); if (s.Length > 0) tokens.Add(s); }
            string q = u.Query.TrimStart('?');
            foreach (string pair in q.Split(new[] { '&', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                tokens.Add(eq >= 0 ? pair.Substring(eq + 1) : pair);
            }
            string found = null;
            foreach (string raw in tokens)
            {
                string t;
                try { t = Uri.UnescapeDataString(raw.Replace('+', ' ')); } catch { t = raw; }
                if (name.InFileName(t)) found = t;
            }
            return found;
        }

        static bool SameResource(Uri a, Uri b)
        {
            return a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.AbsolutePath.TrimEnd('/') == b.AbsolutePath.TrimEnd('/') && a.Query == b.Query;
        }

        /// <summary>El dominio sin «www.» ni el subdominio de idioma o de descargas: crystalmark.info, download.example.com → example.com.</summary>
        public static string SiteOf(Uri u)
        {
            if (u == null) return "";
            string h = u.Host.ToLowerInvariant();
            string[] p = h.Split('.');
            if (p.Length <= 2) return h;
            // «co.uk», «com.ar»: tres tramos.
            bool twoLevel = p[p.Length - 1].Length == 2 && (p[p.Length - 2].Length <= 3 || p[p.Length - 2] == "gov" || p[p.Length - 2] == "edu");
            int keep = twoLevel ? 3 : 2;
            return string.Join(".", p, p.Length - keep, keep);
        }

        /// <summary>
        /// Páginas del mismo sitio que conviene mirar para encontrar el instalador: la de descargas («Download», «Descargas») y la del
        /// producto (su dirección nombra el programa). Como mucho <see cref="MaxSubPages"/>, la más clara primero.
        /// </summary>
        public static List<string> DownloadPages(string html, string pageUrl, string programName, string publisher)
        {
            var result = new List<string>();
            Uri page;
            if (!Uri.TryCreate(pageUrl ?? "", UriKind.Absolute, out page)) return result;
            var name = new SiteVersions.NameMatcher(programName, publisher);
            string site = SiteOf(page);
            var scored = new List<KeyValuePair<int, string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in Links(html, pageUrl))
            {
                if (SiteOf(l.Uri) != site || l.Uri.Scheme != Uri.UriSchemeHttps) continue;
                if (SameResource(l.Uri, page)) continue;
                string path;
                try { path = Uri.UnescapeDataString(l.Uri.AbsolutePath).ToLowerInvariant(); } catch { path = l.Uri.AbsolutePath.ToLowerInvariant(); }
                if (Regex.IsMatch(path, @"\.(?:exe|msi|zip|7z|rar|gz|dmg|pkg|deb|rpm|apk|pdf|png|jpe?g|gif|svg|css|js|xml|rss)$")) continue;
                if (l.Uri.Query.Length > 0 && !DownloadMark.IsMatch(l.Uri.Query)) continue;          // búsquedas, filtros, sesiones
                if (Regex.IsMatch(path, @"/(?:20\d\d|19\d\d)/\d{1,2}/|/blog/|/news/|/forum|/category/|/tag/|/login|/account|/cart|/privacy|/terms|/contact|/about")) continue;
                bool named = name.InFileName(path.Replace('/', ' '));
                bool down = Regex.IsMatch(path, @"download|descarg") || DownloadText.IsMatch(l.Text);
                if (!named && !down) continue;
                int score = (named ? 3 : 0) + (down ? 4 : 0) - path.Split('/').Length / 3;
                string key = l.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
                if (!seen.Add(key)) continue;
                scored.Add(new KeyValuePair<int, string>(score, l.Uri.GetLeftPart(UriPartial.Query)));
            }
            scored.Sort(delegate(KeyValuePair<int, string> a, KeyValuePair<int, string> b) { return b.Key.CompareTo(a.Key); });
            foreach (var kv in scored) { if (result.Count >= MaxSubPages) break; result.Add(kv.Value); }
            return result;
        }

        // ------------------------------------------------------------------ Búsqueda

        /// <summary>
        /// El instalador de la versión <paramref name="latest"/> de <paramref name="p"/>, o null con el motivo. <paramref name="winget"/>: la
        /// fila de winget del programa (si winget lo conoce); <paramref name="html"/>: la página oficial ya leída.
        /// </summary>
        public static InstallerLead Find(UpdateContext ctx, InstalledProgram p, string latest, string pageUrl, string html, WingetRow winget, out string why)
        {
            why = null;
            if (p == null || p.Unregistered || VersionUtil.Parse(latest) == null) { why = "no hay una versión instalada con la cual trabajar"; return null; }
            var deadline = DateTime.UtcNow + Budget;
            string whyWinget = null, whyPage = null;
            InstallerLead lead = null;
            if (winget != null && !winget.IdTruncated && !winget.PortableCopy && WingetProvider.IsSafeId(winget.Id) &&
                (winget.Source.Length == 0 || winget.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)))
            {
                try { lead = FromWinget(ctx, p, latest, winget, deadline, out whyWinget); }
                catch (OperationCanceledByUserException) { throw; }
                catch (Exception ex) { whyWinget = "no se pudo leer su manifiesto en winget-pkgs (" + Http.Describe(ex) + ")"; }
                if (lead == null) Log.Info(p.Name + ": sin instalador por winget-pkgs: " + whyWinget);
            }
            if (lead == null && !string.IsNullOrEmpty(html) && !string.IsNullOrEmpty(pageUrl))
            {
                try { lead = FromPages(ctx, p, latest, pageUrl, html, deadline, out whyPage); }
                catch (OperationCanceledByUserException) { throw; }
                catch (Exception ex) { whyPage = Http.Describe(ex); }
                if (lead == null) Log.Info(p.Name + ": sin instalador en su página: " + whyPage);
            }
            if (lead != null) { Log.Info(p.Name + " " + latest + ": instalador ubicado (" + lead.Origin + "): " + lead.Url); return lead; }
            why = whyPage ?? whyWinget ?? "su página no enlaza un instalador";
            return null;
        }

        /// <summary>Por winget-pkgs: el manifiesto de esa versión o la dirección de la anterior con la versión nueva.</summary>
        public static InstallerLead FromWinget(UpdateContext ctx, InstalledProgram p, string latest, WingetRow w, DateTime deadlineUtc, out string why)
        {
            why = null;
            string id = w.Id;
            WingetManifest m = null;
            bool exact = false;
            if (Simulated && WingetDirect.ManifestForTests == null) { why = "sin red en las pruebas"; return null; }
            Http.EnableModernTls();
            if (WingetRules.IsSafeVersion(latest))
            {
                string text = WingetDirect.TryGetManifest(WingetRules.ManifestUrl(id, latest, false));
                if (text != null) { m = WingetDirect.Validate(WingetManifest.Parse(text), id); exact = true; }
            }
            if (m == null)
            {
                string known = null;
                foreach (string v in new[] { w.Available, w.Version, p.Version })
                    if (!string.IsNullOrEmpty(v) && !v.EndsWith("…") && !v.EndsWith("...") && WingetRules.IsSafeVersion(v.Trim()) && VersionUtil.Parse(v) != null) { known = v.Trim(); break; }
                m = WingetDirect.FetchManifest(id, known);
                int? cmp = VersionUtil.Compare(m.Version, latest);
                if (cmp == 0) exact = true;
                else if (!cmp.HasValue || cmp > 0) { why = "winget-pkgs publica la " + m.Version + ", que no es anterior a la " + latest + " de la página"; return null; }
            }
            string native = ctx != null && ctx.Os != null ? ctx.Os.NativeArchitecture : "";
            bool other;
            var inst = WingetRules.Choose(m, null, native, WingetDirect.InstalledArch(ctx, p), !VendorProvider.IsPerUser(p), WingetDirect.LooksMsi(p), true, null, out why, out other);
            if (inst == null) return null;
            if (inst.IsArchive) { why = "winget lo publica dentro de un archivo ZIP"; return null; }
            string behavior = inst.UpgradeBehavior.ToLowerInvariant();
            if (behavior == "uninstallprevious" || behavior == "deny") { why = "el paquete pide desinstalar la versión anterior antes de instalar, y eso sólo lo hace winget"; return null; }
            if (ctx != null && ctx.IsAdmin && inst.Elevation.Equals("elevationProhibited", StringComparison.OrdinalIgnoreCase)) { why = "el paquete no admite instalarse como administrador"; return null; }

            string url = inst.Url;
            bool templated = false;
            if (!exact)
            {
                string t = TemplateUrl(inst.Url, m.Version, latest);
                if (t != null) { url = t; templated = true; }
            }
            url = SourceForgeDirect(url);
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || !SiteVersions.IsSecureDownload(u)) { why = "el manifiesto no publica el instalador por HTTPS"; return null; }
            var lead = new InstallerLead();
            lead.Url = u.AbsoluteUri;
            lead.Origin = exact ? InstallerLead.Catalog : InstallerLead.Previous;
            lead.WingetId = id;
            lead.IsMsi = inst.IsMsi;
            lead.FileName = Uri.UnescapeDataString(u.Segments.Length > 0 ? u.Segments[u.Segments.Length - 1] : "");
            if (exact)
            {
                if (inst.Sha256.Length != 64) { why = "el manifiesto no trae el SHA-256 del instalador"; return null; }
                lead.Sha256 = inst.Sha256;
                lead.VersionConfirmed = true;
                lead.Winget = inst;
                return lead;
            }
            // La dirección armada tiene que existir y ser un instalador del mismo tipo. Si la dirección no lleva la versión (siempre
            // trae «la última»), el archivo tiene que decir que es de la versión publicada.
            var probed = ProbeFile(lead.Url, latest, new SiteVersions.NameMatcher(p.Name, p.Publisher), deadlineUtc, out why);
            if (probed == null) { why = "no está en la dirección de donde winget baja la versión anterior (" + u.Host + "): " + why; return null; }
            if (probed.IsMsi != inst.IsMsi) { why = "en esa dirección hay otro tipo de instalador que el del manifiesto de winget"; return null; }
            if (!templated && !probed.VersionConfirmed) { why = "la dirección de winget no lleva la versión y el archivo no dice ser de la " + latest; return null; }
            if (probed.FileName.Length > 0) lead.FileName = probed.FileName;
            lead.VersionConfirmed = templated || probed.VersionConfirmed;
            // Mismo tipo e interruptores que el manifiesto; sin su SHA-256 (es de la versión anterior): manda la firma.
            var clone = new WingetInstaller();
            clone.Architecture = inst.Architecture; clone.Type = inst.Type; clone.NestedType = inst.NestedType; clone.Scope = inst.Scope; clone.Locale = inst.Locale;
            clone.Url = lead.Url; clone.Sha256 = ""; clone.UpgradeBehavior = inst.UpgradeBehavior; clone.Elevation = inst.Elevation; clone.MinOs = inst.MinOs;
            foreach (var kv in inst.Switches) clone.Switches[kv.Key] = kv.Value;
            clone.SuccessCodes.AddRange(inst.SuccessCodes);
            clone.InstallModes.AddRange(inst.InstallModes);
            foreach (var kv in inst.ReturnCodes) clone.ReturnCodes[kv.Key] = kv.Value;
            lead.Winget = clone;
            return lead;
        }

        /// <summary>Por la página oficial: sus enlaces y los de su página de descargas (o la del producto).</summary>
        public static InstallerLead FromPages(UpdateContext ctx, InstalledProgram p, string latest, string pageUrl, string html, DateTime deadlineUtc, out string why)
        {
            why = null;
            string native = ctx != null && ctx.Os != null ? ctx.Os.NativeArchitecture : "";
            var name = new SiteVersions.NameMatcher(p.Name, p.Publisher);
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var all = new List<LinkCandidate>();
            all.AddRange(Candidates(html, pageUrl, p.Name, p.Publisher, latest, SiteVersions.PageIsAbout(html, pageUrl, name), native));
            // Con un enlace directo al archivo de esa versión ya alcanza; si no, se mira la página de descargas.
            bool clear = all.Exists(delegate(LinkCandidate c) { return c.Direct && c.Score >= 120; });
            if (!clear)
                foreach (string sub in DownloadPages(html, pageUrl, p.Name, p.Publisher))
                {
                    if (DateTime.UtcNow > deadlineUtc || (ctx != null && ctx.CancelRequested)) break;
                    string h;
                    try { h = SiteVersions.Fetch(sub, cache); }
                    catch (OperationCanceledByUserException) { throw; }
                    catch (Exception ex) { Log.Quiet("Página de descargas", ex); continue; }
                    foreach (var c in Candidates(h, sub, p.Name, p.Publisher, latest, SiteVersions.PageIsAbout(h, sub, name) || SiteVersions.PageIsAbout(html, pageUrl, name), native))
                        if (!all.Exists(delegate(LinkCandidate x) { return x.Url == c.Url; })) all.Add(c);
                }
            if (all.Count == 0) { why = "su página no enlaza un instalador de este programa"; return null; }
            all.Sort(delegate(LinkCandidate a, LinkCandidate b) { return b.Score.CompareTo(a.Score); });
            int probes = 0;
            string last = null;
            foreach (var c in all)
            {
                if (probes >= MaxProbes || DateTime.UtcNow > deadlineUtc || (ctx != null && ctx.CancelRequested)) break;
                probes++;
                string w;
                var probed = ProbeFile(SourceForgeDirect(c.Url), latest, name, deadlineUtc, out w);
                if (probed == null) { last = w; continue; }
                string f = probed.FileName.ToLowerInvariant();
                if (BadFile.IsMatch(f)) { last = "«" + Fmt.Truncate(probed.FileName, 60) + "» no es el instalador normal del programa"; continue; }
                // Una redirección puede terminar en cualquier archivo: el nombre final tiene que ser de este programa (o genérico) y de esta edición.
                if (!c.Direct && IsInstallerFile(f))
                {
                    string stem = f.Substring(0, f.Length - 4);
                    if (Leftover(stem, name, p.Name).Length >= 2 && !name.InFileName(f)) { last = "«" + Fmt.Truncate(probed.FileName, 60) + "» no parece el instalador de este programa"; continue; }
                }
                // Si el archivo dice otra versión del mismo producto que la publicada, no es el de esta versión.
                if (!probed.VersionConfirmed && VersionUtil.Parse(probed.Version) != null && LooksLikeProductVersion(probed.Version, latest, p.Version) &&
                    VersionUtil.IsNewer(latest, probed.Version))
                { last = "«" + Fmt.Truncate(probed.FileName, 60) + "» es de la " + probed.Version + ", no de la " + latest; continue; }
                var lead = new InstallerLead();
                // La dirección del enlace; si pasaba por una página de espera y la directa es fija (sin datos de sesión), la directa.
                lead.Url = SourceForgeDirect(c.Url);
                if (probed.DownloadUrl.Length > 0 && probed.DownloadUrl != lead.Url && probed.DownloadUrl.IndexOf('?') < 0) lead.Url = probed.DownloadUrl;
                lead.Origin = InstallerLead.Page;
                lead.FoundAt = c.FoundAt;
                lead.FileName = probed.FileName;
                lead.IsMsi = probed.IsMsi;
                lead.VersionConfirmed = probed.VersionConfirmed;
                return lead;
            }
            why = last ?? "no se pudo ubicar el instalador en su página";
            return null;
        }

        /// <summary>¿La versión del archivo usa la numeración del producto? (misma primera parte que la publicada o la instalada)</summary>
        public static bool LooksLikeProductVersion(string fileVersion, string latest, string installed)
        {
            long[] f = VersionUtil.Parse(fileVersion), l = VersionUtil.Parse(latest), i = VersionUtil.Parse(installed);
            if (f == null || l == null) return false;
            return f[0] == l[0] || (i != null && f[0] == i[0]);
        }
    }
}
