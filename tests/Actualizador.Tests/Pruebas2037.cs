// Pruebas de la 2.0.37. Pedido del usuario (23/11/2026): «en el portable se puede hacer que se actualice solo sin que se abra la página».
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        /// <summary>La portable busca su propia entrega en GitHub y se puede actualizar sola.</summary>
        static void TestPortableBuscaSuEntrega2037()
        {
            Eq("Actualizador-2.0.37-portable.zip", SelfUpdate.AssetNameFor("2.0.37", true), "la entrega de la portable");
            Eq("Actualizador-2.0.37.zip", SelfUpdate.AssetNameFor("2.0.37", false), "la de la instalada, como siempre");
            string pedido = null;
            SelfUpdate.LatestForTests = delegate(string repo) { return "https://github.com/" + repo + "/releases/tag/v2.0.37"; };
            SelfUpdate.ShaForTests = delegate(string url) { pedido = url; return new string('D', 64) + "  x.zip\r\n"; };
            try
            {
                var r = SelfUpdate.Check("federicobangher/actualizador", "2.0.36", true);
                Eq("https://github.com/federicobangher/actualizador/releases/download/v2.0.37/Actualizador-2.0.37-portable.zip", r.AssetUrl, "la portable baja la portable");
                Check(r.CanInstall && pedido.EndsWith("Actualizador-2.0.37-portable.zip.sha256"), "con su SHA-256");
                r = SelfUpdate.Check("federicobangher/actualizador", "2.0.36", false);
                Check(r.AssetUrl.EndsWith("/Actualizador-2.0.37.zip"), "la instalada baja la suya");
                SelfUpdate.ShaForTests = delegate(string url) { throw new InvalidOperationException("404"); };
                r = SelfUpdate.Check("federicobangher/actualizador", "2.0.36", true);
                Check(r.Newer && !r.CanInstall, "una versión publicada sin portable: la portable sólo avisa (como antes)");
            }
            finally { SelfUpdate.LatestForTests = null; SelfUpdate.ShaForTests = null; }

            try
            {
                SelfInstall.WritableForTests = delegate(string d) { return true; };
                Check(SelfInstall.CannotInstall(true, true) == null, "portable, como administrador y en una carpeta que se puede escribir: se actualiza sola");
                Check(SelfInstall.CannotInstall(true, false) != null, "sin administrador: sólo avisa");
                SelfInstall.WritableForTests = delegate(string d) { return false; };
                string why = SelfInstall.CannotInstall(true, true);
                Check(why != null && why.Contains("escribir"), "en un pendrive protegido (no se puede escribir): sólo avisa y dice por qué");
                Check(SelfInstall.CannotInstall(false, true) == null, "la instalada no depende de su carpeta");
            }
            finally { SelfInstall.WritableForTests = null; }
        }

        static string Zip2037(string dir, params string[] names)
        {
            string zip = Path.Combine(dir, "z-" + Guid.NewGuid().ToString("N") + ".zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (string n in names) using (var w = new StreamWriter(z.CreateEntry(n).Open())) w.Write("contenido de " + n);
            return zip;
        }

        /// <summary>La entrega de la portable se baja, se verifica y se descomprime; la de la instalada no sirve para la portable.</summary>
        static void TestPortablePrepara2037()
        {
            string dir = Path.Combine(Path.GetTempPath(), "act2037-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string buena = Zip2037(dir, "Actualizador.exe", "Actualizador.Cli.exe", "Actualizador.portable", "LEEME.txt", "SHA256SUMS.txt", "fuentes-oficiales.json");
                string origen = buena;
                SelfInstall.DownloadForTests = delegate(string url, string destino) { File.Copy(origen, destino, true); };
                SelfInstall.ExeVersionForTests = delegate(string exe) { return "2.0.37+abc"; };
                Func<string, SelfUpdate.Result> res = delegate(string z)
                {
                    return new SelfUpdate.Result { Latest = "2.0.37", Newer = true, AssetName = "Actualizador-2.0.37-portable.zip", AssetSha256 = Sha2032(z),
                                                   AssetUrl = "https://github.com/federicobangher/actualizador/releases/download/v2.0.37/Actualizador-2.0.37-portable.zip" };
                };
                string error;
                SelfInstall.StagingForTests = Path.Combine(dir, "bien"); Directory.CreateDirectory(SelfInstall.StagingForTests);
                string listo = SelfInstall.Stage(res(buena), true, null, out error);
                Check(listo != null && File.Exists(Path.Combine(listo, "Actualizador.exe")) && File.Exists(Path.Combine(listo, "Actualizador.portable")),
                      "la portable: bajada, verificada y descomprimida: " + (error ?? listo));

                origen = Zip2037(dir, "Instalar.cmd", "Install-Actualizador.ps1", "Actualizador/Actualizador.exe");
                SelfInstall.StagingForTests = Path.Combine(dir, "instalada"); Directory.CreateDirectory(SelfInstall.StagingForTests);
                Check(SelfInstall.Stage(res(origen), true, null, out error) == null && error.Contains("portable"), "la entrega de la instalada no se copia en la portable");

                origen = Zip2037(dir, "Actualizador.exe", "Actualizador.portable", "Datos/opciones.json");
                SelfInstall.StagingForTests = Path.Combine(dir, "datos"); Directory.CreateDirectory(SelfInstall.StagingForTests);
                Check(SelfInstall.Stage(res(origen), true, null, out error) == null, "una entrega con «Datos» no pisa las opciones ni el historial");
            }
            finally
            {
                SelfInstall.DownloadForTests = null; SelfInstall.ExeVersionForTests = null; SelfInstall.StagingForTests = null;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>El guion de la portable: espera, copia encima sin borrar nada ni tocar «Datos», y la vuelve a abrir.</summary>
        static void TestGuionPortable2037()
        {
            string s = SelfInstall.PortableScript(@"C:\Windows\Temp\Actualizador-1\entrega", 4242, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                                                  @"C:\Windows\System32\robocopy.exe", @"D:\Programas\Técnico\Actualizador\", @"C:\Windows\Temp\Actualizador-1\instalacion-automatica.log");
            int espera = s.IndexOf("Wait-Process -Id 4242", StringComparison.Ordinal);
            int copia = s.IndexOf("robocopy.exe\" \"C:\\Windows\\Temp\\Actualizador-1\\entrega\" \"D:\\Programas\\Técnico\\Actualizador\" /E", StringComparison.Ordinal);
            int abre = s.IndexOf("start \"\" \"D:\\Programas\\Técnico\\Actualizador\\Actualizador.exe\"", StringComparison.Ordinal);
            Check(espera > 0 && copia > espera && abre > copia, "en orden: esperar que se cierre, copiar encima, volver a abrir");
            Check(s.Contains("/XD Datos") && !s.Contains("/MIR") && !s.Contains("/PURGE"), "no borra nada y no toca «Datos»");
            Check(s.Contains("/R:10 /W:3"), "reintenta si un archivo sigue en uso");
            Check(s.Contains("if %RC% LSS 8 set RC=0") && s.Contains(">\"C:\\Windows\\Temp\\Actualizador-1\\resultado.txt\" echo %RC%"), "anota si salió bien (robocopy: menos de 8)");
            Check(s.Contains("rd /s /q \"C:\\Windows\\Temp\\Actualizador-1\\entrega\""), "borra lo bajado");
            bool rechazo = false;
            try { SelfInstall.PortableScript(@"C:\x", 1, "ps", "rc", @"D:\a&calc", "log"); } catch (ArgumentException) { rechazo = true; }
            Check(rechazo, "una ruta rara no entra al guion");

            string raiz = Raiz();
            if (raiz == null) return;
            string pub = File.ReadAllText(Path.Combine(raiz, "build", "Publicar.ps1"), Encoding.UTF8);
            Check(pub.Contains("EmpaquetarPortable.ps1") && pub.Contains("$nombre-portable") && pub.Contains("$zipPortable.sha256"),
                  "Publicar.ps1 publica también la portable (.zip con su .sha256)");
        }

        static void TestVersion2037()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.37") >= 0, "Actualizador 2.0.37 o posterior: " + AppInfo.Version);
        }
    }
}
