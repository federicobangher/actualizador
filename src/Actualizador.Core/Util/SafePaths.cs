using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Actualizador.Core
{
    /// <summary>
    /// Rutas del sistema que NO dependen de variables de entorno.
    ///
    /// Un proceso elevado hereda el entorno del usuario, y un usuario común puede definir
    /// variables propias en HKCU\Environment (SystemRoot, windir, ProgramW6432...). Armar la
    /// ruta de un ejecutable con esas variables permite que un malware sin privilegios haga
    /// que el programa elevado ejecute un binario suyo: es la técnica de varios bypass de UAC.
    /// Estas rutas salen de la API (GetSystemDirectory) o de HKLM, que el usuario no puede escribir.
    /// </summary>
    public static class SafePaths
    {
        /// <summary>C:\Windows\System32, según GetSystemDirectory.</summary>
        public static string System32
        {
            get
            {
                string s = Environment.SystemDirectory;
                return string.IsNullOrEmpty(s) ? @"C:\Windows\System32" : s.TrimEnd('\\');
            }
        }

        /// <summary>C:\Windows, derivado de System32 (no de %SystemRoot% ni de %windir%).</summary>
        public static string Windows
        {
            get
            {
                string s = System32;
                int i = s.LastIndexOfAny(new[] { '\\', '/' });
                return i > 2 ? s.Substring(0, i) : @"C:\Windows";
            }
        }

        /// <summary>Unidad del sistema ("C:"), derivada de la carpeta de Windows.</summary>
        public static string SystemDrive
        {
            get
            {
                string w = Windows;
                return w.Length >= 2 && w[1] == ':' ? w.Substring(0, 2) : "C:";
            }
        }

        /// <summary>
        /// Program Files de 64 bits, leído de HKLM (ProgramFilesDir), que sólo puede modificar
        /// un administrador. No se usa %ProgramW6432% ni %ProgramFiles%.
        /// </summary>
        public static string ProgramFiles
        {
            get
            {
                try
                {
                    using (var k = Reg.Open(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion", Reg.Native))
                    {
                        string v = Reg.Str(k, "ProgramFilesDir");
                        if (!string.IsNullOrEmpty(v) && v.IndexOf('%') < 0 && Directory.Exists(v)) return v.TrimEnd('\\');
                    }
                }
                catch (Exception qx) { Log.Quiet("ProgramFilesDir del registro", qx); }
                return Path.Combine(SystemDrive + @"\", "Program Files");
            }
        }

        /// <summary>
        /// C:\ProgramData, de HKLM (ProfileList\ProgramData, que sólo escribe un administrador), con la unidad
        /// del sistema en vez de %SystemDrive%. null si no se puede saber.
        /// </summary>
        public static string ProgramData
        {
            get
            {
                try
                {
                    using (var k = Reg.Open(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", Reg.Native))
                    {
                        object v = k == null ? null : k.GetValue("ProgramData", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        string s = v as string;
                        if (!string.IsNullOrEmpty(s))
                        {
                            s = Regex.Replace(s, "%SystemDrive%", SystemDrive, RegexOptions.IgnoreCase).TrimEnd('\\');
                            if (s.IndexOf('%') < 0 && NormalizePath(s) != null) return s;
                        }
                    }
                }
                catch (Exception qx) { Log.Quiet("ProgramData del registro", qx); }
                return null;
            }
        }

        /// <summary>
        /// Crea (si hace falta) una carpeta de datos del programa y dice si se puede escribir ahí. Elevado, se
        /// controla que no haya un enlace (junction) ANTES de crearla y otra vez después: crear carpetas a
        /// través de un enlace ya es escribir en otro lado. Antes cuatro lugares (opciones, estado de la
        /// Store, salida cruda de winget y registros de MSI) creaban la carpeta primero y controlaban después.
        /// </summary>
        public static bool EnsureDataDirectory(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !SafeForElevatedWrite(dir)) return false;
                Directory.CreateDirectory(dir);
                return SafeForElevatedWrite(dir);
            }
            catch { return false; }
        }

        static bool? elevated;

        /// <summary>¿El proceso corre elevado? (se calcula una vez)</summary>
        public static bool Elevated
        {
            get
            {
                if (!elevated.HasValue) elevated = OsInfo.IsAdministrator();
                return elevated.Value;
            }
        }

        /// <summary>
        /// ¿Algún tramo de <paramref name="path"/>, desde <paramref name="root"/> (excluida) hacia
        /// abajo, es un punto de reanálisis (junction, enlace simbólico, punto de montaje)?
        /// Un usuario sin privilegios puede convertir una carpeta suya en un junction hacia
        /// C:\Windows\System32; si el programa elevado escribe o borra ahí, lo hace en el sistema.
        /// </summary>
        public static bool HasReparsePoint(string root, string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string r = (root ?? "").TrimEnd('\\');
            string p;
            try { p = Path.GetFullPath(path).TrimEnd('\\'); } catch { return true; }
            while (!string.IsNullOrEmpty(p) && p.Length > r.Length && p.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Directory.Exists(p) || File.Exists(p))
                        if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return true;
                }
                catch { return true; }
                string parent = Path.GetDirectoryName(p);
                if (parent == null || parent == p) break;
                p = parent.TrimEnd('\\');
            }
            return false;
        }

        /// <summary>
        /// Carpeta nueva y privada para descargar y ejecutar instaladores.
        /// Elevado: en C:\Windows\Temp, con permisos SÓLO para SYSTEM y Administradores (sin
        /// herencia). Así un proceso sin privilegios no puede dejar DLL al lado del instalador
        /// (el instalador, que corre como administrador, las cargaría) ni cambiar el archivo.
        /// Sin elevación no hay límite de privilegios que proteger: se usa la carpeta del usuario.
        /// </summary>
        public static string CreatePrivateDirectory(string userFallbackBase)
        {
            string name = "Actualizador-" + Guid.NewGuid().ToString("N");
            if (!Elevated)
            {
                string d = Path.Combine(userFallbackBase, name);
                Directory.CreateDirectory(d);
                return d;
            }
            string dir = Path.Combine(Path.Combine(Windows, "Temp"), name);
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            if (Directory.Exists(dir)) throw new IOException("La carpeta privada ya existía: " + dir);
#if NET5_0_OR_GREATER
            new DirectoryInfo(dir).Create(sec);
#else
            Directory.CreateDirectory(dir, sec);
#endif
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("La carpeta privada resultó ser un enlace: " + dir);
            return dir;
        }

        /// <summary>
        /// ¿Se puede escribir o borrar en esta carpeta de datos del programa? Siempre, salvo que el
        /// proceso esté elevado y algún tramo desde %LOCALAPPDATA% (o, en la portable, desde la carpeta
        /// del programa) sea un enlace.
        /// </summary>
        public static bool SafeForElevatedWrite(string dir)
        {
            if (!Elevated) return true;
            string root = ElevatedWriteRoot(dir, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                            AppInfo.IsPortable ? AppInfo.ProgramDirectory : null);
            return !HasReparsePoint(root, dir);
        }

        /// <summary>
        /// 2.0.10: desde dónde se controlan los enlaces al escribir elevado en <paramref name="dir"/>: la carpeta del programa
        /// portable si está adentro de ella («E:\Actualizador\Datos\…»), si no %LOCALAPPDATA%. Si no está adentro de ninguna
        /// de las dos, la raíz de su unidad: se controla el camino entero (antes no se controlaba nada, porque
        /// <see cref="HasReparsePoint"/> sólo mira lo que está debajo de la raíz que recibe).
        /// </summary>
        public static string ElevatedWriteRoot(string dir, string localAppData, string portableProgramDirectory)
        {
            if (string.IsNullOrEmpty(dir)) return "";
            if (IsInside(dir, portableProgramDirectory)) return portableProgramDirectory.TrimEnd('\\', '/');
            if (IsInside(dir, localAppData)) return localAppData.TrimEnd('\\', '/');
            string root = null;
            try { root = Path.GetPathRoot(Path.GetFullPath(dir)); } catch { }
            return (root ?? "").TrimEnd('\\', '/');
        }

        /// <summary>¿<paramref name="path"/> es <paramref name="root"/> o está dentro de ella? (sin tocar el disco)</summary>
        public static bool IsInside(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string p = NormalizePath(path), r = NormalizePath(root);
            if (p == null || r == null)
            {
                // Rutas UNC (\\servidor\…) o de otro sistema (las pruebas en Linux): comparación textual.
                p = path.Replace('/', '\\').TrimEnd('\\');
                r = root.Replace('/', '\\').TrimEnd('\\');
                if (p.IndexOf(@"\..\", StringComparison.Ordinal) >= 0 || p.EndsWith(@"\..", StringComparison.Ordinal)) return false;
            }
            return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(r.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase);
        }

        // Carpetas dentro de Windows donde un usuario común puede crear archivos.
        static readonly string[] UserWritableUnderWindows =
        {
            @"Temp\", @"Tasks\", @"tracing\", @"debug\", @"Registration\CRMLog\",
            @"System32\Tasks\", @"System32\spool\", @"System32\FxsTmp\", @"System32\com\dmp\", @"System32\Microsoft\Crypto\RSA\MachineKeys\",
            @"SysWOW64\Tasks\", @"SysWOW64\FxsTmp\", @"SysWOW64\com\dmp\"
        };

        /// <summary>
        /// ¿La ruta es un componente de Windows (dentro de su carpeta, normalizada y fuera de las subcarpetas
        /// que un usuario común puede escribir)? «C:\Windows\..\Users\x.exe» o «C:\Windows\Temp\x.exe» no lo son.
        /// </summary>
        public static bool IsUnderWindows(string path)
        {
            string p = NormalizePath(path);
            if (p == null) return false;
            string win = (NormalizePath(Windows) ?? Windows).TrimEnd('\\') + @"\";
            if (!(p + @"\").StartsWith(win, StringComparison.OrdinalIgnoreCase)) return false;
            string inside = (p + @"\").Substring(win.Length);
            foreach (string w in UserWritableUnderWindows)
                if (inside.StartsWith(w, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>
        /// Ruta absoluta de Windows normalizada con las reglas de Win32: «/» → «\», separadores repetidos,
        /// «.» y «..» resueltos, un punto final suelto quitado de cada tramo y, sólo en el ÚLTIMO tramo, los
        /// puntos y espacios finales. En un tramo intermedio, «Windows » (con espacio) y «...» son nombres de
        /// carpeta reales y se conservan: «C:\Windows \System32» es otra carpeta, la que un usuario común puede
        /// crear para imitar a la de Windows. Sin «\\?\» ni barra final. null si no es una ruta absoluta con
        /// unidad («C:\…») o si «..» sale de la raíz. No toca el disco (sirve en las pruebas).
        /// </summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.Trim().Trim('"').Replace('/', '\\');
            if (p.StartsWith(@"\\?\") || p.StartsWith(@"\??\")) p = p.Substring(4);
            if (p.Length < 3 || !char.IsLetter(p[0]) || p[1] != ':' || p[2] != '\\') return null;
            bool endsWithSeparator = p.EndsWith(@"\");
            var parts = new List<string>();
            foreach (string raw in p.Substring(3).Split('\\'))
            {
                if (raw.Length == 0 || raw == ".") continue;
                if (raw == "..")
                {
                    if (parts.Count == 0) return null;
                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                string s = raw;
                if (s.EndsWith(".") && !s.EndsWith("..")) s = s.Substring(0, s.Length - 1);   // «Windows.» → «Windows»
                parts.Add(s);
            }
            if (!endsWithSeparator && parts.Count > 0)
            {
                string last = parts[parts.Count - 1].TrimEnd('.', ' ');
                if (last.Length == 0) parts.RemoveAt(parts.Count - 1);
                else parts[parts.Count - 1] = last;
            }
            return char.ToUpperInvariant(p[0]) + @":\" + string.Join(@"\", parts.ToArray());
        }

        /// <summary>
        /// ¿La carpeta puede ser escrita por un usuario sin privilegios? Heurística por ubicación:
        /// el perfil del usuario, la carpeta temporal y ProgramData lo son; Program Files y
        /// Windows no.
        /// </summary>
        public static bool IsUserWritableLocation(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = NormalizePath(path);
            if (p == null) { try { p = Path.GetFullPath(path); } catch { p = path; } }
            p = p.TrimEnd('\\') + @"\";
            string pf = ProgramFiles.TrimEnd('\\') + @"\";
            string pf86 = pf.Replace(@"\Program Files\", @"\Program Files (x86)\");
            string win = Windows.TrimEnd('\\') + @"\";
            if (p.StartsWith(pf, StringComparison.OrdinalIgnoreCase) || p.StartsWith(pf86, StringComparison.OrdinalIgnoreCase))
                return false;
            // Dentro de Windows, sólo las subcarpetas que puede escribir cualquiera (Temp, Tasks…).
            if (p.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return !IsUnderWindows(p);
            return true;
        }
    }
}
