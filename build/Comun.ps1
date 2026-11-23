# Funciones comunes de los scripts de compilación (se cargan con «. Comun.ps1»). Compatibles con Windows PowerShell 5.1.

function Write-Paso([string]$Texto) {
    Write-Host ''
    Write-Host "=== $Texto" -ForegroundColor Cyan
}

function Get-ActualizadorVersion([string]$Raiz) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $Raiz 'Directory.Build.props') -Raw -Encoding UTF8
    $v = $props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
    if (-not $v) { throw 'No se encontró <Version> en Directory.Build.props.' }
    return [string]$v
}

# Smart App Control (Windows 11) bloquea los .exe y .dll recién compilados que no tienen una firma de código de confianza:
# «Una directiva de control de aplicaciones bloqueó este archivo» (0x800711C7 / 0x800711CD). No admite excepciones por
# programa. Estado en HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy, VerifiedAndReputablePolicyState:
# 0 = desactivado, 1 = activado (bloquea), 2 = evaluación (no bloquea).
function Get-SmartAppControl {
    try {
        $v = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -Name 'VerifiedAndReputablePolicyState' -ErrorAction Stop
        switch ([int]$v.VerifiedAndReputablePolicyState) { 1 { return 'Activado' } 2 { return 'Evaluacion' } default { return 'Desactivado' } }
    } catch { return 'Desactivado' }
}

function Get-TextoSmartAppControl {
    return @(
        'Smart App Control (Seguridad de Windows) está ACTIVADO en este equipo.',
        'Con Smart App Control activado, Windows no deja ejecutar ningún programa recién compilado que no esté firmado con un',
        'certificado de firma de código de confianza: bloquea los archivos de Actualizador (y de cualquier programa propio) con',
        '«Una directiva de control de aplicaciones bloqueó este archivo». No es un error de Actualizador y no admite excepciones.',
        '',
        'Opciones:',
        '  1. Desactivarlo: Seguridad de Windows > Control de aplicaciones y navegador > Configuración de Smart App Control > Desactivado.',
        '     Antivirus y SmartScreen siguen funcionando. Desde abril de 2026 se puede volver a activar sin reinstalar Windows.',
        '  2. Firmar Actualizador con un certificado de firma de código (Azure Trusted Signing, DigiCert, Sectigo...):',
        '     build\Publish.ps1 -CertificadoHuella <huella>',
        '  3. Instalar una entrega ya firmada en este equipo.'
    ) -join [Environment]::NewLine
}

function Assert-SinSmartAppControl([switch]$HayFirma) {
    if ($HayFirma) { return }
    if ((Get-SmartAppControl) -ne 'Activado') { return }
    Write-Host ''
    Write-Host (Get-TextoSmartAppControl) -ForegroundColor Yellow
    Write-Host ''
    throw 'Smart App Control bloquearía los archivos compilados: no se compila (no tiene sentido esperar).'
}

# 2.0.14 (captura del usuario, 08/11/2026): winget decía que el SDK de .NET 8 ya estaba instalado, pero «dotnet --list-sdks» no
# lo mostraba. El «dotnet» que aparece primero en el PATH puede ser el de 32 bits (C:\Program Files (x86)\dotnet), que sólo
# lista los SDK de 32 bits, mientras el de 64 bits está en C:\Program Files\dotnet; o el PATH de la ventana es anterior a la
# instalación. Ahora se busca el SDK donde está instalado (registro del instalador, carpetas conocidas, todos los «dotnet» del
# PATH) y se usa ese para toda la compilación.
function Get-DotNetCandidatos {
    $rutas = @()
    $arquitecturas = @('x64', 'arm64', 'x86')
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { $arquitecturas = @('arm64', 'x64', 'x86') }
    foreach ($arq in $arquitecturas) {
        foreach ($base in @('HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions', 'HKLM:\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions')) {
            try {
                $d = (Get-ItemProperty -LiteralPath "$base\$arq" -Name 'InstallLocation' -ErrorAction Stop).InstallLocation
                if ($d) { $rutas += (Join-Path $d 'dotnet.exe') }
            } catch { }
        }
    }
    if ($env:DOTNET_ROOT) { $rutas += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    if ($env:ProgramW6432) { $rutas += (Join-Path $env:ProgramW6432 'dotnet\dotnet.exe') }
    if ($env:ProgramFiles) { $rutas += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    if ($env:LOCALAPPDATA) { $rutas += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe') }
    foreach ($c in @(Get-Command dotnet.exe -All -CommandType Application -ErrorAction SilentlyContinue)) { $rutas += $c.Path }
    if (${env:ProgramFiles(x86)}) { $rutas += (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe') }
    $vistos = @{}
    $salida = @()
    foreach ($r in $rutas) {
        if (-not $r) { continue }
        $clave = $r.ToLowerInvariant()
        if ($vistos.ContainsKey($clave)) { continue }
        $vistos[$clave] = $true
        if (Test-Path -LiteralPath $r -PathType Leaf) { $salida += $r }
    }
    return $salida
}

# Los SDK 8.x que lista ese dotnet.exe (vacío si no corre).
function Get-DotNetSdk8([string]$Exe) {
    $antes = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { return @(& $Exe --list-sdks 2>$null | ForEach-Object { [string]$_ } | Where-Object { $_ -match '^8\.' }) }
    catch { return @() }
    finally { $ErrorActionPreference = $antes }
}

# Busca un dotnet.exe con el SDK 8; si lo encuentra, lo pone primero en el PATH de esta ventana y devuelve su ruta.
function Use-DotNetSdk8 {
    foreach ($exe in @(Get-DotNetCandidatos)) {
        $sdks = @(Get-DotNetSdk8 $exe)
        if ($sdks.Count -gt 0) {
            $dir = Split-Path -Parent $exe
            $env:PATH = $dir + ';' + (($env:PATH -split ';' | Where-Object { $_ -and ($_.TrimEnd('\') -ne $dir.TrimEnd('\')) }) -join ';')
            $env:DOTNET_ROOT = $dir
            $ultimo = ($sdks[$sdks.Count - 1] -split ' ')[0]
            Write-Host "SDK de .NET $ultimo en $dir" -ForegroundColor DarkGray
            return $exe
        }
    }
    return $null
}

function Write-DiagnosticoDotNet {
    $candidatos = @(Get-DotNetCandidatos)
    if ($candidatos.Count -eq 0) { Write-Host 'No se encontró ningún dotnet.exe en este equipo.' -ForegroundColor Yellow; return }
    Write-Host 'dotnet.exe encontrados y los SDK que listan:' -ForegroundColor Yellow
    foreach ($exe in $candidatos) {
        $antes = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { $todos = @(& $exe --list-sdks 2>$null | ForEach-Object { [string]$_ }) } catch { $todos = @() } finally { $ErrorActionPreference = $antes }
        if ($todos.Count -eq 0) { Write-Host "  $exe  (ningún SDK)" } else { Write-Host "  $exe"; foreach ($s in $todos) { Write-Host "      $s" } }
    }
}

function Assert-DotNetSdk {
    if (Use-DotNetSdk8) { return }
    Write-Host 'Falta el SDK de .NET 8 (hace falta sólo en el equipo donde se compila).' -ForegroundColor Yellow
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        $r = Read-Host '¿Instalarlo ahora con winget (Microsoft.DotNet.SDK.8)? [s/N]'
        if ($r -match '^(s|si|sí|y|yes)$') {
            & winget install --id Microsoft.DotNet.SDK.8 --exact --source winget --accept-package-agreements --accept-source-agreements
            $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User')
            if (Use-DotNetSdk8) { return }
            # winget lo da por instalado («No hay versiones más recientes») pero no aparece: se reinstala encima (repara los archivos).
            Write-Host 'winget lo da por instalado pero el SDK no aparece: se reinstala para repararlo.' -ForegroundColor Yellow
            & winget install --id Microsoft.DotNet.SDK.8 --exact --source winget --force --accept-package-agreements --accept-source-agreements
            $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User')
            if (Use-DotNetSdk8) { return }
        }
    }
    Write-Host ''
    Write-DiagnosticoDotNet
    throw 'Instalá el SDK de .NET 8 x64 (https://dotnet.microsoft.com/download/dotnet/8.0) y volvé a ejecutar.'
}

function Invoke-DotNet([string[]]$Argumentos) {
    Write-Host ('dotnet ' + ($Argumentos -join ' ')) -ForegroundColor DarkGray
    & dotnet @Argumentos
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Argumentos[0]) terminó con el código $LASTEXITCODE." }
}

function Invoke-Pruebas([string]$Raiz, [string]$Configuracion) {
    Write-Paso 'Corriendo las pruebas'
    $proyecto = Join-Path $Raiz 'tests\Actualizador.Tests\Actualizador.Tests.csproj'
    Invoke-DotNet @('build', $proyecto, '-c', $Configuracion, '-nologo')
    $bin = Join-Path $Raiz "tests\Actualizador.Tests\bin\$Configuracion\net8.0-windows"
    # En Windows PowerShell 5.1, con «Stop» la primera línea de error de un programa externo corta todo: se lee completa.
    $antes = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $salida = @(& dotnet (Join-Path $bin 'Actualizador.Tests.dll') (Join-Path $bin 'samples') 2>&1 | ForEach-Object { $t = [string]$_; Write-Host $t; $t })
        $codigo = $LASTEXITCODE
    } finally { $ErrorActionPreference = $antes }
    if ($codigo -ne 0) {
        if (($salida -join ' ') -match '0x800711C|directiva de control de aplicaciones|Application Control policy') {
            Write-Host ''
            Write-Host (Get-TextoSmartAppControl) -ForegroundColor Yellow
            throw 'Windows bloqueó los archivos compilados (Smart App Control o Control de aplicaciones). No es una falla de las pruebas.'
        }
        throw "Fallaron pruebas (código $codigo). No se continúa."
    }
}

# 2.0.16: ¿hay que publicar de nuevo? Devuelve el motivo, o $null si lo publicado en $Salida es de esta versión y posterior a
# todo el código (los .cs, proyectos, manifiestos e iconos de src, la versión, el catálogo de fuentes y lo de deploy que se copia).
function Get-MotivoParaPublicar([string]$Raiz, [string]$Salida, [string]$Version) {
    $exe = Join-Path $Salida 'Actualizador.exe'
    $sumas = Join-Path $Salida 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { return 'todavía no se publicó' }
    if (-not (Test-Path -LiteralPath $sumas -PathType Leaf)) { return 'la publicación anterior quedó incompleta (falta SHA256SUMS.txt)' }
    $publicada = ''
    try { $publicada = [string][Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion } catch { $publicada = '' }
    # «2.0.16» o «2.0.16+<identificador de la compilación>».
    $base = ($publicada -split '[+ ]')[0]
    if ($base -ne $Version) { return "lo publicado es la versión «$publicada» y el código es la $Version" }
    $hecho = (Get-Item -LiteralPath $sumas).LastWriteTimeUtc
    $archivos = @()
    $src = Join-Path $Raiz 'src'
    if (Test-Path -LiteralPath $src) {
        $archivos += @(Get-ChildItem -LiteralPath $src -Recurse -File -ErrorAction SilentlyContinue |
                       Where-Object { ($_.FullName -notmatch '\\(bin|obj)\\') -and (@('.cs', '.csproj', '.manifest', '.ico', '.resx', '.json') -contains $_.Extension.ToLowerInvariant()) })
    }
    foreach ($suelto in @('Directory.Build.props', 'res\fuentes-oficiales.json', 'deploy\politica.ejemplo.json', 'deploy\Uninstall-Actualizador.ps1')) {
        $ruta = Join-Path $Raiz $suelto
        if (Test-Path -LiteralPath $ruta -PathType Leaf) { $archivos += (Get-Item -LiteralPath $ruta) }
    }
    foreach ($a in $archivos) {
        if ($a.LastWriteTimeUtc -gt $hecho) { return "el código cambió después de publicar ($($a.Name))" }
    }
    return $null
}

# 7z.exe de 7-Zip (para comprimir en .7z), o $null si no está instalado. Primero la ruta que 7-Zip deja en el registro.
function Find-7Zip {
    foreach ($clave in @('HKLM:\SOFTWARE\7-Zip', 'HKCU:\SOFTWARE\7-Zip')) {
        foreach ($valor in @('Path64', 'Path')) {
            try {
                $d = (Get-ItemProperty -LiteralPath $clave -Name $valor -ErrorAction Stop).$valor
                if ($d) { $exe = Join-Path $d '7z.exe'; if (Test-Path -LiteralPath $exe) { return $exe } }
            } catch { }
        }
    }
    foreach ($d in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($d) { $exe = Join-Path $d '7-Zip\7z.exe'; if (Test-Path -LiteralPath $exe) { return $exe } }
    }
    $cmd = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}
