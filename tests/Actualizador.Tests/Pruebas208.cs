// Pruebas de la 2.0.8: la ventana siempre como administrador, busca sólo al pedirlo, instala sólo lo marcado, «Marcar todo»,
// tema claro/oscuro y los certificados que quedaban abiertos.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static void TestVentana208()
        {
            string raiz = Raiz();
            Check(raiz != null, "se encuentra Actualizador.sln");
            if (raiz == null) return;
            string app = "src/Actualizador.App/";

            // --- Siempre como administrador (sólo la ventana)
            string man = Leer(raiz, app + "app.manifest");
            Check(Regex.IsMatch(man, "requestedExecutionLevel level=\"requireAdministrator\""), "la ventana pide siempre administrador");
            Check(Leer(raiz, "src/Actualizador.Cli/app.manifest").Contains("level=\"asInvoker\""), "la consola no (scripts desatendidos)");
            Check(Leer(raiz, "src/Actualizador.Aviso/app.manifest").Contains("level=\"asInvoker\""), "el aviso no (Programador de tareas)");
            string aviso = Leer(raiz, "src/Actualizador.Aviso/Program.cs");
            Check(Regex.IsMatch(aviso, @"OpenActualizador[\s\S]*UseShellExecute = true"), "el aviso abre la ventana con ShellExecute (muestra el UAC; sin él daría el error 740)");

            // --- No busca al abrir
            string form = Leer(raiz, app + "MainForm.cs");
            Check(!form.Contains("SearchOnStart"), "ya no existe «buscar al abrir»");
            int shown = form.IndexOf("protected override void OnShown", StringComparison.Ordinal);
            int closing = form.IndexOf("protected override void OnFormClosing", StringComparison.Ordinal);
            Check(shown > 0 && closing > shown, "OnShown encontrado");
            if (shown > 0 && closing > shown)
            {
                string onShown = form.Substring(shown, closing - shown);
                var llamadas = Regex.Matches(onShown, @"StartUpdateScan\(\)");
                Eq(1, llamadas.Count, "una sola búsqueda en OnShown");
                Check(Regex.IsMatch(onShown, @"if \(startArgs\.Contains\(""--actualizaciones""\) \|\| startArgs\.Contains\(""--todo""\)\) StartUpdateScan\(\);"),
                      "y sólo si se pidió (aviso de la revisión programada)");
            }
            string opciones = Leer(raiz, app + "OptionsForm.cs");
            Check(!opciones.Contains("searchOnStart") && !opciones.Contains("repeatRounds"), "Opciones ya no ofrece buscar al abrir ni repetir vueltas");

            // --- Instala sólo lo marcado
            string ops = Leer(raiz, app + "MainForm.Operations.cs");
            Check(ops.Contains("updateManager.RepeatUntilDone = false;"), "la ventana no vuelve a buscar e instalar sola lo que no se marcó");
            Check(!ops.Contains("StartMainAction") && !ops.Contains("MarkAll(true)"), "«Actualizar» no marca nada por su cuenta");
            Check(ops.Contains("var selected = Marked();"), "«Actualizar» instala lo marcado");
            Check(Regex.IsMatch(ops, @"foreach \(var u in SnapshotUpdates\(\)\) u\.Selected = false;"), "al terminar queda todo desmarcado");
            string upd = Leer(raiz, app + "MainForm.Updates.cs");
            Check(!upd.Contains("PremarkNew") && !upd.Contains("ShouldPremark"), "nada aparece marcado solo después de buscar");
            string sources = Leer(raiz, app + "MainForm.Sources.cs");
            Check(!sources.Contains("ShouldPremark"), "indicar la página oficial tampoco marca la fila");

            // --- Marcar todo / Desmarcar todo
            string layout = Leer(raiz, app + "MainForm.Layout.cs");
            Check(layout.Contains("\"Buscar actualizaciones\"") && layout.Contains("\"Actualizar\"") && layout.Contains("\"Marcar todo\""), "los tres botones");
            Check(!layout.Contains("Marcar «Para actualizar»"), "ya no está «Marcar «Para actualizar»»");
            Check(upd.Contains("\"Desmarcar todo\"") && upd.Contains("\"Marcar todo\""), "el botón alterna entre marcar y desmarcar");
            Check(Regex.IsMatch(upd, @"btnInstall\.Enabled = !busy && show && k\.Selected > 0;"), "«Actualizar» sólo se habilita con algo marcado");

            // --- Tema
            string tema = Leer(raiz, app + "Theme.cs");
            Check(tema.Contains("enum ThemeMode { Sistema, Claro, Oscuro }"), "tres modos de tema");
            Check(tema.Contains("AppsUseLightTheme"), "«Según Windows» lee el modo de Windows");
            Check(tema.Contains("DwmSetWindowAttribute"), "barra de título oscura");
            string prog = Leer(raiz, app + "Program.cs");
            Check(prog.Contains("d[\"tema\"]") && prog.Contains("Json.Str(o, \"tema\")"), "el tema se guarda en las opciones");
            foreach (string f in new[] { "OptionsForm.cs", "InstallConfirmForm.cs", "HistoryForm.cs" })
                Check(Leer(raiz, app + f).Contains("Theme.Apply(this)"), f + ": usa el tema");
        }

        static void TestCertificados208()
        {
            string raiz = Raiz();
            Check(raiz != null, "se encuentra Actualizador.sln");
            if (raiz == null) return;
            // Cada CreateFromSignedFile(...) sin liberar dejaba un identificador de certificado abierto por archivo verificado.
            int sueltos = 0;
            foreach (string f in Directory.GetFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                string t = File.ReadAllText(f);
                if (Path.GetFileName(f) == "Native.cs") continue;
                sueltos += Regex.Matches(t, @"CreateFromSignedFile\(").Count;
            }
            Eq(0, sueltos, "el firmante se lee sólo con Native.SignerSubject (que libera el certificado)");
            string tmp = Path.Combine(Path.GetTempPath(), "sp-sinfirma-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(tmp, "no es un ejecutable firmado");
            try
            {
                bool lanzo = false;
                try { Native.SignerSubject(tmp); } catch (Exception) { lanzo = true; }
                Check(lanzo, "un archivo sin firma: excepción (los que llaman la convierten en «no se instala»)");
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        static InstalledProgram Prog208(string name, string version, string location, string uninstall)
        {
            return new InstalledProgram { Name = name, Version = version, Publisher = "", InstallLocation = location ?? "", UninstallString = uninstall ?? "",
                                          DisplayIcon = "", Scope = "Equipo", Architecture = "x64" };
        }

        /// <summary>Los «No verificable» de la captura del usuario (06/11/2026).</summary>
        static void TestNoVerificables208()
        {
            // --- Tor Browser: la versión publicada (downloads.json) y la real (tbb_version.json), no la de Firefox ESR.
            var tor = VendorCatalog.BuiltIn.Find(delegate(VendorCheck v) { return v.Id == "tor-browser"; });
            Check(tor != null, "Tor Browser en el catálogo");
            string json = "{\n  \"version\" : \"16.0\",\n  \"downloads\" : {\n    \"win64\" : {\n      \"ALL\" : {\n        \"binary\" : \"https://dist.torproject.org/torbrowser/16.0/tor-browser-windows-x86_64-portable-16.0.exe\"\n      }\n    }\n  }\n}";
            string url;
            Eq("16.0", VendorCatalog.ParsePage(tor, json, out url), "la versión publicada por The Tor Project");
            var torProg = Prog208("Tor Browser", "140.17.0", @"C:\Users\Fede\Desktop\Tor Browser\Browser", "");
            torProg.Unregistered = true;     // copia descomprimida en el Escritorio, encontrada por su acceso directo
            var oldFetch = VendorProvider.FetchForTests;
            var oldRead = VendorProvider.ReadFileForTests;
            try
            {
                VendorProvider.FetchForTests = delegate(string u) { return u == tor.Url ? json : null; };
                VendorProvider.ReadFileForTests = delegate(string path)
                {
                    return path.Replace('\\', '/').EndsWith("Tor Browser/Browser/tbb_version.json")
                        ? "{\"version\":\"15.0.4\",\"architecture\":\"WINNT_x86_64-gcc3\",\"channel\":\"release\",\"locale\":\"es-ES\"}" : null;
                };
                var outTor = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, torProg), outTor, new List<string>());
                var row = outTor.Find(delegate(UpdateCandidate c) { return c.Name == "Tor Browser"; });
                Check(row != null, "la copia portable de Tor Browser se compara");
                if (row != null)
                {
                    Eq("15.0.4", row.InstalledVersion, "instalada: la de tbb_version.json, no 140.17.0");
                    Eq("16.0", row.AvailableVersion, "disponible: la 16.0");
                    Eq(UpdateStatus.Disponible, row.Status, "hay una versión nueva");
                    var all = new List<UpdateCandidate> { row };
                    UpdateManager.MakeManualActionable(all);
                    Check(row.Installable && row.ProviderName == SiteProvider.ProviderId, "se puede marcar: abre la página oficial de descarga");
                }
                // Sin el archivo de versión no se compara (140 contra 16 diría cualquier cosa).
                VendorProvider.ReadFileForTests = delegate(string path) { return null; };
                var outNo = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, torProg), outNo, new List<string>());
                Check(!outNo.Exists(delegate(UpdateCandidate c) { return c.Name == "Tor Browser"; }), "sin tbb_version.json: no se inventa una comparación");
            }
            finally { VendorProvider.FetchForTests = oldFetch; VendorProvider.ReadFileForTests = oldRead; }

            // --- Samsung USB Driver y LaunchBox: sus páginas oficiales.
            var usb = VendorCatalog.BuiltIn.Find(delegate(VendorCheck v) { return v.Id == "samsung-usb-driver"; });
            Eq("1.9.5.0", VendorCatalog.ParsePage(usb, "<h2 class=\"title\">Samsung Android USB Driver for Windows v1.9.5.0</h2>", out url), "Samsung USB Driver: v1.9.5.0");
            Check(usb.Applies(Prog208("Samsung USB Driver for Mobile Phones", "1.9.0.0", "", "")), "Samsung: el nombre de la captura");
            var lb = VendorCatalog.BuiltIn.Find(delegate(VendorCheck v) { return v.Id == "launchbox"; });
            Eq("14.1", VendorCatalog.ParsePage(lb, "<h4>Version 14.2-beta-3 - Released November 2, 2026</h4><h4>Version 14.1 - Released October 29, 2026</h4><h4>Version 14.0.1 - Released August 25, 2026</h4>", out url),
               "LaunchBox: la última estable (no la beta)");
            Check(lb.Applies(Prog208("LaunchBox Big Box", "13.26.0.0", "", "")) && lb.Applies(Prog208("LaunchBox", "13.26.0.0", "", "")), "LaunchBox y Big Box");

            // --- Componentes de Windows: al día con el sistema.
            var ctx = ContextoSimulado(true);
            ctx.Os.Ubr = 9818;
            var ie = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Internet Explorer", InstalledVersion = "11.00.26100.9818", Status = UpdateStatus.NoVerificable };
            var so = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Sistema operativo Microsoft® Windows®", InstalledVersion = "12.0.26100.9818", Status = UpdateStatus.NoVerificable };
            var fix = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Microsoft Windows Application Compatibility Fix Database", Status = UpdateStatus.NoVerificable };
            LocalChecks.Resolve(ctx, new List<UpdateCandidate> { ie, so, fix }, ctx.Programs, new List<string>());
            Check(ie.Status == UpdateStatus.AlDia && so.Status == UpdateStatus.AlDia && fix.Status == UpdateStatus.AlDia, "partes de Windows: se actualizan con Windows Update");
            Check(ie.Detail.Contains("26100.9818"), "con la compilación del sistema: " + ie.Detail);

            // --- Steam: appmanifest_N.acf dice si hay una actualización pendiente.
            string acfOk = "\"AppState\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"name\"\t\t\"Counter-Strike 2\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"LastUpdated\"\t\t\"1793900000\"\n\t\"buildid\"\t\t\"20512345\"\n\t\"TargetBuildID\"\t\t\"0\"\n}";
            string acfPend = acfOk.Replace("\"StateFlags\"\t\t\"4\"", "\"StateFlags\"\t\t\"6\"").Replace("\"TargetBuildID\"\t\t\"0\"", "\"TargetBuildID\"\t\t\"20519999\"");
            Eq(@"D:\SteamLibrary\steamapps", LocalChecks.SteamAppsFolder(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive"), "biblioteca de Steam desde la carpeta del juego");
            var cs2 = Prog208("Counter-Strike 2", "", @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive", "\"C:\\Program Files (x86)\\Steam\\steam.exe\" steam://uninstall/730");
            Eq("730", LocalChecks.SteamAppId(cs2), "número de la app");
            var ctxS = ContextoSimulado(true, cs2);
            var oldLocal = LocalChecks.ReadForTests;
            var oldOpen = ExternalProvider.OpenForTests;
            try
            {
                string acf = acfOk;
                LocalChecks.ReadForTests = delegate(string path) { return path == @"D:\SteamLibrary\steamapps\appmanifest_730.acf" ? acf : null; };
                var r1 = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Counter-Strike 2", InstalledVersion = "Desconocida", Status = UpdateStatus.NoVerificable };
                LocalChecks.Resolve(ctxS, new List<UpdateCandidate> { r1 }, ctxS.Programs, new List<string>());
                Eq(UpdateStatus.AlDia, r1.Status, "Steam: al día");
                Eq("compilación 20512345", r1.InstalledVersion, "con su compilación");
                acf = acfPend;
                var r2 = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Counter-Strike 2", InstalledVersion = "Desconocida", Status = UpdateStatus.NoVerificable };
                LocalChecks.Resolve(ctxS, new List<UpdateCandidate> { r2 }, ctxS.Programs, new List<string>());
                Check(r2.Status == UpdateStatus.Disponible && r2.Installable && UpdateManager.CanInstall(r2), "Steam con actualización pendiente: se puede marcar");
                Eq("compilación 20519999", r2.AvailableVersion, "la compilación que baja Steam");
                string opened = null;
                ExternalProvider.OpenForTests = delegate(string kind, string target) { opened = target; };
                new ExternalProvider().Install(ctxS, new List<UpdateCandidate> { r2 }, delegate(UpdateCandidate c) { });
                Eq("steam://validate/730", opened, "«Actualizar» se lo pide a Steam");
                Eq(UpdateStatus.Pendiente, r2.Status, "queda pendiente hasta la próxima búsqueda");
            }
            finally { LocalChecks.ReadForTests = oldLocal; ExternalProvider.OpenForTests = oldOpen; }

            // --- Programas propios: la carpeta de versiones.
            Eq("riosyclimadelchaco", LocalChecks.KeyOf("Ríos y Clima del Chaco"), "clave sin acentos ni espacios");
            var b1 = LocalChecks.ParseBuildName(@"D:\Entregas\RelojMonitor_1.9.0.7z");
            Check(b1 != null && b1.Key == "relojmonitor" && b1.Version == "1.9.0", "«RelojMonitor_1.9.0.7z»");
            var b2 = LocalChecks.ParseBuildName(@"D:\Entregas\Virtual Pet\Virtual Pet 2.0.2 Setup.exe");
            Check(b2 != null && b2.Key == "virtualpet" && b2.Version == "2.0.2", "«Virtual Pet 2.0.2 Setup.exe»");
            Eq(null, LocalChecks.ParseBuildName(@"D:\Entregas\notas 1.2.txt"), "un .txt no es una versión");
            var oldList = LocalChecks.ListForTests;
            try
            {
                LocalChecks.ListForTests = delegate(string f)
                {
                    return new[] { @"D:\Entregas\RelojMonitor_1.8.6.7z", @"D:\Entregas\RelojMonitor_1.9.0.7z", @"D:\Entregas\Rios_y_Clima_del_Chaco-1.0.zip",
                                   @"D:\Entregas\Virtual Pet\VirtualPet-v2.1.0-setup.exe", @"D:\Entregas\Actualizador_2.0.8.7z" };
                };
                var reloj = Prog208("Reloj Monitor", "1.8.6", @"C:\Program Files\Reloj Monitor", "");
                var rios = Prog208("Ríos y Clima del Chaco", "1.0", "", "");
                var pet = Prog208("Virtual Pet", "2.0.1", "", "");
                var ctxP = ContextoSimulado(true, reloj, rios, pet);
                ctxP.Options.OwnBuildsFolder = @"D:\Entregas";
                var rr = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Reloj Monitor", InstalledVersion = "1.8.6", Status = UpdateStatus.NoVerificable };
                var rc = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Ríos y Clima del Chaco", InstalledVersion = "1.0", Status = UpdateStatus.NoVerificable };
                var rp = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Virtual Pet", InstalledVersion = "2.0.1", Status = UpdateStatus.NoVerificable };
                LocalChecks.Resolve(ctxP, new List<UpdateCandidate> { rr, rc, rp }, ctxP.Programs, new List<string>());
                Check(rr.Status == UpdateStatus.Disponible && rr.AvailableVersion == "1.9.0" && rr.Installable, "Reloj Monitor: la 1.9.0 de la carpeta");
                Eq(UpdateStatus.AlDia, rc.Status, "Ríos y Clima del Chaco 1.0: al día con la carpeta");
                Check(rp.Status == UpdateStatus.Disponible && rp.AvailableVersion == "2.1.0", "Virtual Pet: la 2.1.0 de su subcarpeta");
                string opened = null;
                ExternalProvider.OpenForTests = delegate(string kind, string target) { opened = target; };
                new ExternalProvider().Install(ctxP, new List<UpdateCandidate> { rr }, delegate(UpdateCandidate c) { });
                Eq(@"D:\Entregas\RelojMonitor_1.9.0.7z", opened, "«Actualizar» abre la carpeta con el archivo marcado");
            }
            finally { LocalChecks.ListForTests = oldList; ExternalProvider.OpenForTests = oldOpen; }

            // --- Lo que no tiene fuente de versiones: se explica bien.
            var rep = ProgramHints.For(Prog208("Age of Mythology Extended Edition MULTi9 - ElAmigos versión 2.7.911", "2.7.911", "", ""));
            Check(rep != null && rep.Text.Contains("reempaquetada"), "copias reempaquetadas (ElAmigos)");
            Check(!ProgramHints.AllowsCatalogSearch(Prog208("Re-Volt MULTi5 - ElAmigos versión 1.10", "1.10", "", "")), "y no se buscan en winget");
            var pch = ProgramHints.For(Prog208("PCHelpSoft Driver Updater", "11.1.29574.1514", "", ""));
            Check(pch != null && pch.Text.Contains("desinstalarlo"), "PCHelpSoft: se recomienda desinstalarlo");
            Check(ProgramHints.For(Prog208("ACDSync", "1.0.18.1", "", "")) != null, "ACDSync: componente de ACDSee");
        }
    }
}
