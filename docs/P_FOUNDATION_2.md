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
- Added named `MyPowerTools.Abstractions` contract aliases for the review-facing API names: `IMptModuleFactory`, `IModuleContext`, `ICommandContext`, `ModuleStatus`, `ModuleCommand`, `CommandResult`, `SettingsSchema`, `ModuleEvent`, and `UiSurfaceDescriptor`.
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

### Shell UI Architecture

- Added `src/MyPowerTools.UI/Themes/MptTheme.axaml` as the first shared Avalonia `ResourceDictionary` for Shell brushes, spacing, radius, and text styles.
- Updated `App.cs` to load the shared theme through `avares://MyPowerTools.UI/Themes/MptTheme.axaml`.
- Added typed AXAML page views for Dashboard, Command Palette, Settings, Logs, Package Manager, and Diagnostics under `src/MyPowerTools.Shell.Avalonia/Views`.
- Added `src/MyPowerTools.Shell.Avalonia/ViewModels/ShellPageViewModels.cs` with control-free page viewmodels and HostControl protocol mapping factories.
- Added static guardrails for typed AXAML bindings, thin code-behind files, theme-token usage, and ViewModel independence from Avalonia controls.
- Wired the Dashboard page to `DashboardView` and `DashboardViewModel`, preserving Details and quick-action command execution through ViewModel commands.
- Removed the old imperative `BuildDashboardCard` path from `MainWindow.cs`.
- Added and wired `NotificationsView` / `NotificationsViewModel`, replacing the old imperative notification list construction.
- Added and wired `ModulesView` / `ModulesViewModel`, preserving Details, Settings, Logs, and Enable/Disable actions through ViewModel commands.
- Removed the old imperative `BuildModuleSummaryCard` path from `MainWindow.cs`.
- Added and wired `LogsView` / `LogsViewModel`, preserving module selection and log tail rendering through ViewModel commands and data binding.
- Removed the old imperative `FillLogsAsync` path from `MainWindow.cs`.
- Added and wired `PackageManagerView` / `PackageManagerViewModel`, preserving install, rollback, repair, uninstall, and module detail actions through ViewModel commands.
- Removed the old imperative package operations and package action row builders from `MainWindow.cs`.
- Added and wired `DiagnosticsView` / `DiagnosticsViewModel`, preserving runtime paths, transports, process controls, policy history, module diagnostics, command history, and broker audit rendering through data binding.
- Removed the old imperative runtime diagnostics builders from `MainWindow.cs`.
- Wired the right-side Command Palette panel to `CommandPaletteView` / `CommandPaletteViewModel`, preserving command execution through ViewModel commands.
- Removed the old `_commandPanel.Children` imperative command-list rendering path from `MainWindow.cs`.
- Wired Settings Center to `SettingsCenterView` / `SettingsCenterViewModel`, preserving module selection, schema-backed editors, raw JSON fallback, and settings save through ViewModel commands.
- Removed the old imperative settings editor filler and schema field control builders from `MainWindow.cs`.
- Wired Module Detail to `ModuleDetailView` / `ModuleDetailViewModel`, preserving module toggle, permissions, requirements, diagnostics, and command execution through ViewModel commands.
- Removed the old imperative module detail hero, permission, requirement, diagnostic, and command builders from `MainWindow.cs`.
- Wired Permission Prompt and Broker Audit sidebars to `PermissionPromptView` / `BrokerAuditView` with ViewModels.
- Removed the old imperative permission prompt and broker audit entry builders from `MainWindow.cs`.
- Wired unavailable/error pages to `UnavailablePageView` / `UnavailablePageViewModel`.
- Removed the old generic imperative `BuildPage` error wrapper from `MainWindow.cs`.
- Wired the Shell chrome layout to `ShellChromeView.axaml`, preserving navigation, search, refresh, content host, command panel, permission panel, broker audit panel, and status row through named controls.
- Removed the old imperative `BuildLayout`, `Header`, and dock helper layout construction from `MainWindow.cs`.
- Wired Shell navigation items and refresh action to `ShellChromeViewModel`.
- Removed the old imperative navigation button builder and navigation state update loop from `MainWindow.cs`.
- Bound the Shell status row and runner status text through `ShellChromeViewModel`.
- Removed direct status `TextBlock` fields from `MainWindow.cs`.
- Added `ShellCommandExecutionService` as the first Shell service-layer extraction for HostControl-backed command execution.
- Updated `ShellWorkspaceController` to consume command execution results from the service while keeping permission prompt presentation in the Shell workspace.
- Added `ShellRunnerEventService` to own Runner connection monitor, host event stream monitor, status text updates, runner status text updates, and recovery notifications.
- Updated `ShellWorkspaceController` to subscribe to runner/event service outputs while keeping page refresh decisions in the Shell workspace.
- Added `ShellHostActionService` to own HostControl-backed package operations, runtime process controls, runtime restart policy changes, and module enable/disable actions.
- Updated `ShellWorkspaceController` to consume Host action service results and keep page refresh choreography in the Shell workspace.
- Added `ShellSettingsService` to own settings patch construction, `UpdateSettingsAsync`, and RPC error mapping for Settings Center saves.
- Updated `ShellWorkspaceController` to consume Settings save results and keep ViewModel status refresh choreography in the Shell workspace.
- Added `ShellPageDataService` to own read-only HostControl page data loading and ViewModel factory mapping for Dashboard, Modules, Module Detail, Settings, Logs, Notifications, Packages, Diagnostics, Command Palette, and Broker Audit.
- Updated `ShellWorkspaceController` to consume page data service results while keeping View assignment, status assignment, and page refresh choreography in the Shell workspace.
- Added `ShellPageRefreshRouter` to own Host event to page refresh routing.
- Updated `ShellWorkspaceController` to execute refresh plans instead of carrying Host event routing rules inline.
- Added `ShellWorkspaceController` to own Shell page routing, command palette loading, permission prompt presentation, broker audit loading, keyboard shortcut handling, runner event subscriptions, package operations, diagnostics actions, settings saves, and module enable/disable choreography.
- Reduced `MainWindow.cs` to a 75-line bootstrap wrapper for Shell chrome construction, named host lookup, startup options, lifecycle hooks, and keyboard dispatch.
- Split the shared UI theme into concern-specific Avalonia resource dictionaries: `MptColors.axaml`, `MptSpacing.axaml`, `MptTypography.axaml`, and `MptDensity.axaml`.
- Kept `MptTheme.axaml` as the single Shell import point and wired it to the token dictionaries plus `Controls/MptControls.axaml`.
- Added AXAML component styles for module cards, status badges, metric tiles, command items, settings sections, settings fields, log rows, log viewers, notification items, permission prompts, empty/error/loading states, page headers, action bars, and action buttons.
- Expanded `MptControls.cs` with matching foundation control classes and class names for `MptSettingsField`, `MptLogRow`, `MptLoadingSkeleton`, `MptPageHeader`, and `MptActionBar`.
- Replaced remaining Shell AXAML raw `FontSize` values with typography tokens.
- Added C# typography constants to `MptTheme` and updated UI controls to use those constants instead of raw `FontSize` literals.
- Added deeper static style lint coverage for Shell/UI AXAML and C# files, including raw color literals, raw font-size literals, thin code-behind limits, and HostControl-free view code-behind.
- Rewired Shell AXAML views to use foundation component classes such as `MptModuleCard`, `MptMetricTile`, `MptSettingsSection`, `MptSettingsField`, `MptLogRow`, `MptNotificationItem`, `MptPermissionPrompt`, `MptCommandItem`, and `MptErrorState` instead of generic `MptCard` markup.
- Extended Shell UI snapshot metadata for Command Palette validation/execution states and Settings staged-diff/apply-failed states.
- Added HostControl command parameter descriptors and mapped module/static command parameters through Runtime, gRPC IPC, HostControl, and Shell services.
- Updated Command Palette ViewModels and AXAML to render basic text/boolean parameter forms and build JSON args for command execution.
- Added HostControl client and Shell command execution overloads that pass command args through `ExecuteCommandRequest.args`.
- Added Command Palette local parameter validation, execution preview text, per-command execution state, and result/error message binding.
- Added HostControl-backed command cancellation by invocation id and wired Command Palette running-state cancel actions.
- Added HostControl server-streaming command execution and wired Command Palette progress event rows for accepted/running/final states.
- Added gRPC IPC sidecar stdout/stderr drain with redacted diagnostic tails and process-level line counts.
- Added Runtime settings validate/store/apply sequencing through `UpdateSettingsWithApplyAsync`.
- Mapped settings validate/apply hooks through `IModuleTransportRuntime`, InProc modules, gRPC IPC modules, HostControl, and Shell save status.
- Extended HostControl settings snapshots with `apply_state` and `apply_message` so Shell can distinguish stored, applied, and apply-failed outcomes.
- Added Settings Center staged-change tracking, field-level dirty summaries, patch preview text, and save enablement only when edits are staged.
- Added Settings Center local validation preview for numeric, enum, object, array, and raw JSON edits before save.
- Added Settings apply-failure rollback policy for persistent settings snapshots, including `apply-failed-rolled-back` state propagation to Shell.
- Added Settings Center save-result summary rows for stored, applied, apply-failed, rolled-back, and RPC failure outcomes, with saved values accepted back into the staged-diff model.

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
- `Abstractions_project_exposes_named_plugin_contracts`
  verifies the abstractions project exposes the named plugin-facing contract types from the P-Foundation-2 review prompt.
- `P_foundation_2_ui_architecture_debt_is_tracked`
  verifies this document reflects the live shell line count and refactor target.
- `Shell_workspace_controller_owns_shell_orchestration`
  verifies `MainWindow.cs` delegates Shell workspace orchestration to `ShellWorkspaceController` and stays below the 250-line target.
- `Shell_axaml_mvvm_migration_scaffold_exists_with_typed_bindings`
  verifies the current Shell page migration set has AXAML views, typed bindings, theme tokens, and thin code-behind files.
- `Shell_viewmodels_are_control_free_and_map_host_protocol`
  verifies Shell viewmodels do not depend on Avalonia controls and can map HostControl dashboard and command data.
- `Shell_modules_page_is_wired_to_axaml_view_model`
  verifies Modules rendering uses `ModulesView` plus `ShellPageViewModelFactory.FromModules`, preserves the four module action commands, and removes the old imperative module summary builder.
- `Shell_module_detail_page_is_wired_to_axaml_view_model`
  verifies Module Detail rendering uses `ModuleDetailView` plus `ShellPageViewModelFactory.FromModuleDetail`, preserves toggle and command execution, and removes old detail builders.
- `Shell_dashboard_page_is_wired_to_axaml_view_model`
  verifies Dashboard rendering uses `DashboardView` plus `ShellPageViewModelFactory.FromDashboard` and that the old imperative dashboard card builder is gone.
- `Shell_notifications_page_is_wired_to_axaml_view_model`
  verifies Notifications rendering uses `NotificationsView` plus `ShellPageViewModelFactory.FromNotifications` and that the old imperative notification item path is gone.
- `Shell_logs_page_is_wired_to_axaml_view_model`
  verifies Logs rendering uses `LogsView` plus `ShellPageViewModelFactory.FromLogs`, preserves module picker commands, and removes the old imperative log filler path.
- `Shell_packages_page_is_wired_to_axaml_view_model`
  verifies Package Manager rendering uses `PackageManagerView` plus `ShellPageViewModelFactory.FromPackages`, preserves package operations, and removes old package builders.
- `Shell_diagnostics_page_is_wired_to_axaml_view_model`
  verifies Diagnostics rendering uses `DiagnosticsView` plus `ShellPageViewModelFactory.FromDiagnostics`, preserves process commands and diagnostic sections, and removes old diagnostics builders.
- `Shell_command_palette_is_wired_to_axaml_view_model`
  verifies the right-side Command Palette uses `CommandPaletteView` plus `ShellPageViewModelFactory.FromCommands`, preserves command execution, and removes the old `_commandPanel.Children` rendering path.
- `Shell_command_palette_parameter_form_builds_command_args`
  verifies Command Palette parameter ViewModels render text/boolean fields and build JSON args from edited form values.
- `Shell_command_palette_parameter_form_validates_preview_and_execution_state`
  verifies Command Palette parameter forms validate required and numeric fields, update execution preview text, and surface per-command execution results.
- `Shell_command_palette_progress_stream_records_events`
  verifies Command Palette command items record accepted, running, and final progress events from an async execution stream.
- `Shell_command_palette_cancel_command_updates_running_state`
  verifies Command Palette command items expose cancel actions while running and move to cancelled state after local cancellation.
- `Shell_command_parameter_contract_flows_through_hostcontrol`
  verifies command parameter descriptors, command stream RPCs, and cancel RPCs flow through proto, abstractions, static indexes, gRPC IPC, HostControl mapping, and Shell execution args.
- `Shell_command_execution_is_extracted_to_service`
  verifies HostControl unary and streaming command execution is owned by `ShellCommandExecutionService` and `ShellWorkspaceController` consumes service results for status and permission prompt presentation.
- `Shell_runner_events_are_extracted_to_service`
  verifies Runner connection and host event stream monitors are owned by `ShellRunnerEventService` and `ShellWorkspaceController` consumes service events.
- `Shell_host_actions_are_extracted_to_service`
  verifies package operations, runtime process controls, restart policy changes, and module enable/disable calls are owned by `ShellHostActionService`.
- `Shell_settings_page_is_wired_to_axaml_view_model`
  verifies Settings Center uses `SettingsCenterView` plus `ShellPageViewModelFactory.FromSettings`, preserves save command wiring, and removes the old imperative settings editor path.
- `Shell_settings_save_is_extracted_to_service`
  verifies settings patch construction, update calls, and RPC error mapping are owned by `ShellSettingsService` while `ShellWorkspaceController` owns refresh choreography.
- `Shell_settings_page_tracks_staged_diff_before_save`
  verifies Settings Center keeps original field values, tracks staged changes, renders patch preview text, previews validation errors, enables save only after valid edits are staged, and accepts successful save results back into the dirty-state model.
- `Settings_update_validates_stores_and_applies_runtime`
  verifies Runtime settings updates call transport validation before storage, apply the stored snapshot afterward, and publish `applyState`.
- `Settings_apply_failure_rolls_back_persisted_update`
  verifies Runtime rolls settings back to the previous revision when module apply fails and emits `apply-failed-rolled-back`.
- `Settings_validate_apply_chain_is_wired_through_hostcontrol_and_shell`
  verifies settings validate/apply hooks flow through module proto, Runtime transport contracts, HostControl, gRPC IPC, and Shell save status.
- `Runtime_cancel_command_stops_running_invocation`
  verifies Runtime tracks active command cancellation sources, cancels a running transport invocation, and records the cancelled command history state.
- `Runtime_command_stream_emits_progress_and_final_result`
  verifies Runtime emits accepted, running, and terminal command progress events with the final execution result attached.
- `HostControl_execute_command_stream_exposes_progress_events`
  verifies HostControl maps Runtime command progress events into the server-streaming RPC response.
- `Runtime_drains_grpc_sidecar_stdio_into_process_diagnostics`
  verifies gRPC sidecar stdout/stderr streams are drained into process diagnostics with line counts and tail messages.
- `Shell_read_only_page_data_is_extracted_to_service`
  verifies read-only HostControl data loading and page ViewModel factory mapping are owned by `ShellPageDataService` while `ShellWorkspaceController` owns page view assignment.
- `Shell_host_event_refresh_routing_is_extracted_to_service`
  verifies Host event refresh routing is owned by `ShellPageRefreshRouter` and covers command and settings event plans.
- `Shell_permission_and_audit_sidebars_are_wired_to_axaml_view_models`
  verifies Permission Prompt and Broker Audit sidebars use AXAML views and ViewModel factories, and removes old sidebar builders.
- `Shell_unavailable_page_is_wired_to_axaml_view_model`
  verifies unavailable/error pages use `UnavailablePageView` with a typed ViewModel and removes the old generic `BuildPage` helper.
- `Shell_chrome_layout_is_wired_to_axaml_view`
  verifies the Shell three-column chrome is defined in `ShellChromeView.axaml`, preserves named hosts, binds navigation/refresh/status text to `ShellChromeViewModel`, and removes old navigation builders plus direct status `TextBlock` fields.
- `Shell_theme_resource_dictionary_is_loaded_and_defines_design_tokens`
  verifies the Shell app loads the shared UI theme entry point and that color, spacing, typography, density, and component dictionaries are split by concern.
- `Shell_ui_component_styles_cover_foundation_controls`
  verifies the foundation UI control classes have matching AXAML component styles.
- `Shell_axaml_views_use_foundation_component_classes`
  verifies Shell AXAML views use foundation component class names and do not continue rendering repeated cards with the generic `MptCard` class.
- `Shell_axaml_views_use_theme_tokens_without_inline_colors`
  verifies Shell AXAML views use theme resources instead of inline colors.
- `Shell_static_style_lint_rejects_raw_axaml_and_csharp_ui_literals`
  verifies Shell/UI AXAML and UI C# files avoid raw color literals and raw `FontSize` values outside token surfaces.
- `Shell_code_behind_files_stay_thin_and_hostcontrol_free`
  verifies all Shell view code-behind files only load AXAML, remain under the thin-file limit, and avoid HostControl/data loading.
- `Ui_shell_snapshot_writes_key_surface_matrix`
  verifies Shell PNG snapshot metadata covers the key Shell page surfaces, keyboard/focus evidence, Command Palette validation/execution states, and Settings staged-diff/apply-failed states.

## Current UI Architecture State

- `src/MyPowerTools.Shell.Avalonia/MainWindow.cs` current: 75 lines.
- `MainWindow.cs` target <= 250 lines.
- AXAML + MVVM migration: Dashboard, Modules, Module Detail, Logs, Notifications, Package Manager, Diagnostics, Settings Center, Permission Prompt, Broker Audit, unavailable/error pages, Shell chrome layout/navigation/status row, the right-side Command Palette list, command execution service extraction, runner/event service extraction, Host action service extraction, Settings save service extraction, read-only page data service extraction, Host event refresh routing extraction, and Shell workspace controller extraction are now live on typed AXAML plus ViewModels/services.
- Component AXAML library: foundation component styles now cover module cards, status badges, metric tiles, command items, settings sections, settings fields, log rows, log viewers, notification items, permission prompts, empty/error/loading states, page headers, action bars, and action buttons; Shell views now use the foundation component class names instead of repeated generic card markup.
- Token `ResourceDictionary` split: `MptColors.axaml`, `MptSpacing.axaml`, `MptTypography.axaml`, and `MptDensity.axaml` are loaded through `MptTheme.axaml`.
- Static style lint for shell UI: covers split-token dictionaries, component-style coverage, AXAML raw colors/font sizes, C# raw colors/font sizes in Shell/UI control surfaces, thin code-behind files, and ViewModel independence from Avalonia controls.
- Shell snapshot gate: writes PNG-backed metadata for Dashboard, Command Palette, Settings Center, Module Detail, Logs, Notifications, Permission Prompt, Degraded Module, Package Manager, and Runtime Diagnostics, including keyboard/focus evidence and the new command/settings states.
- Command palette typed argument binding: HostControl parameter descriptors, Shell text/boolean parameter editors, JSON args pass-through, required/numeric validation, execution preview, per-command result/error state, cancel action wiring, and progress streaming are done.
- Settings validate/apply chain with staged diff UX: Runtime validate/store/apply sequencing, HostControl apply state fields, Shell save status messages, staged-change tracking, field-level dirty summaries, patch preview text, local validation preview, save enablement, apply-failure rollback, and structured save-result summaries are wired.
- Sidecar process supervision: gRPC IPC sidecars have native IPC startup, initialization handshake, process diagnostics, manual restart, restart pause/resume/expiry policy, crash-loop limit, crash recovery, process-tree cleanup, and stdout/stderr drain with diagnostic tails.

The existing shell has UI snapshot gates, keyboard shortcut tests, centralized color-token checks, typed component styles, and Shell views wired onto foundation component class names.

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
| Sidecar default for complex modules | Complex modules prefer sidecar transport | Done: sidecar-capable modules stay on sidecar paths, with startup handshake, diagnostics, restart policy, crash recovery, process-tree cleanup, and stdout/stderr drain |
| MainWindow size | target <= 250 lines | current: 75 lines |
| AXAML + MVVM | Main shell split into views/viewmodels | Started: Dashboard, Modules, Module Detail, Logs, Notifications, Package Manager, Diagnostics, Settings Center, Permission Prompt, Broker Audit, unavailable/error pages, Shell chrome layout/navigation/status row, the right-side Command Palette list, command execution service extraction, runner/event service extraction, Host action service extraction, Settings save service extraction, read-only page data service extraction, Host event refresh routing extraction, and Shell workspace controller extraction wired to AXAML/MVVM/service layers; thirteen typed views and control-free viewmodels exist |
| Component library | Reusable AXAML controls and tokens | Done for foundation Shell surfaces: component styles, matching C# classes, and Shell view class usage cover cards, badges, metrics, command items, settings sections/fields, logs, notifications, prompts, states, headers, action bars, and action buttons |
| Style lint | Static lint over shell UI style usage | Started: split-token, component-style, raw color/font-size, code-behind, and viewmodel guardrails |
| Pixel snapshots | Module and Shell PNG snapshot evidence | Done for module dashboard cards and 10 Shell surfaces with keyboard/focus, command validation/execution, and settings staged-diff/apply-failed metadata |
| Command palette | Typed args, validation UI, cancellation, and progress streaming | Parameter descriptors, text/boolean editors, args pass-through, required/numeric validation, execution preview, per-command result/error state, cancel action wiring, and accepted/running/final progress rows done |
| Settings UX | Validate/apply chain with clear states | Runtime validate/store/apply sequencing, Shell save apply-state messages, staged diff, patch preview, local validation preview, apply-failure rollback, and structured save-result summaries wired |

## Validation Evidence

The current slice has been validated locally with:

```text
dotnet build MyPowerTools.slnx --no-restore
dotnet run --no-build --project src\MyPowerTools.Cli -- package sign-local modules
dotnet test MyPowerTools.slnx --no-build
dotnet run --no-build --project src\MyPowerTools.Cli -- validate modules
dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts
dotnet run --no-build --project src\MyPowerTools.Cli -- ui check modules
dotnet run --no-build --project src\MyPowerTools.Cli -- ui snapshot --surface dashboard-card --theme light --size 1366x768 --density normal --out artifacts\ui-snapshots
dotnet run --no-build --project src\MyPowerTools.Cli -- ui shell-snapshot --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots
dotnet run --no-build --project src\MyPowerTools.Cli -- package trust modules --strict
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1
```

Latest observed result before packaging:

```text
Build: passed, 0 warnings, 0 errors
Tests: passed, 139 passed, 0 failed, 0 skipped
Module validation: 5 packages valid
Contract validation: 5 packages, 7 modules passed
UI gate: passed
Module UI snapshots: 7 dashboard-card PNG snapshots
Shell UI snapshots: 10 Shell PNG snapshots
Strict trust check: 5 signatures accepted under local policy
Template validation: 6 templates passed manifest validation, UI gate, .NET builds, and Python syntax checks
```

The packaging step for external review should run the same validation again and include the current Git commit hash in the archive manifest.

## Recommended Next Slice

1. Run final package validation, commit the review-ready build, and create the external review archive with the current Git commit hash.
