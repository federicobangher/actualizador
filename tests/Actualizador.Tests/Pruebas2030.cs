// Pruebas de la 2.0.30. Captura del usuario (23/11/2026, 12:38): winget como servicio ya anda («como servicio, lo mismo que ve Vigía»),
// pero Office Hub para todos los usuarios quedó «No verificable» con un detalle falso («No figura en winget…») y sin probarse como servicio.
using System;
using System.Collections.Generic;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static void TestOfficeHubComoServicio2030()
        {
            MachineMemory.StoreForTests = new Dictionary<string, string>();
            try
            {
                string error = "winget respondió: winget no tiene una actualización aplicable a lo instalado; y su manifiesto no publica un paquete MSIX para x64; " +
                               "tampoco un instalador que se pueda ejecutar en silencio para x64";
                // La falla anotada fue con «--scope machine» (winget como servicio no arrancaba en ese equipo).
                MachineMemory.Put(MachineMemory.MachineFailureKey("Microsoft.365Copilot", "19.2609.33020.0", false), error);
                Func<bool, UpdateCandidate> fila = delegate(bool servicio)
                {
                    var w = new WingetRow { Name = "Microsoft.MicrosoftOfficeHub", Id = "Microsoft.365Copilot", Version = "19.2506.56051.0", Available = "19.2609.33020.0",
                                            Source = "winget", MachineScope = true, SystemContext = servicio };
                    return new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Microsoft.MicrosoftOfficeHub" + WingetProvider.MachineSuffix, ProviderName = WingetProvider.ProviderId,
                                                 Identifier = w.Id, ProviderTag = w, Status = UpdateStatus.Disponible, Installable = true, Recommended = true,
                                                 InstalledVersion = w.Version, AvailableVersion = w.Available, Detail = "Lo ve winget como servicio." };
                };
                var servicioFila = fila(true);
                Eq(0, WingetProvider.ApplyMachineFailures(new List<UpdateCandidate> { servicioFila }, null), "como servicio todavía no se probó: se ofrece");
                Check(servicioFila.Status == UpdateStatus.Disponible && servicioFila.Recommended, "marcada, para intentarla una vez por la vía de Vigía");
                var respaldo = fila(false);
                Eq(1, WingetProvider.ApplyMachineFailures(new List<UpdateCandidate> { respaldo }, null), "por «--scope machine» ya se sabe que no");
                Check(respaldo.Status == UpdateStatus.NoVerificable, "esa vía: sin verificar");

                // Su explicación no la reemplaza la genérica («No figura en winget…»).
                string antes = respaldo.Detail;
                ProgramHints.Annotate(new List<UpdateCandidate> { respaldo }, new List<InstalledProgram>());
                Eq(antes, respaldo.Detail, "el detalle de «para todos los usuarios» queda como está");
                Check(!respaldo.Detail.Contains("No figura en winget"), "sin el texto falso de la captura");
            }
            finally { MachineMemory.StoreForTests = null; }
        }

        // ================================================================== 2.0.31 (grabación del usuario, 23/11/2026 12:57)

        /// <summary>
        /// «High Definition Audio Device»: usa el genérico de Windows (10.0.26100.1); el 22/11 se instaló el del fabricante (10.0.1.42) y Windows
        /// siguió con el suyo. La 2.0.30 lo seguía ofreciendo para forzar; sin «forzar» no hacía nada. Ahora queda «Al día».
        /// </summary>
        static void TestGenericoDeWindowsElegido2031()
        {
            string hwid = @"HDAUDIO\FUNC_01&VEN_14F1&DEV_1F86&SUBSYS_103C8615&REV_1000";
            var audio = Controlador("High Definition Audio Device", "Microsoft", "MEDIA", "10.0.26100.1", hwid);
            audio.DeviceId = hwid + @"\4&1234&0&0001";
            var ctx = new UpdateContext();
            ctx.Os = new OsInfo(); ctx.Os.NativeArchitecture = "x64"; ctx.Os.Major = 10; ctx.Os.Build = 26100; ctx.Os.IsWindows = true;
            ctx.IsAdmin = true;
            ctx.Drivers.Add(audio); ctx.DriversLoaded = true; ctx.ProgramsLoaded = true;
            string guid = "cccccccc-3333-4333-8333-000000000001";
            string pagina = Catalogo2025(new[] { guid, "Conexant - MEDIA - 10.0.1.42", "Windows 10 and later drivers", "3/1/2024", "10.0.1.42" });
            string vacia = Muestra("catalogo_sin_resultados.html");
            CatalogDriverProvider.FetchForTests = delegate(string url)
            {
                if (url.Contains("ScopedViewInline.aspx")) return "<div><span id=\"ScopedViewHandler_labelArchitecture_Separator\" class=\"labelTitle\">Architecture:</span> AMD64 </div>";
                return url.EndsWith("q=" + Uri.EscapeDataString(hwid)) ? pagina : vacia;
            };
            try
            {
                var salida = new List<UpdateCandidate>();
                new CatalogDriverProvider().Scan(ctx, salida, new List<string>());
                var fila = salida.Find(delegate(UpdateCandidate c) { return c.Name == "High Definition Audio Device"; });
                Check(fila != null && fila.Status == UpdateStatus.Disponible, "la primera vez se ofrece el del fabricante");

                CatalogDriverProvider.RejectedForTests = delegate(string device, string g) { return g == guid ? "2026-11-22" : null; };
                salida.Clear();
                new CatalogDriverProvider().Scan(ctx, salida, new List<string>());
                fila = salida.Find(delegate(UpdateCandidate c) { return c.Name == "High Definition Audio Device"; });
                Check(fila != null && fila.Status == UpdateStatus.AlDia && !fila.Installable && !fila.Recommended, "Windows ya eligió el suyo: «Al día», no se ofrece forzado");
                Check(fila != null && fila.Detail.Contains("22/11/2026") && fila.Detail.Contains("no se vuelve a ofrecer"), "con el motivo: " + (fila != null ? fila.Detail : ""));
                Eq(0, UpdateManager.NeedingForce(new List<UpdateCandidate> { fila }).Count, "y la ventana no pregunta si forzar");
            }
            finally { CatalogDriverProvider.FetchForTests = null; CatalogDriverProvider.RejectedForTests = null; }
        }

        static void TestVersion2031()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.31") >= 0, "Actualizador 2.0.31 o posterior: " + AppInfo.Version);
        }

        static void TestVersion2030()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.30") >= 0, "Actualizador 2.0.30 o posterior: " + AppInfo.Version);
        }
    }
}
