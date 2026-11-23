// Pruebas de la 2.0.4: Cura (beta que el programa anuncia, catálogo de winget atrasado) y programas sin editor registrado.
using System;
using System.Collections.Generic;
using System.IO;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        /// <summary>Cura como lo registra su instalador NSIS: el nombre lleva la versión y cada versión va en su carpeta.</summary>
        static InstalledProgram Cura204(string version)
        {
            var p = Vc("UltiMaker Cura " + version, version, "x64");
            p.Publisher = "UltiMaker";
            p.InstallLocation = @"C:\Program Files\UltiMaker Cura " + version;
            p.UninstallString = @"C:\Program Files\UltiMaker Cura " + version + @"\uninstall.exe";
            p.DisplayIcon = @"C:\Program Files\UltiMaker Cura " + version + @"\UltiMaker-Cura.exe";
            p.RegistryKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UltiMaker Cura " + version + "-" + version;
            return p;
        }

        /// <summary>Fila «Al día» de winget como la arma «winget list» (FromListRow).</summary>
        static UpdateCandidate AlDiaWinget204(string name, string id, string version)
        {
            var w = new WingetRow { Name = name, Id = id, Version = version, Available = "", Source = "winget" };
            return WingetProvider.FromListRow(w, new UpdateCandidate
            {
                Kind = UpdateKind.Programa, Name = name, InstalledVersion = version, AvailableVersion = "", Identifier = id,
                ProviderName = WingetProvider.ProviderId, ProviderTag = w, Source = "winget (winget)"
            });
        }

        static void TestCuraYBetas204()
        {
            string releasesJson = File.ReadAllText(Path.Combine(samples, "github_releases_cura.json"));
            string manifestYaml = File.ReadAllText(Path.Combine(samples, "winget_manifest_cura.yaml"));

            // --- Lista de versiones de GitHub: estables y preliminares.
            var rels = GitHubRules.ParseReleaseList(releasesJson);
            Eq(7, rels.Count, "las 7 versiones de la página (también las preliminares)");
            var stable = GitHubRules.NewestStable(rels);
            var pre = GitHubRules.NewestPreRelease(rels);
            Eq("5.13.0", stable != null ? stable.Version : null, "la estable más nueva: 5.13.0 (no la primera de la lista)");
            Eq("5.14.0-beta.2-RC1", pre != null ? pre.FullVersion : null, "la preliminar más nueva, con su sufijo");
            Check(rels.Find(delegate(GitHubRelease r) { return r.Tag == "5.13.0-RC6"; }).PreRelease, "una RC es preliminar");
            Eq(4, stable.Assets.Count, "con sus archivos");
            Check(VersionUtil.IsNewer("5.14.0-beta.2-RC1", "5.14.0-beta.1") && VersionUtil.IsNewer("5.14.0", "5.14.0-beta.2-RC1"), "beta.2 > beta.1 y la final > cualquier beta");
            Eq(null, GitHubRules.ParseRelease("{\"tag_name\":\"5.14.0-beta.1\",\"prerelease\":true,\"draft\":false,\"assets\":[]}"), "«latest» nunca devuelve una preliminar");
            Eq(0, GitHubRules.ParseReleaseList("{\"message\":\"Not Found\"}").Count, "respuesta que no es una lista: vacía");
            Eq("https://api.github.com/repos/Ultimaker/Cura/releases?per_page=30", GitHubRules.ReleasesUrl("Ultimaker/Cura", 30), "dirección de la lista");

            // --- De dónde baja winget el instalador: el manifiesto real de Ultimaker.Cura 5.13.0.
            var m = WingetManifest.Parse(manifestYaml);
            Eq("Ultimaker.Cura", m.Id, "manifiesto de Cura leído");
            Eq("Ultimaker/Cura", ReleaseCrossCheck.RepoOfManifest(m), "el instalador sale de github.com/Ultimaker/Cura");
            var ajeno = WingetManifest.Parse("PackageIdentifier: A.B\nPackageVersion: 1.0\nInstallers:\n- Architecture: x64\n  InstallerType: exe\n  InstallerUrl: https://github.com/a/b/archive/refs/tags/v1.0.zip\n  InstallerSha256: " + new string('0', 64) + "\n");
            Eq(null, ReleaseCrossCheck.RepoOfManifest(ajeno), "un archivo del código (no «/releases/download/») no cuenta");

            // --- El nombre lleva la versión: se instala una al lado de la otra.
            Check(ReleaseCrossCheck.VersionInName("UltiMaker Cura 5.13.0", "5.13.0"), "«UltiMaker Cura 5.13.0» lleva su versión");
            Check(!ReleaseCrossCheck.VersionInName("Notepad++ (64-bit x64)", "8.8.1"), "un nombre sin versión");
            Check(!ReleaseCrossCheck.VersionInName("Programa 5.13.01", "5.13.0"), "«5.13.01» no es «5.13.0»");

            // --- El caso del usuario: Cura 5.13.0 (la última estable) y Cura avisa «¡Está disponible Ultimaker Cura 5.14.0-beta.1!».
            var cura = Cura204("5.13.0");
            var fila = AlDiaWinget204(cura.Name, "Ultimaker.Cura", "5.13.0");
            Check(ReleaseCrossCheck.Eligible(fila), "fila «Al día» del repositorio winget: se contrasta");
            var r1 = ReleaseCrossCheck.Apply(ContextoSimulado(true, cura), fila, cura, "Ultimaker/Cura", rels);
            Eq(ReleaseCrossCheck.Outcome.PreReleaseNoted, r1, "sólo hay una beta más nueva");
            Eq(UpdateStatus.AlDia, fila.Status, "sigue «Al día»: una beta no se ofrece");
            Check(!fila.Installable && !fila.Recommended, "y no se puede marcar");
            Check(fila.Detail.Contains("5.14.0-beta.2-RC1") && fila.Detail.Contains("no es final"), "el detalle explica la beta que anuncia el programa: " + fila.Detail);
            Check(fila.Detail.StartsWith("Coincide con la última versión publicada en winget"), "conserva lo que decía");

            // --- Cura 5.12.1 y el catálogo de winget atrasado en la 5.12.1: GitHub ya publica la 5.13.0.
            var vieja = Cura204("5.12.1");
            var fila2 = AlDiaWinget204(vieja.Name, "Ultimaker.Cura", "5.12.1");
            var r2 = ReleaseCrossCheck.Apply(ContextoSimulado(true, vieja), fila2, vieja, "Ultimaker/Cura", rels);
            Eq(ReleaseCrossCheck.Outcome.Lagging, r2, "catálogo atrasado");
            Eq(UpdateStatus.Disponible, fila2.Status, "pasa a «Disponible»");
            Eq("5.13.0", fila2.AvailableVersion, "con la estable, nunca la beta");
            Eq(SiteProvider.ProviderId, fila2.ProviderName, "Cura se instala al lado de la vieja: se abre su página");
            Check(fila2.Installable && !fila2.Recommended, "se puede marcar (abre la página), queda opcional");
            Check(fila2.InfoUrl.EndsWith("/releases/tag/5.13.0"), "la página de esa versión: " + fila2.InfoUrl);
            Check(fila2.Detail.Contains("va atrasado") && fila2.Detail.Contains("al lado de la otra") && fila2.Detail.Contains("5.14.0-beta.2-RC1"), "lo explica: " + fila2.Detail);
            Eq(UpdateGroup.Opcional, UpdateGroups.Of(fila2), "en «Opcionales»");

            // --- Un programa que se reemplaza en su lugar: se instala con el archivo de GitHub (SHA-256 de GitHub y misma firma).
            var npp = Vc("Notepad++ (64-bit x64)", "8.8.0", "x64");
            npp.Publisher = "Notepad++ Team";
            npp.InstallLocation = @"C:\Program Files\Notepad++";
            var nppRels = GitHubRules.ParseReleaseList("[" +
                "{\"tag_name\":\"v8.8.1\",\"prerelease\":false,\"html_url\":\"https://github.com/notepad-plus-plus/notepad-plus-plus/releases/tag/v8.8.1\",\"assets\":[" +
                "{\"name\":\"npp.8.8.1.Installer.x64.exe\",\"browser_download_url\":\"https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/v8.8.1/npp.8.8.1.Installer.x64.exe\",\"size\":5000000,\"digest\":\"sha256:" + new string('a', 64) + "\"}," +
                "{\"name\":\"npp.8.8.1.Installer.exe\",\"browser_download_url\":\"https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/v8.8.1/npp.8.8.1.Installer.exe\",\"size\":5000000,\"digest\":\"sha256:" + new string('b', 64) + "\"}]}," +
                "{\"tag_name\":\"v8.8.0\",\"prerelease\":false,\"assets\":[]}]");
            var fila3 = AlDiaWinget204(npp.Name, "Notepad++.Notepad++", "8.8.0");
            var r3 = ReleaseCrossCheck.Apply(ContextoSimulado(true, npp), fila3, npp, "notepad-plus-plus/notepad-plus-plus", nppRels);
            Eq(ReleaseCrossCheck.Outcome.Lagging, r3, "Notepad++: catálogo atrasado");
            Eq(VendorProvider.ProviderId, fila3.ProviderName, "se instala con la verificación de GitHub");
            var plan = fila3.ProviderTag as VendorPlan;
            Check(plan != null && plan.Repo == "notepad-plus-plus/notepad-plus-plus" && plan.InstallerUrl.EndsWith("Installer.x64.exe") && plan.InstallerSha256 == new string('a', 64),
                  "el instalador x64 con el SHA-256 de GitHub");
            Check(fila3.Installable && fila3.Recommended, "entra en «Actualizar todo»");
            // Sin administrador y para todo el equipo (sin carpeta conocida cuenta como Program Files, en cualquier sistema).
            var nppEquipo = Vc("Notepad++ (64-bit x64)", "8.8.0", "x64"); nppEquipo.Publisher = "Notepad++ Team";
            var sinAdmin = AlDiaWinget204(nppEquipo.Name, "Notepad++.Notepad++", "8.8.0");
            ReleaseCrossCheck.Apply(ContextoSimulado(false, nppEquipo), sinAdmin, nppEquipo, "notepad-plus-plus/notepad-plus-plus", nppRels);
            Check(sinAdmin.ProviderName == SiteProvider.ProviderId && sinAdmin.Detail.Contains("administrador"), "sin administrador: abre la página y dice por qué");

            // --- Numeración distinta: no se compara.
            var otra = Vc("Programa Fechado", "24.10", "x64");
            var fila4 = AlDiaWinget204(otra.Name, "Editor.Fechado", "24.10");
            var fechas = GitHubRules.ParseReleaseList("[{\"tag_name\":\"v2025.1\",\"prerelease\":false,\"assets\":[]},{\"tag_name\":\"v2024.10\",\"prerelease\":false,\"assets\":[]}]");
            Eq(ReleaseCrossCheck.Outcome.None, ReleaseCrossCheck.Apply(ContextoSimulado(true, otra), fila4, otra, "editor/fechado", fechas), "«2024.10» no es «24.10»: no se toca");
            Eq(UpdateStatus.AlDia, fila4.Status, "sigue como estaba");

            // --- Por serie (Python 3.13 al lado de 3.14): sólo la misma serie.
            var py = Vc("Python 3.13.5 (64-bit)", "3.13.5", "x64");
            py.Publisher = "Python Software Foundation";
            var filaPy = AlDiaWinget204(py.Name, "Python.Python.3.13", "3.13.5");
            var pyRels = GitHubRules.ParseReleaseList("[{\"tag_name\":\"v3.14.0\",\"prerelease\":false,\"assets\":[]},{\"tag_name\":\"v3.13.5\",\"prerelease\":false,\"assets\":[]}]");
            Eq(ReleaseCrossCheck.Outcome.None, ReleaseCrossCheck.Apply(ContextoSimulado(true, py), filaPy, py, "python/cpython", pyRels), "Python 3.13 no pasa a la 3.14");

            // --- Lo que no se contrasta.
            var disp = FilaWinget("7-Zip", "7zip.7zip", "24.09", "25.01");
            Check(!ReleaseCrossCheck.Eligible(disp), "lo que ya está «Disponible» en winget");
            var store = AlDiaWinget204("Spotify", "9NCBCSZSJRSB", "1.2");
            ((WingetRow)store.ProviderTag).Source = "msstore";
            Check(!ReleaseCrossCheck.Eligible(store), "lo del origen msstore");
            var recorte = AlDiaWinget204("Programa", "Editor.Progr…", "1.0");
            Check(!ReleaseCrossCheck.Eligible(recorte), "un identificador recortado");

            // --- La búsqueda completa simulada: manifiesto de winget-pkgs y versiones de GitHub.
            var pedidas = new List<string>();
            ReleaseCrossCheck.FetchForTests = delegate(string url)
            {
                pedidas.Add(url);
                if (url == "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/u/Ultimaker/Cura/5.13.0/Ultimaker.Cura.installer.yaml") return manifestYaml;
                if (url == "https://api.github.com/repos/Ultimaker/Cura/releases?per_page=30") return releasesJson;
                return null;                                            // 404
            };
            try
            {
                var gimp = Vc("GIMP 3.0.4", "3.0.4", "x64"); gimp.Publisher = "The GIMP Team";
                var filaCura = AlDiaWinget204(cura.Name, "Ultimaker.Cura", "5.13.0");
                var filaGimp = AlDiaWinget204(gimp.Name, "GIMP.GIMP.3", "3.0.4");
                var todas = new List<UpdateCandidate> { filaCura, filaGimp };
                var notas = new List<string>();
                var ctx = ContextoSimulado(true, cura, gimp);
                ctx.Options = new UpdateOptions();
                int cambios = ReleaseCrossCheck.Resolve(ctx, todas, ctx.Programs, notas);
                Eq(1, cambios, "una fila con algo que decir (Cura)");
                Check(filaCura.Detail.Contains("5.14.0-beta.2-RC1"), "Cura: la beta en el detalle");
                Eq(UpdateStatus.AlDia, filaGimp.Status, "GIMP (su manifiesto no está en la simulación): sin cambios");
                Eq(1, pedidas.FindAll(delegate(string u) { return u.StartsWith("https://api.github.com/"); }).Count, "una sola consulta a la API de GitHub");
                Check(notas.Exists(delegate(string n) { return n.Contains("versión preliminar") && n.Contains("sólo versiones finales"); }), "la nota explica las betas");
                ctx.Options.QueryVendors = false;
                pedidas.Clear();
                Eq(0, ReleaseCrossCheck.Resolve(ctx, new List<UpdateCandidate> { AlDiaWinget204(cura.Name, "Ultimaker.Cura", "5.13.0") }, ctx.Programs, notas), "sin consultas a fabricantes (Opciones): no se consulta");
                Eq(0, pedidas.Count, "ninguna descarga");
            }
            finally { ReleaseCrossCheck.FetchForTests = null; }
        }

        static void TestSinEditor204()
        {
            string[] cab = { "Nombre", "Id", "Versión" };
            var sinEditor = Vc("HandBrake", "1.9.0", "x64"); sinEditor.Publisher = "";
            var una = WingetTable.Parse(TablaWinget(cab, new[] { "HandBrake", "HandBrake.HandBrake", "1.10.2" }, new[] { "HandBrake CLI", "HandBrake.HandBrake.CLI", "1.10.2" }));
            var m = WingetProvider.PickSearchMatch(una, sinEditor);
            Check(m != null && m.Id == "HandBrake.HandBrake", "sin editor registrado: el único paquete con ese nombre exacto");
            var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "HandBrake", ProviderName = WingetProvider.ProviderId, Status = UpdateStatus.NoVerificable };
            WingetProvider.ApplySearchMatch(c, sinEditor, m);
            Check(c.Status == UpdateStatus.Disponible && c.Installable && !c.Recommended, "se ofrece, pero opcional (no hay editor con qué confirmarlo)");
            Check(c.Detail.Contains("no registra su editor") && c.Detail.Contains("Revisá"), "y lo dice: " + c.Detail);

            var dos = WingetTable.Parse(TablaWinget(cab, new[] { "HandBrake", "HandBrake.HandBrake", "1.10.2" }, new[] { "HandBrake", "Otro.HandBrake", "2.0" }));
            Eq(null, WingetProvider.PickSearchMatch(dos, sinEditor), "dos paquetes con ese nombre: ninguno");
            var corto = Vc("Zoom", "6.0", "x64"); corto.Publisher = "";
            Eq(null, WingetProvider.PickSearchMatch(WingetTable.Parse(TablaWinget(cab, new[] { "Zoom", "Zoom.Zoom", "6.5" })), corto), "nombre corto sin editor: no se adivina");
            var conEditor = Vc("HandBrake", "1.9.0", "x64"); conEditor.Publisher = "Otro";
            var m2 = WingetProvider.PickSearchMatch(dos, conEditor);
            Check(m2 != null && m2.Id == "Otro.HandBrake", "con editor, las reglas de siempre: el paquete de ese editor");
        }
    }
}
