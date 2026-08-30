[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$outputFull = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) { throw "ZIP source root is missing: $rootFull" }
if ($outputFull.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Release ZIP output must stay outside its source root.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $outputFull) -Force | Out-Null
if (Test-Path -LiteralPath $outputFull) { Remove-Item -LiteralPath $outputFull -Force }

$stream = [IO.File]::Open($outputFull, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    foreach ($file in Get-ChildItem -LiteralPath $rootFull -Recurse -File | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($rootFull, $file.FullName).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $file.FullName,
            $relative,
            [IO.Compression.CompressionLevel]::SmallestSize)
    }
}
finally {
    $archive.Dispose()
    $stream.Dispose()
}

Get-Item -LiteralPath $outputFull
