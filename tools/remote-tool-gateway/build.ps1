[CmdletBinding()]
param(
    [string]$MyPowerToolsRepoRoot,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    # The repository module mirror (modules/remote-tool-gateway) is written only when this switch is
    # passed. Repository registration belongs to the integration agent, so the default build never
    # touches modules/.
    [switch]$StageRepositoryModule
)
$ErrorActionPreference = 'Stop'
$repo = if ($MyPowerToolsRepoRoot) { [IO.Path]::GetFullPath($MyPowerToolsRepoRoot) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')) }
$template = Join-Path $PSScriptRoot 'package'
$package = Join-Path $PSScriptRoot 'artifacts/package'
# Build into a staging directory and swap it in only after every project compiled, so a failed
# build can never leave a half-populated module package behind.
$staging = Join-Path $PSScriptRoot 'artifacts/package.staging'
$surface = Join-Path $staging 'ui/surface'

$dotnetCommand = Get-Command 'dotnet' -CommandType Application -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $HOME '.dotnet/dotnet' }
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'The dotnet host was not found. Add it to PATH, or set DOTNET_ROOT.'
}

if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $surface -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $template) {
    Copy-Item -LiteralPath $item.FullName -Destination $staging -Recurse -Force
}

foreach ($project in @('RemoteToolGateway.MyPowerTools', 'RemoteToolGateway.Surface')) {
    $destination = if ($project.EndsWith('.Surface')) { $surface } else { $staging }
    # The module must carry its own HostControl/protobuf/gRPC dependencies because the Runner loads
    # it into a collectible load context. The surface resolves Avalonia and the shared SDK contracts
    # from the Shell, exactly like the existing dotnet surfaces, so it stays a small package.
    $copyLocal = if ($project.EndsWith('.Surface')) { 'false' } else { 'true' }
    & $dotnet build (Join-Path $PSScriptRoot "src/$project/$project.csproj") -c $Configuration --nologo -o $destination `
        "-p:MyPowerToolsRepoRoot=$repo" '-p:StageRepositoryModule=false' "-p:CopyLocalLockFileAssemblies=$copyLocal"
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

# The surface is compiled against the in-repo AvaloniaSdk project (it needs
# ExecuteCommandWithInvocationAsync), which makes the build copy the shared SDK contracts next to the
# surface. Every dotnet-surface host already loads MyPowerTools.AvaloniaSdk and
# MyPowerTools.Abstractions from its own graph (they are in the loader's shared-assembly list), so the
# copies would only add weight to the tool package and the Android bundle. Drop them and keep the
# surface folder to the surface assembly, exactly like the tools that consume the SDK as a package.
foreach ($shared in @('MyPowerTools.AvaloniaSdk', 'MyPowerTools.Abstractions')) {
    foreach ($pattern in @("$shared.dll", "$shared.pdb", "$shared.deps.json")) {
        $stale = Join-Path $surface $pattern
        if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force }
    }
}

# The Shell resolves the Surface through ui/tool.json and the module host resolves the entry point
# from module.json; fail here instead of shipping a catalog that points at a missing assembly.
$required = @(
    (Join-Path $staging 'module.json'),
    (Join-Path $staging 'RemoteToolGateway.MyPowerTools.dll'),
    (Join-Path $staging 'RemoteToolGateway.Core.dll'),
    (Join-Path $staging 'MyPowerTools.HostControl.Client.dll'),
    (Join-Path $surface 'RemoteToolGateway.Surface.dll'),
    (Join-Path $staging 'ui/tool.json')
)
foreach ($item in $required) {
    if (-not (Test-Path -LiteralPath $item -PathType Leaf)) {
        throw "Remote Tool Gateway stage is incomplete: $item is missing."
    }
}

if (Test-Path -LiteralPath $package) {
    Remove-Item -LiteralPath $package -Recurse -Force
}
Move-Item -LiteralPath $staging -Destination $package

if ($StageRepositoryModule) {
    $module = Join-Path $repo 'modules/remote-tool-gateway'
    if (Test-Path -LiteralPath $module) {
        Remove-Item -LiteralPath $module -Recurse -Force
    }
    New-Item -ItemType Directory -Path $module -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $package) {
        Copy-Item -LiteralPath $item.FullName -Destination $module -Recurse -Force
    }
    Write-Output "Remote Tool Gateway staged at $package and mirrored to $module"
}
else {
    Write-Output "Remote Tool Gateway staged at $package (repository module mirror untouched; pass -StageRepositoryModule to update it)"
}
