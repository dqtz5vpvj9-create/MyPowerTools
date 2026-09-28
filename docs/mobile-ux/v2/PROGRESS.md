# V2 实施与验收记录

2026-09-28。此记录区分代码实现、自动测试和实际界面操作；没有列为通过的用例继续待验收。

## 已推送的实现

- `076cbea`：功能、UI、实现与验收规格，以及交互页面稿。
- `b6c472f`：产品/实现 ID 去重、收藏兼容、真实手机能力、中文状态、电脑目标选择、详情隐藏底栏。
- `59c4c29`：紧凑消息与文件行、单一发送按钮、明确目标、配对预览、草稿/附件/目标持久化、系统图片过滤。
- `447304d`：设备发现诊断与日常会话状态分离。默认目标名称为“文件传输助手”，未改已有发送协议。
- `bb7253c`：Android 软键盘避让，已重装并通过真实键盘输入/发送复验。

## 已完成的自动检查

| 范围 | 结果 | 证据 |
| --- | --- | --- |
| 移动目录与导航 | 38/38 | `/mnt/cache/data-cache/mpt-mobile-catalog-tests.log` |
| 共享 Shell 产品与设备服务 | 54/54 | `/mnt/cache/data-cache/mpt-catalog-shared-tests.log` |
| 文件助手 Surface（含发现诊断回归） | 135/135 | `/mnt/cache/data-cache/mpt-v2-discovery-tests.log` |
| FileTransfer.Core | 244 通过，3 跳过，0 失败 | DSH `1f45be05-526a-4fce-93f7-97fa9344773c` 的测试结果；主代理已审查持久化与授权目标代码 |
| Android 开发构建 | 成功 | `/mnt/cache/data-cache/mpt-v2-android-build.log`；仍为 CoreCLR experimental preview |
| Windows 官方 Tools Dev 更新 | 成功启动 | 2026-09-28 15:20:42 UTC；完整安装目录 Shell PID 1091112，Input Monitor DLL 保持 69120 字节 |

主代理逐张看过 320/390 宽度的文字和文件截图。此前发现的文件裁切及文字布局问题已修复。截图不是双端功能验收。

## 实际界面操作记录

Android 设备为专用测试 AVD `127.0.0.1:15683`；Windows 为 LIS-IMAC 的官方 Dev。保留升级配置、配对和历史；AVD 原有自定义 WebDAV 测试地址仍在，不能用该配置证明首次安装的默认公网行为。

主代理通过 AutoDroid MCP 操作手机界面，未通过后台接口代发：

- 选中已配对 LIS-IMAC；选择只改变目标，不发送。
- 输入并发送 `MPT V2 UI acceptance 0928-1515`；手机后来显示 LIS-IMAC 已送达。
- 从系统文件选择器选 `beta.txt`，返回后保留草稿附件，点发送；手机后来显示已送达。
- 从会话打开已接收文件，系统查看器显示文本，返回到原会话；输入框仍能接受输入。
- 写入未发送草稿 `DraftV2-restore`，随后安装最终诊断修复构建；重启后草稿与 LIS-IMAC 目标均恢复，截图 `v2-root-restored.png`。

这些步骤的截图与界面树在 `artifacts/.tmp-android-verify/m7-device-v022/v2-root-*`。电脑端已通过 UI 查找并确认收件，点击 beta.txt 后默认 EmEditor 打开了精确的收件文件，已通过官方 Launcher 返回 MPT。查看器标题和状态栏显示 beta.txt、10 字节、2 行；UIA 正文为空，PrintWindow 也未显示正文，故可见内容仍未判通过。独立只读落盘核对为 `beta file\n`（10 字节）。证据 `/mnt/cache/data-cache/mpt-windows-viewer-probe/`。

- 图片入口实际进入“Recent images / Images in Pictures”过滤视图；选择测试图片后，草稿显示真实缩略图、名称、大小，没有自动发送。再点击发送，手机显示已送达。截图 `v2-root-photo-picker.png`、`v2-root-photo-draft.png`、`v2-root-photo-sent.png`。
- 远程通知从工具库打开到 Android 对应页面，未重复显示桌面入口；缺少签名密钥时给出配置提示。仅证明导航和未配置状态，尚未验证真实通知收取。

## 实际验收发现及修复

- 打开 Android 真实软键盘后发现输入栏被遮挡（`v2-root-soft-keyboard.png`）。现已由 Android 根容器消费 IME 遮挡高度，保留系统栏安全区域；Android 单测 87/87、APK 构建通过。重装后主代理亲眼检查 `v2-root-ime-fixed.png`，输入框和发送按钮均在键盘上方。实际追加文字、点发送后显示已送达，键盘收起且布局恢复（`v2-root-ime-sent.png`）。AVD 的 `show_ime_with_hard_keyboard` 已恢复原值 0。
- Windows 最终 Dev 进程有主窗口，但处于隐藏状态；通过完整安装目录的官方 Launcher 恢复后，同一窗口正常显示和响应。初始隐藏原因尚未确定。主代理随后通过实际侧栏打开文件互传，并查看窗口截图 `/mnt/cache/data-cache/mpt-v2-windows-chat.png`，确认手机发送的文字、beta.txt 和键盘测试文字均显示为已接收。

## 故障路径补修

DSH `047b4fb9-40d1-4aa7-afe8-bdd4639c5b29` 修复永久投递失败仍被自动重传的问题：永久失败等待手动重试，临时网络/中转错误仍自动退避恢复，重试保持原 itemId。Core 全套 246 通过、3 跳过；主代理补齐文件副本丢失的明确错误文案后，两个相关回归再次通过（`/mnt/cache/data-cache/mpt-v2-inbox-final-tests.log`）。Surface 增加失败条目取消、明确错误原因及输入框无障碍名称；140/140 测试通过，名称断言另已通过。以上故障用例为自动测试，尚未记作真实双端故障验收。

## 本轮仍待完成

Windows 文件打开后的查看器内容与返回；干净配置默认公网路径、共享会话、离线恢复、图片与系统分享；完整工具逐项任务；macOS 实机功耗与功能验收。尚未发布新的 APK Release，也未发送新的发布邮件。
