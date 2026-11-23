// Pruebas de la 2.0.29. Grabación y captura del usuario (23/11/2026, 12:21): «en Vigía dice 2 programas, el Actualizador sólo detecta 1 y
// encima da error». Vigía: Microsoft.MicrosoftOfficeHub (Microsoft.365Copilot) 19.2506.56051.0 → 19.2609.33020.0 y
// Microsoft.WindowsAppRuntime.1.6 1.6.6 → 1.6.9. El Actualizador: sólo Office Hub, con origen «winget (winget, todo el equipo)» y en rojo.
using System;
using System.Collections.Generic;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        /// <summary>winget como servicio necesita las bibliotecas de Visual C++: se toman del paquete de Microsoft que las trae.</summary>
        static void TestWingetComoServicioConVcLibs2029()
        {
            string pf = @"C:\Program Files";
            var appx = new List<AppxInfo>
            {
                new AppxInfo { Name = "Microsoft.VCLibs.140.00.UWPDesktop", Version = "14.0.30704.0", Architecture = "X64", InstallLocation = pf + @"\WindowsApps\Microsoft.VCLibs.140.00.UWPDesktop_14.0.30704.0_x64__8wekyb3d8bbwe" },
                new AppxInfo { Name = "Microsoft.VCLibs.140.00.UWPDesktop", Version = "14.0.33519.0", Architecture = "X64", InstallLocation = pf + @"\WindowsApps\Microsoft.VCLibs.140.00.UWPDesktop_14.0.33519.0_x64__8wekyb3d8bbwe\" },
                new AppxInfo { Name = "Microsoft.VCLibs.140.00.UWPDesktop", Version = "14.0.33728.0", Architecture = "X86", InstallLocation = pf + @"\WindowsApps\Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x86__8wekyb3d8bbwe" },
                new AppxInfo { Name = "Microsoft.VCLibs.140.00.UWPDesktop", Version = "14.0.99999.0", Architecture = "X64", InstallLocation = @"C:\Users\Pamela\AppData\Microsoft.VCLibs.140.00.UWPDesktop_14.0.99999.0_x64__8wekyb3d8bbwe" },
                new AppxInfo { Name = "Microsoft.VCLibs.140.00", Version = "14.0.40000.0", Architecture = "X64", InstallLocation = pf + @"\WindowsApps\Microsoft.VCLibs.140.00_14.0.40000.0_x64__8wekyb3d8bbwe" },
            };
            Eq(pf + @"\WindowsApps\Microsoft.VCLibs.140.00.UWPDesktop_14.0.33519.0_x64__8wekyb3d8bbwe", SystemWinget.VcLibsDir(appx, pf, "AMD64"),
               "la más nueva de x64, sólo dentro de WindowsApps (no la de otra carpeta, ni la de x86, ni la que no es «UWPDesktop»)");
            Eq(null, SystemWinget.VcLibsDir(new List<AppxInfo>(), pf, "x64"), "sin el paquete: nada");
            Check(!SystemWinget.IsSafeForCmd(@"C:\a&b") && !SystemWinget.IsSafeForCmd("C:\\a\"b") && !SystemWinget.IsSafeForCmd(@"C:\%x%") && SystemWinget.IsSafeForCmd(@"C:\Program Files\WindowsApps\x"),
                  "en el guion sólo van rutas sin caracteres especiales de cmd");
            string winget = pf + @"\WindowsApps\Microsoft.DesktopAppInstaller_1.29.70.0_x64__8wekyb3d8bbwe\winget.exe";
            string guion = SystemWinget.Script(winget, "upgrade", @"C:\t\salida.txt", @"C:\t\codigo.txt", SystemWinget.VcLibsDir(appx, pf, "x64"));
            Check(guion.Contains("set \"PATH=" + pf + @"\WindowsApps\Microsoft.VCLibs.140.00.UWPDesktop_14.0.33519.0_x64__8wekyb3d8bbwe;%PATH%" + "\""), "las bibliotecas primero en el PATH");
            Check(guion.Contains("cd /d \"" + pf + @"\WindowsApps\Microsoft.DesktopAppInstaller_1.29.70.0_x64__8wekyb3d8bbwe" + "\""), "y se ubica en la carpeta de winget");
            Check(guion.IndexOf("set \"PATH=", StringComparison.Ordinal) < guion.IndexOf("\"" + winget + "\"", StringComparison.Ordinal), "antes de ejecutar winget");
            Check(guion.Contains(">\"C:\\t\\codigo.txt.tmp\" echo %ERRORLEVEL%"), "el código de salida, como en la 2.0.23");
            Check(!SystemWinget.Script(winget, "upgrade", "o", "e", @"C:\malo&calc").Contains("calc"), "una ruta rara no entra al guion");

            Check(SystemWinget.Explain(new ProcResult { Started = true, ExitCode = unchecked((int)0xC0000135) }).Contains("Visual C++"), "0xC0000135: falta una biblioteca");
            Check(SystemWinget.Explain(new ProcResult { StartError = "no se pudo crear la tarea programada" }).Contains("tarea programada"), "no arrancó: el motivo");
        }

        /// <summary>Con «--scope machine» (sin winget como servicio), Windows App Runtime 1.6 de todo el equipo también aparece.</summary>
        static void TestWindowsAppRuntimeSinServicio2029()
        {
            var equipo = new List<WingetRow>
            {
                new WingetRow { Name = "Microsoft.WindowsAppRuntime.1.6", Id = "Microsoft.WindowsAppRuntime.1.6", Version = "1.6.6", Available = "1.6.9", Source = "winget" },
                new WingetRow { Name = "Microsoft.MicrosoftOfficeHub", Id = "Microsoft.365Copilot", Version = "19.2506.56051.0", Available = "19.2609.33020.0", Source = "winget" },
                new WingetRow { Name = "Google Chrome", Id = "Google.Chrome", Version = "142.0.7444.60", Available = "142.0.7444.135", Source = "winget" },
            };
            var usuario = new List<WingetRow>
            {
                new WingetRow { Name = "WindowsAppRuntime.1.6", Id = "Microsoft.WindowsAppRuntime.1.6", Version = "1.6.6", Source = "winget" },
                new WingetRow { Name = "Google Chrome", Id = "Google.Chrome", Version = "142.0.7444.60", Source = "winget" },
            };
            var upgradesUsuario = new List<WingetRow> { new WingetRow { Name = "Google Chrome", Id = "Google.Chrome", Version = "142.0.7444.60", Available = "142.0.7444.135", Source = "winget" } };
            var salida = new List<UpdateCandidate>();
            int n = WingetProvider.AddMachineRows(equipo, upgradesUsuario, usuario, salida, false);
            Eq(2, n, "las dos que lista Vigía (la 2.0.28 mostraba sólo Office Hub)");
            Check(salida.Exists(delegate(UpdateCandidate c) { return c.Identifier == "Microsoft.WindowsAppRuntime.1.6" && c.AvailableVersion == "1.6.9" && c.Recommended; }),
                  "Windows App Runtime 1.6 de todo el equipo, marcada");
            Check(!salida.Exists(delegate(UpdateCandidate c) { return c.Identifier == "Google.Chrome"; }), "un programa común sigue en una sola fila (la del usuario)");
        }

        /// <summary>Office Hub para todos los usuarios no tiene con qué actualizarse: no se cuenta como pendiente ni se reintenta en rojo.</summary>
        static void TestOfficeHubSinPaquete2029()
        {
            string error = "winget respondió: winget no tiene una actualización aplicable a lo instalado (alcance equipo/usuario, arquitectura, tecnología de instalación o versión de " +
                           "Windows distintos); y su manifiesto no publica un paquete MSIX para x64; tampoco un instalador que se pueda ejecutar en silencio para x64";
            Check(WingetProvider.IsStructuralFailure(error), "el error de la grabación: no hay con qué");
            Check(!WingetProvider.IsStructuralFailure("winget respondió: falló la descarga; al aprovisionarlo directamente, Windows respondió: 0x80073CF3"), "un error del momento no");
            MachineMemory.StoreForTests = new Dictionary<string, string>();
            try
            {
                var w = new WingetRow { Name = "Microsoft.MicrosoftOfficeHub", Id = "Microsoft.365Copilot", Version = "19.2506.56051.0", Available = "19.2609.33020.0", Source = "winget", MachineScope = true };
                var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Microsoft.MicrosoftOfficeHub" + WingetProvider.MachineSuffix, ProviderName = WingetProvider.ProviderId, Identifier = w.Id,
                                              ProviderTag = w, Status = UpdateStatus.Disponible, Installable = true, Recommended = true, InstalledVersion = w.Version, AvailableVersion = w.Available };
                MachineMemory.Put(MachineMemory.MachineFailureKey(w.Id, w.Available, w.SystemContext), error);
                Eq(1, WingetProvider.ApplyMachineFailures(new List<UpdateCandidate> { c }, null), "se aplica");
                Check(c.Status == UpdateStatus.NoVerificable && !c.Installable && !c.Recommended, "pasa a «sin verificar»: no cuenta como pendiente ni se marca");
                Check(c.Detail.Contains("No se puede actualizar para todos los usuarios") && c.Detail.Contains("Vigía") && c.Detail.Contains("Microsoft Store"),
                      "explica por qué, por qué Vigía la sigue listando y qué pasa con la de cada usuario: " + c.Detail);
            }
            finally { MachineMemory.StoreForTests = null; }
        }

        static void TestVersion2029()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.29") >= 0, "Actualizador 2.0.29 o posterior: " + AppInfo.Version);
        }
    }
}
