// Pruebas de la 2.0.36. Pedido del usuario (23/11/2026): «que no tenga que descargar nada, como Virtual Pet».
using System;
using System.IO;
using System.Text;
using Actualizador.Core;

namespace Actualizador.Tests
{
    public static partial class Program
    {
        /// <summary>GitHub arma y publica la versión solo, cuando a main llega una versión que no está publicada.</summary>
        static void TestPublicaGitHub2036()
        {
            string raiz = Raiz();
            if (raiz == null) return;
            string wf = Path.Combine(raiz, ".github", "workflows", "publicar.yml");
            Check(File.Exists(wf), "está el flujo de GitHub que publica");
            if (!File.Exists(wf)) return;
            string y = File.ReadAllText(wf, Encoding.UTF8);
            Check(y.Contains("branches: [main]"), "se dispara al subir a main");
            Check(y.Contains("runs-on: windows-latest"), "se arma en Windows");
            Check(y.Contains("contents: write") && y.Contains("GH_TOKEN"), "puede publicar la versión");
            Check(y.Contains("Publicar.ps1") && y.Contains("-ConPruebas"), "la misma entrega que Publicar.cmd, con las pruebas");
            Check(y.Contains("gh release view") && y.Contains("publicada != 'true'"), "una versión ya publicada no se vuelve a publicar");
        }

        /// <summary>«Authentication complete» y «No se inició sesión»: gh termina con error si no está Git, aunque la sesión quedó.</summary>
        static void TestSesionSinGit2036()
        {
            string raiz = Raiz();
            if (raiz == null) return;
            string ps = File.ReadAllText(Path.Combine(raiz, "build", "Publicar.ps1"), Encoding.UTF8);
            int login = ps.IndexOf("auth login", StringComparison.Ordinal);
            Check(login > 0, "inicia sesión con gh");
            if (login <= 0) return;
            string despues = ps.Substring(login, Math.Min(400, ps.Length - login));
            Check(!despues.Contains("$LASTEXITCODE -ne 0) { throw 'No se inició"), "no decide por el código de salida de «gh auth login»");
            Check(despues.Contains("Test-Gh @('auth', 'status'"), "comprueba que la sesión quedó iniciada");
        }

        static void TestVersion2036()
        {
            Check(VersionUtil.Compare(AppInfo.Version, "2.0.36") >= 0, "Actualizador 2.0.36 o posterior: " + AppInfo.Version);
        }
    }
}
