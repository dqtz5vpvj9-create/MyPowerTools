[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [switch]$RemoveData,
    [ValidateSet('Cancel', 'Restore', 'Remove')]
    [string]$NssmServiceAction = 'Cancel',
    [switch]$Force,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$runtimeEnvironmentScript = Join-Path $PSScriptRoot 'runtime-environment.ps1'
if (-not (Test-Path -LiteralPath $runtimeEnvironmentScript -PathType Leaf)) {
    $runtimeEnvironmentScript = Join-Path $InstallDir 'runtime-environment.ps1'
}
if (Test-Path -LiteralPath $runtimeEnvironmentScript -PathType Leaf) {
    . $runtimeEnvironmentScript
}

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
$protectedNssmHostRoot = [System.IO.Path]::GetFullPath((Join-Path $env:ProgramData 'MyPowerTools\ServiceHosts\nssm-manager'))
$managedNssmServices = [Collections.Generic.List[object]]::new()
$servicesRegistryRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services'
if (Test-Path -LiteralPath $servicesRegistryRoot) {
    foreach ($serviceKey in Get-ChildItem -LiteralPath $servicesRegistryRoot -ErrorAction SilentlyContinue) {
        $imagePath = [string](Get-ItemPropertyValue -LiteralPath $serviceKey.PSPath -Name 'ImagePath' -ErrorAction SilentlyContinue)
        if ($imagePath.IndexOf('nssm-manager.exe', [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            ($imagePath.IndexOf($InstallDirFull, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
             $imagePath.IndexOf($protectedNssmHostRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            $managedNssmServices.Add([ordered]@{ Name = $serviceKey.PSChildName; ImagePath = $imagePath })
        }
    }
}

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
    managedNssmServices = @($managedNssmServices)
    nssmServiceAction = $NssmServiceAction
    protectedNssmHostRoot = $protectedNssmHostRoot
}

if ($DryRun) {
    $plan | ConvertTo-Json -Depth 4
    return
}

if ($managedNssmServices.Count -gt 0) {
    if ($NssmServiceAction -eq 'Cancel') {
        $names = @($managedNssmServices | ForEach-Object { $_.Name }) -join ', '
        throw "MyPowerTools still hosts NSSM services: $names. Choose -NssmServiceAction Restore or Remove."
    }
    if (-not $isAdministrator) {
        throw 'Restoring or removing NSSM-managed Windows services requires an elevated PowerShell session.'
    }
    $scExecutable = (Get-Command 'sc.exe' -CommandType Application -ErrorAction Stop).Source
    foreach ($managedService in $managedNssmServices) {
        $serviceName = [string]$managedService.Name
        $serviceController = Get-Service -Name $serviceName -ErrorAction Stop
        if ($serviceController.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            $stopArguments = @('stop', $serviceName)
            & $scExecutable @stopArguments | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not stop NSSM-managed service '$serviceName'." }
            $serviceController.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
        }
        if ($NssmServiceAction -eq 'Remove') {
            $deleteArguments = @('delete', $serviceName)
            & $scExecutable @deleteArguments | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not remove NSSM-managed service '$serviceName'." }
            continue
        }
        $snapshotPath = Join-Path $env:ProgramData "MyPowerTools\state\tools\nssm-manager\migrations\$serviceName.json"
        if (-not (Test-Path -LiteralPath $snapshotPath -PathType Leaf)) {
            throw "Migration snapshot for '$serviceName' is missing: $snapshotPath"
        }
        $snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
        $originalImagePath = [string]$snapshot.originalImagePath
        if ([string]::IsNullOrWhiteSpace($originalImagePath)) { throw "Migration snapshot for '$serviceName' has no original ImagePath." }
        $serviceRegistryPath = Join-Path $servicesRegistryRoot $serviceName
        $configArguments = @('config', $serviceName, 'binPath=', $originalImagePath)
        & $scExecutable @configArguments | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not restore ImagePath for '$serviceName'." }
        $restoredImagePath = [string](Get-ItemPropertyValue -LiteralPath $serviceRegistryPath -Name 'ImagePath')
        if (-not [string]::Equals($restoredImagePath, $originalImagePath, [StringComparison]::Ordinal)) { throw "Restored ImagePath verification failed for '$serviceName'." }
        $snapshotState = [string]$snapshot.state
        if ($snapshotState -in @('2', '4', '5', '6', '7', 'Running', 'StartPending', 'ContinuePending', 'PausePending', 'Paused')) {
            $startArguments = @('start', $serviceName)
            & $scExecutable @startArguments | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Restored service '$serviceName' could not start with its original NSSM host." }
            $serviceController.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
            if ($snapshotState -in @('6', '7', 'PausePending', 'Paused')) {
                $pauseArguments = @('pause', $serviceName)
                & $scExecutable @pauseArguments | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "Restored service '$serviceName' could not return to its paused state." }
                $serviceController.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Paused, [TimeSpan]::FromSeconds(30))
            }
        }
    }
}

if ((Test-Path -LiteralPath $protectedNssmHostRoot) -and $isAdministrator) {
    $programDataFull = [System.IO.Path]::GetFullPath($env:ProgramData).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $protectedNssmHostRoot.StartsWith($programDataFull, [StringComparison]::OrdinalIgnoreCase) -or
        -not $protectedNssmHostRoot.EndsWith('MyPowerTools\ServiceHosts\nssm-manager', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe protected NSSM host path '$protectedNssmHostRoot'."
    }
    Remove-Item -LiteralPath $protectedNssmHostRoot -Recurse -Force
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

$inputRemapStatePath = Join-Path $DataRootFull 'state\tools\ime-manager\win-space-shift-task.json'
$inputRemapBroker = Join-Path $InstallDirFull 'Broker\MyPowerTools.ElevatedBroker.exe'
if ((Test-Path -LiteralPath $inputRemapStatePath -PathType Leaf) -and
    (Test-Path -LiteralPath $inputRemapBroker -PathType Leaf)) {
    $inputRemapStartInfo = [Diagnostics.ProcessStartInfo]::new()
    $inputRemapStartInfo.FileName = $inputRemapBroker
    $inputRemapStartInfo.Arguments = 'input-remap uninstall --data-root "{0}"' -f $DataRootFull.Replace('"', '\"')
    $inputRemapStartInfo.UseShellExecute = $true
    $inputRemapStartInfo.Verb = 'runas'
    $inputRemapStartInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $inputRemapProcess = [Diagnostics.Process]::Start($inputRemapStartInfo)
    if ($null -eq $inputRemapProcess) {
        throw 'Could not start the elevated input remap cleanup.'
    }
    $inputRemapProcess.WaitForExit()
    if ($inputRemapProcess.ExitCode -ne 0) {
        throw "Elevated input remap cleanup failed with exit code $($inputRemapProcess.ExitCode)."
    }
    $inputRemapProcess.Dispose()
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

if (Get-Command 'Clear-MyPowerToolsLegacyUserDotNetRoot' -ErrorAction SilentlyContinue) {
    [void](Clear-MyPowerToolsLegacyUserDotNetRoot -InstallRoot $InstallDirFull)
}

if (Test-Path -LiteralPath $InstallDirFull) {
    Remove-Item -LiteralPath $InstallDirFull -Recurse -Force
}

if ($RemoveData -and (Test-Path -LiteralPath $DataRootFull)) {
    Remove-Item -LiteralPath $DataRootFull -Recurse -Force
}

Write-Host "Uninstalled MyPowerTools from $InstallDirFull"
