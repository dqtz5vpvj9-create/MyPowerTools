[CmdletBinding()]
param(
    [string]$CliDirectory,
    [string]$ServerDirectory,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools\TransferMcp'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [string]$Python = 'python',
    [int]$Port = 17843,
    [switch]$RegisterCodex
)
$ErrorActionPreference = 'Stop'
try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    if (-not $CliDirectory) { $CliDirectory = Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools\Cli' }
    $cli = Join-Path $CliDirectory 'MyPowerTools.Cli.exe'
    if (-not (Test-Path -LiteralPath $cli)) { throw "Installed CLI missing: $cli" }
    & (Join-Path $PSScriptRoot 'register-windows-runner-service.ps1') -CliPath $cli -DataRoot $DataRoot
    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    # This directory contains the HTTP bearer capability and client configuration.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($identity)
    $acl.SetAccessRuleProtection($true,$false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $InstallRoot -AclObject $acl
    $tokenFile = Join-Path $InstallRoot 'http.token'
    if (-not (Test-Path -LiteralPath $tokenFile)) {
        [IO.File]::WriteAllText($tokenFile,[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    }
    if (-not $ServerDirectory) {
        $ServerDirectory = Join-Path $InstallRoot 'staging'
        & dotnet publish (Join-Path $repoRoot 'src\MyPowerTools.TransferMcp\MyPowerTools.TransferMcp.csproj') -c Release -r win-x64 --self-contained true -o $ServerDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Shared MCP publish failed.' }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $ServerDirectory 'MyPowerTools.TransferMcp.exe'))) { throw 'Published shared MCP executable missing.' }
    $units = Join-Path $DataRoot 'ServiceManager\units'
    $unitFile = Join-Path $units 'mpt-transfer-mcp.service.json'
    $previous = $env:MPT_DATA_ROOT
    try {
        $env:MPT_DATA_ROOT = $DataRoot
        if (Test-Path -LiteralPath $unitFile) {
            & $cli service stop mpt-transfer-mcp.service
            if ($LASTEXITCODE -ne 0) { throw 'Could not stop the existing MCP service for update.' }
        }
        $server = Join-Path $InstallRoot 'Server'
        New-Item -ItemType Directory -Force -Path $server | Out-Null
        Get-ChildItem -LiteralPath $ServerDirectory | Copy-Item -Destination $server -Recurse -Force
        $manifest = @{
            id='mpt-transfer-mcp.service';toolId='host';displayName='MyPowerTools Shared File Transfer MCP'
            exec=(Join-Path $server 'MyPowerTools.TransferMcp.exe');arguments=@();workingDirectory=$server
            environment=@{MPT_DATA_ROOT=$DataRoot;MPT_MCP_TOKEN_FILE=$tokenFile;MPT_MCP_PORT=[string]$Port}
            autostart=$true;restartPolicy=@{maxRestarts=5;backoffMs=2000};readiness=@{kind='none'}
            stopTimeoutMs=10000;dataRoots=@($DataRoot);dependsOn=@('mpt-runner.service');instanceToken='mpt-transfer-mcp-http-v1'
        }
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$unitFile.new" -Encoding utf8
        Move-Item -LiteralPath "$unitFile.new" -Destination $unitFile -Force
        & $cli service reload
        if ($LASTEXITCODE -ne 0) { throw 'ServiceManager reload failed.' }
        & $cli service start mpt-transfer-mcp.service
        if ($LASTEXITCODE -ne 0) { throw 'Shared MCP service start failed.' }
    } finally { $env:MPT_DATA_ROOT = $previous }
    $ready = $false
    for ($attempt=0;$attempt -lt 30;$attempt++) {
        try {
            $headers=@{Authorization='Bearer '+[IO.File]::ReadAllText($tokenFile).Trim()}
            $health=Invoke-RestMethod "http://127.0.0.1:$Port/health" -Headers $headers -TimeoutSec 2 -NoProxy
            if($health.status -eq 'ready') {$ready=$true;break}
        } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) { throw 'Shared MCP endpoint did not become ready; client configuration was not changed.' }
    $configArgs=@((Join-Path $repoRoot 'integrations\file-transfer-mcp\configure_http.py'),'--token-file',$tokenFile,'--output',(Join-Path $InstallRoot 'mcp.json'),'--port',[string]$Port)
    if ($RegisterCodex) { $configArgs += '--register-codex' }
    & $Python @configArgs
    if ($LASTEXITCODE -ne 0) { throw 'HTTP MCP configuration failed.' }
    & $Python (Join-Path $repoRoot 'integrations\file-transfer-mcp\install_skill.py')
    if ($LASTEXITCODE -ne 0) { throw 'Agent skill installation failed.' }
    Write-Output "Shared MCP ready; PID $($health.processId), no per-chat Python or CLI processes."
} catch { Write-Error $_; exit 1 }
