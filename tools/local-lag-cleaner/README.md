# 本机卡顿专清

本工具包含可由 MyPowerTools 动态发现的 Tool SDK surface 和独立 Windows CLI。

## Tool SDK 入口

`sdk-tool/` 是当前 MyPowerTools Tool SDK 交付：

- 根目录包含 `tool.json`。
- Surface 项目通过 NuGet 引用 `MyPowerTools.AvaloniaSdk` 与 `MyPowerTools.ToolSdk`。
- Surface 项目没有指向 MyPowerTools Suite 源码的项目引用。
- Release 程序集由 `tool.json` 的 `dotnet-surface` 路由加载。
- 诊断与处置通过 `stdio-jsonrpc` Runtime 在 Shell 进程外执行。

## 能力

- 5 秒快速扫描与 15 秒深度扫描，PDH 每秒采样并保留全部时间对齐样本，输出真实平均、P95 与峰值。
- 覆盖 CPU 队列、DPC/中断、分页读入、磁盘延迟/队列/吞吐、逐进程 CPU/内存/I/O/句柄/线程。
- 读取物理/提交内存、分页池、非分页池、内核池标签、全机句柄、窗口响应性、磁盘空间、电源计划、用户静置边界和待重启状态。
- PID 4 超过高句柄触发线时，一次性读取系统扩展句柄表，按 File、Event、Process、Thread、Key、Section、ALPC Port、IoCompletion 等内核对象类型拆分；报告同时显示 PID 4 占比、全机同类型数量和关联 Pool Tag 证据。
- File 成为主导类型时，继续按 GrantedAccess 全量聚合，并盘点文件系统过滤驱动的注册项、运行状态、加载组、Altitude、驱动厂商和版本。
- “管理员 File 归因”通过安装目录中的 `MyPowerTools.ElevatedBroker` 触发 Windows UAC，启用 `SeDebugPrivilege` 后二次读取系统句柄表，只复制最多 512 个 PID 4 File 句柄，按设备、卷、路径根和文件类型归并来源。
- 可选采集 NVIDIA GPU 利用率、显存和温度，汇总七天硬件、存储、显示、资源耗尽与应用挂起事件。
- 保存同一次开机历史快照；使用至少四个同场景样本、成对斜率中位数和连续增长比例识别内核池与 System 句柄泄漏趋势。
- 识别 WeFlow 静置持续占用。
- 按显式成员识别豆包 computer-use MCP，保留最新会话组；孤儿会话采用高可信证据，同父更新替代采用中可信证据。
- 标记 NVIDIA Container、服务宿主的句柄或线程异常。
- 输出 JSON 与 Markdown 报告，逐项标注探针覆盖、可信度、因果链与处置风险。
- 诊断门槛可在 MPT Settings 中配置，关键告警与严重阈值会自动保持有序。
- 使用“计划 ID + 动作 + 八位确认令牌 + 十分钟有效期”执行清理；计划采用跨进程锁和原子领取。

报告不会写入完整命令行、可执行路径、用户名或令牌。

## 构建

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\build.ps1
```

输出：

- `artifacts/cli/`：独立 CLI
- `artifacts/local-lag-cleaner.mptpkg`：Tool SDK 安装包
- `sdk-tool/src/LocalLagCleaner.Tool/bin/Release/net10.0/`：Tool SDK surface

## 独立 CLI

```powershell
.\artifacts\cli\local-lag-cleaner.exe scan --seconds 3
.\artifacts\cli\local-lag-cleaner.exe plan --action mcp-residue
.\artifacts\cli\local-lag-cleaner.exe apply --token XXXXXXXX
```

WeFlow 清理会结束应用，先保存其中的工作：

```powershell
.\artifacts\cli\local-lag-cleaner.exe plan --action weflow
```

服务重启要求管理员终端和额外开关：

```powershell
.\artifacts\cli\local-lag-cleaner.exe plan --action delivery-optimization
.\artifacts\cli\local-lag-cleaner.exe apply --token XXXXXXXX --allow-service-restart
```

远程桌面服务重启会断开会话，还需添加 `--allow-disconnect`。

## 安全边界

- 扫描和计划阶段只读。
- MyPowerTools Surface 只展示结果并调用 MPT 命令，系统探针和进程操作运行在隔离 Runtime。
- 管理员 File 归因使用五分钟一次性请求、SHA-256 请求摘要、Broker 文件锁和身份复核；管理员进程仅执行固定的句柄复制与路径读取动作，结果写入 Broker 请求目录并由 Runtime 校验后消费。
- 管理员 File 归因不会关闭远程句柄、修改文件、卸载驱动或停止服务。
- MCP 逐个结束计划中明确列出的 PID，生成与执行阶段均复核 PID、名称、启动时间、父子链、直接标记、替代会话证据和短时 CPU。
- WeFlow 先请求优雅退出，等待后才处理仍存活的计划实例。
- 服务重启仅由独立 CLI 执行，要求管理员权限和显式开关。
- 内核池或 System 句柄达到严重阈值时，工具只建议重启，不自动重启电脑。
- 计划过期、三元确认不匹配、PID 被复用、身份或证据变化时拒绝执行；计划一经领取即一次性消费。
