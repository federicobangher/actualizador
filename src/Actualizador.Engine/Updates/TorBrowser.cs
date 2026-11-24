// 2.0.41: Tor Browser se actualiza solo (antes «Actualizar» abría www.torproject.org para bajarlo a mano).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>La última versión de Tor Browser para Windows según The Tor Project, con su instalador oficial.</summary>
    public sealed class TorRelease
    {
        public string Version = "";
        /// <summary>Instalador oficial (dist.torproject.org) para la arquitectura instalada; null si no se encontró o no es válido.</summary>
        public string Binary;
    }

    /// <summary>
    /// 2.0.41 (captura del usuario, 24/11/2026: «ese navegador no actualiza, me abre la página para descargar, no es la idea»).
    ///
    /// Tor Browser para Windows es portable: se instala en una carpeta (el Escritorio, por lo general), sin entrada en «Aplicaciones»
    /// ni desinstalador. The Tor Project indica, para actualizarlo a mano, cerrarlo, borrar esa carpeta e instalar la versión nueva
    /// (y así se pierden los marcadores y la configuración, que están adentro). Actualizador hace lo mismo sin perder nada:
    ///  1. la versión y el instalador salen del mismo archivo que consulta la página de descargas de The Tor Project
    ///     (aus1.torproject.org/…/download-windows-x86_64.json, o el de 32 bits);
    ///  2. el instalador se baja sólo de *.torproject.org por HTTPS, su nombre tiene que ser el oficial de ESA versión y arquitectura,
    ///     su SHA-256 tiene que coincidir con el que publica The Tor Project al lado (sha256sums-signed-build.txt) y su firma digital
    ///     tiene que ser válida, no revocada y de The Tor Project;
    ///  3. con Tor Browser cerrado, su carpeta se aparta («Tor Browser (anterior)»), se instala la versión nueva en silencio en la misma
    ///     carpeta (/S /D=…) y se comprueba que quedó esa versión;
    ///  4. se copian los datos del usuario (Browser\TorBrowser\Data: perfil con marcadores y configuración, puentes y estado de Tor),
    ///     menos los archivos de fábrica que trae la versión nueva (torrc-defaults, geoip), y recién entonces se borra la carpeta anterior.
    ///  Si la versión nueva no quedó o no se pudieron copiar los datos, se vuelve todo atrás: la carpeta anterior vuelve a su lugar.
    ///
    /// Los pasos 3 y 4 los hace un guion de cmd que corre COMO EL USUARIO (sin elevar) si la carpeta es del usuario: Actualizador elevado
    /// no mueve, copia ni borra en carpetas que un programa del usuario puede cambiar (un enlace o una unión plantados ahí podrían
    /// redirigir lo que escribe un administrador a otro lugar del equipo).
    /// </summary>
    public static class TorBrowser
    {
        public const string FeedBase = "https://aus1.torproject.org/torbrowser/update_3/release/";
        /// <summary>El archivo anterior (todas las plataformas). The Tor Project lo marca como obsoleto: sólo si el de la plataforma no responde.</summary>
        public const string LegacyFeed = FeedBase + "downloads.json";
        public static readonly string[] Signers = { "The Tor Project, Inc.", "The Tor Project" };
        /// <summary>Nombre de la carpeta anterior mientras se instala la nueva (al lado, en la misma carpeta).</summary>
        public const string BackupSuffix = " (anterior)";
        /// <summary>Archivos de fábrica de Tor dentro de «Data»: quedan los de la versión nueva.</summary>
        public static readonly string[] NewDefaults = { "torrc-defaults", "geoip", "geoip6", "parent.lock", "lock" };
        public const string DataRelative = @"Browser\TorBrowser\Data";

        /// <summary>«windows-x86_64» o «windows-i686» (Tor Browser no publica una versión para ARM64 en Windows: usa la de 64 bits).</summary>
        public static string Platform(string arch)
        {
            return string.Equals(arch, "x86", StringComparison.OrdinalIgnoreCase) ? "windows-i686" : "windows-x86_64";
        }

        public static string FeedUrl(string arch) { return FeedBase + "download-" + Platform(arch) + ".json"; }

        /// <summary>
        /// La versión y el instalador del archivo de la plataforma ({"version","binary","sig"}) o del anterior
        /// ({"version","downloads":{"win64":{"ALL":{"binary"}}}}). null si no dice una versión válida. El instalador queda en null
        /// si no es el oficial de esa versión y arquitectura (<see cref="IsOfficialInstaller"/>).
        /// </summary>
        public static TorRelease ParseFeed(string json, string arch)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 256 * 1024) return null;
            object root;
            try { root = Json.Parse(json); } catch { return null; }
            string v = (Json.Str(root, "version") ?? "").Trim();
            if (!Regex.IsMatch(v, @"^\d{1,3}(?:\.\d{1,4}){1,3}$") || VersionUtil.Parse(v) == null) return null;
            string bin = Json.Str(root, "binary");
            if (string.IsNullOrEmpty(bin))
                bin = Json.Str(root, "downloads." + (Platform(arch) == "windows-i686" ? "win32" : "win64") + ".ALL.binary");
            var r = new TorRelease { Version = v };
            if (!string.IsNullOrEmpty(bin) && IsOfficialInstaller(bin.Trim(), v, arch)) r.Binary = new Uri(bin.Trim()).AbsoluteUri;
            return r;
        }

        /// <summary>¿Se puede bajar de esta dirección? Sólo HTTPS, puerto normal y torproject.org (dist., archive., www.).</summary>
        public static bool IsAllowedDownload(Uri u)
        {
            if (u == null || !u.IsAbsoluteUri || u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo.Length > 0) return false;
            string h = u.Host.ToLowerInvariant();
            return h == "torproject.org" || h.EndsWith(".torproject.org", StringComparison.Ordinal);
        }

        /// <summary>
        /// ¿Es el instalador oficial de ESA versión y arquitectura? «https://dist.torproject.org/torbrowser/16.0.3/
        /// tor-browser-windows-x86_64-portable-16.0.3.exe»: por HTTPS desde torproject.org, en la carpeta de esa versión y con ese nombre.
        /// </summary>
        public static bool IsOfficialInstaller(string url, string version, string arch)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(version) || !Uri.TryCreate(url, UriKind.Absolute, out u) || !IsAllowedDownload(u)) return false;
            if (u.Query.Length > 0 || u.Fragment.Length > 0) return false;
            string expected = InstallerName(version, arch);
            string path = u.AbsolutePath;
            return path.EndsWith("/" + version + "/" + expected, StringComparison.Ordinal);
        }

        /// <summary>«tor-browser-windows-x86_64-portable-16.0.3.exe».</summary>
        public static string InstallerName(string version, string arch)
        {
            return "tor-browser-" + Platform(arch) + "-portable-" + version + ".exe";
        }

        /// <summary>El archivo con los SHA-256 que publica The Tor Project al lado del instalador.</summary>
        public static string SumsUrl(string installerUrl)
        {
            int slash = (installerUrl ?? "").LastIndexOf('/');
            return slash > 0 ? installerUrl.Substring(0, slash + 1) + "sha256sums-signed-build.txt" : null;
        }

        /// <summary>El SHA-256 (en mayúsculas) de <paramref name="fileName"/> en sha256sums-signed-build.txt, o null si no figura.</summary>
        public static string HashFromSums(string sums, string fileName)
        {
            if (string.IsNullOrEmpty(sums) || string.IsNullOrEmpty(fileName)) return null;
            foreach (string raw in sums.Split('\n'))
            {
                var m = Regex.Match(raw.Trim(), @"^(?<h>[0-9A-Fa-f]{64})\s+\*?(?<f>\S+)$");
                if (m.Success && string.Equals(m.Groups["f"].Value, fileName, StringComparison.Ordinal)) return m.Groups["h"].Value.ToUpperInvariant();
            }
            return null;
        }

        /// <summary>
        /// La carpeta de Tor Browser (la que tiene «Browser\firefox.exe»), a partir de la carpeta registrada o la del acceso directo
        /// («…\Tor Browser\Browser»). null si ninguna es una instalación de Tor Browser.
        /// </summary>
        public static string RootOf(IEnumerable<string> folders, Func<string, bool> fileExists)
        {
            if (folders == null || fileExists == null) return null;
            foreach (string f in folders)
            {
                string d = (f ?? "").Trim().Trim('"').TrimEnd('\\', '/');
                if (d.Length < 4) continue;
                if (d.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) d = DirName(d);
                var tries = new List<string> { d };
                string name = LastSegment(d);
                if (name.Equals("Browser", StringComparison.OrdinalIgnoreCase)) tries.Insert(0, DirName(d));
                foreach (string root in tries)
                {
                    if (string.IsNullOrEmpty(root) || root.Length < 4) continue;
                    if (fileExists(root + @"\Browser\firefox.exe") && (fileExists(root + @"\Browser\tbb_version.json") || fileExists(root + @"\Browser\TorBrowser\Docs\ChangeLog.txt")))
                        return root;
                }
            }
            return null;
        }

        static string DirName(string p) { int i = p.LastIndexOf('\\'); return i > 2 ? p.Substring(0, i) : ""; }
        static string LastSegment(string p) { int i = p.LastIndexOf('\\'); return i >= 0 ? p.Substring(i + 1) : p; }

        /// <summary>Arquitectura de lo instalado según tbb_version.json («"architecture": "windows-i686"»); "" si no lo dice.</summary>
        public static string ArchFromVersionFile(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 4096) return "";
            var m = Regex.Match(json, @"""architecture""\s*:\s*""(?<a>[^""]{1,40})""");
            if (!m.Success) return "";
            string a = m.Groups["a"].Value.ToLowerInvariant();
            // «WINNT_x86_64-gcc3», «windows-x86_64» o «WINNT_x86-gcc3», «windows-i686».
            if (a.Contains("x86_64") || a.Contains("amd64") || a.Contains("win64") || a.Contains("aarch64")) return "x64";
            if (a.Contains("i686") || a.Contains("x86") || a.Contains("win32")) return "x86";
            return "";
        }

        public static string BackupOf(string root) { return (root ?? "").TrimEnd('\\') + BackupSuffix; }

        /// <summary>
        /// null si Actualizador puede actualizar Tor Browser en <paramref name="root"/>; si no, por qué (texto para la fila).
        /// <paramref name="asUser"/>: los pasos se ejecutan como el usuario, sin elevar (la carpeta la puede cambiar un usuario común).
        /// </summary>
        public static string CannotInstall(string root, bool admin, out bool asUser)
        {
            asUser = false;
            if (string.IsNullOrEmpty(root)) return "no se encontró la carpeta de Tor Browser (la que tiene «Browser\\firefox.exe»)";
            if (!RunningPrograms.IsSpecificFolder(root)) return "Tor Browser está en una carpeta general (" + root + "), no en una propia: no se mueve";
            if (!SystemWinget.IsSafeForCmd(root) || !SystemWinget.IsSafeForCmd(BackupOf(root)))
                return "la carpeta de Tor Browser tiene caracteres que no se pueden usar en el guion de actualización (" + root + ")";
            bool writable = SafePaths.IsUserWritableLocation(root);
            if (!writable && !admin) return "Tor Browser está en " + root + ": para actualizarlo hace falta ejecutar Actualizador como administrador";
            asUser = admin && writable;
            return null;
        }

        // ---------------------------------------------------------------- Guiones (cmd)

        static string Head()
        {
            // Sin bloques entre paréntesis. UTF-8: carpetas con acentos. System32 y chcp.com por ruta completa (no %SystemRoot%: en la
            // carpeta de Program Files el guion corre elevado).
            return "@echo off\r\n" + SelfInstall.Utf8Console + SelfInstall.LeaveStaging;
        }

        static void Safe(params string[] paths)
        {
            foreach (string p in paths)
                if (!SystemWinget.IsSafeForCmd(p)) throw new ArgumentException("ruta no válida para el guion de Tor Browser: " + p);
        }

        /// <summary>
        /// Aparta la carpeta y ejecuta el instalador en silencio en la misma carpeta. Deja en <paramref name="result"/>: 20 si ya había
        /// una carpeta «(anterior)», 21 si no se pudo apartar (algo abierto adentro), o el código de salida del instalador.
        /// </summary>
        public static string InstallScript(string root, string installer, string result)
        {
            string r = (root ?? "").TrimEnd('\\'), b = BackupOf(r);
            Safe(r, b, installer, result);
            var sb = new StringBuilder(Head());
            sb.Append("if exist \"").Append(b).Append("\" goto respaldo\r\n");
            sb.Append("move \"").Append(r).Append("\" \"").Append(b).Append("\" >nul 2>&1\r\n");
            sb.Append("if not exist \"").Append(b).Append("\" goto nomovio\r\n");
            sb.Append("if exist \"").Append(r).Append("\" goto nomovio\r\n");
            // NSIS: /D= va último y sin comillas (toma el resto de la línea, con espacios).
            sb.Append("\"").Append(installer).Append("\" /S /D=").Append(r).Append("\r\n");
            sb.Append(">\"").Append(result).Append("\" echo %ERRORLEVEL%\r\n");
            sb.Append("exit /b 0\r\n");
            sb.Append(":respaldo\r\n>\"").Append(result).Append("\" echo 20\r\nexit /b 20\r\n");
            sb.Append(":nomovio\r\n>\"").Append(result).Append("\" echo 21\r\nexit /b 21\r\n");
            return sb.ToString();
        }

        /// <summary>
        /// Copia los datos del usuario de la carpeta anterior a la nueva (sin seguir uniones, sin los archivos de fábrica) y borra la
        /// anterior. Deja 0 en <paramref name="result"/> si se copiaron, 40 si robocopy no pudo copiar algo (la anterior no se borra).
        /// </summary>
        public static string DataScript(string root, string robocopy, string log, string result)
        {
            string r = (root ?? "").TrimEnd('\\'), b = BackupOf(r);
            string from = b + "\\" + DataRelative, to = r + "\\" + DataRelative;
            Safe(r, b, robocopy, log, result);
            var sb = new StringBuilder(Head());
            sb.Append("if not exist \"").Append(from).Append("\\\" goto borrar\r\n");
            // /E sin /PURGE: lo nuevo que no estaba queda; /XJ: no sigue uniones ni enlaces; /XF: quedan los archivos de fábrica nuevos.
            sb.Append("\"").Append(robocopy).Append("\" \"").Append(from).Append("\" \"").Append(to).Append("\" /E /XJ /R:2 /W:1 /NP /XF");
            foreach (string f in NewDefaults) sb.Append(' ').Append(f);
            sb.Append(" >\"").Append(log).Append("\" 2>&1\r\n");
            // robocopy: de 0 a 7 salió bien; 8 o más, algo no se pudo copiar.
            sb.Append("if errorlevel 8 goto fallo\r\n");
            sb.Append(":borrar\r\n");
            sb.Append("rd /s /q \"").Append(b).Append("\" >nul 2>&1\r\n");
            sb.Append(">\"").Append(result).Append("\" echo 0\r\nexit /b 0\r\n");
            sb.Append(":fallo\r\n>\"").Append(result).Append("\" echo 40\r\nexit /b 40\r\n");
            return sb.ToString();
        }

        /// <summary>
        /// Vuelve atrás: borra lo que dejó el instalador y devuelve la carpeta anterior a su lugar. Nunca borra nada si no está la
        /// anterior (30). 31 si no se pudo devolver (la anterior queda con su nombre «(anterior)»), 0 si quedó como estaba.
        /// </summary>
        public static string UndoScript(string root, string result)
        {
            string r = (root ?? "").TrimEnd('\\'), b = BackupOf(r);
            Safe(r, b, result);
            var sb = new StringBuilder(Head());
            sb.Append("if not exist \"").Append(b).Append("\\Browser\\firefox.exe\" goto sinrespaldo\r\n");
            sb.Append("if exist \"").Append(r).Append("\" rd /s /q \"").Append(r).Append("\"\r\n");
            sb.Append("if exist \"").Append(r).Append("\" goto ocupado\r\n");
            sb.Append("move \"").Append(b).Append("\" \"").Append(r).Append("\" >nul 2>&1\r\n");
            sb.Append("if not exist \"").Append(r).Append("\" goto ocupado\r\n");
            sb.Append(">\"").Append(result).Append("\" echo 0\r\nexit /b 0\r\n");
            sb.Append(":sinrespaldo\r\n>\"").Append(result).Append("\" echo 30\r\nexit /b 30\r\n");
            sb.Append(":ocupado\r\n>\"").Append(result).Append("\" echo 31\r\nexit /b 31\r\n");
            return sb.ToString();
        }

        /// <summary>Qué significa el resultado del guion que aparta la carpeta e instala, si no llegó a instalar.</summary>
        public static string InstallScriptProblem(int code, string root)
        {
            switch (code)
            {
                case 20: return "quedó la carpeta «" + LastSegment(BackupOf(root)) + "» de un intento anterior al lado de Tor Browser: revisá que no tenga nada tuyo, borrala y volvé a actualizar";
                case 21: return "no se pudo apartar la carpeta de Tor Browser (" + root + "): suele ser porque hay algo abierto adentro (Tor Browser, una ventana del Explorador o el antivirus revisándola). Cerralo y volvé a actualizar; no se cambió nada";
                default: return null;
            }
        }
    }
}
