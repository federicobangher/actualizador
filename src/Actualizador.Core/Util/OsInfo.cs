using System;
using System.Globalization;
using System.Security.Principal;
using Microsoft.Win32;

namespace Actualizador.Core
{
    /// <summary>
    /// Versión real de Windows. Usa RtlGetVersion (no afectado por el "manifest shim"
    /// que hace que Environment.OSVersion mienta en 8.1/10/11) y corrige el ProductName
    /// de Windows 11, que en el registro sigue diciendo "Windows 10".
    /// </summary>
    public sealed class OsInfo
    {
        public int Major, Minor, Build, Ubr;
        public string ProductName = "Windows";
        public string EditionId = "";
        public string DisplayVersion = "";
        public string InstallationType = "";
        public string NativeArchitecture = "";
        public bool IsServer;
        public bool IsWindows;

        public static OsInfo Detect()
        {
            var os = new OsInfo();
            os.IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            var v = Environment.OSVersion.Version;
            os.Major = v.Major; os.Minor = v.Minor; os.Build = v.Build;
            if (!os.IsWindows) { os.ProductName = Environment.OSVersion.VersionString; return os; }

            try
            {
                var info = new Native.OSVERSIONINFOEX();
                info.dwOSVersionInfoSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.OSVERSIONINFOEX));
                if (Native.RtlGetVersion(ref info) == 0)
                {
                    os.Major = info.dwMajorVersion; os.Minor = info.dwMinorVersion; os.Build = info.dwBuildNumber;
                    os.IsServer = info.wProductType != 1; // VER_NT_WORKSTATION = 1
                }
            }
            catch (Exception qx) { Log.Quiet("RtlGetVersion", qx); }

            const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            os.ProductName = Reg.Str(RegistryHive.LocalMachine, cv, "ProductName") ?? "Windows";
            os.EditionId = Reg.Str(RegistryHive.LocalMachine, cv, "EditionID") ?? "";
            os.InstallationType = Reg.Str(RegistryHive.LocalMachine, cv, "InstallationType") ?? "";
            os.DisplayVersion = Reg.Str(RegistryHive.LocalMachine, cv, "DisplayVersion")
                                ?? Reg.Str(RegistryHive.LocalMachine, cv, "ReleaseId") ?? "";
            os.Ubr = Reg.Int(RegistryHive.LocalMachine, cv, "UBR") ?? 0;
            if (os.InstallationType.IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0) os.IsServer = true;
            if (os.IsWindows11 && os.ProductName.Contains("Windows 10"))
                os.ProductName = os.ProductName.Replace("Windows 10", "Windows 11");

            os.NativeArchitecture = Reg.Str(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "PROCESSOR_ARCHITECTURE")
                                    ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            if (os.NativeArchitecture.Equals("AMD64", StringComparison.OrdinalIgnoreCase)) os.NativeArchitecture = "x64";
            else if (os.NativeArchitecture.Equals("x86", StringComparison.OrdinalIgnoreCase)) os.NativeArchitecture = "x86";
            // Windows en ARM: «ARM64» en mayúsculas no coincidía con «arm64» al elegir el instalador de winget,
            // del catálogo o del navegador, y en esos equipos se ofrecía el de x64 o ninguno.
            else if (os.NativeArchitecture.Equals("ARM64", StringComparison.OrdinalIgnoreCase)) os.NativeArchitecture = "arm64";
            return os;
        }

        public bool AtLeast(int major, int minor, int build = 0)
        {
            if (Major != major) return Major > major;
            if (Minor != minor) return Minor > minor;
            return Build >= build;
        }

        public bool IsWindows8OrLater { get { return IsWindows && AtLeast(6, 2); } }
        public bool IsWindows10OrLater { get { return IsWindows && AtLeast(10, 0); } }
        public bool IsWindows11 { get { return IsWindows && Major == 10 && Build >= 22000 && !IsServer; } }

        public ulong Encoded { get { return VersionUtil.Encode64(Major, Minor, Build, 0); } }

        public string Version { get { return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}", Major, Minor, Build, Ubr); } }

        public string FriendlyName
        {
            get
            {
                string s = ProductName;
                if (!string.IsNullOrEmpty(DisplayVersion)) s += " " + DisplayVersion;
                s += " (compilación " + Build + (Ubr > 0 ? "." + Ubr : "") + ")";
                if (!string.IsNullOrEmpty(NativeArchitecture)) s += " " + NativeArchitecture;
                return s;
            }
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Fin de soporte según el ciclo de vida publicado por Microsoft. Las fechas están en una sola tabla
    /// (<see cref="WindowsReleases"/>); una compilación que no está en la tabla NO recibe la fecha de la versión
    /// más parecida: queda «desconocida» (antes, la 26H2 heredó la fecha de la 25H2 y lo informaba como cierto).
    /// </summary>
    public static class Lifecycle
    {
        public sealed class Result
        {
            public DateTime? EndOfSupport;
            public string Label;
            public string Note;
            /// <summary>La compilación no está en la tabla: más nueva que las conocidas, o preliminar (Insider).</summary>
            public bool Unknown;
            /// <summary>La versión que se reconoció («Windows 11 25H2», «Windows Server 2022»…), o null.</summary>
            public string ReleaseName;
        }

        public static Result Evaluate(OsInfo os) { return Evaluate(os, DateTime.Today); }

        public static Result Evaluate(OsInfo os, DateTime today)
        {
            var r = new Result();
            if (os == null || !os.IsWindows) { r.Label = "No es Windows"; return r; }
            string ed = (os.EditionId ?? "").ToLowerInvariant();
            bool enterprise = ed.Contains("enterprise") || ed.Contains("education");
            // EnterpriseS, IoTEnterpriseS: las ediciones LTSC/LTSB. IoT Enterprise LTSC tiene 10 años de soporte (la común, 5).
            bool ltsc = ed.Contains("enterprises") || ed.Contains("ltsc");
            bool iotLtsc = ed.Contains("iotenterprises");

            var rel = WindowsReleases.Find(os.Major, os.Minor, os.Build, os.IsServer, ltsc);
            if (rel == null)
            {
                r.Unknown = true;
                r.Label = "Fecha de fin de soporte desconocida";
                r.Note = WindowsReleases.UnknownExplanation(os.Major, os.Minor, os.Build, os.IsServer);
                return r;
            }
            r.ReleaseName = rel.Name;
            r.EndOfSupport = iotLtsc && rel.EndIot.HasValue ? rel.EndIot : enterprise && rel.EndEnterprise.HasValue ? rel.EndEnterprise : rel.EndHome;
            r.Note = rel.Note;
            if (!os.IsServer && os.Major == 10 && os.Build == 19045 && !ltsc) r.Note = Windows10Esu(enterprise, today);

            if (r.EndOfSupport.HasValue)
            {
                double days = (r.EndOfSupport.Value - today).TotalDays;
                if (days < 0) r.Label = "Sin soporte desde " + Fmt.Date(r.EndOfSupport.Value);
                else if (days <= 90) r.Label = "El soporte termina pronto (" + Fmt.Date(r.EndOfSupport.Value) + ")";
                else r.Label = "Con soporte hasta " + Fmt.Date(r.EndOfSupport.Value);
            }
            return r;
        }

        /// <summary>Fin de las actualizaciones extendidas (ESU) de Windows 10 22H2: hogares y empresas.</summary>
        public static readonly DateTime Windows10EsuConsumerEnd = new DateTime(2026, 10, 13);
        public static readonly DateTime Windows10EsuCommercialEnd = new DateTime(2028, 10, 10);

        /// <summary>
        /// Windows 10 22H2 terminó su soporte el 14/10/2025. Después, sólo las ESU: para hogares (Home y Pro) hasta el
        /// 13/10/2026 y para empresas hasta el 10/10/2028. La nota de la 1.6.7 decía «sólo recibe parches con el
        /// programa ESU» también después del 13/10/2026, cuando para un equipo de hogar ya no hay ninguno.
        /// </summary>
        public static string Windows10Esu(bool enterprise, DateTime today)
        {
            const string fin = "Windows 10 22H2 terminó su soporte el 14/10/2025. ";
            if (enterprise)
                return today <= Windows10EsuCommercialEnd
                    ? fin + "Sólo recibe parches con las actualizaciones extendidas (ESU) para empresas, hasta el " + Fmt.Date(Windows10EsuCommercialEnd) + " como máximo."
                    : fin + "Las actualizaciones extendidas (ESU) para empresas terminaron el " + Fmt.Date(Windows10EsuCommercialEnd) + ": ya no recibe parches.";
            if (today <= Windows10EsuConsumerEnd)
                return fin + "Sólo recibe parches inscripto en las actualizaciones extendidas (ESU) para hogares, hasta el " + Fmt.Date(Windows10EsuConsumerEnd) + ".";
            return fin + "Las actualizaciones extendidas (ESU) para hogares terminaron el " + Fmt.Date(Windows10EsuConsumerEnd) +
                   ": salvo que tenga las ESU para empresas (que se contratan con licencias por volumen, hasta el " + Fmt.Date(Windows10EsuCommercialEnd) +
                   "), este equipo ya no recibe parches de seguridad.";
        }
    }
}
