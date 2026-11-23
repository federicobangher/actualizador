// 2.0.25: controladores de video AMD (Radeon, Vega, ATI) instalados con AMD Software.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// AMD Software (Adrenalin, Radeon Software, Radeon Settings) instala el controlador de video junto con sus componentes (audio por
    /// HDMI, AMD Crash Defender, AMD Controller Emulation…) como un solo conjunto, y al abrir comprueba que el controlador sea el de su
    /// misma versión. Reemplazar sólo el controlador por otra versión (del Catálogo de Microsoft Update o de Windows Update) deja a AMD
    /// Software con el aviso «la versión de AMD Software no es compatible con el controlador de gráficos instalado» y sin sus funciones, y
    /// los componentes quedan mezclados de versiones distintas. Ese conjunto se actualiza con el instalador de AMD.
    /// Sin AMD Software instalado (sólo el controlador, como lo deja Windows Update) el catálogo sigue sirviendo igual que antes.
    /// </summary>
    public static class AmdGraphics
    {
        const RegexOptions RX = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        static readonly Regex SuiteName = new Regex(@"^AMD (?:Software|Radeon Software|Radeon Settings|Settings)\b", RX);

        /// <summary>¿Está instalado AMD Software (el programa de escritorio de AMD con su controlador de video)?</summary>
        public static bool SuiteInstalled(IList<InstalledProgram> programs)
        {
            return Suite(programs) != null;
        }

        /// <summary>La entrada de AMD Software entre los programas instalados, o null.</summary>
        public static InstalledProgram Suite(IList<InstalledProgram> programs)
        {
            if (programs == null) return null;
            foreach (var p in programs)
            {
                if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                if (!SuiteName.IsMatch(p.Name.Trim())) continue;
                string pub = (p.Publisher ?? "").Trim();
                // «AMD Settings» u otro nombre corto: sólo si lo publica AMD (sin editor, se acepta: así lo registran las viejas).
                if (pub.Length > 0 && pub.IndexOf("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) < 0 && !pub.StartsWith("AMD", StringComparison.OrdinalIgnoreCase)) continue;
                return p;
            }
            return null;
        }

        static readonly Regex FamilyHwid = new Regex(
            @"^(?:PCI\\VEN_1002&|HDAUDIO\\FUNC_01&VEN_1002&|ROOT\\AMD|SWC\\VEN_1002&)", RX);

        /// <summary>
        /// ¿El dispositivo es parte del conjunto de video de AMD? La placa o el gráfico integrado (VEN_1002, el de ATI), su audio por
        /// HDMI/DisplayPort, y los dispositivos de software que instala con él (AMD Crash Defender ROOT\AMDLOG, AMD Controller Emulation
        /// ROOT\AMDXE, AMD Streaming Audio…). No el chipset (VEN_1022), que AMD actualiza con otro paquete.
        /// </summary>
        public static bool IsGraphicsFamily(DriverInfo d)
        {
            if (d == null || string.IsNullOrEmpty(d.HardwareId)) return false;
            return FamilyHwid.IsMatch(d.HardwareId.Trim());
        }

        /// <summary>¿Es la placa de video (o el gráfico integrado) de AMD?</summary>
        public static bool IsGpu(DriverInfo d)
        {
            return d != null && (d.DeviceClass ?? "").Trim().Equals("Display", StringComparison.OrdinalIgnoreCase) &&
                   (d.HardwareId ?? "").Trim().StartsWith(@"PCI\VEN_1002&", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Por qué no se ofrece desde el catálogo un controlador del conjunto de video de AMD.</summary>
        public static string CatalogText(InstalledProgram suite, string offered)
        {
            return "Lo instaló " + SuiteLabel(suite) + ", que trae el controlador de video y sus componentes como un solo conjunto. El Catálogo " +
                   "de Microsoft Update tiene la " + offered + ", pero instalada suelta deja a AMD Software con el aviso «la versión de AMD Software " +
                   "no es compatible con el controlador instalado» y sin sus funciones: por eso no se ofrece. Se actualiza todo junto con el " +
                   "instalador de AMD (AMD Software › Sistema › «Buscar actualizaciones»).";
        }

        /// <summary>Lo que se agrega a una oferta de Windows Update para el conjunto de video de AMD (y no se marca sola).</summary>
        public static string WindowsUpdateText(InstalledProgram suite)
        {
            return " Atención: el controlador lo instaló " + SuiteLabel(suite) + "; si se reemplaza solo, AMD Software avisa que «no es compatible con " +
                   "el controlador instalado». No se marca sola: conviene actualizarlo con AMD Software (Sistema › «Buscar actualizaciones»).";
        }

        static string SuiteLabel(InstalledProgram suite)
        {
            if (suite == null) return "AMD Software";
            return suite.Name.Trim() + (string.IsNullOrEmpty(suite.Version) ? "" : " " + suite.Version.Trim());
        }
    }
}
