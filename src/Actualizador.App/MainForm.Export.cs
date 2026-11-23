// Ventana principal: exportación de la lista, diagnóstico, política y diálogos.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.App
{
    sealed partial class MainForm
    {
        // ================================================================== Exportación y diálogos
        /// <summary>Guarda la lista de actualizaciones en CSV (planillas) o JSON (inventarios y scripts).</summary>
        void ExportUpdates(UpdateExportFormat format)
        {
            var updates = SnapshotUpdates();
            if (report == null || updates.Count == 0) { MessageBox.Show(this, "Primero buscá actualizaciones.", AppInfo.Name); return; }
            using (var dlg = new SaveFileDialog())
            {
                dlg.FileName = UpdateExport.DefaultFileName(DateTime.Now, format);
                dlg.Filter = format == UpdateExportFormat.Json ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv";
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    UpdateExport.Write(dlg.FileName, updates, lastCtx != null ? lastCtx.Os : OsInfo.Detect(), format);
                    lblStatus.Text = "Lista guardada en " + dlg.FileName;
                    if (MessageBox.Show(this, "Lista guardada.\n\n¿Abrir la carpeta?", AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        OpenPath(Path.GetDirectoryName(dlg.FileName));
                }
                catch (Exception ex) { MessageBox.Show(this, "No se pudo guardar la lista: " + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        /// <summary>
        /// Guarda un .zip con la lista de actualizaciones (texto completo de cada fila) y los registros de
        /// Actualizador: con eso se ve por qué un programa no se actualizó en ESTE equipo, sin buscar archivos a mano.
        /// Se puede guardar aunque se esté instalando (no cambia nada).
        /// </summary>
        void ExportDiagnostic()
        {
            string target;
            using (var dlg = new SaveFileDialog())
            {
                dlg.FileName = DiagnosticPackage.DefaultFileName(DateTime.Now);
                dlg.Filter = "Archivo ZIP (*.zip)|*.zip";
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                target = dlg.FileName;
            }
            lblStatus.Text = "Armando el diagnóstico…";
            SaveDiagnostic(target, delegate(int updatesCount, int logs, Exception error)
            {
                if (error != null)
                {
                    lblStatus.Text = "No se pudo guardar el diagnóstico.";
                    MessageBox.Show(this, "No se pudo guardar el diagnóstico: " + error.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                lblStatus.Text = "Diagnóstico guardado en " + target;
                if (MessageBox.Show(this, "Diagnóstico guardado (" + Plural(updatesCount, "fila", "filas") + " de actualizaciones y " + Plural(logs, "registro", "registros") + ").\n\n" +
                                    "Contiene versiones, resultados y los registros de Actualizador (sin contraseñas ni tokens). ¿Abrir la carpeta?",
                                    AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    OpenPath(Path.GetDirectoryName(target));
            });
        }

        /// <summary>Carpeta de los diagnósticos que se guardan solos cuando algo falla.</summary>
        static string AutoDiagnosticDir { get { return Path.Combine(AppInfo.MachineDataDirectory, "diagnosticos"); } }
        const int AutoDiagnosticKeep = 5;
        string lastAutoDiagnostic;

        /// <summary>
        /// Si una tanda terminó con errores, el diagnóstico se guarda solo: así basta con mandar ese archivo, sin
        /// tener que acordarse del menú Exportar. Se guardan los últimos <see cref="AutoDiagnosticKeep"/>.
        /// </summary>
        void AutoSaveDiagnostic()
        {
            try
            {
                if (!SafePaths.EnsureDataDirectory(AppInfo.MachineDataDirectory)) return;
                Directory.CreateDirectory(AutoDiagnosticDir);
                var old = new List<FileInfo>(new DirectoryInfo(AutoDiagnosticDir).GetFiles("*.zip"));
                old.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                for (int i = AutoDiagnosticKeep - 1; i < old.Count; i++) { try { old[i].Delete(); } catch { } }
            }
            catch (Exception ex) { Log.Warn("Diagnóstico automático: " + ex.Message); return; }
            string target = Path.Combine(AutoDiagnosticDir, DiagnosticPackage.DefaultFileName(DateTime.Now));
            SaveDiagnostic(target, delegate(int n, int logs, Exception error)
            {
                if (error != null) return;
                lastAutoDiagnostic = target;
                if (installSummary != null) installSummary += " Se guardó un diagnóstico para enviar (enlace «Diagnóstico», arriba).";
                ShowUpdates();
            });
        }

        /// <summary>Abre la carpeta con el último diagnóstico guardado solo, o pide dónde guardar uno nuevo.</summary>
        void OpenOrExportDiagnostic()
        {
            if (lastAutoDiagnostic != null && File.Exists(lastAutoDiagnostic)) { OpenPath(Path.GetDirectoryName(lastAutoDiagnostic)); return; }
            ExportDiagnostic();
        }

        void SaveDiagnostic(string target, Action<int, int, Exception> done)
        {
            // Los datos se copian acá (hilo de la ventana); el .zip se arma en otro hilo para no congelar la ventana.
            var updates = report != null ? SnapshotUpdates() : new List<UpdateCandidate>();
            var notes = new List<string>();
            if (report != null) lock (report.UpdateNotes) notes.AddRange(report.UpdateNotes);
            var os = lastCtx != null && lastCtx.Os != null ? lastCtx.Os : OsInfo.Detect();
            string system = "Sistema: " + os.FriendlyName + " · " + (isAdmin ? "como administrador" : "sin privilegios de administrador") + " · Actualizador " + AppInfo.Version;
            var worker = new Thread(delegate()
            {
                int logs = 0;
                Exception error = null;
                try
                {
                    var extra = new Dictionary<string, string>();
                    extra["historial.txt"] = UpdateHistory.ToText(UpdateHistory.Load(), 500);
                    logs = DiagnosticPackage.Create(target, updates, notes, system, AppInfo.LogDirectory, extra);
                }
                catch (Exception ex) { error = ex; Log.Warn("Diagnóstico: " + ex.Message); }
                UiInvoke(delegate { done(updates.Count, logs, error); });
            });
            worker.IsBackground = true;
            worker.Name = "diagnostico";
            worker.Start();
        }

        /// <summary>
        /// Menú «Más › Desinstalar Actualizador…»: abre el desinstalador que quedó junto al programa (en una ventana de
        /// consola, que muestra lo que va quitando) y cierra esta ventana, porque el desinstalador cierra Actualizador.
        /// </summary>
        void UninstallSelf()
        {
            if (busy)
            {
                MessageBox.Show(this, "Esperá a que termine la búsqueda o la instalación (o tocá «Detener») para desinstalar Actualizador.",
                                "Desinstalar Actualizador", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string script = SelfUninstall.ScriptPath(AppDomain.CurrentDomain.BaseDirectory);
            if (script == null)
            {
                MessageBox.Show(this, "Esta copia de Actualizador no está instalada (falta " + SelfUninstall.ScriptName + " en su carpeta): " +
                                "no hay nada que desinstalar. Si la instalaste, quitala desde Configuración › Aplicaciones o con «Instalar.cmd /quitar».",
                                "Desinstalar Actualizador", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "Se va a desinstalar Actualizador de este equipo: el programa, sus accesos directos y las revisiones " +
                                "programadas. Se conservan la política y el registro de cambios de la empresa.\n\n¿Desinstalar ahora?",
                                "Desinstalar Actualizador", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(SelfUninstall.PowerShellPath(Environment.GetEnvironmentVariable("SystemRoot")),
                                                                 SelfUninstall.Arguments(script, false));
                // Con ventana: el desinstalador muestra lo que va quitando y espera Enter al final.
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                using (System.Diagnostics.Process.Start(psi)) { }
                Log.Info("Desinstalación pedida desde la ventana: " + script);
                closingForElevation = true;   // cerrar sin preguntar
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo abrir el desinstalador: " + ex.Message, "Desinstalar Actualizador", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static void OpenPath(string path)
        {
            // Elevado, explorer.exe lo abre en la sesión normal: el navegador o el visor no quedan
            // corriendo como administrador.
            try { ProcessRunner.OpenForUser(path); }
            catch (Exception ex) { Log.Warn("No se pudo abrir " + path + ": " + ex.Message); }
        }

        void ShowOptions()
        {
            if (busy) return;
            using (var f = new OptionsForm(settings))
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    lblStatus.Text = "Opciones guardadas.";
                    if (f.IgnoreChanged) ApplyIgnoreList(IgnoreList.Load());
                    // El tema elegido en Opciones se aplica ya.
                    Theme.Set(Theme.Parse(settings.Theme));
                    ApplyTheme();
                    if (f.InstallRequested != null) { newVersion = f.InstallRequested; InstallNewVersion(); }
                }
        }

        void ShowAbout()
        {
            var os = lastCtx != null ? lastCtx.Os : OsInfo.Detect();
            var policy = Policy.Current;
            MessageBox.Show(this,
                AppInfo.Title + "\nVersión " + AppInfo.Version + "\n\n" +
                "Actualiza programas (winget y páginas oficiales de cada fabricante), controladores, Windows (Windows Update) " +
                "y apps de Microsoft Store, desde un solo lugar.\n\n" +
                "Sistema: " + os.FriendlyName + "\n" +
                ".NET: " + Environment.Version + "\n" +
                (AppInfo.IsPortable ? "Versión portable (no está instalada; para quitarla, se borra su carpeta).\n" +
                                      "Datos: " + AppInfo.DataDirectory + "\n" +
                                      (AppInfo.PortableNote != null ? AppInfo.PortableNote + "\n" : "") : "") +
                "Registros: " + AppInfo.LogDirectory + "\n" +
                "Política: " + (policy.Origin ?? "ninguna (valores de fábrica)") + "\n" +
                "Revisión programada: " + ScheduledReview.Query().Describe() + "\n" +
                (newVersion != null && newVersion.Newer ? "\n" + SelfUpdate.Message(newVersion) + "\n" : "") + "\n" +
                "Atajos: F5 buscar actualizaciones · Ctrl+A marcar o desmarcar todo (en la lista) · Ctrl+F filtrar · Ctrl+H historial",
                "Acerca de " + AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Muestra la política vigente (GPO o politica.json) tal como la aplica el programa.</summary>
        void ShowPolicy()
        {
            Policy.Reset();
            var p = Policy.Current;
            MessageBox.Show(this, p.Describe(), "Política de " + AppInfo.Name, MessageBoxButtons.OK,
                p.Problems.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        /// <summary>Verifica la cadena HMAC del registro de auditoría de cambios (detecta borrados o ediciones).</summary>
        void VerifyAuditTrail()
        {
            AuditTrail.VerifyResult r;
            try { r = AuditTrail.Open().Verify(); }
            catch (Exception ex) { MessageBox.Show(this, "No se pudo verificar el registro: " + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            MessageBox.Show(this,
                r.Ok ? "El registro de cambios está íntegro (" + Plural(r.Entries, "entrada", "entradas") + ")." : "El registro de cambios NO está íntegro:\n\n" + r.Problem,
                "Registro de cambios", MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }

        void OpenAuditFolder()
        {
            try
            {
                string dir = AuditTrail.Open().Directory;
                if (Directory.Exists(dir)) OpenPath(dir);
                else MessageBox.Show(this, "Todavía no hay registro de cambios: se crea con la primera búsqueda o instalación.", AppInfo.Name);
            }
            catch (Exception ex) { Log.Warn("Carpeta del registro de cambios: " + ex.Message); }
        }
    }
}
