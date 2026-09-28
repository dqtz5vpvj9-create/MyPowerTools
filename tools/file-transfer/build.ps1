[CmdletBinding()]
param(
    [string]$MyPowerToolsRepoRoot,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoMirror
)
$ErrorActionPreference = 'Stop'
$repo = if ($MyPowerToolsRepoRoot) { [IO.Path]::GetFullPath($MyPowerToolsRepoRoot) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')) }
$template = Join-Path $PSScriptRoot 'package'
$package = Join-Path $PSScriptRoot 'artifacts/package'
# Build into a staging directory and swap it in only after every project compiled, so a
# failed build can never leave a half-populated module package behind for the Android
# app or the release scripts to embed.
$staging = Join-Path $PSScriptRoot 'artifacts/package.staging'
$surface = Join-Path $staging 'ui/surface'
$module = Join-Path $repo 'modules/file-transfer'

$dotnetCommand = Get-Command 'dotnet' -CommandType Application -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $HOME '.dotnet/dotnet' }
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'The dotnet host was not found. Add it to PATH, or set DOTNET_ROOT.'
}

# Only the disposable staging directory is cleared up front. The published package
# and modules/file-transfer are replaced after a successful build, so a failed build
# never destroys the module a running dev install or a parallel build still uses.
if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $surface -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $template) {
    Copy-Item -LiteralPath $item.FullName -Destination $staging -Recurse -Force
}

foreach ($project in @('FileTransfer.MyPowerTools', 'FileTransfer.Surface')) {
    $destination = if ($project.EndsWith('.Surface')) { $surface } else { $staging }
    & $dotnet build (Join-Path $PSScriptRoot "src/$project/$project.csproj") -c $Configuration --nologo -o $destination "-p:MyPowerToolsRepoRoot=$repo" '-p:StageRepositoryModule=false'
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

# The Shell resolves the Surface through ui/tool.json; fail here instead of shipping a
# module catalog that points at a missing factory assembly.
$adapterAssembly = Join-Path $staging 'FileTransfer.MyPowerTools.dll'
$surfaceAssembly = Join-Path $surface 'FileTransfer.Surface.dll'
foreach ($required in @((Join-Path $staging 'module.json'), $adapterAssembly, $surfaceAssembly)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "File Transfer module stage is incomplete: $required is missing."
    }
}

if (Test-Path -LiteralPath $package) {
    Remove-Item -LiteralPath $package -Recurse -Force
}
Move-Item -LiteralPath $staging -Destination $package

if (-not $NoMirror) {
    if (Test-Path -LiteralPath $module) {
        Remove-Item -LiteralPath $module -Recurse -Force
    }
    New-Item -ItemType Directory -Path $module -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $package) {
        Copy-Item -LiteralPath $item.FullName -Destination $module -Recurse -Force
    }
}
Write-Output "File Transfer staged at $package"
