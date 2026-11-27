// winget: instalación (orden, versión fijada, guardia contra degradaciones, confirmación y lectura del resultado).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    public sealed partial class WingetProvider
    {
        public const uint NoApplicableInstaller = 0x8A150010;

        public const uint UpdateNotApplicable = 0x8A15002B;

        public const uint InstallerFailed = 0x8A150006;

        /// <summary>APPINSTALLER_CLI_ERROR_INSTALL_REBOOT_REQUIRED_TO_FINISH: instalado, falta reiniciar (winget lo cuenta como éxito).</summary>
        public const uint RebootToFinish = 0x8A150109;

        /// <summary>
        /// APPINSTALLER_CLI_ERROR_INSTALL_REBOOT_REQUIRED_FOR_INSTALL: el instalador pide reiniciar ANTES de
        /// instalar; winget lo trata como error porque la instalación no se completó.
        /// </summary>
        public const uint RebootBeforeInstall = 0x8A15010A;

        /// <summary>APPINSTALLER_CLI_ERROR_INSTALL_REBOOT_INITIATED: el instalador terminó e inició el reinicio (MSI 1641).</summary>
        public const uint RebootInitiated = 0x8A15010B;

        public const string RebootBeforeInstallText =
            "No se instaló: el instalador pide reiniciar el equipo ANTES de instalar (quedó pendiente otra instalación o actualización). " +
            "Reiniciá el equipo y volvé a actualizarlo";

        /// <summary>App Installer es el propio winget: se actualiza al final de la tanda.</summary>
        public static bool IsWingetItself(string id)
        {
            return string.Equals(id, "Microsoft.AppInstaller", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(id, "9NBLGGH4NNS1", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Orden de instalación: todo lo demás primero y App Installer (winget) al final, porque al
        /// actualizarse cambia de carpeta y deja a winget inutilizable hasta encontrarlo de nuevo.
        /// </summary>
        public static List<UpdateCandidate> InstallOrder(List<UpdateCandidate> items)
        {
            var first = new List<UpdateCandidate>();
            var last = new List<UpdateCandidate>();
            foreach (var c in items)
            {
                var w = c.ProviderTag as WingetRow;
                if (w != null && IsWingetItself(w.Id)) last.Add(c); else first.Add(c);
            }
            // Los complementos después del programa: «TheDocumentFoundation.LibreOffice.HelpPack» detrás de
            // «TheDocumentFoundation.LibreOffice». La lista viene por nombre y «LibreOffice 26.2 Help Pack»
            // quedaba antes que «LibreOffice 26.2.5.2»: el MSI del paquete de ayuda 26.8 exige LibreOffice
            // 26.8 instalado y terminaba con 1603. Orden estable: lo demás no cambia.
            var ids = new List<string>();
            foreach (var c in first) { var w = c.ProviderTag as WingetRow; ids.Add(w != null ? w.Id ?? "" : ""); }
            var depth = new Dictionary<UpdateCandidate, int>();
            for (int i = 0; i < first.Count; i++)
            {
                int d = 0;
                for (int j = 0; j < first.Count; j++)
                    if (i != j && ids[j].Length > 0 && ids[i].Length > ids[j].Length + 1 &&
                        ids[i].StartsWith(ids[j] + ".", StringComparison.OrdinalIgnoreCase)) d++;
                depth[first[i]] = d;
            }
            var ordered = new List<UpdateCandidate>();
            for (int d = 0; ordered.Count < first.Count; d++)
                foreach (var c in first) if (depth[c] == d) ordered.Add(c);
            ordered.AddRange(last);
            return ordered;
        }

        /// <summary>
        /// Cerrar los programas abiertos antes de actualizarlos (lo acepta el usuario en la ventana que
        /// los lista, o --cerrar-programas). Si no, se instala igual y, si falla, se avisa que estaba abierto.
        /// </summary>
        public bool CloseRunning;

        public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed)
        {
            string native = ctx.Os != null ? ctx.Os.NativeArchitecture : "";
            bool interactiveAsked = Interactive;
            try
            {
                foreach (var c in InstallOrder(items))
                {
                    Interactive = interactiveAsked;
                    if (ctx.CancelRequested) { c.Status = UpdateStatus.Pendiente; c.Result = "Cancelada antes de empezar"; changed(c); continue; }
                    var w = c.ProviderTag as WingetRow;
                    if (w == null || w.IdTruncated) { c.Status = UpdateStatus.Fallida; c.Result = "Identificador no utilizable"; changed(c); continue; }
                    if (!IsSafeId(w.Id) || (w.Source.Length > 0 && !IsSafeSource(w.Source)))
                    {
                        // El texto viene de la salida de winget: no se le permite colar argumentos.
                        c.Status = UpdateStatus.Fallida;
                        c.Result = "Identificador con caracteres no permitidos: no se ejecuta";
                        Log.Warn("Se rechazó el identificador de winget " + w.Id + " / origen " + w.Source);
                        changed(c);
                        continue;
                    }
                    // 2.0.40: un instalador que no se puede ejecutar en silencio (Npcap gratuito) se abre directamente con su ventana
                    // (winget --interactive). Sin nadie frente a la pantalla o sin permiso de la política, no: quedaría esperando un clic.
                    string windowOnly = Interactive ? null : InstallerRules.WindowOnly(w.Name, w.Id);
                    if (windowOnly != null)
                    {
                        if (Unattended || !(UpdateManager.PolicyForTests ?? Policy.Current).AllowInteractiveInstallers)
                        {
                            c.Status = UpdateStatus.Fallida;
                            c.Result = char.ToUpperInvariant(windowOnly[0]) + windowOnly.Substring(1) + ": " +
                                       (Unattended ? "sin nadie frente a la pantalla no se abre su instalador. Actualizalo desde la ventana de Actualizador: se abre su instalador y se completa con unos clics"
                                                   : "la política de la empresa no permite instaladores con ventana");
                            changed(c);
                            continue;
                        }
                        Log.Info(c.Name + ": " + windowOnly + ": se instala con la ventana de su instalador (winget --interactive)");
                        Interactive = true;
                    }
                    c.Status = UpdateStatus.Instalando;
                    c.Result = windowOnly != null ? "Completá la instalación en la ventana de su instalador" : "";
                    changed(c);
                    ctx.Report("Actualizando " + c.Name + " (" + w.Id + ") a " + c.AvailableVersion + "…");
                    // 2.0.20: paquete aprovisionado para todos los usuarios.
                    if (w.MachineScope) { InstallMachine(ctx, c, w, changed); changed(c); continue; }
                    // 2.0.20: Roblox (su paquete de winget pasó a «portable» y winget ya no puede actualizar lo instalado).
                    if (RobloxPlayer.Handles(w)) { RobloxPlayer.Install(ctx, c, w, Interactive, changed); changed(c); continue; }
                    bool store = w.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase);
                    // Una copia portable no la toca el instalador oficial: no se cierran sus procesos ni se comparan sus archivos.
                    var prog = store || w.PortableCopy ? null : FindProgram(ctx.Programs, w.Name, w.Version);
                    string before = c.InstalledVersion;
                    string expected = PinnedVersion(w);
                    // Versión de los archivos ANTES de instalar: para saber después si cambiaron aunque el registro no.
                    string filesBefore = "";
                    if (prog != null) { try { string ignored; filesBefore = FilesVersion(prog, before, out ignored); } catch { filesBefore = ""; } }

                    // Abierto en segundo plano (Epic Games Launcher): su MSI termina con 1603.
                    string openNames = store ? null : HandleRunning(ctx, c, prog, changed);

                    // La arquitectura instalada: winget elige la del equipo, y sobre un TeamViewer de 32 bits
                    // el instalador de 64 aborta con el código 2. Se le pide la misma que está instalada.
                    string archArg = null, installedArch = "";
                    if (Modern && !store)
                    {
                        installedArch = ProgramRules.DetectArchitecture(prog, native, BrowserProvider.PeArch);
                        archArg = ProgramRules.WingetArchitecture(installedArch, native);
                        if (archArg != null) Log.Info(c.Name + ": está instalado en " + archArg + " (el equipo es " + native + "): se actualiza con --architecture " + archArg);
                    }
                    // Nunca una versión más vieja que la ofrecida o que la instalada (WinRAR 7.01 sobre la 7.23).
                    Func<string, string> guard = delegate(string line) { return DowngradeGuard(line, w.Id, c.AvailableVersion, before); };
                    bool pinned = expected != null;
                    string baseArgs = UpgradeArgs(w, "upgrade", pinned);
                    string args = baseArgs + (archArg != null ? " --architecture " + archArg : "");
                    var last = new StringBuilder();
                    ProcResult r = RunUpgrade(args, last, guard);
                    int attempts = 1;
                    string directNote = null;
                    uint code = unchecked((uint)r.ExitCode);

                    // Con --version, winget responde «no hay actualización aplicable» (0x8A15002B) o «ningún
                    // instalador aplicable» (0x8A150010) por cualquier motivo, incluso cuando la versión nueva usa
                    // otra tecnología de instalación (sin --version eso es 0x8A15008E y tiene arreglo). Se repite
                    // sin fijar la versión: winget dice la causa real, y la guardia sigue impidiendo una más vieja.
                    if (pinned && IsNotApplicable(code) && Usable(r, ctx))
                    {
                        ProcResult free = RunUpgrade(UpgradeArgs(w, "upgrade", false) + (archArg != null ? " --architecture " + archArg : ""), last, guard);
                        if (free.Aborted && !ctx.CancelRequested)
                            Log.Info(c.Name + ": sin --version winget elegía una versión más vieja (" + free.AbortReason + "); se conserva la respuesta anterior");
                        else
                        {
                            Log.Info(c.Name + ": sin --version winget respondió " + free.Describe());
                            r = free;
                            code = unchecked((uint)r.ExitCode);
                            pinned = false;
                            baseArgs = UpgradeArgs(w, "upgrade", false);
                            args = baseArgs + (archArg != null ? " --architecture " + archArg : "");
                        }
                    }

                    // Con «--scope» winget no encontró un instalador para ese alcance (el manifiesto no lo declara): se repite sin él.
                    if (w.InstallInsteadOfUpgrade && w.Scope.Length > 0 && Usable(r, ctx) && IsNotApplicable(code))
                    {
                        Log.Info(c.Name + ": winget no tiene un instalador declarado para el alcance «" + w.Scope + "»; se repite sin --scope");
                        w.Scope = "";
                        baseArgs = UpgradeArgs(w, "upgrade", pinned);
                        args = baseArgs + (archArg != null ? " --architecture " + archArg : "");
                        r = RunUpgrade(args, last, guard);
                        code = unchecked((uint)r.ExitCode);
                    }

                    // El instalador no admite correr como administrador (Spotify y otras apps «sólo para el usuario»): con
                    // Actualizador elevado no se actualizaban nunca. Se repite lo mismo como el usuario, sin elevar.
                    if (ctx.IsAdmin && Usable(r, ctx) && ProhibitsElevation(code) && !store)
                    {
                        ctx.Report(c.Name + ": su instalador no admite ejecutarse como administrador; se actualiza como tu usuario, sin elevar…");
                        InstallerSupport.Progress(c, changed, "Actualizando sin elevar (el instalador no admite administrador)…");
                        var u = RunUnelevated(args);
                        if (u.Started)
                        {
                            // Sin el código (no se encontró el proceso): se da por terminado y lo decide la versión instalada (ConfirmInstalled).
                            if (u.ExitCode == Unelevated.UnknownExit) u.ExitCode = 0;
                            r = u;
                            code = unchecked((uint)r.ExitCode);
                            lock (last) { last.Length = 0; last.Append("winget sin elevar: " + r.Describe()); }
                            Log.Info(c.Name + ": winget sin elevar terminó: " + r.Describe());
                        }
                        else
                        {
                            directNote = "tampoco se pudo ejecutar sin elevar: " + u.StartError + ". Abrí Actualizador sin «Ejecutar como administrador» y actualizalo desde ahí";
                            Log.Warn(c.Name + ": " + directNote);
                        }
                    }

                    // Sin instalador para esa arquitectura: sólo un MSI se puede pasar a la que elija winget
                    // (reemplaza al instalado). Un EXE dejaría dos copias o abortaría: se explica y no se toca.
                    if (archArg != null && Usable(r, ctx) && IsNotApplicable(code))
                    {
                        if (!ProgramRules.CanSwitchArchitecture(prog))
                        {
                            c.Status = UpdateStatus.Fallida;
                            c.Result = ProgramRules.ArchitectureBlockedText(expected ?? c.AvailableVersion, installedArch, native, w.Id);
                            Log.Warn(c.Name + ": " + c.Result);
                            changed(c);
                            continue;
                        }
                        ctx.Report(c.Name + ": winget no publica la versión nueva para " + archArg + "; se instala la que elija winget (es MSI: reemplaza a la instalada)…");
                        archArg = null;
                        pinned = expected != null;
                        baseArgs = UpgradeArgs(w, "upgrade", pinned);
                        args = baseArgs;
                        r = RunUpgrade(args, last, guard);
                        code = unchecked((uint)r.ExitCode);
                        if (pinned && IsNotApplicable(code) && Usable(r, ctx))
                        {
                            ProcResult free = RunUpgrade(UpgradeArgs(w, "upgrade", false), last, guard);
                            if (!free.Aborted || ctx.CancelRequested)
                            {
                                r = free;
                                code = unchecked((uint)r.ExitCode);
                                pinned = false;
                                baseArgs = UpgradeArgs(w, "upgrade", false);
                                args = baseArgs;
                            }
                        }
                    }

                    // La versión nueva usa otro tipo de instalador (Double Commander): se reinstala, verificada.
                    if (r.Started && !r.TimedOut && !r.Aborted && !ctx.CancelRequested && code == WingetDirect.TechMismatch)
                    {
                        ctx.Report(c.Name + ": la versión nueva usa otro tipo de instalador; se reinstala (primero se descarga y verifica la nueva)…");
                        string installArgs = UpgradeArgs(w, "install") + (archArg != null ? " --architecture " + archArg : "");
                        DirectAttempt d = TryDirect(ctx, c, changed, delegate { return WingetDirect.TryReinstall(ctx, c, w, Interactive, changed, delegate { return RunUpgrade(installArgs, last, guard); }, windowForInstallerOnly,
                                                                                    delegate(string folder, string version) { return RunUpgrade(DownloadArgs(w, version, folder), last, null); }); });
                        if (d.Handled) { changed(c); continue; }
                        directNote = d.NotApplicable;
                        Log.Info(c.Name + ": reinstalación no disponible: " + directNote);
                    }

                    // Hash viejo en el catálogo (GOM Player) o corte de red: se prueba el instalador oficial
                    // del mismo manifiesto, verificado, sin esperar más intentos de winget.
                    if (r.Started && !r.TimedOut && !r.Aborted && !ctx.CancelRequested && WingetDirect.Applies(code))
                    {
                        ctx.Report(WingetDirect.Why(code, c.Name));
                        string output = r.Out + "\n" + r.Err;
                        uint failed = code;
                        DirectAttempt d = TryDirect(ctx, c, changed, delegate { return WingetDirect.TryInstall(ctx, c, w, output, failed, Interactive, changed); });
                        if (d.Handled) { changed(c); continue; }
                        directNote = d.NotApplicable;
                        Log.Info(c.Name + ": vía directa no disponible: " + directNote);
                    }

                    // Sigue sin haber un instalador aplicable: el manifiesto oficial dice por qué (alcance equipo/
                    // usuario, arquitectura, Windows mínimo). Si la única diferencia es el alcance de un instalador
                    // NSIS o Inno Setup (TeraBox instalado para el usuario y publicado «para el equipo»), winget no
                    // lo actualiza nunca, pero el instalador oficial sí: se usa ése, verificado con su SHA-256.
                    if (Usable(r, ctx) && IsNotApplicable(code))
                    {
                        ctx.Report(c.Name + ": winget no tiene un instalador aplicable; se revisa su manifiesto oficial…");
                        string output = r.Out + "\n" + r.Err;
                        uint failed = code;
                        DirectAttempt d = TryDirect(ctx, c, changed, delegate { return WingetDirect.TryInstall(ctx, c, w, output, failed, Interactive, changed); });
                        if (d.Handled) { changed(c); continue; }
                        directNote = d.NotApplicable;
                        Log.Info(c.Name + ": sin instalador aplicable: " + directNote);
                    }

                    // Sin vía directa, los cortes de red se reintentan con winget.
                    while (attempts < MaxAttempts && r.Started && !r.TimedOut && !r.Aborted && IsTransientNetwork(unchecked((uint)r.ExitCode)) && !ctx.CancelRequested)
                    {
                        attempts++;
                        ctx.Report("winget no pudo descargar " + c.Name + " (" + Describe(unchecked((uint)r.ExitCode)) + "); reintento " + attempts + " de " + MaxAttempts + " en 10 segundos…");
                        c.Result = "Reintentando la descarga (" + attempts + " de " + MaxAttempts + ")";
                        changed(c);
                        for (int s = 0; s < 10 && !ctx.CancelRequested; s++) System.Threading.Thread.Sleep(1000);
                        if (ctx.CancelRequested) break;
                        r = RunUpgrade(args, last, guard);
                    }
                    string lastLine;
                    lock (last) lastLine = last.ToString();
                    // El instalador falló: la causa real está en el registro de Windows Installer que dejó winget.
                    string diagnosis = "";
                    if (r.Started && !r.TimedOut && !r.Aborted && r.ExitCode != 0 && InstallerExitCode(r.Out + " " + r.Err) != null)
                        diagnosis = MsiLogAnalyzer.ExplainFromWingetOutput(r.Out + "\n" + r.Err);
                    ApplyResult(c, r, lastLine, attempts, directNote, Interactive, diagnosis, openNames);
                    if (c.Status == UpdateStatus.Fallida && !ctx.IsAdmin && !store && NeedsAdminHint(prog, code, r.Out + " " + r.Err))
                        c.Result = c.Result.TrimEnd('.') + ". " + NotAdminHint;
                    // winget se actualizó a sí mismo: su carpeta cambió.
                    if (IsWingetItself(w.Id) && r.Started && RunForTests == null) RefreshExe("se actualizó App Installer");
                    if ((c.Status == UpdateStatus.Instalada || c.Status == UpdateStatus.RequiereReinicio) && !store)
                        ConfirmInstalled(ctx, c, w, before, c.AvailableVersion, pinned, prog, openNames, filesBefore);
                    changed(c);
                }
            }
            finally { Interactive = interactiveAsked; }
        }

        /// <summary>
        /// Una vía directa (reinstalación verificada o instalador del manifiesto). 2.0.44, diagnóstico del usuario (26/11/2026): Epic
        /// Online Services 5.4.0 → 5.8.0 «Falló la instalación: … el manifiesto no indica cómo instalarlo en silencio». Su paquete nuevo
        /// es un ZIP con un .exe adentro y ningún interruptor: en silencio no se puede, con su ventana sí. Como Npcap (2.0.40), si hay
        /// alguien frente a la pantalla y la política lo permite, se repite la misma vía con la ventana del instalador (descarga,
        /// SHA-256 y firma se verifican igual); si no, la fila dice por qué no se abrió.
        /// </summary>
        /// <summary>2.0.44: mientras <see cref="TryDirect"/> repite una vía con la ventana sólo por el instalador nuevo.</summary>
        bool windowForInstallerOnly;

        DirectAttempt TryDirect(UpdateContext ctx, UpdateCandidate c, Action<UpdateCandidate> changed, Func<DirectAttempt> attempt)
        {
            DirectAttempt d;
            try { d = attempt(); }
            catch (OperationCanceledByUserException) { d = new DirectAttempt(); d.NotApplicable = "cancelada"; }
            if (d.Handled || !d.NeedsWindow || Interactive || ctx.CancelRequested) return d;
            if (Unattended || !(UpdateManager.PolicyForTests ?? Policy.Current).AllowInteractiveInstallers)
            {
                d.NotApplicable = "su instalador no tiene modo silencioso y " +
                                  (Unattended ? "sin nadie frente a la pantalla no se abre su ventana: actualizalo desde la ventana de Actualizador (se completa con unos clics)"
                                              : "la política de la empresa no permite instaladores con ventana");
                return d;
            }
            Log.Info(c.Name + ": su instalador no tiene modo silencioso: se usa su ventana");
            ctx.Report(c.Name + ": su instalador no tiene modo silencioso; se abre su ventana para completarlo…");
            c.Result = "Su instalador no tiene modo silencioso: completá la instalación en su ventana";
            changed(c);
            Interactive = true;
            windowForInstallerOnly = true;
            try { d = attempt(); }
            catch (OperationCanceledByUserException) { d = new DirectAttempt(); d.NotApplicable = "cancelada"; }
            finally { windowForInstallerOnly = false; }
            if (!d.Handled) Interactive = false;
            if (d.Handled && (c.Status == UpdateStatus.Instalada || c.Status == UpdateStatus.RequiereReinicio) && !string.IsNullOrEmpty(c.Result) &&
                c.Result.IndexOf("ventana", StringComparison.OrdinalIgnoreCase) < 0)
                c.Result = c.Result.TrimEnd('.') + " (con la ventana de su instalador: no tiene modo silencioso)";
            return d;
        }

        /// <summary>APPINSTALLER_CLI_ERROR_INSTALLER_PROHIBITS_ELEVATION: el instalador no admite correr como administrador.</summary>
        public const uint ProhibitsElevationCode = 0x8A150056;
        /// <summary>La acción no se permite como administrador.</summary>
        public const uint NotAllowedAsAdmin = 0x8A15007D;

        /// <summary>¿winget se negó porque está elevado? Se puede repetir sin elevar.</summary>
        public static bool ProhibitsElevation(uint code)
        {
            return code == ProhibitsElevationCode || code == NotAllowedAsAdmin;
        }

        /// <summary>winget como el usuario de la sesión (sin elevar), con los mismos argumentos.</summary>
        ProcResult RunUnelevated(string args)
        {
            if (RunForTests != null && Unelevated.RunForTests == null)
            {
                var none = new ProcResult();
                none.StartError = "no disponible en las pruebas";
                return none;
            }
            // El alias del usuario (el que usa una consola normal); si no está, el winget elegido.
            string file = exe;
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string alias = string.IsNullOrEmpty(local) ? null : Path.Combine(local, @"Microsoft\WindowsApps\winget.exe");
                if (alias != null && File.Exists(alias)) file = alias;
            }
            catch (Exception qx) { Log.Quiet("Alias de winget para correr sin elevar", qx); }
            Log.Info("winget (sin elevar) " + args);
            return Unelevated.Run(file, args, 45 * 60 * 1000, Interactive);
        }

        /// <summary>«No hay una actualización aplicable» o «ningún instalador aplicable».</summary>
        public static bool IsNotApplicable(uint code)
        {
            return code == NoApplicableInstaller || code == UpdateNotApplicable;
        }

        /// <summary>winget arrancó, terminó solo (sin plazo vencido ni guardia) y no se canceló.</summary>
        static bool Usable(ProcResult r, UpdateContext ctx)
        {
            return r != null && r.Started && !r.TimedOut && !r.Aborted && !ctx.CancelRequested;
        }

        public const string NotAdminHint =
            "Actualizador no se está ejecutando como administrador y este programa está instalado para todo el equipo: " +
            "tocá «Ejecutar como administrador» y reintentá";

        /// <summary>
        /// ¿El fallo puede deberse a no estar elevado? Sólo para programas del equipo (HKLM) y si falló el
        /// instalador, se negó el acceso o winget pidió administrador. Un MSI «de equipo» en silencio no
        /// puede pedir elevación (no hay ventana de UAC) y termina con 1603, 1625, 1925 o 1730.
        /// </summary>
        public static bool NeedsAdminHint(InstalledProgram prog, uint code, string output)
        {
            if (prog != null && (prog.Scope ?? "").StartsWith("Usuario", StringComparison.OrdinalIgnoreCase)) return false;
            if (code == 0x80070005 || code == 0x8A150019 || code == InstallerFailed || code == 0x8A150049) return true;
            string inst = InstallerExitCode(output);
            return inst == "1603" || inst == "1625" || inst == "1925" || inst == "1730" || inst == "5" || inst == "740";
        }

        /// <summary>
        /// Procesos abiertos desde la carpeta del programa: se cierran si el usuario lo aceptó. Devuelve
        /// los nombres de los que quedan abiertos (para explicar un fallo), o null.
        /// </summary>
        string HandleRunning(UpdateContext ctx, UpdateCandidate c, InstalledProgram prog, Action<UpdateCandidate> changed)
        {
            try
            {
                string folder = RunningPrograms.FolderOf(prog);
                if (folder.Length == 0) return null;
                var open = RunningPrograms.Closable(RunningPrograms.In(RunningPrograms.Snapshot(), folder));
                if (open.Count == 0) return null;
                string names = RunningPrograms.Names(open);
                if (!CloseRunning)
                {
                    Log.Info(c.Name + ": está abierto (" + names + "); no se cierra porque no se pidió");
                    return names;
                }
                ctx.Report("Cerrando " + c.Name + " (" + names + ") para poder actualizarlo…");
                c.Result = "Cerrando el programa abierto (" + names + ")…";
                changed(c);
                var still = RunningPrograms.Close(open, 15000);
                if (still.Count == 0) { Log.Info(c.Name + ": se cerró " + names); return null; }
                string left = RunningPrograms.Names(still);
                ctx.Report(c.Name + ": no se pudo cerrar " + left + " (puede estar preguntando si guardar algo); se intenta igual…");
                return left;
            }
            catch (Exception ex) { Log.Warn("Programas abiertos de " + c.Name + ": " + ex.Message); return null; }
        }

        /// <summary>
        /// La versión a pedirle a winget (--version): la que se ofreció. Sin ella, winget elige «la más
        /// nueva que tenga instalador para lo pedido», que con --architecture x86 y versión instalada
        /// desconocida fue WinRAR 7.01 (de 2024) en vez de la 7.23 que figuraba en la lista. null si la
        /// versión no se puede usar (recortada con «…», desconocida o con caracteres raros).
        /// </summary>
        public static string PinnedVersion(WingetRow w)
        {
            if (w == null || w.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)) return null;
            string v = (w.Available ?? "").Trim();
            if (v.Length == 0 || v.EndsWith("…") || v.EndsWith("...") || VersionUtil.Parse(v) == null || !WingetRules.IsSafeVersion(v)) return null;
            return v;
        }

        static readonly Regex FoundLine = new Regex(@"\[(?<id>[^\]\s]+)\]\s+\S+\s+(?<v>\S+)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Línea «Encontrado WinRAR [RARLab.WinRAR] Versión 7.01.0» («Found … Version …»): si la versión
        /// que winget va a instalar es más vieja que la ofrecida o que la instalada, el motivo para
        /// detenerlo (antes de que descargue); si no, null. Las dependencias tienen otro Id y no cuentan.
        /// </summary>
        public static string DowngradeGuard(string line, string id, string offered, string installed)
        {
            if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(id)) return null;
            var m = FoundLine.Match(WingetTable.CleanLine(line));
            if (!m.Success || !m.Groups["id"].Value.Equals(id, StringComparison.OrdinalIgnoreCase)) return null;
            string v = m.Groups["v"].Value;
            if (VersionUtil.Parse(v) == null) return null;
            string o = (offered ?? "").Trim();
            if (o.Length > 0 && !o.EndsWith("…") && !o.EndsWith("...") && VersionUtil.Parse(o) != null && VersionUtil.Compare(v, o) < 0)
                return "winget iba a instalar la " + v + ", más vieja que la " + o + " ofrecida";
            if (VersionUtil.Parse(installed) != null && VersionUtil.Compare(v, installed) < 0)
                return "winget iba a instalar la " + v + ", más vieja que la instalada (" + installed + ")";
            return null;
        }

        /// <summary>«install» para un paquete encontrado por nombre que winget no reconoce como instalado; si no, el pedido.</summary>
        public static string VerbFor(WingetRow w, string verb)
        {
            return verb == "upgrade" && w != null && w.InstallInsteadOfUpgrade ? "install" : verb;
        }

        /// <summary>«winget upgrade|install --id X --exact …» con los modificadores de siempre.</summary>
        string UpgradeArgs(WingetRow w, string verb)
        {
            return UpgradeArgs(w, verb, true);
        }

        /// <summary><paramref name="pin"/>: con la versión ofrecida fijada (--version), si se puede.</summary>
        string UpgradeArgs(WingetRow w, string verb, bool pin)
        {
            verb = VerbFor(w, verb);
            // --interactive es incompatible con --silent y con --disable-interactivity.
            string args = verb + " --id " + ProcessRunner.QuoteArg(w.Id) + " --exact --accept-package-agreements --accept-source-agreements" +
                          (Interactive ? " --interactive" : " --silent" + (Modern ? " --disable-interactivity" : ""));
            if (w.Source.Length > 0) args += " --source " + ProcessRunner.QuoteArg(w.Source);
            string pinned = pin ? PinnedVersion(w) : null;
            if (pinned != null) args += " --version " + ProcessRunner.QuoteArg(pinned);
            if (verb == "upgrade" && Modern && WingetTable.IsUnknownVersion(w.Version)) args += " --include-unknown";
            // Lo encontrado por nombre se instala con el mismo alcance que lo instalado (si no, elevado iba «para todo el equipo»).
            if (verb == "install" && w.InstallInsteadOfUpgrade && Modern && (w.Scope == "user" || w.Scope == "machine")) args += " --scope " + w.Scope;
            return args;
        }

        /// <summary>2.0.45: «winget download» de esa versión exacta en una carpeta (la usa la reinstalación si falla la descarga directa).</summary>
        internal string DownloadArgs(WingetRow w, string version, string folder)
        {
            string args = "download --id " + ProcessRunner.QuoteArg(w.Id) + " --exact --version " + ProcessRunner.QuoteArg(version) +
                          " --download-directory " + ProcessRunner.QuoteArg(folder) + " --accept-package-agreements --accept-source-agreements" +
                          (Modern ? " --disable-interactivity" : "");
            if (w.Source.Length > 0) args += " --source " + ProcessRunner.QuoteArg(w.Source);
            return args;
        }

        /// <summary>Sólo para las pruebas: reemplaza la ejecución de winget (recibe los argumentos).</summary>
        internal Func<string, ProcResult> RunForTests;

        /// <summary>Sólo para las pruebas: responde a «winget list --id X» después de instalar (null: no se confirma).</summary>
        internal Func<string, ProcResult> ListForTests;

        /// <summary>Sólo para las pruebas: la versión de winget que se simula.</summary>
        internal void SimulateVersion(string v) { version = v; exe = "winget"; }

        ProcResult RunUpgrade(string args, StringBuilder last, Func<string, string> guard)
        {
            if (RunForTests != null)
            {
                Log.Info("winget " + args);
                var fake = RunForTests(args);
                if (guard != null && fake.Started)
                    foreach (string line in (fake.Out ?? "").Replace("\r\n", "\n").Split('\n'))
                    {
                        string why = guard(line);
                        if (why == null) continue;
                        var stopped = new ProcResult();
                        stopped.Started = true; stopped.Aborted = true; stopped.AbortReason = why; stopped.Out = line;
                        lock (last) { last.Length = 0; last.Append(WingetTable.CleanLine(line)); }
                        return stopped;
                    }
                lock (last) { last.Length = 0; last.Append(WingetTable.CleanLine(LastLine(fake.Out))); }
                return fake;
            }
            return RunWinget(args, 45 * 60 * 1000, delegate(string line)
            {
                string l = WingetTable.CleanLine(line);
                if (l.Length == 0 || WingetTable.IsProgressLine(l)) return;
                lock (last) { last.Length = 0; last.Append(l); }
                Log.Info("  " + l);
            }, guard);
        }

        /// <summary>
        /// Versiones con que winget ve instalado el paquete ahora («winget list --id X --exact»): la misma
        /// numeración que usa para ofrecer actualizaciones. null si no se pudo consultar.
        /// </summary>
        List<string> InstalledVersionsNow(string id)
        {
            string args = "list --id " + ProcessRunner.QuoteArg(id) + " --exact" + CommonArgs;
            ProcResult r;
            if (RunForTests != null)
            {
                if (ListForTests == null) return null;
                Log.Info("winget " + args);
                r = ListForTests(args);
            }
            else r = RunWinget(args, 120000, null);
            if (r == null || !r.Started || r.TimedOut) return null;
            var list = new List<string>();
            foreach (var row in WingetTable.Parse(r.Out))
                if (row.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) list.Add(row.Version);
            if (list.Count == 0 && !r.Ok && !IsNoResults(r.ExitCode)) return null;
            return list;
        }

        /// <summary>Sólo para las pruebas: la versión que figura en el registro para el programa después de instalar.</summary>
        internal static Func<InstalledProgram, string> RegistryVersionForTests;

        /// <summary>Versión que figura AHORA en el registro para la misma entrada (o, sin entrada conocida, por nombre). "" si no se sabe.</summary>
        string RegistryVersionNow(UpdateContext ctx, InstalledProgram prog, string name)
        {
            if (RegistryVersionForTests != null) return RegistryVersionForTests(prog) ?? "";
            if (RunForTests != null) return "";
            try
            {
                string best = null;
                string n = (name ?? "").Trim().TrimEnd('…');
                foreach (var q in InventoryLoader.ReadProgramsNow(ctx.Os != null ? ctx.Os.NativeArchitecture : ""))
                {
                    // La misma entrada o, si el instalador nuevo registró otra (otro código de producto), el mismo nombre.
                    bool same = (prog != null && string.Equals(q.RegistryKey, prog.RegistryKey, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(q.Architecture, prog.Architecture, StringComparison.OrdinalIgnoreCase)) ||
                                (n.Length > 0 && string.Equals((q.Name ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase));
                    if (!same || VersionUtil.Parse(q.Version) == null) continue;
                    if (best == null || VersionUtil.IsNewer(q.Version, best)) best = q.Version;
                }
                return best ?? "";
            }
            catch (Exception ex) { Log.Warn("No se pudo releer el registro de " + name + ": " + ex.Message); return ""; }
        }

        /// <summary>
        /// El instalador terminó sin error y no cambió nada. Pasa sobre todo con el programa abierto: muchos
        /// instaladores NSIS en modo silencioso no pueden reemplazar sus archivos y terminan con 0 sin decir nada
        /// (TeraBox queda siempre en el área de notificación). Antes la fila quedaba «Actualizada» y el programa
        /// volvía a aparecer en la búsqueda siguiente.
        /// </summary>
        public static string NothingChangedText(string version, string openNames, bool interactive)
        {
            string lead = "El instalador terminó sin error, pero no cambió nada: sigue instalada la " + version + ". ";
            if (!string.IsNullOrEmpty(openNames))
                return lead + "Estaba abierto mientras se instalaba (" + openNames + "), y en modo silencioso muchos instaladores terminan sin hacer nada " +
                       "si el programa está abierto. Cerralo del todo (también desde el área de notificación) o aceptá cerrarlo cuando Actualizador lo pregunte, y reintentá";
            return lead + "Suele pasar si el programa estaba abierto (también en segundo plano o en el área de notificación) o si su instalador no admite " +
                   "el modo silencioso: cerralo y reintentá" + (interactive ? "" : "; si se repite, marcá «Mostrar la ventana de cada instalador» para ver qué dice el instalador") +
                   ". Si el programa termina de actualizarse solo en segundo plano, volvé a buscar actualizaciones en unos minutos";
        }

        /// <summary>
        /// El registro ya dice la versión nueva, pero «version.json» del programa sigue en la vieja: el instalador
        /// no pudo reemplazar los archivos (Mullvad Browser abierto: su instalador no lo comprueba y, si NSIS en
        /// silencio sigue de largo ante un archivo en uso, escribe igual la entrada de «Aplicaciones»).
        /// </summary>
        public static string PartialInstallText(string registry, string files, string source, string openNames)
        {
            return "Windows registra la " + registry + ", pero los archivos del programa siguen en la " + files + " (lo indica " + source + "): el instalador no pudo " +
                   "reemplazarlos" + (string.IsNullOrEmpty(openNames) ? ", casi siempre porque el programa estaba abierto" : " porque estaba abierto (" + openNames + ")") +
                   ". Cerralo del todo y volvé a actualizar; si Actualizador ya no lo ofrece, abrilo y usá su propio actualizador";
        }

        /// <summary>Cuánto se espera, como mucho, a que el registro muestre la versión nueva después de instalar, y cada cuánto se mira.</summary>
        internal static int ConfirmWaitMs = 20000, ConfirmPollMs = 2000;

        /// <summary>
        /// winget dijo «Instalado correctamente»: se confirma con lo que winget ve instalado ahora. Antes
        /// la fila pasaba a «Actualizada» con la versión ofrecida aunque hubiera quedado otra (WinRAR
        /// quedó en la 7.01 y la fila decía 7.23.0). Sólo se cambia el veredicto con evidencia: si winget
        /// no informa la versión, se mira el registro; si tampoco hay dato, queda «Actualizada» como antes. Desde la
        /// 1.6.7 también se miran los archivos del programa (<see cref="FilesVersion"/>): si no cambió nada es un error,
        /// si el registro quedó atrás pero los archivos llegaron es correcta, y si el registro llegó pero «version.json»
        /// no, los archivos no se pudieron reemplazar.
        /// </summary>
        void ConfirmInstalled(UpdateContext ctx, UpdateCandidate c, WingetRow w, string before, string expected, bool pinned, InstalledProgram prog, string openNames,
                              string filesBefore)
        {
            try
            {
                bool reboot = c.Status == UpdateStatus.RequiereReinicio;
                string done = reboot ? "Actualizada; requiere reiniciar" : "Actualizada correctamente";
                bool expectedKnown = VersionUtil.Parse(expected) != null && !(expected ?? "").EndsWith("…");
                bool beforeKnown = VersionUtil.Parse(before) != null;
                Predicate<string> reached = delegate(string v) { return VersionUtil.Parse(v) != null && !VersionUtil.IsNewer(expected, v); };
                // 2.0.25: si el registro ya muestra la versión pedida, alcanza: antes se le preguntaba SIEMPRE a winget («winget list --id»,
                // un proceso de varios segundos, más en un equipo viejo) por cada programa instalado.
                List<string> versions = null;
                if (expectedKnown && prog != null)
                {
                    string regNow = RegistryVersionNow(ctx, prog, w.Name);
                    if (reached(regNow)) { versions = new List<string> { regNow }; Log.Info(c.Name + ": el registro ya informa la " + regNow + " (sin consultar a winget)"); }
                }
                if (versions == null) versions = InstalledVersionsNow(w.Id);
                // Algunos instaladores terminan de registrar la versión un momento después. 2.0.17: antes se le preguntaba a winget
                // hasta 8 veces (cada «winget list» tarda varios segundos, más 4 s de pausa): más de un minuto por programa cuando la
                // versión no llegaba. Ahora se mira el registro (milisegundos) hasta ConfirmWaitMs, y a winget sólo una vez más.
                if (versions != null && versions.Count > 0 && expectedKnown && !versions.Exists(reached) && !ctx.CancelRequested && RunForTests == null)
                {
                    var waited = System.Diagnostics.Stopwatch.StartNew();
                    string first = RegistryVersionNow(ctx, prog, w.Name);
                    while (waited.ElapsedMilliseconds < ConfirmWaitMs && !ctx.CancelRequested)
                    {
                        System.Threading.Thread.Sleep(ConfirmPollMs);
                        string reg = RegistryVersionNow(ctx, prog, w.Name);
                        if (reg != first || reached(reg)) break;
                    }
                    var again = InstalledVersionsNow(w.Id);
                    if (again != null && again.Count > 0) versions = again;
                }
                var parsed = new List<string>();
                if (versions != null) foreach (string v in versions) if (VersionUtil.Parse(v) != null) parsed.Add(v);
                if (parsed.Count == 0)
                {
                    // winget no informa la versión (la correlación cambió al reinstalar): manda el registro.
                    string reg = RegistryVersionNow(ctx, prog, w.Name);
                    if (VersionUtil.Parse(reg) == null) return;      // sin evidencia: queda como estaba
                    parsed.Add(reg);
                }
                string best = parsed[0];
                foreach (string v in parsed) if (VersionUtil.IsNewer(v, best)) best = v;
                var older = new List<string>();
                foreach (string v in parsed) if (VersionUtil.IsNewer(best, v) && !older.Contains(v)) older.Add(v);
                string olderNote = older.Count > 0
                    ? ". Sigue instalada también la " + Fmt.JoinY(older) + " (otra copia del programa, quizá de otra arquitectura): si no la usás, desinstalala desde Configuración › Aplicaciones"
                    : "";
                c.InstalledVersion = best;
                string source = "";
                string files = "";
                if (prog != null) { try { files = FilesVersion(prog, best, out source); } catch (Exception ex) { Log.Warn("Versión de los archivos de " + c.Name + ": " + ex.Message); } }
                if (!expectedKnown || !VersionUtil.IsNewer(expected, best))
                {
                    // El registro llegó. «version.json» es la versión de los archivos (el ejecutable no siempre sube con el producto).
                    if (expectedKnown && files.Length > 0 && ProgramRules.IsProductVersionSource(source) && VersionUtil.IsNewer(expected, files))
                    {
                        c.Status = UpdateStatus.Fallida;
                        c.InstalledVersion = files;
                        c.Result = PartialInstallText(best, files, source, openNames);
                        Log.Warn(c.Name + ": " + c.Result);
                        return;
                    }
                    c.Result = done + olderNote;
                    return;
                }
                if (files.Length > 0 && ProgramRules.FilesAlreadyAt(files, best, expected, ProgramRules.IsProductVersionSource(source)))
                {
                    // El programa quedó en la versión pedida, pero no actualiza su entrada en «Aplicaciones».
                    c.InstalledVersion = files;
                    c.Result = done + ". Windows sigue registrando la " + best + " porque el programa no actualiza su entrada en «Aplicaciones», pero " + source + " ya indica la " + files;
                    Log.Info(c.Name + ": " + c.Result);
                    return;
                }
                if (beforeKnown && VersionUtil.IsNewer(before, best))
                {
                    c.Status = UpdateStatus.Fallida;
                    c.Result = "winget terminó bien, pero ahora figura instalada la " + best + ", MÁS VIEJA que la que había (" + before + "). " +
                               "Volvé a buscar actualizaciones e instalá la última; si se repite, actualizalo desde el sitio del fabricante";
                }
                else if (beforeKnown && VersionUtil.Compare(best, before) == 0 &&
                         !(VersionUtil.Parse(filesBefore) != null && VersionUtil.Parse(files) != null && VersionUtil.IsNewer(files, filesBefore)))
                {
                    c.Status = UpdateStatus.Fallida;
                    c.Result = NothingChangedText(best, openNames, Interactive);
                }
                else if (beforeKnown && VersionUtil.Compare(best, before) == 0)
                    // Los archivos sí cambiaron (de filesBefore a files), aunque no hasta la versión pedida ni en el registro.
                    c.Result = "winget terminó bien: " + source + " pasó de la " + filesBefore + " a la " + files + ", pero Windows sigue registrando la " + best +
                               " (se esperaba la " + expected + "). Volvé a buscar actualizaciones en unos minutos";
                else if (!beforeKnown && !pinned)
                {
                    c.Status = UpdateStatus.Fallida;
                    c.Result = "winget terminó bien, pero instaló la " + best + " y no la " + expected + " ofrecida. Volvé a buscar actualizaciones; si se repite, actualizalo desde el sitio del fabricante";
                }
                else
                    c.Result = "winget terminó bien, pero todavía figura la " + best + " (se esperaba la " + expected + "). Algunos programas terminan de " +
                               "actualizarse en segundo plano o al abrirlos: volvé a buscar actualizaciones en unos minutos" + olderNote;
                Log.Warn(c.Name + ": " + c.Result);
            }
            catch (Exception ex) { Log.Warn("No se pudo confirmar la versión de " + c.Name + ": " + ex.Message); }
        }

        static string LastLine(string text)
        {
            string last = "";
            foreach (string l in (text ?? "").Split('\n')) if (l.Trim().Length > 0) last = l.Trim();
            return last;
        }

        public const int MaxAttempts = 3;

        /// <summary>Errores de red pasajeros de la descarga (WinINet / winget): vale la pena reintentar.</summary>
        public static bool IsTransientNetwork(uint code)
        {
            switch (code)
            {
                case 0x80072EE2:   // ERROR_INTERNET_TIMEOUT
                case 0x80072EFD:   // no se pudo conectar
                case 0x80072EFE:   // conexión cortada
                case 0x80072F78:   // respuesta inválida del servidor
                case 0x80072EE7:   // no se resolvió el nombre
                case 0x80072F7D:   // error de TLS
                case 0x8A150008:   // winget: falló la descarga
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Resultado de winget en la fila. <paramref name="attempts"/>: cuántas veces se corrió;
        /// <paramref name="directNote"/>: por qué no se pudo usar la vía directa o la reinstalación (o null).
        /// </summary>
        public static void ApplyResult(UpdateCandidate c, ProcResult r, string lastLine, int attempts, string directNote)
        {
            ApplyResult(c, r, lastLine, attempts, directNote, false);
        }

        public static void ApplyResult(UpdateCandidate c, ProcResult r, string lastLine, int attempts, string directNote, bool interactive)
        {
            ApplyResult(c, r, lastLine, attempts, directNote, interactive, null, null);
        }

        /// <summary>
        /// <paramref name="diagnosis"/>: la causa según el registro de Windows Installer (o null);
        /// <paramref name="openNames"/>: procesos del programa que estaban abiertos durante la instalación (o null).
        /// </summary>
        public static void ApplyResult(UpdateCandidate c, ProcResult r, string lastLine, int attempts, string directNote, bool interactive,
                                       string diagnosis, string openNames)
        {
            uint code = unchecked((uint)r.ExitCode);
            if (!r.Started) { c.Status = UpdateStatus.Fallida; c.Result = r.Describe(); return; }
            if (r.Aborted)
            {
                c.Status = UpdateStatus.Fallida;
                c.Result = "No se instaló nada: " + r.AbortReason + ". Volvé a buscar actualizaciones; si se repite, actualizalo desde el propio programa o el sitio del fabricante";
                return;
            }
            if (r.TimedOut) { c.Status = UpdateStatus.Fallida; c.Result = "El instalador no terminó en 45 minutos"; return; }
            if (code == 0)
            {
                bool reboot = MentionsMachineRestart(r.Out + " " + r.Err);
                c.Status = reboot ? UpdateStatus.RequiereReinicio : UpdateStatus.Instalada;
                c.Result = reboot ? "Actualizada; requiere reiniciar" : "Actualizada correctamente";
                if (!string.IsNullOrEmpty(c.AvailableVersion) && !WingetTable.IsUnknownVersion(c.AvailableVersion)) c.InstalledVersion = c.AvailableVersion;
                return;
            }
            if (code == 3010 || code == RebootToFinish || code == RebootInitiated)
            {
                c.Status = UpdateStatus.RequiereReinicio;
                c.Result = "Actualizada; requiere reiniciar";
                return;
            }
            if (code == RebootBeforeInstall)
            {
                // winget lo trata como error: la instalación NO se hizo (antes figuraba «Actualizada»).
                c.Status = UpdateStatus.Fallida;
                c.RestartBeforeRetry = true;
                c.Result = RebootBeforeInstallText;
                return;
            }
            c.Status = UpdateStatus.Fallida;
            string installer = InstallerExitCode(r.Out + " " + r.Err);
            string extra = "";
            if (installer != null && installer != "0")
            {
                extra = ". " + InstallerCodeHint(installer, interactive);
                if (!string.IsNullOrEmpty(diagnosis)) extra += ". Según el registro del instalador: " + diagnosis;
                if (!string.IsNullOrEmpty(openNames)) extra += ". " + RunningPrograms.OpenHint(openNames);
            }
            else if (!string.IsNullOrEmpty(openNames) && (code == 0x8A150101 || code == 0x8A150103 || code == 0x8A150111 || code == InstallerFailed))
                extra = ". " + RunningPrograms.OpenHint(openNames);
            string tries = attempts > 1 ? " (" + attempts + " intentos)" : "";
            string direct = "";
            if (!string.IsNullOrEmpty(directNote))
            {
                if (code == WingetDirect.HashMismatch)
                    direct = ". No se pudo usar el instalador oficial verificado: " + directNote + ". Se resuelve solo cuando actualicen el catálogo (en general, pocos días); mientras tanto, actualizalo desde el propio programa";
                else if (code == WingetDirect.TechMismatch)
                    direct = ". No se pudo reinstalar automáticamente: " + directNote;
                else if (IsNotApplicable(code) || ProhibitsElevation(code))
                    direct = ". " + char.ToUpperInvariant(directNote[0]) + directNote.Substring(1);
                else
                    direct = ". Tampoco se pudo descargar directo: " + directNote;
            }
            // La última línea de winget sólo si agrega algo (en el hash, los cortes y la tecnología repite lo mismo).
            bool addLast = lastLine.Length > 0 && code != WingetDirect.HashMismatch && code != WingetDirect.TechMismatch && !IsTransientNetwork(code) && installer == null &&
                           !(IsNotApplicable(code) && direct.Length > 0);
            c.Result = Describe(code) + tries + extra + direct + (addLast ? " — " + Fmt.Truncate(lastLine, 140) : "");
        }

        static readonly Regex MachineRestart = new Regex(
            @"restart (?:your |the )?(?:pc|computer|device|system)|reboot (?:your |the )?(?:pc|computer|device|system)|(?:pc|computer|system) (?:restart|reboot) is required|" +
            @"reinici\w* (?:el|su|tu) (?:equipo|pc|sistema|dispositivo|computadora|ordenador)|es necesario reiniciar el (?:equipo|sistema)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// ¿La salida pide reiniciar el EQUIPO? «Restart the application to complete the upgrade» (App
        /// Installer) es reiniciar la aplicación, y antes contaba como «requiere reiniciar el equipo».
        /// </summary>
        public static bool MentionsMachineRestart(string text)
        {
            return !string.IsNullOrEmpty(text) && MachineRestart.IsMatch(text);
        }

        /// <summary>Qué suele significar el código de salida del instalador que informa winget.</summary>
        public static string InstallerCodeHint(string installerCode, bool interactive)
        {
            string retry = interactive ? "" : ". Probá de nuevo con «Mostrar la ventana de cada instalador» marcado";
            switch (installerCode)
            {
                case "1":
                    return "El instalador devolvió el código 1: se canceló o falló (en NSIS, «cancelado por el usuario»)" + retry;
                case "2":
                    return "El instalador devolvió el código 2: el propio instalador interrumpió la instalación. En los instaladores NSIS suele ser " +
                           "porque hay instalada otra edición o arquitectura (32 y 64 bits) del programa, o porque está abierto; cerralo y reintentá";
                case "5":
                    return "El instalador devolvió el código 5: se canceló durante la instalación (Inno Setup)" + retry;
                case "1602":
                    return "El instalador devolvió el código 1602: se canceló en su ventana";
                case "1603":
                    return "El instalador (Windows Installer) devolvió el código 1603, «error grave durante la instalación»: casi siempre es porque " +
                           "el programa sigue abierto, aunque sea en segundo plano, o porque no se pudo quitar la versión anterior";
                case "1625":
                    return "El instalador devolvió el código 1625: una directiva del equipo bloquea la instalación";
                case "1618":
                    return "El instalador devolvió el código 1618: hay otra instalación en curso; esperá a que termine y reintentá";
                case "1638":
                    return "El instalador devolvió el código 1638: Windows Installer informa otra versión instalada del mismo producto";
                default:
                    return "El instalador devolvió el código " + installerCode + "; suele significar que no acepta instalarse en silencio, que el programa está abierto o que pidió confirmación" + retry;
            }
        }

        static readonly Regex InstallerExit = new Regex(@"(?:exit code|código de salida|codigo de salida)\D{0,4}(-?\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Código de salida del instalador que winget informa en su salida (lo más útil para diagnosticar).</summary>
        public static string InstallerExitCode(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            var m = InstallerExit.Matches(output);
            return m.Count > 0 ? m[m.Count - 1].Groups[1].Value : null;
        }

        public static string Describe(uint code)
        {
            switch (code)
            {
                case 0x8A150001: return "Error interno de winget";
                case 0x8A150002: return "Argumentos inválidos";
                case 0x8A150003: return "El comando falló";
                case 0x8A150004: return "El manifiesto del paquete falló";
                case 0x8A150005: return "Interrumpido (Ctrl+C)";
                case 0x8A150006: return "El instalador del programa terminó con error";
                case 0x8A150008: return "Falló la descarga";
                case 0x8A15000A: return "El índice de los orígenes de winget está dañado (se repara con «winget source reset --force» como administrador)";
                case 0x8A15000B: return "Los orígenes de winget no son válidos (se reparan con «winget source reset --force» como administrador)";
                case 0x8A15000E: return "El paquete es un conjunto (bundle) no soportado";
                case 0x8A15000F: return "Faltan datos de los orígenes de winget: están dañados (se reparan con «winget source reset --force» como administrador)";
                case 0x8A150010: return "winget no tiene un instalador de la versión nueva que sirva para lo instalado (arquitectura, alcance equipo/usuario o versión de Windows)";
                case 0x8A150011: return "El hash del instalador no coincide con el del catálogo de winget: el fabricante reemplazó el archivo y el catálogo todavía tiene la huella del anterior";
                case 0x8A150014: return "winget no encontró el paquete instalado";
                case 0x8A150016: return "Hay varios paquetes que coinciden";
                case 0x8A150019: return "Requiere ejecutar como administrador";
                case 0x8A15001A: return "El origen no es seguro";
                case 0x8A15001B: return "Una directiva bloquea Microsoft Store";
                case 0x8A15001C: return "Una directiva bloquea esta app de Microsoft Store";
                case 0x8A15001E: return "Falló la instalación desde Microsoft Store";
                case 0x8A15002B: return "winget no tiene una actualización aplicable a lo instalado (alcance equipo/usuario, arquitectura, tecnología de instalación o versión de Windows distintos)";
                case 0x8A15002D: return "El control de seguridad de Windows rechazó el instalador (SmartScreen o zona de Internet)";
                case 0x8A15002E: return "El tamaño de la descarga no coincide con el esperado";
                case 0x8A150041: return "No se aceptaron las condiciones del paquete";
                case 0x8A150049: return "Falló la instalación con Windows Installer (MSI)";
                case 0x8A15004F: return "La versión publicada no es más nueva que la instalada";
                case 0x8A150050: return "winget no conoce la versión instalada";
                case 0x8A150056: return "El instalador no admite ejecutarse como administrador";
                case 0x8A15005C: return "No se pudo extraer el archivo comprimido del instalador";
                case 0x8A150060: return "El análisis antivirus del archivo comprimido falló o lo bloqueó";
                case 0x8A150061: return "El paquete ya está instalado";
                case 0x8A150068: return "El paquete está anclado (winget pin): no se actualiza";
                case 0x8A15007D: return "La acción no se permite como administrador";
                case 0x8A150086: return "El instalador descargado está vacío";
                case 0x8A15008E: return "La versión nueva usa otro tipo de instalador que la instalada, y winget no la actualiza: hay que desinstalar e instalar de nuevo";
                case 0x8A150101: return "El programa está en uso; cerralo y reintentá";
                case 0x8A150102: return "Hay otra instalación en curso";
                case 0x8A150103: return "Un archivo está en uso";
                case 0x8A150104: return "Falta una dependencia";
                case 0x8A150105: return "Disco lleno";
                case 0x8A150106: return "Memoria insuficiente";
                case 0x8A150107: return "Sin conexión de red";
                case 0x8A150108: return "El instalador pide contactar al soporte del fabricante";
                case 0x8A150109: return "Instalada; falta reiniciar el equipo para terminar";
                case 0x8A15010A: return "El instalador pide reiniciar el equipo antes de instalar";
                case 0x8A15010B: return "El instalador terminó e inició el reinicio del equipo";
                case 0x8A15010C: return "Cancelada por el usuario";
                case 0x8A15010D: return "Ya está instalada otra versión";
                case 0x8A15010E: return "Se requiere reducir la versión (no se hace automáticamente)";
                case 0x8A15010F: return "Bloqueada por directiva";
                case 0x8A150110: return "Falló la instalación de una dependencia";
                case 0x8A150111: return "El programa está abierto; cerralo y reintentá";
                case 0x8A150112: return "El instalador rechazó los parámetros de instalación";
                case 0x8A150113: return "El programa no es compatible con este sistema";
                case 0x8A150114: return "El paquete no admite actualización directa";
                case 0x8A150115: return "El instalador devolvió un error propio";
                case 0x80070005: return "Acceso denegado (ejecutá como administrador)";
                case 0x80072EE2: return "La descarga se cortó por tiempo de espera: el servidor del fabricante no respondió";
                case 0x80072EFD: return "No se pudo conectar con el servidor de descarga del fabricante";
                case 0x80072EFE: return "Se cortó la conexión durante la descarga";
                case 0x80072EE7: return "El DNS no resolvió el nombre del servidor de descarga";
                case 0x80072F7D: return "Falló la conexión segura (TLS) con el servidor de descarga";
                case 0x80072F78: return "El servidor de descarga devolvió una respuesta inválida";
                default: return "winget devolvió " + Fmt.Hex(unchecked((int)code));
            }
        }
    }
}
