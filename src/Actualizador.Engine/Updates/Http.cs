using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Falla de red ya explicada en castellano, con el servidor involucrado. Antes el mensaje era
    /// «sin conexión a Internet (no se resolvió el nombre)» aunque winget estuviera descargando en
    /// ese mismo momento: no decía qué servidor ni si el DNS no respondió o respondió que no existe.
    /// </summary>
    public sealed class HttpFailureException : Exception
    {
        /// <summary>Código HTTP de la respuesta; 0 si no hubo respuesta.</summary>
        public readonly int StatusCode;
        public readonly WebExceptionStatus Status;
        /// <summary>¿Vale la pena reintentar? (cortes y demoras sí; DNS, 404 y TLS no)</summary>
        public readonly bool Retryable;

        public HttpFailureException(string message, Exception inner, int statusCode, WebExceptionStatus status, bool retryable)
            : base(message, inner)
        {
            StatusCode = statusCode;
            Status = status;
            Retryable = retryable;
        }
    }

    /// <summary>HTTPS mínimo (.NET Framework 4.x) restringido a los servidores que decide cada llamador.</summary>
    public static class Http
    {
        /// <summary>Tiempo para conectar y recibir los encabezados de cada pedido de descarga.</summary>
        public const int ConnectTimeoutMs = 30000;
        /// <summary>Si durante este tiempo no llega ningún byte, la conexión se da por colgada y se retoma.</summary>
        public const int StallTimeoutMs = 90000;
        /// <summary>Reconexiones seguidas sin avanzar un solo byte antes de rendirse.</summary>
        public const int MaxReconnectsWithoutProgress = 2;

        /// <summary>Pausa antes de retomar una descarga cortada (las pruebas la acortan).</summary>
        internal static int RetryPauseMs = 5000;
        /// <summary>Pausa antes del único reintento de una consulta de texto.</summary>
        internal static int GetRetryPauseMs = 2000;

        /// <summary>
        /// Sólo TLS 1.2 y 1.3. Antes se AGREGABA TLS 1.2 a lo que hubiera, y en .NET 4.0/4.5 lo que
        /// hay por defecto incluye SSL 3.0 y TLS 1.0. Los servidores consultados soportan TLS 1.2.
        /// </summary>
        public static readonly string UserAgent = "Mozilla/5.0 (compatible; " + AppInfo.Name + "/" + AppInfo.Version + "; Windows)";

        /// <summary>
        /// 2.0.18: el que se presenta como herramienta de descargas, no como navegador. SourceForge, a todo lo que parece un navegador
        /// («Mozilla/5.0…»), le responde una página («Your download will start shortly…») que arranca la descarga con JavaScript; a
        /// una herramienta de descargas (winget, wget, curl) le contesta con la redirección al servidor espejo que tiene el archivo.
        /// Con «Mozilla» en el nombre, la 2.0.17 nunca obtenía el instalador de CrystalDiskInfo y terminaba abriendo su página.
        /// </summary>
        public static readonly string DownloaderAgent = AppInfo.Name + "/" + AppInfo.Version + " (Windows; descargas)";

        /// <summary>El User-Agent para ese servidor: el de herramienta de descargas donde un navegador recibe una página en vez del archivo.</summary>
        public static string UserAgentFor(Uri u)
        {
            if (u == null) return UserAgent;
            string h = u.Host.ToLowerInvariant();
            bool sourceForge = h == "sourceforge.net" || h.EndsWith(".sourceforge.net") || h == "sf.net" || h.EndsWith(".sf.net") ||
                               h == "sourceforge.io" || h.EndsWith(".sourceforge.io");
            return sourceForge ? DownloaderAgent : UserAgent;
        }

        public static void EnableModernTls()
        {
            InstallCertificateDiagnostics();
            // TLS 1.3 sólo donde Windows lo trae (Windows 11 y Server 2022 en adelante). En Windows 10 y
            // anteriores, pedirlo con .NET Framework 4.8 puede hacer fallar el canal seguro entero
            // («No se puede crear un canal seguro SSL/TLS»), aunque TLS 1.2 funcione. Si un servidor ya
            // falló con TLS 1.3 en esta ejecución, se sigue sólo con TLS 1.2.
            if (!tls13Disabled && SupportsTls13(OsBuild))
            {
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); return; }   // Tls12 | Tls13
                catch (Exception qx) { Log.Quiet("TLS 1.3 no disponible", qx); }
            }
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }                      // Tls12
            catch (Exception ex) { Log.Warn("TLS 1.2 no disponible: " + ex.Message); }
        }

        /// <summary>¿Este Windows trae TLS 1.3 en su pila (Schannel)? Windows 11 (22000) y Server 2022 (20348) o posteriores.</summary>
        public static bool SupportsTls13(int windows10Build)
        {
            return windows10Build >= 20348;
        }

        static int osBuild = -1;

        /// <summary>Compilación real de Windows 10/11 (RtlGetVersion); 0 en Windows 8.1 o anteriores y fuera de Windows.</summary>
        static int OsBuild
        {
            get
            {
                if (osBuild >= 0) return osBuild;
                int b = 0;
                try
                {
                    var os = OsInfo.Detect();
                    if (os.IsWindows && os.Major >= 10) b = os.Build;
                }
                catch (Exception qx) { Log.Quiet("Versión de Windows para elegir TLS", qx); }
                osBuild = b;
                return b;
            }
        }

        /// <summary>Cualquier servidor, pero sólo por HTTPS y en el puerto estándar (también en cada redirección).</summary>
        public static bool IsHttps(Uri u)
        {
            return u != null && u.IsAbsoluteUri && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.Host.Length > 0;
        }

        static HttpWebRequest Create(Uri u, int timeoutMs, Func<Uri, bool> allowed)
        {
            if (!allowed(u)) throw new InvalidOperationException("dirección no permitida: " + u.Host);
            var req = (HttpWebRequest)WebRequest.Create(u);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = Math.Min(timeoutMs, 120000);
            // «Mozilla/5.0 (compatible; …)»: algunos sitios de fabricantes detrás de una CDN con
            // protección contra bots rechazan lo que no empieza así. Igual se identifica como Actualizador.
            req.UserAgent = UserAgentFor(u);
            req.AllowAutoRedirect = false;   // cada salto se valida acá (HTTPS y dominio permitido)
            // Proxy de la empresa que pide usuario: se le presentan las credenciales de Windows, como hacen el
            // navegador y winget. Sin esto cada consulta terminaba en «407» y en esas redes no se verificaba nada.
            try { if (req.Proxy != null && req.Proxy.Credentials == null) req.Proxy.Credentials = CredentialCache.DefaultCredentials; }
            catch (Exception qx) { Log.Quiet("Credenciales del proxy", qx); }
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return req;
        }

        /// <summary>
        /// Sigue las redirecciones a mano, validando CADA salto: con la redirección automática sólo
        /// se podía revisar el destino final, y un salto intermedio por HTTP sin cifrar dejaba que
        /// alguien en la red eligiera el siguiente destino.
        /// </summary>
        /// <summary>
        /// 2.0.45 (captura del usuario, 26/11/2026 20:55): Epic Online Services 5.8.0 «Reinstalación cancelada: demasiadas redirecciones».
        /// Su dirección de descarga (onlineservices.epicgames.com/api/cosmos/sdk/download) redirige poniendo una cookie y espera recibirla
        /// en el salto siguiente; sin cookies vuelve a redirigir hasta agotar los saltos (winget y el navegador la guardan). Ahora cada
        /// pedido guarda las cookies entre SUS saltos (sólo para ese pedido; cada cookie, para su dominio), se permiten hasta
        /// <see cref="MaxHops"/> saltos y, si igual se agotan, el error dice por dónde pasó.
        /// </summary>
        public const int MaxHops = 12;

        static HttpWebResponse Send(string url, int timeoutMs, Func<Uri, bool> allowed, Action<HttpWebRequest> prepare)
        {
            var u = new Uri(url);
            var jar = new CookieContainer();
            var path = new List<string>();
            for (int hop = 0; hop <= MaxHops; hop++)
            {
                var req = Create(u, timeoutMs, allowed);
                req.CookieContainer = jar;
                HttpWebResponse resp;
                var sw = Stopwatch.StartNew();
                // prepare va dentro del try: en un POST escribe el cuerpo, y eso ya conecta (y puede fallar).
                try { if (prepare != null) prepare(req); resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException we)
                {
                    resp = we.Response as HttpWebResponse;
                    if (resp != null) MarkReached(u.Host);      // respondió (aunque sea con un error): hay red hasta él
                    if (resp == null || !IsRedirect(resp.StatusCode))
                    {
                        var failure = Explain(we, u, sw.Elapsed);
                        if (resp != null) resp.Close();
                        throw failure;
                    }
                }
                MarkReached(u.Host);
                if (!IsRedirect(resp.StatusCode)) return resp;
                string location = resp.Headers[HttpResponseHeader.Location];
                int code = (int)resp.StatusCode;
                resp.Close();
                if (string.IsNullOrEmpty(location)) throw new InvalidOperationException("redirección sin destino");
                var next = new Uri(u, location);
                path.Add(code + " " + u.Host);
                if (!allowed(next)) throw new InvalidOperationException("redirección a un sitio no permitido: " + next.Scheme + "://" + next.Host);
                // Un POST redirigido con 301/302/303 sigue como GET, sin cuerpo (como los navegadores);
                // antes se volvía a mandar el formulario a la página de destino. 307/308 conservan el POST.
                if (req.Method == "POST" && (code == 301 || code == 302 || code == 303)) prepare = null;
                u = next;
            }
            Log.Warn("Demasiadas redirecciones desde " + SafeHost(url) + ": " + string.Join(" → ", path.ToArray()) + " → " + u.Host);
            throw new InvalidOperationException("demasiadas redirecciones (" + RedirectSummary(path, u.Host) + ")");
        }

        /// <summary>«onlineservices.epicgames.com → cdn… (x3)»: los dominios por los que pasó, sin repetir seguidos ni mostrar direcciones completas.</summary>
        internal static string RedirectSummary(List<string> hops, string last)
        {
            var hosts = new List<string>();
            var counts = new List<int>();
            var all = new List<string>();
            foreach (string h in hops) { int i = h.IndexOf(' '); all.Add(i >= 0 ? h.Substring(i + 1) : h); }
            all.Add(last ?? "");
            foreach (string h in all)
            {
                if (hosts.Count > 0 && hosts[hosts.Count - 1].Equals(h, StringComparison.OrdinalIgnoreCase)) { counts[counts.Count - 1]++; continue; }
                hosts.Add(h); counts.Add(1);
            }
            var parts = new List<string>();
            for (int i = 0; i < hosts.Count; i++) parts.Add(hosts[i] + (counts[i] > 1 ? " ×" + counts[i] : ""));
            return string.Join(" → ", parts.ToArray());
        }

        static bool IsRedirect(HttpStatusCode c)
        {
            int n = (int)c;
            return n == 301 || n == 302 || n == 303 || n == 307 || n == 308;
        }

        public static string GetString(string url, int timeoutMs)
        {
            return GetString(url, timeoutMs, BrowserCatalog.IsOfficialUrl, "application/json");
        }

        /// <summary>
        /// GET de texto; <paramref name="allowed"/> decide qué direcciones se aceptan (también tras
        /// redirecciones). Un corte o una demora se reintenta UNA vez; una falla de DNS no, porque el
        /// cliente DNS de Windows ya reintentó varias veces durante esos segundos.
        /// </summary>
        public static string GetString(string url, int timeoutMs, Func<Uri, bool> allowed, string accept)
        {
            return WithRetries(url, delegate() { return GetStringOnce(url, timeoutMs, allowed, accept); });
        }

        /// <summary>
        /// Un pedido de texto con sus reintentos: uno ante un corte o una demora; si la red se cayó en medio
        /// de la tanda (el servidor respondía y ahora ni se resuelve su nombre), se espera a que vuelva y se
        /// repite; si el canal seguro falló con TLS 1.3, se repite una vez sólo con TLS 1.2.
        /// </summary>
        static string WithRetries(string url, Func<string> once)
        {
            string host = SafeHost(url);
            bool waited = false, tlsFallback = false;
            int attempt = 1;
            while (true)
            {
                try { return once(); }
                catch (HttpFailureException hf)
                {
                    if (!waited && ShouldWaitForNetwork(hf, host))
                    {
                        waited = true;
                        if (!WaitForNetwork(host, null, null)) throw NetworkGone(hf, host);
                        attempt = 1;
                        continue;
                    }
                    if (!tlsFallback && TryTls12Fallback(hf, host)) { tlsFallback = true; continue; }
                    if (attempt >= 2 || !hf.Retryable) throw;
                    attempt++;
                    Log.Info("Reintento de " + host + ": " + hf.Message);
                    System.Threading.Thread.Sleep(GetRetryPauseMs);
                }
            }
        }

        /// <summary>
        /// POST de un formulario (application/x-www-form-urlencoded) y respuesta como texto. Mismas
        /// reglas que <see cref="GetString(string,int,Func{Uri,bool},string)"/>: HTTPS, dominio
        /// permitido en cada salto y un reintento ante un corte o una demora.
        /// </summary>
        public static string PostForm(string url, string body, int timeoutMs, Func<Uri, bool> allowed)
        {
            byte[] data = Encoding.ASCII.GetBytes(body ?? "");
            return WithRetries(url, delegate()
            {
                return ReadText(url, timeoutMs, allowed, delegate(HttpWebRequest req)
                {
                    req.Method = "POST";
                    req.ServicePoint.Expect100Continue = false;    // sin la espera de «100 Continue» antes del cuerpo
                    req.ContentType = "application/x-www-form-urlencoded";
                    req.Accept = "text/html";
                    req.Headers[HttpRequestHeader.AcceptLanguage] = "en-US,en;q=0.8";
                    req.ContentLength = data.Length;
                    using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                });
            });
        }

        /// <summary>Lo que trajo <see cref="GetRange"/>.</summary>
        public sealed class RangeResult
        {
            /// <summary>Los bytes pedidos (menos si el archivo termina antes).</summary>
            public byte[] Data = new byte[0];
            /// <summary>Dirección final, después de las redirecciones (cada salto validado).</summary>
            public Uri Final;
            /// <summary>Tamaño total del archivo; -1 si el servidor no lo dice.</summary>
            public long Total = -1;
            /// <summary>El servidor respetó el pedido de un pedazo (206); si no, se leyó desde el principio y se descartó lo anterior.</summary>
            public bool Partial;
            /// <summary>2.0.17: el encabezado Content-Disposition (trae el nombre del archivo cuando la dirección no lo dice), o null.</summary>
            public string Disposition;
        }

        /// <summary>Como mucho esto se descarta para llegar al pedazo si el servidor no admite pedidos por partes.</summary>
        public const long MaxRangeSkip = 128L * 1024 * 1024;

        /// <summary>
        /// 2.0.15: un pedazo de un archivo remoto (encabezado Range), con las mismas reglas que el resto: HTTPS y dirección
        /// permitida en cada salto, un reintento ante un corte. Sirve para leer la versión de un instalador oficial (sus
        /// recursos) sin bajarlo entero. Si el servidor no admite pedidos por partes, se lee desde el principio y se descarta
        /// lo anterior (hasta <see cref="MaxRangeSkip"/>).
        /// </summary>
        public static RangeResult GetRange(string url, long from, int count, int timeoutMs, Func<Uri, bool> allowed)
        {
            if (from < 0 || count <= 0 || count > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException("count");
            RangeResult result = null;
            WithRetries(url, delegate() { result = GetRangeOnce(url, from, count, timeoutMs, allowed); return ""; });
            return result;
        }

        static RangeResult GetRangeOnce(string url, long from, int count, int timeoutMs, Func<Uri, bool> allowed)
        {
            using (var resp = Send(url, timeoutMs, allowed, delegate(HttpWebRequest req)
            {
                req.AutomaticDecompression = DecompressionMethods.None;   // los bytes tal cual: el pedazo es del archivo, no de su versión comprimida
                req.Accept = "*/*";
                req.AddRange(from, from + count - 1);
            }))
            {
                var r = new RangeResult();
                r.Final = resp.ResponseUri;
                try { r.Disposition = resp.Headers["Content-Disposition"]; } catch { r.Disposition = null; }
                long skip = 0;
                int code = (int)resp.StatusCode;
                long start, total;
                if (code == 206)
                {
                    if (!ParseContentRange(resp.Headers[HttpResponseHeader.ContentRange], out start, out total) || start != from)
                        throw new InvalidDataException(r.Final.Host + " devolvió otro pedazo del archivo");
                    r.Partial = true;
                    r.Total = total;
                }
                else if (code == 200)
                {
                    r.Total = resp.ContentLength;
                    if (from > MaxRangeSkip) throw new InvalidDataException(r.Final.Host + " no admite descargas por partes y el dato está demasiado adentro del archivo");
                    skip = from;
                }
                else throw new InvalidDataException(r.Final.Host + " respondió " + code);
                var ms = new MemoryStream();
                using (var s = resp.GetResponseStream())
                {
                    byte[] buf = new byte[65536];
                    while (ms.Length < count)
                    {
                        int want = skip > 0 ? (int)Math.Min(buf.Length, skip) : (int)Math.Min(buf.Length, count - ms.Length);
                        int n;
                        try { n = s.Read(buf, 0, want); }
                        catch (Exception ex) { throw ReadFailure(r.Final, ex, Math.Min(timeoutMs, 120000)); }
                        if (n <= 0) break;
                        if (skip > 0) { skip -= n; continue; }
                        ms.Write(buf, 0, n);
                    }
                }
                r.Data = ms.ToArray();
                return r;
            }
        }

        static string GetStringOnce(string url, int timeoutMs, Func<Uri, bool> allowed, string accept)
        {
            return ReadText(url, timeoutMs, allowed, delegate(HttpWebRequest req)
            {
                req.Accept = accept;
                req.Headers[HttpRequestHeader.AcceptLanguage] = "en-US,en;q=0.8";   // textos en inglés: estables para leer
            });
        }

        static string ReadText(string url, int timeoutMs, Func<Uri, bool> allowed, Action<HttpWebRequest> prepare)
        {
            using (var resp = Send(url, timeoutMs, allowed, prepare))
            {
                using (var s = resp.GetResponseStream())
                using (var sr = new StreamReader(s, Encoding.UTF8))
                {
                    var sb = new StringBuilder();
                    char[] buf = new char[8192];
                    while (true)
                    {
                        int n;
                        try { n = sr.Read(buf, 0, buf.Length); }
                        catch (Exception ex) { throw ReadFailure(resp.ResponseUri, ex, Math.Min(timeoutMs, 120000)); }
                        if (n <= 0) break;
                        sb.Append(buf, 0, n);
                        if (sb.Length > 16 * 1024 * 1024) throw new InvalidDataException("respuesta demasiado grande");
                    }
                    return sb.ToString();
                }
            }
        }

        public static void Download(string url, string path, int timeoutMs, UpdateContext ctx)
        {
            Download(url, path, timeoutMs, ctx, BrowserCatalog.IsOfficialUrl);
        }

        public static void Download(string url, string path, int timeoutMs, UpdateContext ctx, Func<Uri, bool> allowed)
        {
            Download(url, path, timeoutMs, ctx, allowed, null);
        }

        /// <summary>
        /// Descarga reanudable. Cada pedido tiene 30 s para conectar y, una vez conectado, la
        /// conexión se da por colgada si pasan 90 s sin recibir nada. Si se corta, se retoma desde el
        /// último byte recibido (encabezado Range); si el servidor no admite retomar, se empieza de
        /// nuevo. Se rinde tras 3 intentos seguidos sin avanzar o al agotar <paramref name="timeoutMs"/>.
        /// winget, en cambio, tira la descarga entera ante cualquier corte: TeamViewer falló 3 veces
        /// seguidas a los 2 minutos de cada intento.
        /// </summary>
        /// <param name="progress">Opcional: porcentaje (o -1 si no se conoce el tamaño) y bytes recibidos.</param>
        public static void Download(string url, string path, int timeoutMs, UpdateContext ctx, Func<Uri, bool> allowed, Action<int, long> progress)
        {
            var total = Stopwatch.StartNew();
            long done = 0, size = -1;
            int stuck = 0, reconnects = 0, lastPct = -1, networkWaits = 0;
            bool tlsFallback = false;
            string host = SafeHost(url);
            long lastMb = 0;
            string validator = null;      // ETag fuerte o Last-Modified de la primera respuesta (para If-Range)
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                while (true)
                {
                    if (ctx != null && ctx.CancelRequested) throw new OperationCanceledByUserException();
                    long before = done;
                    try
                    {
                        long from = done;
                        string ifRange = validator;
                        using (var resp = Send(url, ConnectTimeoutMs, allowed, delegate(HttpWebRequest req)
                        {
                            req.ReadWriteTimeout = StallTimeoutMs;
                            req.AutomaticDecompression = DecompressionMethods.None;   // con Range hacen falta los bytes tal cual
                            if (from > 0)
                            {
                                req.AddRange(from);
                                // Si el archivo cambió en el servidor (otra compilación en el mismo enlace), con
                                // If-Range responde el archivo nuevo entero en vez de pegar sus bytes al viejo.
                                if (!string.IsNullOrEmpty(ifRange)) req.Headers[HttpRequestHeader.IfRange] = ifRange;
                            }
                        }))
                        {
                            int status = (int)resp.StatusCode;
                            if (from > 0)
                            {
                                long start = -1, whole = -1;
                                bool rangeOk = status == 206 && ParseContentRange(resp.Headers["Content-Range"], out start, out whole) && start == from &&
                                               (whole <= 0 || size <= 0 || whole == size);
                                if (rangeOk)
                                {
                                    if (whole > 0) size = whole;
                                    Log.Info("Descarga retomada desde el byte " + from + " (" + resp.ResponseUri.Host + ")");
                                }
                                else if (status == 206)
                                {
                                    // Otro tramo u otro tamaño total: es otro archivo. Se empieza de cero.
                                    RestartDownload(fs, ref done, ref before, ref size);
                                    validator = null;
                                    throw new HttpFailureException(resp.ResponseUri.Host + " devolvió otra parte del archivo al retomar; se descarga de nuevo desde el principio",
                                                                   null, status, WebExceptionStatus.ProtocolError, true);
                                }
                                else
                                {
                                    // El servidor no retoma: se descarta lo recibido y se empieza de cero.
                                    Log.Info("El servidor " + resp.ResponseUri.Host + " no permite retomar (HTTP " + status + "): se descarga de nuevo desde el principio");
                                    if (status != 200) throw new HttpFailureException(resp.ResponseUri.Host + " respondió " + status + " al retomar la descarga", null, status, WebExceptionStatus.ProtocolError, true);
                                    RestartDownload(fs, ref done, ref before, ref size);
                                    size = resp.ContentLength;
                                    validator = ValidatorOf(resp);
                                }
                            }
                            else
                            {
                                size = resp.ContentLength;
                                validator = ValidatorOf(resp);
                            }
                            if (size > MaxDownload) throw new InvalidDataException("descarga demasiado grande (" + size + " bytes)");

                            using (var s = resp.GetResponseStream())
                            {
                                byte[] buf = new byte[81920];
                                while (true)
                                {
                                    int n;
                                    // Sólo los errores de LECTURA de la red se reintentan; uno al escribir el
                                    // archivo (disco lleno) corta la descarga.
                                    try { n = s.Read(buf, 0, buf.Length); }
                                    catch (Exception ex) { throw ReadFailure(resp.ResponseUri, ex, StallTimeoutMs); }
                                    if (n <= 0) break;
                                    if (ctx != null && ctx.CancelRequested) throw new OperationCanceledByUserException();
                                    // Mientras llegan datos, un tope más amplio (el triple del plazo): una descarga
                                    // grande y constante pero lenta termina (un controlador de 900 MB a 4 Mbit/s
                                    // tarda media hora), y una conexión que casi no avanza no dura horas.
                                    if (total.ElapsedMilliseconds > HardCapMs(timeoutMs))
                                        throw new HttpFailureException("la descarga sigue después de " + (HardCapMs(timeoutMs) / 60000) + " minutos (" + Mb(done) + (size > 0 ? " de " + Mb(size) : "") +
                                                                       "): la conexión es demasiado lenta", null, 0, WebExceptionStatus.Timeout, false);
                                    fs.Write(buf, 0, n);
                                    done += n;
                                    if (done > MaxDownload) throw new InvalidDataException("descarga demasiado grande");
                                    if (size > 0)
                                    {
                                        int pct = (int)(done * 100 / size);
                                        if (pct / 10 != lastPct / 10)
                                        {
                                            lastPct = pct;
                                            if (progress != null) progress(pct, done);
                                            else if (ctx != null) ctx.Report("Descargando… " + pct + " %");
                                        }
                                    }
                                    else if (done / (10L * 1024 * 1024) != lastMb)
                                    {
                                        lastMb = done / (10L * 1024 * 1024);
                                        if (progress != null) progress(-1, done);
                                        else if (ctx != null) ctx.Report("Descargando… " + Mb(done));
                                    }
                                }
                            }
                            if (size > 0 && done < size)
                                throw new HttpFailureException("la conexión con " + resp.ResponseUri.Host + " se cerró antes de terminar (" + Mb(done) + " de " + Mb(size) + ")",
                                                               null, 0, WebExceptionStatus.ConnectionClosed, true);
                            if (size > 0 && done > size) throw new InvalidDataException("el servidor envió más datos de los anunciados");
                            fs.Flush();
                            return;
                        }
                    }
                    catch (HttpFailureException hf)
                    {
                        // La red se cayó en medio de la descarga o entre una y otra: se espera a que vuelva
                        // y se retoma desde el último byte, sin gastar los reintentos de una conexión inestable.
                        if (networkWaits < 2 && ShouldWaitForNetwork(hf, host))
                        {
                            networkWaits++;
                            if (!WaitForNetwork(host, ctx != null ? (Func<bool>)delegate() { return ctx.CancelRequested; } : null,
                                                ctx != null ? (Action<string>)ctx.Report : null))
                            {
                                if (ctx != null && ctx.CancelRequested) throw new OperationCanceledByUserException();
                                throw NetworkGone(hf, host);
                            }
                            stuck = 0;
                            continue;
                        }
                        if (!tlsFallback && TryTls12Fallback(hf, host)) { tlsFallback = true; continue; }
                        // 416 al retomar: el archivo del servidor cambió (o se achicó). Se empieza de cero, una vez.
                        bool restart416 = hf.StatusCode == 416 && done > 0;
                        if (restart416) { RestartDownload(fs, ref done, ref before, ref size); validator = null; }
                        else if (!hf.Retryable) throw;
                        stuck = done > before ? 0 : stuck + 1;
                        reconnects++;
                        if (stuck > MaxReconnectsWithoutProgress || reconnects > 20)
                            throw new HttpFailureException(hf.Message + (reconnects > 1 ? " (" + reconnects + " intentos)" : ""), hf.InnerException, hf.StatusCode, hf.Status, false);
                        if (total.ElapsedMilliseconds > timeoutMs)
                            throw new HttpFailureException(hf.Message + "; se agotaron los " + (timeoutMs / 60000) + " minutos de la descarga", hf.InnerException, hf.StatusCode, hf.Status, false);
                        Log.Warn("Descarga: " + hf.Message + "; se retoma (" + Mb(done) + " recibidos, reintento " + reconnects + ")");
                        if (ctx != null) ctx.Report("Se cortó la descarga (" + hf.Message + "); se retoma en " + Math.Max(1, RetryPauseMs / 1000) + " segundos…");
                        var pause = Stopwatch.StartNew();
                        while (pause.ElapsedMilliseconds < RetryPauseMs && (ctx == null || !ctx.CancelRequested))
                            System.Threading.Thread.Sleep(Math.Min(200, RetryPauseMs));
                    }
                }
            }
        }

        const long MaxDownload = 2L * 1024 * 1024 * 1024;

        /// <summary>Tope total mientras la descarga avanza: el triple del plazo (que rige para rendirse tras un corte).</summary>
        public static long HardCapMs(int timeoutMs)
        {
            return 3L * Math.Max(0, timeoutMs);
        }

        static void RestartDownload(FileStream fs, ref long done, ref long before, ref long size)
        {
            fs.SetLength(0);
            fs.Position = 0;
            done = 0;
            before = 0;
            size = -1;
        }

        /// <summary>Lo que identifica esta versión del archivo para If-Range: un ETag fuerte o, si no hay, Last-Modified.</summary>
        static string ValidatorOf(HttpWebResponse resp)
        {
            string etag = (resp.Headers[HttpResponseHeader.ETag] ?? "").Trim();
            if (etag.Length > 0 && !etag.StartsWith("W/", StringComparison.Ordinal)) return etag;   // uno débil no sirve para If-Range
            string lm = (resp.Headers[HttpResponseHeader.LastModified] ?? "").Trim();
            return lm.Length > 0 ? lm : null;
        }

        /// <summary>«bytes 1000-9999/10000» → inicio 1000, total 10000 (-1 si es «*»).</summary>
        public static bool ParseContentRange(string header, out long start, out long total)
        {
            start = -1; total = -1;
            if (string.IsNullOrEmpty(header)) return false;
            string h = header.Trim();
            if (!h.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase)) return false;
            h = h.Substring(6).Trim();
            int dash = h.IndexOf('-'), slash = h.IndexOf('/');
            if (dash <= 0 || slash < dash) return false;
            if (!long.TryParse(h.Substring(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out start)) return false;
            string t = h.Substring(slash + 1).Trim();
            if (t != "*" && !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out total)) return false;
            return true;
        }

        static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0", CultureInfo.GetCultureInfo("es-AR")) + " MB";
        }

        static string SafeHost(string url)
        {
            try { return new Uri(url).Host; } catch { return url; }
        }

        /// <summary>Falla al leer la respuesta ya conectada: se colgó (sin datos durante el plazo) o se cortó.</summary>
        static Exception ReadFailure(Uri u, Exception ex, int stallMs)
        {
            if (ex is OperationCanceledByUserException) return ex;
            string host = u != null ? u.Host : "el servidor";
            var we = ex as WebException ?? ex.InnerException as WebException;
            var se = ex.InnerException as SocketException;
            bool timeout = (we != null && EffectiveStatus(we) == WebExceptionStatus.Timeout) || (se != null && se.SocketErrorCode == SocketError.TimedOut);
            if (timeout)
                return new HttpFailureException(host + " dejó de enviar datos durante " + (stallMs / 1000) + " s", ex, 0, WebExceptionStatus.Timeout, true);
            if (ex is IOException || we != null)
                return new HttpFailureException("se cortó la conexión con " + host, ex, 0, WebExceptionStatus.ReceiveFailure, true);
            return ex;
        }

        /// <summary>
        /// El tipo de falla, también en .NET 8: ahí HttpWebRequest funciona sobre HttpClient y casi todo llega como
        /// «UnknownError» con la causa real adentro (SocketException, AuthenticationException, HttpRequestException). Sin
        /// esto, «sin red», «no existe el servidor» o «certificado inválido» se informaban como «error de red (UnknownError)».
        /// </summary>
        public static WebExceptionStatus EffectiveStatus(WebException we)
        {
            if (we == null) return WebExceptionStatus.UnknownError;
            if (we.Status != WebExceptionStatus.UnknownError) return we.Status;
            for (Exception e = we.InnerException; e != null; e = e.InnerException)
            {
                var se = e as SocketException;
                if (se != null)
                {
                    switch (se.SocketErrorCode)
                    {
                        case SocketError.HostNotFound: case SocketError.NoData: case SocketError.TryAgain: return WebExceptionStatus.NameResolutionFailure;
                        case SocketError.TimedOut: return WebExceptionStatus.Timeout;
                        case SocketError.ConnectionReset: case SocketError.ConnectionAborted: case SocketError.Shutdown: return WebExceptionStatus.ReceiveFailure;
                        default: return WebExceptionStatus.ConnectFailure;   // rechazada, red o servidor inalcanzable
                    }
                }
                if (e is System.Security.Authentication.AuthenticationException) return WebExceptionStatus.TrustFailure;
                if (e is TimeoutException || e is System.Threading.Tasks.TaskCanceledException) return WebExceptionStatus.Timeout;
#if NET8_0_OR_GREATER
                var hre = e as System.Net.Http.HttpRequestException;
                if (hre != null)
                {
                    switch (hre.HttpRequestError)
                    {
                        case System.Net.Http.HttpRequestError.NameResolutionError: return WebExceptionStatus.NameResolutionFailure;
                        case System.Net.Http.HttpRequestError.ConnectionError: return WebExceptionStatus.ConnectFailure;
                        case System.Net.Http.HttpRequestError.SecureConnectionError: return WebExceptionStatus.TrustFailure;
                        case System.Net.Http.HttpRequestError.ProxyTunnelError: return WebExceptionStatus.ProxyNameResolutionFailure;
                        case System.Net.Http.HttpRequestError.ResponseEnded: return WebExceptionStatus.ConnectionClosed;
                    }
                }
#endif
                if (e is IOException) return WebExceptionStatus.ReceiveFailure;
            }
            return WebExceptionStatus.UnknownError;
        }

        /// <summary>Traduce la falla al castellano, con el servidor, y decide si se puede reintentar.</summary>
        static HttpFailureException Explain(WebException we, Uri u, TimeSpan elapsed)
        {
            string host = u != null ? u.Host : "el servidor";
            var hr = we.Response as HttpWebResponse;
            if (hr != null)
            {
                int code = (int)hr.StatusCode;
                bool retry = code >= 500 || code == 408 || code == 429;
                if (code == 407)
                    return new HttpFailureException("el proxy de la red pide autenticación (407) y no aceptó las credenciales de Windows: pedile a quien administra la red que permita " + host, we, code, we.Status, false);
                return new HttpFailureException(host + " respondió " + code + " " + hr.StatusDescription, we, code, we.Status, retry);
            }
            var status = EffectiveStatus(we);
            switch (status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                    return new HttpFailureException(DescribeNameFailure(host, elapsed, NetworkUsable(), ReachedBefore(host)), we, 0, status, false);
                case WebExceptionStatus.ConnectFailure:
                {
                    string m = DescribeConnectFailure(host, ResolvedAddresses(host));
                    if (!NetworkUsable()) m = "sin conexión de red: " + m + " (ningún adaptador está conectado a una red con salida)";
                    else if (ReachedBefore(host) && m.StartsWith("no se pudo conectar", StringComparison.Ordinal)) m += ", que respondía hace un momento";
                    return new HttpFailureException(m, we, 0, status, true);
                }
                case WebExceptionStatus.Timeout:
                    return new HttpFailureException(host + " no respondió en " + Math.Max(1, (int)Math.Round(elapsed.TotalSeconds)) + " s", we, 0, status, true);
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure:
                    Log.Info("HTTPS con " + host + " (" + status + ", TLS " + ProtocolsText() + "): " + InnerChain(we));
                    return new HttpFailureException(TlsFailureText(host, status, TakeCertificateProblem(host), LegacyTlsWindows(), tls13Disabled), we, 0, status, false);
                case WebExceptionStatus.ProxyNameResolutionFailure:
                    return new HttpFailureException("no se encontró el proxy configurado en Windows", we, 0, status, false);
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure:
                case WebExceptionStatus.KeepAliveFailure:
                case WebExceptionStatus.PipelineFailure:
                    return new HttpFailureException("se cortó la conexión con " + host, we, 0, status, true);
                default:
                    return new HttpFailureException("error de red con " + host + " (" + status + ")", we, 0, status, true);
            }
        }

        /// <summary>
        /// El DNS no resolvió el nombre. Si tardó varios segundos, el servidor DNS no contestó (el
        /// cliente de Windows reintenta durante unos 10 s); si fue inmediato, contestó que el nombre
        /// no existe, que es lo que hacen muchos filtros DNS con lo que bloquean.
        /// </summary>
        public static string DescribeNameFailure(string host, TimeSpan elapsed, bool networkAvailable)
        {
            return DescribeNameFailure(host, elapsed, networkAvailable, false);
        }

        /// <summary>
        /// Ídem, sabiendo si el mismo servidor respondió antes en esta ejecución. En ese caso no es un
        /// filtro DNS: la red se cortó (lo típico al instalar un controlador de red o del chipset, que reinicia
        /// el adaptador). Antes se culpaba al filtro DNS aunque el catálogo hubiera respondido un minuto antes.
        /// </summary>
        public static string DescribeNameFailure(string host, TimeSpan elapsed, bool networkAvailable, bool reachedBefore)
        {
            if (!networkAvailable) return "sin conexión de red (ningún adaptador está conectado a una red con salida)";
            if (reachedBefore)
                return "se cortó la red: el DNS dejó de resolver " + host + ", que respondía hace un momento (pasa cuando se reinicia el adaptador de red, por ejemplo al instalar un controlador)";
            if (elapsed.TotalSeconds >= 4)
                return "el servidor DNS no respondió al buscar " + host + " (esperó " + (int)Math.Round(elapsed.TotalSeconds) + " s)";
            return "el servidor DNS respondió que " + host + " no existe (si la red tiene un filtro DNS, puede estar bloqueándolo)";
        }

        /// <summary>No se pudo conectar. Si el nombre resolvió a 0.0.0.0 o a la propia máquina, lo bloquea un filtro DNS.</summary>
        public static string DescribeConnectFailure(string host, IPAddress[] addresses)
        {
            IPAddress literal;
            // Una dirección escrita tal cual (127.0.0.1) no pasó por el DNS: no la bloquea ningún filtro.
            if (addresses != null && addresses.Length > 0 && !IPAddress.TryParse(host ?? "", out literal))
            {
                bool sinkhole = true;
                foreach (var a in addresses)
                    if (!(a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(a))) { sinkhole = false; break; }
                if (sinkhole) return "un filtro DNS bloquea " + host + " (lo resuelve a " + addresses[0] + ")";
            }
            return "no se pudo conectar con " + host;
        }

        static IPAddress[] ResolvedAddresses(string host)
        {
            // Si se llegó a intentar la conexión, el nombre ya resolvió y está en la caché: es inmediato.
            try { return Dns.GetHostAddresses(host); }
            catch (SocketException) { return null; }
            catch (ArgumentException) { return null; }
        }

        // ================================================================== Red que se corta en medio de la tanda
        //
        // Instalar un controlador del puerto PCI Express o del chipset (pnputil) reinicia los dispositivos que
        // cuelgan de él, y el adaptador de red suele ser uno: unos segundos sin red. El catálogo había respondido
        // un minuto antes y los 6 controladores siguientes fallaron en 50 ms con «el servidor DNS respondió que
        // no existe (filtro DNS)». Ahora: si el servidor ya respondió en esta ejecución (o hubo red y ahora no
        // hay ningún adaptador con salida), se espera a que vuelva y se repite el pedido.

        /// <summary>Cuánto se espera a que vuelva la red (las pruebas lo acortan).</summary>
        internal static int NetworkWaitMs = 120000;
        /// <summary>Cada cuánto se mira si volvió.</summary>
        internal static int NetworkPollMs = 2000;
        /// <summary>Con la red ya arriba, cuánto se espera a que el nombre resuelva antes de vaciar la caché DNS.</summary>
        internal static int DnsFlushAfterMs = 6000;
        /// <summary>Sólo para las pruebas: ¿hay red con salida? (null = la de verdad).</summary>
        internal static Func<bool> NetworkUsableForTests;
        /// <summary>Sólo para las pruebas: ¿ya responde el servidor? (null = se resuelve su nombre).</summary>
        internal static Func<string, bool> HostBackForTests;
        /// <summary>Sólo para las pruebas: también se espera por 127.0.0.1 (si no, un servidor local caído no es «la red»).</summary>
        internal static bool WaitOnLoopbackForTests;

        static readonly Dictionary<string, DateTime> reached = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static DateTime lastGaveUp = DateTime.MinValue;
        static bool everHadGateway;

        internal static void MarkReached(string host)
        {
            if (string.IsNullOrEmpty(host)) return;
            lock (reached) reached[host] = DateTime.UtcNow;
        }

        /// <summary>¿Este servidor respondió (con lo que sea) en esta ejecución?</summary>
        public static bool ReachedBefore(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            lock (reached) return reached.ContainsKey(host);
        }

        static bool AnyReached()
        {
            lock (reached) return reached.Count > 0;
        }

        /// <summary>Sólo para las pruebas: olvida qué servidores respondieron y la última espera fallida.</summary>
        internal static void ResetNetworkStateForTests()
        {
            lock (reached) reached.Clear();
            lastGaveUp = DateTime.MinValue;
        }

        /// <summary>
        /// ¿Hay algún adaptador conectado a una red con salida (con puerta de enlace)? Antes se usaba
        /// GetIsNetworkAvailable, que da «sí» con cualquier adaptador encendido: los virtuales de Hyper-V,
        /// VirtualBox o WSL siguen arriba aunque el cable o el Wi-Fi no tengan conexión. En un equipo que no
        /// tuvo nunca una puerta de enlace en esta ejecución (red sin salida directa), se vuelve a lo de antes.
        /// </summary>
        public static bool NetworkUsable()
        {
            if (NetworkUsableForTests != null) return NetworkUsableForTests();
            try
            {
                bool anyUp = false;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    anyUp = true;
                    IPInterfaceProperties p;
                    try { p = ni.GetIPProperties(); }
                    catch (Exception qx) { Log.Quiet("Propiedades IP de " + ni.Name, qx); continue; }
                    foreach (var g in p.GatewayAddresses)
                    {
                        var a = g.Address;
                        if (a == null || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) continue;
                        everHadGateway = true;
                        return true;
                    }
                }
                return !everHadGateway && anyUp;
            }
            catch (Exception qx) { Log.Quiet("Estado de la red", qx); return true; }
        }

        static bool IsNetworkLoss(HttpFailureException hf)
        {
            return hf != null && hf.StatusCode == 0 &&
                   (hf.Status == WebExceptionStatus.NameResolutionFailure || hf.Status == WebExceptionStatus.ConnectFailure);
        }

        static bool IsLoopbackHost(string host)
        {
            IPAddress a;
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out a) && IPAddress.IsLoopback(a));
        }

        /// <summary>
        /// ¿Vale la pena esperar a que vuelva la red? Sí si la falla es de red (DNS o conexión), el servidor
        /// respondió antes en esta ejecución (o hubo red y ahora no hay ningún adaptador con salida) y no se
        /// esperó en vano hace menos de 5 minutos. Un servidor que nunca respondió con la red bien (un filtro
        /// DNS que lo bloquea) falla al instante, como siempre.
        /// </summary>
        internal static bool ShouldWaitForNetwork(HttpFailureException hf, string host)
        {
            if (!IsNetworkLoss(hf) || string.IsNullOrEmpty(host)) return false;
            if (IsLoopbackHost(host) && !WaitOnLoopbackForTests) return false;
            if (DateTime.UtcNow - lastGaveUp < TimeSpan.FromMinutes(5)) return false;
            bool usable = NetworkUsable();
            if (!(ReachedBefore(host) || (AnyReached() && !usable))) return false;
            // No conecta, pero hay red y el nombre resuelve: es el servidor, no la red (no se esperan 2 minutos).
            if (hf.Status == WebExceptionStatus.ConnectFailure && usable && HostBack(host)) return false;
            return true;
        }

        /// <summary>
        /// Espera (hasta <see cref="NetworkWaitMs"/>) a que haya red con salida y el nombre del servidor vuelva a
        /// resolver. Si la red ya está arriba y el nombre sigue sin resolver, se vacía una vez la caché del
        /// cliente DNS: mientras no había red, Windows guardó «no existe» y lo sigue contestando unos minutos.
        /// true si volvió; false si no volvió a tiempo o se canceló.
        /// </summary>
        public static bool WaitForNetwork(string host, Func<bool> cancelled, Action<string> report)
        {
            var sw = Stopwatch.StartNew();
            int maxS = Math.Max(1, NetworkWaitMs / 1000);
            Log.Warn("Se perdió la conexión con " + host + ", que respondía antes: se espera hasta " + maxS + " s a que vuelva la red");
            if (report != null) report("Se cortó la red (" + host + " respondía hace un momento): esperando que vuelva, hasta " + maxS + " s…");
            bool flushed = false;
            long usableSince = -1;
            while (true)
            {
                if (cancelled != null && cancelled()) return false;
                if (NetworkUsable())
                {
                    if (usableSince < 0) usableSince = sw.ElapsedMilliseconds;
                    if (HostBack(host))
                    {
                        Log.Info("Volvió la red: " + host + " resuelve de nuevo (después de " + (int)Math.Round(sw.Elapsed.TotalSeconds) + " s)");
                        return true;
                    }
                    if (!flushed && sw.ElapsedMilliseconds - usableSince >= DnsFlushAfterMs)
                    {
                        flushed = true;
                        bool ok = HostBackForTests == null && Native.FlushDnsCache();
                        Log.Info("La red está arriba pero " + host + " no resuelve: " + (ok ? "se vació la caché DNS de Windows" : "no se pudo vaciar la caché DNS"));
                        continue;
                    }
                }
                else usableSince = -1;
                if (sw.ElapsedMilliseconds >= NetworkWaitMs) break;
                var pause = Stopwatch.StartNew();
                while (pause.ElapsedMilliseconds < NetworkPollMs && (cancelled == null || !cancelled()))
                    System.Threading.Thread.Sleep(Math.Max(1, Math.Min(200, NetworkPollMs)));
            }
            lastGaveUp = DateTime.UtcNow;
            Log.Warn("La red no volvió en " + maxS + " s (" + host + ")" + (NetworkUsable() ? ": hay adaptador conectado, pero el nombre no resuelve" : ": ningún adaptador con salida"));
            return false;
        }

        static bool HostBack(string host)
        {
            if (HostBackForTests != null) return HostBackForTests(host);
            try
            {
                foreach (var a in Dns.GetHostAddresses(host))
                    if (!(a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(a))) return true;
                return IsLoopbackHost(host);
            }
            catch (SocketException) { return false; }
            catch (ArgumentException) { return false; }
        }

        /// <summary>La red no volvió a tiempo: el motivo, sin culpar a un filtro DNS.</summary>
        static HttpFailureException NetworkGone(HttpFailureException hf, string host)
        {
            string m = "se cortó la red y no volvió en " + Math.Max(1, NetworkWaitMs / 1000) + " s (" + host + " respondía hace un momento)" +
                       (NetworkUsable() ? "" : ": ningún adaptador está conectado a una red con salida") +
                       ". Si se acaba de instalar un controlador de red o del chipset, reiniciá el equipo y volvé a buscar";
            return new HttpFailureException(m, hf.InnerException ?? hf, hf.StatusCode, hf.Status, false);
        }

        // ================================================================== HTTPS que falla

        static bool tls13Disabled;

        /// <summary>
        /// El canal seguro falló con TLS 1.3 activado (Windows 11): algunos servidores negocian mal TLS 1.3 con
        /// .NET Framework 4.8. Se sigue sólo con TLS 1.2 por el resto de la ejecución (lo mismo que en
        /// Windows 10; los servidores consultados lo soportan) y se repite. false si no corresponde.
        /// </summary>
        static bool TryTls12Fallback(HttpFailureException hf, string host)
        {
            if (hf == null || hf.Status != WebExceptionStatus.SecureChannelFailure || tls13Disabled) return false;
            try
            {
                if (((int)ServicePointManager.SecurityProtocol & 12288) == 0) return false;
                tls13Disabled = true;
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                Log.Warn("HTTPS con " + host + " falló con TLS 1.3 activado: se sigue sólo con TLS 1.2 y se repite");
                return true;
            }
            catch (Exception qx) { Log.Quiet("Pasar a TLS 1.2", qx); return false; }
        }

        /// <summary>Windows 7 o anterior (Server 2008 R2): sin TLS 1.2 de fábrica en .NET; ahí sí sirve el consejo de instalar .NET 4.8.</summary>
        static bool LegacyTlsWindows()
        {
            if (legacyTls < 0)
            {
                int v = 0;
                try { var os = OsInfo.Detect(); if (os.IsWindows && (os.Major < 6 || (os.Major == 6 && os.Minor <= 1))) v = 1; }
                catch (Exception qx) { Log.Quiet("Versión de Windows para el mensaje de TLS", qx); }
                legacyTls = v;
            }
            return legacyTls == 1;
        }
        static int legacyTls = -1;

        /// <summary>
        /// Por qué no se pudo establecer HTTPS, sin el consejo de Windows 7 en Windows 10 y 11: AIMP en
        /// Windows 11 decía «en Windows 7, instalá .NET Framework 4.8…». Con el motivo del certificado si se supo.
        /// </summary>
        public static string TlsFailureText(string host, WebExceptionStatus status, string certificateProblem, bool legacyWindows, bool triedTls12)
        {
            if (!string.IsNullOrEmpty(certificateProblem)) return "el certificado HTTPS de " + host + " " + certificateProblem;
            if (legacyWindows) return "no se pudo establecer HTTPS con " + host + " (en Windows 7, instalá .NET Framework 4.8 y las actualizaciones de TLS 1.2)";
            if (status == WebExceptionStatus.TrustFailure)
                return "Windows no confía en el certificado HTTPS de " + host + " (si un antivirus o un filtro de la red inspecciona HTTPS, suele ser eso)";
            return "no se pudo establecer HTTPS con " + host + ": el servidor cortó la negociación segura" + (triedTls12 ? " (también con TLS 1.2)" : "") +
                   ". Si en el navegador el sitio abre bien, un antivirus o un filtro de la red puede estar interfiriendo";
        }

        static readonly Dictionary<string, string> certificateProblems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static int certificateCallback;

        /// <summary>
        /// Anota por qué Windows rechazó un certificado, para decirlo en el mensaje. La decisión es la MISMA
        /// que sin esto (se acepta sólo si no hay ningún error); si otro componente ya puso su verificación, no se toca.
        /// </summary>
        static void InstallCertificateDiagnostics()
        {
            if (System.Threading.Interlocked.Exchange(ref certificateCallback, 1) == 1) return;
            try
            {
                if (ServicePointManager.ServerCertificateValidationCallback == null)
                    ServicePointManager.ServerCertificateValidationCallback = CheckCertificate;
            }
            catch (Exception qx) { Log.Quiet("Diagnóstico de certificados", qx); }
        }

        static bool CheckCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None) return true;
            try
            {
                var req = sender as HttpWebRequest;
                string host = req != null && req.RequestUri != null ? req.RequestUri.Host : "";
                string issuer = "";
                if (chain != null && chain.ChainElements.Count > 0)
                    issuer = chain.ChainElements[chain.ChainElements.Count - 1].Certificate.GetNameInfo(X509NameType.SimpleName, true) ?? "";
                else if (certificate != null)
                    using (var c2 = new X509Certificate2(certificate)) issuer = c2.GetNameInfo(X509NameType.SimpleName, true) ?? "";
                string text = DescribeCertificateProblem(errors, chain != null ? chain.ChainStatus : null, issuer);
                if (host.Length > 0) lock (certificateProblems) certificateProblems[host] = text;
                Log.Warn("Certificado HTTPS rechazado" + (host.Length > 0 ? " (" + host + ")" : "") + ": " + errors + " — " + text);
            }
            catch (Exception qx) { Log.Quiet("Diagnóstico del certificado", qx); }
            return false;
        }

        static string TakeCertificateProblem(string host)
        {
            string t;
            lock (certificateProblems)
            {
                if (!certificateProblems.TryGetValue(host ?? "", out t)) return null;
                certificateProblems.Remove(host);
            }
            return t;
        }

        /// <summary>Qué tiene de malo el certificado, para seguir a «el certificado HTTPS de X …».</summary>
        public static string DescribeCertificateProblem(SslPolicyErrors errors, X509ChainStatus[] status, string issuer)
        {
            if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return "no llegó (el servidor no presentó ninguno)";
            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return "es de otro sitio (el nombre no coincide)";
            var flags = X509ChainStatusFlags.NoError;
            if (status != null) foreach (var s in status) flags |= s.Status;
            string by = string.IsNullOrEmpty(issuer) ? "" : " (lo emitió «" + issuer + "»)";
            if ((flags & X509ChainStatusFlags.Revoked) != 0) return "fue revocado" + by;
            if ((flags & X509ChainStatusFlags.NotTimeValid) != 0) return "está vencido o todavía no es válido" + by + ": revisá que la fecha y la hora del equipo estén bien";
            if ((flags & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0)
                return "no es de confianza para Windows" + by + ": si un antivirus o un filtro de la red inspecciona HTTPS, suele ser eso";
            if ((flags & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0)
                return "no se pudo verificar: no se llegó al servidor que dice si está revocado" + by;
            return "no es válido para Windows (" + (flags != X509ChainStatusFlags.NoError ? flags.ToString() : errors.ToString()) + ")" + by;
        }

        static string ProtocolsText()
        {
            try
            {
                int p = (int)ServicePointManager.SecurityProtocol;
                var l = new List<string>();
                if ((p & 3072) != 0) l.Add("1.2");
                if ((p & 12288) != 0) l.Add("1.3");
                return l.Count > 0 ? string.Join(" y ", l.ToArray()) : p.ToString(CultureInfo.InvariantCulture);
            }
            catch { return "?"; }
        }

        /// <summary>Las excepciones internas, con el código de Windows (Schannel), para el registro.</summary>
        static string InnerChain(Exception ex)
        {
            var sb = new StringBuilder();
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" → ");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
                var w = e as System.ComponentModel.Win32Exception;
                if (w != null) sb.Append(" (").Append(Fmt.Hex(w.NativeErrorCode)).Append(")");
            }
            return Fmt.Truncate(sb.ToString(), 600);
        }

        public static string Describe(Exception ex)
        {
            var hf = ex as HttpFailureException;
            if (hf != null) return hf.Message;
            var we = ex as WebException;
            if (we != null)
            {
                var hr = we.Response as HttpWebResponse;
                if (hr != null) return "el servidor respondió " + (int)hr.StatusCode + " " + hr.StatusDescription;
                var st = EffectiveStatus(we);
                switch (st)
                {
                    case WebExceptionStatus.NameResolutionFailure: return "el servidor DNS no resolvió el nombre del servidor";
                    case WebExceptionStatus.Timeout: return "el servidor no respondió a tiempo";
                    case WebExceptionStatus.ConnectFailure: return "no se pudo conectar con el servidor";
                    case WebExceptionStatus.TrustFailure:
                    case WebExceptionStatus.SecureChannelFailure: return TlsFailureText("el servidor", st, null, LegacyTlsWindows(), tls13Disabled);
                    case WebExceptionStatus.ProxyNameResolutionFailure: return "no se encontró el proxy configurado";
                    default: return "error de red (" + st + ")";
                }
            }
            return ex.Message;
        }
    }
}
