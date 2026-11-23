// Ignorados, historial y revisión programada.
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

        static void TestIgnorados168()
        {
            var siete = FilaWinget("7-Zip 24.07 (x64)", "7zip.7zip", "24.07", "25.01");
            siete.Recommended = true; siete.Selected = true;
            var chrome = FilaWinget("Google Chrome", "Google.Chrome", "128.0", "129.0");
            chrome.Recommended = true;
            var sinCatalogo = new UpdateCandidate();
            sinCatalogo.Kind = UpdateKind.Programa; sinCatalogo.Name = "Programa raro (usuario)"; sinCatalogo.Identifier = "registro:Programa raro";
            sinCatalogo.Status = UpdateStatus.NoVerificable;
            var todas = new List<UpdateCandidate> { siete, chrome, sinCatalogo };

            // --- Ignorar esta versión: se reconoce por el Id de winget, no por el nombre (que trae la versión).
            var l = new IgnoreList();
            var regla = IgnoreList.RuleFor(siete, false, new DateTime(2026, 10, 18, 15, 0, 0));
            Eq("7zip.7zip", regla.Id, "la regla guarda el Id de winget");
            Eq("25.01", regla.Version, "y la versión ofrecida");
            Check(l.Add(regla), "se agrega");
            Eq(1, l.Apply(todas), "una fila ignorada");
            Eq(UpdateStatus.Ignorada, siete.Status, "7-Zip queda «Ignorada»");
            Check(!siete.Recommended && !siete.Selected, "sin recomendar ni marcar (no la toman «Marcar disponibles» ni --aplicar)");
            Check(!UpdateManager.CanInstall(siete), "no se puede instalar");
            Check(siete.Result.Contains("2026-10-18") && siete.Result.Contains("Dejar de ignorar"), "el resultado dice desde cuándo y cómo volver atrás");
            Eq(UpdateStatus.Disponible, chrome.Status, "Chrome sigue disponible");
            Eq("Ignorada", UpdateCandidate.StatusText(UpdateStatus.Ignorada), "texto del estado");

            // Otra búsqueda: el nombre cambió (7-Zip 25.01 instalado por otro lado) y sale una versión nueva → se vuelve a ofrecer.
            var nueva = FilaWinget("7-Zip 25.01 (x64)", "7zip.7zip", "25.01", "25.02");
            l.Apply(new List<UpdateCandidate> { nueva });
            Eq(UpdateStatus.Disponible, nueva.Status, "una versión más nueva que la ignorada se vuelve a ofrecer");
            var misma = FilaWinget("7-Zip 24.07 (x64)", "7zip.7zip", "24.07", "25.01");
            l.Apply(new List<UpdateCandidate> { misma });
            Eq(UpdateStatus.Ignorada, misma.Status, "la misma versión sigue ignorada en la búsqueda siguiente");

            // --- Ignorar siempre.
            var l2 = new IgnoreList();
            l2.Add(IgnoreList.RuleFor(chrome, true, DateTime.Now));
            var chromeNuevo = FilaWinget("Google Chrome", "Google.Chrome", "129.0", "130.0");
            l2.Apply(new List<UpdateCandidate> { chromeNuevo });
            Eq(UpdateStatus.Ignorada, chromeNuevo.Status, "«siempre»: también las versiones siguientes");
            Check(chromeNuevo.Result.Contains("todas las versiones"), "el resultado lo aclara");

            // --- Dejar de ignorar vuelve al estado anterior sin otra búsqueda.
            siete.Result = "resultado anterior";
            var l3 = new IgnoreList();
            var otra = FilaWinget("WinRAR 7.01", "RARLab.WinRAR", "7.01", "7.23");
            otra.Status = UpdateStatus.Fallida; otra.Result = "Falló: 1603"; otra.Recommended = true;
            l3.Add(IgnoreList.RuleFor(otra, false, DateTime.Now));
            l3.Apply(new List<UpdateCandidate> { otra });
            Eq(UpdateStatus.Ignorada, otra.Status, "una que falló se puede ignorar");
            Eq(1, l3.RemoveFor(otra), "se quita la regla");
            l3.Apply(new List<UpdateCandidate> { otra });
            Eq(UpdateStatus.Fallida, otra.Status, "vuelve a «Falló la instalación»");
            Eq("Falló: 1603", otra.Result, "con su resultado de antes");
            Check(otra.Recommended && otra.IgnoredBy == null, "y vuelve a estar recomendada");

            // --- Filas sin catálogo: por el nombre (sin «(usuario)» ni mayúsculas).
            var l4 = new IgnoreList();
            var r4 = IgnoreList.RuleFor(sinCatalogo, true, DateTime.Now);
            Eq("", r4.Id, "una fila «registro:» no guarda identificador");
            l4.Add(r4);
            var mismoNombre = new UpdateCandidate();
            mismoNombre.Kind = UpdateKind.Programa; mismoNombre.Name = "PROGRAMA  raro"; mismoNombre.Status = UpdateStatus.NoVerificable;
            l4.Apply(new List<UpdateCandidate> { mismoNombre });
            Eq(UpdateStatus.Ignorada, mismoNombre.Status, "por nombre, sin importar mayúsculas, espacios ni «(usuario)»");
            var controlador = new UpdateCandidate();
            controlador.Kind = UpdateKind.Controlador; controlador.Name = "Programa raro"; controlador.Status = UpdateStatus.Disponible;
            l4.Apply(new List<UpdateCandidate> { controlador });
            Eq(UpdateStatus.Disponible, controlador.Status, "la regla de un programa no tapa un controlador con el mismo nombre");

            // --- Lo que no se ignora: al día, ya instalado.
            var alDia = FilaWinget("VLC", "VideoLAN.VLC", "3.0.21", "3.0.21"); alDia.Status = UpdateStatus.AlDia;
            Check(!IgnoreList.CanIgnore(alDia), "lo que está al día no se ignora");
            var hecha = FilaWinget("VLC", "VideoLAN.VLC", "3.0.20", "3.0.21"); hecha.Status = UpdateStatus.Instalada;
            Check(!IgnoreList.CanIgnore(hecha), "lo ya actualizado tampoco");

            // --- Consola: «nombre o Id[@versión]».
            string n, v;
            IgnoreList.SplitText("Notepad++@8.7.1", out n, out v);
            Check(n == "Notepad++" && v == "8.7.1", "«Notepad++@8.7.1»");
            IgnoreList.SplitText("  Google.Chrome  ", out n, out v);
            Check(n == "Google.Chrome" && v == "", "sin versión");
            IgnoreList.SplitText("correo@empresa@1.2", out n, out v);
            Check(n == "correo@empresa" && v == "1.2", "el último «@» separa la versión");
            var porId = IgnoreList.RuleFromText("google.chrome", DateTime.Now);
            var l5 = new IgnoreList(); l5.Add(porId);
            var chrome2 = FilaWinget("Google Chrome", "Google.Chrome", "128.0", "129.0");
            l5.Apply(new List<UpdateCandidate> { chrome2 });
            Eq(UpdateStatus.Ignorada, chrome2.Status, "una regla de la consola con el Id de winget");
            Check(IgnoreList.RuleFromText("@", DateTime.Now) == null, "«@» solo: nada");
            Eq(1, l5.RemoveByText("GOOGLE.CHROME"), "--dejar-de-ignorar por Id, sin importar mayúsculas");
            Eq(0, l5.Rules.Count, "lista vacía");

            // --- Reglas repetidas: la nueva reemplaza a la anterior.
            var l6 = new IgnoreList();
            l6.Add(IgnoreList.RuleFor(chrome, false, DateTime.Now));
            l6.Add(IgnoreList.RuleFor(chrome, true, DateTime.Now));
            Eq(1, l6.Rules.Count, "«siempre» reemplaza a «esta versión» del mismo programa");

            // --- Archivo: ida y vuelta, y uno roto no rompe nada.
            string dir = Path.Combine(Path.GetTempPath(), "sp168-ign-" + Guid.NewGuid().ToString("N"));
            try
            {
                string f = Path.Combine(dir, "ignorados.json");
                var g = new IgnoreList();
                g.Add(IgnoreList.RuleFor(siete, false, new DateTime(2026, 10, 18, 15, 0, 0)));
                g.Add(IgnoreList.RuleFromText("Programa \"raro\"\u0007", DateTime.Now));
                Check(g.Save(f), "se guarda");
                var h = IgnoreList.Load(f);
                Eq(2, h.Rules.Count, "se lee");
                Eq("7zip.7zip", h.Rules[0].Id, "con el Id");
                Eq(new DateTime(2026, 10, 18, 15, 0, 0), h.Rules[0].Added, "con la fecha");
                Eq("Programa \"raro\"", h.Rules[1].Name, "sin caracteres de control");
                File.WriteAllText(f, "{ esto no es JSON");
                Eq(0, IgnoreList.Load(f).Rules.Count, "un archivo roto: lista vacía, sin excepción");
                Eq(0, IgnoreList.Load(Path.Combine(dir, "no-existe.json")).Rules.Count, "un archivo que no existe: lista vacía");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            // --- Hallazgo: lo ignorado queda a la vista.
            var rep = new UpdateSession();
            var x = FilaWinget("7-Zip 24.07 (x64)", "7zip.7zip", "24.07", "25.01");
            rep.Updates.Add(x);
            var l7 = new IgnoreList(); l7.Add(IgnoreList.RuleFor(x, false, DateTime.Now)); l7.Apply(rep.Updates);
            Check(UpdateManager.SelectionFromVisible(new[] { new KeyValuePair<UpdateCandidate, bool>(x, true) }).Count == 0, "marcada a la fuerza en la grilla, igual no se instala");
            var orden = new List<UpdateCandidate> { x, FilaWinget("Z", "Z.Z", "1", "2") };
            UpdateManager.Sort(orden);
            Check(orden[1] == x, "lo ignorado va al final de la lista");
        }

        static void TestHistorial168()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sp168-hist-" + Guid.NewGuid().ToString("N"));
            try
            {
                string f = Path.Combine(dir, "historial.jsonl");
                var ok = FilaWinget("7-Zip 24.07 (x64)", "7zip.7zip", "24.07", "25.01");
                var mal = FilaWinget("WinRAR 7.01", "RARLab.WinRAR", "7.01", "7.23");
                var cancelada = FilaWinget("VLC", "VideoLAN.VLC", "3.0.20", "3.0.21");
                var antes = new Dictionary<UpdateCandidate, string>();
                antes[ok] = ok.InstalledVersion; antes[mal] = mal.InstalledVersion; antes[cancelada] = cancelada.InstalledVersion;
                // Lo que hace un proveedor al confirmar: cambia la versión instalada.
                ok.Status = UpdateStatus.Instalada; ok.InstalledVersion = "25.01"; ok.Result = "Actualizada correctamente — 25.01";
                mal.Status = UpdateStatus.Fallida; mal.Result = "El instalador devolvió 1603. token=secreto123";
                cancelada.Status = UpdateStatus.Pendiente; cancelada.Result = InstallTally.CancelledBeforeStart;
                var cuando = new DateTime(2026, 10, 18, 15, 30, 0);
                var e = UpdateHistory.FromBatch(new[] { ok, mal, cancelada }, antes, cuando, "interfaz", true);
                Eq(2, e.Count, "lo cancelado antes de empezar no se anota");
                Eq("24.07", e[0].From, "versión anterior: la de ANTES de instalar (el proveedor ya la cambió)");
                Eq("25.01", e[0].To, "versión nueva: la confirmada");
                Check(e[0].Succeeded && !e[0].Failed, "correcta");
                Eq("", e[1].To, "la que falló no tiene versión nueva");
                Check(e[1].Failed, "con error");
                Check(!e[1].Result.Contains("secreto123"), "sin secretos en el resultado");
                Check(UpdateHistory.Append(f, e), "se anota");
                // Una línea rota en el medio (corte de luz) no arruina las demás.
                File.AppendAllText(f, "{\"fecha\": \"2026-10-18T15:31\", \"nombre\": \"cort");
                File.AppendAllText(f, "\n");
                var otra = FilaWinget("Notepad++", "Notepad++.Notepad++", "8.7", "8.7.1");
                otra.Status = UpdateStatus.RequiereReinicio; otra.InstalledVersion = "8.7.1";
                Check(UpdateHistory.Append(f, UpdateHistory.FromBatch(new[] { otra }, null, cuando.AddMinutes(5), "consola", false)), "se agrega otra tanda");
                var leidas = UpdateHistory.Load(f, 100);
                Eq(3, leidas.Count, "se leen las tres buenas (la rota se saltea)");
                Eq("Notepad++", leidas[2].Name, "en orden: la última al final");
                Eq("consola", leidas[2].Via, "desde dónde");
                Check(leidas[0].Admin && !leidas[2].Admin, "si fue como administrador");
                Eq(cuando, leidas[0].When, "la fecha");
                Eq(2, UpdateHistory.Load(f, 2).Count, "como mucho las últimas n");

                var sec = UpdateHistory.ToSection(leidas);
                Eq("Notepad++", sec.Rows[0][2], "la sección muestra lo más nuevo primero");
                Check(sec.Notes[0].Contains("2 correctos") && sec.Notes[0].Contains("1 con error"), "con el resumen: " + sec.Notes[0]);
                string txt = UpdateHistory.ToText(leidas, 10);
                Check(txt.Contains("24.07 → 25.01") && txt.Contains("WinRAR") && txt.Contains("(sin cambios)") && txt.Contains("desde interfaz (administrador)"), "texto para el diagnóstico y la consola");
                Check(UpdateHistory.ToSection(new List<HistoryEntry>()).Notes[0].Contains("Todavía no"), "historial vacío: lo dice");

                // Compactar: se quedan las últimas.
                var muchas = new List<HistoryEntry>();
                for (int i = 0; i < 30; i++) { var h = new HistoryEntry(); h.When = cuando.AddMinutes(i); h.Name = "P" + i; muchas.Add(h); }
                string f2 = Path.Combine(dir, "h2.jsonl");
                UpdateHistory.Append(f2, muchas);
                UpdateHistory.Compact(f2, 10);
                var quedan = UpdateHistory.Load(f2, 100);
                Check(quedan.Count == 10 && quedan[0].Name == "P20" && quedan[9].Name == "P29", "compactar deja las últimas 10");
                Eq(0, UpdateHistory.Load(Path.Combine(dir, "no-existe.jsonl"), 10).Count, "sin archivo: vacío");

                // El motor lo anota al terminar la tanda (acá winget no está disponible: falla, pero se anota).
                var rep = new UpdateSession();
                var fila = FilaWinget("Paquete", "X.Paquete", "1.0", "2.0");
                rep.Updates.Add(fila);
                var man = new UpdateManager();
                man.HistoryPath = Path.Combine(dir, "motor.jsonl");
                man.Via = "programada";
                var ctx = new UpdateContext(); ctx.Os = Os(10, 0, 26200, "Professional", false); ctx.ProgramsLoaded = true;
                man.Install(ctx, rep, new List<UpdateCandidate> { fila }, false, null);
                var delMotor = UpdateHistory.Load(man.HistoryPath, 10);
                Check(delMotor.Count == 1 && delMotor[0].Name == "Paquete" && delMotor[0].Failed && delMotor[0].Via == "programada" && delMotor[0].From == "1.0",
                      "el motor anota la tanda, con la versión de antes");
                var sinHistorial = new UpdateManager(); sinHistorial.HistoryPath = "";
                var fila2 = FilaWinget("Otro", "X.Otro", "1.0", "2.0");
                sinHistorial.Install(ctx, rep, new List<UpdateCandidate> { fila2 }, false, null);
                Eq(1, UpdateHistory.Load(man.HistoryPath, 10).Count, "HistoryPath vacío: no se anota");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestRevisionProgramada168()
        {
            var sem = new ReviewSchedule();
            sem.Frequency = ReviewFrequency.Semanal; sem.Day = DayOfWeek.Wednesday; sem.Hour = 9; sem.Minute = 30;
            string exe = @"C:\Program Files\Actualizador\Actualizador.Aviso.exe";
            // Domingo 18/10/2026 a las 15:00: la primera es el miércoles 21 a las 09:30.
            string xml = ScheduledReview.TaskXml(sem, exe, "S-1-5-21-1-2-3-1001", new DateTime(2026, 10, 18, 15, 0, 0));
            var doc = new System.Xml.XmlDocument();
            bool valido = true;
            try { doc.LoadXml(xml.Substring(xml.IndexOf("<Task", StringComparison.Ordinal))); } catch (Exception ex) { valido = false; Console.WriteLine("    " + ex.Message); }
            Check(valido, "la definición de la tarea es XML válido");
            var ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            Func<string, string> X = delegate(string path) { var n = doc.SelectSingleNode(path, ns); return n == null ? null : n.InnerText; };
            Eq("2026-10-21T09:30:00", X("/t:Task/t:Triggers/t:CalendarTrigger/t:StartBoundary"), "empieza el próximo miércoles a las 09:30");
            Check(doc.SelectSingleNode("/t:Task/t:Triggers/t:CalendarTrigger/t:ScheduleByWeek/t:DaysOfWeek/t:Wednesday", ns) != null, "semanal, los miércoles");
            Eq("LeastPrivilege", X("/t:Task/t:Principals/t:Principal/t:RunLevel"), "SIN elevar (nunca «Highest»)");
            Eq("InteractiveToken", X("/t:Task/t:Principals/t:Principal/t:LogonType"), "sólo con la sesión iniciada (el aviso se tiene que ver)");
            Eq("S-1-5-21-1-2-3-1001", X("/t:Task/t:Principals/t:Principal/t:UserId"), "con la cuenta del usuario");
            Eq(exe, X("/t:Task/t:Actions/t:Exec/t:Command"), "ejecuta el aviso");
            Eq("--programada", X("/t:Task/t:Actions/t:Exec/t:Arguments"), "en modo programado");
            Eq(@"C:\Program Files\Actualizador", X("/t:Task/t:Actions/t:Exec/t:WorkingDirectory"), "desde su carpeta");
            Eq("true", X("/t:Task/t:Settings/t:StartWhenAvailable"), "si el equipo estaba apagado, corre apenas pueda");
            Eq("IgnoreNew", X("/t:Task/t:Settings/t:MultipleInstancesPolicy"), "nunca dos a la vez");
            Eq("PT2H", X("/t:Task/t:Settings/t:ExecutionTimeLimit"), "como mucho 2 horas");
            Eq("false", X("/t:Task/t:Settings/t:DisallowStartIfOnBatteries"), "también con batería");
            Check(X("/t:Task/t:RegistrationInfo/t:Description").Contains("No instala nada"), "la descripción dice que no instala nada");

            var dia = new ReviewSchedule(); dia.Frequency = ReviewFrequency.Diaria; dia.Hour = 20;
            string xml2 = ScheduledReview.TaskXml(dia, @"D:\Programas & cosas\<SP>\Actualizador.Aviso.exe", null, new DateTime(2026, 10, 18, 15, 0, 0));
            var doc2 = new System.Xml.XmlDocument();
            doc2.LoadXml(xml2.Substring(xml2.IndexOf("<Task", StringComparison.Ordinal)));
            var ns2 = new System.Xml.XmlNamespaceManager(doc2.NameTable);
            ns2.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            Eq("2026-10-18T20:00:00", doc2.SelectSingleNode("/t:Task/t:Triggers/t:CalendarTrigger/t:StartBoundary", ns2).InnerText, "diaria a las 20: hoy mismo");
            Check(doc2.SelectSingleNode("/t:Task/t:Triggers/t:CalendarTrigger/t:ScheduleByDay/t:DaysInterval", ns2) != null, "todos los días");
            Eq(@"D:\Programas & cosas\<SP>\Actualizador.Aviso.exe", doc2.SelectSingleNode("/t:Task/t:Actions/t:Exec/t:Command", ns2).InnerText, "una ruta con & y < queda bien escapada");
            Check(doc2.SelectSingleNode("/t:Task/t:Principals/t:Principal/t:UserId", ns2) == null, "sin SID: el usuario que la registra");
            var temprano = ScheduledReview.TaskXml(dia, exe, null, new DateTime(2026, 10, 18, 21, 0, 0));
            Check(temprano.Contains("2026-10-19T20:00:00"), "si ya pasó la hora de hoy, empieza mañana");

            Eq("Actualizador - Revisión de actualizaciones (fede)", ScheduledReview.TaskName("fede"), "nombre de la tarea con el usuario");
            Check(ScheduledReview.TaskName("ad\\min").IndexOf('\\') < 0, "sin barras invertidas (no son carpetas)");
            Eq(DayOfWeek.Wednesday, ScheduledReview.ParseDay("miércoles"), "«miércoles»");
            Eq(DayOfWeek.Wednesday, ScheduledReview.ParseDay("Miercoles"), "«Miercoles»");
            Eq(DayOfWeek.Saturday, ScheduledReview.ParseDay("sábados"), "«sábados»");
            Eq(DayOfWeek.Monday, ScheduledReview.ParseDay("lunes"), "«lunes» (no se le quita la s)");
            Eq(DayOfWeek.Sunday, ScheduledReview.ParseDay("7"), "«7»");
            Check(!ScheduledReview.ParseDay("feriado").HasValue, "un día que no existe");
            int h, m;
            Check(ScheduledReview.TryParseTime("9:05", out h, out m) && h == 9 && m == 5, "«9:05»");
            Check(ScheduledReview.TryParseTime("21", out h, out m) && h == 21 && m == 0, "«21»");
            Check(!ScheduledReview.TryParseTime("24:00", out h, out m) && !ScheduledReview.TryParseTime("10:60", out h, out m) && !ScheduledReview.TryParseTime("x", out h, out m), "horas inválidas");
            Eq("los miércoles a las 09:30", sem.Describe(), "descripción semanal");
            Eq("todos los días a las 20:00", dia.Describe(), "descripción diaria");
            Check(ScheduledReview.Unavailable(Os(6, 0, 6002, "Business", false)) != null, "Vista: no");

            // --- Qué se avisa.
            var filas = new List<UpdateCandidate>();
            var p1 = FilaWinget("7-Zip", "7zip.7zip", "24.07", "25.01"); filas.Add(p1);
            var p2 = FilaWinget("Chrome", "Google.Chrome", "128", "129"); filas.Add(p2);
            var ig = FilaWinget("TeamViewer", "TeamViewer.TeamViewer", "15", "16"); ig.Status = UpdateStatus.Ignorada; filas.Add(ig);
            var al = FilaWinget("VLC", "VideoLAN.VLC", "3", "3"); al.Status = UpdateStatus.AlDia; filas.Add(al);
            var drv = new UpdateCandidate(); drv.Kind = UpdateKind.Controlador; drv.Name = "Intel Wi-Fi"; drv.AvailableVersion = "23.60"; drv.Status = UpdateStatus.Disponible; filas.Add(drv);
            var wu = new UpdateCandidate(); wu.Kind = UpdateKind.WindowsUpdate; wu.Name = "KB5065789"; wu.Status = UpdateStatus.Disponible; filas.Add(wu);
            var tienda = new UpdateCandidate(); tienda.Kind = UpdateKind.Store; tienda.Name = "Todas las apps"; tienda.Status = UpdateStatus.Pendiente; filas.Add(tienda);
            var hoy = new DateTime(2026, 10, 18, 10, 0, 0);
            var o = ReviewOutcome.From(filas, hoy);
            Check(o.Programs == 2 && o.Drivers == 1 && o.Windows == 1 && o.Store == 0 && o.Ignored == 1, "cuenta por tipo, sin lo ignorado ni la acción de la Store");
            Eq(4, o.Total, "4 disponibles");
            Eq("Actualizador: 4 actualizaciones disponibles", o.Title, "título del aviso");
            Check(o.Text.StartsWith("2 programas, 1 controlador y 1 de Windows (") && o.Text.EndsWith("Tocá acá para revisarlas en Actualizador.") && o.Text.Length <= 250, "texto del aviso: " + o.Text);
            Check(ReviewOutcome.ShouldNotify(o, null, null), "primera vez: avisa");
            Check(!ReviewOutcome.ShouldNotify(o, o.Signature, hoy.AddDays(-1)), "lo mismo que ayer: no vuelve a avisar");
            Check(ReviewOutcome.ShouldNotify(o, o.Signature, hoy.AddDays(-3)), "lo mismo hace 3 días: recuerda");
            Check(ReviewOutcome.ShouldNotify(o, "otra", hoy.AddHours(-2)), "algo distinto: avisa");
            Check(!ReviewOutcome.ShouldNotify(ReviewOutcome.From(new[] { al }, hoy), null, null), "todo al día: no avisa");
            var conError = ReviewOutcome.From(filas, hoy); conError.Error = "sin red";
            Check(!ReviewOutcome.ShouldNotify(conError, null, null), "si la búsqueda falló, no avisa");
            var otraVez = ReviewOutcome.From(new List<UpdateCandidate>(filas), hoy.AddDays(1));
            Eq(o.Signature, otraVez.Signature, "la firma depende de lo disponible, no del orden ni de la fecha");

            string dir = Path.Combine(Path.GetTempPath(), "sp168-prog-" + Guid.NewGuid().ToString("N"));
            try
            {
                string f = Path.Combine(dir, "revision_programada.json");
                o.Save(f, true, null, null);
                string sig; DateTime? avisado, corrida; int total;
                ReviewOutcome.ReadLast(f, out sig, out avisado, out corrida, out total);
                Check(sig == o.Signature && avisado == hoy && corrida == hoy && total == 4, "se guarda lo avisado");
                string c = Path.Combine(dir, "programada.json");
                ScheduledReview.SaveChoice(c, sem);
                var leida = ScheduledReview.LastChoice(c);
                Check(leida.Frequency == ReviewFrequency.Semanal && leida.Day == DayOfWeek.Wednesday && leida.Hour == 9 && leida.Minute == 30, "se recuerda lo programado (miércoles 09:30)");
                var def = ScheduledReview.LastChoice(Path.Combine(dir, "no-existe.json"));
                Check(def.Frequency == ReviewFrequency.Semanal && def.Day == DayOfWeek.Monday && def.Hour == 10, "lo predeterminado: lunes a las 10");
                otraVez.Save(f, false, sig, avisado);
                ReviewOutcome.ReadLast(f, out sig, out avisado, out corrida, out total);
                Check(sig == o.Signature && avisado == hoy && corrida == hoy.AddDays(1), "sin avisar: conserva el último aviso");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
