// winget como servicio (cuenta SYSTEM), como lo ejecuta el agente de Vigía: con una tarea programada de una sola vez.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// 2.0.21 (captura del usuario, 21/11/2026, con la 2.0.20 en el mismo equipo): Vigía decía «3 programas con versión nueva»
    /// (Microsoft.Outlook, Microsoft.Teams y PDF24) y el Actualizador «Nada para actualizar». El agente de Vigía corre
    /// «winget upgrade» como servicio (SYSTEM): winget ve ahí otra lista que la del usuario —y otra que la de «--scope machine»—.
    /// Para ver exactamente lo mismo, Actualizador (que corre como administrador) ejecuta winget como SYSTEM con una tarea
    /// programada que se crea, se ejecuta una vez y se borra. Lo que hace la tarea está en un guion dentro de una carpeta privada
    /// (sólo Administradores y SYSTEM), y el winget.exe tiene que estar en Program Files\WindowsApps (sólo lo escribe Windows).
    /// </summary>
    public static class SystemWinget
    {
        /// <summary>Sólo para las pruebas: reemplaza la ejecución como SYSTEM (argumentos → resultado).</summary>
        internal static Func<string, ProcResult> RunForTests;

        /// <summary>Sólo para las pruebas: el winget.exe que se usaría.</summary>
        internal static string ExeForTests;

        /// <summary>
        /// El winget.exe del paquete oficial (App Installer), o null. Primero el de la app registrada del usuario (su carpeta de
        /// instalación), después el destino del alias de ejecución (si es el genuino) y por último la carpeta de WindowsApps.
        /// </summary>
        public static string FindExe(UpdateContext ctx)
        {
            if (ExeForTests != null) return ExeForTests;
            string pf = SafePaths.ProgramFiles;
            if (ctx != null)
            {
                lock (ctx.Appx)
                    foreach (var a in ctx.Appx)
                    {
                        if (a == null || !string.Equals(a.Name, "Microsoft.DesktopAppInstaller", StringComparison.OrdinalIgnoreCase)) continue;
                        string exe = Path.Combine((a.InstallLocation ?? "").Trim().TrimEnd('\\'), "winget.exe");
                        if (IsTrustedExe(exe, pf) && File.Exists(exe)) return exe;
                    }
            }
            foreach (string c in Inventory.SoftwareCollectors.WingetCandidates())
            {
                string target = c;
                try
                {
                    if (Native.ReparseTag(c) == Native.IO_REPARSE_TAG_APPEXECLINK)
                    {
                        var link = Native.AppExecLinkStrings(c);
                        if (Inventory.SoftwareCollectors.IsGenuineWingetLink(link, pf)) target = link[2].Trim(); else continue;
                    }
                }
                catch (Exception ex) { Log.Quiet("Destino del alias de winget", ex); continue; }
                if (IsTrustedExe(target, pf)) return target;
            }
            return null;
        }

        /// <summary>¿Es el winget.exe de una carpeta de App Installer en Program Files\WindowsApps? Pública para las pruebas.</summary>
        public static bool IsTrustedExe(string exe, string programFiles)
        {
            return Inventory.SoftwareCollectors.IsGenuineWingetLink(new[] { "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "", exe ?? "" }, programFiles);
        }

        /// <summary>El guion que corre como SYSTEM: winget con la salida a un archivo y, al final, su código. Público para las pruebas.</summary>
        public static string Script(string wingetExe, string args, string outFile, string exitFile)
        {
            return Script(wingetExe, args, outFile, exitFile, null);
        }

        /// <summary>
        /// 2.0.29 (grabación del usuario, 23/11/2026 12:21: Vigía listaba 2 y el Actualizador 1, y la fila decía «winget, todo el equipo»: en
        /// ese equipo winget como servicio no anduvo). winget.exe ejecutado por su ruta, fuera de su paquete (como SYSTEM), necesita las
        /// bibliotecas de Visual C++ (msvcp140.dll, vcruntime140.dll); Windows las resuelve para la app del usuario por sus dependencias,
        /// pero como servicio las busca en System32: en un equipo sin el «Visual C++ 2015-2022 Redistributable» winget no arranca
        /// (0xC0000135, falta una DLL). Ahora el guion pone primero en el PATH la carpeta del paquete de Microsoft que las trae
        /// (Microsoft.VCLibs.140.00.UWPDesktop, en Program Files\WindowsApps) y se ubica en la carpeta de winget.
        /// </summary>
        public static string Script(string wingetExe, string args, string outFile, string exitFile, string vcLibsDir)
        {
            var sb = new StringBuilder();
            sb.Append("@echo off\r\n");
            sb.Append("chcp 65001 >nul\r\n");
            if (!string.IsNullOrEmpty(vcLibsDir) && IsSafeForCmd(vcLibsDir)) sb.Append("set \"PATH=").Append(vcLibsDir).Append(";%PATH%\"\r\n");
            string exePath = wingetExe ?? "";
            int slash = exePath.LastIndexOf('\\');
            string wingetDir = slash > 0 ? exePath.Substring(0, slash) : "";
            if (!string.IsNullOrEmpty(wingetDir) && IsSafeForCmd(wingetDir)) sb.Append("cd /d \"").Append(wingetDir).Append("\"\r\n");
            sb.Append('"').Append(wingetExe).Append("\" ").Append(args).Append(" > \"").Append(outFile).Append("\" 2>&1\r\n");
            // 2.0.23: la redirección VA ADELANTE. «echo %ERRORLEVEL%> archivo» con el código 0 queda «echo 0> archivo», y cmd lee «0>»
            // como redirección de la entrada: el archivo quedaba vacío y el código se leía como -1. Con winget terminado bien (0), la
            // 2.0.21/2.0.22 lo tomaba como falla: repetía con «--scope machine», probaba el paquete directo y la fila salía en rojo.
            sb.Append(">\"").Append(exitFile).Append(".tmp\" echo %ERRORLEVEL%\r\n");
            sb.Append("move /y \"").Append(exitFile).Append(".tmp\" \"").Append(exitFile).Append("\" >nul\r\n");
            return sb.ToString();
        }

        /// <summary>Una ruta que se puede escribir tal cual en el guion (sin comillas, %, ni caracteres especiales de cmd).</summary>
        public static bool IsSafeForCmd(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            foreach (char ch in path) if (ch < 32 || "\"%&|<>^!".IndexOf(ch) >= 0) return false;
            return true;
        }

        /// <summary>
        /// 2.0.29: la carpeta del paquete Microsoft.VCLibs.140.00.UWPDesktop de esta arquitectura (la versión más nueva), sólo si está en
        /// Program Files\WindowsApps (la escribe únicamente Windows); null si no hay. Pública para las pruebas.
        /// </summary>
        public static string VcLibsDir(IList<AppxInfo> appx, string programFiles, string nativeArch)
        {
            if (appx == null || string.IsNullOrEmpty(programFiles)) return null;
            string arch = (nativeArch ?? "").Trim().ToLowerInvariant();
            if (arch.Length == 0 || arch == "amd64") arch = "x64";
            string root = programFiles.TrimEnd('\\') + @"\WindowsApps\Microsoft.VCLibs.140.00.UWPDesktop_";
            string best = null, bestVersion = null;
            foreach (var a in appx)
            {
                if (a == null || !string.Equals(a.Name, "Microsoft.VCLibs.140.00.UWPDesktop", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals((a.Architecture ?? "").Trim(), arch, StringComparison.OrdinalIgnoreCase)) continue;
                string dir = (a.InstallLocation ?? "").Trim().TrimEnd('\\');
                if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !dir.EndsWith("__8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)) continue;
                if (dir.Substring(root.Length).IndexOf('\\') >= 0 || !IsSafeForCmd(dir)) continue;
                if (best == null || VersionUtil.IsNewer(a.Version, bestVersion)) { best = dir; bestVersion = a.Version; }
            }
            return best;
        }

        /// <summary>Por qué winget como servicio no sirvió, en palabras (para la nota de la búsqueda). Pública para las pruebas.</summary>
        public static string Explain(ProcResult r)
        {
            if (r == null) return "no se ejecutó";
            if (!r.Started) return r.StartError ?? "no arrancó";
            if (r.TimedOut) return "no terminó a tiempo";
            switch (unchecked((uint)r.ExitCode))
            {
                case 0xC0000135: return "winget no arranca como servicio en este equipo: le falta una biblioteca de Visual C++ (0xC0000135)";
                case 0xC0000142: return "winget no pudo iniciarse como servicio (0xC0000142)";
                case 0x8A15000F: return "los orígenes de winget del servicio están dañados (0x8A15000F)";
            }
            return "winget respondió " + Fmt.Hex(r.ExitCode) + " sin una lista";
        }

        /// <summary>
        /// La tarea: cuenta SYSTEM, oculta, sin desencadenador (se ejecuta a pedido), también con batería, prioridad baja y una hora
        /// como máximo. Pública para las pruebas.
        /// </summary>
        public static string TaskXml(string cmdExe, string scriptPath)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                   "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                   "  <RegistrationInfo><Description>Actualizador: consulta de winget como servicio (se borra sola)</Description></RegistrationInfo>\r\n" +
                   "  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                   "  <Settings>\r\n" +
                   "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                   "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                   "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                   "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                   "    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
                   "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                   "    <Enabled>true</Enabled>\r\n" +
                   "    <Hidden>true</Hidden>\r\n" +
                   "    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>\r\n" +
                   "    <Priority>7</Priority>\r\n" +
                   "  </Settings>\r\n" +
                   "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(cmdExe) + "</Command><Arguments>" +
                   SecurityElement.Escape("/d /c \"" + scriptPath + "\"") + "</Arguments></Exec></Actions>\r\n" +
                   "</Task>\r\n";
        }

        /// <summary>
        /// Ejecuta winget como SYSTEM y espera. <see cref="ProcResult.Started"/> false (con el motivo en StartError) si no se pudo
        /// (sin administrador, sin el Programador de tareas, sin winget en WindowsApps).
        /// </summary>
        public static ProcResult Run(UpdateContext ctx, string args, int timeoutMs)
        {
            if (RunForTests != null)
            {
                Log.Info("winget (como servicio) " + args);
                return RunForTests(args) ?? new ProcResult();
            }
            var r = new ProcResult();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) { r.StartError = "sólo en Windows"; return r; }
            if (ctx == null || !ctx.IsAdmin) { r.StartError = "hace falta ejecutar Actualizador como administrador"; return r; }
            string exe = FindExe(ctx);
            if (exe == null) { r.StartError = "no se encontró el winget.exe de App Installer en Program Files\\WindowsApps"; return r; }
            string dir = null;
            string task = "Actualizador-winget-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string schtasks = Path.Combine(SafePaths.System32, "schtasks.exe");
            bool created = false;
            try
            {
                dir = SafePaths.CreatePrivateDirectory(Path.GetTempPath());
                string script = Path.Combine(dir, "winget.cmd");
                string outFile = Path.Combine(dir, "salida.txt");
                string exitFile = Path.Combine(dir, "codigo.txt");
                string xml = Path.Combine(dir, "tarea.xml");
                string vcLibs = null;
                lock (ctx.Appx) vcLibs = VcLibsDir(ctx.Appx, SafePaths.ProgramFiles, ctx.Os != null ? ctx.Os.NativeArchitecture : "");
                File.WriteAllText(script, Script(exe, args, outFile, exitFile, vcLibs), Encoding.ASCII);
                File.WriteAllText(xml, TaskXml(Path.Combine(SafePaths.System32, "cmd.exe"), script), Encoding.Unicode);
                Log.Info("winget (como servicio) " + args);
                var c = ProcessRunner.Run(schtasks, "/create /tn " + ProcessRunner.QuoteArg(task) + " /xml " + ProcessRunner.QuoteArg(xml) + " /f", 60000);
                if (!c.Ok) { r.StartError = "no se pudo crear la tarea programada (" + Fmt.Truncate((c.Out + " " + c.Err).Trim(), 200) + ")"; return r; }
                created = true;
                var run = ProcessRunner.Run(schtasks, "/run /tn " + ProcessRunner.QuoteArg(task), 60000);
                if (!run.Ok) { r.StartError = "no se pudo ejecutar la tarea programada (" + Fmt.Truncate((run.Out + " " + run.Err).Trim(), 200) + ")"; return r; }
                r.Started = true;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!File.Exists(exitFile))
                {
                    if (sw.ElapsedMilliseconds > timeoutMs) { r.TimedOut = true; break; }
                    if (ctx.CancelRequested) { r.TimedOut = true; break; }
                    System.Threading.Thread.Sleep(500);
                }
                if (r.TimedOut)
                    ProcessRunner.Run(schtasks, "/end /tn " + ProcessRunner.QuoteArg(task), 30000);
                try { r.Out = File.Exists(outFile) ? File.ReadAllText(outFile, new UTF8Encoding(false)) : ""; } catch (Exception ex) { r.Out = ""; Log.Quiet("Salida de winget como servicio", ex); }
                r.Err = "";
                if (!r.TimedOut) r.ExitCode = ParseExit(SafeRead(exitFile));
                Log.Info("winget (como servicio) terminó: " + r.Describe());
                return r;
            }
            catch (Exception ex)
            {
                if (!r.Started) r.StartError = ex.Message;
                Log.Warn("winget como servicio: " + ex.Message);
                return r;
            }
            finally
            {
                if (created) { try { ProcessRunner.Run(schtasks, "/delete /tn " + ProcessRunner.QuoteArg(task) + " /f", 30000); } catch (Exception qx) { Log.Quiet("Borrar la tarea de winget", qx); } }
                if (dir != null) BrowserProvider.TryDeleteDir(dir);
            }
        }

        static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); } catch { return ""; }
        }

        /// <summary>«-1978335212» (lo que escribe %ERRORLEVEL%) → el código; -1 si no se entiende. Pública para las pruebas.</summary>
        public static int ParseExit(string text)
        {
            int v;
            return int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
        }
    }
}
