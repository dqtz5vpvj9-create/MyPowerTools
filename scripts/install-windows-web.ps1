[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [ValidateSet('stable', 'nightly', 'local')][string]$Channel = 'stable',
    [string]$DownloadBaseUrl = '',
    [string]$ManifestPath = (Join-Path $PSScriptRoot 'runtime-components.json'),
    [string]$SignaturePath = (Join-Path $PSScriptRoot 'runtime-components.json.sig'),
    [string]$PublicKeyPath = (Join-Path $PSScriptRoot 'ota-signing-public-key.txt'),
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [switch]$AllowUnsigned,
    [switch]$DryRun,
    [switch]$NoOpenApp
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-VerifiedAsset {
    param([object]$Record, [string]$BaseUrl, [string]$CacheRoot)
    $destination = Join-Path $CacheRoot ([string]$Record.asset)
    $expected = ([string]$Record.sha256).ToLowerInvariant()
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $cached = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($cached -eq $expected) { return $destination }
        Remove-Item -LiteralPath $destination -Force
    }
    if (Test-Path -LiteralPath $BaseUrl -PathType Container) {
        $localCandidates = @(
            (Join-Path $BaseUrl ([string]$Record.asset)),
            (Join-Path $BaseUrl (Join-Path 'runtime-components' ([string]$Record.asset))))
        $localSource = $localCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if ($null -eq $localSource) { throw "Local release asset is missing: $($Record.asset)" }
        Copy-Item -LiteralPath $localSource -Destination $destination -Force
    } else {
        Invoke-WebRequest -Uri "$($BaseUrl.TrimEnd('/'))/$([string]$Record.asset)" -OutFile $destination -UseBasicParsing -Headers @{ 'User-Agent' = 'MyPowerTools-Web-Setup' }
    }
    $actual = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Remove-Item -LiteralPath $destination -Force
        throw "Downloaded asset hash mismatch: $($Record.asset)"
    }
    return $destination
}

function Expand-VerifiedArchive {
    param([object]$Record, [string]$BaseUrl, [string]$CacheRoot, [string]$Destination)
    $archive = Get-VerifiedAsset -Record $Record -BaseUrl $BaseUrl -CacheRoot $CacheRoot
    Expand-Archive -LiteralPath $archive -DestinationPath $Destination -Force
}

function Test-SystemDotNet10X64 {
    $dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $dotnet) { return $false }
    $info = (& $dotnet.Source --info 2>$null) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $info -notmatch '(?im)^\s*Architecture:\s*x64\s*$') { return $false }
    $runtimes = @(& $dotnet.Source --list-runtimes 2>$null)
    if ($LASTEXITCODE -ne 0) { return $false }
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App', 'Microsoft.WindowsDesktop.App')) {
        if (-not ($runtimes | Where-Object { $_ -match "^$([regex]::Escape($framework))\s+10\.0\.\d+\s+" })) { return $false }
    }
    return $true
}

function Resolve-SystemPython312 {
    $launcher = Get-Command py.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $launcher) { return $null }
    $lines = @(& $launcher.Source -3.12 -s -c 'import platform,ssl,sqlite3,venv,sys; assert platform.architecture()[0] == "64bit"; print(sys.prefix)' 2>$null)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0) { return $null }
    $root = [string]$lines[-1]
    if (-not (Test-Path -LiteralPath (Join-Path $root 'python.exe') -PathType Leaf)) { return $null }
    return [IO.Path]::GetFullPath($root)
}

function Resolve-SystemAdbRoot {
    $candidates = [Collections.Generic.List[string]]::new()
    $adb = Get-Command adb.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $adb) { $candidates.Add((Split-Path -Parent $adb.Source)) }
    foreach ($name in @('ANDROID_SDK_ROOT', 'ANDROID_HOME')) {
        $value = [Environment]::GetEnvironmentVariable($name, 'Process')
        if (-not [string]::IsNullOrWhiteSpace($value)) { $candidates.Add((Join-Path $value 'platform-tools')) }
    }
    $candidates.Add((Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools'))
    $candidates.Add((Join-Path $env:USERPROFILE 'AppData\Local\Android\Sdk\platform-tools'))
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'adb.exe') -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    return $null
}

function Resolve-DefaultDownloadBaseUrl {
    param([string]$ChannelName, [string]$ReleaseVersion)

    $repository = 'dqtz5vpvj9-create/MyPowerTools'
    if ($ChannelName -eq 'stable') {
        return "https://github.com/$repository/releases/download/v$ReleaseVersion"
    }
    if ($ChannelName -eq 'local') {
        throw 'Local installers require -DownloadBaseUrl.'
    }

    $releases = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$repository/releases?per_page=30" `
        -UseBasicParsing `
        -Headers @{ 'User-Agent' = 'MyPowerTools-Web-Setup'; Accept = 'application/vnd.github+json' }
    $assetName = [string]$manifest.core.asset
    foreach ($release in @($releases)) {
        if ([string]$release.tag_name -notlike "nightly-$ReleaseVersion-*") { continue }
        $asset = @($release.assets) |
            Where-Object { [string]$_.name -eq $assetName } |
            Select-Object -First 1
        if ($null -ne $asset -and -not [string]::IsNullOrWhiteSpace([string]$asset.browser_download_url)) {
            return ([string]$asset.browser_download_url).Substring(
                0,
                ([string]$asset.browser_download_url).LastIndexOf('/'))
        }
    }
    throw "No nightly release publishes $assetName for version $ReleaseVersion."
}

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "Runtime component manifest is missing: $ManifestPath" }
$manifestJson = [IO.File]::ReadAllText([IO.Path]::GetFullPath($ManifestPath), [Text.UTF8Encoding]::new($false))
$manifest = $manifestJson | ConvertFrom-Json
if ([string]$manifest.version -ne $Version -or [string]$manifest.architecture -ne 'x64') {
    throw 'Runtime component manifest version or architecture does not match this installer.'
}
if (Test-Path -LiteralPath $SignaturePath -PathType Leaf) {
    if (-not (Test-Path -LiteralPath $PublicKeyPath -PathType Leaf)) { throw 'Signed component manifest has no public key.' }
    Add-Type -Path (Join-Path $PSScriptRoot 'ed25519.cs')
    $keyHex = ([IO.File]::ReadAllText($PublicKeyPath)).Trim()
    if ($keyHex -notmatch '^[0-9a-fA-F]{64}$') { throw 'Component manifest public key is invalid.' }
    $key = [byte[]]::new(32)
    for ($index = 0; $index -lt 32; $index++) { $key[$index] = [Convert]::ToByte($keyHex.Substring($index * 2, 2), 16) }
    $signature = [Convert]::FromBase64String(([IO.File]::ReadAllText($SignaturePath)).Trim())
    if (-not [Mpt.Ed25519]::Verify([Text.Encoding]::UTF8.GetBytes($manifestJson), $signature, $key)) {
        throw 'Runtime component manifest signature verification failed.'
    }
} elseif (-not $AllowUnsigned) {
    throw 'Runtime component manifest is unsigned.'
}

if ([string]::IsNullOrWhiteSpace($DownloadBaseUrl)) {
    $DownloadBaseUrl = Resolve-DefaultDownloadBaseUrl -ChannelName $Channel -ReleaseVersion $Version
}

$cacheRoot = Join-Path $DataRoot "installer-cache\$Version"
$assemblyRoot = Join-Path $cacheRoot 'assembly'
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
if (Test-Path -LiteralPath $assemblyRoot) { Remove-Item -LiteralPath $assemblyRoot -Recurse -Force }
New-Item -ItemType Directory -Path $assemblyRoot -Force | Out-Null

$requiredBytes = [long]$manifest.core.size + [long](($manifest.components | Measure-Object -Property size -Sum).Sum)
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($InstallDir)))
if ($drive.AvailableFreeSpace -lt ($requiredBytes * 3)) { throw 'Insufficient disk space for download, staging, and rollback.' }

Expand-VerifiedArchive -Record $manifest.core -BaseUrl $DownloadBaseUrl -CacheRoot $cacheRoot -Destination $assemblyRoot
$componentById = @{}
foreach ($component in @($manifest.components)) { $componentById[[string]$component.id] = $component }
$sources = [ordered]@{}

if (Test-SystemDotNet10X64) {
    $sources.dotnet = 'system'
} else {
    Expand-VerifiedArchive -Record $componentById['dotnet-10-x64'] -BaseUrl $DownloadBaseUrl -CacheRoot $cacheRoot -Destination $assemblyRoot
    $sources.dotnet = 'private-download'
}

$systemPython = Resolve-SystemPython312
if ($null -ne $systemPython) {
    $pythonDestination = Join-Path $assemblyRoot 'Runtimes\Python312'
    New-Item -ItemType Directory -Path $pythonDestination -Force | Out-Null
    $robocopy = (Get-Command robocopy.exe -CommandType Application -ErrorAction Stop).Source
    & $robocopy $systemPython $pythonDestination /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /XJ /XD (Join-Path $systemPython 'Lib\site-packages') /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -ge 8) { throw "Could not reuse system Python (robocopy $LASTEXITCODE)." }
    $sources.python = 'system-copy'
} else {
    Expand-VerifiedArchive -Record $componentById['python-3.12-x64'] -BaseUrl $DownloadBaseUrl -CacheRoot $cacheRoot -Destination $assemblyRoot
    $sources.python = 'private-download'
}
Expand-VerifiedArchive -Record $componentById['smartbird-dependencies'] -BaseUrl $DownloadBaseUrl -CacheRoot $cacheRoot -Destination $assemblyRoot
$sources.smartbird = 'private-download'
Expand-VerifiedArchive -Record $componentById['doubao-dependencies'] -BaseUrl $DownloadBaseUrl -CacheRoot $cacheRoot -Destination $assemblyRoot
$sources.doubao = 'private-download'

$systemAdb = Resolve-SystemAdbRoot
if ($null -ne $systemAdb) {
    $adbDestination = Join-Path $assemblyRoot 'Tools\AndroidPlatformTools'
    New-Item -ItemType Directory -Path $adbDestination -Force | Out-Null
    foreach ($name in @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'libwinpthread-1.dll')) {
        $source = Join-Path $systemAdb $name
        if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination $adbDestination -Force }
    }
    $sources.adb = 'system-copy'
} else {
    Expand-VerifiedArchive -Record $componentById['android-platform-tools'] -BaseUrl $DownloadBaseUrl -CacheRoot $cacheRoot -Destination $assemblyRoot
    $sources.adb = 'private-download'
}

$python = Join-Path $assemblyRoot 'Runtimes\Python312\python.exe'
& $python -s -c 'import ssl, sqlite3, venv, flask, psutil, win32gui, pywinauto'
if ($LASTEXITCODE -ne 0) { throw 'Prepared Python runtime failed its health check.' }
if (-not (Test-SystemDotNet10X64) -and -not (Test-Path -LiteralPath (Join-Path $assemblyRoot 'Runtime\dotnet\host\fxr') -PathType Container)) {
    throw 'Prepared private .NET runtime failed its health check.'
}
if (-not (Test-Path -LiteralPath (Join-Path $assemblyRoot 'Tools\AndroidPlatformTools\adb.exe') -PathType Leaf)) {
    throw 'Prepared ADB runtime failed its health check.'
}

if ($DryRun) {
    & (Join-Path $assemblyRoot 'install-windows.ps1') -PackageRoot $assemblyRoot -InstallDir $InstallDir -DataRoot $DataRoot -NoStartRunner -NoOpenApp -DryRun
    [pscustomobject]@{ success = $true; dryRun = $true; version = $Version; runtimeSources = $sources }
    return
}

& (Join-Path $assemblyRoot 'install-windows.ps1') -PackageRoot $assemblyRoot -InstallDir $InstallDir -DataRoot $DataRoot -NoStartRunner -NoOpenApp
$installManifestPath = Join-Path $InstallDir 'install.manifest.json'
$installManifest = Get-Content -LiteralPath $installManifestPath -Raw | ConvertFrom-Json
$installManifest | Add-Member -NotePropertyName runtimeComponents -NotePropertyValue $sources -Force
$installManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $installManifestPath -Encoding UTF8

if (-not $NoOpenApp) {
    Start-Process -FilePath (Join-Path $InstallDir 'MyPowerTools.exe') -ArgumentList @('--data-root', $DataRoot)
}

[pscustomobject]@{ success = $true; version = $Version; installDir = $InstallDir; runtimeSources = $sources }
