// Instalación automática de winget (App Installer) cuando falta: Windows LTSC, Server o una Store quitada.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    /// <summary>Qué bajar de la última versión de winget publicada por Microsoft en GitHub.</summary>
    public sealed class WingetSetupPlan
    {
        public string Version = "";
        public GitHubAsset Bundle, Dependencies, License;
    }

    /// <summary>
    /// Sin winget no hay con qué comparar ni instalar los programas: quedaban todos «No verificable» (grabación del usuario:
    /// Windows sin Microsoft Store, «winget (App Installer) no está instalado»). Si falta, se instala solo, como lo documenta
    /// Microsoft para los equipos sin Store: el paquete oficial de github.com/microsoft/winget-cli (la última versión estable),
    /// sus dependencias (VCLibs, UI.Xaml, Windows App Runtime) del mismo lanzamiento y su licencia.
    /// Cada archivo se acepta sólo si su SHA-256 coincide con el que publica GitHub para ese lanzamiento, y Windows además
    /// exige la firma de Microsoft del paquete al instalarlo (Add-AppxPackage rechaza un paquete sin firma válida).
    /// </summary>
    public static class WingetSetup
    {
        public const string Repo = "microsoft/winget-cli";
        public const string BundleName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle";
        public const string DependenciesName = "DesktopAppInstaller_Dependencies.zip";

        /// <summary>Sólo para las pruebas: reemplaza la consulta a GitHub (url → JSON).</summary>
        internal static Func<string, string> FetchForTests;

        static readonly object gate = new object();
        static bool attempted;
        static string lastResult;

        /// <summary>Se intenta una vez por ejecución: si falló, no se vuelven a bajar 300 MB en cada búsqueda.</summary>
        internal static void ResetForTests() { lock (gate) { attempted = false; lastResult = null; } }

        /// <summary>Por qué no se puede instalar winget automáticamente en este equipo, o null si se puede.</summary>
        public static string CannotInstall(UpdateContext ctx)
        {
            if (ctx == null || ctx.Os == null || !ctx.Os.IsWindows10OrLater || ctx.Os.Build < 17763)
                return "winget necesita Windows 10 1809 o posterior (o Windows Server 2019 o posterior).";
            if (ctx.Options != null && !ctx.Options.InstallWinget)
                return "La instalación automática de winget está desactivada (Opciones o --sin-instalar-winget).";
            if (!ctx.IsAdmin)
                return "Para que Actualizador instale winget solo, ejecutalo como administrador (botón «Ejecutar como administrador»).";
            if (ArchFolder(ctx.Os.NativeArchitecture) == null)
                return "No hay un paquete de winget para la arquitectura " + ctx.Os.NativeArchitecture + ".";
            return null;
        }

        /// <summary>Arquitectura de Windows → la que usan los paquetes de dependencias, o null.</summary>
        public static string ArchFolder(string nativeArch)
        {
            switch ((nativeArch ?? "").Trim().ToLowerInvariant())
            {
                case "x64": case "amd64": case "": return "x64";
                case "x86": return "x86";
                case "arm64": return "arm64";
                default: return null;
            }
        }

        /// <summary>Los tres archivos del lanzamiento, o null (con el motivo) si falta alguno o su SHA-256.</summary>
        public static WingetSetupPlan PlanFrom(GitHubRelease release, out string why)
        {
            why = null;
            if (release == null) { why = "GitHub no informa una versión estable de winget"; return null; }
            var plan = new WingetSetupPlan();
            plan.Version = release.Version;
            foreach (var a in release.Assets)
            {
                if (a.Name.Equals(BundleName, StringComparison.OrdinalIgnoreCase)) plan.Bundle = a;
                else if (a.Name.Equals(DependenciesName, StringComparison.OrdinalIgnoreCase)) plan.Dependencies = a;
                else if (a.Name.EndsWith("_License1.xml", StringComparison.OrdinalIgnoreCase)) plan.License = a;
            }
            if (plan.Bundle == null) { why = "el lanzamiento " + release.Tag + " no trae " + BundleName; return null; }
            if (plan.Dependencies == null) { why = "el lanzamiento " + release.Tag + " no trae " + DependenciesName; return null; }
            if (plan.Bundle.Sha256.Length != 64 || plan.Dependencies.Sha256.Length != 64)
            {
                why = "GitHub no publica el SHA-256 de los archivos de " + release.Tag + ": no se instala nada que no se pueda verificar";
                return null;
            }
            return plan;
        }

        /// <summary>
        /// Las dependencias de esta arquitectura dentro del .zip («x64/Microsoft.VCLibs…_x64.appx»): por la carpeta o por el
        /// sufijo del nombre. VCLibs primero (el resto de los marcos la usa). Sólo nombres de archivo, nunca rutas.
        /// </summary>
        public static List<string> DependencyEntries(IEnumerable<string> entries, string arch)
        {
            var list = new List<string>();
            if (entries == null || string.IsNullOrEmpty(arch)) return list;
            string a = arch.ToLowerInvariant();
            foreach (string e in entries)
            {
                if (string.IsNullOrEmpty(e)) continue;
                string n = e.Replace('\\', '/');
                string file = n.Substring(n.LastIndexOf('/') + 1).ToLowerInvariant();
                if (!(file.EndsWith(".appx") || file.EndsWith(".msix"))) continue;
                string stem = file.Substring(0, file.Length - 5);
                bool byName = stem.EndsWith("_" + a);
                bool byFolder = ("/" + n.ToLowerInvariant()).Contains("/" + a + "/") && !Regex.IsMatch(stem, @"_(x64|x86|arm64|arm)$");
                if ((byName || byFolder) && !list.Contains(e)) list.Add(e);
            }
            list.Sort(delegate(string x, string y)
            {
                int c = Rank(x).CompareTo(Rank(y));
                return c != 0 ? c : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        static int Rank(string entry) { return entry.IndexOf("VCLibs", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1; }

        /// <summary>Texto entre comillas simples de PowerShell.</summary>
        public static string PsQuote(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }

        /// <summary>
        /// El guion de PowerShell que instala: dependencias, paquete para todo el equipo (con su licencia) y para el usuario
        /// actual. Una versión igual o más nueva ya instalada (0x80073D06) no es un error. Cada paso informa una línea
        /// «DEP-OK/DEP-ERR», «PROV-OK/PROV-ERR», «USER-OK/USER-ERR» para saber qué pasó.
        /// </summary>
        public static string InstallScript(string bundle, string license, List<string> deps)
        {
            var sb = new StringBuilder();
            sb.Append("$newer=-2147009274;");
            foreach (string d in deps ?? new List<string>())
                sb.Append("try{Add-AppxPackage -Path ").Append(PsQuote(d)).Append(" -ErrorAction Stop;'DEP-OK'}catch{if($_.Exception.HResult -eq $newer){'DEP-OK'}else{'DEP-ERR: '+$_.Exception.Message}};");
            sb.Append("try{");
            if (deps != null && deps.Count > 0)
            {
                var quoted = new List<string>();
                foreach (string d in deps) quoted.Add(PsQuote(d));
                sb.Append("$d=@(").Append(string.Join(",", quoted.ToArray())).Append(");");
            }
            sb.Append("Add-AppxProvisionedPackage -Online -PackagePath ").Append(PsQuote(bundle));
            if (deps != null && deps.Count > 0) sb.Append(" -DependencyPackagePath $d");
            sb.Append(string.IsNullOrEmpty(license) ? " -SkipLicense" : " -LicensePath " + PsQuote(license));
            sb.Append(" -ErrorAction Stop|Out-Null;'PROV-OK'}catch{'PROV-ERR: '+$_.Exception.Message};");
            sb.Append("try{Add-AppxPackage -Path ").Append(PsQuote(bundle)).Append(" -ErrorAction Stop;'USER-OK'}catch{if($_.Exception.HResult -eq $newer){'USER-OK'}else{'USER-ERR: '+$_.Exception.Message}}");
            return sb.ToString();
        }

        /// <summary>Resultado del guion: true si quedó instalado para el usuario actual o para todo el equipo.</summary>
        public static bool ScriptSucceeded(string output, out string error)
        {
            error = null;
            string o = output ?? "";
            bool user = o.Contains("USER-OK"), prov = o.Contains("PROV-OK");
            if (user || prov) return true;
            foreach (string line in o.Split('\n'))
            {
                string l = line.Trim();
                if (l.StartsWith("USER-ERR: ") || l.StartsWith("PROV-ERR: ") || l.StartsWith("DEP-ERR: "))
                {
                    error = l.Substring(l.IndexOf(':') + 1).Trim();
                    if (l.StartsWith("USER-ERR: ")) break;
                }
            }
            if (error == null) error = "PowerShell no informó el resultado";
            return false;
        }

        /// <summary>
        /// Si winget falta y se puede, lo instala. true si al terminar hay un winget que responde. Lo que pasó queda en
        /// <paramref name="notes"/> (la lista de notas de la búsqueda).
        /// </summary>
        public static bool EnsureInstalled(UpdateContext ctx, List<string> notes)
        {
            if (SoftwareCollectors.WingetCandidates().Count > 0) return true;
            string cannot = CannotInstall(ctx);
            if (cannot != null) { Log.Info("winget falta y no se instala: " + cannot); return false; }
            lock (gate)
            {
                if (attempted)
                {
                    if (lastResult != null) notes.Add(lastResult);
                    return false;
                }
                attempted = true;
            }
            string result;
            bool ok = false;
            try { ok = Install(ctx, out result); }
            catch (OperationCanceledByUserException) { lock (gate) attempted = false; throw; }
            catch (Exception ex)
            {
                Log.Error("Instalación de winget", ex);
                result = "winget no está instalado y no se pudo instalar automáticamente: " + Http.Describe(ex) + ". Instalalo desde https://aka.ms/getwinget";
            }
            lock (gate) lastResult = ok ? null : result;
            notes.Add(result);
            return ok;
        }

        static bool Install(UpdateContext ctx, out string result)
        {
            Http.EnableModernTls();
            ctx.Report("winget no está instalado: se instala App Installer (winget) desde github.com/microsoft/winget-cli…");
            string url = GitHubRules.LatestReleaseUrl(Repo);
            string json = FetchForTests != null ? FetchForTests(url) : Http.GetString(url, 30000, GitHubRules.IsApi, "application/vnd.github+json");
            string why;
            var plan = PlanFrom(GitHubRules.ParseRelease(json), out why);
            if (plan == null)
            {
                result = "winget no está instalado y no se instaló automáticamente: " + why + ". Instalalo desde https://aka.ms/getwinget";
                return false;
            }
            string arch = ArchFolder(ctx.Os.NativeArchitecture);
            string dir = BrowserProvider.NewDownloadDir();
            try
            {
                string depsZip = Path.Combine(dir, DependenciesName);
                string bundle = Path.Combine(dir, BundleName);
                DownloadVerified(ctx, plan.Dependencies, depsZip, "dependencias de winget");
                DownloadVerified(ctx, plan.Bundle, bundle, "winget " + plan.Version);
                string license = null;
                if (plan.License != null)
                {
                    try
                    {
                        string lic = Path.Combine(dir, "License1.xml");
                        DownloadVerified(ctx, plan.License, lic, "licencia de winget");
                        license = lic;
                    }
                    catch (OperationCanceledByUserException) { throw; }
                    catch (Exception ex) { Log.Warn("Licencia de winget: " + Http.Describe(ex) + " (se instala sin ella)"); }
                }
                ctx.ThrowIfCancelled();

                // Dependencias de esta arquitectura, con nombres planos dentro de la carpeta privada.
                var deps = new List<string>();
                string depsDir = Path.Combine(dir, "dependencias");
                Directory.CreateDirectory(depsDir);
                using (var zip = ZipFile.OpenRead(depsZip))
                {
                    var names = new List<string>();
                    foreach (var e in zip.Entries) names.Add(e.FullName);
                    foreach (string name in DependencyEntries(names, arch))
                    {
                        var entry = zip.GetEntry(name);
                        if (entry == null) continue;
                        string target = Path.Combine(depsDir, Path.GetFileName(name.Replace('/', '\\')));
                        entry.ExtractToFile(target, true);
                        deps.Add(target);
                    }
                }
                if (deps.Count == 0) Log.Warn("winget: el .zip de dependencias no trae paquetes de " + arch + "; se instala igual (pueden estar ya instaladas)");

                ctx.Report("Instalando winget " + plan.Version + " (App Installer)…");
                var r = ProcessRunner.PowerShell(InstallScript(bundle, license, deps), 15 * 60 * 1000);
                Log.Info("Instalación de winget: " + r.Describe() + " — " + Fmt.Truncate((r.Out ?? "").Replace("\r", " ").Replace("\n", " | "), 1500));
                string error;
                if (!r.Started || r.TimedOut || !ScriptSucceeded(r.Out, out error))
                {
                    error = !r.Started || r.TimedOut ? r.Describe() : (string.IsNullOrEmpty(r.Out) ? Fmt.Truncate(r.Err, 300) : ScriptError(r.Out));
                    result = "winget no está instalado y no se pudo instalar automáticamente (" + Fmt.Truncate(error, 300) + "). Instalalo desde https://aka.ms/getwinget";
                    return false;
                }

                // Windows registra el alias de ejecución unos segundos después.
                for (int i = 0; i < 20 && !ctx.CancelRequested; i++)
                {
                    if (SoftwareCollectors.WingetCandidates().Count > 0) break;
                    System.Threading.Thread.Sleep(3000);
                }
                if (SoftwareCollectors.WingetCandidates().Count == 0)
                {
                    result = "Se instaló winget " + plan.Version + ", pero Windows todavía no lo muestra: cerrá y volvé a abrir Actualizador (o reiniciá la sesión) y buscá de nuevo.";
                    return false;
                }
                result = "winget no estaba instalado: Actualizador instaló App Installer (winget " + plan.Version + ") desde github.com/microsoft/winget-cli, " +
                         "verificado por el SHA-256 que publica GitHub y la firma de Microsoft. Con eso se comparan y actualizan los programas.";
                Log.Info(result);
                return true;
            }
            finally { BrowserProvider.TryDeleteDir(dir); }
        }

        static string ScriptError(string output)
        {
            string e;
            ScriptSucceeded(output, out e);
            return e;
        }

        static void DownloadVerified(UpdateContext ctx, GitHubAsset asset, string path, string what)
        {
            ctx.ThrowIfCancelled();
            int lastPct = -10;
            string size = asset.Size > 0 ? " (" + Fmt.Bytes(asset.Size) + ")" : "";
            ctx.Report("Descargando " + what + size + "…");
            Http.Download(asset.Url, path, 60 * 60 * 1000, ctx, GitHubRules.IsDownload, delegate(int pct, long bytes)
            {
                if (pct >= 0 && pct >= lastPct + 10) { lastPct = pct; ctx.Report("Descargando " + what + size + ": " + pct + " %…"); }
            });
            if (asset.Sha256.Length == 64)
            {
                string got;
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) got = InstallerSupport.Sha256(f);
                if (!got.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("el SHA-256 de «" + asset.Name + "» no coincide con el que publica GitHub");
            }
        }
    }
}
