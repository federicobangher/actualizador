<#
.SYNOPSIS
    Arma la entrega para instalar en los equipos: dist\Actualizador-<versión>.7z (.zip si no está 7-Zip)

.DESCRIPTION
    Publica (ver Publish.ps1) y junta en un .7z (con 7-Zip instalado; si no, en un .zip) lo que necesita el técnico:
        Instalar.cmd, Install-Actualizador.ps1, Uninstall-Actualizador.ps1, politica.ejemplo.json, LEEME.txt
        Actualizador\   (los ejecutables publicados y SHA256SUMS.txt)
    Si dejás las entregas en una carpeta compartida y la configurás en Actualizador › Opciones › «Dónde se publican las
    versiones nuevas», cada equipo avisa cuando hay una versión más nueva.

    La versión portable (no se instala) se arma con EmpaquetarPortable.ps1 (o Portable.cmd).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\Empaquetar.ps1
    powershell -ExecutionPolicy Bypass -File build\Empaquetar.ps1 -CertificadoHuella 0123… -Destino \\servidor\Actualizador
#>
[CmdletBinding()]
param(
    [ValidateSet('Autonomo', 'DependeDelRuntime')]
    [string]$Modo = 'Autonomo',
    [string]$Destino,
    [switch]$SinPruebas,
    [string]$CertificadoHuella,
    [string]$Pfx
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$raiz = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Comun.ps1')

$version = Get-ActualizadorVersion $raiz
if (-not $Destino) { $Destino = Join-Path $raiz 'dist' }
$nombre = "Actualizador-$version"
$armado = Join-Path $raiz "obj\entrega\$nombre"

if (Test-Path -LiteralPath $armado) { Remove-Item -LiteralPath $armado -Recurse -Force }
New-Item -ItemType Directory -Path $armado -Force | Out-Null

$pub = @{ Modo = $Modo; Salida = (Join-Path $armado 'Actualizador') }
if ($SinPruebas) { $pub.SinPruebas = $true }
if ($CertificadoHuella) { $pub.CertificadoHuella = $CertificadoHuella }
if ($Pfx) { $pub.Pfx = $Pfx }
& (Join-Path $PSScriptRoot 'Publish.ps1') @pub

Write-Paso 'Armando la entrega'
foreach ($f in @('Instalar.cmd', 'Install-Actualizador.ps1', 'Uninstall-Actualizador.ps1', 'politica.ejemplo.json', 'LEEME.txt')) {
    Copy-Item -LiteralPath (Join-Path $raiz "deploy\$f") -Destination $armado -Force
}
New-Item -ItemType Directory -Path $Destino -Force | Out-Null
# 2.0.16: como la portable, en .7z si está 7-Zip (antes siempre .zip). Una entrega anterior de esta versión en el otro formato
# se quita, para que no queden dos archivos distintos con el mismo número.
foreach ($ext in @('7z', 'zip')) {
    foreach ($viejo in @((Join-Path $Destino "$nombre.$ext"), (Join-Path $Destino "$nombre.$ext.sha256"))) {
        if (Test-Path -LiteralPath $viejo) { Remove-Item -LiteralPath $viejo -Force }
    }
}
$sieteZip = Find-7Zip
if ($sieteZip) {
    $archivo = Join-Path $Destino "$nombre.7z"
    # El contenido de la carpeta, no la carpeta: al descomprimir queda Instalar.cmd a la vista, igual que con el .zip.
    & $sieteZip 'a' '-t7z' '-mx=9' '-bso0' '-bsp0' $archivo (Join-Path $armado '*')
    if ($LASTEXITCODE -ne 0) { throw "7-Zip terminó con el código $LASTEXITCODE." }
} else {
    Write-Host 'No se encontró 7-Zip: se arma un .zip (con 7-Zip instalado sale un .7z).' -ForegroundColor Yellow
    $archivo = Join-Path $Destino "$nombre.zip"
    Compress-Archive -Path (Join-Path $armado '*') -DestinationPath $archivo -CompressionLevel Optimal
}
$h = (Get-FileHash -LiteralPath $archivo -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archivo.sha256", "$h  $(Split-Path -Leaf $archivo)`r`n", (New-Object Text.UTF8Encoding($false)))
Write-Paso "Entrega: $archivo"
Write-Host "SHA-256: $h"
