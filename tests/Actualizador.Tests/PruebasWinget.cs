// winget: tablas, errores, manifiestos, vía directa e instalación.
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
        static InstalledProgram Registro(string key, string name, string version, string publisher, string location, string uninstall, string quiet, string icon, bool msi)
        {
            var p = new InstalledProgram();
            p.RegistryKey = key; p.Name = name; p.Version = version; p.Publisher = publisher;
            p.InstallLocation = location; p.UninstallCommand = uninstall; p.QuietUninstallCommand = quiet ?? "";
            p.UninstallString = !string.IsNullOrEmpty(quiet) ? quiet : uninstall;
            p.DisplayIcon = icon ?? ""; p.IsWindowsInstaller = msi; p.Scope = "Equipo"; p.Architecture = "x64";
            return p;
        }

        const string Uninst = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

        static void TestLineasDeComando()
        {
            string exe, args;
            Check(ProgramRules.SplitCommand("\"C:\\Program Files\\Double Commander\\unins000.exe\" /SILENT", out exe, out args), "comando entre comillas");
            Eq(@"C:\Program Files\Double Commander\unins000.exe", exe, "ejecutable entre comillas");
            Eq("/SILENT", args, "argumentos entre comillas");
            Check(ProgramRules.SplitCommand(@"C:\Program Files (x86)\TeamViewer\uninstall.exe /S", out exe, out args), "comando sin comillas con espacios");
            Eq(@"C:\Program Files (x86)\TeamViewer\uninstall.exe", exe, "ejecutable sin comillas hasta .exe");
            Eq("/S", args, "argumentos sin comillas");
            Check(!ProgramRules.SplitCommand("MsiExec.exe /X{5ABF4005-31D2-4ADC-8492-910E100C71C0}", out exe, out args), "msiexec suelto no es una ruta absoluta (se usa el de System32)");
            Check(!ProgramRules.SplitCommand("uninstall.exe /S", out exe, out args), "ruta relativa: se buscaría en el PATH");
            Check(!ProgramRules.SplitCommand(@"\\servidor\share\unins000.exe", out exe, out args), "recurso de red: no");
            Check(!ProgramRules.SplitCommand(@"C:\x\setup.exed /S", out exe, out args), "«.exe» dentro de otra palabra");
            Check(!ProgramRules.SplitCommand(@"C:\x\..\Windows\evil.exe", out exe, out args), "rutas con «..»: no");
            Check(!ProgramRules.SplitCommand("", out exe, out args) && !ProgramRules.SplitCommand(null, out exe, out args), "vacío");
            Eq("unins000.exe", ProgramRules.FileNameOf(@"C:\Program Files\Double Commander\unins000.exe"), "nombre del archivo con barras de Windows");
            Eq(@"C:\Program Files\Double Commander", ProgramRules.DirectoryOf(@"C:\Program Files\Double Commander\unins000.exe"), "carpeta con barras de Windows");
            Eq(@"C:\Program Files\TeamViewer\TeamViewer.exe", ProgramRules.CleanIconPath("\"C:\\Program Files\\TeamViewer\\TeamViewer.exe\",0"), "DisplayIcon con comillas e índice");
            Eq(@"C:\X\app.exe", ProgramRules.CleanIconPath(@"C:\X\app.exe,-101"), "DisplayIcon con índice negativo");
        }

        static void TestDoubleCommander()
        {
            // Lo que hay en el equipo: Double Commander 1.1.28 instalado con su .exe (Inno Setup).
            var dc = Registro(Uninst + "Double Commander_is1", "Double Commander", "1.1.28", "Alexander Koblov",
                              @"C:\Program Files\Double Commander\", "\"C:\\Program Files\\Double Commander\\unins000.exe\"",
                              "\"C:\\Program Files\\Double Commander\\unins000.exe\" /SILENT", @"C:\Program Files\Double Commander\doublecmd.exe", false);
            Eq(InstallTech.Inno, ProgramRules.Technology(dc), "Double Commander 1.1.28: instalado con Inno Setup (clave _is1)");
            Check(!WingetDirect.LooksMsi(dc), "no es MSI");
            // Lo que publica winget: sólo MSI (wix). Por eso winget devuelve 0x8A15008E.
            var m = Manifest("winget_manifest_doublecommander.yaml");
            Eq("1.2.9", m.Version, "manifiesto 1.2.9");
            Check(m.Installers.TrueForAll(delegate(WingetInstaller i) { return i.IsMsi; }), "todos los instaladores de la 1.2.9 son MSI");
            Eq("uninstallPrevious", m.Installers[0].UpgradeBehavior, "UpgradeBehavior heredado de la raíz");
            var inst = Choose(m, null, "x64", ProgramRules.DetectArchitecture(dc, "x64", null), true, false);
            Check(inst != null && inst.Architecture == "x64" && inst.Url.EndsWith("doublecmd-1.2.9.x86_64-win64.msi"), "reinstalación: el MSI de 64 bits");
            Eq("750c0aab8bcfd22d4d029d552c2bdb573d51213e05a630e5d1ae28fb944c5f55", inst.Sha256, "con su SHA-256 del catálogo");
            bool msiexec; string why;
            string a = WingetRules.BuildArguments(inst, @"C:\Windows\Temp\x\doublecmd.msi", false, null, out msiexec, out why);
            Check(msiexec && a.StartsWith("/i ") && a.Contains("/quiet /norestart"), "se instala con msiexec /i … /quiet /norestart: " + a);

            // Cómo se desinstala la anterior: el desinstalador de Inno, sin ventanas ni preguntas.
            var plan = ProgramRules.PlanUninstall(dc, false, out why);
            Check(plan != null, "hay forma de desinstalarlo en silencio");
            if (plan != null)
            {
                Eq(@"C:\Program Files\Double Commander\unins000.exe", plan.Exe, "el unins000.exe del programa");
                Eq("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", plan.Args, "Inno: /SILENT de QuietUninstallString pasa a /VERYSILENT, sin repetir");
                Check(!plan.UsesMsiexec, "no usa msiexec");
            }
            var inter = ProgramRules.PlanUninstall(dc, true, out why);
            Check(inter != null && inter.Args.Length == 0, "interactivo: el desinstalador tal cual, con sus ventanas");
            // Sin QuietUninstallString también sirve (Inno por el nombre del desinstalador).
            var sinQuiet = Registro(Uninst + "{X}", "Otro", "1", "", @"C:\Program Files\Otro", @"C:\Program Files\Otro\unins001.exe", null, null, false);
            Eq(InstallTech.Inno, ProgramRules.Technology(sinQuiet), "Inno reconocido por unins001.exe");
            plan = ProgramRules.PlanUninstall(sinQuiet, false, out why);
            Check(plan != null && plan.Args == "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", "Inno sin QuietUninstallString");

            // El caso inverso: instalado con MSI.
            var msi = Registro(Uninst + "{5ABF4005-31D2-4ADC-8492-910E100C71C0}", "Double Commander", "1.1.28", "Alexander Koblov", "",
                               "MsiExec.exe /X{5ABF4005-31D2-4ADC-8492-910E100C71C0}", null, null, true);
            Eq(InstallTech.Msi, ProgramRules.Technology(msi), "MSI por WindowsInstaller = 1");
            Eq("{5ABF4005-31D2-4ADC-8492-910E100C71C0}", ProgramRules.MsiProductCode(msi), "código de producto por el nombre de la clave");
            plan = ProgramRules.PlanUninstall(msi, false, out why);
            Check(plan != null && plan.UsesMsiexec && plan.Args == "/x {5ABF4005-31D2-4ADC-8492-910E100C71C0} /qn /norestart", "MSI: msiexec /x {código} /qn /norestart");
            plan = ProgramRules.PlanUninstall(msi, true, out why);
            Check(plan != null && plan.Args == "/x {5ABF4005-31D2-4ADC-8492-910E100C71C0} /norestart", "MSI interactivo: con la ventana de Windows Installer");
            var msiOtraClave = Registro(Uninst + "Programa", "P", "1", "", "", "MsiExec.exe /I{6D07C94B-E850-4F2F-9C38-49209D5BF6D4}", null, null, false);
            Eq("{6D07C94B-E850-4F2F-9C38-49209D5BF6D4}", ProgramRules.MsiProductCode(msiOtraClave), "código de producto desde MsiExec.exe /I{…}");
            Eq(InstallTech.Msi, ProgramRules.Technology(msiOtraClave), "MSI por el comando de desinstalación");

            // NSIS y ejecutables desconocidos.
            var nsis = Registro(Uninst + "App", "App", "1", "", @"C:\Program Files\App", @"C:\Program Files\App\uninst.exe", null, null, false);
            Eq(InstallTech.Nsis, ProgramRules.Technology(nsis), "NSIS por uninst.exe");
            plan = ProgramRules.PlanUninstall(nsis, false, out why);
            Check(plan != null && plan.Args == "/S", "NSIS: /S");
            var raro = Registro(Uninst + "Raro", "Raro", "1", "", @"C:\Program Files\Raro", @"C:\Program Files\Raro\remove.exe -x", null, null, false);
            Eq(InstallTech.Exe, ProgramRules.Technology(raro), "otro ejecutable");
            Check(ProgramRules.PlanUninstall(raro, false, out why) == null && why.Contains("Mostrar la ventana de cada instalador"), "sin forma silenciosa conocida: no se adivina, se explica: " + why);
            Check(ProgramRules.PlanUninstall(raro, true, out why) != null, "interactivo sí: con su ventana");
            var conQuiet = Registro(Uninst + "Raro", "Raro", "1", "", @"C:\Program Files\Raro", @"C:\Program Files\Raro\remove.exe", @"C:\Program Files\Raro\remove.exe --quiet", null, false);
            plan = ProgramRules.PlanUninstall(conQuiet, false, out why);
            Check(plan != null && plan.Args == "--quiet", "QuietUninstallString del programa, tal cual");
            Eq("/X /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", ProgramRules.InnoSilent("/X /silent /NORESTART"), "Inno: sin repetir interruptores");

            // winget: el código y el mensaje.
            Eq(0x8A15008Eu, WingetDirect.TechMismatch, "0x8A15008E es UPDATE_INSTALL_TECHNOLOGY_MISMATCH");
            Check(WingetProvider.Describe(0x8A15008E).Contains("otro tipo de instalador"), "0x8A15008E en castellano: " + WingetProvider.Describe(0x8A15008E));
            var c = new UpdateCandidate();
            var r = new ProcResult();
            r.Started = true; r.ExitCode = unchecked((int)0x8A15008E);
            r.Out = "Se encontró una versión más reciente, pero la tecnología de instalación es diferente de la versión actual instalada. Desinstale el paquete e instale la versión más reciente.";
            WingetProvider.ApplyResult(c, r, "Se encontró una versión más reciente…", 1, "para desinstalar la versión anterior hay que ejecutar Actualizador como administrador", true);
            Check(c.Result.StartsWith("La versión nueva usa otro tipo de instalador") && c.Result.Contains("No se pudo reinstalar automáticamente: para desinstalar"),
                  "si no se puede reinstalar, se dice por qué: " + c.Result);
            Check(!c.Result.Contains("winget devolvió 0x8A1"), "ya no dice sólo «winget devolvió 0x8A1…»");
        }

        static void TestGomPlayer()
        {
            // Lo que registró el equipo del usuario: lo instalado firmado por GRETECH, el instalador nuevo por GOM&Company.
            const string subject = "CN=GOM&Company, O=GOM&Company, L=Songpa District, S=Seoul, C=KR, SERIALNUMBER=110111-1649578, OID.2.5.4.15=Private Organization, OID.1.3.6.1.4.1.311.60.2.1.2=Seoul, OID.1.3.6.1.4.1.311.60.2.1.3=KR";
            var installed = new List<string> { "GRETECH CORPORATION" };
            string how;
            string reason = WingetRules.MatchSigner(subject, installed, "GOM & Company", out how);
            Check(reason != null, "la regla anterior (mismo firmante que lo instalado) lo rechazaba: " + reason);

            // El editor en el manifiesto oficial (manifiesto de versión → idioma predeterminado → Publisher).
            string locale;
            Eq("", WingetRules.PublisherFrom(Sample("winget_version_gomplayer.yaml"), out locale), "el manifiesto de versión no trae el editor…");
            Eq("en-US", locale, "…pero sí el idioma predeterminado");
            var loc = MiniYaml.Parse(Sample("winget_locale_gomplayer.yaml"));
            string manifestPub = MiniYaml.Str(loc, "Publisher");
            Eq("GOM & Company", manifestPub, "editor en el manifiesto de idioma en-US");
            Eq("GOMLab.GOMPlayer", MiniYaml.Str(loc, "PackageIdentifier"), "el manifiesto de idioma es del mismo paquete");
            Eq("https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/g/GOMLab/GOMPlayer/2.3.114.5384/GOMLab.GOMPlayer.locale.en-US.yaml",
               WingetRules.LocaleUrl("GOMLab.GOMPlayer", "2.3.114.5384", "en-US"), "dirección del manifiesto de idioma");
            Eq(null, WingetRules.LocaleUrl("GOMLab.GOMPlayer", "2.3.114.5384", "../x"), "idioma con barras: no");
            Eq(null, WingetRules.LocaleUrl("GOMLab.GOMPlayer", "2.3.114.5384", "en US"), "idioma con espacios: no");
            string single = "PackageIdentifier: A.B\nPackageVersion: 1.0\nPublisher: Empresa S.A.\nInstallers: []\n";
            Eq("Empresa S.A.", WingetRules.PublisherFrom(single, out locale), "manifiesto de un solo archivo: el editor directo");

            // La regla nueva: firmante == editor del registro == editor del manifiesto (letras y dígitos, estricto).
            Eq("gomcompany", WingetRules.CompactCompany("GOM & Company"), "«GOM & Company» compacto");
            Eq("gomcompany", WingetRules.CompactCompany("GOM&Company"), "«GOM&Company» compacto");
            reason = WingetRules.MatchRenamedPublisher(subject, installed, "GOM & Company", manifestPub, out how);
            Check(reason == null, "GOM Player: se acepta el cambio de nombre de la empresa: " + reason);
            Check(how != null && how.Contains("GOM&Company") && how.Contains("GRETECH CORPORATION"), "y se explica: " + how);

            // Lo que NO pasa.
            Check(WingetRules.MatchRenamedPublisher(subject, installed, "", manifestPub, out how) != null, "sin editor confiable en el registro (instalación de usuario, elevado): no");
            Check(WingetRules.MatchRenamedPublisher(subject, installed, "GOM & Company", "", out how) != null, "sin el manifiesto oficial: no");
            Check(WingetRules.MatchRenamedPublisher(subject, installed, "GOM & Company", "Otra Empresa", out how) != null, "registro y manifiesto no coinciden: no");
            Check(WingetRules.MatchRenamedPublisher("CN=GOM Inc., O=GOM Inc.", installed, "GOM & Company", "GOM & Company", out how) != null,
                  "«GOM Inc.» no es «GOM & Company»: la comparación no quita la forma jurídica");
            Check(WingetRules.MatchRenamedPublisher("CN=Atacante, O=Atacante", installed, "GOM & Company", "GOM & Company", out how) != null, "otro firmante: no");
            Check(WingetRules.MatchRenamedPublisher("", installed, "GOM & Company", "GOM & Company", out how) != null, "sin firmante legible: no");
            Check(WingetRules.MatchRenamedPublisher(subject, installed, "GO", "GO", out how) != null, "editores de menos de 3 letras: no");
        }

        static void TestTeamViewer()
        {
            // TeamViewer de 32 bits en un Windows de 64: winget elegía el de 64 y el instalador abortaba con el código 2.
            var tv32 = Registro(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TeamViewer", "TeamViewer", "15.81.5", "TeamViewer",
                                @"C:\Program Files (x86)\TeamViewer", "\"C:\\Program Files (x86)\\TeamViewer\\uninstall.exe\"", null,
                                "\"C:\\Program Files (x86)\\TeamViewer\\TeamViewer.exe\",0", false);
            Eq("x86", ProgramRules.DetectArchitecture(tv32, "x64", null), "TeamViewer en Program Files (x86): 32 bits");
            Eq("x86", ProgramRules.DetectArchitecture(tv32, "x64", delegate(string f) { return f.EndsWith("TeamViewer.exe") ? "x86" : ""; }), "…y su TeamViewer.exe es x86");
            Eq("x86", ProgramRules.WingetArchitecture("x86", "x64"), "se le pide a winget --architecture x86");
            Eq(null, ProgramRules.WingetArchitecture("x64", "x64"), "si es la nativa, no hace falta");
            Eq(null, ProgramRules.WingetArchitecture("", "x64"), "si no se sabe, no se fuerza");
            Eq("x64", ProgramRules.WingetArchitecture("x64", "ARM64"), "x64 emulado en ARM64: se mantiene x64");
            Eq(null, ProgramRules.WingetArchitecture("x86", "x86"), "Windows de 32 bits: no hay otra");
            Eq(null, ProgramRules.WingetArchitecture("arm64", "x64"), "dato imposible: no se fuerza");

            // El encabezado PE del ejecutable principal manda sobre la carpeta.
            var raro = Registro(Uninst + "Raro", "Raro", "1", "", @"C:\Program Files\Raro", @"C:\Program Files\Raro\unins000.exe", null, @"C:\Program Files\Raro\raro.exe", false);
            Eq("x86", ProgramRules.DetectArchitecture(raro, "x64", delegate(string f) { return "x86"; }), "PE x86 aunque esté en Program Files");
            Eq("x64", ProgramRules.DetectArchitecture(raro, "x64", delegate(string f) { return ""; }), "PE ilegible: la carpeta (Program Files = 64 bits)");
            Eq("", ProgramRules.DetectArchitecture(raro, "ARM64", delegate(string f) { return ""; }), "en ARM64, Program Files tiene arm64 y x64: no se adivina");
            var desins = Registro(Uninst + "Z", "Z", "1", "", "", @"C:\Program Files (x86)\Z\unins000.exe", null, @"C:\Program Files (x86)\Z\unins000.exe,0", false);
            Eq("x86", ProgramRules.DetectArchitecture(desins, "x64", delegate(string f) { return "x64"; }), "el desinstalador no cuenta como ejecutable principal (se usa la carpeta)");
            var nada = Registro(Uninst + "N", "N", "1", "", "", "", null, null, false);
            Eq("", ProgramRules.DetectArchitecture(nada, "x64", null), "sin datos: no se sabe");
            Eq("", ProgramRules.DetectArchitecture(null, "x64", null), "sin programa: no se sabe");
            Eq("x86", ProgramRules.DetectArchitecture(nada, "x86", null), "Windows de 32 bits: todo es x86");
            var usuario = Registro(Uninst + "U", "U", "1", "", @"C:\Users\fede\AppData\Local\Programs\U", "", null, null, false);
            Eq("", ProgramRules.DetectArchitecture(usuario, "x64", null), "instalación de usuario sin ejecutable: no se adivina");

            // Con x86, la vía directa elige el nullsoft de 32 bits (no el zip ni el de 64).
            var m = Manifest("winget_manifest_teamviewer.yaml");
            var t = Choose(m, null, "x64", "x86", true, false);
            Check(t != null && t.Architecture == "x86" && t.Type == "nullsoft" && t.Url.EndsWith("TeamViewer_Setup_15.81.6.exe"), "TeamViewer x86: TeamViewer_Setup_15.81.6.exe");

            // El mensaje del código 2 explica la causa y no repite «probá con interactiva» si ya lo era.
            var c = new UpdateCandidate();
            var r = new ProcResult();
            r.Started = true; r.ExitCode = unchecked((int)0x8A150006);
            r.Out = "Iniciando instalación de paquete...\nError del instalador con el código de salida: 2\n";
            WingetProvider.ApplyResult(c, r, "Error del instalador con el código de salida: 2", 1, null, true);
            Check(c.Result.Contains("código 2") && c.Result.Contains("arquitectura"), "código 2: otra edición o arquitectura instalada: " + c.Result);
            Check(!c.Result.Contains("Mostrar la ventana de cada instalador"), "ya era interactiva: no se sugiere de nuevo");
            WingetProvider.ApplyResult(c, r, "", 1, null, false);
            Check(c.Status == UpdateStatus.Fallida, "sigue siendo un error");
            r.Out = "Error del instalador con el código de salida: 1234\n";
            WingetProvider.ApplyResult(c, r, "", 1, null, false);
            Check(c.Result.Contains("1234") && c.Result.Contains("Mostrar la ventana de cada instalador"), "otro código, no interactivo: se sugiere interactiva");
            // 1603 (1.5.3): no se arregla con el modo interactivo; casi siempre es el programa abierto.
            r.Out = "Error del instalador con el código de salida: 1603\n";
            WingetProvider.ApplyResult(c, r, "", 1, null, false);
            Check(c.Result.Contains("1603") && c.Result.Contains("abierto") && !c.Result.Contains("Mostrar la ventana de cada instalador"), "1603: programa abierto, sin sugerir interactiva: " + c.Result);
        }

        static void TestCodigosWinget150()
        {
            // Contra AppInstallerErrors.h de winget-cli.
            Eq("El instalador no admite ejecutarse como administrador", WingetProvider.Describe(0x8A150056), "0x8A150056 es INSTALLER_PROHIBITS_ELEVATION (antes decía «falta de interacción»)");
            Check(WingetProvider.Describe(0x8A150068).Contains("anclado"), "0x8A150068 paquete anclado");
            Check(WingetProvider.Describe(0x8A15002D).Contains("SmartScreen"), "0x8A15002D control de seguridad");
            Check(WingetProvider.Describe(0x8A150060).Contains("antivirus"), "0x8A150060 análisis del archivo comprimido");
            Check(WingetProvider.Describe(0x8A15004F).Contains("no es más nueva"), "0x8A15004F");
            Check(!WingetProvider.Describe(0x8A150061).StartsWith("winget devolvió"), "0x8A150061 traducido");
            Eq(0x8A150010u, WingetProvider.NoApplicableInstaller, "sin instalador aplicable");
            Eq(0x8A15002Bu, WingetProvider.UpdateNotApplicable, "sin actualización aplicable");
            var c = new UpdateCandidate();
            var r = new ProcResult();
            r.Started = true; r.ExitCode = unchecked((int)0x8A15010B);
            WingetProvider.ApplyResult(c, r, "", 1, null);
            Eq(UpdateStatus.RequiereReinicio, c.Status, "0x8A15010B: el instalador inició el reinicio = instalada, requiere reinicio");
        }

        /// <summary>El flujo real de WingetProvider.Install, con winget simulado (los códigos HRESULT no existen en Linux).</summary>
        static void TestFlujoDeInstalacion()
        {
            var tv = Registro(Uninst + "TeamViewer", "TeamViewer", "15.81.5", "TeamViewer", @"C:\Program Files (x86)\TeamViewer",
                              "\"C:\\Program Files (x86)\\TeamViewer\\uninstall.exe\"", null, null, false);
            tv.Architecture = "x86";

            // TeamViewer: se pide la arquitectura instalada y se instala.
            var llamadas = new List<string>();
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            wp.RunForTests = delegate(string args)
            {
                llamadas.Add(args);
                return args.Contains("--architecture x86") ? Salida(0, "Instalado correctamente") : Salida(0x8A150006, "Error del instalador con el código de salida: 2");
            };
            var c = FilaWinget("TeamViewer", "TeamViewer.TeamViewer", "15.81.5", "15.81.6");
            wp.Install(ContextoSimulado(true, tv), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Eq(1, llamadas.Count, "TeamViewer: una sola llamada a winget");
            Check(llamadas.Count > 0 && llamadas[0].StartsWith("upgrade --id \"TeamViewer.TeamViewer\" --exact") && llamadas[0].EndsWith(" --architecture x86"),
                  "con --architecture x86: " + (llamadas.Count > 0 ? llamadas[0] : ""));
            Eq(UpdateStatus.Instalada, c.Status, "TeamViewer x86 actualizado");

            // Si winget no tiene instalador para esa arquitectura y lo instalado NO es MSI (1.5.3), no se
            // pasa a la de 64 bits: dejaría dos copias (o abortaría, como TeamViewer con el código 2).
            llamadas.Clear();
            wp.RunForTests = delegate(string args)
            {
                llamadas.Add(args);
                return args.Contains("--architecture") ? Salida(WingetProvider.NoApplicableInstaller, "No se encontró ningún instalador aplicable") : Salida(0, "Instalado correctamente");
            };
            c = FilaWinget("TeamViewer", "TeamViewer.TeamViewer", "15.81.5", "15.81.6");
            wp.Install(ContextoSimulado(true, tv), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            // 1.6.6: con la versión fijada winget no dice la causa; se repite UNA vez sin --version (misma
            // arquitectura). Nunca sin --architecture.
            Eq(2, llamadas.Count, "sin instalador x86 y no es MSI: no se reintenta sin --architecture (sólo sin --version)");
            Check(llamadas.Count == 2 && llamadas[1].Contains("--architecture x86") && !llamadas[1].Contains("--version"), "el segundo intento, sin --version y en x86: " + (llamadas.Count > 1 ? llamadas[1] : ""));
            Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("dos copias") && c.Result.Contains("winget install --id TeamViewer.TeamViewer"),
                  "…y se explica cómo pasar a 64 bits: " + c.Result);

            // Un programa de 64 bits (o sin datos) no lleva --architecture.
            llamadas.Clear();
            wp.RunForTests = delegate(string args) { llamadas.Add(args); return Salida(0, ""); };
            var x64 = Registro(Uninst + "7-Zip", "7-Zip 24.07 (x64)", "24.07", "Igor Pavlov", @"C:\Program Files\7-Zip\", @"C:\Program Files\7-Zip\Uninstall.exe", null, @"C:\Program Files\7-Zip\7zFM.exe", false);
            c = FilaWinget("7-Zip 24.07 (x64)", "7zip.7zip", "24.07", "25.01");
            wp.Install(ContextoSimulado(true, x64), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(llamadas.Count == 1 && !llamadas[0].Contains("--architecture"), "7-Zip x64: sin --architecture");

            // Double Commander: winget devuelve 0x8A15008E; se intenta la reinstalación verificada. En Linux
            // su desinstalador (C:\…) no existe, así que no se toca nada y la fila explica por qué.
            var dc = Registro(Uninst + "Double Commander_is1", "Double Commander", "1.1.28", "Alexander Koblov",
                              @"C:\Program Files\Double Commander\", "\"C:\\Program Files\\Double Commander\\unins000.exe\"",
                              "\"C:\\Program Files\\Double Commander\\unins000.exe\" /SILENT", null, false);
            llamadas.Clear();
            wp.RunForTests = delegate(string args)
            {
                llamadas.Add(args);
                return Salida(WingetDirect.TechMismatch, "Se encontró una versión más reciente, pero la tecnología de instalación es diferente de la versión actual instalada. Desinstale el paquete e instale la versión más reciente.");
            };
            c = FilaWinget("Double Commander", "alexx2000.DoubleCommander", "1.1.28", "1.2.9");
            wp.Install(ContextoSimulado(true, dc), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Eq(1, llamadas.Count, "Double Commander: winget upgrade una vez; sin desinstalador no se llama a winget install");
            Eq(UpdateStatus.Fallida, c.Status, "no se pudo reinstalar");
            Check(c.Result.Contains("otro tipo de instalador") && c.Result.Contains("no se encuentra su desinstalador"), "y se dice por qué: " + c.Result);

            // Instalado para un usuario y Actualizador elevado: no se ejecuta un desinstalador que puede escribir cualquiera.
            var dcUsuario = Registro(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Double Commander_is1", "Double Commander", "1.1.28", "Alexander Koblov",
                                     @"C:\Users\fede\AppData\Local\Double Commander", @"C:\Users\fede\AppData\Local\Double Commander\unins000.exe", null, null, false);
            c = FilaWinget("Double Commander", "alexx2000.DoubleCommander", "1.1.28", "1.2.9");
            wp.Install(ContextoSimulado(true, dcUsuario), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("instalado para un usuario"), "instalación de usuario, elevado: no se desinstala: " + c.Result);
            // Sin administrador, una instalación del equipo tampoco.
            c = FilaWinget("Double Commander", "alexx2000.DoubleCommander", "1.1.28", "1.2.9");
            wp.Install(ContextoSimulado(false, dc), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("como administrador"), "sin administrador: se pide elevar: " + c.Result);
        }

        static void TestMensajes150()
        {
            var report = new UpdateSession();
            var u = new UpdateCandidate();
            u.Kind = UpdateKind.Programa; u.Status = UpdateStatus.Disponible; u.Name = "X";
            report.Updates.Add(u);
            Eq("1 app", Fmt.Plural(1, "app", "apps"), "plural de apps");
            Check(ProgramRules.IsInFolder(@"C:\Program Files\Double Commander\doublecmd.exe", @"C:\Program Files\Double Commander\"), "proceso abierto desde la carpeta del programa");
            Check(!ProgramRules.IsInFolder(@"C:\Program Files\Double Commander 2\x.exe", @"C:\Program Files\Double Commander"), "otra carpeta con el mismo comienzo: no");
            Check(!ProgramRules.IsInFolder(@"C:\x.exe", @"C:\"), "la raíz del disco no cuenta como carpeta del programa");
            Check(!ProgramRules.IsInFolder(null, @"C:\X"), "sin ruta");
            Eq("EXE (Inno Setup)", ProgramRules.TechText(InstallTech.Inno), "texto de la tecnología");
            Eq("MSI", ProgramRules.WingetTypeText("wix"), "wix es MSI");
        }

        // ------------------------------------------------------------------ WinRAR: nunca una versión más vieja

        static void TestGuardiaDeVersion()
        {
            string linea = "  Encontrado WinRAR [RARLab.WinRAR] Versión 7.01.0";
            string why = WingetProvider.DowngradeGuard(linea, "RARLab.WinRAR", "7.23.0", "Desconocida");
            Check(why != null && why.Contains("7.01.0") && why.Contains("7.23.0"), "WinRAR 7.01 cuando se ofrecía la 7.23: se detiene: " + why);
            Check(WingetProvider.DowngradeGuard("Found WinRAR [RARLab.WinRAR] Version 7.23.0", "RARLab.WinRAR", "7.23.0", "") == null, "la versión ofrecida: sigue");
            Check(WingetProvider.DowngradeGuard("Found WinRAR [RARLab.WinRAR] Version 7.24.0", "RARLab.WinRAR", "7.23.0", "") == null, "una más nueva: sigue");
            Check(WingetProvider.DowngradeGuard("Found Microsoft Visual C++ 2015-2022 Redistributable (x64) [Microsoft.VCRedist.2015+.x64] Version 14.40", "Oracle.VirtualBox", "7.2.20", "7.2.16") == null,
                  "una dependencia (otro Id) no cuenta");
            why = WingetProvider.DowngradeGuard("Encontrado WinRAR [RARLab.WinRAR] Versión 7.01.0", "RARLab.WinRAR", "7.2…", "7.13.0");
            Check(why != null && why.Contains("instalada (7.13.0)"), "ofrecida recortada: se compara con la instalada: " + why);
            Check(WingetProvider.DowngradeGuard("Iniciando instalación de paquete...", "RARLab.WinRAR", "7.23.0", "") == null, "otras líneas no cuentan");

            var w = new WingetRow(); w.Id = "RARLab.WinRAR"; w.Name = "WinRAR"; w.Version = "Unknown"; w.Available = "7.23.0"; w.Source = "winget";
            Eq("7.23.0", WingetProvider.PinnedVersion(w), "se fija la versión ofrecida");
            w.Available = "174.0.0.13…"; Check(WingetProvider.PinnedVersion(w) == null, "recortada con «…»: no se fija");
            w.Available = "Unknown"; Check(WingetProvider.PinnedVersion(w) == null, "desconocida: no se fija");
            w.Available = "1.0\" --override \"x"; Check(WingetProvider.PinnedVersion(w) == null, "con comillas: no se fija (no se cuelan argumentos)");
            w.Available = "7.23.0"; w.Source = "msstore"; Check(WingetProvider.PinnedVersion(w) == null, "Store: no se fija");

            // Proceso real que anuncia una versión vieja: se detiene enseguida (sin esperar a que termine).
            if (Path.DirectorySeparatorChar == '/' && File.Exists("/bin/sh"))
            {
                var sw = Stopwatch.StartNew();
                var r = ProcessRunner.Run("/bin/sh", "-c \"echo 'Encontrado WinRAR [RARLab.WinRAR] Versión 7.01.0'; exec sleep 30\"", 60000, Encoding.UTF8, null, null,
                                          delegate(string l) { return WingetProvider.DowngradeGuard(l, "RARLab.WinRAR", "7.23.0", ""); });
                Check(r.Aborted && r.AbortReason.Contains("7.01.0"), "proceso detenido por la guardia: " + r.Describe());
                Check(sw.ElapsedMilliseconds < 15000, "se detuvo enseguida (" + sw.ElapsedMilliseconds + " ms)");
                Check(!r.Ok, "un proceso detenido no cuenta como correcto");
            }
        }

        // ------------------------------------------------------------------ Flujos simulados

        static InstalledProgram WinRar32()
        {
            var p = Registro(Uninst + "WinRAR archiver", "Compresor WinRAR", "", "win.rar GmbH", @"C:\Program Files (x86)\WinRAR\",
                             @"C:\Program Files (x86)\WinRAR\uninstall.exe", null, @"C:\Program Files (x86)\WinRAR\WinRAR.exe", false);
            p.Architecture = "x86";
            return p;
        }

        static InstalledProgram EpicMsi()
        {
            var p = Registro(Uninst + "{A2FB1E1A-55D9-4511-A0BF-DEAD0493FBBC}", "Epic Games Launcher", "1.3.193.0", "Epic Games, Inc.", @"C:\Program Files (x86)\Epic Games\",
                             "MsiExec.exe /X{A2FB1E1A-55D9-4511-A0BF-DEAD0493FBBC}", null, null, true);
            p.Architecture = "x86";
            return p;
        }

        static void TestFlujoWinRarYEpic()
        {
            var llamadas = new List<string>();
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");

            // WinRAR de 32 bits (EXE), versión desconocida: se pide la 7.23 exacta y en 32 bits. winget no
            // la tiene para 32 bits → antes se reintentaba sin --architecture (o winget elegía la 7.01).
            // Ahora no se toca y se explica.
            wp.RunForTests = delegate(string args)
            {
                llamadas.Add(args);
                return Salida(WingetProvider.NoApplicableInstaller, "No se encontró ningún instalador aplicable");
            };
            var c = FilaWinget("Compresor WinRAR", "RARLab.WinRAR", "Unknown", "7.23.0");
            c.InstalledVersion = "Desconocida";
            wp.Install(ContextoSimulado(true, WinRar32()), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Eq(2, llamadas.Count, "WinRAR: no se cambia de arquitectura un EXE (1.6.6: un segundo intento sin --version, en x86)");
            Check(llamadas.Count == 2 && llamadas[1].Contains("--architecture x86") && !llamadas[1].Contains("--version"), "segundo intento sin --version: " + (llamadas.Count > 1 ? llamadas[1] : ""));
            Check(llamadas[0].Contains("--version \"7.23.0\"") && llamadas[0].Contains("--architecture x86") && llamadas[0].Contains("--include-unknown"),
                  "con --version, --architecture x86 e --include-unknown: " + llamadas[0]);
            Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("32 bits") && c.Result.Contains("dos copias"), "se explica: " + c.Result);
            Eq("Desconocida", c.InstalledVersion, "la versión instalada no se inventa");

            // Lo que pasó el 03/10: winget anuncia la 7.01.0 → se detiene antes de instalar.
            llamadas.Clear();
            wp.RunForTests = delegate(string args)
            {
                llamadas.Add(args);
                return Salida(0, "Encontrado WinRAR [RARLab.WinRAR] Versión 7.01.0\nDescargando https://www.rarlab.com/rar/winrar-x32-701es.exe\nInstalado correctamente\n");
            };
            c = FilaWinget("Compresor WinRAR", "RARLab.WinRAR", "Unknown", "7.23.0");
            wp.Install(ContextoSimulado(true, WinRar32()), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(c.Status == UpdateStatus.Fallida && c.Result.StartsWith("No se instaló nada") && c.Result.Contains("7.01.0"), "WinRAR 7.01: detenido: " + c.Result);

            // Epic Games Launcher (MSI, en Program Files (x86)): sin instalador x86 sí se pasa a la que elija
            // winget (el MSI reemplaza al instalado). Estaba abierto en segundo plano y el MSI devolvió 1603.
            var epicAbierto = new RunningProcess();
            epicAbierto.Pid = 987654; epicAbierto.Name = "EpicGamesLauncher.exe"; epicAbierto.SessionId = -1;
            epicAbierto.Path = @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe";
            var servicio = new RunningProcess();
            servicio.Pid = 987655; servicio.Name = "EpicOnlineServices.exe"; servicio.IsService = true;
            servicio.Path = @"C:\Program Files (x86)\Epic Games\Epic Online Services\service\EpicOnlineServices.exe";
            var otro = new RunningProcess();
            otro.Pid = 987656; otro.Name = "notepad.exe"; otro.Path = @"C:\Windows\System32\notepad.exe";
            RunningPrograms.SnapshotForTests = delegate { return new List<RunningProcess> { epicAbierto, servicio, otro }; };
            try
            {
                string salida1603 = "Encontrado Epic Games Launcher [EpicGames.EpicGamesLauncher] Versión 1.3.210.0\n" +
                                    "Iniciando instalación de paquete...\nError del instalador con el código de salida: 1603\n" +
                                    "El registro del instalador está disponible en: C:\\Users\\Pc1\\AppData\\Local\\Packages\\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\\LocalState\\DiagOutputDir\\EpicGames.log\n";
                llamadas.Clear();
                wp.RunForTests = delegate(string args)
                {
                    llamadas.Add(args);
                    return args.Contains("--architecture x86") ? Salida(WingetProvider.UpdateNotApplicable, "No se ha encontrado ninguna actualización aplicable.")
                                                               : Salida(WingetProvider.InstallerFailed, salida1603);
                };
                wp.CloseRunning = false;
                c = FilaWinget("Epic Games Launcher", "EpicGames.EpicGamesLauncher", "1.3.193.0", "1.3.210.0");
                wp.Install(ContextoSimulado(true, EpicMsi()), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                // 1.6.6: x86 con versión fijada → x86 sin versión (winget dice la causa real) → sin --architecture, con la versión.
                Eq(3, llamadas.Count, "Epic (MSI): tercer intento sin --architecture");
                Check(llamadas[1].Contains("--architecture x86") && !llamadas[1].Contains("--version"), "…el segundo, sin --version: " + llamadas[1]);
                Check(!llamadas[2].Contains("--architecture") && llamadas[2].Contains("--version \"1.3.210.0\""), "…el tercero, con la versión fijada: " + llamadas[2]);
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("1603") && c.Result.Contains("EpicGamesLauncher.exe") && c.Result.Contains("segundo plano"),
                      "1603 con el launcher abierto: se dice que estaba abierto: " + c.Result);
                Check(!c.Result.Contains("EpicOnlineServices"), "el servicio no se menciona (lo maneja el instalador)");

                // Aceptando cerrarlo: el proceso simulado ya no existe, así que «se cerró» y no queda la pista.
                wp.CloseRunning = true;
                c = FilaWinget("Epic Games Launcher", "EpicGames.EpicGamesLauncher", "1.3.193.0", "1.3.210.0");
                wp.Install(ContextoSimulado(true, EpicMsi()), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && !c.Result.Contains("Estaba abierto"), "cerrado antes: sin la pista de «abierto»: " + c.Result);

                // La lista que muestra la interfaz antes de instalar.
                var ctx = ContextoSimulado(true, EpicMsi(), WinRar32());
                var filaEpic = FilaWinget("Epic Games Launcher", "EpicGames.EpicGamesLauncher", "1.3.193.0", "1.3.210.0");
                var filaRar = FilaWinget("Compresor WinRAR", "RARLab.WinRAR", "Unknown", "7.23.0");
                var abiertos = UpdateManager.FindRunning(ctx, new List<UpdateCandidate> { filaEpic, filaRar });
                Check(abiertos.Count == 1 && abiertos[0].Key == filaEpic && abiertos[0].Value.Count == 1 && abiertos[0].Value[0].Name == "EpicGamesLauncher.exe",
                      "la interfaz pregunta sólo por Epic, sin el servicio");
            }
            finally { RunningPrograms.SnapshotForTests = null; wp.CloseRunning = false; }
        }

        static void TestConfirmacionDespuesDeInstalar()
        {
            var tv = Registro(Uninst + "TeamViewer", "TeamViewer", "15.81.5", "TeamViewer", @"C:\Program Files\TeamViewer",
                              "\"C:\\Program Files\\TeamViewer\\uninstall.exe\"", null, null, false);
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            wp.RunForTests = delegate(string args) { return Salida(0, "Encontrado TeamViewer [TeamViewer.TeamViewer] Versión 15.82.6\nInstalado correctamente\n"); };
            Func<string, string> tabla = delegate(string version)
            {
                return "Nombre     Id                    Versión  Origen\n-----------------------------------------------------\nTeamViewer TeamViewer.TeamViewer " + version + "  winget\n";
            };
            var listas = new List<string>();

            // winget ve la versión nueva: confirmada.
            wp.ListForTests = delegate(string args) { listas.Add(args); return Salida(0, tabla("15.82.6")); };
            var c = FilaWinget("TeamViewer", "TeamViewer.TeamViewer", "15.81.5", "15.82.6");
            wp.Install(ContextoSimulado(true, tv), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(listas.Count == 1 && listas[0].StartsWith("list --id \"TeamViewer.TeamViewer\" --exact"), "se consulta winget list del paquete: " + (listas.Count > 0 ? listas[0] : ""));
            Check(c.Status == UpdateStatus.Instalada && c.Result == "Actualizada correctamente" && c.InstalledVersion == "15.82.6", "confirmada: " + c.Result);

            // Quedó una versión MÁS VIEJA que la que había: error, no «Actualizada».
            wp.ListForTests = delegate(string args) { return Salida(0, tabla("15.70.1")); };
            c = FilaWinget("TeamViewer", "TeamViewer.TeamViewer", "15.81.5", "15.82.6");
            wp.Install(ContextoSimulado(true, tv), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("MÁS VIEJA") && c.InstalledVersion == "15.70.1", "degradación detectada: " + c.Result);

            // Sigue EXACTAMENTE la misma: no cambió nada. Desde la 1.6.7 es un error con la causa probable (antes
            // quedaba «Actualizada» en verde y el programa volvía a aparecer en la búsqueda siguiente: TeraBox).
            wp.ListForTests = delegate(string args) { return Salida(0, tabla("15.81.5")); };
            c = FilaWinget("TeamViewer", "TeamViewer.TeamViewer", "15.81.5", "15.82.6");
            wp.Install(ContextoSimulado(true, tv), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("no cambió nada") && c.Result.Contains("15.81.5"), "nada cambió: error con la causa: " + c.Result);

            // Cambió, pero a una intermedia (algunos terminan en segundo plano): instalada, con aviso.
            wp.ListForTests = delegate(string args) { return Salida(0, tabla("15.82.0")); };
            c = FilaWinget("TeamViewer", "TeamViewer.TeamViewer", "15.81.5", "15.82.6");
            wp.Install(ContextoSimulado(true, tv), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(c.Status == UpdateStatus.Instalada && c.Result.Contains("todavía figura la 15.82.0"), "todavía figura una intermedia: " + c.Result);

            // Dos copias (32 y 64 bits): se avisa de la vieja.
            wp.ListForTests = delegate(string args)
            {
                return Salida(0, "Nombre     Id                    Versión  Origen\n-----------------------------------------------------\n" +
                                 "TeamViewer TeamViewer.TeamViewer 15.82.6  winget\nTeamViewer TeamViewer.TeamViewer 15.40.2  winget\n");
            };
            c = FilaWinget("TeamViewer", "TeamViewer.TeamViewer", "15.81.5", "15.82.6");
            wp.Install(ContextoSimulado(true, tv), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(c.Status == UpdateStatus.Instalada && c.Result.Contains("Sigue instalada también la 15.40.2"), "otra copia vieja: " + c.Result);

            // winget no informa la versión: queda como antes (sin inventar un error).
            wp.ListForTests = delegate(string args) { return Salida(0, tabla("Unknown")); };
            c = FilaWinget("TeamViewer", "TeamViewer.TeamViewer", "15.81.5", "15.82.6");
            wp.Install(ContextoSimulado(true, tv), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(c.Status == UpdateStatus.Instalada && c.Result == "Actualizada correctamente", "sin versión en winget list: como antes");

            // Versión instalada desconocida y sin --version (ofrecida recortada): instaló otra → error.
            wp.ListForTests = delegate(string args) { return Salida(0, tabla("15.70.1")); };
            c = FilaWinget("TeamViewer", "TeamViewer.TeamViewer", "Unknown", "15.82.6");
            c.InstalledVersion = "Desconocida";
            ((WingetRow)c.ProviderTag).Available = "15.82…";
            c.AvailableVersion = "15.82.6";
            wp.Install(ContextoSimulado(true, tv), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("instaló la 15.70.1"), "sin versión fijada, quedó otra: " + c.Result);
        }

        static void TestVersionDesconocida()
        {
            var w = new WingetRow(); w.Id = "RARLab.WinRAR"; w.Name = "Compresor WinRAR"; w.Version = "Unknown"; w.Available = "7.23.0"; w.Source = "winget";
            var progs = new List<InstalledProgram> { WinRar32() };
            var anterior = WingetProvider.ExeVersionReader;
            try
            {
                // El ejecutable dice 7.23.0: ya está al día y no se ofrece (antes se «actualizaba» a la 7.01).
                WingetProvider.ExeVersionReader = delegate(string path) { return path.EndsWith("WinRAR.exe") ? "7.23.0" : ""; };
                var c = new UpdateCandidate(); c.Status = UpdateStatus.Disponible; c.Installable = true; c.Recommended = true;
                WingetProvider.ResolveUnknown(c, w, progs);
                Check(c.Status == UpdateStatus.AlDia && !c.Installable && c.InstalledVersion == "7.23.0" && c.Detail.Contains("ejecutable"), "al día por el ejecutable: " + c.Detail);

                WingetProvider.ExeVersionReader = delegate(string path) { return "7.24.1"; };
                c = new UpdateCandidate(); c.Status = UpdateStatus.Disponible; c.Installable = true; c.Recommended = true;
                WingetProvider.ResolveUnknown(c, w, progs);
                Check(c.Status == UpdateStatus.AlDia && c.Detail.Contains("más nueva"), "más nueva que el catálogo: no se ofrece: " + c.Detail);

                WingetProvider.ExeVersionReader = delegate(string path) { return "7.13.0"; };
                c = new UpdateCandidate(); c.Status = UpdateStatus.Disponible; c.Installable = true; c.Recommended = true;
                WingetProvider.ResolveUnknown(c, w, progs);
                Check(c.Status == UpdateStatus.Disponible && c.Recommended && c.InstalledVersion == "7.13.0", "más vieja: se ofrece y se marca");

                // Otra numeración (versión mayor distinta): no se usa; se ofrece sin marcar.
                WingetProvider.ExeVersionReader = delegate(string path) { return "20.3.3"; };
                c = new UpdateCandidate(); c.Status = UpdateStatus.Disponible; c.Installable = true; c.Recommended = true;
                WingetProvider.ResolveUnknown(c, w, progs);
                Check(c.Status == UpdateStatus.Disponible && !c.Recommended && c.Installable && c.Detail.Contains("no se marca sola"), "sin datos confiables: sin premarcar: " + c.Detail);

                // El registro manda sobre el ejecutable.
                var conVersion = WinRar32(); conVersion.Version = "7.20.0";
                c = new UpdateCandidate(); c.Status = UpdateStatus.Disponible; c.Installable = true; c.Recommended = true;
                WingetProvider.ResolveUnknown(c, w, new List<InstalledProgram> { conVersion });
                Check(c.Status == UpdateStatus.Disponible && c.InstalledVersion == "7.20.0" && c.Detail.Contains("registro"), "versión del registro: " + c.Detail);
            }
            finally { WingetProvider.ExeVersionReader = anterior; }

            string inst, src;
            Eq(ProgramRules.UnknownVerdict.SinDatos, ProgramRules.ResolveUnknownVersion("Unknown", "1.0", "", out inst, out src), "ofrecida ilegible");
            Eq(ProgramRules.UnknownVerdict.AlDia, ProgramRules.ResolveUnknownVersion("7.23.0", "", "7.23.0.0", out inst, out src), "7.23.0.0 = 7.23.0");
            Eq("", ProgramRules.MainExe(Registro(Uninst + "x", "X", "", "", "", "", null, @"C:\Program Files\X\uninstall.exe", false)), "el desinstalador no es el ejecutable principal");
            Eq(@"C:\Program Files\X\x.exe", ProgramRules.MainExe(Registro(Uninst + "x", "X", "", "", "", "", null, "\"C:\\Program Files\\X\\x.exe\",0", false)), "ícono con índice");
        }

        static void TestArquitecturaYMensajes153()
        {
            Check(ProgramRules.CanSwitchArchitecture(EpicMsi()), "MSI: se puede pasar a la arquitectura que elija winget");
            Check(!ProgramRules.CanSwitchArchitecture(WinRar32()), "EXE: no");
            Check(!ProgramRules.CanSwitchArchitecture(null), "sin datos: no");
            string t = ProgramRules.ArchitectureBlockedText("7.23.0", "x86", "x64", "RARLab.WinRAR");
            Check(t.Contains("7.23.0") && t.Contains("32 bits") && t.Contains("64 bits") && t.Contains("winget install --id RARLab.WinRAR"), "texto: " + t);

            var c = new UpdateCandidate(); c.Name = "Futuremark SystemInfo";
            var r = Salida(WingetProvider.InstallerFailed, "Error del instalador con el código de salida: 1603\n");
            WingetProvider.ApplyResult(c, r, "", 1, null, true, "No se pudo quitar la versión anterior (error 1714)", null);
            Check(c.Result.Contains("Según el registro del instalador: No se pudo quitar") && c.Result.Contains("1603"), "1603 con la causa del registro: " + c.Result);
            WingetProvider.ApplyResult(c, r, "", 1, null, true, null, "EpicGamesLauncher.exe");
            Check(c.Result.Contains("Estaba abierto mientras se instalaba (EpicGamesLauncher.exe)"), "1603 con el programa abierto: " + c.Result);
            var detenido = new ProcResult(); detenido.Started = true; detenido.Aborted = true; detenido.AbortReason = "winget iba a instalar la 7.01.0";
            WingetProvider.ApplyResult(c, detenido, "", 1, null, false);
            Check(c.Status == UpdateStatus.Fallida && c.Result.StartsWith("No se instaló nada: winget iba a instalar la 7.01.0"), "detenido: " + c.Result);
            Check(detenido.Describe().StartsWith("Detenido"), "Describe de un proceso detenido");
            Check(VendorProviderTexts(), "texto de 1603 en instaladores de fabricantes");
        }

        static bool VendorProviderTexts()
        {
            // ExitText es privado: se verifica a través del texto fijo que usa para 1603.
            return WingetProvider.InstallerCodeHint("1603", true).Contains("segundo plano");
        }
        const string PnpSynaptics = "Utilidad PnP de Microsoft    Agregando paquete de controladores:  SynAMDSmbDrv.inf  El paquete de controladores se agregó correctamente.  Nombre publicado:         oem17.inf    Agregando paquete de controladores:  SynHidMini.inf  El paquete de controladores se agregó correctamente.  Nombre publicado:         oem18.inf    Agregando paquete de controladores:  SynPD.inf  El paquete de controladores se agregó correctamente.  Nombre publicado:         oem19.inf  El paquete de controladores se agregó correctamente.  Nombre publicado:         oem22.inf  El paquete de controladores se agregó correctamente.  Nombre publicado:         oem21.inf  El paquete de controladores se agregó correctamente.  Nombre publicado:         oem20.inf  Paquete de controladores instalado en el dispositivo: PCI\\VEN_8086&DEV_9C22&SUBSYS_018A1B0A&REV_04\\3&11583659&0&FB    Agregando paquete de controladores:  SynRMIHID.inf  El paquete de controladores se agregó correctamente. (ya existe en el sistema)  Nombre publicado:         oem20.inf    Agregando paquete de controladores:  SynService.inf  El paquete de controladores se agregó correctamente. (ya existe en el sistema)  Nombre publicado:         oem22.inf    Agregando paquete de controladores:  SynSmbDrv.inf  El paquete de controladores se agregó correctamente. (ya existe en el sistema)  Nombre publicado:         oem21.inf  El paquete de controladores está actualizado en el dispositivo: PCI\\VEN_8086&DEV_9C22&SUBSYS_018A1B0A&REV_04\\3&11583659&0&FB    Paquetes de controladores totales:  6  Paquetes de controladores agregados:  5";
        const string SmbusDevice = @"PCI\VEN_8086&DEV_9C22&SUBSYS_018A1B0A&REV_04\3&11583659&0&FB";

        static void TestReinicioDeLaAplicacion()
        {
            // App Installer: reiniciar la APLICACIÓN no es reiniciar el equipo.
            Check(!WingetProvider.MentionsMachineRestart("Successfully installed. Restart the application to complete the upgrade."), "reiniciar la aplicación");
            Check(!WingetProvider.MentionsMachineRestart("Instalado correctamente. Reinicie la aplicación para completar la actualización."), "reiniciar la aplicación (español)");
            Check(!WingetProvider.MentionsMachineRestart(""), "vacío");
            Check(!WingetProvider.MentionsMachineRestart(null), "null");
            Check(!WingetProvider.MentionsMachineRestart("Restarting service AnyDesk"), "reinicio de un servicio");
            Check(WingetProvider.MentionsMachineRestart("Successfully installed. Restart your PC to finish installation."), "restart your PC");
            Check(WingetProvider.MentionsMachineRestart("A system restart is required"), "system restart is required");
            Check(WingetProvider.MentionsMachineRestart("Reboot the computer to complete"), "reboot the computer");
            Check(WingetProvider.MentionsMachineRestart("Instalado correctamente. Reinicie el equipo para terminar la instalación."), "reinicie el equipo");
            Check(WingetProvider.MentionsMachineRestart("Es necesario reiniciar el sistema"), "es necesario reiniciar el sistema");

            var c = FilaWinget("App Installer", "Microsoft.AppInstaller", "1.29.379", "1.29.380");
            WingetProvider.ApplyResult(c, Salida(0, "Successfully installed. Restart the application to complete the upgrade."), "", 1, null);
            Check(c.Status == UpdateStatus.Instalada && c.Result == "Actualizada correctamente", "App Installer: actualizada, sin reiniciar el equipo: " + c.Result);
            c = FilaWinget("X", "X.X", "1.0", "2.0");
            WingetProvider.ApplyResult(c, Salida(0, "Successfully installed. Restart your PC to finish installation."), "", 1, null);
            Check(c.Status == UpdateStatus.RequiereReinicio, "reinicio del equipo: requiere reiniciar");
        }

        static void TestAppInstallerAlFinal()
        {
            Check(WingetProvider.IsWingetItself("Microsoft.AppInstaller") && WingetProvider.IsWingetItself("microsoft.appinstaller") && WingetProvider.IsWingetItself("9NBLGGH4NNS1"), "App Installer (winget y msstore)");
            Check(!WingetProvider.IsWingetItself("Microsoft.AppInstallerFileBuilder") && !WingetProvider.IsWingetItself(null) && !WingetProvider.IsWingetItself(""), "nada más");

            var zip = FilaWinget("7-Zip", "7zip.7zip", "26.00", "26.03");
            var app = FilaWinget("App Installer", "Microsoft.AppInstaller", "1.29.379", "1.29.380");
            var vlc = FilaWinget("VLC media player", "VideoLAN.VLC", "3.0.21", "3.0.24");
            var otra = new UpdateCandidate(); otra.Name = "sin fila de winget";
            var orden = WingetProvider.InstallOrder(new List<UpdateCandidate> { zip, app, vlc, otra });
            Check(orden.Count == 4 && orden[0] == zip && orden[1] == vlc && orden[2] == otra && orden[3] == app, "App Installer al final, el resto en su orden");

            // Flujo simulado: el orden en que se llama a winget.
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            var llamadas = new List<string>();
            wp.RunForTests = delegate(string args)
            {
                llamadas.Add(args);
                if (args.Contains("Microsoft.AppInstaller")) return Salida(0, "Found App Installer [Microsoft.AppInstaller] Version 1.29.380\nSuccessfully installed. Restart the application to complete the upgrade.\n");
                return Salida(0, "Successfully installed\n");
            };
            wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { zip, app, vlc }, delegate(UpdateCandidate x) { });
            Check(llamadas.Count == 3 && llamadas[2].Contains("\"Microsoft.AppInstaller\""), "winget se actualiza a sí mismo al final: " + string.Join(" | ", llamadas.ToArray()));
            Check(zip.Status == UpdateStatus.Instalada && vlc.Status == UpdateStatus.Instalada, "los demás se instalaron");
            Check(app.Status == UpdateStatus.Instalada, "App Installer: «Actualizada», no «requiere reiniciar»: " + app.Status);
        }

        // ------------------------------------------------------------------ Auditoría 1.6.0: winget

        static void TestAuditoriaWinget()
        {
            // Encabezados de varias palabras (coreano): antes no se leía ninguna fila.
            var up = WingetTable.Parse(Sample("winget_upgrade_ko.txt"));
            Eq(3, up.Count, "coreano: las 3 filas de «upgrade» (sin el pie)");
            var chrome = Find(up, "Google.Chrome");
            Check(chrome != null && chrome.Version == "130.0.6723.70" && chrome.Available == "131.0.6778.86" && chrome.Source == "winget", "coreano: Chrome completo");
            var npp = Find(up, "Notepad++.Notepad++");
            Check(npp != null && npp.Name == "메모장++" && npp.Available == "8.7.1", "coreano: nombre con caracteres anchos");
            var list = WingetTable.Parse(Sample("winget_list_ko.txt"));
            Eq(3, list.Count, "coreano: las 3 filas de «list»");
            var c2 = Find(list, "Google.Chrome");
            var git = Find(list, "Git.Git");
            Check(c2 != null && c2.Available == "131.0" && c2.Source == "winget", "coreano list: disponible corta y origen en su lugar");
            Check(git != null && git.Available == "" && git.Source == "winget" && git.Version == "2.47.1", "coreano list: sin disponible");

            // Pies que winget imprime pegados a la tabla: con una sola fila no pueden unir columnas.
            const string pies = "Name           Id             Version       Available     Source\r\n" +
                                "------------------------------------------------------------------\r\n" +
                                "Microsoft Edge Microsoft.Edge 128.0.2739.67 129.0.2792.52 winget\r\n" +
                                "1 upgrades available.\r\n" +
                                "1 package(s) have pins that prevent upgrade. Use the 'winget pin' command to view and edit pins.\r\n";
            var edge = WingetTable.Parse(pies);
            Check(edge.Count == 1 && edge[0].Id == "Microsoft.Edge" && edge[0].Name == "Microsoft Edge" && edge[0].Available == "129.0.2792.52" && edge[0].Source == "winget",
                  "una fila y dos pies: columnas intactas (" + (edge.Count > 0 ? edge[0].Name + "|" + edge[0].Id : "sin filas") + ")");
            const string piesEs = "Nombre  Id         Versión Disponible Origen\r\n" +
                                  "---------------------------------------------\r\n" +
                                  "7-Zip   7zip.7zip  24.08   24.09      winget\r\n" +
                                  "1 actualizaciones disponibles.\r\n" +
                                  "1 paquetes tienen números de versión que no se pueden determinar. Use --include-unknown para ver todos los resultados.\r\n";
            var zip = WingetTable.Parse(piesEs);
            Check(zip.Count == 1 && zip[0].Id == "7zip.7zip" && zip[0].Available == "24.09" && zip[0].Source == "winget", "lo mismo en español");
            var ko1 = WingetTable.Parse("이름                  장치 ID                 버전           사용 가능      원본\r\n" +
                                        "----------------------------------------------------------------------------------\r\n" +
                                        "Google Chrome         Google.Chrome           130.0.6723.70  131.0.6778.86  winget\r\n" +
                                        "1 업그레이드를 사용할 수 있습니다.\r\n");
            Check(ko1.Count == 1 && ko1[0].Id == "Google.Chrome" && ko1[0].Available == "131.0.6778.86" && ko1[0].Source == "winget", "coreano con una sola fila y su pie");

            // Un origen con dígitos no es una versión.
            var repo = WingetTable.Parse("Name      Id           Version Source\r\n---------------------------------------\r\nMi App    Empresa.App  1.2.3   repo2\r\n");
            Check(repo.Count == 1 && repo[0].Source == "repo2" && repo[0].Available == "", "4 columnas: «repo2» es el origen");
            var avail = WingetTable.Parse("Name      Id           Version Available\r\n------------------------------------------\r\nMi App    Empresa.App  1.2.3   1.3.0\r\n");
            Check(avail.Count == 1 && avail[0].Available == "1.3.0" && avail[0].Source == "", "4 columnas: versión disponible");
            // El inglés de siempre sigue igual.
            Eq(7, WingetTable.Parse(Sample("winget_upgrade_en.txt")).Count, "inglés: sin cambios");

            // Versiones recortadas con «…».
            Check(WingetTable.IsTruncated("10.0.26100.1…") && WingetTable.IsTruncated("1.2...") && !WingetTable.IsTruncated("1.2.3") && !WingetTable.IsTruncated(null), "recortes");
            var w = new WingetRow(); w.Name = "SDK"; w.Id = "Microsoft.WindowsSDK"; w.Version = "10.0.26100.150"; w.Available = "10.0.26100.1…"; w.Source = "winget";
            var c = WingetProvider.FromListRow(w, new UpdateCandidate());
            Check(c.Status == UpdateStatus.Disponible && !c.Recommended, "list con disponible recortada: hay actualización (antes «Al día»)");
            string inst, src;
            Eq(ProgramRules.UnknownVerdict.SinDatos, ProgramRules.ResolveUnknownVersion("15.82…", "15.81.5", "", out inst, out src), "ofrecida recortada: no se compara");

            // Qué programa del registro corresponde a la fila.
            var a32 = Registro(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\App", "Mi App", "1.0", "Empresa", @"C:\Program Files (x86)\App", "", null, null, false);
            a32.Architecture = "x86";
            var a64 = Registro(Uninst + "App", "Mi App", "2.0", "Empresa", @"C:\Program Files\App", "", null, null, false);
            var progs = new List<InstalledProgram> { a32, a64 };
            Check(WingetProvider.FindProgram(progs, "Mi App", "2.0") == a64, "dos copias: elige por la versión de winget");
            Check(WingetProvider.FindProgram(progs, "Mi App", "1.0") == a32, "dos copias: la otra versión");
            Check(WingetProvider.FindProgram(progs, "Mi App", "Unknown") == null, "dos copias sin versión: no se adivina");
            Check(WingetProvider.FindProgram(new List<InstalledProgram> { a64 }, "Mi App", null) == a64, "una sola: esa");
            var otro = Registro(Uninst + "App2", "Mi App Pro", "5.0", "Empresa", "", "", null, null, false);
            Check(WingetProvider.FindProgram(new List<InstalledProgram> { a64, otro }, "Mi App…", null) == null, "nombre recortado con dos programas posibles: null");
            Check(WingetProvider.FindProgram(new List<InstalledProgram> { a64, otro }, "Mi App…", "5.0") == otro, "recortado: la versión desempata");

            // WiX Burn: el ícono es el instalador guardado (32 bits) y no dice la arquitectura.
            var vc = Registro(Uninst + "{vc}", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "14.40.33810.0", "Microsoft Corporation", "",
                              "\"C:\\ProgramData\\Package Cache\\{0b5169e3}\\VC_redist.x64.exe\" /uninstall", null, "C:\\ProgramData\\Package Cache\\{0b5169e3}\\VC_redist.x64.exe,0", false);
            Eq("", ProgramRules.DetectArchitecture(vc, "x64", delegate(string p) { return "x86"; }), "Package Cache: no se usa el PE del instalador");
            var app = Registro(Uninst + "app", "App", "1.0", "X", @"C:\Program Files\App", "", null, "C:\\Program Files\\App\\app.exe,0", false);
            Eq("x64", ProgramRules.DetectArchitecture(app, "x64", delegate(string p) { return "x64"; }), "un programa común: el PE de su ejecutable");
        }
        /// <summary>
        /// winget-cli (InstallFlow.cpp, ReportInstallerResult): REBOOT_REQUIRED_TO_FINISH (0x8A150109) es
        /// éxito con reinicio; REBOOT_REQUIRED_FOR_INSTALL (0x8A15010A) es un ERROR porque la instalación no
        /// se completó. Actualizador mostraba los dos como «Actualizada (requiere reinicio)».
        /// </summary>
        static void TestReinicioAntesDeInstalar()
        {
            Eq(0x8A150109u, WingetProvider.RebootToFinish, "constante REBOOT_REQUIRED_TO_FINISH");
            Eq(0x8A15010Au, WingetProvider.RebootBeforeInstall, "constante REBOOT_REQUIRED_FOR_INSTALL");
            Eq(0x8A15010Bu, WingetProvider.RebootInitiated, "constante REBOOT_INITIATED");

            var c = FilaWinget("Programa X", "Editor.ProgramaX", "1.0", "2.0");
            WingetProvider.ApplyResult(c, Salida(0x8A15010A, "Your PC will restart to finish installation."), "Your PC will restart to finish installation.", 1, null);
            Eq(UpdateStatus.Fallida, c.Status, "0x8A15010A: NO se instaló (antes: «Actualizada, requiere reinicio»)");
            Check(c.RestartBeforeRetry, "0x8A15010A: se marca que hay que reiniciar antes de reintentar");
            Check(c.Result.Contains("No se instaló") && c.Result.Contains("Reiniciá"), "0x8A15010A: lo dice: " + c.Result);
            Eq("1.0", c.InstalledVersion, "0x8A15010A: la versión instalada sigue siendo la anterior");

            c = FilaWinget("Programa X", "Editor.ProgramaX", "1.0", "2.0");
            WingetProvider.ApplyResult(c, Salida(0x8A150109, "Restart your PC to finish installation."), "", 1, null);
            Eq(UpdateStatus.RequiereReinicio, c.Status, "0x8A150109: instalada, falta reiniciar");
            Check(!c.RestartBeforeRetry, "0x8A150109: no pide reiniciar antes");

            c = FilaWinget("Programa X", "Editor.ProgramaX", "1.0", "2.0");
            WingetProvider.ApplyResult(c, Salida(0x8A15010B, ""), "", 1, null);
            Eq(UpdateStatus.RequiereReinicio, c.Status, "0x8A15010B (MSI 1641): instalada, el instalador inició el reinicio");

            Check(WingetProvider.Describe(0x8A15010A).Contains("antes de instalar"), "descripción de 0x8A15010A");
            Check(WingetProvider.Describe(0x8A150109).Contains("reiniciar"), "descripción de 0x8A150109");
            Check(WingetProvider.Describe(0x8A15010B).Contains("reinicio"), "descripción de 0x8A15010B");

            // La vía directa: «rebootRequiredForInstall» de ExpectedReturnCodes tampoco es éxito.
            var m = WingetManifest.Parse("PackageIdentifier: A.B\nPackageVersion: 1\nInstallers:\n- Architecture: x64\n  InstallerType: inno\n  InstallerUrl: https://a.b/c.exe\n  InstallerSha256: AA\n" +
                                         "  ExpectedReturnCodes:\n  - InstallerReturnCode: 77\n    ReturnResponse: rebootRequiredForInstall\n  - InstallerReturnCode: 78\n    ReturnResponse: rebootRequiredToFinish\n");
            var antes = WingetRules.InterpretExit(m.Installers[0], 77);
            Check(!antes.Success && antes.RestartFirst && antes.Message.Contains("reiniciar"), "rebootRequiredForInstall: falla y pide reiniciar antes");
            var despues = WingetRules.InterpretExit(m.Installers[0], 78);
            Check(despues.Success && despues.Reboot && !despues.RestartFirst, "rebootRequiredToFinish: éxito con reinicio");

            // Flujo simulado: la fila queda con error y las demás siguen.
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            wp.RunForTests = delegate(string args)
            {
                if (args.Contains("Editor.ProgramaX")) return Salida(0x8A15010A, "Found Programa X [Editor.ProgramaX] Version 2.0\nYour PC will restart to finish installation.\n");
                return Salida(0, "Found Otro [Editor.Otro] Version 3.0\nSuccessfully installed\n");
            };
            var x = FilaWinget("Programa X", "Editor.ProgramaX", "1.0", "2.0");
            var otro = FilaWinget("Otro", "Editor.Otro", "2.0", "3.0");
            wp.Install(ContextoSimulado(true), new List<UpdateCandidate> { x, otro }, delegate(UpdateCandidate u) { });
            Check(x.Status == UpdateStatus.Fallida && x.RestartBeforeRetry, "flujo: la fila que pide reiniciar antes queda con error");
            Eq(UpdateStatus.Instalada, otro.Status, "flujo: la siguiente se instala igual");
        }
        static readonly string[] Encabezado = { "Nombre", "Id", "Versión", "Disponible", "Origen" };

        static InstalledProgram LibreOfficeMsi(string version)
        {
            var p = Registro(Uninst + "{4B17E523-5D91-4E69-BD96-7FD81CFA81BB}", "LibreOffice " + version, version, "The Document Foundation",
                             @"C:\Program Files\LibreOffice\", "MsiExec.exe /I{4B17E523-5D91-4E69-BD96-7FD81CFA81BB}", null, @"C:\Program Files\LibreOffice\program\soffice.exe", true);
            return p;
        }

        static InstalledProgram TeraBoxUsuario()
        {
            var p = Registro(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TeraBox", "TeraBox", "1.47.0", "Flextech Inc.",
                             @"C:\Users\Fede\AppData\Roaming\TeraBox", "\"C:\\Users\\Fede\\AppData\\Roaming\\TeraBox\\uninst.exe\"", null,
                             @"C:\Users\Fede\AppData\Roaming\TeraBox\TeraBox.exe", false);
            p.Scope = "Usuario actual";
            p.Architecture = "x86";
            return p;
        }

        // ------------------------------------------------------------------ Identificadores recortados

        static void TestIdentificadoresRecortados()
        {
            Eq("TheDocumentFoundation.LibreOf", WingetProvider.TruncatedPrefix("TheDocumentFoundation.LibreOf…"), "prefijo de un Id recortado");
            Eq("Microsoft.VCRedist.2015+.x", WingetProvider.TruncatedPrefix("Microsoft.VCRedist.2015+.x..."), "también con «...»");
            Eq(null, WingetProvider.TruncatedPrefix("ab…"), "demasiado corto para buscar");
            Eq(null, WingetProvider.TruncatedPrefix(@"ARP\Machine\X64\Pro…"), "los Id locales no se buscan");
            Eq(null, WingetProvider.TruncatedPrefix("Ed\"itor.Paq…"), "caracteres raros: no se pasa a winget");

            // La consulta del paquete solo devuelve las dos filas de LibreOffice (programa y ayuda): cada
            // fila recortada se completa con la suya, por nombre y versión.
            string unaFila = TablaWinget(Encabezado,
                new[] { "LibreOffice 26.2.5.2", "TheDocumentFoundation.LibreOffice", "26.2.5.2", "26.8.1.1", "winget" },
                new[] { "LibreOffice 26.2 Help Pack (Spanish)", "TheDocumentFoundation.LibreOffice.HelpPack", "26.2.5.2", "26.8.0.3", "winget" });
            var lo = new WingetRow { Name = "LibreOffice 26.2.5.2", Id = "TheDocumentFoundation.LibreOf…", Version = "26.2.5.2", Available = "26.8.1.1", Source = "winget", IdTruncated = true };
            Check(WingetProvider.CompleteFromListing(lo, unaFila), "LibreOffice: Id completado");
            Eq("TheDocumentFoundation.LibreOffice", lo.Id, "Id completo de LibreOffice");
            Check(!lo.IdTruncated, "ya no figura recortado");
            var hp = new WingetRow { Name = "LibreOffice 26.2 Help Pack (Spa…", Id = "TheDocumentFoundation.LibreOf…", Version = "26.2.5.2", Available = "26.8.0.3", Source = "winget", IdTruncated = true };
            Check(WingetProvider.CompleteFromListing(hp, unaFila), "paquete de ayuda: Id completado");
            Eq("TheDocumentFoundation.LibreOffice.HelpPack", hp.Id, "Id del paquete de ayuda");
            Eq("LibreOffice 26.2 Help Pack (Spanish)", hp.Name, "y su nombre completo");
            var ambigua = new WingetRow { Name = "LibreOffice…", Id = "TheDocumentFoundation.LibreOf…", Version = "26.2.5.2", Available = "26.8.1.1", Source = "winget", IdTruncated = true };
            Check(!WingetProvider.CompleteFromListing(ambigua, unaFila), "nombre recortado que encaja con dos paquetes: no se adivina");
            Check(ambigua.IdTruncated, "…y queda como estaba (no instalable)");
            var otraVersion = new WingetRow { Name = "LibreOffice 26.2.5.2", Id = "TheDocumentFoundation.LibreOf…", Version = "7.6.7.2", Available = "26.8.1.1", Source = "winget", IdTruncated = true };
            Check(!WingetProvider.CompleteFromListing(otraVersion, unaFila), "otra versión instalada: no es la misma fila");
            var dispRecortada = new WingetRow { Name = "Microsoft Edge WebView2 Runtime", Id = "Microsoft.EdgeWebView2Runtime", Version = "129.0.2792.65", Available = "130.0.2849.…", Source = "winget" };
            string webview = TablaWinget(Encabezado, new[] { "Microsoft Edge WebView2 Runtime", "Microsoft.EdgeWebView2Runtime", "129.0.2792.65", "130.0.2849.46", "winget" });
            Check(WingetProvider.CompleteFromListing(dispRecortada, webview) && dispRecortada.Available == "130.0.2849.46", "versión disponible recortada: completada (" + dispRecortada.Available + ")");

            // Búsqueda completa con winget 1.6 simulado (tabla recortada a 120 columnas).
            string upgrade = TablaWinget(Encabezado,
                new[] { "LibreOffice 26.2.5.2", "TheDocumentFoundation.LibreOf…", "26.2.5.2", "26.8.1.1", "winget" },
                new[] { "LibreOffice 26.2 Help Pack (Spa…", "TheDocumentFoundation.LibreOf…", "26.2.5.2", "26.8.0.3", "winget" },
                new[] { "TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5", "winget" }) + "3 actualizaciones disponibles.\n";
            // «winget list» también recortado (con otro ancho: tiene más filas, como los Id locales «ARP\…»).
            string list = TablaWinget(Encabezado,
                new[] { "LibreOffice 26.2.5.2", "TheDocumentFoundation.Libr…", "26.2.5.2", "26.8.1.1", "winget" },
                new[] { "LibreOffice 26.2 Help Pack (S…", "TheDocumentFoundation.Libr…", "26.2.5.2", "26.8.0.3", "winget" },
                new[] { "Programa local", @"ARP\Machine\X64\{6F1C0D2E-1234-…", "1.0", "", "" },
                new[] { "TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5", "winget" });
            var consultas = new List<string>();
            var wp = new WingetProvider();
            wp.SimulateVersion("1.6.3482");
            wp.QueryForTests = delegate(string args)
            {
                consultas.Add(args);
                if (args.StartsWith("upgrade")) return Salida(0, upgrade);
                if (args.StartsWith("list --id \"TheDocumentFoundation.LibreOf\"")) return Salida(0, unaFila);
                if (args.StartsWith("list")) return Salida(0, list);
                return Salida(0x8A150014, "");
            };
            var ctx = ContextoSimulado(true, LibreOfficeMsi("26.2.5.2"), TeraBoxUsuario());
            var salida = new List<UpdateCandidate>();
            var notas = new List<string>();
            wp.Scan(ctx, salida, notas);
            var filaLo = salida.Find(delegate(UpdateCandidate x) { return x.Name == "LibreOffice 26.2.5.2"; });
            var filaHp = salida.Find(delegate(UpdateCandidate x) { return x.Name.StartsWith("LibreOffice 26.2 Help Pack"); });
            Check(filaLo != null && filaLo.Identifier == "TheDocumentFoundation.LibreOffice" && filaLo.Installable && filaLo.Status == UpdateStatus.Disponible,
                  "LibreOffice se puede instalar (antes: «winget recortó el identificador»): " + (filaLo != null ? filaLo.Identifier + " " + filaLo.Detail : "sin fila"));
            Check(filaHp != null && filaHp.Identifier == "TheDocumentFoundation.LibreOffice.HelpPack" && filaHp.Installable, "el paquete de ayuda también");
            Eq(1, consultas.FindAll(delegate(string a) { return a.StartsWith("list --id"); }).Count, "una sola consulta para los dos (mismo prefijo)");
            Check(consultas.Exists(delegate(string a) { return a.Contains("list --id \"TheDocumentFoundation.LibreOf\" --source \"winget\""); }), "consulta por el prefijo y el origen");
            Eq(1, salida.FindAll(delegate(UpdateCandidate x) { return x.Identifier == "TheDocumentFoundation.LibreOffice"; }).Count, "sin filas repetidas de «winget list»");
            Check(!salida.Exists(delegate(UpdateCandidate x) { return (x.Detail ?? "").Contains("recortó el identificador"); }), "ninguna fila queda como «recortada»");
        }

        static void TestMsstoreQueNoResponde()
        {
            // El origen msstore no responde (proxy o filtro): antes se perdía la lista entera de programas.
            string[] cuatro = { "Nombre", "Id", "Versión", "Disponible" };
            string upgradeWinget = TablaWinget(cuatro, new[] { "TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5" }) + "1 actualizaciones disponibles.\n";
            string listWinget = TablaWinget(cuatro, new[] { "TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5" }, new[] { "Git", "Git.Git", "2.51.0", "" });
            var consultas = new List<string>();
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            wp.QueryForTests = delegate(string args)
            {
                consultas.Add(args);
                if (!args.Contains("--source winget")) { var t = new ProcResult(); t.Started = true; t.TimedOut = true; return t; }
                return Salida(0, args.StartsWith("upgrade") ? upgradeWinget : listWinget);
            };
            var ctx = ContextoSimulado(true, TeraBoxUsuario());
            var salida = new List<UpdateCandidate>();
            var notas = new List<string>();
            wp.Scan(ctx, salida, notas);
            var tb = salida.Find(delegate(UpdateCandidate x) { return x.Identifier == "Baidu.TeraBox"; });
            Check(tb != null && tb.Status == UpdateStatus.Disponible && tb.Installable && tb.Source == "winget (winget)", "TeraBox sigue apareciendo y se puede instalar: " + (tb != null ? tb.Source : "sin fila"));
            var git = salida.Find(delegate(UpdateCandidate x) { return x.Identifier == "Git.Git"; });
            Check(git != null && git.Status == UpdateStatus.AlDia, "lo que está al día figura al día (no «no figura en winget»)");
            Check(notas.Exists(delegate(string n) { return n.Contains("msstore"); }), "se avisa que msstore no respondió");
            Check(consultas.Exists(delegate(string a) { return a.StartsWith("list --source winget"); }), "«winget list» también sólo con winget");
        }

        // ------------------------------------------------------------------ Orden: el programa antes que sus complementos

        static void TestOrdenComplementos()
        {
            var ayuda = FilaWinget("LibreOffice 26.2 Help Pack (Spanish)", "TheDocumentFoundation.LibreOffice.HelpPack", "26.2.5.2", "26.8.0.3");
            var lo = FilaWinget("LibreOffice 26.2.5.2", "TheDocumentFoundation.LibreOffice", "26.2.5.2", "26.8.1.1");
            var appInstaller = FilaWinget("App Installer", "Microsoft.AppInstaller", "1.26.430.0", "1.27.460.0");
            var tera = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
            var orden = WingetProvider.InstallOrder(new List<UpdateCandidate> { appInstaller, ayuda, tera, lo });
            Eq(4, orden.Count, "los cuatro");
            Check(orden.IndexOf(lo) < orden.IndexOf(ayuda), "LibreOffice antes que su paquete de ayuda (antes, al revés: el MSI de ayuda fallaba)");
            Eq(appInstaller, orden[3], "App Installer sigue al final");
            Eq(tera, orden[0], "lo demás conserva su orden");
            var solo = WingetProvider.InstallOrder(new List<UpdateCandidate> { ayuda });
            Check(solo.Count == 1 && solo[0] == ayuda, "el paquete de ayuda solo");
            // «Microsoft.VCRedist.2015+.x64» no es complemento de «Microsoft.VCRedist.2015+.x86» (no empieza con «…x86.»).
            var x86 = FilaWinget("VC x86", "Microsoft.VCRedist.2015+.x86", "14.40", "14.44");
            var x64 = FilaWinget("VC x64", "Microsoft.VCRedist.2015+.x64", "14.40", "14.44");
            var o2 = WingetProvider.InstallOrder(new List<UpdateCandidate> { x86, x64 });
            Check(o2[0] == x86 && o2[1] == x64, "Id hermanos: el orden no cambia");
        }

        // ------------------------------------------------------------------ Alcance: TeraBox y LibreOffice

        static void TestAlcanceDelInstalador()
        {
            var tera = WingetManifest.Parse(Sample("winget_manifest_terabox.yaml"));
            Eq("Baidu.TeraBox", tera.Id, "manifiesto real de TeraBox");
            Eq("machine", tera.Installers[0].Scope, "TeraBox se publica «para el equipo»");
            Eq("nullsoft", tera.Installers[0].Type, "…con un instalador NSIS");
            string why;
            bool other;
            // Como antes (hash viejo, cortes de red): el otro alcance no se usa.
            Check(WingetRules.Choose(tera, null, "x64", "x86", false, false, out why) == null, "sin flexibilidad: TeraBox del usuario no tiene instalador");
            Check(why.Contains("sólo para tu usuario"), "y se explica por qué: " + why);
            // winget dijo «no aplicable»: NSIS decide solo dónde instalar → se acepta, marcado.
            var inst = WingetRules.Choose(tera, null, "x64", "x86", false, false, true, "10.0.26100.0", out why, out other);
            Check(inst != null && other && inst.Architecture == "x86", "TeraBox del usuario: el instalador NSIS oficial, con el otro alcance");
            // Instalado para el equipo: el mismo alcance, sin «otro».
            inst = WingetRules.Choose(tera, null, "x64", "x86", true, false, true, "10.0.26100.0", out why, out other);
            Check(inst != null && !other, "TeraBox del equipo: mismo alcance (winget sabe algo más, no se le pasa por encima)");

            var lo = WingetManifest.Parse(Sample("winget_manifest_libreoffice.yaml"));
            Eq(3, lo.Installers.Count, "LibreOffice: x86, x64 y arm64");
            Check(lo.Installers.TrueForAll(delegate(WingetInstaller i) { return i.IsMsi && i.Scope == "machine"; }), "MSI para el equipo");
            inst = WingetRules.Choose(lo, null, "x64", "x64", false, true, true, "10.0.26100.0", out why, out other);
            Check(inst == null && why.Contains("MSI") && why.Contains("dos copias"), "LibreOffice instalado sólo para el usuario: un MSI no se cambia de alcance: " + why);
            inst = WingetRules.Choose(lo, null, "x64", "x64", true, true, true, "10.0.26100.0", out why, out other);
            Check(inst != null && inst.Architecture == "x64" && !other, "LibreOffice del equipo: el MSI de 64 bits");

            // Windows mínimo.
            var m = new WingetManifest();
            m.Id = "Ejemplo.App"; m.Version = "2.0";
            var i11 = new WingetInstaller { Architecture = "x64", Type = "inno", Scope = "machine", Url = "https://ejemplo.com/a.exe", Sha256 = new string('a', 64), MinOs = "10.0.22000.0" };
            m.Installers.Add(i11);
            inst = WingetRules.Choose(m, null, "x64", "x64", true, false, true, "10.0.19045.0", out why, out other);
            Check(inst == null && why.Contains("Windows 11"), "requiere Windows 11 en un Windows 10: " + why);
            inst = WingetRules.Choose(m, null, "x64", "x64", true, false, true, "10.0.26100.0", out why, out other);
            Check(inst != null, "en Windows 11, sí");
            Eq("10 1809", WingetRules.WindowsName("10.0.17763.0"), "nombre de Windows 10 1809");
            Check(WingetRules.ScopeAgnosticType(i11) && !WingetRules.ScopeAgnosticType(lo.Installers[0]), "Inno sí, MSI no");
        }

        static void TestFlujoNoAplicable()
        {
            WingetDirect.ManifestForTests = delegate(string url)
            {
                if (url.EndsWith("/b/Baidu/TeraBox/1.49.5/Baidu.TeraBox.installer.yaml")) return Sample("winget_manifest_terabox.yaml");
                if (url.EndsWith("/TheDocumentFoundation/LibreOffice/26.8.1.1/TheDocumentFoundation.LibreOffice.installer.yaml")) return Sample("winget_manifest_libreoffice.yaml");
                return null;
            };
            try
            {
                var llamadas = new List<string>();
                var wp = new WingetProvider();
                wp.SimulateVersion("1.29.380");
                wp.RunForTests = delegate(string args)
                {
                    llamadas.Add(args);
                    return Salida(WingetProvider.UpdateNotApplicable, "No se encontró ninguna actualización aplicable.");
                };

                // TeraBox para el usuario, SIN administrador: winget no lo actualiza; el instalador oficial es
                // «para el equipo» y necesita administrador. Se dice exactamente eso.
                var c = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
                wp.Install(ContextoSimulado(false, TeraBoxUsuario()), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Eq(2, llamadas.Count, "TeraBox: con --version y sin --version");
                Check(llamadas.Count == 2 && llamadas[0].Contains("--version \"1.49.5\"") && !llamadas[1].Contains("--version"), "el segundo, sin fijar la versión");
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("administrador"), "TeraBox sin administrador: lo dice: " + c.Result);
                Check(!c.Result.Contains("No se encontró ninguna"), "sin repetir la línea de winget");

                // LibreOffice instalado (raro) sólo para el usuario: MSI, no se cambia de alcance.
                llamadas.Clear();
                var loUsuario = LibreOfficeMsi("26.2.5.2");
                loUsuario.RegistryKey = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{4B17E523-5D91-4E69-BD96-7FD81CFA81BB}";
                loUsuario.Scope = "Usuario actual";
                c = FilaWinget("LibreOffice 26.2.5.2", "TheDocumentFoundation.LibreOffice", "26.2.5.2", "26.8.1.1");
                wp.Install(ContextoSimulado(true, loUsuario), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("dos copias") && c.Result.Contains("Desinstalalo"), "LibreOffice del usuario: se explica qué hacer: " + c.Result);

                // LibreOffice del equipo y winget igual dice «no aplicable»: no se le pasa por encima.
                c = FilaWinget("LibreOffice 26.2.5.2", "TheDocumentFoundation.LibreOffice", "26.2.5.2", "26.8.1.1");
                wp.Install(ContextoSimulado(true, LibreOfficeMsi("26.2.5.2")), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("verbose-logs"), "mismo alcance: se indica cómo ver la causa en winget: " + c.Result);

                // Con --version, la otra tecnología de instalación se veía como «no aplicable»: sin --version winget
                // devuelve 0x8A15008E y se llega a la reinstalación verificada (acá no hay desinstalador real).
                llamadas.Clear();
                wp.RunForTests = delegate(string args)
                {
                    llamadas.Add(args);
                    return args.Contains("--version") ? Salida(WingetProvider.UpdateNotApplicable, "No se encontró ninguna actualización aplicable.")
                                                      : Salida(WingetDirect.TechMismatch, "La tecnología de instalación es distinta");
                };
                var dc = Registro(Uninst + "Double Commander_is1", "Double Commander", "1.1.28", "Alexander Koblov",
                                  @"C:\Program Files\Double Commander\", "\"C:\\Program Files\\Double Commander\\unins000.exe\"",
                                  "\"C:\\Program Files\\Double Commander\\unins000.exe\" /SILENT", null, false);
                c = FilaWinget("Double Commander", "alexx2000.DoubleCommander", "1.1.28", "1.1.30");
                wp.Install(ContextoSimulado(true, dc), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("No se pudo reinstalar automáticamente"),
                      "con --version oculto, sin --version se llega a la reinstalación (antes: «no hay actualización aplicable»): " + c.Result);

                // Sin --version winget quiere instalar una versión MÁS VIEJA: se descarta ese intento y la causa es la de antes.
                llamadas.Clear();
                wp.RunForTests = delegate(string args)
                {
                    llamadas.Add(args);
                    return args.Contains("--version") ? Salida(WingetProvider.UpdateNotApplicable, "No aplicable")
                                                      : Salida(0, "Encontrado TeraBox [Baidu.TeraBox] Versión 1.44.3\nInstalado correctamente\n");
                };
                c = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
                wp.Install(ContextoSimulado(false, TeraBoxUsuario()), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.InstalledVersion == "1.47.0", "nunca la 1.44.3: " + c.Result);
            }
            finally { WingetDirect.ManifestForTests = null; }
        }

        // ------------------------------------------------------------------ winget elevado y sin administrador

        static byte[] AliasDeEjecucion(params string[] cadenas)
        {
            var datos = new List<byte>();
            datos.AddRange(BitConverter.GetBytes((uint)3));                       // versión
            foreach (string s in cadenas) { datos.AddRange(Encoding.Unicode.GetBytes(s)); datos.Add(0); datos.Add(0); }
            var buf = new List<byte>();
            buf.AddRange(BitConverter.GetBytes(Native.IO_REPARSE_TAG_APPEXECLINK));
            buf.AddRange(BitConverter.GetBytes((ushort)datos.Count));
            buf.AddRange(new byte[2]);
            buf.AddRange(datos);
            return buf.ToArray();
        }

        static void TestWingetElevadoYSinAdministrador()
        {
            const string pf = @"C:\Program Files";
            string destino = pf + @"\WindowsApps\Microsoft.DesktopAppInstaller_1.27.460.0_x64__8wekyb3d8bbwe\winget.exe";
            byte[] buf = AliasDeEjecucion("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe!winget", destino, "0");
            string[] partes = Native.ParseAppExecLink(buf, buf.Length);
            Check(partes != null && partes.Length >= 3, "alias leído");
            Eq(destino, partes != null && partes.Length > 2 ? partes[2] : null, "destino del alias");
            Check(SoftwareCollectors.IsGenuineWingetLink(partes, pf), "el alias de App Installer se acepta elevado");
            Check(!SoftwareCollectors.IsGenuineWingetLink(new[] { "Malo_123", "x!y", destino }, pf), "otra familia de paquete: no");
            Check(!SoftwareCollectors.IsGenuineWingetLink(new[] { "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "a!b", @"C:\Users\Fede\winget.exe" }, pf), "destino fuera de WindowsApps: no");
            Check(!SoftwareCollectors.IsGenuineWingetLink(new[] { "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "a!b", pf + @"\WindowsApps\Microsoft.DesktopAppInstaller_1_x64__8wekyb3d8bbwe\..\..\x\winget.exe" }, pf), "con «..»: no");
            Check(!SoftwareCollectors.IsGenuineWingetLink(new[] { "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "a!b", pf + @"\WindowsApps\Microsoft.DesktopAppInstaller_1_x64__8wekyb3d8bbwe\sub\winget.exe" }, pf), "otra carpeta adentro: no");
            Check(!SoftwareCollectors.IsGenuineWingetLink(new[] { "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "a!b", pf + @"\WindowsApps\Microsoft.DesktopAppInstaller_1_x64__otroeditor\winget.exe" }, pf), "otro editor: no");
            Check(Native.ParseAppExecLink(new byte[8], 8) == null, "buffer corto: no es un alias");
            byte[] otraEtiqueta = (byte[])buf.Clone(); otraEtiqueta[0] = 0x0C;
            Check(Native.ParseAppExecLink(otraEtiqueta, otraEtiqueta.Length) == null, "otra etiqueta (enlace simbólico): no");

            // Sin administrador: un MSI de equipo en silencio falla sin poder pedir elevación.
            var prog = LibreOfficeMsi("26.2.5.2");
            Check(WingetProvider.NeedsAdminHint(prog, WingetProvider.InstallerFailed, "Error del instalador con el código de salida: 1603"), "1603 de un programa del equipo: pista de administrador");
            Check(WingetProvider.NeedsAdminHint(prog, 0x8A150019, ""), "winget pide administrador");
            Check(!WingetProvider.NeedsAdminHint(TeraBoxUsuario(), WingetProvider.InstallerFailed, "código de salida: 1603"), "programa del usuario: no");
            Check(!WingetProvider.NeedsAdminHint(prog, 0x80072EE2, ""), "corte de red: no");

            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            wp.RunForTests = delegate(string args) { return Salida(WingetProvider.InstallerFailed, "Encontrado LibreOffice [TheDocumentFoundation.LibreOffice] Versión 26.8.1.1\nError del instalador con el código de salida: 1603\n"); };
            var c = FilaWinget("LibreOffice 26.2.5.2", "TheDocumentFoundation.LibreOffice", "26.2.5.2", "26.8.1.1");
            wp.Install(ContextoSimulado(false, prog), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("Ejecutar como administrador"), "sin elevar: se dice que hay que ejecutar como administrador: " + c.Result);
            c = FilaWinget("LibreOffice 26.2.5.2", "TheDocumentFoundation.LibreOffice", "26.2.5.2", "26.8.1.1");
            wp.Install(ContextoSimulado(true, prog), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
            Check(c.Status == UpdateStatus.Fallida && !c.Result.Contains("Ejecutar como administrador"), "elevado: sin esa pista");

            Check(WingetProvider.Describe(WingetProvider.UpdateNotApplicable).Contains("alcance"), "0x8A15002B explica las causas posibles");

            // Antes de instalar, la interfaz ofrece reiniciar como administrador si hay programas del equipo.
            var seleccion = new List<UpdateCandidate> { FilaWinget("LibreOffice 26.2.5.2", "TheDocumentFoundation.LibreOffice", "26.2.5.2", "26.8.1.1"),
                                                        FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5") };
            var equipo = UpdateManager.ProgramsNeedingAdmin(ContextoSimulado(false, prog, TeraBoxUsuario()), seleccion);
            Check(equipo.Count == 1 && equipo[0].Name == "LibreOffice 26.2.5.2", "sin elevar: LibreOffice (del equipo) sí, TeraBox (del usuario) no");
            Eq(0, UpdateManager.ProgramsNeedingAdmin(ContextoSimulado(true, prog, TeraBoxUsuario()), seleccion).Count, "elevado: no se pregunta");
        }
        const string TeraBoxDir = @"C:\Users\Fede\AppData\Roaming\TeraBox";
        const string MullvadDir = @"C:\Users\Fede\AppData\Local\Mullvad\MullvadBrowser\Release";

        /// <summary>TeraBox como lo deja su instalador: clave del equipo (32 bits), archivos en la carpeta del usuario.</summary>
        static InstalledProgram TeraBoxReal(string version)
        {
            var p = Registro(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\TeraBox", "TeraBox", version, "Flextech Inc.",
                             "", TeraBoxDir + @"\uninst.exe", null, TeraBoxDir + @"\TeraBox.exe", false);
            p.Architecture = "x86";
            return p;
        }

        /// <summary>Mullvad Browser 15.0.x como lo deja su instalador (valores leídos del instalador oficial).</summary>
        static InstalledProgram MullvadReal(string version)
        {
            var p = Registro(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MullvadBrowserRelease", "Mullvad Browser", version, "Mullvad VPN",
                             MullvadDir, "\"" + MullvadDir + "\\uninstall.exe\"", "\"" + MullvadDir + "\\uninstall.exe\" /S", "\"" + MullvadDir + "\\mullvadbrowser.exe\"", false);
            p.Scope = "Usuario actual";
            p.Architecture = "x64";
            p.UpdateUrl = "https://github.com/mullvad/mullvad-browser/releases/" + version;
            p.InfoUrl = "https://mullvad.net/en/browser";
            p.HelpLink = "https://mullvad.net/help/tag/browser/";
            return p;
        }

        /// <summary>Simula los archivos del programa: versión de cada ejecutable y contenido de cada archivo chico.</summary>
        static void ArchivosSimulados(Dictionary<string, string> exes, Dictionary<string, string> textos)
        {
            WingetProvider.ExeVersionReader = delegate(string path)
            {
                string v;
                return exes != null && exes.TryGetValue(path, out v) ? v : "";
            };
            WingetProvider.SmallTextReader = delegate(string path)
            {
                string t;
                return textos != null && textos.TryGetValue(path, out t) ? t : null;
            };
        }

        static readonly Func<string, string> LectorExeOriginal = WingetProvider.ExeVersionReader;
        static readonly Func<string, string> LectorTextoOriginal = WingetProvider.SmallTextReader;

        static void SinArchivosSimulados()
        {
            WingetProvider.ExeVersionReader = delegate(string path) { return ""; };
            WingetProvider.SmallTextReader = delegate(string path) { return null; };
        }

        static void LectoresOriginales()
        {
            WingetProvider.ExeVersionReader = LectorExeOriginal;
            WingetProvider.SmallTextReader = LectorTextoOriginal;
        }

        static readonly string[] Cab167 = { "Nombre", "Id", "Versión", "Disponible", "Origen" };

        // ------------------------------------------------------------------ Versión real de los archivos

        static void TestVersionDeLosArchivos()
        {
            // «version.json» del instalador oficial de Mullvad Browser 15.0.24 (tal cual).
            const string mullvadJson = "{\"version\":\"15.0.24\",\"architecture\":\"windows-x86_64\",\"channel\":\"release\"}";
            Eq("15.0.24", ProgramRules.VersionFromJson(mullvadJson), "versión de version.json");
            Eq(null, ProgramRules.VersionFromJson("{\"architecture\":\"windows-x86_64\"}"), "sin versión");
            Eq(null, ProgramRules.VersionFromJson(new string(' ', 5000) + mullvadJson), "archivo grande: no se lee");
            Eq("1.49.5", ProgramRules.Truncate("1.49.5.183", 3), "recorte a 3 componentes");

            string src;
            var mv = MullvadReal("15.0.10");
            try
            {
                // El ejecutable dice 140.17.0 (Firefox ESR): no es la versión del producto. version.json sí.
                ArchivosSimulados(new Dictionary<string, string> { { MullvadDir + @"\mullvadbrowser.exe", "140.17.0" } },
                                  new Dictionary<string, string> { { MullvadDir + @"\version.json", mullvadJson } });
                Eq("15.0.24", WingetProvider.FilesVersion(mv, "15.0.10", out src), "Mullvad: versión de sus archivos");
                Check(src.Contains("version.json"), "…por version.json: " + src);
                ArchivosSimulados(new Dictionary<string, string> { { MullvadDir + @"\mullvadbrowser.exe", "140.17.0" } }, null);
                Eq("", WingetProvider.FilesVersion(mv, "15.0.10", out src), "sin version.json, el 140.x del ejecutable no se usa (otra versión mayor)");

                // TeraBox: el ejecutable principal (DisplayIcon) dice 1.49.5.183.
                var tb = TeraBoxReal("1.47.0");
                ArchivosSimulados(new Dictionary<string, string> { { TeraBoxDir + @"\TeraBox.exe", "1.49.5.183" } }, null);
                Eq("1.49.5.183", WingetProvider.FilesVersion(tb, "1.47.0", out src), "TeraBox: versión del ejecutable");
                Check(src.Contains("TeraBox.exe"), "…por TeraBox.exe: " + src);
                // Sin DisplayIcon (o con el desinstalador de ícono): se prueba «<Nombre>.exe» en su carpeta.
                tb.DisplayIcon = TeraBoxDir + @"\uninst.exe";
                Eq("1.49.5.183", WingetProvider.FilesVersion(tb, "1.47.0", out src), "TeraBox sin ícono útil: TeraBox.exe de su carpeta");
                // Un ejecutable con otra numeración (versión mayor distinta) no dice nada.
                ArchivosSimulados(new Dictionary<string, string> { { TeraBoxDir + @"\TeraBox.exe", "7.0.0.1" } }, null);
                Eq("", WingetProvider.FilesVersion(tb, "1.47.0", out src), "otra versión mayor: no se usa");
            }
            finally { LectoresOriginales(); }

            Check(ProgramRules.FilesAlreadyAt("1.49.5.183", "1.47.0", "1.49.5", false), "archivos 1.49.5.183, registro 1.47.0, ofrecida 1.49.5: ya está");
            Check(!ProgramRules.FilesAlreadyAt("1.48.2.10", "1.47.0", "1.49.5", false), "archivos en una intermedia: no");
            Check(!ProgramRules.FilesAlreadyAt("1.47.0.120", "1.47.0", "1.49.5", false), "el ejecutable con el número de compilación de la misma versión: no");
            Check(!ProgramRules.FilesAlreadyAt("15.0.24", "15.0.24", "15.0.24", true), "sin diferencia con el registro: no es un retraso del registro");
            Check(!ProgramRules.FilesAlreadyAt("", "1.47.0", "1.49.5", false), "sin dato: no");
            // Notepad++: su ejecutable dice «8.69» (ProductVersion) y el producto es 8.6.9; winget ofrece 8.7.1. Leída como
            // número, 8.69 es «más nueva» que 8.7.1: un ejecutable no alcanza si no coincide EXACTO con la ofrecida.
            Check(!ProgramRules.FilesAlreadyAt("8.69", "8.6.9", "8.7.1", false), "Notepad++ 8.69 (ejecutable) no es «ya está en la 8.7.1»");
            Check(!ProgramRules.PlausibleFilesVersion("8.69", "8.7.1", false), "…ni se informa como versión de los archivos");
            Check(ProgramRules.PlausibleFilesVersion("1.48.2.10", "1.49.5", false), "una intermedia sí se informa");
            Check(ProgramRules.FilesAlreadyAt("15.0.25", "15.0.10", "15.0.24", true), "version.json más nueva que la ofrecida (winget atrasado): ya está");
            Check(!ProgramRules.FilesAlreadyAt("15.0.25", "15.0.10", "15.0.24", false), "un ejecutable más nuevo que la ofrecida: no se da por bueno");
        }

        // ------------------------------------------------------------------ Búsqueda

        static void TestBusquedaConRegistroAtrasado()
        {
            string upgrade = TablaWinget(Cab167,
                new[] { "TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5", "winget" },
                new[] { "Mullvad Browser", "MullvadVPN.MullvadBrowser", "15.0.10", "15.0.24", "winget" }) + "2 actualizaciones disponibles.\n";
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            wp.QueryForTests = delegate(string args) { return Salida(0, args.StartsWith("upgrade") ? upgrade : upgrade); };
            try
            {
                // TeraBox se actualizó solo (TeraBox.exe 1.49.5.183) y el registro quedó en 1.47.0: «Al día», explicado.
                ArchivosSimulados(new Dictionary<string, string> { { TeraBoxDir + @"\TeraBox.exe", "1.49.5.183" }, { MullvadDir + @"\mullvadbrowser.exe", "140.17.0" } },
                                  new Dictionary<string, string> { { MullvadDir + @"\version.json", "{\"version\":\"15.0.10\"}" } });
                var salida = new List<UpdateCandidate>();
                wp.Scan(ContextoSimulado(true, TeraBoxReal("1.47.0"), MullvadReal("15.0.10")), salida, new List<string>());
                var tb = salida.Find(delegate(UpdateCandidate x) { return x.Identifier == "Baidu.TeraBox"; });
                Check(tb != null && tb.Status == UpdateStatus.AlDia && !tb.Installable && !tb.Recommended, "TeraBox actualizado solo: al día, sin ofrecer reinstalar: " + (tb != null ? tb.Detail : "sin fila"));
                Check(tb != null && tb.InstalledVersion == "1.49.5.183" && tb.Detail.Contains("1.47.0") && tb.Detail.Contains("se actualizó solo"), "con la versión real y el motivo");
                var mv = salida.Find(delegate(UpdateCandidate x) { return x.Identifier == "MullvadVPN.MullvadBrowser"; });
                Check(mv != null && mv.Status == UpdateStatus.Disponible && mv.Installable && mv.InstalledVersion == "15.0.10", "Mullvad realmente en la 15.0.10: se ofrece normal");

                // Archivos en una versión intermedia: se ofrece, con el dato; la columna sigue con la del registro.
                ArchivosSimulados(new Dictionary<string, string> { { TeraBoxDir + @"\TeraBox.exe", "1.48.2.10" } }, null);
                salida = new List<UpdateCandidate>();
                wp.Scan(ContextoSimulado(true, TeraBoxReal("1.47.0")), salida, new List<string>());
                tb = salida.Find(delegate(UpdateCandidate x) { return x.Identifier == "Baidu.TeraBox"; });
                Check(tb != null && tb.Status == UpdateStatus.Disponible && tb.Installable && tb.InstalledVersion == "1.47.0" && tb.Detail.Contains("1.48.2.10"),
                      "intermedia: se ofrece y se dice: " + (tb != null ? tb.Detail : "sin fila"));

                // Mullvad: el navegador se actualizó solo y la entrada quedó atrás: al día por version.json.
                ArchivosSimulados(null, new Dictionary<string, string> { { MullvadDir + @"\version.json", "{\"version\":\"15.0.24\"}" } });
                salida = new List<UpdateCandidate>();
                wp.Scan(ContextoSimulado(true, MullvadReal("15.0.10")), salida, new List<string>());
                mv = salida.Find(delegate(UpdateCandidate x) { return x.Identifier == "MullvadVPN.MullvadBrowser"; });
                Check(mv != null && mv.Status == UpdateStatus.AlDia && mv.Detail.Contains("version.json"), "Mullvad actualizado solo: al día: " + (mv != null ? mv.Detail : "sin fila"));

                // Sin poder leer los archivos: como antes.
                SinArchivosSimulados();
                salida = new List<UpdateCandidate>();
                wp.Scan(ContextoSimulado(true, TeraBoxReal("1.47.0")), salida, new List<string>());
                tb = salida.Find(delegate(UpdateCandidate x) { return x.Identifier == "Baidu.TeraBox"; });
                Check(tb != null && tb.Status == UpdateStatus.Disponible && tb.Recommended, "sin datos de los archivos: disponible como siempre");
            }
            finally { LectoresOriginales(); }

            // Zoom: registro «6.2.11 (12345)», ejecutable 6.2.11.12345, winget ofrece 6.3.0. Es la misma versión: sin nota.
            try
            {
                var zoomDir = @"C:\Users\Fede\AppData\Roaming\Zoom\bin";
                var zoom = Registro(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ZoomUMX", "Zoom Workplace", "6.2.11 (12345)", "Zoom", zoomDir,
                                    zoomDir + @"\Installer.exe /uninstall", null, zoomDir + @"\Zoom.exe", false);
                zoom.Scope = "Usuario actual";
                ArchivosSimulados(new Dictionary<string, string> { { zoomDir + @"\Zoom.exe", "6.2.11.12345" } }, null);
                var w = new WingetRow { Name = "Zoom Workplace", Id = "Zoom.Zoom", Version = "6.2.11 (12345)", Available = "6.3.0", Source = "winget" };
                var c = FilaWinget("Zoom Workplace", "Zoom.Zoom", "6.2.11 (12345)", "6.3.0");
                c.Detail = "Versión más nueva en el repositorio winget.";
                WingetProvider.CheckFilesVersion(c, w, new List<InstalledProgram> { zoom });
                Check(c.Status == UpdateStatus.Disponible && c.Detail == "Versión más nueva en el repositorio winget.", "Zoom: misma versión con otro formato, sin cambios: " + c.Detail);
            }
            finally { LectoresOriginales(); }

            // TeraBox es del equipo (HKLM) aunque viva en la carpeta del usuario: sin administrador se ofrece elevar.
            var sel = new List<UpdateCandidate> { FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5"), FilaWinget("Mullvad Browser", "MullvadVPN.MullvadBrowser", "15.0.10", "15.0.24") };
            var equipo = UpdateManager.ProgramsNeedingAdmin(ContextoSimulado(false, TeraBoxReal("1.47.0"), MullvadReal("15.0.10")), sel);
            Check(equipo.Count == 1 && equipo[0].Name == "TeraBox", "sin administrador: TeraBox (equipo) sí, Mullvad (usuario) no");
            // Su carpeta es la del programa: se pueden encontrar sus procesos abiertos.
            Eq(TeraBoxDir, RunningPrograms.FolderOf(TeraBoxReal("1.47.0")), "carpeta de TeraBox (por el ícono, sin InstallLocation)");
            Eq(MullvadDir, RunningPrograms.FolderOf(MullvadReal("15.0.10")), "carpeta de Mullvad Browser");
            Eq("mullvad/mullvad-browser", GitHubRules.RepoOf(MullvadReal("15.0.24")), "Mullvad registra su proyecto de GitHub (URLUpdateInfo)");

            // Seguridad (S30): elevado, el editor de una entrada del usuario (HKCU) no sirve para aceptar la firma de un
            // instalador de GitHub; el de una entrada del equipo (HKLM) sí, y sin elevar, el del usuario también.
            Eq("", WingetDirect.TrustedRegistryPublisher(ContextoSimulado(true), MullvadReal("15.0.24")), "elevado + HKCU: no cuenta");
            Eq("Mullvad VPN", WingetDirect.TrustedRegistryPublisher(ContextoSimulado(false), MullvadReal("15.0.24")), "sin elevar + HKCU: cuenta");
            Eq("Flextech Inc.", WingetDirect.TrustedRegistryPublisher(ContextoSimulado(true), TeraBoxReal("1.47.0")), "HKLM: cuenta");
        }

        // ------------------------------------------------------------------ Instalación

        static void TestInstalacionQueNoCambiaNada()
        {
            var wp = new WingetProvider();
            wp.SimulateVersion("1.29.380");
            wp.RunForTests = delegate(string args) { return Salida(0, "Encontrado TeraBox [Baidu.TeraBox] Versión 1.49.5\nInstalado correctamente\n"); };
            string sigue = TablaWinget(Cab167, new[] { "TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5", "winget" });
            wp.ListForTests = delegate(string args) { return Salida(0, sigue); };
            try
            {
                SinArchivosSimulados();
                // TeraBox abierto en el área de notificación y el usuario no aceptó cerrarlo: winget «terminó bien» y
                // no cambió nada. Antes: «Actualizada» en verde. Ahora: error con el motivo.
                RunningPrograms.SnapshotForTests = delegate
                {
                    return new List<RunningProcess> { new RunningProcess { Pid = 4100, Name = "TeraBox.exe", Path = TeraBoxDir + @"\TeraBox.exe" },
                                                      new RunningProcess { Pid = 4101, Name = "TeraBoxHost.exe", Path = TeraBoxDir + @"\TeraBoxHost.exe" } };
                };
                var c = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
                wp.Install(ContextoSimulado(true, TeraBoxReal("1.47.0")), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("no cambió nada") && c.Result.Contains("TeraBox.exe") && c.Result.Contains("área de notificación"),
                      "TeraBox abierto: no cambió nada, se dice por qué: " + c.Result);
                Check(UpdateManager.CanInstall(c), "…y se puede reintentar");

                // Sin procesos abiertos: el motivo genérico (y cómo verlo).
                RunningPrograms.SnapshotForTests = delegate { return new List<RunningProcess>(); };
                c = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
                wp.Install(ContextoSimulado(true, TeraBoxReal("1.47.0")), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("no cambió nada") && c.Result.Contains("Mostrar la ventana de cada instalador"), "sin procesos: motivo genérico: " + c.Result);

                // El registro no cambia, pero los archivos quedaron en la versión pedida: es TeraBox, que no actualiza su entrada.
                ArchivosSimulados(new Dictionary<string, string> { { TeraBoxDir + @"\TeraBox.exe", "1.49.5.183" } }, null);
                c = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
                wp.Install(ContextoSimulado(true, TeraBoxReal("1.47.0")), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Instalada && c.InstalledVersion == "1.49.5.183" && c.Result.Contains("Windows sigue registrando la 1.47.0"),
                      "archivos actualizados, registro atrasado: actualizada, explicado: " + c.Result);

                // Los archivos cambiaron (1.47.0.120 → 1.48.0.5) aunque el registro no: no es «no cambió nada».
                bool despues = false;
                WingetProvider.ExeVersionReader = delegate(string path) { return path == TeraBoxDir + @"\TeraBox.exe" ? (despues ? "1.48.0.5" : "1.47.0.120") : ""; };
                wp.RunForTests = delegate(string args) { despues = true; return Salida(0, "Encontrado TeraBox [Baidu.TeraBox] Versión 1.49.5\nInstalado correctamente\n"); };
                c = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
                wp.Install(ContextoSimulado(true, TeraBoxReal("1.47.0")), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Instalada && c.Result.Contains("pasó de la 1.47.0.120 a la 1.48.0.5"), "los archivos cambiaron: no es «no cambió nada»: " + c.Result);
                wp.RunForTests = delegate(string args) { return Salida(0, "Encontrado TeraBox [Baidu.TeraBox] Versión 1.49.5\nInstalado correctamente\n"); };

                // Mullvad abierto: el registro dice 15.0.24 pero version.json sigue en 15.0.10 (archivos en uso).
                SinArchivosSimulados();
                ArchivosSimulados(null, new Dictionary<string, string> { { MullvadDir + @"\version.json", "{\"version\":\"15.0.10\"}" } });
                RunningPrograms.SnapshotForTests = delegate { return new List<RunningProcess> { new RunningProcess { Pid = 5200, Name = "mullvadbrowser.exe", Path = MullvadDir + @"\mullvadbrowser.exe" } }; };
                wp.RunForTests = delegate(string args) { return Salida(0, "Encontrado Mullvad Browser [MullvadVPN.MullvadBrowser] Versión 15.0.24\nInstalado correctamente\n"); };
                wp.ListForTests = delegate(string args) { return Salida(0, TablaWinget(Cab167, new[] { "Mullvad Browser", "MullvadVPN.MullvadBrowser", "15.0.24", "", "winget" })); };
                c = FilaWinget("Mullvad Browser", "MullvadVPN.MullvadBrowser", "15.0.10", "15.0.24");
                wp.Install(ContextoSimulado(true, MullvadReal("15.0.10")), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("siguen en la 15.0.10") && c.Result.Contains("mullvadbrowser.exe") && c.InstalledVersion == "15.0.10",
                      "Mullvad abierto: registro nuevo y archivos viejos, se detecta: " + c.Result);

                // Mullvad cerrado: archivos y registro en 15.0.24: correcta.
                ArchivosSimulados(null, new Dictionary<string, string> { { MullvadDir + @"\version.json", "{\"version\":\"15.0.24\"}" } });
                RunningPrograms.SnapshotForTests = delegate { return new List<RunningProcess>(); };
                c = FilaWinget("Mullvad Browser", "MullvadVPN.MullvadBrowser", "15.0.10", "15.0.24");
                wp.Install(ContextoSimulado(true, MullvadReal("15.0.10")), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Instalada && c.Result == "Actualizada correctamente" && c.InstalledVersion == "15.0.24", "Mullvad cerrado: correcta: " + c.Result);

                // winget ya no lista el paquete después de instalar: se confirma con el registro.
                SinArchivosSimulados();
                wp.ListForTests = delegate(string args) { return Salida(0x8A150014, "No se encontró ningún paquete instalado que coincida."); };
                WingetProvider.RegistryVersionForTests = delegate(InstalledProgram p) { return p != null && p.Name == "TeraBox" ? "1.47.0" : ""; };
                wp.RunForTests = delegate(string args) { return Salida(0, "Instalado correctamente\n"); };
                c = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
                wp.Install(ContextoSimulado(true, TeraBoxReal("1.47.0")), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("no cambió nada"), "sin winget list: manda el registro: " + c.Result);
                WingetProvider.RegistryVersionForTests = delegate(InstalledProgram p) { return "1.49.5"; };
                c = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
                wp.Install(ContextoSimulado(true, TeraBoxReal("1.47.0")), new List<UpdateCandidate> { c }, delegate(UpdateCandidate x) { });
                Check(c.Status == UpdateStatus.Instalada && c.InstalledVersion == "1.49.5", "sin winget list, el registro confirma: " + c.Result);
            }
            finally
            {
                RunningPrograms.SnapshotForTests = null;
                WingetProvider.RegistryVersionForTests = null;
                LectoresOriginales();
            }
        }

        static void TestDescargaDirectaQueNoCambiaNada()
        {
            try
            {
                SinArchivosSimulados();
                var ok = new InstallerExit();
                ok.Success = true;
                WingetDirect.RegistryVersionForTests = delegate(string name) { return "1.47.0"; };
                var c = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
                WingetDirect.Finish(ContextoSimulado(true), c, TeraBoxReal("1.47.0"), "1.49.5", ok, "SHA-256 del catálogo verificado");
                Check(c.Status == UpdateStatus.Fallida && c.Result.Contains("no cambió nada"), "vía directa sin cambios: error (antes «Actualizada»): " + c.Result);
                ArchivosSimulados(new Dictionary<string, string> { { TeraBoxDir + @"\TeraBox.exe", "1.49.5.183" } }, null);
                c = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
                WingetDirect.Finish(ContextoSimulado(true), c, TeraBoxReal("1.47.0"), "1.49.5", ok, "SHA-256 del catálogo verificado");
                Check(c.Status == UpdateStatus.Instalada && c.InstalledVersion == "1.49.5.183" && c.Result.Contains("Windows sigue registrando"), "vía directa, registro atrasado: " + c.Result);
                WingetDirect.RegistryVersionForTests = delegate(string name) { return "1.49.5"; };
                c = FilaWinget("TeraBox", "Baidu.TeraBox", "1.47.0", "1.49.5");
                WingetDirect.Finish(ContextoSimulado(true), c, TeraBoxReal("1.47.0"), "1.49.5", ok, "SHA-256 del catálogo verificado");
                Check(c.Status == UpdateStatus.Instalada && c.Result.StartsWith("Actualizada correctamente"), "vía directa correcta: " + c.Result);
            }
            finally { WingetDirect.RegistryVersionForTests = null; LectoresOriginales(); }
        }
        static WingetManifest Manifest(string name) { return WingetManifest.Parse(Sample(name)); }

        static WingetInstaller Choose(WingetManifest m, string output, string native, string installedArch, bool perMachine, bool isMsi)
        {
            string why;
            return WingetRules.Choose(m, output, native, installedArch, perMachine, isMsi, out why);
        }

        static void TestManifiestosWinget()
        {
            // --- Lector YAML: las formas que aparecen en los manifiestos reales.
            string y = "# comentario\nA: uno\nB: 'con '' comilla' # comentario\nC: \"doble \\\"x\\\"\"\nUrl: https://x.com/a:b?c=1\n" +
                       "Lista:\n- a\n- 'b # no es comentario'\nMapa:\n  K1: v1\n  K2:\n  - x\n  - y\n" +
                       "Items:\n- N: 1\n  Sub:\n  - P: q\n  Otro: z\n- N: 2\nEnLinea: [1, 2, '3']\nBloque: |\n  linea 1\n  linea 2\nFin: si\n";
            var root = MiniYaml.Parse(y) as Dictionary<string, object>;
            Check(root != null, "YAML: la raíz es un mapa");
            Eq("uno", MiniYaml.Str(root, "A"), "YAML: escalar simple");
            Eq("con ' comilla", MiniYaml.Str(root, "B"), "YAML: comillas simples con '' y comentario");
            Eq("doble \"x\"", MiniYaml.Str(root, "C"), "YAML: comillas dobles con escapes");
            Eq("https://x.com/a:b?c=1", MiniYaml.Str(root, "Url"), "YAML: los «:» de una URL no parten la clave");
            var lista = MiniYaml.List(root, "Lista");
            Check(lista != null && lista.Count == 2 && (string)lista[1] == "b # no es comentario", "YAML: lista a la misma altura que la clave");
            var mapa = MiniYaml.Map(root, "Mapa");
            Check(mapa != null && MiniYaml.Str(mapa, "K1") == "v1" && MiniYaml.List(mapa, "K2") != null && MiniYaml.List(mapa, "K2").Count == 2, "YAML: mapa anidado con lista");
            var items = MiniYaml.List(root, "Items");
            Check(items != null && items.Count == 2, "YAML: lista de mapas");
            if (items != null && items.Count == 2)
            {
                Eq("z", MiniYaml.Str(items[0], "Otro"), "YAML: la clave después de una lista anidada sigue en el mismo mapa");
                Eq("q", MiniYaml.Str(MiniYaml.List(items[0], "Sub")[0], "P"), "YAML: lista de mapas dentro de un mapa de lista");
                Eq("2", MiniYaml.Str(items[1], "N"), "YAML: segundo elemento");
            }
            var enLinea = MiniYaml.List(root, "EnLinea");
            Check(enLinea != null && enLinea.Count == 3 && (string)enLinea[2] == "3", "YAML: lista en línea");
            Eq("linea 1\nlinea 2", MiniYaml.Str(root, "Bloque"), "YAML: bloque «|»");
            Eq("si", MiniYaml.Str(root, "Fin"), "YAML: la clave después de un bloque");
            Check(MiniYaml.Parse("") is Dictionary<string, object>, "YAML: texto vacío");

            // --- GOM Player: el paquete del hash viejo. Tipo y alcance vienen de la raíz.
            var gom = Manifest("winget_manifest_gomplayer.yaml");
            Eq("GOMLab.GOMPlayer", gom.Id, "GOM: identificador");
            Eq("2.3.114.5384", gom.Version, "GOM: versión");
            Eq(1, gom.Installers.Count, "GOM: un instalador");
            var g = gom.Installers[0];
            Eq("x86", g.Architecture, "GOM: x86");
            Eq("nullsoft", g.Type, "GOM: tipo heredado de la raíz");
            Eq("machine", g.Scope, "GOM: alcance heredado de la raíz");
            Eq("https://cdn2.gomlab.com/gretech/player/edge/GOMPLAYERGLOBALSETUP_NEW.EXE", g.Url, "GOM: la dirección SIN versión (por eso el fabricante la reemplaza)");
            Eq("5923fb5af5944f124576e8f82f0518c294be05ebdda44beeeba3f62572aca2b9", g.Sha256, "GOM: hash en minúsculas");
            Check(g.InstallModes.Contains("silent"), "GOM: InstallModes");
            var nested = WingetManifest.Parse("PackageIdentifier: A.B\nPackageVersion: 1\nInstallerType: zip\nNestedInstallerType: inno\nNestedInstallerFiles:\n- RelativeFilePath: setup.exe\nInstallers:\n- Architecture: x64\n  InstallerUrl: https://a.b/setup.zip\n  InstallerSha256: 0123456789012345678901234567890123456789012345678901234567890123\n");
            Check(nested.Installers[0].IsArchive && nested.Installers[0].NestedFile == "setup.exe" && nested.Installers[0].EffectiveType == "inno", "ZIP: NestedInstallerType y NestedInstallerFiles heredados de la raíz");

            // --- TeamViewer: CRLF, instaladores zip (MSI anidado) y nullsoft.
            var tv = Manifest("winget_manifest_teamviewer.yaml");
            Eq("TeamViewer.TeamViewer", tv.Id, "TeamViewer: identificador (CRLF)");
            Eq(4, tv.Installers.Count, "TeamViewer: cuatro instaladores");
            Eq("zip", tv.Installers[0].Type, "TeamViewer: el primero es zip");
            Eq("wix", tv.Installers[0].NestedType, "TeamViewer: con un MSI adentro");
            Eq("update.msi", tv.Installers[0].NestedFile, "TeamViewer: archivo MSI interno declarado");
            Check(tv.Installers[0].IsArchive && tv.Installers[0].EffectiveType == "wix" && tv.Installers[0].IsMsi, "TeamViewer ZIP: tecnología efectiva = MSI/WiX");
            Eq("INSTALLFOLDER=\"<INSTALLPATH>\"", tv.Installers[0].Switch("InstallLocation"), "TeamViewer: interruptor InstallLocation con comillas");
            Eq("machine", tv.Installers[3].Scope, "TeamViewer: alcance de la raíz");
            Eq("c3f9e5b719e91e8c2b677265b088d7d6694bc5ca9c8a8221a0849b0165b73fee", tv.Installers[3].Sha256, "TeamViewer x64: hash");

            // --- Códigos: InstallerSuccessCodes y ExpectedReturnCodes.
            var vc = Manifest("winget_manifest_vcredist2013.yaml");
            Check(vc.Installers[0].SuccessCodes.Contains(3010), "VC++ 2013: 3010 es éxito");
            Eq("/quiet", vc.Installers[0].Switch("Silent"), "VC++ 2013: Silent de la raíz");
            Eq("/install /norestart", vc.Installers[0].Switch("Custom"), "VC++ 2013: Custom de la raíz");
            Eq("uninstallPrevious", vc.Installers[0].UpgradeBehavior, "VC++ 2013: UpgradeBehavior");
            var chrome = Manifest("winget_manifest_chrome_exe.yaml");
            Eq(6, chrome.Installers.Count, "Chrome EXE: seis instaladores");
            string resp;
            Check(chrome.Installers[0].ReturnCodes.TryGetValue(60, out resp) && resp == "installInProgress", "Chrome EXE: ExpectedReturnCodes de la raíz");
            Eq("--do-not-launch-chrome --system-level", chrome.Installers[3].Switch("Custom"), "Chrome EXE: Custom por instalador");
            // Códigos en hexadecimal y en decimal sin signo: el mismo valor que el ExitCode (Int32).
            var hex = WingetManifest.Parse("PackageIdentifier: A.B\nPackageVersion: 1\nInstallerType: exe\nInstallerSuccessCodes:\n- 0x8A150011\n- 2316632082\n- -1\n" +
                                           "ExpectedReturnCodes:\n- InstallerReturnCode: 0x80070005\n  ReturnResponse: blockedByPolicy\n" +
                                           "Installers:\n- Architecture: x64\n  InstallerUrl: https://a.b/c.exe\n  InstallerSha256: AA\n  ExpectedReturnCodes:\n  - InstallerReturnCode: 5\n    ReturnResponse: packageInUse\n");
            var h = hex.Installers[0];
            Check(h.SuccessCodes.Contains(unchecked((int)0x8A150011)) && h.SuccessCodes.Contains(unchecked((int)0x8A150012)) && h.SuccessCodes.Contains(-1), "códigos hexadecimales y decimales como Int32");
            Check(h.ReturnCodes.ContainsKey(unchecked((int)0x80070005)) && h.ReturnCodes.ContainsKey(5), "ExpectedReturnCodes: raíz e instalador combinados");

            // --- Direcciones del repositorio.
            Eq("https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/g/GOMLab/GOMPlayer/2.3.114.5384/GOMLab.GOMPlayer.installer.yaml",
               WingetRules.ManifestUrl("GOMLab.GOMPlayer", "2.3.114.5384", false), "URL del manifiesto de GOM Player");
            Eq("https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/t/TeamViewer/TeamViewer/15.81.6/TeamViewer.TeamViewer.yaml",
               WingetRules.ManifestUrl("TeamViewer.TeamViewer", "15.81.6", true), "URL de manifiesto de un solo archivo");
            Eq("https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/m/Microsoft/VCRedist/2015+/x64/14.0.1/Microsoft.VCRedist.2015+.x64.installer.yaml",
               WingetRules.ManifestUrl("Microsoft.VCRedist.2015+.x64", "14.0.1", false), "URL con «+» en el identificador");
            Check(WingetRules.ManifestUrl("GOMLab.GOMPlayer", "2.3.11…", false) == null, "versión recortada por winget: sin URL directa");
            Check(WingetRules.ManifestUrl("GOMLab..GOMPlayer", "1", false) == null, "identificador con «..» rechazado");
            Check(WingetRules.ManifestUrl("GOMLab/../x", "1", false) == null, "identificador con barras rechazado");
            Check(WingetRules.ManifestUrl("SinPunto", "1", false) == null, "identificador sin editor rechazado");
            Check(WingetRules.ManifestUrl("A.B", "1/../2", false) == null, "versión con barras rechazada");
            Eq("https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/g/GOMLab/GOMPlayer?ref=master",
               WingetRules.ListingUrl("GOMLab.GOMPlayer"), "URL del listado de versiones");
            Check(WingetRules.IsGitHubRaw(new Uri("https://raw.githubusercontent.com/microsoft/winget-pkgs/master/x")), "raw.githubusercontent.com permitido");
            Check(!WingetRules.IsGitHubRaw(new Uri("http://raw.githubusercontent.com/x")), "raw por HTTP rechazado");
            Check(!WingetRules.IsGitHubRaw(new Uri("https://raw.githubusercontent.com.evil.io/x")), "dominio parecido rechazado");
            Check(!WingetRules.IsGitHubRaw(new Uri("https://api.github.com/x")) && WingetRules.IsGitHubApi(new Uri("https://api.github.com/x")), "API de GitHub sólo para el listado");

            string listado = "[{\"name\":\".validation\",\"type\":\"file\"},{\"name\":\"2.3.114.5384\",\"type\":\"dir\"},{\"name\":\"2.3.115.5390\",\"type\":\"dir\"}," +
                             "{\"name\":\"2.3.99.1\",\"type\":\"dir\"},{\"name\":\"../x\",\"type\":\"dir\"}]";
            var versiones = WingetRules.ParseListing(listado);
            Eq(3, versiones.Count, "listado: sólo carpetas con versión válida");
            Eq("2.3.115.5390", WingetRules.PickVersion(versiones, null), "listado: la más alta");
            Eq("2.3.114.5384", WingetRules.PickVersion(versiones, "2.3.114"), "listado: la que empieza con la versión recortada");
            Check(WingetRules.PickVersion(versiones, "9.") == null, "listado: ninguna con ese prefijo");
        }

        static void TestInstaladorDirecto()
        {
            // --- El mismo instalador que usó winget: su dirección está en la salida (log real).
            var gom = Manifest("winget_manifest_gomplayer.yaml");
            string gomOut = "Encontrado GOM Player [GOMLab.GOMPlayer] Versión 2.3.114.5384\nDescargando https://cdn2.gomlab.com/gretech/player/edge/GOMPLAYERGLOBALSETUP_NEW.EXE\n" +
                            "El hash del instalador no coincide; esto no se puede sobreescribir al ejecutarse como administrador\n";
            var g = Choose(gom, gomOut, "x64", "x86", true, false);
            Check(g != null && g.Architecture == "x86" && g.Type == "nullsoft", "GOM: el nullsoft x86 que eligió winget");
            Check(Choose(gom, gomOut, "ARM64", "", true, false) != null, "GOM: x86 corre en ARM64");

            var tv = Manifest("winget_manifest_teamviewer.yaml");
            string tvOut = "Encontrado TeamViewer [TeamViewer.TeamViewer] Versión 15.81.6\n" +
                           "Descargando https://download.teamviewer.com/download/version_15x/TeamViewer_Setup_x64_15.81.6.exe\nInternetOpenUrl() failed.\n0x80072ee2 : unknown error\n";
            var t = Choose(tv, tvOut, "x64", "x64", true, false);
            Check(t != null && t.Url.EndsWith("TeamViewer_Setup_x64_15.81.6.exe"), "TeamViewer: el nullsoft x64 que eligió winget");
            t = Choose(tv, null, "x64", "x64", true, false);
            Check(t != null && t.Type == "nullsoft" && t.Architecture == "x64", "TeamViewer sin salida de winget: x64 compatible con el tipo instalado");
            t = Choose(tv, null, "x86", "", true, false);
            Check(t != null && t.Type == "nullsoft" && t.Architecture == "x86", "TeamViewer en Windows de 32 bits: el x86");
            string why;
            var soloZip = WingetRules.Choose(tv, "Descargando https://download.teamviewer.com/download/version_15x/update/Update_msi_15.81.6_x64.zip\n", "x64", "", true, true, out why);
            Check(soloZip != null && soloZip.IsArchive && soloZip.NestedType == "wix" && soloZip.NestedFile == "update.msi", "si winget usa el ZIP, la vía directa puede seleccionar el MSI interno");
            Eq("Update_msi_15.81.6_x64.zip", WingetDirect.FileNameFor(soloZip), "nombre del archivo ZIP conservado por FileNameFor");
            Check(WingetRules.OutputMentions("Descargando https://a.b/setup.exe\r\n", "https://a.b/setup.exe"), "URL al final de la línea");
            Check(!WingetRules.OutputMentions("Descargando https://a.b/setup.exe?v=2", "https://a.b/setup.exe"), "una URL que es prefijo de otra no cuenta");

            // --- Alcance: WinSCP publica la MISMA dirección para equipo y para usuario, con otros interruptores.
            var scp = Manifest("winget_manifest_winscp.yaml");
            string scpOut = "Descargando https://sourceforge.net/projects/winscp/files/WinSCP/5.21.7/WinSCP-5.21.7-Setup.exe/download\n";
            var sm = Choose(scp, scpOut, "x64", "x86", true, false);
            Check(sm != null && sm.Scope == "machine" && sm.Switch("Custom") == "/NORESTART /ALLUSERS", "WinSCP instalado para el equipo: /ALLUSERS");
            var su = Choose(scp, scpOut, "x64", "x86", false, false);
            Check(su != null && su.Scope == "user" && su.Switch("Custom") == "/NORESTART /CURRENTUSER", "WinSCP instalado para el usuario: /CURRENTUSER");
            Eq("download.exe", WingetDirect.FileNameFor(sm), "nombre del archivo cuando la URL termina en /download");

            // --- Arquitecturas: nunca un instalador que el equipo no ejecuta.
            Eq(-1, WingetRules.ArchRank("arm", "x64", ""), "ARM de 32 bits no corre en x64");
            Eq(-1, WingetRules.ArchRank("x64", "x86", ""), "x64 no corre en Windows de 32 bits");
            Eq(-1, WingetRules.ArchRank("arm64", "x64", ""), "ARM64 no corre en x64");
            Check(WingetRules.ArchRank("x64", "arm64", "") > 0, "x64 corre emulado en ARM64");
            Check(WingetRules.ArchRank("x64", "x64", "x64") > WingetRules.ArchRank("x86", "x64", ""), "se prefiere la arquitectura instalada");
            Check(WingetRules.ArchRank("neutral", "x86", "") > 0 && WingetRules.ArchRank("", "x86", "") > 0, "neutral o sin arquitectura: sirve en cualquier equipo");
            var chrome = Manifest("winget_manifest_chrome_exe.yaml");
            var cm = Choose(chrome, null, "x64", "x64", true, false);
            Check(cm != null && cm.Architecture == "x64" && cm.Scope == "machine", "Chrome EXE: x64 del equipo");
            var cu = Choose(chrome, null, "x64", "x64", false, false);
            Check(cu != null && cu.Scope == "user", "Chrome EXE: del usuario si está instalado para el usuario");

            // --- Interruptores, como los arma winget.
            bool msi;
            Eq("/S", WingetRules.BuildArguments(g, @"C:\t\gom.exe", false, null, out msi, out why), "nullsoft en silencio: /S");
            Check(!msi, "nullsoft no usa msiexec");
            Eq("", WingetRules.BuildArguments(g, @"C:\t\gom.exe", true, null, out msi, out why), "nullsoft interactivo: sin interruptores");
            Eq("/quiet /install /norestart", WingetRules.BuildArguments(Manifest("winget_manifest_vcredist2013.yaml").Installers[0], "x", false, null, out msi, out why),
               "burn: Silent y Custom del manifiesto");
            Eq("--do-not-launch-chrome --system-level", WingetRules.BuildArguments(cm, "x", false, null, out msi, out why),
               "exe silencioso por naturaleza (InstallModes: silent): sólo Custom");
            // Como winget al actualizar: predeterminados de Inno + Custom + Upgrade (WinSCP repite los mismos en los dos).
            Eq("/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NORESTART /ALLUSERS /NORESTART /ALLUSERS", WingetRules.BuildArguments(sm, "x", false, null, out msi, out why),
               "inno: interruptores predeterminados de winget más Custom y Upgrade");
            var msiInst = WingetManifest.Parse("PackageIdentifier: A.B\nPackageVersion: 1\nInstallers:\n- Architecture: x64\n  InstallerType: msi\n  InstallerUrl: https://a.b/c.msi\n  InstallerSha256: AA\n  InstallerSwitches:\n    Custom: ALLUSERS=1\n").Installers[0];
            Eq("/i \"C:\\d d\\c.msi\" /quiet /norestart ALLUSERS=1 /l*v \"C:\\l\\msi.log\"",
               WingetRules.BuildArguments(msiInst, @"C:\d d\c.msi", false, @"C:\l\msi.log", out msi, out why), "MSI: msiexec /i con el registro detallado");
            Check(msi, "MSI usa msiexec");
            Eq("c.msi", WingetDirect.FileNameFor(msiInst), "nombre del MSI");
            var exeSin = WingetManifest.Parse("PackageIdentifier: A.B\nPackageVersion: 1\nInstallers:\n- Architecture: x64\n  InstallerType: exe\n  InstallerUrl: https://a.b/c\n  InstallerSha256: AA\n").Installers[0];
            Check(WingetRules.BuildArguments(exeSin, "x", false, null, out msi, out why) == null && why.Contains("silencio"), "exe sin interruptores ni modo silencioso: no se ejecuta a ciegas");
            Eq("", WingetRules.BuildArguments(exeSin, "x", true, null, out msi, out why), "el mismo exe en modo interactivo sí");
            Eq("c.exe", WingetDirect.FileNameFor(exeSin), "nombre sin extensión: se agrega .exe");
            var conToken = WingetManifest.Parse("PackageIdentifier: A.B\nPackageVersion: 1\nInstallers:\n- Architecture: x64\n  InstallerType: nullsoft\n  InstallerUrl: https://a.b/%2e%2e%5cx.exe\n  InstallerSha256: AA\n  InstallerSwitches:\n    Custom: /D=<INSTALLPATH>\n").Installers[0];
            Check(WingetRules.BuildArguments(conToken, "x", false, null, out msi, out why) == null, "interruptores con <INSTALLPATH>: sólo los resuelve winget");
            Eq("instalador.exe", WingetDirect.FileNameFor(conToken), "un nombre de archivo raro de la URL se reemplaza");

            // --- Códigos de salida.
            Check(WingetRules.InterpretExit(g, 0).Success, "0 es éxito");
            var reboot = WingetRules.InterpretExit(g, 3010);
            Check(reboot.Success && reboot.Reboot, "3010: éxito con reinicio");
            Check(WingetRules.InterpretExit(Manifest("winget_manifest_vcredist2013.yaml").Installers[0], 3010).Success, "código de éxito del manifiesto");
            var enUso = WingetRules.InterpretExit(chrome.Installers[0], 60);
            Check(!enUso.Success && enUso.Message.Contains("otra instalación"), "ExpectedReturnCodes: installInProgress");
            var ya = WingetRules.InterpretExit(chrome.Installers[0], 3);
            Check(ya.Success, "alreadyInstalled: se confirma después con el registro");
            Check(!WingetRules.InterpretExit(msiInst, 1618).Success && WingetRules.InterpretExit(msiInst, 1618).Message.Contains("otra instalación"), "MSI 1618");
            var raro = WingetRules.InterpretExit(g, 5);
            Check(!raro.Success && raro.Message.Contains("5") && raro.Message.Contains("Mostrar la ventana de cada instalador"), "código desconocido: se muestra y se sugiere el modo interactivo");

            // --- Cuándo se prueba la vía directa.
            Check(WingetDirect.Applies(0x8A150011), "hash que no coincide (GOM Player): vía directa");
            Check(WingetDirect.Applies(0x80072EE2), "tiempo de espera (TeamViewer): vía directa");
            Check(WingetDirect.Applies(0x8A150008), "descarga fallida: vía directa");
            Check(!WingetDirect.Applies(0) && !WingetDirect.Applies(0x8A150006) && !WingetDirect.Applies(0x8A150101), "éxito, error del instalador o programa abierto: no");
            Check(WingetDirect.Why(0x8A150011, "GOM Player").Contains("firma digital"), "motivo del hash para la barra de estado");
            Check(WingetDirect.Why(0x80072EE2, "TeamViewer").Contains("retoma"), "motivo del corte para la barra de estado");
        }

        static void TestFirmantes()
        {
            var n = WingetRules.SignerNames("CN=GOM & Company, O=GOM & Company, L=Seoul, S=Seoul, C=KR");
            Check(n.Count == 2 && n[0] == "GOM & Company", "firmante: O= primero, después CN=");
            n = WingetRules.SignerNames("CN=\"Foo, Inc.\", O=\"Foo, Inc.\", C=US");
            Check(n.Count == 2 && n[0] == "Foo, Inc.", "firmante con coma entre comillas");
            Eq(0, WingetRules.SignerNames("").Count, "sin sujeto");

            Eq("gom", WingetRules.NormalizeCompany("GOM & Company"), "empresa: sin la forma jurídica");
            Eq("teamviewer germany", WingetRules.NormalizeCompany("TeamViewer Germany GmbH"), "empresa: GmbH");
            Eq("microsoft", WingetRules.NormalizeCompany("Microsoft Corporation"), "empresa: Corporation");
            Eq("co", WingetRules.NormalizeCompany("Co."), "una sola palabra no se vacía");

            string how;
            string gomSubject = "CN=GOM & Company, O=GOM & Company, L=Seoul, C=KR";
            // Mismo firmante que lo instalado: se acepta.
            Check(WingetRules.MatchSigner(gomSubject, new List<string> { "GOM & Company" }, "GOM & Company", out how) == null && how.Contains("igual que el programa instalado"),
                  "GOM: firmado igual que el programa instalado");
            Check(WingetRules.MatchSigner(gomSubject, new List<string> { "gom  &  company" }, "", out how) == null, "comparación sin mayúsculas ni espacios de más");
            // Otro firmante: se rechaza aunque el editor del registro coincida.
            string r = WingetRules.MatchSigner("CN=Otra Empresa, O=Otra Empresa", new List<string> { "GOM & Company" }, "Otra Empresa", out how);
            Check(r != null && r.Contains("Otra Empresa") && r.Contains("GOM & Company"), "firmante distinto del instalado: rechazado aunque el registro diga otra cosa");
            // Sin ejecutables firmados: el editor del registro, exacto salvo la forma jurídica.
            Check(WingetRules.MatchSigner(gomSubject, new List<string>(), "GOM & Company", out how) == null && how.Contains("editor registrado"), "sin firmas instaladas: editor del registro");
            r = WingetRules.MatchSigner("O=TeamViewer Germany GmbH", null, "TeamViewer", out how);
            Check(r != null, "«TeamViewer Germany» no es «TeamViewer»: no se acepta por parecido");
            r = WingetRules.MatchSigner("O=Gom Evil Ltd", null, "GOM & Company", out how);
            Check(r != null, "un nombre que sólo empieza igual no pasa");
            Check(WingetRules.MatchSigner(gomSubject, null, "", out how) != null, "sin nada con qué comparar: rechazado");
            Check(WingetRules.MatchSigner("", new List<string> { "X" }, "X", out how) != null, "sin firmante legible: rechazado");
            Check(WingetRules.MatchSigner(gomSubject, null, "Co", out how) != null, "un editor de menos de 3 letras no alcanza");
        }

        static void TestMensajes139()
        {
            // El registro real decía «(… se reintentó 3 veces); reintento 2 de 3».
            Check(WingetProvider.Describe(0x80072EE2).IndexOf("reintent", StringComparison.OrdinalIgnoreCase) < 0, "0x80072EE2 no dice cuántas veces se reintentó");
            Check(WingetProvider.Describe(0x80072EE2).IndexOf('(') < 0, "0x80072EE2 sin paréntesis (se anidaban)");
            Check(WingetProvider.Describe(0x80072EE7).IndexOf("Internet", StringComparison.OrdinalIgnoreCase) < 0, "DNS: no dice «sin Internet»");

            var c = new UpdateCandidate();
            var r = new ProcResult();
            r.Started = true;
            r.ExitCode = unchecked((int)0x8A150011);
            WingetProvider.ApplyResult(c, r, "El hash del instalador no coincide; esto no se puede sobreescribir al ejecutarse como administrador", 1,
                                       "no se pudo leer su manifiesto en github.com/microsoft/winget-pkgs (x)");
            Eq(UpdateStatus.Fallida, c.Status, "hash sin vía directa: fallida");
            Check(c.Result.Contains("catálogo de winget") && c.Result.Contains("no se pudo leer su manifiesto"), "hash: explica por qué no hubo vía directa");
            Check(!c.Result.Contains("sobreescribir"), "hash: no repite la línea de winget");

            c = new UpdateCandidate();
            r.ExitCode = unchecked((int)0x80072EE2);
            WingetProvider.ApplyResult(c, r, "0x80072ee2 : unknown error", 3, null);
            Check(c.Result.Contains("(3 intentos)") && !c.Result.Contains("unknown error"), "corte: cuántos intentos, sin la línea cruda");

            c = new UpdateCandidate();
            r.ExitCode = unchecked((int)0x8A150101);
            WingetProvider.ApplyResult(c, r, "El paquete está en uso", 1, null);
            Check(c.Result.Contains("El paquete está en uso"), "otros errores: la última línea de winget sigue");

            // Notas y plurales.
            Eq("Google Chrome y Microsoft Edge", Fmt.JoinY(new[] { "Google Chrome", "Microsoft Edge" }), "«y» entre dos nombres");
            Eq("A, B y C", Fmt.JoinY(new[] { "A", "B", "C" }), "«y» entre tres nombres");
            Eq("A", Fmt.JoinY(new[] { "A" }), "un nombre");
            Eq("", Fmt.JoinY(new string[0]), "ninguno");
            Eq("x; y", Fmt.JoinDistinct(new[] { "x", "y", "x" }), "motivos sin repetir");
            Eq("1 actualización del sistema", Fmt.Plural(1, "actualización del sistema", "actualizaciones del sistema"), "singular");
            Eq("0 instaladas", Fmt.Plural(0, "instalada", "instaladas"), "cero en plural");
            foreach (string f in new[] { Path.Combine("src", Path.Combine("Actualizador.Engine", Path.Combine("Updates", "BrowserProvider.cs"))),
                                         Path.Combine("src", Path.Combine("Actualizador.Engine", Path.Combine("Updates", "VendorProvider.cs"))) })
            {
                string ruta = ProjectFile(f);
                Check(ruta != null && File.ReadAllText(ruta).Contains("Fmt.JoinY(errors.Keys)"), f + ": nombres unidos con «y»");
            }

            // Red: qué pasó, con el servidor.
            string dns = Http.DescribeNameFailure("versionhistory.googleapis.com", TimeSpan.FromSeconds(11.1), true);
            Check(dns.Contains("no respondió") && dns.Contains("versionhistory.googleapis.com") && dns.Contains("11 s"), "DNS que no contesta (11 s, como en el registro): " + dns);
            dns = Http.DescribeNameFailure("www.aimp.ru", TimeSpan.FromMilliseconds(40), true);
            Check(dns.Contains("no existe") && dns.Contains("filtro DNS"), "DNS que contesta «no existe» al instante: " + dns);
            Check(Http.DescribeNameFailure("x", TimeSpan.FromSeconds(1), false).Contains("sin conexión"), "sin adaptador de red");
            Check(Http.DescribeConnectFailure("download.teamviewer.com", new[] { IPAddress.Any }).Contains("filtro DNS"), "resuelto a 0.0.0.0: bloqueado por un filtro DNS");
            Check(Http.DescribeConnectFailure("a.b", new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }).Contains("filtro DNS"), "resuelto a la propia máquina: bloqueado");
            Check(Http.DescribeConnectFailure("a.b", new[] { IPAddress.Parse("93.184.216.34") }) == "no se pudo conectar con a.b", "dirección real: no se pudo conectar");
            Check(Http.DescribeConnectFailure("a.b", null) == "no se pudo conectar con a.b", "sin direcciones");

            long start, total;
            Check(Http.ParseContentRange("bytes 120000-299999/300000", out start, out total) && start == 120000 && total == 300000, "Content-Range");
            Check(Http.ParseContentRange("bytes 0-0/*", out start, out total) && total == -1, "Content-Range con total desconocido");
            Check(!Http.ParseContentRange("items 1-2/3", out start, out total) && !Http.ParseContentRange("", out start, out total), "Content-Range inválido");
            Check(Http.IsHttps(new Uri("https://cdn2.gomlab.com/x.exe")), "HTTPS cualquier servidor");
            Check(!Http.IsHttps(new Uri("http://cdn2.gomlab.com/x.exe")) && !Http.IsHttps(new Uri("https://cdn2.gomlab.com:8443/x.exe")), "HTTP o puerto raro: rechazado");
        }

        // ------------------------------------------------------------------ Servidor HTTP mínimo

        delegate void Responder(int request, long rangeFrom, Stream s);

        /// <summary>
        /// Servidor HTTP de prueba sobre TcpListener (HttpListener en Windows pide permisos de URL).
        /// Escucha sólo en 127.0.0.1 y cierra la conexión después de cada respuesta.
        /// </summary>
        sealed class MiniServer : IDisposable
        {
            readonly TcpListener listener;
            readonly Responder responder;
            public int Requests;
            public readonly List<long> Ranges = new List<long>();
            /// <summary>Encabezados de cada pedido, en orden (método, ruta, If-Range…).</summary>
            public readonly List<string> Heads = new List<string>();
            /// <summary>Cuerpo de cada pedido (vacío en un GET), en orden (1.7.1).</summary>
            public readonly List<string> Bodies = new List<string>();
            public readonly int Port;

            public MiniServer(Responder r) : this(r, 0) { }

            /// <summary>En un puerto dado (0 = el que dé el sistema): para «volver» en el mismo puerto (1.7.0).</summary>
            public MiniServer(Responder r, int port)
            {
                responder = r;
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var t = new Thread(Loop);
                t.IsBackground = true;
                t.Start();
            }

            public string Url(string path) { return "http://127.0.0.1:" + Port + path; }

            void Loop()
            {
                while (true)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); }
                    catch { return; }
                    try
                    {
                        using (c)
                        {
                            var s = c.GetStream();
                            string head = ReadHead(s);
                            long from = -1;
                            foreach (string line in head.Split('\n'))
                            {
                                string l = line.Trim();
                                if (!l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                                string v = l.Substring(6).Trim();
                                if (v.StartsWith("bytes=")) long.TryParse(v.Substring(6).TrimEnd('-'), out from);
                            }
                            // El cuerpo del pedido (POST) se lee entero antes de responder (1.7.1): si queda sin leer,
                            // Windows corta la conexión con RST al cerrarla y el cliente pierde la respuesta
                            // («se ha forzado la interrupción de una conexión existente»; en Linux no pasa).
                            long body = 0;
                            foreach (string line in head.Split('\n'))
                            {
                                string l = line.Trim();
                                if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) long.TryParse(l.Substring(15).Trim(), out body);
                            }
                            var bodyBytes = new List<byte>();
                            for (long i = 0; i < body; i++) { int b = s.ReadByte(); if (b < 0) break; bodyBytes.Add((byte)b); }
                            lock (Bodies) Bodies.Add(Encoding.ASCII.GetString(bodyBytes.ToArray()));
                            int n = Interlocked.Increment(ref Requests);
                            lock (Ranges) Ranges.Add(from);
                            lock (Heads) Heads.Add(head);
                            responder(n, from, s);
                            s.Flush();
                            try { c.Client.Shutdown(SocketShutdown.Send); } catch { }
                        }
                    }
                    catch { }
                }
            }

            static string ReadHead(Stream s)
            {
                var sb = new StringBuilder();
                int b;
                while ((b = s.ReadByte()) >= 0)
                {
                    sb.Append((char)b);
                    if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break;
                    if (sb.Length > 16384) break;
                }
                return sb.ToString();
            }

            public void Dispose() { try { listener.Stop(); } catch { } }
        }

        static void Respond(Stream s, string status, string[] headers, byte[] body, int offset, int count)
        {
            var sb = new StringBuilder("HTTP/1.1 " + status + "\r\nConnection: close\r\n");
            foreach (string h in headers) sb.Append(h).Append("\r\n");
            sb.Append("\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            s.Write(head, 0, head.Length);
            if (body != null && count > 0) s.Write(body, offset, count);
        }

        static bool SameBytes(string file, byte[] data)
        {
            byte[] got = File.ReadAllBytes(file);
            if (got.Length != data.Length) return false;
            for (int i = 0; i < got.Length; i++) if (got[i] != data[i]) return false;
            return true;
        }

        static void TestWingetEnglish()
        {
            var rows = WingetTable.Parse(Sample("winget_upgrade_en.txt"));
            Eq(7, rows.Count, "filas (6 + 1 explícita), sin el pie «upgrades available»");
            var edge = Find(rows, "Microsoft.Edge");
            Check(edge != null, "Edge encontrado");
            if (edge != null)
            {
                Eq("Microsoft Edge", edge.Name, "nombre");
                Eq("128.0.2739.67", edge.Version, "versión instalada");
                Eq("129.0.2792.52", edge.Available, "versión disponible");
                Eq("winget", edge.Source, "origen");
                Check(!edge.RequiresExplicit, "no explícita");
            }
            var ff = Find(rows, "Mozilla.Firefox.es-AR");
            Check(ff != null && ff.Version == "< 129.0.2", "versión con «<»");
            var zoom = Find(rows, "Zoom.Zoom");
            Check(zoom != null && WingetTable.IsUnknownVersion(zoom.Version), "versión desconocida");
            WingetRow vc = null;
            foreach (var r in rows) if (r.Id.StartsWith("Microsoft.VCRedist")) vc = r;
            Check(vc != null && vc.IdTruncated, "Id truncado detectado");
            var wa = Find(rows, "9NKSQGP7F2NH");
            Check(wa != null && wa.Source == "msstore", "app de la Store vía msstore");
            var py = Find(rows, "Python.Python.3.11");
            Check(py != null && py.RequiresExplicit, "tabla de selección explícita");
            Eq(0, WingetTable.Parse(Sample("winget_upgrade_vacio.txt")).Count, "sin resultados");
            Eq("Name  Id", WingetTable.CleanLine("   - \r   \\ \rName  Id"), "limpieza del indicador de progreso");
            Eq("x", WingetTable.CleanLine("\u001b[32mx\u001b[0m"), "secuencias ANSI");
        }

        static void TestWingetSpanish()
        {
            var rows = WingetTable.Parse(Sample("winget_upgrade_es.txt"));
            Eq(3, rows.Count, "filas");
            var baidu = Find(rows, "Baidu.BaiduNetdisk");
            Check(baidu != null, "fila con caracteres de ancho doble");
            if (baidu != null)
            {
                Eq("百度网盘", baidu.Name, "nombre CJK");
                Eq("7.40.0.3", baidu.Version, "versión tras CJK");
                Eq("7.44.0.5", baidu.Available, "disponible tras CJK");
            }
            var java = Find(rows, "Oracle.JavaRuntimeEnvironment");
            Check(java != null && java.Name == "Actualización de Java 8", "acentos");
        }

        static void TestWingetList()
        {
            var rows = WingetTable.Parse(Sample("winget_list_es.txt"));
            Eq(6, rows.Count, "filas");
            var arp = Find(rows, "ARP\\Machine\\X64\\{6F1C0D2E-1234-4ABC-9DEF-001122334455}");
            Check(arp != null && arp.Source == "" && arp.Available == "", "entrada ARP sin origen");
            var calc = Find(rows, "Microsoft.WindowsCalculator_8wekyb3d8bbwe");
            Check(calc != null && calc.Source == "msstore" && calc.Available == "", "al día en msstore");
            var sz = Find(rows, "7zip.7zip");
            Check(sz != null && sz.Available == "25.01", "con actualización");
            var unk = Find(rows, "ARP\\User\\X64\\ProgramaX");
            Check(unk != null && WingetTable.IsUnknownVersion(unk.Version), "«Desconocido» en español");

            var four = WingetTable.Parse(Sample("winget_list_4col.txt"));
            Eq(2, four.Count, "4 columnas");
            var git = Find(four, "Git.Git");
            Check(git != null && git.Source == "winget" && git.Available == "", "4.ª columna interpretada como origen");
        }

        static void TestProgramasQueFaltaban()
        {
            string[] cab = { "Nombre", "Id", "Versión", "Disponible", "Origen" };

            // AnyDesk se registra como «ad 9.7.15»: con el espacio, la fila se descartaba entera.
            var up = WingetTable.Parse(TablaWinget(cab, new[] { "AnyDesk", "AnyDesk.AnyDesk", "ad 9.7.15", "9.7.16", "winget" }) + "1 actualizaciones disponibles.\n");
            Eq(1, up.Count, "AnyDesk aparece en winget upgrade");
            if (up.Count == 1) { Eq("ad 9.7.15", up[0].Version, "versión instalada de AnyDesk"); Eq("9.7.16", up[0].Available, "versión disponible de AnyDesk"); }

            // Programas fuera de los repositorios: winget arma el Id con la clave del registro, con espacios.
            var list = WingetTable.Parse(TablaWinget(cab,
                new[] { "AnyDesk", "AnyDesk.AnyDesk", "ad 9.7.15", "9.7.16", "winget" },
                new[] { "Picasa 3", @"ARP\Machine\X86\Picasa 3", "3.9.141.259", "", "" },
                new[] { "USB Disk Security", @"ARP\Machine\X86\USB Disk Security_is1", "Unknown", "", "" },
                new[] { "Microsoft Office Profesional Plus 2024 - e…", @"ARP\Machine\X64\ProPlus2024Retail - es-mx", "16.0.19127.20…", "", "" },
                new[] { "Google Chrome", "Google.Chrome", "140.0.7339.128", "", "winget" }));
            Eq(5, list.Count, "winget list: las 5 filas, incluidas las de Id con espacios");
            var ids = new List<string>(); foreach (var r in list) ids.Add(r.Id);
            Check(ids.Contains(@"ARP\Machine\X86\Picasa 3"), "Picasa 3 con su Id local");
            Check(ids.Contains(@"ARP\Machine\X86\USB Disk Security_is1"), "USB Disk Security con su Id local");
            Check(ids.Contains(@"ARP\Machine\X64\ProPlus2024Retail - es-mx"), "Office 2024 con su Id local");

            // Un Id de REPOSITORIO con espacios sigue siendo señal de fila mal leída: se descarta.
            var mal = WingetTable.Parse(TablaWinget(cab, new[] { "Algo", "Google Chrome", "1.0", "2.0", "winget" }));
            Eq(0, mal.Count, "Id de repositorio con espacios: se descarta");
            Check(WingetTable.IsLocalId(@"ARP\Machine\X64\x") && WingetTable.IsLocalId(@"MSIX\x") && !WingetTable.IsLocalId("Google.Chrome"), "IsLocalId");

            // Red de seguridad: lo que está en el registro y winget no informó aparece igual.
            var programas = new List<InstalledProgram>();
            foreach (var n in new[] { "AnyDesk", "Picasa 3", "Programa Fantasma", "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.51.36247", "Programa Fantasma" })
            { var p = new InstalledProgram(); p.Name = n; p.Version = "1.0"; p.Publisher = "X"; programas.Add(p); }
            var vistos = new List<WingetRow>();
            foreach (var n in new[] { "AnyDesk", "Picasa 3", "Microsoft Visual C++ 2022 X64 Minimum Runt…" }) { var w = new WingetRow(); w.Name = n; vistos.Add(w); }
            var salida = new List<UpdateCandidate>();
            int agregados = WingetProvider.AddMissingPrograms(programas, vistos, salida);
            Eq(1, agregados, "sólo se agrega el programa que winget no informó (y una sola vez)");
            if (salida.Count == 1)
            {
                Eq("Programa Fantasma", salida[0].Name, "el que faltaba");
                Eq(UpdateStatus.NoVerificable, salida[0].Status, "como no verificable");
                Check(!salida[0].Installable, "no se puede instalar desde acá");
            }
            Eq(0, WingetProvider.AddMissingPrograms(null, vistos, salida), "sin programas no revienta");
        }

        static void TestWingetErrors()
        {
            // El código de salida de winget llega como int negativo: no debe extenderse con signo.
            Eq("0x8A150006", Fmt.Hex(-1978335226), "hexadecimal de 32 bits sin signo");
            Eq("0x00000002", Fmt.Hex(2), "hexadecimal con ceros");
            Eq("0x8A150006", Fmt.Hex((long)unchecked((int)0x8A150006)), "long negativo de 32 bits");
            Check(WingetProvider.Describe(0x8A150006).IndexOf("instalador", StringComparison.OrdinalIgnoreCase) >= 0, "0x8A150006 descrito");
            Check(WingetProvider.Describe(0x8A150011).IndexOf("hash", StringComparison.OrdinalIgnoreCase) >= 0, "hash que no coincide");
            Check(WingetProvider.Describe(0x8A150019).IndexOf("administrador", StringComparison.OrdinalIgnoreCase) >= 0, "requiere administrador");
            Eq("2", WingetProvider.InstallerExitCode("Iniciando instalación de paquete...\r\nError del instalador con el código de salida: 2"), "código del instalador en español");
            Eq("1603", WingetProvider.InstallerExitCode("Installer failed with exit code: 1603"), "código del instalador en inglés");
            Check(WingetProvider.InstallerExitCode("todo bien") == null, "sin código de instalador");
        }
    }
}
