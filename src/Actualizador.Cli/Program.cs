// Actualizador.Cli.exe: el actualizador desde la consola, para scripts, RMM y tareas de despliegue.
//
//   Actualizador.Cli buscar                      busca y muestra qué hay para actualizar
//   Actualizador.Cli actualizar --tipos programas,controladores
//   Actualizador.Cli politica | verificar-registro | historial | ignorar … | programar …
//
// Códigos de salida pensados para automatizar: 0 todo al día / todo instalado, 10 hay actualizaciones pendientes,
// 3 alguna actualización falló, 4 cancelado, 5 bloqueado por la política, 1 argumentos inválidos, 2 error fatal.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Updates;

namespace Actualizador.Cli
{
    public static class Program
    {
        public const int ExitOk = 0, ExitArgs = 1, ExitFatal = 2, ExitUpdateErrors = 3, ExitCancelled = 4, ExitPolicy = 5, ExitPending = 10;

        public enum Command { Buscar, Actualizar, Historial, Ignorar, DejarDeIgnorar, Ignorados, Programar, Politica, VerificarRegistro, Diagnostico, Version, Ayuda }

        public sealed class Args
        {
            public Command Command = Command.Buscar;
            public readonly List<string> Values = new List<string>();
            public HashSet<string> Kinds;
            public bool Quiet, Betas, NoVendors, NoWingetSetup, OneRound, CloseRunning, NoRestorePoint, Interactive, IncludePages;
            /// <summary>--json / --csv: ruta del archivo ("-" = la salida estándar); null si no se pidió.</summary>
            public string Json, Csv;
            public string Time, Day;
            public int HistoryCount = 30;
        }

        static readonly string[] KindNames = { "todas", "todo", "programas", "controladores", "store", "windows" };

        [DllImport("kernel32.dll")]
        static extern uint GetConsoleProcessList(uint[] processList, uint count);

        /// <summary>true si la consola es sólo de este proceso (doble clic desde el Explorador): se espera una tecla al final.</summary>
        static bool OwnConsole()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            try { return GetConsoleProcessList(new uint[4], 4) == 1; }
            catch { return false; }
        }

        public static int Main(string[] argv)
        {
            AppInfo.HardenProcess();
            bool own = OwnConsole();
            int rc;
            try { rc = MainCore(argv); }
            catch (Exception ex)
            {
                Log.Error("Error fatal", ex);
                try { Console.Error.WriteLine("Error fatal: " + ex.Message); } catch { }
                rc = ExitFatal;
            }
            if (own)
            {
                try { Console.Error.WriteLine(); Console.Error.Write("Presioná una tecla para cerrar esta ventana…"); Console.ReadKey(true); }
                catch { }
            }
            return rc;
        }

        static int MainCore(string[] argv)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            Log.Init(AppInfo.LogDirectory, "cli");
            if (AppInfo.PortableNote != null) Log.Warn(AppInfo.PortableNote);
            TextEncodings.EnsureRegistered();   // páginas de códigos de Windows (850, 1252…) en .NET 8
            Args a;
            string error;
            if (!Parse(argv, out a, out error))
            {
                Console.Error.WriteLine("Error: " + error);
                Console.Error.WriteLine("Usá «Actualizador.Cli ayuda» para ver los comandos.");
                return ExitArgs;
            }
            try
            {
                switch (a.Command)
                {
                    case Command.Ayuda: PrintHelp(); return ExitOk;
                    case Command.Version:
                        Console.WriteLine(AppInfo.Name + " " + AppInfo.Version + (AppInfo.IsPortable ? " (portable)" : ""));
                        if (AppInfo.IsPortable) Console.WriteLine("Datos: " + AppInfo.DataDirectory + (AppInfo.PortableNote != null ? Environment.NewLine + AppInfo.PortableNote : ""));
                        return ExitOk;
                    case Command.Politica: return ShowPolicy();
                    case Command.VerificarRegistro: return VerifyTrail();
                    case Command.Historial:
                        Console.WriteLine(UpdateHistory.ToText(UpdateHistory.Load(), a.HistoryCount).TrimEnd());
                        Console.WriteLine();
                        Console.WriteLine("Archivo: " + UpdateHistory.DefaultPath);
                        return ExitOk;
                    case Command.Ignorar: case Command.DejarDeIgnorar: case Command.Ignorados: return ManageIgnored(a);
                    case Command.Programar: return Schedule(a);
                    default: return RunUpdates(a);
                }
            }
            catch (IOException) { return ExitOk; }   // la salida se cerró antes de tiempo («| more» cancelado)
            catch (OperationCanceledByUserException) { Console.Error.WriteLine("Cancelado."); return ExitCancelled; }
        }

        // ================================================================== Análisis de argumentos
        /// <summary>
        /// Comando (la primera palabra) y opciones. Acepta «--opcion valor» y «--opcion=valor». Sin comando: «buscar».
        /// </summary>
        public static bool Parse(string[] argv, out Args a, out string error)
        {
            a = new Args();
            error = null;
            int i = 0;
            if (argv.Length > 0 && !argv[0].StartsWith("-", StringComparison.Ordinal) && !argv[0].StartsWith("/", StringComparison.Ordinal))
            {
                Command c;
                if (!TryCommand(argv[0], out c)) { error = "Comando desconocido: «" + argv[0] + "»."; return false; }
                a.Command = c;
                i = 1;
            }
            for (; i < argv.Length; i++)
            {
                string arg = argv[i];
                if (!arg.StartsWith("-", StringComparison.Ordinal) && !arg.StartsWith("/", StringComparison.Ordinal)) { a.Values.Add(arg); continue; }
                string key = arg, value = null;
                int eq = arg.IndexOf('=');
                if (eq > 0) { key = arg.Substring(0, eq); value = arg.Substring(eq + 1); }
                key = key.ToLowerInvariant();
                if (key.StartsWith("/", StringComparison.Ordinal)) key = "--" + key.Substring(1);
                Func<bool, string> next = delegate(bool required)
                {
                    if (value != null) return value;
                    if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal)) return argv[++i];
                    return required ? null : "";
                };
                switch (key)
                {
                    case "--ayuda": case "--help": case "-h": case "-?": case "--?": a.Command = Command.Ayuda; break;
                    case "--version": a.Command = Command.Version; break;
                    case "--silencioso": case "--quiet": case "-q": a.Quiet = true; break;
                    case "--sin-fabricantes": a.NoVendors = true; break;
                    case "--betas": a.Betas = true; break;
                    case "--sin-instalar-winget": a.NoWingetSetup = true; break;
                    case "--una-vuelta": a.OneRound = true; break;
                    case "--cerrar-programas": a.CloseRunning = true; break;
                    case "--sin-punto-restauracion": a.NoRestorePoint = true; break;
                    case "--interactivo": a.Interactive = true; break;
                    case "--incluir-paginas": a.IncludePages = true; break;
                    case "--json": a.Json = NonEmpty(next(false), "-"); break;
                    case "--csv":
                        a.Csv = next(true);
                        if (string.IsNullOrEmpty(a.Csv)) { error = "--csv necesita la ruta del archivo."; return false; }
                        break;
                    case "--hora":
                        a.Time = next(true);
                        int h, m;
                        if (a.Time == null || !ScheduledReview.TryParseTime(a.Time, out h, out m)) { error = "--hora necesita una hora válida (HH:MM)."; return false; }
                        break;
                    case "--dia":
                        a.Day = next(true);
                        if (a.Day == null || ScheduledReview.ParseDay(a.Day) == null) { error = "--dia necesita un día de la semana (lunes … domingo)."; return false; }
                        break;
                    case "--tipos": case "--aplicar":
                    {
                        string v = next(true);
                        if (string.IsNullOrEmpty(v)) { error = key + " necesita los tipos: " + string.Join(", ", KindNames, 2, 4) + " o todas."; return false; }
                        a.Kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string part in v.Split(','))
                        {
                            string p = part.Trim().ToLowerInvariant();
                            if (p.Length == 0) continue;
                            if (Array.IndexOf(KindNames, p) < 0) { error = "Tipo desconocido: «" + p + "» (programas, controladores, store, windows o todas)."; return false; }
                            a.Kinds.Add(p);
                        }
                        if (key == "--aplicar" && a.Command == Command.Buscar) a.Command = Command.Actualizar;
                        break;
                    }
                    default:
                        error = "Opción desconocida: " + arg;
                        return false;
                }
            }
            return Validate(a, out error);
        }

        static string NonEmpty(string s, string fallback) { return string.IsNullOrEmpty(s) ? fallback : s; }

        static bool TryCommand(string word, out Command c)
        {
            switch (word.ToLowerInvariant())
            {
                case "buscar": case "scan": c = Command.Buscar; return true;
                case "actualizar": case "update": case "instalar": c = Command.Actualizar; return true;
                case "historial": case "history": c = Command.Historial; return true;
                case "ignorar": case "ignore": c = Command.Ignorar; return true;
                case "dejar-de-ignorar": case "unignore": c = Command.DejarDeIgnorar; return true;
                case "ignorados": case "ignored": c = Command.Ignorados; return true;
                case "programar": case "schedule": c = Command.Programar; return true;
                case "politica": case "política": case "policy": c = Command.Politica; return true;
                case "verificar-registro": case "verify-log": c = Command.VerificarRegistro; return true;
                case "diagnostico": case "diagnóstico": case "diagnostic": c = Command.Diagnostico; return true;
                case "version": case "versión": c = Command.Version; return true;
                case "ayuda": case "help": c = Command.Ayuda; return true;
            }
            c = Command.Ayuda;
            return false;
        }

        static bool Validate(Args a, out string error)
        {
            error = null;
            switch (a.Command)
            {
                case Command.Ignorar: case Command.DejarDeIgnorar:
                    if (a.Values.Count == 0) { error = "Indicá qué ignorar: «nombre o Id[@versión]» (por ejemplo: ignorar TeamViewer.TeamViewer@15.58.4)."; return false; }
                    return true;
                case Command.Programar:
                    if (a.Values.Count > 1) { error = "programar acepta diaria, semanal, no o nada (para ver el estado)."; return false; }
                    if (a.Values.Count == 1 && Array.IndexOf(new[] { "diaria", "semanal", "no" }, a.Values[0].ToLowerInvariant()) < 0)
                    { error = "programar: «" + a.Values[0] + "» no es diaria, semanal ni no."; return false; }
                    return true;
                case Command.Historial:
                    if (a.Values.Count == 1)
                    {
                        int n;
                        if (!int.TryParse(a.Values[0], out n) || n < 1 || n > 10000) { error = "historial acepta la cantidad de entradas (1 a 10000)."; return false; }
                        a.HistoryCount = n;
                        return true;
                    }
                    break;
                case Command.Diagnostico:
                    if (a.Values.Count <= 1) return true;
                    break;
            }
            if (a.Values.Count > 0) { error = "Argumento de más: «" + a.Values[0] + "»."; return false; }
            if (a.Command != Command.Actualizar && (a.Kinds != null || a.CloseRunning || a.NoRestorePoint || a.Interactive || a.OneRound || a.IncludePages))
            { error = "--tipos, --cerrar-programas, --sin-punto-restauracion, --interactivo, --una-vuelta e --incluir-paginas son del comando «actualizar»."; return false; }
            return true;
        }

        // ================================================================== Buscar y actualizar
        static int RunUpdates(Args a)
        {
            var policy = Policy.Current;
            var options = new UpdateOptions();
            options.QueryVendors = !a.NoVendors;
            options.IncludePreReleases = a.Betas;
            options.InstallWinget = !a.NoWingetSetup;
            var ctx = UpdateContext.Create(options);
            if (!a.Quiet) ctx.Progress += delegate(string m) { Console.Error.WriteLine("  " + m); };
            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
            {
                if (ctx.CancelRequested) return;    // el segundo Ctrl+C termina el proceso
                e.Cancel = true;
                ctx.RequestCancel();
                Console.Error.WriteLine("Cancelando… (Ctrl+C otra vez para salir ya)");
            };

            Console.Error.WriteLine(AppInfo.Title + " " + AppInfo.Version);
            Console.Error.WriteLine(ctx.Os.FriendlyName + (ctx.IsAdmin ? " — administrador" : " — SIN privilegios de administrador (controladores y Windows Update no se instalan)"));
            if (policy.Origin != "ninguna") Console.Error.WriteLine("Política de la empresa: " + policy.Origin);
            if (a.Command == Command.Actualizar && policy.RequireAdministrator && !ctx.IsAdmin)
            {
                Console.Error.WriteLine("La política de la empresa exige ejecutar las actualizaciones como administrador.");
                return ExitPolicy;
            }

            var report = UpdateSession.Create(ctx);
            var manager = new UpdateManager();
            manager.Via = "consola";
            manager.Interactive = a.Interactive && policy.AllowInteractiveInstallers;
            manager.CloseRunning = a.CloseRunning;
            manager.Scan(ctx, report);
            if (ctx.CancelRequested) return ExitCancelled;

            int exit = ExitOk;
            if (a.Command == Command.Actualizar)
            {
                var selected = new List<UpdateCandidate>();
                foreach (var u in report.Updates)
                    if (u.Recommended && UpdateManager.CanInstall(u) && UpdateManager.MatchesKind(u, a.Kinds) &&
                        (a.IncludePages || u.ProviderName != SiteProvider.ProviderId))   // lo que sólo abre su página, sólo si se pide
                        selected.Add(u);
                Console.Error.WriteLine("Se van a actualizar " + Fmt.Plural(selected.Count, "elemento", "elementos") + ".");
                if (!ctx.IsAdmin)
                {
                    int equipo = UpdateManager.ProgramsNeedingAdmin(ctx, selected).Count;
                    if (equipo > 0)
                        Console.Error.WriteLine("AVISO: " + Fmt.Plural(equipo, "programa está instalado", "programas están instalados") +
                                                " para todo el equipo y esta consola no es de administrador: sus instaladores MSI van a fallar.");
                }
                manager.RepeatUntilDone = !a.OneRound;
                var tried = manager.InstallUntilDone(ctx, report, selected, !a.NoRestorePoint || policy.RequireRestorePoint, delegate(UpdateCandidate c)
                {
                    if (!a.Quiet && c.Status != UpdateStatus.Pendiente)
                        Console.Error.WriteLine("  [" + UpdateCandidate.StatusText(c.Status) + "] " + c.Name + (string.IsNullOrEmpty(c.Result) ? "" : " — " + c.Result));
                }, delegate(int n)
                {
                    if (!a.Quiet) Console.Error.WriteLine("Vuelta " + (n + 1) + ": se buscó de nuevo después de instalar.");
                });
                var tally = InstallTally.Of(tried);
                Console.WriteLine(tally.Text());
                if (tally.Failed > 0) exit = ExitUpdateErrors;
                if (ctx.CancelRequested) exit = ExitCancelled;
            }

            var snapshot = new List<UpdateCandidate>(report.Updates);
            if (a.Command == Command.Diagnostico) return SaveDiagnostic(a, report, snapshot, ctx);
            if (a.Json == null || a.Json != "-") PrintSummary(report, snapshot);
            if (!WriteExports(a, snapshot, ctx.Os)) exit = exit == ExitOk ? ExitFatal : exit;
            if (a.Command == Command.Buscar && exit == ExitOk && UpdateGroups.Count(snapshot).ToUpdate > 0) exit = ExitPending;
            return exit;
        }

        static int SaveDiagnostic(Args a, UpdateSession report, List<UpdateCandidate> updates, UpdateContext ctx)
        {
            string target = a.Values.Count == 1 ? a.Values[0] : Environment.CurrentDirectory;
            string zip = Directory.Exists(target) || target.EndsWith("\\") || target.EndsWith("/")
                ? Path.Combine(target, DiagnosticPackage.DefaultFileName(DateTime.Now)) : target;
            try
            {
                var extra = new Dictionary<string, string>();
                extra["historial.txt"] = UpdateHistory.ToText(UpdateHistory.Load(), 500);
                extra["politica.txt"] = Policy.Current.Describe();
                List<string> notes;
                lock (report.UpdateNotes) notes = new List<string>(report.UpdateNotes);
                int logs = DiagnosticPackage.Create(zip, updates, notes, ctx.Os.FriendlyName + (ctx.IsAdmin ? " — administrador" : " — sin administrador") +
                                                    " · " + AppInfo.Name + " " + AppInfo.Version, AppInfo.LogDirectory, extra);
                Console.WriteLine("Diagnóstico: " + Path.GetFullPath(zip) + " (" + Fmt.Plural(logs, "registro", "registros") + ")");
                return ExitOk;
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo guardar el diagnóstico", ex);
                Console.Error.WriteLine("No se pudo guardar el diagnóstico en " + zip + ": " + ex.Message);
                return ExitFatal;
            }
        }

        static bool WriteExports(Args a, List<UpdateCandidate> updates, OsInfo os)
        {
            bool ok = true;
            if (a.Json == "-") Console.WriteLine(UpdateExport.ToJson(updates, os));
            else if (a.Json != null) ok &= Write(a.Json, updates, os, UpdateExportFormat.Json);
            if (a.Csv != null) ok &= Write(a.Csv, updates, os, UpdateExportFormat.Csv);
            return ok;
        }

        static bool Write(string path, List<UpdateCandidate> updates, OsInfo os, UpdateExportFormat format)
        {
            try
            {
                if (Directory.Exists(path)) path = Path.Combine(path, UpdateExport.DefaultFileName(DateTime.Now, format));
                UpdateExport.Write(path, updates, os, format);
                Console.Error.WriteLine((format == UpdateExportFormat.Json ? "JSON: " : "CSV: ") + Path.GetFullPath(path));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo escribir " + path, ex);
                Console.Error.WriteLine("No se pudo escribir " + path + ": " + ex.Message);
                return false;
            }
        }

        static void PrintSummary(UpdateSession r, List<UpdateCandidate> updates)
        {
            var k = UpdateGroups.Count(updates);
            Console.WriteLine();
            Console.WriteLine(UpdateGroups.Title(k));
            string sub = UpdateGroups.Subtitle(k);
            if (sub.Length > 0) Console.WriteLine(sub);
            foreach (var u in updates)
            {
                if (UpdateGroups.Of(u) != UpdateGroup.ParaActualizar && u.Status != UpdateStatus.Fallida) continue;
                Console.WriteLine("  " + Pad(UpdateCandidate.KindText(u.Kind), 16) + Pad(Fmt.Truncate(u.Name, 48), 50) +
                                  Pad(Fmt.Truncate(u.InstalledVersion ?? "", 20), 22) + "→ " + Fmt.Truncate(u.AvailableVersion ?? "", 24) +
                                  (u.Status == UpdateStatus.Fallida ? "  [falló: " + Fmt.Truncate(u.Result ?? "", 80) + "]" : ""));
            }
            lock (r.UpdateNotes) foreach (string n in r.UpdateNotes) Console.WriteLine("  · " + n);
        }

        static string Pad(string s, int width) { s = s ?? ""; return s.Length >= width ? s + " " : s.PadRight(width); }

        // ================================================================== Administración
        static int ShowPolicy()
        {
            Policy.Reset();
            var p = Policy.Current;
            Console.WriteLine(p.Describe());
            return p.Problems.Count > 0 ? ExitPolicy : ExitOk;
        }

        static int VerifyTrail()
        {
            var trail = AuditTrail.Open();
            var r = trail.Verify();
            Console.WriteLine(r.Ok ? "Registro de cambios íntegro: " + Fmt.Plural(r.Entries, "entrada", "entradas") + "." : "Registro de cambios NO íntegro: " + r.Problem);
            Console.WriteLine("Carpeta: " + trail.Directory);
            return r.Ok ? ExitOk : ExitFatal;
        }

        static int ManageIgnored(Args a)
        {
            var list = IgnoreList.Load();
            bool changed = false;
            if (a.Command == Command.Ignorar)
                foreach (string t in a.Values)
                {
                    var r = IgnoreList.RuleFromText(t, DateTime.Now);
                    if (r != null && list.Add(r)) { changed = true; Console.WriteLine("Ignorada: " + r.Describe()); }
                }
            if (a.Command == Command.DejarDeIgnorar)
                foreach (string t in a.Values)
                {
                    int n = list.RemoveByText(t);
                    changed |= n > 0;
                    Console.WriteLine(n > 0 ? "Se vuelve a ofrecer: «" + t + "»." : "No había ninguna regla para «" + t + "».");
                }
            if (changed && !list.Save()) { Console.Error.WriteLine("No se pudo guardar la lista en " + IgnoreList.DefaultPath); return ExitFatal; }
            if (a.Command == Command.Ignorados)
            {
                Console.WriteLine(list.Rules.Count == 0 ? "No hay actualizaciones ignoradas." : "Actualizaciones ignoradas (" + list.Rules.Count + "):");
                foreach (var r in list.Rules)
                    Console.WriteLine("  " + r.Describe() + (string.IsNullOrEmpty(r.Kind) ? "" : " · " + r.Kind) + (string.IsNullOrEmpty(r.Id) || r.Id == r.Name ? "" : " · " + r.Id) +
                                      (r.Added > DateTime.MinValue ? " · desde el " + Fmt.Date(r.Added.Date) : ""));
            }
            return ExitOk;
        }

        static int Schedule(Args a)
        {
            string what = a.Values.Count == 1 ? a.Values[0].ToLowerInvariant() : "";
            if (what == "no")
            {
                string err = ScheduledReview.Unregister();
                Console.WriteLine(err == null ? "Revisión programada desactivada." : "No se pudo desactivar la revisión programada: " + err);
                if (err != null) return ExitFatal;
            }
            else if (what.Length > 0)
            {
                var sch = new ReviewSchedule();
                sch.Frequency = what == "diaria" ? ReviewFrequency.Diaria : ReviewFrequency.Semanal;
                int h, m;
                if (a.Time != null && ScheduledReview.TryParseTime(a.Time, out h, out m)) { sch.Hour = h; sch.Minute = m; }
                if (a.Day != null) sch.Day = ScheduledReview.ParseDay(a.Day).Value;
                string err = ScheduledReview.Register(sch);
                Console.WriteLine(err == null ? "Revisión programada: " + sch.Describe() + ". Busca actualizaciones (sin instalar nada) y avisa junto al reloj si hay."
                                              : "No se pudo programar la revisión: " + err);
                if (err != null) return ExitFatal;
            }
            Console.WriteLine("Estado: " + ScheduledReview.Query().Describe());
            return ExitOk;
        }

        // ================================================================== Ayuda
        static void PrintHelp()
        {
            Console.WriteLine(AppInfo.Title + " " + AppInfo.Version + " — consola");
            Console.WriteLine(@"
Uso: Actualizador.Cli <comando> [opciones]

Comandos
  buscar                 Busca actualizaciones de programas, controladores, Windows y Microsoft Store
                         (no instala nada). Es el comando si no se indica otro.
  actualizar             Busca e instala lo recomendado.
  historial [n]          Las últimas n instalaciones (30 si no se indica).
  ignorar <texto>        Deja de ofrecer una actualización: «nombre o Id[@versión]».
  dejar-de-ignorar <t>   Vuelve a ofrecerla.
  ignorados              Lista lo ignorado.
  programar [diaria|semanal|no] [--hora HH:MM] [--dia lunes]
                         Revisión automática que avisa junto al reloj (sin instalar). Sin valor: el estado.
  politica               Muestra la política de la empresa vigente (GPO o politica.json).
  verificar-registro     Verifica la integridad (HMAC) del registro de cambios.
  diagnostico [ruta]     Guarda un .zip con la última búsqueda y los registros (para soporte).
  version | ayuda

Opciones de buscar y actualizar
  --json [archivo]       Resultado en JSON (sin archivo: a la salida estándar).
  --csv <archivo>        Resultado en CSV (Excel).
  --sin-fabricantes      Sólo winget, Windows Update y Microsoft Store (sin páginas oficiales ni catálogo).
  --betas                Ofrecer también las betas que publica el fabricante (opcionales; abren su página).
  --sin-instalar-winget  No instalar winget si falta.
  --silencioso           Sin mensajes de progreso.

Opciones de actualizar
  --tipos <lista>        programas, controladores, store, windows o todas (por defecto: todas).
  --cerrar-programas     Cierra los programas abiertos que se actualizan (si no, se saltean).
  --sin-punto-restauracion
  --interactivo          Muestra la ventana de los instaladores que no tienen modo silencioso.
  --una-vuelta           No volver a buscar después de instalar.
  --incluir-paginas      También abre la página de descarga de lo que no se instala solo.

Códigos de salida
  0 todo al día / todo instalado   10 hay actualizaciones pendientes (buscar)
  3 alguna actualización falló     4 cancelado   5 bloqueado por la política
  1 argumentos inválidos           2 error

Ejemplos
  Actualizador.Cli buscar --json C:\Inventario\
  Actualizador.Cli actualizar --tipos programas --cerrar-programas
  Actualizador.Cli programar semanal --dia viernes --hora 12:30");
        }
    }
}
