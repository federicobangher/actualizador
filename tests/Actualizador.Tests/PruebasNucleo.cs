// Núcleo: versiones, formato, JSON, argumentos y procesos.
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

        static void TestProcesosSinColgarse()
        {
            if (Path.DirectorySeparatorChar != '/' || !File.Exists("/bin/sh")) { Check(true, "(sólo en Linux/Mono)"); return; }
            // Un proceso que deja un hijo con su salida heredada (como un instalador que abre el programa al
            // terminar): antes WaitForExit() sin plazo esperaba al hijo, para siempre.
            int antes = ProcessRunner.OutputGraceMs;
            ProcessRunner.OutputGraceMs = 1500;
            try
            {
                var sw = Stopwatch.StartNew();
                var r = ProcessRunner.Run("/bin/sh", "-c \"sleep 25 & echo listo\"", 20000);
                sw.Stop();
                Check(r.Started && !r.TimedOut && r.ExitCode == 0, "el proceso terminó bien: " + r.Describe());
                Check(sw.ElapsedMilliseconds < 12000, "no espera al hijo que heredó la salida (" + sw.ElapsedMilliseconds + " ms)");
                Check(r.Out.Contains("listo"), "y la salida que llegó se conserva");
                sw = Stopwatch.StartNew();
                var ri = ProcessRunner.RunInstaller("/bin/sh", "-c \"sleep 25 & exit 3\"", 20000, null);
                sw.Stop();
                Check(ri.Started && ri.ExitCode == 3 && sw.ElapsedMilliseconds < 5000, "instalador sin tuberías: vuelve al terminar, con su código (" + sw.ElapsedMilliseconds + " ms, " + ri.Describe() + ")");
                var rt = ProcessRunner.RunInstaller("/bin/sh", "-c \"sleep 30\"", 1000, null);
                Check(rt.Started && rt.TimedOut, "el plazo se respeta");
                var rn = ProcessRunner.RunInstaller("/no/existe.exe", "", 1000, null);
                Check(!rn.Started && !string.IsNullOrEmpty(rn.StartError), "si no existe, se informa");
                var normal = ProcessRunner.Run("/bin/sh", "-c \"echo a; echo b 1>&2; exit 4\"", 10000);
                Check(normal.ExitCode == 4 && normal.Out.Contains("a") && normal.Err.Contains("b"), "salida y errores completos en un proceso normal");
            }
            finally { ProcessRunner.OutputGraceMs = antes; }
        }

        static void TestRegistroDeFallasSilenciosas168()
        {
            var vistas = new List<string>();
            Action<string> h = delegate(string l) { vistas.Add(l); };
            Log.LineWritten += h;
            try
            {
                for (int i = 0; i < 5; i++) Log.Quiet("Prueba 1.6.8 de fallas silenciosas", new InvalidOperationException("x"));
                Log.Info("línea común de prueba");
            }
            finally { Log.LineWritten -= h; }
            Check(!vistas.Exists(delegate(string l) { return l.Contains("DETAL"); }), "las fallas silenciosas no van al panel de la ventana (sólo al archivo)");
            Check(vistas.Exists(delegate(string l) { return l.Contains("línea común de prueba"); }), "las demás líneas sí");
            // Quedan en el código: cada «catch { }» que escondía un dato del diagnóstico ahora lo anota.
            int anotadas = 0;
            foreach (string f in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(ProjectFile("Actualizador.sln")), "src"), "*.cs", SearchOption.AllDirectories))
                anotadas += System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"Log\.Quiet\(").Count;
            Check(anotadas >= 30, "fallas que antes se tragaban y ahora quedan en el registro: " + anotadas);
        }

        static void TestVersions()
        {
            Eq(1, VersionUtil.Compare("129.0.2792.52", "128.0.2739.67"), "mayor");
            Eq(-1, VersionUtil.Compare("24.07", "24.09"), "menor");
            Eq(0, VersionUtil.Compare("1.2", "1.2.0.0"), "ceros a la derecha");
            Eq(1, VersionUtil.Compare("v8.7.1", "8.7"), "prefijo v");
            Eq(1, VersionUtil.Compare("24.09 (x64)", "24.08"), "texto adicional");
            Eq(-1, VersionUtil.Compare("1.2.3-beta", "1.2.3"), "prerelease menor que final");
            Eq(1, VersionUtil.Compare("2.47.1.windows.2", "2.46.0"), "sufijo git");
            Eq(-1, VersionUtil.Compare("< 129.0.2", "130.0"), "prefijo <");
            Check(VersionUtil.Compare("Unknown", "1.0") == null, "desconocida → null");
            Check(VersionUtil.Compare("", "1.0") == null, "vacía → null");
            Check(VersionUtil.IsNewer("10.0", "9.99"), "10 > 9.99 numérico, no léxico");
            Eq("1.2.3", VersionUtil.Normalize("v1.2.3 build"), "normalizar");
            Eq("10.0.22631.0", VersionUtil.Decode64(VersionUtil.Encode64(10, 0, 22631, 0)), "UInt64 ida y vuelta");
        }

        /// <summary>
        /// Canario de compilación: ejercita cada sobrecarga con los tipos que usa el código real.
        /// El compilador de Microsoft rechaza llamadas ambiguas que Mono resuelve por su cuenta
        /// (CS0121), así que esto las detecta al compilar, no en la máquina del usuario.
        /// </summary>
        static void TestSobrecargas()
        {
            long entero = 1536;
            long? nulable = 1536;
            long? vacio = null;
            double real = 1536.0;
            int chico = 1536;
            Eq(Fmt.Bytes(real), Fmt.Bytes(entero), "Bytes(long) == Bytes(double)");
            Eq(Fmt.Bytes(entero), Fmt.Bytes(nulable), "Bytes(long?) con valor");
            Eq("", Fmt.Bytes(vacio), "Bytes(long?) vacío");
            Eq(Fmt.Bytes(entero), Fmt.Bytes(chico), "Bytes(int)");

            DateTime fecha = new DateTime(2026, 9, 20);
            DateTime? fechaN = fecha;
            DateTime? fechaV = null;
            Eq(Fmt.Date(fecha), Fmt.Date(fechaN), "Date(DateTime?) con valor");
            Eq("", Fmt.Date(fechaV), "Date(DateTime?) vacío");

            Eq("0x0000002A", Fmt.Hex(42), "Hex(int)");
            Eq("0x0000002A", Fmt.Hex(42L), "Hex(long)");
            Eq("0x8A150006", Fmt.Hex(unchecked((int)0x8A150006)), "Hex(int) negativo");
        }

        static void TestFmt()
        {
            Eq("'=HYPERLINK(\"x\")", Fmt.Csv("=HYPERLINK(\"x\")", ";").Trim('"').Replace("\"\"", "\""), "anti inyección de fórmulas");
            Eq("-5", Fmt.Csv("-5", ";"), "número negativo intacto");
            Eq("\"a;b\"", Fmt.Csv("a;b", ";"), "comillas por separador");
            Eq("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;", Fmt.Html("<script>alert('x')</script>"), "escape HTML");
            Eq("Sí", Fmt.Cell(true), "bool");
            Eq(new DateTime(2023, 1, 15), Fmt.ParseLooseDate("20230115"), "fecha del registro");
            Eq(new DateTime(2023, 1, 15, 10, 30, 0), Fmt.ParseLooseDate("20230115103000.000000+000"), "fecha CIM");
            Check(Fmt.ParseLooseDate("basura") == null, "fecha inválida");
            Check(Fmt.Bytes(1536).StartsWith("1"), "bytes");
            Eq("a…", Fmt.Truncate("abc", 2), "truncar");
            Eq("a_b", Fmt.SafeFileName("a" + Path.GetInvalidFileNameChars()[0] + "b"), "nombre de archivo seguro");
        }

        static void TestJson()
        {
            string text = "{\"a\":[1,2.5,\"x\\u00e9\\n\"],\"b\":{\"c\":true,\"d\":null},\"Versions\":[{\"Version\":\"1.2\"}]}";
            object o = Json.Parse(text);
            Eq("1.2", Json.Str(o, "versions[0].version"), "ruta tolerante a mayúsculas");
            Eq("true", Json.Str(o, "b.c"), "bool");
            Eq("xé\n", Json.Str(o, "a[2]"), "escape unicode");
            string round = Json.Serialize(o, false);
            Eq(round, Json.Serialize(Json.Parse(round), false), "ida y vuelta estable");
            Eq("\"\\u2028<\"", Json.Serialize("\u2028<", false), "escape de separadores de línea");
            bool threw = false;
            try { Json.Parse("{\"a\":1} x"); } catch (FormatException) { threw = true; }
            Check(threw, "contenido extra rechazado");
            var deep = new StringBuilder();
            for (int i = 0; i < 300; i++) deep.Append('[');
            threw = false;
            try { Json.Parse(deep.ToString()); } catch (FormatException) { threw = true; }
            Check(threw, "límite de anidamiento");
            Check(Json.Parse("\uFEFF[]") is List<object>, "BOM");
        }

        static OsInfo Os(int major, int minor, int build, string edition, bool server)
        {
            var os = new OsInfo();
            os.IsWindows = true; os.Major = major; os.Minor = minor; os.Build = build; os.EditionId = edition; os.IsServer = server;
            return os;
        }

        /// <summary>Decodificador de referencia con las reglas de CommandLineToArgvW (un solo argumento).</summary>
        static string ParseArg(string cmd)
        {
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < cmd.Length; i++)
            {
                int bs = 0;
                while (i < cmd.Length && cmd[i] == '\\') { bs++; i++; }
                if (i < cmd.Length && cmd[i] == '"')
                {
                    sb.Append('\\', bs / 2);
                    if (bs % 2 == 1) sb.Append('"'); else inQuotes = !inQuotes;
                }
                else
                {
                    sb.Append('\\', bs);
                    if (i < cmd.Length) sb.Append(cmd[i]);
                }
            }
            return sb.ToString();
        }

        /// <summary>Busca un archivo del proyecto subiendo desde la carpeta de muestras.</summary>
        static string ProjectFile(string relative)
        {
            var dir = new DirectoryInfo(samples);
            for (int i = 0; i < 9 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        static void TestQuoteArg()
        {
            string[] cases =
            {
                "", "simple", "con espacios", "a\"b", "a\\\"b", "termina\\", "root\\cimv2\\mdm\\dmmap",
                "$r=@(Get-PhysicalDisk | ForEach-Object { [pscustomobject]@{I=\"$($_.DeviceId)\"} }); ConvertTo-Json $r",
                "'root\\cimv2\\mdm\\dmmap' -ClassName 'X'; \"RC=$($r.ReturnValue)\"", "\\\\servidor\\c$\\", "\"\""
            };
            foreach (string c in cases)
                Eq(c, ParseArg(Actualizador.Core.ProcessRunner.QuoteArg(c)), "ida y vuelta: " + c);
            Check(!Actualizador.Core.ProcessRunner.QuoteArg("x").Contains("EncodedCommand"), "sin codificación");
        }

    }
}
