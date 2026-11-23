using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    /// <summary>Lo que el proveedor sabe de una fila: qué programas cubre y cómo instalarla.</summary>
    public sealed class VendorPlan
    {
        /// <summary>Entrada del catálogo; null para un programa verificado por su repositorio de GitHub.</summary>
        public VendorCheck Check;
        public readonly List<InstalledProgram> Programs = new List<InstalledProgram>();
        /// <summary>Nombres de «Programas instalados» que representa la fila (winget los repite como otras filas).</summary>
        public readonly List<string> CoveredNames = new List<string>();
        public string InstallDir = "", Arch = "", Latest = "";
        public bool Machine = true;
        public VendorDownload Download;                  // AIMP
        public string InstallerUrl;                      // instalador firmado (catálogo o GitHub)
        public string InstallerSha256 = "";              // GitHub: SHA-256 que publica GitHub para el archivo
        /// <summary>El SHA-256 es fijo en el catálogo (Visual C++): si coincide, el archivo es exactamente el oficial.</summary>
        public bool PinnedSha256;
        public readonly List<string> MspUrls = new List<string>();   // Adobe
        public string Repo;                              // GitHub: propietario/repositorio
        /// <summary>
        /// 2.0.13: el repositorio sale del manifiesto de winget (el proyecto de donde winget baja el instalador), no del registro. Si el
        /// autor no firma (Double Commander) y lo instalado tampoco está firmado, vale el SHA-256 de GitHub más la identidad del paquete.
        /// </summary>
        public bool RepoFromWinget;
        /// <summary>Página oficial de donde salió el instalador: se instala con las reglas de GitHub (mismo firmante) y que sea de este programa.</summary>
        public string SitePage;
        /// <summary>
        /// 2.0.17: el instalador se ubicó para la versión que publica la página (<see cref="SiteInstallers"/>): por winget-pkgs (con su SHA-256
        /// o con los interruptores del manifiesto) o por un enlace de la página. null si el enlace estaba a la vista en la página.
        /// </summary>
        public InstallerLead Lead;
        /// <summary>2.0.17: el instalador llegó a ejecutarse (si falló después, la causa es del instalador: no se abre la página).</summary>
        public bool InstallerRan;
        /// <summary>2.0.17: se llegó a buscar o verificar el instalador (el programa no estaba abierto ni se canceló antes).</summary>
        public bool Locating;
        public string[] Processes = new string[0];
        /// <summary>
        /// 2.0.20: el programa está instalado sólo para el usuario y Actualizador corre como administrador: el instalador verificado
        /// se ejecuta como el usuario, sin elevar (antes no se instalaba y se abría su página: Ollama 0.40.16 → 0.40.17).
        /// </summary>
        public bool RunAsUser;
        /// <summary>
        /// 2.0.19: el archivo del perfil donde el programa anota su versión (<see cref="VendorCheck.ProfileVersionFiles"/>), si se encontró.
        /// Con <see cref="VendorInstall.Starter4j"/>, la carpeta de ese archivo es la de trabajo del arranque.
        /// </summary>
        public string ProfileFile;

        public InstalledProgram Program { get { return Programs.Count > 0 ? Programs[0] : null; } }
        public string Product { get { return Check != null && !string.IsNullOrEmpty(Check.Product) ? Check.Product : Program != null ? Program.Name : ""; } }
    }

    /// <summary>
    /// Consulta la versión en la fuente oficial de cada fabricante (<see cref="VendorCatalog"/>) y en
    /// GitHub para los programas que registran su repositorio, y los instala verificados.
    /// </summary>
    public sealed partial class VendorProvider : IUpdateProvider
    {
        public const string ProviderId = "fabricante";
        /// <summary>GitHub admite 60 consultas por hora sin cuenta: se usa como mucho una cuarta parte por búsqueda.</summary>
        public const int MaxGitHubQueries = 15;

        public bool Interactive;

        /// <summary>Cerrar el programa abierto antes de instalar (lo aceptó el usuario). No se aplica a Office.</summary>
        public bool CloseRunning;

        public string Name { get { return ProviderId; } }

        public string Unavailable(UpdateContext ctx)
        {
            if (ctx.Os == null) return "La verificación con fabricantes sólo funciona en Windows.";
            if (ctx.Options != null && !ctx.Options.QueryVendors)
                return "La consulta a los sitios de los fabricantes está desactivada (en Opciones o con --sin-fabricantes): los programas que winget no conoce quedan como «No verificable».";
            return null;
        }

        // ================================================================= Búsqueda

        sealed class FeedCache
        {
            public readonly Dictionary<string, string> Pages = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Errors = new Dictionary<string, string>(StringComparer.Ordinal);
            /// <summary>2.0.15: instaladores oficiales ya consultados (dirección → lo averiguado).</summary>
            public readonly Dictionary<string, InstallerProbe> Probes = new Dictionary<string, InstallerProbe>(StringComparer.Ordinal);
        }

        public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes)
        {
            Http.EnableModernTls();
            var cache = new FeedCache();
            var errors = new Dictionary<string, string>();
            var covered = new HashSet<InstalledProgram>();
            foreach (var check in VendorCatalog.All)
            {
                var matches = new List<InstalledProgram>();
                // 2.0.8: lo que sólo se compara (no se instala desde acá) también se busca en las copias portables, encontradas por su
                // acceso directo (Tor Browser se descomprime en el Escritorio, sin entrada en el registro).
                foreach (var p in ctx.Programs) if (p != null && (!p.Unregistered || check.Install == VendorInstall.None || check.Install == VendorInstall.Starter4j) && check.Applies(p)) matches.Add(p);
                if (matches.Count == 0) continue;
                var groups = new Dictionary<string, List<InstalledProgram>>(StringComparer.OrdinalIgnoreCase);
                var order = new List<string>();
                foreach (var p in matches)
                {
                    string key = check.OneRowForAll ? "" : p.Name.Trim() + "|" + p.Scope;
                    List<InstalledProgram> g;
                    if (!groups.TryGetValue(key, out g)) { g = new List<InstalledProgram>(); groups[key] = g; order.Add(key); }
                    g.Add(p);
                }
                foreach (string key in order)
                {
                    ctx.ThrowIfCancelled();
                    UpdateCandidate c;
                    try { c = Evaluate(ctx, check, groups[key], matches, cache); }
                    catch (OperationCanceledByUserException) { throw; }
                    catch (Exception ex)
                    {
                        // Un error de un fabricante no puede tirar la búsqueda de los demás.
                        Log.Error("Consulta a " + check.Id, ex);
                        c = null;
                    }
                    if (c == null) continue;
                    if (c.Status == UpdateStatus.Error) errors[check.Product] = ErrorText(c);
                    output.Add(c);
                    foreach (var p in groups[key]) covered.Add(p);
                }
            }
            ScanGitHub(ctx, covered, output, notes);
            if (errors.Count > 0)
                notes.Add("No se pudo consultar al fabricante de " + Fmt.JoinY(errors.Keys) + ": " + Fmt.JoinDistinct(errors.Values) + ". Se muestra lo que informa winget.");
        }

        static string ErrorText(UpdateCandidate c)
        {
            var plan = c.ProviderTag as VendorPlan;
            return plan != null && plan.Latest.Length > 0 ? plan.Latest : (c.Result ?? "error");
        }

        static UpdateCandidate NewCandidate(InstalledProgram p, string source, string id, VendorPlan plan)
        {
            var c = new UpdateCandidate();
            c.Kind = UpdateKind.Programa;
            c.Name = p.Name;
            c.Publisher = p.Publisher;
            c.Identifier = id;
            c.ProviderName = ProviderId;
            c.Source = source;
            c.Installable = false;
            c.ProviderTag = plan;
            return c;
        }

        internal static string InstallDirOf(InstalledProgram p)
        {
            string dir = BrowserCatalog.InstallDirOf(p);
            if (dir.Length > 0 && dir.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) dir = Path.GetDirectoryName(dir) ?? "";
            if (dir.Length == 0) dir = DirOfUninstaller(p.UninstallString);
            return dir ?? "";
        }

        /// <summary>«Usuario actual», «Usuario PC\otra»: instalado sólo para un usuario.</summary>
        internal static bool IsPerUser(InstalledProgram p) { return (p.Scope ?? "").StartsWith("Usuario", StringComparison.OrdinalIgnoreCase); }
        internal static bool IsOtherUser(InstalledProgram p) { return IsPerUser(p) && !(p.Scope ?? "").Equals("Usuario actual", StringComparison.OrdinalIgnoreCase); }

        /// <summary>
        /// 2.0.20: ¿el instalador se ejecuta como el usuario, sin elevar? Si el programa es sólo del usuario actual, está en una
        /// carpeta suya y Actualizador corre como administrador. Así no hay nada que el usuario pueda aprovechar (el instalador
        /// corre con sus mismos permisos) y el programa se actualiza en vez de abrir su página.
        /// </summary>
        public const string AsUserNote = " Está instalado sólo para tu usuario: el instalador se ejecuta como tu usuario, sin privilegios de administrador.";

        public static bool InstallsAsUser(bool admin, InstalledProgram p, string installDir, bool machine)
        {
            if (!admin || p == null || machine || !IsPerUser(p) || IsOtherUser(p)) return false;
            return string.IsNullOrEmpty(installDir) || SafePaths.IsUserWritableLocation(installDir);
        }

        /// <summary>Sólo para las pruebas: reemplaza la descarga de las fuentes de los fabricantes (dirección → contenido).</summary>
        internal static Func<string, string> FetchForTests;

        /// <summary>
        /// La versión más nueva de Acrobat/Reader PARA WINDOWS: el índice de notas de versión lista la
        /// más nueva primero; se toma la primera nota que publica parches de Windows (alguna puede ser
        /// sólo para Mac). null si ninguna de las primeras las publica.
        /// </summary>
        AdobeWindowsRelease LatestForWindows(UpdateContext ctx, VendorCheck check, FeedCache cache)
        {
            var pages = AdobeRules.ReleasePages(Fetch(ctx, check, check.Url, "text/html", cache), check.Url);
            foreach (string page in pages)
            {
                Uri u;
                if (!Uri.TryCreate(page, UriKind.Absolute, out u) || !check.Allows(u)) continue;
                var release = AdobeRules.ParseWindowsRelease(Fetch(ctx, check, page, "text/html", cache));
                if (release != null) return release;
                Log.Info(check.Product + ": " + Path.GetFileName(u.AbsolutePath) + " no publica parches para Windows; se revisa la nota anterior");
            }
            return null;
        }

        /// <summary>
        /// 2.0.12: el SHA-256 que publica el autor para el instalador que enlaza la página oficial (<see cref="VendorCheck.HashRepo"/>),
        /// o null. Es sólo el hash de ESE archivo: la versión la decidió la página oficial.
        /// </summary>
        string PublishedHash(UpdateContext ctx, VendorCheck check, string installerUrl, string latest, FeedCache cache)
        {
            string file;
            string api = VendorCatalog.HashQueryUrl(check, installerUrl, latest, out file);
            if (api == null) return null;
            if (!(UpdateManager.PolicyForTests ?? Actualizador.Core.Security.Policy.Current).IsSourceAllowed("github"))
            {
                Log.Info(check.Product + ": la política de la empresa no permite consultar GitHub; sin el SHA-256 publicado no se instala desde acá");
                return null;
            }
            string json, err;
            if (!cache.Pages.TryGetValue(api, out json))
            {
                if (cache.Errors.TryGetValue(api, out err)) return null;
                try
                {
                    ctx.Report("Leyendo el SHA-256 publicado del instalador de " + check.Product + "…");
                    json = FetchForTests != null ? FetchForTests(api)
                         : RecentPages.Get("github " + api, delegate { return Http.GetString(api, 15000, GitHubRules.IsApi, "application/vnd.github+json"); });
                    cache.Pages[api] = json ?? "";
                }
                catch (OperationCanceledByUserException) { throw; }
                catch (Exception ex)
                {
                    cache.Errors[api] = Http.Describe(ex);
                    Log.Warn(check.Product + ": no se pudo leer el SHA-256 publicado (" + cache.Errors[api] + ")");
                    return null;
                }
            }
            string sha = VendorCatalog.PublishedSha256(json, file);
            if (sha == null) Log.Warn(check.Product + ": github.com/" + check.HashRepo + " no publica el SHA-256 de «" + file + "»");
            return sha;
        }

        /// <summary>«Fortinet publica la 7.4.3.8758 (el instalador oficial de su página de descargas, FortiClientVPNInstaller.exe)».</summary>
        static string FortinetLead(VendorCheck check, InstallerProbe probe)
        {
            string who = check.Vendor ?? "El fabricante";
            int paren = who.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) who = who.Substring(0, paren);
            return who + " publica la " + probe.Version + " (el instalador oficial de su página de descargas, " + probe.FileName + ")";
        }

        /// <summary>
        /// 2.0.15: el instalador oficial fijo de la entrada (<see cref="OfficialInstaller"/>), una sola vez por búsqueda. null con el
        /// motivo en <paramref name="error"/>.
        /// </summary>
        InstallerProbe Probe(UpdateContext ctx, VendorCheck check, string url, FeedCache cache, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(url)) { error = "no hay un instalador oficial para esta arquitectura"; return null; }
            InstallerProbe probe;
            string err;
            if (cache.Probes.TryGetValue(url, out probe)) return probe;
            if (cache.Errors.TryGetValue(url, out err)) { error = err; return null; }
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || !check.Allows(u)) { error = "dirección no permitida"; return null; }
            ctx.Report("Consultando la última versión de " + check.Product + " en " + u.Host + "…");
            probe = OfficialInstaller.Probe(url, check.Allows, check.Version, check.VersionShape, 20000, out err);
            if (probe == null) { cache.Errors[url] = err ?? "sin respuesta"; error = cache.Errors[url]; return null; }
            cache.Probes[url] = probe;
            return probe;
        }

        string Fetch(UpdateContext ctx, VendorCheck check, string url, string accept, FeedCache cache)
        {
            string page, err;
            if (cache.Pages.TryGetValue(url, out page)) return page;
            if (cache.Errors.TryGetValue(url, out err)) throw new InvalidOperationException(err);
            ctx.Report("Consultando la última versión de " + check.Product + " en " + new Uri(url).Host + "…");
            try
            {
                page = FetchForTests != null ? FetchForTests(url)
                     : RecentPages.Get(accept + " " + url, delegate { return Http.GetString(url, 20000, check.Allows, accept); });
                if (page == null) throw new InvalidOperationException("sin respuesta");
                cache.Pages[url] = page;
                return page;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledByUserException) throw;
                cache.Errors[url] = Http.Describe(ex);
                throw new InvalidOperationException(cache.Errors[url], ex);
            }
        }

        /// <summary>
        /// 2.0.16: la fuente de la entrada o, si no responde, sus direcciones alternativas (<see cref="VendorCheck.MirrorUrls"/>), en orden.
        /// Si ninguna responde, el error es el de la principal.
        /// </summary>
        string FetchWithMirrors(UpdateContext ctx, VendorCheck check, string accept, FeedCache cache)
        {
            try { return Fetch(ctx, check, check.Url, accept, cache); }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception first)
            {
                if (check.MirrorUrls != null)
                    foreach (string mirror in check.MirrorUrls)
                    {
                        Uri u;
                        if (string.IsNullOrEmpty(mirror) || !Uri.TryCreate(mirror, UriKind.Absolute, out u) || !check.Allows(u)) continue;
                        try
                        {
                            string page = Fetch(ctx, check, mirror, accept, cache);
                            Log.Info(check.Product + ": " + new Uri(check.Url).Host + " no respondió (" + first.Message + "); se leyó de " + u.Host);
                            return page;
                        }
                        catch (OperationCanceledByUserException) { throw; }
                        catch (Exception ex) { Log.Quiet(check.Product + ": " + u.Host, ex); }
                    }
                throw;
            }
        }

        /// <summary>Sólo para las pruebas: las carpetas «AppData\Roaming» donde buscar (null: las de las cuentas del equipo).</summary>
        internal static Func<InstalledProgram, List<string>> RoamingFoldersForTests;

        static readonly System.Text.RegularExpressions.Regex RoamingInPath = new System.Text.RegularExpressions.Regex(
            @"(?<r>[A-Za-z]:\\[^""|<>*?\r\n]{1,200}?\\AppData\\Roaming)(?=\\|$|"")", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// 2.0.16: carpetas «AppData\Roaming» donde un programa por usuario anota su versión, en orden: la de la cuenta donde está
        /// instalado (si su carpeta, su desinstalador o su icono están en un perfil), la de quien ejecuta Actualizador y las de las
        /// demás cuentas del equipo.
        /// </summary>
        internal static List<string> RoamingFolders(InstalledProgram p)
        {
            var l = new List<string>();
            Action<string> add = delegate(string d)
            {
                if (string.IsNullOrEmpty(d)) return;
                string x = d.Trim().TrimEnd('\\', '/');
                if (x.Length > 3 && !l.Exists(delegate(string e) { return e.Equals(x, StringComparison.OrdinalIgnoreCase); })) l.Add(x);
            };
            if (p != null)
                foreach (string src in new[] { p.InstallLocation, p.DisplayIcon, p.UninstallString })
                {
                    var m = RoamingInPath.Match(src ?? "");
                    if (m.Success) add(m.Groups["r"].Value);
                }
            try { add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)); } catch (Exception ex) { Log.Quiet("Carpeta de datos del usuario", ex); }
            try { foreach (string profile in ShortcutPrograms.Profiles()) add(Path.Combine(profile, @"AppData\Roaming")); }
            catch (Exception ex) { Log.Quiet("Perfiles del equipo", ex); }
            return l;
        }

        /// <summary>
        /// 2.0.16: la versión que el propio programa anota en el perfil del usuario (<see cref="VendorCheck.ProfileVersionFiles"/>): la del
        /// primer archivo legible, en el orden de <paramref name="roaming"/>. null si no hay ninguno.
        /// </summary>
        internal static string ProfileVersionOf(VendorCheck check, List<string> roaming, Func<string, string> read, out string source)
        {
            source = "";
            if (check == null || check.ProfileVersionFiles == null || check.ProfileVersion == null || roaming == null || read == null) return null;
            foreach (string folder in roaming)
                foreach (string rel in check.ProfileVersionFiles)
                {
                    // Siempre adentro del perfil: ni rutas completas («C:\…», «\\servidor\…») ni «..».
                    if (string.IsNullOrEmpty(rel) || rel.Contains("..") || rel.IndexOf(':') >= 0 || rel[0] == '\\' || rel[0] == '/') continue;
                    string path = folder.TrimEnd('\\', '/') + "\\" + rel;
                    string text = null;
                    try { text = read(path); } catch { text = null; }
                    if (string.IsNullOrEmpty(text)) continue;
                    var m = check.ProfileVersion.Match(text.Length > 65536 ? text.Substring(0, 65536) : text);
                    if (!m.Success || VersionUtil.Parse(m.Groups["v"].Value) == null) continue;
                    source = path;
                    return VersionUtil.Normalize(m.Groups["v"].Value);
                }
            return null;
        }

        UpdateCandidate Evaluate(UpdateContext ctx, VendorCheck check, List<InstalledProgram> group, List<InstalledProgram> allMatches, FeedCache cache)
        {
            var p = group[0];
            var plan = new VendorPlan();
            plan.Check = check;
            plan.Programs.AddRange(group);
            foreach (var g in group) if (!plan.CoveredNames.Contains(g.Name.Trim())) plan.CoveredNames.Add(g.Name.Trim());
            plan.Processes = check.Processes ?? new string[0];
            plan.InstallDir = InstallDirOf(p);
            plan.Machine = !IsPerUser(p);
            var c = NewCandidate(p, check.Vendor, "fabricante:" + check.Id, plan);
            c.InfoUrl = Fmt.WebUrl(check.InfoUrl);

            string installed = p.Version ?? "";
            if (check.VersionFiles != null && check.VersionFiles.Length > 0)
            {
                // 2.0.8: la versión del producto está en un archivo (Tor Browser): si no se puede leer, no se compara.
                string fileSource;
                string real = ProductVersionFromFiles(check, CandidateFolders(p), ReadSmallFile, out fileSource);
                if (real == null) { Log.Info(p.Name + ": no se encontró su archivo de versión (" + string.Join(", ", check.VersionFiles) + ")"); return null; }
                if (real != installed) Log.Info(p.Name + ": versión del producto " + real + " (de " + fileSource + "; el registro o el ejecutable dicen " + installed + ")");
                installed = real;
                var direct = EvaluateFeed(ctx, check, plan, c, installed, allMatches, cache);
                if (direct != null) direct.InstalledVersion = installed;
                return direct;
            }
            if (check.ProfileVersionFiles != null && check.ProfileVersionFiles.Length > 0)
            {
                // 2.0.16: el programa se actualiza solo dentro del perfil del usuario (TLauncher) y anota ahí la versión que tiene:
                // esa es la que compara su propio actualizador. La de «Aplicaciones» es la del día que se instaló.
                string where;
                string own = ProfileVersionOf(check, RoamingFoldersForTests != null ? RoamingFoldersForTests(p) : RoamingFolders(p), ReadSmallFile, out where);
                if (own != null)
                {
                    plan.ProfileFile = where;
                    // 2.0.19: lo que se escribe va al perfil del usuario, no a Program Files: no hace falta ser administrador.
                    if (check.Install == VendorInstall.Starter4j) plan.Machine = false;
                    if (own != installed) Log.Info(p.Name + ": versión " + own + " según " + where + " (el registro de Windows dice " + (installed.Length > 0 ? installed : "nada") + ")");
                    string registry = installed;
                    var mine = EvaluateFeed(ctx, check, plan, c, own, allMatches, cache);
                    if (mine != null && mine.Status != UpdateStatus.Error)
                    {
                        mine.InstalledVersion = own;
                        mine.Detail = (mine.Detail ?? "").TrimEnd() + " La versión instalada es la que anota el propio " + check.Product + " en el perfil del usuario (" +
                                      Path.GetFileName(where.Replace('\\', Path.DirectorySeparatorChar)) + ")" +
                                      (VersionUtil.Parse(registry) != null && VersionUtil.Compare(registry, own) != 0 ? "; «Aplicaciones» de Windows sigue mostrando la " + registry + ", la del día que se instaló." : ".");
                    }
                    return mine;
                }
                Log.Info(p.Name + ": no se encontró " + string.Join(", ", check.ProfileVersionFiles) + " en ningún perfil; se compara con la versión del registro (" + installed + ")");
            }
            // La versión de los archivos, si el programa la publica en «version.json» (Mullvad Browser): si se
            // actualizó solo y su entrada del registro quedó atrás, manda la de los archivos.
            try
            {
                string filesSource;
                string files = WingetProvider.FilesVersion(p, installed, out filesSource);
                if (files.Length > 0 && ProgramRules.IsProductVersionSource(filesSource) && VersionUtil.IsNewer(files, installed)) installed = files;
            }
            catch (Exception ex) { Log.Warn("Versión de los archivos de " + p.Name + ": " + ex.Message); }
            string exe = ExePath(plan);
            if (exe != null)
            {
                string fv = ExeVersion(exe);
                if (VersionUtil.Parse(fv) != null && VersionUtil.Parse(installed) == null) installed = fv;
                plan.Arch = BrowserProvider.PeArch(exe);
            }
            if (plan.Arch.Length == 0) plan.Arch = ArchitectureOf(ctx, p);

            var r = EvaluateFeed(ctx, check, plan, c, installed, allMatches, cache);
            if (r != null && group.Count > 1 && check.OneRowForAll && r.Status != UpdateStatus.Error)
            {
                // Office: una fila por instalación, no una por producto e idioma.
                var others = new List<string>();
                for (int i = 1; i < group.Count; i++) others.Add("«" + group[i].Name + "»");
                r.Detail += " Una sola actualización para todo " + check.Product + ": también cubre " + Fmt.JoinY(others) + ".";
            }
            return r;
        }

        /// <summary>Carpetas donde buscar los archivos de versión: la de instalación y la del ejecutable del acceso directo.</summary>
        static List<string> CandidateFolders(InstalledProgram p)
        {
            var l = new List<string>();
            foreach (string d in new[] { InstallDirOf(p), (p.InstallLocation ?? "").Trim().Trim('"') })
            {
                string x = d;
                if (x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) x = Path.GetDirectoryName(x) ?? "";
                x = x.TrimEnd('\\', '/');
                if (x.Length > 0 && !l.Exists(delegate(string e) { return e.Equals(x, StringComparison.OrdinalIgnoreCase); })) l.Add(x);
            }
            return l;
        }

        /// <summary>Sólo para las pruebas: reemplaza la lectura de los archivos de versión.</summary>
        internal static Func<string, string> ReadFileForTests;

        static string ReadSmallFile(string path)
        {
            if (ReadFileForTests != null) return ReadFileForTests(path);
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > 256 * 1024) return null;
            return File.ReadAllText(path);
        }

        static readonly System.Text.RegularExpressions.Regex AnyVersion = new System.Text.RegularExpressions.Regex(@"(?<v>\d{1,3}(?:\.\d{1,5}){1,3})");

        /// <summary>
        /// La versión del producto según sus archivos (<see cref="VendorCheck.VersionFiles"/>): un .json con «"version"» o, en un
        /// texto (ChangeLog), la primera línea que nombra el producto. null si no hay ninguno legible.
        /// </summary>
        internal static string ProductVersionFromFiles(VendorCheck check, List<string> folders, Func<string, string> read, out string source)
        {
            source = "";
            if (check == null || check.VersionFiles == null || folders == null || read == null) return null;
            foreach (string folder in folders)
                foreach (string rel in check.VersionFiles)
                {
                    string path;
                    try { path = Path.GetFullPath(Path.Combine(folder, rel.Replace('\\', Path.DirectorySeparatorChar))); }
                    catch { continue; }
                    string text = null;
                    try { text = read(path); } catch { text = null; }
                    if (string.IsNullOrEmpty(text)) continue;
                    string v = null;
                    if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) v = ProgramRules.VersionFromJson(text);
                    else
                        foreach (string line in text.Split('\n'))
                        {
                            if (line.IndexOf(check.Product ?? "", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var m = AnyVersion.Match(line);
                            if (m.Success) v = m.Groups["v"].Value;
                            break;
                        }
                    if (v != null && VersionUtil.Parse(v) != null) { source = Path.GetFileName(path); return VersionUtil.Normalize(v); }
                }
            return null;
        }

        static readonly System.Text.RegularExpressions.Regex ArchInName = new System.Text.RegularExpressions.Regex(
            @"\b(x64|x86|amd64|arm64)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Arquitectura de una entrada: la del nombre («… x86 …») o, si no la dice, la del registro.</summary>
        public static string ArchTag(InstalledProgram p)
        {
            if (p == null) return "";
            var m = ArchInName.Match(p.Name ?? "");
            if (m.Success) return BrowserCatalog.NormalizeArch(m.Value);
            return BrowserCatalog.NormalizeArch(p.Architecture);
        }

        UpdateCandidate EvaluateFeed(UpdateContext ctx, VendorCheck check, VendorPlan plan, UpdateCandidate c, string installed,
                                     List<InstalledProgram> allMatches, FeedCache cache)
        {
            var p = plan.Program;
            string latest = null, error = null;
            NvidiaRelease nvidia = null;
            InstallerProbe probe = null;
            OfficeConfig office = null;
            OfficeChannel channel = null;
            if (check.Feed == VendorFeed.FinalVersion)
            {
                var st = VendorCatalog.EvaluateFinal(installed, check.FinalVersion);
                if (!st.HasValue) return null;           // versión instalada ilegible: queda la fila de winget
                c.InstalledVersion = installed;
                c.AvailableVersion = check.FinalVersion;
                if (st.Value == UpdateStatus.AlDia) { c.Status = UpdateStatus.AlDia; c.Detail = check.Note; return c; }
                foreach (var o in allMatches)
                    if (o != p && ArchTag(o) == ArchTag(p) && VersionUtil.Compare(o.Version, check.FinalVersion) >= 0)
                    {
                        // Sólo la de la MISMA arquitectura: los programas de 32 bits cargan el runtime x86
                        // aunque el x64 esté al día.
                        // Visual C++ 2008: 9.0.30729.4148 convive con 9.0.30729.6161, que es la que se carga.
                        c.Status = UpdateStatus.AlDia;
                        c.Detail = "Convive con «" + o.Name + "» (" + o.Version + "), la última versión, que es la que usan los programas: esta entrada queda por compatibilidad.";
                        return c;
                    }
                c.Status = UpdateStatus.Disponible;
                c.Recommended = false;
                // Visual C++ 2008–2013 se instalan con el instalador oficial fijo de esa arquitectura (la del nombre:
                // el paquete x64 de 2012/2013 se registra en la vista de 32 bits del registro).
                string tag = ArchTag(p);
                var fixedInstaller = check.Install == VendorInstall.SignedInstaller ? check.FinalInstallerFor(tag) : null;
                if (fixedInstaller == null)
                {
                    c.Detail = "La última versión que publicó Microsoft es la " + check.FinalVersion + ". " + check.HowToUpdate;
                    return c;
                }
                plan.Arch = tag;
                plan.Latest = check.FinalVersion;
                plan.InstallerUrl = fixedInstaller.Url;
                plan.InstallerSha256 = fixedInstaller.Sha256;
                plan.PinnedSha256 = true;
                if (!ctx.IsAdmin)
                {
                    c.Detail = "La última versión que publicó Microsoft es la " + check.FinalVersion + ". Para instalarla hace falta ejecutar Actualizador como administrador.";
                    return c;
                }
                c.Installable = true;
                c.Recommended = true;
                c.Detail = "La última versión que publicó Microsoft es la " + check.FinalVersion + " (" + check.Product + " " + tag + "). Se descarga de " +
                           new Uri(fixedInstaller.Url).Host + " y sólo se instala si es exactamente el archivo oficial (el SHA-256 que publica el catálogo de winget). " +
                           "La instalada se reemplaza; los programas que la usan siguen funcionando.";
                return c;
            }
            try
            {
                switch (check.Feed)
                {
                    case VendorFeed.AimpPage:
                    {
                        string page = Fetch(ctx, check, check.Url, "text/html", cache);
                        var downloads = VendorCatalog.ParseDownloads(page);
                        latest = VendorCatalog.ParseLatest(check, page);
                        if (latest != null) plan.Download = VendorCatalog.Pick(downloads, latest, plan.Arch);
                        break;
                    }
                    case VendorFeed.PageRegex:
                    {
                        string page = FetchWithMirrors(ctx, check, "text/html", cache);
                        string url;
                        latest = VendorCatalog.ParsePage(check, page, out url);
                        // 2.0.12: el instalador de la arquitectura instalada que enlaza la página oficial (7-Zip).
                        if (latest != null && check.ArchLinks.Count > 0) url = VendorCatalog.PickArchLink(check, page, latest, plan.Arch, p != null && WingetDirect.LooksMsi(p));
                        plan.InstallerUrl = url ?? check.LatestInstallerUrl;
                        if (latest != null && plan.InstallerUrl != null && !string.IsNullOrEmpty(check.HashRepo))
                            plan.InstallerSha256 = PublishedHash(ctx, check, plan.InstallerUrl, latest, cache) ?? "";
                        break;
                    }
                    case VendorFeed.SparkleAppcast:
                    {
                        string feed = plan.Arch == "x86" ? check.Url.Replace("appcast.x64.xml", "appcast.xml") : check.Url;
                        var item = VendorCatalog.ParseAppcast(Fetch(ctx, check, feed, "application/xml", cache));
                        if (item != null)
                        {
                            latest = item.Version;
                            Uri u;
                            if (plan.Arch != "arm64" && Uri.TryCreate(item.Url, UriKind.Absolute, out u) && check.Allows(u)) plan.InstallerUrl = u.AbsoluteUri;
                        }
                        break;
                    }
                    case VendorFeed.AdobeReleaseNotes:
                    {
                        var release = LatestForWindows(ctx, check, cache);
                        if (release == null) break;
                        latest = release.Version;
                        // «Adobe Acrobat (64-bit)» es el nombre de Acrobat Y de Reader de 64 bits: el código de
                        // producto los distingue, y así se baja primero el parche que corresponde.
                        bool reader = check.Id == "acrobat-reader" || plan.Programs.Exists(AdobeRules.IsUnifiedReader);
                        plan.MspUrls.AddRange(AdobeRules.MspUrls(reader, plan.Arch, latest, release.Patches));
                        break;
                    }
                    case VendorFeed.GitHubRelease:
                    {
                        var rel = GitHubRules.ParseRelease(Fetch(ctx, check, check.Url, "application/vnd.github+json", cache));
                        if (rel == null) { error = "el proyecto no publica una versión estable"; break; }
                        latest = rel.Version;
                        var asset = GitHubRules.PickAsset(rel, plan.Arch, false);
                        Uri u;
                        if (asset != null && Uri.TryCreate(asset.Url, UriKind.Absolute, out u) && check.Allows(u))
                        {
                            plan.InstallerUrl = u.AbsoluteUri;
                            plan.InstallerSha256 = asset.Sha256;
                        }
                        break;
                    }
                    case VendorFeed.NvidiaDriver:
                    {
                        // 2.0.13: la última versión que publica NVIDIA para la placa instalada (no hay una sola «última» para todas).
                        var gpus = NvidiaDriver.InstalledGpus(ctx);
                        bool dch = NvidiaDriver.InstalledIsDch();
                        string nvError;
                        nvidia = NvidiaDriver.Query(delegate(string u, string accept) { return Fetch(ctx, check, u, accept, cache); },
                                                    gpus, NvidiaDriver.IsLaptop(), NvidiaDriver.OsId(ctx.Os), dch, out nvError);
                        if (nvidia == null)
                        {
                            if (nvError != null) { error = nvError; break; }
                            // Ninguna placa de NVIDIA reconocible (se sacó la placa y quedó el controlador): queda la fila de winget con su pista.
                            Log.Info(p.Name + ": no hay una placa de video de NVIDIA que NVIDIA reconozca (" + (gpus.Count > 0 ? string.Join(", ", gpus.ToArray()) : "ninguna") + ")");
                            return null;
                        }
                        latest = nvidia.Version;
                        Uri du, iu;
                        if (Uri.TryCreate(nvidia.DownloadUrl, UriKind.Absolute, out du) && check.Allows(du) &&
                            du.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) plan.InstallerUrl = du.AbsoluteUri;
                        if (Uri.TryCreate(nvidia.DetailsUrl, UriKind.Absolute, out iu) && check.Allows(iu)) c.InfoUrl = iu.AbsoluteUri;
                        Log.Info(p.Name + ": placa «" + nvidia.Gpu + "», controlador " + (dch ? "DCH" : "estándar") + "; NVIDIA publica la " + latest);
                        break;
                    }
                    case VendorFeed.OfficialInstaller:
                    {
                        // 2.0.15: la versión del instalador que enlaza la página oficial (Fortinet), y ese mismo archivo para instalar.
                        probe = Probe(ctx, check, check.ArchUrlFor(plan.Arch), cache, out error);
                        if (probe == null) break;
                        latest = probe.Version;
                        Uri u;
                        if (Uri.TryCreate(probe.FinalUrl, UriKind.Absolute, out u) && check.Allows(u)) plan.InstallerUrl = u.AbsoluteUri;
                        Log.Info(p.Name + ": " + probe.FileName + " (" + new Uri(probe.FinalUrl).Host + ") es la " + latest + " según " + probe.VersionSource);
                        break;
                    }
                    case VendorFeed.OfficeReleases:
                    {
                        office = ReadOfficeConfig();
                        if (office.Found || office.InventoryFound) installed = OfficeC2R.InstalledVersion(office);
                        else if (OfficeC2R.IsLegacyClickToRun(installed))
                            return LegacyOffice(c, installed);
                        var channels = OfficeC2R.ParseReleases(Fetch(ctx, check, check.Url, "application/json", cache));
                        channel = OfficeC2R.ResolveChannel(channels, office);
                        if (!office.Found && !office.InventoryFound) error = "no se pudo leer la configuración de Office en el registro";
                        else if (channel == null) error = "no se reconoce el canal de actualización de Office (" + Fmt.Truncate(FirstNonEmpty(office.PolicyBranch, office.UpdateChannel, office.CdnBaseUrl, office.Products), 80) + ")";
                        else if (channel.Retired) return RetiredOffice(c, plan, installed, channel);
                        else latest = channel.Latest;
                        break;
                    }
                }
                if (latest == null && error == null) error = "la página del fabricante cambió y no se encontró la versión";
            }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception ex) { error = Http.Describe(ex); }

            c.InstalledVersion = installed.Length > 0 ? installed : "Desconocida";
            if (error != null)
            {
                c.Status = UpdateStatus.Error;
                c.Detail = "No se pudo consultar " + (check.Domain ?? "al fabricante") + ": " + error + ".";
                plan.Latest = error;
                Log.Warn(c.Name + ": " + error);
                return c;
            }
            plan.Latest = latest;
            c.AvailableVersion = latest;
            if (VersionUtil.Parse(installed) != null && !VersionUtil.IsNewer(latest, installed))
            {
                c.Status = UpdateStatus.AlDia;
                c.Detail = check.Feed == VendorFeed.OfficeReleases
                    ? "Coincide con la última versión del " + OfficeC2R.ChannelTitle(channel) + " publicada por Microsoft."
                    : nvidia != null ? NvidiaUpToDateText(nvidia, DateTime.Today)
                    : probe != null ? "Coincide con la última versión que publica " + check.Vendor + ": la del instalador oficial que enlaza su página de descargas (" + probe.FileName + ")."
                    : "Coincide con la última versión estable publicada por el fabricante.";
                return c;
            }
            if (VersionUtil.Parse(installed) == null && (check.Install == VendorInstall.None || check.Install == VendorInstall.Starter4j))
            {
                // 2.0.13: sin la versión instalada no se puede decir «desactualizado»: se muestra la publicada y cómo comparar.
                c.Status = UpdateStatus.NoVerificable;
                c.Detail = "El fabricante publica la " + latest + ", pero no se pudo leer la versión instalada para compararla. " + check.HowToUpdate;
                return c;
            }
            if (check.SideBySide)
            {
                // Cura: cada versión en su carpeta. Si la nueva ya está instalada al lado, esta entrada es la copia vieja.
                var newer = allMatches.Find(delegate(InstalledProgram o) { return o != p && VersionUtil.Compare(o.Version, latest) >= 0; });
                if (newer != null)
                {
                    c.Status = UpdateStatus.AlDia;
                    c.Detail = "La " + latest + " ya está instalada («" + newer.Name + "»); esta " + installed + " quedó de antes, porque " + check.Product +
                               " instala cada versión al lado de la anterior. Podés desinstalarla desde «Aplicaciones».";
                    return c;
                }
            }
            c.Status = UpdateStatus.Disponible;
            string lead = (check.Feed == VendorFeed.OfficeReleases ? "Microsoft publica la " + latest + " en el " + OfficeC2R.ChannelTitle(channel)
                         : nvidia != null ? "NVIDIA publica la " + latest + " para la " + nvidia.Gpu + NvidiaReleaseText(nvidia)
                         : probe != null ? FortinetLead(check, probe)
                         : "El fabricante publica la " + latest) + ". ";
            bool needsAdmin = plan.Machine && (plan.InstallDir.Length == 0 || !SafePaths.IsUserWritableLocation(plan.InstallDir));
            if (IsOtherUser(p))
            {
                c.Detail = lead + "Está instalado para otro usuario (" + p.Scope.Substring("Usuario ".Length) + "): actualizalo desde su sesión. " + check.HowToUpdate;
                return c;
            }
            switch (check.Install)
            {
                case VendorInstall.PageHash:
                    if (plan.Download == null)
                        c.Detail = lead + "No se encontró en su página el instalador de " + plan.Arch + " con su hash. " + check.HowToUpdate;
                    else if (needsAdmin && !ctx.IsAdmin)
                        c.Detail = lead + "Para instalarla en " + (plan.InstallDir.Length > 0 ? plan.InstallDir : "Program Files") + " hace falta ejecutar Actualizador como administrador. " + check.HowToUpdate;
                    else
                    {
                        c.Installable = true;
                        c.Recommended = check.Recommended;
                        c.Detail = "El fabricante publica la " + latest + " (el catálogo de winget todavía no la tiene). Se descarga de " + check.Domain +
                                   " y sólo se instala si su SHA-256 coincide con el publicado en la página oficial. Cerrá " + check.Product + " antes de actualizar.";
                    }
                    break;
                case VendorInstall.SignedInstaller:
                    if (plan.InstallerUrl == null)
                        c.Detail = lead + "No se encontró el instalador oficial para " + plan.Arch + ". " + check.HowToUpdate;
                    else if (check.UnsignedWithPublishedHash && plan.InstallerSha256.Length != 64)
                        c.Detail = lead + "Su página oficial enlaza «" + VendorProvider.FileNameFor(plan.InstallerUrl, "el instalador") + "», pero no se pudo leer el SHA-256 que " +
                                   "publica el autor para ese archivo (github.com/" + check.HashRepo + "), y sin eso no se instala un instalador sin firma digital. " +
                                   "Volvé a buscar en unos minutos. " + check.HowToUpdate;
                    else if (check.UnsignedWithPublishedHash && needsAdmin && !ctx.IsAdmin)
                        c.Detail = lead + "Para instalarla en " + (plan.InstallDir.Length > 0 ? plan.InstallDir : "Program Files") + " hace falta ejecutar Actualizador como administrador. " + check.HowToUpdate;
                    else if (check.UnsignedWithPublishedHash)
                    {
                        c.Installable = true;
                        c.Recommended = check.Recommended;
                        c.Detail = lead + "Se instala «" + VendorProvider.FileNameFor(plan.InstallerUrl, "el instalador") + "», el que enlaza su página oficial (" + check.Domain +
                                   "), descargado de " + new Uri(plan.InstallerUrl).Host + ". El autor no firma sus instaladores: sólo se instala si el SHA-256 coincide con el que " +
                                   "publica para ese archivo (github.com/" + check.HashRepo + ") y es el instalador de " + check.Product + ". Reemplaza al anterior en su misma carpeta. " +
                                   "Cerrá " + check.Product + " antes de actualizar.";
                    }
                    else if (needsAdmin && !ctx.IsAdmin)
                        c.Detail = lead + "Para instalarla en " + (plan.InstallDir.Length > 0 ? plan.InstallDir : "Program Files") + " hace falta ejecutar Actualizador como administrador. " + check.HowToUpdate;
                    else
                    {
                        c.Installable = true;
                        c.Recommended = check.Recommended;
                        c.Detail = lead + "Se descarga de " + new Uri(plan.InstallerUrl).Host + " y sólo se instala si tiene una firma digital válida " +
                                   (check.Signers != null && check.Signers.Length > 0 ? "de " + check.Signers[0] : "del mismo fabricante que el programa instalado") +
                                   (plan.InstallerSha256.Length == 64 ? " y el SHA-256 publicado" : "") + "." +
                                   (string.IsNullOrEmpty(check.Note) ? "" : " " + check.Note) +
                                   (check.ShowInstaller ? "" :
                                   (check.SilentArgs == null ? " Si el instalador no admite instalarse en silencio, se abre para que lo completes." : "") +
                                   " Cerrá " + check.Product + " antes de actualizar.");
                    }
                    break;
                case VendorInstall.AdobeMsp:
                    if (plan.MspUrls.Count == 0)
                        c.Detail = lead + "No hay un parche oficial para esta arquitectura (" + plan.Arch + "). " + check.HowToUpdate;
                    else if (!ctx.IsAdmin)
                        c.Detail = lead + "Para aplicar el parche oficial hace falta ejecutar Actualizador como administrador. " + check.HowToUpdate;
                    else
                    {
                        c.Installable = true;
                        c.Recommended = check.Recommended;
                        c.Detail = lead + "Se aplica el parche oficial (MSP) de ardownload2.adobe.com, sólo si está firmado por Adobe. Cerrá Acrobat antes de actualizar.";
                    }
                    break;
                case VendorInstall.OfficeClickToRun:
                    c.Detail = lead + OfficeInstallDetail(ctx, office, latest, c);
                    if (c.Installable) c.Recommended = check.Recommended;
                    break;
                case VendorInstall.Starter4j:
                    if (string.IsNullOrEmpty(plan.ProfileFile))
                        c.Detail = lead + "No se encontró " + StarterFileName(check) + " en ningún perfil (TLauncher nunca se abrió en este equipo): " + check.HowToUpdate;
                    else
                    {
                        c.Installable = true;
                        c.Recommended = check.Recommended;
                        c.Detail = lead + "Se actualiza desde acá igual que lo hace el propio " + check.Product + " al abrirse, pero sin su ventana de actualización " +
                                   "(ni su «oferta opcional» de programas de terceros): se bajan los archivos de la " + latest + " de " + check.Domain +
                                   ", se guardan sólo si su SHA-1 y su tamaño coinciden con los que publica la configuración oficial, y se anota la versión en " +
                                   StarterFileName(check) + ". Cerrá " + check.Product + " antes de actualizar (Actualizador ofrece cerrarlo).";
                    }
                    break;
                default:
                    c.Detail = lead + check.HowToUpdate;
                    break;
            }
            return c;
        }

        /// <summary>«starter.json»: el archivo del perfil donde el programa anota su versión.</summary>
        internal static string StarterFileName(VendorCheck check)
        {
            string rel = check != null && check.ProfileVersionFiles != null && check.ProfileVersionFiles.Length > 0 ? check.ProfileVersionFiles[0] : "starter.json";
            int i = rel.LastIndexOfAny(new[] { '\\', '/' });
            return "«" + (i >= 0 ? rel.Substring(i + 1) : rel) + "»";
        }

        /// <summary>« (GeForce Security Update Driver, del 09/07/2024)».</summary>
        internal static string NvidiaReleaseText(NvidiaRelease r)
        {
            var parts = new List<string>();
            if (r != null && !string.IsNullOrEmpty(r.Name)) parts.Add(Fmt.Truncate(r.Name, 80));
            if (r != null && r.Released.HasValue) parts.Add("del " + Fmt.Date(r.Released.Value));
            return parts.Count > 0 ? " (" + string.Join(", ", parts.ToArray()) + ")" : "";
        }

        /// <summary>Al día con NVIDIA; si la última es de hace más de un año, que NVIDIA no publicó nada más nuevo para esa placa.</summary>
        internal static string NvidiaUpToDateText(NvidiaRelease r, DateTime today)
        {
            string text = "Coincide con la última versión que publica NVIDIA para la " + r.Gpu + NvidiaReleaseText(r) + ".";
            if (r.Released.HasValue && (today - r.Released.Value).TotalDays > 365)
                text += " NVIDIA no publicó versiones más nuevas para esta placa desde entonces.";
            return text;
        }

        static string FirstNonEmpty(params string[] values)
        {
            foreach (string v in values) if (!string.IsNullOrEmpty(v)) return v;
            return "";
        }

        /// <summary>
        /// Qué se hace con Office «Hacer clic y ejecutar» que tiene una versión nueva, y si se puede instalar desde
        /// acá (deja <see cref="UpdateCandidate.Installable"/> en true). Público para las pruebas.
        /// </summary>
        public static string OfficeInstallDetail(UpdateContext ctx, OfficeConfig office, string latest, UpdateCandidate c)
        {
            string managed = OfficeC2R.ManagedSource(office);
            if (managed.Length > 0)
                return "En este equipo Office se actualiza desde «" + Fmt.Truncate(managed, 80) + "», que administra la empresa o un servidor de la red: pedíselo a quien lo administra.";
            if (office != null && office.PolicyTargetVersion.Length > 0 && VersionUtil.Parse(office.PolicyTargetVersion) != null &&
                VersionUtil.IsNewer(latest, office.PolicyTargetVersion))
                return "Una directiva de la empresa fija Office en la " + office.PolicyTargetVersion + " (updatetargetversion): pedíselo a quien administra el equipo.";
            c.Installable = true;
            string text = "Se actualiza con el actualizador propio de Office (OfficeC2RClient.exe, firmado por Microsoft), pidiéndole exactamente la " + latest +
                          "; baja sólo lo que cambió. Hay que cerrar los programas de Office (también Skype Empresarial y «Enviar a OneNote», que quedan en segundo plano): Actualizador pregunta si cerrarlos.";
            if (office != null && office.UpdatesDisabled)
                text += ctx != null && ctx.IsAdmin
                    ? " Las actualizaciones de Office están desactivadas en este equipo (lo deja así el instalador): se activan mientras se actualiza y después se vuelven a desactivar."
                    : " Las actualizaciones de Office están desactivadas en este equipo: si no se actualiza, ejecutá Actualizador como administrador para que las active mientras actualiza.";
            else if (office != null && office.PolicyUpdatesDisabled)
                text += " Una directiva desactiva las actualizaciones automáticas de Office; la actualización manual igual se pide.";
            return text;
        }

        /// <summary>Office 2013 o 2010 «Hacer clic y ejecutar»: sin soporte, sin canal que consultar.</summary>
        static UpdateCandidate LegacyOffice(UpdateCandidate c, string installed)
        {
            bool is2013 = installed.Trim().StartsWith("15.", StringComparison.Ordinal);
            c.InstalledVersion = installed;
            c.AvailableVersion = "";
            c.Status = UpdateStatus.NoVerificable;
            c.Installable = false;
            c.Detail = (is2013 ? "Office 2013 «Hacer clic y ejecutar» dejó de recibir actualizaciones el 11/04/2023"
                               : "Office 2010 «Hacer clic y ejecutar» dejó de recibir actualizaciones el 13/10/2020") +
                       ": Microsoft ya no publica versiones nuevas. Conviene pasar a una versión con soporte (Microsoft 365 u Office LTSC 2024).";
            return c;
        }

        /// <summary>
        /// Canal que Microsoft ya no publica (Office LTSC 2019; Office LTSC 2021 desde que termina su soporte):
        /// no hay versión nueva contra la cual comparar: queda «No verificable» con el motivo, en lugar de
        /// «no se reconoce el canal de actualización».
        /// </summary>
        static UpdateCandidate RetiredOffice(UpdateCandidate c, VendorPlan plan, string installed, OfficeChannel channel)
        {
            DateTime? end = OfficeC2R.EndOfSupportOf(channel);
            c.InstalledVersion = installed.Length > 0 ? installed : "Desconocida";
            c.AvailableVersion = "";
            c.Status = UpdateStatus.NoVerificable;
            c.Recommended = false;
            plan.Latest = "";
            string ended = end.HasValue && end.Value.Date < DateTime.Today
                ? OfficeC2R.ChannelTitle(channel) + " no tiene soporte desde el " + Fmt.Date(end.Value)
                : "Microsoft ya no publica la versión del " + OfficeC2R.ChannelTitle(channel);
            c.Detail = ended + ": no hay una versión publicada contra la cual comparar. Si querés la última compilación que haya dejado Microsoft, " +
                       "pedila desde Word o Excel: Archivo › Cuenta › Opciones de actualización › Actualizar ahora. Conviene pasar a una versión con soporte (Microsoft 365 u Office LTSC 2024).";
            c.Installable = false;
            return c;
        }

        /// <summary>Compatibilidad interna: la lectura real vive en OfficeC2R y cubre x86/x64 + inventario oficial.</summary>
        internal static OfficeConfig ReadOfficeConfig()
        {
            return OfficeC2R.ReadConfig();
        }

        // ================================================================= GitHub

        void ScanGitHub(UpdateContext ctx, HashSet<InstalledProgram> covered, List<UpdateCandidate> output, List<string> notes)
        {
            var releases = new Dictionary<string, GitHubRelease>(StringComparer.OrdinalIgnoreCase);
            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int queries = 0, skipped = 0;
            bool limited = false;
            var errors = new List<string>();
            foreach (var p in ctx.Programs)
            {
                if (p == null || p.Unregistered || covered.Contains(p) || string.IsNullOrEmpty(p.Name)) continue;
                if (!(UpdateManager.PolicyForTests ?? Actualizador.Core.Security.Policy.Current).IsSourceAllowed("github")) break;   // política de la empresa (2.0)
                string repo = GitHubRules.RepoOf(p);
                if (repo == null || !seen.Add(p.Name.Trim() + "|" + p.Scope)) continue;
                if (VersionUtil.Parse(p.Version) == null || failed.Contains(repo)) continue;
                GitHubRelease rel;
                if (!releases.TryGetValue(repo, out rel))
                {
                    if (limited || queries >= MaxGitHubQueries) { skipped++; continue; }
                    ctx.ThrowIfCancelled();
                    queries++;
                    ctx.Report("Consultando la última versión de " + p.Name + " en github.com/" + repo + "…");
                    try { rel = GitHubRules.ParseRelease(Http.GetString(GitHubRules.LatestReleaseUrl(repo), 20000, GitHubRules.IsApi, "application/vnd.github+json")); }
                    catch (OperationCanceledByUserException) { throw; }
                    catch (HttpFailureException hf)
                    {
                        failed.Add(repo);
                        if (hf.StatusCode == 403 || hf.StatusCode == 429) { limited = true; skipped++; continue; }
                        if (hf.StatusCode != 404) errors.Add(p.Name + " (" + hf.Message + ")");
                        continue;                        // 404: el proyecto no publica versiones en GitHub
                    }
                    catch (Exception ex) { failed.Add(repo); errors.Add(p.Name + " (" + Http.Describe(ex) + ")"); continue; }
                    releases[repo] = rel;
                }
                if (rel == null) continue;

                var plan = new VendorPlan();
                plan.Repo = repo;
                plan.Programs.Add(p);
                plan.CoveredNames.Add(p.Name.Trim());
                plan.InstallDir = InstallDirOf(p);
                plan.Machine = !IsPerUser(p);
                plan.Latest = rel.Version;
                plan.Arch = ArchitectureOf(ctx, p);
                var c = NewCandidate(p, "GitHub (" + repo + ")", "github:" + repo, plan);
                c.InfoUrl = rel.HtmlUrl.Length > 0 ? rel.HtmlUrl : "https://github.com/" + repo + "/releases/latest";
                c.InstalledVersion = p.Version;
                c.AvailableVersion = rel.Version;
                if (!VersionUtil.IsNewer(rel.Version, p.Version))
                {
                    c.Status = UpdateStatus.AlDia;
                    c.Detail = "Coincide con la última versión publicada en github.com/" + repo + ".";
                    output.Add(c);
                    continue;
                }
                string filesSource;
                string files = "";
                try { files = WingetProvider.FilesVersion(p, p.Version, out filesSource); } catch { files = ""; filesSource = ""; }
                if (files.Length > 0 && ProgramRules.FilesAlreadyAt(files, p.Version, rel.Version, ProgramRules.IsProductVersionSource(filesSource)))
                {
                    // Se actualizó solo y su entrada en «Aplicaciones» quedó atrás (ver WingetProvider.CheckFilesVersion).
                    c.Status = UpdateStatus.AlDia;
                    c.InstalledVersion = files;
                    c.Detail = "Ya está en la " + files + " (lo indica " + filesSource + "), la última publicada en github.com/" + repo +
                               "; su entrada en «Aplicaciones» de Windows quedó en la " + p.Version + ".";
                    output.Add(c);
                    continue;
                }
                c.Status = UpdateStatus.Disponible;
                string lead = "El proyecto publica la " + rel.Version + " en github.com/" + repo + ". ";
                var asset = GitHubRules.PickAsset(rel, plan.Arch, WingetDirect.LooksMsi(p));
                bool needsAdmin = plan.Machine && (plan.InstallDir.Length == 0 || !SafePaths.IsUserWritableLocation(plan.InstallDir));
                if (IsOtherUser(p))
                    c.Detail = lead + "Está instalado para otro usuario: actualizalo desde su sesión.";
                else if (asset == null)
                    c.Detail = lead + "Entre los archivos de esa versión no hay un instalador claro para " + plan.Arch + ": descargalo de la página del proyecto (clic derecho › «Abrir sitio del fabricante»).";
                else if (needsAdmin && !ctx.IsAdmin)
                    c.Detail = lead + "Para instalarla hace falta ejecutar Actualizador como administrador, o descargala de la página del proyecto.";
                else
                {
                    plan.InstallerUrl = asset.Url;
                    plan.InstallerSha256 = asset.Sha256;
                    c.Installable = true;
                    // entra en «Actualizar todo»: el instalador se verifica igual (mismo firmante que lo instalado y el SHA-256 de GitHub).
                    c.Recommended = true;
                    c.Detail = lead + "Se descarga «" + asset.Name + "» y sólo se instala si es un instalador reconocido (Inno Setup, NSIS o MSI) con una firma digital válida " +
                               "del mismo firmante que el programa instalado" + (asset.Sha256.Length > 0 ? " y el SHA-256 que publica GitHub" : "") + ".";
                    if (InstallsAsUser(ctx.IsAdmin, p, plan.InstallDir, plan.Machine))
                    {
                        plan.RunAsUser = true;
                        c.Detail += AsUserNote;
                    }
                }
                output.Add(c);
            }
            if (limited) notes.Add("GitHub limitó las consultas (admite 60 por hora sin cuenta): algunos programas no se compararon con su proyecto. Reintentá en una hora.");
            else if (skipped > 0) notes.Add(Fmt.Plural(skipped, "programa con proyecto en GitHub no se consultó", "programas con proyecto en GitHub no se consultaron") + " para no superar el límite de consultas (" + MaxGitHubQueries + " por búsqueda).");
            if (errors.Count > 0) notes.Add("No se pudo consultar GitHub para " + Fmt.JoinY(errors) + ".");
        }

        // ================================================================= Utilidades

        /// <summary>
        /// Arquitectura REAL del programa instalado: la del ejecutable principal o la de su carpeta
        /// (<see cref="ProgramRules.DetectArchitecture"/>); recién si no se puede saber, la vista del
        /// registro y por último la del equipo. Antes se usaba primero la vista del registro, y muchos
        /// instaladores de 32 bits registran en WOW6432Node programas de 64 bits: en GitHub se elegía el
        /// instalador de 32 bits y quedaban dos copias del programa.
        /// </summary>
        internal static string ArchitectureOf(UpdateContext ctx, InstalledProgram p)
        {
            string native = ctx != null && ctx.Os != null ? ctx.Os.NativeArchitecture : "";
            string a = "";
            try { a = BrowserCatalog.NormalizeArch(ProgramRules.DetectArchitecture(p, native, BrowserProvider.PeArch)); } catch (Exception qx) { Log.Quiet("Arquitectura del programa", qx); }
            if (a.Length == 0 && p != null) a = BrowserCatalog.NormalizeArch(p.Architecture);
            if (a.Length == 0) a = BrowserCatalog.NormalizeArch(native);
            return a.Length > 0 ? a : "x64";
        }

        internal static string DirOfUninstaller(string uninstall)
        {
            if (string.IsNullOrEmpty(uninstall)) return "";
            string u = uninstall.Trim();
            if (u.StartsWith("\"")) { int q = u.IndexOf('"', 1); u = q > 0 ? u.Substring(1, q - 1) : u.Trim('"'); }
            else { int e = u.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); if (e > 0) u = u.Substring(0, e + 4); }
            try { return Path.GetDirectoryName(u) ?? ""; } catch { return ""; }
        }

        static string ExePath(VendorPlan plan)
        {
            if (plan.Check == null || string.IsNullOrEmpty(plan.Check.ExeName) || string.IsNullOrEmpty(plan.InstallDir)) return null;
            try
            {
                string p = Path.Combine(plan.InstallDir, plan.Check.ExeName);
                return File.Exists(p) ? p : null;
            }
            catch { return null; }
        }

        static string ExeVersion(string path)
        {
            try
            {
                var fi = FileVersionInfo.GetVersionInfo(path);
                string v = fi.ProductVersion;
                if (VersionUtil.Parse(v) == null) v = fi.FileVersion;
                return v ?? "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// Coincidencia por nombre de una fila de otro proveedor con esta fila del fabricante: por los
        /// nombres que cubre, por el nombre recortado con «…» de winget, o por la expresión del catálogo.
        /// </summary>
        public static bool Covers(UpdateCandidate vendorRow, UpdateCandidate other)
        {
            if (vendorRow == null || other == null || other.Kind != UpdateKind.Programa || other.ProviderName == ProviderId) return false;
            string n = BaseName(other.Name);
            if (n.Length == 0) return false;
            var plan = vendorRow.ProviderTag as VendorPlan;
            var names = new List<string>();
            if (plan != null) names.AddRange(plan.CoveredNames);
            if (names.Count == 0) names.Add(BaseName(vendorRow.Name));
            var w = other.ProviderTag as WingetRow;
            bool truncated = w != null && ((w.Name ?? "").EndsWith("…") || (w.Name ?? "").EndsWith("..."));
            foreach (string name in names)
            {
                if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
                if (truncated && n.Length >= 8 && name.StartsWith(n, StringComparison.OrdinalIgnoreCase)) return true;
            }
            // Con plan, sólo los nombres que cubre: la expresión del catálogo abarca también entradas
            // hermanas (Visual C++ 2008 .4148 y .6161), y si una no tuvo fila propia desaparecería.
            if (plan != null) return false;
            var check = VendorCatalog.Match(vendorRow.Name);
            return check != null && check.Name != null && check.Extra == null && check.Name.IsMatch(n);
        }

        internal static string BaseName(string name)
        {
            string n = (name ?? "").Trim().TrimEnd('…');
            if (n.EndsWith("...")) n = n.Substring(0, n.Length - 3);
            if (n.EndsWith(" (usuario)")) n = n.Substring(0, n.Length - " (usuario)".Length);
            return n.Trim();
        }

        // La instalación está en VendorProvider.Install.cs.
    }
}
