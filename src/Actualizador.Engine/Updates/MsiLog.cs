using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Lo que dice el registro detallado de Windows Installer sobre por qué falló una instalación.</summary>
    public sealed class MsiDiagnosis
    {
        /// <summary>Primer error de Windows Installer con número (1714, 1722…), 0 si no hay.</summary>
        public int Error;
        /// <summary>El texto que escribió Windows Installer (en el idioma de Windows), sin el número.</summary>
        public string Message = "";
        /// <summary>Primera acción que devolvió «3» (falla).</summary>
        public string FailedAction = "";
        public string CustomAction = "";
        public int CustomActionCode;
        /// <summary>Programas que, según el administrador de reinicio, tenían archivos en uso.</summary>
        public readonly List<string> FilesInUseBy = new List<string>();

        public bool IsEmpty
        {
            get { return Error == 0 && Message.Length == 0 && FailedAction.Length == 0 && CustomAction.Length == 0 && FilesInUseBy.Count == 0; }
        }
    }

    /// <summary>
    /// winget informa sólo «el instalador terminó con el código 1603» y deja el registro de Windows
    /// Installer en DiagOutputDir. Ahí está la causa real (programa abierto, versión anterior que no
    /// se puede quitar, una acción del instalador que falló). Esto la extrae y la explica. No depende
    /// del idioma: se usan los números de error y los nombres de acción, que no se traducen.
    /// </summary>
    public static class MsiLogAnalyzer
    {
        const RegexOptions RX = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        static readonly Regex LogPath = new Regex(@"(?<p>[A-Za-z]:\\[^\r\n""<>|*?]+?\.(?:log|txt))\s*$", RX);
        static readonly Regex ProductLine = new Regex(@"(?:Product|Producto|Produkt|Produit|Prodotto|Produto)\s*:\s*[^\r\n]*?\s--\s(?<msg>.+)$", RX);
        static readonly Regex ErrorCode = new Regex(@"^\s*(?:Error|Fehler|Erreur|Errore|Erro)\s+(?<code>\d{4})\.?\s*(?<rest>.*)$", RX);
        static readonly Regex PlainError = new Regex(@"^(?:MSI \([cs]\)\s*\([0-9A-F:]+\)\s*\[[^\]]+\]:\s*)?(?:Error|Fehler|Erreur|Errore|Erro)\s+(?<code>\d{4})\.\s*(?<rest>.+)$", RX);
        static readonly Regex FailedActionLine = new Regex(@"\d{1,2}:\d{2}:\d{2}:\s+(?<a>[A-Za-z_][\w.]*)\.\s+[^\d\r\n]{2,40}?\s3\.\s*$", RX);
        static readonly Regex CustomActionLine = new Regex(@"^\s*CustomAction\s+(?<ca>[\w.\-]+)\s+\D*?(?<code>-?\d{1,10})\b", RX);
        static readonly Regex FilesInUse = new Regex(@"friendly name\s+'(?<n>[^']{1,120})'.*?(?:holds?|in use|en uso)", RX);
        static readonly Regex FinalStatus = new Regex(@"(?:success or error status|estado de error o éxito|error o éxito|Erfolgs- oder Fehlerstatus|état d'erreur)\s*:?\s*\d+", RX);

        /// <summary>Errores que sólo dicen «falló» (1603, 1708): no explican nada, se busca otro.</summary>
        static bool IsGeneric(int code) { return code == 1603 || code == 1708 || code == 1709; }

        /// <summary>La ruta del registro del instalador que imprime winget («El registro del instalador está disponible en: …»).</summary>
        public static string LogPathFromWinget(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            string found = null;
            foreach (string raw in output.Replace("\r\n", "\n").Split('\n'))
            {
                string line = WingetTable.CleanLine(raw);
                var m = LogPath.Match(line);
                if (!m.Success) continue;
                string p = m.Groups["p"].Value.Trim();
                // Sólo la carpeta de diagnóstico de winget: es donde deja el registro del instalador.
                if (p.IndexOf(@"\DiagOutputDir\", StringComparison.OrdinalIgnoreCase) >= 0) found = p;
            }
            return found;
        }

        /// <summary>
        /// Lee el final del registro (los últimos <paramref name="maxBytes"/>), detectando UTF-16 o ANSI.
        /// null si no existe, es un enlace o no se puede leer.
        /// </summary>
        public static string ReadTail(string path, int maxBytes)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
                if (!SafeToRead(path)) { Log.Warn("No se lee el registro del instalador " + path + ": está fuera de %LOCALAPPDATA% o pasa por un enlace"); return null; }
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] head = new byte[2];
                    int hn = fs.Read(head, 0, 2);
                    bool bomUtf16 = hn == 2 && head[0] == 0xFF && head[1] == 0xFE;
                    long start = Math.Max(0, fs.Length - maxBytes);
                    if (start % 2 == 1) start++;
                    fs.Position = start;
                    var buf = new byte[fs.Length - start];
                    int read = 0, n;
                    while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
                    return Decode(buf, read, bomUtf16);
                }
            }
            catch (Exception ex) { Log.Warn("No se pudo leer el registro del instalador " + path + ": " + ex.Message); return null; }
        }

        /// <summary>
        /// Elevado, sólo se leen registros dentro de %LOCALAPPDATA% (donde los dejan winget y Actualizador)
        /// y sin enlaces en el camino: la ruta sale de la salida de winget y la carpeta la puede tocar el
        /// usuario, y un proceso elevado no debería leer, siguiendo un enlace, un archivo que el usuario no puede.
        /// </summary>
        static bool SafeToRead(string path)
        {
            if (Path.DirectorySeparatorChar != '\\' || !SafePaths.Elevated) return true;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) return false;
            string full;
            try { full = Path.GetFullPath(path); } catch { return false; }
            if (!full.StartsWith(local.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            return !SafePaths.HasReparsePoint(local, full);
        }

        /// <summary>UTF-16 LE si tiene BOM o la mitad de los bytes impares son cero; si no, ANSI.</summary>
        public static string Decode(byte[] buf, int count, bool bomUtf16)
        {
            if (buf == null || count <= 0) return "";
            int zeros = 0, sample = Math.Min(count, 4000);
            for (int i = 1; i < sample; i += 2) if (buf[i] == 0) zeros++;
            bool utf16 = bomUtf16 || zeros > sample / 4;
            string s = utf16 ? Encoding.Unicode.GetString(buf, 0, count - (count % 2)) : TextEncodings.Ansi.GetString(buf, 0, count);
            return s.TrimStart('﻿');
        }

        public static MsiDiagnosis Analyze(string log)
        {
            var d = new MsiDiagnosis();
            if (string.IsNullOrEmpty(log)) return d;
            string genericMessage = null;
            foreach (string raw in log.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd('\r', ' ');
                if (line.Length == 0) continue;

                var fu = FilesInUse.Match(line);
                if (fu.Success)
                {
                    string n = fu.Groups["n"].Value.Trim();
                    if (n.Length > 0 && !d.FilesInUseBy.Exists(delegate(string x) { return x.Equals(n, StringComparison.OrdinalIgnoreCase); })) d.FilesInUseBy.Add(n);
                    continue;
                }

                var pl = ProductLine.Match(line);
                if (pl.Success)
                {
                    string msg = pl.Groups["msg"].Value.Trim();
                    if (FinalStatus.IsMatch(msg)) continue;            // el resumen final («… estado: 1603»)
                    var ec = ErrorCode.Match(msg);
                    if (ec.Success)
                    {
                        int code;
                        if (!int.TryParse(ec.Groups["code"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out code)) continue;
                        string rest = ec.Groups["rest"].Value.Trim();
                        if (IsGeneric(code)) { if (genericMessage == null) genericMessage = rest; continue; }
                        if (d.Error == 0) { d.Error = code; d.Message = rest; }
                    }
                    else if (d.Error == 0 && d.Message.Length == 0 && !LooksLikeFinalSummary(msg))
                        d.Message = msg;                               // condición de inicio («Ya hay instalada una versión más nueva»)
                    continue;
                }

                if (d.Error == 0)
                {
                    var pe = PlainError.Match(line);
                    if (pe.Success)
                    {
                        int code;
                        if (int.TryParse(pe.Groups["code"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out code) && !IsGeneric(code)) { d.Error = code; d.Message = pe.Groups["rest"].Value.Trim(); continue; }
                    }
                }

                if (d.CustomAction.Length == 0)
                {
                    var ca = CustomActionLine.Match(line);
                    if (ca.Success)
                    {
                        int code;
                        if (int.TryParse(ca.Groups["code"].Value, out code) && code != 0) { d.CustomAction = ca.Groups["ca"].Value; d.CustomActionCode = code; }
                        continue;
                    }
                }

                if (d.FailedAction.Length == 0)
                {
                    var fa = FailedActionLine.Match(line);
                    if (fa.Success && !fa.Groups["a"].Value.Equals("INSTALL", StringComparison.OrdinalIgnoreCase)) d.FailedAction = fa.Groups["a"].Value;
                }
            }
            if (d.Error == 0 && d.Message.Length == 0 && genericMessage != null && d.CustomAction.Length == 0 && d.FailedAction.Length == 0 && d.FilesInUseBy.Count == 0)
                d.Message = genericMessage;
            return d;
        }

        static bool LooksLikeFinalSummary(string msg)
        {
            string m = msg.ToLowerInvariant();
            return m.Contains("installation failed") || m.Contains("installation operation failed") || m.Contains("configuration failed") ||
                   m.Contains("completed successfully") || m.Contains("error en la instalación") || m.Contains("instalación completada") ||
                   m.Contains("la instalación ha fallado") || m.Contains("operación de instalación");
        }

        /// <summary>La causa en palabras, para la columna Resultado. "" si el registro no dice nada útil.</summary>
        public static string Explain(MsiDiagnosis d)
        {
            if (d == null || d.IsEmpty) return "";
            var parts = new List<string>();
            if (d.FilesInUseBy.Count > 0)
                parts.Add("Windows Installer encontró archivos en uso por " + Fmt.JoinY(d.FilesInUseBy) + ": cerralo (también desde el área de notificación) y reintentá");
            if (d.Error != 0)
                parts.Add(ErrorHint(d.Error, d.CustomAction) + (d.Message.Length > 0 ? ": «" + Fmt.Truncate(d.Message, 180) + "»" : ""));
            else if (d.Message.Length > 0)
                parts.Add("Windows Installer dice: «" + Fmt.Truncate(d.Message, 180) + "»");
            if (d.Error == 0 && d.CustomAction.Length > 0)
                parts.Add("falló una acción propia del instalador («" + d.CustomAction + "», código " + d.CustomActionCode + "); reiniciar el equipo y reintentar suele resolverlo");
            else if (d.Error == 0 && d.FailedAction.Length > 0 && d.FilesInUseBy.Count == 0)
                parts.Add("falló el paso «" + d.FailedAction + "» de la instalación");
            return string.Join(". ", parts.ToArray());
        }

        static string ErrorHint(int code, string customAction)
        {
            switch (code)
            {
                case 1714:
                case 1612:
                case 1706:
                case 1605:
                    return "No se pudo quitar la versión anterior: falta su instalador original en la caché de Windows (error " + code + "). " +
                           "Desinstalala desde Configuración › Aplicaciones (si también falla, con el solucionador de problemas de instalación y desinstalación de Microsoft) y volvé a instalar";
                case 1720:
                case 1721:
                case 1722:
                case 1723:
                case 1001:
                    return "Falló un programa o script que ejecuta el propio instalador (error " + code + (customAction.Length > 0 ? ", acción «" + customAction + "»" : "") +
                           "); suele ser una falla del paquete o algo que bloquea esa acción. Reiniciá el equipo y reintentá";
                case 1925:
                    return "El instalador necesita permisos de administrador (error 1925)";
                case 1920:
                case 1921:
                case 1923:
                case 1939:
                    return "Un servicio del programa no se pudo iniciar, detener o registrar (error " + code + ")";
                case 1500:
                case 1618:
                    return "Hay otra instalación en curso (error " + code + "): esperá a que termine y reintentá";
                case 1638:
                    return "Windows Installer informa otra versión instalada del mismo producto (error 1638): desinstalala y volvé a instalar";
                case 1303:
                case 1304:
                case 1305:
                case 1306:
                case 1310:
                    return "No se pudo escribir o abrir un archivo del programa (error " + code + "): suele estar en uso por el programa abierto o bloqueado por el antivirus";
                case 1308:
                case 1309:
                case 1311:
                case 1316:
                case 1335:
                case 1336:
                    return "El paquete descargado está incompleto o dañado, o falta un archivo de origen (error " + code + ")";
                case 2502:
                case 2503:
                    return "Windows Installer no pudo usar la carpeta temporal (error " + code + ")";
                case 1401:
                case 1402:
                case 1406:
                    return "No se pudo escribir en el registro de Windows (error " + code + ")";
                case 1935:
                case 1904:
                    return "No se pudo registrar un componente del programa (error " + code + "); reiniciar el equipo suele resolverlo";
                default:
                    return "Windows Installer informó el error " + code;
            }
        }

        /// <summary>Lee y explica el registro que winget menciona en su salida. "" si no hay nada útil.</summary>
        public static string ExplainFromWingetOutput(string output)
        {
            string path = LogPathFromWinget(output);
            if (path == null) return "";
            return ExplainFile(path);
        }

        /// <summary>Lee y explica un registro detallado de msiexec (/l*v). "" si no hay nada útil.</summary>
        public static string ExplainFile(string path)
        {
            string text = ReadTail(path, 4 * 1024 * 1024);
            if (text == null) return "";
            var d = Analyze(text);
            string e = Explain(d);
            if (e.Length > 0) Log.Info("Registro del instalador (" + Path.GetFileName(path) + "): " + e);
            return e;
        }
    }
}
