# 最终架构结论

## 定位

MyPowerTools 是个人工具平台底座。它负责：

```text
统一入口
统一 Runner
统一 Shell
统一模块协议
统一命令面板
统一设置中心
统一通知中心
统一日志视图
统一权限代理
统一跨平台能力抽象
统一模块安装和更新
统一传输运行时
统一 UI 设计约束
```

工具本身实现 MyPowerTools SDK。Runner 只加载模块契约，Shell 只渲染模块贡献的 UI Surface。

## 最终技术栈

```text
Avalonia Shell
.NET Runner Runtime
Typed MPT Module Protocol
Typed Host Control Protocol
Transport-tiered Module Runtime
InProc trusted module path
gRPC over Native IPC for sidecars
Multi-module Package
Platform Capability Packs
Privileged Broker
Module Packaging System
Design Token System
stdio compatibility layer
```

## PowerToys 对照后的修正

PowerToys 的主线是常驻 Runner 加载和管理模块、控制 tray、hotkey、settings、enable/disable；Settings UI 作为独立进程通过 Named Pipes 与 Runner 传递 JSON；Command Palette 又有自己的 provider 模型。MyPowerTools 保留这些结构性思想，并进行三处升级：

```text
Runner 与 Shell 分进程
模块协议强类型化
平台能力包跨平台化
```

最终控制面：

```text
MyPowerTools.Runner
  常驻、单实例、tray、hotkey、runtime、settings、event bus、module hosts、broker

MyPowerTools.Shell.Avalonia
  独立 UI 进程、Dashboard、Settings、Detail、Logs、Command Palette

MyPowerTools.ModuleHost
  InProc .NET、gRPC IPC、HTTP facade、stdio compatibility
```

## 核心修正

`JSON-RPC over stdio` 不作为主通信方案。它适合兼容层、脚本层和开发样例，但不承担正式底座的性能、完整性和扩展性目标。

正式底座采用：

```text
协议统一
传输分级
平台原生 IPC
可信模块进程内加载
Sidecar 模块 gRPC 化
高权限动作 broker 化
Shell 与 Runner 解耦
UI 受 token 和组件约束
```

## 分级传输

| 等级 | 通道 | 适用对象 | 目标 |
|---:|---|---|---|
| T0 | 静态 manifest / 预索引 | 模块发现、命令索引、首屏卡片 | 启动速度和首屏可用 |
| T1 | In-process .NET SDK | 自有可信 .NET/Avalonia 模块 | 最高性能和最直接集成 |
| T2 | gRPC over Native IPC | Python、Rust、Node、Go、长期 sidecar | 强类型、高吞吐、强隔离 |
| T3 | HTTP / WebSocket localhost | 已有 HTTP 服务、调试服务、远程服务 | 兼容既有服务 |
| T4 | JSON-RPC over stdio | 临时脚本、小工具、fallback | 兼容性 |

## 架构原则

| 问题 | 决策 |
|---|---|
| MyPowerTools 是否逐个适配旧工具 | 不采用。工具主动实现 SDK 和模块契约 |
| 是否复制 PowerToys DLL 插件机制 | 不复制 Windows 细节。保留 Runner 思想，采用跨平台分级传输 |
| 是否把 Shell 和 Runner 绑死 | 不采用。Shell 是独立 Avalonia 进程 |
| 是否把所有工具塞进同一进程 | 不采用。可信 UI 和轻量模块可 InProc，复杂 runtime 仍有进程隔离 |
| 是否默认 sidecar | 不采用单一路线。T1 InProc 与 T2 gRPC IPC 是主力 |
| 是否保留 stdio | 保留为 fallback，不进入性能关键路径 |
| 是否支持一个包导出多个工具 | 支持。Package 是安装单位，Module 是用户可见工具单位 |
| 是否跨平台 | 是。平台差异进入 Platform Capability Packs |
| 高权限动作如何处理 | 全部进入 Privileged Broker |
| UI 是否允许模块自由发挥 | 不允许。模块只能贡献受控 Surface |

## 最终目标

新增第 50 个工具时，Host 不增加工具专属逻辑。新增工具只需要：

```text
声明 package/module manifest
选择 entrypoint
实现 IMptModule 或 gRPC ModuleControl
贡献受控 UI Surface
贡献 Command Provider
声明 capability 和 permission
通过 mpt validate
通过 UI visual regression gate
打包为 .mptpkg
```
