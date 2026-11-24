<#
.SYNOPSIS
    Instala Actualizador en este equipo desde cero (como administrador).

.DESCRIPTION
    Siempre deja una instalación LIMPIA, haya lo que haya antes:
    1. Comprueba que Windows deje abrir la entrega (Smart App Control) y verifica cada archivo (SHA256SUMS.txt).
    2. Quita la versión anterior entera, sea cual sea (también las 1.x de instalar.bat): cierra el programa, aparta su
       carpeta, quita accesos directos de todos los usuarios, revisiones programadas y su entrada en «Aplicaciones
       instaladas». Los datos de usuario de las versiones 1.x (auditorías, opciones viejas, registros) se borran; los de
       2.x (historial e ignorados) se conservan, salvo con -Limpio.
    3. Copia a «C:\Program Files\Actualizador» (sólo escriben los administradores).
    4. Comprueba que el programa instalado ABRE. Si Windows lo bloquea, vuelve todo como estaba y dice por qué.
    5. Prepara %ProgramData%\Actualizador (política y registro de cambios) y el Visor de eventos.
    6. Lo registra en «Aplicaciones instaladas» y crea los accesos directos.
    Todo queda anotado en %ProgramData%\Actualizador\instalacion.log.

.PARAMETER Origen
    Carpeta con los archivos publicados. Si no se indica: «Actualizador» junto a este script o «publish\Actualizador».

.PARAMETER Politica
    Un politica.json para copiar a %ProgramData%\Actualizador (ver docs\POLITICAS.md).

.PARAMETER Limpio
    Borra también el historial, los ignorados y las opciones de 2.x de todos los usuarios.

.PARAMETER ExigirFirma
    No instala si algún ejecutable no tiene una firma Authenticode válida.

.PARAMETER Silencioso
    Sin preguntas (Intune, SCCM, GPO o RMM). Cierra Actualizador si está abierto.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Install-Actualizador.ps1
    powershell -ExecutionPolicy Bypass -File Install-Actualizador.ps1 -Limpio -Silencioso -Politica \\servidor\ti\politica.json
#>
[CmdletBinding()]
param(
    [string]$Origen,
    [string]$Destino = (Join-Path $env:ProgramFiles 'Actualizador'),
    [string]$Politica,
    [switch]$Limpio,
    [switch]$ExigirFirma,
    [switch]$SinEscritorio,
    [switch]$Silencioso
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$Nombre = 'Actualizador'
$Editor = 'Fede'
$ClaveDesinstalar = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Actualizador'
$OtrasClaves = @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Actualizador',
                 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Actualizador')
$Datos = Join-Path $env:ProgramData 'Actualizador'
$Ejecutables = @('Actualizador.exe', 'Actualizador.Cli.exe', 'Actualizador.Aviso.exe')
$Procesos = @('Actualizador', 'Actualizador.Cli', 'Actualizador.Aviso')

# 2.0.11: hasta la 2.0.10 el programa se llamaba SuperProgram. Esa instalación se quita y sus datos pasan a Actualizador.
$Viejo = 'SuperProgram'
$ViejaClave = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SuperProgram'
$ViejasClaves = @($ViejaClave, 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\SuperProgram',
                  'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SuperProgram')
$ViejosDatos = Join-Path $env:ProgramData 'SuperProgram'
$ViejosEjecutables = @('SuperProgram.exe', 'SuperProgram.Cli.exe', 'SuperProgram.Aviso.exe')
$ViejosProcesos = @('SuperProgram', 'SuperProgram.Cli', 'SuperProgram.Aviso')

function Test-Administrador {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Paso([string]$t) { Write-Host ''; Write-Host "=== $t" -ForegroundColor Cyan }

function Test-Enlace([string]$Ruta) {
    if (-not (Test-Path -LiteralPath $Ruta)) { return $false }
    return [bool]((Get-Item -LiteralPath $Ruta -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
}

function Set-AclAdministradores([string]$Carpeta, [bool]$UsuariosLeen) {
    # Dueño Administradores; sin herencia; SYSTEM y Administradores control total; Usuarios sólo lectura (si corresponde).
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $admins = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $users = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
    $herencia = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $prop = [Security.AccessControl.PropagationFlags]::None
    $acl.SetOwner($admins)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($system, 'FullControl', $herencia, $prop, 'Allow')))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($admins, 'FullControl', $herencia, $prop, 'Allow')))
    if ($UsuariosLeen) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', $herencia, $prop, 'Allow')))
    }
    if (-not (Test-Path -LiteralPath $Carpeta)) { New-Item -ItemType Directory -Path $Carpeta -Force | Out-Null }
    if (Test-Enlace $Carpeta) { throw "$Carpeta es un enlace (junction/symlink): no se usa por seguridad." }
    Set-Acl -LiteralPath $Carpeta -AclObject $acl
}

function Get-SmartAppControl {
    try {
        $v = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -Name 'VerifiedAndReputablePolicyState' -ErrorAction Stop
        switch ([int]$v.VerifiedAndReputablePolicyState) { 1 { return 'Activado' } 2 { return 'Evaluación' } default { return 'Desactivado' } }
    } catch { return 'Desactivado' }
}

# Carpetas de perfil de todos los usuarios del equipo (se leen del registro, no se supone C:\Users).
function Get-Perfiles {
    $lista = @()
    try {
        foreach ($k in Get-ChildItem -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' -ErrorAction Stop) {
            if ($k.PSChildName -notmatch '^S-1-5-21-') { continue }
            $p = (Get-ItemProperty -LiteralPath $k.PSPath -ErrorAction SilentlyContinue).ProfileImagePath
            if ($p) { $p = [Environment]::ExpandEnvironmentVariables($p) }
            if ($p -and (Test-Path -LiteralPath $p)) { $lista += $p }
        }
    } catch { }
    return $lista
}

# Bloqueos recientes de Windows (Smart App Control / Control de aplicaciones), para decir exactamente qué pasó.
function Get-BloqueosRecientes([datetime]$Desde) {
    try {
        $ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-CodeIntegrity/Operational'; StartTime = $Desde } -ErrorAction Stop |
                Where-Object { @(3033, 3034, 3077, 3089, 3114, 3118) -contains $_.Id } | Select-Object -First 3)
        return ($ev | ForEach-Object { "  [$($_.Id)] " + (($_.Message -split "`r?`n")[0]) }) -join [Environment]::NewLine
    } catch { return '' }
}

if (-not (Test-Administrador)) {
    Write-Host 'Hace falta ejecutarlo como administrador (doble clic en Instalar.cmd y aceptar el aviso de Windows).' -ForegroundColor Red
    exit 5
}

# Origen de los archivos
if (-not $Origen) {
    foreach ($c in @((Join-Path $PSScriptRoot 'Actualizador'), (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\Actualizador'), $PSScriptRoot)) {
        if (Test-Path -LiteralPath (Join-Path $c 'Actualizador.exe')) { $Origen = $c; break }
    }
}
if (-not $Origen -or -not (Test-Path -LiteralPath (Join-Path $Origen 'Actualizador.exe'))) {
    Write-Host 'No se encontraron los archivos de Actualizador. Compilalos con build\Publish.ps1 o indicá -Origen.' -ForegroundColor Red
    exit 2
}
$Origen = (Resolve-Path -LiteralPath $Origen).Path
if ($Origen.TrimEnd('\') -ieq $Destino.TrimEnd('\')) { Write-Host 'El origen no puede ser la carpeta de instalación.' -ForegroundColor Red; exit 2 }

New-Item -ItemType Directory -Path $Datos -Force | Out-Null
Set-AclAdministradores $Datos $true
$log = Join-Path $Datos 'instalacion.log'
try { Start-Transcript -LiteralPath $log -Append | Out-Null } catch { }
$codigo = 0
$respaldo = $null
$claveVieja = $null
$habiaAccesos = $false
$instalado = $false
$shell = New-Object -ComObject WScript.Shell
try {
    $version = (Get-Item -LiteralPath (Join-Path $Origen 'Actualizador.exe')).VersionInfo.ProductVersion
    if ($version -match '^([0-9]+\.[0-9]+\.[0-9]+)') { $version = $Matches[1] }
    Write-Host "Actualizador $version - desde $Origen - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - $env:USERDOMAIN\$env:USERNAME"

    # ------------------------------------------------------------------------------------------------------------
    Write-Paso '1 de 6: comprobando la entrega'
    $sinFirma = 0
    foreach ($e in $Ejecutables) {
        $f = Join-Path $Origen $e
        if (-not (Test-Path -LiteralPath $f)) { throw "Falta $e en la entrega." }
        $firma = Get-AuthenticodeSignature -LiteralPath $f
        if ($firma.Status -eq 'Valid') { Write-Host "$e firmado por $($firma.SignerCertificate.Subject)" }
        elseif ($ExigirFirma) { throw "$e no tiene una firma válida ($($firma.Status))." }
        else { $sinFirma++ }
    }
    $sac = Get-SmartAppControl
    Write-Host "Smart App Control: $sac"
    if ($sinFirma -gt 0 -and $sac -eq 'Activado') {
        throw ('Smart App Control está activado y esta entrega no está firmada: Windows no dejaría abrir Actualizador, así que no se tocó nada. ' +
               'Desactivalo en Seguridad de Windows > Control de aplicaciones y navegador > Configuración de Smart App Control, ' +
               'o instalá una entrega firmada (build\Publish.ps1 -CertificadoHuella).')
    }
    $sumas = Join-Path $Origen 'SHA256SUMS.txt'
    if (Test-Path -LiteralPath $sumas) {
        $n = 0
        foreach ($linea in [IO.File]::ReadAllLines($sumas)) {
            if ($linea -notmatch '^([0-9a-f]{64})  (.+)$') { continue }
            $esperado = $Matches[1]; $relativo = $Matches[2]
            if ($relativo -match '(^|\\)\.\.(\\|$)' -or [IO.Path]::IsPathRooted($relativo)) { throw "Ruta inválida en SHA256SUMS.txt: $relativo" }
            $archivo = Join-Path $Origen $relativo
            if (-not (Test-Path -LiteralPath $archivo)) { throw "Falta $relativo (la entrega está incompleta)." }
            $real = (Get-FileHash -LiteralPath $archivo -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($real -ne $esperado) { throw "El archivo $relativo no coincide con su SHA-256: la entrega está dañada o fue modificada." }
            $n++
        }
        Write-Host "$n archivos verificados con SHA-256."
    } elseif ($ExigirFirma) {
        throw 'Falta SHA256SUMS.txt en la entrega.'
    } else {
        Write-Host 'La entrega no trae SHA256SUMS.txt (compilada en este equipo): no se verifican hashes.' -ForegroundColor Yellow
    }

    # ------------------------------------------------------------------------------------------------------------
    Write-Paso '2 de 6: quitando la versión anterior'
    $abiertos = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { ($Procesos -contains $_.Name) -or ($ViejosProcesos -contains $_.Name) })
    if ($abiertos.Count -gt 0) {
        if (-not $Silencioso) {
            $r = Read-Host 'Actualizador está abierto. ¿Cerrarlo para instalar? [S/n]'
            if ($r -match '^(n|no)$') { throw 'Instalación cancelada: Actualizador está abierto.' }
        }
        $abiertos | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }

    $anterior = $null
    if (Test-Path -LiteralPath $ClaveDesinstalar) {
        $p = Get-ItemProperty -LiteralPath $ClaveDesinstalar -ErrorAction SilentlyContinue
        if ($p -and ($p.PSObject.Properties.Name -contains 'DisplayVersion')) { $anterior = [string]$p.DisplayVersion }
        $claveVieja = Join-Path $env:TEMP ('Actualizador-clave-' + [Guid]::NewGuid().ToString('N') + '.reg')
        & reg.exe export 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Actualizador' $claveVieja /y | Out-Null
    }
    $eraUno = [bool](($anterior -and $anterior -match '^1\.') -or (Test-Path -LiteralPath (Join-Path $Destino 'desinstalar.bat')))
    if ($anterior) { Write-Host "Versión instalada: $anterior" }
    elseif (Test-Path -LiteralPath $Destino) { Write-Host "Había una carpeta $Destino sin registrar." }
    else { Write-Host 'No había una versión instalada.' }

    # Con el nombre viejo (SuperProgram): dónde está y qué versión. Se quita recién cuando Actualizador abre bien (paso 4).
    $viejaVersion = $null
    $viejaCarpeta = Join-Path $env:ProgramFiles $Viejo
    if (Test-Path -LiteralPath $ViejaClave) {
        $p = Get-ItemProperty -LiteralPath $ViejaClave -ErrorAction SilentlyContinue
        if ($p -and ($p.PSObject.Properties.Name -contains 'DisplayVersion')) { $viejaVersion = [string]$p.DisplayVersion }
        if ($p -and ($p.PSObject.Properties.Name -contains 'InstallLocation') -and $p.InstallLocation) { $viejaCarpeta = ([string]$p.InstallLocation).TrimEnd('\') }
    }
    if ($viejaVersion) { Write-Host "También está instalado con el nombre anterior: SuperProgram $viejaVersion (se reemplaza por Actualizador)." }
    elseif (Test-Path -LiteralPath (Join-Path $viejaCarpeta 'SuperProgram.exe')) { Write-Host "Hay una carpeta $viejaCarpeta con el nombre anterior (SuperProgram): se reemplaza por Actualizador." }

    if (Test-Enlace $Destino) { throw "$Destino es un enlace (junction/symlink): no se instala ahí por seguridad." }
    if (Test-Path -LiteralPath $Destino) {
        # Se aparta entera (no se copia encima): si algo falla, vuelve tal cual estaba.
        $respaldo = $Destino.TrimEnd('\') + '.anterior-' + (Get-Date -Format 'yyyyMMddHHmmss')
        try { Rename-Item -LiteralPath $Destino -NewName (Split-Path -Leaf $respaldo) }
        catch { throw "No se pudo apartar $Destino (¿algún archivo abierto?): $($_.Exception.Message)" }
        Write-Host 'Versión anterior apartada (se borra al terminar bien).'
    }

    # Accesos directos a Actualizador: los comunes y los de cada usuario (se vuelven a crear al final).
    $perfiles = @(Get-Perfiles)
    $comunes = @((Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'), (Join-Path $env:PUBLIC 'Desktop'))
    $lugaresAccesos = @($comunes)
    foreach ($perfil in $perfiles) {
        $lugaresAccesos += (Join-Path $perfil 'Desktop')
        $lugaresAccesos += (Join-Path $perfil 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs')
    }
    foreach ($l in $lugaresAccesos) {
        if (-not (Test-Path -LiteralPath $l)) { continue }
        foreach ($lnk in @(Get-ChildItem -LiteralPath $l -Filter '*.lnk' -File -ErrorAction SilentlyContinue)) {
            $objetivo = ''
            try { $objetivo = [string]$shell.CreateShortcut($lnk.FullName).TargetPath } catch { continue }
            if ($objetivo -and ($Ejecutables -contains [IO.Path]::GetFileName($objetivo))) {
                if ($comunes -contains $lnk.DirectoryName) { $habiaAccesos = $true }
                Remove-Item -LiteralPath $lnk.FullName -Force
                Write-Host "Acceso directo quitado: $($lnk.FullName)"
            }
        }
    }

    foreach ($k in @($ClaveDesinstalar) + $OtrasClaves) { if (Test-Path -LiteralPath $k) { Remove-Item -LiteralPath $k -Recurse -Force } }
    # Una desinstalación anterior que dejó archivos bloqueados programó borrar la carpeta al reiniciar: borraría ESTA instalación.
    $runOnce = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce'
    try {
        if ((Test-Path -LiteralPath $runOnce) -and ((Get-Item -LiteralPath $runOnce).GetValueNames() -contains 'Actualizador - terminar de desinstalar')) {
            Remove-ItemProperty -LiteralPath $runOnce -Name 'Actualizador - terminar de desinstalar' -Force
            Write-Host 'Se canceló el borrado pendiente de una desinstalación anterior.'
        }
    } catch { Write-Host "No se pudo revisar RunOnce: $($_.Exception.Message)" -ForegroundColor Yellow }
    $copiaDesinstalar = Join-Path $Datos 'Uninstall-Actualizador.ps1'
    if (Test-Path -LiteralPath $copiaDesinstalar) { Remove-Item -LiteralPath $copiaDesinstalar -Force -ErrorAction SilentlyContinue }

    # ------------------------------------------------------------------------------------------------------------
    Write-Paso ('3 de 6: copiando a ' + $Destino)
    New-Item -ItemType Directory -Path $Destino -Force | Out-Null
    $instalado = $true
    & robocopy.exe $Origen $Destino /E /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy terminó con el código $LASTEXITCODE." }
    Get-ChildItem -LiteralPath $Destino -Recurse -File | Unblock-File
    # 2.0.10: la marca de la versión portable no va en una instalación (guardaría los datos en Program Files).
    $marca = Join-Path $Destino 'Actualizador.portable'
    if (Test-Path -LiteralPath $marca) { Remove-Item -LiteralPath $marca -Force }
    # Program Files: hereda los permisos del sistema (sólo administradores escriben).
    & icacls.exe $Destino /reset /T /C /Q | Out-Null
    foreach ($e in $Ejecutables) { if (-not (Test-Path -LiteralPath (Join-Path $Destino $e))) { throw "No quedó $e en $Destino." } }
    # El desinstalador tiene que quedar en la carpeta del programa (lo usa «Aplicaciones instaladas»).
    $quitar = Join-Path $Destino 'Uninstall-Actualizador.ps1'
    if (-not (Test-Path -LiteralPath $quitar)) {
        $junto = Join-Path $PSScriptRoot 'Uninstall-Actualizador.ps1'
        if (-not (Test-Path -LiteralPath $junto)) { throw 'Falta Uninstall-Actualizador.ps1 en la entrega: no se podría desinstalar.' }
        Copy-Item -LiteralPath $junto -Destination $quitar -Force
    }

    # ------------------------------------------------------------------------------------------------------------
    Write-Paso '4 de 6: comprobando que Actualizador abre'
    $inicio = (Get-Date).AddSeconds(-5)
    $antes = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $rc = -1
    $salida = ''
    try {
        $salida = (& (Join-Path $Destino 'Actualizador.Cli.exe') version 2>&1 | ForEach-Object { [string]$_ }) -join ' '
        $rc = $LASTEXITCODE
    } catch {
        $salida = $_.Exception.Message
    } finally { $ErrorActionPreference = $antes }
    if ($rc -ne 0 -or $salida -notmatch [regex]::Escape($version)) {
        $bloqueos = Get-BloqueosRecientes $inicio
        $msg = "Actualizador se copió pero Windows no lo deja abrir (código $rc): $salida"
        if ($bloqueos) { $msg += [Environment]::NewLine + 'Bloqueos registrados por Windows:' + [Environment]::NewLine + $bloqueos }
        if ($bloqueos -or $salida -match '0x800711C|directiva de control|Application Control|Smart App Control') {
            $msg += [Environment]::NewLine + 'Es Smart App Control o el Control de aplicaciones de Windows: hace falta desactivarlo o una entrega firmada.'
        }
        throw $msg
    }
    Write-Host "Abre bien: $salida"

    # Recién ahora, con la versión nueva funcionando, se borran los restos que no se pueden deshacer.
    # Revisiones programadas de cualquier usuario: si eran de una 1.x, apuntaban a otro lado o se pidió -Limpio, se quitan.
    try {
        $servicio = New-Object -ComObject Schedule.Service
        $servicio.Connect()
        $raizTareas = $servicio.GetFolder('\')
        foreach ($t in @($raizTareas.GetTasks(1))) {
            if ($t.Name -notlike 'Actualizador - *') { continue }
            $apunta = ''
            try { foreach ($a in $t.Definition.Actions) { $apunta += [string]$a.Path } } catch { }
            $ajena = $apunta -and ($apunta.Trim('"') -ine (Join-Path $Destino 'Actualizador.Aviso.exe'))
            if ($eraUno -or $Limpio -or $ajena) { $raizTareas.DeleteTask($t.Name, 0); Write-Host "Revisión programada quitada: $($t.Name)" }
        }
    } catch { Write-Host "No se pudieron revisar las tareas programadas: $($_.Exception.Message)" -ForegroundColor Yellow }

    # 2.0.11: de SuperProgram a Actualizador. Nada de esto frena la instalación: lo que no se pudo queda anotado.
    Write-Host ''
    Write-Host 'Pasando de SuperProgram (el nombre anterior) a Actualizador:'
    $viejoEraUno = [bool]($viejaVersion -and $viejaVersion -match '^1\.')
    # Revisiones programadas: las de 2.x se vuelven a crear con el nombre y la carpeta nuevos, con el mismo horario.
    try {
        $servicio = New-Object -ComObject Schedule.Service
        $servicio.Connect()
        $raizTareas = $servicio.GetFolder('\')
        $avisoNuevo = Join-Path $Destino 'Actualizador.Aviso.exe'
        foreach ($t in @($raizTareas.GetTasks(1))) {
            if ($t.Name -notlike ($Viejo + ' - *')) { continue }
            $xml = [string]$t.Xml
            $deDosX = $xml -match '<Command>[^<]*SuperProgram\.Aviso\.exe"?</Command>'
            if ($deDosX -and -not $viejoEraUno -and -not $Limpio) {
                $nuevoNombre = 'Actualizador - ' + $t.Name.Substring($Viejo.Length + 3)
                $nuevoXml = [regex]::Replace($xml, '<Command>[^<]*</Command>', ('<Command>' + [Security.SecurityElement]::Escape($avisoNuevo) + '</Command>'))
                $nuevoXml = $nuevoXml.Replace('SuperProgram', 'Actualizador')
                try {
                    [void]$raizTareas.RegisterTask($nuevoNombre, $nuevoXml, 6, $null, $null, 3)
                    Write-Host "  Revisión programada pasada a Actualizador: $nuevoNombre"
                } catch {
                    Write-Host "  No se pudo pasar la revisión programada «$($t.Name)» ($($_.Exception.Message)): activala de nuevo en Actualizador › Opciones › Actualizaciones." -ForegroundColor Yellow
                }
            }
            $raizTareas.DeleteTask($t.Name, 0)
            Write-Host "  Revisión programada vieja quitada: $($t.Name)"
        }
    } catch { Write-Host "  No se pudieron revisar las tareas programadas viejas: $($_.Exception.Message)" -ForegroundColor Yellow }

    # Accesos directos a SuperProgram (comunes y de cada usuario).
    foreach ($l in $lugaresAccesos) {
        if (-not (Test-Path -LiteralPath $l)) { continue }
        foreach ($lnk in @(Get-ChildItem -LiteralPath $l -Filter '*.lnk' -File -ErrorAction SilentlyContinue)) {
            $objetivo = ''
            try { $objetivo = [string]$shell.CreateShortcut($lnk.FullName).TargetPath } catch { continue }
            if ($objetivo -and ($ViejosEjecutables -contains [IO.Path]::GetFileName($objetivo))) {
                try { Remove-Item -LiteralPath $lnk.FullName -Force; Write-Host "  Acceso directo viejo quitado: $($lnk.FullName)" } catch { }
            }
        }
    }

    # Entrada en «Aplicaciones instaladas», borrado pendiente y carpeta del programa.
    foreach ($k in $ViejasClaves) {
        if (Test-Path -LiteralPath $k) {
            try { Remove-Item -LiteralPath $k -Recurse -Force; Write-Host "  Quitado de «Aplicaciones instaladas»: $k" } catch { Write-Host "  No se pudo quitar $k" -ForegroundColor Yellow }
        }
    }
    try {
        if ((Test-Path -LiteralPath $runOnce) -and ((Get-Item -LiteralPath $runOnce).GetValueNames() -contains 'SuperProgram - terminar de desinstalar')) {
            Remove-ItemProperty -LiteralPath $runOnce -Name 'SuperProgram - terminar de desinstalar' -Force
        }
    } catch { }
    $carpetasViejas = @()
    if (((Split-Path -Leaf $viejaCarpeta) -eq $Viejo) -and ($viejaCarpeta.Length -gt 3)) { $carpetasViejas += $viejaCarpeta }
    $padre = Split-Path -Parent $viejaCarpeta
    if ($padre -and (Test-Path -LiteralPath $padre)) {
        $carpetasViejas += @(Get-ChildItem -LiteralPath $padre -Directory -Filter ($Viejo + '.anterior-*') -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    }
    foreach ($c in $carpetasViejas) {
        if (-not (Test-Path -LiteralPath $c)) { continue }
        if (Test-Enlace $c) { Write-Host "  $c es un enlace: no se toca." -ForegroundColor Yellow; continue }
        try { Remove-Item -LiteralPath $c -Recurse -Force -ErrorAction Stop; Write-Host "  Carpeta vieja borrada: $c" }
        catch { Write-Host "  No se pudo borrar $c ($($_.Exception.Message)): borrala a mano." -ForegroundColor Yellow }
    }

    # %ProgramData%\SuperProgram: la política y el registro de cambios de la empresa pasan a %ProgramData%\Actualizador.
    if ((Test-Path -LiteralPath $ViejosDatos) -and -not (Test-Enlace $ViejosDatos)) {
        foreach ($i in @(Get-ChildItem -LiteralPath $ViejosDatos -Force -ErrorAction SilentlyContinue)) {
            if ($i.Name -like 'Uninstall-SuperProgram*.ps1') { Remove-Item -LiteralPath $i.FullName -Force -ErrorAction SilentlyContinue; continue }
            $nombre = $i.Name
            if ($nombre -like '*.log*') { $nombre = 'SuperProgram-' + $nombre }   # los registros viejos se guardan con otro nombre
            $a = Join-Path $Datos $nombre
            $vacia = (Test-Path -LiteralPath $a -PathType Container) -and -not @(Get-ChildItem -LiteralPath $a -Force -ErrorAction SilentlyContinue).Count
            if ($vacia) { Remove-Item -LiteralPath $a -Force -ErrorAction SilentlyContinue }
            if (Test-Path -LiteralPath $a) { Write-Host "  Ya había $($a): se deja el de SuperProgram en $($i.FullName)."; continue }
            if ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            try { Move-Item -LiteralPath $i.FullName -Destination $a -Force -ErrorAction Stop; Write-Host "  Pasado: $($i.FullName) -> $a" }
            catch { Write-Host "  No se pudo pasar $($i.FullName): $($_.Exception.Message)" -ForegroundColor Yellow }
        }
        if (-not @(Get-ChildItem -LiteralPath $ViejosDatos -Force -ErrorAction SilentlyContinue).Count) { Remove-Item -LiteralPath $ViejosDatos -Force -ErrorAction SilentlyContinue }
    }

    # %LOCALAPPDATA%\SuperProgram de cada usuario (opciones, historial, ignoradas): pasa entera a ...\Actualizador.
    foreach ($perfil in $perfiles) {
        $vieja = Join-Path $perfil ('AppData\Local\' + $Viejo)
        $nueva = Join-Path $perfil 'AppData\Local\Actualizador'
        if (-not (Test-Path -LiteralPath $vieja) -or (Test-Enlace $vieja)) { continue }
        if ($Limpio) { Remove-Item -LiteralPath $vieja -Recurse -Force -ErrorAction SilentlyContinue; Write-Host "  Datos de usuario borrados: $vieja"; continue }
        if (Test-Path -LiteralPath $nueva) { Write-Host "  Ya había $($nueva): se deja $vieja como estaba."; continue }
        try { Rename-Item -LiteralPath $vieja -NewName 'Actualizador' -ErrorAction Stop; Write-Host "  Datos de usuario pasados: $vieja -> $nueva" }
        catch { Write-Host "  No se pudo pasar $vieja ($($_.Exception.Message)): se empieza con opciones nuevas." -ForegroundColor Yellow }
    }

    # Datos de cada usuario: los de 1.x (auditorías y opciones de la auditoría) no sirven en 2.x y se borran enteros.
    foreach ($perfil in $perfiles) {
        $carpeta = Join-Path $perfil 'AppData\Local\Actualizador'
        if (-not (Test-Path -LiteralPath $carpeta) -or (Test-Enlace $carpeta)) { continue }
        $opciones = Join-Path $carpeta 'opciones.json'
        $deUno = (Test-Path -LiteralPath (Join-Path $carpeta 'auditorias')) -or
                 ((Test-Path -LiteralPath $opciones) -and ([IO.File]::ReadAllText($opciones) -match '"(diasEventos|seccionesExcluidas)"'))
        if ($deUno -or $Limpio) {
            Remove-Item -LiteralPath $carpeta -Recurse -Force -ErrorAction SilentlyContinue
            if ($deUno) { Write-Host "Datos de la versión 1.x borrados: $carpeta" } else { Write-Host "Datos de usuario borrados: $carpeta" }
        }
    }

    # ------------------------------------------------------------------------------------------------------------
    Write-Paso '5 de 6: carpeta de datos, política y Visor de eventos'
    Set-AclAdministradores (Join-Path $Datos 'auditoria') $false
    if ($Politica) {
        if (-not (Test-Path -LiteralPath $Politica)) { throw "No existe $Politica." }
        $texto = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Politica).Path)
        try { $null = $texto | ConvertFrom-Json } catch { throw "$Politica no es JSON válido: $($_.Exception.Message)" }
        $dest = Join-Path $Datos 'politica.json'
        [IO.File]::WriteAllText($dest, $texto, (New-Object Text.UTF8Encoding($false)))
        & icacls.exe $dest /reset /C /Q | Out-Null   # hereda: sólo administradores escriben
        Write-Host "Política instalada en $dest"
    } elseif (Test-Path -LiteralPath (Join-Path $Datos 'politica.json')) {
        Write-Host 'Se mantiene la política de la empresa que ya estaba.'
    } else {
        Write-Host 'Sin política de la empresa (todo permitido). Ejemplo: politica.ejemplo.json'
    }
    try {
        if (-not [Diagnostics.EventLog]::SourceExists($Nombre)) {
            [Diagnostics.EventLog]::CreateEventSource($Nombre, 'Application')
            Write-Host 'Origen «Actualizador» registrado en el Visor de eventos (Aplicación).'
        }
    } catch { Write-Host "No se pudo registrar el origen del Visor de eventos: $($_.Exception.Message)" -ForegroundColor Yellow }

    # ------------------------------------------------------------------------------------------------------------
    Write-Paso '6 de 6: «Aplicaciones instaladas» y accesos directos'
    $exe = Join-Path $Destino 'Actualizador.exe'
    # Ruta completa de PowerShell (no se busca en el PATH) y el script entre comillas. El script pide el permiso de
    # administrador por su cuenta: Configuración abre los desinstaladores sin elevar.
    # 2.0.41 (relevamiento): la carpeta de sistema de Windows, no la variable SystemRoot: el usuario puede redefinirla en su
    # cuenta, este proceso elevado la hereda y la entrada de «Aplicaciones» quedaba apuntando a SU powershell.exe (que después
    # ejecutaría un administrador al desinstalar).
    $powershellExe = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    $desinstalar = '"' + $powershellExe + '" -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $Destino 'Uninstall-Actualizador.ps1') + '"'
    New-Item -Path $ClaveDesinstalar -Force | Out-Null
    $tam = [int]((Get-ChildItem -LiteralPath $Destino -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1KB)
    $valores = @{
        DisplayName = $Nombre; DisplayVersion = $version; Publisher = $Editor; DisplayIcon = $exe; InstallLocation = $Destino
        UninstallString = $desinstalar; QuietUninstallString = $desinstalar + ' -Silencioso'
        InstallDate = (Get-Date -Format 'yyyyMMdd'); Comments = 'Actualizador de programas, controladores, Windows y Microsoft Store'
    }
    foreach ($k in $valores.Keys) { New-ItemProperty -Path $ClaveDesinstalar -Name $k -Value $valores[$k] -PropertyType String -Force | Out-Null }
    foreach ($k in @('NoModify', 'NoRepair')) { New-ItemProperty -Path $ClaveDesinstalar -Name $k -Value 1 -PropertyType DWord -Force | Out-Null }
    New-ItemProperty -Path $ClaveDesinstalar -Name 'EstimatedSize' -Value $tam -PropertyType DWord -Force | Out-Null

    $lugares = @($comunes[0])
    if (-not $SinEscritorio) { $lugares += $comunes[1] }
    foreach ($l in $lugares) {
        if (-not (Test-Path -LiteralPath $l)) { continue }
        $s = $shell.CreateShortcut((Join-Path $l 'Actualizador.lnk'))
        $s.TargetPath = $exe
        $s.WorkingDirectory = $Destino
        $s.Description = 'Actualizador - programas, controladores y Windows'
        $s.Save()
    }

    if ($respaldo -and (Test-Path -LiteralPath $respaldo)) { Remove-Item -LiteralPath $respaldo -Recurse -Force -ErrorAction SilentlyContinue }
    if ($claveVieja -and (Test-Path -LiteralPath $claveVieja)) { Remove-Item -LiteralPath $claveVieja -Force -ErrorAction SilentlyContinue }
    $antesTexto = ''
    if ($anterior) { $antesTexto = " (antes: $anterior)" }
    try { Write-EventLog -LogName Application -Source $Nombre -EventId 1001 -EntryType Information -Message "Actualizador $version instalado en $Destino por $env:USERDOMAIN\$env:USERNAME$antesTexto." } catch { }

    Write-Host ''
    Write-Host "Listo: Actualizador $version instalado desde cero en $Destino." -ForegroundColor Green
    Write-Host 'Abrilo desde el menú Inicio o el escritorio. Consola: Actualizador.Cli ayuda'
} catch {
    $codigo = 1
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    # Vuelve a como estaba: la versión anterior en su lugar, con su entrada en «Aplicaciones instaladas» y sus accesos.
    if ($respaldo -and (Test-Path -LiteralPath $respaldo)) {
        try {
            if ($instalado -and (Test-Path -LiteralPath $Destino)) { Remove-Item -LiteralPath $Destino -Recurse -Force }
            Rename-Item -LiteralPath $respaldo -NewName (Split-Path -Leaf $Destino)
            if ($claveVieja -and (Test-Path -LiteralPath $claveVieja)) { & reg.exe import $claveVieja 2>$null | Out-Null }
            if ($habiaAccesos) {
                foreach ($l in @((Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'), (Join-Path $env:PUBLIC 'Desktop'))) {
                    if (-not (Test-Path -LiteralPath $l)) { continue }
                    $s = $shell.CreateShortcut((Join-Path $l 'Actualizador.lnk'))
                    $s.TargetPath = (Join-Path $Destino 'Actualizador.exe')
                    $s.WorkingDirectory = $Destino
                    $s.Save()
                }
            }
            Write-Host 'Se dejó la versión anterior como estaba.' -ForegroundColor Yellow
        } catch { Write-Host "No se pudo restaurar la versión anterior: $($_.Exception.Message). Está en $respaldo" -ForegroundColor Red }
    } elseif ($instalado -and (Test-Path -LiteralPath $Destino)) {
        Remove-Item -LiteralPath $Destino -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host 'No había otra versión: se quitó lo copiado.' -ForegroundColor Yellow
    }
    Write-Host "Detalle en $log"
} finally {
    try { Stop-Transcript | Out-Null } catch { }
}
exit $codigo
