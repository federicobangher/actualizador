using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Actualizador.Core
{
    /// <summary>
    /// JSON propio (RFC 8259) para no depender de System.Web.Extensions ni de NuGet.
    /// Objetos -> Dictionary&lt;string,object&gt;, arreglos -> List&lt;object&gt;,
    /// números -> long o double, más string/bool/null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            var p = new Parser(text);
            p.SkipWs();
            object v = p.ReadValue(0);
            p.SkipWs();
            if (!p.End) throw new FormatException("JSON: contenido extra en la posición " + p.Pos);
            return v;
        }

        /// <summary>Navega con rutas tipo "versions[0].version" o "a.b[2]".</summary>
        public static object Path(object root, string path)
        {
            object cur = root;
            int i = 0;
            while (cur != null && i < path.Length)
            {
                if (path[i] == '.') { i++; continue; }
                if (path[i] == '[')
                {
                    int close = path.IndexOf(']', i);
                    int idx;
                    // «a[» o «a[x]»: ruta mal formada, no se encuentra nada (antes, excepción).
                    if (close < 0 || !int.TryParse(path.Substring(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out idx)) return null;
                    var list = cur as List<object>;
                    cur = (list != null && idx < list.Count) ? list[idx] : null;
                    i = close + 1;
                    continue;
                }
                int end = i;
                while (end < path.Length && path[end] != '.' && path[end] != '[') end++;
                string key = path.Substring(i, end - i);
                var dict = cur as Dictionary<string, object>;
                object next = null;
                if (dict != null && !dict.TryGetValue(key, out next))
                {
                    foreach (var kv in dict)   // tolerante a mayúsculas/minúsculas
                        if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { next = kv.Value; break; }
                }
                cur = next;
                i = end;
            }
            return cur;
        }

        public static string Str(object root, string path)
        {
            object v = Path(root, path);
            if (v == null) return null;
            if (v is string) return (string)v;
            if (v is bool) return ((bool)v) ? "true" : "false";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static List<object> List(object root, string path)
        {
            return Path(root, path) as List<object> ?? new List<object>();
        }

        // ------------------------------------------------------------------ Escritura

        public static string Serialize(object value, bool indent = true)
        {
            var sb = new StringBuilder();
            Write(sb, value, indent, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v, bool indent, int level)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is DateTime) { WriteString(sb, ((DateTime)v).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)); return; }
            if (v is Enum) { WriteString(sb, v.ToString()); return; }
            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong || v is ushort || v is sbyte)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            var dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, level + 1);
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(indent ? ": " : ":");
                    Write(sb, e.Value, indent, level + 1);
                }
                if (!first) NewLine(sb, indent, level);
                sb.Append('}');
                return;
            }
            var seq = v as IEnumerable;
            if (seq != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, level + 1);
                    Write(sb, item, indent, level + 1);
                }
                if (!first) NewLine(sb, indent, level);
                sb.Append(']');
                return;
            }
            WriteString(sb, v.ToString());
        }

        static void NewLine(StringBuilder sb, bool indent, int level)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------------ Lectura

        sealed class Parser
        {
            readonly string s;
            int i;
            public Parser(string text) { s = text; i = 0; if (s.Length > 0 && s[0] == '\uFEFF') i = 1; }
            public bool End { get { return i >= s.Length; } }
            public int Pos { get { return i; } }

            public void SkipWs()
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
            }

            Exception Err(string what) { return new FormatException("JSON: " + what + " en la posición " + i); }

            public object ReadValue(int depth)
            {
                if (depth > 256) throw Err("anidamiento excesivo");
                SkipWs();
                if (i >= s.Length) throw Err("fin inesperado");
                char c = s[i];
                if (c == '{') return ReadObject(depth);
                if (c == '[') return ReadArray(depth);
                if (c == '"') return ReadString();
                if (c == 't') { Expect("true"); return true; }
                if (c == 'f') { Expect("false"); return false; }
                if (c == 'n') { Expect("null"); return null; }
                if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                throw Err("carácter inesperado '" + c + "'");
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw Err("se esperaba " + word);
                i += word.Length;
            }

            Dictionary<string, object> ReadObject(int depth)
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                i++;
                SkipWs();
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipWs();
                    if (i >= s.Length || s[i] != '"') throw Err("se esperaba una clave");
                    string key = ReadString();
                    SkipWs();
                    if (i >= s.Length || s[i] != ':') throw Err("se esperaba ':'");
                    i++;
                    d[key] = ReadValue(depth + 1);
                    SkipWs();
                    if (i >= s.Length) throw Err("objeto sin cerrar");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw Err("se esperaba ',' o '}'");
                }
            }

            List<object> ReadArray(int depth)
            {
                var list = new List<object>();
                i++;
                SkipWs();
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ReadValue(depth + 1));
                    SkipWs();
                    if (i >= s.Length) throw Err("arreglo sin cerrar");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw Err("se esperaba ',' o ']'");
                }
            }

            string ReadString()
            {
                i++; // comilla inicial
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length) throw Err("cadena sin cerrar");
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) throw Err("escape incompleto");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            int code;
                            if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                throw Err("escape \\u inválido");
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default: throw Err("escape inválido");
                    }
                }
            }

            object ReadNumber()
            {
                int start = i;
                if (s[i] == '-') i++;
                while (i < s.Length && "0123456789.eE+-".IndexOf(s[i]) >= 0) i++;
                string num = s.Substring(start, i - start);
                long l;
                if (num.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l))
                    return l;
                double d;
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                throw Err("número inválido '" + num + "'");
            }
        }
    }
}
