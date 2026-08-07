[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,

    [string]$PythonRuntimeSource = '',
    [string]$DoubaoVenvSource = '',
    [string]$PlatformToolsSource = 'D:\AndroidSDK\platform-tools'
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PythonRuntimeSource)) {
    $PythonRuntimeSource = Join-Path $RepoRoot 'artifacts\runtime-cache\python312-full'
}
if ([string]::IsNullOrWhiteSpace($DoubaoVenvSource)) {
    $DoubaoVenvSource = Join-Path $env:USERPROFILE '.codex\computer-use\doubao-computer-use-local'
}
$DoubaoSourceRoot = Join-Path $RepoRoot 'tools\doubao-computer-use\original-source\computer_use'
$SmartBirdSourceRoot = Join-Path $RepoRoot 'tools\smartbird-thermostat\original-source'

function Resolve-RequiredDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label directory is missing: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Assert-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label file is missing: $Path"
    }
}

function Test-PythonRuntimeStandardLibrary {
    param([Parameter(Mandatory = $true)][string]$PythonExecutable)

    & $PythonExecutable -s -c 'import pyexpat, ssl, sqlite3' 2>$null
    return $LASTEXITCODE -eq 0
}

function Resolve-SystemPython312 {
    $launcher = (Get-Command 'py.exe' -ErrorAction Stop).Source
    $resolveArguments = @('-3.12', '-s', '-c', 'import sys; print(sys.prefix)')
    $prefixLines = @(& $launcher @resolveArguments)
    $resolveExitCode = $LASTEXITCODE
    $prefix = ($prefixLines | Select-Object -First 1).Trim()
    if ($resolveExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($prefix)) {
        throw 'A working system Python 3.12 installation is required to stage the bundled runtime.'
    }

    $python = Join-Path $prefix 'python.exe'
    Assert-RequiredFile -Path $python -Label 'System Python 3.12 entrypoint'
    if (-not (Test-PythonRuntimeStandardLibrary -PythonExecutable $python)) {
        throw "System Python 3.12 has an incomplete standard library: $prefix"
    }

    return [pscustomobject]@{
        Root = $prefix
        Executable = $python
        Launcher = $launcher
    }
}

function Copy-DirectoryFast {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string[]]$ExcludedDirectories = @(),
        [string[]]$ExcludedFiles = @()
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $robocopy = (Get-Command 'robocopy.exe' -ErrorAction Stop).Source
    $copyArguments = @(
        $Source,
        $Destination,
        '/E',
        '/COPY:DAT',
        '/DCOPY:DAT',
        '/R:1',
        '/W:1',
        '/XJ',
        '/NFL',
        '/NDL',
        '/NJH',
        '/NJS',
        '/NP'
    )

    if ($ExcludedDirectories.Count -gt 0) {
        $copyArguments += '/XD'
        $copyArguments += $ExcludedDirectories
    }
    if ($ExcludedFiles.Count -gt 0) {
        $copyArguments += '/XF'
        $copyArguments += $ExcludedFiles
    }

    & $robocopy @copyArguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ge 8) {
        throw "robocopy failed with exit code $exitCode while copying $Source to $Destination"
    }
}

$publishRootFull = [System.IO.Path]::GetFullPath($PublishRoot)
if (-not (Test-Path -LiteralPath $publishRootFull -PathType Container)) {
    throw "Publish root is missing: $publishRootFull"
}

if ([string]::IsNullOrWhiteSpace($PlatformToolsSource) -or
    -not (Test-Path -LiteralPath $PlatformToolsSource -PathType Container)) {
    $candidatePlatformTools = Join-Path $RepoRoot 'artifacts\android-platform-tools'
    if (Test-Path -LiteralPath $candidatePlatformTools -PathType Container) {
        $PlatformToolsSource = $candidatePlatformTools
    }
}

$runtimesRoot = [System.IO.Path]::GetFullPath((Join-Path $publishRootFull 'Runtimes'))
$toolsRoot = [System.IO.Path]::GetFullPath((Join-Path $publishRootFull 'Tools'))
$publishPrefix = $publishRootFull
if (-not $publishPrefix.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
    $publishPrefix += [System.IO.Path]::DirectorySeparatorChar
}
if (-not $runtimesRoot.StartsWith($publishPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Runtime staging path must stay inside the publish root: $runtimesRoot"
}
if (-not $toolsRoot.StartsWith($publishPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Tool staging path must stay inside the publish root: $toolsRoot"
}

$pythonSource = Resolve-RequiredDirectory -Path $PythonRuntimeSource -Label 'Python 3.12 runtime'
$doubaoSource = Resolve-RequiredDirectory -Path $DoubaoSourceRoot -Label 'Doubao submodule source'
$doubaoVenvSource = Resolve-RequiredDirectory -Path $DoubaoVenvSource -Label 'Doubao virtual-environment cache'
$smartBirdSource = Resolve-RequiredDirectory -Path $SmartBirdSourceRoot -Label 'SmartBird submodule source'
$platformToolsRoot = Resolve-RequiredDirectory -Path $PlatformToolsSource -Label 'Android platform-tools'

Assert-RequiredFile -Path (Join-Path $pythonSource 'python.exe') -Label 'Python 3.12 runtime entrypoint'
Assert-RequiredFile -Path (Join-Path $platformToolsRoot 'adb.exe') -Label 'ADB entrypoint'

$buildPython = Resolve-SystemPython312
$pythonSourceExecutable = Join-Path $pythonSource 'python.exe'
if (-not (Test-PythonRuntimeStandardLibrary -PythonExecutable $pythonSourceExecutable)) {
    Write-Warning "Cached Python runtime is incomplete; using the working system Python 3.12 runtime from $($buildPython.Root)."
    $pythonSource = Resolve-RequiredDirectory -Path $buildPython.Root -Label 'System Python 3.12 runtime'
}

$doubaoServices = @('tool_server', 'mcp_server', 'planner')
foreach ($service in $doubaoServices) {
    Resolve-RequiredDirectory -Path (Join-Path $doubaoSource $service) -Label "$service submodule source" | Out-Null
    Assert-RequiredFile -Path (Join-Path $doubaoVenvSource "$service\.venv\pyvenv.cfg") -Label "$service virtual environment"
}

$smartBirdFiles = @(
    'test_tools\smartbird_thermostat.py',
    'test_tools\smartbird_thermostat_service.py',
    'test_tools\smartbird_thermostat_task.py',
    'test_tools\energy_server.py',
    'test_tools\energy_server_task.py',
    'test_tools\energy_control_impl.py',
    'test_tools\usbmeter_hid_backend.py',
    'py_modules\logging_lib.py',
    'py_modules\__init__.py',
    'requirements-energy-runtime.txt',
    'scripts\install-smartbird-thermostat-task.ps1',
    'scripts\install-energy-server-task.ps1'
)
foreach ($relativePath in $smartBirdFiles) {
    Assert-RequiredFile -Path (Join-Path $smartBirdSource $relativePath) -Label 'SmartBird submodule runtime'
}

if (Test-Path -LiteralPath $runtimesRoot) {
    Remove-Item -LiteralPath $runtimesRoot -Recurse -Force
}
if (Test-Path -LiteralPath $toolsRoot) {
    Remove-Item -LiteralPath $toolsRoot -Recurse -Force
}

$pythonDestination = Join-Path $runtimesRoot 'Python312'
$doubaoDestination = Join-Path $runtimesRoot 'Doubao'
$smartBirdDestination = Join-Path $runtimesRoot 'SmartBird'
$platformToolsDestination = Join-Path $toolsRoot 'AndroidPlatformTools'
New-Item -ItemType Directory -Path $doubaoDestination -Force | Out-Null
New-Item -ItemType Directory -Path $smartBirdDestination -Force | Out-Null

$pythonCopyParameters = @{
    Source = $pythonSource
    Destination = $pythonDestination
    ExcludedDirectories = @('site-packages', '__pycache__')
    ExcludedFiles = @('*.pyc', '*.pyo')
}
Copy-DirectoryFast @pythonCopyParameters

$pythonExecutable = Join-Path $pythonDestination 'python.exe'
$energyRequirements = Join-Path $smartBirdSource 'requirements-energy-runtime.txt'
$targetSitePackages = Join-Path $pythonDestination 'Lib\site-packages'
New-Item -ItemType Directory -Path $targetSitePackages -Force | Out-Null
$pipArguments = @(
    '-3.12', '-s', '-m', 'pip', '--isolated', 'install',
    '--disable-pip-version-check',
    '--no-warn-script-location',
    '--upgrade',
    '--target', $targetSitePackages,
    '--requirement', $energyRequirements
)
$buildPythonLauncher = $buildPython.Launcher
& $buildPythonLauncher @pipArguments
if ($LASTEXITCODE -ne 0) {
    throw "Failed to install SmartBird Energy Server runtime dependencies with system Python 3.12."
}
& $pythonExecutable -s -c 'import flask, psutil, win32gui, pywinauto'
if ($LASTEXITCODE -ne 0) {
    throw "Published Python runtime cannot import SmartBird Energy Server dependencies."
}

$doubaoExcludedDirectories = @(
    '.git',
    '.hg',
    '.svn',
    '.cache',
    '.mypy_cache',
    '.pytest_cache',
    '.ruff_cache',
    '.venv',
    '__pycache__',
    'log',
    'logs'
)
$doubaoExcludedFiles = @('*.log', '*.pyc', '*.pyo', '*.tmp')
foreach ($service in $doubaoServices) {
    $sourceCopyParameters = @{
        Source = Join-Path $doubaoSource $service
        Destination = Join-Path $doubaoDestination $service
        ExcludedDirectories = $doubaoExcludedDirectories
        ExcludedFiles = $doubaoExcludedFiles
    }
    Copy-DirectoryFast @sourceCopyParameters

    $venvCopyParameters = @{
        Source = Join-Path $doubaoVenvSource "$service\.venv"
        Destination = Join-Path $doubaoDestination "$service\.venv"
        ExcludedDirectories = @('__pycache__', '.cache')
        ExcludedFiles = $doubaoExcludedFiles
    }
    Copy-DirectoryFast @venvCopyParameters
}

foreach ($relativePath in $smartBirdFiles) {
    $sourcePath = Join-Path $smartBirdSource $relativePath
    $destinationPath = Join-Path $smartBirdDestination $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
}

Copy-DirectoryFast -Source $platformToolsRoot -Destination $platformToolsDestination

[ordered]@{
    runtimesRoot = $runtimesRoot
    python = $pythonDestination
    doubao = $doubaoDestination
    smartBird = $smartBirdDestination
    androidPlatformTools = $platformToolsDestination
    doubaoServices = $doubaoServices
    smartBirdFiles = $smartBirdFiles
    doubaoSource = $doubaoSource
    doubaoVenvCache = $doubaoVenvSource
    smartBirdSource = $smartBirdSource
    adb = Join-Path $platformToolsDestination 'adb.exe'
} | ConvertTo-Json -Depth 4
