@echo off
rem ==========================================================================
rem  Actualizador - FIRMA la ultima version publicada en GitHub con tu clave
rem  (que nunca sube a GitHub). Los equipos se actualizan solos solo con
rem  versiones firmadas. Doble clic. La primera vez crea la clave (pide una
rem  contrasena), guarda un respaldo y sube la clave PUBLICA al proyecto.
rem     Firmar.cmd -Version 2.0.46      otra version
rem     Firmar.cmd -Respaldo E:\        guarda el respaldo de la clave en E:\
rem ==========================================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "PS="%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File"
%PS% "%~dp0build\Firmar.ps1" %*
set "RC=%ERRORLEVEL%"

echo(%cmdcmdline%| findstr /i /c:"%~nx0" >nul && pause
exit /b %RC%
