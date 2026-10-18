# Focused development tests

Use `scripts/test-dev.ps1` for an explicit, reviewable test selection. It preserves all tests and defaults to **Full**. Choose a focused area only when the changed code is owned entirely by that feature. Shared SDK, protocol, schema, security/broker, packaging, build/dependency changes, broad Shell changes, and uncertain ownership require Full.

```powershell
# Default: existing solution tests, standalone managed test projects, and console regressions.
pwsh -NoProfile -File scripts/test-dev.ps1

# Leaf examples. Build the affected project normally before discovery and execution.
pwsh -NoProfile -File scripts/test-dev.ps1 -Area Tools -ToolId input-monitor -Feature Core
pwsh -NoProfile -File scripts/test-dev.ps1 -Area Tools -ToolId screenshot -Feature Lifecycle
pwsh -NoProfile -File scripts/test-dev.ps1 -Area Settings -ToolId remote-notifications
pwsh -NoProfile -File scripts/test-dev.ps1 -Area Shell -Feature Failure
```

The Remote Commands execution-fix project is deferred from this publication, so it is not listed by the solution or focused selector. Existing Remote Commands product coverage remains.

## Selection contract

| Area / tool / feature | Selected coverage | Observed/source count at introduction |
|---|---|---:|
| Tools / input-monitor / Core | InputMonitor.Core.Tests, which owns the product and core fixtures | 32 passed before ownership-only moves |
| Tools / screenshot / Lifecycle | Screenshot.Lifecycle.Regression console runner | 10 passed |
| Settings / remote-notifications | RemoteNotifications.Configuration.Tests | 32 source cases |
| Tools / remote-notifications / Configuration | Same configuration project | 32 source cases |
| Shell / Failure | ShellFailurePresenterTests and HostConnectionRecoveryTests | 13 source cases |
| Shell / Navigation | PersonalUxFavoritesTests, PersonalUxSearchTests, ShellBackNavigationUxTests in PersonalUx.Tests | 12 discovered cases |
| Shell / Shortcuts | Entire PersonalUx.Tests shared headless host, matching the existing shortcut script | 98 cases (36 shortcuts + 62 personal UX) |
| Packaging | Full, because packaging is shared infrastructure | No small-subset shortcut |

These are named regression boundaries, not a guarantee that every change under an entire tool directory is covered. In particular, Core does not replace Input Monitor UI tests, Configuration does not replace notification delivery/protocol tests, and Navigation does not replace all Shell lifecycle/layout tests. Use Full when those boundaries are crossed. Python, original-source legacy suites, architecture/installed-device/service acceptance, macOS power measurements, and Android builds remain separate existing checks; Full here means the active managed regression entry points listed below, not all verification on every platform.

Full runs:

- `dotnet test MyPowerTools.slnx`, with no root-test filter
- `tests/PersonalUx.Tests`, `tests/RemoteNotifications.Mac.Ui.Tests`, and `tools/ime-manager/sdk-tool/tests/ImeManager.Tests`
- Screenshot lifecycle, ADB runtime, and XBRD runtime console regression runners, which `dotnet test` does not execute

Each consolidated fixture has one owner: InputMonitor.Core.Tests owns the 12 product cases, and PersonalUx.Tests owns the 38 cases formerly compiled from eight root fixture files. The root project no longer compiles those same files. Full retains every distinct test behavior while eliminating 50 duplicate executions. PersonalUx retains its existing xUnit v3/headless configuration; the duplicate xUnit v2 root compilation is removed. The Navigation selector follows the canonical PersonalUx owner.

ShortcutCenter fixtures now compile only in PersonalUx.Tests, with their original namespace and assertions unchanged. Full and the shortcut entry point run those 98 cases once, eliminating one standalone build/discovery/test invocation. `scripts/test-personal-ux.ps1` excludes only the `ShortcutCenter.Tests.` namespace to preserve its original personal-UX scope; other namespaces remain eligible. The one-case macOS layout host keeps its small dependency graph and separate application lifetime, compiling the same TestApp source instead of maintaining a duplicate fixture.

The Windows CI gate explicitly runs only those eight moved PersonalUx fixtures (38 cases), using the SDK packages already prepared in that job. It does not add the other 24 PersonalUx cases to the gate. The 12 InputMonitor cases still run through their existing solution project, so that gate removes 12 repeated executions while preserving its original distinct coverage.

Full includes process, local IPC, rendering, and platform-native tests. Run it in an appropriately isolated development/CI environment with those prerequisites; it is not the cloud audit's conservative headless allowlist.

## Inspect first, then run

```powershell
# Read-only test discovery from already-built artifacts. -List is an alias for -Plan.
pwsh -NoProfile -File scripts/test-dev.ps1 -Area Tools -ToolId input-monitor -Feature Core -Plan

# Explicitly opt in to reusing built artifacts when source has not changed.
pwsh -NoProfile -File scripts/test-dev.ps1 -Area Shell -Feature Failure -NoBuild
```

Every run discovers and prints all selected names and the total before executing any cases. Missing artifacts, failed discovery, empty selections, and failed tests are errors. Plan never builds or executes test cases; its counts describe the current binaries, which can be older than source. Console discovery requires a compiled safe-listing protocol marker before launching `--list`, so an old runner that ignores this option is rejected before it could execute tests.

Without `-NoBuild`, the selected projects build normally, preserving MSBuild source/dependency invalidation. SDK-dependent selections use the shared focused-SDK preparation helper; standalone leaf projects avoid preparing unrelated SDK packages. Native build/restore errors are not hidden. Results go to a unique temporary directory unless `-ResultsDirectory` is supplied. `-Configuration` accepts Debug (default) or Release.

Focused SDK preparation always evaluates native Restore/Build/Pack. It requires the repository-owned `artifacts/sdk/global-packages` cache and rejects conflicting `NUGET_PACKAGES`, NuGet configuration, or `RestorePackagesPath` environment overrides before publication; an override resolving to the same cache is supported. SDK-consuming selector builds, discovery, and test commands explicitly use that repository cache. No global user cache is evicted.

## Counts and timing

Canonical ownership removes 50 duplicate executions from Full and 12 from the Windows CI gate, while preserving each distinct case. Those are actual duplicate removals. A smaller feature selection does not establish equivalent coverage for a broad change or a 90% development-cycle speedup. Counts come from current compiled artifacts; no fixed historical root count is used as a performance claim.

The standard `Start-MyPowerTools-Dev.ps1` overlay already invokes zero tests. Incremental SDK preparation and shared build-graph work address build overhead separately. No 90% full-loop, cold-build, or native-install reduction has been measured here.

## Runtime console regressions

ADB maintenance/process safety and XBRD runtime contracts have explicit console regression targets. They are included in the solution build, CI execution, and the selector Full target; `dotnet test` alone does not run their assertions. Use `-Area Tools -ToolId adb-forwarder -Feature Runtime` or `-Area Tools -ToolId xbrd -Feature Runtime`. `-Plan` lists cases without executing test bodies. These use synthetic transports/processes and do not contact real device, quota or publisher endpoints.

Bounded command/notification history and log-tail regressions now run in the existing `src/MyPowerTools.Tests` project through solution `dotnet test`. `HistoryCountTests` owns the history contracts; `RuntimeAcceptanceTests.Log_tail_*` owns tail decoding, live-file boundaries, and public JSON outcomes. The two source-linked console projects, benchmark runners, and samplers are retired. Their exact recovery commit and retained measurement evidence are documented in [Runtime history retention](RUNTIME_HISTORY_RETENTION.md#verification-and-scope). This changes test ownership and discovery grouping, not the preserved behavior boundaries; no focused-build speedup is claimed.
