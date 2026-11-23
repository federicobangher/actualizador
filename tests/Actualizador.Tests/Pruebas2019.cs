// Pruebas de la 2.0.19. Captura del usuario (17/11/2026, con la 2.0.18): TLauncher 2.9381 → 2.9382 «Pendiente: Se abrió su página
// (tlauncher.org): descargá e instalá la 2.9382…». El pedido: que todo se actualice sin abrir la página.
// TLauncher usa el arranque starter4j (github.com/gdevby/starter4j): «Actualizar» en su ventana sólo anota la versión nueva en
// starter.json y después el arranque baja los archivos verificando el SHA-1 de appConfig.json. Actualizador hace lo mismo.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static string Sha1Hex(byte[] data)
        {
            using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        static bool AllowsTl(Uri u)
        {
            var tl = VendorCatalog.BuiltIn.Find(delegate(VendorCheck v) { return v.Id == "tlauncher"; });
            return tl != null && tl.Allows(u);
        }

        /// <summary>appConfig.json como lo publica TLauncher (formato de starter4j), con el .jar de esa versión y sus índices.</summary>
        static string TlRealConfig(string version, string jarSha1, long jarSize)
        {
            return "{\n  \"comment\": \"Config file for desktop-starter\",\n  \"appName\": \"tlauncher\",\n  \"appVersion\": \"" + version + "\",\n" +
                   "  \"mainClass\": \"org.tlauncher.tlauncher.rmo.TLauncher\",\n  \"appArguments\": [],\n  \"jvmArguments\": [\"-Xmx256m\"],\n" +
                   "  \"appFileRepo\": { \"repositories\": [\"https://repo.tlauncher.org/tlauncher-sources/prod/release/tlauncher/\", " +
                   "\"https://repo.fastrepo.org/tlauncher-sources/prod/release/tlauncher/\", \"http://advancedrepository.net/tlauncher-sources/prod/release/tlauncher/\"], " +
                   "\"resources\": [ { \"sha1\": \"" + jarSha1 + "\", \"size\": " + jarSize + ", \"path\": \"original-TLauncher-" + version + ".jar\", " +
                   "\"relativeUrl\": \"" + version + "/original-TLauncher-" + version + ".jar\" } ] },\n" +
                   "  \"appDependencies\": { \"repositories\": [\"https://repo.tlauncher.org/tlauncher-sources/prod/release/tlauncher/\"], " +
                   "\"resources\": [ { \"path\": \"dependencies.json\", \"relativeUrl\": \"" + version + "/dependencies.json\" } ] },\n" +
                   "  \"appResources\": { \"repositories\": [\"https://repo.tlauncher.org/tlauncher-sources/prod/release/tlauncher/\"], " +
                   "\"resources\": [ { \"path\": \"resources.json\", \"relativeUrl\": \"" + version + "/resources.json\" } ] },\n" +
                   "  \"javaRepo\": { \"repositories\": [\"https://repo.tlauncher.org/java/\"], \"resources\": [ { \"path\": \"javaConfig21.json\", \"relativeUrl\": \"javaConfig21.json\" } ] }\n}";
        }

        /// <summary>Un índice de starter4j (Repo) con esos archivos: ruta → contenido.</summary>
        static string TlIndex(string repo, Dictionary<string, byte[]> files)
        {
            var sb = new StringBuilder("{ \"repositories\": [\"" + repo + "\"], \"resources\": [");
            bool first = true;
            foreach (var kv in files)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{ \"sha1\": \"" + Sha1Hex(kv.Value) + "\", \"size\": " + kv.Value.Length + ", \"path\": \"" + kv.Key + "\", \"relativeUrl\": \"" + kv.Key + "\" }");
            }
            return sb.Append("] }").ToString();
        }

        /// <summary>El formato, las direcciones y las rutas: sólo lo del fabricante, por HTTPS, y siempre dentro de la carpeta del arranque.</summary>
        static void TestStarter4jFormato2019()
        {
            byte[] jar = Encoding.UTF8.GetBytes("jar 2.9382");
            string err;
            var cfg = Starter4j.ParseConfig(TlRealConfig("2.9382", Sha1Hex(jar), jar.Length), TlConfigUrl, AllowsTl, out err);
            Check(cfg != null, "appConfig.json real se entiende: " + err);
            if (cfg == null) return;
            Eq("2.9382", cfg.Version, "la versión publicada");
            Eq(1, cfg.MainFiles.Count, "el .jar de la aplicación");
            var main = cfg.MainFiles[0];
            Eq("original-TLauncher-2.9382.jar", main.Path, "dónde va");
            Check(main.Main && main.Size == jar.Length && main.Sha1 == Sha1Hex(jar), "con su tamaño y su SHA-1");
            Eq(2, main.Urls.Count, "sólo los dos servidores HTTPS (no advancedrepository.net, que es HTTP)");
            Check(main.Urls[0] == "https://repo.tlauncher.org/tlauncher-sources/prod/release/tlauncher/2.9382/original-TLauncher-2.9382.jar" &&
                  main.Urls[1].StartsWith("https://repo.fastrepo.org/"), "primero el principal, después el alternativo: " + string.Join(" ", main.Urls.ToArray()));
            Eq(2, cfg.Indexes.Count, "dependencias y recursos (Java no: el arranque lo mantiene aparte)");
            Check(cfg.Indexes[0][0].EndsWith("/2.9382/dependencies.json"), "el índice de dependencias de esa versión");

            // Lo que no sirve.
            Check(Starter4j.ParseConfig("{\"appVersion\":\"2.9382\"}", TlConfigUrl, AllowsTl, out err) == null, "sin el archivo de la aplicación: no sirve");
            Check(Starter4j.ParseConfig("{\"appVersion\":\"latest\",\"appFileRepo\":{}}", TlConfigUrl, AllowsTl, out err) == null, "una versión que no es versión: no sirve");
            Check(Starter4j.ParseConfig("<html>", TlConfigUrl, AllowsTl, out err) == null && err.Contains("JSON"), "una página cualquiera: no sirve");
            string ajeno = TlRealConfig("2.9382", Sha1Hex(jar), jar.Length).Replace("https://repo.tlauncher.org/", "https://evil.example/").Replace("https://repo.fastrepo.org/", "https://tlauncher.org.evil.example/");
            Check(Starter4j.ParseConfig(ajeno, TlConfigUrl, AllowsTl, out err) == null, "con los repositorios en otros servidores: no se baja nada");
            string sinSha = TlRealConfig("2.9382", "xyz", jar.Length);
            Check(Starter4j.ParseConfig(sinSha, TlConfigUrl, AllowsTl, out err) == null, "sin un SHA-1 válido: no se baja");

            // Direcciones.
            string b = "https://repo.tlauncher.org/tlauncher-sources/prod/release/tlauncher/";
            Check(Starter4j.Resolve(b, "2.9382/a.jar", AllowsTl) == b + "2.9382/a.jar", "relativa al repositorio");
            Check(Starter4j.Resolve(b, "../../../other/a.jar", AllowsTl) == null, "nunca fuera del repositorio");
            Check(Starter4j.Resolve(b, "//evil.example/a.jar", AllowsTl) == null && Starter4j.Resolve(b, "https://evil.example/a.jar", AllowsTl) == null, "nunca otro servidor");
            Check(Starter4j.Resolve(null, "http://repo.tlauncher.org/a.jar", AllowsTl) == null, "nunca HTTP");
            Check(Starter4j.Resolve(null, "https://user@repo.tlauncher.org/a.jar", AllowsTl) == null, "nunca con usuario en la dirección");

            // Rutas en la carpeta del arranque.
            Check(Starter4j.RelativeParts("original-TLauncher-2.9382.jar") != null && Starter4j.RelativeParts("libs/org/x/x-1.0.jar") != null, "rutas normales");
            foreach (string mal in new[] { "../x.jar", "a/../../x.jar", "C:\\Windows\\x.dll", "/etc/x", "\\\\srv\\x", "a//b", "starter.json", "starter.json.actualizador.bak",
                                           "con.txt", "COM1", "a/nul", ".hidden/x", "x.jar.", "a:b", "" })
                Check(Starter4j.RelativeParts(mal) == null, "ruta rechazada: «" + mal + "»");
            string work = Path.Combine(Path.GetTempPath(), "act-s4j-" + Guid.NewGuid().ToString("N"));
            Check(Starter4j.LocalPath(work, "libs/a.jar") == Path.GetFullPath(Path.Combine(work, "libs", "a.jar")), "dentro de la carpeta");
            Check(Starter4j.LocalPath(work, "../a.jar") == null, "nunca fuera");

            // starter.json: lo mismo que escribe el arranque al tocar «Actualizar».
            string nuevo = Starter4j.WithVersion("{\"currentAppVersion\":\"2.9381\",\"skipUpdateVersion\":\"2.9382\",\"lastCacheCleaningDate\":\"2026-11-01\"}", "2.9382");
            Check(nuevo != null && nuevo.Contains("\"currentAppVersion\":\"2.9382\"") && nuevo.Contains("\"lastCacheCleaningDate\":\"2026-11-01\"") &&
                  nuevo.Contains("\"skipUpdateVersion\":null"), "versión nueva, la «omitida» deja de estarlo y lo demás queda: " + nuevo);
            string conOtra = Starter4j.WithVersion("{\"currentAppVersion\":\"2.9381\",\"skipUpdateVersion\":\"2.9400\"}", "2.9382");
            Check(conOtra != null && conOtra.Contains("\"skipUpdateVersion\":\"2.9400\""), "una omitida más nueva queda omitida");
            Check(Starter4j.WithVersion("no es json", "2.9382") == null && Starter4j.WithVersion("{}", "x; rm") == null, "nada raro se escribe");
        }

        /// <summary>Aplicar: se baja lo que falta, se verifica, y recién con la aplicación verificada se cambia starter.json.</summary>
        static void TestStarter4jAplicar2019()
        {
            string root = Path.Combine(Path.GetTempPath(), "act-s4j-" + Guid.NewGuid().ToString("N"));
            string work = Path.Combine(root, ".tlauncher", "starter");
            Directory.CreateDirectory(work);
            string starter = Path.Combine(work, "starter.json");
            string original = "{\"currentAppVersion\":\"2.9381\",\"skipUpdateVersion\":null,\"lastCacheCleaningDate\":\"2026-11-01\"}";
            File.WriteAllText(starter, original);
            byte[] jar = Encoding.UTF8.GetBytes("contenido del jar 2.9382");
            var deps = new Dictionary<string, byte[]> { { "libs/org/a/a-1.0.jar", Encoding.UTF8.GetBytes("a 1.0") }, { "libs/org/b/b-2.0.jar", Encoding.UTF8.GetBytes("b 2.0") } };
            var res = new Dictionary<string, byte[]> { { "resources/fondo.png", Encoding.UTF8.GetBytes("png") } };
            string repo = "https://repo.tlauncher.org/tlauncher-sources/prod/release/tlauncher/";
            var server = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            server[repo + "2.9382/original-TLauncher-2.9382.jar"] = jar;
            foreach (var kv in deps) server[repo + kv.Key] = kv.Value;
            foreach (var kv in res) server[repo + kv.Key] = kv.Value;
            var indexes = new Dictionary<string, string>
            {
                { repo + "2.9382/dependencies.json", TlIndex(repo, deps) },
                { repo + "2.9382/resources.json", TlIndex(repo, res) },
            };
            var asked = new List<string>();
            var oldDl = Starter4j.DownloadForTests;
            var oldFetch = Starter4j.FetchForTests;
            try
            {
                Starter4j.FetchForTests = delegate(string u) { string t; return indexes.TryGetValue(u, out t) ? t : null; };
                Starter4j.DownloadForTests = delegate(string u, string path)
                {
                    asked.Add(u);
                    byte[] data;
                    if (!server.TryGetValue(u, out data)) throw new InvalidOperationException("404");
                    File.WriteAllBytes(path, data);
                };
                string err;
                var cfg = Starter4j.ParseConfig(TlRealConfig("2.9382", Sha1Hex(jar), jar.Length), TlConfigUrl, AllowsTl, out err);
                // Una dependencia ya está bien: no se baja de nuevo.
                Directory.CreateDirectory(Path.Combine(work, "libs", "org", "a"));
                File.WriteAllBytes(Path.Combine(work, "libs", "org", "a", "a-1.0.jar"), deps["libs/org/a/a-1.0.jar"]);
                var o = Starter4j.Apply(cfg, work, starter, AllowsTl, null, null);
                Check(o.Ok, "la captura: se actualiza sin abrir nada: " + o.Error);
                Eq(3, o.Downloaded, "el .jar, la dependencia que faltaba y el recurso");
                Eq(1, o.AlreadyOk, "la que ya estaba no se baja de nuevo");
                Check(!asked.Contains(repo + "libs/org/a/a-1.0.jar"), "no se pidió la que ya estaba");
                Check(File.Exists(Path.Combine(work, "original-TLauncher-2.9382.jar")) && File.ReadAllText(Path.Combine(work, "original-TLauncher-2.9382.jar")) == "contenido del jar 2.9382", "el .jar quedó en su lugar");
                Check(File.Exists(Path.Combine(work, "resources", "fondo.png")), "los recursos también");
                string ahora = File.ReadAllText(starter);
                Check(ahora.Contains("\"currentAppVersion\":\"2.9382\"") && ahora.Contains("2026-11-01"), "starter.json con la 2.9382 (y lo demás igual): " + ahora);
                Check(File.Exists(starter + ".actualizador.bak") && File.ReadAllText(starter + ".actualizador.bak") == original, "con copia del anterior");
                Check(Directory.GetFiles(work, "*.part", SearchOption.AllDirectories).Length == 0, "sin temporales");

                // El servidor principal manda otra cosa: se prueba el alternativo, y sólo vale lo que coincide con el SHA-1.
                File.WriteAllText(starter, original);
                File.Delete(Path.Combine(work, "original-TLauncher-2.9382.jar"));
                server[repo + "2.9382/original-TLauncher-2.9382.jar"] = Encoding.UTF8.GetBytes("contenido alterado del jar 2.9382");
                server["https://repo.fastrepo.org/tlauncher-sources/prod/release/tlauncher/2.9382/original-TLauncher-2.9382.jar"] = jar;
                o = Starter4j.Apply(cfg, work, starter, AllowsTl, null, null);
                Check(o.Ok && File.ReadAllText(Path.Combine(work, "original-TLauncher-2.9382.jar")) == "contenido del jar 2.9382", "lo alterado no se guarda; vale el del alternativo");

                // Ningún servidor da el .jar correcto: no se cambia nada (TLauncher queda como estaba).
                File.WriteAllText(starter, original);
                File.Delete(Path.Combine(work, "original-TLauncher-2.9382.jar"));
                server.Remove("https://repo.fastrepo.org/tlauncher-sources/prod/release/tlauncher/2.9382/original-TLauncher-2.9382.jar");
                o = Starter4j.Apply(cfg, work, starter, AllowsTl, null, null);
                Check(!o.Ok && o.Error.Contains("SHA-1"), "sin el .jar verificado: error con el motivo: " + o.Error);
                Eq(original, File.ReadAllText(starter), "starter.json sin tocar");
                Check(!File.Exists(Path.Combine(work, "original-TLauncher-2.9382.jar")), "y lo alterado no quedó en la carpeta");

                // Una dependencia no se puede bajar: se actualiza igual (el arranque la completa al abrirse, verificándola).
                server[repo + "2.9382/original-TLauncher-2.9382.jar"] = jar;
                File.Delete(Path.Combine(work, "libs", "org", "b", "b-2.0.jar"));
                server.Remove(repo + "libs/org/b/b-2.0.jar");
                o = Starter4j.Apply(cfg, work, starter, AllowsTl, null, null);
                Check(o.Ok && o.LeftForStarter == 1, "una dependencia que falta la completa TLauncher: " + o.Error);
                Check(File.ReadAllText(starter).Contains("2.9382"), "y la versión quedó anotada");

                // Una ruta fuera de la carpeta en un índice: se ignora.
                indexes[repo + "2.9382/resources.json"] = "{ \"repositories\": [\"" + repo + "\"], \"resources\": [ { \"sha1\": \"" + Sha1Hex(jar) + "\", \"size\": " + jar.Length +
                                                          ", \"path\": \"../../evil.jar\", \"relativeUrl\": \"2.9382/original-TLauncher-2.9382.jar\" } ] }";
                o = Starter4j.Apply(cfg, work, starter, AllowsTl, null, null);
                Check(o.Ok && !File.Exists(Path.Combine(root, "evil.jar")) && !File.Exists(Path.Combine(root, ".tlauncher", "evil.jar")), "nunca se escribe fuera de la carpeta del arranque");
            }
            finally
            {
                Starter4j.DownloadForTests = oldDl;
                Starter4j.FetchForTests = oldFetch;
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>De punta a punta: la búsqueda la deja marcable y «Actualizar» la instala sin abrir tlauncher.org.</summary>
        static void TestCapturaTLauncher2019()
        {
            string root = Path.Combine(Path.GetTempPath(), "act-tl-" + Guid.NewGuid().ToString("N"));
            string roaming = Path.Combine(root, "AppData", "Roaming");
            string work = Path.Combine(roaming, ".tlauncher", "starter");
            Directory.CreateDirectory(work);
            string starter = Path.Combine(work, "starter.json");
            File.WriteAllText(starter, "{\"currentAppVersion\":\"2.9381\",\"skipUpdateVersion\":null}");
            byte[] jar = Encoding.UTF8.GetBytes("jar 2.9382");
            string repo = "https://repo.tlauncher.org/tlauncher-sources/prod/release/tlauncher/";
            string config = TlRealConfig("2.9382", Sha1Hex(jar), jar.Length);

            var oldFetch = VendorProvider.FetchForTests;
            var oldRead = VendorProvider.ReadFileForTests;
            var oldRoaming = VendorProvider.RoamingFoldersForTests;
            var oldDl = Starter4j.DownloadForTests;
            var oldIdx = Starter4j.FetchForTests;
            var oldSnap = RunningPrograms.SnapshotForTests;
            var oldOpen = VendorProvider.OpenPageForTests;
            var opened = new List<string>();
            try
            {
                VendorProvider.RoamingFoldersForTests = delegate(InstalledProgram p) { return new List<string> { roaming }; };
                VendorProvider.ReadFileForTests = delegate(string path)
                {
                    string real = path.Replace('\\', Path.DirectorySeparatorChar);
                    return File.Exists(real) ? File.ReadAllText(real) : null;
                };
                VendorProvider.FetchForTests = delegate(string a) { return a == TlConfigUrl ? config : null; };
                Starter4j.FetchForTests = delegate(string u) { return u.EndsWith("dependencies.json") || u.EndsWith("resources.json") ? "{ \"repositories\": [], \"resources\": [] }" : null; };
                Starter4j.DownloadForTests = delegate(string u, string path)
                {
                    if (u != repo + "2.9382/original-TLauncher-2.9382.jar") throw new InvalidOperationException("404");
                    File.WriteAllBytes(path, jar);
                };
                VendorProvider.OpenPageForTests = delegate(string u) { opened.Add(u); };
                var running = new List<RunningProcess>();
                RunningPrograms.SnapshotForTests = delegate() { return running; };

                // --- La búsqueda: 2.9381 (starter.json) con la 2.9382 publicada → marcable, del fabricante, en «Marcar todo».
                var ctx = ContextoSimulado(true, TlProgram("2.9323"));
                var rows = new List<UpdateCandidate>();
                new VendorProvider().Scan(ctx, rows, new List<string>());
                var row = rows.Find(delegate(UpdateCandidate c) { return c.Name == "TLauncher"; });
                Check(row != null && row.Status == UpdateStatus.Disponible && row.InstalledVersion == "2.9381" && row.AvailableVersion == "2.9382", "la captura: 2.9381 → 2.9382");
                if (row == null) return;
                Check(row.Installable && row.Recommended, "se puede instalar desde acá y entra en «Marcar todo» (antes era opcional y abría la página)");
                var plan = row.ProviderTag as VendorPlan;
                Check(plan != null && !plan.Machine, "no pide reiniciar como administrador: se escribe en el perfil del usuario");
                var all = new List<UpdateCandidate> { row };
                UpdateManager.MakeManualActionable(all);
                Check(row.ProviderName == VendorProvider.ProviderId, "no pasa a «abrir la página»");

                // --- TLauncher abierto y sin aceptar cerrarlo: no se toca nada y la fila dice qué cerrar.
                running.Add(new RunningProcess { Pid = 4242, Name = "TLauncher.exe", Path = @"C:\Users\Fede\AppData\Roaming\.minecraft\TLauncher.exe", SessionId = 1 });
                var found = UpdateManager.FindRunning(ctx, new List<UpdateCandidate> { row });
                Check(found.Count == 1 && found[0].Value.Count == 1, "la ventana de confirmación ofrece cerrarlo");
                var vp = new VendorProvider();
                vp.Install(ctx, new List<UpdateCandidate> { row }, delegate(UpdateCandidate c) { });
                Check(row.Status == UpdateStatus.Fallida && row.Result.Contains("TLauncher.exe") && row.Result.Contains("abierto"), "abierto: no se actualiza (" + row.Result + ")");
                Check(File.ReadAllText(starter).Contains("2.9381"), "starter.json sin tocar");
                Check(opened.Count == 0, "y no se abre ninguna página");

                // --- Cerrado: se actualiza.
                running.Clear();
                row.Status = UpdateStatus.Disponible;
                var steps = new List<string>();
                new VendorProvider().Install(ctx, new List<UpdateCandidate> { row }, delegate(UpdateCandidate c) { steps.Add(c.Result ?? ""); });
                Check(row.Status == UpdateStatus.Instalada, "la captura: «Actualizada» (" + row.Status + ": " + row.Result + ")");
                Eq("2.9382", row.InstalledVersion, "instalada la 2.9382");
                Check(row.Result.StartsWith("Actualizada correctamente") && row.Result.Contains("SHA-1") && row.Result.Contains("oferta"), "el resultado dice cómo: " + row.Result);
                Check(File.ReadAllText(starter).Contains("\"currentAppVersion\":\"2.9382\""), "starter.json con la 2.9382");
                Check(File.Exists(Path.Combine(work, "original-TLauncher-2.9382.jar")), "el .jar verificado en la carpeta del arranque");
                Check(opened.Count == 0, "sin abrir tlauncher.org");
                Check(steps.Exists(delegate(string s) { return s.Contains("Descargando original-TLauncher-2.9382.jar"); }), "la fila muestra qué está bajando");

                // --- La búsqueda siguiente: al día.
                rows.Clear();
                new VendorProvider().Scan(ctx, rows, new List<string>());
                row = rows.Find(delegate(UpdateCandidate c) { return c.Name == "TLauncher"; });
                Check(row != null && row.Status == UpdateStatus.AlDia && row.InstalledVersion == "2.9382", "al buscar de nuevo: al día");

                // --- Sin starter.json (nunca se abrió): no se inventa nada.
                File.Delete(starter);
                rows.Clear();
                new VendorProvider().Scan(ctx, rows, new List<string>());
                row = rows.Find(delegate(UpdateCandidate c) { return c.Name == "TLauncher"; });
                Check(row != null && !row.Installable && row.Detail.Contains("starter.json"), "sin starter.json: lo dice y no se instala");
            }
            finally
            {
                VendorProvider.FetchForTests = oldFetch; VendorProvider.ReadFileForTests = oldRead; VendorProvider.RoamingFoldersForTests = oldRoaming;
                Starter4j.DownloadForTests = oldDl; Starter4j.FetchForTests = oldIdx; RunningPrograms.SnapshotForTests = oldSnap; VendorProvider.OpenPageForTests = oldOpen;
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
