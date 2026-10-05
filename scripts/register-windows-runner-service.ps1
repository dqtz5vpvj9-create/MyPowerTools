[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [string]$CliPath = '',
    [switch]$RegisterOnly
)
$ErrorActionPreference = 'Stop'
try {
    if (-not $CliPath) { $CliPath = Join-Path $InstallRoot 'Cli\MyPowerTools.Cli.exe' }
    $runner = Join-Path $InstallRoot 'Runner\MyPowerTools.Runner.exe'
    if (-not (Test-Path -LiteralPath $runner)) { throw "Installed Runner missing: $runner" }
    $units = Join-Path $DataRoot 'ServiceManager\units'
    New-Item -ItemType Directory -Path $units -Force | Out-Null
    $path = Join-Path $units 'mpt-runner.service.json'
    # Match the canonical launch arguments so ServiceManager adopts an existing
    # Runner before starting anything. Never create a second data owner.
    $manifest = @{
        id='mpt-runner.service'; toolId='host'; displayName='MyPowerTools Background Runner'
        exec=$runner; arguments=@('--modules',(Join-Path $InstallRoot 'modules'),'--data-root',$DataRoot)
        workingDirectory=$InstallRoot; autostart=$true
        restartPolicy=@{maxRestarts=5;backoffMs=2000}
        # HostControl speaks authenticated gRPC, not the unit readiness JSON protocol.
        # Verify business readiness through the CLI after registration.
        readiness=@{kind='none'}; stopTimeoutMs=10000
        dataRoots=@($DataRoot); dependsOn=@(); instanceToken='mpt-background-runner-v1'
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$path.new" -Encoding utf8
    Move-Item -LiteralPath "$path.new" -Destination $path -Force
    if (-not $RegisterOnly) {
        $previous = $env:MPT_DATA_ROOT
        try {
            $env:MPT_DATA_ROOT = $DataRoot
            & $CliPath service reload
            if ($LASTEXITCODE -ne 0) { throw 'ServiceManager reload failed. Start the installed ServiceManager first.' }
            & $CliPath service status mpt-runner.service
            if ($LASTEXITCODE -ne 0) { throw 'Runner unit status query failed.' }
        } finally { $env:MPT_DATA_ROOT = $previous }
    }
} catch { Write-Error $_; exit 1 }
