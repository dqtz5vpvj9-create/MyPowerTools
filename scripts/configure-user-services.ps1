[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall')]
    [string]$Mode = 'Install',
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [switch]$RegisterOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime-environment.ps1')

function Invoke-NativeQuiet {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 30
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = ($ArgumentList |
        ForEach-Object { ConvertTo-WindowsCommandLineArgument -Value $_ }) -join ' '
    $process = $null
    try {
        $process = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            return 1
        }
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch {}
            [void]$process.WaitForExit(2000)
            return 124
        }
        [void]$outputTask.GetAwaiter().GetResult()
        [void]$errorTask.GetAwaiter().GetResult()
        return [int]$process.ExitCode
    }
    catch {
        return 1
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
    }
}

function Write-TextFileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content
    )

    # The ServiceManager reloads unit manifests while this script rewrites them, and a reader that
    # catches a half-written file treats the unit as uninstalled and stops it. Staging in the same
    # directory keeps the rename on one volume, so the target only ever holds a complete file.
    $temporary = "$Path.tmp"
    Set-Content -LiteralPath $temporary -Value $Content -Encoding UTF8 -NoNewline
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Resolve-ManagedUnitIds {
    param([Parameter(Mandatory = $true)][string]$StatePath)

    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        return @()
    }

    try {
        return @((Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json).unitIds |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    }
    catch {
        return @()
    }
}

function ConvertTo-WindowsCommandLineArgument {
    param([AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [Text.StringBuilder]::new('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$builder.Append(('\' * $backslashes))
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashes -gt 0) {
        [void]$builder.Append(('\' * ($backslashes * 2)))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Test-IsInsidePath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $childFull = [IO.Path]::GetFullPath($Child)
    return $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)
}

function Stop-ManagedProcessByState {
    param(
        [Parameter(Mandatory = $true)][string]$UnitId,
        [Parameter(Mandatory = $true)][string]$DataRootFull,
        [Parameter(Mandatory = $true)][string[]]$AllowedRoots
    )

    $runtimeStatePath = Join-Path $DataRootFull "state\units\$UnitId.json"
    if (-not (Test-Path -LiteralPath $runtimeStatePath -PathType Leaf)) {
        return
    }
    try {
        $runtimeState = Get-Content -LiteralPath $runtimeStatePath -Raw | ConvertFrom-Json
        $process = Get-Process -Id ([int]$runtimeState.Pid) -ErrorAction SilentlyContinue
        if ($null -ne $process) {
            $path = $null
            try { $path = $process.MainModule.FileName } catch {}
            if ($path -and ($AllowedRoots | Where-Object { Test-IsInsidePath -Parent $_ -Child $path })) {
                if (-not $process.WaitForExit(5000)) {
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
                }
            }
        }
    }
    catch {
    }
    Remove-Item -LiteralPath $runtimeStatePath -Force -ErrorAction SilentlyContinue
}

function Stop-ManagedProcessesUnderRoots {
    param([Parameter(Mandatory = $true)][string[]]$AllowedRoots)

    $targets = [System.Collections.Generic.List[Diagnostics.Process]]::new()
    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        $path = $null
        try { $path = $process.MainModule.FileName } catch {}
        if ($path -and ($AllowedRoots | Where-Object { Test-IsInsidePath -Parent $_ -Child $path })) {
            $targets.Add($process)
        }
    }

    foreach ($process in $targets) {
        try {
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
            }
        }
        catch {
        }
        finally {
            $process.Dispose()
        }
    }
}

function Deploy-UnitManifests {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadRoot,
        [Parameter(Mandatory = $true)][string]$UnitsRoot,
        [Parameter(Mandatory = $true)][string]$DataRootFull,
        [Parameter(Mandatory = $true)][string]$InstallRootFull,
        [Parameter(Mandatory = $true)][string]$StatePath
    )

    if (-not (Test-Path -LiteralPath $PayloadRoot -PathType Container)) {
        throw "Installed package has no service-units payload: $PayloadRoot"
    }

    New-Item -ItemType Directory -Path $UnitsRoot -Force | Out-Null
    $previousIds = Resolve-ManagedUnitIds -StatePath $StatePath
    $records = [System.Collections.Generic.List[object]]::new()

    foreach ($unitDirectory in Get-ChildItem -LiteralPath $PayloadRoot -Directory | Sort-Object Name) {
        $templatePath = Join-Path $unitDirectory.FullName 'unit-manifest.json'
        if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
            throw "Service Unit template is missing: $templatePath"
        }

        $manifest = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json
        $unitId = [string]$manifest.id
        if ($unitId -notmatch '^[A-Za-z0-9_.-]+$' -or
            -not [string]::Equals($unitId, $unitDirectory.Name, [StringComparison]::Ordinal)) {
            throw "Service Unit id does not match its package directory: $unitId"
        }

        $binRoot = [IO.Path]::GetFullPath((Join-Path $unitDirectory.FullName 'bin'))
        $execName = [IO.Path]::GetFileName([string]$manifest.exec)
        $resolvedExec = [IO.Path]::GetFullPath((Join-Path $binRoot $execName))
        if (-not (Test-Path -LiteralPath $resolvedExec -PathType Leaf)) {
            throw "Service Unit executable is missing: $resolvedExec"
        }

        $manifest.exec = $resolvedExec
        $manifest.workingDirectory = $binRoot
        $toolDataRoot = Join-Path $DataRootFull "state\tools\$([string]$manifest.toolId)"
        $environment = [ordered]@{}
        if ($null -ne $manifest.environment) {
            foreach ($property in $manifest.environment.PSObject.Properties) {
                $environment[$property.Name] = [Environment]::ExpandEnvironmentVariables([string]$property.Value)
            }
        }
        $environment['MPT_DATA_ROOT'] = $DataRootFull
        $environment['MPT_TOOL_DATA_ROOT'] = $toolDataRoot
        $environment['MPT_INSTALL_ROOT'] = $InstallRootFull
        if (-not [string]::IsNullOrWhiteSpace($dotnetRoot)) {
            $environment['DOTNET_ROOT'] = $dotnetRoot
        } else {
            [void]$environment.Remove('DOTNET_ROOT')
        }
        $manifest.environment = $environment
        $manifest.dataRoots = @($toolDataRoot)

        $arguments = @($manifest.arguments)
        for ($index = 0; $index -lt $arguments.Count; $index++) {
            if ([string]$arguments[$index] -eq '--heartbeat-file' -and $index + 1 -lt $arguments.Count) {
                $arguments[$index + 1] = Join-Path $DataRootFull "state\$unitId.heartbeat"
                $index++
            }
        }
        $manifest.arguments = $arguments

        Write-TextFileAtomically `
            -Path (Join-Path $UnitsRoot "$unitId.json") `
            -Content ($manifest | ConvertTo-Json -Depth 8)
        $records.Add([pscustomobject]@{
            UnitId = $unitId
            Autostart = [bool]$manifest.autostart
        })
    }

    $currentIds = @($records | ForEach-Object UnitId)
    foreach ($staleId in $previousIds | Where-Object { $_ -notin $currentIds }) {
        $staleManifest = Join-Path $UnitsRoot "$staleId.json"
        if (Test-Path -LiteralPath $staleManifest -PathType Leaf) {
            Remove-Item -LiteralPath $staleManifest -Force
        }
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $StatePath) -Force | Out-Null
    [ordered]@{
        unitIds = $currentIds
        updatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $StatePath -Encoding UTF8
    return $records.ToArray()
}

$installRootFull = [IO.Path]::GetFullPath($InstallRoot)
$canonicalInstallRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'))
$dataRootFull = [IO.Path]::GetFullPath($DataRoot)
if (-not $installRootFull.Equals($canonicalInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "MyPowerTools user services require the canonical install root $canonicalInstallRoot. InstallRoot=$installRootFull"
}

$cli = Join-Path $installRootFull 'Cli\MyPowerTools.Cli.exe'
$manager = Join-Path $installRootFull 'ServiceManager\MyPowerTools.ServiceManager.exe'
$deployRoot = Join-Path $dataRootFull 'ServiceManager'
$unitsRoot = Join-Path $deployRoot 'units'
$statePath = Join-Path $dataRootFull 'state\installed-service-units.json'
$serviceManagerRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$serviceManagerRunName = 'MyPowerTools.ServiceManager'
$currentSessionId = (Get-Process -Id $PID).SessionId
$savedDataRoot = [Environment]::GetEnvironmentVariable('MPT_DATA_ROOT', 'Process')
[Environment]::SetEnvironmentVariable('MPT_DATA_ROOT', $dataRootFull, 'Process')
$runtimeResolution = Set-MyPowerToolsProcessDotNetRoot -InstallRoot $installRootFull
$dotnetRoot = [string]$runtimeResolution.root

try {
    if ($Mode -eq 'Uninstall') {
        $managedIds = Resolve-ManagedUnitIds -StatePath $statePath
        $legacyVersionsRoot = [IO.Path]::GetFullPath((Join-Path $dataRootFull 'ServiceManager\versions'))
        $allowedUnitRoots = @(
            (Join-Path $installRootFull 'service-units'),
            $legacyVersionsRoot
        )
        foreach ($unitId in $managedIds) {
            if ($currentSessionId -ne 0 -and (Test-Path -LiteralPath $cli -PathType Leaf)) {
                [void](Invoke-NativeQuiet -FilePath $cli -ArgumentList @('service', 'stop', [string]$unitId) -TimeoutSeconds 20)
            }
            Stop-ManagedProcessByState `
                -UnitId ([string]$unitId) `
                -DataRootFull $dataRootFull `
                -AllowedRoots $allowedUnitRoots
        }
        if ($currentSessionId -ne 0 -and (Test-Path -LiteralPath $cli -PathType Leaf)) {
            [void](Invoke-NativeQuiet -FilePath $cli -ArgumentList @('service', 'shutdown') -TimeoutSeconds 15)
        }

        # State can be stale or missing after an interrupted upgrade. The roots are resolved
        # and constrained above, so clean up any surviving managed process from either layout.
        Stop-ManagedProcessesUnderRoots -AllowedRoots $allowedUnitRoots
        if ((Test-IsInsidePath -Parent (Join-Path $dataRootFull 'ServiceManager') -Child $legacyVersionsRoot) -and
            (Test-Path -LiteralPath $legacyVersionsRoot -PathType Container)) {
            Remove-Item -LiteralPath $legacyVersionsRoot -Recurse -Force
        }

        if (Test-Path -LiteralPath $serviceManagerRunKey) {
            Remove-ItemProperty `
                -LiteralPath $serviceManagerRunKey `
                -Name $serviceManagerRunName `
                -ErrorAction SilentlyContinue
        }
        foreach ($managerProcess in Get-Process -Name 'MyPowerTools.ServiceManager' -ErrorAction SilentlyContinue) {
            $managerPath = $null
            try { $managerPath = $managerProcess.MainModule.FileName } catch {}
            if ($managerPath -and (Test-IsInsidePath -Parent $installRootFull -Child $managerPath)) {
                if (-not $managerProcess.WaitForExit(5000)) {
                    Stop-Process -Id $managerProcess.Id -Force -ErrorAction SilentlyContinue
                    Wait-Process -Id $managerProcess.Id -Timeout 5 -ErrorAction SilentlyContinue
                }
            }
        }

        foreach ($unitId in $managedIds) {
            $manifestPath = Join-Path $unitsRoot "$unitId.json"
            if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
                Remove-Item -LiteralPath $manifestPath -Force
            }
        }
        if (Test-Path -LiteralPath $statePath -PathType Leaf) {
            Remove-Item -LiteralPath $statePath -Force
        }
        $protocolKey = 'HKCU:\Software\Classes\mypowertools'
        if (Test-Path -LiteralPath $protocolKey) {
            Remove-Item -LiteralPath $protocolKey -Recurse -Force
        }
        Unregister-ScheduledTask `
            -TaskName 'MyPowerTools OTA Check' `
            -Confirm:$false `
            -ErrorAction SilentlyContinue

        [ordered]@{ mode = $Mode; units = @($managedIds); dataRoot = $dataRootFull } |
            ConvertTo-Json -Depth 4
        return
    }

    foreach ($requiredPath in @($cli, $manager)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Installed user service component is missing: $requiredPath"
        }
    }

    New-Item -ItemType Directory -Path $dataRootFull, $deployRoot -Force | Out-Null
    $records = @(Deploy-UnitManifests `
        -PayloadRoot (Join-Path $installRootFull 'service-units') `
        -UnitsRoot $unitsRoot `
        -DataRootFull $dataRootFull `
        -InstallRootFull $installRootFull `
        -StatePath $statePath)

    if (-not (Test-Path -LiteralPath $serviceManagerRunKey)) {
        New-Item -Path $serviceManagerRunKey -Force | Out-Null
    }
    $managerCommand = "`"$manager`" --headless --data-root `"$dataRootFull`""
    Set-ItemProperty `
        -LiteralPath $serviceManagerRunKey `
        -Name $serviceManagerRunName `
        -Value $managerCommand

    $otaScript = Join-Path $installRootFull 'ota-update.ps1'
    $pwshCommand = Get-Command 'pwsh.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ((Test-Path -LiteralPath $otaScript -PathType Leaf) -and $pwshCommand) {
        try {
            $otaCommand = 'Check'
            $updatePolicyPath = Join-Path $dataRootFull 'ota-state\update-policy.json'
            if (Test-Path -LiteralPath $updatePolicyPath -PathType Leaf) {
                try {
                    $updatePolicy = Get-Content -LiteralPath $updatePolicyPath -Raw | ConvertFrom-Json
                    if ([bool]$updatePolicy.autoApply) {
                        $otaCommand = 'Apply'
                    }
                }
                catch {
                    Write-Warning "OTA update policy could not be read: $($_.Exception.Message)"
                }
            }
            Unregister-ScheduledTask `
                -TaskName 'MyPowerTools OTA Check' `
                -Confirm:$false `
                -ErrorAction SilentlyContinue
            $otaTaskAction = New-ScheduledTaskAction `
                -Execute $pwshCommand.Source `
                -Argument ('-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File "{0}" -Command {1}' -f $otaScript, $otaCommand) `
                -WorkingDirectory $installRootFull
            $otaTaskTrigger = New-ScheduledTaskTrigger -Daily -At 03:00
            $otaTaskPrincipal = New-ScheduledTaskPrincipal `
                -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) `
                -LogonType Interactive `
                -RunLevel Limited
            $otaTaskSettings = New-ScheduledTaskSettingsSet `
                -StartWhenAvailable `
                -AllowStartIfOnBatteries `
                -DontStopIfGoingOnBatteries `
                -ExecutionTimeLimit (New-TimeSpan -Hours 2)
            Register-ScheduledTask `
                -TaskName 'MyPowerTools OTA Check' `
                -Action $otaTaskAction `
                -Trigger $otaTaskTrigger `
                -Principal $otaTaskPrincipal `
                -Settings $otaTaskSettings `
                -Description "Daily MyPowerTools OTA $otaCommand" `
                -Force | Out-Null
        }
        catch {
            Write-Warning "MyPowerTools OTA Check task registration skipped: $($_.Exception.Message)"
        }
    } elseif (-not $pwshCommand) {
        Write-Warning 'pwsh.exe was not found; the daily MyPowerTools OTA check task was skipped.'
    }

    if ($RegisterOnly) {
        [ordered]@{
            mode = $Mode
            registerOnly = $true
            installRoot = $installRootFull
            dataRoot = $dataRootFull
            manager = $manager
            units = @($records | ForEach-Object UnitId)
            activeUnits = @()
        } | ConvertTo-Json -Depth 5
        return
    }

    if ($currentSessionId -eq 0) {
        throw 'MyPowerTools runtime launch is blocked in Windows Session 0. Use -RegisterOnly or launch from an interactive user session.'
    }

    $reloadExitCode = Invoke-NativeQuiet -FilePath $cli -ArgumentList @('service', 'reload') -TimeoutSeconds 3
    if ($reloadExitCode -ne 0) {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $manager
        $startInfo.WorkingDirectory = Split-Path -Parent $manager
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.Arguments = (@('--data-root', $dataRootFull, '--deploy-root', $deployRoot) |
            ForEach-Object { ConvertTo-WindowsCommandLineArgument -Value $_ }) -join ' '
        [Diagnostics.Process]::Start($startInfo) | Out-Null
        Start-Sleep -Seconds 1
        $reloadExitCode = Invoke-NativeQuiet -FilePath $cli -ArgumentList @('service', 'reload') -TimeoutSeconds 60
    }
    if ($reloadExitCode -ne 0) {
        throw "ServiceManager did not become ready after installation. reloadExit=$reloadExitCode"
    }

    $activeUnits = [System.Collections.Generic.List[string]]::new()
    foreach ($record in $records | Where-Object Autostart) {
        $startExitCode = Invoke-NativeQuiet -FilePath $cli -ArgumentList @('service', 'start', [string]$record.UnitId) -TimeoutSeconds 30
        if ($startExitCode -ne 0) {
            throw "Service Unit activation failed: $($record.UnitId) (exit $startExitCode)"
        }
        $activeUnits.Add([string]$record.UnitId)
    }

    [ordered]@{
        mode = $Mode
        installRoot = $installRootFull
        dataRoot = $dataRootFull
        manager = $manager
        units = @($records | ForEach-Object UnitId)
        activeUnits = $activeUnits.ToArray()
    } | ConvertTo-Json -Depth 5
}
finally {
    [Environment]::SetEnvironmentVariable('MPT_DATA_ROOT', $savedDataRoot, 'Process')
}
