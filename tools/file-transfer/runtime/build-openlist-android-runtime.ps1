<#
.SYNOPSIS
Builds the pinned OpenList Android runtime from source into the APK-embeddable staging layout.

.DESCRIPTION
Android 10 (API 29) forbids executing files from an app's data directory, so the embedded runtime
cannot be downloaded on the device: it must arrive inside the APK as lib/<abi>/libopenlist.so and be
extracted by Android into the app's native library directory.

The official v4.2.6 Android release asset cannot be used for that: it is built with -tags=jsoniter
only, so its GORM SQLite driver is the pure-Go glebarez/modernc stack, and modernc.org/libc v1.22.5
issues the raw lstat syscall (syscall 6 on x86_64). Android's app seccomp filter rejects that
syscall, so `openlist admin random` (the admin initialization OpenListRuntime performs before the
first server start) dies with SIGSYS before it can create the database.

This script builds the same pinned tag with the OpenList project's own sqlite_cgo_compat tag, which
selects github.com/mattn/go-sqlite3 compiled by the Android NDK against Bionic libc. Bionic
implements stat/lstat through fstatat, which app seccomp allows, so the runtime stops making
forbidden syscalls without weakening the app sandbox. The link also uses 16 KB page alignment so the
payload runs on 16 KB memory page Android 15/16 devices.

Everything is pinned in openlist-runtime.json: source tag, commit, Go toolchain, build tags, frontend
release, NDK revision and loader page size. The payload is validated structurally (Bionic PIE,
expected ABI, loader, page alignment) and by build information (go-sqlite3 linked, no modernc.org
module). No checksum scheme is added: integrity stays on the pinned version plus the official HTTPS
origins, exactly like the desktop download path.

The staged tree is only replaced after every requested ABI has built and passed validation, so a
failing build cannot leave a broken runtime in the packaging layout.

.PARAMETER Abi
Which Android ABIs to build. 'all' builds both the phone (arm64) and emulator (x64) runtime.

.PARAMETER CacheDirectory
Where downloads, the source tree, the Go module cache and intermediate binaries live. Defaults to the
shared /mnt/cache/data-cache location when it is writable, otherwise to the repository scratch class
artifacts/.tmp-* that scripts/artifacts-policy.json already governs.

.PARAMETER OutputDirectory
Stage root. Defaults to runtime/stage next to this script; the staged tree is git-ignored.

.PARAMETER NdkRoot
Explicit Android NDK root. Without it the pinned revision is looked up through ANDROID_NDK_ROOT,
ANDROID_NDK_HOME, ANDROID_NDK, the Android SDK ndk/<revision> directories and the conventional
per-host locations. Naming a root accepts a different revision with a warning.

.PARAMETER GoProxy
GOPROXY for the module and toolchain downloads. Empty keeps the host setting, or the public default
when the host set none.

.PARAMETER GoToolchain
GOTOOLCHAIN value. Defaults to the pinned toolchain from the manifest, so the build uses exactly the
Go release the manifest names. Use 'local' to force the host toolchain.

.PARAMETER DownloadNdk
Download and unpack the pinned official NDK archive when no matching installation exists
(linux-x86_64 hosts only; that is the archive the manifest pins).

.PARAMETER Force
Re-download the source/frontend archives and rebuild even when cached copies exist.

.EXAMPLE
pwsh -NoLogo -NoProfile -File tools/file-transfer/runtime/build-openlist-android-runtime.ps1 -Abi all
#>
[CmdletBinding()]
param(
    [ValidateSet('arm64', 'x64', 'all')]
    [string]$Abi = 'all',

    [string]$CacheDirectory = '',

    [string]$OutputDirectory = '',

    [string]$NdkRoot = '',

    [string]$GoProxy = '',

    [string]$GoToolchain = '',

    [switch]$DownloadNdk,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'OpenListRuntimePackage.psm1') -Force

function Invoke-OpenListProcess {
    <#
    .SYNOPSIS
    Runs one native program with an explicit argument list and optional environment overrides,
    capturing stdout/stderr without a shell layer.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [string[]]$Arguments = @(),
        [hashtable]$Environment = @{},
        [string]$WorkingDirectory = '',
        [switch]$Echo
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Executable
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    foreach ($argument in $Arguments) { $psi.ArgumentList.Add($argument) }
    foreach ($name in $Environment.Keys) { $psi.Environment[$name] = [string]$Environment[$name] }

    $process = [System.Diagnostics.Process]::Start($psi)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    if ($Echo) {
        foreach ($line in @($stdout, $stderr)) {
            if (-not $line) { continue }
            foreach ($row in ($line.TrimEnd() -split "`r?`n")) { Write-Host "    $row" -ForegroundColor DarkGray }
        }
    }
    return [pscustomobject]@{ ExitCode = $process.ExitCode; StandardOutput = $stdout; StandardError = $stderr }
}

function Resolve-OpenListWritableDirectory {
    <#
    .SYNOPSIS
    Returns a writable directory for a Go cache location: the requested one when it accepts a real
    write, otherwise the fallback inside the packaging cache. Builds stay hermetic on machines whose
    HOME (and therefore the default GOPATH/GOCACHE) is read-only, such as sandboxed build hosts.
    #>
    param([string]$Requested, [string]$Fallback, [string]$Label)

    if ($Requested) {
        try {
            New-Item -ItemType Directory -Path $Requested -Force -ErrorAction Stop | Out-Null
            $probe = Join-Path $Requested ('.write-probe-' + [Guid]::NewGuid().ToString('N'))
            Set-Content -LiteralPath $probe -Value 'ok' -ErrorAction Stop
            Remove-Item -LiteralPath $probe -Force -ErrorAction Stop
            return $Requested
        }
        catch {
            Write-Warning "$Label at $Requested is not writable; using $Fallback for this build."
        }
    }
    New-Item -ItemType Directory -Path $Fallback -Force | Out-Null
    return $Fallback
}

function Get-OpenListSourceTree {
    <#
    .SYNOPSIS
    Downloads the pinned source tag archive when needed and returns the extracted module directory.
    #>
    param($Manifest, $Build, [string]$CacheRoot, [switch]$Force)

    $archive = Join-Path $CacheRoot "openlist-$($Manifest.version).tar.gz"
    if ($Force -or -not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        Write-Host "  > download $($Build.sourceUrl)" -ForegroundColor DarkGray
        Invoke-WebRequest -Uri $Build.sourceUrl -OutFile "$archive.partial"
        Move-Item -LiteralPath "$archive.partial" -Destination $archive -Force
    }

    $sourceRoot = Join-Path $CacheRoot 'src'
    $tree = Join-Path $sourceRoot $Build.sourceArchiveRoot
    # The recursive delete below must never escape the extraction root, even if a manifest edit
    # pointed sourceArchiveRoot somewhere unexpected.
    $resolvedTree = [IO.Path]::GetFullPath($tree)
    if (-not $resolvedTree.StartsWith([IO.Path]::GetFullPath($sourceRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) {
        throw "android.build.sourceArchiveRoot must name a directory inside the extraction root; got '$($Build.sourceArchiveRoot)'."
    }
    if ($Force -or -not (Test-Path -LiteralPath (Join-Path $tree 'go.mod') -PathType Leaf)) {
        if (Test-Path -LiteralPath $tree) { Remove-Item -LiteralPath $tree -Recurse -Force }
        New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
        & tar -xzf $archive -C $sourceRoot
        if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $archive (exit $LASTEXITCODE)." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $tree 'go.mod') -PathType Leaf)) {
        throw "The source archive does not contain $($Build.sourceArchiveRoot)/go.mod."
    }
    return $tree
}

function Install-OpenListFrontend {
    <#
    .SYNOPSIS
    Places the pinned OpenList-Frontend dist into the source tree so the admin web UI is embedded.
    The official build script fetches the newest frontend release at build time; pinning the release
    here is what keeps the source build reproducible.
    #>
    param($Build, [string]$SourceTree, [string]$CacheRoot, [switch]$Force)

    $frontend = $Build.frontend
    $asset = Join-Path $CacheRoot $frontend.asset
    if ($Force -or -not (Test-Path -LiteralPath $asset -PathType Leaf)) {
        $url = '{0}/{1}/{2}' -f $frontend.downloadBase.TrimEnd('/'), $frontend.tag, $frontend.asset
        Write-Host "  > download $url" -ForegroundColor DarkGray
        Invoke-WebRequest -Uri $url -OutFile "$asset.partial"
        Move-Item -LiteralPath "$asset.partial" -Destination $asset -Force
    }

    $dist = Join-Path $SourceTree 'public/dist'
    if ($Force -or -not (Test-Path -LiteralPath (Join-Path $dist 'index.html') -PathType Leaf)) {
        if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
        New-Item -ItemType Directory -Path $dist -Force | Out-Null
        & tar -xzf $asset -C $dist
        if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $asset (exit $LASTEXITCODE)." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $dist 'index.html') -PathType Leaf)) {
        throw "$($frontend.asset) did not contain index.html; refusing to build a runtime without the admin web UI."
    }
    $versionFile = Join-Path $dist 'VERSION'
    if (Test-Path -LiteralPath $versionFile -PathType Leaf) {
        $actual = (Get-Content -LiteralPath $versionFile -Raw).Trim()
        if ($actual -and $actual -ne $frontend.tag) {
            throw "The frontend payload reports '$actual' but the manifest pins '$($frontend.tag)'."
        }
    }
    Write-Host "    frontend $($frontend.tag) -> public/dist" -ForegroundColor DarkGray
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$manifest = Get-OpenListRuntimeManifest -RuntimeRoot $PSScriptRoot
$build = Get-OpenListAndroidBuildPlan -Manifest $manifest
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'stage' }
$cache = Resolve-OpenListTempRoot -Requested $CacheDirectory -RepositoryRoot $repoRoot -Leaf 'cache'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $cache -Force | Out-Null

$selected = @($manifest.android.abis | Where-Object { $Abi -eq 'all' -or $_.runtimeIdentifier -eq "android-$Abi" })
if ($selected.Count -eq 0) { throw "No Android ABI matches -Abi $Abi in the runtime manifest." }
foreach ($entry in $selected) {
    if (-not $entry.PSObject.Properties['goArch'] -or -not $entry.goArch) {
        throw "android.abis[$($entry.androidAbi)] has no goArch in the runtime manifest."
    }
}

Write-Host "==> OpenList $($manifest.version) Android runtime (source build)" -ForegroundColor Cyan
Write-Host "    source : $($build.sourceUrl)" -ForegroundColor DarkGray
Write-Host "    cache  : $cache" -ForegroundColor DarkGray
Write-Host "    tags   : $($build.tags -join ',')" -ForegroundColor DarkGray

# Every toolchain and input is resolved before anything is built, so a missing prerequisite fails
# fast instead of halfway through staging.
$ndk = Resolve-OpenListAndroidNdk -Requested $NdkRoot -Manifest $manifest -CacheRoot $cache -Download:$DownloadNdk
Write-Host "    ndk    : $($ndk.Revision) ($($ndk.Root))" -ForegroundColor DarkGray
if (-not $ndk.MatchesPinnedRevision) {
    Write-Warning "NDK revision $($ndk.Revision) differs from the pinned $($build.ndk.revision); the staged manifest records the revision actually used."
}

$goCommand = Get-Command go -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $goCommand) {
    throw "The Go toolchain is required to build the Android runtime; install Go $($build.goMinimum)+ (pinned: $($build.goToolchain))."
}
$goExe = $goCommand.Source
$localGo = Invoke-OpenListProcess -Executable $goExe -Arguments @('version')
if ($localGo.ExitCode -ne 0) { throw "go version failed: $($localGo.StandardError.Trim())" }
if ($localGo.StandardOutput -notmatch 'go([0-9]+\.[0-9]+(?:\.[0-9]+)?)') {
    throw "Could not read the local Go version from: $($localGo.StandardOutput.Trim())"
}
$localGoVersion = [version]$Matches[1]
if ($localGoVersion -lt [version]$build.goMinimum) {
    throw "Local Go $localGoVersion is older than the required $($build.goMinimum)."
}

$toolchainSetting = if ($GoToolchain) { $GoToolchain } else { $build.goToolchain }
$goProxySetting = $GoProxy
if (-not $goProxySetting) {
    $goProxySetting = [Environment]::GetEnvironmentVariable('GOPROXY')
    if (-not $goProxySetting) { $goProxySetting = 'https://proxy.golang.org,direct' }
}

$tmpRoot = Join-Path $cache 'tmp'
New-Item -ItemType Directory -Path $tmpRoot -Force | Out-Null
$buildRoot = Join-Path $cache 'build'
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

$goPath = Resolve-OpenListWritableDirectory -Requested $env:GOPATH -Fallback (Join-Path $cache 'gopath') -Label 'GOPATH'
$goCache = Resolve-OpenListWritableDirectory -Requested $env:GOCACHE -Fallback (Join-Path $cache 'gocache') -Label 'GOCACHE'
if ($goPath -ne $env:GOPATH) { Write-Host "    gopath : $goPath" -ForegroundColor DarkGray }
if ($goCache -ne $env:GOCACHE) { Write-Host "    gocache: $goCache" -ForegroundColor DarkGray }

$sourceTree = Get-OpenListSourceTree -Manifest $manifest -Build $build -CacheRoot $cache -Force:$Force
Write-Host "    source : $sourceTree" -ForegroundColor DarkGray
Install-OpenListFrontend -Build $build -SourceTree $sourceTree -CacheRoot $cache -Force:$Force

$conf = 'github.com/OpenListTeam/OpenList/v4/internal/conf'
# -X values are single-quoted because Go's -ldflags parser splits on spaces: BuiltAt and GitAuthor
# contain spaces and would otherwise arrive as separate linker flags.
$ldflags = @(
    '-w'
    '-s'
    "-X '$conf.BuiltAt=$($build.builtAt)'"
    "-X '$conf.GitAuthor=$($build.gitAuthor)'"
    "-X '$conf.GitCommit=$($build.gitCommit)'"
    "-X '$conf.Version=$($manifest.version)'"
    "-X '$conf.WebVersion=$($build.frontend.tag)'"
) -join ' '

$pageSize = [int]$build.loaderPageSize
$stagedEntries = [System.Collections.Generic.List[object]]::new()
$built = [System.Collections.Generic.List[object]]::new()

foreach ($entry in $selected) {
    $binary = Join-Path $buildRoot "openlist-$($manifest.version)-android-$($entry.goArch)"
    Write-Host "  > build android/$($entry.goArch) ($($entry.androidAbi))" -ForegroundColor Cyan
    $environment = @{
        GOOS         = 'android'
        GOARCH       = $entry.goArch
        CGO_ENABLED  = '1'
        CC           = (Join-Path $ndk.Bin "$($entry.ndkTriple)-clang")
        CXX          = (Join-Path $ndk.Bin "$($entry.ndkTriple)-clang++")
        CGO_LDFLAGS  = "-Wl,-z,max-page-size=$pageSize"
        GOTOOLCHAIN  = $toolchainSetting
        GOFLAGS      = '-mod=mod'
        GOPROXY      = $goProxySetting
        TMPDIR       = $tmpRoot
        GOPATH       = $goPath
        GOCACHE      = $goCache
    }
    foreach ($tool in @($environment.CC, $environment.CXX)) {
        if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
            throw "The NDK at $($ndk.Root) has no $([IO.Path]::GetFileName($tool)); pass -NdkRoot with a complete installation."
        }
    }

    $goBuild = Invoke-OpenListProcess -Executable $goExe -WorkingDirectory $sourceTree -Environment $environment -Echo -Arguments @(
        'build'
        '-trimpath'
        '-buildvcs=false'
        '-buildmode=pie'
        "-tags=$($build.tags -join ',')"
        "-ldflags=$ldflags"
        '-o'
        $binary
        '.'
    )
    if ($goBuild.ExitCode -ne 0) { throw "go build failed for $($entry.androidAbi) with exit code $($goBuild.ExitCode)." }

    $verdict = Test-OpenListAndroidElf -Path $binary -ExpectedMachine $entry.expectedElfMachine -Require16KbPages
    if ($verdict.Problems.Count -gt 0) {
        throw ("The $($entry.androidAbi) build is not an Android runtime: " + ($verdict.Problems -join '; ') + '.')
    }
    foreach ($warning in $verdict.Warnings) { Write-Warning "$($entry.androidAbi): $warning" }

    # go-sqlite3 is the whole point of the source build: without it the payload keeps the raw-lstat
    # driver and the app seccomp filter kills admin initialization again.
    $buildInfo = Invoke-OpenListProcess -Executable $goExe -Arguments @('version', '-m', $binary)
    if ($buildInfo.ExitCode -ne 0) { throw "go version -m failed for $binary (exit $($buildInfo.ExitCode))." }
    if ($buildInfo.StandardOutput -notmatch 'github\.com/mattn/go-sqlite3') {
        throw "The $($entry.androidAbi) payload has no github.com/mattn/go-sqlite3 dependency; the pure-Go SQLite driver (raw lstat on Android) would still be linked."
    }
    if ($buildInfo.StandardOutput -match 'modernc\.org/') {
        throw "The $($entry.androidAbi) payload still depends on modernc.org/*; the raw-lstat SQLite driver is still linked."
    }

    # Mirror the official build: strip with the same NDK's llvm-strip after the Go link.
    $strip = Join-Path $ndk.Bin 'llvm-strip'
    if (Test-Path -LiteralPath $strip -PathType Leaf) {
        $stripRun = Invoke-OpenListProcess -Executable $strip -Arguments @($binary)
        if ($stripRun.ExitCode -ne 0) { throw "llvm-strip failed for $binary (exit $($stripRun.ExitCode))." }
    }
    else {
        Write-Warning "llvm-strip is missing from $($ndk.Bin); the payload keeps its Go symbol tables."
    }

    $final = Test-OpenListAndroidElf -Path $binary -ExpectedMachine $entry.expectedElfMachine -Require16KbPages
    if ($final.Problems.Count -gt 0) {
        throw ("The stripped $($entry.androidAbi) build is not an Android runtime: " + ($final.Problems -join '; ') + '.')
    }
    $built.Add([pscustomobject]@{ Entry = $entry; Binary = $binary; Facts = $final.Facts })
}

# Only now, with every requested ABI built and verified, is the staging tree touched.
foreach ($item in $built) {
    $entry = $item.Entry
    $library = Install-OpenListStagedLibrary -Source $item.Binary -OutputDirectory $output `
        -AndroidAbi $entry.androidAbi -LibraryFileName $manifest.android.libraryFileName
    $facts = $item.Facts
    Write-Host ("    staged  lib/{0}/{1}  ({2} {3}, {4:N1} MB, PT_LOAD {5})" -f $entry.androidAbi,
        $manifest.android.libraryFileName, $facts.Machine, $facts.Type, ($facts.Size / 1MB), $facts.MinLoadAlignment) -ForegroundColor Green
    $stagedEntries.Add([ordered]@{
            runtimeIdentifier = $entry.runtimeIdentifier
            androidAbi        = $entry.androidAbi
            apkEntry          = "lib/$($entry.androidAbi)/$($manifest.android.libraryFileName)"
            goArch            = $entry.goArch
            ndkTriple         = $entry.ndkTriple
            elf               = "$($facts.Class) $($facts.Endian)-endian $($facts.Machine) $($facts.Type)"
            minLoadAlignment  = $facts.MinLoadAlignment
            supports16KbPages = $facts.Supports16KbPages
            sqliteDriver      = $build.sqliteDriver
        })
}

$stageManifest = [ordered]@{
    schemaVersion   = 2
    version         = $manifest.version
    origin          = 'source-build'
    source          = [ordered]@{
        url    = $build.sourceUrl
        tag    = $manifest.version
        commit = $build.gitCommit
    }
    build           = [ordered]@{
        goToolchain       = $toolchainSetting
        goToolchainLocal  = $localGoVersion.ToString()
        ndkRevision       = $ndk.Revision
        ndkRoot           = $ndk.Root
        tags              = @($build.tags)
        sqliteDriver      = $build.sqliteDriver
        frontendTag       = $build.frontend.tag
        loaderPageSize    = '0x{0:x}' -f $pageSize
        builtAt           = $build.builtAt
    }
    runtimeVariable = $manifest.runtimeVariable
    libraryFileName = $manifest.android.libraryFileName
    abis            = @($stagedEntries)
}
$stageManifestPath = Write-OpenListStageManifest -OutputDirectory $output -StageManifest $stageManifest
Write-Host "    stage manifest: $stageManifestPath" -ForegroundColor DarkGray

Write-Host ''
Write-Host 'Standard Android packaging path (already wired in MyPowerTools.Android.csproj):' -ForegroundColor Cyan
Write-Host "  lib/arm64-v8a/$($manifest.android.libraryFileName)  ->  AndroidNativeLibrary Abi=arm64-v8a"
Write-Host "  lib/x86_64/$($manifest.android.libraryFileName)     ->  AndroidNativeLibrary Abi=x86_64"
Write-Host ''
Write-Host "OpenListRuntime resolves the extracted copy from nativeLibraryDir/$($manifest.android.libraryFileName) at runtime." -ForegroundColor DarkGray
