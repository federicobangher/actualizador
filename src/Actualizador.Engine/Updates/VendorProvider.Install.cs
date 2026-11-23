// Fabricantes: instalación verificada (catálogo y GitHub, Adobe, Office y AIMP).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    public sealed partial class VendorProvider
    {
        public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed)
        {
            Http.EnableModernTls();
            foreach (var c in items)
            {
                if (ctx.CancelRequested) { c.Status = UpdateStatus.Pendiente; c.Result = "Cancelada antes de empezar"; changed(c); continue; }
                var plan = c.ProviderTag as VendorPlan;
                if (plan == null) { c.Status = UpdateStatus.Fallida; c.Result = "No hay un instalador verificable para este programa"; changed(c); continue; }
                c.Status = UpdateStatus.Instalando;
                c.Result = "";
                changed(c);
                try
                {
                    VendorInstall kind = plan.Check != null ? plan.Check.Install : VendorInstall.SignedInstaller;
                    switch (kind)
                    {
                        case VendorInstall.PageHash: InstallAimp(ctx, c, plan, changed); break;
                        case VendorInstall.SignedInstaller:
                            InstallSigned(ctx, c, plan, changed);
                            OpenPageInstead(ctx, c, plan);
                            break;
                        case VendorInstall.AdobeMsp: InstallAdobe(ctx, c, plan, changed); break;
                        case VendorInstall.OfficeClickToRun: InstallOffice(ctx, c, plan, changed); break;
                        case VendorInstall.Starter4j: InstallStarter(ctx, c, plan, changed); break;
                        default: InstallerSupport.Fail(c, "Este programa se actualiza a mano: " + (plan.Check != null ? plan.Check.HowToUpdate : "")); break;
                    }
                }
                catch (OperationCanceledByUserException) { c.Status = UpdateStatus.Fallida; c.Result = "Cancelada durante la actualización"; }
                catch (Exception ex)
                {
                    Log.Error("Actualización de " + c.Name, ex);
                    c.Status = UpdateStatus.Fallida;
                    c.Result = Http.Describe(ex);
                    // 2.0.17: la descarga falló (el archivo ya no está en esa dirección): de la página oficial, se abre la página.
                    try { OpenPageInstead(ctx, c, plan); } catch (Exception ox) { Log.Quiet("Abrir la página oficial", ox); }
                }
                changed(c);
            }
        }

        /// <summary>Sólo para las pruebas: en vez de abrir el navegador.</summary>
        internal static Action<string> OpenPageForTests;

        /// <summary>
        /// 2.0.17: una fila que salió de la página oficial del programa y no se pudo instalar desde acá ANTES de ejecutar nada (no se
        /// encontró el archivo, no se pudo verificar, no hay cómo instalarlo en silencio): se abre su página de descarga, como
        /// hacía antes para todas, y la fila dice por qué. Si el instalador llegó a ejecutarse y falló, queda el error.
        /// </summary>
        void OpenPageInstead(UpdateContext ctx, UpdateCandidate c, VendorPlan plan)
        {
            if (plan == null || plan.Check != null || string.IsNullOrEmpty(plan.SitePage) || c.Status != UpdateStatus.Fallida || !plan.Locating || plan.InstallerRan) return;
            // Desde la consola o la revisión programada no se abre el navegador: queda el motivo en la fila (y el código de salida).
            if (ctx.CancelRequested || Unattended) return;
            var policy = UpdateManager.PolicyForTests ?? Policy.Current;
            if (!policy.AllowOpenPages || !policy.IsSourceAllowed(SiteProvider.ProviderId)) return;
            string url = Fmt.WebUrl(plan.SitePage);
            Uri u;
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out u) || !SiteVersions.IsPublicPage(u)) return;
            string why = (c.Result ?? "").Trim().TrimEnd('.');
            try
            {
                if (OpenPageForTests != null) OpenPageForTests(url); else ProcessRunner.OpenForUser(url);
                c.Status = UpdateStatus.Pendiente;
                c.Result = "No se pudo instalar sola" + (why.Length > 0 ? " (" + char.ToLowerInvariant(why[0]) + why.Substring(1) + ")" : "") + ". Se abrió su página (" + u.Host +
                           "): descargá e instalá la " + plan.Latest + " desde ahí; después tocá «Buscar actualizaciones» para confirmarla";
                Log.Info(c.Name + ": " + c.Result);
            }
            catch (Exception ex) { Log.Warn("No se pudo abrir " + u.Host + ": " + ex.Message); }
        }

        /// <summary>
        /// ¿Se ofrece cerrar este programa antes de instalarlo? Todos los que tienen procesos conocidos,
        /// Office incluido: con «forceappshutdown=false» su actualizador baja la versión nueva pero no la
        /// aplica mientras quede algo abierto, y Skype Empresarial (lync.exe, que trae Office 2016–2021 y
        /// arranca con Windows) o «Enviar a OneNote» quedan en segundo plano sin que el usuario lo sepa.
        /// </summary>
        public static bool ClosesProcesses(VendorPlan plan)
        {
            return plan != null && plan.Processes != null && plan.Processes.Length > 0;
        }

        /// <summary>
        /// El programa tiene que estar cerrado para instalar. Si está abierto y el usuario aceptó
        /// cerrarlo, se cierra; si no (o no se pudo), sin modo interactivo es un error con el motivo.
        /// null si se puede seguir.
        /// </summary>
        string EnsureClosed(UpdateContext ctx, UpdateCandidate c, VendorPlan plan, Action<UpdateCandidate> changed)
        {
            // 2.0.19: TLauncher (arranque starter4j): su ventana y lo que corre desde la carpeta del arranque (su Java). Abierto, al cerrarse
            // volvería a escribir starter.json con la versión vieja.
            if (plan.Check != null && plan.Check.Install == VendorInstall.Starter4j)
            {
                var open = RunningPrograms.Closable(StarterRunning(RunningPrograms.Snapshot(), plan));
                if (open.Count == 0) return null;
                string names = RunningPrograms.Names(open);
                if (!CloseRunning) return names + " está abierto: cerralo y reintentá (o aceptá cerrarlo cuando Actualizador lo pregunte)";
                ctx.Report("Cerrando " + c.Name + " (" + names + ") para poder actualizarlo…");
                InstallerSupport.Progress(c, changed, "Cerrando el programa abierto (" + names + ")…");
                var still = RunningPrograms.Close(open, 15000);
                if (still.Count > 0) return RunningPrograms.Names(still) + " sigue abierto: cerralo y reintentá";
                return null;
            }
            // 2.0.18: de la página oficial, lo abierto desde la carpeta del programa (no sólo el ejecutable principal: CrystalDiskInfo queda
            // en el área de notificación con otro proceso). Igual que con winget.
            if (plan.Check == null && plan.SitePage != null && plan.Program != null)
            {
                string folder = RunningPrograms.FolderOf(plan.Program);
                if (folder.Length > 0)
                {
                    var open = RunningPrograms.Closable(RunningPrograms.In(RunningPrograms.Snapshot(), folder));
                    if (open.Count > 0)
                    {
                        string names = RunningPrograms.Names(open);
                        if (!CloseRunning) return Interactive ? null : names + " está abierto: cerralo y reintentá (o aceptá cerrarlo cuando Actualizador lo pregunte)";
                        ctx.Report("Cerrando " + c.Name + " (" + names + ") para poder actualizarlo…");
                        InstallerSupport.Progress(c, changed, "Cerrando el programa abierto (" + names + ")…");
                        var still = RunningPrograms.Close(open, 15000);
                        if (still.Count > 0) return RunningPrograms.Names(still) + " sigue abierto (puede estar preguntando si guardar algo): cerralo y reintentá";
                    }
                    return null;
                }
            }
            string running = FirstRunning(plan.Processes);
            if (running == null) return null;
            if (CloseRunning && ClosesProcesses(plan))
            {
                var open = RunningPrograms.Closable(RunningPrograms.ByName(RunningPrograms.Snapshot(), plan.Processes));
                if (open.Count > 0)
                {
                    string names = RunningPrograms.Names(open);
                    ctx.Report("Cerrando " + c.Name + " (" + names + ") para poder actualizarlo…");
                    InstallerSupport.Progress(c, changed, "Cerrando el programa abierto (" + names + ")…");
                    var still = RunningPrograms.Close(open, 15000);
                    if (still.Count > 0) return RunningPrograms.Names(still) + " sigue abierto (puede estar preguntando si guardar algo): cerralo y reintentá";
                }
                running = FirstRunning(plan.Processes);
                if (running == null) return null;
            }
            if (!Interactive) return running + " está abierto: cerralo y reintentá (o marcá «Mostrar la ventana de cada instalador»)";
            return null;
        }

        /// <summary>El primer proceso de la lista que está abierto, o null.</summary>
        static string FirstRunning(string[] names)
        {
            if (names == null) return null;
            foreach (string n in names)
            {
                try
                {
                    var ps = Process.GetProcessesByName(n);
                    bool any = ps.Length > 0;
                    foreach (var p in ps) p.Dispose();
                    if (any) return n + ".exe";
                }
                catch (Exception qx) { Log.Quiet("Procesos abiertos del fabricante", qx); }
            }
            return null;
        }

        /// <summary>Sólo para las pruebas: en vez de bajar el archivo (dirección, destino).</summary>
        internal static Action<string, string> DownloadForTests;
        /// <summary>Sólo para las pruebas: la firma de un archivo descargado (archivo → resultado de WinVerifyTrust y sujeto del certificado).</summary>
        internal static Func<string, KeyValuePair<int, string>> SignatureForTests;

        static int VerifyStrict(string file, out string note)
        {
            if (SignatureForTests != null) { note = null; return SignatureForTests(file).Key; }
            return Native.VerifyAuthenticodeStrict(file, out note);
        }

        static string SubjectOf(string file)
        {
            return SignatureForTests != null ? SignatureForTests(file).Value : Native.SignerSubject(file);
        }

        static void Download(UpdateContext ctx, UpdateCandidate c, Action<UpdateCandidate> changed, string url, string file, Func<Uri, bool> allowed)
        {
            string host = new Uri(url).Host;
            ctx.Report("Descargando " + c.Name + " desde " + host + "…");
            InstallerSupport.Progress(c, changed, "Descargando de " + host + "…");
            if (DownloadForTests != null)
            {
                if (!allowed(new Uri(url))) throw new InvalidOperationException("dirección no permitida: " + host);
                DownloadForTests(url, file);
                return;
            }
            int lastShown = -1;
            Http.Download(url, file, 45 * 60 * 1000, ctx, allowed, delegate(int pct, long bytes)
            {
                if (pct >= 0 && pct == lastShown) return;
                lastShown = pct;
                string text = pct >= 0 ? pct + " %" : (bytes / 1048576) + " MB";
                ctx.Report("Descargando " + c.Name + "… " + text);
                InstallerSupport.Progress(c, changed, "Descargando: " + text);
            });
        }

        /// <summary>El nombre original del archivo (algunos instaladores se configuran por su nombre), si es seguro.</summary>
        internal static string FileNameFor(string url, string fallback)
        {
            string name = InstallerSupport.RawLastSegment(url);
            return InstallerSupport.IsSafeFileName(name) &&
                   (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".msp", StringComparison.OrdinalIgnoreCase)) ? name : fallback;
        }

        /// <summary>
        /// Firma del archivo descargado: válida, no revocada y del fabricante esperado (catálogo) o del
        /// mismo firmante que el programa instalado (GitHub). null si se acepta; si no, el motivo.
        /// </summary>
        static string CheckSignature(UpdateContext ctx, UpdateCandidate c, VendorPlan plan, string file, out string how)
        {
            how = null;
            string note;
            int sig = VerifyStrict(file, out note);
            if (note != null) Log.Warn(c.Name + ": " + note);
            if (sig == Native.CERT_E_REVOKED) return "el certificado que firma el instalador fue REVOCADO por su emisor: no se instala";
            if (sig != 0) return "el instalador no tiene una firma digital válida (" + Fmt.Hex(sig) + "): no se instala";
            string subject;
            try { subject = SubjectOf(file); }
            catch (Exception ex) { return "no se pudo leer la firma del instalador (" + ex.Message + "): no se instala"; }
            Log.Info(c.Name + ": firmante del instalador: " + subject);
            if (plan.Check != null && plan.Check.Signers != null && plan.Check.Signers.Length > 0)
            {
                if (!InstallerRules.SignerAccepted(subject, plan.Check.Signers))
                {
                    var names = WingetRules.SignerNames(subject);
                    return "el instalador está firmado por «" + (names.Count > 0 ? names[0] : subject) + "», no por " + plan.Check.Signers[0] + ": no se instala";
                }
                how = "firmado por " + plan.Check.Signers[0];
                return null;
            }
            // Programa de GitHub: el mismo firmante que lo instalado. Elevado, ni las carpetas que puede escribir un usuario
            // común ni el editor de una entrada del usuario (HKCU) sirven de referencia (S20/S30): antes se tomaba el
            // editor del registro tal cual, y un programa sin privilegios podía registrar una entrada con el nombre de su
            // propio certificado y un repositorio suyo para que su instalador corriera como administrador.
            // 2.0.20: si el instalador se ejecuta como el usuario (RunAsUser), lo del usuario sí sirve de referencia: no gana permisos.
            var signers = WingetDirect.InstalledSigners(ctx, plan.Program, plan.RunAsUser);
            string publisher = plan.Program != null ? WingetDirect.TrustedRegistryPublisher(ctx, plan.Program, plan.RunAsUser) : (ctx.IsAdmin ? "" : (c.Publisher ?? ""));
            if (signers.Count == 0 && publisher.Length == 0 && ctx.IsAdmin && !plan.RunAsUser && plan.Program != null && IsPerUser(plan.Program))
                return "el programa está instalado sólo para tu usuario y Actualizador se está ejecutando como administrador: por seguridad, la firma del " +
                       "instalador no se compara con datos que cualquier programa de tu usuario puede cambiar. Actualizalo con winget, desde el propio programa " +
                       "o abriendo Actualizador sin «Ejecutar como administrador»";
            string reason = WingetRules.MatchSigner(subject, signers, publisher, out how);
            return reason != null ? "el instalador " + reason + ": no se instala" : null;
        }

        /// <summary>
        /// 2.0.12: ¿se acepta un instalador SIN firma? Sólo con <see cref="VendorCheck.UnsignedWithPublishedHash"/>, si no tiene
        /// ninguna firma (no una inválida o revocada), su SHA-256 coincidió con el publicado por el autor y su producto es este
        /// (para un .msi no se lee: llega vacío y vale el hash). Público para las pruebas.
        /// </summary>
        public static bool UnsignedAccepted(VendorCheck check, int signatureStatus, bool hashMatched, string installerProduct)
        {
            if (check == null || !check.UnsignedWithPublishedHash || !hashMatched || string.IsNullOrEmpty(check.HashRepo)) return false;
            if (signatureStatus != Native.TRUST_E_NOSIGNATURE) return false;
            if (installerProduct == null) return false;
            string prod = installerProduct.Trim();
            if (prod.Length == 0) return true;   // .msi: el SHA-256 publicado ya identifica el archivo
            return SiteVersions.Norm(prod).StartsWith(SiteVersions.Norm(check.Product), StringComparison.Ordinal);
        }

        /// <summary>Sólo para las pruebas: firma de lo instalado (-1 no se sabe, 0 sin firma, 1 firmado), propiedades del .msi y productos relacionados.</summary>
        internal static Func<InstalledProgram, int> InstalledSignedForTests;
        internal static Func<string, Dictionary<string, string>> MsiPropertiesForTests;
        internal static Func<string, List<string>> MsiRelatedForTests;

        /// <summary>null si el instalador SIN firma de un repositorio de winget se acepta; si no, el motivo (2.0.13).</summary>
        internal static string UnsignedRelease(UpdateContext ctx, VendorPlan plan, string file, bool msi, bool hashOk)
        {
            var p = plan.Program;
            if (p == null) return "no se encontró su entrada en «Aplicaciones» para compararlo: no se instala";
            int installedSigned = InstalledSignedForTests != null ? InstalledSignedForTests(p) : InstalledSignatureState(ctx, p);
            Dictionary<string, string> props = null;
            List<string> related = null;
            string exeProduct = "";
            if (msi)
            {
                props = MsiPropertiesForTests != null ? MsiPropertiesForTests(file) : Native.MsiProperties(file);
                string up;
                if (props.TryGetValue("UpgradeCode", out up) && up.Length > 0)
                    related = MsiRelatedForTests != null ? MsiRelatedForTests(up) : Native.MsiRelatedProducts(up);
            }
            else exeProduct = SiteVersions.ProductOf(file);
            return UnsignedReleaseVerdict(hashOk, installedSigned, msi, props, related, ProductCodeOf(p), plan.Latest, exeProduct, p);
        }

        /// <summary>
        /// 2.0.13: la regla, sin Windows (pública para las pruebas). Se acepta sólo si: el SHA-256 coincidió con el de GitHub; lo instalado
        /// tampoco está firmado (comprobado, no supuesto); y el paquete es de este producto: un .msi con el mismo UpgradeCode que lo instalado
        /// (o, si lo instalado no es MSI, con su nombre) y exactamente la versión anunciada; un .exe que diga ser de este programa.
        /// </summary>
        public static string UnsignedReleaseVerdict(bool hashOk, int installedSigned, bool msi, Dictionary<string, string> props, List<string> related,
                                                    string installedProductCode, string latest, string exeProduct, InstalledProgram p)
        {
            if (!hashOk) return "el instalador no tiene firma digital y no se pudo comprobar su SHA-256 con el que publica GitHub: no se instala";
            if (installedSigned > 0) return "el programa instalado tiene firma digital y el instalador nuevo no: no se instala";
            if (installedSigned < 0) return "el instalador no tiene firma digital y no se pudo comprobar que lo instalado tampoco la tenga: no se instala";
            string name = p != null ? p.Name : "";
            var matcher = new SiteVersions.NameMatcher(name, p != null ? p.Publisher : "");
            if (!msi)
            {
                if (string.IsNullOrEmpty(exeProduct) || !matcher.In(SiteVersions.Norm(exeProduct)))
                    return "el instalador no tiene firma digital ni dice ser de «" + Fmt.Truncate(name, 60) + "»: no se instala";
                return null;
            }
            string pv, up, pn;
            if (props == null || !props.TryGetValue("ProductVersion", out pv) || !props.TryGetValue("UpgradeCode", out up) || up.Length == 0)
                return "no se pudo leer el paquete MSI (sin firma digital) para saber de qué producto es: no se instala";
            if (VersionUtil.Parse(pv) == null || VersionUtil.Compare(pv, latest) != 0)
                return "el paquete MSI dice ser la versión " + pv + ", no la " + latest + " que anuncia GitHub: no se instala";
            if (!string.IsNullOrEmpty(installedProductCode))
            {
                bool same = related != null && related.Exists(delegate(string x) { return x.Equals(installedProductCode, StringComparison.OrdinalIgnoreCase); });
                if (!same) return "el paquete MSI no es de la misma familia de producto que lo instalado (otro UpgradeCode): no se instala";
                return null;
            }
            props.TryGetValue("ProductName", out pn);
            if (string.IsNullOrEmpty(pn) || !matcher.In(SiteVersions.Norm(pn)))
                return "el paquete MSI es de «" + Fmt.Truncate(pn ?? "", 60) + "», no de «" + Fmt.Truncate(name, 60) + "»: no se instala";
            return null;
        }

        /// <summary>
        /// 2.0.17: ¿se acepta un instalador SIN firma bajado de la página oficial del programa? (el autor no firma: ni lo instalado ni el
        /// instalador). Sólo si la política lo permite; lo instalado tampoco tiene firma (comprobado, no supuesto); la página de donde
        /// salió el enlace se leyó por HTTPS (o la dirección sale del manifiesto de winget); y el paquete es de este producto y
        /// EXACTAMENTE de la versión publicada: un .msi con el mismo UpgradeCode que lo instalado (o su nombre) y esa versión; un .exe
        /// que dice ser de este programa y de esa versión. Es el mismo archivo que bajaría el usuario a mano de esa página. null si
        /// se acepta; si no, el motivo. Pública para las pruebas.
        /// </summary>
        public static string UnsignedSiteVerdict(bool policyAllows, int installedSigned, bool elevatedPerUser, bool trustedPath, bool msi,
                                                 Dictionary<string, string> props, List<string> related, string installedProductCode,
                                                 string latest, string exeProduct, string exeVersion, InstalledProgram p)
        {
            if (!policyAllows) return "el instalador no tiene firma digital y la política de la empresa no permite instaladores sin firma: no se instala";
            if (installedSigned > 0) return "el programa instalado tiene firma digital y el instalador nuevo no: no se instala";
            if (installedSigned < 0) return "el instalador no tiene firma digital y no se pudo comprobar que lo instalado tampoco la tenga: no se instala";
            if (elevatedPerUser) return "el instalador no tiene firma digital y el programa está instalado sólo para tu usuario: como administrador no se instala";
            if (!trustedPath) return "el instalador no tiene firma digital y su página no se leyó por una conexión segura (HTTPS): no se instala";
            string name = p != null ? p.Name : "";
            var matcher = new SiteVersions.NameMatcher(name, p != null ? p.Publisher : "");
            if (!msi)
            {
                if (string.IsNullOrEmpty(exeProduct) || !(matcher.In(SiteVersions.Norm(exeProduct)) || matcher.InFileName(exeProduct)))
                    return "el instalador no tiene firma digital ni dice ser de «" + Fmt.Truncate(name, 60) + "»: no se instala";
                if (VersionUtil.Parse(exeVersion) == null || VersionUtil.Compare(exeVersion, latest) != 0)
                    return "el instalador no tiene firma digital y no dice ser de la versión " + latest +
                           (VersionUtil.Parse(exeVersion) != null ? " (dice " + VersionUtil.Normalize(exeVersion) + ")" : "") + ": no se instala";
                return null;
            }
            string pv, up, pn;
            if (props == null || !props.TryGetValue("ProductVersion", out pv) || !props.TryGetValue("UpgradeCode", out up) || up.Length == 0)
                return "no se pudo leer el paquete MSI (sin firma digital) para saber de qué producto es: no se instala";
            if (VersionUtil.Parse(pv) == null || VersionUtil.Compare(pv, latest) != 0)
                return "el paquete MSI (sin firma digital) dice ser la versión " + pv + ", no la " + latest + " que publica la página: no se instala";
            if (!string.IsNullOrEmpty(installedProductCode))
            {
                bool same = related != null && related.Exists(delegate(string x) { return x.Equals(installedProductCode, StringComparison.OrdinalIgnoreCase); });
                return same ? null : "el paquete MSI (sin firma digital) no es de la misma familia de producto que lo instalado (otro UpgradeCode): no se instala";
            }
            props.TryGetValue("ProductName", out pn);
            if (string.IsNullOrEmpty(pn) || !matcher.In(SiteVersions.Norm(pn)))
                return "el paquete MSI (sin firma digital) es de «" + Fmt.Truncate(pn ?? "", 60) + "», no de «" + Fmt.Truncate(name, 60) + "»: no se instala";
            return null;
        }

        /// <summary>Sólo para las pruebas: la versión que dice un ejecutable descargado.</summary>
        internal static Func<string, string> ExeVersionForTests;

        /// <summary>null si el instalador SIN firma de la página oficial se acepta (<see cref="UnsignedSiteVerdict"/>); si no, el motivo.</summary>
        static string UnsignedSite(UpdateContext ctx, VendorPlan plan, string file, bool msi)
        {
            var p = plan.Program;
            if (p == null) return "no se encontró su entrada en «Aplicaciones» para compararlo: no se instala";
            var policy = UpdateManager.PolicyForTests ?? Policy.Current;
            int installedSigned = InstalledSignedForTests != null ? InstalledSignedForTests(p) : InstalledSignatureState(ctx, p);
            Dictionary<string, string> props = null;
            List<string> related = null;
            string exeProduct = "", exeVersion = "";
            if (msi)
            {
                props = MsiPropertiesForTests != null ? MsiPropertiesForTests(file) : Native.MsiProperties(file);
                string up;
                if (props.TryGetValue("UpgradeCode", out up) && up.Length > 0)
                    related = MsiRelatedForTests != null ? MsiRelatedForTests(up) : Native.MsiRelatedProducts(up);
            }
            else
            {
                exeProduct = SiteVersions.ProductOf(file);
                exeVersion = ExeVersionForTests != null ? ExeVersionForTests(file) : ExeVersion(file);
            }
            // De dónde salió la dirección: del manifiesto de winget (revisado por sus moderadores) o de una página leída por HTTPS.
            bool trusted = plan.Lead != null && plan.Lead.Origin != InstallerLead.Page;
            if (!trusted)
            {
                Uri page;
                string from = plan.Lead != null && plan.Lead.FoundAt.Length > 0 ? plan.Lead.FoundAt : plan.SitePage;
                trusted = Uri.TryCreate(from ?? "", UriKind.Absolute, out page) && page.Scheme == Uri.UriSchemeHttps;
            }
            return UnsignedSiteVerdict(policy.AllowUnsignedSiteInstallers, installedSigned, ctx.IsAdmin && IsPerUser(p) && !plan.RunAsUser, trusted, msi, props, related,
                                       ProductCodeOf(p), plan.Latest, exeProduct, exeVersion, p);
        }

        static readonly System.Text.RegularExpressions.Regex GuidKey = new System.Text.RegularExpressions.Regex(@"\\(?<g>\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})$");

        /// <summary>El ProductCode de un programa instalado con Windows Installer (la clave de «Desinstalar» es su GUID), o null.</summary>
        internal static string ProductCodeOf(InstalledProgram p)
        {
            if (p == null) return null;
            var m = GuidKey.Match((p.RegistryKey ?? "").Trim());
            return m.Success ? m.Groups["g"].Value : null;
        }

        /// <summary>
        /// ¿Lo instalado está firmado? 1 si algún ejecutable de su carpeta tiene firma (válida o no); 0 si se revisó alguno y ninguno tiene;
        /// -1 si no se puede saber (sin carpeta, o elevado con una carpeta que puede escribir un usuario común).
        /// </summary>
        static int InstalledSignatureState(UpdateContext ctx, InstalledProgram p)
        {
            var dirs = new List<string>();
            string loc = (p.InstallLocation ?? "").Trim().Trim('"').TrimEnd('\\');
            if (loc.Length > 0) dirs.Add(loc);
            string un = DirOfUninstaller(p.UninstallString).TrimEnd('\\');
            if (un.Length > 0 && !dirs.Exists(delegate(string d) { return d.Equals(un, StringComparison.OrdinalIgnoreCase); })) dirs.Add(un);
            int unsigned = 0;
            foreach (string d in dirs)
            {
                try
                {
                    if (!Directory.Exists(d) || SafePaths.IsUnderWindows(d)) continue;
                    if (ctx.IsAdmin && SafePaths.IsUserWritableLocation(d)) return -1;
                    int n = 0;
                    foreach (string f in Directory.GetFiles(d, "*.exe", SearchOption.TopDirectoryOnly))
                    {
                        if (++n > 30) break;
                        int sig = Native.VerifyAuthenticode(f);
                        if (sig == Native.TRUST_E_NOSIGNATURE) unsigned++;
                        else return 1;          // firmado, o con una firma que no valida: se exige firma
                    }
                }
                catch (Exception ex) { Log.Warn("Firma de lo instalado en " + d + ": " + ex.Message); return -1; }
            }
            return unsigned > 0 ? 0 : -1;
        }

        /// <summary>Sólo para las pruebas: en vez de ejecutar el instalador verificado (ejecutable, argumentos).</summary>
        internal static Func<string, string, ProcResult> RunInstallerForTests;

        /// <summary>
        /// Sin nadie delante de la pantalla (consola, revisión programada): un instalador sin modo silencioso no se abre con su
        /// ventana, porque quedaría esperando un clic para siempre.
        /// </summary>
        public bool Unattended;

        /// <summary>¿El archivo empieza como un ejecutable de Windows o un paquete MSI? (no una página web que llegó en su lugar)</summary>
        static bool LooksLikeInstaller(string file)
        {
            try
            {
                using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    return InstallerRules.Detect(s) == InstallerKind.Msi || IsMz(s);
            }
            catch (Exception qx) { Log.Quiet("Tipo del archivo descargado", qx); return true; }
        }

        static bool IsMz(FileStream hold)
        {
            hold.Position = 0;
            int a = hold.ReadByte(), b = hold.ReadByte();
            hold.Position = 0;
            return a == 'M' && b == 'Z';
        }

        void InstallSigned(UpdateContext ctx, UpdateCandidate c, VendorPlan plan, Action<UpdateCandidate> changed)
        {
            var check = plan.Check;
            // 2.0.20: sólo del usuario y Actualizador elevado: el instalador se ejecuta como el usuario.
            if (!plan.RunAsUser && InstallsAsUser(ctx.IsAdmin, plan.Program, plan.InstallDir, plan.Machine)) plan.RunAsUser = true;
            string notClosed = EnsureClosed(ctx, c, plan, changed);
            if (notClosed != null) { InstallerSupport.Fail(c, notClosed); return; }
            if (string.IsNullOrEmpty(plan.InstallerUrl)) { InstallerSupport.Fail(c, "No hay un instalador oficial para descargar"); return; }
            // Desde acá y hasta ejecutar el instalador, lo que falle es de la descarga o de la verificación (ver OpenPageInstead).
            plan.Locating = true;
            plan.InstallerRan = false;
            Func<Uri, bool> allowed = check != null ? (Func<Uri, bool>)check.Allows
                                    : plan.SitePage != null ? (Func<Uri, bool>)SiteVersions.IsSecureDownload : GitHubRules.IsDownload;
            string dir = BrowserProvider.NewDownloadDir();
            try
            {
                var lead = plan.Lead;
                string fallbackName = lead != null && lead.IsMsi ? "instalador.msi" : "instalador.exe";
                string file = Path.Combine(dir, lead != null && InstallerSupport.IsSafeFileName(lead.FileName) && FileNameFor("https://x/" + lead.FileName, null) != null
                                                ? lead.FileName : FileNameFor(plan.InstallerUrl, fallbackName));
                Download(ctx, c, changed, plan.InstallerUrl, file, allowed);
                // 2.0.17: el enlace de una página puede pasar por una página de espera («tu descarga empieza en unos segundos»): si lo
                // que llegó no es un instalador, se sigue esa página una vez y se baja el archivo al que lleva.
                if (plan.SitePage != null && !LooksLikeInstaller(file))
                {
                    string why;
                    var probed = SiteInstallers.ProbeFile(plan.InstallerUrl, plan.Latest, plan.Program != null ? new SiteVersions.NameMatcher(plan.Program.Name, plan.Program.Publisher) : null,
                                                          DateTime.UtcNow + SiteInstallers.Budget, out why);
                    if (probed != null && probed.DownloadUrl.Length > 0 && probed.DownloadUrl != plan.InstallerUrl)
                    {
                        Log.Info(c.Name + ": el enlace pasaba por una página intermedia; se baja de " + new Uri(probed.DownloadUrl).Host);
                        try { File.Delete(file); } catch (Exception qx) { Log.Quiet("Borrar la descarga que no era un instalador", qx); }
                        Download(ctx, c, changed, probed.DownloadUrl, file, allowed);
                    }
                }
                using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (hold.Length < 16 * 1024) { InstallerSupport.Fail(c, "La descarga no es un instalador (" + hold.Length + " bytes): no se instala"); return; }
                    var kind = InstallerRules.Detect(hold);
                    if (kind != InstallerKind.Msi && !IsMz(hold)) { InstallerSupport.Fail(c, "La descarga no es un ejecutable de Windows ni un paquete MSI: no se instala"); return; }
                    bool fromCatalog = lead != null && lead.Origin == InstallerLead.Catalog;
                    bool hashOk = false;
                    if (plan.InstallerSha256.Length == 64)
                    {
                        string got = InstallerSupport.Sha256(hold);
                        if (got != plan.InstallerSha256)
                        {
                            Log.Warn(c.Name + ": SHA-256 esperado " + plan.InstallerSha256 + ", obtenido " + got);
                            InstallerSupport.Fail(c, "El SHA-256 de la descarga no coincide con el " +
                                                     (plan.PinnedSha256 ? "del instalador oficial" : fromCatalog ? "del catálogo de winget" : "que publica GitHub") + ": no se instala");
                            return;
                        }
                        hashOk = true;
                    }
                    string how;
                    string bad = CheckSignature(ctx, c, plan, file, out how);
                    // 2.0.17: el manifiesto de winget-pkgs de esta misma versión: con su SHA-256, el archivo es exactamente el que instalaría
                    // winget (que tampoco exige otra cosa). Un certificado revocado, no.
                    if (fromCatalog && hashOk)
                    {
                        string ignoredNote;
                        if (bad == null) how = "SHA-256 del catálogo de winget verificado; " + how;
                        else if (VerifyStrict(file, out ignoredNote) != Native.CERT_E_REVOKED)
                        {
                            Log.Info(c.Name + ": firma no comparable (" + bad + "); se instala por el SHA-256 del catálogo de winget, como haría winget");
                            bad = null;
                            how = "SHA-256 del catálogo de winget verificado";
                        }
                    }
                    else if (fromCatalog) { InstallerSupport.Fail(c, "No se pudo comprobar el SHA-256 del catálogo de winget: no se instala"); return; }
                    // Instalador fijo (Visual C++ 2008–2013): con el SHA-256 exacto, el archivo ES el oficial. Una firma vieja
                    // (SHA-1 de 2011) que este Windows ya no valida no lo invalida; un certificado revocado, sí.
                    if (bad != null && hashOk && plan.PinnedSha256 && VerifyStrict(file, out how) != Native.CERT_E_REVOKED)
                    {
                        Log.Warn(c.Name + ": firma no verificada (" + bad + "); se instala por el SHA-256 fijo del instalador oficial");
                        bad = null;
                        how = "SHA-256 del instalador oficial de Microsoft";
                    }
                    // 2.0.12: el autor no firma sus instaladores (7-Zip): sin NINGUNA firma, vale el SHA-256 publicado por el autor para ese
                    // archivo, y el instalador tiene que decir que es de este producto. Una firma presente pero inválida o revocada, no.
                    bool unsigned = false;
                    if (bad != null && check != null && check.UnsignedWithPublishedHash)
                    {
                        string ignored;
                        int sig = VerifyStrict(file, out ignored);
                        if (UnsignedAccepted(check, sig, hashOk, kind == InstallerKind.Msi ? "" : SiteVersions.ProductOf(file)))
                        {
                            Log.Info(c.Name + ": instalador sin firma digital; se instala por el SHA-256 que publica el autor en github.com/" + check.HashRepo);
                            bad = null;
                            unsigned = true;
                            how = "sin firma digital (el autor no firma); SHA-256 igual al publicado en github.com/" + check.HashRepo;
                        }
                        else if (sig == Native.TRUST_E_NOSIGNATURE && !hashOk)
                            bad = "el instalador no tiene firma digital y no se pudo comprobar su SHA-256 con el que publica el autor: no se instala";
                        else if (sig == Native.TRUST_E_NOSIGNATURE)
                            bad = "el instalador descargado no dice ser de " + check.Product + ": no se instala";
                    }
                    // 2.0.13 (captura del 08/11/2026, Double Commander 1.2.11): el autor no firma, y lo instalado tampoco está firmado. Si el
                    // repositorio es el del manifiesto de winget y el SHA-256 coincide con el de GitHub, vale con que el paquete sea del mismo
                    // producto que lo instalado y de la versión anunciada. Una firma presente pero inválida o revocada, no.
                    if (bad != null && check == null && plan.RepoFromWinget)
                    {
                        string ignored;
                        int sig = VerifyStrict(file, out ignored);
                        if (sig == Native.TRUST_E_NOSIGNATURE)
                        {
                            string verdict = UnsignedRelease(ctx, plan, file, kind == InstallerKind.Msi, hashOk);
                            if (verdict == null)
                            {
                                Log.Info(c.Name + ": instalador sin firma digital (el autor no firma, y lo instalado tampoco); se instala por el SHA-256 de github.com/" + plan.Repo + " y la identidad del paquete");
                                bad = null;
                                how = "sin firma digital, como lo instalado (el autor no firma); SHA-256 igual al publicado en github.com/" + plan.Repo + " y mismo producto";
                            }
                            else bad = verdict;
                        }
                    }
                    // 2.0.17: página oficial, y ni el instalador ni lo instalado tienen firma (el autor no firma): vale si es este producto y
                    // exactamente la versión publicada. Una firma presente pero inválida o revocada, no.
                    if (bad != null && check == null && plan.SitePage != null && !fromCatalog)
                    {
                        string ignored;
                        int sig = VerifyStrict(file, out ignored);
                        if (sig == Native.TRUST_E_NOSIGNATURE)
                        {
                            string verdict = UnsignedSite(ctx, plan, file, kind == InstallerKind.Msi);
                            if (verdict == null)
                            {
                                Log.Info(c.Name + ": instalador sin firma digital (el autor no firma, y lo instalado tampoco); se instala por ser de este producto y de la versión " + plan.Latest + " que publica su página oficial");
                                bad = null;
                                how = "sin firma digital, como lo instalado (el autor no firma); mismo producto y la versión que publica su página oficial";
                            }
                            else bad = verdict;
                        }
                    }
                    if (bad == null && check != null && check.InstallerHasProductVersion) bad = InstallerRules.Downgrade(ExeVersion(file), c.InstalledVersion);
                    // 2.0.17: de la página oficial, nunca un instalador que dice ser de una versión anterior a la instalada.
                    if (bad == null && plan.SitePage != null && kind != InstallerKind.Msi)
                    {
                        string fv = ExeVersionForTests != null ? ExeVersionForTests(file) : ExeVersion(file);
                        if (SiteInstallers.LooksLikeProductVersion(fv, plan.Latest, c.InstalledVersion)) bad = InstallerRules.Downgrade(fv, c.InstalledVersion);
                    }
                    if (unsigned && bad == null && kind == InstallerKind.Msi && plan.Program != null) bad = SiteVersions.ForeignInstaller(file, plan.Program);
                    // Instalador de una página oficial: además, que sea de ESTE programa (el mismo fabricante firma otros).
                    if (bad == null && plan.SitePage != null && kind != InstallerKind.Msi && plan.Program != null) bad = SiteVersions.ForeignInstaller(file, plan.Program);
                    if (bad != null) { InstallerSupport.Fail(c, char.ToUpperInvariant(bad[0]) + bad.Substring(1)); return; }

                    string exe, args;
                    bool visible = false;
                    string log = null;
                    // 2.0.17: con el manifiesto de winget, los mismos interruptores que usaría winget (silencioso, Custom y Upgrade).
                    WingetInstaller manifest = lead != null && lead.Winget != null && lead.Winget.IsMsi == (kind == InstallerKind.Msi) ? lead.Winget : null;
                    string manifestArgs = null;
                    if (manifest != null)
                    {
                        bool msiexec;
                        string whyNot;
                        if (kind == InstallerKind.Msi) log = InstallerSupport.MsiLogPath(lead.WingetId.Length > 0 ? lead.WingetId : "sitio", null);
                        manifestArgs = WingetRules.BuildArguments(manifest, file, Interactive, log, out msiexec, out whyNot);
                        if (manifestArgs == null) { Log.Info(c.Name + ": el manifiesto de winget no dice cómo instalarlo en silencio (" + whyNot + "); se usa el tipo del instalador"); manifest = null; log = null; }
                    }
                    if (manifestArgs != null)
                    {
                        exe = kind == InstallerKind.Msi ? Path.Combine(SafePaths.System32, "msiexec.exe") : file;
                        args = manifestArgs;
                        visible = Interactive;
                    }
                    else if (kind == InstallerKind.Msi)
                    {
                        exe = Path.Combine(SafePaths.System32, "msiexec.exe");
                        log = InstallerSupport.MsiLogPath(check != null ? check.Id : (plan.Repo ?? "sitio").Replace('/', '_'), null);
                        args = "/i " + ProcessRunner.QuoteArg(file) + (Interactive ? "" : " /qn") + " /norestart" + (log != null ? " /l*v " + ProcessRunner.QuoteArg(log) : "");
                    }
                    else
                    {
                        exe = file;
                        string silent = check != null && check.ShowInstaller ? null
                                      : check != null && check.SilentArgs != null ? check.SilentArgs : InstallerRules.SilentArgs(kind);
                        // Instalador de otro tipo (InstallShield, WiX…) sin interruptores conocidos: con «Mostrar la ventana de cada instalador»
                        // se abre con su ventana (ya está verificado: mismo firmante que lo instalado); si no, no se adivina.
                        // 2.0.17: de la página oficial y ya verificado: se abre con su ventana (dos o tres clics) en vez de mandar a
                        // la página a bajarlo a mano. Sólo si la política permite instaladores con ventana.
                        bool showAnyway = check == null && silent == null && !Interactive && plan.SitePage != null && !Unattended &&
                                          (UpdateManager.PolicyForTests ?? Policy.Current).AllowInteractiveInstallers;
                        if (check == null && silent == null && !Interactive && !showAnyway)
                        {
                            InstallerSupport.Fail(c, "«" + Path.GetFileName(file) + "» no es un instalador que se pueda ejecutar en silencio (Inno Setup, NSIS o MSI). " +
                                                     "Marcá «Mostrar la ventana de cada instalador» y volvé a actualizar para instalarlo con su ventana, o descargalo de " +
                                                     (plan.SitePage != null ? "su página (clic derecho › «Abrir sitio del fabricante»)" : "la página del proyecto"));
                            return;
                        }
                        if (!InstallerRules.IsSafeArgs(silent) || (check != null && !InstallerRules.IsSafeArgs(check.MachineArgs))) { InstallerSupport.Fail(c, "Interruptores del instalador no permitidos"); return; }
                        visible = Interactive || silent == null;
                        args = Interactive || silent == null ? "" : silent;
                        if (check != null && plan.Machine && !string.IsNullOrEmpty(check.MachineArgs)) args = (args + " " + check.MachineArgs).Trim();
                    }
                    Log.Info(c.Name + ": instalador verificado (" + how + "; tipo " + InstallerRules.KindText(kind) + ")");
                    ctx.Report("Instalando " + c.Name + " " + plan.Latest + " (instalador oficial verificado)…" + (visible ? " Completá la instalación en su ventana." : ""));
                    InstallerSupport.Progress(c, changed, visible ? "Instalando: completá la instalación en su ventana…" : "Instalando (instalador oficial verificado)…");
                    Log.Info("Instalador de " + c.Name + ": " + Path.GetFileName(exe) + " " + args);
                    ProcResult r;
                    if (plan.RunAsUser && ctx.IsAdmin)
                    {
                        Log.Info(c.Name + ": está instalado sólo para el usuario: el instalador se ejecuta como el usuario, sin elevar");
                        r = RunAsUser(exe, args, file, log, hold, visible);
                    }
                    else r = RunInstallerForTests != null ? RunInstallerForTests(exe, args) : ProcessRunner.RunInstaller(exe, args, 45 * 60 * 1000, BrowserProvider.InstallerEnvironment(dir), dir, hold);
                    Log.Info("Instalador de " + c.Name + " terminó: " + r.Describe());
                    if (!r.Started) { InstallerSupport.Fail(c, "No se pudo ejecutar el instalador: " + r.Describe()); return; }
                    plan.InstallerRan = true;
                    if (r.TimedOut) { InstallerSupport.Fail(c, "El instalador no terminó en 45 minutos"); return; }
                    if (manifest != null)
                    {
                        // Los códigos de salida, como los interpreta winget (InstallerSuccessCodes y ExpectedReturnCodes del manifiesto).
                        var exit = WingetRules.InterpretExit(manifest, r.ExitCode);
                        if (!exit.Success)
                        {
                            string cause = log != null ? MsiLogAnalyzer.ExplainFile(log) : "";
                            InstallerSupport.Fail(c, "Instalador oficial verificado, pero " + exit.Message + (cause.Length > 0 ? ". Según el registro del instalador: " + cause : ""));
                            if (exit.RestartFirst) c.RestartBeforeRetry = true;
                            return;
                        }
                        Confirm(ctx, c, plan, exit.Reboot, how);
                        return;
                    }
                    // 3010/1641 es «instalado, falta reiniciar» también en los EXE que lo propagan (Inno usa 0-8 y
                    // NSIS 0-2: no se confunde con otra cosa).
                    bool reboot = r.ExitCode == 3010 || r.ExitCode == 1641;
                    if (r.ExitCode != 0 && !reboot && check != null && check.ShowInstaller)
                    {
                        // 2.0.13: el instalador de NVIDIA devuelve 1 cuando hace falta reiniciar, y otros códigos propios: manda la versión
                        // que quedó instalada.
                        string now = InstalledVersionNow(ctx, plan);
                        if (VersionUtil.Parse(now) != null && !VersionUtil.IsNewer(plan.Latest, now)) { Confirm(ctx, c, plan, r.ExitCode == 1, how); return; }
                        InstallerSupport.Fail(c, "El instalador de " + check.Product + " terminó (código " + r.ExitCode + ") sin dejar instalada la " + plan.Latest +
                                                 (VersionUtil.Parse(now) != null ? " (sigue la " + now + ")" : "") + ". Si lo cerraste antes de terminar, volvé a actualizar");
                        return;
                    }
                    if (r.ExitCode != 0 && !reboot)
                    {
                        string why = log != null ? MsiLogAnalyzer.ExplainFile(log) : "";
                        InstallerSupport.Fail(c, "El instalador verificado devolvió " + ExitText(kind, r.ExitCode) + (why.Length > 0 ? ". Según el registro del instalador: " + why : ""));
                        return;
                    }
                    Confirm(ctx, c, plan, reboot, how);
                }
            }
            finally { BrowserProvider.TryDeleteDir(dir); }
        }

        /// <summary>Sólo para las pruebas: la ejecución como el usuario (programa, argumentos → resultado).</summary>
        internal static Func<string, string, ProcResult> RunAsUserForTests;

        /// <summary>
        /// 2.0.20: ejecuta el instalador verificado como el usuario de la sesión, sin elevar. La carpeta privada de descargas es sólo de
        /// administradores, así que se copia (de lo ya verificado) a una carpeta temporal del usuario; el registro de un .msi también
        /// va ahí. Lo que haga el usuario con esa copia no le da más permisos de los que tiene: el instalador corre como él.
        /// Pública para las pruebas: <see cref="AsUserArgs"/>.
        /// </summary>
        static ProcResult RunAsUser(string exe, string args, string file, string log, FileStream hold, bool visible)
        {
            string userDir = Path.Combine(Path.GetTempPath(), "Actualizador-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            try
            {
                Directory.CreateDirectory(userDir);
                string copy = Path.Combine(userDir, Path.GetFileName(file));
                hold.Position = 0;
                using (var w = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None)) hold.CopyTo(w);
                hold.Position = 0;
                string userLog = log != null ? Path.Combine(userDir, Path.GetFileName(log)) : null;
                string runExe = string.Equals(exe, file, StringComparison.OrdinalIgnoreCase) ? copy : exe;
                string runArgs = AsUserArgs(args, file, copy, log, userLog);
                Log.Info("Instalador (como el usuario): " + Path.GetFileName(runExe) + " " + runArgs);
                var r = RunAsUserForTests != null ? RunAsUserForTests(runExe, runArgs) : Unelevated.Run(runExe, runArgs, 45 * 60 * 1000, visible);
                // Sin el código (no se encontró el proceso): lo decide la versión que quede instalada.
                if (r.Started && r.ExitCode == Unelevated.UnknownExit) r.ExitCode = 0;
                if (userLog != null && log != null)
                {
                    try { if (File.Exists(userLog)) File.Copy(userLog, log, true); }
                    catch (Exception qx) { Log.Quiet("Copiar el registro del instalador", qx); }
                }
                return r;
            }
            finally
            {
                try { Directory.Delete(userDir, true); } catch (Exception qx) { Log.Quiet("Borrar la copia del instalador", qx); }
            }
        }

        /// <summary>Los argumentos con la ruta de la copia y del registro del usuario en vez de los de la carpeta privada. Pública para las pruebas.</summary>
        public static string AsUserArgs(string args, string file, string copy, string log, string userLog)
        {
            string a = args ?? "";
            if (!string.IsNullOrEmpty(file)) a = a.Replace(ProcessRunner.QuoteArg(file), ProcessRunner.QuoteArg(copy)).Replace(file, copy);
            if (!string.IsNullOrEmpty(log) && !string.IsNullOrEmpty(userLog)) a = a.Replace(ProcessRunner.QuoteArg(log), ProcessRunner.QuoteArg(userLog)).Replace(log, userLog);
            return a;
        }

        static string ExitText(InstallerKind kind, int code)
        {
            if (kind == InstallerKind.Msi)
                switch (code)
                {
                    case 1602: return "1602: se canceló en el instalador";
                    case 1603: return "1603: error grave durante la instalación (casi siempre, el programa abierto o una versión anterior que no se pudo quitar)";
                    case 1618: return "1618: hay otra instalación en curso; esperá a que termine y reintentá";
                    case 1625: return "1625: una directiva del equipo bloquea la instalación";
                    case 1638: return "1638: Windows Installer informa otra versión instalada";
                }
            if (code == 1638 || code == 5100) return code + ": ya hay instalada una versión igual o más nueva";
            if (kind == InstallerKind.Nsis && code == 1) return "1: se canceló en el instalador";
            if (kind == InstallerKind.Inno && code == 5) return "5: se canceló en el instalador";
            return "el código " + code + "; suele significar que no acepta instalarse en silencio o que el programa está abierto. Probá con «Mostrar la ventana de cada instalador»";
        }

        /// <summary>Cuánto se espera, como mucho, a que el registro muestre la versión nueva después de instalar, y cada cuánto se mira.</summary>
        internal static int ConfirmWaitMs = 20000, ConfirmPollMs = 1000;

        /// <summary>Después de instalar, relee la versión (algunos instaladores terminan de copiar en segundo plano).</summary>
        void Confirm(UpdateContext ctx, UpdateCandidate c, VendorPlan plan, bool reboot, string how)
        {
            string before = c.InstalledVersion;
            string now = "";
            // 2.0.17: antes se esperaba hasta 90 segundos (30 lecturas cada 3 s) cuando la versión no llegaba, que es justo cuando el
            // instalador no hizo nada: la fila quedaba un minuto y medio «Instalando» para terminar en error. El instalador y los
            // procesos que dejó ya terminaron (RunInstaller los espera): alcanza con unos segundos, mirando seguido.
            var waited = Stopwatch.StartNew();
            while (!ctx.CancelRequested)
            {
                now = InstalledVersionNow(ctx, plan);
                if (VersionUtil.Parse(now) != null && !VersionUtil.IsNewer(plan.Latest, now)) break;
                if (waited.ElapsedMilliseconds >= ConfirmWaitMs) break;
                System.Threading.Thread.Sleep(ConfirmPollMs);
            }
            string filesSource = "";
            string files = "";
            if (plan.Program != null && VersionUtil.Parse(now) != null)
            {
                try { files = WingetProvider.FilesVersion(plan.Program, now, out filesSource); }
                catch (Exception ex) { Log.Warn("Versión de los archivos de " + c.Name + ": " + ex.Message); files = ""; }
            }
            ConfirmVerdict(c, before, now, plan.Latest, files, filesSource, reboot, how, Interactive);
        }

        /// <summary>
        /// El veredicto de la instalación con lo que dice el registro (<paramref name="now"/>) y los archivos del programa
        /// (<paramref name="files"/>, de «version.json» o del ejecutable). Antes quedaba «Actualizada» (en verde) aunque no
        /// hubiera cambiado nada, y no se miraba si los archivos se habían reemplazado de verdad.
        /// </summary>
        internal static void ConfirmVerdict(UpdateCandidate c, string before, string now, string latest, string files, string filesSource,
                                            bool reboot, string how, bool interactive)
        {
            string tail = how != null ? " — " + how : "";
            string done = reboot ? "Actualizada; requiere reiniciar" : "Actualizada correctamente";
            bool strong = ProgramRules.IsProductVersionSource(filesSource);
            bool filesKnown = VersionUtil.Parse(files) != null;
            bool nowKnown = VersionUtil.Parse(now) != null;
            c.Status = reboot ? UpdateStatus.RequiereReinicio : UpdateStatus.Instalada;
            if (nowKnown && !VersionUtil.IsNewer(latest, now))
            {
                if (strong && filesKnown && VersionUtil.IsNewer(latest, files))
                {
                    // El registro llegó y «version.json» no: los archivos en uso no se reemplazaron.
                    c.Status = UpdateStatus.Fallida;
                    c.InstalledVersion = files;
                    c.Result = WingetProvider.PartialInstallText(now, files, filesSource, null) + tail;
                    return;
                }
                c.InstalledVersion = now;
                c.Result = done + tail;
                return;
            }
            if (nowKnown && filesKnown && ProgramRules.FilesAlreadyAt(files, now, latest, strong))
            {
                c.InstalledVersion = files;
                c.Result = done + ". Windows sigue registrando la " + now + " porque el programa no actualiza su entrada en «Aplicaciones», pero " +
                           filesSource + " ya indica la " + files + tail;
                return;
            }
            // Lo que hay de verdad: «version.json» si dice más que el registro (que pudo quedar atrás).
            string effective = strong && filesKnown && (!nowKnown || VersionUtil.IsNewer(files, now)) ? files : now;
            if (VersionUtil.Parse(effective) != null && VersionUtil.Parse(before) != null && VersionUtil.Compare(effective, before) == 0)
            {
                c.Status = UpdateStatus.Fallida;
                c.Result = WingetProvider.NothingChangedText(effective, null, interactive) + tail;
                return;
            }
            c.Result = "El instalador terminó bien" + (nowKnown ? ", pero el registro indica la " + now : "") +
                       "; volvé a buscar actualizaciones para confirmar" + tail;
        }

        /// <summary>Sólo para las pruebas: los programas instalados después de instalar.</summary>
        internal static Func<List<InstalledProgram>> ProgramsNowForTests;

        static List<InstalledProgram> InstalledProgramsNow(UpdateContext ctx)
        {
            if (ProgramsNowForTests != null) return ProgramsNowForTests();
            return InventoryLoader.ReadProgramsNow(ctx.Os != null ? ctx.Os.NativeArchitecture : "");
        }

        internal static string InstalledVersionNowForTests(UpdateContext ctx, VendorPlan plan) { return InstalledVersionNow(ctx, plan); }

        static string InstalledVersionNow(UpdateContext ctx, VendorPlan plan)
        {
            if (plan.Check != null && plan.Check.Install == VendorInstall.OfficeClickToRun) return OfficeC2R.InstalledVersion(OfficeC2R.ReadConfig());
            string best = null;
            try
            {
                bool final = plan.Check != null && plan.Check.Feed == VendorFeed.FinalVersion;
                bool renamed = plan.Check != null && plan.Check.NameHasVersion;
                foreach (var p in InstalledProgramsNow(ctx))
                {
                    if (p.Name == null) continue;
                    // Visual C++: la entrada nueva se llama distinto («… - 11.0.61030»): se busca por la expresión del catálogo y la
                    // misma arquitectura. NVIDIA (2.0.13): «NVIDIA Controlador de gráficos 475.14» pasa a llamarse «… 621.14».
                    bool same = final ? plan.Check.Applies(p) && ArchTag(p) == plan.Arch
                              : renamed ? plan.Check.Applies(p)
                                        : plan.CoveredNames.Exists(delegate(string n) { return n.Equals(p.Name.Trim(), StringComparison.OrdinalIgnoreCase); });
                    if (!same) continue;
                    if (best == null || VersionUtil.IsNewer(p.Version, best)) best = p.Version;
                }
            }
            catch (Exception ex) { Log.Warn("No se pudo releer la versión de " + plan.Product + ": " + ex.Message); }
            string exe = ExePath(plan);
            if (exe != null)
            {
                string fv = ExeVersion(exe);
                if (VersionUtil.Parse(fv) != null && (best == null || VersionUtil.IsNewer(fv, best))) best = fv;
            }
            return best ?? "";
        }

        // ----------------------------------------------------------------- Adobe

        void InstallAdobe(UpdateContext ctx, UpdateCandidate c, VendorPlan plan, Action<UpdateCandidate> changed)
        {
            if (!ctx.IsAdmin) { InstallerSupport.Fail(c, "Aplicar el parche de Adobe requiere ejecutar Actualizador como administrador"); return; }
            string notClosed = EnsureClosed(ctx, c, plan, changed);
            if (notClosed != null) { InstallerSupport.Fail(c, notClosed); return; }
            var check = plan.Check;
            int notFound = 0, notApplicable = 0;
            foreach (string url in plan.MspUrls)
            {
                ctx.ThrowIfCancelled();
                string dir = BrowserProvider.NewDownloadDir();
                try
                {
                    string file = Path.Combine(dir, FileNameFor(url, "parche.msp"));
                    try { Download(ctx, c, changed, url, file, check.Allows); }
                    catch (HttpFailureException hf)
                    {
                        if (hf.StatusCode != 404) throw;
                        notFound++;
                        Log.Info(c.Name + ": Adobe no publica " + Path.GetFileName(file) + " (404)");
                        continue;
                    }
                    int code;
                    string how;
                    string log;
                    using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (InstallerRules.Detect(hold) != InstallerKind.Msi) { InstallerSupport.Fail(c, "La descarga no es un parche de Windows Installer: no se instala"); return; }
                        string bad = CheckSignature(ctx, c, plan, file, out how);
                        if (bad != null) { InstallerSupport.Fail(c, char.ToUpperInvariant(bad[0]) + bad.Substring(1)); return; }
                        log = InstallerSupport.MsiLogPath(check.Id, null);
                        string args = "/p " + ProcessRunner.QuoteArg(file) + (Interactive ? "" : " /qn") + " /norestart" + (log != null ? " /l*v " + ProcessRunner.QuoteArg(log) : "");
                        ctx.Report("Aplicando el parche oficial de Adobe (" + Path.GetFileName(file) + ")…");
                        InstallerSupport.Progress(c, changed, "Aplicando el parche oficial de Adobe…");
                        var r = ProcessRunner.RunInstaller(Path.Combine(SafePaths.System32, "msiexec.exe"), args, 45 * 60 * 1000, BrowserProvider.InstallerEnvironment(dir), dir, hold);
                        Log.Info("msiexec " + args + " terminó: " + r.Describe());
                        if (!r.Started) { InstallerSupport.Fail(c, "No se pudo ejecutar msiexec: " + r.Describe()); return; }
                        if (r.TimedOut) { InstallerSupport.Fail(c, "El parche no terminó en 45 minutos"); return; }
                        code = r.ExitCode;
                    }
                    if (code == 1642)
                    {
                        // El parche no corresponde a esta instalación (Acrobat y Reader de 64 bits se llaman igual): el siguiente.
                        notApplicable++;
                        Log.Info(c.Name + ": " + Path.GetFileName(file) + " no corresponde a esta instalación (1642); se prueba el siguiente parche");
                        continue;
                    }
                    if (code == 0 || code == 3010 || code == 1641) { Confirm(ctx, c, plan, code != 0, how); return; }
                    string why = log != null ? MsiLogAnalyzer.ExplainFile(log) : "";
                    InstallerSupport.Fail(c, "msiexec devolvió " + ExitText(InstallerKind.Msi, code) + (why.Length > 0 ? ". Según el registro del instalador: " + why : ""));
                    return;
                }
                finally { BrowserProvider.TryDeleteDir(dir); }
            }
            if (notFound == plan.MspUrls.Count)
                InstallerSupport.Fail(c, "Adobe anunció la " + plan.Latest + " pero todavía no publicó el parche para Windows; reintentá más tarde. " + check.HowToUpdate);
            else
                InstallerSupport.Fail(c, "Ningún parche oficial de Adobe corresponde a esta instalación (msiexec 1642). " + check.HowToUpdate);
        }

        // ----------------------------------------------------------------- Office

        /// <summary>Sólo para las pruebas: reemplaza la ejecución de OfficeC2RClient.exe (argumentos → código de salida).</summary>
        internal static Func<string, int> OfficeClientForTests;
        /// <summary>Sólo para las pruebas: reemplaza la lectura de la configuración de Office.</summary>
        internal static Func<OfficeConfig> OfficeConfigForTests;
        /// <summary>Sólo para las pruebas: minutos que se espera al servicio de Office (45 en Windows).</summary>
        internal static double OfficeWaitMinutes = 45;

        static OfficeConfig OfficeConfigNow() { return OfficeConfigForTests != null ? OfficeConfigForTests() : OfficeC2R.ReadConfig(); }

        void InstallOffice(UpdateContext ctx, UpdateCandidate c, VendorPlan plan, Action<UpdateCandidate> changed)
        {
            var office = OfficeConfigNow();
            string client = OfficeClientForTests != null ? "OfficeC2RClient.exe" : OfficeC2R.ClientPath(office);
            if (OfficeClientForTests == null && (string.IsNullOrEmpty(client) || !File.Exists(client)))
            {
                InstallerSupport.Fail(c, "No se encontró OfficeC2RClient.exe. Se probaron las rutas de Click-to-Run de 64 y 32 bits." );
                return;
            }
            // Los programas de Office abiertos (también Skype Empresarial y «Enviar a OneNote» en segundo plano):
            // si el usuario aceptó, se cierran como cualquier otro programa. Si siguen abiertos, igual se le pide
            // a Office que baje la actualización: la aplica cuando se cierren, y la fila queda «pendiente» diciendo cuál.
            string notClosed = EnsureClosed(ctx, c, plan, changed);
            if (notClosed != null) Log.Info("Office: " + notClosed + "; se pide la actualización igual (se aplica al cerrarlos)");
            if (OfficeClientForTests == null)
            {
                try
                {
                    if (Native.VerifyAuthenticode(client) != 0 || !InstallerRules.SignerAccepted(Native.SignerSubject(client), new[] { "Microsoft Corporation" }))
                    {
                        InstallerSupport.Fail(c, "El actualizador de Office no tiene una firma válida de Microsoft: no se ejecuta");
                        return;
                    }
                }
                catch (Exception ex) { InstallerSupport.Fail(c, "No se pudo verificar la firma del actualizador de Office: " + ex.Message); return; }
            }

            string before = OfficeC2R.InstalledVersion(office);
            // Office 2021 instalado con ODT u Office Tool Plus suele tener las actualizaciones desactivadas
            // (UpdatesEnabled = False): el actualizador responde sin hacer nada. Se activan mientras dura la
            // actualización, como «Opciones de actualización › Habilitar actualizaciones», y después se restaura.
            bool reenabled = false;
            if (office.UpdatesDisabled && ctx.IsAdmin)
            {
                reenabled = OfficeConfigForTests != null || OfficeC2R.SetUpdatesEnabled(office, true);
                if (reenabled) Log.Info("Office: actualizaciones activadas temporalmente (UpdatesEnabled estaba en False)");
            }
            try
            {
                RunOfficeUpdate(ctx, c, plan, changed, client, before);
            }
            finally
            {
                if (reenabled)
                {
                    if (c.Status == UpdateStatus.Pendiente)
                    {
                        // Office sigue trabajando en segundo plano: desactivarlas ahora cortaría la actualización.
                        c.Result += ". Las actualizaciones de Office quedaron activadas para que termine; si las querés desactivadas, Archivo › Cuenta › Opciones de actualización › Deshabilitar actualizaciones";
                    }
                    else if (OfficeConfigForTests == null && !OfficeC2R.SetUpdatesEnabled(OfficeConfigNow(), false))
                        Log.Warn("Office: no se pudieron volver a desactivar las actualizaciones");
                }
            }
        }

        void RunOfficeUpdate(UpdateContext ctx, UpdateCandidate c, VendorPlan plan, Action<UpdateCandidate> changed, string client, string before)
        {
            string args = OfficeC2R.UpdateArguments(Interactive, plan.Latest);
            ctx.Report("Pidiéndole a Office que se actualice (OfficeC2RClient.exe " + args + ")…");
            InstallerSupport.Progress(c, changed, "Office descarga e instala la actualización con su propio actualizador…");
            int code = RunOfficeClient(client, args, c);
            if (code == int.MinValue) return;                    // ya quedó el error en la fila
            if (code != 0 && code != -1 && args.IndexOf("updatetoversion=", StringComparison.Ordinal) >= 0)
            {
                // Con la versión exacta falló (un canal que no la tiene): se pide «la última» como antes.
                Log.Warn("OfficeC2RClient devolvió " + code + " con la versión exacta; se reintenta sin ella");
                args = OfficeC2R.UpdateArguments(Interactive, null);
                code = RunOfficeClient(client, args, c);
                if (code == int.MinValue) return;
            }
            // OfficeC2RClient puede devolver -1 aun cuando deja el trabajo en manos del servicio.
            // No lo tratamos como un fallo inmediato: la instalación se valida con la versión instalada.
            if (code != 0 && code != -1)
            {
                InstallerSupport.Fail(c, "El actualizador de Office devolvió " + code + " (" + Fmt.Hex(code) + ")");
                return;
            }
            if (code == -1)
            {
                Log.Warn("OfficeC2RClient devolvió -1; se continúa verificando la versión instalada porque ese código no confirma por sí solo un fallo");
                c.Result = "Office inició la actualización; se está verificando la versión instalada…";
                changed(c);
            }

            // El trabajo lo hace el servicio de Office: se espera a que cambie la versión.
            var sw = Stopwatch.StartNew();
            string now = before;
            while (sw.Elapsed.TotalMinutes < OfficeWaitMinutes)
            {
                now = OfficeC2R.InstalledVersion(OfficeConfigNow());
                if (Reached(now, plan.Latest, before)) break;
                if (ctx.CancelRequested) break;
                int min = (int)sw.Elapsed.TotalMinutes;
                InstallerSupport.Progress(c, changed, "Office se está actualizando en segundo plano" + (min > 0 ? " (" + min + " min)" : "") + "…");
                for (int s = 0; s < 10 && !ctx.CancelRequested && sw.Elapsed.TotalMinutes < OfficeWaitMinutes; s++) System.Threading.Thread.Sleep(OfficeClientForTests != null ? 10 : 1000);
            }
            if (Reached(now, plan.Latest, before))
            {
                c.Status = UpdateStatus.Instalada;
                c.InstalledVersion = now;
                c.Result = "Actualizada correctamente con el actualizador de Office";
                return;
            }
            string open = FirstRunning(plan.Processes);
            c.Status = UpdateStatus.Pendiente;
            c.Result = "El actualizador de Office sigue trabajando en segundo plano" + (VersionUtil.IsNewer(now, before) ? " (ya va por la " + now + ")" : "") +
                       (open != null ? ": " + open + " está abierto y Office aplica la actualización al cerrarlo (cerralo también del área de notificación, o aceptá cerrarlo cuando Actualizador lo pregunte)"
                                     : ": si hay un programa de Office abierto, la aplica al cerrarlo") +
                       ". Volvé a buscar actualizaciones más tarde";
        }

        /// <summary>
        /// ¿Office ya llegó? A la versión pedida o, si no había una (canal sin versión publicada), a cualquiera
        /// más nueva que la que tenía.
        /// </summary>
        internal static bool Reached(string now, string latest, string before)
        {
            if (VersionUtil.Parse(now) == null) return false;
            if (VersionUtil.Parse(latest) != null) return !VersionUtil.IsNewer(latest, now);
            return VersionUtil.Parse(before) != null && VersionUtil.IsNewer(now, before);
        }

        /// <summary>Ejecuta OfficeC2RClient.exe; int.MinValue si no arrancó o no terminó (el error queda en la fila).</summary>
        static int RunOfficeClient(string client, string args, UpdateCandidate c)
        {
            if (OfficeClientForTests != null) return OfficeClientForTests(args);
            var r = ProcessRunner.RunInstaller(client, args, 60 * 60 * 1000, null);
            Log.Info("OfficeC2RClient terminó: " + r.Describe());
            if (!r.Started) { InstallerSupport.Fail(c, "No se pudo ejecutar el actualizador de Office: " + r.Describe()); return int.MinValue; }
            if (r.TimedOut) { InstallerSupport.Fail(c, "El actualizador de Office no respondió en una hora"); return int.MinValue; }
            return r.ExitCode;
        }

        // ----------------------------------------------------------------- AIMP

        // ================================================================= 2.0.19: arranque starter4j (TLauncher)

        /// <summary>
        /// «starter.json» del perfil donde se leyó la versión instalada, comprobado: que exista y que sea el archivo que el catálogo
        /// dice (%APPDATA%\.tlauncher\starter\starter.json). null si no.
        /// </summary>
        internal static string StarterLocalFile(VendorPlan plan)
        {
            if (plan == null || plan.Check == null || string.IsNullOrEmpty(plan.ProfileFile) || plan.Check.ProfileVersionFiles == null) return null;
            string f = plan.ProfileFile;
            if (Path.DirectorySeparatorChar != '\\') f = f.Replace('\\', Path.DirectorySeparatorChar);
            bool known = false;
            foreach (string rel in plan.Check.ProfileVersionFiles)
            {
                string r = (rel ?? "").Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                if (r.Length > 0 && f.EndsWith(Path.DirectorySeparatorChar + r, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
            }
            if (!known || f.Contains("..")) return null;
            try { return File.Exists(f) ? Path.GetFullPath(f) : null; }
            catch (Exception ex) { Log.Quiet("starter.json", ex); return null; }
        }

        /// <summary>Lo abierto de TLauncher: su ventana (por nombre) y lo que corre desde la carpeta de su arranque (su propio Java).</summary>
        public static List<RunningProcess> StarterRunning(List<RunningProcess> all, VendorPlan plan)
        {
            var result = new List<RunningProcess>();
            if (all == null || plan == null) return result;
            var pids = new HashSet<int>();
            Action<List<RunningProcess>> add = delegate(List<RunningProcess> l) { foreach (var p in l) if (pids.Add(p.Pid)) result.Add(p); };
            add(RunningPrograms.ByName(all, plan.Processes));
            string file = StarterLocalFile(plan);
            if (file != null) add(RunningPrograms.In(all, Path.GetDirectoryName(file)));
            return result;
        }

        /// <summary>
        /// 2.0.19: TLauncher se actualiza haciendo lo mismo que su arranque cuando se toca «Actualizar» en su ventana (ver
        /// <see cref="Starter4j"/>): sin abrir su página, sin su ventana y sin su oferta de programas de terceros.
        /// </summary>
        void InstallStarter(UpdateContext ctx, UpdateCandidate c, VendorPlan plan, Action<UpdateCandidate> changed)
        {
            var check = plan.Check;
            string file = StarterLocalFile(plan);
            if (file == null) { InstallerSupport.Fail(c, "No se encontró " + StarterFileName(check) + " de " + check.Product + " en el perfil: abrilo una vez y volvé a buscar"); return; }
            string workDir = Path.GetDirectoryName(file);
            string notClosed = EnsureClosed(ctx, c, plan, changed);
            if (notClosed != null) { InstallerSupport.Fail(c, notClosed); return; }

            // La configuración publicada, recién leída (puede haber salido otra versión desde la búsqueda).
            InstallerSupport.Progress(c, changed, "Leyendo la versión publicada en " + check.Domain + "…");
            string json = null, from = null, error = "sin respuesta";
            var sources = new List<string> { check.Url };
            if (check.MirrorUrls != null) sources.AddRange(check.MirrorUrls);
            foreach (string url in sources)
            {
                Uri u;
                if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u) || !check.Allows(u)) continue;
                ctx.ThrowIfCancelled();
                try
                {
                    json = FetchForTests != null ? FetchForTests(url) : Http.GetString(url, 20000, check.Allows, "application/json");
                    if (!string.IsNullOrEmpty(json)) { from = url; break; }
                }
                catch (OperationCanceledByUserException) { throw; }
                catch (Exception ex) { error = Http.Describe(ex); Log.Warn(check.Product + ": " + u.Host + ": " + error); }
                json = null;
            }
            if (json == null) { InstallerSupport.Fail(c, "No se pudo leer la versión publicada en " + check.Domain + " (" + error + ")"); return; }
            string parseError;
            var cfg = Starter4j.ParseConfig(json, from, check.Allows, out parseError);
            if (cfg == null) { InstallerSupport.Fail(c, "La configuración que publica " + check.Domain + " no se pudo usar: " + parseError); return; }

            string before = StarterVersion(check, file);
            if (before != null && !VersionUtil.IsNewer(cfg.Version, before))
            {
                c.Status = UpdateStatus.Instalada;
                c.InstalledVersion = before;
                c.Result = "Ya estaba en la " + before + " (la última que publica " + check.Domain + ")";
                return;
            }
            plan.Locating = true;
            ctx.Report("Actualizando " + c.Name + " a la " + cfg.Version + " (archivos de " + check.Domain + ", SHA-1 verificado)…");
            var o = Starter4j.Apply(cfg, workDir, file, check.Allows, ctx, delegate(string t) { InstallerSupport.Progress(c, changed, t); });
            if (!o.Ok) { InstallerSupport.Fail(c, "No se actualizó (" + o.Error + "); " + check.Product + " quedó como estaba"); return; }

            string now = StarterVersion(check, file);
            if (now == null || VersionUtil.Compare(now, cfg.Version) != 0)
            {
                InstallerSupport.Fail(c, StarterFileName(check) + " no quedó con la " + cfg.Version + " (dice " + (now ?? "nada") + ")");
                return;
            }
            c.Status = UpdateStatus.Instalada;
            c.InstalledVersion = now;
            c.AvailableVersion = cfg.Version;
            plan.Latest = cfg.Version;
            var parts = new List<string>();
            if (o.Downloaded > 0) parts.Add(Fmt.Plural(o.Downloaded, "archivo descargado", "archivos descargados") + " (" + Fmt.Bytes(o.Bytes) + ")");
            if (o.AlreadyOk > 0) parts.Add(Fmt.Plural(o.AlreadyOk, "ya estaba", "ya estaban"));
            c.Result = "Actualizada correctamente — " + string.Join(", ", parts.ToArray()) + (parts.Count > 0 ? "; " : "") +
                       "SHA-1 verificado con la configuración oficial de " + check.Domain + ". Sin la ventana de " + check.Product + " ni su oferta de programas" +
                       (o.LeftForStarter > 0 ? ". " + Fmt.Plural(o.LeftForStarter, "archivo menor lo completa", "archivos menores los completa") + " " + check.Product + " al abrirse" : "");
            Log.Info(c.Name + ": " + (before ?? "?") + " → " + now + ". " + c.Result);
        }

        /// <summary>La versión que dice ahora el archivo del perfil (leído de verdad, no de la búsqueda).</summary>
        static string StarterVersion(VendorCheck check, string file)
        {
            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists || fi.Length > 1024 * 1024) return null;
                var m = check.ProfileVersion.Match(File.ReadAllText(file));
                return m.Success && VersionUtil.Parse(m.Groups["v"].Value) != null ? VersionUtil.Normalize(m.Groups["v"].Value) : null;
            }
            catch (Exception ex) { Log.Quiet("Versión en " + file, ex); return null; }
        }

        void InstallAimp(UpdateContext ctx, UpdateCandidate c, VendorPlan plan, Action<UpdateCandidate> changed)
        {
            var check = plan.Check;
            var d = plan.Download;
            if (d == null) { InstallerSupport.Fail(c, "No hay un instalador verificable para este programa"); return; }
            string notClosed = EnsureClosed(ctx, c, plan, changed);
            if (notClosed != null) { InstallerSupport.Fail(c, notClosed); return; }
            string dir = BrowserProvider.NewDownloadDir();
            string file = Path.Combine(dir, check.Id + "_" + d.Version + "_" + d.Arch + ".exe");
            try
            {
                ctx.Report("Descargando " + c.Name + " " + d.Version + " (" + d.Arch + ") desde " + check.Domain + "…");
                try { Http.Download(VendorCatalog.DownloadUrl(d), file, 20 * 60 * 1000, ctx, check.Allows); }
                catch (Exception first)
                {
                    if (first is OperationCanceledByUserException) throw;
                    Log.Warn("Descarga de " + c.Name + " por el enlace de la página: " + Http.Describe(first) + "; se prueba la dirección directa.");
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
                    Http.Download(VendorCatalog.DirectUrl(d), file, 20 * 60 * 1000, ctx, check.Allows);
                }
                // si el enlace de la página trajo otro archivo (la página cambió de diseño y el enlace quedó cruzado con otra
                // arquitectura), se prueba la dirección directa del instalador antes de rendirse; el hash publicado decide igual.
                string firstHash;
                using (var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) firstHash = InstallerSupport.Sha256(probe);
                if (firstHash != d.Sha256)
                {
                    Log.Warn(c.Name + ": el enlace de la página trajo otro archivo (" + firstHash + "); se prueba la dirección directa");
                    try
                    {
                        File.Delete(file);
                        Http.Download(VendorCatalog.DirectUrl(d), file, 20 * 60 * 1000, ctx, check.Allows);
                    }
                    catch (Exception ex)
                    {
                        if (ex is OperationCanceledByUserException) throw;
                        Log.Warn("Descarga directa de " + c.Name + ": " + Http.Describe(ex));
                    }
                }
                if (!File.Exists(file)) { InstallerSupport.Fail(c, "No se pudo descargar el instalador de " + check.Domain); return; }
                using (var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string got = InstallerSupport.Sha256(hold);
                    if (got != d.Sha256)
                    {
                        InstallerSupport.Fail(c, "El SHA-256 de la descarga no coincide con el publicado en " + check.Domain + ": no se instala");
                        Log.Warn(c.Name + ": hash esperado " + d.Sha256 + ", obtenido " + got);
                        return;
                    }
                    string sigNote;
                    int sig = Native.VerifyAuthenticodeStrict(file, out sigNote);
                    if (sig == Native.CERT_E_REVOKED) { InstallerSupport.Fail(c, "El certificado que firma el instalador fue REVOCADO por su emisor: no se instala"); return; }
                    // Sin firma se acepta por el hash publicado; una firma presente pero inválida (archivo
                    // alterado, cadena no confiable) es una señal de manipulación y no se instala.
                    if (sig != 0 && unchecked((uint)sig) != 0x800B0100)
                    {
                        InstallerSupport.Fail(c, "El instalador tiene una firma digital que no es válida (" + Fmt.Hex(sig) + "): no se instala");
                        return;
                    }
                    Log.Info(c.Name + ": SHA-256 verificado (" + got + "); firma Authenticode: " + (sig == 0 ? "válida" : Fmt.Hex(sig)) + (sigNote != null ? " — " + sigNote : ""));
                    ctx.Report("Instalando " + c.Name + " " + d.Version + " (instalador oficial, hash verificado)…");
                    string args = Interactive ? "" : check.SilentArgs;
                    Log.Info("Instalador de " + c.Name + (string.IsNullOrEmpty(args) ? " (interactivo: con sus ventanas)" : " " + args));
                    var r = ProcessRunner.RunInstaller(file, args, 30 * 60 * 1000, BrowserProvider.InstallerEnvironment(dir), dir, hold);
                    Log.Info("Instalador de " + c.Name + " terminó: " + r.Describe());
                    if (!r.Started) { InstallerSupport.Fail(c, r.Describe()); return; }
                    if (r.TimedOut) { InstallerSupport.Fail(c, "El instalador no terminó en 30 minutos"); return; }
                    if (r.ExitCode != 0) { InstallerSupport.Fail(c, "El instalador devolvió " + r.ExitCode + (Interactive ? "" : "; probá con «Mostrar la ventana de cada instalador»")); return; }
                }
                // Antes se miraba la versión del .exe en el mismo instante en que terminaba el instalador:
                // si la instalación seguía un momento más, la fila decía «volvé a buscar actualizaciones»
                // aunque hubiera salido bien. Confirm relee el registro y el ejecutable durante un rato.
                plan.Latest = d.Version;
                Confirm(ctx, c, plan, false, "SHA-256 publicado por " + check.Domain + " verificado");
            }
            finally { BrowserProvider.TryDeleteDir(dir); }
        }
    }
}
