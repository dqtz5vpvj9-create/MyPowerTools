[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo 'artifacts\sdk'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$nuget = Join-Path $OutputRoot 'nuget'
$npm = Join-Path $OutputRoot 'npm'
$protocol = Join-Path $OutputRoot 'protocol'
$cli = Join-Path $OutputRoot 'cli'
$localPackageCache = Join-Path $OutputRoot 'global-packages'
$npmExecutable = if ($IsWindows) { 'npm.cmd' } else { 'npm' }

New-Item -ItemType Directory -Force -Path $nuget, $npm, $protocol | Out-Null

# Local SDK packages intentionally reuse the development version. Remove only MyPowerTools
# entries from the repository-scoped package cache so the next tool build consumes the newly
# produced package contents instead of an older immutable NuGet cache entry.
if (Test-Path -LiteralPath $localPackageCache -PathType Container) {
    Get-ChildItem -LiteralPath $localPackageCache -Directory -Filter 'mypowertools.*' |
        Remove-Item -Recurse -Force
}

# Use the same strict SDK package graph as focused tests so shared dependencies
# are evaluated by one build manager instead of a separate process per package.
$outputList = [IO.Path]::GetTempFileName()
try {
    & dotnet msbuild (Join-Path $PSScriptRoot 'focused-test-sdk.proj') -restore -target:Pack -maxCpuCount `
        "-property:Configuration=$Configuration" "-property:PackageOutputPath=$nuget" `
        "-property:FocusedSdkOutputList=$outputList"
    if ($LASTEXITCODE -ne 0) { throw 'SDK package build failed.' }
}
finally {
    Remove-Item -LiteralPath $outputList -Force -ErrorAction SilentlyContinue
}

# src/Directory.Build.props routes src/ projects through the artifacts output layout,
# which names build directories <lowercase configuration> under one shared bin root.
$buildBinRoot = Join-Path $repo 'artifacts\build\bin'
$configurationDirName = $Configuration.ToLowerInvariant()

function Get-ProjectBuildOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectName
    )

    $path = Join-Path $buildBinRoot (Join-Path $ProjectName $configurationDirName)
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Build output for $ProjectName was not found at $path"
    }

    return $path
}

$cliProject = Join-Path $repo 'src\MyPowerTools.Cli\MyPowerTools.Cli.csproj'
& dotnet build $cliProject --configuration $Configuration --maxcpucount
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed.' }
if (Test-Path -LiteralPath $cli) { Remove-Item -LiteralPath $cli -Recurse -Force }
New-Item -ItemType Directory -Force -Path $cli | Out-Null
Copy-Item -Path (Join-Path (Get-ProjectBuildOutput -ProjectName 'MyPowerTools.Cli') '*') -Destination $cli -Recurse

$visualProject = Join-Path $repo 'src\Mpt.Cli.VisualTesting\Mpt.Cli.VisualTesting.csproj'
& dotnet build $visualProject --configuration $Configuration --maxcpucount
if ($LASTEXITCODE -ne 0) { throw 'VisualTesting CLI build failed.' }
$visual = Join-Path $cli 'visual'
New-Item -ItemType Directory -Force -Path $visual | Out-Null
Copy-Item -Path (Join-Path (Get-ProjectBuildOutput -ProjectName 'Mpt.Cli.VisualTesting') '*') -Destination $visual -Recurse

$webBridge = Join-Path $repo 'sdk\web-bridge'
Push-Location $webBridge
try {
    if (Test-Path -LiteralPath (Join-Path $webBridge 'package-lock.json')) {
        & $npmExecutable ci
    } else {
        & $npmExecutable install
    }
    if ($LASTEXITCODE -ne 0) { throw 'npm dependency restore failed.' }
    & $npmExecutable run build
    if ($LASTEXITCODE -ne 0) { throw 'WebBridge TypeScript build failed.' }
    & $npmExecutable pack --pack-destination $npm
    if ($LASTEXITCODE -ne 0) { throw 'WebBridge npm pack failed.' }
}
finally {
    Pop-Location
}

$stage = Join-Path $protocol 'bundle'
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'proto'), (Join-Path $stage 'schema'), (Join-Path $stage 'test-vectors') | Out-Null
Copy-Item -Path (Join-Path $repo 'proto\*.proto') -Destination (Join-Path $stage 'proto')
Copy-Item -Path (Join-Path $repo 'schemas\*.json') -Destination (Join-Path $stage 'schema')
Copy-Item -LiteralPath (Join-Path $repo 'sdk\protocol\README.md') -Destination $stage
Copy-Item -Path (Join-Path $repo 'sdk\protocol\test-vectors\*.json') -Destination (Join-Path $stage 'test-vectors')

$bundlePath = Join-Path $protocol 'mypowertools-protocol-0.2.0.zip'
if (Test-Path -LiteralPath $bundlePath) { Remove-Item -LiteralPath $bundlePath -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $bundlePath -CompressionLevel Optimal

Write-Host "NuGet: $nuget"
Write-Host "npm: $npm"
Write-Host "Protocol: $bundlePath"
Write-Host "CLI: $cli"
