// Ubicación de winget y utilidades del inventario que usa el motor de actualizaciones.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using Actualizador.Core;

namespace Actualizador.Inventory
{
    /// <summary>winget.exe del equipo y nombres de editor de las apps de la Store.</summary>
    public static class SoftwareCollectors
    {
        public static string PublisherName(string dn)
        {
            if (string.IsNullOrEmpty(dn)) return "";
            foreach (string key in new[] { "O=", "CN=" })
                foreach (string part in dn.Split(','))
                {
                    string p = part.Trim();
                    if (p.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return p.Substring(key.Length).Trim('"');
                }
            return dn;
        }

        /// <summary>
        /// Ubica winget.exe sin darle al usuario común la posibilidad de colar un ejecutable.
        ///
        /// %LOCALAPPDATA%\Microsoft\WindowsApps es escribible por el usuario sin privilegios. Si el
        /// programa corre como administrador y ahí hubiera un winget.exe plantado, se ejecutaría
        /// elevado. Un alias de ejecución genuino es un punto de reanálisis (reparse point), no un
        /// .exe común: siendo administrador, sólo se acepta si lo es.
        /// </summary>
        public static string FindWinget()
        {
            var all = WingetCandidates();
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>
        /// winget.exe posibles, en orden de preferencia. Elevado: primero el paquete protegido y
        /// después el alias del usuario, sólo si es un alias de ejecución genuino.
        /// </summary>
        public static List<string> WingetCandidates()
        {
            var list = new List<string>();
            bool elevated = OsInfo.IsAdministrator();
            string pkg = FindWingetPackage();
            bool verified;
            string alias = FindWingetAlias(elevated, out verified);
            // El alias es la forma en que Windows ejecuta winget (también desde una consola de
            // administrador): arranca con la identidad del paquete, sus dependencias (VCLibs) y sus
            // orígenes ya registrados. El winget.exe de WindowsApps ejecutado directo arranca SIN esa
            // identidad: en unos equipos funciona y en otros falla al abrir los orígenes, no encuentra
            // vcruntime140 o ve otra lista de programas. Elevado se usaba primero ese, y por eso las
            // mismas actualizaciones andaban en un Windows y en otro no. Ahora, elevado, el alias va
            // primero sólo si su destino es el winget.exe del paquete oficial (verificado).
            if (elevated && !verified) { if (pkg != null) list.Add(pkg); if (alias != null) list.Add(alias); }
            else { if (alias != null) list.Add(alias); if (pkg != null) list.Add(pkg); }
            return list;
        }

        static string FindWingetAlias(bool elevated, out bool verified)
        {
            verified = false;
            // El alias vive en la carpeta del usuario, que un malware sin privilegios puede
            // escribir: elevado, se acepta sólo si es un alias de ejecución de Windows genuino.
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                {
                    string alias = Path.Combine(local, @"Microsoft\WindowsApps\winget.exe");
                    if (File.Exists(alias))
                    {
                        // Un alias de ejecución de apps tiene la etiqueta APPEXECLINK; un archivo común
                        // o un enlace simbólico con ese nombre, no.
                        bool genuino = Native.ReparseTag(alias) == Native.IO_REPARSE_TAG_APPEXECLINK;
                        if (genuino)
                        {
                            string[] link = null;
                            try { link = Native.AppExecLinkStrings(alias); } catch (Exception ex) { Log.Warn("Alias de winget: " + ex.Message); }
                            verified = IsGenuineWingetLink(link, SafePaths.ProgramFiles);
                            if (!verified) Log.Warn("El alias " + alias + " no apunta al winget.exe de App Installer" + (link != null && link.Length > 2 ? " (" + link[2] + ")" : "") + ".");
                        }
                        if (genuino || !elevated) return alias;
                        Log.Warn("Se ignoró " + alias + ": no es un alias de ejecución de Windows (posible ejecutable plantado).");
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Alias de winget: " + ex.Message); }
            return null;
        }

        /// <summary>
        /// ¿El alias de ejecución es el de App Installer? Familia «Microsoft.DesktopAppInstaller_8wekyb3d8bbwe»
        /// y destino «…\WindowsApps\Microsoft.DesktopAppInstaller_…\winget.exe» dentro de Program Files
        /// (carpeta que sólo escribe el sistema).
        /// </summary>
        public static bool IsGenuineWingetLink(string[] link, string programFiles)
        {
            if (link == null || link.Length < 3 || string.IsNullOrEmpty(programFiles)) return false;
            if (!string.Equals(link[0], "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)) return false;
            string target = (link[2] ?? "").Trim();
            if (target.IndexOf("..", StringComparison.Ordinal) >= 0 || target.IndexOf('/') >= 0) return false;
            string root = programFiles.TrimEnd('\\') + @"\WindowsApps\Microsoft.DesktopAppInstaller_";
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            if (!target.EndsWith(@"_8wekyb3d8bbwe\winget.exe", StringComparison.OrdinalIgnoreCase)) return false;
            // Una sola carpeta entre WindowsApps y winget.exe.
            string middle = target.Substring(root.Length, target.Length - root.Length - @"\winget.exe".Length);
            return middle.Length > 0 && middle.IndexOf('\\') < 0;
        }

        static string FindWingetPackage()
        {
            // Paquete instalado en Program Files\WindowsApps: sólo lo puede escribir el sistema.
            try
            {
                string apps = Path.Combine(SafePaths.ProgramFiles, "WindowsApps");
                string best = null, bestVer = null;
                foreach (string d in Directory.GetDirectories(apps, "Microsoft.DesktopAppInstaller_*"))
                {
                    string exe = Path.Combine(d, "winget.exe");
                    if (!File.Exists(exe)) continue;
                    string[] parts = Path.GetFileName(d).Split('_');
                    string ver = parts.Length > 1 ? parts[1] : "";
                    if (best == null || (ver.Length > 0 && VersionUtil.IsNewer(ver, bestVer))) { best = exe; bestVer = ver; }
                }
                return best;
            }
            catch { return null; }   // sin permiso para listar WindowsApps: es lo normal
        }

        static string[] SafeDirs(string path)
        {
            try { return Directory.Exists(path) ? Directory.GetDirectories(path) : new string[0]; }
            catch { return new string[0]; }
        }

        public static string FileVersion(string path)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                return string.IsNullOrEmpty(v.ProductVersion) ? v.FileVersion ?? "" : v.ProductVersion;
            }
            catch { return ""; }
        }

        // ------------------------------------------------------------------ ODBC
    }

    /// <summary>Conversiones de los resultados de Windows Update y de WMI.</summary>
    public static class SystemCollectors
    {
        public static string WuResult(int rc)
        {
            switch (rc)
            {
                case 0: return "No iniciada";
                case 1: return "En curso";
                case 2: return "Correcta";
                case 3: return "Correcta con errores";
                case 4: return "Error";
                case 5: return "Cancelada";
                default: return "Código " + rc;
            }
        }

        public static int ToInt(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); }
            catch
            {
                try { return unchecked((int)Convert.ToUInt32(o, CultureInfo.InvariantCulture)); } catch { return 0; }
            }
        }
    }
}
