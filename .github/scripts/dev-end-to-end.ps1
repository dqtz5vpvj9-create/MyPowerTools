# Temporary PR-only evidence. This harness never changes the official Dev entry point.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$CandidateCommit
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$baselineCommit = '6b83a1567756a044f4512a31f5bde7e26fb67122'
if ($CandidateCommit -eq $baselineCommit) { throw 'Candidate must differ from the immutable baseline.' }
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$script:stepDeadline = [DateTime]::UtcNow.AddMinutes(32)
$source = [IO.Path]::GetFullPath($SourceRoot)
$work = Join-Path $env:RUNNER_TEMP 'mpt-dev-e2e'
$report = Join-Path $env:RUNNER_TEMP 'dev-e2e-report'
$local = Join-Path $work 'profile\Local'
$roaming = Join-Path $work 'profile\Roaming'
$install = Join-Path $local 'Programs\MyPowerTools'
$data = Join-Path $local 'MyPowerTools'
$rows = [Collections.Generic.List[object]]::new()
$cleanup = [Collections.Generic.List[string]]::new()
$script:cleanupAttempt = 0
$script:activeArm = ''
$script:knownFolderPaths = @{}
$script:knownFolderMoves = [Collections.Generic.List[object]]::new()
$pwsh = (Get-Command pwsh.exe -CommandType Application).Source
$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$originalEnvironment = @{}
foreach ($name in @('LOCALAPPDATA', 'APPDATA', 'MPT_DATA_ROOT', 'MPT_SHELL_STARTUP_TRACE', 'MPT_DEV_TIMING_TRACE', 'MPT_DEV_TIMING_TRACE_PATH')) {
    $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
New-Item -ItemType Directory -Force $work, $report | Out-Null
. (Join-Path $PSScriptRoot 'dev-e2e-safety.ps1')

function Save-Json($Value, [string]$Path) {
    $Value | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $Path -Encoding utf8
}
function Get-OptionalRegistryValue([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -ErrorAction Stop)) { return $null }
    $key = Get-Item -LiteralPath $Path -ErrorAction Stop
    try { return $key.GetValue($Name, $null) }
    finally { $key.Close() }
}
function Is-Inside([string]$Path, [string]$Root) {
    return [IO.Path]::GetFullPath($Path).StartsWith(
        [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Remove-OwnDirectory([string]$Path, [string]$Root) {
    if (-not (Is-Inside $Path $Root)) { throw "Unsafe cleanup path: $Path" }
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
}
function Get-InstalledProcesses {
    @(Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
        $path = $null
        try { $path = $_.MainModule.FileName } catch {}
        if ($path -and (Is-Inside $path $install)) {
            [pscustomobject]@{ id = $_.Id; name = $_.ProcessName; path = $path; sessionId = $_.SessionId }
        }
    })
}
function Move-OwnKnownFolderState([ValidateSet('baseline', 'candidate')][string]$Arm) {
    # Only paths proved absent before any product startup are eligible. Backups
    # remain outside the uploaded report because generated state can contain tokens.
    $backup = Join-Path $work "known-folder-backup\$Arm"
    if (-not (Is-Inside $backup $work) -or (Is-Inside $backup $report)) { throw 'Unsafe KnownFolder backup path.' }
    foreach ($name in $script:knownFolderPaths.Keys) {
        $path = $script:knownFolderPaths[$name]
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $directory = Get-Item -LiteralPath $path -Force
        if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Unexpected KnownFolder state type: $name"
        }
        $entries = @(Get-ChildItem -LiteralPath $path -Force -Recurse)
        if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
            throw "Refusing to relocate linked KnownFolder state: $name"
        }
        $destination = Join-Path $backup $name
        if (Test-Path -LiteralPath $destination) { throw "KnownFolder backup already exists: $Arm/$name" }
        New-Item -ItemType Directory -Force $backup | Out-Null
        Move-Item -LiteralPath $path -Destination $destination -ErrorAction Stop
        if ((Test-Path -LiteralPath $path) -or -not (Test-Path -LiteralPath $destination -PathType Container)) {
            throw "KnownFolder relocation did not complete: $name"
        }
        $script:knownFolderMoves.Add([ordered]@{ arm = $Arm; name = $name; entries = $entries.Count; initialAbsenceVerified = $true; contentsPublished = $false })
        Save-Json $script:knownFolderMoves.ToArray() (Join-Path $report 'known-folder-moves.json')
        $cleanup.Add("Preserved run-created KnownFolder state in private $Arm backup: $name")
    }
}
function Stop-OwnInstallation {
    if (Test-Path -LiteralPath $install -PathType Container) {
        if (-not (Is-Inside $install $work) -or -not (Is-Inside $data $work) -or
            $install -ne (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools') -or
            $data -ne (Join-Path $env:LOCALAPPDATA 'MyPowerTools') -or $env:MPT_DATA_ROOT -ne $data) {
            throw 'Refusing service cleanup outside the isolated canonical installation.'
        }
        # The official uninstaller also removes shared registrations. Check ownership first.
        $existingTask = Get-ScheduledTask -TaskName 'MyPowerTools OTA Check' -ErrorAction SilentlyContinue
        if ($existingTask -and @($existingTask.Actions | Where-Object { $_.WorkingDirectory -eq $install }).Count -eq 0) {
            throw 'Refusing service cleanup with an unrelated OTA task.'
        }
        foreach ($name in @('MyPowerTools', 'MyPowerTools.ServiceManager')) {
            $value = Get-OptionalRegistryValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' $name
            if ($null -ne $value -and [string]$value -notlike "*$install*") {
                throw "Refusing service cleanup with unrelated Run value: $name"
            }
        }
        $protocol = 'HKCU:\Software\Classes\mypowertools'
        if (Test-Path -LiteralPath $protocol) {
            $command = (Get-Item -LiteralPath "$protocol\shell\open\command").GetValue('')
            if ([string]$command -notlike "*$install*") { throw 'Refusing service cleanup with unrelated protocol registration.' }
        }
        $configurationScript = Join-Path $install 'configure-user-services.ps1'
        if (-not (Test-Path -LiteralPath $configurationScript -PathType Leaf)) { throw 'Installed official service cleanup script is missing.' }
        $cli = Join-Path $install 'Cli\MyPowerTools.Cli.exe'
        if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw 'Installed official service CLI is missing.' }
        $script:cleanupAttempt++
        $logPrefix = Join-Path $report "cleanup-services-$script:cleanupAttempt"
        # Quiesce and stop managed children before their ServiceManager parent. The
        # executable-path fallback below cannot identify system pwsh-hosted services.
        # Give the supported CLI its own bound; Uninstall's inner CLI limit is only 5 s.
        $quiesce = Start-Captured $cli @('service', 'quiesce') $install "$logPrefix-quiesce"
        Complete-Captured $quiesce "$logPrefix-quiesce" 90
        $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $configurationScript,
            '-Mode', 'Uninstall', '-InstallRoot', $install, '-DataRoot', $data)
        $uninstall = Start-Captured $pwsh $arguments $install $logPrefix
        Complete-Captured $uninstall $logPrefix 90
        $cleanup.Add('Completed official service quiesce and scoped Uninstall before stopping remaining installed processes.')
    }
    foreach ($process in @(Get-InstalledProcesses)) {
        Stop-Process -Id $process.id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $process.id -Timeout 10 -ErrorAction SilentlyContinue
    }
    $task = Get-ScheduledTask -TaskName 'MyPowerTools OTA Check' -ErrorAction SilentlyContinue
    if ($task) {
        $owned = @($task.Actions | Where-Object { $_.WorkingDirectory -eq $install }).Count -gt 0
        if (-not $owned) { throw 'Refusing to remove an OTA task not owned by this isolated installation.' }
        Stop-ScheduledTask -InputObject $task -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -InputObject $task -Confirm:$false
        $cleanup.Add('Removed this isolated installation''s OTA task.')
    }
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    foreach ($name in @('MyPowerTools', 'MyPowerTools.ServiceManager')) {
        $value = Get-OptionalRegistryValue $runKey $name
        if ($null -ne $value) {
            if ([string]$value -notlike "*$install*") { throw "Refusing to remove unrelated Run value: $name" }
            Remove-ItemProperty -LiteralPath $runKey -Name $name
            $cleanup.Add("Removed this isolated installation's Run value: $name")
        }
    }
    $protocol = 'HKCU:\Software\Classes\mypowertools'
    if (Test-Path -LiteralPath $protocol) {
        $command = (Get-Item -LiteralPath "$protocol\shell\open\command").GetValue('')
        if ([string]$command -notlike "*$install*") { throw 'Refusing to remove unrelated protocol registration.' }
        Remove-Item -LiteralPath $protocol -Recurse -Force
        $cleanup.Add('Removed this isolated installation''s protocol registration.')
    }
    if (@(Get-InstalledProcesses).Count) { throw 'An isolated installed process survived cleanup.' }
    $ownedCommandRoot = $install.TrimEnd('\') + '\'
    $hostedSurvivors = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        $_.ProcessId -ne $PID -and $_.CommandLine -and
        $_.CommandLine.IndexOf($ownedCommandRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0
    } | Select-Object ProcessId, Name)
    if ($hostedSurvivors.Count) {
        Save-Json $hostedSurvivors (Join-Path $report 'cleanup-hosted-survivors.json')
        throw 'An isolated command-line-hosted process survived official service cleanup; refusing arm reset.'
    }
    if ($script:activeArm) {
        Move-OwnKnownFolderState $script:activeArm
        $script:activeArm = ''
    }
}
function ConvertTo-WindowsArgument([AllowEmptyString()][string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $builder = [Text.StringBuilder]::new('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $backslashes++; continue }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
            [void]$builder.Append('"'); $backslashes = 0; continue
        }
        if ($backslashes -gt 0) { [void]$builder.Append(('\' * $backslashes)); $backslashes = 0 }
        [void]$builder.Append($character)
    }
    if ($backslashes -gt 0) { [void]$builder.Append(('\' * ($backslashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}
function Start-Captured([string]$File, [string[]]$Arguments, [string]$Directory, [string]$LogPrefix) {
    # Windows Start-Process redirects directly to files. Resident descendants may
    # retain these handles without making this harness wait for pipe EOF.
    $commandLine = ($Arguments | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' '
    $process = Start-Process -FilePath $File -ArgumentList $commandLine -WorkingDirectory $Directory `
        -RedirectStandardOutput "$LogPrefix.stdout.log" -RedirectStandardError "$LogPrefix.stderr.log" `
        -WindowStyle Hidden -PassThru
    Write-Host "Started $LogPrefix (PID $($process.Id)); output is written directly to log files."
    [pscustomobject]@{ process = $process; exitObservedTicks = $null; actualExitUtc = $null }
}
function Complete-Captured($Job, [string]$LogPrefix, [int]$TimeoutSeconds = 900) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    if ($deadline -gt $script:stepDeadline) { $deadline = $script:stepDeadline }
    $nextProgress = [DateTime]::UtcNow.AddSeconds(30)
    while (-not $Job.process.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            try { $Job.process.Kill($true) } catch {}
            [void]$Job.process.WaitForExit(5000)
            throw "Timed out: $LogPrefix"
        }
        if ([DateTime]::UtcNow -ge $nextProgress) {
            Write-Host "Still waiting for process exit: $LogPrefix (PID $($Job.process.Id))."
            $nextProgress = [DateTime]::UtcNow.AddSeconds(30)
        }
    }
    # Capture exit before any diagnostics, log reads, or readiness waiting.
    $Job.exitObservedTicks = [Diagnostics.Stopwatch]::GetTimestamp()
    $Job.actualExitUtc = $Job.process.ExitTime.ToUniversalTime().ToString('O')
    Save-Json ([ordered]@{ processId = $Job.process.Id; exitCode = $Job.process.ExitCode; actualExitUtc = $Job.actualExitUtc; exitObservedTicks = $Job.exitObservedTicks }) "$LogPrefix.exit.json"
    Write-Host "Exited $LogPrefix with code $($Job.process.ExitCode)."
    if ($Job.process.ExitCode -ne 0) {
        Get-Content -LiteralPath "$LogPrefix.stdout.log", "$LogPrefix.stderr.log" -Tail 40 -ErrorAction Continue | Out-Host
        throw "Process failed ($($Job.process.ExitCode)): $LogPrefix"
    }
}
function Assert-CleanRunner {
    if ((Get-Process -Id $PID).SessionId -eq 0 -or -not [Environment]::UserInteractive) { throw 'BLOCKED: interactive session required.' }
    if (@(Get-Process -Name 'MyPowerTools*', 'adb' -ErrorAction SilentlyContinue).Count) { throw 'BLOCKED: pre-existing product or ADB processes.' }
    $tasks = @(Get-ScheduledTask | Where-Object { $_.TaskName -like '*MyPowerTools*' -or $_.TaskName -in @('SmartBirdThermostat', 'EnergyServer') })
    if ($tasks.Count) { throw 'BLOCKED: pre-existing product or legacy scheduled tasks.' }
    if (Test-Path 'HKCU:\Software\Classes\mypowertools') { throw 'BLOCKED: pre-existing product protocol.' }
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    foreach ($name in @('MyPowerTools', 'MyPowerTools.ServiceManager')) {
        if ($null -ne (Get-OptionalRegistryValue $runKey $name)) { throw "BLOCKED: pre-existing Run value $name." }
    }
    $protected = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'MyPowerTools\InputRemap\MyPowerTools.InputRemapHost.exe'
    if (Test-Path -LiteralPath $protected) { throw 'BLOCKED: protected InputRemap host already exists.' }
    if ([Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'User')) { throw 'BLOCKED: existing account-wide DOTNET_ROOT; do not mutate it.' }
    $nextOta = [DateTime]::Today.AddHours(3)
    if ($nextOta -le [DateTime]::Now) { $nextOta = $nextOta.AddDays(1) }
    if (($nextOta - [DateTime]::Now).TotalMinutes -lt 120) { throw 'BLOCKED: scheduled 03:00 OTA window is too close for this run.' }
    $knownLocal = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ($knownLocal -and [IO.Path]::GetFullPath($knownLocal) -ne [IO.Path]::GetFullPath($local)) {
        foreach ($name in @('MyPowerTools', 'AdbForwarder')) {
            $path = Join-Path $knownLocal $name
            if (Test-Path -LiteralPath $path) { throw "BLOCKED: pre-existing KnownFolder state: $name" }
            $script:knownFolderPaths[$name] = $path
        }
    }
    Save-Json ([ordered]@{ initiallyAbsent = @($script:knownFolderPaths.Keys | Sort-Object); checkedAt = [DateTime]::UtcNow.ToString('O') }) (Join-Path $report 'known-folder-preflight.json')
}
function Reset-BuildOutputs {
    & dotnet build-server shutdown | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Cannot stop build servers before resetting the arm.' }
    $paths = @('artifacts\build', 'artifacts\dev-update', 'artifacts\.governance-cache.json')
    $projects = @(& git -C $source ls-files '*/*.csproj')
    foreach ($project in $projects | Where-Object { $_ -like 'tools/local-lag-cleaner/*' -or $_ -like 'tools/nssm-manager/*' }) {
        $directory = Split-Path $project
        $paths += "$directory\bin", "$directory\obj"
    }
    foreach ($relative in $paths | Select-Object -Unique) {
        $path = Join-Path $source $relative
        if (-not (Is-Inside $path $source)) { throw 'Build cleanup escaped source.' }
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    New-Item -ItemType Directory -Force (Join-Path $source 'artifacts\sdk\nuget') | Out-Null
}
function Invoke-DevSample([string]$Arm, [string]$Commit, [int]$Iteration) {
    $label = "$Arm-$Iteration"
    $sample = Join-Path $report $label
    New-Item -ItemType Directory -Force $sample | Out-Null
    $oldProcesses = @(Get-InstalledProcesses)
    $oldShellIds = @($oldProcesses | Where-Object name -eq 'MyPowerTools.Shell.Avalonia' | ForEach-Object id)
    $trace = Join-Path $sample 'shell-startup.tsv'
    $env:MPT_SHELL_STARTUP_TRACE = $trace
    $env:MPT_DEV_TIMING_TRACE = '1'
    $env:MPT_DEV_TIMING_TRACE_PATH = Join-Path $sample 'timing'
    $startedAt = [DateTime]::UtcNow
    $watcherArguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'dev-e2e-readiness.ps1'),
        '-ExpectedShellPath', (Join-Path $install 'Shell\MyPowerTools.Shell.Avalonia.exe'), '-TracePath', $trace,
        '-OutputPath', (Join-Path $sample 'visible-home.json'), '-ScreenshotPath', (Join-Path $sample 'visible-home.png'),
        '-StartedAfterUtc', $startedAt.ToString('O'))
    if ($oldShellIds.Count) { $watcherArguments += @('-RejectedProcessIds', ($oldShellIds -join ',')) }
    $watcher = Start-Captured $windowsPowerShell $watcherArguments $source (Join-Path $sample 'readiness')
    $dev = $null
    try {
        $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $source 'scripts\Start-MyPowerTools-Dev.ps1'))
        Write-Host "Starting ${label}: default official Core/Debug Dev loop, including restart and visible Home."
        $startTicks = [Diagnostics.Stopwatch]::GetTimestamp()
        $dev = Start-Captured $pwsh $arguments $source (Join-Path $sample 'official-dev')
        Complete-Captured $dev (Join-Path $sample 'official-dev')
        $scriptEndTicks = [long]$dev.exitObservedTicks
        Complete-Captured $watcher (Join-Path $sample 'readiness') 120
        $ready = Get-Content -LiteralPath (Join-Path $sample 'visible-home.json') -Raw | ConvertFrom-Json
        if ($ready.status -ne 'ready') { throw 'Visible Home readiness was not established.' }
        $newProcesses = @(Get-InstalledProcesses)
        foreach ($old in $oldProcesses | Where-Object { $_.name -in @('MyPowerTools.Shell.Avalonia', 'MyPowerTools.Runner', 'MyPowerTools.ServiceManager') }) {
            if ($old.id -in @($newProcesses | ForEach-Object id)) { throw "Old $($old.name) process survived the official restart." }
        }
        $timings = Get-Content -LiteralPath (Join-Path $source 'artifacts\dev-update\last-timings.json') -Raw | ConvertFrom-Json
        if (-not $timings.succeeded -or $timings.scope -ne 'Core' -or $timings.configuration -ne 'Debug' -or $timings.noRestore) { throw 'Official Dev validation did not complete under the required defaults.' }
        Copy-Item -LiteralPath (Join-Path $source 'artifacts\dev-update\last-timings.json') -Destination (Join-Path $sample 'native-timings.json')
        $overlay = Get-Content -LiteralPath (Join-Path $install 'dev-update.manifest.json') -Raw | ConvertFrom-Json
        $expectedComponents = @('Broker', 'InputRemap', 'Runner', 'ServiceManager', 'Shell')
        if (@(Compare-Object $expectedComponents @($overlay.components.relativePath | Sort-Object)).Count) { throw 'Full Core overlay component set differs from expected.' }
        Save-Json $overlay (Join-Path $sample 'dev-update.manifest.json')
        $scale = 1000.0 / [Diagnostics.Stopwatch]::Frequency
        $row = [ordered]@{
            arm = $Arm; commit = $Commit; iteration = $Iteration
            scenario = $(if ($Iteration -eq 0) { 'warm-up: initial-core-rebuild/setup-state; excluded from comparison' } else { 'measured: warm-no-edit after one actual Dev warm-up' })
            includedInComparison = ($Iteration -eq 1); timingTracePrefix = $env:MPT_DEV_TIMING_TRACE_PATH
            startedAt = $startedAt.ToString('O'); actualScriptExitUtc = $dev.actualExitUtc; scriptMilliseconds = ($scriptEndTicks - $startTicks) * $scale
            visibleReadyMilliseconds = ([long]$ready.observedTimestampTicks - $startTicks) * $scale
            endToEndMilliseconds = ([Math]::Max($scriptEndTicks, [long]$ready.observedTimestampTicks) - $startTicks) * $scale
            nativeMilliseconds = $timings.totalMilliseconds; oldProcesses = $oldProcesses; newProcesses = $newProcesses
            visibleShellProcessId = $ready.processId; command = @($pwsh) + $arguments
        }
        $rows.Add($row)
        Save-Json $row (Join-Path $sample 'result.json')
        Save-Json $rows.ToArray() (Join-Path $report 'results.json')
        Write-Host "$label ready: $([math]::Round($row.endToEndMilliseconds / 1000, 3)) seconds."
    } finally {
        foreach ($job in @($dev, $watcher)) {
            if ($job -and -not $job.process.HasExited) { try { $job.process.Kill($true) } catch {} }
        }
        $timingPath = Join-Path $source 'artifacts\dev-update\last-timings.json'
        if (Test-Path -LiteralPath $timingPath) { Copy-Item -LiteralPath $timingPath -Destination (Join-Path $sample 'native-timings.json') -Force }
    }
}

try {
    Assert-CleanRunner
    $release = Invoke-RestMethod 'https://api.github.com/repos/dqtz5vpvj9-create/MyPowerTools/releases/tags/v0.3.26' -TimeoutSec 60
    if ($release.tag_name -ne 'v0.3.26' -or $release.draft) { throw 'Unexpected release metadata.' }
    $downloads = Join-Path $work 'download'
    New-Item -ItemType Directory -Force $downloads | Out-Null
    foreach ($name in @('MyPowerTools-win-x64.zip', 'MyPowerTools-win-x64.zip.sha256')) {
        $asset = @($release.assets | Where-Object name -eq $name)
        if ($asset.Count -ne 1 -or $asset[0].browser_download_url -notlike 'https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/download/v0.3.26/*') { throw "Unverified release asset: $name" }
        Invoke-WebRequest -Uri $asset[0].browser_download_url -OutFile (Join-Path $downloads $name) -TimeoutSec 300
    }
    $zip = Join-Path $downloads 'MyPowerTools-win-x64.zip'
    $checksum = Get-Content -LiteralPath "$zip.sha256" -Raw
    if ($checksum -notmatch '(?i)\b[0-9a-f]{64}\b') { throw 'Paired SHA256 file has no digest.' }
    $expectedHash = $Matches[0].ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) { throw 'Full release ZIP checksum mismatch.' }
    Save-Json ([ordered]@{ tag = $release.tag_name; releaseUrl = $release.html_url; publishedAt = $release.published_at; sha256 = $actualHash; assets = @($release.assets | Where-Object name -in @('MyPowerTools-win-x64.zip', 'MyPowerTools-win-x64.zip.sha256') | Select-Object name, id, size, browser_download_url); baseline = $baselineCommit; candidate = $CandidateCommit; sourcePath = $source; installPath = $install; dataPath = $data; dependencyCaches = 'shared NuGet/HTTP and OS caches; same source/install paths; build outputs reset before each arm, then retained from that arm warm-up for its measured no-edit sample'; sampling = 'baseline warm-up, baseline measured, candidate warm-up, candidate measured; warm-ups recorded but excluded from the comparison; one measured sample per arm is directional evidence, not a variance estimate'; timingTrace = 'MPT_DEV_TIMING_TRACE=1; per-sample dedicated timing prefix; official Dev stdout/stderr also captured'; readiness = 'max(official script success, new visible responsive populated Home + live trace); external integrations may be offline' }) (Join-Path $report 'conditions.json')
    Copy-Item -LiteralPath "$zip.sha256" -Destination (Join-Path $report 'official-zip.sha256')
    $package = Join-Path $work 'package'
    Expand-Archive -LiteralPath $zip -DestinationPath $package
    if (-not (Test-Path -LiteralPath (Join-Path $package 'install-windows.ps1'))) { throw 'Expected official portable installer at ZIP root.' }
    $env:LOCALAPPDATA = $local; $env:APPDATA = $roaming; $env:MPT_DATA_ROOT = $data
    $arms = [ordered]@{ baseline = $baselineCommit; candidate = $CandidateCommit }
    foreach ($arm in $arms.GetEnumerator()) {
        Stop-OwnInstallation
        Remove-OwnDirectory $local $work
        Remove-OwnDirectory $roaming $work
        New-Item -ItemType Directory -Force $local, $roaming | Out-Null
        & git -C $source checkout --detach $arm.Value
        if ($LASTEXITCODE -ne 0 -or ((& git -C $source rev-parse HEAD).Trim() -ne $arm.Value)) { throw 'Source commit selection failed.' }
        Reset-BuildOutputs
        $setupReport = Join-Path $report ($arm.Key + '-setup')
        New-Item -ItemType Directory -Force $setupReport | Out-Null
        Initialize-E2ESafety -PackageRoot $package -DataRoot $data -ReportRoot $setupReport
        $script:activeArm = $arm.Key
        $setupWatch = [Diagnostics.Stopwatch]::StartNew()
        $installerArguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $package 'install-windows.ps1'), '-PackageRoot', $package,
            '-NoStartMenuShortcut', '-NoDesktopShortcut', '-NoAutostart', '-NoStartRunner', '-NoOpenApp')
        $installer = Start-Captured $pwsh $installerArguments $package (Join-Path $setupReport 'official-install')
        Complete-Captured $installer (Join-Path $setupReport 'official-install') 600
        $setupWatch.Stop()
        Save-Json ([ordered]@{ milliseconds = $setupWatch.Elapsed.TotalMilliseconds; command = @($pwsh) + $installerArguments; note = 'Official setup outside Dev timings; ServiceManager configuration still runs.' }) (Join-Path $setupReport 'installer.json')
        # Each arm pays its first restore/build/startup cost outside the comparison.
        # The next sample reuses that arm's outputs and performs the same real restart.
        Invoke-DevSample $arm.Key $arm.Value 0
        Invoke-DevSample $arm.Key $arm.Value 1
        if (@(& git -C $source status --porcelain --untracked-files=no).Count) { throw 'Official source tree was unexpectedly modified.' }
    }
} catch {
    Save-Json ([ordered]@{ status = 'BLOCKED'; message = $_.Exception.Message; at = [DateTime]::UtcNow.ToString('O'); completedSamples = $rows.Count }) (Join-Path $report 'failure.json')
    throw
} finally {
    try { Stop-OwnInstallation } catch { $cleanup.Add("CLEANUP BLOCKED: $($_.Exception.Message)"); Write-Warning $_.Exception.Message }
    Save-Json ([ordered]@{ actions = $cleanup.ToArray(); remainingInstalledProcesses = @(Get-InstalledProcesses) }) (Join-Path $report 'cleanup.json')
    foreach ($name in $originalEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process') }
}
