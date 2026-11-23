@echo off
rem ==========================================================================
rem  Actualizador - PUBLICA la version de esta carpeta en GitHub para que todos
rem  los equipos se actualicen solos (como Virtual Pet).
rem  Doble clic. La primera vez: instala GitHub CLI si falta, te pide iniciar
rem  sesion en GitHub (una sola vez) y crea el proyecto federicobangher/actualizador.
rem  Otro proyecto:  Publicar.cmd -Repo usuario/proyecto
rem  Necesita el SDK de .NET 8 solo en este equipo (para compilar).
rem ==========================================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "PS="%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File"
%PS% "%~dp0build\Publicar.ps1" %*
set "RC=%ERRORLEVEL%"

echo(%cmdcmdline%| findstr /i /c:"%~nx0" >nul && pause
exit /b %RC%
