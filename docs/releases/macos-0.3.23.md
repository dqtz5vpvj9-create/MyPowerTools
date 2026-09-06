# macOS 0.3.23: lower idle overhead

macOS background hosts now use workstation GC instead of allocating server-GC heaps per CPU core. Host and service event subscriptions wait for actual events rather than waking every second or 250 ms. Broadcast delivery, cancellation and filtered subscription cursor advancement are preserved.

Remote Notifications reuses an unchanged persisted inbox snapshot and skips repeated list reconciliation; relative timestamps refresh every 15 seconds. File metadata changes and local writes invalidate the cache. Message formats and mobile protocol are unchanged.

On macOS, ADB maintenance stays idle while no forwarding, Wi-Fi or wake-up device is configured. Manual refresh still scans immediately, and configuring a device resumes maintenance. ADB heartbeat writes are reduced to every 15 seconds. No tools or service autostart settings were disabled.

## Local validation

Built and installed the complete Apple Silicon app layout, including updated module and service assemblies. Deep code-signature verification passed. Opened persisted notification history and formatted message details, exercised ADB manual refresh, and returned to the SmartBird page. All eight application processes remained running. Focused checks cover event wake-up/cancellation and inbox cache invalidation, including another writer and local read/filter changes.

## Short idle measurements

CPU is cumulative process CPU time over a 30-second interval; 100% means one CPU core. Before: 8.02% combined across eight processes. Stable after: 5.98%. An earlier after sample captured navigation/initialization work at 43.99%; it is retained as a transient result, not treated as idle savings. These are short observations, not a battery-life benchmark.

Physical footprint from macOS vmmap (MiB as reported):

| Process | Before | After |
| --- | ---: | ---: |
| Runner | 433.5 | 98.3 |
| ServiceManager | 112.7 | 76.9 |
| Shell | 361.9 | 502.2 |
| These three combined | 908.1 | 677.4 |

Runner and ServiceManager footprints fell substantially. Shell grew after exercising notification content and navigating between tools; UI/WebView retention remains an optimization opportunity. The combined figure is not total application memory and samples are not identical cold-start workloads. RSS was not used for memory claims because compression and residency changed between samples.

Remote Notifications connection refusal and unavailable SmartBird/Doubao backends were already present; historical notification reading was validated, but live remote delivery was not. No measured watts or battery runtime improvement is claimed.
