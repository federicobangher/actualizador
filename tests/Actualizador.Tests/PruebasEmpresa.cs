// 2.0: seguridad empresarial — política de la empresa, registro de cambios con HMAC encadenado, Visor de eventos,
// exportación de la lista y los estados de red de .NET 8.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static string NuevaCarpeta(string nombre)
        {
            string dir = Path.Combine(Path.GetTempPath(), "sp-pruebas-" + nombre + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static UpdateCandidate Fila(UpdateKind k, string name, string id, string publisher, string provider)
        {
            var c = new UpdateCandidate();
            c.Kind = k; c.Name = name; c.Identifier = id; c.Publisher = publisher; c.ProviderName = provider;
            c.InstalledVersion = "1.0"; c.AvailableVersion = "2.0"; c.Status = UpdateStatus.Disponible;
            c.Installable = true; c.Recommended = true; c.Selected = true; c.Source = provider;
            return c;
        }

        // ------------------------------------------------------------------ Política
        static void TestPoliticaJson20()
        {
            var vacia = new Policy();
            Check(vacia.IsEmpty && vacia.Describe().StartsWith("Sin política"), "sin política: todo permitido");

            var p = new Policy();
            p.ApplyJson(@"{
              ""fuentes"": { ""github"": false, ""sitio"": false, ""winget"": true, ""inventada"": false },
              ""bloquear"": [ ""TeamViewer*"", ""Oracle.JavaRuntime*"", """" ],
              ""editoresPermitidos"": [ ""Microsoft*"", ""Google LLC"" ],
              ""permitirAbrirPaginas"": false,
              ""permitirForzarControladores"": false,
              ""permitirInstalarWinget"": false,
              ""permitirInstaladoresConVentana"": false,
              ""exigirPuntoDeRestauracion"": true,
              ""exigirAdministrador"": true,
              ""bloquearOpciones"": true,
              ""catalogoFuentesUrl"": ""https://intranet.example/fuentes.json""
            }");
            Check(!p.IsSourceAllowed("github") && !p.IsSourceAllowed("sitio") && p.IsSourceAllowed("winget"), "fuentes apagadas y encendidas");
            Check(p.IsSourceAllowed(null) && p.IsSourceAllowed(""), "sin proveedor: no se bloquea por fuente");
            Check(p.Problems.Count == 1 && p.Problems[0].Contains("inventada"), "fuente desconocida: se informa y se ignora");
            Eq(2, p.Blocked.Count, "patrones vacíos no cuentan");
            Check(!p.AllowOpenPages && !p.AllowForcedDrivers && !p.AllowInstallWinget && !p.AllowInteractiveInstallers, "permisos en falso");
            Check(p.RequireRestorePoint && p.RequireAdministrator && p.LockSettings, "exigencias");
            Eq("https://intranet.example/fuentes.json", p.SourcesCatalogUrl, "catálogo https");
            Check(!p.IsEmpty && p.Describe().Contains("sólo administradores"), "descripción de lo que restringe");

            var http = new Policy();
            http.ApplyJson(@"{ ""catalogoFuentesUrl"": ""http://intranet/fuentes.json"" }");
            Check(http.SourcesCatalogUrl == null && http.Problems.Count == 1, "un catálogo por http se rechaza (sólo https)");

            var rota = new Policy();
            rota.ApplyJson("{ esto no es json");
            Check(rota.Problems.Count == 1 && rota.IsEmpty, "JSON inválido: se informa y no restringe nada");

            var largo = new Policy();
            largo.ApplyJson("{ \"bloquear\": [\"" + new string('a', 250) + "\"] }");
            Check(largo.Blocked.Count == 0 && largo.Problems.Count == 1, "patrón demasiado largo: se rechaza");
        }

        static void TestPoliticaPatrones20()
        {
            Check(Policy.Matches("TeamViewer*", "TeamViewer 15"), "comodín *");
            Check(Policy.Matches("teamviewer*", "TEAMVIEWER.TeamViewer"), "sin distinguir mayúsculas");
            Check(Policy.Matches("7-Zip ??.??*", "7-Zip 24.07 (x64)"), "comodín ?");
            Check(!Policy.Matches("Zip", "7-Zip"), "el patrón cubre el texto entero");
            Check(!Policy.Matches("a.b", "aXb"), "el punto es literal");
            Check(Policy.Matches("C++ (x64)*", "C++ (x64) Redistributable"), "los caracteres de expresiones regulares son literales");
            Check(!Policy.Matches("", "algo") && !Policy.Matches("*", null), "vacíos no coinciden");

            var p = new Policy();
            p.Blocked.Add("TeamViewer*");
            p.AllowedPublishers.Add("Microsoft*");
            Check(p.BlockReason("TeamViewer", "TeamViewer.TeamViewer", "TeamViewer Germany GmbH", true).Contains("TeamViewer*"), "bloqueo por nombre");
            Check(p.BlockReason("Otro", "TeamViewer.Host", "x", true) != null, "bloqueo por identificador");
            Check(p.BlockReason("Visual Studio Code", "Microsoft.VisualStudioCode", "Microsoft Corporation", true) == null, "editor aprobado");
            Check(p.BlockReason("7-Zip", "7zip.7zip", "Igor Pavlov", true).Contains("Igor Pavlov"), "editor no aprobado");
            Check(p.BlockReason("7-Zip", "7zip.7zip", "", true).Contains("desconocido"), "editor desconocido no pasa");
            Check(p.BlockReason("Intel Wi-Fi", "drv", "Intel", false) == null, "los editores se exigen sólo a programas");
        }

        static void TestPoliticaArchivoYRegistro20()
        {
            string dir = NuevaCarpeta("politica");
            try
            {
                string file = Path.Combine(dir, Policy.FileName);
                File.WriteAllText(file, "{ \"bloquear\": [\"AnyDesk*\"], \"exigirPuntoDeRestauracion\": true }", Encoding.UTF8);
                Policy.FileForTests = file;
                var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                values["Fuente_catalogo"] = 0;
                values["Fuente_winget"] = 1;
                values["PermitirAbrirPaginas"] = 0;
                values["Bloquear"] = new[] { "Opera*", "Brave*" };
                Policy.RegistryForTests = delegate(string name) { object v; return values.TryGetValue(name, out v) ? v : null; };
                var p = Policy.Load();
                Eq("directiva de grupo y archivo", p.Origin, "origen: GPO y archivo");
                Check(p.RequireRestorePoint, "lo del archivo se aplica");
                Check(!p.IsSourceAllowed("catalogo") && p.IsSourceAllowed("winget") && !p.AllowOpenPages, "lo de la GPO se aplica");
                Check(p.Blocked.Contains("Opera*") && !p.Blocked.Contains("AnyDesk*"), "la GPO manda sobre el archivo en las listas");

                File.WriteAllText(file, new string(' ', 1024 * 1024 + 10), Encoding.UTF8);
                Policy.RegistryForTests = delegate(string name) { return null; };
                var grande = Policy.Load();
                Check(grande.Problems.Count == 1 && grande.Problems[0].Contains("grande"), "archivo de más de 1 MB: se ignora");

                File.Delete(file);
                var nada = Policy.Load();
                Check(nada.IsEmpty && nada.Origin == "ninguna", "sin archivo ni GPO: ninguna");

                Policy.Reset();
                Policy.RegistryForTests = delegate(string name) { return name == "ExigirAdministrador" ? (object)1 : null; };
                Check(Policy.Current.RequireAdministrator, "Policy.Current lee la política");
                Policy.RegistryForTests = delegate(string name) { return null; };
                Check(Policy.Current.RequireAdministrator, "Policy.Current se guarda un rato (no relee en cada fila)");
                Policy.Reset();
                Check(!Policy.Current.RequireAdministrator, "Reset vuelve a leer");
            }
            finally
            {
                Policy.FileForTests = null;
                Policy.RegistryForTests = null;
                Policy.Reset();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static void TestPoliticaEnElMotor20()
        {
            var p = new Policy();
            p.ApplyJson(@"{ ""bloquear"": [""TeamViewer*""], ""permitirAbrirPaginas"": false, ""fuentes"": { ""catalogo"": false },
                            ""permitirInstalarWinget"": false, ""catalogoFuentesUrl"": ""https://intranet.example/f.json"" }");
            var tv = Fila(UpdateKind.Programa, "TeamViewer", "TeamViewer.TeamViewer", "TeamViewer", "winget");
            var zip = Fila(UpdateKind.Programa, "7-Zip", "7zip.7zip", "Igor Pavlov", "winget");
            var pdf = Fila(UpdateKind.Programa, "PDF24 Creator", "pdf24", "geek software GmbH", SiteProvider.ProviderId);
            var drv = Fila(UpdateKind.Controlador, "Realtek PCIe GbE", "drv1", "Realtek", "catalogo");
            var aldia = Fila(UpdateKind.Programa, "TeamViewer Host", "TeamViewer.Host", "TeamViewer", "winget");
            aldia.Status = UpdateStatus.AlDia;
            var store = Fila(UpdateKind.Store, "Todas las apps", StoreProvider.AllAppsId, "Microsoft", "store");
            var all = new List<UpdateCandidate> { tv, zip, pdf, drv, aldia, store };

            int n = UpdateManager.ApplyPolicy(p, all);
            Eq(2, n, "bloquea TeamViewer y la página de descarga");
            Check(tv.Status == UpdateStatus.Ignorada && tv.IgnoredBy == UpdateManager.PolicyMarker && !tv.Recommended && !tv.Selected, "fila bloqueada: ignorada y desmarcada");
            Check(tv.Detail.StartsWith("No se instala: bloqueada por la política"), "con el motivo a la vista");
            Check(pdf.Status == UpdateStatus.Ignorada, "abrir páginas no permitido");
            Check(zip.Status == UpdateStatus.Disponible && aldia.Status == UpdateStatus.AlDia && store.Status == UpdateStatus.Disponible, "el resto queda igual");
            Check(!IgnoreList.CanIgnore(tv), "lo bloqueado por la política no se puede «ignorar»");
            IgnoreList.Unignore(tv);
            Check(tv.Status == UpdateStatus.Ignorada && tv.IgnoredBy == UpdateManager.PolicyMarker, "ni destrabar desde la lista de ignorados");

            var lista = new IgnoreList();
            lista.Apply(all);
            Check(tv.IgnoredBy == UpdateManager.PolicyMarker, "aplicar la lista del usuario no destraba lo de la política");

            var ctxAdmin = new UpdateContext();
            ctxAdmin.IsAdmin = true;
            Check(UpdateManager.PolicyRefusal(p, ctxAdmin, tv) != null, "rechazo al instalar: bloqueada");
            Check(UpdateManager.PolicyRefusal(p, ctxAdmin, drv).Contains("desactivó esa fuente"), "rechazo al instalar: fuente apagada");
            Check(UpdateManager.PolicyRefusal(p, ctxAdmin, zip) == null, "lo permitido pasa");
            var tv2 = Fila(UpdateKind.Programa, "TeamViewer", "TeamViewer.TeamViewer", "TeamViewer", "winget");
            Check(UpdateManager.PolicyRefusal(p, ctxAdmin, tv2) != null, "se vuelve a comprobar al instalar (aunque la fila no se haya marcado antes)");

            var admin = new Policy();
            admin.RequireAdministrator = true;
            var ctxUser = new UpdateContext();
            ctxUser.IsAdmin = false;
            Check(UpdateManager.PolicyRefusal(admin, ctxUser, zip).Contains("administrador"), "exigir administrador");
            Check(UpdateManager.PolicyRefusal(admin, ctxAdmin, zip) == null, "como administrador pasa");

            var o = new UpdateOptions();
            o.InstallWinget = true; o.QueryVendors = true; o.SourcesCatalogUrl = "https://otro/x.json";
            UpdateManager.ApplyPolicyToOptions(p, o);
            Check(!o.InstallWinget && o.SourcesCatalogUrl == "https://intranet.example/f.json" && o.QueryVendors, "la política manda sobre las opciones");
            var bloqueada = new Policy();
            bloqueada.LockSettings = true;
            var o2 = new UpdateOptions();
            o2.SourcesCatalogUrl = "https://usuario/x.json";
            UpdateManager.ApplyPolicyToOptions(bloqueada, o2);
            Check(o2.SourcesCatalogUrl == "", "opciones bloqueadas: el usuario no elige el catálogo");
            var sinFabricantes = new Policy();
            sinFabricantes.ApplyJson(@"{ ""fuentes"": { ""fabricante"": false, ""github"": false, ""sitio"": false, ""catalogo"": false } }");
            var o3 = new UpdateOptions();
            o3.QueryVendors = true;
            UpdateManager.ApplyPolicyToOptions(sinFabricantes, o3);
            Check(!o3.QueryVendors, "sin ninguna fuente de fabricantes: no se consultan");
        }

        // ------------------------------------------------------------------ Registro de cambios
        static void TestRegistroDeCambios20()
        {
            string dir = NuevaCarpeta("auditoria");
            var eventos = new List<string>();
            try
            {
                AuditTrail.DirectoryForTests = dir;
                AuditTrail.KeyForTests = Encoding.ASCII.GetBytes("clave-de-prueba-de-32-bytes-1234");
                EventLogWriter.WriteForTests = delegate(int id, EventLogWriter.Level level, string message) { eventos.Add(id + ":" + level + ":" + message); };
                AuditTrail.ResetShared();
                var t = AuditTrail.Open();
                Check(t.Verify().Ok && t.Verify().Entries == 0, "registro vacío: íntegro");
                for (int i = 1; i <= 5; i++)
                {
                    var d = new Dictionary<string, object>();
                    d["nombre"] = "Programa " + i;
                    d["texto"] = "comillas \" y barra \\ y salto\nde línea";
                    Check(t.Record("actualizacion", d), "anota el evento " + i);
                }
                var r = t.Verify();
                Check(r.Ok && r.Entries == 5, "cinco eventos encadenados: " + r);

                AuditTrail.Write("busqueda", new Dictionary<string, object>(), EventLogWriter.Ids.ScanFinished, EventLogWriter.Level.Information, "Búsqueda terminada");
                Check(eventos.Count == 1 && eventos[0].StartsWith("1000:Information:"), "también va al Visor de eventos");
                Check(t.Verify().Entries == 6, "Write usa el mismo registro");

                string file = Directory.GetFiles(dir, AuditTrail.Prefix + "*.jsonl")[0];
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                Check(lines[0].Contains("\"n\":1") && lines[0].Contains("\"anterior\":\"inicio\"") && lines[0].Contains("\"mac\":\""), "formato de la línea");

                // Cambiar un dato
                var cambiado = (string[])lines.Clone();
                cambiado[2] = cambiado[2].Replace("Programa 3", "Programa X");
                File.WriteAllLines(file, cambiado, new UTF8Encoding(false));
                r = t.Verify();
                Check(!r.Ok && r.Problem.Contains("línea 3") && r.Problem.Contains("se modificó"), "detecta una línea cambiada: " + r.Problem);

                // Borrar una línea
                var borrado = new List<string>(lines);
                borrado.RemoveAt(1);
                File.WriteAllLines(file, borrado.ToArray(), new UTF8Encoding(false));
                r = t.Verify();
                Check(!r.Ok && r.Problem.Contains("faltan o sobran"), "detecta una línea borrada: " + r.Problem);

                // Reordenar
                var orden = (string[])lines.Clone();
                string x = orden[1]; orden[1] = orden[2]; orden[2] = x;
                File.WriteAllLines(file, orden, new UTF8Encoding(false));
                Check(!t.Verify().Ok, "detecta líneas reordenadas");

                // Borrar la última línea: no se ve por la cadena, pero la siguiente anotación sigue desde ahí y no se recupera el número
                File.WriteAllLines(file, lines, new UTF8Encoding(false));
                Check(t.Verify().Ok, "restaurado: íntegro otra vez");

                // Otra clave: todo falla
                AuditTrail.KeyForTests = Encoding.ASCII.GetBytes("otra-clave-distinta-de-32-bytes!");
                Check(!t.Verify().Ok, "con otra clave no verifica (no se puede rehacer la firma sin la clave)");
                AuditTrail.KeyForTests = Encoding.ASCII.GetBytes("clave-de-prueba-de-32-bytes-1234");

                // Línea sin firma agregada a mano
                File.AppendAllText(file, "{\"n\":7,\"evento\":\"falso\"}\n", new UTF8Encoding(false));
                r = t.Verify();
                Check(!r.Ok && r.Problem.Contains("no tiene firma"), "detecta una línea agregada a mano");
            }
            finally
            {
                AuditTrail.DirectoryForTests = null;
                AuditTrail.KeyForTests = null;
                AuditTrail.ResetShared();
                EventLogWriter.WriteForTests = null;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static void TestRegistroConClaveDpapi20()
        {
            // 2.0.11: la clave ya no se cifra con DPAPI (Avast cortaba el programa al descifrarla: IDP.Generic).
            string dir = NuevaCarpeta("clave");
            try
            {
                AuditTrail.DirectoryForTests = dir;
                var t = AuditTrail.Open();
                Check(t.Record("prueba", null), "crea la clave la primera vez");
                string keyFile = Path.Combine(dir, "clave.key");
                Check(File.Exists(keyFile) && new FileInfo(keyFile).Length == 32, "la clave queda guardada (32 bytes)");
                Check(!File.Exists(Path.Combine(dir, "clave.bin")), "no se crea la clave vieja");
                Check(t.Record("prueba", null) && t.Verify().Ok && t.Verify().Entries == 2, "la segunda vez usa la misma clave");
                File.WriteAllBytes(keyFile, new byte[5]);
                var r = t.Verify();
                Check(!r.Ok && r.Problem.Contains("clave"), "una clave dañada no da por bueno el registro");
                File.Delete(keyFile);
                r = t.Verify();
                Check(!r.Ok && r.Problem.Contains("clave"), "sin la clave no se puede dar por bueno el registro");
            }
            finally
            {
                AuditTrail.DirectoryForTests = null;
                try { Directory.Delete(dir, true); } catch { }
            }

            // Un registro de la 2.0.10 (clave.bin cifrada): se guarda entero, sin descifrar, y empieza una cadena nueva.
            dir = NuevaCarpeta("clave-vieja");
            try
            {
                AuditTrail.DirectoryForTests = dir;
                byte[] vieja = new byte[178];
                for (int i = 0; i < vieja.Length; i++) vieja[i] = (byte)(i * 7);
                File.WriteAllBytes(Path.Combine(dir, "clave.bin"), vieja);
                File.WriteAllText(Path.Combine(dir, AuditTrail.Prefix + "2026-10.jsonl"), "{\"n\":1,\"evento\":\"busqueda\",\"mac\":\"" + new string('a', 64) + "\"}\n");
                var t = AuditTrail.Open();
                var r0 = t.Verify();
                Check(r0.Ok && r0.Entries == 0, "verificar con un registro viejo lo guarda aparte y no da error: " + r0);
                var anteriores = Directory.GetDirectories(dir, "anterior-*");
                Check(anteriores.Length == 1, "queda una carpeta «anterior-…»");
                if (anteriores.Length == 1)
                {
                    byte[] guardada = File.ReadAllBytes(Path.Combine(anteriores[0], "clave.bin"));
                    Check(guardada.Length == vieja.Length && guardada[100] == vieja[100], "la clave vieja se guarda sin tocar (no se descifra)");
                    Check(File.Exists(Path.Combine(anteriores[0], AuditTrail.Prefix + "2026-10.jsonl")), "y los eventos viejos también");
                }
                Check(!File.Exists(Path.Combine(dir, "clave.bin")) && Directory.GetFiles(dir, AuditTrail.Prefix + "*").Length == 0, "en la carpeta no queda nada de la cadena vieja");
                Check(t.Record("busqueda", null), "se anota con la clave nueva");
                var r = t.Verify();
                Check(r.Ok && r.Entries == 1, "la cadena nueva está completa: " + r);
            }
            finally
            {
                AuditTrail.DirectoryForTests = null;
                try { Directory.Delete(dir, true); } catch { }
            }

            // Si al anotar todavía estaba la cadena vieja, el primer evento nuevo dice dónde quedó.
            dir = NuevaCarpeta("clave-vieja-2");
            try
            {
                AuditTrail.DirectoryForTests = dir;
                File.WriteAllBytes(Path.Combine(dir, "clave.bin"), new byte[178]);
                var t = AuditTrail.Open();
                Check(t.Record("busqueda", null), "anota con un registro viejo en la carpeta");
                string texto = string.Join("\n", Directory.GetFiles(dir, AuditTrail.Prefix + "*").Select(File.ReadAllText));
                Check(texto.Contains("\"evento\":\"registro-nuevo\"") && texto.Contains("anterior-"), "el primer evento dice que empieza una cadena nueva y dónde quedó la vieja");
                var r = t.Verify();
                Check(r.Ok && r.Entries == 2, "la cadena nueva tiene ese aviso y el evento: " + r);
            }
            finally
            {
                AuditTrail.DirectoryForTests = null;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // ------------------------------------------------------------------ Exportación
        static void TestExportacion20()
        {
            var a = Fila(UpdateKind.Programa, "=HYPERLINK(\"http://x\")", "evil", "Editor; con punto y coma", "winget");
            a.Detail = "token=abc123secret";
            var b = Fila(UpdateKind.Controlador, "Intel Wi-Fi", "drv", "Intel", "windowsupdate");
            b.Status = UpdateStatus.AlDia;
            var list = new List<UpdateCandidate> { a, b };
            string csv = UpdateExport.ToCsv(list);
            string[] lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            Eq(3, lines.Length, "encabezado y dos filas");
            Check(lines[0].StartsWith("Tipo;Nombre;Editor"), "separador ; (Excel en español)");
            Check(lines[1].Contains("'=HYPERLINK"), "neutraliza las fórmulas (inyección en CSV)");
            Check(lines[1].Contains("\"Editor; con punto y coma\""), "comillas cuando hay separador");
            Check(!csv.Contains("abc123secret"), "sin secretos en la exportación");
            Eq("'+1", UpdateExport.NeutralizeFormula("+1"), "+ neutralizado");
            Eq("'-1", UpdateExport.NeutralizeFormula("-1"), "- neutralizado");
            Eq("'@x", UpdateExport.NeutralizeFormula("@x"), "@ neutralizado");
            Eq("1.0", UpdateExport.NeutralizeFormula("1.0"), "lo normal queda igual");

            string json = UpdateExport.ToJson(list, null);
            object o = Json.Parse(json);
            Eq("2", Json.Str(o, "resumen.total"), "JSON: total");
            Eq(2, Json.List(o, "actualizaciones").Count, "JSON: filas");
            Check(!json.Contains("abc123secret"), "JSON sin secretos");
            Check(UpdateExport.DefaultFileName(new DateTime(2026, 11, 1, 9, 5, 0), UpdateExportFormat.Json).EndsWith("_20261101_090500.json"), "nombre del archivo");

            string dir = NuevaCarpeta("exportar");
            try
            {
                string f = Path.Combine(dir, "lista.csv");
                UpdateExport.Write(f, list, null, UpdateExportFormat.Csv);
                byte[] bytes = File.ReadAllBytes(f);
                Check(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "CSV con BOM (Excel lee bien los acentos)");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ------------------------------------------------------------------ Red en .NET 8 y WMI
        static void TestEstadosDeRed20()
        {
            Eq(WebExceptionStatus.ConnectFailure, Http.EffectiveStatus(new WebException("x", new HttpRequestExceptionShim(new SocketException((int)SocketError.ConnectionRefused)), WebExceptionStatus.UnknownError, null)), "conexión rechazada");
            Eq(WebExceptionStatus.NameResolutionFailure, Http.EffectiveStatus(new WebException("x", new HttpRequestExceptionShim(new SocketException((int)SocketError.HostNotFound)), WebExceptionStatus.UnknownError, null)), "nombre que no existe");
            Eq(WebExceptionStatus.Timeout, Http.EffectiveStatus(new WebException("x", new TimeoutException(), WebExceptionStatus.UnknownError, null)), "tiempo agotado");
            Eq(WebExceptionStatus.TrustFailure, Http.EffectiveStatus(new WebException("x", new System.Security.Authentication.AuthenticationException("cert"), WebExceptionStatus.UnknownError, null)), "certificado inválido");
            Eq(WebExceptionStatus.ProtocolError, Http.EffectiveStatus(new WebException("x", null, WebExceptionStatus.ProtocolError, null)), "un estado conocido queda igual");

            // Conexión real a un puerto cerrado de esta máquina.
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            try
            {
#pragma warning disable SYSLIB0014
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/");
#pragma warning restore SYSLIB0014
                req.Timeout = 5000;
                using (req.GetResponse()) { }
                Check(false, "un puerto cerrado tendría que fallar");
            }
            catch (WebException we) { Eq(WebExceptionStatus.ConnectFailure, Http.EffectiveStatus(we), "puerto cerrado: ConnectFailure (no UnknownError)"); }
        }

        sealed class HttpRequestExceptionShim : Exception
        {
            public HttpRequestExceptionShim(Exception inner) : base("shim", inner) { }
        }

        static void TestWmiFechas20()
        {
            var d = WmiObject.ParseDmtf("20240620000000.000000-180");
            Check(d.HasValue && d.Value.Year == 2024 && d.Value.Month == 6 && d.Value.Day == 20, "fecha DMTF");
            Check(!WmiObject.ParseDmtf("").HasValue && !WmiObject.ParseDmtf("basura").HasValue && !WmiObject.ParseDmtf(null).HasValue, "fechas DMTF inválidas");
            var o = new WmiObject(new Dictionary<string, object> { { "Name", "Intel" }, { "DriverVersion", "1.2.3" } });
            Eq("Intel", o["Name"] as string, "propiedad");
            Check(o["NoExiste"] == null, "propiedad que no existe: null");
        }

        static void TestSesion20()
        {
            var ctx = new UpdateContext();
            ctx.Os = OsInfo.Detect();
            var s = UpdateSession.Create(ctx);
            Check(s.ProgramName == AppInfo.Name && s.ProgramVersion == AppInfo.Version, "sesión con el nombre y la versión");
            Check(s.ComputerName == Environment.MachineName && s.Updates.Count == 0 && !s.UpdatesScannedLocal.HasValue, "sesión vacía de este equipo");
            Check(AppInfo.Version.StartsWith("2."), "versión 2.x");
        }
    }
}
