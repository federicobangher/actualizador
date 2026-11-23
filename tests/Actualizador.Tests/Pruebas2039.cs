// Pruebas de la 2.0.39. Pedido del usuario (23/11/2026): «la idea es que pese lo menos posible es el portable».
using System;
using System.IO;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        /// <summary>La portable se arma sin la consola y sin las bibliotecas de WPF, y se prueba abriendo la ventana.</summary>
        static void TestPortableLiviana2039()
        {
            string raiz = Raiz();
            if (raiz == null) return;
            string emp = File.ReadAllText(Path.Combine(raiz, "build", "EmpaquetarPortable.ps1"), Encoding.UTF8);
            Check(!emp.Contains("'Actualizador.Cli'") && !emp.Contains("'Actualizador.Cli.exe'"), "sin la consola");
            Check(emp.Contains("-p:QuitarNativosWpf=true"), "sin las bibliotecas de WPF");
            Check(emp.Contains("Start-Process -FilePath $ventana") && emp.Contains("-Wait -PassThru"), "abre la ventana armada y espera que termine");
            Check(emp.Contains("No se borran a mano"), "si quedaron bibliotecas de WPF, no se borran a mano (.NET las esperaría)");
            string csproj = File.ReadAllText(Path.Combine(raiz, "src", "Actualizador.App", "Actualizador.App.csproj"), Encoding.UTF8);
            Check(csproj.Contains("AfterTargets=\"ResolveRuntimePackAssets\"") && csproj.Contains("Condition=\"'$(QuitarNativosWpf)' == 'true'\""),
                  "se quitan al publicar, sólo en la portable");
            foreach (string n in new[] { "wpfgfx_cor3", "PresentationNative_cor3", "PenImc_cor3", "D3DCompiler_47_cor3", "vcruntime140_cor3" })
                Check(csproj.Contains("'" + n + "'"), "quita " + n);
            string prog = File.ReadAllText(Path.Combine(raiz, "src", "Actualizador.App", "Program.cs"), Encoding.UTF8);
            Check(prog.Contains("\"--comprobar-arranque\"") && prog.Contains("RemoveObsolete"), "la ventana: prueba de arranque y limpieza de lo viejo");
            Check(!File.ReadAllText(Path.Combine(raiz, "deploy", "LEEME-portable.txt"), Encoding.UTF8).Contains("Actualizador.Cli buscar"), "el LEEME ya no ofrece la consola");
        }

        /// <summary>Al abrir, la portable borra lo que trajeron versiones anteriores y ella ya no usa; nada más.</summary>
        static void TestPortableBorraLoViejo2039()
        {
            string dir = Path.Combine(Path.GetTempPath(), "act2039-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Func<string, string> crear = delegate(string n) { string p = Path.Combine(dir, n); File.WriteAllText(p, "x"); return p; };
                foreach (string n in new[] { "Actualizador.exe", "Actualizador.portable", "Actualizador.Cli.exe", "wpfgfx_cor3.dll", "PenImc_cor3.dll", "otro.dll", "LEEME.txt" }) crear(n);
                Directory.CreateDirectory(Path.Combine(dir, "Datos"));
                File.WriteAllText(Path.Combine(dir, "Datos", "opciones.json"), "{}");

                Check(PortableLayout.RemoveObsolete(dir).Count == 0 && File.Exists(Path.Combine(dir, "Actualizador.Cli.exe")), "sin SHA256SUMS.txt no se borra nada");
                File.WriteAllText(Path.Combine(dir, "SHA256SUMS.txt"), new string('a', 64) + "  LEEME.txt\r\n");
                Check(PortableLayout.RemoveObsolete(dir).Count == 0, "un SHA256SUMS.txt que no es el de la portable (sin Actualizador.exe): no se borra nada");

                File.WriteAllText(Path.Combine(dir, "SHA256SUMS.txt"),
                    new string('a', 64) + "  Actualizador.exe\r\n" + new string('b', 64) + "  Actualizador.portable\r\n" + new string('c', 64) + "  PenImc_cor3.dll\r\n");
                var quitados = PortableLayout.RemoveObsolete(dir);
                Check(quitados.Contains("Actualizador.Cli.exe") && quitados.Contains("wpfgfx_cor3.dll") && quitados.Count == 2, "borra la consola y lo de WPF que no trae: " + string.Join(",", quitados.ToArray()));
                Check(File.Exists(Path.Combine(dir, "PenImc_cor3.dll")), "lo que esta versión sí trae (figura en SHA256SUMS.txt) queda");
                Check(File.Exists(Path.Combine(dir, "otro.dll")) && File.Exists(Path.Combine(dir, "Actualizador.exe")) && File.Exists(Path.Combine(dir, "Datos", "opciones.json")),
                      "no toca otros archivos ni «Datos»");
                Check(PortableLayout.RemoveObsolete(dir).Count == 0, "la segunda vez no hay nada que borrar");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestVersion2039()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.39") >= 0, "Actualizador 2.0.39 o posterior: " + AppInfo.Version);
        }
    }
}
