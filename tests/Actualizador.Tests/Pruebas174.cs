// Pruebas de la 1.7.4: auditoría después de la grabación (AMD Ryzen Master «4.1», filas que no se podían marcar, catálogo saturado).
using System.Collections.Generic;
using System;
using Actualizador.Core;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        // La portada de amd.com: noticias con versiones de OTROS productos de AMD.
        const string PortadaAmd = @"<html><head><title>AMD ׀ together we advance_AI</title></head><body>
<nav>Processors Graphics Software Drivers</nav>
<section><h2>News</h2><p>AI-Ready infrastructure. October 27, 2026 AMD FSR Upscaling 4.1 Boosts Frames and Image Quality in New Games</p>
<p>AMD Software: Adrenalin Edition 26.10.1 now available</p><p>AMD Ryzen 9 9950X3D review</p></section></body></html>";

        const string PaginaRyzenMaster = @"<html><head><title>AMD Ryzen™ Master Utility for Overclocking</title></head><body>
<h1>AMD Ryzen™ Master Utility</h1>
<p>Download AMD Ryzen Master 2.14.2.3341 — Requires Windows 11 64-bit 23H2.</p>
<a href=""https://drivers.amd.com/drivers/amd-software-adrenalin-edition-26.10.1-minimalsetup.exe"">AMD Software</a>
<a href=""/downloads/ryzen-master/amd-ryzen-master-2.14.2.3341.exe"">Download Ryzen Master</a>
<a href=""http://download.amd.com/ryzen-master-old.exe"">old</a></body></html>";

        static void TestAuditoria174()
        {
            // --- AMD Ryzen Master 2.8 «Disponible: 4.1» (era AMD FSR 4.1, de la portada de amd.com).
            var rm = new SiteVersions.NameMatcher("AMD Ryzen Master", "Advanced Micro Devices, Inc.");
            Check(!rm.Own.Contains("amd") && rm.Own.Contains("ryzen") && rm.Own.Contains("master"), "la marca (AMD = Advanced Micro Devices) no identifica al programa");
            Check(!rm.In(SiteVersions.Norm("AMD FSR Upscaling 4.1")), "«AMD FSR» no es Ryzen Master");
            Check(rm.In(SiteVersions.Norm("AMD Ryzen™ Master 2.14")), "«AMD Ryzen™ Master» sí");
            Eq(null, SiteVersions.Evaluate(PortadaAmd, "AMD Ryzen Master", "Advanced Micro Devices, Inc.", "2.8.0.1937", false, false, "https://www.amd.com/"), "la portada de AMD: nada (antes «4.1»)");
            var r = SiteVersions.Evaluate(PaginaRyzenMaster, "AMD Ryzen Master", "Advanced Micro Devices, Inc.", "2.8.0.1937", false, false, "https://www.amd.com/es/products/software/ryzen-master.html");
            Check(r != null && r.Latest == "2.14.2.3341", "su página de producto: la 2.14.2.3341 (" + (r != null ? r.Latest : "nada") + ")");
            Check(r != null && r.InstallerUrl == "https://www.amd.com/downloads/ryzen-master/amd-ryzen-master-2.14.2.3341.exe" && r.InstallerHasVersion,
                  "el instalador de Ryzen Master (no el de AMD Software ni uno por HTTP): " + (r != null ? r.InstallerUrl : ""));

            var gom = new SiteVersions.NameMatcher("GOM Player", "GOM & Company");
            Check(!gom.In(SiteVersions.Norm("Windows Media Player 12.0")) && gom.In(SiteVersions.Norm("GOM Player 2.3.123")), "GOM Player: «player» solo no alcanza");
            Check(new SiteVersions.NameMatcher("TightVNC", "GlavSoft LLC.").InFileName("tightvnc-2.8.85-gpl-setup-64bit.msi"), "nombre del instalador");
            Check(!gom.InFileName("player-setup.exe"), "un instalador genérico no es de GOM");

            // Palabras clave sin el nombre: sólo en una página que es del programa.
            Eq(null, SiteVersions.Evaluate("<html><head><title>Empresa X</title></head><body><p>Latest version: 9.2.1</p></body></html>", "ACDSee Pro", "ACD Systems", "9.0.108", false, false, "https://www.acdsee.com/"),
               "«Latest version» en una página que no es del programa: nada");
            var propia = SiteVersions.Evaluate("<html><head><title>ACDSee Pro 9 - descargas</title></head><body><p>Latest version: 9.2.1</p></body></html>", "ACDSee Pro", "ACD Systems", "9.0.108", false, false, "https://www.acdsee.com/");
            Check(propia != null && propia.Latest == "9.2.1", "en la página del programa, sí");
            Check(SiteVersions.Evaluate("<p>Latest version: 9.2.1</p>", "ACDSee Pro", "ACD Systems", "9.0.108", false, true, "https://x.example.com/") != null, "en la página que indicó el usuario, también");

            // --- La fila: con instalador del programa, se instala verificada; sin instalador, se puede marcar y abre la página.
            var ctx = ContextoSimulado(true);
            var p = Vc("AMD Ryzen Master", "2.8.0.1937", "x64");
            p.Publisher = "Advanced Micro Devices, Inc."; p.InstallLocation = @"C:\Program Files\AMD\RyzenMaster"; p.DisplayIcon = @"C:\Program Files\AMD\RyzenMaster\bin\AMD Ryzen Master.exe";
            r.Url = "https://www.amd.com/es/products/software/ryzen-master.html";
            var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = p.Name, InstalledVersion = p.Version, Status = UpdateStatus.NoVerificable, ProviderName = WingetProvider.ProviderId };
            Check(SiteVersions.Apply(ctx, c, p, r, false), "la fila cambia");
            var plan = c.ProviderTag as VendorPlan;
            Check(c.Status == UpdateStatus.Disponible && c.Installable && c.Recommended && c.ProviderName == VendorProvider.ProviderId && plan != null, "se puede marcar e instalar (y queda marcada: el archivo dice la versión)");
            Check(plan != null && plan.SitePage != null && plan.Check == null && plan.Repo == null && plan.InstallerUrl.EndsWith("2.14.2.3341.exe"), "con el instalador de su página");
            Check(plan != null && plan.Processes.Length == 1 && plan.Processes[0] == "AMD Ryzen Master", "cierra el programa antes de instalar");
            Check(c.Detail.Contains("mismo fabricante"), "dice cómo se verifica");
            var ctxUser = ContextoSimulado(false);
            var cu = new UpdateCandidate { Kind = UpdateKind.Programa, Name = p.Name, InstalledVersion = p.Version, Status = UpdateStatus.NoVerificable };
            var pSinCarpeta = Vc("AMD Ryzen Master", "2.8.0.1937", "x64"); pSinCarpeta.Publisher = p.Publisher;   // para todo el equipo, sin carpeta conocida
            SiteVersions.Apply(ctxUser, cu, pSinCarpeta, r, false);
            Check(!cu.Installable && cu.Detail.Contains("administrador"), "sin administrador, para todo el equipo: lo dice");

            var sinInstalador = new SiteReading { Latest = "2.9.0", Url = "https://www.tightvnc.com/download.php", Evidence = "TightVNC 2.9.0" };
            var t = Vc("TightVNC", "2.8.81", "x64");
            var ct = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "TightVNC", InstalledVersion = "2.8.81", Status = UpdateStatus.NoVerificable };
            SiteVersions.Apply(ctx, ct, t, sinInstalador, false);
            // 1.7.6: entra en «Actualizar todo» (abre su página); lo que winget daba al día (estricto), no.
            Check(ct.Installable && ct.Recommended && ct.ProviderName == SiteProvider.ProviderId && UpdateManager.CanInstall(ct), "sin instalador: se puede marcar y entra en «Actualizar todo» (abre su página)");
            var ctEstricta = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "TightVNC", InstalledVersion = "2.8.81", Status = UpdateStatus.AlDia, ProviderName = WingetProvider.ProviderId };
            SiteVersions.Apply(ctx, ctEstricta, t, sinInstalador, true);
            Check(ctEstricta.Installable && !ctEstricta.Recommended, "la de winget atrasado sin instalador: queda opcional");

            // «Actualizar seleccionadas» con esa fila: abre la página y queda pendiente.
            var abiertas = new List<string>();
            var oldOpen = SiteProvider.OpenForTests;
            try
            {
                SiteProvider.OpenForTests = delegate(string u) { abiertas.Add(u); };
                var man = new UpdateManager(new IUpdateProvider[] { new SiteProvider() });
                man.HistoryPath = "";
                man.RepeatUntilDone = false;
                var rep = UpdateSession.Create(ctx);
                rep.Updates.Add(ct);
                man.Install(ctx, rep, new List<UpdateCandidate> { ct }, false, delegate(UpdateCandidate x) { });
                Check(abiertas.Count == 1 && abiertas[0] == "https://www.tightvnc.com/download.php", "se abrió su página de descarga");
                Check(ct.Status == UpdateStatus.Pendiente && ct.Result.Contains("Buscar actualizaciones"), "queda pendiente hasta confirmar: " + ct.Result);
            }
            finally { SiteProvider.OpenForTests = oldOpen; }

            // --- El instalador descargado tiene que ser de este programa.
            var oldProd = SiteVersions.ProductOfForTests;
            try
            {
                SiteVersions.ProductOfForTests = delegate(string f) { return "AMD Software: Adrenalin Edition"; };
                Check(SiteVersions.ForeignInstaller("x.exe", p) != null, "otro producto del mismo fabricante: no se instala");
                SiteVersions.ProductOfForTests = delegate(string f) { return "AMD Ryzen™ Master"; };
                Eq(null, SiteVersions.ForeignInstaller("x.exe", p), "Ryzen Master: sí");
                SiteVersions.ProductOfForTests = delegate(string f) { return ""; };
                Eq(null, SiteVersions.ForeignInstaller("x.exe", p), "sin datos de producto: vale el nombre del archivo, ya controlado");
            }
            finally { SiteVersions.ProductOfForTests = oldProd; }
            Check(SiteVersions.IsSecureDownload(new Uri("https://www.amd.com/x.exe")) && !SiteVersions.IsSecureDownload(new Uri("http://www.amd.com/x.exe")), "sólo HTTPS para bajar instaladores");

            // --- Todo lo «Disponible» se puede marcar: lo que no tiene instalador verificable abre su página.
            var vc2005 = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Microsoft Visual C++ 2005 Redistributable", Status = UpdateStatus.Disponible, Installable = false,
                                                InfoUrl = "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist", Detail = "La última versión que publicó Microsoft es la 8.0.61001." };
            var admin = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "X", Status = UpdateStatus.Disponible, Installable = false, InfoUrl = "https://x.example.com/",
                                               Detail = "Para instalarla hace falta ejecutar Actualizador como administrador." };
            var sinSitio = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Y", Status = UpdateStatus.Disponible, Installable = false };
            var drv = new UpdateCandidate { Kind = UpdateKind.Controlador, Name = "Z", Status = UpdateStatus.Disponible, Installable = false, InfoUrl = "https://x.example.com/" };
            Eq(1, UpdateManager.MakeManualActionable(new List<UpdateCandidate> { vc2005, admin, sinSitio, drv }), "sólo Visual C++ 2005");
            Check(vc2005.Installable && vc2005.ProviderName == SiteProvider.ProviderId && !vc2005.Recommended && UpdateManager.CanInstall(vc2005), "Visual C++ 2005: se puede marcar (abre la página de Microsoft)");
            Check(!admin.Installable && !sinSitio.Installable && !drv.Installable, "lo que necesita administrador, lo que no tiene página y los controladores, igual que antes");

            // --- ACDSync junto a ACDSee (mismo fabricante, misma carpeta del fabricante): es un componente, no un portable.
            var acdsee = Vc("ACDSee 9 Administrador fotografico", "9.0.108", "x86");
            acdsee.Publisher = "ACD Systems Ltd."; acdsee.InstallLocation = @"C:\Program Files (x86)\ACD Systems\ACDSee\9.0\";
            Check(ShortcutPrograms.IsComponentOf(@"C:\Program Files (x86)\ACD Systems\ACDSync\ACDSync.exe", "ACD Systems, Ltd.", new[] { acdsee }, @"C:\Windows"), "ACDSync es un componente de ACDSee");
            Check(!ShortcutPrograms.IsComponentOf(@"C:\Program Files (x86)\Otra\x.exe", "ACD Systems, Ltd.", new[] { acdsee }, @"C:\Windows"), "en otra carpeta, no");
            Check(!ShortcutPrograms.IsComponentOf(@"C:\Program Files (x86)\ACD Systems\ACDSync\ACDSync.exe", "Otra Empresa", new[] { acdsee }, @"C:\Windows"), "de otra empresa, no");
            var vlcPf = Vc("VLC media player", "3.0.21", "x64"); vlcPf.Publisher = "VideoLAN"; vlcPf.InstallLocation = @"C:\Program Files\VideoLAN\VLC";
            Check(!ShortcutPrograms.IsComponentOf(@"C:\Program Files\Otro\x.exe", "VideoLAN", new[] { vlcPf }, @"C:\Windows"), "Program Files entero nunca cuenta como carpeta del fabricante");

            // --- Catálogo: un dispositivo con el controlador de Windows que no se pudo consultar no es un error para el usuario.
            var oldFetch = CatalogDriverProvider.FetchForTests;
            try
            {
                CatalogDriverProvider.FetchForTests = delegate(string u) { return "<html>error</html>"; };
                var cx = ContextoSimulado(true);
                cx.Os.Build = 19045;
                cx.Drivers.Add(Drv("Puerto raíz PCI Express", "System", "Microsoft", "10.0.19041.1", @"PCI\VEN_8086&DEV_A33C&SUBSYS_86941043&REV_F0"));
                cx.DriversLoaded = true;
                var outC = new List<UpdateCandidate>();
                var notasC = new List<string>();
                new CatalogDriverProvider().Scan(cx, outC, notasC);
                Check(!notasC.Exists(delegate(string n) { return n.StartsWith("No se pudo consultar el catálogo"); }), "sin «No se pudo consultar el catálogo para N controladores» por los de Windows");
            }
            finally { CatalogDriverProvider.FetchForTests = oldFetch; }
        }
    }
}
