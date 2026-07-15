# MyPowerTools UI Rebuild Audit

Date: 2026-07-06

Scope: P-UI-Foundation audit for the Avalonia Shell. The runtime blocker fixes from the prior pass are preserved, and this file records the baseline UI failures plus the current UI repair status.

## Baseline Evidence

Command:

```powershell
dotnet run --project src\MyPowerTools.Cli -- ui shell-snapshot --full-shell --fixture-only --theme light --size 1366x768 --density normal --out artifacts\ui-before
```

Artifacts:

| Evidence | Path |
| --- | --- |
| Contract manifest | `artifacts/ui-before/shell-ui-snapshot-manifest.json` |
| Real screenshot manifest | `artifacts/ui-before/shell-real-screenshot-manifest.json` |
| Dashboard | `artifacts/ui-before/real-dashboard.light.normal.1366x768.png` |
| Command Palette | `artifacts/ui-before/real-command-palette-with-params.light.normal.1366x768.png` |
| Module Detail | `artifacts/ui-before/real-module-detail-degraded.light.normal.1366x768.png` |
| Settings | `artifacts/ui-before/real-settings-dirty-state.light.normal.1366x768.png` |
| Logs | `artifacts/ui-before/real-logs-long-lines.light.normal.1366x768.png` |
| Notifications | `artifacts/ui-before/real-notifications-list.light.normal.1366x768.png` |
| Packages | `artifacts/ui-before/real-packages.light.normal.1366x768.png` |
| Diagnostics | `artifacts/ui-before/real-diagnostics-wide.light.normal.1366x768.png` |

The baseline manifest records `dataSource=sample-fixture`, `usesHostControlData=false`, eight full-shell screenshots, and ten contract snapshots.

## Findings

| Area | Failure | Evidence | Required Fix |
| --- | --- | --- | --- |
| Shell IA | `ShellChromeView` uses a permanent three-column layout with a 360px right rail for Command Palette, Permission, and Audit. | `src/MyPowerTools.Shell.Avalonia/Views/ShellChromeView.axaml` column definitions `220,*,360`; `artifacts/ui-before/real-dashboard.light.normal.1366x768.png`. | Replace with sidebar, top bar, content host, status bar, and global overlay host. |
| Dashboard | Dashboard content is squeezed by the permanent command rail and lacks a product-grade summary/action row. | `DashboardView.axaml`; Dashboard screenshot. | Dashboard gets full width, page header actions, metrics, responsive module grid, and no command/audit rail. |
| Command Palette | Command Palette is docked to the page shell and rendered as a secondary panel. | `ShellChromeView.axaml`; `CommandPaletteView.axaml`. | Palette opens as centered global overlay from search, keyboard, or Commands nav. |
| Permission Prompt | Permission prompt is mounted inside the right rail. | `ShellWorkspaceController.Commands.cs`; `ShellChromeView.axaml`. | Prompt becomes modal/sheet overlay and has a clear command/audit context. |
| Audit | Broker audit is always present in the right rail. | `BrokerAuditView.axaml`; `ShellWorkspaceController.Commands.cs`. | Audit details move into command result and Diagnostics page evidence. |
| Theme Tokens | Token set is incomplete and uses old colors. | `src/MyPowerTools.UI/Themes/MptColors.axaml`; missing `MptRadii.axaml`, `MptShadows.axaml`, `MptAnimations.axaml`. | Add the full target token suite and load it through `MptTheme.axaml`. |
| Raw Controls | Shell page AXAML still uses raw `Button`, `TextBox`, `CheckBox`, `ComboBox`, direct widths, and direct heights. | `rg '<Button|<TextBox|<ComboBox|<CheckBox|MinWidth="|MinHeight="' src\MyPowerTools.Shell.Avalonia\Views`. | Replace production controls with MPT components/styles and tokenized dimensions. |
| UI Gate | Current lint catches colors, spacing, and font sizes, but misses permanent command rail, default Button usage, raw control dimensions, duplicate headings, and fixture labels in live evidence. | `src/MyPowerTools.UI/UiSurfaceGate.cs`. | Add P-UI-Foundation lint rules MPTUI001-MPTUI015 and fail semantic UI regressions. |
| Evidence Matrix | Existing screenshot command writes useful fixture evidence but lacks page filters, mode naming, matrix coverage, and semantic rejection for clipped/default/docked UI. | `mpt ui shell-snapshot`; manifests in `artifacts/ui-before`. | Extend CLI/evidence workflow to cover fixture and live-runner full-shell matrix. |

## Subgoal Status

| Subgoal | Status | Completion Reason |
| --- | --- | --- |
| Baseline screenshots | Complete | `artifacts/ui-before` contains real full-shell PNGs and manifests for the requested pages. |
| Production closure state | Complete for UI scope | `.codex/project-state.json` now records `currentPhase=P-UI-Foundation UI acceptance complete`, `lastCompletedPhase=P-UI-Foundation`, and `productionClosure=false` for the broader release/hardware/signing scope. |
| Shell IA rebuild | Complete | Updated ShellChrome screenshots show the Dashboard without a permanent command rail and Command Palette as a global overlay. |
| Token suite | Complete | The eight target AXAML token files are loaded by `MptTheme.axaml`. |
| Component replacement | Complete | Shell pages use MPT input/action controls and `MPTUI012` blocks raw Shell `Button/TextBox/CheckBox/ComboBox` regressions. |
| UI gate | Complete | Semantic MPTUI rules are implemented and final `ui check modules` passed. |
| Acceptance docs | Complete | `docs/UI_ACCEPTANCE.md` records final evidence paths, per-subgoal completion basis, and validation results. |

## Production Closure Position

P-UI-Foundation UI acceptance is complete. Runtime fixes stay preserved as historical support, and this UI-only phase keeps broader production closure false. The completed hardening pass fixed real screenshot page filtering, screenshot manifest coverage, lint rule alignment, compact Command Palette clipping, screenshot test isolation, and refreshed fixture/live-runner visual evidence.
