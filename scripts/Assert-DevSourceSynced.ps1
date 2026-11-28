<#
.SYNOPSIS
Refuses to deploy unless the local checkout exactly matches its upstream.

.DESCRIPTION
Every deployment (the Windows Dev overlay, Broker overlay, or any future deploy entry point) must
be built on the target machine from a checkout that is in sync with the shared repository:

  * the checkout is a git work tree,
  * the upstream was fetched just now,
  * there are no uncommitted or untracked source changes, and
  * HEAD is exactly the upstream commit (nothing unpushed, nothing unpulled).

The upstream is the current branch's tracking branch, or origin/main for a detached HEAD.
There is no override: commit and push, or pull, then deploy again.

.OUTPUTS
An object with RepositoryRoot, Upstream and Commit for provenance records.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$DefaultUpstream = 'origin/main'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)

function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & git -C $RepositoryRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed in ${RepositoryRoot}: $($output | Out-String)"
    }

    return ($output | Out-String).Trim()
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'Deployment requires git to verify that the local source is in sync. Install git, then try again.'
}

$insideWorkTree = & git -C $RepositoryRoot rev-parse --is-inside-work-tree 2>$null
if ($LASTEXITCODE -ne 0 -or $insideWorkTree -ne 'true') {
    throw "Deployment must run from a git checkout of the repository on this machine. '$RepositoryRoot' is not a work tree. Clone or add a worktree first, then deploy from it."
}

$branch = Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')
$upstream = $null
if ($branch -ne 'HEAD') {
    $upstream = & git -C $RepositoryRoot rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null
    if ($LASTEXITCODE -ne 0) { $upstream = $null }
}
if ([string]::IsNullOrWhiteSpace($upstream)) {
    $upstream = $DefaultUpstream
}

$remote = $upstream.Split('/')[0]
try {
    Invoke-Git @('fetch', '--quiet', $remote) | Out-Null
}
catch {
    throw "Could not fetch '$remote' to confirm the local source is current. Deployment needs a successful fetch. $($_.Exception.Message)"
}

$dirty = Invoke-Git @('status', '--porcelain', '--untracked-files=normal')
if (-not [string]::IsNullOrWhiteSpace($dirty)) {
    throw @"
Local source has uncommitted or untracked changes, so the deployment would not match the shared repository:
$dirty
Commit and push them, or discard them, then deploy again.
"@
}

$head = Invoke-Git @('rev-parse', 'HEAD')
$target = Invoke-Git @('rev-parse', $upstream)
if ($head -ne $target) {
    $counts = (Invoke-Git @('rev-list', '--left-right', '--count', "HEAD...$upstream")) -split '\s+'
    $ahead = $counts[0]
    $behind = $counts[1]
    $hint = if ($branch -eq 'HEAD') {
        "git -C `"$RepositoryRoot`" checkout --detach $upstream"
    }
    elseif ([int]$ahead -gt 0) {
        "git -C `"$RepositoryRoot`" push (and pull first if the upstream also moved)"
    }
    else {
        "git -C `"$RepositoryRoot`" pull --ff-only"
    }
    throw "Local source is not in sync with $upstream (ahead $ahead, behind $behind). Sync first: $hint"
}

Write-Host ("==> Source in sync with {0} at {1}" -f $upstream, $head.Substring(0, 12)) -ForegroundColor Cyan
[pscustomobject]@{
    RepositoryRoot = $RepositoryRoot
    Upstream = $upstream
    Commit = $head
}
