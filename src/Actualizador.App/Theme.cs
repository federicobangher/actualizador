// 2.0.8: tema claro / oscuro de todas las ventanas (principal, opciones, confirmación, historial).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Actualizador.Core;

namespace Actualizador.App
{
    /// <summary>Lo que se elige en Opciones: seguir a Windows, claro u oscuro.</summary>
    enum ThemeMode { Sistema, Claro, Oscuro }

    /// <summary>
    /// Colores de la interfaz y cómo se aplican a cada tipo de control. Un solo lugar: cambiar de tema es volver a recorrer
    /// las ventanas abiertas con <see cref="Apply"/>. Los controles que tienen un papel especial (botón principal, texto
    /// secundario, superficie) lo declaran con <see cref="SetRole"/> al armarse.
    /// </summary>
    static class Theme
    {
        public enum Role { None, Surface, Sub, Primary, Good, Secondary, Tab, Danger }

        public static bool Dark { get; private set; }
        public static ThemeMode Mode { get; private set; }

        public static Color Back, Surface, SurfaceAlt, Border, Text, SubText, Muted, Input;
        public static Color Accent, AccentHover, Good, GoodHover, GoodText, Warn, Bad, Info;
        public static Color Selection, SelectionText, HeaderBack, Disabled, DisabledText, CheckBorder;

        /// <summary>Se avisa a las ventanas abiertas para que se vuelvan a pintar.</summary>
        public static event Action Changed;

        static readonly Dictionary<Control, Role> roles = new Dictionary<Control, Role>();

        static Theme() { Set(ThemeMode.Claro); }

        public static ThemeMode Parse(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "claro": return ThemeMode.Claro;
                case "oscuro": return ThemeMode.Oscuro;
                default: return ThemeMode.Sistema;
            }
        }

        public static string ToText(ThemeMode m) { return m == ThemeMode.Claro ? "claro" : m == ThemeMode.Oscuro ? "oscuro" : "sistema"; }

        /// <summary>¿Windows usa el modo oscuro para las aplicaciones? (Configuración › Personalización › Colores).</summary>
        public static bool SystemPrefersDark()
        {
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        // 2.0.13: sin azul ni amarillo (pedido del usuario): el color de acento, los enlaces, lo opcional y la fila elegida son violetas;
        // los avisos, naranja.
        public static void Set(ThemeMode mode)
        {
            Mode = mode;
            Dark = mode == ThemeMode.Oscuro || (mode == ThemeMode.Sistema && SystemPrefersDark());
            if (Dark)
            {
                Back = Rgb(0x17, 0x18, 0x1C); Surface = Rgb(0x1F, 0x21, 0x26); SurfaceAlt = Rgb(0x25, 0x27, 0x2D);
                Border = Rgb(0x36, 0x39, 0x41); Text = Rgb(0xE8, 0xEA, 0xED); SubText = Rgb(0xA3, 0xA8, 0xB3); Muted = Rgb(0x6E, 0x74, 0x80);
                Input = Rgb(0x2A, 0x2D, 0x34);
                Accent = Rgb(0x7C, 0x3A, 0xED); AccentHover = Rgb(0x6D, 0x28, 0xD9);
                Good = Rgb(0x16, 0xA3, 0x4A); GoodHover = Rgb(0x15, 0x80, 0x3D); GoodText = Rgb(0x4A, 0xDE, 0x80);
                Warn = Rgb(0xFB, 0x92, 0x3C); Bad = Rgb(0xF8, 0x71, 0x71); Info = Rgb(0xA7, 0x8B, 0xFA);
                Selection = Rgb(0x3B, 0x2A, 0x5C); SelectionText = Rgb(0xF4, 0xF4, 0xF5); HeaderBack = Rgb(0x25, 0x27, 0x2D);
                Disabled = Rgb(0x2C, 0x2F, 0x36); DisabledText = Rgb(0x6E, 0x74, 0x80); CheckBorder = Rgb(0x7A, 0x80, 0x8C);
            }
            else
            {
                Back = Rgb(0xF3, 0xF4, 0xF6); Surface = Color.White; SurfaceAlt = Rgb(0xF9, 0xFA, 0xFB);
                Border = Rgb(0xE2, 0xE5, 0xEA); Text = Rgb(0x11, 0x18, 0x27); SubText = Rgb(0x5B, 0x64, 0x72); Muted = Rgb(0x9C, 0xA3, 0xAF);
                Input = Color.White;
                Accent = Rgb(0x6D, 0x28, 0xD9); AccentHover = Rgb(0x5B, 0x21, 0xB6);
                Good = Rgb(0x16, 0x80, 0x3D); GoodHover = Rgb(0x14, 0x6C, 0x34); GoodText = Rgb(0x15, 0x80, 0x3D);
                Warn = Rgb(0xC2, 0x41, 0x0C); Bad = Rgb(0xB9, 0x1C, 0x1C); Info = Rgb(0x6D, 0x28, 0xD9);
                Selection = Rgb(0xED, 0xE9, 0xFE); SelectionText = Rgb(0x11, 0x18, 0x27); HeaderBack = Rgb(0xF3, 0xF4, 0xF6);
                Disabled = Rgb(0xE5, 0xE7, 0xEB); DisabledText = Rgb(0x9C, 0xA3, 0xAF); CheckBorder = Rgb(0x8A, 0x93, 0xA0);
            }
            var h = Changed;
            if (h != null) { try { h(); } catch (Exception ex) { Log.Warn("Tema: " + ex.Message); } }
        }

        static Color Rgb(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        public static void SetRole(Control c, Role r)
        {
            if (c == null) return;
            roles[c] = r;
            c.Disposed += delegate { roles.Remove(c); };
        }

        static Role RoleOf(Control c)
        {
            Role r;
            return c != null && roles.TryGetValue(c, out r) ? r : Role.None;
        }

        // ------------------------------------------------------------------ Aplicar a una ventana

        /// <summary>Colores de toda la ventana (y de sus menús y barras). Se puede llamar de nuevo al cambiar de tema.</summary>
        public static void Apply(Form f)
        {
            if (f == null) return;
            f.BackColor = Back;
            f.ForeColor = Text;
            ApplyTree(f);
            TitleBar(f);
            if (f.ContextMenuStrip != null) Menu(f.ContextMenuStrip);
            f.Invalidate(true);
        }

        static void ApplyTree(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                ApplyOne(c);
                if (c.Controls.Count > 0 && !(c is DataGridView) && !(c is NumericUpDown) && !(c is ComboBox)) ApplyTree(c);
                if (c.ContextMenuStrip != null) Menu(c.ContextMenuStrip);
            }
        }

        static void ApplyOne(Control c)
        {
            var role = RoleOf(c);
            if (c is Button) { Button((Button)c, role); return; }
            if (c is DataGridView) { Grid((DataGridView)c); return; }
            if (c is ToolStrip) { Strip((ToolStrip)c); return; }
            if (c is TextBox)
            {
                var t = (TextBox)c;
                t.BackColor = role == Role.Surface ? Surface : Input;
                t.ForeColor = Text;
                DarkScroll(t);
                return;
            }
            if (c is ComboBox)
            {
                var cb = (ComboBox)c;
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = Input;
                cb.ForeColor = Text;
                DarkScroll(cb, "DarkMode_CFD");
                return;
            }
            if (c is ListBox) { c.BackColor = Input; c.ForeColor = Text; DarkScroll(c); return; }
            if (c is NumericUpDown) { c.BackColor = Input; c.ForeColor = Text; return; }
            if (c is LinkLabel)
            {
                var l = (LinkLabel)c;
                l.LinkColor = Accent; l.ActiveLinkColor = AccentHover; l.VisitedLinkColor = Accent;
                l.DisabledLinkColor = Muted;
                l.ForeColor = role == Role.Sub ? SubText : Text;
                return;
            }
            if (c is CheckBox || c is RadioButton)
            {
                var b = (ButtonBase)c;
                b.ForeColor = Text;
                // El dibujo del sistema pinta la casilla en claro; en oscuro, plana con los colores del tema.
                b.FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard;
                if (Dark) { b.FlatAppearance.BorderColor = CheckBorder; b.FlatAppearance.CheckedBackColor = Accent; }
                return;
            }
            if (c is GroupBox) { c.ForeColor = Text; return; }
            if (c is Label)
            {
                c.ForeColor = role == Role.Sub ? SubText : Text;
                if (role == Role.Surface) c.BackColor = Surface; else c.ResetBackColor();
                return;
            }
            if (c is SplitContainer) { c.BackColor = Border; return; }
            // Paneles: o superficie (blanco en claro) o heredan el fondo de su contenedor.
            if (role == Role.Surface) c.BackColor = Surface;
            else if (c is SplitterPanel) c.BackColor = Back;
            else c.ResetBackColor();
            c.ForeColor = Text;
        }

        // ------------------------------------------------------------------ Botones

        public static void Button(Button b, Role role)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.FlatAppearance.BorderSize = 1;
            b.Cursor = Cursors.Hand;
            switch (role)
            {
                case Role.Primary: Filled(b, Accent, AccentHover); break;
                case Role.Good: Filled(b, Good, GoodHover); break;
                case Role.Danger: Filled(b, Bad, Bad); break;
                case Role.Tab:
                    b.FlatAppearance.BorderSize = 0;
                    b.BackColor = Back;
                    b.FlatAppearance.MouseOverBackColor = Back;
                    b.FlatAppearance.MouseDownBackColor = Back;
                    break;
                default:
                    b.BackColor = Surface;
                    b.ForeColor = Text;
                    b.FlatAppearance.BorderColor = Border;
                    b.FlatAppearance.MouseOverBackColor = SurfaceAlt;
                    b.FlatAppearance.MouseDownBackColor = Border;
                    break;
            }
            b.Invalidate();
        }

        static void Filled(Button b, Color back, Color hover)
        {
            bool on = b.Enabled;
            b.BackColor = on ? back : Disabled;
            b.ForeColor = on ? Color.White : DisabledText;
            b.FlatAppearance.BorderColor = on ? back : Disabled;
            b.FlatAppearance.MouseOverBackColor = on ? hover : Disabled;
            b.FlatAppearance.MouseDownBackColor = on ? hover : Disabled;
        }

        /// <summary>Un botón con color propio cambia de aspecto al habilitarse o deshabilitarse (WinForms lo dibuja gris sobre el color).</summary>
        public static void TrackEnabled(Button b)
        {
            b.EnabledChanged += delegate { Button(b, RoleOf(b)); };
        }

        /// <summary>
        /// Texto de un botón plano deshabilitado: WinForms lo pinta en gris del sistema, ilegible sobre el fondo oscuro. Se dibuja
        /// encima con el color del tema.
        /// </summary>
        public static void PaintDisabledText(Button b)
        {
            b.Paint += delegate(object s, PaintEventArgs e)
            {
                var btn = (Button)s;
                if (btn.Enabled) return;
                var r = btn.ClientRectangle;
                r.Inflate(-1, -1);
                using (var back = new SolidBrush(btn.BackColor)) e.Graphics.FillRectangle(back, r);
                TextRenderer.DrawText(e.Graphics, btn.Text, btn.Font, btn.ClientRectangle, DisabledText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            };
        }

        // ------------------------------------------------------------------ Grilla

        public static void Grid(DataGridView g)
        {
            g.EnableHeadersVisualStyles = false;
            g.BackgroundColor = Surface;
            g.GridColor = Border;
            g.BorderStyle = BorderStyle.None;
            g.DefaultCellStyle.BackColor = Surface;
            g.DefaultCellStyle.ForeColor = Text;
            g.DefaultCellStyle.SelectionBackColor = Selection;
            g.DefaultCellStyle.SelectionForeColor = SelectionText;
            g.AlternatingRowsDefaultCellStyle.BackColor = SurfaceAlt;
            g.AlternatingRowsDefaultCellStyle.ForeColor = Text;
            g.ColumnHeadersDefaultCellStyle.BackColor = HeaderBack;
            g.ColumnHeadersDefaultCellStyle.ForeColor = SubText;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = HeaderBack;
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = SubText;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.RowHeadersDefaultCellStyle.BackColor = HeaderBack;
            DarkScroll(g);
            foreach (Control child in g.Controls) DarkScroll(child);   // las barras de desplazamiento son controles hijos
            g.Invalidate();
        }

        /// <summary>
        /// La casilla de la primera columna, dibujada con los colores del tema (la del sistema queda blanca en oscuro).
        /// <paramref name="state"/>: 0 vacía, 1 marcada, 2 marcada pero fija (instalándose), -1 no se puede marcar.
        /// </summary>
        public static void PaintCheck(Graphics g, Rectangle cell, int state, float scale)
        {
            if (state < 0) return;
            int size = (int)Math.Round(16 * scale);
            var box = new Rectangle(cell.Left + (cell.Width - size) / 2, cell.Top + (cell.Height - size) / 2, size, size);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(box, Math.Max(2, size / 5)))
            {
                if (state == 0)
                {
                    using (var fill = new SolidBrush(Input)) g.FillPath(fill, path);
                    using (var pen = new Pen(CheckBorder, Math.Max(1f, scale))) g.DrawPath(pen, path);
                }
                else
                {
                    Color c = state == 2 ? Muted : Accent;
                    using (var fill = new SolidBrush(c)) g.FillPath(fill, path);
                    using (var pen = new Pen(Color.White, Math.Max(1.6f, 2f * scale)))
                    {
                        pen.StartCap = pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;
                        g.DrawLines(pen, new[]
                        {
                            new PointF(box.Left + size * 0.24f, box.Top + size * 0.52f),
                            new PointF(box.Left + size * 0.43f, box.Top + size * 0.70f),
                            new PointF(box.Left + size * 0.76f, box.Top + size * 0.32f)
                        });
                    }
                }
            }
            g.SmoothingMode = old;
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ------------------------------------------------------------------ Menús y barras

        public static void Strip(ToolStrip s)
        {
            s.RenderMode = ToolStripRenderMode.Professional;
            s.Renderer = new Renderer();
            s.BackColor = s is StatusStrip ? Surface : Back;
            s.ForeColor = Text;
            foreach (ToolStripItem i in s.Items) Item(i);
        }

        public static void Menu(ToolStrip menu)
        {
            menu.RenderMode = ToolStripRenderMode.Professional;
            menu.Renderer = new Renderer();
            menu.BackColor = Surface;
            menu.ForeColor = Text;
            foreach (ToolStripItem i in menu.Items) Item(i);
        }

        static void Item(ToolStripItem i)
        {
            if (!(i is ToolStripStatusLabel) || ((ToolStripStatusLabel)i).Tag == null) i.ForeColor = Text;
            var dd = i as ToolStripDropDownItem;
            if (dd != null && dd.HasDropDownItems) Menu(dd.DropDown);
        }

        sealed class Renderer : ToolStripProfessionalRenderer
        {
            public Renderer() : base(new Colors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (!e.Item.Enabled) e.TextColor = DisabledText;
                else if (e.Item is ToolStripMenuItem) e.TextColor = Text;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item != null && e.Item.Enabled ? SubText : DisabledText;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is ToolStripDropDown)
                    using (var pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                // en las barras, sin borde
            }
        }

        sealed class Colors : ProfessionalColorTable
        {
            public Colors() { UseSystemColors = false; }
            public override Color ToolStripDropDownBackground { get { return Surface; } }
            public override Color ImageMarginGradientBegin { get { return Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Color ImageMarginGradientEnd { get { return Surface; } }
            public override Color MenuBorder { get { return Border; } }
            public override Color MenuItemBorder { get { return Selection; } }
            public override Color MenuItemSelected { get { return Selection; } }
            public override Color MenuItemSelectedGradientBegin { get { return Selection; } }
            public override Color MenuItemSelectedGradientEnd { get { return Selection; } }
            public override Color MenuItemPressedGradientBegin { get { return Selection; } }
            public override Color MenuItemPressedGradientEnd { get { return Selection; } }
            public override Color SeparatorDark { get { return Border; } }
            public override Color SeparatorLight { get { return Border; } }
            public override Color StatusStripGradientBegin { get { return Surface; } }
            public override Color StatusStripGradientEnd { get { return Surface; } }
            public override Color ToolStripGradientBegin { get { return Back; } }
            public override Color ToolStripGradientMiddle { get { return Back; } }
            public override Color ToolStripGradientEnd { get { return Back; } }
            public override Color ToolStripBorder { get { return Back; } }
            public override Color ButtonSelectedHighlight { get { return Selection; } }
            public override Color ButtonSelectedGradientBegin { get { return Selection; } }
            public override Color ButtonSelectedGradientEnd { get { return Selection; } }
            public override Color ButtonSelectedBorder { get { return Selection; } }
            public override Color CheckBackground { get { return Selection; } }
        }

        // ------------------------------------------------------------------ Windows: barra de título y barras de desplazamiento

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        static bool OnWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        /// <summary>Barra de título oscura (Windows 10 1809 en adelante; en versiones anteriores no hace nada).</summary>
        public static void TitleBar(Form f)
        {
            if (!OnWindows) return;
            if (!f.IsHandleCreated) { f.HandleCreated += delegate { TitleBar(f); }; return; }
            try
            {
                int on = Dark ? 1 : 0;
                // 20: DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+ y 11); 19 en las compilaciones 17763–18363.
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));
                // La barra de título no se redibuja sola al cambiar el atributo con la ventana a la vista.
                if (f.Visible && f.WindowState == FormWindowState.Normal) { f.Width += 1; f.Width -= 1; }
            }
            catch (Exception ex) { Log.Quiet("Barra de título oscura", ex); }
        }

        static void DarkScroll(Control c) { DarkScroll(c, "DarkMode_Explorer"); }

        static void DarkScroll(Control c, string dark)
        {
            if (!OnWindows || c == null) return;
            if (!c.IsHandleCreated)
            {
                EventHandler h = null;
                h = delegate { c.HandleCreated -= h; DarkScroll(c, dark); };
                c.HandleCreated += h;
                return;
            }
            try { SetWindowTheme(c.Handle, Dark ? dark : "Explorer", null); }
            catch (Exception ex) { Log.Quiet("Barras de desplazamiento oscuras", ex); }
        }
    }
}
