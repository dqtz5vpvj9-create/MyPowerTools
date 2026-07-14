<#
.SYNOPSIS
  MyPowerTools architecture gate runner.

.DESCRIPTION
  Drives the Quick / Process / Release architecture gates defined in
  docs/18-architecture-revision-fast-plan.md. Each run uses isolated temp
  resources (unique data root, pipe, unit id, ports) and never touches the
  user's running ScreenEase.Service, Doubao or powertoold processes.

  -Tier Quick    : A1 dependency boundaries + A2 dynamic discovery (TODO: Batch-2)
  -Tier Process  : A3 service lifecycle + dual-UI scope gate
  -Tier Release  : A5 real-machine install loop (run only against a candidate build)

.PARAMETER Tier
  Which gate tier to run. Default: Process.
#>
[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Process', 'Release')]
    [string]$Tier = 'Process'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Item $MyInvocation.MyCommand.Path).Directory.Parent.FullName
$artifactsDir = Join-Path (Join-Path $repoRoot 'artifacts') 'architecture-smoke'
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

$runId = [System.Guid]::NewGuid().ToString('N').Substring(0, 8)
$dataRoot = Join-Path $env:TEMP "mpt-arch-$Tier-$runId"
$deployRoot = Join-Path $dataRoot 'deploy'
$unitsDir = Join-Path $deployRoot 'units'
$fixtureDir = Join-Path (Join-Path (Join-Path $repoRoot 'tests') 'fixtures') 'test-service-unit'
$gateProject = Join-Path (Join-Path (Join-Path $repoRoot 'tests') 'architecture-gate') 'ArchitectureGate.csproj'
$smProject = Join-Path (Join-Path (Join-Path $repoRoot 'src') 'MyPowerTools.ServiceManager') 'MyPowerTools.ServiceManager.csproj'

$unitId = "test-service-unit-$runId"
$toolId = "test-tool-$runId"
$otherToolId = "other-tool-$runId"
$resultPath = Join-Path $artifactsDir 'result.json'
$smLogPath = Join-Path $dataRoot 'servicemanager.log'

$smProcess = $null

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  OK  $msg" -ForegroundColor Green }
function Write-Bad($msg)  { Write-Host "  FAIL $msg" -ForegroundColor Red }

try {
    Write-Step "Tier=$Tier runId=$runId"
    Write-Step "dataRoot=$dataRoot"

    New-Item -ItemType Directory -Force -Path $dataRoot, $deployRoot, $unitsDir | Out-Null

    # Build everything first.
    Write-Step "Building ServiceManager, fixture and gate driver..."
    & dotnet build $smProject --nologo -v quiet 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "ServiceManager build failed" }
    & dotnet build (Join-Path $fixtureDir 'TestServiceUnit.csproj') --nologo -v quiet 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "fixture build failed" }
    & dotnet build $gateProject --nologo -v quiet 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "gate driver build failed" }

    # Locate the built fixture executable (publish a self-contained copy under deploy root).
    $fixtureExeDir = Join-Path $dataRoot 'fixture'
    Write-Step "Publishing test-service-unit fixture..."
    & dotnet publish (Join-Path $fixtureDir 'TestServiceUnit.csproj') -c Release -o $fixtureExeDir --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "fixture publish failed" }
    $fixtureExe = Join-Path $fixtureExeDir 'test-service-unit.exe'
    if (-not (Test-Path $fixtureExe)) { $fixtureExe = Join-Path $fixtureExeDir 'test-service-unit.dll' }

    if ($Tier -eq 'Process') {
        # Deploy a unit manifest pointing at the fixture exe, owned by our test tool.
        $manifest = @{
            id = $unitId
            toolId = $toolId
            displayName = "Test Service Unit"
            exec = $fixtureExe
            arguments = @("--heartbeat-file", (Join-Path $dataRoot 'heartbeat.txt'))
            workingDirectory = ""
            environment = @{}
            autostart = $false
            restartPolicy = @{ maxRestarts = 2; backoffMs = 500 }
            readiness = @{ kind = "none"; address = ""; timeoutMs = 3000 }
            stopTimeoutMs = 3000
            dataRoots = @()
            dependsOn = @()
            instanceToken = "test-fixture-token-$runId"
        }
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $unitsDir "$unitId.json") -Encoding UTF8

        # Launch ServiceManager against the temp data root + deploy root.
        Write-Step "Launching ServiceManager..."
        $env:MPT_DATA_ROOT = $dataRoot
        $smProcess = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--no-build','--project',$smProject,'--','--data-root',$dataRoot,'--deploy-root',$deployRoot) -WindowStyle Hidden -PassThru -RedirectStandardOutput $smLogPath -RedirectStandardError (Join-Path $dataRoot 'sm.err')
        Start-Sleep -Seconds 6

        if ($smProcess.HasExited) {
            Write-Bad "ServiceManager exited early. Log:"
            Get-Content $smLogPath -ErrorAction SilentlyContinue | Out-Host
            throw "ServiceManager did not stay running"
        }
        Write-Ok "ServiceManager PID=$($smProcess.Id)"

        # Run the A3 gate driver.
        Write-Step "Running A3 gate driver..."
        & dotnet run --no-build --project $gateProject -- `
            --data-root $dataRoot `
            --unit-id $unitId `
            --tool-id $toolId `
            --other-tool-id $otherToolId `
            --result $resultPath 2>&1 | Out-Host

        if ($LASTEXITCODE -eq 0) {
            Write-Ok "A3 gate PASSED"
        } else {
            Write-Bad "A3 gate FAILED (exit $LASTEXITCODE)"
            throw "A3 gate failed"
        }
    } else {
        Write-Host "Tier '$Tier' is not yet implemented in this batch." -ForegroundColor Yellow
        Write-Host "Result written to $resultPath"
    }

    Write-Step "Result: $resultPath"
    exit 0
}
catch {
    Write-Bad $_.ToString()
    if (Test-Path $resultPath) {
        Write-Host "---- result.json ----" -ForegroundColor DarkGray
        Get-Content $resultPath | Out-Host
    }
    exit 1
}
finally {
    if ($smProcess -and -not $smProcess.HasExited) {
        try { Stop-Process -Id $smProcess.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
    # Best-effort cleanup of the temp data root. Never touches the user's real data root.
    if (Test-Path $dataRoot) {
        try { Remove-Item -Recurse -Force $dataRoot -ErrorAction SilentlyContinue } catch {}
    }
}
