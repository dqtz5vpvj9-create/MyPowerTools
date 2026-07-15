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

New-Item -ItemType Directory -Force -Path $nuget, $npm, $protocol | Out-Null

# Local SDK packages intentionally reuse the development version. Remove only MyPowerTools
# entries from the repository-scoped package cache so the next tool build consumes the newly
# produced package contents instead of an older immutable NuGet cache entry.
if (Test-Path -LiteralPath $localPackageCache -PathType Container) {
    Get-ChildItem -LiteralPath $localPackageCache -Directory -Filter 'mypowertools.*' |
        Remove-Item -Recurse -Force
}

$projects = @(
    'src\MyPowerTools.Platform.Abstractions\MyPowerTools.Platform.Abstractions.csproj',
    'src\MyPowerTools.Abstractions\MyPowerTools.Abstractions.csproj',
    'src\MyPowerTools.Protocol\MyPowerTools.Protocol.csproj',
    'src\MyPowerTools.Ipc.Shared\MyPowerTools.Ipc.Shared.csproj',
    'src\MyPowerTools.Packaging\MyPowerTools.Packaging.csproj',
    'src\MyPowerTools.Platform.Windows\MyPowerTools.Platform.Windows.csproj',
    'src\MyPowerTools.Broker\MyPowerTools.Broker.csproj',
    'src\MyPowerTools.UI.Primitives\MyPowerTools.UI.Primitives.csproj',
    'src\MyPowerTools.UI\MyPowerTools.UI.csproj',
    'src\MyPowerTools.HostControl.Contracts\MyPowerTools.HostControl.Contracts.csproj',
    'src\MyPowerTools.HostControl.Client\MyPowerTools.HostControl.Client.csproj',
    'src\MyPowerTools.ServiceManager.Client\MyPowerTools.ServiceManager.Client.csproj',
    'src\MyPowerTools.AvaloniaSdk\MyPowerTools.AvaloniaSdk.csproj'
)
foreach ($project in $projects) {
    $arguments = @('pack', (Join-Path $repo $project), '--configuration', $Configuration, '--output', $nuget)
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed: $project" }
}

$cliProject = Join-Path $repo 'src\MyPowerTools.Cli\MyPowerTools.Cli.csproj'
& dotnet build $cliProject --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed.' }
if (Test-Path -LiteralPath $cli) { Remove-Item -LiteralPath $cli -Recurse -Force }
New-Item -ItemType Directory -Force -Path $cli | Out-Null
Copy-Item -Path (Join-Path $repo "src\MyPowerTools.Cli\bin\$Configuration\net10.0\*") -Destination $cli -Recurse

$visualProject = Join-Path $repo 'src\Mpt.Cli.VisualTesting\Mpt.Cli.VisualTesting.csproj'
& dotnet build $visualProject --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw 'VisualTesting CLI build failed.' }
$visual = Join-Path $cli 'visual'
New-Item -ItemType Directory -Force -Path $visual | Out-Null
Copy-Item -Path (Join-Path $repo "src\Mpt.Cli.VisualTesting\bin\$Configuration\net10.0\*") -Destination $visual -Recurse

$webBridge = Join-Path $repo 'sdk\web-bridge'
Push-Location $webBridge
try {
    if (Test-Path -LiteralPath (Join-Path $webBridge 'package-lock.json')) {
        & npm.cmd ci
    } else {
        & npm.cmd install
    }
    if ($LASTEXITCODE -ne 0) { throw 'npm dependency restore failed.' }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw 'WebBridge TypeScript build failed.' }
    & npm.cmd pack --pack-destination $npm
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
