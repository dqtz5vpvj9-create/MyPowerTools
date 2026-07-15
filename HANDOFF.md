# MyPowerTools 架构闭环交接

更新时间：2026-07-15（Asia/Shanghai）

实现执行者：外部 Agent

审核与修正负责人：主 Codex

## 任务

把 MyPowerTools 收敛为可安装、可动态发现工具、可托管长期 Service Units 的 PowerToys 式底座。工具保留 submodule 与独立发行能力，父仓库提供 SDK、协议、Shell、Runner、ServiceManager、安装器和门禁。

外部 Agent 负责繁杂编码、脚本执行、远程部署和证据采集。主 Codex 逐批审查 diff、复跑最小门禁、修正实现并更新完成状态。

权威执行计划：[`docs/18-architecture-revision-fast-plan.md`](docs/18-architecture-revision-fast-plan.md)

## 当前真实状态

- Quick/A1：11 项通过。
- Quick/A2：5 项通过。
- Process/A3：13 项通过，证据：`artifacts/architecture-smoke/a3/result.json`。
- Process/A4：6 项通过，证据：`artifacts/architecture-smoke/a4/result.json`。
- Release/A5 本地阶段通过 7 项，不可变证据：`artifacts/architecture-smoke/a5/result-18065254.json`。
- Release/A5 远程阶段已有一次通过记录 `result-65aef22b.json`，随后一次并发复跑 `result-ff61fff8.json` 失败；远程闭环等待主 Codex 复核与稳定复跑。
- 五个工具已产出动态 Surface DLL 和独立 `.mptpkg`。
- Shell 已移除工具源码编译链接；CLI 视觉测试已迁入 `Mpt.Cli.VisualTesting`。
- 产品 CLI 已包含 `service list/status/start/stop/restart/reload/shutdown`。
- Runner 已支持 `--endpoint-address` 与 `--instance-name`，供隔离远程验收使用。
- 当前候选包：`artifacts/install/MyPowerTools-0.2.0-win-x64.zip`。
- 候选包正在被 A5 执行者重建；最终 SHA-256 必须从静止后的 zip 重新计算并写入执行报告。
- 当前大小：521,998,368 bytes（497.8 MiB）。
- 最新候选构建已包含 installer stdout 收敛修复；后续 A5 仍会按 runId 重建候选包。

## 最关键的事实校正

- ScreenEase 已有独立 `ScreenEase.Service` 与 unit manifest。
- Remote Notifications 仍由 Surface 内服务执行轮询、验证、历史和 banner；独立 Service Unit 尚未实现。
- Doubao 的服务控制与子进程监督仍位于 Surface；独立 controller Service Unit 尚未实现。
- 安装器当前只部署真实 ScreenEase Service Unit，动态收集全部工具 units 尚未完成。
- 远程 A5 曾启动 ScreenEase 测试 unit，随后因 installer 输出解析问题停止；测试进程与临时文件已精确清理。
- `src/MyPowerTools.Cli/Program.cs` 仍含 `#if MPT_LEGACY_VISUAL_HARNESS` 死代码，产品构建未包含该块，后续需要删除。

## 下一步唯一任务

执行计划中的 **Batch 0：闭合远程 A5 驱动**。

1. 重建 `0.2.0` 候选包。
2. 复跑本地 Release/A5。
3. 使用隔离 endpoint 与 instance 在 `veeam` 执行远程 A5。
4. 保存远程 JSON 证据。
5. 精确清理本轮 runId 资源。
6. 把执行报告交给主 Codex；保持计划验收框为未勾选状态。

Batch 0 经主 Codex 审核后，再领取 Remote Notifications Service Unit 的 Batch 1。

## 远程环境事实

- SSH alias：`veeam`，当前解析到 `10.33.0.183`。
- 远程主机名：`WIN-9RQATO3GN18`。
- 日常 Shell PID：`596364`，安装路径位于 `C:\Program Files\MyPowerTools\Shell\...`。
- 日常 Runner PID：`639168`，安装路径位于 `C:\Program Files\MyPowerTools\Runner\...`。
- 远程还有日常 SmartBird/Doubao runtime 进程。
- 上述日常进程、安装目录、设置和数据均在验收保护范围内。
- 远程测试只能使用独立 TEMP 根、独立 Runner endpoint、独立 instance name、独立日志和带 runId 的资源名。

## 当前证据与产物

- A3：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\architecture-smoke\a3\result.json`
- A4：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\architecture-smoke\a4\result.json`
- A5 local：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\architecture-smoke\a5\result-18065254.json`
- A5 remote pass：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\architecture-smoke\a5\result-65aef22b.json`
- A5 latest failed rerun：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\architecture-smoke\a5\result-ff61fff8.json`
- Candidate root：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\install\0.2.0`
- Candidate zip：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\install\MyPowerTools-0.2.0-win-x64.zip`
- 五个工具包：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\install\0.2.0\payload\packages`

## 最小门禁命令

```powershell
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Quick
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Process
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.2.0 -RuntimeIdentifier win-x64
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Release -CandidateRoot .\artifacts\install\0.2.0
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Release -CandidateRoot .\artifacts\install\0.2.0 -RemoteHost veeam
```

## 工作树保护

当前父仓库与五个 submodule 均有大量既有改动和未跟踪文件。外部 Agent 必须：

- 先运行 `git status --short` 与 `git submodule status`，保存到执行报告。
- 只改当前 Batch 列出的文件和对应工具 submodule。
- 保留用户改动，严禁 reset、checkout、clean、stash、批量覆盖和历史重写。
- submodule 内的改动直接留在该 submodule，父仓库显示 `m` 属于预期状态。
- 远程递归删除前解析绝对路径，并验证目标位于本轮测试根。

## 外部 Agent 报告模板

```markdown
## Batch <n> execution report

- Scope:
- Parent repo changed files:
- Submodule changed files:
- Commands and exit codes:
- Evidence paths:
- Remote PIDs before/after:
- Residual work:
- Risks:
```

## 审核规则

- 外部 Agent 不修改计划勾选状态。
- 主 Codex 检查 diff、进程边界、IPC、真实业务路径与证据 JSON。
- 主 Codex 复跑该 Batch 的最小相关门禁。
- 主 Codex 修正发现的问题，再勾选通过项。
- 任何 `blocked-by-environment` 条目继续保持未完成。

## 踩过的坑，严禁重复

- Surface 内后台轮询器无法提供独立生命周期；Remote Notifications 必须迁入 Service Unit。
- Surface 内子进程监督器会随 UI 生命周期中断；Doubao 必须迁入 Service Unit。
- 本地 A5 通过仅覆盖候选清单、哈希、UI 契约与本地 Runner discovery。
- installer stdout 若混入自启注册输出，远程 verifier 会把多行数组当成结果路径。
- 远程命令长度会触发 Windows/OpenSSH 限制；上传 `.ps1` 后执行。
- `PowerShell` 变量名大小写不敏感；`$ToolId` 与 `$toolId` 会冲突。
- WebView 空白页、静态壳、诊断页和按钮存在均无法证明产品路径完成。
- 远程清理只接受 runId 精确目标；现有 Program Files MPT 属于用户日常实例。
- 候选包目前重复携带运行时，体积达到 497.8 MiB；安装器动态化后处理体积。

## Suggested skills

- `powershell-safe-invocation`：所有 Windows、PowerShell、SSH 和远程脚本任务。
- `plan-doc`：仅在主 Codex审核后更新本执行计划。
- `computer-use`：仅用于真实 GUI 核心路径与截图验收。
- `browser:control-in-app-browser`：仅用于本地 Web Surface 页面交互验收；涉及用户现有 Edge 会话时遵循根 `AGENTS.md` 的扩展浏览器要求。

## 交接完成条件

新会话读完本文件与权威计划后，可直接执行 Batch 0。无需重新规划架构，禁止重新扫描整个仓库来生成另一份方案。
