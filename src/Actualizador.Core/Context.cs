using System;
using System.Collections.Generic;
using System.IO;

namespace Actualizador.Core
{
    public static class AppInfo
    {
        public const string Name = "Actualizador";
        public const string Version = "2.0.40";
        const string BaseTitle = "Actualizador — programas, controladores y Windows";

        /// <summary>
        /// 2.0.11: el nombre que tenía hasta la 2.0.10. Se sigue reconociendo donde hace falta para pasar de una a otra: la
        /// política en el registro, las entregas viejas en la carpeta de versiones, las tareas programadas y las carpetas
        /// temporales que quedaron. El instalador mueve los datos y quita la instalación vieja.
        /// </summary>
        public const string LegacyName = "SuperProgram";

        /// <summary>Tiempo máximo de cada expresión regular (2.0.18).</summary>
        public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

        /// <summary>
        /// 2.0.18 (auditoría): se llama primero en cada ejecutable. Todas las expresiones regulares del proceso tienen un tiempo máximo:
        /// se aplican a páginas y archivos que vienen de internet (hasta 4 MB), y un texto armado a propósito podía dejar una búsqueda
        /// trabada indefinidamente (ReDoS). Con el tope, esa página falla con un error y se sigue con las demás.
        /// </summary>
        public static void HardenProcess()
        {
            try { AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", RegexTimeout); } catch { }
        }

        /// <summary>El título de las ventanas; la versión portable lo dice («… (portable)»).</summary>
        public static string Title { get { return IsPortable ? BaseTitle + " (portable)" : BaseTitle; } }

        /// <summary>
        /// 2.0.10: archivo que convierte una copia en portable. Lo pone build\EmpaquetarPortable.ps1 junto a Actualizador.exe; la
        /// entrega que se instala no lo lleva (y en Program Files sólo un administrador podría agregarlo).
        /// </summary>
        public const string PortableMarker = "Actualizador.portable";

        /// <summary>La carpeta de los ejecutables (en una publicación de un solo archivo, la del .exe).</summary>
        public static string ProgramDirectory
        {
            get
            {
                string d = AppDomain.CurrentDomain.BaseDirectory;
                return string.IsNullOrEmpty(d) ? "" : d.TrimEnd('\\', '/');
            }
        }

        static bool? portable;

        /// <summary>¿Es la versión portable? (está <see cref="PortableMarker"/> junto al programa; se mira una vez)</summary>
        public static bool IsPortable
        {
            get
            {
                if (!portable.HasValue)
                {
                    bool p = false;
                    try { p = ProgramDirectory.Length > 0 && File.Exists(Path.Combine(ProgramDirectory, PortableMarker)); } catch { }
                    portable = p;
                }
                return portable.Value;
            }
        }

        static string dataDir;
        static string portableNote;

        /// <summary>
        /// Opciones, catálogos y páginas oficiales. Instalado: %LOCALAPPDATA%\Actualizador. Portable: «Datos» junto al programa
        /// (viaja con el pendrive); si ahí no se puede escribir (CD, pendrive protegido, carpeta de otro), la del usuario.
        /// </summary>
        public static string DataDirectory
        {
            get
            {
                if (dataDir != null) return dataDir;
                string d = null;
                if (IsPortable)
                {
                    string p = PortableLayout.DataDirectory(ProgramDirectory);
                    string why = PortableLayout.CannotWrite(p);
                    if (why == null) d = p;
                    else portableNote = "No se puede escribir en " + p + " (" + why + "): las opciones y los registros de esta vez van a " + UserDataDirectory + ".";
                }
                dataDir = d ?? UserDataDirectory;
                return dataDir;
            }
        }

        /// <summary>%LOCALAPPDATA%\Actualizador (la de la versión instalada).</summary>
        public static string UserDataDirectory
        {
            get
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
                return Path.Combine(baseDir, Name);
            }
        }

        /// <summary>¿Los datos de la portable están junto al programa? (false si es la instalada o si ahí no se pudo escribir)</summary>
        public static bool DataNextToProgram
        {
            get { return IsPortable && string.Equals(DataDirectory, PortableLayout.DataDirectory(ProgramDirectory), StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>Si la portable no pudo guardar sus datos junto al programa, por qué (para el registro y «Acerca de»); si no, null.</summary>
        public static string PortableNote
        {
            get
            {
                if (dataDir == null) dataDir = DataDirectory;   // la nota sale de elegir la carpeta
                return portableNote;
            }
        }

        /// <summary>
        /// Lo que es de ESTE equipo: historial, ignoradas, registros, diagnósticos. Instalado es lo mismo que
        /// <see cref="DataDirectory"/>; la portable, que pasa por muchos equipos, lo separa en Datos\equipos\NOMBRE-DEL-EQUIPO.
        /// </summary>
        public static string MachineDataDirectory
        {
            get { return DataNextToProgram ? PortableLayout.MachineDirectory(DataDirectory, Environment.MachineName) : DataDirectory; }
        }

        public static string LogDirectory { get { return Path.Combine(MachineDataDirectory, "logs"); } }
    }

    /// <summary>
    /// 2.0.10: dónde guarda sus cosas la versión portable (funciones puras: se prueban sin Windows).
    /// <code>
    /// Actualizador.exe, Actualizador.portable   (2.0.39: sin Actualizador.Cli.exe)
    /// Datos\opciones.json, fuentes.json…           (del técnico: valen en todos los equipos)
    /// Datos\equipos\PC-VENTAS\historial.jsonl, ignorados.json, logs\, diagnosticos\   (de cada equipo)
    /// </code>
    /// </summary>
    public static class PortableLayout
    {
        public const string DataFolder = "Datos";
        public const string MachinesFolder = "equipos";

        public static string DataDirectory(string programDirectory)
        {
            return Path.Combine(programDirectory ?? "", DataFolder);
        }

        /// <summary>
        /// 2.0.39 (pedido del usuario: «la idea es que pese lo menos posible es el portable»): lo que traían las portables anteriores y la
        /// 2.0.39 ya no lleva: la consola (35 MB) y las bibliotecas nativas de WPF (8 MB).
        /// </summary>
        public static readonly string[] ObsoleteFiles =
        {
            "Actualizador.Cli.exe", "wpfgfx_cor3.dll", "PresentationNative_cor3.dll", "PenImc_cor3.dll", "D3DCompiler_47_cor3.dll", "vcruntime140_cor3.dll"
        };

        /// <summary>
        /// Borra de la carpeta de la portable esos archivos si esta versión no los trae: no figuran en su SHA256SUMS.txt (que tiene que listar
        /// Actualizador.exe; si no está o no es el de la portable, no se borra nada). Nunca toca «Datos» ni otro nombre. Devuelve lo borrado.
        /// </summary>
        public static System.Collections.Generic.List<string> RemoveObsolete(string programDirectory)
        {
            var removed = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(programDirectory)) return removed;
            string sums = Path.Combine(programDirectory, "SHA256SUMS.txt");
            if (!File.Exists(sums)) return removed;
            var listed = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(sums))
            {
                string t = line.Trim();
                int i = t.IndexOf("  ", StringComparison.Ordinal);
                if (i > 0) listed.Add(t.Substring(i + 2).Trim().TrimStart('*'));
            }
            if (!listed.Contains("Actualizador.exe")) return removed;
            foreach (string name in ObsoleteFiles)
            {
                if (listed.Contains(name)) continue;
                string p = Path.Combine(programDirectory, name);
                try
                {
                    if (!File.Exists(p) || (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) continue;
                    File.Delete(p);
                    removed.Add(name);
                }
                catch { }
            }
            return removed;
        }

        /// <summary>Datos\equipos\NOMBRE: el nombre del equipo sin caracteres que no van en un nombre de carpeta.</summary>
        public static string MachineDirectory(string dataDirectory, string machineName)
        {
            return Path.Combine(Path.Combine(dataDirectory ?? "", MachinesFolder), SafeMachineName(machineName));
        }

        public static string SafeMachineName(string machineName)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (machineName ?? "").Trim())
            {
                bool bad = c < ' ' || "\\/:*?\"<>|".IndexOf(c) >= 0;
                sb.Append(bad ? '_' : c);
            }
            string s = sb.ToString().Trim().TrimEnd('.', ' ');
            if (s.Length == 0 || s.Trim('.', '_').Length == 0) return "equipo";
            return s.Length > 64 ? s.Substring(0, 64) : s;
        }

        /// <summary>
        /// ¿Por qué no se puede escribir en esa carpeta? null si se puede (la crea y escribe y borra un archivo de prueba).
        /// Elevado, antes y después de crearla se controla que no haya un enlace (junction) en el camino.
        /// </summary>
        public static string CannotWrite(string dir)
        {
            try
            {
                if (!SafePaths.SafeForElevatedWrite(dir)) return "la carpeta es un enlace (junction/symlink)";
                Directory.CreateDirectory(dir);
                if (!SafePaths.SafeForElevatedWrite(dir)) return "la carpeta es un enlace (junction/symlink)";
                string probe = Path.Combine(dir, ".prueba-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return null;
            }
            catch (UnauthorizedAccessException) { return "sin permiso"; }
            catch (IOException ex) { return ex.Message.TrimEnd('.', ' '); }
            catch (Exception ex) { return ex.GetType().Name; }
        }
    }

    /// <summary>Qué fuentes se consultan y cómo (las fija la interfaz, la consola o la política de la empresa).</summary>
    public sealed class UpdateOptions
    {
        /// <summary>
        /// Consultar las fuentes oficiales de los fabricantes (catálogo propio, GitHub, páginas oficiales y el Catálogo de
        /// Microsoft Update). Se apaga en redes que sólo permiten winget, Windows Update y Microsoft Store.
        /// </summary>
        public bool QueryVendors = true;
        /// <summary>Si winget falta (Windows LTSC o sin Microsoft Store), instalarlo solo desde su proyecto oficial.</summary>
        public bool InstallWinget = true;
        /// <summary>Dirección HTTPS de un catálogo de fuentes oficiales (fuentes-oficiales.json); vacío = sólo los archivos locales.</summary>
        public string SourcesCatalogUrl = "";
        /// <summary>
        /// Ofrecer también las versiones preliminares (beta, RC) que publica el fabricante en GitHub (2.0.5). Apagado por
        /// omisión: una beta nunca se marca sola ni entra en «Actualizar todo»; queda entre las opcionales y abre su página.
        /// </summary>
        public bool IncludePreReleases;
        /// <summary>
        /// 2.0.8: carpeta donde el usuario deja las versiones de sus propios programas («RelojMonitor_1.9.0.7z»); los que no figuran
        /// en ningún catálogo se comparan con lo que hay ahí. Vacío = no se usa.
        /// </summary>
        public string OwnBuildsFolder = "";
    }

    public sealed class OperationCanceledByUserException : Exception
    {
        public OperationCanceledByUserException() : base("Operación cancelada por el usuario") { }
    }

    /// <summary>Estado compartido durante una búsqueda o una instalación: el equipo, lo instalado y la cancelación.</summary>
    public sealed class UpdateContext
    {
        public OsInfo Os;
        public bool IsAdmin;
        public UpdateOptions Options = new UpdateOptions();

        public readonly List<InstalledProgram> Programs = new List<InstalledProgram>();
        public readonly List<DriverInfo> Drivers = new List<DriverInfo>();
        public readonly List<AppxInfo> Appx = new List<AppxInfo>();
        public bool ProgramsLoaded, DriversLoaded, AppxLoaded;
        public string DriversError, AppxError;
        volatile bool cancel;

        public event Action<string> Progress;

        public static UpdateContext Create(UpdateOptions options)
        {
            var c = new UpdateContext();
            c.Os = OsInfo.Detect();
            c.IsAdmin = OsInfo.IsAdministrator();
            if (options != null) c.Options = options;
            return c;
        }

        public bool CancelRequested { get { return cancel; } }
        public void RequestCancel() { cancel = true; }

        public void ThrowIfCancelled()
        {
            if (cancel) throw new OperationCanceledByUserException();
        }

        public void Report(string message)
        {
            Log.Info(message);
            var h = Progress;
            if (h != null) { try { h(message); } catch { } }
        }
    }
}
