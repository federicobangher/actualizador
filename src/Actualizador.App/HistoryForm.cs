// 2.0.8: el historial de actualizaciones en su propia ventana, con filtro y orden por columna.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Actualizador.Core;

namespace Actualizador.App
{
    sealed class HistoryForm : Form
    {
        readonly TableData table;
        readonly DataGridView grid = new DataGridView();
        readonly TextBox filter = new TextBox();
        readonly Label count = new Label();
        List<string[]> rows = new List<string[]>();
        int sortColumn = -1;
        bool sortAscending = true;

        public HistoryForm(Form owner, TableData table)
        {
            this.table = table;
            float scale = 1f;
            try { using (var g = owner.CreateGraphics()) scale = g.DpiX / 96f; } catch { }
            Func<int, int> S = delegate(int px) { return (int)Math.Round(px * scale); };
            Text = table.Title;
            Font = owner.Font;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            ClientSize = new Size(S(980), S(560));
            MinimumSize = new Size(S(600), S(360));
            KeyPreview = true;

            var top = new Panel();
            top.Dock = DockStyle.Top;
            top.Padding = new Padding(S(14), S(12), S(14), S(8));
            var notes = new List<string>();
            if (table.Error != null) notes.Add("⚠ " + table.Error);
            notes.AddRange(table.Notes);
            var lblNotes = new Label();
            lblNotes.AutoSize = true;
            lblNotes.Text = string.Join("\r\n", notes.ToArray());
            lblNotes.Location = new Point(S(14), S(12));
            Theme.SetRole(lblNotes, Theme.Role.Sub);
            filter.Width = S(260);
            filter.BorderStyle = BorderStyle.FixedSingle;
            filter.TextChanged += delegate { Refill(); };
            count.AutoSize = true;
            Theme.SetRole(count, Theme.Role.Sub);
            top.Controls.AddRange(new Control[] { lblNotes, filter, count });
            top.Resize += delegate
            {
                lblNotes.MaximumSize = new Size(Math.Max(S(200), top.ClientSize.Width - S(28)), 0);
                int y = lblNotes.Text.Length > 0 ? lblNotes.Bottom + S(8) : S(12);
                filter.Location = new Point(S(14), y);
                count.Location = new Point(filter.Right + S(12), y + (filter.Height - count.Height) / 2);
                top.Height = filter.Bottom + S(10);
            };

            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.VirtualMode = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithAutoHeaderText;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = S(32);
            grid.RowTemplate.Height = S(28);
            grid.DefaultCellStyle.Padding = new Padding(S(4), 0, S(4), 0);
            grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            try { typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(grid, true, null); } catch { }
            foreach (string col in table.Columns)
            {
                var c = new DataGridViewTextBoxColumn();
                c.HeaderText = col;
                c.SortMode = DataGridViewColumnSortMode.Programmatic;
                grid.Columns.Add(c);
            }
            grid.CellValueNeeded += delegate(object s, DataGridViewCellValueEventArgs e)
            {
                if (e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
                var r = rows[e.RowIndex];
                e.Value = e.ColumnIndex < r.Length ? r[e.ColumnIndex] : "";
            };
            grid.ColumnHeaderMouseClick += delegate(object s, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || e.ColumnIndex < 0) return;
                if (sortColumn == e.ColumnIndex) sortAscending = !sortAscending;
                else { sortColumn = e.ColumnIndex; sortAscending = true; }
                foreach (DataGridViewColumn c in grid.Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
                grid.Columns[sortColumn].HeaderCell.SortGlyphDirection = sortAscending ? SortOrder.Ascending : SortOrder.Descending;
                Refill();
            };

            var buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.Padding = new Padding(S(12), S(8), S(12), S(8));
            var close = new Button();
            close.Text = "Cerrar";
            close.Size = new Size(S(110), S(34));
            close.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(close);
            CancelButton = close;

            Controls.Add(grid);
            Controls.Add(top);
            Controls.Add(buttons);
            Refill();
            Load += delegate
            {
                if (grid.Columns.Count > 0)
                {
                    grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
                    foreach (DataGridViewColumn c in grid.Columns) c.Width = Math.Max(S(70), Math.Min(S(420), c.Width + S(12)));
                    var last = grid.Columns[grid.Columns.Count - 1];
                    last.MinimumWidth = last.Width;
                    last.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                }
                filter.Focus();
            };
            Theme.Apply(this);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F)) { filter.Focus(); filter.SelectAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Refill()
        {
            string q = filter.Text.Trim();
            rows = new List<string[]>();
            foreach (var r in table.Rows)
            {
                if (q.Length == 0) { rows.Add(r); continue; }
                foreach (string c in r) if (c != null && c.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) { rows.Add(r); break; }
            }
            if (sortColumn >= 0) Sort();
            grid.RowCount = 0;
            grid.RowCount = rows.Count;
            grid.Invalidate();
            count.Text = table.Rows.Count == 0 ? (table.Notes.Count > 0 || table.Error != null ? "" : "Todavía no se actualizó nada con Actualizador.")
                       : q.Length == 0 ? Fmt.Plural(table.Rows.Count, "registro", "registros")
                       : rows.Count + " de " + table.Rows.Count;
        }

        void Sort()
        {
            int col = sortColumn;
            bool asc = sortAscending;
            var indexed = new List<KeyValuePair<int, string[]>>();
            for (int i = 0; i < rows.Count; i++) indexed.Add(new KeyValuePair<int, string[]>(i, rows[i]));
            indexed.Sort(delegate(KeyValuePair<int, string[]> a, KeyValuePair<int, string[]> b)
            {
                string x = col < a.Value.Length ? a.Value[col] : "", y = col < b.Value.Length ? b.Value[col] : "";
                int c = NaturalCompare(x, y);
                if (!asc) c = -c;
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            rows.Clear();
            foreach (var kv in indexed) rows.Add(kv.Value);
        }

        /// <summary>Compara tramos numéricos por valor ("10.2" &gt; "9.11", "PID 100" &gt; "PID 20").</summary>
        internal static int NaturalCompare(string a, string b)
        {
            a = a ?? ""; b = b ?? "";
            if (a.Length == 0 || b.Length == 0) return a.Length.CompareTo(b.Length);
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = string.Compare(a.Substring(i, 1), b.Substring(j, 1), StringComparison.CurrentCultureIgnoreCase);
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
