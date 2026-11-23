// Actualizaciones que el usuario pidió no ofrecer.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Una regla: «no ofrecer esta versión» (Version con valor) o «no ofrecer nunca» (Version vacía).
    /// Se reconoce por el identificador estable de la fila (el Id de winget, el del fabricante, la familia de la
    /// app de la Store) y, si no tiene, por el nombre. Las reglas de la consola pueden dar un nombre O un
    /// identificador, sin tipo.
    /// </summary>
    public sealed class IgnoreRule
    {
        /// <summary>Tipo de la fila (Programa, Controlador, Store, WindowsUpdate) o vacío para cualquiera.</summary>
        public string Kind = "";
        public string Id = "";
        public string Name = "";
        /// <summary>Versión ofrecida que se ignora; vacía = todas las versiones.</summary>
        public string Version = "";
        public DateTime Added;

        public bool AllVersions { get { return string.IsNullOrEmpty(Version); } }

        public string Describe()
        {
            string who = !string.IsNullOrEmpty(Name) ? Name : Id;
            return "«" + who + "»" + (AllVersions ? " (todas las versiones)" : " (versión " + Version + ")");
        }
    }

    /// <summary>
    /// Lista de actualizaciones ignoradas, en %LOCALAPPDATA%\Actualizador\ignorados.json. Lo ignorado NO se
    /// esconde: la fila sigue en la lista con el estado «Ignorada» (se ve con «Mostrar también al día…»), no se
    /// marca, no se instala (tampoco con --aplicar) y figura en Hallazgos. Así, si otro programa del usuario
    /// escribiera en ese archivo para que no se actualice algo, igual queda a la vista (ver SEGURIDAD.md, S67).
    /// </summary>
    public sealed class IgnoreList
    {
        public readonly List<IgnoreRule> Rules = new List<IgnoreRule>();
        public const int MaxRules = 500;

        // 2.0.10: de cada equipo (en la portable, Datos\equipos\NOMBRE): ignorar algo en un equipo no lo esconde en los demás.
        public static string DefaultPath { get { return Path.Combine(AppInfo.MachineDataDirectory, "ignorados.json"); } }

        public static IgnoreList Load() { return Load(DefaultPath); }

        public static IgnoreList Load(string path)
        {
            var l = new IgnoreList();
            try
            {
                if (!File.Exists(path)) return l;
                object o = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                foreach (object x in Json.List(o, "ignorados"))
                {
                    var r = new IgnoreRule();
                    r.Kind = Clean(Json.Str(x, "tipo"), 20);
                    r.Id = Clean(Json.Str(x, "id"), 300);
                    r.Name = Clean(Json.Str(x, "nombre"), 300);
                    r.Version = Clean(Json.Str(x, "version"), 100);
                    DateTime d;
                    if (DateTime.TryParse(Json.Str(x, "fecha"), CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) r.Added = d;
                    if (r.Id.Length == 0 && r.Name.Length == 0) continue;
                    if (l.Rules.Count < MaxRules) l.Rules.Add(r);
                }
            }
            catch (Exception ex) { Log.Warn("No se pudo leer la lista de ignorados (" + path + "): " + ex.Message); }
            return l;
        }

        public bool Save() { return Save(DefaultPath); }

        public bool Save(string path)
        {
            try
            {
                var items = new List<object>();
                foreach (var r in Rules)
                {
                    var d = new Dictionary<string, object>();
                    d["tipo"] = r.Kind ?? "";
                    d["id"] = r.Id ?? "";
                    d["nombre"] = r.Name ?? "";
                    d["version"] = r.Version ?? "";
                    d["fecha"] = r.Added.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                    items.Add(d);
                }
                var root = new Dictionary<string, object>();
                root["ignorados"] = items;
                string dir = Path.GetDirectoryName(path);
                if (!SafePaths.EnsureDataDirectory(dir)) { Log.Warn("No se guarda la lista de ignorados: la carpeta " + dir + " contiene un enlace."); return false; }
                AtomicFile.WriteAllText(path, Json.Serialize(root, true), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) { Log.Warn("No se pudo guardar la lista de ignorados: " + ex.Message); return false; }
        }

        static string Clean(string s, int max)
        {
            s = (s ?? "").Trim();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsControl(c)) sb.Append(c);
            s = sb.ToString();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        /// <summary>Nombre para comparar: sin mayúsculas, espacios de más ni el « (usuario)» final.</summary>
        public static string NameKey(string name)
        {
            string n = (name ?? "").Trim();
            if (n.EndsWith(" (usuario)", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - " (usuario)".Length);
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c in n)
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>Identificador que sirve para reconocer la fila en otra búsqueda (no el «registro:» de una fila sin catálogo).</summary>
        public static string StableId(UpdateCandidate c)
        {
            if (c == null || string.IsNullOrEmpty(c.Identifier)) return "";
            if (c.Identifier == StoreProvider.AllAppsId) return c.Identifier;
            if (c.Identifier.StartsWith("registro:", StringComparison.OrdinalIgnoreCase)) return "";
            return c.Identifier;
        }

        public static bool Matches(IgnoreRule r, UpdateCandidate c)
        {
            if (r == null || c == null) return false;
            if (!string.IsNullOrEmpty(r.Kind) && !string.Equals(r.Kind, c.Kind.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
            bool who;
            string id = StableId(c);
            if (!string.IsNullOrEmpty(r.Id)) who = id.Length > 0 && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase);
            else
            {
                // Sin identificador (reglas de la consola o filas sin catálogo): el nombre, o el identificador escrito como nombre.
                string key = NameKey(r.Name);
                who = key.Length > 0 && (key == NameKey(c.Name) || (id.Length > 0 && string.Equals(r.Name.Trim(), id, StringComparison.OrdinalIgnoreCase)));
            }
            if (!who) return false;
            if (r.AllVersions) return true;
            return string.Equals((r.Version ?? "").Trim(), (c.AvailableVersion ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public IgnoreRule Find(UpdateCandidate c)
        {
            foreach (var r in Rules) if (Matches(r, c)) return r;
            return null;
        }

        /// <summary>¿Se puede ignorar esta fila? Lo que ofrece algo, lo pendiente y lo que no se pudo verificar.</summary>
        public static bool CanIgnore(UpdateCandidate c)
        {
            if (c == null || c.IgnoredBy != null) return false;
            switch (c.Status)
            {
                case UpdateStatus.Disponible:
                case UpdateStatus.Pendiente:
                case UpdateStatus.Error:
                case UpdateStatus.NoVerificable:
                case UpdateStatus.Fallida:
                    return true;
                default: return false;
            }
        }

        /// <summary>Regla para una fila: de esa versión ofrecida, o de todas.</summary>
        public static IgnoreRule RuleFor(UpdateCandidate c, bool allVersions, DateTime now)
        {
            var r = new IgnoreRule();
            r.Kind = c.Kind.ToString();
            r.Id = StableId(c);
            r.Name = (c.Name ?? "").Trim();
            r.Version = allVersions || string.IsNullOrEmpty(c.AvailableVersion) ? "" : c.AvailableVersion.Trim();
            r.Added = now;
            return r;
        }

        /// <summary>Agrega la regla (reemplaza una igual) y devuelve true si cambió algo.</summary>
        public bool Add(IgnoreRule rule)
        {
            if (rule == null || (string.IsNullOrEmpty(rule.Id) && string.IsNullOrEmpty(rule.Name))) return false;
            Rules.RemoveAll(delegate(IgnoreRule x)
            {
                return string.Equals(x.Kind, rule.Kind, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(x.Id, rule.Id, StringComparison.OrdinalIgnoreCase) &&
                       NameKey(x.Name) == NameKey(rule.Name) &&
                       (x.AllVersions || rule.AllVersions || string.Equals(x.Version, rule.Version, StringComparison.OrdinalIgnoreCase));
            });
            if (Rules.Count >= MaxRules) return false;
            Rules.Add(rule);
            return true;
        }

        /// <summary>Quita todas las reglas que cubren la fila. Devuelve cuántas quitó.</summary>
        public int RemoveFor(UpdateCandidate c)
        {
            return Rules.RemoveAll(delegate(IgnoreRule r) { return Matches(r, c); });
        }

        /// <summary>
        /// Quita las reglas que nombran ese texto (nombre o identificador; con «@versión», sólo esa versión).
        /// Para la consola: --dejar-de-ignorar.
        /// </summary>
        public int RemoveByText(string text)
        {
            string name, version;
            SplitText(text, out name, out version);
            string key = NameKey(name);
            return Rules.RemoveAll(delegate(IgnoreRule r)
            {
                bool who = NameKey(r.Name) == key || string.Equals(r.Id, name, StringComparison.OrdinalIgnoreCase);
                return who && (version.Length == 0 || string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));
            });
        }

        /// <summary>«Nombre o Id[@versión]» de la consola. El «@» que separa es el último.</summary>
        public static void SplitText(string text, out string name, out string version)
        {
            string t = (text ?? "").Trim();
            int at = t.LastIndexOf('@');
            if (at > 0 && at < t.Length - 1) { name = t.Substring(0, at).Trim(); version = t.Substring(at + 1).Trim(); }
            else { name = t.TrimEnd('@').Trim(); version = ""; }
        }

        public static IgnoreRule RuleFromText(string text, DateTime now)
        {
            string name, version;
            SplitText(text, out name, out version);
            if (name.Length == 0) return null;
            var r = new IgnoreRule();
            r.Name = name;
            r.Version = version;
            r.Added = now;
            return r;
        }

        /// <summary>
        /// Marca como «Ignorada» lo que cubre alguna regla (después de la búsqueda). Devuelve cuántas filas.
        /// También vuelve atrás las que ya no cubre ninguna (se quitó la regla).
        /// </summary>
        public int Apply(List<UpdateCandidate> all)
        {
            int n = 0;
            if (all == null) return 0;
            foreach (var c in all)
            {
                if (c == null) continue;
                var current = c.IgnoredBy as IgnoreRule;
                if (current != null)
                {
                    if (Rules.Contains(current)) { n++; continue; }
                    var other = Find(c);
                    if (other != null) { c.IgnoredBy = other; n++; continue; }
                    Unignore(c);
                    continue;
                }
                if (!CanIgnore(c)) continue;
                var r = Find(c);
                if (r == null) continue;
                Ignore(c, r);
                n++;
            }
            return n;
        }

        static void Ignore(UpdateCandidate c, IgnoreRule r)
        {
            c.IgnoredBy = r;
            c.StatusBeforeIgnore = c.Status;
            c.RecommendedBeforeIgnore = c.Recommended;
            c.ResultBeforeIgnore = c.Result;
            c.Status = UpdateStatus.Ignorada;
            c.Recommended = false;
            c.Selected = false;
            c.Result = "Ignorada " + (r.AllVersions ? "(todas las versiones)" : "(la " + r.Version + ")") +
                       (r.Added > DateTime.MinValue ? " desde el " + Fmt.Date(r.Added.Date) : "") + ". Clic derecho › «Dejar de ignorar» para volver a ofrecerla.";
        }

        public static void Unignore(UpdateCandidate c)
        {
            // Sólo lo que ignoró el usuario: lo que bloquea la política de la empresa no se destraba desde acá.
            if (c == null || !(c.IgnoredBy is IgnoreRule)) return;
            c.Status = c.StatusBeforeIgnore;
            c.Recommended = c.RecommendedBeforeIgnore;
            c.IgnoredBy = null;
            c.Result = c.ResultBeforeIgnore ?? "";
        }
    }
}
