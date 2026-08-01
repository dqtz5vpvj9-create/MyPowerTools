[CmdletBinding()]
param(
    [string] $MyPowerToolsRepoRoot,
    [ValidateSet('Release')]
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
$mptCliExecutable = Join-Path $repoRoot "src\MyPowerTools.Cli\bin\$Configuration\net10.0\MyPowerTools.Cli.exe"
$artifactsRoot = Join-Path $toolRoot 'artifacts'
$artifactCli = Join-Path $artifactsRoot 'cli'
$artifactPackage = Join-Path $artifactsRoot 'local-lag-cleaner.mptpkg'

if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'MyPowerTools.slnx') -PathType Leaf) -or
    -not (Test-Path -LiteralPath $mptCliProject -PathType Leaf)) {
    throw "MyPowerToolsRepoRoot '$repoRoot' is invalid."
}

$dotnetCommand = Get-Command 'dotnet' -CommandType Application -ErrorAction Stop

$sdkToolArguments = @(
    'build'
    $sdkToolProject
    '--configuration'
    $Configuration
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
$expectedSdkTool = Join-Path $sdkToolRoot "src\LocalLagCleaner.Tool\bin\$Configuration\net10.0\LocalLagCleaner.Tool.dll"
$expectedRuntime = Join-Path $sdkToolRoot "src\LocalLagCleaner.Runtime\bin\$Configuration\net10.0\LocalLagCleaner.Runtime.exe"
foreach ($expectedPath in @($expectedStandalone, $expectedSdkTool, $expectedRuntime, $artifactPackage)) {
    if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf)) {
        throw "Expected build output '$expectedPath' is missing."
    }
}

Write-Output "Standalone CLI staged at $artifactCli"
Write-Output "SDK tool built at $expectedSdkTool"
Write-Output "Isolated runtime built at $expectedRuntime"
Write-Output "SDK package written to $artifactPackage"
