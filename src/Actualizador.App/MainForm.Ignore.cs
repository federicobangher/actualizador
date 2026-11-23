// Ventana principal: ignorar actualizaciones.
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.App
{
    sealed partial class MainForm
    {
        /// <summary>Filas elegidas (con el clic derecho se elige la fila bajo el mouse).</summary>
        List<UpdateCandidate> SelectedUpdates()
        {
            var list = new List<UpdateCandidate>();
            foreach (DataGridViewRow r in gridUpdates.SelectedRows)
            {
                var u = r.Tag as UpdateCandidate;
                if (u != null && !list.Contains(u)) list.Add(u);
            }
            if (list.Count == 0) { var c = CurrentUpdate(); if (c != null) list.Add(c); }
            return list;
        }

        void IgnoreSelected(bool allVersions)
        {
            if (installing || report == null) return;
            var rows = SelectedUpdates().FindAll(delegate(UpdateCandidate u) { return IgnoreList.CanIgnore(u) && (allVersions || !string.IsNullOrEmpty(u.AvailableVersion)); });
            if (rows.Count == 0) { ShowReason("Sólo se puede ignorar lo que ofrece una actualización, lo pendiente o lo que no se pudo verificar."); return; }
            var list = IgnoreList.Load();
            foreach (var u in rows) list.Add(IgnoreList.RuleFor(u, allVersions, DateTime.Now));
            if (!list.Save()) { MessageBox.Show(this, "No se pudo guardar la lista de ignorados (el detalle está en el registro).", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            ApplyIgnoreList(list);
            lblStatus.Text = (rows.Count == 1 ? "«" + Fmt.Truncate(rows[0].Name, 60) + "» ignorada" : Plural(rows.Count, "elemento ignorado", "elementos ignorados")) +
                             (allVersions ? " (todas las versiones)." : " (esta versión: si sale una más nueva, se vuelve a ofrecer).") +
                             " Se ve en la lista «Al día y sin verificar».";
        }

        void UnignoreSelected()
        {
            if (installing || report == null) return;
            var list = IgnoreList.Load();
            int removed = 0, rows = 0;
            foreach (var u in SelectedUpdates())
            {
                if (u.IgnoredBy == null) continue;
                rows++;
                removed += list.RemoveFor(u);
                list.Rules.Remove(u.IgnoredBy as IgnoreRule);
                IgnoreList.Unignore(u);
            }
            if (rows == 0) return;
            list.Save();
            ApplyIgnoreList(list);
            lblStatus.Text = Plural(rows, "elemento vuelve", "elementos vuelven") + " a ofrecerse.";
        }

        /// <summary>Aplica la lista a las filas actuales sin buscar de nuevo.</summary>
        void ApplyIgnoreList(IgnoreList list)
        {
            var r = report;
            if (r == null) return;
            lock (r.Updates) list.Apply(r.Updates);
            ShowUpdates();
        }
    }
}
