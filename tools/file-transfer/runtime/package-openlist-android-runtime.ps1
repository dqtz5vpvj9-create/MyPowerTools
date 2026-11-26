<#
.SYNOPSIS
Stages the pinned OpenList Android runtime in the APK-embeddable layout.

.DESCRIPTION
Android 10 (API 29) forbids executing files from an app's data directory, so the embedded runtime
cannot be downloaded on the device: it must arrive inside the APK as lib/<abi>/libopenlist.so and be
extracted by Android into the app's native library directory. This script produces that layout at:

  lib/arm64-v8a/libopenlist.so   (android-arm64)
  lib/x86_64/libopenlist.so      (android-x64)

Two payload sources exist; the default is the one that works under the Android app sandbox:

  * Source (default) - builds the pinned OpenList tag from source through
    build-openlist-android-runtime.ps1, using the project's sqlite_cgo_compat tag so SQLite is the
    NDK/Bionic CGO driver instead of the pure-Go driver whose raw lstat syscall the app seccomp
    filter rejects. This is what the APK must ship.

  * OfficialRelease - downloads the official pinned release asset and stages it after structural
    validation. The official v4.2.6 Android payload is kept only for comparison: under a real app
    UID its `admin random` initialization dies with SIGSYS (blocked lstat/0x6 on x86_64), because
    the official build does not enable sqlite_cgo_compat.

Both paths validate the payload structurally (64-bit little-endian PIE, expected ABI, links
/system/bin/linker64, no glibc/musl loader) and never add a checksum scheme: integrity stays on the
pinned version plus the official HTTPS origins, the same trust boundary the desktop download path
already uses.

.PARAMETER Abi
Which Android ABIs to stage. 'all' stages both the phone (arm64) and emulator (x64) runtime.

.PARAMETER Source
Payload source. 'Source' (default) builds from the pinned OpenList tag; 'OfficialRelease' uses the
official release asset, which is not compatible with the Android app seccomp filter.

.PARAMETER CacheDirectory
Where downloads and extraction staging live. Defaults to the shared /mnt/cache/data-cache location
when it is writable, otherwise to the repository scratch class artifacts/.tmp-* that
scripts/artifacts-policy.json already governs.

.PARAMETER OutputDirectory
Stage root. Defaults to runtime/stage next to this script; the staged tree is git-ignored.

.PARAMETER NdkRoot
Forwarded to the source build: explicit Android NDK root.

.PARAMETER GoProxy
Forwarded to the source build: GOPROXY for module and toolchain downloads.

.PARAMETER GoToolchain
Forwarded to the source build: GOTOOLCHAIN value ('local' forces the host toolchain).

.PARAMETER DownloadNdk
Forwarded to the source build: fetch the pinned NDK archive when no matching installation exists.

.PARAMETER Force
Re-download and rebuild/re-stage even when cached copies exist.

.EXAMPLE
pwsh -NoLogo -NoProfile -File tools/file-transfer/runtime/package-openlist-android-runtime.ps1 -Abi all

.EXAMPLE
# Diagnostic only: stages the known-broken official asset to reproduce the SIGSYS behaviour.
pwsh -NoLogo -NoProfile -File tools/file-transfer/runtime/package-openlist-android-runtime.ps1 -Abi x64 -Source OfficialRelease -AllowUnsafePayload -OutputDirectory /mnt/cache/data-cache/openlist-official-stage
#>
[CmdletBinding()]
param(
    [ValidateSet('arm64', 'x64', 'all')]
    [string]$Abi = 'all',

    [ValidateSet('Source', 'OfficialRelease')]
    [string]$Source = 'Source',

    [string]$CacheDirectory = '',

    [string]$OutputDirectory = '',

    [string]$NdkRoot = '',

    [string]$GoProxy = '',

    [string]$GoToolchain = '',

    [switch]$DownloadNdk,

    # OfficialRelease only: stage the official asset even though validation proves it links the
    # raw-lstat SQLite shim that the app seccomp filter rejects. For reproducing the SIGSYS evidence
    # and for comparing payloads; never a shipping configuration.
    [switch]$AllowUnsafePayload,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Source -eq 'Source') {
    $buildScript = Join-Path $PSScriptRoot 'build-openlist-android-runtime.ps1'
    $forward = @{
        Abi            = $Abi
        CacheDirectory = $CacheDirectory
        OutputDirectory = $OutputDirectory
        NdkRoot        = $NdkRoot
        GoProxy        = $GoProxy
        GoToolchain    = $GoToolchain
        DownloadNdk    = $DownloadNdk
        Force          = $Force
    }
    # The delegated script throws on failure; reaching the next line means it staged successfully.
    & $buildScript @forward
    exit 0
}

Import-Module (Join-Path $PSScriptRoot 'OpenListRuntimePackage.psm1') -Force

function Resolve-OpenListCacheDirectory {
    param([string]$Requested, [string]$RepositoryRoot)

    return Resolve-OpenListTempRoot -Requested $Requested -RepositoryRoot $RepositoryRoot -Leaf 'cache'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$manifest = Get-OpenListRuntimeManifest -RuntimeRoot $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'stage' }
$cache = Resolve-OpenListCacheDirectory -Requested $CacheDirectory -RepositoryRoot $repoRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $cache -Force | Out-Null

$selected = @($manifest.android.abis | Where-Object { $Abi -eq 'all' -or $_.runtimeIdentifier -eq "android-$Abi" })
if ($selected.Count -eq 0) { throw "No Android ABI matches -Abi $Abi in the runtime manifest." }

Write-Warning ('The official-release payload does not enable sqlite_cgo_compat, so its pure-Go SQLite ' +
    'driver issues the raw lstat syscall that the Android app seccomp filter blocks. Build from source ' +
    '(the default) for any APK that must run under a real app UID; validation therefore refuses to stage ' +
    'it unless -AllowUnsafePayload is passed for diagnostics.')
Write-Host "==> OpenList $($manifest.version) Android runtime (official release asset)" -ForegroundColor Cyan
Write-Host "    source : $($manifest.releaseDownloadBase)/$($manifest.version)" -ForegroundColor DarkGray
Write-Host "    cache  : $cache" -ForegroundColor DarkGray

$staged = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $selected) {
    $asset = $entry.asset
    $archive = Join-Path $cache $asset
    if ($Force -or -not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        $url = Get-OpenListReleaseUrl -Manifest $manifest -Asset $asset
        Write-Host "  > download $asset" -ForegroundColor DarkGray
        Invoke-WebRequest -Uri $url -OutFile "$archive.partial"
        Move-Item -LiteralPath "$archive.partial" -Destination $archive -Force
    }

    $extract = Join-Path $cache "extract-$($entry.androidAbi)"
    if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    & tar -xzf $archive -C $extract
    if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $archive (exit $LASTEXITCODE)." }
    $binary = Join-Path $extract $entry.archiveEntry
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
        throw "$asset does not contain $($entry.archiveEntry)."
    }

    $verdict = Test-OpenListAndroidElf -Path $binary -ExpectedMachine $entry.expectedElfMachine
    if ($AllowUnsafePayload) {
        # Diagnostic escape hatch: the seccomp finding is exactly what makes the official asset
        # unusable, so it stays visible as a warning instead of a hard failure.
        foreach ($finding in @($verdict.Problems | Where-Object { $_ -like '*modernc.org/libc*' })) {
            Write-Warning "$($entry.androidAbi): $finding"
        }
        $verdict.Problems = @($verdict.Problems | Where-Object { $_ -notlike '*modernc.org/libc*' })
    }
    if ($verdict.Problems.Count -gt 0) {
        throw ("$asset is not an Android $($entry.androidAbi) runtime: " + ($verdict.Problems -join '; ') +
            '. Refusing to stage a Linux ELF that Android cannot execute.')
    }
    foreach ($warning in $verdict.Warnings) { Write-Warning "$($entry.androidAbi): $warning" }

    $library = Install-OpenListStagedLibrary -Source $binary -OutputDirectory $output `
        -AndroidAbi $entry.androidAbi -LibraryFileName $manifest.android.libraryFileName

    $facts = $verdict.Facts
    Write-Host ("    staged  lib/{0}/{1}  ({2} {3}, {4:N1} MB, PT_LOAD {5})" -f $entry.androidAbi,
        $manifest.android.libraryFileName, $facts.Machine, $facts.Type, ($facts.Size / 1MB), $facts.MinLoadAlignment) -ForegroundColor Green
    $staged.Add([ordered]@{
            runtimeIdentifier = $entry.runtimeIdentifier
            androidAbi        = $entry.androidAbi
            apkEntry          = "lib/$($entry.androidAbi)/$($manifest.android.libraryFileName)"
            asset             = $asset
            ndkTriple         = $entry.ndkTriple
            elf               = "$($facts.Class) $($facts.Endian)-endian $($facts.Machine) $($facts.Type)"
            minLoadAlignment  = $facts.MinLoadAlignment
            supports16KbPages = $facts.Supports16KbPages
        })
}

$stageManifest = [ordered]@{
    schemaVersion   = 2
    version         = $manifest.version
    origin          = 'official-release'
    source          = [ordered]@{
        url = "$($manifest.releaseDownloadBase)/$($manifest.version)"
        tag = $manifest.version
    }
    runtimeVariable = $manifest.runtimeVariable
    libraryFileName = $manifest.android.libraryFileName
    abis            = @($staged)
}
$stageManifestPath = Write-OpenListStageManifest -OutputDirectory $output -StageManifest $stageManifest
Write-Host "    stage manifest: $stageManifestPath" -ForegroundColor DarkGray

Write-Host ''
Write-Host 'Standard Android packaging path (already wired in MyPowerTools.Android.csproj):' -ForegroundColor Cyan
Write-Host "  lib/arm64-v8a/$($manifest.android.libraryFileName)  ->  AndroidNativeLibrary Abi=arm64-v8a"
Write-Host "  lib/x86_64/$($manifest.android.libraryFileName)     ->  AndroidNativeLibrary Abi=x86_64"
Write-Host ''
Write-Host "OpenListRuntime resolves the extracted copy from nativeLibraryDir/$($manifest.android.libraryFileName) at runtime." -ForegroundColor DarkGray
