// Actualizaciones que sólo se pueden bajar de la página oficial del programa.
using System;
using System.Collections.Generic;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Filas «Disponible» que salieron de la página oficial de un programa y no tienen un instalador que se pueda verificar
    /// (<see cref="SiteVersions"/>). Antes no se podían marcar y parecía un error. Ahora sí: «instalar» es abrir esa página
    /// en el navegador del usuario (sin privilegios de administrador), para bajar e instalar la versión nueva desde ahí.
    /// La fila queda «Pendiente» hasta la próxima búsqueda, que confirma la versión instalada.
    /// La búsqueda la hace <see cref="SiteVersions.Resolve"/> después de los demás proveedores: este no busca nada.
    /// </summary>
    public sealed class SiteProvider : IUpdateProvider
    {
        public const string ProviderId = "sitio";
        public string Name { get { return ProviderId; } }

        /// <summary>Sólo para las pruebas: en vez de abrir el navegador.</summary>
        internal static Action<string> OpenForTests;

        public string Unavailable(UpdateContext ctx) { return null; }

        public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes) { }

        public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed)
        {
            foreach (var c in items)
            {
                if (ctx.CancelRequested) { c.Status = UpdateStatus.Pendiente; c.Result = "Cancelada antes de empezar"; changed(c); continue; }
                string url = Fmt.WebUrl(c.InfoUrl);
                Uri u;
                if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out u) || !SiteVersions.IsPublicPage(u))
                {
                    InstallerSupport.Fail(c, "No se conoce su página de descarga");
                    changed(c);
                    continue;
                }
                try
                {
                    if (OpenForTests != null) OpenForTests(url); else ProcessRunner.OpenForUser(url);
                    c.Status = UpdateStatus.Pendiente;
                    // 2.0.18: si se intentó bajar el instalador y no se pudo, la fila dice por qué (antes sólo «Se abrió su página»).
                    var plan = c.ProviderTag as SitePlan;
                    string why = plan != null ? (plan.Why ?? "").Trim().TrimEnd('.') : "";
                    c.Result = (why.Length > 0 ? "No se pudo bajar el instalador solo (" + Fmt.Truncate(why, 220) + "). " : "") +
                               "Se abrió su página (" + u.Host + "): descargá e instalá la " + c.AvailableVersion + " desde ahí; después tocá «Buscar actualizaciones» para confirmarla";
                }
                catch (Exception ex) { InstallerSupport.Fail(c, "No se pudo abrir " + u.Host + ": " + ex.Message); }
                changed(c);
            }
        }
    }
}
