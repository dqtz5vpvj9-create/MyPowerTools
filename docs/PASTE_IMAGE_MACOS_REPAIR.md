# Paste Image macOS repair — 2026-09-09

Status: development acceptance candidate based on 0.3.25, not a new stable release or a power-qualified release.

The Carbon hotkey worker previously waited in CFRunLoopRun without dequeuing and dispatching Carbon events. It now blocks in ReceiveNextEvent, dispatches received events, and uses posted Carbon events to wake registration, unregistration and shutdown. There is no periodic polling timer.

Paste Image no longer disposes its view model on temporary visual detachment. DotnetSurfaceLoader retains responsibility for final disposal. This preserves commands, subscriptions and history when the resident window or layout detaches and restores the page.

Shell startup now prefers the nested MyPowerTools Runner.app executable on macOS. Launching through the compatibility path caused the running host to lack its bundle identity.

Validation on the user's Apple Silicon Mac:

- Eight targeted tests passed, including idle registration, conflict detection, unregistration and disposal without keyboard input.
- A physical Control+Option+J press fired the repaired provider's callback and the test process exited successfully.
- Complete application built, signed and verified with codesign --verify --deep --strict, installed at /Applications/MyPowerTools.app and launched.
- Running Runner path verified inside Helpers/MyPowerTools Runner.app.
- Paste Image history and manual refresh worked after closing/restoring the window.
- SSH connectivity to the configured destination succeeded.

Pending: final installed Control+Option+V upload confirmation, automatic paste after Accessibility authorization, notification delivery, and the complete MACOS_POWER_ACCEPTANCE.md scenarios (120-second idle measurement, wakeups, memory stability and notification recovery). No battery or idle CPU qualification is claimed. The existing configured after-upload shortcut is Ctrl+Shift+V; users should choose the shortcut appropriate for their destination app.
