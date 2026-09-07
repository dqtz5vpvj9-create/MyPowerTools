# Pre-push CI rig

Two checks that run before a push, so a red X on GitHub is news rather than routine.

```bash
scripts/ci-local/lint-workflows.sh          # seconds, no network
scripts/ci-local/preflight.sh --suite Quick # ~minutes, real Windows host
scripts/ci-local/preflight.sh               # the full Windows job
```

## Why there is no container simulation here

`nektos/act` is the usual answer to "run GitHub Actions locally", and it does
not apply to this repository. act runs jobs in Linux Docker images. Every
Windows job here is pwsh, MSBuild, named pipes, the Windows SCM and NSSM; every
macOS job is `xcrun`, `launchd` and app bundles. Neither has a Linux container
equivalent, and the development machine is Linux, so the solution does not even
compile there.

So the Windows half is not simulated, it is executed — on the machine reachable
as `ssh win`, from a dedicated clone at `C:\ci\MyPowerTools` that is kept apart
from any development clone.

## What each piece does

| File | Runs on | Purpose |
| --- | --- | --- |
| `lint-workflows.sh` | Linux | actionlint over `.github/workflows`. Catches unknown runner labels, `${{ }}` referencing outputs or secrets that do not exist, `needs` on undeclared jobs, bad `if:` syntax. Downloads and caches the pinned actionlint on first use. |
| `Sync-CiWorkspace.ps1` | Windows | Reproduces `actions/checkout@v4` with `submodules: recursive`: detached at the exact commit, submodules at the recorded commits, `git clean -xdff` on the superproject and every submodule. |
| `Invoke-WindowsCi.ps1` | Windows | Replays the workflow jobs step for step under the runner's environment variables. Each step is a gate; step names match the workflow so a local failure names the GitHub step. |
| `preflight.sh` | Linux | Ships the commit as a git bundle over SSH, drives the two scripts above, brings back a JSON step report. |

Suites map onto workflow jobs:

| `--suite` | Reproduces |
| --- | --- |
| `Quick` | restore, build, architecture Quick, the filtered test run |
| `Handoff` | `handoff-windows-gates.yml`, job `architecture-and-tool-gates` |
| `Ci` | `ci.yml`, job `windows` |
| `Both` | Handoff then Ci over one build (default) |

Pick the suite that matches the branch, not the newest one. The suites mirror
the workflows as they exist on `main`, and a branch that forked before a
workflow was added does not necessarily satisfy it: running `Handoff` on
`codex/ux-platform-hardening-2026-09` fails at "Stage complete Android Tools
package" because that branch pins `tools/remote-notifications` at a commit
predating the supervised service observer the step asserts on. That is a
correct result about the wrong question. `Ci` is the suite for a branch whose
own workflow is `ci.yml` alone.

Nothing is pushed to GitHub to be tested. `--dirty` tests uncommitted work by
writing a throwaway commit object with `git stash create`, which leaves HEAD,
the index and the stash list alone.

## Fidelity, and where it ends

Faithful: the OS, pwsh, the SDK pinned by `global.json`, the step order, the
step commands, `CI` / `GITHUB_WORKSPACE` / `RUNNER_TEMP`, submodules at the
recorded commits, a clean tree.

Not faithful, and it matters:

* **The host is much faster than a runner** — 24 cores and 64 GB against
  GitHub's 4 and 16. Anything asserting on wall-clock passes here and can still
  fail on GitHub. Never tune a timing threshold against a green local run;
  A3.0 in `tests/architecture-gate/ProcessGateRunner.cs` is the worked example
  of writing such an assertion so machine speed cannot decide it.
* **The workspace is reused.** `git clean -xdff` covers this, which is why
  `Sync-CiWorkspace.ps1` runs it on the superproject and every submodule.
* **`actions/cache` is not reproduced.** The NuGet cache here is simply warm,
  so local runs are shorter than GitHub's. A cache-key change shows up on
  GitHub only. `artifacts/sdk/global-packages` is also the one path the
  workspace scrub leaves alone — the workflow restores it from cache, so an
  empty one would be less faithful, not more.
* **Secrets are absent.** Steps needing `MPT_OTA_SIGNING_KEY_BASE64` are out of
  scope. The private `external/NotifyApp` submodule is covered: `preflight.sh`
  passes a `gh auth token` over stdin, standing in for `MPT_SUBMODULE_PAT`.
* **Someone has to be logged in.** SSH on Windows authenticates with a network
  logon and no interactive session, and the Credential Manager refuses to answer
  without one — `CredRead` returns 1312 `ERROR_NO_SUCH_LOGON_SESSION` where a
  runner returns 1168 `ERROR_NOT_FOUND`. `Start-InSession.ps1` therefore runs the
  job as a scheduled task with `-LogonType Interactive`, inside the session the
  user is logged into, which restores the runner's behaviour. The cost is that
  the host needs an active session: `query session` must show one for this user.
  Output is tailed out of the task's log, so the caller still sees a live stream
  and the real exit code.
* **macOS is not covered at all.** Six workflows run on `macos-14`; there is no
  Mac in this rig.
