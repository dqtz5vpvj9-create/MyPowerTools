<#
.SYNOPSIS
Downloads the pinned official OpenList Android runtime and stages it in the APK-embeddable layout.

.DESCRIPTION
Android 10 (API 29) forbids executing files from an app's data directory, so the embedded runtime
cannot be downloaded on the device: it must arrive inside the APK as lib/<abi>/libopenlist.so and be
extracted by Android into the app's native library directory. This script produces exactly that
layout from the official OpenList release:

  lib/arm64-v8a/libopenlist.so   (android-arm64)
  lib/x86_64/libopenlist.so      (android-x64)

Sources and guard rails:
  * The version and asset names come from openlist-runtime.json; only the official GitHub release
    of that exact tag is used, over HTTPS. No checksum scheme is added on top: the project keeps the
    same trust boundary as the desktop download path (pinned version + official origin).
  * The payload must be a 64-bit little-endian PIE for the expected ABI that links
    /system/bin/linker64 and carries no glibc/musl loader, i.e. a real Android (Bionic) build and
    not a Linux ELF that Android could never execute.
  * The PT_LOAD alignment is recorded and reported. The official v4.2.6 binaries are 4 KB aligned,
    so they are not compatible with 16 KB memory page Android devices; the script warns instead of
    pretending otherwise.

.PARAMETER Abi
Which Android ABIs to stage. 'all' stages both the phone (arm64) and emulator (x64) runtime.

.PARAMETER CacheDirectory
Where downloads and extraction staging live. Defaults to the shared /mnt/cache/data-cache location
when it is writable, otherwise to the repository scratch class artifacts/.tmp-* that
scripts/artifacts-policy.json already governs.

.PARAMETER OutputDirectory
Stage root. Defaults to runtime/stage next to this script; the staged tree is git-ignored.

.PARAMETER Force
Re-download and re-stage even when the cached archive already exists.

.EXAMPLE
pwsh -NoLogo -NoProfile -File tools/file-transfer/runtime/package-openlist-android-runtime.ps1 -Abi all
#>
[CmdletBinding()]
param(
    [ValidateSet('arm64', 'x64', 'all')]
    [string]$Abi = 'all',

    [string]$CacheDirectory = '',

    [string]$OutputDirectory = '',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
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

Write-Host "==> OpenList $($manifest.version) Android runtime" -ForegroundColor Cyan
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
    if ($verdict.Problems.Count -gt 0) {
        throw ("$asset is not an Android $($entry.androidAbi) runtime: " + ($verdict.Problems -join '; ') +
            '. Refusing to stage a Linux ELF that Android cannot execute.')
    }
    foreach ($warning in $verdict.Warnings) { Write-Warning "$($entry.androidAbi): $warning" }

    $library = Join-Path $output "lib/$($entry.androidAbi)/$($manifest.android.libraryFileName)"
    New-Item -ItemType Directory -Path (Split-Path -Parent $library) -Force | Out-Null
    Copy-Item -LiteralPath $binary -Destination $library -Force
    if (-not $IsWindows) {
        try { [IO.File]::SetUnixFileMode($library, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) } catch { }
    }

    $facts = $verdict.Facts
    Write-Host ("    staged  lib/{0}/{1}  ({2} {3}, {4:N1} MB, PT_LOAD {5})" -f $entry.androidAbi, $manifest.android.libraryFileName,
        $facts.Machine, $facts.Type, ($facts.Size / 1MB), $facts.MaxLoadAlignment) -ForegroundColor Green
    $staged.Add([ordered]@{
            runtimeIdentifier = $entry.runtimeIdentifier
            androidAbi        = $entry.androidAbi
            apkEntry          = "lib/$($entry.androidAbi)/$($manifest.android.libraryFileName)"
            asset             = $asset
            ndkTriple         = $entry.ndkTriple
            elf               = "$($facts.Class) $($facts.Endian)-endian $($facts.Machine) $($facts.Type)"
            maxLoadAlignment  = $facts.MaxLoadAlignment
            supports16KbPages = $facts.Supports16KbPages
        })
}

$stageManifest = [ordered]@{
    schemaVersion   = 1
    version         = $manifest.version
    source          = "$($manifest.releaseDownloadBase)/$($manifest.version)"
    runtimeVariable = $manifest.runtimeVariable
    libraryFileName = $manifest.android.libraryFileName
    abis            = @($staged)
}
$stageManifestPath = Join-Path $output 'openlist-runtime-stage.json'
$stageManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $stageManifestPath -Encoding utf8
Write-Host "    stage manifest: $stageManifestPath" -ForegroundColor DarkGray

Write-Host ''
Write-Host 'Standard Android packaging path (already wired in MyPowerTools.Android.csproj):' -ForegroundColor Cyan
Write-Host "  lib/arm64-v8a/$($manifest.android.libraryFileName)  ->  AndroidNativeLibrary Abi=arm64-v8a"
Write-Host "  lib/x86_64/$($manifest.android.libraryFileName)     ->  AndroidNativeLibrary Abi=x86_64"
Write-Host ''
Write-Host "OpenListRuntime resolves the extracted copy from nativeLibraryDir/$($manifest.android.libraryFileName) at runtime." -ForegroundColor DarkGray
