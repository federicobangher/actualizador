// Pruebas de la 2.0.17. Captura del usuario (15/11/2026): CrystalDiskInfo 9.9.4 con la 9.9.5 publicada ese día quedaba «Pendiente: Se abrió
// su página (crystalmark.info): descargá e instalá la 9.9.5 desde ahí», y la fila de Microsoft Store «Instalando…» varios minutos.
// Pedido: que los programas se actualicen solos (no abrir la página) y revisar por qué las instalaciones tardan tanto.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        const string CdiId = "CrystalDewWorld.CrystalDiskInfo";
        const string CdiHome = "https://crystalmark.info/";
        const string CdiDownloads = "https://crystalmark.info/en/download/";
        const string CdiDirect995 = "https://downloads.sourceforge.net/project/crystaldiskinfo/9.9.5/CrystalDiskInfo9_9_5.exe";
        const string CdiMirror995 = "https://netcologne.dl.sourceforge.net/project/crystaldiskinfo/9.9.5/CrystalDiskInfo9_9_5.exe?viasf=1";
        const string CdiSigner = "CN=\"Open Source Developer, Noriyuki Miyazaki\", O=Open Source Developer, C=JP";

        /// <summary>La portada de crystalmark.info (lo que importa de ella): las novedades con la versión y la barra de descarga rápida.</summary>
        const string CdiHomeHtml =
            "<html><head><title>Crystal Dew World [en]</title></head><body>" +
            "<div class=\"quick\">Quick Download <a href=\"https://crystalmark.info/redirect.php?product=CrystalDiskInfoInstallerAds\">CDI</a> " +
            "<a href=\"https://crystalmark.info/redirect.php?product=CrystalDiskInfoInstallerAoiAds\">CDI Aoi</a> " +
            "<a href=\"https://crystalmark.info/redirect.php?product=CrystalDiskInfoInstallerShizukuAds\">CDI Shizuku</a> " +
            "<a href=\"https://crystalmark.info/redirect.php?product=CrystalDiskMarkInstallerAds\">CDM</a></div>" +
            "<ul><li>[2026/11/15] <a href=\"https://crystalmark.info/en/software/crystaldiskinfo/\">CrystalDiskInfo 9.9.5</a> Release!!</li>" +
            "<li>[2026/05/24] <a href=\"https://crystalmark.info/en/software/crystaldiskmark/\">CrystalDiskMark 9.0.3</a></li></ul>" +
            "<a href=\"https://crystalmark.info/en/download/#CrystalDiskInfo\">Download</a> " +
            "<a href=\"https://crystalmark.info/en/2026/10/11/crystaldiskinfo-100-million-downloads/\">CrystalDiskInfo Reaches 100 Million Downloads!</a> " +
            "<a href=\"https://crystalmark.info/en/category/crystaldiskinfo/\">CrystalDiskInfo</a> " +
            "<a href=\"https://crystalmark.info/en/2021/03/21/crystaldiskinfo-8-12-0/\">CrystalDiskInfo 8.12.0</a>" +
            "</body></html>";

        /// <summary>Su página de descargas: cada edición con su instalador, su ZIP y el código fuente, todos por una redirección.</summary>
        const string CdiDownloadsHtml =
            "<html><head><title>Download - Crystal Dew World [en]</title></head><body><h2 id=\"CrystalDiskInfo\">CrystalDiskInfo</h2>" +
            "<table><tr><td>Standard Edition</td><td><a href=\"https://crystalmark.info/redirect.php?product=CrystalDiskInfoInstaller\">Installer</a></td>" +
            "<td><a href=\"https://crystalmark.info/redirect.php?product=CrystalDiskInfo\">ZIP</a></td>" +
            "<td><a href=\"https://crystalmark.info/redirect.php?product=CrystalDiskInfoSrc\">Source Code</a></td></tr>" +
            "<tr><td>Shizuku Edition</td><td><a href=\"https://crystalmark.info/redirect.php?product=CrystalDiskInfoInstallerShizuku\">Installer</a></td>" +
            "<td><a href=\"https://crystalmark.info/redirect.php?product=CrystalDiskInfoShizuku\">ZIP</a></td></tr>" +
            "<tr><td>Aoi Edition</td><td><a href=\"https://crystalmark.info/redirect.php?product=CrystalDiskInfoInstallerAoi\">Installer</a></td></tr>" +
            "<tr><td>Kurei Kei Edition</td><td><a href=\"https://crystalmark.info/redirect.php?product=CrystalDiskInfoInstallerKureiKei\">Installer</a></td></tr></table>" +
            "<h2 id=\"CrystalDiskMark\">CrystalDiskMark</h2><a href=\"https://crystalmark.info/redirect.php?product=CrystalDiskMarkInstaller\">Installer</a>" +
            "</body></html>";

        /// <summary>El manifiesto real de winget-pkgs para la 9.9.4 (15/11/2026), y el mismo para otra versión con otro SHA-256.</summary>
        static string CdiManifest(string version, string sha)
        {
            string under = version.Replace('.', '_');
            string url = "https://sourceforge.net/projects/crystaldiskinfo/files/" + version + "/CrystalDiskInfo" + under + ".exe/download";
            return "# Created with YamlCreate.ps1 Dumplings Mod\n# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.28.0.schema.json\n\n" +
                   "PackageIdentifier: " + CdiId + "\nPackageVersion: " + version + "\nInstallerType: inno\nScope: machine\nUpgradeBehavior: install\nReleaseDate: 2026-11-03\nInstallers:\n" +
                   "- Architecture: x86\n  InstallerUrl: " + url + "\n  InstallerSha256: " + sha + "\n" +
                   "- Architecture: x64\n  InstallerUrl: " + url + "\n  InstallerSha256: " + sha + "\n" +
                   "- Architecture: arm64\n  InstallerUrl: " + url + "\n  InstallerSha256: " + sha + "\n" +
                   "ManifestType: installer\nManifestVersion: 1.28.0\n";
        }

        static string CdiManifestUrl(string version)
        {
            return WingetRules.RawBase + "c/CrystalDewWorld/CrystalDiskInfo/" + version + "/" + CdiId + ".installer.yaml";
        }

        static InstalledProgram CdiInstalado(string version)
        {
            var p = Vc("CrystalDiskInfo " + version, version, "x64");
            p.Publisher = "Crystal Dew World";
            p.InstallLocation = @"C:\Program Files\CrystalDiskInfo\";
            p.DisplayIcon = @"C:\Program Files\CrystalDiskInfo\DiskInfo64.exe";
            p.UninstallString = "\"C:\\Program Files\\CrystalDiskInfo\\unins000.exe\"";
            p.RegistryKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CrystalDiskInfo_is1";
            p.InfoUrl = CdiHome;
            return p;
        }

        /// <summary>Un instalador de mentira: empieza como un ejecutable de Windows y lleva la marca de Inno Setup (o de ninguno).</summary>
        static byte[] InstaladorFalso(string marca, int relleno)
        {
            var b = new byte[20000];
            b[0] = (byte)'M'; b[1] = (byte)'Z';
            if (!string.IsNullOrEmpty(marca)) Encoding.ASCII.GetBytes(marca).CopyTo(b, 4096);
            b[9000] = (byte)relleno;
            return b;
        }

        static string Sha256De(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToUpperInvariant();
        }

        /// <summary>Un servidor de mentira para los pedidos por partes: redirecciones, archivos y páginas; lo que no está, 404.</summary>
        static Func<string, long, int, Http.RangeResult> Servidor2017(Dictionary<string, string> redirige, Dictionary<string, byte[]> archivos, List<string> pedidos)
        {
            return delegate(string url, long from, int count)
            {
                if (pedidos != null && from == 0) pedidos.Add(url);
                string final = url;
                for (int i = 0; i < 5 && redirige.ContainsKey(final); i++) final = redirige[final];
                byte[] data;
                if (!archivos.TryGetValue(final, out data)) throw new InvalidOperationException("404 " + final);
                var r = new Http.RangeResult();
                r.Final = new Uri(final);
                r.Partial = true;
                r.Total = data.Length;
                r.Data = Lector(data)(from, count);
                return r;
            };
        }

        // ------------------------------------------------------------------ La dirección con la versión nueva

        static void TestDireccionConLaVersionNueva2017()
        {
            Eq("https://sourceforge.net/projects/crystaldiskinfo/files/9.9.5/CrystalDiskInfo9_9_5.exe/download",
               SiteInstallers.TemplateUrl("https://sourceforge.net/projects/crystaldiskinfo/files/9.9.4/CrystalDiskInfo9_9_4.exe/download", "9.9.4", "9.9.5"),
               "CrystalDiskInfo: la versión con puntos (carpeta) y con guiones bajos (archivo)");
            Eq("https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/v8.6.9/npp.8.6.9.Installer.x64.exe",
               SiteInstallers.TemplateUrl("https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/v8.6.8/npp.8.6.8.Installer.x64.exe", "8.6.8", "8.6.9"),
               "con «v» adelante y dentro del nombre");
            Eq("https://www.rarlab.com/rar/winrar-x64-710.exe", SiteInstallers.TemplateUrl("https://www.rarlab.com/rar/winrar-x64-701.exe", "7.01", "7.10"), "sin separadores, en el nombre del archivo");
            Eq("https://www.7-zip.org/a/7z2409-x64.exe", SiteInstallers.TemplateUrl("https://www.7-zip.org/a/7z2408-x64.exe", "24.08", "24.09"), "7z2408 → 7z2409 (con el cero)");
            Eq("https://ejemplo.com/dl/app-2-5-0-setup.exe?src=web", SiteInstallers.TemplateUrl("https://ejemplo.com/dl/app-2-4-1-setup.exe?src=web", "2.4.1", "2.5.0"), "con guiones, y la consulta queda igual");
            Eq("https://ejemplo.com/9.9.5/app.exe", SiteInstallers.TemplateUrl("https://ejemplo.com/9.9.4/app.exe", "9.9.4.0", "9.9.5"), "el manifiesto dice 9.9.4.0 y la dirección 9.9.4");
            Eq("https://ejemplo.com/9.10/app_9_10.exe", SiteInstallers.TemplateUrl("https://ejemplo.com/9.9.4/app_9_9_4.exe", "9.9.4", "9.10"), "otra cantidad de partes");
            Check(SiteInstallers.TemplateUrl("https://ejemplo.com/download/latest/setup.exe", "9.9.4", "9.9.5") == null, "una dirección sin la versión: no hay nada que cambiar");
            Check(SiteInstallers.TemplateUrl("https://ejemplo.com/19.9.4/app-9.9.41.exe", "9.9.4", "9.9.5") == null, "no un pedazo de otro número (19.9.4, 9.9.41)");
            Check(SiteInstallers.TemplateUrl("https://ejemplo.com/a/9.9.4.1/app.exe", "9.9.4", "9.9.5") == null, "ni el principio de una versión más larga (9.9.4.1)");
            Check(SiteInstallers.TemplateUrl("https://ejemplo.com/7012/setup.exe", "7.01", "7.10") == null, "sin separadores sólo en el nombre del archivo, no en una carpeta");
            Check(SiteInstallers.TemplateUrl("https://ejemplo.com/app-701-701.exe", "7.01", "7.10") == null, "sin separadores y dos veces en el nombre: no se adivina");
            Check(SiteInstallers.TemplateUrl("https://ejemplo.com/9.9.4/app.exe", "9.9.4", "9.9.4") == null, "la misma versión: nada");
            Check(SiteInstallers.TemplateUrl("https://ejemplo.com/9.9.4/app.exe", "9.9.4-beta", "9.9.5") == null && SiteInstallers.TemplateUrl("https://ejemplo.com/9.9.4/app.exe", "9.9.4", "última") == null &&
                  SiteInstallers.TemplateUrl(null, "1.0", "1.1") == null && SiteInstallers.TemplateUrl("https://ejemplo.com/9/app.exe", "9", "10") == null,
                  "versiones que no son sólo números y puntos (o de una sola parte): no se arma nada");
            Check(SiteInstallers.TemplateUrl("https://ejemplo.com/1.2.3/app.exe", "1.2.3", "$1.2.4") == null, "nada raro en la versión nueva");

            Eq(CdiDirect995, SiteInstallers.SourceForgeDirect("https://sourceforge.net/projects/crystaldiskinfo/files/9.9.5/CrystalDiskInfo9_9_5.exe/download"),
               "SourceForge: la dirección directa (la otra muestra una página de espera)");
            Eq("https://downloads.sourceforge.net/project/sevenzip/7-Zip/24.09/7z2409-x64.exe", SiteInstallers.SourceForgeDirect("https://sourceforge.net/projects/sevenzip/files/7-Zip/24.09/7z2409-x64.exe/download?use_mirror=x"),
               "con subcarpetas y con consulta");
            Eq("https://ejemplo.com/files/x.exe/download", SiteInstallers.SourceForgeDirect("https://ejemplo.com/files/x.exe/download"), "otros sitios, igual");
            Eq("https://sourceforge.net.malo.example/projects/a/files/b.exe/download", SiteInstallers.SourceForgeDirect("https://sourceforge.net.malo.example/projects/a/files/b.exe/download"), "no un dominio parecido");

            Check(SiteInstallers.NameHasVersion("CrystalDiskInfo9_9_5.exe", "9.9.5") && SiteInstallers.NameHasVersion("npp.8.6.9.Installer.x64.exe", "8.6.9") &&
                  SiteInstallers.NameHasVersion("winrar-x64-710.exe", "7.10") && SiteInstallers.NameHasVersion("app-2-5-0-setup.exe", "2.5.0") &&
                  SiteInstallers.NameHasVersion("TightVNC-2.8.85-gpl-setup-64bit.msi", "2.8.85") && SiteInstallers.NameHasVersion("app-9.9.5.exe", "9.9.5.0"),
                  "el nombre del archivo lleva la versión (con puntos, guiones, guiones bajos o sin separadores)");
            Check(!SiteInstallers.NameHasVersion("CrystalDiskInfo9_9_4.exe", "9.9.5") && !SiteInstallers.NameHasVersion("setup.exe", "9.9.5") && !SiteInstallers.NameHasVersion("app-19.9.5.exe", "9.9.5") &&
                  !SiteInstallers.NameHasVersion("app-9.9.51.exe", "9.9.5") && !SiteInstallers.NameHasVersion("app-9.9.5.1.exe", "9.9.5") && !SiteInstallers.NameHasVersion("x64.exe", "6.4") &&
                  !SiteInstallers.NameHasVersion("", "1.0") && !SiteInstallers.NameHasVersion("a.exe", "beta"),
                  "otra versión, ninguna, o un pedazo de otro número: no");
        }

        // ------------------------------------------------------------------ Los enlaces de la página

        static void TestEnlacesDeLaPagina2017()
        {
            var cdi = new SiteVersions.NameMatcher("CrystalDiskInfo 9.9.4", "Crystal Dew World");
            Eq("", SiteInstallers.Leftover("CrystalDiskInfoInstaller", cdi, "CrystalDiskInfo 9.9.4"), "«…Installer»: el instalador de este programa");
            Eq("", SiteInstallers.Leftover("CrystalDiskInfo9_9_5", cdi, "CrystalDiskInfo 9.9.4"), "con la versión");
            Eq("", SiteInstallers.Leftover("crystaldiskinfo-setup-x64", cdi, "CrystalDiskInfo 9.9.4"), "con «setup» y la arquitectura");
            Eq("ads", SiteInstallers.Leftover("CrystalDiskInfoInstallerAds", cdi, "CrystalDiskInfo 9.9.4"), "«…InstallerAds»: otra edición (con publicidad)");
            Eq("shizuku", SiteInstallers.Leftover("crystaldiskinfoinstallershizuku", cdi, "CrystalDiskInfo 9.9.4"), "todo en minúsculas y pegado");
            Eq("kureikei", SiteInstallers.Leftover("CrystalDiskInfoInstallerKureiKei", cdi, "CrystalDiskInfo 9.9.4"), "Kurei Kei");
            Eq("src", SiteInstallers.Leftover("CrystalDiskInfoSrc", cdi, "CrystalDiskInfo 9.9.4"), "el código fuente");
            var shizuku = new SiteVersions.NameMatcher("CrystalDiskInfo 9.9.4 Shizuku Edition", "Crystal Dew World");
            Eq("", SiteInstallers.Leftover("CrystalDiskInfoInstallerShizuku", shizuku, "CrystalDiskInfo 9.9.4 Shizuku Edition"), "con la edición Shizuku instalada, ése es el suyo");
            Eq("", SiteInstallers.Leftover("setup", cdi, "CrystalDiskInfo 9.9.4"), "«setup»: no dice de qué programa es, pero tampoco otra cosa");
            Eq("pro", SiteInstallers.Leftover("MiPrograma-Pro-Setup", new SiteVersions.NameMatcher("MiPrograma", "Ejemplo SA"), "MiPrograma"), "la edición Pro no es el programa instalado");

            // La portada: sólo ofrece las ediciones con publicidad → ningún candidato; y manda a mirar la página de descargas.
            var home = SiteInstallers.Candidates(CdiHomeHtml, CdiHome, "CrystalDiskInfo 9.9.4", "Crystal Dew World", "9.9.5", false, "x64");
            Eq(0, home.Count, "portada: «CDI» es la edición con publicidad, no se toma");
            var subs = SiteInstallers.DownloadPages(CdiHomeHtml, CdiHome, "CrystalDiskInfo 9.9.4", "Crystal Dew World");
            Check(subs.Count == 2 && subs[0] == "https://crystalmark.info/en/download/" && subs[1] == "https://crystalmark.info/en/software/crystaldiskinfo/",
                  "se miran su página de descargas y la del producto, no las noticias ni las categorías: " + string.Join(" | ", subs.ToArray()));

            // La página de descargas: el instalador de la edición estándar primero; después el ZIP (que al seguirlo no es un instalador). Nunca otra edición, ni otro programa.
            var dl = SiteInstallers.Candidates(CdiDownloadsHtml, CdiDownloads, "CrystalDiskInfo 9.9.4", "Crystal Dew World", "9.9.5", false, "x64");
            Check(dl.Count == 2 && dl[0].Url == "https://crystalmark.info/redirect.php?product=CrystalDiskInfoInstaller" && !dl[0].Direct &&
                  dl[1].Url == "https://crystalmark.info/redirect.php?product=CrystalDiskInfo" && dl[0].Score > dl[1].Score,
                  "descargas: «Installer» de la edición estándar, y nada de Shizuku, Aoi, Kurei Kei, el código fuente ni CrystalDiskMark (" + dl.Count + ")");
            var dlShizuku = SiteInstallers.Candidates(CdiDownloadsHtml, CdiDownloads, "CrystalDiskInfo 9.9.4 Shizuku Edition", "Crystal Dew World", "9.9.5", false, "x64");
            Check(dlShizuku.Count >= 1 && dlShizuku[0].Url.EndsWith("product=CrystalDiskInfoInstallerShizuku") &&
                  !dlShizuku.Exists(delegate(SiteInstallers.LinkCandidate c) { return c.Url.EndsWith("product=CrystalDiskInfoInstaller"); }),
                  "con la edición Shizuku instalada: su instalador, no el de la estándar");

            // Enlaces directos: el del programa (mejor con la versión y de 64 bits); uno genérico sólo si la página es de este programa.
            string pagina = "<html><head><title>MiPrograma - Descargas</title></head><body>" +
                            "<a href=\"/files/MiPrograma-2.5.0-x64.exe\">Descargar (64 bits)</a> <a href=\"/files/MiPrograma-2.5.0-arm64.exe\">ARM64</a> " +
                            "<a href=\"/files/MiPrograma-2.5.0-portable.zip\">Portable</a> <a href=\"/files/MiPrograma-Pro-2.5.0.exe\">Pro</a> " +
                            "<a href=\"/files/MiPrograma-2.6.0-beta1.exe\">Beta</a> <a href=\"http://www.ejemplo-soft.com/files/MiPrograma-2.5.0.exe\">por HTTP</a> " +
                            "<a href=\"/files/OtroPrograma-1.0.exe\">Otro</a> <a href=\"/files/MiPrograma-2.5.0-src.exe\">fuentes</a></body></html>";
            var directos = SiteInstallers.Candidates(pagina, "https://www.ejemplo-soft.com/descargas", "MiPrograma 2.4.1", "Ejemplo Soft", "2.5.0", true, "x64");
            Check(directos.Count >= 1 && directos[0].Url == "https://www.ejemplo-soft.com/files/MiPrograma-2.5.0-x64.exe" && directos[0].Direct, "el instalador de 64 bits con la versión, primero");
            Check(!directos.Exists(delegate(SiteInstallers.LinkCandidate c) { return c.Url.Contains("portable") || c.Url.Contains("beta") || c.Url.StartsWith("http://") || c.Url.Contains("Otro") || c.Url.Contains("src"); }),
                  "nunca el portable, la beta, las fuentes, otro programa ni un enlace por HTTP");
            Check(directos.FindIndex(delegate(SiteInstallers.LinkCandidate c) { return c.Url.Contains("-Pro-"); }) > 0 && directos.FindIndex(delegate(SiteInstallers.LinkCandidate c) { return c.Url.Contains("arm64"); }) > 0,
                  "la edición Pro y el de ARM64, después del normal");
            var arm = SiteInstallers.Candidates(pagina, "https://www.ejemplo-soft.com/descargas", "MiPrograma 2.4.1", "Ejemplo Soft", "2.5.0", true, "arm64");
            Check(arm.Count >= 1 && arm[0].Url.Contains("arm64"), "en un equipo ARM64, el de ARM64 primero");
            string generica = "<html><head><title>MiPrograma</title></head><body><a href=\"https://www.ejemplo-soft.com/download/setup.exe\">Download</a></body></html>";
            Eq(1, SiteInstallers.Candidates(generica, "https://www.ejemplo-soft.com/", "MiPrograma 2.4.1", "Ejemplo Soft", "2.5.0", true, "x64").Count, "«setup.exe» en la página del programa: vale");
            Eq(0, SiteInstallers.Candidates(generica, "https://www.ejemplo-soft.com/", "MiPrograma 2.4.1", "Ejemplo Soft", "2.5.0", false, "x64").Count, "«setup.exe» en una página que no es de este programa: no");

            // Una página de espera: la redirección «meta refresh», o el único enlace al instalador de este programa.
            var mi = new SiteVersions.NameMatcher("MiPrograma 2.4.1", "Ejemplo Soft");
            Eq("https://cdn.ejemplo-soft.com/f/MiPrograma-2.5.0.exe",
               SiteInstallers.InterstitialTarget("<html><head><meta http-equiv=\"refresh\" content=\"3; url=https://cdn.ejemplo-soft.com/f/MiPrograma-2.5.0.exe\"></head><body>Tu descarga empieza…</body></html>",
                                                 "https://www.ejemplo-soft.com/thanks", mi), "meta refresh");
            Eq("https://www.ejemplo-soft.com/f/MiPrograma-2.5.0.exe",
               SiteInstallers.InterstitialTarget("<p>Si no empieza, <a href=\"/f/MiPrograma-2.5.0.exe\">hacé clic acá</a>. <a href=\"/f/OtraCosa.exe\">Oferta</a></p>", "https://www.ejemplo-soft.com/thanks", mi),
               "el único enlace a un instalador de este programa (la «oferta» no cuenta)");
            Check(SiteInstallers.InterstitialTarget("<a href=\"/f/MiPrograma-2.5.0.exe\">a</a> <a href=\"/f/MiPrograma-2.5.0-x86.exe\">b</a>", "https://www.ejemplo-soft.com/thanks", mi) == null, "dos distintos: no se adivina");
            Check(SiteInstallers.InterstitialTarget("<meta http-equiv=\"refresh\" content=\"0; url=http://cdn.ejemplo-soft.com/f/x.exe\">", "https://www.ejemplo-soft.com/thanks", mi) == null, "una redirección a HTTP sin cifrar: no");

            Eq("crystalmark.info", SiteInstallers.SiteOf(new Uri("https://www.crystalmark.info/en/")), "el sitio, sin www");
            Eq("example.com", SiteInstallers.SiteOf(new Uri("https://download.example.com/x")), "ni el subdominio de descargas");
            Eq("ejemplo.com.ar", SiteInstallers.SiteOf(new Uri("https://www.ejemplo.com.ar/")), "dominios de dos niveles (.com.ar)");
        }

        // ------------------------------------------------------------------ Por winget-pkgs

        static void TestInstaladorPorWinget2017()
        {
            var oldManifest = WingetDirect.ManifestForTests;
            var oldRange = OfficialInstaller.RangeForTests;
            var oldFetch = SiteVersions.FetchForTests;
            try
            {
                SiteVersions.FetchForTests = delegate(string u) { return null; };
                var ctx = ContextoSimulado(true);
                var p = CdiInstalado("9.9.4");
                var w = new WingetRow { Name = p.Name, Id = CdiId, Version = "9.9.4", Source = "winget" };
                var name = new SiteVersions.NameMatcher(p.Name, p.Publisher);
                var exe = InstaladorFalso("Inno Setup Setup Data (6.4.0)", 1);
                var pedidos = new List<string>();
                var manifiestos = new Dictionary<string, string> { { CdiManifestUrl("9.9.4"), CdiManifest("9.9.4", "248D99FC43041E6C9C486BEAAE3E38E7FB864D8787B31F24D7634E0032952333") } };
                WingetDirect.ManifestForTests = delegate(string u) { string t; return manifiestos.TryGetValue(u, out t) ? t : null; };
                OfficialInstaller.RangeForTests = Servidor2017(new Dictionary<string, string> { { CdiDirect995, CdiMirror995 } }, new Dictionary<string, byte[]> { { CdiMirror995, exe } }, pedidos);
                DateTime plazo = DateTime.UtcNow.AddMinutes(1);

                // --- La captura: winget-pkgs tiene la 9.9.4 y la página publica la 9.9.5 → la dirección de la 9.9.4 con la 9.9.5, que existe.
                string why;
                var lead = SiteInstallers.FromWinget(ctx, p, "9.9.5", w, plazo, out why);
                Check(lead != null, "CrystalDiskInfo 9.9.5: se ubica su instalador por la dirección del manifiesto de la 9.9.4 (" + why + ")");
                if (lead != null)
                {
                    Eq(CdiDirect995, lead.Url, "la dirección de la 9.9.5, directa (sin la página de espera de SourceForge)");
                    Eq("CrystalDiskInfo9_9_5.exe", lead.FileName, "el archivo");
                    Eq(InstallerLead.Previous, lead.Origin, "de dónde salió");
                    Check(lead.VersionConfirmed && !lead.IsMsi && lead.Sha256 == "", "lleva la versión en el nombre; sin SHA-256 (el del manifiesto es de la 9.9.4)");
                    Check(lead.Winget != null && lead.Winget.Type == "inno" && lead.Winget.Sha256 == "" && lead.Winget.Url == CdiDirect995, "con el tipo de instalador del manifiesto (Inno Setup)");
                    bool msiexec; string no;
                    Eq("/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", WingetRules.BuildArguments(lead.Winget, "x.exe", false, null, out msiexec, out no), "y los interruptores que usaría winget");
                    Check(lead.Describe().Contains("CrystalDewWorld.CrystalDiskInfo") && lead.Describe().Contains("firma digital") && lead.Describe().Contains("CrystalDiskInfo9_9_5.exe"), "el detalle dice de dónde sale y cómo se verifica");
                    Check(pedidos.Count == 1 && pedidos[0] == CdiDirect995, "un solo pedido (el principio del archivo): " + string.Join(", ", pedidos.ToArray()));
                }

                // --- winget-pkgs ya tiene la 9.9.5 (el catálogo de este equipo todavía no): ese manifiesto, con su SHA-256, sin pedir nada más.
                manifiestos[CdiManifestUrl("9.9.5")] = CdiManifest("9.9.5", Sha256De(exe));
                pedidos.Clear();
                var exacto = SiteInstallers.FromWinget(ctx, p, "9.9.5", w, plazo, out why);
                Check(exacto != null && exacto.Origin == InstallerLead.Catalog && exacto.Sha256 == Sha256De(exe).ToLowerInvariant() && exacto.Url == CdiDirect995 && exacto.VersionConfirmed,
                      "con el manifiesto de la 9.9.5 publicado: el mismo instalador y la misma comprobación que winget (" + why + ")");
                Eq(0, pedidos.Count, "sin pedidos al servidor de descargas (lo verifica el SHA-256 al instalar)");
                Check(exacto != null && exacto.Describe().Contains("SHA-256"), "el detalle lo dice");
                manifiestos.Remove(CdiManifestUrl("9.9.5"));

                // --- El archivo de la 9.9.6 todavía no está en esa dirección: no se inventa.
                var no996 = SiteInstallers.FromWinget(ctx, p, "9.9.6", w, plazo, out why);
                Check(no996 == null && why != null && why.Contains("downloads.sourceforge.net"), "no existe en esa dirección: sin instalador, y dice dónde buscó (" + why + ")");

                // --- En esa dirección hay otra cosa (una página): tampoco.
                OfficialInstaller.RangeForTests = Servidor2017(new Dictionary<string, string>(), new Dictionary<string, byte[]> { { CdiDirect995, Encoding.ASCII.GetBytes("<html><body>Not found</body></html>") } }, null);
                Check(SiteInstallers.FromWinget(ctx, p, "9.9.5", w, plazo, out why) == null && why.Contains("no es un instalador"), "una página en vez del archivo: no (" + why + ")");

                // --- winget-pkgs publica una MÁS NUEVA que la de la página: no se toca (winget la va a ofrecer solo).
                manifiestos[CdiManifestUrl("9.9.4")] = CdiManifest("9.9.7", "248D99FC43041E6C9C486BEAAE3E38E7FB864D8787B31F24D7634E0032952333").Replace("9_9_7", "9_9_7");
                Check(SiteInstallers.FromWinget(ctx, p, "9.9.5", w, plazo, out why) == null && why.Contains("9.9.7"), "winget-pkgs ya va por otra más nueva: no se arma nada (" + why + ")");
                manifiestos[CdiManifestUrl("9.9.4")] = CdiManifest("9.9.4", "248D99FC43041E6C9C486BEAAE3E38E7FB864D8787B31F24D7634E0032952333");

                // --- Una dirección que no lleva la versión («siempre la última»): sólo si el archivo dice ser de la versión publicada.
                string fija = "PackageIdentifier: Ejemplo.MiPrograma\nPackageVersion: 2.4.1\nInstallerType: nullsoft\nInstallers:\n- Architecture: x64\n  InstallerUrl: https://www.ejemplo-soft.com/download/MiPrograma-setup.exe\n" +
                              "  InstallerSha256: 248D99FC43041E6C9C486BEAAE3E38E7FB864D8787B31F24D7634E0032952333\nManifestType: installer\nManifestVersion: 1.6.0\n";
                manifiestos[WingetRules.RawBase + "e/Ejemplo/MiPrograma/2.4.1/Ejemplo.MiPrograma.installer.yaml"] = fija;
                var mi = Vc("MiPrograma 2.4.1", "2.4.1", "x64"); mi.Publisher = "Ejemplo Soft";
                var wm = new WingetRow { Name = mi.Name, Id = "Ejemplo.MiPrograma", Version = "2.4.1", Source = "winget" };
                OfficialInstaller.RangeForTests = Servidor2017(new Dictionary<string, string>(), new Dictionary<string, byte[]> { { "https://www.ejemplo-soft.com/download/MiPrograma-setup.exe", FortiPe("2.5.0.0", "2.5.0.0", 0x8000, 0x20000, true) } }, null);
                var conRecursos = SiteInstallers.FromWinget(ctx, mi, "2.5.0", wm, plazo, out why);
                Check(conRecursos != null && conRecursos.VersionConfirmed && conRecursos.Url == "https://www.ejemplo-soft.com/download/MiPrograma-setup.exe" && conRecursos.Winget.Type == "nullsoft",
                      "sin versión en la dirección, pero el archivo dice 2.5.0.0 en sus recursos: vale (" + why + ")");
                OfficialInstaller.RangeForTests = Servidor2017(new Dictionary<string, string>(), new Dictionary<string, byte[]> { { "https://www.ejemplo-soft.com/download/MiPrograma-setup.exe", FortiPe("2.4.1.0", "2.4.1.0", 0x8000, 0x20000, true) } }, null);
                Check(SiteInstallers.FromWinget(ctx, mi, "2.5.0", wm, plazo, out why) == null && why.Contains("2.5.0"), "si el archivo sigue siendo el de la 2.4.1: no (" + why + ")");

                // --- Lo que sólo sabe hacer winget (ZIP, desinstalar la anterior) y un instalador por HTTP: no.
                manifiestos[WingetRules.RawBase + "e/Ejemplo/MiPrograma/2.4.1/Ejemplo.MiPrograma.installer.yaml"] = fija.Replace("InstallerType: nullsoft", "InstallerType: nullsoft\nUpgradeBehavior: uninstallPrevious");
                Check(SiteInstallers.FromWinget(ctx, mi, "2.5.0", wm, plazo, out why) == null && why.Contains("desinstalar"), "pide desinstalar la anterior: eso lo hace winget (" + why + ")");
                manifiestos[WingetRules.RawBase + "e/Ejemplo/MiPrograma/2.4.1/Ejemplo.MiPrograma.installer.yaml"] = fija.Replace("https://www.ejemplo-soft.com/download/MiPrograma-setup.exe", "http://www.ejemplo-soft.com/2.4.1/MiPrograma-setup.exe");
                Check(SiteInstallers.FromWinget(ctx, mi, "2.5.0", wm, plazo, out why) == null && why.Contains("HTTPS"), "un instalador por HTTP sin cifrar: no (" + why + ")");

                // --- Sin fila de winget utilizable (identificador recortado, de la Store, copia portable): ni se consulta.
                InstallerLead ninguno = SiteInstallers.Find(ctx, p, "9.9.5", null, null, new WingetRow { Name = p.Name, Id = "CrystalDewWorld.CrystalDisk…", IdTruncated = true, Source = "winget" }, out why);
                Check(ninguno == null, "identificador recortado: no se usa");
                Check(SiteInstallers.Find(ctx, p, "9.9.5", null, null, new WingetRow { Name = p.Name, Id = "9PDXGNCFSCZV", Source = "msstore" }, out why) == null, "de la Store: no");
                var portable = CdiInstalado("9.9.4"); portable.Unregistered = true;
                Check(SiteInstallers.Find(ctx, portable, "9.9.5", null, null, w, out why) == null, "una copia portable (sin entrada en Aplicaciones): no se instala encima");
            }
            finally { WingetDirect.ManifestForTests = oldManifest; OfficialInstaller.RangeForTests = oldRange; SiteVersions.FetchForTests = oldFetch; }
        }

        // ------------------------------------------------------------------ La búsqueda de la captura

        static void TestCapturaCrystalDiskInfo2017()
        {
            var oldManifest = WingetDirect.ManifestForTests;
            var oldRange = OfficialInstaller.RangeForTests;
            var oldFetch = SiteVersions.FetchForTests;
            var oldSources = SiteVersions.SourcesForTests;
            bool oldFind = SiteVersions.FindInstallers;
            try
            {
                SiteVersions.SourcesForTests = new SourceList();
                var paginas = new List<string>();
                SiteVersions.FetchForTests = delegate(string u)
                {
                    paginas.Add(u);
                    if (u == CdiHome) return CdiHomeHtml;
                    if (u == CdiDownloads) return CdiDownloadsHtml;
                    if (u == "https://crystalmark.info/en/software/crystaldiskinfo/") return "<html><head><title>CrystalDiskInfo</title></head><body>CrystalDiskInfo 9.9.5</body></html>";
                    return "";
                };
                var exe = InstaladorFalso("Inno Setup Setup Data (6.4.0)", 1);
                var manifiestos = new Dictionary<string, string> { { CdiManifestUrl("9.9.4"), CdiManifest("9.9.4", "248D99FC43041E6C9C486BEAAE3E38E7FB864D8787B31F24D7634E0032952333") } };
                WingetDirect.ManifestForTests = delegate(string u) { string t; return manifiestos.TryGetValue(u, out t) ? t : null; };
                const string sfPage = "https://sourceforge.net/projects/crystaldiskinfo/files/9.9.5/CrystalDiskInfo9_9_5.exe/download";
                var redirige = new Dictionary<string, string>
                {
                    { CdiDirect995, CdiMirror995 },
                    { "https://crystalmark.info/redirect.php?product=CrystalDiskInfoInstaller", sfPage },
                    { "https://crystalmark.info/redirect.php?product=CrystalDiskInfo", "https://netcologne.dl.sourceforge.net/project/crystaldiskinfo/9.9.5/CrystalDiskInfo9_9_5.zip" }
                };
                var archivos = new Dictionary<string, byte[]>
                {
                    { CdiMirror995, exe },
                    { sfPage, Encoding.ASCII.GetBytes("<!doctype html><html><body>Your download will start shortly…</body></html>") },
                    { "https://netcologne.dl.sourceforge.net/project/crystaldiskinfo/9.9.5/CrystalDiskInfo9_9_5.zip", new byte[] { (byte)'P', (byte)'K', 3, 4, 0, 0, 0, 0, 0, 0 } }
                };
                var pedidos = new List<string>();
                OfficialInstaller.RangeForTests = Servidor2017(redirige, archivos, pedidos);

                // --- La captura: winget la da «Al día» (su catálogo va por la 9.9.4) y crystalmark.info publica la 9.9.5.
                var ctx = ContextoSimulado(true);
                var p = CdiInstalado("9.9.4");
                var fila = new UpdateCandidate { Kind = UpdateKind.Programa, Name = p.Name, Publisher = p.Publisher, InstalledVersion = "9.9.4", AvailableVersion = "9.9.4", Status = UpdateStatus.AlDia,
                                                 ProviderName = WingetProvider.ProviderId, Identifier = CdiId,
                                                 ProviderTag = new WingetRow { Name = p.Name, Id = CdiId, Version = "9.9.4", Source = "winget" } };
                var filas = new List<UpdateCandidate> { fila };
                var notas = new List<string>();
                Eq(1, SiteVersions.Resolve(ctx, filas, new List<InstalledProgram> { p }, notas), "la fila cambió");
                Check(fila.Status == UpdateStatus.Disponible && fila.AvailableVersion == "9.9.5", "disponible la 9.9.5 (" + fila.Status + " " + fila.AvailableVersion + ")");
                Check(fila.ProviderName == VendorProvider.ProviderId && fila.Installable && fila.Recommended && UpdateManager.CanInstall(fila),
                      "se instala desde acá y entra en «Marcar todo» (antes: «sitio», opcional, y abría crystalmark.info): " + fila.ProviderName);
                var plan = fila.ProviderTag as VendorPlan;
                Check(plan != null && plan.Lead != null && plan.InstallerUrl == CdiDirect995 && plan.SitePage == CdiHome && plan.Latest == "9.9.5" && plan.Lead.Origin == InstallerLead.Previous,
                      "con el instalador de la 9.9.5: " + (plan != null ? plan.InstallerUrl : "sin plan"));
                Check(fila.Detail.Contains("catálogo de winget va atrasado") && fila.Detail.Contains("CrystalDiskInfo9_9_5.exe") && fila.Detail.Contains("firma digital") && fila.Detail.Contains("se abre su página"),
                      "el detalle dice de dónde sale, cómo se verifica y qué pasa si no se puede: " + fila.Detail);
                Check(!fila.Detail.Contains("queda entre las opcionales"), "y no dice que queda opcional");
                Check(plan != null && plan.Processes.Length == 1 && plan.Processes[0] == "DiskInfo64", "se cierra antes de instalar si está abierto (queda en el área de notificación)");
                Check(!pedidos.Exists(delegate(string u) { return u.Contains("redirect.php"); }), "por winget-pkgs alcanzó: no hizo falta recorrer la página");
                Check(UpdateGroups.Of(fila) == UpdateGroup.ParaActualizar, "queda en «Para actualizar»: " + UpdateGroups.Of(fila));

                // --- Sin administrador: lo dice (está instalado para todo el equipo), no se puede marcar.
                var filaUser = new UpdateCandidate { Kind = UpdateKind.Programa, Name = p.Name, InstalledVersion = "9.9.4", Status = UpdateStatus.AlDia, ProviderName = WingetProvider.ProviderId,
                                                     ProviderTag = new WingetRow { Name = p.Name, Id = CdiId, Version = "9.9.4", Source = "winget" } };
                SiteVersions.Resolve(ContextoSimulado(false), new List<UpdateCandidate> { filaUser }, new List<InstalledProgram> { p }, new List<string>());
                Check(filaUser.Status == UpdateStatus.Disponible && !filaUser.Installable && filaUser.Detail.Contains("administrador"), "sin administrador: lo dice (" + filaUser.Detail + ")");

                // --- Un programa que winget no conoce: se recorre su página → página de descargas → la redirección → el archivo.
                pedidos.Clear(); paginas.Clear();
                var sinWinget = new UpdateCandidate { Kind = UpdateKind.Programa, Name = p.Name, Publisher = p.Publisher, InstalledVersion = "9.9.4", Status = UpdateStatus.NoVerificable, ProviderName = WingetProvider.ProviderId };
                Eq(1, SiteVersions.Resolve(ctx, new List<UpdateCandidate> { sinWinget }, new List<InstalledProgram> { p }, new List<string>()), "la fila sin winget cambió");
                var plan2 = sinWinget.ProviderTag as VendorPlan;
                Check(sinWinget.Status == UpdateStatus.Disponible && sinWinget.ProviderName == VendorProvider.ProviderId && plan2 != null && plan2.Lead != null && plan2.Lead.Origin == InstallerLead.Page,
                      "se ubicó por la página: " + sinWinget.ProviderName + " " + sinWinget.Detail);
                if (plan2 != null && plan2.Lead != null)
                {
                    Eq(CdiDirect995, plan2.InstallerUrl, "la dirección fija del archivo (la redirección pasaba por la página de espera de SourceForge)");
                    Eq(CdiDownloads, plan2.Lead.FoundAt, "enlazado desde su página de descargas");
                    Eq("CrystalDiskInfo9_9_5.exe", plan2.Lead.FileName, "el archivo");
                    Check(plan2.Lead.VersionConfirmed && sinWinget.Recommended && plan2.Lead.Winget == null, "con la versión en el nombre: recomendada; sin manifiesto de winget");
                }
                Check(paginas.Contains(CdiDownloads), "leyó la página de descargas");
                Check(pedidos.Count >= 1 && pedidos[0] == "https://crystalmark.info/redirect.php?product=CrystalDiskInfoInstaller" && !pedidos.Exists(delegate(string u) { return u.Contains("Ads") || u.Contains("Shizuku") || u.Contains("Aoi") || u.Contains("Src"); }),
                      "probó el instalador de la edición estándar y ninguna otra edición: " + string.Join(", ", pedidos.ToArray()));

                // --- Una página que no enlaza nada utilizable: como antes, abre la página; y dice por qué.
                SiteVersions.FetchForTests = delegate(string u) { return u == CdiHome ? "<html><body><p>CrystalDiskInfo 9.9.5 released</p></body></html>" : ""; };
                var sinEnlace = new UpdateCandidate { Kind = UpdateKind.Programa, Name = p.Name, InstalledVersion = "9.9.4", Status = UpdateStatus.NoVerificable, ProviderName = WingetProvider.ProviderId };
                SiteVersions.Resolve(ctx, new List<UpdateCandidate> { sinEnlace }, new List<InstalledProgram> { p }, new List<string>());
                Check(sinEnlace.Status == UpdateStatus.Disponible && sinEnlace.ProviderName == SiteProvider.ProviderId && sinEnlace.Installable && sinEnlace.Detail.Contains("se abre su página") &&
                      sinEnlace.Detail.Contains("no enlaza un instalador de este programa"),
                      "sin enlace: abre su página y dice por qué (" + sinEnlace.Detail + ")");

                // --- Apagado (SiteVersions.FindInstallers = false): como la 2.0.16.
                SiteVersions.FindInstallers = false;
                SiteVersions.FetchForTests = delegate(string u) { return u == CdiHome ? CdiHomeHtml : u == CdiDownloads ? CdiDownloadsHtml : ""; };
                var vieja = new UpdateCandidate { Kind = UpdateKind.Programa, Name = p.Name, InstalledVersion = "9.9.4", Status = UpdateStatus.AlDia, ProviderName = WingetProvider.ProviderId,
                                                  ProviderTag = new WingetRow { Name = p.Name, Id = CdiId, Version = "9.9.4", Source = "winget" } };
                SiteVersions.Resolve(ctx, new List<UpdateCandidate> { vieja }, new List<InstalledProgram> { p }, new List<string>());
                Check(vieja.ProviderName == SiteProvider.ProviderId && !vieja.Recommended, "con la búsqueda de instaladores apagada: abre la página, como antes");
            }
            finally
            {
                WingetDirect.ManifestForTests = oldManifest; OfficialInstaller.RangeForTests = oldRange; SiteVersions.FetchForTests = oldFetch;
                SiteVersions.SourcesForTests = oldSources; SiteVersions.FindInstallers = oldFind;
            }
        }

        // ------------------------------------------------------------------ La instalación

        /// <summary>La fila de la captura ya resuelta: CrystalDiskInfo 9.9.4 → 9.9.5 con el instalador ubicado.</summary>
        static UpdateCandidate FilaCdi(UpdateContext ctx, InstalledProgram p, InstallerLead lead)
        {
            var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = p.Name, Publisher = p.Publisher, InstalledVersion = p.Version, Status = UpdateStatus.AlDia, ProviderName = WingetProvider.ProviderId };
            var r = new SiteReading { Latest = "9.9.5", Url = CdiHome, Evidence = "CrystalDiskInfo 9.9.5" };
            SiteVersions.Apply(ctx, c, p, r, true, lead, null);
            return c;
        }

        static InstallerLead LeadCdi(string origin, string sha)
        {
            var w = new WingetInstaller { Architecture = "x64", Type = "inno", Scope = "machine", Url = CdiDirect995, Sha256 = sha ?? "", UpgradeBehavior = "install" };
            return new InstallerLead { Url = CdiDirect995, FileName = "CrystalDiskInfo9_9_5.exe", Origin = origin, Sha256 = sha ?? "", WingetId = CdiId, Winget = origin == InstallerLead.Page ? null : w,
                                       VersionConfirmed = true, FoundAt = origin == InstallerLead.Page ? CdiDownloads : "" };
        }

        static void TestInstalacionDesdeLaPagina2017()
        {
            var oldDownload = VendorProvider.DownloadForTests;
            var oldSig = VendorProvider.SignatureForTests;
            var oldSigners = WingetDirect.InstalledSignersForTests;
            var oldProduct = SiteVersions.ProductOfForTests;
            var oldRun = VendorProvider.RunInstallerForTests;
            var oldNow = VendorProvider.ProgramsNowForTests;
            var oldExeV = VendorProvider.ExeVersionForTests;
            var oldOpen = VendorProvider.OpenPageForTests;
            var oldPolicy = UpdateManager.PolicyForTests;
            var oldInstalled = VendorProvider.InstalledSignedForTests;
            var oldRange = OfficialInstaller.RangeForTests;
            var oldFetch = SiteVersions.FetchForTests;
            int oldWait = VendorProvider.ConfirmWaitMs, oldPoll = VendorProvider.ConfirmPollMs;
            try
            {
                VendorProvider.ConfirmWaitMs = 60; VendorProvider.ConfirmPollMs = 10;
                UpdateManager.PolicyForTests = new Policy();
                var ctx = ContextoSimulado(true);
                var instalado = CdiInstalado("9.9.4");
                ctx.Programs.Add(instalado);
                byte[] exe = InstaladorFalso("Inno Setup Setup Data (6.4.0)", 1);
                var bajadas = new List<string>();
                var ejecutados = new List<string>();
                var abiertas = new List<string>();
                string versionAhora = "9.9.4";
                bool instala = true;
                VendorProvider.DownloadForTests = delegate(string url, string file) { bajadas.Add(url); File.WriteAllBytes(file, exe); };
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(0, CdiSigner); };
                WingetDirect.InstalledSignersForTests = delegate(InstalledProgram p) { return new List<string> { "Open Source Developer", "Open Source Developer, Noriyuki Miyazaki" }; };
                SiteVersions.ProductOfForTests = delegate(string f) { return "CrystalDiskInfo"; };
                VendorProvider.ExeVersionForTests = delegate(string f) { return "9.9.5.0"; };
                VendorProvider.RunInstallerForTests = delegate(string file, string args)
                {
                    ejecutados.Add(Path.GetFileName(file) + " " + args);
                    if (instala) versionAhora = "9.9.5";
                    return new ProcResult { Started = true, ExitCode = 0 };
                };
                VendorProvider.ProgramsNowForTests = delegate { return new List<InstalledProgram> { CdiInstaladoComo(instalado, versionAhora) }; };
                VendorProvider.OpenPageForTests = delegate(string u) { abiertas.Add(u); };
                Action reiniciar = delegate { bajadas.Clear(); ejecutados.Clear(); abiertas.Clear(); versionAhora = "9.9.4"; instala = true; };
                var prov = new VendorProvider();

                // --- 1) La captura: firmado por el mismo que lo instalado → se instala en silencio con los interruptores de winget, y se confirma.
                var c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Previous, null));
                int avisos = 0;
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { avisos++; });
                Eq(UpdateStatus.Instalada, c.Status, "CrystalDiskInfo 9.9.5: instalada sin abrir ninguna página (" + c.Result + ")");
                Check(bajadas.Count == 1 && bajadas[0] == CdiDirect995, "bajó el instalador: " + string.Join(", ", bajadas.ToArray()));
                Check(ejecutados.Count == 1 && ejecutados[0] == "CrystalDiskInfo9_9_5.exe /SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", "lo ejecutó en silencio, con su nombre: " + string.Join(", ", ejecutados.ToArray()));
                Check(c.InstalledVersion == "9.9.5" && c.Result.Contains("Actualizada correctamente") && c.Result.Contains("igual que el programa instalado"), "confirmada por la versión y dice cómo se verificó: " + c.Result);
                Eq(0, abiertas.Count, "ninguna página abierta");
                Check(avisos >= 3, "la fila fue avisando el progreso (" + avisos + ")");

                // --- 2) Firmado por otro: NO se ejecuta; se abre su página (como antes) y la fila dice por qué.
                reiniciar();
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(0, "CN=Otra Empresa SRL, O=Otra Empresa SRL, C=AR"); };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Previous, null));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Pendiente && ejecutados.Count == 0 && abiertas.Count == 1 && abiertas[0] == CdiHome, "otro firmante: no se ejecuta y se abre crystalmark.info (" + c.Status + ")");
                Check(c.Result.StartsWith("No se pudo instalar sola (") && c.Result.Contains("Otra Empresa SRL") && c.Result.Contains("crystalmark.info") && c.Result.Contains("9.9.5"), "dice por qué y qué hacer: " + c.Result);
                Check(UpdateManager.CanInstall(c), "y se puede reintentar");

                // --- 3) Una firma revocada o inválida: nunca.
                reiniciar();
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(Native.CERT_E_REVOKED, CdiSigner); };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Previous, null));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(ejecutados.Count == 0 && c.Result.Contains("REVOCADO"), "certificado revocado: no se ejecuta (" + c.Result + ")");

                // --- 4) Con el manifiesto de winget-pkgs de esa versión: vale su SHA-256 aunque la firma no se pueda comparar (como winget).
                reiniciar();
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(Native.TRUST_E_NOSIGNATURE, ""); };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Catalog, Sha256De(exe).ToLowerInvariant()));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Instalada && ejecutados.Count == 1 && c.Result.Contains("SHA-256 del catálogo de winget"), "SHA-256 del catálogo: instalada (" + c.Result + ")");
                // … y si el archivo no es el del catálogo, no.
                reiniciar();
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Catalog, new string('a', 64)));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(ejecutados.Count == 0 && c.Result.Contains("no coincide con el del catálogo de winget") && abiertas.Count == 1, "otro archivo que el del catálogo: no se ejecuta (" + c.Result + ")");
                // … ni con el certificado revocado, aunque el SHA-256 coincida.
                reiniciar();
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(Native.CERT_E_REVOKED, CdiSigner); };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Catalog, Sha256De(exe).ToLowerInvariant()));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(ejecutados.Count == 0 && c.Result.Contains("REVOCADO"), "revocado: no, aunque coincida el SHA-256 (" + c.Result + ")");

                // --- 5) El autor no firma (ni lo instalado ni el instalador): vale si es este producto y EXACTAMENTE la versión publicada.
                reiniciar();
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(Native.TRUST_E_NOSIGNATURE, ""); };
                WingetDirect.InstalledSignersForTests = delegate(InstalledProgram p) { return new List<string>(); };
                VendorProvider.InstalledSignedForTests = delegate(InstalledProgram p) { return 0; };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Instalada && ejecutados.Count == 1 && c.Result.Contains("sin firma digital, como lo instalado"), "sin firma, como lo instalado, mismo producto y versión: instalada (" + c.Result + ")");
                Check(ejecutados.Count == 1 && ejecutados[0].EndsWith("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-"), "sin manifiesto: los interruptores de Inno Setup, reconocido por el archivo: " + string.Join(", ", ejecutados.ToArray()));
                // … pero no si dice otra versión, otro producto, si lo instalado sí está firmado, o si la política no lo permite.
                reiniciar();
                VendorProvider.ExeVersionForTests = delegate(string f) { return "9.9.4.0"; };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(ejecutados.Count == 0 && c.Status == UpdateStatus.Pendiente && c.Result.Contains("no dice ser de la versión 9.9.5"), "sin firma y de otra versión: no (" + c.Result + ")");
                reiniciar();
                VendorProvider.ExeVersionForTests = delegate(string f) { return "9.9.5.0"; };
                VendorProvider.InstalledSignedForTests = delegate(InstalledProgram p) { return 1; };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(ejecutados.Count == 0 && c.Result.Contains("tiene firma digital y el instalador nuevo no"), "lo instalado está firmado y el instalador no: no (" + c.Result + ")");
                reiniciar();
                VendorProvider.InstalledSignedForTests = delegate(InstalledProgram p) { return 0; };
                UpdateManager.PolicyForTests = PoliticaDe("{ \"permitirInstaladoresSinFirma\": false }");
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(ejecutados.Count == 0 && c.Result.Contains("política de la empresa no permite instaladores sin firma") && abiertas.Count == 1, "la política no permite instaladores sin firma: se abre la página (" + c.Result + ")");
                // … y si la política tampoco deja abrir páginas, queda el error.
                reiniciar();
                UpdateManager.PolicyForTests = PoliticaDe("{ \"permitirInstaladoresSinFirma\": false, \"permitirAbrirPaginas\": false }");
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && abiertas.Count == 0, "sin permiso para abrir páginas: queda el error, no se abre nada");
                UpdateManager.PolicyForTests = new Policy();

                // La regla, caso por caso.
                Check(VendorProvider.UnsignedSiteVerdict(true, 0, false, true, false, null, null, null, "9.9.5", "CrystalDiskInfo", "9.9.5.0", instalado) == null, "regla: se acepta");
                Check(VendorProvider.UnsignedSiteVerdict(true, -1, false, true, false, null, null, null, "9.9.5", "CrystalDiskInfo", "9.9.5", instalado).Contains("no se pudo comprobar"), "no se sabe si lo instalado está firmado: no");
                Check(VendorProvider.UnsignedSiteVerdict(true, 0, true, true, false, null, null, null, "9.9.5", "CrystalDiskInfo", "9.9.5", instalado).Contains("sólo para tu usuario"), "como administrador sobre un programa del usuario: no");
                Check(VendorProvider.UnsignedSiteVerdict(true, 0, false, false, false, null, null, null, "9.9.5", "CrystalDiskInfo", "9.9.5", instalado).Contains("HTTPS"), "la página se leyó por HTTP: no");
                Check(VendorProvider.UnsignedSiteVerdict(true, 0, false, true, false, null, null, null, "9.9.5", "Otro Programa", "9.9.5", instalado).Contains("ni dice ser"), "otro producto: no");
                Check(VendorProvider.UnsignedSiteVerdict(true, 0, false, true, false, null, null, null, "9.9.5", "CrystalDiskInfo", "", instalado).Contains("no dice ser de la versión"), "no dice su versión: no");
                var msi = new Dictionary<string, string> { { "ProductVersion", "9.9.5" }, { "UpgradeCode", "{11111111-2222-3333-4444-555555555555}" }, { "ProductName", "CrystalDiskInfo" } };
                Check(VendorProvider.UnsignedSiteVerdict(true, 0, false, true, true, msi, null, null, "9.9.5", "", "", instalado) == null, "un MSI de este producto y esa versión: se acepta");
                Check(VendorProvider.UnsignedSiteVerdict(true, 0, false, true, true, msi, new List<string> { "{AAAAAAAA-0000-0000-0000-000000000000}" }, "{BBBBBBBB-0000-0000-0000-000000000000}", "9.9.5", "", "", instalado).Contains("UpgradeCode"), "un MSI de otra familia de producto: no");
                msi["ProductVersion"] = "9.9.4";
                Check(VendorProvider.UnsignedSiteVerdict(true, 0, false, true, true, msi, null, null, "9.9.5", "", "", instalado).Contains("9.9.4"), "un MSI de otra versión: no");

                // --- 6) El enlace pasaba por una página de espera: lo que llega no es un instalador → se sigue la página y se baja el archivo.
                reiniciar();
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(0, CdiSigner); };
                WingetDirect.InstalledSignersForTests = delegate(InstalledProgram p) { return new List<string> { "Open Source Developer, Noriyuki Miyazaki" }; };
                const string enlace = "https://crystalmark.info/redirect.php?product=CrystalDiskInfoInstaller";
                const string espera = "https://www.ejemplo-espera.com/download/thanks";
                SiteVersions.FetchForTests = delegate(string u) { return u == espera ? "<html><head><meta http-equiv=\"refresh\" content=\"5; url=https://cdn.ejemplo-espera.com/f/CrystalDiskInfo9_9_5.exe\"></head></html>" : ""; };
                OfficialInstaller.RangeForTests = Servidor2017(new Dictionary<string, string> { { enlace, espera } },
                    new Dictionary<string, byte[]> { { espera, Encoding.ASCII.GetBytes("<!DOCTYPE html><html>…</html>") }, { "https://cdn.ejemplo-espera.com/f/CrystalDiskInfo9_9_5.exe", exe } }, null);
                VendorProvider.DownloadForTests = delegate(string url, string file)
                {
                    bajadas.Add(url);
                    File.WriteAllBytes(file, url == enlace ? Encoding.ASCII.GetBytes("<!DOCTYPE html><html>" + new string(' ', 20000) + "</html>") : exe);
                };
                var conEspera = LeadCdi(InstallerLead.Page, null); conEspera.Url = enlace;
                c = FilaCdi(ctx, instalado, conEspera);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Instalada && bajadas.Count == 2 && bajadas[1] == "https://cdn.ejemplo-espera.com/f/CrystalDiskInfo9_9_5.exe" && ejecutados.Count == 1,
                      "página de espera: se siguió y se instaló (" + c.Result + "; " + string.Join(", ", bajadas.ToArray()) + ")");
                VendorProvider.DownloadForTests = delegate(string url, string file) { bajadas.Add(url); File.WriteAllBytes(file, exe); };
                OfficialInstaller.RangeForTests = oldRange; SiteVersions.FetchForTests = oldFetch;

                // --- 7) Un instalador de otro tipo (ni Inno Setup, ni NSIS, ni MSI), ya verificado: se abre con su ventana, no la página.
                reiniciar();
                exe = InstaladorFalso(null, 2);
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Instalada && ejecutados.Count == 1 && ejecutados[0] == "CrystalDiskInfo9_9_5.exe " && abiertas.Count == 0,
                      "tipo desconocido: se ejecuta con su ventana (sin interruptores): «" + string.Join(", ", ejecutados.ToArray()) + "» " + c.Result);
                // … salvo sin nadie delante (consola, revisión programada) o si la política no permite ventanas: queda el error, sin abrir nada.
                reiniciar();
                var desatendido = new VendorProvider { Unattended = true };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                desatendido.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && ejecutados.Count == 0 && abiertas.Count == 0 && c.Result.Contains("en silencio"), "desatendido: no se abre ni una ventana ni el navegador (" + c.Result + ")");
                reiniciar();
                UpdateManager.PolicyForTests = PoliticaDe("{ \"permitirInstaladoresConVentana\": false }");
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Page, null));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(ejecutados.Count == 0 && c.Status == UpdateStatus.Pendiente && abiertas.Count == 1, "la política no permite ventanas: se abre la página, como antes (" + c.Result + ")");
                UpdateManager.PolicyForTests = new Policy();
                exe = InstaladorFalso("Inno Setup Setup Data (6.4.0)", 1);

                // --- 8) El instalador se ejecutó y no cambió nada (el programa estaba abierto): es un error del instalador; NO se abre la página.
                reiniciar();
                instala = false;
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Previous, null));
                var reloj = System.Diagnostics.Stopwatch.StartNew();
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && ejecutados.Count == 1 && abiertas.Count == 0 && c.Result.Contains("no cambió nada"), "no cambió nada: error, sin abrir la página (" + c.Result + ")");
                Check(reloj.ElapsedMilliseconds < 5000, "y no se queda esperando (" + reloj.ElapsedMilliseconds + " ms)");
                Check(VendorProvider.ConfirmWaitMs == 60, "la espera es la de la prueba");

                // --- 9) El instalador devuelve un error: lo mismo.
                reiniciar();
                VendorProvider.RunInstallerForTests = delegate(string file, string args) { ejecutados.Add(args); return new ProcResult { Started = true, ExitCode = 5 }; };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Previous, null));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && abiertas.Count == 0 && c.Result.Contains("código 5"), "el instalador falló: queda su error (" + c.Result + ")");

                // --- 10) La descarga falla (el archivo ya no está): se abre la página.
                reiniciar();
                VendorProvider.DownloadForTests = delegate(string url, string file) { throw new InvalidOperationException("404 No encontrado"); };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Previous, null));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Pendiente && abiertas.Count == 1 && c.Result.Contains("404"), "la descarga falló: se abre la página (" + c.Result + ")");

                // --- 11) Una versión más vieja que la instalada no se instala nunca.
                reiniciar();
                VendorProvider.DownloadForTests = delegate(string url, string file) { bajadas.Add(url); File.WriteAllBytes(file, exe); };
                VendorProvider.ExeVersionForTests = delegate(string f) { return "9.9.3.0"; };
                VendorProvider.RunInstallerForTests = delegate(string file, string args) { ejecutados.Add(args); return new ProcResult { Started = true, ExitCode = 0 }; };
                c = FilaCdi(ctx, instalado, LeadCdi(InstallerLead.Previous, null));
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(ejecutados.Count == 0 && c.Result.Contains("más vieja que la instalada"), "un instalador de la 9.9.3 sobre la 9.9.4: no (" + c.Result + ")");

                // --- 12) Las filas del catálogo de fabricantes (con «Check») no cambian: nunca abren una página por su cuenta.
                var planCat = new VendorPlan { Check = VendorCatalog.Find("7-zip"), SitePage = "https://www.7-zip.org/", Latest = "26.05" };
                planCat.Locating = true;
                var filaCat = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "7-Zip", Status = UpdateStatus.Fallida, Result = "x", ProviderTag = planCat };
                abiertas.Clear();
                prov.Install(ctx, new List<UpdateCandidate>(), delegate(UpdateCandidate x) { });
                Eq(0, abiertas.Count, "sin filas: nada");
            }
            finally
            {
                VendorProvider.DownloadForTests = oldDownload; VendorProvider.SignatureForTests = oldSig; WingetDirect.InstalledSignersForTests = oldSigners;
                SiteVersions.ProductOfForTests = oldProduct; VendorProvider.RunInstallerForTests = oldRun; VendorProvider.ProgramsNowForTests = oldNow;
                VendorProvider.ExeVersionForTests = oldExeV; VendorProvider.OpenPageForTests = oldOpen; UpdateManager.PolicyForTests = oldPolicy;
                VendorProvider.InstalledSignedForTests = oldInstalled; OfficialInstaller.RangeForTests = oldRange; SiteVersions.FetchForTests = oldFetch;
                VendorProvider.ConfirmWaitMs = oldWait; VendorProvider.ConfirmPollMs = oldPoll;
            }
        }

        static InstalledProgram CdiInstaladoComo(InstalledProgram p, string version)
        {
            var q = CdiInstalado(version);
            q.Name = p.Name;      // Inno Setup conserva la entrada; acá el nombre es el de la fila
            return q;
        }

        static Policy PoliticaDe(string json)
        {
            var p = new Policy();
            p.ApplyJson(json);
            return p;
        }

        // ------------------------------------------------------------------ Microsoft Store

        static void TestStoreSinEsperarDeMas2017()
        {
            // --- Cuándo se deja de medir.
            var s = TimeSpan.FromSeconds(1);
            Check(!StoreProvider.ShouldStopMeasuring(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), 0, true, 1, 0), "una sola consulta con la cola vacía no alcanza (la Store todavía puede estar buscando)");
            Check(StoreProvider.ShouldStopMeasuring(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40), 0, true, 2, 0), "dos seguidas y nada cambió: no había nada; se termina (antes: 4 minutos)");
            Check(!StoreProvider.ShouldStopMeasuring(TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(100), 1, true, 0, 2), "con apps en descarga o instalación se sigue, aunque haga rato que no cambia una versión");
            Check(!StoreProvider.ShouldStopMeasuring(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5), 3, true, 2, 0), "cola vacía pero acaba de cambiar una versión: un momento más");
            Check(StoreProvider.ShouldStopMeasuring(TimeSpan.FromSeconds(80), TimeSpan.FromSeconds(16), 3, true, 2, 0), "cola vacía y quieto: terminó");
            Check(!StoreProvider.ShouldStopMeasuring(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(90), 0, false, 0, -1), "sin poder consultar la cola: por tiempo, todavía no");
            Check(StoreProvider.ShouldStopMeasuring(TimeSpan.FromSeconds(121), TimeSpan.FromSeconds(121), 0, false, 0, -1), "sin la cola y sin cambios: 2 minutos (antes 4)");
            Check(!StoreProvider.ShouldStopMeasuring(TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(30), 2, false, 0, -1) && StoreProvider.ShouldStopMeasuring(TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(61), 2, false, 0, -1),
                  "sin la cola y con algo actualizado: un minuto sin cambios nuevos, como antes");
            Check(s.TotalSeconds == 1, "");

            // --- Lo que contesta la consulta a la cola.
            var q = StoreProvider.ParseQueue("COLA=2 PAUSA=1\r\n");
            Check(q != null && q.Active == 2 && q.Paused == 1, "«COLA=2 PAUSA=1»");
            Check(StoreProvider.ParseQueue("COLA=0 PAUSA=0") != null && StoreProvider.ParseQueue("COLA=0 PAUSA=0").Active == 0, "cola vacía");
            Check(StoreProvider.ParseQueue("COLA=? No se puede encontrar el tipo [Windows.ApplicationModel…]") == null && StoreProvider.ParseQueue("") == null && StoreProvider.ParseQueue(null) == null,
                  "si no se pudo consultar, no se inventa una cola vacía");
            Check(StoreProvider.QueueScript.Contains("AppInstallItems") && !StoreProvider.QueueScript.Contains("\"") && !StoreProvider.QueueScript.Contains("Cancel(") &&
                  !StoreProvider.QueueScript.Contains("Pause(") && !StoreProvider.QueueScript.Contains("Async"),
                  "la consulta sólo lee la cola (no inicia, pausa ni cancela nada) y no lleva comillas dobles");

            var oldTrigger = StoreProvider.TriggerForTests;
            var oldQueue = StoreProvider.QueueForTests;
            var oldQuick = StoreProvider.QuickSnapshotForTests;
            var oldSnap = StoreProvider.SnapshotForTests;
            var oldLast = StoreProvider.LastCheckForTests;
            int oldPause = StoreProvider.PollPauseMs;
            TimeSpan oldWindow = StoreProvider.MeasureWindow, oldFirst = StoreProvider.FirstQueueProbe, oldEvery = StoreProvider.QueueProbeEvery, oldIdle = StoreProvider.IdleQuiet,
                     oldBlind = StoreProvider.BlindWindow, oldQuiet = StoreProvider.QuietAfter;
            bool oldBackground = UpdateManager.StoreInBackground;
            try
            {
                StoreProvider.PollPauseMs = 4;                                   // cada vuelta, 20 ms
                StoreProvider.MeasureWindow = TimeSpan.FromSeconds(20);          // el tope: si se llega, la prueba lo nota
                StoreProvider.FirstQueueProbe = TimeSpan.FromMilliseconds(60);
                StoreProvider.QueueProbeEvery = TimeSpan.FromMilliseconds(40);
                StoreProvider.IdleQuiet = TimeSpan.FromMilliseconds(50);
                StoreProvider.BlindWindow = TimeSpan.FromMilliseconds(400);
                StoreProvider.QuietAfter = TimeSpan.FromMilliseconds(200);
                var versiones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "Microsoft.WindowsTerminal", "1.21.0.0" }, { "Microsoft.Paint", "11.2410.0.0" } };
                Func<Dictionary<string, string>> foto = delegate { lock (versiones) return new Dictionary<string, string>(versiones, StringComparer.OrdinalIgnoreCase); };
                StoreProvider.TriggerForTests = delegate { return true; };
                StoreProvider.QuickSnapshotForTests = foto;
                StoreProvider.SnapshotForTests = foto;
                int anotado = -1;
                StoreProvider.LastCheckForTests = delegate(int n) { anotado = n; };
                var ctx = ContextoSimulado(true);
                Func<UpdateCandidate> todas = delegate
                {
                    return new UpdateCandidate { Kind = UpdateKind.Store, Name = "Todas las apps de Microsoft Store", Identifier = StoreProvider.AllAppsId, ProviderName = StoreProvider.ProviderId,
                                                 Installable = true, Status = UpdateStatus.Pendiente };
                };

                // --- 1) La Store no tiene nada (lo más común): se termina enseguida, «al día». Antes: los 4 minutos enteros.
                int consultas = 0;
                StoreProvider.QueueForTests = delegate { consultas++; return new StoreProvider.StoreQueue(); };
                var fila = todas();
                var reloj = System.Diagnostics.Stopwatch.StartNew();
                new StoreProvider().Install(ctx, new List<UpdateCandidate> { fila }, delegate(UpdateCandidate u) { });
                Check(fila.Status == UpdateStatus.AlDia && fila.Result.Contains("no instaló nada"), "sin novedades: al día (" + fila.Result + ")");
                Check(reloj.ElapsedMilliseconds < 5000, "sin esperar el tope de la medición (" + reloj.ElapsedMilliseconds + " ms de 20000)");
                Check(consultas == 2, "con dos consultas a la cola (" + consultas + ")");
                Eq(0, anotado, "y queda anotado que no había nada");

                // --- 2) La Store tiene 2 apps en curso: se espera a que termine la cola, se ve lo que cambió y se termina.
                consultas = 0; anotado = -1;
                lock (versiones) { versiones["Microsoft.WindowsTerminal"] = "1.21.0.0"; versiones["Microsoft.Paint"] = "11.2410.0.0"; }
                StoreProvider.QueueForTests = delegate
                {
                    consultas++;
                    if (consultas == 1) return new StoreProvider.StoreQueue { Active = 2 };
                    if (consultas == 2) { lock (versiones) versiones["Microsoft.WindowsTerminal"] = "1.22.0.0"; return new StoreProvider.StoreQueue { Active = 1 }; }
                    if (consultas == 3) { lock (versiones) versiones["Microsoft.Paint"] = "11.2411.0.0"; return new StoreProvider.StoreQueue(); }
                    return new StoreProvider.StoreQueue();
                };
                fila = todas();
                var textos = new List<string>();
                reloj.Restart();
                new StoreProvider().Install(ctx, new List<UpdateCandidate> { fila }, delegate(UpdateCandidate u) { lock (textos) textos.Add(u.Result ?? ""); });
                Check(fila.Status == UpdateStatus.Instalada && fila.Result.StartsWith("2 apps actualizadas") && fila.Result.Contains("Microsoft.WindowsTerminal 1.21.0.0 → 1.22.0.0"), "dos apps actualizadas: " + fila.Result);
                Check(textos.Exists(delegate(string t) { return t.Contains("en descarga o instalación"); }), "mientras tanto la fila dice cuántas tiene en curso");
                Check(consultas >= 4 && reloj.ElapsedMilliseconds < 5000, "se esperó a que la cola quedara vacía dos veces, sin llegar al tope (" + consultas + " consultas, " + reloj.ElapsedMilliseconds + " ms)");
                Eq(2, anotado, "anotadas las dos");

                // --- 3) No se puede consultar la cola (un Windows sin esa interfaz): por tiempo, con el plazo corto si no cambió nada.
                consultas = 0; anotado = -1;
                StoreProvider.QueueForTests = delegate { consultas++; return null; };
                fila = todas();
                reloj.Restart();
                new StoreProvider().Install(ctx, new List<UpdateCandidate> { fila }, delegate(UpdateCandidate u) { });
                Check(fila.Status == UpdateStatus.AlDia && consultas == 2, "sin la cola: dos intentos y se sigue por tiempo (" + consultas + ")");
                Check(reloj.ElapsedMilliseconds >= 350 && reloj.ElapsedMilliseconds < 5000, "termina al plazo sin la cola (400 ms en la prueba; 2 minutos de verdad): " + reloj.ElapsedMilliseconds + " ms");

                // --- 4) En la tanda: la Store se mide MIENTRAS se instala lo demás (antes, lo demás esperaba a que terminara).
                var orden = new List<string>();
                consultas = 0;
                StoreProvider.FirstQueueProbe = TimeSpan.FromMilliseconds(250);          // la Store tarda ~350 ms en esta prueba
                StoreProvider.QueueForTests = delegate { lock (orden) orden.Add("store: consulta"); return new StoreProvider.StoreQueue(); };
                StoreProvider.TriggerForTests = delegate { lock (orden) orden.Add("store: empieza"); return true; };
                var winget = new ProveedorDePrueba(WingetProvider.ProviderId, orden, 30);
                var fabricante = new ProveedorDePrueba(VendorProvider.ProviderId, orden, 30);
                var wu = new ProveedorDePrueba(WindowsUpdateProvider.ProviderId, orden, 30);
                var man = new UpdateManager(new IUpdateProvider[] { winget, fabricante, new StoreProvider(), wu });
                man.HistoryPath = ""; man.RepeatUntilDone = false;
                var rep = UpdateSession.Create(ctx);
                var fw = FilaDePrueba("Programa de winget", WingetProvider.ProviderId);
                var fv = FilaDePrueba("Programa del fabricante", VendorProvider.ProviderId);
                var fu = FilaDePrueba("Actualización de Windows", WindowsUpdateProvider.ProviderId); fu.Kind = UpdateKind.WindowsUpdate;
                var fs = todas();
                rep.Updates.AddRange(new[] { fs, fu, fv, fw });
                UpdateManager.StoreInBackground = true;
                reloj.Restart();
                man.Install(ctx, rep, new List<UpdateCandidate> { fs, fu, fv, fw }, false, delegate(UpdateCandidate x) { });
                long conStore = reloj.ElapsedMilliseconds;
                List<string> pasos;
                lock (orden) pasos = new List<string>(orden);
                int iWinget = pasos.IndexOf("winget: termina"), iStore = pasos.IndexOf("store: empieza"), iFab = pasos.IndexOf("fabricante: empieza"), iWu = pasos.IndexOf("windowsupdate: termina"),
                    iCola = pasos.IndexOf("store: consulta");
                Check(iWinget >= 0 && iStore > iWinget, "la Store empieza después de winget (winget es una app de la Store): " + string.Join(" · ", pasos.ToArray()));
                Check(iFab > iWinget && iWu > iFab && iCola > iWu, "y lo demás se instala mientras la Store trabaja: terminó antes de la primera consulta a su cola (" + string.Join(" · ", pasos.ToArray()) + ")");
                Check(fs.Status == UpdateStatus.AlDia && fw.Status == UpdateStatus.Instalada && fv.Status == UpdateStatus.Instalada && fu.Status == UpdateStatus.Instalada, "todo terminó: " + fs.Status + " " + fw.Status + " " + fv.Status + " " + fu.Status);
                Check(conStore < 5000, "la tanda termina cuando termina la Store (" + conStore + " ms)");

                // … y con «Detener», la Store no se queda midiendo.
                var ctxCancel = ContextoSimulado(true);
                StoreProvider.FirstQueueProbe = TimeSpan.FromSeconds(10);
                StoreProvider.BlindWindow = TimeSpan.FromSeconds(10);
                var lento = new ProveedorDePrueba(VendorProvider.ProviderId, new List<string>(), 150, delegate { ctxCancel.RequestCancel(); });
                var man2 = new UpdateManager(new IUpdateProvider[] { lento, new StoreProvider() });
                man2.HistoryPath = ""; man2.RepeatUntilDone = false;
                var fs2 = todas(); var fv2 = FilaDePrueba("Programa del fabricante", VendorProvider.ProviderId);
                var rep2 = UpdateSession.Create(ctxCancel); rep2.Updates.AddRange(new[] { fs2, fv2 });
                reloj.Restart();
                man2.Install(ctxCancel, rep2, new List<UpdateCandidate> { fs2, fv2 }, false, delegate(UpdateCandidate x) { });
                Check(reloj.ElapsedMilliseconds < 5000 && fs2.Status == UpdateStatus.Pendiente && fs2.Result.Contains("Cancelada"), "cancelado: la Store deja de medir enseguida (" + reloj.ElapsedMilliseconds + " ms, " + fs2.Result + ")");
            }
            finally
            {
                StoreProvider.TriggerForTests = oldTrigger; StoreProvider.QueueForTests = oldQueue; StoreProvider.QuickSnapshotForTests = oldQuick; StoreProvider.SnapshotForTests = oldSnap;
                StoreProvider.LastCheckForTests = oldLast; StoreProvider.PollPauseMs = oldPause; StoreProvider.MeasureWindow = oldWindow; StoreProvider.FirstQueueProbe = oldFirst;
                StoreProvider.QueueProbeEvery = oldEvery; StoreProvider.IdleQuiet = oldIdle; StoreProvider.BlindWindow = oldBlind; StoreProvider.QuietAfter = oldQuiet;
                UpdateManager.StoreInBackground = oldBackground;
            }
        }

        static UpdateCandidate FilaDePrueba(string name, string provider)
        {
            return new UpdateCandidate { Kind = UpdateKind.Programa, Name = name, ProviderName = provider, Installable = true, Status = UpdateStatus.Disponible, InstalledVersion = "1.0", AvailableVersion = "2.0" };
        }

        /// <summary>Un proveedor que «instala» en unos milisegundos y anota cuándo empieza y cuándo termina.</summary>
        sealed class ProveedorDePrueba : IUpdateProvider
        {
            readonly string id;
            readonly List<string> orden;
            readonly int ms;
            readonly Action alTerminar;
            public ProveedorDePrueba(string id, List<string> orden, int ms) : this(id, orden, ms, null) { }
            public ProveedorDePrueba(string id, List<string> orden, int ms, Action alTerminar) { this.id = id; this.orden = orden; this.ms = ms; this.alTerminar = alTerminar; }
            public string Name { get { return id; } }
            public string Unavailable(UpdateContext ctx) { return null; }
            public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes) { }
            public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed)
            {
                lock (orden) orden.Add(id + ": empieza");
                Thread.Sleep(ms);
                foreach (var c in items) { c.Status = UpdateStatus.Instalada; c.Result = "Actualizada correctamente"; changed(c); }
                lock (orden) orden.Add(id + ": termina");
                if (alTerminar != null) alTerminar();
            }
        }

        // ------------------------------------------------------------------ Las esperas después de instalar

        static void TestEsperasDespuesDeInstalar2017()
        {
            Check(VendorProvider.ConfirmWaitMs <= 20000 && VendorProvider.ConfirmPollMs <= 1000, "instalador oficial: como mucho 20 s esperando la versión (antes 90 s), mirando cada segundo");
            Check(WingetProvider.ConfirmWaitMs <= 20000 && WingetProvider.ConfirmPollMs <= 2000, "winget: como mucho 20 s mirando el registro (antes, hasta 8 consultas a winget con 4 s de pausa)");
            Check(StoreProvider.MeasureWindow <= TimeSpan.FromMinutes(4) && StoreProvider.BlindWindow <= TimeSpan.FromMinutes(2) && StoreProvider.FirstQueueProbe <= TimeSpan.FromSeconds(20) &&
                  StoreProvider.QueueProbeEvery <= TimeSpan.FromSeconds(15) && StoreProvider.IdleQuiet <= TimeSpan.FromSeconds(15),
                  "Microsoft Store sin novedades: menos de un minuto (antes, siempre 4)");
            Check(SiteInstallers.Budget <= TimeSpan.FromSeconds(60) && SiteInstallers.MaxSubPages <= 3 && SiteInstallers.MaxProbes <= 5, "ubicar un instalador no alarga la búsqueda: con tope de tiempo, de páginas y de pedidos");

            // La política: se lee de politica.json y del registro, y figura en el resumen.
            var p = PoliticaDe("{ \"permitirInstaladoresSinFirma\": false }");
            Check(!p.AllowUnsignedSiteInstallers && !p.IsEmpty && p.Describe().Contains("sin instaladores sin firma digital"), "permitirInstaladoresSinFirma: " + p.Describe());
            Check(new Policy().AllowUnsignedSiteInstallers && new Policy().IsEmpty, "de fábrica: permitido");
        }
    }
}
