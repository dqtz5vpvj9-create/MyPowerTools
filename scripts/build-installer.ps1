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
    [string]$Version = '0.2.0',
    [string]$RuntimeIdentifier = 'win-x64',
    [string[]]$ToolIds = @(),
    [switch]$SkipBuild,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
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
    [pscustomobject]@{ Name = 'CLI'; Project = 'src\MyPowerTools.Cli\MyPowerTools.Cli.csproj'; Output = 'Cli' }
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

# ScreenEase is currently the only first-party product with a real independently
# managed Service Unit executable. Other products stay out of the unit catalog
# until their dedicated worker/controller process is shipped.
$serviceUnitRoot = Join-Path $payloadRoot 'service-units\screenease.service'
$serviceBinaryRoot = Join-Path $serviceUnitRoot 'bin'
Invoke-Native -FilePath 'dotnet' -ArgumentList @(
    'publish', (Join-Path $repoRoot 'tools\screenease\current-integration\src\ScreenEase.Service\ScreenEase.Service.csproj'),
    '--configuration', 'Release',
    '--runtime', $RuntimeIdentifier,
    '--output', $serviceBinaryRoot,
    '--self-contained', $selfContained.ToString().ToLowerInvariant(),
    '--nologo', '--verbosity', 'minimal'
) -Activity 'publish ScreenEase Service Unit'
Copy-Item -LiteralPath (Join-Path $repoRoot 'tools\screenease\current-integration\src\ScreenEase.Service\unit-manifest.json') -Destination (Join-Path $serviceUnitRoot 'unit-manifest.json') -Force

$installerTemplate = @'
[CmdletBinding()]
param(
    [string]$InstallBase = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$version = '__VERSION__'
$sourcePayload = Join-Path $PSScriptRoot 'payload'
$installRoot = Join-Path ([IO.Path]::GetFullPath($InstallBase)) $version
$dataRootFull = [IO.Path]::GetFullPath($DataRoot)

New-Item -ItemType Directory -Path $installRoot, $dataRootFull -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $sourcePayload -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $installRoot -Recurse -Force
}

$managerDeployRoot = Join-Path $dataRootFull 'ServiceManager'
$managerUnitsRoot = Join-Path $managerDeployRoot 'units'
$unitVersionRoot = Join-Path $managerDeployRoot "versions\$version\screenease.service"
New-Item -ItemType Directory -Path $managerUnitsRoot, $unitVersionRoot -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $installRoot 'service-units\screenease.service\bin') -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $unitVersionRoot -Recurse -Force
}

$unitTemplatePath = Join-Path $installRoot 'service-units\screenease.service\unit-manifest.json'
$unitManifest = Get-Content -LiteralPath $unitTemplatePath -Raw | ConvertFrom-Json
$unitManifest.exec = Join-Path $unitVersionRoot 'ScreenEase.Service.exe'
$unitManifest.workingDirectory = $unitVersionRoot
$unitManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $managerUnitsRoot 'screenease.service.json') -Encoding UTF8

$serviceManager = Join-Path $installRoot 'ServiceManager\MyPowerTools.ServiceManager.exe'
& $serviceManager --register-autostart --data-root $dataRootFull *> $null
if ($LASTEXITCODE -ne 0) { throw 'ServiceManager autostart registration failed.' }

$managerProcess = Get-Process -Name 'MyPowerTools.ServiceManager' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $managerProcess) {
    $managerProcess = Start-Process -FilePath $serviceManager -ArgumentList @('--data-root', $dataRootFull, '--deploy-root', $managerDeployRoot) -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru
}

$env:MPT_DATA_ROOT = $dataRootFull
$cli = Join-Path $installRoot 'Cli\MyPowerTools.Cli.exe'
$managerReady = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    & $cli service reload *> $null
    if ($LASTEXITCODE -eq 0) {
        $managerReady = $true
        break
    }
    Start-Sleep -Milliseconds 500
}
if (-not $managerReady) { throw 'ServiceManager did not become ready after installation.' }

& $cli service start screenease.service *> $null
if ($LASTEXITCODE -ne 0) { throw 'ScreenEase Service Unit activation failed.' }
$unitStatus = & $cli service status screenease.service | ConvertFrom-Json

$result = [ordered]@{
    version = $version
    installedAt = [DateTimeOffset]::UtcNow.ToString('O')
    installRoot = $installRoot
    dataRoot = $dataRootFull
    serviceManager = $serviceManager
    serviceManagerPid = $managerProcess.Id
    units = @('screenease.service')
    serviceUnitState = $unitStatus.state
    serviceUnitPid = $unitStatus.Pid
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
    serviceUnits = @('screenease.service')
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
