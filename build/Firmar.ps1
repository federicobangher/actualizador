<#
.SYNOPSIS
    Firma una versión publicada de Actualizador con TU clave (la que nunca sube a GitHub). Doble clic en Firmar.cmd.

.DESCRIPTION
    2.0.46 (pedido del usuario, 26/11/2026). GitHub compila y publica cada versión; cada equipo se actualiza solo SÓLO si esa versión
    está firmada con una clave que el programa trae compilada adentro. La clave privada queda en esta PC (almacén de certificados de
    Windows, protegida con contraseña): aunque alguien entre a la cuenta de GitHub, no puede firmar una versión falsa.

    La primera vez crea la clave (pide una contraseña), ofrece guardar un respaldo (.pfx con contraseña: en un pendrive) y sube la
    clave PÚBLICA al proyecto (res\claves-de-firma.txt) para que la versión siguiente la traiga adentro.
    Cada versión: muestra qué se va a firmar, baja las entregas publicadas, comprueba su SHA-256, las firma (pide la contraseña de la
    clave) y sube «<entrega>.firma» al lado de cada una. Firmá sólo versiones que sabés que pediste.

.EXAMPLE
    Firmar.cmd                      la última versión publicada
    Firmar.cmd -Version 2.0.46
    Firmar.cmd -Respaldo E:\        guarda (otra vez) el respaldo de la clave en E:\
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Repo = 'federicobangher/actualizador',
    [string]$Respaldo,
    # Sólo para las pruebas automáticas: clave sin contraseña, con otro nombre, y sin subir nada.
    [switch]$Prueba
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$raiz = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Comun.ps1')
. (Join-Path $PSScriptRoot 'Firma.ps1')

if ($Repo -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}$') { throw "Proyecto no válido: $Repo" }
$nombreClave = if ($Prueba) { $NombreClavePrueba } else { $NombreClaveFirma }

# --- GitHub CLI (como Publicar.cmd)
$gh = Find-GhFirma
if (-not $gh) {
    Write-Host 'No está GitHub CLI: se instala con winget (GitHub.cli)...'
    $w = Find-Winget
    if ($w) { & $w install --id GitHub.cli --exact --silent --accept-package-agreements --accept-source-agreements }
    $gh = Find-GhFirma
    if (-not $gh) { throw 'No se pudo instalar GitHub CLI. Instalalo desde https://cli.github.com y volvé a ejecutar Firmar.cmd.' }
}
function Invoke-Gh([string[]]$Argumentos) {
    $antes = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $salida = & $gh @Argumentos 2>&1 | ForEach-Object { [string]$_ }; return @{ Ok = ($LASTEXITCODE -eq 0); Texto = ($salida -join "`n") } }
    finally { $ErrorActionPreference = $antes }
}
if (-not (Invoke-Gh @('auth', 'status', '--hostname', 'github.com')).Ok) {
    Write-Host 'Iniciá sesión en GitHub (una sola vez): se abre el navegador.' -ForegroundColor Yellow
    $antes = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $gh auth login --hostname github.com --git-protocol https --web } finally { $ErrorActionPreference = $antes }
    if (-not (Invoke-Gh @('auth', 'status', '--hostname', 'github.com')).Ok) { throw 'No se inició sesión en GitHub.' }
}

# --- La clave
$cert = Get-ClaveDeFirma $nombreClave
if (-not $cert) {
    Write-Paso 'Primera vez: no hay clave de firma en esta PC'
    Write-Host 'Se crea ahora. Windows te va a pedir una contraseña para protegerla: elegí una que no olvides (se pide cada vez que firmás).'
    Write-Host 'Si perdés la clave (y su respaldo), los equipos dejan de actualizarse solos hasta instalar a mano una versión con una clave nueva.' -ForegroundColor Yellow
    if (-not $Prueba) {
        $r = Read-Host '¿Crear la clave de firma? [S/n]'
        if ($r -match '^(n|no)$') { throw 'No se creó la clave: no se firmó nada.' }
    }
    $cert = New-ClaveDeFirma -Nombre $nombreClave -SinContrasena:$Prueba
    $linea = Get-LineaClavePublica $cert
    Write-Host "Clave creada. Identificador: $($linea.Split(' ')[0])" -ForegroundColor Green
    if (-not $Prueba) {
        Write-Paso 'Respaldo de la clave'
        Write-Host 'Guardá un respaldo (archivo .pfx con contraseña) en un pendrive y guardalo aparte. Sin respaldo, si se rompe esta PC no se puede firmar más.'
        if (-not $Respaldo) { $Respaldo = Read-Host 'Carpeta para el respaldo (Enter = Escritorio; después copialo a un pendrive)' }
        if (-not $Respaldo) { $Respaldo = [Environment]::GetFolderPath('Desktop') }
        Save-RespaldoClave -Cert $cert -Carpeta $Respaldo
        $Respaldo = $null
        Write-Paso "Subiendo la clave PÚBLICA a $Repo (res/claves-de-firma.txt)"
        Publish-ClavePublica -Linea $linea -Repo $Repo
        Write-Host 'La próxima versión de Actualizador la trae adentro; desde esa, los equipos sólo se actualizan solos con versiones firmadas.' -ForegroundColor Green
    }
}
if ($Respaldo) { Save-RespaldoClave -Cert $cert -Carpeta $Respaldo }

# --- Qué versión
if (-not $Version) {
    $v = Invoke-Gh @('release', 'view', '--repo', $Repo, '--json', 'tagName', '--jq', '.tagName')
    if (-not $v.Ok -or -not $v.Texto) { throw "No se pudo leer la última versión publicada en $Repo." }
    $Version = $v.Texto.Trim().TrimStart('v')
}
$Version = $Version.Trim().TrimStart('v')
if ($Version -notmatch '^\d{1,4}(\.\d{1,6}){2,3}$') { throw "Versión no válida: $Version" }
$tag = "v$Version"
$info = Invoke-Gh @('release', 'view', $tag, '--repo', $Repo, '--json', 'name,publishedAt,author,assets', '--jq',
                    '.name + " | publicada " + .publishedAt + " por " + .author.login + " | " + ([.assets[].name] | join(", "))')
if (-not $info.Ok) { throw "No existe la versión $tag en $Repo." }
Write-Paso "Versión a firmar: $tag"
Write-Host $info.Texto
Write-Host 'Firmala sólo si es una versión que pediste (si no la reconocés, no la firmes y avisá).' -ForegroundColor Yellow
if (-not $Prueba) {
    $r = Read-Host "¿Firmar Actualizador $Version? [s/N]"
    if ($r -notmatch '^(s|si|sí|y|yes)$') { throw 'No se firmó nada.' }
}

# --- Bajar, comprobar, firmar y subir
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('Actualizador-firma-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    $d = Invoke-Gh @('release', 'download', $tag, '--repo', $Repo, '--dir', $tmp, '--pattern', '*.zip', '--pattern', '*.7z', '--pattern', '*.sha256')
    if (-not $d.Ok) { throw "No se pudieron bajar las entregas de $tag`n$($d.Texto)" }
    $firmas = New-Object System.Collections.ArrayList
    $entregas = @(Get-ChildItem -LiteralPath $tmp -File | Where-Object { $_.Extension -eq '.zip' -or $_.Extension -eq '.7z' } | Sort-Object Name)
    if ($entregas.Count -eq 0) { throw "La versión $tag no tiene entregas (.zip o .7z)." }
    $claves = @(Get-ClavePublicaComoLista $cert)
    foreach ($e in $entregas) {
        if ($e.Name -notmatch ('^Actualizador-' + [regex]::Escape($Version) + '(-portable)?\.(zip|7z)$')) { Write-Host "  (se saltea $($e.Name): no es una entrega de esta versión)"; continue }
        $sha = (Get-FileHash -LiteralPath $e.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $shaArchivo = Join-Path $tmp ($e.Name + '.sha256')
        if (Test-Path -LiteralPath $shaArchivo) {
            $publicado = ([IO.File]::ReadAllText($shaArchivo) -split '\s+')[0].ToLowerInvariant()
            if ($publicado -ne $sha) { throw "$($e.Name) no coincide con su .sha256 publicado: no se firma nada." }
        }
        $texto = New-TextoFirma -Cert $cert -Archivo $e.Name -Version $Version -Sha256 $sha
        $problema = Test-FirmaEntrega -Texto $texto -Archivo $e.Name -Version $Version -Sha256 $sha -Claves $claves
        if ($problema) { throw "La firma recién hecha de $($e.Name) no se pudo verificar: $problema" }
        $destino = Join-Path $tmp ($e.Name + '.firma')
        [IO.File]::WriteAllText($destino, $texto, (New-Object Text.UTF8Encoding($false)))
        [void]$firmas.Add($destino)
        Write-Host "  $($e.Name)  SHA-256 $sha  firmada" -ForegroundColor Green
    }
    if ($firmas.Count -eq 0) { throw 'No se firmó ninguna entrega.' }
    if ($Prueba) { Write-Host "Prueba: $($firmas.Count) firmas hechas y verificadas (no se suben)."; return }
    Write-Paso "Subiendo las firmas a $tag"
    $u = Invoke-Gh (@('release', 'upload', $tag) + @($firmas.ToArray()) + @('--repo', $Repo, '--clobber'))
    if (-not $u.Ok) { throw "GitHub no aceptó las firmas`n$($u.Texto)" }
    Write-Paso "Listo: Actualizador $Version firmado. Los equipos se actualizan solos la próxima vez que abran Actualizador."
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
