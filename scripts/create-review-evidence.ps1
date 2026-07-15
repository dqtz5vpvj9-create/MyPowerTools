param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $EvidenceRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\review-evidence'),
    [switch] $SkipCommandExecution,
    [switch] $RefreshPackageSignatures,
    [switch] $IncludeReleaseEvidence
)

$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
$EvidenceRoot = [System.IO.Path]::GetFullPath($EvidenceRoot)
$CommandOutputRoot = Join-Path $EvidenceRoot 'command-outputs'
$UiSnapshotRoot = Join-Path $EvidenceRoot 'ui-snapshots'
$ShellScreenshotRoot = Join-Path $EvidenceRoot 'shell-screenshots'
$ReviewRoot = Join-Path $RepoRoot 'artifacts\review'

Set-Location -LiteralPath $RepoRoot
New-Item -ItemType Directory -Path $EvidenceRoot, $CommandOutputRoot, $UiSnapshotRoot, $ShellScreenshotRoot, $ReviewRoot -Force | Out-Null

$script:CommandIndex = 0
$script:Results = [System.Collections.Generic.List[object]]::new()

function ConvertTo-SafeName {
    param([string] $Value)
    $safe = $Value -replace '[^A-Za-z0-9_.-]', '-'
    return $safe.Trim('-')
}

function Format-CommandLine {
    param(
        [string] $FilePath,
        [string[]] $ArgumentList
    )

    return ($FilePath + ' ' + ($ArgumentList -join ' ')).Trim()
}

function Invoke-EvidenceCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [string[]] $ArgumentList = @(),

        [int[]] $ExpectedExitCodes = @(0),

        [int] $TimeoutSeconds = 900,

        [hashtable] $Environment = @{}
    )

    $script:CommandIndex += 1
    $safeName = ConvertTo-SafeName $Name
    $outputPath = Join-Path $CommandOutputRoot ('{0:D2}-{1}.txt' -f $script:CommandIndex, $safeName)
    $startedAt = [DateTimeOffset]::UtcNow
    $commandLine = Format-CommandLine $FilePath $ArgumentList

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $RepoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $ArgumentList) {
        $startInfo.ArgumentList.Add($argument)
    }

    foreach ($key in $Environment.Keys) {
        $startInfo.Environment[$key] = [string] $Environment[$key]
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start command: $commandLine"
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try {
            $process.Kill($true)
        } catch {
        }

        throw "Command timed out after $TimeoutSeconds seconds: $commandLine"
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $finishedAt = [DateTimeOffset]::UtcNow
    $combined = @(
        "Command: $commandLine"
        "StartedAtUtc: $($startedAt.ToString('O'))"
        "FinishedAtUtc: $($finishedAt.ToString('O'))"
        "ExitCode: $($process.ExitCode)"
        ""
        "STDOUT:"
        $stdout
        ""
        "STDERR:"
        $stderr
    ) -join [Environment]::NewLine
    Set-Content -LiteralPath $outputPath -Value $combined -Encoding UTF8

    $result = [pscustomobject]@{
        name = $Name
        command = $commandLine
        exitCode = $process.ExitCode
        expectedExitCodes = $ExpectedExitCodes
        output = $outputPath
        startedAtUtc = $startedAt.ToString('O')
        finishedAtUtc = $finishedAt.ToString('O')
    }
    $script:Results.Add($result)

    if ($ExpectedExitCodes -notcontains $process.ExitCode) {
        throw "Command '$Name' failed with exit code $($process.ExitCode). Output: $outputPath"
    }

    return $result
}

function Add-SkippedEvidenceCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $Reason
    )

    $script:CommandIndex += 1
    $safeName = ConvertTo-SafeName $Name
    $outputPath = Join-Path $CommandOutputRoot ('{0:D2}-{1}.txt' -f $script:CommandIndex, $safeName)
    $now = [DateTimeOffset]::UtcNow
    $content = @(
        "Command: skipped"
        "StartedAtUtc: $($now.ToString('O'))"
        "FinishedAtUtc: $($now.ToString('O'))"
        "ExitCode: 0"
        ""
        "STDOUT:"
        $Reason
        ""
        "STDERR:"
    ) -join [Environment]::NewLine
    Set-Content -LiteralPath $outputPath -Value $content -Encoding UTF8

    $script:Results.Add([pscustomobject]@{
        name = $Name
        command = 'skipped'
        exitCode = 0
        expectedExitCodes = @(0)
        output = $outputPath
        startedAtUtc = $now.ToString('O')
        finishedAtUtc = $now.ToString('O')
    })
}

function Assert-RunnerOutputSemantics {
    param(
        [Parameter(Mandatory = $true)]
        [string] $OutputPath,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $content = Get-Content -LiteralPath $OutputPath -Raw
    $internalFailurePatterns = @(
        'is not an absolute path',
        'assemblyPath',
        'LoadFromAssemblyPath',
        'ReflectionTypeLoadException',
        'FileNotFoundException',
        'Could not load file or assembly',
        'Unhandled exception',
        'NullReferenceException',
        'StackTrace'
    )

    foreach ($pattern in $internalFailurePatterns) {
        if ($content.IndexOf($pattern, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Runner semantic validation failed for ${Name}: output contains internal failure marker '$pattern'. Output: $OutputPath"
        }
    }

    $allowedExternalDegraded = @{
        'android-tools.process-monitor' = @('watch list', 'process')
        'doubao-agent' = @('service', 'planner', 'tool', 'mcp')
        'screenease' = @('ddc/ci', 'monitor', 'capabilities', 'unsupported', 'hardware', 'native display writer')
        'smartbird-thermostat' = @('energy server', 'fnb', 'adb', 'device', 'not configured')
    }

    foreach ($line in ($content -split "`r?`n")) {
        $match = [regex]::Match($line, '^(?<module>[a-z0-9.-]+)\s+\[(?<state>degraded|unsupported)\]\s+(?<message>.+)$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $match.Success) {
            continue
        }

        $moduleId = $match.Groups['module'].Value
        $message = $match.Groups['message'].Value.ToLowerInvariant()
        if (-not $allowedExternalDegraded.ContainsKey($moduleId)) {
            throw "Runner semantic validation failed for ${Name}: module '$moduleId' is degraded without an external allow-list reason. Output: $OutputPath"
        }

        $allowed = $false
        foreach ($token in $allowedExternalDegraded[$moduleId]) {
            if ($message.Contains($token)) {
                $allowed = $true
                break
            }
        }

        if (-not $allowed) {
            throw "Runner semantic validation failed for ${Name}: module '$moduleId' degraded reason is not classified as external: $($match.Groups['message'].Value). Output: $OutputPath"
        }
    }
}

function Invoke-ReleaseShellSmoke {
    $runnerExe = Join-Path $RepoRoot 'artifacts\release\win-x64\Runner\MyPowerTools.Runner.exe'
    $shellExe = Join-Path $RepoRoot 'artifacts\release\win-x64\Shell\MyPowerTools.Shell.Avalonia.exe'
    $dataRoot = Join-Path $RepoRoot 'artifacts\review-evidence\release-smoke-data'
    $runnerOutput = Join-Path $CommandOutputRoot ('{0:D2}-release-runner-background.txt' -f ($script:CommandIndex + 1))

    if (-not (Test-Path -LiteralPath $runnerExe)) {
        throw "Release Runner not found: $runnerExe"
    }

    if (-not (Test-Path -LiteralPath $shellExe)) {
        throw "Release Shell not found: $shellExe"
    }

    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $previousDataRoot = $env:MPT_DATA_ROOT
    $env:MPT_DATA_ROOT = $dataRoot
    $runner = $null
    try {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $runnerExe
        $startInfo.WorkingDirectory = $RepoRoot
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.CreateNoWindow = $true
        $startInfo.Environment['MPT_DATA_ROOT'] = $dataRoot
        foreach ($argument in @('--data-root', $dataRoot, '--no-tray')) {
            $startInfo.ArgumentList.Add($argument)
        }

        $runner = [System.Diagnostics.Process]::Start($startInfo)
        if ($null -eq $runner) {
            throw 'Release Runner did not start.'
        }

        Start-Sleep -Milliseconds 750
        Invoke-EvidenceCommand `
            -Name 'release-shell-smoke' `
            -FilePath $shellExe `
            -ArgumentList @('--smoke', '--timeout-ms', '30000', '--quit-runner') `
            -Environment @{ MPT_DATA_ROOT = $dataRoot } `
            -TimeoutSeconds 90 | Out-Null

        if (-not $runner.WaitForExit(15000)) {
            throw 'Release Runner did not exit after release Shell smoke.'
        }

        $runnerStdout = $runner.StandardOutput.ReadToEnd()
        $runnerStderr = $runner.StandardError.ReadToEnd()
        Set-Content -LiteralPath $runnerOutput -Value (@(
            'Command: release Runner background process'
            "ExitCode: $($runner.ExitCode)"
            ''
            'STDOUT:'
            $runnerStdout
            ''
            'STDERR:'
            $runnerStderr
        ) -join [Environment]::NewLine) -Encoding UTF8

        if ($runner.ExitCode -ne 0) {
            throw "Release Runner exited with $($runner.ExitCode). Output: $runnerOutput"
        }

        Assert-RunnerOutputSemantics -OutputPath $runnerOutput -Name 'release-runner-background'
    } finally {
        if ($null -ne $runner -and -not $runner.HasExited) {
            $runner.Kill($true)
            $runner.WaitForExit()
        }

        if ($null -eq $previousDataRoot) {
            Remove-Item Env:\MPT_DATA_ROOT -ErrorAction SilentlyContinue
        } else {
            $env:MPT_DATA_ROOT = $previousDataRoot
        }
    }
}

if (-not $SkipCommandExecution) {
    Invoke-EvidenceCommand -Name 'dotnet-version' -FilePath 'dotnet' -ArgumentList @('--version') | Out-Null
    Invoke-EvidenceCommand -Name 'dotnet-restore' -FilePath 'dotnet' -ArgumentList @('restore', 'MyPowerTools.slnx') | Out-Null
    Invoke-EvidenceCommand -Name 'dotnet-build' -FilePath 'dotnet' -ArgumentList @('build', 'MyPowerTools.slnx', '--no-restore') | Out-Null
    Invoke-EvidenceCommand -Name 'dotnet-test' -FilePath 'dotnet' -ArgumentList @('test', 'MyPowerTools.slnx', '--no-build') -TimeoutSeconds 1200 | Out-Null
    Invoke-EvidenceCommand -Name 'dotnet-test-foundation-p7' -FilePath 'dotnet' -ArgumentList @('test', 'src\MyPowerTools.Tests\MyPowerTools.Tests.csproj', '--no-build', '--filter', 'Foundation=P7') | Out-Null
    Invoke-EvidenceCommand -Name 'validate-modules' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'validate', 'modules') | Out-Null
    Invoke-EvidenceCommand -Name 'validate-contracts' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'validate', 'contracts') -TimeoutSeconds 900 | Out-Null
    if ($RefreshPackageSignatures) {
        Invoke-EvidenceCommand -Name 'package-sign-local' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'package', 'sign-local', 'modules') | Out-Null
        Invoke-EvidenceCommand -Name 'package-trust-strict' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'package', 'trust', 'modules', '--strict') | Out-Null
    } else {
        Add-SkippedEvidenceCommand -Name 'package-sign-local-skipped' -Reason 'Skipped during P-UI-Foundation because this phase is UI-only; pass -RefreshPackageSignatures to refresh local package signatures.'
        Add-SkippedEvidenceCommand -Name 'package-trust-strict-skipped' -Reason 'Skipped because solution build can refresh module binaries during UI-only validation; pass -RefreshPackageSignatures to refresh local package signatures and run strict trust.'
    }
    Invoke-EvidenceCommand -Name 'ui-check' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'ui', 'check', 'modules') | Out-Null
    Invoke-EvidenceCommand -Name 'module-list-include-disabled' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'module', 'list', '--include-disabled') | Out-Null
    Invoke-EvidenceCommand -Name 'diagnostics' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'diagnostics') | Out-Null
    Invoke-EvidenceCommand -Name 'ui-snapshot' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'ui', 'snapshot', '--surface', 'dashboard-card', '--theme', 'light', '--size', '1366x768', '--density', 'normal', '--out', $UiSnapshotRoot) | Out-Null
    Invoke-EvidenceCommand -Name 'shell-snapshot-fixture' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'ui', 'shell-snapshot', '--full-shell', '--theme', 'light', '--size', '1366x768', '--density', 'normal', '--out', (Join-Path $ShellScreenshotRoot 'fixture')) | Out-Null
    Invoke-EvidenceCommand -Name 'runner-autostart-status' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'runner', 'autostart', 'status') | Out-Null
    Invoke-EvidenceCommand -Name 'broker-secret-self-test' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'broker', 'secret', 'self-test', '--module', 'cli.secret-self-test', '--name', 'review-evidence') | Out-Null
    Invoke-EvidenceCommand -Name 'runner-once' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Runner', '--', '--once') | Out-Null
    Invoke-EvidenceCommand -Name 'adb-forwarder-portproxy-permission' -FilePath 'dotnet' -ArgumentList @('run', '--no-build', '--project', 'src\MyPowerTools.Cli', '--', 'run', 'adb-forwarder.portproxy.apply') -ExpectedExitCodes @(1) | Out-Null
    Invoke-EvidenceCommand -Name 'validate-templates' -FilePath 'pwsh.exe' -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', 'scripts\validate-templates.ps1') -TimeoutSeconds 900 | Out-Null
    Invoke-EvidenceCommand -Name 'smoke' -FilePath 'pwsh.exe' -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', 'scripts\smoke.ps1') -TimeoutSeconds 1800 | Out-Null
    if ($IncludeReleaseEvidence) {
        Invoke-EvidenceCommand -Name 'publish-windows' -FilePath 'pwsh.exe' -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', 'scripts\publish-windows.ps1') -TimeoutSeconds 1800 | Out-Null
        Invoke-EvidenceCommand -Name 'release-package-trust' -FilePath (Join-Path $RepoRoot 'artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe') -ArgumentList @('package', 'trust', 'artifacts\release\win-x64\modules', '--strict') | Out-Null
        $releaseRunnerOnce = Invoke-EvidenceCommand -Name 'release-runner-once' -FilePath (Join-Path $RepoRoot 'artifacts\release\win-x64\Runner\MyPowerTools.Runner.exe') -ArgumentList @('--once', '--data-root', 'artifacts\review-evidence\release-root-once-data')
        Assert-RunnerOutputSemantics -OutputPath $releaseRunnerOnce.output -Name 'release-runner-once'
        Invoke-ReleaseShellSmoke
        Invoke-EvidenceCommand -Name 'release-autostart-dry-run' -FilePath (Join-Path $RepoRoot 'artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe') -ArgumentList @('runner', 'autostart', 'enable', '--dry-run') | Out-Null
        Invoke-EvidenceCommand -Name 'install-dry-run' -FilePath 'pwsh.exe' -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', 'scripts\install-windows.ps1', '-PackageRoot', 'artifacts\release\win-x64', '-InstallDir', 'artifacts\install-dryrun', '-DryRun') | Out-Null
        Invoke-EvidenceCommand -Name 'uninstall-dry-run' -FilePath 'pwsh.exe' -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', 'scripts\uninstall-windows.ps1', '-InstallDir', 'artifacts\install-dryrun', '-DryRun', '-Force') | Out-Null
    } else {
        Add-SkippedEvidenceCommand -Name 'release-evidence-skipped' -Reason 'Skipped during P-UI-Foundation because this phase is UI-only; pass -IncludeReleaseEvidence to run Windows publish and release validation.'
    }
}

$releaseZip = Join-Path $RepoRoot 'artifacts\release\MyPowerTools-win-x64.zip'
$releaseHash = if (Test-Path -LiteralPath $releaseZip) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $releaseZip).Hash
} else {
    ''
}

$resultsPath = Join-Path $EvidenceRoot 'command-results.json'
if (-not $SkipCommandExecution -or -not (Test-Path -LiteralPath $resultsPath)) {
    $script:Results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultsPath -Encoding UTF8
}

$readme = @"
# MyPowerTools Review Evidence

Generated: $([DateTimeOffset]::UtcNow.ToString('O'))

## Summary

- Command outputs: command-outputs/
- UI snapshots: ui-snapshots/
- Shell screenshots: shell-screenshots/
- Command result index: command-results.json
- Release artifact: ../release/MyPowerTools-win-x64.zip
- Release SHA256: $releaseHash

## Completion Basis

- Runtime evidence is collected from restore/build/test, P7 runtime tests, Runner once, diagnostics, HostControl auth tests, module event cursor persistence tests, and cancellation tests.
- UI evidence is collected from UI gate, module UI snapshots, full Shell screenshots, Shell smoke, and token-based UI lint.
- Module closure is evidenced by module validation, contract validation, package trust, Runner once, diagnostics, broker permission output, and expected external degraded states.
- Package signature refresh is skipped by default during P-UI-Foundation. Pass `-RefreshPackageSignatures` to refresh local package signatures.
- Release evidence is skipped by default during P-UI-Foundation. Pass `-IncludeReleaseEvidence` to run publish, release trust, release Runner once, release Shell smoke, autostart dry-run, install dry-run, uninstall dry-run, release metadata, and release hash checks.

## External Checks

External checks are limited to administrator/elevated helper execution, production signing material, connected hardware/services, and macOS/Linux native host validation. See `docs/KNOWN_LIMITATIONS.md` and `docs/EXTERNAL_VALIDATION.md`.
"@
Set-Content -LiteralPath (Join-Path $EvidenceRoot 'README.md') -Value $readme -Encoding UTF8

$evidenceZip = Join-Path $ReviewRoot 'MyPowerTools-final-evidence.zip'
if (Test-Path -LiteralPath $evidenceZip) {
    Remove-Item -LiteralPath $evidenceZip -Force
}

Compress-Archive -Path (Join-Path $EvidenceRoot '*') -DestinationPath $evidenceZip -Force
Write-Host "Review evidence written to $EvidenceRoot"
Write-Host "Evidence zip written to $evidenceZip"
