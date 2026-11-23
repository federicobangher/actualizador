// 2.0.21: modo liviano para equipos con pocos recursos.
using System;
using System.Runtime.InteropServices;

namespace Actualizador.Core
{
    /// <summary>
    /// 2.0.21 (pedido del usuario: «quiero poder usarlo en computadoras medio viejitas, consume muchos recursos»): en un equipo con
    /// 4 procesadores lógicos o menos, o con menos de 6 GB de memoria, la búsqueda hace lo mismo pero con menos cosas a la vez:
    /// las consultas a winget de a una (no tres procesos juntos), una búsqueda por nombre en su catálogo a la vez, y dos páginas
    /// o consultas web a la vez en vez de seis. Tarda un poco más, pero el equipo se puede seguir usando mientras tanto.
    /// La variable de entorno ACTUALIZADOR_LIVIANO=1 (o 0) lo fuerza.
    /// </summary>
    public static class Performance
    {
        static bool? light;

        /// <summary>¿Modo liviano? Se decide una vez por ejecución (las pruebas lo pueden fijar).</summary>
        public static bool Light
        {
            get
            {
                if (!light.HasValue)
                {
                    string forced = null;
                    try { forced = Environment.GetEnvironmentVariable("ACTUALIZADOR_LIVIANO"); } catch { }
                    if (forced == "1") light = true;
                    else if (forced == "0") light = false;
                    else light = Detect(Environment.ProcessorCount, TotalMemoryMb());
                    Log.Info("Equipo: " + Environment.ProcessorCount + " procesadores lógicos, " + (TotalMemoryMb() > 0 ? (TotalMemoryMb() / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB de memoria" : "memoria desconocida") +
                             (light.Value ? ": modo liviano (menos tareas a la vez)" : ""));
                }
                return light.Value;
            }
            set { light = value; }
        }

        /// <summary>¿Es un equipo con pocos recursos? Pública para las pruebas.</summary>
        public static bool Detect(int processors, long memoryMb)
        {
            return processors <= 4 || (memoryMb > 0 && memoryMb < 6 * 1024);
        }

        /// <summary>Cuántos a la vez: <paramref name="normal"/>, o <paramref name="light"/> en modo liviano.</summary>
        public static int Workers(int normal, int lightCount)
        {
            return Light ? Math.Max(1, lightCount) : Math.Max(1, normal);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        /// <summary>Memoria física total en MB (0 si no se sabe).</summary>
        public static long TotalMemoryMb()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return 0;
            try
            {
                var m = new MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                return GlobalMemoryStatusEx(ref m) ? (long)(m.ullTotalPhys / (1024 * 1024)) : 0;
            }
            catch { return 0; }
        }
    }
}
