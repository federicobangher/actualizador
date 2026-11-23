// Pruebas de la 2.0.6: dos listas en vez de cuatro tarjetas, más programas encontrados en el catálogo de winget
// (idiomas, paréntesis, nombres parecidos, copias portables) y páginas de códigos de Windows en .NET 8.
using System;
using System.Collections.Generic;
using System.Text;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static UpdateCandidate Fila206(string name, UpdateKind kind, UpdateStatus status, bool recommended, bool installable)
        {
            return new UpdateCandidate { Name = name, Kind = kind, Status = status, Recommended = recommended, Installable = installable, ProviderName = WingetProvider.ProviderId };
        }

        static InstalledProgram Juego206()
        {
            var p = Vc("Counter-Strike 2", "1.0", "x86"); p.Publisher = "Valve";
            p.UninstallString = "\"C:\\Program Files (x86)\\Steam\\steam.exe\" steam://uninstall/730";
            return p;
        }

        static void TestDosListas206()
        {
            var para = Fila206("7-Zip", UpdateKind.Programa, UpdateStatus.Disponible, true, true);
            var opcional = Fila206("BIOS", UpdateKind.Controlador, UpdateStatus.Disponible, false, true);
            var alDia = Fila206("Git", UpdateKind.Programa, UpdateStatus.AlDia, false, false);
            var sinVerificar = Fila206("Programa raro", UpdateKind.Programa, UpdateStatus.NoVerificable, false, false);
            var ignorada = Fila206("Ignorado", UpdateKind.Programa, UpdateStatus.Ignorada, false, false);
            var app = Fila206("Fotos", UpdateKind.Store, UpdateStatus.NoVerificable, false, false);
            var todasStore = Fila206("Todas las apps", UpdateKind.Store, UpdateStatus.Pendiente, true, true);
            todasStore.Identifier = StoreProvider.AllAppsId; todasStore.ProviderName = StoreProvider.ProviderId;
            var todas = new List<UpdateCandidate> { alDia, sinVerificar, opcional, para, ignorada, app, todasStore };

            // --- Cada fila en su lista: «Para actualizar» con las opcionales; «Al día» con lo sin verificar.
            Eq(UpdateView.Pendientes, UpdateGroups.ViewOf(para), "para actualizar → primera lista");
            Eq(UpdateView.Pendientes, UpdateGroups.ViewOf(opcional), "opcional → primera lista (antes, otra tarjeta)");
            Eq(UpdateView.Revisados, UpdateGroups.ViewOf(alDia), "al día → segunda lista");
            Eq(UpdateView.Revisados, UpdateGroups.ViewOf(sinVerificar), "sin verificar → segunda lista (antes, otra tarjeta)");
            Eq(UpdateView.Revisados, UpdateGroups.ViewOf(ignorada), "ignorada → segunda lista");

            // --- Orden: lo de «Actualizar todo» arriba, las opcionales debajo; lo sin verificar antes que lo al día.
            var pend = UpdateGroups.InView(todas, UpdateView.Pendientes);
            Eq(3, pend.Count, "tres pendientes (7-Zip, la Store y la BIOS)");
            Check(pend[pend.Count - 1] == opcional, "la opcional queda al final de la lista");
            Check(pend.IndexOf(para) < pend.IndexOf(opcional) && pend.IndexOf(todasStore) < pend.IndexOf(opcional), "lo recomendado primero");
            var rev = UpdateGroups.InView(todas, UpdateView.Revisados);
            Eq(4, rev.Count, "cuatro revisadas");
            Check(rev[rev.Count - 1] == alDia, "lo al día al final; primero lo que hay que mirar");
            Eq(0, UpdateGroups.InView(null, UpdateView.Pendientes).Count, "sin filas: lista vacía");

            // --- Tarjetas: el número grande es el del título y el botón; la otra cuenta en la etiqueta.
            var k = UpdateGroups.Count(todas);
            Eq("2", UpdateGroups.CardNumber(UpdateView.Pendientes, k), "el mismo número que «Actualizar todo (2)»");
            Eq("Actualizar todo (2)", UpdateGroups.ButtonText(k), "el botón");
            Eq("Para actualizar · 1 opcional", UpdateGroups.CardLabel(UpdateView.Pendientes, k), "etiqueta de la primera");
            Eq("1", UpdateGroups.CardNumber(UpdateView.Revisados, k), "al día: 1");
            Eq("Al día · 3 sin verificar", UpdateGroups.CardLabel(UpdateView.Revisados, k), "etiqueta de la segunda");
            Eq("—", UpdateGroups.CardNumber(UpdateView.Pendientes, null), "antes de buscar: raya");
            Eq("Para actualizar · Opcionales", UpdateGroups.CardLabel(UpdateView.Pendientes, null), "antes de buscar: los nombres");
            Eq("Al día · Sin verificar", UpdateGroups.CardLabel(UpdateView.Revisados, null), "antes de buscar: los nombres");

            // --- Lo que se revisó, por tipo (la fila de toda la Store no es una app).
            Eq(4, k.Programs, "programas revisados");
            Eq(1, k.Drivers, "controladores revisados");
            Eq(1, k.StoreApps, "apps de la Store (sin la fila «Todas las apps»)");
            Eq("Se revisaron 4 programas, 1 controlador y 1 app de la Store.", UpdateGroups.Reviewed(k), "la frase");
            Check(UpdateGroups.Explain(UpdateView.Revisados, k).Contains("Se revisaron 4 programas"), "la explicación de la segunda lista la incluye");
            Check(UpdateGroups.Explain(UpdateView.Pendientes, k).Contains("opcionales"), "la de la primera explica las opcionales");
            Eq("", UpdateGroups.Reviewed(new UpdateGroups.Counts()), "sin nada revisado: vacío");

            // --- La lista que se abre sola.
            Eq(UpdateView.Pendientes, UpdateGroups.DefaultView(k), "hay algo para hacer: la primera");
            Eq(UpdateView.Pendientes, UpdateGroups.DefaultView(UpdateGroups.Count(new[] { opcional, alDia })), "sólo opcionales: también la primera (antes quedaban en otra tarjeta)");
            Eq(UpdateView.Revisados, UpdateGroups.DefaultView(UpdateGroups.Count(new[] { alDia, sinVerificar })), "nada pendiente: la segunda, con todo lo revisado");

            // --- La columna Estado distingue las opcionales, que ahora comparten lista.
            Eq("Disponible", UpdateGroups.StatusText(para), "recomendada");
            Eq("Disponible (opcional)", UpdateGroups.StatusText(opcional), "opcional");
            Eq("No verificable", UpdateGroups.StatusText(sinVerificar), "sin verificar");
            Eq("Al día", UpdateGroups.StatusText(alDia), "al día");
        }

        static void TestNombresParaBuscar206()
        {
            // Idioma del instalador: Thunderbird, Firefox, LibreOffice u Office lo agregan al nombre; winget no.
            Eq("Mozilla Thunderbird", WingetProvider.SearchName("Mozilla Thunderbird (x64 es-AR)"), "sin arquitectura ni idioma");
            Eq("Mozilla Thunderbird", WingetProvider.SearchName("Mozilla Thunderbird 128.3.1esr (x64 es-ES)"), "con versión ESR");
            Check(!ProgramHints.AllowsCatalogSearch(Juego206()), "un juego de Steam no se busca (lo actualiza Steam)");
            var zoom = Vc("Zoom Workplace", "6.2.0", "x64"); zoom.Publisher = "Zoom Video Communications, Inc.";
            Check(ProgramHints.AllowsCatalogSearch(zoom), "un programa con actualizador propio sí se busca (antes quedaba sin verificar)");
            var avast = Vc("Avast Free Antivirus", "24.1", "x64"); avast.Publisher = "Avast Software";
            Check(!ProgramHints.AllowsCatalogSearch(avast), "un antivirus no");
            var otraCuenta = Vc("VLC media player", "3.0.18", "x64"); otraCuenta.Unregistered = true; otraCuenta.Scope = "Usuario PC\\Ana";
            Check(!ProgramHints.AllowsCatalogSearch(otraCuenta), "lo de otra cuenta no");
            Eq("Microsoft 365", WingetProvider.SearchName("Microsoft 365 - es-es"), "Office en español");
            Eq("LibreOffice", WingetProvider.SearchName("LibreOffice 24.8.2.1 (pt-BR)"), "idioma suelto entre paréntesis");
            Eq("Intel Wi-Fi Driver", WingetProvider.SearchName("Intel Wi-Fi Driver"), "«Wi-Fi» no es un idioma");
            Eq("Microsoft Visual Studio Code", WingetProvider.SearchName("Microsoft Visual Studio Code (User)"), "«(User)» dice para quién se instaló");
            Eq("Zoom Workplace", WingetProvider.SearchName("Zoom Workplace (64-bit)"), "arquitectura");
            Eq("Java Update", WingetProvider.SearchName("Java Update"), "palabras comunes no se tocan");
            // Las de siempre siguen igual.
            Eq("7-Zip", WingetProvider.SearchName("7-Zip 26.00 (x64)"), "7-Zip");
            Eq("GOM Player", WingetProvider.SearchName("GOM Player"), "GOM");

            // Búsqueda sin la última palabra, para «winget search --name», que busca el texto dentro del nombre del paquete.
            Eq("Opera", WingetProvider.ShorterQuery("Opera Stable"), "Opera Stable → Opera");
            Eq("Revo Uninstaller", WingetProvider.ShorterQuery("Revo Uninstaller Free"), "una palabra menos");
            Eq(null, WingetProvider.ShorterQuery("GOM"), "una sola palabra: nada que sacar");
            Eq(null, WingetProvider.ShorterQuery("HP Smart"), "muy corta: no se busca «HP»");
        }

        static void TestNombresParecidos206()
        {
            string[] cab = { "Nombre", "Id", "Versión" };
            var opera = Vc("Opera Stable", "114.0.5282.102", "x64"); opera.Publisher = "Opera Software";
            var rows = WingetTable.Parse(TablaWinget(cab, new[] { "Opera", "Opera.Opera", "115.0.5322.68" }, new[] { "Opera GX", "Opera.OperaGX", "115.0.5322.68" }));
            bool aprox;
            var m = WingetProvider.PickSearchMatch(rows, opera, out aprox);
            Check(m != null && m.Id == "Opera.Opera" && aprox, "«Opera Stable» ↔ «Opera» del mismo editor: parecido (" + (m != null ? m.Id : "ninguno") + ")");

            var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = opera.Name, ProviderName = WingetProvider.ProviderId, Status = UpdateStatus.NoVerificable };
            WingetProvider.ApplySearchMatch(c, opera, m, aprox);
            Check(c.Status == UpdateStatus.Disponible && c.Installable && !c.Recommended, "se ofrece entre las opcionales (no se marca sola)");
            Check(c.Detail.Contains("nombre parecido") && c.Detail.Contains("Revisá"), "y lo dice: " + c.Detail);
            Check(((WingetRow)c.ProviderTag).ApproximateName && ((WingetRow)c.ProviderTag).InstallInsteadOfUpgrade, "se instala con «winget install» del paquete encontrado");
            Eq(UpdateGroup.Opcional, UpdateGroups.Of(c), "en la lista, como opcional");

            // Otro editor: no.
            var ajeno = Vc("Opera Stable", "114.0", "x64"); ajeno.Publisher = "Otra empresa";
            Eq(null, WingetProvider.PickSearchMatch(rows, ajeno, out aprox), "otro editor: ninguno");
            // Sin editor registrado: no se adivina con un nombre sólo parecido.
            var sinEditor = Vc("Opera Stable", "114.0", "x64"); sinEditor.Publisher = "";
            Eq(null, WingetProvider.PickSearchMatch(rows, sinEditor, out aprox), "sin editor: ninguno");
            // La diferencia es otra edición del producto: no.
            var viewer = Vc("TightVNC Viewer", "2.8.81", "x64"); viewer.Publisher = "GlavSoft LLC.";
            Eq(null, WingetProvider.PickSearchMatch(WingetTable.Parse(TablaWinget(cab, new[] { "TightVNC", "GlavSoft.TightVNC", "2.8.85" })), viewer, out aprox), "«TightVNC Viewer» no es «TightVNC»");
            var tight = Vc("TightVNC", "2.8.81", "x64"); tight.Publisher = "GlavSoft LLC.";
            Eq(null, WingetProvider.PickSearchMatch(WingetTable.Parse(TablaWinget(cab, new[] { "TightVNC Server", "GlavSoft.TightVNC.Server", "2.8.85" })), tight, out aprox), "ni al revés (Server)");
            // Dos paquetes parecidos del mismo editor: ninguno.
            var revo = Vc("Revo Uninstaller Free", "2.4.5", "x86"); revo.Publisher = "Revo Uninstaller";
            var dosRevo = WingetTable.Parse(TablaWinget(cab, new[] { "Revo Uninstaller", "RevoUninstaller.RevoUninstaller", "2.5.5" }, new[] { "Revo Uninstaller Mobile", "RevoUninstaller.Mobile", "1.0" }));
            m = WingetProvider.PickSearchMatch(dosRevo, revo, out aprox);
            Check(m != null && m.Id == "RevoUninstaller.RevoUninstaller", "«Revo Uninstaller Free» ↔ «Revo Uninstaller»: el otro tiene una palabra de más que no encaja");
            var dosIguales = WingetTable.Parse(TablaWinget(cab, new[] { "Revo Uninstaller", "RevoUninstaller.RevoUninstaller", "2.5.5" }, new[] { "Revo Uninstaller", "RevoUninstaller.Otro", "2.5.5" }));
            Eq(null, WingetProvider.PickSearchMatch(dosIguales, revo, out aprox), "dos paquetes parecidos: ninguno");
            // El nombre exacto manda: con el exacto ambiguo no se prueba un parecido.
            var dosExactos = WingetTable.Parse(TablaWinget(cab, new[] { "Opera Stable", "Opera.Opera", "115.0" }, new[] { "Opera Stable", "Opera.OperaOtro", "115.0" }, new[] { "Opera", "Opera.Opera2", "115.0" }));
            Eq(null, WingetProvider.PickSearchMatch(dosExactos, opera, out aprox), "exacto ambiguo: ninguno, sin probar parecidos");
            // El corte en medio de una palabra no es parecido («Operator» no empieza con la palabra «Opera»).
            var operator1 = Vc("Operator", "1.0", "x64"); operator1.Publisher = "Opera Software";
            Eq(null, WingetProvider.PickSearchMatch(rows, operator1, out aprox), "«Operator» no es «Opera»");
        }

        static void TestBusquedaCompleta206()
        {
            string[] cab = { "Nombre", "Id", "Versión" };
            string[] enc = { "Nombre", "Id", "Versión", "Disponible", "Origen" };
            // winget no reconoce ninguno de los tres (no figuran en «winget list» con origen).
            string list = TablaWinget(enc,
                new[] { "Mozilla Thunderbird (x64 es-AR)", @"ARP\Machine\X64\Mozilla Thunderbird 128.0 (x64 es-AR)", "128.0", "", "" },
                new[] { "Git", "Git.Git", "2.51.0", "", "winget" });
            var consultas = new List<string>();
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            wp.QueryForTests = delegate(string args)
            {
                consultas.Add(args);
                if (args.StartsWith("search --name \"Mozilla Thunderbird\" --source winget"))
                    return Salida(0, TablaWinget(cab, new[] { "Mozilla Thunderbird", "Mozilla.Thunderbird", "132.0.1" }, new[] { "Mozilla Thunderbird Beta", "Mozilla.Thunderbird.Beta", "133.0b1" }));
                if (args.StartsWith("search --name \"Opera\" --source winget")) return Salida(0, TablaWinget(cab, new[] { "Opera", "Opera.Opera", "115.0.5322.68" }));
                if (args.StartsWith("search --name \"VLC media player\" --source winget")) return Salida(0, TablaWinget(cab, new[] { "VLC media player", "VideoLAN.VLC", "3.0.21" }));
                if (args.StartsWith("search")) return Salida(0x8A150014, "No se encontró ningún paquete que coincida con los criterios de entrada.");
                if (args.StartsWith("upgrade")) return Salida(0x8A15002B, "No se encontró ninguna actualización disponible.");
                if (args.StartsWith("list")) return Salida(0, list);
                return Salida(0x8A150014, "");
            };
            var tb = Vc("Mozilla Thunderbird (x64 es-AR)", "128.0", "x64"); tb.Publisher = "Mozilla";
            var opera = Vc("Opera Stable", "114.0.5282.102", "x64"); opera.Publisher = "Opera Software";
            var vlc = Vc("VLC media player", "3.0.18", "x64"); vlc.Publisher = "VideoLAN"; vlc.Unregistered = true; vlc.InstallLocation = @"D:\Portables\VLC";
            var git = Vc("Git", "2.51.0", "x64"); git.Publisher = "The Git Development Community";
            var ctx = ContextoSimulado(true, tb, opera, vlc, git);
            var salida = new List<UpdateCandidate>();
            var notas = new List<string>();
            wp.Scan(ctx, salida, notas);

            var fTb = salida.Find(delegate(UpdateCandidate x) { return x.Name == tb.Name; });
            Check(fTb != null && fTb.Status == UpdateStatus.Disponible && fTb.Identifier == "Mozilla.Thunderbird" && fTb.Recommended,
                  "Thunderbird en español: encontrado con su nombre sin idioma (" + (fTb != null ? fTb.Status + " " + fTb.Identifier : "sin fila") + ")");
            var fOp = salida.Find(delegate(UpdateCandidate x) { return x.Name == opera.Name; });
            Check(fOp != null && fOp.Status == UpdateStatus.Disponible && fOp.Identifier == "Opera.Opera" && !fOp.Recommended,
                  "Opera Stable: encontrado con la búsqueda sin la última palabra, opcional (" + (fOp != null ? fOp.Status + " " + fOp.Identifier : "sin fila") + ")");
            Check(consultas.Exists(delegate(string a) { return a.StartsWith("search --name \"Opera Stable\""); }) &&
                  consultas.Exists(delegate(string a) { return a.StartsWith("search --name \"Opera\" "); }), "primero «Opera Stable», después «Opera»");
            var fVlc = salida.Find(delegate(UpdateCandidate x) { return x.Name == vlc.Name; });
            Check(fVlc != null && fVlc.Status == UpdateStatus.Disponible && fVlc.Installable && !fVlc.Recommended,
                  "VLC portable: ya no «sin verificar»; opcional (" + (fVlc != null ? fVlc.Status + " " + fVlc.Detail : "sin fila") + ")");
            Check(fVlc != null && fVlc.Detail.Contains("copia sin instalador") && fVlc.Detail.Contains(@"D:\Portables\VLC"), "dice que es una copia portable y dónde está");
            Check(fVlc != null && ((WingetRow)fVlc.ProviderTag).PortableCopy, "la instalación no toca la copia portable");
            Eq(0, consultas.FindAll(delegate(string a) { return a.StartsWith("search --name \"Git\""); }).Count, "Git ya lo reconoce winget: no se busca");
            Check(notas.Exists(delegate(string n) { return n.Contains("nombre parecido"); }), "la nota cuenta lo encontrado por un nombre parecido");
        }

        static void TestAlcanceAlInstalar206()
        {
            string[] cab = { "Nombre", "Id", "Versión" };
            var zoom = Vc("Zoom Workplace", "6.2.0", "x64"); zoom.Publisher = "Zoom Video Communications, Inc."; zoom.Scope = "Usuario actual"; zoom.RegistryKey = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ZoomUMX";
            Eq("user", WingetProvider.ScopeOf(zoom), "instalado para el usuario");
            Eq("machine", WingetProvider.ScopeOf(Vc("7-Zip", "24.07", "x64")), "instalado para el equipo");
            var portable = Vc("VLC media player", "3.0.18", "x64"); portable.Unregistered = true;
            Eq("", WingetProvider.ScopeOf(portable), "copia portable: el que elija winget");

            var m = WingetProvider.PickSearchMatch(WingetTable.Parse(TablaWinget(cab, new[] { "Zoom Workplace", "Zoom.Zoom", "6.3.0" })), zoom);
            Check(m != null && m.Id == "Zoom.Zoom", "Zoom encontrado por nombre y editor");
            var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = zoom.Name, ProviderName = WingetProvider.ProviderId, Status = UpdateStatus.NoVerificable };
            WingetProvider.ApplySearchMatch(c, zoom, m);
            Check(c.Status == UpdateStatus.Disponible && c.Recommended, "nombre exacto y editor: para actualizar");

            var pedidos = new List<string>();
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            wp.RunForTests = delegate(string args) { pedidos.Add(args); return Salida(0, "Instalado correctamente\n"); };
            wp.Install(ContextoSimulado(true, zoom), new List<UpdateCandidate> { c }, delegate(UpdateCandidate u) { });
            Check(pedidos.Count > 0 && pedidos[0].StartsWith("install --id \"Zoom.Zoom\"") && pedidos[0].Contains("--scope user"),
                  "elevado, se instala con el mismo alcance (para el usuario), no «para todo el equipo»: " + (pedidos.Count > 0 ? pedidos[0] : "nada"));

            // El manifiesto no declara instalador para ese alcance: se repite sin --scope.
            pedidos.Clear();
            var c2 = new UpdateCandidate { Kind = UpdateKind.Programa, Name = zoom.Name, ProviderName = WingetProvider.ProviderId, Status = UpdateStatus.NoVerificable };
            WingetProvider.ApplySearchMatch(c2, zoom, m);
            wp.RunForTests = delegate(string args)
            {
                pedidos.Add(args);
                return args.Contains("--scope") ? Salida(0x8A150010, "No se encontró ningún instalador aplicable.\n") : Salida(0, "Instalado correctamente\n");
            };
            wp.Install(ContextoSimulado(true, zoom), new List<UpdateCandidate> { c2 }, delegate(UpdateCandidate u) { });
            Check(pedidos.Count >= 2 && pedidos[0].Contains("--scope user") && !pedidos[pedidos.Count - 1].Contains("--scope"), "sin instalador para el alcance: se repite sin --scope (" + string.Join(" | ", pedidos.ToArray()) + ")");
            Eq(UpdateStatus.Instalada, c2.Status, "y se instala");
        }

        static void TestPaginasDeCodigos206()
        {
            // .NET 8 no trae las páginas de Windows sin registrarlas: GetEncoding(850) fallaba y la salida de pnputil quedaba en UTF-8.
            TextEncodings.EnsureRegistered();
            var oem = TextEncodings.ForCodePage(850, 437);
            Eq(850, oem.CodePage, "la página OEM en español");
            Eq("Controlador agregado correctamente: ó ñ", oem.GetString(oem.GetBytes("Controlador agregado correctamente: ó ñ")), "ida y vuelta");
            Eq("ó", oem.GetString(new byte[] { 0xA2 }), "0xA2 es «ó» en la 850 (en UTF-8 era un carácter roto)");
            var ansi = TextEncodings.ForCodePage(1252, 1252);
            Eq("ó", ansi.GetString(new byte[] { 0xF3 }), "0xF3 es «ó» en la 1252");
            Eq(1252, TextEncodings.ForCodePage(0, 1252).CodePage, "sin página conocida: la de respaldo");
            Check(TextEncodings.ForCodePage(-5, -7) != null, "nunca falla");
            Check(TextEncodings.Oem != null && TextEncodings.Ansi != null, "las del sistema (o las de respaldo fuera de Windows)");
            // Un INF «ANSI» con acentos (antes se leía como UTF-8).
            Eq("[Strings]\r\nDesc=\"Cámara\"", CatalogRules.InfText(ansi.GetBytes("[Strings]\r\nDesc=\"Cámara\"")), "INF ANSI");
        }
    }
}
