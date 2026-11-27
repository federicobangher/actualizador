# Seguridad de Actualizador 2.0

Actualizador instala software como administrador, así que cada paso que toca el equipo está pensado para que un
atacante —o un error— no pueda aprovecharlo.

## Qué se instala

| Control | Detalle |
|---|---|
| Origen verificado | winget (manifiestos de Microsoft), sitios oficiales por HTTPS, Windows Update y Microsoft Store. Nada de «sitios de descargas». |
| Hash | Lo descargado se compara con el SHA-256 publicado por la fuente (winget, GitHub, fabricante). Si no coincide, no se ejecuta. |
| Firma Authenticode | Un instalador bajado de una página sólo se ejecuta si lo firma **el mismo editor** que el programa instalado (y la firma es válida, con cadena de confianza). |
| Instalador de la página oficial (2.0.17) | Cuando la página oficial publica una versión que winget todavía no tiene, se ubica su instalador sin ejecutar nada (se piden sus primeros bytes): el manifiesto de esa versión en winget-pkgs (con su SHA-256), la dirección del instalador de la versión anterior con la versión nueva, o un enlace de la página o de su página de descargas (cada salto por HTTPS; nunca otra edición, un portable ni una beta). Se ejecuta sólo con firma válida, no revocada y del mismo editor que lo instalado —o con el SHA-256 del catálogo de winget—, si dice ser de este programa y no es de una versión anterior a la instalada. Sin firma (el autor no firma) sólo si lo instalado tampoco la tiene (comprobado), la página se leyó por HTTPS y el archivo es de este producto y exactamente de la versión publicada; `permitirInstaladoresSinFirma` lo prohíbe. Sobre un programa instalado sólo para el usuario, se ejecuta como el usuario, sin elevar (2.0.20). Si no se puede verificar, no se ejecuta y se abre la página. |
| Cola de Microsoft Store (2.0.17) | Para saber cuándo terminó la Store se lee su cola de instalaciones (`AppInstallManager.AppInstallItems`) con Windows PowerShell: sólo lectura, no inicia, pausa ni cancela nada. |
| Instalador oficial de un paquete de winget (2.0.22) | Si un paquete para todo el equipo no se pudo actualizar con winget y su manifiesto no trae un `.msix` (Windows App Runtime), se ejecuta como administrador el instalador del mismo manifiesto de winget-pkgs: sólo por HTTPS, sólo si su SHA-256 coincide con el del catálogo (lo mismo que exige winget), con los interruptores del manifiesto (`--quiet`) y nunca si el manifiesto pide no elevarlo o es sólo para el usuario. |
| winget como servicio (2.0.21) | Para ver lo mismo que el agente de Vigía, winget se ejecuta como SYSTEM con una tarea programada de una sola vez (oculta, sin desencadenador, se borra al terminar). Sólo desde la ventana como administrador. La tarea ejecuta un guion de una carpeta privada (dueño Administradores, sólo Administradores y SYSTEM, sin herencia), y el winget.exe tiene que ser el de App Installer en `Program Files\WindowsApps` —la carpeta de la app registrada o el destino del alias genuino—; nunca el alias de la carpeta del usuario. Los identificadores van validados como siempre. |
| Programas sólo del usuario (2.0.20) | Si el programa está instalado sólo para el usuario actual (en una carpeta suya) y Actualizador corre como administrador, el instalador —verificado igual que siempre: SHA-256, firma válida del mismo editor— se copia a una carpeta temporal del usuario y se ejecuta **como el usuario, sin elevar** (lo abre el Explorador). Antes no se instalaba y se abría su página. Lo que un programa del usuario pudiera cambiar (su carpeta, su entrada en «Aplicaciones», la copia) no le da más permisos de los que ya tiene: por eso, sólo en este caso, esos datos sirven de referencia para la firma. |
| Apps para todos los usuarios (2.0.20) | Los paquetes MSIX aprovisionados (VCLibs, Windows App Runtime, Teams…) se actualizan con `winget upgrade --scope machine`. Si winget no puede aprovisionar (Windows anterior a 24H2, 0x8A150113), se baja el paquete del mismo manifiesto de winget-pkgs por HTTPS, se acepta sólo con su SHA-256, y se aprovisiona con `Add-AppxProvisionedPackage` (Windows exige además la firma de Microsoft del paquete). |
| Roblox (2.0.20) | Su paquete de winget pasó a «portable» y winget ya no puede actualizar lo instalado. Se usa el instalador del mismo manifiesto (`RobloxPlayerInstaller.exe` de setup.rbxcdn.com, sólo por HTTPS), con el SHA-256 del catálogo y firma válida de Roblox Corporation, ejecutado como el usuario (`-install -silent`): Roblox vive en la carpeta del usuario. |
| TLauncher (2.0.19) | Se hace lo mismo que su arranque (starter4j, de código abierto) cuando se toca «Actualizar» en su ventana, sin ejecutar nada: se lee `appConfig.json` de repo.tlauncher.org (o repo.fastrepo.org) por HTTPS, se bajan de esos dos servidores (nunca de otro ni por HTTP) los archivos de la versión nueva y sólo se guardan si su tamaño y su SHA-1 coinciden con los publicados; recién con el de la aplicación verificado se anota la versión en `starter.json` (con copia del anterior). Todo dentro de `AppData\Roaming\.tlauncher\starter`: rutas con `..`, absolutas o con nombres reservados se rechazan, y no se escribe si hay un enlace (unión o enlace simbólico) en el camino. TLauncher tiene que estar cerrado (se ofrece cerrarlo). No se usa su ventana de actualización, que ofrece programas de terceros. |
| Tor Browser (2.0.41) | La versión y el instalador salen del archivo de The Tor Project para la plataforma instalada (aus1.torproject.org). Se baja sólo de torproject.org por HTTPS, con el nombre oficial de esa versión y arquitectura; se ejecuta sólo si su SHA-256 coincide con `sha256sums-signed-build.txt` de la misma carpeta y su firma es válida, no revocada y de The Tor Project. Con Tor Browser cerrado (sólo lo que corre desde su carpeta), un guion aparta la carpeta, instala en silencio en el mismo lugar (`/S /D=`), y si quedó la versión nueva copia `Browser\TorBrowser\Data` (con robocopy `/XJ`: sin seguir uniones; sin los archivos de fábrica nuevos) y borra la anterior; si no, la devuelve a su lugar. En una carpeta del usuario el guion corre **como el usuario** (lo abre el Explorador): un proceso elevado no mueve, copia ni borra donde un programa del usuario podría plantar un enlace. Rutas con `% & | < > ^ ! "` no entran al guion. |
| Sólo se compara (2.0.16) | TLauncher hasta la 2.0.18 (su versión sale del mismo `appConfig.json` de repo.tlauncher.org que consulta su arranque; la instalada, del `starter.json` de su perfil, sólo lectura y nunca fuera de `AppData\Roaming`) y MSI Afterburner (la versión «Final» de msi.com): no se ejecuta ningún instalador. El actualizador de TLauncher ofrece programas de terceros: la fila lo advierte. |
| Instalador oficial fijo (2.0.15) | Fortinet no publica el número de versión en su página: la versión sale del instalador al que llevan sus botones (links.fortinet.com → filestore.fortinet.com), del nombre del archivo o de sus recursos, leídos por partes sin ejecutar nada. Se instala ese mismo archivo, sólo por HTTPS desde fortinet.com, con firma válida de Fortinet y nunca una versión más vieja que la instalada. FortiClient administrado por EMS sólo se compara. |
| Sin firma desde GitHub (2.0.14) | Sólo si el repositorio sale del manifiesto de winget, el instalador no tiene ninguna firma, lo instalado tampoco (comprobado), el SHA-256 es el que publica GitHub y el .msi tiene el mismo UpgradeCode que lo instalado y la versión anunciada (o el .exe dice ser de este programa). |
| Controlador de NVIDIA (2.0.13) | Versión del servicio de la página de descargas de NVIDIA para la placa instalada; instalador de download.nvidia.com, sólo con firma válida de NVIDIA Corporation, ejecutado **con su ventana** (no en silencio). Se confirma por la versión instalada. |
| Sin firma (2.0.12) | Sólo 7-Zip, porque su autor no firma: se ejecuta el instalador que enlaza 7-zip.org si **no tiene ninguna firma** (una inválida o revocada se rechaza), su SHA-256 coincide con el que publica el autor para ese archivo (github.com/ip7z/7zip) y dice ser 7-Zip. Sin ese SHA-256, no se instala. |
| Versión real | Después de instalar se vuelve a leer la versión: un instalador que «termina bien» sin cambiar nada no cuenta como actualizado. Nunca se instala una versión más vieja que la que hay. |
| Descargas | En carpetas temporales propias (no compartidas), sin seguir enlaces (junction/symlink), y se borran al terminar. |
| Política | Bloqueos, editores aprobados y fuentes permitidas se comprueban de nuevo en cada instalación (`PolicyRefusal`), no sólo al mostrar la lista. |

## Privilegios

- `Actualizador.exe` pide `requireAdministrator` (2.0.8): siempre se abre como administrador; un usuario común tiene que
  escribir la contraseña de un administrador en el aviso de UAC.
- La ventana no busca nada al abrirse y no instala nada que no esté marcado: no marca filas sola ni vuelve a buscar e
  instalar lo que apareció después (eso sigue disponible sólo en la consola, con `actualizar`).
- `Actualizador.Cli.exe` y `Actualizador.Aviso.exe` son `asInvoker`: nunca piden elevación por su cuenta.
- La revisión programada corre con la cuenta del usuario, sin privilegios (LeastPrivilege) y **no instala nada**.
- Desde la ventana elevada, las páginas y carpetas se abren en la sesión normal (no queda un navegador como administrador).
- Se instala en `C:\Program Files\Actualizador` (sólo administradores escriben). `%ProgramData%\Actualizador` se crea con
  dueño Administradores y sin herencia; su subcarpeta `auditoria` no la puede leer un usuario común.

## Trazabilidad

**Visor de eventos** (registro Aplicación, origen `Actualizador`):

| Id | Nivel | Evento |
|---|---|---|
| 1000 | Información | Búsqueda terminada (cuántas actualizaciones, de qué tipo) |
| 1001 / 1002 | Información | Actualizador instalado / desinstalado |
| 1100 / 1103 | Información | Comienzo y fin de una tanda de instalación |
| 1101 / 1102 | Información / Advertencia | Una actualización instalada / fallida (nombre, versiones, fuente, resultado) |
| 1200 | Advertencia | Instalación rechazada por la política |
| 1201 / 1202 | Información / Advertencia | Política aplicada / política con problemas (p. ej. archivo con permisos inseguros) |
| 1300 | Error | El registro de cambios fue alterado |

**Registro de cambios** (`%ProgramData%\Actualizador\auditoria\auditoria-AAAA-MM.jsonl`): una línea JSON por evento con
número de secuencia, fecha UTC, equipo, usuario, si era administrador, versión de Actualizador, el evento y un
**HMAC-SHA256 que incluye el HMAC de la línea anterior**. Cambiar, borrar, agregar o reordenar una línea rompe la cadena.
La clave (32 bytes al azar) está en `clave.key`, en esa carpeta que sólo leen los administradores. No se cifra con DPAPI
(2.0.11): descifrar con `CryptUnprotectData` es lo que hacen los programas que roban contraseñas, y el escudo de
comportamiento de Avast cortaba Actualizador por eso (IDP.Generic); con DPAPI de la máquina, además, cualquier proceso del
equipo la descifraba igual. Un registro de la 2.0.10 o anterior se guarda entero, sin descifrar, en `auditoria\anterior-…`.
Verificación: «Más › Verificar el registro de cambios» o `Actualizador.Cli verificar-registro` (código 2 si está alterado).
Sin privilegios, el registro va a `%LOCALAPPDATA%\Actualizador\auditoria`, con su propia clave.

**Antivirus.** Actualizador no usa las funciones que la protección de identidad de los antivirus vigila: no descifra con
DPAPI, no lee perfiles Wi-Fi, ni cuentas o grupos locales, ni la política de contraseñas, ni las conexiones abiertas
(2.0.11: se sacó lo que quedaba de la época del auditor). Una prueba lo controla en el código.

El **historial** por usuario (`%LOCALAPPDATA%\Actualizador\historial.jsonl`) es lo que muestra la ventana; el registro de
cambios es la evidencia para auditorías.

## Datos

- Nada sale del equipo salvo las consultas de versiones a las fuentes (winget, fabricantes, Microsoft). No hay
  telemetría.
- Registros, diagnósticos y exportaciones pasan por `Redactor`: contraseñas, tokens, claves y credenciales en URLs se
  reemplazan antes de escribirse.
- La exportación CSV neutraliza las celdas que empiezan con `= + - @` (inyección de fórmulas en Excel).
- La política sólo se lee de lugares que controla un administrador (HKLM\SOFTWARE\Policies o un archivo que sólo
  pueden modificar administradores). Un catálogo de fuentes remoto sólo por `https://`.

## Cadena de suministro

- Sin paquetes NuGet de terceros: sólo .NET 8 y APIs de Windows.
- `build\Publish.ps1` escribe `SHA256SUMS.txt`; `Install-Actualizador.ps1` verifica cada archivo antes de copiarlo y, con
  `-ExigirFirma`, exige firma Authenticode válida en los ejecutables.
- `build\Sign.ps1` firma con SHA-256 y sello de tiempo RFC 3161; con `-CertificadoHuella` la clave privada no sale del
  almacén de certificados ni del token.
- **Firma propia de las versiones (2.0.46).** GitHub compila y publica; la actualización sola exige además `<entrega>.firma`: RSA de
  3072 bits, PKCS#1 v1.5 con SHA-256, sobre «Actualizador|1|archivo|versión|sha256» (ata el archivo a su nombre y su versión: no sirve para
  hacer pasar una versión vieja por nueva ni un archivo por otro). La clave privada está sólo en la PC de quien firma (almacén de
  certificados de Windows, con contraseña, respaldo `.pfx` aparte); las públicas de confianza están compiladas dentro del programa
  (`res\claves-de-firma.txt` como recurso), no en un archivo al lado del ejecutable. La firma se verifica al consultar y otra vez justo
  antes de instalar, contra el SHA-256 de lo bajado (lo guardado en `version_nueva.json` no decide). `Instalar.cmd` sin SDK también la exige.
  Límite: una instalación NUEVA hecha desde una copia sacada de un GitHub ya comprometido no tiene contra qué comparar; los equipos ya
  instalados, sí. Si se pierde la clave y su respaldo, los equipos sólo se pueden pasar a una clave nueva instalando a mano.

## Reportar un problema de seguridad

Escribí al responsable del proyecto con los pasos para reproducirlo; no lo publiques hasta que haya una versión corregida.
