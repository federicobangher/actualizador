// Ventana principal: armado de los controles.
// 2.0.8: más simple. Arriba tres botones (Buscar actualizaciones · Actualizar · Marcar todo), el tema y «Más»; debajo el
// estado en una frase, las dos listas como pestañas y la grilla. Lo administrativo (historial, registro, opciones,
// exportar, diagnóstico, política, desinstalar) va en el menú «Más».
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.App
{
    sealed partial class MainForm
    {
        // ================================================================== Construcción
        void BuildUi()
        {
            // --- Barra de estado
            var status = new StatusStrip();
            status.SizingGrip = false;
            lblStatus = new ToolStripStatusLabel("Listo.");
            lblStatus.Spring = true;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            progress = new ToolStripProgressBar();
            progress.Visible = false;
            progress.Size = new Size(S(160), S(14));
            // Con el manifiesto (requireAdministrator) siempre es administrador; si igual no lo es (copia sin manifiesto,
            // directiva que lo impide), se avisa.
            lblAdmin = new ToolStripStatusLabel("Sin permisos de administrador: controladores y Windows Update no se pueden instalar");
            lblAdmin.Tag = "color propio";
            lblAdmin.Visible = !isAdmin;
            status.Items.AddRange(new ToolStripItem[] { lblStatus, progress, lblAdmin });

            // --- Registro (abajo, oculto hasta que se pide)
            txtLog = new TextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.WordWrap = false;
            txtLog.ScrollBars = ScrollBars.Both;
            txtLog.BorderStyle = BorderStyle.None;
            try { txtLog.Font = new Font("Consolas", Font.Size); } catch { }

            var main = new Panel();
            main.Dock = DockStyle.Fill;
            BuildTopBar();
            BuildHeader();
            BuildTabs();
            BuildGrid();
            // Orden de acoplamiento: primero lo que llena, después los bordes (el último agregado queda más afuera).
            main.Controls.Add(gridHost);
            main.Controls.Add(txtUpdDetail);
            main.Controls.Add(lblUpdNotes);
            main.Controls.Add(tabsBar);
            main.Controls.Add(header);
            main.Controls.Add(topBar);

            outerSplit = new SplitContainer();
            outerSplit.Dock = DockStyle.Fill;
            outerSplit.Orientation = Orientation.Horizontal;
            outerSplit.FixedPanel = FixedPanel.Panel2;
            outerSplit.SplitterWidth = Math.Max(1, S(1));
            outerSplit.Panel1.Controls.Add(main);
            outerSplit.Panel2.Controls.Add(txtLog);
            outerSplit.Panel2Collapsed = true;

            Controls.Add(outerSplit);
            Controls.Add(status);
            SetCueBanner(txtFilter, "Filtrar…  (Ctrl+F)");
        }

        void BuildTopBar()
        {
            topBar = new Panel();
            topBar.Dock = DockStyle.Top;
            topBar.Height = S(64);
            topBar.Padding = new Padding(S(16), S(12), S(16), S(12));
            Theme.SetRole(topBar, Theme.Role.Surface);

            btnScan = BigButton("Buscar actualizaciones", Theme.Role.Primary, S(200));
            btnScan.Click += delegate { if (busy) StopCurrent(); else StartUpdateScan(); };
            Tip(btnScan, "Compara cada programa, controlador y app con winget, las páginas oficiales de los fabricantes, Windows Update, " +
                         "el Catálogo de Microsoft Update y Microsoft Store (F5). Mientras busca o instala, este botón detiene.");
            btnInstall = BigButton("Actualizar", Theme.Role.Good, S(150));
            btnInstall.Enabled = false;
            btnInstall.Click += delegate { StartInstall(); };
            Tip(btnInstall, "Instala sólo lo que está marcado. Antes muestra la lista para confirmar.");
            btnMarkAll = BigButton("Marcar todo", Theme.Role.Secondary, S(140));
            btnMarkAll.Enabled = false;
            btnMarkAll.Click += delegate { ToggleMarkAll(); };
            Tip(btnMarkAll, "Marca todo lo que se puede actualizar en la lista que estás viendo; si ya está todo marcado, lo desmarca (Ctrl+A en la lista).");

            btnMore = BigButton("Más", Theme.Role.Secondary, S(84));
            btnMore.Text = "Más  ▾";
            btnTheme = BigButton("", Theme.Role.Secondary, S(130));
            btnTheme.Click += delegate { ToggleTheme(); };
            Tip(btnTheme, "Cambia entre el tema claro y el oscuro (también en Más › Opciones, donde se puede elegir «Según Windows»).");

            moreMenu = new ContextMenuStrip();
            miHistory = MenuItem("Historial de actualizaciones…", "Ctrl+H", delegate { ShowHistory(); });
            miLog = MenuItem("Ver registro", null, delegate { ToggleLog(); });
            miOptions = MenuItem("Opciones…", null, delegate { ShowOptions(); });
            miElevate = MenuItem("Reiniciar como administrador", null, delegate { Elevate(""); });
            miElevate.Visible = !isAdmin;
            miNewVersion = MenuItem("Versión nueva de Actualizador…", null, delegate { ShowNewVersion(); });
            miNewVersion.Visible = false;
            // 2.0.33: siempre a mano, además de la búsqueda sola al abrir.
            var miCheckSelf = MenuItem("Buscar actualización de Actualizador…", null, delegate { CheckSelfUpdateNow(); });
            moreMenu.Items.AddRange(new ToolStripItem[]
            {
                miHistory, miLog, miOptions, miElevate, new ToolStripSeparator(),
                MenuItem("Exportar la lista en CSV (Excel)…", null, delegate { ExportUpdates(UpdateExportFormat.Csv); }),
                MenuItem("Exportar la lista en JSON…", null, delegate { ExportUpdates(UpdateExportFormat.Json); }),
                MenuItem("Guardar un diagnóstico de actualizaciones (.zip)…", null, delegate { ExportDiagnostic(); }),
                new ToolStripSeparator(),
                MenuItem("Ver la política de la empresa", null, delegate { ShowPolicy(); }),
                MenuItem("Verificar el registro de cambios", null, delegate { VerifyAuditTrail(); }),
                MenuItem("Abrir la carpeta del registro de cambios", null, delegate { OpenAuditFolder(); }),
                MenuItem("Abrir la carpeta de registros", null, delegate { OpenPath(AppInfo.LogDirectory); }),
                new ToolStripSeparator(),
                miNewVersion,
                miCheckSelf,
                MenuItem("Acerca de Actualizador", null, delegate { ShowAbout(); }),
                // 2.0.10: la portable no está instalada: no hay nada que desinstalar (se borra su carpeta).
                AppInfo.IsPortable ? (ToolStripItem)new ToolStripSeparator { Visible = false }
                                   : MenuItem("Desinstalar Actualizador…", null, delegate { UninstallSelf(); })
            });
            moreMenu.Opening += delegate
            {
                miLog.Text = outerSplit.Panel2Collapsed ? "Ver registro" : "Ocultar registro";
                miOptions.Enabled = !busy;
            };
            btnMore.Click += delegate { moreMenu.Show(btnMore, new Point(btnMore.Width - moreMenu.Width, btnMore.Height + S(2)), ToolStripDropDownDirection.BelowRight); };

            var left = new FlowLayoutPanel();
            left.Dock = DockStyle.Left;
            left.AutoSize = true;
            left.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            left.WrapContents = false;
            left.Controls.AddRange(new Control[] { btnScan, btnInstall, btnMarkAll });
            var right = new FlowLayoutPanel();
            right.Dock = DockStyle.Right;
            right.AutoSize = true;
            right.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            right.WrapContents = false;
            right.Controls.AddRange(new Control[] { btnTheme, btnMore });
            foreach (Control c in new Control[] { btnScan, btnInstall, btnMarkAll, btnTheme }) c.Margin = new Padding(0, 0, S(8), 0);
            btnMore.Margin = Padding.Empty;
            topBar.Controls.Add(right);
            topBar.Controls.Add(left);
            // Línea fina debajo de la barra.
            topBar.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };
        }

        void BuildHeader()
        {
            header = new Panel();
            header.Dock = DockStyle.Top;
            header.Padding = new Padding(S(18), S(14), S(18), S(4));
            lblHeroTitle = new Label();
            lblHeroTitle.AutoSize = true;
            lblHeroTitle.Font = new Font(Font.FontFamily, Font.Size + 6, FontStyle.Bold);
            lblHeroTitle.Location = new Point(S(16), S(14));
            lblHeroSub = new Label();
            lblHeroSub.AutoSize = true;
            Theme.SetRole(lblHeroSub, Theme.Role.Sub);
            lnkNotes = new LinkLabel();
            lnkNotes.AutoSize = true;
            lnkNotes.Text = "Detalles de la búsqueda";
            lnkNotes.Visible = false;
            lnkNotes.LinkClicked += delegate { notesOpen = !notesOpen; ShowUpdates(); };
            // Diagnóstico a mano: visible cuando algo falló o quedó sin verificar, que es cuando sirve mandarlo.
            lnkDiag = new LinkLabel();
            lnkDiag.AutoSize = true;
            lnkDiag.Text = "Diagnóstico";
            lnkDiag.Visible = false;
            lnkDiag.LinkClicked += delegate { OpenOrExportDiagnostic(); };
            Tip(lnkDiag, "Un .zip con las filas, las notas y los registros (sin contraseñas ni tokens) para mandar si algo no se actualizó.");
            header.Controls.AddRange(new Control[] { lblHeroTitle, lblHeroSub, lnkNotes, lnkDiag });
            header.Resize += delegate { LayoutHeader(); };
        }

        void BuildTabs()
        {
            tabsBar = new Panel();
            tabsBar.Dock = DockStyle.Top;
            tabsBar.Height = S(44);
            tabPending = TabButton(UpdateView.Pendientes);
            tabReviewed = TabButton(UpdateView.Revisados);
            Tip(tabPending, "Lo que tiene una versión nueva. Arriba lo recomendado; debajo, en violeta, lo opcional (BIOS, controladores que Windows no eligió, betas…).");
            Tip(tabReviewed, "Todo lo demás que se revisó: lo que está al día y lo que no se pudo comparar («No verificable»).");
            cmbKind = new ComboBox();
            cmbKind.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbKind.Width = S(160);
            cmbKind.Items.AddRange(new object[] { "Todos los tipos", "Programas", "Controladores", "Microsoft Store", "Windows Update" });
            cmbKind.SelectedIndex = 0;
            cmbKind.SelectedIndexChanged += delegate { ShowUpdates(); };
            txtFilter = new TextBox();
            txtFilter.Width = S(220);
            txtFilter.BorderStyle = BorderStyle.FixedSingle;
            txtFilter.TextChanged += delegate { ShowUpdates(); };
            tabsBar.Controls.AddRange(new Control[] { tabPending, tabReviewed, cmbKind, txtFilter });
            tabsBar.Resize += delegate { LayoutTabs(); };
            tabsBar.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, tabsBar.Height - 1, tabsBar.Width, tabsBar.Height - 1);
            };

            lblUpdNotes = new Label();
            lblUpdNotes.Dock = DockStyle.Top;
            lblUpdNotes.AutoSize = false;
            lblUpdNotes.Height = 0;
            lblUpdNotes.Padding = new Padding(S(18), S(6), S(18), S(8));
            Theme.SetRole(lblUpdNotes, Theme.Role.Sub);
        }

        Button TabButton(UpdateView view)
        {
            var b = new Button();
            b.AutoSize = false;
            b.Height = S(40);
            b.Width = S(160);
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.TabStop = true;
            Theme.SetRole(b, Theme.Role.Tab);
            b.Click += delegate { ShowList(view); };
            // Pestaña: texto en negrita y una línea de color abajo si es la elegida.
            b.Paint += delegate(object s, PaintEventArgs e)
            {
                bool active = updView == view;
                var g = e.Graphics;
                g.Clear(Theme.Back);
                using (var f = new Font(Font, active ? FontStyle.Bold : FontStyle.Regular))
                    TextRenderer.DrawText(g, b.Text, f, new Rectangle(0, 0, b.Width, b.Height - S(3)), active ? Theme.Text : Theme.SubText,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                if (active) using (var br = new SolidBrush(Theme.Accent)) g.FillRectangle(br, S(6), b.Height - S(3), b.Width - S(12), S(3));
                if (b.Focused) ControlPaint.DrawFocusRectangle(g, new Rectangle(S(2), S(4), b.Width - S(4), b.Height - S(10)));
            };
            return b;
        }

        void BuildGrid()
        {
            gridHost = new Panel();
            gridHost.Dock = DockStyle.Fill;
            gridHost.Padding = new Padding(0);

            gridUpdates = NewGrid();
            gridUpdates.Dock = DockStyle.Fill;
            gridUpdates.ReadOnly = false;
            var check = new DataGridViewCheckBoxColumn();
            check.HeaderText = "";
            check.Width = S(40);
            check.SortMode = DataGridViewColumnSortMode.NotSortable;
            // Fija a la izquierda: al hacer clic en una celda cortada del borde derecho la grilla se desplaza y la casilla
            // quedaba fuera de la vista.
            check.Frozen = true;
            check.Resizable = DataGridViewTriState.False;
            check.ToolTipText = "Marcá lo que quieras actualizar (también: doble clic en la fila o barra espaciadora)";
            gridUpdates.Columns.Add(check);
            // Índices fijos (los usa el resto del código): 1 Tipo, 2 Nombre, 3 Editor, 4 Instalada, 5 Disponible, 6 Estado,
            // 7 Resultado, 8 Origen, 9 Detalle. Se ven en otro orden: el nombre primero.
            AddColumn(gridUpdates, "Tipo", S(120));
            AddColumn(gridUpdates, "Nombre", S(260));
            AddColumn(gridUpdates, "Editor", S(150));
            AddColumn(gridUpdates, "Instalada", S(130));
            AddColumn(gridUpdates, "Disponible", S(130));
            AddColumn(gridUpdates, "Estado", S(160));
            AddColumn(gridUpdates, "Resultado", S(240));
            AddColumn(gridUpdates, "Origen", S(140));
            AddColumn(gridUpdates, "Detalle", S(360));
            for (int i = 1; i < gridUpdates.Columns.Count; i++) { gridUpdates.Columns[i].ReadOnly = true; gridUpdates.Columns[i].SortMode = DataGridViewColumnSortMode.Automatic; }
            gridUpdates.Columns[2].DisplayIndex = 1;
            gridUpdates.Columns[3].Visible = false;   // Editor
            gridUpdates.Columns[8].Visible = false;   // Origen (está en el recuadro de abajo)
            gridUpdates.Columns[9].Visible = false;   // Detalle (ídem)
            gridUpdates.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            gridUpdates.Columns[2].FillWeight = 55;
            gridUpdates.Columns[2].MinimumWidth = S(200);
            gridUpdates.Columns[7].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            gridUpdates.Columns[7].FillWeight = 45;
            gridUpdates.Columns[7].MinimumWidth = S(140);
            gridUpdates.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            gridUpdates.ColumnHeadersHeight = S(34);
            gridUpdates.RowTemplate.Height = S(32);
            gridUpdates.DefaultCellStyle.Padding = new Padding(S(6), 0, S(6), 0);
            gridUpdates.ColumnHeadersDefaultCellStyle.Padding = new Padding(S(6), 0, S(6), 0);
            gridUpdates.ColumnHeadersDefaultCellStyle.Font = boldFont;
            gridUpdates.CurrentCellDirtyStateChanged += delegate
            {
                if (gridUpdates.IsCurrentCellDirty) gridUpdates.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            gridUpdates.CellValueChanged += OnUpdateCheckChanged;
            gridUpdates.CellClick += OnUpdateCellClick;
            gridUpdates.CellPainting += OnUpdateCellPainting;
            gridUpdates.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                // La casilla ya cambia con el clic simple; el doble clic es para el resto de la fila.
                if (e.RowIndex >= 0 && e.ColumnIndex > 0) ToggleRows(new[] { gridUpdates.Rows[e.RowIndex] });
            };
            gridUpdates.ContextMenuStrip = BuildRowMenu();
            gridUpdates.CellMouseDown += delegate(object s, DataGridViewCellMouseEventArgs e)
            {
                // El clic derecho elige la fila, como en el Explorador (sin cambiar la columna de casillas).
                if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
                try
                {
                    var row = gridUpdates.Rows[e.RowIndex];
                    if (!row.Selected) { gridUpdates.ClearSelection(); row.Selected = true; }
                    gridUpdates.CurrentCell = row.Cells[Math.Max(1, e.ColumnIndex)];
                }
                catch { }
            };
            gridUpdates.KeyDown += delegate(object s, KeyEventArgs e)
            {
                // Barra espaciadora sobre filas seleccionadas (en la casilla la maneja la grilla).
                if (e.KeyCode != Keys.Space || e.Modifiers != Keys.None) return;
                if (gridUpdates.CurrentCell != null && gridUpdates.CurrentCell.ColumnIndex == 0) return;
                var rows = new List<DataGridViewRow>();
                foreach (DataGridViewRow r in gridUpdates.SelectedRows) rows.Add(r);
                if (rows.Count == 0 && gridUpdates.CurrentRow != null) rows.Add(gridUpdates.CurrentRow);
                ToggleRows(rows);
                e.Handled = e.SuppressKeyPress = true;
            };
            gridUpdates.CurrentCellChanged += delegate { ShowUpdateDetail(CurrentUpdate()); };

            // Estado vacío: antes de la primera búsqueda y mientras busca, un mensaje grande en el centro en vez de una grilla vacía.
            emptyState = new Panel();
            emptyState.Dock = DockStyle.Fill;
            Theme.SetRole(emptyState, Theme.Role.Surface);
            lblEmptyTitle = new Label();
            lblEmptyTitle.AutoSize = true;
            lblEmptyTitle.Font = new Font(Font.FontFamily, Font.Size + 4, FontStyle.Bold);
            lblEmptySub = new Label();
            lblEmptySub.AutoSize = true;
            Theme.SetRole(lblEmptySub, Theme.Role.Sub);
            emptyState.Controls.Add(lblEmptyTitle);
            emptyState.Controls.Add(lblEmptySub);
            emptyState.Resize += delegate { LayoutEmpty(); };

            gridHost.Controls.Add(gridUpdates);
            gridHost.Controls.Add(emptyState);

            // Recuadro de detalle: el texto completo de la fila elegida (detalle y resultado), que en la grilla se corta.
            txtUpdDetail = new TextBox();
            txtUpdDetail.Dock = DockStyle.Bottom;
            txtUpdDetail.Multiline = true;
            txtUpdDetail.ReadOnly = true;
            txtUpdDetail.WordWrap = true;
            txtUpdDetail.ScrollBars = ScrollBars.Vertical;
            txtUpdDetail.Height = S(84);
            txtUpdDetail.BorderStyle = BorderStyle.None;
            Theme.SetRole(txtUpdDetail, Theme.Role.Surface);
        }

        ContextMenuStrip BuildRowMenu()
        {
            // Clic derecho: el sitio oficial del programa (el que publica el fabricante o el que registra el propio programa)
            // o, si no hay, una búsqueda web; la página con la versión; ignorar.
            var menu = new ContextMenuStrip();
            var miSite = new ToolStripMenuItem("Abrir sitio del fabricante");
            var miSearch = new ToolStripMenuItem("Buscar el sitio oficial en la web");
            miSite.Click += delegate { OpenSite(false); };
            miSearch.Click += delegate { OpenSite(true); };
            var miSource = new ToolStripMenuItem("Indicar la página oficial con la versión…");
            miSource.Click += delegate { SetOfficialPage(); };
            var miIgnoreVersion = new ToolStripMenuItem("Ignorar esta versión");
            var miIgnoreAll = new ToolStripMenuItem("Ignorar siempre (todas las versiones)");
            var miUnignore = new ToolStripMenuItem("Dejar de ignorar");
            miIgnoreVersion.Click += delegate { IgnoreSelected(false); };
            miIgnoreAll.Click += delegate { IgnoreSelected(true); };
            miUnignore.Click += delegate { UnignoreSelected(); };
            menu.Items.AddRange(new ToolStripItem[] { miSite, miSearch, miSource, new ToolStripSeparator(), miIgnoreVersion, miIgnoreAll, miUnignore });
            menu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                var u = CurrentUpdate();
                if (u == null) { e.Cancel = true; return; }
                string url = Fmt.WebUrl(u.InfoUrl);
                miSite.Enabled = url != null;
                miSite.Text = url != null ? "Abrir sitio del fabricante (" + new Uri(url).Host + ")" : "Abrir sitio del fabricante (no se conoce)";
                miSource.Enabled = CanSetSource(u) && !installing && !demo;
                var rows = SelectedUpdates();
                bool canIgnore = false, canUnignore = false, withVersion = false;
                foreach (var x in rows)
                {
                    if (IgnoreList.CanIgnore(x)) { canIgnore = true; if (!string.IsNullOrEmpty(x.AvailableVersion)) withVersion = true; }
                    if (x.IgnoredBy is IgnoreRule) canUnignore = true;
                }
                miIgnoreVersion.Enabled = canIgnore && withVersion && !installing && !demo;
                miIgnoreAll.Enabled = canIgnore && !installing && !demo;
                miUnignore.Enabled = canUnignore && !installing && !demo;
                miIgnoreVersion.Text = rows.Count == 1 && !string.IsNullOrEmpty(u.AvailableVersion) ? "Ignorar esta versión (" + Fmt.Truncate(u.AvailableVersion, 40) + ")" : "Ignorar esta versión";
            };
            return menu;
        }

        // ================================================================== Ubicación

        void LayoutHeader()
        {
            if (header == null) return;
            int x = S(18), width = Math.Max(S(200), header.ClientSize.Width - S(36));
            lblHeroTitle.Location = new Point(x - S(2), S(12));
            lblHeroTitle.MaximumSize = new Size(width, 0);
            lblHeroSub.Location = new Point(x, lblHeroTitle.Bottom + S(4));
            lblHeroSub.MaximumSize = new Size(width, 0);
            int y = lblHeroSub.Bottom + S(6);
            int lx = x;
            foreach (var l in new[] { lnkNotes, lnkDiag })
            {
                if (!l.Visible) continue;
                l.Location = new Point(lx, y);
                lx = l.Right + S(16);
            }
            bool links = lnkNotes.Visible || lnkDiag.Visible;
            header.Height = (links ? lnkNotes.Visible ? lnkNotes.Bottom : lnkDiag.Bottom : lblHeroSub.Bottom) + S(12);
        }

        void LayoutTabs()
        {
            if (tabsBar == null) return;
            int x = S(12);
            foreach (var t in new[] { tabPending, tabReviewed })
            {
                int w = TextRenderer.MeasureText(t.Text, boldFont).Width + S(28);
                t.SetBounds(x, tabsBar.Height - t.Height - 1, w, t.Height);
                x += w + S(4);
            }
            int right = tabsBar.ClientSize.Width - S(16);
            int cy = (tabsBar.Height - txtFilter.Height) / 2;
            txtFilter.Location = new Point(right - txtFilter.Width, cy);
            cmbKind.Location = new Point(txtFilter.Left - S(8) - cmbKind.Width, (tabsBar.Height - cmbKind.Height) / 2);
            bool fits = cmbKind.Left > x + S(8);
            cmbKind.Visible = txtFilter.Visible = fits || txtFilter.Text.Length > 0;
        }

        void LayoutEmpty()
        {
            if (emptyState == null) return;
            int w = Math.Max(S(200), emptyState.ClientSize.Width - S(80));
            lblEmptyTitle.MaximumSize = new Size(w, 0);
            lblEmptySub.MaximumSize = new Size(w, 0);
            int total = lblEmptyTitle.Height + S(8) + lblEmptySub.Height;
            int top = Math.Max(S(20), (emptyState.ClientSize.Height - total) / 2 - S(20));
            lblEmptyTitle.Location = new Point((emptyState.ClientSize.Width - lblEmptyTitle.Width) / 2, top);
            lblEmptySub.Location = new Point((emptyState.ClientSize.Width - lblEmptySub.Width) / 2, lblEmptyTitle.Bottom + S(8));
        }

        // ================================================================== Tema

        void ApplyTheme()
        {
            Theme.Apply(this);
            Theme.Menu(moreMenu);
            Theme.Menu(gridUpdates.ContextMenuStrip);
            lblAdmin.ForeColor = Theme.Warn;
            txtLog.BackColor = Theme.Surface;
            txtLog.ForeColor = Theme.Text;
            outerSplit.BackColor = Theme.Border;
            btnTheme.Text = Theme.Dark ? "☀  Tema claro" : "☾  Tema oscuro";
            // Lo que depende del estado (colores del título y de cada fila) se vuelve a calcular.
            if (report != null && !scanning) ShowUpdates(); else UpdateSummary();
            gridUpdates.Invalidate();
            Invalidate(true);
        }

        void ToggleTheme()
        {
            Theme.Set(Theme.Dark ? ThemeMode.Claro : ThemeMode.Oscuro);
            if (!demo) { settings.Theme = Theme.ToText(Theme.Mode); settings.Save(); }
            ApplyTheme();
        }

        // ================================================================== Fábricas

        Button BigButton(string text, Theme.Role role, int width)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = false;
            b.Size = new Size(width, S(40));
            b.Font = role == Theme.Role.Primary || role == Theme.Role.Good ? boldFont : Font;
            Theme.SetRole(b, role);
            Theme.TrackEnabled(b);
            Theme.PaintDisabledText(b);
            return b;
        }

        ToolStripMenuItem MenuItem(string text, string keys, EventHandler click)
        {
            var mi = new ToolStripMenuItem(text, null, click);
            if (keys != null) mi.ShortcutKeyDisplayString = keys;
            return mi;
        }

        readonly ToolTip tips = new ToolTip();

        void Tip(Control c, string text) { tips.SetToolTip(c, text); }

        static DataGridView NewGrid()
        {
            var g = new DataGridView();
            g.ReadOnly = true;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.AllowUserToOrderColumns = true;
            g.RowHeadersVisible = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = true;
            g.BorderStyle = BorderStyle.None;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithAutoHeaderText;
            try
            {
                typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(g, true, null);
            }
            catch { }
            return g;
        }

        static void AddColumn(DataGridView g, string header, int width)
        {
            var c = new DataGridViewTextBoxColumn();
            c.HeaderText = header;
            c.SortMode = DataGridViewColumnSortMode.Automatic;
            c.Width = width;
            g.Columns.Add(c);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        static void SetCueBanner(TextBox box, string text)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            try { SendMessage(box.Handle, 0x1501, (IntPtr)1, text); } catch { }   // EM_SETCUEBANNER
        }
    }
}
