// 2.0.27: lo que ya se intentó en ESTE equipo y no sirvió, para no repetirlo en cada pasada.
using System;
using System.Collections.Generic;
using System.Globalization;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Grabación del usuario (23/11/2026, 11:50): en cada pasada aparecían las mismas tres filas, se «instalaban» y volvían a aparecer:
    /// dos «HP Printer (BIDI)» de Windows Update («Instalada correctamente», y en la búsqueda siguiente otra vez «Sin controlador»), y
    /// «Microsoft.MicrosoftOfficeHub (para todos los usuarios)», que fallaba siempre igual. Se anota qué se intentó (con la versión) en
    /// los datos del equipo (como los controladores que Windows no aplicó), por <see cref="Life"/>.
    /// </summary>
    public static class MachineMemory
    {
        public static readonly TimeSpan Life = TimeSpan.FromDays(30);

        /// <summary>Sólo para las pruebas: reemplaza el archivo del equipo.</summary>
        internal static Dictionary<string, string> StoreForTests;

        /// <summary>Lo anotado, leído una vez por búsqueda.</summary>
        public sealed class Snapshot
        {
            internal QueryCache Cache;
            public static Snapshot Open()
            {
                var s = new Snapshot();
                if (StoreForTests != null) return s;
                try { s.Cache = QueryCache.OpenMachine(QueryCache.MachineStateName, Life); } catch (Exception ex) { Log.Info("Memoria del equipo: " + ex.Message); }
                return s;
            }
        }

        /// <summary>Anota <paramref name="text"/> con la fecha de hoy.</summary>
        public static void Put(string key, string text)
        {
            if (string.IsNullOrEmpty(key)) return;
            string value = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + (text ?? "");
            if (value.Length > 2000) value = value.Substring(0, 2000);
            if (StoreForTests != null) { lock (StoreForTests) StoreForTests[key] = value; return; }
            try
            {
                lock (QueryCache.MachineGate)
                {
                    var q = QueryCache.OpenMachine(QueryCache.MachineStateName, Life);
                    q.Put(key, value);
                    q.Save();
                }
            }
            catch (Exception ex) { Log.Info("Memoria del equipo: " + ex.Message); }
        }

        /// <summary>Lo anotado con esa clave (fecha «aaaa-mm-dd» y texto), o false.</summary>
        public static bool TryGet(Snapshot snap, string key, out string date, out string text)
        {
            date = null; text = null;
            string raw = null;
            if (StoreForTests != null) { lock (StoreForTests) StoreForTests.TryGetValue(key ?? "", out raw); }
            else if (snap != null && snap.Cache != null) snap.Cache.TryGet(key, out raw);
            if (string.IsNullOrEmpty(raw)) return false;
            int bar = raw.IndexOf('|');
            date = bar > 0 ? raw.Substring(0, bar) : raw;
            text = bar > 0 ? raw.Substring(bar + 1) : "";
            return true;
        }

        /// <summary>«2026-11-23» → «23/11/2026».</summary>
        public static string ShowDate(string isoDate)
        {
            DateTime d;
            return DateTime.TryParseExact(isoDate ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : (isoDate ?? "");
        }

        // ------------------------------------------------------------------ Windows Update: controladores sin dispositivo

        public static string WuDriverKey(string hwid, string version) { return "wu-sin-dispositivo|" + (hwid ?? "").Trim().ToUpperInvariant() + "|" + (version ?? "").Trim(); }

        // ------------------------------------------------------------------ winget: para todos los usuarios

        public static string MachineFailureKey(string id, string version) { return MachineFailureKey(id, version, false); }

        /// <summary>
        /// 2.0.30: con la vía (como servicio o «--scope machine»). En la captura del 23/11 la falla anotada era de «--scope machine» (winget
        /// como servicio no arrancaba en ese equipo); arreglado eso, la vía como servicio —la que usa el agente de Vigía— no se probaba
        /// nunca porque la falla vieja la tapaba.
        /// </summary>
        public static string MachineFailureKey(string id, string version, bool systemContext)
        {
            return (systemContext ? "servicio-fallo|" : "maquina-fallo|") + (id ?? "").Trim().ToUpperInvariant() + "|" + (version ?? "").Trim();
        }
    }
}
