using System;
using System.Collections.Generic;

namespace Actualizador.Core
{
    /// <summary>
    /// Versiones de Windows que conoce Actualizador: compilación, nombre y fin de soporte. Es la ÚNICA tabla:
    /// la usan el ciclo de vida (<see cref="Lifecycle"/>), los mensajes de «requiere Windows …» de winget y el
    /// Catálogo de Microsoft Update. Hasta la 1.6.7 había tres tablas y se resolvía «la más parecida»: cuando salió
    /// la 26H2 (26300), heredó la fecha de la 25H2 y lo informaba como cierto (se corrigió).
    ///
    /// Regla: una compilación que no está acá NO toma los datos de otra. Se informa «desconocida» con el motivo
    /// (más nueva que las conocidas, o preliminar del programa Insider). Al salir una versión nueva de Windows se
    /// agrega UNA fila acá (fuentes: «Windows 11 release information» y «Lifecycle» de Microsoft).
    /// </summary>
    public static class WindowsReleases
    {
        public enum Family { Windows10, Windows11, Server, Legacy }

        public sealed class Release
        {
            public Family Family;
            public int Major, Minor, Build;
            /// <summary>Versión corta: «25H2», «1809», «2022».</summary>
            public string Version;
            /// <summary>Nombre completo: «Windows 11 25H2», «Windows Server 2022».</summary>
            public string Name;
            /// <summary>Home y Pro (o la única fecha, si es la misma para todas las ediciones).</summary>
            public DateTime? EndHome;
            /// <summary>Enterprise y Education, si es distinta.</summary>
            public DateTime? EndEnterprise;
            /// <summary>IoT Enterprise LTSC, si es distinta de la LTSC común.</summary>
            public DateTime? EndIot;
            public bool Ltsc;
            public string Note;
        }

        static DateTime D(int y, int m, int d) { return new DateTime(y, m, d); }

        static Release R(Family f, int major, int minor, int build, string version, string name, DateTime? home, DateTime? ent)
        {
            var r = new Release();
            r.Family = f; r.Major = major; r.Minor = minor; r.Build = build; r.Version = version; r.Name = name; r.EndHome = home; r.EndEnterprise = ent;
            return r;
        }

        static Release W10(int build, string version, DateTime home, DateTime ent) { return R(Family.Windows10, 10, 0, build, version, "Windows 10 " + version, home, ent); }
        static Release W11(int build, string version, DateTime home, DateTime ent) { return R(Family.Windows11, 10, 0, build, version, "Windows 11 " + version, home, ent); }
        static Release Srv(int build, string version, DateTime end) { return R(Family.Server, 10, 0, build, version, "Windows Server " + version, end, null); }

        static Release Ltsc(int build, string name, DateTime end, DateTime? iot)
        {
            var r = R(build >= 22000 ? Family.Windows11 : Family.Windows10, 10, 0, build, name, "Windows " + (build >= 22000 ? "11 " : "10 ") + name, end, null);
            r.Ltsc = true; r.EndIot = iot;
            return r;
        }

        /// <summary>Windows de escritorio con soporte general (no LTSC).</summary>
        public static readonly Release[] Client =
        {
            W10(10240, "1507", D(2017, 5, 9), D(2017, 5, 9)),
            W10(10586, "1511", D(2017, 10, 10), D(2018, 4, 10)),
            W10(14393, "1607", D(2018, 4, 10), D(2019, 4, 9)),
            W10(15063, "1703", D(2018, 10, 9), D(2019, 10, 8)),
            W10(16299, "1709", D(2019, 4, 9), D(2020, 10, 13)),
            W10(17134, "1803", D(2019, 11, 12), D(2021, 5, 11)),
            W10(17763, "1809", D(2020, 11, 10), D(2021, 5, 11)),
            W10(18362, "1903", D(2020, 12, 8), D(2020, 12, 8)),
            W10(18363, "1909", D(2021, 5, 11), D(2022, 5, 10)),
            W10(19041, "2004", D(2021, 12, 14), D(2021, 12, 14)),
            W10(19042, "20H2", D(2022, 5, 10), D(2023, 5, 9)),
            W10(19043, "21H1", D(2022, 12, 13), D(2022, 12, 13)),
            W10(19044, "21H2", D(2023, 6, 13), D(2024, 6, 11)),
            W10(19045, "22H2", D(2025, 10, 14), D(2025, 10, 14)),
            W11(22000, "21H2", D(2023, 10, 10), D(2024, 10, 8)),
            W11(22621, "22H2", D(2024, 10, 8), D(2025, 10, 14)),
            W11(22631, "23H2", D(2025, 11, 11), D(2026, 11, 10)),
            W11(26100, "24H2", D(2026, 10, 13), D(2027, 10, 12)),
            W11(26200, "25H2", D(2027, 10, 12), D(2028, 10, 10)),
            W11(26300, "26H2", D(2028, 10, 10), D(2029, 10, 9)),
            // 26H1 es la rama de los equipos nuevos de 2026: salió antes que la 26H2 aunque su número sea mayor.
            W11(28000, "26H1", D(2028, 3, 14), D(2029, 3, 13)),
        };

        /// <summary>Ediciones LTSC/LTSB (EnterpriseS, IoTEnterpriseS).</summary>
        public static readonly Release[] LongTerm =
        {
            Ltsc(10240, "LTSB 2015", D(2025, 10, 14), null),
            Ltsc(14393, "LTSB 2016", D(2026, 10, 13), null),
            Ltsc(17763, "LTSC 2019", D(2029, 1, 9), null),
            Ltsc(19044, "LTSC 2021", D(2027, 1, 12), D(2032, 1, 13)),
            Ltsc(26100, "LTSC 2024", D(2029, 10, 9), D(2034, 10, 10)),
        };

        public static readonly Release[] Servers =
        {
            R(Family.Server, 6, 0, 6002, "2008", "Windows Server 2008", D(2020, 1, 14), null),
            R(Family.Server, 6, 1, 7601, "2008 R2", "Windows Server 2008 R2", D(2020, 1, 14), null),
            R(Family.Server, 6, 2, 9200, "2012", "Windows Server 2012", D(2023, 10, 10), null),
            R(Family.Server, 6, 3, 9600, "2012 R2", "Windows Server 2012 R2", D(2023, 10, 10), null),
            Srv(14393, "2016", D(2027, 1, 12)),
            Srv(16299, "1709", D(2019, 4, 9)),
            Srv(17134, "1803", D(2019, 11, 12)),
            Srv(17763, "2019", D(2029, 1, 9)),
            Srv(18362, "1903", D(2020, 12, 8)),
            Srv(18363, "1909", D(2021, 5, 11)),
            Srv(19041, "2004", D(2021, 12, 14)),
            Srv(19042, "20H2", D(2022, 8, 9)),
            Srv(20348, "2022", D(2031, 10, 14)),
            Srv(25398, "23H2", D(2025, 10, 24)),
            Srv(26100, "2025", D(2034, 11, 14)),
        };

        /// <summary>Windows anteriores a 10 (escritorio): un solo número por versión, sin fecha por edición.</summary>
        public static readonly Release[] Legacy =
        {
            R(Family.Legacy, 5, 1, 2600, "XP", "Windows XP", D(2014, 4, 8), null),
            R(Family.Legacy, 6, 0, 6002, "Vista", "Windows Vista", D(2017, 4, 11), null),
            R(Family.Legacy, 6, 1, 7601, "7", "Windows 7", D(2020, 1, 14), null),
            R(Family.Legacy, 6, 2, 9200, "8", "Windows 8", D(2016, 1, 12), null),
            R(Family.Legacy, 6, 3, 9600, "8.1", "Windows 8.1", D(2023, 1, 10), null),
        };

        static WindowsReleases()
        {
            foreach (var r in Legacy) if (r.Major == 6 && r.Minor == 1) r.Note = "Windows 7: sin soporte (las ESU terminaron en 2023).";
        }

        /// <summary>La compilación más nueva de Windows de escritorio que conoce esta versión de Actualizador.</summary>
        public static int NewestClientBuild
        {
            get { int m = 0; foreach (var r in Client) if (r.Build > m) m = r.Build; return m; }
        }

        public static int NewestServerBuild
        {
            get { int m = 0; foreach (var r in Servers) if (r.Major == 10 && r.Build > m) m = r.Build; return m; }
        }

        /// <summary>
        /// La versión EXACTA de esa compilación, o null si no está en la tabla. Windows anterior a 10 se reconoce
        /// por versión (6.x); Windows 10 y posteriores, por número de compilación.
        /// </summary>
        public static Release Find(int major, int minor, int build, bool server, bool ltsc)
        {
            if (major < 6) return Legacy[0];                                    // XP y anteriores
            if (major == 6 && minor == 0 && !server) return Legacy[1];          // Vista
            if (major == 6)
            {
                foreach (var r in server ? Servers : Legacy) if (r.Major == 6 && r.Minor == minor) return r;
                return null;
            }
            if (major != 10 || minor != 0) return null;
            if (server)
            {
                foreach (var r in Servers) if (r.Major == 10 && r.Build == build) return r;
                return null;
            }
            if (ltsc)
            {
                foreach (var r in LongTerm) if (r.Build == build) return r;
                return null;
            }
            foreach (var r in Client) if (r.Build == build) return r;
            return null;
        }

        /// <summary>Windows 10/11 de escritorio por compilación (sin LTSC), o null.</summary>
        public static Release FindClient(int build)
        {
            foreach (var r in Client) if (r.Build == build) return r;
            return null;
        }

        /// <summary>Compilación de una versión de Windows 11 («25H2» → 26200); null si no se conoce.</summary>
        public static int? Windows11Build(string version)
        {
            string v = (version ?? "").Trim().ToUpperInvariant();
            foreach (var r in Client) if (r.Family == Family.Windows11 && r.Version == v) return r.Build;
            return null;
        }

        /// <summary>Compilación de una versión de Windows 10 («1809» → 17763); null si no se conoce.</summary>
        public static int? Windows10Build(string version)
        {
            string v = (version ?? "").Trim().ToUpperInvariant();
            foreach (var r in Client) if (r.Family == Family.Windows10 && r.Version == v) return r.Build;
            return null;
        }

        /// <summary>Por qué no se conoce la fecha de una compilación (para la nota de la sección y el hallazgo).</summary>
        public static string UnknownExplanation(int major, int minor, int build, bool server)
        {
            string app = AppInfo.Name + " " + AppInfo.Version;
            int newest = server ? NewestServerBuild : NewestClientBuild;
            if (major == 10 && minor == 0 && build > newest)
                return "La compilación " + build + " es más nueva que las versiones de Windows que conoce " + app +
                       ": no se informa una fecha de fin de soporte que podría ser de otra versión. Actualizá Actualizador para conocerla.";
            if (major == 10 && minor == 0)
                return "La compilación " + build + " no es una versión publicada de Windows" + (server ? "" : " (suele ser una compilación preliminar del programa Windows Insider)") +
                       ", o es una edición que " + app + " no conoce: no se informa una fecha de fin de soporte.";
            return "Versión de Windows " + major + "." + minor + " (compilación " + build + ") que " + app + " no conoce.";
        }

        /// <summary>
        /// Nombre corto para mensajes («10.0.22000.0» → «11 21H2», «10.0.17763.0» → «10 1809»). Una compilación que
        /// no está en la tabla se nombra por su número («11 (compilación 27800)»), sin tomar el nombre de otra.
        /// </summary>
        public static string ShortName(int build)
        {
            var r = FindClient(build);
            if (r != null) return (r.Family == Family.Windows11 ? "11 " : "10 ") + r.Version;
            return (build >= 22000 ? "11" : "10") + " (compilación " + build + ")";
        }
    }
}
