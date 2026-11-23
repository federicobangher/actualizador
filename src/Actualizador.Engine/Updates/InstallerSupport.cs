// Ayudas comunes de las instalaciones (descarga directa de winget, fabricantes, navegadores y
// Catálogo de Microsoft Update). Antes cada proveedor tenía su copia y ya habían empezado a diferir
// (una de ellas no toleraba que el aviso de progreso fuera null).
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Updates
{
    public static class InstallerSupport
    {
        /// <summary>La fila queda «Fallida» con el motivo, que también va al registro.</summary>
        public static void Fail(UpdateCandidate c, string message)
        {
            c.Status = UpdateStatus.Fallida;
            c.Result = message;
            Log.Warn(c.Name + ": " + message);
        }

        /// <summary>Texto de progreso de la fila; un error al avisar a la interfaz no corta la instalación.</summary>
        public static void Progress(UpdateCandidate c, Action<UpdateCandidate> changed, string text)
        {
            c.Result = text;
            if (changed == null) return;
            try { changed(c); } catch (Exception ex) { Log.Warn("Aviso de progreso: " + ex.Message); }
        }

        /// <summary>SHA-256 en hexadecimal minúscula; deja el archivo al principio (sigue abierto y bloqueado).</summary>
        public static string Sha256(Stream hold)
        {
            hold.Position = 0;
            string got;
            using (var sha = SHA256.Create()) got = BitConverter.ToString(sha.ComputeHash(hold)).Replace("-", "").ToLowerInvariant();
            hold.Position = 0;
            return got;
        }

        static readonly Regex SafeName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9 ._()\-]{0,120}$", RegexOptions.Compiled);

        /// <summary>¿Se puede usar como nombre de archivo en la carpeta de descarga? Sin rutas, «..» ni caracteres raros.</summary>
        public static bool IsSafeFileName(string name)
        {
            return !string.IsNullOrEmpty(name) && SafeName.IsMatch(name);
        }

        /// <summary>
        /// Último segmento de la ruta de la URL, tal cual. System.Uri normaliza los «..» y el
        /// separador de Windows «%5C» y Path.GetFileName los recorta, con lo que un nombre como
        /// «..\x.exe» quedaba como «x.exe» y pasaba el control. Aquí se toma el segmento crudo
        /// (sin la consulta ni el fragmento) para que esos caracteres sigan presentes y se rechacen.
        /// Devuelve null si no hay un segmento utilizable.
        /// </summary>
        public static string RawLastSegment(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string s = url;
            int q = s.IndexOf('?'); if (q >= 0) s = s.Substring(0, q);
            int h = s.IndexOf('#'); if (h >= 0) s = s.Substring(0, h);
            int slash = s.LastIndexOf('/');
            if (slash < 0 || slash + 1 >= s.Length) return null;
            string seg = s.Substring(slash + 1);
            try { return Uri.UnescapeDataString(seg); } catch { return null; }
        }

        /// <summary>
        /// Ruta del registro detallado de Windows Installer (msi-ID[-sufijo].log) en la carpeta de
        /// registros, o null si no se puede escribir ahí (elevado y con un enlace en el camino).
        /// </summary>
        public static string MsiLogPath(string id, string suffix)
        {
            try
            {
                if (!SafePaths.EnsureDataDirectory(AppInfo.LogDirectory)) return null;
                // El identificador viene de winget o del catálogo: sin separadores ni caracteres inválidos.
                string name = Fmt.SafeFileName("msi-" + (id ?? "") + (string.IsNullOrEmpty(suffix) ? "" : "-" + suffix) + ".log").Replace('/', '_').Replace('\\', '_');
                return Path.Combine(AppInfo.LogDirectory, name);
            }
            catch { return null; }
        }
    }
}
