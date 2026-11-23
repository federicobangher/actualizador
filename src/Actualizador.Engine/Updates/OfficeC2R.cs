// Microsoft Office «Hacer clic y ejecutar»: canal, compilación publicada y actualizador propio.
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
    /// <summary>Configuración de Office «Hacer clic y ejecutar» leída del registro.</summary>
    public sealed class OfficeConfig
    {
        public string VersionToReport = "", OfficePackageVersion = "", OfficeProductReleaseIds = "";
        public string CdnBaseUrl = "", UpdateChannel = "", PolicyBranch = "", PolicyPath = "";
        public string OdtUpdatePath = "", UnmanagedUpdateUrl = "", UpdatePath = "", UpdatesEnabled = "";
        public string ClientFolder = "", InstallationPath = "", Platform = "";
        /// <summary>ProductReleaseIds de Configuration («ProPlus2021Volume,VisioPro2021Volume»): respaldo del inventario.</summary>
        public string ProductReleaseIds = "";
        /// <summary>Directiva «updatetargetversion»: la empresa fija Office en esa versión.</summary>
        public string PolicyTargetVersion = "";
        /// <summary>Directiva «enableautomaticupdates» = 0: actualizaciones automáticas desactivadas por la empresa.</summary>
        public bool PolicyUpdatesDisabled;
        public bool Found, InventoryFound;
        /// <summary>Vista del registro donde está Configuration (para activar las actualizaciones al actualizar).</summary>
        public Microsoft.Win32.RegistryView ConfigView = Microsoft.Win32.RegistryView.Default;

        public string ProductSummary()
        {
            if (!string.IsNullOrEmpty(OfficeProductReleaseIds)) return OfficeProductReleaseIds;
            if (!string.IsNullOrEmpty(ProductReleaseIds)) return ProductReleaseIds;
            return "Office Click-to-Run";
        }

        /// <summary>Productos instalados (del inventario o, si falta, de Configuration).</summary>
        public string Products { get { return !string.IsNullOrEmpty(OfficeProductReleaseIds) ? OfficeProductReleaseIds : (ProductReleaseIds ?? ""); } }

        /// <summary>«UpdatesEnabled = False»: el instalador (ODT, Office Tool Plus…) desactivó las actualizaciones.</summary>
        public bool UpdatesDisabled { get { return (UpdatesEnabled ?? "").Trim().Equals("False", StringComparison.OrdinalIgnoreCase); } }
    }

    /// <summary>Un canal de actualización de Office tal como lo publica Microsoft.</summary>
    public sealed class OfficeChannel
    {
        public string Name = "", Id = "", Latest = "";
        public readonly List<string> Guids = new List<string>();
        public readonly List<string> AlternateNames = new List<string>();
        /// <summary>
        /// Canal conocido que Microsoft ya no publica en su listado (Office 2019 desde el 14/10/2025; Office
        /// 2021 cuando termine su soporte): no hay versión nueva que comparar. Latest queda vacío.
        /// </summary>
        public bool Retired;
        /// <summary>Fin del soporte del producto del canal (sólo los perpetuos), o null.</summary>
        public DateTime? EndOfSupport;
    }

    public static class OfficeC2R
    {
        public const string ConfigKey = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
        public const string InventoryKey = @"SOFTWARE\Microsoft\Office\ClickToRun\Inventory\Office\16.0";
        public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\office\16.0\common\officeupdate";
        public const string CloudPolicyKey = @"SOFTWARE\Policies\Microsoft\cloud\office\16.0\Common\officeupdate";

        static readonly string[] OfficeCommandFields = { "UninstallString", "UninstallCommand", "QuietUninstallCommand", "DisplayIcon", "InstallLocation" };

        /// <summary>Las entradas de «Programas instalados» de Office C2R se desinstalan con OfficeClickToRun.exe.
        /// Algunas ediciones registran el comando silencioso en un campo distinto, así que se revisan
        /// todos los campos y no sólo UninstallString.</summary>
        public static bool IsClickToRun(InstalledProgram p)
        {
            if (p == null) return false;
            foreach (string field in OfficeCommandFields)
            {
                string value = field == "UninstallString" ? p.UninstallString :
                               field == "UninstallCommand" ? p.UninstallCommand :
                               field == "QuietUninstallCommand" ? p.QuietUninstallCommand :
                               field == "DisplayIcon" ? p.DisplayIcon : p.InstallLocation;
                if (!string.IsNullOrEmpty(value) && value.IndexOf("OfficeClickToRun.exe", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Versión instalada: la más nueva entre OfficePackageVersion (inventario) y VersionToReport (la que
        /// muestra Office en Archivo › Cuenta). Las instalaciones viejas de Office 2016/2019/2021 tienen la
        /// clave del inventario sin actualizar (o no la tienen): tomar sólo OfficePackageVersion dejaba a Office
        /// «desactualizado» para siempre, aunque el actualizador ya hubiera instalado la nueva.
        /// </summary>
        public static string InstalledVersion(OfficeConfig cfg)
        {
            if (cfg == null) return "";
            string pkg = VersionUtil.Parse(cfg.OfficePackageVersion) != null ? cfg.OfficePackageVersion : "";
            string rep = VersionUtil.Parse(cfg.VersionToReport) != null ? cfg.VersionToReport : "";
            if (pkg.Length == 0) return rep;
            if (rep.Length == 0) return pkg;
            return VersionUtil.IsNewer(rep, pkg) ? rep : pkg;
        }

        public static bool IsManaged(OfficeConfig cfg)
        {
            return ManagedSource(cfg).Length > 0;
        }

        /// <summary>
        /// ¿Es una dirección de la red de distribución de Office de Microsoft (officecdn.microsoft.com)?
        /// ODT, Office Tool Plus y el cambio de canal escriben ahí UpdateUrl/UpdatePath/UnmanagedUpdateURL
        /// con la dirección del canal: eso NO es un servidor de la empresa.
        /// </summary>
        public static bool IsMicrosoftCdn(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            Uri u;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
            string h = u.Host.ToLowerInvariant();
            return h == "officecdn.microsoft.com" || h.EndsWith(".officecdn.microsoft.com") ||
                   h == "officecdn.microsoft.com.edgesuite.net" || h.EndsWith(".officecdn.microsoft.com.edgesuite.net");
        }

        /// <summary>
        /// Origen de las actualizaciones que administra la empresa (carpeta de red, servidor propio,
        /// Configuration Manager), o "" si Office se actualiza desde Microsoft. Hasta la 1.6.2 cualquier
        /// UpdateUrl contaba como «lo administra la empresa», también la dirección del canal en la red de
        /// Microsoft que dejan ODT y Office Tool Plus: por eso Office 2021 instalado con esas herramientas
        /// nunca se ofrecía para actualizar.
        /// </summary>
        public static string ManagedSource(OfficeConfig cfg)
        {
            if (cfg == null) return "";
            foreach (string s in new[] { cfg.PolicyPath, cfg.OdtUpdatePath, cfg.UnmanagedUpdateUrl })
            {
                if (string.IsNullOrEmpty(s) || s.Trim().Length == 0) continue;
                if (IsMicrosoftCdn(s)) continue;
                return s.Trim();
            }
            return "";
        }

        static readonly Regex OfficeBuild = new Regex(@"^1[56]\.0\.\d{4,5}\.\d{1,5}$", RegexOptions.Compiled);

        /// <summary>
        /// Argumentos de OfficeC2RClient.exe. Con la versión exacta («updatetoversion», como documenta Microsoft)
        /// Office va a esa compilación aunque el canal todavía la esté distribuyendo de a poco o las
        /// actualizaciones estén desactivadas; sin ella, «/update user» puede responder «ya está al día» y no
        /// hacer nada (lo que pasaba con Office 2021).
        /// </summary>
        public static string UpdateArguments(bool interactive, string targetVersion)
        {
            string flag = interactive ? "true" : "false";
            string args = "/update user updatepromptuser=" + flag + " forceappshutdown=false displaylevel=" + flag;
            string v = (targetVersion ?? "").Trim();
            if (OfficeBuild.IsMatch(v)) args += " updatetoversion=" + v;
            return args;
        }

        /// <summary>
        /// Office instalado con Windows Installer (MSI): 2007, 2010, 2013 y 2016 de licencia por volumen, y
        /// Visio/Project de esos años. No tiene actualizador propio: sus parches llegan por Microsoft Update
        /// («Recibir actualizaciones de otros productos de Microsoft» en Windows Update).
        /// </summary>
        public static bool IsOfficeMsi(InstalledProgram p)
        {
            if (p == null || string.IsNullOrEmpty(p.Name) || IsClickToRun(p)) return false;
            if (!OfficeMsiName.IsMatch(p.Name.Trim())) return false;
            string u = (p.UninstallCommand ?? "") + " | " + (p.UninstallString ?? "");
            return u.IndexOf("Office Setup Controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (p.IsWindowsInstaller && u.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0 && OfficeMsiVersion.IsMatch(p.Version ?? ""));
        }

        static readonly Regex OfficeMsiName = new Regex(@"^Microsoft (?:Office|Visio|Project|Access|Excel|Word|PowerPoint|Outlook|Publisher|OneNote|InfoPath|SharePoint Designer|Lync|Skype for Business)\b(?!.*\b(?:Runtime|Proofing|MUI|Shared|Click-to-Run|Extensibility|Licensing|Language Pack|Interop|Primary Interop|Viewer|Compatibility Pack|Web Components|database engine|Visual Web Developer)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        static readonly Regex OfficeMsiVersion = new Regex(@"^1[2-6]\.0\.", RegexOptions.Compiled);

        /// <summary>
        /// Office «Hacer clic y ejecutar» anterior a 2016 (2013 = 15.0, 2010 = 14.0): sin soporte y con otra
        /// configuración en el registro. Se informa como sin soporte en lugar de «no se pudo leer la configuración».
        /// </summary>
        public static bool IsLegacyClickToRun(string version)
        {
            string v = (version ?? "").Trim();
            return VersionUtil.Parse(v) != null && (v.StartsWith("15.", StringComparison.Ordinal) || v.StartsWith("14.", StringComparison.Ordinal));
        }

        /// <summary>Ruta real del OfficeC2RClient, respetando instalaciones x86/x64.</summary>
        public static string ClientPath(OfficeConfig cfg)
        {
            var paths = new List<string>();
            if (cfg != null && !string.IsNullOrEmpty(cfg.ClientFolder)) paths.Add(Path.Combine(cfg.ClientFolder, "OfficeC2RClient.exe"));
            paths.Add(Path.Combine(SafeProgramFiles(), @"Common Files\microsoft shared\ClickToRun\OfficeC2RClient.exe"));
            paths.Add(Path.Combine(SafeProgramFilesX86(), @"Common Files\microsoft shared\ClickToRun\OfficeC2RClient.exe"));
            foreach (string p in paths)
                try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p; } catch { }
            return paths.Count > 0 ? paths[0] : "";
        }

        static string SafeProgramFiles()
        {
            return SafePaths.ProgramFiles;
        }

        static string SafeProgramFilesX86()
        {
            try
            {
                if (!Environment.Is64BitOperatingSystem) return SafePaths.ProgramFiles;
                using (var k = Reg.Open(Microsoft.Win32.RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion", Microsoft.Win32.RegistryView.Registry64))
                {
                    string v = Reg.Str(k, "ProgramFilesDir (x86)");
                    if (!string.IsNullOrEmpty(v) && v.IndexOf('%') < 0) return v.TrimEnd('\\');
                }
            }
            catch (Exception qx) { Log.Quiet("Office: Program Files (x86)", qx); }
            return Path.Combine(SafePaths.SystemDrive + @"\", "Program Files (x86)");
        }

        /// <summary>Lee configuración e inventario C2R en las dos vistas del registro y conserva la
        /// prioridad de Microsoft para la fuente de actualización: Cloud Policy → Policy → ODT → Unmanaged → CDNBaseUrl.</summary>
        public static OfficeConfig ReadConfig()
        {
            var best = new OfficeConfig();
            foreach (var view in Reg.Views())
            {
                OfficeConfig cfg = ReadConfig(view);
                if (!cfg.Found && !cfg.InventoryFound) continue;
                if (!best.Found && !best.InventoryFound) best = cfg;
                else
                {
                    // Preferimos la vista que tenga inventario autoritativo y ClientFolder.
                    int score = (cfg.InventoryFound ? 4 : 0) + (!string.IsNullOrEmpty(cfg.ClientFolder) ? 2 : 0) + (!string.IsNullOrEmpty(cfg.OfficePackageVersion) ? 2 : 0);
                    int old = (best.InventoryFound ? 4 : 0) + (!string.IsNullOrEmpty(best.ClientFolder) ? 2 : 0) + (!string.IsNullOrEmpty(best.OfficePackageVersion) ? 2 : 0);
                    if (score > old) best = cfg;
                }
            }
            return best;
        }

        static OfficeConfig ReadConfig(Microsoft.Win32.RegistryView view)
        {
            var cfg = new OfficeConfig();
            try
            {
                using (var k = Reg.Open(Microsoft.Win32.RegistryHive.LocalMachine, ConfigKey, view))
                {
                    if (k != null)
                    {
                        cfg.Found = true;
                        cfg.VersionToReport = Reg.Str(k, "VersionToReport") ?? "";
                        cfg.CdnBaseUrl = Reg.Str(k, "CDNBaseUrl") ?? "";
                        cfg.UpdateChannel = Reg.Str(k, "UpdateChannel") ?? "";
                        cfg.UpdatesEnabled = Reg.Str(k, "UpdatesEnabled") ?? "";
                        cfg.OdtUpdatePath = FirstNonEmpty(Reg.Str(k, "UpdateUrl"), Reg.Str(k, "UpdatePath"));
                        cfg.UnmanagedUpdateUrl = Reg.Str(k, "UnmanagedUpdateURL") ?? "";
                        cfg.ClientFolder = Reg.Str(k, "ClientFolder") ?? "";
                        cfg.InstallationPath = Reg.Str(k, "InstallationPath") ?? "";
                        cfg.Platform = Reg.Str(k, "Platform") ?? "";
                        cfg.ProductReleaseIds = Reg.Str(k, "ProductReleaseIds") ?? "";
                        cfg.ConfigView = view;
                    }
                }
                using (var k = Reg.Open(Microsoft.Win32.RegistryHive.LocalMachine, InventoryKey, view))
                {
                    if (k != null)
                    {
                        cfg.InventoryFound = true;
                        cfg.OfficePackageVersion = Reg.Str(k, "OfficePackageVersion") ?? "";
                        cfg.OfficeProductReleaseIds = Reg.Str(k, "OfficeProductReleaseIds") ?? "";
                    }
                }
                OfficePolicy(cfg, view, CloudPolicyKey, true);
                OfficePolicy(cfg, view, PolicyKey, false);
                cfg.UpdatePath = FirstNonEmpty(cfg.PolicyPath, cfg.OdtUpdatePath, cfg.UnmanagedUpdateUrl);
            }
            catch (Exception ex) { Log.Warn("Configuración de Office (" + view + "): " + ex.Message); }
            return cfg;
        }

        static void OfficePolicy(OfficeConfig cfg, Microsoft.Win32.RegistryView view, string key, bool cloud)
        {
            using (var k = Reg.Open(Microsoft.Win32.RegistryHive.LocalMachine, key, view))
            {
                if (k == null) return;
                string branch = Reg.Str(k, "updatebranch");
                string path = Reg.Str(k, "updatepath");
                string target = Reg.Str(k, "updatetargetversion");
                if (!string.IsNullOrEmpty(target) && (string.IsNullOrEmpty(cfg.PolicyTargetVersion) || cloud)) cfg.PolicyTargetVersion = target;
                long? auto = Reg.Long(k, "enableautomaticupdates");
                if (auto.HasValue && auto.Value == 0) cfg.PolicyUpdatesDisabled = true;
                if (!string.IsNullOrEmpty(branch) && string.IsNullOrEmpty(cfg.PolicyBranch)) cfg.PolicyBranch = branch;
                if (!string.IsNullOrEmpty(path) && string.IsNullOrEmpty(cfg.PolicyPath)) cfg.PolicyPath = path;
                if (cloud && (!string.IsNullOrEmpty(branch) || !string.IsNullOrEmpty(path)))
                {
                    // La actualización en la nube tiene precedencia aunque también exista GPO clásica.
                    if (!string.IsNullOrEmpty(branch)) cfg.PolicyBranch = branch;
                    if (!string.IsNullOrEmpty(path)) cfg.PolicyPath = path;
                }
            }
        }

        static string FirstNonEmpty(params string[] values)
        {
            foreach (string v in values) if (!string.IsNullOrEmpty(v)) return v;
            return "";
        }

        static readonly Regex GuidRx = new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

        public static string GuidOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var m = GuidRx.Match(url);
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        /// <summary>Canales del JSON oficial (https://clients.config.office.net/releases/v1.0/OfficeReleases).</summary>
        public static List<OfficeChannel> ParseReleases(string json)
        {
            var result = new List<OfficeChannel>();
            var root = Json.Parse(json) as List<object>;
            if (root == null) throw new InvalidDataException("el listado de versiones de Office no tiene el formato esperado");
            foreach (object o in root)
            {
                var ch = new OfficeChannel();
                ch.Name = Json.Str(o, "channel") ?? "";
                ch.Id = Json.Str(o, "channelId") ?? "";
                ch.Latest = Json.Str(o, "latestVersion") ?? "";
                foreach (object a in Json.List(o, "alternateNames")) if (a is string) ch.AlternateNames.Add((string)a);
                foreach (object v in Json.List(o, "officeVersions"))
                {
                    string g = GuidOf(Json.Str(v, "cdnBaseUrl"));
                    if (g != null && !ch.Guids.Contains(g)) ch.Guids.Add(g);
                }
                if (ch.Id.Length > 0 && VersionUtil.Parse(ch.Latest) != null) result.Add(ch);
            }
            return result;
        }

        /// <summary>
        /// Canal efectivo siguiendo la prioridad de actualización: ruta de Cloud Policy/policy,
        /// branch de policy, UpdateChannel, ODT/Unmanaged y, por último, CDNBaseUrl de la instalación.
        /// Cada valor puede ser una dirección con el GUID del canal o un nombre («Current», «MonthlyEnterprise»…).
        /// null si no se reconoce.
        /// </summary>
        public static OfficeChannel ResolveChannel(List<OfficeChannel> channels, OfficeConfig cfg)
        {
            if (channels == null || cfg == null) return null;
            foreach (string source in new[] { cfg.PolicyPath, cfg.PolicyBranch, cfg.UpdateChannel, cfg.OdtUpdatePath, cfg.UnmanagedUpdateUrl, cfg.CdnBaseUrl })
            {
                if (string.IsNullOrEmpty(source) || source.Trim().Length == 0) continue;
                string g = GuidOf(source);
                foreach (var ch in channels)
                {
                    if (g != null ? ch.Guids.Contains(g) : NameMatches(ch, source.Trim())) return ch;
                }
                // Un canal que Microsoft ya no publica (Office 2019): se reconoce igual, sin versión que comparar.
                var known = Known(g, source.Trim());
                if (known != null) return Inherit(channels, known);
            }
            // Sin dirección ni nombre de canal: por los productos instalados (LTSC por volumen o comercial).
            var byProduct = KnownByProducts(cfg.Products);
            return byProduct != null ? Inherit(channels, byProduct) : null;
        }

        /// <summary>Si el listado de Microsoft trae el canal, se usa ése (con su versión); si no, el conocido sin versión.</summary>
        static OfficeChannel Inherit(List<OfficeChannel> channels, OfficeChannel known)
        {
            foreach (var ch in channels)
                if (ch.Id.Equals(known.Id, StringComparison.OrdinalIgnoreCase)) return ch;
            return known;
        }

        sealed class KnownChannel
        {
            public string Id, Guid;
            public string[] Names;
            public DateTime? End;
            public KnownChannel(string id, string guid, DateTime? end, params string[] names) { Id = id; Guid = guid; End = end; Names = names; }
        }

        /// <summary>
        /// Canales documentados por Microsoft («Change the Microsoft 365 Apps update channel», «Update channels
        /// for Office LTSC»). El listado oficial deja de publicar un canal cuando termina su soporte: Office
        /// 2019 ya no figura, y eso daba «no se reconoce el canal de actualización».
        /// </summary>
        static readonly KnownChannel[] KnownChannels =
        {
            new KnownChannel("Current", "492350f6-3a01-4f97-b9c0-c7c6ddf67d60", null, "Monthly", "CurrentChannel"),
            new KnownChannel("CurrentPreview", "64256afe-f5d9-4f86-8936-8840a6a4f5be", null, "Insiders", "FirstReleaseCurrent", "InsiderSlow", "MonthlyPreview"),
            new KnownChannel("BetaChannel", "5440fd1f-7ecb-4221-8110-145efaa6372f", null, "InsiderFast", "Beta"),
            new KnownChannel("MonthlyEnterprise", "55336b82-a18d-4dd6-b5f6-9e5095c314a6", null, "MEC"),
            new KnownChannel("SemiAnnual", "7ffbc6bf-bc32-4f92-8982-f9dd17fd3114", null, "Broad", "Deferred", "SAC"),
            new KnownChannel("SemiAnnualPreview", "b8f9b850-328d-4355-9145-c59439a0c4cf", null, "Targeted", "FirstReleaseDeferred", "SACT"),
            new KnownChannel("PerpetualVL2019", "f2e724c1-748f-4b47-8fb8-8e0d210e9208", new DateTime(2025, 10, 14), "Perpetual2019", "LTSB2019"),
            new KnownChannel("PerpetualVL2021", "5030841d-c919-4594-8d2d-84ae4f96e58e", new DateTime(2026, 10, 13), "Perpetual2021", "LTSB2021"),
            new KnownChannel("PerpetualVL2024", "7983bac0-e531-40cf-be00-fd24fe66619c", new DateTime(2029, 10, 9), "Perpetual2024", "LTSB2024"),
        };

        static OfficeChannel Known(string guid, string name)
        {
            foreach (var k in KnownChannels)
            {
                bool hit = guid != null ? k.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase)
                                        : k.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || Array.Exists(k.Names, delegate(string n) { return n.Equals(name, StringComparison.OrdinalIgnoreCase); });
                if (!hit) continue;
                var ch = new OfficeChannel();
                ch.Id = k.Id;
                ch.Name = k.Id;
                ch.Guids.Add(k.Guid);
                ch.AlternateNames.AddRange(k.Names);
                ch.Retired = true;
                ch.EndOfSupport = k.End;
                return ch;
            }
            return null;
        }

        /// <summary>Fin de soporte del canal (perpetuos: 2019, 2021, 2024), conocido aunque el listado no lo diga.</summary>
        public static DateTime? EndOfSupportOf(OfficeChannel ch)
        {
            if (ch == null) return null;
            if (ch.EndOfSupport.HasValue) return ch.EndOfSupport;
            foreach (var k in KnownChannels) if (k.Id.Equals(ch.Id, StringComparison.OrdinalIgnoreCase)) return k.End;
            return null;
        }

        /// <summary>
        /// Canal que corresponde a los productos cuando el registro no dice ninguno: los de licencia por volumen
        /// («ProPlus2021Volume») van en su canal LTSC; los comerciales, en el Canal actual.
        /// </summary>
        static OfficeChannel KnownByProducts(string products)
        {
            if (string.IsNullOrEmpty(products)) return null;
            if (Regex.IsMatch(products, @"2024Volume", RegexOptions.IgnoreCase)) return Known(null, "PerpetualVL2024");
            if (Regex.IsMatch(products, @"2021Volume", RegexOptions.IgnoreCase)) return Known(null, "PerpetualVL2021");
            if (Regex.IsMatch(products, @"2019Volume", RegexOptions.IgnoreCase)) return Known(null, "PerpetualVL2019");
            if (Regex.IsMatch(products, @"Retail|O365|Microsoft365", RegexOptions.IgnoreCase)) return Known(null, "Current");
            return null;
        }

        /// <summary>
        /// Activa o desactiva las actualizaciones de Office (Configuration\UpdatesEnabled), lo mismo que
        /// Archivo › Cuenta › Opciones de actualización. Necesita administrador. Devuelve si se pudo.
        /// </summary>
        public static bool SetUpdatesEnabled(OfficeConfig cfg, bool enabled)
        {
            if (cfg == null || !cfg.Found) return false;
            try
            {
                var view = cfg.ConfigView == Microsoft.Win32.RegistryView.Default ? Reg.Native : cfg.ConfigView;
                using (var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view))
                using (var k = root.OpenSubKey(ConfigKey, true))
                {
                    if (k == null) return false;
                    k.SetValue("UpdatesEnabled", enabled ? "True" : "False", Microsoft.Win32.RegistryValueKind.String);
                    return true;
                }
            }
            catch (Exception ex) { Log.Warn("No se pudo cambiar UpdatesEnabled de Office: " + ex.Message); return false; }
        }

        static bool NameMatches(OfficeChannel ch, string name)
        {
            if (name.Equals(ch.Id, StringComparison.OrdinalIgnoreCase) || name.Equals(ch.Name, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string a in ch.AlternateNames) if (name.Equals(a, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string ChannelTitle(OfficeChannel ch)
        {
            if (ch == null) return "canal desconocido";
            switch (ch.Id)
            {
                case "Current": return "Canal actual";
                case "CurrentPreview": return "Canal actual (versión preliminar)";
                case "MonthlyEnterprise": return "Canal mensual para empresas";
                case "SemiAnnual": return "Canal semestral para empresas";
                case "SemiAnnualPreview": return "Canal semestral (versión preliminar)";
                case "BetaChannel": return "Canal beta";
                case "PerpetualVL2024": return "Office LTSC 2024";
                case "PerpetualVL2021": return "Office LTSC 2021";
                case "PerpetualVL2019": return "Office LTSC 2019";
                default: return "canal " + ch.Id;
            }
        }
    }
}
