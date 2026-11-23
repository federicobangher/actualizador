// Registro de auditoría a prueba de cambios: quién actualizó qué, cuándo y con qué resultado.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Actualizador.Core.Security
{
    /// <summary>
    /// Cada evento es una línea JSON en «auditoria-AAAA-MM.jsonl» con un número de secuencia y un HMAC-SHA256 que incluye el
    /// HMAC de la línea anterior (cadena). Borrar, cambiar o reordenar una línea —o un archivo entero— rompe la cadena y
    /// «Actualizador.Cli verificar-registro» lo detecta. La clave del HMAC está en la misma carpeta, que sólo pueden leer y
    /// modificar los administradores (%ProgramData%\Actualizador\auditoria). Sin privilegios, el registro va a la carpeta del
    /// usuario, con su propia clave.
    /// 2.0.11: la clave ya no se cifra con DPAPI. Descifrar con CryptUnprotectData es lo que hacen los programas que roban
    /// contraseñas del navegador, y el escudo de comportamiento de Avast cortaba Actualizador al terminar cada búsqueda
    /// (IDP.Generic). Con DPAPI «de la máquina» cualquier proceso del equipo la descifraba igual: lo que protege la clave es el
    /// permiso de la carpeta. Un registro de antes (con «clave.bin», cifrada) se guarda entero en una subcarpeta «anterior-…»
    /// sin descifrarlo, y la cadena empieza de nuevo.
    /// Cada evento importante también va al Visor de eventos de Windows (origen «Actualizador»).
    /// </summary>
    public sealed class AuditTrail
    {
        public const string Prefix = "auditoria-";
        const string KeyFile = "clave.key";
        const string LegacyKeyFile = "clave.bin";   // hasta la 2.0.10: cifrada con DPAPI
        const int KeyLength = 32;

        readonly string dir;
        readonly bool machine;
        static readonly object gate = new object();

        /// <summary>Sólo para las pruebas: carpeta propia y clave fija.</summary>
        internal static string DirectoryForTests;
        internal static byte[] KeyForTests;

        AuditTrail(string dir, bool machine) { this.dir = dir; this.machine = machine; }

        public string Directory { get { return dir; } }
        public bool IsMachineWide { get { return machine; } }

        /// <summary>El registro de este proceso: el del equipo si se puede escribir con seguridad; si no, el del usuario.</summary>
        public static AuditTrail Open()
        {
            if (DirectoryForTests != null) return new AuditTrail(DirectoryForTests, false);
            string machineDir = Path.Combine(Policy.MachineDirectory, "auditoria");
            if (OsInfo.IsAdministrator() && AclGuard.EnsureAdminDirectory(Policy.MachineDirectory, true) && AclGuard.EnsureAdminDirectory(machineDir, false))
                return new AuditTrail(machineDir, true);
            // De cada equipo: la clave es DPAPI del usuario EN ESTE equipo (la portable no la puede leer en otro).
            string userDir = Path.Combine(AppInfo.MachineDataDirectory, "auditoria");
            try { if (SafePaths.EnsureDataDirectory(AppInfo.MachineDataDirectory)) System.IO.Directory.CreateDirectory(userDir); } catch (Exception ex) { Log.Warn("Registro de auditoría: " + ex.Message); }
            return new AuditTrail(userDir, false);
        }

        /// <summary>
        /// Anota un evento. Nunca interrumpe la actualización: si no se puede escribir, lo dice en el registro de la aplicación.
        /// </summary>
        public bool Record(string evento, IDictionary<string, object> data)
        {
            try
            {
                using (var mutex = new Mutex(false, (machine ? @"Global\" : @"Local\") + "Actualizador.Auditoria"))
                {
                    bool owned = false;
                    try
                    {
                        try { owned = mutex.WaitOne(10000); } catch (AbandonedMutexException) { owned = true; }
                        lock (gate) return Append(evento, data);
                    }
                    finally { if (owned) mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex) { Log.Warn("No se pudo anotar en el registro de auditoría (" + evento + "): " + ex.Message); return false; }
        }

        bool Append(string evento, IDictionary<string, object> data)
        {
            System.IO.Directory.CreateDirectory(dir);
            string archived = ArchiveLegacy();
            byte[] key = Key(true);
            if (archived != null)
            {
                var d = new Dictionary<string, object>();
                d["motivo"] = "registro de una versión anterior, con la clave cifrada con DPAPI: se guardó sin tocar y empieza una cadena nueva";
                d["carpeta"] = archived;
                AppendLine(key, "registro-nuevo", d);
            }
            return AppendLine(key, evento, data);
        }

        bool AppendLine(byte[] key, string evento, IDictionary<string, object> data)
        {
            long seq;
            string prev;
            LastState(out seq, out prev);
            var e = new Dictionary<string, object>();
            e["n"] = seq + 1;
            e["fecha"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            e["equipo"] = Environment.MachineName;
            e["usuario"] = Environment.UserDomainName + "\\" + Environment.UserName;
            e["administrador"] = OsInfo.IsAdministrator();
            e["programa"] = AppInfo.Name + " " + AppInfo.Version;
            e["evento"] = evento ?? "";
            e["datos"] = data ?? new Dictionary<string, object>();
            e["anterior"] = prev;
            string body = Json.Serialize(e, false);
            string line = body.Substring(0, body.Length - 1) + ",\"mac\":\"" + Mac(key, body) + "\"}";
            string file = Path.Combine(dir, Prefix + DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");
            using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n");
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            return true;
        }

        static readonly Regex MacRx = new Regex(",\"mac\":\"(?<m>[0-9a-f]{64})\"}$", RegexOptions.Compiled);

        static string Mac(byte[] key, string body)
        {
            using (var h = new HMACSHA256(key))
            {
                var sb = new StringBuilder();
                foreach (byte b in h.ComputeHash(Encoding.UTF8.GetBytes(body))) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        string[] Files()
        {
            if (!System.IO.Directory.Exists(dir)) return new string[0];
            var files = System.IO.Directory.GetFiles(dir, Prefix + "*.jsonl");
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }

        /// <summary>Número y HMAC del último evento (o 0 e «inicio»).</summary>
        void LastState(out long seq, out string mac)
        {
            seq = 0; mac = "inicio";
            var files = Files();
            for (int i = files.Length - 1; i >= 0; i--)
            {
                string last = null;
                foreach (string l in File.ReadAllLines(files[i], Encoding.UTF8)) if (l.Trim().Length > 0) last = l;
                if (last == null) continue;
                var m = MacRx.Match(last);
                long n;
                if (m.Success && long.TryParse(Json.Str(Json.Parse(last), "n"), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { seq = n; mac = m.Groups["m"].Value; }
                return;
            }
        }

        /// <summary>
        /// La clave del HMAC: 32 bytes al azar, en un archivo de la carpeta del registro (que sólo pueden tocar los
        /// administradores). Se crea la primera vez.
        /// </summary>
        byte[] Key(bool create)
        {
            if (KeyForTests != null) return KeyForTests;
            string path = Path.Combine(dir, KeyFile);
            if (File.Exists(path))
            {
                byte[] k = File.ReadAllBytes(path);
                if (k.Length != KeyLength) throw new InvalidDataException("la clave del registro está dañada (" + path + ")");
                return k;
            }
            if (!create) throw new InvalidOperationException("no está la clave del registro (" + path + ")");
            var key = new byte[KeyLength];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(key, 0, key.Length);
                fs.Flush(true);
            }
            return key;
        }

        /// <summary>
        /// Registro de hasta la 2.0.10 (clave cifrada con DPAPI): se mueve entero, sin descifrar nada, a «anterior-AAAAMMDD-HHMMSS»
        /// y la cadena empieza de nuevo. Devuelve esa carpeta, o null si no había nada que guardar.
        /// </summary>
        string ArchiveLegacy()
        {
            string legacy = Path.Combine(dir, LegacyKeyFile);
            if (!File.Exists(legacy) || File.Exists(Path.Combine(dir, KeyFile))) return null;
            string target = Path.Combine(dir, "anterior-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            for (int i = 2; System.IO.Directory.Exists(target); i++) target = Path.Combine(dir, "anterior-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + i);
            System.IO.Directory.CreateDirectory(target);
            foreach (string f in Files()) File.Move(f, Path.Combine(target, Path.GetFileName(f)));
            File.Move(legacy, Path.Combine(target, LegacyKeyFile));
            Log.Info("Registro de auditoría: el de la versión anterior (clave con DPAPI) quedó guardado en " + target + "; empieza una cadena nueva.");
            return target;
        }

        // ------------------------------------------------------------------ Verificación

        public sealed class VerifyResult
        {
            public bool Ok;
            public int Entries;
            public string Problem;
            public override string ToString()
            {
                return Ok ? "Registro de auditoría íntegro: " + Entries + " eventos, la cadena está completa." : "EL REGISTRO DE AUDITORÍA FUE ALTERADO: " + Problem;
            }
        }

        /// <summary>
        /// Recorre todos los archivos en orden y comprueba la secuencia, la cadena y cada HMAC. Si encuentra una alteración,
        /// también la informa en el Visor de eventos (evento 1300) para que la vea el monitoreo de la empresa.
        /// </summary>
        public VerifyResult Verify()
        {
            var r = VerifyCore();
            if (!r.Ok) EventLogWriter.Write(EventLogWriter.Ids.AuditTampered, EventLogWriter.Level.Error, r.ToString() + " (" + dir + ")");
            return r;
        }

        VerifyResult VerifyCore()
        {
            var r = new VerifyResult();
            byte[] key;
            try { ArchiveLegacy(); key = Key(false); }
            catch (Exception ex)
            {
                r.Ok = Files().Length == 0;
                r.Problem = r.Ok ? null : "no se puede leer la clave: " + ex.Message;
                return r;
            }
            long expected = 1;
            string prev = "inicio";
            foreach (string f in Files())
            {
                int lineNo = 0;
                foreach (string raw in File.ReadAllLines(f, Encoding.UTF8))
                {
                    lineNo++;
                    if (raw.Trim().Length == 0) continue;
                    string where = Path.GetFileName(f) + ", línea " + lineNo;
                    var m = MacRx.Match(raw);
                    if (!m.Success) { r.Problem = where + ": la línea no tiene firma"; return r; }
                    string body = raw.Substring(0, m.Index) + "}";
                    if (Mac(key, body) != m.Groups["m"].Value) { r.Problem = where + ": el contenido no coincide con su firma (se modificó)"; return r; }
                    object o;
                    try { o = Json.Parse(body); } catch { r.Problem = where + ": la línea no es JSON"; return r; }
                    long n;
                    if (!long.TryParse(Json.Str(o, "n"), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n != expected)
                    { r.Problem = where + ": se esperaba el evento " + expected + " y está el " + Json.Str(o, "n") + " (faltan o sobran eventos)"; return r; }
                    if (Json.Str(o, "anterior") != prev) { r.Problem = where + ": no encadena con el evento anterior (se borró o se reordenó algo)"; return r; }
                    prev = m.Groups["m"].Value;
                    expected++;
                    r.Entries++;
                }
            }
            r.Ok = true;
            return r;
        }

        // ------------------------------------------------------------------ Atajos

        static AuditTrail shared;

        /// <summary>Anota en el registro de este proceso y, si <paramref name="eventId"/> &gt; 0, en el Visor de eventos.</summary>
        public static void Write(string evento, IDictionary<string, object> data, int eventId, EventLogWriter.Level level, string message)
        {
            try
            {
                AuditTrail t;
                lock (gate) t = shared ?? (shared = Open());
                t.Record(evento, data);
            }
            catch (Exception ex) { Actualizador.Core.Log.Warn("Registro de auditoría: " + ex.Message); }
            if (eventId > 0) EventLogWriter.Write(eventId, level, message ?? evento);
        }

        /// <summary>Sólo para las pruebas.</summary>
        internal static void ResetShared() { lock (gate) shared = null; }
    }
}
