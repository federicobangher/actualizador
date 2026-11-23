// Una sola confirmación antes de instalar: qué se instala, qué programas abiertos se cierran, controladores forzados y opciones.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.App
{
    /// <summary>
    /// Antes había hasta cuatro cuadros seguidos (confirmar, programas abiertos, controladores forzados, resultado). Ahora todo
    /// en una ventana: la lista de lo que se instala y, sólo si hacen falta, las decisiones con su valor recomendado ya puesto.
    /// </summary>
    sealed class InstallConfirmForm : Form
    {
        readonly CheckBox chkClose = new CheckBox(), chkForce = new CheckBox(), chkRestore = new CheckBox(), chkInteractive = new CheckBox();

        public bool CloseRunning { get { return chkClose.Visible && chkClose.Checked; } }
        public bool ForceDrivers { get { return chkForce.Visible && chkForce.Checked; } }
        public bool RestorePoint { get { return chkRestore.Checked; } }
        public bool Interactive { get { return chkInteractive.Checked; } }

        public InstallConfirmForm(Form owner, List<UpdateCandidate> selected, List<KeyValuePair<UpdateCandidate, List<RunningProcess>>> running,
                                  List<UpdateCandidate> forced, int hidden, bool restore, bool admin, bool interactive, bool rounds)
        {
            float scale = 1f;
            try { using (var g = owner.CreateGraphics()) scale = g.DpiX / 96f; } catch { }
            Func<int, int> S = delegate(int px) { return (int)Math.Round(px * scale); };
            Text = "Actualizar " + Fmt.Plural(selected.Count, "elemento", "elementos");
            Font = owner.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(S(620), S(520));

            var layout = new FlowLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.FlowDirection = FlowDirection.TopDown;
            layout.WrapContents = false;
            layout.AutoScroll = true;
            layout.Padding = new Padding(S(16), S(14), S(16), S(8));
            int w = S(580);

            var title = new Label();
            title.Text = "Se va a actualizar:";
            title.Font = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold);
            title.AutoSize = true;
            layout.Controls.Add(title);

            var list = new ListBox();
            list.Width = w;
            list.Height = S(170);
            list.BorderStyle = BorderStyle.FixedSingle;
            list.IntegralHeight = false;
            foreach (var u in selected)
            {
                string how = u.ProviderName == SiteProvider.ProviderId ? "  (se abre su página de descarga)"
                           : forced.Contains(u) ? "  (controlador del Catálogo: ver abajo)"
                           : UpdateGroups.Of(u) == UpdateGroup.Opcional ? "  (opcional)" : "";
                list.Items.Add(u.Name + (string.IsNullOrEmpty(u.AvailableVersion) ? "" : "  →  " + u.AvailableVersion) + how);
            }
            layout.Controls.Add(list);

            var notes = new StringBuilder();
            if (hidden > 0) notes.Append(Fmt.Plural(hidden, "elemento marcado queda", "elementos marcados quedan")).Append(" afuera porque el filtro lo oculta. ");
            if (rounds) notes.Append("Al terminar se busca de nuevo y se instala lo recomendado que aparezca recién (hasta ").Append(UpdateManager.MaxExtraRounds).Append(" vueltas más). ");
            notes.Append("El equipo nunca se reinicia solo.");
            layout.Controls.Add(Note(notes.ToString(), w));

            // --- Programas abiertos
            if (running != null && running.Count > 0)
            {
                var names = new StringBuilder();
                for (int i = 0; i < running.Count && i < 6; i++)
                    names.Append(i == 0 ? "" : ", ").Append(Fmt.Truncate(running[i].Key.Name, 40)).Append(" (").Append(RunningPrograms.Names(running[i].Value)).Append(")");
                if (running.Count > 6) names.Append(" y ").Append(running.Count - 6).Append(" más");
                chkClose.Text = "Cerrar los programas abiertos justo antes de actualizarlos: " + names;
                chkClose.Checked = true;
                Section(layout, chkClose, w, "Si siguen abiertos, su instalador puede fallar (error 1603). A los que tienen ventana se les pide cerrarse como con la ✕: si preguntan si guardar, no se los fuerza.");
            }
            else chkClose.Visible = false;

            // --- Controladores que Windows sólo acepta forzados
            if (forced != null && forced.Count > 0)
            {
                chkForce.Text = "Instalar forzados " + Fmt.Plural(forced.Count, "controlador del Catálogo", "controladores del Catálogo") + " que reemplazan al del fabricante del equipo";
                chkForce.Checked = false;
                Section(layout, chkForce, w, "Windows prefiere el controlador que puso el fabricante del equipo para este modelo, aunque sea más viejo. Forzado lo reemplaza (puede perder ajustes propios del equipo); " +
                        "se vuelve atrás desde el Administrador de dispositivos › «Revertir al controlador anterior». Sin marcar, esos no se instalan y el resto sí.");
            }
            else chkForce.Visible = false;

            // --- Opciones
            chkRestore.Text = "Crear un punto de restauración antes";
            chkRestore.Checked = restore && admin;
            chkRestore.Enabled = admin;
            Section(layout, chkRestore, w, admin ? null : "Requiere ejecutar como administrador.");
            chkInteractive.Text = "Mostrar la ventana de cada instalador";
            chkInteractive.Checked = interactive;
            Section(layout, chkInteractive, w, "Dejala sin marcar: todo se instala en silencio, solo. Marcala sólo para reintentar uno que falló porque no se " +
                    "instala en silencio: cada instalador espera que lo completes a mano.");

            var buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Height = S(52);
            buttons.Padding = new Padding(S(12), S(10), S(12), S(8));
            Theme.SetRole(buttons, Theme.Role.Surface);
            var ok = new Button();
            ok.Text = "Actualizar";
            ok.DialogResult = DialogResult.OK;
            ok.Size = new Size(S(120), S(32));
            ok.Font = new Font(Font, FontStyle.Bold);
            Theme.SetRole(ok, Theme.Role.Good);
            var cancel = new Button();
            cancel.Text = "Cancelar";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Size = new Size(S(100), S(32));
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(layout);
            Controls.Add(buttons);
            Theme.Apply(this);
        }

        static Label Note(string text, int width)
        {
            var l = new Label();
            l.Text = text;
            Theme.SetRole(l, Theme.Role.Sub);
            l.MaximumSize = new Size(width, 0);
            l.AutoSize = true;
            l.Margin = new Padding(0, 6, 0, 6);
            return l;
        }

        static void Section(FlowLayoutPanel layout, CheckBox box, int width, string note)
        {
            box.MaximumSize = new Size(width, 0);
            box.AutoSize = true;
            box.Margin = new Padding(0, 10, 0, 0);
            layout.Controls.Add(box);
            if (!string.IsNullOrEmpty(note))
            {
                var l = Note(note, width - 20);
                l.Margin = new Padding(18, 2, 0, 0);
                layout.Controls.Add(l);
            }
        }
    }
}
