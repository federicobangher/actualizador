// Microsoft Store y Windows Update.
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System;
using Actualizador.Core;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {

        static void TestStoreSinDuplicados()
        {
            var appWinget = FilaWinget("App Installer", "Microsoft.AppInstaller", "1.29.379", "1.29.380");
            var appStore = Cand("App Installer", StoreProvider.ProviderId, UpdateStatus.NoVerificable); appStore.Kind = UpdateKind.Store;
            var terminalStore = Cand("Terminal Windows ", StoreProvider.ProviderId, UpdateStatus.NoVerificable); terminalStore.Kind = UpdateKind.Store;
            var terminalWinget = FilaWinget("Terminal Windows", "Microsoft.WindowsTerminal", "1.24.11911.0", "1.25.2733.0");
            var fotos = Cand("Fotos", StoreProvider.ProviderId, UpdateStatus.NoVerificable); fotos.Kind = UpdateKind.Store;
            var todas = Cand("App Installer", StoreProvider.ProviderId, UpdateStatus.Pendiente); todas.Kind = UpdateKind.Store; todas.Identifier = StoreProvider.AllAppsId;
            var all = new List<UpdateCandidate> { appWinget, appStore, terminalStore, terminalWinget, fotos, todas };
            UpdateManager.Deduplicate(all);
            Check(all.Contains(appWinget) && !all.Contains(appStore), "App Installer: queda la fila de winget");
            Check(all.Contains(terminalWinget) && !all.Contains(terminalStore), "Terminal Windows: igual (espacios al final incluidos)");
            Check(all.Contains(fotos), "la Store sigue listando lo que winget no compara");
            Check(all.Contains(todas), "«Todas las apps de Microsoft Store» nunca se quita");
        }

        static void TestStoreActualizoLoDeWinget()
        {
            var ch = StoreChange.FromSnapshot("Microsoft.WindowsTerminal", "1.24.11911.0 → 1.24.12741.0", "Terminal Windows");
            Check(ch.Package == "Microsoft.WindowsTerminal" && ch.Before == "1.24.11911.0" && ch.After == "1.24.12741.0" && ch.DisplayName == "Terminal Windows", "cambio leído");
            var sinFlecha = StoreChange.FromSnapshot("X", "2.0", null);
            Check(sinFlecha.Before == "" && sinFlecha.After == "2.0", "sin flecha: sólo la nueva");

            // El caso del registro: winget ofrecía la 1.25.2733.0 y la Store instaló la 1.24.12741.0 → sigue el error.
            var terminal = FilaWinget("Terminal Windows", "Microsoft.WindowsTerminal", "1.24.11911.0", "1.25.2733.0");
            terminal.Status = UpdateStatus.Fallida; terminal.Result = "No se pudo iniciar";
            var cambios = new List<StoreChange> { ch };
            Eq(0, StoreProvider.Reconcile(new List<UpdateCandidate> { terminal }, cambios, null), "versión menor que la ofrecida: no se toca");
            Check(terminal.Status == UpdateStatus.Fallida, "sigue fallida");

            // La Store llegó a la ofrecida (o más): actualizada por la Store.
            terminal = FilaWinget("Terminal Windows", "Microsoft.WindowsTerminal", "1.24.11911.0", "1.24.12741.0");
            terminal.Status = UpdateStatus.Fallida;
            var avisos = new List<UpdateCandidate>();
            Eq(1, StoreProvider.Reconcile(new List<UpdateCandidate> { terminal }, cambios, delegate(UpdateCandidate x) { avisos.Add(x); }), "reconciliada por el Id");
            Check(terminal.Status == UpdateStatus.Instalada && terminal.InstalledVersion == "1.24.12741.0" && terminal.Result.Contains("Microsoft Store") && avisos.Count == 1,
                  "fila actualizada y notificada: " + terminal.Result);

            // Por nombre visible (Id distinto), y una ofrecida sin versión legible.
            var calc = FilaWinget("Calculadora de Windows", "9WZDNCRFHVN5", "11.2502.2.0", "Unknown");
            calc.Status = UpdateStatus.Disponible;
            var calcCambio = StoreChange.FromSnapshot("Microsoft.WindowsCalculator", "11.2502.2.0 → 11.2607.0.0", "Calculadora de Windows");
            Eq(1, StoreProvider.Reconcile(new List<UpdateCandidate> { calc }, new List<StoreChange> { calcCambio }, null), "reconciliada por el nombre");

            // Lo que no es de winget, lo ya instalado y lo que no cambió no se tocan.
            var store = Cand("Terminal Windows", StoreProvider.ProviderId, UpdateStatus.NoVerificable);
            var hecha = FilaWinget("Terminal Windows", "Microsoft.WindowsTerminal", "1.24.11911.0", "1.24.12741.0"); hecha.Status = UpdateStatus.Instalada; hecha.Result = "Actualizada correctamente";
            var otra = FilaWinget("VLC media player", "VideoLAN.VLC", "3.0.21", "3.0.24"); otra.Status = UpdateStatus.Fallida;
            Eq(0, StoreProvider.Reconcile(new List<UpdateCandidate> { store, hecha, otra, null }, cambios, null), "nada más");
            Check(hecha.Result == "Actualizada correctamente" && otra.Status == UpdateStatus.Fallida, "sin cambios");
            Eq(0, StoreProvider.Reconcile(null, cambios, null), "sin filas");
            Eq(0, StoreProvider.Reconcile(new List<UpdateCandidate> { otra }, null, null), "sin cambios de la Store");
        }

        // ------------------------------------------------------------------ Auditoría 1.6.0: actualizaciones

        static void TestAuditoriaActualizaciones()
        {
            // Visual C++: la de x64 al día no tapa la de x86 vieja.
            var x86 = Registro(Uninst + "a", "Microsoft Visual C++ 2010 x86 Redistributable - 10.0.30319", "10.0.30319", "Microsoft", "", "", null, null, true);
            var x64 = Registro(Uninst + "b", "Microsoft Visual C++ 2010 x64 Redistributable - 10.0.40219", "10.0.40219", "Microsoft", "", "", null, null, true);
            Check(VendorProvider.ArchTag(x86) == "x86" && VendorProvider.ArchTag(x64) == "x64", "arquitectura por el nombre");
            var sinNombre = Registro(Uninst + "c", "Programa", "1", "X", "", "", null, null, false);
            Eq("x64", VendorProvider.ArchTag(sinNombre), "sin arquitectura en el nombre: la del registro");

            // GitHub: la misma tecnología de instalación.
            var rel = new GitHubRelease(); rel.Version = "2.0";
            var msi = new GitHubAsset(); msi.Name = "App-2.0-x64.msi"; msi.Url = "https://github.com/o/r/releases/download/v2.0/App-2.0-x64.msi";
            rel.Assets.Add(msi);
            Check(GitHubRules.PickAsset(rel, "x64", false) == null, "instalado con EXE y sólo hay MSI: no se cambia de tecnología");
            Check(GitHubRules.PickAsset(rel, "x64", true) == msi, "instalado con MSI: el MSI");

            // Windows Update: identificador menos específico del mismo dispositivo.
            string inst = @"PCI\VEN_8086&DEV_9A49&SUBSYS_0A201028&REV_01";
            Check(WindowsUpdateProvider.HardwareIdMatches(inst, @"PCI\VEN_8086&DEV_9A49&SUBSYS_0A201028"), "sin REV");
            Check(WindowsUpdateProvider.HardwareIdMatches(inst, @"PCI\VEN_8086&DEV_9A49"), "sin SUBSYS");
            Check(WindowsUpdateProvider.HardwareIdMatches(inst, inst.ToLowerInvariant()), "igual, sin importar mayúsculas");
            Check(!WindowsUpdateProvider.HardwareIdMatches(inst, @"PCI\VEN_8086"), "sólo el fabricante: no alcanza");
            Check(!WindowsUpdateProvider.HardwareIdMatches(inst, @"PCI\VEN_8086&DEV_9A4"), "cortado en medio de un tramo: no");
            Check(!WindowsUpdateProvider.HardwareIdMatches(inst, @"PCI\VEN_8086&DEV_9A49&SUBSYS_0A201028&REV_01&X"), "más específico que el instalado: no");
            Check(!WindowsUpdateProvider.HardwareIdMatches(null, "x&y") && !WindowsUpdateProvider.HardwareIdMatches(inst, ""), "vacíos");

            // Versiones: preliminares numeradas y fechas.
            Check(VersionUtil.IsNewer("1.2.3-beta.10", "1.2.3-beta.2"), "beta.10 > beta.2");
            Check(VersionUtil.IsNewer("1.2.3-rc1", "1.2.3-beta5"), "rc > beta");
            Check(VersionUtil.IsNewer("1.2.3", "1.2.3-rc.1"), "la final > la rc");
            Eq(0, VersionUtil.Compare("1.2.3-beta.2", "1.2.3-beta.2"), "iguales");
            Check(VersionUtil.IsNewer("2024-12-31", "2024-10-01"), "fechas: diciembre > octubre (antes, iguales)");
            Check(VersionUtil.IsNewer("v2025-01-02", "2024-12-31"), "fechas con v");
            Check(VersionUtil.IsNewer("26.03", "26.00") && VersionUtil.IsNewer("1.10", "1.9"), "lo de siempre");
        }

        static void TestStoreYWindowsUpdate()
        {
            var ahora = new DateTime(2026, 9, 20, 15, 0, 0);
            Check(StoreProvider.ShouldPreselect(null, 0, ahora), "nunca verificada: se propone");
            Check(!StoreProvider.ShouldPreselect(ahora.AddMinutes(-5), 0, ahora), "recién verificada sin novedades: NO se vuelve a proponer");
            Check(!StoreProvider.ShouldPreselect(ahora.AddHours(-11), 0, ahora), "verificada hace 11 h sin novedades: no se propone");
            Check(StoreProvider.ShouldPreselect(ahora.AddHours(-13), 0, ahora), "verificada hace 13 h: se propone de nuevo");
            Check(StoreProvider.ShouldPreselect(ahora.AddMinutes(-5), 3, ahora), "si actualizó algo, se propone de nuevo");
            Check(StoreProvider.ShouldPreselect(ahora.AddHours(2), 0, ahora), "fecha futura (reloj cambiado): se propone");

            // Qué se puede accionar: la fila de la Store se puede repetir aunque figure «al día».
            var store = new UpdateCandidate();
            store.Kind = UpdateKind.Store; store.Identifier = StoreProvider.AllAppsId; store.Installable = true; store.Status = UpdateStatus.AlDia;
            Check(UpdateManager.CanInstall(store), "la verificación de la Store se puede repetir estando al día");
            var prog = new UpdateCandidate();
            prog.Kind = UpdateKind.Programa; prog.Identifier = "X"; prog.Installable = true; prog.Status = UpdateStatus.AlDia;
            Check(!UpdateManager.CanInstall(prog), "un programa al día no se reinstala");
            prog.Status = UpdateStatus.Disponible; Check(UpdateManager.CanInstall(prog), "un programa disponible sí");
            prog.Status = UpdateStatus.Fallida; Check(UpdateManager.CanInstall(prog), "un programa que falló se puede reintentar");
            prog.Status = UpdateStatus.Instalada; Check(!UpdateManager.CanInstall(prog), "algo ya instalado no se repite");
            prog.Status = UpdateStatus.Disponible; prog.Installable = false; Check(!UpdateManager.CanInstall(prog), "no instalable nunca");
            Check(!UpdateManager.CanInstall(null), "null no revienta");

            Check(WindowsUpdateProvider.IsTransient(0x8024001E), "servicio deteniéndose es transitorio");
            Check(WindowsUpdateProvider.IsTransient(0x8024A105), "servicio ocupado es transitorio");
            Check(!WindowsUpdateProvider.IsTransient(0x80070422), "servicio deshabilitado NO es transitorio");
            Check(!WindowsUpdateProvider.IsTransient(0x8024402C), "sin conexión NO se reintenta en bucle");

            // --- Windows Update: las opcionales (versión preliminar KB5124010, caso real) tienen que aparecer
            Check(WindowsUpdateProvider.CriteriaFull.Contains("DeploymentAction=*"), "el criterio pide también las opcionales");
            Check(WindowsUpdateProvider.CriteriaFull.Contains("IsInstalled=0") && WindowsUpdateProvider.CriteriaFull.Contains("IsHidden=0"), "sin instaladas ni ocultas");
            Check(!WindowsUpdateProvider.CriteriaBasic.Contains("DeploymentAction"), "el respaldo para agentes viejos no usa DeploymentAction");
            Eq(WindowsUpdateProvider.Offer.Optional, WindowsUpdateProvider.Classify(4, false), "OptionalInstallation (versión preliminar) se ofrece como opcional");
            Eq(WindowsUpdateProvider.Offer.Normal, WindowsUpdateProvider.Classify(1, false), "Installation se ofrece normal");
            Eq(WindowsUpdateProvider.Offer.Optional, WindowsUpdateProvider.Classify(1, true), "BrowseOnly sigue siendo opcional");
            Eq(WindowsUpdateProvider.Offer.Skip, WindowsUpdateProvider.Classify(2, false), "una desinstalación pendiente no se ofrece");
            Eq(WindowsUpdateProvider.Offer.Skip, WindowsUpdateProvider.Classify(3, false), "sólo detección no se ofrece");
            Eq(WindowsUpdateProvider.Offer.Skip, WindowsUpdateProvider.Classify(0, false), "sin acción no se ofrece");
            Eq(WindowsUpdateProvider.Offer.Normal, WindowsUpdateProvider.Classify(null, false), "agente sin la propiedad: normal");
            Eq(WindowsUpdateProvider.Offer.Optional, WindowsUpdateProvider.Classify(null, true), "agente sin la propiedad y BrowseOnly: opcional");
            Check(WindowsUpdateProvider.RecommendOptional(false, true), "preliminar premarcada si el usuario pidió las últimas actualizaciones");
            Check(!WindowsUpdateProvider.RecommendOptional(false, false), "preliminar sin premarcar si no lo pidió");
            Check(!WindowsUpdateProvider.RecommendOptional(true, true), "controlador opcional nunca premarcado");
        }
    }
}
