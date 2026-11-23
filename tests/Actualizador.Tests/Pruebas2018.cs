// Pruebas de la 2.0.18. Captura del usuario (15/11/2026, con la 2.0.17): CrystalDiskInfo 9.9.4 → 9.9.5 seguía «Pendiente: Se abrió su página
// (crystalmark.info)». Causa: las pruebas de la 2.0.17 simulaban que crystalmark.info y SourceForge redirigen al archivo, y no es así:
// - crystalmark.info/redirect.php responde una página «Redirecting… Please wait…» con un botón «Download Now» a SourceForge;
// - SourceForge, a todo lo que se presenta como navegador («Mozilla/5.0…», como Actualizador), le responde «Your download will start
//   shortly…» y baja el archivo con JavaScript; a una herramienta de descargas le contesta con la redirección al servidor espejo.
// Estas pruebas simulan los servidores como responden de verdad (también según el User-Agent).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        const string CdiRedirect = "https://crystalmark.info/redirect.php?product=CrystalDiskInfoInstaller";
        const string CdiSfPage = "https://sourceforge.net/projects/crystaldiskinfo/files/9.9.5/CrystalDiskInfo9_9_5.exe/download";
        const string CdiPsychz = "https://psychz.dl.sourceforge.net/project/crystaldiskinfo/9.9.5/CrystalDiskInfo9_9_5.exe?viasf=1";

        /// <summary>La página de crystalmark.info/redirect.php (lo que importa): «Redirecting…», «Please wait…» y el botón «Download Now».</summary>
        const string CdiRedirectHtml =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Redirecting...</title>" +
            "<script>setTimeout(function(){ document.getElementById('dl').click(); }, 1000);</script></head>" +
            "<body><h1>Please wait...</h1><p><a id=\"dl\" class=\"button\" href=\"https://sourceforge.net/projects/crystaldiskinfo/files/9.9.5/CrystalDiskInfo9_9_5.exe/download\">Download Now</a></p>" +
            "<p><a href=\"https://crystalmark.info/en/\">Crystal Dew World</a></p></body></html>";

        /// <summary>La página de espera de SourceForge para un navegador: sin redirección ni enlace al archivo; sí el espejo elegido.</summary>
        const string SfInterstitialHtml =
            "<!DOCTYPE html><html><head><title>Download CrystalDiskInfo from SourceForge.net</title>" +
            "<link rel=\"canonical\" href=\"https://sourceforge.net/projects/crystaldiskinfo/files/9.9.5/CrystalDiskInfo9_9_5.exe/download\">" +
            "<script>var dl = { mirror: 'psychz' }; /* la descarga arranca con JavaScript */</script></head><body>" +
            "<h2>Your download will start shortly...</h2>" +
            "<a href=\"https://x.com/share?url=https%3A%2F%2Fsourceforge.net%2Fprojects%2Fcrystaldiskinfo%2Ffiles%2F9.9.5%2FCrystalDiskInfo9_9_5.exe%2Fdownload\">Share</a> " +
            "<a href=\"/settings/mirror_choices?projectname=crystaldiskinfo&amp;filename=9.9.5/CrystalDiskInfo9_9_5.exe&amp;selected=psychz\">Problems Downloading?</a>" +
            "</body></html>";

        /// <summary>
        /// SourceForge como responde de verdad: con un User-Agent de navegador, la página de espera (siempre 200); con el de una herramienta de
        /// descargas, la redirección a downloads.sourceforge.net y de ahí al espejo. <paramref name="honorsTool"/> en false: la página de espera
        /// también para la herramienta (por si un día cambia), para probar el espejo sacado de la página.
        /// </summary>
        static Func<string, long, int, Http.RangeResult> SourceForgeReal(byte[] exe, bool honorsTool, bool mirrorInPage, List<string> pedidos)
        {
            return delegate(string url, long from, int count)
            {
                if (pedidos != null && from == 0) pedidos.Add(url);
                var u = new Uri(url);
                string ua = Http.UserAgentFor(u);
                bool browser = ua.Contains("Mozilla");
                byte[] data = null;
                string final = url;
                if (url == CdiRedirect) data = Encoding.UTF8.GetBytes(CdiRedirectHtml);
                else if (u.Host == "sourceforge.net" || u.Host == "downloads.sourceforge.net")
                {
                    if (browser || !honorsTool) data = Encoding.UTF8.GetBytes(mirrorInPage ? SfInterstitialHtml : SfInterstitialHtml.Replace("mirror_choices", "otra_cosa"));
                    else { final = "https://netcologne.dl.sourceforge.net/project/crystaldiskinfo/9.9.5/CrystalDiskInfo9_9_5.exe?viasf=1"; data = exe; }
                }
                else if (u.Host.EndsWith(".dl.sourceforge.net") && u.AbsolutePath == "/project/crystaldiskinfo/9.9.5/CrystalDiskInfo9_9_5.exe") data = exe;
                if (data == null) throw new InvalidOperationException("404 " + url);
                var r = new Http.RangeResult();
                r.Final = new Uri(final);
                r.Partial = true;
                r.Total = data.Length;
                r.Data = Lector(data)(from, count);
                return r;
            };
        }

        static Func<string, string> PaginasReales(Dictionary<string, string> extra)
        {
            return PaginasReales(extra, true);
        }

        /// <summary>Las páginas como las lee el programa; <paramref name="mirrorInPage"/> en false: la de SourceForge no nombra el espejo.</summary>
        static Func<string, string> PaginasReales(Dictionary<string, string> extra, bool mirrorInPage)
        {
            return delegate(string u)
            {
                if (u == CdiHome) return CdiHomeHtml;
                if (u == CdiDownloads) return CdiDownloadsHtml;
                if (u == CdiRedirect) return CdiRedirectHtml;
                if (u.StartsWith("https://sourceforge.net/projects/crystaldiskinfo/") || u.StartsWith("https://downloads.sourceforge.net/"))
                    return mirrorInPage ? SfInterstitialHtml : SfInterstitialHtml.Replace("mirror_choices", "otra_cosa");
                string t;
                if (extra != null && extra.TryGetValue(u, out t)) return t;
                return "";
            };
        }

        // ------------------------------------------------------------------ El User-Agent

        static void TestUserAgentDeDescargas2018()
        {
            foreach (string h in new[] { "https://sourceforge.net/projects/x/files/a.exe/download", "https://downloads.sourceforge.net/project/x/a.exe", "https://psychz.dl.sourceforge.net/project/x/a.exe",
                                         "https://master.dl.sourceforge.net/project/x/a.exe", "https://sf.net/x", "https://downloads.sf.net/x" })
                Check(!Http.UserAgentFor(new Uri(h)).Contains("Mozilla") && Http.UserAgentFor(new Uri(h)).StartsWith("Actualizador/"), "SourceForge: herramienta de descargas, no navegador (" + h + ")");
            Check(Http.UserAgentFor(new Uri("https://crystalmark.info/")).StartsWith("Mozilla/5.0"), "los demás sitios, como siempre (algunas CDN rechazan lo que no empieza con «Mozilla»)");
            Check(Http.UserAgentFor(new Uri("https://sourceforge.net.malo.example/x")).StartsWith("Mozilla") && Http.UserAgentFor(new Uri("https://notsourceforge.net/x")).StartsWith("Mozilla"),
                  "no un dominio parecido");
            Check(Http.DownloaderAgent.Contains(AppInfo.Version), "se identifica con su versión");
            string http = File.ReadAllText(Path.Combine(RaizDelProyecto(), "src/Actualizador.Engine/Updates/Http.cs"));
            Check(http.Contains("req.UserAgent = UserAgentFor(u);") && !http.Contains("req.UserAgent = UserAgent;"), "cada pedido (páginas, pedazos y descargas) usa el User-Agent de su servidor, en cada salto");
        }

        static string RaizDelProyecto()
        {
            string d = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && d != null; i++)
            {
                if (File.Exists(Path.Combine(d, "Actualizador.sln"))) return d;
                d = Path.GetDirectoryName(d.TrimEnd('/', '\\'));
            }
            return Directory.GetCurrentDirectory();
        }

        // ------------------------------------------------------------------ Las páginas intermedias

        static void TestPaginasIntermedias2018()
        {
            var cdi = new SiteVersions.NameMatcher("CrystalDiskInfo 9.9.4", "Crystal Dew World");
            // «Redirecting…» de crystalmark.info: el botón «Download Now» lleva a SourceForge («…/CrystalDiskInfo9_9_5.exe/download»).
            Eq(CdiSfPage, SiteInstallers.InterstitialTarget(CdiRedirectHtml, CdiRedirect, cdi), "crystalmark.info: el botón «Download Now» (antes no se reconocía: el enlace termina en «/download»)");
            Eq("CrystalDiskInfo9_9_5.exe", SiteInstallers.LinkFileName(new Uri(CdiSfPage)), "el archivo de una dirección de SourceForge es el anterior a «download»");
            Eq("download", SiteInstallers.LinkFileName(new Uri("https://ejemplo.com/files/download")), "si no, el último tramo");
            // La de SourceForge: el espejo elegido.
            Eq(CdiPsychz, SiteInstallers.SourceForgeMirror(SfInterstitialHtml), "SourceForge: el archivo en el espejo que eligió la página");
            Eq(CdiPsychz, SiteInstallers.InterstitialTarget(SfInterstitialHtml, CdiSfPage, cdi), "y como destino de la página de espera");
            Check(SiteInstallers.SourceForgeMirror("<a href=\"/settings/mirror_choices?projectname=x&amp;filename=../../etc/passwd&amp;selected=psychz\">") == null &&
                  SiteInstallers.SourceForgeMirror("<a href=\"/settings/mirror_choices?projectname=x&amp;filename=a.exe&amp;selected=evil.example.com/\">") == null &&
                  SiteInstallers.SourceForgeMirror("<a href=\"/settings/mirror_choices?projectname=x%2F..&amp;filename=a.exe&amp;selected=psychz\">") == null &&
                  SiteInstallers.SourceForgeMirror("<a href=\"/settings/mirror_choices?projectname=x&amp;filename=a.exe\">") == null,
                  "nunca una ruta con «..», otro servidor como «espejo», ni sin espejo");
            Eq("https://kumisystems.dl.sourceforge.net/project/sevenzip/7-Zip/24.09/7z2409%20x64.exe?viasf=1",
               SiteInstallers.SourceForgeMirror("mirror_choices?projectname=sevenzip&filename=7-Zip/24.09/7z2409%20x64.exe&selected=kumisystems\""), "con subcarpetas y espacios");
            // Redirección por JavaScript.
            Eq("https://cdn.ejemplo.com/f/MiPrograma-2.5.0.exe",
               SiteInstallers.InterstitialTarget("<script>window.location.href = \"https://cdn.ejemplo.com/f/MiPrograma-2.5.0.exe\";</script>", "https://www.ejemplo.com/gracias", new SiteVersions.NameMatcher("MiPrograma", "")),
               "location.href con el instalador");
            Check(SiteInstallers.InterstitialTarget("<script>location.href = \"https://www.ejemplo.com/inicio\";</script>", "https://www.ejemplo.com/gracias", new SiteVersions.NameMatcher("MiPrograma", "")) == null,
                  "una redirección por JavaScript a una página que no es un instalador: no");
        }

        // ------------------------------------------------------------------ La captura, con los servidores como responden de verdad

        static void TestCapturaConServidoresReales2018()
        {
            var oldManifest = WingetDirect.ManifestForTests;
            var oldRange = OfficialInstaller.RangeForTests;
            var oldFetch = SiteVersions.FetchForTests;
            var oldSources = SiteVersions.SourcesForTests;
            try
            {
                SiteVersions.SourcesForTests = new SourceList();
                SiteVersions.FetchForTests = PaginasReales(null);
                var exe = InstaladorFalso("Inno Setup Setup Data (6.4.0)", 1);
                var manifiestos = new Dictionary<string, string> { { CdiManifestUrl("9.9.4"), CdiManifest("9.9.4", "248D99FC43041E6C9C486BEAAE3E38E7FB864D8787B31F24D7634E0032952333") } };
                WingetDirect.ManifestForTests = delegate(string u) { string t; return manifiestos.TryGetValue(u, out t) ? t : null; };
                var ctx = ContextoSimulado(true);
                var p = CdiInstalado("9.9.4");
                Func<UpdateCandidate> filaWinget = delegate
                {
                    return new UpdateCandidate { Kind = UpdateKind.Programa, Name = p.Name, Publisher = p.Publisher, InstalledVersion = "9.9.4", AvailableVersion = "9.9.4", Status = UpdateStatus.AlDia,
                                                 ProviderName = WingetProvider.ProviderId, Identifier = CdiId, ProviderTag = new WingetRow { Name = p.Name, Id = CdiId, Version = "9.9.4", Source = "winget" } };
                };
                var pedidos = new List<string>();

                // --- Lo que pasaba con la 2.0.17: SourceForge contesta la página de espera a cualquier pedido «de navegador».
                // Ahora (User-Agent de herramienta de descargas) contesta la redirección al espejo: se ubica por winget-pkgs.
                OfficialInstaller.RangeForTests = SourceForgeReal(exe, true, true, pedidos);
                var fila = filaWinget();
                SiteVersions.Resolve(ctx, new List<UpdateCandidate> { fila }, new List<InstalledProgram> { p }, new List<string>());
                var plan = fila.ProviderTag as VendorPlan;
                Check(fila.ProviderName == VendorProvider.ProviderId && fila.Installable && fila.Recommended && plan != null && plan.Lead != null,
                      "CrystalDiskInfo 9.9.5 se instala desde acá y entra en «Marcar todo» (" + fila.ProviderName + ": " + fila.Detail + ")");
                if (plan != null) Eq(CdiDirect995, plan.InstallerUrl, "por la dirección de winget con la versión nueva");

                // --- Si SourceForge respondiera la página de espera también a la herramienta: el espejo sale de la misma página.
                pedidos.Clear();
                OfficialInstaller.RangeForTests = SourceForgeReal(exe, false, true, pedidos);
                fila = filaWinget();
                SiteVersions.Resolve(ctx, new List<UpdateCandidate> { fila }, new List<InstalledProgram> { p }, new List<string>());
                plan = fila.ProviderTag as VendorPlan;
                Check(fila.ProviderName == VendorProvider.ProviderId && plan != null && plan.Lead != null && plan.Lead.VersionConfirmed,
                      "con la página de espera: por el espejo que nombra la página (" + fila.Detail + ")");
                Check(pedidos.Contains(CdiPsychz), "pidió el archivo al espejo: " + string.Join(", ", pedidos.ToArray()));

                // --- Sin winget: la página oficial → su página de descargas → «Redirecting…» → SourceForge → espejo.
                pedidos.Clear();
                OfficialInstaller.RangeForTests = SourceForgeReal(exe, true, true, pedidos);
                var sinWinget = new UpdateCandidate { Kind = UpdateKind.Programa, Name = p.Name, Publisher = p.Publisher, InstalledVersion = "9.9.4", Status = UpdateStatus.NoVerificable, ProviderName = WingetProvider.ProviderId };
                SiteVersions.Resolve(ctx, new List<UpdateCandidate> { sinWinget }, new List<InstalledProgram> { p }, new List<string>());
                var plan2 = sinWinget.ProviderTag as VendorPlan;
                Check(sinWinget.ProviderName == VendorProvider.ProviderId && plan2 != null && plan2.Lead != null && plan2.Lead.Origin == InstallerLead.Page && plan2.Lead.FileName == "CrystalDiskInfo9_9_5.exe",
                      "por la página: «Download Now» de crystalmark.info y SourceForge (" + sinWinget.Detail + ")");
                Check(pedidos.Count >= 2 && pedidos[0] == CdiRedirect, "empezó por el instalador de la edición estándar: " + string.Join(", ", pedidos.ToArray()));

                // --- Si no hay forma (la página de SourceForge no nombra el espejo y no redirige): abre la página y DICE POR QUÉ.
                OfficialInstaller.RangeForTests = SourceForgeReal(exe, false, false, null);
                SiteVersions.FetchForTests = PaginasReales(null, false);
                fila = filaWinget();
                SiteVersions.Resolve(ctx, new List<UpdateCandidate> { fila }, new List<InstalledProgram> { p }, new List<string>());
                var sp = fila.ProviderTag as SitePlan;
                Check(fila.ProviderName == SiteProvider.ProviderId && sp != null && sp.Program == p && sp.Reading != null && sp.Reading.Latest == "9.9.5" && sp.Winget != null && sp.Strict,
                      "sin forma de bajarlo: abre su página, pero se guarda lo necesario para volver a intentarlo al instalar");
                Check(!string.IsNullOrEmpty(sp != null ? sp.Why : null) && fila.Detail.Contains(sp.Why.Length > 40 ? sp.Why.Substring(0, 40) : sp.Why), "y el motivo, en el detalle: " + (sp != null ? sp.Why : ""));
            }
            finally
            {
                WingetDirect.ManifestForTests = oldManifest; OfficialInstaller.RangeForTests = oldRange; SiteVersions.FetchForTests = oldFetch; SiteVersions.SourcesForTests = oldSources;
            }
        }

        // ------------------------------------------------------------------ Auditoría: límites ante datos de internet

        static void TestLimitesAuditoria2018()
        {
            AppInfo.HardenProcess();
            Check(new System.Text.RegularExpressions.Regex("x").MatchTimeout == AppInfo.RegexTimeout, "toda expresión regular nueva tiene tiempo máximo (" + new System.Text.RegularExpressions.Regex("x").MatchTimeout + ")");
            foreach (string f in new[] { "src/Actualizador.App/Program.cs", "src/Actualizador.Cli/Program.cs", "src/Actualizador.Aviso/Program.cs" })
            {
                string t = File.ReadAllText(Path.Combine(RaizDelProyecto(), f));
                int main = t.IndexOf(" Main(");
                Check(main > 0 && t.IndexOf("AppInfo.HardenProcess();", main) > main && t.IndexOf("AppInfo.HardenProcess();", main) < t.IndexOf("Log.Init", main) + (t.IndexOf("Log.Init", main) < 0 ? int.MaxValue / 2 : 0) + 1000,
                      "se activa al empezar: " + f);
            }
            // Un manifiesto con miles de niveles: error, no la pila agotada (que cierra el programa sin aviso).
            var sb = new StringBuilder();
            for (int i = 0; i < 400; i++) sb.Append(new string(' ', i)).Append("a:\n");
            sb.Append(new string(' ', 400)).Append("b: 1\n");
            bool error = false;
            try { MiniYaml.Parse(sb.ToString()); } catch (FormatException) { error = true; }
            Check(error, "YAML con 400 niveles: se rechaza");
            Check(MiniYaml.Parse("a:\n  b:\n    c: 1\n") is Dictionary<string, object>, "uno normal se sigue leyendo");
            error = false;
            try { MiniYaml.Parse(new string('#', MiniYaml.MaxChars + 1)); } catch (FormatException) { error = true; }
            Check(error, "más de 2 MB: se rechaza");
        }

        // ------------------------------------------------------------------ Al tocar «Actualizar»: otra oportunidad

        static void TestSegundaOportunidadAlInstalar2018()
        {
            var oldDownload = VendorProvider.DownloadForTests;
            var oldSig = VendorProvider.SignatureForTests;
            var oldSigners = WingetDirect.InstalledSignersForTests;
            var oldProduct = SiteVersions.ProductOfForTests;
            var oldRun = VendorProvider.RunInstallerForTests;
            var oldNow = VendorProvider.ProgramsNowForTests;
            var oldExeV = VendorProvider.ExeVersionForTests;
            var oldOpenV = VendorProvider.OpenPageForTests;
            var oldOpenS = SiteProvider.OpenForTests;
            var oldPolicy = UpdateManager.PolicyForTests;
            var oldFind = UpdateManager.FindLeadForTests;
            var oldSnap = RunningPrograms.SnapshotForTests;
            int oldWait = VendorProvider.ConfirmWaitMs, oldPoll = VendorProvider.ConfirmPollMs;
            try
            {
                VendorProvider.ConfirmWaitMs = 60; VendorProvider.ConfirmPollMs = 10;
                UpdateManager.PolicyForTests = new Policy();
                RunningPrograms.SnapshotForTests = delegate { return new List<RunningProcess>(); };
                var ctx = ContextoSimulado(true);
                var p = CdiInstalado("9.9.4");
                ctx.Programs.Add(p);
                byte[] exe = InstaladorFalso("Inno Setup Setup Data (6.4.0)", 1);
                string ahora = "9.9.4";
                var ejecutados = new List<string>();
                var abiertas = new List<string>();
                VendorProvider.DownloadForTests = delegate(string url, string file) { File.WriteAllBytes(file, exe); };
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(0, CdiSigner); };
                WingetDirect.InstalledSignersForTests = delegate(InstalledProgram x) { return new List<string> { "Open Source Developer, Noriyuki Miyazaki" }; };
                SiteVersions.ProductOfForTests = delegate(string f) { return "CrystalDiskInfo"; };
                VendorProvider.ExeVersionForTests = delegate(string f) { return "9.9.5.0"; };
                VendorProvider.RunInstallerForTests = delegate(string file, string args) { ejecutados.Add(args); ahora = "9.9.5"; return new ProcResult { Started = true, ExitCode = 0 }; };
                VendorProvider.ProgramsNowForTests = delegate { return new List<InstalledProgram> { CdiInstaladoComo(p, ahora) }; };
                VendorProvider.OpenPageForTests = delegate(string u) { abiertas.Add(u); };
                SiteProvider.OpenForTests = delegate(string u) { abiertas.Add(u); };

                // La fila como la dejó la búsqueda en el equipo del usuario: abre su página (no se encontró el instalador).
                Func<UpdateCandidate> filaSitio = delegate
                {
                    var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = p.Name, Publisher = p.Publisher, InstalledVersion = "9.9.4", Status = UpdateStatus.AlDia, ProviderName = WingetProvider.ProviderId,
                                                  ProviderTag = new WingetRow { Name = p.Name, Id = CdiId, Version = "9.9.4", Source = "winget" } };
                    SiteVersions.Apply(ctx, c, p, new SiteReading { Latest = "9.9.5", Url = CdiHome, Evidence = "CrystalDiskInfo 9.9.5" }, true, null, "«CrystalDiskInfo9_9_5.exe» no es un instalador de Windows (sourceforge.net respondió una página)");
                    return c;
                };
                Func<UpdateManager> motor = delegate
                {
                    var m = new UpdateManager(new IUpdateProvider[] { new VendorProvider(), new SiteProvider() });
                    m.HistoryPath = ""; m.RepeatUntilDone = false;
                    return m;
                };

                // --- 1) Al tocar «Actualizar» se busca de nuevo y ahora aparece: se instala, sin abrir la página.
                var fila = filaSitio();
                Check(fila.ProviderName == SiteProvider.ProviderId, "la búsqueda la había dejado para abrir su página");
                UpdateManager.FindLeadForTests = delegate(UpdateContext x, SitePlan sp)
                {
                    return new InstallerLead { Url = CdiDirect995, FileName = "CrystalDiskInfo9_9_5.exe", Origin = InstallerLead.Previous, WingetId = CdiId, VersionConfirmed = true,
                                               Winget = new WingetInstaller { Architecture = "x64", Type = "inno", Scope = "machine", Url = CdiDirect995 } };
                };
                var rep = UpdateSession.Create(ctx); rep.Updates.Add(fila);
                motor().Install(ctx, rep, new List<UpdateCandidate> { fila }, false, delegate(UpdateCandidate x) { });
                Check(fila.Status == UpdateStatus.Instalada && fila.InstalledVersion == "9.9.5" && ejecutados.Count == 1 && abiertas.Count == 0,
                      "segunda oportunidad: instalada sin abrir ninguna página (" + fila.Status + ": " + fila.Result + ")");

                // --- 2) Tampoco aparece: se abre la página y la fila dice POR QUÉ no se pudo bajar solo.
                ejecutados.Clear(); abiertas.Clear(); ahora = "9.9.4";
                fila = filaSitio();
                UpdateManager.FindLeadForTests = delegate(UpdateContext x, SitePlan sp) { return null; };
                rep = UpdateSession.Create(ctx); rep.Updates.Add(fila);
                motor().Install(ctx, rep, new List<UpdateCandidate> { fila }, false, delegate(UpdateCandidate x) { });
                Check(fila.Status == UpdateStatus.Pendiente && abiertas.Count == 1 && abiertas[0] == CdiHome && ejecutados.Count == 0, "sin instalador: abre su página");
                Check(fila.Result.StartsWith("No se pudo bajar el instalador solo (") && fila.Result.Contains("respondió una página") && fila.Result.Contains("Se abrió su página (crystalmark.info)"),
                      "y dice por qué (con la 2.0.17 sólo decía «Se abrió su página»): " + fila.Result);

                // --- 3) Aparece, pero sin administrador no se puede instalar para todo el equipo: abre la página como antes (sin romper la fila).
                abiertas.Clear();
                var ctxUser = ContextoSimulado(false); ctxUser.Programs.Add(p);
                fila = filaSitio();
                UpdateManager.FindLeadForTests = delegate(UpdateContext x, SitePlan sp)
                {
                    return new InstallerLead { Url = CdiDirect995, FileName = "CrystalDiskInfo9_9_5.exe", Origin = InstallerLead.Previous, WingetId = CdiId, VersionConfirmed = true,
                                               Winget = new WingetInstaller { Architecture = "x64", Type = "inno", Scope = "machine", Url = CdiDirect995 } };
                };
                rep = UpdateSession.Create(ctxUser); rep.Updates.Add(fila);
                motor().Install(ctxUser, rep, new List<UpdateCandidate> { fila }, false, delegate(UpdateCandidate x) { });
                Check(fila.Status == UpdateStatus.Pendiente && abiertas.Count == 1 && fila.ProviderTag is SitePlan, "sin administrador: abre la página y la fila sigue igual (" + fila.Result + ")");

                // --- 4) La política no deja usar el instalador del fabricante: ni se busca.
                int buscadas = 0;
                UpdateManager.FindLeadForTests = delegate(UpdateContext x, SitePlan sp) { buscadas++; return null; };
                UpdateManager.PolicyForTests = PoliticaDe("{ \"fuentes\": { \"fabricante\": false } }");
                fila = filaSitio();
                rep = UpdateSession.Create(ctx); rep.Updates.Add(fila);
                motor().Install(ctx, rep, new List<UpdateCandidate> { fila }, false, delegate(UpdateCandidate x) { });
                Eq(0, buscadas, "con la fuente «fabricante» apagada por la política, no se busca el instalador");
                UpdateManager.PolicyForTests = new Policy();

                // --- 5) El programa abierto se ofrece cerrar también en estas filas (CrystalDiskInfo queda en el área de notificación).
                RunningPrograms.SnapshotForTests = delegate { return new List<RunningProcess> { new RunningProcess { Pid = 4100, Name = "DiskInfo64.exe", Path = @"C:\Program Files\CrystalDiskInfo\DiskInfo64.exe" } }; };
                var abiertos = UpdateManager.FindRunning(ctx, new List<UpdateCandidate> { filaSitio() });
                Check(abiertos.Count == 1 && abiertos[0].Value.Count == 1 && abiertos[0].Value[0].Name == "DiskInfo64.exe", "la ventana de confirmación lo lista para cerrarlo (" + abiertos.Count + ")");
            }
            finally
            {
                VendorProvider.DownloadForTests = oldDownload; VendorProvider.SignatureForTests = oldSig; WingetDirect.InstalledSignersForTests = oldSigners;
                SiteVersions.ProductOfForTests = oldProduct; VendorProvider.RunInstallerForTests = oldRun; VendorProvider.ProgramsNowForTests = oldNow;
                VendorProvider.ExeVersionForTests = oldExeV; VendorProvider.OpenPageForTests = oldOpenV; SiteProvider.OpenForTests = oldOpenS;
                UpdateManager.PolicyForTests = oldPolicy; UpdateManager.FindLeadForTests = oldFind; RunningPrograms.SnapshotForTests = oldSnap;
                VendorProvider.ConfirmWaitMs = oldWait; VendorProvider.ConfirmPollMs = oldPoll;
            }
        }
    }
}
