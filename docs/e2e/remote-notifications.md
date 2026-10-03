# Remote Notifications workflow verification

Run from the repository root after building the Debug test project:

```powershell
npx e2e run tests/e2e/remote-notifications.e2e.ts --output .e2e/tools/remote-notifications
```

The tester-army/e2e Windows workflow target invokes the compiled production
poller, view models, message formatters, activation protocol and HostControl API
through xUnit. Each run must produce TRX counters with the expected number of
executed passing cases. Source-text checks and native toast display are excluded.

| Workflow | Verification |
| --- | --- |
| Signed receive | Ed25519 `hello` signature; signed `/pull` query, channel, cursor, limit and user agent; response mapping |
| Response boundaries | Empty queue and blank message become idle; malformed JSON and null envelope return an error; missing key and HTTP 401 report actionable failure |
| Transport recovery | Three-attempt transport retry, success on attempt three, signature redaction after exhaustion, cancellation before dispatch |
| Session metadata | Message ID fallback, default channel/icon, session name/ID, source client/event/message ID and content kind |
| History | Newest-first restore, topic chips, case-insensitive content search, topic/search combination, session position, navigation boundaries, detail ID resolution |
| Refresh | Empty response status; backfill with no cursor; persisted cursor resume; edited settings rebuild the poller and refresh |
| Arrival | Deduplication, saved messages/seen IDs/labels, unread chip acknowledgment, stable collection instance, replay rejection |
| Background receive | Persisted-message event delivers each message once to the recording toast sink |
| Preferences and clear | Persistent banner preference reaches the sink; clearing history updates the store and empty state |
| Message content | Reference quotation retained in stored history; main content displayed first; no duplicate reference appendix; assistant blockquotes retained; banner omits reference request |
| Error recovery | Compact summary and expandable technical detail; retry clears error state |
| Activation | Encoded message ID round trip and delivery through a unique test-owned activation pipe |
| Read state | Idempotent notification read changes; HostControl unread/read/module filtering; unknown ID rejection |

Transport handlers intercept requests to the synthetic `.example.test` endpoint
and use generated temporary signing keys. Stores and toast publishers are in-memory test
doubles. Tests never send a user's notification, update production settings or
clear the user's notification history. Read-state acceptance uses an isolated
temporary runtime. Native activation uses a unique test pipe.

These are executable workflow regressions. They do not drive Avalonia controls
with browser locators. Native window painting, actual Windows notification center
interaction, Android permission dialogs, FCM delivery, phone notification tray
and true network/server integration need separate desktop/device acceptance.
NotifyApp has no device target in this file. No phone production code or version
was changed for these parent-repository tests.

The report and JUnit/Markdown outputs are written under
`.e2e/tools/remote-notifications`; each workflow's raw TRX is under `.e2e/dotnet`.

## Recorded run

On 2026-10-04, e2e 0.16.0 passed all 5 workflow groups (39 compiled xUnit
cases). The explicit-channel isolation adjustment was rebuilt and passed in
the complete suite and in the independent clean-checkout run.
