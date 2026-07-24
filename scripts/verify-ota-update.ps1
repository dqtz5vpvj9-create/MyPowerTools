[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Utf8Text {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Read-JsonOutput {
    param([Parameter(Mandatory = $true)][object[]]$Output)

    return (($Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine) | ConvertFrom-Json
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$scriptsRoot = $PSScriptRoot
$manifestScript = Join-Path $scriptsRoot 'new-ota-file-manifest.ps1'
$packageScript = Join-Path $scriptsRoot 'new-ota-delta-package.ps1'
$applyScript = Join-Path $scriptsRoot 'invoke-ota-update.ps1'
$testId = [Guid]::NewGuid().ToString('N')
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempParent "mypowertools-ota-test-$testId"))

try {
    $sourceRoot = Join-Path $testRoot 'source'
    $targetRoot = Join-Path $testRoot 'target'
    $driftTargetRoot = Join-Path $testRoot 'drift-target'
    $metadataRoot = Join-Path $testRoot 'metadata'
    New-Item -ItemType Directory -Path $sourceRoot, $targetRoot, $driftTargetRoot, $metadataRoot -Force | Out-Null

    Write-Utf8Text -Path (Join-Path $sourceRoot 'same.txt') -Value 'same'
    Write-Utf8Text -Path (Join-Path $sourceRoot 'changed.txt') -Value 'new-value'
    Write-Utf8Text -Path (Join-Path $sourceRoot 'nested\added.txt') -Value 'added'

    foreach ($root in @($targetRoot, $driftTargetRoot)) {
        Write-Utf8Text -Path (Join-Path $root 'same.txt') -Value 'same'
        Write-Utf8Text -Path (Join-Path $root 'changed.txt') -Value 'old-value'
        Write-Utf8Text -Path (Join-Path $root 'removed.txt') -Value 'remove-me'
        Write-Utf8Text -Path (Join-Path $root 'install.manifest.json') -Value '{"preserve":true}'
    }

    $sourceManifestPath = Join-Path $metadataRoot 'source-manifest.json'
    $targetManifestPath = Join-Path $metadataRoot 'target-manifest.json'
    $driftManifestPath = Join-Path $metadataRoot 'drift-target-manifest.json'
    [void](& $manifestScript -Root $sourceRoot -OutputPath $sourceManifestPath -Version '2.0.0')
    [void](& $manifestScript -Root $targetRoot -OutputPath $targetManifestPath -Version '1.0.0')
    [void](& $manifestScript -Root $driftTargetRoot -OutputPath $driftManifestPath -Version '1.0.0')

    $packagePath = Join-Path $metadataRoot 'delta.zip'
    $packageParams = @{
        SourceRoot = $sourceRoot
        SourceManifestPath = $sourceManifestPath
        TargetManifestPath = $targetManifestPath
        OutputPath = $packagePath
    }
    $packageResult = Read-JsonOutput -Output @(& $packageScript @packageParams)
    Assert-True -Condition ($packageResult.CopyCount -eq 2) -Message 'OTA package should copy two files.'
    Assert-True -Condition ($packageResult.DeleteCount -eq 1) -Message 'OTA package should delete one file.'
    Assert-True -Condition ($packageResult.UnchangedCount -eq 1) -Message 'OTA package should retain one unchanged file.'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $payloadEntries = @($archive.Entries |
            Where-Object { $_.FullName.StartsWith('payload/', [StringComparison]::Ordinal) -and -not $_.FullName.EndsWith('/') } |
            ForEach-Object FullName |
            Sort-Object)
    }
    finally {
        $archive.Dispose()
    }
    $payloadAssertParams = @{
        Condition = ($payloadEntries -join ',') -eq 'payload/changed.txt,payload/nested/added.txt'
        Message = "OTA payload contains unexpected files: $($payloadEntries -join ',')"
    }
    Assert-True @payloadAssertParams

    $hashMismatchRejected = $false
    try {
        $badHashApplyParams = @{
            PackagePath = $packagePath
            ExpectedPackageSha256 = ('0' * 64)
            TargetRoot = $targetRoot
            TargetManifestPath = $targetManifestPath
            StateRoot = Join-Path $testRoot 'bad-hash-state'
            ApplyDeletes = $true
        }
        [void](& $applyScript @badHashApplyParams)
    }
    catch {
        $hashMismatchRejected = $_.Exception.Message -like '*package SHA-256 mismatch*'
    }
    Assert-True -Condition $hashMismatchRejected -Message 'OTA apply accepted an unexpected package hash.'

    $unsafeManifestPath = Join-Path $metadataRoot 'unsafe-target-manifest.json'
    $unsafeManifest = Get-Content -LiteralPath $targetManifestPath -Raw | ConvertFrom-Json
    $unsafeManifest.files[0].path = '../escape.txt'
    Write-Utf8Text -Path $unsafeManifestPath -Value ($unsafeManifest | ConvertTo-Json -Depth 10)
    $unsafePathRejected = $false
    try {
        $unsafePackageParams = @{
            SourceRoot = $sourceRoot
            SourceManifestPath = $sourceManifestPath
            TargetManifestPath = $unsafeManifestPath
            OutputPath = Join-Path $metadataRoot 'unsafe-delta.zip'
        }
        [void](& $packageScript @unsafePackageParams)
    }
    catch {
        $unsafePathRejected = $_.Exception.Message -like '*unsafe path*'
    }
    Assert-True -Condition $unsafePathRejected -Message 'OTA packager accepted a traversal path.'

    $applyParams = @{
        PackagePath = $packagePath
        ExpectedPackageSha256 = [string]$packageResult.PackageSha256
        TargetRoot = $targetRoot
        TargetManifestPath = $targetManifestPath
        StateRoot = Join-Path $testRoot 'state'
        ApplyDeletes = $true
    }
    $applyResult = Read-JsonOutput -Output @(& $applyScript @applyParams)
    Assert-True -Condition ([bool]$applyResult.success) -Message 'OTA apply did not report success.'
    Assert-True -Condition ((Get-Content -LiteralPath (Join-Path $targetRoot 'changed.txt') -Raw) -eq 'new-value') -Message 'Changed file was not replaced.'
    Assert-True -Condition ((Get-Content -LiteralPath (Join-Path $targetRoot 'nested\added.txt') -Raw) -eq 'added') -Message 'Added file is missing.'
    Assert-True -Condition (-not (Test-Path -LiteralPath (Join-Path $targetRoot 'removed.txt'))) -Message 'Removed file still exists.'
    Assert-True -Condition ((Get-Content -LiteralPath (Join-Path $targetRoot 'install.manifest.json') -Raw) -eq '{"preserve":true}') -Message 'Protected install manifest changed.'

    $updatedTargetManifestPath = Join-Path $metadataRoot 'updated-target-manifest.json'
    [void](& $manifestScript -Root $targetRoot -OutputPath $updatedTargetManifestPath -Version '2.0.0')
    $emptyPackageParams = @{
        SourceRoot = $sourceRoot
        SourceManifestPath = $sourceManifestPath
        TargetManifestPath = $updatedTargetManifestPath
        OutputPath = Join-Path $metadataRoot 'empty-delta.zip'
    }
    $emptyPackageResult = Read-JsonOutput -Output @(& $packageScript @emptyPackageParams)
    Assert-True -Condition ($emptyPackageResult.CopyCount -eq 0) -Message 'Second OTA package still contains copies.'
    Assert-True -Condition ($emptyPackageResult.DeleteCount -eq 0) -Message 'Second OTA package still contains deletions.'

    $driftPackagePath = Join-Path $metadataRoot 'drift-delta.zip'
    $driftPackageParams = @{
        SourceRoot = $sourceRoot
        SourceManifestPath = $sourceManifestPath
        TargetManifestPath = $driftManifestPath
        OutputPath = $driftPackagePath
    }
    $driftPackageResult = Read-JsonOutput -Output @(& $packageScript @driftPackageParams)
    Write-Utf8Text -Path (Join-Path $driftTargetRoot 'changed.txt') -Value 'locally-mutated'
    $driftRejected = $false
    try {
        $driftApplyParams = @{
            PackagePath = $driftPackagePath
            ExpectedPackageSha256 = [string]$driftPackageResult.PackageSha256
            TargetRoot = $driftTargetRoot
            TargetManifestPath = $driftManifestPath
            StateRoot = Join-Path $testRoot 'drift-state'
            ApplyDeletes = $true
        }
        [void](& $applyScript @driftApplyParams)
    }
    catch {
        $driftRejected = $_.Exception.Message -like '*changed after its manifest was generated*'
    }
    Assert-True -Condition $driftRejected -Message 'OTA apply accepted a drifted target.'
    Assert-True -Condition ((Get-Content -LiteralPath (Join-Path $driftTargetRoot 'changed.txt') -Raw) -eq 'locally-mutated') -Message 'Drifted file was overwritten.'
    Assert-True -Condition (-not (Test-Path -LiteralPath (Join-Path $driftTargetRoot 'nested\added.txt'))) -Message 'OTA wrote files before drift validation finished.'
    Assert-True -Condition (Test-Path -LiteralPath (Join-Path $driftTargetRoot 'removed.txt')) -Message 'OTA deleted files before drift validation finished.'

    [pscustomobject]@{
        Success = $true
        CopyCount = $packageResult.CopyCount
        DeleteCount = $packageResult.DeleteCount
        UnchangedCount = $packageResult.UnchangedCount
        PayloadEntries = $payloadEntries
        Idempotent = $emptyPackageResult.CopyCount -eq 0 -and $emptyPackageResult.DeleteCount -eq 0
        DriftRejected = $driftRejected
        HashMismatchRejected = $hashMismatchRejected
        UnsafePathRejected = $unsafePathRejected
    } | ConvertTo-Json -Depth 5
}
finally {
    $tempPrefix = $tempParent.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($testRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($testRoot) -eq "mypowertools-ota-test-$testId" -and
        (Test-Path -LiteralPath $testRoot -PathType Container)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
