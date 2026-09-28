# 电脑工具（远程工具控制）— Android integration

Phone-side half of `docs/mobile-ux/REMOTE_CONTROL_CONTRACT.md` (G2). It lets the phone import a
`mpt://control/…` connection code, read the tools and commands a computer really offers through the
Tailnet gateway, invoke them with real parameters, follow progress, cancel, and see exactly the state
and result the computer returned.

| Piece | Location |
| --- | --- |
| Module adapter (`IMptModule`, HTTP client, credential + device store, command contract, tests) | this directory |
| Phone page (`dotnet-surface`) | `src/MyPowerTools.MobileToolControl` (separate deliverable, same tool id) |
| Desktop gateway (G1) | `tools/remote-tool-gateway/` outside `android-integration/` |

The module owns the only HTTP client on the phone that talks to a computer. The page owns no socket,
no token and no file: every effect is one module command.

## 1. Frozen identifiers

| Item | Value |
| --- | --- |
| Module id / package id / tool id | `mobile-tool-control` |
| Display name | 电脑工具 |
| Module entry point | `MobileToolControl.Android.dll` → `MobileToolControl.Android.MobileToolControlModule` |
| Phone page | `surface/MyPowerTools.MobileToolControl.dll` → `MyPowerTools.MobileToolControl.MobileToolControlSurfaceFactory` |
| Activation URI | `mypowertools://device-tool?device=<grantId>[&tool=<toolId>][&command=<commandId>][&run=1]` |
| Data file | `<module data dir>/devices.json` (addresses and names only) |
| Credential | `secret://mobile-tool-control/<sanitized grantId>.token` (platform `secret.store`) |
| Address rule | literal `http://` Tailnet IP only: `100.64.0.0/10`, `fd7a:115c:a1e0::/48`, explicit port |

`MobileToolControlOptions` is the single source of truth; `PackageManifestTests` fails if
`package/module.json`, `package/ui/tool.json`, `package/commands.index.json` or
`manifest/android-package-manifest.json` stop agreeing with it.

## 2. Module command contract

| Command | Arguments | Result payload (JSON) |
| --- | --- | --- |
| `mobile-tool-control.status` | – | `devices[]` (`deviceId, deviceName, endpoint, platform, lastState, lastDetail, lastCheckedAt, credentialConfigured`), `deviceCount`, `activeInvocationId`, `activeDeviceId`, `transport`, `endpointRule`, `loadError`, `lastResult` |
| `mobile-tool-control.import.preview` | `code` | `ok`, `error` (when `ok=false`), `version`, `endpoint`, `endpointDisplay`, `grantId`, `deviceName`, `tokenConfigured`, `alreadyImported`, `replacesExisting`, `addressRule`. Saves nothing and sends no request |
| `mobile-tool-control.import.confirm` | `code`, `accepted=true` | `devices[]`, `imported`. Re-parses the code in the module, writes the token to `secret.store`, upserts `devices.json`. `accepted` is mandatory |
| `mobile-tool-control.devices.list` | – | `devices[]`, `count`, `loadError`, `addressRule` |
| `mobile-tool-control.devices.check` | `deviceId` | `reachable`, `deviceName`, `platform`, `toolCount`, `commandCount`, `detail`; records `lastState`/`lastDetail` |
| `mobile-tool-control.devices.remove` | `deviceId` | `devices[]`, `removed`; deletes the credential and the record |
| `mobile-tool-control.catalog` | `deviceId`, `refresh?` | `device{name,platform}`, `tools[]`, `commands[]`, `toolCount`, `commandCount`, `fetchedAt`, `fromCache` |
| `mobile-tool-control.invoke` | `deviceId`, `commandId`, `args`, `invocationId` | invocation JSON (below) |
| `mobile-tool-control.invocation.status` | `deviceId`, `invocationId` | invocation JSON |
| `mobile-tool-control.invoke.cancel` | `deviceId`, `invocationId` | invocation JSON with `cancelAccepted=true` |

`tools[]`: `toolId, moduleId, title, description, category, state, availability`.
`commands[]`: `commandId, moduleId, title, subtitle, dangerLevel, requiresElevation, supportsProgress,
supportsCancellation, allowed, notAllowedReason, parameters[]`; `parameters[]`: `id, label, type,
required, defaultValue` — passed through from the gateway unchanged.

Invocation JSON: `deviceId, invocationId, commandId, state, message, terminal, cancelAccepted, result`
where `result` is the HostControl `CommandExecutionResponse`-compatible object
(`invocationId, state, summary, logCursor, errorCode, errorMessage, retryable, errorDetails`).

Module events (`SubscribeEventsAsync`): `device.imported`, `device.removed`, `catalog.refreshed`,
`invocation.started`, `invocation.finished`, `invocation.cancelled`. Payloads carry ids, counts and
states only — never a token, endpoint path or argument value.

## 3. Frozen security behaviour

* **Literal Tailnet addresses only.** `MobileToolEndpointParser` rejects host names, LAN/public
  addresses, other schemes, implicit ports, user info, paths, queries and fragments. The stored
  endpoint is re-validated on **every** request, so a hand-edited `devices.json` cannot widen the rule.
* **No redirects.** The `HttpClient` is created with `AllowAutoRedirect = false`; a 3xx becomes
  `MPT_CONTROL_REDIRECT_REFUSED` and is never followed, which is also what stops the bearer token from
  being replayed to another host.
* **Token only in `secret.store`.** `devices.json`, command results, events, status payloads and error
  messages never contain it; `RedactedToken` is used whenever a payload has to say a credential exists.
  `ImportFlowTests` scans the data file and the status payload for the exact secret string.
* **Authorized commands only.** A command whose catalog entry is not `allowed` is refused before any
  request (`MPT_CONTROL_COMMAND_NOT_ALLOWED`); a command missing from the catalog is
  `MPT_CONTROL_UNKNOWN_COMMAND`; a missing `allowed` flag counts as *not* allowed. The gateway re-checks
  server-side, which remains the authority.
* **No duplicate execution.** A repeated `invocationId` never re-submits: a terminal invocation is
  answered from the recorded wire result, a running one is read back with `GET /invocations/{id}`, and
  reusing an id for a different command is `MPT_CONTROL_INVOCATION_CONFLICT`.
* **Cross-grant isolation.** `invocation.status`/`cancel` refuse an id this phone did not start for that
  computer (`MPT_CONTROL_NOT_FOUND`) without sending a request, so nothing is probed.
* **No generic file access.** The module implements only the contract's endpoints (catalog, submit,
  read, cancel). There is no command, argument or endpoint that can read an arbitrary computer file, and
  the phone never asks for one; the page shows what the computer's own commands return.
* **File-pairing tokens are never grant tokens.** The module sends only the token from the imported
  `mpt://control` code and treats every 401/403 as "authorization lost" (re-import required); rejecting a
  file-transfer token is the gateway's check, and the contract requires it.
* **External links never execute.** `mypowertools://device-tool…&run=1` only locates and prefills; the
  `run` parameter is accepted and ignored, and the user must press the page's button. `mpt://control/…`
  opens the import confirmation sheet (or the host scanner feeds the same path) and stores nothing before
  the user confirms.
* **`deviceId` is the control grant id.** The page's device id is the connection code's `grantId`
  (`g-…` from the desktop gateway), not a file-transfer pairing peer id; the two are independent
  credentials.
* **No background work.** `Initialize/Enable/Start/Stop/Disable`, `status` and `devices.list` open no
  socket (asserted with a request-recording HTTP double). There is no timer and no polling loop: the
  catalog cache is a lazy 30 s document cache, and a connection only happens inside an explicit command.

## 4. Wire expectations towards G1

The module speaks exactly `/mpt-control/v1` with `Authorization: Bearer <grant token>`,
`Accept: application/json`, and expects the JSON documents above. Mapping of transport failures:

| Condition | Module error code |
| --- | --- |
| 401/403 | `MPT_CONTROL_UNAUTHORIZED` |
| 404 | `MPT_CONTROL_NOT_FOUND` |
| 400/409/422 | `MPT_CONTROL_REJECTED` (server code preserved in `error.details.serverCode`) |
| 3xx | `MPT_CONTROL_REDIRECT_REFUSED` |
| 5xx | `MPT_CONTROL_UNAVAILABLE` (retryable) |
| timeout / connect failure | `MPT_CONTROL_TIMEOUT` / `MPT_CONTROL_UNREACHABLE` (retryable) |
| non-JSON body | `MPT_CONTROL_PROTOCOL` |

The page shows `state = awaiting-confirmation` as 等待电脑确认 (the legacy `pending-confirmation`
spelling is accepted and means the same), `claimed`/`cancelling` with their own labels, and an unknown
state verbatim. Nothing is derived from HTTP success alone: only `terminal`/`result` decide success or
failure, and a 2xx cancel response is never reported as "已取消" - the page reads the computer's real
`cancelAccepted` answer (accepted / refused / already finished).

## 5. Build and stage

```powershell
# module + tests only (no package)
pwsh tools/remote-tool-gateway/android-integration/build.ps1 -VerifyOnly
# full package (module + phone page + metadata), staged under
# tools/remote-tool-gateway/android-integration/artifacts/package/mobile-tool-control
pwsh tools/remote-tool-gateway/android-integration/build.ps1
# backend-only package (explicitly incomplete)
pwsh tools/remote-tool-gateway/android-integration/build.ps1 -SkipSurface
```

The script always passes `-p:StageRepositoryModule=false` and never writes `<repo>/modules` unless
`-Mirror` is given, so it cannot overwrite the `modules/` tree other tasks own. There is no third-party
runtime dependency: the wire client uses `System.Net.Http` from the shared framework.

## 6. Tests

```bash
dotnet test tools/remote-tool-gateway/android-integration/tests/MobileToolControl.Android.Tests/MobileToolControl.Android.Tests.csproj -c Debug
```

The module suite runs against a **real HTTP gateway double** on `127.0.0.1` (`GatewayDouble`, an
`HttpListener`): request path/method/bearer header, catalog and invocation parsing, redirect refusal
(exactly one request recorded), error-envelope mapping, retryable 5xx, non-JSON protocol error,
unreachable host, duplicate invocation ids, cross-grant refusal, cancel endpoint, import/secret rules,
`devices.json` never containing the token, hand-edited non-Tailnet records, and the no-background-work
guarantees.

The loopback double is reached through an internal, test-only `IMobileToolEndpointPolicy`. Production
always constructs `TailnetEndpointPolicy`, and no settings file, preference or command argument can
select the test policy.

The **real combination** is verified by a separate suite
(`tests/MobileToolControl.Integration.Tests`): the real desktop gateway (`RemoteToolGatewayService`) on a
real loopback listener, the real module and HTTP client, and the real page view model - only
`IHostControlBridge` is a test command. It covers catalog → typed form → success / real host failure /
awaiting-confirmation → cancel accepted / refused / already finished, and the deep-link rules.

**Not verified here:** no real Tailnet gateway was contacted (the loopback doubles are test-only seams)
and no Android device/emulator ran the package.

## 7. Android bundle integration checklist (root / M7 owner)

1. `MyPowerTools.Android.slnx`: add
   `tools/remote-tool-gateway/android-integration/src/MobileToolControl.Android/MobileToolControl.Android.csproj`
   and `src/MyPowerTools.MobileToolControl/MyPowerTools.MobileToolControl.csproj`.
2. `src/MyPowerTools.Android/MyPowerTools.Android.csproj`: add the two `ProjectReference`s and an
   `AndroidAsset` glob for the staged package. Keep the `ui/surface/*.dll` file: no `MyPowerTools.*`
   wildcard exclusion, exactly like `remote-commands-android`:
   ```xml
   <AndroidAsset Include="../../tools/remote-tool-gateway/android-integration/artifacts/package/**/*"
                 Exclude="../../tools/remote-tool-gateway/android-integration/artifacts/package/**/*.pdb;../../tools/remote-tool-gateway/android-integration/artifacts/package/MyPowerTools.Abstractions.dll;../../tools/remote-tool-gateway/android-integration/artifacts/package/MyPowerTools.Platform.Abstractions.dll;../../tools/remote-tool-gateway/android-integration/artifacts/package/Avalonia*.dll;../../tools/remote-tool-gateway/android-integration/artifacts/package/module.json;../../tools/remote-tool-gateway/android-integration/artifacts/package/commands.index.json;../../tools/remote-tool-gateway/android-integration/artifacts/package/ui/*.json">
     <Link>modules/mobile-tool-control/%(RecursiveDir)%(Filename)%(Extension)</Link>
   </AndroidAsset>
   ```
   Expected staged content: `MobileToolControl.Android.dll`, `ui/surface/MyPowerTools.MobileToolControl.dll`,
   the metadata documents.
3. `scripts/build-android.ps1`: run
   `pwsh tools/remote-tool-gateway/android-integration/build.ps1 -MyPowerToolsRepoRoot <repo>` next to the
   existing File Transfer / Remote Notifications / Remote Commands staging steps.
4. No `scripts/artifacts-policy.json` entry is needed: the package stays inside the tool directory and
   the test temp directory is covered by `artifacts/.tmp-*` (scratch).
