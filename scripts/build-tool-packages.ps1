[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputRoot = '',
    [switch]$SkipSdk
)

$ErrorActionPreference = 'Stop'
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot 'artifacts\tool-packages'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$allowedArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'artifacts'))
$allowedPrefix = $allowedArtifactsRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $OutputRoot.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must stay under $allowedArtifactsRoot"
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$FilePath failed with exit code $exitCode"
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Tool package source is missing: $Source"
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function Assert-DotnetSurfaceAssemblies {
    param([Parameter(Mandatory = $true)][string]$PackageRoot)

    $toolManifests = @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -File -Filter 'tool.json')
    foreach ($toolManifest in $toolManifests) {
        $tool = Get-Content -Raw -LiteralPath $toolManifest.FullName | ConvertFrom-Json
        foreach ($route in @($tool.routes)) {
            if (-not [string]::Equals([string]$route.surface.kind, 'dotnet', [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $assembly = [string]$route.surface.assembly
            if ([string]::IsNullOrWhiteSpace($assembly)) {
                throw "Dotnet surface route '$($route.routeId)' has no assembly in $($toolManifest.FullName)"
            }

            $assemblyPath = [System.IO.Path]::GetFullPath((Join-Path $toolManifest.DirectoryName $assembly))
            if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
                throw "Dotnet surface assembly is missing for route '$($route.routeId)': $assemblyPath"
            }
        }
    }
}

if (Test-Path -LiteralPath $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$toolArtifactsRoot = Join-Path $RepoRoot 'artifacts\tool-package-build'
$buildArguments = @(
    '-NoLogo', '-NoProfile', '-NonInteractive',
    '-File', (Join-Path $RepoRoot 'scripts\build-all-tools.ps1'),
    '-Configuration', 'Release',
    '-OutputRoot', $toolArtifactsRoot)
if ($SkipSdk) {
    $buildArguments += '-SkipSdk'
}
Invoke-Native -FilePath 'pwsh.exe' -ArgumentList $buildArguments

$sourceManifestPath = Join-Path $toolArtifactsRoot 'source-manifest.json'
if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) {
    throw "Tool source manifest is missing: $sourceManifestPath"
}

$sourceManifest = Get-Content -Raw -LiteralPath $sourceManifestPath | ConvertFrom-Json
foreach ($tool in @($sourceManifest.tools)) {
    $artifactDirectory = Join-Path $RepoRoot ([string]$tool.output -replace '/', '\')
    $runtimeDirectory = Join-Path $artifactDirectory 'runtime'
    $packageManifest = Join-Path $runtimeDirectory 'package.json'
    $moduleManifest = Join-Path $runtimeDirectory 'module.json'
    if (Test-Path -LiteralPath $packageManifest -PathType Leaf) {
        $packageId = [string](Get-Content -Raw -LiteralPath $packageManifest | ConvertFrom-Json).id
    }
    elseif (Test-Path -LiteralPath $moduleManifest -PathType Leaf) {
        $packageId = [string](Get-Content -Raw -LiteralPath $moduleManifest | ConvertFrom-Json).packageId
    }
    else {
        throw "Runtime package has no package.json or module.json: $runtimeDirectory"
    }

    $packageDestination = Join-Path $OutputRoot $packageId
    Copy-DirectoryContents -Source $runtimeDirectory -Destination $packageDestination
    Assert-DotnetSurfaceAssemblies -PackageRoot $packageDestination
}

Write-Host $OutputRoot
