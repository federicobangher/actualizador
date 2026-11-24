// Pruebas de la 2.0.42. Capturas del usuario (24/11/2026, 18:14 y 18:21): Outlook para todos los usuarios falla y en la búsqueda siguiente
// aparece en «Al día y sin verificar» aunque sigue desactualizada (Vigía la sigue listando).
using System;
using System.Collections.Generic;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        /// <summary>
        /// La captura de las 18:14: «Microsoft.OutlookForWindows (para todos los usuarios) 1.2026.922.300 → 1.2026.1026.100: Falló la instalación»; a las
        /// 18:21, otra búsqueda: «Disponibles (0)», Outlook en «Al día y sin verificar»; y Vigía la seguía listando.
        /// </summary>
        static void TestFallaNoPasaAAlDia2042()
        {
            // 1) La red de seguridad: una versión más nueva conocida nunca queda en «Al día y sin verificar».
            foreach (var st in new[] { UpdateStatus.NoVerificable, UpdateStatus.Error })
            {
                var c = new UpdateCandidate { Name = "X", Kind = UpdateKind.Programa, Status = st, InstalledVersion = "1.2026.922.300", AvailableVersion = "1.2026.1026.100" };
                Check(UpdateGroups.KnownOutdated(c) && UpdateGroups.ViewOf(c) == UpdateView.Pendientes && UpdateGroups.IsPending(c) && !UpdateGroups.ShouldPremark(c),
                      st + " con una versión más nueva: en «Disponibles», pendiente, sin marcarse sola");
            }
            var ignorada = new UpdateCandidate { Name = "X", Status = UpdateStatus.NoVerificable, InstalledVersion = "1.0", AvailableVersion = "2.0", IgnoredBy = "usuario" };
            Check(!UpdateGroups.KnownOutdated(ignorada), "lo que el usuario ignoró no vuelve");
            var sinComparar = new UpdateCandidate { Name = "X", Status = UpdateStatus.NoVerificable, InstalledVersion = "Desconocida", AvailableVersion = "2.0" };
            Check(!UpdateGroups.KnownOutdated(sinComparar), "sin la versión instalada no se puede decir que esté desactualizada");
            var alDia = new UpdateCandidate { Name = "X", Status = UpdateStatus.AlDia, InstalledVersion = "2.0", AvailableVersion = "2.0" };
            Check(UpdateGroups.Of(alDia) == UpdateGroup.AlDia, "lo que está al día sigue al día");

            // 2) Outlook para todos los usuarios: winget no puede y su manifiesto no trae un paquete; una cuenta ya tiene la 1.2026.1026.100.
            var oldManifest = WingetDirect.ManifestForTests;
            var oldProvision = WingetProvider.ProvisionForTests;
            var oldInstalled = WingetProvider.ProvisionInstalledForTests;
            MachineMemory.StoreForTests = new Dictionary<string, string>();
            try
            {
                string manifiesto = "PackageIdentifier: Microsoft.OutlookForWindows\nPackageVersion: 1.2026.1026.100\nInstallers:\n- Architecture: x64\n  InstallerType: exe\n  Scope: user\n" +
                                    "  InstallerUrl: https://res.cdn.office.net/nativehost/5mttl/installer/v2/indirect/Setup.exe\n  InstallerSha256: " + new string('A', 64) + "\n" +
                                    "  InstallerSwitches:\n    Silent: --quiet\nManifestType: installer\nManifestVersion: 1.6.0\n";
                WingetDirect.ManifestForTests = delegate(string u) { return u.EndsWith("/Microsoft/OutlookForWindows/1.2026.1026.100/Microsoft.OutlookForWindows.installer.yaml") ? manifiesto : null; };
                WingetProvider.ProvisionForTests = delegate(string u) { return Salida(0, "PROV-ERR: no debería llegar acá"); };
                Func<UpdateCandidate> fila = delegate
                {
                    var w = new WingetRow { Name = "Microsoft.OutlookForWindows", Id = "Microsoft.OutlookForWindows", Version = "1.2026.922.300", Available = "1.2026.1026.100", Source = "winget" };
                    var salida = new List<UpdateCandidate>();
                    WingetProvider.AddMachineRows(new List<WingetRow> { w }, null, new List<WingetRow>(), salida, false);
                    return salida.Count == 1 ? salida[0] : null;
                };
                string provisioned = "1.2026.922.300";
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.RunForTests = delegate(string args) { return Salida(0x8A15002B, "No se encontró ninguna actualización aplicable."); };
                wp.ListForTests = delegate(string args)
                {
                    return Salida(0, "Nombre Id Versión Origen\r\n---------------------------------------------------------------------------\r\n" +
                                     "Microsoft.OutlookForWindows Microsoft.OutlookForWindows " + provisioned + " winget\r\n");
                };
                var guiones = new List<string>();
                WingetProvider.ProvisionInstalledForTests = delegate(string ps) { guiones.Add(ps); provisioned = "1.2026.1026.100"; return Salida(0, "PROV-OK 1.2026.1026.100"); };
                var outlook = fila();
                Check(outlook != null, "la fila de Outlook para todos los usuarios");
                if (outlook == null) return;
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { outlook }, delegate(UpdateCandidate x) { });
                Check(outlook.Status == UpdateStatus.Instalada && outlook.Result.Contains("ya tenía instalada una cuenta"), "se aprovisiona desde la copia de la cuenta: " + outlook.Result);
                Check(guiones.Count == 1 && guiones[0].Contains("Get-AppxPackage -AllUsers -Name $n") && guiones[0].Contains("'Microsoft.OutlookForWindows'") &&
                      guiones[0].Contains("ProvisionPackageForAllUsersAsync($p.PackageFamilyName)") && guiones[0].Contains("[version]'1.2026.1026.100'"), "el guion: " + (guiones.Count > 0 ? guiones[0] : ""));

                // 3) Ninguna cuenta la tiene: falla con el motivo; en la búsqueda siguiente sigue en «Disponibles» (no en «Al día»).
                provisioned = "1.2026.922.300";
                WingetProvider.ProvisionInstalledForTests = delegate(string ps) { return Salida(0, "PROV-ERR: ninguna cuenta del equipo tiene instalada la 1.2026.1026.100 para aprovisionarla"); };
                outlook = fila();
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { outlook }, delegate(UpdateCandidate x) { });
                Check(outlook.Status == UpdateStatus.Fallida && outlook.Result.Contains("ninguna cuenta del equipo"), "falla diciendo por qué: " + outlook.Result);
                var otra = fila();
                Eq(1, WingetProvider.ApplyMachineFailures(new List<UpdateCandidate> { otra }, null), "la búsqueda siguiente recuerda la falla");
                Check(otra.Status == UpdateStatus.Disponible && UpdateGroups.ViewOf(otra) == UpdateView.Pendientes && UpdateGroups.IsPending(otra) && !UpdateGroups.MarkedByMarkAll(otra),
                      "y Outlook sigue en «Disponibles», contada como pendiente (como en Vigía), sin marcarse sola: " + otra.Detail);
                Check(otra.Detail.StartsWith("Sigue desactualizada"), "lo dice: " + otra.Detail);
            }
            finally
            {
                WingetDirect.ManifestForTests = oldManifest; WingetProvider.ProvisionForTests = oldProvision; WingetProvider.ProvisionInstalledForTests = oldInstalled;
                MachineMemory.StoreForTests = null;
            }

            // 4) Los nombres de paquete y el guion no aceptan nada raro.
            var nombres = WingetProvider.PackageNamesOf(new WingetRow { Name = "Microsoft.MicrosoftOfficeHub", Id = "Microsoft.365Copilot" });
            Eq("Microsoft.MicrosoftOfficeHub,Microsoft.365Copilot", string.Join(",", nombres.ToArray()), "el nombre del paquete y el identificador");
            Eq(0, WingetProvider.PackageNamesOf(new WingetRow { Name = "Outlook'; calc", Id = "x" }).Count, "nada con comillas ni espacios");
            bool rechazo = false;
            try { WingetProvider.ProvisionFromInstalledScript(new List<string> { "Microsoft.Outlook" }, "1.0'; calc; '"); } catch (ArgumentException) { rechazo = true; }
            Check(rechazo, "una versión rara no entra al guion");
        }

        /// <summary>
        /// Grabación del usuario (24/11/2026, 18:43), con la 2.0.42: «Hay 1 actualización disponible» y en «Disponibles» una sola fila,
        /// «Microsoft Visual C++ 2012 Redistributable (x64) - 11.0.50727», 11.0.50727.1 → 11.0.61030, estado «Al día», sin casilla.
        /// </summary>
        static void TestRuntimeQueConviveSigueAlDia2043()
        {
            var vc = new UpdateCandidate { Name = "Microsoft Visual C++ 2012 Redistributable (x64) - 11.0.50727", Kind = UpdateKind.Programa, Status = UpdateStatus.AlDia,
                                           InstalledVersion = "11.0.50727.1", AvailableVersion = "11.0.61030", ProviderName = VendorProvider.ProviderId,
                                           Detail = "Convive con «Microsoft Visual C++ 2012 Redistributable (x64) - 11.0.61030» (11.0.61030.0), la última versión, que es la que usan los programas: esta entrada queda por compatibilidad." };
            Check(!UpdateGroups.KnownOutdated(vc), "el veredicto «Al día» de quien compara se respeta");
            Eq(UpdateGroup.AlDia, UpdateGroups.Of(vc), "va a «Al día y sin verificar»");
            Check(!UpdateGroups.IsPending(vc) && UpdateGroups.ViewOf(vc) == UpdateView.Revisados, "no cuenta como pendiente");
            var k = UpdateGroups.Count(new List<UpdateCandidate> { vc });
            Eq(0, k.ToUpdate + k.Optional, "«Hay 0 actualizaciones», no «Hay 1»");
        }

        static void TestVersion2043()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.43") >= 0, "Actualizador 2.0.43 o posterior: " + AppInfo.Version);
        }

        static void TestVersion2042()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.42") >= 0, "Actualizador 2.0.42 o posterior: " + AppInfo.Version);
        }
    }
}
