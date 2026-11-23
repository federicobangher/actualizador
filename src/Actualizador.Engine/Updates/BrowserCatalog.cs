// Chrome, Edge y Firefox: dónde están instalados y cómo se consulta la última versión con cada fabricante.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Un navegador instalado, tal como lo ve el proveedor de navegadores.</summary>
    public sealed class BrowserInfo
    {
        public string Key = "";          // chrome, edge, firefox
        public string Name = "";         // nombre para mostrar
        public string Publisher = "";
        public string Version = "";      // la que corre hoy
        public string PendingVersion;    // descargada, se aplica al reabrir el navegador (Chrome/Edge)
        public string InstallDir = "";
        public string Arch = "";         // x64, x86, arm64
        public bool PerUser;
        public bool Esr;
        public string Lang = "";         // idioma del instalador de Firefox (es-AR)
        /// <summary>Nombres del registro que representa esta fila (para no repetirlos desde winget).</summary>
        public readonly List<string> RegistryNames = new List<string>();

        public string ExeName
        {
            get { return Key == "chrome" ? "chrome.exe" : Key == "edge" ? "msedge.exe" : "firefox.exe"; }
        }
    }

    /// <summary>
    /// Partes del proveedor de navegadores que no dependen de Windows: reconocer los navegadores
    /// en la lista de programas e interpretar las respuestas de los servicios oficiales de versiones.
    /// </summary>
    public static class BrowserCatalog
    {
        public const string ChromeSigner = "Google LLC";
        public const string EdgeSigner = "Microsoft Corporation";
        public const string FirefoxSigner = "Mozilla Corporation";

        static readonly Regex FirefoxName = new Regex(
            @"^Mozilla Firefox(?<esr> ESR)?(?: [0-9][0-9.]*(?:esr)?)?(?: \((?<arch>x64|x86|ARM64|AArch64)?\s*(?<lang>[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8})?)?\))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SafeLang = new Regex(@"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8})?$", RegexOptions.Compiled);

        /// <summary>¿Qué navegador es este nombre del registro? null si no es uno de los que se verifican.</summary>
        public static string KeyFor(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name.Trim();
            // winget recorta los nombres largos: «Mozilla Firefox (x64 es-…».
            if ((n.EndsWith("…") || n.EndsWith("...")) && n.StartsWith("Mozilla Firefox", StringComparison.OrdinalIgnoreCase)) return "firefox";
            if (n.Equals("Google Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
            if (n.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase)) return "edge";
            if (FirefoxName.IsMatch(n)) return "firefox";
            return null;
        }

        public static List<BrowserInfo> Detect(List<InstalledProgram> programs, string nativeArch)
        {
            var result = new List<BrowserInfo>();
            if (programs == null) return result;
            string native = NormalizeArch(nativeArch);
            if (native.Length == 0) native = "x64";
            foreach (var p in programs)
            {
                if (p == null || p.Unregistered) continue;     // copia portable: no se instala encima
                string key = KeyFor(p.Name);
                if (key == null) continue;
                bool perUser = !string.IsNullOrEmpty(p.Scope) && !p.Scope.Equals("Equipo", StringComparison.OrdinalIgnoreCase);
                var b = new BrowserInfo();
                b.Key = key;
                b.PerUser = perUser;
                b.Version = (p.Version ?? "").Trim();
                b.Publisher = string.IsNullOrEmpty(p.Publisher) ? DefaultPublisher(key) : p.Publisher;
                b.InstallDir = InstallDirOf(p);
                string archFromPath = b.InstallDir.IndexOf("(x86)", StringComparison.OrdinalIgnoreCase) >= 0 ? "x86" : "";
                if (key == "firefox")
                {
                    var m = FirefoxName.Match(p.Name.Trim());
                    b.Esr = m.Groups["esr"].Success || b.Version.IndexOf("esr", StringComparison.OrdinalIgnoreCase) >= 0;
                    b.Arch = NormalizeArch(m.Groups["arch"].Value);
                    b.Lang = m.Groups["lang"].Success && SafeLang.IsMatch(m.Groups["lang"].Value) ? m.Groups["lang"].Value : "en-US";
                    b.Name = b.Esr ? "Mozilla Firefox ESR" : "Mozilla Firefox";
                }
                else
                {
                    b.Name = key == "chrome" ? "Google Chrome" : "Microsoft Edge";
                    // El registro de Chrome/Edge vive en la vista de 32 bits aunque el navegador sea de 64:
                    // la arquitectura se decide por la carpeta (y en Windows, por el propio chrome.exe).
                    b.Arch = "";
                }
                if (b.Arch.Length == 0) b.Arch = archFromPath.Length > 0 && key == "firefox" ? "x86" : native;
                if (key == "edge") b.PerUser = false;   // Edge siempre es del equipo

                BrowserInfo same = null;
                foreach (var x in result)
                    if (x.Key == b.Key && x.PerUser == b.PerUser && x.Esr == b.Esr) { same = x; break; }
                if (same != null)
                {
                    if (!same.RegistryNames.Contains(p.Name)) same.RegistryNames.Add(p.Name);
                    if (VersionUtil.IsNewer(b.Version, same.Version)) same.Version = b.Version;
                    if (same.InstallDir.Length == 0) same.InstallDir = b.InstallDir;
                    continue;
                }
                b.RegistryNames.Add(p.Name);
                result.Add(b);
            }
            return result;
        }

        static string DefaultPublisher(string key)
        {
            return key == "chrome" ? ChromeSigner : key == "edge" ? EdgeSigner : FirefoxSigner;
        }

        public static string NormalizeArch(string a)
        {
            if (string.IsNullOrEmpty(a)) return "";
            string s = a.Trim().ToLowerInvariant();
            if (s == "x64" || s == "amd64") return "x64";
            if (s == "x86" || s == "i386") return "x86";
            if (s == "arm64" || s == "aarch64") return "arm64";
            return "";
        }

        /// <summary>Carpeta del navegador: InstallLocation o, si falta, la que se deduce del desinstalador.</summary>
        public static string InstallDirOf(InstalledProgram p)
        {
            string loc = (p.InstallLocation ?? "").Trim().Trim('"').TrimEnd('\\');
            if (loc.Length > 0) return loc;
            string u = p.UninstallString ?? "";
            if (u.StartsWith("\"")) { int q = u.IndexOf('"', 1); u = q > 0 ? u.Substring(1, q - 1) : u.Trim('"'); }
            else { int e = u.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (e > 0) u = u.Substring(0, e + 4); }
            // Chrome/Edge: ...\Application\153.0.8010.53\Installer\setup.exe -> ...\Application
            int app = u.IndexOf(@"\Application\", StringComparison.OrdinalIgnoreCase);
            if (app > 0) return u.Substring(0, app + @"\Application".Length);
            // Firefox: ...\Mozilla Firefox\uninstall\helper.exe -> ...\Mozilla Firefox
            int un = u.IndexOf(@"\uninstall\", StringComparison.OrdinalIgnoreCase);
            if (un > 0) return u.Substring(0, un);
            return "";
        }

        // ------------------------------------------------------------------ Servicios oficiales

        public static string ChromePlatform(string arch)
        {
            return arch == "x86" ? "win" : arch == "arm64" ? "win_arm64" : "win64";
        }

        public static string ChromeReleasesUrl(string arch)
        {
            return "https://versionhistory.googleapis.com/v1/chrome/platforms/" + ChromePlatform(arch) +
                   "/channels/stable/versions/all/releases?filter=endtime=none";
        }

        public const string EdgeProductsUrl = "https://edgeupdates.microsoft.com/api/products";
        public const string FirefoxVersionsUrl = "https://product-details.mozilla.org/1.0/firefox_versions.json";

        /// <summary>
        /// Versión estable de Chrome que Google está entregando ahora (versionhistory, lanzamientos
        /// sin fecha de fin). Si hay un despliegue parcial en curso se toma la versión que llega al
        /// menos a la mitad de los equipos: es la que baja el instalador oficial.
        /// </summary>
        public static string ParseChromeLatest(string json)
        {
            object root = Json.Parse(json);
            string best = null, bestAny = null;
            foreach (object r in Json.List(root, "releases"))
            {
                string v = Json.Str(r, "version");
                if (VersionUtil.Parse(v) == null) continue;
                double fraction = 1;
                string f = Json.Str(r, "fraction");
                if (!string.IsNullOrEmpty(f)) double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out fraction);
                if (bestAny == null || VersionUtil.IsNewer(v, bestAny)) bestAny = v;
                if (fraction >= 0.5 && (best == null || VersionUtil.IsNewer(v, best))) best = v;
            }
            if (best == null && bestAny == null)
                foreach (object r in Json.List(root, "versions"))   // respuesta de /versions
                {
                    string v = Json.Str(r, "version");
                    if (VersionUtil.Parse(v) != null && (bestAny == null || VersionUtil.IsNewer(v, bestAny))) bestAny = v;
                }
            return best ?? bestAny;
        }

        public sealed class EdgeRelease
        {
            public string Version, MsiUrl, Sha256;
            public long Size;
        }

        /// <summary>Última versión estable de Edge para Windows y esa arquitectura, con su MSI y hash.</summary>
        public static EdgeRelease ParseEdgeLatest(string json, string arch)
        {
            object root = Json.Parse(json);
            var products = root as List<object> ?? new List<object>();
            string wanted = arch == "x86" ? "x86" : arch == "arm64" ? "arm64" : "x64";
            EdgeRelease best = null;
            foreach (object p in products)
            {
                if (!string.Equals(Json.Str(p, "Product"), "Stable", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (object r in Json.List(p, "Releases"))
                {
                    if (!string.Equals(Json.Str(r, "Platform"), "Windows", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(Json.Str(r, "Architecture"), wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    string v = Json.Str(r, "ProductVersion");
                    if (VersionUtil.Parse(v) == null) continue;
                    if (best != null && !VersionUtil.IsNewer(v, best.Version)) continue;
                    var e = new EdgeRelease();
                    e.Version = v;
                    foreach (object a in Json.List(r, "Artifacts"))
                    {
                        if (!string.Equals(Json.Str(a, "ArtifactName"), "msi", StringComparison.OrdinalIgnoreCase)) continue;
                        e.MsiUrl = Json.Str(a, "Location");
                        if (string.Equals(Json.Str(a, "HashAlgorithm"), "SHA256", StringComparison.OrdinalIgnoreCase))
                            e.Sha256 = Json.Str(a, "Hash");
                        long size;
                        if (long.TryParse(Json.Str(a, "SizeInBytes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size)) e.Size = size;
                    }
                    best = e;
                }
            }
            return best;
        }

        public static string ParseFirefoxLatest(string json, bool esr)
        {
            object root = Json.Parse(json);
            string v = Json.Str(root, esr ? "FIREFOX_ESR" : "LATEST_FIREFOX_VERSION");
            return VersionUtil.Parse(v) == null ? null : v;
        }

        public static string ChromeMsiUrl(string arch)
        {
            if (arch == "x86") return "https://dl.google.com/dl/chrome/install/googlechromestandaloneenterprise.msi";
            if (arch == "arm64") return "https://dl.google.com/dl/chrome/install/googlechromestandaloneenterprise_arm64.msi";
            return "https://dl.google.com/dl/chrome/install/googlechromestandaloneenterprise64.msi";
        }

        public static string FirefoxInstallerUrl(bool esr, string arch, string lang)
        {
            string os = arch == "x86" ? "win" : arch == "arm64" ? "win64-aarch64" : "win64";
            string l = SafeLang.IsMatch(lang ?? "") ? lang : "en-US";
            return "https://download.mozilla.org/?product=" + (esr ? "firefox-esr-latest-ssl" : "firefox-latest-ssl") +
                   "&os=" + os + "&lang=" + Uri.EscapeDataString(l);
        }

        /// <summary>
        /// Servidores exactos de los fabricantes. Antes se aceptaba cualquier subdominio de
        /// google.com, googleapis.com o microsoft.com, y ahí también hay servicios que alojan
        /// archivos de cualquiera (storage.googleapis.com, sites.google.com…).
        /// </summary>
        static readonly string[] OfficialHosts =
        {
            "versionhistory.googleapis.com", "dl.google.com",                           // Chrome
            "edgeupdates.microsoft.com",                                                // Edge (versiones)
            "product-details.mozilla.org", "download.mozilla.org", "download-installer.cdn.mozilla.net"  // Firefox
        };

        /// <summary>CDN propias de los fabricantes, sólo por sufijo (redirecciones de las descargas).</summary>
        static readonly string[] OfficialCdnSuffixes =
        {
            ".gvt1.com",                        // CDN de descargas de Google
            ".dl.delivery.mp.microsoft.com"     // CDN de Microsoft del MSI de Edge
        };

        /// <summary>Sólo se descarga desde los servidores de los fabricantes, por HTTPS (también en cada redirección).</summary>
        public static bool IsOfficialUrl(Uri u)
        {
            if (u == null || !u.IsAbsoluteUri || u.Scheme != Uri.UriSchemeHttps) return false;
            if (!u.IsDefaultPort) return false;
            string h = u.Host.ToLowerInvariant();
            foreach (string a in OfficialHosts) if (h == a) return true;
            foreach (string s in OfficialCdnSuffixes) if (h.EndsWith(s) && h.Length > s.Length) return true;
            return false;
        }

        /// <summary>¿El certificado de firma es del editor esperado? (campo O= del sujeto)</summary>
        public static bool SubjectIsFrom(string subject, string organization)
        {
            if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(organization)) return false;
            foreach (Match m in Regex.Matches(subject, @"(?:^|,)\s*O=(?:""(?<v>[^""]*)""|(?<v>[^,]*))"))
                if (m.Groups["v"].Value.Trim().Equals(organization, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Estado de la fila según la versión que corre, la que está descargada esperando que se
        /// reabra el navegador y la última publicada.
        /// </summary>
        public static UpdateStatus Evaluate(string installed, string pending, string latest)
        {
            if (VersionUtil.Parse(latest) == null) return UpdateStatus.Error;
            if (VersionUtil.Parse(installed) == null) return UpdateStatus.Disponible;
            if (!string.IsNullOrEmpty(pending) && VersionUtil.IsNewer(pending, installed) && !VersionUtil.IsNewer(latest, pending))
                return UpdateStatus.RequiereReinicio;
            return VersionUtil.IsNewer(latest, installed) ? UpdateStatus.Disponible : UpdateStatus.AlDia;
        }
    }
}
