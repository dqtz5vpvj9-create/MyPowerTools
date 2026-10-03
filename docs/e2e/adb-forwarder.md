# ADB Forwarder 流程验证

`tests/e2e/adb-forwarder.e2e.ts` 通过 tester-army/e2e 调度 C# 产品服务和 ViewModel 测试，使用 TRX 计数确保每组实际执行、全部通过且没有跳过。前置步骤是构建 Debug 的 `src/MyPowerTools.Tests/MyPowerTools.Tests.csproj`，随后运行 `npx e2e run tests/e2e/adb-forwarder.e2e.ts`。

| 用户流程 | 自动覆盖与边界 |
| --- | --- |
| 有线设备预检、启动转发 | 执行生产 workflow service，验证 setprop、tcpip、等待设备和 forward 顺序；ADB 使用受控适配器 |
| WiFi 转发、传输模式选择 | 验证无线 endpoint 转发和模式校验；ADB 使用受控适配器 |
| SSH 转发与远程 AOSP | 验证固定脚本 stdin、长期 tunnel 所有权、重启后身份核验和停止；SSH 使用受控适配器 |
| 断线、连接重试、取消 | 验证离线 endpoint 清理、重试次数、取消终止和错误序列号脱敏 |
| 转发清理 | 验证只停止本工具拥有的 tunnel，身份冲突或未退出时保留记录 |
| 设备环境配置 | 验证原子写入和重新载入、重复项拒绝、WiFi 间隔与 WakeupPad 保留、typed editor |
| 共享端口边界 | 新增回归：50535 可派生内部端口 65535；50536/65535 拒绝并保留原文件；手改越界配置返回错误；WiFi 保持完整 TCP 端口范围 |
| 映射编辑、预览、恢复 | 验证 typed mapping、修改后预览失效和 Apply 禁用、保存映射恢复、选中设备刷新 |
| 管理员授权 | 验证第二次确认、磁盘篡改、binary 变化、一次消费、过期、并发和状态冲突；特权网络适配器受控 |
| 部分失败回滚、日志故障 | 验证取消和列表失败回滚、审计故障与实际网络变更结果 |
| 外部程序超时 | 执行真实子进程超时/进程树终止测试；进程创建使用无控制台窗口模式 |

现有仅检查源码文本的 `Shell_wires...`、`Manifest_and_shell_route...` 和 `Source_gates...` 已从此流程套件排除。

桌面实际点击、UAC 弹窗、真实 USB 授权、无线设备连接、远程 SSH/AOSP 连通、Windows 实际 portproxy 写入和后台周期维护尚需具备对应设备和环境的验证。当前套件报告的是产品服务流程覆盖，无法据此宣称这些外设与桌面 UI 已通过。

2026-10-03 本机执行结果：5 个 e2e 场景全部通过，底层 TRX 共 54 个实际测试（22 + 4 + 4 + 10 + 14），零跳过，运行时间 22.72 秒。

本次开发更新同时暴露 `build.ps1` 缺少 `-Configuration` 参数的问题。Dev 更新入口会传入该参数，原脚本在参数绑定阶段退出。脚本现已支持 Debug/Release，实际构建和产物提示均使用所选配置；完整 Dev 更新由主任务串行验证。
