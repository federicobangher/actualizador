// 2.0.32: Actualizador se actualiza solo desde sus versiones publicadas en GitHub.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Pedido del usuario (23/11/2026): «¿podemos hacer que tenga actualización algo así como hicimos con Virtual Pet? es cansador estar
    /// borrando e instalando cada vez que hay un error». Al abrir, si GitHub tiene una versión más nueva (ver <see cref="SelfUpdate"/>):
    ///  1. baja la entrega «Actualizador-x.y.z.zip» (sólo de github.com / *.githubusercontent.com, por HTTPS);
    ///  2. la acepta sólo si su SHA-256 es el publicado, y la descomprime en una carpeta privada (sólo Administradores y SYSTEM), sin
    ///     rutas que se salgan de ella;
    ///  3. comprueba que adentro esté la versión anunciada (Actualizador.exe) y el instalador de siempre;
    ///  4. deja corriendo un guion que espera a que Actualizador se cierre, ejecuta Install-Actualizador.ps1 -Silencioso (el mismo de
    ///     Instalar.cmd: verifica SHA256SUMS.txt, quita la versión anterior, comprueba que la nueva abra y, si no, vuelve atrás) y
    ///     vuelve a abrir Actualizador.
    /// 2.0.37: la portable también se actualiza sola: baja «Actualizador-x.y.z-portable.zip» y el guion copia sus archivos encima de los de
    /// su carpeta (la carpeta «Datos», con las opciones y el historial, no se toca) y la vuelve a abrir. Hace falta administrador
    /// (Actualizador siempre lo pide) y, en la portable, poder escribir en su carpeta (no en un pendrive protegido, por ejemplo).
    /// </summary>
    public static class SelfInstall
    {
        /// <summary>Sólo para las pruebas: reemplaza la descarga (dirección, archivo de destino).</summary>
        internal static Action<string, string> DownloadForTests;
        /// <summary>Sólo para las pruebas: la versión del Actualizador.exe descomprimido.</summary>
        internal static Func<string, string> ExeVersionForTests;
        /// <summary>Sólo para las pruebas: la carpeta donde se descomprime (si no, una privada).</summary>
        internal static string StagingForTests;

        /// <summary>Sólo para las pruebas: si se puede escribir en la carpeta de la portable.</summary>
        internal static Func<string, bool> WritableForTests;

        /// <summary>¿Esta copia se puede actualizar sola? (como administrador; la portable, si se puede escribir en su carpeta)</summary>
        public static string CannotInstall(bool portable, bool admin)
        {
            if (!admin) return "hace falta ejecutar Actualizador como administrador";
            if (portable && !CanWrite(AppInfo.ProgramDirectory)) return "no se puede escribir en la carpeta de la portable (" + AppInfo.ProgramDirectory + "): se reemplaza a mano";
            return null;
        }

        static bool CanWrite(string dir)
        {
            if (WritableForTests != null) return WritableForTests(dir);
            try
            {
                string probe = Path.Combine(dir, ".prueba-escritura-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Baja y prepara la entrega. Devuelve la carpeta lista (con Install-Actualizador.ps1 y Actualizador\), o null y por qué.
        /// </summary>
        public static string Stage(SelfUpdate.Result r, Action<string> report, out string error)
        {
            return Stage(r, AppInfo.IsPortable, report, out error);
        }

        /// <summary>
        /// Como <see cref="Stage(SelfUpdate.Result, Action{string}, out string)"/>. La portable: devuelve la carpeta con Actualizador.exe y
        /// Actualizador.portable (los archivos que se copian encima de los de su carpeta).
        /// </summary>
        public static string Stage(SelfUpdate.Result r, bool portable, Action<string> report, out string error)
        {
            error = null;
            if (r == null || !r.CanInstall) { error = "no hay una entrega para instalar sola"; return null; }
            Uri u;
            if (!Uri.TryCreate(r.AssetUrl, UriKind.Absolute, out u) || !GitHubRules.IsDownload(u)) { error = "la entrega no está en GitHub (" + r.AssetUrl + ")"; return null; }
            string dir = null;
            bool keep = false;
            try
            {
                dir = StagingForTests ?? SafePaths.CreatePrivateDirectory(Path.GetTempPath());
                string zip = Path.Combine(dir, "entrega.zip");      // el guion lo borra al terminar («entrega» + «.zip»)
                if (report != null) report("Bajando Actualizador " + r.Latest + "…");
                if (DownloadForTests != null) DownloadForTests(r.AssetUrl, zip);
                else Http.Download(r.AssetUrl, zip, 15 * 60 * 1000, null, GitHubRules.IsDownload);
                string got;
                using (var f = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read)) got = InstallerSupport.Sha256(f);
                if (!got.Equals(r.AssetSha256, StringComparison.OrdinalIgnoreCase))
                { error = "el SHA-256 de lo bajado no coincide con el publicado: no se instala"; return null; }
                string target = Path.Combine(dir, "entrega");
                Directory.CreateDirectory(target);
                if (!Extract(zip, target, out error)) return null;
                string exe;
                if (portable)
                {
                    exe = Path.Combine(target, "Actualizador.exe");
                    if (!File.Exists(exe) || !File.Exists(Path.Combine(target, "Actualizador.portable")) || Directory.Exists(Path.Combine(target, PortableDataFolder)))
                    { error = "la entrega no es la de la portable (Actualizador.exe y Actualizador.portable, sin «Datos»)"; return null; }
                }
                else
                {
                    string installer = Path.Combine(target, "Install-Actualizador.ps1");
                    exe = Path.Combine(Path.Combine(target, "Actualizador"), "Actualizador.exe");
                    if (!File.Exists(installer) || !File.Exists(exe)) { error = "la entrega no trae Install-Actualizador.ps1 y Actualizador\\Actualizador.exe"; return null; }
                }
                string v = ExeVersionForTests != null ? ExeVersionForTests(exe) : FileVersionInfo.GetVersionInfo(exe).ProductVersion;
                if (!SameVersion(v, r.Latest)) { error = "la entrega dice ser la " + r.Latest + " pero trae la " + (v ?? "?") + ": no se instala"; return null; }
                keep = true;
                return target;
            }
            catch (Exception ex) { error = Http.Describe(ex); return null; }
            finally { if (!keep && dir != null && StagingForTests == null) BrowserProvider.TryDeleteDir(dir); }
        }

        /// <summary>«2.0.32+abc123» (la versión del producto puede traer el commit) es la 2.0.32. Pública para las pruebas.</summary>
        public static bool SameVersion(string productVersion, string published)
        {
            string v = (productVersion ?? "").Trim();
            int plus = v.IndexOf('+');
            if (plus >= 0) v = v.Substring(0, plus);
            return VersionUtil.Parse(v) != null && VersionUtil.Parse(published) != null && VersionUtil.Compare(v, published) == 0;
        }

        /// <summary>Descomprime sin dejar que una entrada escriba fuera de <paramref name="target"/> (rutas «..\», absolutas).</summary>
        public static bool Extract(string zip, string target, out string error)
        {
            error = null;
            string root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var z = ZipFile.OpenRead(zip))
            {
                if (z.Entries.Count == 0 || z.Entries.Count > 2000) { error = "la entrega no tiene el contenido esperado"; return false; }
                foreach (var e in z.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    string full = Path.GetFullPath(Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { error = "la entrega trae una ruta que sale de su carpeta («" + e.FullName + "»): no se instala"; return false; }
                    if (name.EndsWith("/")) { Directory.CreateDirectory(full); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    e.ExtractToFile(full, true);
                }
            }
            return true;
        }

        /// <summary>
        /// El guion que instala: espera a que termine el proceso <paramref name="pid"/> (Actualizador), instala en silencio y lo vuelve a
        /// abrir. Público para las pruebas.
        /// </summary>
        public static string Script(string stagedDir, int pid, string powershell, string installedExe, string logFile)
        {
            if (!SystemWinget.IsSafeForCmd(stagedDir) || !SystemWinget.IsSafeForCmd(powershell) || !SystemWinget.IsSafeForCmd(installedExe) || !SystemWinget.IsSafeForCmd(logFile))
                throw new ArgumentException("ruta no válida para el guion de instalación");
            var sb = new StringBuilder();
            sb.Append("@echo off\r\n");
            sb.Append("chcp 65001 >nul\r\n");
            sb.Append("cd /d \"").Append(stagedDir).Append("\"\r\n");
            // Hasta 2 minutos esperando que Actualizador termine de cerrarse.
            sb.Append("\"").Append(powershell).Append("\" -NoProfile -Command \"Wait-Process -Id ").Append(pid).Append(" -Timeout 120 -ErrorAction SilentlyContinue\"\r\n");
            sb.Append("\"").Append(powershell).Append("\" -NoProfile -ExecutionPolicy Bypass -File \"").Append(stagedDir).Append("\\Install-Actualizador.ps1\" -Silencioso -Origen \"")
              .Append(stagedDir).Append("\\Actualizador\" > \"").Append(logFile).Append("\" 2>&1\r\n");
            // 2.0.35: el resultado, para que la próxima vez se sepa si salió bien (la redirección adelante: «echo 0> archivo» no anda).
            sb.Append(">\"").Append(ResultFile(logFile)).Append("\" echo %ERRORLEVEL%\r\n");
            sb.Append(LeaveStaging);
            sb.Append("start \"\" \"").Append(installedExe).Append("\"\r\n");
            // Lo bajado ya no hace falta (queda el registro de la instalación y este guion, que pesan poco).
            sb.Append("rd /s /q \"").Append(stagedDir).Append("\" >nul 2>&1\r\n");
            sb.Append("del /q \"").Append(stagedDir).Append(".zip\" >nul 2>&1\r\n");
            return sb.ToString();
        }

        /// <summary>
        /// 2.0.38 (relevamiento): antes de abrir Actualizador y de borrar lo bajado, el guion sale de esa carpeta. Si no, Actualizador arrancaba
        /// con su carpeta de trabajo ahí adentro y «rd /s /q» no podía borrarla (cmd tampoco: estaba parado en ella): quedaba en C:\Windows\Temp.
        /// </summary>
        const string LeaveStaging = "cd /d \"%SystemRoot%\\System32\"\r\n";

        /// <summary>La carpeta de la portable con las opciones, el historial y los registros: nunca se toca.</summary>
        public const string PortableDataFolder = "Datos";

        /// <summary>
        /// 2.0.37: el guion que actualiza la portable: espera a que se cierre, copia los archivos nuevos encima de los de su carpeta (con
        /// robocopy, reintentando si alguno sigue en uso; «Datos» no se toca), anota el resultado y la vuelve a abrir. Público para las pruebas.
        /// </summary>
        public static string PortableScript(string stagedDir, int pid, string powershell, string robocopy, string portableDir, string logFile)
        {
            foreach (string p in new[] { stagedDir, powershell, robocopy, portableDir, logFile })
                if (!SystemWinget.IsSafeForCmd(p)) throw new ArgumentException("ruta no válida para el guion de actualización");
            string dir = portableDir.TrimEnd('\\');
            var sb = new StringBuilder();
            sb.Append("@echo off\r\n");
            sb.Append("chcp 65001 >nul\r\n");
            sb.Append("cd /d \"").Append(stagedDir).Append("\"\r\n");
            sb.Append("\"").Append(powershell).Append("\" -NoProfile -Command \"Wait-Process -Id ").Append(pid).Append(" -Timeout 120 -ErrorAction SilentlyContinue\"\r\n");
            // Sin /PURGE ni /MIR: no se borra nada de la carpeta. /XD: «Datos» no se copia ni se toca. Hasta 10 reintentos de 3 segundos.
            sb.Append("\"").Append(robocopy).Append("\" \"").Append(stagedDir).Append("\" \"").Append(dir).Append("\" /E /R:10 /W:3 /NP /XD ").Append(PortableDataFolder)
              .Append(" > \"").Append(logFile).Append("\" 2>&1\r\n");
            // robocopy: de 0 a 7 salió bien (1 = copió archivos); 8 o más, algo no se pudo copiar.
            sb.Append("set RC=%ERRORLEVEL%\r\n");
            sb.Append("if %RC% LSS 8 set RC=0\r\n");
            sb.Append(">\"").Append(ResultFile(logFile)).Append("\" echo %RC%\r\n");
            sb.Append(LeaveStaging);
            sb.Append("start \"\" \"").Append(dir).Append("\\Actualizador.exe\"\r\n");
            sb.Append("rd /s /q \"").Append(stagedDir).Append("\" >nul 2>&1\r\n");
            sb.Append("del /q \"").Append(stagedDir).Append(".zip\" >nul 2>&1\r\n");
            return sb.ToString();
        }

        /// <summary>Dónde deja el guion el código de salida del instalador (junto a su registro).</summary>
        public static string ResultFile(string logFile)
        {
            int slash = (logFile ?? "").LastIndexOf('\\');
            return (slash > 0 ? logFile.Substring(0, slash + 1) : "") + "resultado.txt";
        }

        static string Key(string version) { return "autoinstalacion|" + (version ?? "").Trim(); }

        /// <summary>Sólo para las pruebas: el texto de un archivo (null si no existe).</summary>
        internal static Func<string, string> ReadForTests;

        static string ReadText(string path)
        {
            if (ReadForTests != null) return ReadForTests(path);
            try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
        }

        /// <summary>
        /// 2.0.35 (relevamiento): ¿ya se intentó instalar sola esa versión y no quedó? (sigue abierta la versión anterior). Sin esto, si el
        /// instalador no podía (Smart App Control, un archivo en uso), se volvía a bajar e intentar cada vez que se abría Actualizador. Devuelve
        /// el motivo (con dónde está el registro) o null si no se intentó, o si se intentó y quedó.
        /// </summary>
        public static string PreviousFailure(MachineMemory.Snapshot memory, string version, string current)
        {
            string date, logFile;
            if (!MachineMemory.TryGet(memory, Key(version), out date, out logFile) || string.IsNullOrEmpty(logFile)) return null;
            if (!VersionUtil.IsNewer(version, current)) return null;       // quedó: se está usando esa (o una más nueva)
            string code = (ReadText(ResultFile(logFile)) ?? "").Trim();
            string log = ReadText(logFile) ?? "";
            string last = "";
            foreach (string l in log.Split('\n')) { string t = l.Trim(); if (t.Length > 0 && !t.StartsWith("===", StringComparison.Ordinal)) last = t; }
            string why = code.Length == 0 ? "el instalador no terminó (el equipo se apagó o se cerró la sesión)" : "el instalador terminó con el código " + code;
            return "El " + MachineMemory.ShowDate(date) + " se intentó instalar sola la " + version + " y no quedó: " + why +
                   (last.Length > 0 ? " («" + Fmt.Truncate(last, 160) + "»)" : "") + ". Registro: " + logFile;
        }

        /// <summary>
        /// Deja corriendo el guion (oculto) y vuelve: Actualizador tiene que cerrarse enseguida para que se pueda reemplazar.
        /// null si arrancó; si no, por qué.
        /// </summary>
        public static string Launch(string stagedDir)
        {
            return Launch(stagedDir, null);
        }

        /// <summary>Como <see cref="Launch(string)"/>, y anota el intento de <paramref name="version"/> (ver <see cref="PreviousFailure"/>).</summary>
        public static string Launch(string stagedDir, string version)
        {
            try
            {
                string ps = ProcessRunner.PowerShellExe;
                string log = Path.Combine(Path.GetDirectoryName(stagedDir) ?? stagedDir, "instalacion-automatica.log");
                string script = Path.Combine(Path.GetDirectoryName(stagedDir) ?? stagedDir, "actualizar.cmd");
                int pid = Process.GetCurrentProcess().Id;
                string text;
                if (AppInfo.IsPortable)
                    text = PortableScript(stagedDir, pid, ps, Path.Combine(SafePaths.System32, "robocopy.exe"), AppInfo.ProgramDirectory, log);
                else
                    text = Script(stagedDir, pid, ps, Path.Combine(Path.Combine(SafePaths.ProgramFiles, AppInfo.Name), AppInfo.Name + ".exe"), log);
                // 2.0.37: en UTF-8 (chcp 65001): la carpeta de la portable puede tener acentos («D:\\Programas\\Técnico»).
                File.WriteAllText(script, text, new UTF8Encoding(false));
                var psi = new ProcessStartInfo(Path.Combine(SafePaths.System32, "cmd.exe"), "/d /c \"" + script + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = SafePaths.System32;
                using (Process.Start(psi)) { }
                if (!string.IsNullOrEmpty(version)) MachineMemory.Put(Key(version), log);
                Log.Info("Actualización automática: se instala la entrega de " + stagedDir + " (registro: " + log + ")");
                return null;
            }
            catch (Exception ex) { Log.Warn("Actualización automática: " + ex.Message); return ex.Message; }
        }
    }
}
