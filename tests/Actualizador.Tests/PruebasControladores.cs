// Controladores: Catálogo de Microsoft Update, pnputil y controladores del fabricante.
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System;
using Actualizador.Core;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static string Muestra(string nombre) { return Sample(nombre); }

        static void TestCatalogoLector()
        {
            bool sinResultados, completa;
            var office = CatalogRules.ParseSearch(Muestra("catalogo_busqueda_office.html"), out sinResultados, out completa);
            Check(completa && !sinResultados, "página real: completa y con resultados");
            Eq(2, office.Count, "página real: dos filas");
            Eq("d001ca72-ce16-4700-9a6d-b2bb28974002", office[0].Guid, "identificador de la fila");
            Eq("Update for Microsoft Office 2013 (KB4022233) 64-Bit Edition", office[0].Title, "título sin espacios de sobra");
            Eq("Office 2013", office[0].Products, "productos");
            Eq("Critical Updates", office[0].Classification, "clasificación");
            Check(office[0].LastUpdated.HasValue && office[0].LastUpdated.Value == new DateTime(2018, 9, 11), "fecha M/d/yyyy");
            Eq("n/a", office[0].Version, "versión (n/a fuera de controladores)");
            Eq("10.9 MB", office[0].Size, "tamaño");
            Eq(11457318L, office[0].SizeBytes, "tamaño en bytes");

            var drv = CatalogRules.ParseSearch(Muestra("catalogo_busqueda_controladores.html"), out sinResultados, out completa);
            Eq(6, drv.Count, "página de controladores: seis filas");
            Eq("Intel - Net - 23.60.0.10", drv[0].Title, "título del controlador");
            Eq("23.110.0.5", drv[1].Version, "versión del controlador");
            Check(drv[3].Products.Contains("Upgrade & Servicing"), "«&amp;» decodificado: " + drv[3].Products);

            var nada = CatalogRules.ParseSearch(Muestra("catalogo_sin_resultados.html"), out sinResultados, out completa);
            Check(nada.Count == 0 && sinResultados && completa, "sin resultados: respuesta válida y vacía");
            CatalogRules.ParseSearch("<html><body>Service unavailable</body></html>", out sinResultados, out completa);
            Check(!completa, "página a medias: no es una respuesta válida (se reintenta)");
            CatalogRules.ParseSearch("<div id=\"errorPageDisplayedError\">error</div><table id=\"ctl00_catalogBody_updateMatches\"></table>", out sinResultados, out completa);
            Check(!completa, "página de error del catálogo: no es válida");
            Check(CatalogRules.ParseSearch(null, out sinResultados, out completa).Count == 0 && !completa, "sin contenido");

            // Diálogo de descarga real.
            string dialogo = Muestra("catalogo_dialogo_descarga.html");
            var files = CatalogRules.ParseDownloadDialog(dialogo, "7f659ce1-a58f-41a8-bcf0-18fe434f0916");
            Eq(1, files.Count, "diálogo real: un archivo");
            Eq("https://catalog.s.download.windowsupdate.com/c/msdownload/update/software/crup/2019/01/oart-x-none_e3fa9c9103bc509dcdf7e971ca3cbea0c18a7b0d.cab", files[0].Url,
               "http://download.windowsupdate.com → https://catalog.s.download.windowsupdate.com");
            Eq("4/qckQO8UJ3N9+lxyjy+oMGKew0=", files[0].Digest, "huella publicada");
            Eq("oart-x-none_e3fa9c9103bc509dcdf7e971ca3cbea0c18a7b0d.cab", files[0].FileName, "nombre del archivo");
            Eq(0, CatalogRules.ParseDownloadDialog(dialogo, "00000000-0000-0000-0000-000000000000").Count, "el diálogo de OTRA actualización no se usa");
            Eq(0, CatalogRules.ParseDownloadDialog("", "7f659ce1-a58f-41a8-bcf0-18fe434f0916").Count, "diálogo vacío");

            // Sólo servidores de descarga de Microsoft, siempre por HTTPS.
            Eq("https://catalog.s.download.windowsupdate.com/d/x.cab", CatalogRules.NormalizeDownloadUrl("http://www.download.windowsupdate.com/d/x.cab"), "www. y http → host seguro");
            Eq("https://catalog.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/a/x.cab", CatalogRules.NormalizeDownloadUrl("http://catalog.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/a/x.cab"), "servidor nuevo de entrega");
            Eq("", CatalogRules.NormalizeDownloadUrl("https://download.windowsupdate.com.evil.example/x.cab"), "dominio parecido: rechazado");
            Eq("", CatalogRules.NormalizeDownloadUrl("https://example.com/download.windowsupdate.com/x.cab"), "otro servidor: rechazado");
            Eq("", CatalogRules.NormalizeDownloadUrl("ftp://download.windowsupdate.com/x.cab"), "otro protocolo: rechazado");
            Check(CatalogRules.IsCatalogPage(new Uri("https://www.catalog.update.microsoft.com/Search.aspx?q=x")), "página del catálogo");
            Check(!CatalogRules.IsCatalogPage(new Uri("http://www.catalog.update.microsoft.com/Search.aspx?q=x")), "el catálogo sólo por HTTPS");
            Check(CatalogRules.DownloadDialogBody("7f659ce1-a58f-41a8-bcf0-18fe434f0916").StartsWith("updateIDs=%5B%7B%22size%22%3A0"), "cuerpo del POST codificado");
            bool lanzo = false;
            try { CatalogRules.DownloadDialogBody("x\",\"updateID\":\"y"); } catch (ArgumentException) { lanzo = true; }
            Check(lanzo, "un identificador que no es GUID no entra en el POST");
            Check(CatalogRules.SearchUrl(@"PCI\VEN_8086&DEV_2526").EndsWith("q=PCI%5CVEN_8086%26DEV_2526"), "búsqueda codificada");
            Eq(null, CatalogRules.DetailsUrl("../x"), "detalle sólo con GUID");
        }

        static void TestCatalogoReglas()
        {
            var q = CatalogRules.QueriesFor(@"PCI\VEN_8086&DEV_2526&SUBSYS_00148086&REV_29");
            Eq(3, q.Count, "PCI: tal cual, sin REV y sin SUBSYS");
            Eq(@"PCI\VEN_8086&DEV_2526&SUBSYS_00148086", q[1].Key, "sin la revisión");
            Check(!q[0].Value && !q[1].Value && q[2].Value && q[2].Key == @"PCI\VEN_8086&DEV_2526", "la última es general (sólo el chip)");
            q = CatalogRules.QueriesFor(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_1458A0C3&REV_1003");
            Check(q.Count == 2 && !q[1].Value, "audio HD (1.5.5): sin la búsqueda por el chip, que trajo el paquete de Clevo");
            Eq(1, CatalogRules.QueriesFor(@"SWC\VEN_10EC&AID_0001").Count, "componente de software: una búsqueda");
            q = CatalogRules.QueriesFor(@"USB\VID_8087&PID_0029&REV_0001");
            Check(q.Count == 2 && !q[1].Value, "USB: sin REV, sin búsqueda general");
            Eq(0, CatalogRules.QueriesFor("").Count, "sin identificador");
            Eq(0, CatalogRules.QueriesFor("SinBarra").Count, "identificador sin enumerador");

            Check(CatalogRules.AppliesTo("Windows 10 and later drivers", 19045, false), "Windows 10 and later en 22H2");
            Check(CatalogRules.AppliesTo("Windows 10 and later drivers", 26100, false), "Windows 10 and later en Windows 11");
            Check(!CatalogRules.AppliesTo("Windows 11 Client, version 24H2 and later, Servicing Drivers", 22631, false), "24H2 en 23H2: no");
            Check(CatalogRules.AppliesTo("Windows 11 Client, version 24H2 and later, Servicing Drivers", 26100, false), "24H2 en 24H2");
            Check(CatalogRules.AppliesTo("Windows 11 Client, version 21H2 and later, Upgrade & Servicing Drivers", 22000, false), "Windows 11 21H2");
            Check(!CatalogRules.AppliesTo("Windows 10, version 1903 and later, Servicing Drivers", 17763, false), "1903 en 1809: no");
            Check(CatalogRules.AppliesTo("Windows 10, version 1903 and later, Servicing Drivers", 18362, false), "1903 en 1903");
            Check(CatalogRules.AppliesTo("Windows 10 Anniversary Update and Later Servicing Drivers", 14393, false), "Anniversary Update");
            Check(!CatalogRules.AppliesTo("Windows 10 Anniversary Update and Later Servicing Drivers", 10586, false), "Anniversary Update en 1511: no");
            Check(CatalogRules.AppliesTo("Windows - Client, version 21H2 and later, Servicing Drivers", 19044, false), "Windows - Client 21H2");
            Check(!CatalogRules.AppliesTo("Windows 7,Windows Server 2008 R2", 26100, false), "Windows 7: no");
            Check(!CatalogRules.AppliesTo("Windows 8.1 and later drivers", 26100, false), "Windows 8.1: no");
            Check(!CatalogRules.AppliesTo("Windows Server 2019 and later, Servicing Drivers", 26100, false), "Server en un Windows de escritorio: no");
            Check(CatalogRules.AppliesTo("Windows Server 2019 and later, Servicing Drivers", 17763, true), "Server en un servidor");
            Check(!CatalogRules.AppliesTo("", 26100, false), "sin productos");

            Eq("x64", CatalogRules.ParseArchitecture("<div><span id=\"ScopedViewHandler_labelArchitecture_Separator\" class=\"labelTitle\">Architecture:</span>\r\n   AMD64\r\n</div>"), "arquitectura AMD64");
            Eq("x86,x64", CatalogRules.ParseArchitecture("<div><span id=\"ScopedViewHandler_labelArchitecture_Separator\" class=\"labelTitle\">Architecture:</span> X86, AMD64 </div>"), "dos arquitecturas");
            Eq("", CatalogRules.ParseArchitecture("<div><span id=\"ScopedViewHandler_labelArchitecture_Separator\" class=\"labelTitle\">Architecture:</span> n/a </div>"), "n/a: sin datos");
            Eq("", CatalogRules.ParseArchitecture("<html></html>"), "página sin arquitectura");
            Check(CatalogRules.ArchMatches("x86,x64", "x64") && !CatalogRules.ArchMatches("arm64", "x64") && CatalogRules.ArchMatches("", "x64"), "arquitectura del equipo");

            var intel = Controlador("Intel(R) Wireless-AC 9260 160MHz", "Intel", "Net", "23.60.0.10", @"PCI\VEN_8086&DEV_2526&SUBSYS_00148086&REV_29");
            bool s1, s2;
            var filas = CatalogRules.ParseSearch(Muestra("catalogo_busqueda_controladores.html"), out s1, out s2);
            var cand = CatalogRules.NewerCandidates(filas, intel, 26100, false);
            Check(cand.Count == 2 && cand[0].Version == "23.110.0.5" && cand[1].Version == "23.110.0.5",
                  "24H2: la 23.110.0.5 (sin la de Windows 7 ni el firmware, ni las viejas): " + cand.Count);
            Eq(0, CatalogRules.NewerCandidates(filas, intel, 22631, false).Count, "23H2: la 23.110 es sólo para 24H2 → nada más nuevo");
            var sinVersion = Controlador("X", "Intel", "Net", "", @"PCI\VEN_8086&DEV_2526");
            Eq(0, CatalogRules.NewerCandidates(filas, sinVersion, 26100, false).Count, "instalada sin versión: no se compara");
            Check(CatalogRules.AnyApplicable(filas, intel, 22631, false), "hay filas para 23H2 (aunque no más nuevas)");
            Check(CatalogRules.IsFirmware(filas[5], intel), "fila de firmware");
            Check(CatalogRules.IsFirmware(null, Controlador("Sistema", "Lenovo", "Firmware", "1.0", @"UEFI\RES_{AAAA}")), "dispositivo de firmware");

            var ms = Controlador("Puerto USB", "Microsoft", "USB", "10.0.26100.1", @"USB\ROOT_HUB30");
            Check(!CatalogRules.ShouldCheck(ms), "los controladores de Microsoft (incluidos con Windows) no se comparan");
            Check(CatalogRules.ShouldCheck(intel), "los de terceros sí");
            Check(!CatalogRules.ShouldCheck(Controlador("BIOS", "AMI", "Firmware", "1.0.0.1", @"UEFI\RES_{X}")), "el firmware no");

            string tmp = Path.Combine(Path.GetTempPath(), "sp154-" + Guid.NewGuid().ToString("N") + ".cab");
            try
            {
                byte[] data = Encoding.ASCII.GetBytes("MSCF\0\0\0\0contenido de prueba");
                File.WriteAllBytes(tmp, data);
                string sha1, sha256;
                using (var h = SHA1.Create()) sha1 = Convert.ToBase64String(h.ComputeHash(data));
                using (var h = SHA256.Create()) sha256 = Convert.ToBase64String(h.ComputeHash(data));
                using (var fs = File.OpenRead(tmp))
                {
                    Check(CatalogRules.IsCab(fs), "empieza con MSCF");
                    Check(CatalogRules.DigestMatches(fs, sha1), "huella SHA-1 del catálogo");
                    Check(CatalogRules.DigestMatches(fs, sha256), "huella SHA-256");
                    Check(!CatalogRules.DigestMatches(fs, "4/qckQO8UJ3N9+lxyjy+oMGKew0="), "otra huella: no");
                    Check(!CatalogRules.DigestMatches(fs, "no es base64!"), "huella ilegible: no");
                    Check(!CatalogRules.DigestMatches(fs, ""), "sin huella: no");
                }
                File.WriteAllBytes(tmp, Encoding.ASCII.GetBytes("MZ\x90\0"));
                using (var fs = File.OpenRead(tmp)) Check(!CatalogRules.IsCab(fs), "un ejecutable no es un CAB");
            }
            finally { try { File.Delete(tmp); } catch { } }
            Check(CatalogRules.PnpUtilText(259).Contains("almacén") && CatalogRules.PnpUtilText(3010).Contains("reiniciar"), "códigos de pnputil");
        }

        /// <summary>El POST del diálogo de descarga contra un servidor local (método, tipo y cuerpo).</summary>
        static void TestCatalogoPost()
        {
            string body = CatalogRules.DownloadDialogBody("7f659ce1-a58f-41a8-bcf0-18fe434f0916");
            string recibido = null;
            var proxy = System.Net.WebRequest.DefaultWebProxy;
            System.Net.WebRequest.DefaultWebProxy = null;
            try
            {
                using (var srv = new MiniServer(delegate(int n, long from, Stream s)
                {
                    // El cuerpo lo lee MiniServer antes de responder (1.7.1): queda en srv.Bodies.
                    byte[] html = Encoding.UTF8.GetBytes("downloadInformation[0].updateID ='7f659ce1-a58f-41a8-bcf0-18fe434f0916';\n" +
                        "downloadInformation[0].files[0].url = 'http://download.windowsupdate.com/d/msdownload/update/driver/drvs/2025/03/x.cab';\n" +
                        "downloadInformation[0].files[0].digest = '4/qckQO8UJ3N9+lxyjy+oMGKew0=';\n");
                    Respond(s, "200 OK", new[] { "Content-Type: text/html; charset=utf-8", "Content-Length: " + html.Length }, html, 0, html.Length);
                }))
                {
                    string resp = Http.PostForm(srv.Url("/DownloadDialog.aspx"), body, 10000, delegate(Uri u) { return u.Host == "127.0.0.1"; });
                    lock (srv.Bodies) recibido = srv.Bodies.Count > 0 ? srv.Bodies[0] : null;
                    Eq(body, recibido, "el servidor recibe el cuerpo tal cual");
                    var files = CatalogRules.ParseDownloadDialog(resp, "7f659ce1-a58f-41a8-bcf0-18fe434f0916");
                    Check(files.Count == 1 && files[0].Url.StartsWith("https://catalog.s.download.windowsupdate.com/"), "respuesta leída y dirección segura");
                }
                bool rechazado = false;
                try { Http.PostForm("http://127.0.0.1:9/x", body, 2000, CatalogRules.IsCatalogPage); }
                catch (InvalidOperationException) { rechazado = true; }
                Check(rechazado, "un POST a un sitio no permitido no sale");
            }
            finally { System.Net.WebRequest.DefaultWebProxy = proxy; }
        }

        static DriverInfo Controlador(string nombre, string proveedor, string clase, string version, string hwid)
        {
            var d = new DriverInfo();
            d.DeviceName = nombre; d.Provider = proveedor; d.DeviceClass = clase; d.Version = version; d.HardwareId = hwid;
            d.DeviceId = hwid + @"\4&1A2B3C4D&0&00E0"; d.InfName = "oem12.inf"; d.Date = new DateTime(2024, 3, 1);
            return d;
        }

        static void TestCatalogoFlujo()
        {
            var intel = Controlador("Intel(R) Wireless-AC 9260 160MHz", "Intel", "Net", "23.60.0.10", @"PCI\VEN_8086&DEV_2526&SUBSYS_00148086&REV_29");
            var realtek = Controlador("Realtek(R) Audio", "Realtek Semiconductor Corp.", "MEDIA", "6.0.9600.1", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_1458A0C3&REV_1003");
            var ms = Controlador("Concentrador raíz USB", "Microsoft", "USB", "10.0.26100.1", @"USB\ROOT_HUB30");
            var ctx = new UpdateContext();
            ctx.Os = new OsInfo(); ctx.Os.NativeArchitecture = "x64"; ctx.Os.Major = 10; ctx.Os.Build = 26100; ctx.Os.IsWindows = true;
            ctx.IsAdmin = false;
            ctx.Drivers.AddRange(new[] { intel, realtek, ms });
            ctx.DriversLoaded = true;

            string controladores = Muestra("catalogo_busqueda_controladores.html"), vacia = Muestra("catalogo_sin_resultados.html");
            var pedidos = new List<string>();
            CatalogDriverProvider.FetchForTests = delegate(string url)
            {
                lock (pedidos) pedidos.Add(url);
                if (url.Contains("ScopedViewInline.aspx"))
                {
                    // La primera 23.110.0.5 es para ARM64; la segunda, para AMD64.
                    string arch = url.EndsWith("6b02") ? "ARM64" : "AMD64";
                    return "<div><span id=\"ScopedViewHandler_labelArchitecture_Separator\" class=\"labelTitle\">Architecture:</span> " + arch + " </div>";
                }
                if (url.EndsWith("q=" + Uri.EscapeDataString(@"PCI\VEN_8086&DEV_2526&SUBSYS_00148086"))) return controladores;
                return vacia;
            };
            try
            {
                var prov = new CatalogDriverProvider();
                Check(prov.Unavailable(ctx) == null, "disponible en Windows 10/11");
                var salida = new List<UpdateCandidate>();
                var notas = new List<string>();
                prov.Scan(ctx, salida, notas);
                Eq(2, salida.Count, "dos filas: Intel y Realtek (el de Microsoft no se compara)");
                var fi = salida.Find(delegate(UpdateCandidate c) { return c.Name.StartsWith("Intel"); });
                var fr = salida.Find(delegate(UpdateCandidate c) { return c.Name.StartsWith("Realtek"); });
                Check(fi != null && fi.Status == UpdateStatus.Disponible && fi.AvailableVersion == "23.110.0.5", "Intel: la 23.110.0.5 del catálogo");
                Check(fi.Installable && !fi.Recommended, "se puede instalar, pero no se marca sola");
                Check(fi.Kind == UpdateKind.Controlador && fi.Source == CatalogDriverProvider.SourceName, "tipo y origen");
                var plan = fi.ProviderTag as CatalogPlan;
                Check(plan != null && plan.Entry.Guid.EndsWith("6b03") && plan.Arch == "x64", "se descartó la de ARM64 y quedó la de AMD64");
                Check(fi.Detail.Contains("Windows Update no la ofrece") && fi.Detail.Contains("Revertir"), "explica por qué no la ofrece Windows Update y cómo volver atrás");
                Check(fr != null && fr.Status == UpdateStatus.AlDia && !fr.Installable && fr.Detail.Contains("no tiene este dispositivo"), "Realtek: no está en el catálogo");
                Check(!pedidos.Exists(delegate(string u) { return u.EndsWith("q=" + Uri.EscapeDataString(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897")); }),
                      "Realtek: el audio no se busca por el chip (los paquetes son por modelo de equipo)");
                Check(!pedidos.Exists(delegate(string u) { return u.EndsWith("q=" + Uri.EscapeDataString(@"PCI\VEN_8086&DEV_2526")); }),
                      "Intel: con resultados exactos, no se busca por el chip (traería controladores de otros fabricantes de equipos)");
                Check(notas.Exists(delegate(string n) { return n.Contains("1 con versión más nueva"); }), "nota con la cuenta");

                // Un dispositivo, una fila: el catálogo reemplaza al «al día» de Windows Update…
                var wuAlDia = new UpdateCandidate();
                wuAlDia.Kind = UpdateKind.Controlador; wuAlDia.Name = intel.DeviceName; wuAlDia.ProviderName = WindowsUpdateProvider.ProviderId;
                wuAlDia.Status = UpdateStatus.AlDia; wuAlDia.ProviderTag = intel;
                // …pero si Windows Update ofrece algo para el dispositivo, gana Windows Update.
                var wuOferta = new UpdateCandidate();
                wuOferta.Kind = UpdateKind.Controlador; wuOferta.Name = realtek.DeviceName; wuOferta.ProviderName = WindowsUpdateProvider.ProviderId;
                wuOferta.Status = UpdateStatus.Disponible; wuOferta.Installable = true; wuOferta.ProviderTag = realtek;
                var todas = new List<UpdateCandidate>(salida);
                todas.Add(wuAlDia); todas.Add(wuOferta);
                UpdateManager.Deduplicate(todas);
                Check(!todas.Contains(wuAlDia) && todas.Contains(fi), "Intel: queda la fila del catálogo");
                Check(todas.Contains(wuOferta) && !todas.Contains(fr), "Realtek: queda la oferta de Windows Update");

                // Hallazgo aparte, informativo: no es «Windows Update ofrece».
                var rep = new UpdateSession();
                rep.Updates.Add(fi);

                // Instalar: sin administrador no se intenta.
                var cambios = 0;
                prov.Install(ctx, new List<UpdateCandidate> { fi }, delegate(UpdateCandidate c) { cambios++; });
                Check(fi.Status == UpdateStatus.Fallida && fi.Result.Contains("administrador"), "sin administrador: " + fi.Result);

                // Como administrador: el diálogo de OTRA actualización o de otra arquitectura no se usa (antes de descargar nada).
                ctx.IsAdmin = true;
                CatalogDriverProvider.DownloadDialogForTests = delegate(string body) { return Muestra("catalogo_dialogo_descarga.html"); };
                fi.Status = UpdateStatus.Disponible;
                prov.Install(ctx, new List<UpdateCandidate> { fi }, delegate(UpdateCandidate c) { });
                Check(fi.Status == UpdateStatus.Fallida && fi.Result.Contains("no publicó un paquete"), "diálogo de otra actualización: " + fi.Result);
                CatalogDriverProvider.DownloadDialogForTests = delegate(string body)
                {
                    return "downloadInformation[0].updateID ='" + plan.Entry.Guid + "';\ndownloadInformation[0].files[0].url = 'http://download.windowsupdate.com/d/msdownload/update/driver/drvs/2025/03/x_arm64.cab';\n" +
                           "downloadInformation[0].files[0].digest = '4/qckQO8UJ3N9+lxyjy+oMGKew0=';\ndownloadInformation[0].files[0].architectures = 'ARM64';\n";
                };
                fi.Status = UpdateStatus.Disponible;
                prov.Install(ctx, new List<UpdateCandidate> { fi }, delegate(UpdateCandidate c) { });
                Check(fi.Status == UpdateStatus.Fallida && fi.Result.Contains("otra arquitectura"), "paquete de otra arquitectura: " + fi.Result);

                // Desactivado en Opciones o con --sin-fabricantes; y antes de Windows 10, no existe.
                ctx.Options.QueryVendors = false;
                Check(prov.Unavailable(ctx) != null, "se desactiva con --sin-fabricantes");
                ctx.Options.QueryVendors = true;
                ctx.Os.Major = 6; ctx.Os.Minor = 1;
                Check(prov.Unavailable(ctx) != null, "Windows 7: no");
            }
            finally { CatalogDriverProvider.FetchForTests = null; CatalogDriverProvider.DownloadDialogForTests = null; }
        }
        const string PnpCrashDefender = "Utilidad PnP de Microsoft    Agregando paquete de controladores:  amdfendr.inf  El paquete de controladores se agreg¾ correctamente.  Nombre publicado:         oem130.inf  Paquete de controladores instalado en el dispositivo: ROOT\\AMDLOG\\0000    Paquetes de controladores totales:  1  Paquetes de controladores agregados:  1";
        const string PnpControllerEmulation = "Utilidad PnP de Microsoft    Agregando paquete de controladores:  amdxe.inf  El paquete de controladores se agreg¾ correctamente.  Nombre publicado:         oem129.inf  El paquete de controladores estß actualizado en el dispositivo: ROOT\\AMDXE\\0000    Paquetes de controladores totales:  1  Paquetes de controladores agregados:  0";
        const string PnpRealtekHsa = "Utilidad PnP de Microsoft    Agregando paquete de controladores:  RealtekService.inf  El paquete de controladores se agreg¾ correctamente. (ya existe en el sistema)  Nombre publicado:         oem136.inf  El paquete de controladores estß actualizado en el dispositivo: SWD\\DRIVERENUM\\{c3a63edd-2d27-4b66-b155-5e94b43d926a}#RealtekSRV&6&38d76992&0    Agregando paquete de controladores:  RealtekHSA_398\\RealtekHSA.inf  El paquete de controladores se agreg¾ correctamente. (ya existe en el sistema)  Nombre publicado:         oem137.inf  El paquete de controladores estß actualizado en el dispositivo: SWD\\DRIVERENUM\\{c3a63edd-2d27-4b66-b155-5e94b43d926a}#RealtekHSA&6&38d76992&0    Paquetes de controladores totales:  2  Paquetes de controladores agregados:  0";
        const string PnpRealtekClevo = "Utilidad PnP de Microsoft    Agregando paquete de controladores:  HDXACPClevo.inf  El paquete de controladores se agreg¾ correctamente.  Nombre publicado:         oem138.inf    Agregando paquete de controladores:  HDXClevo.inf  El paquete de controladores se agreg¾ correctamente.  Nombre publicado:         oem139.inf    Agregando paquete de controladores:  HDXSSTClevo.inf  El paquete de controladores se agreg¾ correctamente.  Nombre publicado:         oem140.inf    Agregando paquete de controladores:  HDXWHITE.inf  El paquete de controladores se agreg¾ correctamente.  Nombre publicado:         oem141.inf    Paquetes de controladores totales:  4  Paquetes de controladores agregados:  4";
        const string PnpIntelWifi = "Utilidad PnP de Microsoft    Agregando paquete de controladores:  Netwtw08.INF  El paquete de controladores se agreg¾ correctamente.  Nombre publicado:         oem134.inf  Paquete de controladores instalado en el dispositivo: PCI\\VEN_8086&DEV_2526&SUBSYS_00148086&REV_29\\6&354f3f5a&0&0010020B    Agregando paquete de controladores:  Netwtw6e.INF  El paquete de controladores se agreg¾ correctamente.  Nombre publicado:         oem135.inf    Paquetes de controladores totales:  2  Paquetes de controladores agregados:  2";

        static void TestPnpUtilResultados()
        {
            var O = CatalogRules.InstallOutcome.Actualizado;
            var R = CatalogRules.InstallOutcome.Reinicio;
            var N = CatalogRules.InstallOutcome.NoAplicado;

            // AMD Crash Defender: pnputil 0, «instalado en el dispositivo». Con el registro al día, actualizado.
            Eq(O, CatalogRules.Interpret(0, PnpCrashDefender, @"ROOT\AMDLOG\0000", "26.10.0.2", "26.10.0.2"), "Crash Defender: el registro ya informa la nueva");
            // Si todavía figura la anterior (lo que leía WMI): se instaló y se aplica al reiniciar, no «falló».
            Eq(R, CatalogRules.Interpret(0, PnpCrashDefender, @"ROOT\AMDLOG\0000", "25.10.0.7", "26.10.0.2"), "Crash Defender: instalado en el dispositivo, pendiente de reinicio");
            Eq(R, CatalogRules.Interpret(0, PnpIntelWifi, @"PCI\VEN_8086&DEV_2526&SUBSYS_00148086&REV_29\6&354f3f5a&0&0010020B", "24.30.1.1", "24.40.0.4"), "Intel Wi-Fi: igual");
            // AMD Controller Emulation: 259, «está actualizado en el dispositivo» → Windows se quedó con el suyo.
            Eq(N, CatalogRules.Interpret(259, PnpControllerEmulation, @"ROOT\AMDXE\0000", "25.10.0.1", "25.20.0.11"), "Controller Emulation: no aplicado (aunque nombre al dispositivo)");
            // Realtek HSA: 259, pero el paquete anterior de la misma tanda ya lo había actualizado.
            Eq(O, CatalogRules.Interpret(259, PnpRealtekHsa, @"SWD\DRIVERENUM\{c3a63edd-2d27-4b66-b155-5e94b43d926a}#RealtekHSA&6&38d76992&0", "11.0.6000.398", "11.0.6000.398"),
               "Realtek HSA: ya actualizado por el paquete anterior");
            // Realtek HD Audio con el paquete de Clevo: pnputil 0 pero no nombra al dispositivo → no aplicado.
            Eq(N, CatalogRules.Interpret(0, PnpRealtekClevo, @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_1458A0C3&REV_1003\4&2B0C2D1&0&0001", "6.0.9963.1", "6.0.10016.1"), "Clevo: no aplicado");
            // Intel Bluetooth: 3010.
            Eq(R, CatalogRules.Interpret(3010, "Es necesario reiniciar el sistema", @"USB\VID_8087&PID_0029\5&1", "24.30.1.1", "24.40.10.8"), "Bluetooth: reinicio");
            Eq(N, CatalogRules.Interpret(0, PnpCrashDefender, "", "25.10.0.7", "26.10.0.2"), "sin identificador de dispositivo: no se adivina");

            Check(CatalogRules.IsOemInf("oem130.inf") && CatalogRules.IsOemInf("OEM7.INF"), "paquetes de terceros");
            Check(!CatalogRules.IsOemInf("machine.inf") && !CatalogRules.IsOemInf("oem1.inf.bak") && !CatalogRules.IsOemInf("..\\oem1.inf") && !CatalogRules.IsOemInf(null), "nada más");
        }

        static void TestFechaDelControlador()
        {
            // AMD Controller Emulation: la 25.20.0.11 se publicó el 08/09/2025 y la instalada es del 23/09/2025.
            var instalado = Controlador("AMD Controller Emulation", "AMD", "System", "25.10.0.1", @"ROOT\AMDXE");
            instalado.Date = new DateTime(2025, 9, 23);
            var vieja = new CatalogEntry(); vieja.Version = "25.20.0.11"; vieja.Products = "Windows 10 and later drivers"; vieja.LastUpdated = new DateTime(2025, 9, 8); vieja.Title = "AMD System Driver Update (25.20.0.11)";
            var nueva = new CatalogEntry(); nueva.Version = "25.20.0.12"; nueva.Products = "Windows 10 and later drivers"; nueva.LastUpdated = new DateTime(2025, 10, 2); nueva.Title = "AMD System Driver Update (25.20.0.12)";
            var mismoDia = new CatalogEntry(); mismoDia.Version = "25.20.0.13"; mismoDia.Products = "Windows 10 and later drivers"; mismoDia.LastUpdated = new DateTime(2025, 9, 22); mismoDia.Title = "x";
            Check(CatalogRules.OlderThanInstalled(vieja, instalado), "publicada antes de la fecha del instalado: Windows la rechaza");
            Check(!CatalogRules.OlderThanInstalled(nueva, instalado), "publicada después: puede servir");
            Check(!CatalogRules.OlderThanInstalled(mismoDia, instalado), "un día de margen por la zona horaria");
            var cand = CatalogRules.NewerCandidates(new List<CatalogEntry> { vieja, nueva, mismoDia }, instalado, 26100, false);
            Check(cand.Count == 2 && !cand.Contains(vieja), "la de fecha vieja no se ofrece aunque su número sea mayor");
            instalado.Date = null;
            Check(!CatalogRules.OlderThanInstalled(vieja, instalado), "sin fecha del instalado: no se descarta");

            Check(DriverRegistry.ParseDriverDate("9-23-2025") == new DateTime(2025, 9, 23), "fecha como la escribe Windows");
            Check(DriverRegistry.ParseDriverDate("10/03/2026") == new DateTime(2026, 10, 3), "con barras");
            Check(DriverRegistry.ParseDriverDate("basura") == null, "ilegible");
            Check(DriverRegistry.IsSafeDeviceId(@"PCI\VEN_8086&DEV_2526&SUBSYS_00148086&REV_29\6&354f3f5a&0&0010020B"), "identificador normal");
            Check(DriverRegistry.IsSafeDeviceId(@"SWD\DRIVERENUM\{c3a63edd-2d27-4b66-b155-5e94b43d926a}#RealtekHSA&6&38d76992&0"), "dispositivo de software");
            foreach (string malo in new[] { "", null, @"..\..\SAM", @"\ROOT\X", @"ROOT\\X", "ROOT/X", "ROOT\\X\n" })
                Check(!DriverRegistry.IsSafeDeviceId(malo), "identificador rechazado: " + (malo ?? "null"));
            Check(DriverRegistry.Read(@"..\x") == null, "no se lee con un identificador inseguro");
        }

        static void TestPnpUtilSynaptics()
        {
            var R = CatalogRules.InstallOutcome.Reinicio;
            var N = CatalogRules.InstallOutcome.NoAplicado;
            var O = CatalogRules.InstallOutcome.Actualizado;
            // Un INF se instaló en el dispositivo y otro de la tanda dijo «está actualizado»: pnputil devolvió 259.
            Check(CatalogRules.InstalledOnDevice(PnpSynaptics, SmbusDevice), "Synaptics: instalado en el dispositivo");
            Eq(R, CatalogRules.Interpret(259, PnpSynaptics, SmbusDevice, "10.1.1.1", "19.5.45.30"), "Synaptics: 259 pero instalado → reinicio");
            Eq(O, CatalogRules.Interpret(259, PnpSynaptics, SmbusDevice, "19.5.45.30", "19.5.45.30"), "Synaptics: el registro ya informa la nueva");
            // Sólo «está actualizado en el dispositivo» (también con la decodificación vieja, «estß»): no aplicado.
            Check(!CatalogRules.InstalledOnDevice(PnpControllerEmulation, @"ROOT\AMDXE\0000"), "Controller Emulation: sólo «está actualizado»");
            Eq(N, CatalogRules.Interpret(259, PnpControllerEmulation, @"ROOT\AMDXE\0000", "25.10.0.1", "25.20.0.11"), "Controller Emulation: sigue no aplicado");
            Check(CatalogRules.InstalledOnDevice("Driver package installed on device: ROOT\\X\\0000", @"ROOT\X\0000"), "en inglés");
            Check(!CatalogRules.InstalledOnDevice("Driver package is up-to-date on device: ROOT\\X\\0000", @"ROOT\X\0000"), "en inglés, actualizado");
            Check(!CatalogRules.InstalledOnDevice("El paquete de controladores está actualizado en el dispositivo:   ROOT\\X\\0000", @"ROOT\X\0000"), "espacios de más antes del dispositivo");
            Check(!CatalogRules.InstalledOnDevice(PnpSynaptics, @"PCI\VEN_10EC&DEV_8168\1"), "otro dispositivo");
            Check(!CatalogRules.InstalledOnDevice(null, SmbusDevice) && !CatalogRules.InstalledOnDevice(PnpSynaptics, ""), "sin datos");
            Eq(N, CatalogRules.Interpret(1, PnpSynaptics, SmbusDevice, "10.1.1.1", "19.5.45.30"), "código de error: no se adivina");
        }
        const string PnpIntelUhd = "Utilidad PnP de Microsoft    Agregando paquete de controladores:  cui_dch.inf  El paquete de controladores se agregó correctamente.  Nombre publicado:         oem55.inf    Agregando paquete de controladores:  HdBusExt.inf  El paquete de controladores se agregó correctamente.  Nombre publicado:         oem56.inf    Agregando paquete de controladores:  igcc_dch.inf  El paquete de controladores se agregó correctamente.  Nombre publicado:         oem57.inf    Agregando paquete de controladores:  iigd_dch.inf  El paquete de controladores se agregó correctamente.  Nombre publicado:         oem58.inf  El paquete de controladores se agregó correctamente.  Nombre publicado:         oem59.inf  El paquete de controladores está actualizado en el dispositivo: PCI\\VEN_8086&DEV_3E91&SUBSYS_3E911849&REV_00\\3&11583659&0&10    Agregando paquete de controladores:  MSHdaDac.inf  El paquete de controladores se agregó correctamente. (ya existe en el sistema)  Nombre publicado:         oem59.inf    Paquetes de controladores totales:  9  Paquetes de controladores agregados:  7";
        const string UhdHwid = @"PCI\VEN_8086&DEV_3E91&SUBSYS_3E911849&REV_00";

        static void TestControladorDelFabricante()
        {
            // Lo que dijo pnputil: no aplicado (sólo «está actualizado en el dispositivo»).
            Eq(CatalogRules.InstallOutcome.NoAplicado, CatalogRules.Interpret(259, PnpIntelUhd, UhdHwid + @"\3&11583659&0&10", "26.20.100.7584", "31.0.101.2141"), "Intel UHD: no aplicado");

            // Cuándo Windows se va a quedar con el instalado.
            Check(CatalogRules.WindowsPrefersInstalled(@"pci\ven_8086&dev_3e91&subsys_3e911849", true), "del fabricante del equipo, y lo nuevo sólo por el chip");
            Check(!CatalogRules.WindowsPrefersInstalled(@"pci\ven_8086&dev_3e91&subsys_3e911849", false), "lo nuevo también por el modelo exacto: compiten por fecha");
            Check(!CatalogRules.WindowsPrefersInstalled(@"pci\ven_8086&dev_3e91", true), "el instalado también es genérico: compiten por fecha");
            Check(!CatalogRules.WindowsPrefersInstalled("", true) && !CatalogRules.WindowsPrefersInstalled(null, true), "sin dato: no se supone");

            // Identificadores para forzar: del más específico al más general.
            var ids = CatalogRules.ForceIds(UhdHwid);
            // 2.0.25: también el chip con su revisión (como declaran AMD y ATI sus placas), antes del chip solo.
            Check(ids.Count == 4 && ids[0] == UhdHwid && ids[1] == @"PCI\VEN_8086&DEV_3E91&SUBSYS_3E911849" && ids[2] == @"PCI\VEN_8086&DEV_3E91&REV_00" && ids[3] == @"PCI\VEN_8086&DEV_3E91",
                  "completo, sin REV, chip con REV, sin SUBSYS: " + string.Join(" | ", ids.ToArray()));
            var audio = CatalogRules.ForceIds(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_1458A0C3&REV_1003");
            Check(audio.Count == 4 && audio[2] == @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&REV_1003" && audio[3] == @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897", "audio: también el genérico (sólo para elegir el INF)");
            Check(CatalogRules.ForceIds("").Count == 0 && CatalogRules.ForceIds(null).Count == 0, "sin identificador");

            // El identificador en el INF, como palabra.
            string inf = "[Intel.NTamd64.10.0]\r\n%iCFLDTGT2% = iCFLD_w10_DS, PCI\\VEN_8086&DEV_3E92\r\n%iCFLDTGT1% = iCFLD_w10_DS, pci\\ven_8086&dev_3e91\r\n";
            Eq(@"PCI\VEN_8086&DEV_3E91", CatalogRules.InfMatchingId(inf, ids), "el genérico, sin importar mayúsculas");
            Eq(null, CatalogRules.InfMatchingId("x = y, PCI\\VEN_8086&DEV_3E912\r\n", ids), "«DEV_3E912» no es «DEV_3E91»");
            Eq(null, CatalogRules.InfMatchingId("x = y, PCI\\VEN_8086&DEV_3E91&SUBSYS_00001028\r\n", ids), "el de otro fabricante de equipos no cuenta");
            Eq(@"PCI\VEN_8086&DEV_3E91&SUBSYS_3E911849", CatalogRules.InfMatchingId("a = b, PCI\\VEN_8086&DEV_3E91\r\nc = d, PCI\\VEN_8086&DEV_3E91&SUBSYS_3E911849\r\n", ids), "el más específico gana");
            Eq(null, CatalogRules.InfMatchingId(null, ids), "sin texto");

            // INF en UTF-16 (con y sin BOM), UTF-8 y ANSI.
            string texto = "PCI\\VEN_8086&DEV_3E91";
            var conBom = new List<byte> { 0xFF, 0xFE }; conBom.AddRange(Encoding.Unicode.GetBytes(texto));
            Eq(texto, CatalogRules.InfText(conBom.ToArray()), "UTF-16 con BOM");
            Eq(texto, CatalogRules.InfText(Encoding.Unicode.GetBytes(texto)), "UTF-16 sin BOM");
            Eq(texto, CatalogRules.InfText(Encoding.ASCII.GetBytes(texto)), "ANSI");
            Eq("", CatalogRules.InfText(null), "vacío");

            // Elegir el INF del paquete extraído.
            string dir = Path.Combine(Path.GetTempPath(), "sp-inf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "Graphics"));
            try
            {
                File.WriteAllText(Path.Combine(dir, "cui_dch.inf"), "[Version]\r\nClass=SoftwareComponent\r\n[M]\r\nx = y, SWC\\VEN_8086&AID_0001\r\n");
                var uni = new List<byte> { 0xFF, 0xFE }; uni.AddRange(Encoding.Unicode.GetBytes("[Intel.NTamd64]\r\n%iCFL% = iCFL_w10_DS, PCI\\VEN_8086&DEV_3E91\r\n"));
                File.WriteAllBytes(Path.Combine(dir, "Graphics", "iigd_dch.inf"), uni.ToArray());
                string id;
                string elegido = CatalogDriverProvider.PickForceInf(dir, UhdHwid, out id);
                Check(elegido != null && elegido.EndsWith("iigd_dch.inf") && id == @"PCI\VEN_8086&DEV_3E91", "el INF de video (UTF-16, en una subcarpeta): " + elegido + " / " + id);
                Check(CatalogDriverProvider.PickForceInf(dir, @"PCI\VEN_10DE&DEV_1C82&SUBSYS_00000000", out id) == null && id == null, "otro dispositivo: ninguno");
                Check(CatalogDriverProvider.PickForceInf(Path.Combine(dir, "no-existe"), UhdHwid, out id) == null, "carpeta inexistente");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            // Mensajes de error de la instalación forzada.
            Check(CatalogRules.ForceErrorText(unchecked((int)0xE000020B)).Contains("no corresponde"), "ERROR_NO_SUCH_DEVINST");
            Check(CatalogRules.ForceErrorText(unchecked((int)0xE0000235)).Contains("64 bits"), "ERROR_IN_WOW64");
            Check(CatalogRules.ForceErrorText(5).Contains("administrador"), "acceso denegado");
            Check(CatalogRules.ForceErrorText(1234).Contains("0x"), "otro código: en hexadecimal");
        }

        static void TestControladorDelFabricanteEnLaBusqueda()
        {
            // El dispositivo tiene el controlador del fabricante del equipo (SUBSYS) y el catálogo sólo lo tiene por el chip.
            var gpu = Controlador("Intel(R) Wireless-AC 9260 160MHz", "Intel", "Net", "23.60.0.10", @"PCI\VEN_8086&DEV_2526&SUBSYS_12345678&REV_29");
            var ctx = new UpdateContext();
            ctx.Os = new OsInfo(); ctx.Os.NativeArchitecture = "x64"; ctx.Os.Major = 10; ctx.Os.Build = 26100; ctx.Os.IsWindows = true;
            ctx.IsAdmin = true;
            ctx.Drivers.Add(gpu);
            ctx.DriversLoaded = true;
            string controladores = Muestra("catalogo_busqueda_controladores.html"), vacia = Muestra("catalogo_sin_resultados.html");
            CatalogDriverProvider.FetchForTests = delegate(string url)
            {
                if (url.Contains("ScopedViewInline.aspx")) return "<div><span id=\"ScopedViewHandler_labelArchitecture_Separator\" class=\"labelTitle\">Architecture:</span> AMD64 </div>";
                if (url.EndsWith("q=" + Uri.EscapeDataString(@"PCI\VEN_8086&DEV_2526"))) return controladores;
                return vacia;
            };
            string coincidencia = @"pci\ven_8086&dev_2526&subsys_12345678";
            DriverRegistry.ReadForTests = delegate(string id)
            {
                var info = new DriverRegistryInfo(); info.Version = "23.60.0.10"; info.MatchingDeviceId = coincidencia;
                return info;
            };
            try
            {
                var salida = new List<UpdateCandidate>();
                new CatalogDriverProvider().Scan(ctx, salida, new List<string>());
                var fila = salida.Find(delegate(UpdateCandidate c) { return c.Status == UpdateStatus.Disponible; });
                var plan = fila != null ? fila.ProviderTag as CatalogPlan : null;
                Check(plan != null && plan.PreferOem && !plan.Force && plan.InstalledMatch == coincidencia, "se anticipa que Windows prefiere el del fabricante");
                Check(fila != null && fila.Detail.Contains("se comprueba") && !fila.Detail.Contains("FORZADA") && !fila.Recommended, "lo explica en el detalle y no se marca sola: " + (fila != null ? fila.Detail : ""));

                // 2.0.26: ya no se pregunta si forzarla (sin forzar no pasaba nada; forzada, Windows Update la cambiaba al reiniciar): se comprueba sola.
                Check(UpdateManager.NeedingForce(new List<UpdateCandidate> { fila }).Count == 0, "no se pregunta si forzarla");
                plan.ForceOnly = true;
                var forzables = UpdateManager.NeedingForce(new List<UpdateCandidate> { fila });
                Check(forzables.Count == 1, "sólo lo que ya se sabe que se aplica únicamente forzado");
                UpdateManager.AllowForce(forzables);
                Check(plan.Force && UpdateManager.NeedingForce(new List<UpdateCandidate> { fila }).Count == 0, "aceptada: no se vuelve a preguntar");
                Check(UpdateManager.NeedingForce(null).Count == 0, "sin selección");

                // El instalado también es genérico: no se anticipa nada (compiten por fecha y versión).
                coincidencia = @"pci\ven_8086&dev_2526";
                salida.Clear();
                new CatalogDriverProvider().Scan(ctx, salida, new List<string>());
                fila = salida.Find(delegate(UpdateCandidate c) { return c.Status == UpdateStatus.Disponible; });
                plan = fila != null ? fila.ProviderTag as CatalogPlan : null;
                Check(plan != null && !plan.PreferOem && !fila.Detail.Contains("FORZADA"), "instalado genérico: instalación normal");
                Check(UpdateManager.NeedingForce(new List<UpdateCandidate> { fila }).Count == 0, "y no se pregunta");
            }
            finally { CatalogDriverProvider.FetchForTests = null; DriverRegistry.ReadForTests = null; }
        }

        static void TestControladores163()
        {
            // Versiones de Windows 11 que este programa todavía no conoce: no se ofrecen.
            Check(!CatalogRules.AppliesTo("Windows 11 Client, version 26H1 and later, Servicing Drivers", 26100, false), "26H1 en 24H2: no");
            Check(CatalogRules.AppliesTo("Windows 11 Client, version 26H1 and later, Servicing Drivers", 28000, false), "26H1 en 26H1");
            Check(!CatalogRules.AppliesTo("Windows 11 Client, version 27H2 and later, Servicing Drivers", 26200, false), "versión desconocida: no");
            Check(CatalogRules.AppliesTo("Windows 11 Client, version 25H2 and later, Servicing Drivers", 26200, false), "25H2 en 25H2");
            Check(CatalogRules.AppliesTo("Windows 11 Client, version 21H2 and later, Servicing Drivers", 22000, false), "21H2 explícito");
            Check(CatalogRules.AppliesTo("Windows 11", 22000, false), "Windows 11 sin versión");
            Check(!CatalogRules.AppliesTo("Windows - Client, version 24H2 and later, Servicing Drivers", 19045, false), "Client 24H2 en Windows 10: no");
            Check(CatalogRules.AppliesTo("Windows - Client, version 24H2 and later, Servicing Drivers", 26100, false), "Client 24H2 en 24H2");
            Check(!CatalogRules.AppliesTo("Windows - Client, version 26H1 and later, Servicing Drivers", 26100, false), "Client 26H1 en 24H2: no (antes sí)");
            Check(CatalogRules.AppliesTo("Windows - Client, version 22H2 and later, Servicing Drivers", 19045, false), "Client 22H2 en Windows 10 22H2");
            Check(CatalogRules.AppliesTo("Windows 10 and later drivers,Windows 11 Client, version 26H1 and later, Servicing Drivers", 19041, false), "con dos productos vale el menor");

            // Windows Server por versión.
            Check(!CatalogRules.AppliesTo("Windows Server 2019 and later, Servicing Drivers", 14393, true), "Server 2019 en 2016: no");
            Check(CatalogRules.AppliesTo("Windows Server 2016 and later, Servicing Drivers", 20348, true), "Server 2016 and later en 2022");
            Check(!CatalogRules.AppliesTo("Windows 7,Windows Server 2008 R2", 20348, true), "Server 2008 R2 en 2022: no (antes sí)");
            Check(CatalogRules.AppliesTo("Windows Server 2022", 20348, true), "Server 2022 en 2022");
            Check(CatalogRules.AppliesTo("Windows Server", 17763, true), "«Windows Server» sin año: como antes");
            Check(CatalogRules.AppliesTo("Windows 10 and later drivers", 17763, true), "producto de escritorio en un servidor: como antes");

            // pnputil en todas las versiones de Windows 10.
            Check(CatalogRules.LegacyPnpUtil(10240) && CatalogRules.LegacyPnpUtil(10586), "1507 y 1511: pnputil viejo");
            Check(!CatalogRules.LegacyPnpUtil(14393) && !CatalogRules.LegacyPnpUtil(26100) && !CatalogRules.LegacyPnpUtil(0), "1607 en adelante: moderno");
            Eq("-i -a \"C:\\d\\a b.inf\"", CatalogRules.AddDriverArgs(10586, @"C:\d\a b.inf"), "agregar con el pnputil viejo");
            Eq("/add-driver \"C:\\d\\*.inf\" /subdirs /install", CatalogRules.AddDriverArgs(19045, @"C:\d\*.inf"), "agregar con el moderno");
            Eq("-d oem12.inf", CatalogRules.DeleteDriverArgs(10240, "oem12.inf"), "quitar con el viejo");
            Eq("/delete-driver oem12.inf", CatalogRules.DeleteDriverArgs(22631, "oem12.inf"), "quitar con el moderno");

            // Un error de pnputil sólo cuenta si el dispositivo no tomó el controlador.
            Check(!CatalogRules.PnpUtilFailed(1, CatalogRules.InstallOutcome.Actualizado), "código 1 pero el dispositivo ya tiene la nueva: correcto");
            Check(CatalogRules.PnpUtilFailed(1, CatalogRules.InstallOutcome.NoAplicado), "código 1 y no se aplicó: error");
            Check(!CatalogRules.PnpUtilFailed(259, CatalogRules.InstallOutcome.NoAplicado), "259: lo explica el resultado, no es error de pnputil");
            Eq(CatalogRules.InstallOutcome.Actualizado, CatalogRules.Interpret(1, "", @"PCI\VEN_10EC&DEV_8168\4&1", "10.75.0.0", "10.75.0.0"), "Interpret con código 1 y la versión al día");
            Check(CatalogRules.PnpUtilRank(0) < CatalogRules.PnpUtilRank(3010) && CatalogRules.PnpUtilRank(3010) < CatalogRules.PnpUtilRank(259) && CatalogRules.PnpUtilRank(259) < CatalogRules.PnpUtilRank(1), "orden de resultados");
            Check(CatalogRules.PnpUtilText(unchecked((int)0xE0000228)).Contains("compatible"), "texto de ERROR_NO_COMPAT_DRIVERS");

            // Windows Update: la versión que quedó se lee del registro.
            Check(WindowsUpdateProvider.DriverStillOld("31.0.101.2141", "26.20.100.7584"), "el dispositivo sigue con la vieja");
            Check(!WindowsUpdateProvider.DriverStillOld("31.0.101.2141", "31.0.101.2141") && !WindowsUpdateProvider.DriverStillOld("", "1.0") && !WindowsUpdateProvider.DriverStillOld("1.0", ""), "al día o sin dato");
            Eq("31.0.101.2141", WindowsUpdateProvider.DriverVerdictAfterInstall("31.0.101.2141", "31.0.101.2141", "31.0.101.2141 (29/03/2026)"), "la del registro");
            Eq("31.0.101.2141 (29/03/2026)", WindowsUpdateProvider.DriverVerdictAfterInstall("31.0.101.2141", "", "31.0.101.2141 (29/03/2026)"), "sin registro: la ofrecida");

            // Ciclo de vida de cada Windows 10.
            Eq(new DateTime(2023, 6, 13), Lifecycle.Evaluate(Os(10, 0, 19044, "Professional", false)).EndOfSupport, "Windows 10 21H2 Pro");
            Eq(new DateTime(2024, 6, 11), Lifecycle.Evaluate(Os(10, 0, 19044, "Enterprise", false)).EndOfSupport, "Windows 10 21H2 Enterprise");
            Eq(new DateTime(2023, 5, 9), Lifecycle.Evaluate(Os(10, 0, 19042, "Education", false)).EndOfSupport, "Windows 10 20H2 Education");
            Eq(new DateTime(2025, 10, 14), Lifecycle.Evaluate(Os(10, 0, 10240, "EnterpriseS", false)).EndOfSupport, "Windows 10 LTSB 2015");
            Eq(new DateTime(2025, 10, 14), Lifecycle.Evaluate(Os(10, 0, 19045, "Core", false)).EndOfSupport, "Windows 10 22H2 Home");
            Eq(new DateTime(2026, 10, 13), Lifecycle.Evaluate(Os(10, 0, 26100, "Professional", false)).EndOfSupport, "Windows 11 24H2 Pro");
        }

        static void TestCatalogoLlaves()
        {
            // El catálogo responde EN BLANCO a estas búsquedas (verificado contra el sitio el 12/10/2026).
            Eq(0, CatalogRules.QueriesFor(@"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_VID&0001005d_PID&223b").Count, "Bluetooth con llaves: no se busca");
            Eq(0, CatalogRules.QueriesFor(@"{5d624f94-8850-40c3-a3fa-a4fd2080baf3}\vwifimp_wfd").Count, "dispositivo de software con llaves: no se busca");
            var bt = Controlador("Auriculares Bluetooth", "Fabricante", "Bluetooth", "1.0.0.1", @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_VID&0001005d_PID&223b");
            Check(!CatalogRules.ShouldCheck(bt), "no se compara con el catálogo (antes: «el catálogo no devolvió una respuesta válida»)");
            Eq(2, CatalogRules.QueriesFor(@"PCI\VEN_8086&DEV_2526&SUBSYS_00148086").Count, "los identificadores comunes se siguen buscando");
            Check(CatalogRules.QueriesFor(@"ROOT\VMWVMCIHOSTDEV").Count > 0, "ROOT\\… sí (el catálogo los tiene)");

            // Página en blanco simulada para un identificador con llaves: ni siquiera se consulta.
            var ctx = new UpdateContext();
            ctx.Os = Os(10, 0, 26300, "Professional", false);
            ctx.Drivers.Add(bt);
            int consultas = 0;
            CatalogDriverProvider.FetchForTests = delegate(string url) { consultas++; return "<html><body>If you have a pop-up blocker enabled…</body></html>"; };
            var output = new List<UpdateCandidate>();
            var notes = new List<string>();
            try { new CatalogDriverProvider().Scan(ctx, output, notes); }
            finally { CatalogDriverProvider.FetchForTests = null; }
            Eq(0, consultas, "sin consultas al catálogo para ese dispositivo");
            Check(!notes.Exists(delegate(string n) { return n.Contains("No se pudo consultar el catálogo"); }), "sin la nota de error");
        }

        static void TestSeleccionYControladores()
        {
            // --- Controladores: nunca degradar (caso real: Intel UHD Graphics 630)
            var instalada = new DateTime(2025, 5, 20);
            Eq(WindowsUpdateProvider.DriverVerdict.Older,
               WindowsUpdateProvider.CompareDriver("24.20.100.6346", new DateTime(2018, 9, 1), "31.0.101.2140", instalada),
               "Intel UHD 630: el 24.20 de 2018 es MÁS VIEJO que el 31.0 de 2025");
            Eq(WindowsUpdateProvider.DriverVerdict.Older,
               WindowsUpdateProvider.CompareDriver("26.20.100.7584", new DateTime(2019, 11, 1), "31.0.101.2140", instalada),
               "Intel UHD 630: el 26.20 de 2019 también es más viejo");
            Eq(WindowsUpdateProvider.DriverVerdict.Newer,
               WindowsUpdateProvider.CompareDriver("31.0.101.5000", new DateTime(2026, 1, 1), "31.0.101.2140", instalada),
               "versión mayor: más nuevo");
            Eq(WindowsUpdateProvider.DriverVerdict.Newer,
               WindowsUpdateProvider.CompareDriver("1.0", new DateTime(2026, 1, 1), "1.0", instalada),
               "misma versión con fecha posterior: más nuevo");
            Eq(WindowsUpdateProvider.DriverVerdict.Same,
               WindowsUpdateProvider.CompareDriver("1.0", instalada, "1.0", instalada),
               "misma versión y fecha: igual");
            Eq(WindowsUpdateProvider.DriverVerdict.Unknown,
               WindowsUpdateProvider.CompareDriver("", null, "", null),
               "sin datos: no se puede decidir");

            // --- Selección: exactamente lo visible y marcado
            var a = new UpdateCandidate { Name = "A", Installable = true, Status = UpdateStatus.Disponible };
            var b = new UpdateCandidate { Name = "B", Installable = true, Status = UpdateStatus.Disponible };
            var c = new UpdateCandidate { Name = "C", Installable = false, Status = UpdateStatus.Disponible };
            var oculta = new UpdateCandidate { Name = "Oculta", Installable = true, Status = UpdateStatus.Disponible, Selected = true };
            var visibles = new List<KeyValuePair<UpdateCandidate, bool>>
            {
                new KeyValuePair<UpdateCandidate, bool>(a, true),
                new KeyValuePair<UpdateCandidate, bool>(b, false),
                new KeyValuePair<UpdateCandidate, bool>(c, true),
                new KeyValuePair<UpdateCandidate, bool>(a, true)
            };
            var sel = UpdateManager.SelectionFromVisible(visibles);
            Eq(1, sel.Count, "sólo una fila visible, marcada e instalable");
            Check(sel.Contains(a), "se instala lo marcado");
            Check(!sel.Contains(b), "NO se instala lo desmarcado");
            Check(!sel.Contains(c), "NO se instala lo que no es instalable aunque esté marcado");
            Check(!sel.Contains(oculta), "NO se instala lo que el filtro oculta, aunque esté marcado internamente");
            Eq(0, UpdateManager.SelectionFromVisible(null).Count, "sin filas no revienta");

            // --- Por defecto nada viene marcado
            Check(!new UpdateCandidate().Selected, "un candidato nuevo no está elegido por el usuario");
        }
    }
}
