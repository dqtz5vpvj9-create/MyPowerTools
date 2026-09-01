[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [Parameter(Mandatory = $true)][string]$RuntimeComponentsManifestPath,
    [Parameter(Mandatory = $true)][string]$SigningKeyIncludePath,
    [string]$EvidencePath = ''
)

$ErrorActionPreference = 'Stop'

function Find-ISSigTool {
    $command = Get-Command 'ISSigTool.exe' -CommandType Application -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }
    foreach ($candidate in @(
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\ISSigTool.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISSigTool.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISSigTool.exe')
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }
    throw 'Inno Setup Signature Tool (ISSigTool.exe) was not found.'
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList
    )
    & $FilePath @ArgumentList
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$FilePath failed with exit code $exitCode."
    }
}

function Invoke-DownloadWithRetry {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$OutFile,
        [int]$Attempts = 6
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Uri -OutFile $OutFile
            return
        }
        catch {
            if (Test-Path -LiteralPath $OutFile) {
                Remove-Item -LiteralPath $OutFile -Force
            }
            if ($attempt -eq $Attempts) {
                throw
            }
            Start-Sleep -Seconds ([Math]::Min($attempt * 2, 10))
        }
    }
}

function Get-InnoDefineMap {
    param([Parameter(Mandatory = $true)][string]$Path)
    $result = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^#define\s+(\S+)\s+"([0-9A-Za-z]+)"$') {
            $result[$matches[1]] = $matches[2]
        }
    }
    return $result
}

$manifestFull = [IO.Path]::GetFullPath($RuntimeComponentsManifestPath)
$includeFull = [IO.Path]::GetFullPath($SigningKeyIncludePath)
if (-not (Test-Path -LiteralPath $manifestFull -PathType Leaf)) {
    throw "Runtime component manifest does not exist: $manifestFull"
}
if (-not (Test-Path -LiteralPath $includeFull -PathType Leaf)) {
    throw "Web installer signing key include does not exist: $includeFull"
}

$keyDefines = Get-InnoDefineMap -Path $includeFull
foreach ($requiredDefine in @('WebISSigKeyID', 'WebISSigPublicX', 'WebISSigPublicY')) {
    if ([string]::IsNullOrWhiteSpace([string]$keyDefines[$requiredDefine])) {
        throw "Signing key include is missing $requiredDefine."
    }
}

$manifest = Get-Content -LiteralPath $manifestFull -Raw | ConvertFrom-Json
$assetRecords = [Collections.Generic.List[object]]::new()
$assetRecords.Add([pscustomobject]@{
    Asset = [string]$manifest.core.asset
    Sha256 = [string]$manifest.core.sha256
    Size = [long]$manifest.core.size
})
foreach ($component in @($manifest.components)) {
    $assetRecords.Add([pscustomobject]@{
        Asset = [string]$component.asset
        Sha256 = [string]$component.sha256
        Size = [long]$component.size
    })
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'MyPowerTools-web-assets-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$results = [Collections.Generic.List[object]]::new()
try {
    $publicKeyPath = Join-Path $tempRoot 'release-public.key'
    $publicKeyText = @(
        'format issig-public-key'
        "key-id $($keyDefines.WebISSigKeyID)"
        "public-x $($keyDefines.WebISSigPublicX)"
        "public-y $($keyDefines.WebISSigPublicY)"
        ''
    ) -join "`r`n"
    [IO.File]::WriteAllText($publicKeyPath, $publicKeyText, [Text.UTF8Encoding]::new($false))

    $normalizedBaseUrl = $BaseUrl.TrimEnd('/')
    $remoteManifestPath = Join-Path $tempRoot 'runtime-components.json'
    Invoke-DownloadWithRetry -Uri "$normalizedBaseUrl/runtime-components.json" -OutFile $remoteManifestPath
    $localManifestHash = (Get-FileHash -LiteralPath $manifestFull -Algorithm SHA256).Hash
    $remoteManifestHash = (Get-FileHash -LiteralPath $remoteManifestPath -Algorithm SHA256).Hash
    if ($remoteManifestHash -ne $localManifestHash) {
        throw "Remote runtime-components.json differs from the release candidate. expected=$localManifestHash actual=$remoteManifestHash"
    }

    $sigTool = Find-ISSigTool
    foreach ($record in $assetRecords) {
        $assetName = [string]$record.Asset
        if ([string]::IsNullOrWhiteSpace($assetName) -or
            $assetName -ne [IO.Path]::GetFileName($assetName)) {
            throw "Invalid runtime component asset name: $assetName"
        }
        $downloadPath = Join-Path $tempRoot $assetName
        Invoke-DownloadWithRetry -Uri "$normalizedBaseUrl/$assetName" -OutFile $downloadPath
        Invoke-DownloadWithRetry -Uri "$normalizedBaseUrl/$assetName.issig" -OutFile "$downloadPath.issig"

        $actualItem = Get-Item -LiteralPath $downloadPath
        $actualHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualItem.Length -ne [long]$record.Size) {
            throw "$assetName size mismatch. expected=$($record.Size) actual=$($actualItem.Length)"
        }
        if ($actualHash -ne ([string]$record.Sha256).ToLowerInvariant()) {
            throw "$assetName SHA-256 mismatch. expected=$($record.Sha256) actual=$actualHash"
        }
        Invoke-Native -FilePath $sigTool -ArgumentList @(
            "--key-file=$publicKeyPath",
            'verify',
            $downloadPath)
        $results.Add([pscustomobject]@{
            asset = $assetName
            bytes = $actualItem.Length
            sha256 = $actualHash
            issig = 'valid'
        })
    }

    $evidence = [ordered]@{
        success = $true
        baseUrl = $normalizedBaseUrl
        manifestSha256 = $localManifestHash.ToLowerInvariant()
        verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        assets = $results.ToArray()
    }
    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $evidenceFull = [IO.Path]::GetFullPath($EvidencePath)
        New-Item -ItemType Directory -Path (Split-Path -Parent $evidenceFull) -Force | Out-Null
        [IO.File]::WriteAllText(
            $evidenceFull,
            ($evidence | ConvertTo-Json -Depth 6),
            [Text.UTF8Encoding]::new($false))
    }
    $evidence
}
finally {
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $tempRootFull = [IO.Path]::GetFullPath($tempRoot)
    if (-not $tempRootFull.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean verification path outside the temp root: $tempRootFull"
    }
    if (Test-Path -LiteralPath $tempRootFull) {
        Remove-Item -LiteralPath $tempRootFull -Recurse -Force
    }
}
