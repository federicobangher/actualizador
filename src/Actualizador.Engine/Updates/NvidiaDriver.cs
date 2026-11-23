// 2.0.13: el controlador de gráficos de NVIDIA, comparado con el servicio oficial de NVIDIA para la placa instalada.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Un elemento de las listas de productos y series de NVIDIA (lookupValueSearch.aspx).</summary>
    public sealed class NvidiaLookupValue
    {
        public string Name = "";
        public int Value, ParentId;
    }

    /// <summary>Lo que publica NVIDIA para la placa instalada.</summary>
    public sealed class NvidiaRelease
    {
        /// <summary>Nombre de la placa como lo lista NVIDIA («GeForce GTX 760»).</summary>
        public string Gpu = "";
        public string Version = "", DownloadUrl = "", DetailsUrl = "", Name = "";
        public DateTime? Released;
    }

    /// <summary>
    /// Captura del usuario (07/11/2026): «NVIDIA Controlador de gráficos 475.14» quedaba «No verificable». NVIDIA publica la última
    /// versión de cada placa en el mismo servicio que usa su página de descargas (nvidia.com/Download): con la lista de productos
    /// (lookupValueSearch.aspx) se identifica la placa por su nombre y se consulta su controlador (DriverManualLookup, en
    /// gfwsl.geforce.com) para la versión de Windows del equipo y el tipo de controlador instalado (DCH o estándar), sin betas.
    /// La descarga sale de download.nvidia.com y se instala sólo si la firma es de NVIDIA Corporation, con el instalador de NVIDIA
    /// a la vista (un controlador de video se instala mejor viendo lo que pasa).
    /// </summary>
    public static class NvidiaDriver
    {
        public const string ProductsUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
        public const string SeriesUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=2";
        const string LookupBase = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php";

        /// <summary>Sólo para las pruebas: las placas de video del equipo, si hay controlador de NVIDIA (DCH) y si es portátil.</summary>
        internal static Func<List<string>> GpusForTests;
        internal static bool? DchForTests, LaptopForTests;

        const RegexOptions RX = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
        static readonly Regex Entry = new Regex(@"<LookupValue\b(?<a>[^>]{0,400})>(?<b>.{0,2000}?)</LookupValue>", RX | RegexOptions.Singleline);
        static readonly Regex NameTag = new Regex(@"<Name>(?<n>[^<]{1,200})</Name>", RX);
        static readonly Regex ValueTag = new Regex(@"<Value>(?<v>\d{1,6})</Value>", RX);
        static readonly Regex ParentAttr = new Regex(@"\bParentID\s*=\s*""(?<p>\d{1,6})""", RX);

        /// <summary>Los elementos de una respuesta de lookupValueSearch.aspx (productos con TypeID=3, series con TypeID=2).</summary>
        public static List<NvidiaLookupValue> ParseLookup(string xml)
        {
            var list = new List<NvidiaLookupValue>();
            if (string.IsNullOrEmpty(xml)) return list;
            string s = xml.Length > VendorCatalog.MaxPage ? xml.Substring(0, VendorCatalog.MaxPage) : xml;
            foreach (Match m in Entry.Matches(s))
            {
                var n = NameTag.Match(m.Groups["b"].Value);
                var v = ValueTag.Match(m.Groups["b"].Value);
                if (!n.Success || !v.Success) continue;
                var e = new NvidiaLookupValue();
                e.Name = WebUtility.HtmlDecode(n.Groups["n"].Value).Trim();
                e.Value = int.Parse(v.Groups["v"].Value, CultureInfo.InvariantCulture);
                var p = ParentAttr.Match(m.Groups["a"].Value);
                if (p.Success) e.ParentId = int.Parse(p.Groups["p"].Value, CultureInfo.InvariantCulture);
                list.Add(e);
            }
            return list;
        }

        static readonly Regex Trademarks = new Regex(@"\((?:R|TM|C)\)|[®™]", RX);
        static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>«NVIDIA GeForce GTX 760» y «GeForce GTX 760» son la misma placa: sin la marca, sin ® ni ™, en minúsculas.</summary>
        public static string NormalizeGpu(string name)
        {
            string s = Trademarks.Replace(name ?? "", " ");
            s = Spaces.Replace(s, " ").Trim().ToLowerInvariant();
            if (s.StartsWith("nvidia ", StringComparison.Ordinal)) s = s.Substring(7).Trim();
            return s;
        }

        /// <summary>
        /// El producto de NVIDIA que corresponde a la placa instalada, o null. Una misma placa puede figurar en la serie de
        /// escritorio y en la de portátiles («GeForce GTX 1060»): se elige según el equipo.
        /// </summary>
        public static NvidiaLookupValue FindProduct(List<NvidiaLookupValue> products, List<NvidiaLookupValue> series, string gpuName, bool laptop)
        {
            string want = NormalizeGpu(gpuName);
            if (want.Length < 4 || products == null) return null;
            var notebookSeries = new HashSet<int>();
            if (series != null)
                foreach (var s in series)
                    if (s.Name.IndexOf("Notebook", StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.IndexOf("Laptop", StringComparison.OrdinalIgnoreCase) >= 0) notebookSeries.Add(s.Value);
            NvidiaLookupValue first = null, preferred = null;
            foreach (var p in products)
            {
                if (NormalizeGpu(p.Name) != want) continue;
                if (first == null) first = p;
                if (preferred == null && notebookSeries.Contains(p.ParentId) == laptop) preferred = p;
            }
            return preferred ?? first;
        }

        /// <summary>Código de sistema de NVIDIA: Windows 11 (135), Windows 10 de 64 bits (57) o de 32 bits (56).</summary>
        public static int OsId(OsInfo os)
        {
            if (os != null && os.IsWindows11) return 135;
            string a = os != null ? BrowserCatalog.NormalizeArch(os.NativeArchitecture) : "x64";
            return a == "x86" ? 56 : 57;
        }

        /// <summary>La consulta del controlador estable (WHQL, sin betas) de ese producto para ese Windows.</summary>
        public static string LookupUrl(NvidiaLookupValue product, int osId, bool dch)
        {
            return LookupBase + "?func=DriverManualLookup&psid=" + product.ParentId.ToString(CultureInfo.InvariantCulture) +
                   "&pfid=" + product.Value.ToString(CultureInfo.InvariantCulture) + "&osID=" + osId.ToString(CultureInfo.InvariantCulture) +
                   "&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=" + (dch ? "1" : "0") + "&upCRD=0&qnf=0&sort1=0&numberOfResults=1";
        }

        static readonly Regex DriverVersion = new Regex(@"^\d{3}\.\d{2}$", RegexOptions.Compiled);

        /// <summary>La versión publicada en la respuesta de DriverManualLookup (sin betas), o null.</summary>
        public static NvidiaRelease ParseLookupResult(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            object root;
            try { root = Json.Parse(json); }
            catch (Exception ex) { Log.Quiet("Respuesta de NVIDIA", ex); return null; }
            if (Json.Str(root, "Success") != "1") return null;
            foreach (object id in Json.List(root, "IDS"))
            {
                object d = Json.Path(id, "downloadInfo");
                if (d == null || Json.Str(d, "Success") == "0") continue;
                if (Json.Str(d, "IsBeta") == "1") continue;
                string v = (Json.Str(d, "Version") ?? "").Trim();
                if (!DriverVersion.IsMatch(v)) continue;
                var r = new NvidiaRelease();
                r.Version = v;
                r.DownloadUrl = (Json.Str(d, "DownloadURL") ?? "").Trim();
                r.DetailsUrl = (Json.Str(d, "DetailsURL") ?? "").Trim();
                try { r.Name = WebUtility.UrlDecode(Json.Str(d, "Name") ?? "").Trim(); } catch { r.Name = ""; }
                r.Released = ParseDate(Json.Str(d, "ReleaseDateTime"));
                return r;
            }
            return null;
        }

        /// <summary>«Tue Jul 09, 2024» → 09/07/2024.</summary>
        public static DateTime? ParseDate(string s)
        {
            DateTime d;
            if (!string.IsNullOrEmpty(s) && DateTime.TryParseExact(s.Trim(), new[] { "ddd MMM dd, yyyy", "ddd MMM d, yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            return null;
        }

        /// <summary>
        /// Busca la última versión que publica NVIDIA para alguna de las placas (la primera que NVIDIA reconoce). <paramref name="fetch"/>
        /// descarga una dirección. null si no hay ninguna placa de NVIDIA reconocible (el programa sigue «No verificable», con su pista).
        /// </summary>
        public static NvidiaRelease Query(Func<string, string, string> fetch, List<string> gpus, bool laptop, int osId, bool dch, out string error)
        {
            error = null;
            if (gpus == null || gpus.Count == 0) return null;
            var products = ParseLookup(fetch(ProductsUrl, "text/xml"));
            if (products.Count == 0) { error = "la lista de productos de NVIDIA llegó vacía"; return null; }
            List<NvidiaLookupValue> series = null;
            foreach (string gpu in gpus)
            {
                var candidates = products.FindAll(delegate(NvidiaLookupValue p) { return NormalizeGpu(p.Name) == NormalizeGpu(gpu); });
                if (candidates.Count == 0) { Log.Info("NVIDIA: «" + gpu + "» no figura en la lista de productos de NVIDIA"); continue; }
                if (candidates.Count > 1 && series == null) series = ParseLookup(fetch(SeriesUrl, "text/xml"));
                var product = FindProduct(products, series, gpu, laptop);
                if (product == null || product.ParentId <= 0) continue;
                var rel = ParseLookupResult(fetch(LookupUrl(product, osId, dch), "application/json"));
                if (rel == null && !dch)
                {
                    // Un controlador estándar que NVIDIA ya no publica para este Windows: se prueba el DCH.
                    rel = ParseLookupResult(fetch(LookupUrl(product, osId, true), "application/json"));
                }
                if (rel == null) { error = "NVIDIA no publica un controlador para la " + product.Name + " en este Windows"; continue; }
                rel.Gpu = product.Name;
                return rel;
            }
            return null;
        }

        // ================================================================= El equipo

        /// <summary>Nombres de las placas de video de NVIDIA del equipo (Administrador de dispositivos).</summary>
        public static List<string> InstalledGpus(UpdateContext ctx)
        {
            if (GpusForTests != null) return GpusForTests();
            var list = new List<string>();
            if (ctx != null)
                foreach (var d in ctx.Drivers)
                    if (d != null && (d.DeviceClass ?? "").Equals("Display", StringComparison.OrdinalIgnoreCase) && IsNvidia(d.DeviceName, d.Manufacturer, d.Provider, d.HardwareId))
                        Add(list, d.DeviceName);
            if (list.Count > 0 || Environment.OSVersion.Platform != PlatformID.Win32NT) return list;
            // Sin el inventario de controladores (sólo se pidió «programas»): las claves de la clase «Adaptadores de pantalla».
            try
            {
                using (var cls = Reg.Open(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return list;
                    foreach (string sub in cls.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;
                        using (var k = cls.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            string desc = k.GetValue("DriverDesc") as string;
                            string provider = k.GetValue("ProviderName") as string;
                            string id = k.GetValue("MatchingDeviceId") as string;
                            if (IsNvidia(desc, null, provider, id)) Add(list, desc);
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Placas de video: " + ex.Message); }
            return list;
        }

        static void Add(List<string> list, string name)
        {
            string n = (name ?? "").Trim();
            if (n.Length > 0 && !list.Exists(delegate(string x) { return x.Equals(n, StringComparison.OrdinalIgnoreCase); })) list.Add(n);
        }

        internal static bool IsNvidia(string name, string manufacturer, string provider, string hardwareId)
        {
            return (hardwareId ?? "").IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (manufacturer ?? "").IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   ((provider ?? "").IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 && (name ?? "").IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// ¿El controlador instalado es DCH? Los DCH dejan «DCHUVen» en la clave del servicio nvlddmkm. Si no se puede leer, DCH
        /// (es el único que NVIDIA publica para Windows 10 1803 y posteriores en las placas actuales).
        /// </summary>
        public static bool InstalledIsDch()
        {
            if (DchForTests.HasValue) return DchForTests.Value;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return true;
            try
            {
                using (var k = Reg.Open(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\nvlddmkm"))
                {
                    if (k == null) return true;
                    if (k.GetValue("DCHUVen") != null) return true;
                    return k.GetValue("ImagePath") == null;   // sin servicio legible: DCH
                }
            }
            catch (Exception ex) { Log.Quiet("Tipo de controlador de NVIDIA", ex); return true; }
        }

        /// <summary>¿Es una portátil? (tiene batería). Sólo decide entre la serie de escritorio y la de portátiles de NVIDIA.</summary>
        public static bool IsLaptop()
        {
            if (LaptopForTests.HasValue) return LaptopForTests.Value;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            try
            {
                string err;
                return Wmi.TryQuery(@"root\cimv2", "SELECT Name FROM Win32_Battery", out err, 15).Count > 0;
            }
            catch (Exception ex) { Log.Quiet("Batería", ex); return false; }
        }
    }
}
