// Pruebas de la 2.0.44. Diagnóstico del usuario (26/11/2026, 20:10): Epic Online Services 5.4.0 → 5.8.0 «Falló la instalación: … No se
// pudo reinstalar automáticamente: el manifiesto no indica cómo instalarlo en silencio». Y foto de Instalar.cmd (código fuente) en un
// equipo sin el SDK de .NET 8: «Instalá el SDK de .NET 8 x64 … y volvé a ejecutar». Más el relevamiento de seguridad.
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
        // El manifiesto real de winget-pkgs (EpicGames.EpicOnlineServices 5.8.0), con la descarga apuntando a esta
        // misma máquina: la prueba no baja nada de internet.
        const string EosManifiesto =
            "PackageIdentifier: EpicGames.EpicOnlineServices\n" +
            "PackageVersion: 5.8.0\n" +
            "InstallerType: zip\n" +
            "NestedInstallerType: exe\n" +
            "NestedInstallerFiles:\n" +
            "- RelativeFilePath: SDK\\Tools\\EpicOnlineServicesInstaller.exe\n" +
            "Scope: machine\n" +
            "UpgradeBehavior: uninstallPrevious\n" +
            "ProductCode: '{01D2EA5A-2EC8-4C48-BF14-FF6B826ED9E7}'\n" +
            "ElevationRequirement: elevatesSelf\n" +
            "Installers:\n" +
            "- Architecture: x64\n" +
            "  InstallerUrl: https://127.0.0.1/eos-sdk.zip\n" +
            "  InstallerSha256: 2D1A73C7912DE71CBAC8CA39E51D189CD8A7CC4B5EB1B3D86D645F1467B703E6\n" +
            "ManifestType: installer\n" +
            "ManifestVersion: 1.28.0\n";

        static InstalledProgram EosInstalado()
        {
            // 5.4.0 instalado con Windows Installer (lo que había en el equipo del diagnóstico).
            return Registro(Uninst + "{2B9F3A44-1C61-4E0C-9A37-5F0E2C2B7D10}", "Epic Online Services", "5.4.0", "Epic Games, Inc.",
                            @"C:\Program Files (x86)\Epic Games\Epic Online Services\", "MsiExec.exe /X{2B9F3A44-1C61-4E0C-9A37-5F0E2C2B7D10}", null, null, true);
        }

        /// <summary>El manifiesto de Epic Online Services: ZIP con un .exe adentro y ningún interruptor.</summary>
        static void TestSinModoSilencioso2044()
        {
            var m = WingetManifest.Parse(EosManifiesto);
            Eq(1, m.Installers.Count, "un instalador");
            var inst = m.Installers[0];
            Check(inst.IsArchive && inst.EffectiveType == "exe", "ZIP con un .exe adentro: " + inst.Type + "/" + inst.EffectiveType);
            Check(WingetRules.HasNoSilentMode(inst), "no dice cómo instalarlo en silencio");
            bool msiexec; string why;
            Check(WingetRules.BuildArguments(inst, "x.exe", false, null, out msiexec, out why) == null && why.Contains("en silencio"), "en silencio: no se puede (" + why + ")");
            Check(WingetRules.BuildArguments(inst, "x.exe", true, null, out msiexec, out why) != null, "con su ventana: sí");

            // Lo que sí trae una forma silenciosa no cuenta como «sin modo silencioso».
            var nsis = WingetManifest.Parse(EosManifiesto.Replace("NestedInstallerType: exe", "NestedInstallerType: nullsoft")).Installers[0];
            Check(!WingetRules.HasNoSilentMode(nsis), "NSIS: /S");
            var conSwitch = WingetManifest.Parse(EosManifiesto + "InstallerSwitches:\n  Silent: /quiet\n").Installers[0];
            Check(!WingetRules.HasNoSilentMode(conSwitch), "con Silent declarado");
            var modos = WingetManifest.Parse(EosManifiesto + "InstallModes:\n- silent\n").Installers[0];
            Check(!WingetRules.HasNoSilentMode(modos), "InstallModes: silent");
            Check(!WingetRules.HasNoSilentMode(null), "sin instalador: no");
        }

        /// <summary>
        /// La captura: winget devuelve 0x8A15008E, se intenta la reinstalación verificada y ya no se corta por «el manifiesto no indica
        /// cómo instalarlo en silencio»: con alguien frente a la pantalla se sigue con la ventana de su instalador (acá la descarga falla
        /// porque apunta a un puerto cerrado: no se desinstala nada). Sin nadie, o si la política no lo permite, la fila dice por qué.
        /// </summary>
        static void TestEosConSuVentana2044()
        {
            var oldManifest = WingetDirect.ManifestForTests;
            var oldPolicy = UpdateManager.PolicyForTests;
            try
            {
                UpdateManager.PolicyForTests = new Policy();
                WingetDirect.ManifestForTests = delegate(string u)
                {
                    return u.EndsWith("/EpicGames/EpicOnlineServices/5.8.0/EpicGames.EpicOnlineServices.installer.yaml") ? EosManifiesto : null;
                };
                var eos = EosInstalado();
                Eq(InstallTech.Msi, ProgramRules.Technology(eos), "5.4.0 instalada con Windows Installer");
                var pedidos = new List<string>();
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.RunForTests = delegate(string args)
                {
                    pedidos.Add(args);
                    return Salida(WingetDirect.TechMismatch, "Se encontró una versión más reciente, pero la tecnología de instalación es diferente de la versión actual instalada.");
                };
                var c = FilaWinget("Epic Online Services", "EpicGames.EpicOnlineServices", "5.4.0", "5.8.0");
                var resultados = new List<string>();
                wp.Install(ContextoSimulado(true, eos), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { resultados.Add(x.Result ?? ""); });
                Check(!c.Result.Contains("no indica cómo instalarlo en silencio"), "ya no se corta por el modo silencioso: " + c.Result);
                Check(resultados.Exists(delegate(string r) { return r.Contains("completá la instalación en su ventana"); }), "la fila avisa que se abre la ventana de su instalador");
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("no se desinstaló nada"), "la descarga falló antes de tocar nada: " + c.Result);
                Check(!wp.Interactive, "terminado, el proveedor vuelve a como estaba");
                Check(!pedidos.Exists(delegate(string a) { return a.Contains("--interactive"); }), "winget sigue en silencio (la ventana es sólo para el instalador del manifiesto)");

                // Sin nadie frente a la pantalla (revisión programada): no se abre nada y se dice por qué.
                var wu = new WingetProvider { Unattended = true };
                wu.SimulateVersion("1.29.380");
                wu.RunForTests = wp.RunForTests;
                c = FilaWinget("Epic Online Services", "EpicGames.EpicOnlineServices", "5.4.0", "5.8.0");
                wu.Install(ContextoSimulado(true, eos), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("no tiene modo silencioso") && c.Result.Contains("sin nadie frente a la pantalla"),
                      "sin nadie: no se abre su ventana (" + c.Result + ")");

                // La política de la empresa no permite instaladores con ventana.
                UpdateManager.PolicyForTests = new Policy { AllowInteractiveInstallers = false };
                c = FilaWinget("Epic Online Services", "EpicGames.EpicOnlineServices", "5.4.0", "5.8.0");
                wp.Install(ContextoSimulado(true, eos), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("política de la empresa"), "con la política: " + c.Result);
            }
            finally { WingetDirect.ManifestForTests = oldManifest; UpdateManager.PolicyForTests = oldPolicy; }
        }

        /// <summary>Con la ventana sólo para el instalador nuevo, la versión anterior se desinstala en silencio, como siempre.</summary>
        static void TestDesinstalaEnSilencio2044()
        {
            string why;
            var plan = ProgramRules.PlanUninstall(EosInstalado(), false, out why);
            Check(plan != null && plan.UsesMsiexec && plan.Args.Contains("/qn"), "MSI en silencio: " + (plan != null ? plan.Args : why));
            string raiz = Raiz();
            if (raiz == null) { Check(false, "no se encontró la raíz del repositorio"); return; }
            string src = Leer(raiz, "src/Actualizador.Engine/Updates/WingetDirect.cs");
            Check(src.Contains("bool uninstallWindow = interactive && !quietUninstall;"), "la ventana del desinstalador sólo si se pidió para todo");
            Check(src.Contains("if (running != null && !uninstallWindow)"), "con el programa abierto y desinstalación silenciosa no se toca nada");
        }

        /// <summary>Instalar.cmd del código fuente sin el SDK: se usa la misma versión ya publicada por GitHub, verificada.</summary>
        static void TestInstalarSinSdk2044()
        {
            string raiz = Raiz();
            if (raiz == null) { Check(false, "no se encontró la raíz del repositorio"); return; }
            string comun = Leer(raiz, "build/Comun.ps1");
            string publish = Leer(raiz, "build/Publish.ps1");
            Check(comun.Contains("function Get-EntregaPublicada"), "función para bajar la versión publicada");
            Check(comun.Contains("\"https://github.com/$Repo/releases/download/v$Version/\""), "sólo de github.com/<repo>/releases, por HTTPS");
            Check(comun.Contains("$Version -notmatch '^\\d{1,4}(\\.\\d{1,6}){2,3}$'"), "la versión se valida antes de armar la dirección");
            Check(comun.Contains("no coincide con el SHA-256 publicado"), "el .zip se compara con su SHA-256 publicado");
            Check(comun.Contains("el .zip trae una ruta no válida"), "rutas que salen de la carpeta: se rechaza antes de descomprimir");
            Check(comun.Contains("la entrega publicada es la versión"), "el Actualizador.exe de adentro tiene que ser de esta versión");
            Check(comun.Contains("no coincide con su SHA-256\""), "cada archivo contra SHA256SUMS.txt");
            Check(comun.Contains("Remove-Item -LiteralPath $tmp -Recurse -Force"), "la carpeta temporal se borra siempre");
            int iSdk = publish.IndexOf("-not (Use-DotNetSdk8)", StringComparison.Ordinal);
            int iBaja = publish.IndexOf("Get-EntregaPublicada -Version $version -Salida $Salida", StringComparison.Ordinal);
            int iAssert = publish.IndexOf("Assert-DotNetSdk", StringComparison.Ordinal);
            Check(iSdk > 0 && iBaja > iSdk && iAssert > iBaja, "Publish.ps1: sólo sin SDK, y antes de pedirlo");
            Check(publish.Contains("-not $CertificadoHuella -and -not $Pfx"), "si se pidió firmar, no se reemplaza por la publicada");
            Check(publish.IndexOf("Get-EntregaPublicada", StringComparison.Ordinal) > publish.IndexOf("if ($SiHaceFalta)", StringComparison.Ordinal), "sólo con -SiHaceFalta (Instalar.cmd)");
            // winget fuera del PATH de la ventana elevada.
            Check(comun.Contains("function Find-Winget") && comun.Contains("Microsoft\\WindowsApps\\winget.exe") && comun.Contains("Microsoft.DesktopAppInstaller"),
                  "winget se busca también en el alias de la cuenta y en App Installer");
            Check(comun.Contains("& $winget install --id Microsoft.DotNet.SDK.8") && !comun.Contains("& winget install"), "y se usa el que se encontró");
            // Los guiones de PowerShell 5.1 con acentos necesitan la marca UTF-8.
            foreach (string f in new[] { "build/Comun.ps1", "build/Publish.ps1" })
            {
                byte[] b = File.ReadAllBytes(Path.Combine(raiz, f.Replace('/', Path.DirectorySeparatorChar)));
                Check(b.Length > 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF, f + ": UTF-8 con BOM");
            }
        }

        /// <summary>Auditoría 2.0.44: el aprovisionamiento para todos sólo toma una copia de la Store o de Windows.</summary>
        static void TestAprovisionaSoloDeLaStore2044()
        {
            string ps = WingetProvider.ProvisionFromInstalledScript(new List<string> { "Microsoft.OutlookForWindows" }, "1.2026.1026.100");
            Check(ps.Contains("-not $_.IsDevelopmentMode"), "nunca una registrada en modo desarrollador");
            Check(ps.Contains("(@('Store','System') -contains [string]$_.SignatureKind)"), "sólo firma de la Store o del sistema");
            Check(ps.IndexOf("SignatureKind", StringComparison.Ordinal) < ps.IndexOf("ProvisionPackageForAllUsersAsync", StringComparison.Ordinal), "se filtra antes de aprovisionar");
        }

        /// <summary>Relevamiento: dos copias del mismo paquete de winget (Kingston SSD Manager 1.1.2.6 y 1.5.6.5), las dos «Al día».</summary>
        static void TestCopiaAnteriorDelMismoPaquete2044()
        {
            var vieja = WingetProvider.FromListRow(new WingetRow { Name = "Kingston SSD Manager version 1.1.2.6", Id = "Kingston.SSDManager", Version = "1.1.2.6", Available = "", Source = "winget" },
                                                   FilaWinget("Kingston SSD Manager version 1.1.2.6", "Kingston.SSDManager", "1.1.2.6", ""));
            var nueva = WingetProvider.FromListRow(new WingetRow { Name = "Kingston SSD Manager x64 1.5.6.5", Id = "Kingston.SSDManager", Version = "1.5.6.5", Available = "", Source = "winget" },
                                                   FilaWinget("Kingston SSD Manager x64 1.5.6.5", "Kingston.SSDManager", "1.5.6.5", ""));
            var otra = WingetProvider.FromListRow(new WingetRow { Name = "7-Zip", Id = "7zip.7zip", Version = "26.00", Available = "", Source = "winget" },
                                                  FilaWinget("7-Zip", "7zip.7zip", "26.00", ""));
            Check(vieja.Status == UpdateStatus.AlDia && vieja.Detail.StartsWith("Coincide"), "antes: la 1.1.2.6 «coincide con la última»");
            var filas = new List<UpdateCandidate> { vieja, nueva, otra };
            WingetProvider.MarkOlderCopies(filas);
            Check(vieja.Detail.StartsWith("Convive con «Kingston SSD Manager x64 1.5.6.5» (1.5.6.5)"), "la 1.1.2.6 dice con qué copia convive: " + vieja.Detail);
            Eq("1.5.6.5", vieja.AvailableVersion, "y la versión del paquete");
            Eq(UpdateStatus.AlDia, vieja.Status, "sigue «Al día» (winget y Vigía no la ofrecen)");
            Eq(UpdateGroup.AlDia, UpdateGroups.Of(vieja), "no pasa a «Disponibles»");
            Check(nueva.Detail.StartsWith("Coincide") && nueva.AvailableVersion == "1.5.6.5", "la más nueva queda igual");
            Check(otra.Detail.StartsWith("Coincide"), "otro paquete: sin cambios");
            // Una sola copia, o versiones que no se comparan: sin cambios.
            var sola = FilaWinget("X", "X.Y", "1.0", "");
            sola = WingetProvider.FromListRow(new WingetRow { Name = "X", Id = "X.Y", Version = "1.0", Available = "", Source = "winget" }, sola);
            WingetProvider.MarkOlderCopies(new List<UpdateCandidate> { sola });
            Check(sola.Detail.StartsWith("Coincide"), "una sola copia: sin cambios");
            WingetProvider.MarkOlderCopies(null);
        }

        static void TestVersion2044()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.44") >= 0, "Actualizador 2.0.44 o posterior: " + AppInfo.Version);
        }
    }
}
