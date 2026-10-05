# 文件传输 CLI 与 Agent MCP

Linux 使用已安装的 MyPowerTools Runner 持续接收、上传并保存待发队列。CLI 和 MCP 通过带认证的 HostControl 连接这个 Runner，沿用文件助手的身份、配对和回执。

## Windows 部署

复用当前用户已运行的开发版 Runner、设备身份和文件助手数据。无需启动第二个 Runner。
在仓库根目录使用隐藏 PowerShell 执行：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File scripts/install-windows-transfer-mcp.ps1 -RegisterCodex
```

需要 .NET 10 SDK、支持 fastmcp 的 Python，以及可用的 Codex CLI。安装脚本构建 Windows CLI，在 `%LOCALAPPDATA%\Programs\MyPowerTools\TransferMcp` 创建独立 Python 环境，并生成 `mcp.json`，可供其他支持 stdio MCP 的 Agent 客户端导入。使用 `-CliDirectory` 可以指定预先发布的 Windows CLI 目录，该目录必须持久保留。

`-RegisterCodex` 注册名为 `mpt-file-transfer` 的服务器；已启动的 Agent 会话需重新加载 MCP 或新建会话。服务器使用 `pythonw.exe`，CLI 子进程设置 `CREATE_NO_WINDOW`。不会因每次工具调用而闪出控制台。Runner 必须由正常的开发版运行流程启动；模块被隔离时会返回真实错误，不另建空白设备身份。

## 安装 Linux 服务

需要 .NET 10 SDK、PowerShell、Python 3、`libsecret-tools`，以及当前用户可用且已解锁的 Secret Service 密钥环。无桌面会话时也需要配置用户 D-Bus 和密钥环；凭据不会降级到明文文件。安装脚本使用 `/mnt/cache/data-cache` 暂存构建文件，该目录需可写。

在仓库根目录执行：

```bash
scripts/install-linux-transfer.sh --start --with-mcp
export PATH="$HOME/.local/bin:$PATH"
mpt transfer help
mpt transfer status --json
systemctl --user status mpt-transfer.service
```

默认安装到 `~/.local/share/MyPowerTools/headless`，持久数据位于 `~/.local/share/MyPowerTools/headless-data`（设置 `XDG_DATA_HOME` 时跟随该目录）。脚本安装完整 Runner、CLI 和文件传输模块，创建 `mpt` 包装命令及用户服务；包装命令自动设置数据目录和 socket。可以用 `--prefix`、`--data-root`、`--bin-dir` 更改位置。再次执行脚本更新安装；已有运行服务会恢复启动。

直接调用 CLI 时，`MPT_DATA_ROOT` 和 `MPT_ENDPOINT_ADDRESS` 设置连接默认值，命令行 `--data-root`、`--endpoint-address` 优先。数据目录必须对应正在运行的 Runner，以便读取其认证令牌。

## 配对、共享会话与发送

设备配对建立私聊权限，共享邀请用于加入文件助手公屏，两者分别执行：

```bash
mpt transfer pair --code-file /path/to/private-device-code --json
mpt transfer join --code-file /path/to/private-shared-invitation --json
mpt transfer devices --json
mpt transfer conversations --json
```

在已有共享会话的设备上可以导出邀请文件：

```bash
mpt transfer invite --output /path/to/new-private-invitation --json
```

输出路径必须不存在；Unix 上创建权限为 `0600` 的文件。文件只包含邀请代码，JSON 只返回文件名与设备状态。通过可信方式将文件交给加入设备；不要把邀请内容贴入 Agent 对话或工具参数。

私聊发送只对指定的已配对设备投递：

```bash
mpt transfer send --to DEVICE_ID --text '资料已整理' \
  --file /path/to/report.pdf --wait --timeout 600 --json
```

公屏内容对全部共享成员可见。`--receipt-from` 只指定等待谁的回执，不改变内容可见范围：

```bash
mpt transfer send --conversation shared --text '共享资料' \
  --file /path/to/report.pdf --receipt-from DEVICE_ID \
  --wait --timeout 600 --json
```

`--file` 可以重复。优先使用 `devices` 返回的稳定 `deviceId`；CLI 也接受完全匹配且唯一的显示名称，重名会报错。文件路径属于 CLI/MCP 所在主机。

## 夸克大文件

夸克目前用于共享附件，私聊不能使用 `--via quark`。先在 Linux 端授权，并明确选择默认账号和持久传输策略：

```bash
mpt transfer cloud authorize --provider quark --json
# 当前 Linux 授权需要已有有效凭据；将其保存到本机权限 0600 的私密文件。
mpt transfer cloud complete --operation OPERATION_ID \
  --credential-file /path/to/private-cookie --kind cookie --json
mpt transfer cloud accounts --json
mpt transfer cloud default --account ACCOUNT_ID --json
mpt transfer cloud preferences --mode cloud-only --json
mpt transfer send --conversation shared --file /path/to/large-file.zip \
  --via quark --receipt-from DEVICE_ID --wait --timeout 1800 --json
```

授权入口返回操作 ID；只有提供方已接入登录入口时才返回网页地址。当前 Linux CLI 不提供扫码或浏览器 Cookie 提取，需要已有有效授权凭据。完成授权后，凭据保存在系统密钥环；凭据文件仍由用户管理。百度使用 `--provider baidu` 和 `--kind refreshToken`。

`--via quark` 要求默认账号为就绪的夸克账号，且策略为 `cloud-only`。首个附件入队后，后台才确认领取能力；`transferAvailable:false` 可能表示尚未确认，不阻止首次入队。后台无法发送时保留待发状态或报告错误，必须查看指定接收方的实际回执。网盘领取时发送设备需要在线。

策略更改会影响 Linux 端后续发送；CLI 不会临时改动再恢复。需要恢复自动选择时执行 `mpt transfer cloud preferences --mode auto --json`。

## 回执与取消

```bash
mpt transfer receipts --item ITEM_ID --from DEVICE_ID --json
mpt transfer wait --item ITEM_ID --from DEVICE_ID --timeout 600 --json
mpt transfer cancel --item ITEM_ID --json
```

`--item` 可以重复。私聊默认等待原目标；共享项需要明确 `--from`，共享发送的 `--wait` 需要 `--receipt-from`。每条消息都必须有该设备保存内容后的真实回执才算等待成功；入队、上传完成和其他成员的回执均不够。等待通过 Runner 事件流唤醒。

超时退出码为 `3`，待发项继续由 Runner 管理。等待遇到失败、已取消或找不到的项时返回非零；参数错误为 `2`。当前后台检查返回最近 200 条项，更早的项可能无法通过此接口查询。

所有传输命令输出一个 JSON 对象，使用 `--json` 明确机器调用意图：

```json
{"ok":true,"command":"send","data":{"accepted":true,"itemIds":["ITEM_ID"],"targetDeviceId":"DEVICE_ID"},"error":null}
```

等待结果在 `data.items` 中返回各项的 `receiverDeviceId`、`confirmed`、`state` 和 `receipts`；发送同时等待时放在 `data.delivery` 中。超时 JSON 保留结果并返回 `error.code=receipt_timeout`，诊断写入 stderr，邀请和凭据内容不会写入输出。

## 接入 Agent MCP

安装脚本的 `--with-mcp` 会创建安装目录内的独立 Python 环境、复制 MCP 服务并打印 Codex 注册命令。也可以在仓库根目录手动创建独立 Python 环境：

```bash
python3 -m venv "$HOME/.local/share/MyPowerTools/transfer-mcp-venv"
"$HOME/.local/share/MyPowerTools/transfer-mcp-venv/bin/pip" install \
  -r integrations/file-transfer-mcp/requirements.txt
```

在支持 stdio MCP 的客户端配置下列服务器，将示例中的用户目录和仓库目录替换为实际绝对路径：

```json
{
  "mcpServers": {
    "mpt-transfer": {
      "command": "/home/your-user/.local/share/MyPowerTools/transfer-mcp-venv/bin/python",
      "args": ["/path/to/MyPowerTools/integrations/file-transfer-mcp/server.py"],
      "env": {
        "MPT_COMMAND_JSON": "[\"/home/your-user/.local/bin/mpt\"]"
      }
    }
  }
}
```

使用安装脚本生成的 `mpt` 包装命令时无需另配数据目录。使用其他 CLI 启动命令时，`MPT_COMMAND_JSON` 是命令参数的 JSON 数组，例如 `["dotnet","/installed/CLI/MyPowerTools.Cli.dll"]`，并设置对应的 `MPT_DATA_ROOT` 与 `MPT_ENDPOINT_ADDRESS`。

主要工具为 `mpt_status`、`mpt_devices`、`mpt_conversations`、`mpt_send_files`、`mpt_send_shared`、`mpt_receipts`、`mpt_wait_receipts` 和 `mpt_cancel`。私聊调用 `mpt_send_files(device, files, text)`；公屏调用 `mpt_send_shared(files, text, receipt_from, via)`，默认等待回执，公屏必须提供 `receipt_from`。两类发送默认等待 600 秒，可调整 `timeout_seconds`。

`mpt_pair_device` 和 `mpt_join_shared` 接收私密邀请文件路径。网盘管理使用 `mpt_cloud_accounts`、`mpt_cloud_authorize`、`mpt_cloud_complete` 和 `mpt_cloud_select`；后者会持久修改默认账号与策略。MCP 超时作为工具错误返回完整 CLI JSON，保留待发 ID，后续可继续查询回执；终止 MCP 调用只停止当前 CLI 客户端，已接受消息仍由 Runner 处理。

### Windows 部署验收（2026-10-05）

LIS-IMAC 已安装 Windows CLI 和独立 MCP 环境，并注册至该用户 Codex。实际通过配置中的 `pythonw.exe` 建立 stdio MCP 连接，发现全部 14 个工具；MCP 回归测试 7/7 通过，包括 Windows 隐藏 CLI 子进程。

传输业务验收尚未通过：部署前现有 Runner 的 `file-transfer` 已被软隔离熔断，错误为模块清理发生空引用异常且要求重启 Runner。`mpt_devices` 经 MCP 返回这一真实错误。当前用户桌面会话处于断开状态，交互式恢复任务返回 `0x800710E0`，未能恢复；临时任务已删除。Shell 和 Runner 原进程保持运行，未替换插件或打开桌面窗口。需要在可用的用户会话中恢复开发版 Runner 后重新验证发送和接收回执，不能把工具发现成功视为文件传输验收。
