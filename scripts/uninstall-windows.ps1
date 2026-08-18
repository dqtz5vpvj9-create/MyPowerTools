[CmdletBinding()]
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

    $alwaysStopProcessNames = @(
        'adb'
    )
    foreach ($name in $alwaysStopProcessNames) {
        foreach ($process in Get-Process -Name $name -ErrorAction SilentlyContinue) {
            if ($DryRun) {
                Write-Host "Would stop $name ($($process.Id))"
                continue
            }

            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
        }
    }

    $processNames = @(
        'MyPowerTools',
        'MyPowerTools.Runner',
        'MyPowerTools.Shell.Avalonia',
        'MyPowerTools.Cli',
        'MyPowerTools.ElevatedBroker',
        'MyPowerTools.ServiceManager'
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

    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        $path = $null
        try {
            $path = $process.MainModule.FileName
        } catch {
            continue
        }

        if (-not $path -or -not (Test-IsInsidePath -Parent $Root -Child $path)) {
            continue
        }

        if ($DryRun) {
            Write-Host "Would stop installed child process $($process.ProcessName) ($($process.Id))"
            continue
        }

        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
    }

    $rootPrefix = (Resolve-FullPath $Root).TrimEnd('\')
    $nestedMarker = $rootPrefix + '\'
    $selfId = $PID
    foreach ($proc in Get-CimInstance Win32_Process -ErrorAction SilentlyContinue) {
        if ($proc.ProcessId -eq $selfId) {
            continue
        }

        $usesRoot = $false
        $exe = [string]$proc.ExecutablePath
        if (-not [string]::IsNullOrWhiteSpace($exe)) {
            try {
                $usesRoot = Test-IsInsidePath -Parent $Root -Child $exe
            } catch {
                $usesRoot = $false
            }
        }

        $cmd = [string]$proc.CommandLine
        if (-not $usesRoot -and -not [string]::IsNullOrWhiteSpace($cmd)) {
            $usesRoot = $cmd.IndexOf($nestedMarker, [StringComparison]::OrdinalIgnoreCase) -ge 0
        }

        if (-not $usesRoot) {
            continue
        }

        if ($DryRun) {
            Write-Host "Would stop host process $($proc.Name) ($($proc.ProcessId))"
            continue
        }

        Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $proc.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
    }
}

$InstallDirFull = Resolve-FullPath $InstallDir
$DataRootFull = Resolve-FullPath $DataRoot
$programFilesFull = Resolve-FullPath $env:ProgramFiles
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
$isAdministrator = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$requiresAdministrator = Test-IsInsidePath -Parent $programFilesFull -Child $InstallDirFull
if (-not $DryRun.IsPresent -and $requiresAdministrator -and -not $isAdministrator) {
    throw "Removing MyPowerTools from $InstallDirFull requires an elevated PowerShell session."
}
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

$serviceConfigurationScript = Join-Path $InstallDirFull 'configure-user-services.ps1'
if (Test-Path -LiteralPath $serviceConfigurationScript -PathType Leaf) {
    $pwsh = Get-Command 'pwsh.exe' -CommandType Application -ErrorAction Stop |
        Select-Object -First 1
    $configurationArguments = @(
        '-NoLogo'
        '-NoProfile'
        '-NonInteractive'
        '-File'
        $serviceConfigurationScript
        '-Mode'
        'Uninstall'
        '-InstallRoot'
        $InstallDirFull
        '-DataRoot'
        $DataRootFull
    )
    & $pwsh.Source @configurationArguments
    if ($LASTEXITCODE -ne 0) {
        throw "User service shutdown failed with exit code $LASTEXITCODE."
    }
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

$userDotnetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'User')
if ($userDotnetRoot -and
    (Test-IsInsidePath -Parent $InstallDirFull -Child $userDotnetRoot)) {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $null, 'User')
}

if (Test-Path -LiteralPath $InstallDirFull) {
    Remove-Item -LiteralPath $InstallDirFull -Recurse -Force
}

if ($RemoveData -and (Test-Path -LiteralPath $DataRootFull)) {
    Remove-Item -LiteralPath $DataRootFull -Recurse -Force
}

Write-Host "Uninstalled MyPowerTools from $InstallDirFull"
