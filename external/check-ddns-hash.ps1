$ErrorActionPreference = 'Stop'
$manifest = Get-Content 'C:\Users\lixinrui\AppData\Local\MyPowerTools\ota-state\installed-files.manifest.json' -Raw | ConvertFrom-Json
$entry = $manifest.files | Where-Object { $_.path -eq 'service-units/ddns.service/bin/ddns.ps1' } | Select-Object -First 1
Write-Output ("manifest: " + $entry.sha256)
Write-Output ("zip036  : " + (Get-FileHash 'C:\Temp\mpt-036\service-units\ddns.service\bin\ddns.ps1' -Algorithm SHA256).Hash.ToLowerInvariant())
Write-Output ("current : " + (Get-FileHash 'C:\Users\lixinrui\AppData\Local\Programs\MyPowerTools\service-units\ddns.service\bin\ddns.ps1' -Algorithm SHA256).Hash.ToLowerInvariant())
