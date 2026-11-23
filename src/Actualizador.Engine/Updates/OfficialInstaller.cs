// 2.0.15: la versión de un instalador oficial que siempre trae la última (Fortinet), leída del propio archivo.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>Lo que se averiguó del instalador oficial: dónde está y de qué versión es.</summary>
    public sealed class InstallerProbe
    {
        /// <summary>La dirección final del instalador (después de las redirecciones del fabricante).</summary>
        public string FinalUrl = "";
        public string FileName = "";
        public string Version = "";
        /// <summary>«nombre del archivo» o «recursos del instalador»: de dónde salió la versión.</summary>
        public string VersionSource = "";
    }

    /// <summary>
    /// Para los fabricantes que no publican el número de versión en su página de descargas, pero enlazan una dirección
    /// fija que siempre trae el instalador de la última versión. Fortinet: la página de descargas dice sólo «FortiClient 7.4»,
    /// y «links.fortinet.com/forticlient/win/vpnagent» redirige a «filestore.fortinet.com/…/FortiClientVPNInstaller.exe».
    /// <list type="bullet">
    /// <item>Si el nombre final del archivo lleva la versión («FortiClientOnlineInstaller_7.4.8.2066.M.exe»), se toma de ahí.</item>
    /// <item>Si no, se lee la versión de los recursos del instalador (VS_VERSIONINFO, lo que Windows muestra en
    /// Propiedades › Detalles, que es lo que Fortinet indica revisar) pidiendo sólo los pedazos del archivo que hacen falta:
    /// el encabezado, la tabla de secciones y el directorio de recursos (unos pocos KB, no el instalador entero).</item>
    /// </list>
    /// La firma del instalador no se verifica acá (sólo se compara la versión); se verifica, con el firmante esperado, antes
    /// de ejecutarlo.
    /// </summary>
    public static class OfficialInstaller
    {
        /// <summary>Sólo para las pruebas: reemplaza los pedidos por partes (dirección, desde, cantidad).</summary>
        internal static Func<string, long, int, Http.RangeResult> RangeForTests;

        const int Block = 64 * 1024;

        /// <summary>
        /// La versión del instalador al que lleva <paramref name="url"/>. <paramref name="fileVersion"/>: expresión sobre el nombre
        /// del archivo final con el grupo «v» (opcional). <paramref name="shape"/>: forma que tiene que tener una versión leída de
        /// los recursos (un instalador que no es del producto, o un recurso con otra numeración, no se compara). null y el motivo
        /// en <paramref name="error"/> si no se pudo.
        /// </summary>
        public static InstallerProbe Probe(string url, Func<Uri, bool> allowed, Regex fileVersion, Regex shape, int timeoutMs, out string error)
        {
            error = null;
            var cache = new Dictionary<long, byte[]>();
            long total = -1;
            Uri final = null;
            string current = url;
            Func<long, int, byte[]> read = delegate(long offset, int count)
            {
                return ReadCached(current, offset, count, cache, ref total, ref final, allowed, timeoutMs);
            };
            byte[] head;
            try { head = read(0, 4096); }
            catch (Exception ex) { if (ex is OperationCanceledByUserException) throw; error = Http.Describe(ex); return null; }
            if (final == null) { error = "sin respuesta"; return null; }
            // Las próximas lecturas, directo a la dirección final (sin volver a pasar por las redirecciones).
            current = final.AbsoluteUri;
            var probe = new InstallerProbe();
            probe.FinalUrl = final.AbsoluteUri;
            probe.FileName = Uri.UnescapeDataString(final.Segments.Length > 0 ? final.Segments[final.Segments.Length - 1] : "");
            if (fileVersion != null)
            {
                var m = fileVersion.Match(probe.FileName);
                if (m.Success && VersionUtil.Parse(m.Groups["v"].Value) != null)
                {
                    probe.Version = VersionUtil.Normalize(m.Groups["v"].Value);
                    probe.VersionSource = "nombre del archivo";
                    return probe;
                }
            }
            if (head.Length < 2 || head[0] != (byte)'M' || head[1] != (byte)'Z')
            {
                error = "«" + Fmt.Truncate(probe.FileName, 60) + "» no es un instalador de Windows";
                return null;
            }
            string why;
            string v;
            try { v = PeVersion.Read(read, out why); }
            catch (Exception ex) { if (ex is OperationCanceledByUserException) throw; v = null; why = Http.Describe(ex); }
            if (v == null) { error = "no se pudo leer la versión de «" + Fmt.Truncate(probe.FileName, 60) + "» (" + why + ")"; return null; }
            if (shape != null && !shape.IsMatch(v))
            {
                error = "«" + Fmt.Truncate(probe.FileName, 60) + "» dice ser la " + v + ", que no tiene la forma de una versión del producto";
                return null;
            }
            probe.Version = v;
            probe.VersionSource = "recursos del instalador";
            return probe;
        }

        static byte[] ReadCached(string url, long offset, int count, Dictionary<long, byte[]> cache, ref long total, ref Uri final,
                                 Func<Uri, bool> allowed, int timeoutMs)
        {
            if (offset < 0 || count < 0) throw new InvalidDataException("posición inválida");
            if (total >= 0 && offset >= total) return new byte[0];
            var ms = new MemoryStream();
            long pos = offset, end = offset + count;
            if (total >= 0 && end > total) end = total;
            while (pos < end)
            {
                long b = pos / Block * Block;
                byte[] data;
                if (!cache.TryGetValue(b, out data))
                {
                    if (cache.Count >= 64) throw new InvalidDataException("el instalador pide leer demasiado");
                    var r = RangeForTests != null ? RangeForTests(url, b, Block) : Http.GetRange(url, b, Block, timeoutMs, allowed);
                    if (r == null) throw new InvalidDataException("sin respuesta");
                    if (final == null) final = r.Final;
                    if (r.Total >= 0) total = r.Total;
                    data = r.Data ?? new byte[0];
                    cache[b] = data;
                    if (total < 0 && data.Length < Block) total = b + data.Length;
                    if (total >= 0 && end > total) end = total;
                }
                int from = (int)(pos - b);
                if (from >= data.Length) break;
                int n = (int)Math.Min(data.Length - from, end - pos);
                ms.Write(data, from, n);
                pos += n;
            }
            return ms.ToArray();
        }
    }

    /// <summary>
    /// La versión de un ejecutable de Windows leída de su recurso VS_VERSIONINFO, sin cargarlo ni depender de Windows
    /// (sirve con los pedazos que se bajan del servidor y en las pruebas en Linux). Primero la versión del producto (lo
    /// mismo que lee <c>FileVersionInfo.ProductVersion</c>) y, si está vacía, la del archivo.
    /// </summary>
    public static class PeVersion
    {
        const int RtVersion = 16;
        const uint FixedSignature = 0xFEEF04BD;

        /// <summary>Lee la versión con <paramref name="read"/> (posición, cantidad → bytes). null y el motivo si no la tiene.</summary>
        public static string Read(Func<long, int, byte[]> read, out string error)
        {
            error = null;
            byte[] dos = read(0, 64);
            if (dos.Length < 64 || dos[0] != (byte)'M' || dos[1] != (byte)'Z') { error = "no es un ejecutable de Windows"; return null; }
            long pe = U32(dos, 0x3C);
            if (pe <= 0 || pe > 64 * 1024 * 1024) { error = "encabezado PE inválido"; return null; }
            byte[] hdr = read(pe, 24);
            if (hdr.Length < 24 || hdr[0] != (byte)'P' || hdr[1] != (byte)'E' || hdr[2] != 0 || hdr[3] != 0) { error = "encabezado PE inválido"; return null; }
            int sections = U16(hdr, 6);
            int optSize = U16(hdr, 20);
            if (sections <= 0 || sections > 96 || optSize < 96 || optSize > 4096) { error = "encabezado PE inválido"; return null; }
            byte[] opt = read(pe + 24, optSize);
            if (opt.Length < optSize) { error = "encabezado PE incompleto"; return null; }
            int magic = U16(opt, 0);
            int dirs = magic == 0x10b ? 96 : magic == 0x20b ? 112 : -1;
            if (dirs < 0) { error = "encabezado PE de tipo desconocido"; return null; }
            if (optSize < dirs + 3 * 8) { error = "el ejecutable no tiene recursos"; return null; }
            long count = U32(opt, dirs - 4);   // NumberOfRvaAndSizes
            if (count < 3) { error = "el ejecutable no tiene recursos"; return null; }
            long rsrcRva = U32(opt, dirs + 2 * 8), rsrcSize = U32(opt, dirs + 2 * 8 + 4);
            if (rsrcRva == 0 || rsrcSize == 0) { error = "el ejecutable no tiene recursos"; return null; }
            byte[] table = read(pe + 24 + optSize, sections * 40);
            if (table.Length < sections * 40) { error = "tabla de secciones incompleta"; return null; }
            var secs = new List<long[]>();
            for (int i = 0; i < sections; i++)
            {
                int o = i * 40;
                long vsize = U32(table, o + 8), va = U32(table, o + 12), rawSize = U32(table, o + 16), raw = U32(table, o + 20);
                secs.Add(new[] { va, Math.Max(vsize, rawSize), raw, rawSize });
            }
            Func<long, long> toFile = delegate(long rva)
            {
                foreach (var s in secs)
                    if (rva >= s[0] && rva < s[0] + s[1] && rva - s[0] < s[3]) return s[2] + (rva - s[0]);
                return -1;
            };
            long root = toFile(rsrcRva);
            if (root < 0) { error = "recursos fuera de las secciones"; return null; }

            // Tipo RT_VERSION → el primer nombre → el primer idioma → la entrada con los datos.
            long dir = Entry(read, root, root, RtVersion);
            if (dir < 0) { error = "el ejecutable no tiene información de versión"; return null; }
            long dataEntry = -1;
            for (int level = 0; level < 2; level++)
            {
                long next = Entry(read, root, dir, -1);
                if (next < 0) { error = "recurso de versión vacío"; return null; }
                if (level == 1) dataEntry = next; else dir = next;
            }
            byte[] de = read(dataEntry, 16);
            if (de.Length < 16) { error = "recurso de versión incompleto"; return null; }
            long dataRva = U32(de, 0), dataSize = U32(de, 4);
            if (dataSize < 92 || dataSize > 1024 * 1024) { error = "recurso de versión inválido"; return null; }
            long dataAt = toFile(dataRva);
            if (dataAt < 0) { error = "recurso de versión fuera de las secciones"; return null; }
            byte[] vi = read(dataAt, (int)dataSize);
            return FromVersionInfo(vi, out error);
        }

        /// <summary>
        /// Una entrada del directorio de recursos en <paramref name="dirAt"/>: la que tiene ese número (o la primera si
        /// <paramref name="id"/> es -1). Devuelve la posición en el archivo del subdirectorio o de la entrada de datos; -1 si no está.
        /// </summary>
        static long Entry(Func<long, int, byte[]> read, long root, long dirAt, int id)
        {
            byte[] d = read(dirAt, 16);
            if (d.Length < 16) return -1;
            int named = U16(d, 12), ids = U16(d, 14);
            int n = Math.Min(named + ids, 4096);
            if (n == 0) return -1;
            byte[] entries = read(dirAt + 16, n * 8);
            if (entries.Length < n * 8) return -1;
            for (int i = 0; i < n; i++)
            {
                uint name = (uint)U32(entries, i * 8), off = (uint)U32(entries, i * 8 + 4);
                bool isNamed = (name & 0x80000000u) != 0;
                if (id >= 0 && (isNamed || name != (uint)id)) continue;
                return root + (off & 0x7FFFFFFFu);
            }
            return -1;
        }

        /// <summary>De los bytes del recurso VS_VERSIONINFO: la versión del producto o, si está en cero, la del archivo.</summary>
        public static string FromVersionInfo(byte[] vi, out string error)
        {
            error = null;
            if (vi == null || vi.Length < 92) { error = "recurso de versión incompleto"; return null; }
            // VS_FIXEDFILEINFO va después de la clave «VS_VERSION_INFO» (UTF-16) alineada a 4 bytes: se busca su firma.
            for (int o = 6; o + 52 <= vi.Length && o < 256; o += 2)
            {
                if ((uint)U32(vi, o) != FixedSignature) continue;
                string product = Join(U32(vi, o + 16), U32(vi, o + 20));
                string file = Join(U32(vi, o + 8), U32(vi, o + 12));
                string v = product != "0.0.0.0" ? product : file;
                if (v == "0.0.0.0") { error = "el instalador no dice su versión"; return null; }
                return v;
            }
            error = "el recurso de versión no tiene VS_FIXEDFILEINFO";
            return null;
        }

        static string Join(long ms, long ls)
        {
            return ((ms >> 16) & 0xFFFF) + "." + (ms & 0xFFFF) + "." + ((ls >> 16) & 0xFFFF) + "." + (ls & 0xFFFF);
        }

        static int U16(byte[] b, int o) { return o + 2 <= b.Length ? b[o] | (b[o + 1] << 8) : 0; }
        static long U32(byte[] b, int o) { return o >= 0 && o + 4 <= b.Length ? (long)(uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)) : 0; }
    }
}
