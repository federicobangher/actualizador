# Funciones de la firma propia de las versiones (2.0.46). Las usan Firmar.ps1 y la prueba automática de GitHub. Windows PowerShell 5.1.

$NombreClaveFirma = 'Actualizador - firma de versiones'
$NombreClavePrueba = 'Actualizador - firma de PRUEBA'

function Find-GhFirma {
    $c = Get-Command gh -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in @((Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'), (Join-Path ${env:ProgramFiles(x86)} 'GitHub CLI\gh.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\GitHub CLI\gh.exe'))) {
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }
    return $null
}

# La clave (certificado con su clave privada) en el almacén de esta cuenta; la más nueva si hubiera varias.
function Get-ClaveDeFirma([string]$Nombre) {
    return Get-ChildItem -Path 'Cert:\CurrentUser\My' | Where-Object { $_.FriendlyName -eq $Nombre -and $_.HasPrivateKey } |
           Sort-Object NotBefore -Descending | Select-Object -First 1
}

# RSA de 3072 bits en el proveedor de claves de Windows (CNG). Con contraseña (ProtectHigh): Windows la pide al crearla y en cada firma.
function New-ClaveDeFirma([string]$Nombre, [switch]$SinContrasena) {
    $p = @{
        Subject = 'CN=' + $Nombre; FriendlyName = $Nombre; Type = 'CodeSigningCert'
        KeyAlgorithm = 'RSA'; KeyLength = 3072; HashAlgorithm = 'SHA256'; KeyUsage = 'DigitalSignature'
        KeyExportPolicy = 'Exportable'; Provider = 'Microsoft Software Key Storage Provider'
        CertStoreLocation = 'Cert:\CurrentUser\My'; NotAfter = (Get-Date).AddYears(30)
    }
    if (-not $SinContrasena) { $p.KeyProtection = 'ProtectHigh' }
    return New-SelfSignedCertificate @p
}

function Get-ClavePublicaRsa($Cert) {
    $pub = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Cert)
    try { return $pub.ExportParameters($false) } finally { $pub.Dispose() }
}

# «identificador módulo-base64 exponente-base64 comentario»: la línea para res\claves-de-firma.txt.
function Get-LineaClavePublica($Cert) {
    $p = Get-ClavePublicaRsa $Cert
    return (Get-IdClave $p.Modulus) + ' ' + [Convert]::ToBase64String($p.Modulus) + ' ' + [Convert]::ToBase64String($p.Exponent) +
           ' Fede ' + (Get-Date -Format 'yyyy-MM-dd')
}

# La misma clave como la usa Test-FirmaEntrega (para comprobar cada firma recién hecha).
function Get-ClavePublicaComoLista($Cert) {
    $p = Get-ClavePublicaRsa $Cert
    return @(@{ Id = (Get-IdClave $p.Modulus); Modulo = $p.Modulus; Exponente = $p.Exponent })
}

# El contenido de «<entrega>.firma».
function New-TextoFirma($Cert, [string]$Archivo, [string]$Version, [string]$Sha256) {
    $p = Get-ClavePublicaRsa $Cert
    $datos = [Text.Encoding]::UTF8.GetBytes((Get-MensajeFirma $Archivo $Version $Sha256))
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Cert)
    try { $firma = $rsa.SignData($datos, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1) }
    finally { $rsa.Dispose() }
    return (@('Actualizador firma de versión 1',
              "archivo: $Archivo",
              "version: $Version",
              ('sha256: ' + $Sha256.ToLowerInvariant()),
              ('clave: ' + (Get-IdClave $p.Modulus)),
              ('firma: ' + [Convert]::ToBase64String($firma))) -join "`n") + "`n"
}

# Respaldo .pfx con contraseña (la clave privada; guardalo en un pendrive, aparte).
function Save-RespaldoClave($Cert, [string]$Carpeta) {
    if (-not (Test-Path -LiteralPath $Carpeta -PathType Container)) { New-Item -ItemType Directory -Path $Carpeta -Force | Out-Null }
    $archivo = Join-Path $Carpeta ('Actualizador-clave-de-firma-' + (Get-Date -Format 'yyyyMMdd') + '.pfx')
    $pw = Read-Host 'Contraseña para el respaldo (no se muestra)' -AsSecureString
    $pw2 = Read-Host 'Repetila' -AsSecureString
    $a = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw))
    $b = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw2))
    if ($a -cne $b -or $a.Length -lt 8) { throw 'Las contraseñas no coinciden o tienen menos de 8 caracteres: no se guardó el respaldo (volvé a ejecutar Firmar.cmd -Respaldo <carpeta>).' }
    Export-PfxCertificate -Cert $Cert -FilePath $archivo -Password $pw | Out-Null
    Write-Host "Respaldo guardado: $archivo" -ForegroundColor Green
    Write-Host 'Copialo a un pendrive y borralo de esta PC si querés. Para recuperarlo en otra PC: doble clic en el .pfx (almacén «Usuario actual»).'
}

# Agrega la línea a res/claves-de-firma.txt del proyecto en GitHub (una confirmación en main, sin versión nueva).
function Publish-ClavePublica([string]$Linea, [string]$Repo) {
    $ruta = 'res/claves-de-firma.txt'
    $actual = Invoke-Gh @('api', "repos/$Repo/contents/$ruta", '--jq', '.sha + " " + .content')
    if (-not $actual.Ok) { throw "No se pudo leer $ruta en $Repo`n$($actual.Texto)" }
    $partes = $actual.Texto.Trim().Split(' ', 2)
    $texto = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($partes[1] -replace '\\n', '' -replace '\s', '')))
    $id = $Linea.Split(' ')[0]
    if ($texto -match ('(?m)^' + $id + '\s')) { Write-Host 'La clave ya estaba en el proyecto.'; return }
    $nuevo = $texto.TrimEnd("`r", "`n") + "`r`n" + $Linea + "`r`n"
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($nuevo))
    $r = Invoke-Gh @('api', '-X', 'PUT', "repos/$Repo/contents/$ruta", '-f', "message=Clave pública de firma de versiones ($id)", '-f', "content=$b64", '-f', "sha=$($partes[0])", '-f', 'branch=main')
    if (-not $r.Ok) { throw "GitHub no aceptó la clave pública`n$($r.Texto)" }
    Write-Host "Clave pública $id agregada a $ruta." -ForegroundColor Green
}
