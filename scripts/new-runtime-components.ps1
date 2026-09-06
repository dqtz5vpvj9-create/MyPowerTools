[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$FullRoot,
    [Parameter(Mandatory = $true)][string]$CoreZipPath,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$SigningKeyBase64 = '',
    [switch]$AllowUnsigned
)

$ErrorActionPreference = 'Stop'
$fullRootFull = [IO.Path]::GetFullPath($FullRoot)
$outputRootFull = [IO.Path]::GetFullPath($OutputRoot)
$coreZipFull = [IO.Path]::GetFullPath($CoreZipPath)
New-Item -ItemType Directory -Path $outputRootFull -Force | Out-Null

function Add-Tree {
    param([string]$Source, [string]$Stage, [string]$RelativeDestination)
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Runtime component source is missing: $Source"
    }
    $destination = Join-Path $Stage $RelativeDestination
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination -Recurse -Force
}

function New-ComponentArchive {
    param(
        [string]$Id,
        [string]$Asset,
        [string]$InstallLocation,
        [string[]]$HealthCheck,
        [scriptblock]$Populate
    )
    $stage = Join-Path $outputRootFull ".stage-$Id"
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        # Populate blocks can invoke native tools such as robocopy. Suppress their
        # success output so it cannot become part of this function's return value
        # and corrupt the JSON component array.
        $null = & $Populate $stage
        $archive = Join-Path $outputRootFull $Asset
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
        return [ordered]@{
            id = $Id
            version = $Version
            architecture = 'x64'
            asset = $Asset
            sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
            size = (Get-Item -LiteralPath $archive).Length
            installLocation = $InstallLocation
            healthCheck = $HealthCheck
        }
    }
    finally {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    }
}

$components = [Collections.Generic.List[object]]::new()
$components.Add((New-ComponentArchive -Id 'dotnet-10-x64' -Asset 'MyPowerTools-runtime-dotnet-10-win-x64.zip' -InstallLocation 'Runtime\dotnet' -HealthCheck @(
    'host\fxr\10.0.x\hostfxr.dll',
    'shared\Microsoft.NETCore.App\10.0.x',
    'shared\Microsoft.AspNetCore.App\10.0.x',
    'shared\Microsoft.WindowsDesktop.App\10.0.x') -Populate {
        param($stage)
        Add-Tree -Source (Join-Path $fullRootFull 'Runtime\dotnet') -Stage $stage -RelativeDestination 'Runtime\dotnet'
    }))
$components.Add((New-ComponentArchive -Id 'python-3.12-x64' -Asset 'MyPowerTools-runtime-python-3.12-win-x64.zip' -InstallLocation 'Runtimes\Python312' -HealthCheck @(
    'python.exe', 'import ssl, sqlite3, venv') -Populate {
        param($stage)
        $source = Join-Path $fullRootFull 'Runtimes\Python312'
        $destination = Join-Path $stage 'Runtimes\Python312'
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $robocopy = (Get-Command robocopy.exe -CommandType Application -ErrorAction Stop).Source
        & $robocopy $source $destination /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /XJ /XD (Join-Path $source 'Lib\site-packages') /NFL /NDL /NJH /NJS /NP
        if ($LASTEXITCODE -ge 8) { throw "Could not stage the Python runtime (robocopy $LASTEXITCODE)." }
    }))
$components.Add((New-ComponentArchive -Id 'smartbird-dependencies' -Asset "MyPowerTools-runtime-smartbird-$Version-win-x64.zip" -InstallLocation 'Runtimes\SmartBird' -HealthCheck @(
    'test_tools\smartbird_thermostat.py', 'Python312\Lib\site-packages\flask') -Populate {
        param($stage)
        Add-Tree -Source (Join-Path $fullRootFull 'Runtimes\SmartBird') -Stage $stage -RelativeDestination 'Runtimes\SmartBird'
        Add-Tree -Source (Join-Path $fullRootFull 'Runtimes\Python312\Lib\site-packages') -Stage $stage -RelativeDestination 'Runtimes\Python312\Lib\site-packages'
    }))
$components.Add((New-ComponentArchive -Id 'doubao-dependencies' -Asset "MyPowerTools-runtime-doubao-$Version-win-x64.zip" -InstallLocation 'Runtimes\Doubao' -HealthCheck @(
    '.venv\Scripts\mcp-server.exe', 'planner', 'tool_server') -Populate {
        param($stage)
        Add-Tree -Source (Join-Path $fullRootFull 'Runtimes\Doubao') -Stage $stage -RelativeDestination 'Runtimes\Doubao'
    }))
$components.Add((New-ComponentArchive -Id 'android-platform-tools' -Asset 'MyPowerTools-runtime-android-platform-tools-win-x64.zip' -InstallLocation 'Tools\AndroidPlatformTools' -HealthCheck @(
    'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll') -Populate {
        param($stage)
        Add-Tree -Source (Join-Path $fullRootFull 'Tools\AndroidPlatformTools') -Stage $stage -RelativeDestination 'Tools\AndroidPlatformTools'
    }))

$core = [ordered]@{
    asset = [IO.Path]::GetFileName($coreZipFull)
    sha256 = (Get-FileHash -LiteralPath $coreZipFull -Algorithm SHA256).Hash.ToLowerInvariant()
    size = (Get-Item -LiteralPath $coreZipFull).Length
}
$manifest = [ordered]@{
    schemaVersion = 1
    product = 'MyPowerTools'
    version = $Version
    architecture = 'x64'
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    core = $core
    components = $components.ToArray()
}
$manifestPath = Join-Path $outputRootFull 'runtime-components.json'
$manifestJson = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($manifestPath, $manifestJson, [Text.UTF8Encoding]::new($false))

$signaturePath = "$manifestPath.sig"
if ([string]::IsNullOrWhiteSpace($SigningKeyBase64)) {
    if (-not $AllowUnsigned) { throw 'Runtime component manifest signing key is required.' }
    if (Test-Path -LiteralPath $signaturePath) { Remove-Item -LiteralPath $signaturePath -Force }
} else {
    Add-Type -Path (Join-Path $PSScriptRoot 'ed25519.cs')
    $key = [Convert]::FromBase64String($SigningKeyBase64.Trim())
    if ($key.Length -ne 32) { throw 'Runtime component signing key must be a 32-byte base64 seed.' }
    $signature = [Mpt.Ed25519]::Sign([Text.Encoding]::UTF8.GetBytes($manifestJson), $key)
    [IO.File]::WriteAllText($signaturePath, [Convert]::ToBase64String($signature), [Text.UTF8Encoding]::new($false))
}

$installerSignatureParams = @{
    RuntimeComponentsManifestPath = $manifestPath
    CoreZipPath = $coreZipFull
    OutputIncludePath = Join-Path (Split-Path -Parent $outputRootFull) 'web-installer-signing-key.iss'
    SigningKeyBase64 = $SigningKeyBase64
    AllowUnsigned = $AllowUnsigned
}
[void](& (Join-Path $PSScriptRoot 'new-web-installer-signatures.ps1') @installerSignatureParams)

$manifest
