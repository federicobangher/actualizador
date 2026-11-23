// Seguridad: rutas, secretos, firmas, antivirus.
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System;
using Actualizador.Core;
using Actualizador.Inventory;
using Actualizador.Updates;

namespace Actualizador.Tests
{
    public static partial class Program
    {

        static void TestAntivirusCentroDeSeguridad()
        {
            // productState reales de Avast: 0x61000 activado y al día; 0x61010 firmas viejas; 0x60000 desactivado.
            var ok = AntivirusStatus.FromProductState("Avast Antivirus", 0x61000);
            Check(ok.Enabled && ok.UpToDate, "0x61000: activado y al día");
            var viejo = AntivirusStatus.FromProductState("Avast Antivirus", 0x61010);
            Check(viejo.Enabled && !viejo.UpToDate, "0x61010: firmas desactualizadas");
            var apagado = AntivirusStatus.FromProductState("Avast Antivirus", 0x60000);
            Check(!apagado.Enabled, "0x60000: desactivado");
            var av = new List<AntivirusStatus> { ok };
            Check(ProgramHints.MatchAntivirus("Avast Free Antivirus", av) == ok, "«Avast Free Antivirus» (Programas) es «Avast Antivirus» (Centro de seguridad)");
            Check(ProgramHints.MatchAntivirus("AVG AntiVirus FREE", av) == null, "otra marca: no");
            Check(ProgramHints.MatchAntivirus("Avast Free Antivirus", null) == null, "sin Centro de seguridad: nada");

            // La fila de Avast de la captura del usuario: «No verificable» → «Al día» según Windows.
            var fila = new UpdateCandidate();
            fila.Kind = UpdateKind.Programa; fila.Name = "Avast Free Antivirus"; fila.Status = UpdateStatus.NoVerificable;
            fila.ProviderName = WingetProvider.ProviderId; fila.Identifier = "registro:Avast Free Antivirus"; fila.InstalledVersion = "26.9.11171.3829";
            var prog = Prog("Avast Free Antivirus", "26.9.11171.3829", "Gen Digital Inc.", "Equipo", @"C:\Program Files\Avast Software\Avast", "x64");
            ProgramHints.Annotate(new List<UpdateCandidate> { fila }, new List<InstalledProgram> { prog }, av);
            Eq(UpdateStatus.AlDia, fila.Status, "Avast activado y al día: «Al día»");
            Check(fila.Detail.Contains("Centro de seguridad de Windows") && fila.Source == "Centro de seguridad de Windows", "y se dice de dónde sale: " + fila.Detail);
            var fila2 = new UpdateCandidate();
            fila2.Kind = UpdateKind.Programa; fila2.Name = "Avast Free Antivirus"; fila2.Status = UpdateStatus.NoVerificable; fila2.ProviderName = WingetProvider.ProviderId;
            ProgramHints.Annotate(new List<UpdateCandidate> { fila2 }, new List<InstalledProgram> { prog }, new List<AntivirusStatus> { viejo });
            Check(fila2.Status == UpdateStatus.NoVerificable && fila2.Detail.Contains("DESACTUALIZADAS"), "firmas viejas: se avisa, no se inventa «al día»: " + fila2.Detail);
            var fila3 = new UpdateCandidate();
            fila3.Kind = UpdateKind.Programa; fila3.Name = "Avast Free Antivirus"; fila3.Status = UpdateStatus.NoVerificable; fila3.ProviderName = WingetProvider.ProviderId;
            ProgramHints.Annotate(new List<UpdateCandidate> { fila3 }, new List<InstalledProgram> { prog }, null);
            Check(fila3.Status == UpdateStatus.NoVerificable && fila3.Detail.StartsWith("Antivirus: se actualiza solo"), "sin datos del Centro de seguridad: como antes");
        }

        static UpdateContext ContextoSimulado(bool admin, params InstalledProgram[] programas)
        {
            var ctx = new UpdateContext();
            ctx.Os = new OsInfo();
            ctx.Os.NativeArchitecture = "x64"; ctx.Os.Major = 10; ctx.Os.Build = 26100; ctx.Os.IsWindows = true;
            ctx.IsAdmin = admin;
            ctx.Programs.AddRange(programas);
            ctx.ProgramsLoaded = true;
            return ctx;
        }

        static UpdateCandidate FilaWinget(string name, string id, string installed, string available)
        {
            var w = new WingetRow();
            w.Name = name; w.Id = id; w.Version = installed; w.Available = available; w.Source = "winget";
            var c = new UpdateCandidate();
            c.Kind = UpdateKind.Programa; c.Name = name; c.InstalledVersion = installed; c.AvailableVersion = available;
            c.Identifier = id; c.ProviderName = WingetProvider.ProviderId; c.ProviderTag = w; c.Status = UpdateStatus.Disponible; c.Installable = true;
            return c;
        }

        static ProcResult Salida(uint code, string output)
        {
            var r = new ProcResult();
            r.Started = true; r.ExitCode = unchecked((int)code); r.Out = output ?? "";
            return r;
        }

        // ------------------------------------------------------------------ Auditoría 1.6.0: seguridad

        static ProcessRunner.ProcessRow Fila(int pid, int parent, DateTime? created)
        {
            var r = new ProcessRunner.ProcessRow();
            r.Pid = pid; r.Parent = parent; r.Created = created;
            return r;
        }

        static void TestSeguridad()
        {
            string M = Redactor.Mask;
            // --- Secretos que DEBEN ocultarse
            Check(!Redactor.Text("app.exe --password Hunter2").Contains("Hunter2"), "--password valor");
            Check(!Redactor.Text("app.exe --password=Hunter2").Contains("Hunter2"), "--password=valor");
            Check(!Redactor.Text("tool /token abc123XYZ").Contains("abc123XYZ"), "/token valor");
            Check(!Redactor.Text("Server=db;User Id=sa;Password=S3cr3t;").Contains("S3cr3t"), "cadena de conexión");
            Check(!Redactor.Text("Server=db;Pwd=S3cr3t").Contains("S3cr3t"), "Pwd= en cadena de conexión");
            Check(!Redactor.Text("https://juan:MiClave@proxy.local:8080").Contains("MiClave"), "credenciales en URL");
            Check(!Redactor.Text("https://x.blob.core.windows.net/c?sv=2020&sig=AbCdEf123").Contains("AbCdEf123"), "firma SAS de Azure");
            Check(!Redactor.Text("run.cmd --api-key \"k-9f8e7d\"").Contains("k-9f8e7d"), "valor entre comillas");
            Check(!Redactor.Text("x --client_secret zzTOPzz").Contains("zzTOPzz"), "client_secret");
            Eq(M + " (oculto por seguridad)", Redactor.Variable("GITHUB_TOKEN", "ghp_abc"), "variable *_TOKEN");
            Eq(M + " (oculto por seguridad)", Redactor.Variable("DB_PASSWORD", "x"), "variable *_PASSWORD");
            Eq(M + " (oculto por seguridad)", Redactor.Variable("AZURE_STORAGE_CONNECTION_STRING", "a"), "cadena de conexión en variable");
            Check(!Redactor.Variable("MI_CONFIG", "Server=db;Password=zz9").Contains("zz9"), "secreto dentro del valor de una variable inocente");

            // --- Información que NO debe ocultarse (ocultar de más destruye el informe)
            Eq(@"C:\Windows\System32", Redactor.Variable("SystemRoot", @"C:\Windows\System32"), "rutas normales intactas");
            Eq(@"C:\Program Files\App\app.exe -minimized", Redactor.Text(@"C:\Program Files\App\app.exe -minimized"), "línea de comandos sin secretos intacta");
            Eq(@"C:\Program Files\Password Manager\pm.exe", Redactor.Text(@"C:\Program Files\Password Manager\pm.exe"), "una carpeta llamada Password no se oculta");
            Eq("svchost.exe -k netsvcs -p", Redactor.Text("svchost.exe -k netsvcs -p"), "svchost intacto");
            Eq("NUMBER_OF_PROCESSORS", Redactor.IsSecretName("NUMBER_OF_PROCESSORS") ? "marcada" : "NUMBER_OF_PROCESSORS", "variable normal no marcada");
            Check(Redactor.Text(null) == null && Redactor.Text("") == "", "nulos y vacíos");

            // --- Identificadores de winget: nada que permita colar argumentos
            Check(WingetProvider.IsSafeId("Microsoft.Edge"), "Id normal");
            Check(WingetProvider.IsSafeId("Microsoft.VCRedist.2015+.x64"), "Id con + (VC++)");
            Check(WingetProvider.IsSafeId("9NKSQGP7F2NH"), "Id de la Store");
            Check(WingetProvider.IsSafeId("Notepad++.Notepad++"), "Id con ++");
            Check(!WingetProvider.IsSafeId("Foo\" --override \"/S"), "comillas: rechazado");
            Check(!WingetProvider.IsSafeId("Foo --location C:\\x"), "espacios: rechazado");
            Check(!WingetProvider.IsSafeId("Foo&calc"), "ampersand: rechazado");
            Check(!WingetProvider.IsSafeId(""), "vacío: rechazado");
            Check(!WingetProvider.IsSafeId(null), "nulo: rechazado");
            Check(WingetProvider.IsSafeSource("winget") && WingetProvider.IsSafeSource("msstore"), "orígenes normales");
            Check(!WingetProvider.IsSafeSource("winget --force"), "origen con argumentos: rechazado");

            // --- Rutas del sistema sin variables de entorno
            string original = Environment.GetEnvironmentVariable("SystemRoot");
            try
            {
                Environment.SetEnvironmentVariable("SystemRoot", @"C:\Users\atacante\falso");
                Check(!SafePaths.Windows.Contains("atacante"), "SystemRoot manipulado no cambia la carpeta de Windows");
                Check(!ProcessRunner.SystemExe("x.exe").Contains("atacante"), "SystemRoot manipulado no cambia la ruta de los ejecutables");
                Check(!SafePaths.IsUnderWindows(@"C:\Users\atacante\falso\malware.exe"), "un archivo del atacante no pasa por componente de Windows");
            }
            finally { Environment.SetEnvironmentVariable("SystemRoot", original); }
            Check(!ProcessRunner.SystemExe("no-existe.exe").Equals("no-existe.exe"), "un ejecutable inexistente no se busca en el PATH");
            Check(SafePaths.IsUserWritableLocation(@"C:\Users\fede\Desktop\Actualizador"), "el Escritorio es una carpeta del usuario");

            // --- CSV: todos los caracteres de inicio peligrosos (lista de OWASP)
            foreach (string f in new[] { "=1+1", "+cmd", "-cmd|x", "@SUMA(A1)", "\t=1", "\r=1" })
                Check(Fmt.Csv(f, ";").TrimStart('"').StartsWith("'"), "CSV neutraliza «" + f.Replace("\t", "\\t").Replace("\r", "\\r") + "»");
            Eq("-5", Fmt.Csv("-5", ";"), "CSV: un número negativo no se toca");
        }
    }
}
