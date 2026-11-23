// Pruebas de la 2.0.7: el desinstalador (que no funcionaba desde Configuración) y el ciclo instalar/desinstalar.
using System;
using System.IO;
using System.Text.RegularExpressions;
using Actualizador.Core;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        static bool Falla207(Action a)
        {
            try { a(); return false; } catch (ArgumentException) { return true; }
        }

        static void TestUnaInstalacionALaVez207()
        {
            var antes = Actualizador.Updates.InstallLock.Wait;
            Actualizador.Updates.InstallLock.Wait = TimeSpan.FromMilliseconds(200);
            var tomado = new System.Threading.ManualResetEvent(false);
            var soltar = new System.Threading.ManualResetEvent(false);
            var otra = new System.Threading.Thread(delegate()
            {
                using (var l = Actualizador.Updates.InstallLock.TryEnter(null)) { tomado.Set(); soltar.WaitOne(10000); }
            });
            try
            {
                otra.IsBackground = true;
                otra.Start();
                Check(tomado.WaitOne(5000), "otra «copia» está instalando");
                var c = new UpdateCandidate { Name = "7-Zip", Kind = UpdateKind.Programa, Status = UpdateStatus.Disponible, Installable = true, Recommended = true,
                                              ProviderName = Actualizador.Updates.WingetProvider.ProviderId, AvailableVersion = "25.01" };
                var m = new Actualizador.Updates.UpdateManager();
                m.Install(ContextoSimulado(true), null, new System.Collections.Generic.List<UpdateCandidate> { c }, false, delegate(UpdateCandidate u) { });
                Eq(UpdateStatus.Fallida, c.Status, "con otra instalación en curso no se instala");
                Check(c.Result.Contains("Otra copia de Actualizador"), "y lo explica: " + c.Result);
                using (var l = Actualizador.Updates.InstallLock.TryEnter(null))
                {
                    Check(!l.Acquired, "el cerrojo sigue tomado por la otra");
                }
                soltar.Set();
                otra.Join(5000);
                using (var l = Actualizador.Updates.InstallLock.TryEnter(null))
                {
                    Check(l.Acquired, "suelto: se puede instalar");
                    using (var l2 = Actualizador.Updates.InstallLock.TryEnter(null)) Check(l2.Acquired, "reentrante en el mismo hilo (varias rondas)");
                }
            }
            finally { soltar.Set(); Actualizador.Updates.InstallLock.Wait = antes; }
        }

        static void TestEscrituraAtomica207()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sp-atomico-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string f = Path.Combine(dir, "opciones.json");
                AtomicFile.WriteAllText(f, "{\"a\":1}", null);
                Eq("{\"a\":1}", File.ReadAllText(f), "archivo nuevo");
                AtomicFile.WriteAllText(f, "{\"a\":2}", new System.Text.UTF8Encoding(false));
                Eq("{\"a\":2}", File.ReadAllText(f), "reemplazo");
                Eq(1, Directory.GetFiles(dir).Length, "no quedan temporales");
                Check(Falla207(delegate { AtomicFile.WriteAllText("", "x", null); }), "sin ruta: error");
                string src = Leer(Raiz(), "src/Actualizador.Engine/Updates/IgnoreList.cs") + Leer(Raiz(), "src/Actualizador.Engine/Updates/UpdateHistory.cs") +
                             Leer(Raiz(), "src/Actualizador.Engine/Updates/SiteVersions.cs") + Leer(Raiz(), "src/Actualizador.App/Program.cs");
                Check(!src.Contains("File.Delete(path);\n") && src.Contains("AtomicFile.WriteAllText"), "las listas y opciones se guardan enteras o nada");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        static void TestDesinstalador207()
        {
            string raiz = Raiz();
            if (raiz == null) { Check(false, "no se encontró la raíz del repositorio"); return; }
            string u = Leer(raiz, "deploy/Uninstall-Actualizador.ps1");
            string i = Leer(raiz, "deploy/Install-Actualizador.ps1");
            string cmd = Leer(raiz, "Instalar.cmd");

            // Configuración › Aplicaciones abre el desinstalador SIN elevar: antes terminaba con «Hace falta ejecutarlo como
            // administrador» y la ventana se cerraba sola. Ahora pide el permiso y se vuelve a abrir.
            Check(u.Contains("-Verb RunAs") && u.Contains("$PSCommandPath"), "se eleva solo (UAC) volviendo a abrir el mismo script");
            Check(!Regex.IsMatch(u, @"Hace falta ejecutarlo como administrador\.'\s*-ForegroundColor Red\s*\r?\n\s*exit 5"), "ya no termina sin hacer nada si no es administrador");
            foreach (string sw in new[] { "BorrarDatos", "Silencioso", "Confirmado" })
                Check(u.Contains("if ($" + sw + ") { $argumentos += '-" + sw + "' }"), "al elevarse conserva -" + sw);

            // Antes la pregunta era [s/N]: Enter cancelaba y parecía que no hacía nada.
            Check(u.Contains("[S/n]") && !u.Contains("[s/N]"), "Enter confirma (antes cancelaba)");
            Check(u.Contains("Wait-Cierre") && u.Contains("Presioná Enter para cerrar"), "espera Enter al final: abierto desde Configuración se cerraba sin dejar leer");
            Check(Regex.IsMatch(u, @"function Wait-Cierre \{\s*\r?\n\s*if \(\$Silencioso\) \{ return \}"), "pero no en modo silencioso (las herramientas de distribución no se cuelgan)");

            // Orden: la entrada de «Aplicaciones instaladas» se quita AL FINAL (antes, primero: si fallaba el borrado de la
            // carpeta quedaba medio desinstalado y sin forma de volver a desinstalarlo).
            int carpeta = u.IndexOf("# 3. La carpeta del programa", StringComparison.Ordinal);
            int accesos = u.IndexOf("# 4. Accesos directos", StringComparison.Ordinal);
            int entrada = u.IndexOf("# 5. Al final, la entrada", StringComparison.Ordinal);
            int quitaClave = u.IndexOf("foreach ($k in @($ClaveDesinstalar) + $OtrasClaves)", StringComparison.Ordinal);
            Check(carpeta > 0 && accesos > carpeta && entrada > accesos && quitaClave > entrada, "orden: procesos, tareas, carpeta, accesos y la entrada al final");
            Check(u.Contains("$OtrasClaves") && u.Contains("WOW6432Node") && u.Contains("HKCU:"), "quita también las entradas de 32 bits y del usuario");
            Check(u.Contains("Uninstall-Actualizador.ps1'") && u.Contains("Set-ItemProperty -LiteralPath $ClaveDesinstalar -Name 'UninstallString'"),
                  "antes de borrar la carpeta deja una copia del desinstalador en ProgramData y la entrada apunta a ella");

            // Procesos de otras sesiones (Path ilegible) también se cierran, y se espera a que suelten los archivos.
            Check(u.Contains("Where-Object { $Procesos -contains $_.Name }") && u.Contains("Wait-Process"), "cierra todos los procesos de Actualizador y espera");
            Check(u.Contains("function Remove-Carpeta") && u.Contains("Start-Sleep -Seconds 2"), "borra con reintentos");
            Check(u.Contains("RunOnce") && u.Contains("rd /s /q"), "lo bloqueado se termina de borrar al reiniciar");
            Check(u.Contains("Actualizador.anterior-*"), "quita las carpetas apartadas por instalaciones que no terminaron");
            Check(u.Contains("Get-Perfiles") && u.Contains("AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs"), "accesos directos de todos los usuarios (antes sólo los comunes)");
            Check(u.Contains("(Split-Path -Leaf $destino) -eq 'Actualizador'") && u.Contains("Test-Enlace $destino"), "sólo borra una carpeta «Actualizador» que no sea un enlace");
            Check(u.Contains("Set-Location -LiteralPath $env:SystemRoot"), "la consola no queda parada en la carpeta que se borra");
            Check(u.Contains("desinstalacion.log") && u.Contains("Start-Transcript"), "deja registro");

            // Instalador: la entrada apunta al PowerShell de Windows por ruta completa y el script queda en la carpeta.
            Check(i.Contains("$powershellExe = Join-Path $env:SystemRoot 'System32\\WindowsPowerShell\\v1.0\\powershell.exe'") &&
                  i.Contains("$desinstalar = '\"' + $powershellExe + '\" -NoProfile"), "UninstallString con la ruta completa de PowerShell, entre comillas");
            Check(i.Contains("Falta Uninstall-Actualizador.ps1 en la entrega"), "el instalador comprueba que el desinstalador quede junto al programa");
            Check(i.Contains("Actualizador - terminar de desinstalar"), "el instalador cancela un borrado pendiente (si no, al reiniciar borraría la instalación nueva)");
            Check(u.Contains("'Actualizador - terminar de desinstalar'"), "y usa el mismo nombre que el desinstalador");

            // Instalar.cmd: la ruta y los argumentos van por variables de entorno (una comilla simple ya no rompe la elevación).
            Check(cmd.Contains("set \"SP_SELF=%~f0\"") && cmd.Contains("$env:SP_SELF") && !cmd.Contains("'%~f0'"), "Instalar.cmd se eleva sin meter la ruta entre comillas simples");
            Check(cmd.Contains("%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"), "Instalar.cmd usa PowerShell por ruta completa");
            Check(cmd.Contains("%PS% \"%QUITAR%\" -Silencioso"), "Instalar.cmd /quitar no pregunta dos veces (ya pausa al final)");

            // La ventana: «Más › Desinstalar Actualizador…».
            Eq(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", SelfUninstall.PowerShellPath(@"C:\Windows\"), "PowerShell por ruta completa");
            Eq(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", SelfUninstall.PowerShellPath(null), "sin SystemRoot: el habitual");
            Eq("-NoProfile -ExecutionPolicy Bypass -File \"C:\\Program Files\\Actualizador\\Uninstall-Actualizador.ps1\" -Confirmado",
               SelfUninstall.Arguments(@"C:\Program Files\Actualizador\Uninstall-Actualizador.ps1", false), "desde la ventana: ya confirmado, con pausa");
            Check(SelfUninstall.Arguments(@"C:\x\Uninstall-Actualizador.ps1", true).EndsWith(" -Silencioso"), "silencioso");
            Check(Falla207(delegate { SelfUninstall.Arguments("C:\\x\" -Command calc \"", false); }), "una comilla en la ruta no se acepta");
            Check(Falla207(delegate { SelfUninstall.Arguments("", false); }), "sin ruta tampoco");
            string tmp = Path.Combine(Path.GetTempPath(), "sp-desinst-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                Eq(null, SelfUninstall.ScriptPath(tmp), "una copia sin instalar: no hay desinstalador");
                File.WriteAllText(Path.Combine(tmp, SelfUninstall.ScriptName), "#");
                Eq(Path.Combine(tmp, SelfUninstall.ScriptName), SelfUninstall.ScriptPath(tmp), "instalada: el script junto al programa");
                Eq(null, SelfUninstall.ScriptPath(null), "sin carpeta");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
            string menu = Leer(raiz, "src/Actualizador.App/MainForm.Layout.cs");
            Check(menu.Contains("\"Desinstalar Actualizador…\"") && menu.Contains("UninstallSelf()"), "menú «Más › Desinstalar Actualizador…»");
        }
    }
}
