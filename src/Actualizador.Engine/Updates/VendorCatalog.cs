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
    /// <summary>Un instalador publicado por el fabricante, con el hash que el fabricante publica al lado.</summary>
    public sealed class VendorDownload
    {
        public string Version = "", Arch = "", Id = "", Sha256 = "";
    }

    /// <summary>
    /// Instalador fijo de un producto que ya no se actualiza (Visual C++ 2008–2013): la dirección oficial de
    /// Microsoft y su SHA-256, los mismos que publica el catálogo de winget. El archivo no cambia nunca.
    /// </summary>
    public sealed class FixedInstaller
    {
        public string Url = "", Sha256 = "";
    }

    /// <summary>Cómo se lee la última versión en la fuente oficial.</summary>
    public enum VendorFeed
    {
        /// <summary>Página de descargas de AIMP, con el SHA-256 de cada instalador.</summary>
        AimpPage,
        /// <summary>Página HTML: expresión con el grupo «v» (versión) y, opcional, «url» (instalador).</summary>
        PageRegex,
        /// <summary>Appcast de Sparkle/WinSparkle (XML que consulta el actualizador del propio programa).</summary>
        SparkleAppcast,
        /// <summary>Notas de versión de Adobe: la versión para Windows y los parches publicados.</summary>
        AdobeReleaseNotes,
        /// <summary>JSON de versiones por canal de Microsoft 365 / Office «Hacer clic y ejecutar».</summary>
        OfficeReleases,
        /// <summary>Producto que ya no se actualiza: la última versión publicada es conocida.</summary>
        FinalVersion,
        /// <summary>
        /// Última versión estable que el fabricante publica en SU proyecto de GitHub (dirección fija en el
        /// catálogo, no la que registra el programa), con el firmante esperado fijo: Mullvad Browser.
        /// </summary>
        GitHubRelease,
        /// <summary>
        /// 2.0.13: controlador de gráficos de NVIDIA: la última versión que publica NVIDIA para la placa instalada
        /// (<see cref="NvidiaDriver"/>).
        /// </summary>
        NvidiaDriver,
        /// <summary>
        /// 2.0.15: la página de descargas no dice el número de versión, pero enlaza una dirección fija que siempre trae el
        /// instalador de la última (Fortinet). La versión sale del nombre final del archivo o de sus recursos
        /// (<see cref="OfficialInstaller"/>), y ese mismo archivo es el que se instala.
        /// </summary>
        OfficialInstaller,
        /// <summary>2.0.41: el archivo de versiones de Tor Browser de la plataforma instalada (<see cref="Updates.TorBrowser"/>).</summary>
        TorBrowser
    }

    /// <summary>Cómo se instala la versión nueva.</summary>
    public enum VendorInstall
    {
        /// <summary>Sólo se compara: se indica cómo actualizarlo a mano.</summary>
        None,
        /// <summary>Instalador de la página verificado con el SHA-256 publicado al lado (AIMP).</summary>
        PageHash,
        /// <summary>Instalador oficial verificado por su firma Authenticode del fabricante esperado.</summary>
        SignedInstaller,
        /// <summary>Parche MSP oficial de Adobe, firmado por Adobe, aplicado con msiexec /p.</summary>
        AdobeMsp,
        /// <summary>El actualizador propio de Office (OfficeC2RClient.exe), firmado por Microsoft.</summary>
        OfficeClickToRun,
        /// <summary>
        /// 2.0.19: lo mismo que hace el arranque starter4j del programa (TLauncher): baja los archivos de la versión nueva verificando el
        /// SHA-1 que publica su configuración oficial y anota la versión en «starter.json» (<see cref="Updates.Starter4j"/>).
        /// </summary>
        Starter4j,
        /// <summary>
        /// 2.0.41: Tor Browser (portable): su instalador oficial verificado (firma de The Tor Project y SHA-256 publicado) se instala en
        /// silencio en la misma carpeta y se conservan los datos del usuario (<see cref="Updates.TorBrowser"/>).
        /// </summary>
        TorBrowser
    }

    /// <summary>Un programa cuya última versión se consulta en la fuente oficial del fabricante.</summary>
    public sealed class VendorCheck
    {
        public string Id;             // vivaldi
        public string Vendor;         // texto de la columna Origen
        public string Product;        // nombre corto para los mensajes («Vivaldi»)
        public Regex Name;            // nombre en «Programas instalados»
        public Func<InstalledProgram, bool> Extra;   // condición adicional (canal de Adobe, Office C2R…)
        public string Url;            // fuente oficial con la versión
        public string InfoUrl;        // página para personas (se abre con «Abrir sitio del fabricante»)
        public string Domain;         // único dominio aceptado (y sus subdominios), también para la descarga
        public string[] AlsoDomains = new string[0];   // otros dominios del mismo servicio (redirecciones de descarga de GitHub)
        public VendorFeed Feed;
        public VendorInstall Install;
        public Regex Version;         // PageRegex: grupo «v» y, opcional, «url»; AimpPage: respaldo
        public string FinalVersion;   // FinalVersion
        public string ExeName;        // ejecutable principal (versión real y arquitectura)
        public string[] Processes = new string[0];   // procesos que tienen que estar cerrados para instalar
        public string SilentArgs;     // interruptores del instalador en silencio (null: se detectan por el tipo de instalador)
        public string MachineArgs;    // interruptores extra si el programa está instalado para todo el equipo
        public string[] Signers = new string[0];     // organizaciones aceptadas como firmante del instalador
        public bool Recommended = true;              // «Marcar disponibles» la incluye
        public bool OneRowForAll;     // una sola fila para todas las entradas que coinciden (Office)
        /// <summary>Cada versión se instala en su carpeta, al lado de la anterior (Cura): la vieja no se «actualiza» si la nueva ya está.</summary>
        public bool SideBySide;
        public bool InstallerHasProductVersion;      // la versión del .exe descargado es la del producto (se rechaza una más vieja)
        /// <summary>Vino del catálogo de fuentes oficiales (JSON), no del programa: se instala sólo con el firmante de lo instalado.</summary>
        public bool FromCatalogFile;
        public string HowToUpdate;    // cómo actualizarlo a mano si no se puede desde acá
        /// <summary>PageRegex sin enlace en la página: dirección fija del fabricante que siempre trae la última (GOM Player).</summary>
        public string LatestInstallerUrl;
        public string Note;           // aclaración fija para la columna Detalle
        /// <summary>
        /// 2.0.8: archivos (relativos a la carpeta del programa) con la versión REAL del producto, que manda sobre la del registro y
        /// la del ejecutable. Tor Browser se registra con la de Firefox ESR (140.17.0) y su versión (15.0.x) está en tbb_version.json.
        /// Si ninguno se puede leer, la fila no se compara (140 contra 16 diría «más nueva» o «desactualizada» sin serlo).
        /// </summary>
        public string[] VersionFiles;
        /// <summary>FinalVersion con instalador: arquitectura («x64», «x86») → instalador oficial fijo.</summary>
        public readonly Dictionary<string, FixedInstaller> FinalInstallers = new Dictionary<string, FixedInstaller>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// 2.0.12: instaladores enlazados en la página oficial, por arquitectura («x64», «x86», «arm64» y, para lo instalado con
        /// Windows Installer, «x64-msi», «x86-msi»): expresión sobre el NOMBRE del archivo. El archivo tiene que llevar la versión
        /// publicada (7-Zip: «7z2605-x64.exe» es la 26.05): un enlace a otra versión no sirve.
        /// </summary>
        public readonly Dictionary<string, Regex> ArchLinks = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// 2.0.12: direcciones (prefijos exactos) desde donde también se acepta la descarga del instalador, aunque no sean del
        /// dominio de la página: la página oficial de 7-Zip enlaza sus instaladores en «github.com/ip7z/7zip/releases/download/».
        /// Sólo ese proyecto, no todo github.com.
        /// </summary>
        public string[] DownloadPrefixes = new string[0];
        /// <summary>
        /// 2.0.12: proyecto de GitHub del AUTOR donde está publicado el mismo instalador que enlaza la página oficial, con su SHA-256
        /// («ip7z/7zip»). La versión la decide siempre la página oficial; de GitHub sólo se lee el SHA-256 de ese archivo.
        /// </summary>
        public string HashRepo;
        /// <summary>
        /// 2.0.12: el autor no firma sus instaladores (7-Zip). Se instala SIN firma digital sólo si el archivo no tiene ninguna firma,
        /// su SHA-256 coincide con el publicado por el autor (<see cref="HashRepo"/>) y el instalador dice ser de este producto.
        /// Una firma presente pero inválida o revocada sigue rechazándose.
        /// </summary>
        public bool UnsignedWithPublishedHash;
        /// <summary>
        /// 2.0.13: el nombre en «Aplicaciones» lleva la versión y cambia al actualizar («NVIDIA Controlador de gráficos 475.14»): después
        /// de instalar, la versión se relee de la entrada que coincide con <see cref="Name"/>, no de la que tenía el nombre viejo.
        /// </summary>
        public bool NameHasVersion;
        /// <summary>
        /// 2.0.13: el instalador se ejecuta siempre con su ventana (controlador de video de NVIDIA): ya está verificado (firma del
        /// fabricante), y el resultado se confirma por la versión instalada, no por el código de salida.
        /// </summary>
        public bool ShowInstaller;
        /// <summary>
        /// 2.0.15: <see cref="VendorFeed.OfficialInstaller"/>: arquitectura («x64», «x86», «arm64») → dirección oficial fija que
        /// siempre trae el instalador de la última versión (la que enlaza la página de descargas del fabricante).
        /// </summary>
        public readonly Dictionary<string, string> ArchUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// 2.0.15: forma que tiene que tener la versión leída de los recursos del instalador (<see cref="VendorFeed.OfficialInstaller"/>):
        /// si el archivo dice otra cosa, no se compara (mejor «no se pudo consultar» que un «al día» o un «disponible» falso).
        /// </summary>
        public Regex VersionShape;
        /// <summary>
        /// 2.0.16: archivos (relativos a la carpeta «AppData\Roaming» de cada cuenta del equipo) donde el PROPIO programa anota la versión
        /// que tiene, y la expresión (grupo «v») para leerla. TLauncher se actualiza solo dentro del perfil del usuario y su entrada
        /// en «Aplicaciones» queda con la versión del día que se instaló: lo que compara su actualizador es «starter.json». Si se
        /// puede leer, manda sobre el registro; si no, se compara con la del registro (y la fila lo dice).
        /// </summary>
        public string[] ProfileVersionFiles = new string[0];
        public Regex ProfileVersion;
        /// <summary>
        /// 2.0.16: otras direcciones del mismo fabricante con el mismo contenido que <see cref="Url"/> (los servidores alternativos
        /// que usa el propio programa), por si la primera no responde. Tienen que estar permitidas por <see cref="Allows"/>.
        /// </summary>
        public string[] MirrorUrls = new string[0];
        /// <summary>
        /// 2.0.16: la expresión ya identifica por sí sola la versión estable (un campo de un JSON, o el texto «(Final)» pegado al
        /// número): no se descarta porque cerca diga «beta». La página de MSI Afterburner publica «4.6.6 (Final)» y enseguida
        /// «4.6.7 (Beta)», y con el descarte por cercanía la 4.6.6 no se leía.
        /// </summary>
        public bool ExactVersion;

        /// <summary>2.0.15: la dirección oficial fija para esa arquitectura (o la de x64 si no hay una propia), o null.</summary>
        public string ArchUrlFor(string arch)
        {
            string u;
            string a = BrowserCatalog.NormalizeArch(arch ?? "");
            if (a.Length > 0 && ArchUrls.TryGetValue(a, out u)) return u;
            return ArchUrls.TryGetValue("x64", out u) ? u : null;
        }

        /// <summary>El instalador fijo para esa arquitectura, o null.</summary>
        public FixedInstaller FinalInstallerFor(string arch)
        {
            FixedInstaller f;
            return !string.IsNullOrEmpty(arch) && FinalInstallers.TryGetValue(arch, out f) ? f : null;
        }

        public bool Allows(Uri u)
        {
            if (u == null || !u.IsAbsoluteUri || u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort) return false;
            string h = u.Host.ToLowerInvariant();
            if (!string.IsNullOrEmpty(Domain) && (h == Domain || h.EndsWith("." + Domain))) return true;
            if (DownloadPrefixes != null)
                foreach (string pre in DownloadPrefixes)
                    if (!string.IsNullOrEmpty(pre) && u.AbsoluteUri.StartsWith(pre, StringComparison.OrdinalIgnoreCase) && u.AbsoluteUri.Length > pre.Length) return true;
            // Sólo subdominios de los otros (GitHub redirige a release-assets.githubusercontent.com).
            if (AlsoDomains != null) foreach (string d in AlsoDomains) if (!string.IsNullOrEmpty(d) && h.EndsWith("." + d)) return true;
            return false;
        }

        public bool Applies(InstalledProgram p)
        {
            if (p == null || string.IsNullOrEmpty(p.Name)) return false;
            if (Name != null && !Name.IsMatch(p.Name.Trim())) return false;
            return Extra == null || Extra(p);
        }
    }

    /// <summary>
    /// Fuentes oficiales de los fabricantes para programas que winget no sigue, o no sigue a tiempo.
    /// Cada entrada lee la versión de donde la lee el propio programa o de la página de descargas
    /// del fabricante, y sólo descarga del dominio del fabricante, por HTTPS. Los instaladores se
    /// ejecutan sólo si su firma Authenticode es válida, no está revocada y es del fabricante
    /// esperado (o, en AIMP, si el SHA-256 coincide con el publicado). Fuentes verificadas contra el
    /// contenido real el 24/09/2026.
    /// </summary>
    public static class VendorCatalog
    {
        /// <summary>Las fuentes incorporadas en el programa (se revisan con las pruebas).</summary>
        public static readonly List<VendorCheck> BuiltIn = Build();

        /// <summary>
        /// Todas las fuentes: las incorporadas y las del catálogo de fuentes oficiales, un archivo JSON que se puede
        /// ampliar sin recompilar: <c>fuentes-oficiales.json</c> junto al programa y en %LOCALAPPDATA%\Actualizador (el que se
        /// descarga de la dirección configurada en Opciones). Se relee si cambió.
        /// </summary>
        public static List<VendorCheck> All { get { return SourceCatalog.Merge(BuiltIn); } }

        const RegexOptions RX = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        static List<VendorCheck> Build()
        {
            var list = new List<VendorCheck>();

            var aimp = new VendorCheck();
            aimp.Id = "aimp";
            aimp.Vendor = "AIMP (aimp.ru)";
            aimp.Product = "AIMP";
            aimp.Name = new Regex(@"^AIMP(?:\s+v?[0-9][0-9.]*)?(?:\s*\((?:x64|x86|64-bit|32-bit)\))?$", RX);
            aimp.Url = "https://www.aimp.ru/?do=download&os=windows";
            aimp.InfoUrl = aimp.Url;
            aimp.Domain = "aimp.ru";
            aimp.Feed = VendorFeed.AimpPage;
            aimp.Install = VendorInstall.PageHash;
            aimp.Version = new Regex(@"\bv(?<v>\d+\.\d+\.\d+)\s*\(\d{2}\.\d{2}\.\d{4}\)", RegexOptions.Compiled);
            aimp.ExeName = "AIMP.exe";
            aimp.Processes = new[] { "AIMP" };
            aimp.SilentArgs = "/SILENT /AUTO";
            aimp.HowToUpdate = "También se puede actualizar desde el propio AIMP: menú › Configuración › Plugins › Buscador de actualizaciones › «Instalar».";
            list.Add(aimp);

            // Vivaldi estable no está en winget (sólo la versión preliminar). La versión sale del mismo
            // appcast que consulta el actualizador de Vivaldi; el instalador es el que ese appcast indica.
            var viv = new VendorCheck();
            viv.Id = "vivaldi";
            viv.Vendor = "Vivaldi (update.vivaldi.com)";
            viv.Product = "Vivaldi";
            viv.Name = new Regex(@"^Vivaldi$", RX);
            viv.Url = "https://update.vivaldi.com/update/1.0/public/appcast.x64.xml";
            viv.InfoUrl = "https://vivaldi.com/es/download/";
            viv.Domain = "vivaldi.com";
            viv.Feed = VendorFeed.SparkleAppcast;
            viv.Install = VendorInstall.SignedInstaller;
            viv.ExeName = "vivaldi.exe";
            viv.Processes = new[] { "vivaldi" };
            viv.SilentArgs = "--vivaldi-silent --do-not-launch-chrome";
            viv.MachineArgs = "--system-level";
            viv.Signers = new[] { "Vivaldi Technologies AS" };
            viv.InstallerHasProductVersion = true;          // como Chrome: el instalador lleva la versión del navegador
            viv.HowToUpdate = "También se actualiza desde el propio Vivaldi: menú › Ayuda › Buscar actualizaciones.";
            list.Add(viv);

            // Office «Hacer clic y ejecutar» (Microsoft 365, Office 2019/2021/2024): una sola instalación
            // con un solo número de versión para todos los productos e idiomas.
            var office = new VendorCheck();
            office.Id = "office";
            office.Vendor = "Microsoft (config.office.net)";
            office.Product = "Office";
            // Cualquier nombre: lo que decide es que se desinstale con OfficeClickToRun.exe. Los nombres
            // traducidos («Aplicaciones de Microsoft 365 para empresas», «Microsoft OneNote - es-es») no
            // empiezan con «Office» y quedaban sin comparar.
            office.Name = new Regex(@"\S", RX);
            office.Extra = delegate(InstalledProgram p) { return OfficeC2R.IsClickToRun(p); };
            office.Url = "https://clients.config.office.net/releases/v1.0/OfficeReleases";
            office.InfoUrl = "https://learn.microsoft.com/officeupdates/update-history-microsoft365-apps-by-date";
            office.Domain = "office.net";
            office.Feed = VendorFeed.OfficeReleases;
            office.Install = VendorInstall.OfficeClickToRun;
            // Incluye los que quedan abiertos en segundo plano: Skype Empresarial (lync, viene con Office 2016,
            // 2019 y 2021 y arranca con Windows), «Enviar a OneNote» (ONENOTEM) y el Centro de carga (MSOUC).
            // Office no aplica la actualización mientras alguno siga abierto.
            office.Processes = new[] { "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "MSACCESS", "MSPUB", "ONENOTE", "ONENOTEM", "VISIO", "WINPROJ",
                                       "lync", "UcMapi", "MSOUC", "GROOVE", "MSOSYNC", "INFOPATH", "SPDESIGN" };
            office.OneRowForAll = true;
            office.HowToUpdate = "También se actualiza desde cualquier programa de Office: Archivo › Cuenta › Opciones de actualización › Actualizar ahora.";
            list.Add(office);

            // Adobe Acrobat y Reader, sólo la vía continua (DC). La versión es la de Windows publicada en
            // las notas de versión de Adobe (no la de current_version.txt, que es la de Mac); la
            // actualización, el parche MSP oficial con la dirección que publican esas notas.
            var acro = new VendorCheck();
            acro.Id = "acrobat";
            acro.Vendor = "Adobe (adobe.com)";
            acro.Product = "Adobe Acrobat";
            acro.Name = new Regex(@"^Adobe Acrobat(?: DC| Pro DC| Pro)?(?: \((?:64|32)-bit\))?$", RX);
            acro.Extra = delegate(InstalledProgram p) { return AdobeRules.IsContinuous(p.Version); };
            acro.Url = AdobeRules.ReleaseNotesUrl;   // la de Windows (current_version.txt de armmf es la de Mac)
            acro.InfoUrl = "https://helpx.adobe.com/acrobat/release-note/release-notes-acrobat-reader.html";
            acro.Domain = "adobe.com";
            acro.Feed = VendorFeed.AdobeReleaseNotes;
            acro.Install = VendorInstall.AdobeMsp;
            acro.Processes = new[] { "Acrobat", "AcroRd32", "AcroCEF", "RdrCEF" };
            acro.Signers = new[] { "Adobe Inc.", "Adobe Systems Incorporated" };
            acro.HowToUpdate = "También se actualiza desde Acrobat: Ayuda › Buscar actualizaciones.";
            list.Add(acro);

            var reader = new VendorCheck();
            reader.Id = "acrobat-reader";
            reader.Vendor = "Adobe (adobe.com)";
            reader.Product = "Adobe Acrobat Reader";
            reader.Name = new Regex(@"^Adobe Acrobat Reader(?: DC)?(?: - [^()]{2,40})?(?: \((?:64|32)-bit\))?$", RX);
            reader.Extra = acro.Extra;
            reader.Url = AdobeRules.ReleaseNotesUrl;   // Acrobat y Reader comparten versión y notas
            reader.InfoUrl = acro.InfoUrl;
            reader.Domain = "adobe.com";
            reader.Feed = VendorFeed.AdobeReleaseNotes;
            reader.Install = VendorInstall.AdobeMsp;
            reader.Processes = acro.Processes;
            reader.Signers = acro.Signers;
            reader.HowToUpdate = "También se actualiza desde Acrobat Reader: Menú › Ayuda › Buscar actualizaciones.";
            list.Add(reader);

            // Samsung Magician: la página de herramientas enlaza el instalador con la versión en el nombre.
            var magician = new VendorCheck();
            magician.Id = "samsung-magician";
            magician.Vendor = "Samsung (semiconductor.samsung.com)";
            magician.Product = "Samsung Magician";
            magician.Name = new Regex(@"^Samsung Magician$", RX);
            magician.Url = "https://semiconductor.samsung.com/consumer-storage/support/tools/";
            magician.InfoUrl = magician.Url;
            magician.Domain = "samsung.com";
            magician.Feed = VendorFeed.PageRegex;
            // Enlace absoluto a Samsung o relativo (sólo si empieza el atributo: «href="/resources/…»).
            magician.Version = new Regex(@"(?<url>(?:https://(?:download\.)?semiconductor\.samsung\.com|(?<=[""'=]))/resources/software-resources/Samsung_Magician_Installer_Official_(?<v>\d{1,3}(?:\.\d{1,5}){1,3})\.exe)", RX);
            magician.Install = VendorInstall.SignedInstaller;
            magician.Processes = new[] { "SamsungMagician" };
            magician.Signers = new[] { "Samsung Electronics Co., Ltd." };
            magician.Recommended = false;
            magician.HowToUpdate = "También se actualiza desde el propio Samsung Magician (botón «Update»).";
            list.Add(magician);

            // DAEMON Tools Lite: sólo se compara. Su instalador oficial es un descargador que ofrece
            // programas de terceros: no se ejecuta desde acá.
            var dt = new VendorCheck();
            dt.Id = "daemon-tools-lite";
            dt.Vendor = "Disc Soft (daemon-tools.cc)";
            dt.Product = "DAEMON Tools Lite";
            dt.Name = new Regex(@"^DAEMON Tools Lite$", RX);
            dt.Url = "https://www.daemon-tools.cc/releasenotes/dtLite";
            dt.InfoUrl = "https://www.daemon-tools.cc/products/dtLite";
            dt.Domain = "daemon-tools.cc";
            dt.Feed = VendorFeed.PageRegex;
            dt.Version = new Regex(@"DAEMON Tools Lite\s+(?<v>\d{1,3}\.\d{1,3}(?:\.\d{1,5}){0,2})(?![\d.])", RX);
            dt.Install = VendorInstall.None;
            dt.HowToUpdate = "Su instalador oficial es un descargador que ofrece programas de terceros, así que no se ejecuta desde acá: " +
                             "actualizalo desde el propio DAEMON Tools Lite o desde su sitio, y desmarcá las ofertas.";
            list.Add(dt);

            // UltiMaker Cura (2.0.5): lo publica UltiMaker en su proyecto de GitHub, y su instalador registra cada versión con su
            // propio nombre y carpeta («UltiMaker Cura 5.13.0»), así que winget puede no reconocerlo y el programa quedaba sin
            // comparar. Se compara siempre con la última estable de github.com/Ultimaker/Cura. No se instala desde acá: la versión
            // nueva va en otra carpeta y la vieja queda (winget sí la reemplaza; si winget la ofrece, se usa winget). Las betas
            // que anuncia Cura no son estables: las menciona el contraste con GitHub (ReleaseCrossCheck).
            var cura = new VendorCheck();
            cura.Id = "ultimaker-cura";
            cura.Vendor = "UltiMaker (github.com/Ultimaker/Cura)";
            cura.Product = "UltiMaker Cura";
            cura.Name = new Regex(@"^Ulti[Mm]aker Cura(?: \d+(?:\.\d+){1,3}(?:[-+][\w.+-]*)?)?$", RX);
            cura.Url = "https://api.github.com/repos/Ultimaker/Cura/releases/latest";
            cura.InfoUrl = "https://ultimaker.com/software/ultimaker-cura/";
            cura.Domain = "github.com";
            cura.AlsoDomains = new[] { "githubusercontent.com" };
            cura.Feed = VendorFeed.GitHubRelease;
            cura.Install = VendorInstall.None;
            cura.HowToUpdate = "Descargala de ultimaker.com (o desde Cura: Ayuda › Buscar actualizaciones). Cura instala cada versión en su propia carpeta: " +
                               "después de instalar la nueva, desinstalá la vieja desde «Aplicaciones».";
            cura.Note = "Cura instala cada versión al lado de la anterior.";
            cura.SideBySide = true;
            list.Add(cura);

            // Mullvad Browser: lo publica Mullvad en su proyecto de GitHub y lo compila y firma The Tor Project
            // (el certificado del instalador es de «THE TOR PROJECT, INC.»). winget lo tiene, pero el registro de
            // Mullvad apunta a GitHub y, sin esta entrada, la verificación genérica de GitHub (firmante = el de lo
            // instalado) no se podía hacer elevado: la carpeta del navegador la puede escribir el usuario (S64). Con
            // el firmante fijo acá, se instala verificado aunque winget no lo reconozca o vaya atrasado. La versión
            // real del navegador está en «version.json» (su ejecutable dice la de Firefox ESR, 140.x).
            var mullvad = new VendorCheck();
            mullvad.Id = "mullvad-browser";
            mullvad.Vendor = "Mullvad (github.com/mullvad)";
            mullvad.Product = "Mullvad Browser";
            mullvad.Name = new Regex(@"^Mullvad Browser$", RX);
            mullvad.Url = "https://api.github.com/repos/mullvad/mullvad-browser/releases/latest";
            mullvad.InfoUrl = "https://mullvad.net/browser";
            mullvad.Domain = "github.com";
            mullvad.AlsoDomains = new[] { "githubusercontent.com" };
            mullvad.Feed = VendorFeed.GitHubRelease;
            mullvad.Install = VendorInstall.SignedInstaller;
            mullvad.SilentArgs = "/S";
            mullvad.Processes = new[] { "mullvadbrowser" };
            mullvad.Signers = new[] { "THE TOR PROJECT, INC.", "Mullvad VPN AB" };
            mullvad.HowToUpdate = "También se actualiza desde el propio navegador: menú › Ayuda › Acerca de Mullvad Browser.";
            mullvad.Note = "Lo publica Mullvad en GitHub y lo compila y firma The Tor Project.";
            list.Add(mullvad);

            // GOM Player: el catálogo de winget quedó en la 2.3.114 (agosto de 2025) y Chocolatey en la 2.3.90, mientras GOM
            // publicó la 2.3.123 el 13/10/2026: Actualizador decía «Al día» y el propio GOM avisaba que había una nueva. La versión
            // sale de sus notas de versión («V 2.3.123 20261013»); el instalador, de la dirección fija de su CDN, la misma que usa
            // el manifiesto de winget, que siempre trae la última. Se ejecuta sólo si está firmado por GOM & Company (antes GRETECH).
            var gom = new VendorCheck();
            gom.Id = "gom-player";
            gom.Vendor = "GOM & Company (gomlab.com)";
            gom.Product = "GOM Player";
            gom.Name = new Regex(@"^GOM Player$", RX);
            gom.Url = "https://www.gomlab.com/en/gomplayer-media-player/release-note";
            gom.InfoUrl = "https://www.gomlab.com/en/gomplayer-media-player";
            gom.Domain = "gomlab.com";
            gom.Feed = VendorFeed.PageRegex;
            gom.Version = new Regex(@"\bV(?:\s|&nbsp;|<[^>]{0,200}>)*(?<v>\d{1,2}\.\d{1,3}\.\d{1,4})(?:\s|&nbsp;|<[^>]{0,200}>)+20\d{2}[01]\d[0-3]\d\b", RX);
            gom.LatestInstallerUrl = "https://cdn2.gomlab.com/gretech/player/edge/GOMPLAYERGLOBALSETUP_NEW.EXE";
            gom.Install = VendorInstall.SignedInstaller;
            gom.SilentArgs = "/S";
            gom.ExeName = "GOM.exe";
            gom.Processes = new[] { "GOM" };
            gom.Signers = new[] { "GOM & Company", "GRETECH CORPORATION" };
            gom.HowToUpdate = "También se actualiza desde el propio GOM Player cuando avisa que hay una versión nueva.";
            gom.Note = "El catálogo de winget va atrasado con GOM Player: se compara con las notas de versión del fabricante.";
            list.Add(gom);

            // Productos que Microsoft ya no actualiza: la última versión es conocida y no hace falta consultar nada.
            // 2008 a 2013 también se INSTALAN: el instalador oficial de download.microsoft.com, con el SHA-256 que
            // publica el catálogo de winget (Microsoft.VCRedist.20xx.x64/.x86, verificado el 01/11/2026) y la firma de Microsoft.
            // Antes la fila decía «Actualización disponible» pero no se podía marcar (grabación del usuario: Visual C++ 2012).
            AddFinal(list, "vcredist2005-x64", @"^Microsoft Visual C\+\+ 2005 Redistributable \(x64\)$", "8.0.61000", "Visual C++ 2005 (x64)", "Microsoft.VCRedist.2005.x64", null);
            AddFinal(list, "vcredist2005-x86", @"^Microsoft Visual C\+\+ 2005 Redistributable$", "8.0.61001", "Visual C++ 2005 (x86)", "Microsoft.VCRedist.2005.x86", null);
            AddFinal(list, "vcredist2008", @"^Microsoft Visual C\+\+ 2008 Redistributable - x(?:64|86) 9\.0\.[0-9.]+$", "9.0.30729.6161", "Visual C++ 2008", "Microsoft.VCRedist.2008.x64 / .x86", "/qn /norestart",
                "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x64.exe", "C5E273A4A16AB4D5471E91C7477719A2F45DDADB76C7F98A38FA5074A6838654",
                "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x86.exe", "8742BCBF24EF328A72D2A27B693CC7071E38D3BB4B9B44DEC42AA3D2C8D61D92");
            AddFinal(list, "vcredist2010", @"^Microsoft Visual C\+\+ 2010\s+x(?:64|86) Redistributable - 10\.0\.[0-9.]+$", "10.0.40219", "Visual C++ 2010", "Microsoft.VCRedist.2010.x64 / .x86", "/quiet /norestart",
                "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe", "F3B7A76D84D23F91957AA18456A14B4E90609E4CE8194C5653384ED38DADA6F3",
                "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x86.exe", "99DCE3C841CC6028560830F7866C9CE2928C98CF3256892EF8E6CF755147B0D8");
            AddFinal(list, "vcredist2012", @"^Microsoft Visual C\+\+ 2012 Redistributable \(x(?:64|86)\) - 11\.0\.[0-9.]+$", "11.0.61030", "Visual C++ 2012", "Microsoft.VCRedist.2012.x64 / .x86", "/install /quiet /norestart",
                "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe", "681BE3E5BA9FD3DA02C09D7E565ADFA078640ED66A0D58583EFAD2C1E3CC4064",
                "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x86.exe", "B924AD8062EAF4E70437C8BE50FA612162795FF0839479546CE907FFA8D6E386");
            AddFinal(list, "vcredist2013", @"^Microsoft Visual C\+\+ 2013 Redistributable \(x(?:64|86)\) - 12\.0\.[0-9.]+$", "12.0.40664", "Visual C++ 2013", "Microsoft.VCRedist.2013.x64 / .x86", "/install /quiet /norestart",
                "https://download.visualstudio.microsoft.com/download/pr/10912041/cee5d6bca2ddbcd039da727bf4acb48a/vcredist_x64.exe", "A4BBA7701E355AE29C403431F871A537897C363E215CAFE706615E270984F17C",
                "https://download.visualstudio.microsoft.com/download/pr/10912113/5da66ddebb0ad32ebd4b922fd82e8e25/vcredist_x86.exe", "53B605D1100AB0A88B867447BBF9274B5938125024BA01F5105A9E178A3DCDBD");

            var ice = new VendorCheck();
            ice.Id = "image-composite-editor";
            ice.Vendor = "Microsoft Research";
            ice.Product = "Image Composite Editor";
            ice.Name = new Regex(@"^(?:Microsoft )?Image Composite Editor(?: \(64-bit\))?$", RX);
            ice.Feed = VendorFeed.FinalVersion;
            ice.FinalVersion = "2.0.3";
            ice.Install = VendorInstall.None;
            ice.Note = "Microsoft Research discontinuó el programa: la 2.0.3 es la última versión publicada y no va a recibir actualizaciones.";
            ice.HowToUpdate = "No hay versiones más nuevas.";
            list.Add(ice);

            // ---------------------------------------------------------------- 2.0.8: los que quedaban «No verificable»
            // Sólo se compara: «Actualizar» abre la página oficial (no hay un instalador que se pueda ejecutar en silencio y
            // verificar, o el programa se actualiza solo). También se comparan las copias portables (sin entrada en el registro).

            // Tor Browser: la versión publicada sale del mismo archivo que consulta la página de descargas de The Tor Project; la
            // instalada, de tbb_version.json (su ejecutable y el registro dicen la de Firefox ESR, 140.x).
            // 2.0.41: se actualiza desde acá (antes sólo se comparaba y «Actualizar» abría la página): ver TorBrowser.
            var tor = new VendorCheck();
            tor.Id = "tor-browser";
            tor.Vendor = "The Tor Project (torproject.org)";
            tor.Product = "Tor Browser";
            tor.Name = new Regex(@"^Tor Browser(?: \d+(?:\.\d+){0,3})?$", RX);
            tor.Url = TorBrowser.LegacyFeed;
            tor.InfoUrl = "https://www.torproject.org/download/";
            tor.Domain = "torproject.org";
            tor.Feed = VendorFeed.TorBrowser;
            tor.Install = VendorInstall.TorBrowser;
            tor.Signers = TorBrowser.Signers;
            tor.VersionFiles = new[] { @"tbb_version.json", @"Browser\tbb_version.json", @"..\Browser\tbb_version.json",
                                       @"TorBrowser\Docs\ChangeLog.txt", @"Browser\TorBrowser\Docs\ChangeLog.txt" };
            tor.Recommended = true;
            tor.HowToUpdate = "Tor Browser se actualiza solo: abrilo y, cuando avise, tocá «Reiniciar para actualizar» (o ☰ › Ayuda › Acerca de Tor Browser). " +
                              "También se puede bajar de su página oficial.";
            tor.Note = "Su ejecutable dice la versión de Firefox ESR (140.x): se compara la de Tor Browser.";
            list.Add(tor);

            // Samsung USB Driver for Mobile Phones: la página para desarrolladores de Samsung publica la versión en el título.
            var samsungUsb = new VendorCheck();
            samsungUsb.Id = "samsung-usb-driver";
            samsungUsb.Vendor = "Samsung (developer.samsung.com)";
            samsungUsb.Product = "Samsung USB Driver";
            samsungUsb.Name = new Regex(@"^Samsung USB Driver for Mobile Phones$", RX);
            samsungUsb.Url = "https://developer.samsung.com/android-usb-driver";
            samsungUsb.InfoUrl = samsungUsb.Url;
            samsungUsb.Domain = "samsung.com";
            samsungUsb.Feed = VendorFeed.PageRegex;
            samsungUsb.Version = new Regex(@"USB Driver for Windows(?:\s|&nbsp;|<[^>]{0,200}>)*v(?<v>\d{1,2}\.\d{1,3}\.\d{1,3}\.\d{1,4})\b", RX);
            samsungUsb.Install = VendorInstall.None;
            samsungUsb.Recommended = false;
            samsungUsb.HowToUpdate = "Bajá el instalador de la página de Samsung para desarrolladores e instalalo (reemplaza al anterior).";
            list.Add(samsungUsb);

            // LaunchBox / Big Box: el registro de cambios oficial («Version 14.1 - Released October 29, 2026»). Big Box es la
            // interfaz de pantalla completa de LaunchBox y lleva el mismo número de versión. El instalador se pide con un formulario.
            var launchbox = new VendorCheck();
            launchbox.Id = "launchbox";
            launchbox.Vendor = "Unbroken Software (launchbox-app.com)";
            launchbox.Product = "LaunchBox";
            launchbox.Name = new Regex(@"^LaunchBox(?: Big Box)?$", RX);
            launchbox.Url = "https://www.launchbox-app.com/about/changelog";
            launchbox.InfoUrl = "https://www.launchbox-app.com/download";
            launchbox.Domain = "launchbox-app.com";
            launchbox.Feed = VendorFeed.PageRegex;
            launchbox.Version = new Regex(@"Version\s+(?<v>\d{1,3}\.\d{1,3}(?:\.\d{1,3}){0,2})\s*(?:-|&#8211;|–)\s*Released\b", RX);
            launchbox.Install = VendorInstall.None;
            launchbox.Recommended = false;
            launchbox.HowToUpdate = "LaunchBox se actualiza desde el propio programa (Ayuda › Buscar actualizaciones) o con el instalador de su página.";
            list.Add(launchbox);

            // ---------------------------------------------------------------- 2.0.12: 7-Zip, siempre con su página oficial
            // La versión sale de 7-zip.org («Download 7-Zip 26.05 (2026-10-21) for Windows»), no del catálogo de winget ni de GitHub:
            // en la 2.0.11 apareció una 26.06 en github.com/ip7z/7zip que 7-zip.org todavía no publicaba, se ofreció y «Actualizar» abría
            // GitHub. El instalador es el que enlaza 7-zip.org para la arquitectura instalada (hoy lo aloja en el proyecto de GitHub del
            // autor). Igor Pavlov no firma sus instaladores: se instala sólo si el SHA-256 coincide con el que publica el autor para ese
            // mismo archivo y el instalador es de 7-Zip.
            var sevenZip = new VendorCheck();
            sevenZip.Id = "7-zip";
            sevenZip.Vendor = "Igor Pavlov (7-zip.org)";
            sevenZip.Product = "7-Zip";
            sevenZip.Name = new Regex(@"^7-Zip(?: \d{1,2}\.\d{2})?(?: \((?:x64|arm64)(?: edition)?\))?$", RX);
            sevenZip.Url = "https://www.7-zip.org/download.html";
            sevenZip.InfoUrl = "https://www.7-zip.org/download.html";
            sevenZip.Domain = "7-zip.org";
            sevenZip.DownloadPrefixes = new[] { "https://github.com/ip7z/7zip/releases/download/" };
            sevenZip.AlsoDomains = new[] { "githubusercontent.com" };     // GitHub redirige la descarga a release-assets.githubusercontent.com
            sevenZip.Feed = VendorFeed.PageRegex;
            // «Download 7-Zip 26.05 (2026-10-21)»: con la fecha entre paréntesis justo después (las beta dicen «26.06 beta (…)»).
            sevenZip.Version = new Regex(@"Download(?:\s|&nbsp;)+7-Zip(?:\s|&nbsp;)+(?<v>\d{1,2}\.\d{2})(?:\s|&nbsp;|<[^<>]{0,40}>)*\(\s*20\d{2}-[01]\d-[0-3]\d\s*\)", RX);
            sevenZip.ArchLinks["x64"] = new Regex(@"^7z\d{4}-x64\.exe$", RX);
            sevenZip.ArchLinks["x86"] = new Regex(@"^7z\d{4}\.exe$", RX);
            sevenZip.ArchLinks["arm64"] = new Regex(@"^7z\d{4}-arm64\.exe$", RX);
            sevenZip.ArchLinks["x64-msi"] = new Regex(@"^7z\d{4}-x64\.msi$", RX);
            sevenZip.ArchLinks["x86-msi"] = new Regex(@"^7z\d{4}\.msi$", RX);
            sevenZip.HashRepo = "ip7z/7zip";
            sevenZip.UnsignedWithPublishedHash = true;
            sevenZip.Signers = new[] { "Igor Pavlov" };                    // si algún día los firma, tiene que ser él
            sevenZip.Install = VendorInstall.SignedInstaller;
            sevenZip.SilentArgs = "/S";                                    // el instalador conserva la carpeta donde ya estaba
            sevenZip.ExeName = "7zFM.exe";
            sevenZip.Processes = new[] { "7zFM", "7zG" };
            sevenZip.InstallerHasProductVersion = true;
            sevenZip.HowToUpdate = "Bajá el instalador de su página oficial (www.7-zip.org) e instalalo encima: reemplaza al anterior y conserva la configuración.";
            sevenZip.Note = "La versión se compara siempre con la que publica www.7-zip.org.";
            list.Add(sevenZip);

            // ---------------------------------------------------------------- 2.0.13: controlador de gráficos de NVIDIA
            // Captura del usuario (07/11/2026): «NVIDIA Controlador de gráficos 475.14» quedaba «No verificable». La versión sale del
            // servicio que usa la página de descargas de NVIDIA, para la placa instalada; el instalador, de download.nvidia.com, firmado
            // por NVIDIA. El nombre en «Aplicaciones» cambia con cada versión y depende del idioma de Windows.
            var nvidia = new VendorCheck();
            nvidia.Id = "nvidia-graphics-driver";
            nvidia.Vendor = "NVIDIA (nvidia.com)";
            nvidia.Product = "Controlador de gráficos NVIDIA";
            nvidia.Name = new Regex(NvidiaDriverPattern, RX);   // constante: BuiltIn se arma antes que los campos estáticos de más abajo
            nvidia.Url = NvidiaDriver.ProductsUrl;
            nvidia.InfoUrl = "https://www.nvidia.com/Download/index.aspx";
            nvidia.Domain = "nvidia.com";
            nvidia.AlsoDomains = new[] { "geforce.com" };                 // gfwsl.geforce.com: el servicio de la página de descargas
            nvidia.Feed = VendorFeed.NvidiaDriver;
            nvidia.Install = VendorInstall.SignedInstaller;
            nvidia.Signers = new[] { "NVIDIA Corporation" };
            nvidia.SilentArgs = null;
            nvidia.ShowInstaller = true;
            nvidia.NameHasVersion = true;
            nvidia.HowToUpdate = "Bajalo de nvidia.com › Controladores (eligiendo tu placa) o desde la aplicación NVIDIA, e instalalo encima.";
            nvidia.Note = "Se abre el instalador de NVIDIA para que elijas «Instalación rápida»; la pantalla parpadea un momento mientras cambia el controlador.";
            list.Add(nvidia);

            // ---------------------------------------------------------------- 2.0.15: Fortinet (FortiClient)
            // Pedido del usuario (11/11/2026): «https://www.fortinet.com/lat/support/product-downloads no detecta ningún programa de
            // esa página». La página no publica el número de versión (dice «FortiClient 7.4»): sus botones llevan a direcciones fijas
            // de links.fortinet.com que redirigen al instalador de la última versión en filestore.fortinet.com. Esa es la fuente
            // oficial: la versión sale del nombre del archivo (FortiClientOnlineInstaller_7.4.8.2066.M.exe) o, si no la lleva
            // (FortiClientVPNInstaller.exe), de sus recursos, que es lo que Fortinet indica revisar (Propiedades › Detalles).
            // Se instala ese mismo archivo, sólo con la firma de Fortinet.
            var fctVpn = new VendorCheck();
            fctVpn.Id = "forticlient-vpn";
            fctVpn.Vendor = "Fortinet (fortinet.com)";
            fctVpn.Product = "FortiClient VPN";
            fctVpn.Name = new Regex(@"^FortiClient VPN(?: \d+(?:\.\d+){0,3})?$", RX);
            fctVpn.Url = FortinetVpnX64;
            fctVpn.InfoUrl = FortinetDownloads;
            fctVpn.Domain = "fortinet.com";                       // links.fortinet.com (redirección) y filestore.fortinet.com (instalador)
            fctVpn.Feed = VendorFeed.OfficialInstaller;
            fctVpn.ArchUrls["x64"] = FortinetVpnX64;
            fctVpn.ArchUrls["x86"] = FortinetVpnX64;
            fctVpn.ArchUrls["arm64"] = "https://links.fortinet.com/forticlient/winarm/vpnagent";
            fctVpn.Version = new Regex(@"^FortiClientVPN(?:Online)?(?:Installer|Setup)_(?<v>\d{1,2}\.\d{1,2}\.\d{1,3}(?:\.\d{1,5})?)", RX);
            fctVpn.VersionShape = new Regex(FortinetVersionShape, RX);
            fctVpn.Install = VendorInstall.SignedInstaller;
            fctVpn.SilentArgs = "/quiet /norestart";
            fctVpn.ExeName = "FortiClient.exe";
            fctVpn.Processes = new[] { "FortiClient" };            // la consola; el ícono de la bandeja y los servicios los maneja el instalador
            fctVpn.Signers = FortinetSigners();
            fctVpn.InstallerHasProductVersion = true;
            fctVpn.HowToUpdate = "Bajá «FortiClient VPN» (Solo VPN) de la página de descargas de Fortinet e instalalo encima: conserva las conexiones configuradas.";
            fctVpn.Note = "Es el instalador que enlaza la página de descargas de Fortinet: siempre trae la última versión gratuita y baja el resto de " +
                          "los servidores de Fortinet mientras instala. Si estás conectado por esta VPN, la conexión se corta mientras se actualiza.";
            list.Add(fctVpn);

            // FortiClient completo (ZTNA / EPP): la versión sale del nombre del instalador que enlaza la misma página. Lo administra el
            // FortiClient EMS de la organización (licencia, versión aprobada, perfiles): se compara, pero no se instala desde acá.
            var fct = new VendorCheck();
            fct.Id = "forticlient";
            fct.Vendor = "Fortinet (fortinet.com)";
            fct.Product = "FortiClient";
            fct.Name = new Regex(@"^FortiClient(?: \d+(?:\.\d+){0,3})?$", RX);
            fct.Url = "https://links.fortinet.com/forticlient/win64/fabricagent";
            fct.InfoUrl = FortinetDownloads;
            fct.Domain = "fortinet.com";
            fct.Feed = VendorFeed.OfficialInstaller;
            fct.ArchUrls["x64"] = fct.Url;
            fct.ArchUrls["arm64"] = "https://links.fortinet.com/forticlient/winarm/fabricagent";
            fct.Version = new Regex(@"^FortiClient(?:Online)?(?:Installer|Setup)_(?<v>\d{1,2}\.\d{1,2}\.\d{1,3}\.\d{1,5})", RX);
            fct.VersionShape = new Regex(FortinetVersionShape, RX);
            fct.Install = VendorInstall.None;
            fct.ExeName = "FortiClient.exe";
            fct.Signers = FortinetSigners();
            fct.Recommended = false;
            fct.HowToUpdate = "FortiClient (ZTNA/EPP) lo actualiza el FortiClient EMS de la organización que lo administra, con la versión que aprobó: " +
                              "pedíselo a quien lo administra. Si no está administrado, bajá el instalador de la página de descargas de Fortinet.";
            fct.Note = "Se compara con el instalador que enlaza la página de descargas de Fortinet.";
            list.Add(fct);

            // ---------------------------------------------------------------- 2.0.16: TLauncher y MSI Afterburner
            // Captura del usuario (13/11/2026): TLauncher 2.9323 «No verificable» mientras el propio TLauncher avisaba «¡Nueva
            // actualización encontrada! 2.9381». winget no lo tiene y la portada de tlauncher.org nombra una versión vieja (2.9307).
            // La versión sale de donde la lee el propio TLauncher: su arranque (starter4j, de código abierto) baja «appConfig.json»
            // de repo.tlauncher.org y compara «appVersion» con «currentAppVersion» de %APPDATA%\.tlauncher\starter\starter.json.
            // 2.0.19 (captura del 17/11/2026: «Pendiente: Se abrió su página (tlauncher.org)»): se instala desde acá como lo hace ese
            // arranque al tocar «Actualizar» en su ventana: archivos verificados con el SHA-1 de appConfig.json y la versión en
            // starter.json (Starter4j). Sin abrir la página, ni la ventana de TLauncher, ni su oferta de programas de terceros.
            var tl = new VendorCheck();
            tl.Id = "tlauncher";
            tl.Vendor = "TLauncher (repo.tlauncher.org)";
            tl.Product = "TLauncher";
            tl.Name = new Regex(@"^TLauncher(?: \d{1,2}(?:\.\d{1,6}){1,3})?$", RX);
            tl.Url = "https://repo.tlauncher.org/tlauncher-sources/prod/release/tlauncher/appConfig.json";
            tl.MirrorUrls = new[] { "https://repo.fastrepo.org/tlauncher-sources/prod/release/tlauncher/appConfig.json" };
            tl.InfoUrl = "https://tlauncher.org/";
            tl.Domain = "tlauncher.org";
            tl.AlsoDomains = new[] { "fastrepo.org" };              // el servidor alternativo que usa el propio arranque de TLauncher
            tl.Feed = VendorFeed.PageRegex;
            tl.Version = new Regex(TLauncherAppVersion, RX);
            tl.ExactVersion = true;
            tl.ProfileVersionFiles = new[] { @".tlauncher\starter\starter.json" };
            tl.ProfileVersion = new Regex(@"""currentAppVersion""\s*:\s*""?(?<v>\d{1,2}\.\d{1,6}(?:\.\d{1,6}){0,2})(?![\d.])", RX);
            tl.Install = VendorInstall.Starter4j;
            tl.Processes = new[] { "TLauncher" };
            tl.Recommended = true;
            tl.HowToUpdate = "Abrilo una vez y volvé a buscar: Actualizador lo actualiza desde acá. Si lo actualizás desde su propia ventana, ojo: trae una " +
                             "«oferta opcional» de programas de terceros (un navegador, un antivirus): rechazala o desmarcala antes de seguir.";
            tl.Note = "Se compara con la misma fuente que consulta el propio TLauncher al abrirse.";
            list.Add(tl);

            // MSI Afterburner: quedaba «No verificable». Su página oficial en msi.com publica «4.6.6 (Final)» y, aparte, la beta
            // («4.6.7 (Beta)»): se toma sólo la que dice «Final». MSI lo entrega en un .zip: se compara, no se instala desde acá.
            var ab = new VendorCheck();
            ab.Id = "msi-afterburner";
            ab.Vendor = "MSI (msi.com)";
            ab.Product = "MSI Afterburner";
            ab.Name = new Regex(@"^MSI Afterburner(?: \d{1,2}(?:\.\d{1,3}){1,3})?(?: Beta(?: \d{1,2})?)?$", RX);
            ab.Url = "https://www.msi.com/Landing/afterburner/graphics-cards";
            ab.InfoUrl = ab.Url;
            ab.Domain = "msi.com";
            ab.Feed = VendorFeed.PageRegex;
            ab.Version = new Regex(@"(?<![\d.])(?<v>\d{1,2}\.\d{1,2}\.\d{1,3})(?:\s|&nbsp;|<[^>]{0,200}>)*\(\s*Final\s*\)", RX);
            ab.ExactVersion = true;
            ab.Install = VendorInstall.None;
            ab.ExeName = "MSIAfterburner.exe";
            ab.Recommended = false;
            ab.HowToUpdate = "Bajá el instalador de la página oficial de MSI (o de Guru3D, el sitio de su autor) e instalalo encima: conserva los perfiles. " +
                             "MSI advierte que los demás sitios que ofrecen Afterburner son falsos.";
            ab.Note = "Se compara con la versión «Final» que publica msi.com (las beta no se ofrecen).";
            list.Add(ab);
            return list;
        }

        /// <summary>
        /// 2.0.13: «NVIDIA Graphics Driver 621.14», «NVIDIA Controlador de gráficos 475.14», «NVIDIA Grafiktreiber 621.14»… (el componente
        /// Display.Driver, con la versión al final). No: «NVIDIA HD Audio Driver 1.3.40.14», «NVIDIA PhysX…», «NVIDIA 3D Vision…».
        /// </summary>
        // 2.0.15: constantes (BuiltIn se arma antes que los campos estáticos).
        const string FortinetDownloads = "https://www.fortinet.com/lat/support/product-downloads";
        const string FortinetVpnX64 = "https://links.fortinet.com/forticlient/win/vpnagent";
        /// <summary>7.4.3.1790: cuatro números, de la 6 en adelante (FortiClient 6.x, 7.x, 8.x).</summary>
        const string FortinetVersionShape = @"^(?:[6-9]|[1-9]\d)\.\d{1,2}\.\d{1,3}\.\d{1,5}$";
        static string[] FortinetSigners() { return new[] { "Fortinet Technologies (Canada) ULC", "Fortinet Technologies (Canada) Inc.", "Fortinet Technologies Inc", "Fortinet Inc." }; }

        /// <summary>2.0.16: «"appVersion": "2.9381"» (o sin comillas: starter4j lo escribe de las dos maneras) en appConfig.json.</summary>
        const string TLauncherAppVersion = @"""appVersion""\s*:\s*""?(?<v>\d{1,2}\.\d{1,6}(?:\.\d{1,6}){0,2})(?![\d.])";

        public const string NvidiaDriverPattern =
            @"^NVIDIA\s[\p{L}\s]{0,40}?(?:Graphics|Gr[aá]fic|Grafik|graphique|V[ií]d[eé]o|Display|Графическ|Grafick|Graficzn)[\p{L}\s]{0,40}?\s\d{3}\.\d{2}$";

        static void AddFinal(List<VendorCheck> list, string id, string name, string final, string product, string wingetId, string silentArgs,
                             params string[] installers)
        {
            var c = new VendorCheck();
            c.Id = id;
            c.Vendor = "Microsoft (versión final)";
            c.Product = product;
            c.Name = new Regex(name, RX);
            c.Feed = VendorFeed.FinalVersion;
            c.FinalVersion = final;
            c.InfoUrl = "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist";
            c.Install = VendorInstall.None;
            c.Note = "Microsoft ya no publica versiones nuevas de " + product + "; la última es la " + final +
                     ". La instalan programas viejos que la necesitan: no la desinstales.";
            c.HowToUpdate = "Descargala de la página oficial de Microsoft (clic derecho › «Abrir sitio del fabricante») o con «winget install " + wingetId + "».";
            // installers: url x64, sha x64, url x86, sha x86.
            if (installers != null && installers.Length == 4 && silentArgs != null)
            {
                c.Install = VendorInstall.SignedInstaller;
                c.Domain = "microsoft.com";
                c.Signers = new[] { "Microsoft Corporation" };
                c.SilentArgs = silentArgs;
                c.FinalInstallers["x64"] = new FixedInstaller { Url = installers[0], Sha256 = installers[1].ToLowerInvariant() };
                c.FinalInstallers["x86"] = new FixedInstaller { Url = installers[2], Sha256 = installers[3].ToLowerInvariant() };
            }
            list.Add(c);
        }

        /// <summary>La primera entrada del catálogo que corresponde al programa, o null.</summary>
        public static VendorCheck Match(InstalledProgram p)
        {
            foreach (var v in All) if (v.Applies(p)) return v;
            return null;
        }

        /// <summary>Por nombre solo (sin las condiciones adicionales). Lo usan la deduplicación y las pruebas.</summary>
        public static VendorCheck Match(string programName)
        {
            if (string.IsNullOrEmpty(programName)) return null;
            string n = programName.Trim();
            foreach (var v in All) if (v.Name != null && v.Extra == null && v.Name.IsMatch(n)) return v;
            return null;
        }

        public static VendorCheck Find(string id)
        {
            foreach (var v in All) if (v.Id == id) return v;
            return null;
        }

        static readonly Regex PreRelease = new Regex(@"beta|\brc\d*\b|alpha|preview|snapshot|бета|альфа", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ================================================================= Página con expresión

        /// <summary>
        /// Versión más alta que aparece en la página según la expresión de la entrada (grupo «v»),
        /// descartando las que tienen «beta», «preview»… cerca. <paramref name="url"/>: el grupo
        /// «url» de esa coincidencia, si la expresión lo tiene y es del dominio del fabricante.
        /// </summary>
        public static string ParsePage(VendorCheck check, string page, out string url)
        {
            url = null;
            if (check == null || check.Version == null || string.IsNullOrEmpty(page)) return null;
            string t = page.Length > MaxPage ? page.Substring(0, MaxPage) : page;
            // Los scripts (publicidad, datos de seguimiento) pueden nombrar otras versiones: no cuentan.
            t = StripBlocks(StripBlocks(t, "script"), "style");
            string best = null;
            foreach (Match m in check.Version.Matches(t))
            {
                string v = m.Groups["v"].Value;
                if (VersionUtil.Parse(v) == null) continue;
                int from = Math.Max(0, m.Index - 40);
                string around = t.Substring(from, Math.Min(t.Length - from, m.Length + 80));
                if (!check.ExactVersion && PreRelease.IsMatch(around)) continue;
                if (best != null && !VersionUtil.IsNewer(v, best)) continue;
                string u = m.Groups["url"].Success ? WebUtility.HtmlDecode(m.Groups["url"].Value) : null;
                if (u != null)
                {
                    // Enlace relativo («/resources/…»): se completa con la dirección de la página.
                    Uri parsed;
                    u = Uri.TryCreate(new Uri(check.Url), u, out parsed) && check.Allows(parsed) ? parsed.AbsoluteUri : null;
                }
                best = v;
                url = u;
            }
            return best;
        }

        static readonly Regex HrefAny = new Regex(@"href\s*=\s*[""'](?<u>[^""'<>\s]{3,600})[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>La clave de <see cref="VendorCheck.ArchLinks"/> para la arquitectura y el tipo de instalación («x64», «x86-msi»).</summary>
        public static string ArchLinkKey(string arch, bool msi)
        {
            string a = BrowserCatalog.NormalizeArch(arch ?? "");
            if (a.Length == 0) a = "x64";
            return msi ? a + "-msi" : a;
        }

        /// <summary>
        /// 2.0.12: el instalador que la página oficial enlaza para esa arquitectura y esa versión (<see cref="VendorCheck.ArchLinks"/>),
        /// como dirección absoluta permitida por la entrada, o null. El nombre del archivo tiene que llevar la versión publicada sin
        /// puntos («26.05» → «2605»): un enlace a una versión vieja que quedó en la página no se usa.
        /// </summary>
        public static string PickArchLink(VendorCheck check, string page, string latest, string arch, bool msi)
        {
            if (check == null || string.IsNullOrEmpty(page) || string.IsNullOrEmpty(latest)) return null;
            Regex rx;
            if (!check.ArchLinks.TryGetValue(ArchLinkKey(arch, msi), out rx)) return null;
            string digits = VersionDigits(latest);
            if (digits.Length == 0) return null;
            string t = page.Length > MaxPage ? page.Substring(0, MaxPage) : page;
            t = StripBlocks(t, "script");
            foreach (Match m in HrefAny.Matches(t))
            {
                string raw = WebUtility.HtmlDecode(m.Groups["u"].Value);
                Uri u;
                if (!Uri.TryCreate(new Uri(check.Url), raw, out u) || !check.Allows(u)) continue;
                string file = Uri.UnescapeDataString(u.Segments.Length > 0 ? u.Segments[u.Segments.Length - 1] : "");
                if (!rx.IsMatch(file) || file.IndexOf(digits, StringComparison.Ordinal) < 0) continue;
                return u.AbsoluteUri;
            }
            return null;
        }

        /// <summary>«26.05» → «2605»; «1.2.3» → «123». Vacío si no es una versión.</summary>
        public static string VersionDigits(string version)
        {
            if (VersionUtil.Parse(version) == null) return "";
            var sb = new StringBuilder();
            foreach (char ch in version.Trim()) if (ch >= '0' && ch <= '9') sb.Append(ch); else if (ch != '.') return "";
            return sb.ToString();
        }

        static readonly Regex SafeTag = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,40}$", RegexOptions.Compiled);

        /// <summary>
        /// 2.0.12: la consulta a GitHub con el SHA-256 del instalador que enlaza la página oficial: la versión publicada en el proyecto
        /// del autor (<see cref="VendorCheck.HashRepo"/>) con la etiqueta de la dirección («…/releases/download/26.05/7z2605-x64.exe»)
        /// o, si la página enlaza su propio servidor, la de la versión publicada. null si no corresponde.
        /// </summary>
        public static string HashQueryUrl(VendorCheck check, string installerUrl, string latest, out string fileName)
        {
            fileName = null;
            if (check == null || string.IsNullOrEmpty(check.HashRepo) || string.IsNullOrEmpty(installerUrl)) return null;
            Uri u;
            if (!Uri.TryCreate(installerUrl, UriKind.Absolute, out u)) return null;
            fileName = Uri.UnescapeDataString(u.Segments.Length > 0 ? u.Segments[u.Segments.Length - 1] : "");
            if (!InstallerSupport.IsSafeFileName(fileName)) { fileName = null; return null; }
            string tag = latest;
            string prefix = "https://github.com/" + check.HashRepo + "/releases/download/";
            if (u.AbsoluteUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = u.AbsoluteUri.Substring(prefix.Length);
                int slash = rest.IndexOf('/');
                tag = slash > 0 ? Uri.UnescapeDataString(rest.Substring(0, slash)) : null;
            }
            if (tag == null || !SafeTag.IsMatch(tag)) return null;
            return "https://api.github.com/repos/" + check.HashRepo + "/releases/tags/" + Uri.EscapeDataString(tag);
        }

        /// <summary>El SHA-256 (64 hexadecimales en minúsculas) del archivo en la respuesta de «/releases/tags/…», o null.</summary>
        public static string PublishedSha256(string releaseJson, string fileName)
        {
            if (string.IsNullOrEmpty(releaseJson) || string.IsNullOrEmpty(fileName)) return null;
            GitHubRelease rel;
            try { rel = GitHubRules.ParseRelease(releaseJson); }
            catch (Exception ex) { Log.Quiet("SHA-256 publicado", ex); return null; }
            if (rel == null) return null;                                   // borrador o preliminar: no
            foreach (var a in rel.Assets)
                if (a.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && a.Sha256.Length == 64) return a.Sha256.ToLowerInvariant();
            return null;
        }

        // ================================================================= Appcast (Sparkle)

        static readonly Regex AttrRx = new Regex(@"(?<k>[A-Za-z_][A-Za-z0-9_:.\-]*)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", RegexOptions.Compiled);

        /// <summary>Una versión publicada en un appcast: número y dirección del instalador completo.</summary>
        public sealed class AppcastItem
        {
            public string Version = "", Url = "";
        }

        /// <summary>
        /// Instalador completo más nuevo de un appcast de Sparkle. Se ignoran los paquetes
        /// incrementales (&lt;sparkle:deltas&gt; o enclosure con sparkle:deltaFrom), que no se instalan solos.
        /// </summary>
        public static AppcastItem ParseAppcast(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            string s = xml.Length > MaxPage ? xml.Substring(0, MaxPage) : xml;
            s = StripBlocks(s, "sparkle:deltas");
            AppcastItem best = null;
            int pos = 0;
            while (pos < s.Length)
            {
                int a = s.IndexOf("<enclosure", pos, StringComparison.OrdinalIgnoreCase);
                if (a < 0) break;
                int gt = s.IndexOf('>', a);
                if (gt < 0 || gt - a > 4000) break;
                string tag = s.Substring(a, gt - a);
                pos = gt + 1;
                string url = null, ver = null, shortVer = null;
                bool delta = false;
                foreach (Match m in AttrRx.Matches(tag))
                {
                    string k = m.Groups["k"].Value, v = WebUtility.HtmlDecode(m.Groups["v"].Value).Trim();
                    if (k.Equals("url", StringComparison.OrdinalIgnoreCase)) url = v;
                    else if (k.Equals("sparkle:version", StringComparison.OrdinalIgnoreCase)) ver = v;
                    else if (k.Equals("sparkle:shortVersionString", StringComparison.OrdinalIgnoreCase)) shortVer = v;
                    else if (k.Equals("sparkle:deltaFrom", StringComparison.OrdinalIgnoreCase)) delta = true;
                }
                if (delta || string.IsNullOrEmpty(url)) continue;
                string version = VersionUtil.Parse(ver) != null ? ver : shortVer;
                if (VersionUtil.Parse(version) == null) continue;
                if (best != null && !VersionUtil.IsNewer(version, best.Version)) continue;
                best = new AppcastItem();
                best.Version = VersionUtil.Normalize(version);
                best.Url = url;
            }
            return best;
        }

        // ================================================================= Versión final conocida

        /// <summary>
        /// Estado de un producto que ya no se actualiza: al día si la instalada es la final (o
        /// posterior); disponible (a mano) si es anterior; null si la instalada no se puede leer.
        /// </summary>
        public static UpdateStatus? EvaluateFinal(string installed, string final)
        {
            int? c = VersionUtil.Compare(installed, final);
            if (!c.HasValue) return null;
            return c.Value >= 0 ? UpdateStatus.AlDia : UpdateStatus.Disponible;
        }

        // ================================================================= AIMP (sin cambios)

        /// <summary>La versión estable más alta publicada en la página de AIMP (se descartan las beta).</summary>
        public static string ParseLatest(VendorCheck check, string page)
        {
            if (check == null || string.IsNullOrEmpty(page)) return null;
            string best = null;
            foreach (var d in ParseDownloads(page))
                if (best == null || VersionUtil.IsNewer(d.Version, best)) best = d.Version;
            if (best != null) return best;
            foreach (Match m in check.Version.Matches(page))
            {
                int from = Math.Max(0, m.Index - 60);
                string around = page.Substring(from, Math.Min(page.Length - from, m.Length + 120));
                if (PreRelease.IsMatch(around)) continue;
                string v = m.Groups["v"].Value;
                if (best == null || VersionUtil.IsNewer(v, best)) best = v;
            }
            return best;
        }

        static readonly Regex Token = new Regex(@"\[\[ID:(\d+)\]\]", RegexOptions.Compiled);
        static readonly Regex Header = new Regex(@"AIMP\s*v(?<v>\d+\.\d+\.\d+)(?<rest>.{0,24})", RegexOptions.Compiled);
        // Sin límite de palabra al final: en la página el hash puede quedar pegado al texto siguiente
        // («…c4cdPlugins from previous…»). Se toman exactamente 64 caracteres desde «SHA256:».
        static readonly Regex Sha = new Regex(@"SHA-?256\s*:?\s*(?<h>[0-9A-Fa-f]{64})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Texto plano de la página con cada enlace de descarga reemplazado por «[[ID:n]]», para
        /// leerla sin depender del diseño HTML.
        /// </summary>
        public static string Flatten(string html)
        {
            // La página viene de internet: se limita el tamaño y los bloques <script>/<style> se
            // quitan recorriendo el texto (una expresión con retroceso sobre una página
            // malformada puede tardar muchísimo).
            string s = html ?? "";
            if (s.Length > MaxPage) s = s.Substring(0, MaxPage);
            s = StripBlocks(StripBlocks(s, "script"), "style");
            s = ReplaceDownloadLinks(s);
            s = StripTags(s);
            s = WebUtility.HtmlDecode(s);
            return Regex.Replace(s, @"\s+", " ");
        }

        internal const int MaxPage = 4 * 1024 * 1024;

        static readonly Regex DownloadId = new Regex(@"do=download\.file(?:&amp;|&)id=(\d{1,9})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Quita las etiquetas en un solo recorrido. «&lt;[^&gt;]+&gt;» sobre una página con muchas
        /// etiquetas sin cerrar recorría el resto de la página desde cada «&lt;» (tiempo cuadrático).
        /// Una etiqueta termina en el primer «&gt;»; si antes aparece otro «&lt;» o pasan 4000
        /// caracteres, el «&lt;» se deja como texto.
        /// </summary>
        internal static string StripTags(string s)
        {
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c != '<') { sb.Append(c); i++; continue; }
                int end = -1, limit = Math.Min(s.Length, i + 4000);
                for (int j = i + 1; j < limit; j++)
                {
                    if (s[j] == '>') { end = j; break; }
                    if (s[j] == '<') break;
                }
                if (end < 0) { sb.Append(c); i++; continue; }
                sb.Append(' ');
                i = end + 1;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Cada etiqueta &lt;a …&gt; cuyo destino es una descarga se reemplaza por «[[ID:n]]».
        /// Recorrido lineal: se busca «&lt;a», se toma la etiqueta hasta «&gt;» (como mucho 2000
        /// caracteres) y sólo sobre ese pedazo se aplica la expresión. Con una expresión sobre toda
        /// la página, 100.000 etiquetas sin cerrar tardaban minutos.
        /// </summary>
        static string ReplaceDownloadLinks(string s)
        {
            var sb = new StringBuilder(s.Length);
            int pos = 0;
            while (pos < s.Length)
            {
                int a = s.IndexOf("<a", pos, StringComparison.OrdinalIgnoreCase);
                if (a < 0) { sb.Append(s, pos, s.Length - pos); break; }
                bool isTag = a + 2 < s.Length && (char.IsWhiteSpace(s[a + 2]) || s[a + 2] == '>');
                int gt = -1;
                if (isTag)
                    for (int j = a + 2, lim = Math.Min(s.Length, a + 2000); j < lim; j++)
                    {
                        if (s[j] == '>') { gt = j; break; }
                        if (s[j] == '<') break;   // etiqueta sin cerrar: no se sigue buscando
                    }
                if (gt < 0) { sb.Append(s, pos, a + 2 - pos); pos = a + 2; continue; }
                var m = DownloadId.Match(s.Substring(a, gt - a));
                sb.Append(s, pos, a - pos);
                if (m.Success) sb.Append(" [[ID:").Append(m.Groups[1].Value).Append("]] ");
                else sb.Append(s, a, gt + 1 - a);
                pos = gt + 1;
            }
            return sb.ToString();
        }

        /// <summary>Quita los bloques &lt;tag …&gt;…&lt;/tag&gt; en un recorrido lineal.</summary>
        internal static string StripBlocks(string s, string tag)
        {
            var sb = new StringBuilder(s.Length);
            int pos = 0;
            string open = "<" + tag, close = "</" + tag;
            while (pos < s.Length)
            {
                int a = s.IndexOf(open, pos, StringComparison.OrdinalIgnoreCase);
                if (a < 0) { sb.Append(s, pos, s.Length - pos); break; }
                sb.Append(s, pos, a - pos).Append(' ');
                int b = s.IndexOf(close, a + open.Length, StringComparison.OrdinalIgnoreCase);
                if (b < 0) break;                               // bloque sin cerrar: se descarta el resto
                int gt = s.IndexOf('>', b);
                pos = gt < 0 ? s.Length : gt + 1;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Instaladores estables de la página de AIMP: por cada sección «AIMP vX.Y.Z» que no sea
        /// beta, cada enlace de descarga con su arquitectura y el SHA-256 publicado al lado.
        /// Los enlaces sin hash (la versión portable) se ignoran.
        /// </summary>
        public static List<VendorDownload> ParseDownloads(string html)
        {
            var result = new List<VendorDownload>();
            string t = Flatten(html);
            var heads = Header.Matches(t);
            for (int h = 0; h < heads.Count; h++)
            {
                if (PreRelease.IsMatch(heads[h].Groups["rest"].Value)) continue;
                int start = heads[h].Index, end = h + 1 < heads.Count ? heads[h + 1].Index : t.Length;
                string version = heads[h].Groups["v"].Value;
                string section = t.Substring(start, end - start);
                // la página cambió el orden (antes: enlace → «64-bit» → hash; ahora: «32-bit» → tamaño → hash → «sin instalador»
                // → enlace). Con el orden viejo se tomaba el enlace de 32 bits como el de 64 y el hash no coincidía (grabación del
                // usuario). Ahora cada arquitectura se arma desde su encabezado y el enlace se reconoce por el tamaño que dice.
                var byArch = ParseByArchBlocks(section, version);
                if (byArch.Count > 0) { result.AddRange(byArch); continue; }
                result.AddRange(ParseByLinks(section, version));
            }
            return result;
        }

        static readonly Regex ArchMark = new Regex(@"(?<!\d)(?<a>64|32)-bit|\b(?<arm>ARM64|ARM)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex FileSize = new Regex(@"File\s*Size\s*:?\s*(?<s>\d+(?:[.,]\d+)?)\s*(?<u>[KMG]B)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex InstallerLink = new Regex(@"\[\[ID:(?<id>\d+)\]\]\s*Download\s*\((?<s>\d+(?:[.,]\d+)?)\s*(?<u>[KMG]B)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Cada arquitectura desde su encabezado («32-bit», «64-bit») hasta el siguiente: su hash y su tamaño; el instalador es el
        /// enlace «Download (tamaño)» de esa sección con el mismo tamaño (no el «build without the installer»). Vacío si la página
        /// no tiene tamaños (se usa la lectura por enlaces).
        /// </summary>
        static List<VendorDownload> ParseByArchBlocks(string section, string version)
        {
            var list = new List<VendorDownload>();
            var marks = ArchMark.Matches(section);
            var links = InstallerLink.Matches(section);
            if (marks.Count == 0 || links.Count == 0) return list;
            for (int i = 0; i < marks.Count; i++)
            {
                int from = marks[i].Index, to = i + 1 < marks.Count ? marks[i + 1].Index : section.Length;
                string block = section.Substring(from, to - from);
                var sm = Sha.Match(block);
                var size = FileSize.Match(block);
                if (!sm.Success || !size.Success) continue;
                string arch = marks[i].Groups["a"].Value == "64" ? "x64" : marks[i].Groups["a"].Value == "32" ? "x86" : "arm64";
                string wanted = (size.Groups["s"].Value.Replace(',', '.') + size.Groups["u"].Value).ToUpperInvariant();
                string id = null;
                foreach (Match l in links)
                {
                    string got = (l.Groups["s"].Value.Replace(',', '.') + l.Groups["u"].Value).ToUpperInvariant();
                    if (got != wanted) continue;
                    if (id != null && id != l.Groups["id"].Value) { id = null; break; }    // dos enlaces distintos del mismo tamaño: no se adivina
                    id = l.Groups["id"].Value;
                }
                if (id == null || list.Exists(delegate(VendorDownload x) { return x.Arch == arch; })) continue;
                list.Add(new VendorDownload { Version = version, Arch = arch, Id = id, Sha256 = sm.Groups["h"].Value.ToLowerInvariant() });
            }
            // Dos arquitecturas con el mismo enlace: la página no se entendió.
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                    if (list[i].Id == list[j].Id) return new List<VendorDownload>();
            return list;
        }

        /// <summary>La lectura anterior (enlace seguido de su arquitectura y su hash), para páginas sin tamaños.</summary>
        static List<VendorDownload> ParseByLinks(string section, string version)
        {
            var result = new List<VendorDownload>();
            var tokens = Token.Matches(section);
            for (int k = 0; k < tokens.Count; k++)
            {
                string id = tokens[k].Groups[1].Value;
                if (k > 0 && tokens[k - 1].Groups[1].Value == id) continue;    // el mismo enlace repetido
                int segEnd = section.Length;
                for (int j = k + 1; j < tokens.Count; j++)
                    if (tokens[j].Groups[1].Value != id) { segEnd = tokens[j].Index; break; }
                string seg = section.Substring(tokens[k].Index, segEnd - tokens[k].Index);
                var sm = Sha.Match(seg);
                if (!sm.Success) continue;
                // También pegado: «64-bitFile Size».
                string arch = Regex.IsMatch(seg, @"(?<!\d)64-bit", RegexOptions.IgnoreCase) ? "x64"
                            : Regex.IsMatch(seg, @"(?<!\d)32-bit", RegexOptions.IgnoreCase) ? "x86"
                            : Regex.IsMatch(seg, @"\bARM", RegexOptions.IgnoreCase) ? "arm64" : "";
                if (arch.Length == 0) continue;
                result.Add(new VendorDownload { Version = version, Arch = arch, Id = id, Sha256 = sm.Groups["h"].Value.ToLowerInvariant() });
            }
            return result;
        }

        public static VendorDownload Pick(List<VendorDownload> downloads, string version, string arch)
        {
            foreach (var d in downloads)
                if (d.Arch == arch && VersionUtil.Compare(d.Version, version) == 0) return d;
            return null;
        }

        public static string DownloadUrl(VendorDownload d)
        {
            return "https://www.aimp.ru/?do=download.file&id=" + d.Id;
        }

        /// <summary>Dirección directa del instalador (la que usa el propio sitio al redirigir).</summary>
        public static string DirectUrl(VendorDownload d)
        {
            return "https://www.aimp.ru/files/desktop/builds/aimp_" + d.Version + (d.Arch == "x86" ? "_w32" : "_w64") + ".exe";
        }
    }
}
