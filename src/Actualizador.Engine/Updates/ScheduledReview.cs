// Revisión programada de actualizaciones.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Updates
{
    public enum ReviewFrequency { Diaria, Semanal }

    public sealed class ReviewSchedule
    {
        public ReviewFrequency Frequency = ReviewFrequency.Semanal;
        public DayOfWeek Day = DayOfWeek.Monday;
        public int Hour = 10, Minute = 0;

        public string Describe()
        {
            string hora = Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + Minute.ToString("00", CultureInfo.InvariantCulture);
            return Frequency == ReviewFrequency.Diaria ? "todos los días a las " + hora : "los " + ScheduledReview.DayName(Day) + " a las " + hora;
        }
    }

    /// <summary>Estado de la tarea programada en el Programador de tareas de Windows.</summary>
    public sealed class ReviewTaskState
    {
        public bool Exists, Enabled;
        public DateTime? NextRun, LastRun;
        public int LastResult;
        public string Command = "";
        public string Error;

        public string Describe()
        {
            if (Error != null) return "No se pudo consultar el Programador de tareas: " + Error;
            if (!Exists) return "Desactivada.";
            var sb = new StringBuilder(Enabled ? "Activada" : "Creada pero deshabilitada en el Programador de tareas");
            if (NextRun.HasValue) sb.Append(". Próxima: ").Append(Fmt.Date(NextRun.Value));
            if (LastRun.HasValue) sb.Append(". Última: ").Append(Fmt.Date(LastRun.Value)).Append(LastResult == 0 ? " (correcta)" : " (código " + Fmt.Hex(LastResult) + ")");
            return sb.Append(".").ToString();
        }
    }

    /// <summary>
    /// Una tarea del Programador de tareas, de la cuenta del usuario, que ejecuta «Actualizador.Aviso.exe --programada»:
    /// busca actualizaciones (SIN instalar nada) y, si hay, muestra un aviso junto al reloj. Corre sin privilegios
    /// elevados (LeastPrivilege) y sólo con la sesión iniciada; se crea con la API COM del Programador de tareas
    /// (no con schtasks.exe), como la lee la auditoría de tareas.
    /// </summary>
    public static class ScheduledReview
    {
        public const string AvisoExe = "Actualizador.Aviso.exe";
        public const string Argument = "--programada";
        const int TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_INTERACTIVE_TOKEN = 3;

        public static string TaskName(string userName)
        {
            string u = Fmt.SafeFileName(string.IsNullOrEmpty(userName) ? "usuario" : userName).Replace("\\", "_");
            return "Actualizador - Revisión de actualizaciones (" + u + ")";
        }

        public static string CurrentTaskName { get { return TaskName(Environment.UserName); } }

        /// <summary>2.0.11: el nombre de la tarea cuando el programa se llamaba SuperProgram (el instalador la pasa al nuevo).</summary>
        public static string LegacyTaskName(string userName)
        {
            string u = Fmt.SafeFileName(string.IsNullOrEmpty(userName) ? "usuario" : userName).Replace("\\", "_");
            return AppInfo.LegacyName + " - Revisión de actualizaciones (" + u + ")";
        }

        public static string DayName(DayOfWeek d)
        {
            switch (d)
            {
                case DayOfWeek.Monday: return "lunes";
                case DayOfWeek.Tuesday: return "martes";
                case DayOfWeek.Wednesday: return "miércoles";
                case DayOfWeek.Thursday: return "jueves";
                case DayOfWeek.Friday: return "viernes";
                case DayOfWeek.Saturday: return "sábados";
                default: return "domingos";
            }
        }

        /// <summary>«lunes», «Miércoles», «miercoles», «sábado», «1»… → día; null si no se reconoce.</summary>
        public static DayOfWeek? ParseDay(string s)
        {
            string t = (s ?? "").Trim().ToLowerInvariant().Replace("á", "a").Replace("é", "e");
            if (t.EndsWith("s") && t.Length > 3 && t != "lunes" && t != "martes" && t != "miercoles" && t != "jueves" && t != "viernes") t = t.Substring(0, t.Length - 1);
            switch (t)
            {
                case "lunes": case "lun": case "1": case "monday": return DayOfWeek.Monday;
                case "martes": case "mar": case "2": case "tuesday": return DayOfWeek.Tuesday;
                case "miercoles": case "mie": case "3": case "wednesday": return DayOfWeek.Wednesday;
                case "jueves": case "jue": case "4": case "thursday": return DayOfWeek.Thursday;
                case "viernes": case "vie": case "5": case "friday": return DayOfWeek.Friday;
                case "sabado": case "sab": case "6": case "saturday": return DayOfWeek.Saturday;
                case "domingo": case "dom": case "7": case "0": case "sunday": return DayOfWeek.Sunday;
                default: return null;
            }
        }

        /// <summary>«10:30», «9», «09:05» → hora y minutos.</summary>
        public static bool TryParseTime(string s, out int hour, out int minute)
        {
            hour = minute = 0;
            string t = (s ?? "").Trim();
            string[] p = t.Split(':');
            if (p.Length > 2) return false;
            if (!int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out hour) || hour < 0 || hour > 23) return false;
            if (p.Length == 2 && (!int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out minute) || minute < 0 || minute > 59)) return false;
            return true;
        }

        /// <summary>
        /// La definición XML de la tarea (esquema 1.2: Windows 7 en adelante). Corre con la cuenta de
        /// <paramref name="userSid"/>, sólo con la sesión iniciada y SIN elevar; si el equipo estaba apagado a esa
        /// hora, corre apenas pueda; necesita red; como mucho 2 horas; nunca dos a la vez.
        /// </summary>
        public static string TaskXml(ReviewSchedule s, string exePath, string userSid, DateTime now)
        {
            if (s == null) throw new ArgumentNullException("s");
            if (string.IsNullOrEmpty(exePath)) throw new ArgumentException("ruta vacía", "exePath");
            var start = new DateTime(now.Year, now.Month, now.Day, s.Hour, s.Minute, 0);
            if (start <= now) start = start.AddDays(1);
            if (s.Frequency == ReviewFrequency.Semanal) while (start.DayOfWeek != s.Day) start = start.AddDays(1);
            var x = new StringBuilder();
            x.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n");
            x.Append("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n");
            x.Append("  <RegistrationInfo>\r\n");
            x.Append("    <Author>").Append(E(AppInfo.Name)).Append("</Author>\r\n");
            x.Append("    <Description>").Append(E("Busca actualizaciones de programas, controladores, Windows y apps de Microsoft Store " + s.Describe() +
                     " y avisa si hay. No instala nada. Se configura en Actualizador › Opciones › Actualizaciones.")).Append("</Description>\r\n");
            x.Append("  </RegistrationInfo>\r\n");
            x.Append("  <Triggers>\r\n    <CalendarTrigger>\r\n");
            x.Append("      <StartBoundary>").Append(start.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)).Append("</StartBoundary>\r\n");
            x.Append("      <Enabled>true</Enabled>\r\n");
            x.Append("      <RandomDelay>PT10M</RandomDelay>\r\n");
            if (s.Frequency == ReviewFrequency.Diaria)
                x.Append("      <ScheduleByDay>\r\n        <DaysInterval>1</DaysInterval>\r\n      </ScheduleByDay>\r\n");
            else
                x.Append("      <ScheduleByWeek>\r\n        <DaysOfWeek>\r\n          <").Append(s.Day.ToString()).Append(" />\r\n        </DaysOfWeek>\r\n        <WeeksInterval>1</WeeksInterval>\r\n      </ScheduleByWeek>\r\n");
            x.Append("    </CalendarTrigger>\r\n  </Triggers>\r\n");
            x.Append("  <Principals>\r\n    <Principal id=\"Author\">\r\n");
            if (!string.IsNullOrEmpty(userSid)) x.Append("      <UserId>").Append(E(userSid)).Append("</UserId>\r\n");
            x.Append("      <LogonType>InteractiveToken</LogonType>\r\n      <RunLevel>LeastPrivilege</RunLevel>\r\n");
            x.Append("    </Principal>\r\n  </Principals>\r\n");
            x.Append("  <Settings>\r\n");
            x.Append("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n");
            x.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n");
            x.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n");
            x.Append("    <AllowHardTerminate>true</AllowHardTerminate>\r\n");
            x.Append("    <StartWhenAvailable>true</StartWhenAvailable>\r\n");
            x.Append("    <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>\r\n");
            x.Append("    <IdleSettings>\r\n      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n      <RestartOnIdle>false</RestartOnIdle>\r\n    </IdleSettings>\r\n");
            x.Append("    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n");
            x.Append("    <Enabled>true</Enabled>\r\n");
            x.Append("    <Hidden>false</Hidden>\r\n");
            x.Append("    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n");
            x.Append("    <WakeToRun>false</WakeToRun>\r\n");
            x.Append("    <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>\r\n");
            x.Append("    <Priority>7</Priority>\r\n");
            x.Append("  </Settings>\r\n");
            x.Append("  <Actions Context=\"Author\">\r\n    <Exec>\r\n");
            x.Append("      <Command>").Append(E(exePath)).Append("</Command>\r\n");
            x.Append("      <Arguments>").Append(Argument).Append("</Arguments>\r\n");
            int slash = exePath.LastIndexOfAny(new[] { '\\', '/' });
            string dir = slash > 0 ? exePath.Substring(0, slash) : "";
            if (!string.IsNullOrEmpty(dir)) x.Append("      <WorkingDirectory>").Append(E(dir)).Append("</WorkingDirectory>\r\n");
            x.Append("    </Exec>\r\n  </Actions>\r\n</Task>\r\n");
            return x.ToString();
        }

        static string E(string s) { return SecurityElement.Escape(s ?? "") ?? ""; }

        /// <summary>El aviso que ejecuta la tarea: al lado de este programa.</summary>
        public static string AvisoPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AvisoExe); } }

        /// <summary>
        /// 2.0.10: la portable no programa revisiones: la tarea quedaría apuntando a un pendrive o a una carpeta que la próxima
        /// vez puede no estar (y el Programador de tareas ejecutaría lo que haya en esa ruta).
        /// </summary>
        public const string PortableReason = "La versión portable no programa revisiones: la tarea quedaría apuntando a esta carpeta, que la próxima vez " +
                                             "puede no estar en el equipo. Para usarla, instalá Actualizador (Instalar.cmd de la entrega común).";

        /// <summary>¿Por qué no se puede programar desde acá? null si se puede.</summary>
        public static string Unavailable(OsInfo os)
        {
            return Unavailable(os, AppInfo.IsPortable);
        }

        public static string Unavailable(OsInfo os, bool portable)
        {
            if (portable) return PortableReason;
            if (os == null || !os.IsWindows) return "La revisión programada usa el Programador de tareas de Windows.";
            if (!os.AtLeast(6, 1)) return "La revisión programada necesita Windows 7 o posterior.";
            if (!File.Exists(AvisoPath)) return "Falta " + AvisoExe + " en la carpeta del programa (" + AppDomain.CurrentDomain.BaseDirectory + "): reinstalá Actualizador con Instalar.cmd.";
            return null;
        }

        static object Folder()
        {
            object svc = Com.Create("Schedule.Service");
            Com.Call(svc, "Connect");
            return Com.Call(svc, "GetFolder", "\\");
        }

        /// <summary>Crea o actualiza la tarea del usuario actual. Devuelve null si salió bien; si no, el motivo.</summary>
        public static string Register(ReviewSchedule s)
        {
            string why = Unavailable(OsInfo.Detect());
            if (why != null) return why;
            try
            {
                string sid;
                using (var id = WindowsIdentity.GetCurrent()) sid = id.User != null ? id.User.Value : null;
                string xml = TaskXml(s, AvisoPath, sid, DateTime.Now);
                object folder = Folder();
                Com.Call(folder, "RegisterTask", CurrentTaskName, xml, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
                DeleteLegacy(folder);
                SaveChoice(ChoicePath, s);
                Log.Info("Revisión programada: " + s.Describe() + " (tarea «" + CurrentTaskName + "», " + AvisoPath + ")");
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn("No se pudo crear la revisión programada: " + ex.Message);
                return Describe(ex);
            }
        }

        /// <summary>Lo último que se programó (para mostrarlo en Opciones), en %LOCALAPPDATA%\Actualizador\programada.json.</summary>
        public static string ChoicePath { get { return Path.Combine(AppInfo.DataDirectory, "programada.json"); } }

        public static void SaveChoice(string path, ReviewSchedule s)
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["frecuencia"] = s.Frequency == ReviewFrequency.Diaria ? "diaria" : "semanal";
                d["dia"] = DayName(s.Day);
                d["hora"] = s.Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + s.Minute.ToString("00", CultureInfo.InvariantCulture);
                if (!SafePaths.EnsureDataDirectory(Path.GetDirectoryName(path))) return;
                AtomicFile.WriteAllText(path, Json.Serialize(d, true), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("Revisión programada: " + ex.Message); }
        }

        /// <summary>Lo último que se programó, o lo predeterminado (semanal, los lunes a las 10:00).</summary>
        public static ReviewSchedule LastChoice() { return LastChoice(ChoicePath); }

        public static ReviewSchedule LastChoice(string path)
        {
            var s = new ReviewSchedule();
            try
            {
                if (!File.Exists(path)) return s;
                object o = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                s.Frequency = Json.Str(o, "frecuencia") == "diaria" ? ReviewFrequency.Diaria : ReviewFrequency.Semanal;
                var d = ParseDay(Json.Str(o, "dia"));
                if (d.HasValue) s.Day = d.Value;
                int h, m;
                if (TryParseTime(Json.Str(o, "hora"), out h, out m)) { s.Hour = h; s.Minute = m; }
            }
            catch (Exception ex) { Log.Warn("Revisión programada guardada: " + ex.Message); }
            return s;
        }

        /// <summary>Borra la tarea del usuario actual (si no existe, no es un error).</summary>
        public static string Unregister()
        {
            try
            {
                object folder = Folder();
                DeleteLegacy(folder);
                Com.Call(folder, "DeleteTask", CurrentTaskName, 0);
                Log.Info("Revisión programada desactivada.");
                return null;
            }
            catch (Exception ex)
            {
                if (NotFound(ex)) return null;
                Log.Warn("No se pudo borrar la revisión programada: " + ex.Message);
                return Describe(ex);
            }
        }

        /// <summary>Quita la tarea con el nombre viejo, si quedó (no avisaría dos veces).</summary>
        static void DeleteLegacy(object folder)
        {
            try { Com.Call(folder, "DeleteTask", LegacyTaskName(Environment.UserName), 0); }
            catch (Exception ex) { if (!NotFound(ex)) Log.Warn("No se pudo quitar la revisión programada vieja: " + ex.Message); }
        }

        public static ReviewTaskState Query()
        {
            var st = new ReviewTaskState();
            if (OsInfo.Detect().IsWindows == false) return st;
            try
            {
                object t = Com.Call(Folder(), "GetTask", CurrentTaskName);
                st.Exists = true;
                st.Enabled = Convert.ToBoolean(Com.Get(t, "Enabled"), CultureInfo.InvariantCulture);
                st.NextRun = ComDate(Com.TryGet(t, "NextRunTime"));
                st.LastRun = ComDate(Com.TryGet(t, "LastRunTime"));
                object r = Com.TryGet(t, "LastTaskResult");
                st.LastResult = r == null ? 0 : Convert.ToInt32(r, CultureInfo.InvariantCulture);
                string xml = Com.Str(t, "Xml") ?? "";
                int a = xml.IndexOf("<Command>", StringComparison.Ordinal), b = xml.IndexOf("</Command>", StringComparison.Ordinal);
                if (a >= 0 && b > a) st.Command = xml.Substring(a + 9, b - a - 9);
            }
            catch (Exception ex)
            {
                if (!NotFound(ex)) st.Error = Describe(ex);
            }
            return st;
        }

        /// <summary>El Programador de tareas devuelve 30/12/1899 como «nunca».</summary>
        static DateTime? ComDate(object o)
        {
            if (!(o is DateTime)) return null;
            var d = (DateTime)o;
            return d.Year < 2000 ? (DateTime?)null : d;
        }

        /// <summary>La tarea no existe (todavía no se programó): no es un error. Ver <see cref="Com.HResultOf"/>.</summary>
        internal static bool NotFound(Exception ex)
        {
            int hr = Com.HResultOf(ex);
            return hr == unchecked((int)0x80070002) || hr == unchecked((int)0x80070003);
        }

        internal static string Describe(Exception ex)
        {
            if (Com.HResultOf(ex) == unchecked((int)0x80070005)) return "Windows no permite crear la tarea con esta cuenta (acceso denegado). Probá desde una cuenta de administrador.";
            return ex.Message;
        }
    }

    /// <summary>Resultado de una revisión programada, para decidir el aviso y mostrarlo.</summary>
    public sealed class ReviewOutcome
    {
        public DateTime When;
        public int Programs, Drivers, Windows, Store, Ignored;
        public readonly List<string> Names = new List<string>();
        public string Error;

        public int Total { get { return Programs + Drivers + Windows + Store; } }

        public static ReviewOutcome From(IEnumerable<UpdateCandidate> items, DateTime when)
        {
            var o = new ReviewOutcome();
            o.When = when;
            if (items == null) return o;
            foreach (var u in items)
            {
                if (u == null) continue;
                if (u.Status == UpdateStatus.Ignorada) { o.Ignored++; continue; }
                if (u.Status != UpdateStatus.Disponible) continue;
                switch (u.Kind)
                {
                    case UpdateKind.Programa: o.Programs++; break;
                    case UpdateKind.Controlador: o.Drivers++; break;
                    case UpdateKind.Store: o.Store++; break;
                    default: o.Windows++; break;
                }
                o.Names.Add((u.Name ?? "") + (string.IsNullOrEmpty(u.AvailableVersion) ? "" : " " + u.AvailableVersion));
            }
            o.Names.Sort(StringComparer.OrdinalIgnoreCase);
            return o;
        }

        /// <summary>Identifica el conjunto de lo disponible: si es el mismo que la vez anterior, no hace falta volver a avisar enseguida.</summary>
        public string Signature
        {
            get
            {
                var sb = new StringBuilder();
                foreach (string n in Names) sb.Append(n.ToLowerInvariant()).Append('\n');
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").Substring(0, 16);
            }
        }

        public string Title
        {
            get { return Total == 1 ? "Actualizador: 1 actualización disponible" : "Actualizador: " + Total + " actualizaciones disponibles"; }
        }

        /// <summary>«3 programas, 1 controlador y 2 de Windows. Tocá para revisarlas.» (cabe en un aviso de Windows)</summary>
        public string Text
        {
            get
            {
                var parts = new List<string>();
                if (Programs > 0) parts.Add(Fmt.Plural(Programs, "programa", "programas"));
                if (Drivers > 0) parts.Add(Fmt.Plural(Drivers, "controlador", "controladores"));
                if (Windows > 0) parts.Add(Windows == 1 ? "1 de Windows" : Windows + " de Windows");
                if (Store > 0) parts.Add(Store == 1 ? "1 app de la Store" : Store + " apps de la Store");
                string names = Names.Count == 0 ? "" : " (" + Fmt.Truncate(string.Join(", ", Names.ToArray()), 120) + ")";
                return Fmt.Truncate(Fmt.JoinY(parts) + names, 200) + ". Tocá acá para revisarlas en Actualizador.";
            }
        }

        /// <summary>
        /// ¿Avisar? Si hay algo disponible y es distinto de lo que se avisó la última vez, o pasaron 3 días desde ese
        /// aviso (así no aparece el mismo aviso todos los días con la revisión diaria).
        /// </summary>
        public static bool ShouldNotify(ReviewOutcome now, string lastSignature, DateTime? lastNotified)
        {
            if (now == null || now.Total == 0 || now.Error != null) return false;
            if (string.IsNullOrEmpty(lastSignature) || !lastNotified.HasValue) return true;
            if (lastSignature != now.Signature) return true;
            return (now.When - lastNotified.Value).TotalDays >= 3 || lastNotified.Value > now.When;
        }

        public static string StatePath { get { return Path.Combine(AppInfo.DataDirectory, "revision_programada.json"); } }

        public void Save(string path, bool notified, string lastSignature, DateTime? lastNotified)
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["fecha"] = When.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                d["disponibles"] = Total;
                d["programas"] = Programs; d["controladores"] = Drivers; d["windows"] = Windows; d["store"] = Store; d["ignoradas"] = Ignored;
                d["elementos"] = new List<string>(Names);
                if (Error != null) d["error"] = Error;
                d["firmaAvisada"] = notified ? Signature : (lastSignature ?? "");
                var n = notified ? When : lastNotified;
                d["avisado"] = n.HasValue ? n.Value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : "";
                if (!SafePaths.EnsureDataDirectory(Path.GetDirectoryName(path))) return;
                AtomicFile.WriteAllText(path, Json.Serialize(d, true), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("Revisión programada: no se pudo guardar el resultado: " + ex.Message); }
        }

        /// <summary>Lo que se avisó la última vez (firma y fecha), y la fecha y el total de la última revisión.</summary>
        public static void ReadLast(string path, out string signature, out DateTime? notified, out DateTime? lastRun, out int lastTotal)
        {
            signature = null; notified = null; lastRun = null; lastTotal = 0;
            try
            {
                if (!File.Exists(path)) return;
                object o = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                signature = Json.Str(o, "firmaAvisada");
                DateTime d;
                if (DateTime.TryParse(Json.Str(o, "avisado"), CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) notified = d;
                if (DateTime.TryParse(Json.Str(o, "fecha"), CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) lastRun = d;
                int t;
                if (int.TryParse(Json.Str(o, "disponibles"), out t)) lastTotal = t;
            }
            catch (Exception ex) { Log.Warn("Revisión programada: " + ex.Message); }
        }
    }
}
