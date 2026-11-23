// Pruebas de la 2.0.11: el programa pasa a llamarse «Actualizador» (antes SuperProgram) y tiene un icono nuevo.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static void TestNombre2011()
        {
            Eq("Actualizador", AppInfo.Name, "el programa se llama Actualizador");
            Eq("SuperProgram", AppInfo.LegacyName, "el nombre anterior se conoce para pasar de uno a otro");
            Check(AppInfo.Title.StartsWith("Actualizador", StringComparison.Ordinal) && !AppInfo.Title.Contains("SuperProgram"), "título de la ventana: " + AppInfo.Title);
            Eq("Actualizador.portable", AppInfo.PortableMarker, "marca de la portable con el nombre nuevo");

            // Las carpetas de datos llevan el nombre nuevo.
            Check(AppInfo.UserDataDirectory.EndsWith("Actualizador", StringComparison.Ordinal), "datos del usuario en …\\Actualizador");

            // La política: la clave nueva y, si no está, la vieja (las directivas ya desplegadas siguen valiendo).
            Eq(@"SOFTWARE\Policies\Actualizador", Policy.RegistryPath, "clave de la directiva de grupo");
            Eq(@"SOFTWARE\Policies\SuperProgram", Policy.LegacyRegistryPath, "clave vieja de la directiva de grupo");

            // Tareas programadas: nombre nuevo; la vieja se reconoce para quitarla.
            Eq("Actualizador - Revisión de actualizaciones (ana)", ScheduledReview.TaskName("ana"), "nombre de la revisión programada");
            Eq("SuperProgram - Revisión de actualizaciones (ana)", ScheduledReview.LegacyTaskName("ana"), "nombre viejo de la revisión programada");
            Eq("Actualizador.Aviso.exe", ScheduledReview.AvisoExe, "el aviso se llama Actualizador.Aviso.exe");

            // Versiones nuevas en una carpeta: con el nombre nuevo y también las entregas viejas.
            string best;
            Eq("2.0.11", SelfUpdate.NewestDelivery(new[] { @"D:\Entregas\SuperProgram-2.0.10.7z", @"D:\Entregas\Actualizador-2.0.11.7z" }, false, out best),
               "la entrega con el nombre nuevo es la más nueva");
            Check(best != null && best.EndsWith("Actualizador-2.0.11.7z", StringComparison.Ordinal), "y es esa: " + best);
            Eq("2.0.10", SelfUpdate.NewestDelivery(new[] { @"D:\Entregas\SuperProgram-2.0.10.7z" }, false, out best), "una entrega vieja (SuperProgram) se sigue reconociendo");
            Eq("2.1.0", SelfUpdate.NewestDelivery(new[] { @"D:\Entregas\Actualizador_2.1.0_portable" }, true, out best), "la portable con el nombre nuevo");
            Eq(null, SelfUpdate.NewestDelivery(new[] { @"D:\Entregas\OtroActualizador-9.0.0.7z" }, false, out best), "otro programa no se confunde");

            // Este programa en la lista de instalados: con el nombre nuevo y el editor.
            Check(ProgramRules.IsSelf("Actualizador", "Fede", ""), "se reconoce a sí mismo en «Aplicaciones instaladas»");
            Check(!ProgramRules.IsSelf("Actualizador", "Otro editor", ""), "otro «Actualizador» de otro editor no es este");
        }

        static void TestRenombrado2011()
        {
            string raiz = Raiz();
            Check(raiz != null, "se encuentra Actualizador.sln");
            if (raiz == null) return;

            // Sólo nombran a SuperProgram los lugares que tienen que reconocer lo viejo.
            var permitidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Context.cs", "Policy.cs", "SelfUpdate.cs", "BrowserProvider.cs", "ScheduledReview.cs", "Install-Actualizador.ps1",
                "LEEME-portable.txt", "Pruebas2011.cs", "PruebasBase.cs",
            };
            var revisar = new List<string>();
            foreach (string dir in new[] { "src", "deploy", "build", "docs", "tests" })
            {
                string d = Path.Combine(raiz, dir);
                if (!Directory.Exists(d)) continue;
                foreach (string f in Directory.GetFiles(d, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(raiz.Length);
                    if (rel.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) || rel.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".cs" && ext != ".ps1" && ext != ".cmd" && ext != ".txt" && ext != ".md" && ext != ".json" && ext != ".csproj" && ext != ".manifest" && ext != ".sh") continue;
                    revisar.Add(f);
                }
            }
            foreach (string f in Directory.GetFiles(raiz)) revisar.Add(f);
            var sobran = new List<string>();
            foreach (string f in revisar)
            {
                string nombre = Path.GetFileName(f);
                if (nombre.Equals("CHANGELOG.md", StringComparison.OrdinalIgnoreCase)) continue;   // la historia queda como fue
                if (nombre.Contains("SuperProgram")) { sobran.Add(nombre + " (nombre del archivo)"); continue; }
                if (permitidos.Contains(nombre)) continue;
                if (Path.GetExtension(f).Equals(".ico", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.ReadAllText(f).Contains("SuperProgram")) sobran.Add(nombre);
            }
            Check(sobran.Count == 0, "no queda «SuperProgram» fuera de lo que pasa de una versión a otra: " + string.Join(", ", sobran));
            Check(File.Exists(Path.Combine(raiz, "deploy", "Install-Actualizador.ps1")) && File.Exists(Path.Combine(raiz, "deploy", "Uninstall-Actualizador.ps1")),
                  "instalador y desinstalador con el nombre nuevo");

            // El instalador pasa una instalación de SuperProgram a Actualizador.
            string inst = Leer(raiz, "deploy/Install-Actualizador.ps1");
            Check(inst.Contains("$Destino = (Join-Path $env:ProgramFiles 'Actualizador')"), "se instala en Program Files\\Actualizador");
            Check(inst.Contains("$Viejo = 'SuperProgram'") && inst.Contains("$ViejosProcesos -contains $_.Name"), "cierra SuperProgram si está abierto");
            Check(inst.Contains("RegisterTask($nuevoNombre, $nuevoXml, 6, $null, $null, 3)") && inst.Contains("DeleteTask($t.Name, 0)"),
                  "las revisiones programadas viejas se vuelven a crear con el nombre nuevo y se quitan las viejas");
            Check(inst.Contains("foreach ($k in $ViejasClaves)"), "quita SuperProgram de «Aplicaciones instaladas»");
            Check(inst.Contains("$ViejosEjecutables -contains [IO.Path]::GetFileName($objetivo)"), "quita los accesos directos viejos");
            Check(inst.Contains("Rename-Item -LiteralPath $vieja -NewName 'Actualizador'"), "las opciones, el historial y las ignoradas de cada usuario pasan a Actualizador");
            Check(inst.Contains("Move-Item -LiteralPath $i.FullName -Destination $a"), "la política y el registro de cambios pasan a %ProgramData%\\Actualizador");
            int paso4 = inst.IndexOf("Write-Paso '4 de 6", StringComparison.Ordinal), migra = inst.IndexOf("Pasando de SuperProgram", StringComparison.Ordinal);
            Check(paso4 > 0 && migra > paso4, "lo viejo se quita recién cuando Actualizador abre bien");
            Check(inst.IndexOf("Pasando de SuperProgram", StringComparison.Ordinal) < inst.IndexOf("Datos de cada usuario: los de 1.x", StringComparison.Ordinal),
                  "los datos de 1.x con el nombre viejo se pasan antes de revisarlos (y se borran como siempre)");
            Check(inst.Contains("(Split-Path -Leaf $viejaCarpeta) -eq $Viejo"), "sólo se borra una carpeta vieja llamada SuperProgram");

            // El icono nuevo: el mismo en los tres lugares, con todos los tamaños de Windows.
            byte[] ico = File.ReadAllBytes(Path.Combine(raiz, "res", "app.ico"));
            foreach (string p in new[] { "src/Actualizador.App/app.ico", "src/Actualizador.Aviso/app.ico" })
                Check(ico.SequenceEqual(File.ReadAllBytes(Path.Combine(raiz, p.Replace('/', Path.DirectorySeparatorChar)))), "mismo icono en " + p);
            Check(ico.Length > 6 && ico[0] == 0 && ico[2] == 1, "app.ico es un icono");
            int n = ico.Length > 6 ? ico[4] | (ico[5] << 8) : 0;
            var tam = new HashSet<int>();
            for (int i = 0; i < n && 6 + i * 16 < ico.Length; i++) tam.Add(ico[6 + i * 16] == 0 ? 256 : ico[6 + i * 16]);
            foreach (int t in new[] { 16, 24, 32, 48, 256 }) Check(tam.Contains(t), "icono de " + t + "×" + t);
        }
            static void TestAntivirus2011()
        {
            string raiz = Raiz();
            Check(raiz != null, "se encuentra Actualizador.sln");
            if (raiz == null) return;
            // Lo que la protección de identidad de los antivirus asocia con los ladrones de datos. Avast cortaba Actualizador
            // al terminar cada búsqueda (IDP.Generic, escudo de comportamiento): descifraba con DPAPI la clave del registro, y
            // quedaban sin usar consultas del auditor (cuentas, grupos, política de contraseñas, Wi-Fi, puertos).
            var prohibido = new[]
            {
                "CryptUnprotectData", "CryptProtectData", "ProtectedData", "\"netapi32.dll\"", "\"wlanapi.dll\"", "\"iphlpapi.dll\"",
                "\"vaultcli.dll\"", "CredEnumerate", "CredRead", "NetUserEnum", "NetUserModalsGet", "WlanGetProfile", "GetExtendedTcpTable",
                "Login Data", "\\User Data", "Local State", "logins.json", "key4.db", "Wlansvc", "remote-debugging-port",
            };
            var hallados = new List<string>();
            foreach (string f in Directory.GetFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                string codigo = string.Join("\n", File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                foreach (string p in prohibido) if (codigo.Contains(p)) hallados.Add(Path.GetFileName(f) + ": " + p);
            }
            Check(hallados.Count == 0, "el código no usa nada de lo que vigila la protección de identidad: " + string.Join("; ", hallados));
            Check(!File.Exists(Path.Combine(raiz, "src", "Actualizador.Core", "Security", "Dpapi.cs")), "ya no está Dpapi.cs");
        }
    }
}
