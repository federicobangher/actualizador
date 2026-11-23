using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Por qué un programa no se puede verificar y dónde está su sitio oficial.</summary>
    public sealed class ProgramHint
    {
        public string Text = "";
        /// <summary>Página para abrir con «Abrir sitio del fabricante» (validada con <see cref="Fmt.WebUrl"/>); null si no hay.</summary>
        public string Url;
    }

    /// <summary>Lo que un antivirus le informa al Centro de seguridad de Windows (root\SecurityCenter2).</summary>
    public sealed class AntivirusStatus
    {
        public string Name = "";
        public bool Enabled;
        public bool UpToDate;

        /// <summary>productState: bits 12-15 = activado (1); bits 4-7 = firmas desactualizadas (1).</summary>
        public static AntivirusStatus FromProductState(string name, long state)
        {
            var a = new AntivirusStatus();
            a.Name = name ?? "";
            a.Enabled = ((state >> 12) & 0xF) == 1;
            a.UpToDate = ((state >> 4) & 0xF) == 0;
            return a;
        }
    }

    /// <summary>
    /// Para lo que queda «No verificable» después de winget y de los fabricantes: en vez de «revisá
    /// el sitio del fabricante», decir lo que se sabe. Un juego de Steam lo actualiza Steam; Avast se
    /// actualiza solo; un paquete de controladores se compara en «Controladores». Y si el programa
    /// registra su sitio (URLUpdateInfo, URLInfoAbout, HelpLink), queda a un clic.
    /// </summary>
    public static class ProgramHints
    {
        const RegexOptions RX = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        sealed class Rule
        {
            public Regex Name, Location, Publisher;
            public string Text, Url;
        }

        static readonly List<Rule> Launchers = new List<Rule>
        {
            L(@"steam://uninstall/|\\steamapps\\common\\", "Juego de Steam: lo actualiza Steam al abrirlo."),
            L(@"com\.epicgames\.launcher://|\\Epic Games\\(?!Launcher)", "Juego de Epic Games: lo actualiza Epic Games Launcher."),
            L(@"\\GOG Galaxy\\Games\\|\\GOG Games\\", "Juego de GOG: se actualiza desde GOG Galaxy o descargando el instalador nuevo desde tu cuenta de GOG."),
            L(@"uplay://uninstall|\\Ubisoft Game Launcher\\", "Juego de Ubisoft: lo actualiza Ubisoft Connect."),
            L(@"EAInstaller|\\EA Games\\|\\Origin Games\\", "Juego de EA: lo actualiza la aplicación EA."),
            L(@"Battle\.net\\Agent|Blizzard Uninstaller", "Juego de Blizzard: lo actualiza Battle.net."),
            L(@"\\Riot Client\\|RiotClientServices", "Juego de Riot: lo actualiza Riot Client."),
        };

        static readonly Regex AntivirusName = new Regex(@"^(?:Avast|AVG|Avira|Norton|McAfee|Kaspersky|ESET|Bitdefender|Malwarebytes|Panda Dome|Sophos|Trend Micro|F-Secure|Webroot|G DATA)\b", RX);
        const string AntivirusText = "Antivirus: se actualiza solo, programa y firmas. Para forzarlo, usá «Buscar actualizaciones» en el propio antivirus.";

        static readonly List<Rule> SelfUpdating = new List<Rule>
        {
            new Rule { Name = AntivirusName, Text = AntivirusText },
            N(@"^Logi (?:Plugin Service|Download Assistant|Options\+?|Bolt)\b|^Logitech G HUB$",
              "Componente de Logitech: se actualiza con Logi Options+ o G HUB, que traen su propio actualizador."),
            // 2.0.13 (captura del 07/11/2026): DWAgent sale de la lista general para decir de dónde viene y qué conviene.
            N(@"^DWAgent\b",
              "Agente de DWService (acceso remoto): se actualiza solo cada vez que se conecta al servicio, y dwservice.net no publica un número de " +
              "versión con el cual comparar. Si ya no usás DWService para acceder a este equipo, conviene desinstalarlo."),
            N(@"^(?:Discord|Spotify|Zoom(?: Workplace)?|Slack|Dropbox|Google Drive|Microsoft OneDrive|Microsoft Teams(?: classic)?|Steam|Epic Games Launcher|GOG GALAXY|Battle\.net|EA app|Ubisoft Connect|Riot Client|NVIDIA App|NVIDIA GeForce Experience|Opera(?: Stable| GX Stable)?|Telegram Desktop|Signal|WhatsApp|Grammarly(?: for Windows)?|Adobe Creative Cloud|Adobe Genuine Service)\b",
              "Tiene actualizador propio: se actualiza solo o desde su menú, y no publica una fuente de versiones que se pueda consultar."),
            N(@"^(?:µ|μ|u)Torrent\b|^BitTorrent\b",
              "Se actualiza desde el propio programa (Opciones › Buscar actualizaciones). utorrent.com no publica el número de versión, y su instalador " +
              "ofrece programas de terceros, así que no se instala desde acá."),
        };

        /// <summary>
        /// 2.0.8: copias de juegos reempaquetadas por grupos (ElAmigos, FitGirl, DODI…): no las publica el fabricante, no tienen
        /// actualizador ni una fuente de versiones. La versión oficial se actualiza desde la tienda donde se compró.
        /// </summary>
        static readonly Regex Repack = new Regex(@"\b(?:ElAmigos|FitGirl|DODI|KaOsKrew|CODEX|SKIDROW|PLAZA|TENOKE|RUNE|EMPRESS)\b|\bMULTi\d{1,2}\b", RX);
        const string RepackText = "Copia reempaquetada de un juego (el nombre lo dice): no la publica el fabricante, no tiene actualizador ni una fuente de versiones " +
                                  "con la cual comparar. La versión oficial se actualiza desde la tienda donde se compró (Steam, GOG…).";

        /// <summary>
        /// 2.0.16 (captura del 13/11/2026: «Juego Prototype(TM)», «Street FighterTM 6», «Saint Seiya Soldiers Soul»): juegos instalados fuera
        /// de una tienda. Los editores de juegos no publican versiones para comparar; el juego lo actualiza la tienda donde se compró.
        /// Se reconocen por el editor o por estar en una carpeta de juegos; si además están en una tienda, manda la regla del lanzador.
        /// </summary>
        static readonly Regex GamePublisher = new Regex(@"^(?:CAPCOM|BANDAI\s*NAMCO|NAMCO\s*BANDAI|Activision|SEGA|SQUARE\s*ENIX|KONAMI|Ubisoft|Electronic Arts|Bethesda|" +
            @"Warner Bros|2K(?: Games)?|THQ(?: Nordic)?|Deep Silver|KOEI\s*TECMO|CD PROJEKT|Paradox Interactive|Rockstar Games|Focus (?:Home )?Entertainment|Codemasters|" +
            @"Mojang|Riot Games|Valve|Epic Games(?! Inc\.? *Launcher)|FromSoftware|Sony Interactive|Xbox Game Studios|Devolver Digital|Team17|Frontier Developments)\b", RX);
        static readonly Regex GameFolder = new Regex(@"\\(?:Games|Juegos|Jogos|Spiele|Jeux)\\", RX);
        const string GameText = "Juego instalado fuera de una tienda: los editores de juegos no publican sus versiones en una página que se pueda comparar. " +
                                "Si lo compraste en una tienda (Steam, Epic Games, GOG, EA, Ubisoft…), se actualiza desde ahí; si es una copia de otro origen, no tiene " +
                                "actualizador ni una fuente de versiones.";

        /// <summary>¿Es un juego? (por su editor o por estar instalado en una carpeta de juegos).</summary>
        public static bool IsGame(InstalledProgram p)
        {
            if (p == null) return false;
            string where = (p.InstallLocation ?? "") + " | " + (p.UninstallString ?? "") + " | " + (p.DisplayIcon ?? "");
            return GamePublisher.IsMatch((p.Publisher ?? "").Trim()) || GameFolder.IsMatch(where);
        }

        /// <summary>2.0.8: lo que no tiene fuente de versiones pero se sabe cómo se actualiza (o qué conviene hacer).</summary>
        static readonly List<Rule> Known = new List<Rule>
        {
            N(@"^ACDSync\b", "Componente de ACDSee (sincroniza con ACDSee Mobile): se actualiza junto con ACDSee (Ayuda › Buscar actualizaciones)."),
            // 2.0.13 (captura del 07/11/2026): los que quedaban sin explicación propia.
            NP(@"^Device Detector$", @"\bACD\b|ACD Systems", "Componente de ACDSee (detecta cámaras, memorias y escáneres al conectarlos): se actualiza junto con ACDSee " +
               "(Ayuda › Buscar actualizaciones). ACD Systems no lo publica por separado."),
            N(@"^USB Disk Security\b", "Su sitio (zbshareware.com) ahora lleva a privatedlp.com, que ofrece otro producto (prevención de fuga de datos) y no publica " +
              "versiones del USB Disk Security clásico: no hay una versión con la cual comparar. La protección en tiempo real de Windows ya analiza lo que " +
              "se abre desde una memoria USB."),
            N(@"^EZVIZ Studio\b", "EZVIZ no publica la versión de EZVIZ Studio en una página que se pueda leer (su sitio rechaza las consultas automáticas). " +
              "Para actualizarlo, bajá la última de ezviz.com (Soporte › Descargas › EZVIZ Studio) e instalala."),
            N(@"^Logi Plugin Service\b", "Componente de Logi Options+ (los complementos de Actions Ring y de la MX Creative Console): se actualiza solo junto con " +
              "Logi Options+, que Actualizador compara en su propia fila. Logitech no publica la versión de este componente por separado."),
            N(@"^Logi Download Assistant\b", "Asistente de Logitech que ofrece bajar el software de un dispositivo Logitech al conectarlo; se actualiza solo y Logitech " +
              "no publica su versión. Si no usás software de Logitech (Logi Options+, G HUB), se puede desinstalar."),
            N(@"^ViewSonic\b.*\b(?:Signed Files|INF|Monitor Driver)\b", "Archivos del controlador (INF) de un monitor ViewSonic: sólo le informan a Windows el nombre y " +
              "las resoluciones del monitor. No es un programa ni tiene versiones nuevas que instalar; los controladores de monitor se comparan en «Controladores»."),
            N(@"^PCHelpSoft Driver Updater\b", "Actualizador de controladores de terceros: este programa ya compara los controladores con Windows Update y el Catálogo de " +
              "Microsoft Update. Varios antivirus lo marcan como programa potencialmente no deseado: conviene desinstalarlo."),
            N(@"^Tor Browser\b", "Tor Browser se actualiza solo: abrilo y, cuando avise, tocá «Reiniciar para actualizar». No se encontró su archivo tbb_version.json para " +
              "comparar (su ejecutable dice la versión de Firefox ESR, no la de Tor Browser)."),
            N(@"^Counter-Strike 1\.6\b", "Counter-Strike 1.6 fuera de Steam: no tiene actualizador ni una fuente de versiones. La versión oficial está en Steam."),
        };

        static readonly List<Rule> Drivers = new List<Rule>
        {
            D(@"^NVIDIA\b.*(?:Controlador|Driver|Treiber|Pilote)", "https://www.nvidia.com/Download/index.aspx"),
            D(@"^AMD (?:Software|Chipset|Radeon)|^AMD\b.*(?:Controlador|Driver)", "https://www.amd.com/en/support/download/drivers.html"),
            D(@"^Intel(?:\(R\))?\b.*(?:Controlador|Driver|Graphics|Chipset|Management Engine|Wireless|Bluetooth|Rapid Storage|Serial IO)", "https://www.intel.com/content/www/us/en/support/detect.html"),
            D(@"^Samsung USB Driver", "https://developer.samsung.com/android-usb-driver"),
            D(@"^Realtek\b.*(?:Controlador|Driver|Audio|Ethernet|Card Reader|Network)", null),
        };

        static Rule L(string location, string text) { return new Rule { Location = new Regex(location, RX), Text = text }; }
        static Rule N(string name, string text) { return new Rule { Name = new Regex(name, RX), Text = text }; }
        /// <summary>Por nombre y editor (o carpeta): «Device Detector» es de ACDSee, pero CyberLink tiene otro con el mismo nombre.</summary>
        static Rule NP(string name, string publisherOrFolder, string text) { return new Rule { Name = new Regex(name, RX), Publisher = new Regex(publisherOrFolder, RX), Text = text }; }

        static bool Matches(Rule r, InstalledProgram p, string name)
        {
            if (!r.Name.IsMatch(name)) return false;
            if (r.Publisher == null) return true;
            return r.Publisher.IsMatch(p.Publisher ?? "") || r.Publisher.IsMatch((p.InstallLocation ?? "") + " | " + (p.UninstallString ?? "") + " | " + (p.DisplayIcon ?? ""));
        }

        static Rule D(string name, string url)
        {
            return new Rule
            {
                Name = new Regex(name, RX),
                Url = url,
                Text = "Paquete de controladores: Windows Update los compara en «Controladores». El fabricante puede tener versiones más nuevas en su sitio" +
                       (url != null ? " (clic derecho › «Abrir sitio del fabricante»)." : ".")
            };
        }

        /// <summary>Lo que se sabe de un programa que no se pudo verificar; null si no hay nada que agregar.</summary>
        public static ProgramHint For(InstalledProgram p)
        {
            if (p == null || string.IsNullOrEmpty(p.Name)) return null;
            string name = p.Name.Trim();
            string where = (p.UninstallString ?? "") + " | " + (p.InstallLocation ?? "");
            string site = SiteOf(p);
            if (OfficeC2R.IsOfficeMsi(p))
                return new ProgramHint { Text = OfficeMsiText(p), Url = "https://learn.microsoft.com/officeupdates/office-updates-msi" };
            foreach (var r in Launchers)
                if (r.Location.IsMatch(where)) return new ProgramHint { Text = r.Text, Url = site };
            if (Repack.IsMatch(name)) return new ProgramHint { Text = RepackText, Url = null };
            // Los conocidos primero: dicen algo más preciso que «tiene actualizador propio» (Logi Plugin Service, DWAgent).
            foreach (var r in Known)
                if (Matches(r, p, name)) return new ProgramHint { Text = r.Text, Url = site };
            foreach (var r in SelfUpdating)
                if (r.Name.IsMatch(name)) return new ProgramHint { Text = r.Text, Url = site };
            foreach (var r in Drivers)
                if (r.Name.IsMatch(name)) return new ProgramHint { Text = r.Text, Url = Fmt.WebUrl(r.Url) ?? site };
            if (p.Unregistered) return new ProgramHint { Text = UnregisteredText(p), Url = site };
            if (IsGame(p)) return new ProgramHint { Text = GameText, Url = site };
            return null;
        }

        /// <summary>
        /// ¿Se lo puede buscar en el catálogo de winget por su nombre? 2.0.6: antes cualquier explicación lo dejaba afuera, y los
        /// programas con actualizador propio (Opera, Zoom, Discord, Telegram…) y las copias portables (VLC o GOM copiados a una
        /// carpeta) quedaban «sin verificar» para siempre aunque winget los tuviera. Siguen afuera los juegos (los actualiza su
        /// tienda), los antivirus y los componentes de Logitech (se actualizan solos), los paquetes de controladores (se comparan
        /// en «Controladores»), Office MSI (Windows Update) y lo instalado por otra cuenta (no se puede tocar desde esta).
        /// </summary>
        public static bool AllowsCatalogSearch(InstalledProgram p)
        {
            if (p == null || string.IsNullOrEmpty(p.Name)) return false;
            string name = p.Name.Trim();
            string where = (p.UninstallString ?? "") + " | " + (p.InstallLocation ?? "");
            if (OfficeC2R.IsOfficeMsi(p)) return false;
            foreach (var r in Launchers) if (r.Location.IsMatch(where)) return false;
            if (AntivirusName.IsMatch(name) || Repack.IsMatch(name)) return false;
            if (SelfUpdating.Count > 1 && SelfUpdating[1].Name.IsMatch(name)) return false;     // Logitech
            foreach (var r in Drivers) if (r.Name.IsMatch(name)) return false;
            if (p.Unregistered && (p.Scope ?? "").StartsWith("Usuario ", StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>Programa encontrado por su acceso directo, sin entrada de desinstalación.</summary>
        public static string UnregisteredText(InstalledProgram p)
        {
            string where = p != null && !string.IsNullOrEmpty(p.InstallLocation) ? " (" + p.InstallLocation + ")" : "";
            if (p != null && (p.Scope ?? "").StartsWith("Usuario ", StringComparison.Ordinal))
                return "Lo instaló otra cuenta del equipo" + where + ", que no tiene la sesión abierta: su registro no se puede leer y winget no lo ve. " +
                       "Para actualizarlo, abrí Actualizador desde esa cuenta.";
            return "No tiene entrada de desinstalación en Windows" + where + ": es una versión portable o copiada a mano, y se encontró por su acceso directo. " +
                   "winget no puede actualizar una copia así: instalalo desde su sitio oficial (o reemplazá la carpeta con la versión portable nueva).";
        }

        /// <summary>Office instalado con MSI: cómo se actualiza y, si corresponde, que ya no tiene soporte.</summary>
        public static string OfficeMsiText(InstalledProgram p)
        {
            string v = (p != null ? p.Version ?? "" : "").Trim();
            string support = v.StartsWith("16.", StringComparison.Ordinal) ? " Office 2016 dejó de recibir parches el 14/10/2025."
                           : v.StartsWith("15.", StringComparison.Ordinal) ? " Office 2013 dejó de recibir parches el 11/04/2023."
                           : v.StartsWith("14.", StringComparison.Ordinal) ? " Office 2010 dejó de recibir parches el 13/10/2020."
                           : v.StartsWith("12.", StringComparison.Ordinal) ? " Office 2007 dejó de recibir parches el 10/10/2017." : "";
            return "Office instalado con Windows Installer (MSI): no tiene actualizador propio, sus parches llegan por Microsoft Update y se listan en «Windows Update» " +
                   "(si no aparece ninguno, Actualizador ofrece activar «Recibir actualizaciones de otros productos de Microsoft»)." + support;
        }

        /// <summary>
        /// Sitio que registra el propio programa: primero el de actualizaciones (URLUpdateInfo),
        /// después el del producto (URLInfoAbout) y por último el de ayuda (HelpLink). Sólo http/https.
        /// </summary>
        public static string SiteOf(InstalledProgram p)
        {
            if (p == null) return null;
            return Fmt.WebUrl(p.UpdateUrl) ?? Fmt.WebUrl(p.InfoUrl) ?? Fmt.WebUrl(p.HelpLink);
        }

        /// <summary>Búsqueda web del sitio oficial, para lo que no registra ninguno.</summary>
        public static string SearchUrl(string name, string publisher)
        {
            string q = (name ?? "").Trim();
            string pub = (publisher ?? "").Trim();
            if (pub.Length > 0 && q.IndexOf(pub, StringComparison.OrdinalIgnoreCase) < 0) q += " " + pub;
            return "https://www.google.com/search?q=" + Uri.EscapeDataString(Fmt.Truncate(q, 120) + " sitio oficial descargar");
        }

        /// <summary>
        /// Completa el detalle y el sitio de las filas de programas «No verificable» que no vienen del
        /// fabricante. Los textos de winget («No figura en los repositorios de winget…») se reemplazan;
        /// los demás (Windows sin winget) se conservan y se les agrega el sitio.
        /// </summary>
        public static void Annotate(List<UpdateCandidate> all, List<InstalledProgram> programs)
        {
            Annotate(all, programs, null);
        }

        /// <param name="antivirus">Lo que informa el Centro de seguridad de Windows (puede ser null).</param>
        public static void Annotate(List<UpdateCandidate> all, List<InstalledProgram> programs, List<AntivirusStatus> antivirus)
        {
            if (all == null) return;
            foreach (var c in all)
            {
                if (c == null || c.Kind != UpdateKind.Programa || c.Status != UpdateStatus.NoVerificable || c.ProviderName == VendorProvider.ProviderId) continue;
                // 2.0.30 (captura del usuario, 23/11/2026 12:38: Office Hub para todos los usuarios decía «No figura en winget…», que es falso):
                // una fila «para todos los usuarios» ya trae su explicación; no se reemplaza por la genérica.
                var wr = c.ProviderTag as WingetRow;
                if (wr != null && wr.MachineScope) continue;
                var p = Find(programs, c.Name);
                var hint = For(p);
                string site = SiteOf(p);
                bool fromWinget = c.ProviderName == WingetProvider.ProviderId;
                var av = AntivirusName.IsMatch((c.Name ?? "").Trim()) ? MatchAntivirus(c.Name, antivirus) : null;
                if (av != null)
                {
                    // El propio antivirus le informa a Windows si está activado y al día: es lo mismo que
                    // muestra «Seguridad de Windows». Mejor dato que «No verificable».
                    c.InfoUrl = hint != null ? hint.Url : site;
                    c.Source = "Centro de seguridad de Windows";
                    if (av.Enabled && av.UpToDate)
                    {
                        c.Status = UpdateStatus.AlDia;
                        c.AvailableVersion = "";
                        c.Detail = "Según el Centro de seguridad de Windows, " + av.Name + " está activado y al día (lo informa el propio antivirus, " +
                                   "igual que en «Seguridad de Windows»). Se actualiza solo, programa y firmas.";
                    }
                    else
                    {
                        var problems = new List<string>();
                        if (!av.Enabled) problems.Add("DESACTIVADO");
                        if (!av.UpToDate) problems.Add("con las firmas DESACTUALIZADAS");
                        c.Detail = "El Centro de seguridad de Windows informa " + av.Name + " " + string.Join(" y ", problems.ToArray()) + ": " +
                                   (av.Enabled ? "abrilo y tocá «Actualizar»." : "abrilo y activá la protección.");
                    }
                    continue;
                }
                if (hint != null)
                {
                    c.Detail = hint.Text;
                    c.InfoUrl = hint.Url;
                }
                else if (fromWinget)
                {
                    string why = (c.Identifier ?? "").StartsWith("registro:", StringComparison.Ordinal) ? "Instalado, pero winget no lo informa" : "No figura en winget";
                    c.Detail = why + " ni está en el catálogo de fabricantes de Actualizador. " +
                               (site != null ? "Sitio que registra el programa: " + new Uri(site).Host + " (clic derecho › «Abrir sitio del fabricante»)."
                                             : "El programa no registra su sitio: clic derecho › «Buscar el sitio oficial en la web».");
                    c.InfoUrl = site;
                }
                else if (site != null)
                {
                    c.Detail = (c.Detail ?? "").TrimEnd() + " Sitio que registra el programa: " + new Uri(site).Host + ".";
                    c.InfoUrl = site;
                }
            }
            // 2.0.13: lo que sigue sin verificar y sin versión («Desconocida»: DWAgent, EZVIZ Studio, USB Disk Security) muestra al menos la
            // de su ejecutable principal, para saber qué hay instalado.
            foreach (var c in all)
            {
                if (c == null || c.Kind != UpdateKind.Programa || c.Status != UpdateStatus.NoVerificable || VersionUtil.Parse(c.InstalledVersion) != null) continue;
                var p = Find(programs, c.Name);
                string exe = ProgramRules.MainExe(p);
                string v = exe.Length > 0 ? (ExeVersionForTests != null ? ExeVersionForTests(exe) : ExeProductVersion(exe)) : null;
                if (VersionUtil.Parse(v) == null) continue;
                c.InstalledVersion = VersionUtil.Normalize(v);
                c.Detail = (c.Detail ?? "").TrimEnd() + " La versión instalada se leyó de su ejecutable (" + System.IO.Path.GetFileName(exe) + "): Windows no la registra.";
                c.Detail = c.Detail.TrimStart();
            }
        }

        /// <summary>Sólo para las pruebas: la versión del ejecutable principal.</summary>
        internal static Func<string, string> ExeVersionForTests;

        static string ExeProductVersion(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return null;
                var fi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                string v = (fi.ProductVersion ?? "").Trim();
                // «1.2.3.4 (build xyz)», «1, 2, 3, 4»: sólo el número.
                var m = Regex.Match(v.Replace(", ", "."), @"^\d+(?:\.\d+){1,3}");
                if (m.Success && m.Value != "0.0.0.0" && m.Value != "1.0.0.0") return m.Value;
                m = Regex.Match((fi.FileVersion ?? "").Replace(", ", "."), @"^\d+(?:\.\d+){1,3}");
                return m.Success && m.Value != "0.0.0.0" && m.Value != "1.0.0.0" ? m.Value : null;
            }
            catch (Exception ex) { Log.Quiet("Versión del ejecutable", ex); return null; }
        }

        /// <summary>
        /// El antivirus del Centro de seguridad que corresponde a un programa instalado: por la marca
        /// (primera palabra), porque los nombres no coinciden («Avast Free Antivirus» en Programas,
        /// «Avast Antivirus» en el Centro de seguridad).
        /// </summary>
        public static AntivirusStatus MatchAntivirus(string programName, List<AntivirusStatus> antivirus)
        {
            if (antivirus == null || string.IsNullOrEmpty(programName)) return null;
            string brand = FirstWord(programName);
            if (brand.Length < 3) return null;
            foreach (var a in antivirus)
                if (a != null && FirstWord(a.Name) == brand) return a;
            return null;
        }

        static string FirstWord(string s)
        {
            var m = Regex.Match(s ?? "", @"[\p{L}\p{N}]+");
            return m.Success ? m.Value.ToLowerInvariant() : "";
        }

        static InstalledProgram Find(List<InstalledProgram> programs, string name)
        {
            if (programs == null || string.IsNullOrEmpty(name)) return null;
            string n = VendorProvider.BaseName(name);
            bool truncated = name.TrimEnd().EndsWith("…") || name.TrimEnd().EndsWith("...");
            InstalledProgram prefix = null;
            foreach (var p in programs)
            {
                if (p == null || p.Name == null) continue;
                string pn = p.Name.Trim();
                if (pn.Equals(n, StringComparison.OrdinalIgnoreCase)) return p;
                if (prefix == null && n.Length >= 8 && pn.StartsWith(n, StringComparison.OrdinalIgnoreCase) && (truncated || pn.Length > n.Length)) prefix = p;
            }
            return prefix;
        }
    }
}
