# Actualizador 2.0 — programas, controladores y Windows

Actualiza desde un solo lugar **programas, controladores, Windows y apps de Microsoft Store**, para usar equipo por
equipo (técnico) o desatendido (scripts, RMM, Intune/SCCM).

| Qué | De dónde sale la versión nueva |
|---|---|
| Programas | winget, páginas oficiales de cada fabricante (Office, navegadores, Adobe, AIMP, 7-Zip, controlador de NVIDIA, GitHub…) y el catálogo `fuentes-oficiales.json`. Si la página oficial publica una versión que winget todavía no tiene, se ubica su instalador y se instala desde acá, verificado (2.0.17); la página se abre sólo si no se puede |
| Controladores | Windows Update, Catálogo de Microsoft Update y herramientas del fabricante del equipo |
| Windows | Windows Update (API del Agente de Windows Update) |
| Microsoft Store | La propia Store (actualiza todas las apps) |

Nada se instala sin verificar: hash SHA-256 publicado, firma Authenticode del mismo editor que el programa instalado y
versión real del archivo después de instalar.

## Uso

**Ventana** (`Actualizador.exe`, siempre como administrador): no busca nada al abrir. «Buscar actualizaciones» (F5) compara todo;
después se marca lo que se quiere («Marcar todo» / «Desmarcar todo», o una por una) y «Actualizar» instala **sólo lo marcado**.
Dos listas: «Disponibles» (lo recomendado y, en azul, lo opcional) y «Al día y sin verificar». Tema claro u oscuro (o según
Windows). Historial (Ctrl+H), registro paso a paso, opciones, diagnóstico para soporte y exportación a CSV/JSON en «Más».

Lo que no está en ningún catálogo también se intenta comparar: los juegos de Steam (según Steam), los componentes de Windows
(van con Windows Update) y **tus propios programas**, si en Opciones › «Tus programas» indicás la carpeta donde dejás sus
versiones nuevas («RelojMonitor_1.9.0.7z»).

**Consola** (`Actualizador.Cli.exe`):

```
Actualizador.Cli buscar --json C:\Inventario\
Actualizador.Cli actualizar --tipos programas,controladores --cerrar-programas
Actualizador.Cli politica
Actualizador.Cli verificar-registro
Actualizador.Cli ayuda
```

Códigos de salida: `0` al día / todo instalado · `10` hay pendientes · `3` alguna falló · `4` cancelado ·
`5` bloqueado por la política · `1` argumentos · `2` error.

**Revisión programada**: Opciones › «Buscar actualizaciones automáticamente y avisar» (o `Actualizador.Cli programar
semanal --dia viernes --hora 12:30`). Busca sin instalar nada y avisa junto al reloj.

## Empresa

- **Política** por directiva de grupo (`HKLM\SOFTWARE\Policies\Actualizador`) o `%ProgramData%\Actualizador\politica.json`:
  fuentes permitidas, bloqueos, editores aprobados, punto de restauración obligatorio, opciones bloqueadas… → [docs/POLITICAS.md](docs/POLITICAS.md)
- **Trazabilidad**: cada búsqueda e instalación va al Visor de eventos (Aplicación, origen `Actualizador`) y a un
  registro de cambios con HMAC encadenado que detecta borrados o ediciones → [docs/SEGURIDAD.md](docs/SEGURIDAD.md)
- **Instalación verificada**: `Instalar.cmd` comprueba el SHA-256 de cada archivo (y la firma, con `-ExigirFirma`).

## Compilar

Hace falta el **SDK de .NET 8** sólo en el equipo donde se compila (`winget install Microsoft.DotNet.SDK.8`).

```
powershell -ExecutionPolicy Bypass -File build\Build.ps1         # compila y corre las pruebas
powershell -ExecutionPolicy Bypass -File build\Publish.ps1       # publish\Actualizador (con .NET adentro)
powershell -ExecutionPolicy Bypass -File build\Empaquetar.ps1    # dist\Actualizador-2.0.38.7z para los equipos (.zip sin 7-Zip)
Instalar.cmd                                                     # publica (si hace falta) e instala en este equipo
Instalar.cmd /quitar                                             # desinstala (también: Configuración › Aplicaciones, o Más › Desinstalar)
Publicar.cmd                                                     # publica esta versión en GitHub: los equipos se actualizan solos
```

**Actualización automática (2.0.32):** cada versión se publica en GitHub (`federicobangher/actualizador`): sola, cuando llega a `main`
una versión que todavía no está publicada (`.github/workflows/publicar.yml`, desde la 2.0.36), o con `Publicar.cmd` desde una PC. Cada
equipo con la 2.0.32 o posterior la encuentra al abrir Actualizador, la baja, verifica su SHA-256, la instala en silencio y se vuelve a
abrir. La portable también, desde la 2.0.37: copia los archivos nuevos en su carpeta sin tocar «Datos».

Firma de código: `build\Publish.ps1 -CertificadoHuella <huella>` (o `-Pfx archivo.pfx`).

## Versión portable

Además de la que se instala, una que **no se instala**: para llevar en un pendrive y usar equipo por equipo.

```
Portable.cmd                                                     # dist\Actualizador-2.0.38-portable.7z (o .zip sin 7-Zip)
powershell -ExecutionPolicy Bypass -File build\EmpaquetarPortable.ps1 -CertificadoHuella <huella>   # firmada
```

- `Actualizador.exe` (ventana) y `Actualizador.Cli.exe` (consola), **un solo archivo cada uno con .NET 8 adentro**: el
  equipo no necesita nada instalado. Lo que la hace portable es el archivo `Actualizador.portable` que va al lado.
- Guarda todo en la carpeta `Datos` de al lado: las opciones del técnico en `Datos\` y lo de cada equipo (historial,
  ignoradas, registros, diagnósticos) en `Datos\equipos\NOMBRE-DEL-EQUIPO\`. Si ahí no se puede escribir (CD, pendrive
  protegido), usa `%LOCALAPPDATA%\Actualizador` como la instalada.
- Respeta la política de la empresa y deja el registro de cambios y el Visor de eventos en el equipo, como la instalada.
- No se registra en Aplicaciones ni en el menú Inicio (se quita borrando la carpeta) y no programa revisiones automáticas.
- Antes de comprimir, el script prueba la consola armada y que la ventana pida administrador. Detalle en
  `deploy\LEEME-portable.txt` (va como LEEME.txt adentro de la portable).

Arquitectura y decisiones: [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md). Cambios: [CHANGELOG.md](CHANGELOG.md).
