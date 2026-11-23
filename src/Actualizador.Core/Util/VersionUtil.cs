using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Actualizador.Core
{
    /// <summary>
    /// Comparación de versiones "del mundo real". System.Version no sirve porque
    /// los editores publican cosas como "24.09 (x64)", "v8.7.1", "131.0.6778.86",
    /// "2.47.1.windows.2" o "1.2.3-beta".
    /// </summary>
    public static class VersionUtil
    {
        static readonly Regex Numeric = new Regex(@"\d+(?:\.\d+)*", RegexOptions.Compiled);
        static readonly Regex DateLike = new Regex(@"^v?(\d{4})-(\d{1,2})-(\d{1,2})(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // 2.0.16: también con espacio o guion bajo («MSI Afterburner 4.6.7 Beta 3», «2.1_rc1»): antes «4.6.7 Beta 3» valía lo mismo que
        // la 4.6.7 final y, con la beta instalada, la final publicada se daba por «al día».
        static readonly Regex PreTag = new Regex(@"(?:[-._]|\s)(alpha|beta|preview|pre|rc|dev|nightly)(?![a-z])[ .\-_]?(\d+)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Devuelve los segmentos numéricos de la primera secuencia de versión, o null.</summary>
        public static long[] Parse(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            string v = version.Trim();
            if (v.StartsWith("<") || v.StartsWith(">")) v = v.Substring(1).Trim();
            // Versiones con forma de fecha («2024-10-01»): antes sólo contaba el año y todas las del
            // mismo año eran iguales.
            var date = DateLike.Match(v);
            if (date.Success) v = date.Groups[1].Value + "." + date.Groups[2].Value + "." + date.Groups[3].Value + v.Substring(date.Length);
            var m = Numeric.Match(v);
            if (!m.Success) return null;
            string[] parts = m.Value.Split('.');
            var result = new List<long>();
            foreach (string p in parts)
            {
                long n;
                if (!long.TryParse(p.Length > 18 ? p.Substring(0, 18) : p, NumberStyles.None, CultureInfo.InvariantCulture, out n)) break;
                result.Add(n);
            }
            return result.Count == 0 ? null : result.ToArray();
        }

        public static bool IsPreRelease(string version)
        {
            if (string.IsNullOrEmpty(version)) return false;
            return PreTag.IsMatch(version);
        }

        /// <summary>
        /// Compara a con b: negativo si a &lt; b, 0 si iguales, positivo si a &gt; b.
        /// Devuelve null si alguna no se puede interpretar.
        /// </summary>
        public static int? Compare(string a, string b)
        {
            long[] x = Parse(a), y = Parse(b);
            if (x == null || y == null) return null;
            int n = Math.Max(x.Length, y.Length);
            for (int i = 0; i < n; i++)
            {
                long xi = i < x.Length ? x[i] : 0;
                long yi = i < y.Length ? y[i] : 0;
                if (xi != yi) return xi < yi ? -1 : 1;
            }
            // Mismo número: una prerelease es menor que la final.
            bool pa = IsPreRelease(a), pb = IsPreRelease(b);
            if (pa != pb) return pa ? -1 : 1;
            // Dos prereleases del mismo número: por etapa (alpha < beta < preview < rc) y después por su
            // número («beta.10» > «beta.2»; antes daban iguales).
            if (pa && pb)
            {
                var ma = PreTag.Match(a); var mb = PreTag.Match(b);
                if (ma.Success && mb.Success)
                {
                    int ra = StageRank(ma.Groups[1].Value), rb = StageRank(mb.Groups[1].Value);
                    if (ra != rb) return ra < rb ? -1 : 1;
                    long na = 0, nb = 0;
                    if (ma.Groups[2].Success) long.TryParse(ma.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out na);
                    if (mb.Groups[2].Success) long.TryParse(mb.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out nb);
                    if (na != nb) return na < nb ? -1 : 1;
                }
            }
            return 0;
        }

        static int StageRank(string stage)
        {
            switch ((stage ?? "").ToLowerInvariant())
            {
                case "dev": case "nightly": return 0;
                case "alpha": return 1;
                case "beta": return 2;
                case "pre": case "preview": return 3;
                case "rc": return 4;
                default: return 2;
            }
        }

        public static bool IsNewer(string candidate, string installed)
        {
            int? c = Compare(candidate, installed);
            return c.HasValue && c.Value > 0;
        }

        public static string Normalize(string version)
        {
            long[] p = Parse(version);
            if (p == null) return version;
            var parts = new string[p.Length];
            for (int i = 0; i < p.Length; i++) parts[i] = p[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(".", parts);
        }

        /// <summary>Codifica una versión de 4 partes como UInt64 (formato de PlatformDependencies de la Store).</summary>
        public static ulong Encode64(long major, long minor, long build, long revision)
        {
            return ((ulong)(major & 0xFFFF) << 48) | ((ulong)(minor & 0xFFFF) << 32) | ((ulong)(build & 0xFFFF) << 16) | (ulong)(revision & 0xFFFF);
        }

        public static string Decode64(ulong v)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}", v >> 48, (v >> 32) & 0xFFFF, (v >> 16) & 0xFFFF, v & 0xFFFF);
        }
    }
}
