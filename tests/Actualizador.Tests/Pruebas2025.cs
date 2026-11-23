// Pruebas de la 2.0.25. Pedido del usuario (23/11/2026): «los drivers de AMD (ATI, Vega…) no instalan bien en algunos equipos: dan
// errores, o dicen que instala y cuando reinicio la PC sigue como que no se actualizó».
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Actualizador.Core;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        const string VegaHwid = @"PCI\VEN_1002&DEV_15D8&SUBSYS_8615103C&REV_C1";
        const string VegaDevice = @"PCI\VEN_1002&DEV_15D8&SUBSYS_8615103C&REV_C1\4&2B6A1C3&0&0041";

        static DriverInfo Vega2025(string version)
        {
            var d = Controlador("AMD Radeon(TM) Vega 8 Graphics", "Advanced Micro Devices, Inc.", "Display", version, VegaHwid);
            d.DeviceId = VegaDevice;
            d.Date = new DateTime(2023, 3, 1);
            return d;
        }

        /// <summary>Una página de resultados del catálogo con estas filas (guid, título, productos, fecha, versión).</summary>
        static string Catalogo2025(params string[][] filas)
        {
            var sb = new StringBuilder("<html><body><table id=\"ctl00_catalogBody_updateMatches\">");
            for (int i = 0; i < filas.Length; i++)
            {
                string g = filas[i][0];
                sb.Append("<tr id=\"").Append(g).Append("_R").Append(i).Append("\">");
                sb.Append("<td id=\"").Append(g).Append("_C0_R").Append(i).Append("\"></td>");
                sb.Append("<td id=\"").Append(g).Append("_C1_R").Append(i).Append("\"><a>").Append(filas[i][1]).Append("</a></td>");
                sb.Append("<td id=\"").Append(g).Append("_C2_R").Append(i).Append("\">").Append(filas[i][2]).Append("</td>");
                sb.Append("<td id=\"").Append(g).Append("_C3_R").Append(i).Append("\">Drivers (Display)</td>");
                sb.Append("<td id=\"").Append(g).Append("_C4_R").Append(i).Append("\">").Append(filas[i][3]).Append("</td>");
                sb.Append("<td id=\"").Append(g).Append("_C5_R").Append(i).Append("\">").Append(filas[i][4]).Append("</td>");
                sb.Append("<td id=\"").Append(g).Append("_C6_R").Append(i).Append("\"><span id=\"").Append(g).Append("_size\">612.0 MB</span></td>");
                sb.Append("</tr>");
            }
            return sb.Append("</table></body></html>").ToString();
        }

        const string GuidDisplay2025 = "aaaaaaaa-1111-4111-8111-000000000001";
        const string GuidExtension2025 = "aaaaaaaa-1111-4111-8111-000000000002";

        static List<UpdateCandidate> EscanearVega2025(DriverInfo vega, List<InstalledProgram> programas)
        {
            var ctx = new UpdateContext();
            ctx.Os = new OsInfo(); ctx.Os.NativeArchitecture = "x64"; ctx.Os.Major = 10; ctx.Os.Build = 19045; ctx.Os.IsWindows = true;
            ctx.IsAdmin = true;
            ctx.Drivers.Add(vega);
            ctx.DriversLoaded = true;
            if (programas != null) ctx.Programs.AddRange(programas);
            ctx.ProgramsLoaded = true;
            // Por el chip: el controlador de video y una extensión con un número MÁS ALTO (así los publica AMD).
            string resultados = Catalogo2025(
                new[] { GuidDisplay2025, "Advanced Micro Devices, Inc. - Display - 31.0.21921.1000", "Windows 10 and later drivers", "6/2/2025", "31.0.21921.1000" },
                new[] { GuidExtension2025, "Advanced Micro Devices, Inc. - Extension - 31.0.24000.5000", "Windows 10 and later drivers", "7/1/2025", "31.0.24000.5000" });
            string vacia = Muestra("catalogo_sin_resultados.html");
            CatalogDriverProvider.FetchForTests = delegate(string url)
            {
                if (url.Contains("ScopedViewInline.aspx")) return "<div><span id=\"ScopedViewHandler_labelArchitecture_Separator\" class=\"labelTitle\">Architecture:</span> AMD64 </div>";
                if (url.EndsWith("q=" + Uri.EscapeDataString(@"PCI\VEN_1002&DEV_15D8"))) return resultados;
                return vacia;
            };
            try
            {
                var salida = new List<UpdateCandidate>();
                new CatalogDriverProvider().Scan(ctx, salida, new List<string>());
                return salida;
            }
            finally { CatalogDriverProvider.FetchForTests = null; }
        }

        /// <summary>La extensión no es el controlador: elegirla dejaba el mismo controlador y «se usa después de reiniciar» para siempre.</summary>
        static void TestExtensionNoEsElControlador2025()
        {
            Eq("Extension", CatalogRules.TitleClass("Advanced Micro Devices, Inc. - Extension - 31.0.24000.5000"), "clase del título: Extension");
            Eq("Display", CatalogRules.TitleClass("Advanced Micro Devices, Inc. - Display - 31.0.21921.1000"), "clase del título: Display");
            Eq("System", CatalogRules.TitleClass("Hewlett-Packard - System - 1.0.0.5"), "un guion dentro del nombre del fabricante no confunde");
            Eq("", CatalogRules.TitleClass("Advanced Micro Devices, Inc. driver update for AMD Radeon(TM) R7 Graphics"), "título viejo, sin clase");
            var vega = Vega2025("27.20.21034.37");
            var ext = new CatalogEntry { Title = "Advanced Micro Devices, Inc. - Extension - 31.0.24000.5000" };
            var comp = new CatalogEntry { Title = "Advanced Micro Devices, Inc. - SoftwareComponent - 31.0.24000.5000" };
            Check(CatalogRules.IsComponentFor(ext, vega) && CatalogRules.IsComponentFor(comp, vega), "para la placa de video, la extensión y el componente son complementos");
            var swc = Controlador("AMD Software Component", "Advanced Micro Devices, Inc.", "SoftwareComponent", "1.0.0.1", @"SWC\VEN_1002&AID_0001&SUBSYS_8615103C");
            Check(!CatalogRules.IsComponentFor(comp, swc), "un dispositivo que ES un componente sí compara componentes");

            var salida = EscanearVega2025(vega, null);
            var fila = salida.Find(delegate(UpdateCandidate c) { return c.Name.StartsWith("AMD Radeon"); });
            Check(fila != null && fila.Status == UpdateStatus.Disponible && fila.AvailableVersion == "31.0.21921.1000",
                  "se ofrece el controlador de video (31.0.21921.1000), no la extensión más alta: " + (fila != null ? fila.AvailableVersion : "sin fila"));
        }

        /// <summary>Con AMD Software (Adrenalin) instalado, el video se actualiza con AMD Software y no suelto desde el catálogo.</summary>
        static void TestConjuntoDeVideoAmd2025()
        {
            var adrenalin = new InstalledProgram { Name = "AMD Software", Version = "24.9.1", Publisher = "Advanced Micro Devices, Inc." };
            Check(AmdGraphics.SuiteInstalled(new List<InstalledProgram> { adrenalin }), "AMD Software");
            Check(AmdGraphics.SuiteInstalled(new List<InstalledProgram> { new InstalledProgram { Name = "AMD Software: Adrenalin Edition", Publisher = "Advanced Micro Devices, Inc." } }), "AMD Software: Adrenalin Edition");
            Check(AmdGraphics.SuiteInstalled(new List<InstalledProgram> { new InstalledProgram { Name = "AMD Radeon Settings", Publisher = "" } }), "Radeon Settings (Crimson), sin editor");
            Check(!AmdGraphics.SuiteInstalled(new List<InstalledProgram> {
                      new InstalledProgram { Name = "AMD Ryzen Master", Publisher = "Advanced Micro Devices, Inc." },
                      new InstalledProgram { Name = "AMD Chipset Software", Publisher = "Advanced Micro Devices, Inc." },
                      new InstalledProgram { Name = "AMD Software Helper", Publisher = "Otra empresa" } }), "Ryzen Master, Chipset Software u otro editor: no");
            Check(!AmdGraphics.SuiteInstalled(null), "sin programas");

            Check(AmdGraphics.IsGraphicsFamily(Vega2025("1.0")), "la placa de video (VEN_1002)");
            Check(AmdGraphics.IsGraphicsFamily(Controlador("AMD High Definition Audio Device", "Advanced Micro Devices", "MEDIA", "10.0.1.30", @"HDAUDIO\FUNC_01&VEN_1002&DEV_AA01&SUBSYS_00AA0100&REV_1008")), "su audio por HDMI");
            Check(AmdGraphics.IsGraphicsFamily(Controlador("AMD Crash Defender", "Advanced Micro Devices, Inc.", "System", "25.10.0.7", @"ROOT\AMDLOG")), "AMD Crash Defender");
            Check(!AmdGraphics.IsGraphicsFamily(Controlador("AMD SMBus", "Advanced Micro Devices, Inc", "System", "5.12.0.44", @"PCI\VEN_1022&DEV_790B&SUBSYS_8615103C&REV_61")), "el chipset (VEN_1022) no");

            var salida = EscanearVega2025(Vega2025("27.20.21034.37"), new List<InstalledProgram> { adrenalin });
            var fila = salida.Find(delegate(UpdateCandidate c) { return c.Name.StartsWith("AMD Radeon"); });
            Check(fila != null && fila.Status == UpdateStatus.AlDia && !fila.Installable && !fila.Recommended, "con AMD Software: no se ofrece suelto");
            Check(fila != null && fila.Detail.Contains("AMD Software 24.9.1") && fila.Detail.Contains("no es compatible") && fila.Detail.Contains("31.0.21921.1000"),
                  "explica por qué y cómo se actualiza: " + (fila != null ? fila.Detail : ""));

            // Windows Update: se muestra, pero no se marca sola y lo advierte.
            var wu = new UpdateCandidate { Kind = UpdateKind.Controlador, Status = UpdateStatus.Disponible, Installable = true, Recommended = true, Detail = "Versión más nueva que la instalada." };
            DriverFollowUp.StoreForTests = new Dictionary<string, string>();
            try { WindowsUpdateProvider.AdjustNewerDriver(wu, Vega2025("27.20.21034.37"), "31.0.21921.1000", null, adrenalin); }
            finally { DriverFollowUp.StoreForTests = null; }
            Check(wu.Status == UpdateStatus.Disponible && wu.Installable && !wu.Recommended && wu.Detail.Contains("AMD Software"), "Windows Update: se puede instalar, sin marcar, con la advertencia");
        }

        /// <summary>«Dice que instala y cuando reinicio sigue igual»: se recuerda, y después de reiniciar no se repite.</summary>
        static void TestPendienteDeReinicio2025()
        {
            DriverFollowUp.StoreForTests = new Dictionary<string, string>();
            DateTime? arranque = DateTime.UtcNow.AddHours(-3);      // el equipo arrancó antes de instalar
            DriverFollowUp.BootTimeForTests = delegate { return arranque; };
            try
            {
                DateTime at;
                Eq(DriverFollowUp.State.None, DriverFollowUp.Check(null, VegaDevice, "31.0.21921.1000", "27.20.21034.37", out at), "sin anotar: nada");
                DriverFollowUp.Record(VegaDevice, "31.0.21921.1000");
                Eq(DriverFollowUp.State.WaitingReboot, DriverFollowUp.Check(null, VegaDevice, "31.0.21921.1000", "27.20.21034.37", out at), "instalada y sin reiniciar: espera el reinicio");
                Check(at.Date == DateTime.Now.Date, "con la fecha de la instalación");
                Eq(DriverFollowUp.State.None, DriverFollowUp.Check(null, VegaDevice.ToLowerInvariant(), "31.0.21921.1000", "31.0.21921.1000", out at), "ya la usa: nada pendiente (sin importar mayúsculas)");
                Eq(DriverFollowUp.State.None, DriverFollowUp.Check(null, VegaDevice, "31.0.22000.1", "27.20.21034.37", out at), "otra versión: nada anotado");

                // Catálogo: no se vuelve a instalar mientras espera el reinicio.
                var salida = EscanearVega2025(Vega2025("27.20.21034.37"), null);
                var fila = salida.Find(delegate(UpdateCandidate c) { return c.Name.StartsWith("AMD Radeon"); });
                Check(fila != null && fila.Status == UpdateStatus.RequiereReinicio && !fila.Installable && fila.Detail.Contains("Reiniciar") && fila.Detail.Contains("Apagar"),
                      "catálogo, sin reiniciar: «requiere reinicio», sin instalar otra vez, y cómo reiniciar: " + (fila != null ? fila.Detail : ""));

                // Reinició y Windows siguió con la de antes.
                arranque = DateTime.UtcNow.AddMinutes(2);    // arrancó después de instalar (un minuto de margen)
                Eq(DriverFollowUp.State.NotApplied, DriverFollowUp.Check(null, VegaDevice, "31.0.21921.1000", "27.20.21034.37", out at), "reiniciado y sigue la vieja: Windows no la aplica");
                salida = EscanearVega2025(Vega2025("27.20.21034.37"), null);
                fila = salida.Find(delegate(UpdateCandidate c) { return c.Name.StartsWith("AMD Radeon"); });
                Check(fila != null && fila.Status == UpdateStatus.AlDia && !fila.Installable && fila.AvailableVersion == "27.20.21034.37" && fila.Detail.Contains("después de reiniciar"),
                      "catálogo, reiniciado: «Al día» con el motivo, no se vuelve a ofrecer: " + (fila != null ? fila.Detail : ""));

                // Windows Update: lo mismo.
                var wu = new UpdateCandidate { Kind = UpdateKind.Controlador, Status = UpdateStatus.Disponible, Installable = true, Recommended = true };
                WindowsUpdateProvider.AdjustNewerDriver(wu, Vega2025("27.20.21034.37"), "31.0.21921.1000", null, null);
                Check(wu.Status == UpdateStatus.AlDia && !wu.Installable && !wu.Recommended && wu.Detail.Contains("no la aplica"), "Windows Update, reiniciado: «Al día» con el motivo");
                arranque = DateTime.UtcNow.AddDays(-1);
                wu = new UpdateCandidate { Kind = UpdateKind.Controlador, Status = UpdateStatus.Disponible, Installable = true, Recommended = true };
                WindowsUpdateProvider.AdjustNewerDriver(wu, Vega2025("27.20.21034.37"), "31.0.21921.1000", null, null);
                Check(wu.Status == UpdateStatus.RequiereReinicio && !wu.Installable, "Windows Update, sin reiniciar: «requiere reinicio»");

                // Sin saber cuándo arrancó el equipo: pasado un día se toma como reiniciado.
                arranque = null;
                Check(!DriverFollowUp.Rebooted(DateTime.UtcNow.AddHours(-2)) && DriverFollowUp.Rebooted(DateTime.UtcNow.AddDays(-2)), "sin la hora de arranque: un día de margen");
            }
            finally { DriverFollowUp.StoreForTests = null; DriverFollowUp.BootTimeForTests = null; }

            // Una fila de Windows Update que espera el reinicio se queda con el dispositivo (la del catálogo no se repite).
            var d = Vega2025("27.20.21034.37");
            var cat = new UpdateCandidate { Kind = UpdateKind.Controlador, ProviderName = CatalogDriverProvider.ProviderId, Status = UpdateStatus.Disponible, Installable = true, ProviderTag = new CatalogPlan { Driver = d } };
            var lista = new List<UpdateCandidate> { cat };
            UpdateManager.DeduplicateCatalogDrivers(lista);
            Check(lista.Contains(cat), "sin Windows Update, queda la del catálogo");
            var wuEspera = new UpdateCandidate { Kind = UpdateKind.Controlador, ProviderName = WindowsUpdateProvider.ProviderId, Status = UpdateStatus.RequiereReinicio, ProviderTag = d };
            lista = new List<UpdateCandidate> { cat, wuEspera };
            UpdateManager.DeduplicateCatalogDrivers(lista);
            Check(lista.Contains(wuEspera) && !lista.Contains(cat), "Windows Update ya la instaló y espera el reinicio: no se repite con la del catálogo");
        }

        /// <summary>pnputil 3010 es del paquete: si nombra a este dispositivo sólo como «actualizado», no se le instaló nada.</summary>
        static void TestCodigo3010DelPaquete2025()
        {
            string dev = @"PCI\VEN_1022&DEV_1630&SUBSYS_8615103C&REV_00\3&2411E6FE&0&00";
            string soloActualizado = "Agregando paquete de controladores:  amdpci.inf  El paquete de controladores se agregó correctamente.  Nombre publicado: oem51.inf  " +
                                     "El paquete de controladores está actualizado en el dispositivo: " + dev + "    Agregando paquete de controladores:  amdgpio2.inf  " +
                                     "Paquete de controladores instalado en el dispositivo: ACPI\\AMDI0030\\0";
            Eq(CatalogRules.InstallOutcome.NoAplicado, CatalogRules.Interpret(3010, soloActualizado, dev, "1.0.0.8", "1.0.0.9"),
               "3010 por OTRO dispositivo del paquete: éste no cambia al reiniciar");
            string instalado = "Paquete de controladores instalado en el dispositivo: " + dev;
            Eq(CatalogRules.InstallOutcome.Reinicio, CatalogRules.Interpret(3010, instalado, dev, "1.0.0.8", "1.0.0.9"), "3010 y se instaló en este: al reiniciar");
            Eq(CatalogRules.InstallOutcome.Reinicio, CatalogRules.Interpret(3010, "Driver package added successfully.", dev, "1.0.0.8", "1.0.0.9"), "3010 sin nombrar dispositivos (pnputil viejo): al reiniciar, como antes");
            Eq(CatalogRules.InstallOutcome.Actualizado, CatalogRules.Interpret(3010, soloActualizado, dev, "1.0.0.9", "1.0.0.9"), "si el registro ya informa la nueva: actualizado");
        }

        /// <summary>AMD y ATI declaran sus placas con la revisión y sin el modelo del equipo: forzar y comprobar el paquete los reconoce.</summary>
        static void TestIdentificadoresDeAmd2025()
        {
            var ids = CatalogRules.ForceIds(VegaHwid);
            Check(ids.Contains(@"PCI\VEN_1002&DEV_15D8&REV_C1"), "el chip con su revisión: " + string.Join(" | ", ids.ToArray()));
            Check(ids.IndexOf(@"PCI\VEN_1002&DEV_15D8&REV_C1") < ids.IndexOf(@"PCI\VEN_1002&DEV_15D8"), "más específico que el chip solo");
            Check(CatalogRules.ForceIds(@"PCI\VEN_1002&DEV_6739&SUBSYS_174B174B").Count == 2, "sin revisión: no se inventa una");
            string infAmd = "[Version]\r\nClass=Display\r\n[ATI.Mfg.NTamd64.10.0]\r\n\"AMD Radeon(TM) Vega 8 Graphics\" = ati2mtag_RavenAPU, PCI\\VEN_1002&DEV_15D8&REV_C1\r\n" +
                            "\"AMD Radeon(TM) Vega 8 Graphics\" = ati2mtag_RavenAPU, PCI\\VEN_1002&DEV_15D8&REV_C2\r\n";
            Eq(@"PCI\VEN_1002&DEV_15D8&REV_C1", CatalogRules.InfMatchingId(infAmd, ids), "el INF de AMD declara este dispositivo (antes: «el paquete no declara este dispositivo»)");

            string dir = Path.Combine(Path.GetTempPath(), "act2025-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "Packages", "Drivers", "Display", "WT6A_INF"));
            try
            {
                File.WriteAllText(Path.Combine(dir, "Packages", "Drivers", "Display", "WT6A_INF", "u0389547.inf"), infAmd, Encoding.Unicode);
                File.WriteAllText(Path.Combine(dir, "amdxe.inf"), "[Version]\r\nClass=Extension\r\n[x]\r\n%a% = b, PCI\\VEN_1002&DEV_15D8&REV_C1\r\n", Encoding.ASCII);
                string id;
                string elegido = CatalogDriverProvider.PickForceInf(dir, VegaHwid, out id);
                Check(elegido != null && id == @"PCI\VEN_1002&DEV_15D8&REV_C1", "forzado: encuentra el INF de AMD en su subcarpeta: " + elegido);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>Antes de instalar: ¿el paquete es para ESTE dispositivo? (y no sólo un complemento)</summary>
        static void TestPaqueteParaEsteEquipo2025()
        {
            var ids = CatalogRules.MatchIds(VegaHwid, null);
            string paraLenovo = "[Version]\r\nClass=Display\r\n[m]\r\n%v% = s, PCI\\VEN_1002&DEV_15D8&SUBSYS_380817AA&REV_C1\r\n";
            string generico = "[Version]\r\nClass=Display\r\n[m]\r\n%v% = s, PCI\\VEN_1002&DEV_15D8&REV_C1\r\n";
            string extension = "[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nClass=Extension\r\nClassGuid={e2f84ce7-8efa-411c-aa69-97454ca4cb57}\r\n[m]\r\n%v% = s, PCI\\VEN_1002&DEV_15D8&REV_C1\r\n";
            Eq("Extension", CatalogRules.InfClass(extension), "clase del INF");
            Eq("Display", CatalogRules.InfClass(generico), "clase del INF (Display)");
            Eq(CatalogRules.PackageFit.NotDeclared, CatalogRules.Fit(new[] { paraLenovo }, ids, "Display"), "paquete de otro fabricante de equipos: no declara éste");
            Eq(CatalogRules.PackageFit.Declares, CatalogRules.Fit(new[] { paraLenovo, generico }, ids, "Display"), "el genérico con revisión sí");
            Eq(CatalogRules.PackageFit.OnlyComponents, CatalogRules.Fit(new[] { extension }, ids, "Display"), "sólo una extensión: no cambia el controlador");
            Eq(CatalogRules.PackageFit.Declares, CatalogRules.Fit(new[] { extension }, ids, "Extension"), "para un dispositivo de extensión, sí");
            Eq(CatalogRules.PackageFit.Declares, CatalogRules.Fit(new[] { paraLenovo }, new List<string>(), "Display"), "sin identificadores: decide Windows, como antes");

            // Los del registro (hardware y compatibles) también cuentan, sin repetir.
            var reg = new[] { VegaHwid, @"PCI\VEN_1002&DEV_15D8&SUBSYS_8615103C", @"PCI\VEN_1002&DEV_15D8&CC_030000", @"PCI\VEN_1002&DEV_15D8&REV_C1", @"PCI\VEN_1002&DEV_15D8", @"PCI\VEN_1002&CC_030000" };
            var todos = CatalogRules.MatchIds(VegaHwid, reg);
            Check(todos.Contains(@"PCI\VEN_1002&DEV_15D8&CC_030000") && todos.FindAll(delegate(string s) { return s.Equals(@"PCI\VEN_1002&DEV_15D8&REV_C1", StringComparison.OrdinalIgnoreCase); }).Count == 1,
                  "con los compatibles del registro, sin repetir");
            string porClase = "[Version]\r\nClass=Display\r\n[m]\r\n%v% = s, PCI\\VEN_1002&DEV_15D8&CC_030000\r\n";
            Eq(CatalogRules.PackageFit.Declares, CatalogRules.Fit(new[] { porClase }, todos, "Display"), "un INF que lo declara por su identificador compatible");
            Eq(CatalogRules.PackageFit.NotDeclared, CatalogRules.Fit(new[] { porClase }, ids, "Display"), "sin el registro no se adivina ese identificador");
        }

        /// <summary>Lo que Windows no aplicó a un controlador de terceros no se vuelve a ofrecer (2.0.24 lo prometía para «AMD PCI»).</summary>
        static void TestNoAplicadoNoSeRepite2025()
        {
            var amdPci = Vega2025("27.20.21034.37");
            CatalogDriverProvider.RejectedForTests = delegate(string device, string guid)
            {
                return device == VegaDevice && guid == GuidDisplay2025 ? "2026-11-21" : null;
            };
            try
            {
                var salida = EscanearVega2025(amdPci, null);
                var fila = salida.Find(delegate(UpdateCandidate c) { return c.Name.StartsWith("AMD Radeon"); });
                Check(fila != null && fila.Status == UpdateStatus.AlDia && !fila.Installable && fila.Detail.Contains("21/11/2026") && fila.Detail.Contains("no se vuelve a ofrecer"),
                      "Windows no lo aplicó el 21/11: «Al día», no se ofrece de nuevo: " + (fila != null ? fila.Detail : ""));
            }
            finally { CatalogDriverProvider.RejectedForTests = null; }
        }

        // ================================================================== Rendimiento (pedido del 23/11/2026, 11:00)

        /// <summary>Leer una página del catálogo: cada fila dentro de su propio &lt;tr&gt;, con los mismos datos que antes.</summary>
        static void TestCatalogoSeLeeDeUnaPasada2025()
        {
            var filas = new List<string[]>();
            for (int i = 0; i < 25; i++)
                filas.Add(new[] { "bbbbbbbb-2222-4222-8222-" + (100000000000L + i).ToString(), "Advanced Micro Devices, Inc. - Display - 31.0." + (20000 + i) + ".1000",
                                  "Windows 10 and later drivers", "6/" + (1 + i % 28) + "/2025", "31.0." + (20000 + i) + ".1000" });
            string html = Catalogo2025(filas.ToArray()).Replace("_size\">612.0 MB</span>", "_size\">612.0 MB</span><span id=\"X_originalSize\">641728512</span>");
            // El tamaño exacto de cada fila con su propio guid.
            for (int i = 0; i < 25; i++)
            {
                string g = filas[i][0];
                int at = html.IndexOf("id=\"X_originalSize\"", StringComparison.Ordinal);
                html = html.Substring(0, at) + "id=\"" + g + "_originalSize\"" + html.Substring(at + "id=\"X_originalSize\"".Length);
            }
            bool sinResultados, completa;
            var leidas = CatalogRules.ParseSearch(html, out sinResultados, out completa);
            Check(completa && !sinResultados, "página completa");
            Eq(25, leidas.Count, "las 25 filas");
            bool bien = true;
            for (int i = 0; i < leidas.Count && i < 25; i++)
            {
                var e = leidas[i];
                bien &= e.Guid == filas[i][0] && e.Title == filas[i][1] && e.Version == filas[i][4] && e.Products == filas[i][2] &&
                        e.Classification == "Drivers (Display)" && e.Size == "612.0 MB" && e.SizeBytes == 641728512 && e.LastUpdated.HasValue && e.LastUpdated.Value.Day == 1 + i % 28;
            }
            Check(bien, "cada fila con su título, productos, clasificación, fecha, versión y tamaño (no los de la fila vecina)");
            Check(CatalogDriverProvider.WorkersFor(true) >= 2, "modo liviano: el catálogo con 2 consultas a la vez (es espera de la red)");
        }

        /// <summary>Las páginas oficiales leídas hace un rato no se bajan de nuevo en la vuelta siguiente.</summary>
        static void TestPaginasRecientes2025()
        {
            RecentPages.Clear();
            int pedidos = 0;
            Func<string> pagina = delegate { pedidos++; return "<html>7-Zip 26.00</html>"; };
            Eq("<html>7-Zip 26.00</html>", RecentPages.Get("text/html https://www.7-zip.org/", pagina), "la primera vez se lee");
            Eq("<html>7-Zip 26.00</html>", RecentPages.Get("text/html https://www.7-zip.org/", pagina), "la segunda, de memoria");
            Eq(1, pedidos, "un solo pedido");
            Eq(1, RecentPages.Hits, "una lectura ahorrada");
            int vacios = 0;
            RecentPages.Get("text/html https://vacia.example/", delegate { vacios++; return ""; });
            RecentPages.Get("text/html https://vacia.example/", delegate { vacios++; return ""; });
            Eq(2, vacios, "una respuesta vacía no se guarda");
            int errores = 0;
            for (int i = 0; i < 2; i++)
            {
                try { RecentPages.Get("text/html https://caida.example/", delegate { errores++; throw new InvalidOperationException("sin respuesta"); }); }
                catch (InvalidOperationException) { }
            }
            Eq(2, errores, "un error no se guarda: se vuelve a intentar");
            var vieja = RecentPages.Life;
            try
            {
                RecentPages.Life = TimeSpan.Zero;
                RecentPages.Get("text/html https://www.7-zip.org/", pagina);
                Eq(2, pedidos, "vencida: se lee de nuevo");
            }
            finally { RecentPages.Life = vieja; }
            string grande = new string('x', RecentPages.MaxPageChars / 2);
            for (int i = 0; i < 20; i++) RecentPages.Get("g" + i, delegate { return grande; });
            Check(RecentPages.Size <= RecentPages.MaxChars, "con tope de memoria (" + RecentPages.Size + " caracteres)");
            RecentPages.Clear();
            Eq(0L, RecentPages.Size, "se olvida todo");
        }

        sealed class ProveedorQueCuenta2025 : IUpdateProvider
        {
            readonly string id;
            public int Busquedas;
            public ProveedorQueCuenta2025(string id) { this.id = id; }
            public string Name { get { return id; } }
            public string Unavailable(UpdateContext ctx) { return null; }
            public void Scan(UpdateContext ctx, List<UpdateCandidate> output, List<string> notes)
            {
                Busquedas++;
                output.Add(new UpdateCandidate { Kind = UpdateKind.Controlador, Name = "BIOS del equipo", Identifier = "bios", ProviderName = id, Status = UpdateStatus.Disponible,
                                                 Installable = true, Recommended = false, InstalledVersion = "1.0", AvailableVersion = "1.2" });
            }
            public void Install(UpdateContext ctx, List<UpdateCandidate> items, Action<UpdateCandidate> changed) { }
        }

        /// <summary>En las vueltas de «todo de una vez» se vuelve a consultar sólo lo que puede haber cambiado.</summary>
        static void TestVueltasSoloLoQueCambio2025()
        {
            var programas = new List<UpdateCandidate> { new UpdateCandidate { ProviderName = WingetProvider.ProviderId } };
            var carry = UpdateManager.CarryOverAfter(programas);
            Check(carry.Contains(WindowsUpdateProvider.ProviderId) && carry.Contains(CatalogDriverProvider.ProviderId) && carry.Contains(OemToolsProvider.ProviderId),
                  "sólo programas: Windows Update, el catálogo y la herramienta del fabricante quedan como estaban");
            Check(!carry.Contains(StoreProvider.ProviderId) && !carry.Contains(WingetProvider.ProviderId), "winget se compara siempre; la Store, si winget instaló algo");
            carry = UpdateManager.CarryOverAfter(new List<UpdateCandidate> { new UpdateCandidate { ProviderName = WindowsUpdateProvider.ProviderId } });
            Check(!carry.Contains(WindowsUpdateProvider.ProviderId) && !carry.Contains(CatalogDriverProvider.ProviderId) && carry.Contains(OemToolsProvider.ProviderId) && carry.Contains(StoreProvider.ProviderId),
                  "Windows Update: se vuelve a buscar ahí y en el catálogo");
            carry = UpdateManager.CarryOverAfter(new List<UpdateCandidate> { new UpdateCandidate { ProviderName = OemToolsProvider.ProviderId } });
            Check(!carry.Contains(OemToolsProvider.ProviderId) && !carry.Contains(WindowsUpdateProvider.ProviderId), "la herramienta del fabricante: ella y los controladores");

            // Una tanda: A destraba B (navegadores); la herramienta del fabricante no se vuelve a consultar.
            var prov = new ProveedorPorEtapas();
            var oem = new ProveedorQueCuenta2025(OemToolsProvider.ProviderId);
            var man = new UpdateManager(new IUpdateProvider[] { prov, oem });
            man.Ignored = new IgnoreList();
            man.HistoryPath = "";
            var ctx = ContextoSimulado(true);
            ctx.DriversLoaded = true;
            ctx.Os.Major = 6; ctx.Os.Minor = 1;
            var report = UpdateSession.Create(ctx);
            man.Scan(ctx, report);
            var filaA = report.Updates.Find(delegate(UpdateCandidate u) { return u.Name == "A"; });
            man.InstallUntilDone(ctx, report, new List<UpdateCandidate> { filaA }, false, delegate(UpdateCandidate c) { }, null);
            Eq("A,B", string.Join(",", prov.Llamadas.ToArray()), "igual que antes: A y después B");
            Check(prov.Busquedas == 3, "los programas se buscaron en cada vuelta (" + prov.Busquedas + ")");
            Eq(1, oem.Busquedas, "la herramienta del fabricante, una sola vez (antes, en cada vuelta)");
            Check(report.Updates.Exists(delegate(UpdateCandidate u) { return u.Name == "BIOS del equipo"; }), "y su fila sigue en la lista");
            Check(report.UpdateNotes.Exists(delegate(string n) { return n.Contains("de la búsqueda anterior"); }), "el resumen de tiempos lo dice");
        }

        /// <summary>Windows Update empieza a descargar mientras se instala lo demás.</summary>
        static void TestWindowsUpdateDescargaAntes2025()
        {
            int pedidas = -1;
            var entrada = new System.Threading.ManualResetEvent(false);
            WindowsUpdateProvider.PrefetchForTests = delegate(int n) { pedidas = n; entrada.WaitOne(2000); return false; };
            try
            {
                var wu = new WindowsUpdateProvider();
                var ctx = ContextoSimulado(true);
                var a = new UpdateCandidate { Kind = UpdateKind.WindowsUpdate, Name = "Acumulativa", ProviderName = WindowsUpdateProvider.ProviderId, ProviderTag = new object() };
                var b = new UpdateCandidate { Kind = UpdateKind.WindowsUpdate, Name = ".NET", ProviderName = WindowsUpdateProvider.ProviderId, ProviderTag = new object() };
                var dispositivo = new UpdateCandidate { Kind = UpdateKind.Controlador, Name = "al día", ProviderName = WindowsUpdateProvider.ProviderId, ProviderTag = Vega2025("1.0") };
                wu.StartPrefetch(ctx, new List<UpdateCandidate> { a, b, dispositivo });
                for (int i = 0; i < 100 && pedidas < 0; i++) System.Threading.Thread.Sleep(10);
                Eq(2, pedidas, "se descargan las dos actualizaciones (no la fila de un dispositivo)");
                entrada.Set();
                // La descarga no terminó a tiempo: no se espera otra vez por cada una.
                wu.Install(ctx, new List<UpdateCandidate> { a, b }, delegate(UpdateCandidate c) { });
                Check(a.Status == UpdateStatus.Fallida && b.Status == UpdateStatus.Fallida && a.Result.Contains("no terminó"), "si la descarga no terminó, se informa sin otra espera: " + a.Result);
                pedidas = -1;
                wu.StartPrefetch(ContextoSimulado(false), new List<UpdateCandidate> { a });
                System.Threading.Thread.Sleep(50);
                Eq(-1, pedidas, "sin administrador no se descarga nada");
            }
            finally { WindowsUpdateProvider.PrefetchForTests = null; }
        }

        // ================================================================== 2.0.26 (grabación del usuario, 23/11/2026 11:20)

        /// <summary>
        /// AMD Radeon(TM) Vega 3 Graphics, 31.0.21924.61 del fabricante del equipo; el catálogo, 31.0.21925.3004 sólo por el chip. Antes: «Instalar
        /// forzados» en la ventana; sin marcarlo no pasaba nada y la fila seguía igual.
        /// </summary>
        static void TestVega3DelFabricante2026()
        {
            string hwid = @"PCI\VEN_1002&DEV_15DD&SUBSYS_86F1103C&REV_E1";
            var ids = CatalogRules.MatchIds(hwid, new[] { hwid, @"PCI\VEN_1002&DEV_15DD&SUBSYS_86F1103C", @"PCI\VEN_1002&DEV_15DD&REV_E1", @"PCI\VEN_1002&DEV_15DD" });
            var modelo = CatalogRules.ModelIds(ids);
            Check(modelo.Count == 2 && modelo.TrueForAll(delegate(string s) { return s.Contains("&SUBSYS_86F1103C"); }), "los identificadores con el modelo del equipo");
            string generico = "[Version]\r\nClass=Display\r\n[m]\r\n%v% = s, PCI\\VEN_1002&DEV_15DD&REV_E1\r\n";
            string conModelo = generico + "%h% = s, PCI\\VEN_1002&DEV_15DD&SUBSYS_86F1103C&REV_E1\r\n";
            Eq(CatalogRules.PackageFit.Declares, CatalogRules.Fit(new[] { generico }, ids, "Display"), "el genérico del chip declara el dispositivo…");
            Eq(CatalogRules.PackageFit.NotDeclared, CatalogRules.Fit(new[] { generico }, modelo, "Display"), "…pero no su modelo: Windows se queda con el del fabricante");
            Eq(CatalogRules.PackageFit.Declares, CatalogRules.Fit(new[] { conModelo }, modelo, "Display"), "si el paquete también trae este modelo, se instala");
            Check(CatalogDriverProvider.PreferOemText().Contains("se comprueba") && !CatalogDriverProvider.PreferOemText().Contains("FORZADA"), "el detalle ya no pide forzarla");
        }

        /// <summary>Instalada «bien» y después de reiniciar Windows Update volvió a poner la del fabricante: se dice y no se repite.</summary>
        static void TestVueltaAtrasAlReiniciar2026()
        {
            DriverFollowUp.StoreForTests = new Dictionary<string, string>();
            DateTime? arranque = DateTime.UtcNow.AddMinutes(5);
            DriverFollowUp.BootTimeForTests = delegate { return arranque; };
            try
            {
                DriverFollowUp.Record(VegaDevice, "31.0.21921.1000");
                DateTime at;
                Eq(DriverFollowUp.State.None, DriverFollowUp.Check(null, VegaDevice, "31.0.21921.1000", "31.0.21921.1000", out at), "la usa: nada que decir");
                Eq(DriverFollowUp.State.NotApplied, DriverFollowUp.Check(null, VegaDevice, "31.0.21921.1000", "27.20.21034.37", out at), "reinició y volvió la de antes: no se ofrece otra vez");
            }
            finally { DriverFollowUp.StoreForTests = null; DriverFollowUp.BootTimeForTests = null; }
        }

        static void TestVersion2026()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.26") >= 0, "Actualizador 2.0.26 o posterior: " + AppInfo.Version);
        }

        static void TestVersion2025()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.25") >= 0, "Actualizador 2.0.25 o posterior: " + AppInfo.Version);
        }
    }
}
