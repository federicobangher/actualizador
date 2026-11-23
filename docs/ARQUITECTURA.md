# Arquitectura de Actualizador 2.0

## Proyectos

```
Actualizador.sln
├─ src/Actualizador.Core      Base: modelos, utilidades de Windows (registro, WMI por COM, procesos), seguridad
│   ├─ Util/                  Log, Json, Fmt, VersionUtil, OsInfo, ProcessRunner, SafePaths, Redactor…
│   └─ Security/              Policy, AuditTrail, EventLogWriter, AclGuard
├─ src/Actualizador.Engine    Motor de actualizaciones (sin interfaz)
│   ├─ Inventory/             Qué hay instalado: programas, controladores, apps (Appx)
│   └─ Updates/               Proveedores, UpdateManager, historial, ignorados, exportación, revisión programada
├─ src/Actualizador.App       Actualizador.exe — ventana (WinForms), requireAdministrator
├─ src/Actualizador.Cli       Actualizador.Cli.exe — consola, asInvoker
├─ src/Actualizador.Aviso     Actualizador.Aviso.exe — revisión programada, asInvoker
├─ tests/Actualizador.Tests   Pruebas (ejecutable propio, sin paquetes de terceros)
├─ build/                     Build.ps1, Publish.ps1, Sign.ps1, Empaquetar.ps1, verify-linux.sh
└─ deploy/                    Instalar.cmd, Install-/Uninstall-Actualizador.ps1, politica.ejemplo.json
```

Dependencias en un solo sentido: `App / Cli / Aviso → Engine → Core`. Ningún paquete NuGet: todo lo que se ejecuta
como administrador es código propio o de Windows (menos superficie de ataque y de cadena de suministro).

## Flujo de una búsqueda

1. `UpdateContext.Create` detecta el sistema y si hay privilegios; la **política** ajusta las opciones
   (`UpdateManager.ApplyPolicyToOptions`).
2. `InventoryLoader` lee lo instalado (registro, WMI por COM, Appx) una sola vez.
3. Los proveedores (`IUpdateProvider`) consultan en paralelo: winget, navegadores, fabricantes, GitHub, páginas oficiales,
   Windows Update, Catálogo de Microsoft Update, herramientas del fabricante del equipo y Microsoft Store. Los que la
   política apaga no se consultan.
4. Lo que winget da «Al día» se contrasta con el proyecto de GitHub de donde el manifiesto de winget baja el instalador
   (`ReleaseCrossCheck`): una versión estable más nueva quiere decir que el catálogo de winget va atrasado y se ofrece;
   una preliminar (beta, RC) nunca se ofrece, pero queda dicha en el detalle de la fila. **Manda la página oficial**
   (2.0.12): si en la misma búsqueda se leyó la versión que publica la página oficial del programa (`SiteVersions`, anotada
   en `UpdateCandidate.OfficialVersion`) y es anterior a la de GitHub, la de GitHub no se ofrece hasta que la página oficial
   la publique. Y si no se puede instalar desde acá, se abre la página oficial que registra el programa, no la de GitHub.
5. Se deduplica (la fila del fabricante reemplaza a la de winget; winget se prefiere sólo si ofrece exactamente la versión
   que publica el fabricante), se aplican los ignorados del usuario y después **la política** (`ApplyPolicy`): lo bloqueado queda a la
   vista, con el motivo, y no se puede marcar.
6. `UpdateGroups` clasifica cada fila (Para actualizar, Opcionales, Al día, Sin verificar) y la ventana las muestra en dos
   listas (`UpdateView`): «Para actualizar · opcionales» y «Al día · sin verificar», con el mismo número en el título, la
   tarjeta y el botón.

## El instalador de una versión que sólo publica la página (2.0.17)

`SiteVersions.Resolve` lee la versión en la página oficial; si es más nueva, `SiteInstallers.Find` ubica su instalador: por
winget-pkgs (`FromWinget`: el manifiesto de esa versión o la dirección de la anterior con la versión nueva, `TemplateUrl`) o por los
enlaces de la página y de su página de descargas (`FromPages`, `Candidates`, `ProbeFile`). La fila pasa al proveedor «fabricante»
con un `VendorPlan.Lead` y se instala con `VendorProvider.InstallSigned`, que verifica todo; si no se puede antes de ejecutar nada,
`OpenPageInstead` abre la página como antes.

## Flujo de una instalación

`UpdateManager.Install` vuelve a consultar la política en cada fila (`PolicyRefusal`), crea el punto de restauración
(obligatorio si la política lo exige), instala en orden winget → navegadores, fabricantes y páginas oficiales → controladores →
Windows Update, y confirma cada resultado leyendo de nuevo la versión instalada. **Microsoft Store** (2.0.17) arranca después de
winget y se mide en otro hilo mientras se instala lo demás: la Store descarga e instala por su cuenta, y se le pregunta por su cola
para saber cuándo terminó (`StoreProvider.ShouldStopMeasuring`). Cada paso queda en el historial del usuario, en el registro de
cambios y en el Visor de eventos. `InstallUntilDone` repite la búsqueda hasta 3 vueltas para lo que aparece recién
después de instalar otra cosa.

## Decisiones

- **.NET 8 (LTS) y WinForms.** WinForms es la interfaz de escritorio más madura de .NET 8, con DPI por monitor
  (PerMonitorV2), y la misma interfaz se compila en la integración continua sin Windows (contra .NET Framework 4.8 de
  Mono) para verificarla y sacar capturas. WPF no se puede compilar ni probar fuera de Windows.
- **Tres ejecutables.** La ventana se abre siempre como administrador (`requireAdministrator`, desde la 2.0.8); la consola y
  el aviso usan los privilegios de quien los ejecuta (`asInvoker`): un script desatendido nunca queda esperando un aviso de
  UAC y el Programador de tareas puede abrir el aviso sin privilegios (con un nivel que pide elevación fallaría con el error
  740). El aviso abre la ventana con ShellExecute, que muestra el UAC.
- **Tema claro/oscuro** (`Theme.cs`): un solo lugar con los colores y cómo se aplican a cada control; barra de título oscura
  por DWM y barras de desplazamiento oscuras por `SetWindowTheme`. «Según Windows» lee `AppsUseLightTheme` y sigue los
  cambios con la ventana abierta.
- **WMI por COM tardío** (`WbemScripting.SWbemLocator`) en vez de `System.Management`: no hace falta ningún paquete.
- **Visor de eventos por `ReportEventW`**: P/Invoke directo, sin paquetes.
- **Pruebas sin framework externo.** Un ejecutable que corre todas y devuelve 0 si pasan: se ejecuta igual en Windows
  (`build\Build.ps1`) y en Linux (`build/verify-linux.sh`).
