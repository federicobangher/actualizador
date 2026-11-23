// Respuestas de consultas lentas guardadas en disco por unas horas: la segunda búsqueda del día no repite lo mismo.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Un archivo JSON por tipo de consulta en %LOCALAPPDATA%\Actualizador\cache (clave → texto y hora). Sirve para lo que
    /// cambia poco y tarda: las búsquedas por nombre en winget (una por programa que winget no reconoce) y las del Catálogo de
    /// Microsoft Update (una por controlador). Dentro de «Actualizar todo», que vuelve a buscar después de instalar, evita
    /// repetir cientos de consultas. Es sólo un atajo: si falta, está roto o es viejo, se consulta de nuevo.
    /// </summary>
    public sealed class QueryCache
    {
        /// <summary>Sólo para las pruebas: carpeta propia (null: la de siempre).</summary>
        internal static string DirectoryForTests;

        /// <summary>2.0.16: lo único que no es una consulta guardada: los controladores que Windows no aplicó en este equipo.</summary>
        public const string MachineStateName = "controladores-no-aplicados";

        /// <summary>
        /// 2.0.28: el archivo del equipo lo escriben varios (controladores no aplicados, pendientes de reinicio, fallas para todos los
        /// usuarios): cada uno lo lee, agrega y guarda. Con este candado, dos a la vez no se pisan.
        /// </summary>
        public static readonly object MachineGate = new object();

        public const int MaxEntries = 3000;
        public const int MaxValue = 256 * 1024;

        readonly string path;
        readonly TimeSpan life;
        readonly Dictionary<string, KeyValuePair<DateTime, string>> items = new Dictionary<string, KeyValuePair<DateTime, string>>(StringComparer.OrdinalIgnoreCase);
        bool dirty;

        public static string Directory { get { return DirectoryForTests ?? Path.Combine(AppInfo.DataDirectory, "cache"); } }

        /// <summary>
        /// 2.0.16: lo que se recuerda de ESTE equipo (no respuestas de un catálogo): va con los datos del equipo, fuera de «cache».
        /// En la portable, que pasa por muchos equipos, es Datos\equipos\NOMBRE; instalada, la carpeta de datos de siempre.
        /// </summary>
        public static string MachineDirectory { get { return DirectoryForTests != null ? Path.Combine(DirectoryForTests, "equipo") : AppInfo.MachineDataDirectory; } }

        readonly string dataRoot;

        QueryCache(string directory, string name, TimeSpan life, string dataRoot)
        {
            this.path = Path.Combine(directory, name + ".json");
            this.life = life;
            this.dataRoot = dataRoot;
        }

        /// <summary>Lee el archivo (sin errores: si no se puede, empieza vacío).</summary>
        public static QueryCache Open(string name, TimeSpan life)
        {
            var c = new QueryCache(Directory, name, life, AppInfo.DataDirectory);
            c.Read(c.path, name);
            return c;
        }

        /// <summary>
        /// 2.0.16: como <see cref="Open"/>, pero guardado con los datos de este equipo (<see cref="MachineDirectory"/>) y no entre las
        /// consultas guardadas: «Olvidar las consultas guardadas» no lo borra y la versión portable no lo lleva de un equipo a otro.
        /// La versión instalada toma lo que la 2.0.15 había dejado en «cache» (misma carpeta de datos, mismo equipo).
        /// </summary>
        public static QueryCache OpenMachine(string name, TimeSpan life)
        {
            var c = new QueryCache(MachineDirectory, name, life, DirectoryForTests != null ? null : AppInfo.MachineDataDirectory);
            if (!c.Read(c.path, name) && (DirectoryForTests != null || !AppInfo.IsPortable))
            {
                // Lo que dejó la versión anterior: se lee una vez y queda marcado para guardarse en el lugar nuevo.
                if (c.Read(Path.Combine(Directory, name + ".json"), name) && c.items.Count > 0) c.dirty = true;
            }
            return c;
        }

        /// <summary>Carga un archivo de respuestas. false si no existe o no se pudo leer.</summary>
        bool Read(string file, string name)
        {
            var c = this;
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > 32L * 1024 * 1024) return false;
                object root = Json.Parse(File.ReadAllText(file, Encoding.UTF8));
                foreach (object x in Json.List(root, "items"))
                {
                    string k = Json.Str(x, "k"), v = Json.Str(x, "v");
                    long t;
                    if (string.IsNullOrEmpty(k) || v == null || !long.TryParse(Json.Str(x, "t"), out t)) continue;
                    DateTime at;
                    try { at = new DateTime(t, DateTimeKind.Utc); } catch { continue; }
                    if (c.Fresh(at)) c.items[k] = new KeyValuePair<DateTime, string>(at, v);
                }
                return true;
            }
            catch (Exception ex) { Log.Info("Caché " + name + ": " + ex.Message + " (se empieza vacía)"); c.items.Clear(); return false; }
        }

        bool Fresh(DateTime at) { var age = DateTime.UtcNow - at; return age >= TimeSpan.Zero && age < life; }

        public int Count { get { lock (items) return items.Count; } }

        public bool TryGet(string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key)) return false;
            lock (items)
            {
                KeyValuePair<DateTime, string> e;
                if (!items.TryGetValue(key, out e) || !Fresh(e.Key)) return false;
                value = e.Value;
                return true;
            }
        }

        public void Put(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || value == null || value.Length > MaxValue) return;
            lock (items)
            {
                items[key] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, value);
                dirty = true;
            }
        }

        /// <summary>Guarda si cambió (las más nuevas primero, hasta <see cref="MaxEntries"/>).</summary>
        public void Save()
        {
            List<KeyValuePair<string, KeyValuePair<DateTime, string>>> list;
            lock (items)
            {
                if (!dirty) return;
                list = new List<KeyValuePair<string, KeyValuePair<DateTime, string>>>(items);
                dirty = false;
            }
            try
            {
                list.RemoveAll(delegate(KeyValuePair<string, KeyValuePair<DateTime, string>> e) { return !Fresh(e.Value.Key); });
                list.Sort(delegate(KeyValuePair<string, KeyValuePair<DateTime, string>> a, KeyValuePair<string, KeyValuePair<DateTime, string>> b) { return b.Value.Key.CompareTo(a.Value.Key); });
                if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
                var arr = new List<object>();
                foreach (var e in list)
                {
                    var d = new Dictionary<string, object>();
                    d["k"] = e.Key;
                    d["t"] = e.Value.Key.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    d["v"] = e.Value.Value;
                    arr.Add(d);
                }
                var root = new Dictionary<string, object>();
                root["items"] = arr;
                if (dataRoot != null && DirectoryForTests == null && !SafePaths.EnsureDataDirectory(dataRoot)) return;
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
                AtomicFile.WriteAllText(path, Json.Serialize(root, false), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Info("Caché: no se pudo guardar " + path + ": " + ex.Message); }
        }

        /// <summary>
        /// Borra todas las respuestas guardadas (Opciones › «Olvidar las consultas guardadas»). 2.0.16: no lo que se recuerda del
        /// equipo (<see cref="OpenMachine"/>): si se olvidaba qué controladores Windows no había aplicado, volvían a ofrecerse.
        /// </summary>
        public static int Clear()
        {
            int n = 0;
            try
            {
                if (!System.IO.Directory.Exists(Directory)) return 0;
                foreach (string f in System.IO.Directory.GetFiles(Directory, "*.json"))
                {
                    try
                    {
                        // Lo que la 2.0.15 dejó acá y es del equipo se pasa a su lugar antes de borrar (sólo la instalada).
                        if (Path.GetFileName(f).Equals(MachineStateName + ".json", StringComparison.OrdinalIgnoreCase) && (DirectoryForTests != null || !AppInfo.IsPortable))
                        {
                            string target = Path.Combine(MachineDirectory, MachineStateName + ".json");
                            if (!File.Exists(target)) { System.IO.Directory.CreateDirectory(MachineDirectory); File.Move(f, target); continue; }
                        }
                        File.Delete(f); n++;
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log.Info("Caché: " + ex.Message); }
            return n;
        }
    }
}
