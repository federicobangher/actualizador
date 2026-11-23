<#
.SYNOPSIS
    Publica esta versión de Actualizador en GitHub: los equipos la bajan y se instalan solos al abrir Actualizador.

.DESCRIPTION
    2.0.32 (pedido del usuario: «que se actualice solo, como Virtual Pet»). Un solo paso:
      1. Arma la entrega (Empaquetar.ps1): dist\Actualizador-<versión>.7z, para instalar a mano.
      2. Arma también dist\Actualizador-<versión>.zip con su .sha256: es la que bajan los equipos (Windows abre .zip sin 7-Zip).
      3. Con GitHub CLI (gh): si falta lo instala con winget; si no iniciaste sesión, te la pide (una sola vez); si el proyecto
         no existe, lo crea (público).
      Desde la 2.0.36 esto mismo corre solo en GitHub (.github/workflows/publicar.yml) cuando llega a main una versión nueva.
      4. Crea la versión «v<versión>» con las notas de CHANGELOG.md y sube los tres archivos (si ya existía, los reemplaza).
    Cada equipo con Actualizador 2.0.32 o posterior la encuentra al abrir (como mucho una consulta por hora), la baja, verifica
    el SHA-256, la instala en silencio y vuelve a abrir Actualizador.

.EXAMPLE
    Publicar.cmd
    powershell -ExecutionPolicy Bypass -File build\Publicar.ps1 -Repo usuario/proyecto
#>
[CmdletBinding()]
param(
    [string]$Repo = 'federicobangher/actualizador',
    [switch]$ConPruebas
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$raiz = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Comun.ps1')

$version = Get-ActualizadorVersion $raiz
$nombre = "Actualizador-$version"
$dist = Join-Path $raiz 'dist'

# --- GitHub CLI (antes de compilar: si algo falta, se sabe enseguida)
Write-Paso 'GitHub CLI'
function Find-Gh {
    $c = Get-Command gh -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in @((Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'), (Join-Path ${env:ProgramFiles(x86)} 'GitHub CLI\gh.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\GitHub CLI\gh.exe'))) {
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }
    return $null
}
# 2.0.35 (relevamiento): en Windows PowerShell 5.1, con $ErrorActionPreference = 'Stop', redirigir lo que un programa escribe en stderr
# (con «*>» o «2>&1») lo convierte en un error que corta el guion. gh escribe en stderr aun cuando sale bien («gh auth status») o para
# decir que algo no existe («gh repo view»): Publicar.ps1 se cortaba antes de empezar. Estas consultas corren con 'Continue'.
function Test-Gh([string[]]$Argumentos) {
    $antes = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $gh @Argumentos 2>&1 | Out-Null; return ($LASTEXITCODE -eq 0) }
    finally { $ErrorActionPreference = $antes }
}

$gh = Find-Gh
if (-not $gh) {
    Write-Host 'No está GitHub CLI: se instala con winget (GitHub.cli)...'
    & winget install --id GitHub.cli --exact --silent --accept-package-agreements --accept-source-agreements
    $gh = Find-Gh
    if (-not $gh) { throw 'No se pudo instalar GitHub CLI. Instalalo desde https://cli.github.com y volvé a ejecutar Publicar.cmd.' }
}
if (-not (Test-Gh @('auth', 'status', '--hostname', 'github.com'))) {
    Write-Host 'Iniciá sesión en GitHub (una sola vez): se abre el navegador.' -ForegroundColor Yellow
    # 2.0.36 (captura del usuario, 23/11/2026): «Authentication complete» y aun así «No se inició sesión en GitHub». Después de
    # iniciar sesión, gh configura Git y, si Git no está instalado («unable to find git executable in PATH»), termina con error.
    # Publicar no usa Git: lo que vale es si quedó la sesión iniciada, no el código de salida de «gh auth login».
    $antes = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $gh auth login --hostname github.com --git-protocol https --web } finally { $ErrorActionPreference = $antes }
    if (-not (Test-Gh @('auth', 'status', '--hostname', 'github.com'))) { throw 'No se inició sesión en GitHub.' }
}
if (-not (Test-Gh @('repo', 'view', $Repo))) {
    Write-Host "El proyecto $Repo no existe: se crea (público, sólo con las entregas)."
    & $gh repo create $Repo --public --add-readme --description 'Actualizador: entregas para que cada equipo se actualice solo.'
    if ($LASTEXITCODE -ne 0) { throw "No se pudo crear el proyecto $Repo." }
}

# --- Entrega para instalar a mano (.7z) y la que bajan los equipos (.zip)
$emp = @{}
if (-not $ConPruebas) { $emp.SinPruebas = $true }
& (Join-Path $PSScriptRoot 'Empaquetar.ps1') @emp
$armado = Join-Path $raiz "obj\entrega\$nombre"
if (-not (Test-Path -LiteralPath (Join-Path $armado 'Actualizador\Actualizador.exe'))) { throw "No se encontró la entrega armada en $armado." }

Write-Paso 'Entrega para la actualización automática (.zip)'
$zip = Join-Path $dist "$nombre.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $armado '*') -DestinationPath $zip -CompressionLevel Optimal
$h = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$h  $nombre.zip`r`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "$zip"
Write-Host "SHA-256: $h"

$archivos = @($zip, "$zip.sha256")
$sieteZ = Join-Path $dist "$nombre.7z"
if (Test-Path -LiteralPath $sieteZ) { $archivos += $sieteZ }

# --- 2.0.37 (pedido del usuario: «en el portable se puede hacer que se actualice solo sin que se abra la página»): la portable, para
# que cada portable se actualice sola (.zip con su .sha256) y para usarla a mano (.7z). Las pruebas ya corrieron arriba.
& (Join-Path $PSScriptRoot 'EmpaquetarPortable.ps1') -SinPruebas
$nombrePortable = "$nombre-portable"
$armadoPortable = Join-Path $raiz "obj\entrega\$nombrePortable"
if (-not (Test-Path -LiteralPath (Join-Path $armadoPortable 'Actualizador.portable'))) { throw "No se encontró la portable armada en $armadoPortable." }
Write-Paso 'Portable para la actualización automática (.zip)'
$zipPortable = Join-Path $dist "$nombrePortable.zip"
if (Test-Path -LiteralPath $zipPortable) { Remove-Item -LiteralPath $zipPortable -Force }
Compress-Archive -Path (Join-Path $armadoPortable '*') -DestinationPath $zipPortable -CompressionLevel Optimal
$hp = (Get-FileHash -LiteralPath $zipPortable -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zipPortable.sha256", "$hp  $nombrePortable.zip`r`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "$zipPortable"
Write-Host "SHA-256: $hp"
$archivos += @($zipPortable, "$zipPortable.sha256")
$sieteZPortable = Join-Path $dist "$nombrePortable.7z"
if (Test-Path -LiteralPath $sieteZPortable) { $archivos += $sieteZPortable }

# --- Notas: la sección de esta versión en CHANGELOG.md
$notas = Join-Path $raiz "obj\notas-$version.md"
$lineas = Get-Content -LiteralPath (Join-Path $raiz 'CHANGELOG.md') -Encoding UTF8
$dentro = $false
$texto = New-Object System.Collections.Generic.List[string]
foreach ($l in $lineas) {
    if ($l -match '^## ') { if ($dentro) { break }; if ($l.Trim() -eq "## $version") { $dentro = $true; continue } }
    if ($dentro) { $texto.Add($l) }
}
if ($texto.Count -eq 0) { $texto.Add("Actualizador $version") }
[IO.File]::WriteAllText($notas, ($texto -join "`r`n"), (New-Object Text.UTF8Encoding($false)))

# --- Versión en GitHub (gh muestra su avance por stderr: con 'Continue', para que no corte el guion)
Write-Paso "Publicando v$version en $Repo"
$ErrorActionPreference = 'Continue'
if (Test-Gh @('release', 'view', "v$version", '--repo', $Repo)) {
    Write-Host "La versión v$version ya estaba: se reemplazan sus archivos."
    & $gh release upload "v$version" @archivos --repo $Repo --clobber
} else {
    & $gh release create "v$version" @archivos --repo $Repo --title "Actualizador $version" --notes-file $notas --latest
}
if ($LASTEXITCODE -ne 0) { throw 'GitHub no aceptó la publicación (mirá el mensaje de arriba).' }
$ErrorActionPreference = 'Stop'

Write-Paso 'Listo'
Write-Host "Publicada: https://github.com/$Repo/releases/tag/v$version" -ForegroundColor Green
Write-Host 'Cada equipo con Actualizador 2.0.32 o posterior la instala solo la próxima vez que lo abran.'
