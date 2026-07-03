# MyPowerTools 最终计划包

## 最终结论

MyPowerTools 的正式路线定为：

```text
Avalonia Shell
+ .NET Runner Runtime
+ Runner/Shell 双进程控制面
+ Typed MPT Module Protocol
+ Transport-tiered Module Runtime
+ InProc trusted module path
+ gRPC over Native IPC for sidecars
+ Multi-module Package
+ Platform Capability Packs
+ Privileged Broker
+ Module Packaging System
+ stdio compatibility layer
+ UI Design Token System
+ UI Surface Guardrails
```

核心修正：底座采用 PowerToys 式的 Runner 思路，但执行方式按 MyPowerTools 的跨平台目标重新设计。Runner 长期常驻，Shell 作为 Avalonia UI 进程独立存在；Shell 崩溃或重启不影响模块 runtime。正式模块通信采用统一协议、分级传输、平台原生 IPC。`JSON-RPC over stdio` 只保留为兼容层、脚本层和开发样例层。

MyPowerTools 负责定义平台规范、加载模块、统一 UI、统一设置、统一命令、统一通知、统一日志、统一权限代理。各工具实现 MyPowerTools 模块契约，并通过 package 形式安装到 MyPowerTools。

## 传输分级

| 等级 | 通道 | 用途 | 结论 |
|---:|---|---|---|
| T0 | 静态 manifest / 预索引 | 模块发现、命令首屏、Dashboard skeleton | 必备 |
| T1 | In-process .NET SDK | 可信 .NET / Avalonia 模块 | 主力路径 |
| T2 | gRPC over Native IPC | Python、Rust、Node、Go、长期 sidecar | 主力路径 |
| T3 | HTTP / WebSocket localhost | 已有 HTTP 服务、远程服务、调试接口 | 兼容既有服务 |
| T4 | JSON-RPC over stdio | 临时脚本、轻量工具、开发 fallback | 兼容层 |

平台映射：

```text
Windows  : InProc > gRPC Named Pipes > HTTP/WebSocket > stdio fallback
macOS    : InProc > gRPC Unix Domain Socket > HTTP/WebSocket > stdio fallback
Linux    : InProc > gRPC Unix Domain Socket > HTTP/WebSocket > stdio fallback
```

## 计划包内容

```text
docs/
  00-final-decision.md
  01-system-architecture.md
  02-module-protocol.md
  03-package-model.md
  04-platform-capabilities.md
  05-ui-surfaces.md
  06-existing-tools-migration.md
  07-android-tools-split.md
  08-security-privilege-data.md
  09-roadmap.md
  10-testing-release.md
  11-repository-layout.md
  12-implementation-prompt.md
  13-transport-runtime.md
  14-consistency-integrity-scalability.md
  15-powertoys-code-comparison.md
  16-ui-guardrails.md
  17-agent-execution-model.md

adr/
  ADR-0001-use-avalonia-shell.md
  ADR-0002-sdk-first-module-first.md
  ADR-0003-transport-tiered-module-runtime.md
  ADR-0004-multi-module-package.md
  ADR-0005-platform-capability-packs.md
  ADR-0006-runner-shell-process-split.md
  ADR-0007-ui-design-token-governance.md

schemas/
  package.schema.json
  module.schema.json
  command.schema.json
  status.schema.json
  settings.schema.json
  ui-surface.schema.json

proto/
  mpt_module_v1.proto
  mpt_host_control_v1.proto

ui/
  design-tokens.json
  component-contracts.md
  visual-regression-matrix.md

examples/
  android-tools-suite/
  screenease/
  doubao-agent/
  smartbird-thermostat/
  adb-forwarder/

templates/
  dotnet-module/
  python-grpc-sidecar-module/
  stdio-compat-module/
  webview-module/
```

## 核心定案

| 项目 | 结论 |
|---|---|
| Runner | 独立常驻进程，负责 tray、hotkey、runtime、module host、broker |
| Shell | 独立 Avalonia 进程，负责 Dashboard、Settings、Logs、Detail、Command Palette |
| Shell 到 Runner | Typed Host Control Protocol，gRPC over native IPC |
| Runtime | .NET |
| 模块协议 | Typed MPT Protocol，Protobuf/gRPC 为主 |
| 可信模块 | InProc .NET SDK |
| Sidecar 模块 | gRPC over Named Pipes / Unix Domain Sockets |
| 兼容模块 | HTTP/WebSocket 或 JSON-RPC stdio fallback |
| 跨平台方式 | Platform Capability Packs |
| 高权限动作 | Privileged Broker |
| UI 约束 | Design tokens + Shell components + visual regression gate |
| AndroidTools | `android-tools-suite` package，导出 Notifications、Remote Commands、Process Monitor 三个 module |
| 新工具接入方式 | 工具实现 MyPowerTools SDK，Host 不写工具专属逻辑 |

## 第一轮实现入口

首轮开发直接从以下文件开始：

```text
docs/12-implementation-prompt.md
docs/13-transport-runtime.md
docs/15-powertoys-code-comparison.md
docs/16-ui-guardrails.md
proto/mpt_module_v1.proto
proto/mpt_host_control_v1.proto
schemas/module.schema.json
schemas/ui-surface.schema.json
ui/design-tokens.json
examples/screenease/module.json
examples/android-tools-suite/package.json
```
