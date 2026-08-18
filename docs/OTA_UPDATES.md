# MyPowerTools OTA updates

MyPowerTools uses a two-layer update model:

1. **Core OTA** — file-level delta transactions for the whole product layout,
   with a full portable ZIP as the compatibility fallback.
2. **Package OTA** — per-tool-package feeds that update a single `modules/<id>`
   package without downloading the ~770 MB product ZIP.

Both layers publish Ed25519-signed feeds; the client verifies the feed
signature and every package SHA-256 before applying anything.

## Flow

```text
CI / local publisher
  version.json + git tag vX.Y.Z
        |
        v
publish-windows.ps1
  - build portable layout
  - write build-provenance.json (version, channel)
  - generate MyPowerTools-win-x64.manifest.json
  - zip MyPowerTools-win-x64.zip (+ .sha256)
  - build deltas against ota-history/*.manifest.json
  - write channel-stable.json / channel-nightly.json (+ .sig, public key)
  - release-metadata.json, Scoop manifest, optional Inno Setup + source bundle
        |
        v
GitHub Release assets
  MyPowerTools-win-x64.zip
  MyPowerTools-win-x64.manifest.json
  MyPowerTools-<from>-to-<to>.ota.zip (+ .sha256)
  channel-<channel>.json (+ .sig)
  ota-signing-public-key.txt
  packages/<id>/channel-package-<id>.json (+ .sig)
  packages/<id>/<id>-<version>.mptpkg.zip (+ .sha256)
        |
        v
Client (installed machine)
  ota-update.ps1 Check
    - fetch feed, verify Ed25519 signature
    - downgrade guard
    - pick delta by installed manifest hash, else full ZIP
  ota-update.ps1 Apply
    - bootstrap updater copies itself out of the install root
    - download + SHA-256 verify
    - delta -> invoke-ota-update.ps1 (transaction + rollback)
    - full  -> install-windows.ps1 (staging + backup)
    - health check against the new file manifest
    - persist installed-files.manifest.json + installed-release.json
```

## Version source

`version.json` at the repository root is the central product version:

```json
{
  "schemaVersion": 1,
  "product": "MyPowerTools",
  "version": "0.3.0",
  "channel": "stable",
  "repository": "https://github.com/dqtz5vpvj9-create/MyPowerTools"
}
```

`scripts/get-product-version.ps1` resolves it, preferring an exact
`vX.Y.Z` git tag at HEAD when `-PreferGitTag` is passed (CI release builds).
`release-metadata.ps1`, `install-windows.ps1`, `publish-windows.ps1` and the
Inno Setup build all consume this one source instead of hardcoded versions.
SDK/protocol package versions (e.g. `0.2.0` in `.csproj` files) stay
independent of the product release version.

## Signing

Feeds are signed with Ed25519 using the vendored RFC 8032 implementation in
`scripts/ed25519.cs`, compiled at runtime by `Add-Type`. No external
cryptography packages are required.

- Private key: a 32-byte seed, stored as 64 hex characters or base64.
- CI secret: `MPT_OTA_SIGNING_KEY_BASE64` (base64 of the seed).
- The current key pair was generated on 2026-08-07; the private key lives at
  `%USERPROFILE%\.mypowertools\ota-signing\ota-signing-key.hex` (outside the
  repository) and the public key is embedded in `ota-update.ps1` /
  `package-ota-update.ps1` and committed at
  `ota-history/ota-signing-public-key.txt`.
- Public key: 64 hex characters, published as `ota-signing-public-key.txt`
  in the Release and inside the portable package.
- The installer copies the public key to
  `%LOCALAPPDATA%\MyPowerTools\ota-state\ota-signing-public-key.txt`.
- The updater rejects signed feeds unless the key is configured (state file,
  `-PublicKeyPath`, `-PublicKeyHex`, or the embedded constant in
  `ota-update.ps1`).
- Unsigned feeds are only accepted with `-AllowUnsigned` (local/nightly dev).

Stable releases in CI fail when the signing secret is missing. Nightly builds
continue unsigned with a warning so the pipeline remains usable before the
secret is configured.

## Install-time OTA state

`install-windows.ps1` writes:

```text
%LOCALAPPDATA%\MyPowerTools\ota-state\
├─ installed-release.json          version, channel, install dir, manifest hash
├─ installed-files.manifest.json   exact release manifest bytes (delta target)
├─ ota-signing-public-key.txt
├─ downloads\
├─ bootstrap\
└─ transactions\
```

The installed file manifest is the **exact byte copy** of the release asset
`MyPowerTools-win-x64.manifest.json`, not a regenerated local inventory. Delta
plans bind to that manifest's SHA-256, so byte identity matters for online
delta selection.

## Client commands

```powershell
# from the installed layout or a source checkout
pwsh -NoProfile -File .\ota-update.ps1 -Command Check
pwsh -NoProfile -File .\ota-update.ps1 -Command Apply
pwsh -NoProfile -File .\ota-update.ps1 -Command Status

# via the product CLI
mpt ota check
mpt ota apply [--channel nightly] [--force]
mpt ota status
```

`configure-user-services.ps1` registers a daily `MyPowerTools OTA Check`
scheduled task (03:00, interactive user). Applying updates still requires an
explicit `mpt ota apply` or a manual script invocation. To let the machine
apply updates automatically at the daily window:

```powershell
pwsh scripts/set-ota-policy.ps1 -EnableAutoApply
```

Revert with `-DisableAutoApply`. The policy is stored in
`%LOCALAPPDATA%\MyPowerTools\ota-state\update-policy.json` and re-read whenever
user services are configured.

## Click-to-update in the Shell

Open **Packages & Updates** in the Shell. The **产品更新 (OTA)** section shows
the installed and latest versions and provides two buttons:

- **检查更新** — verifies the signed GitHub feed and reports whether a new
  version is available.
- **立即升级** — 先检测哪些程序正在占用需要更新的文件，只列出这些程序。
  确认后更新器关闭它们并替换文件，完成后按同一名单重新打开。
  命令行 `mpt ota apply` 同样只列出检测到的程序；脚本/计划任务或 `--yes`
  跳过确认，但仍按检测结果恢复。

The updater:

- verifies the feed signature and package SHA-256;
- blocks downgrades;
- selects a delta whose `fromManifestSha256` matches the installed manifest,
  falling back to the full ZIP;
- copies itself (and the OTA scripts) into
  `ota-state\bootstrap` and relaunches with that directory as the working
  directory, so `Directory.Move` of the install root is not blocked by cwd;
- stops product processes **and** host processes whose command line points
  into the install root (for example DDNS `pwsh -File ...\service-units\...`),
  stops `adb.exe` even when it lives outside the install root (the WinGet
  adb server can hold `service-units\adb-forwarder.service\bin`),
  then retries `Directory.Move`;
- restarts the programs that were listed at consent time (Shell / Runner /
  scheduled tasks as detected); if no reopen plan is present (scheduled
  auto-apply), starts Runner and Shell as before;
- health-checks the critical executables and scripts against the new manifest
  **before** recording `installed-release.json`;
- on a delta health failure, reinstalls the full ZIP as the recovery path
  (disable with `-SkipFullRecoveryOnHealthFailure`);
- does **not** reapply a Dev overlay. After OTA, run
  `scripts/Start-MyPowerTools-Dev.ps1` if you still want local Debug binaries;
- records `last-check.json`, `last-update.json`, `health-check.json`.

## Shared .NET runtime

The Windows release ships one .NET runtime copy under
`%LOCALAPPDATA%\Programs\MyPowerTools\Runtime\dotnet`. Every product process is
framework-dependent and resolves that runtime through `DOTNET_ROOT`, which the
installer, OTA updater, and runtime starter set for the current user. Debug
symbols are stripped before packaging; the portable ZIP is ~226 MB instead of
~773 MB.

## Package OTA

Each first-party tool package (`modules/<id>`) can be published independently:

```powershell
pwsh scripts/new-ota-package-feed.ps1 `
  -PackageId android-tools-suite `
  -PackageDir artifacts\release\module-packages\android-tools-suite `
  -SigningKeyBase64 $key `
  -OutputDir artifacts\release\packages\android-tools-suite

pwsh scripts/publish-ota-package-feeds.ps1 `
  -ModulePackagesRoot artifacts\release\module-packages `
  -SigningKeyBase64 $key
```

The client applies a package feed with:

```powershell
pwsh scripts/package-ota-update.ps1 -PackageId android-tools-suite
```

The package updater verifies the feed signature, the archive SHA-256, the
package trust metadata (`mpt package trust --strict`), and the feed's
`coreMinimumVersion` against the installed core version before replacing
`modules/<id>` transactionally with backup/rollback.

## Release automation

`.github/workflows/ci.yml` contains a `release` job that:

1. runs on `v*` tag pushes, `workflow_dispatch`, or the nightly schedule;
2. resolves the version/channel;
3. downloads the previous release manifest into `ota-history/`;
4. runs `publish-windows.ps1` and `publish-ota-package-feeds.ps1`;
5. creates a GitHub Release with all OTA assets;
6. commits the new manifest to `ota-history/` for the next delta build.

Local releases work the same way:

```powershell
pwsh scripts/publish-windows.ps1 -Channel stable `
  -SigningKeyBase64 $env:MPT_OTA_SIGNING_KEY_BASE64
```

Validate a produced release directory before distributing it:

```powershell
pwsh scripts/verify-release-artifacts.ps1 -Channel stable -ExpectedVersion 0.3.0
```

The verifier checks ZIP/SHA-256 parity, manifest consistency, feed structure
and signature, public-key match, ZIP contents, release metadata, delta plan
consistency, and every package feed signature/archive hash.

## First upgrade of an existing machine over SSH

When an older installation (for example the dorm machine on v0.2.0) has no OTA
state yet, deploy the full ZIP with the two-step SSH helper:

```powershell
# preflight first (reachability only)
pwsh scripts/deploy-dorm-upgrade.ps1 -RemoteHost dorm -PreflightOnly

# transfer ~773 MB, verify the remote hash, install, and health-check
pwsh scripts/deploy-dorm-upgrade.ps1 -RemoteHost dorm
```

`deploy-dorm-upgrade.ps1` uploads the ZIP, its SHA-256 marker,
`dorm-upgrade-remote.ps1`, and the current `install-windows.ps1` to
`C:\Users\Public\MyPowerTools-Upgrade`, then runs the remote helper with
`pwsh`. The helper verifies the hash before extraction, overlays the uploaded
installer onto staging, runs it, and checks the installed version, OTA state
files, manifest byte identity, and critical executables. If the ZIP is already
on the remote host, retry with `-SkipZipCopy -SkipExtract`.
The machine keeps its data under `%LOCALAPPDATA%\MyPowerTools`; the install
directory is replaced with backup/restore protection.

## Security baseline

- Ed25519-signed feed binds channel, version, full package hash, and delta
  manifest hashes.
- SHA-256 verification for every downloaded artifact.
- Path traversal, reparse point, target drift, protected-file, and package
  hash checks in the delta transaction.
- Downgrade guard and update mutex in the client.
- Bootstrap updater so the updater itself can be replaced.
- Full ZIP fallback for unknown or drifted installs.
- Health check before a successful update is recorded; full-package recovery
  on delta health failure.

## Verification

```powershell
pwsh -NoProfile -File .\scripts\verify-ota-update.ps1     # offline delta core
pwsh -NoProfile -File .\scripts\verify-ota-online.ps1     # feed + updater + health
pwsh -NoProfile -File .\scripts\verify-ota-package.ps1    # package OTA
```

The online test covers signed feed generation/verification, tamper rejection,
unsigned-feed policy, downgrade blocking, delta selection, bootstrap
relaunch, transactional apply, installed-manifest persistence, and post-update
health checks. The package test covers signed package feeds, up-to-date
detection, core minimum enforcement, and transactional package replacement.
