<#
.SYNOPSIS
Checks that a built MyPowerTools APK carries the embedded OpenList Android runtime.

.DESCRIPTION
Static verification of the packaged artifact, no device required:

  * lib/<abi>/libopenlist.so exists for every requested ABI;
  * each payload is a 64-bit little-endian PIE of the right ABI that links /system/bin/linker64
    and carries no glibc/musl loader (i.e. a real Android build, not a Linux ELF);
  * AndroidManifest.xml still extracts native libraries (extractNativeLibs=true), because
    OpenListRuntime executes the copy in the app's native library directory.

The PT_LOAD alignment of the official binary is reported as a warning: the pinned v4.2.6 payloads
are 4 KB aligned and therefore not compatible with 16 KB memory page Android devices.

Without -AllowMissing a missing runtime is an error: that is the state where Android would have to
fall back to a runtime it cannot execute, which is exactly what this integration removes.

.EXAMPLE
pwsh -NoLogo -NoProfile -File tools/file-transfer/runtime/verify-openlist-android-embed.ps1 `
    -Apk artifacts/build/bin/MyPowerTools.Android/debug/com.mypowertools.android-Signed.apk
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Apk,

    [string[]]$Abi = @(),

    [string]$BuildToolsDirectory = '',

    # Extraction scratch. Defaults to the shared cache location when writable, otherwise the
    # governed repository scratch class artifacts/.tmp-*.
    [string]$ScratchDirectory = '',

    [switch]$AllowMissing
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'OpenListRuntimePackage.psm1') -Force

$manifest = Get-OpenListRuntimeManifest -RuntimeRoot $PSScriptRoot
$libraryFileName = $manifest.android.libraryFileName
if (-not $Abi -or $Abi.Count -eq 0) {
    $Abi = @($manifest.android.abis | ForEach-Object { $_.androidAbi })
}
if (-not (Test-Path -LiteralPath $Apk -PathType Leaf)) { throw "APK not found: $Apk" }

$expectedMachine = @{}
foreach ($entry in $manifest.android.abis) { $expectedMachine[$entry.androidAbi] = $entry.expectedElfMachine }

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$scratchRoot = Resolve-OpenListTempRoot -Requested $ScratchDirectory -RepositoryRoot $repoRoot -Leaf 'tmp'
$scratch = Join-Path $scratchRoot ('apk-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$failures = [System.Collections.Generic.List[string]]::new()
$missing = [System.Collections.Generic.List[string]]::new()

Write-Host "==> OpenList $($manifest.version) embedded runtime in $([IO.Path]::GetFileName($Apk))" -ForegroundColor Cyan
try {
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Apk))
    try {
        foreach ($androidAbi in $Abi) {
            $entryName = "lib/$androidAbi/$libraryFileName"
            $entry = $zip.Entries | Where-Object { $_.FullName -ceq $entryName } | Select-Object -First 1
            if (-not $entry) {
                $missing.Add($entryName)
                Write-Host "    MISSING  $entryName" -ForegroundColor Yellow
                continue
            }

            $extracted = Join-Path $scratch "$androidAbi-$libraryFileName"
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $extracted, $true)
            $verdict = Test-OpenListAndroidElf -Path $extracted -ExpectedMachine $expectedMachine[$androidAbi]
            if ($verdict.Problems.Count -gt 0) {
                $failures.Add("$entryName is not an Android $androidAbi runtime: " + ($verdict.Problems -join '; '))
                Write-Host "    INVALID  $entryName" -ForegroundColor Red
                continue
            }
            foreach ($warning in $verdict.Warnings) { Write-Warning "$entryName : $warning" }

            Write-Host ("    ok       {0}  ({1} {2}, {3:N1} MB, PT_LOAD {4})" -f $entryName, $verdict.Facts.Machine,
                $verdict.Facts.Type, ($verdict.Facts.Size / 1MB), $verdict.Facts.MaxLoadAlignment) -ForegroundColor Green
        }
    }
    finally { $zip.Dispose() }

    $extractNativeLibs = Get-OpenListApkExtractNativeLibs -Apk $Apk -BuildToolsDirectory $BuildToolsDirectory
    if ($null -eq $extractNativeLibs) {
        Write-Host '    ?        extractNativeLibs unverified (no aapt2 found; pass -BuildToolsDirectory)' -ForegroundColor Yellow
    }
    elseif (-not $extractNativeLibs) {
        $failures.Add('AndroidManifest.xml has extractNativeLibs=false: the APK libraries are not extracted to nativeLibraryDir, so OpenListRuntime cannot execute the embedded runtime.')
        Write-Host '    INVALID  extractNativeLibs=false' -ForegroundColor Red
    }
    else {
        Write-Host '    ok       extractNativeLibs=true' -ForegroundColor Green
    }
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}

if ($missing.Count -gt 0) {
    $message = @(
        "The APK does not embed the OpenList Android runtime: $($missing -join ', ').",
        'The Android host project packages it with an AndroidNativeLibrary item for each ABI.',
        'Run package-openlist-android-runtime.ps1 to produce the staged tree, then rebuild the APK.'
    ) -join [Environment]::NewLine
    if ($AllowMissing) { Write-Warning $message } else { $failures.Add($message) }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host "FAIL  $failure" -ForegroundColor Red }
    exit 1
}

if ($missing.Count -gt 0) {
    Write-Host "OpenList runtime is not embedded yet ($($missing -join ', ')); -AllowMissing was requested." -ForegroundColor Yellow
}
else {
    Write-Host 'Embedded OpenList Android runtime verified.' -ForegroundColor Green
}
