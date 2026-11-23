<#
.SYNOPSIS
    Arma la versión portable (no se instala): dist\Actualizador-<versión>-portable.7z

.DESCRIPTION
    Publica la ventana y la consola como un solo .exe cada una, con .NET 8 adentro (el equipo no necesita nada instalado),
    y les agrega:
        Actualizador.portable   el archivo que las hace portables (guardan todo en la carpeta «Datos», al lado)
        LEEME.txt               cómo se usa la portable
        SHA256SUMS.txt          el hash de cada archivo
    No lleva el aviso de la revisión programada (Actualizador.Aviso.exe) ni los scripts de instalación: la portable no
    programa tareas ni se registra en el equipo.

    Antes de comprimir, prueba la consola recién armada («Actualizador.Cli.exe version» tiene que decir «portable») y que
    la ventana pida permiso de administrador.

    Comprime con 7-Zip si está instalado (.7z); si no, en .zip.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\EmpaquetarPortable.ps1
    powershell -ExecutionPolicy Bypass -File build\EmpaquetarPortable.ps1 -CertificadoHuella 0123… -Destino \\servidor\Actualizador
#>
[CmdletBinding()]
param(
    [string]$Destino,
    [switch]$SinPruebas,
    [string]$CertificadoHuella,
    [string]$Pfx,
    [string]$SelloDeTiempo = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$raiz = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Comun.ps1')

$version = Get-ActualizadorVersion $raiz
if (-not $Destino) { $Destino = Join-Path $raiz 'dist' }
$nombre = "Actualizador-$version-portable"
$armado = Join-Path $raiz "obj\entrega\$nombre"
$hayFirma = [bool]($CertificadoHuella -or $Pfx)

Assert-SinSmartAppControl -HayFirma:$hayFirma
Assert-DotNetSdk
if (-not $SinPruebas) { Invoke-Pruebas -Raiz $raiz -Configuracion 'Release' }

Write-Paso "Publicando Actualizador $version portable en $armado"
if (Test-Path -LiteralPath $armado) { Remove-Item -LiteralPath $armado -Recurse -Force }
New-Item -ItemType Directory -Path $armado -Force | Out-Null
foreach ($p in @('Actualizador.App', 'Actualizador.Cli')) {
    $csproj = Join-Path $raiz "src\$p\$p.csproj"
    # Un solo .exe por programa, con .NET adentro (comprimido). Las bibliotecas nativas que hubiera quedan al lado y NO se
    # extraen a %TEMP%: el programa corre como administrador y la carpeta temporal la puede escribir el usuario.
    Invoke-DotNet @('publish', $csproj, '-c', 'Release', '-f', 'net8.0-windows', '-r', 'win-x64', '--self-contained:true', '-o', $armado, '-nologo',
                    '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=false',
                    '-p:DebugType=embedded', '-p:PublishReadyToRun=false')
}
foreach ($exe in @('Actualizador.exe', 'Actualizador.Cli.exe', 'fuentes-oficiales.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $armado $exe))) { throw "Falta $exe en $armado después de publicar." }
}

if ($hayFirma) {
    $firma = @{ Carpeta = $armado; SelloDeTiempo = $SelloDeTiempo }
    if ($CertificadoHuella) { $firma.CertificadoHuella = $CertificadoHuella }
    if ($Pfx) { $firma.Pfx = $Pfx }
    & (Join-Path $PSScriptRoot 'Sign.ps1') @firma
}

Write-Paso 'Agregando el marcador de portable y el LEEME'
$utf8 = New-Object Text.UTF8Encoding($true)
$marcador = @(
    'Este archivo hace que Actualizador funcione como portable: no se instala y guarda sus opciones, el historial y los',
    'registros en la carpeta «Datos», al lado de Actualizador.exe (lo de cada equipo, en Datos\equipos\NOMBRE-DEL-EQUIPO).',
    'Si lo borrás, guarda todo en %LOCALAPPDATA%\Actualizador, como la versión instalada.'
) -join "`r`n"
[IO.File]::WriteAllText((Join-Path $armado 'Actualizador.portable'), $marcador + "`r`n", $utf8)
Copy-Item -LiteralPath (Join-Path $raiz 'deploy\LEEME-portable.txt') -Destination (Join-Path $armado 'LEEME.txt') -Force

Write-Paso 'Probando la portable'
# La ventana pide administrador (UAC) y no se puede abrir sin preguntar: se comprueba que su manifiesto lo pida. Está en los
# recursos del .exe, al principio del archivo (el .NET empaquetado va después).
$ventana = Join-Path $armado 'Actualizador.exe'
$fs = [IO.File]::OpenRead($ventana)
try {
    $n = [int][Math]::Min($fs.Length, 32MB)
    $buf = New-Object byte[] $n
    $leidos = 0
    while ($leidos -lt $n) { $r = $fs.Read($buf, $leidos, $n - $leidos); if ($r -le 0) { break }; $leidos += $r }
} finally { $fs.Dispose() }
$texto = [Text.Encoding]::ASCII.GetString($buf, 0, $leidos)
if ($texto -notmatch 'requestedExecutionLevel\s+level="requireAdministrator"') {
    throw 'Actualizador.exe no quedó con el pedido de administrador en su manifiesto (¿se compiló fuera de Windows?). No se entrega.'
}
Write-Host 'La ventana pide permiso de administrador.'

# La consola no pide administrador: se ejecuta de verdad desde la carpeta armada.
$consola = Join-Path $armado 'Actualizador.Cli.exe'
$antes = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $salida = @(& $consola 'version' 2>&1 | ForEach-Object { [string]$_ })
    $codigo = $LASTEXITCODE
} catch {
    $salida = @([string]$_)
    $codigo = -1
} finally { $ErrorActionPreference = $antes }
$todo = $salida -join ' '
$salida | ForEach-Object { Write-Host "  $_" }
if ($codigo -ne 0 -or $todo -notmatch [regex]::Escape("Actualizador $version (portable)")) {
    if ($todo -match '0x800711C|directiva de control de aplicaciones|Application Control policy') {
        Write-Host ''
        Write-Host (Get-TextoSmartAppControl) -ForegroundColor Yellow
        throw 'Windows bloqueó la portable recién armada (Smart App Control o Control de aplicaciones).'
    }
    throw "La consola de la portable no arrancó bien (código $codigo). No se entrega."
}
if ($todo -notmatch [regex]::Escape((Join-Path $armado 'Datos'))) {
    throw 'La consola de la portable no guarda sus datos en la carpeta «Datos» de al lado. No se entrega.'
}
# Lo que dejó la prueba no va en la entrega.
$datos = Join-Path $armado 'Datos'
if (Test-Path -LiteralPath $datos) { Remove-Item -LiteralPath $datos -Recurse -Force }

Write-Paso 'Calculando SHA256SUMS.txt'
$lineas = New-Object System.Collections.Generic.List[string]
$base = (Resolve-Path -LiteralPath $armado).Path.TrimEnd('\') + '\'
Get-ChildItem -LiteralPath $armado -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object FullName | ForEach-Object {
    $h = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $lineas.Add($h + '  ' + $_.FullName.Substring($base.Length))
}
[IO.File]::WriteAllLines((Join-Path $armado 'SHA256SUMS.txt'), $lineas, (New-Object Text.UTF8Encoding($false)))
Write-Host "$($lineas.Count) archivos."

Write-Paso 'Comprimiendo'
New-Item -ItemType Directory -Path $Destino -Force | Out-Null
$sieteZip = Find-7Zip
if ($sieteZip) {
    $archivo = Join-Path $Destino "$nombre.7z"
    if (Test-Path -LiteralPath $archivo) { Remove-Item -LiteralPath $archivo -Force }
    # Se comprime la carpeta entera: al descomprimir queda Actualizador-x.y.z-portable\ con todo adentro.
    & $sieteZip 'a' '-t7z' '-mx=9' '-bso0' '-bsp0' $archivo $armado
    if ($LASTEXITCODE -ne 0) { throw "7-Zip terminó con el código $LASTEXITCODE." }
} else {
    Write-Host 'No se encontró 7-Zip: se arma un .zip (con 7-Zip instalado sale un .7z).' -ForegroundColor Yellow
    $archivo = Join-Path $Destino "$nombre.zip"
    if (Test-Path -LiteralPath $archivo) { Remove-Item -LiteralPath $archivo -Force }
    Compress-Archive -Path $armado -DestinationPath $archivo -CompressionLevel Optimal
}
$h = (Get-FileHash -LiteralPath $archivo -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archivo.sha256", "$h  $(Split-Path -Leaf $archivo)`r`n", (New-Object Text.UTF8Encoding($false)))
Write-Paso "Portable: $archivo"
Write-Host "SHA-256: $h"
Write-Host "Carpeta lista para copiar a un pendrive: $armado"
