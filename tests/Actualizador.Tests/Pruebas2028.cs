// Pruebas de la 2.0.28. Relevamiento pedido por el usuario (23/11/2026) después de 4 fallas en un día: «se instala y vuelve a aparecer».
using System;
using System.Collections.Generic;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static HistoryEntry Historia2028(string kind, string name, string offered, UpdateStatus st, DateTime when, string result)
        {
            return new HistoryEntry { Kind = kind, Name = name, Offered = offered, StatusCode = st.ToString(), Status = UpdateCandidate.StatusText(st), When = when, Result = result ?? "" };
        }

        static UpdateCandidate Fila2028(UpdateKind k, string name, string offered)
        {
            return new UpdateCandidate { Kind = k, Name = name, AvailableVersion = offered, Status = UpdateStatus.Disponible, Installable = true, Recommended = true, Detail = "Detalle original." };
        }

        /// <summary>Lo que ya se instaló con la misma versión y vuelve a aparecer no se repite solo en cada pasada (para cualquier fuente).</summary>
        static void TestRedDeSeguridadHistorial2028()
        {
            var ahora = new DateTime(2026, 11, 23, 12, 0, 0);
            bool reinicio = false;
            Func<DateTime, bool> reinicioDesde = delegate(DateTime d) { return reinicio; };
            var historial = new List<HistoryEntry>
            {
                Historia2028("Controladores", "HP Printer (BIDI)", "1.0.0.237 (08/04/2019)", UpdateStatus.Instalada, ahora.AddMinutes(-30), "Instalada correctamente"),
                Historia2028("Controladores", "HP Printer (BIDI)", "1.0.0.237 (08/04/2019)", UpdateStatus.Instalada, ahora.AddMinutes(-10), "Instalada correctamente"),
                Historia2028("Programas", "Microsoft.MicrosoftOfficeHub (para todos los usuarios)", "19.2609.33020.0", UpdateStatus.Fallida, ahora.AddMinutes(-30), "No se pudo actualizar: A"),
                Historia2028("Programas", "Microsoft.MicrosoftOfficeHub (para todos los usuarios)", "19.2609.33020.0", UpdateStatus.Fallida, ahora.AddMinutes(-10), "No se pudo actualizar: B"),
                Historia2028("Windows Update", "2026-11 Actualización acumulativa", "KB5070000", UpdateStatus.RequiereReinicio, ahora.AddMinutes(-20), "Instalada; requiere reiniciar"),
                Historia2028("Programas", "7-Zip", "26.00", UpdateStatus.Fallida, ahora.AddMinutes(-20), "se cortó la descarga"),
                Historia2028("Programas", "VLC", "3.0.22", UpdateStatus.Fallida, ahora.AddMinutes(-40), "se cortó la descarga"),
                Historia2028("Programas", "VLC", "3.0.22", UpdateStatus.Instalada, ahora.AddMinutes(-20), "Actualizada correctamente"),
                Historia2028("Programas", "Notepad++", "8.8.1", UpdateStatus.Instalada, ahora.AddDays(-40), "Actualizada correctamente"),
            };
            var bidi = Fila2028(UpdateKind.Controlador, "HP Printer (BIDI)", "1.0.0.237 (08/04/2019)");
            var hub = Fila2028(UpdateKind.Programa, "Microsoft.MicrosoftOfficeHub (para todos los usuarios)", "19.2609.33020.0");
            var acumulativa = Fila2028(UpdateKind.WindowsUpdate, "2026-11 Actualización acumulativa", "KB5070000");
            var zip = Fila2028(UpdateKind.Programa, "7-Zip", "26.00");
            var vlc = Fila2028(UpdateKind.Programa, "VLC", "3.0.22");
            var npp = Fila2028(UpdateKind.Programa, "Notepad++", "8.8.1");
            var nueva = Fila2028(UpdateKind.Controlador, "HP Printer (BIDI)", "1.0.0.240 (01/01/2024)");
            var otroTipo = Fila2028(UpdateKind.Programa, "HP Printer (BIDI)", "1.0.0.237 (08/04/2019)");
            var lista = new List<UpdateCandidate> { bidi, hub, acumulativa, zip, vlc, npp, nueva, otroTipo };
            var r = RepeatGuard.Apply(lista, historial, ahora, reinicioDesde);

            Check(bidi.Status == UpdateStatus.Disponible && bidi.Installable && !bidi.Recommended && bidi.Detail.StartsWith("Ya se instaló 2 veces") && bidi.Detail.Contains("Detalle original."),
                  "instalada dos veces y otra vez ofrecida: sigue en la lista, sin marcar, con el motivo: " + bidi.Detail);
            Check(!hub.Recommended && hub.Detail.StartsWith("Falló 2 veces") && hub.Detail.Contains("B") , "falló dos veces igual: sin marcar, con el último error: " + hub.Detail);
            Check(acumulativa.Status == UpdateStatus.RequiereReinicio && !acumulativa.Installable && acumulativa.Detail.Contains("falta reiniciar"), "instalada, falta reiniciar: no se reinstala");
            Check(zip.Recommended && zip.Detail.StartsWith("La vez anterior") && zip.Detail.Contains("se cortó la descarga"), "falló una vez: se reintenta, y lo dice");
            Check(!vlc.Recommended && vlc.Detail.StartsWith("Ya se instaló el"), "lo último manda: falló y después se instaló bien, y sigue apareciendo");
            Check(npp.Recommended && npp.Detail == "Detalle original.", "lo de hace más de 30 días no cuenta");
            Check(nueva.Recommended && nueva.Detail == "Detalle original.", "otra versión: es otra actualización");
            Check(otroTipo.Recommended && otroTipo.Detail == "Detalle original.", "mismo nombre, otro tipo: no se mezcla");
            Eq(2, r.Repeated, "dos repetidas (BIDI y VLC)");
            Eq(1, r.Failing, "una que falla siempre");
            Eq(1, r.Waiting, "una espera el reinicio");
            Eq(1, r.FailedOnce, "una falló una vez");

            // Después de reiniciar, la acumulativa que sigue apareciendo es una que no quedó.
            reinicio = true;
            var otra = Fila2028(UpdateKind.WindowsUpdate, "2026-11 Actualización acumulativa", "KB5070000");
            RepeatGuard.Apply(new List<UpdateCandidate> { otra }, historial, ahora, reinicioDesde);
            Check(otra.Status == UpdateStatus.Disponible && !otra.Recommended && otra.Detail.Contains("Historial"), "reiniciado y sigue: sin marcar, con dónde mirar: " + otra.Detail);

            // Una fila que ya lo tiene aplicado (vueltas de «todo de una vez») no se marca dos veces.
            string antes = bidi.Detail;
            RepeatGuard.Apply(new List<UpdateCandidate> { bidi }, historial, ahora, reinicioDesde);
            Eq(antes, bidi.Detail, "no se repite el aviso");

            Check(RepeatGuard.SameOffer("1.0.0.237 (08/04/2019)", "1.0.0.237 (2019-04-08)"), "la misma versión con la fecha escrita distinto");
            Check(!RepeatGuard.SameOffer("", "") && !RepeatGuard.SameOffer("1.0.0.237", "1.0.0.238"), "vacías o distintas: no");
        }

        /// <summary>Nada termina la tanda sin resultado («En cola» o «Instalando» para siempre).</summary>
        static void TestNadaSinResultado2028()
        {
            var enCola = new UpdateCandidate { Name = "a", Status = UpdateStatus.Pendiente, Result = "En cola" };
            var colgada = new UpdateCandidate { Name = "b", Status = UpdateStatus.Instalando, Result = "Instalando…" };
            var bien = new UpdateCandidate { Name = "c", Status = UpdateStatus.Instalada, Result = "ok" };
            var sigueSola = new UpdateCandidate { Name = "d", Status = UpdateStatus.Pendiente, Result = "La instalación sigue después de 2 h: Windows Update la termina en segundo plano" };
            int avisos = 0;
            Eq(2, UpdateManager.MarkUntouched(new List<UpdateCandidate> { enCola, colgada, bien, sigueSola }, false, delegate(UpdateCandidate c) { avisos++; }), "dos sin resultado");
            Check(enCola.Status == UpdateStatus.Fallida && enCola.Result.Contains("No se llegó a instalar"), "la que quedó en cola: falló, y por qué");
            Check(colgada.Status == UpdateStatus.Fallida && colgada.Result.Contains("sin informar"), "la que quedó «Instalando»: falló, y por qué");
            Check(bien.Status == UpdateStatus.Instalada && sigueSola.Status == UpdateStatus.Pendiente, "las demás, como estaban");
            Eq(2, avisos, "la interfaz se entera de las dos");
            var cancelada = new UpdateCandidate { Name = "e", Status = UpdateStatus.Pendiente, Result = "En cola" };
            UpdateManager.MarkUntouched(new List<UpdateCandidate> { cancelada }, true, delegate(UpdateCandidate c) { });
            Check(cancelada.Status == UpdateStatus.Pendiente && cancelada.Result == InstallTally.CancelledBeforeStart, "si se canceló: cancelada antes de empezar");
        }

        static void TestVersion2028()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.28") >= 0, "Actualizador 2.0.28 o posterior: " + AppInfo.Version);
        }
    }
}
