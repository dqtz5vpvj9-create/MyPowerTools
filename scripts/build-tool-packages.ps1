[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputRoot = ''
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

if (Test-Path -LiteralPath $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

foreach ($toolId in @(
    'adb-forwarder',
    'remote-notifications',
    'doubao-computer-use',
    'screenease',
    'smartbird-thermostat'
)) {
    $buildScript = Join-Path $RepoRoot "tools\$toolId\build.ps1"
    if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
        throw "Required tool build entry is missing: $buildScript"
    }
    Invoke-Native -FilePath 'pwsh.exe' -ArgumentList @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-File', $buildScript,
        '-MyPowerToolsRepoRoot', $RepoRoot)
}

$packageSources = [ordered]@{
    'adb-forwarder' = Join-Path $RepoRoot 'tools\adb-forwarder\artifacts\package'
    'android-tools-suite' = Join-Path $RepoRoot 'tools\remote-notifications\artifacts\package\android-tools-suite'
    'doubao-agent' = Join-Path $RepoRoot 'tools\doubao-computer-use\artifacts\package'
    'screenease' = Join-Path $RepoRoot 'tools\screenease\artifacts\package'
    'smartbird-thermostat' = Join-Path $RepoRoot 'tools\smartbird-thermostat\artifacts\package'
}
foreach ($entry in $packageSources.GetEnumerator()) {
    Copy-DirectoryContents -Source $entry.Value -Destination (Join-Path $OutputRoot $entry.Key)
}

Write-Host $OutputRoot
