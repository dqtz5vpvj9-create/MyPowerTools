# Desktop UI recovery contract

Applies to every failure state in the desktop Shell (`src/MyPowerTools.Shell.Avalonia`).
"Failure state" means any UI that shows that something didn't load, didn't open or stopped working.

## Rules

1. **One recovery surface.** Page and tool failures go through
   `ShellWorkspaceController.BuildRecoveryPage` (renders `UnavailablePageView`). Hosted tool
   surfaces go through `RecoverToolSurfaceAsync`. Last-resort fallbacks (startup failure,
   failure while rendering the recovery page) use `MptErrorState` with an action. No raw exception
   text in the UI, and no failure state without an action.
2. **Each click escalates.** The primary button runs the next rung of the recovery ladder for
   that failure kind, and each rung is stronger than the one before. Pressing it never just repeats
   the same reload.
3. **Visible work.** While a rung runs, the button is disabled and the page shows what is
   happening ("Starting the background service…", "Reloading…").
4. **No identical failure twice.** If the page still fails, it shows what was tried and when
   ("Tried at 09:13:05: restarted the background service. Still not working.") and the button
   offers the next rung.
5. **Terminal rung always works.** The last rung on desktop is *Restart MyPowerTools*: the Shell
   relaunches itself (`--relaunch-after <pid>`) and exits. Tools hosted by the background service
   keep running. A secondary *Go to Home* button is available on every recovery page.
6. **Memory.** Attempts are tracked per surface (`page:<page>` or `tool:<id>:<route>`) by
   `ShellRecoveryTracker`. A successful load clears the entry, and entries expire after
   10 minutes. Background refreshes that fail again without a user action don't advance the ladder.
7. **No unreachable states.** A view must not contain a failure state that nothing produces.
   Recovery buttons bind their label to the recovery plan. Tests reject literal `"Try again"`
   buttons in Shell views.

## Ladder

| Failure kind (from `ShellFailurePresenter` code) | Rung 1 | Rung 2 | Rung 3 |
|---|---|---|---|
| Service unavailable / timeout | Start the service if it isn't running, wait until it answers, reload | Restart the service (stop our own process, start again, wait), reload | Restart MyPowerTools |
| Credential mismatch / access denied | Restart the service, reload | Restart MyPowerTools | - |
| Permission denied | Reload | Open *Components* to review permissions | Restart MyPowerTools |
| Missing files / invalid data | Reload | Open *Updates* to repair | Restart MyPowerTools |
| Unexpected / UI fault | Reload with a fresh page | Restart MyPowerTools | - |
| Tool surface unreachable | Reload the surface | Restart the background service, reload | Restart MyPowerTools |

"Service" is the Runner for most pages and the Service Manager for *Background services*.
Restarting a service only stops processes that run from this installation's directory.
Mobile builds have no *Restart MyPowerTools* rung, so the ladder ends at its last available rung.

## Surfaces without a manual button

- Command search failures show "Search again", which re-runs the query.
- *Background services* reconnects automatically when the stream returns.
- *Logs* keeps auto-refreshing and shows the last refresh error inline.
