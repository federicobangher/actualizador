using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Actualizador.Core
{
    public sealed class ProcResult
    {
        public bool Started;
        public string StartError;
        public int ExitCode = -1;
        public bool TimedOut;
        public string Out = "";
        public string Err = "";
        /// <summary>Se terminó a propósito porque su salida mostró algo inaceptable (ver <see cref="AbortReason"/>).</summary>
        public bool Aborted;
        public string AbortReason = "";

        public bool Ok { get { return Started && !TimedOut && !Aborted && ExitCode == 0; } }

        public string Describe()
        {
            if (!Started) return "No se pudo iniciar: " + StartError;
            if (Aborted) return "Detenido: " + AbortReason;
            if (TimedOut) return "Tiempo de espera agotado";
            return "Código de salida " + ExitCode + " (" + Fmt.Hex(ExitCode) + ")";
        }
    }

    public static class ProcessRunner
    {
        /// <summary>
        /// Ejecuta un proceso sin ventana. A diferencia de ReadToEnd()+WaitForExit(timeout)
        /// (que bloquea para siempre si el proceso se cuelga), acá el timeout se respeta
        /// y el árbol de procesos se termina.
        /// </summary>
        public static ProcResult Run(string file, string args, int timeoutMs, Encoding encoding = null, Action<string> onLine = null)
        {
            return Run(file, args, timeoutMs, encoding, onLine, null);
        }

        /// <summary>Igual, con variables de entorno propias para el proceso (p. ej. TEMP en una carpeta privada).</summary>
        public static ProcResult Run(string file, string args, int timeoutMs, Encoding encoding, Action<string> onLine, IDictionary<string, string> environment)
        {
            return Run(file, args, timeoutMs, encoding, onLine, environment, null);
        }

        /// <summary>
        /// Igual, y además <paramref name="abortCheck"/> revisa cada línea de la salida: si devuelve un
        /// motivo (no null), se termina el árbol de procesos en ese momento y el resultado queda
        /// <see cref="ProcResult.Aborted"/>. Sirve para frenar a winget cuando anuncia que va a instalar
        /// una versión más vieja que la ofrecida, antes de que descargue o ejecute nada.
        /// </summary>
        public static ProcResult Run(string file, string args, int timeoutMs, Encoding encoding, Action<string> onLine, IDictionary<string, string> environment,
                                     Func<string, string> abortCheck)
        {
            var r = new ProcResult();
            var psi = new ProcessStartInfo(file, args ?? "");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.CreateNoWindow = true;   // sin ventana, pero sin WindowStyle=Hidden (que las heurísticas vigilan)
            // System32, no %TEMP%: lanzar procesos desde una carpeta temporal es una heurística de malware.
            try { psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System); } catch { }
            // Sin codificación indicada, la de la consola (OEM), como en .NET Framework: en .NET 8 quedaba UTF-8 y los
            // programas de consola de Windows escriben en la página OEM (los acentos llegaban rotos).
            if (encoding == null) encoding = TextEncodings.Oem;
            psi.StandardOutputEncoding = encoding;
            psi.StandardErrorEncoding = encoding;
            SanitizeEnvironment(psi);
            if (environment != null)
                foreach (var kv in environment) psi.EnvironmentVariables[kv.Key] = kv.Value;

            var sbOut = new CappedText(MaxCapturedChars);
            var sbErr = new CappedText(MaxCapturedChars / 4);
            var outDone = new ManualResetEvent(false);
            var errDone = new ManualResetEvent(false);
            var p = new Process();
            bool release = true;
            int aborted = 0;
            string abortReason = null;
            try
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) { outDone.Set(); return; }
                    lock (sbOut) sbOut.AppendLine(e.Data);
                    if (onLine != null) { try { onLine(e.Data); } catch { } }
                    if (abortCheck != null && aborted == 0)
                    {
                        string why = null;
                        try { why = abortCheck(e.Data); } catch { }
                        if (why != null && Interlocked.Exchange(ref aborted, 1) == 0)
                        {
                            abortReason = why;
                            Log.Warn(FileLabel(file) + " detenido: " + why);
                            KillTree(p);
                        }
                    }
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) { errDone.Set(); return; }
                    lock (sbErr) sbErr.AppendLine(e.Data);
                };
                try
                {
                    if (!p.Start()) { r.StartError = "Process.Start devolvió false"; return r; }
                }
                catch (Exception ex)
                {
                    r.StartError = ex.Message;
                    return r;
                }
                r.Started = true;
                // Consultas en segundo plano (búsqueda de actualizaciones, auditoría): prioridad baja, para que el
                // equipo siga respondiendo mientras winget o PowerShell trabajan. Sus hijos la heredan.
                if (lowPriority) { try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception qx) { Log.Quiet("Prioridad baja", qx); } }
                try { p.StandardInput.Close(); } catch { }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    r.TimedOut = true;
                    KillTree(p);
                    try { p.WaitForExit(5000); } catch { }
                }
                else r.ExitCode = p.ExitCode;
                if (aborted != 0) { r.Aborted = true; r.AbortReason = abortReason ?? ""; }
                // Espera ACOTADA a que se vacíe la salida. WaitForExit() sin plazo espera el fin de las
                // tuberías, y si el proceso dejó un hijo que las heredó (un instalador que abre el programa
                // al terminar) no volvía nunca: la fila quedaba «Instalando…» para siempre.
                bool drainedOut = outDone.WaitOne(OutputGraceMs);
                bool drainedErr = errDone.WaitOne(drainedOut ? OutputGraceMs : 0);
                if (!drainedOut || !drainedErr)
                {
                    // No se cierra el Process: cerrar la tubería mientras el hijo la tiene abierta puede
                    // tirar una excepción en el hilo lector. Se libera sola cuando el hijo termina.
                    release = false;
                    Log.Warn(FileLabel(file) + " terminó, pero un proceso hijo sigue usando su salida; se continúa sin esperarlo.");
                }
            }
            finally
            {
                if (release) { p.Dispose(); outDone.Close(); errDone.Close(); }
            }
            lock (sbOut) r.Out = sbOut.ToString().TrimStart('﻿');
            lock (sbErr) r.Err = sbErr.ToString();
            return r;
        }

        /// <summary>
        /// Caracteres de salida que se guardan por proceso. winget imprime su barra de progreso como líneas
        /// sueltas: en una descarga larga eran cientos de miles que quedaban en memoria y después se copiaban y se
        /// recorrían con expresiones regulares. Pasado el tope se conserva el principio (las tablas) y las últimas
        /// líneas (el resultado y el código de salida).
        /// </summary>
        internal static int MaxCapturedChars = 4 * 1024 * 1024;

        /// <summary>Líneas finales que se conservan cuando la salida pasa del tope.</summary>
        internal const int TailLines = 400;

        [ThreadStatic] static bool lowPriority;

        /// <summary>
        /// Los procesos que se lancen DESDE ESTE HILO con <see cref="Run(string,string,int,Encoding,Action{string})"/> arrancan con
        /// prioridad baja (consultas, nunca instaladores). Lo activan los hilos de la auditoría y de la búsqueda.
        /// </summary>
        public static bool LowPriorityForThisThread
        {
            get { return lowPriority; }
            set { lowPriority = value; }
        }

        /// <summary>Texto acumulado con tope: el principio completo y, si se pasa, las últimas <see cref="TailLines"/> líneas.</summary>
        internal sealed class CappedText
        {
            readonly int max;
            readonly StringBuilder head = new StringBuilder();
            readonly Queue<string> tail = new Queue<string>();
            long dropped;

            public CappedText(int maxChars) { max = Math.Max(1024, maxChars); }

            public void AppendLine(string line)
            {
                if (head.Length + line.Length + 2 <= max && tail.Count == 0) { head.Append(line).Append(Environment.NewLine); return; }
                tail.Enqueue(line);
                if (tail.Count > TailLines) { tail.Dequeue(); dropped++; }
            }

            public override string ToString()
            {
                if (tail.Count == 0) return head.ToString();
                var sb = new StringBuilder(head.Length + 64 + tail.Count * 80);
                sb.Append(head);
                if (dropped > 0) sb.Append("[… ").Append(dropped).Append(" líneas omitidas …]").Append(Environment.NewLine);
                foreach (string l in tail) sb.Append(l).Append(Environment.NewLine);
                return sb.ToString();
            }
        }

        static string FileLabel(string file)
        {
            try { return Path.GetFileName(file); } catch { return file; }
        }

        /// <summary>Cuánto se espera, después de que el proceso terminó, a que llegue el resto de su salida.</summary>
        internal static int OutputGraceMs = 10000;

        /// <summary>
        /// Ejecuta un instalador o desinstalador SIN redirigir su entrada ni su salida: no hacen falta, y
        /// una tubería heredada por el programa que el instalador abre al terminar dejaba a Actualizador
        /// esperando. El plazo se respeta y, si se vence, se termina el árbol de procesos.
        /// </summary>
        public static ProcResult RunInstaller(string file, string args, int timeoutMs, IDictionary<string, string> environment)
        {
            return RunInstaller(file, args, timeoutMs, environment, null);
        }

        /// <summary>
        /// Igual, y cuando el instalador termina se espera también a los procesos que siguen corriendo
        /// desde <paramref name="waitFolder"/> (la carpeta privada de la descarga, donde también apunta
        /// su TEMP). Muchos instaladores se copian a TEMP, lanzan la segunda etapa y terminan enseguida:
        /// sin esta espera se comprobaba la versión antes de que la instalación terminara (AIMP) y la
        /// carpeta temporal no se podía borrar. El programa que el instalador abre al final corre
        /// desde Program Files, no desde esa carpeta, así que no se lo espera.
        /// </summary>
        public static ProcResult RunInstaller(string file, string args, int timeoutMs, IDictionary<string, string> environment, string waitFolder)
        {
            return RunInstaller(file, args, timeoutMs, environment, waitFolder, null);
        }

        /// <summary>
        /// Igual; <paramref name="releaseBeforeWait"/> (el archivo del instalador que se tenía abierto para
        /// que nadie lo cambiara entre la verificación y la ejecución) se libera apenas termina el
        /// instalador y ANTES de esperar a sus procesos hijos: un auxiliar que espera para borrar el
        /// instalador (AIMP deja uno, «delXXXX.tmp») no podría hacerlo mientras siga abierto, y la
        /// espera no terminaría nunca.
        /// </summary>
        public static ProcResult RunInstaller(string file, string args, int timeoutMs, IDictionary<string, string> environment, string waitFolder, IDisposable releaseBeforeWait)
        {
            var sw = Stopwatch.StartNew();
            var r = RunInstallerCore(file, args, timeoutMs, environment);
            if (releaseBeforeWait != null) { try { releaseBeforeWait.Dispose(); } catch { } }
            if (!r.Started || r.TimedOut || string.IsNullOrEmpty(waitFolder)) return r;
            int left = (int)Math.Max(0, timeoutMs - sw.ElapsedMilliseconds);
            if (!WaitForFolderProcesses(waitFolder, left, FileLabel(file))) r.TimedOut = true;
            return r;
        }

        /// <summary>
        /// Espera a que terminen los procesos que corren desde la carpeta, hasta <paramref name="maxMs"/>.
        /// Si se vence el plazo los termina y devuelve false.
        /// </summary>
        public static bool WaitForFolderProcesses(string folder, int maxMs, string label)
        {
            var sw = Stopwatch.StartNew();
            bool announced = false;
            while (true)
            {
                var left = ProcessesIn(folder);
                if (left.Count == 0) return true;
                if (!announced)
                {
                    announced = true;
                    var names = new List<string>();
                    foreach (var kv in left) if (!names.Contains(kv.Value)) names.Add(kv.Value);
                    Log.Info(label + " terminó, pero su instalación sigue en segundo plano (" + string.Join(", ", names.ToArray()) + "); se espera a que termine…");
                }
                if (sw.ElapsedMilliseconds > maxMs)
                {
                    foreach (var kv in left)
                    {
                        try { using (var child = Process.GetProcessById(kv.Key)) if (!child.HasExited) child.Kill(); }
                        catch (Exception qx) { Log.Quiet("Cortar un proceso hijo colgado", qx); }
                    }
                    Log.Warn(label + ": la instalación en segundo plano no terminó a tiempo; se cerró.");
                    return false;
                }
                Thread.Sleep(1500);
            }
        }

        /// <summary>Procesos (PID → nombre) cuyo ejecutable está dentro de la carpeta. Vacío si no se puede consultar.</summary>
        public static List<KeyValuePair<int, string>> ProcessesIn(string folder)
        {
            var list = new List<KeyValuePair<int, string>>();
            if (string.IsNullOrEmpty(folder)) return list;
            string f = folder.TrimEnd('\\') + "\\";
            if (f.Length < 4) return list;
            int self = -1;
            try { using (var me = Process.GetCurrentProcess()) self = me.Id; } catch { }
            // Con la API de Windows: esta consulta se repite cada 1,5 s mientras un instalador sigue en segundo
            // plano, y por WMI cada vuelta hacía trabajar a WmiPrvSE.exe; con WMI colgado, la fila no avanzaba.
            foreach (var e in ProcessList.Snapshot(true))
            {
                if (string.IsNullOrEmpty(e.Path) || !e.Path.StartsWith(f, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Pid <= 0 || e.Pid == self) continue;
                list.Add(new KeyValuePair<int, string>(e.Pid, e.Name ?? ""));
            }
            return list;
        }

        static ProcResult RunInstallerCore(string file, string args, int timeoutMs, IDictionary<string, string> environment)
        {
            var r = new ProcResult();
            var psi = new ProcessStartInfo(file, args ?? "");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;   // sólo afecta a los de consola; los instaladores con ventanas la muestran igual
            try { psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System); } catch { }
            SanitizeEnvironment(psi);
            if (environment != null)
                foreach (var kv in environment) psi.EnvironmentVariables[kv.Key] = kv.Value;
            using (var p = new Process())
            {
                p.StartInfo = psi;
                try
                {
                    if (!p.Start()) { r.StartError = "Process.Start devolvió false"; return r; }
                }
                catch (Exception ex)
                {
                    r.StartError = ex.Message;
                    return r;
                }
                r.Started = true;
                if (!p.WaitForExit(timeoutMs))
                {
                    r.TimedOut = true;
                    KillTree(p);
                    try { p.WaitForExit(5000); } catch { }
                }
                else r.ExitCode = p.ExitCode;
            }
            return r;
        }

        /// <summary>
        /// Variables que el usuario puede definir en HKCU\Environment y que un proceso elevado
        /// hereda: sirven para meter código en los procesos hijos. COR_PROFILER/COMPLUS_/DOTNET_
        /// cargan una DLL en cualquier proceso .NET (PowerShell incluido); PSModulePath hace que
        /// un PowerShell elevado cargue módulos de una carpeta del usuario; __COMPAT_LAYER cambia
        /// cómo se elevan los instaladores. Elevado, los hijos arrancan sin ellas.
        /// </summary>
        public static void SanitizeEnvironment(ProcessStartInfo psi)
        {
            if (!SafePaths.Elevated) return;
            var remove = new List<string>();
            foreach (string key in psi.EnvironmentVariables.Keys)
                if (IsHijackVariable(key)) remove.Add(key);
            foreach (string key in remove) psi.EnvironmentVariables.Remove(key);
            psi.EnvironmentVariables["PSModulePath"] = SafePsModulePath;
            // Las rutas del sistema también se pueden redefinir en HKCU\Environment: un instalador elevado
            // que llama a system() o a %ComSpec% ejecutaría el programa del usuario como administrador.
            psi.EnvironmentVariables["SystemRoot"] = SafePaths.Windows;
            psi.EnvironmentVariables["windir"] = SafePaths.Windows;
            psi.EnvironmentVariables["SystemDrive"] = SafePaths.SystemDrive;
            psi.EnvironmentVariables["ComSpec"] = Path.Combine(SafePaths.System32, "cmd.exe");
        }

        public static bool IsHijackVariable(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToUpperInvariant();
            return n.StartsWith("COR_") || n.StartsWith("COMPLUS_") || n.StartsWith("DOTNET_") || n.StartsWith("CORECLR_") ||
                   n == "__COMPAT_LAYER" || n == "PSMODULEPATH";
        }

        /// <summary>Rutas de módulos de Windows PowerShell que sólo puede escribir un administrador.</summary>
        public static string SafePsModulePath
        {
            get
            {
                return Path.Combine(SafePaths.ProgramFiles, @"WindowsPowerShell\Modules") + ";" +
                       Path.Combine(SafePaths.System32, @"WindowsPowerShell\v1.0\Modules");
            }
        }

        /// <summary>
        /// Abre un archivo, carpeta o dirección con el programa predeterminado, SIN privilegios de
        /// administrador: elevado, se le pide a explorer.exe, que lo abre en la sesión normal del
        /// usuario. Abrir un informe HTML directamente desde el proceso elevado dejaba el
        /// navegador corriendo como administrador.
        /// </summary>
        public static void OpenForUser(string target)
        {
            if (string.IsNullOrEmpty(target)) return;
            if (SafePaths.Elevated)
            {
                string explorer = Path.Combine(SafePaths.Windows, "explorer.exe");
                var psi = new ProcessStartInfo(explorer, QuoteArg(target));
                psi.UseShellExecute = false;
                SanitizeEnvironment(psi);
                using (Process.Start(psi)) { }
                return;
            }
            using (Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })) { }
        }

        /// <summary>
        /// 2.0.8: abre la carpeta del archivo con el archivo marcado, en la sesión del usuario (sin privilegios aunque Actualizador
        /// esté elevado: lo abre explorer.exe).
        /// </summary>
        public static void ShowInFolderForUser(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            string explorer = Path.Combine(SafePaths.Windows, "explorer.exe");
            var psi = new ProcessStartInfo(explorer, "/select," + QuoteArg(file));
            psi.UseShellExecute = false;
            SanitizeEnvironment(psi);
            using (Process.Start(psi)) { }
        }

        /// <summary>
        /// Termina el proceso y sus descendientes sin lanzar taskkill.exe (un ejecutable externo
        /// que hace «/F» es una señal que los antivirus vigilan). Se recorre el árbol con WMI.
        /// </summary>
        static void KillTree(Process p)
        {
            int rootPid;
            DateTime rootStart;
            try { rootPid = p.Id; rootStart = p.StartTime; } catch { return; }
            var toKill = new List<int>();
            // Con la API de Windows: KillTree es lo que hace cumplir los plazos, no puede colgarse.
            var rows = new List<ProcessRow>();
            foreach (var e in ProcessList.Snapshot(true))
            {
                var row = new ProcessRow();
                row.Pid = e.Pid;
                row.Parent = e.ParentPid;
                row.Created = e.Created;
                rows.Add(row);
            }
            foreach (int pid in Descendants(rows, rootPid, rootStart)) toKill.Add(pid);
            toKill.Add(rootPid);
            foreach (int pid in toKill)
            {
                try { using (var child = Process.GetProcessById(pid)) if (!child.HasExited) child.Kill(); }
                catch (Exception qx) { Log.Quiet("Cortar el árbol de procesos", qx); }
            }
        }

        /// <summary>Un proceso del sistema: PID, PID del padre y cuándo se creó (null si no se sabe).</summary>
        public sealed class ProcessRow
        {
            public int Pid;
            public int Parent;
            public DateTime? Created;
        }

        /// <summary>
        /// Descendientes de <paramref name="rootPid"/>, hijos antes que padres. El PID del padre que guarda
        /// Windows sobrevive al padre y los PID se reutilizan: un proceso creado ANTES que su supuesto padre
        /// no es su hijo (era de otro proceso que terminó) y no se toca.
        /// </summary>
        public static List<int> Descendants(List<ProcessRow> rows, int rootPid, DateTime? rootStart)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            seen.Add(rootPid);
            Collect(rows, rootPid, rootStart, result, seen, 0);
            return result;
        }

        static void Collect(List<ProcessRow> rows, int parentPid, DateTime? parentStart, List<int> acc, HashSet<int> seen, int depth)
        {
            if (depth > 10 || rows == null) return;
            foreach (var r in rows)
            {
                if (r.Parent != parentPid || r.Pid <= 0 || seen.Contains(r.Pid)) continue;
                // Un segundo de margen: las fechas de WMI y de Process.StartTime no se redondean igual.
                if (parentStart.HasValue && r.Created.HasValue && r.Created.Value < parentStart.Value.AddSeconds(-1)) continue;
                seen.Add(r.Pid);
                Collect(rows, r.Pid, r.Created, acc, seen, depth + 1);
                acc.Add(r.Pid);
            }
        }

        /// <summary>Ruta de un ejecutable de System32 evitando la redirección WOW64.</summary>
        public static string SystemExe(string name)
        {
            // Nunca %SystemRoot%: un proceso elevado lo hereda del usuario, que puede redefinirlo.
            string windir = SafePaths.Windows;
            if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
            {
                string sysnative = Path.Combine(windir, "Sysnative", name);
                if (File.Exists(sysnative)) return sysnative;
            }
            string sys = Path.Combine(windir, "System32", name);
            // Si no existe, NO se devuelve el nombre suelto: eso lo buscaría en el PATH del usuario.
            return sys;
        }

        public static string PowerShellExe
        {
            get
            {
                // Siempre la ruta completa: el nombre suelto se buscaría en la carpeta del programa
                // y en el PATH del usuario, y un proceso elevado ejecutaría lo que encuentre ahí.
                return SystemExe(@"WindowsPowerShell\v1.0\powershell.exe");
            }
        }

        /// <summary>
        /// Ejecuta un comando de Windows PowerShell con salida UTF-8.
        /// El texto va legible en la línea de comandos (-Command): nada de -EncodedCommand ni de
        /// -ExecutionPolicy Bypass, que los antivirus tratan, con razón, como señales de malware.
        /// La política de ejecución sólo se aplica a archivos .ps1, no a -Command.
        /// </summary>
        public static ProcResult PowerShell(string script, int timeoutMs)
        {
            string full = "$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Continue';" +
                          "try{[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding $false}catch{};" + script;
            return Run(PowerShellExe, "-NoProfile -NonInteractive -Command " + QuoteArg(full), timeoutMs, new UTF8Encoding(false));
        }

        /// <summary>
        /// Entrecomilla un argumento según las reglas de CommandLineToArgvW / CRT de Windows:
        /// las comillas internas se escapan con \ y las barras sólo se duplican cuando preceden a una comilla.
        /// </summary>
        public static string QuoteArg(string arg)
        {
            if (arg == null) arg = "";
            var sb = new StringBuilder(arg.Length + 8);
            sb.Append('"');
            for (int i = 0; i < arg.Length; i++)
            {
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
                if (i == arg.Length) { sb.Append('\\', backslashes * 2); break; }
                if (arg[i] == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(arg[i]); }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Codificación de consola OEM (para netsh, powercfg, etc.).</summary>
        public static Encoding OemEncoding
        {
            get
            {
                // En .NET 8 GetEncoding(850) fallaba (sin el proveedor de páginas de Windows) y quedaba UTF-8.
                return TextEncodings.Oem;
            }
        }
    }
}
