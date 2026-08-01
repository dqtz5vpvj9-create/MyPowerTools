<#
.SYNOPSIS
  Publishes the Windows product layout (portable package, optional Inno Setup installer,
  source bundle) under artifacts/release/.

.DESCRIPTION
  Orchestrates the full Windows release publish:

    1. Builds the local SDK bundles (build-sdk.ps1) and every first-party tool package
       (build-tool-packages.ps1) under artifacts/release/module-packages.
    2. Publishes Runner, Shell (ReadyToRun composite), Cli, ElevatedBroker (Native AOT),
       ServiceManager and the App launcher (Native AOT) as self-contained win-x64.
    3. Stages Service Units, modules, schemas, ui, assets, templates and the user-facing
       maintenance scripts into artifacts/release/win-x64, validates the module packages
       and writes build-provenance.json.
    4. Produces MyPowerTools-win-x64.zip (+ SHA256), release metadata/notes and, unless
       -PortableOnly is set, the Inno Setup installer and a source bundle.

.NOTES
  The ElevatedBroker and App publishes use Native AOT and need the MSVC linker from the
  Visual Studio "Desktop development with C++" workload
  (https://aka.ms/nativeaot-prerequisites). This script auto-configures that toolchain
  when it is missing: it locates Visual Studio through vswhere.exe, imports the
  vcvarsall environment and sets IlcUseEnvironmentalTools=true so the ILCompiler link
  step uses the environment tools directly (its findvcvarsall.bat fallback corrupts the
  linker command line when vswhere.exe is not on PATH). Running from a Visual Studio
  developer shell also works. If the C++ workload cannot be found the publish fails fast
  with a prerequisite error instead of a broken linker command.

.PARAMETER PortableOnly
  Skip the Inno Setup installer and source bundle; produce only the portable layout + ZIP.

.EXAMPLE
  pwsh scripts/publish-windows.ps1
.EXAMPLE
  pwsh scripts/publish-windows.ps1 -PortableOnly
#>
[CmdletBinding()]
param(
    [switch]$PortableOnly
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $RepoRoot 'artifacts\release'
$PublishRoot = Join-Path $Artifacts 'win-x64'
$ZipPath = Join-Path $Artifacts 'MyPowerTools-win-x64.zip'
$InstallerScript = Join-Path $RepoRoot 'installer\MyPowerTools.iss'
$ModuleStagingRoot = Join-Path $Artifacts 'module-packages'

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$FilePath failed with exit code $exitCode."
    }
}

function Write-Sha256File {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot write SHA256 for missing file: $Path"
    }

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    "$hash  $([System.IO.Path]::GetFileName($Path))" |
        Set-Content -LiteralPath "$Path.sha256" -Encoding ASCII
}

function Find-InnoCompiler {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return $null
}

function Copy-DirectoryWithoutBuildArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $sourceFull = [System.IO.Path]::GetFullPath($Source)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    Get-ChildItem -LiteralPath $sourceFull -Recurse -Force | ForEach-Object {
        $relative = $_.FullName.Substring($sourceFull.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        $segments = $relative -split '[\\/]'
        if (-not ($segments -contains 'bin' -or $segments -contains 'obj')) {
            if ($_.PSIsContainer) {
                $target = Join-Path $Destination $relative
                New-Item -ItemType Directory -Path $target -Force | Out-Null
            } else {
                $target = Join-Path $Destination $relative
                $targetParent = Split-Path -Parent $target
                New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $target -Force
            }
        }
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Source directory is missing: $Source"
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function Assert-DirectoryMatches {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $sourceFull = [System.IO.Path]::GetFullPath($Source)
    $destinationFull = [System.IO.Path]::GetFullPath($Destination)
    $sourceFiles = @(Get-ChildItem -LiteralPath $sourceFull -Recurse -File -Force)
    $destinationFiles = @(Get-ChildItem -LiteralPath $destinationFull -Recurse -File -Force)
    if ($sourceFiles.Count -ne $destinationFiles.Count) {
        throw "Published directory file count differs. Source=$sourceFull ($($sourceFiles.Count)) Destination=$destinationFull ($($destinationFiles.Count))"
    }

    foreach ($sourceFile in $sourceFiles) {
        $relative = $sourceFile.FullName.Substring($sourceFull.Length).TrimStart(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        $destinationFile = Join-Path $destinationFull $relative
        if (-not (Test-Path -LiteralPath $destinationFile -PathType Leaf)) {
            throw "Published file is missing: $destinationFile"
        }

        $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destinationFile -Algorithm SHA256).Hash
        if (-not $sourceHash.Equals($destinationHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Published file hash differs: $relative"
        }
    }
}

function Ensure-NativeBuildTools {
    # The ElevatedBroker and App publishes use Native AOT, which links with MSVC's
    # link.exe. The ILCompiler targets normally resolve the toolchain through
    # findvcvarsall.bat, but that batch embeds vswhere.exe's "command not found" error
    # text into the linker command line when vswhere.exe is not on PATH (MSB3073,
    # exit code 123). Prefer the environment tools instead: make sure link.exe resolves
    # to the MSVC toolchain, keep vswhere.exe reachable for bare callers, and set
    # IlcUseEnvironmentalTools so ILCompiler skips findvcvarsall.bat entirely.
    #
    # Note: do not trust a bare `Get-Command link.exe` — unrelated tools (for example
    # GNU coreutils) also ship a link.exe. The MSVC linker must live under
    # VCToolsInstallDir.

    $installerDirectory = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer'
    if ((";$env:PATH;").IndexOf(";$installerDirectory;", [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
        (Test-Path -LiteralPath (Join-Path $installerDirectory 'vswhere.exe') -PathType Leaf)) {
        $env:PATH = "$installerDirectory;$env:PATH"
    }

    if (Test-MsvcLinkerOnPath) {
        $env:IlcUseEnvironmentalTools = 'true'
        return
    }

    $vswhere = Join-Path $installerDirectory 'vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'Native AOT publishing requires the Visual Studio "Desktop development with C++" workload (https://aka.ms/nativeaot-prerequisites). Install it, or run this script from a Visual Studio developer shell.'
    }

    $vsInstallPath = (& $vswhere -latest -prerelease -products * `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -property installationPath | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($vsInstallPath) -or
        -not (Test-Path -LiteralPath $vsInstallPath -PathType Container)) {
        throw 'No Visual Studio installation with the "Desktop development with C++" workload was found (https://aka.ms/nativeaot-prerequisites).'
    }

    $vcvarsall = Join-Path $vsInstallPath 'VC\Auxiliary\Build\vcvarsall.bat'
    if (-not (Test-Path -LiteralPath $vcvarsall -PathType Leaf)) {
        throw "vcvarsall.bat was not found under $vsInstallPath; repair the C++ workload."
    }

    $vcArch = if ("$env:PROCESSOR_ARCHITECTURE" -eq 'ARM64') { 'arm64_amd64' } else { 'amd64' }
    & cmd.exe /c "`"$vcvarsall`" $vcArch >NUL && set" | ForEach-Object {
        if ($_ -match '^([^=]+)=(.*)$') {
            [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
        }
    }

    if (-not (Test-MsvcLinkerOnPath)) {
        throw "vcvarsall.bat ($vcArch) did not put the MSVC link.exe on PATH; check the C++ workload under $vsInstallPath."
    }

    $env:IlcUseEnvironmentalTools = 'true'
    Write-Host "Native build tools: imported VC environment from $vsInstallPath ($vcArch)."
}

function Test-MsvcLinkerOnPath {
    # True only when link.exe resolves into the MSVC toolchain (VCToolsInstallDir),
    # never for an unrelated link.exe (e.g. GNU coreutils) earlier on PATH.
    if ([string]::IsNullOrWhiteSpace($env:VCToolsInstallDir)) {
        return $false
    }

    $linkCommand = Get-Command 'link.exe' -CommandType Application -ErrorAction SilentlyContinue
    if (-not $linkCommand) {
        return $false
    }

    $vcToolsRoot = [System.IO.Path]::GetFullPath($env:VCToolsInstallDir).TrimEnd('\') + '\'
    return $linkCommand.Source.StartsWith($vcToolsRoot, [StringComparison]::OrdinalIgnoreCase)
}

Set-Location -LiteralPath $RepoRoot
Ensure-NativeBuildTools
New-Item -ItemType Directory -Path $Artifacts -Force | Out-Null

$ArtifactsFull = [System.IO.Path]::GetFullPath($Artifacts)
$PublishRootFull = [System.IO.Path]::GetFullPath($PublishRoot)
$ModuleStagingRootFull = [System.IO.Path]::GetFullPath($ModuleStagingRoot)
if ($PublishRootFull.StartsWith($ArtifactsFull, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $PublishRoot)) {
    Get-ChildItem -LiteralPath $PublishRoot -Force | Remove-Item -Recurse -Force
}

New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null
if (-not $ModuleStagingRootFull.StartsWith($ArtifactsFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Module staging path must stay under release artifacts: $ModuleStagingRootFull"
}
Invoke-Native -FilePath 'pwsh.exe' -ArgumentList @(
    '-NoLogo', '-NoProfile', '-NonInteractive',
    '-File', (Join-Path $PSScriptRoot 'build-sdk.ps1'))
Invoke-Native -FilePath 'pwsh.exe' -ArgumentList @(
    '-NoLogo', '-NoProfile', '-NonInteractive',
    '-File', (Join-Path $PSScriptRoot 'build-tool-packages.ps1'),
    '-RepoRoot', $RepoRoot,
    '-OutputRoot', $ModuleStagingRootFull,
    '-SkipSdk')

Invoke-Native -FilePath 'dotnet' -ArgumentList @(
    'run', '--project', 'src\MyPowerTools.Cli\MyPowerTools.Cli.csproj', '--',
    'package', 'sign-local', $ModuleStagingRootFull)
Invoke-Native -FilePath 'dotnet' -ArgumentList @('publish', 'src\MyPowerTools.Runner\MyPowerTools.Runner.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', (Join-Path $PublishRoot 'Runner'))
Invoke-Native -FilePath 'dotnet' -ArgumentList @('publish', 'src\MyPowerTools.Shell.Avalonia\MyPowerTools.Shell.Avalonia.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishReadyToRun=true', '-p:PublishReadyToRunComposite=true', '-o', (Join-Path $PublishRoot 'Shell'))
Invoke-Native -FilePath 'dotnet' -ArgumentList @('publish', 'src\MyPowerTools.Cli\MyPowerTools.Cli.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', (Join-Path $PublishRoot 'Cli'))
Invoke-Native -FilePath 'dotnet' -ArgumentList @('publish', 'src\MyPowerTools.ElevatedBroker\MyPowerTools.ElevatedBroker.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishAot=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', (Join-Path $PublishRoot 'Broker'))
Invoke-Native -FilePath 'pwsh.exe' -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'validate-elevated-broker.ps1'), '-BrokerDirectory', (Join-Path $PublishRoot 'Broker'))
Invoke-Native -FilePath 'dotnet' -ArgumentList @('publish', 'src\MyPowerTools.ServiceManager\MyPowerTools.ServiceManager.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', (Join-Path $PublishRoot 'ServiceManager'))

$toolBuildManifestPath = Join-Path $RepoRoot 'artifacts\tool-package-build\source-manifest.json'
if (-not (Test-Path -LiteralPath $toolBuildManifestPath -PathType Leaf)) {
    throw "Fresh tool build manifest is missing: $toolBuildManifestPath"
}
$toolBuildManifest = Get-Content -LiteralPath $toolBuildManifestPath -Raw | ConvertFrom-Json
$publishedServiceUnitsRoot = Join-Path $PublishRoot 'service-units'
New-Item -ItemType Directory -Path $publishedServiceUnitsRoot -Force | Out-Null
foreach ($tool in @($toolBuildManifest.tools)) {
    $toolOutput = Join-Path $RepoRoot ([string]$tool.output -replace '/', '\')
    $toolServiceUnits = Join-Path $toolOutput 'service-units'
    if (-not (Test-Path -LiteralPath $toolServiceUnits -PathType Container)) {
        continue
    }
    foreach ($unitDirectory in Get-ChildItem -LiteralPath $toolServiceUnits -Directory) {
        $destination = Join-Path $publishedServiceUnitsRoot $unitDirectory.Name
        if (Test-Path -LiteralPath $destination) {
            throw "Duplicate Service Unit id in release payload: $($unitDirectory.Name)"
        }
        Copy-Item -LiteralPath $unitDirectory.FullName -Destination $destination -Recurse -Force
    }
}
$LauncherPublishRoot = Join-Path $PublishRoot 'App'
Invoke-Native -FilePath 'dotnet' -ArgumentList @('publish', 'src\MyPowerTools.App\MyPowerTools.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishAot=true', '-p:OptimizationPreference=Speed', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $LauncherPublishRoot)
Copy-Item -LiteralPath (Join-Path $LauncherPublishRoot 'MyPowerTools.exe') -Destination (Join-Path $PublishRoot 'MyPowerTools.exe') -Force
Remove-Item -LiteralPath $LauncherPublishRoot -Recurse -Force

Copy-DirectoryContents -Source $ModuleStagingRootFull -Destination (Join-Path $PublishRoot 'modules')
Copy-Item -Path (Join-Path $RepoRoot 'schemas') -Destination (Join-Path $PublishRoot 'schemas') -Recurse -Force
Copy-Item -Path (Join-Path $RepoRoot 'ui') -Destination (Join-Path $PublishRoot 'ui') -Recurse -Force
Assert-DirectoryMatches -Source $ModuleStagingRootFull -Destination (Join-Path $PublishRoot 'modules')
Assert-DirectoryMatches -Source (Join-Path $RepoRoot 'schemas') -Destination (Join-Path $PublishRoot 'schemas')
Copy-DirectoryWithoutBuildArtifacts -Source (Join-Path $RepoRoot 'assets') -Destination (Join-Path $PublishRoot 'assets')
Copy-DirectoryWithoutBuildArtifacts -Source (Join-Path $RepoRoot 'templates') -Destination (Join-Path $PublishRoot 'templates')
Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\install-windows.ps1') -Destination (Join-Path $PublishRoot 'install-windows.ps1') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\uninstall-windows.ps1') -Destination (Join-Path $PublishRoot 'uninstall-windows.ps1') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\configure-user-services.ps1') -Destination (Join-Path $PublishRoot 'configure-user-services.ps1') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\start-user-runtime.ps1') -Destination (Join-Path $PublishRoot 'start-user-runtime.ps1') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\new-ota-file-manifest.ps1') -Destination (Join-Path $PublishRoot 'new-ota-file-manifest.ps1') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\new-ota-delta-package.ps1') -Destination (Join-Path $PublishRoot 'new-ota-delta-package.ps1') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\invoke-ota-update.ps1') -Destination (Join-Path $PublishRoot 'invoke-ota-update.ps1') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\Start-MyPowerTools.cmd') -Destination (Join-Path $PublishRoot 'Start-MyPowerTools.cmd') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\Manage-DoubaoRuntime.ps1') -Destination (Join-Path $PublishRoot 'Manage-DoubaoRuntime.ps1') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'START_HERE.md') -Destination (Join-Path $PublishRoot 'START_HERE.md') -Force
Invoke-Native -FilePath 'pwsh.exe' -ArgumentList @(
    '-NoLogo',
    '-NoProfile',
    '-NonInteractive',
    '-File',
    (Join-Path $PSScriptRoot 'stage-tool-runtimes.ps1'),
    '-PublishRoot',
    $PublishRoot)

$packageByTool = @{
    'adb-forwarder' = 'adb-forwarder'
    'remote-notifications' = 'android-tools-suite'
    'remote-commands' = 'android-tools-suite'
    'process-monitor' = 'android-tools-suite'
    'paste-image' = 'paste-image'
    'local-lag-cleaner' = 'local-lag-cleaner'
    'screenease' = 'screenease'
    'smartbird-thermostat' = 'smartbird-thermostat'
    'doubao-computer-use' = 'doubao-agent'
}
$toolProvenance = foreach ($toolId in $packageByTool.Keys | Sort-Object) {
    $toolRoot = Join-Path $RepoRoot "tools\$toolId"
    $releaseContract = Get-Content -Raw -LiteralPath (Join-Path $toolRoot 'tool-release.json') | ConvertFrom-Json
    $submoduleCommit = (& git -C $toolRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to resolve submodule commit for $toolId"
    }
    $packageId = $packageByTool[$toolId]
    $packageHashesPath = Join-Path $PublishRoot "modules\$packageId\shared\package.hashes.json"
    [ordered]@{
        toolId = $toolId
        status = if ($releaseContract.status) { [string]$releaseContract.status } else { 'active' }
        submoduleCommit = $submoduleCommit
        packageId = $packageId
        packageHashesSha256 = if (Test-Path -LiteralPath $packageHashesPath) {
            (Get-FileHash -LiteralPath $packageHashesPath -Algorithm SHA256).Hash.ToLowerInvariant()
        } else { $null }
    }
}
$provenance = [ordered]@{
    schemaVersion = 2
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    buildSource = 'tools/* submodules'
    shellToolSource = 'linked from tools/*/current-integration'
    smartBirdRuntimeSource = 'tools/smartbird-thermostat/original-source'
    doubaoRuntimeSource = 'tools/doubao-computer-use/original-source/computer_use'
    windowsShell = [ordered]@{
        runtimeIdentifier = 'win-x64'
        selfContained = $true
        publishReadyToRun = $true
        publishReadyToRunComposite = $true
        files = [ordered]@{
            executableSha256 = (Get-FileHash -LiteralPath (Join-Path $PublishRoot 'Shell\MyPowerTools.Shell.Avalonia.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
            assemblySha256 = (Get-FileHash -LiteralPath (Join-Path $PublishRoot 'Shell\MyPowerTools.Shell.Avalonia.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
            runtimeConfigSha256 = (Get-FileHash -LiteralPath (Join-Path $PublishRoot 'Shell\MyPowerTools.Shell.Avalonia.runtimeconfig.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    tools = @($toolProvenance)
}
$provenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PublishRoot 'build-provenance.json') -Encoding UTF8

Invoke-Native -FilePath (Join-Path $PublishRoot 'Cli\MyPowerTools.Cli.exe') -ArgumentList @(
    'validate',
    (Join-Path $PublishRoot 'modules'),
    '--schemas',
    (Join-Path $PublishRoot 'schemas'))

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}

Compress-Archive -Path (Join-Path $PublishRoot '*') -DestinationPath $ZipPath
Write-Sha256File -Path $ZipPath
Invoke-Native -FilePath 'pwsh.exe' -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'release-metadata.ps1'), '-RepoRoot', $RepoRoot, '-ArtifactsRoot', $Artifacts)
Invoke-Native -FilePath 'pwsh.exe' -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'release-notes.ps1'), '-RepoRoot', $RepoRoot, '-ArtifactsRoot', $Artifacts)

if ($PortableOnly) {
    Write-Host "Portable package ready at $PublishRoot"
    Write-Host $ZipPath
    return
}

$innoCompiler = Find-InnoCompiler
if ($innoCompiler) {
    $releaseMetadata = Get-Content -Raw -LiteralPath (Join-Path $Artifacts 'release-metadata.json') | ConvertFrom-Json
    $releaseVersion = [string]$releaseMetadata.version
    if ([string]::IsNullOrWhiteSpace($releaseVersion)) {
        throw 'Release metadata does not contain a version for the installer.'
    }

    $setupBaseName = "MyPowerTools-Setup-$releaseVersion-win-x64"
    $setupPath = Join-Path $Artifacts "$setupBaseName.exe"
    $setupHashPath = "$setupPath.sha256"

    foreach ($stalePath in @($setupPath, $setupHashPath)) {
        if (Test-Path -LiteralPath $stalePath) {
            Remove-Item -LiteralPath $stalePath -Force
        }
    }

    Invoke-Native -FilePath $innoCompiler -ArgumentList @(
        '/Qp',
        "/DMyAppVersion=$releaseVersion",
        "/O$Artifacts",
        "/F$setupBaseName",
        $InstallerScript)

    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw "Inno Setup completed without producing $setupPath"
    }

    $setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
    "$setupHash  $([System.IO.Path]::GetFileName($setupPath))" |
        Set-Content -LiteralPath $setupHashPath -Encoding ASCII
    Write-Host $setupPath
    Write-Host $setupHashPath
} else {
    Write-Warning 'Inno Setup compiler was not found. The portable ZIP was generated; Setup.exe was skipped.'
}

$sourceArchivePath = $null
if (Test-Path -LiteralPath (Join-Path $RepoRoot '.git')) {
    $sourceArchivePath = Join-Path $Artifacts ("MyPowerTools-Source-{0}.zip" -f (Get-Date -Format 'yyyyMMdd'))
    Invoke-Native -FilePath 'pwsh.exe' -ArgumentList @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-File',
        (Join-Path $PSScriptRoot 'create-source-bundle.ps1'),
        '-RepoRoot',
        $RepoRoot,
        '-ArchivePath',
        $sourceArchivePath)
    Write-Sha256File -Path $sourceArchivePath
} else {
    Write-Warning 'Git metadata is absent; binary publishing completed and source archive refresh was skipped.'
}

Write-Host $ZipPath
if ($sourceArchivePath) {
    Write-Host $sourceArchivePath
}
