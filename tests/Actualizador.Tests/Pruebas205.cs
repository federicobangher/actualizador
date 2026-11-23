// Pruebas de la 2.0.5: Cura comparado siempre con su fabricante, betas a pedido y errores de COM que .NET convierte.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static void TestErroresCom205()
        {
            // La captura: «Revisión programada: No se pudo consultar el Programador de tareas: El sistema no puede encontrar el archivo
            // especificado. (0x80070002)». .NET convierte ese HRESULT en FileNotFoundException, no en COMException.
            Eq(unchecked((int)0x80070002), Com.HResultOf(new FileNotFoundException("x")), "FileNotFoundException → 0x80070002");
            Eq(unchecked((int)0x80070003), Com.HResultOf(new DirectoryNotFoundException("x")), "DirectoryNotFoundException → 0x80070003");
            Eq(unchecked((int)0x80070005), Com.HResultOf(new UnauthorizedAccessException("x")), "UnauthorizedAccessException → 0x80070005");
            Eq(unchecked((int)0x80070422), Com.HResultOf(new TargetInvocationException(new COMException("x", unchecked((int)0x80070422)))), "COMException envuelta por la llamada tardía");
            Eq(0, Com.HResultOf(new InvalidOperationException("x")), "un error que no es de Windows: 0");
            Eq(0, Com.HResultOf(null), "sin error: 0");
            Check(ScheduledReview.NotFound(new TargetInvocationException(new FileNotFoundException("x"))), "la tarea que no existe no es un error");
            Check(!ScheduledReview.NotFound(new UnauthorizedAccessException("x")), "acceso denegado sí");
            Check(ScheduledReview.Describe(new UnauthorizedAccessException("x")).StartsWith("Windows no permite crear la tarea"), "y se explica");
            Eq("Desactivada.", new ReviewTaskState().Describe(), "sin tarea: «Desactivada.»");
        }

        static void TestCuraFabricante205()
        {
            var check = VendorCatalog.Find("ultimaker-cura");
            Check(check != null && check.Feed == VendorFeed.GitHubRelease && check.Install == VendorInstall.None && check.SideBySide, "Cura en el catálogo de fabricantes (GitHub, sólo compara)");
            foreach (string n in new[] { "UltiMaker Cura 5.13.0", "Ultimaker Cura 4.13.1", "UltiMaker Cura", "UltiMaker Cura 5.14.0-beta.1" })
                Check(check.Applies(Vc(n, "5.0", "x64")), "reconoce «" + n + "»");
            Check(!check.Applies(Vc("UltiMaker Cura Connect Plugin", "1.0", "x64")), "no otro producto");
            Eq("Ultimaker/Cura", ReleaseCrossCheck.RepoOfVendorRow(new UpdateCandidate
            {
                Kind = UpdateKind.Programa, Status = UpdateStatus.AlDia, ProviderName = VendorProvider.ProviderId, InstalledVersion = "5.13.0", AvailableVersion = "5.13.0",
                ProviderTag = new VendorPlan { Check = check }
            }), "el proyecto sale de la dirección del catálogo");

            string latest = "{\"tag_name\":\"5.13.0\",\"prerelease\":false,\"draft\":false,\"html_url\":\"https://github.com/Ultimaker/Cura/releases/tag/5.13.0\",\"assets\":[]}";
            var old = VendorProvider.FetchForTests;
            try
            {
                VendorProvider.FetchForTests = delegate(string u) { return u == check.Url ? latest : null; };
                // Cura 5.12.1 aunque winget no la reconozca: se compara con UltiMaker.
                var vieja = Cura204("5.12.1");
                var outV = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, vieja), outV, new List<string>());
                var row = outV.Find(delegate(UpdateCandidate c) { return c.Name == vieja.Name; });
                Check(row != null && row.Status == UpdateStatus.Disponible && row.AvailableVersion == "5.13.0", "Cura 5.12.1: disponible la 5.13.0 (" + (row != null ? row.Status + " " + row.Detail : "sin fila") + ")");
                if (row != null)
                {
                    Check(!row.Installable && row.Detail.Contains("desinstalá la vieja"), "no se instala encima: dice cómo");
                    var todas = new List<UpdateCandidate> { row };
                    Eq(1, UpdateManager.MakeManualActionable(todas), "se puede marcar: abre su página");
                    Check(row.InfoUrl.StartsWith("https://ultimaker.com/"), "la de UltiMaker");
                }
                // La 5.12.1 que quedó al lado de la 5.13.0.
                var nueva = Cura204("5.13.0");
                var outV2 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, vieja, nueva), outV2, new List<string>());
                var r12 = outV2.Find(delegate(UpdateCandidate c) { return c.Name == vieja.Name; });
                var r13 = outV2.Find(delegate(UpdateCandidate c) { return c.Name == nueva.Name; });
                Check(r12 != null && r12.Status == UpdateStatus.AlDia && r12.Detail.Contains("ya está instalada"), "la copia vieja: al día, «podés desinstalarla»");
                Check(r13 != null && r13.Status == UpdateStatus.AlDia, "la 5.13.0: al día");
            }
            finally { VendorProvider.FetchForTests = old; }

            // --- Betas a pedido: con la opción, la beta de Cura queda entre las opcionales y abre su página.
            var rels = GitHubRules.ParseReleaseList(File.ReadAllText(Path.Combine(samples, "github_releases_cura.json")));
            var cura = Cura204("5.13.0");
            var ctx = ContextoSimulado(true, cura);
            var sinOpcion = AlDiaWinget204(cura.Name, "Ultimaker.Cura", "5.13.0");
            ReleaseCrossCheck.Apply(ctx, sinOpcion, cura, "Ultimaker/Cura", rels);
            Check(sinOpcion.Status == UpdateStatus.AlDia && sinOpcion.Detail.Contains("Ofrecer también las versiones beta"), "sin la opción: al día y dice dónde activarla");
            ctx.Options.IncludePreReleases = true;
            var fila = AlDiaWinget204(cura.Name, "Ultimaker.Cura", "5.13.0");
            Eq(ReleaseCrossCheck.Outcome.PreReleaseOffered, ReleaseCrossCheck.Apply(ctx, fila, cura, "Ultimaker/Cura", rels), "con la opción: se ofrece");
            Eq(UpdateStatus.Disponible, fila.Status, "disponible");
            Eq("5.14.0-beta.2-RC1", fila.AvailableVersion, "la beta más nueva");
            Check(fila.Installable && !fila.Recommended && fila.ProviderName == SiteProvider.ProviderId, "opcional y abre su página");
            Eq(UpdateGroup.Opcional, UpdateGroups.Of(fila), "en «Opcionales», nunca en «Actualizar todo»");
            Check(!UpdateGroups.ShouldPremark(fila), "no se marca sola");
            Check(fila.Detail.Contains("BETA") && fila.InfoUrl.Contains("5.14.0-beta.2-RC1"), "lo dice y apunta a esa versión");

            // --- La fila del fabricante (Cura) también se contrasta, sin pedir el manifiesto de winget.
            var pedidas = new List<string>();
            string json = File.ReadAllText(Path.Combine(samples, "github_releases_cura.json"));
            ReleaseCrossCheck.FetchForTests = delegate(string u) { pedidas.Add(u); return u.StartsWith("https://api.github.com/repos/Ultimaker/Cura/releases?") ? json : null; };
            try
            {
                var filaFab = new UpdateCandidate
                {
                    Kind = UpdateKind.Programa, Name = cura.Name, Status = UpdateStatus.AlDia, ProviderName = VendorProvider.ProviderId,
                    InstalledVersion = "5.13.0", AvailableVersion = "5.13.0", Detail = "Coincide con la última versión estable publicada por el fabricante.",
                    ProviderTag = new VendorPlan { Check = check }
                };
                var c2 = ContextoSimulado(true, cura);
                var notas = new List<string>();
                Eq(1, ReleaseCrossCheck.Resolve(c2, new List<UpdateCandidate> { filaFab }, c2.Programs, notas), "la fila del fabricante con su beta");
                Check(filaFab.Detail.Contains("5.14.0-beta.2-RC1"), "la beta en el detalle");
                Check(!pedidas.Exists(delegate(string u) { return u.Contains("winget-pkgs"); }), "sin el manifiesto de winget");
            }
            finally { ReleaseCrossCheck.FetchForTests = null; }

            // --- La política: con la configuración fijada o sin GitHub, nunca betas.
            var o = new UpdateOptions { IncludePreReleases = true };
            var pol = new Policy(); pol.ApplyJson("{ \"fuentes\": { \"github\": false } }");
            UpdateManager.ApplyPolicyToOptions(pol, o);
            Check(!o.IncludePreReleases, "sin GitHub por política: sin betas");
            var o2 = new UpdateOptions { IncludePreReleases = true };
            UpdateManager.ApplyPolicyToOptions(new Policy(), o2);
            Check(o2.IncludePreReleases, "sin política: lo que eligió el usuario");
        }
    }
}
