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
    $savedErrorActionPreference = $ErrorActionPreference
    $nativePreference = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    if ($null -ne $nativePreference) {
        $savedNativePreference = $nativePreference.Value
        Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $false
    }

    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @ArgumentList 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    catch {
        $output = $_.Exception.ToString()
        $exitCode = 1
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
        if ($null -ne $nativePreference) {
            Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $savedNativePreference
        }
    }

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

$expectedTools = @('adb-forwarder', 'doubao-computer-use', 'local-lag-cleaner', 'paste-image', 'remote-notifications', 'screenease', 'smartbird-thermostat')
$actualTools = @($manifest.tools | ForEach-Object { [string]$_.toolId })
$toolInventoryOk = $actualTools.Count -eq $expectedTools.Count -and @($expectedTools | Where-Object { $actualTools -notcontains $_ }).Count -eq 0
Add-Record 'A5.1-candidate-tool-inventory' $toolInventoryOk "tools=$($actualTools -join ',')" $manifestPath

$expectedServiceUnits = @('adb-forwarder.service', 'doubao-agent.controller.service', 'remote-notifications.service', 'screenease.service')
$actualServiceUnits = @($manifest.serviceUnits | ForEach-Object { [string]$_ })
$serviceUnitInventoryOk = $actualServiceUnits.Count -eq $expectedServiceUnits.Count -and
    @($expectedServiceUnits | Where-Object { $actualServiceUnits -notcontains $_ }).Count -eq 0
Add-Record 'A5.1b-service-unit-inventory' $serviceUnitInventoryOk "units=$($actualServiceUnits -join ',')" $manifestPath

$criticalPaths = @(
    'Shell\MyPowerTools.Shell.Avalonia.exe',
    'Runner\MyPowerTools.Runner.exe',
    'ServiceManager\MyPowerTools.ServiceManager.exe',
    'Cli\MyPowerTools.Cli.exe'
)
$unitExecById = @{}
$unitManifestErrors = [Collections.Generic.List[string]]::new()
foreach ($unitId in $expectedServiceUnits) {
    $unitManifestRelative = "service-units\$unitId\unit-manifest.json"
    $unitManifestPath = Join-Path $payloadRoot $unitManifestRelative
    $criticalPaths += $unitManifestRelative
    if (-not (Test-Path -LiteralPath $unitManifestPath -PathType Leaf)) {
        $unitManifestErrors.Add("${unitId}:manifest-missing")
        continue
    }

    $unitManifest = Get-Content -LiteralPath $unitManifestPath -Raw | ConvertFrom-Json
    if (-not [string]::Equals([string]$unitManifest.id, $unitId, [StringComparison]::Ordinal)) {
        $unitManifestErrors.Add("${unitId}:manifest-id=$($unitManifest.id)")
        continue
    }

    $execName = [IO.Path]::GetFileName([string]$unitManifest.exec)
    if ([string]::IsNullOrWhiteSpace($execName)) {
        $unitManifestErrors.Add("${unitId}:exec-missing")
        continue
    }

    $unitExecById[$unitId] = $execName
    $criticalPaths += "service-units\$unitId\bin\$execName"
}
$criticalPaths = @($criticalPaths | Select-Object -Unique)
$missingCritical = @($criticalPaths | Where-Object { -not (Test-Path -LiteralPath (Join-Path $payloadRoot $_) -PathType Leaf) })
Add-Record 'A5.2-critical-process-payloads' ($missingCritical.Count -eq 0 -and $unitManifestErrors.Count -eq 0) "missing=$($missingCritical -join ','); manifestErrors=$($unitManifestErrors -join ',')" $payloadRoot

$surfaceDlls = @(Get-ChildItem -LiteralPath (Join-Path $payloadRoot 'modules') -Recurse -File -Filter '*.Surface.dll')
Add-Record 'A5.3-five-loadable-surfaces' ($surfaceDlls.Count -eq 5) "surfaceDlls=$($surfaceDlls.Count)" (($surfaceDlls.FullName) -join ';')

$packages = @(Get-ChildItem -LiteralPath (Join-Path $payloadRoot 'packages') -File -Filter '*.mptpkg')
Add-Record 'A5.4-seven-independent-packages' ($packages.Count -eq 7) "packages=$($packages.Count)" (($packages.FullName) -join ';')

Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageUnitErrors = [Collections.Generic.List[string]]::new()
foreach ($unitId in $expectedServiceUnits) {
    if (-not $unitExecById.ContainsKey($unitId)) {
        $packageUnitErrors.Add("${unitId}:exec-unknown")
        continue
    }

    $manifestEntryName = "service-units/$unitId/unit-manifest.json"
    $execEntryName = "service-units/$unitId/bin/$($unitExecById[$unitId])"
    $matchingPackages = [Collections.Generic.List[string]]::new()
    foreach ($package in $packages) {
        $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
            $hasManifest = $entryNames -contains $manifestEntryName
            $hasExec = $entryNames -contains $execEntryName
            if ($hasManifest -and $hasExec) {
                $matchingPackages.Add($package.Name)
            }
            elseif ($hasManifest -or $hasExec) {
                $packageUnitErrors.Add("${unitId}:$($package.Name):partial")
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    if ($matchingPackages.Count -ne 1) {
        $packageUnitErrors.Add("${unitId}:package-count=$($matchingPackages.Count)")
    }
}
Add-Record 'A5.4b-service-units-in-independent-packages' ($packageUnitErrors.Count -eq 0) "errors=$($packageUnitErrors -join ',')" (($packages.FullName) -join ';')

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
    $discovered = @('adb-forwarder', 'android-tools.notifications', 'doubao-agent', 'local-lag-cleaner', 'paste-image', 'screenease', 'smartbird-thermostat') |
        Where-Object { $runnerResult.Output -match [regex]::Escape($_) }
    Add-Record 'A5.7-local-runner-discovery' ($runnerResult.ExitCode -eq 0 -and $discovered.Count -eq 7) "exit=$($runnerResult.ExitCode); discovered=$($discovered -join ',')" $runnerLog
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
$candidateScpArguments = @('-q', $archive, "${RemoteHost}:$remoteArchiveName")
& scp @candidateScpArguments
if ($LASTEXITCODE -ne 0) { throw "scp failed with exit code $LASTEXITCODE" }

$remoteScriptSource = Join-Path $PSScriptRoot 'verify-release-candidate.remote.ps1'
if (-not (Test-Path -LiteralPath $remoteScriptSource -PathType Leaf)) {
    throw "Remote verifier source is missing: $remoteScriptSource"
}
$remoteScriptPath = Join-Path $EvidenceRoot "remote-script-$runId.ps1"
Copy-Item -LiteralPath $remoteScriptSource -Destination $remoteScriptPath -Force
$scriptScpArguments = @('-q', $remoteScriptPath, "${RemoteHost}:$remoteScriptName")
& scp @scriptScpArguments
if ($LASTEXITCODE -ne 0) { throw "remote verifier scp failed with exit code $LASTEXITCODE" }
$sshArguments = @(
    '-o', 'BatchMode=yes',
    '-o', 'ConnectTimeout=10',
    '-o', 'ServerAliveInterval=15',
    '-o', 'ServerAliveCountMax=4',
    $RemoteHost,
    'powershell.exe',
    '-NoLogo',
    '-NoProfile',
    '-NonInteractive',
    '-File', ".\$remoteScriptName",
    '-RunId', $runId,
    '-ArchiveName', $remoteArchiveName,
    '-SuiteVersion', [string]$manifest.suiteVersion
)
$remoteOutput = & ssh @sshArguments 2>&1 | Out-String
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
