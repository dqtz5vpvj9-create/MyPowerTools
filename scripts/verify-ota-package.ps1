[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Utf8Text {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Read-JsonOutput {
    param([Parameter(Mandatory = $true)][object[]]$Output)

    return (($Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine) | ConvertFrom-Json
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "ASSERT: $Message"
    }
}

$scriptsRoot = $PSScriptRoot
$feedScript = Join-Path $scriptsRoot 'new-ota-package-feed.ps1'
$updaterScript = Join-Path $scriptsRoot 'package-ota-update.ps1'
$testId = [Guid]::NewGuid().ToString('N')
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempParent "mypowertools-ota-package-test-$testId"))

try {
    $packageRoot = Join-Path $testRoot 'packages'
    $v1Dir = Join-Path $packageRoot 'testpkg-v1'
    $v2Dir = Join-Path $packageRoot 'testpkg-v2'
    $outputDir = Join-Path $testRoot 'feeds'
    $installRoot = Join-Path $testRoot 'install'
    $dataRoot = Join-Path $testRoot 'data'
    $stateRoot = Join-Path $dataRoot 'ota-state'
    New-Item -ItemType Directory -Path $v1Dir, $v2Dir, $outputDir, $stateRoot -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $installRoot 'Cli') -Force | Out-Null

    foreach ($dir in @($v1Dir, $v2Dir)) {
        Write-Utf8Text -Path (Join-Path $dir 'tool.txt') -Value "payload-$([IO.Path]::GetFileName($dir))"
    }
    Write-Utf8Text -Path (Join-Path $v1Dir 'module.json') -Value (@{
        schemaVersion = 1
        id = 'testpkg'
        displayName = 'Test Package'
        version = '1.0.0'
        host = @{ minVersion = '0.2.0' }
    } | ConvertTo-Json -Depth 4)
    Write-Utf8Text -Path (Join-Path $v2Dir 'module.json') -Value (@{
        schemaVersion = 1
        id = 'testpkg'
        displayName = 'Test Package'
        version = '2.0.0'
        host = @{ minVersion = '0.2.0' }
    } | ConvertTo-Json -Depth 4)

    $fakeCliPath = Join-Path $installRoot 'Cli\MyPowerTools.Cli.exe'
    $fakeCliProject = Join-Path $testRoot 'fakecli'
    & dotnet new console --no-restore -o $fakeCliProject --force | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to scaffold the fake CLI test project.'
    }
    Write-Utf8Text -Path (Join-Path $fakeCliProject 'Program.cs') -Value @'
if (args.Length >= 2 && args[0] == "package" && args[1] == "trust")
{
    return 0;
}
return 1;
'@
    $publishOutput = @(& dotnet publish $fakeCliProject -c Release -o (Join-Path $installRoot 'Cli') --nologo 2>&1 |
        ForEach-Object { [string]$_ })
    $publishExit = $LASTEXITCODE
    $publishedExe = Join-Path $installRoot 'Cli\fakecli.exe'
    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
        Copy-Item -LiteralPath $publishedExe -Destination $fakeCliPath -Force
    }
    if (-not (Test-Path -LiteralPath $fakeCliPath -PathType Leaf)) {
        throw "Unable to build the fake CLI test executable (exit $publishExit): $($publishOutput -join [Environment]::NewLine)"
    }

    $keyBytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($keyBytes)
    $keyBase64 = [Convert]::ToBase64String($keyBytes)
    $publicKeyPath = Join-Path $outputDir 'ota-signing-public-key.txt'
    $feedResult = Read-JsonOutput -Output @(& $feedScript `
        -PackageId 'testpkg' `
        -PackageDir $v2Dir `
        -Channel 'stable' `
        -OutputDir $outputDir `
        -SigningKeyBase64 $keyBase64 `
        -PublicKeyOutputPath $publicKeyPath)
    Assert-True -Condition ([bool]$feedResult.Signed) -Message 'package feed should be signed'
    Assert-True -Condition ($feedResult.Version -eq '2.0.0') -Message 'package feed version is wrong'
    Assert-True -Condition ($feedResult.CoreMinimumVersion -eq '0.2.0') -Message 'package feed core minimum is wrong'

    $installedPkgDir = Join-Path $installRoot "modules\testpkg"
    Copy-Item -LiteralPath $v1Dir -Destination $installedPkgDir -Recurse -Force
    $installedRelease = [ordered]@{
        schemaVersion = 1
        product = 'MyPowerTools'
        version = '0.3.0'
        channel = 'stable'
        installedAt = (Get-Date).ToString('O')
        installDir = $installRoot
        dataRoot = $dataRoot
        manifestPath = 'installed-files.manifest.json'
        manifestSha256 = ''
        packageKind = 'full'
    }
    $installedRelease | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (
        Join-Path $stateRoot 'installed-release.json') -Encoding UTF8

    $feedPath = Join-Path $outputDir 'channel-package-testpkg.json'
    $updateParams = @{
        PackageId = 'testpkg'
        Channel = 'stable'
        LocalFeedPath = $feedPath
        LocalPackageRoot = $outputDir
        InstallRoot = $installRoot
        DataRoot = $dataRoot
        StateRoot = $stateRoot
        PublicKeyPath = $publicKeyPath
        NoRuntimeRestart = $true
    }
    $apply = Read-JsonOutput -Output @(& $updaterScript @updateParams)
    Assert-True -Condition ([bool]$apply.success) -Message 'package OTA apply did not succeed'
    Assert-True -Condition ($apply.toVersion -eq '2.0.0') -Message 'package OTA applied wrong version'
    $updatedModule = Get-Content -LiteralPath (Join-Path $installedPkgDir 'module.json') -Raw | ConvertFrom-Json
    Assert-True -Condition ([string]$updatedModule.version -eq '2.0.0') -Message 'installed package module.json was not updated'
    Assert-True -Condition ((Get-Content -LiteralPath (Join-Path $installedPkgDir 'tool.txt') -Raw) -eq 'payload-testpkg-v2') -Message 'package payload was not replaced'
    $packagesState = Get-Content -LiteralPath (Join-Path $stateRoot 'installed-packages.json') -Raw | ConvertFrom-Json
    Assert-True -Condition ($packagesState.testpkg.version -eq '2.0.0') -Message 'package state was not recorded'

    $upToDate = Read-JsonOutput -Output @(& $updaterScript @updateParams)
    Assert-True -Condition (-not [bool]$upToDate.available) -Message 'up-to-date package was reported as available'
    Assert-True -Condition ($upToDate.reason -eq 'up-to-date') -Message 'up-to-date reason is wrong'

    $v3Dir = Join-Path $packageRoot 'testpkg-v3'
    New-Item -ItemType Directory -Path $v3Dir -Force | Out-Null
    Write-Utf8Text -Path (Join-Path $v3Dir 'tool.txt') -Value 'payload-testpkg-v3'
    Write-Utf8Text -Path (Join-Path $v3Dir 'module.json') -Value (@{
        schemaVersion = 1
        id = 'testpkg'
        displayName = 'Test Package'
        version = '3.0.0'
        host = @{ minVersion = '9.0.0' }
    } | ConvertTo-Json -Depth 4)
    $highCoreFeedDir = Join-Path $testRoot 'feeds-highcore'
    [void](& $feedScript `
        -PackageId 'testpkg' `
        -PackageDir $v3Dir `
        -Channel 'stable' `
        -OutputDir $highCoreFeedDir `
        -SigningKeyBase64 $keyBase64 `
        -PublicKeyOutputPath (Join-Path $highCoreFeedDir 'ota-signing-public-key.txt'))
    $coreBlocked = $false
    try {
        $highCoreParams = $updateParams.Clone()
        $highCoreParams.LocalFeedPath = Join-Path $highCoreFeedDir 'channel-package-testpkg.json'
        $highCoreParams.LocalPackageRoot = $highCoreFeedDir
        [void](& $updaterScript @highCoreParams)
    }
    catch {
        $coreBlocked = $_.Exception.Message -like '*requires core >= 9.0.0*'
    }
    Assert-True -Condition $coreBlocked -Message 'core minimum version was not enforced'

    [pscustomobject]@{
        Success = $true
        FeedSigned = [bool]$feedResult.Signed
        ApplySucceeded = [bool]$apply.success
        UpToDateDetected = $upToDate.reason -eq 'up-to-date'
        CoreMinimumEnforced = $coreBlocked
    } | ConvertTo-Json -Depth 5
}
finally {
    $tempPrefix = $tempParent.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($testRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($testRoot) -eq "mypowertools-ota-package-test-$testId" -and
        (Test-Path -LiteralPath $testRoot -PathType Container)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
