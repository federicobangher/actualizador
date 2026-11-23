// Pruebas de la 2.0.21. Capturas del usuario (21/11/2026, con la 2.0.20):
//  - pc6: Vigía (winget como servicio) dice «3 programas con versión nueva» (Microsoft.Outlook, Microsoft.Teams, PDF24) y el
//    Actualizador «Nada para actualizar»;
//  - pc7: «Roblox Player 0.740.0.7400927 → 0.747 · Falló la instalación · El instalador oficial de Roblox terminó (código …)»;
//  - el pedido de que consuma menos en equipos viejos.
using System;
using System.Collections.Generic;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static readonly string[] CabWinget2021 = { "Nombre", "Id", "Versión", "Disponible", "Origen" };

        /// <summary>La tarea que corre winget como servicio: el guion, la tarea, de dónde sale winget.exe y su código de salida.</summary>
        static void TestWingetComoServicio2021()
        {
            const string wg = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\winget.exe";
            string s = SystemWinget.Script(wg, "upgrade --include-unknown --source winget --accept-source-agreements --disable-interactivity",
                                           @"C:\Windows\Temp\Actualizador-1\salida.txt", @"C:\Windows\Temp\Actualizador-1\codigo.txt");
            Check(s.Contains("\"" + wg + "\" upgrade --include-unknown --source winget") && s.Contains("> \"C:\\Windows\\Temp\\Actualizador-1\\salida.txt\" 2>&1"),
                  "winget con la salida al archivo: " + s);
            // 2.0.23: la redirección adelante («echo 0> archivo» era redirigir la entrada: el código 0 se perdía).
            Check(s.Contains(">\"C:\\Windows\\Temp\\Actualizador-1\\codigo.txt.tmp\" echo %ERRORLEVEL%") && !s.Contains("%ERRORLEVEL%>") && s.Contains("move /y"),
                  "el código al final, con la redirección adelante, y recién entonces el archivo (no se lee a medias)");
            string x = SystemWinget.TaskXml(@"C:\Windows\System32\cmd.exe", @"C:\Windows\Temp\Actualizador-1\winget.cmd");
            Check(x.Contains("<UserId>S-1-5-18</UserId>"), "como servicio (SYSTEM), igual que el agente de Vigía");
            Check(x.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>") && x.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"), "también con batería (notebooks)");
            Check(x.Contains("<Hidden>true</Hidden>") && x.Contains("<Priority>7</Priority>") && !x.Contains("<Triggers>"), "oculta, prioridad baja y sólo a pedido");
            Check(x.Contains("<Arguments>/d /c &quot;C:\\Windows\\Temp\\Actualizador-1\\winget.cmd&quot;</Arguments>"), "el guion, con las comillas escapadas en el XML");
            const string pf = @"C:\Program Files";
            Check(SystemWinget.IsTrustedExe(wg, pf), "el winget.exe de App Installer en WindowsApps sirve");
            Check(!SystemWinget.IsTrustedExe(@"C:\Users\Fede\AppData\Local\Microsoft\WindowsApps\winget.exe", pf), "el alias de la carpeta del usuario no (se ejecuta como SYSTEM)");
            Check(!SystemWinget.IsTrustedExe(@"C:\Program Files\WindowsApps\Otra.App_1.0_x64__abc\winget.exe", pf), "otro paquete no");
            Check(!SystemWinget.IsTrustedExe(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0_x64__8wekyb3d8bbwe\..\..\x\winget.exe", pf), "con «..» no");
            Eq(-1978335212, SystemWinget.ParseExit("-1978335212\r\n"), "el código negativo de winget (0x8A150014)");
            Eq(-1, SystemWinget.ParseExit(""), "sin código: -1");
        }

        static WingetProvider WingetPc6(List<string> consultas, List<string> comoServicio, string servicioTabla)
        {
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            string list = TablaWinget(CabWinget2021,
                new[] { "Outlook (new)", "Microsoft.Outlook", "1.2026.812.100", "", "winget" },
                new[] { "Microsoft Teams", "Microsoft.Teams", "26288.2004.5269.3420", "", "winget" },
                new[] { "PDF24 Creator 11.35.0", "geeksoftwareGmbH.PDF24Creator", "11.35.0", "", "winget" },
                new[] { "Claude", "Anthropic.Claude", "6.28899.2", "", "winget" });
            wp.QueryForTests = delegate(string args)
            {
                consultas.Add(args);
                if (args.StartsWith("upgrade")) return Salida(0x8A15002B, "No se encontró ninguna actualización disponible.");
                if (args.StartsWith("list")) return Salida(0, list);
                return Salida(0x8A150014, "");
            };
            SystemWinget.RunForTests = delegate(string args)
            {
                comoServicio.Add(args);
                if (servicioTabla == null) return new ProcResult { StartError = "no se pudo crear la tarea programada" };
                return Salida(0, servicioTabla);
            };
            return wp;
        }

        /// <summary>La captura de pc6: lo que ve Vigía aparece en el Actualizador.</summary>
        static void TestCapturaVigiaPc6_2021()
        {
            var oldSys = SystemWinget.RunForTests;
            try
            {
                string vigia = TablaWinget(CabWinget2021,
                    new[] { "Microsoft.OutlookForWindows", "Microsoft.Outlook", "1.2025.829.200", "1.2026.812.100", "winget" },
                    new[] { "MSTeams", "Microsoft.Teams", "25255.703.3978.7153", "26288.2004.5269.3420", "winget" },
                    new[] { "PDF24 Creator 11.34.0", "geeksoftwareGmbH.PDF24Creator", "11.34.0", "11.35.0", "winget" });
                var consultas = new List<string>();
                var servicio = new List<string>();
                var wp = WingetPc6(consultas, servicio, vigia);
                var salida = new List<UpdateCandidate>();
                var notas = new List<string>();
                wp.Scan(ContextoSimulado(true), salida, notas);
                Check(servicio.Count == 1 && servicio[0] == "upgrade --include-unknown --source winget --accept-source-agreements --disable-interactivity",
                      "se consultó winget como servicio, como Vigía: " + string.Join(" | ", servicio.ToArray()));
                Check(!consultas.Exists(delegate(string a) { return a.Contains("--scope machine"); }), "como servicio anduvo: no hace falta «--scope machine» (un proceso de winget menos)");
                var equipo = salida.FindAll(delegate(UpdateCandidate c) { return c.Name.EndsWith(WingetProvider.MachineSuffix); });
                Eq(3, equipo.Count, "los tres de Vigía: " + string.Join(", ", equipo.ConvertAll(delegate(UpdateCandidate c) { return c.Name; }).ToArray()));
                var outlook = equipo.Find(delegate(UpdateCandidate c) { return c.Identifier == "Microsoft.Outlook"; });
                // 2.0.24: con el mismo nombre que muestra Vigía.
                Check(outlook != null && outlook.Name == "Microsoft.OutlookForWindows" + WingetProvider.MachineSuffix && outlook.InstalledVersion == "1.2025.829.200" &&
                      outlook.AvailableVersion == "1.2026.812.100" && outlook.Status == UpdateStatus.Disponible && outlook.Recommended,
                      "Outlook: con el nombre que muestra Vigía, disponible y marcado");
                Check(outlook != null && ((WingetRow)outlook.ProviderTag).SystemContext && outlook.Source.Contains("como servicio") && outlook.Source.Contains("Vigía"), "se instala como servicio, igual que se vio");
                Check(notas.Exists(delegate(string n) { return n.Contains("Vigía"); }), "la nota lo explica");

                // Sin Programador de tareas (o sin administrador): «--scope machine», como en la 2.0.20.
                consultas.Clear(); servicio.Clear();
                wp = WingetPc6(consultas, servicio, null);
                salida.Clear();
                wp.Scan(ContextoSimulado(true), salida, new List<string>());
                Check(consultas.Exists(delegate(string a) { return a.StartsWith("upgrade --scope machine"); }), "si como servicio no se puede, «--scope machine»");

                // Sin administrador, ninguna de las dos.
                consultas.Clear(); servicio.Clear();
                wp = WingetPc6(consultas, servicio, vigia);
                wp.Scan(ContextoSimulado(false), new List<UpdateCandidate>(), new List<string>());
                Check(servicio.Count == 0 && !consultas.Exists(delegate(string a) { return a.Contains("--scope machine"); }), "sin administrador: no");
            }
            finally { SystemWinget.RunForTests = oldSys; }
        }

        /// <summary>La instalación como servicio: «--scope machine» (los MSIX), o sin él para un programa común (PDF24), y se confirma ahí.</summary>
        static void TestInstalarComoServicio2021()
        {
            var oldSys = SystemWinget.RunForTests;
            try
            {
                var w = new WingetRow { Name = "MSTeams", Id = "Microsoft.Teams", Version = "25255.703.3978.7153", Available = "26288.2004.5269.3420", Source = "winget" };
                var output = new List<UpdateCandidate>();
                WingetProvider.AddMachineRows(new List<WingetRow> { w }, null, null, output, true);
                var c = output[0];
                var llamadas = new List<string>();
                string version = "25255.703.3978.7153";
                SystemWinget.RunForTests = delegate(string args)
                {
                    llamadas.Add(args);
                    if (args.StartsWith("upgrade")) { version = "26288.2004.5269.3420"; return Salida(0, "Se instaló correctamente"); }
                    return Salida(0, TablaWinget(CabWinget2021, new[] { "MSTeams", "Microsoft.Teams", version, "", "winget" }));
                };
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.RunForTests = delegate(string args) { llamadas.Add("USUARIO " + args); return Salida(0, ""); };
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(llamadas.Count == 2 && llamadas[0].StartsWith("upgrade --id \"Microsoft.Teams\" --exact --accept") && !llamadas[0].Contains("--scope") && llamadas[1].StartsWith("list --id \"Microsoft.Teams\" --exact"),
                      "2.0.22: como servicio, tal cual se vio (sin --scope), y se confirma como servicio: " + string.Join(" | ", llamadas.ToArray()));
                Check(c.Status == UpdateStatus.Instalada && c.Result.Contains("como servicio"), "instalada: " + c.Result);

                // PDF24 (programa común): sin instalador «para todo el equipo» con --scope → se repite sin él.
                var p = new WingetRow { Name = "PDF24 Creator 11.34.0", Id = "geeksoftwareGmbH.PDF24Creator", Version = "11.34.0", Available = "11.35.0", Source = "winget" };
                output.Clear();
                WingetProvider.AddMachineRows(new List<WingetRow> { p }, null, null, output, true);
                c = output[0];
                llamadas.Clear();
                string pdf = "11.34.0";
                SystemWinget.RunForTests = delegate(string args)
                {
                    llamadas.Add(args);
                    // La captura de pc7: sin --scope, «winget no encontró el paquete instalado»; con --scope machine, sí.
                    if (args.StartsWith("upgrade") && !args.Contains("--scope machine")) return Salida(0x8A150014, "No se encontró ningún paquete instalado que coincida con los criterios de entrada.");
                    if (args.StartsWith("upgrade")) { pdf = "11.35.0"; return Salida(0, "Instalado correctamente"); }
                    return Salida(0, TablaWinget(CabWinget2021, new[] { "PDF24 Creator " + pdf, "geeksoftwareGmbH.PDF24Creator", pdf, "", "winget" }));
                };
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(llamadas.Count == 3 && !llamadas[0].Contains("--scope") && llamadas[1].Contains("--exact --scope machine"),
                      "si sin --scope no lo encuentra, con --scope machine: " + string.Join(" | ", llamadas.ToArray()));
                Check(c.Status == UpdateStatus.Instalada, "PDF24 actualizado como servicio: " + c.Result);
            }
            finally { SystemWinget.RunForTests = oldSys; }
        }

        /// <summary>pc7: el instalador de Roblox se vuelve a abrir solo; se espera a que termine de verdad.</summary>
        static void TestRobloxSeReabre2021()
        {
            const string log = "1795280000 IN_PROGRESS 3668 Installer started\n1795280001 IN_PROGRESS 18820 Restarted\n1795280001 IN_PROGRESS 18820 Runner thread started\n" +
                               "1795280005 IN_PROGRESS 18820 Download started\n";
            Check(RobloxPlayer.LogVerdict(log, 1795279990) == null, "«Restarted» y sigue en curso: todavía no terminó");
            Eq("OK", RobloxPlayer.LogVerdict(log + "1795280040 IN_PROGRESS 18820 Install completed\n1795280040 SUCCESS 18820 Installer success\n", 1795279990), "«SUCCESS»: terminó bien");
            Check(RobloxPlayer.LogVerdict("1762114132 SUCCESS 18820 Installer success\n", 1795279990) == null, "un «SUCCESS» de una instalación anterior no cuenta");
            string err = RobloxPlayer.LogVerdict(log + "1795280050 FAILED 18820 Download failed: HttpError\n", 1795279990);
            Check(err != null && err.StartsWith("ERR:") && err.Contains("Download failed"), "un error se informa: " + err);

            var oldV = RobloxPlayer.VersionsForTests;
            var oldI = RobloxPlayer.InstallForTests;
            var oldW = RobloxPlayer.WaitForTests;
            var oldManifest = WingetDirect.ManifestForTests;
            try
            {
                string yaml = System.IO.File.ReadAllText(System.IO.Path.Combine(samples, "winget_manifest_roblox_0747.yaml"));
                WingetDirect.ManifestForTests = delegate(string u) { return u.EndsWith("/Roblox/Roblox/0.747/Roblox.Roblox.installer.yaml") ? yaml : null; };
                var versiones = new Dictionary<string, string> { { @"...\version-old\RobloxPlayerBeta.exe", "0, 740, 0, 7400927" } };
                RobloxPlayer.VersionsForTests = delegate(string root) { return new Dictionary<string, string>(versiones); };
                // El primer proceso termina enseguida (código 0) sin cambiar nada; el que se reabrió termina después.
                RobloxPlayer.InstallForTests = delegate(string url, bool visible) { return 0; };
                bool esperado = false;
                RobloxPlayer.WaitForTests = delegate { esperado = true; versiones[@"...\version-4ea8e85481e34f46\RobloxPlayerBeta.exe"] = "0, 747, 0, 7470829"; return "OK"; };
                var w = new WingetRow { Name = "Roblox Player", Id = "Roblox.Roblox", Version = "Unknown", Available = "0.747", Source = "winget" };
                var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Roblox Player", ProviderName = WingetProvider.ProviderId, ProviderTag = w, Status = UpdateStatus.Disponible };
                RobloxPlayer.Resolve(c, w, new List<InstalledProgram>());
                Eq("0.740.0.7400927", c.InstalledVersion, "la de la captura");
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(esperado && c.Status == UpdateStatus.Instalada && c.InstalledVersion == "0.747.0.7470829", "esperó al instalador que se reabrió y quedó la 0.747: " + c.Result);

                // Si su registro dice que falló, se dice eso.
                versiones.Remove(@"...\version-4ea8e85481e34f46\RobloxPlayerBeta.exe");
                RobloxPlayer.WaitForTests = delegate { return "ERR: Download failed: HttpError"; };
                c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Roblox Player", ProviderName = WingetProvider.ProviderId, ProviderTag = w, Status = UpdateStatus.Disponible };
                RobloxPlayer.Resolve(c, w, new List<InstalledProgram>());
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("Download failed"), "el error del instalador, en la fila: " + c.Result);
            }
            finally { RobloxPlayer.VersionsForTests = oldV; RobloxPlayer.InstallForTests = oldI; RobloxPlayer.WaitForTests = oldW; WingetDirect.ManifestForTests = oldManifest; }
        }

        /// <summary>Equipos viejos: menos tareas a la vez, el mismo resultado.</summary>
        static void TestModoLiviano2021()
        {
            Check(Performance.Detect(2, 4096) && Performance.Detect(4, 16384) && Performance.Detect(8, 4096), "2 o 4 procesadores, o menos de 6 GB: liviano");
            Check(!Performance.Detect(8, 16384) && !Performance.Detect(6, 0), "8 procesadores y 16 GB (o memoria desconocida con 6): normal");
            bool was = Performance.Light;
            try
            {
                Performance.Light = true;
                Eq(1, WingetProvider.SearchWorkers, "liviano: una búsqueda de winget por vez (cada una es un proceso)");
                Eq(2, Performance.Workers(6, 2), "liviano: dos páginas a la vez");
                Performance.Light = false;
                Eq(4, WingetProvider.SearchWorkers, "normal: cuatro");

                // El resultado es el mismo en los dos modos (la captura de pc6).
                var oldSys = SystemWinget.RunForTests;
                try
                {
                    string vigia = TablaWinget(CabWinget2021, new[] { "MSTeams", "Microsoft.Teams", "25255.703.3978.7153", "26288.2004.5269.3420", "winget" });
                    var resultados = new List<int>();
                    foreach (bool liviano in new[] { false, true })
                    {
                        Performance.Light = liviano;
                        var salida = new List<UpdateCandidate>();
                        WingetPc6(new List<string>(), new List<string>(), vigia).Scan(ContextoSimulado(true), salida, new List<string>());
                        resultados.Add(salida.FindAll(delegate(UpdateCandidate c) { return c.Status == UpdateStatus.Disponible; }).Count);
                    }
                    Check(resultados[0] == resultados[1] && resultados[0] == 1, "liviano o no, lo mismo: " + resultados[0] + " / " + resultados[1]);
                }
                finally { SystemWinget.RunForTests = oldSys; }
            }
            finally { Performance.Light = was; }
            Check(WingetProvider.SearchCacheLife >= TimeSpan.FromDays(3), "las búsquedas por nombre se guardan 3 días (menos procesos de winget en cada búsqueda)");
        }

        static void TestVersion2021()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.21") >= 0, "Actualizador 2.0.21 o posterior: " + AppInfo.Version);
        }
    }
}
