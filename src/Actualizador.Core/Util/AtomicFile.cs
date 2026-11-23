// Escritura de archivos de estado sin riesgo de dejarlos a medias.
using System;
using System.IO;
using System.Text;

namespace Actualizador.Core
{
    /// <summary>
    /// Guarda un archivo entero o nada: escribe a un temporal en la misma carpeta, lo baja a disco y recién ahí reemplaza al
    /// anterior (File.Replace es atómico en NTFS). Antes las opciones, las páginas oficiales indicadas a mano, el estado de
    /// la Store y la revisión programada se escribían directo: un corte de luz o un cierre forzado a mitad de la escritura
    /// dejaba un JSON cortado y se perdían (se volvía a lo predeterminado sin aviso). Las listas que ya usaban un temporal
    /// lo borraban antes de mover el nuevo, y en ese instante no había ninguno de los dos.
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string text, Encoding encoding)
        {
            WriteAllBytes(path, (encoding ?? new UTF8Encoding(false)).GetBytes(text ?? ""));
        }

        public static void WriteAllBytes(string path, byte[] data)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Falta la ruta.", "path");
            string full = Path.GetFullPath(path);
            string tmp = full + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data ?? new byte[0], 0, data == null ? 0 : data.Length);
                    fs.Flush(true);
                }
                if (File.Exists(full))
                {
                    try { File.Replace(tmp, full, null, true); return; }
                    catch (PlatformNotSupportedException) { }
                    catch (IOException) { }                     // unidades FAT o de red que no lo admiten
                    catch (UnauthorizedAccessException) { }     // archivo de sólo lectura: se intenta igual abajo
                    File.Copy(tmp, full, true);
                    File.Delete(tmp);
                    return;
                }
                File.Move(tmp, full);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex) { Log.Quiet("Temporal " + tmp, ex); }
            }
        }
    }
}
