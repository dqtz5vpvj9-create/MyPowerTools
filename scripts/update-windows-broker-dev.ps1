[CmdletBinding()]
param(
    [string] $PublishedBroker = (
        [IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot '..\artifacts\broker-file-handle-test\MyPowerTools.ElevatedBroker.exe')
        )
    ),
    [string] $InstallRoot = (
        [IO.Path]::GetFullPath(
            (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools')
        )
    )
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = [IO.Path]::GetFullPath($PublishedBroker)
$installRootFull = [IO.Path]::GetFullPath($InstallRoot)
$targetDirectory = [IO.Path]::GetFullPath(
    (Join-Path $installRootFull 'Broker')
)
$target = [IO.Path]::GetFullPath(
    (Join-Path $targetDirectory 'MyPowerTools.ElevatedBroker.exe')
)

if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Published Broker is missing: $source"
}
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
    throw "Installed Broker is missing: $target"
}
if (-not [string]::Equals(
        [IO.Path]::GetDirectoryName($target),
        $targetDirectory,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installed Broker target escaped its verified directory.'
}

$running = @(Get-Process -Name 'MyPowerTools.ElevatedBroker' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw "Elevated Broker is currently running: $($running.Id -join ', ')"
}

$transactionId = [Guid]::NewGuid().ToString('N')
$staging = Join-Path $targetDirectory (
    ".MyPowerTools.ElevatedBroker.$transactionId.staging.exe"
)
$backup = Join-Path $targetDirectory (
    "MyPowerTools.ElevatedBroker.$transactionId.previous.exe"
)

try {
    [IO.File]::Copy($source, $staging, $false)
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $stagingHash = (Get-FileHash -LiteralPath $staging -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            $sourceHash,
            $stagingHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Staged Broker hash does not match the published Broker.'
    }

    [IO.File]::Replace($staging, $target, $backup, $true)
    $installedHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            $sourceHash,
            $installedHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installed Broker hash verification failed.'
    }

    [pscustomobject]@{
        State = 'ready'
        Target = $target
        Sha256 = $installedHash.ToLowerInvariant()
        Backup = $backup
        BackupSha256 = (
            Get-FileHash -LiteralPath $backup -Algorithm SHA256
        ).Hash.ToLowerInvariant()
    }
}
finally {
    if (Test-Path -LiteralPath $staging -PathType Leaf) {
        Remove-Item -LiteralPath $staging -Force -ErrorAction SilentlyContinue
    }
}
