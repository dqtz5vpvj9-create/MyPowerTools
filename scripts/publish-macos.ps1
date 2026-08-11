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
    $OutputRoot = Join-Path $artifactsRoot "publish\macos-$Architecture"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
if (-not $OutputRoot.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must stay under $artifactsRoot"
}

$stageRoot = Join-Path $artifactsRoot "macos-stage\$runtimeIdentifier"
$appBundle = Join-Path $OutputRoot 'MyPowerTools.app'
$contentsRoot = Join-Path $appBundle 'Contents'
$macRoot = Join-Path $contentsRoot 'MacOS'
$resourcesRoot = Join-Path $contentsRoot 'Resources'

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

    $moduleStageRoot = Join-Path $stageRoot "Modules\$($Definition.Destination)"
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
    $surfaceBuildRoot = Join-Path $stageRoot "Surfaces\$($Definition.Destination)"
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
New-Item -ItemType Directory -Path $stageRoot, $macRoot, $resourcesRoot -Force | Out-Null

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

Copy-DirectoryContents -Source (Join-Path $stageRoot 'App') -Destination $macRoot
Copy-DirectoryContents -Source (Join-Path $stageRoot 'Shell') -Destination (Join-Path $macRoot 'Shell')
Copy-DirectoryContents -Source (Join-Path $stageRoot 'Runner') -Destination (Join-Path $macRoot 'Runner')
Copy-DirectoryContents -Source (Join-Path $stageRoot 'ServiceManager') -Destination (Join-Path $macRoot 'ServiceManager')
Copy-DirectoryContents -Source (Join-Path $stageRoot 'RemoteNotificationsService') -Destination $macRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/macos/Info.plist') -Destination (Join-Path $contentsRoot 'Info.plist') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/macos/PkgInfo') -Destination (Join-Path $contentsRoot 'PkgInfo') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'assets/MyPowerTools.svg') -Destination $resourcesRoot -Force

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
$surfaceSource = Join-Path (Split-Path -Parent $surfaceProject) "bin\$Configuration\net10.0"
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
    $nativeOutput = Join-Path $stageRoot 'libMptMacNative.dylib'
    Invoke-Native -FilePath 'xcrun' -ArgumentList @(
        'clang++',
        '-std=c++17',
        '-fobjc-arc',
        '-dynamiclib',
        '-arch', $Architecture,
        '-mmacosx-version-min=12.0',
        '-framework', 'Cocoa',
        '-framework', 'WebKit',
        '-framework', 'UserNotifications',
        '-framework', 'Security',
        '-install_name', '@rpath/libMptMacNative.dylib',
        (Join-Path $repoRoot 'native/macos/MptMacNative/MptMacNative.mm'),
        '-o', $nativeOutput
    ) -Activity 'MptMacNative build'
    foreach ($destination in @(
        $macRoot,
        (Join-Path $macRoot 'Shell'),
        (Join-Path $macRoot 'Runner')
    )) {
        Copy-Item -LiteralPath $nativeOutput -Destination (Join-Path $destination 'libMptMacNative.dylib') -Force
    }
}

if ($IsMacOS) {
    foreach ($executable in @(
        (Join-Path $macRoot 'MyPowerTools'),
        (Join-Path $macRoot 'Shell/MyPowerTools.Shell.Avalonia'),
        (Join-Path $macRoot 'Runner/MyPowerTools.Runner'),
        (Join-Path $macRoot 'ServiceManager/MyPowerTools.ServiceManager'),
        (Join-Path $macRoot 'RemoteNotifications.Service'),
        (Join-Path $macRoot "modules/android-tools-suite/macos/$Architecture/powertoold")
    )) {
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
    foreach ($candidate in Get-ChildItem -LiteralPath $macRoot -Recurse -File) {
        $fileDescription = (& /usr/bin/file '-b' $candidate.FullName 2>$null) -join ' '
        if (-not $fileDescription.Contains('Mach-O', [System.StringComparison]::Ordinal)) {
            continue
        }
        # .NET on macOS ad-hoc signs apphosts during publish. Re-signing a
        # binary whose existing signature already sealed nested code (for
        # example a single-file bundle) fails with "code object is not signed
        # at all / In subcomponent: ...". Clear the stale signature first.
        & /usr/bin/codesign '--remove-signature' $candidate.FullName 2>$null
        $signArguments = @('--force', '--sign', $CodeSignIdentity, '--timestamp=none')
        if (-not $fileDescription.Contains('shared library', [System.StringComparison]::OrdinalIgnoreCase)) {
            $signArguments += @('--options', 'runtime', '--entitlements', $entitlements)
        }
        $signArguments += $candidate.FullName
        Invoke-Native -FilePath 'codesign' -ArgumentList $signArguments -Activity "codesign $($candidate.FullName)"
    }
    Invoke-Native -FilePath 'codesign' -ArgumentList @(
        '--force', '--sign', $CodeSignIdentity,
        '--options', 'runtime',
        '--entitlements', $entitlements,
        '--timestamp=none',
        $appBundle
    ) -Activity 'codesign MyPowerTools.app'
    Invoke-Native -FilePath 'codesign' -ArgumentList @('--verify', '--deep', '--strict', $appBundle) -Activity 'codesign verification'
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
