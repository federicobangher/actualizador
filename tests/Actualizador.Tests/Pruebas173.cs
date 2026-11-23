// Pruebas de la 1.7.3: la versión en la página oficial de cada programa que winget no cubre.
using System.Collections.Generic;
using System.IO;
using System.Text;
using System;
using Actualizador.Core;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        const string PaginaTight = @"<html><head><title>TightVNC: Download</title><script>var lib = 'jquery 3.6.0'; var v = 'Version 9.9.9';</script>
<style>.x{width:2.5em}</style></head><body>
<h1>Download TightVNC</h1>
<p>TightVNC for Windows <b>Version 2.8.85</b> (released October 30, 2025)</p>
<p>Requires Windows 7 or newer, .NET 4.8. Installer size: 2.6 MB</p>
<p>Older: TightVNC 2.8.81, 2.8.79. Next major: TightVNC 3.0.0 beta 2 (preview)</p>
<footer>Copyright &copy; 2001-2026 GlavSoft LLC. Updated 30.10.2025</footer></body></html>";

        static void TestPaginasOficiales173()
        {
            // --- Lectura de la página: la versión publicada, sin scripts, Windows, .NET, tamaños, fechas ni betas.
            var r = SiteVersions.Evaluate(PaginaTight, "TightVNC", "2.8.81", false);
            Check(r != null && r.Latest == "2.8.85", "TightVNC: publica la 2.8.85 (" + (r != null ? r.Latest : "nada") + ")");
            Check(r != null && r.InstalledSeen, "nombra la instalada (2.8.81)");
            Check(r != null && r.Evidence.Contains("2.8.85"), "con el texto de dónde salió: " + (r != null ? r.Evidence : ""));
            var al = SiteVersions.Evaluate(PaginaTight, "TightVNC", "2.8.85", false);
            Check(al != null && !VersionUtil.IsNewer(al.Latest, "2.8.85"), "con la 2.8.85 instalada: al día");
            Eq(null, SiteVersions.Evaluate("<p>Founded in 1998. Rated 4.5 stars by 1.200 users. Windows 10.0.19041</p>", "ACDSee Pro", "9.0.108", false), "números que no son versiones: nada");
            Eq(null, SiteVersions.Evaluate("<p>Contact us. Phone 11.4567.8901</p>", "ACDSee Pro", "11.0", false), "un teléfono lejos de toda palabra clave: nada");
            Eq(null, SiteVersions.Evaluate("<p>Copyright 2026</p>", "TightVNC", "", false), "sin versión instalada no se compara");
            Eq(null, SiteVersions.Evaluate("<p>Latest: 01.11.2026</p>", "TightVNC", "2.8.81", false), "una fecha no es una versión");
            var vlc = SiteVersions.Evaluate("<div>VLC media player 3.0.21 — Download VLC</div><div>VLC 4.0.0 beta (nightly)</div>", "VLC media player", "3.0.18.0", false);
            Check(vlc != null && vlc.Latest == "3.0.21", "VLC: la 3.0.21; la 4.0.0 beta no cuenta (" + (vlc != null ? vlc.Latest : "nada") + ")");
            var salto = SiteVersions.Evaluate("<p>Release notes: version 12.0.1</p>", "ACDSee Pro", "9.0.108", false);
            Eq(null, salto, "un número de otra serie sin el nombre del programa: no se acepta");

            // Con winget «Al día» (estricto): sólo con el nombre del programa cerca.
            Check(SiteVersions.Evaluate("<p>GOM Player 2.3.123 is now available</p>", "GOM Player", "2.3.121.5401", true) != null, "estricto: con el nombre cerca, sí");
            Eq(null, SiteVersions.Evaluate("<p>Download v2.4.0</p>", "GOM Player", "2.3.121.5401", true), "estricto: sin el nombre, no");

            // --- Direcciones.
            Check(SiteVersions.IsPublicPage(new Uri("https://www.tightvnc.com/download.php")), "página pública");
            Check(!SiteVersions.IsPublicPage(new Uri("http://192.168.0.1/")), "una IP de la red local: no");
            Check(!SiteVersions.IsPublicPage(new Uri("https://router.lan/")), "un nombre de la red local: no");
            Check(!SiteVersions.IsPublicPage(new Uri("https://www.tightvnc.com:8443/")), "otro puerto: no");
            Check(!SiteVersions.IsPublicPage(new Uri("ftp://ftp.example.com/")), "FTP: no");
            var tight = Vc("TightVNC", "2.8.81", "x64");
            tight.Publisher = "GlavSoft LLC.";
            tight.InfoUrl = "https://www.tightvnc.com/"; tight.UpdateUrl = "https://www.tightvnc.com/download.php"; tight.HelpLink = "http://192.168.0.1/";
            var pages = SiteVersions.PagesFor(tight, new SourceList());
            Eq("https://www.tightvnc.com/download.php|https://www.tightvnc.com/", string.Join("|", pages.ToArray()), "primero la de actualizaciones, sin la IP");
            var mias = new SourceList();
            Check(mias.Set("TightVNC 2.8.81 (x64)", "https://www.tightvnc.com/news.php"), "página indicada por el usuario");
            Eq("https://www.tightvnc.com/news.php", SiteVersions.PagesFor(tight, mias)[0], "la indicada va primero (aunque el nombre tenga versión)");
            Check(!mias.Set("X", "javascript:alert(1)"), "una dirección que no es web: no");

            // --- fuentes.json: se guarda y se lee.
            string dir = Path.Combine(Path.GetTempPath(), "sp173-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "fuentes.json");
                Check(mias.Save(path), "se guarda");
                Eq("https://www.tightvnc.com/news.php", SourceList.Load(path).Find("TightVNC"), "se lee");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            // --- La búsqueda: los no verificables y los «Al día» de winget con catálogo atrasado.
            var oldFetch = SiteVersions.FetchForTests;
            var oldSources = SiteVersions.SourcesForTests;
            try
            {
                SiteVersions.SourcesForTests = new SourceList();
                SiteVersions.FetchForTests = delegate(string u)
                {
                    if (u.Contains("tightvnc.com/download")) return PaginaTight;
                    if (u.Contains("gomlab")) return "<p>GOM Player 2.3.123 (2026-10-13)</p>";
                    if (u.Contains("ejemplo-raro")) return "<p>Bienvenido</p>";
                    return null;
                };
                var gom = Gom("2.3.121.5401"); gom.InfoUrl = "https://www.gomlab.com/";
                var raro = Vc("Programa raro", "1.2.3", "x64"); raro.InfoUrl = "https://ejemplo-raro.com/";
                var juego = Vc("Counter-Strike 2", "1.0.0", "x86"); juego.UninstallString = "\"C:\\Steam\\steam.exe\" steam://uninstall/730"; juego.InfoUrl = "https://www.counter-strike.net/";
                var programas = new List<InstalledProgram> { tight, gom, raro, juego };
                var filas = new List<UpdateCandidate>
                {
                    new UpdateCandidate { Kind = UpdateKind.Programa, Name = "TightVNC", InstalledVersion = "2.8.81", Status = UpdateStatus.NoVerificable, ProviderName = WingetProvider.ProviderId },
                    new UpdateCandidate { Kind = UpdateKind.Programa, Name = "GOM Player", InstalledVersion = "2.3.121.5401", Status = UpdateStatus.AlDia, ProviderName = WingetProvider.ProviderId, ProviderTag = new WingetRow() },
                    new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Programa raro", InstalledVersion = "1.2.3", Status = UpdateStatus.NoVerificable, ProviderName = WingetProvider.ProviderId },
                    new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Counter-Strike 2", InstalledVersion = "1.0.0", Status = UpdateStatus.NoVerificable, ProviderName = WingetProvider.ProviderId },
                };
                var ctx = ContextoSimulado(true);
                var notas = new List<string>();
                int cambios = SiteVersions.Resolve(ctx, filas, programas, notas);
                Eq(2, cambios, "dos filas cambiaron");
                Check(filas[0].Status == UpdateStatus.Disponible && filas[0].AvailableVersion == "2.8.85" && filas[0].Source.Contains("tightvnc.com") && filas[0].Installable &&
                      filas[0].ProviderName == SiteProvider.ProviderId,
                      "TightVNC: disponible la 2.8.85 según su página; se puede marcar (abre su página: no enlaza un instalador)");
                Check(filas[1].Status == UpdateStatus.Disponible && filas[1].Detail.Contains("catálogo de winget va atrasado"), "GOM: winget la daba al día y su página publica una más nueva");
                Eq(UpdateStatus.NoVerificable, filas[2].Status, "página sin versión reconocible: sigue no verificable");
                Eq(UpdateStatus.NoVerificable, filas[3].Status, "juego de Steam: no se lee la página (lo actualiza Steam)");
                Check(notas.Exists(delegate(string n) { return n.Contains("lectura automática"); }) && notas.Exists(delegate(string n) { return n.Contains("va atrasado"); }), "las notas lo cuentan");

                // Con «Consultar los sitios de los fabricantes» apagado, no se lee nada.
                var filas2 = new List<UpdateCandidate> { new UpdateCandidate { Kind = UpdateKind.Programa, Name = "TightVNC", InstalledVersion = "2.8.81", Status = UpdateStatus.NoVerificable } };
                ctx.Options.QueryVendors = false;
                Eq(0, SiteVersions.Resolve(ctx, filas2, programas, new List<string>()), "opción apagada: no se consulta");
                ctx.Options.QueryVendors = true;
            }
            finally { SiteVersions.FetchForTests = oldFetch; SiteVersions.SourcesForTests = oldSources; }
        }
    }
}
