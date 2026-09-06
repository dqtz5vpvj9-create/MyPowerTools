# macOS 0.3.25 — idle services and resilient notification transport

Development/acceptance candidate. CPU, functional and energy acceptance are reported separately.

## Changes

- Distinguish disabled tools from idle services. ADB Forwarder, Doubao Computer Use and SmartBird are disabled in this user's persisted configuration and excluded from the fresh macOS enablement defaults. Optional service units no longer autostart merely because their packages are installed; explicit user choices persist.
- ScreenEase may remain resident. Its macOS heartbeat loop now blocks indefinitely on cancellation while the independent IPC listener waits for work. Read-only catalog/status/settings discovery does not start an unused service. Its empty Runner event stream no longer reconnects every second. No automatic idle-stop policy is used.
- macOS busy indicators are static and decorative transitions apply immediately. Hiding/minimizing detaches the view tree; restoring retains view state, including ScreenEase timers and Paste Image drafts. Active background notification reception remains independent of the window.
- A healthy Runner connection waits on its existing event stream instead of a second five-second health poll. Stream errors and unexpected closure wake connection checks; offline retry/restart remains available. Runner hotkey synchronization waits for host events instead of waking every second.
- Notification connections pool for reuse; the deployed Gunicorn gevent worker uses a 120-second idle keepalive. Fixed signing results, settings and unchanged inbox snapshots are cached, with file replacement invalidation. Inbox and remote-command observers wake on file changes instead of frequent history scans.
- The notification worker caches the persisted cursor with the inbox snapshot and omits periodic macOS heartbeat logging. The live control socket continues to handle readiness and status requests.
- Network-address changes discard sockets from the old route and wake notification polling immediately. Normal online polling remains five seconds; offline retries remain bounded and manual polling is immediate. No certificate-validation bypass was needed.

## Functional checks

- Same real TLS socket accepted HEAD requests after idle gaps of 6, 15 and 65 seconds. HTTP 404 at the root was expected and tested transport continuity.
- Fourteen focused notification checks passed, including signing-key replacement, preservation of the signed-pull contract, persisted history behavior, recovery from a peer-closed persistent socket and explicit connection-pool reset.
- Four focused Shell checks passed: healthy connections stop probing, a fault wakes the monitor, recovery proceeds without another signal, and unexpected event-stream closure reconnects while sequence tracking remains correct.
- Actual server POST to the user's notification channel reached the installed app, persisted in history and appeared in the notification list as the one-time “MPT macOS 验收” message. Manual poll returned idle with no error. Historical full Markdown details displayed normally.
- ScreenEase loaded on demand with eye care/reminders off. Hidden/restored view bindings and Paste Image reopening were exercised. ScreenEase remains resident for the final idle measurement.
- Static busy/determinate bindings survived hide, binding update while detached, and reattachment. Atomic catalog replacement and changed configured catalog paths woke the file observer.
- A transient macOS DNS routing failure was observed: terminal resolution succeeded while the notification process was assigned no DNS service. The running service subsequently recovered without application restart. Physical Wi-Fi switching and sleep/wake were not deliberately forced; the recovery tests simulate connection loss/pool replacement.

## Measurement

Final CPU gate **passed: 0.933% combined single-core CPU**. Apple M1, 8 GiB RAM, macOS 15.7.7, arm64, battery power (37% immediately afterward; system power log also reports battery before the run). Measured 2026-09-06 22:29 +0800, with 60 seconds of warm-up and 120.062 seconds of observation after exercising the UI. The window was closed to the menu bar, all six host PIDs stayed resident, and the three user-disabled tools remained disabled. The notification worker was online before and after the run, with normal five-second polling and successful manual refresh afterward.

| Resident host | Mean CPU, one core | Physical footprint after CPU sampling |
| --- | ---: | ---: |
| MyPowerTools.Shell.Avalonia | 0.025% | 279.9M |
| MyPowerTools.Runner | 0.183% | 110.7M |
| RemoteNotifications.Service | 0.575% | 116.6M |
| MyPowerTools.ServiceManager | 0.067% | 71.7M |
| MPTAndroidTools.Runtime | 0.083% | 59.7M |
| ScreenEase.Service | 0.000% | 40.2M |

ScreenEase recorded no additional CPU time at the sampling resolution; this is not a claim of zero memory use or zero energy. Five-second combined intervals ranged from 0.198% to 5.730%, so this is an average CPU gate, not a promise of continuously staying below 1%. The first 0.3.25 attempt with the same six hosts failed at 2.724%; the final run followed removal of redundant connection probes, hotkey polling, heartbeat writes and unchanged-history cursor scans.

Source review alongside the separate pre-final managed traces located redundant work around `HostControlConnectionMonitor.RunAsync`, `WatchRuntimeHotkeyBindingsAsync`, the notification `RunOnePollCycle`/`ResolveWaterline` path, and proxy/file checks. Their dominant waiting stacks (`WaitForSignal`, `WaitOne`, `NetworkChange.RunLoopThreadStart`) include blocked thread time and are **not CPU percentages**. Those traces did not show the prior animation pulse or TLS certificate-chain stacks among their top 18 functions; this does not prove those functions never run.

The physical-footprint snapshots above were collected separately after CPU sampling. They are not a retained-memory stability test or a uniquely attributable system total. Repeated-navigation memory stability and hardware wake-up/energy acceptance remain incomplete.

The previous 0.3.24 result was 3.790% combined single-core CPU with eight hosts. This version's enabled-tool configuration differs, so comparisons include the user-requested feature disablement.

Power in watts, battery endurance and hardware wake-ups require separate instrumentation; CPU alone cannot establish those. Full Instruments is not installed. This package must not be called fully energy-qualified without the remaining evidence.
