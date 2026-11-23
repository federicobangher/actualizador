// Pruebas de la 1.7.5: catálogo de fuentes oficiales en un archivo, ampliable sin recompilar.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Actualizador.Core;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        const string CatalogoUsb = @"{ ""programas"": [ {
  ""id"": ""usb-disk-security"", ""producto"": ""USB Disk Security"",
  ""nombre"": ""^USB Disk Security"",
  ""pagina"": ""https://www.zbshareware.com/download.html"",
  ""version"": ""USB Disk Security (?:v|versi[oó]n )?(?<v>\\d+\\.\\d+(?:\\.\\d+)*)[^<]*<a href=\""(?<url>[^\""]+\\.exe)\"""",
  ""dominios"": [""dl.zbshareware.com""],
  ""silencioso"": ""/VERYSILENT"", ""procesos"": [""USBGuard.exe""], ""nota"": ""Lo mantiene Fede."" } ] }";

        static string EntradaCon(string campo, string valor)
        {
            var d = new Dictionary<string, object>();
            d["id"] = "prueba"; d["producto"] = "Prueba"; d["nombre"] = "^Prueba"; d["pagina"] = "https://www.prueba.com/";
            d["version"] = "Prueba (?<v>\\d+\\.\\d+)";
            if (valor == null) d.Remove(campo); else d[campo] = valor;
            var root = new Dictionary<string, object>();
            root["programas"] = new List<object> { d };
            return Json.Serialize(root, false);
        }

        static void TestCatalogoFuentes175()
        {
            string error;
            var lista = SourceCatalog.Parse(CatalogoUsb, out error);
            Eq(null, error, "entrada válida, sin error");
            Eq(1, lista.Count, "una entrada");
            var c = lista[0];
            Eq("cat-usb-disk-security", c.Id, "identificador con prefijo (no choca con los incorporados)");
            Eq("zbshareware.com", c.Domain, "dominio: el de la página, sin www");
            Check(c.FromCatalogFile && c.Signers != null && c.Signers.Length == 0, "sin firmante propio: se exige el del programa instalado");
            Eq(VendorInstall.SignedInstaller, c.Install, "con grupo «url»: se puede instalar");
            Eq(VendorFeed.PageRegex, c.Feed, "se lee la página");
            Eq("USBGuard", string.Join(",", c.Processes), "procesos sin «.exe»");
            Check(c.Allows(new Uri("https://dl.zbshareware.com/usbdisk.exe")) && !c.Allows(new Uri("https://otro.com/x.exe")) && !c.Allows(new Uri("http://www.zbshareware.com/x.exe")),
                  "descarga sólo HTTPS y de los dominios indicados");
            Check(c.Applies(Vc("USB Disk Security 6.9", "6.9.0.0", "x86")) && !c.Applies(Vc("Otro programa", "1.0", "x86")), "reconoce el programa por el nombre");

            // La página: la versión más nueva y el enlace relativo completado.
            string url;
            string page = "<p>USB Disk Security v6.9.3.1 <a href=\"https://dl.zbshareware.com/usbdisk.exe\">bajar</a></p>" +
                          "<p>USB Disk Security 6.8.0 <a href=\"https://dl.zbshareware.com/viejo.exe\">anterior</a></p>";
            Eq("6.9.3.1", VendorCatalog.ParsePage(c, page, out url), "versión de la página");
            Eq("https://dl.zbshareware.com/usbdisk.exe", url, "y su instalador");
            VendorCatalog.ParsePage(c, "<p>USB Disk Security 7.0 <a href=\"https://malo.com/x.exe\">x</a></p>", out url);
            Eq(null, url, "un enlace a otro dominio no se usa");

            // Entradas inválidas: se saltean con el motivo.
            Eq(1, SourceCatalog.Parse(EntradaCon("id", "prueba"), out error).Count, "la base de las pruebas es válida");
            Eq(VendorInstall.None, SourceCatalog.Parse(EntradaCon("id", "prueba"), out error)[0].Install, "sin instalador ni «url»: sólo compara (abre la página)");
            Eq(0, SourceCatalog.Parse(EntradaCon("pagina", "http://www.prueba.com/"), out error).Count, "página sin HTTPS: no");
            Check(error != null && error.Contains("HTTPS"), "con el motivo: " + error);
            Eq(0, SourceCatalog.Parse(EntradaCon("pagina", "https://192.168.0.10/"), out error).Count, "página de la red local: no");
            Eq(0, SourceCatalog.Parse(EntradaCon("id", "Con Espacios!"), out error).Count, "id inválido: no");
            Eq(0, SourceCatalog.Parse(EntradaCon("version", "Prueba (\\d+)"), out error).Count, "sin el grupo «v»: no");
            Eq(0, SourceCatalog.Parse(EntradaCon("version", "Prueba (?<v>[0-9"), out error).Count, "expresión rota: no (no se cae)");
            Eq(0, SourceCatalog.Parse(EntradaCon("nombre", ""), out error).Count, "sin nombre: no (aplicaría a todo)");
            Eq(0, SourceCatalog.Parse(EntradaCon("instalador", "https://otro-sitio.com/setup.exe"), out error).Count, "instalador de otro dominio: no");
            Eq(0, SourceCatalog.Parse(EntradaCon("instalador", "http://www.prueba.com/setup.exe"), out error).Count, "instalador sin HTTPS: no");
            Eq(1, SourceCatalog.Parse(EntradaCon("instalador", "https://www.prueba.com/setup.exe"), out error).Count, "instalador del dominio: sí");
            Eq(0, SourceCatalog.Parse(EntradaCon("silencioso", "/S & calc.exe"), out error).Count, "interruptores con caracteres peligrosos: no");
            Eq(0, SourceCatalog.Parse("{ no es json", out error).Count, "JSON roto: nada");
            Check(error != null, "con el motivo");
            Eq(0, SourceCatalog.Parse("{\"firmantes\":[\"CN=Yo\"]}", out error).Count, "sin «programas»: nada");

            // El archivo que se entrega: se lee sin errores y su ejemplo es válido.
            string shipped = Path.GetFullPath(Path.Combine(samples, "..", "..", "res", SourceCatalog.FileName));
            if (File.Exists(shipped))
            {
                string json = File.ReadAllText(shipped, Encoding.UTF8);
                Eq(0, SourceCatalog.Parse(json, out error).Count, "el archivo entregado empieza vacío");
                Eq(null, error, "y se lee bien");
                string why;
                Check(SourceCatalog.Entry(Json.Path(Json.Parse(json), "_ejemplo"), out why) != null, "su ejemplo es una entrada válida (" + why + ")");
            }

            // Mezcla con las incorporadas: dos archivos, sin repetir, y se relee si cambia.
            string dir = Path.Combine(Path.GetTempPath(), "sp175-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var oldPaths = SourceCatalog.PathsForTests;
            try
            {
                string a = Path.Combine(dir, "a.json"), b = Path.Combine(dir, "b.json");
                File.WriteAllText(a, CatalogoUsb, Encoding.UTF8);
                File.WriteAllText(b, CatalogoUsb, Encoding.UTF8);
                SourceCatalog.PathsForTests = new[] { a, b, Path.Combine(dir, "no-existe.json") };
                int builtIn = VendorCatalog.BuiltIn.Count;
                Eq(builtIn + 1, VendorCatalog.All.Count, "las incorporadas más una (la repetida del segundo archivo no se suma)");
                Check(VendorCatalog.All.Exists(delegate(VendorCheck x) { return x.Id == "cat-usb-disk-security"; }), "está la del catálogo");

                // De punta a punta: la búsqueda de fabricantes usa la entrada del catálogo.
                var oldFetch = VendorProvider.FetchForTests;
                try
                {
                    VendorProvider.FetchForTests = delegate(string u)
                    {
                        return u.Contains("zbshareware.com/download") ? "<p>USB Disk Security 6.9.3.1 <a href=\"https://dl.zbshareware.com/usbdisk.exe\">Download</a>" : null;
                    };
                    var usb = Vc("USB Disk Security", "6.8.0.0", "x86");
                    usb.InstallLocation = "C:\\Program Files (x86)\\USB Disk Security";
                    var ctx = ContextoSimulado(true, usb);
                    var output = new List<UpdateCandidate>();
                    new VendorProvider().Scan(ctx, output, new List<string>());
                    var row = output.Find(delegate(UpdateCandidate x) { return x.Name == "USB Disk Security"; });
                    Check(row != null && row.Status == UpdateStatus.Disponible && row.AvailableVersion == "6.9.3.1", "USB Disk Security: disponible la 6.9.3.1 según el catálogo (" + (row != null ? row.Status + " " + row.AvailableVersion + " " + row.Detail : "sin fila") + ")");
                    if (row != null)
                    {
                        var plan = row.ProviderTag as VendorPlan;
                        Check(plan != null && plan.InstallerUrl == "https://dl.zbshareware.com/usbdisk.exe", "con el instalador de la página");
                        Check(row.Detail != null && row.Detail.Contains("mismo fabricante que el programa instalado"), "el detalle dice qué firma se exige: " + row.Detail);
                    }
                }
                finally { VendorProvider.FetchForTests = oldFetch; }
                File.WriteAllText(b, EntradaCon("id", "otra"), Encoding.UTF8);
                File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(1));
                Eq(builtIn + 2, VendorCatalog.All.Count, "al cambiar el archivo se relee");
                File.WriteAllText(b, "basura", Encoding.UTF8);
                File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(2));
                Eq(builtIn + 1, VendorCatalog.All.Count, "un archivo roto no tira el resto");
                SourceCatalog.PathsForTests = new[] { Path.Combine(dir, "no-existe.json") };
                Eq(builtIn, VendorCatalog.All.Count, "sin archivos: sólo las incorporadas");
            }
            finally
            {
                SourceCatalog.PathsForTests = oldPaths;
                try { Directory.Delete(dir, true); } catch { }
            }

            // La descarga del catálogo sólo por HTTPS.
            Eq(null, SourceCatalog.Refresh("http://www.ejemplo.com/fuentes.json"), "dirección sin HTTPS: no se baja");
            Eq(null, SourceCatalog.Refresh(""), "sin dirección: nada");
        }
    }
}
