param(
    [string]$Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\MyPowerTools.svg'),
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\MyPowerTools.ico')
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sourceFull = [System.IO.Path]::GetFullPath($Source)
$destinationFull = [System.IO.Path]::GetFullPath($Destination)
if (-not $sourceFull.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $destinationFull.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Icon source and destination must remain inside the repository.'
}

$inkscape = (Get-Command 'inkscape.com' -ErrorAction Stop).Source
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$buildRoot = Join-Path $repoRoot 'artifacts\icon-build'
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

$images = [System.Collections.Generic.List[object]]::new()
foreach ($size in $sizes) {
    $pngPath = Join-Path $buildRoot "MyPowerTools-$size.png"
    $inkscapeArguments = @(
        $sourceFull,
        '--export-type=png',
        "--export-filename=$pngPath",
        "--export-width=$size",
        "--export-height=$size"
    )
    & $inkscape @inkscapeArguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Inkscape failed for ${size}px with exit code $exitCode."
    }

    $images.Add([pscustomobject]@{
        Size = $size
        Data = [System.IO.File]::ReadAllBytes($pngPath)
    })
}

$destinationDirectory = Split-Path -Parent $destinationFull
New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
$stream = [System.IO.FileStream]::new(
    $destinationFull,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Data.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Data)
    }
} finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Output $destinationFull
