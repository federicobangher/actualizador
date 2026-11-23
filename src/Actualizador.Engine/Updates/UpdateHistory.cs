// Historial de lo que se instaló.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Updates
{
    public sealed class HistoryEntry
    {
        public DateTime When;
        public string Kind = "";
        public string Name = "";
        public string Publisher = "";
        public string From = "";
        /// <summary>Versión con que quedó (la confirmada después de instalar), o la ofrecida si no se pudo confirmar.</summary>
        public string To = "";
        public string Offered = "";
        public string Status = "";
        public string StatusCode = "";
        public string Result = "";
        public string Source = "";
        public string Computer = "";
        public string User = "";
        public bool Admin;
        /// <summary>«interfaz», «consola» o «programada».</summary>
        public string Via = "";

        public bool Succeeded { get { return StatusCode == UpdateStatus.Instalada.ToString() || StatusCode == UpdateStatus.RequiereReinicio.ToString(); } }
        public bool Failed { get { return StatusCode == UpdateStatus.Fallida.ToString(); } }
    }

    /// <summary>
    /// Qué se instaló, cuándo, de qué versión a cuál y cómo terminó. Hasta la 1.6.7 eso quedaba sólo en los
    /// registros de actividad, que se borran a los 30 días. Se guarda en %LOCALAPPDATA%\Actualizador\historial.jsonl,
    /// una línea JSON por elemento (agregar al final no reescribe el archivo, y una línea rota no arruina las demás).
    /// Se conservan las últimas <see cref="MaxEntries"/>.
    /// </summary>
    public static class UpdateHistory
    {
        public const int MaxEntries = 5000;
        const long CompactAboveBytes = 4L * 1024 * 1024;
        static readonly object Gate = new object();

        // 2.0.10: de cada equipo (en la portable, Datos\equipos\NOMBRE): el historial es de lo que se instaló en ESTE equipo.
        public static string DefaultPath { get { return Path.Combine(AppInfo.MachineDataDirectory, "historial.jsonl"); } }

        /// <summary>
        /// Arma las entradas de una tanda: <paramref name="before"/> tiene la versión instalada de cada elemento
        /// ANTES de empezar (los proveedores la cambian al confirmar). Lo que se canceló antes de empezar no se anota.
        /// </summary>
        public static List<HistoryEntry> FromBatch(IEnumerable<UpdateCandidate> items, IDictionary<UpdateCandidate, string> before, DateTime when, string via, bool admin)
        {
            var list = new List<HistoryEntry>();
            if (items == null) return list;
            string computer = "", user = "";
            try { computer = Environment.MachineName; user = Environment.UserDomainName + "\\" + Environment.UserName; } catch { }
            foreach (var c in items)
            {
                if (c == null) continue;
                if (c.Status == UpdateStatus.Pendiente && (c.Result == InstallTally.CancelledBeforeStart || c.Result == "En cola")) continue;
                var e = new HistoryEntry();
                e.When = when;
                e.Kind = UpdateCandidate.KindText(c.Kind);
                e.Name = c.Name ?? "";
                e.Publisher = c.Publisher ?? "";
                string prev;
                e.From = before != null && before.TryGetValue(c, out prev) ? (prev ?? "") : (c.InstalledVersion ?? "");
                e.Offered = c.AvailableVersion ?? "";
                bool ok = c.Status == UpdateStatus.Instalada || c.Status == UpdateStatus.RequiereReinicio;
                string now = c.InstalledVersion ?? "";
                e.To = ok ? (now.Length > 0 && now != e.From ? now : e.Offered) : (now != e.From ? now : "");
                e.Status = UpdateCandidate.StatusText(c.Status);
                e.StatusCode = c.Status.ToString();
                e.Result = Redactor.Text(c.Result ?? "");
                e.Source = c.Source ?? "";
                e.Computer = computer;
                e.User = user;
                e.Admin = admin;
                e.Via = via ?? "";
                list.Add(e);
            }
            return list;
        }

        public static bool Append(IEnumerable<HistoryEntry> entries) { return Append(DefaultPath, entries); }

        public static bool Append(string path, IEnumerable<HistoryEntry> entries)
        {
            if (entries == null) return true;
            var sb = new StringBuilder();
            foreach (var e in entries) sb.Append(Json.Serialize(ToDict(e), false)).Append('\n');
            if (sb.Length == 0) return true;
            lock (Gate)
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!SafePaths.EnsureDataDirectory(dir)) { Log.Warn("No se anota el historial: la carpeta " + dir + " contiene un enlace."); return false; }
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        byte[] b = new UTF8Encoding(false).GetBytes(sb.ToString());
                        fs.Write(b, 0, b.Length);
                    }
                    if (new FileInfo(path).Length > CompactAboveBytes) Compact(path, MaxEntries);
                    return true;
                }
                catch (Exception ex) { Log.Warn("No se pudo anotar el historial de actualizaciones: " + ex.Message); return false; }
            }
        }

        /// <summary>Deja sólo las últimas <paramref name="keep"/> entradas.</summary>
        public static void Compact(string path, int keep)
        {
            var all = Load(path, int.MaxValue);
            if (all.Count <= keep && new FileInfo(path).Length <= CompactAboveBytes) return;
            int skip = Math.Max(0, all.Count - keep);
            var sb = new StringBuilder();
            for (int i = skip; i < all.Count; i++) sb.Append(Json.Serialize(ToDict(all[i]), false)).Append('\n');
            AtomicFile.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static List<HistoryEntry> Load() { return Load(DefaultPath, MaxEntries); }

        /// <summary>Las entradas del archivo, de la más vieja a la más nueva (como mucho las últimas <paramref name="max"/>).</summary>
        public static List<HistoryEntry> Load(string path, int max)
        {
            var list = new List<HistoryEntry>();
            try
            {
                if (!File.Exists(path)) return list;
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var rd = new StreamReader(fs, Encoding.UTF8)) text = rd.ReadToEnd();
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    try { list.Add(FromJson(Json.Parse(line))); }
                    catch { /* una línea rota (corte de luz a mitad de la escritura) no arruina las demás */ }
                }
            }
            catch (Exception ex) { Log.Warn("No se pudo leer el historial de actualizaciones: " + ex.Message); }
            if (list.Count > max) list.RemoveRange(0, list.Count - max);
            return list;
        }

        static Dictionary<string, object> ToDict(HistoryEntry e)
        {
            var d = new Dictionary<string, object>();
            d["fecha"] = e.When.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            d["tipo"] = e.Kind;
            d["nombre"] = e.Name;
            d["editor"] = e.Publisher;
            d["de"] = e.From;
            d["a"] = e.To;
            d["ofrecida"] = e.Offered;
            d["estado"] = e.Status;
            d["codigoEstado"] = e.StatusCode;
            d["resultado"] = e.Result;
            d["origen"] = e.Source;
            d["equipo"] = e.Computer;
            d["usuario"] = e.User;
            d["administrador"] = e.Admin;
            d["via"] = e.Via;
            return d;
        }

        static HistoryEntry FromJson(object o)
        {
            if (!(o is Dictionary<string, object>)) throw new FormatException("no es un objeto");
            var e = new HistoryEntry();
            DateTime d;
            if (!DateTime.TryParse(Json.Str(o, "fecha"), CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) throw new FormatException("sin fecha");
            e.When = d;
            e.Kind = Json.Str(o, "tipo") ?? "";
            e.Name = Json.Str(o, "nombre") ?? "";
            e.Publisher = Json.Str(o, "editor") ?? "";
            e.From = Json.Str(o, "de") ?? "";
            e.To = Json.Str(o, "a") ?? "";
            e.Offered = Json.Str(o, "ofrecida") ?? "";
            e.Status = Json.Str(o, "estado") ?? "";
            e.StatusCode = Json.Str(o, "codigoEstado") ?? "";
            e.Result = Json.Str(o, "resultado") ?? "";
            e.Source = Json.Str(o, "origen") ?? "";
            e.Computer = Json.Str(o, "equipo") ?? "";
            e.User = Json.Str(o, "usuario") ?? "";
            e.Admin = Json.Str(o, "administrador") == "true";
            e.Via = Json.Str(o, "via") ?? "";
            return e;
        }

        /// <summary>El historial en texto (para el paquete de diagnóstico y la consola), lo más nuevo primero.</summary>
        public static string ToText(List<HistoryEntry> entries, int max)
        {
            var sb = new StringBuilder();
            sb.Append("Historial de actualizaciones de Actualizador (lo más nuevo primero)\r\n\r\n");
            int n = 0;
            if (entries != null)
                for (int i = entries.Count - 1; i >= 0 && n < max; i--, n++)
                {
                    var e = entries[i];
                    sb.Append(e.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("  [").Append(e.Status).Append("] ")
                      .Append(e.Kind).Append(" · ").Append(e.Name);
                    if (e.From.Length > 0 || e.To.Length > 0) sb.Append(" · ").Append(e.From.Length > 0 ? e.From : "?").Append(" → ").Append(e.To.Length > 0 ? e.To : "(sin cambios)");
                    if (e.Offered.Length > 0 && e.Offered != e.To) sb.Append(" (ofrecida ").Append(e.Offered).Append(")");
                    if (e.Source.Length > 0) sb.Append(" · ").Append(e.Source);
                    if (e.Via.Length > 0) sb.Append(" · desde ").Append(e.Via).Append(e.Admin ? " (administrador)" : "");
                    sb.Append("\r\n");
                    if (e.Result.Length > 0) sb.Append("      ").Append(e.Result).Append("\r\n");
                }
            if (n == 0) sb.Append("(vacío)\r\n");
            return sb.ToString();
        }

        /// <summary>El historial como sección (la más nueva primero) para la interfaz, el informe y el diagnóstico.</summary>
        public static TableData ToSection(List<HistoryEntry> entries)
        {
            var s = new TableData("historial_actualizaciones", "Historial de actualizaciones");
            s.SetColumns("Fecha", "Tipo", "Nombre", "Versión anterior", "Versión nueva", "Estado", "Resultado", "Origen", "Desde");
            int ok = 0, failed = 0;
            if (entries != null)
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    var e = entries[i];
                    if (e.Succeeded) ok++; else if (e.Failed) failed++;
                    s.Add(e.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), e.Kind, e.Name, e.From, e.To, e.Status, e.Result, e.Source,
                          e.Via + (e.Admin ? " (administrador)" : ""));
                }
            if (s.Rows.Count == 0) s.Note("Todavía no se instaló nada con Actualizador en esta cuenta de usuario.");
            else
            {
                s.Note(Fmt.Plural(s.Rows.Count, "elemento", "elementos") + ": " + Fmt.Plural(ok, "correcto", "correctos") + ", " + failed + " con error. " +
                       "Se guardan los últimos " + MaxEntries + " en " + DefaultPath + ".");
                s.Note("Si Actualizador se ejecuta como administrador con OTRA cuenta (no la tuya), lo que instala queda en el historial de esa cuenta.");
            }
            return s;
        }
    }
}
