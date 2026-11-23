using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Actualizador.Core
{
    public static class Fmt
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Cell(object value)
        {
            if (value == null) return "";
            if (value is string) return ((string)value).Trim();
            if (value is bool) return (bool)value ? "Sí" : "No";
            if (value is DateTime) return Date((DateTime)value);
            if (value is TimeSpan) return Duration((TimeSpan)value);
            if (value is double || value is float || value is decimal)
                return Convert.ToDouble(value, Inv).ToString("0.##", CultureInfo.CurrentCulture);
            if (value is string[]) return string.Join(", ", (string[])value);
            return Convert.ToString(value, Inv) ?? "";
        }

        public static string Date(DateTime d)
        {
            if (d == DateTime.MinValue) return "";
            return d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", Inv) : d.ToString("yyyy-MM-dd HH:mm", Inv);
        }

        public static string Date(DateTime? d) { return d.HasValue ? Date(d.Value) : ""; }

        public static string Bytes(double bytes)
        {
            if (bytes <= 0) return "";
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            int u = 0;
            while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
            return bytes.ToString(u == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[u];
        }

        /// <summary>
        /// Sobrecarga exacta para long. Sin ella, Bytes(unLong) es ambigua entre Bytes(double)
        /// y Bytes(long?): las dos conversiones son implícitas y ninguna es mejor que la otra.
        /// </summary>
        public static string Bytes(long bytes) { return Bytes((double)bytes); }

        public static string Bytes(long? bytes) { return bytes.HasValue ? Bytes((double)bytes.Value) : ""; }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return ((int)t.TotalSeconds) + " s";
            var sb = new StringBuilder();
            if (t.Days > 0) sb.Append(t.Days).Append(t.Days == 1 ? " día " : " días ");
            if (t.Hours > 0) sb.Append(t.Hours).Append(" h ");
            sb.Append(t.Minutes).Append(" min");
            return sb.ToString();
        }

        /// <summary>Tiempo transcurrido corto: «45 s», «2 min 10 s», «1 h 5 min».</summary>
        public static string Elapsed(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalSeconds < 60) return Math.Max(0, (int)Math.Round(t.TotalSeconds)) + " s";
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + " min" + (t.Seconds > 0 ? " " + t.Seconds + " s" : "");
            return (int)t.TotalHours + " h " + t.Minutes + " min";
        }

        public static string Percent(double part, double total)
        {
            if (total <= 0) return "";
            return (part * 100.0 / total).ToString("0.#", CultureInfo.CurrentCulture) + " %";
        }

        /// <summary>Convierte fechas "yyyyMMdd" (InstallDate del registro) o formatos comunes.</summary>
        public static DateTime? ParseLooseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            DateTime d;
            string[] formats = { "yyyyMMdd", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "d/M/yyyy", "dd/MM/yyyy", "yyyy/MM/dd", "M/d/yyyy h:mm:ss tt", "yyyyMMddHHmmss" };
            if (DateTime.TryParseExact(s, formats, Inv, DateTimeStyles.AllowWhiteSpaces, out d)) return d;
            // CIM datetime: 20230115000000.000000+000
            if (s.Length >= 14 && char.IsDigit(s[0]) && s.IndexOf('.') == 14)
            {
                if (DateTime.TryParseExact(s.Substring(0, 14), "yyyyMMddHHmmss", Inv, DateTimeStyles.None, out d)) return d;
            }
            if (DateTime.TryParse(s, Inv, DateTimeStyles.AllowWhiteSpaces, out d)) return d;
            return null;
        }

        public static DateTime? FromUnixSeconds(long seconds)
        {
            if (seconds <= 0) return null;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
        }

        /// <summary>Hexadecimal de 32 bits: un código negativo como -1978335226 sale 0x8A150006, no 0xFFFFFFFF8A150006.</summary>
        public static string Hex(int value) { return "0x" + ((uint)value).ToString("X8", Inv); }

        public static string Hex(long value)
        {
            if (value >= int.MinValue && value <= uint.MaxValue) return Hex(unchecked((int)value));
            return "0x" + value.ToString("X16", Inv);
        }

        /// <summary>«A», «A y B», «A, B y C».</summary>
        public static string JoinY(IEnumerable<string> items)
        {
            var list = new List<string>();
            if (items != null) foreach (string i in items) if (!string.IsNullOrEmpty(i)) list.Add(i);
            if (list.Count == 0) return "";
            if (list.Count == 1) return list[0];
            return string.Join(", ", list.GetRange(0, list.Count - 1).ToArray()) + " y " + list[list.Count - 1];
        }

        /// <summary>Los valores sin repetir, unidos con «; » (varios motivos de falla en una sola nota).</summary>
        public static string JoinDistinct(IEnumerable<string> items)
        {
            var list = new List<string>();
            if (items != null) foreach (string i in items) if (!string.IsNullOrEmpty(i) && !list.Contains(i)) list.Add(i);
            return string.Join("; ", list.ToArray());
        }

        /// <summary>«1 actualización», «2 actualizaciones».</summary>
        public static string Plural(int n, string singular, string plural) { return n + " " + (n == 1 ? singular : plural); }

        public static string Truncate(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length <= max) return s;
            if (max <= 1) return max == 1 ? "…" : "";      // antes: Substring(0, -1) y excepción
            return s.Substring(0, max - 1) + "…";
        }

        /// <summary>
        /// Dirección web segura para abrir o enlazar: absoluta, sólo http o https, con servidor, sin
        /// usuario ni contraseña, sin espacios ni caracteres de control y de hasta 500 caracteres.
        /// Devuelve la dirección normalizada o null. Las direcciones salen del registro (que un
        /// programa cualquiera escribe) o de internet: «javascript:», «file:» o una ruta local
        /// nunca se abren ni se enlazan.
        /// </summary>
        public static string WebUrl(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string t = s.Trim();
            if (t.Length == 0 || t.Length > 500) return null;
            foreach (char c in t) if (c <= ' ' || c == '"' || c == '<' || c == '>' || c == '\\' || c == '^' || c == '`' || char.IsControl(c)) return null;
            // «www.ejemplo.com» sin esquema: muchos instaladores lo registran así.
            if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) t = "https://" + t;
            Uri u;
            if (!Uri.TryCreate(t, UriKind.Absolute, out u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            if (string.IsNullOrEmpty(u.Host) || u.Host.IndexOf('.') < 0 || u.UserInfo.Length > 0) return null;
            return u.AbsoluteUri;
        }

        public static string Html(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '&': sb.Append("&amp;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static string Csv(string s, string separator)
        {
            if (s == null) return "";
            bool quote = s.Contains(separator) || s.Contains("\"") || s.Contains("\n") || s.Contains("\r");
            // Evita inyección de fórmulas al abrir el CSV en Excel (lista de OWASP: = + - @ tab CR).
            if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0 && !IsNumberLike(s)) { s = "'" + s; }
            return quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        static bool IsNumberLike(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Any, Inv, out d);
        }

        public static string SafeFileName(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
                sb.Append(Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        public static string YesNo(bool? b) { return b.HasValue ? (b.Value ? "Sí" : "No") : ""; }
    }
}
