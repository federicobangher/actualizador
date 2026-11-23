// Windows: ciclo de vida, versiones, manifiestos, scripts y TLS.
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System;
using Actualizador.Core;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {

        /// <summary>TLS 1.3 sólo donde Windows lo trae: en Windows 10 pedirlo puede tirar abajo el canal seguro entero.</summary>
        static void TestTls13()
        {
            Check(!Http.SupportsTls13(0), "Windows 8.1 o anterior: no");
            Check(!Http.SupportsTls13(19045), "Windows 10 22H2: no");
            Check(!Http.SupportsTls13(19044), "Windows 10 LTSC 2021: no");
            Check(Http.SupportsTls13(20348), "Windows Server 2022: sí");
            Check(Http.SupportsTls13(22000), "Windows 11 21H2: sí");
            Check(Http.SupportsTls13(26100), "Windows 11 24H2: sí");
            Http.EnableModernTls();                      // no lanza en ningún sistema
            Check(true, "EnableModernTls no lanza");
        }

        /// <summary>Fechas del ciclo de vida verificadas en learn.microsoft.com/lifecycle (04/10/2026).</summary>
        static void TestCicloDeVida161()
        {
            Eq(new DateTime(2034, 11, 14), Lifecycle.Evaluate(Os(10, 0, 26100, "ServerDatacenter", true)).EndOfSupport, "Windows Server 2025: 14/11/2034 (antes 10/10/2034)");
            Eq(new DateTime(2034, 10, 10), Lifecycle.Evaluate(Os(10, 0, 26100, "IoTEnterpriseS", false)).EndOfSupport, "Windows 11 IoT LTSC 2024 sigue igual");

        }
        static void TestWindows11Recientes()
        {
            // --- Fin de soporte (Microsoft, «Windows 11 release information», 29/09/2026).
            Eq(new DateTime(2028, 10, 10), Lifecycle.Evaluate(Os(10, 0, 26300, "Professional", false)).EndOfSupport, "Windows 11 26H2 Pro (el equipo del registro): 10/10/2028, no la fecha de 25H2");
            Eq(new DateTime(2029, 10, 9), Lifecycle.Evaluate(Os(10, 0, 26300, "Enterprise", false)).EndOfSupport, "Windows 11 26H2 Enterprise");
            Eq(new DateTime(2028, 3, 14), Lifecycle.Evaluate(Os(10, 0, 28000, "Professional", false)).EndOfSupport, "Windows 11 26H1 Pro");
            Eq(new DateTime(2029, 3, 13), Lifecycle.Evaluate(Os(10, 0, 28000, "Education", false)).EndOfSupport, "Windows 11 26H1 Education");
            Eq(new DateTime(2027, 10, 12), Lifecycle.Evaluate(Os(10, 0, 26200, "Professional", false)).EndOfSupport, "25H2 Pro sigue igual");
            Eq(new DateTime(2029, 10, 9), Lifecycle.Evaluate(Os(10, 0, 26100, "EnterpriseS", false)).EndOfSupport, "LTSC 2024 sigue igual");

            // --- Catálogo: controladores para 26H2.
            Check(CatalogRules.AppliesTo("Windows 11 Client, version 26H2 and later, Servicing Drivers", 26300, false), "26H2 en 26H2: se ofrece (antes no)");
            Check(!CatalogRules.AppliesTo("Windows 11 Client, version 26H2 and later, Servicing Drivers", 26200, false), "26H2 en 25H2: no");
            Check(CatalogRules.AppliesTo("Windows - Client, version 26H2 and later, Servicing Drivers", 26300, false), "«Windows - Client» 26H2 en 26H2");
            Check(CatalogRules.AppliesTo("Windows 11 Client, version 24H2 and later, Servicing Drivers", 26300, false), "24H2 y posteriores en 26H2");
            Check(!CatalogRules.AppliesTo("Windows 11 Client, version 26H1 and later, Servicing Drivers", 26300, false), "26H1 (otra rama) en 26H2: no");
        }

        static void TestNombresDeWindows167()
        {
            Eq("11 24H2", WingetRules.WindowsName("10.0.26100.0"), "24H2");
            Eq("11 25H2", WingetRules.WindowsName("10.0.26200.0"), "25H2 (antes decía 24H2)");
            Eq("11 26H2", WingetRules.WindowsName("10.0.26300.0"), "26H2");
            Eq("11 26H1", WingetRules.WindowsName("10.0.28000.0"), "26H1");
            Eq("10 1809", WingetRules.WindowsName("10.0.17763.0"), "1809 sigue igual");
        }
        static void TestVersionesDeWindows168()
        {
            // --- Una compilación que no está en la tabla no toma la fecha de otra.
            var futura = Lifecycle.Evaluate(Os(10, 0, 29000, "Professional", false));
            Check(futura.Unknown, "compilación 29000 (más nueva que 26H1): desconocida");
            Check(!futura.EndOfSupport.HasValue, "29000: sin fecha (antes, la de 26H1)");
            Check(futura.Label.Contains("desconocida"), "29000: la etiqueta lo dice");
            Check(futura.Note.Contains("más nueva") && futura.Note.Contains("Actualizá Actualizador"), "29000: explica por qué y qué hacer");
            var insider = Lifecycle.Evaluate(Os(10, 0, 27800, "Professional", false));
            Check(insider.Unknown && !insider.EndOfSupport.HasValue, "27800 (Insider, entre 26H2 y 26H1): desconocida, no la fecha de 26H2");
            Check(insider.Note.Contains("Insider"), "27800: sugiere que es preliminar");
            Check(Lifecycle.Evaluate(Os(10, 0, 26120, "Professional", false)).Unknown, "26120 (canal Dev): desconocida, no la de 24H2");
            Check(Lifecycle.Evaluate(Os(10, 0, 22635, "Professional", false)).Unknown, "22635 (canal Beta): desconocida, no la de 23H2");
            Check(Lifecycle.Evaluate(Os(10, 0, 26300, "ServerDatacenter", true)).Unknown, "un Windows Server posterior a 2025: desconocido (antes, la fecha de 2025)");
            Check(Lifecycle.Evaluate(Os(10, 0, 26100, "EnterpriseS", false)).ReleaseName == "Windows 11 LTSC 2024", "nombre de la versión reconocida");
            Check(Lifecycle.Evaluate(Os(10, 0, 19045, "EnterpriseS", false)).Unknown, "LTSC en una compilación que no es LTSC: desconocida");

            // --- Las fechas conocidas siguen iguales (todas las de las versiones anteriores).
            Eq(new DateTime(2028, 10, 10), Lifecycle.Evaluate(Os(10, 0, 26300, "Professional", false)).EndOfSupport, "26H2 Pro");
            Eq(new DateTime(2029, 3, 13), Lifecycle.Evaluate(Os(10, 0, 28000, "Enterprise", false)).EndOfSupport, "26H1 Enterprise");
            Eq(new DateTime(2026, 11, 10), Lifecycle.Evaluate(Os(10, 0, 22631, "Enterprise", false)).EndOfSupport, "23H2 Enterprise");
            Eq(new DateTime(2025, 10, 24), Lifecycle.Evaluate(Os(10, 0, 25398, "ServerStandard", true)).EndOfSupport, "Server 23H2 (canal anual)");
            Eq(new DateTime(2027, 1, 12), Lifecycle.Evaluate(Os(10, 0, 14393, "ServerStandard", true)).EndOfSupport, "Server 2016");
            Eq(new DateTime(2023, 10, 10), Lifecycle.Evaluate(Os(6, 3, 9600, "ServerStandard", true)).EndOfSupport, "Server 2012 R2");
            Eq(new DateTime(2023, 1, 10), Lifecycle.Evaluate(Os(6, 3, 9600, "Core", false)).EndOfSupport, "Windows 8.1");
            Eq(new DateTime(2014, 4, 8), Lifecycle.Evaluate(Os(5, 1, 2600, "Professional", false)).EndOfSupport, "Windows XP (antes, la fecha de Vista)");
            Eq(new DateTime(2017, 4, 11), Lifecycle.Evaluate(Os(6, 0, 6002, "Business", false)).EndOfSupport, "Windows Vista");
            Eq(new DateTime(2021, 5, 11), Lifecycle.Evaluate(Os(10, 0, 17763, "Enterprise", false)).EndOfSupport, "Windows 10 1809 Enterprise");
            Eq(new DateTime(2020, 11, 10), Lifecycle.Evaluate(Os(10, 0, 17763, "Professional", false)).EndOfSupport, "Windows 10 1809 Pro");

            // --- La tabla misma: ordenada, sin repetidos y con fechas coherentes.
            var vistos = new HashSet<int>();
            foreach (var r in WindowsReleases.Client)
            {
                Check(vistos.Add(r.Build), "compilación sin repetir: " + r.Build);
                Check(r.EndHome.HasValue && r.EndEnterprise.HasValue && r.EndEnterprise.Value >= r.EndHome.Value, r.Name + ": Enterprise no termina antes que Home/Pro");
                Check(r.Name.StartsWith("Windows " + (r.Build >= 22000 ? "11 " : "10 ")), r.Name + ": familia según la compilación");
            }
            foreach (var r in WindowsReleases.LongTerm)
                Check(!r.EndIot.HasValue || r.EndIot.Value > r.EndHome.Value, r.Name + ": IoT LTSC dura más que la LTSC común");
            Eq(28000, WindowsReleases.NewestClientBuild, "la compilación más nueva que se conoce");
            Eq(26100, WindowsReleases.NewestServerBuild, "el servidor más nuevo que se conoce");

            // --- Los mensajes de winget y el catálogo usan la misma tabla.
            Eq("11 21H2", WingetRules.WindowsName("10.0.22000.0"), "22000 se nombra con su versión");
            Eq("11 (compilación 27800)", WingetRules.WindowsName("10.0.27800.0"), "una compilación desconocida se nombra por su número, no «11 26H2»");
            Eq("10 (compilación 19046)", WingetRules.WindowsName("10.0.19046.0"), "Windows 10 desconocido: por su número");
            Eq("10 2004", WingetRules.WindowsName("10.0.19041.0"), "10 2004 sigue igual");
            Check(!CatalogRules.AppliesTo("Windows 11 Client, version 27H1 and later, Servicing Drivers", 28000, false), "catálogo: una versión que no se conoce se toma como más nueva que el equipo");
            Check(CatalogRules.AppliesTo("Windows 10, version 1809 and later, Servicing Drivers", 19045, false), "catálogo: Windows 10 1809 y posteriores en 22H2");
            Eq(26200, WindowsReleases.Windows11Build("25h2"), "25H2 → 26200 (sin importar mayúsculas)");
            Eq(17763, WindowsReleases.Windows10Build("1809"), "1809 → 17763");
            Check(!WindowsReleases.Windows10Build("27H1").HasValue, "una versión que no existe: null");

            // --- ESU de Windows 10 22H2: para hogares terminaron el 13/10/2026.
            var antes = Lifecycle.Evaluate(Os(10, 0, 19045, "Professional", false), new DateTime(2026, 10, 1));
            Check(antes.Note.Contains("2026-10-13") && antes.Note.Contains("Sólo recibe parches"), "Pro antes del 13/10/2026: ESU para hogares hasta esa fecha");
            var despues = Lifecycle.Evaluate(Os(10, 0, 19045, "Core", false), new DateTime(2026, 10, 18));
            Check(despues.Note.Contains("terminaron") && despues.Note.Contains("ya no recibe parches"), "Home después del 13/10/2026: ya no hay parches (la 1.6.7 decía que sí, con ESU)");
            var empresa = Lifecycle.Evaluate(Os(10, 0, 19045, "Enterprise", false), new DateTime(2026, 10, 18));
            Check(empresa.Note.Contains("2028-10-10") && !empresa.Note.Contains("terminaron"), "Enterprise: ESU para empresas hasta el 10/10/2028");
            Check(Lifecycle.Evaluate(Os(10, 0, 19045, "Enterprise", false), new DateTime(2028, 11, 1)).Note.Contains("terminaron"), "Enterprise después de 2028: terminaron");
            Check(Lifecycle.Evaluate(Os(10, 0, 19045, "Core", false), new DateTime(2026, 10, 18)).Label.StartsWith("Sin soporte"), "22H2: «Sin soporte»");

        }

        static void TestLifecycle()
        {
            Check(Lifecycle.Evaluate(Os(6, 1, 7601, "Professional", false)).Label.StartsWith("Sin soporte"), "Windows 7");
            Check(Lifecycle.Evaluate(Os(10, 0, 19045, "Professional", false)).Label.StartsWith("Sin soporte"), "Windows 10 22H2 (fin 14/10/2025)");
            Eq(new DateTime(2029, 1, 9), Lifecycle.Evaluate(Os(10, 0, 17763, "EnterpriseS", false)).EndOfSupport, "LTSC 2019");
            Eq(new DateTime(2031, 10, 14), Lifecycle.Evaluate(Os(10, 0, 20348, "ServerStandard", true)).EndOfSupport, "Server 2022");
            Eq(new DateTime(2027, 10, 12), Lifecycle.Evaluate(Os(10, 0, 26200, "Professional", false)).EndOfSupport, "Windows 11 25H2 Pro");
            Eq(new DateTime(2028, 10, 10), Lifecycle.Evaluate(Os(10, 0, 26200, "Enterprise", false)).EndOfSupport, "Windows 11 25H2 Enterprise");
            Check(Os(10, 0, 22631, "", false).IsWindows11, "22631 es Windows 11");
            Check(!Os(10, 0, 20348, "", true).IsWindows11, "Server 2022 no es Windows 11");
        }

        static string Sample(string name) { return File.ReadAllText(Path.Combine(samples, name), Encoding.UTF8); }

        static WingetRow Find(List<WingetRow> rows, string id)
        {
            foreach (var r in rows) if (r.Id == id) return r;
            return null;
        }

        /// <summary>
        /// Un manifiesto mal formado hace que Windows rechace el ejecutable entero
        /// («la configuración en paralelo no es correcta»), y Mono no lo detecta porque
        /// ignora los manifiestos Win32. Estas reglas son las que Windows aplica.
        /// </summary>
        static void RevisarManifiesto(string nombre, string nivelEsperado)
        {
            string path = ProjectFile(Path.Combine("res", nombre));
            Check(path != null, "res/" + nombre + " encontrado");
            if (path == null) return;
            string m = File.ReadAllText(path, Encoding.UTF8);

            int apps = 0, idx = 0;
            while ((idx = m.IndexOf("<asmv3:application", idx, StringComparison.Ordinal)) >= 0) { apps++; idx++; }
            Eq(1, apps, nombre + ": exactamente un elemento asmv3:application (dos invalidan el manifiesto)");

            int trust = 0; idx = 0;
            while ((idx = m.IndexOf("<trustInfo", idx, StringComparison.Ordinal)) >= 0) { trust++; idx++; }
            Eq(1, trust, nombre + ": un solo trustInfo");
            int compat = 0; idx = 0;
            while ((idx = m.IndexOf("<compatibility", idx, StringComparison.Ordinal)) >= 0) { compat++; idx++; }
            Eq(1, compat, nombre + ": un solo compatibility");

            var known = new List<string> {
                "http://schemas.microsoft.com/SMI/2005/WindowsSettings",
                "http://schemas.microsoft.com/SMI/2011/WindowsSettings",
                "http://schemas.microsoft.com/SMI/2016/WindowsSettings",
                "http://schemas.microsoft.com/SMI/2017/WindowsSettings",
                "http://schemas.microsoft.com/SMI/2019/WindowsSettings",
                "http://schemas.microsoft.com/SMI/2020/WindowsSettings" };
            idx = 0;
            int settings = 0;
            while ((idx = m.IndexOf("<asmv3:windowsSettings", idx, StringComparison.Ordinal)) >= 0)
            {
                settings++;
                int end = m.IndexOf('>', idx);
                string tag = m.Substring(idx, end - idx);
                bool ok = false;
                foreach (string ns in known) if (tag.Contains(ns)) ok = true;
                Check(ok, nombre + ": espacio de nombres conocido en windowsSettings");
                idx = end;
            }
            Check(settings >= 1, nombre + ": hay al menos un windowsSettings");

            // Exactamente un nivel de ejecución, y el esperado para este ejecutable.
            var niveles = System.Text.RegularExpressions.Regex.Matches(m, "requestedExecutionLevel level=\"(\\w+)\"");
            Eq(1, niveles.Count, nombre + ": un solo requestedExecutionLevel");
            if (niveles.Count == 1) Eq(nivelEsperado, niveles[0].Groups[1].Value, nombre + ": nivel de ejecución");
            Check(m.Contains("uiAccess=\"false\""), nombre + ": uiAccess desactivado");
            // 2.0.8: sólo la ventana pide siempre administrador; la consola y el aviso nunca (scripts y Programador de tareas).
            if (nivelEsperado != "requireAdministrator")
                Check(m.IndexOf("requireAdministrator", StringComparison.Ordinal) < 0, nombre + ": sin requireAdministrator (la consola y el aviso corren sin elevar)");

            Check(m.Contains("<dpiAware>true</dpiAware>"), nombre + ": declara compatibilidad con DPI");
            Check(m.Contains("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"), nombre + ": declara compatibilidad con Windows 10/11");
            Check(m.IndexOf("heapType", StringComparison.OrdinalIgnoreCase) < 0, nombre + ": sin elementos que no aporten y puedan invalidarlo");
            Check(m.TrimStart().StartsWith("<?xml") && m.Contains("<assembly") && m.TrimEnd().EndsWith("</assembly>"), nombre + ": estructura del manifiesto");
        }

        /// <summary>
        /// Trampa de cmd: dentro de un bloque entre paréntesis, un ")" sin escapar ni entrecomillar
        /// —aunque esté en el texto de un echo— cierra el bloque antes de tiempo, y lo que sigue se
        /// ejecuta siempre. Así build.bat intentaba firmar sin que hubiera configuración de firma.
        /// </summary>
        public static List<string> LintBat(string texto)
        {
            var problemas = new List<string>();
            string[] lineas = texto.Replace("\r\n", "\n").Split('\n');
            int prof = 0;
            for (int n = 0; n < lineas.Length; n++)
            {
                string s = lineas[n].Trim();
                if (s.Length == 0 || s.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || s.StartsWith("::")) continue;
                bool esEcho = System.Text.RegularExpressions.Regex.IsMatch(s, @"^@?echo[\s.(:]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                bool comillas = false;
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '^' && !comillas) { i++; continue; }
                    if (c == '"') { comillas = !comillas; continue; }
                    if (comillas || (c != '(' && c != ')')) continue;
                    if (esEcho && c == '(' && i <= 5) continue;               // echo( y echo.
                    if (c == '(') { if (!esEcho) prof++; continue; }
                    if (prof == 0) continue;                                   // ")" suelto fuera de bloque: inofensivo
                    if (esEcho) problemas.Add("línea " + (n + 1) + ": ')' en un echo cierra el bloque antes de tiempo: " + s);
                    prof--;
                }
            }
            if (prof != 0) problemas.Add("quedan " + prof + " bloque(s) sin cerrar");

            // Dentro de comillas cmd NO interpreta el ^: llega literal a echo o a PowerShell.
            // Así firmar.bat le pasaba «^|» a PowerShell y build.bat no calculaba las huellas.
            for (int n = 0; n < lineas.Length; n++)
            {
                string[] partes = lineas[n].Split('"');
                for (int i = 1; i < partes.Length; i += 2)
                    if (System.Text.RegularExpressions.Regex.IsMatch(partes[i], @"\^[|&<>()^]"))
                        problemas.Add("línea " + (n + 1) + ": '^' dentro de comillas llega literal: " + lineas[n].Trim());
            }
            return problemas;
        }

        /// <summary>
        /// El número de versión está repetido en AppInfo, en los AssemblyInfo de la interfaz y de la
        /// consola, y en el manifiesto. Si uno queda atrás, Windows, «Acerca de» y el nombre de la
        /// entrega muestran versiones distintas: esta prueba no lo deja pasar.
        /// </summary>
        static InstalledProgram Prog(string name, string version, string publisher, string scope, string location, string arch)
        {
            var p = new InstalledProgram();
            p.Name = name; p.Version = version; p.Publisher = publisher; p.Scope = scope;
            p.InstallLocation = location; p.Architecture = arch; p.UninstallString = "";
            return p;
        }

        /// <summary>Arma una tabla como la imprime winget: cada columna del ancho de su valor más largo + 1.</summary>
        static string TablaWinget(string[] cab, params string[][] filas)
        {
            int[] w = new int[cab.Length];
            for (int c = 0; c < cab.Length; c++)
            {
                w[c] = cab[c].Length;
                foreach (var f in filas) if (c < f.Length && f[c].Length > w[c]) w[c] = f[c].Length;
            }
            var sb = new StringBuilder();
            int total = 0;
            for (int c = 0; c < cab.Length; c++) { sb.Append(c < cab.Length - 1 ? cab[c].PadRight(w[c] + 1) : cab[c]); total += w[c] + 1; }
            sb.Append("\n").Append(new string('-', total)).Append("\n");
            foreach (var f in filas)
            {
                var linea = new StringBuilder();
                for (int c = 0; c < cab.Length; c++) { string v = c < f.Length ? f[c] : ""; linea.Append(c < cab.Length - 1 ? v.PadRight(w[c] + 1) : v); }
                sb.Append(linea.ToString().TrimEnd()).Append("\n");
            }
            return sb.ToString();
        }
    }
}
