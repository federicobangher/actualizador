// Pruebas de la 2.0.16. Captura del usuario (13/11/2026): TLauncher 2.9323 «No verificable» mientras el propio TLauncher mostraba
// «¡Nueva actualización encontrada! 2.9381», y el pedido de una auditoría completa de la versión que se instala y de la portable.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        const string TlConfigUrl = "https://repo.tlauncher.org/tlauncher-sources/prod/release/tlauncher/appConfig.json";
        const string TlMirrorUrl = "https://repo.fastrepo.org/tlauncher-sources/prod/release/tlauncher/appConfig.json";

        /// <summary>El appConfig.json que baja el arranque de TLauncher (formato de starter4j), con esa versión.</summary>
        static string TlAppConfig(string version, bool quoted)
        {
            string v = quoted ? "\"" + version + "\"" : version;
            return "{\n  \"comment\": \"Config file for desktop-starter (beta channel is separate)\",\n  \"appName\": \"tlauncher\",\n  \"appVersion\": " + v + ",\n" +
                   "  \"mainClass\": \"org.tlauncher.tlauncher.rmo.TLauncher\",\n  \"appArguments\": [],\n  \"jvmArguments\": [\"-Xmx256m\"],\n" +
                   "  \"appFileRepo\": { \"repositories\": [\"https://repo.tlauncher.org/tlauncher-sources/prod/release/tlauncher/\"], \"resources\": [ { \"sha1\": \"039f3bdb8a7e985c1f90ebb992795f171222058b\", " +
                   "\"size\": 9187331, \"path\": \"original-TLauncher-" + version + ".jar\", \"relativeUrl\": \"" + version + "/original-TLauncher-" + version + ".jar\" } ] },\n" +
                   "  \"appDependencies\": { \"resources\": [ { \"path\": \"dependencies.json\", \"relativeUrl\": \"" + version + "/dependencies.json\" } ] },\n" +
                   "  \"javaRepo\": { \"resources\": [ { \"path\": \"javaConfig21.json\", \"relativeUrl\": \"javaConfig21.json\" } ] }\n}";
        }

        static InstalledProgram TlProgram(string registryVersion)
        {
            return new InstalledProgram { Name = "TLauncher", Version = registryVersion, Publisher = "TLauncher Inc.", Scope = "Equipo", Architecture = "x86",
                                          InstallLocation = @"C:\Users\Fede\AppData\Roaming\.minecraft", DisplayIcon = @"C:\Users\Fede\AppData\Roaming\.minecraft\TLauncher.exe",
                                          UninstallString = "\"C:\\Users\\Fede\\AppData\\Roaming\\.minecraft\\unins000.exe\"", InfoUrl = "https://tlauncher.org" };
        }

        /// <summary>La captura: TLauncher se compara con la misma fuente que consulta al abrirse, y con la versión que anota en el perfil.</summary>
        static void TestTLauncher2016()
        {
            var tl = VendorCatalog.BuiltIn.Find(delegate(VendorCheck v) { return v.Id == "tlauncher"; });
            Check(tl != null, "TLauncher en el catálogo de fabricantes");
            if (tl == null) return;
            Check(tl.Applies(TlProgram("2.9323")) && tl.Applies(new InstalledProgram { Name = "TLauncher 2.9323" }), "el nombre de la captura (y con la versión en el nombre)");
            Check(!tl.Applies(new InstalledProgram { Name = "TLauncher PE" }) && !tl.Applies(new InstalledProgram { Name = "TLauncher Installer Helper" }), "no otros productos con ese prefijo");
            // 2.0.19: se instala desde acá como lo hace su arranque (Pruebas2019); antes sólo se comparaba y «Actualizar» abría tlauncher.org.
            Check(tl.Install == VendorInstall.Starter4j && tl.Recommended, "2.0.19: se actualiza desde acá, como su propio arranque");
            Check(tl.HowToUpdate.Contains("oferta") && !tl.HowToUpdate.ToLowerInvariant().Contains("administrador"), "dice cómo actualizarlo y avisa de la oferta (sin la palabra que impide marcar la fila)");
            Uri u;
            Check(Uri.TryCreate(tl.Url, UriKind.Absolute, out u) && tl.Allows(u), "la fuente es de tlauncher.org por HTTPS");
            foreach (string m in tl.MirrorUrls) Check(Uri.TryCreate(m, UriKind.Absolute, out u) && tl.Allows(u), "el servidor alternativo está permitido: " + m);
            Check(!tl.Allows(new Uri("https://tlauncher.org.evil.example/appConfig.json")) && !tl.Allows(new Uri("http://repo.tlauncher.org/x.json")), "no otros dominios ni HTTP");

            // La versión publicada: «appVersion», con comillas o como número (starter4j lo escribe de las dos maneras). No la del nombre del .jar
            // ni se descarta porque el comentario del archivo diga «beta».
            string url;
            Eq("2.9381", VendorCatalog.ParsePage(tl, TlAppConfig("2.9381", true), out url), "appVersion con comillas");
            Eq("2.9381", VendorCatalog.ParsePage(tl, TlAppConfig("2.9381", false), out url), "appVersion como número");
            Check(VendorCatalog.ParsePage(tl, "<html><body>TLauncher 2.9307 at the moment</body></html>", out url) == null, "una página cualquiera no sirve de fuente");
            Check(VersionUtil.IsNewer("2.9381", "2.9323") && !VersionUtil.IsNewer("2.9323", "2.9381"), "2.9381 es más nueva que 2.9323 (como la compara el propio TLauncher)");

            // La versión instalada: la que anota el arranque de TLauncher en el perfil (starter.json).
            string starter = "{\"currentAppVersion\":\"2.9323\",\"skipUpdateVersion\":null,\"lastCacheCleaningDate\":\"2026-11-01\"}";
            string source;
            var folders = new List<string> { @"C:\Users\Fede\AppData\Roaming", @"C:\Users\Otra\AppData\Roaming" };
            Eq("2.9323", VendorProvider.ProfileVersionOf(tl, folders, delegate(string path) { return path == @"C:\Users\Fede\AppData\Roaming\.tlauncher\starter\starter.json" ? starter : null; }, out source), "starter.json del perfil");
            Eq(@"C:\Users\Fede\AppData\Roaming\.tlauncher\starter\starter.json", source, "de qué archivo salió");
            Eq("2.9350", VendorProvider.ProfileVersionOf(tl, folders, delegate(string path) { return path.StartsWith(@"C:\Users\Otra\") ? "{ \"currentAppVersion\" : \"2.9350\" }" : null; }, out source),
               "si el primer perfil no lo tiene, el siguiente");
            Check(VendorProvider.ProfileVersionOf(tl, folders, delegate(string path) { return "{\"currentAppVersion\":null}"; }, out source) == null, "sin versión anotada: no se inventa");
            Check(VendorProvider.ProfileVersionOf(tl, folders, delegate(string path) { throw new UnauthorizedAccessException(); }, out source) == null, "un perfil que no se puede leer no rompe nada");
            var fuera = new VendorCheck { ProfileVersionFiles = new[] { @"..\..\Windows\win.ini", @"C:\Windows\win.ini", @"\\servidor\c$\x.json", "/etc/x.json" }, ProfileVersion = tl.ProfileVersion };
            Check(VendorProvider.ProfileVersionOf(fuera, folders, delegate(string path) { return starter; }, out source) == null, "nunca un archivo fuera del perfil");
            var roaming = VendorProvider.RoamingFolders(TlProgram("2.9323"));
            Check(roaming.Count > 0 && roaming[0] == @"C:\Users\Fede\AppData\Roaming", "primero el perfil donde está instalado: " + (roaming.Count > 0 ? roaming[0] : "(ninguno)"));

            var oldFetch = VendorProvider.FetchForTests;
            var oldRead = VendorProvider.ReadFileForTests;
            var oldRoaming = VendorProvider.RoamingFoldersForTests;
            try
            {
                VendorProvider.RoamingFoldersForTests = delegate(InstalledProgram p) { return new List<string> { @"C:\Users\Fede\AppData\Roaming" }; };
                string published = TlAppConfig("2.9381", true), local = starter;
                var asked = new List<string>();
                VendorProvider.FetchForTests = delegate(string a) { asked.Add(a); return a == TlConfigUrl ? published : null; };
                VendorProvider.ReadFileForTests = delegate(string path) { return path.EndsWith(@"\.tlauncher\starter\starter.json") ? local : null; };

                // --- La captura: 2.9323 instalada, 2.9381 publicada.
                var rows = new List<UpdateCandidate>();
                var notes = new List<string>();
                new VendorProvider().Scan(ContextoSimulado(true, TlProgram("2.9323")), rows, notes);
                var row = rows.Find(delegate(UpdateCandidate c) { return c.Name == "TLauncher"; });
                Check(row != null, "TLauncher se compara");
                if (row != null)
                {
                    Eq(UpdateStatus.Disponible, row.Status, "la captura: hay una versión nueva");
                    Eq("2.9323", row.InstalledVersion, "instalada 2.9323");
                    Eq("2.9381", row.AvailableVersion, "disponible 2.9381");
                    // 2.0.19: se instala desde acá (antes «no se instala desde acá» y «Actualizar» abría tlauncher.org).
                    Check(row.Installable && row.Recommended && row.ProviderName == VendorProvider.ProviderId, "se instala desde acá y entra en «Marcar todo»");
                    Check(row.Detail.Contains("2.9381") && row.Detail.Contains("oferta") && row.Detail.Contains("starter.json") && row.Detail.Contains("SHA-1"),
                          "el detalle dice la versión, cómo se actualiza y de dónde salió la instalada: " + row.Detail);
                    // La fila de winget («No verificable») se reemplaza por esta, y NO pasa a «abrir la página».
                    var winget = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "TLauncher", InstalledVersion = "2.9323", Status = UpdateStatus.NoVerificable,
                                                       ProviderName = WingetProvider.ProviderId, Identifier = "registro:TLauncher" };
                    var all = new List<UpdateCandidate> { winget, row };
                    UpdateManager.DeduplicateVendor(all);
                    Check(all.Count == 1 && all[0] == row, "una sola fila: la del fabricante");
                    UpdateManager.MakeManualActionable(all);
                    Check(row.Installable && row.ProviderName == VendorProvider.ProviderId && row.Recommended, "sigue siendo del fabricante: no abre tlauncher.org");
                    var k = UpdateGroups.Count(all);
                    Eq(0, k.Unverified, "ya no figura «sin verificar»");
                    Eq(1, k.ToUpdate + k.Optional, "figura entre las disponibles");
                }

                // --- TLauncher ya se actualizó solo: «Aplicaciones» sigue diciendo 2.9323, starter.json dice 2.9381.
                local = "{\"currentAppVersion\":\"2.9381\"}";
                rows.Clear();
                new VendorProvider().Scan(ContextoSimulado(true, TlProgram("2.9323")), rows, notes);
                row = rows.Find(delegate(UpdateCandidate c) { return c.Name == "TLauncher"; });
                Check(row != null && row.Status == UpdateStatus.AlDia, "actualizado por su cuenta: al día (antes habría dicho «disponible» para siempre)");
                if (row != null)
                {
                    Eq("2.9381", row.InstalledVersion, "instalada: la real, 2.9381");
                    Check(row.Detail.Contains("2.9323") && row.Detail.Contains("Aplicaciones"), "explica que Windows sigue mostrando la 2.9323: " + row.Detail);
                }

                // --- Sin starter.json (un TLauncher viejo, o el perfil de otra cuenta): se compara con el registro.
                local = null;
                rows.Clear();
                new VendorProvider().Scan(ContextoSimulado(true, TlProgram("2.86")), rows, notes);
                row = rows.Find(delegate(UpdateCandidate c) { return c.Name == "TLauncher"; });
                Check(row != null && row.Status == UpdateStatus.Disponible && row.InstalledVersion == "2.86" && row.AvailableVersion == "2.9381", "sin starter.json: con la versión del registro");

                // --- El servidor principal no responde: se lee del alternativo (el que usa el propio arranque de TLauncher).
                local = starter;
                asked.Clear();
                VendorProvider.FetchForTests = delegate(string a) { asked.Add(a); return a == TlMirrorUrl ? published : null; };
                rows.Clear();
                new VendorProvider().Scan(ContextoSimulado(true, TlProgram("2.9323")), rows, notes);
                row = rows.Find(delegate(UpdateCandidate c) { return c.Name == "TLauncher"; });
                Check(row != null && row.Status == UpdateStatus.Disponible && row.AvailableVersion == "2.9381", "con repo.tlauncher.org caído se lee del servidor alternativo");
                Check(asked.Count == 2 && asked[0] == TlConfigUrl && asked[1] == TlMirrorUrl, "primero el principal, después el alternativo");

                // --- Ninguno responde: error (queda la fila de winget), nunca un «al día» inventado.
                VendorProvider.FetchForTests = delegate(string a) { return null; };
                rows.Clear();
                notes.Clear();
                new VendorProvider().Scan(ContextoSimulado(true, TlProgram("2.9323")), rows, notes);
                row = rows.Find(delegate(UpdateCandidate c) { return c.Name == "TLauncher"; });
                Check(row != null && row.Status == UpdateStatus.Error, "sin respuesta: error al verificar");
                Check(notes.Exists(delegate(string n) { return n.Contains("TLauncher"); }), "y la búsqueda lo dice en sus notas");
                if (row != null)
                {
                    var winget = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "TLauncher", InstalledVersion = "2.9323", Status = UpdateStatus.NoVerificable, ProviderName = WingetProvider.ProviderId };
                    var all = new List<UpdateCandidate> { winget, row };
                    UpdateManager.DeduplicateVendor(all);
                    Check(all.Count == 1 && all[0] == winget, "con error del fabricante queda la fila de winget");
                }

                // --- El archivo cambió de forma: error, no una versión cualquiera.
                VendorProvider.FetchForTests = delegate(string a) { return "{\"appName\":\"tlauncher\",\"version\":\"7.1\"}"; };
                rows.Clear();
                new VendorProvider().Scan(ContextoSimulado(true, TlProgram("2.9323")), rows, notes);
                row = rows.Find(delegate(UpdateCandidate c) { return c.Name == "TLauncher"; });
                Check(row != null && row.Status == UpdateStatus.Error, "appConfig.json sin «appVersion»: error");
            }
            finally { VendorProvider.FetchForTests = oldFetch; VendorProvider.ReadFileForTests = oldRead; VendorProvider.RoamingFoldersForTests = oldRoaming; }
        }

        /// <summary>MSI Afterburner (también «No verificable» en la captura): la versión «Final» de msi.com, no la beta de al lado.</summary>
        static void TestAfterburner2016()
        {
            var ab = VendorCatalog.BuiltIn.Find(delegate(VendorCheck v) { return v.Id == "msi-afterburner"; });
            Check(ab != null, "MSI Afterburner en el catálogo de fabricantes");
            if (ab == null) return;
            Check(ab.Applies(new InstalledProgram { Name = "MSI Afterburner 4.6.6" }) && ab.Applies(new InstalledProgram { Name = "MSI Afterburner" }) &&
                  ab.Applies(new InstalledProgram { Name = "MSI Afterburner 4.6.7 Beta 3" }), "los nombres con que se registra");
            Check(!ab.Applies(new InstalledProgram { Name = "MSI Afterburner Remote Server" }) && !ab.Applies(new InstalledProgram { Name = "MSI Center" }), "no otros programas de MSI");
            string url;
            string page = "<div class=\"ver\"><h3>MSI AFTERBURNER</h3><p class=\"num\">4.6.6 (Final)</p><p>Released : Sep 2026</p></div>\n" +
                          "<div class=\"ver\"><p class=\"num\">4.6.7 (Beta)</p><p>Released : Sep 2026</p></div><script>var v='9.9.9 (Final)';</script>";
            Eq("4.6.6", VendorCatalog.ParsePage(ab, page, out url), "la que dice «Final»");
            Eq("4.6.6", VendorCatalog.ParsePage(ab, "<span>4.6.6</span>&nbsp;<b>( Final )</b> 4.6.7 (Beta)", out url), "con la beta pegada al lado igual se lee la final");
            Check(VendorCatalog.ParsePage(ab, "<p>4.6.7 (Beta)</p><p>Windows 10.0.19045 (Final build)</p>", out url) == null, "sin una versión «Final»: nada (no la beta)");
            Check(ab.Install == VendorInstall.None && !ab.Recommended, "sólo se compara (MSI lo entrega en un .zip)");

            var oldFetch = VendorProvider.FetchForTests;
            try
            {
                VendorProvider.FetchForTests = delegate(string a) { return a == ab.Url ? page : null; };
                var rows = new List<UpdateCandidate>();
                var prog = new InstalledProgram { Name = "MSI Afterburner 4.6.6", Version = "4.6.6", Publisher = "MSI Co., LTD", Scope = "Equipo", Architecture = "x86",
                                                  InstallLocation = @"C:\Program Files (x86)\MSI Afterburner", UninstallString = "", DisplayIcon = "" };
                new VendorProvider().Scan(ContextoSimulado(true, prog), rows, new List<string>());
                var row = rows.Find(delegate(UpdateCandidate c) { return c.Name == "MSI Afterburner 4.6.6"; });
                Check(row != null && row.Status == UpdateStatus.AlDia && row.AvailableVersion == "4.6.6", "la captura: 4.6.6 instalada está al día (antes «No verificable»)");
                prog.Name = "MSI Afterburner 4.6.5"; prog.Version = "4.6.5";
                rows.Clear();
                new VendorProvider().Scan(ContextoSimulado(true, prog), rows, new List<string>());
                row = rows.Find(delegate(UpdateCandidate c) { return c.Name == "MSI Afterburner 4.6.5"; });
                Check(row != null && row.Status == UpdateStatus.Disponible && row.AvailableVersion == "4.6.6", "una 4.6.5: disponible la 4.6.6");
                prog.Name = "MSI Afterburner 4.6.7 Beta 3"; prog.Version = "4.6.7 Beta 3";
                rows.Clear();
                new VendorProvider().Scan(ContextoSimulado(true, prog), rows, new List<string>());
                row = rows.Find(delegate(UpdateCandidate c) { return c.Name.StartsWith("MSI Afterburner 4.6.7"); });
                Check(row != null && row.Status == UpdateStatus.AlDia, "con una beta más nueva instalada no se ofrece volver a la final");
            }
            finally { VendorProvider.FetchForTests = oldFetch; }
        }

        /// <summary>Versiones preliminares escritas con espacio y los juegos de la captura que no tienen tienda.</summary>
        static void TestBetasYJuegos2016()
        {
            Check(VersionUtil.IsPreRelease("4.6.7 Beta 3") && VersionUtil.IsPreRelease("2.1_rc1") && VersionUtil.IsPreRelease("1.0-beta.2"), "«Beta 3» con espacio es preliminar");
            Check(!VersionUtil.IsPreRelease("4.6.6") && !VersionUtil.IsPreRelease("1.0 Developer Edition") && !VersionUtil.IsPreRelease("3.2 Release"), "«Developer», «Release» no son betas");
            Check(VersionUtil.IsNewer("4.6.7", "4.6.7 Beta 3"), "la 4.6.7 final es más nueva que la 4.6.7 Beta 3");
            Check(VersionUtil.IsNewer("4.6.7 Beta 3", "4.6.7 Beta 2") && VersionUtil.IsNewer("4.6.7 RC1", "4.6.7 Beta 3"), "entre betas, por etapa y número");
            Check(!VersionUtil.IsNewer("4.6.6", "4.6.7 Beta 3"), "una final más vieja no es más nueva que una beta posterior");

            var sf = new InstalledProgram { Name = "Street FighterTM 6", Version = "", Publisher = "CAPCOM CO., LTD.", InstallLocation = @"D:\Juegos\Street Fighter 6", UninstallString = "", Scope = "Equipo" };
            var h = ProgramHints.For(sf);
            Check(h != null && h.Text.Contains("tienda"), "Street Fighter 6 (CAPCOM): juego, se actualiza desde su tienda");
            var proto = new InstalledProgram { Name = "Juego Prototype(TM)", Version = "1.0", Publisher = "", InstallLocation = @"C:\Games\Prototype", UninstallString = "", Scope = "Equipo" };
            Check(ProgramHints.For(proto) != null && ProgramHints.IsGame(proto), "en una carpeta de juegos también");
            var steam = new InstalledProgram { Name = "Street Fighter 6", Publisher = "CAPCOM CO., LTD.", InstallLocation = @"D:\SteamLibrary\steamapps\common\Street Fighter 6", UninstallString = "steam://uninstall/1364780", Scope = "Equipo" };
            Check(ProgramHints.For(steam).Text.Contains("Steam"), "si es de Steam, manda Steam");
            Check(!ProgramHints.IsGame(new InstalledProgram { Name = "Notepad++", Publisher = "Notepad++ Team", InstallLocation = @"C:\Program Files\Notepad++" }), "un programa común no es un juego");
            var epic = ProgramHints.For(new InstalledProgram { Name = "Epic Games Launcher", Publisher = "Epic Games, Inc." });
            Check(epic != null && epic.Text.Contains("actualizador propio"), "el lanzador de Epic conserva su propia explicación");
        }

        /// <summary>
        /// Portable: lo que se recuerda de UN equipo (los controladores que Windows no aplicó) no viaja al siguiente ni se borra con
        /// «Olvidar las consultas guardadas».
        /// </summary>
        static void TestDatosDelEquipo2016()
        {
            string dir = Path.Combine(Path.GetTempPath(), "act-pruebas-" + Guid.NewGuid().ToString("N"));
            string old = QueryCache.DirectoryForTests;
            try
            {
                QueryCache.DirectoryForTests = dir;
                Check(QueryCache.MachineDirectory != QueryCache.Directory, "lo del equipo no va en la carpeta de consultas guardadas");
                var life = TimeSpan.FromDays(30);
                var q = QueryCache.OpenMachine(QueryCache.MachineStateName, life);
                q.Put(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0887\4&2c4b&0&0001|11111111-2222-3333-4444-555555555555", "2026-11-13");
                q.Save();
                Check(File.Exists(Path.Combine(QueryCache.MachineDirectory, QueryCache.MachineStateName + ".json")), "se guarda con los datos del equipo");
                Check(!File.Exists(Path.Combine(QueryCache.Directory, QueryCache.MachineStateName + ".json")), "no entre las consultas guardadas (en la portable, compartidas por todos los equipos)");
                var c = QueryCache.Open("winget-busquedas", TimeSpan.FromHours(12));
                c.Put("vlc", "tabla");
                c.Save();
                Check(QueryCache.Clear() == 1, "«Olvidar las consultas guardadas» borra las consultas");
                string when;
                Check(QueryCache.OpenMachine(QueryCache.MachineStateName, life).TryGet(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0887\4&2c4b&0&0001|11111111-2222-3333-4444-555555555555", out when) && when == "2026-11-13",
                      "pero no olvida los controladores que Windows no aplicó (volvían a ofrecerse)");
                Check(!QueryCache.Open("winget-busquedas", TimeSpan.FromHours(12)).TryGet("vlc", out when), "las consultas sí se olvidaron");

                // Lo que dejó la 2.0.15 en «cache» se sigue leyendo (versión instalada) y pasa a su lugar.
                Directory.Delete(dir, true);
                var legacy = QueryCache.Open(QueryCache.MachineStateName, life);
                legacy.Put("DISPOSITIVO|GUID", "2026-11-10");
                legacy.Save();
                var moved = QueryCache.OpenMachine(QueryCache.MachineStateName, life);
                Check(moved.TryGet("DISPOSITIVO|GUID", out when) && when == "2026-11-10", "lo que recordaba la 2.0.15 no se pierde");
                moved.Put("OTRO|GUID", "2026-11-13");
                moved.Save();
                var again = QueryCache.OpenMachine(QueryCache.MachineStateName, life);
                Check(again.TryGet("DISPOSITIVO|GUID", out when) && again.TryGet("OTRO|GUID", out when), "y queda guardado en el lugar nuevo junto con lo nuevo");
                Directory.Delete(dir, true);
                legacy = QueryCache.Open(QueryCache.MachineStateName, life);
                legacy.Put("DISPOSITIVO|GUID", "2026-11-10");
                legacy.Save();
                Eq(0, QueryCache.Clear(), "al olvidar las consultas, lo del equipo que estaba en «cache» se pasa en vez de borrarse");
                Check(QueryCache.OpenMachine(QueryCache.MachineStateName, life).TryGet("DISPOSITIVO|GUID", out when), "y se sigue recordando");
            }
            finally
            {
                QueryCache.DirectoryForTests = old;
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
            Check(Leer(Raiz(), "src/Actualizador.Engine/Updates/CatalogDriverProvider.cs").Contains("QueryCache.OpenMachine(QueryCache.MachineStateName"), "el catálogo de controladores usa lo del equipo");
        }

        /// <summary>Tus programas: el nombre con una palabra «de relleno» (Windows, Portable, Final…) ahora coincide con su archivo.</summary>
        static void TestCarpetaDeVersiones2016()
        {
            var builds = new List<OwnBuild>();
            foreach (string f in new[] { @"D:\Versiones\Windows Monitor 1.2.zip", @"D:\Versiones\RelojMonitor_1.9.4.7z", @"D:\Versiones\RelojMonitor_1.9.5.7z",
                                         @"D:\Versiones\Reloj Portable 2.1.7z", @"D:\Versiones\Actualizador_2.0.16.7z", @"D:\Versiones\Rios y Clima del Chaco 1.1 Setup.exe" })
            {
                var b = LocalChecks.ParseBuildName(f);
                Check(b != null, "se reconoce " + Path.GetFileName(f.Replace('\\', Path.DirectorySeparatorChar)));
                if (b != null) builds.Add(b);
            }
            var w = LocalChecks.NewestFor("Windows Monitor", builds);
            Check(w != null && w.Version == "1.2", "«Windows Monitor» coincide con «Windows Monitor 1.2.zip» (antes no: al archivo se le sacaba «Windows»)");
            var r = LocalChecks.NewestFor("Reloj Monitor", builds);
            Check(r != null && r.Version == "1.9.5", "Reloj Monitor: la más nueva de la carpeta");
            var p = LocalChecks.NewestFor("Reloj Portable", builds);
            Check(p != null && p.Version == "2.1", "«Reloj Portable» coincide con su archivo");
            var c = LocalChecks.NewestFor("Ríos y Clima del Chaco", builds);
            Check(c != null && c.Version == "1.1", "con acentos y «Setup» en el archivo");
            Check(LocalChecks.NewestFor("Clima del Chaco", builds) == null && LocalChecks.NewestFor("Reloj Monitor Pro", builds) == null, "un nombre parecido no coincide con otro programa");
        }

        /// <summary>
        /// Instalar.cmd nunca instala una publicación vieja; la entrega que se instala sale en .7z (como la portable) y el aviso de
        /// versiones nuevas la reconoce.
        /// </summary>
        static void TestEntregas2016()
        {
            string raiz = Raiz();
            if (raiz == null) { Check(false, "se encuentra la raíz del proyecto"); return; }
            string cmd = Leer(raiz, "Instalar.cmd");
            Check(cmd.Contains("Publish.ps1\" -SinPruebas -SiHaceFalta"), "Instalar.cmd publica si hace falta");
            Check(!cmd.Contains("if not exist \"%~dp0publish\\Actualizador\\Actualizador.exe\""), "ya no saltea la publicación sólo porque haya una carpeta publish (podía ser de otra versión)");
            string pub = Leer(raiz, "build/Publish.ps1");
            Check(pub.Contains("[switch]$SiHaceFalta") && pub.Contains("Get-MotivoParaPublicar"), "Publish.ps1 -SiHaceFalta");
            Check(pub.IndexOf("Get-MotivoParaPublicar", StringComparison.Ordinal) < pub.IndexOf("Assert-DotNetSdk", StringComparison.Ordinal), "se decide antes de pedir el SDK (si no hace falta publicar, tampoco hace falta el SDK)");
            string comun = Leer(raiz, "build/Comun.ps1");
            Check(comun.Contains("function Get-MotivoParaPublicar") && comun.Contains("ProductVersion") && comun.Contains("LastWriteTimeUtc"), "compara la versión publicada y las fechas del código");
            string emp = Leer(raiz, "build/Empaquetar.ps1");
            Check(emp.Contains("Find-7Zip") && emp.Contains("\"$nombre.7z\"") && emp.Contains("Compress-Archive"), "la entrega que se instala: .7z con 7-Zip, .zip si no está");
            foreach (string s in new[] { "build/EmpaquetarPortable.ps1" })
            {
                byte[] b = File.ReadAllBytes(Path.Combine(raiz, s.Replace('/', Path.DirectorySeparatorChar)));
                Check(b.Length > 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF, s + ": UTF-8 con BOM");
                Check(Balanceado(Encoding.UTF8.GetString(b)), s + ": llaves y paréntesis balanceados");
            }
            foreach (string c in new[] { "Portable.cmd" })
            {
                byte[] b = File.ReadAllBytes(Path.Combine(raiz, c));
                bool ok = true;
                for (int i = 0; i < b.Length; i++) if (b[i] > 127 || (b[i] == '\n' && (i == 0 || b[i - 1] != '\r'))) ok = false;
                Check(ok, c + ": ASCII con CRLF (cmd.exe)");
            }
            string best;
            Eq(AppInfo.Version, SelfUpdate.NewestDelivery(new[] { "Actualizador-" + AppInfo.Version + ".7z", "Actualizador-2.0.15.zip", "Actualizador_2.0.15.7z" }, false, out best), "el aviso de versiones nuevas reconoce la entrega en .7z");
            Eq("Actualizador-" + AppInfo.Version + ".7z", best, "y elige ese archivo");
            Check(Leer(raiz, "README.md").Contains("Actualizador-" + AppInfo.Version) && !Leer(raiz, "README.md").Contains("Actualizador-2.0.9.zip"), "README con los nombres de esta versión");
        }
    }
}
