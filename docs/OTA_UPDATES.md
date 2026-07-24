# MyPowerTools OTA delta updates

The OTA flow exchanges file inventories before transferring application data:

1. The source release produces `source-manifest.json`.
2. The target installation produces `target-manifest.json` and sends that small file to the source.
3. The source compares both inventories and creates a ZIP containing only added or changed files, plus an explicit deletion plan.
4. The target verifies the package hash, its own pre-update file hashes, every payload file, and the final installed hashes.

Each inventory record contains a normalized relative path, byte length, and SHA-256. Paths that are rooted, duplicated, traversing, or backed by reparse points are rejected.

## 1. Produce the two inventories

On the source machine:

```powershell
$sourceRoot = 'C:\release\win-x64'
$sourceManifest = 'C:\ota\source-manifest.json'

$manifestParams = @{
    Root = $sourceRoot
    OutputPath = $sourceManifest
    Version = '0.2.1'
}
& .\scripts\new-ota-file-manifest.ps1 @manifestParams
```

On the target machine:

```powershell
$targetRoot = Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'
$targetManifest = Join-Path $env:TEMP 'target-manifest.json'

$manifestParams = @{
    Root = $targetRoot
    OutputPath = $targetManifest
    Version = '0.2.0'
}
& (Join-Path $targetRoot 'new-ota-file-manifest.ps1') @manifestParams
```

`install.manifest.json`, OTA state, Python bytecode, and `__pycache__` content are excluded by default.

## 2. Build the delta package

After copying `target-manifest.json` to the source machine:

```powershell
$packagePath = 'C:\ota\MyPowerTools-0.2.0-to-0.2.1.ota.zip'

$packageParams = @{
    SourceRoot = $sourceRoot
    SourceManifestPath = $sourceManifest
    TargetManifestPath = 'C:\ota\target-manifest.json'
    OutputPath = $packagePath
}
& .\scripts\new-ota-delta-package.ps1 @packageParams
```

The command also writes `<package>.sha256`. Transfer the ZIP and communicate its SHA-256 through an authenticated channel. SHA-256 detects corruption and substitution only when the expected value is trusted.

Pass `-NoDelete` when the target should retain managed files that disappeared from the source release.

## 3. Apply on the target

```powershell
$expectedHash = '<64-character SHA-256 from the source>'

$updateParams = @{
    PackagePath = 'C:\ota\MyPowerTools-0.2.0-to-0.2.1.ota.zip'
    ExpectedPackageSha256 = $expectedHash
    TargetRoot = $targetRoot
    TargetManifestPath = $targetManifest
    ApplyDeletes = $true
    StopTargetProcesses = $true
    RestartRuntime = $true
}
& (Join-Path $targetRoot 'invoke-ota-update.ps1') @updateParams
```

The updater backs up every affected target file before mutation. Copies use same-directory atomic replacement. A failure restores the journaled files. Successful transactions remove their backup unless `-KeepBackup` is supplied.

When invoked through Windows OpenSSH, `-RestartRuntime` registers a one-time Task Scheduler action with `LogonType=Interactive` and `RunLevel=Limited`. Runner, ServiceManager, and service units launch in the logged-in desktop session. Session 0 only performs inventory, verification, file replacement, process shutdown, and task registration.

Deletion requires `-ApplyDeletes`. `install.manifest.json` remains protected even if a supplied target inventory contains it.

## Verification

Run the deterministic local acceptance test:

```powershell
& pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\scripts\verify-ota-update.ps1
```

The test covers changed, added, deleted, unchanged, protected, idempotent, target-drift, package-hash mismatch, and path-traversal cases while checking that the ZIP payload contains only changed bytes.
