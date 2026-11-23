using System;
using System.Text.RegularExpressions;

namespace Actualizador.Core
{
    /// <summary>
    /// Oculta secretos antes de que lleguen a un informe. Los informes se comparten con técnicos
    /// y clientes, y las variables de entorno, las líneas de comandos de los procesos y los
    /// argumentos de tareas suelen llevar tokens de API, contraseñas de bases de datos y claves.
    /// Criterio: ante la duda se oculta. Ocultar de más es inofensivo; mostrar de más, no.
    /// </summary>
    public static class Redactor
    {
        public const string Mask = "••••••••";

        const string Nombres = @"pass(?:word|wd|phrase)?|pwd|secret|token|api[-_]?key|access[-_]?key|private[-_]?key|" +
                               @"client[-_]?secret|credentials?|auth(?:orization)?|sas|signature|sig|session[-_]?key";

        /// <summary>¿El nombre de una variable sugiere que guarda un secreto?</summary>
        static readonly Regex SecretName = new Regex(@"(?i)(" + Nombres + @"|connection[-_]?string|conn[-_]?str)", RegexOptions.Compiled);

        /// <summary>
        /// clave=valor o clave:valor (cadenas de conexión, parámetros URL, archivos .ini, JSON «"token":"x"»,
        /// encabezados «Authorization: Bearer x»: el esquema no es el secreto, el token sí).
        /// </summary>
        static readonly Regex KeyValue = new Regex(
            @"(?i)(?<=^|[\s;&?,""'({])(?<k>(?:--?|/)?(?:" + Nombres + @"))(?<q>[""']?)(?<s>\s*[=:]\s*)" +
            @"(?<a>(?:Bearer|Basic|Digest|NTLM|Negotiate)\s+)?(?<v>""[^""]*""|'[^']*'|[^\s;&""',}]+)",
            RegexOptions.Compiled);

        /// <summary>Parámetros de línea de comandos: --password valor, /token valor, -apikey valor.</summary>
        static readonly Regex Flag = new Regex(
            @"(?i)(?<=^|\s)(?<k>(?:--?|/)(?:" + Nombres + @"))(?<s>\s+)(?<v>""[^""]*""|'[^']*'|[^\s""']+)",
            RegexOptions.Compiled);

        /// <summary>Credenciales dentro de una URL: esquema://usuario:clave@servidor.</summary>
        static readonly Regex UrlCredentials = new Regex(@"(?i)(?<p>[a-z][a-z0-9+.\-]*://)[^\s/:@]+:[^\s/@]+@", RegexOptions.Compiled);

        public static bool IsSecretName(string name)
        {
            return !string.IsNullOrEmpty(name) && SecretName.IsMatch(name);
        }

        /// <summary>Valor de una variable: se oculta entero si el nombre sugiere un secreto.</summary>
        public static string Variable(string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (IsSecretName(name)) return Mask + " (oculto por seguridad)";
            return Text(value);
        }

        /// <summary>Oculta los valores secretos dentro de un texto libre o una línea de comandos.</summary>
        public static string Text(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = UrlCredentials.Replace(s, "${p}" + Mask + "@");
            s = KeyValue.Replace(s, delegate(Match m)
            {
                string v = m.Groups["v"].Value;
                string quote = v.Length > 1 && (v[0] == '"' || v[0] == '\'') ? v.Substring(0, 1) : "";
                return m.Groups["k"].Value + m.Groups["q"].Value + m.Groups["s"].Value + m.Groups["a"].Value + quote + Mask + quote;
            });
            s = Flag.Replace(s, "${k}${s}" + Mask);
            return s;
        }
    }
}
