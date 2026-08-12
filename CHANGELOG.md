# Changelog

## 0.3.6 (unreleased)

- Logs page is now functional: search, Info/Warning/Error level filter, wrap
  toggle, refresh, copy, and export. When the Runner/HostControl endpoint is
  unreachable the page falls back to the persistent JSONL/text logs under
  `%LOCALAPPDATA%\MyPowerTools\logs` instead of showing an empty shell.
- Packages & Updates (OTA) page renders even when the runtime is not running;
  OTA check/apply only require the local CLI and updater scripts.
- `mpt ota` on installs that predate the online updater now prints an
  actionable message (install a 0.3.0+ release first) instead of dumping CLI
  help text.
- Added a first-party DDNS plugin (`tools/ddns`) with Tencent DNSPod support:
  `mpt ddns status|update|list|watch`, a ServiceManager-supervised
  `ddns.service` unit that keeps `subDomain.mainDomain` A records in sync with
  the selected adapter IP, and release packaging as the `ddns` module package.

## 0.3.5 (unreleased)

- Remote Notifications detail popup closes with Escape (window key handling
  plus WebView2/WKWebView JavaScript bridge for webview-focused input).
- Added macOS release automation: CI builds `MyPowerTools-macos-arm64.zip`
  (ad-hoc signed .app bundle) and uploads it to the versioned GitHub Release.

## 0.3.4 (unreleased)

- Python runtime switched to the official 3.12 embeddable distribution
  (Python312 164 MB -> 59 MB) with site-packages configured for SmartBird
  Energy Server dependencies.
- Doubao tool/mcp/planner consolidated into one shared venv under
  `Runtimes\Doubao\.venv` (single copy of all dependencies).
- Android platform-tools trimmed to adb + its runtime DLLs (16.7 MB -> 8.5 MB).
- Inno Setup uses LZMA2/ultra64 with solid compression.
- Portable ZIP: 200 MB (original 773 MB); uncompressed layout: 479 MB.

## 0.3.3 (unreleased)

- Replaced the per-process self-contained .NET runtime copies with one shared
  runtime under `Runtime\dotnet` (host\fxr + shared frameworks). Runner, Shell,
  Cli, ServiceManager, Broker, App, WebToolHost, all Service Units, and the
  powertoold sidecar are now framework-dependent and resolve the bundled
  runtime through `DOTNET_ROOT`, which is set at install/update time.
- Debug symbols are stripped from the published layout and package archives.
- Release size: uncompressed 1.78 GB -> 0.59 GB; ZIP 773 MB -> 226 MB.

## 0.3.2 (unreleased)

- Added PowerToys-style click-to-update in the Shell Packages & Updates page:
  “检查更新” runs `mpt ota check` and “立即升级” runs `mpt ota apply`; the
  page shows current/latest versions, availability, and the update result.
- Added an optional auto-apply policy: `scripts/set-ota-policy.ps1
  -EnableAutoApply` switches the daily OTA task from Check to Apply so the
  machine updates itself without any command.

## 0.3.1 (unreleased)

- Stopped `adb.exe` during Windows install replacement so service-unit
  directories are not locked by the ADB server's working directory.
- CI release automation now pushes OTA history from detached HEAD via
  `HEAD:main`, prepares Python/platform-tools/Doubao runtime caches on the
  runner, and authenticates `gh` steps with `GITHUB_TOKEN`.
- First delta release: `MyPowerTools-0.3.0-to-0.3.1.ota.zip` is generated
  from the 0.3.0 manifest stored in `ota-history/`.

## 0.3.0 (unreleased)

- Added central product versioning via `version.json` and
  `scripts/get-product-version.ps1`; release metadata, installs, and the
  Inno Setup build now consume one version source instead of hardcoded values.
- Added install-time OTA state under `%LOCALAPPDATA%\MyPowerTools\ota-state`
  with the exact release file manifest, installed-release record, and signing
  public key.
- Added release orchestration: release file manifests, cross-version delta
  packages, Ed25519-signed channel feeds, and per-package OTA feeds.
- Added `scripts/ed25519.cs`, a vendored RFC 8032 Ed25519 implementation
  validated against RFC test vectors, used for feed signing and verification.
- Added `ota-update.ps1` (check/apply/status) with feed signature
  verification, downgrade guard, delta/full selection, updater bootstrap,
  transaction apply, health checks, and full-package recovery.
- Added `package-ota-update.ps1` for independent tool package updates with
  trust validation, core minimum enforcement, and transactional replacement.
- Added `mpt ota check|apply|status` CLI entry points.
- Added a daily `MyPowerTools OTA Check` scheduled task during user service
  configuration.
- Added CI release automation: tag/nightly builds publish GitHub Releases with
  full ZIP, manifest, deltas, signed feeds, package feeds, and OTA history
  commits.
- Fixed `New-Item -Force` registry footgun that recreated the HKCU Run key and
  wiped unrelated autostart values; Run-key creation is now guarded by
  `Test-Path` in the installer, user-service configuration, updater restore,
  and dorm deployment helpers.
- Fixed `start-user-runtime.ps1` to detect the running Runner with
  `Get-Process` instead of WMI, which failed with access denied inside
  scheduled-task sessions and blocked the interactive runtime bootstrap.
- Made the daily OTA check task registration tolerant of restricted task
  contexts so runtime start never fails when re-registration is denied.

## 0.2.0

- Added .NET 10 solution structure for Runner, Avalonia Shell, Protocol, Runtime, ModuleHost, Platform Packs, Broker, Packaging, UI, CLI, and tests.
- Added typed gRPC proto generation for module and host control protocols.
- Added persistent settings, event sequence handling, command idempotency, log redaction, notification records, and command history.
- Added package schema validation, sha256 hash manifests, install/uninstall/update/rollback/repair foundations.
- Added Windows platform provider and macOS/Linux compile-ready degradation providers.
- Added Broker audit foundations for privileged, service, network, secret, and autostart actions.
- Added reusable Avalonia MPT controls and UI gate validation.
- Moved sample modules to test fixtures so production module root contains real tool modules only.
- Added HostControl-backed Avalonia Shell pages for Dashboard, Modules, Settings, Logs, Notifications, Packages, Diagnostics, command palette, broker permission prompt, and broker audit.
- Added HostControl notification and package summary RPCs.
- Added deterministic UI contract and PNG pixel snapshots through `mpt ui snapshot`.
- Added module contract validation through `mpt validate contracts`, CI, smoke, and CLI acceptance coverage.
- Added Windows CI workflow, release notes generation, and production README.
- Added Windows portable install and uninstall scripts with dry-run validation.
- Added release/update metadata generation and a Scoop package-manager manifest with hash parity checks for the Windows portable zip.
- Added cross-platform local IPC endpoint selection plus macOS/Linux degraded service providers for platform capability packs.
- Added cross-platform hotkey and privilege provider surfaces with Win32 `RegisterHotKey` on Windows, broker-required privilege behavior, and explicit macOS/Linux unsupported states.
- Added six validated module templates plus template validation script for CI and smoke checks.
- Added Shell HostControl smoke mode and upgraded the smoke script to launch Runner, verify Shell IPC, and report module/dashboard/command counts.
- Wired HostControl `QuitRunner` to the Runner host lifetime so smoke-owned Runner processes exit gracefully after Shell IPC validation.
- Added persistent module enable/disable state through Runtime, HostControl `SetModuleEnabled`, CLI `mpt module list|enable|disable`, and Shell module toggles.
- Added typed RuntimeDiagnostics HostControl IPC, Shell Diagnostics rendering, CLI `mpt diagnostics`, and Runtime/HostControl diagnostics tests.
- Added CLI package rollback through `mpt rollback`, test-isolated `--store-root`, and install/uninstall/rollback/repair acceptance coverage.
- Added local HTTP facade integration coverage for health refresh, HTTP command execution, output redaction, and log correlation.
- Added broader CLI process coverage for `mpt inspect`, `mpt package hash`, and `mpt doctor`.
- Added gRPC IPC sidecar crash-policy coverage for process replacement, log correlation, and restart limit enforcement.
- Added Windows Runner autostart provider, AutostartBroker status/enable/disable audit coverage, CLI `mpt runner autostart`, and release-root discovery for portable Runner/CLI layout.
- Added gRPC IPC runtime process diagnostics for shared runtime pools, PID/endpoint/start-limit telemetry, module membership, HostControl mapping, Shell Diagnostics rendering, CLI output, and acceptance coverage.
- Added Runtime transport cleanup on Runner and CLI shutdown so diagnostics and command probes do not leave sidecar processes behind.
- Added Runner tray infrastructure with cross-platform tray abstractions, Windows native Shell_NotifyIcon implementation, Open Shell and Quit Runner actions, unsupported-platform degradation, and Runner `--no-tray` escape hatch.
- Added HostControl-backed runtime process restart controls for gRPC IPC pools, including Runtime restart execution, Shell Diagnostics Restart action, CLI `mpt runner process restart`, and acceptance coverage.
- Added runtime process restart-policy pause/resume controls for gRPC IPC pools, including policy diagnostics, paused-pool degraded rows, Shell Diagnostics Pause/Resume actions, CLI `mpt runner process pause|resume`, HostControl IPC, and Runtime/HostControl/CLI acceptance coverage.
- Added persistent runtime process policy state under `state/runtime.process-policies.json`, source-aware policy history in RuntimeDiagnostics, Shell Diagnostics policy history rendering, CLI `process-policy` diagnostics output, and reload coverage for paused gRPC IPC pools.
- Added restart-policy maintenance windows with CLI `--until` / `--duration-minutes`, typed `expires_at` HostControl fields, Shell Diagnostics `Pause 1h`, automatic expiry recovery, and acceptance coverage for expiring gRPC IPC pools.
- Added SecretBroker-backed OS secret storage: Windows now uses Credential Manager, tests use an in-memory provider, macOS/Linux expose degraded secret providers, CLI has `mpt broker secret self-test`, and broker audit records save/read/delete without leaking secret values.
- Added typed HostControl module permission and capability requirement fields, Shell module permission sections, CLI `mpt inspect modules` permission output, and acceptance coverage for permission visibility.
- Added local package trust hooks: package manifests can declare `trust.signature`, `mpt package sign-local` writes `shared/package.signature.json`, `mpt package trust --strict` validates hash/signature metadata, and package install/repair now use the trust verifier.
- Added package trust visibility to HostControl package summaries and the Shell Packages page, including trust state, policy, signature path, and trust issue count.
- Added `mpt ui shell-snapshot` for deterministic Shell surface snapshot coverage across Dashboard, Command Palette, Settings, Module Detail, Logs, Notifications, Permission Prompt, Degraded Module, Packages, and Runtime Diagnostics.
- Added Shell HostControl connection monitoring with offline state tracking and automatic page/command/audit refresh after Runner IPC reconnection.
- Added HostControl package lifecycle operations for install, repair, uninstall, and rollback, wired them into the Shell Packages page, and excluded `.rollback` package backups from active runtime discovery.
- Added Shell HostControl event stream consumption with sequence resume, duplicate replay filtering, fault reporting, reconnect, and event-driven Shell page refresh.
- Added ModuleSupervisor health policy automation with per-module observations, consecutive failure counts, supervisor state/action, RuntimeDiagnostics/HostControl/CLI/Shell visibility, Dashboard alerts, and repeated HTTP facade outage/recovery tests.
- Added CLI `mpt runner process pause . --duration-minutes <minutes>` shorthand to select the first active RuntimeDiagnostics process pool for smoke-friendly restart-policy validation.
- Added Doubao Agent InProc controller module with planner/tool/MCP port health separation, self-test, log summary, runtime settings schema, and degraded status for partial runtime availability.
- Added SmartBird Thermostat InProc typed facade with HTTP status/events/config/log probes, brokered restart request details, settings schema, bounded event output, local path redaction, and degraded Energy Server/FNB-58/ADB diagnostics.
- Added ScreenEase Windows DDC/CI native display writer probing and explicit hardware-write application for brightness/color-temperature profile changes, with actionable unsupported-monitor diagnostics.
- Hardened `LogRouter` concurrent append behavior for parallel CLI/runtime command probes.
- Completed P8 final production closure with final audit classification, release metadata relative URLs, release hash parity, release Runner/Shell smoke, install/uninstall dry-runs, and zip hygiene verification.
- Renamed local package trust algorithm metadata to `sha256-manifest-local` and refreshed production package signatures.
- Fixed release Runner relative `--data-root` handling by normalizing `RuntimePaths` to absolute paths before InProc shadow-copy loading.
- Added release evidence semantic validation for internal path, assembly, reflection, and runtime failure markers, while allowing only documented external degraded module reasons.
- Hardened gRPC IPC cancellation so cancel requests reuse an existing initialized host and never start a fresh sidecar for a missing invocation.
- Suppressed gRPC stream cleanup exceptions after a terminal runtime-unavailable failure has already been emitted.
- Bounded AndroidTools Remote Commands shell streaming with channel capacity, event count limits, stdout/stderr byte limits, single-line byte limits, `output.truncated` events, and final truncation metadata.
