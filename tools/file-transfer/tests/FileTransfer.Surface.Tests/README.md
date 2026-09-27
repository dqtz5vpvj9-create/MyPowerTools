# FileTransfer.Surface.Tests

`FileTransfer.Surface` 的行为测试：用脚本化的模块（记录每条命令并返回页面读取的状态）驱动真实控件树，
不需要真机、Tailscale 或 OpenList 服务。每个用例的数据目录在 `MPT_TEST_TEMP` 下创建，成功后自动删除，
失败时保留现场。

## 运行

```powershell
dotnet test tools/file-transfer/tests/FileTransfer.Surface.Tests/FileTransfer.Surface.Tests.csproj
```

环境变量（都可选）：

| 变量 | 作用 |
|---|---|
| `MPT_TEST_TEMP` | 测试数据根目录（默认系统临时目录）。CI/受限环境下指向可写的大盘目录，例如 `/mnt/cache/data-cache`。 |
| `MPT_FT_SHOTS` | 设置后额外保存 320/360/390 宽的页面截图到该目录，用于人工核对排版。 |

## 覆盖范围

- 320/360/390/768 宽：Button/TextBox/ComboBox 不越出视口，触控目标不小于 44。
- 手机首屏（320x900、360x800）：选文件、选设备、发送三个控件都在首屏内。测试宿主直接承载 Surface，
  断言扣除了 `SystemChromeAllowance = 88`（约 24 Android 状态栏 + 56 MPT 手机顶栏 + 8 导航提示）；
  真机顶栏更高时需同步调大该常数。
- 重开页面后已建立的 OpenList 专用账号不需要重新配置（直接 `openlist.connect`，不重发账号密码）。
- 手填 WebDAV 配置不会被本机 OpenList 顶掉；密码框留空时不下发 `password`。
- Android 分享：同一个 Surface 实例连续接收多次激活后仍保留全部文件；打开新页面时列表为空（无磁盘交接）。
- 失败/取消后可从历史或顶部重试；进度、取消、接收开关、云连接码导入走对应模块命令。
- 设备连接码与网盘连接码进错框时被拦截。
- 记忆设备：`lastPeer` 默认选中并标记 `✓`。
- 连接码预览：导入前显示解码后的设备名称/设备号/地址或网盘地址/账号，**令牌与密码不出现**。
- outbox 清理：移除或清空时回收自己暂存的副本；传输完成后回收已发送的暂存副本；页面销毁时回收；
  用户自选文件永不删除；传输进行中不删除暂存副本。

## 未覆盖（需要真机或宿主能力）

- 真实 Tailscale 直传、真实 OpenList 服务与国内网盘驱动登录。
- Android `OperatingSystem.IsAndroid()` 分支（桌面 OpenList 按钮隐藏、系统下载目录提示）。
- 文件选择器/文件夹选择器、剪贴板、拖放的平台实现。
