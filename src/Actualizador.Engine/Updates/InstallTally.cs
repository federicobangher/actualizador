// Recuento de una tanda de instalación, el mismo para la interfaz, la consola y el registro.
using System;
using System.Collections.Generic;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Cómo terminó cada elemento de una tanda. Antes el resumen decía sólo «N correctas, M con error»:
    /// «Todas las apps de la Store» sin novedades, Office todavía actualizándose en segundo plano o la
    /// página de la Store abierta no figuraban en ningún número («0 correctas, 0 con error»).
    /// </summary>
    public sealed class InstallTally
    {
        /// <summary>Instaladas (incluye las que requieren reiniciar).</summary>
        public int Ok;
        /// <summary>De las correctas, las que esperan un reinicio del equipo.</summary>
        public int Reboot;
        public int Failed;
        /// <summary>De las fallidas, las que piden reiniciar el equipo ANTES de reintentar.</summary>
        public int RestartFirst;
        /// <summary>Se ejecutaron y no había nada que cambiar (la Store sin novedades).</summary>
        public int Unchanged;
        /// <summary>Siguen en segundo plano o esperan que el usuario termine (Office, la página de la Store).</summary>
        public int Pending;
        /// <summary>Canceladas antes de empezar.</summary>
        public int NotStarted;

        public const string CancelledBeforeStart = "Cancelada antes de empezar";

        public static InstallTally Of(IEnumerable<UpdateCandidate> items)
        {
            var t = new InstallTally();
            if (items == null) return t;
            foreach (var c in items)
            {
                if (c == null) continue;
                switch (c.Status)
                {
                    case UpdateStatus.Instalada: t.Ok++; break;
                    case UpdateStatus.RequiereReinicio: t.Ok++; t.Reboot++; break;
                    case UpdateStatus.Fallida: t.Failed++; if (c.RestartBeforeRetry) t.RestartFirst++; break;
                    case UpdateStatus.AlDia: t.Unchanged++; break;
                    case UpdateStatus.Pendiente:
                        if (c.Result == CancelledBeforeStart || c.Result == "En cola") t.NotStarted++;
                        else t.Pending++;
                        break;
                    default: t.Pending++; break;      // «Instalando…» que quedó así (no debería pasar)
                }
            }
            return t;
        }

        /// <summary>«3 correctas, 1 con error, 1 sin cambios (ya estaba al día), 1 pendiente (…), 2 canceladas antes de empezar.»</summary>
        public string Text()
        {
            var parts = new List<string>();
            parts.Add(Fmt.Plural(Ok, "correcta", "correctas"));
            parts.Add(Failed + " con error");
            if (Unchanged > 0) parts.Add(Unchanged + " sin cambios (ya estaba al día)");
            if (Pending > 0) parts.Add(Fmt.Plural(Pending, "pendiente", "pendientes") + " (" + (Pending == 1 ? "termina" : "terminan") + " en segundo plano o en su ventana)");
            if (NotStarted > 0) parts.Add(Fmt.Plural(NotStarted, "cancelada antes de empezar", "canceladas antes de empezar"));
            return string.Join(", ", parts.ToArray()) + ".";
        }
    }
}
