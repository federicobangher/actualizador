// Descargas: reanudación, servidores que cambian el archivo y carpetas temporales.
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
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

        static void TestLimpiezaDeTemporales()
        {
            // Una carpeta que se puede borrar: se borra al primer intento.
            string d = Path.Combine(Path.GetTempPath(), "sp153-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(d, "tmp"));
            File.WriteAllText(Path.Combine(d, "tmp", "delA016.tmp"), "x");
            var sw = Stopwatch.StartNew();
            BrowserProvider.TryDeleteDir(d);
            Check(!Directory.Exists(d) && sw.ElapsedMilliseconds < 3000, "se borra enseguida");
            BrowserProvider.TryDeleteDir(d);   // ya no existe: no falla
            BrowserProvider.TryDeleteDir(null);
            // La espera de procesos hijos con una carpeta vacía (recién creada, sin procesos) vuelve enseguida.
            // OJO: nunca se prueba con una carpeta real como %TEMP%: al vencerse el plazo cierra lo que corre ahí.
            string vacia = Path.Combine(Path.GetTempPath(), "sp153-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(vacia);
            try
            {
                sw = Stopwatch.StartNew();
                Check(ProcessRunner.WaitForFolderProcesses(vacia, 2000, "prueba") && sw.ElapsedMilliseconds < 5000, "sin procesos en la carpeta: no espera");
                Eq(0, ProcessRunner.ProcessesIn(vacia).Count, "ningún proceso corre desde una carpeta nueva");
                Eq(0, ProcessRunner.ProcessesIn(@"C:").Count, "la raíz del disco nunca cuenta como carpeta");
            }
            finally { try { Directory.Delete(vacia, true); } catch { } }
        }

        // ------------------------------------------------------------------ Auditoría 1.6.0: descargas

        static void TestAuditoriaDescargas()
        {
            var data = new byte[200000];
            new Random(11).NextBytes(data);
            var nuevo = new byte[210000];
            new Random(12).NextBytes(nuevo);
            Func<Uri, bool> local = delegate(Uri u) { return u.Host == "127.0.0.1"; };
            string dir = Path.Combine(Path.GetTempPath(), "sp-descarga160-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            System.Net.IWebProxy proxy = System.Net.WebRequest.DefaultWebProxy;
            int pause = Http.RetryPauseMs;
            System.Net.WebRequest.DefaultWebProxy = null;
            Http.RetryPauseMs = 50;
            try
            {
                // 1. Corte y, al retomar, el archivo del servidor cambió: If-Range hace que llegue el nuevo entero.
                using (var srv = new MiniServer(delegate(int n, long from, Stream s)
                {
                    if (n == 1) { Respond(s, "200 OK", new[] { "Content-Length: 200000", "ETag: \"v1\"", "Accept-Ranges: bytes" }, data, 0, 80000); return; }
                    Respond(s, "200 OK", new[] { "Content-Length: 210000", "ETag: \"v2\"" }, nuevo, 0, nuevo.Length);
                }))
                {
                    string file = Path.Combine(dir, "a.bin");
                    Http.Download(srv.Url("/a.bin"), file, 60000, null, local, null);
                    Check(SameBytes(file, nuevo), "archivo cambiado en el servidor: queda el nuevo entero, sin mezclar");
                    Check(srv.Heads.Count == 2 && srv.Heads[1].IndexOf("If-Range: \"v1\"", StringComparison.OrdinalIgnoreCase) >= 0, "al retomar se manda If-Range con el ETag");
                }

                // 2. 206 de otro tamaño total: es otro archivo, se empieza de cero.
                using (var srv = new MiniServer(delegate(int n, long from, Stream s)
                {
                    if (n == 1) { Respond(s, "200 OK", new[] { "Content-Length: 200000" }, data, 0, 50000); return; }
                    if (n == 2) { Respond(s, "206 Partial Content", new[] { "Content-Length: 10", "Content-Range: bytes 50000-50009/999999" }, data, 50000, 10); return; }
                    Respond(s, "200 OK", new[] { "Content-Length: 200000" }, data, 0, data.Length);
                }))
                {
                    string file = Path.Combine(dir, "b.bin");
                    Http.Download(srv.Url("/b.bin"), file, 60000, null, local, null);
                    Check(SameBytes(file, data), "206 de otro archivo: se descarga de nuevo y queda bien");
                    Check(srv.Ranges.Count == 3 && srv.Ranges[2] == -1, "el tercer pedido es desde cero (sin Range)");
                }

                // 3. 416 al retomar: se empieza de cero.
                using (var srv = new MiniServer(delegate(int n, long from, Stream s)
                {
                    if (n == 1) { Respond(s, "200 OK", new[] { "Content-Length: 200000" }, data, 0, 70000); return; }
                    if (n == 2) { Respond(s, "416 Range Not Satisfiable", new[] { "Content-Length: 0" }, null, 0, 0); return; }
                    Respond(s, "200 OK", new[] { "Content-Length: 200000" }, data, 0, data.Length);
                }))
                {
                    string file = Path.Combine(dir, "c.bin");
                    Http.Download(srv.Url("/c.bin"), file, 60000, null, local, null);
                    Check(SameBytes(file, data), "416: se descarga de nuevo desde el principio");
                    Eq(3, srv.Requests, "416: tres pedidos");
                }

                // 4. POST redirigido con 302: sigue como GET, sin reenviar el formulario.
                string cuerpo = "a=1&b=2";
                using (var srv = new MiniServer(delegate(int n, long from, Stream s)
                {
                    if (n == 1)
                    {
                        // El cuerpo (cuerpo.Length bytes) ya lo leyó MiniServer antes de llamar acá (1.7.1).
                        Respond(s, "302 Found", new[] { "Location: /fin", "Content-Length: 0" }, null, 0, 0);
                        return;
                    }
                    byte[] ok = Encoding.UTF8.GetBytes("listo");
                    Respond(s, "200 OK", new[] { "Content-Type: text/plain", "Content-Length: " + ok.Length }, ok, 0, ok.Length);
                }))
                {
                    string resp = Http.PostForm(srv.Url("/form"), cuerpo, 10000, local);
                    Eq("listo", resp, "respuesta del destino");
                    Check(srv.Heads.Count == 2 && srv.Heads[0].StartsWith("POST /form") && srv.Heads[1].StartsWith("GET /fin"), "el segundo pedido es GET: " + (srv.Heads.Count > 1 ? srv.Heads[1].Split('\r')[0] : ""));
                }
            }
            finally
            {
                System.Net.WebRequest.DefaultWebProxy = proxy;
                Http.RetryPauseMs = pause;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static void TestDescargaReanudable()
        {
            var data = new byte[300000];
            new Random(7).NextBytes(data);
            Func<Uri, bool> local = delegate(Uri u) { return u.Host == "127.0.0.1"; };
            string dir = Path.Combine(Path.GetTempPath(), "sp-descarga-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            IWebProxy proxy = WebRequest.DefaultWebProxy;
            int pause = Http.RetryPauseMs, pauseGet = Http.GetRetryPauseMs;
            WebRequest.DefaultWebProxy = null;      // 127.0.0.1 nunca por el proxy del sistema
            Http.RetryPauseMs = 50;
            Http.GetRetryPauseMs = 50;
            try
            {
                // 1. Se corta a los 120.000 bytes y el servidor permite retomar: se pide el resto con Range.
                using (var srv = new MiniServer(delegate(int n, long from, Stream s)
                {
                    if (n == 1) { Respond(s, "200 OK", new[] { "Content-Length: 300000", "Accept-Ranges: bytes" }, data, 0, 120000); return; }
                    long f = from < 0 ? 0 : from;
                    Respond(s, "206 Partial Content", new[] { "Content-Length: " + (data.Length - f), "Content-Range: bytes " + f + "-" + (data.Length - 1) + "/" + data.Length },
                            data, (int)f, data.Length - (int)f);
                }))
                {
                    string file = Path.Combine(dir, "a.bin");
                    Http.Download(srv.Url("/a.bin"), file, 60000, null, local, null);
                    Check(SameBytes(file, data), "corte a mitad: el archivo final es idéntico");
                    Eq(2, srv.Requests, "corte a mitad: un solo pedido extra");
                    Check(srv.Ranges.Count == 2 && srv.Ranges[1] == 120000, "corte a mitad: se retomó desde el byte 120000, no desde cero");
                }

                // 2. Se corta y el servidor NO retoma (responde 200 completo): se empieza de nuevo sin mezclar.
                using (var srv = new MiniServer(delegate(int n, long from, Stream s)
                {
                    Respond(s, "200 OK", new[] { "Content-Length: 300000" }, data, 0, n == 1 ? 100000 : data.Length);
                }))
                {
                    string file = Path.Combine(dir, "b.bin");
                    Http.Download(srv.Url("/b.bin"), file, 60000, null, local, null);
                    Check(SameBytes(file, data), "servidor que no retoma: se descarga entero de nuevo, sin bytes duplicados");
                    Eq(2, srv.Requests, "servidor que no retoma: dos pedidos");
                }

                // 3. Nunca llega nada: se rinde tras 3 intentos seguidos sin avanzar, con el servidor en el mensaje.
                using (var srv = new MiniServer(delegate(int n, long from, Stream s)
                {
                    Respond(s, "200 OK", new[] { "Content-Length: 300000" }, data, 0, 0);
                }))
                {
                    string msg = null;
                    try { Http.Download(srv.Url("/c.bin"), Path.Combine(dir, "c.bin"), 60000, null, local, null); }
                    catch (HttpFailureException hf) { msg = hf.Message; }
                    Check(msg != null && msg.Contains("127.0.0.1") && msg.Contains("3 intentos"), "sin avance: se rinde a los 3 intentos y dice dónde: " + msg);
                    Eq(3, srv.Requests, "sin avance: exactamente 3 pedidos");
                }

                // 4. 404: no se reintenta.
                using (var srv = new MiniServer(delegate(int n, long from, Stream s) { Respond(s, "404 Not Found", new[] { "Content-Length: 0" }, null, 0, 0); }))
                {
                    int code = 0;
                    try { Http.Download(srv.Url("/d.bin"), Path.Combine(dir, "d.bin"), 60000, null, local, null); }
                    catch (HttpFailureException hf) { code = hf.StatusCode; }
                    Eq(404, code, "404: código en la excepción");
                    Eq(1, srv.Requests, "404: un solo pedido");
                }

                // 5. Texto: un 503 se reintenta una vez; un 404 no (así se detecta la versión que falta en winget-pkgs).
                using (var srv = new MiniServer(delegate(int n, long from, Stream s)
                {
                    byte[] ok = Encoding.UTF8.GetBytes("PackageIdentifier: A.B");
                    if (n == 1) Respond(s, "503 Service Unavailable", new[] { "Content-Length: 0" }, null, 0, 0);
                    else Respond(s, "200 OK", new[] { "Content-Length: " + ok.Length, "Content-Type: text/plain" }, ok, 0, ok.Length);
                }))
                {
                    Eq("PackageIdentifier: A.B", Http.GetString(srv.Url("/m.yaml"), 20000, local, "text/plain"), "503 y después 200: se obtiene el texto");
                    Eq(2, srv.Requests, "503: un reintento");
                }
                using (var srv = new MiniServer(delegate(int n, long from, Stream s) { Respond(s, "404 Not Found", new[] { "Content-Length: 0" }, null, 0, 0); }))
                {
                    int code = 0;
                    try { Http.GetString(srv.Url("/no.yaml"), 20000, local, "text/plain"); }
                    catch (HttpFailureException hf) { code = hf.StatusCode; }
                    Eq(404, code, "texto 404: StatusCode para buscar otra versión");
                    Eq(1, srv.Requests, "texto 404: sin reintento");
                }

                // 6. Redirección fuera de lo permitido: se corta en ese salto.
                using (var srv = new MiniServer(delegate(int n, long from, Stream s) { Respond(s, "302 Found", new[] { "Location: http://evil.example/x.exe", "Content-Length: 0" }, null, 0, 0); }))
                {
                    bool blocked = false;
                    try { Http.Download(srv.Url("/e.bin"), Path.Combine(dir, "e.bin"), 60000, null, local, null); }
                    catch (InvalidOperationException ex) { blocked = ex.Message.Contains("no permitido"); }
                    Check(blocked, "redirección a un servidor no permitido: rechazada");
                }

                // 7. Progreso: porcentajes crecientes.
                using (var srv = new MiniServer(delegate(int n, long from, Stream s) { Respond(s, "200 OK", new[] { "Content-Length: 300000" }, data, 0, data.Length); }))
                {
                    var pcts = new List<int>();
                    Http.Download(srv.Url("/f.bin"), Path.Combine(dir, "f.bin"), 60000, null, local, delegate(int p, long b) { pcts.Add(p); });
                    Check(pcts.Count > 0 && pcts[pcts.Count - 1] == 100, "progreso hasta 100 %");
                }
            }
            finally
            {
                WebRequest.DefaultWebProxy = proxy;
                Http.RetryPauseMs = pause;
                Http.GetRetryPauseMs = pauseGet;
                try { Directory.Delete(dir, true); } catch { }
            }
        }
            // ------------------------------------------------------------------ 1.7.0: la red se corta en medio de la tanda

        /// <summary>Un puerto de 127.0.0.1 libre en este momento (nada escucha en él: conectar da «rechazada»).</summary>
        static int PuertoLibre()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        static void TestRedQueSeCorta170()
        {
            // Mensajes: si el servidor respondió antes, no es un filtro DNS: se cortó la red.
            string m = Http.DescribeNameFailure("www.catalog.update.microsoft.com", TimeSpan.FromMilliseconds(8), true, true);
            Check(m.Contains("se cortó la red") && m.Contains("respondía hace un momento") && !m.Contains("filtro DNS"), "nombre que dejó de resolver: " + m);
            m = Http.DescribeNameFailure("www.catalog.update.microsoft.com", TimeSpan.FromMilliseconds(8), true, false);
            Check(m.Contains("no existe") && m.Contains("filtro DNS"), "nunca respondió y el DNS dice que no existe: sigue la sospecha del filtro: " + m);
            m = Http.DescribeNameFailure("x", TimeSpan.FromMilliseconds(8), false, true);
            Check(m.StartsWith("sin conexión de red") && m.Contains("red con salida"), "sin adaptador con salida: " + m);
            m = Http.DescribeNameFailure("x", TimeSpan.FromSeconds(1), false);
            Check(m.Contains("sin conexión"), "la sobrecarga de antes sigue igual");
            Check(Http.NetworkUsable() || !Http.NetworkUsable(), "NetworkUsable no lanza en este sistema");

            Func<Uri, bool> local = delegate(Uri u) { return u.Host == "127.0.0.1"; };
            IWebProxy proxy = WebRequest.DefaultWebProxy;
            int espera = Http.NetworkWaitMs, sondeo = Http.NetworkPollMs, flush = Http.DnsFlushAfterMs, pausa = Http.GetRetryPauseMs, pausaDescarga = Http.RetryPauseMs;
            WebRequest.DefaultWebProxy = null;
            Http.NetworkWaitMs = 4000; Http.NetworkPollMs = 20; Http.DnsFlushAfterMs = 100; Http.GetRetryPauseMs = 20; Http.RetryPauseMs = 20;
            Http.WaitOnLoopbackForTests = true;
            Http.NetworkUsableForTests = delegate() { return true; };
            MiniServer vuelta = null;
            string dir = Path.Combine(Path.GetTempPath(), "sp170-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Http.ResetNetworkStateForTests();
                byte[] ok = Encoding.UTF8.GetBytes("catalogo");
                Responder responde = delegate(int n, long from, Stream st) { Respond(st, "200 OK", new[] { "Content-Type: text/html", "Content-Length: " + ok.Length }, ok, 0, ok.Length); };

                // 1. Nunca respondió (y hay red): falla como siempre, sin esperar. En Windows, conectar a un puerto cerrado
                //    tarda unos 2 s por intento (reintenta el SYN): el plazo de la prueba es el de la espera de red, que acá
                //    se agranda para distinguir «falló enseguida» de «esperó a la red» (1.7.1; antes fallaba en Windows).
                Http.NetworkWaitMs = 30000;
                int cerrado = PuertoLibre();
                int sondeos = 0;
                Http.HostBackForTests = delegate(string h) { sondeos++; return true; };
                var sw = Stopwatch.StartNew();
                string error = null;
                try { Http.GetString("http://127.0.0.1:" + cerrado + "/", 3000, local, "text/html"); }
                catch (HttpFailureException hf) { error = hf.Message; }
                Check(error != null && error.StartsWith("no se pudo conectar con 127.0.0.1") && sondeos == 0, "servidor que nunca respondió: falla sin esperar la red (" + error + ")");
                Check(sw.ElapsedMilliseconds < 15000, "y sin esperar la red (" + sw.ElapsedMilliseconds + " ms)");
                Http.NetworkWaitMs = 4000;

                // 2. Respondió y después se cayó la red (como el catálogo tras instalar el controlador del puerto PCI Express):
                //    se espera a que vuelva y el pedido sale bien.
                using (var srv = new MiniServer(responde))
                    Eq("catalogo", Http.GetString(srv.Url("/Search.aspx"), 3000, local, "text/html"), "primera consulta bien");
                Check(Http.ReachedBefore("127.0.0.1"), "se anotó que el servidor respondió");
                int puerto = PuertoLibre();
                sondeos = 0;
                Http.HostBackForTests = delegate(string h)
                {
                    sondeos++;
                    if (sondeos < 3) return false;                 // dos sondeos sin red
                    if (vuelta == null) vuelta = new MiniServer(responde, puerto);
                    return true;
                };
                string r = Http.PostForm("http://127.0.0.1:" + puerto + "/DownloadDialog.aspx", "updateIDs=x", 3000, local);
                Eq("catalogo", r, "después del corte, el diálogo de descarga sale bien");
                Check(sondeos >= 3, "se esperó a que volviera (" + sondeos + " sondeos)");
                vuelta.Dispose(); vuelta = null;

                // 3. Descarga que se corta porque se fue la red: también espera y la termina.
                int puerto2 = PuertoLibre();
                var datos = new byte[50000];
                new Random(170).NextBytes(datos);
                sondeos = 0;
                Http.HostBackForTests = delegate(string h)
                {
                    sondeos++;
                    if (sondeos < 2) return false;
                    if (vuelta == null) vuelta = new MiniServer(delegate(int n, long from, Stream st) { Respond(st, "200 OK", new[] { "Content-Length: " + datos.Length }, datos, 0, datos.Length); }, puerto2);
                    return true;
                };
                string archivo = Path.Combine(dir, "paquete0.cab");
                Http.Download("http://127.0.0.1:" + puerto2 + "/x.cab", archivo, 20000, null, local, null);
                Check(SameBytes(archivo, datos), "descarga terminada después de que volvió la red");
                vuelta.Dispose(); vuelta = null;

                // 4. La red no vuelve: el mensaje lo dice (sin culpar al filtro DNS), y no se espera de nuevo en cada pedido.
                Http.NetworkWaitMs = 300;
                Http.HostBackForTests = delegate(string h) { return false; };
                int puerto3 = PuertoLibre();
                error = null;
                try { Http.GetString("http://127.0.0.1:" + puerto3 + "/", 3000, local, "text/html"); }
                catch (HttpFailureException hf) { error = hf.Message; Check(!hf.Retryable, "no se reintenta después de esperar"); }
                Check(error != null && error.Contains("se cortó la red y no volvió") && error.Contains("reiniciá el equipo") && !error.Contains("filtro DNS"), "la red no volvió: " + error);
                sw = Stopwatch.StartNew();
                sondeos = 0;
                Http.HostBackForTests = delegate(string h) { sondeos++; return false; };
                try { Http.GetString("http://127.0.0.1:" + puerto3 + "/", 3000, local, "text/html"); } catch (HttpFailureException) { }
                Check(sondeos == 0, "después de una espera en vano no se vuelve a esperar enseguida");
                Check(Http.DescribeConnectFailure("127.0.0.1", new[] { IPAddress.Loopback }) == "no se pudo conectar con 127.0.0.1", "una dirección escrita tal cual no es un filtro DNS");

                // 5. Sin ningún adaptador con salida, también se espera aunque el servidor sea otro (hubo red antes).
                Http.ResetNetworkStateForTests();
                Http.NetworkUsableForTests = delegate() { return false; };
                var falla = new HttpFailureException("x", null, 0, WebExceptionStatus.NameResolutionFailure, false);
                Check(!Http.ShouldWaitForNetwork(falla, "www.aimp.ru"), "nunca hubo red en esta ejecución: no se espera (equipo sin Internet)");
                Http.MarkReached("github.com");
                Check(Http.ShouldWaitForNetwork(falla, "www.aimp.ru"), "hubo red y ya no hay adaptador con salida: se espera");
                Http.NetworkUsableForTests = delegate() { return true; };
                Check(!Http.ShouldWaitForNetwork(falla, "www.aimp.ru"), "con red y un servidor que nunca respondió: no se espera (puede ser un filtro DNS)");
                Check(!Http.ShouldWaitForNetwork(new HttpFailureException("x", null, 404, WebExceptionStatus.ProtocolError, false), "github.com"), "un 404 no es un corte de red");
                Check(!Http.ShouldWaitForNetwork(new HttpFailureException("x", null, 0, WebExceptionStatus.SecureChannelFailure, false), "github.com"), "un error de HTTPS no es un corte de red");
                Http.WaitOnLoopbackForTests = false;
                Http.MarkReached("127.0.0.1");
                Check(!Http.ShouldWaitForNetwork(new HttpFailureException("x", null, 0, WebExceptionStatus.ConnectFailure, true), "127.0.0.1"), "un servidor local caído no es «la red»");

                // 5b. Respondía, no conecta, pero hay red y el nombre resuelve: es el servidor, no se espera.
                Http.WaitOnLoopbackForTests = true;
                Http.HostBackForTests = delegate(string h) { return true; };
                Check(!Http.ShouldWaitForNetwork(new HttpFailureException("x", null, 0, WebExceptionStatus.ConnectFailure, true), "127.0.0.1"), "servidor caído con la red bien: no se espera");
                Http.HostBackForTests = delegate(string h) { return false; };
                Check(Http.ShouldWaitForNetwork(new HttpFailureException("x", null, 0, WebExceptionStatus.ConnectFailure, true), "127.0.0.1"), "no conecta y el nombre no resuelve: se espera");
                Http.WaitOnLoopbackForTests = false;

                // 6. Cancelar corta la espera.
                Http.NetworkWaitMs = 60000;
                Http.HostBackForTests = delegate(string h) { return false; };
                sw = Stopwatch.StartNew();
                int llamadas = 0;
                Check(!Http.WaitForNetwork("github.com", delegate() { return ++llamadas > 3; }, null) && sw.ElapsedMilliseconds < 5000, "cancelar corta la espera");
            }
            finally
            {
                if (vuelta != null) vuelta.Dispose();
                WebRequest.DefaultWebProxy = proxy;
                Http.NetworkWaitMs = espera; Http.NetworkPollMs = sondeo; Http.DnsFlushAfterMs = flush; Http.GetRetryPauseMs = pausa; Http.RetryPauseMs = pausaDescarga;
                Http.WaitOnLoopbackForTests = false;
                Http.NetworkUsableForTests = null;
                Http.HostBackForTests = null;
                Http.ResetNetworkStateForTests();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static void TestOrdenControladores170()
        {
            // Lo que puede cortar la red, al final; entre los demás se respeta el orden.
            var audio = Controlador("Realtek High Definition Audio", "Realtek", "MEDIA", "6.0.9000.1", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897");
            var red = Controlador("Realtek Gaming GbE Family Controller", "Realtek", "Net", "10.70.1.1", @"PCI\VEN_10EC&DEV_8125");
            var puerto = Controlador("Intel(R) PCI Express Root Port #11 - 43B2", "Intel", "System", "10.1.30.1", @"PCI\VEN_8086&DEV_43B2");
            var video = Controlador("Intel(R) UHD Graphics 630", "Intel", "Display", "31.0.101.2100", @"PCI\VEN_8086&DEV_3E92");
            var mei = Controlador("Intel(R) Management Engine Interface #1", "Intel", "System", "2400.1.1.0", @"PCI\VEN_8086&DEV_43E0");
            var apo = Controlador("Realtek Audio Effects Component", "Realtek", "SoftwareComponent", "13.0.6000.1", @"SWC\VEN_10EC&AID_0001");
            var orden = CatalogDriverProvider.InstallOrderOf(new List<DriverInfo> { puerto, audio, red, video, mei, apo });
            var nombres = new List<string>();
            foreach (var d in orden) nombres.Add(d.DeviceName);
            Eq("Realtek High Definition Audio|Realtek Audio Effects Component|Intel(R) UHD Graphics 630|Intel(R) PCI Express Root Port #11 - 43B2|Intel(R) Management Engine Interface #1|Realtek Gaming GbE Family Controller",
               string.Join("|", nombres.ToArray()), "orden: audio y componentes, video, puerto PCI Express y chipset, y la placa de red al final");
            Eq(3, CatalogDriverProvider.NetworkImpact(red), "placa de red");
            Eq(2, CatalogDriverProvider.NetworkImpact(puerto), "puerto PCI Express (reinicia lo que cuelga de él)");
            Eq(0, CatalogDriverProvider.NetworkImpact(null), "sin controlador");

            // Las descargas se piden TODAS antes de instalar la primera: con dos controladores cuyos diálogos no sirven,
            // se piden los dos diálogos y ninguno queda «Instalando».
            var ctx = new UpdateContext();
            ctx.Os = new OsInfo(); ctx.Os.NativeArchitecture = "x64"; ctx.Os.Major = 10; ctx.Os.Build = 26100; ctx.Os.IsWindows = true;
            ctx.IsAdmin = true;
            var filas = new List<UpdateCandidate>();
            foreach (var d in new[] { puerto, red })
            {
                var c = new UpdateCandidate();
                c.Kind = UpdateKind.Controlador; c.Name = d.DeviceName; c.ProviderName = CatalogDriverProvider.ProviderId;
                c.Status = UpdateStatus.Disponible; c.Installable = true;
                var e = new CatalogEntry(); e.Guid = Guid.NewGuid().ToString("D"); e.Title = d.DeviceName; e.Version = "99.0.0.1";
                c.ProviderTag = new CatalogPlan { Driver = d, Entry = e };
                filas.Add(c);
            }
            var dialogos = new List<string>();
            CatalogDriverProvider.DownloadDialogForTests = delegate(string body) { dialogos.Add(body); return "downloadInformation[0].updateID ='otro';"; };
            try
            {
                new CatalogDriverProvider().Install(ctx, filas, delegate(UpdateCandidate c) { });
                Eq(2, dialogos.Count, "se pidieron las dos descargas");
                Check(filas.TrueForAll(delegate(UpdateCandidate c) { return c.Status == UpdateStatus.Fallida && c.Result.Contains("no publicó"); }), "las dos fallan con su motivo, ninguna queda a medias");
                ctx.RequestCancel();
                foreach (var c in filas) c.Status = UpdateStatus.Disponible;
                new CatalogDriverProvider().Install(ctx, filas, delegate(UpdateCandidate c) { });
                Check(filas.TrueForAll(delegate(UpdateCandidate c) { return c.Status == UpdateStatus.Pendiente && c.Result.Contains("Cancelada"); }), "cancelado antes de empezar");
            }
            finally { CatalogDriverProvider.DownloadDialogForTests = null; }

            // El código: la instalación (pnputil) está separada de la descarga y viene después de descargar todo.
            string fuente = File.ReadAllText(ProjectFile(Path.Combine("src", Path.Combine("Actualizador.Engine", Path.Combine("Updates", "CatalogDriverProvider.cs")))));
            int descargar = fuente.IndexOf("var p = Prepare(ctx, c, plan, changed);", StringComparison.Ordinal);
            int instalar = fuente.IndexOf("try { Apply(ctx, c, p, changed); }", StringComparison.Ordinal);
            Check(descargar > 0 && instalar > descargar, "Install: primero Prepare para todos, después Apply");
        }

        static void TestHttps170()
        {
            // AIMP en Windows 11 decía «en Windows 7, instalá .NET Framework 4.8…».
            string m = Http.TlsFailureText("www.aimp.ru", WebExceptionStatus.SecureChannelFailure, null, false, true);
            Check(!m.Contains("Windows 7") && m.Contains("www.aimp.ru") && m.Contains("también con TLS 1.2"), "Windows 11, canal seguro: " + m);
            m = Http.TlsFailureText("www.aimp.ru", WebExceptionStatus.TrustFailure, null, false, false);
            Check(!m.Contains("Windows 7") && m.Contains("no confía") && m.Contains("antivirus"), "Windows 11, certificado: " + m);
            m = Http.TlsFailureText("www.aimp.ru", WebExceptionStatus.SecureChannelFailure, null, true, false);
            Check(m.Contains("Windows 7") && m.Contains(".NET Framework 4.8"), "Windows 7: el consejo sigue: " + m);
            m = Http.TlsFailureText("www.aimp.ru", WebExceptionStatus.TrustFailure, "está vencido o todavía no es válido", false, false);
            Eq("el certificado HTTPS de www.aimp.ru está vencido o todavía no es válido", m, "con el motivo del certificado");

            var vencido = new X509ChainStatus(); vencido.Status = X509ChainStatusFlags.NotTimeValid;
            var raiz = new X509ChainStatus(); raiz.Status = X509ChainStatusFlags.UntrustedRoot;
            var revocado = new X509ChainStatus(); revocado.Status = X509ChainStatusFlags.Revoked;
            var sinCrl = new X509ChainStatus(); sinCrl.Status = X509ChainStatusFlags.RevocationStatusUnknown;
            m = Http.DescribeCertificateProblem(SslPolicyErrors.RemoteCertificateChainErrors, new[] { vencido }, "R11");
            Check(m.Contains("vencido") && m.Contains("«R11»") && m.Contains("fecha y la hora"), "vencido: " + m);
            m = Http.DescribeCertificateProblem(SslPolicyErrors.RemoteCertificateChainErrors, new[] { raiz }, "Avast Web/Mail Shield Root");
            Check(m.Contains("no es de confianza") && m.Contains("Avast Web/Mail Shield Root") && m.Contains("inspecciona HTTPS"), "raíz desconocida (antivirus que inspecciona HTTPS): " + m);
            Check(Http.DescribeCertificateProblem(SslPolicyErrors.RemoteCertificateChainErrors, new[] { sinCrl, revocado }, "").StartsWith("fue revocado"), "revocado gana");
            Check(Http.DescribeCertificateProblem(SslPolicyErrors.RemoteCertificateChainErrors, new[] { sinCrl }, "").Contains("revocado"), "sin poder verificar la revocación");
            Check(Http.DescribeCertificateProblem(SslPolicyErrors.RemoteCertificateNameMismatch, null, "").Contains("otro sitio"), "nombre que no coincide");
            Check(Http.DescribeCertificateProblem(SslPolicyErrors.RemoteCertificateNotAvailable, null, "").Contains("no presentó"), "sin certificado");

            // La verificación de certificados que se instala para el diagnóstico decide lo MISMO que .NET sin ella.
            Http.EnableModernTls();
            var cb = ServicePointManager.ServerCertificateValidationCallback;
            Check(cb != null, "diagnóstico de certificados instalado");
            if (cb != null)
            {
                Check(cb(null, null, null, SslPolicyErrors.None), "sin errores: se acepta");
                Check(!cb(null, null, null, SslPolicyErrors.RemoteCertificateChainErrors), "con errores de cadena: se rechaza");
                Check(!cb(null, null, null, SslPolicyErrors.RemoteCertificateNameMismatch), "con otro nombre: se rechaza");
            }
            Http.EnableModernTls();
            Check(ServicePointManager.ServerCertificateValidationCallback == cb, "se instala una sola vez");
        }
    }
}
