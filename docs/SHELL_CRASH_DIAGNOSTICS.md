# Shell crash diagnostics

## Evidence and limits

A successful replacement-view smoke test is not proof of the original reported crash's root cause. Confirmation still requires the original stack or a before/after reproduction using the same message, installed bundle, OS and real double-click/plugin-loading path. The existing native smoke exercises the real WKWebView provider directly; it is distinct from the production collectible-plugin loader.

## Default files

On macOS, diagnostics start at the managed Shell entry point without requiring a debugger or terminal:

```
~/Library/Logs/MyPowerTools/
  shell-<UTC>-<PID>-<run-id>.jsonl
  shell-<UTC>-<PID>-<run-id>.stderr.log
  shell-<UTC>-<PID>-<run-id>.pending.json
  shell-<UTC>-<PID>-<run-id>.jsonl.<id>.fatal.json
```

`MPT_DIAGNOSTICS_DIRECTORY` overrides the directory. If it cannot be opened, the logger tries an OS-temporary directory and reports the actual location to stderr. `MPT_SHELL_DIAGNOSTIC_LOG` exposes the current run's managed log path to plugins. Diagnostic-storage failure must not prevent startup.

The JSON-lines log contains UTC time, PID, managed thread ID, event name, detail and full `Exception.ToString()` with inner exceptions. Command-boundary, UI-dispatcher, top-level and runtime exceptions are recorded. Selected Shell/WebView/plugin assemblies include informational version, MVID, location and load-context identity. Existing Trace messages and Avalonia warnings feed the same file. Managed log entries are flushed synchronously; fatal events also get an independent, fsynced file before attempting the normal log lock. The managed log keeps one rollover file after exceeding 8 MiB. Logs from older sessions are retained for manual inspection.

The macOS/Linux stderr capture redirects native file descriptor 2, so native `write(2, ...)`, runtime fatal output and inheriting child-process stderr are retained too. It is not merely `Console.SetError`. CLI `--smoke` retains its original stderr behavior. Capture begins in managed Main, not before .NET startup.

## What the user sees

A contained command error opens a diagnostic notice with the log path. An unclean primary-Shell session leaves a marker. On the next startup, a notice offers **Open diagnostic logs** and, on macOS, **Open macOS crash reports**. Dismissing that notice marks the previous session as reported; its logs remain. A pending marker can also result from force quit, SIGKILL, power loss or restart; the UI does not assert that every unclean exit was a software crash. Live process markers are excluded using PID and process-start time.

## Native failures

Unknown UI exceptions are observed without setting `Handled=true`. AppDomain callbacks cannot intercept every native access violation, abort, stack overflow or fail-fast. Recovery is performed only at known operation boundaries. SIGKILL and power loss permit no final in-process logging; the last flushed breadcrumbs and pending marker remain useful.

For native stack traces, macOS Console > Crash Reports and `~/Library/Logs/DiagnosticReports/` provide the relevant `.ips` report. Stderr alone is not guaranteed to contain a native stack. OS crash-report generation and disk availability remain external dependencies. No managed signal handler attempts to continue execution after memory corruption.

## Privacy

Normal detail breadcrumbs use a per-window correlation ID, message-ID hash and message length, never the notification body. Managed diagnostics apply the existing credential redactor. Native stderr and OS crash reports are raw diagnostic evidence and may contain sensitive data. Files are local, never uploaded automatically; newly created Unix log files use mode 0600 and log directories use 0700. Full memory dumps are not enabled by default.

## Verification

`tests/Shell.Diagnostics.Probe` compiles the same production diagnostic sources. `verify.py` runs separate disposable child processes and checks normal exit, contained errors, top-level exceptions, background-thread exceptions, real Avalonia dispatcher exceptions, actual libc abort, SIGKILL, concurrent writes and an unwritable directory. It also opens the next-launch notice with Avalonia Headless and checks marker acknowledgement. Native WebView correctness is covered separately by the existing real-WKWebView smoke workflow. These are fault-injection tests, not a reproduction of the user's original crash.
