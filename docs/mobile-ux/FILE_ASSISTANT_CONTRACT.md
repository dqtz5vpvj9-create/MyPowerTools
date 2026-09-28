# 文件助手：发给自己与发给设备

用户在 2026-09-28 明确否决以配对、检查连接、选择直传或网盘为日常主流程。
本合同替代 IMPLEMENTATION_PLAN 中原来的文件发送向导，其他 MPT 手机迁移目标不变。
实现继续使用现有模块、HostControl、Surface、secret store、Tailscale 与托管 OpenList。

## 2026-09-28 修正：身份不依赖网络，连通性属于设备之间的路径

本节覆盖此前将本机 Tailscale 地址、网盘配置或接收监听器作为使用前提的设计。
用户现有环境已经能访问 Tailnet；不能因本机未枚举到 `100.x` 网卡地址，就判定用户
“没有连接 Tailscale”。经网关访问、系统路由与本机安装客户端是不同的接入方式。

1. **本机身份**：设备标识、名称和密钥在本地生成并持久保存。首次启动完全断网也能
   生成、查看、复制和展示连接码；不调用网络探测、公网注册或网盘检查，不等待对方设备。
   切换网络和重启不能改变设备身份。连接码中的候选地址可以缺省，不能充当身份主键。
2. **设备关系**：扫码或导入连接码是在确认目标身份及授权范围，不是在确认双方已经连通。
   对方离线也能预览并保存连接关系。文件接收授权、自己的会话加入和远程控制授权继续分开，
   不因知道一个设备编号、发现一个地址或加入 Tailnet 就自动获得权限。
3. **传输路径**：只有确定目标设备或会话后，才评估本机到该目标的具体路径。
   同时允许 A 到 B 的 Tailnet 路径可用、A 到 C 通过公网中转；不存在用于阻止整个工具的
   全局“这台设备不在 Tailscale”状态。Tailscale 内部经 DERP 通信仍是可用的 Tailnet 路径，
   不能把非直连误判成离线。候选地址存在、网络探测失败和目标确实应答分别记录。
4. **公网保底**：没有可用的 Tailnet 路径时，自动通过 `https://proxy.lixinrui000.cn`
   收发。普通用户不填写 IP、端口、WebDAV 或服务器账号；自定义 OpenList/国内网盘继续
   作为可选存储。未授权国内网盘不影响基本收发。公网也暂不可达时，保留待发内容并自动恢复。
5. **体验**：用户只看到设备、内容和真实进度。生成连接码、打开会话和点击发送不出现
   “先连接 Tailscale”的门槛；路由选择不进入操作流程。诊断可以按目标显示某条路径的结果，
   不能把某条路径失败显示成设备整体不可用。接收端离线时允许发送，收到真实回执才显示送达。

实施顺序是先修身份与连接码的本地生成，再修按目标选择路径和现有 Tailnet 的误判，
随后接入公网保底并完成双端验证。新增中转服务本身不能算作上述错误已修复。

## 用户实际操作

**发给自己**是固定入口，打开即为跨设备共用的会话。底部输入文字，附件按钮选文件或图片，
也能粘贴、拖入文件。从系统分享选择 MPT 后，默认进入此会话的待发送区；用户点发送。
不要求先选择电脑。自己的电脑或手机随后打开会看到同一条记录，点文件即可打开或下载。
发送前另一端离线也能用；本机断网时先可靠保存，在网络恢复后继续发送。

**发给设备**从附件待发送区或会话的转发操作进入。接收设备以名字和图标排列，点一下发送。
第一次由对方确认接收并选择是否记住。已信任设备不重复配对；单次接收不默认变为永久信任。
系统负责发现、选择直传或中转、保存进度和重试。接收方可在接收提示或会话中打开文件。

首次只需确认要关联的自己的设备，不要求先启用网络或配置离线收件。发现来源包括已记住的
设备、已授权会话成员和可用网络上的候选；扫码不受当前网络及目标在线状态限制。
默认公网中转由产品提供。用户选择自己的网盘时，OpenList 的安装和启动由 MPT 管理，
网盘授权只做一次；这条可选路径不阻塞基本收发。本地保存不能显示成已同步到其他设备。
IP、端口、口令、WebDAV、检查连接、传输模式放在高级设置，不进入普通发送界面。

## Surface 命令合同

原命令保留兼容，以下命令是新界面的正式入口。JSON 采用 camelCase。

| 命令（前缀 `file-transfer.`） | 输入 | 返回 / 用途 |
| --- | --- | --- |
| `assistant.inspect` | 无 | `{identity, items, pendingRequests, relay, receiving}`，读取缓存，不主动联网 |
| `assistant.send` | `{text?, paths?:string[], targetDeviceId?:string}` | `{accepted, itemIds}`；省略目标表示发给自己。先持久保存再 accepted |
| `assistant.sync` | 无 | 拉取会话、续传待发项、更新真实回执；单次执行可取消，后台调度复用同一实现 |
| `assistant.retry` | `{itemId}` | 仅重试选中失败项，复用原 itemId，不产生重复消息 |
| `assistant.cancel` | `{itemId}` | 取消选中未完成项，已确认送达不能改成取消成功 |
| `assistant.open` | `{itemId}` | `{path?, text?, needsDownload?}`；本地没有文件时实际下载后再返回可打开路径 |
| `assistant.devices` | 无 | `{devices, discoveryState, message?}`；一次有界发现，关闭页面取消 |
| `assistant.receive.respond` | `{requestId, accept, remember:false}` | 对应接收请求的结果；拒绝/超时不写永久凭据 |
| `assistant.link.export` | 无 | `{code}`；纯本地生成，不检查网络、不注册服务器；仅在用户打开“连接我的设备”时展示完整二维码 |
| `assistant.link.preview` | `{code}` | 不含密钥的名称/加入范围预览，不落盘、不联网、不自动加入 |
| `assistant.link.import` | `{code}` | 用户确认后加入自己的会话；凭据只进 secret store |

`identity={id,name,linked}`。identity 不含控制本机能否使用工具的网络状态。
`relay={configured,state,message?}`，state 保留 `unconfigured|unknown|available|unavailable`
以兼容旧接口；默认公网服务不是需要用户配置的项目，也不能仅凭内置地址就宣称服务可用。
`devices[]` 保留 `deviceId,name,address,platform,paired,available` 以兼容旧消费者；address
允许为空，available 的旧含义不得被用于禁止发送。新增按本机、目标、路径记录的探测结果，
明确 `unknown|reachable|unreachable`、检查时间和已选路径。公网可排队不等于目标在线。
具体 DTO 由后端负责人一次定义并交给 Surface 消费，不由不同代理重复推断。
`pendingRequests[]` 包含 `requestId,deviceId,name,itemNames,expiresAt`，无凭据。

`items[]`：`id,kind,text?,name?,size,createdAt,senderDeviceId,senderName,targetDeviceId?,
state,bytesDone,localPath?,error?,receipts[]`；kind 为 `text|image|file`。
state 为 `queued|sending|stored|delivered|downloading|available|failed|cancelled`。
`stored` 只表示中转已保存，`delivered` 必须有目标设备实际保存后的回执；
发给自己没有单一目标，界面用“已同步”表示中转保存，用设备回执展示具体收到的设备。
失败项就地重试，不把整个会话变成报错页面。空状态为可操作的会话，不呈现配置表格。

模块发布既有事件渠道上的 `file-transfer.assistant.changed`。Surface 按事件刷新缓存，
进入或恢复前台时触发一次同步；不能每秒拉取完整列表。队列处理属于模块生命周期，
关闭 Surface 不删除待发内容，不取消已交给模块的传输；禁用工具必须停止其后台任务。

系统分享文字的激活 URI 为 `mypowertools://file-assistant?text=<URI encoded text>`，
附件继续用现有 file URI，来自同一系统分享的多个激活都追加到同一个待发送区。
激活只准备内容，不自动发送。`mpt://assistant/` 属于 file-transfer 的连接入口，
只打开 `assistant.link.preview`，确认后才调用 import；日志不输出 URI 或编码后的片段。

Android 的文件打开使用宿主 `MptAvaloniaSurfaceContext.OpenFileAsync(path, token)`，
为系统查看器临时授予单个文件的读取权限；桌面可用原有 Launcher。显示路径不是打开成功。
现有 AndroidDownloadsService.PublishAsync 会删除发布前的私有副本，助手的持久附件不能
直接交给它删除；发布到下载目录时使用副本，并保留会话原件供再次打开和转发。

## 并行实现归属

- **GPT/Claude（接管 M3）**：FileTransfer.Surface 和其 tests，交付手机与桌面的会话、设备选择、
  接收确认、初次连接面板与系统分享接线；负责视觉和交互决策及实际截图验收。
  DSH 不再自行设计或改造 UI。沿用共享 MptMobile 主题和公开返回接口，禁止模拟成功。
- **M4**：FileTransferModule、DirectTransfer、既有 Core（下述新目录除外）、package 命令元数据、
  MobileDeviceService 与原有测试；负责命令、接收确认、凭据、队列调度、设备发现和收件箱的真实整合。
- **F2**：仅新增 Core/Discovery/ 和 Core.Tests/Discovery/。提供自动发现，不写模块或 Surface。
- **F3**：仅新增 Core/Assistant/ 和 Core.Tests/Assistant/。提供持久会话、待发内容、OpenList
  会话存储和回执，不写模块或 Surface。可以新增 OpenListClient partial 文件，根类由 M4 改为 partial。
- **F4**：仅新增 Android/Files/、Android/Resources/xml/mpt_shared_files.xml 与其独立测试，
  提供 Android 文件查看器、只读 FileProvider 和宿主委托；不改 M7 的 Activity 或 manifest。
- 主代理：文档、最终接线、跨设备验收、部署和发布。

### F2 给 M4 的接口

命名空间 `FileTransfer.Core.Discovery`：
`DiscoveredDevice(string DeviceId,string Name,string Address,int Port,string Platform)`；
`DeviceDiscovery.DiscoverAsync(IReadOnlyList<DiscoveredDevice> known,CancellationToken)`。
默认文件端口复用 DirectReceiver 实际默认端口，禁止新猜测端口。实现应有可注入候选来源与探测器。
候选来自已知设备、已授权会话成员、可用的 Tailscale peer API/CLI，以及局域网发现提供的
Tailnet 地址。未能读取本机 Tailscale API 只表示该候选来源不可用，不表示不能访问目标，
也不能阻止已知设备的探测和公网发送。Tailnet 身份探测继续只访问允许的 Tailnet IP；
公网路径使用独立的 HTTPS 中转客户端，不放宽旧直传地址白名单，不扫描整个网段，
不上传或广播密钥。发现消息不是授权，不支持的发现来源不得伪装为完整发现结果。

身份探测沿用 int32 大端长度 + JSON，请求 `{version:3,kind:"hello"}`，
应答 `{ok:true,deviceId,name,address,port,platform}`。M4 在现有接收器里实现此无文件身份应答；
旧版拒绝时，保留已配对候选但不把它标为可用。F2 负责对应客户端与有界并发。
首次传输授权由 M4 处理，F2 不生成 token、不自动信任发现结果。

### F3 给 M4 的接口

命名空间 `FileTransfer.Core.Assistant`，公开记录 `AssistantItem` 和 `AssistantReceipt`。
字段按上面的 JSON 定义；记录可有额外本地元数据，但密钥不进入条目。
`AssistantStore(string directory)` 提供 LoadAsync、EnqueueAsync、SaveAsync、GetPayloadPath；
入队必须把附件复制到模块自己的持久目录，使用独立 itemId，重复文件名不覆盖，重启可恢复。
具体 C# 参数由 F3 在目录的接口说明中一次固定；M4 读取说明后调用，勿各自复制实现。

OpenListClient 新增 PublishAssistantAsync、ListAssistantAsync、DownloadAssistantAsync、
WriteAssistantReceiptAsync、ListAssistantReceiptsAsync；每个调用显式传会话 id 与取消 token。
使用 `assistant/<conversationId>/<itemId>/` 命名空间；payload 写完再发布 manifest。
每条消息不可变且有唯一 id；每个设备独立写回执，不并发覆盖全局 index。
重试复用相同 id，接收按 id 去重。回执在本地原子保存后才能写入。
继续沿用现有 URL 验证与跨站重定向去凭据规则；不添加新的内容 hash 机制。
文本无需伪装为待下载文件；图片可有预览，原文件必须保留。

## 必须实际完成的验收

以下四项是修正后的首要验收，不得用公网 health 成功或协议单测替代：

1. 全新数据目录、完全断网、无 Tailscale 网卡和客户端、未配置网盘：成功生成身份及二维码；
   导出/预览过程没有网络调用，重启和切换网络后身份不变，对方是否存在不影响结果。
2. 用户实际 Windows Dev 与 Android 环境：核对系统到目标的路由及 MPT 的真实请求。
   记录 `http://nav.tail.lixinrui000.cn/` 作为现有 Tailnet 接入的检查入口；导航站可访问
   不能替代目标文件服务的端到端验证。本机无 Tailnet 地址但可经网关访问目标的情形不得误拦。
3. 同一发送端同时面对 B、C：B 的 Tailnet 路径成功，C 的该路径失败但公网可达；两者都能
   完成文字和文件收发，失败路径不拖住另一条路径，不要求用户切换模式或修改本机状态。
4. 已授权目标离线时接受发送并持久保存；目标恢复后自动收到，发送端取得实际回执。
   完全断网时排队；网络恢复、切换以及进程重启后自动继续，不重复消息，不假报送达。

同时保留以下完整产品验收：

1. 手机系统分享一张图片和一个文件到“发给自己”，电脑同一会话出现，能打开原内容；反向亦然。
2. 发给自己不打开设备列表，不输入 IP/连接码，不选择传输方式。
3. 接收设备关闭后发送，记录不会假报送达；重启接收端后自动同步，发送端取得真实回执。
4. 发送端断网或进程重启，待发文字和附件仍在；恢复后无需重选文件，不出现重复消息。
5. 未配对设备自动出现，点设备后对方确认并收到；拒绝与超时不产生信任或成功状态。
6. 已记住设备下次选中即可发送；接收窗口无需常开，工具明确禁用后不会偷偷运行。
7. 320/390 宽度、深浅色、放大字体与键盘弹出均能完成发送、打开、返回；桌面支持拖入和粘贴。
8. 模拟 WebDAV/loopback 可证明协议和恢复逻辑，但实际 Tailnet、真实 OpenList、平台运行与
   国内网盘授权分别记录证据；没有完成的路径继续作为待办，不计入产品可用验收。
