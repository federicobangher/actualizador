// 2.0.46: firma propia de las versiones publicadas (pedido del usuario, 26/11/2026: «eso quiero hacer»). El SHA-256 de cada entrega se
// publica en el mismo GitHub que la entrega: protege de descargas dañadas, no de alguien que entre a la cuenta de GitHub y publique una
// versión falsa. La firma la hace Fede en su PC (Firmar.cmd), con una clave que nunca sube a GitHub; cada equipo la verifica con la clave
// pública que trae compilada adentro antes de instalarse solo.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Actualizador.Core.Security
{
    /// <summary>
    /// Formato de «Actualizador-x.y.z.zip.firma» (texto UTF-8, una clave por línea):
    /// <code>
    /// Actualizador firma de versión 1
    /// archivo: Actualizador-2.0.46.zip
    /// version: 2.0.46
    /// sha256: &lt;64 hexadecimales&gt;
    /// clave: &lt;identificador de la clave, 16 hexadecimales&gt;
    /// firma: &lt;base64&gt;
    /// </code>
    /// Se firma (RSA, PKCS#1 v1.5, SHA-256) el texto «Actualizador|1|archivo|version|sha256»: la firma ata el archivo a su nombre y a su
    /// versión, así que no sirve para hacer pasar una versión vieja por nueva ni un archivo por otro.
    /// Las claves de confianza están en res/claves-de-firma.txt, compilado DENTRO del programa (no es un archivo al lado del ejecutable que
    /// alguien pueda cambiar): «identificador módulo-base64 exponente-base64 [comentario]».
    /// </summary>
    public static class VersionSignature
    {
        public const string Header = "Actualizador firma de versión 1";
        public const string Extension = ".firma";
        const string ResourceName = "claves-de-firma.txt";

        public sealed class TrustedKey
        {
            public string Id = "";
            public byte[] Modulus, Exponent;
            public string Comment = "";
        }

        /// <summary>Sólo para las pruebas: reemplaza las claves compiladas.</summary>
        internal static List<TrustedKey> KeysForTests;

        static List<TrustedKey> embedded;
        static readonly object Gate = new object();

        /// <summary>Las claves de confianza (las compiladas en el programa).</summary>
        public static List<TrustedKey> TrustedKeys
        {
            get
            {
                if (KeysForTests != null) return KeysForTests;
                lock (Gate)
                {
                    if (embedded == null)
                    {
                        string text = "";
                        try
                        {
                            using (var s = typeof(VersionSignature).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName))
                                if (s != null) using (var r = new StreamReader(s, Encoding.UTF8)) text = r.ReadToEnd();
                        }
                        catch (Exception ex) { Log.Warn("Claves de firma de versiones: " + ex.Message); }
                        embedded = ParseKeys(text);
                    }
                    return embedded;
                }
            }
        }

        /// <summary>¿Las versiones nuevas tienen que venir firmadas? (sí, en cuanto el programa trae al menos una clave)</summary>
        public static bool Required { get { return TrustedKeys.Count > 0; } }

        static readonly Regex KeyLine = new Regex(@"^(?<id>[0-9a-f]{16})\s+(?<n>[A-Za-z0-9+/=]{300,1100})\s+(?<e>[A-Za-z0-9+/=]{2,12})(?:\s+(?<c>.*))?$", RegexOptions.CultureInvariant);

        /// <summary>Lee el archivo de claves. Una línea mal formada, de menos de 3072 bits o cuyo identificador no coincide, no cuenta.</summary>
        public static List<TrustedKey> ParseKeys(string text)
        {
            var list = new List<TrustedKey>();
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var m = KeyLine.Match(line);
                if (!m.Success) { Log.Warn("Claves de firma: línea no válida, no se usa"); continue; }
                try
                {
                    var k = new TrustedKey { Modulus = Convert.FromBase64String(m.Groups["n"].Value), Exponent = Convert.FromBase64String(m.Groups["e"].Value),
                                             Comment = m.Groups["c"].Success ? m.Groups["c"].Value.Trim() : "" };
                    k.Id = KeyId(k.Modulus);
                    if (k.Modulus.Length < 384) { Log.Warn("Claves de firma: clave de menos de 3072 bits, no se usa"); continue; }
                    if (k.Id != m.Groups["id"].Value) { Log.Warn("Claves de firma: el identificador no corresponde a la clave, no se usa"); continue; }
                    if (!list.Exists(delegate(TrustedKey x) { return x.Id == k.Id; })) list.Add(k);
                }
                catch (FormatException) { Log.Warn("Claves de firma: base64 no válido, no se usa"); }
            }
            return list;
        }

        /// <summary>Identificador de una clave: los primeros 16 hexadecimales del SHA-256 de su módulo.</summary>
        public static string KeyId(byte[] modulus)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(modulus ?? new byte[0])).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        /// <summary>El texto que se firma. Público para las pruebas y para Firmar.ps1 (que arma el mismo).</summary>
        public static string Message(string asset, string version, string sha256)
        {
            return "Actualizador|1|" + (asset ?? "").Trim() + "|" + (version ?? "").Trim() + "|" + (sha256 ?? "").Trim().ToLowerInvariant();
        }

        static readonly Regex Hex64 = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

        /// <summary>
        /// ¿<paramref name="firmaText"/> es una firma válida, de una clave de confianza, de ESE archivo, ESA versión y ESE SHA-256?
        /// false con el motivo (para la fila y el registro).
        /// </summary>
        public static bool Verify(string firmaText, string asset, string version, string sha256, out string why)
        {
            return Verify(firmaText, asset, version, sha256, TrustedKeys, out why);
        }

        public static bool Verify(string firmaText, string asset, string version, string sha256, List<TrustedKey> keys, out string why)
        {
            why = null;
            if (keys == null || keys.Count == 0) { why = "el programa no trae claves de firma"; return false; }
            if (string.IsNullOrEmpty(firmaText) || firmaText.Length > 8192) { why = "la firma está vacía o no tiene el formato"; return false; }
            var f = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] lines = firmaText.TrimStart('﻿').Replace("\r", "").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != Header) { why = "la firma no tiene el formato de Actualizador"; return false; }
            for (int i = 1; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0) continue;
                int c = l.IndexOf(':');
                if (c <= 0) { why = "la firma no tiene el formato de Actualizador"; return false; }
                string k = l.Substring(0, c).Trim(), v = l.Substring(c + 1).Trim();
                if (f.ContainsKey(k)) { why = "la firma repite «" + k + "»"; return false; }
                f[k] = v;
            }
            string fa, fv, fs, fk, fsig;
            if (!f.TryGetValue("archivo", out fa) || !f.TryGetValue("version", out fv) || !f.TryGetValue("sha256", out fs) ||
                !f.TryGetValue("clave", out fk) || !f.TryGetValue("firma", out fsig))
            { why = "a la firma le faltan datos"; return false; }
            string want = (sha256 ?? "").Trim().ToLowerInvariant();
            if (!Hex64.IsMatch(want) || !string.Equals(fs.ToLowerInvariant(), want, StringComparison.Ordinal)) { why = "la firma es de otro archivo (otro SHA-256)"; return false; }
            if (!string.Equals(fa, (asset ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) { why = "la firma es de otro archivo («" + fa + "»)"; return false; }
            if (!string.Equals(fv, (version ?? "").Trim(), StringComparison.Ordinal)) { why = "la firma es de otra versión (" + fv + ")"; return false; }
            TrustedKey key = keys.Find(delegate(TrustedKey x) { return x.Id == fk.ToLowerInvariant(); });
            if (key == null) { why = "la firma es de una clave que este programa no reconoce (" + fk + ")"; return false; }
            byte[] sig;
            try { sig = Convert.FromBase64String(fsig); }
            catch (FormatException) { why = "la firma no es base64 válido"; return false; }
            if (sig.Length != key.Modulus.Length) { why = "la firma no corresponde a la clave"; return false; }
            try
            {
                using (var rsa = RSA.Create())
                {
                    rsa.ImportParameters(new RSAParameters { Modulus = key.Modulus, Exponent = key.Exponent });
                    byte[] data = Encoding.UTF8.GetBytes(Message(fa, fv, want));
                    if (!rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) { why = "la firma NO es válida (el archivo o la firma fueron alterados)"; return false; }
                }
            }
            catch (CryptographicException ex) { why = "no se pudo verificar la firma (" + ex.Message + ")"; return false; }
            return true;
        }
    }
}
