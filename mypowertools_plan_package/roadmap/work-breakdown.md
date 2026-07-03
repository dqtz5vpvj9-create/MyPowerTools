# 工作分解

## Work Packet 字段

| 字段 | 含义 |
|---|---|
| ID | Agent 任务编号 |
| 任务 | 可独立验证的最小任务 |
| 依赖 | 开始前必须完成的任务 |
| 允许修改范围 | Agent 可写入的路径 |
| 产物 | 必须出现的文件或功能 |
| 验证 | 必须执行的命令或测试 |

## Epic 1 Protocol and Schema

| ID | 任务 | 依赖 | 允许修改范围 | 产物 | 验证 |
|---|---|---|---|---|---|
| P-001 | package schema | 无 | `schemas/package.schema.json` | schema | `mpt validate package examples/android-tools-suite` |
| P-002 | module schema | P-001 | `schemas/module.schema.json`, `examples/**/module.json` | schema + examples | `mpt validate module examples/screenease` |
| P-003 | ui surface schema | P-002 | `schemas/ui-surface.schema.json`, `examples/**/module.json` | UI schema | `mpt validate ui examples/screenease` |
| P-004 | command/status/settings schema | P-002 | `schemas/command.schema.json`, `schemas/status.schema.json`, `schemas/settings.schema.json` | schemas | `mpt validate examples` |
| P-005 | Protobuf protocol | P-002 | `proto/mpt_module_v1.proto` | proto | `dotnet test MyPowerTools.Protocol.Tests` |
| P-006 | typed errors | P-005 | `docs/02-module-protocol.md`, `proto/mpt_module_v1.proto` | error model | protocol tests |
| P-007 | sync semantics | P-005 | `docs/14-consistency-integrity-scalability.md`, `proto/mpt_module_v1.proto` | revision/event/invocation rules | protocol tests |

## Epic 2 Transport Runtime

| ID | 任务 | 依赖 | 允许修改范围 | 产物 | 验证 |
|---|---|---|---|---|---|
| T-001 | TransportSelector | P-002 | `src/MyPowerTools.Runtime/**` | selector service | Runtime tests |
| T-002 | InProcDotNetHost | T-001 | `src/MyPowerTools.ModuleHost.InProcDotNet/**`, `templates/dotnet-module/**` | InProc host | sample module status/command |
| T-003 | GrpcIpcModuleHost Windows | P-005, T-001 | `src/MyPowerTools.ModuleHost.GrpcIpc/**` | Named Pipe transport | sidecar sample tests |
| T-004 | HTTP ModuleHost | T-001 | `src/MyPowerTools.ModuleHost.HttpFacade/**` | HTTP facade | SmartBird facade tests |
| T-005 | StdioCompatModuleHost | T-001 | `src/MyPowerTools.ModuleHost.StdioCompat/**`, `templates/stdio-compat-module/**` | compat host | compat sample tests |
| T-006 | PackageRuntimePool | T-003 | `src/MyPowerTools.Runtime/**` | shared runtime pool | android-tools-suite runtime inspect |
| T-007 | Sidecar supervisor | T-003 | `src/MyPowerTools.Runtime/**` | crash recovery | sidecar crash test |

## Epic 3 Runner Shell and UI

| ID | 任务 | 依赖 | 允许修改范围 | 产物 | 验证 |
|---|---|---|---|---|---|
| S-001 | Runner and Shell skeleton | P-002 | `src/MyPowerTools.Runner/**`, `src/MyPowerTools.Shell.Avalonia/**`, `src/MyPowerTools.HostControl/**` | 双进程控制面 | Runner/Shell smoke run |
| S-002 | UI tokens and components | S-001, P-003 | `src/MyPowerTools.UI/**`, `ui/**`, `docs/16-ui-guardrails.md` | tokens + components | UI token tests |
| S-003 | Dashboard | S-002 | `src/MyPowerTools.Shell.Avalonia/**` | Dashboard page | screenshot test light/dark |
| S-004 | Command Palette | S-002, P-004 | `src/MyPowerTools.Shell.Avalonia/**` | command page | command index tests |
| S-005 | Settings Center | S-002, P-004 | `src/MyPowerTools.Shell.Avalonia/**` | settings page | settings schema tests |
| S-006 | Module Detail Page | S-002 | `src/MyPowerTools.Shell.Avalonia/**` | detail scaffold | screenshot tests |
| S-007 | Logs Viewer | S-002 | `src/MyPowerTools.Shell.Avalonia/**` | logs page | log stream tests |
| S-008 | UI validator | P-003, S-002 | `src/MyPowerTools.Cli/**`, `src/MyPowerTools.UI.Tests/**` | `mpt validate ui` | UI gate tests |

## Epic 4 Platform Packs

| ID | 任务 | 依赖 | 允许修改范围 | 产物 | 验证 |
|---|---|---|---|---|---|
| W-001 | Platform abstractions | P-002 | `src/MyPowerTools.Platform.Abstractions/**` | interfaces | compile tests |
| W-002 | Windows path/notification | W-001 | `src/MyPowerTools.Platform.Windows/**` | providers | provider tests |
| W-003 | Windows service/task | W-001 | `src/MyPowerTools.Platform.Windows/**` | provider | service broker tests |
| W-004 | Windows Named Pipe security | T-003, W-001 | `src/MyPowerTools.Platform.Windows/**` | IPC security | IPC tests |
| W-005 | Windows display provider | W-001 | `src/MyPowerTools.Platform.Windows/**` | provider | ScreenEase tests |
| W-006 | Windows network provider | W-001 | `src/MyPowerTools.Platform.Windows/**` | provider | AdbForwarder tests |

## Epic 5 Broker

| ID | 任务 | 依赖 | 允许修改范围 | 产物 | 验证 |
|---|---|---|---|---|---|
| B-001 | PrivilegedBroker | W-001 | `src/MyPowerTools.Broker/**` | broker | broker tests |
| B-002 | SecretBroker | W-001 | `src/MyPowerTools.Broker/**` | broker | secret tests |
| B-003 | ServiceBroker | W-003 | `src/MyPowerTools.Broker/**` | broker | SmartBird service tests |
| B-004 | NetworkBroker | W-006, B-001 | `src/MyPowerTools.Broker/**` | broker | AdbForwarder network tests |
| B-005 | Audit log | B-001 | `src/MyPowerTools.Broker/**` | audit log | audit tests |

## Epic 6 Existing Modules

| ID | 任务 | 依赖 | 允许修改范围 | 产物 | 验证 |
|---|---|---|---|---|---|
| M-001 | ScreenEase InProc module | T-002, S-006, W-005 | `modules/screenease/**` | module | `mpt validate ui modules/screenease` |
| M-002 | AndroidTools powertoold | T-006 | `modules/android-tools-suite/**` | package runtime | runtime inspect |
| M-003 | AndroidTools Notifications | M-002, S-007 | `modules/android-tools-suite/modules/notifications/**` | module | UI gate + event tests |
| M-004 | AndroidTools Remote Commands | M-002, S-004 | `modules/android-tools-suite/modules/remote-commands/**` | module | command tests |
| M-005 | AndroidTools Process Monitor | M-002, S-007 | `modules/android-tools-suite/modules/process-monitor/**` | module | event tests |
| M-006 | Doubao Agent controller | T-003, S-006 | `modules/doubao-agent/**` | module | health + logs tests |
| M-007 | SmartBird typed facade | T-004, B-003 | `modules/smartbird-thermostat/**` | module | status + service tests |
| M-008 | AdbForwarder InProc + Broker | T-002, B-004 | `modules/adb-forwarder/**` | module | broker + UI gate tests |

## Epic 7 Cross-platform Preview

| ID | 任务 | 依赖 | 允许修改范围 | 产物 | 验证 |
|---|---|---|---|---|---|
| X-001 | Unix Domain Socket transport | T-003 | `src/MyPowerTools.ModuleHost.GrpcIpc/**` | UDS transport | Linux/macOS sample tests |
| X-002 | Linux Platform Pack | W-001, X-001 | `src/MyPowerTools.Platform.Linux/**` | provider pack | Linux provider tests |
| X-003 | macOS Platform Pack | W-001, X-001 | `src/MyPowerTools.Platform.Mac/**` | provider pack | macOS provider tests |
| X-004 | capability degradation UI | S-006, X-002, X-003 | `src/MyPowerTools.Shell.Avalonia/**` | degradation views | screenshot tests |

## Epic 8 Module Ecosystem

| ID | 任务 | 依赖 | 允许修改范围 | 产物 | 验证 |
|---|---|---|---|---|---|
| E-001 | `mpt create module` | P-002 | `src/MyPowerTools.Cli/**`, `templates/**` | CLI command | template test |
| E-002 | `mpt validate module` | P-004 | `src/MyPowerTools.Cli/**` | CLI command | validation tests |
| E-003 | `mpt validate ui` | S-008 | `src/MyPowerTools.Cli/**` | CLI command | UI gate tests |
| E-004 | `mpt package module` | P-001 | `src/MyPowerTools.Packaging/**` | packager | package tests |
| E-005 | `mpt install/update` | E-004 | `src/MyPowerTools.Packaging/**` | installer | rollback tests |
| E-006 | `mpt inspect runtime` | T-006 | `src/MyPowerTools.Cli/**` | inspector | runtime tests |

## 里程碑

| 里程碑 | 验收 |
|---|---|
| G1 | InProc sample module 可运行 |
| G2 | gRPC Native IPC sample module 可运行 |
| G3 | Static index 首屏可用 |
| G4 | UI Guardrail Gate 可运行 |
| G5 | AndroidTools 三模块共享 powertoold |
| G6 | 现有五个工具全部接入 Dashboard |
| G7 | Windows Broker 可执行高权限动作 |
| G8 | macOS/Linux UDS sample 可运行 |
