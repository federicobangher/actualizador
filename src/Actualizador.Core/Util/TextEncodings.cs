// Codificaciones de Windows (OEM de la consola y ANSI del sistema) que .NET 8 no trae registradas.
using System;
using System.Text;

namespace Actualizador.Core
{
    /// <summary>
    /// En .NET Framework, <c>Encoding.Default</c> era la página ANSI del sistema (1252 en un Windows en español) y
    /// <c>Encoding.GetEncoding(850)</c> funcionaba siempre. En .NET 8 <c>Encoding.Default</c> es UTF-8 y las páginas de
    /// Windows (437, 850, 1252…) no existen hasta registrar <c>CodePagesEncodingProvider</c>: la salida de pnputil y de
    /// expand.exe (en la página OEM), los registros ANSI de Windows Installer, los INF ANSI y las rutas ANSI de los accesos
    /// directos se leían como UTF-8 y cada «ó», «ñ» o «á» quedaba roto (una ruta «C:\Juegos\Música\…» no se encontraba).
    /// </summary>
    public static class TextEncodings
    {
        static readonly object gate = new object();
        static bool registered;

        /// <summary>Registra las páginas de códigos de Windows (una vez; sin efecto en .NET Framework, que ya las tiene).</summary>
        public static void EnsureRegistered()
        {
            lock (gate)
            {
                if (registered) return;
                registered = true;
#if NET
                try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
                catch (Exception ex) { Log.Quiet("Páginas de códigos de Windows", ex); }
#endif
            }
        }

        /// <summary>La página OEM de la consola (850 en español): la de la salida de pnputil, expand.exe, netsh…</summary>
        public static Encoding Oem
        {
            get
            {
                int cp = 0;
                try { cp = Native.GetOEMCP(); } catch { cp = 0; }
                return ForCodePage(cp, 850);
            }
        }

        /// <summary>La página ANSI del sistema (1252 en español): la de los archivos de texto «ANSI» de Windows.</summary>
        public static Encoding Ansi
        {
            get
            {
                int cp = 0;
                try { cp = Native.GetACP(); } catch { cp = 0; }
                return ForCodePage(cp, 1252);
            }
        }

        /// <summary>
        /// La codificación de esa página (o de la de respaldo si no se pudo saber cuál es). Nunca falla: si ninguna está
        /// disponible, Latin-1, que al menos conserva cada byte como un carácter.
        /// </summary>
        public static Encoding ForCodePage(int codePage, int fallback)
        {
            EnsureRegistered();
            foreach (int cp in new[] { codePage, fallback })
            {
                if (cp <= 0) continue;
                try { return Encoding.GetEncoding(cp); }
                catch (Exception qx) { Log.Quiet("Página de códigos " + cp, qx); }
            }
            return Encoding.GetEncoding(28591);
        }
    }
}
