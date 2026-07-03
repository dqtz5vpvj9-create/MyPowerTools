param(
    [string]$PackageRoot = $PSScriptRoot,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [switch]$NoStartMenuShortcut,
    [switch]$DesktopShortcut,
    [switch]$EnableAutostart,
    [switch]$StartRunner,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function Test-IsInsidePath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFull = Resolve-FullPath $Parent
    $childFull = Resolve-FullPath $Child
    if (-not $parentFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $parentFull = $parentFull + [System.IO.Path]::DirectorySeparatorChar
    }

    return $childFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-RequiredPackageContent {
    param([Parameter(Mandatory = $true)][string]$Root)

    $required = @(
        'Runner\MyPowerTools.Runner.exe',
        'Shell\MyPowerTools.Shell.Avalonia.exe',
        'Cli\MyPowerTools.Cli.exe',
        'modules',
        'schemas',
        'ui'
    )

    foreach ($relative in $required) {
        $path = Join-Path $Root $relative
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Portable package is missing $relative at $Root"
        }
    }
}

function New-Shortcut {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [string]$Arguments = '',
        [string]$Description = ''
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.Arguments = $Arguments
    $shortcut.Description = $Description
    $shortcut.Save()
}

function Stop-InstalledProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [switch]$DryRun
    )

    $processNames = @(
        'MyPowerTools.Runner',
        'MyPowerTools.Shell.Avalonia',
        'MyPowerTools.Cli'
    )

    foreach ($name in $processNames) {
        foreach ($process in Get-Process -Name $name -ErrorAction SilentlyContinue) {
            $path = $null
            try {
                $path = $process.MainModule.FileName
            } catch {
                continue
            }

            if ($path -and (Test-IsInsidePath -Parent $Root -Child $path)) {
                if ($DryRun) {
                    Write-Host "Would stop $name ($($process.Id))"
                    continue
                }

                if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                    $null = $process.CloseMainWindow()
                    if ($process.WaitForExit(3000)) {
                        continue
                    }
                }

                Stop-Process -Id $process.Id -Force
            }
        }
    }
}

function Start-RunnerHidden {
    param(
        [Parameter(Mandatory = $true)][string]$RunnerExe,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$ModulesRoot,
        [Parameter(Mandatory = $true)][string]$DataRoot
    )

    $processStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processStartInfo.FileName = $RunnerExe
    $processStartInfo.WorkingDirectory = $WorkingDirectory
    $processStartInfo.UseShellExecute = $false
    $processStartInfo.CreateNoWindow = $true

    foreach ($argument in @('--modules', $ModulesRoot, '--data-root', $DataRoot)) {
        $processStartInfo.ArgumentList.Add($argument)
    }

    [System.Diagnostics.Process]::Start($processStartInfo) | Out-Null
}

$PackageRootFull = Resolve-FullPath $PackageRoot
$InstallDirFull = Resolve-FullPath $InstallDir
$DataRootFull = Resolve-FullPath $DataRoot

Assert-RequiredPackageContent -Root $PackageRootFull

if ($InstallDirFull.Equals($PackageRootFull, [System.StringComparison]::OrdinalIgnoreCase) -or
    (Test-IsInsidePath -Parent $PackageRootFull -Child $InstallDirFull)) {
    throw "InstallDir must be outside PackageRoot. PackageRoot=$PackageRootFull InstallDir=$InstallDirFull"
}

$runnerExe = Join-Path $InstallDirFull 'Runner\MyPowerTools.Runner.exe'
$shellExe = Join-Path $InstallDirFull 'Shell\MyPowerTools.Shell.Avalonia.exe'
$cliExe = Join-Path $InstallDirFull 'Cli\MyPowerTools.Cli.exe'
$runnerArguments = "--modules `"$InstallDirFull\modules`" --data-root `"$DataRootFull`""
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\MyPowerTools'
$desktopShortcutPath = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'MyPowerTools.lnk'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$installManifestPath = Join-Path $InstallDirFull 'install.manifest.json'

$plan = [ordered]@{
    packageRoot = $PackageRootFull
    installDir = $InstallDirFull
    dataRoot = $DataRootFull
    createStartMenuShortcut = -not $NoStartMenuShortcut.IsPresent
    createDesktopShortcut = $DesktopShortcut.IsPresent
    enableAutostart = $EnableAutostart.IsPresent
    startRunner = $StartRunner.IsPresent
}

if ($DryRun) {
    $plan | ConvertTo-Json -Depth 4
    return
}

$installParent = Split-Path -Parent $InstallDirFull
New-Item -ItemType Directory -Path $installParent -Force | Out-Null
New-Item -ItemType Directory -Path $DataRootFull -Force | Out-Null

$stagingDir = Join-Path $installParent ("MyPowerTools.__staging__." + [Guid]::NewGuid().ToString('N'))
$backupDir = Join-Path $installParent ("MyPowerTools.__backup__." + (Get-Date -Format 'yyyyMMddHHmmss'))

try {
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Get-ChildItem -LiteralPath $PackageRootFull -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stagingDir -Recurse -Force
    }

    Stop-InstalledProcess -Root $InstallDirFull

    if (Test-Path -LiteralPath $InstallDirFull) {
        Move-Item -LiteralPath $InstallDirFull -Destination $backupDir
    }

    Move-Item -LiteralPath $stagingDir -Destination $InstallDirFull

    $manifest = [ordered]@{
        product = 'MyPowerTools'
        version = '0.2.0'
        installedAt = (Get-Date).ToString('O')
        packageRoot = $PackageRootFull
        installDir = $InstallDirFull
        dataRoot = $DataRootFull
        runner = $runnerExe
        shell = $shellExe
        cli = $cliExe
        autostart = $EnableAutostart.IsPresent
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $installManifestPath -Encoding UTF8

    if (-not $NoStartMenuShortcut.IsPresent) {
        New-Shortcut -Path (Join-Path $startMenuDir 'MyPowerTools Shell.lnk') -TargetPath $shellExe -WorkingDirectory $InstallDirFull -Description 'Open MyPowerTools Shell'
        New-Shortcut -Path (Join-Path $startMenuDir 'MyPowerTools Runner.lnk') -TargetPath $runnerExe -WorkingDirectory $InstallDirFull -Arguments $runnerArguments -Description 'Start MyPowerTools Runner'
        New-Shortcut -Path (Join-Path $startMenuDir 'MyPowerTools CLI.lnk') -TargetPath $cliExe -WorkingDirectory $InstallDirFull -Description 'Open MyPowerTools CLI'
    }

    if ($DesktopShortcut) {
        New-Shortcut -Path $desktopShortcutPath -TargetPath $shellExe -WorkingDirectory $InstallDirFull -Description 'Open MyPowerTools Shell'
    }

    if ($EnableAutostart) {
        New-Item -Path $runKeyPath -Force | Out-Null
        Set-ItemProperty -Path $runKeyPath -Name 'MyPowerTools' -Value "`"$runnerExe`" $runnerArguments"
    }

    if ($StartRunner) {
        Start-RunnerHidden -RunnerExe $runnerExe -WorkingDirectory $InstallDirFull -ModulesRoot (Join-Path $InstallDirFull 'modules') -DataRoot $DataRootFull
    }

    if (Test-Path -LiteralPath $backupDir) {
        Remove-Item -LiteralPath $backupDir -Recurse -Force
    }
} catch {
    if ((Test-Path -LiteralPath $backupDir) -and -not (Test-Path -LiteralPath $InstallDirFull)) {
        Move-Item -LiteralPath $backupDir -Destination $InstallDirFull
    }

    throw
} finally {
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force
    }
}

Write-Host "Installed MyPowerTools to $InstallDirFull"
