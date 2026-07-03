param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [switch]$RemoveData,
    [switch]$Force,
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

function Assert-SafeMyPowerToolsPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$Force
    )

    $full = Resolve-FullPath $Path
    $leaf = Split-Path -Leaf $full
    if ($leaf -ne 'MyPowerTools' -and -not $Force) {
        throw "Refusing to remove $full because its leaf directory is not MyPowerTools. Pass -Force for an explicit custom path."
    }

    $root = [System.IO.Path]::GetPathRoot($full)
    if ($full.Equals($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove filesystem root $full"
    }
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

$InstallDirFull = Resolve-FullPath $InstallDir
$DataRootFull = Resolve-FullPath $DataRoot
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\MyPowerTools'
$desktopShortcutPath = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'MyPowerTools.lnk'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Assert-SafeMyPowerToolsPath -Path $InstallDirFull -Force:$Force
if ($RemoveData) {
    Assert-SafeMyPowerToolsPath -Path $DataRootFull -Force:$Force
}

$plan = [ordered]@{
    installDir = $InstallDirFull
    dataRoot = $DataRootFull
    removeData = $RemoveData.IsPresent
    removeStartMenuShortcuts = $startMenuDir
    removeDesktopShortcut = $desktopShortcutPath
    removeAutostartValue = 'HKCU Run: MyPowerTools'
}

if ($DryRun) {
    $plan | ConvertTo-Json -Depth 4
    return
}

Stop-InstalledProcess -Root $InstallDirFull

if (Test-Path -LiteralPath $runKeyPath) {
    $currentValue = $null
    try {
        $currentValue = (Get-ItemProperty -Path $runKeyPath -Name 'MyPowerTools' -ErrorAction Stop).MyPowerTools
    } catch {
        $currentValue = $null
    }

    if ($currentValue -and $currentValue.Contains($InstallDirFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-ItemProperty -Path $runKeyPath -Name 'MyPowerTools' -ErrorAction SilentlyContinue
    }
}

if (Test-Path -LiteralPath $startMenuDir) {
    Remove-Item -LiteralPath $startMenuDir -Recurse -Force
}

if (Test-Path -LiteralPath $desktopShortcutPath) {
    Remove-Item -LiteralPath $desktopShortcutPath -Force
}

if (Test-Path -LiteralPath $InstallDirFull) {
    Remove-Item -LiteralPath $InstallDirFull -Recurse -Force
}

if ($RemoveData -and (Test-Path -LiteralPath $DataRootFull)) {
    Remove-Item -LiteralPath $DataRootFull -Recurse -Force
}

Write-Host "Uninstalled MyPowerTools from $InstallDirFull"
