// Proyectos publicados en GitHub: última versión estable y elección del instalador.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Un archivo publicado en una versión de GitHub.</summary>
    public sealed class GitHubAsset
    {
        public string Name = "", Url = "", Sha256 = "";
        public long Size;
    }

    /// <summary>La última versión estable publicada por un proyecto en GitHub.</summary>
    public sealed class GitHubRelease
    {
        public string Tag = "", Version = "", HtmlUrl = "";
        /// <summary>La versión con su sufijo («5.14.0-beta.1»): la que se muestra para una preliminar.</summary>
        public string FullVersion = "";
        /// <summary>Beta, RC, alpha… (GitHub la marca así o lo dice la etiqueta). Nunca se ofrece para instalar.</summary>
        public bool PreRelease;
        public readonly List<GitHubAsset> Assets = new List<GitHubAsset>();
    }

    /// <summary>
    /// Programas cuyo registro apunta a su repositorio de GitHub (URLInfoAbout, URLUpdateInfo o
    /// HelpLink): la versión publicada es la «latest release» del proyecto, que GitHub marca como
    /// la última estable (nunca un borrador ni una preliminar).
    /// </summary>
    public static class GitHubRules
    {
        static readonly Regex RepoRx = new Regex(@"^https?://(?:www\.)?github\.com/(?<o>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<r>[A-Za-z0-9._-]{1,100})(?:[/?#]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "about", "apps", "collections", "contact", "customer-stories", "enterprise", "events", "features", "login", "join",
            "marketplace", "orgs", "pricing", "readme", "security", "settings", "site", "sponsors", "topics", "trending", "users"
        };

        /// <summary>«propietario/repositorio» de una dirección de GitHub, o null.</summary>
        public static string RepoOf(string url)
        {
            string u = Fmt.WebUrl(url);
            if (u == null) return null;
            var m = RepoRx.Match(u);
            if (!m.Success) return null;
            string o = m.Groups["o"].Value, r = m.Groups["r"].Value;
            if (r.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) r = r.Substring(0, r.Length - 4);
            if (Reserved.Contains(o) || r.Length == 0 || r.Trim('.').Length == 0) return null;
            return o + "/" + r;
        }

        /// <summary>El primer repositorio de GitHub que registra el programa, o null.</summary>
        public static string RepoOf(InstalledProgram p)
        {
            if (p == null) return null;
            return RepoOf(p.UpdateUrl) ?? RepoOf(p.InfoUrl) ?? RepoOf(p.HelpLink);
        }

        public static string LatestReleaseUrl(string repo)
        {
            return "https://api.github.com/repos/" + repo + "/releases/latest";
        }

        public static bool IsApi(Uri u) { return Http.IsHttps(u) && u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase); }

        /// <summary>Las descargas de GitHub redirigen a su propio almacenamiento (*.githubusercontent.com).</summary>
        public static bool IsDownload(Uri u)
        {
            if (!Http.IsHttps(u)) return false;
            string h = u.Host.ToLowerInvariant();
            return h == "github.com" || (h.EndsWith(".githubusercontent.com") && h.Length > ".githubusercontent.com".Length);
        }

        static readonly Regex VersionInTag = new Regex(@"\d+(?:\.\d+){1,3}", RegexOptions.Compiled);
        static readonly Regex Hex64 = new Regex(@"^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

        /// <summary>Respuesta de /releases/latest. null si es un borrador, una preliminar o la etiqueta no tiene número.</summary>
        public static GitHubRelease ParseRelease(string json)
        {
            return ParseReleaseObject(Json.Parse(json), false);
        }

        /// <summary>«/releases?per_page=…»: todas las versiones de la página (sin borradores ni etiquetas sin número), estables y preliminares.</summary>
        public static List<GitHubRelease> ParseReleaseList(string json)
        {
            var list = new List<GitHubRelease>();
            var items = Json.Parse(json) as List<object>;
            if (items == null) return list;
            foreach (object it in items)
            {
                var r = ParseReleaseObject(it, true);
                if (r != null) list.Add(r);
            }
            return list;
        }

        public static string ReleasesUrl(string repo, int perPage)
        {
            return "https://api.github.com/repos/" + repo + "/releases?per_page=" + Math.Max(1, Math.Min(100, perPage));
        }

        /// <summary>La versión estable más nueva de la lista (por número, no por fecha), o null.</summary>
        public static GitHubRelease NewestStable(List<GitHubRelease> list)
        {
            GitHubRelease best = null;
            if (list == null) return null;
            foreach (var r in list)
                if (!r.PreRelease && (best == null || VersionUtil.IsNewer(r.Version, best.Version))) best = r;
            return best;
        }

        /// <summary>La preliminar (beta, RC…) más nueva de la lista, comparada con su etiqueta completa, o null.</summary>
        public static GitHubRelease NewestPreRelease(List<GitHubRelease> list)
        {
            GitHubRelease best = null;
            if (list == null) return null;
            foreach (var r in list)
                if (r.PreRelease && (best == null || VersionUtil.IsNewer(r.FullVersion, best.FullVersion))) best = r;
            return best;
        }

        static GitHubRelease ParseReleaseObject(object root, bool allowPreRelease)
        {
            if (root == null || Json.Str(root, "draft") == "true") return null;
            var r = new GitHubRelease();
            r.Tag = Json.Str(root, "tag_name") ?? "";
            r.HtmlUrl = Fmt.WebUrl(Json.Str(root, "html_url")) ?? "";
            var m = VersionInTag.Match(r.Tag);
            if (!m.Success) return null;
            // Preliminar: la marca GitHub o la dice la etiqueta («5.14.0-beta.1», «v2.0-rc1»).
            r.PreRelease = Json.Str(root, "prerelease") == "true" || VersionUtil.IsPreRelease(r.Tag);
            if (r.PreRelease && !allowPreRelease) return null;
            r.Version = m.Value;
            string full = r.Tag.Substring(m.Index);
            r.FullVersion = Fmt.Truncate(full.Trim(), 64);
            foreach (object a in Json.List(root, "assets"))
            {
                var asset = new GitHubAsset();
                asset.Name = Json.Str(a, "name") ?? "";
                asset.Url = Json.Str(a, "browser_download_url") ?? "";
                long size;
                long.TryParse(Json.Str(a, "size") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                asset.Size = size;
                string digest = Json.Str(a, "digest") ?? "";
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && Hex64.IsMatch(digest.Substring(7)))
                    asset.Sha256 = digest.Substring(7).ToLowerInvariant();
                Uri u;
                if (asset.Name.Length > 0 && Uri.TryCreate(asset.Url, UriKind.Absolute, out u) && IsDownload(u)) r.Assets.Add(asset);
            }
            return r;
        }

        static readonly Regex Excluded = new Regex(@"portable|debug|symbol|pdb|source|src\b|uninstall|update(?:r)?[-_.]?only|\.(?:sig|asc|sha\d*)$|checksum", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex X64 = new Regex(@"(?:x64|x86[-_]64|amd64|win64|64[-_]?bit)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex X86 = new Regex(@"(?:x86(?![-_]64)|win32|i[36]86|32[-_]?bit)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Arm = new Regex(@"(?:arm64|aarch64)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SetupWord = new Regex(@"setup|install", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static string ArchOf(string name)
        {
            if (Arm.IsMatch(name)) return "arm64";
            if (X64.IsMatch(name)) return "x64";
            if (X86.IsMatch(name)) return "x86";
            return "";
        }

        /// <summary>
        /// El instalador que corresponde: .msi o .exe, sin portables ni símbolos, de la arquitectura
        /// instalada (o sin arquitectura en el nombre). Si el programa se instaló con MSI, sólo un .msi;
        /// si no, sólo un .exe. null si no hay uno claro. Que sea de verdad un instalador (Inno Setup, NSIS o MSI)
        /// se comprueba después de descargarlo, por su contenido.
        /// </summary>
        public static GitHubAsset PickAsset(GitHubRelease release, string arch, bool preferMsi)
        {
            if (release == null) return null;
            GitHubAsset best = null;
            int bestScore = int.MinValue;
            foreach (var a in release.Assets)
            {
                string n = a.Name;
                bool msi = n.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
                bool exe = n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if ((!msi && !exe) || Excluded.IsMatch(n)) continue;
                // La misma tecnología que la instalada: un .msi sobre una instalación de Inno/NSIS (o al
                // revés) no la reemplaza, deja dos copias del programa y dos entradas para desinstalar.
                if (msi != preferMsi) continue;
                string aa = ArchOf(n);
                int score = 0;
                if (aa.Length == 0) score += 10;
                else if (aa == arch) score += 20;
                else continue;
                if (SetupWord.IsMatch(n)) score += 3;
                if (score > bestScore) { best = a; bestScore = score; }
            }
            return best;
        }
    }
}
