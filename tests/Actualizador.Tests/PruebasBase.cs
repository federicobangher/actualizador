// Infraestructura de las pruebas (Main, Run, Check, Eq). Las pruebas están por área en los demás archivos Pruebas*.cs; se compilan junto con las fuentes: ver build.bat (objetivo «pruebas») o tests/run_tests.sh.
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
        static int passed, failed;
        static string samples;

        static void Check(bool condition, string name)
        {
            if (condition) passed++;
            else { failed++; Console.WriteLine("  FALLÓ: " + name); }
        }

        static void Eq(object expected, object actual, string name)
        {
            bool ok = Equals(expected, actual) || (expected != null && actual != null && expected.ToString() == actual.ToString());
            if (ok) passed++;
            else { failed++; Console.WriteLine("  FALLÓ: " + name + " — esperado «" + expected + "», obtenido «" + actual + "»"); }
        }

        public static int Main(string[] args)
        {
            AppInfo.HardenProcess();   // 2.0.18: como los ejecutables; así todas las pruebas corren con el tiempo máximo de las expresiones regulares
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            samples = args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "samples");
            Performance.Light = false;   // 2.0.21: las pruebas, iguales en cualquier equipo (el modo liviano tiene su prueba)
            // --- Núcleo: versiones, formato, JSON, argumentos y procesos (PruebasNucleo.cs)
            Run("VersionUtil", TestVersions);
            Run("Fmt", TestFmt);
            Run("Sobrecargas (canario del compilador)", TestSobrecargas);
            Run("Json", TestJson);
            Run("Argumentos de procesos", TestQuoteArg);
            Run("Procesos que dejan hijos con la salida abierta", TestProcesosSinColgarse);
            Run("fallas silenciosas anotadas en el registro", TestRegistroDeFallasSilenciosas168);
            // --- Windows: ciclo de vida, versiones, manifiestos, scripts y TLS (PruebasWindows.cs)
            Run("Lifecycle", TestLifecycle);
            Run("TLS 1.3 sólo en Windows 11 / Server 2022 o posteriores", TestTls13);
            Run("ciclo de vida (Server 2025, Office 2021)", TestCicloDeVida161);
            Run("Windows 11 26H2 y 26H1 (soporte y controladores)", TestWindows11Recientes);
            Run("nombres de Windows 11 recientes en los mensajes", TestNombresDeWindows167);
            Run("tabla única de versiones de Windows, versiones futuras y ESU", TestVersionesDeWindows168);
            // --- winget: tablas, errores, manifiestos, vía directa e instalación (PruebasWinget.cs)
            Run("winget en inglés", TestWingetEnglish);
            Run("winget en español / CJK", TestWingetSpanish);
            Run("winget list", TestWingetList);
            Run("Programas que faltaban (AnyDesk, claves con espacios)", TestProgramasQueFaltaban);
            Run("Errores de winget", TestWingetErrors);
            Run("Manifiestos de winget (lector YAML y datos reales)", TestManifiestosWinget);
            Run("Elección de instalador, interruptores y códigos de salida", TestInstaladorDirecto);
            Run("Firmantes para el hash viejo del catálogo", TestFirmantes);
            Run("Mensajes de winget, red y plurales", TestMensajes139);
            Run("Líneas de comando del registro", TestLineasDeComando);
            Run("Double Commander: otra tecnología de instalación (0x8A15008E)", TestDoubleCommander);
            Run("GOM Player: la empresa cambió de nombre", TestGomPlayer);
            Run("TeamViewer: arquitectura instalada (código 2)", TestTeamViewer);
            Run("Códigos de winget (AppInstallerErrors.h)", TestCodigosWinget150);
            Run("Flujo de instalación con winget simulado", TestFlujoDeInstalacion);
            Run("Mensajes de instalación", TestMensajes150);
            Run("WinRAR: nunca una versión más vieja que la ofrecida", TestGuardiaDeVersion);
            Run("Flujo simulado: WinRAR de 32 bits y Epic Games Launcher abierto", TestFlujoWinRarYEpic);
            Run("Confirmación con winget list después de instalar", TestConfirmacionDespuesDeInstalar);
            Run("Versión instalada que winget no conoce", TestVersionDesconocida);
            Run("Arquitectura y mensajes", TestArquitecturaYMensajes153);
            Run("Reiniciar la aplicación no es reiniciar el equipo", TestReinicioDeLaAplicacion);
            Run("App Installer se actualiza al final de la tanda", TestAppInstallerAlFinal);
            Run("tablas de winget, versiones recortadas y programa correcto", TestAuditoriaWinget);
            Run("winget pide reiniciar ANTES de instalar (0x8A15010A) no es «Actualizada»", TestReinicioAntesDeInstalar);
            Run("identificadores recortados por winget (LibreOffice)", TestIdentificadoresRecortados);
            Run("Microsoft Store (msstore) que no responde: sólo el repositorio winget", TestMsstoreQueNoResponde);
            Run("el programa antes que sus complementos (paquete de ayuda de LibreOffice)", TestOrdenComplementos);
            Run("alcance equipo/usuario y Windows mínimo del instalador (TeraBox, LibreOffice)", TestAlcanceDelInstalador);
            Run("«no hay actualización aplicable»: sin --version, la causa y el instalador oficial", TestFlujoNoAplicable);
            Run("winget elevado (alias verificado) y sin administrador", TestWingetElevadoYSinAdministrador);
            Run("versión real de los archivos (TeraBox, Mullvad Browser)", TestVersionDeLosArchivos);
            Run("búsqueda con el registro atrasado (se actualizó solo)", TestBusquedaConRegistroAtrasado);
            Run("instalación que no cambia nada / archivos en uso", TestInstalacionQueNoCambiaNada);
            Run("descarga directa que no cambia nada", TestDescargaDirectaQueNoCambiaNada);
            // --- Descargas: reanudación, servidores que cambian el archivo y carpetas temporales (PruebasDescargas.cs)
            Run("Descarga reanudable contra un servidor local", TestDescargaReanudable);
            Run("Carpetas temporales de los instaladores", TestLimpiezaDeTemporales);
            Run("descargas que cambian, 416 y POST redirigido", TestAuditoriaDescargas);
            // --- Fabricantes: navegadores, catálogo de fuentes oficiales, GitHub, Office, Adobe y fabricante del equipo (PruebasFabricantes.cs)
            Run("Navegadores (Chrome, Edge, Firefox)", TestNavegadores);
            Run("Fabricante (AIMP) y reintentos de winget", TestFabricanteYReintentos);
            Run("Catálogo de fabricantes", TestCatalogoFabricantes);
            Run("Fuentes oficiales: Office, Vivaldi, Adobe, Samsung, DAEMON Tools, versiones finales", TestFuentesFabricantes);
            Run("GitHub, tipo de instalador y firmante esperado", TestGitHubEInstaladores);
            Run("Pistas, sitio del fabricante, deduplicación e informes", TestPistasYDeduplicacion);
            Run("Adobe Reader de 64 bits: el parche correcto primero", TestAcrobatReader64);
            Run("GitHub y fabricantes con la arquitectura real del programa", TestArquitecturaDeFabricantes);
            Run("Office 2021 (canal, red de Microsoft, actualizaciones desactivadas, Skype Empresarial)", TestOffice2021);
            Run("Office 2021, instalación con el actualizador simulado", TestOffice2021Instalacion);
            Run("Office MSI y Microsoft Update", TestOfficeMsi);
            Run("Adobe Acrobat, notas de versión de Windows (no el manifiesto de Mac)", TestAdobeNotasDeVersion);
            Run("Adobe Acrobat, búsqueda con la fuente simulada", TestAdobeBusquedaWindows);
            Run("Mullvad Browser en el catálogo de fabricantes (GitHub, firmante fijo)", TestMullvadEnElCatalogo);
            Run("aviso de versión nueva de Actualizador", TestVersionNueva168);
            Run("herramientas del fabricante del equipo (Dell, Lenovo, HP)", TestFabricanteDelEquipo168);
            // --- Controladores: Catálogo de Microsoft Update, pnputil y controladores del fabricante (PruebasControladores.cs)
            Run("Selección y controladores", TestSeleccionYControladores);
            Run("Catálogo de Microsoft Update: páginas reales y diálogo de descarga", TestCatalogoLector);
            Run("Catálogo de Microsoft Update: búsquedas, versiones de Windows y huellas", TestCatalogoReglas);
            Run("Catálogo de Microsoft Update: flujo con el catálogo simulado", TestCatalogoFlujo);
            Run("Catálogo de Microsoft Update: POST del diálogo de descarga contra un servidor local", TestCatalogoPost);
            Run("pnputil: lo que dijo en el equipo del usuario", TestPnpUtilResultados);
            Run("Fecha del controlador y registro de Windows", TestFechaDelControlador);
            Run("pnputil 259 con un INF instalado en el dispositivo (Synaptics)", TestPnpUtilSynaptics);
            Run("controlador del fabricante del equipo (Intel UHD Graphics 630)", TestControladorDelFabricante);
            Run("se anticipa en la búsqueda y se pregunta antes de forzar", TestControladorDelFabricanteEnLaBusqueda);
            Run("controladores en todas las versiones de Windows 10 y 11", TestControladores163);
            Run("catálogo y los identificadores con llaves", TestCatalogoLlaves);
            // --- Microsoft Store y Windows Update (PruebasStoreYWindowsUpdate.cs)
            Run("Store y Windows Update", TestStoreYWindowsUpdate);
            Run("Microsoft Store: sin filas repetidas", TestStoreSinDuplicados);
            Run("Microsoft Store: lo que actualizó deja de figurar pendiente en winget", TestStoreActualizoLoDeWinget);
            Run("runtimes, GitHub, Windows Update y versiones", TestAuditoriaActualizaciones);
            // --- Seguridad: rutas, secretos, firmas, antivirus (PruebasSeguridad.cs)
            Run("Seguridad", TestSeguridad);
            Run("Avast en la lista: el Centro de seguridad de Windows", TestAntivirusCentroDeSeguridad);
            // --- Instalación: programas abiertos, Windows Installer y resumen de la tanda (PruebasInstalacion.cs)
            Run("Registro de Windows Installer: la causa del 1603", TestRegistroMsi);
            Run("Carpetas de programas y procesos abiertos", TestCarpetasYProcesos);
            Run("el resumen de la instalación cuenta todo", TestResumenDeInstalacion);
            Run("plurales y registros de Windows Installer", TestDetalles161);
            // --- Informes, hallazgos de actualizaciones, diagnóstico y comparación de auditorías (PruebasInformes.cs)
            Run("paquete de diagnóstico", TestPaqueteDeDiagnostico);
            // --- Ignorados, historial y revisión programada (PruebasGestion.cs)
            Run("actualizaciones ignoradas", TestIgnorados168);
            Run("historial de actualizaciones", TestHistorial168);
            Run("revisión programada (tarea y aviso)", TestRevisionProgramada168);
            // --- 1.6.9: robustez, consumo y programas que no se actualizaban (PruebasRobustez.cs)
            Run("WMI con plazo real y sin reintentos inútiles", TestWmiConPlazo169);
            Run("procesos y servicios sin WMI", TestProcesosSinWmi169);
            Run("salida de los procesos acotada", TestSalidaAcotada169);
            Run("búsqueda de actualizaciones con plazo, prioridad baja y de a una en equipos chicos", TestBusquedaConPlazo169);
            Run("Windows Update y punto de restauración con plazo", TestWindowsUpdateConPlazo169);
            Run("Microsoft Store sin abrir PowerShell cada 15 segundos", TestStoreSinPowerShell169);
            Run("identificadores de winget con letras de otros idiomas", TestIdentificadoresWinget169);
            Run("instaladores que no admiten administrador (Spotify)", TestSinElevar169);
            Run("firmas, proxy con usuario, registro", TestDetalles169);
            Run("orígenes de winget dañados", TestOrigenesDanados169);
            // --- 1.7.0: red que se corta al instalar controladores y HTTPS (PruebasDescargas.cs)
            Run("la red se corta en medio de la tanda (controlador del puerto PCI Express)", TestRedQueSeCorta170);
            Run("controladores del catálogo: todo descargado antes de instalar, los de red al final", TestOrdenControladores170);
            Run("HTTPS que falla: sin el consejo de Windows 7 en Windows 11, y el motivo del certificado", TestHttps170);
            // --- 1.7.1: actualizar todo de una vez (Pruebas171.cs)
            Run("Visual C++ 2008–2013 se instalan desde Microsoft (antes no se podían marcar)", TestVisualCpp171);
            Run("winget se instala solo si falta (Windows sin Microsoft Store)", TestWingetSeInstalaSolo171);
            Run("todo de una vez: vuelve a buscar e instala lo que aparece", TestTodoDeUnaVez171);
            Run("programas sin registro (portables, otra cuenta) por sus accesos directos", TestProgramasSinRegistro171);
            Run("todos los controladores, también los incluidos con Windows", TestTodosLosControladores171);
            // --- 1.7.2: confiable: lo que no se compara, se dice (Pruebas172.cs)
            Run("GOM Player con la fuente del fabricante (el catálogo de winget va atrasado)", TestGomPlayer172);
            Run("programas que winget no reconoce, buscados en su catálogo por nombre y editor", TestWingetNoReconoce172);
            // --- 1.7.3: la versión en la página oficial de cada programa (Pruebas173.cs)
            Run("páginas oficiales de los programas que winget no cubre", TestPaginasOficiales173);
            // --- 1.7.4: auditoría después de la grabación (Pruebas174.cs)
            Run("AMD Ryzen Master «4.1», filas que no se podían marcar, instaladores de la página y catálogo saturado", TestAuditoria174);
            // --- 1.7.5: catálogo de fuentes oficiales en un archivo (Pruebas175.cs)
            Run("catálogo de fuentes oficiales (fuentes-oficiales.json)", TestCatalogoFuentes175);
            // --- 1.7.6: un solo número, series de Python, caché (Pruebas176.cs)
            Run("el mismo número en el título, la tarjeta y el botón", TestUnSoloNumero176);
            Run("Python 3.13 no se «actualiza» a la 3.14; el catálogo no consulta el reloj del sistema", TestSeriesYPaginas176);
            Run("consultas guardadas (winget y catálogo) y tiempos", TestCacheDeConsultas176);
            Run("grabación — AIMP con la página nueva y el controlador de audio que no se aplicaba", TestGrabacion176);
            Run("política de la empresa (JSON)", TestPoliticaJson20);
            Run("política — patrones, bloqueos y editores aprobados", TestPoliticaPatrones20);
            Run("política — archivo y directiva de grupo", TestPoliticaArchivoYRegistro20);
            Run("política aplicada por el motor (búsqueda e instalación)", TestPoliticaEnElMotor20);
            Run("registro de cambios con HMAC encadenado (detección de alteraciones)", TestRegistroDeCambios20);
            Run("clave del registro (sin DPAPI desde la 2.0.11; el registro viejo se guarda aparte)", TestRegistroConClaveDpapi20);
            Run("exportación CSV/JSON sin inyección de fórmulas ni secretos", TestExportacion20);
            Run("estados de red reales en .NET 8", TestEstadosDeRed20);
            Run("WMI por COM (fechas y propiedades)", TestWmiFechas20);
            Run("sesión de actualización", TestSesion20);
            Run("estructura del proyecto, scripts y documentación", TestEstructura20);
            Run("consola (comandos y opciones)", TestConsola20);
            // --- 2.0.4: Cura y las betas, catálogo de winget atrasado, programas sin editor (Pruebas204.cs)
            Run("Cura: beta que anuncia el programa y catálogo de winget atrasado (GitHub)", TestCuraYBetas204);
            Run("programas sin editor registrado buscados en el catálogo de winget", TestSinEditor204);
            // --- 2.0.5 (Pruebas205.cs)
            Run("errores de COM que .NET convierte (Programador de tareas, Windows Update)", TestErroresCom205);
            Run("Cura comparado siempre con UltiMaker; betas sólo a pedido", TestCuraFabricante205);
            // --- 2.0.6 (Pruebas206.cs)
            Run("dos listas: «Para actualizar · opcionales» y «Al día · sin verificar»", TestDosListas206);
            Run("nombres para buscar en winget: idiomas, paréntesis y una palabra menos", TestNombresParaBuscar206);
            Run("programas encontrados por un nombre parecido del mismo editor", TestNombresParecidos206);
            Run("búsqueda completa: Thunderbird en español, Opera Stable y VLC portable", TestBusquedaCompleta206);
            Run("lo encontrado por nombre se instala con el mismo alcance (usuario o equipo)", TestAlcanceAlInstalar206);
            Run("páginas de códigos de Windows en .NET 8 (pnputil, INF, accesos directos)", TestPaginasDeCodigos206);
            // --- 2.0.7 (Pruebas207.cs)
            Run("desinstalador: se eleva solo, orden seguro, todos los usuarios, menú de la ventana", TestDesinstalador207);
            Run("una sola instalación a la vez en el equipo", TestUnaInstalacionALaVez207);
            Run("opciones, ignorados, historial y páginas oficiales: se guardan enteros o nada", TestEscrituraAtomica207);
            // --- 2.0.8 (Pruebas208.cs)
            Run("ventana: siempre administrador, busca al pedirlo, instala sólo lo marcado, «Marcar todo» y tema", TestVentana208);
            Run("firmantes: el certificado se libera", TestCertificados208);
            Run("los «No verificable» de la captura: Tor, Samsung USB, LaunchBox, Windows, Steam y programas propios", TestNoVerificables208);
            // --- 2.0.10 (Pruebas2010.cs)
            Run("versión portable: datos al lado y por equipo, sin tareas programadas, versiones nuevas", TestPortable2010);
            Run("versión portable: empaquetado (un solo .exe, prueba antes de entregar, .7z)", TestEmpaquetadoPortable2010);
            // --- 2.0.11 (Pruebas2011.cs)
            Run("nombre nuevo: Actualizador (antes SuperProgram), lo viejo se sigue reconociendo", TestNombre2011);
            Run("nombre nuevo: archivos, instalador que pasa de SuperProgram a Actualizador, icono", TestRenombrado2011);
            Run("antivirus: nada de lo que vigila la protección de identidad (Avast IDP.Generic)", TestAntivirus2011);
            // --- 2.0.12 (Pruebas2012.cs)
            Run("7-Zip con su página oficial (7-zip.org): versión, instalador por arquitectura y SHA-256 del autor", TestSieteZipPaginaOficial2012);
            Run("la captura: 7-Zip 26.05 al día según 7-zip.org aunque GitHub tenga la 26.06; se instala, no abre GitHub", TestSieteZipCaptura2012);
            Run("GitHub no manda sobre la página oficial; si no se instala, se abre la página oficial", TestGitHubNoMandaSobreLaOficial2012);
            // --- 2.0.13 (Pruebas2013.cs)
            Run("controlador de gráficos de NVIDIA comparado con NVIDIA para la placa instalada", TestNvidia2013);
            Run("los «No verificable» de la captura del 07/11: cada uno dice por qué y qué hacer", TestNoVerificablesCaptura2013);
            Run("«Marcar todo» no marca la Store «Sin novedades»; la lista se rearma si una fila cambia de lista", TestMarcarTodoYListas2013);
            Run("instalador sin firma de GitHub (Double Commander): sólo si lo instalado tampoco tiene firma y es el mismo producto", TestSinFirmaDeGitHub2013);
            // --- 2.0.15 (Pruebas2015.cs)
            Run("Fortinet: FortiClient VPN y FortiClient con el instalador que enlaza su página de descargas", TestFortinetCatalogo2015);
            Run("versión de un instalador leída de sus recursos, pidiendo sólo los pedazos que hacen falta", TestVersionDelInstalador2015);
            Run("la búsqueda: FortiClient VPN disponible e instalable con la firma de Fortinet; FortiClient (EMS) sólo se compara", TestFortinetBusqueda2015);
            // --- 2.0.16 (Pruebas2016.cs)
            Run("la captura: TLauncher 2.9323 con la 2.9381 publicada (la fuente del propio TLauncher y la versión de su perfil)", TestTLauncher2016);
            Run("MSI Afterburner: la versión «Final» de msi.com, no la beta de al lado", TestAfterburner2016);
            Run("betas escritas con espacio («4.6.7 Beta 3») y juegos fuera de una tienda", TestBetasYJuegos2016);
            Run("portable: lo que se recuerda de un equipo no viaja al siguiente ni se borra con las consultas guardadas", TestDatosDelEquipo2016);
            Run("tus programas: nombres con «Windows», «Portable», «Final»… coinciden con su archivo", TestCarpetaDeVersiones2016);
            Run("entregas: Instalar.cmd no instala una publicación vieja; la que se instala sale en .7z", TestEntregas2016);
            // --- 2.0.17 (Pruebas2017.cs)
            Run("la dirección del instalador con la versión nueva (winget-pkgs) y SourceForge directo", TestDireccionConLaVersionNueva2017);
            Run("los enlaces de la página oficial: el instalador de este programa, nunca otra edición", TestEnlacesDeLaPagina2017);
            Run("el instalador por winget-pkgs: el manifiesto de esa versión o la dirección de la anterior", TestInstaladorPorWinget2017);
            Run("la captura: CrystalDiskInfo 9.9.4 con la 9.9.5 publicada se instala desde acá (antes abría su página)", TestCapturaCrystalDiskInfo2017);
            Run("la instalación desde la página oficial: verificada, en silencio, y la página sólo si no se puede", TestInstalacionDesdeLaPagina2017);
            Run("Microsoft Store: sin esperar 4 minutos cuando no hay nada, y mientras se instala lo demás", TestStoreSinEsperarDeMas2017);
            Run("las esperas después de instalar y la política de instaladores sin firma", TestEsperasDespuesDeInstalar2017);
            // --- 2.0.18 (Pruebas2018.cs)
            Run("SourceForge: User-Agent de herramienta de descargas (a un navegador le da una página)", TestUserAgentDeDescargas2018);
            Run("páginas intermedias: «Redirecting…» de crystalmark.info, espera de SourceForge, JavaScript", TestPaginasIntermedias2018);
            Run("la captura de la 2.0.17: CrystalDiskInfo con los servidores como responden de verdad", TestCapturaConServidoresReales2018);
            Run("al tocar «Actualizar» se busca de nuevo el instalador antes de abrir la página", TestSegundaOportunidadAlInstalar2018);
            Run("auditoría: tiempo máximo de las expresiones regulares y límites del lector YAML", TestLimitesAuditoria2018);
            // --- 2.0.19 (Pruebas2019.cs)
            Run("starter4j (TLauncher): formato, direcciones del fabricante y rutas dentro de la carpeta del arranque", TestStarter4jFormato2019);
            Run("starter4j: se baja lo que falta verificado con SHA-1 y recién después se anota la versión", TestStarter4jAplicar2019);
            Run("la captura: TLauncher 2.9381 → 2.9382 se actualiza desde acá, sin abrir tlauncher.org", TestCapturaTLauncher2019);
            // --- 2.0.20 (Pruebas2020.cs)
            Run("WMI: los parámetros omitidos ya no dan 0x80020005, y si COM falla, PowerShell (los controladores no se revisaban)", TestWmiSinTiposQueNoCoinciden2020);
            Run("la captura de Vigía: VCLibs, Windows App Runtime y Teams para todos los usuarios aparecen", TestAppsParaTodosLosUsuarios2020);
            Run("apps para todos los usuarios: winget --scope machine o el paquete oficial aprovisionado", TestInstalarAppsParaTodosLosUsuarios2020);
            Run("la captura: Roblox con su versión real y su instalador oficial (winget pasó a «portable»)", TestRoblox2020);
            Run("Claude (Squirrel): la carpeta «app-6.28899.2» dice la versión real aunque el registro quede atrás", TestSquirrelClaude2020);
            Run("programas sólo del usuario: el instalador verificado corre como el usuario en vez de abrir la página", TestProgramasDelUsuarioSinAbrirLaPagina2020);
            Run("Windows Update: tamaño real, la acumulativa más nueva primero y qué hacer con 0x800F081F", TestWindowsUpdate2020);
            Run("versión 2.0.20", TestVersion2020);
            // --- 2.0.21 (Pruebas2021.cs)
            Run("winget como servicio (SYSTEM): el guion, la tarea programada y de dónde sale winget.exe", TestWingetComoServicio2021);
            Run("la captura de pc6: Outlook, Teams y PDF24 que ve Vigía aparecen en el Actualizador", TestCapturaVigiaPc6_2021);
            Run("instalar como servicio: --scope machine para los MSIX, sin él para un programa común", TestInstalarComoServicio2021);
            Run("la captura de pc7: el instalador de Roblox se reabre solo; se espera a que termine", TestRobloxSeReabre2021);
            Run("modo liviano para equipos viejos: menos tareas a la vez, el mismo resultado", TestModoLiviano2021);
            Run("versión 2.0.21", TestVersion2021);
            // --- 2.0.22 (Pruebas2022.cs)
            Run("la captura de pc7: Windows App Runtime para todos los usuarios, como servicio o con su instalador oficial", TestWindowsAppRuntime2022);
            Run("la captura de pc7: Roblox en todas sus carpetas, y con su ventana si en silencio no terminó", TestRobloxConVentana2022);
            Run("versión 2.0.22", TestVersion2022);
            // --- 2.0.23 (Pruebas2023.cs)
            Run("winget como servicio: el código 0 ya no se pierde (la redirección de cmd)", TestCodigoDeSalidaComoServicio2023);
            Run("winget como servicio terminó bien: actualizada, sin reinstalar por otra vía", TestServicioTerminoBien2023);
            Run("versión 2.0.23", TestVersion2023);
            // --- 2.0.24 (Pruebas2024.cs)
            Run("las capturas de pc2/pc3/pc4: la misma lista que Vigía, con el mismo nombre y marcada", TestMismaListaQueVigiaPc4_2024);
            Run("un programa común (del registro) aparece una sola vez", TestProgramaComunUnaVez2024);
            Run("versión 2.0.24", TestVersion2024);
            // --- 2.0.25 (Pruebas2025.cs)
            Run("AMD: la extensión no es el controlador de video (se ofrecía y nunca cambiaba nada)", TestExtensionNoEsElControlador2025);
            Run("AMD: con AMD Software (Adrenalin) el video se actualiza con AMD Software", TestConjuntoDeVideoAmd2025);
            Run("«dice que instala y al reiniciar sigue igual»: se recuerda y no se repite", TestPendienteDeReinicio2025);
            Run("pnputil 3010 es del paquete, no de cada dispositivo", TestCodigo3010DelPaquete2025);
            Run("AMD/ATI: identificadores con revisión (forzar y comprobar el paquete)", TestIdentificadoresDeAmd2025);
            Run("antes de instalar: el paquete tiene que ser para este dispositivo", TestPaqueteParaEsteEquipo2025);
            Run("lo que Windows no aplicó (controladores de terceros) no se vuelve a ofrecer", TestNoAplicadoNoSeRepite2025);
            Run("rendimiento: las páginas del catálogo se leen de una pasada", TestCatalogoSeLeeDeUnaPasada2025);
            Run("rendimiento: las páginas oficiales leídas hace un rato no se bajan de nuevo", TestPaginasRecientes2025);
            Run("rendimiento: en cada vuelta se busca sólo donde algo pudo cambiar", TestVueltasSoloLoQueCambio2025);
            Run("rendimiento: Windows Update descarga mientras se instala lo demás", TestWindowsUpdateDescargaAntes2025);
            Run("versión 2.0.25", TestVersion2025);
            // --- 2.0.26 (Pruebas2025.cs)
            Run("la grabación: AMD Radeon Vega 3 del fabricante del equipo; sin «forzar» no pasaba nada", TestVega3DelFabricante2026);
            Run("instalada y al reiniciar volvió la de antes: se dice y no se repite", TestVueltaAtrasAlReiniciar2026);
            Run("versión 2.0.26", TestVersion2026);
            // --- 2.0.27 (Pruebas2027.cs)
            Run("la grabación: el mismo controlador de Windows Update dos veces (HP Printer BIDI)", TestMismoControladorDosVeces2027);
            Run("la grabación: instalado y otra vez «sin controlador»: ningún dispositivo lo toma", TestControladorSinDispositivo2027);
            Run("la grabación: Office Hub para todos los usuarios fallaba igual en cada pasada", TestParaTodosLosUsuariosQueFalla2027);
            Run("versión 2.0.27", TestVersion2027);
            // --- 2.0.28 (Pruebas2028.cs)
            Run("relevamiento: lo instalado (o fallado) que vuelve a aparecer no se repite solo, en todas las fuentes", TestRedDeSeguridadHistorial2028);
            Run("relevamiento: nada termina la tanda sin resultado", TestNadaSinResultado2028);
            Run("versión 2.0.28", TestVersion2028);
            // --- 2.0.29 (Pruebas2029.cs)
            Run("la captura de Vigía: winget como servicio con las bibliotecas de Visual C++", TestWingetComoServicioConVcLibs2029);
            Run("la captura de Vigía: Windows App Runtime 1.6 también sin winget como servicio", TestWindowsAppRuntimeSinServicio2029);
            Run("la grabación: Office Hub para todos los usuarios no tiene con qué actualizarse", TestOfficeHubSinPaquete2029);
            Run("versión 2.0.29", TestVersion2029);
            // --- 2.0.30 (Pruebas2030.cs)
            Run("la captura: Office Hub se prueba como servicio y su detalle no se reemplaza por uno falso", TestOfficeHubComoServicio2030);
            Run("versión 2.0.30", TestVersion2030);
            // --- 2.0.31 (Pruebas2030.cs)
            Run("la grabación: el genérico de Windows ya elegido no se ofrece para forzar (High Definition Audio Device)", TestGenericoDeWindowsElegido2031);
            Run("versión 2.0.31", TestVersion2031);
            // --- 2.0.32 (Pruebas2032.cs)
            Run("se actualiza solo: la versión nueva en GitHub con su entrega y su SHA-256", TestVersionNuevaEnGitHub2032);
            Run("se actualiza solo: bajar, verificar y preparar la entrega", TestPrepararVersionNueva2032);
            Run("se actualiza solo: el guion que instala y vuelve a abrir", TestGuionDeActualizacion2032);
            Run("versión 2.0.32", TestVersion2032);
            // --- 2.0.33
            Run("buscar actualización de Actualizador desde Opciones y desde Más", TestVersion2033);
            // --- 2.0.34
            Run("la versión nueva se busca sin la API de GitHub (límite por conexión)", TestVersionSinApi2034);
            // --- 2.0.35 (Pruebas2035.cs)
            Run("relevamiento: «Marcar todo» no vuelve a marcar lo que ya falló o no quedó", TestMarcarTodoNoRepite2035);
            Run("relevamiento: la instalación sola que no quedó no se repite en cada apertura", TestInstalacionSolaQueNoQuedo2035);
            Run("relevamiento: Publicar.ps1 en Windows PowerShell 5.1", TestPublicarEnPowerShell51_2035);
            Run("versión 2.0.35", TestVersion2035);
            // --- 2.0.36 (Pruebas2036.cs)
            Run("GitHub arma y publica la versión solo (sin descargar nada)", TestPublicaGitHub2036);
            Run("Publicar: la sesión de GitHub vale aunque no esté Git", TestSesionSinGit2036);
            Run("versión 2.0.36", TestVersion2036);
            // --- 2.0.37 (Pruebas2037.cs)
            Run("la portable busca su propia entrega y se actualiza sola", TestPortableBuscaSuEntrega2037);
            Run("la portable: bajar, verificar y preparar su entrega", TestPortablePrepara2037);
            Run("la portable: el guion que copia encima y la vuelve a abrir", TestGuionPortable2037);
            Run("versión 2.0.37", TestVersion2037);
            // --- 2.0.38 (Pruebas2038.cs)
            Run("relevamiento: la consulta de versión nueva de otra versión no se reusa", TestConsultaDeOtraVersion2038);
            Run("relevamiento: los guiones salen de la carpeta bajada antes de abrir y borrar", TestGuionesSalenDeLaCarpeta2038);
            Run("versión 2.0.38", TestVersion2038);
            // --- 2.0.39 (Pruebas2039.cs)
            Run("la portable pesa menos: sin la consola ni las bibliotecas de WPF", TestPortableLiviana2039);
            Run("la portable borra lo que trajeron versiones anteriores y ya no usa", TestPortableBorraLoViejo2039);
            Run("versión 2.0.39", TestVersion2039);
            // --- 2.0.40 (Pruebas2040.cs)
            Run("Npcap y los instaladores que no aceptan el modo silencioso", TestReglasSinSilencio2040);
            Run("Npcap 1.82 → 1.90 se instala con su ventana (la captura)", TestNpcapConSuVentana2040);
            Run("un instalador que en silencio se niega (NSIS 2) se abre con su ventana", TestSeNiegaEnSilencioSeAbreConVentana2040);
            Run("Npcap por winget: con --interactive, sólo esa fila", TestNpcapPorWinget2040);
            Run("versión 2.0.40", TestVersion2040);
            Console.WriteLine();
            Console.WriteLine("Resultado: " + passed + " correctas, " + failed + " fallidas.");
            return failed == 0 ? 0 : 1;
        }

        static void Run(string name, Action test)
        {
            Console.WriteLine("» " + name);
            try { test(); }
            catch (Exception ex) { failed++; Console.WriteLine("  EXCEPCIÓN: " + ex); }
        }
    }
}
