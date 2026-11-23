// Modelos de datos del actualizador: lo instalado (programas, controladores, apps) y cada actualización encontrada.
using System;
using System.Collections.Generic;

namespace Actualizador.Core
{
    /// <summary>Tabla genérica (columnas y filas) para mostrar listados, como el historial de actualizaciones.</summary>
    public sealed class TableData
    {
        public string Id;
        public string Title;
        public readonly List<string> Columns = new List<string>();
        public readonly List<string[]> Rows = new List<string[]>();
        public readonly List<string> Notes = new List<string>();
        public string Error;

        public TableData(string id, string title)
        {
            Id = id ?? "";
            Title = title ?? Id;
        }

        public void SetColumns(params string[] columns)
        {
            Columns.Clear();
            Columns.AddRange(columns);
        }

        public void Add(params object[] cells)
        {
            if (Columns.Count == 0) throw new InvalidOperationException("La tabla " + Id + " no definió columnas.");
            var row = new string[Columns.Count];
            for (int i = 0; i < row.Length; i++)
                row[i] = i < cells.Length ? Fmt.Cell(cells[i]) : "";
            Rows.Add(row);
        }

        public void Note(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!Notes.Contains(text)) Notes.Add(text);
        }

        /// <summary>Ordena por la columna: los números por su valor y antes que los textos (orden total).</summary>
        public void SortRows(int column)
        {
            Rows.Sort(delegate(string[] a, string[] b)
            {
                string x = a != null && column < a.Length ? a[column] : null;
                string y = b != null && column < b.Length ? b[column] : null;
                long nx, ny;
                bool xn = long.TryParse(x, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out nx);
                bool yn = long.TryParse(y, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out ny);
                if (xn && yn) return nx.CompareTo(ny);
                if (xn != yn) return xn ? -1 : 1;
                return string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
            });
        }
    }

    public enum UpdateKind { Programa, Controlador, Store, WindowsUpdate }

    /// <summary>Ignorada: el usuario pidió no ofrecerla (esa versión o siempre); se ve, pero no se instala.</summary>
    public enum UpdateStatus { Disponible, AlDia, NoVerificable, Error, Instalando, Instalada, RequiereReinicio, Fallida, Pendiente, Ignorada }

    public sealed class UpdateCandidate
    {
        public UpdateKind Kind;
        public string Name;
        public string Publisher;
        public string InstalledVersion;
        public string AvailableVersion;
        public string Source;
        public string Identifier;
        public UpdateStatus Status;
        public string Detail;
        public string Result;
        /// <summary>
        /// Página oficial del programa (sitio del fabricante, página de descargas o del proyecto),
        /// sólo http/https. La interfaz la abre con «Abrir sitio del fabricante» y el informe HTML
        /// la muestra como enlace. Vacía si no se conoce.
        /// </summary>
        public string InfoUrl;
        public bool Installable;
        /// <summary>El proveedor aconseja instalarlo (lo usan «Marcar disponibles» y la consola).</summary>
        public bool Recommended;
        /// <summary>
        /// 2.0.35 (relevamiento): sólo se marca a mano, nunca con «Marcar todo»: ya se instaló y vuelve a aparecer, o ya falló igual. «Marcar
        /// todo» marca también lo opcional, y así esas filas volvían a repetirse en cada pasada aunque no se marcaran solas.
        /// </summary>
        public bool ManualOnly;
        /// <summary>
        /// Elección explícita del usuario en la interfaz. Nunca se marca sola: los proveedores
        /// sólo fijan <see cref="Recommended"/>, así no se instala nada que el usuario no haya visto marcado.
        /// </summary>
        public bool Selected;
        /// <summary>
        /// No se instaló porque el instalador pide reiniciar el equipo ANTES (winget 0x8A15010A: quedó
        /// pendiente otra instalación). Al terminar, la interfaz sugiere reiniciar y reintentar.
        /// </summary>
        public bool RestartBeforeRetry;
        /// <summary>
        /// Regla de la lista de ignorados que la cubre, o null. Mientras está ignorada, el estado es
        /// <see cref="UpdateStatus.Ignorada"/> y lo anterior queda guardado para volver atrás sin otra búsqueda.
        /// </summary>
        public object IgnoredBy;
        public UpdateStatus StatusBeforeIgnore;
        public bool RecommendedBeforeIgnore;
        public string ResultBeforeIgnore;
        /// <summary>
        /// 2.0.12: la versión que publica la página oficial del programa, si se leyó en esta búsqueda, y esa página. Manda sobre
        /// lo que aparezca en GitHub: una versión que la página oficial todavía no publica no se ofrece. Vacías si no se leyó.
        /// </summary>
        public string OfficialVersion, OfficialPage;
        // Datos internos del proveedor (no se exportan).
        public string ProviderName;
        public object ProviderTag;

        public static string KindText(UpdateKind k)
        {
            switch (k)
            {
                case UpdateKind.Programa: return "Programas";
                case UpdateKind.Controlador: return "Controladores";
                case UpdateKind.Store: return "Microsoft Store";
                default: return "Windows Update";
            }
        }

        public static string StatusText(UpdateStatus s)
        {
            switch (s)
            {
                case UpdateStatus.Disponible: return "Actualización disponible";
                case UpdateStatus.AlDia: return "Al día";
                case UpdateStatus.NoVerificable: return "No verificable";
                case UpdateStatus.Error: return "Error al verificar";
                case UpdateStatus.Instalando: return "Instalando…";
                case UpdateStatus.Instalada: return "Actualizada";
                case UpdateStatus.RequiereReinicio: return "Actualizada (requiere reinicio)";
                case UpdateStatus.Fallida: return "Falló la instalación";
                case UpdateStatus.Ignorada: return "Ignorada";
                default: return "Pendiente";
            }
        }
    }

    public sealed class UpdateSession
    {
        public string ProgramName;
        public string ProgramVersion;
        public string ComputerName;
        public string UserName;
        public string OsDescription;
        public bool IsAdmin;
        public DateTime StartedLocal;
        public TimeSpan Duration;
        public readonly List<UpdateCandidate> Updates = new List<UpdateCandidate>();
        public readonly List<string> UpdateNotes = new List<string>();
        public DateTime? UpdatesScannedLocal;

        /// <summary>Una sesión nueva con los datos del equipo (nombre, usuario, Windows, administrador).</summary>
        public static UpdateSession Create(UpdateContext ctx)
        {
            var r = new UpdateSession();
            r.ProgramName = AppInfo.Name;
            r.ProgramVersion = AppInfo.Version;
            r.ComputerName = Environment.MachineName;
            r.UserName = Environment.UserDomainName + "\\" + Environment.UserName;
            r.OsDescription = ctx != null && ctx.Os != null ? ctx.Os.FriendlyName : Environment.OSVersion.ToString();
            r.IsAdmin = ctx != null && ctx.IsAdmin;
            r.StartedLocal = DateTime.Now;
            return r;
        }
        /// <summary>Cuánto tardó la última búsqueda de actualizaciones.</summary>
        public TimeSpan? UpdatesScanDuration;

    }

    // ---- Lo instalado en el equipo ----

    public sealed class InstalledProgram
    {
        public string Name;
        public string Version;
        public string Publisher;
        public DateTime? InstallDate;
        public string InstallLocation;
        /// <summary>QuietUninstallString si existe; si no, UninstallString (para mostrar y para ubicar la carpeta).</summary>
        public string UninstallString;
        /// <summary>UninstallString tal cual (desinstalación con ventanas).</summary>
        public string UninstallCommand;
        /// <summary>QuietUninstallString tal cual (desinstalación silenciosa que registra el propio instalador).</summary>
        public string QuietUninstallCommand;
        /// <summary>DisplayIcon: suele ser el ejecutable principal (sirve para saber si es de 32 o 64 bits).</summary>
        public string DisplayIcon;
        /// <summary>WindowsInstaller = 1: lo instaló Windows Installer (MSI).</summary>
        public bool IsWindowsInstaller;
        public string Architecture;   // x64, x86, ARM64
        public string Scope;          // Equipo / Usuario
        /// <summary>Sin entrada de desinstalación: se encontró por su acceso directo (portable, o de una cuenta sin sesión).</summary>
        public bool Unregistered;
        public string RegistryKey;
        public long SizeKb;
        // Direcciones que el propio programa registra en su clave de desinstalación
        // (URLInfoAbout, URLUpdateInfo, HelpLink). Son datos del instalador: se validan antes de usarlas.
        public string InfoUrl;
        public string UpdateUrl;
        public string HelpLink;
    }

    public sealed class DriverInfo
    {
        public string DeviceName;
        public string DeviceClass;
        public string Manufacturer;
        public string Provider;
        public string Version;
        public DateTime? Date;
        public string InfName;
        public string HardwareId;
        public string DeviceId;
        public bool IsSigned;
        public string Signer;
    }

    public sealed class AppxInfo
    {
        public string Name;
        public string DisplayName;
        public string Version;
        public string Architecture;
        public string FamilyName;
        public string FullName;
        public string SignatureKind;
        public bool IsFramework;
        public string InstallLocation;
        public string Publisher;
    }
}
