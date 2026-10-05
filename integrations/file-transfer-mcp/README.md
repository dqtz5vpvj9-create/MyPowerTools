# 文件传输 CLI 与 Agent MCP

Linux 使用已安装的 MyPowerTools Runner 持续接收、上传并保存待发队列。CLI 和 MCP 通过带认证的 HostControl 连接这个 Runner，沿用文件助手的身份、配对和回执。

## Windows 部署

复用当前用户已运行的开发版 Runner、设备身份和文件助手数据。无需启动第二个 Runner。
在仓库根目录使用隐藏 PowerShell 执行：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File scripts/install-windows-transfer-mcp.ps1 -RegisterCodex
```

需要 .NET 10 SDK、支持 fastmcp 的 Python、已运行的 MPT ServiceManager，以及可用的 Codex CLI。安装时会将现有 Runner 注册为 `mpt-runner.service`，由 ServiceManager 接管和恢复；不要求桌面连接。安装脚本构建 Windows CLI，在 `%LOCALAPPDATA%\Programs\MyPowerTools\TransferMcp` 创建独立 Python 环境，并生成 `mcp.json`，可供其他支持 stdio MCP 的 Agent 客户端导入。使用 `-CliDirectory` 可以指定预先发布的 Windows CLI 目录，该目录必须持久保留。

`-RegisterCodex` 注册名为 `mpt-file-transfer` 的服务器；已启动的 Agent 会话需重新加载 MCP 或新建会话。服务器使用 `pythonw.exe`，CLI 子进程设置 `CREATE_NO_WINDOW`。不会因每次工具调用而闪出控制台。Runner 必须由正常的开发版运行流程启动；模块被隔离时会返回真实错误，不另建空白设备身份。

## 安装 Linux 服务

需要 .NET 10 SDK、PowerShell 和 Python 3。运行时凭据使用服务自己的加密存储，不依赖桌面登录、Secret Service 或交互解锁。安装脚本使用 `/mnt/cache/data-cache` 暂存构建文件，该目录需可写。

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

授权入口返回操作 ID；只有提供方已接入登录入口时才返回网页地址。当前 Linux CLI 不提供扫码或浏览器 Cookie 提取，需要已有有效授权凭据。完成授权后，Linux 凭据保存在服务加密存储；凭据文件仍由用户管理。百度使用 `--provider baidu` 和 `--kind refreshToken`。

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

后续恢复已完成：旧 Runner 的平台接口为 0.2.0，缺少插件要求的 `IBackgroundActivityService`；初始化未执行时的清理又访问了未创建的 OpenList 对象。已同步更新 Runner 和插件，修复未初始化实例的清理，并在 Tools 开发更新中依据依赖版本阻止新插件覆盖旧宿主。

现有 ServiceManager 已接管 `mpt-runner.service`。桌面保持断开，Windows MCP 向 Linux 公屏发送文字和 106 字节文件，两项均收到 `pc-fd7f85a2` 的真实保存回执，文件与源文件逐字节一致。主动终止 Runner 后，ServiceManager 自动重启，设备查询恢复；Shell PID 保持不变。Session 0 调用 `start-user-runtime.ps1 -StartRunner` 通过 ServiceManager 返回现有实例，未启动重复进程。

这使用 MPT 现有的用户级 ServiceManager，不是新增 LocalSystem 服务；本次验证了桌面断开和 Runner 崩溃恢复，未验证整机重启或用户注销后的自动启动。文件传输目前仍是 Runner 内的插件，独立于 Shell，但尚未拆分为独立的文件传输 worker。

Windows 后台运维命令（可从 SSH / Session 0 执行，不用重连桌面）：

```powershell
& "$env:LOCALAPPDATA\Programs\MyPowerTools\Cli\MyPowerTools.Cli.exe" service status mpt-runner.service
& "$env:LOCALAPPDATA\Programs\MyPowerTools\Cli\MyPowerTools.Cli.exe" service restart mpt-runner.service
```

`start-user-runtime.ps1 -StartRunner` 在 Session 0 通过现有 ServiceManager 启动已注册服务；`-StartShell` 仍要求交互会话。开发更新会先显式停止受托管的 Runner，避免更换组件时被崩溃恢复逻辑抢先拉起。

本次补充验收：清理回归修复前稳定触发空引用，修复后通过；隔离网络中的核心测试 387 通过、3 个外部夹具跳过。普通主机网络首次测试有 2 个测试服务器端口占用失败，隔离网络重跑全部通过。

Linux 旧版重启后因桌面 Secret Service 锁定而阻塞。新版改用服务凭据存储；在桌面密钥环仍锁定的条件下，重启后 6.61 秒设备查询成功。之前积压的 Linux→Windows 文字和 106 字节文件均收到 LIS-IMAC 和手机的保存回执。恢复保留设备身份、历史和原公屏，通过现有设备导出的授权恢复公屏密钥，从本机托管 OpenList 恢复网盘授权；旧锁定密钥环未解密、未删除。本机接收令牌重新生成，OpenList 本机管理员密码已轮换。

### Linux 服务凭据与旧版迁移

默认目录为 `~/.local/share/MyPowerTools/secrets`，可通过 `MPT_SECRET_VAULT_ROOT` 指定。目录权限为 0700，密钥及密文文件为 0600，内容使用 AES-GCM 加密。密钥保存在同一用户的私有目录中，因此保护边界是系统用户权限；这不是硬件密钥或额外密码保护。备份和恢复时必须同时保留 `master.key` 和密文文件，缺失密钥时服务不会覆盖已有密文。

旧版迁移使用 `scripts/migrate-linux-secrets.py`，只读取既有 MPT 条目并通过标准输入导入，既不打印凭据，也不删除原条目。迁移需要 Python 的 PyGObject/Gio 和可读取的旧密钥环；锁定时会停止升级并保留旧服务，不弹出解锁窗口或悄悄生成新身份。已有授权也可以通过 `mpt secrets import --stdin` 导入 JSON 数组（`moduleId`、`name`、`value`），不同值不会覆盖已有凭据。正常运行和后续重启不再调用桌面密钥环。
