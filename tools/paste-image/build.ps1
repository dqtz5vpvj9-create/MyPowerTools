[CmdletBinding()]
param(
    [string] $MyPowerToolsRepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot = if ([string]::IsNullOrWhiteSpace($MyPowerToolsRepoRoot)) {
    [System.IO.Path]::GetFullPath((Join-Path $toolRoot '..\..'))
} else {
    [System.IO.Path]::GetFullPath($MyPowerToolsRepoRoot)
}
$projectPath = Join-Path $toolRoot 'current-integration\src\PasteImage.MyPowerTools\PasteImage.MyPowerTools.csproj'
$surfaceProjectPath = Join-Path $toolRoot 'current-integration\src\PasteImage.Surface\PasteImage.Surface.csproj'
$modulePackageRoot = Join-Path $toolRoot 'current-integration\modules\paste-image'
$surfacePackageRoot = Join-Path $modulePackageRoot 'ui\surface'
$repositoryModuleRoot = Join-Path $repoRoot 'modules\paste-image'
$artifactsRoot = Join-Path $toolRoot 'artifacts'
$artifactPackage = Join-Path $artifactsRoot 'package'

if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'src\MyPowerTools.Abstractions\MyPowerTools.Abstractions.csproj') -PathType Leaf)) {
    throw "MyPowerToolsRepoRoot '$repoRoot' is invalid."
}

$dotnet = Get-Command 'dotnet' -CommandType Application -ErrorAction Stop
foreach ($runtimeRoot in @($modulePackageRoot, $repositoryModuleRoot)) {
    foreach ($staleRuntimeFile in @(
        'System.Drawing.Common.dll',
        'System.Private.Windows.Core.dll',
        'System.Private.Windows.GdiPlus.dll',
        'Microsoft.Win32.SystemEvents.dll'
    )) {
        $staleRuntimePath = Join-Path $runtimeRoot $staleRuntimeFile
        if (Test-Path -LiteralPath $staleRuntimePath -PathType Leaf) {
            Remove-Item -LiteralPath $staleRuntimePath -Force
        }
    }
}
$dotnetArguments = @(
    'build'
    $projectPath
    '--configuration'
    'Release'
    '--nologo'
    "-p:MyPowerToolsRepoRoot=$repoRoot"
)
& $dotnet.Source @dotnetArguments
$dotnetExitCode = $LASTEXITCODE
if ($dotnetExitCode -ne 0) {
    throw "dotnet build failed with exit code $dotnetExitCode."
}

$surfaceOutput = Join-Path $artifactsRoot 'surface'
$surfaceArguments = @(
    'build'
    $surfaceProjectPath
    '--configuration'
    'Release'
    '--nologo'
    '--output'
    $surfaceOutput
    "-p:MyPowerToolsRepoRoot=$repoRoot"
)
& $dotnet.Source @surfaceArguments
$surfaceExitCode = $LASTEXITCODE
if ($surfaceExitCode -ne 0) {
    throw "dotnet surface build failed with exit code $surfaceExitCode."
}

New-Item -ItemType Directory -Path $surfacePackageRoot -Force | Out-Null
foreach ($extension in @('*.dll', '*.pdb', '*.deps.json')) {
    Get-ChildItem -LiteralPath $surfaceOutput -File -Filter $extension |
        Copy-Item -Destination $surfacePackageRoot -Force
}

New-Item -ItemType Directory -Path $repositoryModuleRoot -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $modulePackageRoot -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $repositoryModuleRoot -Recurse -Force
}

if (Test-Path -LiteralPath $artifactPackage) {
    $resolvedTarget = [System.IO.Path]::GetFullPath($artifactPackage)
    $allowedPrefix = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTarget.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove artifact path outside '$artifactsRoot'."
    }
    Remove-Item -LiteralPath $artifactPackage -Recurse -Force
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
Copy-Item -LiteralPath $modulePackageRoot -Destination $artifactPackage -Recurse -Force

$expectedAssembly = Join-Path $artifactPackage 'PasteImage.MyPowerTools.dll'
if (-not (Test-Path -LiteralPath $expectedAssembly -PathType Leaf)) {
    throw "Expected adapter assembly '$expectedAssembly' is missing."
}

foreach ($runtimeFile in @('PasteImage.MyPowerTools.deps.json')) {
    $runtimePath = Join-Path $artifactPackage $runtimeFile
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
        throw "Expected Paste Image runtime dependency '$runtimePath' is missing."
    }
}

$expectedSurfaceAssembly = Join-Path $artifactPackage 'ui\surface\PasteImage.Surface.dll'
if (-not (Test-Path -LiteralPath $expectedSurfaceAssembly -PathType Leaf)) {
    throw "Expected Surface assembly '$expectedSurfaceAssembly' is missing."
}

Write-Output "Release package staged at $artifactPackage"
