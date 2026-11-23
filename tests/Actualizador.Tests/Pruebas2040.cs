// Pruebas de la 2.0.40. Captura del usuario (23/11/2026), «seguimos con los errores»: Npcap 1.82 → 1.90 «Falló la instalación»,
// «El instalador verificado devolvió el código 2; suele significar que no acepta instalarse en silencio…».
using System;
using System.Collections.Generic;
using System.IO;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        const string NpcapHome = "https://npcap.com/";
        const string NpcapExe190 = "https://npcap.com/dist/npcap-1.90.exe";
        const string NpcapSigner = "CN=Insecure.Com LLC, O=Insecure.Com LLC, L=Seattle, S=Washington, C=US";

        static InstalledProgram NpcapInstalado(string version)
        {
            var p = Vc("Npcap", version, "x64");
            p.Publisher = "Nmap Project";
            p.InstallLocation = @"C:\Program Files\Npcap";
            p.UninstallString = "\"C:\\Program Files\\Npcap\\uninstall.exe\"";
            p.RegistryKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst";
            p.InfoUrl = NpcapHome;
            return p;
        }

        static UpdateCandidate FilaNpcap(UpdateContext ctx, InstalledProgram p, bool conManifiesto)
        {
            var w = new WingetInstaller { Architecture = "x64", Type = "nullsoft", Scope = "machine", Url = NpcapExe190, Sha256 = "", UpgradeBehavior = "install" };
            var lead = new InstallerLead { Url = NpcapExe190, FileName = "npcap-1.90.exe", Origin = conManifiesto ? InstallerLead.Previous : InstallerLead.Page, Sha256 = "",
                                           WingetId = "Insecure.Npcap", Winget = conManifiesto ? w : null, VersionConfirmed = true, FoundAt = conManifiesto ? "" : NpcapHome + "#download" };
            var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = p.Name, Publisher = p.Publisher, InstalledVersion = p.Version, Status = UpdateStatus.AlDia, ProviderName = WingetProvider.ProviderId };
            var r = new SiteReading { Latest = "1.90", Url = NpcapHome, Evidence = "Npcap 1.90" };
            SiteVersions.Apply(ctx, c, p, r, true, lead, null);
            return c;
        }

        /// <summary>Qué instaladores no aceptan el modo silencioso, y qué código quiere decir «se negó a instalarse en silencio».</summary>
        static void TestReglasSinSilencio2040()
        {
            Check(InstallerRules.WindowOnly("Npcap", null) != null, "Npcap (gratuito): sólo con su ventana");
            Check(InstallerRules.WindowOnly("Npcap 1.82", null) != null, "con la versión en el nombre, también");
            Check(InstallerRules.WindowOnly("Algo", "Insecure.Npcap") != null, "por el paquete de winget");
            Check(InstallerRules.WindowOnly("Npcap OEM", null) == null, "Npcap OEM sí acepta /S");
            Check(InstallerRules.WindowOnly("Wireshark 4.4.2 x64", "WiresharkFoundation.Wireshark") == null && InstallerRules.WindowOnly("Nmap 7.95", "Insecure.Nmap") == null,
                  "Wireshark y Nmap se instalan en silencio");
            Check(InstallerRules.WindowOnly(null, null) == null, "sin nombre: nada");
            Check(InstallerRules.WindowOnly("Npcap", null).Contains("edición gratuita"), "el motivo lo explica: " + InstallerRules.WindowOnly("Npcap", null));
            Check(InstallerRules.RefusedSilent(InstallerKind.Nsis, 2), "NSIS 2: el instalador interrumpió (no acepta el silencio)");
            Check(!InstallerRules.RefusedSilent(InstallerKind.Nsis, 1) && !InstallerRules.RefusedSilent(InstallerKind.Inno, 2) && !InstallerRules.RefusedSilent(InstallerKind.Msi, 2),
                  "otros códigos y otras tecnologías: no");
        }

        /// <summary>La captura: Npcap 1.82 → 1.90 se instala con su ventana, sin el intento en silencio que siempre termina con 2.</summary>
        static void TestNpcapConSuVentana2040()
        {
            var oldDownload = VendorProvider.DownloadForTests;
            var oldSig = VendorProvider.SignatureForTests;
            var oldSigners = WingetDirect.InstalledSignersForTests;
            var oldProduct = SiteVersions.ProductOfForTests;
            var oldRun = VendorProvider.RunInstallerForTests;
            var oldNow = VendorProvider.ProgramsNowForTests;
            var oldExeV = VendorProvider.ExeVersionForTests;
            var oldOpen = VendorProvider.OpenPageForTests;
            var oldPolicy = UpdateManager.PolicyForTests;
            int oldWait = VendorProvider.ConfirmWaitMs, oldPoll = VendorProvider.ConfirmPollMs;
            try
            {
                VendorProvider.ConfirmWaitMs = 60; VendorProvider.ConfirmPollMs = 10;
                UpdateManager.PolicyForTests = new Policy();
                var ctx = ContextoSimulado(true);
                var instalado = NpcapInstalado("1.82");
                ctx.Programs.Add(instalado);
                byte[] exe = InstaladorFalso("NullsoftInst", 7);
                var ejecutados = new List<string>();
                var abiertas = new List<string>();
                string versionAhora = "1.82";
                int codigo = 0;
                VendorProvider.DownloadForTests = delegate(string url, string file) { File.WriteAllBytes(file, exe); };
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(0, NpcapSigner); };
                WingetDirect.InstalledSignersForTests = delegate(InstalledProgram p) { return new List<string> { "Insecure.Com LLC" }; };
                SiteVersions.ProductOfForTests = delegate(string f) { return "Npcap"; };
                VendorProvider.ExeVersionForTests = delegate(string f) { return "1.90"; };
                VendorProvider.RunInstallerForTests = delegate(string file, string args)
                {
                    ejecutados.Add(Path.GetFileName(file) + "|" + args);
                    // El instalador gratuito: con /S se niega (2) y no hace nada; con su ventana, instala.
                    if (args.Contains("/S")) return new ProcResult { Started = true, ExitCode = 2 };
                    if (codigo == 0) versionAhora = "1.90";
                    return new ProcResult { Started = true, ExitCode = codigo };
                };
                VendorProvider.ProgramsNowForTests = delegate { var q = NpcapInstalado(versionAhora); return new List<InstalledProgram> { q }; };
                VendorProvider.OpenPageForTests = delegate(string u) { abiertas.Add(u); };
                Action reiniciar = delegate { ejecutados.Clear(); abiertas.Clear(); versionAhora = "1.82"; codigo = 0; };

                // --- 1) Desde la ventana: se abre directamente con su ventana (sin /S) y queda instalada.
                var prov = new VendorProvider();
                var c = FilaNpcap(ctx, instalado, false);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Eq(UpdateStatus.Instalada, c.Status, "Npcap 1.90 instalada (" + c.Result + ")");
                Check(ejecutados.Count == 1 && ejecutados[0] == "npcap-1.90.exe|", "una sola vez y con su ventana, sin /S: " + string.Join(", ", ejecutados.ToArray()));
                Eq(0, abiertas.Count, "no abre ninguna página");

                // --- 2) Con el manifiesto de winget (nullsoft, /S): tampoco se usan sus interruptores.
                reiniciar();
                c = FilaNpcap(ctx, instalado, true);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Instalada && ejecutados.Count == 1 && ejecutados[0] == "npcap-1.90.exe|", "con el manifiesto: igual, con su ventana (" + string.Join(", ", ejecutados.ToArray()) + "; " + c.Result + ")");

                // --- 3) Se cancela en su ventana (NSIS 1): queda en error y lo dice claro, sin el «no acepta instalarse en silencio».
                reiniciar();
                codigo = 1;
                c = FilaNpcap(ctx, instalado, false);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("cancelado en su ventana") && c.Result.Contains("sigue la 1.82") && !c.Result.Contains("Mostrar la ventana"),
                      "cancelado: «" + c.Result + "»");

                // --- 4) Sin nadie frente a la pantalla (revisión programada, consola): no se abre, y dice cómo hacerlo.
                reiniciar();
                var solo = new VendorProvider { Unattended = true };
                c = FilaNpcap(ctx, instalado, false);
                solo.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && ejecutados.Count == 0 && c.Result.Contains("sin nadie frente a la pantalla") && c.Result.Contains("edición gratuita de Npcap"),
                      "sin nadie: no se ejecuta (" + c.Result + ")");

                // --- 5) La política no permite instaladores con ventana: tampoco.
                reiniciar();
                UpdateManager.PolicyForTests = PoliticaDe("{ \"permitirInstaladoresConVentana\": false }");
                c = FilaNpcap(ctx, instalado, false);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(ejecutados.Count == 0 && c.Result.Contains("política de la empresa no permite instaladores con ventana") && abiertas.Count == 1 && abiertas[0].Contains("npcap.com"),
                      "política: no se ejecuta y se abre su página, como con cualquier instalador que no se puede usar (" + c.Result + ")");
            }
            finally
            {
                VendorProvider.DownloadForTests = oldDownload; VendorProvider.SignatureForTests = oldSig; WingetDirect.InstalledSignersForTests = oldSigners;
                SiteVersions.ProductOfForTests = oldProduct; VendorProvider.RunInstallerForTests = oldRun; VendorProvider.ProgramsNowForTests = oldNow;
                VendorProvider.ExeVersionForTests = oldExeV; VendorProvider.OpenPageForTests = oldOpen; UpdateManager.PolicyForTests = oldPolicy;
                VendorProvider.ConfirmWaitMs = oldWait; VendorProvider.ConfirmPollMs = oldPoll;
            }
        }

        /// <summary>Cualquier instalador NSIS que en silencio se niega (código 2): se vuelve a abrir una vez con su ventana.</summary>
        static void TestSeNiegaEnSilencioSeAbreConVentana2040()
        {
            var oldDownload = VendorProvider.DownloadForTests;
            var oldSig = VendorProvider.SignatureForTests;
            var oldSigners = WingetDirect.InstalledSignersForTests;
            var oldProduct = SiteVersions.ProductOfForTests;
            var oldRun = VendorProvider.RunInstallerForTests;
            var oldNow = VendorProvider.ProgramsNowForTests;
            var oldExeV = VendorProvider.ExeVersionForTests;
            var oldOpen = VendorProvider.OpenPageForTests;
            var oldPolicy = UpdateManager.PolicyForTests;
            int oldWait = VendorProvider.ConfirmWaitMs, oldPoll = VendorProvider.ConfirmPollMs;
            try
            {
                VendorProvider.ConfirmWaitMs = 60; VendorProvider.ConfirmPollMs = 10;
                UpdateManager.PolicyForTests = new Policy();
                var ctx = ContextoSimulado(true);
                var instalado = CdiInstalado("9.9.4");
                ctx.Programs.Add(instalado);
                byte[] exe = InstaladorFalso("NullsoftInst", 3);
                var ejecutados = new List<string>();
                string versionAhora = "9.9.4";
                VendorProvider.DownloadForTests = delegate(string url, string file) { File.WriteAllBytes(file, exe); };
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(0, CdiSigner); };
                WingetDirect.InstalledSignersForTests = delegate(InstalledProgram p) { return new List<string> { "Open Source Developer", "Open Source Developer, Noriyuki Miyazaki" }; };
                SiteVersions.ProductOfForTests = delegate(string f) { return "CrystalDiskInfo"; };
                VendorProvider.ExeVersionForTests = delegate(string f) { return "9.9.5.0"; };
                VendorProvider.RunInstallerForTests = delegate(string file, string args)
                {
                    ejecutados.Add(args);
                    if (args == "/S") return new ProcResult { Started = true, ExitCode = 2 };
                    versionAhora = "9.9.5";
                    return new ProcResult { Started = true, ExitCode = 0 };
                };
                VendorProvider.ProgramsNowForTests = delegate { return new List<InstalledProgram> { CdiInstaladoComo(instalado, versionAhora) }; };
                VendorProvider.OpenPageForTests = delegate(string u) { };

                // --- Desde la ventana: /S → 2, se abre con su ventana y queda.
                var c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                new VendorProvider().Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(ejecutados.Count == 2 && ejecutados[0] == "/S" && ejecutados[1] == "", "primero en silencio y, al negarse, con su ventana: " + string.Join(" | ", ejecutados.ToArray()));
                Eq(UpdateStatus.Instalada, c.Status, "y quedó instalada (" + c.Result + ")");

                // --- Sin nadie frente a la pantalla: no se abre; el error explica el código 2.
                ejecutados.Clear(); versionAhora = "9.9.4";
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                new VendorProvider { Unattended = true }.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(ejecutados.Count == 1 && c.Status == UpdateStatus.Fallida && c.Result.Contains("2: el propio instalador interrumpió la instalación"),
                      "sin nadie: un solo intento y el motivo (" + c.Result + ")");

                // --- Otro código (5, 3…): no se reabre, como antes.
                ejecutados.Clear(); versionAhora = "9.9.4";
                VendorProvider.RunInstallerForTests = delegate(string file, string args) { ejecutados.Add(args); return new ProcResult { Started = true, ExitCode = 3 }; };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                new VendorProvider().Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(ejecutados.Count == 1 && c.Status == UpdateStatus.Fallida && c.Result.Contains("código 3"), "otro código: un solo intento (" + c.Result + ")");
            }
            finally
            {
                VendorProvider.DownloadForTests = oldDownload; VendorProvider.SignatureForTests = oldSig; WingetDirect.InstalledSignersForTests = oldSigners;
                SiteVersions.ProductOfForTests = oldProduct; VendorProvider.RunInstallerForTests = oldRun; VendorProvider.ProgramsNowForTests = oldNow;
                VendorProvider.ExeVersionForTests = oldExeV; VendorProvider.OpenPageForTests = oldOpen; UpdateManager.PolicyForTests = oldPolicy;
                VendorProvider.ConfirmWaitMs = oldWait; VendorProvider.ConfirmPollMs = oldPoll;
            }
        }

        /// <summary>Por winget: Npcap se actualiza con «--interactive» (su ventana), y sólo esa fila.</summary>
        static void TestNpcapPorWinget2040()
        {
            var oldPolicy = UpdateManager.PolicyForTests;
            try
            {
                UpdateManager.PolicyForTests = new Policy();
                var npcap = NpcapInstalado("1.82");
                var zip = Vc("7-Zip 24.08 (x64)", "24.08", "x64");
                var cN = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Npcap", InstalledVersion = "1.82", AvailableVersion = "1.90", ProviderName = WingetProvider.ProviderId,
                                               Status = UpdateStatus.Disponible, ProviderTag = new WingetRow { Name = "Npcap", Id = "Insecure.Npcap", Version = "1.82", Available = "1.90", Source = "winget" } };
                var cZ = new UpdateCandidate { Kind = UpdateKind.Programa, Name = zip.Name, InstalledVersion = "24.08", AvailableVersion = "26.00", ProviderName = WingetProvider.ProviderId,
                                               Status = UpdateStatus.Disponible, ProviderTag = new WingetRow { Name = zip.Name, Id = "7zip.7zip", Version = "24.08", Available = "26.00", Source = "winget" } };
                var pedidos = new List<string>();
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.RunForTests = delegate(string args) { pedidos.Add(args); return Salida(0, "Instalado correctamente\n"); };
                wp.Install(ContextoSimulado(true, npcap, zip), new List<UpdateCandidate> { cN, cZ }, delegate(UpdateCandidate u) { });
                string pN = pedidos.Find(delegate(string a) { return a.Contains("Insecure.Npcap"); });
                string pZ = pedidos.Find(delegate(string a) { return a.Contains("7zip.7zip"); });
                Check(pN != null && pN.Contains("--interactive") && !pN.Contains("--silent"), "Npcap con su ventana: " + pN);
                Check(pZ != null && pZ.Contains("--silent") && !pZ.Contains("--interactive"), "lo demás sigue en silencio: " + pZ);
                Check(!wp.Interactive, "terminado, el proveedor vuelve a como estaba");

                // Sin nadie frente a la pantalla: Npcap no se ejecuta.
                pedidos.Clear();
                cN.Status = UpdateStatus.Disponible; cN.Result = "";
                var wu = new WingetProvider { Unattended = true };
                wu.SimulateVersion("1.29.380");
                wu.RunForTests = delegate(string args) { pedidos.Add(args); return Salida(0, "Instalado correctamente\n"); };
                wu.Install(ContextoSimulado(true, npcap), new List<UpdateCandidate> { cN }, delegate(UpdateCandidate u) { });
                Check(cN.Status == UpdateStatus.Fallida && !pedidos.Exists(delegate(string a) { return a.Contains("Insecure.Npcap"); }) && cN.Result.Contains("sin nadie frente a la pantalla"),
                      "sin nadie: no se ejecuta (" + cN.Result + ")");
            }
            finally { UpdateManager.PolicyForTests = oldPolicy; }
        }

        static void TestVersion2040()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.40") >= 0, "Actualizador 2.0.40 o posterior: " + AppInfo.Version);
        }
    }
}
