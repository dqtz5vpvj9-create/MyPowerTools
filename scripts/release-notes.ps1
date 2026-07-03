param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ArtifactsRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release'),
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
$ArtifactsRoot = [System.IO.Path]::GetFullPath($ArtifactsRoot)
$ChangelogPath = Join-Path $RepoRoot 'CHANGELOG.md'
$ZipPath = Join-Path $ArtifactsRoot 'MyPowerTools-win-x64.zip'
$OutputPath = Join-Path $ArtifactsRoot 'RELEASE_NOTES.md'

New-Item -ItemType Directory -Path $ArtifactsRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $ChangelogPath)) {
    throw "CHANGELOG.md was not found at $ChangelogPath"
}

$changelog = Get-Content -LiteralPath $ChangelogPath
if ([string]::IsNullOrWhiteSpace($Version)) {
    $heading = $changelog | Where-Object { $_ -match '^##\s+(.+)$' } | Select-Object -First 1
    if ($heading -match '^##\s+(.+)$') {
        $Version = $Matches[1].Trim()
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = 'local'
}

$notes = New-Object System.Collections.Generic.List[string]
$inSection = $false
foreach ($line in $changelog) {
    if ($line -match '^##\s+(.+)$') {
        if ($inSection) {
            break
        }

        $inSection = ($Matches[1].Trim() -eq $Version)
        continue
    }

    if ($inSection -and -not [string]::IsNullOrWhiteSpace($line)) {
        $notes.Add($line)
    }
}

if ($notes.Count -eq 0) {
    $notes.Add('- No changelog entries were found for this version.')
}

$zipHash = ''
$zipSize = ''
if (Test-Path -LiteralPath $ZipPath) {
    $zipItem = Get-Item -LiteralPath $ZipPath
    $zipHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
    $zipSize = $zipItem.Length.ToString()
}

$releaseNotes = New-Object System.Collections.Generic.List[string]
$releaseNotes.Add("# MyPowerTools $Version Release Notes")
$releaseNotes.Add('')
$releaseNotes.Add("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
$releaseNotes.Add('')
$releaseNotes.Add('## Artifacts')
$releaseNotes.Add('')
if ($zipHash) {
    $releaseNotes.Add('- Windows portable zip: `artifacts/release/MyPowerTools-win-x64.zip`')
    $releaseNotes.Add('- Module templates: `templates/` inside the zip root')
    $releaseNotes.Add('- Portable installer script: `install-windows.ps1` inside the zip root')
    $releaseNotes.Add('- Portable uninstaller script: `uninstall-windows.ps1` inside the zip root')
    $releaseNotes.Add("- SHA256: ``$zipHash``")
    $releaseNotes.Add("- Size: $zipSize bytes")
} else {
    $releaseNotes.Add('- Windows portable zip has not been produced yet.')
}
$releaseNotes.Add('')
$releaseNotes.Add('## Changes')
$releaseNotes.Add('')
foreach ($line in $notes) {
    $releaseNotes.Add($line)
}
$releaseNotes.Add('')
$releaseNotes.Add('## Verification')
$releaseNotes.Add('')
$releaseNotes.Add('- `dotnet restore MyPowerTools.slnx`')
$releaseNotes.Add('- `dotnet build MyPowerTools.slnx --no-restore`')
$releaseNotes.Add('- `dotnet test MyPowerTools.slnx --no-build`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Cli -- validate modules`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Cli -- validate contracts`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Cli -- package sign-local modules`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Cli -- package trust modules --strict`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Cli -- ui check modules`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Cli -- module list --include-disabled`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Cli -- diagnostics`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Cli -- ui snapshot --surface dashboard-card --theme light --size 1366x768 --density normal --out artifacts\ui-snapshots`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Cli -- ui shell-snapshot --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Cli -- runner autostart status`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Cli -- broker secret self-test`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Runner -- --once`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Shell.Avalonia -- --smoke --timeout-ms 30000`')
$releaseNotes.Add('- `dotnet run --project src\MyPowerTools.Shell.Avalonia -- --smoke --timeout-ms 30000 --quit-runner`')
$releaseNotes.Add('- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1`')
$releaseNotes.Add('- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1`')
$releaseNotes.Add('- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\publish-windows.ps1`')
$releaseNotes.Add('- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\install-windows.ps1 -PackageRoot artifacts\release\win-x64 -InstallDir artifacts\install-dryrun -DryRun`')
$releaseNotes.Add('- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\uninstall-windows.ps1 -InstallDir artifacts\install-dryrun -DryRun -Force`')
$releaseNotes.Add('')
$releaseNotes.Add('## External Requirements')
$releaseNotes.Add('')
$releaseNotes.Add('- ScreenEase hardware brightness/color-temperature writes require the native display writer.')
$releaseNotes.Add('- NetworkBroker elevated portproxy writes require an elevated helper or administrator token.')
$releaseNotes.Add('- SmartBird, FNB-58, Energy Server, and ADB hardware paths depend on local devices and services.')

Set-Content -LiteralPath $OutputPath -Value $releaseNotes -Encoding UTF8
Write-Host $OutputPath
