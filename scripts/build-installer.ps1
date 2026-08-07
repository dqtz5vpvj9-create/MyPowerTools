<#
.SYNOPSIS
  Builds a portable, self-contained MyPowerTools Windows installer candidate.

.DESCRIPTION
  Builds the SDK and selected tool packages, publishes the independent Shell,
  Runner and ServiceManager processes, assembles the module catalog consumed by
  Runner, stages real Service Unit payloads, and emits a zip + SHA-256 digest.
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$RuntimeIdentifier = 'win-x64',
    [string[]]$ToolIds = @(),
    [switch]$SkipBuild,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionOutput = @(& (Join-Path $PSScriptRoot 'get-product-version.ps1') -RepoRoot $repoRoot |
        ForEach-Object { [string]$_ })
    $Version = [string]((($versionOutput -join [Environment]::NewLine) | ConvertFrom-Json).version)
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Invalid installer version '$Version'."
}
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$installParent = Join-Path $artifactsRoot 'install'
$candidateRoot = Join-Path $installParent $Version
$payloadRoot = Join-Path $candidateRoot 'payload'
$toolArtifactsRoot = Join-Path $artifactsRoot 'tools'
$selfContained = -not $FrameworkDependent

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$Activity
    )
    Write-Host "  > $Activity" -ForegroundColor DarkGray
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Activity failed with exit code $LASTEXITCODE."
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Source directory is missing: $Source"
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

if (-not $SkipBuild) {
    Write-Host '==> Building SDK and first-party tools...' -ForegroundColor Cyan
    $toolBuildArgs = @(
        '-NoLogo', '-NoProfile', '-NonInteractive',
        '-File', (Join-Path $repoRoot 'scripts\build-all-tools.ps1'),
        '-Configuration', 'Release'
    )
    foreach ($toolId in $ToolIds) {
        $toolBuildArgs += @('-ToolId', $toolId)
    }
    Invoke-Native -FilePath 'pwsh.exe' -ArgumentList $toolBuildArgs -Activity 'build-all-tools'
}

$sourceManifestPath = Join-Path $toolArtifactsRoot 'source-manifest.json'
if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) {
    throw "Tool source manifest is missing: $sourceManifestPath"
}
$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json
$selectedTools = @($sourceManifest.tools)
if ($ToolIds.Count -gt 0) {
    $selectedTools = @($selectedTools | Where-Object { $ToolIds -contains $_.toolId })
    $missing = @($ToolIds | Where-Object { $selectedTools.toolId -notcontains $_ })
    if ($missing.Count -gt 0) {
        throw "Tool artifacts are missing for: $($missing -join ', ')"
    }
}
if ($selectedTools.Count -eq 0) {
    throw 'No tool artifacts were selected.'
}

if (Test-Path -LiteralPath $candidateRoot) {
    $candidateFull = [System.IO.Path]::GetFullPath($candidateRoot)
    $allowedPrefix = [System.IO.Path]::GetFullPath($installParent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $candidateFull.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean candidate outside $installParent"
    }
    Remove-Item -LiteralPath $candidateRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null

Write-Host "==> Publishing MyPowerTools $Version ($RuntimeIdentifier)..." -ForegroundColor Cyan
$publishProjects = @(
    [pscustomobject]@{ Name = 'Shell'; Project = 'src\MyPowerTools.Shell.Avalonia\MyPowerTools.Shell.Avalonia.csproj'; Output = 'Shell' },
    [pscustomobject]@{ Name = 'Runner'; Project = 'src\MyPowerTools.Runner\MyPowerTools.Runner.csproj'; Output = 'Runner' },
    [pscustomobject]@{ Name = 'ServiceManager'; Project = 'src\MyPowerTools.ServiceManager\MyPowerTools.ServiceManager.csproj'; Output = 'ServiceManager' },
    [pscustomobject]@{ Name = 'CLI'; Project = 'src\MyPowerTools.Cli\MyPowerTools.Cli.csproj'; Output = 'Cli' },
    [pscustomobject]@{ Name = 'Elevated Broker'; Project = 'src\MyPowerTools.ElevatedBroker\MyPowerTools.ElevatedBroker.csproj'; Output = 'Broker' }
)
foreach ($project in $publishProjects) {
    $output = Join-Path $payloadRoot $project.Output
    $arguments = @(
        'publish', (Join-Path $repoRoot $project.Project),
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--output', $output,
        '--self-contained', $selfContained.ToString().ToLowerInvariant(),
        '--nologo', '--verbosity', 'minimal'
    )
    Invoke-Native -FilePath 'dotnet' -ArgumentList $arguments -Activity "publish $($project.Name)"
}

$visualOutput = Join-Path (Join-Path $payloadRoot 'Cli') 'visual'
Invoke-Native -FilePath 'dotnet' -ArgumentList @(
    'publish', (Join-Path $repoRoot 'src\Mpt.Cli.VisualTesting\Mpt.Cli.VisualTesting.csproj'),
    '--configuration', 'Release',
    '--runtime', $RuntimeIdentifier,
    '--output', $visualOutput,
    '--self-contained', $selfContained.ToString().ToLowerInvariant(),
    '--nologo', '--verbosity', 'minimal'
) -Activity 'publish VisualTesting CLI'

Copy-DirectoryContents -Source (Join-Path $repoRoot 'schemas') -Destination (Join-Path $payloadRoot 'schemas')
Copy-DirectoryContents -Source (Join-Path $repoRoot 'assets') -Destination (Join-Path $payloadRoot 'assets')
Copy-Item -LiteralPath $sourceManifestPath -Destination (Join-Path $payloadRoot 'tool-source-manifest.json') -Force

$modulesRoot = Join-Path $payloadRoot 'modules'
$packagesRoot = Join-Path $payloadRoot 'packages'
New-Item -ItemType Directory -Path $modulesRoot, $packagesRoot -Force | Out-Null
$installedTools = [System.Collections.Generic.List[object]]::new()
foreach ($tool in $selectedTools) {
    $artifactDirectory = Join-Path $repoRoot ($tool.output -replace '/', '\')
    $runtimeDirectory = Join-Path $artifactDirectory 'runtime'
    $packageManifest = Join-Path $runtimeDirectory 'package.json'
    $moduleManifest = Join-Path $runtimeDirectory 'module.json'
    if (Test-Path -LiteralPath $packageManifest -PathType Leaf) {
        $packageId = (Get-Content -LiteralPath $packageManifest -Raw | ConvertFrom-Json).id
    } elseif (Test-Path -LiteralPath $moduleManifest -PathType Leaf) {
        $packageId = (Get-Content -LiteralPath $moduleManifest -Raw | ConvertFrom-Json).packageId
    } else {
        throw "Runtime package has no package.json or module.json: $runtimeDirectory"
    }
    $moduleDestination = Join-Path $modulesRoot $packageId
    Copy-DirectoryContents -Source $runtimeDirectory -Destination $moduleDestination

    $mptPackage = Get-ChildItem -LiteralPath $artifactDirectory -Filter '*.mptpkg' -File | Select-Object -First 1
    if ($null -eq $mptPackage) {
        throw "Tool package archive is missing: $artifactDirectory"
    }
    Copy-Item -LiteralPath $mptPackage.FullName -Destination $packagesRoot -Force
    $surfacePackages = Join-Path $artifactDirectory 'surface'
    if (Test-Path -LiteralPath $surfacePackages -PathType Container) {
        Copy-DirectoryContents -Source $surfacePackages -Destination (Join-Path $packagesRoot 'surfaces')
    }
    $installedTools.Add([ordered]@{
        toolId = $tool.toolId
        version = $tool.version
        packageId = $packageId
        archive = $mptPackage.Name
    })
}

# Collect Service Units dynamically from the tool artifacts. build-all-tools.ps1
# already published each tool's declared units into artifacts/tools/<id>/<ver>/service-units/<unit-id>/.
# The installer copies whichever units exist — no first-party tool id is hard-coded here.
$payloadServiceUnitsRoot = Join-Path $payloadRoot 'service-units'
$collectedUnitIds = [System.Collections.Generic.List[string]]::new()
foreach ($tool in $selectedTools) {
    $artifactDirectory = Join-Path $repoRoot ($tool.output -replace '/', '\')
    $toolServiceUnitsRoot = Join-Path $artifactDirectory 'service-units'
    if (-not (Test-Path -LiteralPath $toolServiceUnitsRoot -PathType Container)) {
        continue
    }
    foreach ($unitDir in Get-ChildItem -LiteralPath $toolServiceUnitsRoot -Directory) {
        $unitManifestPath = Join-Path $unitDir.FullName 'unit-manifest.json'
        if (-not (Test-Path -LiteralPath $unitManifestPath -PathType Leaf)) {
            continue
        }
        $unitManifest = Get-Content -LiteralPath $unitManifestPath -Raw | ConvertFrom-Json
        $unitId = [string]$unitManifest.id
        $destination = Join-Path $payloadServiceUnitsRoot $unitId
        Copy-DirectoryContents -Source $unitDir.FullName -Destination $destination
        $collectedUnitIds.Add($unitId)
        Write-Host "  + Service Unit collected: $unitId (toolId=$($unitManifest.toolId))" -ForegroundColor DarkGray
    }
}

$installerTemplate = @'
[CmdletBinding()]
param(
    [string]$InstallBase = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [string]$ServiceManagerEndpoint = '',
    [string]$ServiceManagerInstanceName = '',
    [string]$ServiceUnitInstanceName = '',
    [switch]$IsolatedVerification,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
function Invoke-NativeQuiet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$ArgumentList = @()
    )

    $savedErrorActionPreference = $ErrorActionPreference
    $nativePreference = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    if ($null -ne $nativePreference) {
        $savedNativePreference = $nativePreference.Value
        Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $false
    }

    $ErrorActionPreference = 'Continue'
    try {
        & $FilePath @ArgumentList *> $null
        $nativeExitCode = $LASTEXITCODE
    }
    catch {
        $nativeExitCode = 1
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
        if ($null -ne $nativePreference) {
            Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $savedNativePreference
        }
    }

    return [int]$nativeExitCode
}

function Invoke-NativeCapture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$ArgumentList = @()
    )

    $savedErrorActionPreference = $ErrorActionPreference
    $nativePreference = Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue
    if ($null -ne $nativePreference) {
        $savedNativePreference = $nativePreference.Value
        Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $false
    }

    $ErrorActionPreference = 'Continue'
    try {
        $nativeOutput = @(& $FilePath @ArgumentList 2>&1)
        $nativeExitCode = $LASTEXITCODE
    }
    catch {
        $nativeOutput = @($_.Exception.Message)
        $nativeExitCode = 1
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
        if ($null -ne $nativePreference) {
            Set-Variable -Name PSNativeCommandUseErrorActionPreference -Value $savedNativePreference
        }
    }

    return [pscustomobject]@{
        ExitCode = [int]$nativeExitCode
        Output = $nativeOutput
    }
}

$version = '__VERSION__'
$sourcePayload = Join-Path $PSScriptRoot 'payload'
$installBaseFull = [IO.Path]::GetFullPath($InstallBase)
$canonicalInstallBase = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'))
$hasIsolatedVerificationContext = $IsolatedVerification.IsPresent -and
    -not [string]::IsNullOrWhiteSpace($ServiceManagerEndpoint) -and
    -not [string]::IsNullOrWhiteSpace($ServiceManagerInstanceName) -and
    -not [string]::IsNullOrWhiteSpace($ServiceUnitInstanceName)
if (-not $installBaseFull.Equals($canonicalInstallBase, [StringComparison]::OrdinalIgnoreCase) -and
    -not $hasIsolatedVerificationContext) {
    throw "MyPowerTools must be installed for the current user under $canonicalInstallBase. InstallBase=$installBaseFull"
}
$installRoot = Join-Path $installBaseFull $version
$dataRootFull = [IO.Path]::GetFullPath($DataRoot)
$smEndpointArg = @()
$cliEndpointArg = @()
if (-not [string]::IsNullOrWhiteSpace($ServiceManagerEndpoint)) {
    $smEndpointArg = @('--endpoint-address', $ServiceManagerEndpoint)
    $cliEndpointArg = @('--endpoint-address', $ServiceManagerEndpoint)
}
$smInstanceArg = @()
if (-not [string]::IsNullOrWhiteSpace($ServiceManagerInstanceName)) {
    $smInstanceArg = @('--instance-name', $ServiceManagerInstanceName)
}
$isolatedManager = $smEndpointArg.Count -gt 0 -or $smInstanceArg.Count -gt 0

New-Item -ItemType Directory -Path $installRoot, $dataRootFull -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $sourcePayload -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $installRoot -Recurse -Force
}

$managerDeployRoot = Join-Path $dataRootFull 'ServiceManager'
$managerUnitsRoot = Join-Path $managerDeployRoot 'units'
New-Item -ItemType Directory -Path $managerUnitsRoot -Force | Out-Null

# Deploy every Service Unit shipped in the payload. Each unit lives under
# service-units/<unit-id>/ (bin + unit-manifest.json). The exec path, working
# directory, pipe/address, heartbeat, token and dataRoots are rewritten to the
# install-specific versioned directory; when an isolated instance name is given
# the pipe name and token are namespaced so test runs never collide with the
# user's daily units. No tool id is hard-coded.
$payloadUnitsRoot = Join-Path $installRoot 'service-units'
$deployedUnitIds = @()
$deployedUnits = @()
$unitStatusRecords = @()
$managedUnitsStatePath = Join-Path $dataRootFull 'state\installed-service-units.json'
$previousManagedUnitIds = @()
if (Test-Path -LiteralPath $managedUnitsStatePath -PathType Leaf) {
    try {
        $previousManagedUnitIds = @((Get-Content -LiteralPath $managedUnitsStatePath -Raw | ConvertFrom-Json).unitIds)
    }
    catch {
        $previousManagedUnitIds = @()
    }
}
if (Test-Path -LiteralPath $payloadUnitsRoot -PathType Container) {
    foreach ($unitDir in Get-ChildItem -LiteralPath $payloadUnitsRoot -Directory) {
        $unitTemplatePath = Join-Path $unitDir.FullName 'unit-manifest.json'
        if (-not (Test-Path -LiteralPath $unitTemplatePath -PathType Leaf)) {
            continue
        }
        $unitManifest = Get-Content -LiteralPath $unitTemplatePath -Raw | ConvertFrom-Json
        $unitId = [string]$unitManifest.id
        $unitVersionRoot = Join-Path $managerDeployRoot "versions\$version\$unitId"
        New-Item -ItemType Directory -Path $unitVersionRoot -Force | Out-Null
        foreach ($binItem in Get-ChildItem -LiteralPath (Join-Path $unitDir.FullName 'bin') -Force) {
            Copy-Item -LiteralPath $binItem.FullName -Destination $unitVersionRoot -Recurse -Force
        }

        # The original exec is a bare filename (e.g. ScreenEase.Service.exe); resolve it
        # against the published bin output now staged in the versioned directory.
        $execName = [string]$unitManifest.exec
        $resolvedExec = Join-Path $unitVersionRoot $execName
        if (-not (Test-Path -LiteralPath $resolvedExec -PathType Leaf)) {
            # Fall back to the first .exe in the bin dir if the declared exec name differs.
            $firstExe = Get-ChildItem -LiteralPath $unitVersionRoot -Filter '*.exe' -File | Select-Object -First 1
            if ($null -ne $firstExe) { $resolvedExec = $firstExe.FullName }
        }
        $unitManifest.exec = $resolvedExec
        $unitManifest.workingDirectory = $unitVersionRoot

        # Namespace pipe / token for isolated test instances. The manifest's declared
        # readiness address and environment pipe name are updated to match.
        $unitPipe = $null
        $unitToken = [string]$unitManifest.instanceToken
        $unitHeartbeat = Join-Path $dataRootFull "state\$unitId.heartbeat"
        if ($unitManifest.readiness -and [string]$unitManifest.readiness.kind -eq 'pipe') {
            $unitPipe = [string]$unitManifest.readiness.address
        }
        if (-not [string]::IsNullOrWhiteSpace($ServiceUnitInstanceName)) {
            $safeInstanceName = $ServiceUnitInstanceName -replace '[^A-Za-z0-9_.-]', '-'
            if ([string]::IsNullOrWhiteSpace($safeInstanceName)) {
                throw 'ServiceUnitInstanceName does not contain a usable character.'
            }
            if ($null -ne $unitPipe -and $unitPipe.Length -gt 0) {
                $unitPipe = "$unitPipe.$safeInstanceName"
            }
            $unitToken = "$unitToken-$safeInstanceName"
        }

        # Rewrite the manifest arguments so --pipe / --heartbeat-file / --instance-token
        # point at the install-specific values. Only known flags are rewritten; units that
        # declare additional flags keep them untouched.
        $rewrittenArgs = [System.Collections.Generic.List[string]]::new()
        $args = @($unitManifest.arguments)
        for ($i = 0; $i -lt $args.Count; $i++) {
            $flag = [string]$args[$i]
            if ($flag -eq '--pipe' -and $null -ne $unitPipe -and $unitPipe.Length -gt 0) {
                $rewrittenArgs.Add('--pipe'); $rewrittenArgs.Add($unitPipe); $i++; continue
            }
            if ($flag -eq '--heartbeat-file') {
                $rewrittenArgs.Add('--heartbeat-file'); $rewrittenArgs.Add($unitHeartbeat); $i++; continue
            }
            if ($flag -eq '--instance-token') {
                $rewrittenArgs.Add('--instance-token'); $rewrittenArgs.Add($unitToken); $i++; continue
            }
            $rewrittenArgs.Add($flag)
        }
        $unitManifest.arguments = $rewrittenArgs.ToArray()
        if ($null -ne $unitPipe -and $unitPipe.Length -gt 0) {
            $unitManifest.readiness.address = $unitPipe
        }
        $unitManifest.instanceToken = $unitToken
        # Rewrite dataRoots to live under the install data root.
        $toolDataRoot = Join-Path $dataRootFull "state\tools\$([string]$unitManifest.toolId)"
        $unitManifest.dataRoots = @($toolDataRoot)
        $unitEnvironment = @{}
        if ($null -ne $unitManifest.environment) {
            foreach ($property in $unitManifest.environment.PSObject.Properties) {
                $unitEnvironment[$property.Name] = [string]$property.Value
            }
        }
        $unitEnvironment['MPT_DATA_ROOT'] = $dataRootFull
        $unitEnvironment['MPT_TOOL_DATA_ROOT'] = $toolDataRoot
        $unitEnvironment['MPT_INSTALL_ROOT'] = $installRoot
        $unitManifest.environment = $unitEnvironment

        $unitManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $managerUnitsRoot "$unitId.json") -Encoding UTF8
        $deployedUnitIds += $unitId
        $deployedUnits += [pscustomobject]@{
            unitId = $unitId
            autostart = [bool]$unitManifest.autostart
        }
    }
}

# Remove only units recorded as managed by an earlier MyPowerTools installation.
# User-authored manifests that share the deploy root remain untouched.
foreach ($staleUnitId in @($previousManagedUnitIds | Where-Object { $_ -and $_ -notin $deployedUnitIds })) {
    $staleManifestPath = Join-Path $managerUnitsRoot "$staleUnitId.json"
    if (Test-Path -LiteralPath $staleManifestPath -PathType Leaf) {
        Remove-Item -LiteralPath $staleManifestPath -Force
    }
}

$serviceManager = Join-Path $installRoot 'ServiceManager\MyPowerTools.ServiceManager.exe'
if (-not $isolatedManager) {
    $registerExitCode = Invoke-NativeQuiet -FilePath $serviceManager -ArgumentList @('--register-autostart', '--data-root', $dataRootFull)
    if ($registerExitCode -ne 0) { throw 'ServiceManager autostart registration failed.' }
}

$managerProcess = $null
if (-not $isolatedManager) {
    $managerProcess = Get-Process -Name 'MyPowerTools.ServiceManager' -ErrorAction SilentlyContinue | Select-Object -First 1
}
if ($null -eq $managerProcess) {
    $managerLogRoot = Join-Path $dataRootFull 'logs'
    New-Item -ItemType Directory -Path $managerLogRoot -Force | Out-Null
    $managerArguments = @('--data-root', $dataRootFull, '--deploy-root', $managerDeployRoot) + $smEndpointArg + $smInstanceArg
    $managerProcess = Start-Process -FilePath $serviceManager -ArgumentList $managerArguments -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $managerLogRoot 'servicemanager.stdout.log') -RedirectStandardError (Join-Path $managerLogRoot 'servicemanager.stderr.log')
}

$env:MPT_DATA_ROOT = $dataRootFull
if (-not [string]::IsNullOrWhiteSpace($ServiceManagerEndpoint)) {
    $env:MPT_SERVICEMANAGER_ENDPOINT = $ServiceManagerEndpoint
}
$cli = Join-Path $installRoot 'Cli\MyPowerTools.Cli.exe'
$managerReady = $false
$reloadExitCode = -1
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    $reloadExitCode = Invoke-NativeQuiet -FilePath $cli -ArgumentList (@('service', 'reload') + $cliEndpointArg)
    if ($reloadExitCode -eq 0) {
        $managerReady = $true
        break
    }

    $managerProcess.Refresh()
    if ($managerProcess.HasExited) {
        break
    }

    Start-Sleep -Milliseconds 500
}
if (-not $managerReady) {
    $tokenPath = Join-Path $dataRootFull 'state\servicemanager.token'
    throw "ServiceManager did not become ready after installation. reloadExit=$reloadExitCode; managerExited=$($managerProcess.HasExited); tokenExists=$(Test-Path -LiteralPath $tokenPath)"
}

# Reload performs an atomic stop/update/start for changed live units. Start remains idempotent
# and activates newly installed units whose manifests opt into autostart.
foreach ($unit in $deployedUnits) {
    $unitId = [string]$unit.unitId
    if ([bool]$unit.autostart) {
        $startExitCode = Invoke-NativeQuiet -FilePath $cli -ArgumentList (@('service', 'start', $unitId) + $cliEndpointArg)
        if ($startExitCode -ne 0) { throw "Service Unit activation failed: $unitId" }
    }
    $statusResult = Invoke-NativeCapture -FilePath $cli -ArgumentList (@('service', 'status', $unitId) + $cliEndpointArg)
    if ($statusResult.ExitCode -ne 0) { throw "Service Unit status query failed: $unitId" }
    $unitStatus = ($statusResult.Output | Out-String) | ConvertFrom-Json
    $unitStatusRecords += [pscustomobject]@{
        unitId = $unitId
        state = $unitStatus.state
        pid = $unitStatus.Pid
    }
}

New-Item -ItemType Directory -Path (Split-Path -Parent $managedUnitsStatePath) -Force | Out-Null
[ordered]@{ unitIds = @($deployedUnitIds); updatedAt = [DateTimeOffset]::UtcNow.ToString('O') } |
    ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $managedUnitsStatePath -Encoding UTF8

$result = [ordered]@{
    version = $version
    installedAt = [DateTimeOffset]::UtcNow.ToString('O')
    installRoot = $installRoot
    dataRoot = $dataRootFull
    serviceManager = $serviceManager
    serviceManagerPid = $managerProcess.Id
    serviceManagerEndpoint = $ServiceManagerEndpoint
    serviceManagerInstanceName = $ServiceManagerInstanceName
    units = $deployedUnitIds
    unitStatuses = $unitStatusRecords
    serviceUnitInstanceName = $ServiceUnitInstanceName
}
$resultPath = Join-Path $installRoot 'install-result.json'
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding UTF8

if (-not $NoLaunch) {
    Start-Process -FilePath (Join-Path $installRoot 'Shell\MyPowerTools.Shell.Avalonia.exe') -WorkingDirectory $installRoot
}

Write-Output $resultPath
'@
$installer = $installerTemplate.Replace('__VERSION__', $Version)
$installerPath = Join-Path $candidateRoot 'install.ps1'
$installer | Set-Content -LiteralPath $installerPath -Encoding UTF8

$fileHashes = Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($payloadRoot, $_.FullName).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        size = $_.Length
    }
}
$candidateManifest = [ordered]@{
    schemaVersion = 1
    suiteVersion = $Version
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $selfContained
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    tools = $installedTools
    serviceUnits = $collectedUnitIds
    files = $fileHashes
}
$candidateManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $candidateRoot 'candidate-manifest.json') -Encoding UTF8

$zipPath = Join-Path $installParent "MyPowerTools-$Version-$RuntimeIdentifier.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $candidateRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
$digest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$digest  $(Split-Path -Leaf $zipPath)" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII

Write-Host "==> Candidate directory: $candidateRoot" -ForegroundColor Green
Write-Host "==> Installer archive: $zipPath" -ForegroundColor Green
Write-Host "==> SHA-256: $digest" -ForegroundColor Green
