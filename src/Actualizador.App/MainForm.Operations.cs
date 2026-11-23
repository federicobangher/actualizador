// Ventana principal: operaciones en segundo plano (buscar e instalar actualizaciones).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.App
{
    sealed partial class MainForm
    {
        // ================================================================== Operaciones en segundo plano
        UpdateContext NewContext()
        {
            var ctx = UpdateContext.Create(settings.ToOptions());
            // Sólo los mensajes de la operación en curso: un hilo de una búsqueda cancelada que sigue
            // terminando (Windows Update no se puede interrumpir) no pisa la barra de estado.
            ctx.Progress += delegate(string m) { if (busyCtx == ctx) pendingStatus = m; };
            return ctx;
        }

        UpdateContext UsableContext()
        {
            if (lastCtx == null || lastCtx.CancelRequested) lastCtx = NewContext();
            return lastCtx;
        }

        void SetBusy(bool value, UpdateContext ctx, string message)
        {
            busy = value;
            busyCtx = value ? ctx : null;
            // 2.0.21: sin nada en curso, la ventana se refresca una vez por segundo (antes cuatro, siempre).
            if (uiTimer != null) uiTimer.Interval = value ? 250 : 1000;
            // El último mensaje de progreso que quedó sin mostrar no pisa el resultado final.
            if (!value) pendingStatus = null;
            progress.Visible = value;
            if (value)
            {
                progress.Style = ProgressBarStyle.Marquee;
                progress.MarqueeAnimationSpeed = 30;
            }
            UseWaitCursor = false;
            if (message != null) lblStatus.Text = message;
            UpdateSummary();
        }

        void RunBackground(string name, ThreadStart work, Action<Exception> done)
        {
            var t = new Thread(delegate()
            {
                Exception error = null;
                try { work(); }
                catch (Exception ex) { error = ex; }
                UiInvoke(delegate { done(error); });
            });
            t.IsBackground = true;
            t.Name = name;
            t.Start();
        }

        void UiInvoke(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(a);
            }
            catch (InvalidOperationException) { }   // incluye ObjectDisposedException: la ventana ya se cerró
        }

        void StopCurrent()
        {
            if (busyCtx == null) return;
            busyCtx.RequestCancel();
            lblStatus.Text = installing ? "Se cancelarán las actualizaciones que todavía no empezaron…" : "Cancelando…";
            UpdateSummary();   // el botón queda deshabilitado hasta que termine
        }

        void StartUpdateScan()
        {
            if (busy) return;
            if (demo) { ShowUpdates(); return; }
            var ctx = UsableContext();
            ctx.Options.QueryVendors = settings.QueryVendors;   // si se cambió en Opciones después de la última búsqueda
            ctx.Options.InstallWinget = settings.InstallWinget;
            ctx.Options.SourcesCatalogUrl = settings.SourcesCatalogUrl ?? "";
            ctx.Options.IncludePreReleases = settings.OfferBetas;
            ctx.Options.OwnBuildsFolder = settings.OwnBuildsFolder ?? "";
            // Cada búsqueda vuelve a leer lo instalado: el usuario pudo instalar o desinstalar algo desde la anterior.
            InventoryLoader.Invalidate(ctx);
            groupChosen = false;       // al terminar se muestra lo que haya para hacer
            installSummary = null;
            if (report == null) report = UpdateSession.Create(ctx);
            scanning = true;
            notesOpen = false;
            lblEmptySub.Text = "Preparando…";
            SetBusy(true, ctx, "Buscando actualizaciones…");
            ShowUpdates();
            var target = report;
            RunBackground("actualizaciones", delegate { updateManager.Scan(ctx, target); }, delegate(Exception err)
            {
                scanning = false;
                SetBusy(false, null, null);
                if (err is OperationCanceledByUserException) lblStatus.Text = "Búsqueda de actualizaciones cancelada.";
                else if (err != null) Program.ShowError(err);
                ShowUpdates();
                // la barra de estado quedaba con el último progreso («Páginas oficiales: 20 de 29 leídas…»).
                if (err == null && target != null && target.UpdatesScannedLocal.HasValue)
                {
                    var k = UpdateGroups.Count(SnapshotUpdates());
                    lblStatus.Text = "Búsqueda terminada" + (target.UpdatesScanDuration.HasValue ? " en " + Fmt.Elapsed(target.UpdatesScanDuration.Value) : "") + ": " +
                                     Plural(k.ToUpdate + k.Optional, "actualización disponible", "actualizaciones disponibles") + ".";
                }
            });
        }

        void StartInstall()
        {
            if (busy || report == null) return;
            // se instala todo lo marcado, en cualquier grupo; la ventana de confirmación lo lista entero antes de empezar.
            var selected = Marked();
            const int ocultas = 0;
            if (selected.Count == 0)
            {
                lblStatus.Text = "No hay nada marcado para actualizar.";
                return;
            }

            int programs = 0, store = 0, drivers = 0, windows = 0;
            foreach (var u in selected)
            {
                if (u.Kind == UpdateKind.Controlador) drivers++;            // de Windows Update o del Catálogo de Microsoft Update
                else if (u.ProviderName == WindowsUpdateProvider.ProviderId) windows++;
                else if (u.Kind == UpdateKind.Store) store++;
                else programs++;
            }
            if (!isAdmin && !demo && drivers + windows > 0)
            {
                var answer = MessageBox.Show(this,
                    "Los controladores y las actualizaciones de Windows sólo se pueden instalar como administrador.\n\n" +
                    "Sí: reiniciar Actualizador como administrador (vuelve a buscar actualizaciones automáticamente).\n" +
                    "No: instalar ahora sólo los programas y las apps de la Store.\nCancelar: no hacer nada.",
                    AppInfo.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                if (answer == DialogResult.Yes) { Elevate("--actualizaciones"); return; }
                // 2.0.28: lo que se saca, lo dice (antes, si era lo único marcado, no pasaba nada).
                foreach (var u in selected.FindAll(delegate(UpdateCandidate x) { return x.ProviderName == WindowsUpdateProvider.ProviderId || x.Kind == UpdateKind.Controlador; }))
                {
                    u.Result = "No se instaló: hace falta ejecutar Actualizador como administrador";
                    RefreshUpdateRow(u);
                }
                selected.RemoveAll(delegate(UpdateCandidate u) { return u.ProviderName == WindowsUpdateProvider.ProviderId || u.Kind == UpdateKind.Controlador; });
                drivers = windows = 0;
                if (selected.Count == 0) { lblStatus.Text = "No se instaló nada: los controladores y Windows Update necesitan Actualizador como administrador."; return; }
            }
            else if (!isAdmin && !demo)
            {
                // Programas instalados para todo el equipo (LibreOffice, TeraBox…): sin administrador, el MSI en
                // silencio no puede pedir permiso y falla con 1603; los demás piden confirmación uno por uno.
                List<UpdateCandidate> equipo = null;
                try { equipo = UpdateManager.ProgramsNeedingAdmin(UsableContext(), selected); }
                catch (Exception ex) { Log.Warn("Programas del equipo: " + ex.Message); }
                if (equipo != null && equipo.Count > 0)
                {
                    var lista = new StringBuilder();
                    for (int i = 0; i < equipo.Count && i < 10; i++) lista.Append("  • ").Append(Fmt.Truncate(equipo[i].Name, 60)).Append("\n");
                    if (equipo.Count > 10) lista.Append("  … y ").Append(equipo.Count - 10).Append(" más\n");
                    var answer = MessageBox.Show(this,
                        (equipo.Count == 1 ? "Este programa está instalado" : "Estos programas están instalados") + " para todo el equipo:\n\n" + lista +
                        "\nActualizador no se está ejecutando como administrador. Así, los instaladores MSI (como el de LibreOffice) " +
                        "fallan porque en modo silencioso no pueden pedir permiso, y los demás piden confirmación de Windows uno por uno.\n\n" +
                        "Sí: reiniciar Actualizador como administrador (vuelve a buscar actualizaciones automáticamente).\n" +
                        "No: intentar igual.\nCancelar: no hacer nada.",
                        AppInfo.Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (answer == DialogResult.Cancel) return;
                    if (answer == DialogResult.Yes) { Elevate("--actualizaciones"); return; }
                }
            }

            var ctx = UsableContext();
            // Programas abiertos (también en segundo plano: su instalador falla con 1603) y controladores que Windows sólo acepta
            // forzados: todo se decide en UNA ventana de confirmación.
            List<KeyValuePair<UpdateCandidate, List<RunningProcess>>> running = null;
            Cursor previous = Cursor.Current;
            try { Cursor.Current = Cursors.WaitCursor; if (!demo) running = UpdateManager.FindRunning(ctx, selected); }
            catch (Exception ex) { Log.Warn("Programas abiertos: " + ex.Message); }
            finally { Cursor.Current = previous; }
            var forzables = UpdateManager.NeedingForce(selected);
            bool closeRunning, restore, interactive, force;
            using (var dlg = new InstallConfirmForm(this, selected, running, forzables, ocultas, settings.RestorePoint, isAdmin, false, false))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                closeRunning = dlg.CloseRunning;
                restore = dlg.RestorePoint;
                interactive = dlg.Interactive;
                force = dlg.ForceDrivers;
            }
            if (demo) { lblStatus.Text = "Modo demostración: no se instala nada."; return; }
            // La política de la empresa manda sobre lo elegido (el motor también lo controla).
            var policy = Policy.Current;
            if (policy.RequireRestorePoint) restore = true;
            if (!policy.AllowInteractiveInstallers) interactive = false;
            if (!policy.AllowForcedDrivers) force = false;
            if (force) UpdateManager.AllowForce(forzables);
            else if (forzables.Count > 0)
            {
                // 2.0.26: antes se sacaban en silencio y, si era lo único marcado, no pasaba nada (la grabación del usuario).
                selected.RemoveAll(delegate(UpdateCandidate u) { return forzables.Contains(u); });
                foreach (var u in forzables)
                {
                    u.Result = "No se instaló: sólo se aplica forzado y no se marcó «Instalar forzados»";
                    RefreshUpdateRow(u);
                }
                if (selected.Count == 0)
                {
                    lblStatus.Text = "No se instaló nada: " + Fmt.Plural(forzables.Count, "controlador sólo se aplica forzado", "controladores sólo se aplican forzados") +
                                     " y no se marcó «Instalar forzados».";
                    return;
                }
            }
            if (restore != settings.RestorePoint && isAdmin && !policy.RequireRestorePoint) { settings.RestorePoint = restore; settings.Save(); }
            updateManager.CloseRunning = closeRunning;
            updateManager.Interactive = interactive;
            // 2.0.8: sólo lo marcado. Antes, al terminar, volvía a buscar e instalaba solo lo recomendado que apareciera
            // («todo de una vez»): se instalaban cosas que no se habían elegido. La consola lo sigue haciendo («actualizar», salvo --una-vuelta).
            updateManager.RepeatUntilDone = false;
            installing = true;
            installSummary = null;
            SetBusy(true, ctx, "Instalando actualizaciones…");
            ShowUpdates();
            var target = report;
            // Todo lo que se intentó en todas las vueltas, para el resumen final.
            var attemptedAll = new List<UpdateCandidate>(selected);
            int rounds = 1;
            RunBackground("instalacion", delegate
            {
                var done = updateManager.InstallUntilDone(ctx, target, selected, restore,
                    delegate(UpdateCandidate c) { UiInvoke(delegate { RefreshUpdateRow(c); }); },
                    delegate(int n) { rounds = n + 1; UiInvoke(delegate { ShowUpdates(); }); });
                lock (attemptedAll) { attemptedAll.Clear(); attemptedAll.AddRange(done); }
            }, delegate(Exception err)
            {
                installing = false;
                SetBusy(false, null, null);
                if (err != null) Program.ShowError(err);
                List<UpdateCandidate> intentadas;
                lock (attemptedAll) intentadas = new List<UpdateCandidate>(attemptedAll);
                // Lo instalado queda desmarcado: nada se vuelve a instalar sin marcarlo de nuevo.
                foreach (var u in SnapshotUpdates()) u.Selected = false;
                // El resultado queda arriba de la lista, sin otro cuadro que cerrar.
                installSummary = InstallSummaryText(intentadas, rounds, ctx.CancelRequested);
                lblStatus.Text = installSummary.Replace("\n", " ");
                ShowUpdates();
                var tally = InstallTally.Of(intentadas);
                if (tally.Failed > 0) AutoSaveDiagnostic();
                // Sólo se interrumpe si hay que reiniciar: es lo único que el usuario tiene que hacer.
                if (tally.Reboot > 0 || tally.RestartFirst > 0)
                    MessageBox.Show(this, installSummary + "\n\nGuardá tu trabajo y reiniciá el equipo cuando puedas.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        /// <summary>Resultado de una tanda en una o dos líneas, para el encabezado de Actualizaciones.</summary>
        static string InstallSummaryText(List<UpdateCandidate> items, int rounds, bool cancelled)
        {
            var tally = InstallTally.Of(items);
            var sb = new StringBuilder();
            sb.Append(cancelled ? "Instalación cancelada: " : "Listo" + (rounds > 1 ? " (" + rounds + " vueltas)" : "") + ": ").Append(tally.Text().TrimEnd('.')).Append('.');
            if (tally.Reboot > 0) sb.Append(" ").Append(tally.Reboot == 1 ? "Una necesita" : tally.Reboot + " necesitan").Append(" reiniciar el equipo.");
            if (tally.RestartFirst > 0) sb.Append(" ").Append(tally.RestartFirst == 1 ? "Una se instala" : tally.RestartFirst + " se instalan").Append(" después de reiniciar.");
            if (tally.Failed > 0) sb.Append(" El motivo de cada error está en la fila (columna «Resultado» y el recuadro de abajo).");
            if (tally.Pending > 0) sb.Append(" Lo pendiente termina solo o en la página que se abrió: buscá de nuevo en unos minutos para confirmarlo.");
            return sb.ToString();
        }

        void Elevate(string args)
        {
            if (busy) return;
            if (Program.RestartElevated(args)) { closingForElevation = true; Close(); }
        }
    }
}
