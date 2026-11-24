// Pruebas de la 2.0.41. Captura del usuario (24/11/2026): Tor Browser 16.0.1 → 16.0.3 «Pendiente: Se abrió su página (www.torproject.org)»;
// «ese navegador no actualiza, me abre la página para descargar, no es la idea, quiero que actualice».
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Actualizador.Core;
using Actualizador.Core.Security;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        const string TorRaiz = @"C:\Users\Fede\Desktop\Tor Browser";
        const string TorExe1603 = "https://dist.torproject.org/torbrowser/16.0.3/tor-browser-windows-x86_64-portable-16.0.3.exe";
        const string TorFirmante = "CN=\"The Tor Project, Inc.\", O=\"The Tor Project, Inc.\", L=Winchester, S=New Hampshire, C=US";

        static string TorFeed(string version, string binary)
        {
            return "{\"binary\":\"" + binary + "\",\"git_tag\":\"tbb-" + version + "-build1\",\"sig\":\"" + binary + ".asc\",\"version\":\"" + version + "\"}";
        }

        static InstalledProgram TorAcceso()
        {
            // Copia del Escritorio encontrada por su acceso directo: su ejecutable dice la versión de Firefox ESR.
            var p = new InstalledProgram { Name = "Tor Browser", Version = "140.5.0", Publisher = "The Tor Project", InstallLocation = TorRaiz + @"\Browser",
                                           DisplayIcon = TorRaiz + @"\Browser\firefox.exe", UninstallString = "", Scope = ShortcutPrograms.ScopeText, Architecture = "" };
            p.Unregistered = true;
            return p;
        }

        static byte[] InstaladorTorFalso()
        {
            var b = new byte[1200 * 1024];
            b[0] = (byte)'M'; b[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("NullsoftInst").CopyTo(b, 4096);
            b[9000] = 41;
            return b;
        }

        /// <summary>El archivo de versiones de The Tor Project, el instalador oficial y su SHA-256.</summary>
        static void TestTorFuentes2041()
        {
            Eq("https://aus1.torproject.org/torbrowser/update_3/release/download-windows-x86_64.json", TorBrowser.FeedUrl("x64"), "64 bits: el archivo de su plataforma");
            Eq("https://aus1.torproject.org/torbrowser/update_3/release/download-windows-i686.json", TorBrowser.FeedUrl("x86"), "32 bits");
            Eq("windows-x86_64", TorBrowser.Platform("arm64"), "ARM64 usa la de 64 bits (no hay otra para Windows)");

            var r = TorBrowser.ParseFeed(TorFeed("16.0.3", TorExe1603), "x64");
            Check(r != null && r.Version == "16.0.3" && r.Binary == TorExe1603, "versión e instalador del archivo de la plataforma");
            string viejo = "{\"comment\":\"This file is deprecated\",\"downloads\":{\"win32\":{\"ALL\":{\"binary\":\"https://dist.torproject.org/torbrowser/16.0.3/tor-browser-windows-i686-portable-16.0.3.exe\"}}," +
                           "\"win64\":{\"ALL\":{\"binary\":\"" + TorExe1603 + "\"}}},\"tag\":\"tbb-16.0.3-build1\",\"version\":\"16.0.3\"}";
            r = TorBrowser.ParseFeed(viejo, "x86");
            Check(r != null && r.Binary != null && r.Binary.EndsWith("/tor-browser-windows-i686-portable-16.0.3.exe"), "del archivo anterior, el de 32 bits para 32 bits");
            r = TorBrowser.ParseFeed(TorFeed("16.0.3", "https://evil.example/torbrowser/16.0.3/tor-browser-windows-x86_64-portable-16.0.3.exe"), "x64");
            Check(r != null && r.Version == "16.0.3" && r.Binary == null, "un instalador fuera de torproject.org no se usa");
            Check(TorBrowser.ParseFeed("{\"version\":\"16.0.3; calc\"}", "x64") == null && TorBrowser.ParseFeed("no es json", "x64") == null, "versión rara o JSON roto: nada");

            Check(TorBrowser.IsOfficialInstaller(TorExe1603, "16.0.3", "x64"), "el oficial");
            Check(TorBrowser.IsOfficialInstaller("https://archive.torproject.org/tor-package-archive/torbrowser/16.0.3/tor-browser-windows-x86_64-portable-16.0.3.exe", "16.0.3", "x64"), "el archivo de The Tor Project también");
            Check(!TorBrowser.IsOfficialInstaller(TorExe1603.Replace("https:", "http:"), "16.0.3", "x64"), "sin HTTPS, no");
            Check(!TorBrowser.IsOfficialInstaller(TorExe1603, "16.0.4", "x64"), "de otra versión, no");
            Check(!TorBrowser.IsOfficialInstaller(TorExe1603, "16.0.3", "x86"), "de otra arquitectura, no");
            Check(!TorBrowser.IsOfficialInstaller(TorExe1603 + "?x=1", "16.0.3", "x64"), "con parámetros, no");
            Check(!TorBrowser.IsOfficialInstaller("https://dist.torproject.org.evil.example/torbrowser/16.0.3/tor-browser-windows-x86_64-portable-16.0.3.exe", "16.0.3", "x64"), "un dominio que sólo empieza igual, no");
            Check(!TorBrowser.IsOfficialInstaller("https://dist.torproject.org:8443/torbrowser/16.0.3/tor-browser-windows-x86_64-portable-16.0.3.exe", "16.0.3", "x64"), "otro puerto, no");
            Check(!TorBrowser.IsAllowedDownload(new Uri("https://user@dist.torproject.org/x.exe")), "con usuario en la dirección, no");

            Eq("https://dist.torproject.org/torbrowser/16.0.3/sha256sums-signed-build.txt", TorBrowser.SumsUrl(TorExe1603), "el SHA-256 publicado al lado");
            string h = new string('a', 64);
            string sums = "1111111111111111111111111111111111111111111111111111111111111111  tor-browser-windows-i686-portable-16.0.3.exe\n" +
                          h + "  tor-browser-windows-x86_64-portable-16.0.3.exe\n";
            Eq(h.ToUpperInvariant(), TorBrowser.HashFromSums(sums, "tor-browser-windows-x86_64-portable-16.0.3.exe"), "el SHA-256 de ese archivo");
            Check(TorBrowser.HashFromSums(sums, "tor-browser-windows-x86_64-portable-16.0.4.exe") == null, "otro archivo: nada");

            Eq("x64", TorBrowser.ArchFromVersionFile("{\"version\":\"15.0.4\",\"architecture\":\"WINNT_x86_64-gcc3\"}"), "tbb_version.json de 64 bits");
            Eq("x86", TorBrowser.ArchFromVersionFile("{\"version\":\"15.0.4\",\"architecture\":\"WINNT_x86-gcc3\"}"), "tbb_version.json de 32 bits");
        }

        /// <summary>La carpeta de Tor Browser y cuándo se puede actualizar desde acá.</summary>
        static void TestTorCarpeta2041()
        {
            Func<string, bool> existe = delegate(string f) { return f == TorRaiz + @"\Browser\firefox.exe" || f == TorRaiz + @"\Browser\tbb_version.json"; };
            Eq(TorRaiz, TorBrowser.RootOf(new[] { TorRaiz + @"\Browser" }, existe), "desde la carpeta del acceso directo (…\\Browser)");
            Eq(TorRaiz, TorBrowser.RootOf(new[] { TorRaiz }, existe), "desde la carpeta registrada");
            Eq(TorRaiz, TorBrowser.RootOf(new[] { TorRaiz + @"\Browser\firefox.exe" }, existe), "desde el ejecutable");
            Check(TorBrowser.RootOf(new[] { @"C:\Program Files\Mozilla Firefox" }, existe) == null, "el Firefox del equipo no es Tor Browser");

            bool asUser;
            Check(TorBrowser.CannotInstall(TorRaiz, true, out asUser) == null && asUser, "en el Escritorio y Actualizador elevado: se instala como el usuario");
            Check(TorBrowser.CannotInstall(TorRaiz, false, out asUser) == null && !asUser, "sin elevar: como el usuario, directamente");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                // Program Files sale del registro de Windows: sólo se puede comprobar ahí (lo corre la integración continua de GitHub).
                string pf = SafePaths.ProgramFiles + @"\Tor Browser";
                Check((TorBrowser.CannotInstall(pf, false, out asUser) ?? "").Contains("administrador"), "en Program Files sin administrador: lo dice");
                Check(TorBrowser.CannotInstall(pf, true, out asUser) == null && !asUser, "en Program Files como administrador: elevado (sólo administradores escriben ahí)");
            }
            Check(TorBrowser.CannotInstall(@"C:\Users\Fede\Desktop", true, out asUser) != null, "una carpeta general (el Escritorio entero): nunca se mueve");
            Check(TorBrowser.CannotInstall(@"C:\Users\Fede\Desktop\Tor 100% Browser", true, out asUser) != null, "caracteres que rompen el guion (%): no");
            Check(TorBrowser.CannotInstall(null, true, out asUser) != null, "sin carpeta: no");
            Check(TorBrowser.CannotInstall(@"C:\Users\Fede\Desktop\Tor Browser (2)", true, out asUser) == null, "con paréntesis en el nombre: sí (el guion no usa bloques)");
        }

        /// <summary>Los guiones: apartar e instalar en silencio, copiar los datos y volver atrás.</summary>
        static void TestTorGuiones2041()
        {
            string inst = TorBrowser.InstallScript(TorRaiz, @"C:\Users\Fede\AppData\Local\Temp\Actualizador-tor-1\tor-browser-windows-x86_64-portable-16.0.3.exe", @"C:\T\r.txt");
            Check(inst.Contains("move \"" + TorRaiz + "\" \"" + TorRaiz + " (anterior)\""), "aparta la carpeta");
            Check(inst.Contains("portable-16.0.3.exe\" /S /D=" + TorRaiz + "\r\n"), "instala en silencio en la MISMA carpeta (/D= último y sin comillas)");
            Check(inst.IndexOf("goto respaldo", StringComparison.Ordinal) < inst.IndexOf("move ", StringComparison.Ordinal), "si ya hay una «(anterior)», no toca nada");
            Check(inst.IndexOf("goto nomovio", StringComparison.Ordinal) < inst.IndexOf("/S /D=", StringComparison.Ordinal), "si no se pudo apartar, no instala");
            Check(!inst.Contains(" (\r\n") && !inst.Contains("if exist \"" + TorRaiz + " (anterior)\" ("), "sin bloques entre paréntesis (la carpeta «(anterior)» los rompería)");
            Check(inst.StartsWith("@echo off\r\n\"" + SafePaths.System32 + "\\chcp.com\" 65001"), "en UTF-8 (carpetas con acentos), con chcp.com por ruta completa");
            Check(!inst.Contains("%SystemRoot%"), "sin %SystemRoot% (lo puede redefinir el usuario)");

            string datos = TorBrowser.DataScript(TorRaiz, @"C:\Windows\System32\robocopy.exe", @"C:\T\d.log", @"C:\T\r.txt");
            Check(datos.Contains("\"" + TorRaiz + @" (anterior)\Browser\TorBrowser\Data"" """ + TorRaiz + @"\Browser\TorBrowser\Data"""), "copia Data de la anterior a la nueva");
            Check(datos.Contains("/XJ") && datos.Contains("/XF torrc-defaults geoip geoip6") && !datos.Contains("/PURGE") && !datos.Contains("/MIR"), "sin seguir uniones, sin pisar los archivos de fábrica nuevos y sin borrar nada");
            Check(datos.IndexOf("if errorlevel 8 goto fallo", StringComparison.Ordinal) < datos.IndexOf("rd /s /q", StringComparison.Ordinal), "si la copia falla, la anterior no se borra");

            string undo = TorBrowser.UndoScript(TorRaiz, @"C:\T\r.txt");
            Check(undo.IndexOf("goto sinrespaldo", StringComparison.Ordinal) < undo.IndexOf("rd /s /q", StringComparison.Ordinal), "sin la anterior completa, no borra nada");
            Check(undo.Contains("move \"" + TorRaiz + " (anterior)\" \"" + TorRaiz + "\""), "devuelve la anterior a su lugar");

            bool rechazo = false;
            try { TorBrowser.InstallScript(@"C:\Users\Fede\Desktop\Tor&Browser", @"C:\x.exe", @"C:\r.txt"); } catch (ArgumentException) { rechazo = true; }
            Check(rechazo, "una ruta con & no entra al guion");
        }

        sealed class TorSimulado
        {
            public string Version = "16.0.1";
            public bool Respaldo;
            public string Sums;
            public string Firmante = TorFirmante;
            public int CodigoInstalar = 0;
            public bool InstalaDeVerdad = true;
            public int CodigoDatos = 0;
            public readonly List<string> Guiones = new List<string>();
            public readonly List<string> Textos = new List<string>();
            public readonly List<string> Bajados = new List<string>();
            public List<RunningProcess> Abiertos = new List<RunningProcess>();
        }

        static void ConTor(TorSimulado s, Action<UpdateContext, VendorProvider> prueba)
        {
            var oldFetch = VendorProvider.FetchForTests; var oldRead = VendorProvider.ReadFileForTests; var oldFile = VendorProvider.TorFileExistsForTests;
            var oldDir = VendorProvider.TorDirExistsForTests; var oldScript = VendorProvider.TorScriptForTests; var oldSums = VendorProvider.TorSumsForTests;
            var oldDownload = VendorProvider.DownloadForTests; var oldSig = VendorProvider.SignatureForTests; var oldExeV = VendorProvider.ExeVersionForTests;
            var oldSnap = RunningPrograms.SnapshotForTests; var oldPolicy = UpdateManager.PolicyForTests;
            try
            {
                byte[] exe = InstaladorTorFalso();
                if (s.Sums == null) s.Sums = Sha256De(exe) + "  tor-browser-windows-x86_64-portable-16.0.3.exe\n";
                UpdateManager.PolicyForTests = new Policy();
                VendorProvider.FetchForTests = delegate(string u) { return u == TorBrowser.FeedUrl("x64") ? TorFeed("16.0.3", TorExe1603) : null; };
                VendorProvider.ReadFileForTests = delegate(string path)
                {
                    return path.Replace('\\', '/').EndsWith("Tor Browser/Browser/tbb_version.json")
                        ? "{\"version\":\"" + s.Version + "\",\"architecture\":\"WINNT_x86_64-gcc3\",\"channel\":\"release\",\"locale\":\"es-ES\"}" : null;
                };
                VendorProvider.TorFileExistsForTests = delegate(string f) { return f == TorRaiz + @"\Browser\firefox.exe" || f == TorRaiz + @"\Browser\tbb_version.json"; };
                VendorProvider.TorDirExistsForTests = delegate(string d) { return d == TorBrowser.BackupOf(TorRaiz) && s.Respaldo; };
                VendorProvider.TorSumsForTests = delegate(string u) { return u == TorBrowser.SumsUrl(TorExe1603) ? s.Sums : null; };
                VendorProvider.DownloadForTests = delegate(string url, string file) { s.Bajados.Add(url); File.WriteAllBytes(file, exe); };
                VendorProvider.SignatureForTests = delegate(string file) { return new KeyValuePair<int, string>(0, s.Firmante); };
                VendorProvider.ExeVersionForTests = delegate(string f) { return ""; };
                RunningPrograms.SnapshotForTests = delegate { return s.Abiertos; };
                VendorProvider.TorScriptForTests = delegate(string kind, string text)
                {
                    s.Guiones.Add(kind);
                    s.Textos.Add(text);
                    if (kind == "instalar") { if (s.CodigoInstalar == 0 && s.InstalaDeVerdad) s.Version = "16.0.3"; return s.CodigoInstalar; }
                    if (kind == "datos") return s.CodigoDatos;
                    if (kind == "deshacer") { s.Version = "16.0.1"; return 0; }
                    return -1;
                };
                prueba(ContextoSimulado(true, TorAcceso()), new VendorProvider());
            }
            finally
            {
                VendorProvider.FetchForTests = oldFetch; VendorProvider.ReadFileForTests = oldRead; VendorProvider.TorFileExistsForTests = oldFile;
                VendorProvider.TorDirExistsForTests = oldDir; VendorProvider.TorScriptForTests = oldScript; VendorProvider.TorSumsForTests = oldSums;
                VendorProvider.DownloadForTests = oldDownload; VendorProvider.SignatureForTests = oldSig; VendorProvider.ExeVersionForTests = oldExeV;
                RunningPrograms.SnapshotForTests = oldSnap; UpdateManager.PolicyForTests = oldPolicy;
            }
        }

        static UpdateCandidate BuscarTor(UpdateContext ctx, VendorProvider prov)
        {
            var output = new List<UpdateCandidate>();
            prov.Scan(ctx, output, new List<string>());
            return output.Find(delegate(UpdateCandidate c) { return c.Name == "Tor Browser"; });
        }

        /// <summary>La captura: Tor Browser 16.0.1 → 16.0.3 ya no abre la página: se instala solo y conserva los datos.</summary>
        static void TestTorSeActualizaSolo2041()
        {
            var s = new TorSimulado();
            ConTor(s, delegate(UpdateContext ctx, VendorProvider prov)
            {
                var c = BuscarTor(ctx, prov);
                Check(c != null, "Tor Browser aparece");
                if (c == null) return;
                Eq("16.0.1", c.InstalledVersion, "la instalada: la de tbb_version.json (no la de Firefox ESR)");
                Eq("16.0.3", c.AvailableVersion, "la publicada por The Tor Project");
                Check(c.Status == UpdateStatus.Disponible && c.Installable && c.Recommended && c.ProviderName == VendorProvider.ProviderId,
                      "se instala desde acá y no es opcional (" + c.Detail + ")");
                Check(c.Detail.Contains("se conservan los marcadores") && c.Detail.Contains("dist.torproject.org") && c.Detail.Contains("como tu usuario"), "el detalle lo explica: " + c.Detail);
                UpdateManager.MakeManualActionable(new List<UpdateCandidate> { c });
                Eq(VendorProvider.ProviderId, c.ProviderName, "ya no pasa a «abrir la página»");

                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Eq(UpdateStatus.Instalada, c.Status, "Tor Browser 16.0.3 instalada (" + c.Result + ")");
                Eq("16.0.3", c.InstalledVersion, "quedó la 16.0.3");
                Check(c.Result.Contains("se conservaron los marcadores") && c.Result.Contains("SHA-256 publicado por The Tor Project") && c.Result.Contains("The Tor Project"), "el resultado: " + c.Result);
                Eq("instalar,datos", string.Join(",", s.Guiones.ToArray()), "apartar e instalar, después copiar los datos (sin volver atrás)");
                Check(s.Bajados.Count == 1 && s.Bajados[0] == TorExe1603, "bajó el instalador oficial de dist.torproject.org");
                Check(s.Textos[0].Contains("/S /D=" + TorRaiz + "\r\n"), "en silencio y en la misma carpeta");
            });
        }

        /// <summary>Lo que no se puede verificar no se ejecuta; y si no queda, todo vuelve atrás.</summary>
        static void TestTorVerificaYVuelveAtras2041()
        {
            // SHA-256 distinto del publicado.
            var s = new TorSimulado { Sums = new string('b', 64) + "  tor-browser-windows-x86_64-portable-16.0.3.exe\n" };
            ConTor(s, delegate(UpdateContext ctx, VendorProvider prov)
            {
                var c = BuscarTor(ctx, prov);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("SHA-256") && s.Guiones.Count == 0, "SHA-256 distinto: no se ejecuta nada (" + c.Result + ")");
            });
            // Sin el SHA-256 publicado (no responde): tampoco.
            s = new TorSimulado { Sums = "" };
            ConTor(s, delegate(UpdateContext ctx, VendorProvider prov)
            {
                var c = BuscarTor(ctx, prov);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("No se pudo leer el SHA-256") && s.Guiones.Count == 0, "sin SHA-256 publicado: no (" + c.Result + ")");
            });
            // Firmado por otro.
            s = new TorSimulado { Firmante = "CN=Evil Corp, O=Evil Corp, C=US" };
            ConTor(s, delegate(UpdateContext ctx, VendorProvider prov)
            {
                var c = BuscarTor(ctx, prov);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("Evil Corp") && s.Guiones.Count == 0, "otro firmante: no (" + c.Result + ")");
            });
            // El instalador terminó pero no dejó la 16.0.3: vuelve la anterior.
            s = new TorSimulado { InstalaDeVerdad = false };
            ConTor(s, delegate(UpdateContext ctx, VendorProvider prov)
            {
                var c = BuscarTor(ctx, prov);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("Se volvió a dejar la versión anterior") && string.Join(",", s.Guiones.ToArray()) == "instalar,deshacer",
                      "no quedó: se vuelve atrás (" + c.Result + "; " + string.Join(",", s.Guiones.ToArray()) + ")");
            });
            // No se pudieron copiar los datos: también vuelve la anterior (con sus marcadores).
            s = new TorSimulado { CodigoDatos = 40 };
            ConTor(s, delegate(UpdateContext ctx, VendorProvider prov)
            {
                var c = BuscarTor(ctx, prov);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("no se pudieron copiar tus marcadores") && string.Join(",", s.Guiones.ToArray()) == "instalar,datos,deshacer",
                      "los datos no se copiaron: se vuelve atrás (" + c.Result + ")");
            });
            // No se pudo apartar la carpeta (algo abierto adentro): no se cambió nada, sin «deshacer».
            s = new TorSimulado { CodigoInstalar = 21 };
            ConTor(s, delegate(UpdateContext ctx, VendorProvider prov)
            {
                var c = BuscarTor(ctx, prov);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("no se cambió nada") && string.Join(",", s.Guiones.ToArray()) == "instalar", "no se pudo apartar (" + c.Result + ")");
            });
            // No se pudo ejecutar el guion (sin el Explorador, desde la consola como SYSTEM): no se cambió nada, sin «deshacer».
            s = new TorSimulado { CodigoInstalar = -2 };
            ConTor(s, delegate(UpdateContext ctx, VendorProvider prov)
            {
                var c = BuscarTor(ctx, prov);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("no se cambió nada") && string.Join(",", s.Guiones.ToArray()) == "instalar", "el guion no arrancó (" + c.Result + ")");
            });
            // Quedó una «(anterior)» de un intento anterior: no se baja ni se toca nada.
            s = new TorSimulado { Respaldo = true };
            ConTor(s, delegate(UpdateContext ctx, VendorProvider prov)
            {
                var c = BuscarTor(ctx, prov);
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("(anterior)") && s.Bajados.Count == 0 && s.Guiones.Count == 0, "«(anterior)» de antes: nada (" + c.Result + ")");
            });
        }

        /// <summary>Abierto: se ofrece cerrar SÓLO Tor Browser (su carpeta), nunca el Firefox del equipo.</summary>
        static void TestTorAbierto2041()
        {
            var s = new TorSimulado();
            s.Abiertos = new List<RunningProcess>
            {
                new RunningProcess { Pid = 5100, Name = "firefox.exe", Path = TorRaiz + @"\Browser\firefox.exe" },
                new RunningProcess { Pid = 5101, Name = "tor.exe", Path = TorRaiz + @"\Browser\TorBrowser\Tor\tor.exe" },
                new RunningProcess { Pid = 6100, Name = "firefox.exe", Path = @"C:\Program Files\Mozilla Firefox\firefox.exe" }
            };
            ConTor(s, delegate(UpdateContext ctx, VendorProvider prov)
            {
                var c = BuscarTor(ctx, prov);
                var abiertos = UpdateManager.FindRunning(ctx, new List<UpdateCandidate> { c });
                Check(abiertos.Count == 1 && abiertos[0].Value.Count == 2 && !abiertos[0].Value.Exists(delegate(RunningProcess p) { return p.Pid == 6100; }),
                      "se pregunta por Tor Browser (firefox.exe y tor.exe de su carpeta), no por el Firefox del equipo");
                prov.CloseRunning = false;
                prov.Install(ctx, new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("Tor Browser está abierto") && s.Bajados.Count == 0, "abierto y sin permiso para cerrarlo: no se toca (" + c.Result + ")");
            });
        }

        /// <summary>
        /// Relevamiento de la 2.0.41: nada que corra como administrador toma rutas de %SystemRoot% (el usuario puede redefinir esa variable
        /// en su cuenta y el proceso elevado la hereda) ni abre programas por nombre suelto.
        /// </summary>
        static void TestRelevamientoRutas2041()
        {
            string instalada = SelfInstall.Script(@"C:\Windows\Temp\Actualizador-1\entrega", 7, @"C:\ps.exe", @"C:\Program Files\Actualizador\Actualizador.exe", @"C:\Windows\Temp\Actualizador-1\i.log");
            string portable = SelfInstall.PortableScript(@"C:\Windows\Temp\Actualizador-1\entrega", 7, @"C:\ps.exe", @"C:\Windows\System32\robocopy.exe", @"E:\Actualizador", @"C:\Windows\Temp\Actualizador-1\i.log");
            foreach (string s in new[] { instalada, portable })
            {
                Check(!s.Contains("%SystemRoot%") && !s.Contains("%windir%"), "los guiones de la actualización sola no usan %SystemRoot%");
                Check(s.Contains("\"" + SafePaths.System32 + "\\chcp.com\" 65001") && !s.Contains("\nchcp "), "chcp.com por ruta completa");
                Check(s.Contains("cd /d \"" + SafePaths.System32 + "\""), "salen a System32 por ruta completa");
            }
            string raiz = Raiz();
            if (raiz == null) { Check(false, "no se encontró la raíz del repositorio"); return; }
            string opciones = Leer(raiz, "src/Actualizador.App/OptionsForm.cs");
            string export = Leer(raiz, "src/Actualizador.App/MainForm.Export.cs");
            string inst = Leer(raiz, "deploy/Install-Actualizador.ps1");
            string desinst = Leer(raiz, "deploy/Uninstall-Actualizador.ps1");
            Check(!opciones.Contains("Process.Start(\"explorer.exe\""), "«Abrir la carpeta» no abre explorer.exe por nombre suelto (ni elevado)");
            Check(!export.Contains("GetEnvironmentVariable(\"SystemRoot\")"), "«Desinstalar Actualizador» no toma PowerShell de %SystemRoot%");
            Check(!inst.Contains("$env:SystemRoot") && !desinst.Contains("$env:SystemRoot"), "instalador y desinstalador: la carpeta de sistema real, no $env:SystemRoot");
            Check(desinst.Contains("(Join-Path $Sistema 'cmd.exe')"), "el borrado al reiniciar (RunOnce, como administrador) usa el cmd.exe de Windows");
        }

        static void TestVersion2041()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.41") >= 0, "Actualizador 2.0.41 o posterior: " + AppInfo.Version);
        }
    }
}
