using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Actualizador.Core;

namespace Actualizador.App
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // 2.0.39: «Actualizador.exe --comprobar-arranque informe.txt» (lo usa EmpaquetarPortable.ps1 antes de entregar la portable).
            if (args != null && args.Length == 2 && args[0] == "--comprobar-arranque") return CheckStartup(args[1]);
            AppInfo.HardenProcess();
            Log.Init(AppInfo.LogDirectory, "gui");
            TextEncodings.EnsureRegistered();   // páginas de códigos de Windows (850, 1252…) en .NET 8
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                var ex = e.ExceptionObject as Exception;
                Log.Error("Excepción no controlada", ex);
                ShowFatal("Actualizador se cerró por un error inesperado.", ex);
            };
            try
            {
#if NET5_0_OR_GREATER
                // Nítida en cada monitor aunque tengan escalas distintas (PerMonitorV2, .NET 8).
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#endif
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) { ShowError(e.Exception); };
                Log.Info(AppInfo.Title + " " + AppInfo.Version + " iniciado en " + Environment.OSVersion + ", .NET " + Environment.Version +
                    ", proceso de " + (Environment.Is64BitProcess ? 64 : 32) + " bits. Argumentos: " + string.Join(" ", args));
                if (AppInfo.IsPortable)
                    Log.Info("Versión portable en " + AppInfo.ProgramDirectory + ". Datos: " + AppInfo.DataDirectory + "; de este equipo: " + AppInfo.MachineDataDirectory + ".");
                if (AppInfo.PortableNote != null) Log.Warn(AppInfo.PortableNote);
                if (AppInfo.IsPortable)
                {
                    // 2.0.39: lo que trajeron versiones anteriores y esta ya no usa (la consola, las bibliotecas de WPF): la actualización
                    // sola copia encima sin borrar, así que se quita acá. Sólo esos nombres, y sólo si su SHA256SUMS.txt no los lista.
                    try
                    {
                        var removed = PortableLayout.RemoveObsolete(AppInfo.ProgramDirectory);
                        if (removed.Count > 0) Log.Info("Portable: se borraron archivos que esta versión ya no usa: " + string.Join(", ", removed.ToArray()) + ".");
                    }
                    catch (Exception ex) { Log.Info("Portable: no se pudieron borrar archivos viejos: " + ex.Message); }
                }
                Application.Run(new MainForm(GuiSettings.Load(), args));
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Error al iniciar la interfaz", ex);
                ShowFatal("Actualizador no pudo iniciar.", ex);
                return 1;
            }
        }

        /// <summary>Último recurso: que el error se vea en vez de cerrar en silencio.</summary>
        /// <summary>
        /// 2.0.39: prueba de arranque de la portable recién armada, sin mostrar nada: carga .NET y WinForms (sin las bibliotecas de WPF que
        /// ya no lleva), crea una ventana sin mostrarla y escribe en <paramref name="report"/> la versión y dónde guarda los datos. 0 si anduvo.
        /// </summary>
        static int CheckStartup(string report)
        {
            if (string.IsNullOrEmpty(report) || !report.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return 2;
            try
            {
                Application.EnableVisualStyles();
                using (var f = new Form())
                {
                    IntPtr h = f.Handle;
                    if (h == IntPtr.Zero) throw new InvalidOperationException("no se pudo crear la ventana");
                }
                File.WriteAllText(report, AppInfo.Name + " " + AppInfo.Version + (AppInfo.IsPortable ? " (portable)" : "") + "\r\n" +
                                          "Datos: " + AppInfo.DataDirectory + "\r\n", new UTF8Encoding(false));
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(report, "ERROR: " + ex, new UTF8Encoding(false)); } catch { }
                return 3;
            }
        }

        static void ShowFatal(string title, Exception ex)
        {
            try
            {
                MessageBox.Show(title + "\n\n" + (ex == null ? "Error desconocido." : ex.GetType().Name + ": " + ex.Message) +
                    "\n\nDetalle técnico en:\n" + (Log.FilePath ?? AppInfo.LogDirectory),
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        public static void ShowError(Exception ex)
        {
            Log.Error("Error en la interfaz", ex);
            MessageBox.Show("Ocurrió un error inesperado:\n\n" + ex.Message + (Log.FilePath != null ? "\n\nDetalle en: " + Log.FilePath : ""),
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>Vuelve a abrir el programa con elevación (UAC). false si el usuario la rechazó.</summary>
        public static bool RestartElevated(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, arguments);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                using (Process.Start(psi)) { }
                return true;
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223)   // 1223 = el usuario canceló el aviso de UAC
                    MessageBox.Show("No se pudo reiniciar como administrador: " + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }
    }

    /// <summary>Opciones de la interfaz, guardadas en %LOCALAPPDATA%\Actualizador\opciones.json (la portable: Datos\opciones.json, junto al programa).</summary>
    sealed class GuiSettings
    {
        public bool RestorePoint = true;
        public bool ShowAllUpdates = true;
        public bool InteractiveInstall;
        public bool QueryVendors = true;
        /// <summary>Instalar winget solo si falta.</summary>
        public bool InstallWinget = true;
        /// <summary>2.0.8: tema de la ventana: «sistema» (según Windows), «claro» u «oscuro».</summary>
        public string Theme = "sistema";
        /// <summary>2.0.8: carpeta con las versiones de los programas propios («RelojMonitor_1.9.0.7z»); vacío = no se usa.</summary>
        public string OwnBuildsFolder = "";
        /// <summary>Dónde se publican las versiones nuevas de Actualizador (GitHub o una carpeta); vacío = no se consulta.</summary>
        public string VersionSource = "";
        /// <summary>Dirección HTTPS del catálogo de fuentes oficiales para bajar; vacío = sólo los archivos locales.</summary>
        public string SourcesCatalogUrl = "";
        /// <summary>Ofrecer las betas que publica el fabricante (opcionales, abren su página).</summary>
        public bool OfferBetas;

        static string FilePath { get { return Path.Combine(AppInfo.DataDirectory, "opciones.json"); } }

        public static GuiSettings Load()
        {
            var s = new GuiSettings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                object o = Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                s.RestorePoint = Json.Str(o, "puntoRestauracion") != "false";
                s.ShowAllUpdates = Json.Str(o, "mostrarTodo") != "false";
                s.InteractiveInstall = false;      // se elige en cada confirmación, no queda guardada
                s.QueryVendors = Json.Str(o, "consultarFabricantes") != "false";
                s.InstallWinget = Json.Str(o, "instalarWinget") != "false";
                // 2.0.8: ya no se busca al abrir ni se instala lo que no se marcó («buscarAlAbrir» y «repetirHastaAlDia» no se usan).
                s.Theme = Actualizador.App.Theme.ToText(Actualizador.App.Theme.Parse(Json.Str(o, "tema")));
                s.OwnBuildsFolder = (Json.Str(o, "carpetaPropia") ?? "").Trim();
                s.VersionSource = (Json.Str(o, "fuenteVersiones") ?? "").Trim();
                s.SourcesCatalogUrl = (Json.Str(o, "catalogoFuentes") ?? "").Trim();
                s.OfferBetas = Json.Str(o, "ofrecerBetas") == "true";
            }
            catch (Exception ex) { Log.Warn("No se pudieron leer las opciones (se usan las predeterminadas): " + ex.Message); }
            return s;
        }

        public void Save()
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["puntoRestauracion"] = RestorePoint;
                d["mostrarTodo"] = ShowAllUpdates;
                d["instalacionInteractiva"] = InteractiveInstall;
                d["consultarFabricantes"] = QueryVendors;
                d["instalarWinget"] = InstallWinget;
                d["tema"] = Theme ?? "sistema";
                d["carpetaPropia"] = OwnBuildsFolder ?? "";
                d["fuenteVersiones"] = VersionSource ?? "";
                d["catalogoFuentes"] = SourcesCatalogUrl ?? "";
                d["ofrecerBetas"] = OfferBetas;
                if (!SafePaths.EnsureDataDirectory(AppInfo.DataDirectory))
                {
                    Log.Warn("No se guardan las opciones: la carpeta " + AppInfo.DataDirectory + " contiene un enlace (junction/symlink).");
                    return;
                }
                AtomicFile.WriteAllText(FilePath, Json.Serialize(d, true), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("No se pudieron guardar las opciones: " + ex.Message); }
        }

        public UpdateOptions ToOptions()
        {
            var o = new UpdateOptions();
            o.QueryVendors = QueryVendors;
            o.InstallWinget = InstallWinget;
            o.SourcesCatalogUrl = SourcesCatalogUrl ?? "";
            o.IncludePreReleases = OfferBetas;
            o.OwnBuildsFolder = OwnBuildsFolder ?? "";
            return o;
        }
    }

    /// <summary>Datos ficticios para probar la interfaz sin tocar el equipo (argumento --demo).</summary>
    static class DemoData
    {
        public static UpdateSession Create()
        {
            var r = new UpdateSession();
            r.ProgramName = AppInfo.Name; r.ProgramVersion = AppInfo.Version;
            r.ComputerName = "EQUIPO-DEMO"; r.UserName = "DEMO\\usuario"; r.OsDescription = "Windows 11 Pro 24H2 (compilación 26100.4946) x64";
            r.IsAdmin = true; r.StartedLocal = DateTime.Now; r.Duration = TimeSpan.FromSeconds(83);

            r.Updates.Add(U(UpdateKind.Programa, "7-Zip 24.07 (x64)", "24.07", "25.01", UpdateStatus.Disponible, "winget (winget)", true));
            r.Updates.Add(U(UpdateKind.Programa, "Google Chrome", "128.0.6613.120", "129.0.6668.59", UpdateStatus.Disponible, "winget (winget)", true));
            r.Updates.Add(U(UpdateKind.Controlador, "Intel(R) Wi-Fi 6 AX201 160MHz", "22.200.0.6 (2023-02-10)", "23.60.1.2 (2024-06-20)", UpdateStatus.Disponible, "Windows Update", true));
            r.Updates.Add(U(UpdateKind.WindowsUpdate, "2024-09 Actualización acumulativa para Windows 11 (KB5043076)", "No instalada", "KB5043076", UpdateStatus.Disponible, "Windows Update", true));
            var store = U(UpdateKind.Store, "Todas las apps de Microsoft Store (la Store busca e instala)", "38 apps", "Lo que publique la Store", UpdateStatus.Pendiente, "Microsoft Store", true);
            store.Identifier = Actualizador.Updates.StoreProvider.AllAppsId;
            r.Updates.Add(store);
            var pdf = U(UpdateKind.Programa, "PDF24 Creator 11.30.1", "11.30.1", "11.31.1", UpdateStatus.Disponible, "Sitio oficial (www.pdf24.org)", true);
            pdf.ProviderName = Actualizador.Updates.SiteProvider.ProviderId;
            pdf.Detail = "Su página oficial publica la 11.31.1. Al actualizar se abre su página de descarga.";
            r.Updates.Add(pdf);
            var cat = U(UpdateKind.Controlador, "Realtek PCIe GbE Family Controller", "10.68.815.2023 (2023-08-15)", "10.72.1101.2024 (2024-11-01)", UpdateStatus.Disponible, "Catálogo de Microsoft Update", true);
            cat.Recommended = false;
            cat.Detail = "Windows Update no la eligió para este equipo: lo decidís vos.";
            r.Updates.Add(cat);
            r.Updates.Add(U(UpdateKind.Programa, "Mozilla Firefox (x64 es-AR)", "130.0", "130.0", UpdateStatus.AlDia, "winget (winget)", false));
            r.Updates.Add(U(UpdateKind.Programa, "Controlador de impresora HP", "1.2.3", "", UpdateStatus.NoVerificable, "", false));
            var office = U(UpdateKind.Programa, "Microsoft Office Profesional Plus 2024 - es-mx", "16.0.20326.20158", "16.0.20430.20092", UpdateStatus.Disponible, "Microsoft (config.office.net)", true);
            office.Detail = "Microsoft publica la 16.0.20430.20092 en el Canal actual. Se actualiza con el actualizador propio de Office (OfficeC2RClient.exe, firmado por Microsoft).";
            office.InfoUrl = "https://learn.microsoft.com/officeupdates/update-history-microsoft365-apps-by-date";
            r.Updates.Add(office);
            var viv = U(UpdateKind.Programa, "Vivaldi", "8.2.4133.76", "8.2.4133.76", UpdateStatus.AlDia, "Vivaldi (update.vivaldi.com)", false);
            viv.Detail = "Coincide con la última versión estable publicada por el fabricante.";
            viv.InfoUrl = "https://vivaldi.com/es/download/";
            r.Updates.Add(viv);
            var juego = U(UpdateKind.Programa, "Counter-Strike 2", "Desconocida", "", UpdateStatus.NoVerificable, "", false);
            juego.Detail = "Juego de Steam: lo actualiza Steam al abrirlo.";
            r.Updates.Add(juego);
            var ign = U(UpdateKind.Programa, "TeamViewer", "15.40.8", "15.58.4", UpdateStatus.Disponible, "winget (winget)", true);
            ign.Identifier = "TeamViewer.TeamViewer";
            var lista = new Actualizador.Updates.IgnoreList();
            lista.Add(Actualizador.Updates.IgnoreList.RuleFor(ign, false, DateTime.Now.AddDays(-3)));
            lista.Apply(new List<UpdateCandidate> { ign });
            r.Updates.Add(ign);
            r.UpdateNotes.Add("Datos de demostración: no se instala nada.");
            r.UpdatesScannedLocal = DateTime.Now;
            r.UpdatesScanDuration = TimeSpan.FromSeconds(134);
            return r;
        }

        static UpdateCandidate U(UpdateKind k, string name, string inst, string avail, UpdateStatus st, string src, bool installable)
        {
            var c = new UpdateCandidate();
            c.Kind = k; c.Name = name; c.InstalledVersion = inst; c.AvailableVersion = avail; c.Status = st; c.Source = src;
            c.Installable = installable; c.Recommended = installable; c.Publisher = k == UpdateKind.Programa ? "" : "Microsoft";
            c.Detail = st == UpdateStatus.Disponible ? "Versión más nueva disponible." : st == UpdateStatus.NoVerificable ? "No figura en los repositorios de winget." : "";
            c.ProviderName = "demo";
            return c;
        }
    }
}
