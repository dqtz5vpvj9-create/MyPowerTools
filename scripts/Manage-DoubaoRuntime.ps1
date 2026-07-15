[CmdletBinding()]
param(
    [ValidateSet('start', 'restart', 'stop', 'status')]
    [string]$Action = 'status',

    [string]$InstallRoot = '',

    [string]$DataRoot = '',

    [string]$EnvironmentFile = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = $PSScriptRoot
}
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $env:LOCALAPPDATA 'MyPowerTools\Doubao'
}

$installRootFull = [System.IO.Path]::GetFullPath($InstallRoot)
$shell = Join-Path $installRootFull 'Shell\MyPowerTools.Shell.Avalonia.exe'
$runtimeRoot = Join-Path $installRootFull 'Runtimes\Doubao'
if (-not (Test-Path -LiteralPath $shell -PathType Leaf)) {
    throw "MyPowerTools Shell is missing: $shell"
}
if (-not (Test-Path -LiteralPath $runtimeRoot -PathType Container)) {
    throw "Doubao runtime is missing: $runtimeRoot"
}

$nativeArguments = @(
    '--doubao-runtime', $Action,
    '--doubao-runtime-root', $runtimeRoot,
    '--doubao-data-root', ([System.IO.Path]::GetFullPath($DataRoot))
)
if (-not [string]::IsNullOrWhiteSpace($EnvironmentFile)) {
    $nativeArguments += @('--doubao-env', ([System.IO.Path]::GetFullPath($EnvironmentFile)))
}

$argumentLine = ($nativeArguments | ForEach-Object {
    if ($_ -match '[\s"]') {
        '"' + $_.Replace('"', '\"') + '"'
    } else {
        $_
    }
}) -join ' '

$captureRoot = Join-Path ([System.IO.Path]::GetFullPath($DataRoot)) 'control'
New-Item -ItemType Directory -Path $captureRoot -Force | Out-Null
$captureId = [Guid]::NewGuid().ToString('N')
$stdoutPath = Join-Path $captureRoot "$captureId.stdout.txt"
$stderrPath = Join-Path $captureRoot "$captureId.stderr.txt"

$process = Start-Process `
    -FilePath $shell `
    -ArgumentList $argumentLine `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath `
    -WindowStyle Hidden `
    -PassThru

$null = $process.Handle
$process.WaitForExit()
$exitCode = $process.ExitCode

$stdout = ''
$stderr = ''
try {
    if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
        $stdout = [System.IO.File]::ReadAllText($stdoutPath)
    }
} catch [System.IO.IOException] {
    # Long-running services can inherit the redirected handle from the short-lived
    # controller process. The exit code remains authoritative for start/restart.
}
try {
    if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
        $stderr = [System.IO.File]::ReadAllText($stderrPath)
    }
} catch [System.IO.IOException] {
}

if (-not [string]::IsNullOrEmpty($stdout)) {
    [Console]::Out.Write($stdout)
}

Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
if ($exitCode -ne 0) {
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        [Console]::Error.Write($stderr)
    }
    throw "Doubao runtime '$Action' failed with exit code $exitCode."
}
