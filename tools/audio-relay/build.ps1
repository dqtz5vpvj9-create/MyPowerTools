[CmdletBinding()]
param(
    [string] $MyPowerToolsRepoRoot,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repoRoot = if ([string]::IsNullOrWhiteSpace($MyPowerToolsRepoRoot)) {
    [IO.Path]::GetFullPath((Join-Path $toolRoot '..\..'))
} else {
    [IO.Path]::GetFullPath($MyPowerToolsRepoRoot)
}
$sdkToolRoot = Join-Path $toolRoot 'sdk-tool'
$toolProject = Join-Path $sdkToolRoot 'src\AudioRelay.Tool\AudioRelay.Tool.csproj'
$runtimeProject = Join-Path $sdkToolRoot 'src\AudioRelay.Runtime\AudioRelay.Runtime.csproj'
$cliProject = Join-Path $repoRoot 'src\MyPowerTools.Cli\MyPowerTools.Cli.csproj'
$cli = Join-Path $repoRoot "artifacts\build\bin\MyPowerTools.Cli\$($Configuration.ToLowerInvariant())\MyPowerTools.Cli.exe"
$artifactsRoot = Join-Path $toolRoot 'artifacts'
$package = Join-Path $artifactsRoot 'audio-relay.mptpkg'
$stage = Join-Path $artifactsRoot 'package'
$dotnet = (Get-Command 'dotnet' -CommandType Application -ErrorAction Stop).Source

foreach ($project in @($toolProject, $runtimeProject, $cliProject)) {
    & $dotnet @('build', $project, '--configuration', $Configuration, '--nologo')
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "Build failed for '$project' with exit code $exitCode." }
}
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw "Expected MyPowerTools CLI '$cli' is missing." }

& $cli @('validate', 'tool', $sdkToolRoot)
if ($LASTEXITCODE -ne 0) { throw 'Tool SDK validation failed.' }
& $cli @('pack', 'tool', $sdkToolRoot, '--output', $package)
if ($LASTEXITCODE -ne 0) { throw 'Tool SDK packaging failed.' }

if (Test-Path -LiteralPath $stage) {
    $stageFull = [IO.Path]::GetFullPath($stage)
    $artifactsPrefix = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $stageFull.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace runtime staging outside '$artifactsRoot': $stageFull"
    }
    Remove-Item -LiteralPath $stageFull -Recurse -Force
}
[IO.Compression.ZipFile]::ExtractToDirectory($package, $stage)

# tool.json keeps Release-shaped paths. A Debug developer overlay copies the configuration built
# by this invocation into those stable locations before the staged package is installed.
$outputs = @(
    [ordered]@{
        Source = Join-Path $sdkToolRoot "src\AudioRelay.Tool\bin\$Configuration\net10.0"
        Destination = Join-Path $stage 'src\AudioRelay.Tool\bin\Release\net10.0'
    },
    [ordered]@{
        Source = Join-Path $sdkToolRoot "src\AudioRelay.Runtime\bin\$Configuration\net10.0"
        Destination = Join-Path $stage 'src\AudioRelay.Runtime\bin\Release\net10.0'
    }
)
foreach ($output in $outputs) {
    New-Item -ItemType Directory -Path $output.Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $output.Source -File | Copy-Item -Destination $output.Destination -Force
}

$manifest = Get-Content -LiteralPath (Join-Path $stage 'tool.json') -Raw | ConvertFrom-Json
$module = [ordered]@{
    schemaVersion = '1.0'
    id = 'audio-relay'
    packageId = 'audio-relay'
    displayName = [string]$manifest.title
    version = [string]$manifest.version
    moduleSdk = '1.0'
    entrypoints = @([ordered]@{
        kind = 'jsonrpc-stdio'
        priority = 100
        platforms = @('windows-x64')
        command = [string]$manifest.runtime.command
        args = @()
        compat = $true
    })
    capabilities = @('status', 'commands', 'logs', 'detailPage', 'dashboardCard')
    runtimePolicy = [ordered]@{
        preferred = 'compat'
        allowInProc = $false
        operationRules = [ordered]@{
            status = 'inproc-or-sidecar'
            commandProvider = 'inproc-or-sidecar'
            externalProcess = 'sidecar-required'
            elevatedWrite = 'broker-required'
        }
    }
    permissions = @($manifest.permissions)
    tools = @('tool.json')
    uiSurfaces = @('ui/dashboard-card.json', 'ui/detail-page.json', 'ui/settings.json', 'ui/logs.json')
}
$module | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $stage 'module.json') -Encoding UTF8
Copy-Item -Path (Join-Path $sdkToolRoot 'ui\*') -Destination (Join-Path $stage 'ui') -Recurse -Force

Write-Output "AudioRelay SDK package written to $package"
Write-Output "AudioRelay runtime package staged at $stage"
