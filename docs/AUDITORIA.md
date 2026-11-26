# Auditoría de Actualizador (2.0.44, 26/11/2026)

Alcance: lo nuevo desde la auditoría de la 2.0.41 (aprovisionamiento para todos de la 2.0.42, red de seguridad de «Disponibles» de la
2.0.42/2.0.43), lo que cambia en esta versión y la publicación automática en GitHub. Base: el diagnóstico del 26/11/2026 (registros del
06/11 al 26/11).

## Errores (relevamiento)

| # | Síntoma | Causa | Arreglo |
|---|---|---|---|
| 1 | Epic Online Services 5.4.0 → 5.8.0: «Falló la instalación … el manifiesto no indica cómo instalarlo en silencio» | Paquete nuevo de otra tecnología (ZIP con un .exe, sin interruptores); la reinstalación verificada sólo sabía hacerlo en silencio | Si el instalador no tiene modo silencioso, la misma vía con su ventana (con alguien frente a la pantalla y si la política lo permite); la anterior se desinstala en silencio |
| 2 | `Instalar.cmd` (código fuente) en un equipo sin SDK: «Instalá el SDK de .NET 8 x64 … y volvé a ejecutar» | Instalar desde el código siempre compilaba; además, «winget» no estaba en el PATH de la ventana elevada y ni se ofrecía instalar el SDK | Sin SDK se baja la misma versión ya publicada por GitHub, verificada; winget se busca también en el alias de la cuenta y en App Installer |
| 3 | «Kingston SSD Manager version 1.1.2.6» «Al día — Coincide con la última versión publicada en winget» junto a la 1.5.6.5 del mismo paquete | `winget list` no da versión disponible para la copia anterior y se tomaba la instalada como «la última» | La fila dice con qué copia más nueva convive (sigue «Al día»: ni winget ni Vigía la ofrecen) |

Visto en los registros y sin cambios: «COM … DISP_E_TYPEMISMATCH» (fabricante del equipo y Centro de seguridad) dejó de aparecer después
del 21/11 (ya arreglado); «api.github.com respondió 403 rate limit exceeded» el 23/11 (muchas búsquedas seguidas): la versión nueva de
Actualizador ya no usa la API (2.0.34) y el contraste con GitHub guarda lo consultado (1 a 13 consultas por búsqueda); el día del
diagnóstico no hubo ninguno.

## Seguridad

| # | Riesgo | Causa | Arreglo |
|---|---|---|---|
| 1 | Escalada/movimiento lateral: el Actualizador elevado aprovisionaba para TODAS las cuentas una app que podía haber registrado un usuario | `Get-AppxPackage -AllUsers -Name <paquete>` sin mirar el origen: una copia en modo desarrollador o firmada por el usuario con ese nombre servía | Sólo `SignatureKind` «Store» o «System» y nunca `IsDevelopmentMode` |
| 2 | Cadena de suministro: el trabajo que publica versiones usaba `actions/checkout@v4` y `actions/setup-dotnet@v4` | Una etiqueta se puede mover a otro código; el trabajo tiene `contents: write` | Fijadas por commit (los de «v4» al 26/11/2026); `persist-credentials: false` |
| 3 | Un error de sintaxis en un guion de PowerShell 5.1 se descubría en el equipo del técnico | Ninguna revisión de los .ps1 antes de publicar | Paso que analiza todos los .ps1 con el analizador de Windows PowerShell 5.1 |

Diseño revisado de lo nuevo:
- **Descarga de la versión publicada (`Get-EntregaPublicada`, corre elevada):** sólo `https://github.com/<repo>/releases/download/`, versión y
  repositorio validados con expresiones estrictas antes de armar la dirección; SHA-256 publicado; entradas del .zip revisadas antes de
  descomprimir (`..`, rutas absolutas, `:`); versión del ejecutable; cada archivo contra `SHA256SUMS.txt` (y `Install-Actualizador.ps1`
  lo repite antes de copiar); carpeta temporal con nombre aleatorio, borrada siempre. No se usa si se pidió firmar con un certificado.
- **Instalador con ventana:** misma verificación que en silencio (SHA-256 del catálogo, tipo de archivo, firma no revocada); sólo con alguien
  frente a la pantalla y con `permitirInstaladoresConVentana`; la desinstalación de la versión anterior sigue en silencio y con el programa
  abierto no se toca nada.

Pendiente conocido (sin cambios): el SHA-256 de cada entrega se publica en el mismo lugar que la entrega (GitHub). Protege de descargas
dañadas o cortadas, no de alguien que controle la cuenta de GitHub; la defensa es firmar con un certificado de firma de código
(`build\Sign.ps1`, `-CertificadoHuella`) y exigir la firma (`Instalar.cmd -ExigirFirma`). Lo mismo vale para la actualización sola.

---

# Auditoría de Actualizador (2.0.41, 24/11/2026)

| # | Síntoma / riesgo | Causa | Arreglo |
|---|---|---|---|
| 1 | Tor Browser 16.0.1 → 16.0.3: «Actualizar» abría www.torproject.org | Estaba en el catálogo sólo para comparar (portable, sin instalador registrado) | Se instala solo: instalador oficial con SHA-256 publicado y firma de The Tor Project, en silencio en la misma carpeta, conservando `Browser\TorBrowser\Data`; vuelve atrás si algo falla; como el usuario si la carpeta es suya |
| 2 | Escalada local: la entrada de «Aplicaciones» podía apuntar a un powershell.exe del usuario | `Install-Actualizador.ps1` (elevado) armaba `UninstallString` con `$env:SystemRoot`, que el usuario puede redefinir en su cuenta | `[Environment]::SystemDirectory` |
| 3 | Escalada local: el borrado al reiniciar (RunOnce, HKLM) y el PowerShell del desinstalador | Ídem, en `Uninstall-Actualizador.ps1` | Ídem |
| 4 | «Desinstalar Actualizador» desde la ventana elevada ejecutaba `%SystemRoot%\…\powershell.exe` | `GetEnvironmentVariable("SystemRoot")` | `SafePaths.Windows` (derivado de la carpeta de sistema) |
| 5 | Guiones de la actualización sola (corren elevados): `chcp` por nombre y `cd /d "%SystemRoot%\System32"` antes de abrir Actualizador | Actualizador quedaba abierto como administrador con la carpeta de trabajo que eligiera el usuario | `chcp.com` y System32 por ruta completa |
| 6 | Opciones › «Abrir la carpeta»: `Process.Start("explorer.exe", …)` elevado | Nombre suelto (se busca primero en la carpeta del programa: en la portable la escribe un usuario común) y Explorador como administrador | `ProcessRunner.OpenForUser` (ruta completa, sin elevar) |

Revisado sin cambios: descargas (HTTPS y dominio en cada redirección, respuestas de texto limitadas a 16 MB), descompresión de .zip
(rutas que salen de la carpeta rechazadas; dependencias de winget con nombre plano y SHA-256), carpetas privadas de descarga (dueño
Administradores, sin herencia, sin enlaces), lanzamientos de procesos (todos por ruta completa) y variables de entorno leídas.
Pendiente conocido: `Instalar.cmd`, `Portable.cmd` y `Publicar.cmd` usan `%SystemRoot%` (los ejecuta a mano el técnico; cmd no tiene otra
forma confiable de ubicar System32 y cada elevación pasa por el aviso de UAC).

---

# Auditoría de Actualizador (2.0.24, 21/11/2026)

| # | Síntoma | Causa | Arreglo |
|---|---|---|---|
| 1 | pc2/pc3/pc4: Vigía lista paquetes que el Actualizador no muestra | Regla de la 2.0.22 (copias viejas de marcos «al día») y filas del servicio ocultas cuando el usuario tenía su propia actualización del mismo paquete | Todo lo que ve winget como servicio aparece, con el nombre de Vigía; un paquete por cuenta puede tener dos filas (la tuya y la del servicio) |
| 2 | Outlook y Teams como opcionales | Se marcaban sólo si el usuario los tenía | Como servicio, todo marcado |
| 3 | «AMD PCI · Falló · Windows no lo aplicó» | Se informaba como falla una decisión de Windows | «Al día» con la explicación, y no se vuelve a ofrecer |

---

# Auditoría de Actualizador (2.0.23, 21/11/2026)

## Errores encontrados

| # | Síntoma | Causa | Arreglo |
|---|---|---|---|
| 1 | Las actualizaciones «como servicio» que salían bien quedaban en rojo; una búsqueda sin novedades repetía la consulta con `--scope machine` | `echo %ERRORLEVEL%> archivo`: con el código 0, cmd lee `0>` como redirección de la entrada; el código se leía -1 | `>archivo echo %ERRORLEVEL%`; prueba que revisa el guion con los códigos 0–9 |
| 2 | Con winget terminado bien y la versión vieja todavía listada, se reinstalaba por la vía directa | Se confirmaba sólo por la lista | Terminó bien = actualizada (con la explicación); reiniciar para terminar = actualizada, requiere reinicio |

## Revisión con los analizadores de .NET (todas las reglas, motor y base)

| Regla | Resultado |
|---|---|
| CA2000 (objetos sin liberar) | `ProcessRunner`: el proceso y los eventos se liberan en `finally`, salvo cuando un hijo sigue usando la salida (a propósito: cerrarla haría fallar el hilo lector). `Semaphore` de la búsqueda: vive lo que dura la búsqueda. Sin fugas reales. |
| CA1508 (código muerto) | Falsos positivos: `aborted` y `abortReason` los cambia el hilo que lee la salida; el criterio de Windows Update cambia dentro del bucle. |
| CA1806 (resultado sin usar) | `MsiCloseHandle` y `GetWindowThreadProcessId`: el resultado no cambia nada. |
| CA5350 (SHA-1) | Sólo para comparar con lo que publica TLauncher (starter4j) y el Catálogo de Microsoft Update, que no publican otro: nunca como única verificación de algo que se ejecuta como administrador. |
| CA5386 (TLS fijo) | A propósito: en Windows 7/8.1 con .NET Framework hay que pedir TLS 1.2 explícitamente; en .NET 8 decide el sistema. |
| CA1309/CA1310 (comparaciones según el idioma) | Sólo para ordenar nombres en pantalla (está bien que sea según el idioma). Las comparaciones de identificadores, rutas y versiones son ordinales. |

## Optimización

Lo de la 2.0.21 (modo liviano, búsquedas guardadas 3 días, prioridad baja, ventana quieta sin nada en curso) más, en esta versión, un
proceso de winget menos por búsqueda (el error 1 hacía repetir la consulta del equipo).

---

# Auditoría de Actualizador (2.0.22, 21/11/2026)

| # | Síntoma (pc7) | Causa | Arreglo |
|---|---|---|---|
| 1 | WindowsAppRuntime 1.4/1.5/1.6 «winget no encontró el paquete instalado» | Como servicio se instalaba con `--scope machine` (paquetes aprovisionados), pero el paquete está en lo que ve SYSTEM | Primero tal cual se vio (sin `--scope`), después `--scope machine` |
| 2 | «…y su manifiesto no publica un paquete MSIX para x64» | El respaldo sólo aprovisionaba `.msix`; Windows App Runtime se publica con un instalador `.exe` | Instalador oficial del manifiesto, con su SHA-256 y `--quiet` |
| 3 | Marcos viejos ofrecidos aunque el usuario tenga la versión nueva | Un marco convive en varias versiones; la vieja es un resto | «Al día» con la explicación |
| 4 | Roblox «terminó (código 0) pero sigue la 0.740» | Sólo se miraba una carpeta, y el instalador como usuario no toca un Roblox instalado en Program Files | Todas sus carpetas; como administrador si está en Program Files; segundo intento con su ventana |

---

# Auditoría de Actualizador (2.0.21, 21/11/2026)

| # | Síntoma | Causa | Arreglo |
|---|---|---|---|
| 1 | pc6: Vigía «3 programas con versión nueva» (Outlook, Teams, PDF24), Actualizador «Nada para actualizar» | Vigía consulta winget como SYSTEM; la 2.0.20 consultaba `--scope machine`, que en ese equipo no mostró lo mismo | winget como SYSTEM (tarea programada de una vez), instalación y confirmación en ese mismo contexto; `--scope machine` queda de respaldo |
| 2 | pc7: Roblox «terminó (código 0)» y sigue la 0.740 | El instalador se reabre a sí mismo y el primer proceso termina al instante | Se espera a la versión nueva, al «SUCCESS» de su registro o a que no quede ningún instalador abierto |
| 3 | Consumo en equipos viejos | Tres procesos de winget juntos, cuatro búsquedas por nombre a la vez (35 procesos en pc6), seis consultas web a la vez, búsquedas guardadas sólo 12 h | Modo liviano automático (≤ 4 procesadores o < 6 GB), búsquedas guardadas 3 días, hilos con prioridad baja, ventana quieta sin nada en curso |

---

# Auditoría de Actualizador (2.0.20, 21/11/2026)

Fuentes: las dos capturas del usuario (la consola de Vigía con 5 paquetes que el Actualizador no mostraba, y Roblox «Falló la
instalación»), y los registros, el historial y el diagnóstico reales de un equipo con Windows 11 26H2 (del 06/11 al 21/11/2026).

## 1. Lo que se encontró

| # | Síntoma | Causa | Arreglo |
|---|---|---|---|
| 1 | **Ningún controlador en la lista, nunca** (0 filas de controladores en todo el historial; «Fabricante del equipo» y «Centro de seguridad: COM 0x80020005» en cada búsqueda, 32 veces) | Las llamadas COM a WMI pasaban `null` en los parámetros opcionales (VT_EMPTY); WMI no lo convierte a objeto y responde DISP_E_TYPEMISMATCH. El error quedaba en `ctx.DriversError` sin mostrarse | Parámetros omitidos con `Missing.Value` (como VBScript); si COM falla igual, `Get-CimInstance` de PowerShell; si tampoco, la búsqueda lo dice |
| 2 | VCLibs, Windows App Runtime 1.4/1.5/1.6 y Teams con versión nueva en Vigía, no en el Actualizador | Vigía corre winget como servicio y ve los paquetes aprovisionados; `winget upgrade` del usuario no | `winget upgrade --scope machine` en paralelo; instalación con `--scope machine` y, si winget no puede (Windows < 24H2), el paquete oficial aprovisionado con su SHA-256 |
| 3 | Roblox «Desconocida → 0.747 · winget no tiene una actualización aplicable» | El paquete de winget pasó de «exe» a «portable»; Roblox no registra versión | Versión de `RobloxPlayerBeta.exe`; instalador oficial (setup.rbxcdn.com, SHA-256 y firma de Roblox) como el usuario |
| 4 | Claude «Falló: no cambió nada» con la última ya abierta | Squirrel: la entrada de «Aplicaciones» quedó en 6.6395.0.0; la versión real está en la carpeta «app-6.28899.2» | Esa carpeta es la versión del producto: «Al día» con la explicación |
| 5 | Ollama «Se abrió su página (ollama.com)» | Instalado sólo para el usuario y la ventana como administrador: no se instalaba «por seguridad» | El instalador verificado se ejecuta como el usuario, sin elevar (no hay nada que ganar) |
| 6 | «Descarga de hasta 90,5 GB» | Desde 24H2 Windows informa la suma de todos los archivos de la acumulativa | Más de 10 GB: el mínimo informado o «según lo que le falte» |
| 7 | La acumulativa de seguridad (26300.9974) falló con 0x800F081F y la preliminar (26300.10075) se instaló después | Se instalaban en el orden de la lista; la fila decía sólo «Error (0x800F081F)» | La más nueva primero, la anterior queda «incluida»; los errores comunes dicen qué hacer (DISM, sfc, espacio) |

## 2. Por qué no se vio antes

- **WMI (1)**: las pruebas corren en Linux, sin COM, y simulaban las filas de WMI ya leídas. Ninguna prueba ejercitaba la llamada
  real, y la falla quedaba en una propiedad que nadie mostraba. Cambio de proceso: lo que no se puede leer se dice en las notas de
  la búsqueda; y la consulta tiene una segunda vía (PowerShell) con su propio registro («la consulta se hizo con PowerShell»).
- **Aprovisionados (2)**: el Actualizador nunca miró el alcance «equipo» de los paquetes MSIX; se notó al comparar con Vigía.
- **Roblox (3)**: el cambio de tipo del paquete en winget-pkgs es de las últimas semanas.

## 3. Lo que se revisó y está bien

La cadena de confianza de lo que se ejecuta (HTTPS en cada salto, SHA-256, firma del mismo editor, nunca una versión más vieja,
archivo bloqueado entre la verificación y la ejecución, carpeta privada de descargas) se mantiene. Lo nuevo la respeta:

| Nuevo | Control |
|---|---|
| Como el usuario | Mismo SHA-256 y misma firma que antes; se ejecuta la copia de lo ya verificado, sin elevar. Sólo si el programa es del usuario actual y está en una carpeta suya |
| Aprovisionar | Paquete del manifiesto de winget-pkgs, sólo HTTPS y con su SHA-256; Windows exige la firma de Microsoft |
| Roblox | Sólo `https://*.rbxcdn.com/…-RobloxPlayerInstaller.exe`, SHA-256 del catálogo, firma válida de Roblox Corporation |
| PowerShell para WMI | La consulta va entre comillas simples de PowerShell (las comillas internas duplicadas); sólo consultas `SELECT prop, … FROM Clase` |

## 4. Pendiente (requiere Windows)

- Que WMI responda con los parámetros omitidos (si no, el registro dirá que se usó PowerShell).
- `winget upgrade --scope machine` sobre los paquetes aprovisionados del equipo de la captura.
- El instalador de Roblox con `-install -silent` (los interruptores del manifiesto oficial mientras fue «exe»).
- La carpeta «app-…» de Claude (se supone Squirrel por su `ProductCode: AnthropicClaude`).

---

# Auditoría de Actualizador (2.0.18, 15/11/2026)

## 1. Por qué la 2.0.17 «seguía haciendo lo mismo»

| | |
|---|---|
| Síntoma | CrystalDiskInfo 9.9.4 → 9.9.5: «Pendiente: Se abrió su página (crystalmark.info)», fila opcional. |
| Causa | El instalador nunca se encontraba: **SourceForge responde una página (JavaScript) a todo User-Agent de navegador**, y Actualizador se presentaba como `Mozilla/5.0 (compatible; Actualizador…)`. El botón de crystalmark.info pasa por una página «Redirecting…» con un enlace `…/CrystalDiskInfo9_9_5.exe/download` que no se reconocía como instalador. |
| Por qué las pruebas no lo vieron | Simulaban que esos servidores redirigen al archivo. Se escribió la simulación según lo que se suponía, no según lo que hacen. |
| Arreglo | User-Agent de herramienta de descargas para SourceForge; páginas intermedias (botón, JavaScript, espejo de SourceForge); segunda búsqueda al tocar «Actualizar»; la fila dice el motivo si igual abre la página. |
| Cambio de proceso | Las simulaciones nuevas reproducen el comportamiento documentado de cada servidor (según el User-Agent). Cada vez que algo «no se puede instalar solo», la fila guarda y muestra el motivo: un error así se diagnostica con una captura. |

## 2. Lo que se revisó

### Cadena de confianza de lo que se ejecuta como administrador

| Control | Estado |
|---|---|
| HTTPS en cada salto de redirección, puerto estándar, sin IPs ni nombres locales | Correcto (`Http.Send`, `SiteVersions.IsSecureDownload`) |
| Firma Authenticode válida, no revocada, del mismo editor que lo instalado | Correcto; elevado, no se toman como referencia carpetas que pueda escribir un usuario común |
| SHA-256 del catálogo (winget, GitHub, fabricante) | Correcto; el de winget-pkgs sólo para la misma versión |
| Sin firma (el autor no firma) | Sólo si lo instalado tampoco tiene firma (comprobado), mismo producto y exactamente la versión publicada; apagable por política |
| Nunca una versión anterior a la instalada | Correcto (instalador, winget y página oficial) |
| Archivo bloqueado entre la verificación y la ejecución | Correcto (se mantiene abierto hasta que arranca el instalador) |
| Carpeta privada de descargas (elevado: `C:\Windows\Temp`, dueño Administradores, sin herencia) | Correcto |
| Páginas abiertas con el navegador del usuario, sin elevar (`explorer.exe`) | Correcto; sólo `http/https` validados |
| Tamaño máximo de descarga (2 GB) y de respuesta de texto (16 MB) | Correcto |

### Datos de internet (páginas, JSON, YAML)

| Riesgo | Antes | 2.0.18 |
|---|---|---|
| Expresiones regulares sobre páginas de hasta 4 MB sin tiempo máximo (ReDoS) | Una página armada a propósito podía trabar la búsqueda | **2 s por expresión en todo el proceso** (`AppInfo.HardenProcess`, primera línea de cada ejecutable) |
| JSON anidado | Tope de 256 niveles | Sin cambios |
| YAML (manifiestos de winget-pkgs) sin tope de anidamiento ni tamaño | Miles de niveles agotaban la pila (el proceso se cierra sin aviso) | **Tope de 64 niveles y 2 MB** |

### Velocidad (2.0.17, se mantiene)

Microsoft Store sin novedades: menos de 1 minuto (antes, siempre 4) y en paralelo con lo demás; confirmación después de instalar: 20 s
como mucho (antes, 90 s con el instalador oficial y más de un minuto con winget).

## 3. ¿Reescribir desde cero?

No conviene, y no habría evitado este error.

- La estructura ya es la correcta: `Core` (sin interfaz) → `Engine` (proveedores independientes detrás de `IUpdateProvider`) → tres
  ejecutables (ventana, consola, aviso). Dependencias en un solo sentido, sin paquetes de terceros.
- Las 16 versiones anteriores dejaron más de 3.700 pruebas con casos reales (TeamViewer de 32 bits, LibreOffice 1603, GOM Player que
  cambió de nombre, Double Commander sin firma…). Una reescritura los perdería y volvería a cometer esos errores.
- El error de la 2.0.17 no era de estructura: era una suposición sobre un servidor externo que nunca se comprobó. Eso se corrige
  probando contra el comportamiento real y mostrando el motivo de cada fallo, que es lo que hace esta versión.

## 4. Pendiente (requiere Windows)

- Instalación real de CrystalDiskInfo 9.9.5 (descarga desde SourceForge, firma, instalación silenciosa).
- La consulta a la cola de Microsoft Store (PowerShell): si fallara, se mide por tiempo (2 minutos).
