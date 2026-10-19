# Temporary PR-only evidence. This harness never changes the official Dev entry point.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$source = [IO.Path]::GetFullPath($SourceRoot)
$work = Join-Path $env:RUNNER_TEMP 'mpt-dev-e2e'
$report = Join-Path $env:RUNNER_TEMP 'dev-e2e-report'
$local = Join-Path $work 'profile\Local'
$roaming = Join-Path $work 'profile\Roaming'
$install = Join-Path $local 'Programs\MyPowerTools'
$data = Join-Path $local 'MyPowerTools'
$rows = [Collections.Generic.List[object]]::new()
$cleanup = [Collections.Generic.List[string]]::new()
$pwsh = (Get-Command pwsh.exe -CommandType Application).Source
$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$originalEnvironment = @{}
foreach ($name in @('LOCALAPPDATA', 'APPDATA', 'MPT_DATA_ROOT', 'MPT_SHELL_STARTUP_TRACE')) {
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
function Stop-OwnInstallation {
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
}
function Start-Captured([string]$File, [string[]]$Arguments, [string]$Directory) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $File; $info.WorkingDirectory = $Directory
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    [pscustomobject]@{ process = $process; stdout = $process.StandardOutput.ReadToEndAsync(); stderr = $process.StandardError.ReadToEndAsync() }
}
function Complete-Captured($Job, [string]$LogPrefix, [int]$TimeoutSeconds = 1800) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $Job.process.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -ge $deadline) {
            try { $Job.process.Kill($true) } catch {}
            throw "Timed out: $LogPrefix"
        }
    }
    $stdout = $Job.stdout.GetAwaiter().GetResult()
    $stderr = $Job.stderr.GetAwaiter().GetResult()
    $stdout | Set-Content -LiteralPath "$LogPrefix.stdout.log" -Encoding utf8
    $stderr | Set-Content -LiteralPath "$LogPrefix.stderr.log" -Encoding utf8
    if ($Job.process.ExitCode -ne 0) {
        Write-Host $stdout; Write-Host $stderr
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
    $startedAt = [DateTime]::UtcNow
    $watcherArguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'dev-e2e-readiness.ps1'),
        '-ExpectedShellPath', (Join-Path $install 'Shell\MyPowerTools.Shell.Avalonia.exe'), '-TracePath', $trace,
        '-OutputPath', (Join-Path $sample 'visible-home.json'), '-ScreenshotPath', (Join-Path $sample 'visible-home.png'),
        '-StartedAfterUtc', $startedAt.ToString('O'))
    if ($oldShellIds.Count) { $watcherArguments += @('-RejectedProcessIds', ($oldShellIds -join ',')) }
    $watcher = Start-Captured $windowsPowerShell $watcherArguments $source
    $dev = $null
    try {
        $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $source 'scripts\Start-MyPowerTools-Dev.ps1'))
        Write-Host "Starting ${label}: default official Core/Debug Dev loop, including restart and visible Home."
        $startTicks = [Diagnostics.Stopwatch]::GetTimestamp()
        $dev = Start-Captured $pwsh $arguments $source
        Complete-Captured $dev (Join-Path $sample 'official-dev')
        $scriptEndTicks = [Diagnostics.Stopwatch]::GetTimestamp()
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
            scenario = $(if ($Iteration -eq 0) { 'initial-core-rebuild/setup-state; not cold dependencies' } else { 'warm-no-edit' })
            startedAt = $startedAt.ToString('O'); scriptMilliseconds = ($scriptEndTicks - $startTicks) * $scale
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
    $release = Invoke-RestMethod 'https://api.github.com/repos/dqtz5vpvj9-create/MyPowerTools/releases/tags/v0.3.26'
    if ($release.tag_name -ne 'v0.3.26' -or $release.draft) { throw 'Unexpected release metadata.' }
    $downloads = Join-Path $work 'download'
    New-Item -ItemType Directory -Force $downloads | Out-Null
    foreach ($name in @('MyPowerTools-win-x64.zip', 'MyPowerTools-win-x64.zip.sha256')) {
        $asset = @($release.assets | Where-Object name -eq $name)
        if ($asset.Count -ne 1 -or $asset[0].browser_download_url -notlike 'https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/download/v0.3.26/*') { throw "Unverified release asset: $name" }
        Invoke-WebRequest -Uri $asset[0].browser_download_url -OutFile (Join-Path $downloads $name)
    }
    $zip = Join-Path $downloads 'MyPowerTools-win-x64.zip'
    $checksum = Get-Content -LiteralPath "$zip.sha256" -Raw
    if ($checksum -notmatch '(?i)\b[0-9a-f]{64}\b') { throw 'Paired SHA256 file has no digest.' }
    $expectedHash = $Matches[0].ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) { throw 'Full release ZIP checksum mismatch.' }
    Save-Json ([ordered]@{ tag = $release.tag_name; releaseUrl = $release.html_url; publishedAt = $release.published_at; sha256 = $actualHash; assets = @($release.assets | Where-Object name -in @('MyPowerTools-win-x64.zip', 'MyPowerTools-win-x64.zip.sha256') | Select-Object name, id, size, browser_download_url); baseline = 'd1e852bed838c28d78d23c950568d6af338ca15b'; candidate = '5bcd10011e83a53ab6761b1e142917264f8a8f74'; sourcePath = $source; installPath = $install; dataPath = $data; dependencyCaches = 'shared NuGet/HTTP and OS caches; build outputs reset before each arm'; readiness = 'max(official script success, new visible responsive populated Home + live trace); external integrations may be offline' }) (Join-Path $report 'conditions.json')
    Copy-Item -LiteralPath "$zip.sha256" -Destination (Join-Path $report 'official-zip.sha256')
    $package = Join-Path $work 'package'
    Expand-Archive -LiteralPath $zip -DestinationPath $package
    if (-not (Test-Path -LiteralPath (Join-Path $package 'install-windows.ps1'))) { throw 'Expected official portable installer at ZIP root.' }
    $env:LOCALAPPDATA = $local; $env:APPDATA = $roaming; $env:MPT_DATA_ROOT = $data
    $arms = [ordered]@{ baseline = 'd1e852bed838c28d78d23c950568d6af338ca15b'; candidate = '5bcd10011e83a53ab6761b1e142917264f8a8f74' }
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
        $setupWatch = [Diagnostics.Stopwatch]::StartNew()
        $installerArguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $package 'install-windows.ps1'), '-PackageRoot', $package,
            '-NoStartMenuShortcut', '-NoDesktopShortcut', '-NoAutostart', '-NoStartRunner', '-NoOpenApp')
        $installer = Start-Captured $pwsh $installerArguments $package
        Complete-Captured $installer (Join-Path $setupReport 'official-install') 900
        $setupWatch.Stop()
        Save-Json ([ordered]@{ milliseconds = $setupWatch.Elapsed.TotalMilliseconds; command = @($pwsh) + $installerArguments; note = 'Official setup outside Dev timings; ServiceManager configuration still runs.' }) (Join-Path $setupReport 'installer.json')
        foreach ($iteration in 0..2) { Invoke-DevSample $arm.Key $arm.Value $iteration }
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
