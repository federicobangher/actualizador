// Lista de procesos con la API de Windows, sin WMI.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Actualizador.Core
{
    /// <summary>Un proceso del sistema: PID, padre, sesión, nombre, ruta del ejecutable y cuándo se creó.</summary>
    public sealed class ProcessEntry
    {
        public int Pid;
        public int ParentPid = -1;
        public int SessionId = -1;
        public string Name = "";
        /// <summary>Ruta completa del ejecutable; "" si no se pudo leer (proceso protegido, de otro usuario sin privilegios…).</summary>
        public string Path = "";
        public DateTime? Created;
    }

    /// <summary>
    /// Procesos abiertos leídos con la API de Windows (Toolhelp32 + QueryFullProcessImageName). Hasta la 1.6.8 se
    /// consultaba WMI (Win32_Process) en cada instalación, al cortar un árbol de procesos y, cada 1,5 s, mientras un
    /// instalador seguía en segundo plano: cada consulta hace trabajar al servicio WMI (WmiPrvSE.exe) un buen rato, y
    /// con WMI dañado o lento las instalaciones quedaban esperando. La API tarda milisegundos y no depende de WMI.
    /// Si falla (o fuera de Windows) se usa WMI como antes.
    /// </summary>
    public static class ProcessList
    {
        /// <summary>Sólo para las pruebas: reemplaza la lectura (null = lectura real).</summary>
        public static Func<List<ProcessEntry>> ForTests;

        /// <summary>Todos los procesos; con <paramref name="withDetails"/>, también la ruta, la sesión y la fecha de creación.</summary>
        public static List<ProcessEntry> Snapshot(bool withDetails)
        {
            if (ForTests != null) return ForTests() ?? new List<ProcessEntry>();
            string err;
            var list = NativeSnapshot(withDetails, out err);
            if (list != null) return list;
            Log.Quiet("Procesos con la API de Windows (se usa WMI)", new InvalidOperationException(err));
            return WmiSnapshot();
        }

        static List<ProcessEntry> WmiSnapshot()
        {
            var list = new List<ProcessEntry>();
            string err;
            foreach (var o in Wmi.TryQuery(Wmi.Cimv2, "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, SessionId, CreationDate FROM Win32_Process", out err, 20))
            {
                var e = new ProcessEntry();
                e.Pid = (int)(Wmi.L(o, "ProcessId") ?? -1);
                if (e.Pid < 0) continue;
                e.ParentPid = (int)(Wmi.L(o, "ParentProcessId") ?? -1);
                e.SessionId = (int)(Wmi.L(o, "SessionId") ?? -1);
                e.Name = Wmi.S(o, "Name") ?? "";
                e.Path = Wmi.S(o, "ExecutablePath") ?? "";
                e.Created = Wmi.D(o, "CreationDate");
                list.Add(e);
            }
            if (err != null) Log.Warn("Procesos (WMI): " + err);
            return list;
        }

        // ---------------------------------------------------------------- API de Windows
        const uint TH32CS_SNAPPROCESS = 0x00000002;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FILETIME { public uint Low, High; }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetProcessTimes(IntPtr process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ProcessIdToSessionId(uint pid, out uint session);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr handle);

        /// <summary>null (con el motivo en <paramref name="error"/>) si la API no está disponible o falló.</summary>
        static List<ProcessEntry> NativeSnapshot(bool withDetails, out string error)
        {
            error = null;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) { error = "no es Windows"; return null; }
            IntPtr snap = IntPtr.Zero;
            try
            {
                snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snap == IntPtr.Zero || snap == new IntPtr(-1)) { error = "CreateToolhelp32Snapshot: " + Marshal.GetLastWin32Error(); snap = IntPtr.Zero; return null; }
                var list = new List<ProcessEntry>();
                var pe = new PROCESSENTRY32W();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                if (!Process32FirstW(snap, ref pe)) { error = "Process32First: " + Marshal.GetLastWin32Error(); return null; }
                do
                {
                    var e = new ProcessEntry();
                    e.Pid = unchecked((int)pe.th32ProcessID);
                    e.ParentPid = unchecked((int)pe.th32ParentProcessID);
                    e.Name = pe.szExeFile ?? "";
                    if (withDetails && e.Pid > 4) FillDetails(e);
                    list.Add(e);
                    pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                }
                while (Process32NextW(snap, ref pe));
                return list;
            }
            catch (Exception ex)
            {
                // DllNotFoundException / EntryPointNotFoundException (fuera de Windows) y cualquier otra falla: WMI.
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
            finally
            {
                if (snap != IntPtr.Zero) { try { CloseHandle(snap); } catch { } }
            }
        }

        static void FillDetails(ProcessEntry e)
        {
            uint session;
            try { if (ProcessIdToSessionId((uint)e.Pid, out session)) e.SessionId = unchecked((int)session); } catch { }
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)e.Pid);
                if (h == IntPtr.Zero) return;            // protegido o de otro usuario: sin ruta, como en WMI
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageNameW(h, 0, sb, ref size)) e.Path = sb.ToString(0, (int)Math.Min(size, (uint)sb.Length));
                FILETIME c, x, k, u;
                if (GetProcessTimes(h, out c, out x, out k, out u))
                {
                    long ft = ((long)c.High << 32) | c.Low;
                    if (ft > 0) { try { e.Created = DateTime.FromFileTime(ft); } catch { } }
                }
            }
            catch { }
            finally
            {
                if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { } }
            }
        }

        // ---------------------------------------------------------------- Servicios en ejecución

        /// <summary>Sólo para las pruebas: reemplaza la lectura de los PID de los servicios.</summary>
        public static Func<HashSet<int>> ServicePidsForTests;

        const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
        const int SC_ENUM_PROCESS_INFO = 0;
        const uint SERVICE_WIN32 = 0x00000030;
        const uint SERVICE_ACTIVE = 0x00000001;
        const int ERROR_MORE_DATA = 234;

        [StructLayout(LayoutKind.Sequential)]
        struct ENUM_SERVICE_STATUS_PROCESS
        {
            public IntPtr lpServiceName;
            public IntPtr lpDisplayName;
            public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenSCManagerW(string machine, string database, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool EnumServicesStatusExW(IntPtr manager, int infoLevel, uint serviceType, uint serviceState, IntPtr buffer, uint bufSize,
                                                 out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string groupName);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseServiceHandle(IntPtr handle);

        /// <summary>PID de los procesos que alojan un servicio en ejecución (con WMI si la API falla).</summary>
        public static HashSet<int> ServicePids()
        {
            if (ServicePidsForTests != null) return ServicePidsForTests() ?? new HashSet<int>();
            string err;
            var set = NativeServicePids(out err);
            if (set != null) return set;
            Log.Quiet("Servicios con la API de Windows (se usa WMI)", new InvalidOperationException(err));
            set = new HashSet<int>();
            foreach (var o in Wmi.TryQuery(Wmi.Cimv2, "SELECT ProcessId FROM Win32_Service WHERE State='Running'", out err, 30))
            {
                long? pid = Wmi.L(o, "ProcessId");
                if (pid.HasValue && pid.Value > 0) set.Add((int)pid.Value);
            }
            if (err != null) Log.Warn("Servicios (WMI): " + err);
            return set;
        }

        static HashSet<int> NativeServicePids(out string error)
        {
            error = null;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) { error = "no es Windows"; return null; }
            IntPtr scm = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
                if (scm == IntPtr.Zero) { error = "OpenSCManager: " + Marshal.GetLastWin32Error(); return null; }
                var set = new HashSet<int>();
                int stride = Marshal.SizeOf(typeof(ENUM_SERVICE_STATUS_PROCESS));
                uint size = 64 * 1024, resume = 0;
                buf = Marshal.AllocHGlobal((int)size);
                for (int round = 0; round < 50; round++)
                {
                    uint needed, returned;
                    bool ok = EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, buf, size, out needed, out returned, ref resume, null);
                    int last = ok ? 0 : Marshal.GetLastWin32Error();
                    if (!ok && last != ERROR_MORE_DATA) { error = "EnumServicesStatusEx: " + last; return null; }
                    for (int i = 0; i < returned; i++)
                    {
                        var s = (ENUM_SERVICE_STATUS_PROCESS)Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + (long)i * stride), typeof(ENUM_SERVICE_STATUS_PROCESS));
                        if (s.dwProcessId > 0) set.Add(unchecked((int)s.dwProcessId));
                    }
                    if (ok) return set;
                    // Faltó lugar: lo que vino ya se tomó; se sigue desde «resume» con un búfer más grande si hace falta.
                    if (needed > size)
                    {
                        Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                        size = needed + 4096;
                        buf = Marshal.AllocHGlobal((int)size);
                    }
                }
                error = "EnumServicesStatusEx: demasiadas vueltas";
                return null;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (scm != IntPtr.Zero) { try { CloseServiceHandle(scm); } catch { } }
            }
        }
    }
}
