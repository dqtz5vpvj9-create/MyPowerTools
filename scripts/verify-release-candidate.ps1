<#
.SYNOPSIS
  Verifies a MyPowerTools installer candidate locally and, optionally, on an
  independent Windows host reached through OpenSSH.

.DESCRIPTION
  The local phase validates the candidate inventory, critical hashes, UI
  contract and a real Runner --once discovery pass. The remote phase copies
  the candidate archive, installs it under an isolated TEMP root, starts the
  real ServiceManager and ScreenEase Service Unit, enumerates units through the
  shipped CLI, starts Runner, and executes the shipped Shell HostControl smoke.
#>
[CmdletBinding()]
param(
    [string]$CandidateRoot = '',
    [string]$RemoteHost = '',
    [string]$EvidenceRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($CandidateRoot)) {
    $CandidateRoot = Join-Path $repoRoot 'artifacts\install\0.2.0'
}
$CandidateRoot = [IO.Path]::GetFullPath($CandidateRoot)
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $repoRoot 'artifacts\architecture-smoke\a5'
}
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null

$runId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$records = [Collections.Generic.List[object]]::new()
$startedAt = [DateTimeOffset]::UtcNow

function Add-Record {
    param([string]$Id, [bool]$Passed, [string]$Detail, [string]$Evidence = '')
    $records.Add([ordered]@{ id = $Id; passed = $Passed; detail = $Detail; evidence = $Evidence })
    $label = if ($Passed) { 'PASS' } else { 'FAIL' }
    $color = if ($Passed) { 'Green' } else { 'Red' }
    Write-Host "[$label] ${Id}: $Detail" -ForegroundColor $color
}

function Invoke-Captured {
    param([string]$FilePath, [string[]]$ArgumentList, [string]$OutputPath)
    $output = & $FilePath @ArgumentList 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    $output | Set-Content -LiteralPath $OutputPath -Encoding UTF8
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Write-Result {
    param([string]$Phase, [bool]$Passed, [object]$Remote = $null)
    $payload = [ordered]@{
        gate = 'A5'
        phase = $Phase
        passed = $Passed
        runId = $runId
        startedAt = $startedAt.ToString('O')
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        candidateRoot = $CandidateRoot
        remoteHost = $RemoteHost
        records = $records
        remote = $Remote
    }
    $runPath = Join-Path $EvidenceRoot "result-$runId.json"
    $latestPath = Join-Path $EvidenceRoot 'result.json'
    $payload | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $runPath -Encoding UTF8
    Copy-Item -LiteralPath $runPath -Destination $latestPath -Force
    Write-Host "A5 evidence: $runPath" -ForegroundColor Cyan
    return $runPath
}

$manifestPath = Join-Path $CandidateRoot 'candidate-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Candidate manifest is missing: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$payloadRoot = Join-Path $CandidateRoot 'payload'

$expectedTools = @('adb-forwarder', 'doubao-computer-use', 'remote-notifications', 'screenease', 'smartbird-thermostat')
$actualTools = @($manifest.tools | ForEach-Object { [string]$_.toolId })
$toolInventoryOk = $actualTools.Count -eq $expectedTools.Count -and @($expectedTools | Where-Object { $actualTools -notcontains $_ }).Count -eq 0
Add-Record 'A5.1-candidate-tool-inventory' $toolInventoryOk "tools=$($actualTools -join ',')" $manifestPath

$criticalPaths = @(
    'Shell\MyPowerTools.Shell.Avalonia.exe',
    'Runner\MyPowerTools.Runner.exe',
    'ServiceManager\MyPowerTools.ServiceManager.exe',
    'Cli\MyPowerTools.Cli.exe',
    'service-units\screenease.service\bin\ScreenEase.Service.exe'
)
$missingCritical = @($criticalPaths | Where-Object { -not (Test-Path -LiteralPath (Join-Path $payloadRoot $_) -PathType Leaf) })
Add-Record 'A5.2-critical-process-payloads' ($missingCritical.Count -eq 0) "missing=$($missingCritical -join ',')" $payloadRoot

$surfaceDlls = @(Get-ChildItem -LiteralPath (Join-Path $payloadRoot 'modules') -Recurse -File -Filter '*.Surface.dll')
Add-Record 'A5.3-five-loadable-surfaces' ($surfaceDlls.Count -eq 5) "surfaceDlls=$($surfaceDlls.Count)" (($surfaceDlls.FullName) -join ';')

$packages = @(Get-ChildItem -LiteralPath (Join-Path $payloadRoot 'packages') -File -Filter '*.mptpkg')
Add-Record 'A5.4-five-independent-packages' ($packages.Count -eq 5) "packages=$($packages.Count)" (($packages.FullName) -join ';')

$hashIndex = @{}
foreach ($entry in $manifest.files) { $hashIndex[[string]$entry.path] = [string]$entry.sha256 }
$hashFailures = [Collections.Generic.List[string]]::new()
foreach ($relative in $criticalPaths) {
    $manifestKey = $relative.Replace('\', '/')
    $fullPath = Join-Path $payloadRoot $relative
    if (-not $hashIndex.ContainsKey($manifestKey) -or -not (Test-Path -LiteralPath $fullPath)) {
        $hashFailures.Add($manifestKey)
        continue
    }
    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $hashIndex[$manifestKey].ToLowerInvariant()) { $hashFailures.Add($manifestKey) }
}
Add-Record 'A5.5-critical-hash-integrity' ($hashFailures.Count -eq 0) "failures=$($hashFailures -join ',')" $manifestPath

$uiLog = Join-Path $EvidenceRoot "ui-$runId.log"
$uiResult = Invoke-Captured -FilePath (Join-Path $payloadRoot 'Cli\MyPowerTools.Cli.exe') -ArgumentList @('ui', 'check', $repoRoot) -OutputPath $uiLog
Add-Record 'A5.6-ui-contract' ($uiResult.ExitCode -eq 0) "exit=$($uiResult.ExitCode)" $uiLog

$localDataRoot = Join-Path $env:TEMP "mypowertools-a5-local-$runId"
New-Item -ItemType Directory -Path $localDataRoot -Force | Out-Null
try {
    $runnerLog = Join-Path $EvidenceRoot "runner-local-$runId.log"
    $runnerResult = Invoke-Captured -FilePath (Join-Path $payloadRoot 'Runner\MyPowerTools.Runner.exe') -ArgumentList @(
        '--once', '--modules', (Join-Path $payloadRoot 'modules'), '--data-root', $localDataRoot
    ) -OutputPath $runnerLog
    $discovered = @('adb-forwarder', 'android-tools.notifications', 'doubao-agent', 'screenease', 'smartbird-thermostat') |
        Where-Object { $runnerResult.Output -match [regex]::Escape($_) }
    Add-Record 'A5.7-local-runner-discovery' ($runnerResult.ExitCode -eq 0 -and $discovered.Count -eq 5) "exit=$($runnerResult.ExitCode); discovered=$($discovered -join ',')" $runnerLog
}
finally {
    if (Test-Path -LiteralPath $localDataRoot) { Remove-Item -LiteralPath $localDataRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

$localPassed = @($records | Where-Object { -not $_.passed }).Count -eq 0
if (-not $localPassed) {
    [void](Write-Result -Phase 'local' -Passed $false)
    exit 1
}

if ([string]::IsNullOrWhiteSpace($RemoteHost)) {
    [void](Write-Result -Phase 'local' -Passed $true)
    exit 0
}

$archive = Join-Path (Split-Path -Parent $CandidateRoot) "MyPowerTools-$($manifest.suiteVersion)-$($manifest.runtimeIdentifier).zip"
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Candidate archive is missing: $archive" }
$remoteArchiveName = "MyPowerTools-A5-$runId.zip"
$remoteScriptName = "MyPowerTools-A5-$runId.ps1"

Write-Host "Copying candidate to $RemoteHost..." -ForegroundColor Cyan
& scp -q $archive "${RemoteHost}:$remoteArchiveName"
if ($LASTEXITCODE -ne 0) { throw "scp failed with exit code $LASTEXITCODE" }

$remoteScript = @"
`$ErrorActionPreference = 'Stop'
`$runId = '$runId'
`$archive = Join-Path `$HOME '$remoteArchiveName'
`$testRoot = Join-Path `$env:TEMP "MyPowerTools-A5-`$runId"
`$stage = Join-Path `$testRoot 'candidate'
`$installBase = Join-Path `$testRoot 'installed'
`$dataRoot = Join-Path `$testRoot 'data'
`$remoteRecords = [Collections.Generic.List[object]]::new()
function Add-RemoteRecord([string]`$id, [bool]`$passed, [string]`$detail) {
  `$remoteRecords.Add([ordered]@{ id = `$id; passed = `$passed; detail = `$detail })
}
# Pre-clean any orphaned test ServiceManager / Service Unit from a prior run whose
# command line references an isolated A5 TEMP root. The daily ServiceManager (under
# Program Files) is never touched because its path does not contain MyPowerTools-A5-.
`$dailyInstallRoot = 'C:\Program Files\MyPowerTools'
`$orphanCandidates = Get-CimInstance Win32_Process -Filter "Name='MyPowerTools.ServiceManager.exe' OR Name='ScreenEase.Service.exe'" -ErrorAction SilentlyContinue
foreach (`$proc in @(`$orphanCandidates)) {
  if (`$null -ne `$proc.CommandLine -and `$proc.CommandLine -match 'MyPowerTools-A5-' -and `$proc.CommandLine -notlike "*`$dailyInstallRoot*") {
    Stop-Process -Id `$proc.ProcessId -Force -ErrorAction SilentlyContinue
  }
}
Start-Sleep -Milliseconds 500
# A ServiceManager bound to the default named pipe from a prior run will reject this
# run's token (Unauthenticated). Only fail if a Program Files / daily manager holds it.
`$existingManager = @(Get-Process -Name 'MyPowerTools.ServiceManager' -ErrorAction SilentlyContinue | Where-Object { `$null -ne `$_.Path -and `$_.Path -like "`$dailyInstallRoot*" })
if (`$existingManager.Count -gt 0) { throw "Existing daily ServiceManager process detected: `$(`$existingManager.Id -join ',')" }
if (Test-Path -LiteralPath `$testRoot) { Remove-Item -LiteralPath `$testRoot -Recurse -Force }
New-Item -ItemType Directory -Path `$stage -Force | Out-Null
Expand-Archive -LiteralPath `$archive -DestinationPath `$stage -Force
`$installOutput = @(& (Join-Path `$stage 'install.ps1') -InstallBase `$installBase -DataRoot `$dataRoot -NoLaunch)
`$installResultPath = `$installOutput[-1]
`$installResult = Get-Content -LiteralPath `$installResultPath -Raw | ConvertFrom-Json
Add-RemoteRecord 'A5.R1-install-completes' (Test-Path -LiteralPath `$installResult.installRoot) "root=`$(`$installResult.installRoot)"
`$env:MPT_DATA_ROOT = `$dataRoot
`$cli = Join-Path `$installResult.installRoot 'Cli\MyPowerTools.Cli.exe'
`$servicesJson = & `$cli service list
if (`$LASTEXITCODE -ne 0) { throw 'service list failed' }
`$services = @(`$servicesJson | ConvertFrom-Json)
`$screenEase = `$services | Where-Object { `$_.unitId -eq 'screenease.service' } | Select-Object -First 1
Add-RemoteRecord 'A5.R2-service-unit-enumerated' (`$null -ne `$screenEase) "count=`$(`$services.Count)"
Add-RemoteRecord 'A5.R3-screenease-unit-active' (`$null -ne `$screenEase -and `$screenEase.state -eq 'Active' -and `$screenEase.Pid -gt 0) "state=`$(`$screenEase.state); pid=`$(`$screenEase.Pid)"
`$runner = Join-Path `$installResult.installRoot 'Runner\MyPowerTools.Runner.exe'
`$modules = Join-Path `$installResult.installRoot 'modules'
`$onceOutput = & `$runner --once --modules `$modules --data-root `$dataRoot 2>&1 | Out-String
`$onceExit = `$LASTEXITCODE
`$catalogOk = @('adb-forwarder','android-tools.notifications','doubao-agent','screenease','smartbird-thermostat') | Where-Object { `$onceOutput -match [regex]::Escape(`$_) }
Add-RemoteRecord 'A5.R4-installed-catalog-discovery' (`$onceExit -eq 0 -and `$catalogOk.Count -eq 5) "exit=`$onceExit; tools=`$(`$catalogOk -join ',')"
`$runnerOut = Join-Path `$testRoot 'runner.out.log'
`$runnerErr = Join-Path `$testRoot 'runner.err.log'
`$endpoint = "mypowertools.a5.`$runId"
`$instanceName = "MyPowerTools.Runner.A5.`$runId"
`$runnerProcess = Start-Process -FilePath `$runner -ArgumentList @('--modules',`$modules,'--data-root',`$dataRoot,'--endpoint-address',`$endpoint,'--instance-name',`$instanceName) -WindowStyle Hidden -PassThru -RedirectStandardOutput `$runnerOut -RedirectStandardError `$runnerErr
Start-Sleep -Seconds 4
`$shell = Join-Path `$installResult.installRoot 'Shell\MyPowerTools.Shell.Avalonia.exe'
`$shellOutput = & `$shell --smoke --timeout-ms 20000 --quit-runner --data-root `$dataRoot --endpoint-address `$endpoint 2>&1 | Out-String
`$shellExit = `$LASTEXITCODE
Add-RemoteRecord 'A5.R5-shell-runner-control-path' (`$shellExit -eq 0 -and `$shellOutput -match 'smoke connected') "exit=`$shellExit; output=`$shellOutput"
if (-not `$runnerProcess.HasExited) { Stop-Process -Id `$runnerProcess.Id -Force -ErrorAction SilentlyContinue }
& `$cli service stop screenease.service *> `$null
& `$cli service shutdown *> `$null
Start-Sleep -Milliseconds 700
& (Join-Path `$installResult.installRoot 'ServiceManager\MyPowerTools.ServiceManager.exe') --unregister-autostart --data-root `$dataRoot *> `$null
`$passed = @(`$remoteRecords | Where-Object { -not `$_.passed }).Count -eq 0
`$result = [ordered]@{
  passed = `$passed
  host = `$env:COMPUTERNAME
  os = [Environment]::OSVersion.VersionString
  installRoot = `$installResult.installRoot
  dataRoot = `$dataRoot
  suiteVersion = '$($manifest.suiteVersion)'
  records = `$remoteRecords
  runnerOnceOutput = `$onceOutput
  shellSmokeOutput = `$shellOutput
}
Write-Output ('A5_RESULT=' + (`$result | ConvertTo-Json -Depth 10 -Compress))
Remove-Item -LiteralPath `$archive -Force -ErrorAction SilentlyContinue
`$testRootFull = [IO.Path]::GetFullPath(`$testRoot)
`$tempRoot = [IO.Path]::GetFullPath(`$env:TEMP)
if (`$testRootFull.StartsWith((Join-Path `$tempRoot 'MyPowerTools-A5-'), [StringComparison]::OrdinalIgnoreCase)) {
  Remove-Item -LiteralPath `$testRootFull -Recurse -Force -ErrorAction SilentlyContinue
}
"@

$remoteScriptPath = Join-Path $EvidenceRoot "remote-script-$runId.ps1"
$remoteScript | Set-Content -LiteralPath $remoteScriptPath -Encoding UTF8
& scp -q $remoteScriptPath "${RemoteHost}:$remoteScriptName"
if ($LASTEXITCODE -ne 0) { throw "remote verifier scp failed with exit code $LASTEXITCODE" }
$remoteOutput = & ssh -o BatchMode=yes -o ConnectTimeout=10 $RemoteHost powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ".\$remoteScriptName" 2>&1 | Out-String
$remoteExit = $LASTEXITCODE
$remoteLog = Join-Path $EvidenceRoot "remote-$runId.log"
$remoteOutput | Set-Content -LiteralPath $remoteLog -Encoding UTF8
$resultLine = ($remoteOutput -split "`r?`n" | Where-Object { $_ -like 'A5_RESULT=*' } | Select-Object -Last 1)
$remoteResult = $null
if ($resultLine) { $remoteResult = $resultLine.Substring('A5_RESULT='.Length) | ConvertFrom-Json }
$remotePassed = $remoteExit -eq 0 -and $null -ne $remoteResult -and $remoteResult.passed
Add-Record 'A5.8-independent-windows-host' $remotePassed "host=$RemoteHost; exit=$remoteExit" $remoteLog

$overall = @($records | Where-Object { -not $_.passed }).Count -eq 0
[void](Write-Result -Phase 'remote' -Passed $overall -Remote $remoteResult)
exit $(if ($overall) { 0 } else { 1 })
