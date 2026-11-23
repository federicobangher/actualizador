// Pruebas de la 1.7.2: GOM Player con la fuente del fabricante, programas que winget no reconoce y un resumen que no exagera.
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
        static InstalledProgram Gom(string version)
        {
            var p = Vc("GOM Player", version, "x86");
            p.Publisher = "GOM & Company";
            p.InstallLocation = @"C:\Program Files (x86)\GOMPlayer";
            p.DisplayIcon = @"C:\Program Files (x86)\GOMPlayer\GOM.exe";
            return p;
        }

        static void TestGomPlayer172()
        {
            var gom = VendorCatalog.Find("gom-player");
            Check(gom != null && gom.Install == VendorInstall.SignedInstaller, "GOM Player en el catálogo de fabricantes, instalable");
            Eq("gom-player", VendorCatalog.Match(Gom("2.3.121.5401")) == null ? null : VendorCatalog.Match(Gom("2.3.121.5401")).Id, "«GOM Player» coincide");
            foreach (string otro in new[] { "GOM Player Plus", "GOM Audio", "GOM Remote", "GOM Mix 10" })
                Eq(null, VendorCatalog.Match(Vc(otro, "1.0", "x86")), otro + ": otro producto");
            Check(gom.Allows(new Uri(gom.LatestInstallerUrl)), "el instalador sale de la CDN de gomlab.com");
            Check(gom.Allows(new Uri(gom.Url)), "las notas de versión, de gomlab.com");
            Check(!gom.Allows(new Uri("https://gomlab.com.evil.example/GOMPLAYERGLOBALSETUP_NEW.EXE")), "imitación rechazada");
            Check(InstallerRules.SignerAccepted("CN=GOM & Company, O=GOM & Company, L=Seoul, C=KR", gom.Signers), "firmado por GOM & Company");
            Check(InstallerRules.SignerAccepted("CN=GOM&Company, O=GOM&Company, C=KR", gom.Signers), "con el nombre pegado");
            Check(InstallerRules.SignerAccepted("CN=GRETECH CORPORATION, O=GRETECH CORPORATION, C=KR", gom.Signers), "con el nombre anterior de la empresa");
            Check(!InstallerRules.SignerAccepted("CN=Otra Empresa, O=Otra Empresa", gom.Signers), "otro firmante: no");

            // --- Las notas de versión: la más nueva; no las de los scripts ni las de otros productos.
            string page = Sample("gom_release_note.html");
            string url;
            Eq("2.3.123", VendorCatalog.ParsePage(gom, page, out url), "la 2.3.123 del 13/10/2026 (la del aviso de GOM)");
            Eq("2.3.121", VendorCatalog.ParsePage(gom, "<div>V&nbsp;2.3.121&nbsp;20260721</div><div>V 2.3.120 20260623</div>", out url), "con &nbsp;");
            Eq(null, VendorCatalog.ParsePage(gom, "<p>GOM Remote 2.0.4</p><p>Version 2.3.123</p>", out url), "sin el formato «V x.y.z fecha»: nada");

            // --- La grabación: GOM decía «hay una versión nueva» y Actualizador «Al día» (winget tiene la 2.3.114).
            var old = VendorProvider.FetchForTests;
            try
            {
                VendorProvider.FetchForTests = delegate(string u) { return u == gom.Url ? page : null; };
                var ctx = ContextoSimulado(true, Gom("2.3.121.5401"));
                var output = new List<UpdateCandidate>();
                new VendorProvider().Scan(ctx, output, new List<string>());
                var row = output.Find(delegate(UpdateCandidate c) { return c.Name == "GOM Player"; });
                Check(row != null && row.Status == UpdateStatus.Disponible, "GOM Player 2.3.121: disponible la 2.3.123");
                if (row != null)
                {
                    Check(row.Installable && row.Recommended, "se puede marcar y queda marcada");
                    var plan = (VendorPlan)row.ProviderTag;
                    Eq(gom.LatestInstallerUrl, plan.InstallerUrl, "con el instalador oficial");
                    Eq("2.3.123", plan.Latest, "versión que se instala");
                }

                // La fila de winget («Al día» con su catálogo atrasado) desaparece: manda la del fabricante.
                var wg = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "GOM Player", ProviderName = WingetProvider.ProviderId, Status = UpdateStatus.AlDia, Identifier = "GOMLab.GOMPlayer" };
                var all = new List<UpdateCandidate> { wg, row };
                UpdateManager.DeduplicateVendor(all);
                Check(all.Count == 1 && all[0] == row, "una sola fila: la del fabricante");

                var ctxOk = ContextoSimulado(true, Gom("2.3.123.5402"));
                var outOk = new List<UpdateCandidate>();
                new VendorProvider().Scan(ctxOk, outOk, new List<string>());
                Check(outOk.Count == 1 && outOk[0].Status == UpdateStatus.AlDia, "con la 2.3.123.5402 instalada: al día");
            }
            finally { VendorProvider.FetchForTests = old; }
        }

        // ------------------------------------------------------------------ Programas que winget no reconoce

        static void TestWingetNoReconoce172()
        {
            Eq("7-Zip", WingetProvider.SearchName("7-Zip 26.00 (x64)"), "sin versión ni arquitectura");
            Eq("Microsoft Visual C++ 2012 Redistributable", WingetProvider.SearchName("Microsoft Visual C++ 2012 Redistributable (x64) - 11.0.50727"), "el año no es versión; el guion final se va");
            Eq("Python", WingetProvider.SearchName("Python 3.15.2 (64-bit)"), "Python");
            Eq("GOM Player", WingetProvider.SearchName("GOM Player"), "sin cambios");
            Eq("3DMark", WingetProvider.SearchName("3DMark"), "un nombre que empieza con número");
            Eq("ACDSee Pro", WingetProvider.SearchName("ACDSee® Pro x64"), "marca registrada y arquitectura suelta");

            Check(WingetProvider.PublisherMatches("GOM & Company", "GOMLab.GOMPlayer"), "GOM & Company ↔ GOMLab");
            Check(WingetProvider.PublisherMatches("AnyDesk Software GmbH", "AnyDeskSoftwareGmbH.AnyDesk"), "AnyDesk Software GmbH");
            Check(WingetProvider.PublisherMatches("Igor Pavlov", "7zip.7zip") == false, "7-Zip: el editor no coincide con «7zip» (no se adivina)");
            Check(WingetProvider.PublisherMatches("Python Software Foundation", "Python.Python.3.15"), "Python");
            Check(!WingetProvider.PublisherMatches("UL Solutions", "Futuremark.3DMark"), "otro editor: no");
            Check(!WingetProvider.PublisherMatches("", "GOMLab.GOMPlayer"), "sin editor registrado: no");

            // --- Elección de la fila de «winget search».
            string[] cab = { "Nombre", "Id", "Versión" };
            var rows = WingetTable.Parse(TablaWinget(cab,
                new[] { "TightVNC", "GlavSoft.TightVNC", "2.8.85" },
                new[] { "TightVNC Viewer", "GlavSoft.TightVNC.Viewer", "2.8.85" },
                new[] { "RealVNC Viewer", "RealVNC.VNCViewer", "7.13.1" }));
            Eq(3, rows.Count, "tabla de «winget search» leída (3 columnas)");
            var tight = Vc("TightVNC", "2.8.81", "x64"); tight.Publisher = "GlavSoft LLC.";
            var m = WingetProvider.PickSearchMatch(rows, tight);
            Check(m != null && m.Id == "GlavSoft.TightVNC" && m.Version == "2.8.85", "el mismo nombre y editor: GlavSoft.TightVNC");
            var ajeno = Vc("TightVNC", "2.8.81", "x64"); ajeno.Publisher = "Otro";
            Eq(null, WingetProvider.PickSearchMatch(rows, ajeno), "mismo nombre, otro editor: no");
            var dos = WingetTable.Parse(TablaWinget(cab, new[] { "TightVNC", "GlavSoft.TightVNC", "2.8.85" }, new[] { "TightVNC", "GlavSoft.TightVNC.Beta", "2.9.0" }));
            Eq(null, WingetProvider.PickSearchMatch(dos, tight), "dos paquetes posibles: ninguno (mejor no verificable que el equivocado)");

            // --- La fila: más nueva → se instala con «winget install» del paquete encontrado.
            var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "TightVNC", ProviderName = WingetProvider.ProviderId, Status = UpdateStatus.NoVerificable, Identifier = @"ARP\Machine\X64\TightVNC" };
            WingetProvider.ApplySearchMatch(c, tight, m);
            Check(c.Status == UpdateStatus.Disponible && c.Installable && c.Recommended && c.Identifier == "GlavSoft.TightVNC", "TightVNC 2.8.81: disponible la 2.8.85");
            var w = (WingetRow)c.ProviderTag;
            Check(w.InstallInsteadOfUpgrade && w.Available == "2.8.85" && w.Name == "TightVNC", "con el paquete y la versión del catálogo");
            Eq("install", WingetProvider.VerbFor(w, "upgrade"), "«winget install» (upgrade diría que no está instalado)");
            Eq("upgrade", WingetProvider.VerbFor(new WingetRow(), "upgrade"), "los reconocidos siguen con «upgrade»");
            Eq("2.8.85", WingetProvider.PinnedVersion(w), "con la versión fijada");
            var alDia = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "TightVNC", ProviderName = WingetProvider.ProviderId, Status = UpdateStatus.NoVerificable };
            WingetProvider.ApplySearchMatch(alDia, Vc("TightVNC", "2.8.85", "x64"), m);
            Check(alDia.Status == UpdateStatus.AlDia && !alDia.Installable, "con la misma versión: al día (ya no «no verificable»)");

            // --- La búsqueda completa con winget simulado.
            string[] enc = { "Nombre", "Id", "Versión", "Disponible", "Origen" };
            string list = TablaWinget(enc,
                new[] { "TightVNC", @"ARP\Machine\X64\TightVNC", "2.8.81", "", "" },
                new[] { "Counter-Strike 2", @"ARP\Machine\X86\Steam App 730", "", "", "" },
                new[] { "Git", "Git.Git", "2.51.0", "", "winget" });
            var consultas = new List<string>();
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            wp.QueryForTests = delegate(string args)
            {
                consultas.Add(args);
                if (args.StartsWith("search --name \"TightVNC\" --source winget")) return Salida(0, TablaWinget(cab, new[] { "TightVNC", "GlavSoft.TightVNC", "2.8.85" }));
                if (args.StartsWith("search")) return Salida(0x8A150014, "No se encontró ningún paquete que coincida con los criterios de entrada.");
                if (args.StartsWith("upgrade")) return Salida(0x8A15002B, "No se encontró ninguna actualización disponible.");
                if (args.StartsWith("list")) return Salida(0, list);
                return Salida(0x8A150014, "");
            };
            var git = Vc("Git", "2.51.0", "x64"); git.Publisher = "The Git Development Community";
            var cs2 = Vc("Counter-Strike 2", "", "x86"); cs2.Publisher = "Valve"; cs2.UninstallString = "\"C:\\Program Files (x86)\\Steam\\steam.exe\" steam://uninstall/730";
            var ctx = ContextoSimulado(true, tight, git, cs2);
            var salida = new List<UpdateCandidate>();
            var notas = new List<string>();
            wp.Scan(ctx, salida, notas);
            var fila = salida.Find(delegate(UpdateCandidate x) { return x.Name == "TightVNC"; });
            Check(fila != null && fila.Status == UpdateStatus.Disponible && fila.Identifier == "GlavSoft.TightVNC", "TightVNC deja de ser «no verificable»: " + (fila != null ? fila.Status + " " + fila.Detail : "sin fila"));
            Eq(1, consultas.FindAll(delegate(string a) { return a.StartsWith("search"); }).Count, "una búsqueda: ni el juego de Steam (lo actualiza Steam) ni Git (ya reconocido)");
            Check(notas.Exists(delegate(string n) { return n.Contains("por nombre y editor"); }), "la nota lo cuenta");
        }

        // ------------------------------------------------------------------ Resumen que no exagera

    }
}
