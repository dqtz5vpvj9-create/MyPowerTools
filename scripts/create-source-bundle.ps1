param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\source-bundle'),
    [string] $ArchivePath = (Join-Path (Split-Path -Parent $PSScriptRoot) ("artifacts\release\MyPowerTools-Source-{0}.zip" -f (Get-Date -Format 'yyyyMMdd')))
)

$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$ArchivePath = [System.IO.Path]::GetFullPath($ArchivePath)
$ArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'artifacts'))

$toolIds = @(
    'adb-forwarder',
    'remote-notifications',
    'remote-commands',
    'process-monitor',
    'screenease',
    'smartbird-thermostat',
    'doubao-computer-use'
)

$excludedDirectoryNames = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
    '.git', '.idea', '.vs', '.vscode', '.venv', '__pycache__',
    'artifacts', 'bin', 'dist', 'logs', 'node_modules', 'obj', 'publish',
    'runtime-state', 'secrets'
)) {
    $null = $excludedDirectoryNames.Add($name)
}

$excludedFileNames = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
    '.env', 'server.key', 'simple_http_notification_conf_user.yaml'
)) {
    $null = $excludedFileNames.Add($name)
}

$excludedFileExtensions = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($extension in @(
    '.db', '.db-shm', '.db-wal', '.dll', '.dmp', '.exe', '.log', '.p12',
    '.pdb', '.pfx', '.pyc', '.pyo', '.suo', '.tmp', '.user', '.zip'
)) {
    $null = $excludedFileExtensions.Add($extension)
}

function Assert-ChildOfArtifacts {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Candidate,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $artifactsPrefix = $ArtifactsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $Candidate.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must stay inside $ArtifactsRoot. Received: $Candidate"
    }
}

function Invoke-NativeText {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [Parameter(Mandatory = $true)]
        [string[]] $ArgumentList
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $ArgumentList) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start native command: $FilePath"
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw "$FilePath exited with $($process.ExitCode): $stderr"
    }

    return $stdout
}

function Get-GitMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string] $WorkingTree
    )

    $commit = (Invoke-NativeText -FilePath 'git' -ArgumentList @(
        '-C', $WorkingTree, 'rev-parse', 'HEAD'
    )).Trim()
    $branch = (Invoke-NativeText -FilePath 'git' -ArgumentList @(
        '-C', $WorkingTree, 'branch', '--show-current'
    )).Trim()
    $statusText = (Invoke-NativeText -FilePath 'git' -ArgumentList @(
        '-c', 'core.quotePath=false', '-C', $WorkingTree, 'status', '--short'
    )).TrimEnd()
    $status = if ([string]::IsNullOrWhiteSpace($statusText)) {
        @()
    } else {
        @($statusText -split "`r?`n")
    }

    return [ordered]@{
        commit = $commit
        branch = $branch
        dirty = $status.Count -gt 0
        status = $status
    }
}

function Test-ExcludedFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RelativePath,

        [string[]] $ExcludedTopLevel = @()
    )

    $normalized = $RelativePath.Replace('\', '/')
    $segments = @($normalized -split '/')
    if ($segments.Count -eq 0) {
        return $true
    }

    if ($ExcludedTopLevel -contains $segments[0]) {
        return $true
    }

    foreach ($segment in $segments) {
        if ($excludedDirectoryNames.Contains($segment)) {
            return $true
        }
    }

    $fileName = $segments[-1]
    $extension = [System.IO.Path]::GetExtension($fileName)
    $isPrivateEnvironmentFile = $fileName.StartsWith('.env.', [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $fileName.Equals('.env.example', [System.StringComparison]::OrdinalIgnoreCase)
    return $excludedFileNames.Contains($fileName) -or
        $excludedFileExtensions.Contains($extension) -or
        $fileName.EndsWith('_user.yaml', [System.StringComparison]::OrdinalIgnoreCase) -or
        $isPrivateEnvironmentFile
}

function Copy-GitWorkingTree {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceRoot,

        [Parameter(Mandatory = $true)]
        [string] $DestinationRoot,

        [string[]] $ExcludedTopLevel = @()
    )

    New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null
    $listed = Invoke-NativeText -FilePath 'git' -ArgumentList @(
        '-c', 'core.quotePath=false', '-C', $SourceRoot,
        'ls-files', '-co', '--exclude-standard', '-z'
    )
    $relativePaths = @($listed.Split([char]0, [System.StringSplitOptions]::RemoveEmptyEntries))
    $copied = 0
    $skipped = 0

    foreach ($relativePath in $relativePaths) {
        if (Test-ExcludedFile -RelativePath $relativePath -ExcludedTopLevel $ExcludedTopLevel) {
            $skipped += 1
            continue
        }

        $normalized = $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $SourceRoot $normalized))
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            # Superproject gitlinks are materialized separately below.
            $skipped += 1
            continue
        }

        $destinationPath = Join-Path $DestinationRoot $normalized
        $destinationParent = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
        $copied += 1
    }

    return [ordered]@{
        listedFileCount = $relativePaths.Count
        copiedFileCount = $copied
        skippedFileCount = $skipped
    }
}

if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
    throw "MyPowerTools repository was not found: $RepoRoot"
}
Assert-ChildOfArtifacts -Candidate $OutputRoot -Label 'OutputRoot'
Assert-ChildOfArtifacts -Candidate $ArchivePath -Label 'ArchivePath'

foreach ($toolId in $toolIds) {
    $toolRoot = Join-Path $RepoRoot "tools\$toolId"
    if (-not (Test-Path -LiteralPath (Join-Path $toolRoot 'tool-release.json') -PathType Leaf)) {
        throw "Tool submodule is missing or incomplete: $toolRoot"
    }
}

if (Test-Path -LiteralPath $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$superprojectCopy = Copy-GitWorkingTree `
    -SourceRoot $RepoRoot `
    -DestinationRoot (Join-Path $OutputRoot 'superproject') `
    -ExcludedTopLevel @('artifacts', 'modules', 'tools')

$toolManifest = [System.Collections.Generic.List[object]]::new()
foreach ($toolId in $toolIds) {
    $toolRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot "tools\$toolId"))
    $copy = Copy-GitWorkingTree `
        -SourceRoot $toolRoot `
        -DestinationRoot (Join-Path $OutputRoot "superproject\tools\$toolId")
    $releaseContract = Get-Content -Raw -LiteralPath (Join-Path $toolRoot 'tool-release.json') | ConvertFrom-Json
    $toolManifest.Add([ordered]@{
        toolId = $toolId
        status = [string]$releaseContract.status
        packageId = [string]$releaseContract.packageId
        git = Get-GitMetadata -WorkingTree $toolRoot
        snapshot = $copy
    })
}

$manifest = [ordered]@{
    schemaVersion = 2
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    layout = 'materialized-submodules'
    buildInstructions = 'Extract the archive, enter superproject, and run pwsh scripts/publish-windows.ps1. All seven submodule worktrees are already materialized under superproject/tools.'
    superproject = [ordered]@{
        git = Get-GitMetadata -WorkingTree $RepoRoot
        snapshot = $superprojectCopy
    }
    tools = @($toolManifest)
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'bundle-manifest.json') -Encoding UTF8

$readme = @'
# MyPowerTools complete source bundle

This archive contains the current MyPowerTools superproject and materialized source snapshots for all seven tool submodules.

- `superproject/` contains the suite host, Shell, build scripts, schemas, assets, installer source, and materialized tool submodules.
- `superproject/tools/<tool-id>/` contains each tool's full tracked and non-ignored working tree, including its original source, native UI integration, package template, and independent build contract.
- `bundle-manifest.json` records every repository commit and dirty state at packaging time.
- `files.sha256` verifies every bundled source file.

To build from the archive, run `pwsh scripts/publish-windows.ps1` inside `superproject/`. Git metadata, build outputs, caches, virtual environments, runtime databases, and user secrets are excluded.
'@
Set-Content -LiteralPath (Join-Path $OutputRoot 'README.md') -Value $readme -Encoding UTF8

$checksumPath = Join-Path $OutputRoot 'files.sha256'
$checksumLines = foreach ($file in Get-ChildItem -LiteralPath $OutputRoot -Recurse -File -Force |
    Where-Object { -not $_.FullName.Equals($checksumPath, [System.StringComparison]::OrdinalIgnoreCase) } |
    Sort-Object FullName) {
    $relativePath = [System.IO.Path]::GetRelativePath($OutputRoot, $file.FullName).Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$relativePath"
}
$checksumLines | Set-Content -LiteralPath $checksumPath -Encoding UTF8

$archiveParent = Split-Path -Parent $ArchivePath
New-Item -ItemType Directory -Path $archiveParent -Force | Out-Null
if (Test-Path -LiteralPath $ArchivePath) {
    Remove-Item -LiteralPath $ArchivePath -Force
}
Compress-Archive -Path (Join-Path $OutputRoot '*') -DestinationPath $ArchivePath -CompressionLevel Optimal

$bundleFiles = @(Get-ChildItem -LiteralPath $OutputRoot -Recurse -File -Force)
$bundleBytes = ($bundleFiles | Measure-Object -Property Length -Sum).Sum
Write-Host "Source bundle written to $OutputRoot"
Write-Host "Archive written to $ArchivePath"
Write-Host "Files: $($bundleFiles.Count)"
Write-Host ('Size: {0:N2} MiB' -f ($bundleBytes / 1MB))
