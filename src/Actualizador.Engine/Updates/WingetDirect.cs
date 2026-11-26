using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    /// <summary>Resultado de intentar la instalación directa.</summary>
    public sealed class DirectAttempt
    {
        /// <summary>true: se descargó (o se intentó descargar) e instaló por esta vía; la fila ya tiene el resultado.</summary>
        public bool Handled;
        /// <summary>Si no se pudo intentar, por qué (se agrega al error de winget).</summary>
        public string NotApplicable;
        /// <summary>
        /// 2.0.44: no se intentó sólo porque el manifiesto no dice cómo instalarlo en silencio (Epic Online Services 5.8.0: un ZIP
        /// con un .exe adentro y ningún interruptor). Con la ventana de su instalador sí se puede: quien llama decide si se abre.
        /// </summary>
        public bool NeedsWindow;
    }

    /// <summary>
    /// Instalación sin winget, con el mismo manifiesto oficial que usa winget, para los dos casos en
    /// que winget falla sin culpa del paquete:
    ///
    /// 1. Hash que no coincide (0x8A150011, GOM Player). El fabricante reemplazó el instalador en la
    ///    misma dirección y el catálogo sigue con la huella del anterior. Como administrador, winget
    ///    no permite saltear el control. Acá se acepta el archivo nuevo SÓLO si tiene una firma
    ///    Authenticode válida, no revocada, y del MISMO firmante que los ejecutables del programa ya
    ///    instalado (o, si éste no tiene ejecutables firmados, del editor que figura en su registro).
    ///    Es decir: se reemplaza «coincide con la huella publicada» por «lo firmó quien firmó lo que
    ///    ya tenés instalado». Cualquier otra cosa se rechaza.
    /// 2. Cortes de red (0x80072EE2, TeamViewer). winget tira la descarga entera ante un corte; acá
    ///    la descarga se retoma desde donde quedó y el archivo se verifica con el SHA-256 del catálogo.
    /// </summary>
    public static class WingetDirect
    {
        public const uint HashMismatch = 0x8A150011;

        /// <summary>¿Este error de winget justifica probar la descarga directa?</summary>
        public static bool Applies(uint wingetCode)
        {
            return wingetCode == HashMismatch || WingetProvider.IsTransientNetwork(wingetCode);
        }

        /// <summary>Por qué se prueba la vía directa, para la barra de estado.</summary>
        public static string Why(uint wingetCode, string name)
        {
            return wingetCode == HashMismatch
                ? "El catálogo de winget tiene un hash viejo del instalador de " + name + ": se prueba el instalador oficial, verificado por su firma digital…"
                : "winget no pudo descargar " + name + " (" + WingetProvider.Describe(wingetCode) + "): se prueba la descarga directa, que retoma los cortes…";
        }

        public static DirectAttempt TryInstall(UpdateContext ctx, UpdateCandidate c, WingetRow w, string wingetOutput, uint wingetCode,
                                               bool interactive, Action<UpdateCandidate> changed)
        {
            var a = new DirectAttempt();
            if (!string.Equals(w.Source, "winget", StringComparison.OrdinalIgnoreCase))
            {
                a.NotApplicable = "la vía directa sólo existe para el repositorio winget";
                return a;
            }
            var prog = WingetProvider.FindProgram(ctx.Programs, w.Name, w.Version);
            bool perMachine = prog == null || !(prog.Scope ?? "").StartsWith("Usuario", StringComparison.OrdinalIgnoreCase);
            Http.EnableModernTls();

            InstallerSupport.Progress(c, changed, "Leyendo el manifiesto oficial de winget…");
            ctx.Report("Leyendo el manifiesto de " + w.Id + " en github.com/microsoft/winget-pkgs…");
            WingetManifest m;
            try { m = FetchManifest(w.Id, c.AvailableVersion); }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception ex)
            {
                a.NotApplicable = "no se pudo leer su manifiesto en github.com/microsoft/winget-pkgs (" + Http.Describe(ex) + ")";
                return a;
            }
            if (VersionUtil.Parse(c.InstalledVersion) != null && !VersionUtil.IsNewer(m.Version, c.InstalledVersion))
            {
                a.NotApplicable = "el repositorio publica la " + m.Version + ", que no es más nueva que la instalada";
                return a;
            }

            string why;
            bool naCase = WingetProvider.IsNotApplicable(wingetCode);
            bool otherScope;
            var inst = WingetRules.Choose(m, naCase ? null : wingetOutput, ctx.Os != null ? ctx.Os.NativeArchitecture : "", InstalledArch(ctx, prog),
                                          perMachine, prog != null && LooksMsi(prog), naCase, naCase ? OsVersionText(ctx) : null, out why, out otherScope);
            if (inst == null) { a.NotApplicable = why; return a; }
            if (naCase && !otherScope)
            {
                // El manifiesto tiene un instalador que acá parece aplicable y winget lo rechaza igual: winget
                // sabe algo que acá no (lo que registró al instalarlo, un anclaje): no se le pasa por encima.
                a.NotApplicable = "winget no lo considera aplicable, aunque su manifiesto oficial publica un instalador para este equipo. " +
                                  "El motivo exacto está en el registro de winget («winget upgrade --id " + w.Id + " --verbose-logs»); mientras tanto, actualizalo desde el propio programa o el sitio del fabricante";
                return a;
            }
            if (naCase && prog == null)
            {
                a.NotApplicable = "la versión nueva se publica para otro alcance (equipo/usuario) y no se encontró su entrada en «Programas instalados» para confirmarlo";
                return a;
            }
            string behavior = inst.UpgradeBehavior.ToLowerInvariant();
            if (behavior == "uninstallprevious" || behavior == "deny")
            {
                a.NotApplicable = "el paquete pide desinstalar la versión anterior antes de instalar, y eso sólo lo hace winget";
                return a;
            }
            Uri url;
            if (!Uri.TryCreate(inst.Url, UriKind.Absolute, out url) || !Http.IsHttps(url))
            {
                a.NotApplicable = "el manifiesto no publica el instalador por HTTPS";
                return a;
            }
            bool needsAdmin = inst.Scope == "machine" || (inst.Scope.Length == 0 && perMachine) ||
                              inst.Elevation.Equals("elevationRequired", StringComparison.OrdinalIgnoreCase);
            if (needsAdmin && !ctx.IsAdmin) { a.NotApplicable = "para instalarlo sin winget hay que ejecutar Actualizador como administrador"; return a; }
            if (ctx.IsAdmin && inst.Elevation.Equals("elevationProhibited", StringComparison.OrdinalIgnoreCase))
            {
                a.NotApplicable = "el paquete no admite instalarse como administrador";
                return a;
            }
            bool msiexec;
            if (WingetRules.BuildArguments(inst, "x", interactive, null, out msiexec, out why) == null)
            {
                a.NotApplicable = why;
                a.NeedsWindow = !interactive && WingetRules.HasNoSilentMode(inst);
                return a;
            }

            // Desde acá, esta vía se hace cargo del resultado.
            a.Handled = true;
            bool hashCase = wingetCode == HashMismatch;
            string dir = null;
            try
            {
                dir = BrowserProvider.NewDownloadDir();
                string file = Path.Combine(dir, FileNameFor(inst));
                ctx.Report("Descargando " + c.Name + " " + m.Version + " directamente de " + url.Host + "…");
                InstallerSupport.Progress(c, changed, "Descarga directa de " + url.Host + "…");
                int lastShown = -1;
                Http.Download(inst.Url, file, 30 * 60 * 1000, ctx, Http.IsHttps, delegate(int pct, long bytes)
                {
                    string text = pct >= 0 ? pct + " %" : (bytes / 1048576) + " MB";
                    if (pct >= 0 && pct == lastShown) return;
                    lastShown = pct;
                    ctx.Report("Descargando " + c.Name + "… " + text);
                    InstallerSupport.Progress(c, changed, "Descarga directa: " + text);
                });

                using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string bad = CheckFileKind(hold, inst);
                    if (bad != null) { InstallerSupport.Fail(c, Lead(hashCase, wingetCode) + bad + ": no se instala"); return a; }
                    string sha = InstallerSupport.Sha256(hold);
                    bool hashOk = inst.Sha256.Length == 64 && sha == inst.Sha256;
                    string note;
                    int sig = Native.VerifyAuthenticodeStrict(file, out note);
                    if (note != null) Log.Warn(c.Name + ": " + note);
                    if (sig == Native.CERT_E_REVOKED)
                    {
                        InstallerSupport.Fail(c, Lead(hashCase, wingetCode) + "el certificado que firma el instalador fue REVOCADO por su emisor: no se instala");
                        return a;
                    }
                    string how;
                    if (hashOk) how = "SHA-256 del catálogo verificado";
                    else if (!hashCase)
                    {
                        // Corte de red: es el mismo instalador que winget iba a bajar, así que tiene que coincidir
                        // con la huella del catálogo. Si no coincide, es otro archivo (otra compilación, otra
                        // versión) y no se instala aunque esté firmado.
                        Log.Warn(c.Name + ": SHA-256 descargado " + sha + ", catálogo " + inst.Sha256);
                        InstallerSupport.Fail(c, Lead(false, wingetCode) + "el archivo no coincide con el SHA-256 del catálogo: no se instala");
                        return a;
                    }
                    else
                    {
                        Log.Warn(c.Name + ": SHA-256 descargado " + sha + ", catálogo " + inst.Sha256);
                        const string mismatch = "";
                        if (sig != 0)
                        {
                            InstallerSupport.Fail(c, Lead(hashCase, wingetCode) + mismatch + "el instalador nuevo no tiene una firma digital válida (" + Fmt.Hex(sig) + "): no se instala");
                            return a;
                        }
                        string subject;
                        try { subject = Native.SignerSubject(file); }
                        catch (Exception ex) { InstallerSupport.Fail(c, Lead(hashCase, wingetCode) + "no se pudo leer la firma del instalador (" + ex.Message + "): no se instala"); return a; }
                        var signers = InstalledSigners(ctx, prog);
                        // Sin ejecutables firmados, el editor del registro sólo si es confiable (HKLM, o sin elevar).
                        string fallbackPublisher = prog != null ? TrustedRegistryPublisher(ctx, prog) : (ctx.IsAdmin ? "" : (c.Publisher ?? ""));
                        string reason = WingetRules.MatchSigner(subject, signers, fallbackPublisher, out how);
                        Log.Info(c.Name + ": firmante del instalador nuevo: " + subject);
                        if (reason != null && hashCase && signers.Count > 0)
                        {
                            // La empresa pudo cambiar de nombre (GOM Player: GRETECH CORPORATION → GOM&Company).
                            // Se acepta sólo si el firmante coincide con el editor del registro Y del manifiesto oficial.
                            string registryPub = TrustedRegistryPublisher(ctx, prog);
                            string manifestPub = FetchPublisherQuiet(w.Id, m.Version);
                            Log.Info(c.Name + ": editor en el registro «" + registryPub + "», en el manifiesto oficial «" + manifestPub + "»");
                            string how2;
                            string renamed = WingetRules.MatchRenamedPublisher(subject, signers, registryPub, manifestPub, out how2);
                            if (renamed == null) { reason = null; how = how2; }
                            else Log.Info(c.Name + ": tampoco se acepta como cambio de nombre de la empresa: " + renamed);
                        }
                        if (reason != null)
                        {
                            InstallerSupport.Fail(c, Lead(hashCase, wingetCode) + mismatch + "el instalador nuevo " + reason + ": no se instala");
                            return a;
                        }
                    }
                    Log.Info(c.Name + ": instalador verificado (" + how + ")" + (sig == 0 ? "; firma Authenticode válida" : "; sin firma Authenticode válida (" + Fmt.Hex(sig) + ")"));

                    string installFile = PrepareInstallerFile(dir, file, inst);
                    string nestedKind = inst.IsArchive ? CheckPreparedFileKind(installFile, inst) : null;
                    if (nestedKind != null) { InstallerSupport.Fail(c, Lead(hashCase, wingetCode) + nestedKind + ": no se instala"); return a; }
                    string args = WingetRules.BuildArguments(inst, installFile, interactive, MsiLog(w.Id, inst), out msiexec, out why);
                    if (args == null) { InstallerSupport.Fail(c, "No se pudo preparar el instalador oficial: " + why); return a; }
                    string exe = msiexec ? Path.Combine(SafePaths.System32, "msiexec.exe") : installFile;
                    ctx.Report("Instalando " + c.Name + " " + m.Version + " con el instalador oficial (" + how + ")…");
                    InstallerSupport.Progress(c, changed, "Instalando (descarga directa verificada)…");
                    Log.Info("Instalador de " + c.Name + ": " + Path.GetFileName(exe) + " " + args);
                    var r = ProcessRunner.RunInstaller(exe, args, 45 * 60 * 1000, BrowserProvider.InstallerEnvironment(dir), dir, hold);
                    Log.Info("Instalador de " + c.Name + " terminó: " + r.Describe());
                    if (!r.Started) { InstallerSupport.Fail(c, "No se pudo ejecutar el instalador descargado: " + r.Describe()); return a; }
                    if (r.TimedOut) { InstallerSupport.Fail(c, "El instalador descargado no terminó en 45 minutos"); return a; }
                    var exit = WingetRules.InterpretExit(inst, r.ExitCode);
                    if (!exit.Success)
                    {
                        string cause = msiexec ? MsiLogAnalyzer.ExplainFile(MsiLog(w.Id, inst)) : "";
                        InstallerSupport.Fail(c, "Instalador oficial verificado, pero " + exit.Message + (cause.Length > 0 ? ". Según el registro del instalador: " + cause : ""));
                        if (exit.RestartFirst) c.RestartBeforeRetry = true;
                        return a;
                    }
                    Finish(ctx, c, prog, m.Version, exit, Cause(hashCase, wingetCode) + "; " + how);
                    if (otherScope) NoteLeftover(c, prog, m.Version);
                }
            }
            catch (OperationCanceledByUserException)
            {
                c.Status = UpdateStatus.Fallida;
                c.Result = "Cancelada durante la descarga directa";
            }
            catch (Exception ex)
            {
                Log.Error("Descarga directa de " + c.Name, ex);
                InstallerSupport.Fail(c, hashCase
                    ? "El catálogo de winget tiene un hash viejo y no se pudo bajar el instalador nuevo: " + Http.Describe(ex)
                    : "winget no pudo descargarlo (" + WingetProvider.Describe(wingetCode) + ") y la descarga directa tampoco: " + Http.Describe(ex));
            }
            finally { if (dir != null) BrowserProvider.TryDeleteDir(dir); }
            return a;
        }

        static string Lead(bool hashCase, uint wingetCode)
        {
            if (hashCase) return "El catálogo de winget tiene un hash viejo y ";
            if (WingetProvider.IsNotApplicable(wingetCode)) return "winget no lo actualiza (el instalador se publica para otro alcance: equipo/usuario); con el instalador oficial, ";
            return "winget no pudo descargarlo; en la descarga directa ";
        }

        static string Cause(bool hashCase, uint wingetCode)
        {
            if (hashCase) return "el catálogo de winget tenía un hash viejo, se usó el instalador oficial nuevo";
            if (WingetProvider.IsNotApplicable(wingetCode))
                return "winget no lo actualiza porque el instalador se publica para otro alcance (equipo/usuario); se usó el instalador oficial del manifiesto";
            return "winget no pudo descargarlo (" + ShortNetwork(wingetCode) + "), se descargó directo";
        }

        /// <summary>Versión de Windows para comparar con MinimumOSVersion («10.0.26100.0»), o null.</summary>
        static string OsVersionText(UpdateContext ctx)
        {
            if (ctx == null || ctx.Os == null || ctx.Os.Major <= 0) return null;
            return ctx.Os.Major + "." + ctx.Os.Minor + "." + ctx.Os.Build + ".0";
        }

        /// <summary>
        /// Después de instalar con el otro alcance: si la entrada anterior sigue registrada con la versión
        /// vieja, quedaron dos copias (el instalador no reemplazó la de antes). Se avisa en la fila.
        /// </summary>
        static void NoteLeftover(UpdateCandidate c, InstalledProgram prog, string target)
        {
            if (prog == null || (c.Status != UpdateStatus.Instalada && c.Status != UpdateStatus.RequiereReinicio)) return;
            try
            {
                foreach (var q in InventoryLoader.ReadProgramsNow(""))
                {
                    if (!string.Equals(q.RegistryKey, prog.RegistryKey, StringComparison.OrdinalIgnoreCase)) continue;
                    if (VersionUtil.Parse(q.Version) == null || !VersionUtil.IsNewer(target, q.Version)) return;
                    string note = LeftoverText(prog, q.Version);
                    c.Result = (c.Result ?? "").TrimEnd('.') + ". " + note;
                    Log.Warn(c.Name + ": " + note);
                    return;
                }
            }
            catch (Exception ex) { Log.Warn("No se pudo revisar la instalación anterior de " + c.Name + ": " + ex.Message); }
        }

        public static string LeftoverText(InstalledProgram prog, string oldVersion)
        {
            bool user = prog != null && (prog.Scope ?? "").StartsWith("Usuario", StringComparison.OrdinalIgnoreCase);
            return "Sigue registrada también la " + oldVersion + " " + (user ? "instalada para tu usuario" : "instalada para todo el equipo") +
                   ": si en Configuración › Aplicaciones aparecen dos, desinstalá la de esa versión";
        }

        static string ShortNetwork(uint code)
        {
            switch (code)
            {
                case 0x80072EE2: return "tiempo de espera";
                case 0x80072EFD: return "no conectó";
                case 0x80072EFE: return "conexión cortada";
                case 0x80072EE7: return "DNS";
                case 0x80072F7D: return "TLS";
                default: return Fmt.Hex(unchecked((int)code));
            }
        }

        /// <summary>Confirma en el registro qué versión quedó instalada.</summary>
        internal static void Finish(UpdateContext ctx, UpdateCandidate c, InstalledProgram prog, string target, InstallerExit exit, string how)
        {
            string now = InstalledVersionNow(ctx, prog != null ? prog.Name : c.Name);
            string before = c.InstalledVersion;
            c.Status = exit.Reboot ? UpdateStatus.RequiereReinicio : UpdateStatus.Instalada;
            string extra = exit.Message.Length > 0 ? " (" + exit.Message + ")" : "";
            if (VersionUtil.Parse(now) != null && !VersionUtil.IsNewer(target, now))
            {
                c.InstalledVersion = now;
                c.Result = (exit.Reboot ? "Actualizada; requiere reiniciar" : "Actualizada correctamente") + extra + " — " + how;
            }
            else if (VersionUtil.Parse(now) != null && VersionUtil.Parse(before) != null && VersionUtil.Compare(now, before) == 0)
            {
                // Nada cambió en el registro: o el programa no actualiza su entrada (sus archivos lo dicen), o no se instaló.
                string source;
                string files = prog != null ? WingetProvider.FilesVersion(prog, now, out source) : (source = "");
                if (files.Length > 0 && ProgramRules.FilesAlreadyAt(files, now, target, ProgramRules.IsProductVersionSource(source)))
                {
                    c.InstalledVersion = files;
                    c.Result = (exit.Reboot ? "Actualizada; requiere reiniciar" : "Actualizada correctamente") + extra + ". Windows sigue registrando la " + now +
                               " porque el programa no actualiza su entrada en «Aplicaciones», pero " + source + " ya indica la " + files + " — " + how;
                }
                else
                {
                    c.Status = UpdateStatus.Fallida;
                    c.Result = WingetProvider.NothingChangedText(now, null, false) + " — " + how;
                }
            }
            else
            {
                if (VersionUtil.Parse(now) != null && (VersionUtil.Parse(before) == null || VersionUtil.IsNewer(now, before))) c.InstalledVersion = now;
                c.Result = "El instalador terminó bien" + extra + (VersionUtil.Parse(now) != null ? ", pero el registro indica la " + now : "") +
                           "; volvé a buscar actualizaciones para confirmar — " + how;
            }
        }

        /// <summary>Sólo para las pruebas: la versión que figura en el registro para ese nombre después de instalar.</summary>
        internal static Func<string, string> RegistryVersionForTests;

        static string InstalledVersionNow(UpdateContext ctx, string name)
        {
            if (RegistryVersionForTests != null) return RegistryVersionForTests(name) ?? "";
            try
            {
                string best = null;
                string n = (name ?? "").Trim().TrimEnd('…');
                foreach (var p in InventoryLoader.ReadProgramsNow(ctx.Os != null ? ctx.Os.NativeArchitecture : ""))
                {
                    if (p.Name == null || !p.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.Name.Length != n.Length && !(name ?? "").EndsWith("…")) continue;
                    if (best == null || VersionUtil.IsNewer(p.Version, best)) best = p.Version;
                }
                return best ?? "";
            }
            catch (Exception ex) { Log.Warn("No se pudo releer la versión de " + name + ": " + ex.Message); return ""; }
        }

        // ---------------------------------------------------------------- Reinstalación verificada

        public const uint TechMismatch = 0x8A15008E;

        /// <summary>
        /// winget no actualiza cuando la versión nueva usa otra tecnología de instalación (0x8A15008E:
        /// Double Commander instalado con su .exe de Inno Setup, y en winget sólo el MSI) y pide
        /// desinstalar e instalar de nuevo. Se hace en este orden, para no quedarse sin el programa:
        ///  1. se baja el instalador nuevo del manifiesto oficial y se verifica su SHA-256 (si no se
        ///     puede, no se desinstala nada);
        ///  2. se desinstala la versión anterior con su propio desinstalador (o msiexec) y se espera a
        ///     que desaparezca del registro (si no desaparece, no se instala la nueva: quedarían dos);
        ///  3. se instala la nueva con los interruptores que usaría winget;
        ///  4. si ese instalador fallara, se prueba una vez con «winget install».
        /// </summary>
        public static DirectAttempt TryReinstall(UpdateContext ctx, UpdateCandidate c, WingetRow w, bool interactive,
                                                 Action<UpdateCandidate> changed, Func<ProcResult> wingetInstall)
        {
            return TryReinstall(ctx, c, w, interactive, changed, wingetInstall, false);
        }

        /// <param name="quietUninstall">
        /// 2.0.44: la ventana es sólo para el instalador nuevo (no tiene modo silencioso): la versión anterior se sigue desinstalando en
        /// silencio, como siempre, y con el programa abierto no se toca nada.
        /// </param>
        public static DirectAttempt TryReinstall(UpdateContext ctx, UpdateCandidate c, WingetRow w, bool interactive,
                                                 Action<UpdateCandidate> changed, Func<ProcResult> wingetInstall, bool quietUninstall)
        {
            var a = new DirectAttempt();
            if (!string.Equals(w.Source, "winget", StringComparison.OrdinalIgnoreCase))
            {
                a.NotApplicable = "la reinstalación verificada sólo existe para el repositorio winget";
                return a;
            }
            var prog = WingetProvider.FindProgram(ctx.Programs, w.Name, w.Version);
            if (prog == null) { a.NotApplicable = "no se encontró su entrada en «Programas instalados»"; return a; }
            bool machine = (prog.RegistryKey ?? "").StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase);
            // Elevado, no se ejecuta el desinstalador que registró un usuario (su clave la puede escribir cualquiera).
            if (ctx.IsAdmin && !machine) { a.NotApplicable = "está instalado para un usuario: desinstalalo desde su sesión y volvé a instalarlo"; return a; }
            if (!ctx.IsAdmin && machine) { a.NotApplicable = "para desinstalar la versión anterior hay que ejecutar Actualizador como administrador"; return a; }
            string why;
            bool uninstallWindow = interactive && !quietUninstall;
            var un = ProgramRules.PlanUninstall(prog, uninstallWindow, out why);
            if (un == null && quietUninstall && interactive) { uninstallWindow = true; un = ProgramRules.PlanUninstall(prog, true, out why); }
            if (un == null) { a.NotApplicable = why; return a; }
            string uninstallExe = un.UsesMsiexec ? Path.Combine(SafePaths.System32, "msiexec.exe") : un.Exe;
            if (!un.UsesMsiexec)
            {
                if (!File.Exists(un.Exe)) { a.NotApplicable = "no se encuentra su desinstalador (" + un.Exe + ")"; return a; }
                if (ctx.IsAdmin && SafePaths.IsUserWritableLocation(ProgramRules.DirectoryOf(un.Exe)))
                {
                    a.NotApplicable = "su desinstalador está fuera de Program Files y de Windows (" + ProgramRules.DirectoryOf(un.Exe) + "): por seguridad no se ejecuta como administrador. Desinstalalo desde Configuración › Aplicaciones y volvé a actualizar";
                    return a;
                }
            }

            Http.EnableModernTls();
            InstallerSupport.Progress(c, changed, "Leyendo el manifiesto oficial de winget…");
            ctx.Report("Leyendo el manifiesto de " + w.Id + " en github.com/microsoft/winget-pkgs…");
            WingetManifest m;
            try { m = FetchManifest(w.Id, c.AvailableVersion); }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception ex)
            {
                a.NotApplicable = "no se pudo leer su manifiesto en github.com/microsoft/winget-pkgs (" + Http.Describe(ex) + ")";
                return a;
            }
            if (VersionUtil.Parse(c.InstalledVersion) != null && !VersionUtil.IsNewer(m.Version, c.InstalledVersion))
            {
                a.NotApplicable = "el repositorio publica la " + m.Version + ", que no es más nueva que la instalada";
                return a;
            }
            var oldTech = ProgramRules.Technology(prog);
            var inst = WingetRules.Choose(m, null, ctx.Os != null ? ctx.Os.NativeArchitecture : "", InstalledArch(ctx, prog), machine, oldTech == InstallTech.Msi, out why);
            if (inst == null) { a.NotApplicable = why; return a; }
            if (inst.Sha256.Length != 64) { a.NotApplicable = "el manifiesto no publica el SHA-256 del instalador"; return a; }
            Uri url;
            if (!Uri.TryCreate(inst.Url, UriKind.Absolute, out url) || !Http.IsHttps(url)) { a.NotApplicable = "el manifiesto no publica el instalador por HTTPS"; return a; }
            if (ctx.IsAdmin && inst.Elevation.Equals("elevationProhibited", StringComparison.OrdinalIgnoreCase)) { a.NotApplicable = "el paquete no admite instalarse como administrador"; return a; }
            if (!ctx.IsAdmin && (inst.Scope == "machine" || inst.Elevation.Equals("elevationRequired", StringComparison.OrdinalIgnoreCase)))
            {
                a.NotApplicable = "para instalarlo hay que ejecutar Actualizador como administrador";
                return a;
            }
            bool msiexec;
            if (WingetRules.BuildArguments(inst, "x", interactive, null, out msiexec, out why) == null)
            {
                a.NotApplicable = why;
                a.NeedsWindow = !interactive && WingetRules.HasNoSilentMode(inst);
                return a;
            }

            // Desde acá, esta vía se hace cargo del resultado.
            a.Handled = true;
            string oldText = ProgramRules.TechText(oldTech), newText = ProgramRules.WingetTypeText(inst.EffectiveType);
            string oldVersion = VersionUtil.Parse(c.InstalledVersion) != null ? "la " + c.InstalledVersion : "la versión anterior";
            string dir = null;
            bool uninstalled = false;
            try
            {
                dir = BrowserProvider.NewDownloadDir();
                string file = Path.Combine(dir, FileNameFor(inst));
                ctx.Report("Descargando " + c.Name + " " + m.Version + " (" + newText + ") de " + url.Host + " antes de desinstalar la versión anterior…");
                InstallerSupport.Progress(c, changed, "Descargando la versión nueva antes de desinstalar la anterior…");
                int lastShown = -1;
                Http.Download(inst.Url, file, 30 * 60 * 1000, ctx, Http.IsHttps, delegate(int pct, long bytes)
                {
                    if (pct >= 0 && pct == lastShown) return;
                    lastShown = pct;
                    string text = pct >= 0 ? pct + " %" : (bytes / 1048576) + " MB";
                    ctx.Report("Descargando " + c.Name + "… " + text);
                    InstallerSupport.Progress(c, changed, "Descarga de la versión nueva: " + text);
                });

                using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    const string nothing = ": no se desinstaló nada";
                    string bad = CheckFileKind(hold, inst);
                    if (bad != null) { InstallerSupport.Fail(c, "Reinstalación cancelada: " + bad + nothing); return a; }
                    string sha = InstallerSupport.Sha256(hold);
                    if (sha != inst.Sha256)
                    {
                        Log.Warn(c.Name + ": SHA-256 descargado " + sha + ", catálogo " + inst.Sha256);
                        InstallerSupport.Fail(c, "Reinstalación cancelada: el instalador nuevo no coincide con el SHA-256 del catálogo de winget" + nothing);
                        return a;
                    }
                    string note;
                    int sig = Native.VerifyAuthenticodeStrict(file, out note);
                    if (note != null) Log.Warn(c.Name + ": " + note);
                    if (sig == Native.CERT_E_REVOKED) { InstallerSupport.Fail(c, "Reinstalación cancelada: el certificado que firma el instalador nuevo fue REVOCADO" + nothing); return a; }
                    string installFile = PrepareInstallerFile(dir, file, inst);
                    string nestedKind = inst.IsArchive ? CheckPreparedFileKind(installFile, inst) : null;
                    if (nestedKind != null) { InstallerSupport.Fail(c, "Reinstalación cancelada: " + nestedKind + nothing); return a; }

                    string app = AppFolder(prog);
                    string running = RunningFrom(app);
                    if (running != null && !uninstallWindow)
                    {
                        InstallerSupport.Fail(c, running + " está abierto: cerralo y reintentá (no se desinstaló nada)");
                        return a;
                    }

                    // 2. Desinstalar la versión anterior.
                    ctx.Report("Desinstalando " + oldVersion + " de " + c.Name + " (" + oldText + ") para instalar la " + m.Version + " (" + newText + ")…");
                    InstallerSupport.Progress(c, changed, "Desinstalando la versión anterior (" + oldText + ")…");
                    string uninstallArgs = un.Args;
                    if (un.UsesMsiexec)
                    {
                        string ulog = InstallerSupport.MsiLogPath(w.Id, "desinstalacion");
                        if (ulog != null) uninstallArgs += " /l*v " + ProcessRunner.QuoteArg(ulog);
                    }
                    Log.Info("Desinstalador de " + c.Name + " (" + un.Description + "): " + Path.GetFileName(uninstallExe) + " " + uninstallArgs);
                    var ru = ProcessRunner.RunInstaller(uninstallExe, uninstallArgs, 20 * 60 * 1000, BrowserProvider.InstallerEnvironment(dir));
                    Log.Info("Desinstalador de " + c.Name + " terminó: " + ru.Describe());
                    if (!ru.Started) { InstallerSupport.Fail(c, "No se pudo ejecutar el desinstalador de la versión anterior (" + ru.Describe() + "); no se instaló nada"); return a; }
                    if (ru.TimedOut) { InstallerSupport.Fail(c, "El desinstalador de la versión anterior no terminó en 20 minutos; no se instaló la nueva"); return a; }
                    bool uninstallReboot = un.UsesMsiexec && (ru.ExitCode == 3010 || ru.ExitCode == 1641);
                    if (un.UsesMsiexec && ru.ExitCode != 0 && !uninstallReboot && ru.ExitCode != 1605)
                    {
                        InstallerSupport.Fail(c, "msiexec no pudo desinstalar la versión anterior (" + MsiexecText(ru.ExitCode) + "); no se instaló la nueva");
                        return a;
                    }
                    InstallerSupport.Progress(c, changed, "Esperando que termine la desinstalación…");
                    if (!WaitRemoved(ctx, prog, 180))
                    {
                        InstallerSupport.Fail(c, "La versión anterior sigue instalada después de su desinstalador (código " + ru.ExitCode + "): no se instaló la nueva para no dejar dos copias. " +
                                "Desinstalala desde Configuración › Aplicaciones y volvé a actualizar");
                        return a;
                    }
                    uninstalled = true;

                    // 3. Instalar la nueva. Para ZIP, se ejecuta el instalador anidado que declara el manifiesto.
                    string args = WingetRules.BuildArguments(inst, installFile, interactive, MsiLog(w.Id, inst), out msiexec, out why);
                    if (args == null) { InstallerSupport.Fail(c, "No se pudo preparar el instalador nuevo (" + why + "); no se instaló la nueva"); return a; }
                    string exe = msiexec ? Path.Combine(SafePaths.System32, "msiexec.exe") : installFile;
                    ctx.Report("Instalando " + c.Name + " " + m.Version + " (" + newText + ", SHA-256 del catálogo verificado)…");
                    InstallerSupport.Progress(c, changed, "Instalando la versión nueva (" + newText + ")…");
                    Log.Info("Instalador de " + c.Name + ": " + Path.GetFileName(exe) + " " + args);
                    var r = ProcessRunner.RunInstaller(exe, args, 45 * 60 * 1000, BrowserProvider.InstallerEnvironment(dir), dir, hold);
                    Log.Info("Instalador de " + c.Name + " terminó: " + r.Describe());
                    var exit = r.Started && !r.TimedOut ? WingetRules.InterpretExit(inst, r.ExitCode) : null;
                    string how = "reinstalada: la versión nueva usa otro tipo de instalador (" + oldText + " → " + newText +
                                 "), así que se desinstaló " + oldVersion + " y se instaló la " + m.Version + " verificada con el SHA-256 del catálogo";
                    if (exit != null && exit.Success)
                    {
                        if (uninstallReboot) exit.Reboot = true;
                        Finish(ctx, c, prog, m.Version, exit, how);
                        return a;
                    }
                    string failure = !r.Started ? r.Describe() : r.TimedOut ? "no terminó en 45 minutos" : exit.Message;
                    bool restartFirst = exit != null && exit.RestartFirst;
                    Log.Warn(c.Name + ": el instalador nuevo falló (" + failure + "); se prueba con winget install");

                    // 4. Última oportunidad: winget install (el programa ya no está instalado).
                    ctx.Report("El instalador nuevo de " + c.Name + " falló (" + failure + "); se prueba con winget install…");
                    InstallerSupport.Progress(c, changed, "Instalando con winget…");
                    var wr = wingetInstall != null ? wingetInstall() : null;
                    uint wcode = wr != null ? unchecked((uint)wr.ExitCode) : 0;
                    if (wr != null && wr.Started && !wr.TimedOut && !wr.Aborted && wcode == WingetProvider.RebootBeforeInstall)
                    {
                        // 0x8A15010A: winget no instaló nada, pide reiniciar ANTES. Antes se contaba como éxito
                        // y la fila decía «Actualizada» con el programa desinstalado.
                        InstallerSupport.Fail(c, "Se desinstaló " + oldVersion + ", pero el instalador nuevo pide reiniciar el equipo antes de instalarse (" + failure +
                                                 "). Reiniciá y después instalalo con «winget install --id " + w.Id + "» o volviendo a buscar actualizaciones");
                        c.InstalledVersion = "No instalada";
                        c.RestartBeforeRetry = true;
                        return a;
                    }
                    if (wr != null && wr.Started && !wr.TimedOut && !wr.Aborted &&
                        (wcode == 0 || wcode == 3010 || wcode == WingetProvider.RebootToFinish || wcode == WingetProvider.RebootInitiated))
                    {
                        var ok = new InstallerExit();
                        ok.Success = true;
                        ok.Reboot = wr.ExitCode != 0 || uninstallReboot;
                        Finish(ctx, c, prog, m.Version, ok, "reinstalada con winget install después de desinstalar la versión anterior (" + oldText + " → " + newText + ")");
                        return a;
                    }
                    InstallerSupport.Fail(c, "Se desinstaló " + oldVersion + " pero la nueva no se pudo instalar (" + failure +
                            (wr != null ? "; winget install: " + WingetProvider.Describe(unchecked((uint)wr.ExitCode)) : "") +
                            "). Instalala con «winget install --id " + w.Id + "» o desde el sitio del fabricante");
                    c.InstalledVersion = "No instalada";
                    if (restartFirst) c.RestartBeforeRetry = true;
                }
            }
            catch (OperationCanceledByUserException)
            {
                c.Status = UpdateStatus.Fallida;
                c.Result = uninstalled ? "Cancelada después de desinstalar la versión anterior: instalá la nueva con «winget install --id " + w.Id + "»"
                                       : "Cancelada antes de desinstalar: no se cambió nada";
            }
            catch (Exception ex)
            {
                Log.Error("Reinstalación de " + c.Name, ex);
                InstallerSupport.Fail(c, (uninstalled ? "Se desinstaló la versión anterior y falló la instalación de la nueva: " : "Reinstalación cancelada (no se desinstaló nada): ") + Http.Describe(ex));
            }
            finally { if (dir != null) BrowserProvider.TryDeleteDir(dir); }
            return a;
        }

        static string MsiexecText(int code)
        {
            switch (code)
            {
                case 1602: return "se canceló";
                case 1603: return "error fatal durante la desinstalación, código 1603";
                case 1618: return "hay otra instalación en curso; esperá a que termine";
                case 1625: return "una directiva del equipo lo bloquea";
                default: return "código " + code;
            }
        }

        /// <summary>Carpeta del programa: InstallLocation, o la del ejecutable principal, o la del desinstalador.</summary>
        internal static string AppFolder(InstalledProgram p)
        {
            if (p == null) return "";
            string loc = (p.InstallLocation ?? "").Trim().Trim('"').TrimEnd('\\');
            if (loc.Length > 3) return loc;
            string icon = ProgramRules.CleanIconPath(p.DisplayIcon);
            if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return ProgramRules.DirectoryOf(icon);
            return ProgramRules.DirectoryOf(ProgramRules.ExeOf(!string.IsNullOrEmpty(p.UninstallCommand) ? p.UninstallCommand : p.UninstallString));
        }

        /// <summary>El primer proceso abierto desde esa carpeta (p. ej. «doublecmd.exe»), o null.</summary>
        static string RunningFrom(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder.Length <= 3) return null;
            try
            {
                foreach (var e in ProcessList.Snapshot(true))
                    if (!string.IsNullOrEmpty(e.Path) && ProgramRules.IsInFolder(e.Path, folder))
                        return string.IsNullOrEmpty(e.Name) ? ProgramRules.FileNameOf(e.Path) : e.Name;
            }
            catch (Exception ex) { Log.Warn("Procesos abiertos en " + folder + ": " + ex.Message); }
            return null;
        }

        /// <summary>
        /// Espera a que la entrada del programa desaparezca del registro. Los desinstaladores de Inno Setup
        /// y NSIS se copian a la carpeta temporal y devuelven el control enseguida: el trabajo sigue después.
        /// </summary>
        static bool WaitRemoved(UpdateContext ctx, InstalledProgram p, int seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                if (!StillInstalled(ctx, p)) return true;
                if (DateTime.UtcNow > until || ctx.CancelRequested) return false;
                System.Threading.Thread.Sleep(2000);
            }
        }

        static bool StillInstalled(UpdateContext ctx, InstalledProgram p)
        {
            try
            {
                bool? quick = InventoryLoader.EntryExists(p);
                if (quick.HasValue) return quick.Value;
                foreach (var q in InventoryLoader.ReadProgramsNow(ctx.Os != null ? ctx.Os.NativeArchitecture : ""))
                    if (string.Equals(q.RegistryKey, p.RegistryKey, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(q.Architecture, p.Architecture, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
            catch (Exception ex) { Log.Warn("No se pudo releer el registro: " + ex.Message); return true; }
        }

        /// <summary>Arquitectura real del programa instalado (ejecutable o carpeta); si no se sabe, la vista del registro.</summary>
        internal static string InstalledArch(UpdateContext ctx, InstalledProgram p)
        {
            if (p == null) return "";
            string a = ProgramRules.DetectArchitecture(p, ctx.Os != null ? ctx.Os.NativeArchitecture : "", BrowserProvider.PeArch);
            return a.Length > 0 ? a : (p.Architecture ?? "");
        }

        /// <summary>
        /// Editor del registro, sólo si es confiable: la clave del equipo (HKLM) la escribe sólo un
        /// administrador. Elevado, la de un usuario no cuenta.
        /// </summary>
        internal static string TrustedRegistryPublisher(UpdateContext ctx, InstalledProgram p)
        {
            return TrustedRegistryPublisher(ctx, p, false);
        }

        /// <summary>
        /// 2.0.20: con <paramref name="asUser"/> el instalador se ejecuta como el usuario, sin elevar: lo que el usuario puede cambiar
        /// no le da a nadie más permisos de los que ya tiene, así que su entrada en «Aplicaciones» sirve de referencia.
        /// </summary>
        internal static string TrustedRegistryPublisher(UpdateContext ctx, InstalledProgram p, bool asUser)
        {
            if (p == null) return "";
            bool machine = (p.RegistryKey ?? "").StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase);
            return machine || !ctx.IsAdmin || asUser ? (p.Publisher ?? "") : "";
        }

        /// <summary>Editor del paquete en el repositorio oficial (manifiesto de versión y de idioma); "" si no se pudo leer.</summary>
        static string FetchPublisherQuiet(string id, string version)
        {
            try
            {
                string text = TryGetManifest(WingetRules.ManifestUrl(id, version, true));
                if (text == null) return "";
                var root = MiniYaml.Parse(text) as Dictionary<string, object>;
                if (root == null || !string.Equals(MiniYaml.Str(root, "PackageIdentifier"), id, StringComparison.OrdinalIgnoreCase)) return "";
                string locale;
                string pub = WingetRules.PublisherFrom(text, out locale);
                if (pub.Length > 0 || locale.Length == 0) return pub;
                string lt = TryGetManifest(WingetRules.LocaleUrl(id, version, locale));
                if (lt == null) return "";
                var lroot = MiniYaml.Parse(lt) as Dictionary<string, object>;
                if (lroot == null || !string.Equals(MiniYaml.Str(lroot, "PackageIdentifier"), id, StringComparison.OrdinalIgnoreCase)) return "";
                return MiniYaml.Str(lroot, "Publisher").Trim();
            }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception ex) { Log.Warn("Editor de " + id + " en winget-pkgs: " + Http.Describe(ex)); return ""; }
        }

        // ---------------------------------------------------------------- Manifiesto

        internal static WingetManifest FetchManifest(string id, string version)
        {
            string v = (version ?? "").Trim();
            string prefix = null;
            if (v.EndsWith("…") || v.EndsWith("...")) { prefix = v.TrimEnd('…', '.'); v = null; }
            else if (!WingetRules.IsSafeVersion(v)) v = null;
            if (v != null)
            {
                string text = TryGetManifest(WingetRules.ManifestUrl(id, v, false)) ?? TryGetManifest(WingetRules.ManifestUrl(id, v, true));
                if (text != null) return Validate(WingetManifest.Parse(text), id);
                Log.Info("winget-pkgs no tiene la carpeta " + v + " de " + id + ": se buscan las versiones publicadas");
            }
            string list = WingetRules.ListingUrl(id);
            if (list == null) throw new InvalidOperationException("identificador de paquete no válido");
            var versions = WingetRules.ParseListing(ManifestForTests != null ? (ManifestForTests(list) ?? "[]") : Http.GetString(list, 20000, WingetRules.IsGitHubApi, "application/vnd.github+json"));
            string best = WingetRules.PickVersion(versions, prefix);
            if (best == null) throw new InvalidOperationException("el paquete no tiene versiones publicadas");
            string t = TryGetManifest(WingetRules.ManifestUrl(id, best, false)) ?? TryGetManifest(WingetRules.ManifestUrl(id, best, true));
            if (t == null) throw new InvalidOperationException("no se encontró el manifiesto de la versión " + best);
            return Validate(WingetManifest.Parse(t), id);
        }

        /// <summary>Sólo para las pruebas: responde a las direcciones de winget-pkgs (null = 404).</summary>
        internal static Func<string, string> ManifestForTests;

        internal static string TryGetManifest(string url)
        {
            if (url == null) return null;
            if (ManifestForTests != null) return ManifestForTests(url);
            try { return Http.GetString(url, 20000, WingetRules.IsGitHubRaw, "text/plain"); }
            catch (HttpFailureException hf)
            {
                if (hf.StatusCode == 404) return null;
                throw;
            }
        }

        internal static WingetManifest Validate(WingetManifest m, string id)
        {
            if (!string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("el manifiesto no corresponde a " + id);
            if (m.Installers.Count == 0) throw new InvalidDataException("el manifiesto no tiene instaladores");
            return m;
        }

        // ---------------------------------------------------------------- Verificación

        /// <summary>Sólo para las pruebas: los firmantes de los ejecutables del programa instalado.</summary>
        internal static Func<InstalledProgram, List<string>> InstalledSignersForTests;

        internal static bool LooksMsi(InstalledProgram p)
        {
            if (p == null) return false;
            if (ProgramRules.Technology(p) == InstallTech.Msi) return true;
            string key = p.RegistryKey ?? "";
            int slash = key.LastIndexOf('\\');
            string last = slash >= 0 ? key.Substring(slash + 1) : key;
            return Regex.IsMatch(last, @"^\{[0-9A-Fa-f\-]{36}\}$") || (p.UninstallString ?? "").IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Firmantes (O= y CN=) de los ejecutables firmados de la carpeta del programa instalado.
        /// Elevado, una carpeta que puede escribir un usuario común no cuenta: alguien podría dejar
        /// ahí un ejecutable firmado por otro para que su instalador pase.
        /// </summary>
        internal static List<string> InstalledSigners(UpdateContext ctx, InstalledProgram p)
        {
            return InstalledSigners(ctx, p, false);
        }

        /// <summary>2.0.20: con <paramref name="asUser"/> (el instalador corre como el usuario) cuentan también sus carpetas.</summary>
        internal static List<string> InstalledSigners(UpdateContext ctx, InstalledProgram p, bool asUser)
        {
            var names = new List<string>();
            if (p == null) return names;
            if (InstalledSignersForTests != null) return InstalledSignersForTests(p) ?? names;
            var dirs = new List<string>();
            string loc = (p.InstallLocation ?? "").Trim().Trim('"').TrimEnd('\\');
            if (loc.Length > 0) dirs.Add(loc);
            string un = VendorProvider.DirOfUninstaller(p.UninstallString).TrimEnd('\\');
            if (un.Length > 0 && !dirs.Exists(delegate(string d) { return d.Equals(un, StringComparison.OrdinalIgnoreCase); })) dirs.Add(un);
            foreach (string d in dirs)
            {
                try
                {
                    if (!Directory.Exists(d) || SafePaths.IsUnderWindows(d)) continue;
                    if (ctx.IsAdmin && !asUser && SafePaths.IsUserWritableLocation(d)) { Log.Info("Firmantes: se ignora " + d + " (la puede escribir un usuario común)"); continue; }
                    int n = 0;
                    foreach (string f in Directory.GetFiles(d, "*.exe", SearchOption.TopDirectoryOnly))
                    {
                        if (++n > 30) break;
                        try
                        {
                            if (Native.VerifyAuthenticode(f) != 0) continue;
                            foreach (string s in WingetRules.SignerNames(Native.SignerSubject(f)))
                                if (!names.Contains(s)) names.Add(s);
                        }
                        catch (Exception qx) { Log.Quiet("Firmante de los ejecutables instalados", qx); }
                    }
                }
                catch (Exception ex) { Log.Warn("Firmantes de " + d + ": " + ex.Message); }
            }
            Log.Info(p.Name + ": firmantes de lo instalado: " + (names.Count > 0 ? string.Join(" | ", names.ToArray()) : "ninguno"));
            return names;
        }

        static string CheckFileKind(FileStream hold, WingetInstaller inst)
        {
            hold.Position = 0;
            byte[] head = new byte[8];
            int n = hold.Read(head, 0, head.Length);
            hold.Position = 0;
            if (hold.Length < 16 * 1024) return "la descarga no es un instalador (" + hold.Length + " bytes)";
            if (inst.IsArchive)
            {
                if (n < 4 || head[0] != (byte)'P' || head[1] != (byte)'K' || head[2] != 3 || head[3] != 4)
                    return "la descarga no es un archivo ZIP válido";
            }
            else if (inst.IsMsi)
            {
                byte[] ole = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
                for (int i = 0; i < ole.Length; i++) if (n < 8 || head[i] != ole[i]) return "la descarga no es un paquete MSI";
            }
            else if (n < 2 || head[0] != (byte)'M' || head[1] != (byte)'Z') return "la descarga no es un ejecutable de Windows";
            return null;
        }

        static string CheckPreparedFileKind(string path, WingetInstaller inst)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "no se encontró el instalador interno declarado por el manifiesto";
            var effective = new WingetInstaller();
            effective.Type = inst.NestedType;
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return CheckFileKind(hold, effective);
        }

        /// <summary>
        /// Extrae únicamente NestedInstallerFiles.RelativeFilePath. No admite rutas absolutas ni
        /// segmentos «..», para que un ZIP del catálogo no pueda escribir fuera de la carpeta temporal.
        /// </summary>
        static string PrepareInstallerFile(string dir, string archiveFile, WingetInstaller inst)
        {
            if (!inst.IsArchive) return archiveFile;
            string rel = (inst.NestedFile ?? "").Replace('\\', '/').Trim().TrimStart('/');
            if (rel.Length == 0 || rel.IndexOf(':') >= 0) throw new InvalidDataException("el manifiesto ZIP no declara un archivo interno seguro");
            string[] parts = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
                if (part == "." || part == "..") throw new InvalidDataException("la ruta del instalador interno del ZIP no es segura");
            string target = Path.Combine(dir, "nested-installer");
            Directory.CreateDirectory(target);
            string outFile = Path.Combine(target, Path.GetFileName(rel));
            using (ZipArchive zip = ZipFile.OpenRead(archiveFile))
            {
                ZipArchiveEntry found = null;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string ep = (entry.FullName ?? "").Replace('\\', '/').TrimStart('/');
                    if (ep.Equals(rel, StringComparison.OrdinalIgnoreCase)) { found = entry; break; }
                }
                if (found == null || string.IsNullOrEmpty(found.Name))
                    throw new InvalidDataException("el ZIP no contiene el instalador interno «" + rel + "»");
                using (Stream src = found.Open())
                using (FileStream dst = new FileStream(outFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    src.CopyTo(dst);
            }
            return outFile;
        }

        /// <summary>El nombre original del archivo (algunos instaladores se configuran por su nombre), si es seguro.</summary>
        public static string FileNameFor(WingetInstaller inst)
        {
            string ext = inst.IsArchive ? ".zip" : (inst.IsMsi ? ".msi" : ".exe");
            string name = InstallerSupport.RawLastSegment(inst.Url);
            if (!InstallerSupport.IsSafeFileName(name)) return "instalador" + ext;
            return name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? name : name + ext;
        }

        static string MsiLog(string id, WingetInstaller inst)
        {
            return inst.IsMsi ? InstallerSupport.MsiLogPath(id, null) : null;
        }
    }
}
