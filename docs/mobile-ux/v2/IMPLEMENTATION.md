# 实现设计与任务拆分

## 1. 保留的架构

Android 宿主 MainActivity/MptAndroidApplication → 现有 MobileShellView → 工具产品目录与设备服务 → 既有 Module/Surface/Command/Event/SecretStore。Windows/macOS 继续使用各自宿主，工具和协议共享。禁止重新造一个独立文件应用、第二个权限系统或直接从 UI 绕过模块写传输文件。

文件助手沿用 FileTransfer.Core 的 AssistantStore、普通 Pairing 与自己的 LinkCode；沿用 FileTransferModule 的持久队列、公网 Inbox、共享会话 DAV、直传和可选 OpenList。Surface 负责显示与交互；传输不依赖页面存活。

## 2. 文件助手模型与命令接线

| UI 概念 | 模型/来源 | 接线与约束 |
| --- | --- | --- |
| 当前目标 | Self 或 PairedDevice(deviceId,name) | 不用 IP 作主键；只有发送快照读取目标，异步过程中不受后来选择影响 |
| 草稿 | text + 已准备附件路径列表 + target | 选择器取消不清除；发送接受才移除提交部分；后台失败不丢消息 |
| 记录 | assistant.inspect.items | 正序显示，稳定 itemId；新增、状态、回执变化区分处理 |
| 已添加设备 | legacy.inspect.peers + 已授权会话成员 | 先显示缓存，再 assistant.devices 增量补充；未知可达性仍可发送 |
| 发送 | assistant.send(text,paths,targetDeviceId?) | Self 省略目标，普通设备明确传稳定 ID；accepted 不等于 delivered |
| 重试/取消/打开 | assistant.retry/cancel/open(itemId) | 成功回调后刷新；不能只 toast 不执行；打开走宿主文件服务 |
| 普通配对 | pairing / pair.preview / pair.import | 本地导出、预览无秘密输出、确认后导入；不共享自己的会话 |
| 自己的设备 | assistant.link.export/preview/import | 共享范围预览与用户确认后才导入 |
| 更新 | file-transfer.assistant.changed | 页面订阅、离开退订；回前台读一次快照，不每秒刷新全列表 |
| 高级配置 | 既有 settings/网盘命令 | 保留旧配置；普通页不展示接口原始错误 |

目标状态保留在 Surface 会话状态，并以现有设置服务持久化最后选择的稳定 ID；保存不能写配对密钥。如果目标已被移除，恢复为 Self 并清楚提示，不能静默把尚未提交的定向草稿发到文件传输助手。M1 若尚未完成跨进程草稿恢复，要在验收表明示失败，不能称持久草稿已完成。

消息组件使用轻量行：正文/文件内容、时间与状态、更多菜单。不再嵌外层 Card、内层 Inset、独占操作行。列表项按 ID 更新；必须保留当前阅读位置，必要时记录首个可见 ID 与偏移。接近底部时跟随布局变化；在历史位置时展示“新消息”按钮，点击才滚到底部。

## 3. 发送和路径生命周期

1. UI 校验内容和目标授权，调用模块；模块先持久化消息与有效附件，再返回 accepted。
2. UI 立即显示排队记录，清除已提交草稿。网络不可用不使本地提交失败。
3. 模块分别调度可用直传与公网投递；任何一条慢路径不得阻塞另一条。普通配对使用 deposit inbox，自己的会话使用原共享存储。
4. 公网接受更新 stored；目标落盘完成后才写 receipt；发送端验证相应 itemId/目标的回执再更新 delivered。
5. 暂时错误按既有有界退避；永久授权错误停止无意义自动尝试并等待用户修复。取消传播到实际 I/O。
6. 重启加载同一 itemId，不重复制造消息；回执批次剩余项继续调度；全部完成后等待事件。

不新增内容散列机制。已有幂等 ID、存储事务、权限校验、重定向限制继续使用。单元测试与真实文件内容比对用于发现错误，不能拿 HTTP 200 充当保存成功。

## 4. 产品目录和平台能力修正

产品 ID 和实现 ID 分开。至少明确以下映射，避免同名重复：

| 产品 ID | Android 实现 ID | 电脑实现 |
| --- | --- | --- |
| file-transfer | file-transfer | file-transfer |
| remote-notifications | remote-notifications-android | remote-notifications |
| remote-commands | remote-commands-android | remote-commands |

目录条目存在只证明已索引，不能证明能在本机执行。计算能力必须联合实际平台、availability、state、可加载 Surface/命令以及宿主限制。unsupported 不能被 Available 与 CanOpen=true 掩盖。电脑 metadata 不能因出现在 Android bundle 而被标为手机本地工具。

MobileToolEntry 应保留 ProductId、ImplementationId、ExecutionLocation、Availability 和可打开理由；显示/收藏/搜索按 ProductId，实际调用按 ImplementationId，目标电脑通过独立连接标识传递。兼容旧收藏 ID，迁移时去重而不丢收藏。

状态映射在展示服务统一完成：目录、运行、任务结果各用自己的映射，禁止把未知字符串原样透传。需要电脑的条目点击进入选择授权目标的页面；未实现的远程适配不得返回假成功。

## 5. 代码归属

| 任务 | 主要文件 | 交付内容 |
| --- | --- | --- |
| T01 文件助手显示 | tools/file-transfer/src/FileTransfer.Surface/AssistantView.cs、AssistantModels.cs、相关 partial 与 AssistantCore.cs | 紧凑消息、统一输入、明确目标、缓存设备、状态文案 |
| T02 移动壳 | src/MyPowerTools.Shell.Avalonia/Views/MobileShellView.cs 与 Views/Mobile | 详情导航、隐藏底栏、键盘安全区、主页和工具列表 |
| T03 目录与状态 | Services/Mobile/MobileToolCatalog.cs、ShellToolProductService、MobileHome/Activity ViewModels | 产品/实现映射、真实能力、自然语言状态、收藏迁移 |
| T04 文件与生命周期 | src/MyPowerTools.Android/Files、Input、MainActivity | 系统选择/分享/打开、返回触摸；保留既有修复 |
| T05 传输缺陷 | FileTransfer.Core / FileTransfer.MyPowerTools | 只修真实端到端发现的协议/持久队列问题，保持 API 兼容 |
| T06 已有手机工具 | remote-notifications-android / remote-commands-android 的 Surface | 正确接线、列表/详情/设置、权限反馈和执行结果 |
| T07 电脑工具 | MobileControlDeviceService、REMOTE_CONTROL_CONTRACT.md 与工具适配 | 独立控制授权、目标能力目录、命令结果、移动表单 |
| T08 验收与发布 | tests/MobileLayout、FileTransfer.Surface、Android host tests、设备证据 | 每个用户旅程从界面完整跑通，构建、Dev 启动、GitHub 发布 |

写入范围不得重叠；并发 UI 修改必须由一个负责人整合。禁止用后台 HostControl 命令代替验收中的界面发送/配对；HostControl 可只读核验收件落盘状态。

## 6. 实施顺序与退出条件

| 阶段 | 依赖 | 必须交付 | 才能进入下一阶段的证据 |
| --- | --- | --- | --- |
| S0 规格与页面稿 | 当前源代码与实际截图 | 本目录 4 份规格、交互页面稿、需求编号 | 页面可打开、320/390/桌面截图已查看；按钮与状态都在文档中定义 |
| S1 目录与基本布局 | S0 | 产品去重、状态翻译、详情导航、紧凑聊天、一个发送按钮 | 干净配置的首页和工具库无内部枚举；真实 APK 输入/返回可用 |
| S2 完整传输旅程 | S1 | 首次添加→选文件→明确目标→发送→另一端找到→打开→返回 | A01–A16 真正从双端 UI 完成；离线恢复无需人工同步 |
| S3 手机工具接线 | S1 | 通知、命令等已有模块正常打开并完成任务 | 每项工具的操作与实际结果匹配，不以同名占位代替 |
| S4 电脑工具迁移 | S3 + 控制协议 | 对清单每一项提供真实移动操作页和权限反馈 | 逐工具验收；本地不可实现者通过授权电脑完成，未支持项列缺口 |
| S5 跨平台发布 | 所发布范围对应验收通过 | Windows Dev、Android 版本/APK、源代码、验收记录 | 推送成功、发布下载可用；macOS 未达功耗门槛只可明确标注开发包 |

用户已经授权按规格实现，S0 完成后直接进入 S1，不把再次确认变成前置门槛。发现文档与真实能力冲突时先记录具体差异并修订，不能悄悄降低验收标准。

## 7. 迁移、隔离与验证

保留已配对设备、密钥、自定义 OpenList、历史文件及原有 Windows 组件。旧设置按稳定 ID 兼容，不能为了好看删除用户配置或清空数据。测试使用独立配置；演示数据只能存在于页面稿。

测试覆盖真正故障：目标选择改变 command 参数、缓存设备无需网络即显示、不同实现 ID 去重、unsupported 不可本地运行、草稿取消保留、消息阅读位置、键盘避让、文件查看器返回。不要写仅复述 getter 的测试充数量。

Android 日常验证用 APK 安装；Windows 通过官方 Start-MyPowerTools-Dev.ps1 更新并启动，不能直接跑不完整 build/bin。本次保证 Input Monitor 固定修复不被旧模块覆盖。所有新 artifacts 路径遵守 scripts/artifacts-policy.json；临时材料使用 /mnt/cache/data-cache。

## 8. 本轮源代码审查补充事实

- Android 实际启动链为 MptAndroidApplication → MainActivity → AndroidHost → MptHostRuntime → InProcDotNetModuleHost；HostControlClient.EmbeddedInvoker 接到同一 HostControlGrpcService。
- 手机目前打包的本地业务模块是 file-transfer、remote-notifications-android、remote-commands-android、mobile-tool-control。进程监测没有 Android 系统枚举实现，不能按定义表标为本机可运行。
- 现有电脑控制通过 mobile-tool-control.import.preview/confirm、devices.list/check/remove、catalog、invoke、invocation.status、invoke.cancel；当前网关地址与绑定限制为 Tailnet。这是控制通道现有限制，不得拿来限制文件身份或普通文件发送；公网控制扩展需独立维护同等授权边界，不混入文件公网 inbox。
- 现有详情页只选第一台已导入电脑，应改显式选择目标。统一扫码入口应按 mpt://pair、mpt://assistant、mpt://control 分流到相应预览，不能全部交文件导入。
- OpenList 服务运行由电脑承载，Android 使用客户端；移动 UI 可管理相应配置，但不得声称已经在手机内运行服务器。
- 文件照片入口目前只有标题区别，应实际设置图片 MIME/扩展过滤。远程通知与 SSH 的平台模块已经存在，先修产品 ID 映射和可运行性判定，不能重写一套。
