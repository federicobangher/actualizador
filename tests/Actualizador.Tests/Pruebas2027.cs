// Pruebas de la 2.0.27. Grabación del usuario (23/11/2026, 11:50): en cada pasada las mismas tres filas se «instalaban» y volvían a aparecer.
using System;
using System.Collections.Generic;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        const string BidiHwid2027 = @"usb\vid_03f0&pid_5a2a&rev_0100&mi_05";

        static UpdateCandidate Bidi2027()
        {
            return new UpdateCandidate { Kind = UpdateKind.Controlador, Name = "HP Printer (BIDI)", ProviderName = WindowsUpdateProvider.ProviderId, Identifier = BidiHwid2027,
                                         InstalledVersion = "Sin controlador / genérico", AvailableVersion = "1.0.0.237 (08/04/2019)", Status = UpdateStatus.Disponible,
                                         Installable = true, Recommended = true, Detail = "Ofrecido por Windows Update para " + BidiHwid2027 + "." };
        }

        /// <summary>Dos «HP Printer (BIDI)» iguales: el mismo controlador publicado dos veces. Se muestra una.</summary>
        static void TestMismoControladorDosVeces2027()
        {
            var salida = new List<UpdateCandidate> { Bidi2027() };
            Check(WindowsUpdateProvider.IsSameDriverOffer(salida, Bidi2027()), "la segunda HP Printer (BIDI), igual: no se repite");
            var otraVersion = Bidi2027(); otraVersion.AvailableVersion = "1.0.0.240 (01/01/2024)";
            Check(!WindowsUpdateProvider.IsSameDriverOffer(salida, otraVersion), "otra versión sí es otra fila");
            var otroDispositivo = Bidi2027(); otroDispositivo.Identifier = @"usb\vid_03f0&pid_5a2a&rev_0100&mi_00";
            Check(!WindowsUpdateProvider.IsSameDriverOffer(salida, otroDispositivo), "otro dispositivo, también");
            var programa = Bidi2027(); programa.Kind = UpdateKind.WindowsUpdate;
            Check(!WindowsUpdateProvider.IsSameDriverOffer(salida, programa), "sólo controladores");
        }

        /// <summary>«Instalada correctamente» y en la búsqueda siguiente otra vez «Sin controlador»: no lo tomó ningún dispositivo.</summary>
        static void TestControladorSinDispositivo2027()
        {
            MachineMemory.StoreForTests = new Dictionary<string, string>();
            try
            {
                var c = Bidi2027();
                WindowsUpdateProvider.AdjustNoDeviceDriver(c, BidiHwid2027, "1.0.0.237", null);
                Check(c.Status == UpdateStatus.Disponible && c.Installable && c.Recommended, "la primera vez se ofrece, como antes");
                MachineMemory.Put(MachineMemory.WuDriverKey(BidiHwid2027.ToUpperInvariant(), "1.0.0.237"), "");
                c = Bidi2027();
                WindowsUpdateProvider.AdjustNoDeviceDriver(c, BidiHwid2027, "1.0.0.237", null);
                Check(c.Status == UpdateStatus.AlDia && !c.Installable && !c.Recommended, "ya instalada y ofrecida otra vez: «Al día», no se instala de nuevo");
                Check(c.Detail.Contains("desconectado") && c.Detail.Contains(DateTime.Now.ToString("dd/MM/yyyy")), "explica por qué y desde cuándo: " + c.Detail);
                c = Bidi2027();
                WindowsUpdateProvider.AdjustNoDeviceDriver(c, BidiHwid2027, "1.0.0.240", null);
                Check(c.Status == UpdateStatus.Disponible, "una versión nueva se ofrece igual");
            }
            finally { MachineMemory.StoreForTests = null; }
        }

        /// <summary>«Microsoft.MicrosoftOfficeHub (para todos los usuarios)» fallaba igual en cada pasada.</summary>
        static void TestParaTodosLosUsuariosQueFalla2027()
        {
            MachineMemory.StoreForTests = new Dictionary<string, string>();
            try
            {
                var w = new WingetRow { Name = "Microsoft.MicrosoftOfficeHub", Id = "Microsoft.365Copilot", Version = "19.2506.56051.0", Available = "19.2609.33020.0", Source = "winget", MachineScope = true, SystemContext = true };
                Func<UpdateCandidate> fila = delegate
                {
                    return new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Microsoft.MicrosoftOfficeHub" + WingetProvider.MachineSuffix, ProviderName = WingetProvider.ProviderId,
                                                 Identifier = w.Id, ProviderTag = w, Status = UpdateStatus.Disponible, Installable = true, Recommended = true,
                                                 InstalledVersion = w.Version, AvailableVersion = w.Available, Detail = "Lo ve winget como servicio." };
                };
                var lista = new List<UpdateCandidate> { fila() };
                Eq(0, WingetProvider.ApplyMachineFailures(lista, null), "sin fallas anotadas: nada cambia");
                Check(lista[0].Recommended, "y queda marcada (es lo que lista Vigía)");
                MachineMemory.Put(MachineMemory.MachineFailureKey(w.Id, w.Available, w.SystemContext), "winget respondió: no se encontró una actualización aplicable");
                lista = new List<UpdateCandidate> { fila() };
                Eq(1, WingetProvider.ApplyMachineFailures(lista, null), "falló antes con esta versión");
                var c = lista[0];
                Check(c.Status == UpdateStatus.Disponible && c.Installable && !c.Recommended, "sigue en la lista y se puede marcar a mano, pero no se marca sola");
                Check(c.Detail.Contains("no se pudo actualizar") && c.Detail.Contains("no se encontró una actualización aplicable") && c.Detail.Contains("Microsoft Store"),
                      "dice por qué falló y qué pasa con la app de cada usuario: " + c.Detail);
                var nueva = fila(); nueva.AvailableVersion = "19.2610.1.0";
                lista = new List<UpdateCandidate> { nueva };
                Eq(0, WingetProvider.ApplyMachineFailures(lista, null), "otra versión publicada: se vuelve a intentar sola");
            }
            finally { MachineMemory.StoreForTests = null; }
        }

        static void TestVersion2027()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.27") >= 0, "Actualizador 2.0.27 o posterior: " + AppInfo.Version);
        }
    }
}
