using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Actualizador.Core
{
    /// <summary>Consultas WMI con timeout y lectura segura de propiedades.</summary>
    public static class Wmi
    {
        public const string Cimv2 = @"root\cimv2";

        public static List<WmiObject> Query(string wql)
        {
            return Query(Cimv2, wql, 60);
        }

        /// <summary>
        /// Consulta con plazo REAL. El plazo de System.Management se aplica a cada objeto que devuelve la
        /// enumeración, pero no a la conexión ni a un proveedor que se queda pensando: con el repositorio de WMI dañado o
        /// un proveedor colgado (pasa en algunos equipos), la consulta no volvía nunca y la auditoría, la búsqueda de
        /// actualizaciones o una instalación quedaban «trabadas». Ahora la consulta corre en un hilo aparte y, si no termina
        /// en <see cref="HardLimitMs"/>, se la abandona con <see cref="TimeoutException"/>. Si un espacio de nombres
        /// se cuelga dos veces seguidas, durante unos minutos no se lo vuelve a consultar (cada consulta colgada deja un
        /// hilo esperando).
        /// </summary>
        public static List<WmiObject> Query(string ns, string wql, int timeoutSeconds = 60)
        {
            string open = BreakerOpen(ns);
            if (open != null) throw new TimeoutException(open);
            List<WmiObject> result = null;
            Exception error = null;
            var worker = new System.Threading.Thread(delegate()
            {
                try { result = QueryCore(ns, wql, timeoutSeconds); }
                catch (Exception ex) { error = ex; }
            });
            worker.IsBackground = true;
            worker.Name = "wmi";
            worker.Start();
            int limit = HardLimitMs(timeoutSeconds);
            if (!worker.Join(limit))
            {
                Trip(ns);
                Log.Warn("WMI no respondió en " + (limit / 1000) + " s (" + ns + "): " + Fmt.Truncate(wql, 120) + ". Se sigue sin esos datos.");
                throw new TimeoutException("WMI no respondió en " + (limit / 1000) + " s: el servicio Instrumental de administración de Windows (WMI) de este equipo está colgado o muy lento");
            }
            Reset(ns);
            if (error != null) throw error;
            return result;
        }

        /// <summary>Plazo total de una consulta: el doble del de cada objeto más medio minuto (60 s → 150 s).</summary>
        public static int HardLimitMs(int timeoutSeconds)
        {
            int s = Math.Max(5, timeoutSeconds);
            return (int)Math.Min(int.MaxValue, (long)s * 2000 + 30000);
        }

        /// <summary>Consultas colgadas seguidas por espacio de nombres y hasta cuándo no se lo consulta.</summary>
        static readonly Dictionary<string, KeyValuePair<int, DateTime>> hung = new Dictionary<string, KeyValuePair<int, DateTime>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Minutos sin consultar un espacio de nombres que se colgó dos veces seguidas.</summary>
        public static int BreakerMinutes = 10;

        internal static string BreakerOpen(string ns)
        {
            lock (hung)
            {
                KeyValuePair<int, DateTime> h;
                if (!hung.TryGetValue(ns ?? "", out h) || h.Key < 2) return null;
                if (DateTime.UtcNow >= h.Value) { hung.Remove(ns ?? ""); return null; }
                return "WMI no responde en este equipo (" + ns + "): se omitió la consulta para no demorar el resto";
            }
        }

        internal static void Trip(string ns)
        {
            lock (hung)
            {
                KeyValuePair<int, DateTime> h;
                hung.TryGetValue(ns ?? "", out h);
                hung[ns ?? ""] = new KeyValuePair<int, DateTime>(h.Key + 1, DateTime.UtcNow.AddMinutes(BreakerMinutes));
            }
        }

        internal static void Reset(string ns)
        {
            lock (hung) hung.Remove(ns ?? "");
        }

        /// <summary>
        /// Parámetro opcional omitido en una llamada COM tardía (IDispatch): VT_ERROR/DISP_E_PARAMNOTFOUND, lo mismo que
        /// deja VBScript con «ConnectServer(".", ns, , , , , 128)». 2.0.20: antes se pasaba <c>null</c>, que llega como
        /// VT_EMPTY; WMI no lo puede convertir a los parámetros de tipo objeto (objWbemNamedValueSet) y TODAS las consultas
        /// fallaban con 0x80020005 (DISP_E_TYPEMISMATCH, «Los tipos no coinciden»): los controladores no se revisaban nunca
        /// (32 veces en los registros de un equipo real, ni una fila de controladores en su historial).
        /// </summary>
        public static readonly object Omitted = Missing.Value;

        /// <summary>
        /// La consulta por el servicio de scripting de WMI (WbemScripting.SWbemLocator, COM de Windows). Así no hace falta
        /// System.Management, que en .NET 8 es un paquete aparte: el programa usa sólo lo que trae Windows.
        /// </summary>
        static List<WmiObject> QueryCore(string ns, string wql, int timeoutSeconds)
        {
            if (QueryForTests != null) return QueryForTests(ns, wql);
            object locator = Com.Create("WbemScripting.SWbemLocator");
            try
            {
                // ConnectServer(servidor, espacio, usuario, clave, locale, autoridad, flags=wbemConnectFlagUseMaxWait 128); lo demás, omitido.
                object services = Com.Call(locator, "ConnectServer", ".", ns, Omitted, Omitted, Omitted, Omitted, 128);
                object security = Com.TryGet(services, "Security_");
                if (security != null)
                {
                    try { Com.Set(security, "ImpersonationLevel", 3); } catch { }       // wbemImpersonationLevelImpersonate
                    try
                    {
                        object privs = Com.Get(security, "Privileges");
                        Com.Call(privs, "AddAsString", "SeBackupPrivilege", true);
                    }
                    catch { }
                }
                // Semisincrónica y rebobinable (flags 16 = wbemFlagReturnImmediately): Count + ItemIndex funcionan.
                object set = Com.Call(services, "ExecQuery", wql, "WQL", 16);
                var list = new List<WmiObject>();
                foreach (object o in Com.Enumerate(set)) list.Add(new WmiObject(o));
                return list;
            }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        /// <summary>Sólo para las pruebas: reemplaza la consulta por COM (espacio, WQL → filas; una excepción simula la falla).</summary>
        public static Func<string, string, List<WmiObject>> QueryForTests;

        /// <summary>Sólo para las pruebas: reemplaza la consulta por PowerShell (espacio, WQL → salida JSON).</summary>
        public static Func<string, string, string> PowerShellForTests;

        /// <summary>
        /// 2.0.20: si WMI por COM falla (no por tiempo ni porque la clase no exista), la misma consulta con Get-CimInstance de
        /// PowerShell, que usa otra vía (WinRM/DCOM de CIM). Devuelve null si tampoco se pudo (con el motivo).
        /// </summary>
        internal static List<WmiObject> QueryViaPowerShell(string ns, string wql, int timeoutSeconds, out string error)
        {
            error = null;
            List<string> props;
            if (!TryParseSelect(wql, out props)) { error = "consulta no admitida por la vía de PowerShell"; return null; }
            string script = PowerShellScript(ns, wql, props);
            string json;
            if (PowerShellForTests != null) json = PowerShellForTests(ns, wql);
            else
            {
                var r = ProcessRunner.PowerShell(script, Math.Max(30, timeoutSeconds) * 1000 + 30000);
                if (!r.Ok || r.Out.Trim().Length == 0)
                {
                    error = r.Started && !r.TimedOut && r.Err.Trim().Length > 0 ? Fmt.Truncate(r.Err.Trim(), 200) : r.Describe();
                    return null;
                }
                json = r.Out;
            }
            return ParsePowerShellRows(json, props, out error);
        }

        static readonly System.Text.RegularExpressions.Regex SelectPattern = new System.Text.RegularExpressions.Regex(
            @"^\s*SELECT\s+(?<p>[A-Za-z_][A-Za-z0-9_]*(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*)*)\s+FROM\s+[A-Za-z_][A-Za-z0-9_]*\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Las propiedades de «SELECT a, b FROM Clase …» (no «SELECT *»). Pública para las pruebas.</summary>
        public static bool TryParseSelect(string wql, out List<string> props)
        {
            props = new List<string>();
            var m = SelectPattern.Match(wql ?? "");
            if (!m.Success) return false;
            foreach (string p in m.Groups["p"].Value.Split(',')) props.Add(p.Trim());
            return props.Count > 0;
        }

        /// <summary>
        /// El guion de PowerShell: la consulta tal cual (entre comillas simples de PowerShell) y sólo las propiedades pedidas; las
        /// fechas en el formato de WMI («yyyyMMddHHmmss.ffffff+000», UTC) para que se lean igual que por COM. Pública para las pruebas.
        /// </summary>
        public static string PowerShellScript(string ns, string wql, List<string> props)
        {
            var quoted = new List<string>();
            foreach (string p in props) quoted.Add("'" + p + "'");
            return "$p=@(" + string.Join(",", quoted.ToArray()) + ");" +
                   "$r=@(Get-CimInstance -Namespace " + PsSingle(ns) + " -Query " + PsSingle(wql) + " -OperationTimeoutSec 120 -ErrorAction Stop | ForEach-Object { " +
                   "$o=$_;$h=[ordered]@{};foreach($n in $p){$v=$o.$n;if($v -is [datetime]){$v=$v.ToUniversalTime().ToString('yyyyMMddHHmmss.ffffff')+'+000'};$h[$n]=$v};[pscustomobject]$h });" +
                   "if($r.Count -eq 0){'[]'}else{ConvertTo-Json -InputObject $r -Compress -Depth 3}";
        }

        static string PsSingle(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }

        /// <summary>Filas de la salida JSON de <see cref="PowerShellScript"/> (los arreglos JSON quedan como arreglos, como por COM). Pública para las pruebas.</summary>
        public static List<WmiObject> ParsePowerShellRows(string output, List<string> props, out string error)
        {
            error = null;
            string text = (output ?? "").Trim();
            int a = text.IndexOf('['), o = text.IndexOf('{');
            int start = a < 0 ? o : (o < 0 ? a : Math.Min(a, o));
            if (start < 0) { error = "PowerShell no devolvió datos"; return null; }
            object root;
            try { root = Json.Parse(text.Substring(start)); }
            catch (Exception ex) { error = "salida JSON inválida: " + ex.Message; return null; }
            var items = root as List<object>;
            if (items == null) { items = new List<object>(); if (root != null) items.Add(root); }
            var list = new List<WmiObject>();
            foreach (object it in items)
            {
                var d = it as Dictionary<string, object>;
                if (d == null) continue;
                var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in d)
                {
                    var arr = kv.Value as List<object>;
                    values[kv.Key] = arr != null ? (object)arr.ToArray() : kv.Value;
                }
                list.Add(new WmiObject(values));
            }
            return list;
        }

        /// <summary>Ejecuta un método estático de una clase WMI (p. ej. SystemRestore.CreateRestorePoint) y devuelve ReturnValue.</summary>
        public static object CallStatic(string ns, string className, string method, IDictionary<string, object> args)
        {
            object locator = Com.Create("WbemScripting.SWbemLocator");
            object services = Com.Call(locator, "ConnectServer", ".", ns, Omitted, Omitted, Omitted, Omitted, 128);
            object cls = Com.Call(services, "Get", className, 0);
            object methods = Com.Get(cls, "Methods_");
            object m = Com.Call(methods, "Item", method, 0);
            object inDef = Com.TryGet(m, "InParameters");
            object inParams = inDef != null ? Com.Call(inDef, "SpawnInstance_", 0) : null;
            if (inParams != null && args != null)
            {
                object props = Com.Get(inParams, "Properties_");
                foreach (var kv in args) Com.Set(Com.Call(props, "Item", kv.Key, 0), "Value", kv.Value);
            }
            object result = inParams != null ? Com.Call(cls, "ExecMethod_", method, inParams, 0) : Com.Call(cls, "ExecMethod_", method);
            return new WmiObject(result)["ReturnValue"];
        }

        /// <summary>Igual que Query pero devuelve lista vacía si la clase/espacio no existe o no hay acceso.</summary>
        public static List<WmiObject> TryQuery(string ns, string wql, out string error, int timeoutSeconds = 60)
        {
            error = null;
            // Si WMI por COM ya falló con «los tipos no coinciden» en esta ejecución, va directo por PowerShell (fallaría igual).
            if (comTypeMismatch && QueryForTests == null)
            {
                string direct;
                var rows = QueryViaPowerShell(ns, wql, timeoutSeconds, out direct);
                if (rows != null) return rows;
                error = "WMI por COM no funciona en este equipo (0x80020005) y tampoco con PowerShell: " + direct;
                return new List<WmiObject>();
            }
            try { return Query(ns, wql, timeoutSeconds); }
            catch (Exception ex)
            {
                error = Describe(ex);
                if (!WorthPowerShell(ex)) return new List<WmiObject>();
                var inner = ex;
                while (inner is TargetInvocationException && inner.InnerException != null) inner = inner.InnerException;
                if (inner is COMException && unchecked((uint)((COMException)inner).ErrorCode) == 0x80020005) comTypeMismatch = true;
                string psError;
                var rows = QueryViaPowerShell(ns, wql, timeoutSeconds, out psError);
                if (rows != null)
                {
                    Log.Info("WMI por COM falló (" + error + "); la consulta se hizo con PowerShell (Get-CimInstance): " + Fmt.Truncate(wql, 100));
                    error = null;
                    return rows;
                }
                error += "; tampoco con PowerShell: " + psError;
                return new List<WmiObject>();
            }
        }

        static volatile bool comTypeMismatch;

        /// <summary>Sólo para las pruebas: vuelve a probar WMI por COM.</summary>
        public static void ResetComStateForTests() { comTypeMismatch = false; }

        /// <summary>¿Vale la pena repetir con PowerShell? No si WMI está colgado (tardaría lo mismo) o si la clase o el espacio no existen.</summary>
        static bool WorthPowerShell(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            if (ex is TimeoutException) return false;
            var ce = ex as COMException;
            if (ce != null)
            {
                uint code = unchecked((uint)ce.ErrorCode);
                if (code == 0x80041010 || code == 0x8004100E || code == 0x80041017) return false;
            }
            return true;
        }

        public static WmiObject First(List<WmiObject> list)
        {
            return list != null && list.Count > 0 ? list[0] : null;
        }

        public static object Raw(WmiObject o, string prop)
        {
            if (o == null) return null;
            return o[prop];   // null si la propiedad no existe en esta versión de Windows
        }

        public static string S(WmiObject o, string prop)
        {
            object v = Raw(o, prop);
            if (v == null) return null;
            var arr = v as Array;
            if (arr != null && !(v is string))
            {
                var parts = new List<string>();
                foreach (object x in arr) if (x != null) parts.Add(Convert.ToString(x, CultureInfo.InvariantCulture).Trim());
                return string.Join(", ", parts.ToArray());
            }
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return s == null ? null : s.Trim();
        }

        public static long? L(WmiObject o, string prop)
        {
            object v = Raw(o, prop);
            if (v == null) return null;
            try
            {
                if (v is ulong) { ulong u = (ulong)v; return u > long.MaxValue ? long.MaxValue : (long)u; }
                return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        public static bool? B(WmiObject o, string prop)
        {
            object v = Raw(o, prop);
            if (v == null) return null;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); } catch { return null; }
        }

        public static DateTime? D(WmiObject o, string prop)
        {
            string s = S(o, prop);
            if (string.IsNullOrEmpty(s)) return null;
            return WmiObject.ParseDmtf(s) ?? Fmt.ParseLooseDate(s);
        }

        public static string[] Arr(WmiObject o, string prop)
        {
            var a = Raw(o, prop) as Array;
            if (a == null) return new string[0];
            var list = new List<string>();
            foreach (object x in a) if (x != null) list.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
            return list.ToArray();
        }

        /// <summary>Convierte arreglos uint16 de WmiMonitorID a texto.</summary>
        public static string Chars(WmiObject o, string prop)
        {
            var a = Raw(o, prop) as Array;
            if (a == null) return null;
            var sb = new System.Text.StringBuilder();
            foreach (object x in a)
            {
                int c = Convert.ToInt32(x, CultureInfo.InvariantCulture);
                if (c == 0) break;
                sb.Append((char)c);
            }
            return sb.ToString().Trim();
        }

        public static string Describe(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            if (ex is UnauthorizedAccessException) return "Acceso denegado (ejecutá como administrador)";
            var ce = ex as COMException;
            if (ce != null)
            {
                switch (unchecked((uint)ce.ErrorCode))
                {
                    case 0x80070005: case 0x80041003: return "Acceso denegado (ejecutá como administrador)";
                    case 0x80041010: return "La clase WMI no existe en esta versión de Windows";
                    case 0x8004100E: return "El espacio de nombres WMI no existe en este equipo";
                    case 0x80041002: return "No encontrado";
                    case 0x80041032: return "WMI no respondió a tiempo";
                    case 0x80041017: return "Consulta WMI no soportada en esta versión de Windows";
                }
                return "COM " + Fmt.Hex(ce.ErrorCode) + ": " + ce.Message.Trim();
            }
            if (ex is TimeoutException || ex is NotSupportedException || ex is InvalidOperationException ||
                ex is System.IO.IOException || ex is ArgumentException || ex is OperationCanceledByUserException)
                return ex.Message;
            var w32 = ex as System.ComponentModel.Win32Exception;
            if (w32 != null) return ex.Message + " (" + w32.NativeErrorCode + ")";
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>Un objeto devuelto por WMI (SWbemObject), con sus propiedades leídas por nombre.</summary>
    public sealed class WmiObject
    {
        readonly object com;
        readonly Dictionary<string, object> values;

        internal WmiObject(object comObject) { com = comObject; }

        /// <summary>Para las pruebas: un objeto con valores fijos.</summary>
        public WmiObject(IDictionary<string, object> fixedValues)
        {
            values = new Dictionary<string, object>(fixedValues, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>El valor de la propiedad, o null si no existe o no se puede leer.</summary>
        public object this[string name]
        {
            get
            {
                if (values != null) { object v; return values.TryGetValue(name, out v) ? v : null; }
                if (com == null) return null;
                try
                {
                    object props = Com.Get(com, "Properties_");
                    object p = Com.Call(props, "Item", name, 0);
                    return Com.TryGet(p, "Value");
                }
                catch { return null; }
            }
        }

        /// <summary>Fecha de WMI («20261101143000.000000-180») a DateTime local, o null.</summary>
        public static DateTime? ParseDmtf(string dmtf)
        {
            if (string.IsNullOrEmpty(dmtf) || dmtf.Length < 14) return null;
            DateTime d;
            if (!DateTime.TryParseExact(dmtf.Substring(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return null;
            int offset;
            if (dmtf.Length >= 25 && (dmtf[21] == '+' || dmtf[21] == '-') && int.TryParse(dmtf.Substring(22, 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out offset))
            {
                var utc = DateTime.SpecifyKind(d.AddMinutes(dmtf[21] == '+' ? -offset : offset), DateTimeKind.Utc);
                return utc.ToLocalTime();
            }
            return d;
        }
    }

    /// <summary>Lectura del registro indicando vista (64/32 bits) sin excepciones.</summary>
    public static class Reg
    {
        public static RegistryView[] Views()
        {
            return Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new[] { RegistryView.Registry32 };   // en 32 bits ambas vistas son la misma clave
        }

        public static RegistryView Native
        {
            get { return Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32; }
        }

        public static RegistryKey Open(RegistryHive hive, string path, RegistryView view)
        {
            try
            {
                var root = RegistryKey.OpenBaseKey(hive, view);
                if (string.IsNullOrEmpty(path)) return root;
                // La raíz se cierra: antes quedaba abierta una por cada lectura del registro (miles por auditoría).
                using (root) return root.OpenSubKey(path, false);
            }
            catch { return null; }
        }

        public static RegistryKey Open(RegistryHive hive, string path) { return Open(hive, path, Native); }

        public static object Get(RegistryHive hive, string path, string name, RegistryView view)
        {
            using (var k = Open(hive, path, view))
            {
                if (k == null) return null;
                try { return k.GetValue(name); } catch { return null; }
            }
        }

        public static object Get(RegistryHive hive, string path, string name) { return Get(hive, path, name, Native); }

        public static string Str(RegistryHive hive, string path, string name)
        {
            object v = Get(hive, path, name);
            if (v == null) return null;
            var arr = v as string[];
            if (arr != null) return string.Join("; ", arr);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int? Int(RegistryHive hive, string path, string name)
        {
            object v = Get(hive, path, name);
            if (v == null) return null;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return null; }
        }

        public static bool Exists(RegistryHive hive, string path)
        {
            using (var k = Open(hive, path)) return k != null;
        }

        public static string Str(RegistryKey k, string name)
        {
            if (k == null) return null;
            try
            {
                object v = k.GetValue(name);
                return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
            }
            catch { return null; }
        }

        public static long? Long(RegistryKey k, string name)
        {
            if (k == null) return null;
            try
            {
                object v = k.GetValue(name);
                if (v == null) return null;
                return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        public static string[] SubKeys(RegistryKey k)
        {
            if (k == null) return new string[0];
            try { return k.GetSubKeyNames(); } catch { return new string[0]; }
        }
    }

    /// <summary>COM tardío por reflexión (IDispatch). Evita depender de interop assemblies.</summary>
    public static class Com
    {
        public static object Create(string progId)
        {
            Type t = Type.GetTypeFromProgID(progId, false);
            if (t == null) throw new NotSupportedException("Componente COM no disponible: " + progId);
            return Activator.CreateInstance(t);
        }

        public static object Get(object o, string name, params object[] args)
        {
            try { return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args); }
            catch (TargetInvocationException ex) { throw Unwrap(ex); }
        }

        public static object TryGet(object o, string name)
        {
            try { return o == null ? null : o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null); }
            catch { return null; }
        }

        public static string Str(object o, string name)
        {
            object v = TryGet(o, name);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static void Set(object o, string name, object value)
        {
            try { o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { value }); }
            catch (TargetInvocationException ex) { throw Unwrap(ex); }
        }

        /// <summary>Asigna un objeto COM a una propiedad (DISPATCH_PROPERTYPUTREF con respaldo PUT).</summary>
        public static void SetRef(object o, string name, object value)
        {
            try { o.GetType().InvokeMember(name, BindingFlags.PutRefDispProperty, null, o, new[] { value }); }
            catch { Set(o, name, value); }
        }

        public static object Call(object o, string name, params object[] args)
        {
            try { return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args); }
            catch (TargetInvocationException ex) { throw Unwrap(ex); }
        }

        /// <summary>Recorre una colección COM con Count/Item(i).</summary>
        public static List<object> Items(object collection, int firstIndex)
        {
            var list = new List<object>();
            if (collection == null) return list;
            int n = Convert.ToInt32(Get(collection, "Count"), CultureInfo.InvariantCulture);
            for (int i = 0; i < n; i++) list.Add(Get(collection, "Item", firstIndex + i));
            return list;
        }

        /// <summary>
        /// Recorre una colección COM mediante _NewEnum. Primero intenta IEnumerable (el CLR lo
        /// traduce a DISPID_NEWENUM); si falla, pide [DISPID=-4] y recorre IEnumVARIANT a mano.
        /// </summary>
        public static List<object> Enumerate(object collection)
        {
            var list = new List<object>();
            if (collection == null) return list;
            try
            {
                var e = collection as IEnumerable;
                if (e != null)
                {
                    foreach (object x in e) list.Add(x);
                    return list;
                }
            }
            catch { list.Clear(); }

            object enumObj = collection.GetType().InvokeMember("[DISPID=-4]", BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, collection, null);
            var ev = enumObj as System.Runtime.InteropServices.ComTypes.IEnumVARIANT;
            if (ev == null) throw new NotSupportedException("La colección COM no se puede enumerar.");
            var buffer = new object[1];
            IntPtr fetched = Marshal.AllocCoTaskMem(4);
            try
            {
                while (ev.Next(1, buffer, fetched) == 0 && Marshal.ReadInt32(fetched) == 1)
                    list.Add(buffer[0]);
            }
            finally { Marshal.FreeCoTaskMem(fetched); }
            return list;
        }

        static Exception Unwrap(TargetInvocationException ex)
        {
            return ex.InnerException ?? ex;
        }

        /// <summary>
        /// El HRESULT de un error de COM, sea cual sea el tipo de excepción. .NET convierte algunos HRESULT en excepciones
        /// propias (0x80070002 → FileNotFoundException, 0x80070003 → DirectoryNotFoundException, 0x80070005 →
        /// UnauthorizedAccessException), así que preguntar «¿es COMException?» no alcanza: la tarea programada que no existe
        /// se mostraba como error («El sistema no puede encontrar el archivo especificado. (0x80070002)»). 0 si no hay error de Windows.
        /// </summary>
        public static int HResultOf(Exception ex)
        {
            while (ex != null)
            {
                if (ex is TargetInvocationException || ex is AggregateException) { ex = ex.InnerException; continue; }
                if (ex is COMException || ex is UnauthorizedAccessException || ex is System.IO.FileNotFoundException || ex is System.IO.DirectoryNotFoundException)
                    return ex.HResult;
                if (ex.InnerException == null) return 0;
                ex = ex.InnerException;
            }
            return 0;
        }
    }
}
