# Cambios

## 2.0.36

Pedido del usuario (23/11/2026): «que no tenga que descargar nada, como Virtual Pet».

- **Las versiones nuevas se publican solas desde GitHub.** El código está en github.com/federicobangher/actualizador; cuando llega una versión
  que todavía no está publicada, GitHub arma Actualizador en Windows, corre las pruebas y publica la versión (la misma entrega que Publicar.cmd). Los
  equipos la instalan solos al abrir. Publicar.cmd sigue sirviendo para publicar desde una PC.
- **Publicar.cmd decía «No se inició sesión en GitHub» después de «Authentication complete».** Al iniciar sesión, gh configura Git y, si
  Git no está instalado, termina con error aunque la sesión quedó iniciada. Ahora se comprueba la sesión, no el código de salida.
- Sin versiones publicadas, el aviso dice «todavía no hay versiones publicadas» y cómo publicarlas.
- Pruebas nuevas (Pruebas2036.cs).

## 2.0.35

Relevamiento pedido por el usuario (23/11/2026) antes de probar la actualización automática, sobre todo de lo que todavía no corrió en
Windows.

- **«Marcar todo» volvía a marcar lo que ya falló o no quedó.** Las protecciones de hoy (lo que ya se instaló y vuelve a aparecer, lo que
  falla siempre igual, lo «para todos los usuarios» que ya falló) dejaban esas filas sin marcar, pero «Marcar todo» marca también lo
  opcional: con un clic volvían a repetirse. Ahora se marcan sólo a mano (el detalle lo dice); lo que falló una sola vez sí se reintenta.
- **Actualización automática: si la instalación sola no quedó, no se repite en cada apertura.** El guion anota el código del instalador; si
  al volver a abrir sigue la versión anterior (Smart App Control, un archivo en uso, el equipo se apagó), Actualizador dice por qué y
  dónde está el registro, y no vuelve a bajarla e intentarla sola (se puede reintentar desde «Más»).
- **Publicar.ps1 se cortaba en Windows PowerShell 5.1** antes de empezar: con ErrorActionPreference «Stop», redirigir lo que gh escribe en
  stderr («gh auth status» lo hace aun cuando sale bien) es un error que corta el guion. Las consultas a gh y la publicación corren con
  «Continue».
- El .sha256 de la entrega se pide como cualquier archivo («*/*»).
- Pruebas nuevas (Pruebas2035.cs).

## 2.0.34

Al crear el proyecto federicobangher/actualizador (23/11/2026), GitHub respondió «API rate limit exceeded» para la conexión del usuario.

- **La versión nueva de Actualizador se busca sin la API de GitHub.** La API permite 60 consultas por hora sin clave por conexión a internet,
  y todos los equipos de una red comparten la misma: la búsqueda de versión nueva podía fallar justo donde hay más equipos. Ahora se
  usa la dirección común «github.com/…/releases/latest» (GitHub redirige a la última versión) y la entrega y su .sha256 se bajan por
  «…/releases/download/…», que no tienen ese límite.

## 2.0.33

Pedido del usuario (23/11/2026): «en la parte de opciones, una opción de buscar actualización del programa».

- **Opciones › «Buscar actualización de Actualizador»** (antes «Comprobar ahora»): consulta ya y, si hay una versión nueva que se puede
  instalar sola, ofrece instalarla (guarda las opciones, Actualizador se cierra y se vuelve a abrir solo).
- **Más › «Buscar actualización de Actualizador…»**, siempre a mano: dice si está al día o, si hay una nueva, ofrece instalarla.
  Sin esperar la consulta automática de cada hora.

## 2.0.32

Pedido del usuario (23/11/2026): «¿podemos hacer que tenga actualización algo así como hicimos con Virtual Pet? es cansador estar borrando
e instalando cada vez que hay un error». Elegido: GitHub, como Virtual Pet.

- **Actualizador se actualiza solo.** Al abrir (como mucho una consulta por hora) mira la última versión publicada en GitHub
  (federicobangher/actualizador, o lo que diga Opciones). Si es más nueva: baja «Actualizador-x.y.z.zip», la acepta sólo con el SHA-256
  publicado y sólo desde github.com, la descomprime en una carpeta privada (sin rutas que se salgan), comprueba que traiga la versión
  anunciada y la instala en silencio con el instalador de siempre (verifica SHA256SUMS.txt, quita la anterior, comprueba que abra y, si no,
  vuelve atrás). Actualizador se cierra y se vuelve a abrir solo, en menos de un minuto. Si está buscando o instalando, espera: queda en
  «Más › Instalar la versión nueva…». La portable sólo avisa.
- **Publicar.cmd**: publica la versión de la carpeta en GitHub en un solo paso (arma la entrega .7z y la .zip con su SHA-256, instala
  GitHub CLI si falta, pide iniciar sesión la primera vez, crea el proyecto si no existe y sube la versión con las notas de este archivo).
- Opciones › «Versiones nuevas de Actualizador»: vacío ahora es el proyecto de GitHub de siempre (antes, no consultar nada).
- Pruebas nuevas (Pruebas2032.cs).

## 2.0.31

Grabación del usuario (23/11/2026, 12:57, con la 2.0.30): «High Definition Audio Device» seguía en «Disponible (opcional)», 10.0.26100.1 →
10.0.1.42, con «No se instaló: sólo se aplica forzado y no se marcó "Instalar forzados"». El 22/11 se había instalado el controlador del
fabricante y Windows siguió con el genérico incluido con Windows.

- **Ya no se ofrece nada para instalar forzado.** Era el último caso: el controlador del fabricante en lugar del genérico de Windows, cuando
  Windows ya lo rechazó una vez. Sin «forzar» no hacía nada, y forzado Windows suele volver al suyo. Ahora queda «Al día» con el motivo
  (Windows lo considera mejor para ese dispositivo, y funciona) y no se vuelve a ofrecer; la ventana de confirmación ya no muestra
  «Instalar forzados». Tampoco se guarda el paquete para forzarlo después.
- Las fechas de «se instaló el …» se muestran como dd/mm/aaaa.
- 5 comprobaciones nuevas (Pruebas2030.cs).

## 2.0.30

Captura del usuario (23/11/2026, 12:38, con la 2.0.29): winget como servicio ya anda (el origen dice «como servicio, lo mismo que ve
Vigía»), pero «Microsoft.MicrosoftOfficeHub (para todos los usuarios)» quedó «No verificable» con un detalle falso («No figura en winget
ni está en el catálogo de fabricantes…») y Vigía la sigue listando.

- **El detalle de una fila «para todos los usuarios» ya no se reemplaza** por la explicación genérica de lo que no figura en winget.
- **La falla anotada se recuerda por vía** (como servicio o «--scope machine»). La de Office Hub era de la vía de respaldo, de cuando winget
  como servicio no arrancaba en ese equipo; ahora que anda, se intenta una vez como servicio, que es como lo actualizaría el agente de Vigía.
  Si tampoco se puede, queda «sin verificar» con el motivo real y no se reintenta.
- 7 comprobaciones nuevas (Pruebas2030.cs).

## 2.0.29

Grabación y captura del usuario (23/11/2026, 12:21, con la 2.0.28): «en Vigía dice 2 programas, el Actualizador sólo detecta 1 y encima da
error». Vigía: **Microsoft.MicrosoftOfficeHub** (Microsoft.365Copilot) 19.2506.56051.0 → 19.2609.33020.0 y **Microsoft.WindowsAppRuntime.1.6**
1.6.6 → 1.6.9. El Actualizador: sólo Office Hub, con origen «winget (winget, todo el equipo)», y «Falló la instalación»: «winget no tiene una
actualización aplicable… y su manifiesto no publica un paquete MSIX para x64; tampoco un instalador que se pueda ejecutar en silencio».

- **winget como servicio no arrancaba en ese equipo** (el origen lo delata: «todo el equipo» es la vía de respaldo). winget.exe ejecutado
  como SYSTEM, fuera de su paquete, necesita las bibliotecas de Visual C++ (msvcp140.dll, vcruntime140.dll) y las busca en System32: sin
  el «Visual C++ 2015-2022 Redistributable» no arranca (0xC0000135). Ahora el guion pone primero en el PATH la carpeta del paquete de
  Microsoft que las trae (Microsoft.VCLibs.140.00.UWPDesktop, en Program Files\WindowsApps, sólo si está ahí) y se ubica en la carpeta de
  winget.
- **Si igual no anda, se dice** en los detalles de la búsqueda, con el motivo (antes quedaba sólo en el registro).
- **Windows App Runtime 1.6 de todo el equipo no aparecía con la vía de respaldo («--scope machine»)**: se la tomaba por la misma copia que
  la del usuario. Un paquete de Microsoft con una copia por cuenta ahora tiene su fila aparte también ahí.
- **Office Hub para todos los usuarios no tiene con qué actualizarse**: winget no tiene una actualización aplicable y su manifiesto no trae
  ni un MSIX ni un instalador silencioso. Después de comprobarlo una vez, la fila pasa a «Al día y sin verificar» con la explicación
  (es la copia que reciben las cuentas nuevas; la de cada usuario la actualiza la Store; el agente de Vigía la sigue listando porque su
  «winget upgrade --all» tampoco puede) y no se reintenta en rojo.
- 29 comprobaciones nuevas (Pruebas2029.cs).

## 2.0.28

Relevamiento pedido por el usuario (23/11/2026), después de 4 fallas en un día: «espero que mañana no tenga ningún problema en el trabajo».
Las cuatro tenían la misma forma: algo se «instalaba correctamente» (o fallaba siempre igual) y en la búsqueda siguiente volvía a aparecer
igual, marcado, o se descartaba sin decir nada. Cada caso ya tenía su arreglo (2.0.25 a 2.0.27); esto ataja el patrón en todas las fuentes.

- **Red de seguridad con el historial de este equipo, para todas las fuentes** (programas, controladores, Windows Update, Store): si lo
  ofrecido (mismo tipo, nombre y versión) ya se instaló bien en los últimos 30 días y vuelve a aparecer, la fila sigue pero no se marca
  sola y dice cuándo se instaló y qué suele pasar; si quedó «requiere reiniciar» y el equipo todavía no se reinició, queda «requiere
  reinicio» y no se reinstala. Si ya falló 2 veces con esa versión, no se marca sola y muestra el último error; si falló una, se reintenta
  y lo dice. Lo más reciente manda (falló y después se instaló bien: cuenta lo bien instalado).
- **Nada termina la tanda sin resultado**: lo que su fuente no llegó a procesar (quedaba «En cola» o «Instalando») queda «Falló» con el
  motivo.
- **Sin administrador, los controladores y Windows Update que se sacan de la tanda lo dicen** en la fila y en la barra de estado (antes,
  si era lo único marcado, no pasaba nada).
- El archivo de lo que se recuerda de este equipo (controladores no aplicados, pendientes de reinicio, fallas para todos los usuarios) lo
  escriben varias partes: ahora con un candado, para que dos a la vez no se pisen.
- 23 comprobaciones nuevas (Pruebas2028.cs).

## 2.0.27

Grabación del usuario (23/11/2026, 11:50, con la 2.0.26): en cada pasada aparecían las mismas tres filas, se «instalaban» y volvían a
aparecer.

- **Dos «HP Printer (BIDI)» iguales** (Windows Update, `usb\vid_03f0&pid_5a2a&rev_0100&mi_05`, 1.0.0.237): Windows Update publica a veces el
  mismo controlador dos veces. Se muestra una fila.
- **«Instalada correctamente» y en la búsqueda siguiente otra vez «Sin controlador»**: era un controlador para un dispositivo que no tiene
  ninguno (casi siempre una impresora o un escáner USB desconectado o apagado, o una función suya que no se usa). Windows Update lo instala
  en el equipo pero ningún dispositivo lo toma, y lo sigue ofreciendo. Ahora se anota: si vuelve a aparecer, queda «Al día» con la
  explicación (al conectar el dispositivo, Windows lo toma solo) y no se reinstala en cada pasada.
- **«Microsoft.MicrosoftOfficeHub (para todos los usuarios)» fallaba igual en cada pasada.** Si una versión ya no se pudo actualizar para
  todos los usuarios en este equipo, la fila sigue (es lo que lista Vigía) pero no se marca sola y dice por qué falló la vez anterior; se
  puede marcar a mano. Cuando se publica otra versión, se vuelve a intentar sola. La app de cada usuario la sigue actualizando la Store.
- 15 comprobaciones nuevas (Pruebas2027.cs).

## 2.0.26

Grabación del usuario (23/11/2026, 11:20, con la 2.0.25): «mejoró la velocidad pero sigo con el mismo error». **AMD Radeon(TM) Vega 3
Graphics**: instalada la 31.0.21924.61 (del 11/12/2025, la del fabricante del equipo para ese modelo); el catálogo ofrecía la
31.0.21925.3004 (el genérico de AMD, encontrado sólo por el chip). Al tocar «Actualizar», la ventana preguntaba «Instalar forzados»; sin
marcarlo se cerraba y no pasaba nada: la fila seguía «Disponible», sin resultado, búsqueda tras búsqueda.

- **Ya no se pregunta si forzar un controlador del fabricante del equipo: se comprueba solo.** Windows elige primero por cuán exacto es el
  identificador: el del fabricante del equipo (…&SUBSYS_86F1103C) le gana siempre a uno genérico del chip, aunque sea más viejo; y forzado,
  Windows Update vuelve a poner el del fabricante al reiniciar («dice que instala y cuando reinicio sigue igual»). Ahora, al actualizar, se
  baja el paquete y se mira si también es para este modelo: si lo es, se instala normal; si no, no se instala, la fila queda «Al día» con
  la explicación y no se vuelve a ofrecer.
- **Lo que se saca de la tanda ya no desaparece en silencio**: si algo sólo se aplica forzado y no se marcó «Instalar forzados», la fila
  y la barra de estado lo dicen. (Ese caso queda sólo para el controlador del fabricante del dispositivo en lugar del genérico de Windows,
  cuando Windows ya no lo aplicó una vez.)
- **Si un controlador del catálogo se instaló bien y después de reiniciar volvió el de antes** (Windows Update lo reemplazó), la búsqueda
  siguiente lo dice y no lo ofrece de nuevo.
- 9 comprobaciones nuevas (Pruebas2025.cs).

## 2.0.25

Pedido del usuario (23/11/2026): «los drivers de AMD (ATI, Vega…) no instalan bien en algunos equipos: dan errores, o dicen que instala y
cuando reinicio la PC sigue como que no se actualizó».

- **«Dice que instala y al reiniciar sigue igual».** Una instalación que quedaba «requiere reiniciar» no se recordaba: después de
  reiniciar, si Windows seguía con el controlador de antes, la búsqueda siguiente lo volvía a ofrecer y se instalaba otra vez, sin
  resultado. Ahora se anota (catálogo y Windows Update): mientras el equipo no se reinicia, la fila queda «requiere reinicio» y no se
  reinstala; si ya se reinició y el dispositivo sigue con la versión de antes, queda «Al día» con el motivo (Windows prefiere el que tiene)
  y no se vuelve a ofrecer. Se mira el arranque real del equipo: con el inicio rápido de Windows, «Apagar» y prender no aplica los
  controladores, y la fila lo aclara («Reiniciar», no «Apagar»).
- **La extensión no es el controlador.** AMD (y también Intel) publican en el catálogo, para el mismo identificador de la placa de video,
  el controlador («- Display -») y sus extensiones («- Extension -», «- SoftwareComponent -»), a veces con un número más alto. Se elegía
  la extensión: se instalaba «en el dispositivo», el controlador seguía siendo el mismo y la fila decía «se usa después de reiniciar».
  Ahora los complementos no se comparan con el controlador del dispositivo.
- **pnputil 3010 («reiniciar») es del paquete**, que en AMD trae varios INF (chipset, audio, video): si la salida nombra a este dispositivo
  sólo como «está actualizado», no se le instaló nada y la fila ya no dice «se usa después de reiniciar».
- **Antes de instalar se comprueba que el paquete sea para ESTE dispositivo**, con sus identificadores de hardware y compatibles (del
  registro). Un paquete encontrado por el chip puede ser de otro fabricante de equipos y declarar sólo sus modelos: pnputil lo agregaba al
  almacén, ningún dispositivo lo tomaba y la fila daba error (después de bajar cientos de MB en el caso del video de AMD). Ahora queda
  «Al día» con la explicación, no se instala y no se vuelve a ofrecer. Igual si el paquete sólo trae complementos.
- **AMD y ATI declaran sus placas con la revisión** («PCI\VEN_1002&DEV_15D8&REV_C1», Vega 8): forzar un controlador fallaba con «el paquete
  no declara este dispositivo». Ahora se reconoce el chip con su revisión.
- **Video de AMD instalado con AMD Software (Adrenalin, Radeon Software, Radeon Settings).** AMD Software instala el controlador y sus
  componentes (audio por HDMI, Crash Defender, Controller Emulation) como un conjunto y avisa «no es compatible con el controlador
  instalado» si se reemplaza sólo el controlador. Con AMD Software instalado, el catálogo ya no lo ofrece suelto (queda «Al día» con el
  motivo y cómo se actualiza) y la oferta de Windows Update se muestra sin marcar, con la advertencia. Sin AMD Software, como antes.
- **2.0.24 decía que un controlador que Windows no aplicó «no se vuelve a ofrecer», y sólo se cumplía con los genéricos de Windows**: con
  uno de terceros (la captura de «AMD PCI») volvía a aparecer. Ahora se cumple con todos. Y el paquete ya no se guarda en disco cuando no
  se va a ofrecer forzado (un paquete de video de AMD ocupaba cientos de MB por nada).

Pedido del usuario (23/11/2026, 11:00): «tarda mucho en buscar y en instalar, y consume bastantes recursos en equipos viejos».

- **Catálogo de Microsoft Update: cada página se lee de una pasada.** Cada celda de cada fila se buscaba con una expresión nueva sobre
  la página ENTERA (unas 175 pasadas por página de 25 filas, y una búsqueda lee cientos de páginas). Ahora cada fila se lee dentro de
  su propio `<tr>`: con una página de 208 KB, de 33,8 ms a 0,9 ms (37 veces menos procesador). Con eso, en modo liviano el catálogo
  hace 2 consultas a la vez en vez de 1 (es casi todo espera de la red): con una sola, 160 dispositivos llegaban al límite de 8 minutos.
- **«Todo de una vez» ya no repite toda la búsqueda en cada vuelta.** Después de instalar se vuelve a consultar sólo lo que pudo
  cambiar: los programas siempre; Windows Update y el catálogo si se instaló algo de Windows Update, del catálogo o de la herramienta del
  fabricante; esa herramienta (Dell Command | Update tarda minutos) sólo si se instaló algo con ella; la Store si se instaló algo de la
  Store o de winget. Lo demás se muestra como estaba («… (de la búsqueda anterior)» en el resumen de tiempos).
- **Las páginas oficiales y las versiones de los navegadores leídas en los últimos 20 minutos no se bajan de nuevo** (en memoria, con
  tope de 16 MB; un error se vuelve a intentar). Las vueltas de «todo de una vez» y una segunda búsqueda ya no repiten decenas de pedidos.
- **Windows Update descarga mientras se instala lo demás.** Va último en la tanda y recién ahí empezaba a descargar, una por una. Ahora
  las elegidas se descargan juntas en segundo plano desde que arranca la tanda; al llegar su turno sólo se instalan.
- **Después de instalar un programa con winget, si el registro ya muestra la versión nueva no se le pregunta a winget**
  («winget list», un proceso de varios segundos por programa).
- **Controladores del catálogo:** si pnputil no dice que lo instaló en el dispositivo, ya no se esperan 12 segundos mirando el registro.
- 78 comprobaciones nuevas en total (Pruebas2025.cs).

## 2.0.24

Capturas del usuario (21/11/2026) en pc2, pc3 y pc4: «en Vigía sale una cosa y en el Actualizador otra… ya no sé a quién hacerle
caso». Por ejemplo en pc4 Vigía listaba 5 (Outlook, VCLibs 14, Windows App Runtime 1.5 y 1.6, Teams) y el Actualizador sólo Outlook y
Teams, como opcionales.

**La regla desde esta versión: todo lo que lista Vigía aparece en el Actualizador, con el mismo nombre y marcado.** El Actualizador
muestra además lo que Vigía no revisa (controladores, Windows Update, las apps de tu usuario).

- **Se quitó la regla de la 2.0.22** que daba «al día» la copia vieja de un marco (VCLibs, Windows App Runtime) cuando tu usuario ya tenía
  la nueva. Era cierto para tu usuario, pero Vigía cuenta la copia que ve winget como servicio, y esa se puede actualizar: en la
  captura de pc2, después de que el Actualizador instaló Teams como servicio, Vigía dejó de listar VCLibs y Windows App Runtime 1.4/1.5.
- **Un paquete de Microsoft de la Store tiene una copia por cuenta** (la tuya y la que ve el servicio). Antes, si tu usuario también tenía
  una actualización para el mismo paquete, la fila del servicio no se mostraba (en pc4, Windows App Runtime 1.6). Ahora aparecen las dos:
  la tuya y «… (para todos los usuarios)». Un programa común (del registro: Edge, Chrome, PDF24) es una sola instalación y sigue en una
  sola fila.
- **Mismo nombre que Vigía**: «Microsoft.WindowsAppRuntime.1.6 (para todos los usuarios)», «MSTeams (para todos los usuarios)».
- **Marcadas**: lo que ve winget como servicio entra en «Marcar todo» (antes Outlook y Teams quedaban opcionales si tu usuario no los tenía).
- **Controladores que Windows no aplica** (captura de pc3: «AMD PCI · Falló la instalación · Windows no lo aplicó»): no es un error. Windows
  se queda con el que tiene porque lo considera mejor; ahora la fila queda «Al día» con esa explicación, sin rojo, y no se vuelve a ofrecer.
- 22 comprobaciones nuevas (Pruebas2024.cs), con las listas de las capturas.

## 2.0.23

Pedido del usuario (21/11/2026): relevamiento y auditoría completa, corregir todos los errores y optimizar.

- **Error grave en «winget como servicio» (2.0.21 y 2.0.22): un resultado correcto se leía como falla.** El guion que corre como SYSTEM
  guardaba el código de winget con `echo %ERRORLEVEL%> archivo`. Cuando winget termina bien el código es 0, y la línea queda
  `echo 0> archivo`: cmd lee `0>` como redirección de la entrada, el archivo queda vacío y el código se leía como -1. Con eso:
  - toda actualización como servicio que salía bien se tomaba como fallida, y se repetía con `--scope machine` (que respondía «winget no
    encontró el paquete instalado») y después por la vía directa: así salían en rojo las filas de Windows App Runtime en pc7;
  - una consulta sin novedades (sin tabla) se descartaba y se repetía con `--scope machine`: un proceso de winget de más en cada
    búsqueda.
  Ahora la redirección va adelante (`>archivo echo %ERRORLEVEL%`), y una prueba revisa cada línea del guion como la leería cmd con los
  códigos 0 a 9.
- **Si winget terminó bien, la fila queda «Actualizada»** aunque winget todavía liste la versión vieja (un marco convive en varias
  versiones; la fila lo explica). Antes se volvía a instalar por la vía directa, y con un programa común (PDF24) eso repetía su instalador
  sin necesidad. «Reiniciar para terminar» (0x8A150109/0x8A15010B) también cuenta como actualizada, «requiere reiniciar».
- **Revisión con los analizadores de código de .NET** (todas las reglas) sobre el motor y la base: sin fugas de procesos ni de
  respuestas HTTP, sin esperas sin plazo, sin comparaciones dependientes del idioma en lo que importa. Lo que marcaron y no son
  errores (SHA-1 para verificar lo que publica TLauncher, TLS 1.2/1.3 fijados a propósito en Windows viejos) queda documentado en
  docs/AUDITORIA.md.
- 64 comprobaciones nuevas (Pruebas2023.cs).

## 2.0.22

Captura del usuario (21/11/2026, pc7, con la 2.0.21): «Hay 6 actualizaciones · 4 con error»: **WindowsAppRuntime.1.4/1.5/1.6
(para todos los usuarios) · winget no encontró el paquete instalado; y su manifiesto no publica un paquete MSIX para x64**, y **Roblox
«terminó (código 0) pero sigue la 0.740.0.7400927»**.

- **Windows App Runtime (y cualquier paquete que ve winget como servicio).**
  - Se instalaba con `--scope machine`, que busca entre los paquetes aprovisionados; pero como servicio el paquete está en lo que ve
    SYSTEM, así que winget respondía «no encontró el paquete instalado» (0x8A150014). Ahora se actualiza primero tal cual se vio (como
    servicio, sin `--scope`, lo mismo que el `winget upgrade --all` del agente de Vigía) y recién si no anda, con `--scope machine`.
  - El respaldo sólo sabía aprovisionar un `.msix`, y Windows App Runtime se publica con su instalador oficial
    (`WindowsAppRuntimeInstall-x64.exe --quiet`, que como administrador instala y aprovisiona para todo el equipo). Ahora, si el
    manifiesto no trae un `.msix`, se usa ese instalador: bajado por HTTPS, aceptado sólo con el SHA-256 del catálogo (lo mismo que
    exige winget) y con los interruptores del manifiesto.
  - Un marco de ejecución (VCLibs, Windows App Runtime, UI.Xaml) convive en varias versiones y las apps usan siempre la más nueva. Si tu
    usuario ya tiene la publicada, la copia vieja que ve winget como servicio queda **«Al día»**, con la explicación (Windows la quita
    sola cuando ninguna app la usa), en vez de una actualización que no hace falta. Si después de instalar winget como servicio todavía
    lista la vieja, la fila queda «Actualizada» y lo explica, sin rojo.
- **Roblox (pc7).**
  - Se busca en todas sus carpetas: la de su entrada en «Aplicaciones», la del usuario y las de Program Files. Si Roblox se instaló
    alguna vez como administrador vive en Program Files, y el instalador ejecutado como usuario dejaba la versión nueva en otra carpeta:
    la vieja seguía igual. Ahora, si está en Program Files, el instalador corre como administrador.
  - Si en silencio no deja la versión nueva, se ejecuta otra vez **con su ventana** (la del propio instalador de Roblox, que baja la
    versión y se cierra sola). No se abre ninguna página.
  - Si ni así, la fila dice el último paso que anotó el instalador (`InstallerInfo.log`) y qué hacer.
- 16 comprobaciones nuevas (Pruebas2022.cs), con el manifiesto real de Windows App Runtime 1.4.7.

## 2.0.21

Capturas del usuario (21/11/2026, con la 2.0.20): en **pc6** Vigía decía «3 programas con versión nueva» (Microsoft.Outlook
1.2025.829.200 → 1.2026.812.100, Microsoft.Teams 25255.703.3978.7153 → 26288.2004.5269.3420 y PDF24 11.34.0 → 11.35.0) y el
Actualizador «Nada para actualizar»; en **pc7**, «Roblox Player 0.740.0.7400927 → 0.747 · Falló la instalación · El instalador oficial
de Roblox terminó…». Además: que consuma menos, para usarlo en equipos más viejos.

- **Lo que ve Vigía, ahora también acá.** El agente de Vigía corre `winget upgrade` como servicio (cuenta SYSTEM), y winget ve ahí
  otra lista que la de tu usuario y que la de `--scope machine` (lo que probó la 2.0.20, que en pc6 no mostró nada). Ahora el
  Actualizador ejecuta exactamente esa misma consulta como SYSTEM, con una tarea programada que se crea, se ejecuta una vez y se borra
  (sólo si la ventana corre como administrador). Lo que difiere de lo tuyo aparece como «… (para todos los usuarios)» y se actualiza en
  ese mismo contexto: con `--scope machine` (un paquete MSIX se aprovisiona para todo el equipo) o, si winget no tiene instalador para
  todo el equipo (un programa común como PDF24), sin él. Se confirma volviendo a preguntar como servicio. Si no se puede (sin el
  Programador de tareas), se usa `--scope machine` como antes.
  - Seguridad: el guion que corre como SYSTEM está en una carpeta privada (sólo Administradores y SYSTEM) y el winget.exe tiene que ser
    el de App Installer en `Program Files\WindowsApps` (sólo lo escribe Windows); nunca el alias de la carpeta del usuario.
- **Roblox (pc7).** El instalador de Roblox se vuelve a abrir a sí mismo (en su `InstallerInfo.log`: «Installer started» y enseguida
  «Restarted» con otro proceso): el primero termina con código 0 al instante y el segundo recién empieza a bajar la versión. La 2.0.20
  confirmaba ahí y veía la 0.740. Ahora espera a que termine de verdad (hasta 10 minutos): cuando aparece la versión nueva, cuando su
  registro dice «SUCCESS» o un error (que pasa a la fila), o cuando ya no queda ningún instalador de Roblox abierto.
- **Equipos viejos (modo liviano).** Con 4 procesadores lógicos o menos, o menos de 6 GB de memoria, la búsqueda hace lo mismo con menos
  cosas a la vez:
  - winget de a un proceso (antes tres juntos: la lista, las actualizaciones y la de todo el equipo) y una búsqueda por nombre a la vez
    (antes cuatro procesos de winget);
  - dos páginas oficiales y dos consultas a GitHub a la vez (antes seis), una al Catálogo de Microsoft Update (antes tres);
  - winget, Windows Update y la herramienta del fabricante, de a uno.
  - `ACTUALIZADOR_LIVIANO=1` (o `0`) lo fuerza. El registro dice al empezar qué equipo es y si usa el modo liviano.
- **En todos los equipos:**
  - las búsquedas por nombre en el catálogo de winget se guardan 3 días (antes 12 horas): en pc6 eran 35 procesos de winget en cada
    búsqueda;
  - las búsquedas corren con prioridad baja (debajo de la ventana y de lo que estés usando);
  - sin nada en curso, la ventana se refresca una vez por segundo (antes cuatro, siempre).
- 39 comprobaciones nuevas (Pruebas2021.cs), con las tablas de las capturas.

**Pendiente de comprobar en Windows:** winget como SYSTEM con la tarea programada (es lo mismo que hace el agente de Vigía en pc6; si
no anduviera, el registro dice «winget como servicio no respondió» y se usa `--scope machine`). Lo que vio queda en
`logs\winget-upgrade-servicio.txt`.

## 2.0.20

Capturas del usuario (21/11/2026, con la 2.0.19): el agente de Vigía listaba **5 paquetes con versión nueva que el Actualizador no
mostraba** (Microsoft.VCLibs.Desktop.14, Microsoft.WindowsAppRuntime.1.4/1.5/1.6 y Microsoft.Teams.Free), y **Roblox Player
«Desconocida → 0.747 · Falló la instalación · winget no tiene una actualización aplicable»**. El pedido: controlar todo, auditoría
completa y corregir todos los errores. Se revisaron también los registros y el diagnóstico reales de un equipo (Windows 11 26H2).

- **Los controladores no se revisaban nunca (el error más grave).** Todas las consultas a WMI fallaban con 0x80020005 («Los tipos no
  coinciden»): a los parámetros opcionales se les pasaba `null` (VT_EMPTY), que WMI no acepta donde espera un objeto. En los registros
  aparece en cada búsqueda (32 veces) y el historial no tiene ni una fila de controladores. Se pasan como «omitidos» (lo mismo que hace
  VBScript), y si WMI por COM fallara igual, la consulta se repite con `Get-CimInstance` de PowerShell. Si aun así no se pueden leer los
  controladores, ahora la búsqueda lo dice (antes la lista salía sin ninguno, como si estuvieran todos al día). Esto arregla también
  «Fabricante del equipo» (Dell/HP/Lenovo), el Centro de seguridad y el punto de restauración.
- **Apps de Windows para todos los usuarios (la captura de Vigía).** El agente de Vigía corre winget como servicio y ve las copias de
  Windows «aprovisionadas» (las que reciben las cuentas nuevas y las que todavía no se actualizaron). `winget upgrade` sin más sólo mira
  las del usuario. Ahora se consulta también `winget upgrade --scope machine` (al mismo tiempo que lo demás) y aparecen como
  «… (para todos los usuarios)». Se actualizan con `--scope machine`, que aprovisiona la versión nueva para todo el equipo; en Windows
  anteriores a 24H2, donde winget no puede (0x8A150113), se aprovisiona directamente el paquete oficial del mismo manifiesto, verificado
  con su SHA-256. Los marcos (VCLibs, Windows App Runtime, UI.Xaml) se marcan solos; una app que tu usuario no tiene (Teams) no, porque
  al aprovisionarla también se te instala.
- **Roblox.** El paquete de winget pasó de «exe» a «portable» (desde la 0.70x): winget ya no puede actualizar un Roblox instalado con su
  instalador y responde 0x8A15002B siempre. Además Roblox no registra su versión. Ahora la versión sale de `RobloxPlayerBeta.exe` de su
  carpeta de versiones (si ya está al día, no se ofrece), y se actualiza con su instalador oficial (`RobloxPlayerInstaller.exe` de
  setup.rbxcdn.com, SHA-256 del catálogo y firma de Roblox Corporation) ejecutado como tu usuario, en silencio.
- **Claude «Falló: no cambió nada» estando en la última.** Claude (y otras apps de Electron que se actualizan con Squirrel: Discord,
  Slack…) guarda cada versión en una carpeta «app-6.28899.2» y su entrada en «Aplicaciones» quedaba en la 6.6395.0.0: winget la ofrecía
  en cada búsqueda. Ahora esa carpeta dice la versión real y la fila queda «Al día» con la explicación.
- **Programas instalados sólo para tu usuario (Ollama 0.40.16 → 0.40.17 abría ollama.com).** Con la ventana como administrador no se
  instalaban «por seguridad». Ahora el instalador verificado (SHA-256 y la misma firma que lo instalado) se ejecuta **como tu usuario, sin
  elevar**: así no hay nada que aprovechar y el programa se actualiza en vez de abrir la página.
- **Windows Update:**
  - «Descarga de hasta 90,5 GB» en la acumulativa: desde 24H2 Windows informa la suma de todos sus archivos. Por encima de 10 GB se muestra
    el mínimo que informa Windows o «según lo que le falte a este equipo».
  - Con dos acumulativas de la misma compilación marcadas (la de seguridad 26300.9974 y la preliminar 26300.10075), se instala primero la
    más nueva; la anterior queda «incluida» (la de seguridad había fallado con 0x800F081F y la preliminar, que la incluye, se instaló bien).
  - Los errores más comunes dicen qué hacer: 0x800F081F/0x800F0831/0x80073712 (archivos de componentes dañados: `DISM /Online
    /Cleanup-Image /RestoreHealth` y `sfc /scannow`), 0x800F0922, disco lleno, descarga incompleta.
- 99 comprobaciones nuevas (Pruebas2020.cs) con los datos reales: los manifiestos de winget-pkgs de Roblox 0.747, VCLibs 14.0.33728.0 y
  Teams 26149.701.4759.8860, y la tabla de la captura de Vigía.

**Pendiente de comprobar en Windows** (desde donde se compiló no hay Windows):
- La lectura de WMI con los parámetros omitidos (si no anduviera, la vía de PowerShell la reemplaza y el registro lo dice con
  «la consulta se hizo con PowerShell»). Es la primera versión 2.0 en la que los controladores se comparan de verdad.
- `winget upgrade --scope machine` sobre los paquetes aprovisionados (el procedimiento sale del código de winget-cli:
  `FindProvisionedPackages` y `AddPackageMachineScope`).
- Que el instalador de Roblox termine solo con `-install -silent` (son los interruptores que usó el manifiesto oficial de winget mientras fue
  de tipo «exe»). Si no deja la versión nueva, la fila lo dice; Roblox igual se actualiza solo al abrirlo.
- Claude: la carpeta «app-…» se supone por el instalador de Squirrel (`ProductCode: AnthropicClaude`); si la instalación fuera otra, la
  fila sigue como antes.

## 2.0.19

Captura del usuario (17/11/2026, con la 2.0.18): **TLauncher 2.9381 → 2.9382 «Pendiente: Se abrió su página (tlauncher.org): descargá e
instalá la 2.9382 desde ahí»**, igual que pasaba con CrystalDiskInfo. El pedido: que todo se actualice sin abrir la página.

- **TLauncher se actualiza desde acá, sin abrir tlauncher.org ni su ventana de actualización.** TLauncher usa el arranque de código
  abierto starter4j: lo que hace su botón «Actualizar» es anotar la versión nueva en `%APPDATA%\.tlauncher\starter\starter.json`; al
  abrirse, el arranque baja los archivos de esa versión verificando el SHA-1 que publica `appConfig.json`. Actualizador hace exactamente
  eso: lee `appConfig.json` de repo.tlauncher.org (o del alternativo, repo.fastrepo.org), baja los archivos que faltan (el programa y
  sus dependencias), **sólo los guarda si el tamaño y el SHA-1 coinciden** y, recién con el programa verificado en su lugar, anota la
  versión en `starter.json` (deja una copia del anterior). La próxima vez que lo abrís ya está en la versión nueva y no pregunta nada.
  - Sin la «oferta opcional» de programas de terceros que trae la ventana de TLauncher (la de la captura de la 2.0.16 ofrecía Avast
    Secure Browser).
  - La fila entra en «Marcar todo» (antes era opcional, en violeta) y no pide ejecutar como administrador: se escribe en el perfil del
    usuario.
  - Si TLauncher está abierto, la ventana de confirmación ofrece cerrarlo (también su Java, que corre desde la carpeta del arranque):
    abierto, al cerrarse volvería a anotar la versión vieja.
  - Si ningún servidor da el archivo del programa con el SHA-1 publicado, no se cambia nada y la fila dice el motivo. Lo que no se pudo
    bajar de las dependencias lo completa el propio TLauncher al abrirse (verificándolo igual) y la fila lo dice.
  - Si TLauncher nunca se abrió en el equipo (no hay `starter.json`), la fila lo dice: en ese caso, al abrirlo baja directamente la
    última versión.
- Seguridad (detalle en docs/SEGURIDAD.md): sólo HTTPS de tlauncher.org y fastrepo.org (el tercer servidor que lista TLauncher es HTTP y
  se descarta); todo dentro de la carpeta del arranque (rutas con `..`, absolutas, `starter.json` o nombres reservados se rechazan) y
  nada se escribe si hay un enlace (unión o enlace simbólico) en el camino, porque Actualizador corre como administrador.

**Pendiente de comprobar en Windows:** la actualización real de TLauncher 2.9381 → 2.9382. Desde el entorno donde se compiló no se puede
llegar a repo.tlauncher.org; el procedimiento sale del código fuente de starter4j (github.com/gdevby/starter4j) y de un registro real de
TLauncher (que confirma la carpeta `.tlauncher/starter` y los servidores). Si fallara, la fila dice el motivo exacto y TLauncher queda
como estaba.

## 2.0.18

Captura del usuario (15/11/2026, con la 2.0.17): **CrystalDiskInfo 9.9.4 → 9.9.5 seguía «Pendiente: Se abrió su página (crystalmark.info)»**.
La 2.0.17 decía resolverlo y no lo hacía: sus pruebas simulaban que crystalmark.info y SourceForge redirigen al archivo, y nunca se
comprobó contra los servidores reales. Lo que hacen de verdad:

- **SourceForge**, a todo lo que se presenta como navegador, responde una página («Your download will start shortly…») que baja el
  archivo con JavaScript. Su soporte lo indica: hay que seguir las redirecciones y **no** usar un User-Agent de navegador. Actualizador se
  presentaba como «Mozilla/5.0 (compatible; Actualizador…)», así que recibía siempre esa página, por winget-pkgs y por la página oficial.
- **crystalmark.info/redirect.php** no redirige: responde «Redirecting… Please wait…» con un botón «Download Now» a SourceForge
  («…/CrystalDiskInfo9_9_5.exe/download»), un enlace que no termina en .exe y no se reconocía.

Arreglos:

- **SourceForge con User-Agent de herramienta de descargas** («Actualizador/2.0.18 (Windows; descargas)», como winget, wget o curl):
  responde la redirección al servidor espejo, en la búsqueda y en la descarga. Los demás sitios siguen con el de siempre.
- **Páginas intermedias**: el botón de «Redirecting…» (enlaces «archivo.exe/download»), la redirección por JavaScript y, si SourceForge
  igual respondiera su página de espera, el espejo que nombra esa página («https://ESPEJO.dl.sourceforge.net/project/…»). Hasta 3 saltos,
  sin volver sobre una página ya vista; cada salto por HTTPS.
- **Segunda oportunidad al tocar «Actualizar»**: lo que en la búsqueda quedó para abrir su página vuelve a buscar su instalador antes de
  abrirla; si aparece, se instala verificado como cualquier otro. El programa abierto se ofrece cerrar en la ventana de confirmación
  (también lo que corre desde su carpeta: CrystalDiskInfo queda en el área de notificación).
- **Si igual hay que abrir la página, la fila dice por qué** («No se pudo bajar el instalador solo (motivo). Se abrió su página…»).
  Con la 2.0.17 sólo decía «Se abrió su página» y no había forma de saber qué había fallado.
- Las pruebas nuevas simulan los servidores como responden de verdad (según el User-Agent, con la página «Redirecting…» y la de
  espera de SourceForge), no como se suponía.

Auditoría (detalle en docs/AUDITORIA.md):

- **Tiempo máximo de 2 s para todas las expresiones regulares del proceso**: se aplican a páginas de internet de hasta 4 MB; un texto
  armado a propósito podía trabar una búsqueda indefinidamente (ReDoS). Todas las pruebas corren con ese tope.
- **El lector de manifiestos YAML** rechaza archivos de más de 2 MB o con más de 64 niveles: antes, uno con miles de niveles agotaba la
  pila y cerraba el programa sin aviso.

**Pendiente de comprobar en Windows:** la instalación real de CrystalDiskInfo 9.9.5. Desde el entorno donde se compiló no se puede
llegar a crystalmark.info ni a SourceForge; su comportamiento se comprobó leyendo las páginas reales y la documentación del soporte de
SourceForge. Si fallara, la fila ahora dice el motivo exacto.

## 2.0.17

Captura del usuario (15/11/2026): **CrystalDiskInfo 9.9.4 «Pendiente: Se abrió su página (crystalmark.info): descargá e instalá la 9.9.5
desde ahí»** y la fila de Microsoft Store «Instalando…» durante minutos. El pedido: «varios programas no se actualizan, sólo abren la
página para descargar la actualización; la idea es que sea más fácil y rápido, no más lento», y revisar por qué las instalaciones
tardan tanto.

Programas que abrían su página: ahora se instalan desde acá.

- **Cuando la página oficial publica una versión que winget todavía no tiene, se busca su instalador** (antes la fila quedaba
  «opcional» y «Actualizar» abría la página). Era el caso de la captura: la 9.9.5 salió ese mismo día y el catálogo de winget seguía
  en la 9.9.4. Se busca, en este orden:
  1. **El manifiesto de esa versión en winget-pkgs**, por si el repositorio ya la tiene y el catálogo de este equipo todavía no:
     mismo instalador y misma comprobación (su SHA-256) que usaría winget.
  2. **La dirección del instalador de la versión anterior con la versión nueva en su lugar**
     («…/9.9.4/CrystalDiskInfo9_9_4.exe» → «…/9.9.5/CrystalDiskInfo9_9_5.exe»; también «npp.8.6.8» → «npp.8.6.9» y
     «winrar-x64-701» → «710»), comprobando que el archivo exista y sea un instalador (se piden sólo sus primeros bytes). Se instala con
     los interruptores del manifiesto de winget.
  3. **Los enlaces de la página oficial**, también los de su página de descargas y los que pasan por una redirección
     («redirect.php?product=CrystalDiskInfoInstaller») o por una página de espera (SourceForge). Nunca un portable, el código fuente,
     una beta ni **otra edición** del programa: en crystalmark.info, «CDI» de la portada es la edición con publicidad y no se toma; se
     toma «Installer» de la edición estándar (o el de Shizuku, si la instalada es ésa).
- **La verificación no se afloja:** se descarga por HTTPS y sólo se ejecuta con firma digital válida, no revocada y del mismo fabricante
  que el programa instalado (o con el SHA-256 del catálogo de winget), que diga ser de este programa y nunca de una versión anterior
  a la instalada. La fila entra en «Marcar todo» sólo si el archivo es, comprobado, el de esa versión.
- **Si el autor no firma** (ni lo instalado ni el instalador tienen firma): se instala sólo si lo instalado tampoco está firmado
  (comprobado en sus ejecutables), la página se leyó por HTTPS, y el archivo dice ser de este producto y **exactamente** de la versión
  publicada (un .msi, además, de la misma familia que lo instalado). Es el mismo archivo que bajarías a mano de esa página. Se
  puede prohibir con la política `permitirInstaladoresSinFirma`.
- **Un instalador que no tiene modo silencioso** (ni Inno Setup, ni NSIS, ni MSI), ya verificado, **se abre con su ventana** en vez
  de mandarte a la página a bajarlo. No desde la consola ni la revisión programada, ni si la política no permite ventanas.
- **La página se abre sólo si no se pudo**: no se encontró el archivo, no se pudo verificar o la descarga falló. La fila dice el
  motivo («No se pudo instalar sola (está firmado por «X» y el programa instalado por «Y»). Se abrió su página…»). Si el instalador
  llegó a ejecutarse y falló (el programa estaba abierto), queda ese error y no se abre nada.

Por qué tardaba:

- **Microsoft Store: se esperaban siempre 4 minutos cuando no había nada para actualizar** (lo más común: la fila se marca sola cada
  12 horas). Ahora se le pregunta a la propia Store qué tiene en su cola de descargas e instalaciones (sólo lectura): con la cola
  vacía dos veces seguidas y sin versiones que cambien, termina en **menos de un minuto**. Mientras tiene algo en curso, la fila
  dice cuántas apps. Si no se puede consultar la cola, 2 minutos en vez de 4.
- **La Store se actualiza mientras se instala lo demás.** Va después de winget (winget es una app de la Store: si la Store la
  actualiza mientras winget trabaja, lo corta) y en paralelo con navegadores, fabricantes, controladores y Windows Update. Antes todo
  eso esperaba a que la Store terminara.
- **Instalador oficial: hasta 90 segundos «Instalando» para terminar en error.** Después de ejecutar el instalador se releía la
  versión 30 veces cada 3 segundos; cuando el instalador no había hecho nada (el programa abierto) se esperaba todo ese tiempo. Ahora
  20 segundos como mucho, mirando cada segundo: el instalador y los procesos que deja ya terminaron para entonces.
- **winget: más de un minuto por programa cuando la versión no llegaba.** Se le preguntaba a winget hasta 8 veces («winget list» tarda
  varios segundos cada vez) con 4 segundos de pausa. Ahora se mira el registro (milisegundos) hasta 20 segundos y a winget una sola
  vez más.

Sin cambios: lo que abre su página a propósito (TLauncher, FortiClient administrado, DAEMON Tools, juegos) sigue igual, y una copia
portable no se pisa con un instalador.

**No probado en Windows:** esta versión se compiló y pasó las pruebas en Linux (3.675), con la red y los instaladores simulados. La
consulta a la cola de la Store (PowerShell) y la instalación real de CrystalDiskInfo 9.9.5 no se pudieron ejecutar acá; si la consulta
no funcionara en un equipo, se sigue midiendo por tiempo (2 minutos).

## 2.0.16

Captura del usuario (13/11/2026): **TLauncher 2.9323 «No verificable» mientras el propio TLauncher avisaba «¡Nueva actualización
encontrada! 2.9381»**, y el pedido de una auditoría completa de la versión que se instala y de la portable.

- **TLauncher: se compara con la misma fuente que consulta el propio TLauncher.** winget no lo tiene y la portada de tlauncher.org
  nombra una versión vieja (2.9307). Su arranque (starter4j, de código abierto) baja `appConfig.json` de repo.tlauncher.org y compara
  `appVersion` con la versión que anota en `%APPDATA%\.tlauncher\starter\starter.json`: Actualizador hace lo mismo. Con la captura:
  «Actualización disponible: 2.9323 → 2.9381». Si repo.tlauncher.org no responde, se lee de repo.fastrepo.org (el servidor
  alternativo que usa TLauncher).
- **La versión instalada de TLauncher es la real.** TLauncher se actualiza solo dentro del perfil y «Aplicaciones» se queda con la
  versión del día que se instaló: después de actualizarlo seguía figurando desactualizado para siempre. Ahora manda `starter.json`
  (del perfil donde está instalado, del usuario actual o de las demás cuentas); sin ese archivo, se compara con el registro.
- **No se instala desde acá:** «Actualizar» abre tlauncher.org y la fila explica cómo hacerlo desde TLauncher y que **la ventana
  de actualización trae una «oferta opcional» de programas de terceros** (la de la captura ofrecía Avast Secure Browser): rechazala.
- **MSI Afterburner** (también «No verificable» en la captura): se compara con la versión «Final» que publica msi.com. La página pone
  «4.6.7 (Beta)» al lado, y el filtro de betas por cercanía descartaba también la final: ahora una fuente que identifica la versión
  estable por sí misma (`ExactVersion`) no se descarta por eso.
- **Betas escritas con espacio** («4.6.7 Beta 3», «2.1_rc1»): antes valían lo mismo que la final; con la beta instalada, la 4.6.7
  final publicada se daba por «al día». Ahora la final es más nueva y entre betas se compara por etapa y número.
- **Juegos fuera de una tienda** («Street FighterTM 6», «Juego Prototype(TM)»): por el editor (CAPCOM, BANDAI NAMCO, SEGA…) o por estar
  en una carpeta de juegos, la fila dice que el juego lo actualiza la tienda donde se compró, en vez de «no figura en winget».
- **Tus programas** (carpeta de versiones): un programa con «Windows», «Portable», «Final» o «Setup» en el nombre («Windows Monitor»)
  no coincidía nunca con su archivo, porque al archivo se le sacaban esas palabras y al nombre no.

Versión portable:

- **Los controladores que Windows no quiso aplicar se recordaban en la carpeta de consultas compartidas** (`Datos\cache`): la portable
  los llevaba al equipo siguiente y ahí no ofrecía ese controlador por 30 días. Ahora van con los datos de cada equipo
  (`Datos\equipos\NOMBRE`); en la instalada, junto a los demás datos. Además, «Olvidar las consultas guardadas» los borraba y
  volvían a ofrecerse: ya no. Lo que había guardado la 2.0.15 se conserva.

Versión que se instala:

- **`Instalar.cmd` podía instalar una publicación vieja.** Si había una carpeta `publish\Actualizador` de una compilación anterior, no
  volvía a compilar e instalaba esos archivos sin avisar. Ahora `Publish.ps1 -SiHaceFalta` publica de nuevo si lo publicado es de otra
  versión o anterior al código.
- **La entrega para instalar sale en .7z** (con 7-Zip instalado; si no, .zip), como la portable.

Detalles: fechas de los registros con el calendario de siempre aunque Windows use otro; mayúsculas de los mensajes sin depender del
idioma de Windows.

## 2.0.15

Pedido del usuario (11/11/2026): **«https://www.fortinet.com/lat/support/product-downloads no detecta ningún programa de esa
página», y no actualizaba ni con la versión instalada ni con la portable.** La página de Fortinet no publica el número de
versión (dice «FortiClient 7.4»): sus botones llevan a direcciones fijas de links.fortinet.com que redirigen al instalador de
la última versión en filestore.fortinet.com. Leer la página no podía dar nunca la versión, y winget no reconoce lo instalado.

- **FortiClient VPN (Solo VPN, el gratuito): se compara y se actualiza con el instalador oficial de esa página.** La versión
  sale del propio instalador (FortiClientVPNInstaller.exe): de sus recursos, que es lo que Fortinet indica revisar
  (Propiedades › Detalles). Se leen sólo los pedazos del archivo que hacen falta (encabezado, secciones y recursos: unos KB),
  sin ejecutarlo. Si hay una más nueva se instala ese mismo archivo en silencio (`/quiet /norestart`), sólo por HTTPS desde
  fortinet.com, con firma válida de Fortinet Technologies y nunca una versión más vieja que la instalada; el resultado se
  confirma por la versión que queda instalada. x64 y ARM64, cada uno con su instalador. Si la conexión de la VPN está en uso,
  se corta mientras se actualiza (lo dice la fila).
- **FortiClient (ZTNA / EPP): se compara** con el instalador de la misma página (la versión está en su nombre:
  FortiClientOnlineInstaller_7.4.8.2066.M.exe). No se instala desde acá: lo administra el FortiClient EMS de la organización,
  con la versión que aprobó. La fila lo dice.
- **«Indicar la página oficial…» no reemplaza una fuente incorporada:** en un programa que ya se compara con la fuente oficial
  del fabricante, avisa contra qué se compara en vez de leer otra página (la de Fortinet tiene números de otros productos,
  como FortiConverter 7.4.0).
- Los demás productos de esa página no se instalan como programas de Windows comparables: FortiConverter, FortiFone y
  FortiCentral se publican como .zip, FortiExplorer sólo para celulares y FortiGate-VM/FortiWeb en las nubes.

## 2.0.14

Captura del usuario (08/11/2026): **Double Commander 1.2.11 «Falló la instalación: el instalador no tiene una firma digital
válida (0x800B0100)».** El autor de Double Commander no firma sus instaladores, y Actualizador exigía siempre «la misma firma
que lo instalado» para lo que baja de GitHub.

- **Instaladores sin firma de GitHub, sólo con todas estas condiciones:** el repositorio es el que figura en el manifiesto de
  winget (no uno que registra el propio programa); el instalador no tiene NINGUNA firma (una firma inválida o revocada sigue
  rechazándose); lo instalado tampoco está firmado (comprobado en sus ejecutables: si no se puede saber, no se instala); el
  SHA-256 coincide con el que publica GitHub; y el paquete es de este producto: un .msi con el mismo UpgradeCode que lo
  instalado (Windows Installer lo confirma) y exactamente la versión anunciada; un .exe que diga ser de este programa. El .msi
  se lee sin ejecutarlo (tabla Property, sólo lectura).
- **Compilar: «Instalá el SDK de .NET 8» con el SDK ya instalado** (captura del 08/11/2026: winget decía «No hay versiones
  más recientes»). El «dotnet» que aparecía primero en el PATH no listaba ese SDK (típico: el de 32 bits, en
  C:\Program Files (x86)\dotnet, delante del de 64 bits). Ahora los scripts buscan el SDK 8 donde está instalado (registro
  del instalador de .NET, C:\Program Files\dotnet, la carpeta del usuario y todos los «dotnet» del PATH) y usan ese para
  compilar. Si winget lo da por instalado pero no aparece, lo reinstala para repararlo; si aun así falta, muestra cada
  dotnet.exe encontrado con los SDK que lista.
- **Sin azul ni amarillo** (pedido del usuario): el color de acento, los enlaces, las filas opcionales, la fila elegida y el
  icono pasan a violeta; los avisos siguen en naranja.

## 2.0.13

Captura del usuario (07/11/2026): **22 programas quedaban «No verificable»** y varios no eran juegos. Se revisó la página
oficial de cada uno:

- **Controlador de gráficos de NVIDIA: se compara con NVIDIA para la placa instalada.** La versión sale del mismo servicio
  que usa la página de descargas de NVIDIA (nvidia.com/Download): se identifica la placa por su nombre en el Administrador
  de dispositivos (escritorio o portátil), y se pide el controlador estable (WHQL, sin betas, Game Ready) para la versión
  de Windows del equipo y el tipo instalado (DCH o estándar). Con una GeForce GTX 760 y la 475.14: «Al día» («Coincide
  con la última versión que publica NVIDIA para la GeForce GTX 760 (GeForce Security Update Driver, del 2024-07-09). NVIDIA
  no publicó versiones más nuevas para esta placa desde entonces.»). Cuando haya una nueva, se baja de download.nvidia.com,
  se ejecuta sólo con la firma de NVIDIA Corporation y **se abre el instalador de NVIDIA** para completarlo (un controlador
  de video conviene instalarlo viendo lo que pasa); el resultado se confirma por la versión instalada, aunque Windows
  registre la entrada con otro nombre («… 621.14»).
- **Avast One:** si el Centro de seguridad no contesta por WMI durante la búsqueda, se lee del registro (sólo para confirmar
  que está activado y al día; nunca para alarmar).
- **Los que no publican una versión que se pueda leer** ahora dicen por qué y qué hacer, en vez de un texto genérico:
  Device Detector (componente de ACDSee), DWAgent (DWService se actualiza solo y no publica versión), EZVIZ Studio (su sitio
  rechaza las consultas automáticas), Logi Plugin Service (se actualiza con Logi Options+), Logi Download Assistant,
  USB Disk Security (zbshareware.com ahora lleva a privatedlp.com, otro producto), los archivos de controlador de monitor
  ViewSonic (no son un programa) y µTorrent (utorrent.com no publica versión y su instalador ofrece programas de terceros).
- Lo que sigue sin verificar y **sin versión en el registro** («Desconocida») muestra la de su ejecutable principal.
- Si un fabricante publica una versión pero no se pudo leer la instalada y no se instala desde acá, la fila queda «No
  verificable» con la versión publicada, en vez de «Disponible».

Video del usuario (07/11/2026): **«Actualizar (1)» con «Disponibles (0)», y la lista vacía al actualizar.** «Marcar todo»,
con la lista «Al día y sin verificar» a la vista, marcaba la fila «Todas las apps de Microsoft Store → Sin novedades» (que se
puede marcar a mano para que la Store busque de nuevo). Al instalarla pasaba a «Disponibles (1)», pero la lista no se
rearmaba y quedaba vacía.

- «Marcar todo» marca sólo lo que tiene algo para instalar; en «Al día y sin verificar» queda desactivado. La fila de la
  Store se sigue pudiendo marcar a mano.
- Si lo marcado está en la otra lista, el encabezado lo dice («1 marcada en «Al día y sin verificar»: «Actualizar» hace sólo
  eso»).
- Cuando una fila cambia de lista mientras se instala, la lista que se está viendo se rearma: lo que figura en
  «Disponibles (N)» se ve.

## 2.0.12

Captura del usuario (07/11/2026): **7-Zip 26.05 quedaba «Pendiente» y «Actualizar» abría una página de GitHub.** En
github.com/ip7z/7zip había aparecido una 26.06 que la página oficial, www.7-zip.org, todavía no publicaba (sigue en la 26.05);
el contraste con GitHub (2.0.4) la ofrecía igual y, como el nombre «7-Zip 26.05 (x64)» lleva la versión, creía que se instala
una al lado de la otra y mandaba a GitHub en vez de instalar. Ahora **manda siempre la página oficial**:

- **7-Zip se compara con www.7-zip.org** (catálogo de fuentes oficiales): la versión es la que publica su página de descargas
  («Download 7-Zip 26.05 (2026-10-21)»), no la del catálogo de winget ni la de GitHub. Hoy: «Al día».
- Cuando 7-zip.org publique una nueva, **se instala desde acá, sin abrir ninguna página**: el instalador que enlaza 7-zip.org
  para lo instalado (x64, 32 bits, ARM64; el .msi si se instaló con el .msi), en silencio, encima del anterior y en su misma
  carpeta. Igor Pavlov no firma sus instaladores: se ejecuta sólo si no tiene ninguna firma (una inválida o revocada se
  rechaza), su SHA-256 coincide con el que publica el autor para ese mismo archivo y dice ser 7-Zip. Sin ese SHA-256 no se
  instala y la fila lo explica.
- **winget ya no le gana a la página oficial**: la fila del fabricante reemplaza a la de winget y winget se usa sólo si ofrece
  exactamente la versión que publica el fabricante (como antes: trae el hash de su catálogo). Si winget ofrece una más vieja,
  se instala la del fabricante; si ofrece una que el fabricante todavía no publica, no se ofrece y el detalle lo dice.
- **GitHub no manda sobre la página oficial**, para todos los programas: si en la búsqueda se leyó la versión que publica la
  página oficial del programa y es anterior a la de GitHub, la de GitHub no se ofrece hasta que la página oficial la publique
  (lo dicen el detalle de la fila y el resumen). Y cuando una versión de GitHub no se puede instalar desde acá, «Actualizar»
  abre la página oficial que registra el programa, no la de GitHub.
- «Se instala una al lado de la otra» se decide también por la carpeta: «7-Zip 26.05 (x64)» en `C:\Program Files\7-Zip` se
  reemplaza; «UltiMaker Cura 5.13.0» en `C:\Program Files\UltiMaker Cura 5.13.0` sigue yendo al lado.
- Los controladores no cambian: ya salían sólo de Windows Update, del Catálogo de Microsoft Update y de las herramientas del
  fabricante del equipo (ninguno de GitHub).

## 2.0.11

- **Ahora se llama Actualizador** (antes SuperProgram), con un **icono nuevo**: un círculo de flechas de actualización con
  una «A» en el centro, en azul y verde. Los ejecutables son `Actualizador.exe`, `Actualizador.Cli.exe` y
  `Actualizador.Aviso.exe`; se instala en `C:\Program Files\Actualizador` y la entrega es `Actualizador-2.0.11.7z`.
- **Instalar encima de SuperProgram** pasa todo al nombre nuevo, recién cuando comprobó que Actualizador abre bien:
  - quita SuperProgram entero (carpeta del programa, accesos directos de todos los usuarios y su entrada en «Aplicaciones
    instaladas»);
  - las opciones, el historial y las ignoradas de cada usuario pasan de `%LOCALAPPDATA%\SuperProgram` a
    `%LOCALAPPDATA%\Actualizador`, y la política y el registro de cambios, de `%ProgramData%\SuperProgram` a
    `%ProgramData%\Actualizador`;
  - las revisiones programadas se vuelven a crear con el nombre nuevo y el mismo horario (si alguna no se puede, lo dice y
    se activa otra vez en Opciones › Actualizaciones).
- Lo viejo se sigue reconociendo: la directiva de grupo en `HKLM\SOFTWARE\Policies\SuperProgram` vale si no está la de
  `...\Actualizador`, y en la carpeta de versiones nuevas también cuentan las entregas `SuperProgram-x.y.z.7z`.
- **Avast ya no lo corta (IDP.Generic, «Escudo de comportamiento»).** Al terminar cada búsqueda se anotaba en el registro
  de cambios descifrando su clave con DPAPI (`CryptUnprotectData`), lo mismo que hacen los programas que roban contraseñas
  del navegador. La clave ahora está en `clave.key`, protegida sólo por el permiso de la carpeta (que ya era lo que la
  protegía: con DPAPI de la máquina cualquier proceso del equipo la descifraba). El registro de antes se guarda entero,
  sin descifrar, en `auditoria\anterior-AAAAMMDD-HHMMSS`, y empieza una cadena nueva.
- Se sacó el código que quedaba de la época del auditor y ya no se usaba: cuentas y grupos locales, política de
  contraseñas, perfiles Wi-Fi y puertos abiertos. Una prueba controla que no vuelva nada de eso.
- Portable: si venías usando la de SuperProgram, copiá su carpeta `Datos` junto a `Actualizador.exe` para seguir con tus
  opciones e historial.

## 2.0.10

- **Versión portable**, además de la que se instala: `Portable.cmd` (o `build\EmpaquetarPortable.ps1`) arma
  `dist\SuperProgram-2.0.10-portable.7z` (un .zip si no está 7-Zip). Se descomprime donde sea (un pendrive) y se usa sin
  instalar nada:
  - `SuperProgram.exe` y `SuperProgram.Cli.exe` como un solo archivo cada uno, con .NET 8 adentro. Las bibliotecas nativas
    que hubiera quedan al lado y no se extraen a `%TEMP%` (el programa corre como administrador). El catálogo
    `fuentes-oficiales.json` también queda al lado, editable.
  - Lo que la hace portable es `SuperProgram.portable` junto al .exe. Guarda todo en `Datos\` al lado: las opciones del
    técnico (valen en todos los equipos) y, en `Datos\equipos\NOMBRE-DEL-EQUIPO\`, lo de cada equipo: historial,
    ignoradas, registros, diagnósticos y el estado de la Store. Si ahí no se puede escribir, usa la carpeta del usuario y lo
    dice en «Acerca de» y en el registro.
  - Elevada, antes de escribir en `Datos` se controla que no haya enlaces (junction/symlink) desde la carpeta del programa.
    De paso, una carpeta de datos fuera de `%LOCALAPPDATA%` ahora se controla entera (antes no se controlaba).
  - Sin revisión programada (la tarea quedaría apuntando a un pendrive) ni «Desinstalar»; el título dice «(portable)».
  - El aviso de versiones nuevas reconoce las entregas portables y, para la portable, dice que se reemplaza la carpeta.
  - Antes de comprimir se prueba: la consola armada tiene que arrancar y decir «portable» con su carpeta `Datos`, y la
    ventana tiene que pedir administrador en su manifiesto. Con `-CertificadoHuella` / `-Pfx` se firma como la otra.
  - El instalador quita un `SuperProgram.portable` que se haya colado en Program Files.
- Instalada: el historial, las ignoradas y los registros siguen en `%LOCALAPPDATA%\SuperProgram`, como siempre.

## 2.0.9

Los programas que quedaban «No verificable» (captura del usuario, 06/11/2026), uno por uno:

- **Tor Browser**: se compara con la versión que publica The Tor Project (el mismo archivo que usa su página de descargas). La
  instalada se lee de `tbb_version.json`: el registro y el ejecutable dicen la de Firefox ESR (140.17.0), que no sirve para
  comparar. También la copia descomprimida en el Escritorio (sin entrada en el registro). «Actualizar» abre su página de
  descarga; Tor Browser también se actualiza solo al abrirlo. Si no se encuentra `tbb_version.json`, no se compara (antes
  140 contra 16 habría dado cualquier cosa).
- **Samsung USB Driver for Mobile Phones**: se compara con la página de Samsung para desarrolladores (hoy publica la 1.9.5.0;
  la de la captura era la 1.9.0.0). «Actualizar» abre esa página.
- **LaunchBox y LaunchBox Big Box**: se comparan con el registro de cambios oficial (hoy 14.1; la de la captura era la 13.26),
  sin contar las betas. «Actualizar» abre su página de descarga (el instalador se pide con un formulario).
- **Juegos de Steam** (Counter-Strike 2, Quake Enhanced…): Steam guarda en `appmanifest_N.acf` si el juego está al día o tiene
  una actualización pendiente; ahora se lee. Al día: «Según Steam está al día (compilación N)». Pendiente: se puede marcar y
  «Actualizar» le pide a Steam que verifique el juego y baje lo que falta (`steam://validate/N`).
- **Componentes de Windows** (Internet Explorer, «Sistema operativo Microsoft® Windows®», Application Compatibility Fix
  Database): pasan a «Al día»: no tienen actualizaciones propias, van con el sistema y lo pendiente aparece en Windows Update.
- **Tus propios programas** (Reloj Monitor, Virtual Pet, Ríos y Clima del Chaco…): Opciones › «Tus programas» › la carpeta
  donde dejás sus versiones. Se reconocen por el nombre del archivo, sin importar espacios, guiones ni acentos
  («RelojMonitor_1.9.0.7z», «Virtual Pet 2.0.2 Setup.exe», «Rios_y_Clima_del_Chaco-1.1.zip»), también en subcarpetas. Si hay
  una más nueva, «Actualizar» abre la carpeta con ese archivo marcado (no se ejecutan instaladores sin firma). La revisión
  programada también la usa.
- **Mejor explicados** los que no tienen ninguna fuente de versiones: las copias reempaquetadas de juegos (ElAmigos, FitGirl,
  DODI…), Counter-Strike 1.6 fuera de Steam, ACDSync (va con ACDSee) y PCHelpSoft Driver Updater (actualizador de
  controladores de terceros que varios antivirus marcan como no deseado: conviene desinstalarlo).
- Política de la empresa: nueva fuente `externo` (Steam y la carpeta de versiones) para apagarla.

## 2.0.8

- **Siempre como administrador.** La ventana pide `requireAdministrator`: se abre siempre elevada (aviso de UAC; un usuario
  común escribe la contraseña de un administrador). Controladores, Windows Update y los programas instalados para todo el
  equipo ya no fallan por falta de permisos. La consola y el aviso de la revisión programada siguen sin elevar (scripts y
  Programador de tareas); el aviso abre la ventana con el UAC.
- **No busca al abrir.** Arranca con «Tocá «Buscar actualizaciones» para empezar» y sólo busca con ese botón (F5). La
  excepción es abrirla desde el aviso de la revisión programada («Revisar en SuperProgram»), que es pedirlo. Se quitó la
  opción «Buscar actualizaciones apenas se abre».
- **«Actualizar» instala sólo lo marcado.** Nada aparece marcado solo después de buscar; «Actualizar (N)» se habilita con
  algo marcado y N es exactamente lo que se va a instalar. Antes «Actualizar todo» marcaba lo recomendado si no había nada
  marcado y, al terminar, volvía a buscar e instalaba solo lo que apareciera («todo de una vez»): se instalaban cosas que no
  se habían elegido. Se quitó esa opción de la ventana (la consola la conserva). Al terminar, todo queda desmarcado.
- **«Marcar todo» / «Desmarcar todo».** Un solo botón (antes «Marcar «Para actualizar»» y «Desmarcar todo»): marca todo lo
  que se puede actualizar en la lista que se está viendo (con el tipo y el filtro elegidos), incluidas las opcionales; si ya
  está todo marcado, desmarca todo. También Ctrl+A en la lista. En la confirmación, las opcionales dicen «(opcional)».
- **Tema claro / oscuro.** Botón en la barra y Opciones › Tema: «Según Windows» (de fábrica, sigue el cambio con la ventana
  abierta), «Claro» u «Oscuro». Se aplica a todas las ventanas (opciones, confirmación, historial, página oficial), a los
  menús, a la barra de título y a las barras de desplazamiento.
- **Más simple.** Arriba tres botones (Buscar actualizaciones · Actualizar · Marcar todo), el tema y «Más» (historial,
  registro, opciones, exportar, diagnóstico, política, acerca de, desinstalar). El estado en una frase, las dos listas como
  pestañas («Disponibles» y «Al día y sin verificar»), menos columnas (Origen y Detalle en el recuadro de abajo) y el nombre
  primero. «Buscar actualizaciones» pasa a «Detener» mientras busca o instala. El historial se abre en su propia ventana.
- **Arreglos**
  - Una fila marcada que se estaba instalando aparecía con la casilla vacía (captura del usuario: la Store «Instalando…» sin
    marcar): parecía que se instalaba algo que no se había elegido. Ahora sigue marcada (en gris) hasta que termina.
  - La lista volvía a la primera fila cada vez que se rearmaba (al filtrar, al ignorar, al cambiar de tipo); ahora conserva
    la fila elegida y el desplazamiento.
  - Cada archivo cuya firma se verificaba dejaba un certificado sin liberar (8 lugares: instaladores de winget, navegadores,
    fabricantes, Office, Catálogo de Microsoft Update, herramientas del fabricante y el diagnóstico de HTTPS). Ahora se
    libera siempre (`Native.SignerSubject`).
  - Opciones: la hora de la revisión programada en dos campos (el selector de hora de Windows no respeta el tema oscuro).

## 2.0.7

- **El desinstalador no funcionaba.** Configuración › Aplicaciones abre los desinstaladores sin permiso de administrador;
  el script decía «Hace falta ejecutarlo como administrador» y la ventana se cerraba sola, sin quitar nada. Además la
  pregunta era «[s/N]»: Enter cancelaba. Rehecho:
  - pide el permiso (UAC) él mismo y se vuelve a abrir con las mismas opciones;
  - Enter confirma, y al final espera Enter para que se pueda leer qué hizo (salvo con `-Silencioso`);
  - cierra todos los procesos de SuperProgram (también los de otras sesiones) y espera a que suelten los archivos;
  - borra la carpeta con reintentos; si algo sigue bloqueado, se termina de borrar al reiniciar Windows;
  - quita las carpetas `SuperProgram.anterior-*` que dejan las instalaciones que no terminaron, y los accesos directos de
    **todos** los usuarios (antes sólo los comunes);
  - la entrada de «Aplicaciones instaladas» se quita **al final**: antes era lo primero, y si fallaba el borrado de la
    carpeta quedaba medio desinstalado y sin forma de volver a desinstalarlo. Antes de borrar la carpeta deja una copia del
    desinstalador en `%ProgramData%\SuperProgram` y la entrada apunta a ella;
  - deja registro en `%ProgramData%\SuperProgram\desinstalacion.log`.
- **Más › Desinstalar SuperProgram…** en la ventana.
- **Instalador**: registra el desinstalador con la ruta completa de PowerShell (no se busca en el PATH), comprueba que el
  script quede junto al programa y cancela un borrado pendiente de una desinstalación anterior (al reiniciar habría
  borrado la instalación nueva).
- **Instalar.cmd**: la elevación ya no se rompe con una comilla simple en la ruta (`C:\Users\O'Brien`); `/quitar` no
  pregunta dos veces.
- **Una sola instalación a la vez en el equipo** (ventana, consola u otra ventana abierta, también en otra sesión): dos a la
  vez se hacían fallar entre sí («hay otra instalación en curso») y parecía que no actualizaba.
- **Opciones, ignorados, historial, páginas oficiales indicadas a mano y revisión programada se guardan enteros o nada**:
  se escribían directo y un corte de luz o un cierre forzado dejaba el archivo cortado (se perdían sin aviso).

## 2.0.6

- **Dos listas en vez de cuatro tarjetas.** «Para actualizar · N opcionales» muestra arriba lo que instala «Actualizar todo»
  (ya marcado) y debajo las opcionales, en azul y con el estado «Disponible (opcional)», para marcar a mano. «Al día · N sin
  verificar» muestra todo lo demás que se revisó: primero lo que quedó sin verificar y después lo que está al día, con cuántos
  programas, controladores y apps se revisaron. Con cuatro tarjetas, lo opcional y lo no verificado quedaban fuera de la vista
  y parecía que el programa no los había detectado. El número grande sigue siendo el mismo del título y del botón.
- **Más programas comparados con el catálogo de winget** (los que winget no reconoce como instalados):
  - el nombre se limpia también del **idioma del instalador** («Mozilla Thunderbird (x64 es-AR)», «Microsoft 365 - es-es»),
    de «(User)», «(Machine)» y de versiones como «128.3.1esr»;
  - si `winget search` no trae nada, se busca **sin la última palabra** («Opera Stable» → «Opera»);
  - se acepta un **nombre parecido** (uno empieza con el otro) sólo con el **mismo editor**, un único paquete posible y sin
    que la diferencia sea otra edición (Viewer, CLI, Server, Beta, Portable, SDK…). Queda entre las opcionales;
  - los programas **con actualizador propio** (Opera, Zoom, Discord, Telegram, Spotify…) también se buscan: antes
    quedaban «sin verificar» siempre. Siguen afuera los juegos, los antivirus, los paquetes de controladores y Office MSI;
  - las **copias portables** (VLC o GOM copiados a una carpeta, encontradas por su acceso directo) se comparan con el
    catálogo; si hay una versión nueva queda opcional y, si la marcás, se instala la oficial (la copia vieja no se toca).
- **Lo encontrado por nombre se instala con el mismo alcance** (`--scope user` o `machine`): elevado, un programa que estaba
  instalado sólo para el usuario se instalaba «para todo el equipo» y quedaban dos copias. Si el manifiesto no declara ese
  alcance, se repite sin `--scope`.
- **Acentos rotos en .NET 8**: .NET 8 no trae las páginas de códigos de Windows (850, 1252) y `Encoding.Default` pasó a ser
  UTF-8. La salida de pnputil y expand.exe, los INF y registros de Windows Installer «ANSI» y las rutas ANSI de los accesos
  directos se leían mal (una ruta con «ó» o «ñ» no se encontraba). Ahora se registran al arrancar y se usan las del sistema.

## 2.0.5

- **UltiMaker Cura se compara siempre con UltiMaker** (catálogo de fabricantes, github.com/Ultimaker/Cura), aunque winget no
  lo reconozca: su instalador registra cada versión con otro nombre («UltiMaker Cura 5.13.0») y en otra carpeta. Una versión
  vieja aparece «Para actualizar» con la última estable y abre su página; la copia vieja que quedó al lado de la nueva se
  marca «Al día» con «podés desinstalarla».
- **Betas a pedido**: Opciones › «Ofrecer también las versiones beta que publica el fabricante» (consola: `--betas`). Con la
  opción, la beta (Cura 5.14.0-beta) aparece en «Opcionales»: nunca se marca sola ni entra en «Actualizar todo», y abre su
  página. Sin la opción, el detalle del programa dice que existe y dónde activarla. La política con la configuración fijada,
  o sin la fuente `github`, la apaga.
- **Error en «Acerca de» y Opciones**: «Revisión programada: No se pudo consultar el Programador de tareas: El sistema no
  puede encontrar el archivo especificado (0x80070002)» cuando no hay revisión programada. .NET convierte algunos errores
  de Windows en otras excepciones (0x80070002 → FileNotFoundException, 0x80070005 → UnauthorizedAccessException) y el
  programa sólo reconocía COMException. Ahora dice «Desactivada.». El mismo arreglo en los mensajes de Windows Update
  (acceso denegado se mostraba con el texto crudo).

## 2.0.4

- **Lo que winget da «Al día» se contrasta con el fabricante.** «Al día» quería decir «igual al catálogo de winget», y el
  catálogo puede ir atrasado días o semanas. Para cada paquete cuyo instalador oficial sale de GitHub (lo dice su propio
  manifiesto en winget-pkgs) se consulta la lista de versiones del proyecto:
  - si hay una versión **estable** más nueva, la fila pasa a «Para actualizar» y se instala con el archivo de GitHub,
    verificado con el SHA-256 que publica GitHub y la misma firma digital que el programa instalado (si no se puede
    verificar, abre su página);
  - si lo único más nuevo es una **beta o RC**, la fila sigue «Al día» y lo dice. Es el caso de **UltiMaker Cura**: la
    última estable es la 5.13.0 y Cura avisa «¡Está disponible Ultimaker Cura 5.14.0-beta.1!»; SuperProgram nunca instala
    versiones preliminares, pero ahora explica de dónde sale ese aviso.
  - Sólo si la numeración del proyecto es la del programa, sin cruzar de serie (Python 3.13 ≠ 3.14). Los manifiestos se
    guardan 14 días y las versiones 6 horas (GitHub admite 60 consultas por hora sin cuenta); se apaga con la política
    (`"fuentes": { "github": false }`) o con Opciones › no consultar a los fabricantes.
- Programas que se instalan uno al lado del otro (el nombre lleva la versión, como «UltiMaker Cura 5.13.0»): la versión
  nueva no se instala encima (dejaría dos copias); se abre su página.
- **Programas sin editor registrado** también se buscan en el catálogo de winget (antes quedaban «Sin verificar» para
  siempre): sólo con el nombre exacto, de al menos 6 letras, y un único paquete con ese nombre. Como no hay editor con qué
  confirmarlo, quedan entre las opcionales.

## 2.0.3

- `Publish.ps1` fallaba con NETSDK1129 («El destino Publish no se admite sin especificar una plataforma de destino»):
  ahora publica con `-f net8.0-windows`.

## 2.0.2

- **Instalación desde cero, siempre.** `Instalar.cmd` quita la versión anterior entera, sea 1.x o 2.x: cierra el
  programa, aparta su carpeta, quita los accesos directos de todos los usuarios, las revisiones programadas viejas y la
  entrada de «Aplicaciones instaladas». Los datos de usuario de las 1.x (auditorías y sus opciones) se borran; con
  `-Limpio` también el historial, los ignorados y las opciones de 2.x.
- **Comprueba que el programa abre** antes de dar la instalación por buena. Si Windows lo bloquea (Smart App Control o
  Control de aplicaciones), muestra el bloqueo del Visor de eventos y **vuelve a dejar la versión anterior** como estaba.
- La desinstalación con `-BorrarDatos` no deja nada de SuperProgram en el equipo.
- Código y pruebas sin las marcas de versiones viejas.

## 2.0.1

- **Smart App Control (Windows 11)**: con Smart App Control activado, Windows bloquea cualquier .exe o .dll recién
  compilado sin firma de código de confianza («Una directiva de control de aplicaciones bloqueó este archivo»,
  0x800711CD). Ahora `Instalar.cmd`, `Build.ps1` y `Publish.ps1` lo detectan **antes** de compilar y explican qué hacer,
  en vez de compilar y fallar al final; el instalador tampoco copia una entrega sin firma que Windows no dejaría abrir.
- `Instalar.cmd` ya no corre las pruebas (son para quien desarrolla: `build\Build.ps1`). Instalar es mucho más rápido.
- Las pruebas leen la salida completa aunque el programa escriba errores (Windows PowerShell 5.1 cortaba en la primera).

## 2.0.0 — reestructuración para uso empresarial

**Actualizador puro.** Se quitó la auditoría del equipo (hardware, red, cuentas, eventos, hallazgos, comparaciones e
informes HTML): SuperProgram hace una sola cosa, mantener al día programas, controladores, Windows y apps de Microsoft
Store. La ventana abre directo en Actualizaciones; el Historial está en la barra (Ctrl+H).

**.NET 8 (LTS).** Reescrito sobre .NET 8 en cinco proyectos con dependencias en un solo sentido:
`Core` (base y seguridad) → `Engine` (motor) → `App` (ventana), `Cli` (consola) y `Aviso` (revisión programada).
Sin paquetes NuGet de terceros. WMI por COM, Visor de eventos por `ReportEventW`, DPAPI por `CryptProtectData`.

### Nuevo
- **Política de la empresa** por GPO (`HKLM\SOFTWARE\Policies\SuperProgram`) o `politica.json` protegido: fuentes
  permitidas, bloqueos con comodines, editores aprobados, punto de restauración obligatorio, sólo administradores,
  opciones bloqueadas, catálogo de fuentes de la empresa. Se comprueba al buscar y otra vez al instalar.
- **Registro de cambios a prueba de alteraciones**: JSONL con HMAC-SHA256 encadenado y clave protegida con DPAPI;
  `SuperProgram.Cli verificar-registro` y «Más › Verificar el registro de cambios».
- **Visor de eventos**: búsquedas, instalaciones, fallas, bloqueos de la política y alteraciones del registro
  (ids 1000–1300) para SIEM y reenvío de eventos.
- **Consola nueva** con comandos (`buscar`, `actualizar`, `historial`, `ignorar`, `programar`, `politica`,
  `verificar-registro`, `diagnostico`), salida `--json`/`--csv` y códigos de salida para automatizar (10 = hay
  pendientes). Ya no pide elevación por su cuenta (`asInvoker`).
- **Exportar la lista** de actualizaciones a CSV (sin inyección de fórmulas) o JSON.
- **Instalación verificada**: `Instalar.cmd` / `Install-SuperProgram.ps1` verifican el SHA-256 de cada archivo (y la firma
  con `-ExigirFirma`), vuelven a la versión anterior si la copia falla, registran el origen del Visor de eventos, crean
  `%ProgramData%\SuperProgram` con permisos de administradores y aceptan `-Silencioso` para Intune/SCCM/RMM.
- **Compilación**: `build\Build.ps1`, `Publish.ps1` (con .NET adentro o dependiente del runtime), `Sign.ps1`
  (Authenticode SHA-256 con sello de tiempo) y `Empaquetar.ps1`.
- Los registros de la aplicación ocultan secretos (contraseñas, tokens, credenciales en direcciones).

### Corregido
- En .NET 8 las fallas de red llegaban como «error de red (UnknownError)»: ahora se distingue sin conexión, servidor
  inexistente, tiempo agotado y certificado inválido.
- Lo bloqueado por la política no se puede «ignorar» ni destrabar desde la lista de ignorados.
- Se mantienen todas las correcciones de 1.7.5–1.7.7 (AIMP con la página nueva, controladores que Windows no aplicaba,
  conteo durante la instalación, el mismo número en título, tarjeta y botón).

### Quitado
- Auditoría, hallazgos, comparaciones, informes HTML/TXT y sus opciones. Los archivos `.bat` (reemplazados por los
  scripts de PowerShell y `Instalar.cmd`).
