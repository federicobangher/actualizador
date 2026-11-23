// Tecnología de un instalador descargado (Inno Setup, NSIS, MSI) y sus interruptores silenciosos.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Tecnología de un instalador descargado, reconocida por su contenido.</summary>
    public enum InstallerKind { Unknown, Inno, Nsis, Msi }

    public static class InstallerRules
    {
        static readonly byte[] Ole = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

        /// <summary>
        /// MSI por el contenedor OLE; Inno Setup y NSIS por las marcas que dejan en el ejecutable
        /// («Inno Setup», «Nullsoft»). Se leen como mucho los primeros 8 MB.
        /// </summary>
        public static InstallerKind Detect(Stream s)
        {
            s.Position = 0;
            byte[] head = new byte[8];
            int n = s.Read(head, 0, head.Length);
            if (n == 8)
            {
                bool ole = true;
                for (int i = 0; i < 8; i++) if (head[i] != Ole[i]) { ole = false; break; }
                if (ole) { s.Position = 0; return InstallerKind.Msi; }
            }
            if (n < 2 || head[0] != (byte)'M' || head[1] != (byte)'Z') { s.Position = 0; return InstallerKind.Unknown; }
            s.Position = 0;
            var buf = new byte[Math.Min(s.Length, 8L * 1024 * 1024)];
            int read = 0;
            while (read < buf.Length) { int r = s.Read(buf, read, buf.Length - read); if (r <= 0) break; read += r; }
            s.Position = 0;
            // Inno Setup: la tabla de desplazamientos del cargador («rDlPtS»), el encabezado de los datos
            // («Inno Setup Setup Data») o el texto de la información de versión (UTF-16).
            if (Contains(buf, read, Encoding.ASCII.GetBytes("rDlPtS")) || Contains(buf, read, Encoding.ASCII.GetBytes("Inno Setup")) ||
                Contains(buf, read, Encoding.Unicode.GetBytes("Inno Setup"))) return InstallerKind.Inno;
            // NSIS: la firma del primer encabezado («NullsoftInst»).
            if (Contains(buf, read, Encoding.ASCII.GetBytes("NullsoftInst")) || Contains(buf, read, Encoding.ASCII.GetBytes("Nullsoft Install System")))
                return InstallerKind.Nsis;
            return InstallerKind.Unknown;
        }

        static bool Contains(byte[] buf, int len, byte[] p)
        {
            for (int i = 0; i + p.Length <= len; i++)
            {
                if (buf[i] != p[0]) continue;
                int j = 1;
                while (j < p.Length && buf[i + j] == p[j]) j++;
                if (j == p.Length) return true;
            }
            return false;
        }

        /// <summary>Interruptores de instalación silenciosa de cada tecnología (null si no se conocen).</summary>
        public static string SilentArgs(InstallerKind kind)
        {
            switch (kind)
            {
                case InstallerKind.Inno: return "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";
                case InstallerKind.Nsis: return "/S";
                default: return null;
            }
        }

        public static string KindText(InstallerKind kind)
        {
            switch (kind)
            {
                case InstallerKind.Inno: return "Inno Setup";
                case InstallerKind.Nsis: return "NSIS";
                case InstallerKind.Msi: return "MSI";
                default: return "desconocido";
            }
        }

        /// <summary>
        /// ¿La firma es de alguno de los fabricantes esperados? Se compara la organización (O=) o el
        /// nombre común (CN=) del certificado, sin la forma jurídica («Adobe Inc.» = «Adobe Inc»),
        /// nunca por parecido: «Vivaldi Technologies AS Evil» no es «Vivaldi Technologies AS».
        /// </summary>
        public static bool SignerAccepted(string subject, string[] expected)
        {
            if (expected == null || expected.Length == 0) return false;
            foreach (string n in WingetRules.SignerNames(subject))
            {
                string got = WingetRules.NormalizeCompany(n);
                if (got.Length < 3) continue;
                foreach (string e in expected)
                    if (got == WingetRules.NormalizeCompany(e)) return true;
            }
            return false;
        }

        static readonly Regex SafeArgs = new Regex(@"^[A-Za-z0-9 /=_.:\-]{0,200}$", RegexOptions.Compiled);

        /// <summary>
        /// Defensa contra la degradación: si la versión del instalador descargado es conocida y es
        /// MÁS VIEJA que la instalada, no se instala (un servidor comprometido podría servir un
        /// instalador viejo, vulnerable y legítimamente firmado). null si se acepta.
        /// </summary>
        public static string Downgrade(string installerVersion, string installed)
        {
            if (VersionUtil.Parse(installerVersion) == null || VersionUtil.Parse(installed) == null) return null;
            return VersionUtil.IsNewer(installed, installerVersion)
                ? "el instalador descargado es de la " + VersionUtil.Normalize(installerVersion) + ", más vieja que la instalada (" + installed + "): no se instala"
                : null;
        }

        /// <summary>Los interruptores del catálogo son fijos; igual se exige ASCII simple, sin comillas ni variables.</summary>
        public static bool IsSafeArgs(string args) { return args == null || SafeArgs.IsMatch(args); }
    }
}
