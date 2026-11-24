// Pruebas de la 2.0.38. Relevamiento pedido por el usuario (23/11/2026) antes de presentar Actualizador en el trabajo.
using System;
using System.IO;
using System.Text;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        /// <summary>La última consulta de versión nueva la guardó otra versión (o la otra clase): se vuelve a consultar.</summary>
        static void TestConsultaDeOtraVersion2038()
        {
            string dir = Path.Combine(Path.GetTempPath(), "act2038-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string f = Path.Combine(dir, "version_nueva.json");
                var r = new SelfUpdate.Result { Latest = "2.0.40", Where = "w", Newer = true, Checked = DateTime.Now, AssetName = "Actualizador-2.0.40.zip",
                                                AssetUrl = "https://github.com/x/y/releases/download/v2.0.40/Actualizador-2.0.40.zip", AssetSha256 = new string('a', 64) };
                SelfUpdate.Save(f, r, "x/y");
                Check(SelfUpdate.Load(f, "x/y", "2.0.30") != null, "guardada por esta versión: se reusa (una consulta por hora)");
                string texto = File.ReadAllText(f, Encoding.UTF8);
                File.WriteAllText(f, texto.Replace("\"consultadaPor\"", "\"otraCosa\""), new UTF8Encoding(false));
                Check(SelfUpdate.Load(f, "x/y", "2.0.30") == null, "guardada por una versión anterior (sin «consultadaPor»): se vuelve a consultar");
                File.WriteAllText(f, texto.Replace(AppInfo.Version, "2.0.35"), new UTF8Encoding(false));
                Check(SelfUpdate.Load(f, "x/y", "2.0.30") == null, "guardada por otra versión (la de antes de actualizarse): se vuelve a consultar");
                Check(texto.Contains("\"consultadaPor\": \"" + AppInfo.Version + (AppInfo.IsPortable ? " portable" : "") + "\"") ||
                      texto.Contains("\"consultadaPor\":\"" + AppInfo.Version + (AppInfo.IsPortable ? " portable" : "") + "\""), "anota quién consultó");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>Los guiones salen de la carpeta bajada antes de abrir Actualizador y de borrarla.</summary>
        static void TestGuionesSalenDeLaCarpeta2038()
        {
            string instalada = SelfInstall.Script(@"C:\Windows\Temp\Actualizador-1\entrega", 7, @"C:\ps.exe", @"C:\Program Files\Actualizador\Actualizador.exe", @"C:\Windows\Temp\Actualizador-1\i.log");
            string portable = SelfInstall.PortableScript(@"C:\Windows\Temp\Actualizador-1\entrega", 7, @"C:\ps.exe", @"C:\Windows\System32\robocopy.exe", @"E:\Actualizador", @"C:\Windows\Temp\Actualizador-1\i.log");
            foreach (string s in new[] { instalada, portable })
            {
                int sale = s.IndexOf("cd /d \"" + SafePaths.System32 + "\"", StringComparison.Ordinal);   // 2.0.41: por ruta completa
                int abre = s.IndexOf("start \"\"", StringComparison.Ordinal);
                int borra = s.IndexOf("rd /s /q", StringComparison.Ordinal);
                Check(sale > 0 && abre > sale && borra > sale, "sale de la carpeta bajada antes de abrir Actualizador y de borrarla");
            }
        }

        static void TestVersion2038()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.38") >= 0, "Actualizador 2.0.38 o posterior: " + AppInfo.Version);
        }
    }
}
