using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.App
{
    sealed partial class MainForm : Form
    {
        readonly GuiSettings settings;
        readonly List<string> startArgs;
        readonly bool isAdmin;
        readonly UpdateManager updateManager = new UpdateManager();
        readonly ConcurrentQueue<string> logQueue = new ConcurrentQueue<string>();
        volatile string pendingStatus;

        UpdateSession report;
        UpdateContext lastCtx;
        volatile UpdateContext busyCtx;     // lo leen los hilos de trabajo (mensajes de progreso)
        bool busy, installing, scanning, demo, closingForElevation;
        float scale = 1f;

        // --- Controles (2.0.8: una barra con tres botones; el resto, en «Más»)
        Button btnScan, btnInstall, btnMarkAll, btnMore, btnTheme;
        Button tabPending, tabReviewed;
        ContextMenuStrip moreMenu;
        ToolStripMenuItem miHistory, miLog, miOptions, miElevate, miNewVersion;
        SelfUpdate.Result newVersion;
        SplitContainer outerSplit;
        Panel topBar, header, tabsBar, gridHost, emptyState;
        Label lblHeroTitle, lblHeroSub, lblEmptyTitle, lblEmptySub, lblUpdNotes;
        LinkLabel lnkNotes, lnkDiag;
        TextBox txtFilter, txtLog, txtUpdDetail;
        ComboBox cmbKind;
        DataGridView gridUpdates;
        ToolStripStatusLabel lblStatus, lblAdmin;
        ToolStripProgressBar progress;
        System.Windows.Forms.Timer uiTimer;
        Font boldFont, italicFont;

        bool notesOpen;
        /// <summary>Resultado de la última tanda instalada, para el encabezado (null: no hubo).</summary>
        string installSummary;
        UpdateView updView = UpdateView.Pendientes;
        bool groupChosen;
        readonly Dictionary<UpdateCandidate, DataGridViewRow> updateRows = new Dictionary<UpdateCandidate, DataGridViewRow>();
        bool fillingUpdates;

        public MainForm(GuiSettings settings, string[] args)
        {
            this.settings = settings;
            startArgs = new List<string>(args);
            isAdmin = OsInfo.IsAdministrator();
            demo = startArgs.Contains("--demo");

            // Tema: el de Opciones; en la demostración se puede forzar con --tema claro|oscuro (no se guarda).
            int ti = startArgs.IndexOf("--tema");
            Theme.Set(ti >= 0 && ti + 1 < startArgs.Count ? Theme.Parse(startArgs[ti + 1]) : Theme.Parse(settings.Theme));

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = AppInfo.Title;
            Size = new Size(1180, 760);
            MinimumSize = new Size(860, 540);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f; } catch { }
            boldFont = new Font(Font, FontStyle.Bold);
            italicFont = new Font(Font, FontStyle.Italic);

            BuildUi();
            ApplyTheme();
            Load += delegate { FitToScreen(); };
            Log.LineWritten += OnLogLine;
            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 250;
            uiTimer.Tick += delegate { FlushUi(); };
            uiTimer.Start();
            // «Según Windows»: si se cambia el modo de Windows con el programa abierto, se sigue.
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (Theme.Mode != ThemeMode.Sistema || e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
            UiInvoke(delegate
            {
                bool was = Theme.Dark;
                Theme.Set(ThemeMode.Sistema);
                if (Theme.Dark != was) ApplyTheme();
            });
        }

        /// <summary>
        /// La ventana tiene que entrar en la pantalla. En pantallas chicas (1366×768 con escala) arranca maximizada; si no,
        /// la barra de estado quedaba debajo de la barra de tareas.
        /// </summary>
        void FitToScreen()
        {
            try
            {
                Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, wa.Width), Math.Min(MinimumSize.Height, wa.Height));
                if (Width > wa.Width || Height > wa.Height)
                {
                    Size = new Size(Math.Min(Width, wa.Width), Math.Min(Height, wa.Height));
                    Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
                    if (wa.Height < 780 || wa.Width < 1200) WindowState = FormWindowState.Maximized;
                }
            }
            catch (Exception ex) { Log.Warn("Tamaño de la ventana: " + ex.Message); }
        }

        // ================================================================== Ciclo de vida
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try { outerSplit.SplitterDistance = Math.Max(Math.Min(S(200), outerSplit.Height / 2), outerSplit.Height - S(130)); }
            catch (Exception ex) { Log.Info("Divisor del registro: " + ex.Message); }
            try { gridUpdates.Columns[2].DisplayIndex = 1; } catch { }   // el nombre primero (con la grilla ya creada)
            if (demo)
            {
                if (!startArgs.Contains("--vacio"))
                {
                    report = DemoData.Create();
                    if (startArgs.Contains("--marcado")) foreach (var u in SnapshotUpdates()) if (UpdateGroups.ShouldPremark(u)) u.Selected = true;
                }
                ShowUpdates();
                if (startArgs.Contains("--revisados")) ShowList(UpdateView.Revisados);
                if (startArgs.Contains("--historial")) BeginInvoke((Action)ShowHistory);
                if (startArgs.Contains("--opciones")) BeginInvoke((Action)ShowOptions);
                if (startArgs.Contains("--confirmar")) BeginInvoke((Action)StartInstall);
                if (startArgs.Contains("--registro")) ToggleLog();
                return;
            }
            CheckNewVersionInBackground();
            ShowUpdates();
            // 2.0.8: no busca al abrir. Sólo con «Buscar actualizaciones» (o si se abrió desde el aviso de la revisión
            // programada, que es tocar «Revisar en Actualizador»).
            if (startArgs.Contains("--actualizaciones") || startArgs.Contains("--todo")) StartUpdateScan();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy && !closingForElevation && e.CloseReason == CloseReason.UserClosing)
            {
                string msg = installing
                    ? "Se están instalando actualizaciones. Si cerrás ahora, el instalador en curso sigue en segundo plano pero no vas a ver el resultado.\n\n¿Cerrar de todos modos?"
                    : "Hay una búsqueda en curso. ¿Cancelarla y salir?";
                if (MessageBox.Show(this, msg, AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                if (busyCtx != null) busyCtx.RequestCancel();
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Log.LineWritten -= OnLogLine;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            uiTimer.Stop();
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { StartUpdateScan(); return true; }
            if (keyData == (Keys.Control | Keys.F)) { txtFilter.Focus(); txtFilter.SelectAll(); return true; }
            if (keyData == (Keys.Control | Keys.H)) { ShowHistory(); return true; }
            if (keyData == (Keys.Control | Keys.A) && gridUpdates.Focused) { ToggleMarkAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void OnLogLine(string line)
        {
            logQueue.Enqueue(line);
        }

        void FlushUi()
        {
            string line;
            var sb = new StringBuilder();
            int n = 0;
            while (n < 500 && logQueue.TryDequeue(out line)) { sb.Append(line).Append("\r\n"); n++; }
            if (sb.Length > 0)
            {
                if (txtLog.TextLength > 400000) txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 200000);
                txtLog.AppendText(sb.ToString());
            }
            string st = pendingStatus;
            if (st != null)
            {
                pendingStatus = null;
                lblStatus.Text = Fmt.Truncate(st, 200);
                // Mientras busca, el progreso también se ve en el centro de la ventana (la barra de estado es chica).
                if (scanning && emptyState.Visible) { lblEmptySub.Text = Fmt.Truncate(st, 160); LayoutEmpty(); }
            }
        }

        static string Plural(int n, string singular, string plural) { return Fmt.Plural(n, singular, plural); }

        /// <summary>
        /// ¿Hay una versión nueva de Actualizador? Sólo si se configuró dónde se publican (Opciones), como
        /// mucho una vez cada 20 horas, en segundo plano. Sólo avisa: nunca descarga ni instala.
        /// </summary>
        void CheckNewVersionInBackground()
        {
            // 2.0.32: sin fuente configurada, la de siempre (GitHub, la que publica Publicar.cmd).
            string source = SelfUpdate.EffectiveSource(settings.VersionSource);
            var t = new Thread(delegate()
            {
                SelfUpdate.Result r = null;
                try { r = SelfUpdate.CheckIfDue(source, AppInfo.Version); }
                catch (Exception ex) { Log.Warn("Versión nueva: " + ex.Message); }
                if (r == null || !r.Newer) return;
                UiInvoke(delegate
                {
                    newVersion = r;
                    bool auto = r.CanInstall && SelfInstall.CannotInstall(AppInfo.IsPortable, isAdmin) == null;
                    miNewVersion.Text = (auto ? "Instalar la versión nueva de Actualizador: " : "Versión nueva de Actualizador: ") + r.Latest + "…";
                    miNewVersion.Visible = true;
                    btnMore.Text = "Más ●";
                    lblStatus.Text = auto ? "Hay una versión nueva de Actualizador: la " + r.Latest + "." : SelfUpdate.Message(r);
                    // 2.0.35: si ya se intentó sola esta versión y no quedó, no se repite en cada apertura: se dice por qué (queda en «Más»).
                    string failed = auto ? SelfInstall.PreviousFailure(MachineMemory.Snapshot.Open(), r.Latest, AppInfo.Version) : null;
                    if (failed != null) { Log.Warn("Actualización automática: " + failed); lblStatus.Text = failed + " Podés reintentarlo desde «Más»."; return; }
                    // 2.0.32: se instala sola si Actualizador no está haciendo nada (al abrir, lo normal); si no, queda en «Más».
                    if (auto && !busy) InstallNewVersion();
                });
            });
            t.IsBackground = true;
            t.Name = "version-nueva";
            t.Start();
        }

        bool selfUpdating;

        /// <summary>
        /// 2.0.33: «Más › Buscar actualización de Actualizador…»: consulta ya (sin esperar la hora) y dice qué encontró; si hay una versión
        /// nueva que se puede instalar sola, ofrece instalarla.
        /// </summary>
        void CheckSelfUpdateNow()
        {
            if (selfUpdating) return;
            string source = SelfUpdate.EffectiveSource(settings.VersionSource);
            lblStatus.Text = "Buscando una versión nueva de Actualizador…";
            var t = new Thread(delegate()
            {
                SelfUpdate.Result r;
                try { r = SelfUpdate.Check(source, AppInfo.Version); SelfUpdate.Save(SelfUpdate.StatePath, r, source); }
                catch (Exception ex) { r = new SelfUpdate.Result { Error = ex.Message }; }
                UiInvoke(delegate
                {
                    if (r.Error != null)
                    {
                        lblStatus.Text = "No se pudo buscar una versión nueva de Actualizador.";
                        MessageBox.Show(this, "No se pudo buscar una versión nueva de Actualizador: " + r.Error + ".", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (!r.Newer)
                    {
                        lblStatus.Text = "Actualizador está al día (" + AppInfo.Version + ").";
                        MessageBox.Show(this, "Actualizador está al día: tenés la " + AppInfo.Version + " (la más nueva publicada es la " + r.Latest + ").", AppInfo.Name,
                                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    newVersion = r;
                    bool auto = r.CanInstall && SelfInstall.CannotInstall(AppInfo.IsPortable, isAdmin) == null;
                    miNewVersion.Text = (auto ? "Instalar la versión nueva de Actualizador: " : "Versión nueva de Actualizador: ") + r.Latest + "…";
                    miNewVersion.Visible = true;
                    btnMore.Text = "Más ●";
                    lblStatus.Text = "Hay una versión nueva de Actualizador: la " + r.Latest + ".";
                    ShowNewVersion();
                });
            });
            t.IsBackground = true;
            t.Name = "version-nueva-manual";
            t.Start();
        }

        /// <summary>
        /// 2.0.32: baja la versión nueva, la verifica y la deja instalándose; Actualizador se cierra y se vuelve a abrir solo (menos de un
        /// minuto). Si algo falla, sigue abierto como estaba y lo dice.
        /// </summary>
        void InstallNewVersion()
        {
            var r = newVersion;
            if (r == null || selfUpdating || busy) return;
            selfUpdating = true;
            lblStatus.Text = "Instalando Actualizador " + r.Latest + ": se cierra y se vuelve a abrir solo (menos de un minuto)…";
            btnScan.Enabled = false;
            var t = new Thread(delegate()
            {
                string error;
                string staged = SelfInstall.Stage(r, delegate(string m) { UiInvoke(delegate { lblStatus.Text = m; }); }, out error);
                UiInvoke(delegate
                {
                    selfUpdating = false;
                    btnScan.Enabled = true;
                    if (staged == null)
                    {
                        Log.Warn("Actualización automática: " + error);
                        lblStatus.Text = "No se pudo instalar Actualizador " + r.Latest + " solo: " + error + ". Podés reintentarlo desde «Más».";
                        return;
                    }
                    if (busy)
                    {
                        lblStatus.Text = "Actualizador " + r.Latest + " está listo para instalarse: tocá «Más» › «Instalar la versión nueva…» cuando termine lo que está haciendo.";
                        return;
                    }
                    string why = SelfInstall.Launch(staged, r.Latest);
                    if (why != null) { lblStatus.Text = "No se pudo instalar Actualizador " + r.Latest + " solo: " + why + "."; return; }
                    closingForElevation = true;     // cerrar sin preguntar: el instalador espera a que se cierre
                    Close();
                });
            });
            t.IsBackground = true;
            t.Name = "actualizacion-propia";
            t.Start();
        }

        void ShowNewVersion()
        {
            if (newVersion == null) return;
            if (newVersion.CanInstall && SelfInstall.CannotInstall(AppInfo.IsPortable, isAdmin) == null)
            {
                if (busy) { MessageBox.Show(this, "Esperá a que termine la búsqueda o la instalación para actualizar Actualizador.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (MessageBox.Show(this, "Hay una versión nueva de Actualizador: la " + newVersion.Latest + " (esta es la " + AppInfo.Version + ").\n\n" +
                                    "Se instala ahora: Actualizador se cierra y se vuelve a abrir solo en menos de un minuto. ¿Instalar?", AppInfo.Name,
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) InstallNewVersion();
                return;
            }
            if (MessageBox.Show(this, SelfUpdate.Message(newVersion) + "\n\n¿Abrir dónde está?", AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            string where = newVersion.Where;
            if (Fmt.WebUrl(where) != null) OpenPath(where);
            else if (File.Exists(where) || Directory.Exists(where)) OpenPath(File.Exists(where) ? Path.GetDirectoryName(where) : where);
        }
    }
}
