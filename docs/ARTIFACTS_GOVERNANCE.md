# Artifacts Governance

## The problem this solves

`artifacts/` grew to 12 GB because it had no owner. Over 40 scripts write into it,
and each one cleaned up only its own corner:

- `scripts/build-sdk.ps1` pruned `global-packages/mypowertools.*` and left every
  other package to accumulate forever.
- `scripts/publish-windows.ps1` cleared `release/win-x64/` but kept every installer
  and every OTA delta ever built.
- `scripts/create-source-bundle.ps1` produced a new date-stamped archive per day.
- Three `verify-*-service.ps1` scripts each left a report keyed by a random suffix.

Nothing had a whole-tree view, so nobody could answer "is this 2 GB directory still
needed?". Two package caches totalling 4 GB (`sdk/global-packages-activation` and
`sdk/global-packages-websurface`) outlived the scripts that created them and sat
unnoticed for a month.

The fix is not a cleanup script. It is a contract that makes an unmanaged path
impossible to add quietly.

## The contract

Every path under `artifacts/` must be declared in
[`scripts/artifacts-policy.json`](../scripts/artifacts-policy.json) with a class and
a retention rule. The class fixes the lifecycle; the retention rule fixes when it
goes away.

| Class | Meaning | Typical retention |
| --- | --- | --- |
| `cache` | Re-downloadable or rebuildable input. Losing it costs time only. | `manual` — reclaimed only when you ask for the class by name |
| `build` | Compiler output for `src/` projects. | `manual` — reclaiming forces a full rebuild |
| `scratch` | Single-run staging. Surviving past its window means a leak. | `age`, hours to a few days |
| `output` | Build products for the current and recent versions. | `count`, keep the newest N |
| `evidence` | Verification and audit records. | `age`, days to weeks |

Retention modes:

- `manual` — removed only when the class is named explicitly.
- `age` — removed once older than `maxAgeHours` or `maxAgeDays`.
- `count` — keep the `keep` newest matches; `perParent: true` groups by parent
  directory, so `tools/*/*` keeps N versions *per tool* rather than N overall.
- `pinned` — never removed automatically; requires `-Force`.

Entries are matched in declaration order and the first match wins, so put specific
globs before wildcards. A `*` matches within one path segment, which is why
`tools/*/*` matches `tools/adb-forwarder/0.2.0` but not `tools/adb-forwarder`.

Each entry also records the `producer` script and a one-line `purpose`. That is what
makes orphans detectable later: when a producer disappears, its entry is left
pointing at a script that no longer exists.

## Adding a new artifacts path

1. Write to a path under `artifacts/`.
2. Add an entry to `scripts/artifacts-policy.json` naming the class, the retention
   rule, the producing script, and the purpose.
3. Run `pwsh.exe -NoLogo -NoProfile -File scripts/check-artifacts-governance.ps1`.

If you skip step 2, CI fails with the path listed as undeclared. This is deliberate:
choosing a lifecycle is part of adding a producer, not a follow-up chore.

## Commands

Inspect usage and what the current rules would remove:

```powershell
pwsh.exe -NoLogo -NoProfile -File scripts/prune-artifacts.ps1 -Report
```

Reclaim everything the automatic rules allow, previewing first:

```powershell
pwsh.exe -NoLogo -NoProfile -File scripts/prune-artifacts.ps1 -WhatIf
pwsh.exe -NoLogo -NoProfile -File scripts/prune-artifacts.ps1
```

Reclaim a `manual` class when you need the space back and accept the rebuild or
re-download cost:

```powershell
pwsh.exe -NoLogo -NoProfile -File scripts/prune-artifacts.ps1 -Class cache
pwsh.exe -NoLogo -NoProfile -File scripts/prune-artifacts.ps1 -Class build
```

Remove undeclared paths, after reviewing them:

```powershell
pwsh.exe -NoLogo -NoProfile -File scripts/prune-artifacts.ps1 -IncludeUnclassified -WhatIf
```

## Where the gate runs

`scripts/Start-MyPowerTools-Dev.ps1` runs the check after each dev update in
advisory mode. It warns and never blocks, because a disk-hygiene notice must not
stop a dev loop. Pass `-SkipArtifactsCheck` to silence it.

CI runs it as the `Artifacts Governance Gate` step with `-Enforce`, which fails the
build on undeclared paths. Budget overruns stay advisory there and only fail with
`-EnforceBudget`; path coverage is a review contract, while a budget is an
operational signal about one machine's disk.

Measuring the tree is the expensive part, so the result is cached in
`artifacts/.governance-cache.json` and refreshed at most once every 24 hours. Use
`-Refresh` to force a fresh measurement, or `-SkipBudget` to validate coverage only.

## Centralised build output

`src/Directory.Build.props` enables the .NET artifacts output layout, so every
`src/` project builds into `artifacts/build/bin/<project>/<lowercase configuration>`
instead of its own `bin/` and `obj/`. Roughly 7.9 GB of compiler output used to be
spread across some 60 project directories where no policy could reach it; it is now
a single declared `build` entry that can be measured and reclaimed in one step.

Two consequences worth knowing:

- The configuration segment is lowercase, and a runtime identifier is appended with
  an underscore: `debug`, `debug_win-x64`.
- Anything that needs a `src/` project's output must derive it from that layout.
  `scripts/build-sdk.ps1`, `scripts/smoke.ps1`, and the `WebToolHostBuildOutput`
  property in `src/MyPowerTools.Shell.Avalonia` were updated accordingly.

This is scoped to `src/` on purpose. Tool submodules under `tools/` and the
scaffolding templates declare dotnet surface assemblies with paths like
`bin/Debug/net10.0/X.dll`, which the artifacts layout would invalidate. Projects
built underneath `artifacts/` itself, such as template validation builds, also keep
the default layout.
