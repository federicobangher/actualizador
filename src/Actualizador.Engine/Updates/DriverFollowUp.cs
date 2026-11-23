// 2.0.25: controladores que se instalaron y Windows dijo que usaría «después de reiniciar».
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Pedido del usuario (23/11/2026): «con los de AMD (ATI, Vega…) en algunos equipos dice que instala y cuando reinicio la PC sigue
    /// como si no se hubiera actualizado». Hasta la 2.0.24 una instalación que quedaba «requiere reiniciar» no se recordaba: después de
    /// reiniciar, si Windows seguía usando el controlador de antes, la búsqueda siguiente lo volvía a ofrecer igual, y se instalaba otra
    /// vez sin ningún resultado (en vueltas, cada una con su descarga).
    ///
    /// Ahora se anota el dispositivo y la versión instalada. En la búsqueda siguiente:
    ///  - si el equipo todavía no se reinició, la fila queda «requiere reinicio» (no se vuelve a instalar);
    ///  - si ya se reinició y el dispositivo sigue con la versión de antes, Windows no la usa: la fila queda «Al día», con la explicación,
    ///    y no se vuelve a ofrecer.
    /// Vale para el Catálogo de Microsoft Update y para Windows Update (la clave es el dispositivo y la versión, no la fuente).
    /// </summary>
    public static class DriverFollowUp
    {
        public enum State { None, WaitingReboot, NotApplied }

        /// <summary>Cuánto se recuerda (lo mismo que los controladores que Windows no aplicó).</summary>
        public static readonly TimeSpan Life = TimeSpan.FromDays(30);

        /// <summary>Sólo para las pruebas: reemplaza el archivo del equipo (clave → valor).</summary>
        internal static Dictionary<string, string> StoreForTests;
        /// <summary>Sólo para las pruebas: hora (UTC) del último arranque; null = no se sabe.</summary>
        internal static Func<DateTime?> BootTimeForTests;

        const string Prefix = "reinicio|";

        static string Key(string deviceId, string version)
        {
            return Prefix + (deviceId ?? "").Trim().ToUpperInvariant() + "|" + (version ?? "").Trim();
        }

        /// <summary>Anota que se instaló <paramref name="version"/> en el dispositivo y que lo va a usar al reiniciar.</summary>
        public static void Record(string deviceId, string version)
        {
            if (string.IsNullOrEmpty(deviceId) || VersionUtil.Parse(version) == null) return;
            string value = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            if (StoreForTests != null) { lock (StoreForTests) StoreForTests[Key(deviceId, version)] = value; return; }
            try
            {
                lock (QueryCache.MachineGate)
                {
                    var q = QueryCache.OpenMachine(QueryCache.MachineStateName, Life);
                    q.Put(Key(deviceId, version), value);
                    q.Save();
                }
            }
            catch (Exception ex) { Log.Info("Controladores pendientes de reinicio: " + ex.Message); }
        }

        /// <summary>Lo anotado del equipo, leído una vez por búsqueda (null: se lee al consultar).</summary>
        public sealed class Snapshot
        {
            internal QueryCache Cache;
            public static Snapshot Open()
            {
                var s = new Snapshot();
                if (StoreForTests != null) return s;
                try { s.Cache = QueryCache.OpenMachine(QueryCache.MachineStateName, Life); } catch (Exception ex) { Log.Info("Controladores pendientes de reinicio: " + ex.Message); }
                return s;
            }
        }

        /// <summary>
        /// ¿Qué pasó con <paramref name="offered"/> en este dispositivo, que hoy informa <paramref name="versionNow"/>?
        /// <paramref name="installedAt"/>: cuándo se instaló (hora local), si se anotó.
        /// </summary>
        public static State Check(Snapshot snap, string deviceId, string offered, string versionNow, out DateTime installedAt)
        {
            installedAt = DateTime.MinValue;
            if (string.IsNullOrEmpty(deviceId) || VersionUtil.Parse(offered) == null) return State.None;
            string raw = null;
            if (StoreForTests != null) { lock (StoreForTests) StoreForTests.TryGetValue(Key(deviceId, offered), out raw); }
            else if (snap != null && snap.Cache != null) snap.Cache.TryGet(Key(deviceId, offered), out raw);
            long ticks;
            if (raw == null || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out ticks)) return State.None;
            DateTime atUtc;
            try { atUtc = new DateTime(ticks, DateTimeKind.Utc); } catch { return State.None; }
            installedAt = atUtc.ToLocalTime();
            // Ya la usa (o una más nueva): no hay nada pendiente.
            if (VersionUtil.Parse(versionNow) != null && !VersionUtil.IsNewer(offered, versionNow)) return State.None;
            return Rebooted(atUtc) ? State.NotApplied : State.WaitingReboot;
        }

        /// <summary>
        /// ¿El equipo arrancó de nuevo después de <paramref name="sinceUtc"/>? Se mira el tiempo desde el último arranque REAL: con el
        /// inicio rápido de Windows, «Apagar» y volver a prender no reinicia el núcleo (ni aplica los controladores pendientes), y así
        /// tampoco cuenta acá. Si no se sabe, se toma como reiniciado pasado un día.
        /// </summary>
        public static bool Rebooted(DateTime sinceUtc)
        {
            DateTime? boot = BootTimeUtc();
            if (!boot.HasValue) return DateTime.UtcNow - sinceUtc > TimeSpan.FromDays(1);
            return boot.Value > sinceUtc.AddMinutes(1);
        }

        [DllImport("kernel32.dll")]
        static extern ulong GetTickCount64();

        /// <summary>Hora (UTC) del último arranque del equipo, o null si no se puede saber.</summary>
        public static DateTime? BootTimeUtc()
        {
            if (BootTimeForTests != null) return BootTimeForTests();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            try { return DateTime.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64()); }
            catch (Exception ex) { Log.Quiet("Hora del último arranque", ex); return null; }
        }

        /// <summary>Cómo reiniciar para que se use (el inicio rápido de Windows no lo aplica con «Apagar»).</summary>
        public const string RebootHint = "reiniciá el equipo con Inicio › Inicio/Apagado › «Reiniciar» (con el inicio rápido de Windows, «Apagar» y volver a prender no lo aplica)";

        /// <summary>Texto para una fila que ya se instaló y espera el reinicio.</summary>
        public static string WaitingText(string offered, DateTime installedAt, string versionNow)
        {
            return "La " + offered + " ya se instaló el " + Fmt.Date(installedAt) + " y el dispositivo todavía usa la " + versionNow +
                   ": la toma al reiniciar. No hace falta instalarla de nuevo; " + RebootHint + ".";
        }

        /// <summary>Texto para una fila que se instaló, se reinició y Windows siguió con la de antes.</summary>
        public static string NotAppliedText(string offered, DateTime installedAt, string versionNow)
        {
            return "La " + offered + " se instaló el " + Fmt.Date(installedAt) + " y, después de reiniciar, el dispositivo sigue usando la " + versionNow +
                   ": Windows se queda con el controlador que tiene porque lo considera mejor para este equipo (el del fabricante del equipo o uno " +
                   "más exacto para este modelo). No hace falta hacer nada y no se vuelve a ofrecer (instalarla otra vez da el mismo resultado).";
        }
    }
}
