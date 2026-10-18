# Runtime history retention

`CommandHistory` and `NotificationCenter` are separate, structured, process-local
rolling histories. They are not command execution state, notification action queues,
or permanent archives. The limits apply as writes occur; there is no timer, disk
spill, background polling, or forced collection in production.

## Reference and defaults

The design follows Android's [logcat overview](https://developer.android.com/tools/logcat):
separate circular buffers and structured origin/severity information. The inspected
[AOSP Android 16 `SerializedLogBuffer` source](https://android.googlesource.com/platform/system/logging.git/%2B/refs/tags/android-16.0.0_r2/logd/SerializedLogBuffer.cpp)
caps individual messages, compares used bytes against a per-buffer limit, and removes
old chunks until the excess is freed. This is a pinned Android 16 source reference,
not a claim that the latest AOSP main branch was verified. MyPowerTools removes
individual records instead of Android's compressed chunks.

| Buffer | Maximum charged bytes | Maximum records | Maximum charged record |
| --- | ---: | ---: | ---: |
| Commands | 4 MiB | 4,096 | 16 KiB |
| Notifications | 2 MiB | 2,048 | 16 KiB |

These are MyPowerTools defaults, not Android defaults. Both byte and entry ceilings
apply simultaneously. Small records hit the entry ceiling; large results hit the
byte ceiling sooner. The command budget is larger to retain more command output.
Tests can inject `HistoryBufferOptions`; the default runtime uses the table above.
Invalid options fail construction.

## What the byte budget means

Each retained record is charged 512 bytes of fixed allowance plus twice the length
of every retained string (UTF-16 storage). Identity strings, state/level, titles and
summaries/bodies all count. The fixed allowance covers record, string-header,
linked-list, dictionary and bounded internal-key bookkeeping. It is an accounting
budget rather than an exact CLR object-size or process-RSS measurement. Dictionary
capacity can retain its bounded high-water allocation after rollover. The entry cap
also bounds this overhead; there is no unbounded module or invocation index.

`OriginalBytes` is the charge after redaction and before truncation. Redaction
applies to command state/summary and notification title/body; identifiers and
notification level are not rewritten. `RetainedBytes`
is the charge actually admitted to the buffer. Redaction happens before shortening
so a cut through a token cannot expose a partial secret. Free-text state/level is
capped at 128 bytes, notification title at 2 KiB, and remaining record budget is
available to the summary/body. The suffix `… [truncated]` is included when it fits.
Truncation preserves whole Unicode text elements, including surrogate pairs,
combining sequences and joined emoji. The `IsTruncated` flag remains authoritative
when even the suffix cannot fit.

Large caller-owned inputs and temporary redaction allocations are not retained by
these buffers but can still cause transient allocation peaks. Snapshots/records a
caller deliberately holds are also outside the buffer's lifetime control. Do not
interpret a 6 MiB combined charged budget as a 6 MiB entire-process RAM cap.

Identity fields used for matching are never shortened. If invocation ID, command
ID or module ID alone cannot fit a record, that history admission is rejected and
`RejectedCount` increments. The returned record has `IsRetained=false`,
`RetainedBytes=0`, preserved identity, and empty free-text content. It is not rooted
by the store. `IsRetained` describes admission, not a live membership check on an
old snapshot. This does not reject or cancel the command itself. A rejected
notification cannot subsequently be marked read through the history API.

## Commands

Oldest records are evicted when either budget is exceeded, including when completion
replaces a small accepted-state record with a large result. `Summary` contains the
redacted `CommandExecutionResult.Output`, or the redacted error message for blank
output, and may be truncated. An old record's completion may evict that record.

The runtime's execution tasks, cancellation sources and cancellation targets remain
separate from this history. Evicting an accepted/running record never cancels the
command. Completion of a record no longer present does not recreate it or add a
second active-record cache; `UnretainedCompletionCount` increments. This counter
also includes unknown/rejected invocation completions, not only known evictions.

Duplicate invocation IDs retain the prior behavior: each `Add` creates a distinct
history record, and `Complete` updates the first retained matching invocation.
A bounded internal sequence key avoids deduplicating them.

## Notifications and unread state

The actual host notification record contains ID, time, module, level, title, body
and read state. There is no action payload. Producers are notification commands and
selected module events such as failed/disconnected/alert/degraded events. This is
separate from the Remote Notifications tool's persisted message store.

Eviction selects the oldest already-read record first. If insufficient, it selects
the oldest unread record. Unread eviction does **not** mark the record read or emit
a false read acknowledgement. `UnreadCount` reports only retained unread records;
`EvictedUnreadCount` is a cumulative, separate loss indicator with
`LastEvictedUnreadAt`. The latter is the original timestamp of the most recently
evicted unread record. Overflow must be visible in the existing notifications view
and API response, not silently represented as reading those notifications.

Read toggles remain idempotent and reversible for retained records. Updating an
evicted/rejected ID returns null, which HostControl reports as NotFound. No
unbounded tombstone set is retained. This is bounded recent history, so it cannot
promise lossless delivery or an indefinite unread inbox. If actionable notifications
are introduced later, their pending action/acknowledgement state needs a separate
explicitly bounded/controlled lifecycle rather than this eviction policy.

## APIs and counters

The parameterless constructors, `CommandHistory.List(string? moduleId = null)` and
`NotificationCenter.List()` remain actual CLR methods for existing callers. New
`Query` methods add module, time, state/level, read-state and limit filters. In the direct in-process List/Query APIs, module
matching remains exact and case-sensitive; HostControl preserves its pre-existing
case-insensitive module filter. State and level matching is exact and
case-insensitive. Time ranges are `[since, until)`. Results are newest timestamp
first and independent snapshots. Querying never alters global retention statistics.

`Count` reads under the gate without a sorted snapshot or allocation. `GetStats()`
returns the unfiltered per-buffer snapshot:

- Current count, charged bytes and all three configured limits
- `AddedCount`: admitted additions, including repeated invocation IDs
- `EvictedCount` and original `LastEvictedAt` timestamp
- `TruncatedCount` and `TruncatedBytes`: number of truncated writes and charged
  bytes removed; repeated completion writes count separately, read toggles do not
- `RejectedCount`: identity-too-large admissions
- Current `UnreadCount`, cumulative `EvictedUnreadCount` and original
  `LastEvictedUnreadAt` timestamp
- `UnretainedCompletionCount` (commands only)

The records and stats are in-memory per-process values and reset with the runtime.
HostControl exposes additive fields; clients should not equate the retained count
with lifetime publications. Count, Query and stats are individually synchronized;
separate calls do not constitute one atomic multi-call snapshot.

## Verification and scope

`src/MyPowerTools.Tests/HistoryCount.Tests.cs` tests the production Runtime and
Abstractions assemblies. It covers byte/entry caps, growth on completion, duplicate IDs,
read-first/all-unread eviction, read-state reversal, huge Unicode, redaction,
identity rejection/collection, snapshots, filters, concurrency, API signatures,
allocation-free `Count`, and fixed-budget plateau. Runtime acceptance tests also
exercise cancellation of a genuinely running fake-module command after its history
record has rolled out, and HostControl overflow metadata.

The process-memory probe compares the original and bounded implementations using
the same 5,000-command plus 5,000-notification workload in separate processes. A
separate bounded-only 50,000-pair run establishes sustained retention. It measures
real managed live bytes, process working/private bytes and external `/proc` RSS.
Explicit collections occur only in the probe to compare retained live objects.
These synthetic tests establish the retention behavior, not the cause or exact
savings of any user's observed application-wide memory symptom. No devices,
native services, user accounts, external endpoints or models are involved.

Log-tail correctness is owned by `RuntimeAcceptanceTests.PlatformBrokerPackage.Tests.cs`
in the same existing test project. It covers physical CR/LF selection, seeded byte
and Unicode input, UTF-8/16/32 BOMs, short reads, block boundaries, a 2 MiB line,
append/truncation/rotation, and public JSON filtering and exception outcomes.
Nonpositive limits retain the zero-byte-read invariant. UTF-16/32 still use a
sequential scan; long lines and append-before-EOF do not imply a fixed I/O bound
or an atomic live-file snapshot. The reference scan owns its file handle even
when a nonpositive limit never enumerates lines.

For an already-built test project, run only these owners with:

```sh
dotnet test src/MyPowerTools.Tests/MyPowerTools.Tests.csproj --no-build \
  --filter 'FullyQualifiedName~HistoryCountTests|FullyQualifiedName~RuntimeAcceptanceTests.Log_tail_|FullyQualifiedName~Notification_center_updates_read_state_idempotently'
```

The retired standalone harnesses and measurement tools are recoverable exactly
from commit `f2aadb3ff3e414af6c4cebfa0cd47d275a69e3bb`:
[history harness](https://github.com/dqtz5vpvj9-create/MyPowerTools/tree/f2aadb3ff3e414af6c4cebfa0cd47d275a69e3bb/tests/History.Memory.Regression)
and [log-tail harness](https://github.com/dqtz5vpvj9-create/MyPowerTools/tree/f2aadb3ff3e414af6c4cebfa0cd47d275a69e3bb/tests/LogTail.Memory.Regression).
The retained audit evidence includes `phase2/history-ring` before/after/sustained
JSONL and process samples, `phase2/logtail/io-results.json`, and both original
`source-sha256.json` manifests. The retirement evidence additionally records a
SHA-256 manifest of every harness file at that recovery commit. These historical
measurements are not rerun by the canonical tests; the bounded plateau and
zero-read checks remain regular regression tests.
