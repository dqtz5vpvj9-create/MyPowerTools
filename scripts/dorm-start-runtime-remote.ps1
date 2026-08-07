[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [string]$ResultPath = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $ResultPath = Join-Path $env:TEMP 'mpt-runtime-start-result.json'
}
$logPath = Join-Path $env:TEMP 'mpt-runtime-start.log'
$phasePath = Join-Path $env:TEMP 'mpt-runtime-start-phase.txt'
$runEntryRepaired = $false
$expectedRunner = ''
$afterRepairValue = ''
$afterChildValue = ''
function Write-Phase {
    param([Parameter(Mandatory = $true)][string]$Value)

    [IO.File]::WriteAllText(
        $phasePath,
        "$Value`n$([DateTimeOffset]::UtcNow.ToString('O'))",
        [Text.UTF8Encoding]::new($false))
}

try {
    Write-Phase -Value 'begin'
    $installManifestPath = Join-Path $InstallRoot 'install.manifest.json'
    if (Test-Path -LiteralPath $installManifestPath -PathType Leaf) {
        $installManifest = Get-Content -LiteralPath $installManifestPath -Raw | ConvertFrom-Json
        if ([bool]$installManifest.autostart) {
            $runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
            $runnerExePath = Join-Path $InstallRoot 'Runner\MyPowerTools.Runner.exe'
            $modulesRootPath = Join-Path $InstallRoot 'modules'
            $expectedRunner = "`"$runnerExePath`" --modules `"$modulesRootPath`" --data-root `"$DataRoot`""
            $currentRunner = (Get-ItemProperty -LiteralPath $runKeyPath -Name 'MyPowerTools' -ErrorAction SilentlyContinue).MyPowerTools
            if ([string]::IsNullOrWhiteSpace([string]$currentRunner) -or
                [string]$currentRunner -ne $expectedRunner) {
                if (-not (Test-Path -LiteralPath $runKeyPath)) {
                    New-Item -Path $runKeyPath -Force | Out-Null
                }
                Set-ItemProperty -LiteralPath $runKeyPath -Name 'MyPowerTools' -Value $expectedRunner
                $runEntryRepaired = $true
            }
            $afterRepairValue = [string](Get-ItemProperty -LiteralPath $runKeyPath -Name 'MyPowerTools' -ErrorAction SilentlyContinue).MyPowerTools
        }
    }

    Write-Phase -Value 'launching-runtime'
    & (Join-Path $InstallRoot 'start-user-runtime.ps1') `
        -InstallRoot $InstallRoot `
        -DataRoot $DataRoot `
        -StartRunner *> $logPath
    Write-Phase -Value 'runtime-returned'
    $afterChildValue = [string](Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'MyPowerTools' -ErrorAction SilentlyContinue).MyPowerTools
    $result = [ordered]@{
        success = $true
        exitCode = $LASTEXITCODE
        sessionId = (Get-Process -Id $PID).SessionId
        user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        runEntryRepaired = $runEntryRepaired
        expectedRunner = $expectedRunner
        afterRepairValue = $afterRepairValue
        afterChildValue = $afterChildValue
        log = if (Test-Path -LiteralPath $logPath) {
            [IO.File]::ReadAllText($logPath, [Text.Encoding]::UTF8)
        } else {
            ''
        }
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
}
catch {
    $result = [ordered]@{
        success = $false
        error = $_.Exception.Message
        inner = if ($null -ne $_.Exception.InnerException) { $_.Exception.InnerException.Message } else { '' }
        errorType = $_.Exception.GetType().FullName
        stack = $_.ScriptStackTrace
        phase = if (Test-Path -LiteralPath $phasePath) {
            (Get-Content -LiteralPath $phasePath -Raw).Trim()
        } else {
            ''
        }
        log = if (Test-Path -LiteralPath $logPath) {
            [IO.File]::ReadAllText($logPath, [Text.Encoding]::UTF8)
        } else {
            ''
        }
        sessionId = (Get-Process -Id $PID).SessionId
        user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
}

try {
    [IO.File]::WriteAllText(
        $ResultPath,
        ($result | ConvertTo-Json -Depth 5),
        [Text.UTF8Encoding]::new($false))
}
catch {
    [IO.File]::WriteAllText(
        (Join-Path $env:TEMP 'mpt-runtime-start-result-fallback.json'),
        ($result | ConvertTo-Json -Depth 5),
        [Text.UTF8Encoding]::new($false))
}

if ([bool]$result.success) {
    exit 0
}
exit 1
