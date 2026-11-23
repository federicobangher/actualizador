// Pruebas de la 2.0.13: los «No verificable» de la captura del usuario (07/11/2026). El controlador de NVIDIA se compara con el
// servicio oficial de NVIDIA para la placa instalada; los demás (Avast One, Device Detector, DWAgent, EZVIZ Studio, Logitech,
// PCHelpSoft, USB Disk Security, ViewSonic, µTorrent) no publican una versión que se pueda leer: cada uno dice por qué y qué hacer.
using System;
using System.Collections.Generic;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        // Respuestas reales de NVIDIA, recortadas (07/11/2026): lookupValueSearch.aspx (TypeID=3 productos, TypeID=2 series) y DriverManualLookup.
        const string NvProductos =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><LookupValueSearch><LookupValues>" +
            "<LookupValue ParentID=\"1\"><Name>GeForce 8800 Ultra</Name><Value>8</Value></LookupValue>" +
            "<LookupValue ParentID=\"95\"><Name>GeForce GTX 760 Ti (OEM)</Name><Value>711</Value></LookupValue>" +
            "<LookupValue ParentID=\"95\"><Name>GeForce GTX 760</Name><Value>703</Value></LookupValue>" +
            "<LookupValue ParentID=\"101\"><Name>GeForce GTX 1060</Name><Value>817</Value></LookupValue>" +
            "<LookupValue ParentID=\"102\"><Name>GeForce GTX 1060</Name><Value>821</Value></LookupValue>" +
            "<LookupValue ParentID=\"127\"><Name>NVIDIA GeForce RTX 4060</Name><Value>1023</Value></LookupValue>" +
            "<LookupValue ParentID=\"129\"><Name>GeForce RTX 4060 Laptop GPU</Name><Value>1007</Value></LookupValue>" +
            "</LookupValues></LookupValueSearch>";
        const string NvSeries =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><LookupValueSearch><LookupValues>" +
            "<LookupValue RequiresProduct=\"True\" ParentID=\"1\"><Name>GeForce 700 Series</Name><Value>95</Value></LookupValue>" +
            "<LookupValue RequiresProduct=\"True\" ParentID=\"1\"><Name>GeForce 10 Series</Name><Value>101</Value></LookupValue>" +
            "<LookupValue RequiresProduct=\"True\" ParentID=\"1\"><Name>GeForce 10 Series (Notebooks)</Name><Value>102</Value></LookupValue>" +
            "</LookupValues></LookupValueSearch>";

        static string NvRespuesta(string version, string fecha, string nombre, bool beta)
        {
            return "{ \"Success\" : \"1\", \"IDS\" : [{ \"downloadInfo\": { \"Success\" : \"1\", \"ID\" : \"228836\", \"DownloadTypeID\" : \"1\", " +
                   "\"Name\" : \"" + Uri.EscapeDataString(nombre) + "\", \"Version\" : \"" + version + "\", \"IsBeta\" : \"" + (beta ? "1" : "0") + "\", \"IsWHQL\" : \"1\", " +
                   "\"ReleaseDateTime\" : \"" + fecha + "\", " +
                   "\"DownloadURL\" : \"https://us.download.nvidia.com/Windows/" + version + "/" + version + "-desktop-win10-win11-64bit-international-dch-whql.exe\", " +
                   "\"DetailsURL\" : \"https://www.nvidia.com/Download/driverResults.aspx/228836/en-us\" } }], \"Request\" : [{}] }";
        }

        static InstalledProgram NvidiaInstalado(string version)
        {
            var p = Vc("NVIDIA Controlador de gráficos " + version, version, "x64");
            p.Publisher = "NVIDIA Corporation";
            p.UninstallString = "\"C:\\Windows\\SysWOW64\\RunDll32.EXE\" \"C:\\Program Files\\NVIDIA Corporation\\Installer2\\InstallerCore\\NVI2.DLL\",UninstallPackage Display.Driver";
            p.RegistryKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}_Display.Driver";
            return p;
        }

        static void TestNvidia2013()
        {
            var nv = VendorCatalog.Find("nvidia-graphics-driver");
            Check(nv != null, "NVIDIA en el catálogo de fuentes oficiales");
            if (nv == null) return;
            Check(nv.Feed == VendorFeed.NvidiaDriver && nv.Install == VendorInstall.SignedInstaller && nv.ShowInstaller && nv.NameHasVersion, "se consulta a NVIDIA y se instala con su instalador a la vista");
            Eq("NVIDIA Corporation", nv.Signers.Length > 0 ? nv.Signers[0] : null, "sólo con la firma de NVIDIA");

            // --- Qué entradas son el controlador de gráficos (en varios idiomas) y cuáles no.
            foreach (string n in new[] { "NVIDIA Controlador de gráficos 475.14", "NVIDIA Graphics Driver 621.14", "NVIDIA Grafiktreiber 566.36",
                                         "NVIDIA Pilote graphique 560.94", "NVIDIA Driver de vídeo 551.86" })
                Eq("nvidia-graphics-driver", IdOf(VendorCatalog.Match(Vc(n, "1.0", "x64"))), "«" + n + "» es el controlador de gráficos");
            foreach (string otro in new[] { "NVIDIA HD Audio Driver 1.3.40.14", "NVIDIA PhysX System Software 9.23.1019", "NVIDIA GeForce Experience 3.27.0.120",
                                            "NVIDIA App 11.0.1.163", "NVIDIA 3D Vision Controller Driver 426.00", "NVIDIA USBC Driver 1.50.831.832", "NVIDIA FrameView SDK 1.5.10933.35296130" })
                Eq(null, IdOf(VendorCatalog.Match(Vc(otro, "1.0", "x64"))), otro + ": otro componente");

            // --- Dominios: el servicio de la página de descargas y download.nvidia.com; nada más.
            Check(nv.Allows(new Uri(NvidiaDriver.ProductsUrl)), "lista de productos en nvidia.com");
            Check(nv.Allows(new Uri("https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup")), "consulta en gfwsl.geforce.com");
            Check(nv.Allows(new Uri("https://us.download.nvidia.com/Windows/621.14/621.14-desktop-win10-win11-64bit-international-dch-whql.exe")), "descarga de download.nvidia.com");
            Check(!nv.Allows(new Uri("https://nvidia.com.evil.example/x.exe")) && !nv.Allows(new Uri("http://us.download.nvidia.com/x.exe")) &&
                  !nv.Allows(new Uri("https://geforce.com.evil.example/x")), "imitaciones o sin HTTPS: no");

            // --- Listas de NVIDIA y elección del producto.
            var productos = NvidiaDriver.ParseLookup(NvProductos);
            var series = NvidiaDriver.ParseLookup(NvSeries);
            Eq(7, productos.Count, "productos leídos");
            Eq(3, series.Count, "series leídas");
            Eq("geforce gtx 760", NvidiaDriver.NormalizeGpu("NVIDIA GeForce(R) GTX 760"), "sin marca ni ®");
            var gtx760 = NvidiaDriver.FindProduct(productos, series, "NVIDIA GeForce GTX 760", false);
            Check(gtx760 != null && gtx760.Value == 703 && gtx760.ParentId == 95, "GTX 760 → producto 703 de la serie 700 (no la 760 Ti)");
            Eq(817, NvidiaDriver.FindProduct(productos, series, "NVIDIA GeForce GTX 1060", false).Value, "GTX 1060 de escritorio");
            Eq(821, NvidiaDriver.FindProduct(productos, series, "NVIDIA GeForce GTX 1060", true).Value, "GTX 1060 en una portátil: la serie de portátiles");
            Eq(1007, NvidiaDriver.FindProduct(productos, series, "NVIDIA GeForce RTX 4060 Laptop GPU", true).Value, "RTX 4060 Laptop GPU");
            Eq(1023, NvidiaDriver.FindProduct(productos, series, "NVIDIA GeForce RTX 4060", false).Value, "RTX 4060 (NVIDIA lo lista con la marca)");
            Eq(null, NvidiaDriver.FindProduct(productos, series, "Intel(R) UHD Graphics 630", false), "otra marca: nada");

            var w11 = new OsInfo { IsWindows = true, Major = 10, Build = 26100, NativeArchitecture = "x64" };
            var w10 = new OsInfo { IsWindows = true, Major = 10, Build = 19045, NativeArchitecture = "x64" };
            Eq(135, NvidiaDriver.OsId(w11), "Windows 11");
            Eq(57, NvidiaDriver.OsId(w10), "Windows 10 de 64 bits");
            string lookup = NvidiaDriver.LookupUrl(gtx760, 135, true);
            Check(lookup.Contains("psid=95&pfid=703&osID=135") && lookup.Contains("beta=0") && lookup.Contains("isWHQL=1") && lookup.Contains("dch=1") && lookup.Contains("upCRD=0"),
                  "consulta: la placa, el Windows, sin betas, WHQL, DCH, Game Ready: " + lookup);

            var rel = NvidiaDriver.ParseLookupResult(NvRespuesta("475.14", "Tue Jul 09, 2024", "GeForce Security Update Driver", false));
            Check(rel != null && rel.Version == "475.14" && rel.Name == "GeForce Security Update Driver", "versión y nombre de la respuesta");
            Check(rel != null && rel.Released.HasValue && rel.Released.Value == new DateTime(2024, 7, 9), "fecha de publicación");
            Eq(null, NvidiaDriver.ParseLookupResult(NvRespuesta("622.01", "Tue Nov 03, 2026", "GeForce Game Ready Driver", true)), "una beta no cuenta");
            Eq(null, NvidiaDriver.ParseLookupResult("{ \"Success\" : \"0\" }"), "sin resultado");
            Eq(null, NvidiaDriver.ParseLookupResult("<html>error</html>"), "respuesta que no es JSON");

            // --- La captura: GTX 760 con la 475.14 → al día (NVIDIA no publica nada más nuevo para esa placa).
            var oldFetch = VendorProvider.FetchForTests;
            var oldGpus = NvidiaDriver.GpusForTests;
            var pedidas = new List<string>();
            string ultima = "475.14", fecha = "Tue Jul 09, 2024", nombre = "GeForce Security Update Driver";
            try
            {
                NvidiaDriver.DchForTests = true;
                NvidiaDriver.LaptopForTests = false;
                NvidiaDriver.GpusForTests = delegate { return new List<string> { "NVIDIA GeForce GTX 760" }; };
                VendorProvider.FetchForTests = delegate(string u)
                {
                    pedidas.Add(u);
                    if (u == NvidiaDriver.ProductsUrl) return NvProductos;
                    if (u == NvidiaDriver.SeriesUrl) return NvSeries;
                    if (u.StartsWith("https://gfwsl.geforce.com/", StringComparison.Ordinal) && u.Contains("pfid=703")) return NvRespuesta(ultima, fecha, nombre, false);
                    return null;
                };
                var output = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, NvidiaInstalado("475.14")), output, new List<string>());
                var row = FilaDe(output, "NVIDIA Controlador de gráficos 475.14");
                Check(row != null && row.Status == UpdateStatus.AlDia, "475.14: al día según NVIDIA (" + (row != null ? row.Status + " " + row.Detail : "sin fila") + ")");
                if (row != null)
                {
                    Eq("475.14", row.AvailableVersion, "la que publica NVIDIA para la GTX 760");
                    Check(row.Detail.Contains("GeForce GTX 760") && row.Detail.Contains(Fmt.Date(new DateTime(2024, 7, 9))) && row.Detail.Contains("no publicó versiones más nuevas"),
                          "dice para qué placa y desde cuándo: " + row.Detail);
                    Eq("https://www.nvidia.com/Download/driverResults.aspx/228836/en-us", row.InfoUrl, "su página en nvidia.com");
                }
                Check(pedidas.Exists(delegate(string u) { return u.Contains("osID=135") && u.Contains("dch=1"); }), "consultó para Windows 11 y DCH");

                // --- Cuando NVIDIA publique una nueva: disponible, se instala verificado con la ventana de NVIDIA.
                ultima = "621.14"; fecha = "Wed Nov 04, 2026"; nombre = "GeForce Game Ready Driver";
                var out2 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, NvidiaInstalado("566.36")), out2, new List<string>());
                var r2 = FilaDe(out2, "NVIDIA Controlador de gráficos 566.36");
                Check(r2 != null && r2.Status == UpdateStatus.Disponible && r2.Installable, "566.36 → disponible la 621.14 e instalable (" + (r2 != null ? r2.Status + " " + r2.Detail : "sin fila") + ")");
                if (r2 != null)
                {
                    var plan = r2.ProviderTag as VendorPlan;
                    Eq("https://us.download.nvidia.com/Windows/621.14/621.14-desktop-win10-win11-64bit-international-dch-whql.exe", plan != null ? plan.InstallerUrl : null, "el instalador que publica NVIDIA");
                    Check(r2.Detail.StartsWith("NVIDIA publica la 621.14 para la GeForce GTX 760 (GeForce Game Ready Driver, del " + Fmt.Date(new DateTime(2026, 11, 4)) + ")") &&
                          r2.Detail.Contains("NVIDIA Corporation") && !r2.Detail.Contains("Cerrá "), "detalle: " + r2.Detail);
                    Check(!r2.Detail.Contains("no publicó versiones más nuevas"), "una reciente no dice «desde entonces»");

                    // Después de instalar, Windows registra otro nombre («… 621.14»): la versión se relee por la expresión del catálogo.
                    VendorProvider.ProgramsNowForTests = delegate { return new List<InstalledProgram> { NvidiaInstalado("621.14"), Vc("NVIDIA HD Audio Driver 1.3.40.14", "1.3.40.14", "x64") }; };
                    Eq("621.14", VendorProvider.InstalledVersionNowForTests(ContextoSimulado(true), plan), "la entrada nueva, aunque se llame distinto");
                }

                // --- Sin placa de NVIDIA reconocible: no hay fila del fabricante (queda la de winget con su explicación).
                NvidiaDriver.GpusForTests = delegate { return new List<string>(); };
                var out3 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, NvidiaInstalado("475.14")), out3, new List<string>());
                Eq(0, out3.Count, "sin placa: sin fila de NVIDIA");
                NvidiaDriver.GpusForTests = delegate { return new List<string> { "NVIDIA GeForce GTX 9999 Imaginaria" }; };
                out3.Clear();
                new VendorProvider().Scan(ContextoSimulado(true, NvidiaInstalado("475.14")), out3, new List<string>());
                Eq(0, out3.Count, "placa que NVIDIA no lista: sin fila");

                // --- NVIDIA no contesta: error con el motivo (no «al día»).
                NvidiaDriver.GpusForTests = delegate { return new List<string> { "NVIDIA GeForce GTX 760" }; };
                VendorProvider.FetchForTests = delegate(string u) { return null; };
                var out4 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, NvidiaInstalado("475.14")), out4, new List<string>());
                Check(out4.Count == 1 && out4[0].Status == UpdateStatus.Error, "sin respuesta de NVIDIA: error, no «al día»");

                // --- Controlador estándar (no DCH) que NVIDIA ya no publica para este Windows: se prueba el DCH.
                NvidiaDriver.DchForTests = false;
                ultima = "475.14"; fecha = "Tue Jul 09, 2024"; nombre = "GeForce Security Update Driver";
                pedidas.Clear();
                VendorProvider.FetchForTests = delegate(string u)
                {
                    pedidas.Add(u);
                    if (u == NvidiaDriver.ProductsUrl) return NvProductos;
                    if (u.Contains("pfid=703") && u.Contains("dch=0")) return "{ \"Success\" : \"0\" }";
                    if (u.Contains("pfid=703") && u.Contains("dch=1")) return NvRespuesta(ultima, fecha, nombre, false);
                    return null;
                };
                var out5 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, NvidiaInstalado("475.14")), out5, new List<string>());
                Check(out5.Count == 1 && out5[0].Status == UpdateStatus.AlDia, "estándar sin resultado → DCH: al día");
                Check(pedidas.Exists(delegate(string u) { return u.Contains("dch=0"); }) && pedidas.Exists(delegate(string u) { return u.Contains("dch=1"); }), "probó los dos");
            }
            finally
            {
                VendorProvider.FetchForTests = oldFetch;
                VendorProvider.ProgramsNowForTests = null;
                NvidiaDriver.GpusForTests = oldGpus;
                NvidiaDriver.DchForTests = null;
                NvidiaDriver.LaptopForTests = null;
            }
        }

        static void TestNoVerificablesCaptura2013()
        {
            // --- Device Detector: componente de ACDSee (por editor o carpeta); el de CyberLink no.
            var dd = Prog("Device Detector", "3.1.44.1", "ACD Systems", "");
            var ddHint = ProgramHints.For(dd);
            Check(ddHint != null && ddHint.Text.Contains("ACDSee"), "Device Detector de ACD Systems: componente de ACDSee");
            var ddCarpeta = Prog("Device Detector", "3.1.44.1", "", "\"C:\\Program Files (x86)\\Common Files\\ACD Systems\\EN\\DevDetect\\uninst.exe\"");
            Check(ProgramHints.For(ddCarpeta) != null && ProgramHints.For(ddCarpeta).Text.Contains("ACDSee"), "sin editor, por la carpeta de ACD Systems");
            var ddCyber = ProgramHints.For(Prog("Device Detector", "1.0", "CyberLink Corp.", ""));
            Check(ddCyber == null || !ddCyber.Text.Contains("ACDSee"), "el Device Detector de CyberLink no es de ACDSee");

            // --- Los que no publican versión: cada uno dice por qué y qué hacer.
            var dw = ProgramHints.For(Prog("DWAgent", "", "DWSNet s.r.l.", ""));
            Check(dw != null && dw.Text.Contains("DWService") && dw.Text.Contains("se actualiza solo"), "DWAgent");
            var lps = ProgramHints.For(Prog("Logi Plugin Service", "6.4.2.3414", "Logitech", ""));
            Check(lps != null && lps.Text.Contains("Logi Options+") && lps.Text.Contains("por separado"), "Logi Plugin Service: componente de Options+");
            var lda = ProgramHints.For(Prog("Logi Download Assistant", "2.0.1020", "Logitech", ""));
            Check(lda != null && lda.Text.Contains("se puede desinstalar"), "Logi Download Assistant");
            Check(!ProgramHints.AllowsCatalogSearch(Prog("Logi Plugin Service", "6.4.2.3414", "Logitech", "")), "Logitech: no se busca por nombre en winget");
            var usb = ProgramHints.For(Prog("USB Disk Security", "", "Zbshareware Lab", ""));
            Check(usb != null && usb.Text.Contains("privatedlp.com"), "USB Disk Security: su sitio ya no publica el programa clásico");
            var ez = ProgramHints.For(Prog("Ezviz Studio", "", "EZVIZ Inc.", ""));
            Check(ez != null && ez.Text.Contains("ezviz.com"), "EZVIZ Studio");
            var vs = ProgramHints.For(Prog("ViewSonic Windows 11 64bit Signed Files", "", "ViewSonic", ""));
            Check(vs != null && vs.Text.Contains("monitor") && vs.Text.Contains("«Controladores»"), "ViewSonic: archivos del controlador del monitor");
            var ut = ProgramHints.For(Prog("µTorrent", "3.6.0.47228", "BitTorrent Limited", ""));
            Check(ut != null && ut.Text.Contains("Buscar actualizaciones") && ut.Text.Contains("terceros"), "µTorrent");
            Check(ProgramHints.For(Prog("Opera Stable", "120.0", "Opera Software", "")).Text.Contains("actualizador propio"), "los demás con actualizador propio, como antes");

            // --- Sin versión en el registro: se muestra la del ejecutable principal.
            var ezp = Prog("Ezviz Studio", "", "EZVIZ Inc.", "");
            ezp.DisplayIcon = "C:\\Program Files (x86)\\EZVIZ Studio\\EzvizStudio.exe";
            var fila = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Ezviz Studio", InstalledVersion = "Desconocida", Status = UpdateStatus.NoVerificable, ProviderName = WingetProvider.ProviderId };
            var conVersion = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Avast One", InstalledVersion = "26.10.11201.1041", Status = UpdateStatus.NoVerificable, ProviderName = WingetProvider.ProviderId };
            try
            {
                ProgramHints.ExeVersionForTests = delegate(string path) { return path.EndsWith("EzvizStudio.exe") ? "3.15.3.0" : null; };
                ProgramHints.Annotate(new List<UpdateCandidate> { fila, conVersion }, new List<InstalledProgram> { ezp, Prog("Avast One", "26.10.11201.1041", "Gen Digital Inc.", "") });
                Eq("3.15.3.0", fila.InstalledVersion, "EZVIZ Studio: la versión de su ejecutable");
                Check(fila.Detail.Contains("EzvizStudio.exe") && fila.Detail.Contains("ezviz.com"), "y la explicación: " + fila.Detail);
                Eq("26.10.11201.1041", conVersion.InstalledVersion, "la que ya tiene versión no se toca");
            }
            finally { ProgramHints.ExeVersionForTests = null; }

            // --- Avast One con el Centro de seguridad: al día (lo informa el propio antivirus).
            var av = new List<AntivirusStatus> { AntivirusStatus.FromProductState("Avast One", 0x61000) };
            var avast = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Avast One", InstalledVersion = "26.10.11201.1041", Status = UpdateStatus.NoVerificable, ProviderName = WingetProvider.ProviderId };
            ProgramHints.Annotate(new List<UpdateCandidate> { avast }, new List<InstalledProgram> { Prog("Avast One", "26.10.11201.1041", "Gen Digital Inc.", "") }, av);
            Check(avast.Status == UpdateStatus.AlDia && avast.Source == "Centro de seguridad de Windows", "Avast One: al día según el Centro de seguridad");

            // --- El fabricante publica una versión pero no se pudo leer la instalada (sólo se compara): no se dice «desactualizado».
            var oldFetch = VendorProvider.FetchForTests;
            try
            {
                VendorProvider.FetchForTests = delegate(string u) { return u.Contains("daemon-tools.cc") ? "<h2>DAEMON Tools Lite 12.6.0</h2>" : null; };
                var output = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, Prog("DAEMON Tools Lite", "", "Disc Soft Ltd", "")), output, new List<string>());
                var d = FilaDe(output, "DAEMON Tools Lite");
                Check(d != null && d.Status == UpdateStatus.NoVerificable && d.Detail.Contains("12.6.0"), "versión instalada ilegible: no verificable, con la publicada (" + (d != null ? d.Status + " " + d.Detail : "sin fila") + ")");
            }
            finally { VendorProvider.FetchForTests = oldFetch; }
        }
    
        /// <summary>Video del usuario (07/11/2026): «Actualizar (1)» con «Disponibles (0)», y la lista vacía mientras instalaba.</summary>
        static void TestMarcarTodoYListas2013()
        {
            var store = new UpdateCandidate { Kind = UpdateKind.Store, Name = "Todas las apps de Microsoft Store (la Store busca e instala)", Identifier = StoreProvider.AllAppsId,
                                              Installable = true, Recommended = false, Status = UpdateStatus.AlDia, AvailableVersion = "Sin novedades", ProviderName = StoreProvider.ProviderId };
            var prog = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Programa", Installable = true, Recommended = true, Status = UpdateStatus.Disponible };
            var falla = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Otro", Installable = true, Recommended = true, Status = UpdateStatus.Fallida };
            var noVer = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Juego", Installable = false, Status = UpdateStatus.NoVerificable };

            // «Marcar todo» no marca la Store «Sin novedades» (se puede marcar a mano para que busque de nuevo).
            Check(UpdateManager.CanInstall(store), "la Store «Sin novedades» se puede marcar a mano");
            Check(!UpdateGroups.MarkedByMarkAll(store), "«Marcar todo» no la marca");
            Check(UpdateGroups.MarkedByMarkAll(prog) && UpdateGroups.MarkedByMarkAll(falla), "«Marcar todo» marca lo disponible y lo que falló");
            Check(!UpdateGroups.MarkedByMarkAll(noVer) && !UpdateGroups.MarkedByMarkAll(null), "ni lo que no se puede instalar");

            // Marcada a mano y ya instalando: pasa a «Disponibles», y esa lista se rearma para mostrarla.
            Eq(UpdateView.Revisados, UpdateGroups.ViewOf(store), "al día: en «Al día y sin verificar»");
            store.Status = UpdateStatus.Instalando;
            Eq(UpdateView.Pendientes, UpdateGroups.ViewOf(store), "instalando: en «Disponibles»");
            Check(UpdateGroups.ListChanged(store, UpdateView.Pendientes, false, true, false), "«Disponibles» a la vista sin la fila: se rearma");
            Check(UpdateGroups.ListChanged(store, UpdateView.Revisados, true, true, false), "«Al día» a la vista con la fila que se fue: se rearma");
            Check(!UpdateGroups.ListChanged(store, UpdateView.Pendientes, true, true, false), "ya está donde va: sólo se repinta");
            Check(!UpdateGroups.ListChanged(store, UpdateView.Revisados, false, true, false), "no va en la que se ve y no está: nada");
            Check(!UpdateGroups.ListChanged(store, UpdateView.Pendientes, false, false, false), "filtrada por tipo: nada");
            Check(!UpdateGroups.ListChanged(store, UpdateView.Pendientes, false, true, true), "con texto de filtro: no se rearma en cada cambio");
            var k = UpdateGroups.Count(new List<UpdateCandidate> { store });
            Eq(1, k.ToUpdate + k.Optional, "«Disponibles (1)» cuenta la que se instala");
        }

        /// <summary>Captura del 08/11/2026: Double Commander 1.2.11 de GitHub «no tiene una firma digital válida (0x800B0100)». El autor no firma.</summary>
        static void TestSinFirmaDeGitHub2013()
        {
            var dc = Vc("Double Commander", "1.2.10", "x64");
            dc.Publisher = "Alexander Koblov";
            dc.InstallLocation = @"C:\Program Files\Double Commander\";
            dc.UninstallString = "MsiExec.exe /X{9C1E7B52-0D7C-4C3A-9B0B-6C1A2D3E4F50}";
            dc.RegistryKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{9C1E7B52-0D7C-4C3A-9B0B-6C1A2D3E4F50}";
            const string codigo = "{9C1E7B52-0D7C-4C3A-9B0B-6C1A2D3E4F50}";
            Eq(codigo, VendorProvider.ProductCodeOf(dc), "ProductCode de lo instalado (clave de «Desinstalar»)");
            Eq(null, VendorProvider.ProductCodeOf(Vc("Programa", "1.0", "x64")), "sin GUID: no es MSI");

            var msi = new Dictionary<string, string> { { "ProductName", "Double Commander" }, { "ProductVersion", "1.2.11" }, { "Manufacturer", "Alexander Koblov" },
                                                        { "UpgradeCode", "{4F0C2A11-1B1E-4C6E-8E9A-0A1B2C3D4E5F}" } };
            var familia = new List<string> { codigo };

            // Se acepta: SHA-256 de GitHub, lo instalado sin firma, mismo UpgradeCode y la versión anunciada.
            Eq(null, VendorProvider.UnsignedReleaseVerdict(true, 0, true, msi, familia, codigo, "1.2.11", "", dc), "Double Commander 1.2.11: se instala");
            Eq(null, VendorProvider.UnsignedReleaseVerdict(true, 0, true, new Dictionary<string, string>(msi) { ["ProductVersion"] = "1.2.11.0" }, familia, codigo, "1.2.11", "", dc),
               "1.2.11.0 es la 1.2.11");
            // No se acepta:
            Check(VendorProvider.UnsignedReleaseVerdict(false, 0, true, msi, familia, codigo, "1.2.11", "", dc).Contains("SHA-256"), "sin el SHA-256 de GitHub");
            Check(VendorProvider.UnsignedReleaseVerdict(true, 1, true, msi, familia, codigo, "1.2.11", "", dc).Contains("tiene firma digital"), "lo instalado está firmado y el nuevo no");
            Check(VendorProvider.UnsignedReleaseVerdict(true, -1, true, msi, familia, codigo, "1.2.11", "", dc).Contains("no se pudo comprobar"), "no se sabe si lo instalado está firmado");
            Check(VendorProvider.UnsignedReleaseVerdict(true, 0, true, msi, new List<string> { "{00000000-0000-0000-0000-000000000000}" }, codigo, "1.2.11", "", dc).Contains("UpgradeCode"),
                  "otro producto (otro UpgradeCode)");
            Check(VendorProvider.UnsignedReleaseVerdict(true, 0, true, msi, null, codigo, "1.2.11", "", dc).Contains("UpgradeCode"), "sin productos relacionados");
            Check(VendorProvider.UnsignedReleaseVerdict(true, 0, true, new Dictionary<string, string>(msi) { ["ProductVersion"] = "1.2.9" }, familia, codigo, "1.2.11", "", dc).Contains("1.2.9"),
                  "un paquete de otra versión");
            Check(VendorProvider.UnsignedReleaseVerdict(true, 0, true, new Dictionary<string, string>(), familia, codigo, "1.2.11", "", dc).Contains("no se pudo leer"), "MSI ilegible");
            // Lo instalado no es MSI: el nombre del producto del paquete.
            var exeInst = Vc("Double Commander", "1.2.10", "x64"); exeInst.Publisher = "Alexander Koblov";
            Eq(null, VendorProvider.UnsignedReleaseVerdict(true, 0, true, msi, null, null, "1.2.11", "", exeInst), "instalado sin MSI: por el nombre del producto");
            Check(VendorProvider.UnsignedReleaseVerdict(true, 0, true, new Dictionary<string, string>(msi) { ["ProductName"] = "Otro Programa Distinto" }, null, null, "1.2.11", "", exeInst) != null,
                  "paquete de otro producto");
            // Un .exe sin firma: tiene que decir ser de este programa.
            Eq(null, VendorProvider.UnsignedReleaseVerdict(true, 0, false, null, null, null, "1.2.11", "Double Commander", exeInst), ".exe que dice ser Double Commander");
            Check(VendorProvider.UnsignedReleaseVerdict(true, 0, false, null, null, null, "1.2.11", "", exeInst) != null, ".exe que no dice de qué es");

            // La fila de winget atrasada queda marcada con el repositorio del manifiesto de winget (sólo esas aceptan instaladores sin firma).
            var c = FilaWinget("Double Commander", "alexx2000.DoubleCommander", "1.2.10", "1.2.10");
            c.Status = UpdateStatus.AlDia; c.Installable = false; c.Detail = "Al día según winget.";
            string json = "[{\"tag_name\":\"v1.2.11\",\"html_url\":\"https://github.com/doublecmd/doublecmd/releases/tag/v1.2.11\",\"draft\":false,\"prerelease\":false,\"assets\":[" +
                          "{\"name\":\"doublecmd-1.2.11.x86_64-win64.msi\",\"browser_download_url\":\"https://github.com/doublecmd/doublecmd/releases/download/v1.2.11/doublecmd-1.2.11.x86_64-win64.msi\"," +
                          "\"size\":12000000,\"digest\":\"sha256:" + new string('a', 64) + "\"}]}," +
                          "{\"tag_name\":\"v1.2.10\",\"html_url\":\"https://github.com/doublecmd/doublecmd/releases/tag/v1.2.10\",\"draft\":false,\"prerelease\":false,\"assets\":[]}]";
            Eq(ReleaseCrossCheck.Outcome.Lagging, ReleaseCrossCheck.Apply(ContextoSimulado(true, dc), c, dc, "doublecmd/doublecmd", GitHubRules.ParseReleaseList(json)), "winget atrasado: la 1.2.11");
            var plan = c.ProviderTag as VendorPlan;
            Check(plan != null && plan.RepoFromWinget && c.Installable, "instalable, con el repositorio del manifiesto de winget");
            Check(c.Detail.Contains("el autor no firma") && c.Detail.Contains("mismo producto"), "y lo dice: " + c.Detail);

            // Una fila del fabricante (el repositorio sale del registro del programa): sigue exigiendo firma.
            var v = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Double Commander", InstalledVersion = "1.2.10", AvailableVersion = "1.2.10", Status = UpdateStatus.AlDia,
                                          ProviderName = VendorProvider.ProviderId };
            ReleaseCrossCheck.Apply(ContextoSimulado(true, dc), v, dc, "doublecmd/doublecmd", GitHubRules.ParseReleaseList(json));
            var plan2 = v.ProviderTag as VendorPlan;
            Check(plan2 == null || !plan2.RepoFromWinget, "fila del fabricante: no acepta instaladores sin firma");
        }
}
}
