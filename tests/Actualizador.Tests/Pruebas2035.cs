// Pruebas de la 2.0.35. Relevamiento pedido por el usuario (23/11/2026) antes de probar la actualización automática.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        /// <summary>«Marcar todo» no vuelve a marcar lo que ya se instaló y vuelve a aparecer, ni lo que falla siempre igual.</summary>
        static void TestMarcarTodoNoRepite2035()
        {
            var ahora = new DateTime(2026, 11, 24, 9, 0, 0);
            var historial = new List<HistoryEntry>
            {
                new HistoryEntry { Kind = "Controladores", Name = "HP Printer (BIDI)", Offered = "1.0.0.237", StatusCode = UpdateStatus.Instalada.ToString(), When = ahora.AddHours(-2) },
                new HistoryEntry { Kind = "Programas", Name = "Office Hub", Offered = "19.2609", StatusCode = UpdateStatus.Fallida.ToString(), When = ahora.AddHours(-3), Result = "A" },
                new HistoryEntry { Kind = "Programas", Name = "Office Hub", Offered = "19.2609", StatusCode = UpdateStatus.Fallida.ToString(), When = ahora.AddHours(-2), Result = "B" },
                new HistoryEntry { Kind = "Programas", Name = "7-Zip", Offered = "26.07", StatusCode = UpdateStatus.Fallida.ToString(), When = ahora.AddHours(-2), Result = "se cortó" },
            };
            Func<UpdateKind, string, string, UpdateCandidate> fila = delegate(UpdateKind k, string n, string v)
            {
                return new UpdateCandidate { Kind = k, Name = n, AvailableVersion = v, Status = UpdateStatus.Disponible, Installable = true, Recommended = true, ProviderName = "x" };
            };
            var bidi = fila(UpdateKind.Controlador, "HP Printer (BIDI)", "1.0.0.237");
            var hub = fila(UpdateKind.Programa, "Office Hub", "19.2609");
            var zip = fila(UpdateKind.Programa, "7-Zip", "26.07");
            var nueva = fila(UpdateKind.Programa, "VLC", "3.0.22");
            RepeatGuard.Apply(new List<UpdateCandidate> { bidi, hub, zip, nueva }, historial, ahora, delegate(DateTime d) { return true; });
            Check(bidi.ManualOnly && hub.ManualOnly, "lo que vuelve a aparecer y lo que falla siempre: sólo a mano");
            Check(!UpdateGroups.MarkedByMarkAll(bidi) && !UpdateGroups.MarkedByMarkAll(hub), "«Marcar todo» no las marca");
            Check(UpdateManager.CanInstall(bidi) && UpdateManager.CanInstall(hub), "pero se pueden marcar a mano e instalar");
            Check(!zip.ManualOnly && UpdateGroups.MarkedByMarkAll(zip), "lo que falló una sola vez: se reintenta con «Marcar todo»");
            Check(UpdateGroups.MarkedByMarkAll(nueva), "lo nuevo, como siempre");
            Check(bidi.Detail.Contains("ni con «Marcar todo»"), "el detalle lo dice");

            MachineMemory.StoreForTests = new Dictionary<string, string>();
            try
            {
                var w = new WingetRow { Name = "Microsoft.MicrosoftOfficeHub", Id = "Microsoft.365Copilot", Version = "1", Available = "2", Source = "winget", MachineScope = true, SystemContext = true };
                var c = new UpdateCandidate { Kind = UpdateKind.Programa, Name = "Office Hub" + WingetProvider.MachineSuffix, ProviderName = WingetProvider.ProviderId, Identifier = w.Id,
                                              ProviderTag = w, Status = UpdateStatus.Disponible, Installable = true, Recommended = true, InstalledVersion = "1", AvailableVersion = "2" };
                MachineMemory.Put(MachineMemory.MachineFailureKey(w.Id, "2", true), "winget respondió: falló la descarga");
                WingetProvider.ApplyMachineFailures(new List<UpdateCandidate> { c }, null);
                Check(c.ManualOnly && !UpdateGroups.MarkedByMarkAll(c), "para todos los usuarios que ya falló: tampoco con «Marcar todo»");
            }
            finally { MachineMemory.StoreForTests = null; }
        }

        /// <summary>Si la instalación sola no quedó, no se vuelve a bajar e intentar en cada apertura.</summary>
        static void TestInstalacionSolaQueNoQuedo2035()
        {
            string log = @"C:\Windows\Temp\Actualizador-1\instalacion-automatica.log";
            Eq(@"C:\Windows\Temp\Actualizador-1\resultado.txt", SelfInstall.ResultFile(log), "el resultado, junto al registro");
            string s = SelfInstall.Script(@"C:\Windows\Temp\Actualizador-1\entrega", 7, @"C:\ps.exe", @"C:\Program Files\Actualizador\Actualizador.exe", log);
            Check(s.Contains(">\"C:\\Windows\\Temp\\Actualizador-1\\resultado.txt\" echo %ERRORLEVEL%"), "el guion anota el código del instalador (redirección adelante)");
            Check(s.IndexOf("resultado.txt", StringComparison.Ordinal) < s.IndexOf("start \"\"", StringComparison.Ordinal), "antes de volver a abrir Actualizador");

            MachineMemory.StoreForTests = new Dictionary<string, string>();
            var archivos = new Dictionary<string, string>();
            SelfInstall.ReadForTests = delegate(string p) { string t; return archivos.TryGetValue(p, out t) ? t : null; };
            try
            {
                Eq(null, SelfInstall.PreviousFailure(null, "2.0.36", "2.0.35"), "sin intentos: nada");
                MachineMemory.Put("autoinstalacion|2.0.36", log);
                archivos[SelfInstall.ResultFile(log)] = "1\r\n";
                archivos[log] = "=== 1 de 6\r\nActualizador se copió pero Windows no lo deja abrir (código -1)\r\n";
                string why = SelfInstall.PreviousFailure(null, "2.0.36", "2.0.35");
                Check(why != null && why.Contains("código 1") && why.Contains("no lo deja abrir") && why.Contains(log), "falló: dice por qué y dónde está el registro: " + why);
                Eq(null, SelfInstall.PreviousFailure(null, "2.0.36", "2.0.36"), "quedó (se está usando la 2.0.36): no es una falla");
                archivos.Remove(SelfInstall.ResultFile(log));
                why = SelfInstall.PreviousFailure(null, "2.0.36", "2.0.35");
                Check(why != null && why.Contains("no terminó"), "sin resultado: el instalador no terminó");
                Eq(null, SelfInstall.PreviousFailure(null, "2.0.37", "2.0.35"), "otra versión: se intenta");
            }
            finally { MachineMemory.StoreForTests = null; SelfInstall.ReadForTests = null; }
        }

        /// <summary>Publicar.ps1 no se corta en Windows PowerShell 5.1 por lo que gh escribe en stderr.</summary>
        static void TestPublicarEnPowerShell51_2035()
        {
            string raiz = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string p = Path.Combine(raiz, "build", "Publicar.ps1");
            if (!File.Exists(p)) return;
            string ps = File.ReadAllText(p, Encoding.UTF8);
            Check(!ps.Contains("*> $null"), "sin «*> $null» con ErrorActionPreference Stop");
            Check(ps.Contains("function Test-Gh") && ps.Contains("$ErrorActionPreference = 'Continue'"), "las consultas a gh con 'Continue'");
            Check(ps.Contains("--clobber") && ps.Contains("gh release create") == false || ps.Contains("release create"), "crea la versión o reemplaza sus archivos");
        }

        static void TestVersion2035()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.35") >= 0, "Actualizador 2.0.35 o posterior: " + AppInfo.Version);
        }
    }
}
