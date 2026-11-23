// Pruebas de la 2.0.10: versión portable (no se instala) además de la que se instala.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static void TestPortable2010()
        {
            // --- Dónde guarda las cosas
            Eq(Path.Combine("E:" + Path.DirectorySeparatorChar + "SP", "Datos"), PortableLayout.DataDirectory("E:" + Path.DirectorySeparatorChar + "SP"), "portable: Datos al lado del programa");
            string datos = Path.Combine("x", "Datos");
            Eq(Path.Combine(Path.Combine(datos, "equipos"), "PC-VENTAS"), PortableLayout.MachineDirectory(datos, "PC-VENTAS"), "portable: lo de cada equipo en Datos/equipos/NOMBRE");
            Eq("PC_1", PortableLayout.SafeMachineName("PC:1"), "nombre de equipo sin caracteres prohibidos");
            Eq("equipo", PortableLayout.SafeMachineName(""), "nombre de equipo vacío");
            Eq("equipo", PortableLayout.SafeMachineName(".."), "nombre de equipo que sería «..»");
            Eq("equipo", PortableLayout.SafeMachineName("\\/"), "nombre de equipo que sería sólo separadores");
            Check(PortableLayout.SafeMachineName(new string('A', 200)).Length <= 64, "nombre de equipo acotado");
            Check(!PortableLayout.SafeMachineName("a\\..\\b").Contains("\\"), "el nombre del equipo no sale de Datos/equipos");

            string tmp = Path.Combine(Path.GetTempPath(), "sp-portable-" + Guid.NewGuid().ToString("N"));
            try
            {
                string d = Path.Combine(tmp, "Datos");
                Eq(null, PortableLayout.CannotWrite(d), "se puede escribir en una carpeta normal (y la crea)");
                Check(Directory.Exists(d) && Directory.GetFiles(d).Length == 0, "la prueba de escritura no deja archivos");
                string archivo = Path.Combine(tmp, "archivo");
                File.WriteAllText(archivo, "x");
                Check(PortableLayout.CannotWrite(Path.Combine(archivo, "Datos")) != null, "no se puede escribir debajo de un archivo: dice por qué (y se usan los datos del usuario)");

                // Enlace en Datos (elevado sería escribir en otro lado): se controla desde la carpeta del programa.
                string prog = Path.Combine(tmp, "prog");
                string otro = Path.Combine(tmp, "otro");
                Directory.CreateDirectory(prog); Directory.CreateDirectory(otro);
                bool enlace = false;
                try { Directory.CreateSymbolicLink(Path.Combine(prog, "Datos"), otro); enlace = true; } catch { }
                if (enlace && (File.GetAttributes(Path.Combine(prog, "Datos")) & FileAttributes.ReparsePoint) != 0)
                    Check(SafePaths.HasReparsePoint(prog, Path.Combine(Path.Combine(prog, "Datos"), "logs")), "un enlace en Datos se detecta desde la carpeta del programa");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }

            // --- Raíz desde la que se controlan los enlaces al escribir elevado
            string local = @"C:\Users\ana\AppData\Local";
            Eq(@"E:\Actualizador", SafePaths.ElevatedWriteRoot(@"E:\Actualizador\Datos\equipos\PC\logs", local, @"E:\Actualizador"), "portable: desde la carpeta del programa");
            Eq(local, SafePaths.ElevatedWriteRoot(local + @"\Actualizador\logs", local, null), "instalada: desde %LOCALAPPDATA%");
            Eq(local, SafePaths.ElevatedWriteRoot(local + @"\Actualizador", local, @"E:\Actualizador"), "portable sin poder escribir en su carpeta: la del usuario");
            Check(SafePaths.IsInside(@"E:\Actualizador\Datos", @"E:\Actualizador\"), "IsInside con barra final");
            Check(!SafePaths.IsInside(@"E:\Actualizador2\Datos", @"E:\Actualizador"), "IsInside no confunde Actualizador2 con Actualizador");
            Check(!SafePaths.IsInside(@"E:\Actualizador\..\Windows", @"E:\Actualizador"), "IsInside resuelve «..»");
            Check(SafePaths.IsInside(@"\\srv\ti\Actualizador\Datos", @"\\srv\ti\Actualizador"), "IsInside con rutas de red");
            Check(!SafePaths.IsInside(@"\\srv\ti\Actualizador\..\x", @"\\srv\ti\Actualizador"), "IsInside con «..» en rutas de red");

            // --- Esta copia (la de las pruebas) no es portable
            Check(!AppInfo.IsPortable, "sin Actualizador.portable al lado no es portable");
            Eq(AppInfo.DataDirectory, AppInfo.MachineDataDirectory, "la instalada guarda todo en la misma carpeta");
            Eq(AppInfo.UserDataDirectory, AppInfo.DataDirectory, "la instalada usa %LOCALAPPDATA%\\Actualizador");
            Check(!AppInfo.Title.Contains("portable"), "el título de la instalada no dice portable");
            Eq(null, AppInfo.PortableNote, "la instalada no tiene nota de portable");
            Check(UpdateHistory.DefaultPath.StartsWith(AppInfo.MachineDataDirectory) && IgnoreList.DefaultPath.StartsWith(AppInfo.MachineDataDirectory),
                  "historial e ignoradas: de cada equipo");
            Check(AppInfo.LogDirectory.StartsWith(AppInfo.MachineDataDirectory), "registros: de cada equipo");

            // --- La portable no programa revisiones
            Eq(ScheduledReview.PortableReason, ScheduledReview.Unavailable(null, true), "portable: sin revisión programada (aun sin saber el sistema)");
            Check(ScheduledReview.Unavailable(null, false) != ScheduledReview.PortableReason, "la instalada sigue con sus motivos de siempre");

            // --- Versiones nuevas: también las entregas portables
            string elegido;
            var nombres = new[] { @"D:\E\Actualizador-2.0.10.7z", @"D:\E\Actualizador-2.0.10-portable.7z", @"D:\E\Actualizador-2.0.9.zip", @"D:\E\Actualizador-2.0.10-portable.7z.sha256" };
            Eq("2.0.10", SelfUpdate.NewestDelivery(nombres, true, out elegido), "portable: reconoce Actualizador-x.y.z-portable.7z");
            Eq(@"D:\E\Actualizador-2.0.10-portable.7z", elegido, "la portable elige la entrega portable");
            Eq("2.0.10", SelfUpdate.NewestDelivery(nombres, false, out elegido), "instalada: misma versión");
            Eq(@"D:\E\Actualizador-2.0.10.7z", elegido, "la instalada elige la entrega que se instala");
            Eq("2.0.11", SelfUpdate.NewestDelivery(new[] { "Actualizador-2.0.10-portable.7z", "Actualizador-2.0.11.7z" }, true, out elegido), "una versión más nueva gana aunque sea de la otra entrega");
            Eq("Actualizador-2.0.11.7z", elegido, "y se avisa con ella");
            Eq("2.1.0", SelfUpdate.NewestDelivery(new[] { "Actualizador_2.1.0_portable" }, true, out elegido), "carpeta de la portable con guiones bajos");
            Check(SelfUpdate.HowToUpdate(false, "x").Contains("Instalar.cmd"), "instalada: se actualiza con Instalar.cmd");
            Check(SelfUpdate.HowToUpdate(true, "Actualizador-2.1.0-portable.7z").Contains("en lugar de esta carpeta") &&
                  !SelfUpdate.HowToUpdate(true, "Actualizador-2.1.0-portable.7z").Contains("Instalar.cmd"), "portable: se reemplaza la carpeta (no se instala)");
            Check(SelfUpdate.HowToUpdate(true, "Actualizador-2.1.0.7z").Contains("Portable.cmd"), "portable con sólo la entrega común: cómo armar la portable");
        }

        static void TestEmpaquetadoPortable2010()
        {
            string raiz = Raiz();
            Check(raiz != null, "se encuentra Actualizador.sln");
            if (raiz == null) return;

            string s = "build/EmpaquetarPortable.ps1";
            byte[] b = File.ReadAllBytes(Path.Combine(raiz, s));
            Check(b.Length > 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF, s + ": UTF-8 con BOM");
            string t = Encoding.UTF8.GetString(b);
            Check(t.IndexOfAny(new[] { '\u201C', '\u201D', '\u2018', '\u2019' }) < 0, s + ": sin comillas tipográficas");
            Check(Balanceado(t), s + ": llaves y paréntesis balanceados");
            Check(t.Contains("Set-StrictMode") && t.Contains("$ErrorActionPreference = 'Stop'"), s + ": modo estricto y errores que detienen");
            Check(!Regex.IsMatch(t, @"\?\?|\?\.\w"), s + ": compatible con Windows PowerShell 5.1");
            Check(!t.Contains("\n") || !Regex.IsMatch(t, "[^\r]\n"), s + ": CRLF");
            Check(t.Contains("'-f', 'net8.0-windows'") && t.Contains("'-r', 'win-x64'") && t.Contains("'--self-contained:true'"), s + ": .NET adentro, para Windows x64");
            Check(t.Contains("-p:PublishSingleFile=true") && t.Contains("-p:IncludeNativeLibrariesForSelfExtract=false"), s + ": un solo .exe, sin extraer nada a %TEMP%");
            Check(Regex.IsMatch(t, @"foreach \(\$p in @\('Actualizador\.App', 'Actualizador\.Cli'\)\)"), s + ": ventana y consola");
            Check(!t.Contains("'Actualizador.Aviso'"), s + ": sin el aviso de la revisión programada");
            Check(t.Contains("'Actualizador.portable'") && AppInfo.PortableMarker == "Actualizador.portable", s + ": pone el marcador que lee AppInfo");
            Check(t.Contains("deploy\\LEEME-portable.txt") && File.Exists(Path.Combine(raiz, "deploy", "LEEME-portable.txt")), s + ": lleva el LEEME de la portable");
            Check(t.Contains("& $consola 'version'") && t.Contains("(portable)"), s + ": prueba la consola armada antes de entregar");
            Check(t.Contains("requireAdministrator"), s + ": comprueba que la ventana pida administrador");
            Check(t.Contains("Remove-Item -LiteralPath $datos"), s + ": lo que deja la prueba no va en la entrega");
            Check(t.Contains("SHA256SUMS.txt") && t.Contains("Find-7Zip") && t.Contains("$nombre.7z"), s + ": hashes y .7z");
            Check(Regex.IsMatch("Actualizador-" + AppInfo.Version + "-portable.7z", @"^Actualizador-\d+\.\d+\.\d+-portable\.7z$") &&
                  t.Contains("$nombre = \"Actualizador-$version-portable\""), s + ": nombre que reconoce el aviso de versiones nuevas");
            Check(Leer(raiz, "build/Comun.ps1").Contains("function Find-7Zip"), "Comun.ps1: Find-7Zip");

            byte[] c = File.ReadAllBytes(Path.Combine(raiz, "Portable.cmd"));
            bool ascii = true, crlf = true;
            for (int i = 0; i < c.Length; i++)
            {
                if (c[i] > 127) ascii = false;
                if (c[i] == '\n' && (i == 0 || c[i - 1] != '\r')) crlf = false;
            }
            Check(ascii && crlf, "Portable.cmd: ASCII con CRLF (cmd.exe)");
            string cmd = Encoding.ASCII.GetString(c);
            Check(cmd.Contains("build\\EmpaquetarPortable.ps1") && cmd.Contains("%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"), "Portable.cmd arma la portable con PowerShell por ruta completa");

            byte[] l = File.ReadAllBytes(Path.Combine(raiz, "deploy", "LEEME-portable.txt"));
            Check(l.Length > 3 && l[0] == 0xEF && l[1] == 0xBB && l[2] == 0xBF, "LEEME-portable.txt con BOM (el Bloc de notas viejo)");
            string leeme = Encoding.UTF8.GetString(l);
            Check(leeme.Contains("Datos\\equipos\\") && leeme.Contains("No programa revisiones") && leeme.Contains("SEGURIDAD"), "el LEEME explica datos, límites y seguridad");

            foreach (string p in new[] { "src/Actualizador.App/Actualizador.App.csproj", "src/Actualizador.Cli/Actualizador.Cli.csproj" })
                Check(Regex.IsMatch(Leer(raiz, p), @"fuentes-oficiales\.json""[^>]*ExcludeFromSingleFile=""true"""), p + ": el catálogo queda al lado del .exe");
            Check(Leer(raiz, "deploy/Install-Actualizador.ps1").Contains("'Actualizador.portable'"), "el instalador quita una marca de portable que se haya colado");
            string layout = Leer(raiz, "src/Actualizador.App/MainForm.Layout.cs");
            Check(Regex.IsMatch(layout, @"AppInfo\.IsPortable \?[\s\S]{0,120}MenuItem\(""Desinstalar Actualizador…"""), "la portable no ofrece desinstalarse");
            Check(Leer(raiz, "src/Actualizador.App/MainForm.Export.cs").Contains("Versión portable"), "«Acerca de» dice que es portable y dónde guarda");
            Check(Leer(raiz, "README.md").Contains("Portable.cmd"), "README: cómo se arma la portable");
        }
    }
}
