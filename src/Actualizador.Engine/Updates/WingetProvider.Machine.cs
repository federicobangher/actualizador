// winget: las apps de Windows «para todos los usuarios» (paquetes MSIX aprovisionados: VCLibs, Windows App Runtime, Teams…).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Updates
{
    public sealed partial class WingetProvider
    {
        /// <summary>
        /// 2.0.20 (captura del usuario, 21/11/2026): el agente de Vigía, que corre winget como servicio, listaba cinco paquetes con
        /// versión nueva (Microsoft.VCLibs.Desktop.14, Microsoft.WindowsAppRuntime.1.4/1.5/1.6 y Microsoft.Teams.Free) que el
        /// Actualizador no mostraba. Son las copias de Windows «para todos los usuarios» (aprovisionadas): las que reciben las
        /// cuentas nuevas y las que todavía no las actualizaron. «winget upgrade» sin más sólo mira las del usuario que lo ejecuta;
        /// con «--scope machine» (y como administrador, que es como corre la ventana) winget lee los paquetes aprovisionados y, al
        /// actualizar, aprovisiona la versión nueva para todo el equipo (Deployment::AddPackageMachineScope en winget-cli).
        /// </summary>
        public const string MachineSuffix = " (para todos los usuarios)";

        /// <summary>APPINSTALLER_CLI_ERROR_INSTALL_SYSTEM_NOT_SUPPORTED: winget no puede aprovisionar desde su paquete en este Windows (anterior a 24H2).</summary>
        public const uint MachineScopeNotSupported = 0x8A150113;

        /// <summary>¿Se consultan los paquetes para todos los usuarios? Hace falta administrador y winget 1.6 o posterior.</summary>
        public bool QueriesMachineScope(UpdateContext ctx)
        {
            return ctx != null && ctx.IsAdmin && VersionUtil.Compare(version, "1.6") >= 0;
        }

        /// <summary>Los argumentos de la consulta (sólo el repositorio winget: ahí están los marcos y las apps de Microsoft).</summary>
        string MachineUpgradeArgs { get { return "upgrade --scope machine --include-unknown --source winget" + CommonArgs; } }

        /// <summary>2.0.21: la misma consulta que hace el agente de Vigía, como servicio (sin --scope: lo que ve SYSTEM).</summary>
        string SystemUpgradeArgs { get { return "upgrade --include-unknown --source winget --accept-source-agreements --disable-interactivity"; } }

        /// <summary>¿La consulta respondió con una tabla (o con «no hay nada»)?</summary>
        static bool UsableTable(ProcResult r)
        {
            if (r == null || !r.Started || r.TimedOut) return false;
            return r.Ok || IsNoResults(r.ExitCode) || WingetTable.Parse(r.Out).Count > 0;
        }

        /// <summary>
        /// Agrega las filas de «winget upgrade --scope machine» que no son de lo del usuario. Esa tabla también trae los programas
        /// instalados «para todo el equipo» (registro de Windows), que ya vinieron en la consulta del usuario: se descartan los que
        /// aparecieron ahí con la misma versión instalada, o que ya están en su «winget upgrade». Lo que queda son paquetes
        /// aprovisionados. Se marcan solos los marcos (VCLibs, Windows App Runtime, UI.Xaml…) y las apps que el usuario ya tiene;
        /// una app que el usuario no tiene (Teams, si lo desinstaló) no se marca sola, porque al aprovisionarla también se le instala.
        /// Devuelve cuántas filas agregó. Pública para las pruebas.
        /// </summary>
        public static int AddMachineRows(List<WingetRow> machine, List<WingetRow> userUpgrades, List<WingetRow> userInstalled, List<UpdateCandidate> output)
        {
            return AddMachineRows(machine, userUpgrades, userInstalled, output, false);
        }

        /// <summary>2.0.21: con <paramref name="systemContext"/>, las filas vienen de winget como servicio y se instalan ahí.</summary>
        public static int AddMachineRows(List<WingetRow> machine, List<WingetRow> userUpgrades, List<WingetRow> userInstalled, List<UpdateCandidate> output, bool systemContext)
        {
            if (machine == null || output == null) return 0;
            var upgradeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (userUpgrades != null) foreach (var u in userUpgrades) upgradeIds.Add(u.Id);
            int added = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in machine)
            {
                if (w == null || w.IdTruncated || w.Id.Length == 0 || w.Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase)) continue;
                // 2.0.24 (capturas del 21/11/2026 en pc2, pc3 y pc4: Vigía decía una cosa y el Actualizador otra): un paquete de Microsoft
                // de la Store (VCLibs, Windows App Runtime, Outlook, Teams) tiene una copia por cuenta, y la que ve winget como servicio es
                // otra que la de tu usuario. Esa se muestra SIEMPRE que winget como servicio la vea vieja, aunque la tuya esté al día o
                // también tenga su propia fila: es exactamente la lista del agente de Vigía. Un programa común (del registro) es una sola
                // instalación para los dos: ése se muestra una vez, en la fila de tu usuario.
                // 2.0.29: también con «--scope machine» (cuando winget como servicio no anda): la grabación del 23/11 mostraba Vigía con
                // Windows App Runtime 1.6 (1.6.6 → 1.6.9) y el Actualizador sin esa fila, porque se la tomaba por la misma que la del usuario.
                bool separate = IsPerAccountPackage(w);
                if (upgradeIds.Contains(w.Id) && !separate) continue;
                if (!seen.Add(w.Id + "|" + w.Version)) continue;
                bool sameAsUser = false, userHas = false;
                if (userInstalled != null)
                    foreach (var u in userInstalled)
                    {
                        if (!u.Id.Equals(w.Id, StringComparison.OrdinalIgnoreCase)) continue;
                        userHas = true;
                        if (VersionUtil.Compare(u.Version, w.Version) == 0 || string.Equals(u.Version, w.Version, StringComparison.OrdinalIgnoreCase)) sameAsUser = true;
                    }
                if (sameAsUser && !separate) continue;    // es un programa del registro «para todo el equipo»: ya está en la lista
                if (w.Available.Length == 0) continue;
                if (!WingetTable.IsTruncated(w.Available) && !WingetTable.IsUnknownVersion(w.Version) &&
                    VersionUtil.Parse(w.Available) != null && !VersionUtil.IsNewer(w.Available, w.Version)) continue;
                w.MachineScope = true;
                w.SystemContext = systemContext;
                if (w.Source.Length == 0) w.Source = "winget";
                var c = new UpdateCandidate();
                c.Kind = UpdateKind.Programa;
                // Como servicio, con el mismo nombre que muestra Vigía («Microsoft.WindowsAppRuntime.1.6», «MSTeams»): se comparan de un vistazo.
                c.Name = (systemContext ? w.Name.TrimEnd('…') : FriendlyMachineName(w, userInstalled)) + MachineSuffix;
                if (c.Name == MachineSuffix) c.Name = w.Id + MachineSuffix;
                c.Publisher = w.Id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ? "Microsoft" : null;
                c.InstalledVersion = WingetTable.IsUnknownVersion(w.Version) ? "Desconocida" : w.Version;
                c.AvailableVersion = w.Available;
                c.Source = systemContext ? "winget (como servicio, lo mismo que ve Vigía)" : "winget (winget, todo el equipo)";
                c.Identifier = w.Id;
                c.ProviderName = ProviderId;
                c.ProviderTag = w;
                c.Status = UpdateStatus.Disponible;
                c.Installable = true;
                bool framework = IsFrameworkId(w.Id);
                // 2.0.24: como servicio, todo marcado (es lo que Vigía cuenta como pendiente).
                c.Recommended = systemContext || framework || userHas;
                c.Detail = (systemContext
                               ? "Lo ve winget como servicio (igual que el agente de Vigía): la copia de todo el equipo, aparte de la de tu usuario. " +
                                 "Se actualiza ejecutando winget como servicio, en ese mismo contexto, y así deja de aparecer en Vigía."
                               : "Copia para todo el equipo (paquete aprovisionado): la que reciben las cuentas nuevas. " +
                                 "Se actualiza con «winget upgrade --scope machine», que aprovisiona la versión nueva para todo el equipo.") +
                           (!systemContext && !(framework || userHas) ? " No se marca sola: tu usuario no la tiene, y al actualizarla también se te instala." : "");
                output.Add(c);
                added++;
            }
            return added;
        }

        /// <summary>
        /// 2.0.27 (grabación del usuario, 23/11/2026: «Microsoft.MicrosoftOfficeHub (para todos los usuarios)» fallaba en cada pasada, igual):
        /// si esa versión ya no se pudo actualizar para todos los usuarios en este equipo, la fila sigue (es lo que ve Vigía), pero no se
        /// marca sola y dice por qué falló la vez anterior: reintentarla da el mismo error. Se puede marcar a mano. Devuelve cuántas cambió.
        /// </summary>
        public static int ApplyMachineFailures(List<UpdateCandidate> output, MachineMemory.Snapshot memory)
        {
            int n = 0;
            if (output == null) return 0;
            foreach (var c in output)
            {
                var w = c != null ? c.ProviderTag as WingetRow : null;
                if (w == null || !w.MachineScope || c.Status != UpdateStatus.Disponible) continue;
                string date, why;
                if (!MachineMemory.TryGet(memory, MachineMemory.MachineFailureKey(w.Id, c.AvailableVersion, w.SystemContext), out date, out why)) continue;
                if (IsStructuralFailure(why))
                {
                    // 2.0.29 la pasaba a «Al día y sin verificar» y no la contaba como pendiente. 2.0.41 (usuario, 24/11/2026: «los programas
                    // que dan error, después vuelvo a buscar y aparecen donde dice todo al día por más que no esté actualizado; en Vigía sigue
                    // figurando»): una versión vieja NUNCA va a «Al día». Sigue en «Disponibles», cuenta como pendiente (igual que en Vigía) y
                    // dice por qué no se pudo; no se marca sola (daría el mismo error), pero se puede marcar a mano para reintentar.
                    c.Recommended = false;
                    c.ManualOnly = true;
                    c.Detail = "Sigue desactualizada: el " + MachineMemory.ShowDate(date) + " no se pudo actualizar para todos los usuarios porque " + why +
                               ". Es la copia que reciben las cuentas nuevas; por eso el agente de Vigía también la lista. No se marca sola ni con «Marcar todo» " +
                               "(daría el mismo error); se puede marcar a mano para reintentar cuando haya un paquete nuevo. " + ForEachAccountText(w) + " " + (c.Detail ?? "");
                    n++;
                    continue;
                }
                c.Recommended = false;
                c.ManualOnly = true;
                c.Detail = "El " + MachineMemory.ShowDate(date) + " no se pudo actualizar a esta versión para todos los usuarios" + (string.IsNullOrEmpty(why) ? "" : " (" + why + ")") +
                           ". No se marca sola ni con «Marcar todo», para no repetir el mismo error en cada pasada (se puede marcar a mano). " + ForEachAccountText(w) + " " + (c.Detail ?? "");
                n++;
            }
            return n;
        }

        /// <summary>
        /// 2.0.29: ¿la falla anotada no depende del momento? winget no tiene una actualización aplicable Y el manifiesto no trae ni un
        /// paquete MSIX ni un instalador que se pueda ejecutar en silencio: no hay con qué. Pública para las pruebas.
        /// </summary>
        public static bool IsStructuralFailure(string why)
        {
            string w = why ?? "";
            return w.IndexOf("no publica un paquete MSIX", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   w.IndexOf("tampoco un instalador", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Qué pasa con la copia de cada usuario cuando la de todo el equipo no se puede actualizar.</summary>
        static string ForEachAccountText(WingetRow w)
        {
            return IsPerAccountPackage(w) || (w.Id ?? "").StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                ? "La app de cada usuario la sigue actualizando Microsoft Store en su cuenta; esta copia vieja es la que reciben las cuentas nuevas."
                : "";
        }

        /// <summary>
        /// 2.0.24: ¿es un paquete de Microsoft con una copia por cuenta (MSIX)? Como servicio, winget lo muestra con el nombre del paquete,
        /// sin espacios («Microsoft.VCLibs.140.00.UWPDesktop», «MSTeams», «Microsoft.OutlookForWindows»), y su identificador es de
        /// Microsoft. Un programa del registro lleva su nombre para mostrar («Microsoft Edge», «PDF24 Creator 11.34.0»). Pública para las pruebas.
        /// </summary>
        public static bool IsPerAccountPackage(WingetRow w)
        {
            if (w == null || !w.Id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) return false;
            string n = (w.Name ?? "").Trim().TrimEnd('…');
            return n.Length > 0 && n.IndexOf(' ') < 0;
        }

        /// <summary>¿Es un marco de ejecución (lo usan otras apps; no se abre solo)? Público para las pruebas.</summary>
        public static bool IsFrameworkId(string id)
        {
            string s = (id ?? "").ToLowerInvariant();
            return s.StartsWith("microsoft.vclibs") || s.StartsWith("microsoft.windowsappruntime") || s.StartsWith("microsoft.ui.xaml") ||
                   s.StartsWith("microsoft.dotnet.native") || s.StartsWith("microsoft.net.native") || s.StartsWith("microsoft.directx");
        }

        /// <summary>
        /// Como servicio, winget muestra el nombre del paquete («Microsoft.VCLibs.140.00.UWPDesktop», «MicrosoftTeams»); si la misma
        /// app está en la lista del usuario, se usa el nombre que muestra ahí («Microsoft Visual C++ 2015 UWP Desktop Runtime Package»).
        /// </summary>
        static string FriendlyMachineName(WingetRow w, List<WingetRow> userInstalled)
        {
            if (userInstalled != null)
                foreach (var u in userInstalled)
                    if (u.Id.Equals(w.Id, StringComparison.OrdinalIgnoreCase) && u.Name.Length > 0 && !WingetTable.IsTruncated(u.Name)) return u.Name;
            string n = w.Name.TrimEnd('…');
            return n.Length > 0 ? n : w.Id;
        }

        /// <summary>«winget upgrade --scope machine» de un paquete aprovisionado.</summary>
        string MachineInstallArgs(WingetRow w)
        {
            string args = "upgrade --id " + ProcessRunner.QuoteArg(w.Id) + " --exact --scope machine --accept-package-agreements --accept-source-agreements" +
                          " --silent" + (Modern ? " --disable-interactivity" : "") + " --source " + ProcessRunner.QuoteArg(w.Source.Length > 0 ? w.Source : "winget");
            if (WingetTable.IsUnknownVersion(w.Version)) args += " --include-unknown";
            return args;
        }

        /// <summary>2.0.22: como servicio, sin --scope: el mismo contexto en que winget lo vio.</summary>
        string SystemInstallArgs(WingetRow w)
        {
            return MachineInstallArgs(w).Replace(" --scope machine", "");
        }

        /// <summary>0x8A150014: winget no encontró el paquete instalado (con ese alcance).</summary>
        public static bool IsNoApplications(uint code) { return code == 0x8A150014; }

        static bool IsRebootSuccess(uint code) { return code == RebootToFinish || code == RebootInitiated; }

        /// <summary>Versiones aprovisionadas ahora de ese paquete («winget list --id X --exact --scope machine»), o null si no se supo.</summary>
        List<string> MachineVersionsNow(string id)
        {
            string args = "list --id " + ProcessRunner.QuoteArg(id) + " --exact --scope machine" + CommonArgs;
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

        /// <summary>Versiones de ese paquete que ve winget como servicio («winget list --id X --exact»), o null si no se supo.</summary>
        List<string> SystemVersionsNow(UpdateContext ctx, string id)
        {
            var r = SystemWinget.Run(ctx, "list --id " + ProcessRunner.QuoteArg(id) + " --exact --accept-source-agreements --disable-interactivity", 180000);
            if (r == null || !r.Started || r.TimedOut) return null;
            var list = new List<string>();
            foreach (var row in WingetTable.Parse(r.Out))
                if (row.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) list.Add(row.Version);
            if (list.Count == 0 && !r.Ok && !IsNoResults(r.ExitCode)) return null;
            return list;
        }

        /// <summary>¿Ya no queda aprovisionada una versión anterior a la ofrecida?</summary>
        public static bool MachineReached(List<string> versionsNow, string offered)
        {
            if (versionsNow == null) return false;
            if (versionsNow.Count == 0) return true;           // ya no figura con versión vieja: se reemplazó
            if (VersionUtil.Parse(offered) == null) return false;
            foreach (string v in versionsNow)
                if (VersionUtil.Parse(v) == null || VersionUtil.IsNewer(offered, v)) return false;
            return true;
        }

        /// <summary>Sólo para las pruebas: reemplaza la descarga y el aprovisionamiento por PowerShell (dirección → resultado).</summary>
        internal static Func<string, ProcResult> ProvisionForTests;

        /// <summary>Actualiza un paquete aprovisionado. El resultado queda en la fila.</summary>
        void InstallMachine(UpdateContext ctx, UpdateCandidate c, WingetRow w, Action<UpdateCandidate> changed)
        {
            if (!ctx.IsAdmin)
            {
                InstallerSupport.Fail(c, "Las apps para todos los usuarios sólo se actualizan con Actualizador como administrador");
                return;
            }
            var last = new StringBuilder();
            ProcResult r;
            if (w.SystemContext)
            {
                // 2.0.22 (captura del 21/11/2026: «winget no encontró el paquete instalado» en WindowsAppRuntime 1.4/1.5/1.6): como
                // servicio, el paquete está en lo que ve SYSTEM, no entre los aprovisionados: con «--scope machine» winget no lo
                // encontraba (0x8A150014). Primero exactamente como se vio (sin --scope, lo mismo que «winget upgrade --all» del
                // agente de Vigía); si no anda, con «--scope machine» (un MSIX se aprovisiona para todo el equipo).
                InstallerSupport.Progress(c, changed, "Actualizando con winget como servicio…");
                r = SystemWinget.Run(ctx, SystemInstallArgs(w), 45 * 60 * 1000);
                if (r.Started && !r.TimedOut && !r.Ok && !ctx.CancelRequested && !IsRebootSuccess(unchecked((uint)r.ExitCode)))
                {
                    Log.Info(c.Name + ": como servicio sin --scope respondió " + Describe(unchecked((uint)r.ExitCode)) + "; se repite con --scope machine");
                    var scoped = SystemWinget.Run(ctx, MachineInstallArgs(w), 45 * 60 * 1000);
                    if (scoped.Started && !scoped.TimedOut && (scoped.Ok || !IsNoApplications(unchecked((uint)scoped.ExitCode)))) r = scoped;
                }
                if (!r.Started) Log.Warn(c.Name + ": winget como servicio no arrancó (" + r.StartError + ")");
            }
            else r = RunUpgrade(MachineInstallArgs(w), last, null);
            uint code = unchecked((uint)r.ExitCode);
            Log.Info(c.Name + ": winget " + (w.SystemContext ? "como servicio" : "--scope machine") + " terminó: " + r.Describe());
            if (ctx.CancelRequested && !r.Ok) { c.Status = UpdateStatus.Pendiente; c.Result = "Cancelada"; return; }
            List<string> now = r.Started && !r.TimedOut ? (w.SystemContext ? SystemVersionsNow(ctx, w.Id) : MachineVersionsNow(w.Id)) : null;
            // 2.0.23: si winget terminó bien, está instalada aunque todavía liste la vieja (un marco convive en varias versiones); antes se
            // volvía a instalar por la vía directa, que con un programa común (PDF24) repetía su instalador sin necesidad.
            if (r.Ok || IsRebootSuccess(code))
            {
                c.Status = IsRebootSuccess(code) ? UpdateStatus.RequiereReinicio : UpdateStatus.Instalada;
                c.InstalledVersion = c.AvailableVersion;
                c.Result = (w.SystemContext ? "Actualizada para todo el equipo (winget como servicio instaló la " + c.AvailableVersion + ")"
                                            : "Actualizada para todos los usuarios (winget aprovisionó la " + c.AvailableVersion + ")") +
                           (c.Status == UpdateStatus.RequiereReinicio ? "; requiere reiniciar" : "") +
                           (now != null && !MachineReached(now, c.AvailableVersion) ? ". winget todavía lista también la " + Fmt.JoinY(now) +
                                ": es una copia vieja que convive con la nueva y Windows quita cuando ninguna app la usa" : "");
                return;
            }
            // winget en su paquete no puede aprovisionar en Windows anteriores a 24H2 (0x8A150113), o no cambió nada: se aprovisiona
            // directamente con el paquete oficial del mismo manifiesto, verificado con su SHA-256 (Windows exige además la firma de Microsoft).
            string wingetWhy = r.Started ? (r.TimedOut ? "winget no terminó a tiempo" : "winget respondió: " + Describe(code)) : "winget no arrancó: " + (r.StartError ?? r.Describe());
            ctx.Report(c.Name + ": " + wingetWhy + "; se aprovisiona directamente el paquete oficial…");
            InstallerSupport.Progress(c, changed, "Aprovisionando el paquete oficial verificado…");
            string why;
            ProcResult p = ProvisionDirect(ctx, c, w, out why);
            if (p == null)
            {
                // 2.0.41: sin paquete ni instalador en el manifiesto (Outlook, Office Hub…): si alguna cuenta del equipo ya tiene esa versión
                // (la Store la actualiza en cada cuenta), Windows la aprovisiona para todos a partir de esa copia
                // (PackageManager.ProvisionPackageForAllUsersAsync). Sin bajar nada: es el paquete firmado por Microsoft que ya está instalado.
                string whyCopy;
                InstallerSupport.Progress(c, changed, "Aprovisionando para todos la versión que ya tiene una cuenta del equipo…");
                ProcResult fromInstalled = ProvisionFromInstalled(ctx, c, w, out whyCopy);
                if (fromInstalled != null && fromInstalled.Started && !fromInstalled.TimedOut && WingetSetup.ScriptSucceeded(fromInstalled.Out, out whyCopy))
                {
                    now = w.SystemContext ? SystemVersionsNow(ctx, w.Id) : MachineVersionsNow(w.Id);
                    c.Status = UpdateStatus.Instalada;
                    c.InstalledVersion = c.AvailableVersion;
                    c.Result = "Actualizada para todos los usuarios (Windows aprovisionó la " + c.AvailableVersion + " que ya tenía instalada una cuenta del equipo)" +
                               (now != null && !MachineReached(now, c.AvailableVersion) ? ". winget todavía lista la " + Fmt.JoinY(now) + ": se reemplaza cuando cada usuario inicia sesión" : "");
                    return;
                }
                if (!string.IsNullOrEmpty(whyCopy)) why += "; " + whyCopy;
                InstallerSupport.Fail(c, "No se pudo actualizar para todos los usuarios: " + wingetWhy + "; y " + why);
                MachineMemory.Put(MachineMemory.MachineFailureKey(w.Id, c.AvailableVersion, w.SystemContext), wingetWhy + "; " + why);
                return;
            }
            string error;
            if (!p.Started || p.TimedOut || !WingetSetup.ScriptSucceeded(p.Out, out error))
            {
                error = !p.Started || p.TimedOut ? p.Describe() : ProvisionError(p.Out);
                InstallerSupport.Fail(c, "No se pudo actualizar para todos los usuarios: " + wingetWhy + "; al aprovisionarlo directamente, Windows respondió: " + Fmt.Truncate(error, 300));
                MachineMemory.Put(MachineMemory.MachineFailureKey(w.Id, c.AvailableVersion, w.SystemContext), wingetWhy + "; Windows respondió: " + Fmt.Truncate(error, 200));
                return;
            }
            now = w.SystemContext ? SystemVersionsNow(ctx, w.Id) : MachineVersionsNow(w.Id);
            c.Status = UpdateStatus.Instalada;
            c.InstalledVersion = c.AvailableVersion;
            c.Result = ((p.Out ?? "").Contains("instalador oficial") ? "Actualizada para todo el equipo (instalador oficial, SHA-256 verificado)"
                                                                     : "Actualizada para todos los usuarios (paquete oficial aprovisionado, SHA-256 verificado)") +
                       (now != null && !MachineReached(now, c.AvailableVersion) ? ". winget todavía lista la " + Fmt.JoinY(now) + (w.SystemContext ? ": es una copia vieja que convive con la nueva y Windows quita cuando ninguna app la usa" : ": se reemplaza cuando cada usuario inicia sesión") : "");
        }

        /// <summary>Sólo para las pruebas: reemplaza el aprovisionamiento desde la copia instalada (guion → resultado).</summary>
        internal static Func<string, ProcResult> ProvisionInstalledForTests;

        /// <summary>
        /// 2.0.41: los nombres de paquete (Get-AppxPackage -Name) que puede tener esta fila: el que muestra winget como servicio
        /// («Microsoft.MicrosoftOfficeHub», «Microsoft.OutlookForWindows») y el identificador. Sólo letras, números, puntos y guiones.
        /// </summary>
        public static List<string> PackageNamesOf(WingetRow w)
        {
            var list = new List<string>();
            if (w == null) return list;
            foreach (string s in new[] { (w.Name ?? "").Trim().TrimEnd('…'), (w.Id ?? "").Trim() })
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9.\-]{2,63}$") && s.IndexOf('.') > 0 &&
                    !list.Exists(delegate(string x) { return x.Equals(s, StringComparison.OrdinalIgnoreCase); }))
                    list.Add(s);
            return list;
        }

        /// <summary>
        /// 2.0.41: el guion que aprovisiona para todos los usuarios la versión <paramref name="offered"/> (o más nueva) que ya tiene
        /// instalada alguna cuenta del equipo. «PROV-OK» si quedó; «PROV-ERR: …» si ninguna cuenta la tiene o Windows no pudo. Público para las pruebas.
        /// </summary>
        public static string ProvisionFromInstalledScript(List<string> names, string offered)
        {
            if (names == null || names.Count == 0) throw new ArgumentException("sin nombre de paquete");
            foreach (string n in names)
                if (!System.Text.RegularExpressions.Regex.IsMatch(n ?? "", @"^[A-Za-z0-9][A-Za-z0-9.\-]{2,63}$")) throw new ArgumentException("nombre de paquete no válido: " + n);
            if (!System.Text.RegularExpressions.Regex.IsMatch(offered ?? "", @"^\d{1,9}(\.\d{1,9}){1,3}$")) throw new ArgumentException("versión no válida: " + offered);
            var sb = new StringBuilder();
            sb.Append("$ErrorActionPreference='Stop';try{$want=[version]").Append(WingetSetup.PsQuote(offered)).Append(";$p=$null;");
            sb.Append("foreach($n in @(");
            for (int i = 0; i < names.Count; i++) sb.Append(i > 0 ? "," : "").Append(WingetSetup.PsQuote(names[i]));
            sb.Append(")){if(-not $p){$p=Get-AppxPackage -AllUsers -Name $n -ErrorAction SilentlyContinue|Where-Object{-not $_.IsFramework -and [version]$_.Version -ge $want}|");
            sb.Append("Sort-Object {[version]$_.Version} -Descending|Select-Object -First 1}};");
            sb.Append("if(-not $p){'PROV-ERR: ninguna cuenta del equipo tiene instalada la '+$want+' para aprovisionarla';return};");
            sb.Append("Add-Type -AssemblyName System.Runtime.WindowsRuntime;");
            sb.Append("$null=[Windows.Management.Deployment.PackageManager,Windows.Management.Deployment,ContentType=WindowsRuntime];");
            sb.Append("$pm=New-Object Windows.Management.Deployment.PackageManager;");
            sb.Append("$op=$pm.ProvisionPackageForAllUsersAsync($p.PackageFamilyName);");
            sb.Append("$m=[System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object{$_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and ");
            sb.Append("$_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperationWithProgress`2'}|Select-Object -First 1;");
            sb.Append("$t=$m.MakeGenericMethod([Windows.Management.Deployment.DeploymentResult],[Windows.Management.Deployment.DeploymentProgress]).Invoke($null,@($op));");
            sb.Append("if(-not $t.Wait(600000)){'PROV-ERR: Windows no terminó de aprovisionarla a tiempo';return};");
            sb.Append("$r=$t.Result;if($r.ExtendedErrorCode -and $r.ExtendedErrorCode.HResult -ne 0){'PROV-ERR: '+$r.ErrorText}else{'PROV-OK '+$p.Version}");
            sb.Append("}catch{'PROV-ERR: '+$_.Exception.Message}");
            return sb.ToString();
        }

        /// <summary>2.0.41: aprovisiona desde la copia ya instalada en alguna cuenta (ver <see cref="ProvisionFromInstalledScript"/>). null si no aplica.</summary>
        ProcResult ProvisionFromInstalled(UpdateContext ctx, UpdateCandidate c, WingetRow w, out string why)
        {
            why = null;
            var names = PackageNamesOf(w);
            if (names.Count == 0) { why = "no tiene un nombre de paquete de Windows con el cual buscarla en las cuentas"; return null; }
            string offered = (c.AvailableVersion ?? "").Trim();
            string script;
            try { script = ProvisionFromInstalledScript(names, offered); }
            catch (ArgumentException ex) { why = "no se puede aprovisionar desde una cuenta (" + ex.Message + ")"; return null; }
            if (ProvisionInstalledForTests != null) return ProvisionInstalledForTests(script);
            ctx.Report(c.Name + ": aprovisionando para todos los usuarios la " + offered + " que ya tiene una cuenta del equipo…");
            var r = ProcessRunner.PowerShell(script, 12 * 60 * 1000);
            Log.Info(c.Name + ": aprovisionamiento desde la copia instalada: " + r.Describe() + " — " + Fmt.Truncate((r.Out ?? "").Replace("\r", " ").Replace("\n", " | "), 500));
            return r;
        }

        static string ProvisionError(string output)
        {
            string e;
            WingetSetup.ScriptSucceeded(output, out e);
            return e ?? "sin detalle";
        }

        /// <summary>
        /// El guion que aprovisiona un paquete para todo el equipo (Add-AppxProvisionedPackage). «PROV-OK» si quedó; una versión igual
        /// o más nueva ya aprovisionada (0x80073D06) tampoco es un error. Público para las pruebas.
        /// </summary>
        public static string ProvisionScript(string package)
        {
            return "$newer=-2147009274;try{Add-AppxProvisionedPackage -Online -PackagePath " + WingetSetup.PsQuote(package) +
                   " -SkipLicense -ErrorAction Stop|Out-Null;'PROV-OK'}catch{if($_.Exception.HResult -eq $newer){'PROV-OK'}else{'PROV-ERR: '+$_.Exception.Message}}";
        }

        /// <summary>El instalador del manifiesto que sirve para aprovisionar en esta arquitectura (MSIX/APPX, suelto o dentro de un .zip), o null.</summary>
        public static WingetInstaller ProvisionableInstaller(WingetManifest m, string nativeArch, out string why)
        {
            why = null;
            string arch = (nativeArch ?? "").ToLowerInvariant();
            if (arch.Length == 0 || arch == "amd64") arch = "x64";
            WingetInstaller best = null;
            foreach (var i in m.Installers)
            {
                string t = i.EffectiveType;
                if (t != "msix" && t != "appx") continue;
                if (i.IsArchive && string.IsNullOrEmpty(i.NestedFile)) continue;
                if (i.Architecture == arch) return i;
                if (best == null && (i.Architecture == "neutral" || i.Architecture.Length == 0)) best = i;
            }
            if (best == null) why = "su manifiesto no publica un paquete MSIX para " + arch;
            return best;
        }

        /// <summary>
        /// 2.0.22: el instalador del manifiesto que se puede ejecutar en silencio como administrador (exe, Inno, NSIS, Burn, MSI…) para
        /// esta arquitectura, con su SHA-256; o null y por qué. Pública para las pruebas.
        /// </summary>
        public static WingetInstaller OfficialExeInstaller(WingetManifest m, string nativeArch, out string why)
        {
            why = null;
            string arch = (nativeArch ?? "").ToLowerInvariant();
            if (arch.Length == 0 || arch == "amd64") arch = "x64";
            WingetInstaller best = null;
            foreach (var i in m.Installers)
            {
                if (i.IsArchive || i.IsMsi || !WingetRules.IsRunnableType(i)) continue;
                if (i.Elevation.Equals("elevationProhibited", StringComparison.OrdinalIgnoreCase) || i.Scope == "user") continue;
                if ((i.Sha256 ?? "").Length != 64 || string.IsNullOrEmpty(i.Switch("Silent"))) continue;
                if (i.Architecture == arch) { best = i; break; }
                if (best == null && arch == "arm64" && i.Architecture == "x64") best = i;
                if (best == null && (i.Architecture == "neutral" || i.Architecture.Length == 0)) best = i;
            }
            if (best == null) why = "tampoco un instalador que se pueda ejecutar en silencio para " + arch;
            return best;
        }

        /// <summary>Sólo para las pruebas: reemplaza la descarga y ejecución del instalador oficial (dirección, argumentos → código de salida).</summary>
        internal static Func<string, string, int> OfficialExeForTests;

        /// <summary>Baja el instalador oficial, lo acepta sólo con el SHA-256 del manifiesto (lo mismo que exige winget) y lo ejecuta en silencio.</summary>
        ProcResult RunOfficialExe(UpdateContext ctx, UpdateCandidate c, WingetManifest m, WingetInstaller inst, out string why)
        {
            why = null;
            Uri url;
            if (!Uri.TryCreate(inst.Url, UriKind.Absolute, out url) || !Http.IsHttps(url)) { why = "el manifiesto no publica el instalador por HTTPS"; return null; }
            string args = inst.Switch("Silent");
            if (!InstallerRules.IsSafeArgs(args)) { why = "interruptores del instalador no permitidos"; return null; }
            int code;
            if (OfficialExeForTests != null) code = OfficialExeForTests(inst.Url, args);
            else
            {
                string dir = BrowserProvider.NewDownloadDir();
                try
                {
                    string file = Path.Combine(dir, "instalador.exe");
                    ctx.Report("Descargando el instalador oficial de " + c.Name + " " + m.Version + " de " + url.Host + "…");
                    Http.Download(inst.Url, file, 30 * 60 * 1000, ctx, Http.IsHttps);
                    using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        string got = InstallerSupport.Sha256(hold);
                        if (!got.Equals(inst.Sha256, StringComparison.OrdinalIgnoreCase)) { why = "el SHA-256 del instalador no coincide con el del catálogo de winget: no se instala"; return null; }
                        ctx.Report("Instalando " + c.Name + " " + m.Version + " (instalador oficial, para todo el equipo)…");
                        Log.Info(c.Name + ": instalador oficial verificado (SHA-256 del catálogo de winget): instalador.exe " + args);
                        var r = ProcessRunner.RunInstaller(file, args, 30 * 60 * 1000, BrowserProvider.InstallerEnvironment(dir), dir, hold);
                        Log.Info(c.Name + ": instalador oficial terminó: " + r.Describe());
                        if (!r.Started || r.TimedOut) { why = "el instalador oficial no " + (r.TimedOut ? "terminó en 30 minutos" : "arrancó (" + r.Describe() + ")"); return null; }
                        code = r.ExitCode;
                    }
                }
                catch (OperationCanceledByUserException) { throw; }
                catch (Exception ex) { why = "no se pudo descargar el instalador oficial (" + Http.Describe(ex) + ")"; return null; }
                finally { BrowserProvider.TryDeleteDir(dir); }
            }
            var exit = WingetRules.InterpretExit(inst, code);
            var result = new ProcResult { Started = true, ExitCode = 0 };
            result.Out = exit.Success ? "PROV-OK (instalador oficial)" : "PROV-ERR: el instalador oficial devolvió " + (exit.Message.Length > 0 ? exit.Message : "el código " + code);
            return result;
        }

        ProcResult ProvisionDirect(UpdateContext ctx, UpdateCandidate c, WingetRow w, out string why)
        {
            why = null;
            WingetManifest m;
            try { m = WingetDirect.FetchManifest(w.Id, c.AvailableVersion); }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception ex) { why = "no se pudo leer su manifiesto en github.com/microsoft/winget-pkgs (" + Http.Describe(ex) + ")"; return null; }
            string native = ctx.Os != null ? ctx.Os.NativeArchitecture : "";
            var inst = ProvisionableInstaller(m, native, out why);
            if (inst == null)
            {
                // 2.0.22: Windows App Runtime no publica un .msix: su instalador oficial (WindowsAppRuntimeInstall-x64.exe --quiet),
                // ejecutado como administrador, instala y aprovisiona sus paquetes para todo el equipo. Es lo que ejecutaría winget.
                string whyExe;
                var exe = OfficialExeInstaller(m, native, out whyExe);
                if (exe == null) { why += "; " + whyExe; return null; }
                return RunOfficialExe(ctx, c, m, exe, out why);
            }
            Uri url;
            if (!Uri.TryCreate(inst.Url, UriKind.Absolute, out url) || !Http.IsHttps(url)) { why = "el manifiesto no publica el paquete por HTTPS"; return null; }
            if ((inst.Sha256 ?? "").Length != 64) { why = "el manifiesto no publica el SHA-256 del paquete"; return null; }
            if (ProvisionForTests != null) return ProvisionForTests(inst.Url);
            string dir = BrowserProvider.NewDownloadDir();
            try
            {
                string file = Path.Combine(dir, inst.IsArchive ? "paquete.zip" : "paquete." + (inst.EffectiveType == "appx" ? "appx" : "msix"));
                ctx.Report("Descargando " + c.Name + " " + m.Version + " de " + url.Host + "…");
                Http.Download(inst.Url, file, 30 * 60 * 1000, ctx, Http.IsHttps);
                string got;
                using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) got = InstallerSupport.Sha256(f);
                if (!got.Equals(inst.Sha256, StringComparison.OrdinalIgnoreCase)) { why = "el SHA-256 de la descarga no coincide con el del catálogo de winget: no se instala"; return null; }
                string package = file;
                if (inst.IsArchive)
                {
                    string entryName = inst.NestedFile.Replace('\\', '/');
                    string target = Path.Combine(dir, Path.GetFileName(entryName));
                    if (!InstallerSupport.IsSafeFileName(Path.GetFileName(entryName))) { why = "el manifiesto nombra un archivo no permitido dentro del .zip"; return null; }
                    using (var zip = ZipFile.OpenRead(file))
                    {
                        var entry = zip.GetEntry(entryName);
                        if (entry == null) { why = "el .zip no trae «" + entryName + "»"; return null; }
                        entry.ExtractToFile(target, true);
                    }
                    package = target;
                }
                ctx.Report("Aprovisionando " + c.Name + " " + m.Version + " para todos los usuarios…");
                var r = ProcessRunner.PowerShell(ProvisionScript(package), 15 * 60 * 1000);
                Log.Info(c.Name + ": aprovisionamiento directo: " + r.Describe() + " — " + Fmt.Truncate((r.Out ?? "").Replace("\r", " ").Replace("\n", " | "), 500));
                return r;
            }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception ex) { why = "no se pudo descargar el paquete oficial (" + Http.Describe(ex) + ")"; return null; }
            finally { BrowserProvider.TryDeleteDir(dir); }
        }
    }
}
