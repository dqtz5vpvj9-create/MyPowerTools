[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipPython,
    [switch]$SkipPlatformTools,
    [switch]$SkipDoubao
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "ASSERT: $Message"
    }
}

function Resolve-SystemPython312 {
    $pythonCandidates = @()
    $pythonCommand = Get-Command 'python.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($pythonCommand) {
        $pythonCandidates += $pythonCommand.Source
    }
    $pyCommand = Get-Command 'py.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1

    foreach ($candidate in $pythonCandidates) {
        $versionOutput = @(& $candidate -c 'import sys; print(sys.version_info[:2])' 2>$null)
        if ($LASTEXITCODE -eq 0 -and $versionOutput -match '\(3, 12\)') {
            $prefix = (& $candidate -c 'import sys; print(sys.prefix)' 2>$null | Select-Object -First 1).Trim()
            if ($prefix -and (Test-Path -LiteralPath (Join-Path $prefix 'python.exe') -PathType Leaf)) {
                return [pscustomobject]@{
                    Root = $prefix
                    Executable = Join-Path $prefix 'python.exe'
                    Launcher = $candidate
                }
            }
        }
    }

    if (-not $pyCommand) {
        throw 'Python 3.12 is required to prepare the bundled runtime cache.'
    }
    $resolveArguments = @('-3.12', '-s', '-c', 'import sys; print(sys.prefix)')
    $prefixLines = @(& $pyCommand.Source @resolveArguments)
    if ($LASTEXITCODE -ne 0) {
        throw 'A working system Python 3.12 installation is required to prepare the bundled runtime cache.'
    }
    $prefix = ($prefixLines | Select-Object -First 1).Trim()
    Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($prefix)) -Message 'Python launcher returned an empty prefix.'
    return [pscustomobject]@{
        Root = $prefix
        Executable = Join-Path $prefix 'python.exe'
        Launcher = $pyCommand.Source
    }
}

function Copy-DirectoryFast {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $robocopy = (Get-Command 'robocopy.exe' -ErrorAction Stop).Source
    & $robocopy $Source $Destination /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /XJ /NFL /NDL /NJH /NJS /NP `
        /XD site-packages __pycache__ /XF *.pyc *.pyo
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed with exit code $LASTEXITCODE while copying $Source"
    }
}

$pythonCache = Join-Path $RepoRoot 'artifacts\runtime-cache\python312-full'
if (-not $SkipPython -and -not (Test-Path -LiteralPath $pythonCache -PathType Container)) {
    Write-Host 'Preparing Python 3.12 runtime cache...'
    $python = Resolve-SystemPython312
    Copy-DirectoryFast -Source $python.Root -Destination $pythonCache
    & (Join-Path $pythonCache 'python.exe') -s -c 'import pyexpat, ssl, sqlite3'
    Assert-True -Condition ($LASTEXITCODE -eq 0) -Message 'Python runtime cache has an incomplete standard library.'
    Write-Host "Python cache ready at $pythonCache"
} elseif ($SkipPython) {
    Write-Host 'Skipping Python cache preparation.'
} else {
    Write-Host "Python cache already exists at $pythonCache"
}

$platformTools = Join-Path $RepoRoot 'artifacts\android-platform-tools'
if (-not $SkipPlatformTools -and -not (Test-Path -LiteralPath (Join-Path $platformTools 'adb.exe') -PathType Leaf)) {
    Write-Host 'Preparing Android platform-tools...'
    $artifactsRoot = Join-Path $RepoRoot 'artifacts'
    $zipPath = Join-Path $artifactsRoot 'platform-tools-latest-windows.zip'
    $extractRoot = Join-Path $artifactsRoot 'platform-tools-extract'
    New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
    if (Test-Path -LiteralPath $extractRoot -PathType Container) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
    Invoke-WebRequest `
        -Uri 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' `
        -OutFile $zipPath `
        -UseBasicParsing
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force
    $extracted = Join-Path $extractRoot 'platform-tools'
    Assert-True -Condition (Test-Path -LiteralPath (Join-Path $extracted 'adb.exe') -PathType Leaf) -Message 'platform-tools archive is missing adb.exe.'
    Move-Item -LiteralPath $extracted -Destination $platformTools
    Write-Host "Android platform-tools ready at $platformTools"
} elseif ($SkipPlatformTools) {
    Write-Host 'Skipping Android platform-tools preparation.'
} else {
    Write-Host "Android platform-tools already exist at $platformTools"
}

$doubaoCache = Join-Path $env:USERPROFILE '.codex\computer-use\doubao-computer-use-local'
$doubaoSource = Join-Path $RepoRoot 'tools\doubao-computer-use\original-source\computer_use'
if (-not $SkipDoubao) {
    $uvCommand = Get-Command 'uv.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $uvCommand) {
        Write-Host 'Installing uv...'
        $python = Resolve-SystemPython312
        & $python.Executable -m pip install --disable-pip-version-check --quiet uv
        Assert-True -Condition ($LASTEXITCODE -eq 0) -Message 'uv installation failed.'
        $uvCommand = Get-Command 'uv.exe' -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $uvCommand) {
            $pythonRoot = Split-Path -Parent $python.Executable
            foreach ($candidate in @(
                (Join-Path $pythonRoot 'Scripts\uv.exe'),
                (Join-Path $pythonRoot 'uv.exe'))) {
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    $uvCommand = Get-Item -LiteralPath $candidate
                    break
                }
            }
        }
    }
    Assert-True -Condition ($null -ne $uvCommand) -Message 'uv is not available after installation.'

    foreach ($service in @('tool_server', 'mcp_server', 'planner')) {
        $venvRoot = Join-Path $doubaoCache "$service\.venv"
        $serviceDir = Join-Path $doubaoSource $service
        if (Test-Path -LiteralPath (Join-Path $venvRoot 'pyvenv.cfg') -PathType Leaf) {
            Write-Host "Doubao venv already exists for $service"
            continue
        }
        Write-Host "Preparing Doubao venv for $service..."
        & $uvCommand.Source venv $venvRoot --python 3.12
        Assert-True -Condition ($LASTEXITCODE -eq 0) -Message "uv venv failed for $service"
        $venvPython = Join-Path $venvRoot 'Scripts\python.exe'
        & $uvCommand.Source pip install --python $venvPython $serviceDir
        Assert-True -Condition ($LASTEXITCODE -eq 0) -Message "uv pip install failed for $service"
        & $venvPython -c 'import fastapi' 2>$null
    }
    Write-Host "Doubao venvs ready under $doubaoCache"
} else {
    Write-Host 'Skipping Doubao venv preparation.'
}

[pscustomobject]@{
    Success = $true
    PythonCache = $pythonCache
    PlatformTools = $platformTools
    DoubaoCache = $doubaoCache
} | ConvertTo-Json -Depth 4
