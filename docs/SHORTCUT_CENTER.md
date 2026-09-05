# Keyboard shortcut center

Open **Settings → Keyboard shortcuts** or run **Keyboard shortcuts** from the command palette. All changes are per user. The editor records one key combination at a time; separate lines are alternative bindings, not a multi-stroke chord.

## Ownership and configuration

`HotkeyStore` in Runner remains the only writer of `state/hotkeys.json`. Legacy module overrides, disabled flags and command arguments are retained. The optional `Shortcuts` list in that same file stores application/tool overrides. A schema-versioned export contains bindings only, not saved command arguments or credentials. Unknown action IDs are retained for reinstallation. Reset removes binding overrides without discarding legacy command arguments.

The existing HostControl settings API exposes `runner.shortcuts`; its settings values contain the command catalog, configuration revision and actual native registration state. Each edit/import is one revision-checked atomic file replacement. A stale editor must refresh instead of silently overwriting another editor. The latest edit can be undone. If an RPC response is lost after persistence, the editor does not claim the write failed: refresh reconciles the actual revision.

## Scopes and precedence

- System bindings are registered once by Runner. They can work with the Shell closed if the action itself is available without a Surface.
- Application bindings are dispatched by the active Shell.
- Tool bindings are dispatched only to the active `IMptShortcutCommandSource`, optionally in one exact named context.
- A native control that handles an event gets first refusal. Text input requires a declaration's explicit opt-in. Separate windows, menus, open permission/command overlays and IME-processed events are not intercepted.
- Tool scope outranks application scope. Within a scope user overrides outrank defaults, followed by stable action ID order. The selected action consumes the key even when it cannot currently execute; there is no fallback to an unrelated same-key operation.
- Repeated asynchronous invocations of one action are suppressed until its current call completes. Buttons and shortcuts use the same command implementation.

Different inactive tools may use the same key. Conflicts are shown only for overlapping scopes and contexts. Native registration errors are reported per binding. A failed replacement keeps the previous working registration and reports its actual key. Swapping two occupied system keys may require temporarily clearing one binding. The operating system does not reliably identify other applications' owners of occupied shortcuts.

## Platform bindings and tool SDK

`ShortcutDefinition` uses `all`, `windows`, `linux` and `macos` binding targets. Editing one platform preserves the others. Cmd is canonicalized to the existing platform service's Win/Meta modifier, and shown as Cmd on macOS. Existing platform capability limits remain visible through native registration results.

A tool declares its actions in `ui/tool.json` under `shortcuts` with an ID, title, context, input opt-in and default binding array. Its active Surface implements `IMptShortcutCommandSource` and returns the existing commands. There are no new per-tool keyboard listeners. `MptShortcutHint.CommandId` on controls reads the inherited effective binding map, keeping hints updated after edits. `MptAvaloniaSurfaceContext.OpenShortcutSettingsAsync` opens the filtered center.

Seven native Surfaces expose 84 actions, including currently unbound actions. Input Monitor period navigation uses Alt+PageUp/PageDown to avoid replacing the application's Alt+Left back action. Task cancellation uses Ctrl/Cmd+Escape, not bare Escape.

WebView2 and WKWebView share the same configuration-driven DOM forwarding script. It respects handled events, composition, AltGraph, editable targets and autorepeat. Current integration forwards application bindings in the main document; it does not claim support for arbitrary commands inside third-party web apps, multi-stroke chords, cross-origin iframe propagation or a desktop-wide key-remapping engine.

## Validation

Run `pwsh -File scripts/test-shortcut-center.ps1` to build the actual SDK packages, Shell, Runner and seven Surfaces and run the focused shortcut and existing personal-UX tests. Run `node --test tests/ShortcutCenter.Tests/web-shortcuts.test.cjs` for the shared DOM forwarding script. The Shortcut center workflow runs Linux, Windows and macOS, additionally compiling the Windows WebView host and macOS native bridge.

Automated headless tests do not certify OS-reserved keys, native input-method composition on hardware, actual desktop-wide registrations, accessibility/permission prompts, or visual interaction in installed packages. Follow AGENTS.md for installed-layout development launches; do not run the application from raw build outputs.
