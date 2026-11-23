@echo off
rem ==========================================================================
rem  Actualizador - instala o actualiza en este equipo.
rem  Doble clic (pide permiso de administrador) o, desatendido:
rem     Instalar.cmd -Silencioso [-Limpio] [-Politica ruta\politica.json] [-ExigirFirma]
rem  Siempre instala desde cero: quita la version anterior (tambien las 1.x) y
rem  comprueba que el programa abra antes de dar la instalacion por buena.
rem  Desinstalar: Configuracion > Aplicaciones, o
rem     Instalar.cmd /quitar
rem ==========================================================================
setlocal EnableExtensions
cd /d "%~dp0"

rem Permiso de administrador (Program Files, ProgramData y el Visor de eventos lo necesitan).
fsutil dirty query %SystemDrive% >nul 2>&1
if errorlevel 1 (
    echo Pidiendo permiso de administrador...
    rem La ruta y los argumentos van por variables de entorno: una comilla simple en la ruta
    rem (C:\Users\O'Brien) o en un argumento ya no rompe el comando de PowerShell.
    set "SP_SELF=%~f0"
    set "SP_ARGS=%*"
    "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "$a=$env:SP_ARGS; if ($a) { Start-Process -FilePath $env:SP_SELF -ArgumentList $a -Verb RunAs } else { Start-Process -FilePath $env:SP_SELF -Verb RunAs }" >nul 2>&1
    if errorlevel 1 echo No se dio el permiso de administrador: no se cambio nada.& pause
    exit /b
)

set "PS="%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File"
set "INSTALAR=%~dp0Install-Actualizador.ps1"
if not exist "%INSTALAR%" set "INSTALAR=%~dp0deploy\Install-Actualizador.ps1"
set "QUITAR=%~dp0Uninstall-Actualizador.ps1"
if not exist "%QUITAR%" set "QUITAR=%~dp0deploy\Uninstall-Actualizador.ps1"

if /i "%~1"=="/quitar" (
    %PS% "%QUITAR%" -Silencioso
    goto :fin
)

rem Codigo fuente: se publica primero (necesita el SDK de .NET 8; lo ofrece instalar). Si ya estaba publicado,
rem Publish.ps1 -SiHaceFalta solo vuelve a compilar cuando lo publicado es de otra version o anterior al codigo:
rem asi nunca se instala una publicacion vieja que quedo en la carpeta.
rem Las pruebas son para quien desarrolla (build\Build.ps1); al instalar no se corren.
if not exist "%~dp0Actualizador\Actualizador.exe" (
    if exist "%~dp0build\Publish.ps1" (
        %PS% "%~dp0build\Publish.ps1" -SinPruebas -SiHaceFalta
        if errorlevel 1 goto :fin
    )
)
%PS% "%INSTALAR%" %*

:fin
set "RC=%ERRORLEVEL%"
echo(%cmdcmdline%| findstr /i /c:"%~nx0" >nul && pause
exit /b %RC%
