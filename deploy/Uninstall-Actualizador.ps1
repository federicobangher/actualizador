<#
.SYNOPSIS
    Desinstala Actualizador de este equipo.

.DESCRIPTION
    Lo llaman «Configuración > Aplicaciones > Actualizador > Desinstalar», «Instalar.cmd /quitar», el menú «Más» de la
    ventana o una herramienta de distribución (-Silencioso). Si no se abrió como administrador, pide el permiso él mismo
    (Configuración abre los desinstaladores SIN elevar: antes el script terminaba ahí, sin hacer nada).

    Quita, en este orden: los procesos de Actualizador, las revisiones programadas de todos los usuarios, la carpeta del
    programa (si algún archivo sigue bloqueado, se termina de borrar al reiniciar), las carpetas apartadas por
    instalaciones anteriores, los accesos directos de todos los usuarios y, AL FINAL, la entrada de «Aplicaciones
    instaladas» (si algo falla antes, la entrada queda y se puede volver a desinstalar).

    El registro de cambios (%ProgramData%\Actualizador\auditoria) y la política se CONSERVAN, porque son registros de la
    empresa. Con -BorrarDatos se borran también, junto con las opciones, el historial y los ignorados de cada usuario.
    Todo queda anotado en %ProgramData%\Actualizador\desinstalacion.log.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Uninstall-Actualizador.ps1
    powershell -ExecutionPolicy Bypass -File Uninstall-Actualizador.ps1 -Silencioso -BorrarDatos

.PARAMETER Silencioso
    Sin preguntas ni pausa al final (Intune, SCCM, GPO o RMM).

.PARAMETER Confirmado
    No pregunta (ya se confirmó en la ventana de Actualizador) pero espera Enter al final.
#>
[CmdletBinding()]
param(
    [switch]$BorrarDatos,
    [switch]$Silencioso,
    # Ya se confirmó antes (menú «Más» de la ventana): no se pregunta otra vez, pero se espera Enter al final.
    [switch]$Confirmado
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$ClaveDesinstalar = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Actualizador'
$OtrasClaves = @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Actualizador',
                 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Actualizador')
$Datos = Join-Path $env:ProgramData 'Actualizador'
$Ejecutables = @('Actualizador.exe', 'Actualizador.Cli.exe', 'Actualizador.Aviso.exe')
$Procesos = @('Actualizador', 'Actualizador.Cli', 'Actualizador.Aviso')
$PowerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Test-Administrador {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-Enlace([string]$Ruta) {
    if (-not (Test-Path -LiteralPath $Ruta)) { return $false }
    return [bool]((Get-Item -LiteralPath $Ruta -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
}

# Al terminar se espera una tecla: abierto desde Configuración, la ventana se cerraba antes de poder leer qué pasó.
function Wait-Cierre {
    if ($Silencioso) { return }
    try { if ($Host.Name -eq 'ConsoleHost') { Write-Host ''; [void](Read-Host 'Presioná Enter para cerrar') } } catch { }
}

# Carpetas de perfil de todos los usuarios del equipo (se leen del registro, no se supone C:\Users).
function Get-Perfiles {
    $lista = @()
    try {
        foreach ($k in Get-ChildItem -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' -ErrorAction Stop) {
            if ($k.PSChildName -notmatch '^S-1-5-21-') { continue }
            $p = $null
            try { $p = (Get-ItemProperty -LiteralPath $k.PSPath -Name 'ProfileImagePath' -ErrorAction Stop).ProfileImagePath } catch { }
            if ($p) { $p = [Environment]::ExpandEnvironmentVariables($p) }
            if ($p -and (Test-Path -LiteralPath $p)) { $lista += $p }
        }
    } catch { }
    return $lista
}

# Borra una carpeta entera, con reintentos (un proceso que recién se cerró tarda en soltar sus archivos).
function Remove-Carpeta([string]$Carpeta) {
    for ($i = 1; $i -le 5; $i++) {
        if (-not (Test-Path -LiteralPath $Carpeta)) { return $true }
        try { Remove-Item -LiteralPath $Carpeta -Recurse -Force -ErrorAction Stop; return $true }
        catch { if ($i -lt 5) { Start-Sleep -Seconds 2 } }
    }
    return -not (Test-Path -LiteralPath $Carpeta)
}

# Lo que quedó bloqueado se borra en el próximo inicio de Windows (RunOnce, como administrador).
function Register-BorradoAlReiniciar([string]$Carpeta) {
    $runOnce = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce'
    if (-not (Test-Path -LiteralPath $runOnce)) { New-Item -Path $runOnce -Force | Out-Null }
    $cmd = (Join-Path $env:SystemRoot 'System32\cmd.exe') + ' /c rd /s /q "' + $Carpeta + '"'
    New-ItemProperty -LiteralPath $runOnce -Name 'Actualizador - terminar de desinstalar' -Value $cmd -PropertyType String -Force | Out-Null
}

# ---------------------------------------------------------------------------------------------------------------
# Permiso de administrador: si falta, se pide (UAC) y se vuelve a abrir este mismo script, con las mismas opciones.
if (-not (Test-Administrador)) {
    $argumentos = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
    if ($BorrarDatos) { $argumentos += '-BorrarDatos' }
    if ($Silencioso) { $argumentos += '-Silencioso' }
    if ($Confirmado) { $argumentos += '-Confirmado' }
    try {
        $p = Start-Process -FilePath $PowerShellExe -ArgumentList $argumentos -Verb RunAs -Wait -PassThru
        exit $p.ExitCode
    } catch {
        Write-Host 'Para desinstalar Actualizador hace falta el permiso de administrador, y no se dio (o Windows no dejó pedirlo).' -ForegroundColor Red
        Write-Host "Detalle: $($_.Exception.Message)"
        Wait-Cierre
        exit 5
    }
}

# Nada de esta carpeta queda en uso por la consola (si el script se abrió con la carpeta del programa como actual).
try { Set-Location -LiteralPath $env:SystemRoot } catch { }

if (-not (Test-Path -LiteralPath $Datos)) { try { New-Item -ItemType Directory -Path $Datos -Force | Out-Null } catch { } }
$log = Join-Path $Datos 'desinstalacion.log'
$conTranscripcion = $false
try { Start-Transcript -LiteralPath $log -Append | Out-Null; $conTranscripcion = $true } catch { }

$codigo = 0
try {
    # Dónde está instalado: la entrada de «Aplicaciones instaladas»; si no, la carpeta de este script; si no, la habitual.
    $destino = $null
    if (Test-Path -LiteralPath $ClaveDesinstalar) {
        try { $destino = [string](Get-ItemProperty -LiteralPath $ClaveDesinstalar -Name 'InstallLocation' -ErrorAction Stop).InstallLocation } catch { }
    }
    if (-not $destino -and $PSScriptRoot -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Actualizador.exe'))) { $destino = $PSScriptRoot }
    if (-not $destino) { $destino = Join-Path $env:ProgramFiles 'Actualizador' }
    $destino = $destino.TrimEnd('\')
    Write-Host "Actualizador - desinstalación - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - $env:USERDOMAIN\$env:USERNAME - $destino"

    # Por seguridad sólo se borra una carpeta llamada «Actualizador» que no sea un enlace ni la raíz de un disco.
    $carpetaValida = ((Split-Path -Leaf $destino) -eq 'Actualizador') -and ($destino.Length -gt 3) -and -not (Test-Enlace $destino)
    if ((Test-Path -LiteralPath $destino) -and -not $carpetaValida) {
        throw "La carpeta registrada ($destino) no se llama «Actualizador» o es un enlace: no se borra nada por seguridad."
    }

    if (-not $Silencioso -and -not $Confirmado) {
        $r = Read-Host "¿Desinstalar Actualizador de $destino? [S/n]"
        if ($r -match '^\s*(n|no)\s*$') { Write-Host 'Cancelado: no se cambió nada.'; Wait-Cierre; exit 0 }
    }

    # 1. Procesos: todos los de Actualizador (también los de otras sesiones, cuya ruta no siempre se puede leer).
    $abiertos = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $Procesos -contains $_.Name })
    if ($abiertos.Count -gt 0) {
        $abiertos | Stop-Process -Force -ErrorAction SilentlyContinue
        $abiertos | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
        Write-Host "Cerrados: $($abiertos.Count) procesos de Actualizador."
    }

    # 2. Revisiones programadas de cualquier usuario («Actualizador - Revisión de actualizaciones (usuario)»).
    try {
        $servicio = New-Object -ComObject Schedule.Service
        $servicio.Connect()
        $raizTareas = $servicio.GetFolder('\')
        foreach ($t in @($raizTareas.GetTasks(1))) {
            if ($t.Name -like 'Actualizador - *') { $raizTareas.DeleteTask($t.Name, 0); Write-Host "Revisión programada quitada: $($t.Name)" }
        }
    } catch { Write-Host "No se pudieron revisar las tareas programadas: $($_.Exception.Message)" -ForegroundColor Yellow }

    # Si este script está dentro de la carpeta que se va a borrar, queda una copia en %ProgramData%\Actualizador y la
    # entrada de «Aplicaciones instaladas» apunta a ella: si algo falla después, se puede volver a desinstalar.
    $copia = $null
    if ($PSCommandPath -and $PSCommandPath.StartsWith($destino + '\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $ClaveDesinstalar)) {
        try {
            $copia = Join-Path $Datos 'Uninstall-Actualizador.ps1'
            Copy-Item -LiteralPath $PSCommandPath -Destination $copia -Force
            $nuevo = '"' + $PowerShellExe + '" -NoProfile -ExecutionPolicy Bypass -File "' + $copia + '"'
            Set-ItemProperty -LiteralPath $ClaveDesinstalar -Name 'UninstallString' -Value $nuevo
            Set-ItemProperty -LiteralPath $ClaveDesinstalar -Name 'QuietUninstallString' -Value ($nuevo + ' -Silencioso')
        } catch { $copia = $null }
    }

    # 3. La carpeta del programa (y las apartadas por instalaciones que no terminaron).
    $pendiente = $false
    if (Test-Path -LiteralPath $destino) {
        if (Remove-Carpeta $destino) { Write-Host "Quitado: $destino" }
        else {
            Register-BorradoAlReiniciar $destino
            $pendiente = $true
            Write-Host "Algunos archivos de $destino siguen en uso: se terminan de borrar al reiniciar Windows." -ForegroundColor Yellow
        }
    } else { Write-Host "La carpeta $destino ya no estaba." }
    $padre = Split-Path -Parent $destino
    if ($padre -and (Test-Path -LiteralPath $padre)) {
        foreach ($d in @(Get-ChildItem -LiteralPath $padre -Directory -Filter 'Actualizador.anterior-*' -ErrorAction SilentlyContinue)) {
            if (Remove-Carpeta $d.FullName) { Write-Host "Quitado: $($d.FullName)" }
        }
    }

    # 4. Accesos directos a Actualizador: los comunes y los de cada usuario.
    $shell = $null
    try { $shell = New-Object -ComObject WScript.Shell } catch { }
    $lugares = @((Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'), (Join-Path $env:PUBLIC 'Desktop'))
    foreach ($perfil in @(Get-Perfiles)) {
        $lugares += (Join-Path $perfil 'Desktop')
        $lugares += (Join-Path $perfil 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs')
    }
    foreach ($l in $lugares) {
        if (-not (Test-Path -LiteralPath $l)) { continue }
        foreach ($lnk in @(Get-ChildItem -LiteralPath $l -Filter '*.lnk' -File -ErrorAction SilentlyContinue)) {
            $esNuestro = $lnk.Name -ieq 'Actualizador.lnk'
            if (-not $esNuestro -and $shell) {
                try { $esNuestro = $Ejecutables -contains [IO.Path]::GetFileName([string]$shell.CreateShortcut($lnk.FullName).TargetPath) } catch { }
            }
            if ($esNuestro) {
                try { Remove-Item -LiteralPath $lnk.FullName -Force -ErrorAction Stop; Write-Host "Acceso directo quitado: $($lnk.FullName)" }
                catch { Write-Host "No se pudo quitar $($lnk.FullName): $($_.Exception.Message)" -ForegroundColor Yellow }
            }
        }
    }

    # 5. Al final, la entrada de «Aplicaciones instaladas»: recién ahora, con todo lo demás hecho.
    foreach ($k in @($ClaveDesinstalar) + $OtrasClaves) {
        if (Test-Path -LiteralPath $k) { Remove-Item -LiteralPath $k -Recurse -Force; Write-Host "Quitado de «Aplicaciones instaladas»: $k" }
    }
    if ($copia -and (Test-Path -LiteralPath $copia)) { Remove-Item -LiteralPath $copia -Force -ErrorAction SilentlyContinue }
    try { Write-EventLog -LogName Application -Source 'Actualizador' -EventId 1002 -EntryType Information -Message "Actualizador desinstalado de $destino por $env:USERDOMAIN\$env:USERNAME." } catch { }

    if ($BorrarDatos) {
        # Opciones, historial, ignorados y registros de cada usuario.
        foreach ($perfil in @(Get-Perfiles)) {
            $carpeta = Join-Path $perfil 'AppData\Local\Actualizador'
            if ((Test-Path -LiteralPath $carpeta) -and -not (Test-Enlace $carpeta)) {
                if (Remove-Carpeta $carpeta) { Write-Host "Quitado: $carpeta" } else { Write-Host "No se pudo quitar $carpeta" -ForegroundColor Yellow }
            }
        }
        try { if ([Diagnostics.EventLog]::SourceExists('Actualizador')) { [Diagnostics.EventLog]::DeleteEventSource('Actualizador') } } catch { }
        # La política y el registro de cambios, al último (este mismo registro de la desinstalación está ahí).
        if ($conTranscripcion) { try { Stop-Transcript | Out-Null } catch { }; $conTranscripcion = $false }
        if ((Test-Path -LiteralPath $Datos) -and -not (Test-Enlace $Datos)) {
            if (Remove-Carpeta $Datos) { Write-Host "Quitado: $Datos (política y registro de cambios)" }
        }
    } else {
        Write-Host "Se conservan la política y el registro de cambios en $Datos, y los datos de cada usuario (-BorrarDatos para quitar todo)."
    }

    Write-Host ''
    if ($pendiente) { Write-Host 'Actualizador desinstalado. Reiniciá Windows para terminar de borrar su carpeta.' -ForegroundColor Green }
    else { Write-Host 'Actualizador desinstalado.' -ForegroundColor Green }
} catch {
    $codigo = 1
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'La entrada de «Aplicaciones instaladas» se dejó para que puedas volver a intentarlo.'
    if ($conTranscripcion) { Write-Host "Detalle en $log" }
} finally {
    if ($conTranscripcion) { try { Stop-Transcript | Out-Null } catch { } }
}
Wait-Cierre
exit $codigo
