[CmdletBinding()]
param(
    [string]$CliDirectory,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools\TransferMcp'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [string]$Python = 'python',
    [switch]$RegisterCodex
)
$ErrorActionPreference = 'Stop'
try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    if (-not $CliDirectory) {
        $CliDirectory = Join-Path $InstallRoot 'CLI'
        & dotnet publish (Join-Path $repoRoot 'src\MyPowerTools.Cli\MyPowerTools.Cli.csproj') -c Debug -r win-x64 --self-contained false -o $CliDirectory
        if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
    }
    $cli = Join-Path $CliDirectory 'MyPowerTools.Cli.exe'
    if (-not (Test-Path -LiteralPath $cli)) { throw "CLI missing: $cli" }
    & (Join-Path $PSScriptRoot 'register-windows-runner-service.ps1') -CliPath $cli -DataRoot $DataRoot
    Copy-Item -LiteralPath (Join-Path $repoRoot 'integrations\file-transfer-mcp\server.py') -Destination $InstallRoot -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'integrations\file-transfer-mcp\requirements.txt') -Destination $InstallRoot -Force
    $venv = Join-Path $InstallRoot 'venv'
    if (-not (Test-Path -LiteralPath "$venv\Scripts\python.exe")) {
        & $Python -m venv $venv
        if ($LASTEXITCODE -ne 0) { throw 'Python environment creation failed.' }
    }
    & "$venv\Scripts\python.exe" -m pip install --disable-pip-version-check -r "$InstallRoot\requirements.txt"
    if ($LASTEXITCODE -ne 0) { throw 'MCP dependency installation failed.' }
    # pythonw prevents a console window; the MCP client supplies stdio pipes.
    $entry = [ordered]@{
        command = "$venv\Scripts\pythonw.exe"
        args = @("$InstallRoot\server.py")
        env = @{ MPT_COMMAND_JSON = (ConvertTo-Json -InputObject @($cli) -Compress); MPT_DATA_ROOT = $DataRoot }
    }
    @{mcpServers=@{'mpt-file-transfer'=$entry}} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$InstallRoot\mcp.json" -Encoding utf8
    if ($RegisterCodex) {
        & codex mcp add mpt-file-transfer --env "MPT_COMMAND_JSON=$($entry.env.MPT_COMMAND_JSON)" --env "MPT_DATA_ROOT=$DataRoot" -- $entry.command @($entry.args)
        if ($LASTEXITCODE -ne 0) { throw 'Codex MCP registration failed.' }
    }
    & $cli transfer devices --data-root $DataRoot --json
    if ($LASTEXITCODE -ne 0) { throw 'MCP is installed, but the Runner file-transfer module is not ready. Repair the development runtime before accepting this deployment.' }
    Write-Output "MCP configuration: $InstallRoot\mcp.json"
} catch { Write-Error $_; exit 1 }
