# 文件互传

## Windows 开发版与 e2e 回归

使用 `ci/both-installers` 的匹配宿主：当前工具需要该分支的 Platform 0.3.0、会话、二维码与移动界面 SDK。首次准备本地 SDK 时执行 `dotnet pack src/MyPowerTools.Platform.Abstractions -o artifacts/sdk/nuget`。先从仓库运行 `scripts/Start-MyPowerTools-Dev.ps1` 更新宿主，再运行 `scripts/Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId file-transfer` 更新工具，两步都会从完整安装目录启动开发版。

使用 tester-army/e2e 运行 Windows 传输回归：

```powershell
npm ci
dotnet build tools/file-transfer/tests/FileTransfer.Core.Tests/FileTransfer.Core.Tests.csproj -c Debug
dotnet build tests/FileTransfer.Surface.Tests/FileTransfer.Surface.Tests.csproj -c Debug
dotnet build src/MyPowerTools.Cli/MyPowerTools.Cli.csproj -c Debug
npx e2e run tests/e2e/file-transfer.e2e.ts
```

七组用例覆盖真实 TCP 直传、真实 Python 中转、无 Tailscale 的双模块自动收件与回执、共享附件回退与重启、会话队列与失败重试、Avalonia 真实控件交互及草稿，以及已安装开发版的程序集与 Runner 命令。报告写入 `.e2e/file-transfer`。测试使用短临时路径和隐藏子进程，既保留 Windows 的真实刷盘行为，也避免测试路径超过旧版 Python 的长度限制。浏览器和手机引擎没有 Windows Avalonia 桌面驱动，因此 e2e 通过原生测试宿主和生产模块执行这些流程；本轮没有进行手机真机或真实网盘账号登录验收。

2026-10-04 修复：Windows 中转上传刷盘使用可写句柄；无效或不完整的模块响应会显示错误并保留现有会话。现有会话界面测试位于 `tests/FileTransfer.Surface.Tests`；工具目录中的旧 Surface 用例仍针对先前的经典页面入口。

文件互传是 MPT 模块，使用现有命令、事件、平台密钥存储和 Avalonia Surface。Windows、macOS 与 Android 共用传输协议和页面；Android 接收完成后通过平台接口把文件发布到系统下载目录。

## 使用

两台设备连接同一 Tailscale 网络。在接收设备打开「文件互传」，点击「开启接收」并复制本机连接码；发送设备导入一次，之后选择文件和已保存设备即可发送。支持多选和桌面拖放，Android 可从系统分享菜单进入。接收目录有同名文件时另存为新名称。

接收方只有在文件成功发布到系统下载目录（Android 上即用户能真正取走文件）之后才确认成功。发布失败会明确向发送方报错，完整文件仍保留在收件文件夹，并在下次开启接收或重启应用时自动重试发布；发送方重试会另存为新名称，不会覆盖已收到的文件。

网盘中转在一台常在线的 Windows 或 Mac 上点击「一键启用 OpenList」。MPT 下载并管理官方 OpenList v4.2.6，打开管理页并复制随机管理员密码。登录后添加自己的网盘，在网盘中建立互传目录，将对应的 `http://Tailscale-IP:15244/dav/网盘/互传目录` 填回 MPT，点击「保存并测试」。MPT 会自动建立只允许此目录上传、建目录和 WebDAV 读写的专用账号。账号复用只依据本机保存的明确身份：账号编号与用户名都与远端精确匹配，或本机保存的用户名与远端精确匹配（用于服务端重新编号后找回）；身份不明确时新建一个随机专用账号，绝不按 `mpt-` 前缀认领或改动其它 MPT 设备的账号。随后复制网盘连接码给其他 MPT 设备，免去重复填写服务器和账号。已有 OpenList 也可直接填写 WebDAV 地址与具备上述权限的账号。

连接码包含接收权限或网盘账号，只应交给自己的设备。密钥保存在 MPT 平台密钥存储，命令日志会隐藏凭据和连接码。直传只监听 Tailscale 地址；OpenList 明文 HTTP 仅允许 Tailscale 或回环地址，外部服务必须 HTTPS。网盘下载重定向不携带 OpenList 账号，重定向只允许 HTTPS，或指向同一台已校验中继主机的明文 HTTP 地址。

网盘上传完成后发布收件记录，接收方点击刷新即可下载；半途取消的上传不会出现在收件箱。网盘中的传输目录保留原始文件，需从 OpenList 管理页按需清理。OpenList 仅在用户启用时启动，工具卸载时停止自己启动的进程。

## 批量与状态

一次选择多个文件时按顺序逐个发送：某个文件失败只记录该文件的失败原因，其余文件继续发送，结束时汇总成功/取消/失败数量。传输历史（最近 50 条）与事件序号持久化在模块数据目录的 `history.json`，重启后仍然可见且订阅事件不会丢；应用退出时仍在进行的传输会在下次启动标记为「上次传输未完成」。接收启动时清理遗留的 `.mpt-*.part` 半成品文件，Android 分享暂存目录中超过 24 小时的残留也会删除。设置只接受已知键（`deviceId`、`receiveDirectory`、`listenAddress`、`peerAddress`、`webDavUrl`、`username`、`recipient`、`maxReceiveGiB`）；密码与接收密钥只写入平台密钥存储，不写入 `preferences.json`。接收期间持有后台租约，监听器异常退出会立即释放租约并记录失败原因。

`preferences.json`、`history.json`、`publish-pending.json`、`relay-account.json` 一旦损坏，工具不会用默认值覆盖或静默重置：它保留原文件并报出文件路径，提示修复或重命名后重新加载。

## 构建与验证

先按主仓库说明构建 SDK，然后执行：

```powershell
pwsh -File tools/file-transfer/build.ps1
dotnet test tools/file-transfer/tests/FileTransfer.Core.Tests/FileTransfer.Core.Tests.csproj
```

真实 OpenList 集成测试需要设置 `MPT_OPENLIST_TEST_BINARY` 为官方 v4.2.6 可执行文件路径；未设置时该用例明确跳过。测试会启动自己的 15244 端口服务，建立独立临时目录和 Local 挂载，完成管理员初始化、受限账号创建/更新、上传、收件列表、下载和停止。可用 `MPT_TEST_TEMP` 指定测试临时目录。

测试覆盖：接收密钥错误、路径穿越、大小限制、断线、取消、同名文件、发布成功后才确认（发布异常必须 NACK 且下一个文件仍可接收）、遗留半成品清理、畸形握手不终止监听、监听器丢失上报；批量失败隔离与取消计数；历史/序号持久化、上限与损坏文件保留报错；待发布队列去重、上限与损坏保留报错；网盘重定向同主机明文可跟随且不带账号、外部明文主机被拒绝；设置键白名单与秘密不落盘、后台租约获取/释放、中断传输上报、损坏设置保留原文件并明确失败；多 MPT 专用账号在不明确身份时不被认领（单元 + 官方 OpenList 实测：新建随机账号且其它账号密码不变）。Local 挂载验证的是 OpenList 接口闭环；具体国内网盘仍需要用户完成对应驱动登录后验收。现有 Windows 验收机未连接 Tailscale，跨真实设备的 Tailnet 直传与 macOS 实机验收另行记录。

## Android 数据面验收 fixture（仅测试，无生产代码改动）

`tests/android-dataplane-fixture.sh` 用官方 OpenList v4.2.6 在 `127.0.0.1` 起一个隔离实例（Local 驱动挂载到 `<root>/storage`），通过生产 helper 建立一次性专用账号，写入一条可下载的来件，并打印交给 Android 设备的 WebDAV 地址、`mpt://cloud/...` 连接码、`mpt://pair/...` 设备码与逐字节校验命令。设备侧只需 `adb -s <serial> reverse tcp:<port> tcp:<port>` 即可用手机本地回环访问；不含真实网盘凭据、不涉及 Tailscale、不改动任何设备。

```bash
bash tools/file-transfer/tests/android-dataplane-fixture.sh start   # 启动 + 准备 + 打印 handover
bash tools/file-transfer/tests/android-dataplane-fixture.sh status
bash tools/file-transfer/tests/android-dataplane-fixture.sh stop [--purge]
```

状态目录默认 `/mnt/cache/data-cache/mpt-file-transfer/android-fixture`，端口默认 15244；端口被占用时直接报错并提示改用 `MPT_FIXTURE_PORT`，不会盲目再起一个服务。准备用例默认跳过（需 `MPT_FIXTURE_ROOT` + `MPT_FIXTURE_ADMIN_PASSWORD`，脚本会设置），因此常规 `dotnet test` 仍是 55 通过 + 1 跳过。
