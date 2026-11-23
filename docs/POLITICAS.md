# Política de la empresa

Con una política, el área de TI decide qué fuentes usa Actualizador, qué no se instala nunca y qué puede cambiar el
usuario. Se aplica igual en la ventana, en la consola y en la revisión programada, y se vuelve a comprobar en cada
instalación (no alcanza con destrabar una fila en la interfaz).

## Dónde se configura

1. **Directiva de grupo / Intune / registro** — `HKLM\SOFTWARE\Policies\Actualizador` (manda sobre el archivo).
2. **Archivo** — `%ProgramData%\Actualizador\politica.json` (UTF-8, hasta 1 MB). **Sólo se aplica si únicamente
   Administradores, SYSTEM o TrustedInstaller pueden modificarlo**; si un usuario común puede escribirlo, se ignora y se
   avisa (Visor de eventos y «Detalles de la búsqueda»). `Install-Actualizador.ps1 -Politica archivo.json` lo instala
   con esos permisos.

Ver la política vigente: «Más › Ver la política de la empresa» o `Actualizador.Cli politica`.

## Referencia

| politica.json | Registro (`HKLM\SOFTWARE\Policies\Actualizador`) | Tipo | De fábrica | Efecto |
|---|---|---|---|---|
| `fuentes.<id>` | `Fuente_<id>` | bool / DWORD (0 apaga) | todas encendidas | Fuente que se consulta (ver tabla de fuentes) |
| `bloquear` | `Bloquear` | lista / REG_MULTI_SZ | vacía | Nombres o identificadores que nunca se instalan (`*` y `?`) |
| `editoresPermitidos` | `EditoresPermitidos` | lista / REG_MULTI_SZ | vacía (todos) | Si tiene algo, sólo se instalan **programas** de esos editores |
| `permitirAbrirPaginas` | `PermitirAbrirPaginas` | bool / DWORD | sí | Abrir la página de descarga de lo que no se instala solo |
| `permitirInstaladoresSinFirma` | `PermitirInstaladoresSinFirma` | bool / DWORD | sí | Instalar desde la página oficial un instalador sin firma digital cuando lo instalado tampoco la tiene (el autor no firma) y es del mismo producto y de la versión publicada (2.0.17). Apagado, esos programas abren su página |
| `permitirForzarControladores` | `PermitirForzarControladores` | bool / DWORD | sí | Instalar controladores que Windows sólo acepta forzados |
| `permitirInstalarWinget` | `PermitirInstalarWinget` | bool / DWORD | sí | Instalar winget si falta |
| `permitirInstaladoresConVentana` | `PermitirInstaladoresConVentana` | bool / DWORD | sí | Mostrar la ventana de los instaladores sin modo silencioso |
| `exigirPuntoDeRestauracion` | `ExigirPuntoDeRestauracion` | bool / DWORD | no | Siempre crear un punto de restauración antes de instalar |
| `exigirAdministrador` | `ExigirAdministrador` | bool / DWORD | no | Sólo se instala ejecutando como administrador |
| `bloquearOpciones` | `BloquearOpciones` | bool / DWORD | no | El usuario no cambia fuentes ni catálogo en Opciones |
| `catalogoFuentesUrl` | `CatalogoFuentesUrl` | texto https / REG_SZ | vacío | Catálogo de fuentes oficiales de la empresa (sólo `https://`) |

### Fuentes (`<id>`)

| id | Qué es |
|---|---|
| `winget` | Repositorio de winget |
| `navegadores` | Chrome, Edge, Firefox (sus servicios de versiones) |
| `fabricante` | Sitios de los fabricantes (Office, Vivaldi, Adobe, AIMP…) |
| `github` | Versiones publicadas en GitHub |
| `sitio` | Páginas oficiales del catálogo `fuentes-oficiales.json` |
| `windowsupdate` | Windows Update (actualizaciones y controladores) |
| `catalogo` | Catálogo de Microsoft Update (controladores) |
| `fabricante_equipo` | Herramientas del fabricante del equipo (Lenovo, Dell, HP…) |
| `store` | Microsoft Store |
| `externo` | Juegos de Steam (se le pide a Steam que actualice) y la carpeta de versiones de programas propios |

Los patrones comparan el texto **entero**, sin distinguir mayúsculas: `TeamViewer*` bloquea «TeamViewer 15» y
`TeamViewer.TeamViewer`; `7-Zip` sólo bloquea exactamente «7-Zip».

## Ejemplo (politica.json)

```json
{
  "fuentes": { "github": false, "fabricante_equipo": false },
  "bloquear": [ "Oracle.JavaRuntimeEnvironment*", "TeamViewer*" ],
  "editoresPermitidos": [ "Microsoft*", "Google LLC", "Mozilla", "Igor Pavlov", "Adobe*" ],
  "permitirForzarControladores": false,
  "permitirInstaladoresConVentana": false,
  "exigirPuntoDeRestauracion": true,
  "bloquearOpciones": true,
  "catalogoFuentesUrl": "https://intranet.empresa.local/ti/fuentes-oficiales.json"
}
```

## Ejemplo (registro, para un .reg o una GPO de preferencias)

```
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Actualizador]
"Fuente_github"=dword:00000000
"PermitirForzarControladores"=dword:00000000
"ExigirPuntoDeRestauracion"=dword:00000001
"BloquearOpciones"=dword:00000001
"Bloquear"=hex(7):54,00,65,00,61,00,6d,00,56,00,69,00,65,00,77,00,65,00,72,00,2a,00,00,00,00,00
```

(`Bloquear` = `TeamViewer*`; en el Editor de directivas o en Intune se carga como lista de texto.)

## Qué ve el usuario

Lo bloqueado aparece en «Sin verificar» con el motivo («No se instala: bloqueada por la política de la empresa
(«TeamViewer*»)»), desmarcado y sin poder marcarse ni «ignorarse». En Opciones, lo que fija la política se ve pero no se
puede cambiar. Un intento de instalar algo bloqueado (por ejemplo desde la consola) queda como «Fallida» con el motivo,
en el registro de cambios y en el Visor de eventos (evento 1200).
