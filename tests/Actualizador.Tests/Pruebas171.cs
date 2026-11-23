// Pruebas de la 1.7.1: Visual C++ instalable, winget que se instala solo, todo de una vez, programas sin registro y todos los controladores.
using System.Collections.Generic;
using System.IO;
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
        // ------------------------------------------------------------------ Visual C++ 2008–2013

        static InstalledProgram Vc(string name, string version, string arch)
        {
            var p = new InstalledProgram();
            p.Name = name; p.Version = version; p.Publisher = "Microsoft Corporation"; p.Scope = "Equipo"; p.Architecture = arch;
            p.InstallLocation = ""; p.UninstallString = ""; p.UninstallCommand = ""; p.QuietUninstallCommand = ""; p.DisplayIcon = "";
            p.InfoUrl = ""; p.UpdateUrl = ""; p.HelpLink = ""; p.RegistryKey = "";
            return p;
        }

        static void TestVisualCpp171()
        {
            // --- El catálogo: instaladores oficiales de Microsoft, con SHA-256 y por HTTPS.
            foreach (string id in new[] { "vcredist2008", "vcredist2010", "vcredist2012", "vcredist2013" })
            {
                var check = VendorCatalog.Find(id);
                Check(check != null && check.Install == VendorInstall.SignedInstaller, id + ": se instala");
                Eq("Microsoft Corporation", check.Signers[0], id + ": firmante Microsoft");
                Check(InstallerRules.IsSafeArgs(check.SilentArgs) && check.SilentArgs.Length > 0, id + ": interruptores en silencio seguros");
                foreach (string arch in new[] { "x64", "x86" })
                {
                    var f = check.FinalInstallerFor(arch);
                    Check(f != null, id + " " + arch + ": instalador");
                    if (f == null) continue;
                    Uri u;
                    Check(Uri.TryCreate(f.Url, UriKind.Absolute, out u) && check.Allows(u), id + " " + arch + ": HTTPS de microsoft.com (" + f.Url + ")");
                    Check(f.Url.EndsWith("vcredist_" + arch + ".exe", StringComparison.OrdinalIgnoreCase), id + " " + arch + ": el archivo de esa arquitectura");
                    Check(System.Text.RegularExpressions.Regex.IsMatch(f.Sha256, "^[0-9a-f]{64}$"), id + " " + arch + ": SHA-256 en minúsculas");
                }
                Check(!check.Allows(new Uri("https://download.microsoft.com.evil.example/vcredist_x64.exe")), id + ": imitación rechazada");
            }
            Check(VendorCatalog.Find("vcredist2005-x64").Install == VendorInstall.None, "2005: sólo se compara (su instalador no tiene un modo silencioso seguro)");
            Eq("681be3e5ba9fd3da02c09d7e565adfa078640ed66a0d58583efad2c1e3cc4064", VendorCatalog.Find("vcredist2012").FinalInstallerFor("x64").Sha256, "2012 x64: el SHA-256 del catálogo de winget");

            // --- La grabación del usuario: Visual C++ 2012 x64 y x86 11.0.50727 «Actualización disponible» que no se podía marcar.
            var x64 = Vc("Microsoft Visual C++ 2012 Redistributable (x64) - 11.0.50727", "11.0.50727.1", "x86");   // Burn lo registra en la vista de 32 bits
            var x86 = Vc("Microsoft Visual C++ 2012 Redistributable (x86) - 11.0.50727", "11.0.50727.1", "x86");
            var ctx = ContextoSimulado(true, x64, x86);
            var output = new List<UpdateCandidate>();
            var notes = new List<string>();
            var vp = new VendorProvider();
            vp.Scan(ctx, output, notes);
            var r64 = output.Find(delegate(UpdateCandidate c) { return c.Name == x64.Name; });
            var r86 = output.Find(delegate(UpdateCandidate c) { return c.Name == x86.Name; });
            Check(r64 != null && r86 != null, "una fila por cada arquitectura");
            if (r64 != null && r86 != null)
            {
                Eq(UpdateStatus.Disponible, r64.Status, "x64: disponible");
                Check(r64.Installable && UpdateManager.CanInstall(r64), "x64: ahora SE PUEDE MARCAR");
                Check(r64.Recommended, "x64: recomendada («Marcar disponibles» y «Actualizar todo» la incluyen)");
                var p64 = (VendorPlan)r64.ProviderTag;
                Check(p64.InstallerUrl.EndsWith("/VSU_4/vcredist_x64.exe"), "x64: el instalador x64 aunque el registro diga x86 (" + p64.InstallerUrl + ")");
                Check(p64.PinnedSha256 && p64.InstallerSha256.Length == 64, "x64: SHA-256 fijo");
                Eq("11.0.61030", p64.Latest, "x64: versión que se instala");
                var p86 = (VendorPlan)r86.ProviderTag;
                Check(p86.InstallerUrl.EndsWith("/VSU_4/vcredist_x86.exe"), "x86: el instalador x86");
                Check(r64.Detail.Contains("download.microsoft.com"), "el detalle dice de dónde se baja");
            }

            // Sin administrador: se explica, no se ofrece marcar algo que va a fallar.
            var ctxUser = ContextoSimulado(false, Vc("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.30501", "12.0.30501.0", "x86"));
            var outUser = new List<UpdateCandidate>();
            vp.Scan(ctxUser, outUser, new List<string>());
            Check(outUser.Count == 1 && !outUser[0].Installable && outUser[0].Detail.Contains("administrador"), "sin administrador: no se puede marcar y dice por qué");

            // Al día y 2005 sin cambios.
            var ctxOk = ContextoSimulado(true, Vc("Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219", "10.0.40219", "x64"),
                                               Vc("Microsoft Visual C++ 2005 Redistributable", "8.0.59193", "x86"));
            var outOk = new List<UpdateCandidate>();
            vp.Scan(ctxOk, outOk, new List<string>());
            Check(outOk.Exists(delegate(UpdateCandidate c) { return c.Name.Contains("2010") && c.Status == UpdateStatus.AlDia; }), "2010 en la última: al día");
            var v2005 = outOk.Find(delegate(UpdateCandidate c) { return c.Name.Contains("2005"); });
            Check(v2005 != null && v2005.Status == UpdateStatus.Disponible && !v2005.Installable, "2005: disponible pero se actualiza a mano, como antes");
        }

        // ------------------------------------------------------------------ winget que se instala solo

        const string WingetReleaseJson = @"{""tag_name"":""v1.29.280"",""draft"":false,""prerelease"":false,""html_url"":""https://github.com/microsoft/winget-cli/releases/tag/v1.29.280"",""assets"":[
 {""name"":""DesktopAppInstaller_Dependencies.json"",""browser_download_url"":""https://github.com/microsoft/winget-cli/releases/download/v1.29.280/DesktopAppInstaller_Dependencies.json"",""size"":322,""digest"":""sha256:a56ddd79cf9cd056d9546cfeb6958c2b44d20f6221f8518bf17b003717d47a7a""},
 {""name"":""DesktopAppInstaller_Dependencies.zip"",""browser_download_url"":""https://github.com/microsoft/winget-cli/releases/download/v1.29.280/DesktopAppInstaller_Dependencies.zip"",""size"":97760717,""digest"":""sha256:3bbfcaa5cb011c48fac48d896d64a5c7c6898859a9f3d01555c8cd000f4e2962""},
 {""name"":""e53e159d00e04f729cc2180cffd1c02e_License1.xml"",""browser_download_url"":""https://github.com/microsoft/winget-cli/releases/download/v1.29.280/e53e159d00e04f729cc2180cffd1c02e_License1.xml"",""size"":2686,""digest"":""sha256:bcb15118ec47df24e3e6013a7006147c3a15b3a8104ed660fe87c8b4ed01f485""},
 {""name"":""Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle"",""browser_download_url"":""https://github.com/microsoft/winget-cli/releases/download/v1.29.280/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle"",""size"":216775738,""digest"":""sha256:0809fa9f52e395d6e7de692331dce847ac991952675116bb4d8aae2ddcc20946""}
]}";

        static void TestWingetSeInstalaSolo171()
        {
            // --- Qué se baja: el paquete, las dependencias y la licencia del último lanzamiento, todos con su SHA-256.
            string why;
            var plan = WingetSetup.PlanFrom(GitHubRules.ParseRelease(WingetReleaseJson), out why);
            Check(plan != null, "lanzamiento v1.29.280 reconocido" + (why != null ? " (" + why + ")" : ""));
            if (plan != null)
            {
                Eq("1.29.280", plan.Version, "versión");
                Eq("0809fa9f52e395d6e7de692331dce847ac991952675116bb4d8aae2ddcc20946", plan.Bundle.Sha256, "SHA-256 del paquete");
                Eq(WingetSetup.DependenciesName, plan.Dependencies.Name, "dependencias");
                Check(plan.License != null && plan.License.Name.EndsWith("_License1.xml"), "licencia");
            }
            string sinDigest = WingetReleaseJson.Replace(@",""digest"":""sha256:0809fa9f52e395d6e7de692331dce847ac991952675116bb4d8aae2ddcc20946""", "");
            Check(WingetSetup.PlanFrom(GitHubRules.ParseRelease(sinDigest), out why) == null && why.Contains("SHA-256"), "sin el SHA-256 publicado no se instala nada");
            string sinPaquete = WingetReleaseJson.Replace("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle", "Otro.msixbundle");
            Check(WingetSetup.PlanFrom(GitHubRules.ParseRelease(sinPaquete), out why) == null, "sin el paquete oficial, nada");
            string otroDominio = WingetReleaseJson.Replace("https://github.com/microsoft/winget-cli/releases/download/v1.29.280/Microsoft.DesktopAppInstaller", "https://evil.example/Microsoft.DesktopAppInstaller");
            Check(WingetSetup.PlanFrom(GitHubRules.ParseRelease(otroDominio), out why) == null, "un archivo fuera de GitHub no se acepta");

            // --- Las dependencias de la arquitectura del equipo, VCLibs primero.
            var entries = new[]
            {
                "x64/Microsoft.UI.Xaml.2.8_8.2310.30001.0_x64.appx", "x64/Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64.appx",
                "x64/Microsoft.WindowsAppRuntime.1.8_8000.616.304.0_x64.msix", "x86/Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x86.appx",
                "arm64/Microsoft.UI.Xaml.2.8_8.2310.30001.0_arm64.appx", "x64/LEAME.txt"
            };
            var x64 = WingetSetup.DependencyEntries(entries, "x64");
            Eq(3, x64.Count, "tres dependencias x64");
            Check(x64.Count == 3 && x64[0].Contains("VCLibs"), "VCLibs primero");
            Check(!x64.Exists(delegate(string e) { return e.Contains("_x86") || e.Contains("arm64"); }), "nada de otras arquitecturas");
            var planas = WingetSetup.DependencyEntries(new[] { "Microsoft.VCLibs.140.00_14.0.33519.0_x86.appx", "Microsoft.VCLibs.140.00_14.0.33519.0_x64.appx" }, "x86");
            Check(planas.Count == 1 && planas[0].EndsWith("_x86.appx"), "sin carpetas: por el sufijo del nombre");
            Eq("x64", WingetSetup.ArchFolder("x64"), "x64");
            Eq("arm64", WingetSetup.ArchFolder("ARM64"), "ARM64");
            Eq(null, WingetSetup.ArchFolder("ia64"), "Itanium: no");

            // --- El guion de PowerShell.
            string script = WingetSetup.InstallScript(@"C:\Datos\O'Brien\winget.msixbundle", @"C:\Datos\lic.xml", new List<string> { @"C:\Datos\dep1.appx", @"C:\Datos\dep2.appx" });
            Check(script.Contains("'C:\\Datos\\O''Brien\\winget.msixbundle'"), "las comillas simples se duplican");
            Check(script.Contains("Add-AppxProvisionedPackage -Online") && script.Contains("-LicensePath 'C:\\Datos\\lic.xml'"), "para todo el equipo, con la licencia");
            Check(script.Contains("-DependencyPackagePath $d"), "con las dependencias");
            Check(script.IndexOf("dep1.appx", StringComparison.Ordinal) < script.IndexOf("Add-AppxProvisionedPackage", StringComparison.Ordinal), "las dependencias antes del paquete");
            Check(script.Contains("-2147009274"), "una versión más nueva ya instalada (0x80073D06) no es error");
            Check(!script.Contains("-EncodedCommand") && !script.Contains("Bypass"), "sin trucos que los antivirus marcan");
            Check(WingetSetup.InstallScript("b", null, new List<string>()).Contains("-SkipLicense"), "sin licencia: -SkipLicense");
            string err;
            Check(WingetSetup.ScriptSucceeded("DEP-OK\r\nDEP-OK\r\nPROV-ERR: x\r\nUSER-OK\r\n", out err), "instalado para el usuario: bien");
            Check(WingetSetup.ScriptSucceeded("PROV-OK\r\nUSER-ERR: y", out err), "instalado para el equipo: bien");
            Check(!WingetSetup.ScriptSucceeded("DEP-ERR: falta\r\nPROV-ERR: Acceso denegado\r\nUSER-ERR: 0x80073CF3 dependencia\r\n", out err) && err.Contains("0x80073CF3"), "falla: el motivo del paso del usuario");

            // --- Cuándo se intenta.
            var ctx = ContextoSimulado(true);
            ctx.Os.Build = 17763;
            Eq(null, WingetSetup.CannotInstall(ctx), "Windows 10 1809 LTSC como administrador: se instala");
            ctx.Os.Build = 17134;
            Check(WingetSetup.CannotInstall(ctx).Contains("1809"), "Windows 10 1803: no");
            ctx.Os.Build = 19044;
            ctx.IsAdmin = false;
            Check(WingetSetup.CannotInstall(ctx).Contains("administrador"), "sin administrador: dice cómo");
            ctx.IsAdmin = true;
            ctx.Options.InstallWinget = false;
            Check(WingetSetup.CannotInstall(ctx).Contains("desactivada"), "desactivado en Opciones");
            ctx.Options.InstallWinget = true;

            // --- El mensaje de winget ausente ya no manda sólo a instalarlo a mano.
            var ctxUser = ContextoSimulado(false);
            ctxUser.Os.Build = 19044;
            Check(WingetSetup.CannotInstall(ctxUser).Contains("Ejecutar como administrador"), "sin administrador: botón a usar");
        }

        // ------------------------------------------------------------------ Todo de una vez

        sealed class ProveedorPorEtapas : IUpdateProvider
        {
            public readonly HashSet<string> Instalados = new HashSet<string>();
            public readonly List<string> Llamadas = new List<string>();
            public int Busquedas;
            public string Name { get { return BrowserProvider.ProviderId; } }
            public string Unavailable(UpdateContext ctx) { return null; }
            static UpdateCandidate Fila(string name, string version, bool recommended)
            {
                var c = new UpdateCandidate();
                c.Kind = UpdateKind.Programa; c.Name = name; c.Identifier = "id." + name; c.AvailableVersion = version; c.InstalledVersion = "1";
                c.Status = UpdateStatus.Disponible; c.Installable = true; c.Recommended = recommended; c.ProviderName = BrowserProvider.ProviderId;
                return c;
            }
            public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes)
            {
                Busquedas++;
                if (!Instalados.Contains("A")) output.Add(Fila("A", "2", true));
                if (Instalados.Contains("A") && !Instalados.Contains("B")) output.Add(Fila("B", "3", true));   // aparece después de instalar A
                output.Add(Fila("D", "5", true));        // el usuario la dejó sin marcar a propósito
                output.Add(Fila("C", "4", false));       // no recomendada: nunca sola
                output.Add(Fila("E", "9", true));        // se «instala» pero sigue apareciendo: no se repite
            }
            public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed)
            {
                foreach (var c in items)
                {
                    Llamadas.Add(c.Name);
                    if (c.Name != "E") Instalados.Add(c.Name);
                    c.Status = UpdateStatus.Instalada; c.Result = "ok"; changed(c);
                }
            }
        }

        static void TestTodoDeUnaVez171()
        {
            // --- Qué entra en la vuelta siguiente.
            var a = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "a", ProviderName = "x", Status = UpdateStatus.Disponible, Installable = true, Recommended = true, AvailableVersion = "2" };
            var noRec = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "b", ProviderName = "x", Status = UpdateStatus.Disponible, Installable = true, Recommended = false };
            var drv = new UpdateCandidate { Kind = UpdateKind.Controlador, Name = "c", ProviderName = "x", Status = UpdateStatus.Disponible, Installable = true, Recommended = true };
            var oem = new UpdateCandidate { Kind = UpdateKind.Controlador, Name = "bios", ProviderName = OemToolsProvider.ProviderId, Status = UpdateStatus.Disponible, Installable = true, Recommended = true };
            var ign = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "i", ProviderName = "x", Status = UpdateStatus.Disponible, Installable = true, Recommended = true, IgnoredBy = new object() };
            var all = new List<UpdateCandidate> { a, noRec, drv, oem, ign };
            var next = UpdateManager.NextRound(all, new HashSet<UpdateKind> { UpdateKind.Programa }, new HashSet<string>(), new HashSet<string>());
            Check(next.Count == 1 && next[0] == a, "sólo lo recomendado, de los tipos elegidos, sin ignorados");
            next = UpdateManager.NextRound(all, new HashSet<UpdateKind> { UpdateKind.Programa, UpdateKind.Controlador }, new HashSet<string>(), new HashSet<string>());
            Check(next.Contains(drv) && !next.Contains(oem), "nunca la herramienta del fabricante del equipo (BIOS)");
            next = UpdateManager.NextRound(all, null, new HashSet<string> { UpdateManager.RoundKey(a) }, new HashSet<string>());
            Check(!next.Contains(a), "lo ya intentado no se repite");
            next = UpdateManager.NextRound(all, null, new HashSet<string>(), new HashSet<string> { UpdateManager.RoundKey(drv) });
            Check(!next.Contains(drv), "lo que el usuario dejó sin marcar no se instala");
            Check(UpdateManager.RoundKey(a) != UpdateManager.RoundKey(new UpdateCandidate { Name = "a", ProviderName = "x", AvailableVersion = "3" }), "otra versión ofrecida es otra actualización");

            // --- Una tanda completa: A destraba B; D quedó sin marcar; E «se instala» pero sigue apareciendo.
            var prov = new ProveedorPorEtapas();
            var man = new UpdateManager(new IUpdateProvider[] { prov });
            man.Ignored = new IgnoreList();
            man.HistoryPath = "";
            var ctx = ContextoSimulado(true);
            ctx.DriversLoaded = true;
            ctx.Os.Major = 6; ctx.Os.Minor = 1;
            var report = UpdateSession.Create(ctx);
            man.Scan(ctx, report);
            var filaA = report.Updates.Find(delegate(UpdateCandidate u) { return u.Name == "A"; });
            var filaE = report.Updates.Find(delegate(UpdateCandidate u) { return u.Name == "E"; });
            Check(filaA != null && filaE != null, "primera búsqueda: A y E");
            var vueltas = new List<int>();
            var hechas = man.InstallUntilDone(ctx, report, new List<UpdateCandidate> { filaA, filaE }, false, delegate(UpdateCandidate c) { }, delegate(int n) { vueltas.Add(n); });
            Eq("A,E,B", string.Join(",", prov.Llamadas.ToArray()), "A y E en la primera vuelta, B (que apareció recién) en la segunda; nada más");
            Check(!prov.Llamadas.Contains("D") && !prov.Llamadas.Contains("C"), "ni lo que el usuario dejó sin marcar ni lo no recomendado");
            Eq(3, hechas.Count, "el resumen cuenta las dos vueltas");
            Eq("1,2", string.Join(",", vueltas.ConvertAll(delegate(int n) { return n.ToString(); }).ToArray()), "dos búsquedas nuevas: la que encontró B y la que ya no encontró nada");
            Check(prov.Busquedas == 3, "tres búsquedas en total (" + prov.Busquedas + ")");

            // --- Con la opción apagada, una sola vuelta, como antes.
            var prov2 = new ProveedorPorEtapas();
            var man2 = new UpdateManager(new IUpdateProvider[] { prov2 });
            man2.Ignored = new IgnoreList();
            man2.HistoryPath = "";
            man2.RepeatUntilDone = false;
            var report2 = UpdateSession.Create(ctx);
            man2.Scan(ctx, report2);
            var a2 = report2.Updates.Find(delegate(UpdateCandidate u) { return u.Name == "A"; });
            man2.InstallUntilDone(ctx, report2, new List<UpdateCandidate> { a2 }, false, delegate(UpdateCandidate c) { }, null);
            Eq("A", string.Join(",", prov2.Llamadas.ToArray()), "sin repetir: sólo lo elegido");

            // --- Si en una vuelta no se instaló nada, no se busca de nuevo.
            var prov3 = new ProveedorPorEtapas();
            var man3 = new UpdateManager(new IUpdateProvider[] { prov3 });
            man3.Ignored = new IgnoreList();
            man3.HistoryPath = "";
            var report3 = UpdateSession.Create(ctx);
            man3.Scan(ctx, report3);
            var c3 = report3.Updates.Find(delegate(UpdateCandidate u) { return u.Name == "C"; });
            c3.Installable = false;     // nada instalable elegido
            man3.InstallUntilDone(ctx, report3, new List<UpdateCandidate> { c3 }, false, delegate(UpdateCandidate c) { }, null);
            Eq(1, prov3.Busquedas, "sin nada instalado no se repite la búsqueda");
        }

        // ------------------------------------------------------------------ Programas sin registro (accesos directos)

        /// <summary>Un .lnk mínimo según MS-SHLLINK: LinkInfo con la ruta local (ANSI, o Unicode con la ANSI vacía).</summary>
        static byte[] Lnk(string target, bool unicodeInfo, string relative)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            uint flags = 0x2u | 0x80u | (relative != null ? 0x8u : 0u);     // HasLinkInfo, IsUnicode, HasRelativePath
            w.Write(0x4C);
            w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
            w.Write(flags);
            w.Write(0x20);
            w.Write(new byte[24]);
            w.Write(0); w.Write(0); w.Write(1);
            w.Write((ushort)0); w.Write(new byte[10]);
            byte[] baseAnsi = unicodeInfo ? new byte[] { (byte)'?', 0 } : Encoding.Default.GetBytes(target + "\0");
            byte[] uni = Encoding.Unicode.GetBytes(target + "\0");
            int hdr = unicodeInfo ? 0x24 : 0x1C;
            int volOff = hdr, baseOff = volOff + 0x14, sufOff = baseOff + baseAnsi.Length, uBaseOff = sufOff + 1, uSufOff = uBaseOff + uni.Length;
            int size = unicodeInfo ? uSufOff + 2 : sufOff + 1;
            w.Write(size); w.Write(hdr); w.Write(1); w.Write(volOff); w.Write(baseOff); w.Write(0); w.Write(sufOff);
            if (unicodeInfo) { w.Write(uBaseOff); w.Write(uSufOff); }
            var volume = new byte[0x14]; volume[0] = 0x14;
            w.Write(volume); w.Write(baseAnsi); w.Write((byte)0);
            if (unicodeInfo) { w.Write(uni); w.Write((ushort)0); }
            if (relative != null) { w.Write((ushort)relative.Length); w.Write(Encoding.Unicode.GetBytes(relative)); }
            w.Write(0);
            return ms.ToArray();
        }

        static void TestProgramasSinRegistro171()
        {
            // --- Lectura de accesos directos.
            Eq(@"D:\Portables\VLC\vlc.exe", ShortcutPrograms.ParseTarget(Lnk(@"D:\Portables\VLC\vlc.exe", false, null), @"C:\x"), "ruta local ANSI");
            Eq(@"C:\Program Files\GRETECH\GomPlayer\GOM.exe", ShortcutPrograms.ParseTarget(Lnk(@"C:\Program Files\GRETECH\GomPlayer\GOM.exe", true, null), @"C:\x"), "ruta local Unicode");
            Eq(null, ShortcutPrograms.ParseTarget(new byte[] { 1, 2, 3 }, @"C:\x"), "basura: null, sin excepción");
            Eq(null, ShortcutPrograms.ParseTarget(null, @"C:\x"), "nada: null");
            var corto = Lnk(@"C:\a.exe", false, null);
            Array.Resize(ref corto, 0x60);
            Eq(null, ShortcutPrograms.ParseTarget(corto, @"C:\x"), "recortado: null, sin excepción");

            // --- Qué se agrega.
            var registrados = new List<InstalledProgram>
            {
                Vc("7-Zip 26.00 (x64)", "26.00", "x64"),
                Vc("AnyDesk", "ad 9.7.0", "x86"),
            };
            registrados[0].InstallLocation = @"C:\Program Files\7-Zip\";
            registrados[1].DisplayIcon = "\"C:\\Program Files (x86)\\AnyDesk\\AnyDesk.exe\",0";
            var generic = new HashSet<string> { @"c:\program files\", @"c:\program files (x86)\", @"c:\programdata\", @"c:\windows\", @"c:\" };
            var covered = ShortcutPrograms.CoveredFolders(registrados, generic);
            Check(covered.Contains(@"c:\program files\7-zip\") && covered.Contains(@"c:\program files (x86)\anydesk\"), "carpetas de los registrados");
            Check(!covered.Contains(@"c:\program files\"), "nunca Program Files entero");
            Func<string, ExeInfo> leer = delegate(string exe)
            {
                if (exe.EndsWith("vlc.exe")) return new ExeInfo { Product = "VLC media player", Description = "VLC media player", Company = "VideoLAN", Version = "3,0,18,0" };
                if (exe.EndsWith("GOM.exe")) return new ExeInfo { Product = "GOM Player", Description = "GOM Player", Company = "GOM & Company", Version = "2.3.98.5371" };
                if (exe.EndsWith("7zFM.exe")) return new ExeInfo { Product = "7-Zip", Company = "Igor Pavlov", Version = "26.00" };
                return new ExeInfo { Product = "Otro", Version = "1.0" };
            };
            var vlc = ShortcutPrograms.Candidate(@"D:\Portables\VLC\vlc.exe", covered, registrados, @"C:\Windows", leer);
            Check(vlc != null && vlc.Name == "VLC media player" && vlc.Version == "3.0.18.0" && vlc.Publisher == "VideoLAN", "VLC portable: se agrega con su versión (3,0,18,0 → 3.0.18.0)");
            Check(vlc != null && vlc.Unregistered && vlc.Scope == ShortcutPrograms.ScopeText, "marcado como sin desinstalador");
            var gom = ShortcutPrograms.Candidate(@"C:\Program Files\GRETECH\GomPlayer\GOM.exe", covered, registrados, @"C:\Windows", leer);
            Check(gom != null && gom.Name == "GOM Player" && gom.Version == "2.3.98.5371", "GOM Player sin entrada de desinstalación: se agrega");
            Eq(null, ShortcutPrograms.Candidate(@"C:\Program Files\7-Zip\7zFM.exe", covered, registrados, @"C:\Windows", leer), "7-Zip ya registrado (su carpeta): no se repite");
            Eq(null, ShortcutPrograms.Candidate(@"D:\Apps\7zip\7zFM.exe", new List<string>(), registrados, @"C:\Windows", leer), "mismo nombre que uno registrado: no se repite");
            Eq(null, ShortcutPrograms.Candidate(@"D:\Juego\unins000.exe", covered, registrados, @"C:\Windows", leer), "desinstalador: no");
            Eq(null, ShortcutPrograms.Candidate(@"D:\Juego\setup.exe", covered, registrados, @"C:\Windows", leer), "instalador: no");
            Eq(null, ShortcutPrograms.Candidate(@"C:\Windows\System32\notepad.exe", covered, registrados, @"C:\Windows", leer), "de Windows: no");
            Eq(null, ShortcutPrograms.Candidate(@"D:\doc\manual.pdf", covered, registrados, @"C:\Windows", leer), "un documento: no");
            Eq(null, ShortcutPrograms.Candidate(@"D:\x\nada.exe", covered, registrados, @"C:\Windows", delegate(string e) { return null; }), "ejecutable ilegible: no");

            Eq("3.0.21", ShortcutPrograms.CleanVersion("3.0.21"), "versión limpia");
            Eq("2.3.98.5371", ShortcutPrograms.CleanVersion("2.3.98.5371 (x64)"), "con texto al final");
            Eq("", ShortcutPrograms.CleanVersion("sin versión"), "sin número");
            Check(ShortcutPrograms.SameProgram("VLC media player", "VLC media player 3.0.21") && ShortcutPrograms.SameProgram("GOM Player", "GOM Player (x64)"), "mismo programa con versión en el nombre");
            Check(!ShortcutPrograms.SameProgram("GOM Player", "GOM Audio"), "otro programa del mismo fabricante");
            Eq(@"C:\Program Files (x86)\AnyDesk", ShortcutPrograms.FolderOfCommand("\"C:\\Program Files (x86)\\AnyDesk\\AnyDesk.exe\" --uninstall"), "carpeta de un comando entre comillas");
            Eq(@"C:\Juego", ShortcutPrograms.FolderOfCommand(@"C:\Juego\unins000.exe /SILENT"), "carpeta de un comando sin comillas");

            // --- De otra cuenta sin sesión abierta.
            var perfiles = new List<string> { @"C:\Users\Fede", @"C:\Users\Chicos" };
            Eq("Chicos", ShortcutPrograms.ProfileOwner(@"C:\Users\Chicos\AppData\Local\Programs\GOM\GOM.exe", perfiles, @"C:\Users\Fede"), "de la cuenta «Chicos»");
            Eq(null, ShortcutPrograms.ProfileOwner(@"C:\Users\Fede\AppData\Local\Programs\x\x.exe", perfiles, @"C:\Users\Fede"), "de la cuenta actual: no es de otro");
            Eq(null, ShortcutPrograms.ProfileOwner(@"D:\Portables\VLC\vlc.exe", perfiles, @"C:\Users\Fede"), "fuera de los perfiles");

            // --- La fila dice qué es y no se instala encima.
            var hint = ProgramHints.For(vlc);
            Check(hint != null && hint.Text.Contains("portable") && hint.Text.Contains(@"D:\Portables\VLC"), "la pista explica que es portable y dónde está");
            var otro = ShortcutPrograms.Candidate(@"C:\Users\Chicos\AppData\Local\Programs\GOM\GOM.exe", covered, registrados, @"C:\Windows", leer);
            otro.Scope = "Usuario Chicos (sin sesión abierta)";
            Check(ProgramHints.For(otro).Text.Contains("otra cuenta"), "de otra cuenta: cómo actualizarlo");
            var portableChrome = Vc("Google Chrome", "120.0", "x64"); portableChrome.Unregistered = true;
            Eq(0, BrowserCatalog.Detect(new List<InstalledProgram> { portableChrome }, "x64").Count, "un navegador portable no se actualiza con el instalador oficial");
            var ctx = ContextoSimulado(true, Vc("Microsoft Visual C++ 2012 Redistributable (x64) - 11.0.50727", "11.0.50727.1", "x64"));
            ctx.Programs[0].Unregistered = true;
            var outV = new List<UpdateCandidate>();
            new VendorProvider().Scan(ctx, outV, new List<string>());
            Eq(0, outV.Count, "ni con el instalador de un fabricante");

            // --- Sin winget, la fila de un programa portable dice lo que es (no «revisá el sitio»).
            var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = vlc.Name, Status = UpdateStatus.NoVerificable, ProviderName = "", Detail = "Sin winget…" };
            ProgramHints.Annotate(new List<UpdateCandidate> { c }, new List<InstalledProgram> { vlc });
            Check(c.Detail.Contains("portable"), "detalle de la fila");
        }

        // ------------------------------------------------------------------ Todos los controladores

        static DriverInfo Drv(string name, string cls, string provider, string version, string hwid)
        {
            var d = new DriverInfo();
            d.DeviceName = name; d.DeviceClass = cls; d.Provider = provider; d.Version = version; d.HardwareId = hwid; d.DeviceId = hwid + "\\1";
            d.InfName = "oem1.inf"; d.Manufacturer = ""; d.Signer = "";
            return d;
        }

        static void TestTodosLosControladores171()
        {
            var audio = Drv("Dispositivo de High Definition Audio", "MEDIA", "Microsoft", "10.0.19041.1", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0887&SUBSYS_10438445&REV_1003");
            var raiz = Drv("Puerto raíz PCI Express", "System", "Microsoft", "10.0.19041.1", @"PCI\VEN_8086&DEV_A33C&SUBSYS_86941043&REV_F0");
            var smbus = Drv("Controlador SM Bus", "System", "(Controladores de sistema estándar)", "10.0.19041.1", @"PCI\VEN_1022&DEV_790B&SUBSYS_7C371462&REV_61");
            var volumen = Drv("Volumen", "Volume", "Microsoft", "10.0.19041.1", @"STORAGE\Volume");
            var virtual1 = Drv("Adaptador virtual de Hyper-V", "Net", "Microsoft", "10.0.19041.1", @"PCI\VEN_1414&DEV_5353");
            var teclado = Drv("Dispositivo de teclado HID", "Keyboard", "Microsoft", "10.0.19041.1", @"HID\VID_046D&PID_C31C&MI_00");
            var acpi = Drv("Botón de encendido ACPI", "System", "Microsoft", "10.0.19041.1", @"ACPI\PNP0C0C");
            var intelAcpi = Drv("Intel Serial IO", "System", "Microsoft", "10.0.19041.1", @"ACPI\VEN_INT&DEV_33C3");
            var realtek = Drv("Realtek PCIe GbE", "Net", "Realtek", "10.68.815.2023", @"PCI\VEN_10EC&DEV_8168&SUBSYS_7C371462&REV_15");

            Check(CatalogRules.IsVendorHardware(audio) && CatalogRules.ShouldCheck(audio), "audio Realtek con el genérico de Windows: se busca el del fabricante");
            Check(CatalogRules.ShouldCheck(raiz) && CatalogRules.ShouldCheck(smbus), "chipset Intel y AMD con el genérico: se buscan");
            Check(CatalogRules.ShouldCheck(intelAcpi), "dispositivo ACPI de Intel: se busca");
            Check(!CatalogRules.ShouldCheck(volumen), "volumen: no");
            Check(!CatalogRules.ShouldCheck(virtual1), "hardware de Microsoft (Hyper-V): no");
            Check(!CatalogRules.ShouldCheck(teclado), "teclado HID genérico: no");
            Check(!CatalogRules.ShouldCheck(acpi), "dispositivo ACPI estándar (PNP0C0C): no");
            Check(CatalogRules.ShouldCheck(realtek), "los de terceros, como siempre");

            // --- El del fabricante para un dispositivo con el genérico: aunque el número sea menor que el de Windows.
            var entries = new List<CatalogEntry>
            {
                new CatalogEntry { Guid = "g1", Title = "Realtek Semiconductor Corp. - MEDIA - 6.0.9600.1", Products = "Windows 10, version 1903 and later, Servicing Drivers", Version = "6.0.9600.1", LastUpdated = new DateTime(2024, 3, 1), Generic = true },
                new CatalogEntry { Guid = "g2", Title = "Realtek Semiconductor Corp. - MEDIA - 6.0.9500.1", Products = "Windows 10, version 1903 and later, Servicing Drivers", Version = "6.0.9500.1", LastUpdated = new DateTime(2023, 3, 1), Generic = false },
                new CatalogEntry { Guid = "g3", Title = "Realtek - Firmware - 1.0", Products = "Windows 10, version 1903 and later, Servicing Drivers", Version = "1.0.0.1", Classification = "Firmware" },
                new CatalogEntry { Guid = "g4", Title = "Realtek - MEDIA - 6.0.1.1 (Windows 7)", Products = "Windows 7", Version = "6.0.1.1" },
            };
            var rep = CatalogRules.VendorReplacements(entries, audio, 19044, false);
            Check(rep.Count == 2 && rep[0].Guid == "g2", "primero el del identificador completo; sin firmware ni de otro Windows");
            Eq(0, CatalogRules.NewerCandidates(entries, audio, 19044, false).Count, "por número nunca aparecería (10.0.19041 > 6.0.9600): por eso la regla aparte");

            // --- Los incluidos con Windows ahora se listan.
            var row = WindowsUpdateProvider.InboxCandidate(volumen, "Windows Update", false, false);
            Eq(UpdateStatus.AlDia, row.Status, "incluido con Windows: al día");
            Check(row.Kind == UpdateKind.Controlador && !row.Installable && row.ProviderTag == volumen, "fila de controlador, con su dispositivo");
            Check(row.Detail.Contains("incluido con Windows"), "dice qué es");
            Eq(UpdateStatus.NoVerificable, WindowsUpdateProvider.InboxCandidate(volumen, "Windows Update", true, false).Status, "con la directiva que excluye controladores: no verificable");

            // La fila del catálogo (con el del fabricante) reemplaza a la de Windows Update del mismo dispositivo.
            var wu = WindowsUpdateProvider.InboxCandidate(audio, "Windows Update", false, false);
            var cat = new UpdateCandidate { Kind = UpdateKind.Controlador, Name = audio.DeviceName, ProviderName = CatalogDriverProvider.ProviderId, Status = UpdateStatus.Disponible, Installable = true };
            cat.ProviderTag = new CatalogPlan { Driver = audio };
            var lista = new List<UpdateCandidate> { wu, cat };
            UpdateManager.DeduplicateCatalogDrivers(lista);
            Check(lista.Count == 1 && lista[0] == cat, "una fila por dispositivo: la del catálogo");
        }
    }
}
