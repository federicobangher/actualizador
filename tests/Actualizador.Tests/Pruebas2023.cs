// Pruebas de la 2.0.23 (auditoría completa del 21/11/2026).
using System;
using System.Collections.Generic;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        /// <summary>
        /// El guion de winget como servicio guardaba su código con «echo %ERRORLEVEL%> archivo»: con 0 (winget terminó bien) eso es
        /// «echo 0> archivo», que cmd toma como redirección de la entrada, y el código se leía -1 (falla). Cada línea del guion se
        /// revisa como la leería cmd: nada de un dígito pegado a «>».
        /// </summary>
        static void TestCodigoDeSalidaComoServicio2023()
        {
            string s = SystemWinget.Script(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\winget.exe", "list --id \"X.Y\" --exact",
                                           @"C:\Windows\Temp\A\salida.txt", @"C:\Windows\Temp\A\codigo.txt");
            foreach (string line in s.Split('\n'))
            {
                string l = line.Trim();
                // Lo que cmd vería con los códigos posibles (0 a 9 son los peligrosos).
                for (int code = 0; code <= 9; code++)
                {
                    string expanded = l.Replace("%ERRORLEVEL%", code.ToString());
                    Check(!System.Text.RegularExpressions.Regex.IsMatch(expanded, @"\s\d>\s*""?[A-Za-z]:\\"), "sin «" + code + ">» que cmd lea como redirección: " + expanded);
                }
            }
            Eq(0, SystemWinget.ParseExit("0\r\n"), "«0» se lee 0 (winget terminó bien)");
        }

        /// <summary>Si winget como servicio terminó bien, está actualizada (aunque todavía liste la copia vieja): no se reinstala por otra vía.</summary>
        static void TestServicioTerminoBien2023()
        {
            var oldSys = SystemWinget.RunForTests;
            var oldExe = WingetProvider.OfficialExeForTests;
            var oldProv = WingetProvider.ProvisionForTests;
            try
            {
                var w = new WingetRow { Name = "PDF24 Creator 11.34.0", Id = "geeksoftwareGmbH.PDF24Creator", Version = "11.34.0", Available = "11.35.0", Source = "winget" };
                var output = new List<UpdateCandidate>();
                WingetProvider.AddMachineRows(new List<WingetRow> { w }, null, null, output, true);
                var c = output[0];
                int directas = 0;
                WingetProvider.OfficialExeForTests = delegate(string u, string a) { directas++; return 0; };
                WingetProvider.ProvisionForTests = delegate(string u) { directas++; return Salida(0, "PROV-OK"); };
                SystemWinget.RunForTests = delegate(string args)
                {
                    if (args.StartsWith("upgrade")) return Salida(0, "Instalado correctamente");
                    return Salida(0, TablaWinget(CabWinget2021, new[] { "PDF24 Creator 11.34.0", "geeksoftwareGmbH.PDF24Creator", "11.34.0", "", "winget" }));
                };
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Instalada && directas == 0 && c.Result.Contains("todavía lista también la 11.34.0"),
                      "winget terminó bien: instalada, sin otra vía, y dice qué ve todavía: " + c.Result);
                // Reiniciar para terminar (0x8A150109) también es un éxito.
                output.Clear();
                WingetProvider.AddMachineRows(new List<WingetRow> { new WingetRow { Name = "X", Id = "Editor.X", Version = "1.0", Available = "2.0", Source = "winget" } }, null, null, output, true);
                c = output[0];
                SystemWinget.RunForTests = delegate(string args) { return args.StartsWith("upgrade") ? Salida(WingetProvider.RebootToFinish, "") : Salida(0, ""); };
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.RequiereReinicio && directas == 0, "falta reiniciar: actualizada, requiere reinicio (" + c.Result + ")");
            }
            finally { SystemWinget.RunForTests = oldSys; WingetProvider.OfficialExeForTests = oldExe; WingetProvider.ProvisionForTests = oldProv; }
        }

        static void TestVersion2023()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.23") >= 0, "Actualizador 2.0.23 o posterior: " + AppInfo.Version);
        }
    }
}
