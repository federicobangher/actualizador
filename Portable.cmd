@echo off
rem ==========================================================================
rem  Actualizador - arma la version PORTABLE (no se instala):
rem     dist\Actualizador-<version>-portable.7z   (.zip si no esta 7-Zip)
rem  Doble clic. Necesita el SDK de .NET 8 solo en este equipo (lo ofrece
rem  instalar); los equipos donde se usa la portable no necesitan nada.
rem  Firmada:  Portable.cmd -CertificadoHuella <huella>
rem  La version que se instala sigue siendo Instalar.cmd.
rem ==========================================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "PS="%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File"
rem Las pruebas son para quien desarrolla (build\Build.ps1); aca no se corren.
%PS% "%~dp0build\EmpaquetarPortable.ps1" -SinPruebas %*
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" if exist "%~dp0dist\" start "" "%SystemRoot%\explorer.exe" "%~dp0dist"

echo(%cmdcmdline%| findstr /i /c:"%~nx0" >nul && pause
exit /b %RC%
