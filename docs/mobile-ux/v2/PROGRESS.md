# V2 实施与验收记录

2026-09-28。此记录区分代码实现、自动测试和实际界面操作；没有列为通过的用例继续待验收。

## 已推送的实现

- `076cbea`：功能、UI、实现与验收规格，以及交互页面稿。
- `b6c472f`：产品/实现 ID 去重、收藏兼容、真实手机能力、中文状态、电脑目标选择、详情隐藏底栏。
- `59c4c29`：紧凑消息与文件行、单一发送按钮、明确目标、配对预览、草稿/附件/目标持久化、系统图片过滤。
- `447304d`：设备发现诊断与日常会话状态分离。默认目标名称为“文件传输助手”，未改已有发送协议。
- `bb7253c`：Android 软键盘避让，已重装并通过真实键盘输入/发送复验。
- `3dbd816`：永久投递错误等待手动重试，失败任务可取消，详情提供明确操作建议。
- `3c29627`：接收文件页直接展示普通文件互传码入口。

## 已完成的自动检查

| 范围 | 结果 | 证据 |
| --- | --- | --- |
| 移动目录与导航 | 38/38 | `/mnt/cache/data-cache/mpt-mobile-catalog-tests.log` |
| 共享 Shell 产品与设备服务 | 54/54 | `/mnt/cache/data-cache/mpt-catalog-shared-tests.log` |
| 文件助手 Surface（含失败恢复与接收码入口） | 141/141 | `/mnt/cache/data-cache/mpt-receive-code-tests.log` |
| FileTransfer.Core | 246 通过，3 跳过，0 失败 | DSH `047b4fb9-40d1-4aa7-afe8-bdd4639c5b29`；主代理已审查代码，并重跑错误文案相关 2 项回归 |
| Android 开发构建 | 成功 | `/mnt/cache/data-cache/mpt-v2-code-android-build.log`；仍为 CoreCLR experimental preview |
| Windows 官方 Tools Dev 更新 | 成功启动 | 2026-09-28 16:29:32 UTC；完整安装目录启动，Input Monitor DLL 保持 69120 字节 |

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

## 干净配置与公网实际验收

专用 AVD 新建隔离用户 10（`MPT-Fresh-QA-20260928`），未复制 owner 0 数据。测试前暂时停止 owner 的 MPT 进程，保留全部数据。开启飞行模式并关闭 Wi-Fi 后，系统报告 `Active default network: none`，冷启动仍可打开主页、文件助手并生成完整普通配对二维码。码内直连地址为空、包含公网投递权限、不含自定义云配置；升级 APK 和重启进程后二维码逐字一致。二维码证据仅保存在本地测试目录，包含测试凭据，不应公开发布。

联网后通过 Windows 实际 UI 粘贴扫码所得内容，预览显示 `MPT 手机 phone-35363a66 · 允许文件互传`，确认后选择该设备；Windows 界面发送，Android 干净会话随后显示消息和 LIS-IMAC 已接收，Windows 显示对端已送达。Android 日志确认 `custom=false` 且 16:26:19 UTC 产生 `inbox.received`。证明无 Tailnet 直连候选时默认公网可投递；未以该结果代替 Tailnet 直连或全部离线队列验收。

新接收页的“显示我的文件互传码”按钮在 Android 实际界面可见且能打开码；Surface 141/141 通过。测试过程中曾短暂出现空白草稿未保存提示，收件后消失，原因仍在调查，不能当作传输失败或忽略。测试已切回 owner 0，并恢复飞行模式关闭、AndroidWifi 已连接；隔离用户 10 暂留作为后续验收实例，MPT 进程已停止。

另外，Windows UI 发出的 `MPT V2 Windows UI reply 1605` 已在 owner 0 手机会话中亲眼确认（`v2-root-windows-reply.png`）。

## 本轮仍待完成

Windows 文件打开后的查看器内容与返回；默认公网双向文件、共享会话、离线恢复、图片与系统分享；完整工具逐项任务；macOS 实机功耗与功能验收。尚未发布新的 APK Release，也未发送新的发布邮件。

最新 Windows Tools Dev 更新完成于 16:29:32 UTC，Input Monitor DLL 仍为 69120 字节；最终 APK 已安装到专用 AVD，owner 0 与用户 10 均保持独立数据。

## 离线接收与防重复提交

构建仍为 `3c29627`。Windows 实际 UI 向未运行的 Android 用户 10 连续发送 12 条文字，用户 10 启动后自动收齐，未点同步或再发消息推动队列。两个手机滚动画面合计展示 12 条“已接收”，电脑显示真实送达；只读辅助核对两端 12 个 itemId 一一对应。发送端第 12 条离线时仍停在“等待发送”，该显示缺陷正在修复，因此 A09 只将自动补收边界判通过。未覆盖批量文件。

A20 以 25ms 间隔对同一发送按钮调用两次 UIA Invoke，第二次被禁用按钮拒绝；手机和电脑各仅一条。另一次辅助样本也各一条。没有覆盖失败条目重复重试。主代理已逐张查看手机前后两段历史和 Windows/Android 双击结果截图。报告及截图位于 `artifacts/.tmp-android-verify/m7-device-v022/a09-a20-20260928/`，完整逐动作记录位于 `/mnt/cache/data-cache/mpt-a09-ui`。

随后主代理开始 A10 本机断网验收，尚未发送样本即遇到 LatinIME 全屏空白：系统报告 IME 遮挡 2272px、输入视图未显示，点击输入框后页面不可用。Back 一度恢复，重启输入法后仍留异常遮挡；保存证据后重启专用 AVD，未清除任何应用数据。该轮不记通过。空白草稿保存告警也再次出现，正在分别定位，不能直接把输入法异常归因于网络或产品。

重启 AVD 后先前输入的草稿完整恢复，未发现内容丢失。随后再次断网（`Active default network: none`），通过 UI 发送 `MPT-A10-offline-restart-20260928`，手机显示等待发送；强制关闭并离线重开，条目仍在。恢复网络后无需重试即自动送达，Windows UI 显示同文“已接收”。主代理已亲看 `v2-a10-queued.png`、`v2-a10-network-restored.png` 和 `/mnt/cache/data-cache/mpt-a10-windows-received.png`。A10 的文字排队/进程重启/自动续传边界通过；附件尚未覆盖，之前输入法异常仍单列待诊断。

两个修复已完成源码与回归：公网暂存/失败持久化后通知 UI，状态未变不重复通知，PublicInboxPairingTests 14/14通过；恢复草稿的延迟 TextChanged 不再把未修改内容误当编辑，Surface 149/149通过。两个事件回归在关闭通知时均失败，两个草稿加载回归在修复前均失败。正在部署后复验，尚未发布。

Windows `4d62fe9` 部署后已复验末条状态：18:07:33 UTC 完成官方 Tools Dev 更新，向仍停用的用户 10 只发 `MPT-A09-20260928-B7-refresh-fix`，未导航/再次发送/手动同步，最新行自动显示“已暂存，等待接收”。主代理亲看 `a09-a20-20260928/windows-refresh-fix.png`；辅助落盘同一条为 stored、回执为空。Input Monitor 大小和时间戳均未变。

系统分享实测从 Android Files 长按 beta.txt、追加选择 alpha.txt，经原生“Sharing 2 files”面板点击 MyPowerTools，两个附件进入同一未发送草稿，目标为“文件传输助手”。主代理亲看 `v2-share-mpt-settled.png`；没有自动发送。尚未覆盖文字与两个附件一起分享，草稿留待升级复验。

输入法问题进一步获得稳定对照：重启后硬键盘模式（show_ime_with_hard_keyboard=0）点输入框正常；保持焦点改为 1 并点同一输入框，键盘出现但遮住输入栏；Back 收起再打开后避让正常。`v2-ime-reboot-soft-settled.png` 与 `v2-ime-refocus.png` 均已亲看。此前不同阶段的 native dump 中 IME 可见/输入视图状态不同，不应合并为同一快照。修复补充无动画的原生 insets 变化通知，不替换 Avalonia listener、不轮询；Android 88/88 单测及 Compile 通过，完整 APK 正在构建，设备复验待完成。

无动画键盘切换已在完整 APK 通过原序列复验：setting=0 时点输入框，保持焦点改成 1，再点同一输入框，`v2-insets-switch-fixed.png` 中输入栏和发送按钮立即位于键盘上方，不再需要 Back/重新聚焦。实际输入 `MPT-insets-live-20260928` 并发送，键盘收起、布局恢复，手机显示送达。测试后设置恢复为 0。完整构建 0 错误、3 个既有警告，日志 `/mnt/cache/data-cache/mpt-v2-insets-android-build.log`。

升级复验另发现系统分享的两个附件草稿未恢复，目标也回到升级前的 LIS-IMAC，因此不能判草稿升级通过。只读核对分享缓存文件仍存在、持久偏好保存时间早于分享，排查重点是分享后的草稿没有成功保存，正在诊断。此次修复不宣称解决此前全白输入法画面的所有原因；可稳定复现的无动画避让缺陷已经修复。

草稿故障对照继续：包含 `4d62fe9` 和键盘修复的新 APK 也复现系统分享后未落盘；已排除仅旧版问题。有效临时诊断显示分享发生在 UI 线程，timer 也属于 UI dispatcher；18:28:37 UTC 连续排程 revision 1–3 后，10 秒内没有 tick/save-entry/write。由此排除错误 dispatcher，当前对照只将草稿防抖 timer 的 Background 优先级改成 Normal，尚待实际结果。诊断日志只含阶段、时间、线程、计数，不含草稿内容或凭据。之前一次误装旧输出目录 APK 不计入诊断结果。

Normal 单变量对照成功，尚未加入分享 await：18:36:33.305 scheduled，.570 tick，.572 write，.744 written，均在同一个 UI dispatcher。此前 Background 对照延长观察仍无 tick/write。当前 Android 主循环中 Background 防抖保存不推进是已验证的故障机制；修复改用 Normal 优先级，保留 250ms 内容防抖，没有增加轮询。正在撤下临时诊断并对最终构建做附件、未发送文字两种 force-stop 恢复验收。

最终无诊断构建已通过两项实际恢复：系统 Files 分享 beta.txt、alpha.txt 后直接 force-stop，重开恢复两个附件和默认目标；输入未发送文字 MPT-draft-timer-20260928-final，等待防抖后直接 force-stop，重开精确恢复文字及附件。未依赖 Back、Detach 或 Send 保存。主 agent 已查看 restore-files.png、restore-typed.png；证据与 DRAFT-RESTORE.md 位于 artifacts/.tmp-android-verify/m7-device-v022/a09-a20-20260928/。Surface 152/152 通过，临时诊断源码和设备日志已移除。Windows 官方 Tools 更新于18:37:58 UTC完成，Input Monitor 文件大小及时间戳不变。开始准备0.2.3/code5通用开发预览包；此处尚不代表发布完成。

## 0.2.3 开发预览发布

提交6a50682已推送；0.2.3/code5通用APK构建91秒、0错误、3个既有警告，包含arm64与x64。实际覆盖安装与冷启动后文字、两附件、默认目标保留，主agent亲看v023-upgrade-restored.png；UPGRADE-0.2.3.md记录版本及过程。GitHub预览版 https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/tag/android-v0.2.3-preview.1 已发布，非latest；公开链接重新下载成功，140669838字节，两ABI可读。已通过agently-mail向weather2020@qq.com提交发布通知，服务返回queued=true（不等同收件确认）。完整工具迁移、真实手机、macOS及所有布局矩阵仍未完成；发布说明已明确范围。
