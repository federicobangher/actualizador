// Lector mínimo de YAML para los manifiestos de winget-pkgs (sin dependencias externas).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Lector del subconjunto de YAML que usan los manifiestos de winget: mapas y listas por
    /// sangría, listas de mapas («- Clave: valor»), escalares con o sin comillas, listas en línea
    /// («[a, b]») y bloques «|» / «&gt;». No evalúa etiquetas, anclas ni referencias: todo valor es
    /// texto, un mapa (Dictionary) o una lista (List).
    /// </summary>
    public static class MiniYaml
    {
        sealed class Line
        {
            public int Indent;
            public string Text;
        }

        /// <summary>Tamaño máximo de un manifiesto (los de winget-pkgs tienen unos pocos KB) y anidamiento máximo (2.0.18, auditoría).</summary>
        public const int MaxChars = 2 * 1024 * 1024, MaxDepth = 64;

        [ThreadStatic] static int depth;

        public static object Parse(string text)
        {
            if (text != null && text.Length > MaxChars) throw new FormatException("el manifiesto es demasiado grande");
            depth = 0;
            var lines = new List<Line>();
            string t = (text ?? "").TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (string raw in t.Split('\n'))
            {
                string r = raw.TrimEnd();
                string trimmed = r.TrimStart(' ');
                if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed == "---" || trimmed == "...") continue;
                var l = new Line();
                l.Indent = r.Length - trimmed.Length;
                l.Text = trimmed;
                lines.Add(l);
            }
            if (lines.Count == 0) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            int pos = 0;
            return ParseBlock(lines, ref pos, lines[0].Indent);
        }

        static bool IsSeqItem(string text) { return text == "-" || text.StartsWith("- "); }

        static object ParseBlock(List<Line> lines, ref int pos, int indent)
        {
            // Sin tope, un archivo con miles de niveles agotaba la pila (StackOverflowException cierra el programa y no se puede atrapar).
            if (++depth > MaxDepth) throw new FormatException("el manifiesto tiene demasiados niveles");
            try { return IsSeqItem(lines[pos].Text) ? (object)ParseSeq(lines, ref pos, indent) : ParseMap(lines, ref pos, indent); }
            finally { depth--; }
        }

        static Dictionary<string, object> ParseMap(List<Line> lines, ref int pos, int indent)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (pos < lines.Count)
            {
                var l = lines[pos];
                if (l.Indent < indent) break;
                if (l.Indent > indent) { pos++; continue; }       // línea suelta más adentro: se ignora
                if (IsSeqItem(l.Text)) break;
                string key, rest;
                if (!SplitKey(l.Text, out key, out rest)) { pos++; continue; }
                pos++;
                map[key] = ValueAfterKey(lines, ref pos, indent, rest);
            }
            return map;
        }

        static object ValueAfterKey(List<Line> lines, ref int pos, int indent, string rest)
        {
            string v = StripComment(rest).Trim();
            if (v.StartsWith("|") || v.StartsWith(">"))
            {
                var sb = new StringBuilder();
                while (pos < lines.Count && lines[pos].Indent > indent)
                {
                    if (sb.Length > 0) sb.Append(v.StartsWith(">") ? ' ' : '\n');
                    sb.Append(lines[pos].Text);
                    pos++;
                }
                return sb.ToString();
            }
            if (v.Length > 0) return Scalar(v);
            if (pos < lines.Count)
            {
                var n = lines[pos];
                if (n.Indent > indent) return ParseBlock(lines, ref pos, n.Indent);
                if (n.Indent == indent && IsSeqItem(n.Text)) return ParseSeq(lines, ref pos, indent);   // «Clave:» y la lista a la misma altura
            }
            return "";
        }

        static List<object> ParseSeq(List<Line> lines, ref int pos, int indent)
        {
            var list = new List<object>();
            while (pos < lines.Count)
            {
                var l = lines[pos];
                if (l.Indent != indent || !IsSeqItem(l.Text)) break;
                string content = l.Text.Length > 1 ? l.Text.Substring(2).TrimStart(' ') : "";
                int contentIndent = indent + (l.Text.Length - content.Length);
                if (content.Length == 0)
                {
                    pos++;
                    if (pos < lines.Count && lines[pos].Indent > indent) list.Add(ParseBlock(lines, ref pos, lines[pos].Indent));
                    else list.Add("");
                    continue;
                }
                string key, rest;
                if (IsSeqItem(content) || (!StartsQuoted(content) && SplitKey(content, out key, out rest)))
                {
                    // «- Clave: valor»: el resto del mapa sigue alineado con «Clave».
                    var inner = new Line();
                    inner.Indent = contentIndent;
                    inner.Text = content;
                    lines[pos] = inner;
                    list.Add(ParseBlock(lines, ref pos, contentIndent));
                    continue;
                }
                list.Add(Scalar(StripComment(content).Trim()));
                pos++;
            }
            return list;
        }

        static bool StartsQuoted(string s) { return s.Length > 0 && (s[0] == '\'' || s[0] == '"'); }

        /// <summary>«Clave: valor» o «Clave:». Los «:» de una URL (sin espacio después) no cuentan.</summary>
        static bool SplitKey(string text, out string key, out string rest)
        {
            key = null; rest = null;
            int i = 0;
            if (StartsQuoted(text))
            {
                char q = text[0];
                int end = text.IndexOf(q, 1);
                if (end < 0) return false;
                i = end + 1;
            }
            for (; i < text.Length; i++)
            {
                if (text[i] != ':') continue;
                if (i + 1 == text.Length || text[i + 1] == ' ')
                {
                    key = Unquote(text.Substring(0, i).Trim());
                    rest = i + 1 < text.Length ? text.Substring(i + 1) : "";
                    return key.Length > 0;
                }
            }
            return false;
        }

        static string StripComment(string v)
        {
            string s = v.TrimStart();
            if (StartsQuoted(s))
            {
                char q = s[0];
                for (int i = 1; i < s.Length; i++)
                {
                    if (s[i] == '\\' && q == '"') { i++; continue; }
                    if (s[i] != q) continue;
                    if (q == '\'' && i + 1 < s.Length && s[i + 1] == '\'') { i++; continue; }
                    return s.Substring(0, i + 1);
                }
                return s;
            }
            int c = s.IndexOf(" #", StringComparison.Ordinal);
            return c >= 0 ? s.Substring(0, c) : s;
        }

        static object Scalar(string v)
        {
            if (v.StartsWith("[") && v.EndsWith("]"))
            {
                var list = new List<object>();
                foreach (string part in v.Substring(1, v.Length - 2).Split(','))
                {
                    string p = part.Trim();
                    if (p.Length > 0) list.Add(Unquote(p));
                }
                return list;
            }
            if (v == "{}") return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (v == "~" || v == "null") return "";
            return Unquote(v);
        }

        static string Unquote(string v)
        {
            if (v.Length >= 2 && v[0] == '\'' && v[v.Length - 1] == '\'')
                return v.Substring(1, v.Length - 2).Replace("''", "'");
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
            {
                string s = v.Substring(1, v.Length - 2);
                var sb = new StringBuilder(s.Length);
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] != '\\' || i + 1 == s.Length) { sb.Append(s[i]); continue; }
                    char e = s[++i];
                    sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e);
                }
                return sb.ToString();
            }
            return v;
        }

        // ---- Acceso tipado ----

        public static string Str(object map, string key)
        {
            var d = map as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return "";
            return v as string ?? "";
        }

        public static Dictionary<string, object> Map(object map, string key)
        {
            var d = map as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v as Dictionary<string, object>;
        }

        public static List<object> List(object map, string key)
        {
            var d = map as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v as List<object>;
        }
    }
}
