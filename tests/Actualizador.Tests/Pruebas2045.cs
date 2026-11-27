// Pruebas de la 2.0.45. Captura del usuario (26/11/2026, 20:55), con la 2.0.44: Epic Online Services 5.4.0 → 5.8.0 «Falló la instalación:
// Reinstalación cancelada (no se desinstaló nada): demasiadas redirecciones».
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        /// <summary>Las redirecciones guardan las cookies entre saltos y, si se agotan, el error dice por dónde pasó.</summary>
        static void TestRedireccionesConCookies2045()
        {
            Check(Http.MaxHops >= 12, "más saltos permitidos: " + Http.MaxHops);
            string resumen = Http.RedirectSummary(new List<string> { "302 onlineservices.epicgames.com", "302 onlineservices.epicgames.com", "302 cdn.epicgames.com" }, "onlineservices.epicgames.com");
            Eq("onlineservices.epicgames.com ×2 → cdn.epicgames.com → onlineservices.epicgames.com", resumen, "el recorrido, sin repetir seguidos");
            string raiz = Raiz();
            if (raiz == null) { Check(false, "no se encontró la raíz del repositorio"); return; }
            string http = Leer(raiz, "src/Actualizador.Engine/Updates/Http.cs");
            Check(http.Contains("var jar = new CookieContainer();") && http.Contains("req.CookieContainer = jar;"), "cada pedido guarda sus cookies entre saltos");
            int iJar = http.IndexOf("var jar = new CookieContainer();", StringComparison.Ordinal);
            int iSend = http.IndexOf("static HttpWebResponse Send(", StringComparison.Ordinal);
            Check(iJar > iSend, "las cookies son de ese pedido, no de todo el programa");
            Check(http.Contains("throw new InvalidOperationException(\"demasiadas redirecciones (\" + RedirectSummary("), "el error dice el recorrido");
        }

        static string ZipEosFalso(string dir)
        {
            string zip = Path.Combine(dir, "eos.zip");
            var exe = new byte[64 * 1024];
            exe[0] = (byte)'M'; exe[1] = (byte)'Z';
            for (int i = 2; i < exe.Length; i++) exe[i] = (byte)(i * 31 % 251);
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var e = z.CreateEntry("SDK/Tools/EpicOnlineServicesInstaller.exe", CompressionLevel.NoCompression);
                using (var s = e.Open()) s.Write(exe, 0, exe.Length);
                var r = z.CreateEntry("SDK/README.txt", CompressionLevel.NoCompression);
                using (var s = r.Open()) s.Write(new byte[20000], 0, 20000);
            }
            return zip;
        }

        /// <summary>
        /// La captura: la descarga directa falla y la baja «winget download». Con el SHA-256 del catálogo se sigue (acá hasta el
        /// desinstalador, que en Linux no existe); con otro archivo, no se toca nada.
        /// </summary>
        static void TestEosConWingetDownload2045()
        {
            var oldManifest = WingetDirect.ManifestForTests;
            var oldPolicy = UpdateManager.PolicyForTests;
            string tmp = Path.Combine(Path.GetTempPath(), "act-eos-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                string zip = ZipEosFalso(tmp);
                string sha;
                using (var fs = File.OpenRead(zip)) sha = InstallerSupport.Sha256(fs);
                string manifiesto = EosManifiesto.Replace("2D1A73C7912DE71CBAC8CA39E51D189CD8A7CC4B5EB1B3D86D645F1467B703E6", sha.ToUpperInvariant());
                UpdateManager.PolicyForTests = new Policy();
                WingetDirect.ManifestForTests = delegate(string u)
                {
                    return u.EndsWith("/EpicGames/EpicOnlineServices/5.8.0/EpicGames.EpicOnlineServices.installer.yaml") ? manifiesto : null;
                };
                var eos = EosInstalado();
                var pedidos = new List<string>();
                bool dejarArchivo = true;
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.RunForTests = delegate(string args)
                {
                    pedidos.Add(args);
                    if (args.StartsWith("download "))
                    {
                        var m = Regex.Match(args, "--download-directory \"([^\"]+)\"");
                        if (m.Success && dejarArchivo) File.Copy(zip, Path.Combine(m.Groups[1].Value, "Epic Online Services_5.8.0_Machine_X64_zip.zip"));
                        if (m.Success) File.WriteAllText(Path.Combine(m.Groups[1].Value, "EpicGames.EpicOnlineServices_5.8.0.yaml"), "x");
                        return Salida(0, "Instalador descargado");
                    }
                    return Salida(WingetDirect.TechMismatch, "Se encontró una versión más reciente, pero la tecnología de instalación es diferente de la versión actual instalada.");
                };
                // Sin ejecutar nada en el equipo de prueba: winget no deja el archivo verificado → no se desinstala nada.
                dejarArchivo = false;
                var c = FilaWinget("Epic Online Services", "EpicGames.EpicOnlineServices", "5.4.0", "5.8.0");
                wp.Install(ContextoSimulado(true, eos), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                string bajada = pedidos.Find(delegate(string a) { return a.StartsWith("download "); });
                Check(bajada != null && bajada.Contains("--id \"EpicGames.EpicOnlineServices\"") && bajada.Contains("--version \"5.8.0\"") &&
                      bajada.Contains("--exact") && bajada.Contains("--source \"winget\"") && !bajada.Contains("--interactive"),
                      "si falla la descarga directa: winget download de esa versión exacta: " + bajada);
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("no se desinstaló nada") && c.Result.Contains("SHA-256 del catálogo"),
                      "sin el archivo verificado: no se toca nada y se dicen las dos causas (" + c.Result + ")");

                // Lo que deja winget download: se usa sólo el archivo con el SHA-256 del catálogo (el .yaml y lo demás, no).
                var inst = WingetManifest.Parse(manifiesto).Installers[0];
                string destino = Path.Combine(tmp, "bajado.zip");
                string why;
                var c2 = FilaWinget("Epic Online Services", "EpicGames.EpicOnlineServices", "5.4.0", "5.8.0");
                bool ok = WingetDirect.DownloadWithWinget(ContextoSimulado(true), c2, delegate(UpdateCandidate x) { }, inst, "5.8.0", destino,
                    delegate(string carpeta, string version)
                    {
                        File.WriteAllText(Path.Combine(carpeta, "EpicGames.EpicOnlineServices_5.8.0.yaml"), "x");
                        File.WriteAllBytes(Path.Combine(carpeta, "otro.zip"), new byte[30000]);
                        File.Copy(zip, Path.Combine(carpeta, "Epic Online Services_5.8.0_Machine_X64_zip.zip"));
                        return Salida(0, "Instalador descargado");
                    }, out why);
                string shaDestino = "";
                if (File.Exists(destino)) using (var fs = File.OpenRead(destino)) shaDestino = InstallerSupport.Sha256(fs);
                Check(ok && shaDestino == sha, "se toma el archivo con el SHA-256 del catálogo (" + why + ")");
                File.Delete(destino);
                ok = WingetDirect.DownloadWithWinget(ContextoSimulado(true), c2, delegate(UpdateCandidate x) { }, inst, "5.8.0", destino,
                    delegate(string carpeta, string version) { File.WriteAllBytes(Path.Combine(carpeta, "otro.zip"), new byte[30000]); return Salida(0, ""); }, out why);
                Check(!ok && !File.Exists(destino) && why.Contains("SHA-256"), "otro archivo: no se usa (" + why + ")");
                ok = WingetDirect.DownloadWithWinget(ContextoSimulado(true), c2, delegate(UpdateCandidate x) { }, inst, "5.8.0", destino,
                    delegate(string carpeta, string version) { return Salida(0x8A150014, ""); }, out why);
                Check(!ok && why.StartsWith("winget download:"), "winget falla: se dice (" + why + ")");
                ok = WingetDirect.DownloadWithWinget(ContextoSimulado(true), c2, delegate(UpdateCandidate x) { }, inst, "5.8.0 --source x", destino,
                    delegate(string carpeta, string version) { return Salida(0, ""); }, out why);
                Check(!ok, "versión con argumentos colados: no se ejecuta");
            }
            finally
            {
                WingetDirect.ManifestForTests = oldManifest; UpdateManager.PolicyForTests = oldPolicy;
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        static void TestVersion2045()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.45") >= 0, "Actualizador 2.0.45 o posterior: " + AppInfo.Version);
        }
    }
}
