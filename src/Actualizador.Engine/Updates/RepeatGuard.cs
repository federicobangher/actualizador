// 2.0.28: red de seguridad general contra «se instala y vuelve a aparecer».
using System;
using System.Collections.Generic;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Relevamiento pedido por el usuario (23/11/2026, después de 4 fallas en un día): las cuatro tenían la misma forma. Algo se «instalaba
    /// correctamente» (o fallaba siempre igual) y en la búsqueda siguiente volvía a aparecer igual, marcado: en cada pasada se repetía, sin
    /// resultado (AMD Vega, HP Printer BIDI, Office Hub para todos los usuarios…). Cada caso tiene ahora su arreglo, pero un caso nuevo
    /// (otro fabricante, otro programa) volvería a hacer lo mismo.
    ///
    /// Esto lo ataja para TODAS las fuentes, con el historial de lo instalado en este equipo (historial.jsonl): si lo que se ofrece (mismo
    /// tipo, mismo nombre, misma versión) ya se instaló bien en los últimos <see cref="Window"/>,
    ///  - y quedó «requiere reiniciar» y el equipo todavía no se reinició: la fila queda «requiere reinicio» (no se reinstala);
    ///  - si no, la instalación no quedó: la fila sigue, pero no se marca sola y dice cuándo se instaló y qué suele pasar.
    /// Si ya FALLÓ dos veces con esa versión, tampoco se marca sola y muestra el último error; una sola falla se informa y se reintenta.
    /// </summary>
    public static class RepeatGuard
    {
        /// <summary>Hasta cuándo se mira el historial.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromDays(30);
        /// <summary>Fallas con la misma versión a partir de las cuales no se marca sola.</summary>
        public const int FailuresToUnmark = 2;

        /// <summary>Qué hizo con la lista (para el registro y las pruebas).</summary>
        public sealed class Outcome { public int Waiting, Repeated, Failing, FailedOnce; }

        /// <summary>¿Es la misma versión ofrecida? (texto igual, o el mismo número: «1.0.0.237 (08/04/2019)» = «1.0.0.237 (2019-04-08)»)</summary>
        public static bool SameOffer(string a, string b)
        {
            a = (a ?? "").Trim(); b = (b ?? "").Trim();
            if (a.Length == 0 || b.Length == 0) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            string va = LeadingVersion(a), vb = LeadingVersion(b);
            return va.Length > 0 && vb.Length > 0 && VersionUtil.Parse(va) != null && VersionUtil.Compare(va, vb) == 0 && va.IndexOf('.') > 0;
        }

        static string LeadingVersion(string s)
        {
            int i = 0;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            return s.Substring(0, i).TrimEnd('.');
        }

        /// <summary>Aplica el historial a la lista. <paramref name="rebootedSince"/>: si el equipo se reinició después de esa hora (UTC).</summary>
        public static Outcome Apply(List<UpdateCandidate> all, List<HistoryEntry> history, DateTime nowLocal, Func<DateTime, bool> rebootedSince)
        {
            var o = new Outcome();
            if (all == null || history == null || history.Count == 0) return o;
            // Por nombre: así cada fila mira sólo lo suyo (el historial puede tener miles de entradas).
            var byName = new Dictionary<string, List<HistoryEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in history)
            {
                if (e == null || string.IsNullOrEmpty(e.Name) || nowLocal - e.When > Window || e.When > nowLocal.AddMinutes(5)) continue;
                List<HistoryEntry> l;
                if (!byName.TryGetValue(e.Name.Trim(), out l)) { l = new List<HistoryEntry>(); byName[e.Name.Trim()] = l; }
                l.Add(e);
            }
            foreach (var c in all)
            {
                if (c == null || c.Status != UpdateStatus.Disponible || !c.Installable || string.IsNullOrEmpty(c.Name)) continue;
                // Una fila que viene de la búsqueda anterior (vueltas de «todo de una vez») ya lo tiene aplicado.
                string d0 = c.Detail ?? "";
                if (d0.StartsWith("Ya se instaló ", StringComparison.Ordinal) || d0.StartsWith("Falló ", StringComparison.Ordinal) || d0.StartsWith("La vez anterior (", StringComparison.Ordinal)) continue;
                List<HistoryEntry> l;
                if (!byName.TryGetValue(c.Name.Trim(), out l)) continue;
                string kind = UpdateCandidate.KindText(c.Kind);
                HistoryEntry lastOk = null, lastFail = null;
                int ok = 0, failed = 0;
                foreach (var e in l)
                {
                    if (e.Kind != kind || !SameOffer(e.Offered, c.AvailableVersion)) continue;
                    if (e.Succeeded) { ok++; if (lastOk == null || e.When >= lastOk.When) lastOk = e; }
                    else if (e.Failed) { failed++; if (lastFail == null || e.When >= lastFail.When) lastFail = e; }
                }
                // Lo más reciente manda: si después de fallar se instaló bien, cuenta lo bien instalado (y al revés).
                if (lastOk != null && (lastFail == null || lastOk.When >= lastFail.When))
                {
                    bool waiting = lastOk.StatusCode == UpdateStatus.RequiereReinicio.ToString() && rebootedSince != null && !rebootedSince(lastOk.When.ToUniversalTime());
                    if (waiting)
                    {
                        c.Status = UpdateStatus.RequiereReinicio;
                        c.Installable = false;
                        c.Recommended = false;
                        c.Detail = "Ya se instaló el " + Fmt.Date(lastOk.When) + " y falta reiniciar el equipo para que se aplique: no hace falta instalarla de nuevo; " +
                                   DriverFollowUp.RebootHint + ". " + (c.Detail ?? "");
                        o.Waiting++;
                    }
                    else
                    {
                        c.Recommended = false;
                        c.ManualOnly = true;
                        c.Detail = "Ya se instaló " + (ok == 1 ? "el " + Fmt.Date(lastOk.When) : ok + " veces (la última, el " + Fmt.Date(lastOk.When) + ")") +
                                   " y vuelve a aparecer igual: la instalación no queda. " + Hint(c.Kind) + " No se marca sola ni con «Marcar todo», para no repetirla en cada pasada; " +
                                   "si querés reintentar, marcala a mano. " + (c.Detail ?? "");
                        o.Repeated++;
                    }
                    continue;
                }
                if (lastFail == null) continue;
                string why = Fmt.Truncate((lastFail.Result ?? "").Trim(), 220);
                if (failed >= FailuresToUnmark)
                {
                    c.Recommended = false;
                    c.ManualOnly = true;
                    c.Detail = "Falló " + failed + " veces con esta versión (la última, el " + Fmt.Date(lastFail.When) + (why.Length > 0 ? ": " + why : "") +
                               "). No se marca sola ni con «Marcar todo», para no repetir el mismo error en cada pasada; si querés reintentar, marcala a mano. " + (c.Detail ?? "");
                    o.Failing++;
                }
                else
                {
                    c.Detail = "La vez anterior (el " + Fmt.Date(lastFail.When) + ") falló" + (why.Length > 0 ? ": " + why : "") + ". Se vuelve a intentar. " + (c.Detail ?? "");
                    o.FailedOnce++;
                }
            }
            return o;
        }

        static string Hint(UpdateKind k)
        {
            switch (k)
            {
                case UpdateKind.Controlador:
                    return "Suele ser que Windows siga usando otro controlador (el del fabricante del equipo, o vuelve a ponerlo Windows Update) o que el " +
                           "dispositivo no esté conectado.";
                case UpdateKind.WindowsUpdate:
                    return "Windows no la registró como instalada (a veces la revierte al reiniciar): si se repite, Configuración › Windows Update › " +
                           "Historial muestra el error, y el solucionador de problemas de Windows Update suele arreglarlo.";
                default:
                    return "El programa sigue informando la versión anterior: puede tener otra copia instalada (otra cuenta, otra arquitectura) o " +
                           "terminar de actualizarse recién al abrirlo.";
            }
        }
    }
}
