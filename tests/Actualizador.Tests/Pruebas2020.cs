// Pruebas de la 2.0.20. Capturas y diagnóstico del usuario (21/11/2026, con la 2.0.19):
//  - el agente de Vigía (winget como servicio) lista VCLibs, Windows App Runtime 1.4/1.5/1.6 y Teams con versión nueva, y el
//    Actualizador no los muestra;
//  - «Roblox Player · Desconocida → 0.747 · Falló la instalación · winget no tiene una actualización aplicable»;
//  - en los registros del equipo: WMI fallaba SIEMPRE con 0x80020005 (los controladores no se revisaban), Claude «no cambió nada»
//    estando ya en la última, Ollama abría su página, y Windows Update decía «Descarga de hasta 90,5 GB».
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        // ------------------------------------------------------------------ WMI

        /// <summary>Los parámetros omitidos van como «omitido» (VT_ERROR), no como null (VT_EMPTY), y si COM falla se usa PowerShell.</summary>
        static void TestWmiSinTiposQueNoCoinciden2020()
        {
            Check(ReferenceEquals(Wmi.Omitted, System.Reflection.Missing.Value), "los parámetros opcionales de WMI se omiten con Missing.Value (antes null → 0x80020005)");
            var oldQ = Wmi.QueryForTests;
            Wmi.ResetComStateForTests();
            var oldPs = Wmi.PowerShellForTests;
            try
            {
                // La consulta de los controladores, como la hace el inventario.
                const string wql = "SELECT DeviceName, DeviceClass, Manufacturer, DriverProviderName, DriverVersion, DriverDate, InfName, HardWareID, DeviceID, IsSigned, Signer FROM Win32_PnPSignedDriver";
                List<string> props;
                Check(Wmi.TryParseSelect(wql, out props) && props.Count == 11 && props[0] == "DeviceName" && props[10] == "Signer", "se leen las propiedades pedidas");
                Check(!Wmi.TryParseSelect("SELECT * FROM Win32_Process", out props), "«SELECT *» no va por PowerShell (no se sabría qué pedir)");
                Check(!Wmi.TryParseSelect("DELETE FROM X", out props), "sólo consultas SELECT");
                string script = Wmi.PowerShellScript(@"root\cimv2", "SELECT DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceID='PCI\\VEN_10DE&DEV_1288'", new List<string> { "DriverVersion" });
                Check(script.Contains("-Namespace 'root\\cimv2'") && script.Contains("-Query 'SELECT DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceID=''PCI\\VEN_10DE&DEV_1288'''"),
                      "la consulta va entre comillas simples de PowerShell, con las comillas duplicadas: " + script);
                Check(script.Contains("ToString('yyyyMMddHHmmss.ffffff')+'+000'"), "las fechas, en el formato de WMI");

                int psCalls = 0;
                Wmi.QueryForTests = delegate(string ns, string q) { throw new COMException("Los tipos no coinciden.", unchecked((int)0x80020005)); };
                Wmi.PowerShellForTests = delegate(string ns, string q)
                {
                    psCalls++;
                    return "[{\"DeviceName\":\"NVIDIA GeForce GT 720\",\"DeviceClass\":\"DISPLAY\",\"DriverVersion\":\"30.0.14.7514\",\"DriverDate\":\"20210908000000.000000+000\"," +
                           "\"HardWareID\":[\"PCI\\\\VEN_10DE&DEV_1288&SUBSYS_00000000\",\"PCI\\\\VEN_10DE&DEV_1288\"],\"IsSigned\":true,\"Signer\":\"Microsoft Windows Hardware Compatibility Publisher\"}," +
                           "{\"DeviceName\":\"Realtek PCIe GbE Family Controller\",\"DeviceClass\":\"NET\",\"DriverVersion\":\"10.68.815.2023\",\"HardWareID\":null,\"IsSigned\":true}]";
                };
                string err;
                var rows = Wmi.TryQuery(Wmi.Cimv2, wql, out err, 180);
                Check(err == null, "con WMI por COM fallando (0x80020005), la consulta igual sale por PowerShell: " + err);
                Eq(2, rows.Count, "los dos controladores");
                Eq(1, psCalls, "una sola consulta por PowerShell");
                if (rows.Count == 2)
                {
                    Eq("NVIDIA GeForce GT 720", Wmi.S(rows[0], "DeviceName"), "el nombre");
                    Eq("30.0.14.7514", Wmi.S(rows[0], "DriverVersion"), "la versión");
                    Check(Wmi.D(rows[0], "DriverDate").HasValue && Wmi.D(rows[0], "DriverDate").Value.Year == 2021, "la fecha, como la de WMI");
                    Eq(2, Wmi.Arr(rows[0], "HardWareID").Length, "los identificadores de hardware, como arreglo (igual que por COM)");
                    Check(Wmi.B(rows[0], "IsSigned") == true, "verdadero/falso");
                    Check(Wmi.S(rows[1], "HardWareID") == null, "un valor nulo queda nulo");
                }

                // WMI colgado (tiempo agotado): no se repite por PowerShell (tardaría lo mismo).
                psCalls = 0;
                Wmi.QueryForTests = delegate(string ns, string q) { throw new TimeoutException("WMI no respondió"); };
                rows = Wmi.TryQuery(Wmi.Cimv2, "SELECT Manufacturer FROM Win32_ComputerSystem", out err, 30);
                Check(rows.Count == 0 && err != null && psCalls == 0, "WMI colgado: sin PowerShell, con el motivo (" + err + ")");
                // La clase no existe en este Windows: tampoco.
                Wmi.QueryForTests = delegate(string ns, string q) { throw new COMException("Clase no válida", unchecked((int)0x80041010)); };
                rows = Wmi.TryQuery(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct", out err, 30);
                Check(rows.Count == 0 && err != null && err.Contains("no existe") && psCalls == 0, "clase inexistente: sin PowerShell (" + err + ")");
                // Si PowerShell también falla, el motivo dice las dos cosas.
                Wmi.QueryForTests = delegate(string ns, string q) { throw new COMException("Los tipos no coinciden.", unchecked((int)0x80020005)); };
                Wmi.PowerShellForTests = delegate(string ns, string q) { return "Get-CimInstance : Acceso denegado"; };
                rows = Wmi.TryQuery(Wmi.Cimv2, "SELECT Manufacturer FROM Win32_ComputerSystem", out err, 30);
                Check(rows.Count == 0 && err != null && err.Contains("0x80020005") && err.Contains("tampoco con PowerShell"), "las dos vías fallan: se dice (" + err + ")");
                // Un solo objeto (PowerShell no lo pone entre corchetes).
                Wmi.PowerShellForTests = delegate(string ns, string q) { return "{\"Manufacturer\":\"Dell Inc.\"}"; };
                rows = Wmi.TryQuery(Wmi.Cimv2, "SELECT Manufacturer FROM Win32_ComputerSystem", out err, 30);
                Check(rows.Count == 1 && Wmi.S(rows[0], "Manufacturer") == "Dell Inc.", "un solo objeto también se lee (fabricante del equipo: «Dell Inc.»)");
                // Sin resultados: lista vacía y sin error.
                Wmi.PowerShellForTests = delegate(string ns, string q) { return "[]"; };
                rows = Wmi.TryQuery(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct", out err, 30);
                Check(rows.Count == 0 && err == null, "sin filas: vacío y sin error");
            }
            finally { Wmi.QueryForTests = oldQ; Wmi.PowerShellForTests = oldPs; Wmi.ResetComStateForTests(); }
        }

        // ------------------------------------------------------------------ Apps para todos los usuarios

        static List<WingetRow> UsuarioInstalado2020()
        {
            var list = new List<WingetRow>();
            Action<string, string, string> add = delegate(string n, string id, string v) { list.Add(new WingetRow { Name = n, Id = id, Version = v, Source = "winget" }); };
            add("Microsoft Visual C++ 2015 UWP Desktop Runtime Package", "Microsoft.VCLibs.Desktop.14", "14.0.33728.0");
            add("WindowsAppRuntime.1.4", "Microsoft.WindowsAppRuntime.1.4", "1.4.7");
            add("WindowsAppRuntime.1.5", "Microsoft.WindowsAppRuntime.1.5", "1.5.9");
            add("WindowsAppRuntime.1.6", "Microsoft.WindowsAppRuntime.1.6", "1.6.9");
            add("7-Zip 24.08 (x64)", "7zip.7zip", "24.08");
            add("PDF24 Creator 11.34.0", "geeksoftwareGmbH.PDF24Creator", "11.34.0");
            return list;
        }

        /// <summary>La captura de Vigía: los cinco paquetes aparecen, con su nombre y si se marcan solos.</summary>
        static void TestAppsParaTodosLosUsuarios2020()
        {
            var machine = WingetTable.Parse(File.ReadAllText(Path.Combine(samples, "winget_upgrade_equipo.txt")));
            Eq(7, machine.Count, "la tabla de «winget upgrade --scope machine» (5 paquetes aprovisionados + 2 programas del equipo)");
            var userUpgrades = new List<WingetRow>
            {
                new WingetRow { Name = "PDF24 Creator 11.34.0", Id = "geeksoftwareGmbH.PDF24Creator", Version = "11.34.0", Available = "11.35.0", Source = "winget" },
                new WingetRow { Name = "7-Zip 24.08 (x64)", Id = "7zip.7zip", Version = "24.08", Available = "26.05", Source = "winget" },
            };
            var output = new List<UpdateCandidate>();
            // 2.0.22: con el usuario ya en las versiones nuevas de los marcos, esas copias viejas quedan «Al día» (conviven y no se usan).
            int conUsuario = WingetProvider.AddMachineRows(WingetTable.Parse(File.ReadAllText(Path.Combine(samples, "winget_upgrade_equipo.txt"))), userUpgrades, UsuarioInstalado2020(), output);
            // 2.0.24: ya no se dan por «al día» las copias viejas aunque tu usuario tenga la nueva (Vigía las cuenta): las cinco.
            Eq(5, conUsuario, "aunque tu usuario ya tenga las versiones nuevas de los marcos, las cinco (las cuenta Vigía)");
            output.Clear();
            var soloProgramas = UsuarioInstalado2020().FindAll(delegate(WingetRow r) { return !WingetProvider.IsFrameworkId(r.Id); });
            int n = WingetProvider.AddMachineRows(machine, userUpgrades, soloProgramas, output);
            Eq(5, n, "los cinco de la captura (los programas del equipo ya vinieron en la consulta del usuario)");
            var byId = new Dictionary<string, UpdateCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in output) byId[c.Identifier] = c;
            foreach (string id in new[] { "Microsoft.VCLibs.Desktop.14", "Microsoft.WindowsAppRuntime.1.4", "Microsoft.WindowsAppRuntime.1.5", "Microsoft.WindowsAppRuntime.1.6", "Microsoft.Teams.Free" })
                Check(byId.ContainsKey(id), "aparece " + id);
            if (byId.Count < 5) return;
            var vc = byId["Microsoft.VCLibs.Desktop.14"];
            Eq("Microsoft.VCLibs.140.00.UWPDesktop" + WingetProvider.MachineSuffix, vc.Name, "sin la copia del usuario, el nombre del paquete");
            Check(vc.InstalledVersion == "14.0.33519.0" && vc.AvailableVersion == "14.0.33728.0", "con las versiones de la captura");
            Check(vc.Status == UpdateStatus.Disponible && vc.Installable && vc.Recommended, "disponible y marcada: es un marco de ejecución");
            Check(((WingetRow)vc.ProviderTag).MachineScope, "se actualiza «para todo el equipo»");
            var war = byId["Microsoft.WindowsAppRuntime.1.6"];
            Check(war.InstalledVersion == "1.6.5" && war.AvailableVersion == "1.6.9" && war.Recommended, "Windows App Runtime 1.6: 1.6.5 → 1.6.9, marcada");
            var teams = byId["Microsoft.Teams.Free"];
            Eq("MicrosoftTeams" + WingetProvider.MachineSuffix, teams.Name, "Teams: el usuario no lo tiene, queda el nombre del paquete");
            Check(teams.Status == UpdateStatus.Disponible && teams.Installable && !teams.Recommended && teams.Detail.Contains("no la tiene"),
                  "Teams: se puede marcar, pero no se marca sola (al aprovisionarla también se le instala al usuario): " + teams.Detail);
            Check(vc.Detail.Contains("--scope machine"), "el detalle explica qué es y cómo se actualiza");

            // Lo que no es de lo aprovisionado no se duplica.
            output.Clear();
            var soloPrograma = new List<WingetRow> { new WingetRow { Name = "Programa X", Id = "Editor.X", Version = "1.0", Available = "2.0", Source = "winget" } };
            var usuario = new List<WingetRow> { new WingetRow { Name = "Programa X", Id = "Editor.X", Version = "1.0", Source = "winget" } };
            Eq(0, WingetProvider.AddMachineRows(soloPrograma, new List<WingetRow>(), usuario, output), "un programa del equipo que el usuario ve con la misma versión: no se repite");
            Eq(0, WingetProvider.AddMachineRows(new List<WingetRow> { new WingetRow { Name = "Y", Id = "MSIX\\Y_1.0_x64__abc", Version = "1.0", Available = "2.0" } }, null, null, output), "los MSIX sin paquete en winget: no");
            Eq(0, WingetProvider.AddMachineRows(new List<WingetRow> { new WingetRow { Name = "Z", Id = "Editor.Z", Version = "3.0", Available = "2.0", Source = "winget" } }, null, null, output), "nunca una versión más vieja");
            Check(WingetProvider.IsFrameworkId("Microsoft.UI.Xaml.2.8") && WingetProvider.IsFrameworkId("Microsoft.VCLibs.14") && !WingetProvider.IsFrameworkId("Microsoft.Teams.Free"), "qué es un marco");
        }

        /// <summary>La instalación: «winget upgrade --scope machine»; si winget no puede aprovisionar (Windows anterior a 24H2), el paquete oficial verificado.</summary>
        static void TestInstalarAppsParaTodosLosUsuarios2020()
        {
            var oldManifest = WingetDirect.ManifestForTests;
            var oldProvision = WingetProvider.ProvisionForTests;
            try
            {
                string vclibs = File.ReadAllText(Path.Combine(samples, "winget_manifest_vclibs_desktop14.yaml"));
                WingetDirect.ManifestForTests = delegate(string u) { return u.EndsWith("/Microsoft/VCLibs/Desktop/14/14.0.33728.0/Microsoft.VCLibs.Desktop.14.installer.yaml") ? vclibs : null; };
                var machine = WingetTable.Parse(File.ReadAllText(Path.Combine(samples, "winget_upgrade_equipo.txt")));
                var output = new List<UpdateCandidate>();
                WingetProvider.AddMachineRows(machine, null, new List<WingetRow>(), output);
                var vc = output.Find(delegate(UpdateCandidate c) { return c.Identifier == "Microsoft.VCLibs.Desktop.14"; });
                Check(vc != null, "la fila de VCLibs");
                if (vc == null) return;

                // 1) winget aprovisiona (Windows 11 24H2 o posterior).
                var calls = new List<string>();
                string provisioned = "14.0.33519.0";
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.RunForTests = delegate(string args) { calls.Add(args); provisioned = "14.0.33728.0"; return Salida(0, "Se instaló correctamente"); };
                wp.ListForTests = delegate(string args)
                {
                    calls.Add(args);
                    return Salida(0, "Nombre Id Versión Disponible Origen\r\n---------------------------------------------------------------------------\r\n" +
                                     "Microsoft.VCLibs.140.00.UWPDesktop Microsoft.VCLibs.Desktop.14 " + provisioned + " winget\r\n");
                };
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { vc }, delegate(UpdateCandidate x) { });
                Check(calls.Count >= 1 && calls[0] == "upgrade --id \"Microsoft.VCLibs.Desktop.14\" --exact --scope machine --accept-package-agreements --accept-source-agreements --silent --disable-interactivity --source \"winget\"",
                      "winget upgrade --scope machine: " + (calls.Count > 0 ? calls[0] : ""));
                Check(calls.Count == 2 && calls[1].StartsWith("list --id \"Microsoft.VCLibs.Desktop.14\" --exact --scope machine"), "y se confirma con lo aprovisionado: " + string.Join(" | ", calls.ToArray()));
                Check(vc.Status == UpdateStatus.Instalada && vc.Result.Contains("todos los usuarios"), "instalada para todos los usuarios: " + vc.Result);

                // 2) Windows anterior a 24H2: winget responde 0x8A150113 → se aprovisiona el paquete oficial (SHA-256 del manifiesto).
                output.Clear();
                WingetProvider.AddMachineRows(WingetTable.Parse(File.ReadAllText(Path.Combine(samples, "winget_upgrade_equipo.txt"))), null, new List<WingetRow>(), output);
                vc = output.Find(delegate(UpdateCandidate c) { return c.Identifier == "Microsoft.VCLibs.Desktop.14"; });
                calls.Clear();
                provisioned = "14.0.33519.0";
                var provisions = new List<string>();
                wp.RunForTests = delegate(string args) { calls.Add(args); return Salida(WingetProvider.MachineScopeNotSupported, "No se admite la instalación en todo el sistema"); };
                WingetProvider.ProvisionForTests = delegate(string url) { provisions.Add(url); provisioned = "14.0.33728.0"; return Salida(0, "PROV-OK"); };
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { vc }, delegate(UpdateCandidate x) { });
                Check(provisions.Count == 1 && provisions[0] == "https://github.com/microsoft/winget-cli/releases/download/v1.9.25180/DesktopAppInstaller_Dependencies.zip",
                      "aprovisionó el paquete oficial del manifiesto: " + string.Join(", ", provisions.ToArray()));
                Check(vc.Status == UpdateStatus.Instalada && vc.Result.Contains("SHA-256"), "instalada por la vía directa: " + vc.Result);

                // 3) Si Windows rechaza el paquete, la fila dice las dos cosas.
                output.Clear();
                WingetProvider.AddMachineRows(WingetTable.Parse(File.ReadAllText(Path.Combine(samples, "winget_upgrade_equipo.txt"))), null, new List<WingetRow>(), output);
                vc = output.Find(delegate(UpdateCandidate c) { return c.Identifier == "Microsoft.VCLibs.Desktop.14"; });
                WingetProvider.ProvisionForTests = delegate(string url) { return Salida(0, "PROV-ERR: Error de implementación 0x80073CF3"); };
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { vc }, delegate(UpdateCandidate x) { });
                Check(vc.Status == UpdateStatus.Fallida && vc.Result.Contains("0x80073CF3") && vc.Result.Contains("winget"), "falla con los dos motivos: " + vc.Result);

                // 4) Sin administrador no se intenta.
                output.Clear();
                WingetProvider.AddMachineRows(WingetTable.Parse(File.ReadAllText(Path.Combine(samples, "winget_upgrade_equipo.txt"))), null, new List<WingetRow>(), output);
                vc = output[0];
                calls.Clear();
                wp.Install(ContextoSimulado(false), new List<UpdateCandidate> { vc }, delegate(UpdateCandidate x) { });
                Check(vc.Status == UpdateStatus.Fallida && calls.Count == 0 && vc.Result.Contains("administrador"), "sin administrador: no (" + vc.Result + ")");

                // El instalador que sirve para aprovisionar, del manifiesto real.
                string why;
                var inst = WingetProvider.ProvisionableInstaller(WingetManifest.Parse(vclibs), "x64", out why);
                Check(inst != null && inst.IsArchive && inst.NestedFile == "x64/Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64.appx", "VCLibs: el .appx de x64 dentro del .zip: " + (inst != null ? inst.NestedFile : why));
                inst = WingetProvider.ProvisionableInstaller(WingetManifest.Parse(File.ReadAllText(Path.Combine(samples, "winget_manifest_teams_free.yaml"))), "ARM64", out why);
                Check(inst != null && inst.Url.Contains("production-windows-arm64"), "Teams: el .msix de ARM64");
                string ps = WingetProvider.ProvisionScript(@"C:\Windows\Temp\Actualizador\d1\Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64.appx");
                Check(ps.Contains("Add-AppxProvisionedPackage -Online -PackagePath 'C:\\Windows\\Temp\\Actualizador\\d1\\Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64.appx' -SkipLicense") && ps.Contains("PROV-OK"),
                      "el guion de aprovisionamiento: " + ps);
                Check(WingetProvider.MachineReached(new List<string> { "14.0.33728.0" }, "14.0.33728.0") && !WingetProvider.MachineReached(new List<string> { "14.0.33519.0", "14.0.33728.0" }, "14.0.33728.0") &&
                      WingetProvider.MachineReached(new List<string>(), "1.0") && !WingetProvider.MachineReached(null, "1.0"), "cuándo se da por actualizada");
            }
            finally { WingetDirect.ManifestForTests = oldManifest; WingetProvider.ProvisionForTests = oldProvision; }
        }

        // ------------------------------------------------------------------ Roblox

        static InstalledProgram RobloxInstalado()
        {
            var p = new InstalledProgram();
            p.Name = "Roblox Player"; p.Publisher = "Roblox Corporation"; p.Version = "";
            p.RegistryKey = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\roblox-player";
            p.DisplayIcon = @"C:\Users\Fede\AppData\Local\Roblox\Versions\version-0123456789abcdef\RobloxPlayerBeta.exe";
            p.UninstallString = "\"C:\\Users\\Fede\\AppData\\Local\\Roblox\\Versions\\version-0123456789abcdef\\RobloxPlayerBeta.exe\" -uninstall";
            p.InstallLocation = ""; p.Scope = "Usuario actual"; p.Architecture = "x64";
            return p;
        }

        /// <summary>La captura: Roblox con versión «Desconocida» y «winget no tiene una actualización aplicable».</summary>
        static void TestRoblox2020()
        {
            var oldV = RobloxPlayer.VersionsForTests;
            var oldI = RobloxPlayer.InstallForTests;
            var oldManifest = WingetDirect.ManifestForTests;
            var oldWait = RobloxPlayer.WaitForTests;
            try
            {
                RobloxPlayer.WaitForTests = delegate { return null; };
                string yaml = File.ReadAllText(Path.Combine(samples, "winget_manifest_roblox_0747.yaml"));
                var m = WingetManifest.Parse(yaml);
                Check(m.Installers.Count == 1 && m.Installers[0].Type == "portable", "el manifiesto real de la 0.747 es «portable» (por eso winget no puede con lo instalado)");
                Check(RobloxPlayer.IsOfficialUrl(m.Installers[0].Url), "y su instalador es de setup.rbxcdn.com: " + m.Installers[0].Url);
                Check(!RobloxPlayer.IsOfficialUrl("http://setup.rbxcdn.com/version-x-RobloxPlayerInstaller.exe") && !RobloxPlayer.IsOfficialUrl("https://rbxcdn.com.evil.net/version-x-RobloxPlayerInstaller.exe") &&
                      !RobloxPlayer.IsOfficialUrl("https://setup.rbxcdn.com/version-x-OtraCosa.exe"), "sólo HTTPS de rbxcdn.com y el instalador del reproductor");

                Eq(@"C:\Users\Fede\AppData\Local\Roblox", RobloxPlayer.RootOf(RobloxInstalado(), @"C:\Users\Otro\AppData\Local"), "la carpeta de Roblox sale de su entrada en «Aplicaciones»");
                Eq(@"C:\Users\Otro\AppData\Local" + Path.DirectorySeparatorChar + "Roblox", RobloxPlayer.RootOf(null, @"C:\Users\Otro\AppData\Local"), "sin entrada: la del usuario");
                Eq("0.747.0.7470829", RobloxPlayer.NormalizeVersion("0, 747, 0, 7470829"), "la versión del ejecutable con comas");
                Check(RobloxPlayer.UpToDate("0.747.0.7470829", "0.747") && !RobloxPlayer.UpToDate("0.746.0.7461001", "0.747") && RobloxPlayer.UpToDate("0.748.0.1", "0.747"),
                      "se compara con la precisión de la publicada («0.747»)");

                var versiones = new Dictionary<string, string>
                {
                    { @"...\version-aaaa\RobloxPlayerBeta.exe", "0, 746, 0, 7461001" },
                    { @"...\version-bbbb\RobloxPlayerBeta.exe", "0, 745, 0, 7450999" },
                };
                RobloxPlayer.VersionsForTests = delegate(string root) { return versiones; };
                var prog = RobloxInstalado();
                var w = new WingetRow { Name = "Roblox Player", Id = "Roblox.Roblox", Version = "Unknown", Available = "0.747", Source = "winget" };
                var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Roblox Player", InstalledVersion = "Desconocida", AvailableVersion = "0.747", ProviderName = WingetProvider.ProviderId, ProviderTag = w, Status = UpdateStatus.Disponible };
                RobloxPlayer.Resolve(c, w, new List<InstalledProgram> { prog });
                Check(c.InstalledVersion == "0.746.0.7461001" && c.Status == UpdateStatus.Disponible && c.Installable && c.Recommended,
                      "la versión real (la más nueva de su carpeta) y se marca sola: " + c.InstalledVersion + " · " + c.Detail);
                Check(c.Detail.Contains("portable") && c.Detail.Contains("como tu usuario"), "el detalle dice por qué y cómo: " + c.Detail);

                // Ya está al día: no se ofrece.
                versiones[@"...\version-cccc\RobloxPlayerBeta.exe"] = "0, 747, 0, 7470829";
                var c2 = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Roblox Player", ProviderName = WingetProvider.ProviderId, ProviderTag = w, Status = UpdateStatus.Disponible };
                RobloxPlayer.Resolve(c2, w, new List<InstalledProgram> { prog });
                Check(c2.Status == UpdateStatus.AlDia && !c2.Installable && c2.InstalledVersion == "0.747.0.7470829", "con la 0.747 instalada: al día (" + c2.Detail + ")");
                versiones.Remove(@"...\version-cccc\RobloxPlayerBeta.exe");

                // Sin ejecutable (sólo quedaron instaladores en la carpeta): se puede marcar, pero no se marca solo.
                RobloxPlayer.VersionsForTests = delegate(string root) { return new Dictionary<string, string>(); };
                var c3 = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Roblox Player", InstalledVersion = "Desconocida", ProviderName = WingetProvider.ProviderId, ProviderTag = w, Status = UpdateStatus.Disponible };
                RobloxPlayer.Resolve(c3, w, new List<InstalledProgram> { prog });
                Check(c3.Status == UpdateStatus.Disponible && c3.Installable && !c3.Recommended, "sin " + RobloxPlayer.Exe + ": no se marca sola");

                // La instalación: el instalador oficial del manifiesto (nunca winget), y se confirma con la versión de la carpeta.
                RobloxPlayer.VersionsForTests = delegate(string root) { return versiones; };
                WingetDirect.ManifestForTests = delegate(string u) { return u.EndsWith("/Roblox/Roblox/0.747/Roblox.Roblox.installer.yaml") ? yaml : null; };
                var instalados = new List<string>();
                RobloxPlayer.InstallForTests = delegate(string url, bool visible) { instalados.Add(url); versiones[@"...\version-4ea8e85481e34f46\RobloxPlayerBeta.exe"] = "0, 747, 0, 7470829"; return 0; };
                var winget = new List<string>();
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.RunForTests = delegate(string args) { winget.Add(args); return Salida(WingetProvider.UpdateNotApplicable, "No se encontró ninguna actualización aplicable."); };
                RobloxPlayer.Resolve(c, w, new List<InstalledProgram> { prog });
                wp.Install(ContextoSimulado(true, prog), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Eq(0, winget.Count, "no se le pide a winget (respondería 0x8A15002B)");
                Check(instalados.Count == 1 && instalados[0] == "https://setup.rbxcdn.com/version-4ea8e85481e34f46-RobloxPlayerInstaller.exe", "el instalador oficial de la 0.747: " + string.Join(", ", instalados.ToArray()));
                Check(c.Status == UpdateStatus.Instalada && c.InstalledVersion == "0.747.0.7470829", "instalada y confirmada: " + c.Result);

                // Si el instalador no deja la versión nueva, se dice qué hacer.
                versiones.Remove(@"...\version-4ea8e85481e34f46\RobloxPlayerBeta.exe");
                RobloxPlayer.InstallForTests = delegate(string url, bool visible) { return 0; };
                var c4 = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Roblox Player", ProviderName = WingetProvider.ProviderId, ProviderTag = w, Status = UpdateStatus.Disponible };
                RobloxPlayer.Resolve(c4, w, new List<InstalledProgram> { prog });
                wp.Install(ContextoSimulado(true, prog), new List<UpdateCandidate> { c4 }, delegate(UpdateCandidate x) { });
                Check(c4.Status == UpdateStatus.Fallida && c4.Result.Contains("0.746.0.7461001") && c4.Result.Contains("Abrí Roblox"), "no cambió: lo dice (" + c4.Result + ")");
                // Si la verificación falla, no se ejecuta nada.
                RobloxPlayer.InstallForTests = delegate(string url, bool visible) { return null; };
                var c5 = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Roblox Player", ProviderName = WingetProvider.ProviderId, ProviderTag = w, Status = UpdateStatus.Disponible };
                RobloxPlayer.Resolve(c5, w, new List<InstalledProgram> { prog });
                wp.Install(ContextoSimulado(true, prog), new List<UpdateCandidate> { c5 }, delegate(UpdateCandidate x) { });
                Check(c5.Status == UpdateStatus.Fallida, "verificación fallida: falla sin ejecutar (" + c5.Result + ")");
                Check(!RobloxPlayer.Handles(new WingetRow { Id = "Roblox.Roblox", MachineScope = true }) && !RobloxPlayer.Handles(new WingetRow { Id = "Roblox.RobloxStudio" }), "sólo el reproductor");
            }
            finally { RobloxPlayer.VersionsForTests = oldV; RobloxPlayer.InstallForTests = oldI; WingetDirect.ManifestForTests = oldManifest; RobloxPlayer.WaitForTests = oldWait; }
        }

        // ------------------------------------------------------------------ Squirrel (Claude)

        /// <summary>Claude: el registro sigue en 6.6395.0.0 y la carpeta «app-6.28899.2» dice la real (la que winget ofrece).</summary>
        static void TestSquirrelClaude2020()
        {
            var oldReader = ProgramRules.SquirrelVersionsReader;
            var oldExe = WingetProvider.ExeVersionReader;
            try
            {
                var claude = Registro(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AnthropicClaude", "Claude", "6.6395.0.0", "Anthropic, PBC",
                                      @"C:\Users\Fede\AppData\Local\AnthropicClaude", "\"C:\\Users\\Fede\\AppData\\Local\\AnthropicClaude\\Update.exe\" --uninstall", null,
                                      @"C:\Users\Fede\AppData\Local\AnthropicClaude\app.ico", false);
                claude.Scope = "Usuario actual";
                string pedido = null;
                ProgramRules.SquirrelVersionsReader = delegate(string root) { pedido = root; return new List<string> { "6.6395.0", "6.28899.2", "nada" }; };
                WingetProvider.ExeVersionReader = delegate(string f) { return "6.6395.0.0"; };
                Eq(@"C:\Users\Fede\AppData\Local\AnthropicClaude", ProgramRules.SquirrelRoot(@"C:\Users\Fede\AppData\Local\AnthropicClaude\app-6.28899.2"), "desde una carpeta «app-…», la de arriba");
                Eq("6.28899.2", ProgramRules.BestSquirrelVersion(new List<string> { "6.6395.0", "6.28899.2", "x y" }), "la más nueva");
                string source;
                string actual = WingetProvider.FilesVersion(claude, "6.6395.0.0", out source);
                Check(actual == "6.28899.2" && source.Contains("app-6.28899.2") && pedido == @"C:\Users\Fede\AppData\Local\AnthropicClaude", "la versión real, de su carpeta: " + actual + " (" + source + ")");
                Check(ProgramRules.IsProductVersionSource(source), "es la versión del producto (no la de un ejecutable)");

                // En la búsqueda: al día, con la explicación (antes «Falló: no cambió nada» en cada tanda).
                var w = new WingetRow { Name = "Claude", Id = "Anthropic.Claude", Version = "6.6395.0.0", Available = "6.28899.2", Source = "winget" };
                var c = FilaWinget("Claude", "Anthropic.Claude", "6.6395.0.0", "6.28899.2");
                c.ProviderTag = w;
                WingetProvider.CheckFilesVersion(c, w, new List<InstalledProgram> { claude });
                Check(c.Status == UpdateStatus.AlDia && !c.Installable && c.InstalledVersion == "6.28899.2", "Claude 6.28899.2: al día (" + c.Detail + ")");

                // Sin carpetas de Squirrel (otro tipo de instalación), nada cambia.
                ProgramRules.SquirrelVersionsReader = delegate(string root) { return new List<string>(); };
                c = FilaWinget("Claude", "Anthropic.Claude", "6.6395.0.0", "6.28899.2");
                c.ProviderTag = w;
                WingetProvider.CheckFilesVersion(c, w, new List<InstalledProgram> { claude });
                Check(c.Status == UpdateStatus.Disponible, "sin carpeta de versión: se ofrece como antes");
            }
            finally { ProgramRules.SquirrelVersionsReader = oldReader; WingetProvider.ExeVersionReader = oldExe; }
        }

        // ------------------------------------------------------------------ Programas sólo del usuario (Ollama)

        static void TestProgramasDelUsuarioSinAbrirLaPagina2020()
        {
            var usuario = new InstalledProgram { Name = "Ollama version 0.40.16", Version = "0.40.16", Publisher = "Ollama", Scope = "Usuario actual",
                                                 RegistryKey = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{44E83376-CE68-45EB-8FC1-393500EB558C}_is1",
                                                 InstallLocation = @"C:\Users\Fede\AppData\Local\Programs\Ollama\" };
            string dir = VendorProvider.InstallDirOf(usuario);
            Check(VendorProvider.InstallsAsUser(true, usuario, dir, false), "sólo del usuario, en su carpeta, y Actualizador como administrador: se instala como el usuario");
            Check(!VendorProvider.InstallsAsUser(false, usuario, dir, false), "sin administrador: como siempre");
            Check(!VendorProvider.InstallsAsUser(true, usuario, dir, true), "para todo el equipo: no");
            var otro = new InstalledProgram { Name = "X", Version = "1", Scope = "Usuario (S-1-5-21-…-1002)", RegistryKey = @"HKU\S-1-5-21-1002\…" };
            Check(!VendorProvider.InstallsAsUser(true, otro, "", false), "de otro usuario: no");
            var equipo = new InstalledProgram { Name = "Y", Version = "1", Scope = "Equipo", RegistryKey = @"HKLM\…" };
            Check(!VendorProvider.InstallsAsUser(true, equipo, @"C:\Program Files\Y", true), "del equipo: no");

            string args = VendorProvider.AsUserArgs("/i \"C:\\Windows\\Temp\\Actualizador\\d1\\ollama.msi\" /qn /norestart /l*v \"C:\\Windows\\Temp\\Actualizador\\logs\\msi-ollama.log\"",
                                                    @"C:\Windows\Temp\Actualizador\d1\ollama.msi", @"C:\Users\Fede\AppData\Local\Temp\Actualizador-1\ollama.msi",
                                                    @"C:\Windows\Temp\Actualizador\logs\msi-ollama.log", @"C:\Users\Fede\AppData\Local\Temp\Actualizador-1\msi-ollama.log");
            Eq("/i \"C:\\Users\\Fede\\AppData\\Local\\Temp\\Actualizador-1\\ollama.msi\" /qn /norestart /l*v \"C:\\Users\\Fede\\AppData\\Local\\Temp\\Actualizador-1\\msi-ollama.log\"", args,
               "un .msi como el usuario: la copia y el registro en su carpeta temporal");

            // El flujo completo (página oficial de CrystalDiskInfo, pero instalado sólo para el usuario): se ejecuta como el usuario.
            var oldDownload = VendorProvider.DownloadForTests;
            var oldSig = VendorProvider.SignatureForTests;
            var oldSigners = WingetDirect.InstalledSignersForTests;
            var oldProduct = SiteVersions.ProductOfForTests;
            var oldRun = VendorProvider.RunInstallerForTests;
            var oldAsUser = VendorProvider.RunAsUserForTests;
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
                instalado.Scope = "Usuario actual";
                instalado.RegistryKey = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CrystalDiskInfo_is1";
                instalado.InstallLocation = @"C:\Users\Fede\AppData\Local\Programs\CrystalDiskInfo\";
                instalado.DisplayIcon = @"C:\Users\Fede\AppData\Local\Programs\CrystalDiskInfo\DiskInfo64.exe";
                instalado.UninstallString = "\"C:\\Users\\Fede\\AppData\\Local\\Programs\\CrystalDiskInfo\\unins000.exe\"";
                ctx.Programs.Add(instalado);
                byte[] exe = InstaladorFalso("Inno Setup Setup Data (6.4.0)", 7);
                string versionAhora = "9.9.4";
                var elevados = new List<string>();
                var comoUsuario = new List<string>();
                var abiertas = new List<string>();
                VendorProvider.DownloadForTests = delegate(string url, string file) { File.WriteAllBytes(file, exe); };
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(0, CdiSigner); };
                WingetDirect.InstalledSignersForTests = delegate(InstalledProgram p) { return new List<string> { "Open Source Developer", "Open Source Developer, Noriyuki Miyazaki" }; };
                SiteVersions.ProductOfForTests = delegate(string f) { return "CrystalDiskInfo"; };
                VendorProvider.ExeVersionForTests = delegate(string f) { return "9.9.5.0"; };
                VendorProvider.RunInstallerForTests = delegate(string file, string a) { elevados.Add(a); versionAhora = "9.9.5"; return new ProcResult { Started = true, ExitCode = 0 }; };
                VendorProvider.RunAsUserForTests = delegate(string file, string a)
                {
                    comoUsuario.Add(Path.GetFileName(file) + " " + a);
                    Check(File.Exists(file) && new FileInfo(file).Length == exe.Length, "la copia que se ejecuta es la verificada");
                    versionAhora = "9.9.5";
                    return new ProcResult { Started = true, ExitCode = Unelevated.UnknownExit };
                };
                VendorProvider.ProgramsNowForTests = delegate
                {
                    var q = CdiInstaladoComo(instalado, versionAhora);
                    q.Scope = instalado.Scope; q.RegistryKey = instalado.RegistryKey; q.InstallLocation = instalado.InstallLocation;
                    return new List<InstalledProgram> { q };
                };
                VendorProvider.OpenPageForTests = delegate(string u) { abiertas.Add(u); };
                var c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                new VendorProvider().Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Instalada, "instalada sin abrir la página (" + c.Status + ": " + c.Result + ")");
                Check(comoUsuario.Count == 1 && elevados.Count == 0 && comoUsuario[0] == "CrystalDiskInfo9_9_5.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
                      "ejecutado como el usuario, nunca elevado: " + string.Join(", ", comoUsuario.ToArray()));
                Eq(0, abiertas.Count, "ninguna página abierta");

                // Con otro firmante, tampoco como usuario: la verificación es la misma.
                comoUsuario.Clear(); versionAhora = "9.9.4";
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(0, "CN=Otra Empresa SRL, O=Otra Empresa SRL, C=AR"); };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                new VendorProvider().Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(comoUsuario.Count == 0 && c.Result.Contains("Otra Empresa SRL"), "otro firmante: no se ejecuta ni como el usuario (" + c.Result + ")");
            }
            finally
            {
                VendorProvider.DownloadForTests = oldDownload; VendorProvider.SignatureForTests = oldSig; WingetDirect.InstalledSignersForTests = oldSigners;
                SiteVersions.ProductOfForTests = oldProduct; VendorProvider.RunInstallerForTests = oldRun; VendorProvider.RunAsUserForTests = oldAsUser;
                VendorProvider.ProgramsNowForTests = oldNow; VendorProvider.ExeVersionForTests = oldExeV; VendorProvider.OpenPageForTests = oldOpen;
                UpdateManager.PolicyForTests = oldPolicy; VendorProvider.ConfirmWaitMs = oldWait; VendorProvider.ConfirmPollMs = oldPoll;
            }
        }

        // ------------------------------------------------------------------ Windows Update

        static void TestWindowsUpdate2020()
        {
            double gb = 1024.0 * 1024 * 1024;
            Eq("Descarga de hasta " + Fmt.Bytes(650 * 1024.0 * 1024), WindowsUpdateProvider.DownloadSizeText(650 * 1024.0 * 1024, 0), "un tamaño normal, como siempre");
            string t = WindowsUpdateProvider.DownloadSizeText(90.5 * gb, 0);
            Check(t != null && !t.Contains("GB") && t.Contains("le falte"), "90,5 GB (la suma de todas las variantes): no se muestra ese número: " + t);
            t = WindowsUpdateProvider.DownloadSizeText(90.5 * gb, 420 * 1024.0 * 1024);
            Check(t != null && t.StartsWith("Descarga desde ") && t.Contains("MB"), "con el mínimo que informa Windows: " + t);
            Check(WindowsUpdateProvider.DownloadSizeText(0, 0) == null, "sin dato: nada");

            int b, r;
            Check(WindowsUpdateProvider.CumulativeBuild("2026-11 Actualización de seguridad (KB5132061) (26300.9974)", out b, out r) && b == 26300 && r == 9974, "la compilación de la acumulativa");
            Check(!WindowsUpdateProvider.CumulativeBuild("Actualización de inteligencia de seguridad para Microsoft Defender Antivirus - KB2267602 (versión 1.441.12.0)", out b, out r), "Defender no es acumulativa de Windows");

            Func<string, UpdateCandidate> wu = delegate(string name) { return new UpdateCandidate { Kind = UpdateKind.WindowsUpdate, Name = name }; };
            var seg = wu("2026-11 Actualización de seguridad (KB5132061) (26300.9974)");
            var pre = wu("2026-11 Actualización de versión preliminar (KB5132057) (26300.10075)");
            var net = wu("2026-11 Actualización acumulativa de .NET Framework 3.5 y 4.8.1 (KB5070001)");
            var otra = wu("2026-10 Actualización de seguridad (KB5130000) (26100.6000)");
            var orden = WindowsUpdateProvider.InstallOrder(new List<UpdateCandidate> { net, seg, otra, pre });
            Check(orden.Count == 4 && orden[0] == net && orden[1] == pre && orden[2] == seg && orden[3] == otra,
                  "la preliminar (26300.10075) antes que la de seguridad (26300.9974), en el lugar de la primera; lo demás no se mueve: " +
                  string.Join(" | ", orden.ConvertAll(delegate(UpdateCandidate x) { return x.Name.Substring(0, 30); }).ToArray()));
            Check(WindowsUpdateProvider.IncludedIn(seg, new[] { pre }) == null, "si la nueva no se instaló, la vieja se intenta");
            pre.Status = UpdateStatus.RequiereReinicio;
            Check(WindowsUpdateProvider.IncludedIn(seg, new[] { pre }) == pre, "si la nueva se instaló, la vieja ya está incluida");
            Check(WindowsUpdateProvider.IncludedIn(pre, new[] { seg }) == null && WindowsUpdateProvider.IncludedIn(otra, new[] { pre }) == null, "nunca al revés ni de otra compilación");

            string h = WindowsUpdateProvider.InstallErrorHint(unchecked((int)0x800F081F));
            Check(h.Contains("DISM /Online /Cleanup-Image /RestoreHealth") && h.Contains("sfc /scannow"), "0x800F081F (el de la captura): cómo repararlo: " + h);
            Check(WindowsUpdateProvider.InstallErrorHint(unchecked((int)0x80070070)).Contains("espacio"), "disco lleno");
            Eq("", WindowsUpdateProvider.InstallErrorHint(unchecked((int)0x80004005)), "lo desconocido: sin inventar");
        }

        /// <summary>La versión de esta entrega en todos los lugares que la muestran.</summary>
        static void TestVersion2020()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.20") >= 0, "Actualizador 2.0.20 o posterior: " + AppInfo.Version);
        }
    }
}
