// Paquete de diagnóstico: un .zip con lo necesario para saber por qué un programa no se actualizó en un equipo.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Updates
{
    /// <summary>
    /// Arma un .zip con la lista de actualizaciones tal como se ve (versiones, estado, resultado y detalle
    /// completos) y los registros de Actualizador (actividad, salida cruda de «winget upgrade» y
    /// «winget list», registros de Windows Installer). Antes había que buscar a mano tres archivos en
    /// %LOCALAPPDATA%\Actualizador\logs para entender por qué un programa no se actualizaba en un equipo.
    /// Todo pasa por <see cref="Redactor.Text"/> (contraseñas y tokens que pudieran figurar en una línea de comandos).
    /// </summary>
    public static class DiagnosticPackage
    {
        /// <summary>Más grande que esto, de cada registro se guarda sólo el final (lo más reciente).</summary>
        public const int MaxFileBytes = 2 * 1024 * 1024;
        /// <summary>Tope de todo lo que se agrega de la carpeta de registros.</summary>
        public const long MaxTotalBytes = 8L * 1024 * 1024;

        public static string DefaultFileName(DateTime now)
        {
            return "Actualizador-diagnostico-" + now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".zip";
        }

        /// <summary>La lista de actualizaciones en texto, una fila por bloque, sin cortar nada.</summary>
        public static string UpdatesText(IList<UpdateCandidate> updates, IList<string> notes, string systemLine)
        {
            var sb = new StringBuilder();
            sb.Append(AppInfo.Title).Append(' ').Append(AppInfo.Version).Append("\r\n");
            if (!string.IsNullOrEmpty(systemLine)) sb.Append(systemLine).Append("\r\n");
            sb.Append("Generado: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n\r\n");
            if (notes != null && notes.Count > 0)
            {
                sb.Append("NOTAS DE LA BÚSQUEDA\r\n");
                foreach (string n in notes) sb.Append("  - ").Append(n).Append("\r\n");
                sb.Append("\r\n");
            }
            sb.Append("ACTUALIZACIONES (").Append(updates != null ? updates.Count : 0).Append(")\r\n");
            if (updates != null)
                foreach (var u in updates)
                {
                    if (u == null) continue;
                    sb.Append("\r\n").Append(u.Name).Append("\r\n");
                    Line(sb, "Tipo", UpdateCandidate.KindText(u.Kind));
                    Line(sb, "Estado", UpdateCandidate.StatusText(u.Status) + (u.Installable ? "" : " (no instalable desde acá)") + (u.Recommended ? " · recomendada" : ""));
                    Line(sb, "Instalada", u.InstalledVersion);
                    Line(sb, "Disponible", u.AvailableVersion);
                    Line(sb, "Origen", u.Source);
                    Line(sb, "Identificador", u.Identifier);
                    Line(sb, "Editor", u.Publisher);
                    Line(sb, "Resultado", u.Result);
                    Line(sb, "Detalle", u.Detail);
                }
            return Redactor.Text(sb.ToString());
        }

        static void Line(StringBuilder sb, string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            sb.Append("  ").Append(label).Append(": ").Append(value.Replace("\r", " ").Replace("\n", " ")).Append("\r\n");
        }

        /// <summary>
        /// Escribe el .zip en <paramref name="zipPath"/> (primero a un temporal y después lo reemplaza).
        /// Devuelve cuántos registros se agregaron.
        /// </summary>
        public static int Create(string zipPath, IList<UpdateCandidate> updates, IList<string> notes, string systemLine, string logDirectory)
        {
            return Create(zipPath, updates, notes, systemLine, logDirectory, null);
        }

        /// <summary>Con archivos de texto extra (el historial de actualizaciones), también sin secretos.</summary>
        public static int Create(string zipPath, IList<UpdateCandidate> updates, IList<string> notes, string systemLine, string logDirectory, IDictionary<string, string> extra)
        {
            if (string.IsNullOrEmpty(zipPath)) throw new ArgumentException("ruta vacía");
            string tmp = zipPath + ".tmp";
            int added = 0;
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    AddText(zip, "actualizaciones.txt", UpdatesText(updates, notes, systemLine));
                    if (extra != null)
                        foreach (var kv in extra)
                            if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null) AddText(zip, Fmt.SafeFileName(kv.Key), Redactor.Text(kv.Value));
                    long total = 0;
                    var files = new List<FileInfo>();
                    try
                    {
                        if (!string.IsNullOrEmpty(logDirectory) && Directory.Exists(logDirectory))
                            foreach (string f in Directory.GetFiles(logDirectory, "*", SearchOption.TopDirectoryOnly))
                            {
                                string ext = (Path.GetExtension(f) ?? "").ToLowerInvariant();
                                if (ext == ".log" || ext == ".txt") files.Add(new FileInfo(f));
                            }
                    }
                    catch (Exception ex) { Log.Warn("Diagnóstico: no se pudo listar " + logDirectory + ": " + ex.Message); }
                    // Lo más reciente primero: si se llega al tope, queda afuera lo viejo.
                    files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                    foreach (var fi in files)
                    {
                        if (total >= MaxTotalBytes) break;
                        string text = ReadTail(fi.FullName, MaxFileBytes);
                        if (text == null) continue;
                        total += Encoding.UTF8.GetByteCount(text);
                        AddText(zip, "registros/" + fi.Name, Redactor.Text(text));
                        added++;
                    }
                }
                if (File.Exists(zipPath)) File.Delete(zipPath);
                File.Move(tmp, zipPath);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            return added;
        }

        static void AddText(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name);                 // compresión predeterminada (Optimal)
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(true)))
                w.Write(text ?? "");
        }

        /// <summary>El archivo (o sus últimos <paramref name="maxBytes"/>) como texto, aunque otro proceso lo tenga abierto. null si no se puede leer.</summary>
        public static string ReadTail(string path, int maxBytes)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - maxBytes);
                    fs.Position = start;
                    var buf = new byte[fs.Length - start];
                    int read = 0;
                    while (read < buf.Length) { int r = fs.Read(buf, read, buf.Length - read); if (r <= 0) break; read += r; }
                    string text = DecodeLog(buf, read);
                    return start > 0 ? "[… se omitió el principio del archivo …]\r\n" + text : text;
                }
            }
            catch (Exception ex) { Log.Warn("Diagnóstico: no se pudo leer " + path + ": " + ex.Message); return null; }
        }

        /// <summary>Los registros de Windows Installer son UTF-16; los de Actualizador y winget, UTF-8.</summary>
        static string DecodeLog(byte[] buf, int len)
        {
            if (len >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) return Encoding.Unicode.GetString(buf, 2, len - 2);
            int zeros = 0;
            for (int i = 1; i < Math.Min(len, 400); i += 2) if (buf[i] == 0) zeros++;
            if (len > 40 && zeros > Math.Min(len, 400) / 4) return Encoding.Unicode.GetString(buf, 0, len - (len % 2));
            return Encoding.UTF8.GetString(buf, 0, len).TrimStart('﻿');
        }
    }
}
