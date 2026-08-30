#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('inspect', 'find', 'copy-selected', 'close-selected', 'find-and-close', 'build')]
    [string] $Command,

    [string] $Type = 'File',
    [string] $Query = '',
    [int] $TimeoutSeconds = 180,
    [switch] $Yes
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\si-uia\SiUiaHost.csproj'
$exe = Join-Path $PSScriptRoot '..\si-uia\bin\Release\net10.0-windows\SiUiaHost.exe'

if ($Command -eq 'build' -or -not (Test-Path -LiteralPath $exe)) {
    Write-Host "Building SiUiaHost..."
    $buildArgs = @(
        'build'
        '-nologo'
        '-v:q'
        '-c'
        'Release'
        $project
    )
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
}

if ($Command -eq 'build') {
    return
}

if (-not (Test-Path -LiteralPath $exe)) {
    throw "SiUiaHost.exe was not produced at $exe"
}

$nativeArgs = @($Command)
if ($Command -in @('find', 'find-and-close')) {
    $nativeArgs += @('--type', $Type, '--query', $Query, '--timeout', "$TimeoutSeconds")
}
if ($Command -eq 'close-selected') {
    $nativeArgs += @('--timeout', "$TimeoutSeconds")
}
if ($Yes -and $Command -in @('close-selected', 'find-and-close')) {
    $nativeArgs += '--yes'
}

& $exe @nativeArgs
exit $LASTEXITCODE
