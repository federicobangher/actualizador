// Los cuatro grupos de la lista de actualizaciones y sus cuentas, iguales en el título, las tarjetas y el botón.
using System;
using System.Collections.Generic;
using Actualizador.Core;

namespace Actualizador.Updates
{
    public enum UpdateGroup { ParaActualizar, Opcional, AlDia, SinVerificar }

    /// <summary>
    /// 2.0.6: la ventana muestra DOS listas en vez de cuatro (pedido del usuario): lo que hay para hacer («Para actualizar» y,
    /// debajo, las opcionales) y todo lo demás que se revisó («Al día» y lo que quedó «Sin verificar»). Con cuatro tarjetas
    /// lo opcional y lo no verificado quedaban fuera de la vista y parecía que el programa no los había detectado.
    /// </summary>
    public enum UpdateView { Pendientes, Revisados }

    /// <summary>
    /// En la 1.7.5 la pantalla decía «Hay 5 actualizaciones disponibles», el botón «Actualizar todo (3)» y la tarjeta «6 Para
    /// actualizar»: tres números para lo mismo (captura del usuario). Ahora hay una sola regla: «Para actualizar» es exactamente
    /// lo que hace «Actualizar todo», y ese número es el del título, el de la tarjeta y el del botón. Lo disponible que no
    /// conviene instalar sin mirarlo (BIOS, controladores que Windows no eligió, actualizaciones opcionales, paquetes
    /// anclados) va a «Opcionales», con su propio número.
    /// </summary>
    public static class UpdateGroups
    {
        public static UpdateGroup Of(UpdateCandidate u)
        {
            if (u == null) return UpdateGroup.SinVerificar;
            switch (u.Status)
            {
                case UpdateStatus.AlDia: return UpdateGroup.AlDia;
                case UpdateStatus.NoVerificable:
                case UpdateStatus.Error:
                case UpdateStatus.Ignorada: return UpdateGroup.SinVerificar;
                default:
                    // Disponible, pendiente, instalando, actualizada o con error al instalar: donde estaba antes de instalar.
                    return u.Recommended && u.Installable ? UpdateGroup.ParaActualizar : UpdateGroup.Opcional;
            }
        }

        /// <summary>¿Falta hacer algo con esta fila? (lo actualizado en esta sesión ya no cuenta; lo que se está instalando, sí).</summary>
        public static bool IsPending(UpdateCandidate u)
        {
            if (u == null) return false;
            if (u.Status == UpdateStatus.Instalando) return true;   // durante la instalación la tarjeta decía «0» y el árbol «al día»
            if (Of(u) == UpdateGroup.ParaActualizar) return UpdateManager.CanInstall(u);
            return u.Status == UpdateStatus.Disponible || u.Status == UpdateStatus.Pendiente || u.Status == UpdateStatus.Fallida;
        }

        /// <summary>Se marca sola al aparecer: está en «Para actualizar» y se puede instalar.</summary>
        public static bool ShouldPremark(UpdateCandidate u)
        {
            return u != null && u.IgnoredBy == null && Of(u) == UpdateGroup.ParaActualizar && UpdateManager.CanInstall(u);
        }

        public sealed class Counts
        {
            /// <summary>Lo que hace «Actualizar todo»: el número del título, de la tarjeta y del botón.</summary>
            public int ToUpdate;
            public int Optional, UpToDate, Unverified, UnverifiedPrograms;
            /// <summary>De las de «Para actualizar», cuántas sólo abren su página de descarga.</summary>
            public int Pages;
            public int Failed, Done, Reboot, Installing;
            /// <summary>Lo que se revisó, por tipo (sin la fila de «Todas las apps de Microsoft Store»).</summary>
            public int Programs, Drivers, StoreApps, WindowsUpdates;
            /// <summary>Marcadas que se pueden instalar (en cualquier grupo).</summary>
            public int Selected;
            /// <summary>Lo marcado es exactamente «Para actualizar» (o no hay nada marcado): el botón dice «Actualizar todo».</summary>
            public bool SelectionIsAll;
        }

        /// <summary>
        /// 2.0.13 (video del usuario, 07/11/2026): «Marcar todo» marca sólo lo que tiene algo para instalar. Con la lista «Al día y sin
        /// verificar» a la vista marcaba la fila «Todas las apps de Microsoft Store → Sin novedades» (que se puede marcar a mano para que
        /// la Store busque de nuevo): el botón decía «Actualizar (1)» con «Disponibles (0)» y no se veía qué se iba a actualizar.
        /// </summary>
        public static bool MarkedByMarkAll(UpdateCandidate u)
        {
            return u != null && UpdateManager.CanInstall(u) && u.Status != UpdateStatus.AlDia && !u.ManualOnly;
        }

        /// <summary>
        /// 2.0.13: ¿hay que volver a armar la lista? Una fila que cambia de estado puede pasar de una lista a la otra (la de la Store,
        /// de «Al día» a «Instalando» en «Disponibles»): antes sólo se repintaba la fila, y «Disponibles (1)» quedaba vacía.
        /// </summary>
        public static bool ListChanged(UpdateCandidate u, UpdateView shownView, bool shownNow, bool kindVisible, bool textFilterOn)
        {
            if (u == null) return false;
            bool belongs = ViewOf(u) == shownView && kindVisible;
            if (shownNow) return !belongs;
            return belongs && !textFilterOn;      // con un texto de filtro puede no coincidir: no se rearma en cada cambio
        }

        public static Counts Count(IEnumerable<UpdateCandidate> updates)
        {
            var k = new Counts();
            int selectedInGroup = 0, selectedOutside = 0;
            if (updates != null)
                foreach (var u in updates)
                {
                    if (u == null) continue;
                    var g = Of(u);
                    bool pending = IsPending(u);
                    switch (g)
                    {
                        case UpdateGroup.ParaActualizar:
                            if (pending) { k.ToUpdate++; if (u.ProviderName == SiteProvider.ProviderId) k.Pages++; }
                            break;
                        case UpdateGroup.Opcional: if (pending) k.Optional++; break;
                        case UpdateGroup.AlDia: k.UpToDate++; break;
                        default:
                            k.Unverified++;
                            if (u.Kind == UpdateKind.Programa && u.Status != UpdateStatus.Ignorada) k.UnverifiedPrograms++;
                            break;
                    }
                    switch (u.Kind)
                    {
                        case UpdateKind.Programa: k.Programs++; break;
                        case UpdateKind.Controlador: k.Drivers++; break;
                        case UpdateKind.Store: if (u.Identifier != StoreProvider.AllAppsId) k.StoreApps++; break;
                        default: k.WindowsUpdates++; break;
                    }
                    if (u.Status == UpdateStatus.Fallida) k.Failed++;
                    else if (u.Status == UpdateStatus.Instalada) k.Done++;
                    else if (u.Status == UpdateStatus.RequiereReinicio) { k.Done++; k.Reboot++; }
                    else if (u.Status == UpdateStatus.Instalando) k.Installing++;
                    if (u.Selected && UpdateManager.CanInstall(u))
                    {
                        k.Selected++;
                        if (g == UpdateGroup.ParaActualizar && pending) selectedInGroup++; else selectedOutside++;
                    }
                }
            k.SelectionIsAll = k.Selected == 0 || (selectedOutside == 0 && selectedInGroup == k.ToUpdate);
            return k;
        }

        /// <summary>Texto del botón principal: «Actualizar todo (N)» o, si se cambió la selección, «Actualizar marcadas (M)».</summary>
        public static string ButtonText(Counts k)
        {
            if (k.SelectionIsAll) return k.ToUpdate > 0 ? "Actualizar todo (" + k.ToUpdate + ")" : "Actualizar todo";
            return "Actualizar marcadas (" + k.Selected + ")";
        }

        /// <summary>Título del encabezado, con el mismo número que la tarjeta y el botón.</summary>
        public static string Title(Counts k)
        {
            if (k.ToUpdate > 0) return k.ToUpdate == 1 ? "Hay 1 para actualizar" : "Hay " + k.ToUpdate + " para actualizar";
            if (k.UnverifiedPrograms > 0) return "Nada para actualizar entre lo que se pudo comparar";
            if (k.Optional > 0) return "Lo recomendado está al día ✓";
            return "Todo está al día ✓";
        }

        /// <summary>La línea de abajo: cada número es el de una tarjeta.</summary>
        public static string Subtitle(Counts k)
        {
            var parts = new List<string>();
            if (k.ToUpdate > 0)
                parts.Add("«Actualizar todo» " + (k.ToUpdate == 1 ? "la instala" : "instala las " + k.ToUpdate) +
                          (k.Pages > 0 ? " (" + (k.Pages == 1 ? "1 abre su página de descarga" : k.Pages + " abren su página de descarga") + ")" : "") +
                          (k.Failed > 0 ? "; " + Fmt.Plural(k.Failed, "falló antes y se reintenta", "fallaron antes y se reintentan") : ""));
            if (k.Optional > 0) parts.Add(Fmt.Plural(k.Optional, "opcional para elegir vos", "opcionales para elegir vos"));
            if (k.Unverified > 0) parts.Add(Fmt.Plural(k.Unverified, "sin verificar", "sin verificar"));
            return parts.Count == 0 ? "" : string.Join(" · ", parts.ToArray()) + ".";
        }

        // ------------------------------------------------------------------ Las dos listas de la ventana (2.0.6)

        /// <summary>En qué lista de la ventana va la fila: «Para actualizar» y «Opcionales» juntas; «Al día» y «Sin verificar» juntas.</summary>
        public static UpdateView ViewOf(UpdateCandidate u)
        {
            var g = Of(u);
            return g == UpdateGroup.ParaActualizar || g == UpdateGroup.Opcional ? UpdateView.Pendientes : UpdateView.Revisados;
        }

        /// <summary>
        /// La lista que se abre sola: la de pendientes si hay algo para hacer (o algo que se instaló o falló en esta sesión);
        /// si no, la de todo lo revisado.
        /// </summary>
        public static UpdateView DefaultView(Counts k)
        {
            if (k == null) return UpdateView.Pendientes;
            return k.ToUpdate > 0 || k.Optional > 0 || k.Failed > 0 || k.Done > 0 || k.Installing > 0 ? UpdateView.Pendientes : UpdateView.Revisados;
        }

        /// <summary>
        /// El número grande de cada tarjeta. El de la primera es el de «Actualizar todo» (el mismo del título y del botón);
        /// las opcionales van en la etiqueta, con su propio número.
        /// </summary>
        public static string CardNumber(UpdateView v, Counts k)
        {
            if (k == null) return "—";
            return (v == UpdateView.Pendientes ? k.ToUpdate : k.UpToDate).ToString();
        }

        /// <summary>La etiqueta de cada tarjeta: «Para actualizar · 3 opcionales» / «Al día · 40 sin verificar» (sin cuentas: los nombres).</summary>
        public static string CardLabel(UpdateView v, Counts k)
        {
            if (v == UpdateView.Pendientes)
                return k == null ? "Para actualizar · Opcionales" : "Para actualizar · " + Fmt.Plural(k.Optional, "opcional", "opcionales");
            return k == null ? "Al día · Sin verificar" : "Al día · " + Fmt.Plural(k.Unverified, "sin verificar", "sin verificar");
        }

        /// <summary>Qué tiene cada lista (debajo de las tarjetas).</summary>
        public static string Explain(UpdateView v, Counts k)
        {
            if (v == UpdateView.Pendientes)
                return "Arriba lo que instala «Actualizar todo», ya marcado; debajo las opcionales (BIOS, controladores que Windows no eligió, " +
                       "paquetes anclados o sin editor confirmado), que se instalan sólo si las marcás.";
            string seen = k == null ? "" : " " + Reviewed(k);
            return "Todo lo demás que se revisó." + seen + " Lo «No verificable» no figura en ningún catálogo con el cual comparar: " +
                   "clic derecho › «Indicar la página oficial con la versión…».";
        }

        /// <summary>«Se revisaron 182 programas, 140 controladores y 35 apps de la Store» ("" si no hay nada).</summary>
        public static string Reviewed(Counts k)
        {
            if (k == null) return "";
            var parts = new List<string>();
            if (k.Programs > 0) parts.Add(Fmt.Plural(k.Programs, "programa", "programas"));
            if (k.Drivers > 0) parts.Add(Fmt.Plural(k.Drivers, "controlador", "controladores"));
            if (k.StoreApps > 0) parts.Add(Fmt.Plural(k.StoreApps, "app de la Store", "apps de la Store"));
            if (k.WindowsUpdates > 0) parts.Add(Fmt.Plural(k.WindowsUpdates, "actualización de Windows", "actualizaciones de Windows"));
            if (parts.Count == 0) return "";
            string list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.GetRange(0, parts.Count - 1).ToArray()) + " y " + parts[parts.Count - 1];
            int total = k.Programs + k.Drivers + k.StoreApps + k.WindowsUpdates;
            return (total == 1 ? "Se revisó " : "Se revisaron ") + list + ".";
        }

        /// <summary>
        /// Orden dentro de cada lista. Pendientes: primero lo de «Actualizar todo», después las opcionales (cada parte como venía).
        /// Revisados: primero lo que quedó sin verificar (es lo que hay que mirar), después lo que está al día.
        /// </summary>
        public static int ViewRank(UpdateCandidate u)
        {
            switch (Of(u))
            {
                case UpdateGroup.ParaActualizar: return 0;
                case UpdateGroup.Opcional: return 1;
                case UpdateGroup.SinVerificar: return 2;
                default: return 3;
            }
        }

        /// <summary>La lista de una vista, ordenada (orden estable: dentro de cada parte, el de la búsqueda).</summary>
        public static List<UpdateCandidate> InView(IEnumerable<UpdateCandidate> updates, UpdateView v)
        {
            var list = new List<UpdateCandidate>();
            if (updates == null) return list;
            foreach (var u in updates) if (u != null && ViewOf(u) == v) list.Add(u);
            var index = new Dictionary<UpdateCandidate, int>();
            for (int i = 0; i < list.Count; i++) index[list[i]] = i;
            list.Sort(delegate(UpdateCandidate a, UpdateCandidate b)
            {
                int c = ViewRank(a).CompareTo(ViewRank(b));
                return c != 0 ? c : index[a].CompareTo(index[b]);
            });
            return list;
        }

        /// <summary>
        /// Texto de la columna «Estado» en la lista unificada: las opcionales lo dicen («Disponible (opcional)»), porque ahora
        /// comparten lista con lo que instala «Actualizar todo».
        /// </summary>
        public static string StatusText(UpdateCandidate u)
        {
            if (u == null) return "";
            string s = UpdateCandidate.StatusText(u.Status);
            if (u.Status == UpdateStatus.Disponible) return Of(u) == UpdateGroup.Opcional ? "Disponible (opcional)" : "Disponible";
            return s;
        }

        /// <summary>Qué es cada grupo (debajo de las tarjetas, al elegir uno).</summary>
        public static string Explain(UpdateGroup g)
        {
            switch (g)
            {
                case UpdateGroup.ParaActualizar: return "Lo que instala «Actualizar todo», ya marcado. Desmarcá lo que no quieras.";
                case UpdateGroup.Opcional: return "No se instalan solas: controladores que Windows Update no eligió para este equipo, BIOS, actualizaciones opcionales de Windows, " +
                                                  "paquetes anclados o sin versión segura. Marcá las que quieras y tocá «Actualizar marcadas».";
                case UpdateGroup.AlDia: return "Coinciden con la última versión publicada.";
                default: return "No figuran en ningún catálogo con el cual comparar, o no se pudieron consultar. Clic derecho › «Indicar la página oficial con la versión…» para enseñarle a Actualizador dónde mirar.";
            }
        }
    }
}
