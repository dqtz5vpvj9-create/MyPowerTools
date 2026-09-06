[CmdletBinding()]
param(
    [ValidateSet('arm64', 'x64')]
    [string]$Architecture = 'arm64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot = '',
    [string]$CodeSignIdentity = '-',
    [switch]$SkipNativeBuild,
    [switch]$SkipCodeSign
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$runtimeIdentifier = "osx-$Architecture"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $artifactsRoot "publish/macos-$Architecture"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
if (-not $OutputRoot.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must stay under $artifactsRoot"
}

$stageRoot = Join-Path $artifactsRoot "macos-stage/$runtimeIdentifier"
$appBundle = Join-Path $OutputRoot 'MyPowerTools.app'
$contentsRoot = Join-Path $appBundle 'Contents'
$macRoot = Join-Path $contentsRoot 'MacOS'
$resourcesRoot = Join-Path $contentsRoot 'Resources'
# Nested helper bundles. Contents/MacOS stays the application root that every host finds by
# walking up from its own directory, so the helpers are nested under it rather than under
# Contents/Helpers. Each one turns a background host into a real bundle: an executable at
# <bundle>.app/Contents/MacOS/<name> is what makes NSBundle.mainBundle resolve, and without a
# bundle identifier UNUserNotificationCenter, Dock activation and Launch Services registration
# are all unavailable to the process.
$helpersRoot = Join-Path $macRoot 'Helpers'
$macHelpers = @(
    [pscustomobject]@{
        Key = 'Shell'
        Bundle = 'MyPowerTools Shell.app'
        Executable = 'MyPowerTools.Shell.Avalonia'
        Legacy = 'Shell'
        Plist = 'Shell.Info.plist'
        NeedsNativeLibrary = $true
    },
    [pscustomobject]@{
        Key = 'Runner'
        Bundle = 'MyPowerTools Runner.app'
        Executable = 'MyPowerTools.Runner'
        Legacy = 'Runner'
        Plist = 'Runner.Info.plist'
        NeedsNativeLibrary = $true
    },
    [pscustomobject]@{
        Key = 'ServiceManager'
        Bundle = 'MyPowerTools ServiceManager.app'
        Executable = 'MyPowerTools.ServiceManager'
        Legacy = 'ServiceManager'
        Plist = 'ServiceManager.Info.plist'
        NeedsNativeLibrary = $false
    },
    [pscustomobject]@{
        Key = 'RemoteNotificationsService'
        Bundle = 'MyPowerTools Remote Notifications.app'
        Executable = 'RemoteNotifications.Service'
        Legacy = 'RemoteNotifications'
        Plist = 'RemoteNotifications.Info.plist'
        NeedsNativeLibrary = $true
    }
)

function Get-HelperBundleRoot {
    param([Parameter(Mandatory = $true)][pscustomobject]$Helper)
    return Join-Path $helpersRoot $Helper.Bundle
}

function Get-HelperExecutableRoot {
    param([Parameter(Mandatory = $true)][pscustomobject]$Helper)
    return Join-Path (Get-HelperBundleRoot -Helper $Helper) 'Contents/MacOS'
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$Activity
    )
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Activity failed with exit code $LASTEXITCODE"
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Missing publish output: $Source"
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function Copy-ModuleTemplate {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Missing module template: $Source"
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        $relativePath = [System.IO.Path]::GetRelativePath($Source, $file.FullName)
        $normalizedRelativePath = $relativePath.Replace('\', '/')
        if ($file.Extension -in @('.dll', '.exe', '.pdb') -or
            $file.Name.EndsWith('.deps.json', [System.StringComparison]::OrdinalIgnoreCase) -or
            $file.Name.EndsWith('.runtimeconfig.json', [System.StringComparison]::OrdinalIgnoreCase) -or
            $normalizedRelativePath -in @('shared/package.hashes.json', 'shared/package.signature.json')) {
            continue
        }

        $destinationPath = Join-Path $Destination $relativePath
        $destinationDirectory = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force
    }
}

function Build-StandaloneModule {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Definition,
        [Parameter(Mandatory = $true)][string]$CliProject
    )

    $moduleStageRoot = Join-Path $stageRoot "Modules/$($Definition.Destination)"
    $templateParameters = @{
        Source = Join-Path $repoRoot $Definition.Template
        Destination = $moduleStageRoot
    }
    Copy-ModuleTemplate @templateParameters

    $adapterProject = Join-Path $repoRoot $Definition.AdapterProject
    Invoke-Native -FilePath 'dotnet' -ArgumentList @(
        'build', $adapterProject,
        '--configuration', $Configuration,
        '--nologo',
        "-p:MyPowerToolsRepoRoot=$repoRoot",
        "-p:ModulePackageRoot=$moduleStageRoot",
        '-p:StageRepositoryModule=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    ) -Activity "$($Definition.Id) adapter build"

    $surfaceProject = Join-Path $repoRoot $Definition.SurfaceProject
    $surfaceBuildRoot = Join-Path $stageRoot "Surfaces/$($Definition.Destination)"
    Invoke-Native -FilePath 'dotnet' -ArgumentList @(
        'build', $surfaceProject,
        '--configuration', $Configuration,
        '--output', $surfaceBuildRoot,
        '--nologo',
        "-p:MyPowerToolsRepoRoot=$repoRoot",
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    ) -Activity "$($Definition.Id) Surface build"

    $surfaceDestination = Join-Path $moduleStageRoot 'ui/surface'
    New-Item -ItemType Directory -Path $surfaceDestination -Force | Out-Null
    foreach ($pattern in @('*.dll', '*.deps.json')) {
        foreach ($surfaceFile in Get-ChildItem -LiteralPath $surfaceBuildRoot -Filter $pattern -File) {
            Copy-Item -LiteralPath $surfaceFile.FullName -Destination $surfaceDestination -Force
        }
    }

    Invoke-Native -FilePath 'dotnet' -ArgumentList @(
        'run', '--project', $CliProject,
        '--configuration', $Configuration,
        '--', 'package', 'sign-local', $moduleStageRoot
    ) -Activity "sign macOS $($Definition.Id) module package"
    $copyParameters = @{
        Source = $moduleStageRoot
        Destination = Join-Path $macRoot "modules/$($Definition.Destination)"
    }
    Copy-DirectoryContents @copyParameters
}

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
if (Test-Path -LiteralPath $appBundle) {
    Remove-Item -LiteralPath $appBundle -Recurse -Force
}
New-Item -ItemType Directory -Path $stageRoot, $macRoot, $resourcesRoot, $helpersRoot -Force | Out-Null

$projects = [ordered]@{
    App = 'src/MyPowerTools.App/MyPowerTools.App.csproj'
    Shell = 'src/MyPowerTools.Shell.Avalonia/MyPowerTools.Shell.Avalonia.csproj'
    Runner = 'src/MyPowerTools.Runner/MyPowerTools.Runner.csproj'
    ServiceManager = 'src/MyPowerTools.ServiceManager/MyPowerTools.ServiceManager.csproj'
    RemoteNotificationsService = 'tools/remote-notifications/current-integration/src/RemoteNotifications.Service/RemoteNotifications.Service.csproj'
}
$standaloneModules = @(
    [pscustomobject]@{
        Id = 'adb-forwarder'
        Destination = 'adb-forwarder'
        Template = 'tools/adb-forwarder/current-integration/modules/adb-forwarder'
        AdapterProject = 'tools/adb-forwarder/current-integration/src/AdbForwarder.MyPowerTools/AdbForwarder.MyPowerTools.csproj'
        SurfaceProject = 'tools/adb-forwarder/current-integration/src/AdbForwarder.Surface/AdbForwarder.Surface.csproj'
    },
    [pscustomobject]@{
        Id = 'doubao-computer-use'
        Destination = 'doubao-agent'
        Template = 'tools/doubao-computer-use/current-integration/modules/doubao-agent'
        AdapterProject = 'tools/doubao-computer-use/current-integration/src/DoubaoAgent.MyPowerTools/DoubaoAgent.MyPowerTools.csproj'
        SurfaceProject = 'tools/doubao-computer-use/current-integration/src/DoubaoAgent.Surface/DoubaoAgent.Surface.csproj'
    },
    [pscustomobject]@{
        Id = 'paste-image'
        Destination = 'paste-image'
        Template = 'tools/paste-image/current-integration/modules/paste-image'
        AdapterProject = 'tools/paste-image/current-integration/src/PasteImage.MyPowerTools/PasteImage.MyPowerTools.csproj'
        SurfaceProject = 'tools/paste-image/current-integration/src/PasteImage.Surface/PasteImage.Surface.csproj'
    },
    [pscustomobject]@{
        Id = 'screenease'
        Destination = 'screenease'
        Template = 'tools/screenease/current-integration/modules/screenease'
        AdapterProject = 'tools/screenease/current-integration/src/ScreenEase.MyPowerTools/ScreenEase.MyPowerTools.csproj'
        SurfaceProject = 'tools/screenease/current-integration/src/ScreenEase.Surface/ScreenEase.Surface.csproj'
    },
    [pscustomobject]@{
        Id = 'smartbird-thermostat'
        Destination = 'smartbird-thermostat'
        Template = 'tools/smartbird-thermostat/current-integration/modules/smartbird-thermostat'
        AdapterProject = 'tools/smartbird-thermostat/current-integration/src/SmartBirdThermostat.MyPowerTools/SmartBirdThermostat.MyPowerTools.csproj'
        SurfaceProject = 'tools/smartbird-thermostat/current-integration/src/SmartBird.Surface/SmartBird.Surface.csproj'
    }
)
foreach ($entry in $projects.GetEnumerator()) {
    $projectPath = Join-Path $repoRoot $entry.Value
    $publishPath = Join-Path $stageRoot $entry.Key
    $publishArguments = @(
        'publish', $projectPath,
        '--configuration', $Configuration,
        '--runtime', $runtimeIdentifier,
        '--self-contained', 'true',
        '--output', $publishPath,
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    if ($entry.Key -eq 'App' -or $entry.Key -eq 'RemoteNotificationsService') {
        # Single-file apphosts embed managed assemblies and runtime files that
        # codesign treats as unsigned nested code on macOS. Publish the normal
        # apphost + DLL layout so signing succeeds.
        $publishArguments += '-p:PublishSingleFile=false'
    }
    Write-Host "PUBLISH $($entry.Key): dotnet $($publishArguments -join ' ')"
    Invoke-Native -FilePath 'dotnet' -ArgumentList $publishArguments -Activity "dotnet publish $($entry.Key)"
}

# The packaging plists carry a placeholder version. Launch Services, Finder's Get Info and
# every "which build is this" question read the copy inside the bundle, so stamp the central
# product version into each one rather than shipping whatever the template happened to say.
$productVersion = [string](Get-Content -LiteralPath (Join-Path $repoRoot 'version.json') -Raw |
    ConvertFrom-Json).version
if ($productVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "version.json contains an invalid version '$productVersion'."
}

function Copy-StampedPlist {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    $text = Get-Content -LiteralPath $Source -Raw
    foreach ($versionKey in @('CFBundleShortVersionString', 'CFBundleVersion')) {
        $text = [regex]::Replace(
            $text,
            "(<key>$versionKey</key>\s*<string>)[^<]*(</string>)",
            ('${1}' + $productVersion + '${2}'))
    }
    if ($text -notmatch "<string>$([regex]::Escape($productVersion))</string>") {
        throw "Could not stamp the product version into $Destination."
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    [System.IO.File]::WriteAllText($Destination, $text)
}

Copy-DirectoryContents -Source (Join-Path $stageRoot 'App') -Destination $macRoot

foreach ($helper in $macHelpers) {
    $helperContents = Join-Path (Get-HelperBundleRoot -Helper $helper) 'Contents'
    Copy-DirectoryContents -Source (Join-Path $stageRoot $helper.Key) -Destination (Join-Path $helperContents 'MacOS')
    Copy-StampedPlist `
        -Source (Join-Path $repoRoot "packaging/macos/Helpers/$($helper.Plist)") `
        -Destination (Join-Path $helperContents 'Info.plist')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/macos/PkgInfo') `
        -Destination (Join-Path $helperContents 'PkgInfo') -Force
    New-Item -ItemType Directory -Path (Join-Path $helperContents 'Resources') -Force | Out-Null
    Write-Host "HELPER $($helper.Bundle) staged at $(Get-HelperExecutableRoot -Helper $helper)"
}

Copy-StampedPlist `
    -Source (Join-Path $repoRoot 'packaging/macos/Info.plist') `
    -Destination (Join-Path $contentsRoot 'Info.plist')
Write-Host "Info.plist version stamped as $productVersion"
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/macos/PkgInfo') -Destination (Join-Path $contentsRoot 'PkgInfo') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'assets/MyPowerTools.svg') -Destination $resourcesRoot -Force

# Compatibility links for the hosts that still resolve their siblings through the flat
# Contents/MacOS/<Host>/<executable> layout shared with Windows (the Shell's Runner and
# ServiceManager bootstrappers, ShellRuntimeIdentity, RemoteNotifications' activation
# launcher). The real executables live in the helper bundles; these keep the File.Exists and
# Directory.Exists probes answering until those callers learn the nested layout.
if (-not $IsWindows) {
    foreach ($helper in $macHelpers) {
        $legacyRoot = Join-Path $macRoot $helper.Legacy
        New-Item -ItemType Directory -Path $legacyRoot -Force | Out-Null
        $legacyLink = Join-Path $legacyRoot $helper.Executable
        if (Test-Path -LiteralPath $legacyLink) {
            Remove-Item -LiteralPath $legacyLink -Force
        }
        Invoke-Native -FilePath '/bin/ln' -ArgumentList @(
            '-s',
            "../Helpers/$($helper.Bundle)/Contents/MacOS/$($helper.Executable)",
            $legacyLink
        ) -Activity "link $legacyLink"
    }
}

if ($IsMacOS) {
    $iconPng = Join-Path $stageRoot 'MyPowerTools-1024.png'
    & sips '-s' 'format' 'png' (Join-Path $repoRoot 'assets/MyPowerTools.svg') '--out' $iconPng 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $iconPng -PathType Leaf)) {
        $quickLookOutput = Join-Path $stageRoot 'MyPowerTools.svg.png'
        Invoke-Native -FilePath '/usr/bin/qlmanage' -ArgumentList @(
            '-t', '-s', '1024', '-o', $stageRoot,
            (Join-Path $repoRoot 'assets/MyPowerTools.svg')
        ) -Activity 'render MyPowerTools.svg'
        if (-not (Test-Path -LiteralPath $quickLookOutput -PathType Leaf)) {
            throw "Quick Look did not produce the expected icon preview: $quickLookOutput"
        }
        Move-Item -LiteralPath $quickLookOutput -Destination $iconPng -Force
    }

    $iconset = Join-Path $stageRoot 'MyPowerTools.iconset'
    New-Item -ItemType Directory -Path $iconset -Force | Out-Null
    foreach ($icon in @(
        @{ Size = 16; Name = 'icon_16x16.png' },
        @{ Size = 32; Name = 'icon_16x16@2x.png' },
        @{ Size = 32; Name = 'icon_32x32.png' },
        @{ Size = 64; Name = 'icon_32x32@2x.png' },
        @{ Size = 128; Name = 'icon_128x128.png' },
        @{ Size = 256; Name = 'icon_128x128@2x.png' },
        @{ Size = 256; Name = 'icon_256x256.png' },
        @{ Size = 512; Name = 'icon_256x256@2x.png' },
        @{ Size = 512; Name = 'icon_512x512.png' },
        @{ Size = 1024; Name = 'icon_512x512@2x.png' }
    )) {
        Invoke-Native -FilePath 'sips' -ArgumentList @(
            '-z', [string]$icon.Size, [string]$icon.Size,
            $iconPng,
            '--out', (Join-Path $iconset $icon.Name)
        ) -Activity "create $($icon.Name)"
    }
    Invoke-Native -FilePath 'iconutil' -ArgumentList @(
        '-c', 'icns', $iconset,
        '-o', (Join-Path $resourcesRoot 'MyPowerTools.icns')
    ) -Activity 'create MyPowerTools.icns'

    # A notification banner shows the icon of the bundle that posted it, so the helper that
    # publishes has to carry the product icon rather than the generic placeholder.
    foreach ($helper in $macHelpers) {
        Copy-Item -LiteralPath (Join-Path $resourcesRoot 'MyPowerTools.icns') `
            -Destination (Join-Path (Get-HelperBundleRoot -Helper $helper) 'Contents/Resources/MyPowerTools.icns') -Force
    }
}

$moduleStage = Join-Path $repoRoot 'tools/remote-notifications/artifacts/macos-package'
$toolBuildScript = Join-Path $repoRoot 'tools/remote-notifications/build.ps1'
& $toolBuildScript -MyPowerToolsRepoRoot $repoRoot -Configuration $Configuration -RuntimeIdentifier $runtimeIdentifier -OutputRoot $moduleStage
if ($LASTEXITCODE -ne 0) {
    throw "Remote Notifications module build failed with exit code $LASTEXITCODE"
}

$surfaceProject = Join-Path $repoRoot 'tools/remote-notifications/current-integration/src/RemoteNotifications.Surface/RemoteNotifications.Surface.csproj'
Invoke-Native -FilePath 'dotnet' -ArgumentList @(
    'build', $surfaceProject,
    '--configuration', $Configuration,
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
) -Activity 'Remote Notifications Surface build'
$surfaceSource = Join-Path (Split-Path -Parent $surfaceProject) "bin/$Configuration/net10.0"
$surfaceDestination = Join-Path $moduleStage 'modules/notifications/ui/surface'
New-Item -ItemType Directory -Path $surfaceDestination -Force | Out-Null
foreach ($pattern in @('*.dll', '*.deps.json')) {
    Get-ChildItem -LiteralPath $surfaceSource -Filter $pattern -File |
        Copy-Item -Destination $surfaceDestination -Force
}
$cliProject = Join-Path $repoRoot 'src/MyPowerTools.Cli/MyPowerTools.Cli.csproj'
Invoke-Native -FilePath 'dotnet' -ArgumentList @(
    'run', '--project', $cliProject,
    '--configuration', $Configuration,
    '--', 'package', 'sign-local', $moduleStage
) -Activity 'sign macOS Remote Notifications module package'
Copy-DirectoryContents -Source $moduleStage -Destination (Join-Path $macRoot 'modules/android-tools-suite')
foreach ($standaloneModule in $standaloneModules) {
    Build-StandaloneModule -Definition $standaloneModule -CliProject $cliProject
}
Copy-DirectoryContents -Source (Join-Path $repoRoot 'schemas') -Destination (Join-Path $macRoot 'schemas')

$serviceUnitsRoot = Join-Path $macRoot 'ServiceUnits/units'
New-Item -ItemType Directory -Path $serviceUnitsRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'tools/remote-notifications/current-integration/src/RemoteNotifications.Service/unit-manifest.macos.json') `
    -Destination (Join-Path $serviceUnitsRoot 'remote-notifications.service.json') -Force

if (-not $SkipNativeBuild) {
    if (-not $IsMacOS) {
        throw 'The WKWebView/UserNotifications native library must be built on macOS. Run this script on macOS or use -SkipNativeBuild for managed cross-publish validation.'
    }
    # RID segments and clang arch names disagree for Intel: osx-x64 vs -arch x86_64.
    $clangArch = if ($Architecture -eq 'x64') { 'x86_64' } else { $Architecture }
    $nativeOutput = Join-Path $stageRoot 'libMptMacNative.dylib'
    Invoke-Native -FilePath 'xcrun' -ArgumentList @(
        'clang++',
        '-std=c++17',
        '-fobjc-arc',
        '-dynamiclib',
        '-arch', $clangArch,
        '-mmacosx-version-min=12.0',
        '-framework', 'Cocoa',
        '-framework', 'WebKit',
        '-framework', 'UserNotifications',
        '-framework', 'Security',
        '-install_name', '@rpath/libMptMacNative.dylib',
        (Join-Path $repoRoot 'native/macos/MptMacNative/MptMacNative.mm'),
        '-o', $nativeOutput
    ) -Activity 'MptMacNative build'
    # DllImport("MptMacNative") resolves against the directory of the executable that loads it,
    # so every host that P/Invokes the library gets its own copy next to its own apphost. That
    # keeps the lookup free of DYLD_* variables, which System Integrity Protection strips from
    # anything launchd starts.
    $nativeDestinations = @($macRoot)
    foreach ($helper in $macHelpers) {
        if ($helper.NeedsNativeLibrary) {
            $nativeDestinations += (Get-HelperExecutableRoot -Helper $helper)
        }
    }
    foreach ($destination in $nativeDestinations) {
        Copy-Item -LiteralPath $nativeOutput -Destination (Join-Path $destination 'libMptMacNative.dylib') -Force
    }
}

if ($IsMacOS) {
    $bundleExecutables = @(
        (Join-Path $macRoot 'MyPowerTools'),
        (Join-Path $macRoot "modules/android-tools-suite/macos/$Architecture/MPTAndroidTools.Runtime")
    )
    foreach ($helper in $macHelpers) {
        $bundleExecutables += (Join-Path (Get-HelperExecutableRoot -Helper $helper) $helper.Executable)
    }
    foreach ($executable in $bundleExecutables) {
        if (Test-Path -LiteralPath $executable -PathType Leaf) {
            Invoke-Native -FilePath '/bin/chmod' -ArgumentList @('+x', $executable) -Activity "chmod $executable"
        }
    }
    # codesign --deep treats executable files as nested code objects. .NET
    # publish can leave managed DLLs executable, which makes --deep fail with
    # "code object is not signed at all"; clear the execute bit on DLLs so they
    # are treated as data files by codesign.
    Get-ChildItem -LiteralPath $appBundle -Recurse -File -Filter '*.dll' |
        ForEach-Object {
            Invoke-Native -FilePath '/bin/chmod' -ArgumentList @('-x', $_.FullName) -Activity "chmod -x $($_.FullName)"
        }

    # Diagnostics: confirm whether the App was published as a single-file
    # bundle (managed assemblies embedded in the apphost) or as the normal
    # apphost + DLL layout. codesign reports embedded assemblies as
    # subcomponents when a stale single-file signature is re-signed.
    $diagnosticApphost = Join-Path $macRoot 'MyPowerTools'
    if (Test-Path -LiteralPath $diagnosticApphost -PathType Leaf) {
        $diagnosticItem = Get-Item -LiteralPath $diagnosticApphost
        Write-Host "DIAG apphost size: $($diagnosticItem.Length) bytes"
        & /usr/bin/file '-b' $diagnosticApphost
    }
    $diagnosticCSharp = Join-Path $macRoot 'Microsoft.CSharp.dll'
    Write-Host "DIAG Microsoft.CSharp.dll exists as separate file: $(Test-Path -LiteralPath $diagnosticCSharp -PathType Leaf)"
    if (Test-Path -LiteralPath $diagnosticCSharp -PathType Leaf) {
        $diagnosticItem = Get-Item -LiteralPath $diagnosticCSharp
        Write-Host "DIAG Microsoft.CSharp.dll size: $($diagnosticItem.Length) bytes"
        & /usr/bin/file '-b' $diagnosticCSharp
    }
    $topLevelFileCount = (Get-ChildItem -LiteralPath $macRoot -File).Count
    $topLevelDllCount = (Get-ChildItem -LiteralPath $macRoot -File -Filter '*.dll').Count
    Write-Host "DIAG MacOS top-level files: $topLevelFileCount (managed DLLs: $topLevelDllCount)"
}

if (-not $SkipCodeSign) {
    if (-not $IsMacOS) {
        throw 'codesign requires macOS. Use -SkipCodeSign for managed cross-publish validation.'
    }
    $entitlements = Join-Path $repoRoot 'packaging/macos/MyPowerTools.entitlements'

    function Get-SignableFiles {
        param(
            [Parameter(Mandatory = $true)][string]$Root,
            [string]$ExcludePrefix = ''
        )
        # Symbolic links are sealed by their target string, not signed as code. Signing
        # through one would sign the file inside the helper bundle a second time and
        # invalidate that bundle's own seal.
        return @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
            -not $_.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint) -and
            ($ExcludePrefix.Length -eq 0 -or
                -not $_.FullName.StartsWith($ExcludePrefix, [System.StringComparison]::Ordinal))
        })
    }

    function Invoke-CodeSignPasses {
        param([Parameter(Mandatory = $true)][AllowEmptyCollection()][System.IO.FileInfo[]]$Files)

        # Pass 1: codesign seals every file under a bundle when signing an executable inside
        # it, and validates each sealed file as a nested code object. Managed PE assemblies,
        # JSON manifests, schema files and other data files must therefore carry a plain
        # ad-hoc signature before the native Mach-O objects (apphosts, dylibs) are signed.
        foreach ($candidate in $Files) {
            $fileDescription = (& /usr/bin/file '-b' $candidate.FullName 2>$null) -join ' '
            if ($fileDescription.Contains('Mach-O', [System.StringComparison]::Ordinal)) {
                continue
            }
            Invoke-Native -FilePath 'codesign' -ArgumentList @(
                '--force', '--sign', $CodeSignIdentity, '--timestamp=none', $candidate.FullName
            ) -Activity "codesign data file $($candidate.FullName)"
        }

        # Pass 2: native Mach-O code objects, with entitlements on executables.
        foreach ($candidate in $Files) {
            $fileDescription = (& /usr/bin/file '-b' $candidate.FullName 2>$null) -join ' '
            if (-not $fileDescription.Contains('Mach-O', [System.StringComparison]::Ordinal)) {
                continue
            }
            # .NET on macOS ad-hoc signs apphosts during publish. Re-signing a binary whose
            # existing signature already sealed nested code (for example a single-file
            # bundle) fails with "code object is not signed at all / In subcomponent: ...".
            # Clear the stale signature first.
            & /usr/bin/codesign '--remove-signature' $candidate.FullName 2>$null
            $signArguments = @('--force', '--sign', $CodeSignIdentity, '--timestamp=none')
            if (-not $fileDescription.Contains('shared library', [System.StringComparison]::OrdinalIgnoreCase)) {
                $signArguments += @('--options', 'runtime', '--entitlements', $entitlements)
            }
            $signArguments += $candidate.FullName
            Invoke-Native -FilePath 'codesign' -ArgumentList $signArguments -Activity "codesign $($candidate.FullName)"
        }
    }

    # Nested bundles are sealed into the outer signature, so each helper has to be complete
    # and signed before MyPowerTools.app is signed over it.
    foreach ($helper in $macHelpers) {
        $helperBundle = Get-HelperBundleRoot -Helper $helper
        Invoke-CodeSignPasses -Files (Get-SignableFiles -Root $helperBundle)
        Invoke-Native -FilePath 'codesign' -ArgumentList @(
            '--force', '--sign', $CodeSignIdentity,
            '--options', 'runtime',
            '--entitlements', $entitlements,
            '--timestamp=none',
            $helperBundle
        ) -Activity "codesign $($helper.Bundle)"
    }

    Invoke-CodeSignPasses -Files (Get-SignableFiles `
        -Root $macRoot `
        -ExcludePrefix ($helpersRoot + [System.IO.Path]::DirectorySeparatorChar))
    Invoke-Native -FilePath 'codesign' -ArgumentList @(
        '--force', '--sign', $CodeSignIdentity,
        '--options', 'runtime',
        '--entitlements', $entitlements,
        '--timestamp=none',
        $appBundle
    ) -Activity 'codesign MyPowerTools.app'
    Invoke-Native -FilePath 'codesign' -ArgumentList @('--verify', '--deep', '--strict', $appBundle) -Activity 'codesign verification'
    foreach ($helper in $macHelpers) {
        Invoke-Native -FilePath 'codesign' -ArgumentList @(
            '--verify', '--strict', (Get-HelperBundleRoot -Helper $helper)
        ) -Activity "codesign verification $($helper.Bundle)"
    }
}

$releaseRoot = Join-Path $artifactsRoot 'release'
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$zipName = "MyPowerTools-macos-$Architecture.zip"
$zipPath = Join-Path $releaseRoot $zipName
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
if ($IsMacOS) {
    & /usr/bin/ditto -c -k --keepParent $appBundle $zipPath
    if ($LASTEXITCODE -ne 0) {
        throw "ditto failed to create $zipPath with exit code $LASTEXITCODE"
    }
} else {
    Compress-Archive -Path $appBundle -DestinationPath $zipPath -CompressionLevel Optimal
}
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$zipHash  $zipName" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII

Write-Host $zipPath
Write-Host $appBundle
