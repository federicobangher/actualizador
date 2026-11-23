<#
.SYNOPSIS
    Firma con Authenticode (SHA-256 y sello de tiempo RFC 3161) los ejecutables y bibliotecas de Actualizador.

.DESCRIPTION
    Usa signtool.exe del Windows SDK. El certificado puede estar en el almacén (por su huella, recomendado: la clave
    privada no sale del almacén o del token) o en un .pfx (se pide la contraseña; nunca se pasa por la línea de comandos).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\Sign.ps1 -Carpeta publish\Actualizador -CertificadoHuella 0123…
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Carpeta,
    [string]$CertificadoHuella,
    [string]$Pfx,
    [string]$SelloDeTiempo = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'Comun.ps1')

if (-not $CertificadoHuella -and -not $Pfx) { throw 'Indicá -CertificadoHuella o -Pfx.' }

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits) {
        $c = Get-ChildItem -LiteralPath $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($c) { return $c.FullName }
    }
    throw 'No se encontró signtool.exe: instalá el Windows SDK (componente «Signing Tools for Desktop Apps»).'
}

$signtool = Find-SignTool
$archivos = @(Get-ChildItem -LiteralPath $Carpeta -File | Where-Object { $_.Name -like 'Actualizador*.exe' -or $_.Name -like 'Actualizador*.dll' })
if ($archivos.Count -eq 0) { throw "No hay ejecutables de Actualizador en $Carpeta." }

Write-Paso "Firmando $($archivos.Count) archivos"
$base = @('sign', '/fd', 'SHA256', '/tr', $SelloDeTiempo, '/td', 'SHA256', '/d', 'Actualizador')
if ($CertificadoHuella) {
    $base += @('/sha1', ($CertificadoHuella -replace '\s', ''))
} else {
    $clave = Read-Host -AsSecureString "Contraseña de $Pfx"
    $plano = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($clave))
    $base += @('/f', $Pfx, '/p', $plano)
}
try {
    & $signtool @($base + ($archivos | ForEach-Object { $_.FullName }))
    if ($LASTEXITCODE -ne 0) { throw "signtool terminó con el código $LASTEXITCODE." }
} finally {
    if (Get-Variable -Name plano -ErrorAction SilentlyContinue) { Remove-Variable plano }
}

foreach ($a in $archivos) {
    $s = Get-AuthenticodeSignature -LiteralPath $a.FullName
    if ($s.Status -ne 'Valid') { throw "La firma de $($a.Name) no es válida: $($s.StatusMessage)" }
}
Write-Host 'Firmas verificadas.'
