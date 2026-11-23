// 1.6.9: robustez en equipos con servicios de Windows colgados, consumo de recursos y programas que no se actualizaban.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Actualizador.Core;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {

        // ------------------------------------------------------------------ WMI con plazo real
        static void TestWmiConPlazo169()
        {
            Eq(150000, Wmi.HardLimitMs(60), "60 s por objeto → 150 s en total");
            Eq(390000, Wmi.HardLimitMs(180), "180 s (controladores) → 390 s");
            Eq(40000, Wmi.HardLimitMs(0), "nunca menos de 5 s por objeto");
            string ns = @"root\prueba169";
            Wmi.Reset(ns);
            Check(Wmi.BreakerOpen(ns) == null, "sin cuelgues: se consulta");
            Wmi.Trip(ns);
            Check(Wmi.BreakerOpen(ns) == null, "un cuelgue: se sigue consultando");
            Wmi.Trip(ns);
            string open = Wmi.BreakerOpen(ns);
            Check(open != null && open.Contains("no responde"), "dos cuelgues seguidos: no se vuelve a consultar por un rato");
            string err;
            var rows = Wmi.TryQuery(ns, "SELECT * FROM Algo", out err, 5);
            Check(rows.Count == 0 && err != null && err.Contains("no responde"), "TryQuery responde enseguida con el motivo: " + err);
            Check(Wmi.BreakerOpen(@"root\otro169") == null, "los demás espacios de nombres no se ven afectados");
            Wmi.Reset(ns);
            Check(Wmi.BreakerOpen(ns) == null, "una consulta que responde lo restablece");
            int old = Wmi.BreakerMinutes;
            try
            {
                Wmi.BreakerMinutes = 0;
                Wmi.Trip(ns); Wmi.Trip(ns);
                Check(Wmi.BreakerOpen(ns) == null, "pasado el tiempo, se vuelve a probar");
            }
            finally { Wmi.BreakerMinutes = old; Wmi.Reset(ns); }
        }

        // ------------------------------------------------------------------ Procesos sin WMI
        static void TestProcesosSinWmi169()
        {
            var me = Process.GetCurrentProcess();
            int self = me.Id;
            try
            {
                ProcessList.ForTests = delegate
                {
                    var l = new List<ProcessEntry>();
                    l.Add(new ProcessEntry { Pid = 4, Name = "System", Path = "" });
                    l.Add(new ProcessEntry { Pid = 100, ParentPid = 4, Name = "AIMP.exe", Path = @"C:\Program Files\AIMP\AIMP.exe", SessionId = 1 });
                    l.Add(new ProcessEntry { Pid = 101, ParentPid = 100, Name = "AIMPSvc.exe", Path = @"C:\Program Files\AIMP\AIMPSvc.exe", SessionId = 0 });
                    l.Add(new ProcessEntry { Pid = 102, Name = "AIMP2.exe", Path = @"C:\Program Files\AIMP2\AIMP2.exe" });
                    l.Add(new ProcessEntry { Pid = 103, Name = "protegido.exe", Path = "" });
                    l.Add(new ProcessEntry { Pid = self, Name = "Actualizador.exe", Path = @"C:\Program Files\AIMP\Actualizador.exe" });
                    l.Add(new ProcessEntry { Pid = 200, Name = "setup.tmp", Path = @"C:\Datos\descargas\Actualizador-x\tmp\setup.tmp" });
                    return l;
                };
                ProcessList.ServicePidsForTests = delegate { return new HashSet<int> { 101 }; };

                var snap = RunningPrograms.Snapshot();
                Eq(4, snap.Count, "sin System, sin los que no tienen ruta y sin el propio Actualizador");
                var svc = snap.Find(delegate(RunningProcess p) { return p.Pid == 101; });
                Check(svc != null && svc.IsService && svc.SessionId == 0, "el servicio se marca con la API de servicios (no WMI)");
                var aimp = snap.Find(delegate(RunningProcess p) { return p.Pid == 100; });
                Check(aimp != null && !aimp.IsService && aimp.SessionId == 1 && aimp.Name == "AIMP.exe", "proceso común con su sesión");
                Eq(2, RunningPrograms.In(snap, @"C:\Program Files\AIMP").Count, "en la carpeta: el programa y su servicio");
                Eq(1, RunningPrograms.Closable(RunningPrograms.In(snap, @"C:\Program Files\AIMP")).Count, "se cierra sólo el que no es servicio");

                var dentro = ProcessRunner.ProcessesIn(@"C:\Datos\descargas\Actualizador-x");
                Check(dentro.Count == 1 && dentro[0].Key == 200 && dentro[0].Value == "setup.tmp", "instalación en segundo plano en la carpeta de la descarga");
                Eq(0, ProcessRunner.ProcessesIn(@"C:\Datos\descargas\Actualizador-y").Count, "otra carpeta: nada (no confunde prefijos)");
                Eq(0, ProcessRunner.ProcessesIn("").Count, "sin carpeta: nada");

                var t0 = Stopwatch.StartNew();
                Check(ProcessRunner.WaitForFolderProcesses(@"C:\Datos\descargas\Actualizador-otra", 5000, "prueba"), "sin procesos en la carpeta: no se espera");
                Check(t0.ElapsedMilliseconds < 3000, "y vuelve enseguida");
            }
            finally
            {
                ProcessList.ForTests = null;
                ProcessList.ServicePidsForTests = null;
            }
            // Lectura real: en Windows con la API; fuera de Windows, el respaldo (WMI) no rompe nada.
            var real = ProcessList.Snapshot(true);
            Check(real != null, "la lectura real nunca devuelve null");
            Check(ProcessList.ServicePids() != null, "los servicios tampoco");
        }

        // ------------------------------------------------------------------ Salida acotada de los procesos
        static void TestSalidaAcotada169()
        {
            var t = new ProcessRunner.CappedText(2048);
            for (int i = 0; i < 10; i++) t.AppendLine("línea " + i);
            string chico = t.ToString();
            Check(chico.Contains("línea 0") && chico.Contains("línea 9") && !chico.Contains("omitidas"), "salida chica: completa");

            t = new ProcessRunner.CappedText(2048);
            t.AppendLine("Nombre   Id   Versión");
            for (int i = 0; i < 20000; i++) t.AppendLine("  ██████████▒▒▒▒▒  " + i + " MB / 300 MB");
            t.AppendLine("Installer failed with exit code: 1603");
            string s = t.ToString();
            Check(s.StartsWith("Nombre   Id   Versión"), "se conserva el principio (las tablas)");
            Check(s.Contains("Installer failed with exit code: 1603"), "y el final (el resultado)");
            Check(s.Contains("líneas omitidas"), "lo del medio se omite y se dice");
            Check(s.Length < 2048 + ProcessRunner.TailLines * 60, "la memoria queda acotada (" + s.Length + " caracteres)");
            Eq("1603", WingetProvider.InstallerExitCode(s), "el código del instalador se sigue leyendo");
        }

        // ------------------------------------------------------------------ Búsqueda de actualizaciones con plazo y en orden
        sealed class ProveedorFalso : IUpdateProvider
        {
            public string Id;
            public int Millis;
            public int UnavailableMillis;
            public bool Heavy;
            public static int Concurrent, MaxConcurrent;
            public bool SawLowPriority;
            public string Name { get { return Id; } }
            public string Unavailable(UpdateContext ctx) { if (UnavailableMillis > 0) Thread.Sleep(UnavailableMillis); return null; }
            public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes)
            {
                SawLowPriority = ProcessRunner.LowPriorityForThisThread;
                int now = Interlocked.Increment(ref Concurrent);
                lock (typeof(ProveedorFalso)) if (now > MaxConcurrent) MaxConcurrent = now;
                try
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < Millis) Thread.Sleep(20);
                    var c = new UpdateCandidate();
                    c.Kind = UpdateKind.Programa; c.Name = "De " + Id; c.Status = UpdateStatus.AlDia; c.ProviderName = Id;
                    output.Add(c);
                    notes.Add("nota de " + Id);
                }
                finally { Interlocked.Decrement(ref Concurrent); }
            }
            public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed) { }
        }

        static void TestBusquedaConPlazo169()
        {
            Eq(1, UpdateManager.HeavyConcurrencyFor(1), "un núcleo: una búsqueda pesada por vez");
            Eq(1, UpdateManager.HeavyConcurrencyFor(2), "dos núcleos: una por vez");
            Eq(2, UpdateManager.HeavyConcurrencyFor(4), "cuatro núcleos: dos a la vez");
            Eq(2, UpdateManager.HeavyConcurrencyFor(16), "más: dos a la vez");
            Check(UpdateManager.IsHeavy(WingetProvider.ProviderId) && UpdateManager.IsHeavy(WindowsUpdateProvider.ProviderId), "winget y Windows Update son pesadas");
            Check(!UpdateManager.IsHeavy(CatalogDriverProvider.ProviderId) && !UpdateManager.IsHeavy(BrowserProvider.ProviderId), "las consultas web no");

            var oldLimit = UpdateManager.ScanLimit;
            int oldHeavy = UpdateManager.MaxHeavyConcurrent;
            try
            {
                // Un proveedor colgado no deja sin resultados a los demás.
                UpdateManager.ScanLimit = TimeSpan.FromMilliseconds(1500);
                var rapido = new ProveedorFalso { Id = BrowserProvider.ProviderId, Millis = 50 };
                var colgado = new ProveedorFalso { Id = WindowsUpdateProvider.ProviderId, Millis = 7000 };
                var man = new UpdateManager(new IUpdateProvider[] { rapido, colgado });
                man.Ignored = new IgnoreList();
                var ctx = ContextoSimulado(true);
                ctx.DriversLoaded = true;
                ctx.Os.Major = 6; ctx.Os.Minor = 1;      // sin Store: no se lee Get-AppxPackage
                var report = UpdateSession.Create(ctx);
                var sw = Stopwatch.StartNew();
                man.Scan(ctx, report);
                Check(sw.ElapsedMilliseconds < 5900, "la búsqueda termina al vencer el plazo (" + sw.ElapsedMilliseconds + " ms)");
                Check(report.Updates.Exists(delegate(UpdateCandidate u) { return u.Name == "De " + BrowserProvider.ProviderId; }), "con los resultados del que respondió");
                Check(!report.Updates.Exists(delegate(UpdateCandidate u) { return u.Name == "De " + WindowsUpdateProvider.ProviderId; }), "sin tocar las listas del que sigue colgado");
                Check(report.UpdateNotes.Exists(delegate(string n) { return n.Contains("Windows Update no terminó"); }), "y lo explica en las notas");
                Check(rapido.SawLowPriority && colgado.SawLowPriority, "las búsquedas lanzan sus procesos con prioridad baja");
                Check(!ProcessRunner.LowPriorityForThisThread, "el hilo que busca (en la consola, también instala) queda como estaba");

                // Un proveedor que ni siquiera contesta si está disponible (servicio de Windows Update trabado al arrancar).
                var oldAvail = UpdateManager.AvailabilityLimit;
                try
                {
                    UpdateManager.AvailabilityLimit = TimeSpan.FromMilliseconds(500);
                    var trabado = new ProveedorFalso { Id = WindowsUpdateProvider.ProviderId, UnavailableMillis = 5000 };
                    var sano = new ProveedorFalso { Id = BrowserProvider.ProviderId, Millis = 10 };
                    var man3 = new UpdateManager(new IUpdateProvider[] { trabado, sano });
                    man3.Ignored = new IgnoreList();
                    var report3 = UpdateSession.Create(ctx);
                    sw = Stopwatch.StartNew();
                    man3.Scan(ctx, report3);
                    Check(sw.ElapsedMilliseconds < 4000, "un proveedor que no dice si está disponible no traba la búsqueda (" + sw.ElapsedMilliseconds + " ms)");
                    Check(report3.UpdateNotes.Exists(delegate(string n) { return n.Contains("Windows Update no respondió"); }), "y queda la nota");
                    Check(report3.Updates.Exists(delegate(UpdateCandidate u) { return u.Name == "De " + BrowserProvider.ProviderId; }), "los demás buscan igual");
                }
                finally { UpdateManager.AvailabilityLimit = oldAvail; }

                // Las pesadas, de a una cuando se pide.
                UpdateManager.ScanLimit = TimeSpan.FromMinutes(1);
                UpdateManager.MaxHeavyConcurrent = 1;
                ProveedorFalso.Concurrent = 0; ProveedorFalso.MaxConcurrent = 0;
                Thread.Sleep(200);
                var a = new ProveedorFalso { Id = WingetProvider.ProviderId, Millis = 400 };
                var b = new ProveedorFalso { Id = WindowsUpdateProvider.ProviderId, Millis = 400 };
                var man2 = new UpdateManager(new IUpdateProvider[] { a, b });
                man2.Ignored = new IgnoreList();
                // El proveedor colgado de antes puede seguir corriendo: se espera a que termine para medir bien.
                Thread.Sleep(6000);
                ProveedorFalso.Concurrent = 0; ProveedorFalso.MaxConcurrent = 0;
                var report2 = UpdateSession.Create(ctx);
                man2.Scan(ctx, report2);
                Eq(1, ProveedorFalso.MaxConcurrent, "winget y Windows Update de a una en equipos chicos");
                Eq(2, report2.Updates.FindAll(delegate(UpdateCandidate u) { return u.Name.StartsWith("De "); }).Count, "y las dos terminan");
            }
            finally
            {
                UpdateManager.ScanLimit = oldLimit;
                UpdateManager.MaxHeavyConcurrent = oldHeavy;
            }
        }

        // ------------------------------------------------------------------ Windows Update y punto de restauración con plazo
        static void TestWindowsUpdateConPlazo169()
        {
            bool slow;
            object r = WindowsUpdateProvider.CallWithLimit(delegate { return "listo"; }, TimeSpan.FromSeconds(5), out slow);
            Check(!slow && (string)r == "listo", "llamada que responde: su resultado");
            var sw = Stopwatch.StartNew();
            r = WindowsUpdateProvider.CallWithLimit(delegate { Thread.Sleep(6000); return "tarde"; }, TimeSpan.FromMilliseconds(300), out slow);
            Check(slow && r == null && sw.ElapsedMilliseconds < 5000, "llamada colgada: se deja de esperar al vencer el plazo");
            bool lanzo = false;
            try { WindowsUpdateProvider.CallWithLimit(delegate { throw new InvalidOperationException("0x80240022"); }, TimeSpan.FromSeconds(5), out slow); }
            catch (InvalidOperationException ex) { lanzo = ex.Message == "0x80240022"; }
            Check(lanzo, "el error de Windows Update se propaga igual que antes");
            Check(WindowsUpdateProvider.DownloadLimit >= TimeSpan.FromMinutes(60) && WindowsUpdateProvider.InstallLimit >= TimeSpan.FromMinutes(90), "plazos amplios (acumulativas con disco rígido)");

            var ctx = ContextoSimulado(false);
            Check(!UpdateManager.CreateRestorePoint(ctx, "x"), "punto de restauración sin administrador: se omite como antes");
            Check(UpdateManager.RestorePointLimit <= TimeSpan.FromMinutes(10), "el punto de restauración tiene plazo");
        }

        // ------------------------------------------------------------------ Microsoft Store: medir sin abrir PowerShell cada 15 s
        static void TestStoreSinPowerShell169()
        {
            var antes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            antes["Microsoft.WindowsCalculator"] = "11.2405.2.0";
            antes["Microsoft.Paint"] = "11.2404.1020.0";
            antes["SpotifyAB.SpotifyMusic"] = "1.230.1135.0";
            var ahora = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ahora["Microsoft.WindowsCalculator"] = "11.2409.0.0";
            ahora["Microsoft.Paint"] = "11.2404.1020.0";
            ahora["SpotifyAB.SpotifyMusic"] = "1.229.0.0";
            ahora["Nueva.App"] = "1.0.0.0";
            var ch = StoreProvider.Changes(antes, ahora);
            Eq(1, ch.Count, "sólo lo que subió de versión");
            Eq("11.2405.2.0 → 11.2409.0.0", ch["Microsoft.WindowsCalculator"], "con las dos versiones");
            Eq(0, StoreProvider.Changes(null, ahora).Count, "sin la foto de antes: nada");
            Eq(0, StoreProvider.Changes(antes, null).Count, "sin la de ahora: nada");
            // El lector rápido del registro: en Windows lee las claves; fuera, no rompe.
            var reg = InventoryLoader.LoadAppxVersionsRegistry();
            Check(reg != null, "versiones de la Store desde el registro (nunca null)");
        }

        // ------------------------------------------------------------------ Identificadores de winget
        static void TestIdentificadoresWinget169()
        {
            Check(WingetProvider.IsSafeId("Microsoft.Edge") && WingetProvider.IsSafeId("Notepad++.Notepad++") && WingetProvider.IsSafeId("9NKSQGP7F2NH"), "los de siempre");
            Check(WingetProvider.IsSafeId("Editorial.Aplicación"), "con letras acentuadas (winget las acepta)");
            Check(WingetProvider.IsSafeId("ByteDance.飞书"), "con letras de otros idiomas");
            Check(WingetProvider.IsSafeId("Contoso.App(Beta)"), "con paréntesis");
            Check(WingetProvider.IsSafeId("Contoso.App,Tools"), "con coma");
            foreach (string malo in new[] { "Foo&calc", "Foo^x.Bar", "Foo%PATH%.Bar", "Foo!x.Bar", "-Foo.Bar", ".Foo", "Foo Bar.Baz", "Foo\"x.Bar",
                                            "Foo/x.Bar", "Foo\\x.Bar", "Foo:x.Bar", "Foo|x.Bar", "Foo<x.Bar", "Foo>x.Bar", "Foo*.Bar", "Foo?.Bar", "Foo\tx.Bar",
                                            "TheDocumentFoundation.LibreOf…", "TheDocumentFoundation.LibreOf...", new string('a', 300) })
                Check(!WingetProvider.IsSafeId(malo), "rechazado: " + (malo.Length > 40 ? malo.Substring(0, 40) + "…" : malo));
            Check(WingetProvider.IsAsciiId("Microsoft.Edge") && !WingetProvider.IsAsciiId("Editorial.Aplicación"), "para direcciones web, sólo ASCII");
            Check(WingetRules.ManifestUrl("Editorial.Aplicación", "1.0", false) == null, "un identificador no ASCII no arma una dirección de GitHub sin codificar");
            Eq("Editorial.Aplicaci", WingetProvider.TruncatedPrefix("Editorial.Aplicaci…"), "prefijo recortado con acentos");
        }

        // ------------------------------------------------------------------ Instaladores que no admiten administrador (Spotify)
        static void TestSinElevar169()
        {
            Check(WingetProvider.ProhibitsElevation(0x8A150056) && WingetProvider.ProhibitsElevation(0x8A15007D), "códigos de «no admite administrador»");
            Check(!WingetProvider.ProhibitsElevation(0x8A150006) && !WingetProvider.ProhibitsElevation(0), "los demás no");

            string elevadoArgs = null, sinElevarArgs = null;
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            wp.RunForTests = delegate(string args)
            {
                elevadoArgs = args;
                return Salida(0x8A150056, "Found Spotify [Spotify.Spotify] Version 1.2.60\nThe installer cannot be run from an administrator context.\n");
            };
            try
            {
                Unelevated.RunForTests = delegate(string file, string args) { sinElevarArgs = args; var r = new ProcResult(); r.Started = true; r.ExitCode = 0; return r; };
                var c = FilaWinget("Spotify", "Spotify.Spotify", "1.2.50", "1.2.60");
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c }, delegate(UpdateCandidate u) { });
                Check(sinElevarArgs != null && sinElevarArgs.Contains("--id \"Spotify.Spotify\"") && sinElevarArgs.Contains("--version \"1.2.60\""), "se repite sin elevar, con la misma versión fijada: " + sinElevarArgs);
                Eq(UpdateStatus.Instalada, c.Status, "elevado + instalador que no admite administrador: se actualiza igual (Spotify)");

                // Sin administrador no hace falta: no se repite.
                sinElevarArgs = null;
                var c2 = FilaWinget("Spotify", "Spotify.Spotify", "1.2.50", "1.2.60");
                wp.Install(ContextoSimulado(false), new List<UpdateCandidate> { c2 }, delegate(UpdateCandidate u) { });
                Check(sinElevarArgs == null && c2.Status == UpdateStatus.Fallida, "sin elevar ya: no se repite y queda el error de winget");

                // No se pudo pedir al Explorador: el motivo y qué hacer.
                Unelevated.RunForTests = delegate(string file, string args) { var r = new ProcResult(); r.StartError = "no hay escritorio del Explorador"; return r; };
                var c3 = FilaWinget("Spotify", "Spotify.Spotify", "1.2.50", "1.2.60");
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c3 }, delegate(UpdateCandidate u) { });
                Check(c3.Status == UpdateStatus.Fallida && c3.Result.Contains("no admite ejecutarse como administrador") && c3.Result.Contains("sin elevar"),
                      "sin Explorador: lo explica: " + c3.Result);

                // Sin el código de salida (no se encontró el proceso): se confirma con la versión que quedó.
                Unelevated.RunForTests = delegate(string file, string args) { var r = new ProcResult(); r.Started = true; r.ExitCode = Unelevated.UnknownExit; return r; };
                wp.ListForTests = delegate(string args) { return Salida(0, "Name    Id               Version Available Source\n-------------------------------------------------\nSpotify Spotify.Spotify  1.2.50  1.2.60    winget\n"); };
                var c4 = FilaWinget("Spotify", "Spotify.Spotify", "1.2.50", "1.2.60");
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c4 }, delegate(UpdateCandidate u) { });
                Eq(UpdateStatus.Fallida, c4.Status, "sin código y sin cambio de versión: NO se da por actualizada");
                wp.ListForTests = delegate(string args) { return Salida(0, "Name    Id               Version Source\n-----------------------------------------\nSpotify Spotify.Spotify  1.2.60  winget\n"); };
                var c5 = FilaWinget("Spotify", "Spotify.Spotify", "1.2.50", "1.2.60");
                wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { c5 }, delegate(UpdateCandidate u) { });
                Eq(UpdateStatus.Instalada, c5.Status, "sin código pero con la versión nueva instalada: actualizada");
            }
            finally { Unelevated.RunForTests = null; }
        }

        // ------------------------------------------------------------------ Firmas, proxy, registro y arquitectura
        static void TestDetalles169()
        {
            Check(Native.NeedsChainDownload(unchecked((int)0x800B0109)) && Native.NeedsChainDownload(unchecked((int)0x800B010A)), "raíz o intermedio que falta: se reintenta armando la cadena");
            Check(!Native.NeedsChainDownload(0) && !Native.NeedsChainDownload(Native.CERT_E_REVOKED) && !Native.NeedsChainDownload(unchecked((int)0x80096010)),
                  "firma válida, revocada o adulterada: no se reintenta");

            Check(InventoryLoader.EntryExists(null) == null, "sin programa: no se sabe");
            var p = new InstalledProgram(); p.RegistryKey = @"HKXX\algo";
            Check(InventoryLoader.EntryExists(p) == null, "ruta con otra forma: se usa la lectura completa");
            p.RegistryKey = @"HKLM\SOFTWARE\Otra\Cosa";
            Check(InventoryLoader.EntryExists(p) == null, "fuera de Uninstall: se usa la lectura completa");

            // 407: el mensaje dice que es el proxy.
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var server = new Thread(delegate()
            {
                try
                {
                    using (var cl = listener.AcceptTcpClient())
                    using (var st = cl.GetStream())
                    {
                        var buf = new byte[4096];
                        st.Read(buf, 0, buf.Length);
                        byte[] resp = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"x\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        st.Write(resp, 0, resp.Length);
                    }
                }
                catch { }
            });
            server.IsBackground = true;
            server.Start();
            string msg = null;
            try { Http.GetString("http://127.0.0.1:" + port + "/x", 10000, delegate(Uri u) { return true; }, "text/plain"); }
            catch (HttpFailureException ex) { msg = ex.Message; Check(ex.StatusCode == 407 && !ex.Retryable, "407: no se reintenta"); }
            catch (Exception ex) { msg = ex.GetType().Name + ": " + ex.Message; }
            listener.Stop();
            Check(msg != null && msg.Contains("proxy"), "407 explica que es el proxy de la red: " + msg);
        }

        // ------------------------------------------------------------------ Orígenes de winget dañados
        static void TestOrigenesDanados169()
        {
            Check(WingetProvider.IsSourceErrorCode(0x8A15000F) && WingetProvider.IsSourceErrorCode(0x8A15000B), "códigos de orígenes dañados");
            Check(!WingetProvider.IsSourceErrorCode(0x8A150014), "«no hay resultados» no es un origen dañado");
            Check(WingetProvider.IsSourceProblem(Salida(0x8A150001, "Failed when opening source(s); try the 'source reset' command if the problem persists.")), "lo reconoce por el texto");
            Check(!WingetProvider.IsSourceProblem(Salida(0x8A150001, "Error interno")), "otro error no");
            Check(WingetProvider.Describe(0x8A15000F).Contains("source reset"), "el mensaje dice cómo repararlo");

            string[] cuatro = { "Nombre", "Id", "Versión", "Disponible" };
            string upgrade = TablaWinget(cuatro, new[] { "7-Zip", "7zip.7zip", "24.07", "25.01" }) + "1 actualizaciones disponibles.\n";
            string list = TablaWinget(cuatro, new[] { "7-Zip", "7zip.7zip", "24.07", "25.01" });
            foreach (bool admin in new[] { true, false })
            {
                var consultas = new List<string>();
                bool reparado = false;
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.QueryForTests = delegate(string args)
                {
                    consultas.Add(args);
                    if (args.StartsWith("source update --name")) { reparado = true; return Salida(0, "Done"); }
                    if (args.StartsWith("source reset")) return Salida(0, "Resetting source: winget");
                    if (args.StartsWith("source update")) return Salida(0x8A15000F, "Data required by the source is missing");
                    if (!reparado) return Salida(0x8A15000F, "Failed when searching source: winget\nAn unexpected error occurred while executing the command:\n0x8a15000f : Data required by the source is missing");
                    return Salida(0, args.StartsWith("upgrade") ? upgrade : list);
                };
                var ctx = ContextoSimulado(admin);
                var salida = new List<UpdateCandidate>();
                var notas = new List<string>();
                wp.Scan(ctx, salida, notas);
                if (admin)
                {
                    Check(consultas.Exists(delegate(string a) { return a.StartsWith("source reset --name winget --force"); }), "como administrador: se restablece SÓLO el origen «winget»");
                    Check(!consultas.Exists(delegate(string a) { return a.StartsWith("source reset --force"); }), "nunca se borran los orígenes propios de la empresa");
                    var z = salida.Find(delegate(UpdateCandidate x) { return x.Identifier == "7zip.7zip"; });
                    Check(z != null && z.Status == UpdateStatus.Disponible, "después de reparar, la búsqueda encuentra la actualización");
                    Check(notas.Exists(delegate(string n) { return n.Contains("se restableció el origen oficial"); }), "y lo cuenta en las notas");
                }
                else
                {
                    Check(!consultas.Exists(delegate(string a) { return a.StartsWith("source reset"); }), "sin administrador no se intenta restablecer");
                    Check(notas.Exists(delegate(string n) { return n.Contains("dañados") && n.Contains("administrador"); }), "y explica qué hacer");
                }
            }
        }
    }
}
