// Proveedor de controladores del Catálogo de Microsoft Update: búsqueda en paralelo e instalación verificada.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    /// <summary>
    /// Controladores del Catálogo de Microsoft Update para los dispositivos con controlador de terceros.
    /// Windows Update sólo ofrece lo que el fabricante del equipo destinó a ese modelo; el catálogo tiene
    /// también versiones más nuevas del fabricante del chip (Intel, Realtek, AMD…), firmadas por Microsoft.
    /// Se ofrecen SIN premarcar y con la advertencia de que Windows Update no las eligió para este equipo.
    /// </summary>
    public sealed class CatalogDriverProvider : IUpdateProvider
    {
        public const string ProviderId = "catalogo";
        public const string SourceName = "Catálogo de Microsoft Update";
        /// <summary>Dispositivos que se comparan por búsqueda (el catálogo es lento: unos segundos por consulta).</summary>
        public const int MaxDevices = 160;
        // 2.0.21: 1 en modo liviano. 2.0.25: 2. Es casi todo espera de la red (la pausa entre consultas es común a todos los hilos) y
        // leer cada página ahora cuesta poco procesador: con uno solo, 160 dispositivos llegaban al límite de 8 minutos en un equipo viejo.
        static int Workers { get { return WorkersFor(Performance.Light); } }

        /// <summary>Consultas al catálogo a la vez (pública para las pruebas).</summary>
        public static int WorkersFor(bool light) { return light ? 2 : 3; }
        /// <summary>
        /// Pausa mínima entre dos consultas al catálogo, entre todos los hilos. Con 160 dispositivos a 4 hilos sin pausa,
        /// el catálogo empezó a contestar páginas de error: «No se pudo consultar el catálogo para 53 controladores».
        /// </summary>
        internal static int PacingMs = 350;
        static readonly object pacing = new object();
        static DateTime lastRequest = DateTime.MinValue;
        static readonly TimeSpan Budget = TimeSpan.FromMinutes(8);
        static readonly TimeSpan CacheLife = TimeSpan.FromHours(6);

        public string Name { get { return ProviderId; } }

        /// <summary>Sólo para las pruebas: reemplaza las consultas al catálogo (url → html).</summary>
        internal static Func<string, string> FetchForTests;

        sealed class Cached { public DateTime At; public string Html; }
        static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);

        public string Unavailable(UpdateContext ctx)
        {
            if (ctx.Os == null || !ctx.Os.IsWindows10OrLater) return "El Catálogo de Microsoft Update se usa desde Windows 10 (pnputil instala los controladores desde esa versión).";
            if (ctx.Options != null && !ctx.Options.QueryVendors)
                return "La consulta al Catálogo de Microsoft Update está desactivada (Opciones o --sin-fabricantes): los controladores se comparan sólo con Windows Update.";
            return null;
        }

        static string Fetch(string url)
        {
            if (FetchForTests != null) return FetchForTests(url);
            lock (cache)
            {
                Cached hit;
                if (cache.TryGetValue(url, out hit) && DateTime.UtcNow - hit.At < CacheLife) return hit.Html;
            }
            // Una consulta cada PacingMs como mucho, entre todos los hilos.
            lock (pacing)
            {
                int wait = (int)(lastRequest.AddMilliseconds(PacingMs) - DateTime.UtcNow).TotalMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
                lastRequest = DateTime.UtcNow;
            }
            Cached c;
            lock (cache)
                if (cache.TryGetValue(url, out c) && DateTime.UtcNow - c.At < CacheLife) return c.Html;
            string html = Http.GetString(url, 45000, CatalogRules.IsCatalogPage, "text/html");
            lock (cache) cache[url] = new Cached { At = DateTime.UtcNow, Html = html };
            return html;
        }

        /// <summary>Busca y valida la página; una página a medias se pide una vez más.</summary>
        static List<CatalogEntry> Search(string query, out bool complete)
        {
            string url = CatalogRules.SearchUrl(query);
            bool noResults;
            string html = Fetch(url);
            var rows = CatalogRules.ParseSearch(html, out noResults, out complete);
            // Página a medias o de error (el catálogo se satura): hasta dos intentos más, cada vez más espaciados.
            for (int retry = 0; !complete && FetchForTests == null && retry < 2; retry++)
            {
                lock (cache) cache.Remove(url);
                Log.Info("Catálogo: respuesta incompleta para «" + query + "» (" + Fmt.Truncate(Regex.Replace(html ?? "", @"\s+", " "), 200) + "); se repite");
                Thread.Sleep(retry == 0 ? 4000 : 10000);
                html = Fetch(url);
                rows = CatalogRules.ParseSearch(html, out noResults, out complete);
            }
            if (!complete) lock (cache) cache.Remove(url);
            return rows;
        }

        // ------------------------------------------------------------------ Controladores que Windows no aplicó

        /// <summary>
        /// Paquetes del fabricante que se instalaron para un dispositivo con el genérico de Windows y Windows siguió usando el
        /// genérico. Se recuerdan 30 días: antes la búsqueda siguiente los volvía a ofrecer como si nada (grabación del usuario:
        /// «High Definition Audio Device» decía «Actualizada» y volvía a aparecer).
        /// </summary>
        static readonly TimeSpan RejectedLife = TimeSpan.FromDays(30);

        static void RememberRejected(string deviceId, string guid)
        {
            if (FetchForTests != null || string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(guid)) return;
            try
            {
                // 2.0.16: es de ESTE equipo (la portable lo llevaba al siguiente en «Datos\cache» y ahí no ofrecía ese controlador
                // por 30 días); la fecha, con el calendario de siempre aunque Windows use otro.
                lock (QueryCache.MachineGate)
                {
                    var q = QueryCache.OpenMachine(QueryCache.MachineStateName, RejectedLife);
                    q.Put(deviceId + "|" + guid, DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                    q.Save();
                }
            }
            catch (Exception ex) { Log.Info("Controladores no aplicados: " + ex.Message); }
        }

        /// <summary>Fecha en que Windows no aplicó ese paquete a ese dispositivo, o null.</summary>
        internal static Func<string, string, string> RejectedForTests;
        static QueryCache rejected;

        static string RejectedOn(string deviceId, string guid)
        {
            if (RejectedForTests != null) return RejectedForTests(deviceId, guid);
            var q = rejected;
            string when;
            return q != null && q.TryGet(deviceId + "|" + guid, out when) ? when : null;
        }

        // ------------------------------------------------------------------ Respuestas guardadas

        /// <summary>Cuánto vale una consulta guardada en disco: el catálogo publica controladores unas veces por semana.</summary>
        public static readonly TimeSpan DiskCacheLife = TimeSpan.FromHours(12);
        static QueryCache disk;

        /// <summary>Como <see cref="Search"/>, pero primero mira lo guardado de las últimas horas (y guarda lo completo).</summary>
        static List<CatalogEntry> SearchCached(string query, out bool complete)
        {
            var d = disk;
            string raw;
            if (d != null && d.TryGet("s:" + query, out raw))
            {
                var cached = DecodeEntries(raw);
                if (cached != null) { complete = true; return cached; }
            }
            var rows = Search(query, out complete);
            if (complete && d != null) d.Put("s:" + query, EncodeEntries(rows));
            return rows;
        }

        /// <summary>La arquitectura de una entrada (página de detalle), guardada por unas horas.</summary>
        static string ArchitectureOf(string guid)
        {
            var d = disk;
            string arch;
            if (d != null && d.TryGet("a:" + guid, out arch)) return arch;
            arch = CatalogRules.ParseArchitecture(Fetch(CatalogRules.DetailsUrl(guid)));
            if (d != null && !string.IsNullOrEmpty(arch)) d.Put("a:" + guid, arch);
            return arch;
        }

        public static string EncodeEntries(List<CatalogEntry> rows)
        {
            var list = new List<object>();
            if (rows != null)
                foreach (var e in rows)
                {
                    var o = new Dictionary<string, object>();
                    o["g"] = e.Guid; o["t"] = e.Title; o["p"] = e.Products; o["c"] = e.Classification; o["v"] = e.Version; o["s"] = e.Size;
                    o["b"] = e.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    o["u"] = e.LastUpdated.HasValue ? e.LastUpdated.Value.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
                    list.Add(o);
                }
            return Json.Serialize(list, false);
        }

        public static List<CatalogEntry> DecodeEntries(string raw)
        {
            try
            {
                var list = new List<CatalogEntry>();
                object root = Json.Parse(raw ?? "");
                if (!(root is System.Collections.IList)) return null;
                foreach (object x in (System.Collections.IList)root)
                {
                    var e = new CatalogEntry();
                    e.Guid = Json.Str(x, "g") ?? ""; e.Title = Json.Str(x, "t") ?? ""; e.Products = Json.Str(x, "p") ?? "";
                    e.Classification = Json.Str(x, "c") ?? ""; e.Version = Json.Str(x, "v") ?? ""; e.Size = Json.Str(x, "s") ?? "";
                    long b, u;
                    if (long.TryParse(Json.Str(x, "b"), out b)) e.SizeBytes = b;
                    if (long.TryParse(Json.Str(x, "u"), out u)) { try { e.LastUpdated = new DateTime(u); } catch { } }
                    if (e.Guid.Length == 0) continue;
                    list.Add(e);
                }
                return list;
            }
            catch { return null; }
        }

        sealed class Work
        {
            public DriverInfo Driver;
            public List<CatalogEntry> Entries;
            public CatalogEntry Best;
            public string Arch = "";
            public string Error;
            public bool Generic;
            /// <summary>El dispositivo usa el controlador genérico de Windows.</summary>
            public bool Inbox;
        }

        public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes)
        {
            Http.EnableModernTls();
            if (FetchForTests == null)
                try { ServicePointManager.FindServicePoint(new Uri("https://" + CatalogRules.Host + "/")).ConnectionLimit = Workers; } catch { }
            int build = ctx.Os != null ? ctx.Os.Build : 0;
            bool server = ctx.Os != null && ctx.Os.IsServer;
            string native = ctx.Os != null ? ctx.Os.NativeArchitecture : "";

            // Un trabajo por identificador de hardware (varios dispositivos iguales: una sola consulta).
            var byHwid = new Dictionary<string, List<DriverInfo>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var d in ctx.Drivers)
            {
                if (!CatalogRules.ShouldCheck(d)) continue;
                List<DriverInfo> l;
                if (!byHwid.TryGetValue(d.HardwareId.Trim(), out l)) { l = new List<DriverInfo>(); byHwid[d.HardwareId.Trim()] = l; order.Add(d.HardwareId.Trim()); }
                l.Add(d);
            }
            if (order.Count == 0) return;
            // Primero los de terceros; después los que usan el genérico de Windows.
            var thirdFirst = new List<string>();
            foreach (string h in order) if (!InventoryLoader.IsInboxDriver(byHwid[h][0])) thirdFirst.Add(h);
            foreach (string h in order) if (InventoryLoader.IsInboxDriver(byHwid[h][0])) thirdFirst.Add(h);
            order = thirdFirst;
            int skipped = Math.Max(0, order.Count - MaxDevices);
            if (skipped > 0) order.RemoveRange(MaxDevices, skipped);
            ctx.Report("Catálogo de Microsoft Update: comparando " + Fmt.Plural(order.Count, "controlador", "controladores") + "…");
            disk = FetchForTests == null ? QueryCache.Open("catalogo", DiskCacheLife) : null;
            rejected = FetchForTests == null ? QueryCache.OpenMachine(QueryCache.MachineStateName, RejectedLife) : null;
            var followUp = DriverFollowUp.Snapshot.Open();
            InstalledProgram amdSuite = null;
            if (FetchForTests == null) try { InventoryLoader.EnsurePrograms(ctx); } catch (Exception ex) { Log.Info("Catálogo: programas instalados: " + ex.Message); }
            lock (ctx.Programs) amdSuite = AmdGraphics.Suite(ctx.Programs);
            var timer = System.Diagnostics.Stopwatch.StartNew();

            var works = new List<Work>();
            foreach (string h in order) { var w = new Work(); w.Driver = byHwid[h][0]; works.Add(w); }
            var deadline = DateTime.UtcNow + Budget;
            int next = -1, done = 0;
            var threads = new List<Thread>();
            for (int t = 0; t < Math.Min(Workers, works.Count); t++)
            {
                var th = new Thread(delegate()
                {
                    while (true)
                    {
                        int i = Interlocked.Increment(ref next);
                        if (i >= works.Count || ctx.CancelRequested) return;
                        if (DateTime.UtcNow > deadline) { works[i].Error = "se agotó el tiempo de la búsqueda"; continue; }
                        try { Evaluate(works[i], build, server, native, deadline); }
                        catch (Exception ex) { works[i].Error = Http.Describe(ex); }
                        int n = Interlocked.Increment(ref done);
                        if (n % 10 == 0) ctx.Report("Catálogo de Microsoft Update: " + n + " de " + works.Count + " controladores comparados…");
                    }
                });
                th.IsBackground = true;
                th.Name = "catalogo-" + t;
                threads.Add(th);
                th.Start();
            }
            foreach (var th in threads) while (!th.Join(500)) if (ctx.CancelRequested) break;
            if (disk != null) { disk.Save(); disk = null; }
            Log.Info("Catálogo de Microsoft Update: " + works.Count + " dispositivos en " + Fmt.Duration(timer.Elapsed));
            ctx.ThrowIfCancelled();

            int newer = 0, errors = 0, generic = 0, pending = 0;
            var errorTexts = new List<string>();
            foreach (var w in works)
                foreach (var d in byHwid[w.Driver.HardwareId.Trim()])
                {
                    var c = ToCandidate(w, d, followUp, amdSuite);
                    if (c == null)
                    {
                        // Un dispositivo con el controlador de Windows que no se pudo buscar sigue con su fila de Windows Update
                        // («incluido con Windows»): no es un error para el usuario. Queda en el registro.
                        if (InventoryLoader.IsInboxDriver(d)) { Log.Info("Catálogo: no se pudo buscar " + d.DeviceName + " (" + d.HardwareId + "): " + (w.Error ?? "")); continue; }
                        errors++;
                        if (w.Error != null && !errorTexts.Contains(w.Error)) errorTexts.Add(w.Error);
                        continue;
                    }
                    if (c.Status == UpdateStatus.Disponible) { newer++; if (w.Generic) generic++; }
                    if (c.Status == UpdateStatus.RequiereReinicio) pending++;
                    output.Add(c);
                }
            notes.Add("Controladores comparados también con el " + SourceName + " (catalog.update.microsoft.com): " +
                      Fmt.Plural(newer, "con versión más nueva", "con versión más nueva") + " que Windows Update no ofrece para este equipo" +
                      (generic > 0 ? " (" + generic + " encontrada" + (generic == 1 ? "" : "s") + " sólo por el modelo del chip)" : "") + ". Se muestran sin marcar.");
            if (pending > 0) notes.Add(Fmt.Plural(pending, "controlador ya instalado espera", "controladores ya instalados esperan") + " el reinicio del equipo para usarse: " + DriverFollowUp.RebootHint + ".");
            if (errors > 0) notes.Add("No se pudo consultar el catálogo para " + Fmt.Plural(errors, "controlador", "controladores") + ": " + Fmt.Truncate(string.Join("; ", errorTexts.ToArray()), 200) + ".");
            if (skipped > 0) notes.Add(Fmt.Plural(skipped, "controlador quedó", "controladores quedaron") + " sin comparar con el catálogo (se comparan como mucho " + MaxDevices + " por búsqueda).");
        }

        /// <summary>
        /// Compara un dispositivo con el catálogo. <paramref name="deadline"/> se respeta antes de cada
        /// consulta: un dispositivo hace hasta 3 búsquedas y 3 páginas de detalle (cada una con su plazo
        /// y un reintento), y con el servidor lento el presupuesto total se pasaba varios minutos.
        /// </summary>
        static void Evaluate(Work w, int build, bool server, string native, DateTime deadline)
        {
            var d = w.Driver;
            List<CatalogEntry> entries = null;
            bool anyComplete = false;
            foreach (var q in CatalogRules.QueriesFor(d.HardwareId))
            {
                if (DateTime.UtcNow > deadline)
                {
                    if (!anyComplete) { w.Error = "se agotó el tiempo de la búsqueda"; return; }
                    break;      // con lo que ya respondió alcanza
                }
                bool complete;
                var rows = SearchCached(q.Key, out complete);
                anyComplete |= complete;
                if (!complete) continue;
                foreach (var r in rows) r.Generic = q.Value;
                // La primera búsqueda con algo válido para este Windows manda: las más generales sólo si no hay nada.
                if (CatalogRules.AnyApplicable(rows, d, build, server)) { entries = rows; w.Generic = q.Value; break; }
                if (entries == null && rows.Count > 0) entries = rows;
            }
            if (!anyComplete) { w.Error = "el catálogo no devolvió una respuesta válida"; return; }
            w.Entries = entries ?? new List<CatalogEntry>();
            int looked = 0;
            w.Inbox = InventoryLoader.IsInboxDriver(d);
            var candidates = w.Inbox ? CatalogRules.VendorReplacements(w.Entries, d, build, server) : CatalogRules.NewerCandidates(w.Entries, d, build, server);
            foreach (var e in candidates)
            {
                // La arquitectura está en la página de detalle: se mira sólo para las mejores.
                string arch = "";
                if (looked < 3 && DateTime.UtcNow <= deadline)
                {
                    looked++;
                    try { arch = ArchitectureOf(e.Guid); }
                    catch (Exception ex) { Log.Info("Catálogo: detalle de " + e.Guid + ": " + Http.Describe(ex)); }
                }
                if (!CatalogRules.ArchMatches(arch, native)) continue;
                w.Best = e;
                w.Arch = arch;
                break;
            }
        }

        static UpdateCandidate ToCandidate(Work w, DriverInfo d)
        {
            return ToCandidate(w, d, null, null);
        }

        static UpdateCandidate ToCandidate(Work w, DriverInfo d, DriverFollowUp.Snapshot followUp, InstalledProgram amdSuite)
        {
            if (w.Entries == null) return null;              // no se pudo consultar: queda la fila de Windows Update
            var c = new UpdateCandidate();
            c.Kind = UpdateKind.Controlador;
            c.Name = d.DeviceName;
            c.Publisher = d.Provider;
            c.InstalledVersion = d.Version + (d.Date.HasValue ? " (" + Fmt.Date(d.Date.Value) + ")" : "");
            c.Source = SourceName;
            c.ProviderName = ProviderId;
            c.InfoUrl = CatalogRules.SearchUrl(d.HardwareId);
            if (w.Best == null)
            {
                c.Identifier = "catalogo:" + d.HardwareId;
                c.AvailableVersion = d.Version;
                c.Status = UpdateStatus.AlDia;
                c.Installable = false;
                c.Detail = w.Inbox
                    ? "Usa el controlador incluido con Windows (" + (string.IsNullOrEmpty(d.Provider) ? "Microsoft" : d.Provider) + ") y el Catálogo de Microsoft Update no tiene uno del fabricante para este Windows: se actualiza con Windows Update."
                    : w.Entries.Count == 0
                    ? "Windows Update no ofrece una versión más nueva y el Catálogo de Microsoft Update no tiene este dispositivo. El fabricante puede tener versiones propias en su sitio."
                    : "Ni Windows Update ni el Catálogo de Microsoft Update tienen una versión más nueva para este Windows.";
                c.ProviderTag = new CatalogPlan { Driver = d };
                return c;
            }
            var e = w.Best;
            c.Identifier = "catalogo:" + e.Guid;
            c.AvailableVersion = e.Version;
            c.Status = UpdateStatus.Disponible;
            c.Installable = true;
            c.Recommended = false;      // Windows Update no la eligió para este equipo: lo decide el usuario
            var plan = new CatalogPlan { Driver = d, Entry = e, Arch = w.Arch };

            // 2.0.25: ya instalada, a la espera del reinicio (o reiniciado y Windows siguió con la de antes): no se vuelve a instalar.
            DateTime at;
            var follow = DriverFollowUp.Check(followUp, d.DeviceId, e.Version, d.Version, out at);
            if (follow == DriverFollowUp.State.WaitingReboot)
            {
                c.Status = UpdateStatus.RequiereReinicio;
                c.Installable = false;
                c.Detail = DriverFollowUp.WaitingText(e.Version, at, d.Version);
                c.ProviderTag = plan;
                return c;
            }
            if (follow == DriverFollowUp.State.NotApplied)
            {
                NotOffered(c, d, DriverFollowUp.NotAppliedText(e.Version, at, d.Version));
                c.ProviderTag = plan;
                return c;
            }
            // 2.0.25: el conjunto de video de AMD instalado con AMD Software se actualiza con AMD Software.
            if (amdSuite != null && AmdGraphics.IsGraphicsFamily(d) && !InventoryLoader.IsInboxDriver(d))
            {
                NotOffered(c, d, AmdGraphics.CatalogText(amdSuite, e.Version));
                c.ProviderTag = plan;
                return c;
            }
            // 2.0.24 lo prometía y no se hacía con los de terceros: si Windows ya no lo aplicó, no se vuelve a ofrecer («AMD PCI»).
            if (!w.Inbox)
            {
                string rejectedOn = RejectedOn(d.DeviceId, e.Guid);
                if (rejectedOn != null)
                {
                    NotOffered(c, d, "El " + MachineMemory.ShowDate(rejectedOn) + " se instaló la " + e.Version + " del Catálogo de Microsoft Update y Windows se quedó con la " + d.Version +
                                     ", que considera mejor para este equipo (la del fabricante del equipo o una más exacta para este modelo). No hace falta hacer " +
                                     "nada y no se vuelve a ofrecer.");
                    c.ProviderTag = plan;
                    return c;
                }
            }
            var reg = DriverRegistry.Read(d.DeviceId);
            plan.InstalledMatch = reg != null ? reg.MatchingDeviceId : "";
            plan.PreferOem = !w.Inbox && CatalogRules.WindowsPrefersInstalled(plan.InstalledMatch, w.Generic);
            plan.ReplacesInbox = w.Inbox;
            if (w.Inbox)
            {
                string tried = RejectedOn(d.DeviceId, e.Guid);
                if (tried != null)
                {
                    // 2.0.31 (grabación del usuario, 23/11/2026 12:57: «High Definition Audio Device», 10.0.26100.1 → 10.0.1.42): hasta la 2.0.30
                    // se seguía ofreciendo para instalar FORZADO, sin marcar; marcada sin «forzar» no hacía nada, y forzada Windows suele volver al
                    // suyo. Windows ya eligió: queda «Al día» con el motivo y no se ofrece más.
                    NotOffered(c, d, "El " + MachineMemory.ShowDate(tried) + " se instaló el controlador del fabricante (" + e.Version + ") y Windows siguió usando el " +
                                     "genérico incluido con Windows (" + d.Version + "): lo considera mejor para este dispositivo, y funciona. No hace falta hacer nada " +
                                     "y no se vuelve a ofrecer.");
                    c.ProviderTag = plan;
                    return c;
                }
                c.Detail = "Hoy usa el controlador genérico incluido con Windows (" + (string.IsNullOrEmpty(d.Provider) ? "Microsoft" : d.Provider) + " " + d.Version + "). " +
                           "El Catálogo de Microsoft Update tiene el del fabricante, la " + e.Version + " («" + Fmt.Truncate(e.Title, 90) + "»" +
                           (e.LastUpdated.HasValue ? ", publicada el " + Fmt.Date(e.LastUpdated.Value) : "") + (e.Size.Length > 0 ? ", " + e.Size : "") + "), firmada por Microsoft. " +
                           "Instalalo si querés el controlador propio del dispositivo (funciones del fabricante, ahorro de energía); se puede volver atrás desde el " +
                           "Administrador de dispositivos › Propiedades › Controlador › «Revertir al controlador anterior».";
                c.ProviderTag = plan;
                return c;
            }
            c.Detail = "El Catálogo de Microsoft Update tiene la " + e.Version + " («" + Fmt.Truncate(e.Title, 90) + "»" +
                       (e.LastUpdated.HasValue ? ", publicada el " + Fmt.Date(e.LastUpdated.Value) : "") + (e.Size.Length > 0 ? ", " + e.Size : "") + "), " +
                       "firmada por Microsoft. Windows Update no la ofrece para este equipo: suele ser porque el fabricante del equipo la destinó a otros modelos o todavía no la aprobó para el tuyo" +
                       (w.Generic ? ", y se encontró sólo por el modelo del chip, sin el del fabricante del equipo" : "") + ". " +
                       (plan.PreferOem ? PreferOemText() + " "
                                       : "Instalala si el dispositivo te da problemas; ") +
                       "se puede volver atrás desde el Administrador de dispositivos › Propiedades › Controlador › «Revertir al controlador anterior».";
            c.ProviderTag = plan;
            return c;
        }

        /// <summary>Una fila del catálogo que no se ofrece: queda «Al día» con la versión que usa el dispositivo y el motivo.</summary>
        static void NotOffered(UpdateCandidate c, DriverInfo d, string why)
        {
            c.Status = UpdateStatus.AlDia;
            c.Installable = false;
            c.Recommended = false;
            c.AvailableVersion = d.Version;
            c.Detail = why;
        }

        /// <summary>Por qué Windows se va a quedar con el controlador instalado, y qué se puede hacer.</summary>
        public static string PreferOemText()
        {
            // 2.0.26 (grabación del usuario, 23/11/2026: AMD Radeon Vega 3, 31.0.21924.61 → 31.0.21925.3004): antes se ofrecía FORZADA. Sin
            // marcar «forzar» no pasaba nada (la fila seguía igual) y forzada Windows Update volvía a poner la del fabricante del equipo al
            // reiniciar. Ahora se comprueba sola.
            return "El instalado es el del fabricante del equipo para este modelo exacto, y Windows prefiere ese aunque sea más viejo, salvo que el " +
                   "paquete nuevo también sea para este modelo. Al actualizar se comprueba: si lo es, se instala; si no, no se instala (Windows la " +
                   "rechazaría, y forzada Windows Update vuelve a poner la del fabricante al reiniciar), la fila queda «Al día» y no se vuelve a ofrecer;";
        }

        // ================================================================== Instalación

        /// <summary>Sólo para las pruebas: reemplaza el POST del diálogo de descarga.</summary>
        internal static Func<string, string> DownloadDialogForTests;

        /// <summary>Un controlador ya descargado, verificado y extraído, listo para pnputil.</summary>
        sealed class Prepared
        {
            public UpdateCandidate Candidate;
            public CatalogPlan Plan;
            public string Dir, Drv;
            public string[] Infs;
            public List<string> Cabs;
        }

        /// <summary>
        /// Primero se descargan y verifican TODOS los paquetes y recién después se instalan, con los que pueden
        /// cortar la red (adaptadores de red, puertos PCI Express y chipset) al final. Antes se bajaba e
        /// instalaba de a uno: el controlador del puerto PCI Express reinició el adaptador de red y los 6
        /// siguientes fallaron en 50 ms con «el servidor DNS respondió que www.catalog.update.microsoft.com no
        /// existe», aunque el catálogo había respondido un minuto antes.
        /// </summary>
        public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed)
        {
            Http.EnableModernTls();
            var ready = new List<Prepared>();
            try
            {
                // 1. Descargar y verificar.
                foreach (var c in items)
                {
                    if (ctx.CancelRequested) { c.Status = UpdateStatus.Pendiente; c.Result = "Cancelada antes de empezar"; changed(c); continue; }
                    var plan = c.ProviderTag as CatalogPlan;
                    if (plan == null || plan.Entry == null || plan.Driver == null) { InstallerSupport.Fail(c, "No hay un controlador del catálogo para instalar; volvé a buscar"); changed(c); continue; }
                    if (!ctx.IsAdmin) { InstallerSupport.Fail(c, "Instalar controladores requiere ejecutar Actualizador como administrador"); changed(c); continue; }
                    c.Status = UpdateStatus.Instalando;
                    c.Result = "Pidiendo la descarga al catálogo…";
                    changed(c);
                    try
                    {
                        var p = Prepare(ctx, c, plan, changed);
                        if (p != null)
                        {
                            ready.Add(p);
                            c.Status = UpdateStatus.Pendiente;
                            c.Result = "Descargado y verificado; se instala cuando estén descargados todos";
                        }
                    }
                    catch (OperationCanceledByUserException) { InstallerSupport.Fail(c, "Cancelada durante la descarga"); }
                    catch (Exception ex) { Log.Error("Catálogo: " + c.Name, ex); InstallerSupport.Fail(c, Http.Describe(ex)); }
                    changed(c);
                }

                // 2. Instalar: los que pueden cortar la red, al final (el orden de la lista se respeta entre los demás).
                var ordered = InstallOrder(ready);
                bool networkTouched = false;
                foreach (var p in ordered)
                {
                    var c = p.Candidate;
                    if (ctx.CancelRequested) { c.Status = UpdateStatus.Pendiente; c.Result = "Cancelada antes de instalar (ya estaba descargada)"; changed(c); continue; }
                    c.Status = UpdateStatus.Instalando;
                    changed(c);
                    if (NetworkImpact(p.Plan.Driver) >= 2) networkTouched = true;
                    try { Apply(ctx, c, p, changed); }
                    catch (OperationCanceledByUserException) { InstallerSupport.Fail(c, "Cancelada durante la instalación"); }
                    catch (Exception ex) { Log.Error("Catálogo: " + c.Name, ex); InstallerSupport.Fail(c, Http.Describe(ex)); }
                    changed(c);
                }

                // 3. Lo que sigue (la herramienta del fabricante, Windows Update) necesita red: si un controlador la
                // cortó, se espera a que vuelva en vez de dejar que falle todo lo demás.
                if (networkTouched && !ctx.CancelRequested && !Http.NetworkUsable())
                    Http.WaitForNetwork(CatalogRules.Host, delegate() { return ctx.CancelRequested; }, ctx.Report);
            }
            finally { foreach (var p in ready) BrowserProvider.TryDeleteDir(p.Dir); }
        }

        /// <summary>
        /// Cuánto puede cortar la red instalar el controlador de este dispositivo: 3 el propio adaptador de red;
        /// 2 puertos PCI Express, chipset, USB y controladoras (reinician lo que cuelga de ellos, a veces el
        /// adaptador); 1 video (la pantalla parpadea); 0 el resto.
        /// </summary>
        public static int NetworkImpact(DriverInfo d)
        {
            if (d == null) return 0;
            string cls = (d.DeviceClass ?? "").Trim();
            if (cls.Equals("Net", StringComparison.OrdinalIgnoreCase) || cls.Equals("NetService", StringComparison.OrdinalIgnoreCase) ||
                cls.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase)) return 3;
            if (cls.Equals("System", StringComparison.OrdinalIgnoreCase) || cls.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                cls.Equals("HDC", StringComparison.OrdinalIgnoreCase) || cls.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase)) return 2;
            if (cls.Equals("Display", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        /// <summary>Orden estable por <see cref="NetworkImpact"/>: lo que no toca la red primero.</summary>
        static List<Prepared> InstallOrder(List<Prepared> ready)
        {
            var keyed = new List<KeyValuePair<int, Prepared>>();
            for (int i = 0; i < ready.Count; i++) keyed.Add(new KeyValuePair<int, Prepared>(i, ready[i]));
            keyed.Sort(delegate(KeyValuePair<int, Prepared> a, KeyValuePair<int, Prepared> b)
            {
                int r = NetworkImpact(a.Value.Plan.Driver).CompareTo(NetworkImpact(b.Value.Plan.Driver));
                return r != 0 ? r : a.Key.CompareTo(b.Key);
            });
            var list = new List<Prepared>();
            foreach (var k in keyed) list.Add(k.Value);
            return list;
        }

        /// <summary>Orden en que se instalarían estos controladores (para las pruebas).</summary>
        public static List<DriverInfo> InstallOrderOf(List<DriverInfo> drivers)
        {
            var ready = new List<Prepared>();
            foreach (var d in drivers) ready.Add(new Prepared { Plan = new CatalogPlan { Driver = d } });
            var result = new List<DriverInfo>();
            foreach (var p in InstallOrder(ready)) result.Add(p.Plan.Driver);
            return result;
        }

        /// <summary>Descarga, verifica y extrae el paquete. null si falló (la fila ya quedó con el motivo).</summary>
        Prepared Prepare(UpdateContext ctx, UpdateCandidate c, CatalogPlan plan, Action<UpdateCandidate> changed)
        {
            var e = plan.Entry;
            ctx.Report("Catálogo de Microsoft Update: pidiendo la descarga de «" + e.Title + "»…");
            string body = CatalogRules.DownloadDialogBody(e.Guid);
            string dialog = DownloadDialogForTests != null ? DownloadDialogForTests(body) : Http.PostForm(CatalogRules.DownloadDialogUrl, body, 45000, CatalogRules.IsCatalogPage);
            var files = CatalogRules.ParseDownloadDialog(dialog, e.Guid);
            files.RemoveAll(delegate(CatalogFile f) { return !f.Url.EndsWith(".cab", StringComparison.OrdinalIgnoreCase) || f.Digest.Length == 0; });
            if (files.Count == 0) { InstallerSupport.Fail(c, "El catálogo no publicó un paquete .cab con su huella para esta actualización"); return null; }
            foreach (var f in files)
                if (f.Architectures.Length > 0 && !CatalogRules.ArchMatches(CatalogRules.NormalizeArchList(f.Architectures), ctx.Os != null ? ctx.Os.NativeArchitecture : ""))
                { InstallerSupport.Fail(c, "El paquete es para otra arquitectura (" + f.Architectures + "): no se instala"); return null; }

            string dir = BrowserProvider.NewDownloadDir();
            bool keep = false;
            try
            {
                string drv = Path.Combine(dir, "controlador");
                Directory.CreateDirectory(drv);
                var holds = new List<FileStream>();
                var cabs = new List<string>();
                try
                {
                    int i = 0;
                    foreach (var f in files)
                    {
                        int index = i++;
                        string file = Path.Combine(dir, "paquete" + index + ".cab");
                        // Un paquete ya descargado en un intento anterior (se vuelve a verificar igual que uno nuevo).
                        string cached = CachePath(e.Guid, index);
                        bool fromCache = false;
                        if (cached != null && File.Exists(cached))
                        {
                            try { File.Copy(cached, file); fromCache = true; Log.Info(c.Name + ": se usa el paquete descargado antes (" + cached + ")"); }
                            catch (Exception ex) { Log.Warn(c.Name + ": no se pudo usar el paquete descargado antes: " + ex.Message); TryDelete(file); }
                        }
                        if (fromCache)
                        {
                            bool ok;
                            using (var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                                ok = CatalogRules.IsCab(probe) && CatalogRules.DigestMatches(probe, f.Digest);
                            if (!ok)
                            {
                                Log.Warn(c.Name + ": el paquete guardado no coincide con la huella del catálogo; se descarga de nuevo");
                                TryDelete(cached);
                                TryDelete(file);
                                fromCache = false;
                            }
                        }
                        if (!fromCache)
                        {
                            string host = new Uri(f.Url).Host;
                            ctx.Report("Descargando " + c.Name + " " + e.Version + " desde " + host + "…");
                            int lastShown = -1;
                            Http.Download(f.Url, file, 30 * 60 * 1000, ctx, CatalogRules.IsDownloadHost, delegate(int pct, long bytes)
                            {
                                if (pct >= 0 && pct == lastShown) return;
                                lastShown = pct;
                                InstallerSupport.Progress(c, changed, "Descargando del catálogo: " + (pct >= 0 ? pct + " %" : (bytes / 1048576) + " MB"));
                            });
                        }
                        var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                        holds.Add(hold);
                        cabs.Add(file);
                        if (!CatalogRules.IsCab(hold)) { InstallerSupport.Fail(c, "La descarga no es un paquete CAB: no se instala"); return null; }
                        if (!CatalogRules.DigestMatches(hold, f.Digest)) { InstallerSupport.Fail(c, "El paquete descargado no coincide con la huella que publica el catálogo: no se instala"); return null; }
                        string note;
                        int sig = Native.VerifyAuthenticodeStrict(file, out note);
                        if (sig == Native.CERT_E_REVOKED) { InstallerSupport.Fail(c, "El certificado que firma el paquete fue REVOCADO: no se instala"); return null; }
                        if (sig == 0)
                        {
                            string subject = "";
                            try { subject = Native.SignerSubject(file); } catch (Exception qx) { Log.Quiet("Firmante del paquete del catálogo", qx); }
                            if (!InstallerRules.SignerAccepted(subject, new[] { "Microsoft Corporation" })) { InstallerSupport.Fail(c, "El paquete está firmado por «" + subject + "», no por Microsoft: no se instala"); return null; }
                        }
                        else if (sig != unchecked((int)0x800B0100))   // TRUST_E_NOSIGNATURE: CAB sin firma propia; vale la huella del catálogo
                        { InstallerSupport.Fail(c, "La firma del paquete no es válida (" + Fmt.Hex(sig) + "): no se instala"); return null; }
                        Log.Info(c.Name + ": paquete del catálogo verificado (huella" + (sig == 0 ? " y firma de Microsoft" : "") + ")" + (note != null ? " — " + note : ""));

                        // Se extrae con expand.exe de Windows, sin intérprete de comandos.
                        InstallerSupport.Progress(c, changed, "Extrayendo el controlador…");
                        var x = ProcessRunner.Run(ProcessRunner.SystemExe("expand.exe"), "-F:* " + ProcessRunner.QuoteArg(file) + " " + ProcessRunner.QuoteArg(drv),
                                                  10 * 60 * 1000, TextEncodings.Oem);
                        if (!x.Ok) { InstallerSupport.Fail(c, "No se pudo extraer el paquete (" + x.Describe() + ")"); return null; }
                    }
                }
                finally { foreach (var h in holds) h.Dispose(); }

                string[] infs = Directory.GetFiles(drv, "*.inf", SearchOption.AllDirectories);
                if (infs.Length == 0) { InstallerSupport.Fail(c, "El paquete no contiene un controlador (.inf)"); return null; }
                // 2.0.25: antes de instalar, ¿el paquete es para ESTE dispositivo? (Forzado lo verifica PickForceInf.)
                if (!plan.Force)
                {
                    // 2.0.26: si el instalado es el del fabricante del equipo para este modelo (…&SUBSYS_…), el nuevo tiene que declarar también
                    // el modelo: uno sólo del chip Windows lo rechaza siempre.
                    var fit = PackageFitFor(infs, plan.Driver, plan.PreferOem);
                    if (fit == CatalogRules.PackageFit.NotDeclared && plan.PreferOem)
                    {
                        RememberRejected(plan.Driver.DeviceId, e.Guid);
                        c.Status = UpdateStatus.AlDia;
                        c.Installable = false;
                        c.Recommended = false;
                        c.InstalledVersion = plan.Driver.Version;
                        c.Result = "No hace falta: el paquete del catálogo (" + e.Version + ") es el genérico del chip y el instalado (" + plan.Driver.Version +
                                   ") es el del fabricante del equipo para este modelo; Windows se queda con ese (forzado, Windows Update lo vuelve a poner al " +
                                   "reiniciar). Queda como está y no se vuelve a ofrecer";
                        Log.Info(c.Name + ": " + c.Result);
                        return null;
                    }
                    if (fit != CatalogRules.PackageFit.Declares)
                    {
                        RememberRejected(plan.Driver.DeviceId, e.Guid);
                        c.Status = UpdateStatus.AlDia;
                        c.Installable = false;
                        c.Recommended = false;
                        c.InstalledVersion = plan.Driver.Version;
                        c.Result = fit == CatalogRules.PackageFit.NotDeclared
                            ? "No se instaló: el paquete del catálogo (" + e.Version + ") es para otros modelos de equipo y no declara este dispositivo; Windows no lo usaría. " +
                              "El controlador que tiene (" + plan.Driver.Version + ") queda como está y este paquete no se vuelve a ofrecer"
                            : "No se instaló: el paquete del catálogo (" + e.Version + ") es sólo un complemento del controlador (extensión o componente), no el controlador " +
                              "del dispositivo, y no cambiaría la versión que usa (" + plan.Driver.Version + "). No se vuelve a ofrecer";
                        Log.Info(c.Name + ": " + c.Result);
                        return null;
                    }
                }
                keep = true;
                return new Prepared { Candidate = c, Plan = plan, Dir = dir, Drv = drv, Infs = infs, Cabs = cabs };
            }
            finally { if (!keep) BrowserProvider.TryDeleteDir(dir); }
        }

        /// <summary>Qué dicen los INF extraídos del dispositivo (sin poder leerlos: decide Windows, como antes).</summary>
        static CatalogRules.PackageFit PackageFitFor(string[] infs, DriverInfo d, bool modelOnly)
        {
            // Sin los identificadores del registro no se adivina (una interfaz USB «…&MI_00» se declara con otro que no se deduce): decide Windows.
            var fromRegistry = DriverRegistry.DeviceIds(d.DeviceId);
            if (fromRegistry.Length == 0) { Log.Info(d.DeviceName + ": no se pudieron leer sus identificadores; decide Windows"); return CatalogRules.PackageFit.Declares; }
            var ids = CatalogRules.MatchIds(d.HardwareId, fromRegistry);
            if (modelOnly) ids = CatalogRules.ModelIds(ids);
            var texts = new List<string>();
            foreach (string inf in infs)
            {
                try { texts.Add(CatalogRules.InfText(File.ReadAllBytes(inf))); }
                catch (Exception ex) { Log.Warn("INF " + inf + ": " + ex.Message); return CatalogRules.PackageFit.Declares; }
            }
            var fit = CatalogRules.Fit(texts, ids, d.DeviceClass);
            Log.Info(d.DeviceName + ": el paquete " + (fit == CatalogRules.PackageFit.Declares ? "declara" : fit == CatalogRules.PackageFit.OnlyComponents ? "sólo trae complementos para" : "NO declara") +
                     " el dispositivo (" + string.Join(", ", ids.ToArray()) + ")");
            return fit;
        }

        /// <summary>Instala con pnputil un paquete ya verificado y confirma qué controlador quedó.</summary>
        void Apply(UpdateContext ctx, UpdateCandidate c, Prepared p, Action<UpdateCandidate> changed)
        {
            var plan = p.Plan;
            var e = plan.Entry;
            string drv = p.Drv;
            string[] infs = p.Infs;
            var cabs = p.Cabs;
            string before = plan.Driver.Version;
            var storeBefore = OemPackages();
            ctx.Report("Instalando el controlador de " + c.Name + " " + e.Version + " (pnputil)…");
            InstallerSupport.Progress(c, changed, "Instalando el controlador…");
            // pnputil escribe en la página de códigos ANSI cuando la salida va a un archivo o tubería.
            int build = ctx.Os != null ? ctx.Os.Build : 0;
            var r = AddDriver(build, drv, infs);
            Log.Info("pnputil terminó: " + r.Describe() + "\n" + Fmt.Truncate(r.Out, 2000));
            if (!r.Started) { InstallerSupport.Fail(c, "No se pudo ejecutar pnputil: " + r.Describe()); return; }
            if (r.TimedOut) { InstallerSupport.Fail(c, "pnputil no terminó en 20 minutos"); return; }

            // La versión, del registro (está al día en el momento); unos segundos por si el dispositivo se reinicia.
            // También con un código de error: un paquete grande trae INF para otros equipos o para otro Windows
            // (Intel, Realtek), pnputil informa el error de ésos aunque el del dispositivo se haya instalado.
            InstallerSupport.Progress(c, changed, "Comprobando qué controlador usa el dispositivo…");
            string now = "";
            var outcome = CatalogRules.InstallOutcome.NoAplicado;
            for (int i = 0; i < 8 && !ctx.CancelRequested; i++)
            {
                now = DriverVersionNow(plan.Driver.DeviceId);
                outcome = CatalogRules.Interpret(r.ExitCode, r.Out, plan.Driver.DeviceId, now, e.Version, plan.ReplacesInbox);
                if (outcome == CatalogRules.InstallOutcome.Actualizado || DriverRegistry.ReadForTests != null) break;
                if (!CatalogRules.PnpUtilSucceeded(r.ExitCode) && i >= 2) break;
                // 2.0.25: si pnputil no dice que lo instaló en ESTE dispositivo, el registro no va a cambiar: antes se esperaban 12 s por
                // cada controlador que Windows no tomaba (los más, en los paquetes de AMD).
                if (!CatalogRules.InstalledOnDevice(r.Out, plan.Driver.DeviceId) && i >= 1) break;
                Thread.Sleep(1500);
            }
            if (CatalogRules.PnpUtilFailed(r.ExitCode, outcome) && !plan.Force)
            {
                string last = "";
                foreach (string l in (r.Out + "\n" + r.Err).Split('\n')) if (l.Trim().Length > 0) last = l.Trim();
                InstallerSupport.Fail(c, "pnputil devolvió " + CatalogRules.PnpUtilText(r.ExitCode) + (last.Length > 0 ? " — " + Fmt.Truncate(last, 160) : "") +
                                      RemoveUnusedPackages(storeBefore, plan.Driver.DeviceId, c.Name, build));
                return;
            }
            bool forcedReboot = false;
            if (outcome == CatalogRules.InstallOutcome.NoAplicado && plan.Force && !ctx.CancelRequested)
            {
                // El usuario aceptó reemplazar el controlador del fabricante del equipo.
                string why = ForceInstall(ctx, c, plan, drv, changed, out forcedReboot);
                if (why != null)
                {
                    InstallerSupport.Fail(c, "Windows no lo aplicó de la forma normal y tampoco se pudo forzar: " + why +
                                          RemoveUnusedPackages(storeBefore, plan.Driver.DeviceId, c.Name, build));
                    return;
                }
                for (int i = 0; i < 8 && !ctx.CancelRequested; i++)
                {
                    now = DriverVersionNow(plan.Driver.DeviceId);
                    if (CatalogRules.Interpret(-1, "", "", now, e.Version, plan.ReplacesInbox) == CatalogRules.InstallOutcome.Actualizado) break;
                    if (DriverRegistry.ReadForTests != null) break;
                    Thread.Sleep(1500);
                }
                outcome = CatalogRules.Interpret(-1, "", "", now, e.Version, plan.ReplacesInbox) == CatalogRules.InstallOutcome.Actualizado ? CatalogRules.InstallOutcome.Actualizado : CatalogRules.InstallOutcome.Reinicio;
            }
            switch (outcome)
            {
                case CatalogRules.InstallOutcome.Actualizado:
                    // 2.0.26: también se anota: si después de reiniciar Windows Update vuelve a poner el de antes, la búsqueda siguiente lo dice
                    // y no lo ofrece otra vez («dice que instala y cuando reinicio sigue igual»).
                    DriverFollowUp.Record(plan.Driver.DeviceId, e.Version);
                    c.Status = r.ExitCode == 3010 || forcedReboot ? UpdateStatus.RequiereReinicio : UpdateStatus.Instalada;
                    c.InstalledVersion = now;
                    c.Result = (c.Status == UpdateStatus.RequiereReinicio ? "Instalado; requiere reiniciar" : "Instalado correctamente") +
                               (plan.Force ? " (forzado: reemplazó el controlador del fabricante del equipo)" : "") + " — paquete del catálogo verificado con la huella de Microsoft";
                    DropCache(e.Guid);
                    break;
                case CatalogRules.InstallOutcome.Reinicio:
                    c.Status = UpdateStatus.RequiereReinicio;
                    if (plan.ReplacesInbox) RememberRejected(plan.Driver.DeviceId, e.Guid);   // si después de reiniciar sigue el genérico, no se ofrece igual
                    // 2.0.25: se anota; la búsqueda siguiente confirma si después de reiniciar el dispositivo la usa (y si no, no se repite).
                    DriverFollowUp.Record(plan.Driver.DeviceId, e.Version);
                    c.Result = "Instalado en el dispositivo" + (plan.Force ? " (forzado)" : "") + "; Windows todavía informa la " + (VersionUtil.Parse(now) != null ? now : before) +
                               ": el controlador nuevo se usa después de reiniciar el equipo (" + DriverFollowUp.RebootHint + ")";
                    DropCache(e.Guid);
                    break;
                default:
                    // Windows se quedó con el suyo: casi siempre porque es el del fabricante del equipo para
                    // este modelo exacto. Se guarda el paquete para no volver a bajarlo si el usuario decide forzarlo.
                    plan.PreferOem = true;
                    // 2.0.25: el paquete se guarda sólo si se puede ofrecer forzado (el del fabricante en lugar del genérico de Windows); si
                    // no, no se vuelve a ofrecer y guardarlo ocupaba espacio por nada (un paquete de video de AMD pesa cientos de MB).
                    // 2.0.31: ya no se ofrece forzado: no hace falta guardar el paquete.
                    RememberRejected(plan.Driver.DeviceId, e.Guid);
                    var regNow = DriverRegistry.Read(plan.Driver.DeviceId);
                    if (regNow != null && regNow.MatchingDeviceId.Length > 0) plan.InstalledMatch = regNow.MatchingDeviceId;
                    // 2.0.24 (captura del 21/11/2026: «AMD PCI · Falló la instalación · Windows no lo aplicó»): no es un error. Windows eligió
                    // quedarse con el que tiene, y no se vuelve a ofrecer (RememberRejected). La fila queda «Al día», sin rojo.
                    c.Status = UpdateStatus.AlDia;
                    c.Installable = false;
                    c.Recommended = false;
                    c.InstalledVersion = VersionUtil.Parse(now) != null ? now : before;
                    c.Result = "Windows se quedó con el controlador que ya tiene (" + c.InstalledVersion + ")" +
                               (CatalogRules.WindowsPrefersInstalled(plan.InstalledMatch, true) ? ", el del fabricante del equipo para este modelo exacto" : ", porque lo considera mejor para este equipo (más nuevo o más exacto)") +
                               ": no hace falta hacer nada y no se vuelve a ofrecer" +
                               RemoveUnusedPackages(storeBefore, plan.Driver.DeviceId, c.Name, build);
                    Log.Info(c.Name + ": " + c.Result);
                    break;
            }
        }

        /// <summary>Sólo para las pruebas: reemplaza UpdateDriverForPlugAndPlayDevices (identificador, INF) → código de error (0 = bien).</summary>
        internal static Func<string, string, int> ForceForTests;

        /// <summary>
        /// Instala forzado el controlador del paquete en el dispositivo, como «Usar disco» en el Administrador
        /// de dispositivos (UpdateDriverForPlugAndPlayDevices con INSTALLFLAG_FORCE). Sólo con un INF del
        /// paquete ya verificado que declare un identificador del propio dispositivo, del más específico al
        /// más general. null si se instaló; si no, el motivo.
        /// </summary>
        string ForceInstall(UpdateContext ctx, UpdateCandidate c, CatalogPlan plan, string drv, Action<UpdateCandidate> changed, out bool reboot)
        {
            reboot = false;
            if (CatalogRules.ForceIds(plan.Driver.HardwareId).Count == 0) return "no se conoce el identificador del dispositivo";
            string bestId;
            string bestInf = PickForceInf(drv, plan.Driver.HardwareId, out bestId);
            if (bestInf == null) return "el paquete no declara este dispositivo";
            ctx.Report("Instalando forzado el controlador de " + c.Name + " (" + Path.GetFileName(bestInf) + ", " + bestId + ")…");
            InstallerSupport.Progress(c, changed, "Instalando forzado (reemplaza el del fabricante del equipo)…");
            Log.Info(c.Name + ": instalación forzada con " + bestInf + " para " + bestId);
            int error;
            if (ForceForTests != null) error = ForceForTests(bestId, bestInf);
            else
            {
                bool ok = Native.UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, bestId, bestInf, Native.INSTALLFLAG_FORCE, out reboot);
                error = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (ok && error != 0) error = 0;
            }
            Log.Info(c.Name + ": instalación forzada: " + (error == 0 ? "correcta" + (reboot ? ", requiere reiniciar" : "") : Fmt.Hex(error)));
            return error == 0 ? null : CatalogRules.ForceErrorText(error);
        }

        /// <summary>
        /// El INF del paquete extraído que declara el identificador más específico del dispositivo, y ese
        /// identificador. null si ninguno lo declara.
        /// </summary>
        public static string PickForceInf(string drv, string hardwareId, out string matchedId)
        {
            matchedId = null;
            var ids = CatalogRules.ForceIds(hardwareId);
            if (ids.Count == 0 || string.IsNullOrEmpty(drv) || !Directory.Exists(drv)) return null;
            string bestInf = null;
            int bestRank = int.MaxValue;
            var infs = Directory.GetFiles(drv, "*.inf", SearchOption.AllDirectories);
            Array.Sort(infs, StringComparer.OrdinalIgnoreCase);
            foreach (string inf in infs)
            {
                string text;
                try { text = CatalogRules.InfText(File.ReadAllBytes(inf)); } catch (Exception ex) { Log.Warn("INF " + inf + ": " + ex.Message); continue; }
                string id = CatalogRules.InfMatchingId(text, ids);
                if (id == null) continue;
                int rank = ids.FindIndex(delegate(string s) { return s.Equals(id, StringComparison.OrdinalIgnoreCase); });
                if (rank < bestRank) { bestRank = rank; bestInf = inf; matchedId = id; }
            }
            return bestInf;
        }

        // ------------------------------------------------------------------ Paquetes guardados

        /// <summary>Carpeta de paquetes del catálogo ya descargados y verificados (null si no se puede usar).</summary>
        static string CacheDir()
        {
            if (DownloadDialogForTests != null && CacheDirForTests == null) return null;
            if (CacheDirForTests != null) return CacheDirForTests;
            try
            {
                string d = Path.Combine(AppInfo.DataDirectory, "controladores");
                if (!SafePaths.SafeForElevatedWrite(AppInfo.DataDirectory)) return null;
                return d;
            }
            catch { return null; }
        }

        /// <summary>Sólo para las pruebas: carpeta de paquetes guardados.</summary>
        internal static string CacheDirForTests;

        static string CachePath(string guid, int index)
        {
            Guid g;
            string d = CacheDir();
            if (d == null || !Guid.TryParse(guid ?? "", out g)) return null;
            return Path.Combine(d, g.ToString("D") + "-" + index + ".cab");
        }

        static void SaveCache(string guid, List<string> cabs)
        {
            try
            {
                string d = CacheDir();
                if (d == null) return;
                Directory.CreateDirectory(d);
                // Los guardados viejos (más de 14 días) se borran.
                foreach (string f in Directory.GetFiles(d, "*.cab"))
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) TryDelete(f);
                for (int i = 0; i < cabs.Count; i++)
                {
                    string target = CachePath(guid, i);
                    if (target == null) return;
                    File.Copy(cabs[i], target, true);
                }
                Log.Info("Catálogo: paquete guardado para un próximo intento en " + d);
            }
            catch (Exception ex) { Log.Warn("No se pudo guardar el paquete del catálogo: " + ex.Message); }
        }

        static void DropCache(string guid)
        {
            for (int i = 0; i < 8; i++)
            {
                string p = CachePath(guid, i);
                if (p == null || !File.Exists(p)) break;
                TryDelete(p);
            }
        }

        static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log.Info("No se pudo borrar " + path + ": " + ex.Message); }
        }

        static ProcResult RunPnpUtil(string args, int timeoutMs)
        {
            return ProcessRunner.Run(ProcessRunner.SystemExe("pnputil.exe"), args, timeoutMs, TextEncodings.Oem);   // pnputil escribe en la página OEM
        }

        /// <summary>
        /// Agrega e instala los INF del paquete. Desde Windows 10 1607 (14393) con «/add-driver … /subdirs
        /// /install»; antes (10240, 10586) pnputil sólo entiende «-i -a» y de a un INF por vez, sin subcarpetas.
        /// </summary>
        static ProcResult AddDriver(int build, string drv, string[] infs)
        {
            if (!CatalogRules.LegacyPnpUtil(build))
                return RunPnpUtil(CatalogRules.AddDriverArgs(build, Path.Combine(drv, "*.inf")), 20 * 60 * 1000);
            var all = new ProcResult();
            var outText = new StringBuilder();
            int best = -1;
            foreach (string inf in infs)
            {
                var r = RunPnpUtil(CatalogRules.AddDriverArgs(build, inf), 10 * 60 * 1000);
                all.Started |= r.Started;
                if (!r.Started) { all.StartError = r.StartError; continue; }
                outText.AppendLine(r.Out).AppendLine(r.Err);
                if (r.TimedOut) { all.TimedOut = true; break; }
                // Gana el mejor resultado: un INF que se instaló alcanza; los demás pueden ser de otros equipos.
                if (best == -1 || CatalogRules.PnpUtilRank(r.ExitCode) < CatalogRules.PnpUtilRank(best)) best = r.ExitCode;
            }
            all.ExitCode = best;
            all.Out = outText.ToString();
            return all;
        }

        /// <summary>Paquetes de terceros del almacén de controladores (oemNN.inf de C:\Windows\INF).</summary>
        static HashSet<string> OemPackages()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string f in Directory.GetFiles(Path.Combine(SafePaths.Windows, "INF"), "oem*.inf"))
                {
                    string n = Path.GetFileName(f);
                    if (CatalogRules.IsOemInf(n)) set.Add(n);
                }
            }
            catch (Exception ex) { Log.Warn("Almacén de controladores: " + ex.Message); }
            return set;
        }

        /// <summary>
        /// Si Windows no aplicó el controlador, los paquetes que esta instalación agregó al almacén y
        /// ningún dispositivo usa se quitan (pnputil /delete-driver SIN /force: si alguno lo usa,
        /// pnputil se niega y queda). Así no se acumulan paquetes de otros equipos (los de Clevo).
        /// Devuelve el texto para agregar al resultado ("" si no se quitó nada).
        /// </summary>
        static string RemoveUnusedPackages(HashSet<string> before, string deviceId, string name, int build)
        {
            if (before == null || before.Count == 0) return "";      // sin la foto de antes no se borra nada
            var reg = DriverRegistry.Read(deviceId);
            string inUse = reg != null ? reg.InfPath : "";
            int removed = 0;
            foreach (string inf in OemPackages())
            {
                if (before.Contains(inf) || inf.Equals(inUse, StringComparison.OrdinalIgnoreCase) || !CatalogRules.IsOemInf(inf)) continue;
                var r = RunPnpUtil(CatalogRules.DeleteDriverArgs(build, inf), 2 * 60 * 1000);
                if (r.Ok) { removed++; Log.Info(name + ": se quitó del almacén el paquete sin usar " + inf); }
                else Log.Info(name + ": el paquete " + inf + " queda en el almacén (" + r.Describe() + ")");
            }
            return RemovedText(removed);
        }

        /// <summary>Texto para el resultado: «. Se quitó del almacén… el paquete…» / «. Se quitaron… los 3 paquetes…» ("" si ninguno).</summary>
        public static string RemovedText(int removed)
        {
            if (removed <= 0) return "";
            return removed == 1
                ? ". Se quitó del almacén de controladores el paquete que agregó y no usa ningún dispositivo"
                : ". Se quitaron del almacén de controladores los " + removed + " paquetes que agregó y no usa ningún dispositivo";
        }

        /// <summary>Versión del controlador que usa ahora el dispositivo: del registro; si no, de WMI. "" si no se sabe.</summary>
        static string DriverVersionNow(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return "";
            var reg = DriverRegistry.Read(deviceId);
            if (reg != null && VersionUtil.Parse(reg.Version) != null) return reg.Version;
            if (DriverRegistry.ReadForTests != null) return "";
            try
            {
                string id = deviceId.Replace("\\", "\\\\").Replace("'", "\\'");
                string err;
                foreach (var o in Wmi.TryQuery(Wmi.Cimv2, "SELECT DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceID='" + id + "'", out err, 60))
                    return Wmi.S(o, "DriverVersion") ?? "";
            }
            catch (Exception ex) { Log.Warn("Versión del controlador de " + deviceId + ": " + ex.Message); }
            return "";
        }
    }
}
