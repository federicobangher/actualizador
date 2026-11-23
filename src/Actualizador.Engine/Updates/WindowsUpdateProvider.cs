using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using Actualizador.Core;
using Actualizador.Inventory;
using Microsoft.Win32;

namespace Actualizador.Updates
{
    /// <summary>
    /// Windows Update (actualizaciones del sistema) y controladores, con la API COM del
    /// Agente de Windows Update: existe desde Windows XP, respeta WSUS/directivas y todo lo
    /// que instala viene firmado por Microsoft.
    /// </summary>
    public sealed class WindowsUpdateProvider : IUpdateProvider
    {
        public const string ProviderId = "windowsupdate";
        object session;

        public string Name { get { return ProviderId; } }

        public string Unavailable(UpdateContext ctx)
        {
            try
            {
                session = Com.Create("Microsoft.Update.Session");
                try { Com.Set(session, "ClientApplicationID", AppInfo.Name); } catch { }
                return null;
            }
            catch (Exception ex) { return "El Agente de Windows Update no está disponible: " + Wmi.Describe(ex); }
        }

        sealed class DriverTag
        {
            public object Update;
            public DriverInfo Installed;
            public string Offered = "";
        }

        /// <summary>El dispositivo instalado al que corresponde un controlador ofrecido por Windows Update, o null.</summary>
        public static DriverInfo InstalledOf(UpdateCandidate c)
        {
            var t = c != null ? c.ProviderTag as DriverTag : null;
            return t != null ? t.Installed : null;
        }

        public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes)
        {
            ctx.Report("Windows Update: buscando actualizaciones y controladores (puede tardar varios minutos)…");
            object result = SearchWithRetries(ctx, notes);
            int rc = SystemCollectors.ToInt(Com.TryGet(result, "ResultCode"));
            if (rc == 4 || rc == 5) throw new InvalidOperationException("La búsqueda de Windows Update terminó con estado " + SystemCollectors.WuResult(rc) + ".");
            // 3 = terminó con errores: la lista puede estar incompleta, así que «no ofrece nada» no se afirma.
            bool partial = rc == 3;
            if (partial) notes.Add("La búsqueda de Windows Update terminó con errores parciales: puede faltar alguna actualización. Volvé a buscar más tarde.");

            string wsus = Reg.Str(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer");
            int? useWsus = Reg.Int(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "UseWUServer");
            string source = useWsus == 1 && !string.IsNullOrEmpty(wsus) ? "WSUS (" + wsus + ")" : "Windows Update";
            if (source != "Windows Update") notes.Add("Windows Update está administrado por " + source + ": sólo se ven las actualizaciones aprobadas allí.");

            var drivers = ctx.Drivers;
            var matchedDrivers = new HashSet<DriverInfo>();
            followUp = DriverFollowUp.Snapshot.Open();
            memory = MachineMemory.Snapshot.Open();
            try { InventoryLoader.EnsurePrograms(ctx); } catch (Exception ex) { Log.Info("Windows Update: programas instalados: " + ex.Message); }
            lock (ctx.Programs) amdSuite = AmdGraphics.Suite(ctx.Programs);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool optedIn = OptedInToPreviews();
            int softwareCount = 0, driverCount = 0, optionalCount = 0;
            foreach (object u in Com.Items(Com.Get(result, "Updates"), 0))
            {
                ctx.ThrowIfCancelled();
                string id = Com.Str(Com.TryGet(u, "Identity"), "UpdateID");
                if (!string.IsNullOrEmpty(id) && !seen.Add(id)) continue;
                object da = Com.TryGet(u, "DeploymentAction");
                int? action = da == null ? (int?)null : SystemCollectors.ToInt(da);
                Offer offer = Classify(action, IsBrowseOnly(u));
                if (offer == Offer.Skip) continue;   // desinstalaciones pendientes, sólo detección
                bool optional = offer == Offer.Optional;
                if (optional) optionalCount++;
                int type = SystemCollectors.ToInt(Com.TryGet(u, "Type"));
                if (type == 2)
                {
                    var dc = DriverCandidate(u, drivers, matchedDrivers, source, optional);
                    if (IsSameDriverOffer(output, dc)) { Log.Info("Windows Update: «" + dc.Name + "» " + dc.AvailableVersion + " ofrecido dos veces; se muestra una"); continue; }
                    output.Add(dc);
                    driverCount++;
                }
                else { output.Add(SoftwareCandidate(u, source, optional, optedIn)); softwareCount++; }
            }

            // Controladores de terceros para los que Windows Update no ofrece nada más nuevo.
            int? exclude = Reg.Int(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate");
            int inbox = 0;
            foreach (var d in drivers)
            {
                if (matchedDrivers.Contains(d)) continue;
                // los incluidos con Windows también se listan (antes sólo se contaban: «94 controladores … no se
                // listan»), así se ven TODOS los dispositivos. El Catálogo de Microsoft Update además busca el del fabricante
                // para los que usan el genérico, y su fila reemplaza a ésta.
                if (InventoryLoader.IsInboxDriver(d)) { inbox++; output.Add(InboxCandidate(d, source, exclude == 1, partial)); continue; }
                var c = new UpdateCandidate();
                c.Kind = UpdateKind.Controlador;
                c.Name = d.DeviceName;
                c.Publisher = d.Provider;
                c.InstalledVersion = d.Version + (d.Date.HasValue ? " (" + Fmt.Date(d.Date.Value) + ")" : "");
                c.Source = source;
                c.Identifier = d.HardwareId;
                c.ProviderName = ProviderId;
                c.Installable = false;
                c.ProviderTag = d;          // el dispositivo: el Catálogo de Microsoft Update reemplaza esta fila si compara el mismo
                if (exclude == 1)
                {
                    c.Status = UpdateStatus.NoVerificable;
                    c.Detail = "Una directiva excluye los controladores de Windows Update (ExcludeWUDriversInQualityUpdate).";
                }
                else if (partial)
                {
                    c.Status = UpdateStatus.NoVerificable;
                    c.Detail = "La búsqueda de Windows Update terminó con errores: no se pudo confirmar si hay una versión más nueva.";
                }
                else
                {
                    c.Status = UpdateStatus.AlDia;
                    c.Detail = "Windows Update no ofrece una versión más nueva. El fabricante puede tener versiones propias en su sitio.";
                }
                output.Add(c);
            }
            // Office instalado con MSI (2007–2016) no tiene actualizador propio: sus parches vienen por Microsoft
            // Update, que es lo que activa «Recibir actualizaciones de otros productos de Microsoft».
            var officeMsi = FindOfficeMsi(ctx.Programs);
            if (officeMsi != null && useWsus != 1)
            {
                var mu = MicrosoftUpdateState();
                if (!mu.Key)
                    output.Add(MicrosoftUpdateCandidate(ctx, officeMsi));
                else
                {
                    int found = SearchMicrosoftUpdate(ctx, seen, output, source, optedIn, notes);
                    softwareCount += found;
                    if (found == 0) notes.Add("Microsoft Update (Office instalado con MSI: «" + officeMsi.Name + "»): sin actualizaciones pendientes.");
                }
            }

            notes.Add("Windows Update: " + Fmt.Plural(softwareCount, "actualización del sistema", "actualizaciones del sistema") + " y " +
                      Fmt.Plural(driverCount, "controlador", "controladores") + (softwareCount + driverCount == 1 ? " disponible" : " disponibles")
                + (optionalCount > 0 ? " (" + Fmt.Plural(optionalCount, "opcional", "opcionales") + ": las que Windows muestra en «Actualizaciones opcionales» o como versión preliminar)" : "") + ".");
            if (!searchedOptional) notes.Add("Esta versión del Agente de Windows Update no permite buscar las actualizaciones opcionales; sólo se listan las normales.");
            if (inbox > 0) notes.Add(Fmt.Plural(inbox, "controlador incluido", "controladores incluidos") + " con Windows (proveedor Microsoft): se actualizan con el propio Windows Update y se ven en «Al día».");
            if (!ctx.IsAdmin) notes.Add("Para instalar actualizaciones de Windows y controladores hay que ejecutar como administrador.");
        }

        /// <summary>Fila de un controlador incluido con Windows (proveedor Microsoft) para el que Windows Update no ofrece nada.</summary>
        public static UpdateCandidate InboxCandidate(DriverInfo d, string source, bool excluded, bool partial)
        {
            var c = new UpdateCandidate();
            c.Kind = UpdateKind.Controlador;
            c.Name = string.IsNullOrEmpty(d.DeviceName) ? d.HardwareId : d.DeviceName;
            c.Publisher = string.IsNullOrEmpty(d.Provider) ? "Microsoft" : d.Provider;
            c.InstalledVersion = d.Version + (d.Date.HasValue ? " (" + Fmt.Date(d.Date.Value) + ")" : "");
            c.Source = source;
            c.Identifier = d.HardwareId;
            c.ProviderName = ProviderId;
            c.Installable = false;
            c.ProviderTag = d;
            if (excluded || partial)
            {
                c.Status = UpdateStatus.NoVerificable;
                c.Detail = excluded ? "Una directiva excluye los controladores de Windows Update (ExcludeWUDriversInQualityUpdate)."
                                    : "La búsqueda de Windows Update terminó con errores: no se pudo confirmar si hay una versión más nueva.";
            }
            else
            {
                c.Status = UpdateStatus.AlDia;
                c.Detail = "Controlador incluido con Windows (" + c.Publisher + "): se actualiza con el propio Windows Update, que no ofrece nada más nuevo.";
            }
            return c;
        }

        /// <summary>
        /// Códigos transitorios: el servicio está arrancando, deteniéndose u ocupado con otra
        /// operación. Reintentar resuelve el problema; fallar de entrada obliga a repetir toda
        /// la búsqueda a mano.
        /// </summary>
        public static bool IsTransient(uint code)
        {
            switch (code)
            {
                case 0x8024001E:   // operación cancelada: el servicio se detiene o reinicia
                case 0x8024A105:   // Windows Update ya está trabajando
                case 0x80240016:   // hay otra operación en curso
                case 0x8024000B:   // llamada cancelada
                case 0x80244022:   // servidor no disponible temporalmente (503)
                case 0x80072EE2:   // se agotó el tiempo de espera
                    return true;
                default: return false;
            }
        }

        static uint CodeOf(Exception ex)
        {
            // También los HRESULT que .NET convierte en otras excepciones (0x80070005 → UnauthorizedAccessException).
            return unchecked((uint)Com.HResultOf(ex));
        }

        /// <summary>
        /// Criterio completo. «DeploymentAction=*» es imprescindible: sin él, la API sólo devuelve
        /// lo que Windows instalaría por su cuenta y omite las actualizaciones opcionales
        /// (DeploymentAction = OptionalInstallation): las acumulativas «versión preliminar» de fin
        /// de mes, los controladores opcionales y las que Windows reserva para quien toca
        /// «Buscar actualizaciones». La pantalla de Windows Update sí las muestra.
        /// </summary>
        public const string CriteriaFull = "IsInstalled=0 and IsHidden=0 and DeploymentAction=*";
        /// <summary>Respaldo para agentes viejos (Windows 7/8) que no entienden «DeploymentAction=*».</summary>
        public const string CriteriaBasic = "IsInstalled=0 and IsHidden=0";
        const uint WU_E_INVALID_CRITERIA = 0x80240032;

        bool searchedOptional = true;

        public enum Offer { Skip, Normal, Optional }

        /// <summary>
        /// Qué hacer con cada resultado según IUpdate.DeploymentAction (0 ninguna, 1 instalar,
        /// 2 desinstalar, 3 sólo detección, 4 instalación opcional). Null = el agente no expone
        /// la propiedad. Pura para poder probarla.
        /// </summary>
        public static Offer Classify(int? deploymentAction, bool browseOnly)
        {
            if (!deploymentAction.HasValue) return browseOnly ? Offer.Optional : Offer.Normal;
            switch (deploymentAction.Value)
            {
                case 1: return browseOnly ? Offer.Optional : Offer.Normal;
                case 4: return Offer.Optional;
                default: return Offer.Skip;
            }
        }

        /// <summary>
        /// Una actualización opcional del sistema se premarca sólo si el usuario activó en
        /// Windows «Obtén las últimas actualizaciones en cuanto estén disponibles». Los
        /// controladores opcionales nunca: dependen de la comparación de versiones.
        /// </summary>
        public static bool RecommendOptional(bool isDriver, bool optedInToPreviews)
        {
            return !isDriver && optedInToPreviews;
        }

        static bool OptedInToPreviews()
        {
            int? v = Reg.Int(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "IsContinuousInnovationOptedIn");
            return v == 1;
        }

        static bool IsBrowseOnly(object u)
        {
            object b = Com.TryGet(u, "BrowseOnly");
            return b is bool && (bool)b;
        }

        object SearchWithRetries(UpdateContext ctx, List<string> notes)
        {
            const int intentos = 3;
            string criteria = CriteriaFull;
            searchedOptional = true;
            for (int i = 1; ; i++)
            {
                try { return SearchWithTimeout(ctx, criteria, TimeSpan.FromMinutes(30)); }
                catch (OperationCanceledByUserException) { throw; }
                catch (Exception ex)
                {
                    uint code = CodeOf(ex);
                    if (code == WU_E_INVALID_CRITERIA && criteria == CriteriaFull)
                    {
                        Log.Warn("El Agente de Windows Update no acepta «DeploymentAction=*»; se busca con el criterio básico.");
                        criteria = CriteriaBasic;
                        searchedOptional = false;
                        i--;
                        continue;
                    }
                    if (i >= intentos || !IsTransient(code)) throw;
                    int espera = 15 * i;
                    Log.Warn("Windows Update devolvió " + Fmt.Hex(unchecked((int)code)) + "; reintento " + i + " de " + (intentos - 1) + " en " + espera + " s.");
                    ctx.Report("Windows Update está ocupado; reintentando en " + espera + " segundos…");
                    for (int s = 0; s < espera * 2; s++)
                    {
                        ctx.ThrowIfCancelled();
                        Thread.Sleep(500);
                    }
                    notes.Add("Windows Update estaba ocupado (" + DescribeWu(ex) + "); se reintentó automáticamente.");
                }
            }
        }

        object SearchWithTimeout(UpdateContext ctx, string criteria, TimeSpan timeout)
        {
            return SearchWithTimeout(ctx, criteria, timeout, null);
        }

        object SearchWithTimeout(UpdateContext ctx, string criteria, TimeSpan timeout, string serviceId)
        {
            object result = null;
            Exception error = null;
            object searcher = Com.Call(session, "CreateUpdateSearcher");
            try { Com.Set(searcher, "Online", true); } catch { }
            if (serviceId != null)
            {
                Com.Set(searcher, "ServerSelection", 3);      // ssOthers: el servicio que se indica
                Com.Set(searcher, "ServiceID", serviceId);
            }
            var t = new Thread(delegate()
            {
                try { result = Com.Call(searcher, "Search", criteria); }
                catch (Exception ex) { error = ex; }
            });
            t.IsBackground = true;
            t.Start();
            DateTime limit = DateTime.Now + timeout;
            while (!t.Join(500))
            {
                if (ctx.CancelRequested) throw new OperationCanceledByUserException();
                if (DateTime.Now > limit) throw new TimeoutException("Windows Update no respondió en " + (int)timeout.TotalMinutes + " minutos.");
            }
            if (error != null) throw new InvalidOperationException(DescribeWu(error), error);
            return result;
        }

        UpdateCandidate SoftwareCandidate(object u, string source, bool optional, bool optedIn)
        {
            var c = new UpdateCandidate();
            c.Kind = UpdateKind.WindowsUpdate;
            c.Name = Com.Str(u, "Title") ?? "(sin título)";
            c.Publisher = "Microsoft";
            c.InstalledVersion = "No instalada";
            var kbs = new List<string>();
            try { foreach (object k in Com.Items(Com.Get(u, "KBArticleIDs"), 0)) kbs.Add("KB" + Convert.ToString(k, CultureInfo.InvariantCulture)); } catch (Exception qx) { Log.Quiet("Windows Update: KB de la actualización", qx); }
            c.AvailableVersion = string.Join(", ", kbs.ToArray());
            c.Source = source;
            c.Identifier = Com.Str(Com.TryGet(u, "Identity"), "UpdateID");
            c.Status = UpdateStatus.Disponible;
            c.Installable = true;
            c.Recommended = optional ? RecommendOptional(false, optedIn) : true;
            var parts = new List<string>();
            string sev = Com.Str(u, "MsrcSeverity");
            if (!string.IsNullOrEmpty(sev)) parts.Add("Seguridad: " + Severity(sev));
            string cat = FirstCategory(u);
            if (!string.IsNullOrEmpty(cat)) parts.Add(cat);
            if (optional) parts.Add(optedIn ? "Opcional (tenés activado recibir las últimas actualizaciones)" : "Opcional");
            double bytes = DownloadSize(u);
            string size = DownloadSizeText(bytes, ToDouble(Com.TryGet(u, "MinDownloadSize")));
            if (size != null) parts.Add(size);
            if (SystemCollectors.ToInt(Com.TryGet(u, "RebootBehavior")) == 1) parts.Add("Requiere reinicio");
            c.Detail = string.Join(" · ", parts.ToArray());
            c.ProviderName = ProviderId;
            c.ProviderTag = u;
            return c;
        }

        /// <summary>
        /// Tamaño de descarga realista. En una actualización acumulativa, MaxDownloadSize es la
        /// SUMA de todas las variantes del paquete (idiomas, ediciones, arquitecturas) y puede dar
        /// decenas de GB; lo que se descarga es una sola variante, así que se usa la mayor de ellas.
        /// </summary>
        static double DownloadSize(object u)
        {
            double own = ToDouble(Com.TryGet(u, "MaxDownloadSize"));
            double biggestChild = 0;
            int children = 0;
            try
            {
                foreach (object b in Com.Items(Com.Get(u, "BundledUpdates"), 0))
                {
                    children++;
                    double s = ToDouble(Com.TryGet(b, "MaxDownloadSize"));
                    if (s > biggestChild) biggestChild = s;
                }
            }
            catch (Exception qx) { Log.Quiet("Windows Update: tamaño del paquete", qx); }
            return children > 1 && biggestChild > 0 ? biggestChild : own;
        }

        /// <summary>Más que esto no es lo que se descarga: es la suma de todas las variantes del paquete.</summary>
        public const double UnrealisticDownload = 10.0 * 1024 * 1024 * 1024;

        /// <summary>
        /// 2.0.20 (diagnóstico del 21/11/2026: «Descarga de hasta 90,5 GB» en la acumulativa de Windows 11): desde 24H2 las acumulativas
        /// informan como tamaño máximo el de todos sus archivos completos, aunque el equipo baja sólo lo que le falta (unos cientos de MB).
        /// Por encima de 10 GB no se muestra ese número: el mínimo si Windows lo informa, o que depende del equipo. null si no hay dato.
        /// Pública para las pruebas.
        /// </summary>
        public static string DownloadSizeText(double max, double min)
        {
            if (max <= 0) return null;
            if (max <= UnrealisticDownload) return "Descarga de hasta " + Fmt.Bytes(max);
            if (min > 0 && min <= UnrealisticDownload) return "Descarga desde " + Fmt.Bytes(min) + " (baja sólo lo que le falta a este equipo)";
            return "Descarga según lo que le falte a este equipo";
        }

        static readonly System.Text.RegularExpressions.Regex CumulativeBuildPattern = new System.Text.RegularExpressions.Regex(
            @"\((?<b>\d{5})\.(?<r>\d{1,6})\)\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// La compilación de una actualización acumulativa de Windows por su título («2026-11 Actualización de seguridad (KB5132061)
        /// (26300.9974)» → 26300, 9974), o false. Pública para las pruebas.
        /// </summary>
        public static bool CumulativeBuild(string title, out int build, out int revision)
        {
            build = revision = 0;
            var m = CumulativeBuildPattern.Match(title ?? "");
            return m.Success && int.TryParse(m.Groups["b"].Value, out build) && int.TryParse(m.Groups["r"].Value, out revision);
        }

        /// <summary>
        /// 2.0.20: las acumulativas de la misma compilación, de la más nueva a la más vieja (lo demás, en el orden de siempre). La más
        /// nueva ya incluye a las anteriores: en el diagnóstico del 21/11/2026 la de seguridad (26300.9974) se instaló primero y falló
        /// (0x800F081F) y la preliminar (26300.10075), que la incluye, se instaló bien después. Pública para las pruebas.
        /// </summary>
        public static List<UpdateCandidate> InstallOrder(List<UpdateCandidate> items)
        {
            var source = items ?? new List<UpdateCandidate>();
            var groups = new Dictionary<int, List<UpdateCandidate>>();
            foreach (var c in source)
            {
                int b, r;
                if (c == null || c.Kind != UpdateKind.WindowsUpdate || !CumulativeBuild(c.Name, out b, out r)) continue;
                List<UpdateCandidate> g;
                if (!groups.TryGetValue(b, out g)) groups[b] = g = new List<UpdateCandidate>();
                g.Add(c);
            }
            var list = new List<UpdateCandidate>();
            var placed = new HashSet<int>();
            foreach (var c in source)
            {
                int b, r;
                if (c == null || c.Kind != UpdateKind.WindowsUpdate || !CumulativeBuild(c.Name, out b, out r)) { list.Add(c); continue; }
                if (!placed.Add(b)) continue;                       // el grupo ya se puso entero, en el lugar del primero
                var g = new List<UpdateCandidate>(groups[b]);
                var pos = new Dictionary<UpdateCandidate, int>();
                for (int i = 0; i < g.Count; i++) pos[g[i]] = i;
                g.Sort(delegate(UpdateCandidate x, UpdateCandidate y)
                {
                    int bx, rx, by, ry;
                    CumulativeBuild(x.Name, out bx, out rx);
                    CumulativeBuild(y.Name, out by, out ry);
                    int k = ry.CompareTo(rx);
                    return k != 0 ? k : pos[x].CompareTo(pos[y]);
                });
                list.AddRange(g);
            }
            return list;
        }

        /// <summary>
        /// La acumulativa más nueva de la misma compilación ya instalada en esta tanda (que incluye a <paramref name="c"/>), o null.
        /// Pública para las pruebas.
        /// </summary>
        public static UpdateCandidate IncludedIn(UpdateCandidate c, IEnumerable<UpdateCandidate> done)
        {
            int b, r;
            if (c == null || c.Kind != UpdateKind.WindowsUpdate || !CumulativeBuild(c.Name, out b, out r)) return null;
            foreach (var d in done)
            {
                int db, dr;
                if (d == c || d.Kind != UpdateKind.WindowsUpdate || !CumulativeBuild(d.Name, out db, out dr)) continue;
                if (db == b && dr > r && (d.Status == UpdateStatus.Instalada || d.Status == UpdateStatus.RequiereReinicio)) return d;
            }
            return null;
        }

        /// <summary>
        /// 2.0.20: qué hacer ante los errores de instalación más comunes de Windows Update (antes la fila decía sólo «Error (0x800F081F)»).
        /// "" si no hay una indicación. Pública para las pruebas.
        /// </summary>
        public static string InstallErrorHint(int hresult)
        {
            switch (unchecked((uint)hresult))
            {
                case 0x800F081F:   // CBS_E_SOURCE_MISSING
                case 0x800F0831:   // CBS_E_STORE_CORRUPTION
                case 0x80073712:   // ERROR_SXS_COMPONENT_STORE_CORRUPT
                case 0x800F0906:   // CBS_E_DOWNLOAD_FAILURE
                case 0x800F0984:
                case 0x800F0986:
                    return "faltan o están dañados archivos de componentes de Windows. En una consola de administrador: «DISM /Online /Cleanup-Image /RestoreHealth» " +
                           "y después «sfc /scannow»; reiniciá y volvé a actualizar";
                case 0x800F0922:
                    return "Windows no pudo terminar de instalarla (suele ser la partición reservada del sistema llena, o una VPN o un antivirus que la cortan). " +
                           "Reiniciá y volvé a intentar; si se repite, desactivá la VPN durante la actualización";
                case 0x80070070:
                case 0x80070027:
                    return "no hay espacio suficiente en el disco: liberá espacio (Configuración › Sistema › Almacenamiento) y volvé a intentar";
                case 0x8024200D:
                case 0x80246007:
                    return "la descarga quedó incompleta: volvé a intentar (se vuelve a bajar)";
                case 0x80240022:
                    return "fallaron todas las partes de la actualización: reiniciá y volvé a intentar";
                case 0x80070643:
                    return "falló el instalador de la actualización: reiniciá y volvé a intentar";
                default:
                    return "";
            }
        }

        static double ToDouble(object o)
        {
            try { return o == null ? 0 : Convert.ToDouble(o, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public enum DriverVerdict { Newer, Same, Older, Unknown }

        /// <summary>
        /// Compara el controlador ofrecido con el instalado: primero por versión y, si empatan
        /// o no se pueden comparar, por fecha. Es pura para poder probarla.
        /// </summary>
        public static DriverVerdict CompareDriver(string offered, DateTime? offeredDate, string installed, DateTime? installedDate)
        {
            int? cmp = VersionUtil.Compare(offered, installed);
            if (cmp.HasValue && cmp.Value > 0) return DriverVerdict.Newer;
            if (cmp.HasValue && cmp.Value < 0) return DriverVerdict.Older;
            if (offeredDate.HasValue && installedDate.HasValue)
            {
                if (offeredDate.Value.Date > installedDate.Value.Date) return DriverVerdict.Newer;
                if (offeredDate.Value.Date < installedDate.Value.Date) return DriverVerdict.Older;
                return DriverVerdict.Same;
            }
            return cmp.HasValue ? DriverVerdict.Same : DriverVerdict.Unknown;
        }

        static string Severity(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "critical": return "Crítica";
                case "important": return "Importante";
                case "moderate": return "Moderada";
                case "low": return "Baja";
                default: return s;
            }
        }

        static string FirstCategory(object u)
        {
            try
            {
                foreach (object cat in Com.Items(Com.Get(u, "Categories"), 0))
                {
                    string type = Com.Str(cat, "Type") ?? "";
                    if (type.Equals("UpdateClassification", StringComparison.OrdinalIgnoreCase)) return Com.Str(cat, "Name");
                }
            }
            catch (Exception qx) { Log.Quiet("Windows Update: clasificación", qx); }
            return null;
        }

        static readonly Regex VersionInTitle = new Regex(@"\d+(?:\.\d+){2,}", RegexOptions.Compiled);

        /// <summary>
        /// ¿El identificador ofrecido es el instalado o uno menos específico del mismo dispositivo
        /// («PCI\VEN_8086&amp;DEV_9A49&amp;SUBSYS_0A201028» para «…&amp;SUBSYS_0A201028&amp;REV_01»)? Sólo
        /// cortes completos y con fabricante y modelo (al menos un «&amp;»): «PCI\VEN_8086» no alcanza.
        /// </summary>
        public static bool HardwareIdMatches(string installed, string offered)
        {
            if (string.IsNullOrEmpty(installed) || string.IsNullOrEmpty(offered)) return false;
            if (installed.Equals(offered, StringComparison.OrdinalIgnoreCase)) return true;
            if (offered.IndexOf('&') < 0 || offered.EndsWith("&")) return false;
            return installed.Length > offered.Length && installed.StartsWith(offered, StringComparison.OrdinalIgnoreCase) && installed[offered.Length] == '&';
        }

        DriverFollowUp.Snapshot followUp;
        MachineMemory.Snapshot memory;
        InstalledProgram amdSuite;

        /// <summary>
        /// 2.0.27 (grabación del usuario: dos filas «HP Printer (BIDI)» iguales): Windows Update a veces publica el mismo controlador dos veces
        /// (dos actualizaciones con el mismo identificador de hardware y la misma versión). Se instala para el identificador, no para una
        /// fila: con una alcanza.
        /// </summary>
        public static bool IsSameDriverOffer(List<UpdateCandidate> output, UpdateCandidate c)
        {
            if (output == null || c == null || c.Kind != UpdateKind.Controlador || string.IsNullOrEmpty(c.Identifier)) return false;
            foreach (var o in output)
                if (o.Kind == UpdateKind.Controlador && o.ProviderName == c.ProviderName && string.Equals(o.Identifier, c.Identifier, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(o.AvailableVersion, c.AvailableVersion, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 2.0.27: un controlador de Windows Update para un dispositivo que hoy no tiene controlador. Si ya se instaló (esa versión, para ese
        /// identificador) y Windows Update lo vuelve a ofrecer, ningún dispositivo conectado lo tomó: casi siempre es un dispositivo USB
        /// (impresora, escáner) desconectado o apagado, o una función que no usa. Antes se «instalaba correctamente» en cada pasada.
        /// </summary>
        public static void AdjustNoDeviceDriver(UpdateCandidate c, string hwid, string offered, MachineMemory.Snapshot memory)
        {
            if (c == null || string.IsNullOrEmpty(hwid) || VersionUtil.Parse(offered) == null) return;
            string date, text;
            if (!MachineMemory.TryGet(memory, MachineMemory.WuDriverKey(hwid, offered), out date, out text)) return;
            c.Status = UpdateStatus.AlDia;
            c.Installable = false;
            c.Recommended = false;
            c.Detail = "Ya se instaló el " + MachineMemory.ShowDate(date) + " y Windows Update lo vuelve a ofrecer: ningún dispositivo conectado lo está usando (" + hwid +
                       "). Suele ser un dispositivo USB (una impresora, un escáner) desconectado o apagado, o una función suya que no se usa: al conectarlo, " +
                       "Windows toma el controlador solo, porque ya quedó en el equipo. Instalarlo de nuevo no cambia nada: no se vuelve a ofrecer.";
        }

        UpdateCandidate DriverCandidate(object u, List<DriverInfo> drivers, HashSet<DriverInfo> matched, string source, bool optional)
        {
            var c = new UpdateCandidate();
            c.Kind = UpdateKind.Controlador;
            string title = Com.Str(u, "Title") ?? "";
            string model = Com.Str(u, "DriverModel");
            string hwid = Com.Str(u, "DriverHardwareID") ?? "";
            c.Name = string.IsNullOrEmpty(model) ? title : model;
            c.Publisher = Com.Str(u, "DriverProvider") ?? Com.Str(u, "DriverManufacturer");
            var ms = VersionInTitle.Matches(title);
            string offered = ms.Count > 0 ? ms[ms.Count - 1].Value : "";
            object verDate = Com.TryGet(u, "DriverVerDate");
            DateTime? offeredDate = verDate is DateTime ? (DateTime?)(DateTime)verDate : null;
            c.AvailableVersion = offered + (offeredDate.HasValue ? " (" + Fmt.Date(offeredDate.Value.Date) + ")" : "");
            c.Source = source;
            c.Identifier = hwid;
            c.ProviderName = ProviderId;
            c.Installable = true;
            c.Status = UpdateStatus.Disponible;

            DriverInfo installed = null;
            foreach (var d in drivers)
                if (hwid.Length > 0 && d.HardwareId.Equals(hwid, StringComparison.OrdinalIgnoreCase)) { installed = d; break; }
            // WMI guarda el identificador más específico (…&SUBSYS_…&REV_01) y Windows Update informa con el
            // que coincidió el controlador, a menudo más corto (sin REV, o sin SUBSYS).
            if (installed == null && hwid.Length > 0)
                foreach (var d in drivers)
                    if (HardwareIdMatches(d.HardwareId, hwid)) { installed = d; break; }
            if (installed == null && hwid.Length > 0)
                foreach (var d in drivers)
                    if (d.DeviceId.StartsWith(hwid.Split('&')[0], StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(model) && d.DeviceName.Equals(model, StringComparison.OrdinalIgnoreCase)) { installed = d; break; }
            if (installed == null && !string.IsNullOrEmpty(model))
                foreach (var d in drivers)
                    if (d.DeviceName.Equals(model, StringComparison.OrdinalIgnoreCase)) { installed = d; break; }

            c.Recommended = optional ? RecommendOptional(true, false) : true;
            if (installed != null)
            {
                matched.Add(installed);
                c.InstalledVersion = installed.Version + (installed.Date.HasValue ? " (" + Fmt.Date(installed.Date.Value) + ")" : "");
                switch (CompareDriver(offered, offeredDate, installed.Version, installed.Date))
                {
                    case DriverVerdict.Newer:
                        c.Detail = "Versión más nueva que la instalada.";
                        AdjustNewerDriver(c, installed, offered, followUp, amdSuite);
                        break;
                    case DriverVerdict.Same:
                        c.Status = UpdateStatus.AlDia;
                        c.Installable = false;
                        c.Recommended = false;
                        c.Detail = "Esa versión ya está instalada.";
                        break;
                    case DriverVerdict.Older:
                        // Windows Update a veces ofrece el controlador «oficial» del fabricante del
                        // equipo aunque haya uno más nuevo instalado (típico en video Intel/NVIDIA/AMD).
                        // Instalarlo degradaría el controlador: no se permite.
                        c.Status = UpdateStatus.AlDia;
                        c.Installable = false;
                        c.Recommended = false;
                        c.Detail = "Windows Update ofrece una versión MÁS ANTIGUA que la instalada. No se instala para no degradar el controlador.";
                        break;
                    default:
                        c.Recommended = false;
                        c.Detail = "No se pudo comparar con la versión instalada: revisala antes de instalar.";
                        break;
                }
            }
            else
            {
                c.InstalledVersion = "Sin controlador / genérico";
                c.Detail = "Ofrecido por Windows Update para " + (hwid.Length > 0 ? hwid : "un dispositivo del equipo") + ".";
                AdjustNoDeviceDriver(c, hwid, offered, memory);
            }
            string cls = Com.Str(u, "DriverClass");
            if (!string.IsNullOrEmpty(cls)) c.Detail = "Clase " + cls + ". " + c.Detail;
            if (optional) c.Detail += " Opcional.";
            c.ProviderTag = new DriverTag { Update = u, Installed = installed, Offered = offered };
            return c;
        }

        /// <summary>
        /// 2.0.25: una oferta de Windows Update más nueva que la instalada. Si ya se instaló y espera el reinicio, queda «requiere
        /// reinicio» (no se vuelve a instalar); si ya se reinició y el dispositivo sigue con la de antes, Windows no la usa en este equipo:
        /// «Al día», con el motivo (antes se instalaba en cada pasada, «dice que instala y al reiniciar sigue igual»). Para el video de AMD
        /// instalado con AMD Software, no se marca sola.
        /// </summary>
        public static void AdjustNewerDriver(UpdateCandidate c, DriverInfo installed, string offered, DriverFollowUp.Snapshot followUp, InstalledProgram amdSuite)
        {
            if (c == null || installed == null) return;
            DateTime at;
            switch (DriverFollowUp.Check(followUp, installed.DeviceId, offered, installed.Version, out at))
            {
                case DriverFollowUp.State.WaitingReboot:
                    c.Status = UpdateStatus.RequiereReinicio;
                    c.Installable = false;
                    c.Recommended = false;
                    c.Detail = DriverFollowUp.WaitingText(offered, at, installed.Version);
                    return;
                case DriverFollowUp.State.NotApplied:
                    c.Status = UpdateStatus.AlDia;
                    c.Installable = false;
                    c.Recommended = false;
                    c.Detail = "Windows Update la sigue ofreciendo, pero no la aplica en este equipo. " + DriverFollowUp.NotAppliedText(offered, at, installed.Version);
                    return;
            }
            if (amdSuite != null && AmdGraphics.IsGraphicsFamily(installed) && !InventoryLoader.IsInboxDriver(installed))
            {
                c.Recommended = false;
                c.Detail += AmdGraphics.WindowsUpdateText(amdSuite);
            }
        }

        // ------------------------------------------------------------------ 2.0.25: descarga anticipada

        Thread prefetch;
        volatile bool prefetchTimedOut;
        volatile string prefetchResult;

        /// <summary>Sólo para las pruebas: reemplaza la descarga anticipada (recibe cuántas actualizaciones; true = terminó a tiempo).</summary>
        internal static Func<int, bool> PrefetchForTests;

        /// <summary>
        /// 2.0.25 (pedido del usuario: «tarda mucho en instalar»): Windows Update se instala al final de la tanda (después de winget,
        /// navegadores, fabricantes y controladores) y recién ahí empezaba a DESCARGAR, una por una (la acumulativa sola son cientos de
        /// MB). Ahora las elegidas se empiezan a descargar en segundo plano apenas arranca la tanda, todas juntas (Windows las baja con
        /// su servicio de descargas), MIENTRAS se instala lo demás; al llegar su turno ya están descargadas y sólo se instalan.
        /// </summary>
        public void StartPrefetch(UpdateContext ctx, List<UpdateCandidate> items)
        {
            if (items == null || ctx == null || !ctx.IsAdmin) return;
            var updates = new List<object>();
            foreach (var c in items)
            {
                if (c == null || c.ProviderTag is MicrosoftUpdateActivation) continue;
                object u = c.ProviderTag is DriverTag ? ((DriverTag)c.ProviderTag).Update : c.ProviderTag;
                if (u == null || u is DriverInfo) continue;
                updates.Add(u);
            }
            if (updates.Count == 0 || (session == null && PrefetchForTests == null) || (prefetch != null && prefetch.IsAlive)) return;
            prefetchTimedOut = false;
            prefetchResult = null;
            var t = new Thread(delegate()
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (PrefetchForTests != null) { prefetchTimedOut = !PrefetchForTests(updates.Count); return; }
                    object coll = Com.Create("Microsoft.Update.UpdateColl");
                    int pending = 0;
                    foreach (object u in updates)
                    {
                        object dl = Com.TryGet(u, "IsDownloaded");
                        if (dl is bool && (bool)dl) continue;
                        object eula = Com.TryGet(u, "EulaAccepted");
                        if (eula is bool && !(bool)eula) Com.Call(u, "AcceptEula");
                        Com.Call(coll, "Add", u);
                        pending++;
                    }
                    if (pending == 0) { prefetchResult = "ya estaban descargadas"; return; }
                    Log.Info("Windows Update: se descargan por adelantado " + pending + " actualizaciones mientras se instala lo demás");
                    object downloader = Com.Call(session, "CreateUpdateDownloader");
                    Com.SetRef(downloader, "Updates", coll);
                    bool slow;
                    object dres = CallWithLimit(delegate { return Com.Call(downloader, "Download"); }, DownloadLimit, out slow);
                    prefetchTimedOut = slow;
                    prefetchResult = slow ? "no terminó a tiempo" : SystemCollectors.WuResult(SystemCollectors.ToInt(Com.TryGet(dres, "ResultCode")));
                }
                catch (Exception ex) { prefetchResult = DescribeWu(ex); Log.Warn("Windows Update: descarga anticipada: " + prefetchResult); }
                finally { Log.Info("Windows Update: descarga anticipada en " + Fmt.Elapsed(sw.Elapsed) + (prefetchResult != null ? " (" + prefetchResult + ")" : "")); }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "windows-update-descarga";
            prefetch = t;
            t.Start();
        }

        /// <summary>Espera a que termine la descarga anticipada (cancelable). false si se canceló o no terminó a tiempo.</summary>
        bool WaitPrefetch(UpdateContext ctx)
        {
            var t = prefetch;
            if (t == null) return true;
            bool told = false;
            while (!t.Join(500))
            {
                if (ctx.CancelRequested) return false;
                if (!told) { ctx.Report("Windows Update: terminando de descargar las actualizaciones…"); told = true; }
            }
            prefetch = null;
            return !prefetchTimedOut;
        }

        public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed)
        {
            if (!ctx.IsAdmin)
            {
                foreach (var c in items) { c.Status = UpdateStatus.Fallida; c.Result = "Requiere ejecutar como administrador"; changed(c); }
                return;
            }
            if (!WaitPrefetch(ctx))
            {
                foreach (var c in items)
                {
                    if (ctx.CancelRequested) { c.Status = UpdateStatus.Pendiente; c.Result = "Cancelada antes de empezar"; }
                    else
                    {
                        c.Status = UpdateStatus.Fallida;
                        c.Result = "La descarga no terminó en " + Fmt.Duration(DownloadLimit) + ": Windows Update puede seguir descargándola en segundo plano. Revisá Configuración › Windows Update";
                    }
                    changed(c);
                }
                return;
            }
            // Una por una: da progreso por elemento y aísla fallas. 2.0.20: la acumulativa más nueva primero.
            string busyWith = null;
            var ordered = InstallOrder(items);
            var done = new List<UpdateCandidate>();
            foreach (var c in ordered)
            {
                if (ctx.CancelRequested) { c.Status = UpdateStatus.Pendiente; c.Result = "Cancelada antes de empezar"; changed(c); continue; }
                var newer = IncludedIn(c, done);
                if (newer != null)
                {
                    c.Status = newer.Status;
                    c.InstalledVersion = "Incluida";
                    c.Result = "No hace falta instalarla aparte: «" + Fmt.Truncate(newer.Name, 90) + "», instalada en esta tanda, ya la incluye " +
                               "(cada acumulativa trae todas las anteriores)" + (newer.Status == UpdateStatus.RequiereReinicio ? ". Requiere reiniciar" : "");
                    changed(c);
                    continue;
                }
                done.Add(c);
                if (busyWith != null)
                {
                    // El Agente de Windows Update sigue ocupado con la anterior: ésta esperaría lo mismo.
                    c.Status = UpdateStatus.Fallida;
                    c.Result = "No se intentó: Windows Update sigue trabajando con «" + Fmt.Truncate(busyWith, 80) + "». Esperá a que termine (Configuración › Windows Update) y reintentá";
                    changed(c);
                    continue;
                }
                if (c.ProviderTag is MicrosoftUpdateActivation) { EnableMicrosoftUpdate(ctx, c); changed(c); continue; }
                object update = c.ProviderTag is DriverTag ? ((DriverTag)c.ProviderTag).Update : c.ProviderTag;
                if (update == null) { c.Status = UpdateStatus.Fallida; c.Result = "Referencia de actualización perdida; volvé a buscar"; changed(c); continue; }
                c.Status = UpdateStatus.Instalando;
                c.Result = "Descargando…";
                changed(c);
                try
                {
                    object coll = Com.Create("Microsoft.Update.UpdateColl");
                    object eula = Com.TryGet(update, "EulaAccepted");
                    if (eula is bool && !(bool)eula) Com.Call(update, "AcceptEula");
                    Com.Call(coll, "Add", update);

                    ctx.Report("Descargando: " + c.Name);
                    object downloader = Com.Call(session, "CreateUpdateDownloader");
                    Com.SetRef(downloader, "Updates", coll);
                    bool slow;
                    object dres = CallWithLimit(delegate { return Com.Call(downloader, "Download"); }, DownloadLimit, out slow);
                    if (slow)
                    {
                        busyWith = c.Name;
                        c.Status = UpdateStatus.Fallida;
                        c.Result = "La descarga no terminó en " + Fmt.Duration(DownloadLimit) + ": Windows Update puede seguir descargándola en segundo plano. Revisá Configuración › Windows Update";
                        Log.Warn("Windows Update: " + c.Name + ": " + c.Result);
                        changed(c);
                        continue;
                    }
                    int drc = SystemCollectors.ToInt(Com.TryGet(dres, "ResultCode"));
                    if (drc != 2 && drc != 3)
                    {
                        c.Status = UpdateStatus.Fallida;
                        int dhr = SystemCollectors.ToInt(Com.TryGet(dres, "HResult"));
                        string dhint = InstallErrorHint(dhr);
                        c.Result = "Falló la descarga (" + SystemCollectors.WuResult(drc) + ", " + Fmt.Hex(dhr) + ")" + (dhint.Length > 0 ? ": " + dhint : "");
                        changed(c);
                        continue;
                    }

                    c.Result = "Instalando…";
                    changed(c);
                    ctx.Report("Instalando: " + c.Name);
                    object installer = Com.Call(session, "CreateUpdateInstaller");
                    try { Com.Set(installer, "ForceQuiet", true); } catch { }
                    Com.SetRef(installer, "Updates", coll);
                    object ires = CallWithLimit(delegate { return Com.Call(installer, "Install"); }, InstallLimit, out slow);
                    if (slow)
                    {
                        busyWith = c.Name;
                        c.Status = UpdateStatus.Pendiente;
                        c.Result = "La instalación sigue después de " + Fmt.Duration(InstallLimit) + ": Windows Update la termina en segundo plano. Revisá Configuración › Windows Update y reiniciá cuando lo pida";
                        Log.Warn("Windows Update: " + c.Name + ": " + c.Result);
                        changed(c);
                        continue;
                    }
                    object ur = Com.Call(ires, "GetUpdateResult", 0);
                    int irc = SystemCollectors.ToInt(Com.TryGet(ur, "ResultCode"));
                    object reboot = Com.TryGet(ur, "RebootRequired");
                    bool needsReboot = reboot is bool && (bool)reboot;
                    if (irc == 2 || irc == 3)
                    {
                        c.Status = needsReboot ? UpdateStatus.RequiereReinicio : UpdateStatus.Instalada;
                        c.Result = (needsReboot ? "Instalada; requiere reiniciar" : "Instalada correctamente") +
                                   (irc == 3 ? ". Windows Update informó errores en alguna de sus partes: volvé a buscar actualizaciones después" : "");
                        if (c.Kind == UpdateKind.Controlador) ConfirmDriver(c, needsReboot);
                        else c.InstalledVersion = "Instalada";
                    }
                    else
                    {
                        c.Status = UpdateStatus.Fallida;
                        int hr = SystemCollectors.ToInt(Com.TryGet(ur, "HResult"));
                        string hint = InstallErrorHint(hr);
                        c.Result = SystemCollectors.WuResult(irc) + " (" + Fmt.Hex(hr) + ")" + (hint.Length > 0 ? ": " + hint : "");
                    }
                }
                catch (Exception ex)
                {
                    c.Status = UpdateStatus.Fallida;
                    c.Result = DescribeWu(ex);
                    Log.Error("Windows Update: " + c.Name, ex);
                }
                changed(c);
            }
        }

        /// <summary>Plazo de la descarga de UNA actualización de Windows Update.</summary>
        public static TimeSpan DownloadLimit = TimeSpan.FromMinutes(90);
        /// <summary>Plazo de la instalación de UNA actualización (las acumulativas tardan, sobre todo con disco rígido).</summary>
        public static TimeSpan InstallLimit = TimeSpan.FromMinutes(120);

        /// <summary>
        /// Ejecuta una llamada de Windows Update con plazo. Download e Install no tienen plazo propio: con el
        /// Agente de Windows Update trabado (pasa en algunos equipos), la fila quedaba «Descargando…» para siempre y
        /// el resto de la tanda no empezaba. Si se vence, se deja de esperar (Windows puede seguir en segundo plano).
        /// Las excepciones de la llamada se propagan.
        /// </summary>
        internal static object CallWithLimit(Func<object> call, TimeSpan limit, out bool timedOut)
        {
            object result = null;
            Exception error = null;
            var t = new Thread(delegate()
            {
                try { result = call(); }
                catch (Exception ex) { error = ex; }
            });
            t.IsBackground = true;
            t.Name = "windows-update";
            t.Start();
            timedOut = !t.Join(limit);
            if (timedOut) return null;
            if (error != null) throw error;
            return result;
        }

        /// <summary>
        /// Después de instalar un controlador de Windows Update, la versión que usa el dispositivo se lee del
        /// registro (como con el catálogo) en vez de suponer la ofrecida. Si todavía es la anterior, el
        /// dispositivo la toma al reiniciar.
        /// </summary>
        static void ConfirmDriver(UpdateCandidate c, bool needsReboot)
        {
            var tag = c.ProviderTag as DriverTag;
            string now = "";
            if (tag != null && tag.Installed != null && !string.IsNullOrEmpty(tag.Installed.DeviceId))
            {
                for (int i = 0; i < 4; i++)
                {
                    var reg = DriverRegistry.Read(tag.Installed.DeviceId);
                    now = reg != null ? reg.Version ?? "" : "";
                    if (VersionUtil.Parse(now) == null || VersionUtil.Parse(tag.Offered) == null || !VersionUtil.IsNewer(tag.Offered, now)) break;
                    if (DriverRegistry.ReadForTests != null) break;
                    Thread.Sleep(1500);
                }
            }
            c.InstalledVersion = DriverVerdictAfterInstall(tag != null ? tag.Offered : "", now, c.AvailableVersion);
            // 2.0.27: sin dispositivo con controlador al que compararlo, se anota: si Windows Update lo vuelve a ofrecer, no lo tomó nadie.
            if (tag != null && tag.Installed == null && !string.IsNullOrEmpty(c.Identifier)) MachineMemory.Put(MachineMemory.WuDriverKey(c.Identifier, tag.Offered), "");
            if (DriverStillOld(tag != null ? tag.Offered : "", now))
            {
                // 2.0.25: se anota; la búsqueda siguiente confirma si después de reiniciar el dispositivo la usa.
                if (tag != null && tag.Installed != null) DriverFollowUp.Record(tag.Installed.DeviceId, tag.Offered);
                c.Status = UpdateStatus.RequiereReinicio;
                c.Result = "Instalado por Windows Update; el dispositivo todavía usa la " + now + ": toma la nueva después de reiniciar el equipo (" + DriverFollowUp.RebootHint + ")";
            }
        }

        /// <summary>Versión para la columna «Instalada» después de instalar: la del registro si se sabe; si no, la ofrecida.</summary>
        public static string DriverVerdictAfterInstall(string offered, string registryNow, string availableText)
        {
            if (VersionUtil.Parse(registryNow) != null && !DriverStillOld(offered, registryNow)) return registryNow;
            return availableText ?? offered ?? "";
        }

        /// <summary>¿El registro todavía informa una versión anterior a la que instaló Windows Update?</summary>
        public static bool DriverStillOld(string offered, string registryNow)
        {
            return VersionUtil.Parse(offered) != null && VersionUtil.Parse(registryNow) != null && VersionUtil.IsNewer(offered, registryNow);
        }

        // ------------------------------------------------------------------ Microsoft Update (Office MSI)

        /// <summary>Servicio «Microsoft Update»: Windows Update más las actualizaciones de Office y otros productos.</summary>
        public const string MicrosoftUpdateServiceId = "7971f918-a847-4430-9279-4a52d1efe18d";

        /// <summary>Marca de la fila que activa Microsoft Update.</summary>
        public sealed class MicrosoftUpdateActivation { }

        /// <summary>Sólo para las pruebas: reemplaza la consulta del servicio (registrado, registrado con Actualizaciones automáticas).</summary>
        internal static Func<KeyValuePair<bool, bool>> MicrosoftUpdateStateForTests;

        /// <summary>El primer Office instalado con MSI, o null.</summary>
        public static InstalledProgram FindOfficeMsi(List<InstalledProgram> programs)
        {
            if (programs == null) return null;
            foreach (var p in programs) if (OfficeC2R.IsOfficeMsi(p)) return p;
            return null;
        }

        /// <summary>¿Está registrado Microsoft Update? (Key: registrado; Value: con Actualizaciones automáticas.)</summary>
        static KeyValuePair<bool, bool> MicrosoftUpdateState()
        {
            if (MicrosoftUpdateStateForTests != null) return MicrosoftUpdateStateForTests();
            try
            {
                object sm = Com.Create("Microsoft.Update.ServiceManager");
                foreach (object svc in Com.Items(Com.Get(sm, "Services"), 0))
                {
                    if (!MicrosoftUpdateServiceId.Equals(Com.Str(svc, "ServiceID"), StringComparison.OrdinalIgnoreCase)) continue;
                    object au = Com.TryGet(svc, "IsRegisteredWithAU");
                    return new KeyValuePair<bool, bool>(true, au is bool && (bool)au);
                }
            }
            catch (Exception ex) { Log.Warn("Servicios de Windows Update: " + Wmi.Describe(ex)); }
            return new KeyValuePair<bool, bool>(false, false);
        }

        /// <summary>
        /// Fila que activa Microsoft Update (lo mismo que «Recibir actualizaciones de otros productos de
        /// Microsoft» en Windows Update › Opciones avanzadas). Sin eso, Office MSI no recibe parches.
        /// </summary>
        public static UpdateCandidate MicrosoftUpdateCandidate(UpdateContext ctx, InstalledProgram office)
        {
            var c = new UpdateCandidate();
            c.Kind = UpdateKind.WindowsUpdate;
            c.Name = "Microsoft Update: actualizaciones de Office y otros productos de Microsoft";
            c.Publisher = "Microsoft";
            c.InstalledVersion = "Desactivado";
            c.AvailableVersion = "Activar";
            c.Source = "Windows Update";
            c.Identifier = "microsoft-update";
            c.ProviderName = ProviderId;
            c.Status = UpdateStatus.Disponible;
            c.Installable = ctx != null && ctx.IsAdmin;
            c.Recommended = c.Installable;
            c.Detail = "«" + (office != null ? office.Name : "Office") + "» está instalado con Windows Installer (MSI) y sus parches sólo llegan por Microsoft Update, " +
                       "que en este equipo está desactivado. Se activa lo mismo que «Recibir actualizaciones de otros productos de Microsoft» " +
                       "(Windows Update › Opciones avanzadas); después volvé a buscar y van a aparecer las de Office." +
                       (c.Installable ? "" : " Requiere ejecutar Actualizador como administrador.");
            c.ProviderTag = new MicrosoftUpdateActivation();
            return c;
        }

        /// <summary>Registra Microsoft Update con las Actualizaciones automáticas (AddService2 con los tres permisos).</summary>
        static void EnableMicrosoftUpdate(UpdateContext ctx, UpdateCandidate c)
        {
            c.Status = UpdateStatus.Instalando;
            ctx.Report("Activando Microsoft Update (actualizaciones de otros productos de Microsoft)…");
            try
            {
                object sm = Com.Create("Microsoft.Update.ServiceManager");
                try { Com.Set(sm, "ClientApplicationID", AppInfo.Name); } catch { }
                // asfAllowPendingRegistration | asfAllowOnlineRegistration | asfRegisterServiceWithAU
                Com.Call(sm, "AddService2", MicrosoftUpdateServiceId, 7, "");
                if (ctx.Os != null && ctx.Os.IsWindows10OrLater)
                {
                    // Para que la opción de Windows Update › Opciones avanzadas figure activada.
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Reg.Native))
                        using (var k = root.CreateSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings"))
                            if (k != null) k.SetValue("AllowMUUpdateService", 1, RegistryValueKind.DWord);
                    }
                    catch (Exception ex) { Log.Warn("AllowMUUpdateService: " + ex.Message); }
                }
                c.Status = UpdateStatus.Instalada;
                c.InstalledVersion = "Activado";
                c.Result = "Microsoft Update quedó activado: volvé a buscar actualizaciones para ver las de Office";
            }
            catch (Exception ex)
            {
                c.Status = UpdateStatus.Fallida;
                c.Result = "No se pudo activar Microsoft Update: " + DescribeWu(ex) + ". Activalo en Windows Update › Opciones avanzadas › «Recibir actualizaciones de otros productos de Microsoft»";
                Log.Error("Microsoft Update", ex);
            }
        }

        /// <summary>
        /// Busca en Microsoft Update las actualizaciones de software que no vinieron en la búsqueda normal (las de
        /// Office MSI) y las agrega. Devuelve cuántas agregó. Un error no corta la búsqueda principal.
        /// </summary>
        int SearchMicrosoftUpdate(UpdateContext ctx, HashSet<string> seen, List<UpdateCandidate> output, string source, bool optedIn, List<string> notes)
        {
            ctx.Report("Microsoft Update: buscando actualizaciones de Office…");
            object result;
            try { result = SearchWithTimeout(ctx, "IsInstalled=0 and IsHidden=0 and Type='Software'", TimeSpan.FromMinutes(20), MicrosoftUpdateServiceId); }
            catch (OperationCanceledByUserException) { throw; }
            catch (Exception ex)
            {
                notes.Add("No se pudo buscar en Microsoft Update (actualizaciones de Office): " + DescribeWu(ex) + ".");
                return 0;
            }
            int added = 0;
            foreach (object u in Com.Items(Com.Get(result, "Updates"), 0))
            {
                ctx.ThrowIfCancelled();
                string id = Com.Str(Com.TryGet(u, "Identity"), "UpdateID");
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                var c = SoftwareCandidate(u, "Microsoft Update", false, optedIn);
                output.Add(c);
                added++;
            }
            return added;
        }

        public static string DescribeWu(Exception ex)
        {
            uint code = unchecked((uint)Com.HResultOf(ex));
            while (ex.InnerException != null && !(ex is System.Runtime.InteropServices.COMException)) ex = ex.InnerException;
            if (code == 0) return ex.Message;
            switch (code)
            {
                case 0x8024402C: return "Sin conexión con el servidor de Windows Update (DNS/proxy)";
                case 0x80244022: return "Servicio de Windows Update no disponible temporalmente (503)";
                case 0x8024001E: return "El servicio se está deteniendo o reiniciando";
                case 0x80240016: return "Hay otra instalación en curso";
                case 0x8024A105: return "Windows Update ya está trabajando; reintentá en unos minutos";
                case 0x80070422: return "El servicio Windows Update está deshabilitado";
                case 0x80070005: return "Acceso denegado (ejecutá como administrador)";
                case 0x8024000B: return "Operación cancelada";
                case 0x80240024: return "No hay actualizaciones para instalar";
                case 0x80072EE2: return "Tiempo de espera agotado de la conexión";
                case 0x80072EFD: return "No se puede conectar a Internet";
                default: return "Windows Update " + Fmt.Hex(unchecked((int)code)) + ": " + ex.Message.Trim();
            }
        }
    }
}
