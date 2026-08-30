[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$TestVersion = '99.99.99'
)

$ErrorActionPreference = 'Stop'
$repoRootFull = [IO.Path]::GetFullPath($RepoRoot)
$artifactsRoot = Join-Path $repoRootFull 'artifacts\release'
$smokeRoot = Join-Path $artifactsRoot 'web-installer-smoke'
$installRoot = Join-Path $smokeRoot 'install'
$setupLog = Join-Path $smokeRoot 'setup.log'
$installerPath = Join-Path $smokeRoot "MyPowerTools-Web-Setup-$TestVersion-smoke.exe"
$cacheParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'MyPowerTools\installer-cache'))
$testCache = [IO.Path]::GetFullPath((Join-Path $cacheParent $TestVersion))

function Assert-ChildPath {
    param([string]$Parent, [string]$Child)
    $parentPrefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing filesystem mutation outside $parentPrefix (target: $childFull)"
    }
}

function Invoke-NativeWait {
    param([string]$FilePath, [string[]]$ArgumentList)
    $processInfo = [Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $FilePath
    $processInfo.UseShellExecute = $false
    foreach ($argument in $ArgumentList) { $processInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($processInfo)
    $process.WaitForExit()
    return $process.ExitCode
}

Assert-ChildPath -Parent $artifactsRoot -Child $smokeRoot
Assert-ChildPath -Parent $cacheParent -Child $testCache
if (Test-Path -LiteralPath $smokeRoot) {
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force
}
if (Test-Path -LiteralPath $testCache) {
    Remove-Item -LiteralPath $testCache -Recurse -Force
}
New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
New-Item -ItemType Directory -Path $testCache -Force | Out-Null

$coreArchive = Join-Path $artifactsRoot 'MyPowerTools-core-win-x64.zip'
$componentRoot = Join-Path $artifactsRoot 'runtime-components'
foreach ($source in @($coreArchive) + @(Get-ChildItem -LiteralPath $componentRoot -Filter '*.zip' -File | Select-Object -ExpandProperty FullName)) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Installer smoke asset is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $testCache -Force
}

try {
    $iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
    if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) { throw 'Inno Setup compiler is missing.' }
    $compileArguments = @(
        '/Qp',
        '/DMyInstallerTestMode=1',
        "/DMyAppVersion=$TestVersion",
        '/DMyReleaseChannel=local',
        '/DMyDownloadBaseUrl=https://127.0.0.1/unused',
        "/O$smokeRoot",
        "/F$([IO.Path]::GetFileNameWithoutExtension($installerPath))",
        (Join-Path $repoRootFull 'installer\MyPowerTools.Web.iss')
    )
    & $iscc @compileArguments
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }

    $setupArguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        "/DIR=$installRoot",
        "/LOG=$setupLog"
    )
    $setupExitCode = Invoke-NativeWait -FilePath $installerPath -ArgumentList $setupArguments
    if ($setupExitCode -ne 0) { throw "GUI installer smoke test failed with exit code $setupExitCode. Log: $setupLog" }

    $manifestPath = Join-Path $installRoot 'install.manifest.json'
    if (-not (Test-Path -LiteralPath (Join-Path $installRoot 'MyPowerTools.exe') -PathType Leaf)) {
        throw "GUI installer did not install MyPowerTools.exe. Log: $setupLog"
    }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'GUI installer did not write install.manifest.json.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]$manifest.distributionMode -ne 'web') {
        throw 'GUI installer manifest does not identify a web installation.'
    }
    if ([string]$manifest.version -ne $TestVersion) {
        throw 'GUI installer manifest version does not match the smoke build.'
    }
    if ([string]$manifest.runtimeComponents.smartbird -ne 'not-selected' -or
        [string]$manifest.runtimeComponents.doubao -ne 'not-selected' -or
        [string]$manifest.runtimeComponents.adb -ne 'not-selected') {
        throw 'The recommended core setup unexpectedly installed optional runtime components.'
    }

    $uninstaller = Join-Path $installRoot 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw 'GUI installer did not register an uninstaller.'
    }
    $uninstallExitCode = Invoke-NativeWait -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if ($uninstallExitCode -ne 0) { throw "GUI uninstaller smoke test failed with exit code $uninstallExitCode." }

    [pscustomobject]@{
        success = $true
        installer = $installerPath
        installerBytes = (Get-Item -LiteralPath $installerPath).Length
        setupLog = $setupLog
        distributionMode = [string]$manifest.distributionMode
        runtimeSource = [string]$manifest.runtimeSource
    }
}
finally {
    if (Test-Path -LiteralPath $testCache) {
        Assert-ChildPath -Parent $cacheParent -Child $testCache
        Remove-Item -LiteralPath $testCache -Recurse -Force
    }
}
