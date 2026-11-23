using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    /// <summary>Una fila de resultados del Catálogo de Microsoft Update.</summary>
    public sealed class CatalogEntry
    {
        public string Guid = "", Title = "", Products = "", Classification = "", Version = "", Size = "";
        public long SizeBytes;
        public DateTime? LastUpdated;
        /// <summary>Se encontró buscando el identificador sin el modelo del fabricante del equipo (sin SUBSYS).</summary>
        public bool Generic;
    }

    /// <summary>Un archivo del diálogo de descarga del catálogo.</summary>
    public sealed class CatalogFile
    {
        public string Url = "", Digest = "", FileName = "", Architectures = "";
    }

    /// <summary>Lo que hace falta para instalar una fila del catálogo.</summary>
    public sealed class CatalogPlan
    {
        public DriverInfo Driver;
        /// <summary>El dispositivo usa el genérico de Windows y esto es el del fabricante (se confirma por la versión exacta).</summary>
        public bool ReplacesInbox;
        public CatalogEntry Entry;
        public string Arch = "";
        /// <summary>
        /// Windows va a preferir el controlador instalado: es el del fabricante del equipo para este modelo
        /// exacto (su identificador incluye SUBSYS) y esta versión se encontró sólo por el chip. Sólo se
        /// instala forzada, si el usuario lo acepta.
        /// </summary>
        public bool PreferOem;
        /// <summary>El usuario aceptó instalarla forzada (reemplaza la del fabricante del equipo).</summary>
        public bool Force;
        /// <summary>
        /// 2.0.26: sólo se puede instalar forzada (el del fabricante del dispositivo en lugar del genérico de Windows, que Windows ya no
        /// aplicó una vez). Es lo único que la ventana de confirmación pregunta si forzar.
        /// </summary>
        public bool ForceOnly;
        /// <summary>Con qué identificador eligió Windows el controlador instalado (del registro).</summary>
        public string InstalledMatch = "";
    }

    /// <summary>
    /// Reglas puras (sin red ni Windows) para leer el Catálogo de Microsoft Update, la misma base que
    /// usan los «actualizadores de controladores» como PC HelpSoft («Certificados por Microsoft»): ahí
    /// están todos los controladores firmados por Microsoft (WHQL), también los que Windows Update no
    /// le ofrece a este equipo porque el fabricante los destinó a otros modelos o como opcionales.
    /// La página no tiene API: se lee el HTML por los identificadores de cada celda, que son estables
    /// («{guid}_C1_R0»), no por clases de estilo, que cambiaron con los años.
    /// </summary>
    public static class CatalogRules
    {
        public const string Host = "www.catalog.update.microsoft.com";
        const RegexOptions RX = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        static readonly Regex RowRx = new Regex(@"<tr\s+id=""(?<g>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})_R(?<r>\d+)""", RX);
        static readonly Regex TagRx = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        static readonly Regex SpaceRx = new Regex(@"\s+", RegexOptions.Compiled);
        static readonly Regex GuidRx = new Regex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RX);
        static readonly Regex FileFieldRx = new Regex(@"downloadInformation\[(?<u>\d+)\]\.files\[(?<f>\d+)\]\.(?<k>url|digest|fileName|architectures)\s*=\s*'(?<v>[^']*)'", RX);
        static readonly Regex DialogIdRx = new Regex(@"downloadInformation\[0\]\.updateID\s*=\s*'(?<v>[^']*)'", RX);
        static readonly Regex ArchRx = new Regex(@"ScopedViewHandler_labelArchitecture_Separator[^>]*>.*?</span>(?<v>.*?)</div>", RX | RegexOptions.Singleline);
        static readonly Regex RevRx = new Regex(@"&REV_[0-9A-F]+$", RX);
        static readonly Regex SubsysRx = new Regex(@"&SUBSYS_[0-9A-F]+", RX);

        public static bool IsCatalogPage(Uri u)
        {
            return Http.IsHttps(u) && u.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Servidores de descarga de Microsoft (siempre por HTTPS).</summary>
        public static bool IsDownloadHost(Uri u)
        {
            if (!Http.IsHttps(u)) return false;
            string h = u.Host.ToLowerInvariant();
            return h == "download.windowsupdate.com" || h.EndsWith(".download.windowsupdate.com") || h.EndsWith(".dl.delivery.mp.microsoft.com");
        }

        public static string SearchUrl(string query)
        {
            return "https://" + Host + "/Search.aspx?q=" + Uri.EscapeDataString(query ?? "");
        }

        public static string DetailsUrl(string guid)
        {
            return GuidRx.IsMatch(guid ?? "") ? "https://" + Host + "/ScopedViewInline.aspx?updateid=" + guid : null;
        }

        public const string DownloadDialogUrl = "https://" + Host + "/DownloadDialog.aspx";

        /// <summary>Cuerpo del POST del diálogo de descarga (el mismo que arma el sitio).</summary>
        public static string DownloadDialogBody(string guid)
        {
            if (!GuidRx.IsMatch(guid ?? "")) throw new ArgumentException("identificador de actualización no válido");
            string json = "[{\"size\":0,\"languages\":\"\",\"uidInfo\":\"" + guid + "\",\"updateID\":\"" + guid + "\"}]";
            return "updateIDs=" + Uri.EscapeDataString(json);
        }

        static string CellText(string html)
        {
            string t = TagRx.Replace(html ?? "", " ");
            t = WebUtility.HtmlDecode(t);
            return SpaceRx.Replace(t, " ").Trim();
        }

        /// <summary>El contenido de la celda «{guid}_C{n}_R{r}» dentro del texto de su fila ("" si no está).</summary>
        static string RowCell(string row, string guid, int column, string r)
        {
            string id = "id=\"" + guid + "_C" + column + "_R" + r + "\"";
            int at = row.IndexOf(id, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return "";
            int start = row.IndexOf('>', at + id.Length);
            if (start < 0) return "";
            start++;
            int end = row.IndexOf("</td>", start, StringComparison.OrdinalIgnoreCase);
            return end < 0 ? "" : row.Substring(start, end - start);
        }

        /// <summary>El texto de un elemento con ese id hasta la etiqueta siguiente (null si no está).</summary>
        static string Span(string row, string id)
        {
            string key = "id=\"" + id + "\"";
            int at = row.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            int start = row.IndexOf('>', at + key.Length);
            if (start < 0) return null;
            start++;
            int end = row.IndexOf('<', start);
            return end < 0 ? null : row.Substring(start, end - start);
        }

        /// <summary>
        /// Filas de una página de resultados. <paramref name="noResults"/>: la página dice que no hay
        /// resultados; <paramref name="complete"/>: la página es una respuesta válida (con tabla o con
        /// «no hay resultados»). El catálogo a veces devuelve páginas a medias o su página de error.
        /// </summary>
        public static List<CatalogEntry> ParseSearch(string html, out bool noResults, out bool complete)
        {
            var list = new List<CatalogEntry>();
            html = html ?? "";
            noResults = html.IndexOf("ctl00_catalogBody_noResultText", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        html.IndexOf("We did not find any results", StringComparison.OrdinalIgnoreCase) >= 0;
            bool table = html.IndexOf("ctl00_catalogBody_updateMatches", StringComparison.OrdinalIgnoreCase) >= 0;
            bool error = html.IndexOf("errorPageDisplayedError", StringComparison.OrdinalIgnoreCase) >= 0;
            complete = !error && (noResults || table);
            if (!table || noResults) return list;
            // 2.0.25: cada fila se lee dentro de su propio <tr> (antes, cada celda buscaba con una expresión nueva en la página ENTERA:
            // unas 7 pasadas completas por fila, 175 por página de 25 filas; en un equipo viejo, varios segundos de procesador por búsqueda).
            var rows = RowRx.Matches(html);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.Count; i++)
            {
                Match m = rows[i];
                string g = m.Groups["g"].Value.ToLowerInvariant(), r = m.Groups["r"].Value;
                if (!seen.Add(g)) continue;
                int from = m.Index;
                int to = i + 1 < rows.Count ? rows[i + 1].Index : html.Length;
                int close = html.IndexOf("</tr>", from, StringComparison.OrdinalIgnoreCase);
                if (close >= 0 && close + 5 < to) to = close + 5;
                string row = html.Substring(from, to - from);
                string raw = m.Groups["g"].Value;
                var e = new CatalogEntry();
                e.Guid = g;
                e.Title = CellText(RowCell(row, raw, 1, r));
                e.Products = CellText(RowCell(row, raw, 2, r));
                e.Classification = CellText(RowCell(row, raw, 3, r));
                e.LastUpdated = ParseUsDate(CellText(RowCell(row, raw, 4, r)));
                e.Version = CellText(RowCell(row, raw, 5, r));
                string size = Span(row, raw + "_size");
                if (size != null) e.Size = CellText(size);
                string orig = Span(row, raw + "_originalSize");
                long bytes;
                if (orig != null && long.TryParse(orig.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out bytes)) e.SizeBytes = bytes;
                if (e.Title.Length > 0) list.Add(e);
            }
            return list;
        }

        /// <summary>«9/11/2018» (el catálogo responde en inglés de EE. UU. porque se le pide así).</summary>
        public static DateTime? ParseUsDate(string s)
        {
            DateTime d;
            if (DateTime.TryParseExact((s ?? "").Trim(), new[] { "M/d/yyyy", "MM/dd/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            return null;
        }

        /// <summary>Archivos del diálogo de descarga, con las direcciones pasadas a HTTPS. Vacío si no corresponde a <paramref name="guid"/>.</summary>
        public static List<CatalogFile> ParseDownloadDialog(string html, string guid)
        {
            var files = new List<CatalogFile>();
            if (string.IsNullOrEmpty(html)) return files;
            var id = DialogIdRx.Match(html);
            if (guid != null && (!id.Success || !id.Groups["v"].Value.Equals(guid, StringComparison.OrdinalIgnoreCase))) return files;
            var map = new SortedDictionary<int, CatalogFile>();
            foreach (Match m in FileFieldRx.Matches(html))
            {
                if (m.Groups["u"].Value != "0") continue;                 // sólo la actualización pedida
                int f;
                if (!int.TryParse(m.Groups["f"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out f)) continue;   // número desbordado: no es del sitio
                CatalogFile cf;
                if (!map.TryGetValue(f, out cf)) { cf = new CatalogFile(); map[f] = cf; }
                string v = WebUtility.HtmlDecode(m.Groups["v"].Value).Trim();
                switch (m.Groups["k"].Value.ToLowerInvariant())
                {
                    case "url": cf.Url = NormalizeDownloadUrl(v); break;
                    case "digest": cf.Digest = v; break;
                    case "filename": cf.FileName = v; break;
                    case "architectures": cf.Architectures = v; break;
                }
            }
            foreach (var cf in map.Values) if (cf.Url.Length > 0) files.Add(cf);
            return files;
        }

        /// <summary>
        /// http://download.windowsupdate.com/… → https://catalog.s.download.windowsupdate.com/… y todo a
        /// HTTPS. "" si no es un servidor de descarga de Microsoft.
        /// </summary>
        public static string NormalizeDownloadUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            string u = Regex.Replace(url.Trim(), @"^https?://(?:www\.)?download\.windowsupdate\.com", "https://catalog.s.download.windowsupdate.com", RegexOptions.IgnoreCase);
            u = Regex.Replace(u, @"^http://", "https://", RegexOptions.IgnoreCase);
            Uri parsed;
            return Uri.TryCreate(u, UriKind.Absolute, out parsed) && IsDownloadHost(parsed) ? parsed.AbsoluteUri : "";
        }

        /// <summary>Arquitecturas de la página de detalle («AMD64», «X86, AMD64»…) como «x64,x86»; "" si no se informa.</summary>
        public static string ParseArchitecture(string detailsHtml)
        {
            if (string.IsNullOrEmpty(detailsHtml)) return "";
            var m = ArchRx.Match(detailsHtml);
            return m.Success ? NormalizeArchList(CellText(m.Groups["v"].Value)) : "";
        }

        public static string NormalizeArchList(string text)
        {
            var found = new List<string>();
            foreach (string raw in Regex.Split(text ?? "", @"[\s,;/]+"))
            {
                string t = raw.Trim().ToLowerInvariant(), a = null;
                if (t == "amd64" || t == "x64" || t == "x86_64") a = "x64";
                else if (t == "x86" || t == "i386") a = "x86";
                else if (t == "arm64") a = "arm64";
                else if (t == "ia64") a = "ia64";
                else if (t == "arm") a = "arm";
                if (a != null && !found.Contains(a)) found.Add(a);
            }
            return string.Join(",", found.ToArray());
        }

        /// <summary>¿Sirve para la arquitectura del equipo? Sin datos, sí (Windows lo verifica al instalar).</summary>
        public static bool ArchMatches(string archList, string native)
        {
            if (string.IsNullOrEmpty(archList)) return true;
            string n = BrowserCatalog.NormalizeArch(native);
            if (n.Length == 0) n = "x64";
            foreach (string a in archList.Split(',')) if (a == n) return true;
            return false;
        }

        /// <summary>
        /// Búsquedas para un identificador de hardware, de la más específica a la más general: tal cual,
        /// sin la revisión (REV) y sin el modelo del fabricante del equipo (SUBSYS). La segunda bool
        /// dice si es general (encuentra controladores de otros fabricantes de equipos).
        /// </summary>
        public static List<KeyValuePair<string, bool>> QueriesFor(string hardwareId)
        {
            var list = new List<KeyValuePair<string, bool>>();
            string h = (hardwareId ?? "").Trim();
            if (h.Length < 5 || h.Length > 200 || h.IndexOf('\\') <= 0) return list;
            // Con llaves («BTHENUM\{0000110b-…}_VID&…», «{5d624f94-…}\vwifimp_wfd»: Bluetooth y dispositivos de
            // software) el catálogo responde una página EN BLANCO, sin tabla ni «no hay resultados» (verificado el
            // 12/10/2026). Esos identificadores no se publican ahí: antes cada uno sumaba un «el catálogo no
            // devolvió una respuesta válida» («No se pudo consultar el catálogo para 3 controladores»).
            if (h.IndexOf('{') >= 0 || h.IndexOf('}') >= 0) return list;
            list.Add(new KeyValuePair<string, bool>(h, false));
            string noRev = RevRx.Replace(h, "");
            if (!noRev.Equals(h, StringComparison.OrdinalIgnoreCase)) list.Add(new KeyValuePair<string, bool>(noRev, false));
            // Los códecs de audio (HDAUDIO) se publican por modelo de equipo: buscando sólo por el chip
            // aparecen paquetes de otros fabricantes (Realtek para Clevo) que Windows nunca aplica.
            if (h.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase)) return list;
            string generic = SubsysRx.Replace(noRev, "");
            if (!generic.Equals(noRev, StringComparison.OrdinalIgnoreCase) && generic.IndexOf('&') > 0) list.Add(new KeyValuePair<string, bool>(generic, true));
            return list;
        }

        // ------------------------------------------------------------------ Productos (versión de Windows)

        static readonly Regex Win11Rx = new Regex(@"Windows 11(?: Client)?(?: S)?(?:,? version (?<v>\d{2}H\d|\d{4}))?", RX);
        static readonly Regex Win10Rx = new Regex(@"Windows 10(?: S)?(?:,? version (?<v>\d{2}H\d|\d{4}))?(?: (?<n>Anniversary Update|Creators Update|Fall Creators Update|April 2018 Update|October 2018 Update|May 2019 Update|November 2019 Update|May 2020 Update))?", RX);
        static readonly Regex ClientRx = new Regex(@"Windows - Client(?: S)?,? version (?<v>\d{2}H\d|\d{4})", RX);
        static readonly Regex ServerRx = new Regex(@"Windows Server", RX);

        static int Win10Build(string v, string name)
        {
            int? b = WindowsReleases.Windows10Build(v);
            if (b.HasValue) return b.Value;
            switch ((name ?? "").ToLowerInvariant())
            {
                case "anniversary update": return 14393;
                case "creators update": return 15063;
                case "fall creators update": return 16299;
                case "april 2018 update": return 17134;
                case "october 2018 update": return 17763;
                case "may 2019 update": return 18362;
                case "november 2019 update": return 18363;
                case "may 2020 update": return 19041;
            }
            return 10240;
        }

        /// <summary>Compilación mínima de Windows 11 para una versión. Sin versión: la primera (22000).
        /// Una versión que este programa todavía no conoce se toma como más nueva que el equipo: es mejor no
        /// ofrecer un controlador que ofrecer uno para un Windows que no es éste.</summary>
        static int Win11Build(string v)
        {
            if (string.IsNullOrEmpty((v ?? "").Trim())) return 22000;
            int? b = WindowsReleases.Windows11Build(v);
            return b.HasValue ? b.Value : int.MaxValue;
        }

        static bool IsWin10Version(string v) { return WindowsReleases.Windows10Build(v).HasValue; }

        static readonly Regex ServerYearRx = new Regex(@"Windows Server,? (?:version )?(?<y>2008 R2|2008|2012 R2|2012|2016|2019|2022|2025|1709|1803|1809|1903|1909|2004|20H2)(?<later>[^,;]{0,40}?\band later\b)?", RX);

        static int ServerBuild(string y)
        {
            switch ((y ?? "").ToUpperInvariant())
            {
                case "2008": return 6002;
                case "2008 R2": return 7601;
                case "2012": return 9200;
                case "2012 R2": return 9600;
                case "2016": return 14393;
                case "2019": return 17763;
                case "2022": return 20348;
                case "2025": return 26100;
                default: return Win10Build(y, null);       // semianuales: versión de Windows 10
            }
        }

        /// <summary>
        /// ¿Un producto de Windows Server corresponde a este servidor? «… 2019 and later» desde la 17763; uno sin
        /// «and later», sólo esa versión (un controlador de Windows Server 2008 R2 no sirve en 2022). Un
        /// «Windows Server» sin año: sí, como antes.
        /// </summary>
        static bool ServerApplies(string products, int build)
        {
            bool any = false;
            foreach (Match m in ServerYearRx.Matches(products))
            {
                any = true;
                int min = ServerBuild(m.Groups["y"].Value);
                if (build < min) continue;
                if (m.Groups["later"].Success || ServerBuildOf(build) == min) return true;
            }
            return !any && ServerRx.IsMatch(products);
        }

        static int ServerBuildOf(int build)
        {
            int[] known = { 26100, 20348, 17763, 14393, 9600, 9200, 7601, 6002 };
            foreach (int k in known) if (build >= k) return k;
            return build;
        }

        /// <summary>
        /// ¿Alguno de los productos de la fila corresponde a este Windows? «Windows 10 and later drivers»
        /// sirve desde la 10240; «Windows 11 Client, version 24H2 and later…» desde la 26100. Las de
        /// Windows 7/8 y las de Windows Server (en un Windows de escritorio) no.
        /// </summary>
        public static bool AppliesTo(string products, int build, bool server)
        {
            if (string.IsNullOrEmpty(products) || build <= 0) return false;
            bool any = false;
            int min = int.MaxValue;
            foreach (Match m in Win11Rx.Matches(products)) { any = true; min = Math.Min(min, Win11Build(m.Groups["v"].Value)); }
            foreach (Match m in Win10Rx.Matches(products)) { any = true; min = Math.Min(min, Win10Build(m.Groups["v"].Value, m.Groups["n"].Value)); }
            foreach (Match m in ClientRx.Matches(products))
            {
                // «Windows - Client, version 21H2/22H2»: el mismo nombre de versión existe en 10 y en 11; se toma el de 10.
                // Las que no son de Windows 10 (23H2, 24H2, 25H2, 26H1…) son de Windows 11.
                any = true;
                string v = m.Groups["v"].Value;
                min = Math.Min(min, IsWin10Version(v) ? Win10Build(v, null) : Win11Build(v));
            }
            if (server && ServerApplies(products, build)) return true;
            return any && build >= min;
        }

        /// <summary>Firmware (BIOS/UEFI): nunca desde el catálogo, que lo publica por modelo de equipo.</summary>
        public static bool IsFirmware(CatalogEntry e, DriverInfo d)
        {
            if (d != null && ((d.DeviceClass ?? "").Equals("Firmware", StringComparison.OrdinalIgnoreCase) || (d.HardwareId ?? "").StartsWith("UEFI\\", StringComparison.OrdinalIgnoreCase)))
                return true;
            return e != null && ((e.Classification ?? "").IndexOf("Firmware", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 Regex.IsMatch(e.Title ?? "", @"\bFirmware\b", RegexOptions.IgnoreCase));
        }

        static readonly Regex TitleClassRx = new Regex(@"^\s*[^-].*?\s-\s(?<c>[^-]+?)\s-\s", RX);

        /// <summary>
        /// La clase del controlador según el título del catálogo («Advanced Micro Devices, Inc. - Extension - 31.0.21921.1000» →
        /// «Extension»), o "" si el título no tiene esa forma.
        /// </summary>
        public static string TitleClass(string title)
        {
            var m = TitleClassRx.Match(title ?? "");
            return m.Success ? m.Groups["c"].Value.Trim() : "";
        }

        static bool IsComponentClass(string cls)
        {
            string c = (cls ?? "").Replace(" ", "").Trim();
            return c.Equals("Extension", StringComparison.OrdinalIgnoreCase) || c.Equals("SoftwareComponent", StringComparison.OrdinalIgnoreCase) ||
                   c.Equals("ComponentSoftware", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 2.0.25: ¿la fila es un COMPLEMENTO (INF de extensión o componente de software) y no el controlador del dispositivo? AMD e Intel
        /// publican para el mismo identificador de la placa de video el controlador («- Display -») y sus extensiones («- Extension -»),
        /// a veces con un número más alto. Se elegía la extensión: pnputil la instalaba «en el dispositivo», el controlador seguía siendo
        /// el mismo, la fila decía «se usa después de reiniciar» y después de reiniciar seguía igual (pedido del 23/11/2026). Un dispositivo
        /// que ES una extensión o un componente sí los compara.
        /// </summary>
        public static bool IsComponentFor(CatalogEntry e, DriverInfo installed)
        {
            if (e == null || !IsComponentClass(TitleClass(e.Title))) return false;
            return installed == null || !IsComponentClass(installed.DeviceClass);
        }

        /// <summary>
        /// Las filas más nuevas que la instalada y válidas para este Windows, de la mejor a la peor
        /// (versión, después fecha de publicación). Las específicas (con SUBSYS) van antes que las
        /// generales de la misma versión.
        /// </summary>
        public static List<CatalogEntry> NewerCandidates(List<CatalogEntry> entries, DriverInfo installed, int build, bool server)
        {
            var list = new List<CatalogEntry>();
            if (entries == null || installed == null || VersionUtil.Parse(installed.Version) == null) return list;
            foreach (var e in entries)
            {
                if (VersionUtil.Parse(e.Version) == null || IsFirmware(e, installed) || !AppliesTo(e.Products, build, server)) continue;
                if (IsComponentFor(e, installed)) continue;
                if (!VersionUtil.IsNewer(e.Version, installed.Version)) continue;
                if (OlderThanInstalled(e, installed)) continue;
                list.Add(e);
            }
            list.Sort(delegate(CatalogEntry a, CatalogEntry b)
            {
                int c = (VersionUtil.Compare(b.Version, a.Version) ?? 0);
                if (c != 0) return c;
                c = a.Generic.CompareTo(b.Generic);
                if (c != 0) return c;
                return (b.LastUpdated ?? DateTime.MinValue).CompareTo(a.LastUpdated ?? DateTime.MinValue);
            });
            return list;
        }

        /// <summary>
        /// Windows elige el controlador por FECHA antes que por versión. La fecha de publicación en el
        /// catálogo nunca es anterior a la del controlador, así que si se publicó antes de la fecha del
        /// instalado, Windows lo va a rechazar aunque su número sea mayor (AMD Controller Emulation:
        /// 25.20.0.11 publicada el 08/09/2025 contra 25.10.0.1 del 23/09/2025 → pnputil 259). Un día de
        /// margen por la zona horaria.
        /// </summary>
        public static bool OlderThanInstalled(CatalogEntry e, DriverInfo installed)
        {
            return e != null && installed != null && e.LastUpdated.HasValue && installed.Date.HasValue &&
                   e.LastUpdated.Value.Date < installed.Date.Value.Date.AddDays(-1);
        }

        /// <summary>Resultado de instalar un paquete del catálogo en un dispositivo.</summary>
        public enum InstallOutcome { Actualizado, Reinicio, NoAplicado }

        /// <summary>
        /// Qué pasó, según el código de pnputil, si su salida nombra al dispositivo (el identificador no
        /// se traduce, el texto sí) y la versión que el registro informa ahora.
        ///  - La versión ya es la esperada (o más nueva): actualizado (también con 259, cuando otro paquete
        ///    de la misma tanda ya lo había actualizado: Realtek HSA).
        ///  - 3010: se aplica al reiniciar.
        ///  - La salida dice que un paquete se instaló en el dispositivo («instalado en el dispositivo»,
        ///    no «está actualizado en el dispositivo»): se instaló, pero el dispositivo todavía no lo tomó;
        ///    se aplica al reiniciar. Vale también con 259: en el paquete de Synaptics un INF se instaló en
        ///    el dispositivo y otro de la misma tanda informó «está actualizado», y pnputil devolvió 259.
        ///  - Si no: Windows se quedó con el que tenía.
        /// </summary>
        public static InstallOutcome Interpret(int exitCode, string pnputilOutput, string deviceId, string versionNow, string expected)
        {
            return Interpret(exitCode, pnputilOutput, deviceId, versionNow, expected, false);
        }

        /// <summary>
        /// <paramref name="replacesInbox"/>: el dispositivo usaba el genérico de Windows (numerado 10.0.26100.x) y el del fabricante
        /// tiene un número MENOR (10.0.1.42). Ahí «la versión de ahora no es más vieja que la esperada» no prueba nada: el genérico
        /// ya «ganaba» antes de instalar, y la fila decía «Actualizada» sin que nada cambiara (grabación del usuario). Sólo
        /// cuenta si el dispositivo informa exactamente la versión del fabricante.
        /// </summary>
        public static InstallOutcome Interpret(int exitCode, string pnputilOutput, string deviceId, string versionNow, string expected, bool replacesInbox)
        {
            if (replacesInbox)
            {
                if (VersionUtil.Parse(versionNow) != null && VersionUtil.Compare(versionNow, expected) == 0) return InstallOutcome.Actualizado;
            }
            else if (VersionUtil.Parse(versionNow) != null && VersionUtil.Parse(expected) != null && !VersionUtil.IsNewer(expected, versionNow)) return InstallOutcome.Actualizado;
            // 2.0.25: 3010 es del PAQUETE (un paquete de AMD trae varios INF: chipset, audio, video): si la salida nombra a ESTE
            // dispositivo y sólo como «está actualizado», no se le instaló nada y reiniciar no cambia nada.
            if (exitCode == 3010) return MentionsDevice(pnputilOutput, deviceId) && !InstalledOnDevice(pnputilOutput, deviceId) ? InstallOutcome.NoAplicado : InstallOutcome.Reinicio;
            if ((exitCode == 0 || exitCode == 259) && InstalledOnDevice(pnputilOutput, deviceId)) return InstallOutcome.Reinicio;
            return InstallOutcome.NoAplicado;
        }

        // «está actualizado en el dispositivo» / «is up-to-date on device» y sus traducciones habituales.
        static readonly string[] UpToDateMarkers = { "actualizado", "up-to-date", "up to date", "à jour", "aktuell", "atualizado", "aggiornato" };

        /// <summary>
        /// ¿La salida de pnputil nombra al dispositivo como destino de una instalación? Cada mención se lee
        /// con la frase que la precede (hasta el salto de línea): si dice que ya estaba actualizado, no cuenta.
        /// </summary>
        public static bool InstalledOnDevice(string pnputilOutput, string deviceId)
        {
            if (string.IsNullOrEmpty(pnputilOutput) || string.IsNullOrEmpty(deviceId)) return false;
            int at = 0;
            while ((at = pnputilOutput.IndexOf(deviceId, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int from = Math.Max(0, at - 120);
                string before = pnputilOutput.Substring(from, at - from).TrimEnd();
                int cut = Math.Max(before.LastIndexOf('\n'), Math.Max(before.LastIndexOf('\r'), before.LastIndexOf("  ", StringComparison.Ordinal)));
                if (cut >= 0) before = before.Substring(cut + 1);
                bool upToDate = false;
                foreach (string m in UpToDateMarkers)
                    if (before.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) { upToDate = true; break; }
                if (!upToDate) return true;
                at += deviceId.Length;
            }
            return false;
        }

        /// <summary>¿La salida de pnputil nombra al dispositivo (para bien o para mal)?</summary>
        public static bool MentionsDevice(string pnputilOutput, string deviceId)
        {
            return !string.IsNullOrEmpty(pnputilOutput) && !string.IsNullOrEmpty(deviceId) && pnputilOutput.IndexOf(deviceId, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static readonly Regex OemInf = new Regex(@"^oem\d{1,5}\.inf$", RX);

        /// <summary>¿Es el nombre de un paquete de terceros del almacén de controladores (oemNN.inf)?</summary>
        public static bool IsOemInf(string name) { return !string.IsNullOrEmpty(name) && OemInf.IsMatch(name); }

        // ------------------------------------------------------------------ Controlador del fabricante del equipo

        /// <summary>
        /// ¿Windows va a quedarse con el controlador instalado? Windows ordena los controladores primero por
        /// cuán exacto es el identificador con que coinciden y recién después por fecha y versión: uno del
        /// fabricante del equipo elegido por el modelo exacto (…&amp;SUBSYS_3E911849) le gana a uno genérico del
        /// chip (PCI\VEN_8086&amp;DEV_3E91) aunque sea de 2019 y el otro de 2026. Pasó con Intel UHD Graphics 630:
        /// 13 minutos de descarga para que pnputil respondiera «está actualizado en el dispositivo».
        /// </summary>
        public static bool WindowsPrefersInstalled(string installedMatchingId, bool foundOnlyByChip)
        {
            return foundOnlyByChip && !string.IsNullOrEmpty(installedMatchingId) &&
                   installedMatchingId.IndexOf("&SUBSYS_", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Identificadores del dispositivo con que se puede forzar el controlador, del más específico al más
        /// general: el completo, sin REV y sin SUBSYS (los mismos cortes que la búsqueda en el catálogo).
        /// </summary>
        public static List<string> ForceIds(string hardwareId)
        {
            var list = new List<string>();
            foreach (var q in QueriesFor(hardwareId))
                if (!list.Exists(delegate(string s) { return s.Equals(q.Key, StringComparison.OrdinalIgnoreCase); })) list.Add(q.Key);
            // 2.0.25: el chip con su revisión y sin el modelo del equipo. Es como declaran AMD y ATI sus placas y gráficos integrados
            // («PCI\VEN_1002&DEV_15D8&REV_C1», Vega 8): sin esto, «…&DEV_15D8» no coincidía (le sigue «&REV_C1») y forzarlo decía
            // «el paquete no declara este dispositivo».
            string h = (hardwareId ?? "").Trim();
            var rev = RevRx.Match(h);
            if (rev.Success && SubsysRx.IsMatch(h))
            {
                string chipRev = SubsysRx.Replace(h, "");
                int at = list.FindIndex(delegate(string s) { return s.IndexOf("&SUBSYS_", StringComparison.OrdinalIgnoreCase) < 0; });
                if (chipRev.IndexOf('&') > 0 && !list.Exists(delegate(string s) { return s.Equals(chipRev, StringComparison.OrdinalIgnoreCase); }))
                {
                    if (at < 0) list.Add(chipRev); else list.Insert(at, chipRev);
                }
            }
            if (hardwareId != null && hardwareId.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase))
            {
                // Los códecs de audio no se buscan sin SUBSYS, pero el INF puede declararlos así.
                string generic = SubsysRx.Replace(RevRx.Replace(hardwareId.Trim(), ""), "");
                if (generic.IndexOf('&') > 0 && !list.Exists(delegate(string s) { return s.Equals(generic, StringComparison.OrdinalIgnoreCase); })) list.Add(generic);
            }
            return list;
        }

        /// <summary>
        /// El identificador más específico del dispositivo que declara el INF (comparando como palabra:
        /// «…DEV_3E91» no vale por «…DEV_3E912»), o null si no lo declara.
        /// </summary>
        public static string InfMatchingId(string infText, List<string> ids)
        {
            if (string.IsNullOrEmpty(infText) || ids == null) return null;
            foreach (string id in ids)
            {
                int at = 0;
                while ((at = infText.IndexOf(id, at, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    int end = at + id.Length;
                    bool startOk = at == 0 || !IsIdChar(infText[at - 1]);
                    bool endOk = end >= infText.Length || !IsIdChar(infText[end]);
                    if (startOk && endOk) return id;
                    at = end;
                }
            }
            return null;
        }

        static readonly Regex InfClassRx = new Regex(@"^\s*Class\s*=\s*""?(?<c>[A-Za-z]+)", RX | RegexOptions.Multiline);

        /// <summary>La clase que declara el INF en [Version] («Display», «Extension»…), o "".</summary>
        public static string InfClass(string infText)
        {
            var m = InfClassRx.Match(infText ?? "");
            return m.Success ? m.Groups["c"].Value : "";
        }

        /// <summary>Qué dice el paquete extraído del dispositivo, antes de instalarlo.</summary>
        public enum PackageFit { Declares, OnlyComponents, NotDeclared }

        /// <summary>
        /// 2.0.25: ¿algún INF del paquete declara alguno de los identificadores del dispositivo (de hardware o compatibles)?
        /// <paramref name="infTexts"/>: el texto de cada INF. Un paquete del catálogo encontrado por el chip puede ser de otro fabricante
        /// de equipos y declarar sólo SUS modelos (…&amp;SUBSYS_380817AA): pnputil lo agregaba al almacén, ningún dispositivo lo tomaba y la
        /// fila quedaba en error (los paquetes de video de AMD pesan cientos de MB). Si los únicos que lo declaran son complementos
        /// (extensiones o componentes de software), no cambian el controlador del dispositivo.
        /// </summary>
        public static PackageFit Fit(IEnumerable<string> infTexts, List<string> deviceIds, string deviceClass)
        {
            if (infTexts == null || deviceIds == null || deviceIds.Count == 0) return PackageFit.Declares;   // sin datos: decide Windows
            bool any = false;
            foreach (string t in infTexts)
            {
                if (InfMatchingId(t, deviceIds) == null) continue;
                any = true;
                if (!IsComponentClass(InfClass(t)) || IsComponentClass(deviceClass)) return PackageFit.Declares;
            }
            return any ? PackageFit.OnlyComponents : PackageFit.NotDeclared;
        }

        /// <summary>2.0.26: sólo los identificadores con el modelo del equipo (…&amp;SUBSYS_…).</summary>
        public static List<string> ModelIds(List<string> ids)
        {
            var list = new List<string>();
            if (ids != null) foreach (string id in ids) if (id.IndexOf("&SUBSYS_", StringComparison.OrdinalIgnoreCase) >= 0) list.Add(id);
            return list;
        }

        /// <summary>
        /// Identificadores con que se compara el paquete: los del registro (de hardware y compatibles) y, si no se pudieron leer, los
        /// que se deducen del identificador de hardware.
        /// </summary>
        public static List<string> MatchIds(string hardwareId, IList<string> fromRegistry)
        {
            var list = new List<string>();
            if (fromRegistry != null)
                foreach (string id in fromRegistry)
                {
                    string t = (id ?? "").Trim();
                    if (t.Length >= 4 && !list.Exists(delegate(string s) { return s.Equals(t, StringComparison.OrdinalIgnoreCase); })) list.Add(t);
                }
            foreach (string id in ForceIds(hardwareId))
                if (!list.Exists(delegate(string s) { return s.Equals(id, StringComparison.OrdinalIgnoreCase); })) list.Add(id);
            return list;
        }

        static bool IsIdChar(char ch) { return char.IsLetterOrDigit(ch) || ch == '_' || ch == '&' || ch == '\\'; }

        /// <summary>Texto de un INF (UTF-16 con o sin BOM, UTF-8 o ANSI).</summary>
        public static string InfText(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
            int zeros = 0, n = Math.Min(data.Length, 512);
            for (int i = 1; i < n; i += 2) if (data[i] == 0) zeros++;
            if (zeros * 4 > n) return Encoding.Unicode.GetString(data);      // UTF-16 sin BOM
            return TextEncodings.Ansi.GetString(data);
        }

        /// <summary>Qué significa el error de UpdateDriverForPlugAndPlayDevices.</summary>
        public static string ForceErrorText(int win32Error)
        {
            switch (unchecked((uint)win32Error))
            {
                case 0xE000020B: return "el paquete no corresponde a este dispositivo (ERROR_NO_SUCH_DEVINST)";
                case 0x00000103: return "Windows no encontró en el paquete un controlador para el dispositivo";
                case 0xE0000235: return "hay que ejecutar la versión de 64 bits del programa (ERROR_IN_WOW64)";
                case 0x00000005: return "acceso denegado (ejecutá como administrador)";
                case 0x00000002: return "no se encontró el INF extraído";
                case 0x800B0100: case 0xE0000247: return "el controlador no tiene una firma válida para este Windows";
                case 0x000004C7: return "se canceló";
                default: return "error " + Fmt.Hex(win32Error);
            }
        }

        /// <summary>¿El catálogo tiene alguna fila válida para este Windows (aunque no sea más nueva)?</summary>
        public static bool AnyApplicable(List<CatalogEntry> entries, DriverInfo d, int build, bool server)
        {
            if (entries != null)
                foreach (var e in entries)
                    if (VersionUtil.Parse(e.Version) != null && !IsFirmware(e, d) && !IsComponentFor(e, d) && AppliesTo(e.Products, build, server)) return true;
            return false;
        }

        /// <summary>
        /// ¿Se compara este controlador? Los de terceros con identificador de hardware, sin firmware. También los
        /// dispositivos de un fabricante (PCI, audio HD, USB, ACPI) que usan el controlador genérico de Windows: el catálogo
        /// puede tener el del fabricante (chipset, audio, lector de tarjetas…).
        /// </summary>
        public static bool ShouldCheck(DriverInfo d)
        {
            if (d == null || VersionUtil.Parse(d.Version) == null) return false;
            if (IsFirmware(null, d)) return false;
            if (InventoryLoader.IsInboxDriver(d) && !IsVendorHardware(d)) return false;
            return QueriesFor(d.HardwareId).Count > 0;
        }

        static readonly HashSet<string> NoVendorDriverClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Volume", "VolumeSnapshot", "SoftwareDevice", "SoftwareComponent", "Computer", "Processor", "DiskDrive", "CDROM",
            "PrintQueue", "LegacyDriver", "Keyboard", "Mouse", "Monitor", "Battery", "Firmware"
        };

        static readonly Regex VendorHwid = new Regex(
            @"^(?:PCI\\VEN_(?!1414)[0-9A-F]{4}&DEV_[0-9A-F]{4}|HDAUDIO\\FUNC_[0-9A-F]{2}&VEN_(?!1414)[0-9A-F]{4}&DEV_[0-9A-F]{4}|USB\\VID_(?!045E)[0-9A-F]{4}&PID_[0-9A-F]{4}|ACPI\\(?!PNP|ACPI|MSFT|VEN_MSFT)[A-Z0-9]{3,4}(?:[0-9A-F]{4})?$|ACPI\\VEN_(?!MSFT|PNP)[A-Z0-9]{3,4}&DEV_[0-9A-F]{4})",
            RX);

        /// <summary>
        /// ¿Es hardware de un fabricante (no un dispositivo virtual ni de Microsoft) que hoy usa el controlador genérico de
        /// Windows? Sólo esos se buscan en el catálogo cuando el controlador es el incluido con Windows.
        /// </summary>
        public static bool IsVendorHardware(DriverInfo d)
        {
            if (d == null || string.IsNullOrEmpty(d.HardwareId)) return false;
            if (NoVendorDriverClasses.Contains((d.DeviceClass ?? "").Trim())) return false;
            return VendorHwid.IsMatch(d.HardwareId.Trim());
        }

        /// <summary>
        /// Para un dispositivo con el controlador genérico de Windows: el controlador del fabricante para este Windows, el más
        /// nuevo. La versión no se compara con la del genérico (Windows numera los suyos 10.0.x): Windows prefiere el del
        /// fabricante porque coincide con el identificador del dispositivo, no con uno compatible. Primero los encontrados con
        /// el identificador completo.
        /// </summary>
        public static List<CatalogEntry> VendorReplacements(List<CatalogEntry> entries, DriverInfo installed, int build, bool server)
        {
            var list = new List<CatalogEntry>();
            if (entries == null || installed == null) return list;
            foreach (var e in entries)
            {
                if (VersionUtil.Parse(e.Version) == null || IsFirmware(e, installed) || !AppliesTo(e.Products, build, server)) continue;
                if (IsComponentFor(e, installed)) continue;
                list.Add(e);
            }
            list.Sort(delegate(CatalogEntry a, CatalogEntry b)
            {
                int c = a.Generic.CompareTo(b.Generic);
                if (c != 0) return c;
                c = (VersionUtil.Compare(b.Version, a.Version) ?? 0);
                if (c != 0) return c;
                return (b.LastUpdated ?? DateTime.MinValue).CompareTo(a.LastUpdated ?? DateTime.MinValue);
            });
            return list;
        }

        /// <summary>Verifica la huella que publica el catálogo (base64 de SHA-1, o SHA-256 si es más larga).</summary>
        public static bool DigestMatches(Stream s, string digestBase64)
        {
            byte[] want;
            try { want = Convert.FromBase64String((digestBase64 ?? "").Trim()); }
            catch (FormatException) { return false; }
            HashAlgorithm h;
            if (want.Length == 20) h = SHA1.Create();
            else if (want.Length == 32) h = SHA256.Create();
            else return false;
            using (h)
            {
                s.Position = 0;
                byte[] got = h.ComputeHash(s);
                s.Position = 0;
                if (got.Length != want.Length) return false;
                int diff = 0;
                for (int i = 0; i < got.Length; i++) diff |= got[i] ^ want[i];
                return diff == 0;
            }
        }

        /// <summary>¿Empieza como un archivo CAB («MSCF»)?</summary>
        public static bool IsCab(Stream s)
        {
            s.Position = 0;
            byte[] head = new byte[4];
            int n = s.Read(head, 0, 4);
            s.Position = 0;
            return n == 4 && head[0] == (byte)'M' && head[1] == (byte)'S' && head[2] == (byte)'C' && head[3] == (byte)'F';
        }

        /// <summary>pnputil anterior a Windows 10 1607 (14393): sólo «-i -a» y «-d».</summary>
        public static bool LegacyPnpUtil(int build) { return build > 0 && build < 14393; }

        /// <summary>Argumentos para agregar e instalar: una carpeta con «*.inf» (moderno) o un INF (anterior a 1607).</summary>
        public static string AddDriverArgs(int build, string infPath)
        {
            return LegacyPnpUtil(build) ? "-i -a " + ProcessRunner.QuoteArg(infPath)
                                        : "/add-driver " + ProcessRunner.QuoteArg(infPath) + " /subdirs /install";
        }

        /// <summary>Argumentos para quitar un paquete sin usar del almacén (oemNN.inf, sin forzar).</summary>
        public static string DeleteDriverArgs(int build, string oemInf)
        {
            return (LegacyPnpUtil(build) ? "-d " : "/delete-driver ") + oemInf;
        }

        /// <summary>¿pnputil terminó bien (instalado, requiere reiniciar o «ya está al día»)?</summary>
        public static bool PnpUtilSucceeded(int code) { return code == 0 || code == 3010 || code == 259; }

        /// <summary>
        /// ¿Hay que informar el error de pnputil? Sólo si devolvió un error Y el dispositivo no tomó el
        /// controlador: los paquetes grandes traen INF de otros equipos que fallan sin importar.
        /// </summary>
        public static bool PnpUtilFailed(int code, InstallOutcome outcome)
        {
            return !PnpUtilSucceeded(code) && outcome == InstallOutcome.NoAplicado;
        }

        /// <summary>Orden de los códigos de varias ejecuciones (menor = mejor): instalado, reinicio, al día, error.</summary>
        public static int PnpUtilRank(int code)
        {
            switch (code) { case 0: return 0; case 3010: return 1; case 259: return 2; default: return 3; }
        }

        /// <summary>Qué significa el código de salida de pnputil /add-driver /install.</summary>
        public static string PnpUtilText(int code)
        {
            switch (code)
            {
                case 0: return "correcto";
                case 3010: return "correcto; requiere reiniciar";
                case 259: return "el controlador se agregó al almacén de Windows, pero ningún dispositivo lo usa (Windows considera mejor el que ya tiene)";
                case 5: return "acceso denegado (ejecutá como administrador)";
                case 1: return "error al agregar alguno de los controladores del paquete";
                case 2: return "no se encontró el controlador extraído";
                case unchecked((int)0xE0000247): return "Windows no pudo agregarlo al almacén de controladores (suele ser una firma que este Windows no acepta)";
                case unchecked((int)0x800B0109): case unchecked((int)0xE000022F): return "el controlador no tiene una firma que este Windows acepte";
                case unchecked((int)0xE0000228): return "el paquete no tiene un controlador compatible con este dispositivo o esta versión de Windows";
                default: return "código " + code + " (" + Fmt.Hex(code) + ")";
            }
        }
    }
}
