// Pruebas de la 2.0.46. Pedido del usuario (26/11/2026), sobre la auditoría: «eso quiero hacer» (que alguien que entre a la cuenta de
// GitHub no pueda colar una versión falsa). Eligió la firma propia gratis: la clave privada queda en su PC (Firmar.cmd) y cada equipo
// sólo se actualiza solo con versiones firmadas por una clave que trae compilada adentro.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static string LineaClave2046(RSA rsa)
        {
            var p = rsa.ExportParameters(false);
            return VersionSignature.KeyId(p.Modulus) + " " + Convert.ToBase64String(p.Modulus) + " " + Convert.ToBase64String(p.Exponent) + " prueba";
        }

        static string Firma2046(RSA rsa, string asset, string version, string sha, string idOverride)
        {
            var p = rsa.ExportParameters(false);
            byte[] sig = rsa.SignData(Encoding.UTF8.GetBytes(VersionSignature.Message(asset, version, sha)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return VersionSignature.Header + "\narchivo: " + asset + "\nversion: " + version + "\nsha256: " + sha + "\nclave: " + (idOverride ?? VersionSignature.KeyId(p.Modulus)) +
                   "\nfirma: " + Convert.ToBase64String(sig) + "\n";
        }

        /// <summary>El formato, las claves de confianza y todo lo que tiene que rechazar.</summary>
        static void TestFirmaDeVersiones2046()
        {
            using (var rsa = RSA.Create())
            using (var otra = RSA.Create())
            {
                rsa.KeySize = 3072; otra.KeySize = 3072;
                var claves = VersionSignature.ParseKeys("# comentario\r\n" + LineaClave2046(rsa) + "\r\n\r\n");
                Eq(1, claves.Count, "una clave de 3072 bits");
                string sha = new string('a', 64), asset = "Actualizador-2.0.46.zip";
                string firma = Firma2046(rsa, asset, "2.0.46", sha, null);
                string why;
                Check(VersionSignature.Verify(firma, asset, "2.0.46", sha, claves, out why), "firma válida (" + why + ")");
                Check(VersionSignature.Verify(firma.Replace("\n", "\r\n"), asset, "2.0.46", sha.ToUpperInvariant(), claves, out why), "con CRLF y el SHA-256 en mayúsculas, también");
                Check(!VersionSignature.Verify(firma, asset, "2.0.47", sha, claves, out why) && why.Contains("otra versión"), "otra versión: " + why);
                Check(!VersionSignature.Verify(firma, "Actualizador-2.0.46-portable.zip", "2.0.46", sha, claves, out why) && why.Contains("otro archivo"), "otro archivo: " + why);
                Check(!VersionSignature.Verify(firma, asset, "2.0.46", new string('b', 64), claves, out why) && why.Contains("SHA-256"), "otro contenido: " + why);
                // Cambiar la versión en el texto de la firma no sirve: lo firmado es otra cosa.
                string cambiada = firma.Replace("version: 2.0.46", "version: 2.0.99");
                Check(!VersionSignature.Verify(cambiada, asset, "2.0.99", sha, claves, out why) && why.Contains("NO es válida"), "versión cambiada en la firma: " + why);
                // Firmada por otra clave (la de quien entró a GitHub), aunque diga el identificador bueno.
                string ajena = Firma2046(otra, asset, "2.0.46", sha, claves[0].Id);
                Check(!VersionSignature.Verify(ajena, asset, "2.0.46", sha, claves, out why), "otra clave con el identificador bueno: " + why);
                Check(!VersionSignature.Verify(Firma2046(otra, asset, "2.0.46", sha, null), asset, "2.0.46", sha, claves, out why) && why.Contains("no reconoce"), "clave desconocida: " + why);
                Check(!VersionSignature.Verify("hola", asset, "2.0.46", sha, claves, out why), "basura");
                Check(!VersionSignature.Verify(firma + "version: 2.0.46\n", asset, "2.0.46", sha, claves, out why) && why.Contains("repite"), "campo repetido");
                Check(!VersionSignature.Verify(firma, asset, "2.0.46", sha, new List<VersionSignature.TrustedKey>(), out why), "sin claves no se acepta nada");
                Check(!VersionSignature.Verify(new string('x', 9000), asset, "2.0.46", sha, claves, out why), "demasiado larga");
            }
            using (var chica = RSA.Create())
            {
                chica.KeySize = 2048;
                Eq(0, VersionSignature.ParseKeys(LineaClave2046(chica)).Count, "una clave de 2048 bits no se acepta");
            }
            using (var rsa = RSA.Create())
            {
                rsa.KeySize = 3072;
                string l = LineaClave2046(rsa);
                Eq(0, VersionSignature.ParseKeys("0000000000000000" + l.Substring(16)).Count, "identificador que no corresponde: no se acepta");
            }
            Eq("Actualizador|1|Actualizador-2.0.46.zip|2.0.46|" + new string('a', 64), VersionSignature.Message(" Actualizador-2.0.46.zip ", "2.0.46", new string('A', 64)), "lo que se firma");
            // Las claves compiladas: las de res/claves-de-firma.txt, adentro del programa.
            string raiz = Raiz();
            if (raiz == null) { Check(false, "no se encontró la raíz del repositorio"); return; }
            Eq(VersionSignature.ParseKeys(Leer(raiz, "res/claves-de-firma.txt")).Count, VersionSignature.TrustedKeys.Count, "las claves compiladas son las de res/claves-de-firma.txt");
            Check(Leer(raiz, "src/Actualizador.Core/Actualizador.Core.csproj").Contains("<EmbeddedResource Include=\"..\\..\\res\\claves-de-firma.txt\" LogicalName=\"claves-de-firma.txt\" />"),
                  "compiladas adentro (no un archivo al lado del programa que se pueda cambiar)");
        }

        /// <summary>Lo que firma Windows PowerShell 5.1 (Firma.ps1, en GitHub) lo acepta el programa. Sólo donde corrió ese paso (GitHub).</summary>
        static void TestFirmaDePowerShell2046()
        {
            string dir = Environment.GetEnvironmentVariable("ACTUALIZADOR_FIRMA_PRUEBA");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "firma.txt"))) { Console.WriteLine("  (sin la firma de prueba de PowerShell: se verifica en GitHub)"); return; }
            var claves = VersionSignature.ParseKeys(File.ReadAllText(Path.Combine(dir, "linea.txt")));
            Eq(1, claves.Count, "la línea de clave que arma Firma.ps1");
            string why;
            Check(VersionSignature.Verify(File.ReadAllText(Path.Combine(dir, "firma.txt")), "Actualizador-9.9.9.zip", "9.9.9", string.Concat(System.Linq.Enumerable.Repeat("ab", 32)), claves, out why),
                  "la firma de PowerShell vale para el programa (" + why + ")");
        }

        /// <summary>Con claves en el programa, una versión nueva sin firma válida sólo se avisa; firmada, se instala sola.</summary>
        static void TestActualizacionSoloFirmada2046()
        {
            using (var rsa = RSA.Create())
            {
                rsa.KeySize = 3072;
                string sha = new string('d', 64);
                string firmaUrl = null, firmaTexto = null;
                SelfUpdate.LatestForTests = delegate(string repo) { return "https://github.com/" + repo + "/releases/tag/v2.0.46"; };
                SelfUpdate.ShaForTests = delegate(string url) { return sha + "  x.zip\r\n"; };
                SelfUpdate.FirmaForTests = delegate(string url) { firmaUrl = url; return firmaTexto; };
                try
                {
                    VersionSignature.KeysForTests = new List<VersionSignature.TrustedKey>();
                    var r = SelfUpdate.Check("federicobangher/actualizador", "2.0.45", false);
                    Check(r.CanInstall && firmaUrl == null, "sin claves en el programa: como hasta la 2.0.45 (no se pide firma)");

                    VersionSignature.KeysForTests = VersionSignature.ParseKeys(LineaClave2046(rsa));
                    r = SelfUpdate.Check("federicobangher/actualizador", "2.0.45", false);
                    Check(r.Newer && !r.CanInstall && r.SignatureProblem.Contains("todavía no está firmada"), "sin firma: no se instala sola (" + r.SignatureProblem + ")");
                    Eq("https://github.com/federicobangher/actualizador/releases/download/v2.0.46/Actualizador-2.0.46.zip.firma", firmaUrl, "la firma, al lado de la entrega");
                    Check((SelfUpdate.Message(r) ?? "").Contains("No se instala sola: todavía no está firmada"), "el aviso dice por qué");

                    firmaTexto = Firma2046(rsa, "Actualizador-2.0.46.zip", "2.0.46", sha, null);
                    r = SelfUpdate.Check("federicobangher/actualizador", "2.0.45", false);
                    Check(r.CanInstall && r.SignatureOk, "firmada: se instala sola (" + r.SignatureProblem + ")");

                    // La firma de la portable no sirve para la instalada (y al revés).
                    firmaTexto = Firma2046(rsa, "Actualizador-2.0.46-portable.zip", "2.0.46", sha, null);
                    r = SelfUpdate.Check("federicobangher/actualizador", "2.0.45", false);
                    Check(!r.CanInstall, "firma de otra entrega: no");

                    // Lo guardado no decide: «firmada» se vuelve a verificar al instalar.
                    firmaTexto = Firma2046(rsa, "Actualizador-2.0.46.zip", "2.0.46", sha, null);
                    r = SelfUpdate.Check("federicobangher/actualizador", "2.0.45", false);
                    string path = Path.Combine(Path.GetTempPath(), "act2046-" + Guid.NewGuid().ToString("N") + ".json");
                    SelfUpdate.Save(path, r, "federicobangher/actualizador");
                    var l = SelfUpdate.Load(path, "federicobangher/actualizador", "2.0.45");
                    Check(l != null && l.SignatureOk && l.CanInstall, "lo guardado se lee");
                    File.Delete(path);
                }
                finally
                {
                    SelfUpdate.LatestForTests = null; SelfUpdate.ShaForTests = null; SelfUpdate.FirmaForTests = null; VersionSignature.KeysForTests = null;
                }
            }
        }

        /// <summary>Antes de instalar, la firma se verifica contra lo BAJADO: un version_nueva.json alterado no alcanza.</summary>
        static void TestInstalaSoloLoFirmado2046()
        {
            string dir = Path.Combine(Path.GetTempPath(), "act2046-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            using (var rsa = RSA.Create())
            {
                rsa.KeySize = 3072;
                try
                {
                    string zip = Zip2037(dir, "Instalar.cmd", "Install-Actualizador.ps1", "Actualizador/Actualizador.exe");
                    string falso = Zip2037(dir, "Install-Actualizador.ps1", "Actualizador/Actualizador.exe", "malo.txt");
                    string bajar = zip;
                    SelfInstall.DownloadForTests = delegate(string url, string destino) { File.Copy(bajar, destino, true); };
                    SelfInstall.ExeVersionForTests = delegate(string exe) { return "2.0.46"; };
                    VersionSignature.KeysForTests = VersionSignature.ParseKeys(LineaClave2046(rsa));
                    string firmado = Sha2032(zip);
                    SelfUpdate.FirmaForTests = delegate(string url) { return Firma2046(rsa, "Actualizador-2.0.46.zip", "2.0.46", firmado, null); };
                    Func<string, SelfUpdate.Result> res = delegate(string sha)
                    {
                        return new SelfUpdate.Result { Latest = "2.0.46", Newer = true, AssetName = "Actualizador-2.0.46.zip", AssetSha256 = sha, SignatureOk = true,
                                                       AssetUrl = "https://github.com/federicobangher/actualizador/releases/download/v2.0.46/Actualizador-2.0.46.zip" };
                    };
                    string error;
                    SelfInstall.StagingForTests = Path.Combine(dir, "bien"); Directory.CreateDirectory(SelfInstall.StagingForTests);
                    string listo = SelfInstall.Stage(res(firmado), false, null, out error);
                    Check(listo != null, "la firmada se prepara para instalar: " + (error ?? listo));

                    // Alguien cambió GitHub (otra entrega con su .sha256) y version_nueva.json dice «firmada»: la firma es de otro contenido.
                    bajar = falso;
                    SelfInstall.StagingForTests = Path.Combine(dir, "falsa"); Directory.CreateDirectory(SelfInstall.StagingForTests);
                    Check(SelfInstall.Stage(res(Sha2032(falso)), false, null, out error) == null && error.StartsWith("no se instala: la firma es de otro archivo"),
                          "otra entrega con su propio SHA-256: no se instala (" + error + ")");
                    SelfUpdate.FirmaForTests = delegate(string url) { return null; };
                    SelfInstall.StagingForTests = Path.Combine(dir, "sinfirma"); Directory.CreateDirectory(SelfInstall.StagingForTests);
                    bajar = zip;
                    Check(SelfInstall.Stage(res(firmado), false, null, out error) == null && error.Contains("no está firmada"), "sin firma publicada: no se instala (" + error + ")");
                }
                finally
                {
                    SelfInstall.DownloadForTests = null; SelfInstall.ExeVersionForTests = null; SelfInstall.StagingForTests = null;
                    SelfUpdate.FirmaForTests = null; VersionSignature.KeysForTests = null;
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }

        /// <summary>Firmar.cmd y sus guiones: arman lo mismo que verifica el programa.</summary>
        static void TestGuionesDeFirma2046()
        {
            string raiz = Raiz();
            if (raiz == null) { Check(false, "no se encontró la raíz del repositorio"); return; }
            string comun = Leer(raiz, "build/Comun.ps1"), firma = Leer(raiz, "build/Firma.ps1"), firmar = Leer(raiz, "build/Firmar.ps1"), cmd = Leer(raiz, "Firmar.cmd");
            string yml = Leer(raiz, ".github/workflows/publicar.yml"), publish = Leer(raiz, "build/Publish.ps1");
            Check(comun.Contains("'Actualizador firma de versión 1'") && firma.Contains("'Actualizador firma de versión 1'"), "el mismo encabezado que VersionSignature.Header");
            Check(comun.Contains("return 'Actualizador|1|' + $Archivo.Trim() + '|' + $Version.Trim() + '|' + $Sha256.Trim().ToLowerInvariant()"), "el mismo texto firmado que VersionSignature.Message");
            Check(firma.Contains("KeyLength = 3072") && firma.Contains("KeyProtection = 'ProtectHigh'") && firma.Contains("Cert:\\CurrentUser\\My"), "RSA 3072 con contraseña, en el almacén de la cuenta");
            Check(firma.Contains("RSASignaturePadding]::Pkcs1") && firma.Contains("HashAlgorithmName]::SHA256"), "PKCS#1 v1.5 con SHA-256, como verifica el programa");
            Check(firmar.Contains("Test-FirmaEntrega -Texto $texto") && firmar.IndexOf("Test-FirmaEntrega -Texto $texto", StringComparison.Ordinal) < firmar.IndexOf("'release', 'upload'", StringComparison.Ordinal),
                  "cada firma se verifica antes de subirla");
            Check(firmar.Contains("no coincide con su .sha256 publicado"), "no se firma lo que no coincide con su .sha256");
            Check(firmar.Contains("¿Firmar Actualizador $Version? [s/N]"), "pregunta antes de firmar (no por defecto)");
            Check(!firmar.Contains("Export-PfxCertificate") || firma.Contains("Read-Host 'Contraseña para el respaldo"), "el respaldo, con contraseña");
            Check(cmd.Contains("build\\Firmar.ps1") && Encoding.UTF8.GetByteCount(cmd) == cmd.Length, "Firmar.cmd en ASCII");
            Check(publish.Contains("-Claves @(Get-ClavesDeFirma $raiz)"), "Instalar.cmd sin SDK exige la firma si el código trae claves");
            Check(yml.Contains("Firma de versiones (clave de prueba)") && yml.Contains("ACTUALIZADOR_FIRMA_PRUEBA") && yml.Contains("-DeleteKey"), "GitHub prueba la firma con una clave de prueba que borra al final");
        }

        static void TestVersion2046()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.46") >= 0, "Actualizador 2.0.46 o posterior: " + AppInfo.Version);
        }
    }
}
