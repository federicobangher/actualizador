// Adobe Acrobat y Reader: qué parche corresponde a cada instalación (código de producto, 32/64 bits).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Acrobat y Reader de la vía continua (DC): versión y parches MSP oficiales.</summary>
    public static class AdobeRules
    {
        static readonly Regex Continuous = new Regex(@"^\s*(?<a>\d{2})\.(?<b>\d{3})\.(?<c>2\d{4})(?:\.\d+)?\s*$", RegexOptions.Compiled);
        static readonly Regex Published = new Regex(@"^\s*(?<v>\d{2}\.\d{3}\.\d{5})\s*$", RegexOptions.Compiled);

        /// <summary>
        /// ¿Es de la vía continua? Sus versiones son «AA.00N.2xxxx» (26.002.21931). La vía clásica
        /// (2020, 2024: «20.005.3xxxx») tiene otro calendario y otros parches: no se toca.
        /// </summary>
        public static bool IsContinuous(string version)
        {
            return !string.IsNullOrEmpty(version) && Continuous.IsMatch(version);
        }

        /// <summary>Versión de current_version.txt, validada (sólo «AA.BBB.CCCCC»); null si no tiene ese formato.</summary>
        public static string ParseVersionText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 200) return null;
            var m = Published.Match(text);
            return m.Success && IsContinuous(m.Groups["v"].Value) ? m.Groups["v"].Value : null;
        }

        static readonly Regex UnifiedX64Code = new Regex(@"^\{AC76BA86-[0-9A-F]{4}-(?<kind>[0-9A-F]{4})-7760-BC15014EA700\}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// ¿Es Reader instalado con el instalador unificado de 64 bits? Acrobat y Reader de 64 bits se
        /// registran los dos como «Adobe Acrobat (64-bit)»; lo único que los distingue es el código de
        /// producto: {AC76BA86-1033-FFFF-7760-BC15014EA700} es Acrobat, y Reader tiene otro valor en el
        /// tercer grupo (FF00 si es multilingüe, o el código del idioma). Con eso se baja primero el
        /// parche AcroRdrDCx64Upd y no el de Acrobat, que msiexec rechazaba con 1642.
        /// </summary>
        public static bool IsUnifiedReader(InstalledProgram p)
        {
            string code = ProgramRules.MsiProductCode(p);
            if (code == null) return false;
            var m = UnifiedX64Code.Match(code);
            return m.Success && !m.Groups["kind"].Value.Equals("FFFF", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------ Notas de versión (Windows)

        /// <summary>
        /// Índice de las notas de versión de Acrobat y Reader. Es la fuente de la versión para
        /// WINDOWS: Adobe numera distinto cada plataforma (octubre de 2026: Windows 26.002.21996,
        /// Mac 26.002.22008) y se leía current_version.txt de arm-manifests/mac,
        /// que es el de Mac: se ofrecía una versión que en Windows no existe y todos los parches
        /// daban 404 («Falló la instalación» aunque Acrobat estaba al día).
        /// </summary>
        public const string ReleaseNotesUrl = "https://www.adobe.com/devnet-docs/acrobatetk/tools/ReleaseNotesDC/index.html";

        /// <summary>Notas de versión que se revisan como mucho (la más nueva puede ser sólo para Mac).</summary>
        public const int MaxReleasePages = 4;

        static readonly Regex ContinuousPage = new Regex(
            @"href\s*=\s*[""'](?<u>[^""'#<>\s]*?continuous/dccontinuous[A-Za-z0-9_\-]*\.html)(?:#[^""'<>]*)?[""']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// Páginas de notas de la vía continua, en el orden del índice (la más nueva primero), sin
        /// repetir y sin el ancla. Sólo HTTPS; el dominio lo valida quien las descarga.
        /// </summary>
        public static List<string> ReleasePages(string indexHtml, string indexUrl)
        {
            var list = new List<string>();
            Uri baseUri;
            if (string.IsNullOrEmpty(indexHtml) || !Uri.TryCreate(indexUrl, UriKind.Absolute, out baseUri)) return list;
            foreach (Match m in ContinuousPage.Matches(indexHtml))
            {
                Uri u;
                if (!Uri.TryCreate(baseUri, WebUtility.HtmlDecode(m.Groups["u"].Value), out u)) continue;
                if (u.Scheme != Uri.UriSchemeHttps) continue;
                string page = u.GetLeftPart(UriPartial.Path);
                if (!list.Contains(page)) list.Add(page);
                if (list.Count >= MaxReleasePages) break;
            }
            return list;
        }

        static readonly Regex WindowsMsp = new Regex(
            @"https://ardownload\d*\.adobe\.com/pub/adobe/(?:acrobat|reader)/win/AcrobatDC/(?<d>\d{10})/(?<f>(?:AcrobatDC|AcroRdrDC)(?:x64)?Upd(?<d2>\d{10})(?:_MUI)?\.msp)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>«2600221996» → «26.002.21996»; null si no es de la vía continua.</summary>
        public static string VersionFromFolder(string digits)
        {
            if (digits == null || digits.Length != 10) return null;
            string v = digits.Substring(0, 2) + "." + digits.Substring(2, 3) + "." + digits.Substring(5);
            return IsContinuous(v) ? v : null;
        }

        /// <summary>
        /// Versión para Windows y sus parches (archivo → dirección exacta) en una página de notas.
        /// null si la página no publica parches para Windows (una versión sólo para Mac).
        /// </summary>
        public static AdobeWindowsRelease ParseWindowsRelease(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            AdobeWindowsRelease best = null;
            var byVersion = new Dictionary<string, AdobeWindowsRelease>(StringComparer.Ordinal);
            foreach (Match m in WindowsMsp.Matches(html))
            {
                string d = m.Groups["d"].Value;
                if (d != m.Groups["d2"].Value) continue;   // carpeta y archivo de versiones distintas: no se confía
                string v = VersionFromFolder(d);
                if (v == null) continue;
                AdobeWindowsRelease r;
                if (!byVersion.TryGetValue(v, out r)) { r = new AdobeWindowsRelease(v); byVersion[v] = r; }
                string file = m.Groups["f"].Value;
                if (!r.Patches.ContainsKey(file)) r.Patches[file] = m.Value;
                if (best == null || VersionUtil.IsNewer(v, best.Version)) best = r;
            }
            return best;
        }

        /// <summary>
        /// Parches MSP oficiales para pasar a <paramref name="version"/>, con las direcciones exactas que
        /// publican las notas de versión cuando están (ardownload3); si no, las de ardownload2.
        /// </summary>
        public static List<string> MspUrls(bool reader, string arch, string version, IDictionary<string, string> published)
        {
            var list = MspUrls(reader, arch, version);
            if (published == null || published.Count == 0) return list;
            for (int i = 0; i < list.Count; i++)
            {
                string exact;
                if (published.TryGetValue(list[i].Substring(list[i].LastIndexOf('/') + 1), out exact)) list[i] = exact;
            }
            return list;
        }

        /// <summary>
        /// Parches MSP oficiales para pasar a <paramref name="version"/>, en orden de preferencia. El
        /// Acrobat de 64 bits es un instalador unificado (Reader y Acrobat son el mismo producto): si
        /// msiexec dice que un parche no corresponde (1642), se prueba el siguiente.
        /// </summary>
        public static List<string> MspUrls(bool reader, string arch, string version)
        {
            var list = new List<string>();
            if (ParseVersionText(version) == null) return list;
            string v = version.Replace(".", "");
            string acro = "https://ardownload2.adobe.com/pub/adobe/acrobat/win/AcrobatDC/" + v + "/";
            string rdr = "https://ardownload2.adobe.com/pub/adobe/reader/win/AcrobatDC/" + v + "/";
            if (arch == "x64" || arch == "arm64")
            {
                if (arch == "arm64") return list;   // Acrobat para ARM tiene otros paquetes: se actualiza desde Acrobat
                if (!reader) list.Add(acro + "AcrobatDCx64Upd" + v + ".msp");
                list.Add(acro + "AcroRdrDCx64Upd" + v + ".msp");
                list.Add(acro + "AcroRdrDCx64Upd" + v + "_MUI.msp");
                if (reader) list.Add(acro + "AcrobatDCx64Upd" + v + ".msp");
            }
            else
            {
                if (!reader) list.Add(acro + "AcrobatDCUpd" + v + ".msp");
                list.Add(rdr + "AcroRdrDCUpd" + v + ".msp");
                list.Add(rdr + "AcroRdrDCUpd" + v + "_MUI.msp");
            }
            return list;
        }
    }

    /// <summary>Una versión de Acrobat/Reader para Windows según sus notas: versión y parches publicados.</summary>
    public sealed class AdobeWindowsRelease
    {
        public readonly string Version;
        /// <summary>Nombre del archivo (AcrobatDCx64Upd2600221996.msp) → dirección exacta publicada.</summary>
        public readonly Dictionary<string, string> Patches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public AdobeWindowsRelease(string version) { Version = version; }
    }
}
