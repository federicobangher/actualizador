using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    /// <summary>
    /// Programas (y apps de la Store que winget reconoce en el origen msstore).
    /// winget usa manifiestos firmados con hash SHA-256 del instalador: no se descarga
    /// ningún instalador "suelto" desde sitios arbitrarios.
    /// </summary>
    public sealed partial class WingetProvider : IUpdateProvider
    {
        public const string ProviderId = "winget";
        string exe;
        string version;

        /// <summary>Muestra las ventanas del instalador en vez de instalar en silencio.</summary>
        public bool Interactive;

        public string Name { get { return ProviderId; } }

        public string Unavailable(UpdateContext ctx)
        {
            if (ctx.Os == null || !ctx.Os.IsWindows10OrLater || (!ctx.Os.IsServer && ctx.Os.Build < 17763))
                return "winget requiere Windows 10 1809 o posterior (o Windows Server 2025). En este sistema los programas se listan como «No verificable».";
            var candidates = SoftwareCollectors.WingetCandidates();
            if (candidates.Count == 0)
            {
                string why = WingetSetup.CannotInstall(ctx);
                return "winget (App Installer) no está instalado: sin él, los programas no se pueden comparar ni actualizar." +
                       (why != null ? " " + why : "") + " También se instala desde https://aka.ms/getwinget o desde Microsoft Store («Instalador de aplicación»).";
            }
            ProcResult r = null;
            foreach (string candidate in candidates)
            {
                r = ProcessRunner.Run(candidate, "--version", 30000, new UTF8Encoding(false));
                if (r.Ok) { exe = candidate; version = r.Out.Trim().TrimStart('v'); Log.Info("winget: " + candidate); return null; }
                Log.Warn("winget en " + candidate + " no respondió: " + r.Describe());
            }
            return "winget no responde: " + r.Describe();
        }

        bool Modern { get { return VersionUtil.Compare(version, "1.4") >= 0; } }

        string CommonArgs
        {
            get { return " --accept-source-agreements" + (Modern ? " --disable-interactivity" : ""); }
        }

        ProcResult RunWinget(string args, int timeoutMs, Action<string> onLine)
        {
            return RunWinget(args, timeoutMs, onLine, null);
        }

        /// <summary>
        /// Ejecuta winget. Si no arranca (la ruta ya no existe o está en reemplazo), se vuelve a buscar
        /// winget y se reintenta una vez: al actualizar App Installer, Windows borra la carpeta de la
        /// versión anterior (Microsoft.DesktopAppInstaller_1.29.379.0_…) y todas las llamadas
        /// siguientes fallaban con «El sistema no puede encontrar el archivo especificado».
        /// </summary>
        ProcResult RunWinget(string args, int timeoutMs, Action<string> onLine, Func<string, string> abortCheck)
        {
            EnsureExe();
            Log.Info("winget " + args);
            var r = ProcessRunner.Run(exe, args, timeoutMs, new UTF8Encoding(false), onLine, null, abortCheck);
            if (!r.Started && RefreshExe("no arrancó (" + r.StartError + ")"))
            {
                System.Threading.Thread.Sleep(1000);
                r = ProcessRunner.Run(exe, args, timeoutMs, new UTF8Encoding(false), onLine, null, abortCheck);
            }
            Log.Info("winget terminó: " + r.Describe());
            return r;
        }

        /// <summary>Si la ruta de winget ya no existe, se busca otra vez.</summary>
        void EnsureExe()
        {
            if (string.IsNullOrEmpty(exe) || exe == "winget") return;              // pruebas
            bool exists;
            try { exists = File.Exists(exe); } catch { exists = false; }
            if (!exists) RefreshExe("la ruta ya no existe");
        }

        /// <summary>
        /// Vuelve a buscar winget (otra carpeta de App Installer). true si encontró uno que responde.
        /// Recién actualizado, el paquete nuevo puede tardar unos segundos en registrarse: hasta 3 rondas.
        /// </summary>
        internal bool RefreshExe(string why)
        {
            if (exe == "winget") return false;                                      // pruebas
            // Hay consultas en paralelo: una sola busca de nuevo y las demás usan lo que encontró.
            lock (exeGate) return RefreshExeCore(why);
        }

        bool RefreshExeCore(string why)
        {
            Log.Info("winget: " + (exe ?? "") + " " + why + "; se busca de nuevo…");
            for (int round = 0; round < 3; round++)
            {
                if (round > 0) System.Threading.Thread.Sleep(3000);
                foreach (string candidate in SoftwareCollectors.WingetCandidates())
                {
                    var probe = ProcessRunner.Run(candidate, "--version", 30000, new UTF8Encoding(false));
                    if (!probe.Ok) continue;
                    exe = candidate;
                    version = probe.Out.Trim().TrimStart('v');
                    Log.Info("winget: " + candidate + " (" + version + ")");
                    return true;
                }
            }
            Log.Warn("winget: no se encontró otro winget que responda");
            return false;
        }

        /// <summary>Sólo para las pruebas: responde a las consultas de la búsqueda (upgrade, list) en vez de winget.</summary>
        internal Func<string, ProcResult> QueryForTests;
        readonly object testGate = new object();
        readonly object exeGate = new object();

        /// <summary>Consulta a winget (no instala): en las pruebas responde <see cref="QueryForTests"/>.</summary>
        ProcResult RunQuery(string args, int timeoutMs)
        {
            if (QueryForTests != null)
            {
                // Las respuestas simuladas se piden de a una (las consultas reales pueden correr en paralelo).
                lock (testGate)
                {
                    Log.Info("winget " + args);
                    return QueryForTests(args) ?? new ProcResult();
                }
            }
            return RunWinget(args, timeoutMs, null);
        }

        /// <summary>
        /// «winget upgrade» que no devolvió ninguna tabla y terminó con error (no «no hay resultados»):
        /// el winget elegido no puede abrir sus orígenes. Pasa con el winget.exe de WindowsApps
        /// ejecutado sin la identidad del paquete en algunos equipos.
        /// </summary>
        static bool SourcesBroken(ProcResult r, int rows)
        {
            return r != null && r.Started && !r.TimedOut && rows == 0 && !r.Ok && !IsNoResults(r.ExitCode);
        }

        /// <summary>Códigos de winget de orígenes dañados o que no se pueden abrir.</summary>
        public static bool IsSourceErrorCode(uint code)
        {
            return code == 0x8A15000A      // INDEX_INTEGRITY_COMPROMISED
                || code == 0x8A15000B      // SOURCES_INVALID
                || code == 0x8A15000F      // SOURCE_DATA_MISSING
                || code == 0x8A150015;     // NO_SOURCES_DEFINED
        }

        /// <summary>¿winget dice que sus orígenes están dañados (por el código o porque sugiere «source reset»)?</summary>
        public static bool IsSourceProblem(ProcResult r)
        {
            if (r == null || !r.Started || r.TimedOut) return false;
            if (IsSourceErrorCode(unchecked((uint)r.ExitCode))) return true;
            string text = (r.Out ?? "") + " " + (r.Err ?? "");
            return text.IndexOf("source reset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Data required by the source is missing", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Repara los orígenes de winget: primero «source update» (no borra nada); si sigue mal y hay administrador,
        /// «source reset» SÓLO del origen oficial «winget» (los orígenes propios de una empresa no se tocan). true si
        /// winget quedó respondiendo.
        /// </summary>
        bool RepairSources(UpdateContext ctx, List<string> notes)
        {
            ctx.Report("Los orígenes de winget están dañados; se actualizan y se vuelve a buscar…");
            Log.Warn("winget: orígenes dañados; se ejecuta «winget source update»");
            var u = RunQuery("source update" + CommonArgs, 300000);
            if (u.Ok)
            {
                notes.Add("Los orígenes de winget estaban dañados: se actualizaron («winget source update») y se buscó de nuevo.");
                return true;
            }
            if (!ctx.IsAdmin)
            {
                notes.Add("Los orígenes de winget están dañados y «winget source update» no alcanzó (" + u.Describe() + "). " +
                          "Abrí Actualizador como administrador para repararlos, o ejecutá en una consola de administrador: winget source reset --force");
                return false;
            }
            Log.Warn("winget: «source update» no alcanzó (" + u.Describe() + "); se restablece el origen «winget»");
            var reset = RunQuery("source reset --name winget --force" + CommonArgs, 300000);
            var again = RunQuery("source update --name winget" + CommonArgs, 300000);
            if (reset.Ok || again.Ok)
            {
                notes.Add("Los orígenes de winget estaban dañados: se restableció el origen oficial «winget» y se buscó de nuevo.");
                return true;
            }
            notes.Add("Los orígenes de winget están dañados y no se pudieron reparar (" + reset.Describe() + "). En una consola de administrador: winget source reset --force");
            return false;
        }

        /// <summary>Prueba con otro winget instalado (el alias del usuario o el del paquete). true si cambió.</summary>
        bool SwitchToOtherWinget()
        {
            if (QueryForTests != null || exe == "winget") return false;
            string current = exe;
            foreach (string candidate in SoftwareCollectors.WingetCandidates())
            {
                if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase)) continue;
                var probe = ProcessRunner.Run(candidate, "--version", 30000, new UTF8Encoding(false));
                if (!probe.Ok) continue;
                exe = candidate;
                version = probe.Out.Trim().TrimStart('v');
                Log.Info("winget: se cambia a " + candidate + " (" + version + ")");
                return true;
            }
            return false;
        }

        public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes)
        {
            ctx.Report("winget " + version + ": buscando actualizaciones de programas…");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            // «winget list» corre AL MISMO TIEMPO que «winget upgrade» (no dependen una de la otra y cada una tarda);
            // antes una esperaba a la otra.
            ProcResult listEarly = null;
            var listThread = new System.Threading.Thread(delegate()
            {
                ProcessRunner.LowPriorityForThisThread = true;
                try { listEarly = RunQuery("list" + CommonArgs, 600000); }
                catch (Exception ex) { Log.Warn("winget list: " + ex.Message); }
            });
            listThread.IsBackground = true;
            listThread.Name = "winget-list";
            listThread.Start();
            // 2.0.21: en modo liviano, un proceso de winget por vez (antes tres juntos: list, upgrade y el del equipo).
            if (Performance.Light) while (!listThread.Join(500)) ctx.ThrowIfCancelled();
            // 2.0.20: las apps de Windows para todos los usuarios (aprovisionadas), también al mismo tiempo.
            // 2.0.21: primero como servicio (SYSTEM), que es lo que ve el agente de Vigía; si no se puede, «--scope machine».
            ProcResult machineEarly = null;
            bool machineFromSystem = false;
            string systemProblem = null;
            System.Threading.Thread machineThread = null;
            if (QueriesMachineScope(ctx))
            {
                string machineArgs = MachineUpgradeArgs;
                string systemArgs = SystemUpgradeArgs;
                bool trySystem = QueryForTests == null || SystemWinget.RunForTests != null;
                machineThread = new System.Threading.Thread(delegate()
                {
                    ProcessRunner.LowPriorityForThisThread = true;
                    try
                    {
                        if (trySystem)
                        {
                            var s = SystemWinget.Run(ctx, systemArgs, 600000);
                            if (UsableTable(s)) { machineEarly = s; machineFromSystem = true; return; }
                            systemProblem = SystemWinget.Explain(s);
                            Log.Warn("winget como servicio no respondió (" + (s.Started ? s.Describe() : s.StartError) + "); se usa «--scope machine»");
                        }
                        machineEarly = RunQuery(machineArgs, 600000);
                    }
                    catch (Exception ex) { Log.Warn("winget upgrade (todo el equipo): " + ex.Message); }
                });
                machineThread.IsBackground = true;
                machineThread.Name = "winget-equipo";
                machineThread.Start();
                if (Performance.Light) while (!machineThread.Join(500)) ctx.ThrowIfCancelled();
            }
            bool redoList = false;
            string upgradeArgs = "upgrade" + (Modern ? " --include-unknown" : "") + CommonArgs;
            var up = RunQuery(upgradeArgs, 600000);
            string onlySource = "";
            if (up.TimedOut)
            {
                // El origen «msstore» consulta un servicio web por cada programa: con un proxy o un filtro que lo
                // demora, winget no termina nunca y antes se perdía la lista entera de programas. Se repite sólo
                // con el repositorio winget, que es local.
                Log.Warn("winget upgrade no respondió en 10 minutos; se repite sólo con el origen winget");
                ctx.Report("winget tarda demasiado con Microsoft Store; se busca sólo en el repositorio winget…");
                onlySource = " --source winget";
                up = RunQuery("upgrade" + (Modern ? " --include-unknown" : "") + onlySource + CommonArgs, 600000);
                if (up.TimedOut) throw new TimeoutException("winget upgrade no respondió en 10 minutos.");
                notes.Add("El origen «msstore» de winget no respondió: los programas se compararon sólo con el repositorio winget (las apps de la Store se revisan aparte).");
            }
            var upgrades = WingetTable.Parse(up.Out);
            if (SourcesBroken(up, upgrades.Count) && SwitchToOtherWinget())
            {
                Log.Warn("winget upgrade devolvió " + Fmt.Hex(up.ExitCode) + " sin tabla; se reintenta con " + exe);
                redoList = true;
                upgradeArgs = "upgrade" + (Modern ? " --include-unknown" : "") + onlySource + CommonArgs;
                up = RunQuery(upgradeArgs, 600000);
                if (up.TimedOut) throw new TimeoutException("winget upgrade no respondió en 10 minutos.");
                upgrades = WingetTable.Parse(up.Out);
            }
            // Orígenes de winget dañados («Data required by the source is missing», 0x8A15000F; «try the 'source reset'
            // command»): pasa en algunos equipos después de una actualización cortada de App Installer, y todos los programas
            // quedaban «no verificable» para siempre. Se reparan y se repite la búsqueda una vez.
            if (SourcesBroken(up, upgrades.Count) && IsSourceProblem(up) && RepairSources(ctx, notes))
            {
                redoList = true;
                upgradeArgs = "upgrade" + (Modern ? " --include-unknown" : "") + onlySource + CommonArgs;
                up = RunQuery(upgradeArgs, 600000);
                if (up.TimedOut) throw new TimeoutException("winget upgrade no respondió en 10 minutos.");
                upgrades = WingetTable.Parse(up.Out);
            }
            SaveRaw("winget-upgrade.txt", up);
            if (upgrades.Count == 0 && !up.Ok && !IsNoResults(up.ExitCode))
                notes.Add("winget upgrade devolvió " + Fmt.Hex(up.ExitCode) + ": " + Fmt.Truncate(up.Out + " " + up.Err, 300));

            ctx.ThrowIfCancelled();
            ctx.Report("winget: comparando con todo el software instalado…");
            while (!listThread.Join(500)) ctx.ThrowIfCancelled();
            ProcResult list;
            bool listOnlyWinget;
            if (redoList || listEarly == null)
            {
                // Se cambió de winget o se repararon sus orígenes: la lista que corrió en paralelo tuvo el mismo problema.
                list = RunQuery("list" + onlySource + CommonArgs, 600000);
                listOnlyWinget = onlySource.Length > 0;
            }
            else
            {
                list = listEarly;
                listOnlyWinget = false;
            }
            if (list.TimedOut && !listOnlyWinget)
            {
                Log.Warn("winget list no respondió en 10 minutos; se repite sólo con el origen winget");
                list = RunQuery("list --source winget" + CommonArgs, 600000);
                listOnlyWinget = true;
            }
            SaveRaw("winget-list.txt", list);
            var installed = WingetTable.Parse(list.Out);
            // Con --source winget no hay columna «Origen»: todas las filas son de ese repositorio.
            if (onlySource.Length > 0) FillSource(upgrades, "winget");
            if (listOnlyWinget) FillSource(installed, "winget");

            // winget anteriores a la 1.8 recortan las columnas a 120 caracteres cuando la salida no va a
            // una consola: con muchos programas de nombre o identificador largo, el Id quedaba
            // «TheDocumentFoundation.Libre…» y la fila no se podía instalar. Se completa con una consulta
            // del paquete solo (la tabla de una fila no se recorta).
            int resolved = ResolveTruncated(ctx, upgrades, installed);
            if (resolved > 0) Log.Info("winget: " + resolved + " identificadores recortados completados");

            var programs = ctx.Programs;
            var upgradeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in upgrades)
            {
                if (w.Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase)) continue;
                upgradeIds.Add(w.Id);
                var c = NewCandidate(w, programs);
                int? cmp = VersionUtil.Compare(w.Available, w.Version);
                c.Status = UpdateStatus.Disponible;
                c.Installable = !w.IdTruncated;
                c.Recommended = c.Installable && !w.RequiresExplicit;
                if (RobloxPlayer.Handles(w))
                    RobloxPlayer.Resolve(c, w, programs);
                else if (WingetTable.IsUnknownVersion(w.Version))
                    ResolveUnknown(c, w, programs);
                else if (cmp.HasValue && cmp.Value <= 0 && !WingetTable.IsTruncated(w.Available) && !WingetTable.IsTruncated(w.Version))
                    c.Detail = "winget la ofrece como actualización aunque la numeración no es mayor (cambio de canal o de instalador).";
                else
                    c.Detail = "Versión más nueva en el repositorio " + w.Source + ".";
                if (w.RequiresExplicit) c.Detail += " Está anclada o requiere selección explícita.";
                if (w.IdTruncated) c.Detail += " winget recortó el identificador; actualizala manualmente con «winget upgrade».";
                CheckFilesVersion(c, w, programs);
                output.Add(c);
            }

            foreach (var w in installed)
            {
                if (upgradeIds.Contains(w.Id) || w.Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase)) continue;
                var c = FromListRow(w, NewCandidate(w, programs));
                // 2.0.20: Roblox no registra su versión; si winget la lista con una disponible, se decide por su ejecutable.
                if (RobloxPlayer.Handles(w) && w.Source.Length > 0 && w.Available.Length > 0) RobloxPlayer.Resolve(c, w, programs);
                CheckFilesVersion(c, w, programs);
                output.Add(c);
            }
            if (machineThread != null)
            {
                while (!machineThread.Join(500)) ctx.ThrowIfCancelled();
                // 2.0.29: si no se pudo ver lo mismo que el agente de Vigía, se dice (antes quedaba sólo en el registro).
                if (!machineFromSystem && systemProblem != null)
                    notes.Add("winget como servicio (lo mismo que ejecuta el agente de Vigía) no funcionó en este equipo: " + systemProblem + ". Se usó «winget --scope machine»: " +
                              "lo «para todos los usuarios» puede no coincidir con lo que muestra Vigía.");
                if (machineEarly != null && machineEarly.Started && !machineEarly.TimedOut)
                {
                    SaveRaw(machineFromSystem ? "winget-upgrade-servicio.txt" : "winget-upgrade-equipo.txt", machineEarly);
                    var machineRows = WingetTable.Parse(machineEarly.Out);
                    FillSource(machineRows, "winget");
                    int machine = AddMachineRows(machineRows, upgrades, installed, output, machineFromSystem);
                    ApplyMachineFailures(output, MachineMemory.Snapshot.Open());
                    if (machine > 0)
                        notes.Add(Fmt.Plural(machine, "programa o app tiene", "programas o apps tienen") + " versión nueva sólo para la copia de todo el equipo " +
                                  (machineFromSystem ? "(lo que ve winget como servicio, igual que el agente de Vigía)" : "(paquetes aprovisionados, «winget --scope machine»)") +
                                  ": aparecen como «… (para todos los usuarios)».");
                    else Log.Info("winget " + (machineFromSystem ? "como servicio" : "--scope machine") + ": " + machineRows.Count + " filas, ninguna distinta de lo del usuario");
                }
                else if (machineEarly != null) Log.Warn("winget upgrade --scope machine: " + machineEarly.Describe());
            }
            var vistos = new List<WingetRow>(upgrades);
            vistos.AddRange(installed);
            int extra = AddMissingPrograms(programs, vistos, output);
            if (extra > 0)
                notes.Add(extra + (extra == 1 ? " programa instalado que winget no informa se agregó" : " programas instalados que winget no informa se agregaron") + " como «no verificable».");
            // los que winget no reconoce como instalados se buscan en su catálogo por nombre y editor.
            try { SearchUnrecognized(ctx, output, programs, notes); }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception ex) { Log.Error("Búsqueda por nombre en winget", ex); }
            if (installed.Count == 0)
                notes.Add("No se pudo leer «winget list»; sólo se muestran los programas con actualización.");
            notes.Add("Programas verificados con winget " + version + " (repositorios oficiales winget y msstore).");
            Log.Info("winget: búsqueda completa en " + Fmt.Duration(timer.Elapsed));
        }

        /// <summary>Versión del ejecutable (ProductVersion, si no FileVersion). Reemplazable en las pruebas.</summary>
        internal static Func<string, string> ExeVersionReader = ReadExeVersion;

        /// <summary>Contenido de un archivo de texto chico (como mucho 4 KB) o null. Reemplazable en las pruebas.</summary>
        internal static Func<string, string> SmallTextReader = ReadSmallText;

        static string ReadSmallText(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > 4096) return null;
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch { return null; }
        }

        /// <summary>
        /// Versión real del programa (sus archivos) para compararla con la del registro; "" si no se sabe.
        /// Ver <see cref="ProgramRules.ActualVersion"/>.
        /// </summary>
        internal static string FilesVersion(InstalledProgram p, string reference, out string source)
        {
            return ProgramRules.ActualVersion(p, reference, ExeVersionReader, SmallTextReader, out source);
        }

        /// <summary>
        /// Programa que se actualizó solo y dejó atrás su entrada del registro (TeraBox: su actualizador corre
        /// sin privilegios y no puede cambiar la entrada «para todo el equipo»). winget compara con el
        /// registro y la ofrece para siempre; instalarla no cambia nada. Si los archivos ya están en la versión
        /// ofrecida, la fila queda «Al día» con la explicación. Si están en una intermedia, se muestra esa.
        /// </summary>
        public static void CheckFilesVersion(UpdateCandidate c, WingetRow w, List<InstalledProgram> programs)
        {
            if (c == null || w == null || c.Kind != UpdateKind.Programa || c.Status != UpdateStatus.Disponible) return;
            if (WingetTable.IsUnknownVersion(w.Version) || WingetTable.IsTruncated(w.Version) || WingetTable.IsTruncated(w.Available)) return;
            if (VersionUtil.Parse(w.Available) == null || !VersionUtil.IsNewer(w.Available, w.Version)) return;
            var p = FindProgram(programs, w.Name, w.Version);
            if (p == null) return;
            string source;
            string actual = "";
            try { actual = FilesVersion(p, w.Version, out source); }
            catch (Exception ex) { Log.Warn("Versión de los archivos de " + c.Name + ": " + ex.Message); return; }
            if (actual.Length == 0 || !VersionUtil.IsNewer(actual, w.Version)) return;
            bool strong = ProgramRules.IsProductVersionSource(source);
            Log.Info(c.Name + ": el registro dice " + w.Version + " y " + source + " indica " + actual + " (winget ofrece " + w.Available + ")");
            if (!ProgramRules.PlausibleFilesVersion(actual, w.Available, strong)) return;   // el ejecutable numera distinto que el producto
            if (ProgramRules.FilesAlreadyAt(actual, w.Version, w.Available, strong))
            {
                c.Status = UpdateStatus.AlDia;
                c.Installable = false;
                c.Recommended = false;
                c.InstalledVersion = actual;
                c.Detail = "Ya está en la " + actual + " (lo indica " + source + "): el programa se actualizó solo, pero su entrada en «Aplicaciones» de Windows " +
                           "quedó en la " + w.Version + ", y winget compara con esa: por eso la sigue ofreciendo. No hace falta instalarla.";
                return;
            }
            // Sólo si la diferencia se ve con la precisión de la ofrecida («6.2.11.12345» frente a «6.2.11 (12345)» es lo mismo).
            int parts = Math.Max(1, VersionUtil.Parse(w.Available).Length);
            if (VersionUtil.Compare(ProgramRules.Truncate(actual, parts), ProgramRules.Truncate(w.Version, parts)) <= 0) return;
            // La columna sigue con la del registro: es la que winget compara y la que confirma la instalación.
            c.Detail = (c.Detail ?? "").TrimEnd() + " Los archivos del programa ya están en la " + actual + " (lo indica " + source + "); Windows registra la " + w.Version + ".";
        }

        static string ReadExeVersion(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
                var fi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                string v = fi.ProductVersion;
                if (VersionUtil.Parse(v) == null) v = fi.FileVersion;
                return v ?? "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// winget lista el programa con versión instalada «Desconocida» y, con --include-unknown, lo ofrece
        /// aunque lo instalado sea igual o más nuevo (así se reinstaló WinRAR). Se usa la versión del
        /// registro o la del ejecutable principal: si ya es la ofrecida (o más nueva) no se ofrece; si no
        /// hay forma de saberla, se ofrece SIN premarcar.
        /// </summary>
        public static void ResolveUnknown(UpdateCandidate c, WingetRow w, List<InstalledProgram> programs)
        {
            var p = FindProgram(programs, w.Name);
            string reg = p != null ? (p.Version ?? "") : "";
            string exe = "";
            if (p != null && VersionUtil.Parse(reg) == null && ExeVersionReader != null)
            {
                string main = ProgramRules.MainExe(p);
                if (main.Length > 0) { try { exe = ExeVersionReader(main) ?? ""; } catch { exe = ""; } }
            }
            string installed, source;
            switch (ProgramRules.ResolveUnknownVersion(w.Available, reg, exe, out installed, out source))
            {
                case ProgramRules.UnknownVerdict.Desactualizada:
                    c.InstalledVersion = installed;
                    c.Detail = "winget no reconoce la versión instalada; " + source + " indica la " + installed + " y el repositorio " + w.Source + " publica la " + w.Available + ".";
                    break;
                case ProgramRules.UnknownVerdict.AlDia:
                    c.InstalledVersion = installed;
                    c.Status = UpdateStatus.AlDia;
                    c.Installable = false;
                    c.Recommended = false;
                    c.Detail = "winget no reconoce la versión instalada, pero " + source + " indica la " + installed + ", que ya es la última publicada en " + w.Source + ".";
                    break;
                case ProgramRules.UnknownVerdict.MasNueva:
                    c.InstalledVersion = installed;
                    c.Status = UpdateStatus.AlDia;
                    c.Installable = false;
                    c.Recommended = false;
                    c.Detail = "winget no reconoce la versión instalada; " + source + " indica la " + installed + ", más nueva que la " + w.Available +
                               " del repositorio: no se ofrece, para no instalar una versión más vieja.";
                    break;
                default:
                    c.Recommended = false;
                    c.Detail = "winget no puede determinar la versión instalada y el programa no la registra: no se marca sola, porque lo instalado " +
                               "podría ser igual o más nuevo que la " + w.Available + " que ofrece winget. Marcala si sabés que tenés una anterior.";
                    break;
            }
        }

        /// <summary>
        /// Una fila de «winget list» que no vino en «winget upgrade». Si trae una versión disponible
        /// más nueva NO está al día (antes se la marcaba «Al día» igual): winget la muestra en «list»
        /// pero la saca de «upgrade» cuando el instalador publicado es de otra tecnología o el
        /// programa está anclado, así que se ofrece sin premarcar.
        /// </summary>
        public static UpdateCandidate FromListRow(WingetRow w, UpdateCandidate c)
        {
            c.Installable = false;
            if (w.Source.Length == 0 && ProgramRules.IsSelf(w.Name, "", w.Id))
            {
                ProgramRules.MarkSelf(c, w.Version, AppInfo.Version);
                return c;
            }
            if (w.Source.Length == 0)
            {
                c.AvailableVersion = "";
                c.Status = UpdateStatus.NoVerificable;
                c.Source = "";
                c.Detail = "No figura en los repositorios de winget; revisá el sitio del fabricante.";
                return c;
            }
            // «winget list» sólo muestra «Disponible» cuando hay algo más nuevo: si está recortada
            // («10.0.26100.1…») no se puede comparar, pero sí dice que hay una actualización.
            if (w.Available.Length > 0 && (WingetTable.IsTruncated(w.Available) || VersionUtil.IsNewer(w.Available, w.Version)))
            {
                c.AvailableVersion = w.Available;
                c.Status = UpdateStatus.Disponible;
                c.Installable = !w.IdTruncated;
                c.Recommended = false;
                c.Detail = "winget la publica en " + w.Source + " pero no la ofrece en «winget upgrade» (instalador de otro tipo o paquete anclado): " +
                           "puede requerir desinstalar e instalar de nuevo. Revisá antes de marcarla.";
                return c;
            }
            c.AvailableVersion = w.Available.Length > 0 ? w.Available : w.Version;
            c.Status = UpdateStatus.AlDia;
            c.Detail = "Coincide con la última versión publicada en " + w.Source + ".";
            return c;
        }

        /// <summary>
        /// Copia de la última salida cruda de winget en la carpeta de registros: si un programa
        /// aparece mal, se puede ver exactamente qué imprimió winget.
        /// </summary>
        static void SaveRaw(string fileName, ProcResult r)
        {
            try
            {
                if (!SafePaths.EnsureDataDirectory(AppInfo.LogDirectory)) return;
                File.WriteAllText(Path.Combine(AppInfo.LogDirectory, fileName),
                    "# " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " — " + r.Describe() + "\r\n" + r.Out + (r.Err.Length > 0 ? "\r\n# stderr\r\n" + r.Err : ""),
                    new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("No se pudo guardar " + fileName + ": " + ex.Message); }
        }

        /// <summary>
        /// Red de seguridad: todo programa del registro que winget no informó aparece igual, como
        /// «no verificable». Así ningún programa instalado desaparece de la lista en silencio, aunque
        /// winget no lo liste o su fila no se pueda leer (como pasaba con «ad 9.7.15» de AnyDesk).
        /// </summary>
        public static int AddMissingPrograms(List<InstalledProgram> programs, List<WingetRow> vistos, List<UpdateCandidate> output)
        {
            if (programs == null || output == null) return 0;
            int n = 0;
            var ya = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in programs)
            {
                if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                if (!ya.Add(p.Name.Trim())) continue;          // el mismo programa en x86/x64 o equipo/usuario: una fila
                if (ReportedByWinget(p.Name, vistos)) continue;
                var c = new UpdateCandidate();
                c.Kind = UpdateKind.Programa;
                c.Name = p.Name;
                c.Publisher = p.Publisher;
                c.InstalledVersion = string.IsNullOrEmpty(p.Version) ? "Desconocida" : p.Version;
                c.AvailableVersion = "";
                c.Status = UpdateStatus.NoVerificable;
                c.Installable = false;
                c.Source = "";
                c.Identifier = "registro:" + p.Name;
                c.ProviderName = ProviderId;
                c.Detail = "Instalado, pero winget no lo informa: revisá el sitio del fabricante.";
                if (ProgramRules.IsSelf(p.Name, p.Publisher, "")) ProgramRules.MarkSelf(c, p.Version, AppInfo.Version);
                output.Add(c);
                n++;
            }
            return n;
        }

        /// <summary>¿winget informó este programa? Compara con el nombre completo o con el recortado («…»).</summary>
        static bool ReportedByWinget(string name, List<WingetRow> rows)
        {
            if (rows == null) return false;
            string n = name.Trim();
            foreach (var w in rows)
            {
                string wn = (w.Name ?? "").Trim();
                if (wn.Length == 0) continue;
                string recorte = null;
                if (wn.EndsWith("…")) recorte = wn.Substring(0, wn.Length - 1).TrimEnd();
                else if (wn.EndsWith("...")) recorte = wn.Substring(0, wn.Length - 3).TrimEnd();
                if (recorte != null ? (recorte.Length > 0 && n.StartsWith(recorte, StringComparison.OrdinalIgnoreCase))
                                    : n.Equals(wn, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Filas de una consulta con --source: winget no imprime la columna «Origen».</summary>
        public static void FillSource(List<WingetRow> rows, string source)
        {
            if (rows == null) return;
            foreach (var w in rows)
                if (w.Source.Length == 0 && !WingetTable.IsLocalId(w.Id)) w.Source = source;
        }

        /// <summary>
        /// Completa los identificadores (y las versiones disponibles) que winget recortó con «…» en las
        /// filas que se pueden actualizar. Devuelve cuántos se completaron.
        /// </summary>
        int ResolveTruncated(UpdateContext ctx, List<WingetRow> upgrades, List<WingetRow> installed)
        {
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int done = 0, asked = 0;
            // Primero las filas de «winget upgrade» (son las que se instalan).
            foreach (var w in upgrades)
                if (w.IdTruncated || WingetTable.IsTruncated(w.Available))
                    if (ResolveOne(ctx, w, cache, ref asked)) done++;
            // Las de «winget list» con una versión disponible: si la misma fila vino en «winget upgrade», se
            // toma de ahí sin otra consulta (y así no aparece dos veces).
            foreach (var w in installed)
            {
                if (!w.IdTruncated || w.Available.Length == 0) continue;
                string prefix = TruncatedPrefix(w.Id);
                var same = prefix == null ? null : upgrades.Find(delegate(WingetRow u)
                {
                    return !u.IdTruncated && u.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && SameText(w.Name, u.Name) && SameText(w.Version, u.Version);
                });
                if (same != null) { w.Id = same.Id; w.IdTruncated = false; done++; continue; }
                if (ResolveOne(ctx, w, cache, ref asked)) done++;
            }
            return done;
        }

        /// <summary>Completa una fila con «winget list --id &lt;prefijo&gt;» (como mucho 40 consultas por búsqueda).</summary>
        bool ResolveOne(UpdateContext ctx, WingetRow w, Dictionary<string, string> cache, ref int asked)
        {
            if (ctx.CancelRequested) return false;
            string prefix = TruncatedPrefix(w.Id);
            if (prefix == null) return false;
            string key = prefix + "|" + w.Source;
            string text;
            if (!cache.TryGetValue(key, out text))
            {
                if (asked >= 40) return false;
                asked++;
                string args = "list --id " + ProcessRunner.QuoteArg(prefix) + (IsSafeSource(w.Source) ? " --source " + ProcessRunner.QuoteArg(w.Source) : "") + CommonArgs;
                var r = RunQuery(args, 120000);
                text = r != null && r.Started && !r.TimedOut ? r.Out : "";
                cache[key] = text;
            }
            if (CompleteFromListing(w, text)) return true;
            Log.Warn("winget: no se pudo completar el identificador recortado «" + w.Id + "» de " + w.Name);
            return false;
        }

        /// <summary>Lo que queda de un identificador recortado («Editor.Paq…» → «Editor.Paq»), o null si no sirve para buscar.</summary>
        public static string TruncatedPrefix(string id)
        {
            string s = (id ?? "").Trim();
            if (s.EndsWith("…")) s = s.Substring(0, s.Length - 1);
            else if (s.EndsWith("...")) s = s.Substring(0, s.Length - 3);
            s = s.TrimEnd();
            if (s.Length < 3 || WingetTable.IsLocalId(s)) return null;
            return IsSafeId(s) ? s : null;
        }

        /// <summary>
        /// Completa la fila con la de «winget list --id &lt;prefijo&gt;» que le corresponde: mismo comienzo del
        /// identificador, mismo nombre (o el comienzo, si estaba recortado) y misma versión instalada.
        /// Sólo si hay exactamente un paquete que encaja. true si la fila quedó utilizable.
        /// </summary>
        public static bool CompleteFromListing(WingetRow w, string listing)
        {
            if (w == null || string.IsNullOrEmpty(listing)) return false;
            string prefix = w.IdTruncated ? TruncatedPrefix(w.Id) : w.Id;
            if (prefix == null) return false;
            WingetRow match = null;
            foreach (var row in WingetTable.Parse(listing))
            {
                if (row.IdTruncated || !row.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (w.IdTruncated == false && !row.Id.Equals(w.Id, StringComparison.OrdinalIgnoreCase)) continue;
                if (!SameText(w.Name, row.Name) || !SameText(w.Version, row.Version)) continue;
                if (match != null && !match.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase)) return false;   // ambiguo
                match = row;
            }
            if (match == null) return false;
            w.Id = match.Id;
            w.IdTruncated = false;
            if (WingetTable.IsTruncated(w.Name) && !WingetTable.IsTruncated(match.Name) && match.Name.Length > 0) w.Name = match.Name;
            if ((w.Available.Length == 0 || WingetTable.IsTruncated(w.Available)) && match.Available.Length > 0 && !WingetTable.IsTruncated(match.Available))
                w.Available = match.Available;
            if (WingetTable.IsTruncated(w.Version) && match.Version.Length > 0 && !WingetTable.IsTruncated(match.Version)) w.Version = match.Version;
            return true;
        }

        /// <summary>¿El texto de la tabla grande (quizás recortado con «…») corresponde al completo?</summary>
        static bool SameText(string shown, string full)
        {
            string a = (shown ?? "").Trim(), b = (full ?? "").Trim();
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
            string cut = null;
            if (a.EndsWith("…")) cut = a.Substring(0, a.Length - 1).TrimEnd();
            else if (a.EndsWith("...")) cut = a.Substring(0, a.Length - 3).TrimEnd();
            return cut != null && cut.Length > 0 && b.StartsWith(cut, StringComparison.OrdinalIgnoreCase);
        }

        static bool IsNoResults(int code)
        {
            uint u = unchecked((uint)code);
            return u == 0x8A150014 || u == 0x8A15002B;
        }

        UpdateCandidate NewCandidate(WingetRow w, List<InstalledProgram> programs)
        {
            var c = new UpdateCandidate();
            bool store = w.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase);
            c.Kind = store ? UpdateKind.Store : UpdateKind.Programa;
            c.Name = w.Name.TrimEnd('…');
            c.InstalledVersion = WingetTable.IsUnknownVersion(w.Version) ? "Desconocida" : w.Version;
            c.AvailableVersion = w.Available;
            c.Source = w.Source.Length > 0 ? "winget (" + w.Source + ")" : "";
            c.Identifier = w.Id;
            c.ProviderName = ProviderId;
            c.ProviderTag = w;
            var p = FindProgram(programs, w.Name, w.Version);
            if (p != null) c.Publisher = p.Publisher;
            return c;
        }

        internal static InstalledProgram FindProgram(List<InstalledProgram> programs, string name)
        {
            return FindProgram(programs, name, null);
        }

        /// <summary>
        /// El programa del registro que corresponde a una fila de winget. Con varios candidatos (nombre
        /// recortado con «…», o copias de 32 y 64 bits con el mismo nombre) se elige por la versión que
        /// informa winget; si sigue habiendo más de uno, null: con el programa equivocado se cerraban sus
        /// procesos, se elegía mal la arquitectura o, al reinstalar, se desinstalaba el que no era.
        /// </summary>
        internal static InstalledProgram FindProgram(List<InstalledProgram> programs, string name, string version)
        {
            if (programs == null || string.IsNullOrEmpty(name)) return null;
            bool truncated = name.EndsWith("…");
            string n = name.TrimEnd('…');
            var matches = new List<InstalledProgram>();
            foreach (var p in programs)
            {
                if (p == null || p.Name == null) continue;
                if (truncated ? p.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase) : p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    matches.Add(p);
            }
            if (matches.Count <= 1) return matches.Count == 1 ? matches[0] : null;
            if (VersionUtil.Parse(version) != null)
            {
                var same = matches.FindAll(delegate(InstalledProgram p) { return VersionUtil.Compare(p.Version, version) == 0; });
                if (same.Count > 0) matches = same;
            }
            return DistinctEntries(matches) == 1 ? matches[0] : null;
        }

        /// <summary>Entradas distintas del registro (la misma clave vista dos veces cuenta una).</summary>
        static int DistinctEntries(List<InstalledProgram> list)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in list) keys.Add((p.RegistryKey ?? "") + "|" + (p.Architecture ?? "") + "|" + (p.Name ?? ""));
            return keys.Count;
        }

        static readonly Regex SafeIdPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$", RegexOptions.Compiled);

        /// <summary>
        /// Identificador según las reglas de winget: hasta 8 partes separadas por punto, sin espacios, sin
        /// \ / : * ? " &lt; &gt; | ni caracteres de control. winget acepta letras de cualquier idioma (é, ñ, 中文…) y signos
        /// como «()», «,» o «'»: antes esos programas quedaban «Identificador con caracteres no permitidos» y no se
        /// actualizaban nunca. Va entrecomillado (<see cref="ProcessRunner.QuoteArg"/>) y sin pasar por cmd.exe; igual se
        /// siguen rechazando los caracteres especiales de cmd (&amp; ^ % !), por si algún día pasara por una consola.
        /// </summary>
        static readonly Regex WingetIdPattern = new Regex(@"^[^.\s\\/:*?""<>|&^%!\x00-\x1f\-][^.\s\\/:*?""<>|&^%!\x00-\x1f]{0,63}(?:\.[^.\s\\/:*?""<>|&^%!\x00-\x1f]{1,64}){0,9}$", RegexOptions.Compiled);
        static readonly Regex SafeSourcePattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,63}$", RegexOptions.Compiled);

        /// <summary>
        /// Identificadores de winget («Editor.Paquete», «Microsoft.VCRedist.2015+.x64») y de la
        /// Store («9NKSQGP7F2NH»). Cualquier otro carácter (comillas, espacios, barras) se rechaza.
        /// </summary>
        public static bool IsSafeId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 255 || id.EndsWith("…") || id.EndsWith("...")) return false;
            return SafeIdPattern.IsMatch(id) || WingetIdPattern.IsMatch(id);
        }

        /// <summary>Identificador sólo con letras y números ASCII, punto, guion y «+»: el que se puede usar en una dirección web sin codificar.</summary>
        public static bool IsAsciiId(string id) { return !string.IsNullOrEmpty(id) && SafeIdPattern.IsMatch(id); }
        public static bool IsSafeSource(string source) { return !string.IsNullOrEmpty(source) && SafeSourcePattern.IsMatch(source); }
    }
}
