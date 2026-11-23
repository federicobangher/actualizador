// Pruebas de la 2.0.22. Captura del usuario (21/11/2026, pc7, con la 2.0.21): «WindowsAppRuntime.1.4/1.5/1.6 (para todos los
// usuarios) · Falló la instalación · winget no encontró el paquete instalado; y su manifiesto no publica un paquete MSIX para x64»,
// y Roblox «terminó (código 0) pero sigue la 0.740.0.7400927».
using System;
using System.Collections.Generic;
using System.IO;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        /// <summary>Windows App Runtime: como servicio tal cual se vio, y si no, su instalador oficial (no publica un .msix).</summary>
        static void TestWindowsAppRuntime2022()
        {
            string yaml = File.ReadAllText(Path.Combine(samples, "winget_manifest_windowsappruntime14.yaml"));
            var m = WingetManifest.Parse(yaml);
            string why;
            Check(WingetProvider.ProvisionableInstaller(m, "x64", out why) == null, "su manifiesto real no tiene un .msix (por eso fallaba la 2.0.21): " + why);
            var exe = WingetProvider.OfficialExeInstaller(m, "x64", out why);
            Check(exe != null && exe.Url.Contains("windowsappruntimeinstall-x64.exe") && exe.Switch("Silent") == "--quiet", "su instalador oficial, en silencio: " + (exe != null ? exe.Url : why));
            Check(WingetProvider.OfficialExeInstaller(m, "ARM64", out why).Url.Contains("arm64"), "ARM64: el suyo");

            var oldSys = SystemWinget.RunForTests;
            var oldManifest = WingetDirect.ManifestForTests;
            var oldExe = WingetProvider.OfficialExeForTests;
            var oldProv = WingetProvider.ProvisionForTests;
            try
            {
                WingetDirect.ManifestForTests = delegate(string u) { return u.EndsWith("/Microsoft/WindowsAppRuntime/1/4/1.4.7/Microsoft.WindowsAppRuntime.1.4.installer.yaml") ? yaml : null; };
                WingetProvider.ProvisionForTests = null;
                var w = new WingetRow { Name = "WindowsAppRuntime.1.4", Id = "Microsoft.WindowsAppRuntime.1.4", Version = "1.4.5", Available = "1.4.7", Source = "winget" };
                var output = new List<UpdateCandidate>();
                WingetProvider.AddMachineRows(new List<WingetRow> { w }, null, new List<WingetRow>(), output, true);
                var c = output[0];
                Check(c.Status == UpdateStatus.Disponible && c.Recommended, "tu usuario no la tiene: disponible y marcada (es un marco)");
                var llamadas = new List<string>();
                SystemWinget.RunForTests = delegate(string args)
                {
                    llamadas.Add(args);
                    if (args.StartsWith("upgrade")) return Salida(0x8A150014, "No se encontró ningún paquete instalado que coincida con los criterios de entrada.");
                    return Salida(0, TablaWinget(CabWinget2021, new[] { "WindowsAppRuntime.1.4", "Microsoft.WindowsAppRuntime.1.4", "1.4.5", "1.4.7", "winget" }));
                };
                var ejecutados = new List<string>();
                WingetProvider.OfficialExeForTests = delegate(string url, string a) { ejecutados.Add(url + " " + a); return 0; };
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(llamadas.Count >= 2 && !llamadas[0].Contains("--scope") && llamadas[1].Contains("--scope machine"), "primero tal cual se vio, después --scope machine: " + string.Join(" | ", llamadas.ToArray()));
                Check(ejecutados.Count == 1 && ejecutados[0] == "https://aka.ms/windowsappsdk/1.4/1.4.240802001/windowsappruntimeinstall-x64.exe --quiet",
                      "su instalador oficial, con el interruptor del manifiesto: " + string.Join(", ", ejecutados.ToArray()));
                Check(c.Status == UpdateStatus.Instalada && c.Result.Contains("instalador oficial") && c.Result.Contains("convive"),
                      "instalada, sin rojo; y si winget como servicio sigue viendo la vieja, se explica: " + c.Result);

                // Si el instalador oficial falla, se dice.
                output.Clear();
                WingetProvider.AddMachineRows(new List<WingetRow> { new WingetRow { Name = "WindowsAppRuntime.1.4", Id = "Microsoft.WindowsAppRuntime.1.4", Version = "1.4.5", Available = "1.4.7", Source = "winget" } },
                                              null, new List<WingetRow>(), output, true);
                c = output[0];
                WingetProvider.OfficialExeForTests = delegate(string url, string a) { return 1603; };
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("1603"), "el código del instalador oficial: " + c.Result);

                // Con tu usuario ya en la 1.4.7: al día (la 1.4.5 es una copia vieja que convive).
                output.Clear();
                int n = WingetProvider.AddMachineRows(new List<WingetRow> { new WingetRow { Name = "WindowsAppRuntime.1.4", Id = "Microsoft.WindowsAppRuntime.1.4", Version = "1.4.5", Available = "1.4.7", Source = "winget" } },
                                                      null, new List<WingetRow> { new WingetRow { Name = "WindowsAppRuntime.1.4", Id = "Microsoft.WindowsAppRuntime.1.4", Version = "1.4.7", Source = "winget" } }, output, true);
                // 2.0.24: igual se ofrece (es lo que cuenta Vigía); se actualiza como servicio.
                Check(n == 1 && output.Count == 1 && output[0].Status == UpdateStatus.Disponible && output[0].Recommended, "aunque tu usuario ya la tenga: disponible y marcada, como en Vigía");
            }
            finally { SystemWinget.RunForTests = oldSys; WingetDirect.ManifestForTests = oldManifest; WingetProvider.OfficialExeForTests = oldExe; WingetProvider.ProvisionForTests = oldProv; }
        }

        /// <summary>Roblox: todas sus carpetas, como administrador si está en Program Files, y con su ventana si en silencio no terminó.</summary>
        static void TestRobloxConVentana2022()
        {
            var p = new InstalledProgram { Name = "Roblox Player", DisplayIcon = @"C:\Program Files (x86)\Roblox\Versions\version-abc\RobloxPlayerLauncher.exe" };
            var roots = RobloxPlayer.Roots(p, @"C:\Users\Fede\AppData\Local", @"C:\Program Files", @"C:\Program Files (x86)");
            Check(roots.Count == 3 && roots[0] == @"C:\Program Files (x86)\Roblox" && roots[1] == @"C:\Users\Fede\AppData\Local\Roblox" && roots[2] == @"C:\Program Files\Roblox",
                  "la de su entrada, la del usuario y las de Program Files, sin repetir: " + string.Join(" | ", roots.ToArray()));
            Check(RobloxPlayer.IsMachineRoot(@"C:\Program Files (x86)\Roblox", @"C:\Program Files", @"C:\Program Files (x86)"), "en Program Files: como administrador");
            Check(!RobloxPlayer.IsMachineRoot(@"C:\Users\Fede\AppData\Local\Roblox", @"C:\Program Files", @"C:\Program Files (x86)"), "en la del usuario: como el usuario");

            var oldV = RobloxPlayer.VersionsForTests;
            var oldI = RobloxPlayer.InstallForTests;
            var oldW = RobloxPlayer.WaitForTests;
            var oldManifest = WingetDirect.ManifestForTests;
            try
            {
                string yaml = File.ReadAllText(Path.Combine(samples, "winget_manifest_roblox_0747.yaml"));
                WingetDirect.ManifestForTests = delegate(string u) { return u.EndsWith("/Roblox/Roblox/0.747/Roblox.Roblox.installer.yaml") ? yaml : null; };
                var versiones = new Dictionary<string, string> { { @"...\version-old\RobloxPlayerBeta.exe", "0, 740, 0, 7400927" } };
                RobloxPlayer.VersionsForTests = delegate(string root) { return new Dictionary<string, string>(versiones); };
                RobloxPlayer.WaitForTests = delegate { return null; };
                var intentos = new List<bool>();
                // En silencio no deja nada (la captura de pc7); con su ventana, sí.
                RobloxPlayer.InstallForTests = delegate(string url, bool visible)
                {
                    intentos.Add(visible);
                    if (visible) versiones[@"...\version-4ea8e85481e34f46\RobloxPlayerBeta.exe"] = "0, 747, 0, 7470829";
                    return 0;
                };
                var w = new WingetRow { Name = "Roblox Player", Id = "Roblox.Roblox", Version = "Unknown", Available = "0.747", Source = "winget" };
                var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Roblox Player", ProviderName = WingetProvider.ProviderId, ProviderTag = w, Status = UpdateStatus.Disponible };
                RobloxPlayer.Resolve(c, w, new List<InstalledProgram>());
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(intentos.Count == 2 && !intentos[0] && intentos[1], "primero en silencio, después con su ventana");
                Check(c.Status == UpdateStatus.Instalada && c.InstalledVersion == "0.747.0.7470829" && c.Result.Contains("con su ventana"), "instalada: " + c.Result);

                // Si ni así: la fila dice las dos cosas y qué hacer.
                versiones.Remove(@"...\version-4ea8e85481e34f46\RobloxPlayerBeta.exe");
                intentos.Clear();
                RobloxPlayer.InstallForTests = delegate(string url, bool visible) { intentos.Add(visible); return 0; };
                c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Roblox Player", ProviderName = WingetProvider.ProviderId, ProviderTag = w, Status = UpdateStatus.Disponible };
                RobloxPlayer.Resolve(c, w, new List<InstalledProgram>());
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(intentos.Count == 2 && c.Status == UpdateStatus.Fallida && c.Result.Contains("ni en silencio ni con su ventana") && c.Result.Contains("Abrí Roblox"), "lo dice: " + c.Result);
            }
            finally { RobloxPlayer.VersionsForTests = oldV; RobloxPlayer.InstallForTests = oldI; RobloxPlayer.WaitForTests = oldW; WingetDirect.ManifestForTests = oldManifest; }
        }

        static void TestVersion2022()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.22") >= 0, "Actualizador 2.0.22 o posterior: " + AppInfo.Version);
        }
    }
}
