using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Microsoft.Win32;

namespace Actualizador.Inventory
{
    /// <summary>El controlador que usa un dispositivo según su clave del registro.</summary>
    public sealed class DriverRegistryInfo
    {
        public string Version = "", InfPath = "", Provider = "";
        /// <summary>El identificador con que Windows eligió el controlador (por ejemplo «pci\ven_8086&amp;dev_3e91&amp;subsys_3e911849»).</summary>
        public string MatchingDeviceId = "";
        public DateTime? Date;
    }

    /// <summary>
    /// Versión del controlador de un dispositivo leída del registro (Enum\{dispositivo}\Driver →
    /// Control\Class\{clase}\NNNN). Es donde Windows la escribe al instalar, así que está al día en
    /// el momento: Win32_PnPSignedDriver (WMI) siguió devolviendo la versión anterior después de que
    /// pnputil informó «Paquete de controladores instalado en el dispositivo», y siete controladores
    /// bien instalados figuraron como «Falló la instalación».
    /// </summary>
    public static class DriverRegistry
    {
        /// <summary>Sólo para las pruebas: reemplaza la lectura del registro.</summary>
        public static Func<string, DriverRegistryInfo> ReadForTests;

        static readonly Regex DriverKey = new Regex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}\\\d{4}$", RegexOptions.Compiled);

        /// <summary>Identificador de dispositivo utilizable como ruta del registro (sin «..», sin caracteres de control).</summary>
        public static bool IsSafeDeviceId(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId) || deviceId.Length > 400 || deviceId.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            if (deviceId.StartsWith("\\") || deviceId.EndsWith("\\") || deviceId.IndexOf("\\\\", StringComparison.Ordinal) >= 0) return false;
            foreach (char ch in deviceId) if (ch < 32 || ch == '/') return false;
            return true;
        }

        /// <summary>null si no se puede leer (dispositivo sin controlador, sin permisos o fuera de Windows).</summary>
        public static DriverRegistryInfo Read(string deviceId)
        {
            if (ReadForTests != null) return ReadForTests(deviceId);
            if (!IsSafeDeviceId(deviceId) || Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            try
            {
                string driver = Reg.Str(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Enum\" + deviceId, "Driver");
                if (string.IsNullOrEmpty(driver) || !DriverKey.IsMatch(driver)) return null;
                using (var k = Reg.Open(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Class\" + driver))
                {
                    if (k == null) return null;
                    var info = new DriverRegistryInfo();
                    info.Version = (Reg.Str(k, "DriverVersion") ?? "").Trim();
                    info.InfPath = (Reg.Str(k, "InfPath") ?? "").Trim();
                    info.Provider = (Reg.Str(k, "ProviderName") ?? "").Trim();
                    info.MatchingDeviceId = (Reg.Str(k, "MatchingDeviceId") ?? "").Trim();
                    info.Date = ParseDriverDate(Reg.Str(k, "DriverDate"));
                    return info.Version.Length > 0 ? info : null;
                }
            }
            catch (Exception ex) { Log.Warn("Registro del controlador de " + deviceId + ": " + ex.Message); return null; }
        }

        /// <summary>Sólo para las pruebas: reemplaza la lectura de los identificadores del dispositivo.</summary>
        public static Func<string, string[]> IdsForTests;

        /// <summary>
        /// 2.0.25: los identificadores de hardware y los compatibles del dispositivo (Enum\{dispositivo}: HardwareID y CompatibleIDs), del
        /// más específico al más general. Es con lo que Windows busca un controlador; vacío si no se pueden leer.
        /// </summary>
        public static string[] DeviceIds(string deviceId)
        {
            if (IdsForTests != null) return IdsForTests(deviceId) ?? new string[0];
            if (!IsSafeDeviceId(deviceId) || Environment.OSVersion.Platform != PlatformID.Win32NT) return new string[0];
            var list = new System.Collections.Generic.List<string>();
            try
            {
                foreach (string name in new[] { "HardwareID", "CompatibleIDs" })
                {
                    var v = Reg.Get(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Enum\" + deviceId, name) as string[];
                    if (v == null) continue;
                    foreach (string id in v) if (!string.IsNullOrEmpty(id) && id.Trim().Length > 0) list.Add(id.Trim());
                }
            }
            catch (Exception ex) { Log.Info("Identificadores de " + deviceId + ": " + ex.Message); }
            return list.ToArray();
        }

        /// <summary>«9-23-2025» (como lo escribe Windows) o «9/23/2025».</summary>
        public static DateTime? ParseDriverDate(string s)
        {
            DateTime d;
            if (DateTime.TryParseExact((s ?? "").Trim(), new[] { "M-d-yyyy", "MM-dd-yyyy", "M/d/yyyy", "MM/dd/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                return d;
            return null;
        }
    }
}
