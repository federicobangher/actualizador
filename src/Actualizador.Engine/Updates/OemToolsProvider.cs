// Herramientas del fabricante del equipo: BIOS, firmware y controladores del modelo.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Actualizador.Core;
using Actualizador.Inventory;

namespace Actualizador.Updates
{
    public enum OemVendor { Ninguno, Dell, Lenovo, HP }

    /// <summary>Una actualización que encontró Dell Command | Update.</summary>
    public sealed class DcuUpdate
    {
        public string Name = "", Version = "", Date = "", Urgency = "", Type = "", Category = "", Size = "";
    }

    sealed class OemPlan
    {
        public OemVendor Vendor;
        public string Tool;
        public readonly List<DcuUpdate> Updates = new List<DcuUpdate>();
    }

    /// <summary>
    /// Las actualizaciones que el fabricante del equipo publica para ESE modelo (BIOS/UEFI, firmware, controladores
    /// probados con el equipo). Windows Update y el Catálogo de Microsoft Update no traen casi nunca la BIOS.
    /// <list type="bullet">
    /// <item><b>Dell</b>: con Dell Command | Update instalado, se busca con su consola (<c>dcu-cli /scan</c>) y se
    /// instala con <c>dcu-cli /applyUpdates -silent -reboot=disable</c> (el equipo nunca se reinicia solo). Sólo
    /// como administrador, sólo desde Program Files y sólo si la firma digital es válida y de Dell.</item>
    /// <item><b>Lenovo</b>: con Lenovo System Update instalado, la acción lo abre para que el usuario confirme ahí.</item>
    /// <item><b>HP</b> (y Lenovo/Dell sin su herramienta): se indica la herramienta oficial y su sitio.</item>
    /// </list>
    /// Nunca queda marcado de entrada: una BIOS se instala sólo si el usuario lo elige.
    /// </summary>
    public sealed class OemToolsProvider : IUpdateProvider
    {
        public const string ProviderId = "fabricante_equipo";
        public string Name { get { return ProviderId; } }
        public bool Interactive;

        /// <summary>Para las pruebas: fabricante y ejecución de la herramienta simulados.</summary>
        internal Func<string> ManufacturerForTests;
        internal Func<string, string, int, ProcResult> RunForTests;
        internal Func<string, string> ToolForTests;

        public string Unavailable(UpdateContext ctx)
        {
            if (ManufacturerForTests != null) return null;
            if (ctx.Os == null || !ctx.Os.IsWindows || ctx.Os.IsServer) return "Las herramientas de los fabricantes de equipos son para Windows de escritorio.";
            if (ctx.Options != null && !ctx.Options.QueryVendors)
                return "La consulta a los fabricantes está desactivada (Opciones o --sin-fabricantes): la BIOS y los controladores del fabricante del equipo no se comparan.";
            return null;
        }

        public static OemVendor Classify(string manufacturer)
        {
            string m = (manufacturer ?? "").Trim().ToLowerInvariant();
            if (m.StartsWith("dell") || m.StartsWith("alienware")) return OemVendor.Dell;
            if (m.StartsWith("lenovo")) return OemVendor.Lenovo;
            if (m == "hp" || m.StartsWith("hp ") || m.StartsWith("hp inc") || m.StartsWith("hewlett")) return OemVendor.HP;
            return OemVendor.Ninguno;
        }

        static readonly string[] DellSigners = { "Dell Inc", "Dell Technologies Inc", "Dell Products L.P." };
        static readonly string[] LenovoSigners = { "Lenovo", "Lenovo (Beijing) Limited", "Lenovo Group Limited" };

        string Manufacturer()
        {
            if (ManufacturerForTests != null) return ManufacturerForTests();
            string err;
            foreach (var o in Wmi.TryQuery(Wmi.Cimv2, "SELECT Manufacturer FROM Win32_ComputerSystem", out err, 30)) return Wmi.S(o, "Manufacturer");
            if (err != null) Log.Warn("Fabricante del equipo: " + err);
            return null;
        }

        /// <summary>La herramienta, sólo si está en Program Files (que no escribe un usuario común) y existe.</summary>
        string FindTool(OemVendor v)
        {
            if (ToolForTests != null) return ToolForTests(v.ToString());
            string pf = SafePaths.ProgramFiles;
            string pf86 = pf + " (x86)";
            var candidates = new List<string>();
            if (v == OemVendor.Dell)
            {
                candidates.Add(Path.Combine(pf, @"Dell\CommandUpdate\dcu-cli.exe"));
                candidates.Add(Path.Combine(pf86, @"Dell\CommandUpdate\dcu-cli.exe"));
            }
            else if (v == OemVendor.Lenovo)
            {
                candidates.Add(Path.Combine(pf86, @"Lenovo\System Update\tvsu.exe"));
                candidates.Add(Path.Combine(pf, @"Lenovo\System Update\tvsu.exe"));
            }
            foreach (string c in candidates)
                if (File.Exists(c) && !SafePaths.IsUserWritableLocation(Path.GetDirectoryName(c))) return c;
            return null;
        }

        /// <summary>null si la firma es válida y del fabricante; si no, el motivo.</summary>
        string CheckTool(string file, string[] signers, string vendor)
        {
            if (ToolForTests != null) return null;
            string note;
            int rc = Native.VerifyAuthenticodeStrict(file, out note);
            if (rc == Native.CERT_E_REVOKED) return "el certificado que firma " + Path.GetFileName(file) + " fue revocado";
            if (rc != 0) return "la firma digital de " + Path.GetFileName(file) + " no es válida (" + Fmt.Hex(rc) + ")";
            string subject;
            try { subject = Native.SignerSubject(file); }
            catch (Exception ex) { return "no se pudo leer el firmante de " + Path.GetFileName(file) + ": " + ex.Message; }
            if (!InstallerRules.SignerAccepted(subject, signers)) return Path.GetFileName(file) + " está firmado por «" + subject + "», no por " + vendor;
            return null;
        }

        ProcResult Run(string file, string args, int timeoutMs)
        {
            if (RunForTests != null) return RunForTests(file, args, timeoutMs);
            return ProcessRunner.Run(file, args, timeoutMs);
        }

        public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes)
        {
            var vendor = Classify(Manufacturer());
            if (vendor == OemVendor.Ninguno) return;
            ctx.Report("Fabricante del equipo (" + vendor + "): BIOS, firmware y controladores del modelo…");
            if (vendor == OemVendor.Dell) ScanDell(ctx, output);
            else if (vendor == OemVendor.Lenovo) ScanLenovo(output);
            else output.Add(Row("HP: BIOS, firmware y controladores de este modelo", UpdateStatus.NoVerificable, false,
                "HP publica la BIOS, el firmware y los controladores probados con cada modelo en HP Support Assistant (equipos de hogar) y HP Image Assistant (empresas). " +
                "Windows Update casi nunca trae la BIOS: revisalos con esa herramienta.", "https://support.hp.com/"));
        }

        static UpdateCandidate Row(string name, UpdateStatus st, bool installable, string detail, string url)
        {
            var c = new UpdateCandidate();
            c.Kind = UpdateKind.Controlador;
            c.Name = name;
            c.Status = st;
            c.Installable = installable;
            c.Recommended = false;      // una BIOS se instala sólo si el usuario la elige
            c.Detail = detail;
            c.InfoUrl = url;
            c.ProviderName = ProviderId;
            c.Source = "Fabricante del equipo";
            c.Identifier = "fabricante:" + name;
            return c;
        }

        // ------------------------------------------------------------------ Dell

        void ScanDell(UpdateContext ctx, List<UpdateCandidate> output)
        {
            const string url = "https://www.dell.com/support/";
            string tool = FindTool(OemVendor.Dell);
            if (tool == null)
            {
                output.Add(Row("Dell: BIOS, firmware y controladores de este modelo", UpdateStatus.NoVerificable, false,
                    "Dell Command | Update no está instalado. Es la herramienta oficial de Dell para la BIOS, el firmware y los controladores probados con este modelo " +
                    "(Windows Update casi nunca trae la BIOS). Instalala desde el sitio de soporte de Dell (o con winget: Dell.CommandUpdate) y Actualizador la va a usar.", url));
                return;
            }
            var row = Row("Dell Command | Update: BIOS, firmware y controladores de este modelo", UpdateStatus.Error, false, "", url);
            row.Source = "Dell Command | Update";
            row.Identifier = "fabricante:dell";
            output.Add(row);
            if (!ctx.IsAdmin)
            {
                row.Status = UpdateStatus.NoVerificable;
                row.Detail = "Dell Command | Update sólo consulta como administrador: tocá «Ejecutar como administrador» y volvé a buscar.";
                return;
            }
            string bad = CheckTool(tool, DellSigners, "Dell");
            if (bad != null) { row.Detail = "No se usa Dell Command | Update: " + bad + "."; return; }
            string dir = null;
            try
            {
                dir = SafePaths.CreatePrivateDirectory(Path.Combine(AppInfo.DataDirectory, "descargas"));
                var r = Run(tool, "/scan -silent -report=" + ProcessRunner.QuoteArg(dir.TrimEnd('\\')), 20 * 60 * 1000);
                Log.Info("dcu-cli /scan: código " + r.ExitCode);
                if (r.ExitCode == 500)
                {
                    row.Status = UpdateStatus.AlDia;
                    row.Detail = "Dell Command | Update no encontró actualizaciones para este equipo.";
                    return;
                }
                string report = Path.Combine(dir, "DCUApplicableUpdates.xml");
                if (r.ExitCode != 0 || !File.Exists(report))
                {
                    row.Detail = "Dell Command | Update no pudo buscar: " + DcuExitText(r.ExitCode) + ".";
                    return;
                }
                var plan = new OemPlan();
                plan.Vendor = OemVendor.Dell;
                plan.Tool = tool;
                plan.Updates.AddRange(ParseDcuReport(File.ReadAllText(report, Encoding.UTF8)));
                if (plan.Updates.Count == 0)
                {
                    row.Status = UpdateStatus.AlDia;
                    row.Detail = "Dell Command | Update no encontró actualizaciones para este equipo.";
                    return;
                }
                FillDell(row, plan);
            }
            catch (Exception ex) { row.Detail = "Dell Command | Update no pudo buscar: " + ex.Message; }
            finally { if (dir != null) try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>La fila con lo que encontró Dell Command | Update (se instala todo junto, como lo hace la herramienta).</summary>
        internal static void FillDell(UpdateCandidate row, OemPlan plan)
        {
            row.Status = UpdateStatus.Disponible;
            row.Installable = true;
            row.ProviderTag = plan;
            var tipos = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var nombres = new List<string>();
            bool bios = false;
            foreach (var u in plan.Updates)
            {
                string t = TypeText(u.Type);
                int n; tipos.TryGetValue(t, out n); tipos[t] = n + 1;
                if (t == "BIOS") bios = true;
                nombres.Add(u.Name + (u.Version.Length > 0 ? " " + u.Version : "") + (u.Urgency.Length > 0 ? " (" + UrgencyText(u.Urgency) + ")" : ""));
            }
            var partes = new List<string>();
            foreach (var kv in tipos) partes.Add(kv.Value + " " + kv.Key);
            row.AvailableVersion = Fmt.Plural(plan.Updates.Count, "actualización", "actualizaciones");
            row.InstalledVersion = "";
            row.Detail = "Dell Command | Update encontró " + Fmt.JoinY(partes) + ": " + string.Join("; ", nombres.ToArray()) + ". " +
                         "Se instalan todas juntas con Dell Command | Update, sin reiniciar el equipo" + (bios ? "; la BIOS se aplica al reiniciar (no apagues el equipo mientras tanto)" : "") + ".";
        }

        static string TypeText(string t)
        {
            switch ((t ?? "").Trim().ToLowerInvariant())
            {
                case "bios": return "BIOS";
                case "firmware": return "firmware";
                case "driver": return "controladores";
                case "application": case "utility": return "aplicaciones de Dell";
                default: return string.IsNullOrEmpty(t) ? "otras" : t;
            }
        }

        static string UrgencyText(string u)
        {
            switch ((u ?? "").Trim().ToLowerInvariant())
            {
                case "urgent": case "critical": return "urgente";
                case "recommended": return "recomendada";
                case "optional": return "opcional";
                default: return u;
            }
        }

        static readonly Regex UpdateRx = new Regex(@"<update\b[^>]*>(?<b>.*?)</update>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static string Tag(string block, string name)
        {
            var m = Regex.Match(block, "<" + name + @"\b[^>]*>(?<v>.*?)</" + name + ">", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value).Trim() : "";
        }

        /// <summary>El informe de <c>dcu-cli /scan -report=…</c> (DCUApplicableUpdates.xml).</summary>
        public static List<DcuUpdate> ParseDcuReport(string xml)
        {
            var list = new List<DcuUpdate>();
            foreach (Match m in UpdateRx.Matches(xml ?? ""))
            {
                string b = m.Groups["b"].Value;
                var u = new DcuUpdate();
                u.Name = Tag(b, "name"); u.Version = Tag(b, "version"); u.Date = Tag(b, "date"); u.Urgency = Tag(b, "urgency");
                u.Type = Tag(b, "type"); u.Category = Tag(b, "category"); u.Size = Tag(b, "size");
                if (u.Name.Length > 0) list.Add(u);
            }
            return list;
        }

        /// <summary>Códigos de salida de dcu-cli (los documentados por Dell para la consola de Dell Command | Update).</summary>
        public static string DcuExitText(int code)
        {
            switch (code)
            {
                case 0: return "terminó bien";
                case 1: return "terminó bien; hay que reiniciar el equipo para completar";
                case 2: return "error inesperado de Dell Command | Update (código 2)";
                case 3: return "error de Dell Command | Update (código 3)";
                case 4: return "Dell Command | Update dice que este equipo no es compatible (código 4)";
                case 5: return "hay que reiniciar el equipo y volver a buscar (código 5)";
                case 500: return "no hay actualizaciones para este equipo";
                case 501: return "no pudo determinar las actualizaciones disponibles (código 501)";
                case 502: return "se canceló (código 502)";
                case 503: return "no pudo descargar un archivo (código 503)";
                default: return "código " + code + " (el detalle está en el registro de Dell Command | Update)";
            }
        }

        // ------------------------------------------------------------------ Lenovo

        void ScanLenovo(List<UpdateCandidate> output)
        {
            const string url = "https://support.lenovo.com/";
            string tool = FindTool(OemVendor.Lenovo);
            if (tool == null)
            {
                output.Add(Row("Lenovo: BIOS, firmware y controladores de este modelo", UpdateStatus.NoVerificable, false,
                    "Lenovo publica la BIOS, el firmware y los controladores probados con cada modelo en Lenovo Vantage (Microsoft Store) o Lenovo System Update. " +
                    "Windows Update casi nunca trae la BIOS: revisalos con esa herramienta.", url));
                return;
            }
            var row = Row("Lenovo System Update: BIOS, firmware y controladores de este modelo", UpdateStatus.Pendiente, true,
                "Lenovo System Update busca e instala lo que Lenovo publica para este modelo. La acción lo abre para que elijas y confirmes ahí " +
                "(Lenovo no ofrece una consulta silenciosa sin configurarla antes).", url);
            row.Source = "Lenovo System Update";
            row.Identifier = "fabricante:lenovo";
            var plan = new OemPlan();
            plan.Vendor = OemVendor.Lenovo;
            plan.Tool = tool;
            row.ProviderTag = plan;
            output.Add(row);
        }

        // ------------------------------------------------------------------ Instalación

        public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed)
        {
            foreach (var c in items)
            {
                if (ctx.CancelRequested) { c.Status = UpdateStatus.Pendiente; c.Result = InstallTally.CancelledBeforeStart; changed(c); continue; }
                var plan = c.ProviderTag as OemPlan;
                if (plan == null) { c.Status = UpdateStatus.Fallida; c.Result = "No hay datos de la búsqueda: volvé a buscar actualizaciones"; changed(c); continue; }
                c.Status = UpdateStatus.Instalando; c.Result = "Instalando…"; changed(c);
                try
                {
                    if (plan.Vendor == OemVendor.Dell) InstallDell(ctx, c, plan);
                    else OpenLenovo(c, plan);
                }
                catch (Exception ex) { c.Status = UpdateStatus.Fallida; c.Result = ex.Message; }
                changed(c);
            }
        }

        void InstallDell(UpdateContext ctx, UpdateCandidate c, OemPlan plan)
        {
            if (!ctx.IsAdmin) { c.Status = UpdateStatus.Fallida; c.Result = "Dell Command | Update sólo instala como administrador"; return; }
            string bad = CheckTool(plan.Tool, DellSigners, "Dell");
            if (bad != null) { c.Status = UpdateStatus.Fallida; c.Result = "No se usa Dell Command | Update: " + bad; return; }
            string log = Path.Combine(AppInfo.LogDirectory, "dell-command-update-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
            ctx.Report("Dell Command | Update: instalando " + c.AvailableVersion + " (puede tardar varios minutos)…");
            var r = Run(plan.Tool, "/applyUpdates -silent -reboot=disable -outputLog=" + ProcessRunner.QuoteArg(log), 2 * 60 * 60 * 1000);
            Log.Info("dcu-cli /applyUpdates: código " + r.ExitCode);
            ApplyDellResult(c, r.ExitCode, log);
        }

        internal static void ApplyDellResult(UpdateCandidate c, int code, string log)
        {
            switch (code)
            {
                case 0: c.Status = UpdateStatus.Instalada; c.Result = "Dell Command | Update instaló " + c.AvailableVersion; break;
                case 1:
                case 5: c.Status = UpdateStatus.RequiereReinicio; c.Result = "Dell Command | Update instaló " + c.AvailableVersion + ": " + DcuExitText(code); break;
                case 500: c.Status = UpdateStatus.AlDia; c.Result = "Dell Command | Update no encontró nada para instalar (ya estaba al día)"; break;
                default:
                    c.Status = UpdateStatus.Fallida;
                    c.Result = "Dell Command | Update: " + DcuExitText(code) + (string.IsNullOrEmpty(log) ? "" : ". Registro: " + log);
                    break;
            }
        }

        void OpenLenovo(UpdateCandidate c, OemPlan plan)
        {
            string bad = CheckTool(plan.Tool, LenovoSigners, "Lenovo");
            if (bad != null) { c.Status = UpdateStatus.Fallida; c.Result = "No se abre Lenovo System Update: " + bad; return; }
            if (RunForTests == null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(plan.Tool);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(plan.Tool);
                using (System.Diagnostics.Process.Start(psi)) { }
            }
            c.Status = UpdateStatus.Pendiente;
            c.Result = "Se abrió Lenovo System Update: elegí y confirmá ahí lo que quieras instalar. Después volvé a buscar actualizaciones.";
        }
    }
}
