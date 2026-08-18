[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RemoteHost,
    [string]$RemoteUser = '',
    [string]$ZipPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release\MyPowerTools-win-x64.zip'),
    [string]$RemoteTempDir = 'C:\Users\Public\MyPowerTools-Upgrade',
    [string]$RemotePwsh = 'pwsh.exe',
    [switch]$DryRun,
    [switch]$PreflightOnly,
    [switch]$SkipZipCopy,
    [switch]$SkipExtract
)

$ErrorActionPreference = 'Stop'

$ZipPath = [IO.Path]::GetFullPath($ZipPath)
if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    throw "Local release ZIP is missing: $ZipPath"
}
$zipHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$hashMarker = "$ZipPath.sha256"
if (-not (Test-Path -LiteralPath $hashMarker -PathType Leaf)) {
    throw "Local release SHA-256 marker is missing: $hashMarker"
}

$sshTarget = if ([string]::IsNullOrWhiteSpace($RemoteUser)) { $RemoteHost } else { "$RemoteUser@$RemoteHost" }
$remoteZip = (Join-Path $RemoteTempDir 'MyPowerTools-win-x64.zip').Replace('\', '/')
$remoteMarker = (Join-Path $RemoteTempDir 'MyPowerTools-win-x64.zip.sha256').Replace('\', '/')
$remoteScript = (Join-Path $RemoteTempDir 'dorm-upgrade-remote.ps1').Replace('\', '/')
$remoteStaging = (Join-Path $RemoteTempDir 'staging').Replace('\', '/')
$remoteInstallOverride = (Join-Path $RemoteTempDir 'install-windows.ps1').Replace('\', '/')
$localRemoteHelper = Join-Path $PSScriptRoot 'dorm-upgrade-remote.ps1'
$localInstallScript = Join-Path $PSScriptRoot 'install-windows.ps1'

function Invoke-Ssh {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    if ($DryRun) {
        "ssh $($Arguments -join ' ')"
        return
    }
    & ssh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ssh failed with exit code $LASTEXITCODE."
    }
}

Invoke-Ssh -Arguments @(
    '-o', 'BatchMode=yes',
    '-o', 'ConnectTimeout=10',
    $sshTarget,
    'powershell.exe -NoLogo -NoProfile -NonInteractive -Command "exit 0"'
)

if ($PreflightOnly) {
    [pscustomobject]@{
        success = $true
        host = $sshTarget
        preflight = 'remote-reachable'
        zipSha256 = $zipHash
        dryRun = $DryRun.IsPresent
    } | ConvertTo-Json -Depth 4
    return
}

Invoke-Ssh -Arguments @(
    '-o', 'BatchMode=yes',
    $sshTarget,
    "powershell.exe -NoLogo -NoProfile -NonInteractive -Command `"New-Item -ItemType Directory -Force -Path '$RemoteTempDir' | Out-Null`""
)

if (-not $DryRun) {
    if (-not $SkipZipCopy.IsPresent) {
        & scp -o BatchMode=yes $ZipPath "$sshTarget`:$remoteZip"
        if ($LASTEXITCODE -ne 0) {
            throw "scp failed for the release ZIP (exit $LASTEXITCODE)."
        }
        & scp -o BatchMode=yes $hashMarker "$sshTarget`:$remoteMarker"
        if ($LASTEXITCODE -ne 0) {
            throw "scp failed for the SHA-256 marker (exit $LASTEXITCODE)."
        }
    }
    & scp -o BatchMode=yes $localRemoteHelper "$sshTarget`:$remoteScript"
    if ($LASTEXITCODE -ne 0) {
        throw "scp failed for the remote helper (exit $LASTEXITCODE)."
    }
    & scp -o BatchMode=yes $localInstallScript "$sshTarget`:$remoteInstallOverride"
    if ($LASTEXITCODE -ne 0) {
        throw "scp failed for install-windows.ps1 (exit $LASTEXITCODE)."
    }
}

$remoteCommand = "& '$RemotePwsh' -NoLogo -NoProfile -NonInteractive -File '$remoteScript' -ZipPath '$remoteZip' -StagingDir '$remoteStaging' -InstallScriptOverride '$remoteInstallOverride'"
if ($SkipExtract.IsPresent) {
    $remoteCommand += ' -SkipExtract'
}
if ($DryRun) {
    "ssh $sshTarget `"$remoteCommand`""
    [pscustomobject]@{
        success = $true
        dryRun = $true
        host = $sshTarget
        remoteZip = $remoteZip
        remoteStaging = $remoteStaging
        zipSha256 = $zipHash
    } | ConvertTo-Json -Depth 4
    return
}

$output = @(& ssh -o BatchMode=yes $sshTarget $remoteCommand | ForEach-Object { [string]$_ })
if ($LASTEXITCODE -ne 0) {
    throw "Remote upgrade failed with exit code $LASTEXITCODE."
}
$output -join [Environment]::NewLine
