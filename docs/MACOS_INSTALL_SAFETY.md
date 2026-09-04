# macOS installation safety and remaining release gates

Baseline: `b50e50238c89831fc3ab8473ea2cc11325d882fc`, from
`codex/remote-notifications-macos-production-2026-09`.

## Installation contract

`scripts/install-macos-base.ps1` stages the new application under a private sibling
of the destination, using `ditto` to preserve signed bundle metadata. Before the
installed application's processes or directories are changed, it checks the bundle
identifier, numeric product version, five required apphosts, executable permissions,
module/service-unit directories, and `codesign --verify --deep --strict`.

A failed copy or validation leaves the previous application and its running processes
untouched. Installation from the current application itself is supported because the
candidate copy is complete before the old directory moves.

After validation, replacement saves the old directory under a timestamp-and-GUID
backup. Caught replacement or activation failures restore that directory and the
previous LaunchAgent plist contents and loaded state. Failed rollback preserves
recovery trees and reports the original error, recovery error and relevant paths.
A successful replacement retains the old application as a backup.

Process selection uses current-user executable paths from `ps comm`, with a directory
separator boundary. A command-line argument mentioning the app directory does not
make an unrelated terminal, editor or updater an owned process. Processes are
re-enumerated before escalation from TERM to KILL, and replacement fails if matching
processes remain after both waits.

`-SkipLaunchAgents` performs no launchd unload, bootstrap or plist replacement.
Normal installation refuses to take over existing agent plists pointing to a different
installation root. Launch Services registration occurs before new agents start.

Concurrent base-installer writers share an exclusive file handle at
`<ApplicationsRoot>/.mypowertools-install.lock`. The file remains after release; its
presence does not indicate that an installation is running. Keeping the inode avoids
unlink/recreate races between participating installers. The installer also refuses
symbolic-link app/lock/plist destinations, a destination inside the source bundle,
and application data stored inside the installed bundle. This is a user-level
installer and rejects execution as root.

## Automated verification

The dependency-free regression command is:

```powershell
pwsh -NoProfile -File scripts/verify-macos-install-safety.ps1 -ReportPath /tmp/mpt-install-safety.json
```

The script parses and loads the actual production functions, without executing the
installer entry point, for 11 portable process-selection, directory-swap, rollback,
backup and exclusive-lock cases. On macOS it additionally constructs signed fixture
bundles containing copies of `/bin/sleep` and exercises 8 native scenarios: first
installation, native error handling, a tampered candidate, replacement with an
unrelated process, self-source installation, lock contention, symbolic-link rejection,
and recursive destination rejection. Tests use temporary Applications/Data roots and
`-SkipLaunchAgents`. Cleanup is restricted to test-owned processes and paths.

`.github/workflows/macos-install-safety.yml` runs on `ubuntu-24.04`, `macos-14` and
`macos-15-intel`. Reports are written under `RUNNER_TEMP` and uploaded as workflow
artifacts. No SDK build, submodule checkout or signing secret is needed.

The JSON report records each case's result, the operating system and architecture.
A workflow result is evidence for the tested commit only. Signed fixtures exercise
installation mechanics, not the product's GUI, TCC authorization or service readiness.
The existing full-bundle installation and launchd workflows remain separate gates.

## Remaining production gates

| Priority | Remaining work | Required evidence |
| --- | --- | --- |
| P0 | Unify the outer OTA transaction, base installer and OTA-state writes under one lock and recovery protocol. `ota-apply-macos.ps1` currently performs its own maintenance and directory changes; `install-macos.ps1` writes OTA state after the base installer returns. | Competing update/manual-install processes, manifest-write failure and recovery tests with a consistent old or new release state. |
| P0 | Replace the connect-only health test in `ota-apply-macos.ps1` with authenticated application-level health, release identity and required Service Unit readiness. | A stale socket, another process accepting connections, a mismatched release and a degraded required worker must fail the update gate. |
| P0 | Complete and enforce the Developer ID distribution path. `publish-macos.ps1` currently defaults to ad-hoc signing and uses `--timestamp=none`; signature integrity alone does not establish a notarized release. | Developer ID signing with secure timestamps, notarization acceptance, stapling and Gatekeeper assessment of the downloaded final archive on a clean Mac. |
| P1 | Add durable crash recovery. The two directory renames and launchd changes are not a power-loss-atomic transaction. | Terminating the installer at every phase and recovering on the next launch, without discarding the last working backup. |
| P1 | Validate the installed product lifecycle and permissions on both supported CPU architectures. | Clean-user first launch, notification permission denial/regrant, notification click activation, login restart, sleep/wake and repeated update/rollback cycles. |
| P1 | Bring the older sections of `docs/MACOS_RELEASE.md` in line with the current OTA, CLI and Remote Notifications helper layout. | Commands and paths verified against an actual final signed bundle. |

## Scope limits

Caught-error recovery does not imply crash recovery. The base-installer lock does not
yet serialize the complete OTA wrapper transaction. Ad-hoc signatures are accepted
by the integrity check; it does not authorize a publisher or establish notarization.
The process check is an operational ownership guard, not a security boundary against
malicious processes with the same user privileges. Real LaunchAgent bootstrap/rollback
and full application health require their own native product integration evidence.
