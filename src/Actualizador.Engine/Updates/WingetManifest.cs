using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Un instalador del manifiesto, con los valores de la raíz ya aplicados.</summary>
    public sealed class WingetInstaller
    {
        public string Architecture = "", Type = "", NestedType = "", Scope = "", Locale = "";
        public string Url = "", Sha256 = "", UpgradeBehavior = "", Elevation = "";
        public string NestedFile = "";
        /// <summary>MinimumOSVersion del manifiesto («10.0.17763.0»), o "".</summary>
        public string MinOs = "";
        public bool IsArchive { get { return Type == "zip"; } }
        public string EffectiveType { get { return IsArchive && !string.IsNullOrEmpty(NestedType) ? NestedType : Type; } }
        /// <summary>InstallerSwitches (Silent, SilentWithProgress, Interactive, Custom, Upgrade…).</summary>
        public readonly Dictionary<string, string> Switches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<long> SuccessCodes = new List<long>();
        /// <summary>InstallModes declarados (interactive, silent, silentWithProgress), en minúsculas.</summary>
        public readonly List<string> InstallModes = new List<string>();
        /// <summary>ExpectedReturnCodes: código del instalador → ReturnResponse (packageInUse, rebootRequiredToFinish…).</summary>
        public readonly Dictionary<long, string> ReturnCodes = new Dictionary<long, string>();

        public bool IsMsi { get { return EffectiveType == "msi" || EffectiveType == "wix"; } }

        public string Switch(string name)
        {
            string v;
            return Switches.TryGetValue(name, out v) ? v : null;
        }
    }

    /// <summary>
    /// Manifiesto de instalador del repositorio oficial de winget (github.com/microsoft/winget-pkgs):
    /// dirección, SHA-256, tipo e interruptores de cada instalador. Es el mismo archivo que usa
    /// winget; se lee directamente para poder descargar e instalar sin winget cuando winget falla.
    /// </summary>
    public sealed class WingetManifest
    {
        public string Id = "", Version = "";
        public readonly List<WingetInstaller> Installers = new List<WingetInstaller>();

        static readonly string[] Inherited =
        {
            "InstallerType", "NestedInstallerType", "Scope", "InstallerLocale", "UpgradeBehavior", "ElevationRequirement",
            "Architecture", "InstallerUrl", "InstallerSha256", "MinimumOSVersion"
        };

        public static WingetManifest Parse(string yaml)
        {
            var root = MiniYaml.Parse(yaml) as Dictionary<string, object>;
            if (root == null) throw new FormatException("el manifiesto no es un mapa YAML");
            var m = new WingetManifest();
            m.Id = MiniYaml.Str(root, "PackageIdentifier");
            m.Version = MiniYaml.Str(root, "PackageVersion");
            var list = MiniYaml.List(root, "Installers");
            if (list == null) return m;
            foreach (object o in list)
            {
                var item = o as Dictionary<string, object>;
                if (item == null) continue;
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string k in Inherited)
                {
                    string v = MiniYaml.Str(item, k);
                    if (v.Length == 0) v = MiniYaml.Str(root, k);
                    fields[k] = v.Trim();
                }
                var i = new WingetInstaller();
                i.Architecture = fields["Architecture"].ToLowerInvariant();
                i.Type = fields["InstallerType"].ToLowerInvariant();
                i.NestedType = fields["NestedInstallerType"].ToLowerInvariant();
                i.Scope = fields["Scope"].ToLowerInvariant();
                i.Locale = fields["InstallerLocale"];
                i.UpgradeBehavior = fields["UpgradeBehavior"];
                i.Elevation = fields["ElevationRequirement"];
                i.Url = fields["InstallerUrl"];
                i.Sha256 = fields["InstallerSha256"].ToLowerInvariant();
                i.MinOs = fields["MinimumOSVersion"];
                i.NestedFile = NestedRelativeFile(item);
                if (i.NestedFile.Length == 0) i.NestedFile = NestedRelativeFile(root);
                AddSwitches(i, MiniYaml.Map(root, "InstallerSwitches"));
                AddSwitches(i, MiniYaml.Map(item, "InstallerSwitches"));       // lo del instalador pisa a la raíz
                var codes = MiniYaml.List(item, "InstallerSuccessCodes") ?? MiniYaml.List(root, "InstallerSuccessCodes");
                if (codes != null)
                    foreach (object c in codes) { long n; if (TryLong(c as string, out n)) i.SuccessCodes.Add(n); }
                var modes = MiniYaml.List(item, "InstallModes") ?? MiniYaml.List(root, "InstallModes");
                if (modes != null)
                    foreach (object o2 in modes) { string md = o2 as string; if (!string.IsNullOrEmpty(md)) i.InstallModes.Add(md.Trim().ToLowerInvariant()); }
                AddReturnCodes(i, MiniYaml.List(root, "ExpectedReturnCodes"));
                AddReturnCodes(i, MiniYaml.List(item, "ExpectedReturnCodes"));
                m.Installers.Add(i);
            }
            return m;
        }

        static string NestedRelativeFile(Dictionary<string, object> map)
        {
            if (map == null) return "";
            var list = MiniYaml.List(map, "NestedInstallerFiles");
            if (list == null || list.Count == 0) return "";
            foreach (object o in list)
            {
                string p = MiniYaml.Str(o, "RelativeFilePath");
                if (!string.IsNullOrEmpty(p)) return p.Trim();
            }
            return "";
        }

        static void AddSwitches(WingetInstaller i, Dictionary<string, object> sw)
        {
            if (sw == null) return;
            foreach (var kv in sw)
            {
                string v = kv.Value as string;
                if (v != null) i.Switches[kv.Key] = v.Trim();
            }
        }

        static void AddReturnCodes(WingetInstaller i, List<object> list)
        {
            if (list == null) return;
            foreach (object o in list)
            {
                long n;
                if (!TryLong(MiniYaml.Str(o, "InstallerReturnCode"), out n)) continue;
                i.ReturnCodes[n] = MiniYaml.Str(o, "ReturnResponse");
            }
        }

        static bool TryLong(string s, out long n)
        {
            n = 0;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                ulong u;
                if (!ulong.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out u)) return false;
                if (u > uint.MaxValue) return false;
                n = (long)u;
            }
            else if (!long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) return false;
            // Mismo valor que el código de salida del proceso (Int32): 0x8A150011 y 2316632081 → -1978335215.
            if (n > int.MaxValue && n <= uint.MaxValue) n = unchecked((int)(uint)n);
            return n >= int.MinValue && n <= int.MaxValue;
        }
    }
}
