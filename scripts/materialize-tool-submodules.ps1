<#
.SYNOPSIS
Materializes eight local tool repositories from artifacts/source-bundle and optionally attaches them as submodules.

.DESCRIPTION
The script creates one independent local Git repository per tool under a sibling
MyPowerTools.ToolRepos directory. Each repository commits original-source,
current-integration, source-map.json, and a generated README.md. It then adds the repositories to
MyPowerTools/tools/<tool-id> through absolute file:// submodule URLs.

The default workflow contacts no network URL. Submodule clone and ForceRefresh
fetch operations are restricted to the generated local file:// URLs. Existing
directories and Git repositories are preserved. Conflicting non-Git directories,
dirty repositories, and ordinary tracked paths stop the script.

The generated file:// URLs are development-only bootstrap URLs. After publishing
a tool repository, replace its URL in the superproject and synchronize it:

    git config -f .gitmodules submodule.tools/<tool-id>.url <remote-url>
    git submodule sync -- tools/<tool-id>
    git add .gitmodules

.PARAMETER SkipSubmoduleAdd
Creates or refreshes the eight local repositories without changing the
superproject working tree, index, tools directory, or .gitmodules.

.PARAMETER ForceRefresh
Refreshes original-source, current-integration, and source-map.json from the current source bundle and
commits changes in clean, previously materialized local repositories. Existing
local file:// submodule checkouts are advanced to the refreshed local commit.
Remote submodule URLs remain unchanged and are never contacted.

.NOTES
Run scripts/create-source-bundle.ps1 first. This script intentionally works from
the captured bundle so dirty original working trees are preserved by the source
snapshot without being read or modified during repository materialization.
#>

[CmdletBinding()]
param(
    [string] $RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $SourceBundleRoot,
    [string] $ToolReposRoot,
    [switch] $SkipSubmoduleAdd,
    [switch] $ForceRefresh
)

$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
if ([string]::IsNullOrWhiteSpace($SourceBundleRoot)) {
    $SourceBundleRoot = Join-Path $RepoRoot 'artifacts\source-bundle'
}
if ([string]::IsNullOrWhiteSpace($ToolReposRoot)) {
    $ToolReposRoot = Join-Path (Split-Path -Parent $RepoRoot) 'MyPowerTools.ToolRepos'
}
$SourceBundleRoot = [System.IO.Path]::GetFullPath($SourceBundleRoot)
$ToolReposRoot = [System.IO.Path]::GetFullPath($ToolReposRoot)
$SuperprojectToolsRoot = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'tools'))

$repoRootPrefix = $RepoRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if ($ToolReposRoot.Equals($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    $ToolReposRoot.StartsWith($repoRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ToolReposRoot must stay outside the MyPowerTools superproject: $ToolReposRoot"
}

$toolIds = @(
    'adb-forwarder',
    'remote-notifications',
    'remote-commands',
    'process-monitor',
    'screenease',
    'smartbird-thermostat',
    'doubao-computer-use',
    'input-monitor'
)

$generatedReadmeMarker = '<!-- mypowertools-materialized-source -->'
$gitCommand = Get-Command 'git.exe' -CommandType Application -ErrorAction SilentlyContinue
if ($null -eq $gitCommand) {
    $gitCommand = Get-Command 'git' -CommandType Application -ErrorAction Stop
}
$gitExecutable = $gitCommand.Source

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $ArgumentList,

        [int[]] $ExpectedExitCodes = @(0),

        [switch] $CaptureOutput
    )

    $commandOutput = @(& $gitExecutable @ArgumentList 2>&1)
    $exitCode = $LASTEXITCODE
    if ($ExpectedExitCodes -notcontains $exitCode) {
        $renderedArguments = $ArgumentList -join ' '
        $renderedOutput = ($commandOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        throw "git $renderedArguments failed with exit code $exitCode.$([Environment]::NewLine)$renderedOutput"
    }

    if ($CaptureOutput) {
        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = @($commandOutput | ForEach-Object { $_.ToString() })
        }
    }

    foreach ($line in $commandOutput) {
        Write-Host $line
    }
}

function Get-GitOutputText {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $ArgumentList,

        [int[]] $ExpectedExitCodes = @(0)
    )

    $result = Invoke-Git -ArgumentList $ArgumentList -ExpectedExitCodes $ExpectedExitCodes -CaptureOutput
    return (($result.Output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
}

function Assert-DirectoryExists {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Container)) {
        throw "$Label directory was not found: $LiteralPath"
    }
}

function Test-DirectoryEmpty {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath
    )

    return $null -eq (Get-ChildItem -LiteralPath $LiteralPath -Force | Select-Object -First 1)
}

function Assert-PathInsideRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $Candidate
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $candidateFull = [System.IO.Path]::GetFullPath($Candidate)
    $rootPrefix = $rootFull.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidateFull.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Managed path escapes its root. Root=$rootFull Candidate=$candidateFull"
    }
}

function Assert-GitRepositoryRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryPath,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    if (-not (Test-Path -LiteralPath (Join-Path $RepositoryPath '.git'))) {
        throw "$Label is not an independent Git repository: $RepositoryPath"
    }

    $topLevel = Get-GitOutputText -ArgumentList @(
        '-C', $RepositoryPath, 'rev-parse', '--show-toplevel'
    )
    $topLevelFull = [System.IO.Path]::GetFullPath($topLevel)
    if (-not $topLevelFull.Equals(
        [System.IO.Path]::GetFullPath($RepositoryPath),
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label resolves to a different Git root: $topLevelFull"
    }
}

function Get-RepositoryStatus {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryPath
    )

    return Get-GitOutputText -ArgumentList @(
        '-c', 'core.quotePath=false', '-C', $RepositoryPath, 'status', '--porcelain'
    )
}

function Test-RepositoryHasHead {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryPath
    )

    $result = Invoke-Git -ArgumentList @(
        '-C', $RepositoryPath, 'rev-parse', '--verify', 'HEAD'
    ) -ExpectedExitCodes @(0, 128) -CaptureOutput
    return $result.ExitCode -eq 0
}

function Ensure-LocalGitIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryPath
    )

    $nameResult = Invoke-Git -ArgumentList @(
        '-C', $RepositoryPath, 'config', '--get', 'user.name'
    ) -ExpectedExitCodes @(0, 1) -CaptureOutput
    if ($nameResult.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace(($nameResult.Output -join ''))) {
        Invoke-Git -ArgumentList @(
            '-C', $RepositoryPath, 'config', '--local', 'user.name', 'MyPowerTools Source Materializer'
        )
    }

    $emailResult = Invoke-Git -ArgumentList @(
        '-C', $RepositoryPath, 'config', '--get', 'user.email'
    ) -ExpectedExitCodes @(0, 1) -CaptureOutput
    if ($emailResult.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace(($emailResult.Output -join ''))) {
        Invoke-Git -ArgumentList @(
            '-C', $RepositoryPath, 'config', '--local', 'user.email', 'source-materializer@local.invalid'
        )
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,

        [Parameter(Mandatory = $true)]
        [string] $Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function ConvertTo-FileUrl {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryPath
    )

    return ([System.Uri]::new([System.IO.Path]::GetFullPath($RepositoryPath))).AbsoluteUri
}

function New-MaterializedReadmeContent {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ToolId,

        [Parameter(Mandatory = $true)]
        [string] $LocalFileUrl
    )

    $template = @'
<!-- mypowertools-materialized-source -->
# MyPowerTools tool source: {{TOOL_ID}}

This local development repository was materialized from
`artifacts/source-bundle/tools/{{TOOL_ID}}`.

Committed snapshot content:

- `original-source/`: captured original tool source;
- `current-integration/`: current MyPowerTools module, product UI, service, and related test source;
- `source-map.json`: source commit, dirty-state, and snapshot mapping;
- `README.md`: local materialization and remote migration instructions.

## Local submodule URL

The initial superproject URL is local to this machine:

```text
{{LOCAL_FILE_URL}}
```

After this repository is published, run the following commands from the
MyPowerTools superproject and commit the resulting `.gitmodules` update:

```powershell
git config -f .gitmodules submodule.tools/{{TOOL_ID}}.url <remote-url>
git submodule sync -- tools/{{TOOL_ID}}
git add .gitmodules
```

The materialization script preserves a URL that has already been changed to a
remote location and does not contact that remote.
'@

    return $template.Replace('{{TOOL_ID}}', $ToolId).Replace('{{LOCAL_FILE_URL}}', $LocalFileUrl)
}

function Get-SubmoduleIndexMode {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    $stageText = Get-GitOutputText -ArgumentList @(
        '-C', $RepoRoot, 'ls-files', '--stage', '--', $RelativePath
    )
    if ([string]::IsNullOrWhiteSpace($stageText)) {
        return ''
    }

    $modes = @($stageText -split "`r?`n" | ForEach-Object {
        if ($_ -match '^(?<mode>\d{6})\s') {
            $Matches.mode
        }
    } | Where-Object { $_ }) | Select-Object -Unique
    if ($modes.Count -eq 1) {
        return [string] @($modes)[0]
    }
    return 'mixed'
}

function Get-SubmoduleRegistration {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    $gitModulesPath = Join-Path $RepoRoot '.gitmodules'
    if (-not (Test-Path -LiteralPath $gitModulesPath -PathType Leaf)) {
        return $null
    }

    $result = Invoke-Git -ArgumentList @(
        'config', '-f', $gitModulesPath, '--get-regexp', '^submodule\..*\.path$'
    ) -ExpectedExitCodes @(0, 1) -CaptureOutput
    if ($result.ExitCode -eq 1) {
        return $null
    }

    foreach ($line in $result.Output) {
        if ($line -notmatch '^(?<key>submodule\.(?<name>.+)\.path)\s+(?<path>.+)$') {
            continue
        }
        if (-not $Matches.path.Equals($RelativePath, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $name = $Matches.name
        $urlResult = Invoke-Git -ArgumentList @(
            'config', '-f', $gitModulesPath, '--get', "submodule.$name.url"
        ) -ExpectedExitCodes @(0, 1) -CaptureOutput
        $url = if ($urlResult.ExitCode -eq 0) {
            ($urlResult.Output -join [Environment]::NewLine).Trim()
        } else {
            ''
        }
        return [pscustomobject]@{
            Name = $name
            Path = $RelativePath
            Url = $url
        }
    }

    return $null
}

function Test-UrlsReferenceSameLocalRepository {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RegisteredUrl,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedFileUrl
    )

    $registeredUri = $null
    if (-not [System.Uri]::TryCreate($RegisteredUrl, [System.UriKind]::Absolute, [ref] $registeredUri) -or
        -not $registeredUri.IsFile) {
        return $false
    }

    $expectedUri = [System.Uri]::new($ExpectedFileUrl)
    return [System.IO.Path]::GetFullPath($registeredUri.LocalPath).Equals(
        [System.IO.Path]::GetFullPath($expectedUri.LocalPath),
        [System.StringComparison]::OrdinalIgnoreCase)
}

Assert-DirectoryExists -LiteralPath $RepoRoot -Label 'MyPowerTools superproject'
Assert-DirectoryExists -LiteralPath $SourceBundleRoot -Label 'Source bundle'
Assert-GitRepositoryRoot -RepositoryPath $RepoRoot -Label 'MyPowerTools superproject'
if (Test-Path -LiteralPath $ToolReposRoot -PathType Leaf) {
    throw "ToolReposRoot is an existing file and was preserved: $ToolReposRoot"
}
if (-not $SkipSubmoduleAdd -and (Test-Path -LiteralPath $SuperprojectToolsRoot -PathType Leaf)) {
    throw "Superproject tools path is an existing file and was preserved: $SuperprojectToolsRoot"
}

$bundleManifestPath = Join-Path $SourceBundleRoot 'bundle-manifest.json'
if (-not (Test-Path -LiteralPath $bundleManifestPath -PathType Leaf)) {
    throw "Source bundle manifest was not found: $bundleManifestPath"
}

$bundleManifest = Get-Content -Raw -LiteralPath $bundleManifestPath | ConvertFrom-Json
$manifestToolIds = @($bundleManifest.tools.id)
$missingManifestTools = @($toolIds | Where-Object { $manifestToolIds -notcontains $_ })
if ($missingManifestTools.Count -gt 0) {
    throw "Source bundle is missing tools: $($missingManifestTools -join ', ')"
}

# Complete every source and destination preflight before creating or modifying a repository.
$plans = [System.Collections.Generic.List[object]]::new()
foreach ($toolId in $toolIds) {
    $bundleToolRoot = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $SourceBundleRoot 'tools') $toolId))
    $bundleOriginalSource = Join-Path $bundleToolRoot 'original-source'
    $bundleCurrentIntegration = Join-Path $bundleToolRoot 'current-integration'
    $bundleSourceMap = Join-Path $bundleToolRoot 'source-map.json'
    Assert-DirectoryExists -LiteralPath $bundleOriginalSource -Label "$toolId original-source"
    Assert-DirectoryExists -LiteralPath $bundleCurrentIntegration -Label "$toolId current-integration"
    if (-not (Test-Path -LiteralPath $bundleSourceMap -PathType Leaf)) {
        throw "$toolId source-map.json was not found: $bundleSourceMap"
    }
    $sourceMap = Get-Content -Raw -LiteralPath $bundleSourceMap | ConvertFrom-Json
    if (-not [string]::Equals(
        [string] $sourceMap.toolId,
        $toolId,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$toolId source-map.json declares a different toolId: $($sourceMap.toolId)"
    }

    $repositoryPath = [System.IO.Path]::GetFullPath((Join-Path $ToolReposRoot $toolId))
    Assert-PathInsideRoot -Root $ToolReposRoot -Candidate $repositoryPath
    $repositoryExists = Test-Path -LiteralPath $repositoryPath
    $repositoryInitialized = $false
    $repositoryHasHead = $false
    $materialized = $false
    $activeToolRepository = $false

    if ($repositoryExists) {
        if (-not (Test-Path -LiteralPath $repositoryPath -PathType Container)) {
            throw "Tool repository path is not a directory: $repositoryPath"
        }

        if (Test-Path -LiteralPath (Join-Path $repositoryPath '.git')) {
            Assert-GitRepositoryRoot -RepositoryPath $repositoryPath -Label "$toolId repository"
            $repositoryInitialized = $true
            $status = Get-RepositoryStatus -RepositoryPath $repositoryPath
            if (-not [string]::IsNullOrWhiteSpace($status)) {
                throw "$toolId repository has uncommitted changes and was preserved: $repositoryPath"
            }
            $repositoryHasHead = Test-RepositoryHasHead -RepositoryPath $repositoryPath
            if ($repositoryHasHead) {
                $currentBranch = Get-GitOutputText -ArgumentList @(
                    '-C', $repositoryPath, 'branch', '--show-current'
                )
                if ([string]::IsNullOrWhiteSpace($currentBranch)) {
                    throw "$toolId repository has a detached HEAD. Check out a local branch before materialization: $repositoryPath"
                }
            }
            $materialized = (Test-Path -LiteralPath (Join-Path $repositoryPath 'original-source') -PathType Container) -and
                (Test-Path -LiteralPath (Join-Path $repositoryPath 'current-integration') -PathType Container) -and
                (Test-Path -LiteralPath (Join-Path $repositoryPath 'source-map.json') -PathType Leaf)
            $activeToolRepository = Test-Path -LiteralPath (Join-Path $repositoryPath 'tool-release.json') -PathType Leaf
            if ($activeToolRepository -and $ForceRefresh) {
                throw "$toolId is now an active tool repository. ForceRefresh cannot overwrite it from a source snapshot: $repositoryPath"
            }
            if ($repositoryHasHead -and -not $materialized -and -not $ForceRefresh) {
                throw "$toolId repository already has history without materialized snapshot paths. Use -ForceRefresh after reviewing it: $repositoryPath"
            }
        } elseif (-not (Test-DirectoryEmpty -LiteralPath $repositoryPath)) {
            throw "$toolId destination is a non-empty non-Git directory and was preserved: $repositoryPath"
        }
    }

    $relativeSubmodulePath = "tools/$toolId"
    $submodulePath = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $relativeSubmodulePath))
    Assert-PathInsideRoot -Root $SuperprojectToolsRoot -Candidate $submodulePath
    $indexMode = if ($SkipSubmoduleAdd) { '' } else { Get-SubmoduleIndexMode -RelativePath $relativeSubmodulePath }
    $registration = if ($SkipSubmoduleAdd) { $null } else { Get-SubmoduleRegistration -RelativePath $relativeSubmodulePath }

    if (-not $SkipSubmoduleAdd) {
        if (-not [string]::IsNullOrWhiteSpace($indexMode) -and $indexMode -ne '160000') {
            throw "$relativeSubmodulePath is already tracked as ordinary content and was preserved."
        }
        if ($indexMode -eq '160000' -and $null -eq $registration) {
            throw "$relativeSubmodulePath is a gitlink without a matching .gitmodules registration."
        }
        if ([string]::IsNullOrWhiteSpace($indexMode) -and $null -ne $registration) {
            throw "$relativeSubmodulePath is registered in .gitmodules without a gitlink."
        }
        if ([string]::IsNullOrWhiteSpace($indexMode) -and (Test-Path -LiteralPath $submodulePath)) {
            throw "$relativeSubmodulePath already exists outside the superproject index and was preserved."
        }
    }

    $plans.Add([pscustomobject]@{
        ToolId = $toolId
        BundleOriginalSource = $bundleOriginalSource
        BundleCurrentIntegration = $bundleCurrentIntegration
        BundleSourceMap = $bundleSourceMap
        RepositoryPath = $repositoryPath
        RepositoryExists = $repositoryExists
        RepositoryInitialized = $repositoryInitialized
        RepositoryHasHead = $repositoryHasHead
        Materialized = $materialized
        ActiveToolRepository = $activeToolRepository
        RelativeSubmodulePath = $relativeSubmodulePath
        SubmodulePath = $submodulePath
        IndexMode = $indexMode
        Registration = $registration
        LocalFileUrl = ConvertTo-FileUrl -RepositoryPath $repositoryPath
        Head = ''
    })
}

$previousTerminalPrompt = $env:GIT_TERMINAL_PROMPT
$env:GIT_TERMINAL_PROMPT = '0'
try {
    New-Item -ItemType Directory -Path $ToolReposRoot -Force | Out-Null

    foreach ($plan in $plans) {
        if (-not $plan.RepositoryExists) {
            New-Item -ItemType Directory -Path $plan.RepositoryPath -Force | Out-Null
        }
        if (-not $plan.RepositoryInitialized) {
            Invoke-Git -ArgumentList @(
                'init', '--initial-branch=main', $plan.RepositoryPath
            )
            $plan.RepositoryInitialized = $true
        }

        Ensure-LocalGitIdentity -RepositoryPath $plan.RepositoryPath
        $shouldRefresh = -not $plan.ActiveToolRepository -and
            (-not $plan.RepositoryHasHead -or -not $plan.Materialized -or $ForceRefresh)
        if ($shouldRefresh) {
            $originalDestination = [System.IO.Path]::GetFullPath((Join-Path $plan.RepositoryPath 'original-source'))
            Assert-PathInsideRoot -Root $plan.RepositoryPath -Candidate $originalDestination
            if (Test-Path -LiteralPath $originalDestination) {
                Remove-Item -LiteralPath $originalDestination -Recurse -Force
            }
            Copy-DirectoryContents -Source $plan.BundleOriginalSource -Destination $originalDestination
            $integrationDestination = [System.IO.Path]::GetFullPath((Join-Path $plan.RepositoryPath 'current-integration'))
            Assert-PathInsideRoot -Root $plan.RepositoryPath -Candidate $integrationDestination
            if (Test-Path -LiteralPath $integrationDestination) {
                Remove-Item -LiteralPath $integrationDestination -Recurse -Force
            }
            Copy-DirectoryContents -Source $plan.BundleCurrentIntegration -Destination $integrationDestination
            Copy-Item -LiteralPath $plan.BundleSourceMap -Destination (Join-Path $plan.RepositoryPath 'source-map.json') -Force

            $readmePath = Join-Path $plan.RepositoryPath 'README.md'
            $writeGeneratedReadme = -not (Test-Path -LiteralPath $readmePath -PathType Leaf)
            if (-not $writeGeneratedReadme) {
                $existingReadme = Get-Content -Raw -LiteralPath $readmePath
                $writeGeneratedReadme = $existingReadme.Contains($generatedReadmeMarker, [System.StringComparison]::Ordinal)
            }
            if ($writeGeneratedReadme) {
                $readmeContent = New-MaterializedReadmeContent -ToolId $plan.ToolId -LocalFileUrl $plan.LocalFileUrl
                Set-Content -LiteralPath $readmePath -Value $readmeContent -Encoding UTF8
            }

            Invoke-Git -ArgumentList @(
                '-C', $plan.RepositoryPath, 'add', '--', 'original-source', 'current-integration', 'source-map.json', 'README.md'
            )
            $stagedResult = Invoke-Git -ArgumentList @(
                '-C', $plan.RepositoryPath, 'diff', '--cached', '--quiet'
            ) -ExpectedExitCodes @(0, 1) -CaptureOutput
            if ($stagedResult.ExitCode -eq 1) {
                $commitMessage = if ($plan.RepositoryHasHead) {
                    "Refresh $($plan.ToolId) source snapshot"
                } else {
                    "Import $($plan.ToolId) source snapshot"
                }
                Invoke-Git -ArgumentList @(
                    '-c', 'commit.gpgSign=false', '-C', $plan.RepositoryPath,
                    'commit', '-m', $commitMessage
                )
            }
        } else {
            Write-Host "Reusing materialized repository: $($plan.RepositoryPath)"
        }

        if (-not (Test-RepositoryHasHead -RepositoryPath $plan.RepositoryPath)) {
            throw "$($plan.ToolId) repository has no commit after materialization."
        }
        $plan.Head = Get-GitOutputText -ArgumentList @(
            '-C', $plan.RepositoryPath, 'rev-parse', 'HEAD'
        )
        $sourceMapCommitCheck = Invoke-Git -ArgumentList @(
            '-C', $plan.RepositoryPath, 'cat-file', '-e', 'HEAD:source-map.json'
        ) -ExpectedExitCodes @(0, 128) -CaptureOutput
        $committedOriginalPaths = Get-GitOutputText -ArgumentList @(
            '-C', $plan.RepositoryPath, 'ls-tree', '-r', '--name-only', 'HEAD', '--', 'original-source'
        )
        $committedIntegrationPaths = Get-GitOutputText -ArgumentList @(
            '-C', $plan.RepositoryPath, 'ls-tree', '-r', '--name-only', 'HEAD', '--', 'current-integration'
        )
        if ($sourceMapCommitCheck.ExitCode -ne 0 -or
            [string]::IsNullOrWhiteSpace($committedOriginalPaths) -or
            [string]::IsNullOrWhiteSpace($committedIntegrationPaths)) {
            throw "$($plan.ToolId) HEAD does not commit source-map.json plus both source snapshots."
        }
    }

    if (-not $SkipSubmoduleAdd) {
        New-Item -ItemType Directory -Path $SuperprojectToolsRoot -Force | Out-Null
        foreach ($plan in $plans) {
            if ($plan.IndexMode -ne '160000') {
                Invoke-Git -ArgumentList @(
                    '-c', 'protocol.file.allow=always', '-C', $RepoRoot,
                    'submodule', 'add', '--name', $plan.RelativeSubmodulePath,
                    '--', $plan.LocalFileUrl, $plan.RelativeSubmodulePath
                )
                continue
            }

            if (-not $ForceRefresh) {
                Write-Host "Reusing registered submodule: $($plan.RelativeSubmodulePath)"
                continue
            }

            $urlComparisonParameters = @{
                RegisteredUrl = $plan.Registration.Url
                ExpectedFileUrl = $plan.LocalFileUrl
            }
            if (-not (Test-UrlsReferenceSameLocalRepository @urlComparisonParameters)) {
                Write-Warning "$($plan.RelativeSubmodulePath) URL is no longer the generated local file URL; it was preserved and not contacted."
                continue
            }

            if (-not (Test-Path -LiteralPath (Join-Path $plan.SubmodulePath '.git'))) {
                Invoke-Git -ArgumentList @(
                    '-c', 'protocol.file.allow=always', '-C', $RepoRoot,
                    'submodule', 'update', '--init', '--', $plan.RelativeSubmodulePath
                )
            }
            $submoduleStatus = Get-RepositoryStatus -RepositoryPath $plan.SubmodulePath
            if (-not [string]::IsNullOrWhiteSpace($submoduleStatus)) {
                throw "$($plan.RelativeSubmodulePath) checkout has uncommitted changes and was preserved."
            }

            Invoke-Git -ArgumentList @(
                '-c', 'protocol.file.allow=always', '-C', $plan.SubmodulePath,
                'fetch', '--no-tags', $plan.LocalFileUrl, 'HEAD'
            )
            Invoke-Git -ArgumentList @(
                '-C', $plan.SubmodulePath, 'checkout', '--detach', $plan.Head
            )
            Invoke-Git -ArgumentList @(
                '-C', $RepoRoot, 'add', '--', $plan.RelativeSubmodulePath
            )
        }
    }
} finally {
    if ($null -eq $previousTerminalPrompt) {
        Remove-Item Env:\GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue
    } else {
        $env:GIT_TERMINAL_PROMPT = $previousTerminalPrompt
    }
}

Write-Host ''
Write-Host "Materialized tool repositories: $ToolReposRoot"
foreach ($plan in $plans) {
    Write-Host "  $($plan.ToolId) $($plan.Head) $($plan.LocalFileUrl)"
}
if ($SkipSubmoduleAdd) {
    Write-Host 'Submodule attachment was skipped; the superproject index and .gitmodules were not changed.'
} else {
    Write-Host "Local submodules are under $SuperprojectToolsRoot. Review and commit .gitmodules plus the eight gitlinks."
    Write-Host 'Replace each file:// URL with its remote URL after publishing the corresponding repository.'
}
