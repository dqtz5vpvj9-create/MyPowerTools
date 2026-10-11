---
name: mpt-file-transfer
description: 使用 MyPowerTools 文件传输助手 MCP 收发文件、向手机投递安装包、查询接收回执或选择夸克网盘中转。用户说“发到文件传输助手”“传到我手机”或要求 Agent 间传文件时使用。
---

# MPT 文件传输助手

复用当前主机已安装的共享 HTTP MCP 和后台 Runner。多个聊天共用一个接入服务，不要为每个聊天启动 Python MCP 或新建 Runner。文件助手是共享会话（公屏），设备私聊是另一种投递范围；两者使用同一后台传输服务。网络链路由后台选择，发送不以用户手动连接 Tailscale 为前提。

## 找到工具与目标

查找工具名后缀 `mpt_devices`、`mpt_send_shared`、`mpt_send_files`。不同客户端可能显示 `mcp__mpt_file_transfer__…`、`mypowertools-file-transfer` 等前缀；使用当前客户端提供的实际名称。未加载时用工具搜索发现；仍无 MCP 时按下方 CLI 入口继续。

先调用 `mpt_devices({})`，必要时用 `mpt_conversations({})` 区分当前公屏和私聊，用 `mpt_status({})` 检查消息。文件的绝对路径必须存在于 **MCP 进程所在主机**；远程 MCP 不能直接读取 Agent 本地路径。

- “发到文件传输助手”：用公屏，全部共享成员可见。无须先给每个成员做私聊配对。
- “只发给某设备”：用该设备的稳定 `deviceId` 私聊，检查 `canPrivateMessage`；需要配对时使用已有私密连接码文件，或让用户完成配对。不要把私聊改成公屏。
- “传到我手机”但有多台手机且当前上下文不能确定：先澄清目标。设备显示名称可能重复，也不要把测试 Pixel 当作用户日用手机。

`available:false` 不是禁止公屏投递的依据，也不能据此断言手机关机或无法接收；`canPrivateMessage:false` 只说明不能直接使用私聊。公屏可以先入队，由后台等待接收。

## 发出一次，再等待回执

优先将发送与等待分开，及时拿到持久化的消息 ID，避免长调用超时后重复发送。

公屏发送（把示例路径换成真实文件）：

```json
mpt_send_shared({"files":["/absolute/path/app.apk"],"text":"新版安装包","wait_for_receipt":false})
```

私聊发送：

```json
mpt_send_files({"device":"DEVICE_ID","files":["/absolute/path/report.pdf"],"wait_for_receipt":false})
```

纯文字也用这两个工具，传 `files:[]` 和 `text`。返回的 `data.accepted` 和 `data.itemIds` 只表示已入队。保存 **全部** `itemIds`，然后：

```json
mpt_wait_receipts({"item_ids":["ITEM_ID"],"receiver":"DEVICE_ID","timeout_seconds":60})
```

公屏等待必须指定接收设备；`receiver` / 发送时的 `receipt_from` 只过滤回执，不改变公屏可见范围。若用户只要求投到公屏、未指定接收者，可先报告已入队，再用 `mpt_status({})` 查看该消息已有的回执，无须为此阻塞发送。

等待超时或 MCP 调用中断后，后台仍会继续工作。用原 ID 查询 `mpt_receipts` 或继续 `mpt_wait_receipts`；不要重新发送同一文件。工具错误中的结果 JSON 可能仍含 `data.itemIds` 或 `data.items[].itemId`。如果根本没有拿到 ID，先在 `mpt_status` 的近期条目核对目标、文件和时间，确认是否已入队，再决定是否重发。

报告与证据对应：

| 结果 | 可报告的状态 |
| --- | --- |
| accepted / queued | 已加入发送队列 |
| sending | 发送中，并给出已有进度 |
| stored，无目标回执 | 已发送到中转，等待设备接收 |
| 目标设备对每个 item 的 confirmed 为 true | 目标设备已接收 |
| failed / cancelled / 超时 | 如实说明错误或仍在等待，保留消息 ID |

公屏条目即使仍是 `stored`，也可能已有设备回执；判断指定设备是否收到，以该设备回执为准。多个附件及文字可能对应多个 ID，不能只看其中一个。只有用户要求取消时才调用 `mpt_cancel({"item_id":"ITEM_ID"})`。

## 大文件与网盘

用户要求走夸克或避免公网附件流量时，先调用 `mpt_cloud_accounts({})`，复用 `status:ready` 的账号。默认账号为夸克、策略为 `cloudOnly` 时：

```json
mpt_send_shared({"files":["/absolute/path/large.apk"],"via":"quark","wait_for_receipt":false})
```

需要更改时，`mpt_cloud_select({"account_id":"ACCOUNT_ID","mode":"cloud-only"})` 会持久修改默认账号与后续传输策略；按用户指定的网盘要求执行，并说明这项持久影响。`via:auto` 遵循当前策略，不等于强制直连。

当前 MCP 的显式 `via` 仅支持 `auto`、`quark`，不要编造 `via:baidu`。私聊接口未提供夸克中转；保持用户选择的可见范围。`transferAvailable:false` 可能只是尚未确认领取能力，不能把已就绪账号当作未登录而要求再次授权；首次入队后检查实际状态和回执。当前网盘领取链路仍可能需要发送端在线，不能承诺发完关机后一定可取。

只有账号确实需要授权时才用 `mpt_cloud_authorize` / `mpt_cloud_complete`。当前 CLI 授权可能需要已有凭据文件，而非可用二维码；以工具结果为准。配对用 `mpt_pair_device({"invitation_file":"绝对路径"})`，加入公屏用 `mpt_join_shared`；两者不要混淆。凭据和邀请只传私密文件路径，不把内容复制到工具参数或答复。

## 接收与 MCP 不可见时的入口

后台 Runner 自动接收。`mpt_status` 中收到的文件处于 `available` 且提供 `localPath` 时，在该 MCP 主机读取该路径。当前 MCP 没有下载任意历史消息或打开手机路径的工具；状态接口只包含最近约 200 条，找不到旧条目不代表从未发送。

Linux 已安装包装命令通常为 `~/.local/bin/mpt`：

```bash
~/.local/bin/mpt transfer help
~/.local/bin/mpt transfer devices --json
~/.local/bin/mpt transfer send --conversation shared --file /absolute/path/app.apk --json
~/.local/bin/mpt transfer wait --item ITEM_ID --from DEVICE_ID --timeout 60 --json
```

Windows HTTP 配置位于 `%LOCALAPPDATA%\Programs\MyPowerTools\TransferMcp\mcp.json`，包含认证 headers，不要打印或转发其内容。服务由现有 ServiceManager 管理，名称为 `mpt-transfer-mcp.service`；Linux 对应 `systemctl --user status mpt-transfer-mcp.service`。优先刷新当前客户端的 MCP 配置以连接共享端点。

需要 CLI 备用入口时，Windows 使用 `%LOCALAPPDATA%\Programs\MyPowerTools\Cli\MyPowerTools.Cli.exe transfer ... --data-root %LOCALAPPDATA%\MyPowerTools --json`；不要再从 HTTP 配置寻找 `env.MPT_COMMAND_JSON`。PowerShell 脚本使用 `pwsh.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File ...`，保持控制台隐藏。

使用已有数据根目录和后台服务，不另起空白 Runner、不重新生成身份、不要求重连 Windows 桌面。服务不可用时先报告实际错误并检查已有服务状态；安装或修复按当前任务授权执行。新增 skill/MCP 后，已经打开的会话可能需要刷新工具或新建会话；可让旧会话直接读取本 SKILL.md 并使用 CLI。
