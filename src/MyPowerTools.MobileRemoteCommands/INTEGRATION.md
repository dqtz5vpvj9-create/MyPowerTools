# Remote Commands phone surface — integration contract

`MyPowerTools.MobileRemoteCommands.dll` is the `dotnet-surface` for the `remote-commands-android`
module. It is loaded dynamically by the Android CoreCLR host from the route declared in
`tools/remote-commands/android-integration/package/ui/tool.json`.

| Item | Value |
| --- | --- |
| Tool / module id | `remote-commands-android` |
| Route id | `workspace` |
| Surface assembly | `surface/MyPowerTools.MobileRemoteCommands.dll` |
| Surface type | `MyPowerTools.MobileRemoteCommands.RemoteCommandsMobileSurfaceFactory` |
| Target framework | `net10.0` (plain library; no Android TFM, no apphost) |
| Dependencies | `Avalonia 12.0.5`, `MyPowerTools.AvaloniaSdk 0.3.0`, `MyPowerTools.ToolSdk 0.2.0` |
| Linked product source | `RemoteCommandsYaml.cs`, `RemoteCommandsStore.cs`, `RemoteCommandsFile.cs` (read-only closure of the YAML validator) |
| Owns | only this project folder |

The page never starts a process, never opens a socket, never writes `commands.yaml`, `settings.json` or
`history.json`, and never reads `~/.ssh`. Every effect is a module command, so the module stays the single
owner of SSH, the host catalog, the host-key store, the secret store and the data directory.

## Page shape (approved prototype)

| Prototype | Implementation |
| --- | --- |
| 子导航 + 返回 | sub-nav row: `‹` (tool library), `远程命令`, `⟳` (real status refresh) |
| `指令，触手可及。` + `家用服务器 · 已连接` | page title + **honest** connection summary: default alias, mapped host count, fingerprint state, “连接在运行时按需建立”. The module has no connectivity probe, so the page never claims “已连接”. |
| `常用指令` list | the real `commands.yaml` catalog: label, real command text, `远程`/`本地`, tap → run sheet. Search appears once the catalog is longer than a screen. Empty state explains how to add the first command. |
| `添加` | command form sheet (add/edit/delete) that writes the shared document through `catalog.save` |
| `上次结果` + terminal block | the last run of this page instance, or the module's real `history.summary` when nothing ran here; tap → run sheet |
| `管理服务器连接` | connection sheet: host mappings, credentials, trusted fingerprints, shared settings, raw YAML editor, module state |
| bottom sheets | one overlay layer with a scrim, a bottom panel, scrollable content, back/scrim/✕ to close, focus restored to the opener |

## Commands used

No new module command was added; the ids, arguments and payload shapes are unchanged from the previous
revision (see the module's `package/module.json`).

| Command id | Arguments | Used for |
| --- | --- | --- |
| `remote-commands-android.status` | – | transport/background availability, default host, alias list, retention, CONDA_EXE, timeout, hosts, trusted keys, active invocation + stage, last run summary, `commandsPath`, `dataDirectory` |
| `remote-commands-android.catalog` | – | command list (`id,label,command,description,type,host,input1*,input2*,showSecondInput,usesRemoteHost`), parse `error`, `commandsPath`, and the catalog text (`yaml`/`content`/`commandsYaml`, else the canonical file is read) |
| `remote-commands-android.catalog.save` | `content` | validate with the shipped parser and write the canonical `commands.yaml`; invalid documents are rejected with the parser message and the editor keeps the user's text |
| `remote-commands-android.hosts.list` | – | alias → `host`/`port`/`username`/`auth`/`credentialConfigured`, `missingAliases`, `loadFailed` |
| `remote-commands-android.host.add` | `alias,hostName,port,username,auth,password?,privateKey?,passphrase?` | create/update a mapping; secrets only ever travel here |
| `remote-commands-android.host.remove` | `alias` | delete mapping + credential + trusted key |
| `remote-commands-android.host-key.status` | – | confirmed `host,port,hostKeyName,fingerprint,addedAt` |
| `remote-commands-android.host-key.accept` | `hostName,port,fingerprint,hostKeyName?` | confirm exactly one SHA256 fingerprint |
| `remote-commands-android.host-key.revoke` | `hostName,port` | forget one fingerprint |
| `remote-commands-android.run` | `commandId,host,input1,input2,secondInput,invocationId` | run; payload carries `state,message,exitCode,host,alias,resolvedHost,output,pendingHostKey,transport` |
| `remote-commands-android.cancel` | `invocationId?` | cancel the active run |
| `remote-commands-android.history.summary` | – | `count,latestTimestamp,latestLabel,latestHost` |
| `remote-commands-android.history.clear` | – | delete local history |
| `remote-commands-android.settings.update` | `values` | the only settings write path: `{defaultHost, knownHosts, historyRetention, condaExecutable, commandTimeoutMinutes}` |

Events consumed through `MptAvaloniaSurfaceContext.SubscribeEvents`: `run.started`, `run.stage`,
`command.output` (`line` or `lines`), `run.finished`, `host-key.pending`, `host.updated`, `host.removed`,
`host-key.accepted`, `host-key.revoked`, `history.cleared`, and — new in this revision — `catalog.saved`
and `settings.updated`, which refresh exactly the affected slice. The page polls nothing: it reads the
module on activation, after its own actions, on those events, and when the user taps `⟳`.

## Invocation id (progress and cancel correlation)

The module keys its `run.stage` / `command.output` events and its cancellation on the **invocation id of
the command envelope**, not on a command argument: `RemoteCommandsAndroidRunner` publishes
`request.InvocationId` and `cancel` compares `args.invocationId` against that same value, refusing a
mismatch. A page that cannot set the envelope id therefore gets no attributed progress and its cancel is
refused.

* The page uses `MptAvaloniaSurfaceContext.ExecuteCommandWithInvocationAsync` (SDK 0.3.0) directly: every
  call carries a page-owned id, so streamed progress lines up and `cancel` names exactly its run.
* Fallbacks for a host that leaves the capability null, so the page degrades honestly rather than
  misbehaving:
  * no capability → `cancel` omits the id (the runner then cancels its single active run, which is the one
    this page started), and run events are accepted only while this page has a run in flight;
  * terminal `run` payloads always carry the full output, so a dropped stream never loses the result.

## Run flow

* **Inputs keep the desktop semantics.** Input 1 is always offered with the catalog's own label and
  placeholder; input 2 appears only when the command declares `show_second_input`, and the toggle is what
  is sent as `secondInput`. The runner still receives both `input1`/`input2` and writes them to the remote
  `--file1`/`--file2` temporary files.
* **Progress is the module's own stages.** `run.started` → `connecting` (implicit) → `run.stage:
  uploading` → `run.stage: running` → `run.finished` drive a three-step list (连接主机 / 上传输入 /
  执行命令) plus an indeterminate bar while the run is in flight. Local `py` transforms show one local
  step and never touch SSH.
* **Cancel** sends `cancel` with the invocation id of the run this page started. Events from another
  invocation never own the output pane.
* **Retry** re-sends the last request (same command, host, inputs, second-input flag) with a **new**
  invocation id. It is offered after a failure and after a cancel, and is never a simulation.
* **Readable result**: state glyph + the module's message/exit code, the real execution device
  (`alias（resolvedHost）`), and a monospace block that starts with the configured command (`$ uptime`).
  The terminal `run` payload always overwrites streamed lines, so a dropped event cannot leave a partial
  log.
* **Permission**: if the runtime refuses the command (`MPT_PERMISSION_REQUIRED` / `permission-required`,
  no run payload), the page says it is waiting for authorization and keeps retry available. It never
  treats that as a result and never adds a permission system of its own.
* **Transport**: without a managed SSH transport the run button is disabled for shell commands (the page
  states why) while local transforms stay available.

## Host key trust rules

* No command in this surface trusts a key wholesale: the only accept call passes the exact
  `hostName` + `port` + SHA256 `fingerprint` the module reported for this attempt.
* The accept button stays disabled until the user ticks “我已核对上面的 SHA256 指纹” (two-way bound).
* A run that stops with `state = host-key-required` keeps the entered inputs; confirming the fingerprint
  repeats that same run.
* Rotation is surfaced as a change (the module's `firstUse=false` wording), never as a silent accept.
* Revoking a fingerprint is a two-tap action; back cancels the armed confirmation.

## Command editing

The `添加` / `编辑命令` form edits one entry of the shared `commands.yaml`:

* Fields: 名称, 命令, 说明, and (under “更多设置”) 标识, 运行位置 (shell/py), 固定主机, 输入 1 标签/提示,
  第二个输入 with its labels. The identifier is derived from the label when possible and stays editable.
* The document text is produced by `RemoteCommandsCatalogEditor` (`TryAdd` / `TryUpdate` / `TryRemove`):
  it rewrites only the edited entry and keeps every other line — comments, ordering, the `types:` section
  — byte for byte. The result is handed to `catalog.save`, so the module's shipped parser is still the
  authority; a rejection keeps the form text and shows the module's message.
* Values are single-line and written double-quoted; the shipped parser strips one layer of quotes, so
  quotes/colons/CJK round-trip (covered by tests).
* Deleting refuses to leave the document without a `commands:` entry list.
* The raw YAML editor stays available under 服务器连接 → 高级 → 命令配置 for documents the form cannot
  express; it has the same validate-and-reject behaviour and keeps the text on failure.

## Connection management (on demand)

Everything about connecting lives in one sheet, opened from 管理服务器连接:

* Host mappings with the real endpoint, auth kind and credential state; 编辑 fills the form, 删除 is a
  two-tap action that also removes the credential and the fingerprint.
* Missing aliases (referenced by commands or the shared settings) are listed with a 映射 shortcut.
* Secrets only travel as `host.add` arguments; the fields are cleared as soon as the module stored them
  and the page never renders a stored value back.
* Confirmed fingerprints with revoke.
* The five shared settings are edited with the module's own validation
  (`remote-commands-android.settings.update`); a rejected save keeps the visible draft, and the page
  never writes `settings.json`.
* Module state (commands path, validation status, settings summary, data directory) lives in 高级, not on
  the home page.

## Wire numbers (the 299/300 settings regression)

On Android the arguments cross HostControl as a protobuf `Struct`, which has no integer type, so the
module must accept an integral double and the page must read one back exactly.

* The page sends `historyRetention` / `commandTimeoutMinutes` / `port` as **JSON integers**, asserted by
  `WireNumberTests.Settings_are_sent_as_json_integers_not_doubles_or_strings`.
* `RemoteCommandsMobileJson.Int/NullableInt` accepts int, long, integral double and integral numeric
  string, and rejects fractional/NaN instead of silently rounding. A status payload that came back as
  protobuf doubles (300 → “300”, port 2222 → 2222) is asserted by
  `A_status_payload_that_came_back_as_protobuf_doubles_is_read_exactly`.

## Mobile theme contract (M1)

The page writes against the class names M1 actually ships in
`src/MyPowerTools.AvaloniaSdk/Themes/MptMobile*.axaml` and sets **no local colour** on them, so the SDK
theme owns colours, typography, touch metrics and hover/pressed states.

| Role | Classes used |
| --- | --- |
| page | `MptMobileRoot` (root grid), `MptMobilePage` (+ `MptMobilePageNarrow` below 340 dp) |
| text | `MptMobilePageTitle`, `MptMobilePageSubtitle`, `MptMobileSectionTitle`, `MptMobileRowTitle`, `MptMobileRowSubtitle`, `MptMobileRowMeta`, `MptMobileMeta`, `MptMobileNote`, `MptMobileMono`, `MptMobileFieldLabel`, `MptMobileSheetTitle`, `MptMobileEmptyTitle`, `MptMobilePillText` |
| tone text | `MptMobileSuccessText`, `MptMobileWarningText`, `MptMobileFadedText`, `MptMobileAccentText` (only the error tone, which M1 does not name, is pinned from the palette and cleared again) |
| surfaces | `MptMobileCard`, `MptMobileListCard`, `MptMobileInsetPanel`, `MptMobileCommandOutput`, `MptMobileIconBox`, `MptMobileNotice` (+ `warning`), `MptMobilePill` (+ `offline`), `MptMobileRowDivider`, `MptMobileSearchBox`, `MptMobileSheet`, `MptMobileSheetGrabber`, `MptMobileOverlay` |
| controls | `MptMobilePrimary`, `MptMobileSecondary`, `MptMobileQuietButton`, `MptMobileTextButton`, `MptMobileIconButton`, `MptMobileBackButton`, `MptMobileCloseButton`, `MptMobileListRow`, `MptMobileField`, `MptMobileSearch`, `MptMobileCheck`, `MptMobileProgress` |
| marks | `Path` geometries from the prototype with `MptMobileIcon` (+ `MptMobileIconMuted/Accent/Success/Warning`) — no icon font is required |

* Page-only visuals (pre-M1 fallback styles, error tone, terminal text colour) use
  `RemoteCommandsMobilePalette`, which follows `MptAvaloniaSurfaceContext.Theme` and the prototype's
  light/dark table.
* The SDK theme is detected per theme variant (`MptMobileCardBrush` through
  `TryFindResource(key, ActualThemeVariant, …)`, because the tokens live in variant dictionaries). When it
  is absent (older host), `CreateFallbackStyles` installs class styles built from the same palette and
  `FallbackClassNames`; `MobileThemeContractTests` asserts unconditionally that every `MptMobile*` class the
  page uses is covered there, so a class that would be unstyled on an older host fails the tests instead of
  the device.
* Touch targets, field sizes and page padding come from the M1 tokens; the page sets no local metric where
  the theme provides one (it only selects `MptMobilePageNarrow` below 340 dp) and only the error tone is
  pinned, because M1 names no error colour. On `ActualThemeVariantChanged` the page re-pins that tone from
  the new variant and clears it when the tone changes, so nothing stale survives a theme switch.

## Back, keyboard and focus

* `RemoteCommandsMobileView.TryHandleBack()` is public and is the single back path: it cancels an armed
  two-tap confirmation, closes the open sheet (asking once more when a form holds unsaved edits), and
  returns `false` when the page has nothing left to close.
* The page also listens on its `TopLevel` for `Key.Escape`/`Key.Back` (tunnel) and consumes it through the
  same method.
* `RemoteCommandsMobileView` implements `IMptAvaloniaSurfaceBackHandler` (SDK 0.3.0) and the Shell side is
  wired: `MobileShellView.HandleBackAsync` → `TryHandleSurfaceBack()` offers Back to the open page (direct
  host or managed surface) before it closes the tool surface. Android therefore closes a sheet or disarms a
  confirmation here first, and only then leaves the page.
* Single-line form fields submit on Enter (the phone keyboard's action key); starting a run moves focus
  off the input so the soft keyboard closes; closing a sheet restores the focus it took.

## Build

```bash
dotnet build src/MyPowerTools.MobileRemoteCommands/MyPowerTools.MobileRemoteCommands.csproj -c Release \
  -p:StageRepositoryModule=false
```

## Tests

`tests/` is a separate xunit + Avalonia.Headless project (excluded from the surface's `Compile` glob).
Its host application registers the **real** SDK mobile theme
(`avares://MyPowerTools.AvaloniaSdk/Themes/MptMobileTheme.axaml`), exactly like the Android Shell, so the
suite exercises the shipped variant-aware tokens, typography, surfaces and control styles - not only this
page's pre-theme fallback.
It uses a **contract fake backend** (`FakeModule`): the command catalog is parsed by the shipped
`RemoteCommandsYaml` source, `catalog.save` is validated by that same parser, `settings.update` can be
normalised the way a protobuf `Struct` does, and a held run publishes `run.started`/`run.stage`/
`command.output`/`run.finished` while staying in flight. No SSH, no Android host, no tool data directory.

```bash
dotnet test src/MyPowerTools.MobileRemoteCommands/tests/MyPowerTools.MobileRemoteCommands.Tests.csproj
```

63 tests: run flow (list/tap, payload, stage progress, cancel, retry, second-input semantics, host-key
trust gating, permission-required, no-host and no-transport paths, search), command editing (add / edit /
delete through `catalog.save`, parser rejection keeps the text, invalid id blocked), catalog transforms
(quoting/round-trip, unknown id, last-command refusal, sample document), settings draft regression (kept
from the previous revision, now driving the real controls), wire numbers, back/keyboard/focus, and the
theme contract.

`ScreenshotTests` renders real frames with Skia at 320/360/390 dp plus the run/editor/connection/catalog
sheets into `artifacts/.tmp-android-verify/rc-shots/` (the registered temporary verification area) for
visual comparison with `.lavish/mpt-mobile`.

## Unresolved / handed to the integrator

1. **No connectivity probe.** The module exposes no “check host” command, so the page deliberately shows
   “连接在运行时按需建立” plus the confirmed-fingerprint state instead of an invented online/offline
   badge. Adding a real probe would be a module change, which this task must not make.
2. **Input requirement is unknown per command.** `commands.yaml` has no “this command needs input” flag,
   so the run sheet always shows input 1 (with the catalog's label/placeholder) even for read-only
   commands. A `requires_input` key would be a schema addition owned by the module.
3. **Sensitive operations** keep the existing mechanisms: broker/host authorization for a denied run,
   module validation for the catalog and settings, two-tap confirmations for delete/revoke/clear, and
   host-key acceptance gated on the exact fingerprint.
