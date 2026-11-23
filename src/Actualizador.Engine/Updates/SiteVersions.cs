// La versión publicada en la página oficial de cada programa, para lo que winget no cubre.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    /// <summary>Lo que se leyó en la página de un programa.</summary>
    public sealed class SiteReading
    {
        /// <summary>La versión más nueva que publica la página para ese programa.</summary>
        public string Latest;
        /// <summary>La página nombra exactamente la versión instalada.</summary>
        public bool InstalledSeen;
        /// <summary>El texto alrededor de la versión, para mostrar de dónde salió.</summary>
        public string Evidence = "";
        public string Url = "";
        /// <summary>Enlace de la página a un instalador de ESTE programa (.exe o .msi, por HTTPS), o null.</summary>
        public string InstallerUrl;
        /// <summary>El nombre del instalador trae la versión publicada: es el de esa versión.</summary>
        public bool InstallerHasVersion;
    }

    /// <summary>
    /// 2.0.18: lo que hace falta para volver a buscar el instalador al tocar «Actualizar» (una fila que en la búsqueda no lo encontró
    /// y abriría su página): el programa, lo leído en su página, la fila de winget (si la había) y por qué no se encontró.
    /// </summary>
    public sealed class SitePlan
    {
        public InstalledProgram Program;
        public SiteReading Reading;
        public bool Strict;
        public WingetRow Winget;
        public string Why;
    }

    /// <summary>Páginas oficiales que indicó el usuario para un programa (fuentes.json).</summary>
    public sealed class SourceList
    {
        public readonly Dictionary<string, string> ByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public const int Max = 1000;

        public static string DefaultPath { get { return Path.Combine(AppInfo.DataDirectory, "fuentes.json"); } }

        public static SourceList Load() { return Load(DefaultPath); }

        public static SourceList Load(string path)
        {
            var l = new SourceList();
            try
            {
                if (!File.Exists(path)) return l;
                object o = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                foreach (object x in Json.List(o, "fuentes"))
                {
                    string name = (Json.Str(x, "programa") ?? "").Trim();
                    string url = Fmt.WebUrl(Json.Str(x, "pagina"));
                    if (name.Length == 0 || name.Length > 300 || url == null || l.ByName.Count >= Max) continue;
                    l.ByName[SiteVersions.ProgramKey(name)] = url;
                }
            }
            catch (Exception ex) { Log.Warn("No se pudo leer " + path + ": " + ex.Message); }
            return l;
        }

        /// <summary>La página indicada para el programa (por su nombre sin versión ni arquitectura), o null.</summary>
        public string Find(string programName)
        {
            string url;
            return ByName.TryGetValue(SiteVersions.ProgramKey(programName), out url) ? url : null;
        }

        /// <summary>Guarda la página (o la quita, con null). false si la dirección no es una página web válida.</summary>
        public bool Set(string programName, string url)
        {
            string key = SiteVersions.ProgramKey(programName);
            if (key.Length == 0) return false;
            if (url == null) { ByName.Remove(key); return true; }
            string u = Fmt.WebUrl(url);
            if (u == null) return false;
            ByName[key] = u;
            return true;
        }

        public bool Save() { return Save(DefaultPath); }

        public bool Save(string path)
        {
            try
            {
                if (!SafePaths.EnsureDataDirectory(Path.GetDirectoryName(path))) return false;
                var items = new List<object>();
                foreach (var kv in ByName)
                {
                    var d = new Dictionary<string, object>();
                    d["programa"] = kv.Key;
                    d["pagina"] = kv.Value;
                    items.Add(d);
                }
                var root = new Dictionary<string, object>();
                root["fuentes"] = items;
                AtomicFile.WriteAllText(path, Json.Serialize(root, true), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) { Log.Warn("No se pudo guardar " + path + ": " + ex.Message); return false; }
        }
    }

    /// <summary>
    /// Para cada programa que no se pudo comparar (no está en winget ni en el catálogo de fabricantes) se lee la página
    /// oficial que el propio programa registra en Windows (URLUpdateInfo, URLInfoAbout, HelpLink) o la que indicó el usuario
    /// (clic derecho › «Indicar la página oficial…»), y se busca la versión que publica: un número con la forma de la versión
    /// instalada, junto al nombre del programa o a «versión», «descarga», «última»… Y para los que winget da «Al día», si su
    /// página publica una más nueva, el catálogo de winget va atrasado (GOM Player) y se dice.
    /// Es una lectura automática: la fila dice de qué página salió y nunca instala nada (se descarga de esa página).
    /// </summary>
    public static class SiteVersions
    {
        /// <summary>Sólo para las pruebas: reemplaza la descarga de páginas (dirección → HTML).</summary>
        internal static Func<string, string> FetchForTests;
        /// <summary>Sólo para las pruebas: la lista de páginas indicadas por el usuario.</summary>
        internal static SourceList SourcesForTests;

        public const int MaxPages = 200;
        /// <summary>Plazo de cada página oficial.</summary>
        public const int PageTimeoutMs = 12000;
        static int Workers { get { return Performance.Workers(6, 2); } }   // 2.0.21: 2 en modo liviano
        static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);
        const int MaxText = 1500000;

        const RegexOptions RX = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
        static readonly Regex VersionRx = new Regex(@"(?<![\w.,/-])[vV]?(?<v>\d{1,4}(?:\.\d{1,6}){1,3})(?![\w%]|\.\d|,\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex KeywordBefore = new Regex(@"(?:\bver(?:si[oó]n|sion)?\b|\bv\.?$|\brelease\b|\bbuild\b|\bdownload\b|\bdescarg|\blatest\b|[uú]ltima|\bactual\b|\bcurrent\b|\bstable\b|\bestable\b|\bupdate\b|\bactualizaci[oó]n\b|\bnew\b|\bnueva\b|\bchangelog\b|\bwhat'?s new\b|\bnovedades\b)[^.\n]{0,25}$", RX);
        static readonly Regex OsBefore = new Regex(@"(?:windows|win|\.net|net framework|android|ios|ipados|macos|mac os x|os x|linux|ubuntu|debian|chrome|firefox|edge|safari|directx|opengl|vulkan|java|python|php|jquery|bootstrap|wordpress|node|tls|ssl|http|usb|bluetooth|wi-?fi|pci|sata|hdmi|ddr|gb|mb|kb|ghz|mhz|copyright|©)\s*[:v]?\s*$", RX);
        /// <summary>
        /// Programas que se instalan uno al lado del otro por serie: «Python 3.13» y «Python 3.14» son dos programas, y
        /// la 3.14.10 de python.org no es una actualización de la 3.13.16 (pasó en el equipo del usuario).
        /// </summary>
        static readonly Regex SideBySide = new Regex(@"^\s*(?:python|postgresql|mysql(?: server)?|mariadb|php|node\.?js|java|jre|jdk|eclipse temurin|amazon corretto|microsoft \.net|\.net (?:runtime|sdk|desktop runtime)|asp\.net core|perl|ruby|go programming language|r for windows|qt)\b", RX);

        /// <summary>¿Se instala por serie (major.minor) al lado de las otras? Sólo entonces una versión de otra serie no es una actualización.</summary>
        public static bool IsSideBySide(string programName) { return SideBySide.IsMatch(programName ?? ""); }

        static readonly Regex DateLike = new Regex(@"^(?:[0-3]?\d\.[01]?\d\.(?:19|20)\d\d|(?:19|20)\d\d\.[01]?\d\.[0-3]?\d)$", RX);
        static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "for", "and", "con", "para", "software", "windows", "microsoft", "edition", "free", "gratis", "setup", "version", "versión",
            "update", "client", "cliente", "app", "application", "aplicación", "program", "programa", "tool", "tools", "suite", "plus", "pro",
            "professional", "home", "lite", "x64", "x86", "64", "32", "bit", "inc", "ltd", "llc", "corporation", "corp", "company", "gmbh"
        };

        /// <summary>Clave del programa: su nombre sin versión ni arquitectura, sólo letras y dígitos.</summary>
        public static string ProgramKey(string name) { return WingetProvider.NameKey(WingetProvider.SearchName(name)); }

        /// <summary>Palabras del nombre que lo identifican en una página («GOM Player» → gom, player; «TightVNC» → tightvnc).</summary>
        public static List<string> Tokens(string programName)
        {
            var list = new List<string>();
            foreach (string w in Regex.Split(WingetProvider.SearchName(programName ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}+]+"))
                if (w.Length >= 3 && !Generic.Contains(w) && !list.Contains(w)) list.Add(w);
            return list;
        }

        /// <summary>Texto visible de una página: sin scripts, estilos ni etiquetas, con las entidades decodificadas.</summary>
        public static string VisibleText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string t = html.Length > VendorCatalog.MaxPage ? html.Substring(0, VendorCatalog.MaxPage) : html;
            t = VendorCatalog.StripBlocks(VendorCatalog.StripBlocks(VendorCatalog.StripBlocks(t, "script"), "style"), "noscript");
            t = VendorCatalog.StripTags(t);
            t = WebUtility.HtmlDecode(t);
            t = Regex.Replace(t, @"[ \t ]+", " ");
            t = Regex.Replace(t, @"\s*\n\s*", "\n");
            return t.Length > MaxText ? t.Substring(0, MaxText) : t;
        }

        /// <summary>Minúsculas, sólo letras y dígitos separados por un espacio.</summary>
        public static string Norm(string s)
        {
            var sb = new StringBuilder();
            bool space = true;
            foreach (char ch in (s ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch) || ch == '+') { sb.Append(ch); space = false; }
                else if (!space) { sb.Append(' '); space = true; }
            }
            return sb.ToString().Trim();
        }

        static readonly HashSet<string> WeakWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "player", "studio", "viewer", "manager", "editor", "browser", "office", "reader", "center", "centre", "driver", "drivers", "audio",
            "video", "media", "cleaner", "converter", "control", "panel", "service", "services", "launcher", "installer", "utility", "utilities",
            "security", "antivirus", "backup", "cloud", "drive", "photo", "music", "master", "assistant", "connect", "link", "sync", "remote", "desktop"
        };

        /// <summary>
        /// Cómo se reconoce el programa en una página: la frase completa del nombre (sin versión ni arquitectura) o, si el nombre
        /// tiene palabras propias además de la marca, todas ellas. La marca del fabricante («AMD» de Advanced Micro Devices,
        /// «GOM» de GOM &amp; Company) sola no alcanza: en amd.com, «AMD FSR 4.1» no es «AMD Ryzen Master 4.1».
        /// </summary>
        public sealed class NameMatcher
        {
            public string Phrase = "";
            public readonly List<string> Own = new List<string>();

            public NameMatcher(string programName, string publisher)
            {
                Phrase = Norm(WingetProvider.SearchName(programName));
                var pub = new List<string>(Norm(WingetRules.NormalizeCompany(publisher ?? "")).Split(' '));
                pub.AddRange(Norm(publisher ?? "").Split(' '));
                // Iniciales del editor sin la forma jurídica: «Advanced Micro Devices, Inc.» → «amd».
                var initials = new StringBuilder();
                foreach (string w in Norm(WingetRules.NormalizeCompany(publisher ?? "")).Split(' ')) if (w.Length > 0) initials.Append(w[0]);
                string acro = initials.ToString();
                foreach (string t in Tokens(programName))
                {
                    if (pub.Contains(t)) continue;
                    if (acro.Length >= 2 && t == acro) continue;   // «AMD» de Advanced Micro Devices
                    if (!Own.Contains(t)) Own.Add(t);
                }
            }

            /// <summary>¿El texto (ya normalizado) nombra el programa?</summary>
            public bool In(string norm)
            {
                if (string.IsNullOrEmpty(norm)) return false;
                if (Phrase.Length >= 3 && (" " + norm + " ").Contains(" " + Phrase + " ")) return true;
                if (Own.Count == 0) return false;
                foreach (string t in Own) if (!(" " + norm + " ").Contains(" " + t + " ")) return false;
                if (Own.Count >= 2) return true;
                // Una sola palabra propia: sólo si es larga y no es genérica («Ryzen» sí; «Player» no).
                return Own[0].Length >= 5 && !WeakWords.Contains(Own[0]);
            }

            /// <summary>¿El nombre de archivo de un instalador es de este programa? («TightVNC-2.8.85-gpl-setup-64bit.msi»)</summary>
            public bool InFileName(string fileName)
            {
                string key = WingetProvider.NameKey(fileName);
                string phraseKey = WingetProvider.NameKey(Phrase);
                if (phraseKey.Length >= 4 && key.Contains(phraseKey)) return true;
                if (Own.Count == 0) return false;
                foreach (string t in Own) if (!key.Contains(WingetProvider.NameKey(t))) return false;
                return Own.Count >= 2 || (Own[0].Length >= 5 && !WeakWords.Contains(Own[0]));
            }
        }

        static readonly Regex TitleRx = new Regex(@"<title[^>]*>(?<t>[^<]{0,300})</title>|<h1[^>]*>(?<t>.{0,300}?)</h1>", RX | RegexOptions.Singleline);

        /// <summary>¿La página es de este programa? (su título, su encabezado o su dirección lo nombran).</summary>
        public static bool PageIsAbout(string html, string pageUrl, NameMatcher name)
        {
            var sb = new StringBuilder();
            foreach (Match m in TitleRx.Matches(html ?? "")) { sb.Append(' ').Append(VendorCatalog.StripTags(m.Groups["t"].Value)); if (sb.Length > 2000) break; }
            try { if (!string.IsNullOrEmpty(pageUrl)) sb.Append(' ').Append(Uri.UnescapeDataString(new Uri(pageUrl).AbsolutePath)); } catch { }
            return name.In(Norm(WebUtility.HtmlDecode(sb.ToString())));
        }

        /// <summary>Compatibilidad: sin editor ni dirección.</summary>
        public static SiteReading Evaluate(string html, string programName, string installed, bool strict)
        {
            return Evaluate(html, programName, "", installed, strict, false, null);
        }

        /// <summary>
        /// La versión que publica la página para el programa, o null si no se reconoce con seguridad. Se acepta un número de 2 a
        /// 4 partes que no sea de Windows, .NET, un navegador, un tamaño, una fecha ni una beta, y:
        /// - con el nombre del programa pegado (60 caracteres antes o 20 después): de la misma serie mayor o hasta 3 más;
        /// - con el nombre cerca (150 caracteres): de la misma serie mayor;
        /// - sin el nombre, junto a «versión», «descarga», «última»…: sólo si la página es de este programa (su título, su
        ///   encabezado o su dirección lo nombran, o la indicó el usuario) y de la misma serie mayor.
        /// Con <paramref name="strict"/> (winget ya lo da al día) sólo cuenta el nombre pegado y la misma serie mayor.
        /// </summary>
        public static SiteReading Evaluate(string html, string programName, string publisher, string installed, bool strict, bool userPage, string pageUrl)
        {
            long[] inst = VersionUtil.Parse(installed);
            if (inst == null || inst.Length < 2) return null;
            string text = VisibleText(html);
            if (text.Length == 0) return null;
            string lower = text.ToLowerInvariant();
            var name = new NameMatcher(programName, publisher);
            if (name.Phrase.Length < 2 && name.Own.Count == 0) return null;
            bool about = userPage || PageIsAbout(html, pageUrl, name);
            bool seriesPinned = IsSideBySide(programName);
            var reading = new SiteReading();
            foreach (Match m in VersionRx.Matches(text))
            {
                string v = m.Groups["v"].Value;
                long[] p = VersionUtil.Parse(v);
                if (p == null || p.Length < 2) continue;
                if (DateLike.IsMatch(v) && !DateLike.IsMatch(VersionUtil.Normalize(installed))) continue;
                int start = Math.Max(0, m.Index - 150);
                string before = lower.Substring(start, m.Index - start);
                string beforeShort = before.Length > 20 ? before.Substring(before.Length - 20) : before;
                // «Windows 11», «Python 3.8», «.NET 4.8»: no son la versión del programa… salvo que esa palabra sea el programa
                // mismo («Python 3.13.17» en python.org).
                var os = OsBefore.Match(beforeShort);
                if (os.Success)
                {
                    string word = Norm(os.Value);
                    if (word.Length == 0 || !(" " + name.Phrase + " ").Contains(" " + word + " ")) continue;
                }
                int endAfter = Math.Min(lower.Length, m.Index + m.Length + 40);
                string after = lower.Substring(m.Index + m.Length, endAfter - m.Index - m.Length);
                if (Regex.IsMatch(after, @"^\s*(?:%|mb|gb|kb|ghz|mhz|px|x\d|€|\$|usd|ars)\b", RegexOptions.IgnoreCase)) continue;
                // Versiones de prueba («4.0.0 beta 2», «2.9-rc1»): no son la última estable.
                if (Regex.IsMatch(after, @"^[\s\-_.(]{0,3}(?:alpha|beta|rc\d*|preview|pre-?release|nightly|dev|insider|canary)\b", RegexOptions.IgnoreCase)) continue;
                string before60 = before.Length > 60 ? before.Substring(before.Length - 60) : before;
                string after20 = after.Length > 20 ? after.Substring(0, 20) : after;
                bool closeName = name.In(Norm(before60 + " " + after20));
                bool nearName = closeName || name.In(Norm(before + " " + after));
                bool keyword = about && (KeywordBefore.IsMatch(before.Length > 45 ? before.Substring(before.Length - 45) : before) || m.Value.StartsWith("v", StringComparison.OrdinalIgnoreCase));
                long major = p[0];
                if (major < inst[0]) continue;
                // Misma serie (major.minor): para lo que winget ya da al día (su paquete es de esa serie: Python.Python.3.13) y para
                // los programas que se instalan por serie.
                bool sameMinor = p.Length >= 2 && inst.Length >= 2 && major == inst[0] && p[1] == inst[1];
                if ((strict || seriesPinned) && !sameMinor) continue;
                bool ok;
                if (strict) ok = closeName && major == inst[0];
                else if (closeName) ok = major <= inst[0] + 3;
                else if (nearName || keyword) ok = major == inst[0];
                else ok = false;
                if (!ok) continue;
                if (VersionUtil.Compare(v, installed) == 0) reading.InstalledSeen = true;
                if (reading.Latest == null || VersionUtil.IsNewer(v, reading.Latest))
                {
                    reading.Latest = v;
                    int es = Math.Max(0, m.Index - 60);
                    reading.Evidence = text.Substring(es, Math.Min(text.Length - es, m.Index - es + m.Length + 30)).Replace("\n", " ").Trim();
                }
            }
            if (reading.Latest == null) return null;
            if (!VersionUtil.IsNewer(reading.Latest, installed))
            {
                // Sólo números más viejos o iguales: «al día» únicamente si la página nombra la instalada o la misma serie.
                long[] l = VersionUtil.Parse(reading.Latest);
                bool sameSeries = l.Length >= 2 && l[0] == inst[0] && l[1] == inst[1];
                if (!reading.InstalledSeen && !sameSeries) return null;
            }
            else if (!string.IsNullOrEmpty(pageUrl))
            {
                bool hasVersion;
                reading.InstallerUrl = FindInstaller(html, pageUrl, name, reading.Latest, out hasVersion);
                reading.InstallerHasVersion = hasVersion;
            }
            return reading;
        }

        static readonly Regex HrefRx = new Regex(@"href\s*=\s*[""'](?<u>[^""'<>\s]{4,600}?\.(?:exe|msi))(?:[?#][^""'<>\s]{0,300})?[""']", RX);

        /// <summary>
        /// Un enlace de la página al instalador de ESTE programa: .exe o .msi, por HTTPS, con el nombre del programa en el nombre
        /// del archivo, sin portables, betas ni código fuente. Prefiere el que trae la versión publicada y la arquitectura del
        /// equipo. null si no hay uno claro.
        /// </summary>
        public static string FindInstaller(string html, string pageUrl, NameMatcher name, string latest, out bool hasVersion)
        {
            hasVersion = false;
            Uri page;
            if (string.IsNullOrEmpty(html) || !Uri.TryCreate(pageUrl ?? "", UriKind.Absolute, out page)) return null;
            string best = null;
            int bestScore = int.MinValue;
            string vDots = VersionUtil.Normalize(latest) ?? "";
            string vKey = vDots.Replace(".", "");
            foreach (Match m in HrefRx.Matches(html))
            {
                Uri u;
                if (!Uri.TryCreate(page, WebUtility.HtmlDecode(m.Groups["u"].Value), out u) || !IsSecureDownload(u)) continue;
                string file = Uri.UnescapeDataString(u.Segments.Length > 0 ? u.Segments[u.Segments.Length - 1] : "");
                if (!name.InFileName(file)) continue;
                string f = file.ToLowerInvariant();
                if (Regex.IsMatch(f, @"portable|source|src[._-]|debug|symbols|beta|alpha|[._-]rc\d|nightly|preview|uninst")) continue;
                int score = 0;
                bool withVersion = vDots.Length > 0 && (f.Contains(vDots) || f.Contains(vDots.Replace('.', '_')) || f.Contains(vDots.Replace('.', '-')) || (vKey.Length >= 3 && WingetProvider.NameKey(f).Contains(vKey)));
                if (withVersion) score += 4;
                if (Regex.IsMatch(f, @"arm64|aarch64")) score -= 6;
                if (Regex.IsMatch(f, @"x64|amd64|win64|64[-_]?bit")) score += 1;
                if (score > bestScore) { best = u.AbsoluteUri; bestScore = score; hasVersion = withVersion; }
            }
            return best;
        }

        /// <summary>Sólo para las pruebas: nombre de producto del instalador descargado.</summary>
        internal static Func<string, string> ProductOfForTests;

        /// <summary>
        /// null si el instalador descargado es de este programa (o no dice de qué producto es); si dice otro producto, el
        /// motivo para no instalarlo: el fabricante firma también otros programas (AMD Software y AMD Ryzen Master).
        /// </summary>
        public static string ForeignInstaller(string file, InstalledProgram p)
        {
            string product = ProductOf(file);
            if (product.Length == 0 || Regex.IsMatch(product, @"^(?:setup|installer|instalador|nullsoft.*|inno setup.*)$", RegexOptions.IgnoreCase)) return null;
            var name = new NameMatcher(p.Name, p.Publisher);
            if (name.In(Norm(product)) || name.InFileName(product)) return null;
            return "el instalador de su página es de «" + Fmt.Truncate(product, 60) + "», no de «" + Fmt.Truncate(p.Name, 60) + "»: no se instala";
        }

        /// <summary>github.com y su almacenamiento: no son la página oficial «propia» de un programa para el contraste con GitHub.</summary>
        public static bool IsGitHubHost(Uri u)
        {
            if (u == null || !u.IsAbsoluteUri) return false;
            string h = u.Host.ToLowerInvariant();
            return h == "github.com" || h.EndsWith(".github.com") || h.EndsWith(".githubusercontent.com");
        }

        static Uri SafeUri(string url)
        {
            Uri u;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) ? u : null;
        }

        /// <summary>El producto que dice el instalador (nombre del producto o, si no, su descripción); vacío si no dice.</summary>
        public static string ProductOf(string file)
        {
            string product = "";
            try
            {
                if (ProductOfForTests != null) product = ProductOfForTests(file) ?? "";
                else
                {
                    var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(file);
                    product = !string.IsNullOrEmpty(v.ProductName) ? v.ProductName : (v.FileDescription ?? "");
                }
            }
            catch (Exception ex) { Log.Quiet("Producto del instalador", ex); }
            return product.Trim();
        }

        /// <summary>Descarga de un instalador: sólo HTTPS a una página pública.</summary>
        public static bool IsSecureDownload(Uri u)
        {
            return u != null && u.IsAbsoluteUri && u.Scheme == Uri.UriSchemeHttps && IsPublicPage(u);
        }

        /// <summary>Página web pública: HTTPS o HTTP, puerto estándar, con un nombre (no una dirección IP ni «localhost»).</summary>
        public static bool IsPublicPage(Uri u)
        {
            if (u == null || !u.IsAbsoluteUri || !u.IsDefaultPort) return false;
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return false;
            if (u.HostNameType != UriHostNameType.Dns) return false;
            string h = u.Host.ToLowerInvariant();
            return h.IndexOf('.') > 0 && h != "localhost" && !h.EndsWith(".local") && !h.EndsWith(".lan") && !h.EndsWith(".internal");
        }

        /// <summary>Las páginas a leer: la que indicó el usuario primero; después las que registra el programa.</summary>
        public static List<string> PagesFor(InstalledProgram p, SourceList user)
        {
            var list = new List<string>();
            string mine = user != null && p != null ? user.Find(p.Name) : null;
            foreach (string raw in new[] { mine, p != null ? p.UpdateUrl : null, p != null ? p.InfoUrl : null, p != null ? p.HelpLink : null })
            {
                string u = Fmt.WebUrl(raw);
                Uri parsed;
                if (u == null || !Uri.TryCreate(u, UriKind.Absolute, out parsed) || !IsPublicPage(parsed)) continue;
                if (!list.Contains(u)) list.Add(u);
            }
            return list;
        }

        static InstalledProgram FindProgram(List<InstalledProgram> programs, string name)
        {
            if (programs == null || string.IsNullOrEmpty(name)) return null;
            string n = VendorProvider.BaseName(name);
            foreach (var p in programs) if (p != null && p.Name != null && p.Name.Trim().Equals(n, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        sealed class Job
        {
            public UpdateCandidate Row;
            public InstalledProgram Program;
            public List<string> Pages;
            public bool Strict;
            public SiteReading Reading;
            public string Error;
            /// <summary>2.0.17: el instalador de la versión publicada, si se pudo ubicar; si no, por qué.</summary>
            public InstallerLead Lead;
            public string LeadWhy;
        }

        /// <summary>
        /// 2.0.17: cuando la página publica una versión nueva, buscar su instalador (winget-pkgs, enlaces de la página y de su página de
        /// descargas) para instalarla desde acá. Apagado, las filas sin un enlace directo abren su página, como hasta la 2.0.16.
        /// </summary>
        public static bool FindInstallers = true;

        /// <summary>
        /// Lee las páginas oficiales de los programas sin verificar (y de los que winget da «Al día», para ver si su catálogo
        /// va atrasado) y actualiza sus filas. Devuelve cuántas filas cambiaron.
        /// </summary>
        public static int Resolve(UpdateContext ctx, List<UpdateCandidate> all, List<InstalledProgram> programs, List<string> notes)
        {
            if (all == null || programs == null) return 0;
            if (ctx.Options != null && !ctx.Options.QueryVendors) return 0;
            var user = SourcesForTests ?? SourceList.Load();
            var jobs = new List<Job>();
            foreach (var c in all)
            {
                if (c == null || c.Kind != UpdateKind.Programa) continue;
                bool unverified = c.Status == UpdateStatus.NoVerificable;
                bool wingetOk = c.Status == UpdateStatus.AlDia && c.ProviderName == WingetProvider.ProviderId && !(c.ProviderTag is WingetRow && ((WingetRow)c.ProviderTag).InstallInsteadOfUpgrade);
                if (!unverified && !wingetOk) continue;
                var p = FindProgram(programs, c.Name);
                if (p == null) continue;
                if (unverified && ProgramHints.For(p) != null && !p.Unregistered && user.Find(p.Name) == null) continue;   // Steam, antivirus, actualizador propio: ya tienen explicación
                string installed = VersionUtil.Parse(p.Version) != null ? p.Version : c.InstalledVersion;
                if (VersionUtil.Parse(installed) == null || VersionUtil.Parse(installed).Length < 2) continue;
                var pages = PagesFor(p, user);
                if (pages.Count == 0) continue;
                jobs.Add(new Job { Row = c, Program = p, Pages = pages, Strict = !unverified && user.Find(p.Name) == null });
            }
            if (jobs.Count == 0) return 0;
            // Primero los sin verificar (lo que se pidió); después la comprobación de los «Al día» de winget.
            jobs.Sort(delegate(Job a, Job b) { return a.Strict.CompareTo(b.Strict); });
            int skipped = Math.Max(0, jobs.Count - MaxPages);
            if (skipped > 0) jobs.RemoveRange(MaxPages, skipped);
            ctx.Report("Leyendo la versión en la página oficial de " + Fmt.Plural(jobs.Count, "programa", "programas") + "…");

            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var deadline = DateTime.UtcNow + Budget;
            int next = -1, done = 0;
            var threads = new List<Thread>();
            for (int t = 0; t < Math.Min(Workers, jobs.Count); t++)
            {
                var th = new Thread(delegate()
                {
                    ProcessRunner.LowPriorityForThisThread = true;
                    while (true)
                    {
                        int i = Interlocked.Increment(ref next);
                        if (i >= jobs.Count || ctx.CancelRequested || DateTime.UtcNow > deadline) return;
                        var job = jobs[i];
                        string pageHtml = null;
                        foreach (string url in job.Pages)
                        {
                            try
                            {
                                string html = Fetch(url, cache);
                                bool mine = url.Equals(user.Find(job.Program.Name) ?? "", StringComparison.OrdinalIgnoreCase);
                                var r = Evaluate(html, job.Program.Name, job.Program.Publisher, VersionUtil.Parse(job.Program.Version) != null ? job.Program.Version : job.Row.InstalledVersion,
                                                 job.Strict && !mine, mine, url);
                                if (r != null) { r.Url = url; job.Reading = r; pageHtml = html; break; }
                            }
                            catch (Exception ex) { job.Error = Http.Describe(ex); }
                        }
                        // 2.0.17: con una versión nueva, se ubica su instalador para instalarla desde acá (antes se abría la página).
                        // Si winget conoce el programa, primero por winget-pkgs (mismos interruptores y, si ya está, su SHA-256).
                        if (FindInstallers && job.Reading != null && !job.Program.Unregistered && !ctx.CancelRequested &&
                            VersionUtil.IsNewer(job.Reading.Latest, VersionUtil.Parse(job.Program.Version) != null ? job.Program.Version : job.Row.InstalledVersion))
                        {
                            var winget = job.Row.ProviderTag as WingetRow;
                            if (job.Reading.InstallerUrl == null || winget != null)
                            {
                                try
                                {
                                    string why;
                                    // Con el enlace directo ya a la vista en la página, sólo se mejora con lo que diga winget-pkgs.
                                    job.Lead = SiteInstallers.Find(ctx, job.Program, job.Reading.Latest, job.Reading.Url, job.Reading.InstallerUrl == null ? pageHtml : null, winget, out why);
                                    job.LeadWhy = why;
                                }
                                catch (OperationCanceledByUserException) { }
                                catch (Exception ex) { job.LeadWhy = Http.Describe(ex); Log.Quiet("Instalador de la página oficial", ex); }
                            }
                        }
                        int n = Interlocked.Increment(ref done);
                        if (n % 10 == 0) ctx.Report("Páginas oficiales: " + n + " de " + jobs.Count + " leídas…");
                    }
                });
                th.IsBackground = true;
                th.Name = "paginas-" + t;
                threads.Add(th);
                th.Start();
            }
            foreach (var th in threads) while (!th.Join(500)) if (ctx.CancelRequested) break;
            ctx.ThrowIfCancelled();

            int newer = 0, current = 0, lagging = 0;
            foreach (var job in jobs)
            {
                if (job.Reading == null) continue;
                if (Apply(ctx, job.Row, job.Program, job.Reading, job.Strict, job.Lead, job.LeadWhy))
                {
                    if (job.Strict) lagging++;
                    else if (job.Row.Status == UpdateStatus.Disponible) newer++;
                    else current++;
                }
            }
            if (newer + current > 0)
                notes.Add("Páginas oficiales: " + Fmt.Plural(newer + current, "programa sin otra fuente se comparó", "programas sin otra fuente se compararon") + " con la versión que publica su sitio (" +
                          Fmt.Plural(current, "al día", "al día") + ", " + Fmt.Plural(newer, "con versión nueva", "con versión nueva") + "). Es una lectura automática: cada fila dice de qué página salió.");
            if (lagging > 0)
                notes.Add(Fmt.Plural(lagging, "programa que winget daba «al día» tiene", "programas que winget daba «al día» tienen") + " una versión más nueva en su sitio oficial: el catálogo de winget va atrasado.");
            if (skipped > 0) notes.Add(Fmt.Plural(skipped, "página quedó", "páginas quedaron") + " sin leer esta vez (como mucho " + MaxPages + " por búsqueda).");
            return newer + current + lagging;
        }

        /// <summary>Compatibilidad (sin contexto): sin instalador, la fila abre la página.</summary>
        public static bool Apply(UpdateCandidate c, InstalledProgram p, SiteReading r, bool strict)
        {
            return Apply(null, c, p, r, strict);
        }

        /// <summary>
        /// Cambia la fila según lo leído. true si cambió. Con versión nueva:
        /// - si la página enlaza el instalador de este programa (por HTTPS), la fila se instala con él, verificado como los de
        ///   GitHub: firma válida, no revocada y del MISMO firmante que el programa instalado, y que sea de este programa;
        /// - si no, la fila igual se puede marcar: «Actualizar marcadas» abre su página de descarga (1.7.4; antes no se
        ///   podía marcar y parecía un error).
        /// </summary>
        public static bool Apply(UpdateContext ctx, UpdateCandidate c, InstalledProgram p, SiteReading r, bool strict)
        {
            return Apply(ctx, c, p, r, strict, null, null);
        }

        /// <summary>
        /// Igual; 2.0.17: <paramref name="lead"/> es el instalador que se ubicó para esa versión (<see cref="SiteInstallers"/>) cuando la
        /// página no lo enlaza a la vista: la fila se instala con él en vez de abrir la página. <paramref name="leadWhy"/>: si no se
        /// ubicó, por qué (queda en el detalle).
        /// </summary>
        public static bool Apply(UpdateContext ctx, UpdateCandidate c, InstalledProgram p, SiteReading r, bool strict, InstallerLead lead, string leadWhy)
        {
            var sitePlan = c.ProviderTag as SitePlan;
            var wingetRow = c.ProviderTag as WingetRow ?? (sitePlan != null ? sitePlan.Winget : null);
            string host;
            try { host = new Uri(r.Url).Host; } catch { host = r.Url; }
            string evidence = r.Evidence.Length > 0 ? " Dice: «…" + Fmt.Truncate(r.Evidence, 140) + "…»." : "";
            bool isNewer = VersionUtil.IsNewer(r.Latest, VersionUtil.Parse(p.Version) != null ? p.Version : c.InstalledVersion);
            // 2.0.12: lo que publica la página oficial queda anotado: el contraste con GitHub no ofrece una versión que la página
            // oficial todavía no publica (7-Zip 26.06 en GitHub con 7-zip.org en la 26.05).
            if (!IsGitHubHost(SafeUri(r.Url)) && VersionUtil.Parse(r.Latest) != null)
            {
                c.OfficialVersion = r.Latest;
                c.OfficialPage = r.Url;
            }
            // Ya estaba «Al día» según winget: sólo se cambia si el sitio publica una más nueva.
            if (strict && !isNewer) return false;
            c.AvailableVersion = r.Latest;
            c.Source = "Sitio oficial (" + host + ")";
            c.InfoUrl = r.Url;
            if (!isNewer)
            {
                c.Status = UpdateStatus.AlDia;
                c.Installable = false;
                c.Recommended = false;
                c.Detail = "Coincide con la versión que publica su página oficial (" + host + ")." + evidence;
                return true;
            }
            c.Status = UpdateStatus.Disponible;
            c.Identifier = "sitio:" + ProgramKey(p.Name);
            string leadText = strict ? "winget la daba al día, pero su página oficial publica la " + r.Latest + ": el catálogo de winget va atrasado."
                                     : "Su página oficial publica la " + r.Latest + " (leído automáticamente de " + host + ").";
            if ((r.InstallerUrl != null || lead != null) && ctx != null && !p.Unregistered)
            {
                var plan = new VendorPlan();
                plan.Programs.Add(p);
                plan.CoveredNames.Add(p.Name.Trim());
                plan.InstallerUrl = lead != null ? lead.Url : r.InstallerUrl;
                plan.Lead = lead;
                if (lead != null && lead.Sha256.Length == 64) plan.InstallerSha256 = lead.Sha256;
                plan.Latest = r.Latest;
                plan.SitePage = r.Url;
                plan.InstallDir = VendorProvider.InstallDirOf(p);
                plan.Machine = !VendorProvider.IsPerUser(p);
                plan.Arch = VendorProvider.ArchitectureOf(ctx, p);
                string main = ProgramRules.MainExe(p);
                if (main.Length > 0)
                {
                    string exe = ShortcutPrograms.WinFile(main);
                    plan.Processes = new[] { exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe };
                }
                c.ProviderName = VendorProvider.ProviderId;
                c.ProviderTag = plan;
                string file = lead != null && lead.FileName.Length > 0 ? lead.FileName
                            : Uri.UnescapeDataString(new Uri(plan.InstallerUrl).Segments[new Uri(plan.InstallerUrl).Segments.Length - 1]);
                bool needsAdmin = plan.Machine && (plan.InstallDir.Length == 0 || !SafePaths.IsUserWritableLocation(plan.InstallDir));
                if (lead != null && lead.Winget != null && (lead.Winget.Scope == "machine" || lead.Winget.Elevation.Equals("elevationRequired", StringComparison.OrdinalIgnoreCase))) needsAdmin = true;
                if (needsAdmin && !ctx.IsAdmin)
                {
                    c.Installable = false;
                    c.Recommended = false;
                    c.Detail = leadText + evidence + " Su página tiene el instalador («" + file + "»), pero instalarlo para todo el equipo requiere ejecutar Actualizador como administrador.";
                    return true;
                }
                c.Installable = true;
                if (lead != null)
                {
                    // 2.0.17: se instala desde acá. Entra en «Marcar todo» si el archivo es, comprobado, el de esa versión.
                    c.Recommended = lead.VersionConfirmed;
                    c.Detail = leadText + evidence + " " + lead.Describe() + " Si no se pudiera verificar, se abre su página de descarga." +
                               (c.Recommended ? "" : " El archivo no dice la versión: queda entre las opcionales.");
                    return true;
                }
                // entra en «Actualizar todo» (el pedido: todo de una vez). Lo que winget daba al día sólo si el archivo trae la versión.
                c.Recommended = !strict || r.InstallerHasVersion;
                c.Detail = leadText + evidence + " Su página tiene el instalador («" + file + "»): se descarga por HTTPS y sólo se instala si su firma digital es válida, " +
                           "del mismo fabricante que el programa instalado, y es de este programa." + (c.Recommended ? "" : " El nombre del archivo no dice la versión: queda entre las opcionales.");
                return true;
            }
            c.ProviderName = SiteProvider.ProviderId;
            c.ProviderTag = p.Unregistered ? null : new SitePlan { Program = p, Reading = r, Strict = strict, Winget = wingetRow, Why = leadWhy };
            c.Installable = true;
            // también entra en «Actualizar todo»: se abre su página de descarga (no hay otra forma de bajarla).
            c.Recommended = !strict;
            c.Detail = leadText + evidence + " Su página no enlaza un instalador que se pueda verificar" +
                       (string.IsNullOrEmpty(leadWhy) ? "" : " (" + Fmt.Truncate(leadWhy, 160) + ")") + ": al actualizar se abre su página de descarga " +
                       "(también con clic derecho › «Abrir sitio del fabricante»).";
            return true;
        }

        /// <summary>Lee una página y busca la versión (para la página que indica el usuario). null con el motivo en <paramref name="error"/>.</summary>
        public static SiteReading ReadPage(string url, string programName, string publisher, string installed, out string error)
        {
            error = null;
            Uri u;
            string w = Fmt.WebUrl(url);
            if (w == null || !Uri.TryCreate(w, UriKind.Absolute, out u) || !IsPublicPage(u)) { error = "no es la dirección de una página web pública"; return null; }
            if (VersionUtil.Parse(installed) == null) { error = "no se conoce la versión instalada"; return null; }
            try
            {
                var r = Evaluate(Fetch(w, new Dictionary<string, string>()), programName, publisher, installed, false, true, w);
                if (r == null) { error = "la página no muestra una versión reconocible de «" + programName + "» (puede armarse con JavaScript: probá con la página de descargas o de novedades)"; return null; }
                r.Url = w;
                return r;
            }
            catch (Exception ex) { error = Http.Describe(ex); return null; }
        }

        internal static string Fetch(string url, Dictionary<string, string> cache)
        {
            lock (cache) { string c; if (cache.TryGetValue(url, out c)) return c; }
            // 12 s (antes 20): una página que no contesta no debe demorar la búsqueda entera (con su reintento eran 40 s).
            string html = FetchForTests != null ? FetchForTests(url)
                        : RecentPages.Get("text/html " + url, delegate { return Http.GetString(url, PageTimeoutMs, IsPublicPage, "text/html"); });
            lock (cache) cache[url] = html ?? "";
            return html ?? "";
        }
    }
}
