using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Un proceso abierto desde la carpeta de un programa.</summary>
    public sealed class RunningProcess
    {
        public int Pid;
        public string Name = "", Path = "";
        public int SessionId = -1;
        /// <summary>Es un servicio de Windows: lo detiene y lo vuelve a iniciar el propio instalador (MSI), no se cierra desde acá.</summary>
        public bool IsService;
    }

    /// <summary>
    /// Programas abiertos que hacen fallar a su instalador. El caso real: Epic Games Launcher
    /// queda corriendo en segundo plano, sin ventana ni ícono visible, y su MSI termina con el código
    /// 1603 («error grave durante la instalación»). winget no lo avisa. Antes de actualizar se buscan
    /// los procesos que corren desde la carpeta del programa y, si el usuario lo acepta, se cierran:
    /// primero se les pide que se cierren (como el botón ✕) y sólo se terminan los que no tienen
    /// ventana (en segundo plano o en el área de notificación). Uno con ventana que no se cierra
    /// (p. ej. pregunta si guardar los cambios) NO se termina: se avisa.
    /// </summary>
    public static class RunningPrograms
    {
        /// <summary>Sólo para las pruebas: reemplaza la consulta de procesos abiertos.</summary>
        internal static Func<List<RunningProcess>> SnapshotForTests;

        // ------------------------------------------------------------------ Carpetas

        static readonly string[] SystemTop = { "windows", "program files", "program files (x86)", "programdata", "users", "documents and settings",
                                               "$recycle.bin", "system volume information", "recovery", "perflogs", "boot" };
        static readonly string[] SharedUnderProgramFiles = { "common files", "windowsapps", "modifiablewindowsapps", "package cache", "windows defender",
                                                             "windows defender advanced threat protection", "windows nt", "windowspowershell", "dotnet",
                                                             "internet explorer", "windows mail", "windows media player", "windows photo viewer",
                                                             "windows sidebar", "windows security", "reference assemblies", "msbuild", "uninstall information",
                                                             "microsoft.net", "windows kits", "iis", "iis express" };
        static readonly string[] ProfileFolders = { "appdata", "desktop", "documents", "downloads", "music", "pictures", "videos", "onedrive",
                                                    "escritorio", "documentos", "descargas", "música", "imágenes", "vídeos", "favorites", "links", "saved games", "searches", "contacts" };

        /// <summary>
        /// ¿La carpeta es la de UN programa y no una carpeta compartida? Se rechazan la raíz del disco,
        /// Windows (y todo lo que tiene adentro), Program Files, «Common Files», ProgramData, la raíz de
        /// un perfil, AppData, AppData\Local\Programs, las carpetas personales y las temporales. Con
        /// una InstallLocation mal registrada («C:\Program Files») se cerraría medio sistema.
        /// </summary>
        public static bool IsSpecificFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            string f = folder.Trim().Trim('"').Trim().TrimEnd('\\');
            if (f.Length < 4 || f[1] != ':' || f[2] != '\\' || !char.IsLetter(f[0])) return false;
            if (f.IndexOf(@"\..", StringComparison.Ordinal) >= 0 || f.IndexOf('/') >= 0) return false;
            string[] seg = f.Substring(3).ToLowerInvariant().Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length == 0) return false;
            string top = seg[0];
            if (top == "windows" || top == "temp" || top == "tmp" || top == "$recycle.bin" || top == "system volume information" || top == "recovery") return false;
            if (top == "program files" || top == "program files (x86)" || top == "programdata")
            {
                if (seg.Length < 2) return false;
                string s1 = seg[1];
                // Compartidas por muchos programas o partes de Windows: nunca.
                if (Array.IndexOf(SharedUnderProgramFiles, s1) >= 0) return false;
                // «Microsoft» agrupa productos distintos: hace falta la carpeta del producto (Microsoft\Edge).
                if (s1 == "microsoft") return seg.Length >= 3;
                return true;
            }
            if (top == "users" || top == "documents and settings")
            {
                // C:\Users\fede\AppData\Local\Discord: hace falta llegar a la carpeta del programa.
                if (seg.Length < 3) return false;
                if (seg[1] == "public" || seg[1] == "default" || seg[1] == "all users") return seg.Length >= 4 && Array.IndexOf(ProfileFolders, seg[2]) < 0;
                if (seg[2] == "appdata")
                {
                    if (seg.Length < 5) return false;                               // AppData\Local, AppData\Roaming
                    if (seg[4] == "temp" || seg[4] == "microsoft" || seg[4] == "packages") return false;
                    if (seg[4] == "programs") return seg.Length >= 6;                // AppData\Local\Programs\X
                    return true;
                }
                if (Array.IndexOf(ProfileFolders, seg[2]) >= 0) return seg.Length >= 4;   // Descargas\Programa
                return true;
            }
            return Array.IndexOf(SystemTop, top) < 0;
        }

        /// <summary>Carpeta del programa (InstallLocation, la del ejecutable o la del desinstalador), sólo si es específica; si no, "".</summary>
        public static string FolderOf(InstalledProgram p)
        {
            string folder = WingetDirect.AppFolder(p);
            return IsSpecificFolder(folder) ? folder.Trim().Trim('"').TrimEnd('\\') : "";
        }

        // ------------------------------------------------------------------ Procesos

        /// <summary>
        /// Procesos abiertos con su ruta, marcando los que son servicios. Vacío si no se puede consultar. Con la API de
        /// Windows: se pide antes de instalar cada programa y por WMI eran dos consultas pesadas
        /// por programa, que además dejaban la ventana sin responder con WMI lento.
        /// </summary>
        public static List<RunningProcess> Snapshot()
        {
            if (SnapshotForTests != null) return SnapshotForTests();
            var list = new List<RunningProcess>();
            try
            {
                var services = ProcessList.ServicePids();
                int self = -1;
                try { using (var me = Process.GetCurrentProcess()) self = me.Id; } catch { }
                foreach (var e in ProcessList.Snapshot(true))
                {
                    if (string.IsNullOrEmpty(e.Path)) continue;
                    if (e.Pid <= 4 || e.Pid == self) continue;
                    var rp = new RunningProcess();
                    rp.Pid = e.Pid;
                    rp.Name = string.IsNullOrEmpty(e.Name) ? ProgramRules.FileNameOf(e.Path) : e.Name;
                    rp.Path = e.Path;
                    rp.SessionId = e.SessionId;
                    rp.IsService = services.Contains(rp.Pid);
                    list.Add(rp);
                }
            }
            catch (Exception ex) { Log.Warn("Procesos abiertos: " + ex.Message); }
            return list;
        }

        /// <summary>Los procesos de <paramref name="all"/> que corren desde la carpeta (o una subcarpeta).</summary>
        public static List<RunningProcess> In(List<RunningProcess> all, string folder)
        {
            var result = new List<RunningProcess>();
            if (all == null || !IsSpecificFolder(folder)) return result;
            foreach (var p in all) if (p != null && ProgramRules.IsInFolder(p.Path, folder)) result.Add(p);
            return result;
        }

        /// <summary>Procesos abiertos por nombre (sin «.exe»), para los programas del catálogo de fabricantes.</summary>
        public static List<RunningProcess> ByName(List<RunningProcess> all, string[] names)
        {
            var result = new List<RunningProcess>();
            if (all == null || names == null) return result;
            foreach (var p in all)
            {
                if (p == null) continue;
                string n = p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Name.Substring(0, p.Name.Length - 4) : p.Name;
                foreach (string want in names)
                    if (n.Equals(want, StringComparison.OrdinalIgnoreCase)) { result.Add(p); break; }
            }
            return result;
        }

        /// <summary>Sólo los que se pueden cerrar desde acá (los servicios los maneja el instalador).</summary>
        public static List<RunningProcess> Closable(List<RunningProcess> list)
        {
            var result = new List<RunningProcess>();
            if (list != null) foreach (var p in list) if (!p.IsService) result.Add(p);
            return result;
        }

        /// <summary>«EpicGamesLauncher.exe, EpicWebHelper.exe» (sin repetir, como mucho seis).</summary>
        public static string Names(List<RunningProcess> list)
        {
            var names = new List<string>();
            if (list != null)
                foreach (var p in list)
                {
                    string n = p.Name.Length > 0 ? p.Name : ProgramRules.FileNameOf(p.Path);
                    bool dup = false;
                    foreach (string x in names) if (x.Equals(n, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup) names.Add(n);
                }
            if (names.Count > 6) return string.Join(", ", names.GetRange(0, 6).ToArray()) + " y " + (names.Count - 6) + " más";
            return string.Join(", ", names.ToArray());
        }

        /// <summary>
        /// Cierra los procesos: primero les pide cerrarse (WM_CLOSE a la ventana principal) y espera
        /// hasta <paramref name="graceMs"/>; después termina SÓLO los que no tienen ventana visible
        /// (en segundo plano, en el área de notificación, o que se minimizaron ahí al pedirles
        /// cerrarse). Devuelve los que siguen abiertos (vacío si se cerraron todos).
        /// </summary>
        public static List<RunningProcess> Close(List<RunningProcess> list, int graceMs)
        {
            var still = new List<RunningProcess>();
            if (list == null || list.Count == 0) return still;
            int mySession = -1;
            try { using (var me = Process.GetCurrentProcess()) mySession = me.SessionId; } catch { }
            var open = new List<KeyValuePair<RunningProcess, Process>>();
            var asked = new HashSet<int>();      // a los que se les pidió cerrarse (tenían ventana)
            foreach (var rp in Closable(list))
            {
                if (mySession >= 0 && rp.SessionId >= 0 && rp.SessionId != mySession) { still.Add(rp); continue; }   // otra sesión: no es nuestro
                Process p = null;
                try
                {
                    p = Process.GetProcessById(rp.Pid);
                    // El PID se pudo reutilizar: el nombre tiene que coincidir.
                    string expected = rp.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? rp.Name.Substring(0, rp.Name.Length - 4) : rp.Name;
                    if (p.HasExited || !p.ProcessName.Equals(expected, StringComparison.OrdinalIgnoreCase)) { p.Dispose(); continue; }
                    if (p.MainWindowHandle != IntPtr.Zero) { try { if (p.CloseMainWindow()) asked.Add(rp.Pid); } catch { } }
                    open.Add(new KeyValuePair<RunningProcess, Process>(rp, p));
                }
                catch (ArgumentException) { if (p != null) p.Dispose(); }       // ya terminó
                catch (Exception ex) { Log.Warn("No se pudo cerrar " + rp.Name + ": " + ex.Message); if (p != null) p.Dispose(); still.Add(rp); }
            }
            // A los que se les pidió cerrarse se les da tiempo para terminar solos: mientras sigan con
            // ventana (pueden estar preguntando si guardar), hasta el plazo; si la ventana ya no está
            // (se cerró o se fue al área de notificación), la mitad del plazo.
            var start = DateTime.UtcNow;
            var deadline = start.AddMilliseconds(Math.Max(0, graceMs));
            var half = start.AddMilliseconds(Math.Max(0, graceMs) / 2);
            while (DateTime.UtcNow < deadline)
            {
                bool wait = false;
                foreach (var kv in open)
                {
                    if (!asked.Contains(kv.Key.Pid) || HasExited(kv.Value)) continue;
                    if (HasWindow(kv.Value) || DateTime.UtcNow < half) { wait = true; break; }
                }
                if (!wait) break;
                Thread.Sleep(500);
            }
            foreach (var kv in open)
            {
                var p = kv.Value;
                try
                {
                    if (HasExited(p)) continue;
                    if (HasWindow(p)) { still.Add(kv.Key); continue; }        // tiene ventana y no se cerró: puede estar preguntando si guardar
                    p.Kill();
                    if (!p.WaitForExit(5000)) still.Add(kv.Key);
                }
                catch (Exception ex)
                {
                    if (!HasExited(p)) { Log.Warn("No se pudo terminar " + kv.Key.Name + ": " + ex.Message); still.Add(kv.Key); }
                }
                finally { p.Dispose(); }
            }
            if (open.Count > 0) Thread.Sleep(1000);   // que Windows libere los archivos que tenían abiertos
            return still;
        }

        static bool HasExited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }

        static bool HasWindow(Process p)
        {
            try { p.Refresh(); return p.MainWindowHandle != IntPtr.Zero; } catch { return false; }
        }

        // ------------------------------------------------------------------ Para el usuario

        /// <summary>
        /// Texto de la pista cuando el instalador falló y el programa estaba abierto (no se cerró porque
        /// el usuario no lo pidió). Cubre el caso de Epic Games Launcher: abierto en segundo plano, sin ventana.
        /// </summary>
        public static string OpenHint(string names)
        {
            return "Estaba abierto mientras se instalaba (" + names + "), aunque fuera en segundo plano y sin ventana: " +
                   "es la causa más común de este error. Cerralo (también desde el área de notificación) o aceptá cerrarlo cuando Actualizador lo pregunte, y reintentá";
        }
    }
}
