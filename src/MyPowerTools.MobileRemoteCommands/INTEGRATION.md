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
| Dependencies | `Avalonia 12.0.5`, `MyPowerTools.AvaloniaSdk 0.2.1`, `MyPowerTools.ToolSdk 0.2.0` |
| Linked product source | `RemoteCommandsYaml.cs`, `RemoteCommandsStore.cs`, `RemoteCommandsFile.cs` (read-only closure of the YAML validator) |
| Owns | only this project folder |

The page never starts a process, never opens a socket and never writes `commands.yaml`,
`settings.json` or `history.json`. Every effect is a module command, so the module stays the single
owner of SSH, the host catalog, the host-key store, the secret store and the data directory.

## Commands used

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
| `remote-commands-android.settings.update` | `values` | the only settings write path: `{defaultHost, knownHosts, historyRetention, condaExecutable, commandTimeoutMinutes}`, validated by the module's `UpdateSettingsAsync` |

Events consumed through `MptAvaloniaSurfaceContext.SubscribeEvents`: `run.started`, `run.stage`,
`command.output` (`line` or `lines`), `run.finished`, `host-key.pending`, `host.updated`,
`host.removed`, `host-key.accepted`, `host-key.revoked`, `history.cleared`. Stream events are matched
by `invocationId`, and the terminal `run` payload always overwrites the streamed output, so a dropped
event cannot leave the page with a partial log.

## Host key trust rules

* No command in this surface trusts a key wholesale: the only accept call passes the exact
  `hostName` + `port` + SHA256 `fingerprint` the module reported for this attempt.
* The accept button stays disabled until the user ticks “我已核对上面的 SHA256 指纹”.
* A run that stops with `state = host-key-required` keeps the entered inputs; confirming the
  fingerprint repeats that same run.
* Rotation is surfaced as a change (the module's `firstUse=false` wording), never as a silent accept.

## Commands configuration (editable)

The “命令配置” card is a compact expandable editor for the shared `commands.yaml`:

* 保存到模块 → `catalog.save`; the module validates with the same parser the desktop uses and only
  writes the canonical module file, so the phone cannot create a second catalog.
* 从剪贴板导入 → paste a document into the editor; 重新载入 → drop local edits; 复制当前 YAML.
* On success the page re-reads `catalog` and the command picker refreshes; on failure the validation
  message is shown next to the editor and the edited text is preserved.

## Settings (editable)

The “设置” card edits all five keys in place and saves them with a single
`remote-commands-android.settings.update` call:

| Field | Values key |
| --- | --- |
| 默认主机 | `defaultHost` |
| 主机别名列表（每行一个） | `knownHosts` |
| 历史保留条数 | `historyRetention` |
| CONDA_EXE（远端前缀） | `condaExecutable` |
| 单次执行超时（分钟） | `commandTimeoutMinutes` |

Rules:

* The surface writes **no** settings file. The module validates with the same `UpdateSettingsAsync`
  path the Shell uses, so the desktop and the phone cannot fork the settings document.
* The draft is marked clean only when the whole save succeeds. On any rejection the typed values stay
  on screen together with the module's message, and the card keeps showing “有未保存的修改”.
* Client-side pre-checks (alias shape via the shipped `RemoteCommandsStore.IsValidHost`, retention
  10–5000, timeout 1–1440) only shorten the feedback loop; the module remains the authority.
* 还原为模块状态 discards local edits and re-reads the module status.
* The save click first commits the visible control text into the draft, so a save can never validate a
  value the user already replaced on screen. Settings property setters pass their property name
  explicitly (a `[CallerMemberName]` helper used to notify “SetSetting”, which left the two-way binding
  without a source notification).
* An async status refresh never overwrites a typed draft: the five settings controls keep the user's
  text while `SettingsDirty`, and the commands.yaml editor keeps its text while `CatalogDirty`
  (only 重新载入 or a successful save replaces it).

## Deep link

`mypowertools://remote-command?command=<id>[&host=<alias>][&run=1][&input1=<text>][&input2=<text>]`
preselects a catalog command and optionally runs it. Unknown command ids are rejected (the call
returns `false`), so the Shell can fall back to opening the tool page.

## Build

```bash
dotnet build src/MyPowerTools.MobileRemoteCommands/MyPowerTools.MobileRemoteCommands.csproj -c Release
```

## Tests

`tests/` is a separate xunit + Avalonia.Headless project (excluded from the surface's `Compile` glob) that
drives the real view: it types into the retention `TextBox`, clicks save, and asserts the exact
`settings.update` payload. It uses a fake module client, so it needs no SSH, no Android host and writes no
tool data.

```bash
dotnet test src/MyPowerTools.MobileRemoteCommands/tests/MyPowerTools.MobileRemoteCommands.Tests.csproj
```

Covered regressions: invalid edit rejected with the parser message; corrected edit reaching the module;
visible draft and view model identical after a rejected save; a retry re-sending the same draft; a
programmatic draft write reaching the typed control; the click path committing the visible text even
before the binding round-trips.

Output goes to `artifacts/build/bin/MyPowerTools.MobileRemoteCommands/<config>/` per the repository
artifacts policy; the tool package must copy `MyPowerTools.MobileRemoteCommands.dll` (and its
non-framework dependencies) to `surface/` next to the module payload.

## Backend contract dependencies

* `catalog.save { content }` — implemented by the backend; the page shows the parser's rejection
  verbatim and keeps the edited text.
* `settings.update { values: { defaultHost, knownHosts, historyRetention, condaExecutable,
  commandTimeoutMinutes } }` — implemented by the backend on top of the existing validated
  `UpdateSettingsAsync`. The page adds no commands of its own; if the backend renames an id or an
  argument, change `RemoteCommandsMobileContract` only.
* Per-host credential rotation stays a `host.add` re-save by design (secrets only travel there).
