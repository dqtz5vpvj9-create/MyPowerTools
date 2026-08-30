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
$projects = @(
    Join-Path $sdkToolRoot 'src\NssmManager.Tool\NssmManager.Tool.csproj'
    Join-Path $sdkToolRoot 'src\NssmManager.Runtime\NssmManager.Runtime.csproj'
)
$executableProject = Join-Path $sdkToolRoot 'src\NssmManager.Executable\NssmManager.Executable.csproj'
$publishDirectory = Join-Path $sdkToolRoot 'src\NssmManager.Executable\publish\win-x64'
$mptCliProject = Join-Path $repoRoot 'src\MyPowerTools.Cli\MyPowerTools.Cli.csproj'
$mptCliExecutable = Join-Path $repoRoot "artifacts\build\bin\MyPowerTools.Cli\$($Configuration.ToLowerInvariant())\MyPowerTools.Cli.exe"
$artifactsRoot = Join-Path $toolRoot 'artifacts'
$artifactPackage = Join-Path $artifactsRoot 'nssm-manager.mptpkg'
$artifactRuntime = Join-Path $artifactsRoot 'package'
$dotnet = (Get-Command 'dotnet' -CommandType Application -ErrorAction Stop).Source

function Find-WindowsSdkTool {
    param([Parameter(Mandatory)][string] $Name)
    $kitsBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $kitsBin -PathType Container)) { throw 'Windows 10/11 SDK tools are required to build nssm-manager resources.' }
    $versions = Get-ChildItem -LiteralPath $kitsBin -Directory | ForEach-Object {
        $parsed = [version]::new()
        if ([version]::TryParse($_.Name, [ref]$parsed)) { [pscustomobject]@{ Directory = $_.FullName; Version = $parsed } }
    } | Sort-Object Version -Descending
    foreach ($version in $versions) {
        $candidate = Join-Path $version.Directory "x64\$Name"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "Windows SDK tool '$Name' was not found."
}

$nativeMessageRoot = Join-Path $artifactsRoot 'native-messages'
if (Test-Path -LiteralPath $nativeMessageRoot) {
    $nativeMessageFull = [System.IO.Path]::GetFullPath($nativeMessageRoot)
    $expectedNativeMessageFull = [System.IO.Path]::GetFullPath((Join-Path $toolRoot 'artifacts\native-messages'))
    if (-not $nativeMessageFull.Equals($expectedNativeMessageFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe native message staging path '$nativeMessageFull'."
    }
    Remove-Item -LiteralPath $nativeMessageFull -Recurse -Force
}
New-Item -ItemType Directory -Path $nativeMessageRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $toolRoot 'native\nssm-manager.rc') -Destination $nativeMessageRoot -Force
Copy-Item -LiteralPath (Join-Path $toolRoot 'original-source\src\nssm.ico') -Destination $nativeMessageRoot -Force
$messageCompiler = Find-WindowsSdkTool -Name 'mc.exe'
$resourceCompiler = Find-WindowsSdkTool -Name 'rc.exe'
& $messageCompiler -h $nativeMessageRoot -r $nativeMessageRoot (Join-Path $toolRoot 'original-source\src\messages.mc')
if ($LASTEXITCODE -ne 0) { throw "NSSM message compilation failed with exit code $LASTEXITCODE." }
$nativeResource = Join-Path $nativeMessageRoot 'nssm-manager.res'
& $resourceCompiler /nologo /fo $nativeResource (Join-Path $nativeMessageRoot 'nssm-manager.rc')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $nativeResource -PathType Leaf)) { throw "NSSM resource compilation failed with exit code $LASTEXITCODE." }

foreach ($project in $projects) {
    $buildArguments = @('build', $project, '--configuration', $Configuration, '--nologo')
    & $dotnet @buildArguments
    $buildExit = $LASTEXITCODE
    if ($buildExit -ne 0) { throw "Build failed for '$project' with exit code $buildExit." }
}

if (Test-Path -LiteralPath $publishDirectory) {
    $publishFull = [System.IO.Path]::GetFullPath($publishDirectory)
    $toolPrefix = $toolRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $publishFull.StartsWith($toolPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $publishFull.EndsWith('NssmManager.Executable\publish\win-x64', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe nssm-manager publish path '$publishFull'."
    }
    Remove-Item -LiteralPath $publishFull -Recurse -Force
}

$publishArguments = @(
    'publish'
    $executableProject
    '--configuration'
    $Configuration
    '--runtime'
    'win-x64'
    '--self-contained'
    'true'
    '-p:PublishAot=true'
    '-p:PublishReadyToRun=false'
    '-p:PublishSingleFile=false'
    '-p:PublishTrimmed=true'
    '-p:InvariantGlobalization=true'
    '-p:StripSymbols=true'
    '-p:DebugType=None'
    '-p:DebugSymbols=false'
    "-p:Win32Resource=$nativeResource"
    '--output'
    $publishDirectory
    '--nologo'
)
& $dotnet @publishArguments
$publishExit = $LASTEXITCODE
if ($publishExit -ne 0) { throw "nssm-manager publish failed with exit code $publishExit." }
$publishedExecutable = Join-Path $publishDirectory 'nssm-manager.exe'
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "nssm-manager single-file publish did not produce '$publishedExecutable'."
}
Get-ChildItem -LiteralPath $publishDirectory -File -Filter '*.pdb' | Remove-Item -Force
$publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -cne 'nssm-manager.exe') {
    throw "nssm-manager publish must contain one executable. Found: $($publishedFiles.Name -join ', ')."
}
& pwsh.exe -NoLogo -NoProfile -NonInteractive -File (Join-Path $toolRoot 'tests\verify-native-resources.ps1') -Executable $publishedExecutable
if ($LASTEXITCODE -ne 0) { throw "nssm-manager native resource verification failed with exit code $LASTEXITCODE." }

if ($Configuration -ne 'Release') {
    foreach ($projectName in @('NssmManager.Tool', 'NssmManager.Runtime')) {
        $sourceOutput = Join-Path $sdkToolRoot "src\$projectName\bin\$Configuration\net10.0"
        $stableOutput = Join-Path $sdkToolRoot "src\$projectName\bin\Release\net10.0"
        New-Item -ItemType Directory -Path $stableOutput -Force | Out-Null
        Get-ChildItem -LiteralPath $sourceOutput -File | Copy-Item -Destination $stableOutput -Force
    }
}

$cliBuildArguments = @('build', $mptCliProject, '--configuration', $Configuration, '--nologo')
& $dotnet @cliBuildArguments
$cliBuildExit = $LASTEXITCODE
if ($cliBuildExit -ne 0) { throw "MyPowerTools CLI build failed with exit code $cliBuildExit." }

$validateArguments = @('validate', 'tool', $sdkToolRoot)
& $mptCliExecutable @validateArguments
$validateExit = $LASTEXITCODE
if ($validateExit -ne 0) { throw "Tool SDK validation failed with exit code $validateExit." }

$packArguments = @('pack', 'tool', $sdkToolRoot, '--output', $artifactPackage)
& $mptCliExecutable @packArguments
$packExit = $LASTEXITCODE
if ($packExit -ne 0) { throw "Tool SDK packaging failed with exit code $packExit." }

if (Test-Path -LiteralPath $artifactRuntime) {
    $runtimeFull = [System.IO.Path]::GetFullPath($artifactRuntime)
    $artifactsPrefix = [System.IO.Path]::GetFullPath($artifactsRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $runtimeFull.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe staging path '$runtimeFull'." }
    Remove-Item -LiteralPath $runtimeFull -Recurse -Force
}
[System.IO.Compression.ZipFile]::ExtractToDirectory($artifactPackage, $artifactRuntime)

$manifest = Get-Content -LiteralPath (Join-Path $artifactRuntime 'tool.json') -Raw | ConvertFrom-Json
$module = [ordered]@{
    schemaVersion = '1.0'
    id = 'nssm-manager'
    packageId = 'nssm-manager'
    displayName = [string]$manifest.title
    version = [string]$manifest.version
    moduleSdk = '1.0'
    entrypoints = @([ordered]@{ kind = 'jsonrpc-stdio'; priority = 100; platforms = @('windows-x64'); command = [string]$manifest.runtime.command; args = @(); compat = $true })
    capabilities = @('status', 'commands', 'settings', 'logs', 'events', 'detailPage', 'dashboardCard')
    runtimePolicy = [ordered]@{ preferred = 'compat'; allowInProc = $false; operationRules = [ordered]@{ status = 'inproc-or-sidecar'; settings = 'inproc-or-sidecar'; commandProvider = 'inproc-or-sidecar'; systemMutation = 'sidecar-required'; elevatedWrite = 'broker-required' } }
    permissions = @($manifest.permissions)
    tools = @('tool.json')
    uiSurfaces = @('ui/dashboard-card.json', 'ui/detail-page.json', 'ui/settings.json', 'ui/logs.json')
}
$module | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $artifactRuntime 'module.json') -Encoding UTF8
Copy-Item -Path (Join-Path $sdkToolRoot 'ui\*') -Destination (Join-Path $artifactRuntime 'ui') -Recurse -Force

Write-Output "nssm-manager package: $artifactPackage"
Write-Output "nssm-manager executable: $(Join-Path $publishDirectory 'nssm-manager.exe')"
