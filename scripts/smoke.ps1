$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $RepoRoot

dotnet restore MyPowerTools.slnx
dotnet build MyPowerTools.slnx --no-restore
dotnet run --project src\MyPowerTools.Cli -- package sign-local modules
dotnet test MyPowerTools.slnx --no-build
dotnet run --project src\MyPowerTools.Cli -- validate modules
dotnet run --project src\MyPowerTools.Cli -- validate contracts
dotnet run --project src\MyPowerTools.Cli -- package trust modules --strict
dotnet run --project src\MyPowerTools.Cli -- ui check modules
dotnet run --project src\MyPowerTools.Cli -- ui snapshot --surface dashboard-card --theme light --size 1366x768 --density normal --out artifacts\ui-snapshots
dotnet run --project src\MyPowerTools.Cli -- ui shell-snapshot --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots
dotnet run --project src\MyPowerTools.Cli -- runner autostart status
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1
dotnet run --project src\MyPowerTools.Runner -- --once
dotnet run --project src\MyPowerTools.Cli -- doctor

$RunnerExe = Join-Path $RepoRoot 'src\MyPowerTools.Runner\bin\Debug\net10.0\MyPowerTools.Runner.exe'
$ShellExe = Join-Path $RepoRoot 'src\MyPowerTools.Shell.Avalonia\bin\Debug\net10.0\MyPowerTools.Shell.Avalonia.exe'
$SmokeDataRoot = Join-Path $RepoRoot 'artifacts\smoke-data'

if (-not (Test-Path -LiteralPath $RunnerExe)) {
    throw "Runner executable was not found at $RunnerExe"
}

if (-not (Test-Path -LiteralPath $ShellExe)) {
    throw "Shell executable was not found at $ShellExe"
}

New-Item -ItemType Directory -Path $SmokeDataRoot -Force | Out-Null

$runnerProcess = $null
try {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $RunnerExe
    $startInfo.WorkingDirectory = $RepoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in @('--modules', (Join-Path $RepoRoot 'modules'), '--data-root', $SmokeDataRoot)) {
        $startInfo.ArgumentList.Add($argument)
    }

    $runnerProcess = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $runnerProcess) {
        throw 'Runner process did not start.'
    }

    Start-Sleep -Milliseconds 500
    if ($runnerProcess.HasExited -and $runnerProcess.ExitCode -notin @(0, 2)) {
        throw "Runner exited early with code $($runnerProcess.ExitCode)"
    }

    $shellSmokeArguments = @('--smoke', '--timeout-ms', '30000')
    $startedOwnRunner = -not $runnerProcess.HasExited
    if ($startedOwnRunner) {
        $shellSmokeArguments += '--quit-runner'
    }

    & $ShellExe @shellSmokeArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Shell HostControl smoke failed with exit code $LASTEXITCODE"
    }

    if ($startedOwnRunner -and -not $runnerProcess.WaitForExit(10000)) {
        throw 'Runner did not exit after HostControl QuitRunner.'
    }
} finally {
    if ($null -ne $runnerProcess -and -not $runnerProcess.HasExited) {
        $runnerProcess.Kill($true)
        $runnerProcess.WaitForExit()
        $runnerProcess.Dispose()
    }
}

Write-Host 'MyPowerTools smoke test passed.'
