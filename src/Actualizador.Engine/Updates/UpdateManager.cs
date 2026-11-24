using System;
using System.Collections.Generic;
using System.Threading;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    /// <summary>
    /// Busca actualizaciones con todos los proveedores (en paralelo), compara con lo
    /// instalado y aplica las seleccionadas. Nunca reinicia el equipo por su cuenta.
    /// </summary>
    public sealed class UpdateManager
    {
        readonly List<IUpdateProvider> providers = new List<IUpdateProvider>();
        readonly Dictionary<string, IUpdateProvider> ready = new Dictionary<string, IUpdateProvider>();

        /// <summary>Instalar mostrando las ventanas del instalador (para los que rechazan el modo silencioso).</summary>
        public bool Interactive;

        /// <summary>
        /// Cerrar los programas que están abiertos (también en segundo plano) justo antes de actualizar
        /// cada uno. Lo acepta el usuario en la ventana que los lista, o --cerrar-programas en la CLI.
        /// </summary>
        public bool CloseRunning;

        /// <summary>Lista de ignorados para la búsqueda; null = la del usuario (ignorados.json). Las pruebas ponen la suya.</summary>
        public IgnoreList Ignored;

        /// <summary>Dónde se anota el historial de lo instalado; null = el del usuario (historial.jsonl). "" = no se anota.</summary>
        public string HistoryPath;

        /// <summary>Desde dónde se instala, para el historial: «interfaz», «consola» o «programada».</summary>
        public string Via = "interfaz";

        public UpdateManager()
        {
            providers.Add(new WingetProvider());
            providers.Add(new BrowserProvider());
            providers.Add(new VendorProvider());
            providers.Add(new WindowsUpdateProvider());
            providers.Add(new CatalogDriverProvider());
            providers.Add(new OemToolsProvider());
            providers.Add(new StoreProvider());
            providers.Add(new SiteProvider());
            providers.Add(new ExternalProvider());
        }

        /// <summary>Sólo para las pruebas: con los proveedores que se indican.</summary>
        internal UpdateManager(IEnumerable<IUpdateProvider> only)
        {
            providers.AddRange(only);
        }

        sealed class ProviderRun
        {
            public IUpdateProvider Provider;
            public readonly List<UpdateCandidate> Items = new List<UpdateCandidate>();
            public readonly List<string> Notes = new List<string>();
            public Exception Error;
            public Thread Thread;
            public TimeSpan Elapsed;
        }

        /// <summary>Proveedores de programas: cuando terminan se leen las páginas oficiales, mientras los demás siguen.</summary>
        static bool IsProgramProvider(string id)
        {
            return id == WingetProvider.ProviderId || id == BrowserProvider.ProviderId || id == VendorProvider.ProviderId;
        }

        /// <summary>
        /// Programas, controladores y apps de la Store se leen a la vez. Cada lectura tiene su
        /// propio candado en el contexto.
        /// </summary>
        static void LoadInventory(UpdateContext ctx)
        {
            var errors = new List<Exception>();
            var jobs = new List<ThreadStart>();
            jobs.Add(delegate { InventoryLoader.EnsurePrograms(ctx); });
            jobs.Add(delegate { InventoryLoader.EnsureDrivers(ctx); });
            if (ctx.Os != null && ctx.Os.IsWindows8OrLater) jobs.Add(delegate { InventoryLoader.EnsureAppx(ctx); });
            var threads = new List<Thread>();
            foreach (var job in jobs)
            {
                var j = job;
                var t = new Thread(delegate()
                {
                    ProcessRunner.LowPriorityForThisThread = true;
                    try { j(); }
                    catch (Exception ex) { lock (errors) errors.Add(ex); }
                });
                t.IsBackground = true;
                t.Name = "inventario";
                threads.Add(t);
                t.Start();
            }
            foreach (var t in threads) while (!t.Join(500)) ctx.ThrowIfCancelled();
            foreach (var ex in errors)
            {
                if (ex is OperationCanceledByUserException) throw ex;
                Log.Error("Inventario para comparar versiones", ex);
            }
        }

        public void Scan(UpdateContext ctx, UpdateSession report)
        {
            // Las consultas de la búsqueda (PowerShell, winget) corren con prioridad baja; se restaura al salir porque
            // en la consola el mismo hilo instala después.
            bool previousPriority = ProcessRunner.LowPriorityForThisThread;
            ProcessRunner.LowPriorityForThisThread = true;
            try { ScanCore(ctx, report); }
            finally { ProcessRunner.LowPriorityForThisThread = previousPriority; }
        }

        void ScanCore(UpdateContext ctx, UpdateSession report)
        {
            var total = System.Diagnostics.Stopwatch.StartNew();
            ctx.Report("Preparando inventario para comparar versiones…");
            LoadInventory(ctx);
            var times = new List<string>();
            times.Add("inventario " + Fmt.Elapsed(total.Elapsed));

            var runs = new List<ProviderRun>();
            var notes = new List<string>();
            // 2.0.25: en las vueltas de «todo de una vez», lo de las fuentes donde no se instaló nada se toma de la búsqueda anterior.
            var carried = new List<UpdateCandidate>();
            var carry = CarryOver;
            List<UpdateCandidate> previous = null;
            if (carry != null && carry.Count > 0 && report != null) lock (report.Updates) previous = new List<UpdateCandidate>(report.Updates);
            ready.Clear();
            WaitForLingering(ctx);
            // Política de la empresa (2.0): manda sobre las opciones del usuario.
            var policy = PolicyForTests ?? Policy.Current;
            ApplyPolicyToOptions(policy, ctx.Options);
            if (!policy.IsEmpty) notes.Add(policy.Describe());
            foreach (string pr in policy.Problems) notes.Add("Política: " + pr);
            // Sin winget, todos los programas quedaban «No verificable» (Windows sin Microsoft Store). Si falta y se puede,
            // se instala antes de preguntar a los proveedores.
            if (providers.Exists(delegate(IUpdateProvider p) { return p is WingetProvider; }) && WingetSetup.CannotInstall(ctx) == null)
            {
                try { WingetSetup.EnsureInstalled(ctx, notes); }
                catch (OperationCanceledByUserException) { throw; }
                catch (Exception ex) { Log.Error("Instalación de winget", ex); notes.Add("No se pudo instalar winget automáticamente: " + Http.Describe(ex)); }
            }
            // Catálogo de fuentes oficiales: se baja el de la dirección configurada, si hay, antes de consultar.
            if (ctx.Options != null && ctx.Options.QueryVendors && !string.IsNullOrEmpty(ctx.Options.SourcesCatalogUrl))
            {
                string refreshed = SourceCatalog.Refresh(ctx.Options.SourcesCatalogUrl);
                if (refreshed != null) { notes.Add(refreshed); Log.Info(refreshed); }
            }
            foreach (var p in providers)
            {
                if (IsLingering(p))
                {
                    // La búsqueda cancelada de este proveedor sigue corriendo (Windows Update no se puede
                    // interrumpir): no se lanza otra sobre el mismo objeto.
                    notes.Add("La búsqueda anterior de " + ProviderTitle(p.Name) + " todavía no terminó; se omitió esta vez. Volvé a buscar en unos minutos.");
                    continue;
                }
                if (!policy.IsSourceAllowed(p.Name))
                {
                    if (p.Name != SiteProvider.ProviderId) notes.Add(ProviderTitle(p.Name) + ": desactivado por la política de la empresa.");
                    continue;
                }
                if (previous != null && carry.Contains(p.Name))
                {
                    var rows = previous.FindAll(delegate(UpdateCandidate c) { return c.ProviderName == p.Name; });
                    if (rows.Count > 0)
                    {
                        ready[p.Name] = p;
                        carried.AddRange(rows);
                        times.Add(ProviderTitle(p.Name) + " (de la búsqueda anterior)");
                        Log.Info("Proveedor " + p.Name + ": " + rows.Count + " filas de la búsqueda anterior (en esta vuelta no se instaló nada de ahí)");
                        continue;
                    }
                }
                string reason = CheckAvailable(p, ctx);
                if (reason != null) { notes.Add(reason); Log.Warn(p.Name + ": " + reason); continue; }
                ready[p.Name] = p;
                var run = new ProviderRun();
                run.Provider = p;
                runs.Add(run);
            }
            // winget, Windows Update y la herramienta del fabricante son las búsquedas que más exigen al equipo (CPU y disco).
            // En equipos de dos núcleos o menos van de a una; en los demás, como mucho dos a la vez. El resto
            // (consultas web) va en paralelo.
            int heavyMax = Math.Max(1, HeavyNow);
            var heavyGate = new Semaphore(heavyMax, heavyMax);
            foreach (var run in runs)
            {
                var r = run;
                bool heavy = IsHeavy(r.Provider.Name);
                r.Thread = new Thread(delegate()
                {
                    ProcessRunner.LowPriorityForThisThread = true;
                    bool entered = false;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        if (heavy)
                        {
                            while (!(entered = heavyGate.WaitOne(500))) ctx.ThrowIfCancelled();
                        }
                        sw.Restart();
                        r.Provider.Scan(ctx, r.Items, r.Notes);
                    }
                    catch (Exception ex) { r.Error = ex; }
                    finally
                    {
                        r.Elapsed = sw.Elapsed;
                        if (entered) { try { heavyGate.Release(); } catch { } }
                        Log.Info("Proveedor " + r.Provider.Name + ": " + r.Items.Count + " filas en " + Fmt.Elapsed(r.Elapsed) + (r.Error != null ? " (con error)" : ""));
                    }
                });
                r.Thread.IsBackground = true;
                // 2.0.21: debajo de la ventana, para que el equipo y el programa sigan respondiendo mientras se busca.
                r.Thread.Priority = ThreadPriority.BelowNormal;
                r.Thread.Name = "scan-" + r.Provider.Name;
                r.Thread.Start();
            }
            // Plazo total por búsqueda: cada proveedor tiene los suyos, pero si alguno queda colgado (un servicio
            // de Windows que no contesta), se muestran los resultados de los demás en vez de esperar para siempre.
            var started = System.Diagnostics.Stopwatch.StartNew();
            var abandoned = new List<ProviderRun>();
            try
            {
                // primero se espera a los proveedores de programas; con eso se leen las páginas oficiales MIENTRAS Windows Update,
                // el Catálogo y la Store siguen buscando (antes las páginas esperaban a todos y sumaban su tiempo al final).
                var programRuns = runs.FindAll(delegate(ProviderRun r) { return IsProgramProvider(r.Provider.Name); });
                var otherRuns = runs.FindAll(delegate(ProviderRun r) { return !IsProgramProvider(r.Provider.Name); });
                abandoned.AddRange(WaitRuns(ctx, programRuns, started));

                var all = new List<UpdateCandidate>();
                Collect(programRuns, abandoned, all, notes);
                // Si winget no está (o no terminó a tiempo), los programas instalados igual se listan como no verificables.
                bool wingetAbandoned = abandoned.Exists(delegate(ProviderRun r) { return r.Provider.Name == WingetProvider.ProviderId; });
                if (!ready.ContainsKey(WingetProvider.ProviderId) || wingetAbandoned)
                    foreach (var p in ctx.Programs)
                    {
                        var c = new UpdateCandidate();
                        c.Kind = UpdateKind.Programa;
                        c.Name = p.Name;
                        c.Publisher = p.Publisher;
                        c.InstalledVersion = p.Version;
                        c.Status = UpdateStatus.NoVerificable;
                        c.Detail = wingetAbandoned
                            ? "winget no terminó a tiempo esta vez: no se pudo comparar. Volvé a buscar actualizaciones más tarde."
                            : "Sin winget no hay un catálogo confiable con el cual comparar. Revisá el sitio del fabricante.";
                        c.ProviderName = "";
                        if (ProgramRules.IsSelf(p.Name, p.Publisher, "")) ProgramRules.MarkSelf(c, p.Version, AppInfo.Version);
                        all.Add(c);
                    }
                DeduplicatePrograms(all);
                // lo que sigue sin verificar se compara con la versión que publica la página oficial del programa; y lo que winget
                // da «Al día» también, por si su catálogo va atrasado (GOM Player).
                var siteTimer = System.Diagnostics.Stopwatch.StartNew();
                if (!policy.IsSourceAllowed(SiteProvider.ProviderId)) notes.Add("Páginas oficiales: desactivadas por la política de la empresa.");
                else try { SiteVersions.Resolve(ctx, all, ctx.Programs, notes); }
                catch (OperationCanceledByUserException) { throw; }
                catch (Exception ex) { Log.Error("Páginas oficiales", ex); }
                // Lo que winget da «Al día» se contrasta con el proyecto de GitHub de donde winget baja el instalador: si el
                // catálogo va atrasado se ofrece la versión estable nueva, y si sólo hay una beta se dice (Cura 5.14.0-beta).
                if (policy.IsSourceAllowed(ReleaseCrossCheck.PolicySource))
                {
                    try { ReleaseCrossCheck.Resolve(ctx, all, ctx.Programs, notes); }
                    catch (OperationCanceledByUserException) { throw; }
                    catch (Exception ex) { Log.Error("Contraste con GitHub", ex); }
                }
                var siteElapsed = siteTimer.Elapsed;

                abandoned.AddRange(WaitRuns(ctx, otherRuns, started));
                Collect(otherRuns, abandoned, all, notes);
                all.AddRange(carried);
                DeduplicateOthers(all);
                foreach (var run in runs)
                    if (!abandoned.Contains(run)) times.Add(ProviderTitle(run.Provider.Name) + " " + Fmt.Elapsed(run.Elapsed));
                if (siteElapsed.TotalSeconds >= 1) times.Add("páginas oficiales " + Fmt.Elapsed(siteElapsed));

                // 2.0.8: componentes de Windows, juegos de Steam y programas propios (carpeta de versiones), sin internet.
                try { LocalChecks.Resolve(ctx, all, ctx.Programs, notes, policy.IsSourceAllowed(ExternalProvider.ProviderId)); }
                catch (OperationCanceledByUserException) { throw; }
                catch (Exception ex) { Log.Error("Comprobaciones locales", ex); }
                // 2.0.20: si no se pudieron leer los controladores instalados, se dice (antes quedaba sólo en el contexto y la lista
                // salía sin ningún controlador, como si estuvieran todos al día).
                if (!string.IsNullOrEmpty(ctx.DriversError))
                {
                    Log.Warn("Controladores instalados: " + ctx.DriversError);
                    notes.Add("No se pudieron leer los controladores instalados (" + ctx.DriversError + "): los controladores no se compararon. " +
                              "Si se repite, reiniciá el equipo (el servicio «Instrumental de administración de Windows» puede estar trabado) y buscá de nuevo.");
                }
                // Lo que sigue sin verificar: se explica por qué (lanzador, actualizador propio,
                // controlador) y se agrega el sitio que registra el programa.
                ProgramHints.Annotate(all, ctx.Programs, SecurityCenterAntivirus(ctx));
                // todo lo «Disponible» se puede marcar. Lo que no tiene un instalador verificable (Visual C++ 2005, DAEMON Tools,
                // un identificador de winget recortado…) abre su página de descarga; lo que necesita administrador sigue igual (se eleva).
                int manuales = MakeManualActionable(all);
                if (manuales > 0) Log.Info(manuales + " actualizaciones sin instalador verificable: se marcan para abrir su página");
                // Lo que el usuario pidió no ofrecer: queda en la lista como «Ignorada», sin marcar y sin instalarse.
                // 2.0.28: lo que ya se instaló (o falló) con la misma versión y vuelve a aparecer no se repite solo en cada pasada.
                if (HistoryPath != "")
                {
                    try
                    {
                        var rg = RepeatGuard.Apply(all, UpdateHistory.Load(HistoryPath ?? UpdateHistory.DefaultPath, UpdateHistory.MaxEntries), DateTime.Now, DriverFollowUp.Rebooted);
                        if (rg.Repeated + rg.Failing > 0)
                            notes.Add(Fmt.Plural(rg.Repeated + rg.Failing, "actualización ya se instaló o falló", "actualizaciones ya se instalaron o fallaron") +
                                      " antes con la misma versión y vuelve" + (rg.Repeated + rg.Failing == 1 ? "" : "n") + " a aparecer: no se marca" + (rg.Repeated + rg.Failing == 1 ? "" : "n") +
                                      " sola" + (rg.Repeated + rg.Failing == 1 ? "" : "s") + " (el motivo está en cada fila).");
                        if (rg.Waiting > 0) notes.Add(Fmt.Plural(rg.Waiting, "actualización ya instalada espera", "actualizaciones ya instaladas esperan") + " el reinicio del equipo.");
                        if (rg.Repeated + rg.Failing + rg.Waiting + rg.FailedOnce > 0)
                            Log.Info("Historial: " + rg.Repeated + " repetidas, " + rg.Failing + " que fallan siempre, " + rg.FailedOnce + " que fallaron una vez, " + rg.Waiting + " esperan reinicio");
                    }
                    catch (Exception ex) { Log.Warn("Historial en la búsqueda: " + ex.Message); }
                }
                int ignoradas = (Ignored ?? IgnoreList.Load()).Apply(all);
                int bloqueadas = ApplyPolicy(policy, all);
                if (bloqueadas > 0) notes.Add(Fmt.Plural(bloqueadas, "actualización bloqueada", "actualizaciones bloqueadas") + " por la política de la empresa (se ven en la lista «Al día · sin verificar», con el motivo).");
                if (ignoradas > 0)
                    notes.Add(Fmt.Plural(ignoradas, "elemento ignorado", "elementos ignorados") + " a pedido tuyo: no se marcan ni se instalan " +
                              "(se ven en la lista «Al día · sin verificar»; clic derecho › «Dejar de ignorar»).");
                Sort(all);
                // La interfaz copia estas listas desde su hilo con el mismo candado.
                lock (report.Updates)
                {
                    report.Updates.Clear();
                    report.Updates.AddRange(all);
                }
                lock (report.UpdateNotes)
                {
                    report.UpdateNotes.Clear();
                    report.UpdateNotes.AddRange(notes);
                }
                report.UpdatesScannedLocal = DateTime.Now;
                report.UpdatesScanDuration = total.Elapsed;
                Log.Info("Búsqueda de actualizaciones: " + Fmt.Elapsed(total.Elapsed) + " (" + string.Join(", ", times.ToArray()) + ")");
                lock (report.UpdateNotes)
                    report.UpdateNotes.Add("La búsqueda tardó " + Fmt.Elapsed(total.Elapsed) + " (" + string.Join(" · ", times.ToArray()) + "; lo de cada fuente corre al mismo tiempo).");

                int available = 0;
                foreach (var u in all) if (u.Status == UpdateStatus.Disponible) available++;
                ctx.Report("Búsqueda de actualizaciones terminada: " + Fmt.Plural(available, "disponible", "disponibles") + " de " + Fmt.Plural(all.Count, "elemento comparado", "elementos comparados") + ".");
                var k = UpdateGroups.Count(all);
                var data = new Dictionary<string, object>();
                data["paraActualizar"] = k.ToUpdate; data["opcionales"] = k.Optional; data["alDia"] = k.UpToDate; data["sinVerificar"] = k.Unverified;
                data["bloqueadas"] = bloqueadas; data["segundos"] = (long)total.Elapsed.TotalSeconds; data["politica"] = policy.Origin;
                AuditTrail.Write("busqueda", data, EventLogWriter.Ids.ScanFinished, EventLogWriter.Level.Information,
                                 "Búsqueda de actualizaciones terminada en " + Fmt.Elapsed(total.Elapsed) + ": " + k.ToUpdate + " para actualizar, " + k.Optional + " opcionales, " +
                                 k.UpToDate + " al día, " + k.Unverified + " sin verificar" + (bloqueadas > 0 ? ", " + bloqueadas + " bloqueadas por la política" : "") + ".");
            }
            catch (OperationCanceledByUserException)
            {
                // Lo que sigue corriendo (Windows Update no se puede interrumpir) queda anotado: la próxima búsqueda no lanza otra encima.
                lock (lingering) foreach (var run in runs) if (run.Thread.IsAlive && !lingering.Contains(run)) lingering.Add(run);
                throw;
            }
        }

        /// <summary>
        /// 2.0.25: fuentes que en la próxima búsqueda NO se vuelven a consultar: se usan sus filas de la búsqueda anterior. Lo usa
        /// <see cref="InstallUntilDone"/> en sus vueltas; null en una búsqueda normal.
        /// </summary>
        internal HashSet<string> CarryOver;

        /// <summary>
        /// 2.0.25 (pedido del usuario: «tarda mucho en instalar»): después de instalar, «todo de una vez» vuelve a buscar para ver si
        /// apareció algo nuevo, y cada vuelta consultaba TODAS las fuentes. Ahora sólo las que pueden cambiar por lo que se instaló:
        /// Windows Update y el catálogo si se instaló algo de Windows Update, del catálogo o de la herramienta del fabricante del
        /// equipo; esa herramienta (Dell Command | Update, que tarda minutos) sólo si se instaló algo con ella; la Store si se
        /// instaló algo de la Store o de winget. Los programas se comparan siempre (winget, navegadores, fabricantes y páginas
        /// oficiales). Lo demás se muestra como estaba.
        /// </summary>
        public static HashSet<string> CarryOverAfter(IEnumerable<UpdateCandidate> round)
        {
            var touched = new HashSet<string>(StringComparer.Ordinal);
            if (round != null) foreach (var c in round) if (c != null && c.ProviderName != null) touched.Add(c.ProviderName);
            var carry = new HashSet<string>(StringComparer.Ordinal);
            if (!touched.Contains(WindowsUpdateProvider.ProviderId) && !touched.Contains(CatalogDriverProvider.ProviderId) && !touched.Contains(OemToolsProvider.ProviderId))
            {
                carry.Add(WindowsUpdateProvider.ProviderId);
                carry.Add(CatalogDriverProvider.ProviderId);
            }
            if (!touched.Contains(OemToolsProvider.ProviderId)) carry.Add(OemToolsProvider.ProviderId);
            if (!touched.Contains(StoreProvider.ProviderId) && !touched.Contains(WingetProvider.ProviderId)) carry.Add(StoreProvider.ProviderId);
            return carry;
        }

        /// <summary>Espera a esas búsquedas (cancelable, con el plazo total). Las que no terminan a tiempo quedan en <paramref name="abandoned"/>.</summary>
        List<ProviderRun> WaitRuns(UpdateContext ctx, List<ProviderRun> list, System.Diagnostics.Stopwatch started)
        {
            var abandoned = new List<ProviderRun>();
            foreach (var run in list)
            {
                while (!run.Thread.Join(500))
                {
                    if (ctx.CancelRequested) break;
                    if (started.Elapsed > ScanLimit) { abandoned.Add(run); break; }
                }
                if (ctx.CancelRequested) break;
            }
            if (ctx.CancelRequested)
                lock (lingering) foreach (var run in list) if (run.Thread.IsAlive && !lingering.Contains(run)) lingering.Add(run);
            ctx.ThrowIfCancelled();
            // Las que terminaron justo después del plazo se toman completas.
            abandoned.RemoveAll(delegate(ProviderRun r) { return !r.Thread.IsAlive; });
            lock (lingering) foreach (var run in abandoned) if (run.Thread.IsAlive && !lingering.Contains(run)) lingering.Add(run);
            return abandoned;
        }

        static void Collect(List<ProviderRun> list, List<ProviderRun> abandoned, List<UpdateCandidate> all, List<string> notes)
        {
            foreach (var run in list)
            {
                if (abandoned.Contains(run))
                {
                    // Sus listas las sigue escribiendo su hilo: no se tocan.
                    notes.Add(ProviderTitle(run.Provider.Name) + " no terminó en " + Fmt.Duration(ScanLimit) + " (un servicio de Windows no responde): " +
                              "se muestran los demás resultados. Reiniciar el equipo suele resolverlo.");
                    Log.Warn("Proveedor " + run.Provider.Name + ": se abandonó la búsqueda");
                    continue;
                }
                all.AddRange(run.Items);
                notes.AddRange(run.Notes);
                if (run.Error != null)
                {
                    string msg = run.Error is OperationCanceledByUserException ? "cancelado" : Wmi.Describe(run.Error);
                    notes.Add("Error en " + ProviderTitle(run.Provider.Name) + ": " + msg);
                    Log.Error("Proveedor " + run.Provider.Name, run.Error);
                }
            }
        }

        /// <summary>
        /// Programas «Disponible» que no se pueden instalar desde acá pero tienen su página oficial: pasan a abrir esa página
        /// (<see cref="SiteProvider"/>) para que se puedan marcar como todo lo demás. No los que necesitan administrador (la
        /// interfaz ofrece elevar) ni los de otra cuenta. Devuelve cuántos cambiaron.
        /// </summary>
        public static int MakeManualActionable(List<UpdateCandidate> all)
        {
            int n = 0;
            if (all == null) return 0;
            foreach (var c in all)
            {
                if (c == null || c.Kind != UpdateKind.Programa || c.Status != UpdateStatus.Disponible || c.Installable) continue;
                string url = Fmt.WebUrl(c.InfoUrl);
                Uri u;
                if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out u) || !SiteVersions.IsPublicPage(u)) continue;
                string d = c.Detail ?? "";
                if (d.IndexOf("administrador", StringComparison.OrdinalIgnoreCase) >= 0 || d.IndexOf("otro usuario", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                c.ProviderName = SiteProvider.ProviderId;
                c.Installable = true;
                c.Recommended = false;      // sin instalador verificable: queda entre las opcionales (se abre su página)
                c.Detail = d.TrimEnd() + " Marcala y al actualizar se abre su página de descarga (" + u.Host + ").";
                n++;
            }
            return n;
        }

        // Búsquedas canceladas cuyos hilos todavía no terminaron.
        readonly List<ProviderRun> lingering = new List<ProviderRun>();

        /// <summary>Cuánto puede tardar un proveedor en decir si está disponible.</summary>
        public static TimeSpan AvailabilityLimit = TimeSpan.FromSeconds(120);

        /// <summary>
        /// <see cref="IUpdateProvider.Unavailable"/> con plazo. Se pregunta a cada proveedor uno detrás de otro antes de
        /// buscar: crear la sesión de Windows Update arranca su servicio, y con el servicio trabado «arrancando» la búsqueda
        /// entera quedaba esperando antes de empezar.
        /// </summary>
        static string CheckAvailable(IUpdateProvider p, UpdateContext ctx)
        {
            string reason = null;
            Exception error = null;
            var t = new Thread(delegate()
            {
                try { reason = p.Unavailable(ctx); }
                catch (Exception ex) { error = ex; }
            });
            t.IsBackground = true;
            t.Name = "disponible-" + p.Name;
            t.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!t.Join(250))
            {
                if (ctx.CancelRequested) return "cancelado";
                if (sw.Elapsed > AvailabilityLimit)
                    return ProviderTitle(p.Name) + " no respondió en " + Fmt.Duration(AvailabilityLimit) + " (su servicio de Windows está trabado): no se consultó esta vez.";
            }
            if (error != null) return ProviderTitle(p.Name) + " no está disponible: " + Wmi.Describe(error);
            return reason;
        }

        /// <summary>Plazo total de una búsqueda de actualizaciones.</summary>
        public static TimeSpan ScanLimit = TimeSpan.FromMinutes(90);

        /// <summary>Búsquedas pesadas que corren a la vez: 1 con dos núcleos o menos, 2 con más.</summary>
        public static int MaxHeavyConcurrent = HeavyConcurrencyFor(Environment.ProcessorCount);

        /// <summary>2.0.21: en modo liviano, de a una siempre.</summary>
        static int HeavyNow { get { return Performance.Light ? 1 : MaxHeavyConcurrent; } }

        public static int HeavyConcurrencyFor(int processors)
        {
            return processors <= 2 ? 1 : 2;
        }

        /// <summary>¿Es una búsqueda que exige al equipo (no sólo consultas web)? winget, Windows Update y la herramienta del fabricante (Dell Command | Update).</summary>
        public static bool IsHeavy(string providerId)
        {
            return providerId == WingetProvider.ProviderId || providerId == WindowsUpdateProvider.ProviderId || providerId == OemToolsProvider.ProviderId;
        }

        /// <summary>Espera un poco a las búsquedas canceladas que siguen corriendo (hasta 20 s, cancelable).</summary>
        void WaitForLingering(UpdateContext ctx)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool told = false;
            while (true)
            {
                lock (lingering) lingering.RemoveAll(delegate(ProviderRun r) { return !r.Thread.IsAlive; });
                int left;
                lock (lingering) left = lingering.Count;
                if (left == 0 || sw.ElapsedMilliseconds > 20000 || ctx.CancelRequested) return;
                if (!told) { told = true; ctx.Report("Esperando que termine la búsqueda anterior…"); }
                Thread.Sleep(500);
            }
        }

        bool IsLingering(IUpdateProvider p)
        {
            lock (lingering)
                foreach (var r in lingering) if (r.Provider == p && r.Thread.IsAlive) return true;
            return false;
        }

        /// <summary>Antivirus registrados en el Centro de seguridad de Windows (no existe en Windows Server).</summary>
        static List<AntivirusStatus> SecurityCenterAntivirus(UpdateContext ctx)
        {
            var list = new List<AntivirusStatus>();
            if (ctx.Os == null || !ctx.Os.IsWindows || ctx.Os.IsServer) return list;
            try
            {
                string err;
                foreach (var o in Wmi.TryQuery(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct", out err, 30))
                    list.Add(AntivirusStatus.FromProductState(Wmi.S(o, "displayName"), Wmi.L(o, "productState") ?? 0));
                if (err != null) Log.Warn("Centro de seguridad: " + err);
            }
            catch (Exception ex) { Log.Warn("Centro de seguridad: " + ex.Message); }
            if (list.Count == 0)
            {
                // 2.0.13 (captura del 07/11/2026: Avast One quedó «No verificable»): si WMI no contestó (ocupado durante la búsqueda), lo mismo
                // se lee de donde lo guarda el Centro de seguridad: HKLM\SOFTWARE\Microsoft\Security Center\Provider\Av\{…} (DISPLAYNAME y STATE).
                list.AddRange(SecurityCenterFromRegistry());
                if (list.Count > 0) Log.Info("Centro de seguridad: leído del registro (" + list.Count + " antivirus)");
            }
            return list;
        }

        static List<AntivirusStatus> SecurityCenterFromRegistry()
        {
            var list = new List<AntivirusStatus>();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return list;
            try
            {
                using (var av = Reg.Open(Microsoft.Win32.RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Security Center\Provider\Av"))
                {
                    if (av == null) return list;
                    foreach (string sub in av.GetSubKeyNames())
                        using (var k = av.OpenSubKey(sub))
                        {
                            if (k == null) continue;
                            string name = k.GetValue("DISPLAYNAME") as string;
                            object state = k.GetValue("STATE");
                            if (string.IsNullOrEmpty(name) || !(state is int)) continue;
                            // Sólo para CONFIRMAR que está activado y al día (misma codificación que productState): una lectura que diga
                            // otra cosa no se usa para alarmar; la fila queda «No verificable» como antes.
                            var a = AntivirusStatus.FromProductState(name, (long)(uint)(int)state);
                            if (a.Enabled && a.UpToDate) list.Add(a);
                        }
                }
            }
            catch (Exception ex) { Log.Warn("Centro de seguridad (registro): " + ex.Message); }
            return list;
        }

        static string ProviderTitle(string id)
        {
            switch (id)
            {
                case WingetProvider.ProviderId: return "winget";
                case WindowsUpdateProvider.ProviderId: return "Windows Update";
                case StoreProvider.ProviderId: return "Microsoft Store";
                case BrowserProvider.ProviderId: return "navegadores";
                case VendorProvider.ProviderId: return "consulta a los fabricantes";
                case CatalogDriverProvider.ProviderId: return "Catálogo de Microsoft Update";
                case OemToolsProvider.ProviderId: return "herramienta del fabricante del equipo";
                case SiteProvider.ProviderId: return "páginas oficiales";
                case ExternalProvider.ProviderId: return "Steam y tu carpeta de versiones";
                default: return id;
            }
        }

        /// <summary>
        /// Una app de la Store que winget ya comparó (origen msstore) no se repite como
        /// «no verificable» desde el proveedor de la Store.
        /// </summary>
        public static void Deduplicate(List<UpdateCandidate> all)
        {
            DeduplicatePrograms(all);
            DeduplicateOthers(all);
        }

        /// <summary>Navegadores y fabricantes contra winget (antes de leer las páginas oficiales).</summary>
        public static void DeduplicatePrograms(List<UpdateCandidate> all)
        {
            DeduplicateBrowsers(all);
            DeduplicateVendor(all);
        }

        /// <summary>Controladores del catálogo contra Windows Update, y apps de la Store que winget ya compara.</summary>
        public static void DeduplicateOthers(List<UpdateCandidate> all)
        {
            DeduplicateCatalogDrivers(all);
            // Una app que winget ya compara —del origen msstore, o del repositorio winget con el mismo
            // nombre (App Installer, Terminal Windows)— no se repite como fila de la Store.
            var wingetStore = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var c in all)
                if (c.ProviderName == WingetProvider.ProviderId && (c.Kind == UpdateKind.Store || c.Kind == UpdateKind.Programa) && !string.IsNullOrEmpty(c.Name))
                    wingetStore.Add(c.Name.Trim());
            all.RemoveAll(delegate(UpdateCandidate c)
            {
                return c.ProviderName == StoreProvider.ProviderId && c.Identifier != StoreProvider.AllAppsId && c.Name != null && wingetStore.Contains(c.Name.Trim());
            });
        }

        /// <summary>
        /// Chrome, Edge y Firefox se comparan con el fabricante (BrowserProvider): la fila de winget
        /// o del registro del mismo navegador sobra. Si la consulta al fabricante falló, queda la de
        /// winget, que al menos dice algo.
        /// </summary>
        public static void DeduplicateBrowsers(List<UpdateCandidate> all)
        {
            var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in all)
            {
                if (c.ProviderName != BrowserProvider.ProviderId || c.Status == UpdateStatus.Error) continue;
                string key = BrowserCatalog.KeyFor(BaseName(c.Name));
                if (key != null) verified.Add(key);
            }
            all.RemoveAll(delegate(UpdateCandidate c)
            {
                if (c.Kind != UpdateKind.Programa) return false;
                string key = BrowserCatalog.KeyFor(BaseName(c.Name));
                if (key == null) return false;
                if (c.ProviderName == BrowserProvider.ProviderId) return c.Status == UpdateStatus.Error && HasOther(all, key);
                return verified.Contains(key);
            });
        }

        /// <summary>
        /// Programas consultados con su fabricante (catálogo o GitHub): la fila del fabricante
        /// reemplaza a las de winget o del registro que representa (Office: todas sus entradas),
        /// salvo que winget ya ofrezca una actualización instalable, que se prefiere porque trae el
        /// hash de su catálogo. Si la consulta al fabricante falló, quedan las otras filas.
        /// </summary>
        public static void DeduplicateVendor(List<UpdateCandidate> all)
        {
            var drop = new HashSet<UpdateCandidate>();
            foreach (var v in all)
            {
                if (v.ProviderName != VendorProvider.ProviderId || v.Kind != UpdateKind.Programa) continue;
                var others = all.FindAll(delegate(UpdateCandidate o) { return !drop.Contains(o) && VendorProvider.Covers(v, o); });
                // 2.0.12: winget se prefiere sólo si ofrece EXACTAMENTE la versión que publica el fabricante (trae el hash de su
                // catálogo). Si ofrece otra, manda la página oficial: una más vieja se reemplaza por la del fabricante, y una más nueva
                // que el fabricante todavía no publica no se ofrece (7-Zip: una 26.06 en GitHub que 7-zip.org no publicaba).
                var sameOffer = others.Find(delegate(UpdateCandidate o)
                {
                    return o.Status == UpdateStatus.Disponible && o.Installable &&
                           (v.Status == UpdateStatus.Error || !VendorPublishes(v) || VersionUtil.Compare(o.AvailableVersion, v.AvailableVersion) == 0);
                });
                if (v.Status == UpdateStatus.Error ? others.Count > 0 : sameOffer != null) { drop.Add(v); continue; }
                foreach (var o in others)
                {
                    drop.Add(o);
                    if (o.Status == UpdateStatus.Disponible && VendorPublishes(v) && VersionUtil.IsNewer(o.AvailableVersion, v.AvailableVersion))
                    {
                        var plan = v.ProviderTag as VendorPlan;
                        string product = plan != null && plan.Product.Length > 0 ? plan.Product : v.Name;
                        string site = plan != null && plan.Check != null && !string.IsNullOrEmpty(plan.Check.Domain) ? plan.Check.Domain : "su fabricante";
                        v.Detail = (v.Detail ?? "").TrimEnd() + " " + (o.ProviderName == WingetProvider.ProviderId ? "winget" : "Otra fuente") + " ofrece la " +
                                   o.AvailableVersion + ", pero la página oficial de " + product + " (" + site + ") publica la " + v.AvailableVersion +
                                   ": manda la página oficial, y se va a ofrecer cuando la publique.";
                    }
                }
            }
            all.RemoveAll(delegate(UpdateCandidate c) { return drop.Contains(c); });
        }

        /// <summary>¿La fila del fabricante leyó una versión publicada? (no un error ni una versión ilegible)</summary>
        static bool VendorPublishes(UpdateCandidate v)
        {
            return v != null && v.Status != UpdateStatus.Error && VersionUtil.Parse(v.AvailableVersion) != null;
        }

        /// <summary>El dispositivo de una fila de controlador (Windows Update o catálogo), o null.</summary>
        static DriverInfo DeviceOf(UpdateCandidate c)
        {
            if (c == null || c.Kind != UpdateKind.Controlador) return null;
            var d = c.ProviderTag as DriverInfo;
            if (d != null) return d;
            var plan = c.ProviderTag as CatalogPlan;
            if (plan != null) return plan.Driver;
            return WindowsUpdateProvider.InstalledOf(c);
        }

        static string DeviceKey(DriverInfo d)
        {
            if (d == null) return null;
            string k = !string.IsNullOrEmpty(d.DeviceId) ? d.DeviceId : (d.HardwareId ?? "") + "|" + (d.DeviceName ?? "");
            return k.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// Un dispositivo, una fila. Si Windows Update ofrece algo para el dispositivo, se queda esa
        /// (es lo que el fabricante del equipo eligió para este modelo) y la del catálogo se descarta.
        /// Si no, la del catálogo (más nueva, o «al día» comparado con las dos fuentes) reemplaza a la
        /// «al día» de Windows Update.
        /// </summary>
        public static void DeduplicateCatalogDrivers(List<UpdateCandidate> all)
        {
            var wuOffers = new HashSet<string>();
            var catalog = new HashSet<string>();
            foreach (var c in all)
            {
                string k = DeviceKey(DeviceOf(c));
                if (k == null) continue;
                // 2.0.25: también la que ya se instaló y espera el reinicio (la del catálogo diría lo mismo, o volvería a ofrecer otra).
                if (c.ProviderName == WindowsUpdateProvider.ProviderId && ((c.Installable && c.Status == UpdateStatus.Disponible) || c.Status == UpdateStatus.RequiereReinicio)) wuOffers.Add(k);
                else if (c.ProviderName == CatalogDriverProvider.ProviderId) catalog.Add(k);
            }
            all.RemoveAll(delegate(UpdateCandidate c)
            {
                string k = DeviceKey(DeviceOf(c));
                if (k == null) return false;
                if (c.ProviderName == CatalogDriverProvider.ProviderId) return wuOffers.Contains(k);
                if (c.ProviderName == WindowsUpdateProvider.ProviderId && !(c.Installable && c.Status == UpdateStatus.Disponible))
                    return catalog.Contains(k) && !wuOffers.Contains(k) && c.ProviderTag is DriverInfo;
                return false;
            });
        }

        static string BaseName(string name)
        {
            string n = (name ?? "").Trim();
            if (n.EndsWith(" (usuario)")) n = n.Substring(0, n.Length - " (usuario)".Length);
            return n;
        }

        static bool HasOther(List<UpdateCandidate> all, string key)
        {
            foreach (var c in all)
                if (c.ProviderName != BrowserProvider.ProviderId && c.Kind == UpdateKind.Programa && BrowserCatalog.KeyFor(BaseName(c.Name)) == key) return true;
            return false;
        }

        static int StatusRank(UpdateStatus s)
        {
            switch (s)
            {
                case UpdateStatus.Disponible: return 0;
                case UpdateStatus.Pendiente: return 1;
                case UpdateStatus.Error: return 2;
                case UpdateStatus.NoVerificable: return 3;
                case UpdateStatus.AlDia: return 4;
                case UpdateStatus.Ignorada: return 6;
                default: return 5;
            }
        }

        public static void Sort(List<UpdateCandidate> all)
        {
            all.Sort(delegate(UpdateCandidate a, UpdateCandidate b)
            {
                int c = StatusRank(a.Status).CompareTo(StatusRank(b.Status));
                if (c != 0) return c;
                c = ((int)a.Kind).CompareTo((int)b.Kind);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        /// <summary>
        /// ¿Se puede accionar este elemento ahora? Lo disponible, lo pendiente y lo que falló
        /// (para reintentar). La acción de Microsoft Store se puede repetir siempre, porque es
        /// una verificación: aunque figure «al día», el usuario puede pedirle que busque de nuevo.
        /// </summary>
        public static bool CanInstall(UpdateCandidate c)
        {
            if (c == null || !c.Installable) return false;
            switch (c.Status)
            {
                case UpdateStatus.Disponible:
                case UpdateStatus.Pendiente:
                case UpdateStatus.Fallida:
                    return true;
                case UpdateStatus.AlDia:
                    return c.Identifier == StoreProvider.AllAppsId;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Lo que se va a instalar: exactamente las filas VISIBLES que el usuario ve marcadas y
        /// que se pueden instalar. Lo que el filtro oculta no se instala nunca, aunque esté
        /// marcado: el usuario no lo tiene a la vista y no puede confirmar lo que no ve.
        /// </summary>
        public static List<UpdateCandidate> SelectionFromVisible(IEnumerable<KeyValuePair<UpdateCandidate, bool>> visibleRows)
        {
            var result = new List<UpdateCandidate>();
            if (visibleRows == null) return result;
            foreach (var kv in visibleRows)
                if (kv.Value && kv.Key != null && CanInstall(kv.Key) && !result.Contains(kv.Key)) result.Add(kv.Key);
            return result;
        }

        /// <summary>Filtro de tipos para la CLI: "todas", "programas", "controladores", "store", "windows".</summary>
        public static bool MatchesKind(UpdateCandidate c, ICollection<string> kinds)
        {
            if (kinds == null || kinds.Count == 0 || kinds.Contains("todas") || kinds.Contains("todo")) return true;
            switch (c.Kind)
            {
                case UpdateKind.Programa: return kinds.Contains("programas");
                case UpdateKind.Controlador: return kinds.Contains("controladores");
                case UpdateKind.Store: return kinds.Contains("store");
                default: return kinds.Contains("windows");
            }
        }

        /// <summary>
        /// Instala los candidatos seleccionados. Orden: programas → Store → controladores → Windows Update
        /// (lo que suele pedir reinicio, al final).
        /// </summary>
        public void Install(UpdateContext ctx, UpdateSession report, List<UpdateCandidate> selected, bool restorePoint, Action<UpdateCandidate> changed)
        {
            // 2.0.7: una sola instalación a la vez en el equipo (ventana, consola o dos ventanas abiertas). Dos a la vez hacían
            // fallar a la segunda con «hay otra instalación en curso» (msiexec 1618, winget) y parecía que no actualizaba.
            using (var guard = InstallLock.TryEnter(ctx))
            {
                if (!guard.Acquired)
                {
                    foreach (var c in selected ?? new List<UpdateCandidate>())
                    {
                        if (!CanInstall(c)) continue;
                        c.Status = UpdateStatus.Fallida;
                        c.Result = InstallLock.BusyMessage;
                        SafeNotify(changed, c);
                    }
                    ctx.Report(InstallLock.BusyMessage);
                    return;
                }
                InstallLocked(ctx, report, selected, restorePoint, changed);
            }
        }

        void InstallLocked(UpdateContext ctx, UpdateSession report, List<UpdateCandidate> selected, bool restorePoint, Action<UpdateCandidate> changed)
        {
            var queue = new List<UpdateCandidate>();
            // CanInstall y no sólo Installable: una fila ya «Actualizada» o «Al día» que quedó marcada
            // (p. ej. desde la CLI o una selección vieja) no se vuelve a instalar.
            foreach (var c in selected) if (CanInstall(c) && !queue.Contains(c)) queue.Add(c);
            if (queue.Count == 0) { ctx.Report("No hay elementos instalables seleccionados."); return; }
            // Política de la empresa (2.0): se vuelve a controlar al instalar (defensa en profundidad: la selección pudo venir
            // de otra búsqueda o de la consola).
            var policy = PolicyForTests ?? Policy.Current;
            if (policy.RequireRestorePoint) restorePoint = true;
            if (Interactive && !policy.AllowInteractiveInstallers) Interactive = false;
            foreach (var c in queue.ToArray())
            {
                string why = PolicyRefusal(policy, ctx, c);
                if (why == null) continue;
                queue.Remove(c);
                c.Status = UpdateStatus.Fallida;
                c.Result = char.ToUpperInvariant(why[0]) + why.Substring(1);
                SafeNotify(changed, c);
                var d = AuditData(c, c.InstalledVersion);
                d["motivo"] = why;
                AuditTrail.Write("bloqueada", d, EventLogWriter.Ids.PolicyBlocked, EventLogWriter.Level.Warning, "No se instaló «" + c.Name + "»: " + why + ".");
            }
            if (queue.Count == 0) { ctx.Report("La política de la empresa no permite instalar lo seleccionado."); return; }
            {
                var d = new Dictionary<string, object>();
                var names = new List<object>();
                foreach (var c in queue) names.Add(c.Name + (string.IsNullOrEmpty(c.AvailableVersion) ? "" : " → " + c.AvailableVersion));
                d["elementos"] = names; d["puntoDeRestauracion"] = restorePoint; d["interactiva"] = Interactive; d["via"] = Via;
                AuditTrail.Write("instalacion-inicio", d, EventLogWriter.Ids.InstallStarted, EventLogWriter.Level.Information,
                                 "Se van a actualizar " + Fmt.Plural(queue.Count, "elemento", "elementos") + " (" + Via + "): " + string.Join("; ", names.ConvertAll(delegate(object o) { return (string)o; }).ToArray()));
            }
            // Los proveedores buscan cada programa en el inventario (arquitectura, alcance, carpeta, procesos
            // abiertos). Después de cancelar, la interfaz usa un contexto nuevo y vacío: sin esto, instalaba
            // sin saber nada del programa instalado.
            InventoryLoader.EnsurePrograms(ctx);
            // Una búsqueda cancelada que todavía corre usa los mismos objetos: se la espera un poco y, si
            // sigue, ese proveedor no instala esta vez (más abajo).
            WaitForLingering(ctx);
            if (ready.Count == 0)
            {
                // Instalación sin búsqueda previa en este proceso: se inicializan los proveedores.
                foreach (var p in providers) if (CheckAvailable(p, ctx) == null) ready[p.Name] = p;
            }

            foreach (var p in providers)
            {
                var wg = p as WingetProvider;
                if (wg != null) { wg.Interactive = Interactive; wg.CloseRunning = CloseRunning; wg.Unattended = !string.Equals(Via, "interfaz", StringComparison.OrdinalIgnoreCase); }
                var br = p as BrowserProvider;
                if (br != null) br.Interactive = Interactive;
                var vp = p as VendorProvider;
                if (vp != null) { vp.Interactive = Interactive; vp.CloseRunning = CloseRunning; vp.Unattended = !string.Equals(Via, "interfaz", StringComparison.OrdinalIgnoreCase); }
                var oem = p as OemToolsProvider;
                if (oem != null) oem.Interactive = Interactive;
            }
            // Carpetas temporales que quedaron de una instalación anterior (un archivo en uso al terminar).
            BrowserProvider.CleanupStaleDownloadDirs();
            // 2.0.18: lo que iba a abrir su página se intenta instalar antes: se vuelve a buscar su instalador ahora (en la búsqueda pudo
            // faltar tiempo, o el servidor no contestar).
            RetrySitePages(ctx, queue, policy, changed);
            // La versión de ANTES, para el historial: los proveedores la cambian al confirmar la instalación.
            var before = new Dictionary<UpdateCandidate, string>();
            foreach (var c in queue) before[c] = c.InstalledVersion;
            DateTime started = DateTime.Now;
            foreach (var c in queue) { c.Status = UpdateStatus.Pendiente; c.Result = "En cola"; SafeNotify(changed, c); }

            if (restorePoint)
            {
                bool hasSystemChange = false;
                foreach (var c in queue) if (c.Kind == UpdateKind.Controlador || c.Kind == UpdateKind.WindowsUpdate || c.Kind == UpdateKind.Programa) hasSystemChange = true;
                if (hasSystemChange) CreateRestorePoint(ctx, "Actualizador: antes de actualizar " + queue.Count + " elementos");
            }

            // La herramienta del fabricante del equipo (BIOS incluida) va después de los controladores sueltos y antes de Windows Update.
            // 2.0.17: Microsoft Store va después de winget (winget es una app de la Store: si la Store la actualiza mientras winget
            // trabaja, lo corta) y MIENTRAS se instala lo demás. La Store descarga e instala por su cuenta en segundo plano y acá
            // sólo se mide qué cambió: antes todo lo demás (navegadores, fabricantes, controladores, Windows Update) esperaba esos
            // minutos sin hacer nada.
            string[] order = { WingetProvider.ProviderId, StoreProvider.ProviderId, BrowserProvider.ProviderId, VendorProvider.ProviderId, SiteProvider.ProviderId, ExternalProvider.ProviderId, CatalogDriverProvider.ProviderId, OemToolsProvider.ProviderId, WindowsUpdateProvider.ProviderId };
            Thread storeThread = null;
            StoreProvider storeRunning = null;
            // 2.0.25: Windows Update va último, pero sus descargas empiezan ya, mientras se instala lo demás.
            {
                var wuBatch = queue.FindAll(delegate(UpdateCandidate c) { return c.ProviderName == WindowsUpdateProvider.ProviderId; });
                IUpdateProvider wup;
                if (wuBatch.Count > 0 && ready.TryGetValue(WindowsUpdateProvider.ProviderId, out wup) && !IsLingering(wup) && wup is WindowsUpdateProvider)
                {
                    try { ((WindowsUpdateProvider)wup).StartPrefetch(ctx, wuBatch); }
                    catch (Exception ex) { Log.Warn("Windows Update: descarga anticipada: " + ex.Message); }
                }
            }
            try
            {
                foreach (string providerId in order)
                {
                    var batch = new List<UpdateCandidate>();
                    foreach (var c in queue) if (c.ProviderName == providerId) batch.Add(c);
                    if (providerId == WindowsUpdateProvider.ProviderId)
                        batch.Sort(delegate(UpdateCandidate a, UpdateCandidate b) { return (a.Kind == UpdateKind.Controlador ? 0 : 1).CompareTo(b.Kind == UpdateKind.Controlador ? 0 : 1); });
                    if (batch.Count == 0) continue;
                    IUpdateProvider provider;
                    if (!ready.TryGetValue(providerId, out provider))
                    {
                        foreach (var c in batch) { c.Status = UpdateStatus.Fallida; c.Result = ProviderTitle(providerId) + " no está disponible"; SafeNotify(changed, c); }
                        continue;
                    }
                    if (IsLingering(provider))
                    {
                        foreach (var c in batch)
                        {
                            c.Status = UpdateStatus.Fallida;
                            c.Result = "La búsqueda anterior de " + ProviderTitle(providerId) + " todavía no terminó (se canceló, pero no se puede interrumpir): reintentá en unos minutos";
                            SafeNotify(changed, c);
                        }
                        continue;
                    }
                    var current = provider;
                    var currentBatch = batch;
                    string currentId = providerId;
                    ThreadStart run = delegate()
                    {
                        try { current.Install(ctx, currentBatch, delegate(UpdateCandidate c) { SafeNotify(changed, c); }); }
                        catch (Exception ex)
                        {
                            Log.Error("Instalación con " + currentId, ex);
                            foreach (var c in currentBatch)
                                if (c.Status == UpdateStatus.Instalando || c.Status == UpdateStatus.Pendiente)
                                { c.Status = UpdateStatus.Fallida; c.Result = Wmi.Describe(ex); SafeNotify(changed, c); }
                        }
                    };
                    var store = provider as StoreProvider;
                    if (store != null && StoreInBackground)
                    {
                        storeRunning = store;
                        storeThread = new Thread(run);
                        storeThread.IsBackground = true;
                        storeThread.Name = "instalacion-store";
                        storeThread.Start();
                        continue;
                    }
                    run();
                    if (store != null) ReconcileStore(report, store, changed);
                }
            }
            finally
            {
                // 2.0.28: nada termina sin resultado. Lo que su fuente no llegó a tocar (quedó «En cola» o «Instalando») se informa.
                if (storeThread == null || !storeThread.IsAlive) MarkUntouched(queue, ctx.CancelRequested, changed);
                // La Store siguió midiendo mientras se instalaba lo demás: se la espera (termina sola; con «Detener», enseguida).
                if (storeThread != null)
                {
                    if (storeThread.IsAlive && !ctx.CancelRequested) ctx.Report("Esperando a que Microsoft Store termine de actualizar sus apps…");
                    try { storeThread.Join(); } catch (Exception ex) { Log.Warn("Espera de Microsoft Store: " + ex.Message); }
                    try { ReconcileStore(report, storeRunning, changed); } catch (Exception ex) { Log.Warn("Microsoft Store y winget: " + ex.Message); }
                    MarkUntouched(queue, ctx.CancelRequested, changed);
                }
                // Lo instalado cambió: la próxima búsqueda vuelve a leer el registro, los controladores y la Store.
                InventoryLoader.Invalidate(ctx);
                // Historial: también si la tanda se cortó por un error o se canceló a la mitad.
                if (HistoryPath != "")
                {
                    try { UpdateHistory.Append(HistoryPath ?? UpdateHistory.DefaultPath, UpdateHistory.FromBatch(queue, before, started, Via, ctx.IsAdmin)); }
                    catch (Exception ex) { Log.Warn("Historial de actualizaciones: " + ex.Message); }
                }
            }

            // Registro de auditoría (2.0): una línea por elemento, con el resultado y de dónde salió.
            foreach (var c in queue)
            {
                bool ok = c.Status == UpdateStatus.Instalada || c.Status == UpdateStatus.RequiereReinicio;
                string prev;
                before.TryGetValue(c, out prev);
                var d = AuditData(c, prev);
                AuditTrail.Write("actualizacion", d, ok ? EventLogWriter.Ids.UpdateSucceeded : EventLogWriter.Ids.UpdateFailed,
                                 ok ? EventLogWriter.Level.Information : EventLogWriter.Level.Warning,
                                 (ok ? "Actualizado: " : "No se actualizó: ") + c.Name + " " + (prev ?? "") + " → " + (c.AvailableVersion ?? "") + " (" + (c.Source ?? c.ProviderName) + "). " +
                                 UpdateCandidate.StatusText(c.Status) + (string.IsNullOrEmpty(c.Result) ? "" : ": " + c.Result));
            }
            var tally = InstallTally.Of(queue);
            string text = tally.Text();
            if (tally.Reboot > 0) text = text.TrimEnd('.') + "; " + (tally.Reboot == 1 ? "1 requiere" : tally.Reboot + " requieren") + " reiniciar el equipo.";
            if (tally.RestartFirst > 0) text = text.TrimEnd('.') + "; " + (tally.RestartFirst == 1 ? "1 se puede instalar" : tally.RestartFirst + " se pueden instalar") + " recién después de reiniciar.";
            ctx.Report("Actualización terminada: " + text);
            var fin = new Dictionary<string, object>();
            fin["resumen"] = text;
            AuditTrail.Write("instalacion-fin", fin, EventLogWriter.Ids.InstallFinished, EventLogWriter.Level.Information, "Actualización terminada: " + text);
        }

        /// <summary>
        /// 2.0.28 (relevamiento): un elemento que terminó la tanda «En cola» o «Instalando» no se instaló y no dice por qué (su fuente no
        /// lo procesó, o terminó sin informar). Queda «Falló» con el motivo; si se canceló, «Pendiente: cancelada». Devuelve cuántos cambió.
        /// </summary>
        public static int MarkUntouched(List<UpdateCandidate> queue, bool cancelled, Action<UpdateCandidate> changed)
        {
            int n = 0;
            if (queue == null) return 0;
            foreach (var c in queue)
            {
                if (c == null) continue;
                bool queued = c.Status == UpdateStatus.Pendiente && c.Result == "En cola";
                if (!queued && c.Status != UpdateStatus.Instalando) continue;
                if (cancelled) { c.Status = UpdateStatus.Pendiente; c.Result = InstallTally.CancelledBeforeStart; }
                else
                {
                    c.Status = UpdateStatus.Fallida;
                    c.Result = queued ? "No se llegó a instalar: su fuente terminó sin procesarla. Volvé a buscar actualizaciones y reintentá"
                                      : "La instalación terminó sin informar el resultado. Volvé a buscar actualizaciones para ver si quedó";
                    Log.Warn(c.Name + ": " + c.Result);
                }
                SafeNotify(changed, c);
                n++;
            }
            return n;
        }

        /// <summary>Sólo para las pruebas: reemplaza la búsqueda del instalador al instalar.</summary>
        internal static Func<UpdateContext, SitePlan, InstallerLead> FindLeadForTests;

        /// <summary>
        /// 2.0.18: antes de abrir la página de un programa, se busca de nuevo su instalador (<see cref="SiteInstallers.Find"/>). Si aparece, la
        /// fila pasa a instalarse verificada como cualquier otra del fabricante; si no, queda el motivo para mostrarlo al abrir la página.
        /// </summary>
        void RetrySitePages(UpdateContext ctx, List<UpdateCandidate> queue, Policy policy, Action<UpdateCandidate> changed)
        {
            if (!SiteVersions.FindInstallers || !policy.IsSourceAllowed(VendorProvider.ProviderId)) return;
            IUpdateProvider vendor;
            if (!ready.TryGetValue(VendorProvider.ProviderId, out vendor) && FindLeadForTests == null) return;
            foreach (var c in queue)
            {
                if (ctx.CancelRequested) return;
                var sp = c.ProviderTag as SitePlan;
                if (c.ProviderName != SiteProvider.ProviderId || sp == null || sp.Program == null || sp.Reading == null) continue;
                InstallerLead lead = null;
                string why = null;
                ctx.Report("Buscando el instalador de " + c.Name + " " + sp.Reading.Latest + "…");
                InstallerSupport.Progress(c, changed, "Buscando su instalador…");
                try
                {
                    if (FindLeadForTests != null) lead = FindLeadForTests(ctx, sp);
                    else
                    {
                        string html = null;
                        try { html = SiteVersions.Fetch(sp.Reading.Url, new Dictionary<string, string>()); }
                        catch (Exception ex) { why = Http.Describe(ex); }
                        lead = SiteInstallers.Find(ctx, sp.Program, sp.Reading.Latest, sp.Reading.Url, html, sp.Winget, out why);
                    }
                }
                catch (OperationCanceledByUserException) { return; }
                catch (Exception ex) { why = Http.Describe(ex); Log.Warn("Instalador de " + c.Name + ": " + why); }
                if (lead == null)
                {
                    if (!string.IsNullOrEmpty(why)) sp.Why = why;
                    Log.Info(c.Name + ": sin instalador también al instalar (" + (sp.Why ?? "sin motivo") + "); se abre su página");
                    continue;
                }
                // Se guarda la fila como estaba: si con el instalador no se puede instalar desde acá (administrador), vuelve a abrir la página.
                string pn = c.ProviderName, src = c.Source, det = c.Detail, id = c.Identifier, info = c.InfoUrl, av = c.AvailableVersion;
                object tag = c.ProviderTag;
                bool inst = c.Installable, rec = c.Recommended;
                var st = c.Status;
                SiteVersions.Apply(ctx, c, sp.Program, sp.Reading, sp.Strict, lead, null);
                if (c.ProviderName == VendorProvider.ProviderId && c.Installable)
                {
                    Log.Info(c.Name + ": se encontró su instalador al instalar (" + lead.Url + ")");
                    continue;
                }
                c.ProviderName = pn; c.Source = src; c.Detail = det; c.Identifier = id; c.InfoUrl = info; c.AvailableVersion = av; c.ProviderTag = tag;
                c.Installable = inst; c.Recommended = rec; c.Status = st;
            }
        }

        /// <summary>
        /// 2.0.17: Microsoft Store se actualiza mientras se instala lo demás (después de winget). Las pruebas lo pueden apagar para
        /// instalar todo en un solo hilo.
        /// </summary>
        public static bool StoreInBackground = true;

        /// <summary>Lo que la Store actualizó ya no es un error ni una actualización pendiente de winget.</summary>
        static void ReconcileStore(UpdateSession report, StoreProvider store, Action<UpdateCandidate> changed)
        {
            if (store == null || report == null) return;
            List<UpdateCandidate> rows;
            lock (report.Updates) rows = new List<UpdateCandidate>(report.Updates);
            int n = StoreProvider.Reconcile(rows, store.LastChanges, delegate(UpdateCandidate c) { SafeNotify(changed, c); });
            if (n > 0) Log.Info("Microsoft Store actualizó " + Fmt.Plural(n, "programa que ofrecía winget", "programas que ofrecía winget"));
        }

        // ================================================================== Política de la empresa (2.0)

        /// <summary>Sólo para las pruebas: una política fija en vez de la del equipo.</summary>
        internal static Policy PolicyForTests;

        /// <summary>La política manda sobre las opciones del usuario: winget automático y el catálogo de fuentes.</summary>
        public static void ApplyPolicyToOptions(Policy policy, UpdateOptions options)
        {
            if (policy == null || options == null) return;
            if (!policy.AllowInstallWinget) options.InstallWinget = false;
            if (!string.IsNullOrEmpty(policy.SourcesCatalogUrl)) options.SourcesCatalogUrl = policy.SourcesCatalogUrl;
            else if (policy.LockSettings) options.SourcesCatalogUrl = "";
            bool anyVendor = policy.IsSourceAllowed(VendorProvider.ProviderId) || policy.IsSourceAllowed("github") || policy.IsSourceAllowed(SiteProvider.ProviderId) ||
                             policy.IsSourceAllowed(CatalogDriverProvider.ProviderId);
            if (!anyVendor) options.QueryVendors = false;
            if (policy.LockSettings) options.QueryVendors = anyVendor;
            if (policy.LockSettings || !policy.IsSourceAllowed("github")) options.IncludePreReleases = false;   // betas: nunca en equipos con la configuración fijada
        }

        /// <summary>
        /// Marca lo que la política no deja instalar: queda a la vista («Sin verificar», estado Ignorada) con el motivo, sin
        /// poder marcarse. Devuelve cuántas filas bloqueó.
        /// </summary>
        public static int ApplyPolicy(Policy policy, List<UpdateCandidate> all)
        {
            if (policy == null || all == null) return 0;
            int n = 0;
            foreach (var c in all)
            {
                if (c == null || (c.Status != UpdateStatus.Disponible && c.Status != UpdateStatus.Pendiente)) continue;
                if (c.Identifier == StoreProvider.AllAppsId) continue;
                string why = policy.BlockReason(c.Name, c.Identifier, c.Publisher, c.Kind == UpdateKind.Programa);
                if (why == null && c.ProviderName == SiteProvider.ProviderId && !policy.AllowOpenPages) why = "la política de la empresa no permite abrir páginas de descarga";
                if (why == null) continue;
                c.StatusBeforeIgnore = c.Status;
                c.RecommendedBeforeIgnore = c.Recommended;
                c.ResultBeforeIgnore = c.Result;
                c.IgnoredBy = PolicyMarker;
                c.Status = UpdateStatus.Ignorada;
                c.Recommended = false;
                c.Selected = false;
                c.Detail = "No se instala: " + why + ". " + (c.Detail ?? "");
                n++;
            }
            return n;
        }

        /// <summary>Lo que se pone en <see cref="UpdateCandidate.IgnoredBy"/> cuando bloquea la política (no la lista del usuario).</summary>
        public static readonly object PolicyMarker = "politica";

        /// <summary>Por qué la política no deja instalar esta fila ahora, o null.</summary>
        public static string PolicyRefusal(Policy policy, UpdateContext ctx, UpdateCandidate c)
        {
            if (policy == null || c == null) return null;
            if (c.IgnoredBy == PolicyMarker) return "bloqueada por la política de la empresa";
            if (policy.RequireAdministrator && (ctx == null || !ctx.IsAdmin)) return "la política de la empresa exige ejecutar Actualizador como administrador para instalar";
            if (!policy.IsSourceAllowed(c.ProviderName)) return "la política de la empresa desactivó esa fuente (" + ProviderTitle(c.ProviderName) + ")";
            if (c.Identifier != StoreProvider.AllAppsId)
            {
                string why = policy.BlockReason(c.Name, c.Identifier, c.Publisher, c.Kind == UpdateKind.Programa);
                if (why != null) return why;
            }
            if (c.ProviderName == SiteProvider.ProviderId && !policy.AllowOpenPages) return "la política de la empresa no permite abrir páginas de descarga";
            var plan = c.ProviderTag as CatalogPlan;
            if (plan != null && plan.Force && !policy.AllowForcedDrivers) return "la política de la empresa no permite instalar controladores forzados";
            return null;
        }

        static Dictionary<string, object> AuditData(UpdateCandidate c, string before)
        {
            var d = new Dictionary<string, object>();
            d["nombre"] = c.Name ?? "";
            d["tipo"] = UpdateCandidate.KindText(c.Kind);
            d["identificador"] = c.Identifier ?? "";
            d["editor"] = c.Publisher ?? "";
            d["antes"] = before ?? "";
            d["ofrecida"] = c.AvailableVersion ?? "";
            d["ahora"] = c.InstalledVersion ?? "";
            d["fuente"] = c.ProviderName ?? "";
            d["origen"] = c.Source ?? "";
            d["estado"] = UpdateCandidate.StatusText(c.Status);
            d["resultado"] = c.Result ?? "";
            return d;
        }

        // ================================================================== Todo de una vez

        /// <summary>Vueltas extra después de la primera instalación.</summary>
        public const int MaxExtraRounds = 3;

        /// <summary>Después de instalar, volver a buscar e instalar lo que apareció recién.</summary>
        public bool RepeatUntilDone = true;

        /// <summary>Identifica una actualización entre búsquedas: proveedor, elemento y versión ofrecida.</summary>
        public static string RoundKey(UpdateCandidate c)
        {
            if (c == null) return "";
            return (c.ProviderName ?? "") + "|" + (string.IsNullOrEmpty(c.Identifier) ? (c.Name ?? "") : c.Identifier) + "|" + (c.AvailableVersion ?? "");
        }

        /// <summary>
        /// Lo que se instala en la vuelta siguiente: disponible, instalable y recomendado (lo mismo que «Marcar disponibles» y
        /// --aplicar), de los tipos que eligió el usuario, que no se intentó antes en esta tanda y que el usuario no dejó sin
        /// marcar a propósito. Nunca lo ignorado ni la herramienta del fabricante del equipo (puede incluir la BIOS).
        /// </summary>
        public static List<UpdateCandidate> NextRound(IEnumerable<UpdateCandidate> updates, ICollection<UpdateKind> kinds,
                                                      ICollection<string> attempted, ICollection<string> declined)
        {
            var next = new List<UpdateCandidate>();
            if (updates == null) return next;
            foreach (var c in updates)
            {
                if (c == null || c.Status != UpdateStatus.Disponible || !c.Recommended || !CanInstall(c) || c.IgnoredBy != null) continue;
                if (c.ProviderName == OemToolsProvider.ProviderId) continue;
                // Abrir páginas en el navegador sólo cuando el usuario lo eligió en la primera vuelta.
                if (c.ProviderName == SiteProvider.ProviderId) continue;
                if (kinds != null && kinds.Count > 0 && !kinds.Contains(c.Kind)) continue;
                string key = RoundKey(c);
                if ((attempted != null && attempted.Contains(key)) || (declined != null && declined.Contains(key))) continue;
                if (!next.Contains(c)) next.Add(c);
            }
            return next;
        }

        /// <summary>
        /// Instala lo elegido y, si <see cref="RepeatUntilDone"/>, vuelve a buscar e instala lo que apareció recién, hasta
        /// <see cref="MaxExtraRounds"/> vueltas más: una actualización de Windows que destraba otra, un controlador que hace
        /// aparecer un dispositivo, la dependencia que un programa actualizó. Antes había que pasar Actualizador 2 o 3 veces
        /// para que quedara todo al día. Se detiene cuando no aparece nada nuevo, cuando en una vuelta no se instaló nada o
        /// si se cancela. Devuelve todo lo que se intentó instalar (para el resumen).
        /// </summary>
        /// <param name="rescanned">Opcional: se llama después de cada búsqueda nueva con el número de vuelta (la interfaz rearma la lista).</param>
        public List<UpdateCandidate> InstallUntilDone(UpdateContext ctx, UpdateSession report, List<UpdateCandidate> selected, bool restorePoint,
                                                      Action<UpdateCandidate> changed, Action<int> rescanned)
        {
            var all = new List<UpdateCandidate>();
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var declined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kinds = new HashSet<UpdateKind>();
            var round = new List<UpdateCandidate>();
            if (selected != null) foreach (var c in selected) if (CanInstall(c) && !round.Contains(c)) round.Add(c);
            foreach (var c in round) { attempted.Add(RoundKey(c)); kinds.Add(c.Kind); }
            // Lo recomendado que el usuario dejó sin marcar no se instala en las vueltas siguientes.
            List<UpdateCandidate> before;
            lock (report.Updates) before = new List<UpdateCandidate>(report.Updates);
            foreach (var c in before) if (!round.Contains(c) && CanInstall(c)) declined.Add(RoundKey(c));

            Install(ctx, report, round, restorePoint, changed);
            all.AddRange(round);
            for (int n = 1; n <= MaxExtraRounds && RepeatUntilDone && !ctx.CancelRequested; n++)
            {
                bool progressed = round.Exists(delegate(UpdateCandidate c) { return c.Status == UpdateStatus.Instalada || c.Status == UpdateStatus.RequiereReinicio; });
                if (!progressed) break;
                ctx.Report("Vuelta " + (n + 1) + ": buscando lo que apareció después de instalar…");
                Log.Info("Todo de una vez: vuelta " + (n + 1) + " (se busca de nuevo)");
                CarryOver = CarryOverAfter(round);
                try { Scan(ctx, report); }
                finally { CarryOver = null; }
                if (rescanned != null) { try { rescanned(n); } catch (Exception ex) { Log.Warn("Lista después de buscar: " + ex.Message); } }
                List<UpdateCandidate> now;
                lock (report.Updates) now = new List<UpdateCandidate>(report.Updates);
                round = NextRound(now, kinds, attempted, declined);
                if (round.Count == 0) { ctx.Report("No apareció nada nuevo para instalar."); break; }
                foreach (var c in round) { c.Selected = true; attempted.Add(RoundKey(c)); }
                Log.Info("Vuelta " + (n + 1) + ": " + round.Count + " elementos nuevos");
                Install(ctx, report, round, false, changed);
                all.AddRange(round);
            }
            return all;
        }

        /// <summary>
        /// Controladores del catálogo seleccionados que Windows va a rechazar porque prefiere el del
        /// fabricante del equipo (o que ya rechazó): sólo se instalan forzados, si el usuario lo acepta.
        /// </summary>
        public static List<UpdateCandidate> NeedingForce(List<UpdateCandidate> selected)
        {
            var list = new List<UpdateCandidate>();
            if (selected == null) return list;
            foreach (var c in selected)
            {
                var plan = c != null ? c.ProviderTag as CatalogPlan : null;
                // 2.0.26: sólo los que ya se sabe que no se aplican de otra forma; los del fabricante del equipo se comprueban solos al instalar.
                if (plan != null && plan.Entry != null && plan.ForceOnly && !plan.Force && CanInstall(c)) list.Add(c);
            }
            return list;
        }

        /// <summary>El usuario aceptó instalarlos forzados.</summary>
        public static void AllowForce(List<UpdateCandidate> items)
        {
            if (items == null) return;
            foreach (var c in items)
            {
                var plan = c != null ? c.ProviderTag as CatalogPlan : null;
                if (plan != null) plan.Force = true;
            }
        }

        /// <summary>
        /// Programas seleccionados que están abiertos ahora (con ventana o en segundo plano), con los
        /// procesos de cada uno. La interfaz pregunta si cerrarlos antes de instalar: Epic Games
        /// Launcher abierto sin ventana hacía fallar su MSI con 1603. Office se incluye (su
        /// actualizador no aplica nada con Skype Empresarial abierto en segundo plano); los navegadores no:
        /// sus actualizadores manejan solos los archivos en uso.
        /// </summary>
        public static List<KeyValuePair<UpdateCandidate, List<RunningProcess>>> FindRunning(UpdateContext ctx, List<UpdateCandidate> selected)
        {
            var result = new List<KeyValuePair<UpdateCandidate, List<RunningProcess>>>();
            if (selected == null || selected.Count == 0) return result;
            InventoryLoader.EnsurePrograms(ctx);
            List<RunningProcess> all = null;
            foreach (var c in selected)
            {
                if (c == null || c.Kind != UpdateKind.Programa) continue;
                List<RunningProcess> open = null;
                if (c.ProviderName == WingetProvider.ProviderId)
                {
                    var w = c.ProviderTag as WingetRow;
                    if (w == null) continue;
                    string folder = RunningPrograms.FolderOf(WingetProvider.FindProgram(ctx.Programs, w.Name, w.Version));
                    if (folder.Length == 0) continue;
                    if (all == null) all = RunningPrograms.Snapshot();
                    open = RunningPrograms.Closable(RunningPrograms.In(all, folder));
                }
                else if (c.ProviderName == VendorProvider.ProviderId)
                {
                    var plan = c.ProviderTag as VendorPlan;
                    if (plan == null || !VendorProvider.ClosesProcesses(plan)) continue;
                    if (all == null) all = RunningPrograms.Snapshot();
                    // 2.0.19: TLauncher también por la carpeta de su arranque (su Java no se llama «TLauncher»).
                    // 2.0.41: Tor Browser por su carpeta (su firefox.exe no es el Firefox del equipo).
                    open = RunningPrograms.Closable(plan.Check != null && plan.Check.Install == VendorInstall.Starter4j ? VendorProvider.StarterRunning(all, plan)
                                                  : plan.Check != null && plan.Check.Install == VendorInstall.TorBrowser ? VendorProvider.TorRunning(all, plan)
                                                  : RunningPrograms.ByName(all, plan.Processes));
                }
                else if (c.ProviderName == SiteProvider.ProviderId && c.ProviderTag is SitePlan && ((SitePlan)c.ProviderTag).Program != null)
                {
                    // 2.0.18: también se intenta instalar (se busca su instalador al tocar «Actualizar»): si está abierto, que se pueda cerrar.
                    string folder = RunningPrograms.FolderOf(((SitePlan)c.ProviderTag).Program);
                    if (folder.Length == 0) continue;
                    if (all == null) all = RunningPrograms.Snapshot();
                    open = RunningPrograms.Closable(RunningPrograms.In(all, folder));
                }
                if (open != null && open.Count > 0) result.Add(new KeyValuePair<UpdateCandidate, List<RunningProcess>>(c, open));
            }
            return result;
        }

        /// <summary>
        /// Programas seleccionados que están instalados para todo el equipo y se actualizan con winget o
        /// con el instalador del fabricante. Sin administrador, winget ejecuta el instalador sin elevar:
        /// un MSI en silencio (LibreOffice) no puede pedir permiso y termina con 1603/1925, y los demás
        /// muestran el aviso de Windows uno por uno. La interfaz ofrece reiniciar como administrador.
        /// </summary>
        public static List<UpdateCandidate> ProgramsNeedingAdmin(UpdateContext ctx, List<UpdateCandidate> selected)
        {
            var list = new List<UpdateCandidate>();
            if (ctx == null || ctx.IsAdmin || selected == null) return list;
            InventoryLoader.EnsurePrograms(ctx);
            foreach (var c in selected)
            {
                if (c == null || c.Kind != UpdateKind.Programa || !CanInstall(c)) continue;
                if (c.ProviderName == WingetProvider.ProviderId)
                {
                    var w = c.ProviderTag as WingetRow;
                    if (w == null || w.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)) continue;
                    var p = WingetProvider.FindProgram(ctx.Programs, w.Name, w.Version);
                    if (p != null && !(p.Scope ?? "").StartsWith("Usuario", StringComparison.OrdinalIgnoreCase)) list.Add(c);
                }
                else if (c.ProviderName == VendorProvider.ProviderId)
                {
                    var plan = c.ProviderTag as VendorPlan;
                    if (plan != null && plan.Machine) list.Add(c);
                }
            }
            return list;
        }

        static void SafeNotify(Action<UpdateCandidate> changed, UpdateCandidate c)
        {
            if (changed == null) return;
            try { changed(c); } catch (Exception ex) { Log.Warn("Notificación de progreso: " + ex.Message); }
        }

        /// <summary>Cuánto se espera a que Windows cree el punto de restauración.</summary>
        public static TimeSpan RestorePointLimit = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Punto de restauración (Windows limita a uno cada 24 h: si ya hay uno reciente, lo omite). Con plazo:
        /// con las instantáneas de volumen (VSS) trabadas, la llamada no volvía y la instalación no empezaba nunca.
        /// Si no termina a tiempo, se sigue: Windows lo termina de crear por su cuenta.
        /// </summary>
        public static bool CreateRestorePoint(UpdateContext ctx, string description)
        {
            if (!ctx.IsAdmin || ctx.Os.IsServer) { ctx.Report("Punto de restauración omitido (requiere administrador y Windows de escritorio)."); return false; }
            ctx.Report("Creando punto de restauración…");
            int code = int.MinValue;
            Exception error = null;
            var worker = new Thread(delegate()
            {
                try
                {
                    var args = new Dictionary<string, object>();
                    args["Description"] = description;
                    args["RestorePointType"] = 0;        // APPLICATION_INSTALL
                    args["EventType"] = 100;             // BEGIN_SYSTEM_CHANGE
                    code = SystemCollectors.ToInt(Wmi.CallStatic(@"root\default", "SystemRestore", "CreateRestorePoint", args));
                }
                catch (Exception ex) { error = ex; }
            });
            worker.IsBackground = true;
            worker.Name = "punto-restauracion";
            worker.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!worker.Join(500))
            {
                if (sw.Elapsed > RestorePointLimit || ctx.CancelRequested)
                {
                    ctx.Report(ctx.CancelRequested ? "Se dejó de esperar el punto de restauración (cancelado)."
                                                   : "El punto de restauración tarda más de " + Fmt.Duration(RestorePointLimit) + "; se sigue sin esperarlo (Windows lo termina por su cuenta).");
                    return false;
                }
            }
            if (error != null) { ctx.Report("No se pudo crear el punto de restauración: " + Wmi.Describe(error)); return false; }
            if (code == 0) { ctx.Report("Punto de restauración creado (o ya existía uno de las últimas 24 h)."); return true; }
            ctx.Report("No se pudo crear el punto de restauración (código " + code + "; ¿está activada la protección del sistema?).");
            return false;
        }
    }
}
