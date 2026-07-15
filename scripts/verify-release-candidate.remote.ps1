[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-f0-9]{8}$')]
    [string]$RunId,

    [Parameter(Mandatory)]
    [ValidatePattern('^MyPowerTools-A5-[a-f0-9]{8}\.zip$')]
    [string]$ArchiveName,

    [Parameter(Mandatory)]
    [string]$SuiteVersion
)

$ErrorActionPreference = 'Stop'

function Add-RemoteRecord {
    param(
        [string]$Id,
        [bool]$Passed,
        [string]$Detail
    )

    $script:remoteRecords.Add([ordered]@{
        id = $Id
        passed = $Passed
        detail = $Detail
    })
}

function Invoke-NativeCapture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$ArgumentList = @()
    )

    $savedErrorActionPreference = $ErrorActionPreference
    $nativePreference = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    if ($null -ne $nativePreference) {
        $savedNativePreference = $nativePreference.Value
        Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $false
    }

    $ErrorActionPreference = 'Continue'
    try {
        $nativeOutput = @(& $FilePath @ArgumentList 2>&1)
        $nativeExitCode = $LASTEXITCODE
    }
    catch {
        $nativeOutput = @($_.Exception.Message)
        $nativeExitCode = 1
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
        if ($null -ne $nativePreference) {
            Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $savedNativePreference
        }
    }

    return [pscustomobject]@{
        ExitCode = [int]$nativeExitCode
        Output = ($nativeOutput | Out-String)
    }
}

function Invoke-ProcessCapture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$ArgumentList = @(),

        [Parameter(Mandatory)]
        [string]$StandardOutputPath,

        [Parameter(Mandatory)]
        [string]$StandardErrorPath,

        [int]$TimeoutMs = 30000
    )

    Remove-Item -LiteralPath $StandardOutputPath, $StandardErrorPath -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WindowStyle Hidden -PassThru -RedirectStandardOutput $StandardOutputPath -RedirectStandardError $StandardErrorPath
    $timedOut = -not $process.WaitForExit($TimeoutMs)
    if ($timedOut) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        [void]$process.WaitForExit(5000)
        $exitCode = 124
    }
    else {
        [void]$process.WaitForExit()
        $exitCode = $process.ExitCode
    }

    $standardOutput = if (Test-Path -LiteralPath $StandardOutputPath) {
        Get-Content -LiteralPath $StandardOutputPath -Raw -ErrorAction SilentlyContinue
    }
    else {
        ''
    }
    $standardError = if (Test-Path -LiteralPath $StandardErrorPath) {
        Get-Content -LiteralPath $StandardErrorPath -Raw -ErrorAction SilentlyContinue
    }
    else {
        ''
    }

    $processId = $process.Id
    $process.Dispose()

    return [pscustomobject]@{
        ExitCode = [int]$exitCode
        Output = @($standardOutput, $standardError) -join [Environment]::NewLine
        TimedOut = $timedOut
        ProcessId = $processId
    }
}

function Get-ProtectedProcessSnapshots {
    param([string]$ExcludedRoot)

    $excludedRootFull = if ([string]::IsNullOrWhiteSpace($ExcludedRoot)) {
        $null
    }
    else {
        [IO.Path]::GetFullPath($ExcludedRoot).TrimEnd('\') + '\'
    }
    foreach ($role in @(
        [pscustomobject]@{ Name = 'shell'; ProcessName = 'MyPowerTools.Shell.Avalonia' },
        [pscustomobject]@{ Name = 'runner'; ProcessName = 'MyPowerTools.Runner' }
    )) {
        foreach ($process in @(Get-Process -Name $role.ProcessName -ErrorAction SilentlyContinue)) {
            try {
                $processPath = $process.Path
                if ([string]::IsNullOrWhiteSpace($processPath)) {
                    continue
                }
                $processPathFull = [IO.Path]::GetFullPath($processPath)
                if ($null -ne $excludedRootFull -and $processPathFull.StartsWith($excludedRootFull, [StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }
                [ordered]@{
                    role = $role.Name
                    id = $process.Id
                    name = $process.ProcessName
                    path = $processPathFull
                    startTime = $process.StartTime.ToString('O')
                }
            }
            finally {
                $process.Dispose()
            }
        }
    }
}

function Test-ProtectedProcessesUnchanged {
    param(
        [object[]]$Before,
        [object[]]$After
    )

    if ($Before.Count -eq 0 -or $Before.Count -ne $After.Count) {
        return $false
    }

    foreach ($beforeProcess in $Before) {
        $afterProcess = $After | Where-Object { $_.id -eq $beforeProcess.id } | Select-Object -First 1
        if ($null -eq $afterProcess) {
            return $false
        }

        if (-not [string]::Equals($beforeProcess.path, $afterProcess.path, [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }

        if (-not [string]::Equals($beforeProcess.startTime, $afterProcess.startTime, [StringComparison]::Ordinal)) {
            return $false
        }
    }

    return $true
}

$archive = Join-Path $env:USERPROFILE $ArchiveName
$testRoot = Join-Path $env:TEMP "MyPowerTools-A5-$RunId"
$stage = Join-Path $testRoot 'candidate'
$installBase = Join-Path $testRoot 'installed'
$dataRoot = Join-Path $testRoot 'data'
$testRootFull = [IO.Path]::GetFullPath($testRoot)
$tempRootFull = [IO.Path]::GetFullPath($env:TEMP)
$a5PrefixFull = [IO.Path]::GetFullPath((Join-Path $tempRootFull 'MyPowerTools-A5-'))
if (-not $testRootFull.StartsWith($a5PrefixFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing A5 work outside the expected TEMP prefix: $testRootFull"
}

$smEndpoint = "mypewertools.servicemanager.a5.$RunId"
$smInstanceName = "MyPowerTools.ServiceManager.A5.$RunId"
$unitInstanceName = "a5.$RunId"
$runnerEndpoint = "mypowertools.a5.$RunId"
$runnerInstanceName = "MyPowerTools.Runner.A5.$RunId"
$remoteRecords = [Collections.Generic.List[object]]::new()
$cleanupErrors = [Collections.Generic.List[string]]::new()
$protectedBefore = @(Get-ProtectedProcessSnapshots -ExcludedRoot $testRootFull)
$protectedAfter = @()
$installOutput = @()
$installLog = ''
$installResult = $null
$runnerOnceOutput = ''
$shellSmokeOutput = ''
$executionError = $null
$runnerProcess = $null
$cli = $null
$cliSmArguments = @('--endpoint-address', $smEndpoint)
$stoppedTestProcesses = [Collections.Generic.List[object]]::new()

try {
    $missingProtectedRoles = @('shell', 'runner') | Where-Object { $protectedBefore.role -notcontains $_ }
    if ($missingProtectedRoles.Count -gt 0) {
        throw "Protected daily MyPowerTools processes are missing before the test: $($missingProtectedRoles -join ',')."
    }

    if (Test-Path -LiteralPath $testRootFull) {
        Remove-Item -LiteralPath $testRootFull -Recurse -Force
    }

    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $stage -Force

    $installLogPath = Join-Path $testRootFull 'install.log'
    try {
        $installer = Join-Path $stage 'install.ps1'
        $installParameters = @{
            InstallBase = $installBase
            DataRoot = $dataRoot
            ServiceManagerEndpoint = $smEndpoint
            ServiceManagerInstanceName = $smInstanceName
            ServiceUnitInstanceName = $unitInstanceName
            NoLaunch = $true
        }
        $installOutput = @(& $installer @installParameters 2>&1)
    }
    catch {
        $installOutput = @($_.Exception.Message)
        throw
    }
    finally {
        $installLog = $installOutput | Out-String
        $installLog | Set-Content -LiteralPath $installLogPath -Encoding UTF8
    }

    $installResultPath = $installOutput |
        Where-Object { $_ -is [string] -and $_.EndsWith('install-result.json', [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace([string]$installResultPath)) {
        throw "Installer did not return install-result.json. Output: $installLog"
    }

    $installResult = Get-Content -LiteralPath $installResultPath -Raw | ConvertFrom-Json
    Add-RemoteRecord 'A5.R1-install-completes' (Test-Path -LiteralPath $installResult.installRoot) "root=$($installResult.installRoot); smPid=$($installResult.serviceManagerPid)"

    $env:MPT_DATA_ROOT = $dataRoot
    $cli = Join-Path $installResult.installRoot 'Cli\MyPowerTools.Cli.exe'
    $serviceCall = Invoke-NativeCapture -FilePath $cli -ArgumentList (@('service', 'list') + $cliSmArguments)
    if ($serviceCall.ExitCode -ne 0) {
        throw "service list failed with exit $($serviceCall.ExitCode): $($serviceCall.Output)"
    }

    # Windows PowerShell 5.1 emits a JSON array from ConvertFrom-Json as one nested
    # pipeline object. Flatten it explicitly so each service remains a scalar record.
    $parsedServices = $serviceCall.Output | ConvertFrom-Json
    $services = [Collections.Generic.List[object]]::new()
    foreach ($parsedService in $parsedServices) {
        $services.Add($parsedService)
    }
    $expectedUnitIds = @($installResult.units | ForEach-Object { [string]$_ })
    $actualUnitIds = @($services | ForEach-Object { [string]$_.unitId })
    $missingUnits = @($expectedUnitIds | Where-Object { $actualUnitIds -notcontains $_ })
    $unexpectedUnits = @($actualUnitIds | Where-Object { $expectedUnitIds -notcontains $_ })
    $unitInventoryOk = $expectedUnitIds.Count -gt 0 -and $missingUnits.Count -eq 0 -and $unexpectedUnits.Count -eq 0
    Add-RemoteRecord 'A5.R2-service-units-enumerated' $unitInventoryOk "expected=$($expectedUnitIds -join ','); actual=$($actualUnitIds -join ',')"

    $unitLivenessFailures = [Collections.Generic.List[string]]::new()
    foreach ($unitId in $expectedUnitIds) {
        $service = $services | Where-Object { $_.unitId -eq $unitId } | Select-Object -First 1
        $heartbeatPath = Join-Path $dataRoot "state\$unitId.heartbeat"
        for ($heartbeatAttempt = 0; $heartbeatAttempt -lt 40 -and -not (Test-Path -LiteralPath $heartbeatPath); $heartbeatAttempt++) {
            Start-Sleep -Milliseconds 250
        }
        $unitProcess = if ($null -ne $service -and $service.Pid -gt 0) {
            Get-Process -Id $service.Pid -ErrorAction SilentlyContinue
        }
        $processInTestRoot = $false
        if ($null -ne $unitProcess) {
            try {
                $processInTestRoot = [IO.Path]::GetFullPath($unitProcess.Path).StartsWith(($testRootFull + '\'), [StringComparison]::OrdinalIgnoreCase)
            }
            finally {
                $unitProcess.Dispose()
            }
        }
        $unitReady = $null -ne $service -and
            $service.state -eq 'Active' -and
            $service.Pid -gt 0 -and
            $processInTestRoot -and
            (Test-Path -LiteralPath $heartbeatPath)
        if (-not $unitReady) {
            $unitLivenessFailures.Add("${unitId}:state=$($service.state);pid=$($service.Pid);processInTestRoot=$processInTestRoot;heartbeat=$(Test-Path -LiteralPath $heartbeatPath)")
        }
    }
    Add-RemoteRecord 'A5.R3-all-service-units-active' ($unitLivenessFailures.Count -eq 0) "failures=$($unitLivenessFailures -join '|')"

    # Runner and Shell resolve their scoped Service Unit client through this endpoint.
    # install.ps1 runs in a child process, so its process-local environment cannot
    # flow back into this verifier; bind the isolated endpoint before starting either
    # product process to keep proxy modules away from the daily ServiceManager.
    $env:MPT_SERVICEMANAGER_ENDPOINT = $smEndpoint
    $env:MPT_DATA_ROOT = $dataRoot

    $runner = Join-Path $installResult.installRoot 'Runner\MyPowerTools.Runner.exe'
    $modules = Join-Path $installResult.installRoot 'modules'
    $onceCall = Invoke-NativeCapture -FilePath $runner -ArgumentList @('--once', '--modules', $modules, '--data-root', $dataRoot)
    $runnerOnceOutput = $onceCall.Output
    $catalogOk = @('adb-forwarder', 'android-tools.notifications', 'doubao-agent', 'screenease', 'smartbird-thermostat') |
        Where-Object { $runnerOnceOutput -match [regex]::Escape($_) }
    Add-RemoteRecord 'A5.R4-installed-catalog-discovery' ($onceCall.ExitCode -eq 0 -and $catalogOk.Count -eq 5) "exit=$($onceCall.ExitCode); tools=$($catalogOk -join ',')"

    $runnerOut = Join-Path $testRootFull 'runner.out.log'
    $runnerErr = Join-Path $testRootFull 'runner.err.log'
    $runnerProcess = Start-Process -FilePath $runner -ArgumentList @(
        '--modules', $modules,
        '--data-root', $dataRoot,
        '--endpoint-address', $runnerEndpoint,
        '--instance-name', $runnerInstanceName
    ) -WindowStyle Hidden -PassThru -RedirectStandardOutput $runnerOut -RedirectStandardError $runnerErr
    Start-Sleep -Seconds 4

    $shell = Join-Path $installResult.installRoot 'Shell\MyPowerTools.Shell.Avalonia.exe'
    $shellCall = Invoke-ProcessCapture -FilePath $shell -ArgumentList @(
        '--smoke',
        '--timeout-ms', '20000',
        '--quit-runner',
        '--data-root', $dataRoot,
        '--endpoint-address', $runnerEndpoint
    ) -StandardOutputPath (Join-Path $testRootFull 'shell.out.log') -StandardErrorPath (Join-Path $testRootFull 'shell.err.log') -TimeoutMs 30000
    $shellSmokeOutput = $shellCall.Output
    Add-RemoteRecord 'A5.R5-shell-runner-control-path' ($shellCall.ExitCode -eq 0 -and $shellSmokeOutput -match 'smoke connected') "exit=$($shellCall.ExitCode); timedOut=$($shellCall.TimedOut); pid=$($shellCall.ProcessId); output=$shellSmokeOutput"
}
catch {
    $executionError = $_.Exception.ToString()
    if ([string]::IsNullOrWhiteSpace($installLog)) {
        $installLog = $executionError
    }

    Add-RemoteRecord 'A5.R0-remote-execution' $false $_.Exception.Message
}
finally {
    if ($null -ne $runnerProcess) {
        try {
            $runnerProcess.Refresh()
            if (-not $runnerProcess.HasExited) {
                Stop-Process -Id $runnerProcess.Id -Force -ErrorAction Stop
            }

            [void]$runnerProcess.WaitForExit(5000)
            $runnerProcess.Dispose()
            $runnerProcess = $null
        }
        catch {
            $cleanupErrors.Add("runner: $($_.Exception.Message)")
        }
        finally {
            if ($null -ne $runnerProcess) {
                $runnerProcess.Dispose()
                $runnerProcess = $null
            }
        }
    }

    if ($null -ne $cli -and (Test-Path -LiteralPath $cli -PathType Leaf)) {
        foreach ($unitId in @($installResult.units)) {
            $stopCall = Invoke-NativeCapture -FilePath $cli -ArgumentList (@('service', 'stop', [string]$unitId) + $cliSmArguments)
            if ($stopCall.ExitCode -ne 0) {
                $cleanupErrors.Add("service stop $unitId exit=$($stopCall.ExitCode): $($stopCall.Output)")
            }
        }

        $shutdownCall = Invoke-NativeCapture -FilePath $cli -ArgumentList (@('service', 'shutdown') + $cliSmArguments)
        if ($shutdownCall.ExitCode -ne 0) {
            $cleanupErrors.Add("service shutdown exit=$($shutdownCall.ExitCode): $($shutdownCall.Output)")
        }
    }

    Start-Sleep -Milliseconds 800
    try {
        $candidateProcesses = Get-Process -ErrorAction SilentlyContinue
        foreach ($candidateProcess in $candidateProcesses) {
            $isTestProcess = $false
            try {
                $candidatePath = $candidateProcess.Path
                if ($null -ne $candidatePath -and [IO.Path]::GetFullPath($candidatePath).StartsWith(($testRootFull + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
                    $isTestProcess = $true
                    $stoppedTestProcesses.Add([ordered]@{
                        id = $candidateProcess.Id
                        name = $candidateProcess.ProcessName
                        path = $candidatePath
                    })
                    Stop-Process -Id $candidateProcess.Id -Force -ErrorAction Stop
                    [void]$candidateProcess.WaitForExit(5000)
                }
            }
            catch {
                if ($isTestProcess) {
                    $cleanupErrors.Add("process $($candidateProcess.Id): $($_.Exception.Message)")
                }
            }
            finally {
                $candidateProcess.Dispose()
            }
        }
    }
    catch {
        $cleanupErrors.Add("process cleanup: $($_.Exception.Message)")
    }

    Start-Sleep -Milliseconds 500
    for ($cleanupAttempt = 0; $cleanupAttempt -lt 60; $cleanupAttempt++) {
        try {
            if (Test-Path -LiteralPath $testRootFull) {
                Remove-Item -LiteralPath $testRootFull -Recurse -Force -ErrorAction Stop
            }

            break
        }
        catch {
            if ($cleanupAttempt -eq 59) {
                $cleanupErrors.Add("test root: $($_.Exception.Message)")
                break
            }

            Start-Sleep -Milliseconds 500
        }
    }

    try {
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
    }
    catch {
        $cleanupErrors.Add("archive: $($_.Exception.Message)")
    }
}

$protectedAfter = @(Get-ProtectedProcessSnapshots -ExcludedRoot $testRootFull)
$protectedUnchanged = Test-ProtectedProcessesUnchanged -Before $protectedBefore -After $protectedAfter
Add-RemoteRecord 'A5.R6-daily-processes-preserved' $protectedUnchanged "before=$($protectedBefore.id -join ','); after=$($protectedAfter.id -join ',')"

$residualTestProcesses = [Collections.Generic.List[object]]::new()
$remainingProcesses = Get-Process -ErrorAction SilentlyContinue
foreach ($remainingProcess in $remainingProcesses) {
    try {
        $remainingPath = $remainingProcess.Path
        if ($null -ne $remainingPath -and [IO.Path]::GetFullPath($remainingPath).StartsWith(($testRootFull + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
            $residualTestProcesses.Add([ordered]@{
                id = $remainingProcess.Id
                name = $remainingProcess.ProcessName
                path = $remainingPath
            })
        }
    }
    catch {
        # Access to unrelated system processes can be denied; their path cannot match the test root.
    }
    finally {
        $remainingProcess.Dispose()
    }
}
$cleanupPassed = $cleanupErrors.Count -eq 0 -and
    -not (Test-Path -LiteralPath $testRootFull) -and
    -not (Test-Path -LiteralPath $archive) -and
    $residualTestProcesses.Count -eq 0
Add-RemoteRecord 'A5.R7-run-resources-cleaned' $cleanupPassed "errors=$($cleanupErrors -join ' | '); stopped=$($stoppedTestProcesses.name -join ','); residualPids=$($residualTestProcesses.id -join ',')"

$passed = $null -eq $executionError -and @($remoteRecords | Where-Object { -not $_.passed }).Count -eq 0
$result = [ordered]@{
    passed = $passed
    host = $env:COMPUTERNAME
    os = [Environment]::OSVersion.VersionString
    runId = $RunId
    suiteVersion = $SuiteVersion
    installRoot = if ($null -ne $installResult) { $installResult.installRoot } else { $null }
    dataRoot = $dataRoot
    serviceManagerEndpoint = $smEndpoint
    serviceManagerInstanceName = $smInstanceName
    serviceUnitInstanceName = $unitInstanceName
    runnerEndpoint = $runnerEndpoint
    runnerInstanceName = $runnerInstanceName
    installResult = $installResult
    installLog = $installLog
    runnerOnceOutput = $runnerOnceOutput
    shellSmokeOutput = $shellSmokeOutput
    protectedBefore = $protectedBefore
    protectedAfter = $protectedAfter
    stoppedTestProcesses = @($stoppedTestProcesses)
    residualTestProcesses = @($residualTestProcesses)
    cleanupErrors = @($cleanupErrors)
    executionError = $executionError
    records = $remoteRecords
}

Write-Output ('A5_RESULT=' + ($result | ConvertTo-Json -Depth 12 -Compress))
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
exit $(if ($passed) { 0 } else { 1 })
