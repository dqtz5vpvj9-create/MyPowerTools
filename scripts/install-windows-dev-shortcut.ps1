<#
.SYNOPSIS
Creates or refreshes the Start menu shortcut for the MyPowerTools development overlay.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function ConvertTo-WindowsCommandLineArgument {
    param([AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashCount = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($backslashCount * 2) + 1)))
            [void]$builder.Append('"')
            $backslashCount = 0
            continue
        }
        if ($backslashCount -gt 0) {
            [void]$builder.Append(('\' * $backslashCount))
            $backslashCount = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashCount -gt 0) {
        [void]$builder.Append(('\' * ($backslashCount * 2)))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$startScript = Join-Path $repositoryRoot 'scripts\Start-MyPowerTools-Dev.ps1'
$iconPath = Join-Path $repositoryRoot 'assets\MyPowerTools.ico'
$shortcutDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\MyPowerTools'
$shortcutPath = Join-Path $shortcutDirectory 'MyPowerTools 开发版.lnk'
$pwshCommand = Get-Command 'pwsh.exe' -CommandType Application -ErrorAction Stop |
    Select-Object -First 1

foreach ($requiredPath in @($startScript, $iconPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Development shortcut input is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null
$shortcutArguments = @(
    '-NoLogo',
    '-NoProfile',
    '-NonInteractive',
    '-WindowStyle', 'Hidden',
    '-File', $startScript) |
    ForEach-Object { ConvertTo-WindowsCommandLineArgument -Value $_ }

$shortcutHost = New-Object -ComObject WScript.Shell
$shortcut = $null
try {
    $shortcut = $shortcutHost.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $pwshCommand.Source
    $shortcut.Arguments = $shortcutArguments -join ' '
    $shortcut.WorkingDirectory = $repositoryRoot
    $shortcut.IconLocation = "$iconPath,0"
    $shortcut.Description = '构建并启动 MyPowerTools 完整开发覆盖版'
    $shortcut.Save()
}
finally {
    if ($null -ne $shortcut) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
    }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcutHost)
}

[pscustomobject]@{
    Shortcut = $shortcutPath
    Target = $pwshCommand.Source
    RepositoryRoot = $repositoryRoot
    StartScript = $startScript
}
