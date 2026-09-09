# macOS retained-window repair — 2026-09-09

Status: development acceptance candidate. This changes window ownership, not the application's energy qualification.

## Behavior and design

Closing to the status item and minimizing now retain the existing page tree, view models, drafts, bindings and commands. They no longer set `MainWindow.Content` to null. Final shutdown still disposes the workspace through the existing owner. macOS static progress indicators, disabled decorative transitions and event-driven background work are unchanged.

This follows Apple's [visibility guidance](https://developer.apple.com/library/archive/documentation/Performance/Conceptual/power_efficiency_guidelines_osx/WorkWhenVisible.html) and [efficient drawing guidance](https://developer.apple.com/library/archive/documentation/Performance/Conceptual/power_efficiency_guidelines_osx/UsingEfficientGraphics.html): suspend work that has no value while invisible, rather than disposing the entire page. No new visibility polling or native occlusion bridge was added. An occlusion bridge should be introduced only for identified work requiring covered-window notifications, without replacing Avalonia's delegate or stopping useful background services.

## Functional validation

- Three resident lifecycle tests passed on macOS. The new behavioral test creates the real MainWindow on the shared isolated headless UI thread and runs three hide/minimize/restore cycles. It verifies retained page identity, no temporary detachment, draft and binding continuity, and keyboard activation of a button command after each restoration. Permanent close still detaches the page.
- The same test was run against the old implementation and failed at the retained-page assertion: expected the existing StackPanel, actual null. The fixed implementation passed.
- Published the Shell in Release/osx-arm64, overlaid only its changed DLL into the complete installed app, re-signed the helper and outer bundle, and passed `codesign --verify --deep --strict`.
- The installed DLL's SHA-256 matches the published component. Started the updated Shell from /Applications/MyPowerTools.app. Existing Runner, ServiceManager, notification worker and Android runtime remained running.
- On the actual installed app, Paste Image history remained available, and manual refresh emitted fresh inspect/history requests after close/reopen and minimize/restore.

## Measurement conditions

Apple M1 MacBookAir10,1, macOS 15.7.7, arm64, battery power. Both observations use scripts/measure-macos-idle.py with 60 seconds warm-up, 120 seconds observation and the same 1% combined single-core CPU budget. Paste Image was the selected page and the window was closed to the status item. No build or profiling ran during either observation. No tool configuration or background service was changed; only Shell restarted for the update.

These are one sequential before/after observation, not repeated randomized experiments. Background event timing can differ. CPU time is not watts or battery endurance. RSS across a restarted Shell is not evidence of retained-memory savings. Hardware wakeups, long-term memory stability and complete live-notification acceptance remain separate and incomplete.

| Resident process | Detach page, CPU % | Retain page, CPU % |
| --- | ---: | ---: |
| MyPowerTools.ServiceManager | 0.075 | 0.075 |
| MyPowerTools.Shell.Avalonia | 0.266 | 0.150 |
| MyPowerTools.Runner | 0.275 | 0.108 |
| RemoteNotifications.Service | 0.425 | 0.108 |
| MPTAndroidTools.Runtime | 0.125 | 0.083 |
| Combined | 1.166 | 0.524 |

The baseline at 21:03:56 +0800 failed the 1% gate; the retained-page observation at 21:10:31 +0800 passed. Each group retained the same five host roles and stable PIDs throughout its observation. Shell alone restarted between observations. The result supports retaining this static page without an observed CPU penalty in this trial; it does not establish that retaining pages saves power. Much of the combined reduction came from unchanged background hosts, so it cannot be attributed to the Shell edit. No function-level attribution of the baseline excess is claimed.

Coverage limitation: this comparison uses Paste Image, not every dynamic workspace. ScreenEase has a display timer with explicit SuspendView/ResumeView methods, and the notification surface has Activate/Deactivate observation methods currently attached to tree membership. Their hidden-window presentation work requires a separate visibility-lifecycle audit; retaining a page must not be presented as proving all dynamic work is suspended. No backend service or phone protocol was changed in this repair.
