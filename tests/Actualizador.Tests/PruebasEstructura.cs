// 2.0: coherencia del proyecto — versiones, manifiestos, solución, scripts, documentación y la consola.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Updates;
using Consola = global::Actualizador.Cli.Program;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static string Raiz()
        {
            string sln = ProjectFile("Actualizador.sln");
            return sln == null ? null : Path.GetDirectoryName(sln);
        }

        static string Leer(string raiz, string relativo) { return File.ReadAllText(Path.Combine(raiz, relativo.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8); }

        static void TestEstructura20()
        {
            string raiz = Raiz();
            Check(raiz != null, "se encuentra Actualizador.sln");
            if (raiz == null) return;

            // Versiones: una sola fuente (Directory.Build.props) y los manifiestos iguales.
            var m = Regex.Match(Leer(raiz, "Directory.Build.props"), "<Version>([^<]+)</Version>");
            Eq(AppInfo.Version, m.Groups[1].Value, "Directory.Build.props y AppInfo.Version");
            var niveles = new Dictionary<string, string> { { "App", "requireAdministrator" }, { "Cli", "asInvoker" }, { "Aviso", "asInvoker" } };
            foreach (var kv in niveles)
            {
                string man = Leer(raiz, "src/Actualizador." + kv.Key + "/app.manifest");
                Check(man.Contains("version=\"" + AppInfo.Version + ".0\""), kv.Key + ": versión del manifiesto");
                Check(man.Contains("level=\"" + kv.Value + "\""), kv.Key + ": " + kv.Value);
                Check(man.Contains("8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a"), kv.Key + ": declara Windows 10/11");
            }

            // La revisión programada abre el ejecutable que existe.
            string aviso = Leer(raiz, "src/Actualizador.Aviso/Actualizador.Aviso.csproj");
            Check(aviso.Contains("<AssemblyName>" + Path.GetFileNameWithoutExtension(ScheduledReview.AvisoExe) + "</AssemblyName>"), "ScheduledReview.AvisoExe es el ejecutable del aviso");
            Check(Leer(raiz, "src/Actualizador.App/Actualizador.App.csproj").Contains("<AssemblyName>Actualizador</AssemblyName>"), "la ventana es Actualizador.exe");
            Check(Leer(raiz, "src/Actualizador.Cli/Actualizador.Cli.csproj").Contains("<AssemblyName>Actualizador.Cli</AssemblyName>"), "la consola es Actualizador.Cli.exe");

            // Solución completa.
            string sln = Leer(raiz, "Actualizador.sln");
            foreach (string p in new[] { "Actualizador.Core", "Actualizador.Engine", "Actualizador.App", "Actualizador.Cli", "Actualizador.Aviso", "Actualizador.Tests" })
                Check(sln.Contains(p + ".csproj"), "la solución incluye " + p);

            // Nada de la auditoría quedó en el código.
            var prohibidos = new[] { "AuditSection", "SectionCatalog", "AuditRunner", "AuditComparison", "ReportWriter", "SecurityAnalyzer", "namespace Auditor", "using Auditor" };
            foreach (string f in Directory.GetFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                string t = File.ReadAllText(f);
                foreach (string x in prohibidos) if (t.Contains(x)) Check(false, Path.GetFileName(f) + " todavía usa " + x);
                if (Regex.IsMatch(t, @"\b(instalar|desinstalar|build|empaquetar|firmar)\.bat\b")) Check(false, Path.GetFileName(f) + " menciona un .bat que ya no existe");
            }
            foreach (string f in Directory.GetFiles(raiz, "*.bat", SearchOption.AllDirectories)) Check(false, "quedó un .bat: " + f);

            // Scripts.
            foreach (string s in new[] { "build/Build.ps1", "build/Comun.ps1", "build/Publish.ps1", "build/Sign.ps1", "build/Empaquetar.ps1",
                                         "deploy/Install-Actualizador.ps1", "deploy/Uninstall-Actualizador.ps1" })
            {
                byte[] b = File.ReadAllBytes(Path.Combine(raiz, s));
                Check(b.Length > 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF, s + ": UTF-8 con BOM (Windows PowerShell 5.1 lee mal los acentos sin BOM)");
                string t = Encoding.UTF8.GetString(b);
                Check(t.IndexOfAny(new[] { '\u201C', '\u201D', '\u2018', '\u2019' }) < 0, s + ": sin comillas tipográficas (PowerShell las toma como comillas)");
                Check(Balanceado(t), s + ": llaves y paréntesis balanceados");
                if (!s.EndsWith("Comun.ps1"))   // Comun.ps1 es una biblioteca: hereda el modo de quien la carga
                    Check(t.Contains("Set-StrictMode") && t.Contains("$ErrorActionPreference = 'Stop'"), s + ": modo estricto y errores que detienen");
                Check(!Regex.IsMatch(t, @"\?\?|\?\.\w"), s + ": compatible con Windows PowerShell 5.1 (sin ?? ni ?.)");
                Check(!Regex.IsMatch(t, @"(?m)^\s*Write-Paso\s+'[^']*'\s*\+"), s + ": sin concatenación suelta en argumentos");
            }
            foreach (string c in new[] { "Instalar.cmd", "deploy/Instalar.cmd" })
            {
                byte[] b = File.ReadAllBytes(Path.Combine(raiz, c));
                bool ascii = true, crlf = true;
                for (int i = 0; i < b.Length; i++)
                {
                    if (b[i] > 127) ascii = false;
                    if (b[i] == '\n' && (i == 0 || b[i - 1] != '\r')) crlf = false;
                }
                Check(ascii && crlf, c + ": ASCII con CRLF (cmd.exe)");
            }
            Eq(Leer(raiz, "Instalar.cmd"), Leer(raiz, "deploy/Instalar.cmd"), "Instalar.cmd igual en la raíz y en deploy");
            string emp = Leer(raiz, "build/Empaquetar.ps1");
            var lista = Regex.Match(emp, @"foreach \(\$f in @\(([^)]*)\)\)");
            Check(lista.Success, "Empaquetar.ps1: lista de archivos de la entrega");
            int copiados = 0;
            foreach (Match x in Regex.Matches(lista.Groups[1].Value, @"'([^']+)'"))
            {
                copiados++;
                Check(File.Exists(Path.Combine(raiz, "deploy", x.Groups[1].Value)), "Empaquetar.ps1 copia deploy/" + x.Groups[1].Value);
            }
            Eq(5, copiados, "la entrega lleva Instalar.cmd, los dos scripts, la política de ejemplo y el LEEME");
            string pub = Leer(raiz, "build/Publish.ps1");
            Check(pub.Contains("'-f', 'net8.0-windows'"), "Publish.ps1 elige el marco (los proyectos usan TargetFrameworks en plural: sin -f, publish falla con NETSDK1129)");
            Check(pub.Contains("Uninstall-Actualizador.ps1") && Leer(raiz, "deploy/Install-Actualizador.ps1").Contains("Uninstall-Actualizador.ps1"), "el desinstalador queda en la carpeta del programa");
            Check(Leer(raiz, "deploy/Install-Actualizador.ps1").Contains("DisplayName = $Nombre") && Leer(raiz, "deploy/Install-Actualizador.ps1").Contains("$Editor = '" + ProgramRules.SelfPublisher + "'"),
                  "el instalador registra el nombre y editor que reconoce ProgramRules.IsSelf");
            Check(Regex.IsMatch("Actualizador-" + AppInfo.Version + ".zip", @"^Actualizador[-_ ]v?\d+\.\d+\.\d+\.zip$"), "el nombre de la entrega lo reconoce el aviso de versiones nuevas");

            // Política de ejemplo y documentación coherentes con el código.
            var ejemplo = new Policy();
            ejemplo.ApplyJson(Leer(raiz, "deploy/politica.ejemplo.json"));
            Check(ejemplo.Problems.Count == 0 && !ejemplo.IsEmpty, "politica.ejemplo.json es válida");
            string doc = Leer(raiz, "docs/POLITICAS.md");
            foreach (string id in Policy.SourceIds) Check(doc.Contains("`" + id + "`"), "POLITICAS.md documenta la fuente " + id);
            foreach (string k in new[] { "bloquear", "editoresPermitidos", "permitirAbrirPaginas", "permitirInstaladoresSinFirma", "permitirForzarControladores", "permitirInstalarWinget", "permitirInstaladoresConVentana",
                                         "exigirPuntoDeRestauracion", "exigirAdministrador", "bloquearOpciones", "catalogoFuentesUrl" })
            {
                Check(doc.Contains("`" + k + "`"), "POLITICAS.md documenta " + k);
                Check(Leer(raiz, "src/Actualizador.Core/Security/Policy.cs").Contains("\"" + k + "\""), "Policy.cs lee " + k);
            }
            string seg = Leer(raiz, "docs/SEGURIDAD.md");
            foreach (var f in typeof(EventLogWriter.Ids).GetFields())
                Check(seg.Contains(((int)f.GetValue(null)).ToString()), "SEGURIDAD.md documenta el evento " + f.Name);
            foreach (string d in new[] { "README.md", "CHANGELOG.md", "docs/ARQUITECTURA.md" }) Check(File.Exists(Path.Combine(raiz, d)), d);
            Check(Leer(raiz, "CHANGELOG.md").Contains("## " + AppInfo.Version), "CHANGELOG con la versión actual");
        }

        /// <summary>Llaves, corchetes y paréntesis balanceados fuera de cadenas y comentarios (control grueso de sintaxis).</summary>
        static bool Balanceado(string t)
        {
            var pila = new Stack<char>();
            bool comentarioBloque = false;
            foreach (string linea0 in t.Split('\n'))
            {
                string linea = linea0;
                int i = 0;
                while (i < linea.Length)
                {
                    char c = linea[i];
                    if (comentarioBloque) { if (c == '#' && i + 1 < linea.Length && linea[i + 1] == '>') { comentarioBloque = false; i += 2; continue; } i++; continue; }
                    if (c == '<' && i + 1 < linea.Length && linea[i + 1] == '#') { comentarioBloque = true; i += 2; continue; }
                    if (c == '#') break;
                    if (c == '\'')
                    {
                        i++;
                        while (i < linea.Length) { if (linea[i] == '\'') { if (i + 1 < linea.Length && linea[i + 1] == '\'') { i += 2; continue; } break; } i++; }
                        i++; continue;
                    }
                    if (c == '"')
                    {
                        i++;
                        while (i < linea.Length && linea[i] != '"') { if (linea[i] == '`') i++; i++; }
                        i++; continue;
                    }
                    if (c == '`') { i += 2; continue; }
                    if (c == '(' || c == '{' || c == '[') pila.Push(c);
                    else if (c == ')' || c == '}' || c == ']')
                    {
                        if (pila.Count == 0) return false;
                        char o = pila.Pop();
                        if ((c == ')' && o != '(') || (c == '}' && o != '{') || (c == ']' && o != '[')) return false;
                    }
                    i++;
                }
            }
            return pila.Count == 0 && !comentarioBloque;
        }

        // ------------------------------------------------------------------ Consola
        static void TestConsola20()
        {
            Consola.Args a; string e;
            Check(Consola.Parse(new string[0], out a, out e) && a.Command == Consola.Command.Buscar, "sin argumentos: buscar");
            Check(Consola.Parse(new[] { "actualizar", "--tipos", "programas,controladores", "--cerrar-programas" }, out a, out e) &&
                  a.Command == Consola.Command.Actualizar && a.Kinds.Count == 2 && a.Kinds.Contains("programas") && a.CloseRunning, "actualizar con tipos");
            Check(Consola.Parse(new[] { "actualizar", "--tipos=windows" }, out a, out e) && a.Kinds.Contains("windows"), "--opcion=valor");
            Check(!Consola.Parse(new[] { "actualizar", "--tipos", "juegos" }, out a, out e) && e.Contains("juegos"), "tipo desconocido");
            Check(!Consola.Parse(new[] { "actualizar", "--tipos" }, out a, out e), "--tipos sin valor");
            Check(Consola.Parse(new[] { "--aplicar", "store" }, out a, out e) && a.Command == Consola.Command.Actualizar, "--aplicar de la 1.x sigue funcionando");
            Check(!Consola.Parse(new[] { "buscar", "--cerrar-programas" }, out a, out e) && e.Contains("actualizar"), "opciones de instalar sólo con actualizar");
            Check(Consola.Parse(new[] { "buscar", "--json" }, out a, out e) && a.Json == "-", "--json sin archivo: salida estándar");
            Check(Consola.Parse(new[] { "buscar", "--json", "C:\\Inv\\", "--csv", "x.csv" }, out a, out e) && a.Json == "C:\\Inv\\" && a.Csv == "x.csv", "--json y --csv con archivo");
            Check(!Consola.Parse(new[] { "buscar", "--csv" }, out a, out e), "--csv sin archivo");
            Check(Consola.Parse(new[] { "historial", "50" }, out a, out e) && a.HistoryCount == 50, "historial n");
            Check(!Consola.Parse(new[] { "historial", "cero" }, out a, out e), "historial con un número inválido");
            Check(Consola.Parse(new[] { "ignorar", "TeamViewer.TeamViewer@15.58.4" }, out a, out e) && a.Values.Count == 1, "ignorar");
            Check(!Consola.Parse(new[] { "ignorar" }, out a, out e), "ignorar sin qué");
            Check(Consola.Parse(new[] { "programar", "semanal", "--dia", "viernes", "--hora", "12:30" }, out a, out e) && a.Day == "viernes" && a.Time == "12:30", "programar");
            Check(!Consola.Parse(new[] { "programar", "mensual" }, out a, out e), "programar: frecuencia inválida");
            Check(!Consola.Parse(new[] { "programar", "semanal", "--hora", "25:00" }, out a, out e), "programar: hora inválida");
            Check(!Consola.Parse(new[] { "programar", "semanal", "--dia", "feriado" }, out a, out e), "programar: día inválido");
            Check(Consola.Parse(new[] { "política" }, out a, out e) && a.Command == Consola.Command.Politica, "politica con tilde");
            Check(Consola.Parse(new[] { "verificar-registro" }, out a, out e) && a.Command == Consola.Command.VerificarRegistro, "verificar-registro");
            Check(Consola.Parse(new[] { "diagnostico", "C:\\Soporte\\" }, out a, out e) && a.Command == Consola.Command.Diagnostico && a.Values[0] == "C:\\Soporte\\", "diagnostico con carpeta");
            Check(Consola.Parse(new[] { "buscar", "/ayuda" }, out a, out e) && a.Command == Consola.Command.Ayuda, "/ayuda");
            Check(!Consola.Parse(new[] { "auditar" }, out a, out e) && e.Contains("auditar"), "la auditoría ya no existe");
            Check(!Consola.Parse(new[] { "buscar", "--secciones", "red" }, out a, out e), "opciones de la auditoría: error claro");
            Check(!Consola.Parse(new[] { "buscar", "sobra" }, out a, out e) && e.Contains("sobra"), "argumento de más");
            Eq(10, Consola.ExitPending, "código 10: hay pendientes");
            Eq(5, Consola.ExitPolicy, "código 5: política");
        }
    }
}
