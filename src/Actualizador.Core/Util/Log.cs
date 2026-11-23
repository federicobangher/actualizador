using System;
using System.IO;
using System.Text;

namespace Actualizador.Core
{
    /// <summary>Log a archivo (%LOCALAPPDATA%\Actualizador\logs). Nunca lanza excepciones.</summary>
    public static class Log
    {
        static readonly object Gate = new object();
        static readonly UTF8Encoding Utf8SinBom = new UTF8Encoding(false);
        static string file;
        static StreamWriter writer;

        public static event Action<string> LineWritten;

        public static string FilePath { get { return file; } }

        public static void Init(string directory, string prefix)
        {
            try
            {
                // Antes y después de crearla: elevado, crear carpetas a través de un enlace ya es escribir en otro lado.
                bool safe = SafePaths.SafeForElevatedWrite(directory);
                if (safe) { Directory.CreateDirectory(directory); safe = SafePaths.SafeForElevatedWrite(directory); }
                if (!safe)
                {
                    // Elevado y con un enlace en el camino: escribir o limpiar ahí sería hacerlo
                    // en otra carpeta (quizás del sistema). Sin archivo de registro esta vez.
                    file = null;
                    Write("AVISO", "La carpeta de registros " + directory + " contiene un enlace (junction/symlink): no se escribe el registro en disco.");
                    return;
                }
                lock (Gate) { CloseWriter(); file = Path.Combine(directory, prefix + "_" + DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ".log"); }
                CleanOld(directory, 30);
            }
            catch { file = null; }
        }

        public static void Info(string message) { Write("INFO ", message); }
        public static void Warn(string message) { Write("AVISO", message); }

        public static void Error(string message, Exception ex = null)
        {
            Write("ERROR", ex == null ? message : message + " | " + ex.GetType().Name + ": " + ex.Message);
            if (ex != null) Write("TRAZA", ex.ToString());
        }

        static readonly System.Collections.Generic.Dictionary<string, int> quiet = new System.Collections.Generic.Dictionary<string, int>();

        /// <summary>
        /// Una falla que el programa resuelve solo (usa otro dato o lo deja vacío) pero que sirve para el diagnóstico
        /// (antes se tragaba sin dejar rastro). Va sólo al archivo de registro —no al panel de la ventana— y como
        /// mucho 3 veces por contexto, para no llenarlo cuando se repite en cada evento o en cada regla.
        /// </summary>
        public static void Quiet(string context, Exception ex)
        {
            int n;
            lock (quiet)
            {
                quiet.TryGetValue(context ?? "", out n);
                quiet[context ?? ""] = n + 1;
            }
            if (n >= 3) return;
            Write("DETAL", (context ?? "") + ": " + (ex == null ? "error" : ex.GetType().Name + ": " + ex.Message) + (n == 2 ? " (no se registran más de este tipo)" : ""));
        }

        static void Write(string level, string message)
        {
            // 2.0: los registros nunca guardan secretos (contraseñas, tokens o credenciales en direcciones).
            string safe;
            try { safe = Redactor.Text(message ?? ""); } catch (Exception) { safe = message ?? ""; }
            string line = DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + level + " " + safe;
            lock (Gate)
            {
                if (file != null)
                {
                    // Un solo archivo abierto para toda la sesión (antes se abría y cerraba en cada línea:
                    // miles de veces durante una instalación con winget). Se comparte para lectura, así se
                    // puede abrir mientras el programa corre. Sin BOM, que ensucia la primera línea.
                    try
                    {
                        if (writer == null) writer = new StreamWriter(OpenAppend(file), Utf8SinBom) { AutoFlush = true };
                        writer.WriteLine(line);
                    }
                    catch { CloseWriter(); }
                }
            }
            var h = LineWritten;
            if (h != null && level != "TRAZA" && level != "DETAL")
            {
                try { h(line); } catch { }
            }
        }

        /// <summary>
        /// Con permiso sólo de AGREGAR, Windows escribe cada línea al final aunque otro proceso (otra
        /// ventana, o la instancia que se reinicia como administrador) escriba el mismo archivo; con
        /// FileMode.Append común cada proceso escribe en su propia posición y se pisan las líneas.
        /// </summary>
        static FileStream OpenAppend(string path)
        {
            try
            {
#if NET5_0_OR_GREATER
                return FileSystemAclExtensions.Create(new FileInfo(path), FileMode.Append, System.Security.AccessControl.FileSystemRights.AppendData,
                                                      FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None, null);
#else
                return new FileStream(path, FileMode.Append, System.Security.AccessControl.FileSystemRights.AppendData,
                                      FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
#endif
            }
            catch (Exception ex)
            {
                if (ex is UnauthorizedAccessException || ex is IOException) throw;
                // Plataformas sin ese constructor (Mono): el modo común.
                return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }
        }

        static void CloseWriter()
        {
            try { if (writer != null) writer.Dispose(); } catch { }
            writer = null;
        }

        static void CleanOld(string dir, int days)
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.log"))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-days)) File.Delete(f);
            }
            catch { }
        }
    }
}
