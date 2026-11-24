// Pruebas de la 1.7.6: un solo número en pantalla, Python 3.13 ≠ 3.14, búsqueda más rápida (caché y paralelo).
using System;
using System.Collections.Generic;
using System.IO;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static UpdateCandidate Fila176(string name, UpdateKind kind, UpdateStatus st, bool installable, bool recommended, string provider)
        {
            return new UpdateCandidate { Name = name, Kind = kind, Status = st, Installable = installable, Recommended = recommended, ProviderName = provider, InstalledVersion = "1.0",
                                         // 2.0.41: una fila al día o sin verificar no dice una versión más nueva (si la dijera, está desactualizada: KnownOutdated)
                                         AvailableVersion = st == UpdateStatus.AlDia ? "1.0" : st == UpdateStatus.NoVerificable ? "" : "2.0" };
        }

        static void TestUnSoloNumero176()
        {
            // La captura del usuario: «Hay 5 disponibles», «Actualizar todo (3)» y «6 Para actualizar».
            var dc = Fila176("Double Commander", UpdateKind.Programa, UpdateStatus.Disponible, true, true, WingetProvider.ProviderId);
            var gom = Fila176("GOM Player", UpdateKind.Programa, UpdateStatus.Disponible, true, true, VendorProvider.ProviderId);
            var pdf = Fila176("PDF24 Creator 11.30.1", UpdateKind.Programa, UpdateStatus.Disponible, true, true, SiteProvider.ProviderId);
            var py = Fila176("Python 3.14.7 (64-bit)", UpdateKind.Programa, UpdateStatus.Disponible, true, true, WingetProvider.ProviderId);
            var store = Fila176("Todas las apps de Microsoft Store (la Store busca e instala)", UpdateKind.Store, UpdateStatus.Pendiente, true, true, StoreProvider.ProviderId);
            store.Identifier = StoreProvider.AllAppsId;
            var bios = Fila176("BIOS", UpdateKind.Controlador, UpdateStatus.Disponible, true, false, OemToolsProvider.ProviderId);
            var catalogo = Fila176("Realtek PCIe", UpdateKind.Controlador, UpdateStatus.Disponible, true, false, CatalogDriverProvider.ProviderId);
            var anclado = Fila176("Paquete anclado", UpdateKind.Programa, UpdateStatus.Disponible, true, false, WingetProvider.ProviderId);
            var sinAdmin = Fila176("Necesita administrador", UpdateKind.Programa, UpdateStatus.Disponible, false, false, VendorProvider.ProviderId);
            var aldia = Fila176("7-Zip", UpdateKind.Programa, UpdateStatus.AlDia, false, false, WingetProvider.ProviderId);
            var storeAlDia = Fila176("Todas las apps", UpdateKind.Store, UpdateStatus.AlDia, true, false, StoreProvider.ProviderId);
            storeAlDia.Identifier = StoreProvider.AllAppsId;
            var nov = Fila176("Programa raro", UpdateKind.Programa, UpdateStatus.NoVerificable, false, false, WingetProvider.ProviderId);
            var appStore = Fila176("Fotos", UpdateKind.Store, UpdateStatus.NoVerificable, false, false, StoreProvider.ProviderId);
            var todas = new List<UpdateCandidate> { dc, gom, pdf, py, store, bios, catalogo, anclado, sinAdmin, aldia, storeAlDia, nov, appStore };

            Eq(UpdateGroup.ParaActualizar, UpdateGroups.Of(store), "la Store pendiente: para actualizar (la hace «Actualizar todo»)");
            Eq(UpdateGroup.Opcional, UpdateGroups.Of(bios), "BIOS: opcional");
            Eq(UpdateGroup.Opcional, UpdateGroups.Of(catalogo), "controlador que Windows no eligió: opcional");
            Eq(UpdateGroup.Opcional, UpdateGroups.Of(sinAdmin), "lo que no se puede instalar desde acá: opcional (no lo hace «Actualizar todo»)");
            Eq(UpdateGroup.AlDia, UpdateGroups.Of(storeAlDia), "la Store revisada hace poco: al día");

            var k = UpdateGroups.Count(todas);
            Eq(5, k.ToUpdate, "para actualizar: 4 programas y la Store");
            Eq(4, k.Optional, "opcionales");
            Eq(2, k.UpToDate, "al día");
            Eq(2, k.Unverified, "sin verificar");
            Eq(1, k.Pages, "una abre su página");
            Eq("Hay 5 para actualizar", UpdateGroups.Title(k), "título con el mismo número");
            Eq("Actualizar todo (5)", UpdateGroups.ButtonText(k), "sin nada marcado, el botón hace «Actualizar todo»");
            Check(UpdateGroups.Subtitle(k).StartsWith("«Actualizar todo» instala las 5"), "la línea de abajo, el mismo número: " + UpdateGroups.Subtitle(k));
            Check(UpdateGroups.Subtitle(k).Contains("4 opcionales") && UpdateGroups.Subtitle(k).Contains("2 sin verificar"), "y los de las otras tarjetas");

            // Marcadas solas al aparecer: exactamente las de «Para actualizar».
            foreach (var u in todas) u.Selected = UpdateGroups.ShouldPremark(u);
            k = UpdateGroups.Count(todas);
            Eq(5, k.Selected, "las 5 quedan marcadas");
            Check(k.SelectionIsAll, "lo marcado es «Para actualizar»");
            Eq("Actualizar todo (5)", UpdateGroups.ButtonText(k), "el botón");
            Check(!storeAlDia.Selected && !bios.Selected && !catalogo.Selected, "lo opcional y la Store al día no se marcan solos");

            // El usuario cambia la selección: el botón lo dice.
            pdf.Selected = false;
            catalogo.Selected = true;
            k = UpdateGroups.Count(todas);
            Eq("Actualizar marcadas (5)", UpdateGroups.ButtonText(k), "4 de «Para actualizar» y 1 opcional");
            Eq(5, k.ToUpdate, "la tarjeta no cambia por marcar");

            // Después de instalar: lo hecho ya no cuenta.
            dc.Status = UpdateStatus.Instalada; gom.Status = UpdateStatus.Fallida;
            k = UpdateGroups.Count(todas);
            Eq(4, k.ToUpdate, "lo instalado sale de la cuenta; lo que falló se reintenta");
            Check(UpdateGroups.Subtitle(k).Contains("falló antes y se reintenta"), "y lo dice");
            Eq(UpdateGroup.ParaActualizar, UpdateGroups.Of(dc), "la fila instalada sigue a la vista en su grupo");

            // Nada pendiente.
            var limpio = new List<UpdateCandidate> { aldia, storeAlDia };
            Eq("Todo está al día ✓", UpdateGroups.Title(UpdateGroups.Count(limpio)), "todo al día");
            limpio.Add(nov);
            Eq("Nada para actualizar entre lo que se pudo comparar", UpdateGroups.Title(UpdateGroups.Count(limpio)), "con programas sin verificar no dice «todo al día»");
            limpio.Remove(nov); limpio.Add(Fila176("BIOS", UpdateKind.Controlador, UpdateStatus.Disponible, true, false, OemToolsProvider.ProviderId));
            Eq("Lo recomendado está al día ✓", UpdateGroups.Title(UpdateGroups.Count(limpio)), "sólo opcionales");

            // La consola y las vueltas extra no abren páginas solas.
            var pagina = Fila176("PDF24 Creator", UpdateKind.Programa, UpdateStatus.Disponible, true, true, SiteProvider.ProviderId);
            Eq(0, UpdateManager.NextRound(new[] { pagina }, null, null, null).Count, "vuelta extra: no abre páginas que no se eligieron");
        }

        static void TestSeriesYPaginas176()
        {
            // Python 3.13.16: python.org publica la 3.14.10, que es OTRO programa (se instalan uno al lado del otro).
            string pagina = "<html><head><title>Python Releases for Windows</title></head><body><h1>Python Releases for Windows</h1>" +
                            "<p>Latest Python 3 Release - Python 3.14.10</p><p>Python 3.13.16 - Oct. 9, 2026</p><p>Python 3.12.12</p></body></html>";
            var estricta = SiteVersions.Evaluate(pagina, "Python 3.13.16 (64-bit)", "Python Software Foundation", "3.13.16", true, false, "https://www.python.org/downloads/windows/");
            Check(estricta == null || !VersionUtil.IsNewer(estricta.Latest, "3.13.16"), "winget la da al día: la 3.14 no es una actualización de la 3.13 (" + (estricta != null ? estricta.Latest : "nada") + ")");
            var libre = SiteVersions.Evaluate(pagina, "Python 3.13.16 (64-bit)", "Python Software Foundation", "3.13.16", false, false, "https://www.python.org/downloads/windows/");
            Check(libre == null || !VersionUtil.IsNewer(libre.Latest, "3.13.16"), "tampoco sin winget: Python se instala por serie (" + (libre != null ? libre.Latest : "nada") + ")");
            string otra = pagina.Replace("Python 3.13.16 - Oct", "Python 3.13.17 - Nov");
            var nueva = SiteVersions.Evaluate(otra, "Python 3.13.16 (64-bit)", "Python Software Foundation", "3.13.16", false, false, "https://www.python.org/downloads/windows/");
            Check(nueva != null && nueva.Latest == "3.13.17", "la 3.13.17 sí es de su serie (" + (nueva != null ? nueva.Latest : "nada") + ")");
            Check(SiteVersions.IsSideBySide("Python 3.13.16 (64-bit)") && SiteVersions.IsSideBySide("PostgreSQL 16") && !SiteVersions.IsSideBySide("PDF24 Creator 11.30.1"), "qué se instala por serie");
            // PDF24 sí cambia de serie menor (11.30 → 11.31) sin dejar de ser el mismo programa.
            var pdf = SiteVersions.Evaluate("<h1>PDF24 Creator</h1><p>PDF24 Creator 11.31.1 – Download</p>", "PDF24 Creator 11.30.1", "geek software GmbH", "11.30.1", false, false, "https://www.pdf24.org/es/creator");
            Check(pdf != null && pdf.Latest == "11.31.1", "PDF24 11.30.1 → 11.31.1 (" + (pdf != null ? pdf.Latest : "nada") + ")");
            // Pero con winget al día (estricto), sólo la misma serie: un número de otra serie no es «catálogo atrasado».
            var gom = SiteVersions.Evaluate("<p>GOM Player 2.3.123 is now available</p>", "GOM Player", "GOM & Company", "2.3.121.5401", true, false, null);
            Check(gom != null && gom.Latest == "2.3.123", "GOM, misma serie: sigue funcionando");

            // Catálogo: el reloj del sistema y demás dispositivos genéricos de ACPI no se consultan.
            var reloj = new DriverInfo { DeviceName = "System timer", HardwareId = @"ACPI\VEN_PNP&DEV_0100", Version = "10.0.26100.1", Provider = "Microsoft", DeviceClass = "System" };
            Check(!CatalogRules.IsVendorHardware(reloj), "ACPI\\VEN_PNP: no es hardware de un fabricante");
            var intel = new DriverInfo { DeviceName = "Intel Serial IO", HardwareId = @"ACPI\VEN_INT&DEV_33C3", Version = "10.0.26100.1", Provider = "Microsoft", DeviceClass = "System" };
            Check(CatalogRules.IsVendorHardware(intel), "ACPI\\VEN_INT (Intel): sí");
        }

        static void TestCacheDeConsultas176()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sp176-" + Guid.NewGuid().ToString("N"));
            var old = QueryCache.DirectoryForTests;
            QueryCache.DirectoryForTests = dir;
            try
            {
                var c = QueryCache.Open("prueba", TimeSpan.FromHours(12));
                Eq(0, c.Count, "empieza vacía");
                c.Put("7-Zip", "tabla de winget");
                c.Put("vacía", "");
                c.Save();
                var d = QueryCache.Open("prueba", TimeSpan.FromHours(12));
                string v;
                Check(d.TryGet("7-zip", out v) && v == "tabla de winget", "se guarda y se lee (sin distinguir mayúsculas)");
                Check(d.TryGet("vacía", out v) && v == "", "una respuesta vacía («no hay resultados») también vale");
                Check(!d.TryGet("otra", out v), "lo que no está: no");
                var vencida = QueryCache.Open("prueba", TimeSpan.Zero);
                Check(!vencida.TryGet("7-Zip", out v), "vencida: se consulta de nuevo");
                File.WriteAllText(Path.Combine(dir, "rota.json"), "{ esto no es json");
                Eq(0, QueryCache.Open("rota", TimeSpan.FromHours(1)).Count, "archivo roto: empieza vacía, sin error");
                Check(QueryCache.Clear() >= 2, "se puede borrar todo");
            }
            finally
            {
                QueryCache.DirectoryForTests = old;
                try { Directory.Delete(dir, true); } catch { }
            }

            // Las entradas del catálogo de controladores se guardan completas.
            var e = new CatalogEntry { Guid = "abc-123", Title = "Intel - System - 10.1.2.3", Products = "Windows 11 Client, version 24H2 and later, Servicing Drivers", Classification = "Drivers", Version = "10.1.2.3", Size = "1.2 MB", SizeBytes = 1258291, LastUpdated = new DateTime(2026, 9, 1) };
            var back = CatalogDriverProvider.DecodeEntries(CatalogDriverProvider.EncodeEntries(new List<CatalogEntry> { e }));
            Check(back != null && back.Count == 1 && back[0].Guid == e.Guid && back[0].Title == e.Title && back[0].Products == e.Products && back[0].Version == e.Version &&
                  back[0].SizeBytes == e.SizeBytes && back[0].LastUpdated == e.LastUpdated, "entrada del catálogo: ida y vuelta");
            Eq(0, CatalogDriverProvider.DecodeEntries(CatalogDriverProvider.EncodeEntries(new List<CatalogEntry>())).Count, "sin resultados también se guarda");
            Eq(null, CatalogDriverProvider.DecodeEntries("{roto"), "guardado roto: se consulta de nuevo");

            Eq("45 s", Fmt.Elapsed(TimeSpan.FromSeconds(45)), "segundos");
            Eq("2 min 14 s", Fmt.Elapsed(TimeSpan.FromSeconds(134)), "minutos y segundos");
            Eq("3 min", Fmt.Elapsed(TimeSpan.FromMinutes(3)), "minutos justos");
            Eq("1 h 5 min", Fmt.Elapsed(TimeSpan.FromMinutes(65)), "horas");
        }

        static void TestGrabacion176()
        {
            // --- AIMP: la página nueva pone primero «32-bit», el tamaño y el hash, y DESPUÉS los enlaces (grabación del usuario:
            // se bajaba el de 32 bits creyendo que era el de 64, y el SHA-256 no coincidía).
            string nueva =
                "<table><tr><td><b>AIMP v5.40.2727</b><small>Release date: 08.09.2026 Targets to: Desktop</small></td></tr></table>" +
                "<div>For Windows. Architecture: x86 / x86-64. Required: Windows Vista or newer</div>" +
                "<h4>32-bit</h4><div>File Size: 17.7 MB</div><div>MD5: 1c891e06a1a0f81faa11e3e982b1f4ec</div>" +
                "<div>SHA256: f13fa3933dd749d944f030a47c6263f42b54c3c7bbb44e3cb76852b663d7386e</div>" +
                "<a href=\"https://aimp.ru/?do=download.file&amp;id=8\">Download build without the installer</a>" +
                "<a class=\"btn\" href=\"https://aimp.ru/?do=download.file&amp;id=4\">Download (17.7 MB)</a><a href=\"https://aimp.ru/?do=download.file&amp;id=4\"><img></a>" +
                "<h4>64-bit</h4><div>File Size: 21.0 MB</div><div>MD5: 61136b8cd8640b9ea5180f61ca6aa6b6</div>" +
                "<div>SHA256: cc6babde28777edcdf608d09d5abadb188c13a5fcbfe63740b53e76ea026c4cd</div><i>Plugins from old versions will not work</i>" +
                "<a href=\"https://aimp.ru/?do=download.file&amp;id=31\">Download build without the installer</a>" +
                "<a class=\"btn\" href=\"https://aimp.ru/?do=download.file&amp;id=3\">Download (21.0 MB)</a><a href=\"https://aimp.ru/?do=download.file&amp;id=3\"><img></a>" +
                "<table><tr><td><b>AIMP v6.00.3094 Beta 7</b></td></tr></table><h4>64-bit</h4><div>File Size: 21.8 MB</div>" +
                "<div>SHA256: 2e4e0968abfb389d41e7c96e6c41cc2a75ac9c8dc7a6b5b38275277c5529da2a</div><a href=\"https://aimp.ru/?do=download.file&amp;id=21\">Download (21.8 MB)</a>";
            var dl = VendorCatalog.ParseDownloads(nueva);
            var d64 = VendorCatalog.Pick(dl, "5.40.2727", "x64");
            var d32 = VendorCatalog.Pick(dl, "5.40.2727", "x86");
            Check(d64 != null && d64.Id == "3" && d64.Sha256 == "cc6babde28777edcdf608d09d5abadb188c13a5fcbfe63740b53e76ea026c4cd", "página nueva: 64 bits = id 3 con su hash (" + (d64 != null ? d64.Id : "nada") + ")");
            Check(d32 != null && d32.Id == "4" && d32.Sha256 == "f13fa3933dd749d944f030a47c6263f42b54c3c7bbb44e3cb76852b663d7386e", "página nueva: 32 bits = id 4 (" + (d32 != null ? d32.Id : "nada") + ")");
            Check(!dl.Exists(delegate(VendorDownload d) { return d.Id == "8" || d.Id == "31"; }), "nunca la versión sin instalador");
            Eq(null, VendorCatalog.Pick(dl, "6.00.3094", "x64"), "ni la beta");

            // --- Controlador del fabricante para un dispositivo con el genérico: «Actualizada» sólo si el dispositivo lo usa de verdad.
            // Grabación: «High Definition Audio Device» 10.0.26100.1 → 10.0.1.42 decía «Instalado correctamente» y volvía a aparecer.
            Eq(CatalogRules.InstallOutcome.NoAplicado, CatalogRules.Interpret(0, "Driver package added successfully.", @"HDAUDIO\FUNC_01&VEN_1002&DEV_AA01\5&1", "10.0.26100.1", "10.0.1.42", true),
               "sigue el genérico (número mayor): NO se instaló");
            Eq(CatalogRules.InstallOutcome.Actualizado, CatalogRules.Interpret(0, "", @"HDAUDIO\FUNC_01&VEN_1002&DEV_AA01\5&1", "10.0.1.42", "10.0.1.42", true),
               "el dispositivo informa la del fabricante: sí");
            Eq(CatalogRules.InstallOutcome.Actualizado, CatalogRules.Interpret(0, "", "X", "26.10.0.2", "26.10.0.2"), "un controlador de terceros, como siempre");
        }
    }
}