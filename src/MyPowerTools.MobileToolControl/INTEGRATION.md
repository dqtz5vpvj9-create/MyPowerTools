# 电脑工具 phone page — integration contract

`MyPowerTools.MobileToolControl.dll` is the `dotnet-surface` for the `mobile-tool-control` module
(`docs/mobile-ux/REMOTE_CONTROL_CONTRACT.md`, G2). It is loaded dynamically by the Android CoreCLR host
from the route declared in
`tools/remote-tool-gateway/android-integration/package/ui/tool.json`.

| Item | Value |
| --- | --- |
| Tool / module id | `mobile-tool-control` |
| Route id | `workspace` |
| Surface assembly | `surface/MyPowerTools.MobileToolControl.dll` |
| Surface type | `MyPowerTools.MobileToolControl.MobileToolControlSurfaceFactory` |
| Activation handler | the created `UserControl` implements `IMptAvaloniaSurfaceActivationHandler` |
| Target framework | `net10.0` (plain library; no Android TFM, no apphost) |
| Dependencies | `Avalonia 12.0.5`, `MyPowerTools.AvaloniaSdk 0.3.0`, `MyPowerTools.ToolSdk 0.2.0` |
| Theme | the shipped `MptMobileTheme.axaml` (SDK) on top of Fluent; no private palette, every colour/typography/touch metric comes from `MptMobile*` classes and tokens |
| Owns | only this project folder and its `tests/` |
| Module commands used | `mobile-tool-control.status / import.preview / import.confirm / devices.list / devices.check / devices.remove / catalog / invoke / invocation.status / invoke.cancel` |

The page never opens a socket, reads a credential or writes a file. It uses only the shared
`MptMobile*` style classes and resources from M1 — no new SDK type, no WebView, no new module framework.

## Activation

```
mypowertools://device-tool?device=<grantId>[&tool=<toolId>][&command=<commandId>][&run=1]
mpt://control/<base64url-json>
```

* `device` must be an imported computer. It is the module's `deviceId`, which **is the control
  `grantId`** from the connection code — it is **not** a file-transfer pairing peer id. The two ids are
  different credentials and different lists (`mobile-tool-control.devices.list` vs. file-transfer
  peers); never pass a file peer id here. Use what `import.confirm` returned or read
  `mobile-tool-control.devices.list`.
* `tool`/`command` are applied only when the computer's real catalog offers them. The tool→command
  relationship is taken from the catalog (`toolId` and `moduleId` are not assumed to be equal): a
  command matches when its module is the tool, when the tool's own module owns it, or when its id is
  prefixed by the tool id. A tool-only activation opens that tool's detail sheet; an unknown tool or
  command returns `false` with one surface warning so the Shell can fall back.
* **No external link ever executes anything.** `run=1` is accepted for compatibility and deliberately
  ignored: a web page, a notification or another app can only locate/prefill, and the user still has to
  press the page's own button. There is no auto-execute path.
* `mpt://control/…` is the desktop connection code: it is handed to the existing `import.preview` and
  the confirmation sheet opens. Nothing is stored and no command runs before the user presses 确认导入;
  the code is never logged and never appears in an error message or a command result.
* The import sheet also offers 扫描电脑上的二维码 when the host provides
  `MptAvaloniaSurfaceContext.ScanConnectionCodeAsync`; a scanned code takes exactly the same
  preview → confirm path. Without the capability the button is hidden and paste stays available.

The Shell resolves the handler through the existing activation path
(`ShellWorkspaceController.Activation.cs` → `IMptAvaloniaSurfaceActivationHandler`); no Shell change is
needed for the URI itself, only a device-detail entry that navigates to this tool route with the URI.

## Page-level back

The view implements both shared contracts: `IMptAvaloniaSurfaceActivationHandler` for the links above
and `IMptAvaloniaSurfaceBackHandler` (`TryHandleBack`) for the system back key.

`MobileToolControlView.TryHandleBack()` returns `true` when the page consumed the back key:

1. technical-details layer open → closes it;
2. parameter sheet open → closes it;
3. import sheet open → closes it;
4. invocation sheet open → closes it;
5. device detail open → returns to the device list;
6. otherwise `false` (the Shell may leave the tool page).

The page also consumes the hardware Escape/back key while it is attached. This mirrors
`RemoteCommandsMobileView.TryHandleBack()`; the same one-line Shell hook applies.

## What the page shows (and never invents)

* **Device list** — imported computers from the module's local record, with 尚未检查 / 已连接 / 无法连接.
  "在线" is only ever shown after the computer itself answered an explicit 检查电脑连接.
* **常用动作** — Input Monitor, ScreenEase and Paste Image actions, keyed by the real HostControl command
  ids those modules publish (`input-monitor.*`, `screenease.*`, `paste-image.*`). Wording is Chinese and
  says whether the action only reads (只读) or changes the computer.
* **全部命令** — every other command, grouped by its real `moduleId`, using exactly the computer's
  `title`/`subtitle`. Unauthorized commands are listed with the computer's reason and cannot be opened.
* **工具详情** — tapping a tool row opens that tool's sheet with the computer's real state/availability,
  how many of its commands this grant authorizes (x/y), and the tool's own action list. A tool with no
  declared commands says so instead of inventing capabilities.
* **Parameter form** — built from the real `parameters[]` (`string`/`int`/`bool`; required fields
  enforced client-side, the computer stays the authority).
* **调用** — the state label comes from the wire: `awaiting-confirmation` → 等待电脑确认 (the legacy
  `pending-confirmation` spelling is accepted and means the same), `claimed` → 电脑已受理，正在本机执行,
  `cancelling` → 正在取消，等待最终结果, and any unknown state is shown verbatim. The sheet shows the
  message, result summary / error code / retryable, the cancel answer, and a technical-detail layer with
  the raw JSON.
* **取消** — a 2xx cancel response is never treated as "已取消". The page reads the computer's own
  `cancelAccepted` answer: accepted → keeps polling for the real outcome and stops offering a second
  cancel; refused → says the computer did not accept it and keeps the call running; the invocation was
  already finished → says so.
* **空态 / 错误** — empty catalog, unreachable computer, revoked authorization and refused connection
  code all have their own honest text; there is no fabricated statistic, image or run state.

Refreshing: the device list is local state (no network). The catalog is read on page entry, on explicit
刷新工具目录, and on activation; while an invocation the user started is non-terminal, the page reads
`invocation.status` at most every 1.2 s **only while the page is visible**, and stops on terminal state,
on cancel, or when the page is detached (`Deactivate()`). No timer survives leaving the page.

## Handoff to the main agent (integration list)

1. **Android bundle** — follow §7 of
   `tools/remote-tool-gateway/android-integration/README.md` (slnx entries, `MyPowerTools.Android.csproj`
   `ProjectReference`s + `AndroidAsset` glob, `scripts/build-android.ps1` staging step). M7 owns those
   files, so this is a handoff, not a change made here.
2. **Shell entry (M2 device detail)** — add a phone entry that navigates to the `mobile-tool-control`
   tool route with
   `mypowertools://device-tool?device=<MobileDeviceItem.DeviceId>`; the page then lists that computer's
   real catalog. Optionally pass `&tool=` / `&command=` from a specific action row.
   `MobileToolCatalog` already classifies the ten desktop tools as `Computer`; once the user has imported
   a computer, their detail pages can call this URI instead of showing “手机遥控尚未接入”.
3. **G1 wire** — the module consumes `/mpt-control/v1` exactly as documented in the module README §4.
   The gateway's `allowed`, `notAllowedReason`, `terminal` and `result` fields drive the page; the phone
   never derives success from an HTTP status.
4. **No root solution edit here** — `MyPowerTools.slnx` / `MyPowerTools.Android.slnx` and release
   versioning stay with the main agent.

## Tests

```bash
dotnet test src/MyPowerTools.MobileToolControl/tests/MyPowerTools.MobileToolControl.Tests.csproj -c Debug
```

Headless Avalonia tests over a contract fake of the module: catalog wording and grouping, unauthorized
commands, empty/failed catalog, typed parameter encoding (`int` → JSON number, empty optional → omitted,
invalid value → field error and no request), the exact `invoke` payload (including a non-empty invocation
id), wire-state labels (including `awaiting-confirmation`, `cancelling` and an unknown state shown
verbatim), the three cancel answers (accepted / accepted-and-terminal / refused / already finished),
single-flight polling with a stale-answer guard and resume after re-activation, technical details, import
preview/confirm/refusal, QR scanning through `ScanConnectionCodeAsync`, activation parsing and
application (`run=1` ignored, tool-only, catalog tool→module mapping), and back-key ordering. Output goes
to `artifacts/build/bin/MyPowerTools.MobileToolControl/<config>/` per the repository artifacts policy.

The headless host loads the real SDK mobile theme (`MptMobileTheme.axaml` on top of Fluent) and renders
through Skia, so the page is verified against the shipped classes, tokens and light/dark dictionaries.
`ThemeTests` asserts a live light → dark → light switch on one page instance (cards and text follow the
token values), the page title size and page padding coming from the theme, the 320 dp narrow-page class,
and that every laid-out primary action keeps the theme touch target and fits the screen.
`ThemeScreenshotTests` writes the reviewed frames to `artifacts/.tmp-android-verify/g2-theme-shots/`:
`390-device-tools-light.png`, `390-device-tools-dark.png`, `390-device-tools-light-again.png` (same page
instance after the round trip), `320-parameter-sheet.png` and `320-import-confirm-sheet.png`. Sheets may
scroll; their action buttons are asserted to be laid out and unclipped.

The real combination is covered separately:
`tools/remote-tool-gateway/android-integration/tests/MobileToolControl.Integration.Tests` starts the real
desktop gateway (`RemoteToolGatewayService`) on a real loopback HTTP listener and drives the real
`MobileToolControlModule` and this real view model through catalog → typed form → success / real host
failure / awaiting-confirmation → cancel (accepted, refused, already finished) / deep links. Only
`IHostControlBridge` is a test command there. Run it with:

```bash
dotnet test tools/remote-tool-gateway/android-integration/tests/MobileToolControl.Integration.Tests/MyPowerTools.MobileToolControl.Integration.Tests.csproj -c Debug
```
