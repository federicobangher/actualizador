// Fabricantes: navegadores, catálogo de fuentes oficiales, GitHub, Office, Adobe y fabricante del equipo.
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

        static void TestAcrobatReader64()
        {
            var reader = Registro(Uninst + "{AC76BA86-1033-FF00-7760-BC15014EA700}", "Adobe Acrobat (64-bit)", "26.001.21000", "Adobe", @"C:\Program Files\Adobe\Acrobat DC\",
                                  "MsiExec.exe /I{AC76BA86-1033-FF00-7760-BC15014EA700}", null, null, true);
            var readerEs = Registro(Uninst + "{AC76BA86-3082-3082-7760-BC15014EA700}", "Adobe Acrobat (64-bit)", "26.001.21000", "Adobe", "", "MsiExec.exe /I{AC76BA86-3082-3082-7760-BC15014EA700}", null, null, true);
            var acrobat = Registro(Uninst + "{AC76BA86-1033-FFFF-7760-BC15014EA700}", "Adobe Acrobat (64-bit)", "26.001.21000", "Adobe", @"C:\Program Files\Adobe\Acrobat DC\",
                                   "MsiExec.exe /I{AC76BA86-1033-FFFF-7760-BC15014EA700}", null, null, true);
            var reader32 = Registro(Uninst + "{AC76BA86-7AD7-1033-7B44-AC0F074E4100}", "Adobe Acrobat Reader DC", "24.0", "Adobe", "", "MsiExec.exe /I{AC76BA86-7AD7-1033-7B44-AC0F074E4100}", null, null, true);
            Check(AdobeRules.IsUnifiedReader(reader), "FF00: Reader de 64 bits");
            Check(AdobeRules.IsUnifiedReader(readerEs), "Reader de 64 bits en un idioma");
            Check(!AdobeRules.IsUnifiedReader(acrobat), "FFFF: Acrobat");
            Check(!AdobeRules.IsUnifiedReader(reader32), "Reader de 32 bits: no es el unificado");
            Check(!AdobeRules.IsUnifiedReader(null), "sin programa");
            var urls = AdobeRules.MspUrls(true, "x64", "26.002.21931");
            Check(urls.Count > 0 && urls[0].EndsWith("/AcroRdrDCx64Upd2600221931.msp"), "Reader x64: primero el parche de Reader (antes 1642 con el de Acrobat): " + (urls.Count > 0 ? urls[0] : ""));
            Check(urls[urls.Count - 1].EndsWith("/AcrobatDCx64Upd2600221931.msp"), "…y el de Acrobat queda de respaldo");
            urls = AdobeRules.MspUrls(false, "x64", "26.002.21931");
            Check(urls[0].EndsWith("/AcrobatDCx64Upd2600221931.msp"), "Acrobat x64: primero el de Acrobat");
        }

        /// <summary>
        /// GitHub y fabricantes: la arquitectura del programa REAL, no la vista del registro. Un instalador
        /// de 32 bits registra en WOW6432Node un programa de 64 bits; con «x86» se elegía el instalador de
        /// 32 bits del proyecto y quedaban dos copias.
        /// </summary>
        static void TestArquitecturaDeFabricantes()
        {
            var ctx = ContextoSimulado(true);
            var p = new InstalledProgram();
            p.Name = "Herramienta"; p.Version = "1.0"; p.Scope = "Equipo";
            p.Architecture = "x86";                                       // vista de 32 bits del registro
            p.InstallLocation = @"C:\Program Files\Herramienta";         // pero está en Program Files (64 bits)
            p.UninstallString = "\"C:\\Program Files\\Herramienta\\uninst.exe\"";
            Eq("x64", VendorProvider.ArchitectureOf(ctx, p), "carpeta de 64 bits manda sobre la vista del registro");

            var q = new InstalledProgram();
            q.Name = "Vieja"; q.Version = "1.0"; q.Scope = "Equipo"; q.Architecture = "x64";
            q.InstallLocation = @"C:\Program Files (x86)\Vieja";
            Eq("x86", VendorProvider.ArchitectureOf(ctx, q), "Program Files (x86): 32 bits aunque el registro diga otra cosa");

            var r = new InstalledProgram();
            r.Name = "Sin datos"; r.Version = "1.0"; r.Scope = "Equipo"; r.Architecture = "x86"; r.InstallLocation = "";
            Eq("x86", VendorProvider.ArchitectureOf(ctx, r), "sin ejecutable ni carpeta: la vista del registro");

            var s = new InstalledProgram();
            s.Name = "Nada"; s.Version = "1.0"; s.Scope = "Equipo"; s.Architecture = ""; s.InstallLocation = "";
            Eq("x64", VendorProvider.ArchitectureOf(ctx, s), "sin nada: la del equipo");
            Eq("x64", VendorProvider.ArchitectureOf(null, null), "sin contexto ni programa: x64");

            // Con la arquitectura corregida se elige el instalador de 64 bits del proyecto.
            var rel = GitHubRules.ParseRelease("{\"tag_name\":\"v2.0\",\"html_url\":\"https://github.com/o/r/releases/tag/v2.0\",\"assets\":[" +
                "{\"name\":\"Herramienta-2.0-x86-setup.exe\",\"browser_download_url\":\"https://github.com/o/r/releases/download/v2.0/Herramienta-2.0-x86-setup.exe\"}," +
                "{\"name\":\"Herramienta-2.0-x64-setup.exe\",\"browser_download_url\":\"https://github.com/o/r/releases/download/v2.0/Herramienta-2.0-x64-setup.exe\"}]}");
            var a = GitHubRules.PickAsset(rel, VendorProvider.ArchitectureOf(ctx, p), false);
            Check(a != null && a.Name.Contains("x64"), "GitHub: el instalador de 64 bits para el programa de 64 bits");
        }
        const string Ltsc2021Guid = "http://officecdn.microsoft.com/pr/5030841d-c919-4594-8d2d-84ae4f96e58e";
        const string Ltsc2019Guid = "http://officecdn.microsoft.com/pr/f2e724c1-748f-4b47-8fb8-8e0d210e9208";
        const string Office2021Uninstall = "\"C:\\Program Files\\Common Files\\Microsoft Shared\\ClickToRun\\OfficeClickToRun.exe\" scenario=install scenariosubtype=ARP sourcetype=None productstoremove=ProPlus2021Volume.16_es-es_x-none culture=es-es version.16=16.0";

        static void TestOffice2021()
        {
            var channels = OfficeC2R.ParseReleases(Sample("office_releases.json"));

            // --- Canal: LTSC 2021 por su dirección, por su nombre y por los productos.
            var cfg = new OfficeConfig();
            cfg.Found = true;
            cfg.CdnBaseUrl = Ltsc2021Guid;
            cfg.UpdateChannel = Ltsc2021Guid;
            var ch = OfficeC2R.ResolveChannel(channels, cfg);
            Check(ch != null && ch.Id == "PerpetualVL2021" && !ch.Retired && ch.Latest == "16.0.14334.20918", "Office LTSC 2021 por la dirección del canal");
            Eq("Office LTSC 2021", OfficeC2R.ChannelTitle(ch), "nombre del canal 2021");
            var porNombre = new OfficeConfig(); porNombre.Found = true; porNombre.PolicyBranch = "PerpetualVL2021";
            Eq("PerpetualVL2021", OfficeC2R.ResolveChannel(channels, porNombre).Id, "LTSC 2021 por el nombre de la directiva");
            var porProducto = new OfficeConfig(); porProducto.Found = true; porProducto.ProductReleaseIds = "ProPlus2021Volume,VisioPro2021Volume";
            var pp = OfficeC2R.ResolveChannel(channels, porProducto);
            Check(pp != null && pp.Id == "PerpetualVL2021" && pp.Latest.Length > 0, "sin canal en el registro: LTSC 2021 por los productos (con la versión del listado)");
            var retail = new OfficeConfig(); retail.Found = true; retail.OfficeProductReleaseIds = "HomeBusiness2021Retail";
            Eq("Current", OfficeC2R.ResolveChannel(channels, retail).Id, "Office Hogar y Empresas 2021 (comercial): Canal actual");
            var nada = new OfficeConfig(); nada.Found = true;
            Check(OfficeC2R.ResolveChannel(channels, nada) == null, "sin canal ni productos: null");

            // --- Canales que Microsoft ya no publica.
            var c2019 = new OfficeConfig(); c2019.Found = true; c2019.CdnBaseUrl = Ltsc2019Guid;
            var r2019 = OfficeC2R.ResolveChannel(channels, c2019);
            Check(r2019 != null && r2019.Id == "PerpetualVL2019" && r2019.Retired && r2019.Latest == "", "Office LTSC 2019: canal conocido, sin versión publicada (antes: «no se reconoce el canal»)");
            Eq(new DateTime(2025, 10, 14), OfficeC2R.EndOfSupportOf(r2019), "fin de soporte de Office 2019");
            var sin2021 = new List<OfficeChannel>(channels);
            sin2021.RemoveAll(delegate(OfficeChannel x) { return x.Id == "PerpetualVL2021"; });
            var r2021 = OfficeC2R.ResolveChannel(sin2021, cfg);
            Check(r2021 != null && r2021.Retired && r2021.Id == "PerpetualVL2021", "Office 2021 cuando Microsoft lo saque del listado: reconocido");
            Eq(new DateTime(2026, 10, 13), OfficeC2R.EndOfSupportOf(r2021), "fin de soporte de Office 2021");
            Eq(new DateTime(2026, 10, 13), OfficeC2R.EndOfSupportOf(ch), "fin de soporte también con el canal del listado");
            var prioridad = new OfficeConfig(); prioridad.Found = true; prioridad.UpdateChannel = Ltsc2019Guid; prioridad.CdnBaseUrl = Ltsc2021Guid;
            Eq("PerpetualVL2019", OfficeC2R.ResolveChannel(channels, prioridad).Id, "UpdateChannel retirado igual manda sobre CDNBaseUrl");

            // --- La red de Microsoft no es «un servidor de la empresa».
            Check(OfficeC2R.IsMicrosoftCdn(Ltsc2021Guid), "officecdn.microsoft.com");
            Check(OfficeC2R.IsMicrosoftCdn("https://officecdn.microsoft.com/pr/492350f6-3a01-4f97-b9c0-c7c6ddf67d60"), "officecdn por HTTPS");
            Check(OfficeC2R.IsMicrosoftCdn("http://officecdn.microsoft.com.edgesuite.net/pr/492350f6-3a01-4f97-b9c0-c7c6ddf67d60"), "edgesuite de Microsoft");
            Check(!OfficeC2R.IsMicrosoftCdn(@"\\servidor\office"), "carpeta de red: no");
            Check(!OfficeC2R.IsMicrosoftCdn("http://officecdn.microsoft.com.evil.example/pr/x"), "dominio que sólo empieza igual: no");
            Check(!OfficeC2R.IsMicrosoftCdn("https://evil.example/officecdn.microsoft.com"), "el nombre en la ruta: no");
            Check(!OfficeC2R.IsMicrosoftCdn("file://officecdn.microsoft.com/x"), "otro esquema: no");
            Check(!OfficeC2R.IsMicrosoftCdn(""), "vacío: no");

            var otp = new OfficeConfig(); otp.Found = true; otp.OdtUpdatePath = Ltsc2021Guid; otp.UnmanagedUpdateUrl = "http://officecdn.microsoft.com/pr/492350f6-3a01-4f97-b9c0-c7c6ddf67d60";
            Eq("", OfficeC2R.ManagedSource(otp), "Office Tool Plus / ODT con la dirección del canal: no está administrado");
            Check(!OfficeC2R.IsManaged(otp), "IsManaged coincide");
            var empresa = new OfficeConfig(); empresa.Found = true; empresa.OdtUpdatePath = @"\\srv\office$";
            Eq(@"\\srv\office$", OfficeC2R.ManagedSource(empresa), "carpeta de red de la empresa: administrado");
            var directiva = new OfficeConfig(); directiva.Found = true; directiva.PolicyPath = "http://sccm.empresa.local/office"; directiva.OdtUpdatePath = Ltsc2021Guid;
            Eq("http://sccm.empresa.local/office", OfficeC2R.ManagedSource(directiva), "la directiva con un servidor propio manda");

            // --- Qué se ofrece.
            var ctx = new UpdateContext(); ctx.IsAdmin = true;
            var c = new UpdateCandidate();
            string det = VendorProvider.OfficeInstallDetail(ctx, otp, "16.0.14334.20918", c);
            Check(c.Installable && det.Contains("16.0.14334.20918"), "Office 2021 de Office Tool Plus: se ofrece instalar (antes: «lo administra la empresa»)");
            c = new UpdateCandidate();
            VendorProvider.OfficeInstallDetail(ctx, empresa, "16.0.14334.20918", c);
            Check(!c.Installable, "administrado por la empresa: no se instala desde acá");
            var fijado = new OfficeConfig(); fijado.Found = true; fijado.PolicyTargetVersion = "16.0.14332.20000";
            c = new UpdateCandidate();
            string detFijado = VendorProvider.OfficeInstallDetail(ctx, fijado, "16.0.14334.20918", c);
            Check(!c.Installable && detFijado.Contains("updatetargetversion"), "una directiva fija la versión: no se instala");
            var apagado = new OfficeConfig(); apagado.Found = true; apagado.UpdatesEnabled = "False";
            c = new UpdateCandidate();
            string detApagado = VendorProvider.OfficeInstallDetail(ctx, apagado, "16.0.14334.20918", c);
            Check(c.Installable && detApagado.Contains("se activan mientras se actualiza"), "actualizaciones desactivadas: se activan mientras dura (administrador)");
            Check(apagado.UpdatesDisabled && !otp.UpdatesDisabled, "UpdatesDisabled");
            var usuario = new UpdateContext();
            c = new UpdateCandidate();
            Check(VendorProvider.OfficeInstallDetail(usuario, apagado, "16.0.14334.20918", c).Contains("como administrador"), "sin administrador: se explica");

            // --- Argumentos: la versión exacta.
            Eq("/update user updatepromptuser=false forceappshutdown=false displaylevel=false updatetoversion=16.0.14334.20918",
               OfficeC2R.UpdateArguments(false, "16.0.14334.20918"), "con la versión exacta");
            Eq("/update user updatepromptuser=true forceappshutdown=false displaylevel=true", OfficeC2R.UpdateArguments(true, null), "interactivo, sin versión");
            Check(OfficeC2R.UpdateArguments(false, "16.0.1 & calc").IndexOf("updatetoversion", StringComparison.Ordinal) < 0, "una versión rara no pasa a la línea de comandos");
            Check(OfficeC2R.UpdateArguments(false, "16.0.14334.20918 x").IndexOf("updatetoversion", StringComparison.Ordinal) < 0, "con algo después: no");

            // --- Versión instalada: la más nueva de las dos.
            var inv = new OfficeConfig(); inv.OfficePackageVersion = "16.0.14332.20255"; inv.VersionToReport = "16.0.14334.20918";
            Eq("16.0.14334.20918", OfficeC2R.InstalledVersion(inv), "inventario viejo: manda VersionToReport");
            inv.OfficePackageVersion = "16.0.14334.20918"; inv.VersionToReport = "16.0.14332.20255";
            Eq("16.0.14334.20918", OfficeC2R.InstalledVersion(inv), "al revés también");
            inv.OfficePackageVersion = ""; Eq("16.0.14332.20255", OfficeC2R.InstalledVersion(inv), "sin inventario");
            inv.VersionToReport = "x"; Eq("", OfficeC2R.InstalledVersion(inv), "nada legible");

            // --- Cualquier nombre de Office «Hacer clic y ejecutar», en cualquier idioma.
            var office = VendorCatalog.Find("office");
            var apps = Prog("Aplicaciones de Microsoft 365 para empresas - es-es", "16.0.19127.20100", "Microsoft Corporation", Office2021Uninstall);
            Check(office.Applies(apps), "«Aplicaciones de Microsoft 365 para empresas» (antes no se comparaba)");
            Check(office.Applies(Prog("Microsoft Office LTSC Profesional Plus 2021 - es-es", "16.0.14332.20255", "Microsoft Corporation", Office2021Uninstall)), "Office LTSC 2021 en castellano");
            Check(!office.Applies(Prog("Aplicaciones de algo", "1.0", "", "MsiExec.exe /X{1}")), "otro programa: no");
            Check(Array.IndexOf(office.Processes, "lync") >= 0 && Array.IndexOf(office.Processes, "ONENOTEM") >= 0, "Skype Empresarial y «Enviar a OneNote» entre los procesos de Office");
            var plan = new VendorPlan(); plan.Check = office; plan.Processes = office.Processes;
            Check(VendorProvider.ClosesProcesses(plan), "Office: se ofrece cerrar los programas abiertos (antes no)");

            // --- Office 2013 «Hacer clic y ejecutar».
            Check(OfficeC2R.IsLegacyClickToRun("15.0.5571.1000") && OfficeC2R.IsLegacyClickToRun("14.0.7268.5000"), "Office 2013/2010 C2R");
            Check(!OfficeC2R.IsLegacyClickToRun("16.0.14332.20255") && !OfficeC2R.IsLegacyClickToRun("") && !OfficeC2R.IsLegacyClickToRun(null), "Office 2016+ no");

            // --- ¿Llegó?
            Check(VendorProvider.Reached("16.0.14334.20918", "16.0.14334.20918", "16.0.14332.20255"), "llegó a la pedida");
            Check(!VendorProvider.Reached("16.0.14332.20255", "16.0.14334.20918", "16.0.14332.20255"), "no llegó");
            Check(VendorProvider.Reached("16.0.14332.20300", "", "16.0.14332.20255"), "sin versión pedida: cualquiera más nueva");
            Check(!VendorProvider.Reached("", "16.0.1.1", "16.0.1.0"), "ilegible: no");
        }

        /// <summary>El flujo de instalación de Office con el actualizador simulado.</summary>
        static void TestOffice2021Instalacion()
        {
            var office = VendorCatalog.Find("office");
            Func<string, VendorPlan> newPlan = delegate(string latest)
            {
                var p = new VendorPlan(); p.Check = office; p.Processes = new[] { "proceso-que-no-existe-sp163" }; p.Latest = latest; return p;
            };
            Func<VendorPlan, UpdateCandidate> newCandidate = delegate(VendorPlan p)
            {
                var c = new UpdateCandidate(); c.Kind = UpdateKind.Programa; c.Name = "Microsoft Office LTSC Profesional Plus 2021 - es-es";
                c.ProviderName = VendorProvider.ProviderId; c.ProviderTag = p; c.Installable = true; c.Status = UpdateStatus.Disponible; return c;
            };
            var ctx = new UpdateContext(); ctx.IsAdmin = true;
            double wait = VendorProvider.OfficeWaitMinutes;
            try
            {
                VendorProvider.OfficeWaitMinutes = 0.01;

                // 1) Con la versión exacta, y Office llega.
                var calls = new List<string>();
                string version = "16.0.14332.20255";
                VendorProvider.OfficeConfigForTests = delegate() { var o = new OfficeConfig(); o.Found = true; o.VersionToReport = version; return o; };
                VendorProvider.OfficeClientForTests = delegate(string args) { calls.Add(args); version = "16.0.14334.20918"; return 0; };
                var plan = newPlan("16.0.14334.20918");
                var c = newCandidate(plan);
                new VendorProvider().Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Eq(UpdateStatus.Instalada, c.Status, "Office 2021: actualizada (" + c.Result + ")");
                Check(calls.Count == 1 && calls[0].EndsWith("updatetoversion=16.0.14334.20918"), "se pidió la versión exacta: " + string.Join(" | ", calls.ToArray()));
                Eq("16.0.14334.20918", c.InstalledVersion, "versión que quedó");

                // 2) La versión exacta falla: se reintenta sin ella.
                calls.Clear(); version = "16.0.14332.20255";
                VendorProvider.OfficeClientForTests = delegate(string args)
                {
                    calls.Add(args);
                    if (args.Contains("updatetoversion")) return 17004;
                    version = "16.0.14334.20918"; return 0;
                };
                c = newCandidate(newPlan("16.0.14334.20918"));
                new VendorProvider().Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(calls.Count == 2 && !calls[1].Contains("updatetoversion"), "segundo intento sin versión exacta");
                Eq(UpdateStatus.Instalada, c.Status, "y se actualizó");

                // 3) Las dos fallan: error con el código.
                VendorProvider.OfficeClientForTests = delegate(string args) { return 17004; };
                version = "16.0.14332.20255";
                c = newCandidate(newPlan("16.0.14334.20918"));
                new VendorProvider().Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("17004"), "error del actualizador: " + c.Result);

                // 4) Actualizaciones desactivadas y Office no termina: pendiente, y se avisa que quedaron activadas.
                VendorProvider.OfficeConfigForTests = delegate() { var o = new OfficeConfig(); o.Found = true; o.VersionToReport = "16.0.14332.20255"; o.UpdatesEnabled = "False"; return o; };
                VendorProvider.OfficeClientForTests = delegate(string args) { return 0; };
                c = newCandidate(newPlan("16.0.14334.20918"));
                new VendorProvider().Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Pendiente && c.Result.Contains("quedaron activadas"), "pendiente con las actualizaciones activadas: " + c.Result);

                // 5) -1 del actualizador no es un error por sí solo.
                version = "16.0.14332.20255";
                VendorProvider.OfficeConfigForTests = delegate() { var o = new OfficeConfig(); o.Found = true; o.VersionToReport = version; return o; };
                VendorProvider.OfficeClientForTests = delegate(string args) { version = "16.0.14334.20918"; return -1; };
                c = newCandidate(newPlan("16.0.14334.20918"));
                new VendorProvider().Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Eq(UpdateStatus.Instalada, c.Status, "-1 y la versión cambió: actualizada");
            }
            finally
            {
                VendorProvider.OfficeClientForTests = null;
                VendorProvider.OfficeConfigForTests = null;
                VendorProvider.OfficeWaitMinutes = wait;
            }
        }

        static void TestOfficeMsi()
        {
            const string setup = "\"C:\\Program Files\\Common Files\\Microsoft Shared\\OFFICE16\\Office Setup Controller\\setup.exe\" /uninstall PROPLUS /dll OSETUP.DLL";
            var pro2016 = Prog("Microsoft Office Professional Plus 2016", "16.0.4266.1001", "Microsoft Corporation", setup);
            pro2016.UninstallCommand = setup;
            Check(OfficeC2R.IsOfficeMsi(pro2016), "Office 2016 MSI");
            Check(!OfficeC2R.IsOfficeMsi(Prog("Microsoft Office LTSC Profesional Plus 2021 - es-es", "16.0.14332.20255", "Microsoft Corporation", Office2021Uninstall)), "Office C2R no es MSI");
            var proofing = Prog("Microsoft Office Proofing Tools 2016 - Español", "16.0.4266.1001", "Microsoft Corporation", "MsiExec.exe /X{90160000-001F-0C0A-1000-0000000FF1CE}");
            proofing.IsWindowsInstaller = true;
            Check(!OfficeC2R.IsOfficeMsi(proofing), "herramientas de corrección: no es el producto");
            var ace = Prog("Microsoft Access database engine 2016 (English)", "16.0.5044.1000", "Microsoft Corporation", "MsiExec.exe /X{90160000-00D1-0409-1000-0000000FF1CE}");
            ace.IsWindowsInstaller = true;
            Check(!OfficeC2R.IsOfficeMsi(ace), "Access database engine: no");
            var visio = Prog("Microsoft Visio Professional 2013", "15.0.4569.1506", "Microsoft Corporation", "MsiExec.exe /X{90150000-0051-0000-1000-0000000FF1CE}");
            visio.IsWindowsInstaller = true;
            Check(OfficeC2R.IsOfficeMsi(visio), "Visio 2013 MSI");

            var progs = new List<InstalledProgram> { proofing, pro2016 };
            Check(WindowsUpdateProvider.FindOfficeMsi(progs) == pro2016, "se encuentra el Office MSI de la lista");
            Check(WindowsUpdateProvider.FindOfficeMsi(new List<InstalledProgram> { proofing }) == null, "sin Office MSI");

            var admin = new UpdateContext(); admin.IsAdmin = true;
            var mu = WindowsUpdateProvider.MicrosoftUpdateCandidate(admin, pro2016);
            Check(mu.Installable && mu.Recommended && mu.Status == UpdateStatus.Disponible && mu.ProviderTag is WindowsUpdateProvider.MicrosoftUpdateActivation,
                  "fila para activar Microsoft Update (administrador)");
            Check(mu.Detail.Contains("Microsoft Office Professional Plus 2016"), "nombra el Office instalado");
            var comun = WindowsUpdateProvider.MicrosoftUpdateCandidate(new UpdateContext(), pro2016);
            Check(!comun.Installable && comun.Detail.Contains("administrador"), "sin administrador: no se puede activar desde acá");

            // Microsoft Store en ediciones LTSC (sin Store).
            var appsSinStore = new List<AppxInfo> { new AppxInfo { Name = "Microsoft.WindowsCalculator" } };
            var appsConStore = new List<AppxInfo> { new AppxInfo { Name = "Microsoft.WindowsStore" } };
            Check(!StoreProvider.StoreAppPresent(appsSinStore, false), "LTSC sin Store: no se ofrece «Todas las apps»");
            Check(StoreProvider.StoreAppPresent(appsConStore, false), "con la app Store en el inventario: sí");
            Check(StoreProvider.StoreAppPresent(appsSinStore, null), "sin poder mirar WindowsApps: se supone que está");
            Check(StoreProvider.StoreAppPresent(appsSinStore, true), "la carpeta existe (otro usuario elevado): sí");

            var hint = ProgramHints.For(pro2016);
            Check(hint != null && hint.Text.Contains("Microsoft Update") && hint.Text.Contains("14/10/2025"), "pista de Office MSI con su fin de soporte");
            Check(Fmt.WebUrl(hint.Url) != null, "sitio oficial válido");
        }
        const string AdobeIndice = "https://www.adobe.com/devnet-docs/acrobatetk/tools/ReleaseNotesDC/index.html";
        const string AdobeNotas = "https://www.adobe.com/devnet-docs/acrobatetk/tools/ReleaseNotesDC/continuous/";

        static void TestAdobeNotasDeVersion()
        {
            // --- Índice: la más nueva primero, sin repetir (el «next» y la lista), sin ancla, sólo la vía continua.
            var pages = AdobeRules.ReleasePages(Sample("adobe_notas_indice.html"), AdobeIndice);
            Eq(AdobeRules.MaxReleasePages, pages.Count, "índice: como mucho " + AdobeRules.MaxReleasePages + " notas");
            Eq(AdobeNotas + "dccontinuousoct2026.html", pages[0], "índice: primero la de octubre, dirección absoluta y sin ancla");
            Eq(AdobeNotas + "dccontinuousaug2026qfe2.html", pages[1], "índice: después la anterior (sin repetir la del «next»)");
            Check(!pages.Exists(delegate(string p) { return p.Contains("acrobatclassic"); }), "índice: la vía clásica no entra");
            Eq(AdobeRules.ReleaseNotesUrl, AdobeIndice, "dirección del índice");
            Eq(0, AdobeRules.ReleasePages("<html>Access denied</html>", AdobeIndice).Count, "índice inesperado: sin notas");
            Eq(0, AdobeRules.ReleasePages("<a href=\"http://www.adobe.com/x/continuous/dccontinuousoct2026.html\">x</a>", AdobeIndice).Count, "nota por HTTP: no se acepta");

            // --- Nota de octubre: la versión de WINDOWS, no la de Mac.
            var rel = AdobeRules.ParseWindowsRelease(Sample("adobe_notas_oct2026.html"));
            Check(rel != null, "nota de octubre: tiene parches de Windows");
            Eq("26.002.21996", rel.Version, "nota de octubre: 26.002.21996 (Windows), no 26.002.22008 (Mac)");
            Eq(6, rel.Patches.Count, "nota de octubre: 6 parches de Windows (32 y 64 bits), ningún .dmg");
            Eq(null, AdobeRules.ParseWindowsRelease(Sample("adobe_notas_solo_mac.html")), "nota sólo para Mac: sin versión de Windows");
            Eq(null, AdobeRules.ParseWindowsRelease("https://ardownload3.adobe.com/pub/adobe/acrobat/win/AcrobatDC/2600221996/AcrobatDCx64Upd2600221931.msp"), "carpeta y archivo de versiones distintas: no se confía");
            Eq("26.002.21996", AdobeRules.VersionFromFolder("2600221996"), "carpeta → versión");
            Eq(null, AdobeRules.VersionFromFolder("2400130429"), "carpeta de la vía clásica: no");

            // --- Las direcciones exactas que publica la nota (ardownload3).
            var urls = AdobeRules.MspUrls(false, "x64", rel.Version, rel.Patches);
            Eq("https://ardownload3.adobe.com/pub/adobe/acrobat/win/AcrobatDC/2600221996/AcrobatDCx64Upd2600221996.msp", urls[0], "Acrobat x64: dirección publicada");
            var r86 = AdobeRules.MspUrls(true, "x86", rel.Version, rel.Patches);
            Eq("https://ardownload3.adobe.com/pub/adobe/reader/win/AcrobatDC/2600221996/AcroRdrDCUpd2600221996.msp", r86[0], "Reader x86: dirección publicada");
            foreach (string u in urls) Check(VendorCatalog.Find("acrobat").Allows(new Uri(u)), "parche en un dominio de Adobe: " + u);
            Eq(AdobeRules.MspUrls(false, "x64", "26.002.21996")[0], AdobeRules.MspUrls(false, "x64", "26.002.21996", null)[0], "sin nota: las de ardownload2 como antes");

            // --- El catálogo ya no consulta el manifiesto de Mac.
            foreach (string id in new[] { "acrobat", "acrobat-reader" })
            {
                var check = VendorCatalog.Find(id);
                Eq(VendorFeed.AdobeReleaseNotes, check.Feed, id + ": versión de las notas de Windows");
                Check(check.Url.IndexOf("/mac/", StringComparison.OrdinalIgnoreCase) < 0, id + ": no lee la fuente de Mac");
            }
        }

        static List<UpdateCandidate> BuscarAdobe(InstalledProgram p, Func<string, string> fuente)
        {
            var ctx = new UpdateContext();
            ctx.IsAdmin = true;
            ctx.Programs.Add(p);
            var output = new List<UpdateCandidate>();
            VendorProvider.FetchForTests = fuente;
            try { new VendorProvider().Scan(ctx, output, new List<string>()); }
            finally { VendorProvider.FetchForTests = null; }
            return output;
        }

        static string FuenteAdobe(string url, bool soloMacPrimero)
        {
            if (url == AdobeIndice)
            {
                string indice = Sample("adobe_notas_indice.html");
                return soloMacPrimero ? indice.Replace("continuous/dccontinuousoct2026.html", "continuous/dccontinuousoct2026mac.html")
                                              .Replace("<li><a class=\"reference internal\" href=\"continuous/dccontinuousaug2026qfe2.html", "<li><a class=\"reference internal\" href=\"continuous/dccontinuousoct2026.html\">x</a></li><li><a class=\"reference internal\" href=\"continuous/dccontinuousaug2026qfe2.html")
                                      : indice;
            }
            if (url == AdobeNotas + "dccontinuousoct2026.html") return Sample("adobe_notas_oct2026.html");
            if (url == AdobeNotas + "dccontinuousoct2026mac.html") return Sample("adobe_notas_solo_mac.html");
            throw new InvalidOperationException("no se esperaba consultar " + url);
        }

        static void TestAdobeBusquedaWindows()
        {
            const string AcrobatX64 = "MsiExec.exe /I{AC76BA86-1033-FFFF-7760-BC15014EA700}";

            // --- El caso del registro: 26.002.21996 instalada → al día (antes: «Disponible 26.002.22008» y «Falló la instalación»).
            var rows = BuscarAdobe(Prog("Adobe Acrobat (64-bit)", "26.002.21996", "Adobe", AcrobatX64), delegate(string u) { return FuenteAdobe(u, false); });
            Eq(1, rows.Count, "una fila para Acrobat");
            Eq(UpdateStatus.AlDia, rows[0].Status, "26.002.21996 instalada: al día");
            Eq("26.002.21996", rows[0].AvailableVersion, "disponible: la de Windows");
            Check(!rows[0].Installable, "al día: no se ofrece instalar");

            // --- Una versión anterior: se ofrece la de Windows con el parche publicado.
            rows = BuscarAdobe(Prog("Adobe Acrobat (64-bit)", "26.002.21931", "Adobe", AcrobatX64), delegate(string u) { return FuenteAdobe(u, false); });
            Eq(UpdateStatus.Disponible, rows[0].Status, "26.002.21931: hay una más nueva");
            Eq("26.002.21996", rows[0].AvailableVersion, "se ofrece 26.002.21996");
            Check(rows[0].Installable, "con administrador: se puede instalar");
            var plan = (VendorPlan)rows[0].ProviderTag;
            Eq("https://ardownload3.adobe.com/pub/adobe/acrobat/win/AcrobatDC/2600221996/AcrobatDCx64Upd2600221996.msp", plan.MspUrls[0], "primero el parche de Acrobat x64 publicado");

            // --- Reader de 32 bits: misma fuente, parche de Reader.
            rows = BuscarAdobe(Prog("Adobe Acrobat Reader DC - Español", "26.002.21931", "Adobe", ""), delegate(string u) { return FuenteAdobe(u, false); });
            Eq("26.002.21996", rows[0].AvailableVersion, "Reader: la versión de Windows");

            // --- La nota más nueva es sólo para Mac: se usa la anterior que tiene Windows.
            rows = BuscarAdobe(Prog("Adobe Acrobat (64-bit)", "26.002.21996", "Adobe", AcrobatX64), delegate(string u) { return FuenteAdobe(u, true); });
            Eq(UpdateStatus.AlDia, rows[0].Status, "nota sólo para Mac primero: se toma la de Windows y queda al día");
            Eq("26.002.21996", rows[0].AvailableVersion, "nota sólo para Mac primero: 26.002.21996");

            // --- Sin conexión con Adobe: error de consulta, no una versión inventada.
            rows = BuscarAdobe(Prog("Adobe Acrobat (64-bit)", "26.002.21996", "Adobe", AcrobatX64), delegate(string u) { throw new InvalidOperationException("no se pudo conectar"); });
            Eq(UpdateStatus.Error, rows[0].Status, "Adobe no responde: error de consulta");
            Check(!rows[0].Installable, "Adobe no responde: nada para instalar");

            // --- El índice cambió y no tiene notas: error claro.
            rows = BuscarAdobe(Prog("Adobe Acrobat (64-bit)", "26.002.21931", "Adobe", AcrobatX64), delegate(string u) { return "<html><body>Página nueva</body></html>"; });
            Eq(UpdateStatus.Error, rows[0].Status, "índice sin notas: error, no «Disponible»");
        }

        // ------------------------------------------------------------------ Mullvad Browser en el catálogo de fabricantes

        static void TestMullvadEnElCatalogo()
        {
            var check = VendorCatalog.Find("mullvad-browser");
            Check(check != null && check.Feed == VendorFeed.GitHubRelease && check.Install == VendorInstall.SignedInstaller, "Mullvad Browser en el catálogo, con GitHub y firmante fijo");
            Eq("mullvad-browser", IdOf(VendorCatalog.Match(MullvadReal("15.0.10"))), "el Mullvad Browser real coincide");
            Eq(null, IdOf(VendorCatalog.Match(Prog("Mullvad VPN", "2026.5", "Mullvad VPN AB", ""))), "la VPN no");
            // El instalador oficial está firmado por The Tor Project (verificado en el 15.0.24 real).
            Check(InstallerRules.SignerAccepted("C=US, ST=New Hampshire, L=Winchester, O=\"THE TOR PROJECT, INC.\", CN=\"THE TOR PROJECT, INC.\"", check.Signers), "firmante real aceptado");
            Check(!InstallerRules.SignerAccepted("C=US, O=Mullvad Evil, CN=Mullvad Evil", check.Signers), "otro firmante no");
            Check(check.Allows(new Uri("https://api.github.com/repos/mullvad/mullvad-browser/releases/latest")), "la API de GitHub");
            Check(check.Allows(new Uri("https://github.com/mullvad/mullvad-browser/releases/download/15.0.24/x.exe")), "la descarga de GitHub");
            Check(check.Allows(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/x")), "la redirección de la descarga");
            Check(!check.Allows(new Uri("https://githubusercontent.com.evil.example/x")), "un dominio parecido no");
            Check(!check.Allows(new Uri("http://github.com/mullvad/x.exe")), "sin HTTPS no");

            string json = Sample("github_release_mullvad.json");
            VendorProvider.FetchForTests = delegate(string url) { return url == check.Url ? json : null; };
            try
            {
                SinArchivosSimulados();
                var vp = new VendorProvider();
                var salida = new List<UpdateCandidate>();
                vp.Scan(ContextoSimulado(true, MullvadReal("15.0.10")), salida, new List<string>());
                var c = salida.Find(delegate(UpdateCandidate x) { return x.Identifier == "fabricante:mullvad-browser"; });
                Check(c != null && c.Status == UpdateStatus.Disponible && c.AvailableVersion == "15.0.24" && c.Installable, "Mullvad 15.0.10: disponible la 15.0.24: " + (c != null ? c.Detail : "sin fila"));
                var plan = c != null ? c.ProviderTag as VendorPlan : null;
                Check(plan != null && plan.InstallerUrl.EndsWith("/mullvad-browser-windows-x86_64-15.0.24.exe") && plan.InstallerSha256 == "85fd08c33e4cc0e0f94e92104daba5020f9bcd275d721895eddb113d84588096",
                      "el instalador (no el portable) con el SHA-256 que publica GitHub: " + (plan != null ? plan.InstallerUrl : ""));
                Check(c != null && c.Detail.Contains("THE TOR PROJECT") && c.Detail.Contains("SHA-256"), "el detalle dice qué se verifica");
                Eq(1, salida.Count, "sin otra fila de la verificación genérica de GitHub (lo cubre el catálogo)");

                // version.json ya en la 15.0.24 aunque el registro diga 15.0.10: al día.
                ArchivosSimulados(null, new Dictionary<string, string> { { MullvadDir + @"\version.json", "{\"version\":\"15.0.24\"}" } });
                salida = new List<UpdateCandidate>();
                vp.Scan(ContextoSimulado(true, MullvadReal("15.0.10")), salida, new List<string>());
                c = salida.Find(delegate(UpdateCandidate x) { return x.Identifier == "fabricante:mullvad-browser"; });
                Check(c != null && c.Status == UpdateStatus.AlDia && c.InstalledVersion == "15.0.24", "version.json al día: al día");

                // Con winget ofreciendo la misma actualización instalable, se prefiere winget; sin eso, queda la del fabricante.
                SinArchivosSimulados();
                salida = new List<UpdateCandidate>();
                vp.Scan(ContextoSimulado(true, MullvadReal("15.0.10")), salida, new List<string>());
                var w = FilaWinget("Mullvad Browser", "MullvadVPN.MullvadBrowser", "15.0.10", "15.0.24");
                salida.Add(w);
                UpdateManager.DeduplicateVendor(salida);
                Check(salida.Count == 1 && salida[0] == w, "winget instalable: se usa winget");
                salida = new List<UpdateCandidate>();
                vp.Scan(ContextoSimulado(true, MullvadReal("15.0.10")), salida, new List<string>());
                w = FilaWinget("Mullvad Browser", "MullvadVPN.MullvadBrowser", "15.0.10", "");
                w.Status = UpdateStatus.AlDia; w.Installable = false;    // winget todavía no tiene la versión nueva
                salida.Add(w);
                UpdateManager.DeduplicateVendor(salida);
                Check(salida.Count == 1 && salida[0].ProviderName == VendorProvider.ProviderId, "winget atrasado: queda la del fabricante");
            }
            finally { VendorProvider.FetchForTests = null; LectoresOriginales(); }

            // Veredicto después de instalar con el instalador del fabricante.
            var v = FilaWinget("Mullvad Browser", "x", "15.0.10", "15.0.24");
            VendorProvider.ConfirmVerdict(v, "15.0.10", "15.0.24", "15.0.24", "15.0.24", "su archivo «version.json»", false, "firmado por THE TOR PROJECT, INC.", false);
            Check(v.Status == UpdateStatus.Instalada && v.InstalledVersion == "15.0.24", "correcta: " + v.Result);
            v = FilaWinget("Mullvad Browser", "x", "15.0.10", "15.0.24");
            VendorProvider.ConfirmVerdict(v, "15.0.10", "15.0.24", "15.0.24", "15.0.10", "su archivo «version.json»", false, null, false);
            Check(v.Status == UpdateStatus.Fallida && v.Result.Contains("siguen en la 15.0.10"), "registro nuevo, archivos viejos: error: " + v.Result);
            v = FilaWinget("Mullvad Browser", "x", "15.0.10", "15.0.24");
            VendorProvider.ConfirmVerdict(v, "15.0.10", "15.0.10", "15.0.24", "15.0.10", "su archivo «version.json»", false, null, false);
            Check(v.Status == UpdateStatus.Fallida && v.Result.Contains("no cambió nada"), "nada cambió: error: " + v.Result);
            v = FilaWinget("TeraBox", "x", "1.47.0", "1.49.5");
            VendorProvider.ConfirmVerdict(v, "1.47.0", "1.47.0", "1.49.5", "1.49.5.183", "su ejecutable (TeraBox.exe)", false, null, false);
            Check(v.Status == UpdateStatus.Instalada && v.Result.Contains("Windows sigue registrando la 1.47.0"), "registro atrasado, archivos al día: correcta: " + v.Result);
            v = FilaWinget("Mullvad Browser", "x", "15.0.20", "15.0.24");     // la de antes salía de version.json (registro atrasado en 15.0.10)
            VendorProvider.ConfirmVerdict(v, "15.0.20", "15.0.10", "15.0.24", "15.0.20", "su archivo «version.json»", false, null, false);
            Check(v.Status == UpdateStatus.Fallida && v.Result.Contains("sigue instalada la 15.0.20"), "nada cambió contando version.json: " + v.Result);
            v = FilaWinget("Ejemplo", "x", "2.0", "2.4");
            VendorProvider.ConfirmVerdict(v, "2.0", "2.2", "2.4", "", "", false, null, false);
            Check(v.Status == UpdateStatus.Instalada && v.Result.Contains("el registro indica la 2.2"), "cambió a una intermedia: como antes");
        }

        static void TestFabricanteDelEquipo168()
        {
            Eq(OemVendor.Dell, OemToolsProvider.Classify("Dell Inc."), "Dell Inc.");
            Eq(OemVendor.Dell, OemToolsProvider.Classify("Alienware"), "Alienware es Dell");
            Eq(OemVendor.Lenovo, OemToolsProvider.Classify("LENOVO"), "LENOVO");
            Eq(OemVendor.HP, OemToolsProvider.Classify("HP"), "HP");
            Eq(OemVendor.HP, OemToolsProvider.Classify("Hewlett-Packard"), "Hewlett-Packard");
            Eq(OemVendor.Ninguno, OemToolsProvider.Classify("HPE-like Clone S.A."), "un nombre que sólo empieza con «HP» pegado no es HP");
            Eq(OemVendor.Ninguno, OemToolsProvider.Classify("System manufacturer"), "equipo armado: ninguno");
            Eq(OemVendor.Ninguno, OemToolsProvider.Classify(null), "sin dato: ninguno");

            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><updates>" +
                "<update><name>Dell Latitude 5420 System BIOS</name><version>1.38.0</version><date>2026-09-30</date><urgency>Urgent</urgency><type>BIOS</type><category>BIOS</category><file>Latitude_5420_1.38.0.exe</file><size>34567890</size></update>" +
                "<update><name>Intel UHD Graphics Driver</name><version>31.0.101.5592</version><date>2026-08-01</date><urgency>Recommended</urgency><type>Driver</type><category>Video</category></update>" +
                "<update><name>Realtek USB Audio &amp; Mic</name><version>6.3.9600.2355</version><urgency>Optional</urgency><type>Driver</type></update>" +
                "<update><name></name><version>x</version></update></updates>";
            var ups = OemToolsProvider.ParseDcuReport(xml);
            Eq(3, ups.Count, "tres actualizaciones (la sin nombre se descarta)");
            Eq("Dell Latitude 5420 System BIOS", ups[0].Name, "nombre");
            Eq("1.38.0", ups[0].Version, "versión");
            Eq("BIOS", ups[0].Type, "tipo");
            Eq("Realtek USB Audio & Mic", ups[2].Name, "entidades XML decodificadas");
            Eq(0, OemToolsProvider.ParseDcuReport("no es xml").Count, "un informe roto: nada");

            // La búsqueda simulada: Dell con Dell Command | Update, como administrador.
            var prov = new OemToolsProvider();
            prov.ManufacturerForTests = delegate { return "Dell Inc."; };
            prov.ToolForTests = delegate(string v) { return v == "Dell" ? @"C:\Program Files\Dell\CommandUpdate\dcu-cli.exe" : null; };
            var comandos = new List<string>();
            int codigoScan = 0, codigoApply = 1;
            prov.RunForTests = delegate(string file, string args, int ms)
            {
                comandos.Add(args);
                var r = new ProcResult(); r.Started = true;
                if (args.StartsWith("/scan"))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(args, "-report=\"?(?<d>[^\"]+)\"?");
                    string dir = m.Groups["d"].Value;
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "DCUApplicableUpdates.xml"), xml);
                    r.ExitCode = codigoScan;
                }
                else r.ExitCode = codigoApply;
                return r;
            };
            var ctx = new UpdateContext(); ctx.Os = Os(10, 0, 26200, "Professional", false); ctx.IsAdmin = true;
            var salida = new List<UpdateCandidate>();
            prov.Scan(ctx, salida, new List<string>());
            Eq(1, salida.Count, "una fila para Dell Command | Update");
            var fila = salida[0];
            Eq(UpdateStatus.Disponible, fila.Status, "disponible");
            Check(fila.Installable && !fila.Recommended, "se puede instalar, pero NUNCA queda marcada (incluye la BIOS)");
            Eq("3 actualizaciones", fila.AvailableVersion, "cuántas");
            Check(fila.Detail.Contains("1 BIOS") && fila.Detail.Contains("2 controladores") && fila.Detail.Contains("1.38.0 (urgente)") && fila.Detail.Contains("no apagues"), "el detalle: " + fila.Detail);
            Check(comandos[0].StartsWith("/scan -silent -report="), "busca con /scan y el informe en una carpeta privada: " + comandos[0]);
            Eq(UpdateKind.Controlador, fila.Kind, "va con los controladores");

            prov.Install(ctx, new List<UpdateCandidate> { fila }, delegate { });
            Check(comandos[1].StartsWith("/applyUpdates -silent -reboot=disable -outputLog="), "instala sin reiniciar el equipo: " + comandos[1]);
            Eq(UpdateStatus.RequiereReinicio, fila.Status, "código 1: requiere reiniciar");

            var f2 = new UpdateCandidate(); f2.AvailableVersion = "2 actualizaciones";
            OemToolsProvider.ApplyDellResult(f2, 0, "x.log"); Eq(UpdateStatus.Instalada, f2.Status, "código 0: instalada");
            OemToolsProvider.ApplyDellResult(f2, 500, "x.log"); Eq(UpdateStatus.AlDia, f2.Status, "código 500: ya estaba al día");
            OemToolsProvider.ApplyDellResult(f2, 503, "x.log");
            Check(f2.Status == UpdateStatus.Fallida && f2.Result.Contains("descargar") && f2.Result.Contains("x.log"), "código 503: error con el motivo y el registro");
            Check(OemToolsProvider.DcuExitText(12345).Contains("12345"), "un código desconocido se muestra tal cual");

            // Sin actualizaciones (500), sin administrador, sin la herramienta.
            codigoScan = 500;
            salida.Clear(); prov.Scan(ctx, salida, new List<string>());
            Eq(UpdateStatus.AlDia, salida[0].Status, "dcu-cli 500: al día");
            ctx.IsAdmin = false;
            salida.Clear(); prov.Scan(ctx, salida, new List<string>());
            Check(salida[0].Status == UpdateStatus.NoVerificable && salida[0].Detail.Contains("administrador"), "sin administrador: lo explica, no ejecuta nada");
            prov.ToolForTests = delegate { return null; };
            salida.Clear(); prov.Scan(ctx, salida, new List<string>());
            Check(salida[0].Status == UpdateStatus.NoVerificable && salida[0].Detail.Contains("Dell.CommandUpdate") && !salida[0].Installable, "sin la herramienta: cómo conseguirla");

            // Lenovo con System Update: la acción lo abre. HP: la herramienta oficial. Otro fabricante: nada.
            prov.ManufacturerForTests = delegate { return "LENOVO"; };
            prov.ToolForTests = delegate(string v) { return v == "Lenovo" ? @"C:\Program Files (x86)\Lenovo\System Update\tvsu.exe" : null; };
            salida.Clear(); prov.Scan(ctx, salida, new List<string>());
            Check(salida[0].Status == UpdateStatus.Pendiente && salida[0].Installable && UpdateManager.CanInstall(salida[0]), "Lenovo System Update: acción para abrirlo");
            prov.Install(ctx, new List<UpdateCandidate> { salida[0] }, delegate { });
            Check(salida[0].Status == UpdateStatus.Pendiente && salida[0].Result.Contains("Se abrió Lenovo System Update"), "se abrió; queda pendiente de lo que confirme el usuario");
            prov.ManufacturerForTests = delegate { return "HP"; };
            salida.Clear(); prov.Scan(ctx, salida, new List<string>());
            Check(salida[0].Status == UpdateStatus.NoVerificable && salida[0].Detail.Contains("HP Support Assistant") && Fmt.WebUrl(salida[0].InfoUrl) != null, "HP: la herramienta oficial y su sitio");
            prov.ManufacturerForTests = delegate { return "Gigabyte Technology Co., Ltd."; };
            salida.Clear(); prov.Scan(ctx, salida, new List<string>());
            Eq(0, salida.Count, "otro fabricante: ninguna fila");
        }

        static void TestVersionNueva168()
        {
            string v;
            Eq(SelfUpdate.SourceKind.GitHub, SelfUpdate.Classify("fede/superprogram", out v), "«propietario/repo»: GitHub");
            Eq("fede/superprogram", v, "el repositorio");
            Eq(SelfUpdate.SourceKind.GitHub, SelfUpdate.Classify("https://github.com/fede/superprogram/releases", out v), "dirección de GitHub");
            Eq("fede/superprogram", v, "el repositorio de la dirección");
            Eq(SelfUpdate.SourceKind.Folder, SelfUpdate.Classify(@"\\servidor\Actualizador", out v), "carpeta de red");
            Eq(SelfUpdate.SourceKind.Folder, SelfUpdate.Classify(@"D:\Entregas", out v), "carpeta local");
            Eq(SelfUpdate.SourceKind.None, SelfUpdate.Classify("", out v), "vacía: no se consulta");
            Eq(SelfUpdate.SourceKind.None, SelfUpdate.Classify("https://example.com/sp", out v), "otro sitio: no");
            Eq(SelfUpdate.SourceKind.None, SelfUpdate.Classify("entregas", out v), "una ruta relativa: no");
            string nombre;
            Eq("1.6.10", SelfUpdate.NewestDelivery(new[] { "Actualizador-1.6.8.7z", @"X:\e\Actualizador-1.6.10.zip", "Actualizador-1.6.9", "Actualizador-1.7.0-beta.7z", "Otro-2.0.7z", "ENTREGAS.txt" }, out nombre),
               "la entrega más nueva (1.6.10 > 1.6.9, numérico)");
            Eq(@"X:\e\Actualizador-1.6.10.zip", nombre, "su archivo");
            Check(SelfUpdate.NewestDelivery(new[] { "nada.7z" }, out nombre) == null, "sin entregas: null");
            var hoy = new DateTime(2026, 10, 18, 10, 0, 0);
            // 2.0.32: una vez por hora (antes cada 20 horas), para que un arreglo publicado llegue el mismo día.
            Check(SelfUpdate.ShouldCheck(null, hoy) && SelfUpdate.ShouldCheck(hoy.AddHours(-2), hoy) && !SelfUpdate.ShouldCheck(hoy.AddMinutes(-20), hoy), "como mucho una vez por hora");

            string dir = Path.Combine(Path.GetTempPath(), "sp168-ver-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "Actualizador-1.6.7.7z"), "x");
                Directory.CreateDirectory(Path.Combine(dir, "Actualizador-1.7.1"));
                var r = SelfUpdate.Check(dir, "1.6.8");
                Check(r.Error == null && r.Newer && r.Latest == "1.7.1" && r.Where.EndsWith("Actualizador-1.7.1"), "carpeta con una entrega más nueva");
                Check(SelfUpdate.Message(r).Contains("1.7.1") && SelfUpdate.Message(r).Contains("Instalar.cmd"), "mensaje con la versión y cómo instalarla");
                var igual = SelfUpdate.Check(dir, "1.7.1");
                Check(!igual.Newer && SelfUpdate.Message(igual) == null, "la misma versión: nada que avisar");
                var noHay = SelfUpdate.Check(Path.Combine(dir, "no-existe"), "1.6.8");
                Check(noHay.Error != null && !noHay.Newer, "carpeta que no existe: error, sin avisar");
                string f = Path.Combine(dir, "estado.json");
                SelfUpdate.Save(f, r, dir);
                var leido = SelfUpdate.Load(f, dir, "1.6.8");
                Check(leido != null && leido.Newer && leido.Latest == "1.7.1", "se guarda y se lee la última consulta");
                Check(SelfUpdate.Load(f, "otra/fuente", "1.6.8") == null, "si cambió la fuente, se vuelve a consultar");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
        static InstalledProgram Prog(string name, string version, string publisher, string uninstall)
        {
            var p = new InstalledProgram();
            p.Name = name; p.Version = version; p.Publisher = publisher ?? ""; p.UninstallString = uninstall ?? "";
            p.Scope = "Equipo"; p.Architecture = "x64"; p.InstallLocation = ""; p.InfoUrl = ""; p.UpdateUrl = ""; p.HelpLink = "";
            return p;
        }

        static string IdOf(VendorCheck c) { return c == null ? null : c.Id; }

        const string OfficeUninstall = "\"C:\\Program Files\\Common Files\\Microsoft Shared\\ClickToRun\\OfficeClickToRun.exe\" scenario=install scenariosubtype=ARP sourcetype=None productstoremove=ProPlus2024Retail.16_es-mx_x-none culture=es-mx version.16=16.0";

        static void TestCatalogoFabricantes()
        {
            // --- Integridad del catálogo: cada fuente es HTTPS y de su propio dominio.
            var ids = new HashSet<string>();
            foreach (var c in VendorCatalog.BuiltIn)
            {
                Check(ids.Add(c.Id), "identificador único: " + c.Id);
                Check(c.Name != null, c.Id + ": expresión de nombre");
                Check(!string.IsNullOrEmpty(c.Product) && !string.IsNullOrEmpty(c.Vendor), c.Id + ": producto y origen");
                if (c.Feed != VendorFeed.FinalVersion)
                {
                    Uri u;
                    Check(Uri.TryCreate(c.Url, UriKind.Absolute, out u) && c.Allows(u), c.Id + ": la fuente es HTTPS y de " + c.Domain);
                }
                else Check(VersionUtil.Parse(c.FinalVersion) != null && !string.IsNullOrEmpty(c.Note), c.Id + ": versión final y aclaración");
                Check(c.InfoUrl == null || Fmt.WebUrl(c.InfoUrl) != null, c.Id + ": sitio válido");
                if (c.Install == VendorInstall.SignedInstaller || c.Install == VendorInstall.AdobeMsp)
                    Check(c.Signers != null && c.Signers.Length > 0, c.Id + ": firmante esperado");
                Check(InstallerRules.IsSafeArgs(c.SilentArgs) && InstallerRules.IsSafeArgs(c.MachineArgs), c.Id + ": interruptores seguros");
                Check(!string.IsNullOrEmpty(c.HowToUpdate), c.Id + ": cómo actualizarlo a mano");
            }

            // --- Los programas de la captura del usuario.
            Eq("vivaldi", IdOf(VendorCatalog.Match(Prog("Vivaldi", "8.2.4133.76", "Vivaldi Technologies AS.", ""))), "Vivaldi");
            Eq("acrobat", IdOf(VendorCatalog.Match(Prog("Adobe Acrobat (64-bit)", "26.002.21931", "Adobe", "MsiExec.exe /I{AC76BA86-1033-1033-7760-BC15014EA700}"))), "Adobe Acrobat (64-bit), vía continua");
            Eq(null, IdOf(VendorCatalog.Match(Prog("Adobe Acrobat (64-bit)", "20.005.30574", "Adobe", ""))), "Acrobat de la vía clásica: no se toca");
            Eq("acrobat-reader", IdOf(VendorCatalog.Match(Prog("Adobe Acrobat Reader DC - Español", "25.001.20458", "Adobe", ""))), "Reader con idioma");
            Eq("samsung-magician", IdOf(VendorCatalog.Match(Prog("Samsung Magician", "9.0.1.950", "Samsung Electronics", ""))), "Samsung Magician");
            Eq(null, IdOf(VendorCatalog.Match(Prog("Samsung Data Migration", "4.0", "Samsung Electronics", ""))), "otro programa de Samsung no coincide");
            Eq("daemon-tools-lite", IdOf(VendorCatalog.Match(Prog("DAEMON Tools Lite", "12.6.0.2451", "Disc Soft Ltd", ""))), "DAEMON Tools Lite");
            Eq(null, IdOf(VendorCatalog.Match(Prog("DAEMON Tools Ultra", "6.2", "Disc Soft Ltd", ""))), "DAEMON Tools Ultra no es Lite");
            Eq("office", IdOf(VendorCatalog.Match(Prog("Microsoft Office Profesional Plus 2024 - es-mx", "16.0.20326.20158", "Microsoft Corporation", OfficeUninstall))), "Office «Hacer clic y ejecutar»");
            Eq(null, IdOf(VendorCatalog.Match(Prog("Microsoft Office Professional Plus 2016", "16.0.4266.1001", "Microsoft Corporation", "MsiExec.exe /X{90160000-0011-0000-0000-0000000FF1CE}"))), "Office MSI: no es C2R");
            Eq("vcredist2005-x64", IdOf(VendorCatalog.Match(Prog("Microsoft Visual C++ 2005 Redistributable (x64)", "8.0.61000", "Microsoft Corporation", "MsiExec.exe /X{ad8a2fa1-06e7-4b0d-927d-6e54b3d31028}"))), "VC++ 2005 x64");
            Eq("vcredist2005-x86", IdOf(VendorCatalog.Match(Prog("Microsoft Visual C++ 2005 Redistributable", "8.0.61001", "Microsoft Corporation", ""))), "VC++ 2005 x86");
            Eq("vcredist2010", IdOf(VendorCatalog.Match(Prog("Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219", "10.0.40219", "Microsoft Corporation", ""))), "VC++ 2010 (con dos espacios, como lo registra Microsoft)");
            Eq("vcredist2013", IdOf(VendorCatalog.Match(Prog("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.40664", "12.0.40664.0", "Microsoft Corporation", ""))), "VC++ 2013");
            Eq(null, IdOf(VendorCatalog.Match(Prog("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "14.40.33810.0", "Microsoft Corporation", ""))), "VC++ 2015-2022 sigue vivo: lo compara winget");
            Eq("image-composite-editor", IdOf(VendorCatalog.Match(Prog("Image Composite Editor", "2.0.3", "Microsoft Corporation", ""))), "Image Composite Editor");
            foreach (string otro in new[] { "µTorrent", "Avast One", "Counter-Strike 2", "Vivaldi Mail" })
                Eq(null, IdOf(VendorCatalog.Match(Prog(otro, "1.0", "", ""))), otro + ": fuera del catálogo");

            // --- Dominios: la descarga sólo del fabricante, por HTTPS, sin imitaciones.
            var viv = VendorCatalog.Find("vivaldi");
            Check(viv.Allows(new Uri("https://downloads.vivaldi.com/stable-auto/Vivaldi.8.2.4133.76.x64.exe")), "downloads.vivaldi.com permitido");
            Check(!viv.Allows(new Uri("https://vivaldi.com.evil.example/x.exe")), "imitación de vivaldi.com rechazada");
            Check(!viv.Allows(new Uri("https://evilvivaldi.com/x.exe")), "sufijo sin punto rechazado");
            Check(!viv.Allows(new Uri("http://downloads.vivaldi.com/x.exe")), "HTTP sin cifrar rechazado");
            Check(!viv.Allows(new Uri("https://downloads.vivaldi.com:8443/x.exe")), "puerto no estándar rechazado");
            Check(VendorCatalog.Find("acrobat").Allows(new Uri("https://ardownload2.adobe.com/pub/adobe/acrobat/win/AcrobatDC/2600221931/AcrobatDCx64Upd2600221931.msp")), "ardownload2.adobe.com permitido");
            Check(VendorCatalog.Find("office").Allows(new Uri("https://clients.config.office.net/releases/v1.0/OfficeReleases")), "config.office.net permitido");
            Check(!VendorCatalog.Find("office").Allows(new Uri("https://clients.config.office.net.evil.example/")), "imitación de office.net rechazada");
        }

        static void TestFuentesFabricantes()
        {
            // --- Appcast de Vivaldi: el instalador completo, nunca el paquete incremental.
            var item = VendorCatalog.ParseAppcast(Sample("vivaldi_appcast_x64.xml"));
            Check(item != null, "appcast leído");
            if (item != null)
            {
                Eq("3.6.2165.34", item.Version, "appcast: versión");
                Eq("https://downloads.vivaldi.com/stable-auto/Vivaldi.3.6.2165.34.x64.exe", item.Url, "appcast: instalador completo (no el .delta.7z.cab)");
            }
            string dos = "<rss><channel><item><enclosure url=\"https://x.vivaldi.com/a.exe\" sparkle:version=\"8.1.1\"/></item>" +
                         "<item><enclosure sparkle:shortVersionString=\"8.2.4133.76\" url='https://x.vivaldi.com/b.exe'/></item>" +
                         "<item><enclosure sparkle:version=\"9.0\"/></item>" +
                         "<item><enclosure url=\"https://x.vivaldi.com/c.cab\" sparkle:deltaFrom=\"8.2\" sparkle:version=\"9.9.9\"/></item></channel></rss>";
            var dosItem = VendorCatalog.ParseAppcast(dos);
            Check(dosItem != null && dosItem.Version == "8.2.4133.76" && dosItem.Url == "https://x.vivaldi.com/b.exe",
                  "appcast: la más alta con instalador (sin url y delta se ignoran; shortVersionString como respaldo)");
            Check(VendorCatalog.ParseAppcast("<html>error 503</html>") == null, "appcast inválido: null");
            Check(VendorCatalog.ParseAppcast(null) == null, "appcast vacío: null");

            // --- Samsung Magician: la versión del instalador de Windows, no del de Mac, Android ni la vieja .zip.
            var mag = VendorCatalog.Find("samsung-magician");
            string url;
            Eq("9.0.1.950", VendorCatalog.ParsePage(mag, Sample("samsung_tools.html"), out url), "Samsung: versión del instalador de Windows");
            Eq("https://download.semiconductor.samsung.com/resources/software-resources/Samsung_Magician_Installer_Official_9.0.1.950.exe", url, "Samsung: enlace al .exe oficial");
            Eq("9.1.0.100", VendorCatalog.ParsePage(mag, "<a href=\"/resources/software-resources/Samsung_Magician_Installer_Official_9.1.0.100.exe\">x</a>", out url), "Samsung: enlace relativo");
            Eq("https://semiconductor.samsung.com/resources/software-resources/Samsung_Magician_Installer_Official_9.1.0.100.exe", url, "Samsung: el relativo se completa con el dominio de la página");
            Eq(null, VendorCatalog.ParsePage(mag, "<a href=\"https://evil.example/resources/software-resources/Samsung_Magician_Installer_Official_9.9.9.9.exe\">x</a>", out url), "Samsung: enlace de otro dominio ignorado");

            // --- DAEMON Tools Lite: la versión del encabezado; lo que diga un script no cuenta.
            var dt = VendorCatalog.Find("daemon-tools-lite");
            Eq("12.6.0", VendorCatalog.ParsePage(dt, Sample("daemon_tools_lite.html"), out url), "DAEMON Tools Lite: 12.6.0 (sin el 99.0.0 de un script)");
            Check(url == null, "DAEMON Tools Lite: sin instalador (sólo se compara)");
            Check(VersionUtil.IsNewer("12.6.0.2451", "12.6.0"), "la instalada 12.6.0.2451 queda al día frente a 12.6.0");
            Eq(null, VendorCatalog.ParsePage(dt, "<h2>DAEMON Tools Lite 13.0.0 Beta</h2>", out url), "DAEMON Tools Lite: beta descartada");

            // --- Adobe: versión del texto oficial y parches MSP.
            Eq("26.002.21931", AdobeRules.ParseVersionText("26.002.21931\n"), "Adobe: current_version.txt real (igual a la instalada del usuario)");
            Eq(null, AdobeRules.ParseVersionText("<html>Access denied</html>"), "Adobe: respuesta inesperada → null");
            Eq(null, AdobeRules.ParseVersionText("20.005.30574"), "Adobe: versión de la vía clásica no se acepta en la continua");
            Check(AdobeRules.IsContinuous("26.002.21931") && !AdobeRules.IsContinuous("24.001.30187") && !AdobeRules.IsContinuous("11.0.23"), "vía continua por el número");
            var msp = AdobeRules.MspUrls(false, "x64", "26.002.21931");
            Check(msp.Count == 3 && msp[0] == "https://ardownload2.adobe.com/pub/adobe/acrobat/win/AcrobatDC/2600221931/AcrobatDCx64Upd2600221931.msp",
                  "Acrobat 64 bits: primero AcrobatDCx64Upd, después los de Reader (instalador unificado)");
            foreach (string m in msp) Check(VendorCatalog.Find("acrobat").Allows(new Uri(m)), "parche en un dominio de Adobe: " + Path.GetFileName(m));
            var mspR86 = AdobeRules.MspUrls(true, "x86", "26.002.21931");
            Check(mspR86.Count == 2 && mspR86[0] == "https://ardownload2.adobe.com/pub/adobe/reader/win/AcrobatDC/2600221931/AcroRdrDCUpd2600221931.msp", "Reader 32 bits");
            Eq(0, AdobeRules.MspUrls(false, "arm64", "26.002.21931").Count, "ARM: se actualiza desde Acrobat");
            Eq(0, AdobeRules.MspUrls(false, "x64", "26.002.21931/../x").Count, "versión inválida: sin direcciones");

            // --- Office: canal por el registro y versión del JSON oficial.
            var channels = OfficeC2R.ParseReleases(Sample("office_releases.json"));
            Eq(7, channels.Count, "Office: canales del JSON oficial (los 7 de la muestra)");
            var cfg = new OfficeConfig();
            cfg.Found = true;
            cfg.VersionToReport = "16.0.20326.20158";
            cfg.CdnBaseUrl = "http://officecdn.microsoft.com/pr/492350f6-3a01-4f97-b9c0-c7c6ddf67d60";
            var ch = OfficeC2R.ResolveChannel(channels, cfg);
            Check(ch != null && ch.Id == "Current", "Office del usuario: Canal actual por CDNBaseUrl");
            if (ch != null)
            {
                Eq("16.0.20430.20092", ch.Latest, "Office: última del Canal actual");
                Check(VersionUtil.IsNewer(ch.Latest, cfg.VersionToReport), "Office del usuario (16.0.20326.20158): tiene actualización");
                Eq("Canal actual", OfficeC2R.ChannelTitle(ch), "Office: nombre del canal en castellano");
            }
            cfg.UpdateChannel = "http://officecdn.microsoft.com/pr/7FFBC6BF-BC32-4F92-8982-F9DD17FD3114";
            Eq("SemiAnnual", OfficeC2R.ResolveChannel(channels, cfg).Id, "UpdateChannel manda sobre CDNBaseUrl (GUID en mayúsculas)");
            cfg.PolicyBranch = "MonthlyEnterprise";
            Eq("MonthlyEnterprise", OfficeC2R.ResolveChannel(channels, cfg).Id, "la directiva manda sobre todo");
            cfg.PolicyBranch = "Broad";
            Eq("SemiAnnual", OfficeC2R.ResolveChannel(channels, cfg).Id, "nombre alternativo del canal");
            var ltsc = new OfficeConfig();
            ltsc.CdnBaseUrl = "http://officecdn.microsoft.com/pr/7983bac0-e531-40cf-be00-fd24fe66619c";
            Eq("16.0.17932.21000", OfficeC2R.ResolveChannel(channels, ltsc).Latest, "Office LTSC 2024");
            var raro = new OfficeConfig();
            raro.CdnBaseUrl = "http://officecdn.microsoft.com/pr/00000000-0000-0000-0000-000000000000";
            Check(OfficeC2R.ResolveChannel(channels, raro) == null, "canal desconocido: null");
            Check(OfficeC2R.IsClickToRun(Prog("x", "1", "", OfficeUninstall)) && !OfficeC2R.IsClickToRun(Prog("x", "1", "", "MsiExec.exe /X{1}")), "Office C2R por su desinstalador");
            var officePkg = new OfficeConfig();
            officePkg.OfficePackageVersion = "16.0.20430.20092";
            officePkg.VersionToReport = "16.0.20500.10000";
            // 1.6.3: la más nueva de las dos. El inventario de las instalaciones viejas (Office 2021) queda sin actualizar.
            Eq("16.0.20500.10000", OfficeC2R.InstalledVersion(officePkg), "Office: la más nueva entre OfficePackageVersion y VersionToReport");
            var officeQuiet = Prog("Microsoft Office Professional Plus 2024 - es-es", "16.0.20300.1", "Microsoft Corporation", "");
            officeQuiet.QuietUninstallCommand = OfficeUninstall;
            Check(OfficeC2R.IsClickToRun(officeQuiet), "Office C2R: QuietUninstallCommand también identifica Click-to-Run");
            var office365 = Prog("Microsoft 365 Apps for enterprise - en-us", "16.0.20300.1", "Microsoft Corporation", OfficeUninstall);
            Check(VendorCatalog.Find("office") != null && VendorCatalog.Find("office").Name.IsMatch(office365.Name), "Office: también detecta Microsoft 365 y no sólo Office 2021");
            bool fallo = false;
            try { OfficeC2R.ParseReleases("{\"Error\":{\"Code\":\"Unknown\"}}"); } catch (InvalidDataException) { fallo = true; }
            Check(fallo, "Office: respuesta de error del servicio → error explicado");

            // --- Versiones finales de Microsoft.
            Eq(UpdateStatus.AlDia, VendorCatalog.EvaluateFinal("8.0.61000", "8.0.61000"), "VC++ 2005 x64 del usuario: al día (es la última)");
            Eq(UpdateStatus.Disponible, VendorCatalog.EvaluateFinal("8.0.50727.42", "8.0.61000"), "VC++ 2005 original: hay una más nueva");
            Eq(UpdateStatus.AlDia, VendorCatalog.EvaluateFinal("12.0.40664.0", "12.0.40664"), "VC++ 2013 con .0 final");
            Check(VendorCatalog.EvaluateFinal("", "8.0.61000") == null, "versión ilegible: sin fila del fabricante");

            // --- El proveedor completo, sin red: sólo programas con versión final conocida.
            var ctx = new UpdateContext();
            ctx.Os = new OsInfo();
            ctx.Programs.Add(Prog("Microsoft Visual C++ 2005 Redistributable (x64)", "8.0.61000", "Microsoft Corporation", "MsiExec.exe /X{ad8a2fa1-06e7-4b0d-927d-6e54b3d31028}"));
            ctx.Programs.Add(Prog("Microsoft Visual C++ 2005 Redistributable", "8.0.56336", "Microsoft Corporation", "MsiExec.exe /X{7299052b-02a4-4627-81f2-1818da5d550d}"));
            ctx.Programs.Add(Prog("Microsoft Visual C++ 2008 Redistributable - x64 9.0.30729.4148", "9.0.30729.4148", "Microsoft Corporation", ""));
            ctx.Programs.Add(Prog("Microsoft Visual C++ 2008 Redistributable - x64 9.0.30729.6161", "9.0.30729.6161", "Microsoft Corporation", ""));
            ctx.Programs.Add(Prog("Image Composite Editor", "2.0.3", "Microsoft Corporation", ""));
            ctx.Programs.Add(Prog("Digital Clock 1.0", "1.0", "FlashTECH", ""));
            var output = new List<UpdateCandidate>();
            var notes = new List<string>();
            new VendorProvider().Scan(ctx, output, notes);
            Eq(5, output.Count, "cinco filas del fabricante (el reloj no está en el catálogo ni tiene proyecto en GitHub)");
            Eq(0, notes.Count, "sin consultas de red ni errores");
            foreach (var c in output)
            {
                string n = c.Name;
                Check(c.ProviderName == VendorProvider.ProviderId && c.ProviderTag is VendorPlan, c.Name + ": fila del fabricante");
                // Image Composite Editor ya no tiene página: Microsoft la retiró junto con el programa.
                Check(n.StartsWith("Image Composite") ? c.InfoUrl == null : Fmt.WebUrl(c.InfoUrl) != null, c.Name + ": sitio oficial si existe");
                if (n.EndsWith("(x64)")) Check(c.Status == UpdateStatus.AlDia && c.AvailableVersion == "8.0.61000", "VC++ 2005 x64 del usuario: al día");
                else if (n.EndsWith("2005 Redistributable"))
                    Check(c.Status == UpdateStatus.Disponible && !c.Installable && !c.Recommended && c.Detail.Contains("Microsoft.VCRedist.2005.x86"), "VC++ 2005 x86 viejo: disponible a mano, con el comando de winget");
                else if (n.EndsWith("4148")) Check(c.Status == UpdateStatus.AlDia && c.Detail.StartsWith("Convive"), "VC++ 2008 .4148 convive con la .6161");
                else if (n.EndsWith("6161")) Check(c.Status == UpdateStatus.AlDia, "VC++ 2008 .6161 al día");
                else Check(c.Status == UpdateStatus.AlDia && c.Detail.Contains("discontinuó"), "Image Composite Editor: discontinuado");
            }
        }

        static void TestGitHubEInstaladores()
        {
            // --- Repositorio registrado por el programa.
            Eq("ejemplo/editor", GitHubRules.RepoOf("https://github.com/ejemplo/editor"), "repositorio");
            Eq("ejemplo/editor", GitHubRules.RepoOf("https://github.com/ejemplo/editor.git"), "repositorio .git");
            Eq("ejemplo/editor", GitHubRules.RepoOf("https://www.github.com/ejemplo/editor/releases"), "página de versiones");
            Eq(null, GitHubRules.RepoOf("https://github.com/sponsors/ejemplo"), "página reservada de GitHub");
            Eq(null, GitHubRules.RepoOf("https://github.com/ejemplo"), "usuario sin repositorio");
            Eq(null, GitHubRules.RepoOf("https://gist.github.com/ejemplo/abc"), "gist no es un proyecto");
            Eq(null, GitHubRules.RepoOf("https://github.com.evil.example/a/b"), "imitación de github.com");
            Eq(null, GitHubRules.RepoOf("javascript:alert('https://github.com/a/b')"), "javascript: rechazado");
            var p = Prog("Editor", "2.3.0", "Ejemplo", "");
            p.InfoUrl = "https://ejemplo.dev"; p.UpdateUrl = "https://github.com/ejemplo/editor/releases"; p.HelpLink = "https://github.com/otro/cosa";
            Eq("ejemplo/editor", GitHubRules.RepoOf(p), "primero el de actualizaciones (URLUpdateInfo)");
            Check(GitHubRules.IsApi(new Uri(GitHubRules.LatestReleaseUrl("ejemplo/editor"))), "consulta a api.github.com");

            // --- Última versión y elección del instalador.
            var rel = GitHubRules.ParseRelease(Sample("github_release.json"));
            Check(rel != null, "versión de GitHub leída");
            if (rel != null)
            {
                Eq("2.4.1", rel.Version, "número desde la etiqueta v2.4.1");
                Eq(5, rel.Assets.Count, "archivos de GitHub (el de otro servidor se descarta)");
                var x64 = GitHubRules.PickAsset(rel, "x64", false);
                Check(x64 != null && x64.Name == "Editor-2.4.1-setup-x64.exe", "x64: el instalador .exe, no el portable ni la firma");
                Check(x64 != null && x64.Sha256 == "abcdefabcdef0123456789abcdef0123456789abcdef0123456789abcdef0123", "SHA-256 publicado por GitHub (en minúsculas)");
                var msi = GitHubRules.PickAsset(rel, "x64", true);
                Check(msi != null && msi.Name == "Editor-2.4.1-x64.msi" && msi.Sha256 == "", "instalado con MSI: el .msi");
                var arm = GitHubRules.PickAsset(rel, "arm64", false);
                Check(arm != null && arm.Name == "Editor-2.4.1-setup-arm64.exe", "ARM64: el de ARM64");
                Check(GitHubRules.PickAsset(rel, "x86", false) == null, "x86: no hay instalador de 32 bits ni neutro");
            }
            Check(GitHubRules.ParseRelease("{\"tag_name\":\"v3.0.0\",\"draft\":true}") == null, "borrador descartado");
            Check(GitHubRules.ParseRelease("{\"tag_name\":\"v3.0.0\",\"prerelease\":true}") == null, "preliminar descartada");
            Check(GitHubRules.ParseRelease("{\"tag_name\":\"v3.0.0-beta.1\"}") == null, "etiqueta beta descartada");
            Check(GitHubRules.ParseRelease("{\"tag_name\":\"nightly\"}") == null, "etiqueta sin número descartada");
            Check(GitHubRules.IsDownload(new Uri("https://objects.githubusercontent.com/x")) && GitHubRules.IsDownload(new Uri("https://release-assets.githubusercontent.com/x")),
                  "almacenamiento de GitHub permitido");
            Check(!GitHubRules.IsDownload(new Uri("https://githubusercontent.com/x")) && !GitHubRules.IsDownload(new Uri("http://github.com/x")), "sin subdominio o sin HTTPS: rechazado");

            // --- Tipo de instalador por su contenido.
            Eq(InstallerKind.Msi, InstallerRules.Detect(Stream(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0 })), "MSI/MSP por el contenedor OLE");
            Eq(InstallerKind.Inno, InstallerRules.Detect(Pe("rDlPtS\u00CD\u00E6\u00D7{")), "Inno Setup por la tabla del cargador");
            Eq(InstallerKind.Inno, InstallerRules.Detect(Stream(Concat(Encoding.ASCII.GetBytes("MZ...."), Encoding.Unicode.GetBytes("This installation was built with Inno Setup.")))), "Inno Setup por la información de versión (UTF-16)");
            Eq(InstallerKind.Nsis, InstallerRules.Detect(Pe("\u00EF\u00BE\u00AD\u00DENullsoftInst")), "NSIS por su firma");
            Eq(InstallerKind.Unknown, InstallerRules.Detect(Pe("un ejecutable cualquiera")), "ejecutable sin marcas: desconocido (no se ejecuta desde GitHub)");
            Eq(InstallerKind.Unknown, InstallerRules.Detect(Stream(Encoding.ASCII.GetBytes("<html>404</html>"))), "página de error: desconocido");
            Eq("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", InstallerRules.SilentArgs(InstallerKind.Inno), "Inno en silencio");
            Eq("/S", InstallerRules.SilentArgs(InstallerKind.Nsis), "NSIS en silencio");
            Check(InstallerRules.SilentArgs(InstallerKind.Unknown) == null, "desconocido: sin interruptores inventados");

            // --- Firmante esperado.
            Check(InstallerRules.SignerAccepted("CN=Vivaldi Technologies AS, O=Vivaldi Technologies AS, L=Oslo, C=NO", new[] { "Vivaldi Technologies AS" }), "firma de Vivaldi");
            Check(!InstallerRules.SignerAccepted("CN=Vivaldi Technologies AS Evil, O=Vivaldi Technologies AS Evil, C=NO", new[] { "Vivaldi Technologies AS" }), "parecido no alcanza");
            Check(InstallerRules.SignerAccepted("CN=\"Samsung Electronics Co., Ltd.\", O=\"Samsung Electronics Co., Ltd.\", L=Suwon-si, C=KR", new[] { "Samsung Electronics Co., Ltd." }), "firma de Samsung (con comas entre comillas)");
            Check(InstallerRules.SignerAccepted("CN=Adobe Inc., O=Adobe Inc., L=San Jose, S=ca, C=US", new[] { "Adobe Inc.", "Adobe Systems Incorporated" }), "firma de Adobe");
            Check(InstallerRules.SignerAccepted("CN=Adobe Systems Incorporated, O=Adobe Systems Incorporated", new[] { "Adobe Inc.", "Adobe Systems Incorporated" }), "firma de Adobe (nombre anterior)");
            Check(!InstallerRules.SignerAccepted("CN=Microsoft Corporation, O=Microsoft Corporation", new[] { "Adobe Inc." }), "otro fabricante rechazado");
            Check(!InstallerRules.SignerAccepted("", new[] { "Adobe Inc." }) && !InstallerRules.SignerAccepted("O=Adobe Inc.", new string[0]), "sin datos: rechazado");
            Check(InstallerRules.IsSafeArgs("--vivaldi-silent --do-not-launch-chrome --system-level") && !InstallerRules.IsSafeArgs("/S & calc") && !InstallerRules.IsSafeArgs("\"/D=C:\\x\""),
                  "interruptores: sólo ASCII simple");
            Eq("Editor-2.4.1-setup-x64.exe", VendorProvider.FileNameFor("https://github.com/ejemplo/editor/releases/download/v2.4.1/Editor-2.4.1-setup-x64.exe", "instalador.exe"), "nombre original del instalador");
            Eq("instalador.exe", VendorProvider.FileNameFor("https://x.com/download?id=3", "instalador.exe"), "sin nombre utilizable: genérico");
            Check(InstallerRules.Downgrade("8.1.4000.10", "8.2.4133.76") != null, "degradación: instalador más viejo que lo instalado se rechaza");
            Check(InstallerRules.Downgrade("8.2.4133.76", "8.2.4133.76") == null && InstallerRules.Downgrade("8.3.1", "8.2.4133.76") == null, "misma versión o más nueva: se acepta");
            Check(InstallerRules.Downgrade("", "8.2") == null && InstallerRules.Downgrade("8.1", "Desconocida") == null, "versión desconocida: no se decide por eso");
            Eq("instalador.exe", VendorProvider.FileNameFor("https://x.com/..%5C..%5Cevil.exe", "instalador.exe"), "nombre con barras: genérico");
        }

        static MemoryStream Stream(byte[] b) { return new MemoryStream(b); }
        static MemoryStream Pe(string marca) { return Stream(Concat(Encoding.ASCII.GetBytes("MZ\u0090\0"), new byte[20000], Encoding.GetEncoding(28591).GetBytes(marca))); }

        static byte[] Concat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        static void TestPistasYDeduplicacion()
        {
            // --- Office: una fila del fabricante reemplaza las dos entradas de winget (una recortada con «…»).
            var all = new List<UpdateCandidate>();
            var es = Cand("Microsoft Office Profesional Plus 2024 - es-mx", "winget", UpdateStatus.NoVerificable); all.Add(es);
            var en = Cand("Microsoft Office Professional Plus 2024 - en", "winget", UpdateStatus.NoVerificable);
            var row = new WingetRow(); row.Name = "Microsoft Office Professional Plus 2024 - en…"; en.ProviderTag = row; all.Add(en);
            var zip = Cand("7-Zip 25.01 (x64)", "winget", UpdateStatus.AlDia); all.Add(zip);
            var office = Cand("Microsoft Office Profesional Plus 2024 - es-mx", VendorProvider.ProviderId, UpdateStatus.Disponible);
            var plan = new VendorPlan();
            plan.Check = VendorCatalog.Find("office");
            plan.CoveredNames.Add("Microsoft Office Profesional Plus 2024 - es-mx");
            plan.CoveredNames.Add("Microsoft Office Professional Plus 2024 - en-us");
            office.ProviderTag = plan;
            all.Add(office);
            UpdateManager.DeduplicateVendor(all);
            Check(all.Count == 2 && all.Contains(office) && all.Contains(zip), "Office: queda una sola fila (la del fabricante) y 7-Zip");

            // Entradas hermanas: la fila del fabricante de la .4148 no se lleva la de winget de la .6161.
            var sx = new List<UpdateCandidate>();
            var w6161 = Cand("Microsoft Visual C++ 2008 Redistributable - x64 9.0.30729.6161", "winget", UpdateStatus.AlDia); sx.Add(w6161);
            var v4148 = Cand("Microsoft Visual C++ 2008 Redistributable - x64 9.0.30729.4148", VendorProvider.ProviderId, UpdateStatus.AlDia);
            var p4148 = new VendorPlan(); p4148.Check = VendorCatalog.Find("vcredist2008"); p4148.CoveredNames.Add(v4148.Name); v4148.ProviderTag = p4148; sx.Add(v4148);
            UpdateManager.DeduplicateVendor(sx);
            Check(sx.Count == 2, "la fila de winget de otra entrada hermana no desaparece");

            var err = new List<UpdateCandidate> { Cand("Vivaldi", "winget", UpdateStatus.NoVerificable), Cand("Vivaldi", VendorProvider.ProviderId, UpdateStatus.Error) };
            UpdateManager.DeduplicateVendor(err);
            Check(err.Count == 1 && err[0].ProviderName == "winget", "si falla la consulta a Vivaldi, queda la fila de winget");

            var gh = new List<UpdateCandidate>();
            var wgInst = Cand("Editor", "winget", UpdateStatus.Disponible); wgInst.Installable = true; gh.Add(wgInst);
            var ghRow = Cand("Editor", VendorProvider.ProviderId, UpdateStatus.Disponible);
            var ghPlan = new VendorPlan(); ghPlan.Repo = "ejemplo/editor"; ghPlan.CoveredNames.Add("Editor"); ghRow.ProviderTag = ghPlan; gh.Add(ghRow);
            UpdateManager.DeduplicateVendor(gh);
            Check(gh.Count == 1 && gh[0] == wgInst, "si winget ya la instala (con el hash de su catálogo), se prefiere winget");

            // --- Sitio que registra el programa.
            Eq("https://vivaldi.com/es/download/", Fmt.WebUrl("https://vivaldi.com/es/download/"), "URL normal");
            Eq("http://example.com/", Fmt.WebUrl("HTTP://Example.com"), "http normalizado");
            Eq("https://www.acdsee.com/", Fmt.WebUrl("www.acdsee.com"), "«www.» sin esquema, como lo registran muchos instaladores");
            foreach (string mala in new[] { "javascript:alert(1)", "file:///C:/Windows/System32/calc.exe", "C:\\Windows\\notepad.exe", "\\\\servidor\\recurso",
                                            "https://usuario:clave@ejemplo.com/", "https://a b.com/", "https://localhost/", "\"https://x.com\"", "ftp://x.com/",
                                            "https://x.com/" + new string('a', 600), "mailto:soporte@x.com" })
                Check(Fmt.WebUrl(mala) == null, "URL rechazada: " + Fmt.Truncate(mala, 40));
            var acd = Prog("ACDSee 9 Administrador fotografico", "9.0.108", "ACD Systems Ltd.", "");
            acd.InfoUrl = "javascript:alert(1)"; acd.HelpLink = "www.acdsee.com";
            Eq("https://www.acdsee.com/", ProgramHints.SiteOf(acd), "sitio: la primera dirección válida");

            // --- Pistas.
            var cs2 = Prog("Counter-Strike 2", "", "Valve", "\"C:\\Program Files (x86)\\Steam\\steam.exe\" steam://uninstall/730");
            Check(ProgramHints.For(cs2) != null && ProgramHints.For(cs2).Text.Contains("Steam"), "juego de Steam");
            var fn = Prog("Fortnite", "", "Epic Games", ""); fn.InstallLocation = "C:\\Program Files\\Epic Games\\Fortnite";
            Check(ProgramHints.For(fn) != null && ProgramHints.For(fn).Text.Contains("Epic"), "juego de Epic Games");
            var epic = Prog("Epic Games Launcher", "1.3", "Epic Games, Inc.", ""); epic.InstallLocation = "C:\\Program Files (x86)\\Epic Games\\Launcher";
            Check(ProgramHints.For(epic) != null && ProgramHints.For(epic).Text.Contains("actualizador propio"), "el lanzador de Epic no es un juego: tiene actualizador propio");
            Check(ProgramHints.For(Prog("Avast One", "26.9", "Gen Digital Inc.", "")).Text.StartsWith("Antivirus"), "Avast One: antivirus");
            Check(ProgramHints.For(Prog("Avastar Tool", "1.0", "", "")) == null, "«Avastar» no es Avast");
            Check(ProgramHints.For(Prog("Logi Plugin Service", "6.4.1", "Logitech", "")).Text.Contains("Logi Options+"), "componente de Logitech");
            Check(ProgramHints.For(Prog("µTorrent", "3.6.0.47228", "BitTorrent Limited", "")).Text.Contains("Buscar actualizaciones"), "µTorrent");
            var nv = ProgramHints.For(Prog("NVIDIA Controlador de gráficos 475.14", "475.14", "NVIDIA Corporation", ""));
            Check(nv != null && nv.Text.Contains("Controladores") && nv.Url == "https://www.nvidia.com/Download/index.aspx", "controlador de NVIDIA con su sitio");
            Check(ProgramHints.For(Prog("Samsung USB Driver for Mobile Phones", "1.9.0.0", "Samsung Electronics Co., Ltd.", "")).Url == "https://developer.samsung.com/android-usb-driver", "controlador USB de Samsung");
            Check(ProgramHints.For(Prog("Digital Clock 1.0", "1.0", "FlashTECH", "")) == null, "programa desconocido: sin pista");
            string q = ProgramHints.SearchUrl("ACDSee 9 Administrador fotografico", "ACD Systems Ltd.");
            Check(q.StartsWith("https://www.google.com/search?q=") && q.Contains("ACDSee") && q.IndexOf(' ') < 0 && Fmt.WebUrl(q) != null, "búsqueda web del sitio oficial, escapada");

            // --- Anotación de las filas que quedan sin verificar.
            var programs = new List<InstalledProgram> { acd, cs2, Prog("Digital Clock 1.0", "1.0", "FlashTECH", "") };
            var rows = new List<UpdateCandidate>();
            var rAcd = Cand("ACDSee 9 Administrador fotografico", "winget", UpdateStatus.NoVerificable); rAcd.Detail = "No figura en los repositorios de winget; revisá el sitio del fabricante."; rows.Add(rAcd);
            var rCs2 = Cand("Counter-Strike 2", "winget", UpdateStatus.NoVerificable); rows.Add(rCs2);
            var rClk = Cand("Digital Clock 1.0", "winget", UpdateStatus.NoVerificable); rClk.Identifier = "registro:Digital Clock 1.0"; rows.Add(rClk);
            var rW7 = Cand("ACDSee 9 Administrador fotografico", "", UpdateStatus.NoVerificable); rW7.Detail = "Sin winget no hay un catálogo confiable con el cual comparar."; rows.Add(rW7);
            var rOk = Cand("ACDSee 9 Administrador fotografico", "winget", UpdateStatus.AlDia); rOk.Detail = "Coincide."; rows.Add(rOk);
            ProgramHints.Annotate(rows, programs);
            Check(rAcd.Detail.Contains("www.acdsee.com") && rAcd.InfoUrl == "https://www.acdsee.com/", "sin verificar, con el sitio que registra el programa");
            Check(rCs2.Detail.Contains("Steam"), "juego de Steam: lo actualiza Steam");
            Check(rClk.Detail.StartsWith("Instalado, pero winget no lo informa") && rClk.Detail.Contains("Buscar el sitio oficial") && rClk.InfoUrl == null, "sin sitio: se sugiere buscarlo");
            Check(rW7.Detail.StartsWith("Sin winget") && rW7.Detail.Contains("www.acdsee.com"), "Windows sin winget: se conserva el motivo y se agrega el sitio");
            Eq("Coincide.", rOk.Detail, "las filas verificadas no se tocan");


            // --- Opción para redes que sólo permiten winget, Windows Update y la Store.
            Check(new UpdateOptions().QueryVendors, "la consulta a fabricantes viene activada");
            var ctx = new UpdateContext();
            ctx.Os = new OsInfo();
            ctx.Options.QueryVendors = false;
            string motivo = new VendorProvider().Unavailable(ctx);
            Check(motivo != null && motivo.Contains("desactivada"), "desactivada: el proveedor no corre y lo explica");
            ctx.Options.QueryVendors = true;
            Check(new VendorProvider().Unavailable(ctx) == null, "activada: el proveedor corre");
        }

        static void TestNavegadores()
        {
            // Reconocimiento por el nombre del registro.
            Eq("chrome", BrowserCatalog.KeyFor("Google Chrome"), "Chrome reconocido");
            Eq("edge", BrowserCatalog.KeyFor("Microsoft Edge"), "Edge reconocido");
            Eq("firefox", BrowserCatalog.KeyFor("Mozilla Firefox (x64 es-AR)"), "Firefox es-AR reconocido");
            Eq("firefox", BrowserCatalog.KeyFor("Mozilla Firefox ESR (x64 en-US)"), "Firefox ESR reconocido");
            Eq("firefox", BrowserCatalog.KeyFor("Mozilla Firefox 115.0 (x86 en-US)"), "Firefox con versión en el nombre");
            Eq("firefox", BrowserCatalog.KeyFor("Mozilla Firefox (x64 es-…"), "Firefox con el nombre recortado por winget");
            Eq(null, BrowserCatalog.KeyFor("Mozilla Maintenance Service"), "el servicio de Mozilla no es el navegador");
            Eq(null, BrowserCatalog.KeyFor("Microsoft Edge WebView2 Runtime"), "WebView2 no es Edge");
            Eq(null, BrowserCatalog.KeyFor("Google Chrome Beta"), "Chrome Beta no se toma como estable");
            Eq(null, BrowserCatalog.KeyFor("Microsoft Edge Update"), "Edge Update no es Edge");

            // El caso de la captura: Chrome 153.0.8010.53 y Firefox 156.0.1 (x64 es-AR).
            var progs = new List<InstalledProgram>();
            progs.Add(Prog("Google Chrome", "153.0.8010.53", "Google LLC", "Equipo", @"C:\Program Files\Google\Chrome\Application", "x86"));
            progs.Add(Prog("Mozilla Firefox (x64 es-AR)", "156.0.1", "Mozilla", "Equipo", @"C:\Program Files\Mozilla Firefox", "x64"));
            progs.Add(Prog("Microsoft Edge", "140.0.3485.54", "Microsoft Corporation", "Equipo", @"C:\Program Files (x86)\Microsoft\Edge\Application", "x86"));
            progs.Add(Prog("Mozilla Maintenance Service", "156.0.1", "Mozilla", "Equipo", "", "x64"));
            progs.Add(Prog("Google Chrome", "150.0.1.1", "Google LLC", "Usuario actual", "", ""));
            var b = BrowserCatalog.Detect(progs, "x64");
            Eq(4, b.Count, "tres navegadores del equipo + Chrome del usuario");
            BrowserInfo chrome = null, ff = null, edge = null, chromeUser = null;
            foreach (var x in b)
            {
                if (x.Key == "chrome" && !x.PerUser) chrome = x;
                if (x.Key == "chrome" && x.PerUser) chromeUser = x;
                if (x.Key == "firefox") ff = x;
                if (x.Key == "edge") edge = x;
            }
            Check(chrome != null && chrome.Arch == "x64", "Chrome: 64 bits aunque su registro esté en la vista de 32");
            Check(edge != null && edge.Arch == "x64" && !edge.PerUser, "Edge: 64 bits en Program Files (x86), del equipo");
            Check(ff != null && ff.Arch == "x64" && ff.Lang == "es-AR" && !ff.Esr, "Firefox: x64, es-AR, no ESR");
            Check(chromeUser != null, "Chrome instalado sólo para el usuario, aparte");
            var ff32 = BrowserCatalog.Detect(new List<InstalledProgram> { Prog("Mozilla Firefox", "128.3.0", "Mozilla", "Equipo", @"C:\Program Files (x86)\Mozilla Firefox", "x86") }, "x64");
            Check(ff32.Count == 1 && ff32[0].Arch == "x86" && ff32[0].Lang == "en-US", "Firefox de 32 bits sin idioma en el nombre");

            // Carpeta deducida del desinstalador cuando falta InstallLocation.
            var sinCarpeta = Prog("Google Chrome", "1.0", "", "Equipo", "", "");
            sinCarpeta.UninstallString = "\"C:\\Program Files\\Google\\Chrome\\Application\\153.0.8010.53\\Installer\\setup.exe\" --uninstall --channel=stable --system-level";
            Eq(@"C:\Program Files\Google\Chrome\Application", BrowserCatalog.InstallDirOf(sinCarpeta), "carpeta de Chrome desde el desinstalador");
            var ffSinCarpeta = Prog("Mozilla Firefox (x64 es-AR)", "1.0", "", "Equipo", "", "");
            ffSinCarpeta.UninstallString = @"C:\Program Files\Mozilla Firefox\uninstall\helper.exe";
            Eq(@"C:\Program Files\Mozilla Firefox", BrowserCatalog.InstallDirOf(ffSinCarpeta), "carpeta de Firefox desde el desinstalador");

            // Respuestas de los servicios oficiales.
            string chromeJson = "{\"releases\":[" +
                "{\"name\":\"a\",\"version\":\"153.0.8010.72\",\"fraction\":0.25}," +
                "{\"name\":\"b\",\"version\":\"153.0.8010.68\",\"fraction\":1}," +
                "{\"name\":\"c\",\"version\":\"153.0.8010.61\",\"fraction\":0.75}],\"nextPageToken\":\"\"}";
            Eq("153.0.8010.68", BrowserCatalog.ParseChromeLatest(chromeJson), "Chrome: la versión que llega a la mayoría, no la del despliegue parcial");
            Eq("153.0.8010.72", BrowserCatalog.ParseChromeLatest("{\"releases\":[{\"version\":\"153.0.8010.72\",\"fraction\":0.1}]}"), "Chrome: si sólo hay despliegue parcial, esa");
            Eq("153.0.8010.99", BrowserCatalog.ParseChromeLatest("{\"versions\":[{\"version\":\"153.0.8010.99\"},{\"version\":\"152.0.1.1\"}]}"), "Chrome: respuesta de /versions");
            Check(BrowserCatalog.ChromeReleasesUrl("x64").Contains("/platforms/win64/"), "Chrome x64 → win64");
            Check(BrowserCatalog.ChromeReleasesUrl("x86").Contains("/platforms/win/"), "Chrome x86 → win");

            string edgeJson = "[{\"Product\":\"Beta\",\"Releases\":[{\"Platform\":\"Windows\",\"Architecture\":\"x64\",\"ProductVersion\":\"141.0.1.1\",\"Artifacts\":[]}]}," +
                "{\"Product\":\"Stable\",\"Releases\":[" +
                "{\"Platform\":\"Windows\",\"Architecture\":\"x64\",\"ProductVersion\":\"140.0.3485.66\",\"Artifacts\":[{\"ArtifactName\":\"msi\",\"Location\":\"https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/abc/MicrosoftEdgeEnterpriseX64.msi\",\"Hash\":\"AABB\",\"HashAlgorithm\":\"SHA256\",\"SizeInBytes\":170000000}]}," +
                "{\"Platform\":\"Windows\",\"Architecture\":\"x64\",\"ProductVersion\":\"140.0.3485.54\",\"Artifacts\":[]}," +
                "{\"Platform\":\"Windows\",\"Architecture\":\"x86\",\"ProductVersion\":\"140.0.3485.66\",\"Artifacts\":[]}," +
                "{\"Platform\":\"MacOS\",\"Architecture\":\"universal\",\"ProductVersion\":\"150.0.0.0\",\"Artifacts\":[]}]}]";
            var er = BrowserCatalog.ParseEdgeLatest(edgeJson, "x64");
            Check(er != null, "Edge estable x64 encontrado");
            if (er != null)
            {
                Eq("140.0.3485.66", er.Version, "Edge: última estable para Windows x64 (no Beta ni macOS)");
                Check(er.MsiUrl != null && er.MsiUrl.EndsWith("X64.msi"), "Edge: URL del MSI");
                Eq("AABB", er.Sha256, "Edge: hash SHA-256 publicado");
                Check(BrowserCatalog.IsOfficialUrl(new Uri(er.MsiUrl)), "Edge: el CDN de Microsoft es un origen permitido");
            }

            string ffJson = "{\"FIREFOX_ESR\":\"140.4.0esr\",\"LATEST_FIREFOX_VERSION\":\"156.0.2\",\"FIREFOX_NIGHTLY\":\"158.0a1\"}";
            Eq("156.0.2", BrowserCatalog.ParseFirefoxLatest(ffJson, false), "Firefox: última versión");
            Eq("140.4.0esr", BrowserCatalog.ParseFirefoxLatest(ffJson, true), "Firefox ESR: última versión");
            string ffUrl = BrowserCatalog.FirefoxInstallerUrl(false, "x64", "es-AR");
            Check(ffUrl.Contains("product=firefox-latest-ssl") && ffUrl.Contains("os=win64") && ffUrl.Contains("lang=es-AR"), "Firefox: instalador x64 es-AR");
            Check(BrowserCatalog.FirefoxInstallerUrl(true, "x86", "x\" /S&evil").Contains("lang=en-US"), "Firefox: idioma inválido → en-US");

            // Evaluación: el caso de la captura (Chrome actualizándose, 0 disponibles).
            Eq(UpdateStatus.Disponible, BrowserCatalog.Evaluate("153.0.8010.53", null, "153.0.8010.68"), "Chrome 153.0.8010.53 frente a .68: disponible");
            Eq(UpdateStatus.AlDia, BrowserCatalog.Evaluate("153.0.8010.68", null, "153.0.8010.68"), "misma versión: al día");
            Eq(UpdateStatus.AlDia, BrowserCatalog.Evaluate("154.0.1.1", null, "153.0.8010.68"), "más nueva que la estable: al día");
            Eq(UpdateStatus.RequiereReinicio, BrowserCatalog.Evaluate("153.0.8010.53", "153.0.8010.68", "153.0.8010.68"), "descargada y esperando reabrir el navegador");
            Eq(UpdateStatus.Disponible, BrowserCatalog.Evaluate("153.0.8010.53", "153.0.8010.61", "153.0.8010.68"), "lo descargado ya quedó viejo: disponible");
            Eq(UpdateStatus.Disponible, BrowserCatalog.Evaluate("", null, "153.0.8010.68"), "versión instalada desconocida: disponible");
            Eq(UpdateStatus.Error, BrowserCatalog.Evaluate("153.0.8010.53", null, null), "sin respuesta del fabricante: error");
            Eq(UpdateStatus.AlDia, BrowserCatalog.Evaluate("140.4.0", null, "140.4.0esr"), "Firefox ESR: «esr» en la versión publicada");

            // Sólo orígenes oficiales y por HTTPS.
            Check(BrowserCatalog.IsOfficialUrl(new Uri("https://dl.google.com/dl/chrome/install/googlechromestandaloneenterprise64.msi")), "dl.google.com permitido");
            Check(BrowserCatalog.IsOfficialUrl(new Uri("https://download-installer.cdn.mozilla.net/pub/firefox/x.exe")), "CDN de Mozilla permitido");
            Check(!BrowserCatalog.IsOfficialUrl(new Uri("http://dl.google.com/x.msi")), "HTTP sin cifrar rechazado");
            Check(!BrowserCatalog.IsOfficialUrl(new Uri("https://dl.google.com.evil.example/x.msi")), "dominio que imita a Google rechazado");
            Check(!BrowserCatalog.IsOfficialUrl(new Uri("https://evilgoogle.com/x.msi")), "sufijo sin punto rechazado");

            // Firmante del instalador.
            Check(BrowserCatalog.SubjectIsFrom("CN=Google LLC, O=Google LLC, L=Mountain View, S=California, C=US", "Google LLC"), "firma de Google");
            Check(BrowserCatalog.SubjectIsFrom("CN=Mozilla Corporation, OU=Firefox Engineering Operations, O=Mozilla Corporation, L=San Francisco, S=California, C=US", "Mozilla Corporation"), "firma de Mozilla");
            Check(!BrowserCatalog.SubjectIsFrom("CN=Google LLC, O=Google LLC Falsa, C=US", "Google LLC"), "organización parecida rechazada");
            Check(!BrowserCatalog.SubjectIsFrom("CN=\"O=Google LLC\", O=Otro, C=US", "Google LLC"), "O= dentro del CN no cuenta");

            // Deduplicación: la fila del fabricante reemplaza a la de winget / registro.
            var all = new List<UpdateCandidate>();
            all.Add(Cand("Google Chrome", "winget", UpdateStatus.NoVerificable));
            all.Add(Cand("Mozilla Firefox (x64 es-AR)", "winget", UpdateStatus.NoVerificable));
            all.Add(Cand("Microsoft Edge", "winget", UpdateStatus.AlDia));
            all.Add(Cand("Google Chrome", BrowserProvider.ProviderId, UpdateStatus.Disponible));
            all.Add(Cand("Mozilla Firefox (x64 es-AR)", BrowserProvider.ProviderId, UpdateStatus.AlDia));
            all.Add(Cand("Microsoft Edge", BrowserProvider.ProviderId, UpdateStatus.Error));
            all.Add(Cand("7-Zip 24.07 (x64)", "winget", UpdateStatus.AlDia));
            UpdateManager.DeduplicateBrowsers(all);
            int chromes = 0, firefoxes = 0, edges = 0; string edgeProv = null;
            foreach (var c in all)
            {
                if (c.Name == "Google Chrome") { chromes++; Eq(BrowserProvider.ProviderId, c.ProviderName, "Chrome queda con el dato del fabricante"); }
                if (c.Name.StartsWith("Mozilla Firefox")) { firefoxes++; Eq(BrowserProvider.ProviderId, c.ProviderName, "Firefox queda con el dato del fabricante"); }
                if (c.Name == "Microsoft Edge") { edges++; edgeProv = c.ProviderName; }
            }
            Eq(1, chromes, "una sola fila de Chrome");
            Eq(1, firefoxes, "una sola fila de Firefox");
            Eq(1, edges, "una sola fila de Edge");
            Eq("winget", edgeProv, "si el fabricante no respondió, queda la fila de winget");
            Eq(4, all.Count, "7-Zip no se toca");

            var soloError = new List<UpdateCandidate> { Cand("Google Chrome", BrowserProvider.ProviderId, UpdateStatus.Error) };
            UpdateManager.DeduplicateBrowsers(soloError);
            Eq(1, soloError.Count, "sin otra fila, el error del fabricante se muestra");

            // «winget list» con versión disponible más nueva no es «al día».
            var wr = new WingetRow(); wr.Name = "Zoom"; wr.Id = "Zoom.Zoom"; wr.Version = "6.1.0"; wr.Available = "6.2.0"; wr.Source = "winget";
            var fr = WingetProvider.FromListRow(wr, new UpdateCandidate());
            Eq(UpdateStatus.Disponible, fr.Status, "fila de winget list con disponible más nueva: disponible");
            Check(fr.Installable && !fr.Recommended, "se puede instalar pero no se premarca");
            var wr2 = new WingetRow(); wr2.Name = "Git"; wr2.Id = "Git.Git"; wr2.Version = "2.46.0"; wr2.Source = "winget";
            Eq(UpdateStatus.AlDia, WingetProvider.FromListRow(wr2, new UpdateCandidate()).Status, "fila de winget list sin disponible: al día");
            var wr3 = new WingetRow(); wr3.Name = "X"; wr3.Id = @"ARP\Machine\X64\X"; wr3.Version = "1.0";
            Eq(UpdateStatus.NoVerificable, WingetProvider.FromListRow(wr3, new UpdateCandidate()).Status, "fila sin origen: no verificable");

            var chromeDisp = Cand("Google Chrome", BrowserProvider.ProviderId, UpdateStatus.Disponible);
            chromeDisp.Installable = true;
            Check(UpdateManager.CanInstall(chromeDisp), "Chrome disponible se puede actualizar desde el programa");
        }

        static UpdateCandidate Cand(string name, string provider, UpdateStatus st)
        {
            var c = new UpdateCandidate();
            c.Kind = UpdateKind.Programa; c.Name = name; c.ProviderName = provider; c.Status = st;
            c.InstalledVersion = "1"; c.AvailableVersion = "";
            return c;
        }


        static void TestFabricanteYReintentos()
        {
            Check(VendorCatalog.Match("AIMP") != null, "AIMP reconocido");
            Check(VendorCatalog.Match("AIMP v5.40.2691") != null, "AIMP con versión en el nombre");
            Check(VendorCatalog.Match("AIMP (x64)") != null, "AIMP (x64)");
            Check(VendorCatalog.Match("AIMPlayer Tools") == null, "otro programa que empieza con AIMP no coincide");
            Check(VendorCatalog.Match("Winamp") == null, "Winamp no es AIMP");

            var aimp = VendorCatalog.Match("AIMP");
            // Texto de https://www.aimp.ru/?do=download&os=windows (estable, versión para XP y una beta).
            string page = "<div>AIMP for Windows</div><div class=\"ver\">v5.40.2727 (08.09.2026)</div>" +
                          "<div>Portable</div><div class=\"ver\">v5.40.2727 (08.09.2026)</div>" +
                          "<div>Windows XP</div><div class=\"ver\">v4.31.1747 (10.09.2026)</div>" +
                          "<p>...............................................................................</p>" +
                          "<div>Beta</div><div class=\"ver\">v6.00.3083 (15.09.2026)</div>";
            Eq("5.40.2727", VendorCatalog.ParseLatest(aimp, page), "AIMP: la estable más alta, sin la beta");
            Eq(null, VendorCatalog.ParseLatest(aimp, "<html>mantenimiento</html>"), "página sin versión: null");
            Check(aimp.Allows(new Uri("https://www.aimp.ru/?do=download&os=windows")), "aimp.ru permitido");
            Check(!aimp.Allows(new Uri("https://aimp.ru.evil.example/")), "dominio que imita a aimp.ru rechazado");
            Check(!aimp.Allows(new Uri("http://www.aimp.ru/")), "aimp.ru sin HTTPS rechazado");
            Check(!aimp.Allows(new Uri("https://dl.google.com/")), "la consulta de AIMP no acepta otros dominios");

            // Página de descargas real (estructura de aimp.ru: estable, portable sin hash y beta).
            string html =
                "<html><head><script>var x='AIMP v9.99.9999 (01.01.2030)';</script></head><body>" +
                "<div class=\"logo\"><img src=\"logo.svg\"> <a href=\"/?do=download&amp;os=windows\">v5.40.2727 (08.09.2026)</a>" +
                " <a href=\"/?do=download&amp;os=android\">v4.31.1747 (10.09.2026)</a></div>" +
                "<table><tr><td><img></td><td><b>AIMP v5.40.2727</b><br/><small>Release date: 08.09.2026 Targets to: Desktop</small></td></tr></table>" +
                "<ul><li><a class=\"btn\" href=\"https://aimp.ru/?do=download.file&amp;id=4\">Download (17.7 MB)</a>" +
                "<a href=\"/?do=download.file&amp;id=4\">/?do=download.file&amp;id=4</a><span>32-bit</span><div>File Size: 17.7 MB</div>" +
                "<div>MD5: 1c891e06a1a0f81faa11e3e982b1f4ec</div><div>SHA256: f13fa3933dd749d944f030a47c6263f42b54c3c7bbb44e3cb76852b663d7386e</div>" +
                "<a href='/?do=download.file&id=8'>Download build without the installer</a></li>" +
                "<li><a class=\"btn\" href=\"https://aimp.ru/?do=download.file&amp;id=3\">Download (21.0 MB)</a>" +
                "<a href=\"/?do=download.file&amp;id=3\">/?do=download.file&amp;id=3</a><span>64-bit</span><div>File Size: 21.0 MB</div>" +
                "<div>MD5: 61136b8cd8640b9ea5180f61ca6aa6b6</div><div>SHA256: CC6BABDE28777EDCDF608D09D5ABADB188C13A5FCBFE63740B53E76EA026C4CD</div>" +
                "<i>Plugins from previous versions will not work</i><a href=\"/?do=download.file&amp;id=31\">Download build without the installer</a></li></ul>" +
                "<h3>Beta versions</h3><table><tr><td><b>AIMP v6.00.3085 Beta 6</b><small>Release date: 21.09.2026</small></td></tr></table>" +
                "<ul><li><a href=\"/?do=download.file&amp;id=21\">Download (21.9 MB)</a><span>64-bit</span>" +
                "<div>SHA256: c82293cae69c23bbc0b9fb14f987ce2a93559642928ff56e5940d9ac83275988</div></li></ul></body></html>";
            var dl = VendorCatalog.ParseDownloads(html);
            Eq(2, dl.Count, "AIMP: dos instaladores estables con hash (32 y 64 bits), sin la portable ni la beta");
            var d64 = VendorCatalog.Pick(dl, "5.40.2727", "x64");
            var d32 = VendorCatalog.Pick(dl, "5.40.2727", "x86");
            Check(d64 != null && d64.Id == "3" && d64.Sha256 == "cc6babde28777edcdf608d09d5abadb188c13a5fcbfe63740b53e76ea026c4cd", "AIMP 64 bits: id 3 y su SHA-256");
            Check(d32 != null && d32.Id == "4" && d32.Sha256 == "f13fa3933dd749d944f030a47c6263f42b54c3c7bbb44e3cb76852b663d7386e", "AIMP 32 bits: id 4 y su SHA-256");
            Check(VendorCatalog.Pick(dl, "6.00.3085", "x64") == null, "la beta 6.00 no se ofrece");
            Eq("5.40.2727", VendorCatalog.ParseLatest(aimp, html), "última estable desde la página real (sin la beta ni el texto de un script)");
            if (d64 != null)
            {
                Check(aimp.Allows(new Uri(VendorCatalog.DownloadUrl(d64))), "enlace de descarga en aimp.ru");
                Eq("https://www.aimp.ru/files/desktop/builds/aimp_5.40.2727_w64.exe", VendorCatalog.DirectUrl(d64), "dirección directa del instalador de 64 bits");
            }
            Eq(0, VendorCatalog.ParseDownloads("<a href=\"/?do=download.file&id=3\">x</a> 64-bit sin hash").Count, "sin hash publicado no hay instalador");
            // Texto pegado, como lo devuelve la página real («32-bitFile», «…c4cdPlugins»).
            string pegado = "<p>AIMP v5.40.2727Release date: 08.09.2026</p>" +
                "<a href=\"https://aimp.ru/?do=download.file&amp;id=3\">Download (21.0 MB)</a><a href=\"https://aimp.ru/?do=download.file&amp;id=3\">/?do=download.file&amp;id=3</a>" +
                "64-bitFile Size: 21.0 MBMD5: 61136b8cd8640b9ea5180f61ca6aa6b6SHA256: cc6babde28777edcdf608d09d5abadb188c13a5fcbfe63740b53e76ea026c4cdPlugins from previous versions will not work" +
                "<a href=\"https://aimp.ru/?do=download.file&amp;id=31\">Download build without the installer</a>" +
                "<p>AIMP v6.00.3085 Beta 6Release date: 21.09.2026</p><a href=\"https://aimp.ru/?do=download.file&amp;id=21\">x</a>64-bitSHA256: c82293cae69c23bbc0b9fb14f987ce2a93559642928ff56e5940d9ac83275988";
            var pd = VendorCatalog.ParseDownloads(pegado);
            Check(pd.Count == 1 && pd[0].Arch == "x64" && pd[0].Id == "3" && pd[0].Sha256 == "cc6babde28777edcdf608d09d5abadb188c13a5fcbfe63740b53e76ea026c4cd",
                  "texto pegado: instalador de 64 bits con su hash exacto, sin la beta");

            // El caso de la captura: winget lo daba al día en 5.40.2691, AIMP ofrece 5.40.2727.
            var all = new List<UpdateCandidate>();
            var wg = Cand("AIMP", "winget", UpdateStatus.AlDia); wg.InstalledVersion = "5.40.2691"; all.Add(wg);
            var fab = Cand("AIMP", VendorProvider.ProviderId, UpdateStatus.Disponible); fab.AvailableVersion = "5.40.2727"; all.Add(fab);
            all.Add(Cand("VLC media player", "winget", UpdateStatus.AlDia));
            UpdateManager.DeduplicateVendor(all);
            Eq(2, all.Count, "una sola fila de AIMP (más VLC)");
            Check(all.Contains(fab) && !all.Contains(wg), "queda la fila del fabricante: disponible 5.40.2727");

            // Si winget ya la ofrece instalable, se prefiere winget (se instala desde el programa).
            var all2 = new List<UpdateCandidate>();
            var wg2 = Cand("AIMP", "winget", UpdateStatus.Disponible); wg2.Installable = true; all2.Add(wg2);
            var fab2 = Cand("AIMP", VendorProvider.ProviderId, UpdateStatus.Disponible); all2.Add(fab2);
            UpdateManager.DeduplicateVendor(all2);
            Check(all2.Count == 1 && all2[0] == wg2, "winget con actualización instalable tiene prioridad");

            // Si la consulta al fabricante falló, queda winget.
            var all3 = new List<UpdateCandidate> { Cand("AIMP", "winget", UpdateStatus.AlDia), Cand("AIMP", VendorProvider.ProviderId, UpdateStatus.Error) };
            UpdateManager.DeduplicateVendor(all3);
            Check(all3.Count == 1 && all3[0].ProviderName == "winget", "error del fabricante: queda winget");

            // Reintentos: el corte de TeamViewer (0x80072EE2) se reintenta; el hash de GOM Player no.
            Check(WingetProvider.IsTransientNetwork(0x80072EE2), "0x80072EE2 (tiempo de espera) se reintenta");
            Check(WingetProvider.IsTransientNetwork(0x8A150008), "falla de descarga de winget se reintenta");
            Check(!WingetProvider.IsTransientNetwork(0x8A150011), "hash que no coincide NO se reintenta");
            Check(!WingetProvider.IsTransientNetwork(0), "éxito no se reintenta");
            Check(WingetProvider.Describe(0x80072EE2).Contains("tiempo de espera"), "mensaje claro para 0x80072EE2");
            Check(WingetProvider.Describe(0x8A150011).Contains("catálogo de winget"), "mensaje claro para el hash de GOM Player");
        }
    }
}
