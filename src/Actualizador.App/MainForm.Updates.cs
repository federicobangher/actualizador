// Ventana principal: lista de actualizaciones (filas, casillas, detalle y resumen).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.App
{
    sealed partial class MainForm
    {
        // ================================================================== Lista
        bool Scanned { get { return report != null && report.UpdatesScannedLocal.HasValue; } }

        void ShowUpdates()
        {
            if (gridUpdates == null) return;
            bool showRows = Scanned && !scanning;
            gridUpdates.Visible = showRows;
            emptyState.Visible = !showRows;
            if (!showRows)
            {
                gridUpdates.Rows.Clear();
                updateRows.Clear();
                lblUpdNotes.Text = "";
                lblUpdNotes.Height = 0;
                lnkNotes.Visible = false;
                ShowUpdateDetail(null);
                UpdateSummary();
                return;
            }
            string[] updNotes;
            lock (report.UpdateNotes) updNotes = report.UpdateNotes.ToArray();
            // Las notas de la búsqueda (qué se consultó, qué no respondió) quedan plegadas: son para entender un caso, no para todos los días.
            lnkNotes.Visible = updNotes.Length > 0;
            lnkNotes.Text = (notesOpen ? "▴ " : "▾ ") + "Detalles de la búsqueda (" + updNotes.Length + ")";
            lblUpdNotes.Text = string.Join("\r\n", updNotes);
            lblUpdNotes.Height = updNotes.Length == 0 || !notesOpen ? 0 :
                Math.Min(S(220), TextRenderer.MeasureText(lblUpdNotes.Text, lblUpdNotes.Font, new Size(Math.Max(200, gridHost.Width - S(36)), 0), TextFormatFlags.WordBreak).Height + S(16));
            if (!groupChosen) updView = DefaultView();

            UpdateCandidate keep = CurrentUpdate();
            int firstRow = 0;
            try { firstRow = gridUpdates.FirstDisplayedScrollingRowIndex; } catch { }
            fillingUpdates = true;
            try { gridUpdates.EndEdit(); } catch { }
            gridUpdates.SuspendLayout();
            // try/finally: si algo falla a mitad de camino, la grilla no queda suspendida ni con «fillingUpdates» en true
            // (las marcas dejarían de llegar al modelo).
            try
            {
                gridUpdates.Rows.Clear();
                updateRows.Clear();
                var rows = new List<DataGridViewRow>();
                foreach (var u in UpdateGroups.InView(SnapshotUpdates(), updView))
                {
                    if (!KindVisible(u)) continue;
                    var row = new DataGridViewRow();
                    row.CreateCells(gridUpdates);
                    row.Tag = u;
                    FillUpdateRow(row, u);
                    if (!Matches(CellTexts(row))) continue;
                    rows.Add(row);
                    updateRows[u] = row;
                }
                gridUpdates.Rows.AddRange(rows.ToArray());
                // Sólo se puede marcar lo que se puede actualizar ahora (no lo ya actualizado o al día).
                foreach (var row in rows) row.Cells[0].ReadOnly = !IsActionable((UpdateCandidate)row.Tag) || installing;
            }
            finally { gridUpdates.ResumeLayout(); fillingUpdates = false; }
            // La fila elegida y el desplazamiento se conservan (antes volvía siempre arriba, también al marcar una casilla
            // con el filtro puesto o al refrescar durante la instalación).
            try
            {
                DataGridViewRow sel;
                if (keep != null && updateRows.TryGetValue(keep, out sel)) gridUpdates.CurrentCell = sel.Cells[1];
                else if (gridUpdates.Rows.Count > 0) gridUpdates.CurrentCell = gridUpdates.Rows[0].Cells[1];
                if (firstRow > 0 && firstRow < gridUpdates.Rows.Count) gridUpdates.FirstDisplayedScrollingRowIndex = firstRow;
                gridUpdates.HorizontalScrollingOffset = 0;
            }
            catch { }
            if (gridUpdates.Rows.Count == 0) ShowUpdateDetail(null);
            UpdateSummary();
        }

        static string[] CellTexts(DataGridViewRow row)
        {
            var l = new List<string>();
            for (int i = 1; i < row.Cells.Count; i++) l.Add(Convert.ToString(row.Cells[i].Value));
            return l.ToArray();
        }

        bool Matches(string[] cells)
        {
            string q = txtFilter.Text.Trim();
            if (q.Length == 0) return true;
            foreach (string c in cells)
                if (c != null && c.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) return true;
            return false;
        }

        bool KindVisible(UpdateCandidate u)
        {
            switch (cmbKind.SelectedIndex)
            {
                case 1: return u.Kind == UpdateKind.Programa;
                case 2: return u.Kind == UpdateKind.Controlador;
                case 3: return u.Kind == UpdateKind.Store;
                case 4: return u.Kind == UpdateKind.WindowsUpdate;
                default: return true;
            }
        }

        List<UpdateCandidate> SnapshotUpdates()
        {
            var r = report;
            if (r == null) return new List<UpdateCandidate>();
            lock (r.Updates) return new List<UpdateCandidate>(r.Updates);
        }

        /// <summary>
        /// La casilla muestra la marca del usuario. 2.0.8: durante la instalación también (antes una fila marcada que se estaba
        /// instalando aparecía con la casilla vacía, y parecía que se instalaba algo que no se había elegido).
        /// </summary>
        bool ShowsChecked(UpdateCandidate u) { return u.Selected && (IsActionable(u) || (installing && IsBusyStatus(u.Status))); }

        static bool IsBusyStatus(UpdateStatus s) { return s == UpdateStatus.Instalando || s == UpdateStatus.Pendiente; }

        void FillUpdateRow(DataGridViewRow row, UpdateCandidate u)
        {
            row.Cells[0].Value = ShowsChecked(u);
            row.Cells[1].Value = UpdateCandidate.KindText(u.Kind);
            row.Cells[2].Value = u.Name;
            row.Cells[3].Value = u.Publisher;
            row.Cells[4].Value = u.InstalledVersion;
            row.Cells[5].Value = u.AvailableVersion;
            // La fila de toda la Store no es una versión pendiente: es pedirle a la Store que revise.
            row.Cells[6].Value = u.Identifier == StoreProvider.AllAppsId && u.Status == UpdateStatus.Pendiente && string.IsNullOrEmpty(u.Result)
                ? "La revisa la Store" : UpdateGroups.StatusText(u);
            row.Cells[7].Value = u.Result;
            row.Cells[8].Value = u.Source;
            row.Cells[9].Value = u.Detail;
            row.Cells[2].ToolTipText = string.IsNullOrEmpty(u.Detail) ? "" : u.Detail;
            row.Cells[7].ToolTipText = string.IsNullOrEmpty(u.Result) ? "" : u.Result;
            var st = row.Cells[6].Style;
            st.Font = null;
            switch (u.Status)
            {
                case UpdateStatus.Disponible:
                    // Las opcionales comparten lista con lo recomendado: en violeta y sin negrita, para distinguirlas.
                    if (UpdateGroups.Of(u) == UpdateGroup.Opcional) st.ForeColor = Theme.Info;
                    else { st.ForeColor = Theme.Warn; st.Font = boldFont; }
                    break;
                case UpdateStatus.Pendiente: st.ForeColor = Theme.Warn; break;
                case UpdateStatus.AlDia: st.ForeColor = Theme.GoodText; break;
                case UpdateStatus.Instalada:
                case UpdateStatus.RequiereReinicio: st.ForeColor = Theme.GoodText; st.Font = boldFont; break;
                case UpdateStatus.Instalando: st.ForeColor = Theme.Info; st.Font = boldFont; break;
                case UpdateStatus.Fallida: case UpdateStatus.Error: st.ForeColor = Theme.Bad; st.Font = boldFont; break;
                case UpdateStatus.Ignorada: st.ForeColor = Theme.Muted; st.Font = italicFont; break;
                default: st.ForeColor = Theme.SubText; break;
            }
            st.SelectionForeColor = st.ForeColor;
            // Al pasar el mouse por una casilla que no se puede marcar, el motivo.
            row.Cells[0].ToolTipText = IsActionable(u) && !installing ? "Marcá para actualizar" : WhyNotSelectable(u);
        }

        /// <summary>La casilla con los colores del tema; las filas que no se pueden marcar no la muestran.</summary>
        void OnUpdateCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0) return;
            var u = gridUpdates.Rows[e.RowIndex].Tag as UpdateCandidate;
            e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            int state = -1;
            if (u != null)
            {
                bool shown = ShowsChecked(u);
                if (installing) state = shown ? 2 : (IsActionable(u) ? 0 : -1);
                else if (IsActionable(u)) state = shown ? 1 : 0;
            }
            Theme.PaintCheck(e.Graphics, e.CellBounds, state, scale);
            e.Handled = true;
        }

        static bool IsActionable(UpdateCandidate c) { return UpdateManager.CanInstall(c); }

        void OnUpdateCheckChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (fillingUpdates || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            var row = gridUpdates.Rows[e.RowIndex];
            var u = row.Tag as UpdateCandidate;
            if (u == null) return;
            u.Selected = row.Cells[0].Value is bool && (bool)row.Cells[0].Value;
            UpdateSummary();
        }

        /// <summary>Clic en una casilla que no se puede marcar: se explica por qué.</summary>
        void OnUpdateCellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
            var u = gridUpdates.Rows[e.RowIndex].Tag as UpdateCandidate;
            if (u == null || (IsActionable(u) && !installing)) return;
            ShowReason(WhyNotSelectable(u));
        }

        ToolTip reasonTip;

        /// <summary>El motivo se muestra junto al mouse y en la barra de estado.</summary>
        void ShowReason(string text)
        {
            lblStatus.Text = text;
            try
            {
                if (reasonTip == null) { reasonTip = new ToolTip(); reasonTip.IsBalloon = false; }
                var pt = gridUpdates.PointToClient(Cursor.Position);
                reasonTip.Show(text, gridUpdates, pt.X + S(16), pt.Y + S(16), 7000);
            }
            catch { }
        }

        /// <summary>Invierte la marca de las filas que se pueden actualizar; para las demás, explica el motivo.</summary>
        void ToggleRows(IList<DataGridViewRow> rows)
        {
            if (rows == null || rows.Count == 0) return;
            var marcables = new List<DataGridViewRow>();
            string motivo = null;
            foreach (var row in rows)
            {
                var u = row.Tag as UpdateCandidate;
                if (u == null) continue;
                if (installing || !IsActionable(u)) { if (motivo == null) motivo = WhyNotSelectable(u); continue; }
                marcables.Add(row);
            }
            if (marcables.Count > 0)
            {
                // Si alguna está desmarcada, se marcan todas; si ya estaban todas marcadas, se desmarcan.
                bool value = false;
                foreach (var row in marcables) if (!((UpdateCandidate)row.Tag).Selected) { value = true; break; }
                SetMarks(marcables, value);
                lblStatus.Text = Plural(marcables.Count, value ? "elemento marcado" : "elemento desmarcado", value ? "elementos marcados" : "elementos desmarcados") +
                                 (motivo != null ? ". " + motivo : ".");
            }
            else if (motivo != null) ShowReason(motivo);
        }

        void SetMarks(IEnumerable<DataGridViewRow> rows, bool value)
        {
            try { gridUpdates.EndEdit(); } catch { }
            fillingUpdates = true;
            try
            {
                foreach (var row in rows)
                {
                    var u = (UpdateCandidate)row.Tag;
                    u.Selected = value;
                    row.Cells[0].Value = ShowsChecked(u);
                }
            }
            finally { fillingUpdates = false; }   // si algo falla, la grilla no queda «trabada»
            gridUpdates.Invalidate();
            UpdateSummary();
        }

        string WhyNotSelectable(UpdateCandidate u)
        {
            if (installing) return "Esperá a que termine la instalación en curso para cambiar lo marcado.";
            string n = "«" + Fmt.Truncate(u.Name, 60) + "»";
            switch (u.Status)
            {
                case UpdateStatus.AlDia: return n + " ya está al día: no hay nada para actualizar.";
                case UpdateStatus.Instalada:
                case UpdateStatus.RequiereReinicio: return n + " ya se actualizó en esta sesión. Tocá «Buscar actualizaciones» para volver a comparar.";
                case UpdateStatus.NoVerificable: return n + " no figura en ningún catálogo con el cual comparar: no se puede actualizar desde acá. " +
                                                        (Fmt.WebUrl(u.InfoUrl) != null ? "Clic derecho › «Abrir sitio del fabricante»." : "Clic derecho › «Buscar el sitio oficial en la web».");
                case UpdateStatus.Error: return n + ": no se pudo verificar. " + Fmt.Truncate(u.Detail ?? "", 160);
                case UpdateStatus.Instalando: return n + " se está instalando.";
                case UpdateStatus.Ignorada: return n + " está ignorada: clic derecho › «Dejar de ignorar» para volver a ofrecerla.";
            }
            if (!u.Installable)
                return n + " no se puede actualizar desde Actualizador. " + Fmt.Truncate(u.Detail ?? "", 200);
            return n + " no se puede marcar ahora.";
        }

        /// <summary>Filas de la grilla (la lista y el filtro que se están viendo) que se pueden marcar.</summary>
        List<DataGridViewRow> MarkableVisibleRows()
        {
            var l = new List<DataGridViewRow>();
            foreach (DataGridViewRow row in gridUpdates.Rows)
            {
                var u = row.Tag as UpdateCandidate;
                if (u != null && UpdateGroups.MarkedByMarkAll(u)) l.Add(row);
            }
            return l;
        }

        /// <summary>
        /// 2.0.8: «Marcar todo» hace sólo eso: marca todo lo que se puede actualizar en la lista que se está viendo (con el tipo y el
        /// filtro elegidos). Si ya estaba todo marcado, el botón dice «Desmarcar todo» y desmarca todo, también lo que el filtro oculta.
        /// </summary>
        void ToggleMarkAll()
        {
            if (installing) { lblStatus.Text = "Esperá a que termine la instalación para cambiar lo marcado."; return; }
            if (report == null || busy) return;
            var rows = MarkableVisibleRows();
            if (AllMarked(rows))
            {
                foreach (var u in SnapshotUpdates()) u.Selected = false;
                SetMarks(rows, false);
                lblStatus.Text = "Se desmarcó todo.";
            }
            else
            {
                SetMarks(rows, true);
                lblStatus.Text = rows.Count == 0 ? "No hay nada para marcar en esta lista." : Plural(rows.Count, "elemento marcado", "elementos marcados") + ". Tocá «Actualizar» para instalarlos.";
            }
        }

        static bool AllMarked(List<DataGridViewRow> rows)
        {
            if (rows.Count == 0) return false;
            foreach (var r in rows) if (!((UpdateCandidate)r.Tag).Selected) return false;
            return true;
        }

        /// <summary>La lista que se muestra sola: la de disponibles si hay algo para hacer; si no, todo lo revisado.</summary>
        UpdateView DefaultView()
        {
            return UpdateGroups.DefaultView(UpdateGroups.Count(SnapshotUpdates()));
        }

        void ShowList(UpdateView v)
        {
            updView = v;
            groupChosen = true;
            ShowUpdates();
        }

        /// <summary>Encabezado, pestañas y botones según el estado (sin buscar, buscando, con resultados, instalando).</summary>
        void UpdateSummary()
        {
            if (lblHeroTitle == null) return;
            bool show = Scanned && !scanning;
            var k = show ? UpdateGroups.Count(SnapshotUpdates()) : new UpdateGroups.Counts();
            int available = k.ToUpdate + k.Optional;

            // --- Pestañas
            tabPending.Text = show ? "Disponibles (" + available + ")" : "Disponibles";
            tabReviewed.Text = show ? "Al día y sin verificar (" + (k.UpToDate + k.Unverified) + ")" : "Al día y sin verificar";
            tabPending.Enabled = tabReviewed.Enabled = show;
            LayoutTabs();
            tabPending.Invalidate(); tabReviewed.Invalidate();

            // --- Encabezado: el estado en una frase
            string title, sub;
            Color color = Theme.Text;
            if (installing)
            {
                title = "Actualizando…";
                sub = "Se instala sólo lo que marcaste. Podés seguir usando el equipo; cada fila muestra cómo va. El equipo nunca se reinicia solo.";
            }
            else if (scanning)
            {
                title = "Buscando actualizaciones…";
                sub = "Se compara cada programa, controlador y app con winget, los fabricantes, Windows Update y Microsoft Store. Tarda unos minutos.";
            }
            else if (!Scanned)
            {
                title = "Todavía no se buscaron actualizaciones";
                sub = "Tocá «Buscar actualizaciones» (F5). Después marcá lo que quieras y tocá «Actualizar».";
            }
            else
            {
                if (available > 0) { title = available == 1 ? "Hay 1 actualización disponible" : "Hay " + available + " actualizaciones disponibles"; color = Theme.Warn; }
                else if (k.UnverifiedPrograms > 0) { title = "Nada para actualizar entre lo que se pudo comparar"; color = Theme.GoodText; }
                else { title = "Todo está al día ✓"; color = Theme.GoodText; }
                var parts = new List<string>();
                if (installSummary != null) parts.Add(installSummary);
                if (available > 0 && !busy)
                    parts.Add(k.Selected == 0 ? "Marcá lo que quieras actualizar (o «Marcar todo») y tocá «Actualizar»."
                                              : Plural(k.Selected, "marcada", "marcadas") + ": «Actualizar» instala sólo esas.");
                else if (available == 0 && k.Selected > 0 && !busy)
                    // 2.0.13: lo marcado está en la otra lista (la Store «Sin novedades», marcada para que busque de nuevo).
                    parts.Add(Plural(k.Selected, "marcada", "marcadas") + " en «Al día y sin verificar»: «Actualizar» hace sólo eso.");
                var counts = new List<string>();
                if (k.Optional > 0) counts.Add(Plural(k.Optional, "opcional", "opcionales") + " (en violeta)");
                if (k.Unverified > 0) counts.Add(Plural(k.Unverified, "sin verificar", "sin verificar"));
                var at = report.UpdatesScannedLocal.Value;
                counts.Add((at.Date == DateTime.Today ? "buscado hoy a las " + at.ToString("HH:mm") : "buscado el " + Fmt.Date(at)) +
                           (report.UpdatesScanDuration.HasValue ? ", en " + Fmt.Elapsed(report.UpdatesScanDuration.Value) : ""));
                string line = string.Join(" · ", counts.ToArray());
                parts.Add(char.ToUpper(line[0]) + line.Substring(1) + ".");
                sub = string.Join("  ", parts.ToArray());
            }
            lblHeroTitle.Text = title;
            lblHeroTitle.ForeColor = color;
            lblHeroSub.Text = sub;
            lblHeroSub.ForeColor = Theme.SubText;
            if (lnkDiag != null) lnkDiag.Visible = show && !busy && (k.Failed > 0 || k.UnverifiedPrograms > 0 || lastAutoDiagnostic != null);
            LayoutHeader();

            // --- Estado vacío (antes de buscar y mientras busca)
            if (emptyState.Visible)
            {
                lblEmptyTitle.Text = scanning ? "Buscando actualizaciones…" : "Tocá «Buscar actualizaciones» para empezar";
                lblEmptyTitle.ForeColor = Theme.Text;
                if (!scanning) lblEmptySub.Text = "Actualizador no busca nada hasta que se lo pedís, y no instala nada que no marques.";
                LayoutEmpty();
            }

            // --- Botones
            btnScan.Text = busy ? (installing ? "Detener" : "Detener búsqueda") : "Buscar actualizaciones";
            btnScan.Enabled = !busy || (busyCtx != null && !busyCtx.CancelRequested);
            btnInstall.Text = k.Selected > 0 ? "Actualizar (" + k.Selected + ")" : "Actualizar";
            btnInstall.Enabled = !busy && show && k.Selected > 0;
            var markable = show && !busy ? MarkableVisibleRows() : new List<DataGridViewRow>();
            btnMarkAll.Text = AllMarked(markable) ? "Desmarcar todo" : "Marcar todo";
            btnMarkAll.Enabled = markable.Count > 0;
        }

        void RefreshUpdateRow(UpdateCandidate u)
        {
            DataGridViewRow row;
            bool shown = updateRows.TryGetValue(u, out row) && row.DataGridView != null;
            if (UpdateGroups.ListChanged(u, updView, shown, KindVisible(u), txtFilter.Text.Trim().Length > 0))
            {
                // 2.0.13: la fila cambió de lista (la de la Store pasa de «Al día» a «Instalando»): se rearma la que se está viendo.
                ShowUpdates();
                return;
            }
            if (shown)
            {
                fillingUpdates = true;
                try { FillUpdateRow(row, u); }
                finally { fillingUpdates = false; }
                gridUpdates.InvalidateRow(row.Index);
                if (CurrentUpdate() == u) ShowUpdateDetail(u);
                if (u.Status == UpdateStatus.Instalando)
                {
                    try { gridUpdates.FirstDisplayedScrollingRowIndex = Math.Max(0, row.Index - 3); } catch { }
                }
            }
            UpdateSummary();
        }

        /// <summary>Todo lo marcado que se puede instalar, en cualquier lista (la ventana de confirmación lo lista entero).</summary>
        List<UpdateCandidate> Marked()
        {
            var list = new List<UpdateCandidate>();
            if (report == null) return list;
            foreach (var u in SnapshotUpdates()) if (u.Selected && IsActionable(u) && !list.Contains(u)) list.Add(u);
            return list;
        }

        /// <summary>Texto completo de la fila elegida en el recuadro de abajo.</summary>
        void ShowUpdateDetail(UpdateCandidate u)
        {
            if (txtUpdDetail == null) return;
            if (u == null) { txtUpdDetail.Text = Scanned && !scanning ? "  Elegí una fila para ver su detalle completo." : ""; return; }
            var sb = new StringBuilder();
            sb.Append(u.Name).Append(" — ").Append(UpdateCandidate.StatusText(u.Status));
            string versions = (string.IsNullOrEmpty(u.InstalledVersion) ? "" : "Instalada: " + u.InstalledVersion) +
                              (string.IsNullOrEmpty(u.AvailableVersion) ? "" : "   Disponible: " + u.AvailableVersion) +
                              (string.IsNullOrEmpty(u.Source) ? "" : "   Origen: " + u.Source);
            if (versions.Length > 0) sb.Append("\r\n").Append(versions.Trim());
            if (!string.IsNullOrEmpty(u.Result)) sb.Append("\r\nResultado: ").Append(u.Result);
            if (!string.IsNullOrEmpty(u.Detail)) sb.Append("\r\nDetalle: ").Append(u.Detail);
            // Sangría para que el texto no quede pegado al borde.
            txtUpdDetail.Text = "  " + sb.ToString().Replace("\r\n", "\r\n  ");
        }

        UpdateCandidate CurrentUpdate()
        {
            try { return gridUpdates.CurrentRow != null ? gridUpdates.CurrentRow.Tag as UpdateCandidate : null; }
            catch { return null; }
        }

        /// <summary>
        /// Abre el sitio oficial de la fila actual, o una búsqueda web. Sólo direcciones http/https validadas
        /// (<see cref="Fmt.WebUrl"/>); elevado, la abre explorer.exe en la sesión del usuario.
        /// </summary>
        void OpenSite(bool search)
        {
            var u = CurrentUpdate();
            if (u == null) return;
            string url = search ? ProgramHints.SearchUrl(u.Name, u.Publisher) : Fmt.WebUrl(u.InfoUrl);
            if (url == null) { ShowReason("No se conoce el sitio oficial de «" + Fmt.Truncate(u.Name, 60) + "»: probá «Buscar el sitio oficial en la web»."); return; }
            lblStatus.Text = "Abriendo " + new Uri(url).Host + "…";
            OpenPath(url);
        }
    }
}
