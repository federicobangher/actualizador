using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Microsoft.Win32;

namespace Actualizador.Inventory
{
    /// <summary>
    /// Carga el inventario tipado que comparten las secciones, los hallazgos y el motor
    /// de actualizaciones. Cada carga se hace una sola vez por contexto.
    /// Nota: NUNCA se usa Win32_Product (dispara la reconfiguración de todos los MSI).
    /// </summary>
    public static class InventoryLoader
    {
        const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        // ------------------------------------------------------------------ Programas
        public static List<InstalledProgram> EnsurePrograms(UpdateContext ctx)
        {
            lock (ctx.Programs)
            {
                if (ctx.ProgramsLoaded) return ctx.Programs;
                ctx.Report("Leyendo programas instalados del registro…");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var list = new List<InstalledProgram>();

                foreach (RegistryView view in Reg.Views())
                {
                    string arch = view == RegistryView.Registry64 ? NativeArch(ctx) : "x86";
                    using (var k = Reg.Open(RegistryHive.LocalMachine, UninstallPath, view))
                        ReadUninstall(k, arch, "Equipo", @"HKLM\" + UninstallPath, list, seen);
                }

                using (var k = Reg.Open(RegistryHive.CurrentUser, UninstallPath))
                    ReadUninstall(k, "", "Usuario actual", @"HKCU\" + UninstallPath, list, seen);

                // Perfiles de otros usuarios con el hive cargado (p. ej. al elevar con otra cuenta).
                string currentSid = null;
                try { using (var id = WindowsIdentity.GetCurrent()) currentSid = id.User == null ? null : id.User.Value; } catch { }
                using (var users = Reg.Open(RegistryHive.Users, ""))
                {
                    foreach (string sid in Reg.SubKeys(users))
                    {
                        if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(sid, currentSid, StringComparison.OrdinalIgnoreCase)) continue;
                        using (var k = Reg.Open(RegistryHive.Users, sid + @"\" + UninstallPath))
                            ReadUninstall(k, "", "Usuario " + AccountName(sid), @"HKU\" + sid + @"\" + UninstallPath, list, seen);
                    }
                }

                // los que no tienen entrada de desinstalación (portables, o de otra cuenta sin sesión abierta), por sus accesos directos.
                ctx.Report("Buscando programas sin registro de instalación (accesos directos)…");
                list.AddRange(ShortcutPrograms.Find(list));

                list.Sort(delegate(InstalledProgram a, InstalledProgram b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                });
                ctx.Programs.Clear();
                ctx.Programs.AddRange(list);
                ctx.ProgramsLoaded = true;
                return ctx.Programs;
            }
        }

        /// <summary>
        /// Descarta el inventario cargado (programas, controladores y apps de la Store). Se llama después
        /// de instalar: sin esto, la siguiente búsqueda en la misma sesión comparaba con las versiones
        /// de ANTES de actualizar (Vivaldi, Samsung o el nombre de un programa reinstalado).
        /// </summary>
        public static void Invalidate(UpdateContext ctx)
        {
            if (ctx == null) return;
            lock (ctx.Programs) ctx.ProgramsLoaded = false;
            lock (ctx.Drivers) ctx.DriversLoaded = false;
            lock (ctx.Appx) ctx.AppxLoaded = false;
        }

        /// <summary>
        /// Programas del equipo y del usuario actual leídos AHORA del registro, sin tocar el
        /// inventario del contexto: sirve para confirmar qué versión quedó después de instalar.
        /// </summary>
        public static List<InstalledProgram> ReadProgramsNow(string nativeArch)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<InstalledProgram>();
            string native = string.IsNullOrEmpty(nativeArch) ? "x64" : nativeArch;
            foreach (RegistryView view in Reg.Views())
                using (var k = Reg.Open(RegistryHive.LocalMachine, UninstallPath, view))
                    ReadUninstall(k, view == RegistryView.Registry64 ? native : "x86", "Equipo", @"HKLM\" + UninstallPath, list, seen);
            using (var k = Reg.Open(RegistryHive.CurrentUser, UninstallPath))
                ReadUninstall(k, "", "Usuario actual", @"HKCU\" + UninstallPath, list, seen);
            return list;
        }

        /// <summary>
        /// ¿Sigue la entrada de desinstalación de ese programa? Lee SÓLO su clave: al reinstalar se pregunta
        /// cada 2 s durante hasta 3 minutos, y releer todo el registro de programas cada vez gastaba CPU sin necesidad.
        /// null si no se puede saber así (la ruta no tiene la forma esperada).
        /// </summary>
        public static bool? EntryExists(InstalledProgram p)
        {
            if (p == null || string.IsNullOrEmpty(p.RegistryKey)) return null;
            string rk = p.RegistryKey;
            RegistryHive hive;
            string sub;
            if (rk.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)) { hive = RegistryHive.LocalMachine; sub = rk.Substring(5); }
            else if (rk.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)) { hive = RegistryHive.CurrentUser; sub = rk.Substring(5); }
            else if (rk.StartsWith(@"HKU\", StringComparison.OrdinalIgnoreCase)) { hive = RegistryHive.Users; sub = rk.Substring(4); }
            else return null;
            if (sub.IndexOf(UninstallPath + @"\", StringComparison.OrdinalIgnoreCase) < 0) return null;
            RegistryView view = Reg.Native;
            if (hive == RegistryHive.LocalMachine && Environment.Is64BitOperatingSystem && string.Equals(p.Architecture, "x86", StringComparison.OrdinalIgnoreCase))
                view = RegistryView.Registry32;
            using (var k = Reg.Open(hive, sub, view))
                return k != null && !string.IsNullOrEmpty(Reg.Str(k, "DisplayName"));
        }

        static string NativeArch(UpdateContext ctx)
        {
            string a = ctx.Os != null ? ctx.Os.NativeArchitecture : "";
            return string.IsNullOrEmpty(a) ? "x64" : a;
        }

        static void ReadUninstall(RegistryKey root, string arch, string scope, string path, List<InstalledProgram> list, HashSet<string> seen)
        {
            if (root == null) return;
            foreach (string name in Reg.SubKeys(root))
            {
                RegistryKey k = null;
                try { k = root.OpenSubKey(name, false); } catch (Exception qx) { Log.Quiet("Clave de desinstalación ilegible", qx); }
                if (k == null) continue;
                using (k)
                {
                    string display = Reg.Str(k, "DisplayName");
                    if (string.IsNullOrEmpty(display)) continue;
                    if ((Reg.Long(k, "SystemComponent") ?? 0) == 1) continue;
                    if (!string.IsNullOrEmpty(Reg.Str(k, "ParentKeyName"))) continue;
                    string release = Reg.Str(k, "ReleaseType") ?? "";
                    if (release.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0 || release.IndexOf("Hotfix", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (Regex.IsMatch(display, @"^(Security Update|Update|Hotfix) for ", RegexOptions.IgnoreCase) && display.Contains("(KB")) continue;

                    var p = new InstalledProgram();
                    p.Name = Native.ResolveIndirect(display).Trim();
                    p.Version = Reg.Str(k, "DisplayVersion") ?? "";
                    p.Publisher = Reg.Str(k, "Publisher") ?? "";
                    p.InstallDate = Fmt.ParseLooseDate(Reg.Str(k, "InstallDate"));
                    p.InstallLocation = (Reg.Str(k, "InstallLocation") ?? "").Trim('"');
                    p.UninstallCommand = Reg.Str(k, "UninstallString") ?? "";
                    p.QuietUninstallCommand = Reg.Str(k, "QuietUninstallString") ?? "";
                    p.UninstallString = p.QuietUninstallCommand.Length > 0 ? p.QuietUninstallCommand : p.UninstallCommand;
                    p.DisplayIcon = Reg.Str(k, "DisplayIcon") ?? "";
                    p.IsWindowsInstaller = (Reg.Long(k, "WindowsInstaller") ?? 0) == 1;
                    p.Architecture = arch;
                    p.Scope = scope;
                    p.RegistryKey = path + @"\" + name;
                    p.SizeKb = Reg.Long(k, "EstimatedSize") ?? 0;
                    p.InfoUrl = Reg.Str(k, "URLInfoAbout") ?? "";
                    p.UpdateUrl = Reg.Str(k, "URLUpdateInfo") ?? "";
                    p.HelpLink = Reg.Str(k, "HelpLink") ?? "";
                    if (p.Architecture.Length == 0) p.Architecture = GuessArch(p);

                    string key = p.Name + "|" + p.Version + "|" + p.Architecture + "|" + p.Scope;
                    if (seen.Add(key)) list.Add(p);
                }
            }
        }

        static string GuessArch(InstalledProgram p)
        {
            string loc = (p.InstallLocation + " " + p.UninstallString).ToLowerInvariant();
            if (loc.Contains("(x86)")) return "x86";
            if (p.Name.IndexOf("x64", StringComparison.OrdinalIgnoreCase) >= 0 || p.Name.IndexOf("64-bit", StringComparison.OrdinalIgnoreCase) >= 0) return "x64";
            return "";
        }

        public static string AccountName(string sid)
        {
            try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
            catch { return sid; }
        }

        // ------------------------------------------------------------------ Controladores
        public static List<DriverInfo> EnsureDrivers(UpdateContext ctx)
        {
            lock (ctx.Drivers)
            {
                if (ctx.DriversLoaded) return ctx.Drivers;
                ctx.Report("Consultando controladores instalados (Win32_PnPSignedDriver)…");
                string err;
                var rows = Wmi.TryQuery(Wmi.Cimv2,
                    "SELECT DeviceName, DeviceClass, Manufacturer, DriverProviderName, DriverVersion, DriverDate, InfName, HardWareID, DeviceID, IsSigned, Signer FROM Win32_PnPSignedDriver",
                    out err, 180);
                ctx.DriversError = err;
                var list = new List<DriverInfo>();
                foreach (var o in rows)
                {
                    string version = Wmi.S(o, "DriverVersion");
                    if (string.IsNullOrEmpty(version)) continue;
                    var d = new DriverInfo();
                    d.DeviceName = Wmi.S(o, "DeviceName") ?? "";
                    d.DeviceClass = Wmi.S(o, "DeviceClass") ?? "";
                    d.Manufacturer = Wmi.S(o, "Manufacturer") ?? "";
                    d.Provider = Wmi.S(o, "DriverProviderName") ?? "";
                    d.Version = version;
                    d.Date = Wmi.D(o, "DriverDate");
                    d.InfName = Wmi.S(o, "InfName") ?? "";
                    d.HardwareId = Wmi.S(o, "HardWareID") ?? "";
                    d.DeviceId = Wmi.S(o, "DeviceID") ?? "";
                    d.IsSigned = Wmi.B(o, "IsSigned") ?? false;
                    d.Signer = Wmi.S(o, "Signer") ?? "";
                    // WMI puede quedar atrasado después de instalar un controlador: manda el registro.
                    var reg = DriverRegistry.Read(d.DeviceId);
                    if (reg != null && VersionUtil.Parse(reg.Version) != null)
                    {
                        if (!reg.Version.Equals(d.Version, StringComparison.OrdinalIgnoreCase))
                            Log.Info("Controlador de " + d.DeviceName + ": WMI informa " + d.Version + ", el registro " + reg.Version + " (se usa el registro)");
                        d.Version = reg.Version;
                        if (reg.Date.HasValue) d.Date = reg.Date;
                        if (reg.InfPath.Length > 0) d.InfName = reg.InfPath;
                    }
                    list.Add(d);
                }
                list.Sort(delegate(DriverInfo a, DriverInfo b)
                {
                    int c = string.Compare(a.DeviceClass, b.DeviceClass, StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : string.Compare(a.DeviceName, b.DeviceName, StringComparison.CurrentCultureIgnoreCase);
                });
                ctx.Drivers.Clear();
                ctx.Drivers.AddRange(list);
                ctx.DriversLoaded = true;
                return ctx.Drivers;
            }
        }

        /// <summary>true si el controlador viene incluido con Windows (proveedor Microsoft o genérico).</summary>
        public static bool IsInboxDriver(DriverInfo d)
        {
            string p = (d.Provider ?? "").Trim();
            return p.Length == 0 || p.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("(Standard", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("(Est", StringComparison.OrdinalIgnoreCase)   // "(Estándar …)" en español
                || (d.InfName ?? "").StartsWith("machine.inf", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ Microsoft Store (AppX/MSIX)
        public static List<AppxInfo> EnsureAppx(UpdateContext ctx)
        {
            lock (ctx.Appx)
            {
                if (ctx.AppxLoaded) return ctx.Appx;
                ctx.AppxLoaded = true;
                ctx.Appx.Clear();
                if (ctx.Os == null || !ctx.Os.IsWindows8OrLater)
                {
                    ctx.AppxError = "Las aplicaciones de Microsoft Store existen a partir de Windows 8.";
                    return ctx.Appx;
                }
                ctx.Report("Consultando aplicaciones de Microsoft Store (Get-AppxPackage)…");
                string error;
                var list = LoadAppxPowerShell(out error);
                if (list == null)
                {
                    Log.Warn("Get-AppxPackage falló (" + error + "); se usa el registro como respaldo.");
                    list = LoadAppxRegistry();
                    ctx.AppxError = list.Count == 0 ? error : null;
                }
                foreach (var a in list)
                {
                    if (string.IsNullOrEmpty(a.DisplayName)) a.DisplayName = ResolveAppxDisplayName(a);
                    if (string.IsNullOrEmpty(a.DisplayName)) a.DisplayName = a.Name;
                }
                list.Sort(delegate(AppxInfo x, AppxInfo y)
                {
                    return string.Compare(x.DisplayName, y.DisplayName, StringComparison.CurrentCultureIgnoreCase);
                });
                ctx.Appx.AddRange(list);
                return ctx.Appx;
            }
        }

        public static List<AppxInfo> LoadAppxPowerShell(out string error)
        {
            error = null;
            const string script =
                "$r = @(Get-AppxPackage -ErrorAction Stop | ForEach-Object { " +
                "[pscustomobject]@{N=$_.Name;V=\"$($_.Version)\";A=\"$($_.Architecture)\";F=$_.PackageFamilyName;" +
                "FN=$_.PackageFullName;S=\"$($_.SignatureKind)\";FW=[bool]$_.IsFramework;L=$_.InstallLocation;P=$_.Publisher} });" +
                "if ($r.Count -eq 0) { '[]' } else { ConvertTo-Json -InputObject $r -Compress -Depth 2 }";
            var r = ProcessRunner.PowerShell(script, 120000);
            if (!r.Ok || r.Out.Trim().Length == 0)
            {
                error = r.Started && !r.TimedOut && r.Err.Trim().Length > 0 ? Fmt.Truncate(r.Err, 200) : r.Describe();
                return null;
            }
            object root;
            try { root = Json.Parse(ExtractJson(r.Out)); }
            catch (Exception ex) { error = "Salida JSON inválida: " + ex.Message; return null; }
            var items = root as List<object>;
            if (items == null) { items = new List<object>(); if (root != null) items.Add(root); }
            var list = new List<AppxInfo>();
            foreach (object it in items)
            {
                var a = new AppxInfo();
                a.Name = Json.Str(it, "N") ?? "";
                a.Version = Json.Str(it, "V") ?? "";
                a.Architecture = Json.Str(it, "A") ?? "";
                a.FamilyName = Json.Str(it, "F") ?? "";
                a.FullName = Json.Str(it, "FN") ?? "";
                a.SignatureKind = Json.Str(it, "S") ?? "";
                a.IsFramework = Json.Str(it, "FW") == "true";
                a.InstallLocation = Json.Str(it, "L") ?? "";
                a.Publisher = Json.Str(it, "P") ?? "";
                if (a.Name.Length > 0) list.Add(a);
            }
            return list;
        }

        /// <summary>Recorta ruido antes/después del JSON (advertencias de PowerShell, etc.).</summary>
        public static string ExtractJson(string text)
        {
            int a = text.IndexOfAny(new[] { '[', '{' });
            int b = Math.Max(text.LastIndexOf(']'), text.LastIndexOf('}'));
            return a >= 0 && b > a ? text.Substring(a, b - a + 1) : text;
        }

        /// <summary>Respaldo sin PowerShell: repositorio de paquetes del usuario en el registro.</summary>
        public static List<AppxInfo> LoadAppxRegistry()
        {
            var list = new List<AppxInfo>();
            const string repo = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
            using (var root = Reg.Open(RegistryHive.CurrentUser, repo))
            {
                foreach (string full in Reg.SubKeys(root))
                {
                    var a = ParseFullName(full);
                    if (a == null) continue;
                    using (var k = root.OpenSubKey(full, false))
                    {
                        string dn = Reg.Str(k, "DisplayName");
                        if (!string.IsNullOrEmpty(dn)) a.DisplayName = dn.StartsWith("@") ? Native.ResolveIndirect(dn) : dn;
                        a.InstallLocation = Reg.Str(k, "PackageRootFolder") ?? "";
                    }
                    a.SignatureKind = a.InstallLocation.IndexOf("SystemApps", StringComparison.OrdinalIgnoreCase) >= 0 ? "System" : "";
                    list.Add(a);
                }
            }
            return list;
        }

        /// <summary>
        /// Versión de cada paquete de la Store del usuario (nombre → la más nueva), sólo con los nombres de las claves
        /// del repositorio de paquetes: sin abrir PowerShell ni resolver nombres visibles. Sirve para medir, cada pocos
        /// segundos, qué actualizó la Store. Vacío si no se pudo leer.
        /// </summary>
        public static Dictionary<string, string> LoadAppxVersionsRegistry()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            const string repo = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
            using (var root = Reg.Open(RegistryHive.CurrentUser, repo))
                foreach (string full in Reg.SubKeys(root))
                {
                    var a = ParseFullName(full);
                    if (a == null || a.IsFramework || VersionUtil.Parse(a.Version) == null) continue;
                    string v;
                    if (!map.TryGetValue(a.Name, out v) || VersionUtil.IsNewer(a.Version, v)) map[a.Name] = a.Version;
                }
            return map;
        }

        /// <summary>Nombre_Versión_Arquitectura_IdRecurso_IdEditor → AppxInfo.</summary>
        public static AppxInfo ParseFullName(string full)
        {
            string[] p = full.Split('_');
            if (p.Length < 5) return null;
            var a = new AppxInfo();
            a.FullName = full;
            a.Name = p[0];
            a.Version = p[1];
            a.Architecture = p[2];
            a.FamilyName = p[0] + "_" + p[p.Length - 1];
            string n = a.Name.ToLowerInvariant();
            a.IsFramework = n.Contains("vclibs") || n.Contains(".net.native") || n.Contains("ui.xaml") || n.Contains("windowsappruntime") || n.Contains("directxruntime");
            a.Publisher = "";
            a.SignatureKind = "";
            return a;
        }

        static readonly Regex DisplayNameRx = new Regex(@"<DisplayName>\s*([^<]+?)\s*</DisplayName>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Nombre visible leído del AppxManifest.xml (resuelve "ms-resource:").</summary>
        public static string ResolveAppxDisplayName(AppxInfo a)
        {
            try
            {
                if (string.IsNullOrEmpty(a.InstallLocation)) return null;
                string manifest = Path.Combine(a.InstallLocation, "AppxManifest.xml");
                if (!File.Exists(manifest)) return null;
                var m = DisplayNameRx.Match(File.ReadAllText(manifest));
                if (!m.Success) return null;
                string v = m.Groups[1].Value.Trim();
                if (!v.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return v;
                string res = v.Substring("ms-resource:".Length);
                string uri;
                if (res.StartsWith("//")) uri = "ms-resource:" + res;
                else if (res.StartsWith("/")) uri = "ms-resource://" + a.Name + res;
                else if (res.Contains("/")) uri = "ms-resource://" + a.Name + "/" + res;
                else uri = "ms-resource://" + a.Name + "/Resources/" + res;
                string resolved = Native.ResolveIndirect("@{" + a.FullName + "?" + uri + "}");
                return resolved != null && !resolved.StartsWith("@{") ? resolved : null;
            }
            catch { return null; }
        }
    }
}
