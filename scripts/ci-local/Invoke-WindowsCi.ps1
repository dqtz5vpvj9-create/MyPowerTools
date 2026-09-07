<#
.SYNOPSIS
  Runs the GitHub Actions Windows jobs on a real Windows host before pushing.

.DESCRIPTION
  GitHub's Windows jobs cannot be simulated in a container: nektos/act only
  runs Linux images, and every step here is pwsh, MSBuild, named pipes, the
  Windows SCM or an NSSM service. So this script does the opposite of
  simulation -- it reproduces the job on a real Windows machine, step for step,
  in the same order, under the same environment variables that the runner sets.

  Suites map onto the workflow jobs:

    Ci       -> ci.yml, job "windows"                (build, test, validate, package)
    Handoff  -> handoff-windows-gates.yml, job
                "architecture-and-tool-gates"        (architecture + Android Tools + NSSM)
    Both     -> Handoff then Ci, sharing one build
    Quick    -> restore, build, architecture Quick, filtered tests

  Each step is a gate. The first failure stops the run and the step name matches
  the workflow step name, so a local failure points straight at the GitHub step
  that would have gone red.

.PARAMETER Workspace
  The CI clone. Kept separate from any development clone so a dirty working
  tree or a submodule parked on a feature branch cannot change the result.

.PARAMETER Suite
  Which workflow job to reproduce.

.PARAMETER SkipSync
  Reuse the workspace exactly as it is. Use when re-running a failed step.

.PARAMETER ReportPath
  JSON step report: name, duration, exit code.

.NOTES
  Fidelity limits, worth knowing before trusting a green run:
    * This host is far faster than a GitHub runner (24 cores / 64 GB against
      4 cores / 16 GB). Wall-clock assertions that fail on GitHub can pass here.
      Timing-sensitive gates must not be tuned against this machine.
    * The GitHub runner is a fresh VM. This workspace is reused, so `git clean`
      below matters; without it stale artifacts mask missing build steps.
    * `actions/cache` is not reproduced. The NuGet cache is simply warm here,
      which is why local runs are much shorter than the GitHub ones.
#>
[CmdletBinding()]
param(
    [string]$Workspace = 'C:\ci\MyPowerTools',
    [ValidateSet('Ci', 'Handoff', 'Both', 'Quick')]
    [string]$Suite = 'Both',
    [switch]$SkipSync,
    [string]$Ref = '',
    [string]$ReportPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Tests that cannot run over SSH, with the reason each is here. SSH on Windows
# has no interactive logon session, so the Credential Manager is unreachable:
# CredRead returns 1312 ERROR_NO_SUCH_LOGON_SESSION where a GitHub runner
# returns 1168 ERROR_NOT_FOUND, which WindowsCredentialSecretStore maps to null.
# The gap is the rig's, not the product's, and these still run on GitHub.
#
# Nothing else belongs in this list. A test that fails here for any other reason
# is a finding, and the whole point of the rig is to see it before pushing.
$script:EnvironmentBlockedTests = @(
    'SmartBirdThermostatProductTests'
)

$script:Steps = [System.Collections.Generic.List[object]]::new()
$script:RunStart = [Diagnostics.Stopwatch]::StartNew()

function Write-Banner {
    param([string]$Text)
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Invoke-Step {
    <#
      Runs one workflow step. The script block is expected to invoke native
      commands; $LASTEXITCODE is checked the way `shell: pwsh` does on the
      runner, where a non-zero native exit code fails the step.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Body
    )

    Write-Banner $Name
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $global:LASTEXITCODE = 0
    $failure = $null
    try {
        & $Body
        if ($LASTEXITCODE -ne 0) {
            $failure = "exit code $LASTEXITCODE"
        }
    } catch {
        $failure = $_.Exception.Message
    }
    $watch.Stop()

    $script:Steps.Add([PSCustomObject]@{
        name       = $Name
        seconds    = [math]::Round($watch.Elapsed.TotalSeconds, 1)
        conclusion = if ($failure) { 'failure' } else { 'success' }
        error      = $failure
    })

    $duration = '{0:mm\:ss}' -f $watch.Elapsed
    if ($failure) {
        Write-Host "[FAIL] $Name ($duration): $failure" -ForegroundColor Red
        Complete-Run
        exit 1
    }
    Write-Host "[ok]   $Name ($duration)" -ForegroundColor Green
}

function Complete-Run {
    $script:RunStart.Stop()
    Write-Host ''
    Write-Host ('-' * 72)
    foreach ($step in $script:Steps) {
        $mark = if ($step.conclusion -eq 'success') { 'ok  ' } else { 'FAIL' }
        Write-Host ('{0}  {1,7:N1}s  {2}' -f $mark, $step.seconds, $step.name)
    }
    Write-Host ('-' * 72)
    Write-Host ('total {0:mm\:ss}' -f $script:RunStart.Elapsed)

    if ($ReportPath) {
        $directory = Split-Path -Parent $ReportPath
        if ($directory -and -not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }
        [PSCustomObject]@{
            suite         = $Suite
            workspace     = $Workspace
            commit        = (git -C $Workspace rev-parse HEAD 2>$null)
            totalSeconds  = [math]::Round($script:RunStart.Elapsed.TotalSeconds, 1)
            conclusion    = if ($script:Steps | Where-Object conclusion -eq 'failure') { 'failure' } else { 'success' }
            steps         = $script:Steps
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
        Write-Host "report: $ReportPath"
    }
}

# --------------------------------------------------------------------------
# Workspace: reproduce actions/checkout@v4 with submodules: recursive.
# --------------------------------------------------------------------------

if (-not $SkipSync) {
    if (-not (Test-Path -LiteralPath $Workspace)) {
        throw "Workspace $Workspace does not exist. Run preflight.sh, which clones it."
    }

    Invoke-Step 'Checkout' {
        Push-Location $Workspace
        try {
            $target = if ($Ref) { $Ref } else { 'FETCH_HEAD' }
            git checkout --detach --force $target
            if ($LASTEXITCODE -ne 0) { throw "checkout of $target failed." }

            # actions/checkout starts from an empty directory. Reused workspaces
            # do not, and a stale artifacts tree silently satisfies steps that
            # should have failed, so the tree is scrubbed to match.
            git reset --hard
            git clean -xdff
            git submodule sync --recursive
            git submodule update --init --recursive --force
            if ($LASTEXITCODE -ne 0) { throw 'submodule checkout failed.' }
            git submodule foreach --recursive 'git clean -xdff' | Out-Null
        } finally {
            Pop-Location
        }
    }
}

Set-Location $Workspace

# Set-Location moves PowerShell's location but leaves the process's own working
# directory where pwsh started. .NET path APIs read the latter, so a step doing
# [IO.Path]::GetFullPath('tools\...') -- which ci.yml and handoff-windows-gates
# both do -- resolves against the home directory instead of the workspace and
# produces a path the callee rightly rejects. A GitHub runner starts each step
# with the real working directory already set, so both have to be set here for
# the step to behave the same way.
[Environment]::CurrentDirectory = $Workspace

# --------------------------------------------------------------------------
# Environment: what the GitHub runner exports for these jobs.
# --------------------------------------------------------------------------

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = 'true'
$env:NUGET_XMLDOC_MODE = 'skip'
$env:CI = 'true'
$env:GITHUB_ACTIONS = 'true'
$env:GITHUB_WORKSPACE = $Workspace
$env:RUNNER_OS = 'Windows'
$env:RUNNER_TEMP = Join-Path $Workspace 'artifacts\runner-temp'
New-Item -ItemType Directory -Path $env:RUNNER_TEMP -Force | Out-Null

$script:BlockedFilter = ($script:EnvironmentBlockedTests |
    ForEach-Object { "FullyQualifiedName!~$_" }) -join '&'
if ($script:BlockedFilter) {
    Write-Host ''
    Write-Host 'Excluded here, still covered on GitHub:' -ForegroundColor Yellow
    $script:EnvironmentBlockedTests | ForEach-Object { Write-Host "  $_ (no interactive logon session over SSH)" -ForegroundColor Yellow }
}

Invoke-Step 'Setup .NET SDK' {
    # setup-dotnet installs whatever global.json pins. Locally the SDK is
    # already installed, so the equivalent check is that the pinned version is
    # the one that will actually resolve.
    $pinned = (Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version
    $active = (dotnet --version)
    Write-Host "global.json pins $pinned; dotnet --version reports $active"
    if ($active -ne $pinned) {
        throw "SDK mismatch: install $pinned or GitHub and this host will not agree."
    }
}

Invoke-Step 'Prepare local NuGet source' {
    New-Item -ItemType Directory -Path 'artifacts\sdk\nuget' -Force | Out-Null
}

if ($Suite -eq 'Ci' -or $Suite -eq 'Both') {
    Invoke-Step 'Verify tool submodules' {
        $toolIds = @(
            'adb-forwarder'
            'remote-notifications'
            'remote-commands'
            'process-monitor'
            'screenease'
            'smartbird-thermostat'
            'doubao-computer-use'
            'input-monitor'
        )
        $requiredFiles = @('tool-release.json', 'source-map.json')
        $missing = foreach ($toolId in $toolIds) {
            foreach ($requiredFile in $requiredFiles) {
                $requiredPath = Join-Path 'tools' $toolId $requiredFile
                if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { $requiredPath }
            }
        }
        if ($missing) {
            $missing | ForEach-Object { Write-Host "missing: $_" -ForegroundColor Red }
            throw 'Tool submodule checkout is incomplete.'
        }
    }
}

Invoke-Step 'Build SDK bundles' {
    pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\build-sdk.ps1
}

Invoke-Step 'Restore' {
    dotnet restore MyPowerTools.slnx
}

Invoke-Step 'Build' {
    dotnet build MyPowerTools.slnx --no-restore --maxcpucount
}

Invoke-Step 'Architecture Gate (Quick)' {
    pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\verify-architecture.ps1 -Tier Quick
}

if ($Suite -eq 'Quick') {
    Invoke-Step 'Test' {
        dotnet test MyPowerTools.slnx --no-build `
            --filter "FullyQualifiedName!~AndroidTools_&FullyQualifiedName!~Runtime_collects_production_module_events_and_notifications&$script:BlockedFilter"
    }
    Complete-Run
    exit 0
}

Invoke-Step 'Architecture Gate (Process)' {
    pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\verify-architecture.ps1 -Tier Process
}

if ($Suite -eq 'Handoff' -or $Suite -eq 'Both') {
    Invoke-Step 'Stage complete Android Tools package' {
        $stage = [IO.Path]::GetFullPath('tools\remote-notifications\artifacts\handoff\android-tools-suite')
        pwsh.exe -NoLogo -NoProfile -NonInteractive `
            -File tools\remote-notifications\build.ps1 `
            -MyPowerToolsRepoRoot $env:GITHUB_WORKSPACE `
            -Configuration Debug `
            -RuntimeIdentifier win-x64 `
            -OutputRoot $stage
        if ($LASTEXITCODE -ne 0) { throw "Android Tools package build failed with exit code $LASTEXITCODE." }

        $destination = [IO.Path]::GetFullPath('modules\android-tools-suite')
        if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -Path (Join-Path $stage '*') -Destination $destination -Recurse -Force

        $notificationManifest = Get-Content -LiteralPath `
            (Join-Path $destination 'modules\notifications\module.json') -Raw | ConvertFrom-Json
        $entrypoints = @($notificationManifest.entrypoints)
        if ($entrypoints.Count -ne 1 -or
            [string]$entrypoints[0].kind -ne 'inproc-dotnet' -or
            [string]$entrypoints[0].type -ne 'AndroidTools.MyPowerTools.RemoteNotificationsServiceObserverModule') {
            throw 'The staged notification module is not using the supervised service observer.'
        }
    }
} else {
    Invoke-Step 'Stage Android Tools module host' {
        dotnet publish tools\remote-notifications\current-integration\src\AndroidTools.Runtime\AndroidTools.Runtime.csproj `
            --configuration Debug --runtime win-x64 --self-contained false `
            --output modules\android-tools-suite\windows\x64 --nologo
    }
}

if ($Suite -eq 'Ci' -or $Suite -eq 'Both') {
    Invoke-Step 'Test' {
        dotnet test MyPowerTools.slnx --no-build `
            --filter "FullyQualifiedName!~AndroidTools_&FullyQualifiedName!~Runtime_collects_production_module_events_and_notifications&$script:BlockedFilter"
    }
}

Invoke-Step 'Android Tools test' {
    dotnet test MyPowerTools.slnx --no-build `
        --filter "FullyQualifiedName~AndroidTools_|FullyQualifiedName~Runtime_collects_production_module_events_and_notifications"
}

Invoke-Step 'Build tool packages from submodules' {
    pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\build-tool-packages.ps1 -OutputRoot artifacts\ci-modules
}

Invoke-Step 'NSSM Manager PowerShell tests' {
    $executable = 'tools\nssm-manager\sdk-tool\src\NssmManager.Executable\publish\win-x64\nssm-manager.exe'
    pwsh.exe -NoLogo -NoProfile -NonInteractive -File tools\nssm-manager\tests\verify-native-resources.ps1 -Executable $executable
    if ($LASTEXITCODE -ne 0) { throw "verify-native-resources.ps1 failed with exit code $LASTEXITCODE." }
    pwsh.exe -NoLogo -NoProfile -NonInteractive -File tools\nssm-manager\tests\service-mode-smoke.ps1 -ManagedExecutable $executable -ConfirmIsolatedScmMutation -EvidencePath artifacts\nssm-compat\service-mode-smoke.json
    if ($LASTEXITCODE -ne 0) { throw "service-mode-smoke.ps1 failed with exit code $LASTEXITCODE." }
}

if ($Suite -eq 'Handoff') {
    Complete-Run
    exit 0
}

Invoke-Step 'Refresh Package Trust Hooks' {
    dotnet run --no-build --project src\MyPowerTools.Cli -- package sign-local artifacts\ci-modules
}

Invoke-Step 'Validate Modules' {
    dotnet run --no-build --project src\MyPowerTools.Cli -- validate artifacts\ci-modules
}

Invoke-Step 'Validate Module Contracts' {
    dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts artifacts\ci-modules
}

Invoke-Step 'Validate Package Trust' {
    dotnet run --no-build --project src\MyPowerTools.Cli -- package trust artifacts\ci-modules --strict
}

Invoke-Step 'UI Gate' {
    dotnet run --no-build --project src\MyPowerTools.Cli -- ui check artifacts\ci-modules
}

Invoke-Step 'UI Contract Snapshots' {
    dotnet run --no-build --project src\MyPowerTools.Cli -- ui snapshot artifacts\ci-modules --surface dashboard-card --theme light --size 1366x768 --density normal --out artifacts\ui-snapshots
}

Invoke-Step 'Shell UI Snapshot Matrix' {
    dotnet run --no-build --project src\MyPowerTools.Cli -- ui shell-snapshot --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots
}

Invoke-Step 'Validate Templates' {
    pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1
}

Invoke-Step 'Runner Once' {
    dotnet run --no-build --project src\MyPowerTools.Runner -- --once --modules "$env:GITHUB_WORKSPACE\artifacts\ci-modules"
}

Invoke-Step 'Smoke' {
    pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1 -ModulesRoot artifacts\ci-modules -NoRestore -NoBuild -NoTest -NoTemplateValidation
}

Invoke-Step 'Artifacts Governance Gate' {
    pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\check-artifacts-governance.ps1 -Enforce -Refresh
}

Complete-Run
