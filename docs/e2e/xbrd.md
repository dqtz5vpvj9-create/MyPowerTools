# XBRD workflow verification

Run preparation and this suite from the MPT repository:

```powershell
dotnet build tests/e2e/xbrd-workflows/Xbrd.Workflows.Tests.csproj --nologo
npx e2e run tests/e2e/xbrd.e2e.ts --output .e2e/tools/xbrd
```

The tester-army/e2e runner executes five workflow cases backed by sixteen xUnit regressions against the actual XBRD publisher and unit-control production clients. The helper checks TRX counters, requires every selected regression to execute and pass, and rejects empty selections and skips.

| User flow | Verified outcomes |
| --- | --- |
| Refresh health and sources | Correct endpoint, healthy status, source sorting, TTL expiry and severity |
| Read panel preview | Actual panel parser accepts the fixture document |
| Refresh source | Encoded source ID, POST request, duration/exit code/snapshot/stdout evidence |
| Disable, enable, delete | Correct POST/POST/DELETE methods and endpoint suffixes |
| Older publisher | 404/405 control endpoint produces unsupported result |
| Read source logs | Preserve string/object lines, omit empty lines, clamp limit to 1–1000 |
| Failed control/log response | HTTP 400/401/500 cannot become success through `ok:true` |
| Invalid/rejected response | Invalid JSON, arrays and `ok:false` produce failure |
| Connection recovery | Failed health read followed by a successful retry |
| Publish owned memory/Codex sources | Revision, duration, skipped flag, POST publish-now |
| Publish external source | Reject unowned source without an HTTP request |
| Failed local unit response | HTTP 401/500 cannot become success through `ok:true` |

All requests use an in-memory HTTP handler. The suite invokes production clients and parsers; it makes no network calls and does not alter a real router, quota account, plan, device or running service.

Confirmed fixes: publisher controls, publisher logs and local publish-now previously accepted failed HTTP responses when the JSON body said `ok:true`. Regression tests failed before the fix and passed after requiring successful HTTP status.

Coverage limits: native Avalonia page interaction/layout, settings persistence, service-manager start/stop, production collector scripts, real quota authentication, remote router panel rendering and hardware delivery require additional acceptance environments. The suite validates client workflows and error handling. It does not certify these remaining UI/device flows.

Evidence is written by e2e under `.e2e/tools/xbrd/report.json`, `junit.xml` and `summary.md`; detailed .NET results are under `.e2e/dotnet/`.
