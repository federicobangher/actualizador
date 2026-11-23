// Pruebas de la 2.0.15: Fortinet (pedido del usuario del 11/11/2026: «https://www.fortinet.com/lat/support/product-downloads no detecta
// ningún programa de esa página»). La página no publica el número de versión: sus botones llevan a direcciones fijas de
// links.fortinet.com que redirigen al instalador de la última versión. La versión sale del nombre del archivo o de sus recursos.
using System;
using System.Collections.Generic;
using System.Text;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        const string FortiVpnLink = "https://links.fortinet.com/forticlient/win/vpnagent";
        const string FortiVpnArmLink = "https://links.fortinet.com/forticlient/winarm/vpnagent";
        const string FortiVpnFile = "https://filestore.fortinet.com/forticlient/downloads/FortiClientVPNInstaller.exe";
        const string FortiVpnArmFile = "https://filestore.fortinet.com/forticlient/downloads/FortiClientVPNInstaller_arm64.exe";
        const string FortiFullLink = "https://links.fortinet.com/forticlient/win64/fabricagent";
        const string FortiFullFile = "https://filestore.fortinet.com/forticlient/downloads/FortiClientOnlineInstaller_7.4.8.2066.M.exe";

        // ------------------------------------------------------------------ Un ejecutable de Windows armado a mano

        static void Put16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static void Put32(byte[] b, int o, long v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }

        /// <summary>
        /// Un PE32+ con una sola sección .rsrc (en <paramref name="rsrcAt"/> del archivo) que tiene el recurso de versión: versión del
        /// producto y del archivo. <paramref name="withVersion"/> en false: recursos sin RT_VERSION (sólo un ícono).
        /// </summary>
        static byte[] FortiPe(string product, string file, int rsrcAt, int totalSize, bool withVersion)
        {
            var b = new byte[Math.Max(totalSize, rsrcAt + 0x200)];
            b[0] = (byte)'M'; b[1] = (byte)'Z';
            Put32(b, 0x3C, 0x80);
            int pe = 0x80;
            b[pe] = (byte)'P'; b[pe + 1] = (byte)'E';
            Put16(b, pe + 4, 0x8664);
            Put16(b, pe + 6, 1);                 // una sección
            Put16(b, pe + 20, 240);              // encabezado opcional PE32+
            int opt = pe + 24;
            Put16(b, opt, 0x20b);
            Put32(b, opt + 108, 16);             // NumberOfRvaAndSizes
            Put32(b, opt + 112 + 16, 0x1000);    // directorio de recursos: RVA
            Put32(b, opt + 112 + 20, 0x200);     //                        tamaño
            int sec = opt + 240;
            Encoding.ASCII.GetBytes(".rsrc").CopyTo(b, sec);
            Put32(b, sec + 8, 0x200); Put32(b, sec + 12, 0x1000); Put32(b, sec + 16, 0x200); Put32(b, sec + 20, rsrcAt);

            int r = rsrcAt;
            // Raíz: una entrada por número (16 = RT_VERSION, o 3 = RT_ICON) → subdirectorio en 0x18.
            Put16(b, r + 14, 1);
            Put32(b, r + 16, withVersion ? 16 : 3); Put32(b, r + 20, 0x80000000L | 0x18);
            // Nombre 1 → subdirectorio en 0x30.
            Put16(b, r + 0x18 + 14, 1);
            Put32(b, r + 0x18 + 16, 1); Put32(b, r + 0x18 + 20, 0x80000000L | 0x30);
            // Idioma 0x409 → entrada de datos en 0x48.
            Put16(b, r + 0x30 + 14, 1);
            Put32(b, r + 0x30 + 16, 0x409); Put32(b, r + 0x30 + 20, 0x48);
            // Datos: RVA 0x1058, 92 bytes.
            Put32(b, r + 0x48, 0x1058); Put32(b, r + 0x48 + 4, 92);
            int vi = r + 0x58;
            Put16(b, vi, 92); Put16(b, vi + 2, 52);
            Encoding.Unicode.GetBytes("VS_VERSION_INFO\0").CopyTo(b, vi + 6);
            int fx = vi + 40;
            Put32(b, fx, 0xFEEF04BD); Put32(b, fx + 4, 0x10000);
            Action<string, int> ver = delegate(string v, int at)
            {
                var n = v.Split('.');
                Put32(b, at, (long.Parse(n[0]) << 16) | long.Parse(n[1]));
                Put32(b, at + 4, (long.Parse(n[2]) << 16) | long.Parse(n[3]));
            };
            ver(file, fx + 8);
            ver(product, fx + 16);
            return b;
        }

        static Func<long, int, byte[]> Lector(byte[] data)
        {
            return delegate(long o, int c)
            {
                if (o >= data.Length) return new byte[0];
                int n = (int)Math.Min(c, data.Length - o);
                var x = new byte[n];
                Array.Copy(data, o, x, 0, n);
                return x;
            };
        }

        /// <summary>
        /// Simula a Fortinet: links.fortinet.com redirige al instalador en filestore.fortinet.com, que se sirve por partes.
        /// <paramref name="pedidos"/>: cada pedido (dirección y desde dónde).
        /// </summary>
        static Func<string, long, int, Http.RangeResult> FortiServidor(Dictionary<string, string> redirige, Dictionary<string, byte[]> archivos, List<string> pedidos)
        {
            return delegate(string url, long from, int count)
            {
                if (pedidos != null) pedidos.Add(url + "@" + from);
                string final = redirige.ContainsKey(url) ? redirige[url] : url;
                byte[] data;
                if (!archivos.TryGetValue(final, out data)) throw new InvalidOperationException("404 " + final);
                var r = new Http.RangeResult();
                r.Final = new Uri(final);
                r.Partial = true;
                r.Total = data.Length;
                r.Data = Lector(data)(from, count);
                return r;
            };
        }

        static InstalledProgram FortiInstalado(string name, string version, string arch)
        {
            var p = Vc(name, version, arch);
            p.Publisher = "Fortinet Technologies Inc";
            p.InstallLocation = @"C:\Program Files\Fortinet\FortiClient\";
            p.RegistryKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{0DC51760-4FB7-41F3-8967-D3DEC9D320EB}";
            return p;
        }

        // ------------------------------------------------------------------ El catálogo

        static void TestFortinetCatalogo2015()
        {
            var vpn = VendorCatalog.Find("forticlient-vpn");
            var full = VendorCatalog.Find("forticlient");
            Check(vpn != null && full != null, "FortiClient VPN y FortiClient en el catálogo de fuentes oficiales");
            if (vpn == null || full == null) return;
            Check(vpn.Feed == VendorFeed.OfficialInstaller && full.Feed == VendorFeed.OfficialInstaller, "se consulta el instalador que enlaza la página de descargas");
            Eq("https://www.fortinet.com/lat/support/product-downloads", vpn.InfoUrl, "«Abrir sitio del fabricante» abre la página que indicó el usuario");
            Eq(FortiVpnLink, vpn.ArchUrlFor("x64"), "VPN x64: el botón «Solo VPN» de la página");
            Eq(FortiVpnArmLink, vpn.ArchUrlFor("arm64"), "VPN ARM64: el suyo");
            Eq(FortiVpnLink, vpn.ArchUrlFor(""), "sin arquitectura: la de x64");
            Eq(FortiFullLink, full.ArchUrlFor("AMD64"), "FortiClient ZTNA/EPP: el de 64 bits");

            // Sólo instaladores firmados por Fortinet, en silencio, que no bajen de versión.
            Check(vpn.Install == VendorInstall.SignedInstaller && vpn.InstallerHasProductVersion, "FortiClient VPN se instala desde acá (y nunca una versión más vieja)");
            Check(vpn.Signers.Length > 0 && vpn.Signers[0] == "Fortinet Technologies (Canada) ULC", "sólo con la firma de Fortinet");
            Check(InstallerRules.SignerAccepted("CN=\"Fortinet Technologies (Canada) ULC\", O=\"Fortinet Technologies (Canada) ULC\", L=Burnaby, S=British Columbia, C=CA", vpn.Signers),
                  "acepta el certificado con el que firma Fortinet");
            Check(!InstallerRules.SignerAccepted("CN=Fortinet Fans LLC, O=Fortinet Fans LLC", vpn.Signers), "no acepta otro que se llame parecido");
            Check(InstallerRules.IsSafeArgs(vpn.SilentArgs) && vpn.SilentArgs == "/quiet /norestart", "en silencio y sin reiniciar");
            Check(full.Install == VendorInstall.None && !full.Recommended, "FortiClient ZTNA/EPP: se compara, no se instala (lo administra FortiClient EMS)");

            // Dominios: links. y filestore.fortinet.com, por HTTPS; nada más.
            foreach (string ok in new[] { FortiVpnLink, FortiVpnFile, FortiFullFile })
                Check(vpn.Allows(new Uri(ok)), "permitido: " + ok);
            foreach (string no in new[] { "http://filestore.fortinet.com/forticlient/downloads/FortiClientVPNInstaller.exe", "https://fortinet.com.ejemplo.net/x.exe",
                                          "https://filestore.fortinet.com:8443/x.exe", "https://notfortinet.com/x.exe" })
                Check(!vpn.Allows(new Uri(no)), "rechazado: " + no);

            // Qué entradas de «Aplicaciones» son cada cosa.
            Eq("forticlient-vpn", IdOf(VendorCatalog.Match(FortiInstalado("FortiClient VPN", "7.4.3.1790", "x64"))), "«FortiClient VPN»");
            Eq("forticlient", IdOf(VendorCatalog.Match(FortiInstalado("FortiClient", "7.2.4.0850", "x64"))), "«FortiClient» (ZTNA/EPP)");
            foreach (string otro in new[] { "FortiClient EMS", "FortiClient SSO Mobility Agent", "FortiToken Windows", "FortiClientVPN Helper" })
                Eq(null, IdOf(VendorCatalog.Match(FortiInstalado(otro, "7.4.0.1", "x64"))), "«" + otro + "» no es ninguno de los dos");

            // La versión en el nombre del archivo final.
            var m = full.Version.Match("FortiClientOnlineInstaller_7.4.8.2066.M.exe");
            Check(m.Success && m.Groups["v"].Value == "7.4.8.2066", "FortiClientOnlineInstaller_7.4.8.2066.M.exe → 7.4.8.2066");
            Check(!vpn.Version.Match("FortiClientVPNInstaller.exe").Success, "FortiClientVPNInstaller.exe no dice la versión: se leen sus recursos");
            Check(vpn.VersionShape.IsMatch("7.4.3.1790") && vpn.VersionShape.IsMatch("8.0.0.12") && !vpn.VersionShape.IsMatch("1.0.0.5") && !vpn.VersionShape.IsMatch("7.4"),
                  "forma de una versión de FortiClient");
        }

        // ------------------------------------------------------------------ La versión de un ejecutable, leída por partes

        static void TestVersionDelInstalador2015()
        {
            string err;
            Eq("7.4.3.8758", PeVersion.Read(Lector(FortiPe("7.4.3.8758", "7.4.3.8758", 0x400, 0x800, true)), out err), "versión del producto en VS_FIXEDFILEINFO");
            Eq("7.4.3.4799", PeVersion.Read(Lector(FortiPe("7.4.3.4799", "1.0.0.1", 0x400, 0x800, true)), out err), "manda la del producto (lo que muestra Propiedades › Detalles)");
            Eq("7.4.3.1790", PeVersion.Read(Lector(FortiPe("0.0.0.0", "7.4.3.1790", 0x400, 0x800, true)), out err), "sin versión del producto: la del archivo");
            Eq(null, PeVersion.Read(Lector(FortiPe("0.0.0.0", "0.0.0.0", 0x400, 0x800, true)), out err), "en cero: no dice su versión");
            Eq(null, PeVersion.Read(Lector(FortiPe("7.4.3.1", "7.4.3.1", 0x400, 0x800, false)), out err), "sin RT_VERSION");
            Check(err != null && err.Contains("no tiene información de versión"), "con el motivo: " + err);
            Eq(null, PeVersion.Read(Lector(Encoding.ASCII.GetBytes("<html>Not found</html>".PadRight(400))), out err), "una página de error no es un ejecutable");
            var roto = FortiPe("7.4.3.1", "7.4.3.1", 0x400, 0x800, true);
            Put32(roto, 0x80 + 24 + 240 + 20, 0x7FFFFFF0);   // la sección apunta fuera del archivo
            Eq(null, PeVersion.Read(Lector(roto), out err), "sección fuera del archivo: null, sin excepción");
            var trunco = new byte[0x200];
            Array.Copy(FortiPe("7.4.3.1", "7.4.3.1", 0x400, 0x800, true), trunco, trunco.Length);
            Eq(null, PeVersion.Read(Lector(trunco), out err), "archivo cortado: null, sin excepción");

            // Por partes: los recursos a 320 KB del principio de un instalador de 400 KB → pocos pedidos chicos.
            var pe = FortiPe("7.4.3.8758", "7.4.3.8758", 0x50000, 0x64000, true);
            var pedidos = new List<string>();
            var old = OfficialInstaller.RangeForTests;
            try
            {
                OfficialInstaller.RangeForTests = FortiServidor(new Dictionary<string, string> { { FortiVpnLink, FortiVpnFile } },
                                                                new Dictionary<string, byte[]> { { FortiVpnFile, pe } }, pedidos);
                var probe = OfficialInstaller.Probe(FortiVpnLink, VendorCatalog.Find("forticlient-vpn").Allows, VendorCatalog.Find("forticlient-vpn").Version,
                                                    VendorCatalog.Find("forticlient-vpn").VersionShape, 20000, out err);
                Check(probe != null, "se leyó la versión del instalador (" + err + ")");
                if (probe != null)
                {
                    Eq("7.4.3.8758", probe.Version, "la versión de sus recursos");
                    Eq(FortiVpnFile, probe.FinalUrl, "la dirección final, en filestore.fortinet.com");
                    Eq("FortiClientVPNInstaller.exe", probe.FileName, "el archivo");
                    Eq("recursos del instalador", probe.VersionSource, "de dónde salió");
                }
                Check(pedidos.Count <= 3, "pocos pedidos (" + string.Join(", ", pedidos.ToArray()) + ")");
                Check(pedidos.Count > 0 && pedidos[0] == FortiVpnLink + "@0", "el primero, a la dirección de la página");
                Check(pedidos.TrueForAll(delegate(string x) { return x == FortiVpnLink + "@0" || x.StartsWith(FortiVpnFile + "@", StringComparison.Ordinal); }),
                      "los siguientes, directo al archivo (sin repetir la redirección)");

                // Nombre con versión (FortiClient ZTNA/EPP): un solo pedido, no hace falta leer el archivo.
                pedidos.Clear();
                OfficialInstaller.RangeForTests = FortiServidor(new Dictionary<string, string> { { FortiFullLink, FortiFullFile } },
                                                                new Dictionary<string, byte[]> { { FortiFullFile, FortiPe("1.0.0.0", "1.0.0.0", 0x400, 0x800, true) } }, pedidos);
                var full = VendorCatalog.Find("forticlient");
                var p2 = OfficialInstaller.Probe(FortiFullLink, full.Allows, full.Version, full.VersionShape, 20000, out err);
                Check(p2 != null && p2.Version == "7.4.8.2066" && p2.VersionSource == "nombre del archivo", "FortiClientOnlineInstaller_7.4.8.2066.M.exe → 7.4.8.2066 (" + err + ")");
                Eq(1, pedidos.Count, "un solo pedido");

                // Un instalador que dice otra numeración (1.0.0.5): no se compara.
                OfficialInstaller.RangeForTests = FortiServidor(new Dictionary<string, string> { { FortiVpnLink, FortiVpnFile } },
                                                                new Dictionary<string, byte[]> { { FortiVpnFile, FortiPe("1.0.0.5", "1.0.0.5", 0x400, 0x800, true) } }, null);
                var vpn = VendorCatalog.Find("forticlient-vpn");
                Eq(null, OfficialInstaller.Probe(FortiVpnLink, vpn.Allows, vpn.Version, vpn.VersionShape, 20000, out err), "versión con otra forma: no se compara");
                Check(err != null && err.Contains("1.0.0.5"), "y lo dice: " + err);

                // El servidor devuelve una página en vez del instalador.
                OfficialInstaller.RangeForTests = FortiServidor(new Dictionary<string, string> { { FortiVpnLink, FortiVpnFile } },
                                                                new Dictionary<string, byte[]> { { FortiVpnFile, Encoding.ASCII.GetBytes("<html>Mantenimiento</html>") } }, null);
                Eq(null, OfficialInstaller.Probe(FortiVpnLink, vpn.Allows, vpn.Version, vpn.VersionShape, 20000, out err), "una página no es el instalador");
                Check(err != null && err.Contains("no es un instalador"), "motivo: " + err);
            }
            finally { OfficialInstaller.RangeForTests = old; }
        }

        // ------------------------------------------------------------------ La búsqueda completa

        static void TestFortinetBusqueda2015()
        {
            var old = OfficialInstaller.RangeForTests;
            var pedidos = new List<string>();
            var archivos = new Dictionary<string, byte[]>
            {
                { FortiVpnFile, FortiPe("7.4.3.8758", "7.4.3.8758", 0x8000, 0x20000, true) },
                { FortiVpnArmFile, FortiPe("7.4.3.8758", "7.4.3.8758", 0x8000, 0x20000, true) },
                { FortiFullFile, FortiPe("7.4.8.2066", "7.4.8.2066", 0x400, 0x800, true) }
            };
            var redirige = new Dictionary<string, string> { { FortiVpnLink, FortiVpnFile }, { FortiVpnArmLink, FortiVpnArmFile }, { FortiFullLink, FortiFullFile } };
            try
            {
                OfficialInstaller.RangeForTests = FortiServidor(redirige, archivos, pedidos);

                // --- FortiClient VPN 7.4.3.1790 y Fortinet publica la 7.4.3.8758: disponible, instalable, del archivo oficial.
                var output = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, FortiInstalado("FortiClient VPN", "7.4.3.1790", "x64")), output, new List<string>());
                var row = FilaDe(output, "FortiClient VPN");
                Check(row != null && row.Status == UpdateStatus.Disponible && row.Installable && row.Recommended,
                      "7.4.3.1790 → disponible la 7.4.3.8758, instalable (" + (row != null ? row.Status + " " + row.Detail : "sin fila") + ")");
                if (row != null)
                {
                    Eq("7.4.3.8758", row.AvailableVersion, "la del instalador oficial");
                    Eq("7.4.3.1790", row.InstalledVersion, "la instalada");
                    var plan = row.ProviderTag as VendorPlan;
                    Eq(FortiVpnFile, plan != null ? plan.InstallerUrl : null, "se instala el archivo al que lleva la página (filestore.fortinet.com)");
                    Check(row.Detail.StartsWith("Fortinet publica la 7.4.3.8758 (el instalador oficial de su página de descargas, FortiClientVPNInstaller.exe).") &&
                          row.Detail.Contains("Fortinet Technologies (Canada) ULC") && row.Detail.Contains("la conexión se corta"), "detalle: " + row.Detail);
                    Eq("https://www.fortinet.com/lat/support/product-downloads", row.InfoUrl, "su página de descargas");
                    Eq("Fortinet (fortinet.com)", row.Source, "origen");
                }

                // --- La misma versión: al día, y dice contra qué se comparó.
                var out2 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, FortiInstalado("FortiClient VPN", "7.4.3.8758", "x64")), out2, new List<string>());
                var r2 = FilaDe(out2, "FortiClient VPN");
                Check(r2 != null && r2.Status == UpdateStatus.AlDia, "7.4.3.8758: al día (" + (r2 != null ? r2.Status + " " + r2.Detail : "sin fila") + ")");
                if (r2 != null) Check(r2.Detail.Contains("instalador oficial que enlaza su página de descargas (FortiClientVPNInstaller.exe)"), "detalle: " + r2.Detail);

                // --- ARM64: su propio instalador.
                pedidos.Clear();
                var out3 = new List<UpdateCandidate>();
                var arm = ContextoSimulado(true, FortiInstalado("FortiClient VPN", "7.4.3.1790", "arm64"));
                arm.Os.NativeArchitecture = "arm64";
                new VendorProvider().Scan(arm, out3, new List<string>());
                var r3 = FilaDe(out3, "FortiClient VPN");
                var plan3 = r3 != null ? r3.ProviderTag as VendorPlan : null;
                Eq(FortiVpnArmFile, plan3 != null ? plan3.InstallerUrl : null, "ARM64: FortiClientVPNInstaller_arm64.exe");
                Check(pedidos.Count > 0 && pedidos[0].StartsWith(FortiVpnArmLink, StringComparison.Ordinal), "consultó el enlace de ARM64");

                // --- FortiClient ZTNA/EPP: se compara con el instalador de la página; no se instala (EMS).
                var out5 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, FortiInstalado("FortiClient", "7.2.4.0850", "x64")), out5, new List<string>());
                var r5 = FilaDe(out5, "FortiClient");
                Check(r5 != null && r5.Status == UpdateStatus.Disponible && !r5.Installable && !r5.Recommended,
                      "FortiClient 7.2.4.0850 → disponible la 7.4.8.2066, no instalable desde acá (" + (r5 != null ? r5.Status + " " + r5.Detail : "sin fila") + ")");
                if (r5 != null)
                {
                    Eq("7.4.8.2066", r5.AvailableVersion, "la del nombre del instalador");
                    Check(r5.Detail.Contains("FortiClient EMS"), "dice quién lo actualiza: " + r5.Detail);
                }
                var out6 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, FortiInstalado("FortiClient", "7.4.8.2066", "x64")), out6, new List<string>());
                Check(out6.Count == 1 && out6[0].Status == UpdateStatus.AlDia, "FortiClient 7.4.8.2066: al día");

                // --- Los dos instalados: una consulta por instalador, una fila cada uno.
                pedidos.Clear();
                var out7 = new List<UpdateCandidate>();
                new VendorProvider().Scan(ContextoSimulado(true, FortiInstalado("FortiClient VPN", "7.4.3.1790", "x64"), FortiInstalado("FortiClient", "7.4.8.2066", "x64")), out7, new List<string>());
                Eq(2, out7.Count, "dos filas");

                // --- Fortinet no contesta: error con el motivo (no «al día», no «no verificable»).
                OfficialInstaller.RangeForTests = delegate(string u, long f, int c) { throw new InvalidOperationException("links.fortinet.com no responde"); };
                var out8 = new List<UpdateCandidate>();
                var notas = new List<string>();
                new VendorProvider().Scan(ContextoSimulado(true, FortiInstalado("FortiClient VPN", "7.4.3.1790", "x64")), out8, notas);
                Check(out8.Count == 1 && out8[0].Status == UpdateStatus.Error && out8[0].Detail.Contains("no responde"), "sin respuesta: error con el motivo (" +
                      (out8.Count > 0 ? out8[0].Detail : "sin fila") + ")");
                Check(notas.Exists(delegate(string n) { return n.Contains("FortiClient VPN"); }), "y queda en las notas de la búsqueda");
            }
            finally { OfficialInstaller.RangeForTests = old; }
        }
    }
}
