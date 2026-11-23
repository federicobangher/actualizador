// Pruebas de la 2.0.12: los programas se comparan y se actualizan con su PÁGINA OFICIAL. Captura del usuario (07/11/2026):
// 7-Zip 26.05 quedaba «Pendiente» con una 26.06 que había aparecido en github.com/ip7z/7zip y que 7-zip.org todavía no
// publicaba, y «Actualizar» abría la página de GitHub en vez de instalar.
using System;
using System.Collections.Generic;
using Actualizador.Core;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        const string SieteZipPagina = "https://www.7-zip.org/download.html";
        const string SieteZipApi2605 = "https://api.github.com/repos/ip7z/7zip/releases/tags/26.05";

        static InstalledProgram SieteZip(string version, string arch, bool msi)
        {
            string nombre = "7-Zip " + version + (arch == "x64" ? (msi ? " (x64 edition)" : " (x64)") : arch == "arm64" ? " (arm64)" : "");
            var p = Vc(nombre, version, arch);
            p.Publisher = "Igor Pavlov";
            p.InstallLocation = arch == "x86" ? @"C:\Program Files (x86)\7-Zip\" : @"C:\Program Files\7-Zip\";
            p.InfoUrl = "https://www.7-zip.org/";
            p.UninstallString = msi ? "MsiExec.exe /I{23170F69-40C1-2702-2605-000001000000}" : p.InstallLocation + "Uninstall.exe";
            p.RegistryKey = msi ? @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{23170F69-40C1-2702-2605-000001000000}"
                                : @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip";
            return p;
        }

        static UpdateCandidate FilaDe(List<UpdateCandidate> filas, string nombre)
        {
            return filas.Find(delegate(UpdateCandidate c) { return c.Name == nombre; });
        }

        static void TestSieteZipPaginaOficial2012()
        {
            var sz = VendorCatalog.Find("7-zip");
            Check(sz != null, "7-Zip en el catálogo de fuentes oficiales");
            if (sz == null) return;
            Eq(SieteZipPagina, sz.Url, "la versión sale de 7-zip.org");
            Check(sz.Install == VendorInstall.SignedInstaller && sz.UnsignedWithPublishedHash && sz.HashRepo == "ip7z/7zip", "se instala verificado por el SHA-256 que publica el autor");

            // --- Qué entradas son 7-Zip (el instalador .exe, el .msi, 32 y 64 bits, ARM).
            foreach (string n in new[] { "7-Zip 26.05 (x64)", "7-Zip 26.05", "7-Zip 26.05 (x64 edition)", "7-Zip 26.05 (arm64)", "7-Zip" })
                Eq("7-zip", VendorCatalog.Match(Vc(n, "26.05", "x64")) == null ? null : VendorCatalog.Match(Vc(n, "26.05", "x64")).Id, "«" + n + "» es 7-Zip");
            foreach (string otro in new[] { "7-Zip ZS 24.09 (x64)", "7-Zip-zstd 24.09", "NanaZip", "PeaZip 10.7.0 (WINDOWS)", "7-Zip Theme Manager" })
                Eq(null, VendorCatalog.Match(Vc(otro, "1.0", "x64")), otro + ": otro programa");

            // --- De dónde se acepta qué.
            Check(sz.Allows(new Uri(SieteZipPagina)) && sz.Allows(new Uri("https://7-zip.org/")), "la página oficial");
            Check(sz.Allows(new Uri("https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.exe")), "el instalador que enlaza 7-zip.org (lo aloja el autor en GitHub)");
            Check(sz.Allows(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/x?sp=r")), "la redirección de la descarga de GitHub");
            Check(!sz.Allows(new Uri("https://github.com/otro/7zip/releases/download/26.05/7z2605-x64.exe")), "otro proyecto de GitHub: no");
            Check(!sz.Allows(new Uri("https://github.com/ip7z/7zip/releases/download/")), "sin archivo: no");
            Check(!sz.Allows(new Uri("https://github.com/ip7z/7zip")), "la página del proyecto en GitHub no es la oficial");
            Check(!sz.Allows(new Uri("https://7-zip.org.evil.example/a/7z2605-x64.exe")) && !sz.Allows(new Uri("http://www.7-zip.org/a/7z2605-x64.exe")), "imitación o sin HTTPS: no");

            // --- La página: la versión publicada y el instalador de cada arquitectura.
            string page = Sample("7zip_download.html");
            string url;
            Eq("26.05", VendorCatalog.ParsePage(sz, page, out url), "7-zip.org publica la 26.05 (la de un script no cuenta)");
            Eq(null, VendorCatalog.ParsePage(sz, "<p><b>Download 7-Zip 26.06 beta (2026-11-01)</b></p>", out url), "una beta no es la publicada");
            Eq("26.05", VendorCatalog.ParsePage(sz, "<b>Download&nbsp;7-Zip 26.05</b> (2026-10-21)", out url), "con &nbsp; y la fecha fuera de la negrita");
            Eq("26.05", VendorCatalog.ParsePage(sz, "<b>Download 7-Zip 26.06 beta (2026-11-01)</b> ............................................................ <b>Download 7-Zip 26.05 (2026-10-21) for Windows</b>", out url), "con una beta arriba: la estable");
            Eq("https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.exe", VendorCatalog.PickArchLink(sz, page, "26.05", "x64", false), "x64 (.exe)");
            Eq("https://github.com/ip7z/7zip/releases/download/26.05/7z2605.exe", VendorCatalog.PickArchLink(sz, page, "26.05", "x86", false), "x86 (.exe)");
            Eq("https://github.com/ip7z/7zip/releases/download/26.05/7z2605-arm64.exe", VendorCatalog.PickArchLink(sz, page, "26.05", "arm64", false), "ARM64");
            Eq("https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.msi", VendorCatalog.PickArchLink(sz, page, "26.05", "x64", true), "instalado con el .msi: el .msi");
            Eq(null, VendorCatalog.PickArchLink(sz, page, "26.05", "arm64", true), "no hay .msi para ARM64: no se mezcla con el .exe");
            Eq("https://www.7-zip.org/a/7z2301-x64.exe", VendorCatalog.PickArchLink(sz, page, "23.01", "x64", false), "enlace relativo de la página: se completa con 7-zip.org");
            Eq(null, VendorCatalog.PickArchLink(sz, page, "26.06", "x64", false), "un archivo de otra versión no sirve");
            Eq("2605", VendorCatalog.VersionDigits("26.05"), "26.05 → 2605");

            // --- El SHA-256 que publica el autor para ESE archivo.
            string file;
            Eq(SieteZipApi2605, VendorCatalog.HashQueryUrl(sz, "https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.exe", "26.05", out file), "consulta de la versión 26.05");
            Eq("7z2605-x64.exe", file, "del archivo enlazado");
            Eq(SieteZipApi2605, VendorCatalog.HashQueryUrl(sz, "https://www.7-zip.org/a/7z2605-x64.exe", "26.05", out file), "enlace a 7-zip.org: la versión publicada");
            Eq(null, VendorCatalog.HashQueryUrl(sz, "https://github.com/ip7z/7zip/releases/download/26.05%2F..%2Fx/7z2605-x64.exe", "26.05", out file), "etiqueta rara: no se consulta");
            string rel = Sample("github_release_7zip_2605.json");
            Eq("aaaa0000111122223333444455556666777788889999aaaabbbbccccddddeeee", VendorCatalog.PublishedSha256(rel, "7z2605-x64.exe"), "SHA-256 del instalador x64");
            Eq(null, VendorCatalog.PublishedSha256(rel, "7z2605-arm64.exe"), "sin SHA-256 publicado: null");
            Eq(null, VendorCatalog.PublishedSha256(rel, "otro.exe"), "otro archivo: null");
            Eq(null, VendorCatalog.PublishedSha256(rel.Replace("\"prerelease\":false", "\"prerelease\":true"), "7z2605-x64.exe"), "de una versión preliminar: no");

            // --- Sin firma: se acepta sólo si no tiene NINGUNA firma, el hash coincidió y el instalador es de 7-Zip.
            Check(VendorProvider.UnsignedAccepted(sz, Native.TRUST_E_NOSIGNATURE, true, "7-Zip"), "sin firma, hash igual, producto 7-Zip: se instala");
            Check(VendorProvider.UnsignedAccepted(sz, Native.TRUST_E_NOSIGNATURE, true, ""), ".msi (no se lee el producto): vale el hash");
            Check(!VendorProvider.UnsignedAccepted(sz, Native.TRUST_E_NOSIGNATURE, false, "7-Zip"), "sin el hash publicado: no");
            Check(!VendorProvider.UnsignedAccepted(sz, Native.CERT_E_REVOKED, true, "7-Zip"), "firma revocada: no");
            Check(!VendorProvider.UnsignedAccepted(sz, unchecked((int)0x80096010), true, "7-Zip"), "firma presente pero alterada: no");
            Check(!VendorProvider.UnsignedAccepted(sz, Native.TRUST_E_NOSIGNATURE, true, "WinRAR"), "dice ser otro programa: no");
            Check(!VendorProvider.UnsignedAccepted(VendorCatalog.Find("gom-player"), Native.TRUST_E_NOSIGNATURE, true, "GOM Player"), "los demás siguen exigiendo firma");
        }

        static void TestSieteZipCaptura2012()
        {
            var sz = VendorCatalog.Find("7-zip");
            string page = Sample("7zip_download.html");
            string rel = Sample("github_release_7zip_2605.json");
            var old = VendorProvider.FetchForTests;
            try
            {
                VendorProvider.FetchForTests = delegate(string u) { return u == SieteZipPagina ? page : u == SieteZipApi2605 ? rel : null; };

                // --- La captura: 7-Zip 26.05 instalado y 7-zip.org publica la 26.05 → al día, aunque GitHub tenga una 26.06.
                var ctx = ContextoSimulado(true, SieteZip("26.05", "x64", false));
                var output = new List<UpdateCandidate>();
                new VendorProvider().Scan(ctx, output, new List<string>());
                var row = FilaDe(output, "7-Zip 26.05 (x64)");
                Check(row != null && row.Status == UpdateStatus.AlDia, "7-Zip 26.05: al día según 7-zip.org");
                if (row != null)
                {
                    Eq("26.05", row.AvailableVersion, "la que publica 7-zip.org");
                    Check(row.Source.Contains("7-zip.org") && row.InfoUrl.StartsWith("https://www.7-zip.org/"), "origen y sitio: 7-zip.org");
                    Eq(null, ReleaseCrossCheck.RepoOfVendorRow(row), "no se contrasta con GitHub");
                }

                // La fila de winget («Al día», su catálogo en la 26.05) desaparece: manda la página oficial.
                var wg = FilaWinget("7-Zip 26.05 (x64)", "7zip.7zip", "26.05", "26.05");
                wg.Status = UpdateStatus.AlDia; wg.Installable = false;
                var all = new List<UpdateCandidate> { wg, row };
                UpdateManager.DeduplicateVendor(all);
                Check(all.Count == 1 && all[0] == row, "una sola fila: la de 7-zip.org");

                // Si winget (o GitHub) tuviera una más nueva que 7-zip.org todavía no publica: tampoco se ofrece.
                var wgNueva = FilaWinget("7-Zip 26.05 (x64)", "7zip.7zip", "26.05", "26.06");
                var row2 = new VendorProvider(); var out2 = new List<UpdateCandidate>();
                row2.Scan(ContextoSimulado(true, SieteZip("26.05", "x64", false)), out2, new List<string>());
                var v2 = FilaDe(out2, "7-Zip 26.05 (x64)");
                var all2 = new List<UpdateCandidate> { wgNueva, v2 };
                UpdateManager.DeduplicateVendor(all2);
                Check(all2.Count == 1 && all2[0] == v2 && v2.Status == UpdateStatus.AlDia, "winget con la 26.06 que 7-zip.org no publica: queda al día");
                Check(v2.Detail.Contains("26.06") && v2.Detail.Contains("página oficial"), "y lo explica: " + v2.Detail);

                // --- Con la 26.04 instalada: disponible la 26.05, instalable desde el enlace de 7-zip.org, con el SHA-256 del autor.
                var out3 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, SieteZip("26.04", "x64", false)), out3, new List<string>());
                var r3 = FilaDe(out3, "7-Zip 26.04 (x64)");
                Check(r3 != null && r3.Status == UpdateStatus.Disponible && r3.Installable && r3.Recommended, "7-Zip 26.04: disponible la 26.05, marcable");
                if (r3 != null)
                {
                    var plan = (VendorPlan)r3.ProviderTag;
                    Eq("https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.exe", plan.InstallerUrl, "el instalador que enlaza 7-zip.org");
                    Eq("aaaa0000111122223333444455556666777788889999aaaabbbbccccddddeeee", plan.InstallerSha256, "con el SHA-256 que publica el autor");
                    Eq(VendorProvider.ProviderId, r3.ProviderName, "se instala desde acá (no se abre ninguna página)");
                    Check(r3.Detail.Contains("7-zip.org") && r3.Detail.Contains("SHA-256"), "el detalle dice de dónde y cómo se verifica");
                    Check(!r3.Detail.Contains("Se abrió"), "no abre GitHub");
                }

                // winget con la MISMA versión que 7-zip.org: se usa winget (trae el hash de su catálogo), como antes.
                var wgMisma = FilaWinget("7-Zip 26.04 (x64)", "7zip.7zip", "26.04", "26.05");
                var all3 = new List<UpdateCandidate> { wgMisma, r3 };
                UpdateManager.DeduplicateVendor(all3);
                Check(all3.Count == 1 && all3[0] == wgMisma, "winget ofrece la misma 26.05: se instala con winget");
                // winget atrasado (ofrece una más vieja): manda la de 7-zip.org.
                var out4 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, SieteZip("26.01", "x64", false)), out4, new List<string>());
                var r4 = FilaDe(out4, "7-Zip 26.01 (x64)");
                var wgVieja = FilaWinget("7-Zip 26.01 (x64)", "7zip.7zip", "26.01", "26.03");
                var all4 = new List<UpdateCandidate> { wgVieja, r4 };
                UpdateManager.DeduplicateVendor(all4);
                Check(all4.Count == 1 && all4[0] == r4 && r4.AvailableVersion == "26.05", "winget atrasado (26.03): la 26.05 de 7-zip.org");

                // --- Instalado con el .msi: se usa el .msi; de 32 bits, el de 32 bits.
                var out5 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, SieteZip("26.04", "x64", true), SieteZip("26.04", "x86", false)), out5, new List<string>());
                var msi = FilaDe(out5, "7-Zip 26.04 (x64 edition)");
                var x86 = FilaDe(out5, "7-Zip 26.04");
                Check(msi != null && ((VendorPlan)msi.ProviderTag).InstallerUrl.EndsWith("/7z2605-x64.msi") && msi.Installable, "el .msi para lo instalado con Windows Installer");
                Check(x86 != null && ((VendorPlan)x86.ProviderTag).InstallerUrl.EndsWith("/7z2605.exe") && x86.Installable, "el de 32 bits para el de 32 bits");

                // --- Sin el SHA-256 publicado (GitHub no contesta): no se instala sin firma; se explica.
                VendorProvider.FetchForTests = delegate(string u) { return u == SieteZipPagina ? page : null; };
                var out6 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, SieteZip("26.04", "x64", false)), out6, new List<string>());
                var sinHash = FilaDe(out6, "7-Zip 26.04 (x64)");
                Check(sinHash != null && sinHash.Status == UpdateStatus.Disponible && !sinHash.Installable, "sin el SHA-256 publicado: disponible, no se instala sola");
                Check(sinHash != null && sinHash.Detail.Contains("SHA-256") && sinHash.Detail.Contains("7z2605-x64.exe"), "y lo dice");

                // --- La página no responde: error del fabricante y quedan las filas de winget.
                VendorProvider.FetchForTests = delegate(string u) { return null; };
                var out7 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, SieteZip("26.05", "x64", false)), out7, new List<string>());
                var err = FilaDe(out7, "7-Zip 26.05 (x64)");
                Check(err != null && err.Status == UpdateStatus.Error, "sin respuesta de 7-zip.org: error");
                var wgOk = FilaWinget("7-Zip 26.05 (x64)", "7zip.7zip", "26.05", "26.05"); wgOk.Status = UpdateStatus.AlDia; wgOk.Installable = false;
                var all7 = new List<UpdateCandidate> { wgOk, err };
                UpdateManager.DeduplicateVendor(all7);
                Check(all7.Count == 1 && all7[0] == wgOk, "y se muestra lo de winget");
            }
            finally { VendorProvider.FetchForTests = old; }
        }

        static void TestGitHubNoMandaSobreLaOficial2012()
        {
            // --- Un programa sin entrada en el catálogo: winget lo da al día, su página oficial publica la 3.4.0 y GitHub tiene la 3.5.0.
            var p = Vc("Editor Ejemplo", "3.4.0", "x64");
            p.Publisher = "Ejemplo SA";
            p.InstallLocation = @"C:\Program Files\Editor Ejemplo\";
            p.InfoUrl = "https://www.editor-ejemplo.com/";
            var c = FilaWinget("Editor Ejemplo", "Ejemplo.Editor", "3.4.0", "3.4.0");
            c.Status = UpdateStatus.AlDia; c.Installable = false; c.Detail = "Al día según winget.";
            string json = "[{\"tag_name\":\"v3.5.0\",\"html_url\":\"https://github.com/ejemplo/editor/releases/tag/v3.5.0\",\"draft\":false,\"prerelease\":false,\"assets\":[]}," +
                          "{\"tag_name\":\"v3.4.0\",\"html_url\":\"https://github.com/ejemplo/editor/releases/tag/v3.4.0\",\"draft\":false,\"prerelease\":false,\"assets\":[]}]";
            var releases = GitHubRules.ParseReleaseList(json);

            // La página oficial se leyó en esta búsqueda (SiteVersions, «Al día» de winget): queda anotado.
            var lectura = new SiteReading { Latest = "3.4.0", Url = "https://www.editor-ejemplo.com/", Evidence = "Editor Ejemplo 3.4.0" };
            Check(!SiteVersions.Apply(null, c, p, lectura, true), "la página dice lo mismo que winget: la fila no cambia");
            Eq("3.4.0", c.OfficialVersion, "pero queda anotado lo que publica la página oficial");

            var r = ReleaseCrossCheck.Apply(ContextoSimulado(true, p), c, p, "ejemplo/editor", releases);
            Eq(ReleaseCrossCheck.Outcome.HeldByOfficialPage, r, "la 3.5.0 de GitHub no se ofrece: la página oficial publica la 3.4.0");
            Check(c.Status == UpdateStatus.AlDia && c.AvailableVersion == "3.4.0", "sigue al día");
            Check(c.Detail.Contains("3.5.0") && c.Detail.Contains("www.editor-ejemplo.com"), "y lo explica: " + c.Detail);
            Eq("https://www.editor-ejemplo.com/", c.InfoUrl, "su sitio es la página oficial, no GitHub");

            // Una página de GitHub no cuenta como página oficial propia.
            var c2 = FilaWinget("Editor Ejemplo", "Ejemplo.Editor", "3.4.0", "3.4.0"); c2.Status = UpdateStatus.AlDia; c2.Installable = false;
            SiteVersions.Apply(null, c2, p, new SiteReading { Latest = "3.4.0", Url = "https://github.com/ejemplo/editor", Evidence = "" }, true);
            Eq(null, c2.OfficialVersion, "github.com no se anota como página oficial");

            // Sin lectura de la página oficial, sigue el contraste de 2.0.4 (el catálogo de winget va atrasado)…
            var c3 = FilaWinget("Editor Ejemplo", "Ejemplo.Editor", "3.4.0", "3.4.0"); c3.Status = UpdateStatus.AlDia; c3.Installable = false;
            Eq(ReleaseCrossCheck.Outcome.Lagging, ReleaseCrossCheck.Apply(ContextoSimulado(true, p), c3, p, "ejemplo/editor", releases), "sin página oficial leída: se ofrece la de GitHub");
            // …y si no se puede instalar desde acá, se abre la página oficial del programa (no la de GitHub).
            Check(c3.ProviderName == SiteProvider.ProviderId && c3.InfoUrl == "https://www.editor-ejemplo.com/", "abre su página oficial: " + c3.InfoUrl);
            Check(c3.Detail.Contains("página oficial (www.editor-ejemplo.com)"), "y lo dice: " + c3.Detail);

            // --- «7-Zip 26.05 (x64)» lleva la versión en el nombre pero se instala encima (su carpeta no la lleva).
            Check(!ReleaseCrossCheck.InstallsSideBySide("7-Zip 26.05 (x64)", "26.05", @"C:\Program Files\7-Zip\"), "7-Zip se reemplaza en su carpeta");
            Check(ReleaseCrossCheck.InstallsSideBySide("UltiMaker Cura 5.13.0", "5.13.0", @"C:\Program Files\UltiMaker Cura 5.13.0"), "Cura: una carpeta por versión");
            Check(ReleaseCrossCheck.InstallsSideBySide("UltiMaker Cura 5.13.0", "5.13.0", ""), "sin carpeta conocida: por las dudas, al lado");
            Check(!ReleaseCrossCheck.InstallsSideBySide("Notepad++ (64-bit x64)", "8.8.1", @"C:\Program Files\Notepad++"), "sin versión en el nombre: encima");
        }
    }
}
