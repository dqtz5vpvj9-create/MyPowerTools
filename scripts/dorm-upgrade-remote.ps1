[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$StagingDir,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [string]$HashMarkerPath = '',
    [string]$InstallScriptOverride = '',
    [switch]$SkipExtract
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Read-Utf8TextFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false))
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "ASSERT: $Message"
    }
}

$ZipPath = [IO.Path]::GetFullPath($ZipPath)
$StagingDir = [IO.Path]::GetFullPath($StagingDir)
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if ([string]::IsNullOrWhiteSpace($HashMarkerPath)) {
    $HashMarkerPath = "$ZipPath.sha256"
}
$HashMarkerPath = [IO.Path]::GetFullPath($HashMarkerPath)

Assert-True -Condition (Test-Path -LiteralPath $ZipPath -PathType Leaf) -Message "ZIP is missing: $ZipPath"
Assert-True -Condition (Test-Path -LiteralPath $HashMarkerPath -PathType Leaf) -Message "SHA-256 marker is missing: $HashMarkerPath"

$expectedHash = (Read-Utf8TextFile -Path $HashMarkerPath).Trim()
$expectedHash = [regex]::Match($expectedHash, '^([0-9a-fA-F]{64})').Groups[1].Value.ToLowerInvariant()
Assert-True -Condition ($expectedHash -match '^[0-9a-f]{64}$') -Message "SHA-256 marker is malformed: $HashMarkerPath"
$actualHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-True -Condition ($actualHash -eq $expectedHash) -Message "ZIP SHA-256 mismatch. expected=$expectedHash actual=$actualHash"

if (-not $SkipExtract.IsPresent) {
    if (Test-Path -LiteralPath $StagingDir -PathType Container) {
        Remove-Item -LiteralPath $StagingDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $StagingDir)
} else {
    Assert-True -Condition (Test-Path -LiteralPath $StagingDir -PathType Container) -Message "SkipExtract requires existing staging: $StagingDir"
}

$installScript = Join-Path $StagingDir 'install-windows.ps1'
if (-not [string]::IsNullOrWhiteSpace($InstallScriptOverride)) {
    $overrideFull = [IO.Path]::GetFullPath($InstallScriptOverride)
    Assert-True -Condition (Test-Path -LiteralPath $overrideFull -PathType Leaf) -Message "Install script override is missing: $overrideFull"
    Copy-Item -LiteralPath $overrideFull -Destination $installScript -Force
}
$shippedManifest = Join-Path $StagingDir 'MyPowerTools-win-x64.manifest.json'
Assert-True -Condition (Test-Path -LiteralPath $installScript -PathType Leaf) -Message "Staging is missing install-windows.ps1"
Assert-True -Condition (Test-Path -LiteralPath $shippedManifest -PathType Leaf) -Message "Staging is missing MyPowerTools-win-x64.manifest.json"
$manifest = Get-Content -LiteralPath $shippedManifest -Raw | ConvertFrom-Json
$newVersion = [string]$manifest.version
Assert-True -Condition ($newVersion -match '^[0-9]+\.[0-9]+\.[0-9]+$') -Message "Release manifest has an invalid version '$newVersion'"

& $installScript `
    -PackageRoot $StagingDir `
    -InstallDir $InstallDir `
    -DataRoot $DataRoot `
    -NoOpenApp `
    -StartRunner
if ($LASTEXITCODE -ne 0) {
    throw "install-windows.ps1 failed with exit code $LASTEXITCODE."
}

$installManifestPath = Join-Path $InstallDir 'install.manifest.json'
Assert-True -Condition (Test-Path -LiteralPath $installManifestPath -PathType Leaf) -Message "install.manifest.json was not created"
$installManifest = Get-Content -LiteralPath $installManifestPath -Raw | ConvertFrom-Json
Assert-True -Condition ([string]$installManifest.version -eq $newVersion) -Message "Installed version $($installManifest.version) does not match release $newVersion"

$otaStateDir = Join-Path $DataRoot 'ota-state'
$installedReleasePath = Join-Path $otaStateDir 'installed-release.json'
$installedFilesManifest = Join-Path $otaStateDir 'installed-files.manifest.json'
$installedPublicKey = Join-Path $otaStateDir 'ota-signing-public-key.txt'
Assert-True -Condition (Test-Path -LiteralPath $installedReleasePath -PathType Leaf) -Message "installed-release.json was not created"
Assert-True -Condition (Test-Path -LiteralPath $installedFilesManifest -PathType Leaf) -Message "installed-files.manifest.json was not created"
Assert-True -Condition (Test-Path -LiteralPath $installedPublicKey -PathType Leaf) -Message "ota-signing-public-key.txt was not created"
$installedRelease = Get-Content -LiteralPath $installedReleasePath -Raw | ConvertFrom-Json
Assert-True -Condition ([string]$installedRelease.version -eq $newVersion) -Message "installed-release version $($installedRelease.version) does not match release $newVersion"

$installedManifestHash = (Get-FileHash -LiteralPath $installedFilesManifest -Algorithm SHA256).Hash.ToLowerInvariant()
$shippedManifestHash = (Get-FileHash -LiteralPath $shippedManifest -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-True -Condition ($installedManifestHash -eq $shippedManifestHash) -Message "installed OTA manifest differs from the release manifest"

$criticalPaths = @(
    'Runner\MyPowerTools.Runner.exe',
    'Shell\MyPowerTools.Shell.Avalonia.exe',
    'Cli\MyPowerTools.Cli.exe',
    'ServiceManager\MyPowerTools.ServiceManager.exe',
    'MyPowerTools.exe',
    'ota-update.ps1',
    'ed25519.cs',
    'package-ota-update.ps1'
)
$missing = @($criticalPaths | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $InstallDir $_) -PathType Leaf)
})
Assert-True -Condition ($missing.Count -eq 0) -Message "Critical installed files are missing: $($missing -join ', ')"

$installManifest = Get-Content -LiteralPath $installManifestPath -Raw | ConvertFrom-Json
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ([bool]$installManifest.autostart) {
    $runnerExePath = Join-Path $InstallDir 'Runner\MyPowerTools.Runner.exe'
    $modulesRootPath = Join-Path $InstallDir 'modules'
    $expectedRunner = "`"$runnerExePath`" --modules `"$modulesRootPath`" --data-root `"$DataRoot`""
    $currentRunner = (Get-ItemProperty -LiteralPath $runKeyPath -Name 'MyPowerTools' -ErrorAction SilentlyContinue).MyPowerTools
    if ([string]::IsNullOrWhiteSpace([string]$currentRunner) -or
        [string]$currentRunner -ne $expectedRunner) {
        if (-not (Test-Path -LiteralPath $runKeyPath)) {
            New-Item -Path $runKeyPath -Force | Out-Null
        }
        Set-ItemProperty -LiteralPath $runKeyPath -Name 'MyPowerTools' -Value $expectedRunner
    }
}

$healthChecks = [Collections.Generic.List[object]]::new()
foreach ($relative in $criticalPaths) {
    $exists = Test-Path -LiteralPath (Join-Path $InstallDir $relative) -PathType Leaf
    $healthChecks.Add([pscustomobject]@{
        path = $relative
        ok = $exists
        detail = if ($exists) { 'present' } else { 'missing' }
    })
}
$healthChecks.Add([pscustomobject]@{
    path = 'install.manifest.json'
    ok = $true
    detail = 'present'
})
$health = [ordered]@{
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    expectedVersion = $newVersion
    ok = $true
    failedCount = 0
    checks = $healthChecks.ToArray()
}
[IO.File]::WriteAllText(
    (Join-Path $otaStateDir 'health-check.json'),
    ($health | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))

$pythonPath = Join-Path $InstallDir 'Runtimes\Python312\python.exe'
$smartBirdRoot = Join-Path $InstallDir 'Runtimes\SmartBird'
$smartBirdData = Join-Path $DataRoot 'SmartBird'
$restoredTasks = [Collections.Generic.List[string]]::new()
if (Test-Path -LiteralPath $pythonPath -PathType Leaf) {
    $thermostatScript = Join-Path $smartBirdRoot 'scripts\install-smartbird-thermostat-task.ps1'
    if (Test-Path -LiteralPath $thermostatScript -PathType Leaf) {
        & $thermostatScript `
            -Mode Install `
            -RepoRoot $smartBirdRoot `
            -PythonPath $pythonPath `
            -DataRoot $smartBirdData `
            -StartAfterInstall | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "SmartBird thermostat task registration failed (exit $LASTEXITCODE)."
        }
        $restoredTasks.Add('SmartBirdThermostat')
    }
    $energyScript = Join-Path $smartBirdRoot 'scripts\install-energy-server-task.ps1'
    if (Test-Path -LiteralPath $energyScript -PathType Leaf) {
        & $energyScript `
            -Mode Install `
            -RepoRoot $smartBirdRoot `
            -PythonPath $pythonPath `
            -DataRoot $smartBirdData `
            -SettingsFile (Join-Path $smartBirdData 'settings.json') | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Energy Server task registration failed (exit $LASTEXITCODE)."
        }
        $restoredTasks.Add('EnergyServer')
    }

    $doubaoRoot = Join-Path $InstallDir 'Runtimes\Doubao'
    foreach ($serviceName in @('tool_server', 'mcp_server', 'planner')) {
        $venvConfig = Join-Path $doubaoRoot "$serviceName\.venv\pyvenv.cfg"
        if (-not (Test-Path -LiteralPath $venvConfig -PathType Leaf)) {
            continue
        }
        $lines = [IO.File]::ReadAllLines(
            $venvConfig,
            [Text.Encoding]::UTF8)
        $homeIndex = -1
        for ($index = 0; $index -lt $lines.Length; $index++) {
            if ($lines[$index].TrimStart().StartsWith('home = ', [StringComparison]::OrdinalIgnoreCase)) {
                $homeIndex = $index
                break
            }
        }
        $pythonHome = (Join-Path $InstallDir 'Runtimes\Python312')
        if ($homeIndex -ge 0) {
            $lines[$homeIndex] = "home = $pythonHome"
        } else {
            $lines += "home = $pythonHome"
        }
        [IO.File]::WriteAllLines($venvConfig, $lines, [Text.Encoding]::UTF8)
    }
}

[pscustomobject]@{
    success = $true
    host = $env:COMPUTERNAME
    version = $newVersion
    installDir = $InstallDir
    dataRoot = $DataRoot
    zipSha256 = $actualHash
    manifestSha256 = $shippedManifestHash
    otaStateReady = $true
    healthOk = $true
    restoredTasks = $restoredTasks.ToArray()
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json -Depth 5
