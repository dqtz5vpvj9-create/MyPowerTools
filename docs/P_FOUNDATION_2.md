# P-Foundation-2: Plugin Isolation + UI Architecture Refactor

Status: in progress  
Review date: 2026-07-04  
Current review build: first verified slice after P8 production closure

## Goal

P-Foundation-2 strengthens the production foundation that GPT Pro flagged after P8:

- isolate plugin contracts from host runtime internals;
- make in-process .NET modules unloadable and shadow-copied;
- keep complex modules on the sidecar path by default;
- reduce the Avalonia shell toward AXAML + MVVM with shared component and token systems.

This document tracks the current slice for external review. It is an audit handoff, not a completion claim for the full phase.

## Completed In This Slice

### Contract Boundary

- Added `src/MyPowerTools.Abstractions`.
- Moved plugin-facing contracts into the abstractions assembly while preserving the existing `MyPowerTools.Runtime` namespace for source compatibility.
- Updated production module projects to reference `MyPowerTools.Abstractions` instead of `MyPowerTools.Runtime`.
- Added `MptLogRedactor` to keep module log redaction available without taking a runtime dependency.
- Added `runtimePolicy` schema and C# manifest model fields for preferred runtime, InProc rules, and sidecar rules.
- Updated module templates with runtime policy examples for InProc, sidecar, service facade, WebView, and stdio compatibility modules.

### In-Process .NET Isolation

- Reworked `MyPowerTools.ModuleHost.InProcDotNet` to use a collectible `AssemblyLoadContext`.
- Added `AssemblyDependencyResolver` for module-local dependency resolution.
- Added shadow-copy loading under the runtime cache before loading disk-backed module assemblies.
- Shared only the host contract assembly and selected framework abstractions across the load boundary.
- Added an unload probe through `WeakReference` and GC collection after module disposal.
- Exposed InProc lifecycle through runtime process diagnostics with `loaded`, `unloaded`, and `pending-runner-restart` states.
- Added a manual Runner restart policy marker when a module fails to release its collectible load context.
- Verified side-by-side loading for generated plugins with the same dependency assembly name and different dependency versions.
- Verified module update behavior loads from shadow cache while the original package DLL can be replaced.

### Acceptance Coverage

- `Inproc_disk_module_uses_collectible_load_context_and_unloads`
  verifies disk-backed in-process modules load outside `AssemblyLoadContext.Default` and unload after disposal.
- `Runtime_restarts_clean_inproc_module_by_unloading_context`
  verifies the runtime can unload a clean InProc module through the diagnostics process-control path.
- `Runtime_marks_inproc_unload_failure_as_pending_runner_restart`
  verifies a module that holds a default-context event subscription is marked `pending-runner-restart`.
- `Inproc_plugins_with_conflicting_dependency_versions_load_in_separate_contexts`
  verifies separate plugin load contexts can load different versions of the same dependency assembly.
- `Inproc_module_update_uses_shadow_copy_instead_of_original_package_dll`
  verifies a loaded module runs from shadow cache, keeps using the old bytes while original package files are replaced, and picks up the replacement after reload.
- `Module_schema_accepts_runtime_policy_and_reader_maps_fields`
  verifies `runtimePolicy` validates and maps into the package reader model.
- `Module_schema_rejects_invalid_runtime_policy`
  verifies invalid runtime preferences and unsafe timing values fail module schema validation.
- `Production_module_projects_reference_abstractions_not_runtime`
  verifies production module project files depend on abstractions, not the runtime project.
- `Runtime_shell_and_host_do_not_reference_concrete_module_projects`
  verifies Runtime, HostControl, and Shell avoid references to concrete production module projects.
- `P_foundation_2_ui_architecture_debt_is_tracked`
  verifies this document reflects the live shell line count and refactor target.

## Current UI Architecture State

- `src/MyPowerTools.Shell.Avalonia/MainWindow.cs` current: 1845 lines.
- `MainWindow.cs` target <= 250 lines.
- AXAML + MVVM migration: pending.
- Component AXAML library: pending.
- Token `ResourceDictionary` split: pending.
- Static style lint for shell UI: partially covered by existing token tests, deeper lint pending.
- Command palette typed argument binding: pending.
- Settings validate/apply chain with staged diff UX: pending.

The existing shell already has UI snapshot gates, keyboard shortcut tests, and centralized color-token checks. The next slice should turn those guardrails into a structural refactor that reduces imperative UI construction in `MainWindow.cs`.

## Acceptance Matrix

| Area | Target | Current State |
| --- | --- | --- |
| Abstractions project | Plugin contracts live in `MyPowerTools.Abstractions` | Done |
| Production module dependency direction | Modules reference abstractions only | Done |
| Host dependency direction | Runtime/Shell/Host avoid concrete module references | Done |
| Runtime policy schema | `runtimePolicy.preferred`, `allowInProc`, `inProcRules`, and `sidecarRules` | Done |
| InProc isolation | Collectible ALC + resolver + shadow copy | Done for disk-backed modules |
| InProc dependency isolation | Conflicting dependency versions load side by side | Done |
| InProc shadow-copy update | Loaded module uses cache while package DLL is replaceable | Done |
| InProc unload handling | Unload probe and failure surfaced to runtime policy | Done for clean unload and pending-runner-restart diagnostics |
| Sidecar default for complex modules | Complex modules prefer sidecar transport | Existing manifests keep sidecar-capable modules on sidecar paths |
| MainWindow size | target <= 250 lines | current: 1845 lines |
| AXAML + MVVM | Main shell split into views/viewmodels | Pending |
| Component library | Reusable AXAML controls and tokens | Pending |
| Style lint | Static lint over shell UI style usage | Pending |
| Command palette | Typed args and validation UI | Pending |
| Settings UX | Validate/apply chain with clear states | Pending |

## Validation Evidence

The current slice has been validated locally with:

```text
dotnet build MyPowerTools.slnx --no-restore
dotnet run --no-build --project src\MyPowerTools.Cli -- package sign-local modules
dotnet test MyPowerTools.slnx --no-build
dotnet run --no-build --project src\MyPowerTools.Cli -- validate modules
dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts
dotnet run --no-build --project src\MyPowerTools.Cli -- package trust modules --strict
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1
```

Latest observed result before packaging:

```text
Build: passed, 0 warnings, 0 errors
Tests: passed, 98 passed, 0 failed, 0 skipped
Module validation: 5 packages valid
Contract validation: 5 packages, 7 modules passed
Strict trust check: 5 signatures accepted under local policy
Template validation: 6 templates passed manifest validation, UI gate, .NET builds, and Python syntax checks
```

The packaging step for external review should run the same validation again and include the current Git commit hash in the archive manifest.

## Recommended Next Slice

1. Move dashboard, command palette, settings, logs, package manager, and diagnostics UI into AXAML views with viewmodels.
2. Introduce shell token dictionaries and component styles under the UI project.
3. Add a static style lint that scans AXAML and C# UI files.
4. Add command palette parameter editors and settings validate/apply staging.
5. Keep sidecar process supervision as the next plugin-runtime depth item after the UI architecture slice starts.
