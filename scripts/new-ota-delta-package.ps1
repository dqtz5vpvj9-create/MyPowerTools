[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [Parameter(Mandatory = $true)][string]$SourceManifestPath,
    [Parameter(Mandatory = $true)][string]$TargetManifestPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [switch]$NoDelete,
    [string[]]$ProtectedPath = @('install.manifest.json')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function ConvertTo-SafeRelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $normalized = $Path.Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($normalized) -or
        $normalized.StartsWith('/') -or
        [IO.Path]::IsPathRooted($normalized)) {
        throw "OTA manifest contains an unsafe path: $Path"
    }
    $segments = @($normalized.Split('/') | Where-Object { $_.Length -gt 0 })
    if ($segments.Count -eq 0 -or $segments.Count -ne $normalized.Split('/').Count -or
        ($segments | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) {
        throw "OTA manifest contains an unsafe path: $Path"
    }
    return $segments -join '/'
}

function Resolve-PathInsideRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $candidate = [IO.Path]::GetFullPath((Join-Path $rootFull $RelativePath.Replace('/', '\')))
    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "OTA path escaped its root: $RelativePath"
    }
    return $candidate
}

function Read-FileManifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "OTA file manifest does not exist: $fullPath"
    }
    $manifest = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1 -or
        [string]$manifest.kind -ne 'mypowertools-ota-file-manifest') {
        throw "Unsupported OTA file manifest: $fullPath"
    }

    $fileMap = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($record in @($manifest.files)) {
        $relativePath = ConvertTo-SafeRelativePath -Path ([string]$record.path)
        $sha256 = ([string]$record.sha256).ToLowerInvariant()
        $length = [long]$record.length
        if ($sha256 -notmatch '^[0-9a-f]{64}$' -or $length -lt 0) {
            throw "OTA manifest has invalid metadata for $relativePath"
        }
        if ($fileMap.ContainsKey($relativePath)) {
            throw "OTA manifest contains a duplicate path: $relativePath"
        }
        $fileMap.Add($relativePath, [pscustomobject]@{
            path = $relativePath
            length = $length
            sha256 = $sha256
        })
    }

    return [pscustomobject]@{
        FullPath = $fullPath
        Manifest = $manifest
        FileMap = $fileMap
        Sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$sourceRootFull = [IO.Path]::GetFullPath($SourceRoot)
if (-not (Test-Path -LiteralPath $sourceRootFull -PathType Container)) {
    throw "OTA source root does not exist: $sourceRootFull"
}
$source = Read-FileManifest -Path $SourceManifestPath
$target = Read-FileManifest -Path $TargetManifestPath
if (-not [string]::Equals(
        [string]$source.Manifest.product,
        [string]$target.Manifest.product,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Source and target OTA manifests describe different products.'
}

$protected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($path in $ProtectedPath) {
    [void]$protected.Add((ConvertTo-SafeRelativePath -Path $path))
}

$copyRecords = [Collections.Generic.List[object]]::new()
$deleteRecords = [Collections.Generic.List[object]]::new()
$unchangedCount = 0
foreach ($sourceRecord in $source.FileMap.Values | Sort-Object path) {
    $targetRecord = $null
    $targetExists = $target.FileMap.TryGetValue([string]$sourceRecord.path, [ref]$targetRecord)
    if ($targetExists -and
        [long]$targetRecord.length -eq [long]$sourceRecord.length -and
        [string]$targetRecord.sha256 -eq [string]$sourceRecord.sha256) {
        $unchangedCount++
        continue
    }

    $copyRecords.Add([ordered]@{
        operation = if ($targetExists) { 'replace' } else { 'add' }
        path = [string]$sourceRecord.path
        length = [long]$sourceRecord.length
        sha256 = [string]$sourceRecord.sha256
        targetLength = if ($targetExists) { [long]$targetRecord.length } else { $null }
        targetSha256 = if ($targetExists) { [string]$targetRecord.sha256 } else { $null }
    })
}

if (-not $NoDelete) {
    foreach ($targetRecord in $target.FileMap.Values | Sort-Object path) {
        if ($source.FileMap.ContainsKey([string]$targetRecord.path) -or
            $protected.Contains([string]$targetRecord.path)) {
            continue
        }
        $deleteRecords.Add([ordered]@{
            path = [string]$targetRecord.path
            targetLength = [long]$targetRecord.length
            targetSha256 = [string]$targetRecord.sha256
        })
    }
}

$transactionId = [Guid]::NewGuid().ToString('N')
$stagingRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "mypowertools-ota-pack-$transactionId"))
$payloadRoot = Join-Path $stagingRoot 'payload'
$outputFull = [IO.Path]::GetFullPath($OutputPath)
$outputParent = Split-Path -Parent $outputFull
if ([string]::IsNullOrWhiteSpace($outputParent)) {
    throw "OTA package output must have a parent directory: $outputFull"
}

try {
    New-Item -ItemType Directory -Path $payloadRoot, $outputParent -Force | Out-Null
    foreach ($record in $copyRecords) {
        $sourcePath = Resolve-PathInsideRoot -Root $sourceRootFull -RelativePath ([string]$record.path)
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "OTA source file is missing: $($record.path)"
        }
        $sourceItem = Get-Item -LiteralPath $sourcePath -Force
        if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "OTA source file cannot be a reparse point: $($record.path)"
        }
        $actualHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([long]$sourceItem.Length -ne [long]$record.length -or $actualHash -ne [string]$record.sha256) {
            throw "OTA source changed after its manifest was generated: $($record.path)"
        }

        $payloadPath = Resolve-PathInsideRoot -Root $payloadRoot -RelativePath ([string]$record.path)
        New-Item -ItemType Directory -Path (Split-Path -Parent $payloadPath) -Force | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $payloadPath -Force
    }

    $changedBytes = if ($copyRecords.Count -eq 0) {
        0L
    }
    else {
        [long](($copyRecords | Measure-Object -Property length -Sum).Sum)
    }
    $plan = [ordered]@{
        schemaVersion = 1
        kind = 'mypowertools-ota-delta-plan'
        product = [string]$source.Manifest.product
        sourceVersion = [string]$source.Manifest.version
        targetVersion = [string]$target.Manifest.version
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sourceManifestSha256 = $source.Sha256
        targetManifestSha256 = $target.Sha256
        unchangedCount = $unchangedCount
        copyCount = $copyRecords.Count
        deleteCount = $deleteRecords.Count
        changedBytes = $changedBytes
        copy = $copyRecords.ToArray()
        delete = $deleteRecords.ToArray()
    }
    [IO.File]::WriteAllText(
        (Join-Path $stagingRoot 'ota-plan.json'),
        ($plan | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $source.FullPath -Destination (Join-Path $stagingRoot 'source-manifest.json') -Force

    if (Test-Path -LiteralPath $outputFull -PathType Leaf) {
        Remove-Item -LiteralPath $outputFull -Force
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $stagingRoot,
        $outputFull,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)
    $packageHash = (Get-FileHash -LiteralPath $outputFull -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashMarkerPath = "$outputFull.sha256"
    [IO.File]::WriteAllText(
        $hashMarkerPath,
        "$packageHash  $([IO.Path]::GetFileName($outputFull))`n",
        [Text.UTF8Encoding]::new($false))

    [pscustomobject]@{
        PackagePath = $outputFull
        PackageSha256 = $packageHash
        HashMarkerPath = $hashMarkerPath
        SourceManifestSha256 = $source.Sha256
        TargetManifestSha256 = $target.Sha256
        CopyCount = $copyRecords.Count
        DeleteCount = $deleteRecords.Count
        UnchangedCount = $unchangedCount
        ChangedBytes = $changedBytes
        PackageBytes = (Get-Item -LiteralPath $outputFull).Length
    } | ConvertTo-Json -Depth 4
}
finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($stagingRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($stagingRoot) -eq "mypowertools-ota-pack-$transactionId" -and
        (Test-Path -LiteralPath $stagingRoot -PathType Container)) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
