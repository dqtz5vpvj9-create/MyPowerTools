<#
.SYNOPSIS
  Prepares the Windows CI workspace so it matches actions/checkout@v4.

.DESCRIPTION
  Bootstrap half of the preflight rig. It runs before the tree under test
  exists on the host, so preflight.sh copies it up on every run rather than
  reading it out of the workspace.

  What actions/checkout gives a job, and what this reproduces:
    * the exact commit, detached, not a branch tip that may have moved;
    * submodules initialised recursively at the commits the superproject
      records, not whatever branch a developer left them on;
    * a tree with no untracked or ignored leftovers.

  The last point is the one that matters most on a reused workspace. A stale
  artifacts/ or bin/ tree makes steps pass that would fail on a fresh runner.

.PARAMETER Commit
  The commit to test. Fetched from the bundle when one is supplied, otherwise
  expected to already be reachable from origin.

.PARAMETER SubmoduleManifest
  Tab-separated "path<TAB>commit<TAB>bundle" lines describing submodules whose
  working tree is being tested. The superproject only records a submodule's
  commit, so an uncommitted change inside one is invisible to a bundle of the
  parent; these carry it across. Applied after the recursive submodule update so
  they override the recorded commits.

.PARAMETER TokenFromStdin
  Read a GitHub token from stdin and use it for submodule fetches. Nine of the
  ten submodules are public and clone anonymously; external/NotifyApp is
  private, which is why the workflows pass secrets.MPT_SUBMODULE_PAT to
  actions/checkout. The token is applied through GIT_CONFIG_* environment
  variables for the lifetime of this process, so it reaches neither a command
  line nor a config file on disk.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Commit,
    [string]$Workspace = 'C:\ci\MyPowerTools',
    [string]$BundlePath = '',
    [string]$OriginUrl = 'https://github.com/dqtz5vpvj9-create/MyPowerTools.git',
    [switch]$TokenFromStdin,
    [string]$SubmoduleManifest = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Fail instead of blocking on a credential prompt: over SSH there is no tty, so
# a prompt surfaces as the confusing "could not read Username" plus a hang.
$env:GIT_TERMINAL_PROMPT = '0'

if ($TokenFromStdin) {
    $token = [Console]::In.ReadLine()
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'No token arrived on stdin.' }
    $env:GIT_CONFIG_COUNT = '1'
    $env:GIT_CONFIG_KEY_0 = "url.https://x-access-token:$token@github.com/.insteadOf"
    $env:GIT_CONFIG_VALUE_0 = 'https://github.com/'
    Write-Host '==> using the supplied token for private submodules'
}

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path -LiteralPath (Join-Path $Workspace '.git'))) {
    Write-Host "==> cloning $OriginUrl into $Workspace"
    $parent = Split-Path -Parent $Workspace
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Invoke-Git clone $OriginUrl $Workspace
}

Set-Location $Workspace

Invoke-Git fetch origin --prune --quiet

if ($BundlePath) {
    Write-Host "==> fetching $Commit from bundle"
    Invoke-Git fetch $BundlePath 'refs/preflight/head:refs/preflight/head' --force
} else {
    Write-Host "==> $Commit is expected on origin"
    Invoke-Git update-ref refs/preflight/head $Commit
}

$resolved = (git rev-parse refs/preflight/head).Trim()
if ($resolved -ne $Commit) {
    throw "refs/preflight/head resolved to $resolved, expected $Commit."
}

Write-Host "==> checking out $Commit"
Invoke-Git checkout --detach --force refs/preflight/head
Invoke-Git reset --hard --quiet
Invoke-Git clean -xdff
Invoke-Git submodule sync --recursive --quiet
Invoke-Git submodule update --init --recursive --force
git submodule foreach --recursive 'git clean -xdff' | Out-Null

if ($SubmoduleManifest) {
    foreach ($line in (Get-Content -LiteralPath $SubmoduleManifest)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $path, $commit, $bundle = $line -split "`t"
        Write-Host "==> overriding submodule $path with $commit"
        Push-Location (Join-Path $Workspace $path)
        try {
            Invoke-Git fetch $bundle 'refs/preflight/head:refs/preflight/head' --force
            Invoke-Git checkout --detach --force refs/preflight/head
            $resolved = (git rev-parse HEAD).Trim()
            if ($resolved -ne $commit) {
                throw "submodule $path resolved to $resolved, expected $commit."
            }
        } finally {
            Pop-Location
        }
    }
}

Write-Host "==> workspace is at $(git log --oneline -1)"
