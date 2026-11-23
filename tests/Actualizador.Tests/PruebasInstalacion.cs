// Instalación: programas abiertos, Windows Installer y resumen de la tanda.
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
        // ------------------------------------------------------------------ Registro de Windows Installer

        const string LogEpicEn =
            "=== Verbose logging started: 3/10/2026  10:42:05  Build type: SHIP UNICODE 5.00.10011.00  Calling process: C:\\WINDOWS\\system32\\msiexec.exe ===\r\n" +
            "MSI (s) (A0:B4) [10:42:06:120]: Product: Epic Games Launcher -- Error 1708. Installation operation failed.\r\n" +
            "Action start 10:42:06: InstallValidate.\r\n" +
            "MSI (s) (A0:B4) [10:42:07:001]: RESTART MANAGER: Detected that application with id 7212, friendly name 'Epic Games Launcher', of type RmMainWindow and status 1 holds file[s] in use.\r\n" +
            "MSI (s) (A0:B4) [10:42:07:002]: RESTART MANAGER: Did detect that a critical application holds file[s] in use, so a reboot will be necessary.\r\n" +
            "CustomAction LauncherPrereqSetup returned actual error code 1603 (note this may not be 100% accurate if translation happened inside sandbox)\r\n" +
            "Action ended 10:42:55: InstallFinalize. Return value 3.\r\n" +
            "Action ended 10:42:55: INSTALL. Return value 3.\r\n" +
            "MSI (s) (A0:B4) [10:42:55:900]: Windows Installer installed the product. Product Name: Epic Games Launcher. Product Version: 1.3.210.0. Product Language: 1033. Manufacturer: Epic Games, Inc.. Installation success or error status: 1603.\r\n";

        const string LogSystemInfoEs =
            "MSI (s) (5C:10) [10:43:20:500]: Producto: Futuremark SystemInfo -- Error 1714. No se puede quitar la versión anterior de Futuremark SystemInfo. Póngase en contacto con el grupo de soporte técnico. Error del sistema 1612.\r\n" +
            "Acción finalizada 10:43:26: RemoveExistingProducts. Valor devuelto 3.\r\n" +
            "Acción finalizada 10:43:26: INSTALL. Valor devuelto 3.\r\n" +
            "MSI (s) (5C:10) [10:43:26:100]: Producto: Futuremark SystemInfo -- Error en la instalación.\r\n" +
            "MSI (s) (5C:10) [10:43:26:101]: Windows Installer instaló el producto. Nombre del producto: Futuremark SystemInfo. Versión del producto: 5.93.1517.0. Estado de error o éxito de la instalación: 1603.\r\n";

        static void TestRegistroMsi()
        {
            var d = MsiLogAnalyzer.Analyze(LogEpicEn);
            Eq(0, d.Error, "Epic: 1708 es genérico, no cuenta como causa");
            Check(d.FilesInUseBy.Count == 1 && d.FilesInUseBy[0] == "Epic Games Launcher", "Epic: archivos en uso por el propio launcher");
            Eq("LauncherPrereqSetup", d.CustomAction, "acción propia que falló");
            Eq(1603, d.CustomActionCode, "código de la acción");
            Eq("InstallFinalize", d.FailedAction, "primera acción con «Return value 3» (no INSTALL)");
            string e = MsiLogAnalyzer.Explain(d);
            Check(e.Contains("archivos en uso por Epic Games Launcher") && e.Contains("cerralo"), "explicación: cerrar el launcher: " + e);
            Check(!e.Contains("1708"), "no se menciona el error genérico");

            d = MsiLogAnalyzer.Analyze(LogSystemInfoEs);
            Eq(1714, d.Error, "SystemInfo en español: error 1714 (registro en español)");
            Check(d.Message.StartsWith("No se puede quitar la versión anterior"), "mensaje de Windows Installer en el idioma de Windows: " + d.Message);
            Eq("RemoveExistingProducts", d.FailedAction, "acción que falló, en un registro en español");
            e = MsiLogAnalyzer.Explain(d);
            Check(e.Contains("versión anterior") && e.Contains("caché") && e.Contains("Configuración › Aplicaciones"), "1714: cómo resolverlo: " + e);

            // Condición de inicio sin número de error.
            d = MsiLogAnalyzer.Analyze("MSI (c) (11:22) [01:02:03:004]: Product: Foo -- A newer version of Foo is already installed.\r\nAction ended 01:02:03: LaunchConditions. Return value 3.\r\n");
            Check(d.Message.Contains("newer version"), "condición de inicio sin número: " + d.Message);
            Check(MsiLogAnalyzer.Explain(d).Contains("Windows Installer dice"), "se muestra tal cual");

            // Error suelto (sin «Product:») y un registro sin nada útil.
            d = MsiLogAnalyzer.Analyze("MSI (s) (11:22) [01:02:03:004]: Note: 1: 2205 2:  3: Error\r\nError 1925. You do not have sufficient privileges to complete this installation for all users of the machine.\r\n");
            Eq(1925, d.Error, "error suelto 1925");
            Check(MsiLogAnalyzer.Explain(d).Contains("administrador"), "1925: permisos");
            Check(MsiLogAnalyzer.Analyze("nada que ver\r\notra línea\r\n").IsEmpty, "registro sin datos útiles");
            Eq("", MsiLogAnalyzer.Explain(MsiLogAnalyzer.Analyze("")), "vacío: sin explicación");
            d = MsiLogAnalyzer.Analyze("MSI (s) (5C:10) [10:43:26:100]: Product: X -- Error 1603. Fatal error during installation.\r\n");
            Check(d.Error == 0 && d.Message.Contains("Fatal error"), "sólo el 1603 genérico: queda el texto, sin número");

            // Ruta del registro en la salida de winget (español e inglés, con espacios en el usuario).
            string salidaEs = "  Iniciando instalación de paquete...\r\n  Error del instalador con el código de salida: 1603\r\n" +
                              "  El registro del instalador está disponible en: C:\\Users\\Pc 1\\AppData\\Local\\Packages\\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\\LocalState\\DiagOutputDir\\EpicGames.EpicGamesLauncher.1.3.210.0-26-10-03-10-42-05.log\r\n";
            Eq(@"C:\Users\Pc 1\AppData\Local\Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\DiagOutputDir\EpicGames.EpicGamesLauncher.1.3.210.0-26-10-03-10-42-05.log",
               MsiLogAnalyzer.LogPathFromWinget(salidaEs), "ruta del registro (español, usuario con espacio)");
            Check(MsiLogAnalyzer.LogPathFromWinget("Installer log is available at: C:\\Users\\a\\AppData\\Local\\Packages\\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\\LocalState\\DiagOutputDir\\x.log") != null, "ruta (inglés)");
            Check(MsiLogAnalyzer.LogPathFromWinget("El registro está en: C:\\Windows\\System32\\config\\x.log") == null, "fuera de DiagOutputDir: no se lee");
            Check(MsiLogAnalyzer.LogPathFromWinget(null) == null, "sin salida");

            // Lectura real de un archivo en UTF-16 con BOM (como los que escribe msiexec) y en ANSI.
            string tmp = Path.Combine(Path.GetTempPath(), "sp153-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                File.WriteAllText(tmp, LogSystemInfoEs, new UnicodeEncoding(false, true));
                Check(MsiLogAnalyzer.ExplainFile(tmp).Contains("1714"), "UTF-16 con BOM: se lee y se explica");
                File.WriteAllText(tmp, LogEpicEn, Encoding.ASCII);
                Check(MsiLogAnalyzer.ExplainFile(tmp).Contains("Epic Games Launcher"), "ANSI: se lee y se explica");
                var big = new StringBuilder();
                for (int i = 0; i < 60000; i++) big.Append("MSI (s) (A0:B4) [10:00:00:000]: relleno ").Append(i).Append("\r\n");
                big.Append(LogSystemInfoEs);
                File.WriteAllText(tmp, big.ToString(), new UnicodeEncoding(false, true));
                Check(MsiLogAnalyzer.ReadTail(tmp, 64 * 1024).Contains("Error 1714"), "registro grande: se lee el final");
            }
            finally { try { File.Delete(tmp); } catch { } }
            Eq("", MsiLogAnalyzer.ExplainFile(Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N") + ".log")), "archivo que no existe");
            Eq("", MsiLogAnalyzer.ExplainFile(null), "sin ruta");
            byte[] utf16SinBom = Encoding.Unicode.GetBytes("Error 1722. falla");
            Check(MsiLogAnalyzer.Decode(utf16SinBom, utf16SinBom.Length, false).StartsWith("Error 1722"), "UTF-16 sin BOM se detecta");
        }

        static void TestCarpetasYProcesos()
        {
            Check(RunningPrograms.IsSpecificFolder(@"C:\Program Files (x86)\Epic Games\"), "Epic Games");
            Check(RunningPrograms.IsSpecificFolder(@"C:\Program Files\Microsoft\Edge"), "Microsoft\\Edge");
            Check(RunningPrograms.IsSpecificFolder(@"C:\Users\fede\AppData\Local\Discord"), "AppData\\Local\\Discord");
            Check(RunningPrograms.IsSpecificFolder(@"C:\Users\fede\AppData\Local\Programs\Microsoft VS Code"), "AppData\\Local\\Programs\\X");
            Check(RunningPrograms.IsSpecificFolder(@"D:\Juegos\Foo"), "otra unidad");
            Check(RunningPrograms.IsSpecificFolder("\"C:\\Program Files\\7-Zip\""), "con comillas");
            foreach (string f in new[] { @"C:\", @"C:", @"C:\Program Files", @"C:\Program Files (x86)\", @"C:\Program Files\Common Files\Adobe",
                                         @"C:\Windows", @"C:\Windows\System32", @"C:\ProgramData", @"C:\Users", @"C:\Users\fede", @"C:\Users\fede\AppData",
                                         @"C:\Users\fede\AppData\Local", @"C:\Users\fede\AppData\Roaming", @"C:\Users\fede\AppData\Local\Programs",
                                         @"C:\Users\fede\AppData\Local\Temp\x", @"C:\Program Files\Microsoft", @"C:\Users\fede\Desktop", @"C:\Program Files\WindowsApps\X",
                                         @"C:\ProgramData\Package Cache\{X}", @"\\servidor\recurso\x", "", null, @"C:\Program Files\..\Windows" })
                Check(!RunningPrograms.IsSpecificFolder(f), "carpeta compartida o del sistema: " + (f ?? "null"));

            var a = new RunningProcess(); a.Pid = 10; a.Name = "AIMP.exe"; a.Path = @"C:\Program Files\AIMP\AIMP.exe";
            var b = new RunningProcess(); b.Pid = 11; b.Name = "AIMP.exe"; b.Path = @"C:\Program Files\AIMP\AIMP.exe";
            var s = new RunningProcess(); s.Pid = 12; s.Name = "AIMPSvc.exe"; s.Path = @"C:\Program Files\AIMP\AIMPSvc.exe"; s.IsService = true;
            var n = new RunningProcess(); n.Pid = 999991; n.Name = "AIMP2.exe"; n.Path = @"C:\Program Files\AIMP2\AIMP2.exe";
            var all = new List<RunningProcess> { a, b, s, n };
            Eq(3, RunningPrograms.In(all, @"C:\Program Files\AIMP").Count, "en la carpeta (no la de nombre parecido)");
            Eq(0, RunningPrograms.In(all, @"C:\Program Files").Count, "carpeta compartida: nada");
            Eq(2, RunningPrograms.Closable(RunningPrograms.In(all, @"C:\Program Files\AIMP")).Count, "los servicios no se cierran desde acá");
            Eq(2, RunningPrograms.ByName(all, new[] { "AIMP" }).Count, "por nombre, sin «.exe»");
            Eq("AIMP.exe, AIMPSvc.exe", RunningPrograms.Names(RunningPrograms.In(all, @"C:\Program Files\AIMP")), "nombres sin repetir");
            Eq(0, RunningPrograms.Close(new List<RunningProcess> { n }, 100).Count, "un proceso que ya no existe cuenta como cerrado");
            // Un proceso real sin ventana (como Epic Games Launcher en el área de notificación) se termina.
            if (Path.DirectorySeparatorChar == '/' && File.Exists("/bin/sleep"))
            {
                using (var real = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false }))
                {
                    var rp = new RunningProcess(); rp.Pid = real.Id; rp.Name = "sleep"; rp.Path = "/bin/sleep";
                    // Recién lanzado, en Linux el proceso puede llamarse todavía como el que lo creó (fork antes de exec): se
                    // espera a que sea «sleep» (1.7.4: la prueba fallaba de vez en cuando con el equipo cargado).
                    for (int i = 0; i < 40; i++)
                    {
                        try { using (var q = Process.GetProcessById(real.Id)) if (q.ProcessName == "sleep") break; } catch { }
                        Thread.Sleep(50);
                    }
                    var sw = Stopwatch.StartNew();
                    var quedan = RunningPrograms.Close(new List<RunningProcess> { rp }, 15000);
                    Check(quedan.Count == 0 && real.WaitForExit(5000), "proceso sin ventana: se termina");
                    Check(sw.ElapsedMilliseconds < 10000, "sin esperar el plazo completo (" + sw.ElapsedMilliseconds + " ms)");
                    // El PID de otro programa (el nombre no coincide) no se toca.
                    using (var otroReal = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false }))
                    {
                        var falso = new RunningProcess(); falso.Pid = otroReal.Id; falso.Name = "EpicGamesLauncher.exe"; falso.Path = "/x";
                        RunningPrograms.Close(new List<RunningProcess> { falso }, 100);
                        Check(!otroReal.HasExited, "PID reutilizado por otro programa: no se termina");
                        otroReal.Kill();
                    }
                }
            }
            Check(RunningPrograms.OpenHint("EpicGamesLauncher.exe").Contains("segundo plano"), "pista de programa abierto");
        }

        /// <summary>El resumen de la instalación cuenta todo: antes «0 correctas, 0 con error» con la Store sin novedades.</summary>
        static void TestResumenDeInstalacion()
        {
            var ok = Fila(UpdateStatus.Instalada, "Actualizada correctamente");
            var rb = Fila(UpdateStatus.RequiereReinicio, "Actualizada; requiere reiniciar");
            var mal = Fila(UpdateStatus.Fallida, "El instalador falló");
            var antes = Fila(UpdateStatus.Fallida, WingetProvider.RebootBeforeInstallText); antes.RestartBeforeRetry = true;
            var store = Fila(UpdateStatus.AlDia, "La Store no instaló nada");
            var office = Fila(UpdateStatus.Pendiente, "El actualizador de Office sigue trabajando en segundo plano");
            var cancel = Fila(UpdateStatus.Pendiente, InstallTally.CancelledBeforeStart);
            var cola = Fila(UpdateStatus.Pendiente, "En cola");
            var t = InstallTally.Of(new List<UpdateCandidate> { ok, rb, mal, antes, store, office, cancel, cola, null });
            Eq(2, t.Ok, "correctas (con la que requiere reinicio)");
            Eq(1, t.Reboot, "requieren reinicio");
            Eq(2, t.Failed, "con error");
            Eq(1, t.RestartFirst, "piden reiniciar antes");
            Eq(1, t.Unchanged, "sin cambios (la Store sin novedades)");
            Eq(1, t.Pending, "pendientes (Office)");
            Eq(2, t.NotStarted, "canceladas antes de empezar (y las que quedaron en cola)");
            string text = t.Text();
            Check(text.StartsWith("2 correctas, 2 con error") && text.Contains("1 sin cambios") && text.Contains("1 pendiente (termina") &&
                  text.Contains("2 canceladas antes de empezar") && text.EndsWith("."), "texto: " + text);

            var sola = InstallTally.Of(new List<UpdateCandidate> { store });
            Eq("0 correctas, 0 con error, 1 sin cambios (ya estaba al día).", sola.Text(), "sólo la Store sin novedades: ya no es «0 correctas, 0 con error»");
            Eq("1 correcta, 0 con error.", InstallTally.Of(new List<UpdateCandidate> { ok }).Text(), "singular");
            Eq("0 correctas, 0 con error.", InstallTally.Of(null).Text(), "sin elementos");
        }

        static UpdateCandidate Fila(UpdateStatus st, string result)
        {
            var c = new UpdateCandidate();
            c.Kind = UpdateKind.Programa; c.Name = "X"; c.Status = st; c.Result = result;
            return c;
        }

        /// <summary>Textos y lecturas más robustas.</summary>
        static void TestDetalles161()
        {
            Eq("", CatalogDriverProvider.RemovedText(0), "ningún paquete quitado: nada");
            Check(CatalogDriverProvider.RemovedText(1).Contains("Se quitó") && CatalogDriverProvider.RemovedText(1).Contains("el paquete") &&
                  !CatalogDriverProvider.RemovedText(1).Contains("los 1"), "uno: singular (antes «los 1 paquetes»)");
            Check(CatalogDriverProvider.RemovedText(3).Contains("los 3 paquetes"), "varios: plural");

            // Registro de Windows Installer con dígitos que no son ASCII: antes int.Parse lanzaba y se perdía el diagnóstico.
            var d = MsiLogAnalyzer.Analyze("MSI (s) (A0:B4) [10:00:01:234]: Product: X -- Error \u0661\u0666\u0660\u0663. Algo raro\r\n" +
                                           "MSI (s) (A0:B4) [10:00:01:235]: Product: X -- Error 1712. No se pudo restaurar\r\n");
            Eq(1712, d.Error, "un código ilegible se saltea y se toma el siguiente");

            // Microsoft Store después de «Detener»: no se le pide nada (antes se la disparaba igual).
            var ctx = ContextoSimulado(true);
            ctx.RequestCancel();
            var todas = new UpdateCandidate();
            todas.Kind = UpdateKind.Store; todas.Name = "Todas las apps de Microsoft Store"; todas.Identifier = StoreProvider.AllAppsId;
            todas.ProviderName = StoreProvider.ProviderId; todas.Installable = true; todas.Status = UpdateStatus.Pendiente; todas.Result = "En cola";
            int avisos = 0;
            new StoreProvider().Install(ctx, new List<UpdateCandidate> { todas }, delegate(UpdateCandidate u) { avisos++; });
            Check(todas.Status == UpdateStatus.Pendiente && todas.Result == InstallTally.CancelledBeforeStart && avisos == 1, "Store cancelada antes de empezar: " + todas.Result);
            Check(UpdateManager.CanInstall(todas), "y se puede volver a pedir");
        }
    }
}
