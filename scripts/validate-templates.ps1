param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
$TemplatesRoot = Join-Path $RepoRoot 'templates'
$BuildRoot = Join-Path $RepoRoot 'artifacts\template-build'

Set-Location -LiteralPath $RepoRoot
New-Item -ItemType Directory -Path $BuildRoot -Force | Out-Null

& dotnet run --project src\MyPowerTools.Cli -- validate templates
if ($LASTEXITCODE -ne 0) {
    throw "Template manifest validation failed with exit code $LASTEXITCODE"
}

& dotnet run --project src\MyPowerTools.Cli -- ui check templates
if ($LASTEXITCODE -ne 0) {
    throw "Template UI validation failed with exit code $LASTEXITCODE"
}

$dotnetProjects = @(
    'templates\dotnet-inproc-module\Sample.DotNetInProc.MyPowerTools.csproj',
    'templates\dotnet-grpc-sidecar-module\Sample.DotNetGrpcSidecar.MyPowerTools.csproj'
)

foreach ($project in $dotnetProjects) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $outputDir = Join-Path $BuildRoot $projectName
    & dotnet build $project -c Release -o $outputDir
    if ($LASTEXITCODE -ne 0) {
        throw "Template project build failed for $project with exit code $LASTEXITCODE"
    }
}

$pythonCommand = Get-Command python -ErrorAction SilentlyContinue
$pythonExe = $pythonCommand?.Source
$pythonPrefix = @()
if (-not $pythonExe) {
    $pyCommand = Get-Command py -ErrorAction SilentlyContinue
    if ($pyCommand) {
        $pythonExe = $pyCommand.Source
        $pythonPrefix = @('-3')
    }
}

if (-not $pythonExe) {
    throw 'Python was not found. Install Python or expose python/py on PATH to validate Python templates.'
}

$pythonFiles = @(
    'templates\python-grpc-sidecar-module\module_service.py',
    'templates\stdio-compat-module\module_server.py',
    'templates\http-facade-module\server.py',
    'templates\webview-module\server.py'
)

foreach ($pythonFile in $pythonFiles) {
    $arguments = @()
    $arguments += $pythonPrefix
    $arguments += @('-m', 'py_compile', $pythonFile)
    & $pythonExe @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Python template compile failed for $pythonFile with exit code $LASTEXITCODE"
    }
}

$templateBuildDirectories = @(
    'templates\dotnet-inproc-module\bin',
    'templates\dotnet-inproc-module\obj',
    'templates\dotnet-grpc-sidecar-module\bin',
    'templates\dotnet-grpc-sidecar-module\obj'
)

foreach ($relativeDirectory in $templateBuildDirectories) {
    $directory = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $relativeDirectory))
    if ($directory.StartsWith($TemplatesRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $directory)) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}

Write-Host 'MyPowerTools template validation passed.'
