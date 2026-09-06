[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeComponentsManifestPath,
    [Parameter(Mandatory = $true)][string]$CoreZipPath,
    [Parameter(Mandatory = $true)][string]$OutputIncludePath,
    [string]$SigningKeyBase64 = '',
    [switch]$AllowUnsigned
)

$ErrorActionPreference = 'Stop'

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$FilePath failed with exit code $exitCode."
    }
}

function Find-ISSigTool {
    $command = Get-Command 'ISSigTool.exe' -CommandType Application -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\ISSigTool.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISSigTool.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISSigTool.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }
    throw 'Inno Setup Signature Tool (ISSigTool.exe) was not found.'
}

function ConvertTo-FixedUnsignedBigEndian {
    param(
        [Parameter(Mandatory = $true)][Numerics.BigInteger]$Value,
        [Parameter(Mandatory = $true)][int]$Length
    )

    $encoded = $Value.ToByteArray($true, $true)
    if ($encoded.Length -gt $Length) {
        throw "Integer does not fit in $Length bytes."
    }
    $result = [byte[]]::new($Length)
    [Array]::Copy($encoded, 0, $result, $Length - $encoded.Length, $encoded.Length)
    return $result
}

function New-DeterministicISSigPrivateKey {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Seed,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    $domain = [Text.Encoding]::UTF8.GetBytes('MyPowerTools/ISSig/P-256/v1')
    $hmac = [Security.Cryptography.HMACSHA256]::new($Seed)
    try {
        $digest = $hmac.ComputeHash($domain)
    }
    finally {
        $hmac.Dispose()
    }

    $curveOrderBytes = [Convert]::FromHexString(
        'FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551')
    $curveOrder = [Numerics.BigInteger]::new($curveOrderBytes, $true, $true)
    $candidate = [Numerics.BigInteger]::new($digest, $true, $true)
    $privateValue = ($candidate % ($curveOrder - [Numerics.BigInteger]::One)) +
        [Numerics.BigInteger]::One
    $privateBytes = ConvertTo-FixedUnsignedBigEndian -Value $privateValue -Length 32

    $parameters = [Security.Cryptography.ECParameters]::new()
    $parameters.Curve = [Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256')
    $parameters.D = $privateBytes
    $ecdsa = [Security.Cryptography.ECDsa]::Create($parameters)
    try {
        $exported = $ecdsa.ExportParameters($true)
    }
    finally {
        $ecdsa.Dispose()
    }

    $publicBytes = [byte[]]::new(64)
    [Array]::Copy($exported.Q.X, 0, $publicBytes, 0, 32)
    [Array]::Copy($exported.Q.Y, 0, $publicBytes, 32, 32)
    $keyId = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($publicBytes)).ToLowerInvariant()
    $publicX = [Convert]::ToHexString($exported.Q.X).ToLowerInvariant()
    $publicY = [Convert]::ToHexString($exported.Q.Y).ToLowerInvariant()
    $privateD = [Convert]::ToHexString($exported.D).ToLowerInvariant()

    $keyText = @(
        'format issig-private-key'
        "key-id $keyId"
        "public-x $publicX"
        "public-y $publicY"
        "private-d $privateD"
        ''
    ) -join "`r`n"
    [IO.File]::WriteAllText($OutputPath, $keyText, [Text.UTF8Encoding]::new($false))

    return [pscustomobject]@{
        KeyId = $keyId
        PublicX = $publicX
        PublicY = $publicY
    }
}

$manifestFull = [IO.Path]::GetFullPath($RuntimeComponentsManifestPath)
$coreZipFull = [IO.Path]::GetFullPath($CoreZipPath)
$includeFull = [IO.Path]::GetFullPath($OutputIncludePath)
if (-not (Test-Path -LiteralPath $manifestFull -PathType Leaf)) {
    throw "Runtime component manifest does not exist: $manifestFull"
}
if (-not (Test-Path -LiteralPath $coreZipFull -PathType Leaf)) {
    throw "Core archive does not exist: $coreZipFull"
}

$manifest = Get-Content -LiteralPath $manifestFull -Raw | ConvertFrom-Json
$componentRoot = Split-Path -Parent $manifestFull
$assetPaths = [Collections.Generic.List[string]]::new()
$assetPaths.Add($coreZipFull)
foreach ($component in @($manifest.components)) {
    $assetPath = Join-Path $componentRoot ([string]$component.asset)
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Runtime component archive does not exist: $assetPath"
    }
    $assetPaths.Add([IO.Path]::GetFullPath($assetPath))
}

if ([string]::IsNullOrWhiteSpace($SigningKeyBase64)) {
    if (-not $AllowUnsigned) {
        throw 'Web installer ISSig signing requires a 32-byte signing seed.'
    }
    foreach ($assetPath in $assetPaths) {
        $signaturePath = "$assetPath.issig"
        if (Test-Path -LiteralPath $signaturePath) {
            Remove-Item -LiteralPath $signaturePath -Force
        }
    }
    if (Test-Path -LiteralPath $includeFull) {
        Remove-Item -LiteralPath $includeFull -Force
    }
    return
}

$seed = [Convert]::FromBase64String($SigningKeyBase64.Trim())
if ($seed.Length -ne 32) {
    throw 'Web installer ISSig signing seed must be exactly 32 bytes encoded as base64.'
}

$sigTool = Find-ISSigTool
$tempKeyPath = Join-Path ([IO.Path]::GetTempPath()) (
    'MyPowerTools-issig-' + [guid]::NewGuid().ToString('N') + '.key')
try {
    $publicKey = New-DeterministicISSigPrivateKey -Seed $seed -OutputPath $tempKeyPath
    foreach ($assetPath in $assetPaths) {
        Invoke-Native -FilePath $sigTool -ArgumentList @(
            "--key-file=$tempKeyPath",
            '--allow-overwrite',
            'sign',
            $assetPath)
        Invoke-Native -FilePath $sigTool -ArgumentList @(
            "--key-file=$tempKeyPath",
            'verify',
            $assetPath)
    }

    $includeLines = @(
        '#define WebISSigRuntimeID "mpt01"'
        "#define WebISSigKeyID `"$($publicKey.KeyId)`""
        "#define WebISSigPublicX `"$($publicKey.PublicX)`""
        "#define WebISSigPublicY `"$($publicKey.PublicY)`""
    )
    New-Item -ItemType Directory -Path (Split-Path -Parent $includeFull) -Force | Out-Null
    [IO.File]::WriteAllLines($includeFull, $includeLines, [Text.UTF8Encoding]::new($false))
}
finally {
    [Array]::Clear($seed, 0, $seed.Length)
    if (Test-Path -LiteralPath $tempKeyPath) {
        Remove-Item -LiteralPath $tempKeyPath -Force
    }
}

[pscustomobject]@{
    IncludePath = $includeFull
    SignedAssets = $assetPaths.Count
    RuntimeID = 'mpt01'
    KeyID = $publicKey.KeyId
}
