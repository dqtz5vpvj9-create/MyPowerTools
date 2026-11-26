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
- 命令响应竞态：小文件传输的终态事件先于命令响应到达时，页面不回到「正在传输」，取消按钮恢复禁用，
  紧接着能再次发送；失败事件先到时「重试上次」仍可见可用（命令被拒绝时才按 `inspect` 真实状态回滚）。
- 设备连接码与网盘连接码进错框时被拦截。
- 记忆设备：`lastPeer` 默认选中并标记 `✓`。
- 连接码预览：导入前显示解码后的设备名称/设备号/地址或网盘地址/账号，**令牌与密码不出现**。
- outbox 清理：移除或清空时回收自己暂存的副本；传输完成后回收已发送的暂存副本；页面销毁时回收；
  用户自选文件永不删除；传输进行中不删除暂存副本。

## 未覆盖（需要真机或宿主能力）

- 真实 Tailscale 直传、真实 OpenList 服务与国内网盘驱动登录。
- Android `OperatingSystem.IsAndroid()` 分支（桌面 OpenList 按钮隐藏、系统下载目录提示）。
- 文件选择器/文件夹选择器、剪贴板、拖放的平台实现。

## 网盘账号页面

`CloudAccountsTests` 用脚本化模块驱动实际控件，覆盖原生登录结果只转交一次、返回时取消并丢弃迟到凭据、不可用提供方说明、真实命令参数、暂停/断开、目录 ID 选择、敏感错误不进入页面，以及 320/390/768 宽的布局。准备事件用独立状态读取更新页面，不等待后台账号写锁。会话入口测试确认返回后保留原输入框和草稿，并解除账号事件订阅。

只运行这组测试可加 `--filter FullyQualifiedName~CloudAccountsTests`。设置 `MPT_CLOUD_SHOTS` 可保存 Avalonia headless 控件截图；它们使用脚本化账号状态，不能代替真实平台登录或网盘传输验收。未知容量显示未知，安全清理未实现时不发删除命令，账号就绪也不等于文件领取通道已经就绪。
