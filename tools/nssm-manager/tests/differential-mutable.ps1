[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$UpstreamExecutable,
    [Parameter(Mandatory)][string]$ManagedExecutable,
    [string]$EvidencePath,
    [switch]$ConfirmIsolatedScmMutation
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The mutable NSSM differential requires Windows.' }
if (-not $ConfirmIsolatedScmMutation) { throw 'Pass -ConfirmIsolatedScmMutation to create and delete isolated temporary services.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this differential from an elevated terminal.'
}
$UpstreamExecutable = (Resolve-Path -LiteralPath $UpstreamExecutable).Path
$ManagedExecutable = (Resolve-Path -LiteralPath $ManagedExecutable).Path
$serviceName = 'NssmManagerDiff_' + [Guid]::NewGuid().ToString('N')
$application = $env:ComSpec
$working = Join-Path ([IO.Path]::GetTempPath()) $serviceName
[IO.Directory]::CreateDirectory($working) | Out-Null

function Invoke-CapturedProcess {
    param([string]$Executable, [string[]]$Arguments)
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $Arguments) { [void]$info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        [ordered]@{ exitCode = $process.ExitCode; stdout = $stdout; stderr = $stderr }
    }
    finally { $process.Dispose() }
}

function Get-RegistryKeySnapshot {
    param([string]$Path, [string]$RelativePath = '')
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($Path, $false)
    if ($null -eq $key) { return @() }
    try {
        $values = foreach ($name in @($key.GetValueNames() | Sort-Object)) {
            $kind = $key.GetValueKind($name)
            $value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            if ($RelativePath -eq '' -and $name -eq 'ImagePath') { $value = '<HOST_EXECUTABLE>' }
            [ordered]@{ name = $name; kind = $kind.ToString(); value = $value }
        }
        $result = [Collections.Generic.List[object]]::new()
        $result.Add([ordered]@{ path = $RelativePath; values = @($values) })
        foreach ($child in @($key.GetSubKeyNames() | Sort-Object)) {
            $childRelative = if ($RelativePath) { $RelativePath + '\' + $child } else { $child }
            foreach ($entry in Get-RegistryKeySnapshot -Path ($Path + '\' + $child) -RelativePath $childRelative) { $result.Add($entry) }
        }
        return $result.ToArray()
    }
    finally { $key.Dispose() }
}

function Remove-TestService {
    & sc.exe stop $serviceName 2>$null | Out-Null
    & sc.exe delete $serviceName 2>$null | Out-Null
    for ($index = 0; $index -lt 50; $index++) {
        if (-not (Test-Path -LiteralPath "Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\$serviceName")) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Temporary service '$serviceName' could not be removed."
}

$mutations = @(
    @('set', $serviceName, 'Application', $application),
    @('set', $serviceName, 'AppParameters', '/d /s /c "exit 0"'),
    @('set', $serviceName, 'AppDirectory', $working),
    @('set', $serviceName, 'AppExit', 'Default', 'Exit'),
    @('set', $serviceName, 'AppExit', '37', 'Ignore'),
    @('set', $serviceName, 'AppEvents', 'Start/Pre', "$application /d /s /c exit 0"),
    @('set', $serviceName, 'AppAffinity', '0'),
    @('set', $serviceName, 'AppEnvironment', ':NSSM_DIFF_A=one'),
    @('set', $serviceName, 'AppEnvironmentExtra', ':NSSM_DIFF_B=two'),
    @('set', $serviceName, 'AppNoConsole', '1'),
    @('set', $serviceName, 'AppPriority', 'HIGH_PRIORITY_CLASS'),
    @('set', $serviceName, 'AppRestartDelay', '1250'),
    @('set', $serviceName, 'AppStdin', 'NUL'),
    @('set', $serviceName, 'AppStdinShareMode', '3'),
    @('set', $serviceName, 'AppStdinCreationDisposition', '3'),
    @('set', $serviceName, 'AppStdinFlagsAndAttributes', '128'),
    @('set', $serviceName, 'AppStdout', (Join-Path $working 'stdout.log')),
    @('set', $serviceName, 'AppStdoutShareMode', '7'),
    @('set', $serviceName, 'AppStdoutCreationDisposition', '4'),
    @('set', $serviceName, 'AppStdoutFlagsAndAttributes', '128'),
    @('set', $serviceName, 'AppStdoutCopyAndTruncate', '1'),
    @('set', $serviceName, 'AppStderr', (Join-Path $working 'stderr.log')),
    @('set', $serviceName, 'AppStderrShareMode', '7'),
    @('set', $serviceName, 'AppStderrCreationDisposition', '4'),
    @('set', $serviceName, 'AppStderrFlagsAndAttributes', '128'),
    @('set', $serviceName, 'AppStderrCopyAndTruncate', '1'),
    @('set', $serviceName, 'AppStopMethodSkip', '2'),
    @('set', $serviceName, 'AppStopMethodConsole', '1000'),
    @('set', $serviceName, 'AppStopMethodWindow', '1100'),
    @('set', $serviceName, 'AppStopMethodThreads', '1200'),
    @('set', $serviceName, 'AppKillProcessTree', '1'),
    @('set', $serviceName, 'AppThrottle', '1400'),
    @('set', $serviceName, 'AppRedirectHook', '1'),
    @('set', $serviceName, 'AppRotateFiles', '1'),
    @('set', $serviceName, 'AppRotateOnline', '1'),
    @('set', $serviceName, 'AppRotateSeconds', '60'),
    @('set', $serviceName, 'AppRotateBytes', '4294967295'),
    @('set', $serviceName, 'AppRotateBytesHigh', '1'),
    @('set', $serviceName, 'AppRotateDelay', '25'),
    @('set', $serviceName, 'AppTimestampLog', '1'),
    @('set', $serviceName, 'DependOnGroup', ':NetworkProvider'),
    @('set', $serviceName, 'DependOnService', ':EventLog'),
    @('set', $serviceName, 'Description', 'NSSM differential service'),
    @('set', $serviceName, 'DisplayName', 'NSSM differential display'),
    @('set', $serviceName, 'Environment', ':NSSM_NATIVE_DIFF=one'),
    @('set', $serviceName, 'ObjectName', 'LocalSystem'),
    @('set', $serviceName, 'Start', 'SERVICE_DEMAND_START'),
    @('set', $serviceName, 'Type', 'SERVICE_WIN32_OWN_PROCESS'),
    @('reset', $serviceName, 'AppRestartDelay'),
    @('reset', $serviceName, 'AppEvents', 'Start/Pre'),
    @('reset', $serviceName, 'AppExit', '37')
)

function Invoke-ImplementationScenario {
    param([string]$Executable)
    function Invoke-Normalized {
        param([string[]]$Arguments)
        $captured = Invoke-CapturedProcess $Executable $Arguments
        $captured.stdout = $captured.stdout.Replace($Executable, '<HOST_EXECUTABLE>', [StringComparison]::OrdinalIgnoreCase)
        $captured.stderr = $captured.stderr.Replace($Executable, '<HOST_EXECUTABLE>', [StringComparison]::OrdinalIgnoreCase)
        return $captured
    }
    Remove-TestService
    $steps = [Collections.Generic.List[object]]::new()
    try {
        $install = Invoke-Normalized @('install', $serviceName, $application, '/d', '/s', '/c', 'exit 0')
        $steps.Add([ordered]@{ arguments = @('install'); process = $install; registry = @(Get-RegistryKeySnapshot "SYSTEM\CurrentControlSet\Services\$serviceName") })
        foreach ($arguments in $mutations) {
            $process = Invoke-Normalized $arguments
            $steps.Add([ordered]@{ arguments = @($arguments); process = $process; registry = @(Get-RegistryKeySnapshot "SYSTEM\CurrentControlSet\Services\$serviceName") })
        }
        $dump = Invoke-Normalized @('dump', $serviceName)
        $steps.Add([ordered]@{ arguments = @('dump'); process = $dump; registry = @(Get-RegistryKeySnapshot "SYSTEM\CurrentControlSet\Services\$serviceName") })
        $remove = Invoke-Normalized @('remove', $serviceName, 'confirm')
        $steps.Add([ordered]@{ arguments = @('remove'); process = $remove; registry = @() })
        return $steps.ToArray()
    }
    finally { Remove-TestService }
}

try {
    $upstream = Invoke-ImplementationScenario $UpstreamExecutable
    $managed = Invoke-ImplementationScenario $ManagedExecutable
    $upstreamJson = $upstream | ConvertTo-Json -Depth 20 -Compress
    $managedJson = $managed | ConvertTo-Json -Depth 20 -Compress
    $matched = $upstreamJson -ceq $managedJson
    $document = [ordered]@{
        schemaVersion = 1
        generatedAt = [DateTimeOffset]::UtcNow
        baseline = '2.24-101-g897c7ad'
        serviceName = $serviceName
        matched = $matched
        upstream = $upstream
        managed = $managed
    }
    if ($EvidencePath) {
        $fullEvidencePath = [IO.Path]::GetFullPath($EvidencePath)
        [IO.Directory]::CreateDirectory((Split-Path -Parent $fullEvidencePath)) | Out-Null
        $document | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $fullEvidencePath -Encoding UTF8
    }
    if (-not $matched) { throw 'Mutable NSSM differential failed; inspect the evidence document for the first divergent step.' }
    [pscustomobject]@{ serviceName = $serviceName; stepCount = $upstream.Count; matched = $matched } | Format-List
}
finally {
    Remove-TestService
    Remove-Item -LiteralPath $working -Recurse -Force -ErrorAction SilentlyContinue
}
