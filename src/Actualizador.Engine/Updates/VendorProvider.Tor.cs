// 2.0.41: Tor Browser se actualiza solo: instalador oficial verificado, en silencio, conservando los datos del usuario.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Updates
{
    public sealed partial class VendorProvider
    {
        /// <summary>Sólo para las pruebas: en vez de ejecutar un guion (tipo «instalar», «datos» o «deshacer», texto) → resultado.</summary>
        internal static Func<string, string, int> TorScriptForTests;
        /// <summary>Sólo para las pruebas: el archivo de SHA-256 de The Tor Project (dirección → contenido; null: no responde).</summary>
        internal static Func<string, string> TorSumsForTests;
        /// <summary>Sólo para las pruebas: si existe una carpeta.</summary>
        internal static Func<string, bool> TorDirExistsForTests;

        const int TorInstallTimeoutMs = 20 * 60 * 1000;
        const int TorDataTimeoutMs = 20 * 60 * 1000;

        static bool TorDirExists(string path)
        {
            if (TorDirExistsForTests != null) return TorDirExistsForTests(path);
            try { return Directory.Exists(path); } catch { return false; }
        }

        /// <summary>Los procesos abiertos desde la carpeta de Tor Browser (firefox.exe, tor.exe, lyrebird.exe…), no los de otro Firefox.</summary>
        public static List<RunningProcess> TorRunning(List<RunningProcess> all, VendorPlan plan)
        {
            return plan == null || string.IsNullOrEmpty(plan.TorRoot) ? new List<RunningProcess>() : RunningPrograms.In(all, plan.TorRoot);
        }

        void InstallTor(UpdateContext ctx, UpdateCandidate c, VendorPlan plan, Action<UpdateCandidate> changed)
        {
            var check = plan.Check;
            string root = plan.TorRoot;
            bool asUser;
            string cannot = TorBrowser.CannotInstall(root, ctx.IsAdmin, out asUser);
            if (cannot != null) { InstallerSupport.Fail(c, char.ToUpperInvariant(cannot[0]) + cannot.Substring(1)); return; }
            if (!TorBrowser.IsOfficialInstaller(plan.InstallerUrl, plan.Latest, plan.Arch)) { InstallerSupport.Fail(c, "No hay un instalador oficial de Tor Browser " + plan.Latest + " para descargar"); return; }
            string backup = TorBrowser.BackupOf(root);
            if (TorDirExists(backup)) { InstallerSupport.Fail(c, char.ToUpperInvariant(TorBrowser.InstallScriptProblem(20, root)[0]) + TorBrowser.InstallScriptProblem(20, root).Substring(1)); return; }

            // Cerrado: lo que corre desde SU carpeta (no se toca otro Firefox).
            var open = RunningPrograms.Closable(TorRunning(RunningPrograms.Snapshot(), plan));
            if (open.Count > 0)
            {
                string names = RunningPrograms.Names(open);
                if (!CloseRunning) { InstallerSupport.Fail(c, "Tor Browser está abierto (" + names + "): cerralo y reintentá (o aceptá cerrarlo cuando Actualizador lo pregunte)"); return; }
                ctx.Report("Cerrando Tor Browser (" + names + ") para poder actualizarlo…");
                InstallerSupport.Progress(c, changed, "Cerrando Tor Browser (" + names + ")…");
                var still = RunningPrograms.Close(open, 15000);
                if (still.Count > 0) { InstallerSupport.Fail(c, RunningPrograms.Names(still) + " sigue abierto: cerrá Tor Browser y reintentá"); return; }
            }

            plan.Locating = true;
            plan.InstallerRan = false;
            string dir = BrowserProvider.NewDownloadDir();
            string work = null;
            try
            {
                string name = TorBrowser.InstallerName(plan.Latest, plan.Arch);
                string file = Path.Combine(dir, name);
                Download(ctx, c, changed, plan.InstallerUrl, file, TorBrowser.IsAllowedDownload);
                string how;
                using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (hold.Length < 1024 * 1024) { InstallerSupport.Fail(c, "La descarga no es el instalador de Tor Browser (" + hold.Length + " bytes): no se instala"); return; }
                    if (!IsMz(hold) || InstallerRules.Detect(hold) != InstallerKind.Nsis) { InstallerSupport.Fail(c, "La descarga no es el instalador de Tor Browser (no es un instalador NSIS): no se instala"); return; }
                    // SHA-256 que publica The Tor Project para ese archivo (el mismo que verifica su página).
                    string sums = null;
                    string sumsUrl = TorBrowser.SumsUrl(plan.InstallerUrl);
                    try
                    {
                        sums = TorSumsForTests != null ? TorSumsForTests(sumsUrl)
                             : Http.GetString(sumsUrl, 20000, TorBrowser.IsAllowedDownload, "text/plain");
                    }
                    catch (Exception ex) { Log.Warn("Tor Browser: no se pudo leer " + sumsUrl + ": " + ex.Message); }
                    string expected = TorBrowser.HashFromSums(sums, name);
                    if (expected == null)
                    {
                        InstallerSupport.Fail(c, "No se pudo leer el SHA-256 que publica The Tor Project para " + name + " (" + (sumsUrl != null ? new Uri(sumsUrl).Host : "torproject.org") +
                                                 "): no se instala sin comprobarlo. Volvé a intentar en unos minutos");
                        return;
                    }
                    string got = InstallerSupport.Sha256(hold);
                    if (!string.Equals(got, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warn("Tor Browser: SHA-256 esperado " + expected + ", obtenido " + got);
                        InstallerSupport.Fail(c, "El SHA-256 de la descarga no coincide con el que publica The Tor Project: no se instala");
                        return;
                    }
                    string bad = CheckSignature(ctx, c, plan, file, out how);
                    if (bad != null) { InstallerSupport.Fail(c, char.ToUpperInvariant(bad[0]) + bad.Substring(1)); return; }
                    how = "SHA-256 publicado por The Tor Project y " + how;
                    string fv = ExeVersionForTests != null ? ExeVersionForTests(file) : ExeVersion(file);
                    if (SiteInstallers.LooksLikeProductVersion(fv, plan.Latest, c.InstalledVersion))
                    {
                        string down = InstallerRules.Downgrade(fv, c.InstalledVersion);
                        if (down != null) { InstallerSupport.Fail(c, char.ToUpperInvariant(down[0]) + down.Substring(1)); return; }
                    }

                    // Como el usuario, el instalador y los guiones van a una carpeta temporal suya (los lee y ejecuta él); elevado, en la privada.
                    work = asUser ? Path.Combine(Path.GetTempPath(), "Actualizador-tor-" + Guid.NewGuid().ToString("N").Substring(0, 12)) : Path.Combine(dir, "guion");
                    Directory.CreateDirectory(work);
                    string runFile = Path.Combine(work, name);
                    hold.Position = 0;
                    using (var w = new FileStream(runFile, FileMode.CreateNew, FileAccess.Write, FileShare.None)) hold.CopyTo(w);
                    hold.Position = 0;
                    Log.Info("Tor Browser: instalador verificado (" + how + ")");

                    // 1. Apartar la carpeta e instalar la versión nueva en el mismo lugar.
                    ctx.Report("Instalando Tor Browser " + plan.Latest + " (instalador oficial verificado)…");
                    InstallerSupport.Progress(c, changed, "Instalando la " + plan.Latest + " (instalador oficial verificado)…");
                    int rc = RunTorScript(ctx, "instalar", work, asUser, TorInstallTimeoutMs,
                                          delegate(string res) { return TorBrowser.InstallScript(root, runFile, res); });
                    if (rc == TorScriptNotStarted)
                    {
                        InstallerSupport.Fail(c, "No se pudo ejecutar la actualización de Tor Browser" + (asUser ? " como tu usuario (hace falta la sesión de Windows abierta, con el Escritorio)" : "") +
                                                 ": no se cambió nada");
                        return;
                    }
                    string early = TorBrowser.InstallScriptProblem(rc, root);
                    if (early != null) { InstallerSupport.Fail(c, char.ToUpperInvariant(early[0]) + early.Substring(1)); return; }
                    plan.InstallerRan = true;
                    string now = TorVersionNow(check, root);
                    if (rc == TorScriptNoResult || VersionUtil.Parse(now) == null || VersionUtil.IsNewer(plan.Latest, now))
                    {
                        string undo = TorUndo(ctx, work, asUser, root);
                        InstallerSupport.Fail(c, "El instalador de Tor Browser " + (rc == TorScriptNoResult ? "no terminó" : "terminó (código " + rc + ")") +
                                                 " sin dejar instalada la " + plan.Latest + (VersionUtil.Parse(now) != null ? " (quedó la " + now + ")" : "") + ". " + undo);
                        return;
                    }

                    // 2. Los datos del usuario (perfil, puentes, estado de Tor) a la carpeta nueva; después se borra la anterior.
                    ctx.Report("Tor Browser " + plan.Latest + ": copiando los marcadores y la configuración…");
                    InstallerSupport.Progress(c, changed, "Copiando los marcadores y la configuración…");
                    string robocopy = Path.Combine(SafePaths.System32, "robocopy.exe");
                    string dataLog = Path.Combine(work, "datos.log");
                    int dc = RunTorScript(ctx, "datos", work, asUser, TorDataTimeoutMs,
                                          delegate(string res) { return TorBrowser.DataScript(root, robocopy, dataLog, res); });
                    if (dc != 0)
                    {
                        // Sin ejecutarse (-2), la carpeta anterior sigue apartada con los datos: igual se vuelve atrás.
                        string undo = TorUndo(ctx, work, asUser, root);
                        InstallerSupport.Fail(c, "Se instaló la " + plan.Latest + ", pero no se pudieron copiar tus marcadores y tu configuración (" +
                                                 (dc == TorScriptNoResult ? "la copia no terminó" : dc == TorScriptNotStarted ? "no se pudo ejecutar la copia" : "código " + dc) + "). " + undo);
                        return;
                    }
                    c.Status = UpdateStatus.Instalada;
                    c.InstalledVersion = now;
                    c.Result = "Actualizada correctamente (se conservaron los marcadores, la configuración y los puentes) — " + how;
                    if (TorDirExists(backup))
                        c.Result += ". Quedó la carpeta «" + Path.GetFileName(backup) + "» (algún archivo estaba en uso): ya no hace falta, la podés borrar";
                    Log.Info("Tor Browser: " + c.Result);
                }
            }
            finally
            {
                if (work != null && asUser) { try { Directory.Delete(work, true); } catch (Exception qx) { Log.Quiet("Borrar la carpeta temporal de Tor Browser", qx); } }
                BrowserProvider.TryDeleteDir(dir);
            }
        }

        /// <summary>La versión de Tor Browser en <paramref name="root"/> según tbb_version.json (o su ChangeLog); null si no se puede leer.</summary>
        static string TorVersionNow(VendorCheck check, string root)
        {
            string source;
            return ProductVersionFromFiles(check, new List<string> { root, root + @"\Browser" }, ReadSmallFile, out source);
        }

        /// <summary>Vuelve atrás (la carpeta anterior a su lugar) y dice cómo quedó, para el final del mensaje de la fila.</summary>
        string TorUndo(UpdateContext ctx, string work, bool asUser, string root)
        {
            int u = RunTorScript(ctx, "deshacer", work, asUser, 5 * 60 * 1000, delegate(string res) { return TorBrowser.UndoScript(root, res); });
            if (u == 0) return "Se volvió a dejar la versión anterior, con tus marcadores y tu configuración";
            string backup = TorBrowser.BackupOf(root);
            // 30: no estaba la carpeta apartada (el guion de instalar no llegó a moverla): si Tor Browser sigue en su lugar, no cambió nada.
            if (u == 30 && !TorDirExists(backup) && TorFileExists(root + @"\Browser\firefox.exe")) return "No se cambió nada: sigue la versión anterior";
            Log.Warn("Tor Browser: no se pudo volver atrás (código " + u + ")");
            return "No se pudo volver a dejar la versión anterior en su lugar: está completa en «" + backup + "». Cerrá lo que esté abierto ahí, borrá «" + root +
                   "» y cambiale el nombre a «" + Path.GetFileName(root) + "»";
        }

        const int TorScriptNoResult = -1;
        /// <summary>El guion no se pudo ejecutar (sin el Explorador para abrirlo como el usuario, por ejemplo): no cambió nada.</summary>
        const int TorScriptNotStarted = -2;

        /// <summary>
        /// Escribe el guion en <paramref name="work"/> (UTF-8) y lo ejecuta oculto, como el usuario si <paramref name="asUser"/>. Devuelve lo
        /// que anotó en su archivo de resultado, o <see cref="TorScriptNoResult"/> si no anotó nada (no arrancó o no terminó).
        /// </summary>
        int RunTorScript(UpdateContext ctx, string kind, string work, bool asUser, int timeoutMs, Func<string, string> build)
        {
            string result = Path.Combine(work, kind + "-resultado.txt");
            string text = build(result);
            if (TorScriptForTests != null) return TorScriptForTests(kind, text);
            string script = Path.Combine(work, kind + ".cmd");
            try { File.Delete(result); } catch (Exception qx) { Log.Quiet("Resultado anterior del guion", qx); }
            File.WriteAllText(script, text, new UTF8Encoding(false));
            string cmd = Path.Combine(SafePaths.System32, "cmd.exe");
            string args = "/d /c \"" + script + "\"";
            Log.Info("Tor Browser: guion «" + kind + "»" + (asUser ? " como el usuario" : ""));
            ProcResult r = asUser ? Unelevated.Run(cmd, args, timeoutMs, false) : ProcessRunner.Run(cmd, args, timeoutMs);
            if (!r.Started) { Log.Warn("Tor Browser: no se pudo ejecutar el guion «" + kind + "»: " + r.Describe()); return TorScriptNotStarted; }
            // Sin elevar, si no se encontró el proceso para esperarlo, se espera su archivo de resultado. «Cancelar» no corta esta espera: con
            // la carpeta ya apartada, el guion tiene que terminar (y, si hace falta, volver atrás) antes de seguir.
            var sw = Stopwatch.StartNew();
            while (!File.Exists(result) && sw.ElapsedMilliseconds < timeoutMs) System.Threading.Thread.Sleep(500);
            System.Threading.Thread.Sleep(200);
            int code;
            string got = null;
            try { got = File.Exists(result) ? File.ReadAllText(result).Trim() : null; } catch (Exception qx) { Log.Quiet("Resultado del guion de Tor Browser", qx); }
            if (got == null || !int.TryParse(got, out code)) { Log.Warn("Tor Browser: el guion «" + kind + "» no anotó su resultado"); return TorScriptNoResult; }
            Log.Info("Tor Browser: guion «" + kind + "» → " + code);
            return code;
        }
    }
}
