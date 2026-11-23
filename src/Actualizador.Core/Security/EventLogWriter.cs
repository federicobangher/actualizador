// Eventos en el Visor de eventos de Windows (registro «Application», origen «Actualizador») por P/Invoke.
using System;
using System.Runtime.InteropServices;

namespace Actualizador.Core.Security
{
    /// <summary>
    /// ReportEventW de advapi32: no hace falta el paquete System.Diagnostics.EventLog. El origen «Actualizador» lo registra el
    /// instalador (Install-Actualizador.ps1, como administrador); sin registrar, Windows igual guarda el evento con el texto.
    /// Los sistemas de monitoreo de la empresa (SIEM, reenvío de eventos) lo toman de ahí.
    /// </summary>
    public static class EventLogWriter
    {
        public const string Source = "Actualizador";

        public enum Level : short { Error = 1, Warning = 2, Information = 4 }

        /// <summary>Identificadores de evento (para filtrar en el Visor de eventos o en un SIEM).</summary>
        public static class Ids
        {
            public const int ScanFinished = 1000;
            /// <summary>Los escriben Install-Actualizador.ps1 y Uninstall-Actualizador.ps1.</summary>
            public const int Installed = 1001, Uninstalled = 1002;
            public const int InstallStarted = 1100;
            public const int UpdateSucceeded = 1101;
            public const int UpdateFailed = 1102;
            public const int InstallFinished = 1103;
            public const int PolicyBlocked = 1200;
            public const int PolicyLoaded = 1201;
            public const int PolicyRejected = 1202;
            public const int AuditTampered = 1300;
        }

        /// <summary>Sólo para las pruebas: en vez de escribir en Windows.</summary>
        internal static Action<int, Level, string> WriteForTests;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr RegisterEventSourceW(string server, string source);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DeregisterEventSource(IntPtr handle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ReportEventW(IntPtr handle, short type, short category, int eventId, IntPtr sid, short numStrings, int dataSize, string[] strings, IntPtr data);

        public static bool Write(int eventId, Level level, string message)
        {
            if (WriteForTests != null) { WriteForTests(eventId, level, message); return true; }
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = RegisterEventSourceW(null, Source);
                if (h == IntPtr.Zero) return false;
                // El Visor de eventos limita cada texto a 31.839 caracteres.
                string text = message ?? "";
                if (text.Length > 31000) text = text.Substring(0, 31000) + "…";
                return ReportEventW(h, (short)level, 0, eventId, IntPtr.Zero, 1, 0, new[] { text }, IntPtr.Zero);
            }
            catch (Exception ex) { Log.Quiet("Visor de eventos", ex); return false; }
            finally { if (h != IntPtr.Zero) DeregisterEventSource(h); }
        }
    }
}
