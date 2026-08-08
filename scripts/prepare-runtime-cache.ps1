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

$pythonCache = Join-Path $RepoRoot 'artifacts\runtime-cache\python312-embed'
if (-not $SkipPython -and -not (Test-Path -LiteralPath (Join-Path $pythonCache 'python.exe') -PathType Leaf)) {
    Write-Host 'Preparing Python 3.12 embeddable runtime cache...'
    $artifactsRoot = Join-Path $RepoRoot 'artifacts'
    New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
    $pythonEmbeddedVersion = '3.12.10'
    $pythonZip = Join-Path $artifactsRoot "python-$pythonEmbeddedVersion-embed-amd64.zip"
    $pythonExtract = Join-Path $artifactsRoot 'python-embed-extract'
    if (Test-Path -LiteralPath $pythonExtract -PathType Container) {
        Remove-Item -LiteralPath $pythonExtract -Recurse -Force
    }
    Invoke-WebRequest `
        -Uri "https://www.python.org/ftp/python/$pythonEmbeddedVersion/python-$pythonEmbeddedVersion-embed-amd64.zip" `
        -OutFile $pythonZip `
        -UseBasicParsing
    Expand-Archive -LiteralPath $pythonZip -DestinationPath $pythonExtract -Force
    New-Item -ItemType Directory -Path $pythonCache -Force | Out-Null
    Move-Item -Path (Join-Path $pythonExtract '*') -Destination $pythonCache -Force
    Remove-Item -LiteralPath $pythonExtract -Recurse -Force

    $pthPath = Join-Path $pythonCache 'python312._pth'
    $pthLines = [IO.File]::ReadAllLines($pthPath)
    for ($i = 0; $i -lt $pthLines.Length; $i++) {
        if ($pthLines[$i].Trim() -eq '#import site') {
            $pthLines[$i] = 'import site'
        }
    }
    [IO.File]::WriteAllLines(
        $pthPath,
        $pthLines + @('Lib\site-packages'),
        [Text.Encoding]::ASCII)

    & (Join-Path $pythonCache 'python.exe') -s -c 'import pyexpat, ssl, sqlite3'
    Assert-True -Condition ($LASTEXITCODE -eq 0) -Message 'Python runtime cache has an incomplete standard library.'
    Write-Host "Python cache ready at $pythonCache"
} elseif ($SkipPython) {
    Write-Host 'Skipping Python cache preparation.'
} else {
    Write-Host "Python embeddable cache already exists at $pythonCache"
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

    $sharedVenvRoot = Join-Path $doubaoCache '.venv'
    $venvPython = Join-Path $sharedVenvRoot 'Scripts\python.exe'
    if (-not (Test-Path -LiteralPath (Join-Path $sharedVenvRoot 'pyvenv.cfg') -PathType Leaf)) {
        Write-Host 'Preparing one shared Doubao venv...'
        & $uvCommand.Source venv $sharedVenvRoot --python 3.12
        Assert-True -Condition ($LASTEXITCODE -eq 0) -Message 'uv venv failed for the shared Doubao venv'
        foreach ($service in @('tool_server', 'planner')) {
            $serviceDir = Join-Path $doubaoSource $service
            $pyprojectPath = Join-Path $serviceDir 'pyproject.toml'
            $pyproject = [IO.File]::ReadAllText($pyprojectPath, [Text.Encoding]::UTF8)
            $dependenciesMatch = [regex]::Match(
                $pyproject,
                '(?s)dependencies\s*=\s*\[(.*?)\]')
            Assert-True -Condition $dependenciesMatch.Success -Message "pyproject dependencies block not found for $service"
            $dependencyStrings = [regex]::Matches(
                $dependenciesMatch.Groups[1].Value,
                '"([^"]+)"') |
                ForEach-Object { $_.Groups[1].Value }
            Assert-True -Condition ($dependencyStrings.Count -gt 0) -Message "pyproject has no dependencies for $service"
            & $uvCommand.Source pip install --python $venvPython @dependencyStrings
            Assert-True -Condition ($LASTEXITCODE -eq 0) -Message "uv pip install failed for $service"
        }
        & $uvCommand.Source pip install --python $venvPython (Join-Path $doubaoSource 'mcp_server')
        Assert-True -Condition ($LASTEXITCODE -eq 0) -Message 'uv pip install failed for mcp_server'
    }
    foreach ($service in @('tool_server', 'mcp_server', 'planner')) {
        $legacyVenv = Join-Path $doubaoCache "$service\.venv"
        if (Test-Path -LiteralPath $legacyVenv -PathType Container) {
            try {
                Remove-Item -LiteralPath $legacyVenv -Recurse -Force -ErrorAction Stop
            }
            catch {
                Write-Warning "Legacy Doubao venv cleanup skipped ($($_.Exception.Message))."
            }
        }
    }
    & $venvPython -c 'import fastapi, mcp, openai, pyautogui'
    Assert-True -Condition ($LASTEXITCODE -eq 0) -Message 'Shared Doubao venv imports failed'
    Assert-True -Condition (Test-Path -LiteralPath (Join-Path $sharedVenvRoot 'Scripts\mcp-server.exe') -PathType Leaf) -Message 'mcp-server.exe is missing from the shared venv'
    Write-Host "Shared Doubao venv ready at $sharedVenvRoot"
} else {
    Write-Host 'Skipping Doubao venv preparation.'
}

[pscustomobject]@{
    Success = $true
    PythonCache = $pythonCache
    PlatformTools = $platformTools
    DoubaoCache = $doubaoCache
} | ConvertTo-Json -Depth 4
