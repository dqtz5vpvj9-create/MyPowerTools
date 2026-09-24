param(
    [switch] $RefreshPackageSignatures,
    [string] $ModulesRoot = '',
    [switch] $NoRestore,
    [switch] $NoBuild,
    [switch] $NoTest,
    [switch] $NoTemplateValidation
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $RepoRoot

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string[]] $ArgumentList
    )

    & $FilePath @ArgumentList
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$FilePath failed with exit code $exitCode"
    }
}

if (-not $NoRestore) {
    Invoke-Native 'dotnet' @('restore', 'MyPowerTools.slnx')
}
if (-not $NoBuild) {
    Invoke-Native 'dotnet' @('build', 'MyPowerTools.slnx', '--no-restore', '--maxcpucount')
}
if ([string]::IsNullOrWhiteSpace($ModulesRoot)) {
    $ModulesRoot = Join-Path $RepoRoot 'artifacts\smoke-modules'
    Invoke-Native 'pwsh.exe' @(
        '-NoLogo', '-NoProfile', '-NonInteractive',
        '-File', 'scripts\build-tool-packages.ps1',
        '-RepoRoot', $RepoRoot,
        '-OutputRoot', $ModulesRoot)
} else {
    $ModulesRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $ModulesRoot))
}
if ($RefreshPackageSignatures) {
    Invoke-Native 'dotnet' @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'package', 'sign-local', $ModulesRoot)
    Invoke-Native 'dotnet' @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'package', 'trust', $ModulesRoot, '--strict')
} else {
    Write-Host 'Skipping package signature refresh; pass -RefreshPackageSignatures to update local package signatures.'
    Write-Host 'Skipping strict package trust because solution build can refresh module binaries during UI-only validation.'
}
if (-not $NoTest) {
    Invoke-Native 'dotnet' @('test', 'MyPowerTools.slnx', '--no-build')
}
Invoke-Native 'dotnet' @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'validate', $ModulesRoot)
Invoke-Native 'dotnet' @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'validate', 'contracts', $ModulesRoot)
Invoke-Native 'dotnet' @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'ui', 'check', $ModulesRoot)
Invoke-Native 'dotnet' @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'ui', 'snapshot', $ModulesRoot, '--surface', 'dashboard-card', '--theme', 'light', '--size', '1366x768', '--density', 'normal', '--out', 'artifacts\ui-snapshots')
Invoke-Native 'dotnet' @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'ui', 'shell-snapshot', '--theme', 'light', '--size', '1366x768', '--density', 'normal', '--out', 'artifacts\shell-ui-snapshots')
Invoke-Native 'dotnet' @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'runner', 'autostart', 'status')
if (-not $NoTemplateValidation) {
    Invoke-Native 'pwsh.exe' @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', 'scripts\validate-templates.ps1')
}
Invoke-Native 'dotnet' @('run', '--no-build', '--project', 'src\MyPowerTools.Runner', '--', '--once', '--modules', $ModulesRoot)
Invoke-Native 'dotnet' @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'doctor')

# src/Directory.Build.props routes all src/ output through the artifacts layout,
# which names directories artifacts/build/bin/<project>/<lowercase configuration>.
$BuildBinRoot = Join-Path $RepoRoot 'artifacts\build\bin'
$RunnerExe = Join-Path $BuildBinRoot 'MyPowerTools.Runner\debug\MyPowerTools.Runner.exe'
$ShellExe = Join-Path $BuildBinRoot 'MyPowerTools.Shell.Avalonia\debug\MyPowerTools.Shell.Avalonia.exe'
$SmokeDataRoot = Join-Path $RepoRoot 'artifacts\smoke-data'

if (-not (Test-Path -LiteralPath $RunnerExe)) {
    throw "Runner executable was not found at $RunnerExe"
}

if (-not (Test-Path -LiteralPath $ShellExe)) {
    throw "Shell executable was not found at $ShellExe"
}

New-Item -ItemType Directory -Path $SmokeDataRoot -Force | Out-Null

$runnerProcess = $null
$previousMptDataRoot = $env:MPT_DATA_ROOT
try {
    $env:MPT_DATA_ROOT = $SmokeDataRoot
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $RunnerExe
    $startInfo.WorkingDirectory = $RepoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Environment['MPT_DATA_ROOT'] = $SmokeDataRoot
    foreach ($argument in @('--modules', $ModulesRoot, '--data-root', $SmokeDataRoot)) {
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

    # A cold Runner serves HostControl only after it has probed every enabled module,
    # and the dashboard RPC refreshes every module's health serially. On a headless CI
    # runner the module services are unreachable, so each probe runs to its own timeout:
    # `Runner --once` over the same modules (the same work) takes 42-48 s there.
    # 30 s could never cover that; 120 s leaves headroom without hiding a hang.
    $shellSmokeTimeoutMs = 120000
    $shellSmokeArguments = @('--smoke', '--timeout-ms', "$shellSmokeTimeoutMs")
    $startedOwnRunner = -not $runnerProcess.HasExited
    if ($startedOwnRunner) {
        $shellSmokeArguments += '--quit-runner'
    } else {
        Write-Host "Runner exited immediately with code $($runnerProcess.ExitCode); smoke uses the Runner that is already running."
    }

    # The Shell is a WinExe (GUI subsystem). `& $ShellExe` does not wait for a GUI
    # process and leaves $LASTEXITCODE untouched, so the old call returned at once and
    # the QuitRunner wait below started while the Shell was still waiting for the
    # Runner. Start it explicitly, redirect its output into this log and wait for it.
    $shellStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $shellStartInfo.FileName = $ShellExe
    $shellStartInfo.WorkingDirectory = $RepoRoot
    $shellStartInfo.UseShellExecute = $false
    $shellStartInfo.CreateNoWindow = $true
    $shellStartInfo.RedirectStandardOutput = $true
    $shellStartInfo.RedirectStandardError = $true
    $shellStartInfo.Environment['MPT_DATA_ROOT'] = $SmokeDataRoot
    foreach ($argument in $shellSmokeArguments) {
        $shellStartInfo.ArgumentList.Add($argument)
    }

    $shellProcess = [System.Diagnostics.Process]::Start($shellStartInfo)
    if ($null -eq $shellProcess) {
        throw 'Shell smoke process did not start.'
    }

    try {
        $shellStdout = $shellProcess.StandardOutput.ReadToEndAsync()
        $shellStderr = $shellProcess.StandardError.ReadToEndAsync()
        if (-not $shellProcess.WaitForExit($shellSmokeTimeoutMs + 30000)) {
            $shellProcess.Kill($true)
            $shellProcess.WaitForExit()
            throw "Shell HostControl smoke did not finish within $(($shellSmokeTimeoutMs + 30000) / 1000) s."
        }

        $shellProcess.WaitForExit()
        $shellStdout.Result.TrimEnd() | Where-Object { $_ } | ForEach-Object { Write-Host $_ }
        $shellStderr.Result.TrimEnd() | Where-Object { $_ } | ForEach-Object { Write-Host $_ }
        if ($shellProcess.ExitCode -ne 0) {
            throw "Shell HostControl smoke failed with exit code $($shellProcess.ExitCode)"
        }
    } finally {
        $shellProcess.Dispose()
    }

    # Measured from QuitRunner (the Shell has exited by now): graceful HostControl
    # shutdown plus module host disposal, both bounded well below this in the Runner.
    if ($startedOwnRunner -and -not $runnerProcess.WaitForExit(10000)) {
        throw 'Runner did not exit after HostControl QuitRunner.'
    }
} finally {
    if ($null -eq $previousMptDataRoot) {
        Remove-Item Env:\MPT_DATA_ROOT -ErrorAction SilentlyContinue
    } else {
        $env:MPT_DATA_ROOT = $previousMptDataRoot
    }

    if ($null -ne $runnerProcess -and -not $runnerProcess.HasExited) {
        $runnerProcess.Kill($true)
        $runnerProcess.WaitForExit()
        $runnerProcess.Dispose()
    }
}

Write-Host 'MyPowerTools smoke test passed.'
