// Ventana principal: página oficial de un programa, indicada por el usuario.
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Actualizador.Core;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.App
{
    sealed partial class MainForm
    {
        /// <summary>¿Se le puede indicar la página oficial a esta fila? Programas con versión conocida.</summary>
        static bool CanSetSource(UpdateCandidate u)
        {
            return u != null && u.Kind == UpdateKind.Programa && u.Status != UpdateStatus.Instalando && VersionUtil.Parse(u.InstalledVersion) != null;
        }

        /// <summary>
        /// Clic derecho › «Indicar la página oficial…»: la dirección donde el fabricante publica la versión (descargas o
        /// novedades). Se guarda en fuentes.json, se lee ya mismo y se usa en cada búsqueda.
        /// </summary>
        void SetOfficialPage()
        {
            var u = CurrentUpdate();
            if (installing || !CanSetSource(u)) { ShowReason("Sólo se puede indicar la página de un programa con versión conocida."); return; }
            // 2.0.15: lo que ya se compara con una fuente oficial incorporada (FortiClient VPN con el instalador que enlaza la página de
            // descargas de Fortinet) no se reemplaza por una lectura automática de otra página, que puede encontrar el número de otro producto.
            var vendorPlan = u.ProviderTag as VendorPlan;
            if (vendorPlan != null && vendorPlan.Check != null && !vendorPlan.Check.FromCatalogFile && u.Status != UpdateStatus.Error)
            {
                MessageBox.Show(this, "«" + Fmt.Truncate(u.Name, 60) + "» ya se compara con la fuente oficial de su fabricante (" + vendorPlan.Check.Vendor + "): " +
                                (string.IsNullOrEmpty(u.Detail) ? "" : Fmt.Truncate(u.Detail, 300) + " ") +
                                "No hace falta indicar una página.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var sources = SourceList.Load();
            string current = sources.Find(u.Name) ?? Fmt.WebUrl(u.InfoUrl) ?? "";
            string url = AskUrl(u.Name, current);
            if (url == null) return;
            if (url.Trim().Length == 0)
            {
                sources.Set(u.Name, null);
                sources.Save();
                lblStatus.Text = "Se quitó la página indicada para «" + Fmt.Truncate(u.Name, 60) + "».";
                return;
            }
            if (!sources.Set(u.Name, url)) { MessageBox.Show(this, "No es la dirección de una página web (tiene que empezar con https:// o http://).", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (!sources.Save()) MessageBox.Show(this, "No se pudo guardar la página (el detalle está en el registro).", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            string name = u.Name, installed = u.InstalledVersion, saved = sources.Find(u.Name), publisher = u.Publisher ?? "";
            var ctx = UsableContext();
            lblStatus.Text = "Leyendo la versión de «" + Fmt.Truncate(name, 50) + "» en " + new Uri(saved).Host + "…";
            var t = new Thread(delegate()
            {
                string error;
                var reading = SiteVersions.ReadPage(saved, name, publisher, installed, out error);
                InstalledProgram prog = null;
                try { foreach (var x in InventoryLoader.EnsurePrograms(ctx)) if (x.Name != null && x.Name.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) { prog = x; break; } }
                catch (Exception ex) { Log.Warn("Programa de la fila: " + ex.Message); }
                UiInvoke(delegate
                {
                    if (reading == null)
                    {
                        lblStatus.Text = "«" + Fmt.Truncate(name, 50) + "»: " + error + ".";
                        MessageBox.Show(this, "Se guardó la página, pero no se pudo leer la versión: " + error + ".", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    var p = prog;
                    if (p == null)
                    {
                        p = new InstalledProgram();
                        p.Name = name; p.Version = installed; p.Publisher = publisher; p.Scope = "Equipo";
                        p.InstallLocation = p.UninstallString = p.DisplayIcon = "";
                    }
                    SiteVersions.Apply(prog != null ? ctx : null, u, p, reading, false);
                    RefreshUpdateRow(u);
                    ShowUpdateDetail(u);
                    lblStatus.Text = "«" + Fmt.Truncate(name, 50) + "»: " + (u.Status == UpdateStatus.Disponible ? "su página publica la " + reading.Latest + " (tenés la " + installed + ")." : "al día según su página (" + reading.Latest + ").") +
                                     " Se usa también en las próximas búsquedas.";
                });
            });
            t.IsBackground = true;
            t.Name = "pagina-oficial";
            t.Start();
        }

        /// <summary>Pide la dirección. null si se canceló; "" para quitarla.</summary>
        string AskUrl(string program, string current)
        {
            using (var f = new Form())
            {
                f.Text = "Página oficial de " + Fmt.Truncate(program, 50);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.ClientSize = new Size(S(560), S(150));
                var l = new Label();
                l.Text = "Dirección de la página donde el fabricante publica la última versión (descargas o novedades). " +
                         "Actualizador la lee en cada búsqueda y compara con la instalada. Vacía: se quita.";
                l.SetBounds(S(12), S(10), S(536), S(50));
                var box = new TextBox();
                box.Text = current ?? "";
                box.SetBounds(S(12), S(66), S(536), S(24));
                var ok = new Button(); ok.Text = "Guardar"; ok.DialogResult = DialogResult.OK; ok.SetBounds(S(372), S(108), S(85), S(28));
                var cancel = new Button(); cancel.Text = "Cancelar"; cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(S(463), S(108), S(85), S(28));
                f.Controls.AddRange(new Control[] { l, box, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                Theme.Apply(f);
                return f.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }
    }
}
