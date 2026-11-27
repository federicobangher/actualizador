<#
.SYNOPSIS
    Publica Actualizador (ventana, consola y aviso) en una carpeta lista para instalar.

.DESCRIPTION
    Compila en Release, corre las pruebas, publica para Windows x64 en publish\Actualizador y escribe SHA256SUMS.txt
    con el hash de cada archivo (el instalador lo verifica antes de copiar).

    -Modo Autonomo (predeterminado): incluye el runtime de .NET 8; el equipo destino no necesita nada instalado.
    -Modo DependeDelRuntime: más chico; el equipo destino necesita «.NET Desktop Runtime 8» (x64).

    Con -CertificadoHuella o -Pfx firma los ejecutables (Authenticode, SHA-256 y sello de tiempo) ANTES de calcular los
    hashes. En una empresa es lo recomendado: SmartScreen y los antivirus confían en un ejecutable firmado.

    -SiHaceFalta (lo usa Instalar.cmd): no vuelve a publicar si lo que ya está publicado es de esta misma versión y es
    posterior a todo el código. Si el código cambió o es de otra versión, publica de nuevo: antes, con una publicación
    vieja en la carpeta, Instalar.cmd volvía a instalar esos archivos viejos sin avisar. Desde la 2.0.44, si el equipo no
    tiene el SDK de .NET 8, baja esa misma versión ya publicada por GitHub (SHA-256 verificado) en vez de pedir el SDK.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\Publish.ps1
    powershell -ExecutionPolicy Bypass -File build\Publish.ps1 -CertificadoHuella 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    [ValidateSet('Autonomo', 'DependeDelRuntime')]
    [string]$Modo = 'Autonomo',
    [string]$Salida,
    [switch]$SinPruebas,
    [switch]$SiHaceFalta,
    [string]$CertificadoHuella,
    [string]$Pfx,
    [string]$SelloDeTiempo = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$raiz = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Comun.ps1')

if (-not $Salida) { $Salida = Join-Path $raiz 'publish\Actualizador' }
$version = Get-ActualizadorVersion $raiz
if ($SiHaceFalta) {
    $motivo = Get-MotivoParaPublicar -Raiz $raiz -Salida $Salida -Version $version
    if (-not $motivo) {
        Write-Host "Actualizador $version ya está publicado en $Salida y el código no cambió desde entonces: no se vuelve a compilar." -ForegroundColor DarkGray
        exit 0
    }
    Write-Host "Se publica de nuevo: $motivo." -ForegroundColor Yellow
    # 2.0.44: sin el SDK no se puede compilar; Instalar.cmd usa la misma versión que GitHub ya compiló y publicó.
    if (-not $CertificadoHuella -and -not $Pfx -and -not (Use-DotNetSdk8)) {
        if (Get-EntregaPublicada -Version $version -Salida $Salida -Claves @(Get-ClavesDeFirma $raiz)) { exit 0 }
        Write-Host 'Queda compilarla en este equipo, con el SDK de .NET 8.' -ForegroundColor Yellow
    }
}
Assert-SinSmartAppControl -HayFirma:([bool]($CertificadoHuella -or $Pfx))
Assert-DotNetSdk

if (-not $SinPruebas) { Invoke-Pruebas -Raiz $raiz -Configuracion 'Release' }

Write-Paso "Publicando Actualizador $version ($Modo) en $Salida"
if (Test-Path -LiteralPath $Salida) { Remove-Item -LiteralPath $Salida -Recurse -Force }
New-Item -ItemType Directory -Path $Salida -Force | Out-Null
$autonomo = if ($Modo -eq 'Autonomo') { 'true' } else { 'false' }
foreach ($p in @('Actualizador.App', 'Actualizador.Cli', 'Actualizador.Aviso')) {
    $csproj = Join-Path $raiz "src\$p\$p.csproj"
    # -f: los proyectos declaran <TargetFrameworks> (en plural, por la verificación en Linux) y publish exige elegir uno.
    Invoke-DotNet @('publish', $csproj, '-c', 'Release', '-f', 'net8.0-windows', '-r', 'win-x64', "--self-contained:$autonomo", '-o', $Salida, '-nologo',
                    '-p:DebugType=embedded', '-p:PublishReadyToRun=false', '-p:IncludeNativeLibrariesForSelfExtract=false')
}
Copy-Item -LiteralPath (Join-Path $raiz 'deploy\politica.ejemplo.json') -Destination $Salida -Force
Copy-Item -LiteralPath (Join-Path $raiz 'deploy\Uninstall-Actualizador.ps1') -Destination $Salida -Force

if ($CertificadoHuella -or $Pfx) {
    $firmar = Join-Path $PSScriptRoot 'Sign.ps1'
    $args2 = @{ Carpeta = $Salida; SelloDeTiempo = $SelloDeTiempo }
    if ($CertificadoHuella) { $args2.CertificadoHuella = $CertificadoHuella }
    if ($Pfx) { $args2.Pfx = $Pfx }
    & $firmar @args2
}

Write-Paso 'Calculando SHA256SUMS.txt'
$lineas = New-Object System.Collections.Generic.List[string]
$base = (Resolve-Path -LiteralPath $Salida).Path.TrimEnd('\') + '\'
Get-ChildItem -LiteralPath $Salida -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object FullName | ForEach-Object {
    $h = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $lineas.Add($h + '  ' + $_.FullName.Substring($base.Length))
}
[IO.File]::WriteAllLines((Join-Path $Salida 'SHA256SUMS.txt'), $lineas, (New-Object Text.UTF8Encoding($false)))
Write-Host "$($lineas.Count) archivos."
Write-Paso "Publicado: $Salida"
