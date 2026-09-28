[CmdletBinding()]
param(
    [string] $MyPowerToolsRepoRoot,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot = if ([string]::IsNullOrWhiteSpace($MyPowerToolsRepoRoot)) {
    [System.IO.Path]::GetFullPath((Join-Path $toolRoot '..\..'))
} else {
    [System.IO.Path]::GetFullPath($MyPowerToolsRepoRoot)
}
$sdkToolRoot = Join-Path $toolRoot 'sdk-tool'
$sdkToolProject = Join-Path $sdkToolRoot 'src\LocalLagCleaner.Tool\LocalLagCleaner.Tool.csproj'
$runtimeProject = Join-Path $sdkToolRoot 'src\LocalLagCleaner.Runtime\LocalLagCleaner.Runtime.csproj'
$standaloneProject = Join-Path $toolRoot 'original-source\src\LocalLagCleaner.Cli\LocalLagCleaner.Cli.csproj'
$mptCliProject = Join-Path $repoRoot 'src\MyPowerTools.Cli\MyPowerTools.Cli.csproj'
$mptCliExecutable = Join-Path $repoRoot "artifacts\build\bin\MyPowerTools.Cli\$($Configuration.ToLowerInvariant())\MyPowerTools.Cli.exe"
$artifactsRoot = Join-Path $toolRoot 'artifacts'
$artifactCli = Join-Path $artifactsRoot 'cli'
$artifactPackage = Join-Path $artifactsRoot 'local-lag-cleaner.mptpkg'
$artifactRuntime = Join-Path $artifactsRoot 'package'

if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'MyPowerTools.slnx') -PathType Leaf) -or
    -not (Test-Path -LiteralPath $mptCliProject -PathType Leaf)) {
    throw "MyPowerToolsRepoRoot '$repoRoot' is invalid."
}

# The packaged layout is declared by tool.json and must not follow the compile configuration:
# a Debug developer build still has to produce the runtime and Surface where the manifest, the
# staging step and the installed health check look for them. The projects are compiled with the
# requested configuration and their outputs are written into the declared directories, so the
# packer sees them (the raw bin/Debug layout stays ignored) and the source manifest stays truthful.
$sourceToolManifestPath = Join-Path $sdkToolRoot 'tool.json'
if (-not (Test-Path -LiteralPath $sourceToolManifestPath -PathType Leaf)) {
    throw "Tool manifest was not found: $sourceToolManifestPath"
}
$sourceToolManifest = Get-Content -LiteralPath $sourceToolManifestPath -Raw | ConvertFrom-Json
$declaredRuntimeArtifact = [string]$sourceToolManifest.runtime.command
$declaredSurfaceArtifacts = @(
    $sourceToolManifest.routes |
        ForEach-Object {
            $surfaceProperty = $_.PSObject.Properties['surface']
            if ($null -eq $surfaceProperty -or $null -eq $surfaceProperty.Value) { return }
            $assemblyProperty = $surfaceProperty.Value.PSObject.Properties['assembly']
            if ($null -eq $assemblyProperty) { return }
            [string]$assemblyProperty.Value
        } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
if ([string]::IsNullOrWhiteSpace($declaredRuntimeArtifact) -or $declaredSurfaceArtifacts.Count -eq 0) {
    throw 'tool.json must declare runtime.command and at least one route surface.assembly.'
}
$sdkToolPrefix = [System.IO.Path]::GetFullPath($sdkToolRoot).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$declaredRuntimePath = [System.IO.Path]::GetFullPath((Join-Path $sdkToolRoot $declaredRuntimeArtifact))
$declaredSurfacePaths = @(
    $declaredSurfaceArtifacts | ForEach-Object { [System.IO.Path]::GetFullPath((Join-Path $sdkToolRoot $_)) }
)
foreach ($declaredPath in @($declaredRuntimePath) + $declaredSurfacePaths) {
    if (-not $declaredPath.StartsWith($sdkToolPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "tool.json build output escapes the tool directory: $declaredPath"
    }
}
$surfaceProjectAssembly = [System.IO.Path]::GetFileNameWithoutExtension($sdkToolProject)
$surfaceOutputDirectory = @(
    $declaredSurfacePaths |
        Where-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) -eq $surfaceProjectAssembly } |
        ForEach-Object { Split-Path -Parent $_ } |
        Select-Object -Unique
)
if ($surfaceOutputDirectory.Count -ne 1) {
    throw "Expected exactly one declared Surface output for '$surfaceProjectAssembly', found $($surfaceOutputDirectory.Count)."
}
$runtimeOutputDirectory = Split-Path -Parent $declaredRuntimePath

$dotnetCommand = Get-Command 'dotnet' -CommandType Application -ErrorAction Stop

# Only the declared build output directories are released; nothing outside them is touched.
foreach ($outputDirectory in @($surfaceOutputDirectory[0], $runtimeOutputDirectory)) {
    if (Test-Path -LiteralPath $outputDirectory -PathType Container) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$sdkToolArguments = @(
    'build'
    $sdkToolProject
    '--configuration'
    $Configuration
    '--output'
    $surfaceOutputDirectory[0]
    '--nologo'
)
& $dotnetCommand.Source @sdkToolArguments
$sdkToolExitCode = $LASTEXITCODE
if ($sdkToolExitCode -ne 0) {
    throw "SDK tool build failed with exit code $sdkToolExitCode."
}

$runtimeArguments = @(
    'build'
    $runtimeProject
    '--configuration'
    $Configuration
    '--output'
    $runtimeOutputDirectory
    '--nologo'
)
& $dotnetCommand.Source @runtimeArguments
$runtimeExitCode = $LASTEXITCODE
if ($runtimeExitCode -ne 0) {
    throw "Isolated runtime build failed with exit code $runtimeExitCode."
}

$standaloneArguments = @(
    'publish'
    $standaloneProject
    '--configuration'
    $Configuration
    '--nologo'
    '--output'
    $artifactCli
    '--self-contained'
    'false'
)
& $dotnetCommand.Source @standaloneArguments
$standaloneExitCode = $LASTEXITCODE
if ($standaloneExitCode -ne 0) {
    throw "Standalone CLI publish failed with exit code $standaloneExitCode."
}

$mptCliArguments = @(
    'build'
    $mptCliProject
    '--configuration'
    $Configuration
    '--nologo'
)
& $dotnetCommand.Source @mptCliArguments
$mptCliExitCode = $LASTEXITCODE
if ($mptCliExitCode -ne 0) {
    throw "MyPowerTools CLI build failed with exit code $mptCliExitCode."
}
if (-not (Test-Path -LiteralPath $mptCliExecutable -PathType Leaf)) {
    throw "Expected MyPowerTools CLI '$mptCliExecutable' is missing."
}

# Fail before packing: the packer copies whatever it finds, so a missing runtime, Surface or
# dependency manifest used to be staged as an 11-file metadata-only module.
foreach ($declaredArtifact in @($declaredRuntimePath) + $declaredSurfacePaths) {
    if (-not (Test-Path -LiteralPath $declaredArtifact -PathType Leaf)) {
        throw "Declared package artifact was not produced: $declaredArtifact (configuration $Configuration)."
    }
    $artifactDirectory = Split-Path -Parent $declaredArtifact
    $artifactStem = [System.IO.Path]::GetFileNameWithoutExtension($declaredArtifact)
    if (-not (Test-Path -LiteralPath (Join-Path $artifactDirectory ($artifactStem + '.deps.json')) -PathType Leaf)) {
        throw "Declared artifact dependency '$artifactStem.deps.json' is missing next to $declaredArtifact."
    }
    if ([System.IO.Path]::GetExtension($declaredArtifact) -ieq '.exe' -and
        -not (Test-Path -LiteralPath (Join-Path $artifactDirectory ($artifactStem + '.runtimeconfig.json')) -PathType Leaf)) {
        throw "Declared artifact runtime configuration '$artifactStem.runtimeconfig.json' is missing next to $declaredArtifact."
    }
}

$validateArguments = @(
    'validate'
    'tool'
    $sdkToolRoot
)
& $mptCliExecutable @validateArguments
$validateExitCode = $LASTEXITCODE
if ($validateExitCode -ne 0) {
    throw "Tool SDK validation failed with exit code $validateExitCode."
}

$packArguments = @(
    'pack'
    'tool'
    $sdkToolRoot
    '--output'
    $artifactPackage
)
& $mptCliExecutable @packArguments
$packExitCode = $LASTEXITCODE
if ($packExitCode -ne 0) {
    throw "Tool SDK packaging failed with exit code $packExitCode."
}

$expectedStandalone = Join-Path $artifactCli 'local-lag-cleaner.exe'
foreach ($expectedPath in @($expectedStandalone, $artifactPackage)) {
    if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf)) {
        throw "Expected build output '$expectedPath' is missing."
    }
}

if (Test-Path -LiteralPath $artifactRuntime) {
    $artifactRuntimeFull = [System.IO.Path]::GetFullPath($artifactRuntime)
    $artifactsPrefix = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $artifactRuntimeFull.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace runtime staging outside '$artifactsRoot': $artifactRuntimeFull"
    }
    Remove-Item -LiteralPath $artifactRuntimeFull -Recurse -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($artifactPackage, $artifactRuntime)

# The staged module is what gets deployed; verify the declared artifacts and their dependency
# manifests survived packing instead of trusting the isolated build directories.
foreach ($declaredArtifact in @($declaredSurfaceArtifacts) + @($declaredRuntimeArtifact)) {
    $stagedArtifact = Join-Path $artifactRuntime ($declaredArtifact -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $stagedArtifact -PathType Leaf)) {
        throw "Packaged module is missing the declared artifact '$declaredArtifact'."
    }
    $stagedDirectory = Split-Path -Parent $stagedArtifact
    $stagedStem = [System.IO.Path]::GetFileNameWithoutExtension($stagedArtifact)
    if (-not (Test-Path -LiteralPath (Join-Path $stagedDirectory ($stagedStem + '.deps.json')) -PathType Leaf)) {
        throw "Packaged module is missing '$stagedStem.deps.json' for '$declaredArtifact'."
    }
    if ([System.IO.Path]::GetExtension($declaredArtifact) -ieq '.exe' -and
        -not (Test-Path -LiteralPath (Join-Path $stagedDirectory ($stagedStem + '.runtimeconfig.json')) -PathType Leaf)) {
        throw "Packaged module is missing '$stagedStem.runtimeconfig.json' for '$declaredArtifact'."
    }
}

$toolManifestPath = Join-Path $artifactRuntime 'tool.json'
$toolManifest = Get-Content -LiteralPath $toolManifestPath -Raw | ConvertFrom-Json
$moduleId = if ([string]::IsNullOrWhiteSpace([string]$toolManifest.ownerModuleId)) {
    [string]$toolManifest.toolId
} else {
    [string]$toolManifest.ownerModuleId
}
$moduleManifest = [ordered]@{
    schemaVersion = '1.0'
    id = $moduleId
    packageId = $moduleId
    displayName = [string]$toolManifest.title
    version = [string]$toolManifest.version
    moduleSdk = '1.0'
    entrypoints = @(
        [ordered]@{
            kind = 'jsonrpc-stdio'
            priority = 100
            platforms = @('windows-x64')
            command = [string]$toolManifest.runtime.command
            args = @($toolManifest.runtime.args)
            compat = $true
        }
    )
    capabilities = @('status', 'commands', 'settings', 'logs', 'events', 'detailPage', 'dashboardCard')
    permissions = @($toolManifest.permissions)
    tools = @('tool.json')
    uiSurfaces = @(
        'ui/dashboard-card.json',
        'ui/detail-page.json',
        'ui/settings.json',
        'ui/logs.json'
    )
}
$moduleManifest | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $artifactRuntime 'module.json') -Encoding UTF8
$uiSource = Join-Path $PSScriptRoot 'sdk-tool\ui'
if (Test-Path -LiteralPath $uiSource -PathType Container) {
    Copy-Item -Path (Join-Path $uiSource '*') -Destination (Join-Path $artifactRuntime 'ui') -Recurse -Force
}

Write-Output "Standalone CLI staged at $artifactCli"
Write-Output "SDK tool built at $($declaredSurfacePaths -join ', ')"
Write-Output "Isolated runtime built at $declaredRuntimePath"
Write-Output "SDK package written to $artifactPackage"
Write-Output "Runtime package staged at $artifactRuntime"
