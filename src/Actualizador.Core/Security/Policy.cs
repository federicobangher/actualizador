// Política de la empresa: qué fuentes se consultan, qué no se instala nunca y qué editores están aprobados.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Actualizador.Core.Security
{
    /// <summary>
    /// La política la define el área de sistemas y el usuario no la puede cambiar desde Actualizador. Se lee de:
    /// <list type="number">
    /// <item>Directiva de grupo / Intune: HKLM\SOFTWARE\Policies\Actualizador (sólo administradores pueden escribir ahí).</item>
    /// <item>%ProgramData%\Actualizador\politica.json, sólo si el archivo y su carpeta únicamente los pueden modificar
    /// administradores (si no, se ignora y se avisa: un usuario común no puede «aflojar» la política).</item>
    /// </list>
    /// Lo de la directiva de grupo manda sobre el archivo. Sin ninguna de las dos, todo está permitido (uso personal).
    /// La referencia completa está en docs/POLITICAS.md.
    /// </summary>
    public sealed class Policy
    {
        public const string RegistryPath = @"SOFTWARE\Policies\Actualizador";

        /// <summary>2.0.11: la clave de cuando se llamaba SuperProgram; vale si la nueva no está (las directivas ya desplegadas siguen andando).</summary>
        public const string LegacyRegistryPath = @"SOFTWARE\Policies\" + AppInfo.LegacyName;
        public const string FileName = "politica.json";

        /// <summary>Fuentes que se pueden apagar (los identificadores coinciden con los de los proveedores del motor).</summary>
        public static readonly string[] SourceIds = { "winget", "navegadores", "fabricante", "github", "sitio", "windowsupdate", "catalogo", "fabricante_equipo", "store", "externo" };

        /// <summary>De dónde salió: «directiva de grupo», «archivo», «directiva de grupo y archivo» o «ninguna».</summary>
        public string Origin = "ninguna";
        public readonly HashSet<string> DisabledSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Patrones (con * y ?) de nombre o identificador que nunca se instalan.</summary>
        public readonly List<string> Blocked = new List<string>();
        /// <summary>Si tiene algo, sólo se instalan programas de esos editores (patrones con * y ?).</summary>
        public readonly List<string> AllowedPublishers = new List<string>();
        public bool AllowOpenPages = true;
        /// <summary>
        /// 2.0.17: instalar desde la página oficial un instalador SIN firma digital cuando lo instalado tampoco la tiene (el autor no
        /// firma), es del mismo producto y de la versión publicada. Apagado, esos programas abren su página como antes.
        /// </summary>
        public bool AllowUnsignedSiteInstallers = true;
        public bool AllowForcedDrivers = true;
        public bool AllowInstallWinget = true;
        public bool AllowInteractiveInstallers = true;
        public bool RequireRestorePoint;
        public bool RequireAdministrator;
        /// <summary>El usuario no puede apagar fuentes ni cambiar el catálogo de fuentes desde Opciones.</summary>
        public bool LockSettings;
        public string SourcesCatalogUrl;
        /// <summary>Problemas al leer la política (se muestran en «Detalles de la búsqueda» y van al Visor de eventos).</summary>
        public readonly List<string> Problems = new List<string>();

        /// <summary>No restringe nada (sin política o con una que deja todo como viene de fábrica).</summary>
        public bool IsEmpty
        {
            get
            {
                return DisabledSources.Count == 0 && Blocked.Count == 0 && AllowedPublishers.Count == 0 && AllowOpenPages && AllowUnsignedSiteInstallers && AllowForcedDrivers &&
                       AllowInstallWinget && AllowInteractiveInstallers && !RequireRestorePoint && !RequireAdministrator && !LockSettings &&
                       string.IsNullOrEmpty(SourcesCatalogUrl);
            }
        }

        // ------------------------------------------------------------------ Lectura

        /// <summary>Sólo para las pruebas: reemplaza las rutas y el registro.</summary>
        internal static string FileForTests;
        internal static Func<string, object> RegistryForTests;

        static readonly object gate = new object();
        static Policy current;
        static DateTime loadedAt;

        public static string MachineDirectory
        {
            get
            {
                string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (string.IsNullOrEmpty(pd)) pd = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "ProgramData");
                return Path.Combine(pd, AppInfo.Name);
            }
        }

        public static string FilePath { get { return FileForTests ?? Path.Combine(MachineDirectory, FileName); } }

        /// <summary>La política vigente (se relee cada minuto como mucho).</summary>
        public static Policy Current
        {
            get
            {
                lock (gate)
                {
                    if (current == null || DateTime.UtcNow - loadedAt > TimeSpan.FromMinutes(1)) { current = Load(); loadedAt = DateTime.UtcNow; }
                    return current;
                }
            }
        }

        /// <summary>Fuerza la relectura (después de cambiarla, y en las pruebas).</summary>
        public static void Reset() { lock (gate) current = null; }

        public static Policy Load()
        {
            var p = new Policy();
            bool fromFile = false, fromRegistry = false;
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    if (FileForTests == null && !AclGuard.IsAdminOnlyWritable(path))
                        p.Problems.Add("Se ignoró " + path + ": un usuario sin privilegios puede modificarlo. Debe ser modificable sólo por Administradores y SYSTEM.");
                    else if (new FileInfo(path).Length > 1024 * 1024)
                        p.Problems.Add("Se ignoró " + path + ": es demasiado grande.");
                    else
                    {
                        p.ApplyJson(File.ReadAllText(path, Encoding.UTF8));
                        fromFile = true;
                    }
                }
            }
            catch (Exception ex) { p.Problems.Add("No se pudo leer " + FilePath + ": " + ex.Message); }
            try { fromRegistry = p.ApplyRegistry(); }
            catch (Exception ex) { p.Problems.Add("No se pudo leer la directiva de grupo: " + ex.Message); }
            p.Origin = fromRegistry && fromFile ? "directiva de grupo y archivo" : fromRegistry ? "directiva de grupo" : fromFile ? "archivo" : "ninguna";
            foreach (string pr in p.Problems) Log.Warn("Política: " + pr);
            ReportChange(p);
            return p;
        }

        static string lastReported;

        /// <summary>Al Visor de eventos sólo cuando la política cambia (se relee cada minuto).</summary>
        static void ReportChange(Policy p)
        {
            string text = p.Describe();
            lock (gate)
            {
                if (text == lastReported) return;
                bool first = lastReported == null;
                lastReported = text;
                if (first && p.IsEmpty && p.Problems.Count == 0) return;
            }
            if (p.Problems.Count > 0) EventLogWriter.Write(EventLogWriter.Ids.PolicyRejected, EventLogWriter.Level.Warning, text);
            else EventLogWriter.Write(EventLogWriter.Ids.PolicyLoaded, EventLogWriter.Level.Information, text);
        }

        /// <summary>Aplica un JSON de política (ver docs/POLITICAS.md). Lo desconocido o mal escrito se informa y se ignora.</summary>
        public void ApplyJson(string json)
        {
            object root;
            try { root = Json.Parse(json ?? ""); }
            catch (Exception ex) { Problems.Add("politica.json no es JSON válido: " + ex.Message); return; }
            object fuentes = Json.Path(root, "fuentes");
            if (fuentes is IDictionary<string, object>)
                foreach (var kv in (IDictionary<string, object>)fuentes)
                {
                    if (Array.IndexOf(SourceIds, kv.Key.ToLowerInvariant()) < 0) { Problems.Add("Fuente desconocida en la política: «" + kv.Key + "»."); continue; }
                    if (kv.Value is bool && !(bool)kv.Value) DisabledSources.Add(kv.Key.ToLowerInvariant());
                }
            foreach (object x in Json.List(root, "bloquear")) AddPattern(Blocked, x as string, "bloquear");
            foreach (object x in Json.List(root, "editoresPermitidos")) AddPattern(AllowedPublishers, x as string, "editoresPermitidos");
            AllowOpenPages = Bool(root, "permitirAbrirPaginas", AllowOpenPages);
            AllowUnsignedSiteInstallers = Bool(root, "permitirInstaladoresSinFirma", AllowUnsignedSiteInstallers);
            AllowForcedDrivers = Bool(root, "permitirForzarControladores", AllowForcedDrivers);
            AllowInstallWinget = Bool(root, "permitirInstalarWinget", AllowInstallWinget);
            AllowInteractiveInstallers = Bool(root, "permitirInstaladoresConVentana", AllowInteractiveInstallers);
            RequireRestorePoint = Bool(root, "exigirPuntoDeRestauracion", RequireRestorePoint);
            RequireAdministrator = Bool(root, "exigirAdministrador", RequireAdministrator);
            LockSettings = Bool(root, "bloquearOpciones", LockSettings);
            string url = Json.Str(root, "catalogoFuentesUrl");
            if (!string.IsNullOrEmpty(url)) SetCatalogUrl(url);
        }

        static bool Bool(object root, string key, bool fallback)
        {
            string v = Json.Str(root, key);
            if (v == "true") return true;
            if (v == "false") return false;
            return fallback;
        }

        void SetCatalogUrl(string url)
        {
            Uri u;
            if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out u) && u.Scheme == Uri.UriSchemeHttps) SourcesCatalogUrl = u.AbsoluteUri;
            else Problems.Add("catalogoFuentesUrl tiene que ser una dirección https://");
        }

        void AddPattern(List<string> list, string pattern, string field)
        {
            string p = (pattern ?? "").Trim();
            if (p.Length == 0) return;
            if (p.Length > 200) { Problems.Add(field + ": patrón demasiado largo"); return; }
            if (!list.Contains(p)) list.Add(p);
        }

        /// <summary>
        /// HKLM\SOFTWARE\Policies\Actualizador: DWORD por cada fuente («Fuente_winget» = 0 la apaga) y por cada permiso, y
        /// REG_MULTI_SZ «Bloquear» y «EditoresPermitidos». true si encontró la clave.
        /// </summary>
        bool ApplyRegistry()
        {
            Func<string, object> get = RegistryForTests;
            RegistryKey key = null;
            if (get == null)
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
                key = Reg.Open(RegistryHive.LocalMachine, RegistryPath, Reg.Native) ?? Reg.Open(RegistryHive.LocalMachine, LegacyRegistryPath, Reg.Native);
                if (key == null) return false;
                var k = key;
                get = delegate(string name) { try { return k.GetValue(name); } catch { return null; } };
            }
            try
            {
                bool any = false;
                foreach (string id in SourceIds)
                {
                    object v = get("Fuente_" + id);
                    if (v is int) { any = true; if ((int)v == 0) DisabledSources.Add(id); else DisabledSources.Remove(id); }
                }
                any |= RegBool(get, "PermitirAbrirPaginas", ref AllowOpenPages);
                any |= RegBool(get, "PermitirInstaladoresSinFirma", ref AllowUnsignedSiteInstallers);
                any |= RegBool(get, "PermitirForzarControladores", ref AllowForcedDrivers);
                any |= RegBool(get, "PermitirInstalarWinget", ref AllowInstallWinget);
                any |= RegBool(get, "PermitirInstaladoresConVentana", ref AllowInteractiveInstallers);
                any |= RegBool(get, "ExigirPuntoDeRestauracion", ref RequireRestorePoint);
                any |= RegBool(get, "ExigirAdministrador", ref RequireAdministrator);
                any |= RegBool(get, "BloquearOpciones", ref LockSettings);
                var blocked = get("Bloquear") as string[];
                if (blocked != null) { any = true; Blocked.Clear(); foreach (string b in blocked) AddPattern(Blocked, b, "Bloquear"); }
                var pubs = get("EditoresPermitidos") as string[];
                if (pubs != null) { any = true; AllowedPublishers.Clear(); foreach (string b in pubs) AddPattern(AllowedPublishers, b, "EditoresPermitidos"); }
                var url = get("CatalogoFuentesUrl") as string;
                if (!string.IsNullOrEmpty(url)) { any = true; SetCatalogUrl(url); }
                return any || key != null;
            }
            finally { if (key != null) key.Dispose(); }
        }

        static bool RegBool(Func<string, object> get, string name, ref bool field)
        {
            object v = get(name);
            if (!(v is int)) return false;
            field = (int)v != 0;
            return true;
        }

        // ------------------------------------------------------------------ Decisiones

        public bool IsSourceAllowed(string providerId)
        {
            return string.IsNullOrEmpty(providerId) || !DisabledSources.Contains(providerId);
        }

        /// <summary>
        /// Por qué la política no deja instalar esta actualización, o null. <paramref name="isProgram"/>: los editores aprobados
        /// se exigen a los programas (los controladores, Windows Update y la Store vienen firmados por Microsoft).
        /// </summary>
        public string BlockReason(string name, string identifier, string publisher, bool isProgram)
        {
            foreach (string p in Blocked)
                if (Matches(p, name) || Matches(p, identifier)) return "bloqueada por la política de la empresa («" + p + "»)";
            if (isProgram && AllowedPublishers.Count > 0)
            {
                foreach (string p in AllowedPublishers) if (Matches(p, publisher)) return null;
                return "el editor «" + (string.IsNullOrEmpty(publisher) ? "desconocido" : publisher) + "» no está entre los aprobados por la empresa";
            }
            return null;
        }

        /// <summary>Patrón con * y ?, sin distinguir mayúsculas, contra el texto entero.</summary>
        public static bool Matches(string pattern, string text)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(text)) return false;
            string rx = "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            return Regex.IsMatch(text.Trim(), rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }

        /// <summary>Resumen para mostrar (Opciones, «Detalles de la búsqueda», Visor de eventos).</summary>
        public string Describe()
        {
            string problems = Problems.Count == 0 ? "" : "\r\nProblemas: " + string.Join(" ", Problems.ToArray());
            if (IsEmpty) return (Origin == "ninguna" ? "Sin política de la empresa: todo permitido." : "Política de la empresa (" + Origin + "): sin restricciones.") + problems;
            var parts = new List<string>();
            if (DisabledSources.Count > 0) parts.Add("fuentes apagadas: " + string.Join(", ", new List<string>(DisabledSources).ToArray()));
            if (Blocked.Count > 0) parts.Add(Blocked.Count + " bloqueos");
            if (AllowedPublishers.Count > 0) parts.Add("sólo " + AllowedPublishers.Count + " editores aprobados");
            if (!AllowOpenPages) parts.Add("sin abrir páginas de descarga");
            if (!AllowUnsignedSiteInstallers) parts.Add("sin instaladores sin firma digital");
            if (!AllowForcedDrivers) parts.Add("sin controladores forzados");
            if (!AllowInstallWinget) parts.Add("sin instalar winget");
            if (!AllowInteractiveInstallers) parts.Add("sin instaladores con ventana");
            if (RequireRestorePoint) parts.Add("punto de restauración obligatorio");
            if (RequireAdministrator) parts.Add("sólo administradores instalan");
            if (LockSettings) parts.Add("opciones bloqueadas");
            if (!string.IsNullOrEmpty(SourcesCatalogUrl)) parts.Add("catálogo de fuentes: " + SourcesCatalogUrl);
            return "Política de la empresa (" + Origin + "): " + string.Join("; ", parts.ToArray()) + "." + problems;
        }
    }
}
