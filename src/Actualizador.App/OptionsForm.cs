using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Updates;

namespace Actualizador.App
{
    sealed class OptionsForm : Form
    {
        /// <summary>Campo numérico que muestra dos cifras (09:05, no 9:5).</summary>
        sealed class TwoDigits : NumericUpDown
        {
            protected override void UpdateEditText() { Text = ((int)Value).ToString("00"); }
        }

        sealed class RuleItem
        {
            public IgnoreRule Rule;
            public override string ToString()
            {
                return Rule.Describe() + (string.IsNullOrEmpty(Rule.Kind) ? "" : " · " + Rule.Kind) + (Rule.Added > DateTime.MinValue ? " · desde el " + Fmt.Date(Rule.Added.Date) : "");
            }
        }

        readonly GuiSettings settings;
        readonly CheckBox vendors = new CheckBox();
        readonly CheckBox betas = new CheckBox();
        readonly CheckBox installWinget = new CheckBox();
        readonly ComboBox theme = new ComboBox();
        // Revisión programada
        readonly CheckBox schedule = new CheckBox();
        readonly ComboBox frequency = new ComboBox();
        readonly ComboBox weekday = new ComboBox();
        // Hora y minutos en dos campos numéricos (el selector de hora de Windows no toma los colores del tema oscuro).
        readonly NumericUpDown hour = new TwoDigits(), minute = new TwoDigits();
        readonly Label scheduleState = new Label();
        bool scheduleWas;
        ReviewSchedule scheduleBefore;
        // Ignorados
        readonly ListBox ignored = new ListBox();
        IgnoreList ignoreList;
        bool ignoreChanged;
        // Versiones nuevas
        readonly TextBox versionSource = new TextBox();
        readonly Label versionState = new Label();
        // Catálogo de fuentes oficiales
        readonly TextBox catalogUrl = new TextBox();
        // Carpeta de versiones de los programas propios (2.0.8)
        readonly TextBox ownFolder = new TextBox();
        readonly Label catalogState = new Label();

        /// <summary>La lista de ignorados cambió: la ventana principal la vuelve a aplicar.</summary>
        public bool IgnoreChanged { get { return ignoreChanged; } }

        static readonly DayOfWeek[] Week = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };

        /// <summary>2.0.33: el usuario pidió instalar la versión nueva desde Opciones (la instala la ventana principal al cerrar).</summary>
        public SelfUpdate.Result InstallRequested;

        public OptionsForm(GuiSettings settings)
        {
            this.settings = settings;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = "Opciones";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(760, 660);
            MinimumSize = new Size(520, 520);

            var body = BuildUpdatesTab();

            var buttons = Flow();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Bottom;
            buttons.Padding = new Padding(8);
            var cancel = MakeButton("Cancelar", delegate { DialogResult = DialogResult.Cancel; });
            var ok = MakeButton("Aceptar", delegate { Accept(); });
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(body);
            Controls.Add(buttons);
            Load += delegate { RefreshScheduleState(); };
            Theme.Apply(this);
        }

        Control BuildUpdatesTab()
        {
            // De arriba hacia abajo, con desplazamiento si la ventana es chica. Cada grupo ocupa el ancho disponible.
            var outer = new FlowLayoutPanel();
            outer.Dock = DockStyle.Fill;
            outer.FlowDirection = FlowDirection.TopDown;
            outer.WrapContents = false;
            outer.AutoScroll = true;
            outer.Padding = new Padding(10);

            // 2.0: lo que fija la política de la empresa (GPO o politica.json) se ve, pero no se puede cambiar.
            var policy = Policy.Current;
            Label lblPolicy = null;
            if (policy.LockSettings || !policy.AllowInstallWinget || policy.Origin != "ninguna")
            {
                lblPolicy = new Label();
                lblPolicy.AutoSize = true;
                lblPolicy.ForeColor = Theme.Info;
                lblPolicy.Margin = new Padding(3, 0, 3, 8);
                lblPolicy.Text = "Política de la empresa: " + policy.Origin + "." + (policy.LockSettings ? " Las fuentes y el catálogo los fija el administrador." : "");
                outer.Controls.Add(lblPolicy);
            }

            // --- Apariencia (2.0.8)
            var themeRow = Flow();
            themeRow.Margin = new Padding(0, 0, 0, 8);
            var lblTheme = new Label(); lblTheme.Text = "Tema:"; lblTheme.AutoSize = true; lblTheme.Margin = new Padding(3, 6, 6, 0);
            theme.DropDownStyle = ComboBoxStyle.DropDownList; theme.Width = 170;
            theme.Items.AddRange(new object[] { "Según Windows", "Claro", "Oscuro" });
            var mode = Theme.Parse(settings.Theme);
            theme.SelectedIndex = mode == ThemeMode.Claro ? 1 : mode == ThemeMode.Oscuro ? 2 : 0;
            themeRow.Controls.Add(lblTheme); themeRow.Controls.Add(theme);
            outer.Controls.Add(themeRow);

            vendors.Text = "Consultar los sitios de los fabricantes y el Catálogo de Microsoft Update";
            vendors.AutoSize = true; vendors.Checked = settings.QueryVendors;
            new ToolTip().SetToolTip(vendors, "Office, Vivaldi, Adobe, Samsung, GitHub y los controladores del Catálogo de Microsoft Update. Desmarcala en redes que sólo permiten winget, Windows Update y Microsoft Store.");
            outer.Controls.Add(vendors);
            betas.Text = "Ofrecer también las betas que publica el fabricante (por ejemplo, Cura 5.14.0-beta)";
            betas.AutoSize = true; betas.Checked = settings.OfferBetas;
            betas.Margin = new Padding(20, 4, 3, 0);
            new ToolTip().SetToolTip(betas, "Las betas pueden tener errores. Quedan entre las opcionales: nunca se marcan solas ni entran en «Actualizar todo», " +
                "y al actualizarlas se abre la página del fabricante para descargarlas. Desmarcada, una beta sólo se menciona en el detalle del programa.");
            outer.Controls.Add(betas);

            // --- winget si falta y actualizar todo de una vez
            installWinget.Text = "Si winget no está instalado, instalarlo automáticamente (como administrador)";
            installWinget.AutoSize = true; installWinget.Checked = settings.InstallWinget;
            installWinget.Margin = new Padding(3, 8, 3, 0);
            new ToolTip().SetToolTip(installWinget, "Sin winget no se pueden comparar ni actualizar los programas (pasa en Windows LTSC o sin Microsoft Store). " +
                "Se baja de github.com/microsoft/winget-cli, la fuente oficial de Microsoft, y se instala sólo si coincide el SHA-256 publicado.");
            outer.Controls.Add(installWinget);
            // las búsquedas por nombre en winget y las del Catálogo de Microsoft Update se guardan 12 horas.
            var forget = MakeButton("Olvidar las consultas guardadas", delegate
            {
                int n = QueryCache.Clear();
                RecentPages.Clear();
                MessageBox.Show(this, n == 0 ? "No había consultas guardadas." : "Listo: la próxima búsqueda consulta todo de nuevo (tarda más).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            new ToolTip().SetToolTip(forget, "Para que la búsqueda sea más rápida, lo que winget y el Catálogo de Microsoft Update contestan se guarda 12 horas. " +
                "Con esto se vuelve a consultar todo en la próxima búsqueda.");
            outer.Controls.Add(forget);
            if (policy.LockSettings || !policy.IsSourceAllowed("github")) { betas.Checked = false; betas.Enabled = false; }
            if (policy.LockSettings) { vendors.Enabled = false; installWinget.Enabled = false; catalogUrl.ReadOnly = true; }
            if (!policy.AllowInstallWinget) { installWinget.Checked = false; installWinget.Enabled = false; }

            // --- Tus programas (2.0.8)
            var own = Stack();
            var lblOwn = new Label();
            lblOwn.Text = "Para tus propios programas, que no figuran en ningún catálogo: la carpeta donde dejás sus versiones nuevas. Se reconocen por el nombre " +
                          "del archivo: «RelojMonitor_1.9.0.7z», «Virtual Pet 2.0.2 Setup.exe», «Rios_y_Clima_del_Chaco-1.1.zip» (también en sus subcarpetas). " +
                          "Si hay una más nueva que la instalada, «Actualizar» abre la carpeta con ese archivo marcado.";
            lblOwn.AutoSize = true;
            ownFolder.Width = 400; ownFolder.Text = settings.OwnBuildsFolder ?? "";
            var ownRow = Flow();
            ownRow.Controls.Add(ownFolder);
            ownRow.Controls.Add(MakeButton("Elegir…", delegate
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "Carpeta con las versiones de tus programas";
                    if (System.IO.Directory.Exists(ownFolder.Text.Trim())) dlg.SelectedPath = ownFolder.Text.Trim();
                    if (dlg.ShowDialog(this) == DialogResult.OK) ownFolder.Text = dlg.SelectedPath;
                }
            }));
            own.Controls.Add(lblOwn); own.Controls.Add(ownRow);
            var grpOwn = Group("Tus programas", own);
            outer.Controls.Add(grpOwn);

            // --- Revisión programada
            var sched = Stack();
            schedule.Text = "Buscar actualizaciones automáticamente y avisar si hay (no instala nada)";
            schedule.AutoSize = true;
            var row = Flow();
            frequency.DropDownStyle = ComboBoxStyle.DropDownList; frequency.Width = 170;
            frequency.Items.AddRange(new object[] { "Todos los días", "Una vez por semana" });
            weekday.DropDownStyle = ComboBoxStyle.DropDownList; weekday.Width = 120;
            weekday.Items.AddRange(new object[] { "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo" });
            hour.Minimum = 0; hour.Maximum = 23; hour.Width = 52;
            minute.Minimum = 0; minute.Maximum = 59; minute.Width = 52;
            var lblA = new Label(); lblA.Text = "a las"; lblA.AutoSize = true; lblA.Margin = new Padding(6, 6, 6, 0);
            var lblColon = new Label(); lblColon.Text = ":"; lblColon.AutoSize = true; lblColon.Margin = new Padding(0, 6, 0, 0);
            row.Controls.Add(frequency); row.Controls.Add(weekday); row.Controls.Add(lblA); row.Controls.Add(hour); row.Controls.Add(lblColon); row.Controls.Add(minute);
            scheduleState.AutoSize = true; scheduleState.ForeColor = SystemColors.GrayText; scheduleState.Margin = new Padding(3, 6, 3, 0);
            sched.Controls.Add(schedule); sched.Controls.Add(row); sched.Controls.Add(scheduleState); sched.Controls.Add(MakeButton("Probar el aviso", delegate { TestNotice(); }));
            var grpSched = Group("Revisión programada", sched);
            outer.Controls.Add(grpSched);

            var prev = LoadScheduleGuess();
            frequency.SelectedIndex = prev.Frequency == ReviewFrequency.Diaria ? 0 : 1;
            weekday.SelectedIndex = Array.IndexOf(Week, prev.Day);
            hour.Value = Math.Max(0, Math.Min(23, prev.Hour));
            minute.Value = Math.Max(0, Math.Min(59, prev.Minute));
            frequency.SelectedIndexChanged += delegate { weekday.Enabled = schedule.Checked && frequency.SelectedIndex == 1; };
            schedule.CheckedChanged += delegate { frequency.Enabled = hour.Enabled = minute.Enabled = schedule.Checked; weekday.Enabled = schedule.Checked && frequency.SelectedIndex == 1; };

            // --- Ignorados
            var ign = Stack();
            ignored.IntegralHeight = false; ignored.SelectionMode = SelectionMode.MultiExtended; ignored.Height = 110; ignored.Width = 560;
            ignoreList = IgnoreList.Load();
            FillIgnored();
            var ignRow = Flow();
            ignRow.Controls.Add(MakeButton("Dejar de ignorar las elegidas", delegate
            {
                var del = new List<IgnoreRule>();
                foreach (object o in ignored.SelectedItems) { var ri = o as RuleItem; if (ri != null) del.Add(ri.Rule); }
                foreach (var r in del) ignoreList.Rules.Remove(r);
                if (del.Count > 0) { ignoreChanged = true; FillIgnored(); }
            }));
            ignRow.Controls.Add(MakeButton("Vaciar la lista", delegate
            {
                if (ignoreList.Rules.Count == 0) return;
                ignoreList.Rules.Clear(); ignoreChanged = true; FillIgnored();
            }));
            ign.Controls.Add(ignored); ign.Controls.Add(ignRow);
            var grpIgn = Group("Actualizaciones ignoradas", ign);
            outer.Controls.Add(grpIgn);

            // --- Versiones nuevas de Actualizador
            var ver = Stack();
            var lblVer = new Label();
            lblVer.Text = "Dónde se publican: un proyecto de GitHub («propietario/repositorio» o su dirección) o una carpeta (D:\\Entregas, \\\\servidor\\Actualizador). " +
                          "Vacío: " + SelfUpdate.DefaultSource + " (lo publica Publicar.cmd). Desde GitHub, la versión nueva se instala sola al abrir Actualizador; " +
                          "desde una carpeta, sólo avisa.";
            lblVer.AutoSize = true;
            versionSource.Width = 400; versionSource.Text = settings.VersionSource ?? "";
            var verRow = Flow();
            verRow.Controls.Add(versionSource);
            // 2.0.33 (pedido del usuario: «en Opciones, una opción de buscar actualización del programa»): busca ya y, si hay una versión
            // nueva que se puede instalar sola, ofrece instalarla.
            verRow.Controls.Add(MakeButton("Buscar actualización de Actualizador", delegate { CheckVersionNow(); }));
            versionState.AutoSize = true; versionState.ForeColor = SystemColors.GrayText;
            ver.Controls.Add(lblVer); ver.Controls.Add(verRow); ver.Controls.Add(versionState);
            var grpVer = Group("Versiones nuevas de Actualizador", ver);
            outer.Controls.Add(grpVer);

            // --- Catálogo de fuentes oficiales
            var cat = Stack();
            var lblCat = new Label();
            lblCat.Text = "Para los programas que winget no conoce: de qué página oficial sale su versión y de dónde se baja su instalador. Se lee «" +
                          SourceCatalog.FileName + "» de la carpeta del programa y de " + AppInfo.DataDirectory + ". Si escribís una dirección HTTPS (por ejemplo " +
                          "el archivo «raw» de tu GitHub), se baja de ahí cada 12 horas. Un instalador sólo se ejecuta si lo firma el mismo fabricante que el programa instalado.";
            lblCat.AutoSize = true;
            catalogUrl.Width = 400; catalogUrl.Text = settings.SourcesCatalogUrl ?? "";
            var catRow = Flow();
            catRow.Controls.Add(catalogUrl);
            catRow.Controls.Add(MakeButton("Abrir la carpeta", delegate
            {
                try
                {
                    SafePaths.EnsureDataDirectory(AppInfo.DataDirectory);
                    // 2.0.41 (relevamiento): antes «explorer.exe» por nombre suelto desde el proceso elevado: se busca primero en la carpeta del
                    // programa (en la portable la puede escribir un usuario común) y el Explorador quedaba como administrador.
                    ProcessRunner.OpenForUser(AppInfo.DataDirectory);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }));
            catalogState.AutoSize = true; catalogState.ForeColor = SystemColors.GrayText;
            int custom = VendorCatalog.All.Count - VendorCatalog.BuiltIn.Count;
            catalogState.Text = Fmt.Plural(VendorCatalog.BuiltIn.Count, "fuente incorporada", "fuentes incorporadas") + " y " +
                                (custom == 0 ? "ninguna del catálogo." : Fmt.Plural(custom, "del catálogo", "del catálogo") + ".");
            cat.Controls.Add(lblCat); cat.Controls.Add(catRow); cat.Controls.Add(catalogState);
            var grpCat = Group("Catálogo de fuentes oficiales", cat);
            outer.Controls.Add(grpCat);

            // Ancho: el de la pestaña (los textos largos se cortan en renglones).
            EventHandler fit = delegate
            {
                int w = Math.Max(300, outer.ClientSize.Width - outer.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
                foreach (var l in new[] { scheduleState, lblVer, versionState, lblCat, catalogState, lblOwn }) l.MaximumSize = new Size(w - 30, 0);
                if (lblPolicy != null) lblPolicy.MaximumSize = new Size(w - 10, 0);
                ignored.Width = w - 30;
                // El alto de cada grupo se calcula: un GroupBox que se ajusta solo a un contenido que también se ajusta
                // solo no siempre llega a una medida estable.
                foreach (var g in new[] { grpOwn, grpSched, grpIgn, grpVer, grpCat })
                {
                    var content = g.Controls[0];
                    g.Width = w;
                    g.Height = content.GetPreferredSize(new Size(w - g.Padding.Horizontal - 6, 0)).Height + g.Padding.Vertical + 22;
                }
            };
            outer.Resize += fit;
            outer.HandleCreated += fit;
            return outer;
        }

        static FlowLayoutPanel Stack()
        {
            var f = new FlowLayoutPanel();
            f.FlowDirection = FlowDirection.TopDown;
            f.WrapContents = false;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.Dock = DockStyle.Fill;
            return f;
        }

        static GroupBox Group(string text, Control content)
        {
            var g = new GroupBox();
            g.Text = text;
            g.Padding = new Padding(8, 6, 8, 8);
            g.Size = new Size(560, 120);
            g.Margin = new Padding(0, 12, 0, 0);
            g.Controls.Add(content);
            return g;
        }

        void FillIgnored()
        {
            ignored.BeginUpdate();
            ignored.Items.Clear();
            foreach (var r in ignoreList.Rules) ignored.Items.Add(new RuleItem { Rule = r });
            if (ignoreList.Rules.Count == 0) ignored.Items.Add("(nada ignorado: en Actualizaciones, clic derecho sobre una fila › «Ignorar…»)");
            ignored.EndUpdate();
        }

        static ReviewSchedule LoadScheduleGuess() { return ScheduledReview.LastChoice(); }

        ReviewSchedule CurrentChoice()
        {
            var s = new ReviewSchedule();
            s.Frequency = frequency.SelectedIndex == 0 ? ReviewFrequency.Diaria : ReviewFrequency.Semanal;
            s.Day = Week[Math.Max(0, weekday.SelectedIndex)];
            s.Hour = (int)hour.Value; s.Minute = (int)minute.Value;
            return s;
        }

        void RefreshScheduleState()
        {
            string why = ScheduledReview.Unavailable(OsInfo.Detect());
            if (why != null)
            {
                schedule.Enabled = frequency.Enabled = weekday.Enabled = hour.Enabled = minute.Enabled = false;
                scheduleState.Text = why;
                return;
            }
            var st = ScheduledReview.Query();
            scheduleWas = st.Exists;
            scheduleBefore = CurrentChoice();
            schedule.Checked = st.Exists;
            frequency.Enabled = hour.Enabled = minute.Enabled = st.Exists;
            weekday.Enabled = st.Exists && frequency.SelectedIndex == 1;
            string last = "";
            string sig; DateTime? notified, run; int total;
            ReviewOutcome.ReadLast(ReviewOutcome.StatePath, out sig, out notified, out run, out total);
            if (run.HasValue) last = " Última revisión: " + Fmt.Date(run.Value) + " (" + Fmt.Plural(total, "actualización disponible", "actualizaciones disponibles") + ").";
            if (st.Exists && !string.IsNullOrEmpty(st.Command) && !string.Equals(st.Command.Trim('"'), ScheduledReview.AvisoPath, StringComparison.OrdinalIgnoreCase))
                last += " Ojo: la tarea ejecuta " + st.Command + ", no el aviso de esta carpeta; al aceptar se actualiza.";
            scheduleState.Text = st.Describe() + last + " Corre con tu cuenta, sin privilegios de administrador.";
        }

        void TestNotice()
        {
            try
            {
                if (!System.IO.File.Exists(ScheduledReview.AvisoPath)) { MessageBox.Show(this, "Falta " + ScheduledReview.AvisoPath + ".", Text); return; }
                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ScheduledReview.AvisoPath, "--probar-aviso") { UseShellExecute = false })) { }
            }
            catch (Exception ex) { MessageBox.Show(this, "No se pudo abrir el aviso: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void CheckVersionNow()
        {
            string src = SelfUpdate.EffectiveSource(versionSource.Text);
            string v;
            if (SelfUpdate.Classify(src, out v) == SelfUpdate.SourceKind.None)
            {
                versionState.Text = src.Length == 0 ? "No hay una fuente configurada." : "No se reconoce: escribí «propietario/repositorio» de GitHub, su dirección, o una carpeta (D:\\Entregas, \\\\servidor\\carpeta).";
                return;
            }
            versionState.Text = "Consultando…";
            var form = this;
            var t = new Thread(delegate()
            {
                var r = SelfUpdate.Check(src, AppInfo.Version);
                SelfUpdate.Save(SelfUpdate.StatePath, r, src);
                try
                {
                    form.BeginInvoke((Action)delegate
                    {
                        versionState.Text = r.Error != null ? "No se pudo consultar: " + r.Error
                            : r.Newer ? SelfUpdate.Message(r) : "Estás en la última versión (" + AppInfo.Version + "; la más nueva publicada es la " + r.Latest + ").";
                        if (r.Error == null && r.CanInstall && SelfInstall.CannotInstall(AppInfo.IsPortable, OsInfo.IsAdministrator()) == null)
                        {
                            versionState.Text = "Hay una versión nueva: la " + r.Latest + " (esta es la " + AppInfo.Version + ").";
                            if (MessageBox.Show(form, "Hay una versión nueva de Actualizador: la " + r.Latest + " (esta es la " + AppInfo.Version + ").\n\n" +
                                                "Se instala ahora: se guardan las opciones, Actualizador se cierra y se vuelve a abrir solo en menos de un minuto. ¿Instalar?",
                                                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                            {
                                InstallRequested = r;
                                Accept();
                            }
                        }
                    });
                }
                catch (InvalidOperationException) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        static FlowLayoutPanel Flow()
        {
            var f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = true;
            f.Margin = new Padding(0, 6, 0, 0);
            return f;
        }

        static Button MakeButton(string text, EventHandler click)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(8, 2, 8, 2);
            b.Click += click;
            return b;
        }

        void Accept()
        {
            string v;
            string src = versionSource.Text.Trim();
            if (src.Length > 0 && SelfUpdate.Classify(src, out v) == SelfUpdate.SourceKind.None)
            {
                MessageBox.Show(this, "«Dónde se publican las versiones nuevas» no se reconoce: escribí «propietario/repositorio» de GitHub, su dirección, una carpeta, o dejalo vacío.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string ownPath = ownFolder.Text.Trim().Trim('"');
            if (ownPath.Length > 0 && !System.IO.Directory.Exists(ownPath))
            {
                MessageBox.Show(this, "La carpeta de versiones de tus programas no existe: «" + ownPath + "» (o dejala vacía).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string catalog = catalogUrl.Text.Trim();
            Uri catUri;
            if (catalog.Length > 0 && (!Uri.TryCreate(catalog, UriKind.Absolute, out catUri) || catUri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show(this, "La dirección del catálogo de fuentes oficiales tiene que empezar con https:// (o dejala vacía).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            settings.QueryVendors = vendors.Checked;
            settings.OfferBetas = betas.Checked && betas.Enabled;
            settings.InstallWinget = installWinget.Checked;
            settings.Theme = theme.SelectedIndex == 1 ? "claro" : theme.SelectedIndex == 2 ? "oscuro" : "sistema";
            settings.VersionSource = src;
            settings.SourcesCatalogUrl = catalog;
            settings.OwnBuildsFolder = ownPath;
            settings.Save();

            if (ignoreChanged && !ignoreList.Save())
                MessageBox.Show(this, "No se pudo guardar la lista de ignorados (el detalle está en el registro).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

            // Revisión programada: sólo se toca el Programador de tareas si cambió algo.
            if (schedule.Enabled)
            {
                var now = CurrentChoice();
                bool changed = schedule.Checked != scheduleWas || (schedule.Checked && scheduleBefore != null &&
                               (now.Frequency != scheduleBefore.Frequency || now.Day != scheduleBefore.Day || now.Hour != scheduleBefore.Hour || now.Minute != scheduleBefore.Minute));
                if (changed)
                {
                    string err = schedule.Checked ? ScheduledReview.Register(now) : ScheduledReview.Unregister();
                    if (err != null)
                    {
                        MessageBox.Show(this, (schedule.Checked ? "No se pudo programar la revisión: " : "No se pudo desactivar la revisión programada: ") + err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        RefreshScheduleState();
                        return;
                    }
                }
            }
            DialogResult = DialogResult.OK;
        }
    }
}
