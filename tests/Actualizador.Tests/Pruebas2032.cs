// Pruebas de la 2.0.32. Pedido del usuario (23/11/2026): «¿podemos hacer que tenga actualización como hicimos con Virtual Pet? es cansador
// estar borrando e instalando cada vez que hay un error». Elegido: GitHub (federicobangher/actualizador), publicado con Publicar.cmd.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static string Sha2032(string file)
        {
            using (var s = File.OpenRead(file)) using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>La versión publicada en GitHub trae la entrega .zip con su SHA-256: se puede instalar sola.</summary>
        static void TestVersionNuevaEnGitHub2032()
        {
            Eq(SelfUpdate.DefaultSource, SelfUpdate.EffectiveSource(""), "sin configurar: el proyecto de siempre");
            Eq("otro/proyecto", SelfUpdate.EffectiveSource(" otro/proyecto "), "lo de Opciones manda");
            Eq("Actualizador-2.0.33.zip", SelfUpdate.AssetNameFor("2.0.33"), "el nombre de la entrega");
            var ahora = new DateTime(2026, 11, 24, 9, 0, 0);
            Check(!SelfUpdate.ShouldCheck(ahora.AddMinutes(-30), ahora) && SelfUpdate.ShouldCheck(ahora.AddMinutes(-61), ahora), "una consulta por hora como mucho (antes, cada 20 horas)");

            string sha = new string('a', 64);
            string json = "{\"tag_name\":\"v2.0.33\",\"html_url\":\"https://github.com/federicobangher/actualizador/releases/tag/v2.0.33\",\"draft\":false,\"prerelease\":false,\"assets\":[" +
                          "{\"name\":\"Actualizador-2.0.33.7z\",\"browser_download_url\":\"https://github.com/federicobangher/actualizador/releases/download/v2.0.33/Actualizador-2.0.33.7z\",\"size\":1}," +
                          "{\"name\":\"Actualizador-2.0.33.zip\",\"browser_download_url\":\"https://github.com/federicobangher/actualizador/releases/download/v2.0.33/Actualizador-2.0.33.zip\",\"size\":2,\"digest\":\"sha256:" + sha + "\"}]}";
            var rel = GitHubRules.ParseRelease(json);
            var r = new SelfUpdate.Result { Latest = rel.Version, Newer = VersionUtil.IsNewer(rel.Version, "2.0.32") };
            SelfUpdate.PickAsset(rel, r, null);
            Check(r.CanInstall && r.AssetName == "Actualizador-2.0.33.zip" && r.AssetSha256 == sha, "la .zip con el SHA-256 que informa GitHub");

            // Sin «digest» de GitHub: el de su .sha256 publicado al lado.
            string sinDigest = json.Replace(",\"digest\":\"sha256:" + sha + "\"", "").Replace("]}", ",{\"name\":\"Actualizador-2.0.33.zip.sha256\",\"browser_download_url\":\"https://github.com/federicobangher/actualizador/releases/download/v2.0.33/Actualizador-2.0.33.zip.sha256\",\"size\":3}]}");
            r = new SelfUpdate.Result { Latest = "2.0.33", Newer = true };
            string pedido = null;
            SelfUpdate.PickAsset(GitHubRules.ParseRelease(sinDigest), r, delegate(string url) { pedido = url; return new string('B', 64) + "  Actualizador-2.0.33.zip\r\n"; });
            Check(r.CanInstall && r.AssetSha256 == new string('b', 64) && pedido.EndsWith(".zip.sha256"), "el SHA-256 del archivo .sha256");
            r = new SelfUpdate.Result { Latest = "2.0.33", Newer = true };
            SelfUpdate.PickAsset(GitHubRules.ParseRelease(json.Replace(",\"digest\":\"sha256:" + sha + "\"", "")), r, null);
            Check(!r.CanInstall, "sin SHA-256 publicado no se instala sola (sólo avisa)");
            Check(SelfInstall.CannotInstall(true, true) != null && SelfInstall.CannotInstall(false, false) != null && SelfInstall.CannotInstall(false, true) == null,
                  "la portable y sin administrador sólo avisan");
        }

        static string Entrega2032(string dir, string extra)
        {
            string zip = Path.Combine(dir, "armada-" + Guid.NewGuid().ToString("N") + ".zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (string name in new[] { "Instalar.cmd", "Install-Actualizador.ps1", "Actualizador/Actualizador.exe", "Actualizador/SHA256SUMS.txt" })
                    using (var w = new StreamWriter(z.CreateEntry(name).Open())) w.Write("contenido de " + name);
                if (extra != null) using (var w = new StreamWriter(z.CreateEntry(extra).Open())) w.Write("x");
            }
            return zip;
        }

        /// <summary>Bajar, verificar y preparar: sólo con el SHA-256 publicado, sin rutas que se salgan y con la versión anunciada.</summary>
        static void TestPrepararVersionNueva2032()
        {
            string dir = Path.Combine(Path.GetTempPath(), "act2032-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string buena = Entrega2032(dir, null);
                var r = new SelfUpdate.Result { Latest = "2.0.33", Newer = true, AssetName = "Actualizador-2.0.33.zip",
                                                AssetUrl = "https://github.com/federicobangher/actualizador/releases/download/v2.0.33/Actualizador-2.0.33.zip", AssetSha256 = Sha2032(buena) };
                string origen = buena;
                SelfInstall.DownloadForTests = delegate(string url, string destino) { File.Copy(origen, destino, true); };
                SelfInstall.ExeVersionForTests = delegate(string exe) { return "2.0.33+3f2a1c"; };
                string error;
                SelfInstall.StagingForTests = Path.Combine(dir, "bien"); Directory.CreateDirectory(SelfInstall.StagingForTests);
                string listo = SelfInstall.Stage(r, null, out error);
                Check(listo != null && File.Exists(Path.Combine(listo, "Install-Actualizador.ps1")) && File.Exists(Path.Combine(listo, "Actualizador", "Actualizador.exe")),
                      "bajada, verificada y descomprimida: " + (error ?? listo));

                var otroSha = new SelfUpdate.Result { Latest = r.Latest, Newer = true, AssetName = r.AssetName, AssetUrl = r.AssetUrl, AssetSha256 = new string('0', 64) };
                SelfInstall.StagingForTests = Path.Combine(dir, "sha"); Directory.CreateDirectory(SelfInstall.StagingForTests);
                Check(SelfInstall.Stage(otroSha, null, out error) == null && error.Contains("SHA-256"), "otro SHA-256: no se instala");

                SelfInstall.ExeVersionForTests = delegate(string exe) { return "2.0.31"; };
                SelfInstall.StagingForTests = Path.Combine(dir, "version"); Directory.CreateDirectory(SelfInstall.StagingForTests);
                Check(SelfInstall.Stage(r, null, out error) == null && error.Contains("2.0.31"), "dice 2.0.33 y trae otra: no se instala");
                SelfInstall.ExeVersionForTests = delegate(string exe) { return "2.0.33"; };

                origen = Entrega2032(dir, "../../fuera.txt");
                var trampa = new SelfUpdate.Result { Latest = r.Latest, Newer = true, AssetName = r.AssetName, AssetUrl = r.AssetUrl, AssetSha256 = Sha2032(origen) };
                SelfInstall.StagingForTests = Path.Combine(dir, "trampa"); Directory.CreateDirectory(SelfInstall.StagingForTests);
                Check(SelfInstall.Stage(trampa, null, out error) == null && error.Contains("sale de su carpeta") && !File.Exists(Path.Combine(dir, "fuera.txt")),
                      "una entrada «../» no escribe fuera de su carpeta");

                var otroSitio = new SelfUpdate.Result { Latest = r.Latest, Newer = true, AssetName = r.AssetName, AssetUrl = "https://ejemplo.com/Actualizador-2.0.33.zip", AssetSha256 = r.AssetSha256 };
                Check(SelfInstall.Stage(otroSitio, null, out error) == null && error.Contains("GitHub"), "sólo desde GitHub");

                Check(SelfInstall.SameVersion("2.0.33+3f2a1c", "2.0.33") && SelfInstall.SameVersion("2.0.33.0", "2.0.33") && !SelfInstall.SameVersion("2.0.32", "2.0.33"), "versión del producto con o sin el commit");
            }
            finally
            {
                SelfInstall.DownloadForTests = null; SelfInstall.ExeVersionForTests = null; SelfInstall.StagingForTests = null;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>El guion: espera que Actualizador se cierre, instala en silencio con el instalador de siempre y lo vuelve a abrir.</summary>
        static void TestGuionDeActualizacion2032()
        {
            string s = SelfInstall.Script(@"C:\Windows\Temp\Actualizador-1\entrega", 4321, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                                          @"C:\Program Files\Actualizador\Actualizador.exe", @"C:\Windows\Temp\Actualizador-1\instalacion-automatica.log");
            Check(s.Contains("Wait-Process -Id 4321"), "espera a que Actualizador se cierre");
            Check(s.Contains("Install-Actualizador.ps1\" -Silencioso -Origen \"C:\\Windows\\Temp\\Actualizador-1\\entrega\\Actualizador\""), "el instalador de siempre, en silencio, con lo bajado");
            Check(s.IndexOf("Wait-Process", StringComparison.Ordinal) < s.IndexOf("Install-Actualizador", StringComparison.Ordinal) &&
                  s.IndexOf("Install-Actualizador", StringComparison.Ordinal) < s.IndexOf("start \"\" \"C:\\Program Files\\Actualizador\\Actualizador.exe\"", StringComparison.Ordinal),
                  "en orden: esperar, instalar, volver a abrir");
            Check(s.Contains("rd /s /q \"C:\\Windows\\Temp\\Actualizador-1\\entrega\""), "y borra lo bajado");
            bool rechazo = false;
            try { SelfInstall.Script(@"C:\x&calc", 1, "ps", "exe", "log"); } catch (ArgumentException) { rechazo = true; }
            Check(rechazo, "una ruta rara no entra al guion");
        }

        /// <summary>2.0.34: la versión nueva se busca sin la API de GitHub (60 consultas por hora por conexión, compartidas por toda la red).</summary>
        static void TestVersionSinApi2034()
        {
            Eq("v2.0.35", SelfUpdate.TagFromReleaseUrl("https://github.com/federicobangher/actualizador/releases/tag/v2.0.35"), "la etiqueta, de la dirección final");
            Eq(null, SelfUpdate.TagFromReleaseUrl("https://github.com/federicobangher/actualizador/releases"), "sin versiones: GitHub no redirige a una etiqueta");
            string pedidoSha = null;
            SelfUpdate.LatestForTests = delegate(string repo) { return "https://github.com/" + repo + "/releases/tag/v2.0.35"; };
            SelfUpdate.ShaForTests = delegate(string url) { pedidoSha = url; return new string('C', 64) + "  Actualizador-2.0.35.zip\r\n"; };
            try
            {
                var r = SelfUpdate.Check("federicobangher/actualizador", "2.0.34");
                Check(r.Error == null && r.Newer && r.Latest == "2.0.35", "encuentra la 2.0.35: " + (r.Error ?? r.Latest));
                Eq("https://github.com/federicobangher/actualizador/releases/download/v2.0.35/Actualizador-2.0.35.zip", r.AssetUrl, "la entrega, por la dirección de descarga común");
                Check(r.CanInstall && r.AssetSha256 == new string('c', 64) && pedidoSha.EndsWith("Actualizador-2.0.35.zip.sha256"), "con el SHA-256 de su .sha256");
                SelfUpdate.ShaForTests = delegate(string url) { throw new InvalidOperationException("404"); };
                r = SelfUpdate.Check("federicobangher/actualizador", "2.0.34");
                Check(r.Newer && !r.CanInstall, "sin .sha256 publicado: sólo avisa");
                SelfUpdate.LatestForTests = delegate(string repo) { return "https://github.com/" + repo + "/releases"; };
                r = SelfUpdate.Check("federicobangher/actualizador", "2.0.34");
                Check(r.Error != null && r.Error.Contains("todavía no hay versiones publicadas"), "proyecto sin versiones: lo dice");
            }
            finally { SelfUpdate.LatestForTests = null; SelfUpdate.ShaForTests = null; }
        }

        static void TestVersion2033()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.33") >= 0, "Actualizador 2.0.33 o posterior: " + AppInfo.Version);
            string raiz = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string opciones = Path.Combine(raiz, "src", "Actualizador.App", "OptionsForm.cs");
            string menu = Path.Combine(raiz, "src", "Actualizador.App", "MainForm.Layout.cs");
            if (File.Exists(opciones) && File.Exists(menu))
            {
                Check(File.ReadAllText(opciones, Encoding.UTF8).Contains("\"Buscar actualización de Actualizador\""), "Opciones: botón «Buscar actualización de Actualizador»");
                Check(File.ReadAllText(menu, Encoding.UTF8).Contains("\"Buscar actualización de Actualizador…\""), "Más: «Buscar actualización de Actualizador…»");
            }
        }

        static void TestVersion2032()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.32") >= 0, "Actualizador 2.0.32 o posterior: " + AppInfo.Version);
            string raiz = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            if (File.Exists(Path.Combine(raiz, "Publicar.cmd")))
            {
                string ps = File.ReadAllText(Path.Combine(raiz, "build", "Publicar.ps1"), Encoding.UTF8);
                Check(ps.Contains("federicobangher/actualizador") && ps.Contains("gh release create") && ps.Contains(".zip.sha256") || ps.Contains("$zip.sha256"), "Publicar.ps1 sube la .zip y su SHA-256 a GitHub");
            }
        }
    }
}
