[CmdletBinding()]
param(
    [string]$UpstreamExecutable,
    [string]$ManagedExecutable,
    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
$toolRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $toolRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($UpstreamExecutable)) {
    $UpstreamExecutable = Join-Path $repoRoot 'artifacts\nssm-compat\upstream\expanded\nssm-2.24-101-g897c7ad\win64\nssm.exe'
}
if ([string]::IsNullOrWhiteSpace($ManagedExecutable)) {
    $ManagedExecutable = Join-Path $toolRoot 'sdk-tool\src\NssmManager.Executable\publish\win-x64\nssm-manager.exe'
}
$UpstreamExecutable = (Resolve-Path -LiteralPath $UpstreamExecutable).Path
$ManagedExecutable = (Resolve-Path -LiteralPath $ManagedExecutable).Path

function Invoke-CapturedProcess {
    param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string[]]$Arguments)
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        return [ordered]@{ exitCode = $process.ExitCode; stdout = $stdout; stderr = $stderr }
    }
    finally { $process.Dispose() }
}

$settings = @(
    'Application', 'AppParameters', 'AppDirectory', 'AppAffinity', 'AppEnvironment', 'AppEnvironmentExtra',
    'AppNoConsole', 'AppPriority', 'AppRestartDelay', 'AppStdin', 'AppStdinShareMode',
    'AppStdinCreationDisposition', 'AppStdinFlagsAndAttributes', 'AppStdout', 'AppStdoutShareMode',
    'AppStdoutCreationDisposition', 'AppStdoutFlagsAndAttributes', 'AppStdoutCopyAndTruncate', 'AppStderr',
    'AppStderrShareMode', 'AppStderrCreationDisposition', 'AppStderrFlagsAndAttributes',
    'AppStderrCopyAndTruncate', 'AppStopMethodSkip', 'AppStopMethodConsole', 'AppStopMethodWindow',
    'AppStopMethodThreads', 'AppKillProcessTree', 'AppThrottle', 'AppRedirectHook', 'AppRotateFiles',
    'AppRotateOnline', 'AppRotateSeconds', 'AppRotateBytes', 'AppRotateBytesHigh', 'AppRotateDelay',
    'AppTimestampLog', 'DependOnGroup', 'DependOnService', 'Description', 'DisplayName', 'Environment',
    'ImagePath', 'ObjectName', 'Name', 'Start', 'Type'
)
$hooks = @('Exit/Post', 'Power/Change', 'Power/Resume', 'Rotate/Pre', 'Rotate/Post', 'Start/Pre', 'Start/Post', 'Stop/Pre')
$upstreamList = Invoke-CapturedProcess -Executable $UpstreamExecutable -Arguments @('list')
$managedList = Invoke-CapturedProcess -Executable $ManagedExecutable -Arguments @('list')
$services = @($upstreamList.stdout -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$results = [Collections.Generic.List[object]]::new()

function Add-DifferentialCase {
    param([string[]]$Arguments)
    $upstream = Invoke-CapturedProcess -Executable $UpstreamExecutable -Arguments $Arguments
    $managed = Invoke-CapturedProcess -Executable $ManagedExecutable -Arguments $Arguments
    $knownLsaDiagnostic = $managed.stderr.Length -eq 0 -and $upstream.stderr -match '^LsaOpenPolicy\(\): [^\r\n]+(?:\r|\n)+$'
    $results.Add([pscustomobject][ordered]@{
        arguments = $Arguments
        match = $upstream.exitCode -eq $managed.exitCode -and $upstream.stdout -ceq $managed.stdout -and ($upstream.stderr -ceq $managed.stderr -or $knownLsaDiagnostic)
        exactStderrMatch = $upstream.stderr -ceq $managed.stderr
        knownUpstreamLsaDiagnostic = $knownLsaDiagnostic
        upstream = $upstream
        managed = $managed
    })
}

foreach ($versionArguments in @(
    @('version'), @('/version'), @('-v'), @('-V'), @('-version'), @('--version')
)) { Add-DifferentialCase -Arguments $versionArguments }
Add-DifferentialCase -Arguments @('list')
Add-DifferentialCase -Arguments @('list', 'all')
$missingService = 'NssmManagerMissing_' + [Guid]::NewGuid().ToString('N')
Add-DifferentialCase -Arguments @('status', $missingService)
Add-DifferentialCase -Arguments @('statuscode', $missingService)
Add-DifferentialCase -Arguments @('processes', $missingService)
Add-DifferentialCase -Arguments @('get', $missingService, 'Application')
$nativeService = @((Invoke-CapturedProcess -Executable $UpstreamExecutable -Arguments @('list', 'all')).stdout -split '\r?\n' |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $services -notcontains $_ } |
    Select-Object -First 1)
if ($nativeService.Count -gt 0) {
    Add-DifferentialCase -Arguments @('get', $nativeService[0], 'Name')
    Add-DifferentialCase -Arguments @('get', $nativeService[0], 'Application')
}
foreach ($service in $services) {
    Add-DifferentialCase -Arguments @('status', $service)
    Add-DifferentialCase -Arguments @('statuscode', $service)
    Add-DifferentialCase -Arguments @('get', $service, 'AppExit', 'Default')
    foreach ($hook in $hooks) { Add-DifferentialCase -Arguments @('get', $service, 'AppEvents', $hook) }
    foreach ($setting in $settings) { Add-DifferentialCase -Arguments @('get', $service, $setting) }
}

$mismatches = @($results | Where-Object { -not $_.match })
$knownDiagnostics = @($results | Where-Object knownUpstreamLsaDiagnostic)
$document = [pscustomobject][ordered]@{
    schemaVersion = 1
    generatedAt = [DateTimeOffset]::UtcNow
    baseline = '2.24-101-g897c7ad'
    upstreamExecutable = $UpstreamExecutable
    managedExecutable = $ManagedExecutable
    serviceCount = $services.Count
    caseCount = $results.Count
    matchCount = $results.Count - $mismatches.Count
    mismatchCount = $mismatches.Count
    knownUpstreamLsaDiagnosticCount = $knownDiagnostics.Count
    mismatches = $mismatches
}
if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = [IO.Path]::GetFullPath($EvidencePath)
    $evidenceDirectory = Split-Path -Parent $EvidencePath
    if (-not (Test-Path -LiteralPath $evidenceDirectory)) { New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null }
    $document | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
}
$document | Select-Object serviceCount, caseCount, matchCount, mismatchCount, knownUpstreamLsaDiagnosticCount | Format-List
if ($mismatches.Count -gt 0) {
    $mismatches | Select-Object -First 20 arguments, upstream, managed | Format-List
    throw "$($mismatches.Count) NSSM read-only differential case(s) failed."
}
