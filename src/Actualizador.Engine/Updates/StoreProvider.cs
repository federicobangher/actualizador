using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Globalization;
using System.Threading;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    /// <summary>
    /// Apps de Microsoft Store. La Store no publica una API de versiones disponibles para
    /// escritorio, así que:
    ///  - las apps que winget reconoce en el origen «msstore» ya vienen comparadas por WingetProvider;
    ///  - el resto se lista con su versión instalada y se ofrece una acción que le pide a la
    ///    propia Store que busque e instale todo (UpdateScanMethod del puente MDM), midiendo
    ///    después qué versiones cambiaron.
    /// </summary>
    public sealed class StoreProvider : IUpdateProvider
    {
        public const string ProviderId = "store";
        public const string AllAppsId = "__store_todas__";

        /// <summary>
        /// La Store no expone una API de versiones disponibles, así que la única forma de saber si
        /// hay algo pendiente es pedirle que busque. Para no proponer esa acción una y otra vez,
        /// se recuerda cuándo se verificó por última vez y si encontró algo.
        /// </summary>
        static string StateFile { get { return Path.Combine(AppInfo.MachineDataDirectory, "store.json"); } }   // de este equipo

        public static void ReadLastCheck(out DateTime? when, out int updated)
        {
            when = null; updated = 0;
            try
            {
                if (!File.Exists(StateFile)) return;
                object o = Json.Parse(File.ReadAllText(StateFile, Encoding.UTF8));
                DateTime d;
                if (DateTime.TryParse(Json.Str(o, "fecha"), CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) when = d;
                int n;
                if (int.TryParse(Json.Str(o, "actualizadas"), out n)) updated = n;
            }
            catch (Exception ex) { Log.Warn("Estado de la Store: " + ex.Message); }
        }

        /// <summary>Sólo para las pruebas: en vez de pedirle a la Store que busque (true = aceptó) y de anotar el resultado.</summary>
        internal static Func<bool> TriggerForTests;
        internal static Action<int> LastCheckForTests;

        static void WriteLastCheck(int updated)
        {
            if (LastCheckForTests != null) { LastCheckForTests(updated); return; }
            try
            {
                var d = new Dictionary<string, object>();
                d["fecha"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
                d["actualizadas"] = updated;
                if (!SafePaths.EnsureDataDirectory(AppInfo.MachineDataDirectory)) return;   // enlace en el camino: no se escribe
                AtomicFile.WriteAllText(StateFile, Json.Serialize(d, true), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("No se pudo guardar el estado de la Store: " + ex.Message); }
        }

        /// <summary>
        /// ¿Conviene proponer la verificación marcada de entrada? Sólo si nunca se verificó,
        /// si la última vez encontró actualizaciones, o si pasaron más de 12 horas.
        /// </summary>
        public static bool ShouldPreselect(DateTime? lastCheck, int lastUpdated, DateTime now)
        {
            if (!lastCheck.HasValue) return true;
            if (lastUpdated > 0) return true;
            return (now - lastCheck.Value).TotalHours >= 12 || lastCheck.Value > now;
        }

        readonly List<UpdateCandidate> perApp = new List<UpdateCandidate>();

        /// <summary>Apps que la Store actualizó en la última pasada de «Todas las apps».</summary>
        public readonly List<StoreChange> LastChanges = new List<StoreChange>();

        public string Name { get { return ProviderId; } }

        public string Unavailable(UpdateContext ctx)
        {
            if (ctx.Os == null || !ctx.Os.IsWindows8OrLater) return "Microsoft Store existe a partir de Windows 8.";
            if (ctx.Os.IsServer) return "Windows Server no incluye Microsoft Store.";
            return null;
        }

        public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes)
        {
            perApp.Clear();
            var apps = InventoryLoader.EnsureAppx(ctx);
            if (apps.Count == 0)
            {
                notes.Add("No se pudieron leer las apps de la Store" + (ctx.AppxError != null ? ": " + ctx.AppxError : "."));
                return;
            }
            int count = 0;
            foreach (var a in apps)
            {
                if (a.IsFramework) continue;
                string kind = (a.SignatureKind ?? "").ToLowerInvariant();
                if (kind == "system" || kind == "developer" || kind == "none") continue;   // se actualizan con Windows o no son de la Store
                var c = new UpdateCandidate();
                c.Kind = UpdateKind.Store;
                c.Name = a.DisplayName;
                c.Publisher = SoftwareCollectors.PublisherName(a.Publisher);
                c.InstalledVersion = a.Version;
                c.Source = "Microsoft Store";
                c.Identifier = a.FamilyName;
                c.Status = UpdateStatus.NoVerificable;
                c.Detail = "La Store no informa la versión disponible sin buscarla; usá «Todas las apps de Microsoft Store».";
                c.Installable = false;
                c.ProviderName = ProviderId;
                c.ProviderTag = a;
                output.Add(c);
                perApp.Add(c);
                count++;
            }

            if (!StoreAppPresent(apps, StoreFolderExists()))
            {
                // Windows 10/11 LTSC (y equipos a los que les quitaron la Store): no hay a quién pedirle que actualice.
                foreach (var c in perApp)
                    c.Detail = "Este Windows no tiene Microsoft Store (ediciones LTSC o se quitó): la app se actualiza con winget, si la conoce, o reinstalándola.";
                notes.Add("Este Windows no tiene Microsoft Store (ediciones LTSC o se quitó): no se ofrece «Todas las apps de Microsoft Store».");
                return;
            }

            var all = new UpdateCandidate();
            all.Kind = UpdateKind.Store;
            all.Name = "Todas las apps de Microsoft Store (la Store busca e instala)";
            all.Publisher = "Microsoft";
            all.InstalledVersion = Fmt.Plural(count, "app", "apps");
            all.Source = "Microsoft Store";
            all.Identifier = AllAppsId;
            all.Installable = true;
            all.ProviderName = ProviderId;
            DateTime? lastCheck;
            int lastUpdated;
            ReadLastCheck(out lastCheck, out lastUpdated);
            all.Recommended = ShouldPreselect(lastCheck, lastUpdated, DateTime.Now);
            string how = ctx.IsAdmin && ctx.Os.IsWindows10OrLater
                ? "Le pide a Microsoft Store que busque e instale las actualizaciones y luego compara las versiones."
                : "Abre la página «Descargas y actualizaciones» de la Store (para hacerlo automático, ejecutá como administrador).";
            // Si ya se verificó hace poco y no había nada, el estado honesto es «al día»,
            // no «pendiente»: igual se puede marcar para que la Store vuelva a buscar.
            all.Status = all.Recommended ? UpdateStatus.Pendiente : UpdateStatus.AlDia;
            all.AvailableVersion = all.Recommended ? "Lo que publique la Store" : "Sin novedades";
            all.Detail = all.Recommended ? how
                : "Verificada el " + Fmt.Date(lastCheck.Value) + ": la Store no tenía nada pendiente. Marcala si querés que busque de nuevo. " + how;
            // si la Store las revisó hace poco y no tenía nada, cada app está al día (antes quedaban todas «sin verificar»
            // y la cuenta de «Sin verificar» parecía enorme). Si no, se revisan con la fila de arriba.
            foreach (var c in perApp)
            {
                if (all.Recommended)
                    c.Detail = "La Store no informa la versión disponible de cada app: se revisan todas juntas con «Todas las apps de Microsoft Store» (en «Para actualizar»).";
                else
                {
                    c.Status = UpdateStatus.AlDia;
                    c.AvailableVersion = c.InstalledVersion;
                    c.Detail = "Microsoft Store revisó sus apps el " + Fmt.Date(lastCheck.Value) + " y no tenía actualizaciones.";
                }
            }
            output.Add(all);
        }

        /// <summary>
        /// ¿Está la app Microsoft Store? Por el inventario de apps del usuario o, si no figura ahí (otro usuario
        /// elevado), por su carpeta en WindowsApps. <paramref name="folder"/>: true existe, false no, null no se sabe.
        /// Ante la duda, se supone que está (es lo normal en Windows 10 y 11).
        /// </summary>
        public static bool StoreAppPresent(List<AppxInfo> apps, bool? folder)
        {
            if (apps != null)
                foreach (var a in apps)
                    if (a != null && "Microsoft.WindowsStore".Equals(a.Name, StringComparison.OrdinalIgnoreCase)) return true;
            return folder != false;
        }

        static bool? StoreFolderExists()
        {
            try
            {
                string apps = Path.Combine(SafePaths.ProgramFiles, "WindowsApps");
                if (!Directory.Exists(apps)) return null;
                return Directory.GetDirectories(apps, "Microsoft.WindowsStore_*").Length > 0;
            }
            catch { return null; }   // sin permiso para listar WindowsApps (usuario común): no se sabe
        }

        public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed)
        {
            foreach (var c in items)
            {
                if (c.Identifier != AllAppsId) { c.Status = UpdateStatus.Fallida; c.Result = "Usá «Todas las apps de Microsoft Store»"; changed(c); continue; }
                // Después de «Detener» no se le pide nada a la Store (antes se la disparaba igual).
                if (ctx.CancelRequested) { c.Status = UpdateStatus.Pendiente; c.Result = InstallTally.CancelledBeforeStart; changed(c); continue; }
                c.Status = UpdateStatus.Instalando;
                c.Result = "Solicitando a Microsoft Store…";
                changed(c);
                UpdateAll(ctx, c, changed);
                changed(c);
            }
        }

        void UpdateAll(UpdateContext ctx, UpdateCandidate c, Action<UpdateCandidate> changed)
        {
            string err;
            LastChanges.Clear();
            var before = Snapshot(out err);
            bool triggered = false;
            if (TriggerForTests != null) triggered = TriggerForTests();
            else if (ctx.IsAdmin && ctx.Os.IsWindows10OrLater)
            {
                ctx.Report("Microsoft Store: se le pide que busque e instale las actualizaciones de sus apps…");
                const string script =
                    "$r = Get-CimInstance -Namespace 'root\\cimv2\\mdm\\dmmap' -ClassName 'MDM_EnterpriseModernAppManagement_AppManagement01' -ErrorAction Stop | " +
                    "Invoke-CimMethod -MethodName UpdateScanMethod -ErrorAction Stop; \"RC=$($r.ReturnValue)\"";
                var r = ProcessRunner.PowerShell(script, 120000);
                triggered = r.Ok && r.Out.Contains("RC=0");
                if (!triggered) Log.Warn("UpdateScanMethod: " + r.Describe() + " " + Fmt.Truncate(r.Out + r.Err, 300));
            }
            if (!triggered)
            {
                // Sólo se abrió la página: no se sabe si había algo, así que no se guarda como verificada.
                ctx.Report("Microsoft Store: se abre «Descargas y actualizaciones» para que la actualización la confirmes vos.");
                OpenStoreUpdates(ctx);
                c.Status = UpdateStatus.Pendiente;
                c.Result = "Se abrió «Descargas y actualizaciones» de la Store: tocá «Obtener actualizaciones».";
                return;
            }

            if (before == null)
            {
                // Sin la foto de antes no se puede medir qué cambió: decir «al día» sería inventarlo.
                Log.Warn("Microsoft Store: no se pudieron leer las versiones antes de actualizar: " + err);
                c.Status = UpdateStatus.Pendiente;
                c.Result = "Se le pidió a la Store que actualice, pero no se pudieron leer las versiones para medir el resultado. Volvé a buscar actualizaciones en unos minutos.";
                return;
            }

            // La Store descarga en segundo plano: se mide qué cambió durante unos minutos.
            c.Result = "La Store está buscando e instalando…";
            changed(c);
            ctx.Report("Microsoft Store: buscando e instalando en segundo plano; se controla qué versiones cambian durante unos minutos…");
            var updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            DateTime started = DateTime.Now;
            DateTime limit = started + MeasureWindow;
            DateTime lastChange = started;
            // 2.0.17: antes se esperaban siempre los 4 minutos cuando la Store no tenía nada para instalar (lo más común). Ahora se le
            // pregunta a la propia Store qué tiene en su cola de descargas e instalaciones: con la cola vacía dos veces seguidas y
            // sin versiones que cambien, terminó (o no había nada) y no se espera más.
            DateTime nextProbe = started + FirstQueueProbe;
            int emptyProbes = 0, failedProbes = 0;
            bool probing = true, sawQueue = false;
            StoreQueue queue = null;
            while (DateTime.Now < limit && !ctx.CancelRequested)
            {
                for (int s = 0; s < 5 && !ctx.CancelRequested; s++) Thread.Sleep(PollPauseMs);
                if (ctx.CancelRequested) break;
                // Cada vuelta se lee el registro de paquetes del usuario (milisegundos). Antes cada vuelta abría
                // PowerShell con Get-AppxPackage: 16 veces en 4 minutos, con su consumo de CPU y memoria.
                var now = QuickSnapshot() ?? Snapshot(out err);
                if (now == null) continue;
                int changesThisRound = 0;
                foreach (var kv in now)
                {
                    string old;
                    if (before.TryGetValue(kv.Key, out old) && VersionUtil.IsNewer(kv.Value, old) && !updated.ContainsKey(kv.Key))
                    {
                        updated[kv.Key] = old + " → " + kv.Value;
                        changesThisRound++;
                    }
                }
                if (changesThisRound > 0)
                {
                    lastChange = DateTime.Now;
                    emptyProbes = 0;
                    c.Result = Fmt.Plural(updated.Count, "app actualizada", "apps actualizadas") + " hasta ahora…";
                    changed(c);
                    ctx.Report("Microsoft Store: " + c.Result);
                }
                if (probing && DateTime.Now >= nextProbe)
                {
                    queue = ReadQueue();
                    nextProbe = DateTime.Now + QueueProbeEvery;
                    if (queue == null)
                    {
                        // No se pudo preguntar (un Windows sin esa interfaz): se mide por tiempo, como antes.
                        if (++failedProbes >= 2) { probing = false; Log.Info("Microsoft Store: no se pudo leer su cola de instalaciones; se mide por tiempo"); }
                    }
                    else if (queue.Active > 0)
                    {
                        emptyProbes = 0;
                        sawQueue = true;
                        string doing = Fmt.Plural(queue.Active, "app en descarga o instalación", "apps en descarga o instalación");
                        c.Result = (updated.Count > 0 ? Fmt.Plural(updated.Count, "app actualizada", "apps actualizadas") + "; " : "La Store tiene ") + doing + "…";
                        changed(c);
                        ctx.Report("Microsoft Store: " + doing + "…");
                    }
                    else emptyProbes++;
                }
                if (ShouldStopMeasuring(DateTime.Now - started, DateTime.Now - lastChange, updated.Count, probing, emptyProbes, queue != null ? queue.Active : -1)) break;
            }
            if (!ctx.CancelRequested)
                Log.Info("Microsoft Store: se midió " + Fmt.Elapsed(DateTime.Now - started) + (probing ? " (cola " + (sawQueue ? "con actividad" : "vacía") + ")" : " (por tiempo)") +
                         (queue != null && queue.Paused > 0 ? "; " + queue.Paused + " en pausa" : ""));
            if (ctx.CancelRequested && updated.Count == 0)
            {
                // Cancelado mientras se medía: no se sabe si había algo. No se guarda como «verificada sin
                // novedades» (antes la próxima búsqueda la dejaba sin marcar durante 12 horas).
                c.Status = UpdateStatus.Pendiente;
                c.Result = "Cancelada mientras se medía: la Store puede seguir instalando en segundo plano. Volvé a buscar actualizaciones más tarde.";
                ctx.Report("Microsoft Store: medición cancelada.");
                return;
            }
            // El veredicto final, con Get-AppxPackage (lo que Windows tiene registrado de verdad): confirma lo que se vio
            // en el registro y agrega lo que haya cambiado sin que el registro lo mostrara todavía.
            string ferr;
            var final = Snapshot(out ferr);
            if (final != null) updated = Changes(before, final);
            else Log.Warn("Microsoft Store: no se pudo confirmar con Get-AppxPackage (" + ferr + "); se usa lo medido en el registro.");
            WriteLastCheck(updated.Count);
            LastChanges.Clear();
            foreach (var kv in updated) LastChanges.Add(StoreChange.FromSnapshot(kv.Key, kv.Value, DisplayNameOf(kv.Key)));
            if (updated.Count == 0)
            {
                // No instaló nada: decirlo como «al día», no como «actualizada».
                // Sigue instalable: «Todas las apps» se puede pedir de nuevo siempre (CanInstall lo permite al día).
                c.Status = UpdateStatus.AlDia;
                c.Result = "La Store no instaló nada: todo estaba al día, o las descargas siguen en segundo plano.";
                ctx.Report("Microsoft Store: no instaló nada.");
            }
            else
            {
                c.Status = UpdateStatus.Instalada;
                c.Result = Fmt.Plural(updated.Count, "app actualizada", "apps actualizadas") + ": " + string.Join("; ", Describe(updated)) + ". Otras descargas pueden seguir en segundo plano.";
                ctx.Report("Microsoft Store: " + Fmt.Plural(updated.Count, "app actualizada", "apps actualizadas") + " (" + string.Join("; ", Describe(updated)) + ").");
            }
            foreach (var app in perApp)
            {
                var info = app.ProviderTag as AppxInfo;
                string change;
                if (info == null || !updated.TryGetValue(info.Name, out change)) continue;
                int arrow = change.IndexOf(" → ");
                app.InstalledVersion = arrow >= 0 ? change.Substring(arrow + 3) : app.InstalledVersion;
                app.AvailableVersion = app.InstalledVersion;
                app.Status = UpdateStatus.Instalada;
                app.Result = "Actualizada por Microsoft Store (" + change + ")";
                changed(app);
            }
        }

        string DisplayNameOf(string package)
        {
            foreach (var app in perApp)
            {
                var info = app.ProviderTag as AppxInfo;
                if (info != null && string.Equals(info.Name, package, StringComparison.OrdinalIgnoreCase)) return app.Name;
            }
            return null;
        }

        /// <summary>
        /// Filas de winget de apps que la Store acaba de actualizar (winget falló —p. ej. porque se estaba
        /// actualizando App Installer— o no se eligieron): pasan a «Actualizada por Microsoft Store» si la
        /// versión nueva alcanza la que ofrecía winget. Se reconoce por el Id de winget igual al nombre del
        /// paquete (Microsoft.WindowsTerminal) o por el mismo nombre visible. Devuelve cuántas cambió.
        /// </summary>
        public static int Reconcile(IEnumerable<UpdateCandidate> rows, List<StoreChange> changes, Action<UpdateCandidate> changed)
        {
            if (rows == null || changes == null || changes.Count == 0) return 0;
            int n = 0;
            foreach (var c in rows)
            {
                if (c == null || c.ProviderName != WingetProvider.ProviderId) continue;
                if (c.Status != UpdateStatus.Fallida && c.Status != UpdateStatus.Disponible && c.Status != UpdateStatus.Pendiente) continue;
                var ch = FindChange(c, changes);
                if (ch == null) continue;
                // Sólo si alcanza lo ofrecido (las dos numeraciones son la del paquete, pero sin evidencia no se cambia).
                if (VersionUtil.Parse(c.AvailableVersion) != null && VersionUtil.IsNewer(c.AvailableVersion, ch.After)) continue;
                c.Status = UpdateStatus.Instalada;
                c.InstalledVersion = ch.After;
                c.Result = "Actualizada por Microsoft Store (" + ch.Before + " → " + ch.After + ")";
                n++;
                if (changed != null) changed(c);
            }
            return n;
        }

        static StoreChange FindChange(UpdateCandidate c, List<StoreChange> changes)
        {
            var w = c.ProviderTag as WingetRow;
            string name = (c.Name ?? "").Trim();
            foreach (var ch in changes)
            {
                if (w != null && string.Equals(w.Id, ch.Package, StringComparison.OrdinalIgnoreCase)) return ch;
                if (name.Length > 0 && !string.IsNullOrEmpty(ch.DisplayName) && string.Equals(name, ch.DisplayName.Trim(), StringComparison.CurrentCultureIgnoreCase)) return ch;
            }
            return null;
        }

        static string[] Describe(Dictionary<string, string> updated)
        {
            var l = new List<string>();
            foreach (var kv in updated) l.Add(kv.Key + " " + kv.Value);
            return l.ToArray();
        }

        /// <summary>Lo que la Store tiene en su cola de descargas e instalaciones.</summary>
        public sealed class StoreQueue
        {
            /// <summary>Apps que está descargando o instalando (o esperando su turno).</summary>
            public int Active;
            /// <summary>Apps en pausa (sin Wi-Fi, con poca batería o pausadas por el usuario): no van a terminar solas ahora.</summary>
            public int Paused;
        }

        /// <summary>Sólo para las pruebas: reemplaza la consulta a la cola de la Store (null = no se pudo consultar).</summary>
        internal static Func<StoreQueue> QueueForTests;

        /// <summary>
        /// La cola de la Store, leída de la misma interfaz que usa la app Microsoft Store (AppInstallManager.AppInstallItems). Sólo
        /// lectura: no inicia, pausa ni cancela nada. null si no se pudo (se sigue midiendo por tiempo).
        /// </summary>
        public const string QueueScript =
            "try { $null = [Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallManager,Windows.ApplicationModel.Store.Preview,ContentType=WindowsRuntime]; " +
            "$m = New-Object Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallManager; $a = 0; $p = 0; " +
            "foreach ($i in $m.AppInstallItems) { $s = [string]$i.GetCurrentStatus().InstallState; " +
            "if ($s -eq 'Completed' -or $s -eq 'Canceled' -or $s -eq 'Error') { continue }; if ($s -like 'Paused*') { $p++ } else { $a++ } }; " +
            "'COLA=' + $a + ' PAUSA=' + $p } catch { 'COLA=? ' + $_.Exception.Message }";

        static StoreQueue ReadQueue()
        {
            if (QueueForTests != null) return QueueForTests();
            try
            {
                var r = ProcessRunner.PowerShell(QueueScript, 30000);
                var q = ParseQueue(r.Out);
                if (q == null) Log.Quiet("Cola de la Store", new InvalidOperationException(Fmt.Truncate((r.Out + " " + r.Err).Trim(), 200)));
                return q;
            }
            catch (Exception ex) { Log.Quiet("Cola de la Store", ex); return null; }
        }

        static readonly System.Text.RegularExpressions.Regex QueueLine = new System.Text.RegularExpressions.Regex(@"COLA=(?<a>\d{1,6}) PAUSA=(?<p>\d{1,6})", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>«COLA=2 PAUSA=0» → la cola; null con cualquier otra cosa («COLA=? …»: no se pudo leer).</summary>
        public static StoreQueue ParseQueue(string output)
        {
            var m = QueueLine.Match(output ?? "");
            if (!m.Success) return null;
            var q = new StoreQueue();
            q.Active = int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture);
            q.Paused = int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture);
            return q;
        }

        /// <summary>
        /// ¿Se deja de medir? (pública para las pruebas)
        /// - Con la cola de la Store a la vista: cuando estuvo vacía dos consultas seguidas y hace <see cref="IdleQuiet"/> que no
        ///   cambia ninguna versión: terminó, o no había nada. Mientras tenga algo en curso, se sigue (hasta el tope).
        /// - Sin la cola (no se pudo consultar): con algo ya actualizado, después de <see cref="QuietAfter"/> sin cambios nuevos;
        ///   sin nada actualizado, después de <see cref="BlindWindow"/>.
        /// </summary>
        public static bool ShouldStopMeasuring(TimeSpan elapsed, TimeSpan sinceLastChange, int updatedCount, bool probing, int emptyProbes, int activeInQueue)
        {
            if (probing)
            {
                if (activeInQueue > 0) return false;
                return emptyProbes >= 2 && sinceLastChange >= IdleQuiet;
            }
            if (updatedCount > 0) return sinceLastChange > QuietAfter;
            return elapsed >= BlindWindow;
        }

        /// <summary>Pausa entre segundos de espera de la medición (las pruebas la acortan).</summary>
        internal static int PollPauseMs = 1000;
        /// <summary>Cuándo se le pregunta a la Store por primera vez qué tiene en cola (le lleva unos segundos buscar) y cada cuánto después.</summary>
        internal static TimeSpan FirstQueueProbe = TimeSpan.FromSeconds(20), QueueProbeEvery = TimeSpan.FromSeconds(15);
        /// <summary>Con la cola vacía, cuánto tiene que pasar sin que cambie una versión para dar por terminada la medición.</summary>
        internal static TimeSpan IdleQuiet = TimeSpan.FromSeconds(15);
        /// <summary>Sin poder consultar la cola y sin nada actualizado: cuánto se espera (antes, siempre los 4 minutos).</summary>
        internal static TimeSpan BlindWindow = TimeSpan.FromMinutes(2);
        /// <summary>Cuánto se mide qué cambia después de pedirle a la Store que actualice.</summary>
        internal static TimeSpan MeasureWindow = TimeSpan.FromMinutes(4);
        /// <summary>Sin cambios nuevos durante este tiempo (y con alguno ya visto), se deja de medir.</summary>
        internal static TimeSpan QuietAfter = TimeSpan.FromSeconds(60);

        /// <summary>Sólo para las pruebas: reemplaza las dos lecturas de versiones (rápida y con PowerShell).</summary>
        internal static Func<Dictionary<string, string>> QuickSnapshotForTests, SnapshotForTests;

        /// <summary>
        /// Paquetes que cambiaron de versión entre dos lecturas: nombre → «vieja → nueva». Sólo los que ya estaban
        /// (una app nueva no es una actualización).
        /// </summary>
        public static Dictionary<string, string> Changes(Dictionary<string, string> before, Dictionary<string, string> now)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (before == null || now == null) return result;
            foreach (var kv in now)
            {
                string old;
                if (before.TryGetValue(kv.Key, out old) && VersionUtil.IsNewer(kv.Value, old)) result[kv.Key] = old + " → " + kv.Value;
            }
            return result;
        }

        /// <summary>Versiones (nombre → la más nueva) leídas del registro de paquetes del usuario; null si no se pudo.</summary>
        static Dictionary<string, string> QuickSnapshot()
        {
            if (QuickSnapshotForTests != null) return QuickSnapshotForTests();
            try
            {
                var map = InventoryLoader.LoadAppxVersionsRegistry();
                return map.Count > 0 ? map : null;
            }
            catch (Exception ex) { Log.Quiet("Versiones de la Store en el registro", ex); return null; }
        }

        static Dictionary<string, string> Snapshot(out string error)
        {
            if (SnapshotForTests != null) { error = null; return SnapshotForTests(); }
            var list = InventoryLoader.LoadAppxPowerShell(out error);
            if (list == null) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in list) if (!a.IsFramework) map[a.Name] = a.Version;
            return map;
        }

        static void OpenStoreUpdates(UpdateContext ctx)
        {
            string uri = ctx.Os.IsWindows10OrLater ? "ms-windows-store://downloadsandupdates" : "ms-windows-store:Updates";
            try { ProcessRunner.OpenForUser(uri); }
            catch (Exception ex) { Log.Warn("No se pudo abrir la Store: " + ex.Message); }
        }
    }

    /// <summary>Una app que la Store actualizó: paquete, nombre visible (si se conoce) y versiones.</summary>
    public sealed class StoreChange
    {
        public string Package;
        public string DisplayName;
        public string Before;
        public string After;

        /// <summary>Desde la entrada «anterior → nueva» que arma la medición de versiones.</summary>
        public static StoreChange FromSnapshot(string package, string change, string displayName)
        {
            var s = new StoreChange();
            s.Package = package;
            s.DisplayName = displayName;
            string text = change ?? "";
            int arrow = text.IndexOf(" → ");
            s.Before = arrow >= 0 ? text.Substring(0, arrow) : "";
            s.After = arrow >= 0 ? text.Substring(arrow + 3) : text;
            return s;
        }
    }
}
