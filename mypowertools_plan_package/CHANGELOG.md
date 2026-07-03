# Changelog

## v3

### 架构修正

- 增加 PowerToys 源码对照结论。
- 将原 `App + Shell + Runtime` 调整为 `Runner + Shell` 双进程控制面。
- `Runner` 长期常驻，管理 tray、hotkey、runtime、module host、broker、settings、event bus。
- `Shell.Avalonia` 独立进程，管理 Dashboard、Settings、Logs、Detail Page、Command Palette。
- 新增 `mpt_host_control_v1.proto`，用于 Shell 与 Runner 的 typed IPC。
- `Command Palette` 明确采用 provider 模型、静态索引优先、动态 provider 后台补充。

### UI 修正

- 新增 `docs/16-ui-guardrails.md`。
- 新增 `ui/design-tokens.json`。
- 新增 `ui/component-contracts.md`。
- 新增 `ui/visual-regression-matrix.md`。
- 新增 `schemas/ui-surface.schema.json`。
- 增加 Shell 统一组件、token、布局、状态、权限、日志、设置准入规则。
- 增加视觉回归矩阵和 UI 验收 checklist。

### 执行模型修正

- 删除全部人工排期估算。
- 新增 `docs/17-agent-execution-model.md`。
- 将路线改为 agent gate、依赖关系、验收条件。
- 每个阶段只保留目标、输入、任务、产物、验收。

## v2

- 将主通信方案从 JSON-RPC over stdio 升级为 Typed MPT Module Protocol。
- 采用 Protobuf/gRPC、InProc .NET、gRPC over Named Pipes / Unix Domain Sockets。
- stdio 降级为 compatibility layer。
- 增加 `docs/13-transport-runtime.md`、`docs/14-consistency-integrity-scalability.md`、`proto/mpt_module_v1.proto`。

## v1

- 确定 Avalonia Shell、Module SDK、Multi-module Package、Platform Capability Packs、Privileged Broker。
- 确定 AndroidTools 拆分为 Notifications、Remote Commands、Process Monitor。
