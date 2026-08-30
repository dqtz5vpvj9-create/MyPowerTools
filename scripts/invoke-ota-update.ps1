[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedPackageSha256,
    [Parameter(Mandatory = $true)][string]$TargetRoot,
    [Parameter(Mandatory = $true)][string]$TargetManifestPath,
    [string]$StateRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools\ota-state'),
    [switch]$ApplyDeletes,
    [switch]$StopTargetProcesses,
    [switch]$RestartRuntime,
    [string]$RuntimeDataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [switch]$KeepBackup,
    [switch]$SkipDriftCheck,
    [string[]]$ProtectedPath = @('install.manifest.json')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'runtime-environment.ps1')

function ConvertTo-SafeRelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $normalized = $Path.Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($normalized) -or
        $normalized.StartsWith('/') -or
        [IO.Path]::IsPathRooted($normalized)) {
        throw "OTA plan contains an unsafe path: $Path"
    }
    $rawSegments = $normalized.Split('/')
    $segments = @($rawSegments | Where-Object { $_.Length -gt 0 })
    if ($segments.Count -eq 0 -or $segments.Count -ne $rawSegments.Count -or
        ($segments | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) {
        throw "OTA plan contains an unsafe path: $Path"
    }
    return $segments -join '/'
}

function Resolve-PathInsideRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $candidate = [IO.Path]::GetFullPath((Join-Path $rootFull $RelativePath.Replace('/', '\')))
    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "OTA path escaped its root: $RelativePath"
    }
    return $candidate
}

function Test-FileMatches {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$Length,
        [Parameter(Mandatory = $true)][string]$Sha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        [long]$item.Length -ne $Length) {
        return $false
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.Equals(
        $Sha256,
        [StringComparison]::OrdinalIgnoreCase)
}

function Remove-ValidatedTransactionRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$ExpectedParent,
        [Parameter(Mandatory = $true)][string]$ExpectedLeaf
    )

    $rootFull = [IO.Path]::GetFullPath($Root)
    $parentFull = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $rootFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($rootFull) -ne $ExpectedLeaf) {
        throw "Refusing to remove an unexpected OTA transaction path: $rootFull"
    }
    if (Test-Path -LiteralPath $rootFull -PathType Container) {
        Remove-Item -LiteralPath $rootFull -Recurse -Force
    }
}

function Replace-FileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$TransactionId,
        [Parameter(Mandatory = $true)][string]$Phase
    )

    $replaceBackup = "$Destination.ota-$TransactionId.$Phase"
    if (Test-Path -LiteralPath $replaceBackup) {
        Remove-Item -LiteralPath $replaceBackup -Force
    }
    [IO.File]::Replace($Source, $Destination, $replaceBackup)
    if (Test-Path -LiteralPath $replaceBackup) {
        Remove-Item -LiteralPath $replaceBackup -Force
    }
}

function Resolve-OtaReopenRestart {
    param([Parameter(Mandatory = $true)][string]$StateRoot)

    $startShell = $true
    $startRunner = $true
    $taskNames = @()
    $planPath = Join-Path $StateRoot 'reopen-plan.json'
    if (Test-Path -LiteralPath $planPath -PathType Leaf) {
        $plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
        $ids = @($plan.targets | ForEach-Object { [string]$_.id })
        $startShell = $ids -contains 'shell'
        $startRunner = ($ids -contains 'runner') -or ($ids -contains 'service-manager')
        if ($ids -contains 'smartbird') {
            $taskNames += 'SmartBirdThermostat'
        }
        if ($ids -contains 'energy') {
            $taskNames += 'EnergyServer'
        }
    }

    return [pscustomobject]@{
        StartShell = $startShell
        StartRunner = $startRunner
        TaskNames = $taskNames
    }
}

function Start-OtaReopenedScheduledTasks {
    param([string[]]$TaskNames)

    foreach ($name in @($TaskNames)) {
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }
        if ($null -eq (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue)) {
            continue
        }
        Start-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    }
}

function Invoke-InteractiveRuntimeStart {
    param(
        [Parameter(Mandatory = $true)][string]$RuntimeScript,
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [Parameter(Mandatory = $true)][string]$DataRoot,
        [Parameter(Mandatory = $true)][string]$StateRoot
    )

    if (-not (Test-Path -LiteralPath $RuntimeScript -PathType Leaf)) {
        throw "MyPowerTools runtime starter is missing: $RuntimeScript"
    }
    $reopen = Resolve-OtaReopenRestart -StateRoot $StateRoot
    $currentSessionId = (Get-Process -Id $PID).SessionId
    if ($currentSessionId -ne 0) {
        $runtimeArguments = @{
            InstallRoot = $InstallRoot
            DataRoot = $DataRoot
        }
        if ($reopen.StartRunner) {
            $runtimeArguments.StartRunner = $true
        }
        if ($reopen.StartShell) {
            $runtimeArguments.StartShell = $true
        }
        & $RuntimeScript @runtimeArguments | Out-Null
        Start-OtaReopenedScheduledTasks -TaskNames $reopen.TaskNames
        return
    }

    $interactiveUser = [string](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ([string]::IsNullOrWhiteSpace($interactiveUser) -or
        -not $interactiveUser.Equals($currentUser, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$currentUser has no matching interactive desktop for the OTA runtime restart."
    }

    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $taskName = "MyPowerToolsOtaRestart-$PID-$([Guid]::NewGuid().ToString('N'))"
    $actionArguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File `"$RuntimeScript`" -InstallRoot `"$InstallRoot`" -DataRoot `"$DataRoot`""
    if ($reopen.StartRunner) {
        $actionArguments += ' -StartRunner'
    }
    if ($reopen.StartShell) {
        $actionArguments += ' -StartShell'
    }
    $actionParams = @{
        Execute = $windowsPowerShell
        Argument = $actionArguments
        WorkingDirectory = $InstallRoot
    }
    $principalParams = @{
        UserId = $interactiveUser
        LogonType = 'Interactive'
        RunLevel = 'Limited'
    }
    $settingsParams = @{
        AllowStartIfOnBatteries = $true
        DontStopIfGoingOnBatteries = $true
        ExecutionTimeLimit = (New-TimeSpan -Minutes 5)
    }
    $action = New-ScheduledTaskAction @actionParams
    $principal = New-ScheduledTaskPrincipal @principalParams
    $settings = New-ScheduledTaskSettingsSet @settingsParams
    $registeredAt = Get-Date
    $registered = $false
    try {
        $registerParams = @{
            TaskName = $taskName
            Action = $action
            Principal = $principal
            Settings = $settings
            Description = 'One-time MyPowerTools OTA interactive-session restart'
            Force = $true
        }
        Register-ScheduledTask @registerParams | Out-Null
        $registered = $true
        Start-ScheduledTask -TaskName $taskName

        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline) {
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
            $info = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction Stop
            if ($task.State -ne 'Running' -and $info.LastRunTime -ge $registeredAt.AddSeconds(-1)) {
                if ($info.LastTaskResult -ne 0) {
                    throw "Interactive OTA runtime restart failed with task result $($info.LastTaskResult)."
                }
                Start-OtaReopenedScheduledTasks -TaskNames $reopen.TaskNames
                return
            }
            Start-Sleep -Milliseconds 500
        }
        throw 'Interactive OTA runtime restart timed out.'
    }
    finally {
        if ($registered) {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        }
    }
}

$packageFull = [IO.Path]::GetFullPath($PackagePath)
$targetRootFull = [IO.Path]::GetFullPath($TargetRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$targetManifestFull = [IO.Path]::GetFullPath($TargetManifestPath)
$stateRootFull = [IO.Path]::GetFullPath($StateRoot)
New-Item -ItemType Directory -Path $stateRootFull -Force | Out-Null
try {
    Set-Location -LiteralPath $stateRootFull
} catch {
}
if (-not (Test-Path -LiteralPath $packageFull -PathType Leaf)) {
    throw "OTA package does not exist: $packageFull"
}
if (-not (Test-Path -LiteralPath $targetRootFull -PathType Container)) {
    throw "OTA target root does not exist: $targetRootFull"
}
if (-not (Test-Path -LiteralPath $targetManifestFull -PathType Leaf)) {
    throw "OTA target manifest does not exist: $targetManifestFull"
}
if ($RestartRuntime -and -not $StopTargetProcesses) {
    throw '-RestartRuntime requires -StopTargetProcesses.'
}

$actualPackageHash = (Get-FileHash -LiteralPath $packageFull -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualPackageHash -ne $ExpectedPackageSha256.ToLowerInvariant()) {
    throw "OTA package SHA-256 mismatch. expected=$ExpectedPackageSha256 actual=$actualPackageHash"
}

$transactionId = [Guid]::NewGuid().ToString('N')
$transactionParent = Join-Path $stateRootFull 'transactions'
$transactionRoot = Join-Path $transactionParent $transactionId
$extractRoot = Join-Path $transactionRoot 'extracted'
$backupRoot = Join-Path $transactionRoot 'backup'
$journal = [Collections.Generic.List[object]]::new()
$completed = $false
$stoppedProcessRecords = [Collections.Generic.List[object]]::new()
$runtimeRestarted = $false
$legacyDotNetRootMigration = Clear-MyPowerToolsLegacyUserDotNetRoot -InstallRoot $targetRootFull

try {
    New-Item -ItemType Directory -Path $extractRoot, $backupRoot -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($packageFull)
    try {
        foreach ($entry in $archive.Entries) {
            $entryPath = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($entryPath)) {
                continue
            }
            $isDirectory = $entryPath.EndsWith('/')
            $safePath = ConvertTo-SafeRelativePath -Path $entryPath.TrimEnd('/')
            $destination = Resolve-PathInsideRoot -Root $extractRoot -RelativePath $safePath
            if ($isDirectory) {
                New-Item -ItemType Directory -Path $destination -Force | Out-Null
                continue
            }
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
        }
    }
    finally {
        $archive.Dispose()
    }

    $planPath = Join-Path $extractRoot 'ota-plan.json'
    $sourceManifestPath = Join-Path $extractRoot 'source-manifest.json'
    foreach ($requiredPath in @($planPath, $sourceManifestPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "OTA package is missing $([IO.Path]::GetFileName($requiredPath))"
        }
    }
    $plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
    if ([int]$plan.schemaVersion -ne 1 -or [string]$plan.kind -ne 'mypowertools-ota-delta-plan') {
        throw 'Unsupported OTA delta plan.'
    }
    $targetManifestHash = (Get-FileHash -LiteralPath $targetManifestFull -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceManifestHash = (Get-FileHash -LiteralPath $sourceManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($targetManifestHash -ne ([string]$plan.targetManifestSha256).ToLowerInvariant()) {
        throw 'Target manifest does not match the OTA plan.'
    }
    if ($sourceManifestHash -ne ([string]$plan.sourceManifestSha256).ToLowerInvariant()) {
        throw 'Source manifest inside the OTA package does not match the OTA plan.'
    }

    $protected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $ProtectedPath) {
        [void]$protected.Add((ConvertTo-SafeRelativePath -Path $path))
    }
    $operationPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $copyRecords = [Collections.Generic.List[object]]::new()
    foreach ($record in @($plan.copy)) {
        $relativePath = ConvertTo-SafeRelativePath -Path ([string]$record.path)
        if (-not $operationPaths.Add($relativePath)) {
            throw "OTA plan contains a duplicate operation path: $relativePath"
        }
        $sha256 = ([string]$record.sha256).ToLowerInvariant()
        $length = [long]$record.length
        if ($sha256 -notmatch '^[0-9a-f]{64}$' -or $length -lt 0) {
            throw "OTA plan contains invalid source metadata: $relativePath"
        }
        $payloadPath = Resolve-PathInsideRoot -Root (Join-Path $extractRoot 'payload') -RelativePath $relativePath
        if (-not (Test-FileMatches -Path $payloadPath -Length $length -Sha256 $sha256)) {
            throw "OTA payload verification failed: $relativePath"
        }
        $copyRecords.Add([pscustomobject]@{
            path = $relativePath
            length = $length
            sha256 = $sha256
            targetLength = $record.targetLength
            targetSha256 = $record.targetSha256
            payloadPath = $payloadPath
        })
    }

    $deleteRecords = [Collections.Generic.List[object]]::new()
    foreach ($record in @($plan.delete)) {
        $relativePath = ConvertTo-SafeRelativePath -Path ([string]$record.path)
        if (-not $operationPaths.Add($relativePath)) {
            throw "OTA plan contains a duplicate operation path: $relativePath"
        }
        if ($protected.Contains($relativePath)) {
            throw "OTA plan attempts to delete a protected file: $relativePath"
        }
        $deleteRecords.Add([pscustomobject]@{
            path = $relativePath
            targetLength = [long]$record.targetLength
            targetSha256 = ([string]$record.targetSha256).ToLowerInvariant()
        })
    }

    foreach ($record in $copyRecords) {
        $targetPath = Resolve-PathInsideRoot -Root $targetRootFull -RelativePath ([string]$record.path)
        if ($null -eq $record.targetSha256) {
            if (-not $SkipDriftCheck -and (Test-Path -LiteralPath $targetPath)) {
                throw "OTA target gained a file after its manifest was generated: $($record.path)"
            }
        }
        else {
            $targetMatchParams = @{
                Path = $targetPath
                Length = [long]$record.targetLength
                Sha256 = [string]$record.targetSha256
            }
            if (Test-FileMatches @targetMatchParams) {
                continue
            }
            if (-not $SkipDriftCheck) {
                throw "OTA target changed after its manifest was generated: $($record.path)"
            }
        }
    }
    foreach ($record in $deleteRecords) {
        $targetPath = Resolve-PathInsideRoot -Root $targetRootFull -RelativePath ([string]$record.path)
        $deleteMatchParams = @{
            Path = $targetPath
            Length = [long]$record.targetLength
            Sha256 = [string]$record.targetSha256
        }
        if (-not $SkipDriftCheck -and -not (Test-FileMatches @deleteMatchParams)) {
            throw "OTA deletion target changed after its manifest was generated: $($record.path)"
        }
    }

    if ($StopTargetProcesses) {
        $targetPrefix = $targetRootFull + [IO.Path]::DirectorySeparatorChar
        foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
            $processPath = $null
            try { $processPath = $process.MainModule.FileName } catch {}
            if (-not $processPath -or
                -not $processPath.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                $process.Dispose()
                continue
            }
            $stoppedProcessRecords.Add([pscustomobject]@{
                name = $process.ProcessName
                processId = $process.Id
                path = $processPath
                sessionId = $process.SessionId
            })
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                [void]$process.CloseMainWindow()
                [void]$process.WaitForExit(3000)
            }
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
            }
            $process.Dispose()
        }
    }

    $affectedRecords = @($copyRecords) + $(if ($ApplyDeletes) { @($deleteRecords) } else { @() })
    foreach ($record in $affectedRecords) {
        $targetPath = Resolve-PathInsideRoot -Root $targetRootFull -RelativePath ([string]$record.path)
        $backupPath = Resolve-PathInsideRoot -Root $backupRoot -RelativePath ([string]$record.path)
        $existed = Test-Path -LiteralPath $targetPath -PathType Leaf
        if ($existed) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force | Out-Null
            Copy-Item -LiteralPath $targetPath -Destination $backupPath -Force
        }
        $journal.Add([pscustomobject]@{
            path = [string]$record.path
            targetPath = $targetPath
            backupPath = $backupPath
            existed = $existed
        })
    }

    foreach ($record in $copyRecords) {
        $targetPath = Resolve-PathInsideRoot -Root $targetRootFull -RelativePath ([string]$record.path)
        $targetParent = Split-Path -Parent $targetPath
        New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
        $temporaryPath = Join-Path $targetParent ('.' + [IO.Path]::GetFileName($targetPath) + ".ota-$transactionId.new")
        Copy-Item -LiteralPath ([string]$record.payloadPath) -Destination $temporaryPath -Force
        $stagedMatchParams = @{
            Path = $temporaryPath
            Length = [long]$record.length
            Sha256 = [string]$record.sha256
        }
        if (-not (Test-FileMatches @stagedMatchParams)) {
            throw "OTA staged target verification failed: $($record.path)"
        }
        if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
            $replaceParams = @{
                Source = $temporaryPath
                Destination = $targetPath
                TransactionId = $transactionId
                Phase = 'replaced'
            }
            Replace-FileAtomically @replaceParams
        }
        else {
            Move-Item -LiteralPath $temporaryPath -Destination $targetPath
        }
    }

    $appliedDeleteCount = 0
    if ($ApplyDeletes) {
        foreach ($record in $deleteRecords) {
            $targetPath = Resolve-PathInsideRoot -Root $targetRootFull -RelativePath ([string]$record.path)
            Remove-Item -LiteralPath $targetPath -Force
            $appliedDeleteCount++
        }
    }

    foreach ($record in $copyRecords) {
        $targetPath = Resolve-PathInsideRoot -Root $targetRootFull -RelativePath ([string]$record.path)
        $finalMatchParams = @{
            Path = $targetPath
            Length = [long]$record.length
            Sha256 = [string]$record.sha256
        }
        if (-not (Test-FileMatches @finalMatchParams)) {
            throw "OTA final target verification failed: $($record.path)"
        }
    }
    if ($ApplyDeletes) {
        foreach ($record in $deleteRecords) {
            $targetPath = Resolve-PathInsideRoot -Root $targetRootFull -RelativePath ([string]$record.path)
            if (Test-Path -LiteralPath $targetPath) {
                throw "OTA deletion did not complete: $($record.path)"
            }
        }
    }

    if ($RestartRuntime) {
        $runtimeStartParams = @{
            RuntimeScript = Join-Path $targetRootFull 'start-user-runtime.ps1'
            InstallRoot = $targetRootFull
            DataRoot = [IO.Path]::GetFullPath($RuntimeDataRoot)
            StateRoot = $stateRootFull
        }
        Invoke-InteractiveRuntimeStart @runtimeStartParams
        $runtimeRestarted = $true
    }

    New-Item -ItemType Directory -Path $stateRootFull -Force | Out-Null
    Copy-Item -LiteralPath $sourceManifestPath -Destination (Join-Path $stateRootFull 'desired-source-manifest.json') -Force
    $runtimeResolution = Set-MyPowerToolsProcessDotNetRoot -InstallRoot $targetRootFull
    $result = [ordered]@{
        success = $true
        transactionId = $transactionId
        packageSha256 = $actualPackageHash
        sourceVersion = [string]$plan.sourceVersion
        targetVersion = [string]$plan.targetVersion
        copiedFiles = $copyRecords.Count
        deletedFiles = $appliedDeleteCount
        pendingDeletes = if ($ApplyDeletes) { 0 } else { $deleteRecords.Count }
        stoppedProcesses = $stoppedProcessRecords.ToArray()
        restartRequired = $stoppedProcessRecords.Count -gt 0
        runtimeRestarted = $runtimeRestarted
        runtimeSource = [string]$runtimeResolution.source
        legacyDotNetRootMigration = $legacyDotNetRootMigration
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    [IO.File]::WriteAllText(
        (Join-Path $stateRootFull 'last-update.json'),
        ($result | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
    $completed = $true
    $result | ConvertTo-Json -Depth 6
}
catch {
    $updateError = $_
    for ($index = $journal.Count - 1; $index -ge 0; $index--) {
        $entry = $journal[$index]
        try {
            if ([bool]$entry.existed) {
                New-Item -ItemType Directory -Path (Split-Path -Parent ([string]$entry.targetPath)) -Force | Out-Null
                $rollbackTemporary = ([string]$entry.targetPath) + ".ota-$transactionId.rollback"
                Copy-Item -LiteralPath ([string]$entry.backupPath) -Destination $rollbackTemporary -Force
                if (Test-Path -LiteralPath ([string]$entry.targetPath) -PathType Leaf) {
                    $rollbackReplaceParams = @{
                        Source = $rollbackTemporary
                        Destination = [string]$entry.targetPath
                        TransactionId = $transactionId
                        Phase = 'rollback-replaced'
                    }
                    Replace-FileAtomically @rollbackReplaceParams
                }
                else {
                    Move-Item -LiteralPath $rollbackTemporary -Destination ([string]$entry.targetPath)
                }
            }
            elseif (Test-Path -LiteralPath ([string]$entry.targetPath)) {
                Remove-Item -LiteralPath ([string]$entry.targetPath) -Force
            }
        }
        catch {
        }
    }
    if ($RestartRuntime -and $stoppedProcessRecords.Count -gt 0 -and -not $runtimeRestarted) {
        try {
            $rollbackRuntimeStartParams = @{
                RuntimeScript = Join-Path $targetRootFull 'start-user-runtime.ps1'
                InstallRoot = $targetRootFull
                DataRoot = [IO.Path]::GetFullPath($RuntimeDataRoot)
                StateRoot = $stateRootFull
            }
            Invoke-InteractiveRuntimeStart @rollbackRuntimeStartParams
            $runtimeRestarted = $true
        }
        catch {
            throw [AggregateException]::new(
                'OTA failed, rollback ran, and the interactive runtime restart also failed.',
                @($updateError.Exception, $_.Exception))
        }
    }
    throw $updateError
}
finally {
    if (-not $KeepBackup) {
        $removeTransactionParams = @{
            Root = $transactionRoot
            ExpectedParent = $transactionParent
            ExpectedLeaf = $transactionId
        }
        Remove-ValidatedTransactionRoot @removeTransactionParams
    }
}
