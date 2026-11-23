// Lo que winget da «Al día», contrastado con el proyecto de GitHub de donde winget baja el instalador (2.0.4).
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    /// <summary>
    /// «Al día» de winget quiere decir «igual a la última versión que tiene el catálogo de winget», y el catálogo puede ir
    /// atrasado días o semanas. Para los paquetes cuyo instalador oficial se publica en GitHub (lo dice el propio manifiesto
    /// de winget: «InstallerUrl: https://github.com/Ultimaker/Cura/releases/download/…»), se consulta la lista de versiones
    /// de ese proyecto:
    /// <list type="bullet">
    /// <item>si hay una versión ESTABLE más nueva que la del catálogo, la fila pasa a «Disponible» (el catálogo va atrasado)
    /// y se instala con el instalador de esa versión, verificado como cualquier descarga de GitHub (SHA-256 que publica
    /// GitHub y la misma firma digital que el programa instalado); si no se puede, se abre su página;</item>
    /// <item>si lo único más nuevo es una versión preliminar (beta, RC), la fila sigue «Al día» y lo dice: así se entiende por
    /// qué el propio programa avisa de una versión nueva (Cura 5.14.0-beta.1) que Actualizador no ofrece. Nunca se
    /// instala una preliminar.</item>
    /// </list>
    /// Manda la página oficial (2.0.12): si en esta búsqueda se leyó la versión que publica la página oficial del programa y es
    /// anterior a la de GitHub, la de GitHub no se ofrece hasta que la página oficial la publique (7-Zip 26.06 en GitHub con
    /// 7-zip.org en la 26.05). Y si no se puede instalar desde acá, se abre la página oficial del programa, no la de GitHub.
    /// Sólo se compara si la numeración de GitHub es la del programa: alguna versión del proyecto tiene que coincidir con la
    /// instalada o con la del catálogo (si no, «v2024.10» contra «24.10» daría cualquier cosa). Los programas que se instalan
    /// por serie, uno al lado del otro (Python 3.13 y 3.14), no se cruzan de serie.
    /// </summary>
    public static class ReleaseCrossCheck
    {
        /// <summary>Fuente para la política de la empresa (la misma que las consultas a GitHub del fabricante).</summary>
        public const string PolicySource = "github";

        /// <summary>Sólo para las pruebas: dirección → texto (null = 404).</summary>
        internal static Func<string, string> FetchForTests;

        public const int MaxManifests = 250;
        /// <summary>GitHub admite 60 consultas por hora sin cuenta; las respuestas se guardan 6 horas.</summary>
        public const int MaxGitHubQueries = 20;
        public const int ReleasesPerPage = 30;
        static readonly TimeSpan ManifestLife = TimeSpan.FromDays(14);
        static readonly TimeSpan ReleasesLife = TimeSpan.FromHours(6);
        static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);
        static int Workers { get { return Performance.Workers(6, 2); } }   // 2.0.21: 2 en modo liviano

        static readonly Regex DownloadPath = new Regex(@"^https://github\.com/[^/]+/[^/]+/releases/download/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        sealed class Job
        {
            public UpdateCandidate Row;
            public WingetRow Winget;
            public string Repo;
            /// <summary>Fila del fabricante que ya sabe su proyecto de GitHub (Cura): no hace falta el manifiesto de winget.</summary>
            public bool Known;
        }

        static readonly Regex ApiRepo = new Regex(@"^https://api\.github\.com/repos/(?<o>[A-Za-z0-9][A-Za-z0-9-]{0,38})/(?<r>[A-Za-z0-9._-]{1,100})/releases", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// El proyecto de GitHub de una fila «Al día» del fabricante: la del catálogo que lee GitHub (Cura) o la de un programa
        /// que registra su repositorio. null si no es una fila así.
        /// </summary>
        public static string RepoOfVendorRow(UpdateCandidate c)
        {
            if (c == null || c.Kind != UpdateKind.Programa || c.Status != UpdateStatus.AlDia || c.ProviderName != VendorProvider.ProviderId) return null;
            var plan = c.ProviderTag as VendorPlan;
            if (plan == null || VersionUtil.Parse(c.InstalledVersion) == null || VersionUtil.Parse(c.AvailableVersion) == null) return null;
            if (!string.IsNullOrEmpty(plan.Repo)) return plan.Repo;
            if (plan.Check == null || plan.Check.Feed != VendorFeed.GitHubRelease) return null;
            var m = ApiRepo.Match(plan.Check.Url ?? "");
            return m.Success ? m.Groups["o"].Value + "/" + m.Groups["r"].Value : null;
        }

        /// <summary>
        /// El repositorio de GitHub de donde sale el instalador del manifiesto, o null. Sólo direcciones de descarga de una
        /// versión publicada («/releases/download/»): una página del proyecto o un archivo del código no dicen nada.
        /// </summary>
        public static string RepoOfManifest(WingetManifest m)
        {
            if (m == null) return null;
            string found = null;
            foreach (var i in m.Installers)
            {
                string url = i.Url ?? "";
                if (!DownloadPath.IsMatch(url)) continue;
                string repo = GitHubRules.RepoOf(url);
                if (repo == null) continue;
                if (found != null && !found.Equals(repo, StringComparison.OrdinalIgnoreCase)) return null;   // dos proyectos: no se sabe cuál
                found = repo;
            }
            return found;
        }

        /// <summary>¿Puede contrastarse esta fila? Programas que winget da «Al día» del repositorio winget, con identificador y versión legibles.</summary>
        public static bool Eligible(UpdateCandidate c)
        {
            if (c == null || c.Kind != UpdateKind.Programa || c.Status != UpdateStatus.AlDia || c.ProviderName != WingetProvider.ProviderId) return false;
            var w = c.ProviderTag as WingetRow;
            if (w == null || !string.Equals(w.Source, "winget", StringComparison.OrdinalIgnoreCase) || w.IdTruncated) return false;
            if (!WingetProvider.IsAsciiId(c.Identifier) || c.Identifier.IndexOf('.') <= 0) return false;
            string catalog = CatalogVersion(c);
            return catalog != null && VersionUtil.Parse(c.InstalledVersion) != null;
        }

        /// <summary>La versión del catálogo de winget para una fila «Al día» (la de la carpeta del manifiesto), o null.</summary>
        static string CatalogVersion(UpdateCandidate c)
        {
            string v = (c.AvailableVersion ?? "").Trim();
            if (v.Length == 0 || WingetTable.IsTruncated(v) || !WingetRules.IsSafeVersion(v) || VersionUtil.Parse(v) == null) return null;
            return v;
        }

        public static int Resolve(UpdateContext ctx, List<UpdateCandidate> all, List<InstalledProgram> programs, List<string> notes)
        {
            if (all == null) return 0;
            if (ctx != null && ctx.Options != null && !ctx.Options.QueryVendors) return 0;
            var jobs = new List<Job>();
            foreach (var c in all)
            {
                if (Eligible(c)) { jobs.Add(new Job { Row = c, Winget = (WingetRow)c.ProviderTag }); continue; }
                string known = RepoOfVendorRow(c);
                if (known != null) jobs.Add(new Job { Row = c, Repo = known, Known = true });
            }
            if (jobs.Count == 0) return 0;
            if (jobs.Count > MaxManifests) jobs.RemoveRange(MaxManifests, jobs.Count - MaxManifests);
            var started = DateTime.UtcNow;

            // 1) De qué proyecto de GitHub sale cada paquete (manifiesto de winget-pkgs; no cambia: se guarda dos semanas).
            var manifests = FetchForTests == null ? QueryCache.Open("winget-origen", ManifestLife) : null;
            var pending = new List<Job>();
            foreach (var j in jobs)
            {
                string cached;
                if (j.Known) continue;
                if (manifests != null && manifests.TryGet(ManifestKey(j), out cached)) j.Repo = cached.Length > 0 ? cached : null;
                else pending.Add(j);
            }
            if (pending.Count > 0) ctx.Report("Buscando de dónde publica cada programa sus versiones (" + pending.Count + ")…");
            RunParallel(ctx, pending, started, delegate(Job j)
            {
                string repo;
                if (TryReadOrigin(j, out repo))
                {
                    j.Repo = repo;
                    if (manifests != null) manifests.Put(ManifestKey(j), repo ?? "");
                }
            });
            if (manifests != null) manifests.Save();

            // 2) Las versiones publicadas en cada proyecto (una consulta por proyecto, guardada 6 horas).
            var byRepo = new Dictionary<string, List<GitHubRelease>>(StringComparer.OrdinalIgnoreCase);
            var repos = new List<string>();
            foreach (var j in jobs) if (j.Repo != null && !repos.Exists(delegate(string r) { return r.Equals(j.Repo, StringComparison.OrdinalIgnoreCase); })) repos.Add(j.Repo);
            if (repos.Count == 0) return 0;
            var releaseCache = FetchForTests == null ? QueryCache.Open("github-versiones", ReleasesLife) : null;
            var toAsk = new List<string>();
            foreach (string repo in repos)
            {
                string cached;
                if (releaseCache != null && releaseCache.TryGet(repo, out cached))
                {
                    try { byRepo[repo] = GitHubRules.ParseReleaseList(cached); } catch (Exception qx) { Log.Quiet("Caché de versiones de GitHub", qx); toAsk.Add(repo); }
                }
                else toAsk.Add(repo);
            }
            int skipped = Math.Max(0, toAsk.Count - MaxGitHubQueries);
            if (skipped > 0) toAsk.RemoveRange(MaxGitHubQueries, skipped);
            bool limited = false;
            if (toAsk.Count > 0) ctx.Report("Consultando en GitHub las versiones de " + Fmt.Plural(toAsk.Count, "programa", "programas") + "…");
            foreach (string repo in toAsk)
            {
                if (limited) { skipped++; continue; }
                ctx.ThrowIfCancelled();
                if (DateTime.UtcNow - started > Budget) { skipped++; continue; }
                try
                {
                    string json = Get(GitHubRules.ReleasesUrl(repo, ReleasesPerPage), GitHubRules.IsApi, "application/vnd.github+json");
                    if (json == null) continue;                                   // 404: el proyecto no publica versiones
                    var list = GitHubRules.ParseReleaseList(json);
                    byRepo[repo] = list;
                    if (releaseCache != null) releaseCache.Put(repo, Compact(list));
                }
                catch (OperationCanceledByUserException) { throw; }
                catch (HttpFailureException hf)
                {
                    if (hf.StatusCode == 403 || hf.StatusCode == 429) { limited = true; skipped++; }
                    else Log.Warn("GitHub " + repo + ": " + hf.Message);
                }
                catch (Exception ex) { Log.Warn("GitHub " + repo + ": " + Http.Describe(ex)); }
            }
            if (releaseCache != null) releaseCache.Save();

            // 3) Cada fila con lo publicado en su proyecto.
            int lagging = 0, betas = 0, offered = 0, held = 0;
            foreach (var j in jobs)
            {
                List<GitHubRelease> list;
                if (j.Repo == null || !byRepo.TryGetValue(j.Repo, out list)) continue;
                var p = WingetProvider.FindProgram(programs, j.Row.Name, j.Row.InstalledVersion);
                switch (Apply(ctx, j.Row, p, j.Repo, list))
                {
                    case Outcome.Lagging: lagging++; break;
                    case Outcome.PreReleaseNoted: betas++; break;
                    case Outcome.PreReleaseOffered: offered++; break;
                    case Outcome.HeldByOfficialPage: held++; break;
                }
            }
            if (lagging > 0)
                notes.Add(Fmt.Plural(lagging, "programa que winget daba «al día» tiene", "programas que winget daba «al día» tienen") +
                          " una versión estable más nueva en su proyecto de GitHub (el catálogo de winget va atrasado): se ofrece igual.");
            if (held > 0)
                notes.Add(Fmt.Plural(held, "programa tiene", "programas tienen") + " en GitHub una versión que su página oficial todavía no publica: no se " +
                          (held == 1 ? "ofrece" : "ofrecen") + " hasta que la publique la página oficial (lo dice el detalle de su fila).");
            if (betas > 0)
                notes.Add(Fmt.Plural(betas, "programa al día tiene", "programas al día tienen") + " publicada una versión preliminar (beta o RC) más nueva. " +
                          "No se ofrece: Actualizador instala sólo versiones finales. Si el programa avisa de una versión nueva, es esa beta (lo dice el detalle de su fila).");
            if (offered > 0)
                notes.Add(Fmt.Plural(offered, "versión beta se ofrece", "versiones beta se ofrecen") + " entre las opcionales (Opciones › «Ofrecer también las versiones beta»): no se marcan solas.");
            if (limited) notes.Add("GitHub limitó las consultas (admite 60 por hora sin cuenta): algunos programas no se contrastaron con su proyecto. Reintentá en una hora.");
            else if (skipped > 0) notes.Add(Fmt.Plural(skipped, "programa no se contrastó", "programas no se contrastaron") + " con su proyecto de GitHub esta vez (límite de consultas): se completan en la próxima búsqueda.");
            Log.Info("Contraste con GitHub: " + jobs.Count + " filas, " + repos.Count + " proyectos, " + toAsk.Count + " consultas, en " + Fmt.Duration(DateTime.UtcNow - started));
            return lagging + betas + offered + held;
        }

        public enum Outcome { None, Lagging, PreReleaseNoted, PreReleaseOffered, HeldByOfficialPage }

        /// <summary>
        /// El sitio de la página oficial si ésta se leyó y publica una versión ANTERIOR a <paramref name="github"/> (entonces la de
        /// GitHub no se ofrece); null si no se leyó o ya la publica.
        /// </summary>
        public static string OfficialHold(UpdateCandidate c, string github)
        {
            if (c == null || VersionUtil.Parse(c.OfficialVersion) == null || string.IsNullOrEmpty(c.OfficialPage)) return null;
            if (!VersionUtil.IsNewer(github, c.OfficialVersion)) return null;
            var u = TryUri(c.OfficialPage);
            return u != null ? u.Host : c.OfficialPage;
        }

        static Uri TryUri(string url)
        {
            Uri u;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) ? u : null;
        }

        /// <summary>
        /// Con «Ofrecer también las versiones beta»: la fila pasa a «Disponible» con la beta, pero OPCIONAL (nunca se marca sola
        /// ni entra en «Actualizar todo») y al actualizarla se abre la página de esa versión: una beta no se instala sola.
        /// </summary>
        static void MarkPreRelease(UpdateCandidate c, string repo, GitHubRelease pre)
        {
            string was = c.InstalledVersion;
            c.Status = UpdateStatus.Disponible;
            c.AvailableVersion = pre.FullVersion;
            c.Source = "GitHub (" + repo + ", beta)";
            c.Identifier = "github-beta:" + repo;
            c.InfoUrl = pre.HtmlUrl.Length > 0 ? pre.HtmlUrl : "https://github.com/" + repo + "/releases";
            c.ProviderName = SiteProvider.ProviderId;
            c.ProviderTag = null;
            c.Installable = true;
            c.Recommended = false;
            c.Detail = "Versión BETA " + pre.FullVersion + " publicada en github.com/" + repo + " (la tuya, " + was + ", es la última estable). " +
                       "La ofrecés porque activaste «Ofrecer también las versiones beta»: puede tener errores. No entra en «Actualizar todo»; " +
                       "si la marcás, se abre su página para descargarla.";
        }

        /// <summary>Aplica a la fila lo publicado en su proyecto. Ver la explicación de la clase.</summary>
        public static Outcome Apply(UpdateContext ctx, UpdateCandidate c, InstalledProgram p, string repo, List<GitHubRelease> releases)
        {
            if (c == null || releases == null || releases.Count == 0) return Outcome.None;
            // 2.0.13: fila de winget = el repositorio sale de su manifiesto (las del fabricante lo saben por el catálogo o el registro).
            bool known = c.ProviderName != WingetProvider.ProviderId;
            string installed = c.InstalledVersion ?? "";
            string catalog = CatalogVersion(c) ?? installed;
            if (VersionUtil.Parse(installed) == null) return Outcome.None;
            // La numeración del proyecto tiene que ser la del programa.
            bool sameNumbering = releases.Exists(delegate(GitHubRelease r)
            {
                return VersionUtil.Compare(r.Version, catalog) == 0 || VersionUtil.Compare(r.Version, installed) == 0;
            });
            if (!sameNumbering) return Outcome.None;
            bool series = SiteVersions.IsSideBySide(c.Name);
            var usable = releases.FindAll(delegate(GitHubRelease r) { return !series || SameSeries(r.Version, installed); });
            var stable = GitHubRules.NewestStable(usable);
            var pre = GitHubRules.NewestPreRelease(usable);
            string newestKnown = stable != null && VersionUtil.IsNewer(stable.Version, installed) ? stable.Version : installed;
            string betaText = pre != null && VersionUtil.IsNewer(pre.FullVersion, newestKnown)
                ? " En github.com/" + repo + " también hay una versión preliminar, la " + pre.FullVersion + ": no se ofrece porque no es final (Actualizador instala sólo versiones estables)."
                : "";

            if (stable != null && VersionUtil.IsNewer(stable.Version, installed) && VersionUtil.IsNewer(stable.Version, catalog))
            {
                // 2.0.12: manda la página oficial. Si se leyó y todavía publica una versión anterior, la de GitHub no se ofrece.
                string official = OfficialHold(c, stable.Version);
                if (official != null)
                {
                    c.Detail = (c.Detail ?? "").TrimEnd() + (string.IsNullOrEmpty(c.Detail) ? "" : " ") + "En github.com/" + repo + " ya figura la " + stable.Version +
                               ", pero su página oficial (" + official + ") todavía publica la " + c.OfficialVersion +
                               ": manda la página oficial, y se va a ofrecer cuando la publique." + betaText;
                    if (string.IsNullOrEmpty(c.InfoUrl) || SiteVersions.IsGitHubHost(TryUri(c.InfoUrl))) c.InfoUrl = c.OfficialPage;
                    return Outcome.HeldByOfficialPage;
                }
                MarkLagging(ctx, c, p, repo, stable, catalog, !known);
                c.Detail += betaText;
                return Outcome.Lagging;
            }
            if (betaText.Length == 0) return Outcome.None;
            if (ctx != null && ctx.Options != null && ctx.Options.IncludePreReleases)
            {
                MarkPreRelease(c, repo, pre);
                return Outcome.PreReleaseOffered;
            }
            c.Detail = (c.Detail ?? "").TrimEnd() + betaText +
                       " Si la querés igual: Opciones › «Ofrecer también las versiones beta».";
            if (string.IsNullOrEmpty(c.InfoUrl)) c.InfoUrl = pre.HtmlUrl.Length > 0 ? pre.HtmlUrl : "https://github.com/" + repo + "/releases";
            return Outcome.PreReleaseNoted;
        }

        /// <summary>
        /// El nombre del programa lleva su versión («UltiMaker Cura 5.13.0»): se instala una versión al lado de la otra, en
        /// otra carpeta, y la vieja queda. Instalarla desde acá dejaría dos copias; se abre su página.
        /// </summary>
        /// <summary>
        /// 2.0.12: ¿se instala una versión al lado de la otra? El nombre lleva la versión Y la carpeta también
        /// («C:\Program Files\UltiMaker Cura 5.13.0»). Con la carpeta sin versión («C:\Program Files\7-Zip», aunque el nombre sea
        /// «7-Zip 26.05 (x64)») el instalador reemplaza al anterior. Sin carpeta conocida, por las dudas, sí.
        /// </summary>
        public static bool InstallsSideBySide(string name, string version, string installDir)
        {
            if (!VersionInName(name, version)) return false;
            string dir = (installDir ?? "").Trim().Trim('"').TrimEnd('\\', '/');
            if (dir.Length == 0) return true;
            int cut = Math.Max(dir.LastIndexOf('\\'), dir.LastIndexOf('/'));
            string leaf = cut >= 0 ? dir.Substring(cut + 1) : dir;
            return VersionInName(leaf, version);
        }

        public static bool VersionInName(string name, string version)
        {
            string v = (version ?? "").Trim();
            if (v.Length < 3 || string.IsNullOrEmpty(name)) return false;
            return Regex.IsMatch(name, @"(?<![\w.])v?" + Regex.Escape(v) + @"(?![\w.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        static void MarkLagging(UpdateContext ctx, UpdateCandidate c, InstalledProgram p, string repo, GitHubRelease stable, string catalog, bool fromWinget)
        {
            string lead = "winget la daba al día (su catálogo llega a la " + catalog + "), pero el proyecto de donde winget baja el instalador, github.com/" + repo +
                          ", ya publica la " + stable.Version + " como versión estable: el catálogo de winget va atrasado.";
            c.Status = UpdateStatus.Disponible;
            c.AvailableVersion = stable.Version;
            c.Source = "GitHub (" + repo + ")";
            c.Identifier = "github:" + repo;
            c.InfoUrl = stable.HtmlUrl.Length > 0 ? stable.HtmlUrl : "https://github.com/" + repo + "/releases/latest";

            string why = null;
            GitHubAsset asset = null;
            VendorPlan plan = null;
            if (p == null || p.Unregistered) why = "no se encontró su entrada en «Aplicaciones» para verificar la firma";
            else if (InstallsSideBySide(p.Name, p.Version, VendorProvider.InstallDirOf(p))) why = "se instala una versión al lado de la otra (el nombre lleva la versión): instalala desde su página y después desinstalá la vieja";
            else if (VendorProvider.IsOtherUser(p)) why = "está instalado para otro usuario: actualizalo desde su sesión";
            else
            {
                plan = new VendorPlan();
                plan.Repo = repo;
                plan.RepoFromWinget = fromWinget;
                plan.Programs.Add(p);
                plan.CoveredNames.Add(p.Name.Trim());
                plan.InstallDir = VendorProvider.InstallDirOf(p);
                plan.Machine = !VendorProvider.IsPerUser(p);
                plan.Latest = stable.Version;
                plan.Arch = VendorProvider.ArchitectureOf(ctx, p);
                asset = GitHubRules.PickAsset(stable, plan.Arch, WingetDirect.LooksMsi(p));
                bool needsAdmin = plan.Machine && (plan.InstallDir.Length == 0 || !SafePaths.IsUserWritableLocation(plan.InstallDir));
                bool admin = ctx != null && ctx.IsAdmin;
                if (asset == null) why = "entre los archivos de esa versión no hay un instalador claro para " + plan.Arch;
                else if (asset.Sha256.Length != 64) why = "GitHub no publica el SHA-256 del instalador";
                else if (needsAdmin && !admin) why = "instalarla para todo el equipo requiere ejecutar Actualizador como administrador";
                // 2.0.20: sólo para el usuario y Actualizador como administrador: el instalador se ejecuta como el usuario (antes se abría
                // su página: Ollama 0.40.16 → 0.40.17 «por seguridad no se instala desde acá»).
                else if (VendorProvider.InstallsAsUser(admin, p, plan.InstallDir, plan.Machine)) plan.RunAsUser = true;
            }

            if (why == null)
            {
                plan.InstallerUrl = asset.Url;
                plan.InstallerSha256 = asset.Sha256;
                c.ProviderName = VendorProvider.ProviderId;
                c.ProviderTag = plan;
                c.Installable = true;
                c.Recommended = true;
                c.Detail = lead + " Se descarga «" + asset.Name + "» y sólo se instala si coincide con el SHA-256 que publica GitHub, es un instalador reconocido " +
                           "y tiene una firma digital válida del mismo firmante que el programa instalado" +
                           (fromWinget ? " (si el autor no firma sus instaladores y lo instalado tampoco está firmado: sin firma, pero sólo si el paquete es del mismo " +
                                         "producto que lo instalado y de la versión " + stable.Version + ")" : "") + "." +
                           (plan.RunAsUser ? VendorProvider.AsUserNote : "");
                return;
            }
            // Sin instalador verificable: se puede marcar igual y al actualizar se abre su página (como las páginas oficiales).
            // 2.0.12: la página OFICIAL del programa si la registra (no la de GitHub).
            string officialPage = OfficialPageOf(p);
            if (officialPage != null) c.InfoUrl = officialPage;
            c.ProviderName = SiteProvider.ProviderId;
            c.ProviderTag = null;
            c.Installable = true;
            c.Recommended = false;
            c.Detail = lead + " No se instala desde acá: " + why + ". Al actualizarla se abre su página " +
                       (officialPage != null ? "oficial (" + TryUri(officialPage).Host + ")." : "de descarga.");
        }

        /// <summary>La página oficial que registra el programa (la de actualizaciones primero) que no sea de GitHub, o null.</summary>
        static string OfficialPageOf(InstalledProgram p)
        {
            if (p == null) return null;
            foreach (string page in SiteVersions.PagesFor(p, null))
            {
                var u = TryUri(page);
                if (u != null && u.Scheme == Uri.UriSchemeHttps && !SiteVersions.IsGitHubHost(u)) return page;
            }
            return null;
        }

        static bool SameSeries(string a, string b)
        {
            long[] x = VersionUtil.Parse(a), y = VersionUtil.Parse(b);
            return x != null && y != null && x.Length >= 2 && y.Length >= 2 && x[0] == y[0] && x[1] == y[1];
        }

        static string ManifestKey(Job j) { return j.Row.Identifier + "|" + CatalogVersion(j.Row); }

        /// <summary>Lee el manifiesto de la versión del catálogo. false si no se pudo leer (no se guarda: se reintenta la próxima vez).</summary>
        static bool TryReadOrigin(Job j, out string repo)
        {
            repo = null;
            string version = CatalogVersion(j.Row);
            try
            {
                string text = Get(WingetRules.ManifestUrl(j.Row.Identifier, version, false), WingetRules.IsGitHubRaw, "text/plain")
                              ?? Get(WingetRules.ManifestUrl(j.Row.Identifier, version, true), WingetRules.IsGitHubRaw, "text/plain");
                if (text == null) return true;                                     // no está esa carpeta: sin origen conocido
                var m = WingetManifest.Parse(text);
                if (!string.Equals(m.Id, j.Row.Identifier, StringComparison.OrdinalIgnoreCase)) return true;
                repo = RepoOfManifest(m);
                return true;
            }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception ex) { Log.Info("Manifiesto de " + j.Row.Identifier + " " + version + ": " + Http.Describe(ex)); return false; }
        }

        /// <summary>GET con las mismas reglas que el resto (HTTPS, dominio permitido). null en un 404.</summary>
        static string Get(string url, Func<Uri, bool> allowed, string accept)
        {
            if (url == null) return null;
            if (FetchForTests != null) return FetchForTests(url);
            try { return Http.GetString(url, 15000, allowed, accept); }
            catch (HttpFailureException hf)
            {
                if (hf.StatusCode == 404) return null;
                throw;
            }
        }

        /// <summary>Lo necesario de la lista de versiones, para guardarla (la respuesta de GitHub trae mucho más).</summary>
        static string Compact(List<GitHubRelease> list)
        {
            var arr = new List<object>();
            foreach (var r in list)
            {
                var d = new Dictionary<string, object>();
                d["tag_name"] = r.Tag;
                d["html_url"] = r.HtmlUrl;
                d["prerelease"] = r.PreRelease;
                var assets = new List<object>();
                foreach (var a in r.Assets)
                {
                    var ad = new Dictionary<string, object>();
                    ad["name"] = a.Name;
                    ad["browser_download_url"] = a.Url;
                    ad["size"] = a.Size;
                    if (a.Sha256.Length == 64) ad["digest"] = "sha256:" + a.Sha256;
                    assets.Add(ad);
                }
                d["assets"] = assets;
                arr.Add(d);
            }
            return Json.Serialize(arr, false);
        }

        static void RunParallel(UpdateContext ctx, List<Job> work, DateTime started, Action<Job> action)
        {
            if (work.Count == 0) return;
            int next = -1;
            var threads = new List<Thread>();
            for (int t = 0; t < Math.Min(FetchForTests != null ? 1 : Workers, work.Count); t++)
            {
                var th = new Thread(delegate()
                {
                    ProcessRunner.LowPriorityForThisThread = true;
                    while (true)
                    {
                        int i = Interlocked.Increment(ref next);
                        if (i >= work.Count || ctx.CancelRequested || DateTime.UtcNow - started > Budget) return;
                        try { action(work[i]); }
                        catch (OperationCanceledByUserException) { return; }
                        catch (Exception ex) { Log.Warn("Contraste con GitHub: " + ex.Message); }
                    }
                });
                th.IsBackground = true;
                th.Name = "origen-" + t;
                threads.Add(th);
                th.Start();
            }
            foreach (var th in threads) while (!th.Join(500)) if (ctx.CancelRequested) break;
            ctx.ThrowIfCancelled();
        }
    }
}
