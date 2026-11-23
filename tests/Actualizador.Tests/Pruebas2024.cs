// Pruebas de la 2.0.24. Capturas del usuario (21/11/2026) en pc2, pc3 y pc4: «en Vigía sale una cosa y en el Actualizador otra».
// La regla: todo lo que lista el agente de Vigía (winget como servicio) aparece en el Actualizador, con el mismo nombre, marcado.
using System;
using System.Collections.Generic;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static List<WingetRow> Filas2024(params string[][] filas)
        {
            var l = new List<WingetRow>();
            foreach (var f in filas) l.Add(new WingetRow { Name = f[0], Id = f[1], Version = f[2], Available = f.Length > 3 ? f[3] : "", Source = "winget" });
            return l;
        }

        /// <summary>pc4: Vigía lista 5 (Outlook, VCLibs.14, Windows App Runtime 1.5 y 1.6, Teams); la 2.0.23 mostraba 2 y como opcionales.</summary>
        static void TestMismaListaQueVigiaPc4_2024()
        {
            var vigia = Filas2024(
                new[] { "Microsoft.OutlookForWindows", "Microsoft.Outlook", "1.2025.219.400", "1.2026.812.100" },
                new[] { "Microsoft.VCLibs.140.00", "Microsoft.VCLibs.14", "14.0.30704.0", "14.0.33519.0" },
                new[] { "Microsoft.WindowsAppRuntime.1.5", "Microsoft.WindowsAppRuntime.1.5", "1.5.8", "1.5.9" },
                new[] { "Microsoft.WindowsAppRuntime.1.6", "Microsoft.WindowsAppRuntime.1.6", "1.6.8", "1.6.9" },
                new[] { "MSTeams", "Microsoft.Teams", "25044.2208.3471.2155", "26288.2004.5269.3420" });
            // Tu usuario: los marcos en varias versiones a la vez (también la vieja), y Windows App Runtime 1.6 con su propia actualización.
            var usuario = Filas2024(
                new[] { "Microsoft Visual C++ 2015 UWP Runtime Package", "Microsoft.VCLibs.14", "14.0.33519.0" },
                new[] { "Microsoft Visual C++ 2015 UWP Runtime Package", "Microsoft.VCLibs.14", "14.0.30704.0" },
                new[] { "WindowsAppRuntime.1.5", "Microsoft.WindowsAppRuntime.1.5", "1.5.9" },
                new[] { "WindowsAppRuntime.1.6", "Microsoft.WindowsAppRuntime.1.6", "1.6.8" });
            var upgradesUsuario = Filas2024(new[] { "WindowsAppRuntime.1.6", "Microsoft.WindowsAppRuntime.1.6", "1.6.8", "1.6.9" });
            var output = new List<UpdateCandidate>();
            int n = WingetProvider.AddMachineRows(vigia, upgradesUsuario, usuario, output, true);
            Eq(5, n, "las cinco de Vigía");
            var nombres = new List<string>();
            foreach (var c in output)
            {
                nombres.Add(c.Name);
                Check(c.Status == UpdateStatus.Disponible && c.Installable && c.Recommended, c.Name + ": disponible y marcada (la 2.0.23 dejaba Outlook y Teams como opcionales)");
                Check(((WingetRow)c.ProviderTag).SystemContext, c.Name + ": se actualiza como servicio, donde la ve Vigía");
            }
            foreach (string v in new[] { "Microsoft.OutlookForWindows", "Microsoft.VCLibs.140.00", "Microsoft.WindowsAppRuntime.1.5", "Microsoft.WindowsAppRuntime.1.6", "MSTeams" })
                Check(nombres.Contains(v + WingetProvider.MachineSuffix), "con el mismo nombre que Vigía: " + v);
            var war16 = output.Find(delegate(UpdateCandidate c) { return c.Identifier == "Microsoft.WindowsAppRuntime.1.6"; });
            Check(war16 != null && war16.InstalledVersion == "1.6.8" && war16.AvailableVersion == "1.6.9",
                  "Windows App Runtime 1.6 aparece aunque tu usuario también la tenga para actualizar (son dos copias distintas)");
        }

        /// <summary>Un programa común (del registro) es una sola instalación: no se repite con la fila del servicio.</summary>
        static void TestProgramaComunUnaVez2024()
        {
            var servicio = Filas2024(new[] { "Microsoft Edge", "Microsoft.Edge", "142.0.3595.65", "142.0.3595.80" },
                                     new[] { "Google Chrome", "Google.Chrome", "142.0.7444.60", "142.0.7444.135" });
            var usuario = Filas2024(new[] { "Microsoft Edge", "Microsoft.Edge", "142.0.3595.65" }, new[] { "Google Chrome", "Google.Chrome", "142.0.7444.60" });
            var upgrades = Filas2024(new[] { "Microsoft Edge", "Microsoft.Edge", "142.0.3595.65", "142.0.3595.80" }, new[] { "Google Chrome", "Google.Chrome", "142.0.7444.60", "142.0.7444.135" });
            var output = new List<UpdateCandidate>();
            Eq(0, WingetProvider.AddMachineRows(servicio, upgrades, usuario, output, true), "Edge y Chrome: una sola fila, la de tu usuario (es la misma instalación)");
            Check(!WingetProvider.IsPerAccountPackage(new WingetRow { Name = "Microsoft Edge", Id = "Microsoft.Edge" }), "nombre con espacios: programa del registro");
            Check(WingetProvider.IsPerAccountPackage(new WingetRow { Name = "MSTeams", Id = "Microsoft.Teams" }) &&
                  WingetProvider.IsPerAccountPackage(new WingetRow { Name = "Microsoft.VCLibs.140.00.UWPDesktop", Id = "Microsoft.VCLibs.Desktop.14" }), "nombre de paquete: una copia por cuenta");
            Check(!WingetProvider.IsPerAccountPackage(new WingetRow { Name = "Node.js", Id = "OpenJS.NodeJS" }), "sólo los de Microsoft");
        }

        static void TestVersion2024()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.24") >= 0, "Actualizador 2.0.24 o posterior: " + AppInfo.Version);
        }
    }
}
