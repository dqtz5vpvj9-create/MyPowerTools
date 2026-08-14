$ErrorActionPreference = 'Stop'

$installRoot = 'C:\Users\lixinrui\AppData\Local\Programs\MyPowerTools'
$dataRoot = 'C:\Users\lixinrui\AppData\Local\MyPowerTools'
$otaState = Join-Path $dataRoot 'ota-state'
$packageRoot = 'C:\Temp\mpt-038'

New-Item -ItemType Directory -Path $otaState -Force | Out-Null

$manifestSource = Join-Path $packageRoot 'MyPowerTools-win-x64.manifest.json'
if (-not (Test-Path -LiteralPath $manifestSource -PathType Leaf)) {
    throw "Missing package manifest: $manifestSource"
}
Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $otaState 'installed-files.manifest.json') -Force

$publicKeySource = Join-Path $packageRoot 'ota-signing-public-key.txt'
if (Test-Path -LiteralPath $publicKeySource -PathType Leaf) {
    Copy-Item -LiteralPath $publicKeySource -Destination (Join-Path $otaState 'ota-signing-public-key.txt') -Force
}

$manifestHash = (Get-FileHash -LiteralPath $manifestSource -Algorithm SHA256).Hash.ToLowerInvariant()
$installedRelease = [ordered]@{
    schemaVersion = 1
    product = 'MyPowerTools'
    version = '0.3.8'
    channel = 'stable'
    installedAt = (Get-Date).ToString('O')
    installDir = $installRoot
    dataRoot = $dataRoot
    repository = 'https://github.com/dqtz5vpvj9-create/MyPowerTools'
    manifestPath = 'installed-files.manifest.json'
    manifestSha256 = $manifestHash
    packageKind = 'full'
}
$installedRelease | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $otaState 'installed-release.json') -Encoding UTF8

$installManifest = [ordered]@{
    product = 'MyPowerTools'
    version = '0.3.8'
    installedAt = (Get-Date).ToString('O')
    packageRoot = $packageRoot
    installDir = $installRoot
    dataRoot = $dataRoot
    runner = Join-Path $installRoot 'Runner\MyPowerTools.Runner.exe'
    shell = Join-Path $installRoot 'Shell\MyPowerTools.Shell.Avalonia.exe'
    cli = Join-Path $installRoot 'Cli\MyPowerTools.Cli.exe'
    broker = Join-Path $installRoot 'Broker\MyPowerTools.ElevatedBroker.exe'
    app = Join-Path $installRoot 'MyPowerTools.exe'
    autostart = $true
}
$installManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $installRoot 'install.manifest.json') -Encoding UTF8

Write-Output "installed-release.json manifestSha256: $manifestHash"
Write-Output "Cli exists: $(Test-Path (Join-Path $installRoot 'Cli\MyPowerTools.Cli.exe'))"
Write-Output "ota-update exists: $(Test-Path (Join-Path $installRoot 'ota-update.ps1'))"
