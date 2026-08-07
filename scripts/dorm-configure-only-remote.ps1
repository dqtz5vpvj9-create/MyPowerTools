[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [string]$ConfigureScript = '',
    [string]$ResultPath = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $ResultPath = Join-Path $env:TEMP 'mpt-configure-result.json'
}
$logPath = Join-Path $env:TEMP 'mpt-configure.log'
if ([string]::IsNullOrWhiteSpace($ConfigureScript)) {
    $ConfigureScript = Join-Path $InstallRoot 'configure-user-services.ps1'
}

try {
    $output = @(
        & $ConfigureScript `
            -Mode Install `
            -InstallRoot $InstallRoot `
            -DataRoot $DataRoot 2>&1 |
            ForEach-Object { [string]$_ }
    )
    $result = [ordered]@{
        success = $LASTEXITCODE -eq 0
        exitCode = $LASTEXITCODE
        output = $output -join [Environment]::NewLine
    }
}
catch {
    $result = [ordered]@{
        success = $false
        error = $_.Exception.Message
        errorType = $_.Exception.GetType().FullName
        stack = $_.ScriptStackTrace
        inner = if ($null -ne $_.Exception.InnerException) { $_.Exception.InnerException.Message } else { '' }
        position = if ($null -ne $_.InvocationInfo) { $_.InvocationInfo.PositionMessage } else { '' }
        scriptName = if ($null -ne $_.InvocationInfo) { $_.InvocationInfo.ScriptName } else { '' }
        line = if ($null -ne $_.InvocationInfo) { $_.InvocationInfo.Line } else { '' }
    }
}

[IO.File]::WriteAllText(
    $ResultPath,
    ($result | ConvertTo-Json -Depth 5),
    [Text.UTF8Encoding]::new($false))

if ([bool]$result.success) {
    exit 0
}
exit 1
