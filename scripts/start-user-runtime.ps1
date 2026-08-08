[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [switch]$StartRunner
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function ConvertTo-WindowsCommandLineArgument {
    param([AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
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

$installRootFull = [IO.Path]::GetFullPath($InstallRoot)
$canonicalInstallRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'))
$dataRootFull = [IO.Path]::GetFullPath($DataRoot)
$sessionId = (Get-Process -Id $PID).SessionId
$dotnetRoot = Join-Path $installRootFull 'Runtime\dotnet'
[Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotnetRoot, 'Process')

if (-not $installRootFull.Equals($canonicalInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "MyPowerTools runtime requires the canonical install root $canonicalInstallRoot. InstallRoot=$installRootFull"
}
if ($sessionId -eq 0) {
    throw 'MyPowerTools runtime launch is blocked in Windows Session 0.'
}

$runnerExe = Join-Path $installRootFull 'Runner\MyPowerTools.Runner.exe'
$modulesRoot = Join-Path $installRootFull 'modules'
$configurationScript = Join-Path $installRootFull 'configure-user-services.ps1'
foreach ($requiredPath in @($runnerExe, $modulesRoot, $configurationScript)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Installed runtime component is missing: $requiredPath"
    }
}

$runnerStarted = $false
if ($StartRunner) {
    $existingRunner = Get-Process -Name 'MyPowerTools.Runner' -ErrorAction SilentlyContinue |
        Where-Object {
            if ($_.SessionId -ne $sessionId) {
                return $false
            }
            $processPath = ''
            try {
                $processPath = $_.MainModule.FileName
            } catch {
            }
            return $runnerExe.Equals(
                $processPath,
                [StringComparison]::OrdinalIgnoreCase)
        } |
        Select-Object -First 1
    if ($null -eq $existingRunner) {
        $startInfo = New-Object Diagnostics.ProcessStartInfo
        $startInfo.FileName = $runnerExe
        $startInfo.WorkingDirectory = $installRootFull
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.Arguments = (@('--modules', $modulesRoot, '--data-root', $dataRootFull) |
            ForEach-Object { ConvertTo-WindowsCommandLineArgument -Value $_ }) -join ' '
        $runnerProcess = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $runnerProcess) {
            throw 'MyPowerTools Runner failed to start.'
        }
        $runnerStarted = $true
    }
}

& $configurationScript `
    -Mode Install `
    -InstallRoot $installRootFull `
    -DataRoot $dataRootFull | Out-Null

[pscustomobject]@{
    InstallRoot = $installRootFull
    DataRoot = $dataRootFull
    SessionId = $sessionId
    UserName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    RunnerRequested = $StartRunner.IsPresent
    RunnerStarted = $runnerStarted
    ServicesConfigured = $true
} | ConvertTo-Json -Depth 4
