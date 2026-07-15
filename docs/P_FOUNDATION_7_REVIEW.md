# P-Foundation-7 Review Notes

Date: 2026-07-06

## Codex Self Description

I treated the P7 document as a floor, then audited adjacent runtime behavior while implementing it. I focused on local production correctness: deterministic hotkey registration, observable notification/event refresh, cancellable module work, bounded idempotency, explicit diagnostics, UI lint enforcement, and regression tests that fail on the original gaps.

The later final closure pass extended this slice with HostControl IPC authentication, persistent module event cursors/history, release Runner relative data-root hardening, gRPC cancel semantics, stream cleanup hardening, AndroidTools streaming output bounds, evidence semantic checks, full evidence packaging, release smoke, and final acceptance docs. External hardware/service validation remains tracked separately.

## Subgoal Closure

| Subgoal | Implementation | Why I Consider It Complete |
|---|---|---|
| Hotkey re-registration | Added `RunnerHotkeySynchronizer` with `id -> gesture` tracking, unregister/register on gesture change, command binding updates for `CommandArgsJson`, and hotkey request creation from persisted args. | `Runner_hotkey_sync_reregisters_gesture_updates_args_and_follows_enable_state` verifies initial registration, gesture change OS unregister/register, args propagation, reset, disable unregister, and enable re-register. |
| NotificationCenter live refresh | Module events that qualify as alerts now call `PublishNotification`, which also emits `notification.created`. Shell routing already reloads Notifications on that event. | `Module_event_alert_creates_notification_event_and_shell_refresh_plan` verifies a `watch.alert` module event creates a NotificationCenter entry, emits `notification.created`, and routes to `ReloadNotifications`. |
| Module-level cancellation | Added `IModuleTransportRuntime.CancelCommandAsync`; Runtime tracks active runtime targets and forwards cancellation; HostControl maps async cancellation; gRPC IPC calls ModuleControl `CancelCommand` only on an existing initialized host; AndroidTools.Powertoold tracks invocation cancellation tokens; AndroidTools shell execution kills the child process tree on timeout/cancel. Shell records cancel accepted/rejected evidence in command progress. | `Grpc_cancel_without_active_host_does_not_start_sidecar` verifies a cancel request with no active host returns `module-cancel-not-running` and leaves process diagnostics empty. Existing cancellation tests verify host cancellation stops running commands; the module transport result surfaces accepted/rejected/module-error states. |
| Streaming sidecar failure | `GrpcIpcModuleRuntime.ExecuteCommandStreamAsync` catches restart-worthy stream failures, removes the dead host, emits a terminal failed event with `MPT_RUNTIME_UNAVAILABLE`, and suppresses cleanup exceptions after that terminal event. | `Runtime_streaming_sidecar_crash_emits_runtime_unavailable_terminal_failure` starts a real sample sidecar stream, receives one nonterminal event, kills the sidecar, then verifies a terminal runtime-unavailable failure. `Grpc_stream_cleanup_exceptions_are_suppressed_after_terminal_failure` guards the cleanup path. |
| AndroidTools streaming bounds | AndroidTools Remote Commands shell streaming now uses a bounded channel, max streamed line events, stdout/stderr byte caps, max single-line byte cap, `output.truncated` events, and final result truncation metadata. | `AndroidTools_remote_shell_command_stream_truncates_unbounded_output` emits 1205 lines, verifies stdout events stay bounded at 1000, observes `output.truncated`, and checks final result `truncated=true`. |
| Bounded idempotency cache | Replaced the unbounded `_executions` dictionary with `InvocationExecutionCache` using max count 1000 and 30-minute TTL by default. Completed entries are trimmed on access and completion; failed factory exceptions are removed. | `Invocation_execution_cache_deduplicates_completed_results_and_evicts_old_entries` verifies duplicate invocation coalescing, completed result reuse, max-count trimming, and TTL expiry behavior. |
| Split runtime lifecycle diagnostics | `RuntimeModuleDiagnostics` now carries `ModuleEnabledState`, `TransportActiveState`, and `ToolRuntimeState`; HostControl proto and mapping expose the fields. Tool state is explicit for production modules even when their start/stop surface remains degraded/partial. | `Runtime_diagnostics_split_module_transport_and_tool_runtime_state` verifies enabled/transport/tool fields for loaded and disabled modules, plus nonempty production-module tool runtime states. |
| UI component-layer lint | `UiSurfaceGate.CheckShellSource` now scans `src/MyPowerTools.UI/**/*.cs` as well as Shell C# and AXAML. Raw `Brush.Parse`, `Brushes.*`, `new Thickness(number)`, `new CornerRadius(number)`, and raw font sizes fail outside token files. `MptThemeTokens` centralizes theme colors, font sizes, spacing, and radii; `MptControls` and Shell screenshot code use named tokens. | `Ui_surface_gate_scans_component_csharp_layer_and_requires_tokens` verifies the expanded scan and no raw component literals. Full UI gate passes. |

## Validation

| Command | Result |
|---|---|
| `dotnet restore MyPowerTools.slnx` | Passed. |
| `dotnet build MyPowerTools.slnx --no-restore` | Passed with 0 warnings and 0 errors. |
| `dotnet test MyPowerTools.slnx --no-build` | Passed 187, failed 0, skipped 0. |
| `dotnet test src\MyPowerTools.Tests\MyPowerTools.Tests.csproj --no-build --filter "Foundation=P7"` | Passed 10, failed 0, skipped 0. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- validate modules` | 5 production packages valid. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts` | 5 packages and 7 modules passed. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui check modules` | UI gate passed. |
| `dotnet run --no-build --project src\MyPowerTools.Runner -- --once` | Indexed 7 modules; expected degraded states reported truthfully. |
| `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1` | 6 templates passed validation and builds. |

## Boundary

The local P-Foundation-7 runtime correctness target is complete. The later P-UI-Foundation review reopened Shell product UX and has now completed the UI acceptance gate; `.codex/project-state.json` records `productionClosure=false` for the broader release/hardware/signing scope.

