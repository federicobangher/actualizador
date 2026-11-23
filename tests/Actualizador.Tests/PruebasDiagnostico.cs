// Pruebas del paquete de diagnóstico.
using Actualizador.Core;
using Actualizador.Updates;
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

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static void TestPaqueteDeDiagnostico()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sp-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string logs = Path.Combine(dir, "logs");
                Directory.CreateDirectory(logs);
                File.WriteAllText(Path.Combine(logs, "gui_20261014.log"), "INFO winget upgrade --id \"Baidu.TeraBox\"\nINFO token=abc123secreto\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(logs, "winget-upgrade.txt"), TablaWinget(Cab167, new[] { "TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5", "winget" }), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(logs, "msi-Ejemplo.log"), "=== Verbose logging started ===\r\nMSI (s) Producto: 1603\r\n", Encoding.Unicode);
                File.WriteAllText(Path.Combine(logs, "otra-cosa.dat"), "no va");
                var c = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
                c.Status = UpdateStatus.Fallida;
                c.Result = WingetProvider.NothingChangedText("1.47.0", "TeraBox.exe", false);
                c.Detail = "Versión más nueva en el repositorio winget.";
                string zip = Path.Combine(dir, DiagnosticPackage.DefaultFileName(new DateTime(2026, 10, 14, 12, 30, 0)));
                Eq("Actualizador-diagnostico-20261014-1230.zip", Path.GetFileName(zip), "nombre del archivo");
                int n = DiagnosticPackage.Create(zip, new List<UpdateCandidate> { c }, new List<string> { "Programas verificados con winget 1.29.380." }, "Sistema: Windows 11 26H2", logs);
                Eq(3, n, "tres registros (.log y .txt; el .dat no)");
                using (var z = ZipFile.OpenRead(zip))
                {
                    var nombres = new List<string>();
                    foreach (var e in z.Entries) nombres.Add(e.FullName);
                    Check(nombres.Contains("actualizaciones.txt") && nombres.Contains("registros/winget-upgrade.txt") && nombres.Contains("registros/gui_20261014.log") &&
                          nombres.Contains("registros/msi-Ejemplo.log") && !nombres.Contains("registros/otra-cosa.dat"), "contenido: " + string.Join(", ", nombres.ToArray()));
                    string lista = Leer(z, "actualizaciones.txt");
                    Check(lista.Contains("TeraBox") && lista.Contains("Baidu.TeraBox") && lista.Contains("no cambió nada") && lista.Contains("1.49.5") && lista.Contains("Windows 11 26H2"),
                          "la fila completa, con el resultado y el sistema");
                    string log = Leer(z, "registros/gui_20261014.log");
                    Check(log.Contains("Baidu.TeraBox") && !log.Contains("abc123secreto"), "registro sin secretos: " + log.Replace("\n", " | "));
                    Check(Leer(z, "registros/msi-Ejemplo.log").Contains("1603"), "registro de Windows Installer (UTF-16) legible");
                }
                Check(!File.Exists(zip + ".tmp"), "sin temporales");
                // Un registro enorme: sólo el final.
                File.WriteAllText(Path.Combine(logs, "grande.log"), new string('x', DiagnosticPackage.MaxFileBytes + 1000) + "FINAL", new UTF8Encoding(false));
                string tail = DiagnosticPackage.ReadTail(Path.Combine(logs, "grande.log"), DiagnosticPackage.MaxFileBytes);
                Check(tail.EndsWith("FINAL") && tail.StartsWith("[… se omitió") && tail.Length < DiagnosticPackage.MaxFileBytes + 100, "de un registro enorme, el final");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static string Leer(ZipArchive z, string name)
        {
            var e = z.GetEntry(name);
            if (e == null) return "";
            using (var r = new StreamReader(e.Open(), Encoding.UTF8)) return r.ReadToEnd();
        }
    }
}
