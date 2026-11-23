// Ventana principal: historial y registro.
using System;
using System.Windows.Forms;
using Actualizador.Core;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.App
{
    sealed partial class MainForm
    {
        /// <summary>2.0.8: el historial se abre en su propia ventana (antes reemplazaba la lista de actualizaciones).</summary>
        void ShowHistory()
        {
            TableData s;
            try { s = UpdateHistory.ToSection(UpdateHistory.Load()); }
            catch (Exception ex) { Log.Warn("Historial: " + ex.Message); s = new TableData("historial_actualizaciones", "Historial de actualizaciones"); s.Error = ex.Message; }
            using (var f = new HistoryForm(this, s)) f.ShowDialog(this);
        }

        /// <summary>Muestra u oculta el registro de abajo.</summary>
        void ToggleLog()
        {
            outerSplit.Panel2Collapsed = !outerSplit.Panel2Collapsed;
            if (!outerSplit.Panel2Collapsed)
            {
                try { outerSplit.SplitterDistance = Math.Max(S(200), outerSplit.Height - S(180)); } catch { }
                try { txtLog.SelectionStart = txtLog.TextLength; txtLog.ScrollToCaret(); } catch { }
            }
        }
    }
}
