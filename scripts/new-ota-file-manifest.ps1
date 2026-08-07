[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$Product = 'MyPowerTools',
    [string]$Version = '',
    [string[]]$Exclude = @(
        'install.manifest.json',
        'MyPowerTools-win-x64.manifest.json',
        'ota-state/*',
        '*/__pycache__/*',
        '*.pyc'
    )
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Test-ExcludedPath {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [string[]]$Patterns = @()
    )

    foreach ($pattern in $Patterns) {
        if ($RelativePath -like $pattern) {
            return $true
        }
    }
    return $false
}

$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) {
    throw "OTA manifest root does not exist: $rootFull"
}

$outputFull = [IO.Path]::GetFullPath($OutputPath)
$rootPrefix = $rootFull + [IO.Path]::DirectorySeparatorChar
$records = [Collections.Generic.List[object]]::new()

foreach ($file in Get-ChildItem -LiteralPath $rootFull -File -Recurse -Force | Sort-Object FullName) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        continue
    }
    if ($file.FullName.Equals($outputFull, [StringComparison]::OrdinalIgnoreCase)) {
        continue
    }
    if (-not $file.FullName.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "OTA manifest enumeration escaped its root: $($file.FullName)"
    }

    $relativePath = $file.FullName.Substring($rootPrefix.Length).Replace('\', '/')
    if (Test-ExcludedPath -RelativePath $relativePath -Patterns $Exclude) {
        continue
    }

    $records.Add([pscustomobject]@{
        path = $relativePath
        length = [long]$file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}

$totalBytes = if ($records.Count -eq 0) {
    0L
}
else {
    [long](($records | Measure-Object -Property length -Sum).Sum)
}
$manifest = [ordered]@{
    schemaVersion = 1
    kind = 'mypowertools-ota-file-manifest'
    product = $Product
    version = $Version
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    fileCount = $records.Count
    totalBytes = $totalBytes
    files = $records.ToArray()
}

$outputParent = Split-Path -Parent $outputFull
if (-not [string]::IsNullOrWhiteSpace($outputParent)) {
    New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
}
$json = $manifest | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($outputFull, $json, [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    ManifestPath = $outputFull
    ManifestSha256 = (Get-FileHash -LiteralPath $outputFull -Algorithm SHA256).Hash.ToLowerInvariant()
    FileCount = $records.Count
    TotalBytes = $totalBytes
} | ConvertTo-Json -Depth 3
