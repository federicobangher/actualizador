<#
.SYNOPSIS
    Compila la solución y corre las pruebas.

.DESCRIPTION
    Necesita el SDK de .NET 8 (https://dotnet.microsoft.com/download/dotnet/8.0 o
    «winget install Microsoft.DotNet.SDK.8»). Devuelve 0 si compila y pasan todas las pruebas.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\Build.ps1
    powershell -ExecutionPolicy Bypass -File build\Build.ps1 -Configuracion Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuracion = 'Release',
    [switch]$SinPruebas
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$raiz = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Comun.ps1')

Assert-SinSmartAppControl
Assert-DotNetSdk
Write-Paso "Compilando Actualizador ($Configuracion)"
Invoke-DotNet @('build', (Join-Path $raiz 'Actualizador.sln'), '-c', $Configuracion, '-nologo')

if (-not $SinPruebas) {
    Invoke-Pruebas -Raiz $raiz -Configuracion $Configuracion
}
Write-Paso 'Listo.'
