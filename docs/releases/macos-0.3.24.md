# macOS 0.3.24: resident work and power acceptance

Status: development/acceptance candidate. This is not a power-qualified release until all checks in `docs/MACOS_POWER_ACCEPTANCE.md` pass.

## Changes

- Loading indicators follow actual loading state. On macOS, hidden, minimized or detached progress indicators temporarily leave indeterminate mode; original bindings and their changes are preserved and restored when shown.
- The notification worker retains its history cache across poll cycles instead of constructing a fresh store each time. Deduplication work runs only when a successful pull contains messages.
- The notification observer skips scanning and hashing unchanged history snapshots.
- Normal online polling cadence is unchanged. Consecutive offline failures on macOS back off exponentially to a one-minute interval (or the configured interval if longer). Manual polling remains available immediately. A successful pull resets the backoff.
- Added the default macOS low-power acceptance policy and an executable process-group CPU gate. It includes application descendants and rejects missing standard hosts or process churn. It cannot certify watts, wake-ups, live delivery or memory stability on its own.

## Verification

Built and installed the complete signed Apple Silicon layout. Verified both app bundles and the disk image signature. Opened the notification list and complete message detail. A targeted headless exercise of the real progress-animation suspension code passed visible/hidden parent, binding changes while hidden, restored window, detachment and reattachment. Existing inbox-cache checks passed. No service was disabled.

## Profiling evidence and limits

The device has Command Line Tools but no Instruments/xctrace. Native `sample` and .NET sampled-thread-time traces were captured; waiting-thread percentages are not CPU percentages.

Observed UI call chain: `MediaContext.RenderCore` -> `ClockBase.Pulse`, with dispatcher timer promotion/rescheduling. Hidden progress animation lifetimes were corrected and explicitly exercised.

Observed notification worker activity includes `SslStreamPal.PerformHandshake`, `AppleCryptoNative_X509ChainEvaluate` and `X509ChainGetChainSize`. These are TLS connection/certificate work. No certificate validation was disabled. Repeated failure retries now back off; this evidence does not establish an exact CPU share for each function.

Observed Runner activity includes `Mutex.CreateMutexCore`, mutex creation and handle release during periodic observation. The observer no longer rehashes unchanged history. `RunHotkeyLoop`, semaphore waits and file-watcher wait loops dominate sampled thread time because they block; they are not presented as CPU hotspots.

Earlier short runs were affected by startup and concurrent user navigation/command execution and are not comparable idle benchmarks. In a user-confirmed 45-second idle interval before the final retry/observer changes, the group used 5.90% of one core, with Shell at 0.80%; that intermediate result failed the intended budget. Final acceptance results are recorded below after the default observation completes.

Live remote notification delivery, wake-up/energy measurements and long-duration memory stability still require explicit evidence. Persisted notification reading is not a substitute for live delivery verification.

## Final CPU gate

After a 60-second warm-up, the installed app was observed for 120.0 seconds with its windows closed. All eight standard hosts remained present and stable. Combined CPU was **3.79% of one core**, exceeding the 1% budget: **FAILED**. This build remains a development/acceptance candidate.

| Process | CPU % of one core |
| --- | ---: |
| MPTAndroidTools.Runtime | 0.158 |
| ScreenEase.Service | 0.158 |
| MyPowerTools.ServiceManager | 0.292 |
| MyPowerTools.Shell.Avalonia | 1.133 |
| MyPowerTools.Runner | 0.583 |
| AdbForwarder.Service | 0.125 |
| DoubaoAgent.Controller.Service | 0.208 |
| RemoteNotifications.Service | 1.133 |

This is CPU evidence only, not a battery-life or energy result. The low-power target has not been met.
