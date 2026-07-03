$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $RepoRoot 'artifacts\release'
$PublishRoot = Join-Path $Artifacts 'win-x64'
$ZipPath = Join-Path $Artifacts 'MyPowerTools-win-x64.zip'

function Copy-DirectoryWithoutBuildArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $sourceFull = [System.IO.Path]::GetFullPath($Source)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    Get-ChildItem -LiteralPath $sourceFull -Recurse -Force | ForEach-Object {
        $relative = $_.FullName.Substring($sourceFull.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        $segments = $relative -split '[\\/]'
        if (-not ($segments -contains 'bin' -or $segments -contains 'obj')) {
            if ($_.PSIsContainer) {
                $target = Join-Path $Destination $relative
                New-Item -ItemType Directory -Path $target -Force | Out-Null
            } else {
                $target = Join-Path $Destination $relative
                $targetParent = Split-Path -Parent $target
                New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $target -Force
            }
        }
    }
}

Set-Location -LiteralPath $RepoRoot
New-Item -ItemType Directory -Path $Artifacts -Force | Out-Null

$ArtifactsFull = [System.IO.Path]::GetFullPath($Artifacts)
$PublishRootFull = [System.IO.Path]::GetFullPath($PublishRoot)
if ($PublishRootFull.StartsWith($ArtifactsFull, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $PublishRoot)) {
    Remove-Item -LiteralPath $PublishRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null

dotnet build src\AdbForwarder.MyPowerTools\AdbForwarder.MyPowerTools.csproj -c Release
dotnet build src\AndroidTools.MyPowerTools\AndroidTools.MyPowerTools.csproj -c Release
dotnet build src\ScreenEase.MyPowerTools\ScreenEase.MyPowerTools.csproj -c Release
dotnet run --project src\MyPowerTools.Cli\MyPowerTools.Cli.csproj -- package sign-local modules
dotnet publish src\MyPowerTools.Runner\MyPowerTools.Runner.csproj -c Release -r win-x64 --self-contained true -o (Join-Path $PublishRoot 'Runner')
dotnet publish src\MyPowerTools.Shell.Avalonia\MyPowerTools.Shell.Avalonia.csproj -c Release -r win-x64 --self-contained true -o (Join-Path $PublishRoot 'Shell')
dotnet publish src\MyPowerTools.Cli\MyPowerTools.Cli.csproj -c Release -r win-x64 --self-contained true -o (Join-Path $PublishRoot 'Cli')

Copy-Item -Path (Join-Path $RepoRoot 'modules') -Destination (Join-Path $PublishRoot 'modules') -Recurse -Force
Copy-Item -Path (Join-Path $RepoRoot 'schemas') -Destination (Join-Path $PublishRoot 'schemas') -Recurse -Force
Copy-Item -Path (Join-Path $RepoRoot 'ui') -Destination (Join-Path $PublishRoot 'ui') -Recurse -Force
Copy-DirectoryWithoutBuildArtifacts -Source (Join-Path $RepoRoot 'templates') -Destination (Join-Path $PublishRoot 'templates')
Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\install-windows.ps1') -Destination (Join-Path $PublishRoot 'install-windows.ps1') -Force
Copy-Item -LiteralPath (Join-Path $RepoRoot 'scripts\uninstall-windows.ps1') -Destination (Join-Path $PublishRoot 'uninstall-windows.ps1') -Force

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}

Compress-Archive -Path (Join-Path $PublishRoot '*') -DestinationPath $ZipPath
pwsh.exe -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'release-notes.ps1') -RepoRoot $RepoRoot -ArtifactsRoot $Artifacts
Write-Host $ZipPath
