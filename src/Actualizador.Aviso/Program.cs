// Actualizador.Aviso.exe: lo ejecuta la revisión programada. Busca actualizaciones SIN instalar nada y,
// si hay, avisa junto al reloj. Al tocar el aviso abre Actualizador en «Actualizaciones».
//
// Es un ejecutable aparte, sin pedir elevación (asInvoker): el Programador de tareas no puede abrir un programa que
// pide elevación (Actualizador.exe lo hace con los administradores) desde una tarea sin privilegios, y la consola
// (Actualizador.Cli.exe) abriría una ventana negra cada vez. Respeta la política de la empresa igual que la ventana.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Aviso
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            AppInfo.HardenProcess();
            Log.Init(AppInfo.LogDirectory, "aviso");
            TextEncodings.EnsureRegistered();   // páginas de códigos de Windows (850, 1252…) en .NET 8
            try
            {
#if NET5_0_OR_GREATER
                // Nítida en cada monitor aunque tengan escalas distintas (PerMonitorV2, .NET 8).
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#endif
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var list = new List<string>(args);
                if (list.Contains(ScheduledReview.Argument)) return RunScheduled();
                if (list.Contains("--probar-aviso")) { ShowNotice(Sample(), null, TimeSpan.FromMinutes(2)); return 0; }
                MessageBox.Show(AppInfo.Name + " " + AppInfo.Version + "\n\nEste programa lo usa la revisión programada de actualizaciones: busca actualizaciones sin instalar nada y, si hay, avisa junto al reloj.\n\n" +
                    "Se activa y se configura en Actualizador › Opciones › Actualizaciones.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Aviso", ex);
                return 2;
            }
        }

        static int RunScheduled()
        {
            bool first;
            using (var mutex = new Mutex(true, "Local\\Actualizador.Aviso", out first))
            {
                if (!first) { Log.Info("Revisión programada: ya hay otra en curso."); return 0; }
                Log.Info("Revisión programada " + AppInfo.Version + " (" + (OsInfo.IsAdministrator() ? "como administrador" : "sin elevar") + ").");
                string source = ReadOption("fuenteVersiones");
                var options = new UpdateOptions();
                options.QueryVendors = ReadOption("consultarFabricantes") != "false";
                options.InstallWinget = ReadOption("instalarWinget") != "false";
                options.SourcesCatalogUrl = ReadOption("catalogoFuentes") ?? "";
                options.IncludePreReleases = ReadOption("ofrecerBetas") == "true";
                options.OwnBuildsFolder = (ReadOption("carpetaPropia") ?? "").Trim();
                var ctx = UpdateContext.Create(options);
                var report = UpdateSession.Create(ctx);
                var outcome = new ReviewOutcome();
                var watch = Stopwatch.StartNew();
                try
                {
                    var manager = new UpdateManager();
                    manager.Via = "programada";
                    manager.Scan(ctx, report);
                    outcome = ReviewOutcome.From(report.Updates, DateTime.Now);
                }
                catch (Exception ex)
                {
                    outcome.When = DateTime.Now;
                    outcome.Error = ex.Message;
                    Log.Error("Revisión programada", ex);
                }
                Log.Info("Revisión programada terminada en " + Fmt.Duration(watch.Elapsed) + ": " + Fmt.Plural(outcome.Total, "disponible", "disponibles") +
                         (outcome.Ignored > 0 ? ", " + Fmt.Plural(outcome.Ignored, "ignorada", "ignoradas") : "") + ".");

                string lastSig; DateTime? lastNotified, lastRun; int lastTotal;
                ReviewOutcome.ReadLast(ReviewOutcome.StatePath, out lastSig, out lastNotified, out lastRun, out lastTotal);
                bool notify = ReviewOutcome.ShouldNotify(outcome, lastSig, lastNotified);
                outcome.Save(ReviewOutcome.StatePath, notify, lastSig, lastNotified);

                SelfUpdate.Result version = null;
                try { version = SelfUpdate.CheckIfDue(source, AppInfo.Version); }
                catch (Exception ex) { Log.Warn("Versión nueva: " + ex.Message); }
                string versionText = SelfUpdate.Message(version);

                if (notify || versionText != null) ShowNotice(notify ? outcome : null, versionText, TimeSpan.FromHours(4));
                else Log.Info(outcome.Total == 0 ? "Nada para avisar: todo al día." : "Nada nuevo para avisar (ya se avisó lo mismo el " + Fmt.Date(lastNotified ?? DateTime.MinValue) + ").");
                return outcome.Error == null ? 0 : 1;
            }
        }

        /// <summary>Lee un valor de las opciones de la interfaz (%LOCALAPPDATA%\Actualizador\opciones.json).</summary>
        static string ReadOption(string key)
        {
            try
            {
                string f = Path.Combine(AppInfo.DataDirectory, "opciones.json");
                if (!File.Exists(f)) return null;
                return Json.Str(Json.Parse(File.ReadAllText(f, Encoding.UTF8)), key);
            }
            catch (Exception ex) { Log.Warn("Opciones: " + ex.Message); return null; }
        }

        static ReviewOutcome Sample()
        {
            var o = new ReviewOutcome();
            o.When = DateTime.Now; o.Programs = 2; o.Drivers = 1;
            o.Names.Add("7-Zip 25.01"); o.Names.Add("Google Chrome 130.0"); o.Names.Add("Intel(R) Wi-Fi 6 AX201");
            return o;
        }

        /// <summary>
        /// Ícono junto al reloj con un aviso (en Windows 10 y 11 se ve como una notificación). Queda el ícono hasta
        /// que se toca, se cierra desde su menú o pasa <paramref name="keep"/>: así se puede volver a él más tarde.
        /// </summary>
        static void ShowNotice(ReviewOutcome outcome, string versionText, TimeSpan keep)
        {
            using (var icon = new NotifyIcon())
            using (var menu = new ContextMenuStrip())
            using (var timer = new System.Windows.Forms.Timer())
            {
                try { icon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { icon.Icon = SystemIcons.Information; }
                string title = outcome != null ? outcome.Title : "Actualizador: versión nueva";
                string text = outcome != null ? outcome.Text + (versionText != null ? " También hay una versión nueva de Actualizador." : "") : versionText;
                icon.Text = Fmt.Truncate(title, 63);
                bool opened = false;
                EventHandler open = delegate
                {
                    if (opened) return;
                    opened = true;
                    OpenActualizador(outcome != null ? "--actualizaciones" : "");
                    Application.ExitThread();
                };
                menu.Items.Add("Revisar en Actualizador", null, open);
                if (versionText != null) menu.Items.Add("Versión nueva de Actualizador…", null, delegate { MessageBox.Show(versionText, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information); });
                menu.Items.Add("Desactivar la revisión programada", null, delegate
                {
                    if (MessageBox.Show("¿Dejar de buscar actualizaciones automáticamente?\n\nSe puede volver a activar en Actualizador › Opciones › Actualizaciones.", AppInfo.Name,
                                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    string err = ScheduledReview.Unregister();
                    if (err != null) MessageBox.Show("No se pudo desactivar: " + err, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Application.ExitThread();
                });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Cerrar este aviso", null, delegate { Application.ExitThread(); });
                icon.ContextMenuStrip = menu;
                icon.BalloonTipClicked += open;
                icon.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) open(s, e); };
                icon.Visible = true;
                icon.ShowBalloonTip(30000, title, Fmt.Truncate(text, 250), ToolTipIcon.Info);
                Log.Info("Aviso: " + title + " — " + text);
                timer.Interval = (int)Math.Min(int.MaxValue, keep.TotalMilliseconds);
                timer.Tick += delegate { Application.ExitThread(); };
                timer.Start();
                Application.Run();
                icon.Visible = false;
            }
        }

        /// <summary>Abre Actualizador.exe de la misma carpeta (con los administradores, Windows pide confirmar la elevación).</summary>
        static void OpenActualizador(string args)
        {
            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Actualizador.exe");
            try
            {
                if (!File.Exists(exe)) { MessageBox.Show("No se encuentra " + exe + ".", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                using (Process.Start(psi)) { }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223) Log.Warn("No se pudo abrir Actualizador: " + ex.Message);   // 1223: canceló el aviso de UAC
            }
            catch (Exception ex) { Log.Warn("No se pudo abrir Actualizador: " + ex.Message); }
        }
    }
}
