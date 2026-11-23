// Cómo se desinstala Actualizador desde su propia ventana (menú «Más»).
using System;
using System.IO;

namespace Actualizador.Core
{
    /// <summary>
    /// El comando para desinstalar Actualizador: el desinstalador que quedó junto al programa (Uninstall-Actualizador.ps1)
    /// con el PowerShell de Windows por su ruta completa. El script pide el permiso de administrador él mismo si hace falta,
    /// cierra Actualizador y quita todo (la entrada de «Aplicaciones instaladas», al final).
    /// </summary>
    public static class SelfUninstall
    {
        public const string ScriptName = "Uninstall-Actualizador.ps1";

        /// <summary>El PowerShell de Windows (5.1), por ruta completa: no se busca en el PATH.</summary>
        public static string PowerShellPath(string systemRoot)
        {
            string root = string.IsNullOrEmpty(systemRoot) ? @"C:\Windows" : systemRoot.TrimEnd('\\', '/');
            return root + @"\System32\WindowsPowerShell\v1.0\powershell.exe";
        }

        /// <summary>El script en la carpeta del programa, o null si no está (una copia sin instalar, o compilada a mano).</summary>
        public static string ScriptPath(string programDirectory)
        {
            if (string.IsNullOrEmpty(programDirectory)) return null;
            string p = Path.Combine(programDirectory, ScriptName);
            return File.Exists(p) ? p : null;
        }

        /// <summary>
        /// Los argumentos para PowerShell (el script entre comillas). <paramref name="silent"/>: sin preguntas ni pausa (para
        /// herramientas de distribución). Si no, <c>-Confirmado</c>: ya se preguntó en la ventana, pero al final espera Enter
        /// para que se pueda leer el resultado.
        /// </summary>
        public static string Arguments(string scriptPath, bool silent)
        {
            if (string.IsNullOrEmpty(scriptPath)) throw new ArgumentException("Falta la ruta del desinstalador.", "scriptPath");
            if (scriptPath.IndexOf('"') >= 0) throw new ArgumentException("Ruta del desinstalador inválida.", "scriptPath");
            return "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"" + (silent ? " -Silencioso" : " -Confirmado");
        }
    }
}
