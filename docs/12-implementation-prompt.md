# 首轮实现提示词

下面这段可以直接交给编码 agent 作为第一轮实现任务。

```text
你要在 MyPowerTools 仓库中实现跨平台个人工具平台的第一阶段原型。

目标技术栈：
- .NET
- Avalonia Shell
- Runner / Shell 双进程控制面
- Typed MPT Module Protocol
- Typed Host Control Protocol
- Protobuf/gRPC
- InProc trusted module path
- gRPC over Native IPC sidecar path
- package/module manifest schema
- ui-surface schema
- Windows 先行，但接口必须为 Windows/macOS/Linux 预留 Platform Pack

重要架构约束：
- Runner 是常驻单实例控制面，Shell 是独立 Avalonia UI 进程。
- Shell 只能通过 Host Control IPC 访问 runtime，不直接持有 module host。
- Shell 退出或崩溃不能影响 Runner 和已启动模块。
- JSON-RPC over stdio 只作为 compatibility layer，不作为主通信方案。
- Runner 启动时只读取 package/module manifest 和静态索引，不启动所有模块。
- 可信 .NET 模块优先走 InProcDotNetHost。
- 跨语言 sidecar 优先走 GrpcIpcModuleHost。
- Windows IPC 使用 Named Pipes。
- macOS/Linux IPC 预留 Unix Domain Socket。
- 高权限动作一律进入 PrivilegedBroker。
- SettingsStore 由 Runner 作为单一写入方，使用 revision 防冲突。
- 命令执行使用 invocationId 保证幂等。
- 事件流使用 seq，断线后通过 lastEventSeq 恢复。
- UI 必须使用 MyPowerTools.UI tokens 和 Shell components。
- 模块 surface 不能注入全局样式、全局字体、全局颜色、独立导航 chrome。
- Dashboard、Detail、Settings、Command Palette 必须通过视觉回归 gate。

第一阶段范围：
1. 创建解决方案结构：
   src/MyPowerTools.Runner
   src/MyPowerTools.Shell.Avalonia
   src/MyPowerTools.HostControl
   src/MyPowerTools.Runtime
   src/MyPowerTools.Protocol
   src/MyPowerTools.ModuleHost.InProcDotNet
   src/MyPowerTools.ModuleHost.GrpcIpc
   src/MyPowerTools.Platform.Abstractions
   src/MyPowerTools.Packaging
   src/MyPowerTools.UI
   src/MyPowerTools.Cli

2. 实现 schema validator：
   schemas/package.schema.json
   schemas/module.schema.json
   schemas/command.schema.json
   schemas/status.schema.json
   schemas/settings.schema.json
   schemas/ui-surface.schema.json

3. 实现 Protocol：
   读取 proto/mpt_module_v1.proto
   读取 proto/mpt_host_control_v1.proto
   生成 C# contracts
   定义 error code
   定义 settings revision
   定义 event seq
   定义 invocationId

4. 实现 Runner：
   SingleInstance
   Tray placeholder
   Host Control IPC Server
   PackageRegistry
   ModuleRegistry
   TransportSelector
   CommandIndex
   SettingsStore
   EventBus
   ModuleSupervisor

5. 实现 Shell：
   Host Control IPC Client
   Dashboard
   Module Detail Page
   Settings Center
   Command Palette
   Logs Viewer
   UI tokens
   Shell components

6. 实现 ModuleHost：
   InProcDotNetHost
   GrpcIpcModuleHost Windows Named Pipe 初版
   HttpModuleHost placeholder
   StdioCompatModuleHost placeholder

7. 实现示例模块：
   SampleDotNetModule 走 InProc
   SamplePythonGrpcSidecar 走 gRPC Named Pipe
   验证两者都能显示状态和命令

8. 实现 CLI：
   mpt validate <package-dir>
   mpt inspect <package-dir>
   mpt ui check <package-dir>
   mpt ui snapshot

验收标准：
- Runner 可无 Shell 常驻。
- Shell 可连接 Runner 并渲染 Dashboard snapshot。
- Shell 重启后能恢复模块列表和命令索引。
- Runner 启动时不启动 sidecar 也能显示静态模块列表。
- InProc sample 可执行命令。
- gRPC sidecar sample 可执行命令。
- sidecar 崩溃不影响 Runner。
- settings revision 冲突能被检测。
- schema 校验能覆盖 examples 下所有 module/package。
- UI surface 校验能覆盖 examples 下所有自定义 surface。
- Dashboard light/dark/compact 三套截图通过视觉回归。
```
