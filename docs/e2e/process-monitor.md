# Process Monitor 流程覆盖

工具 `tool-release.json` 当前标记 paused。现有共享 adapter 由 remote-notifications 构建，测试调用该真实 adapter；桌面入口重新启用与独立包拆分未纳入本次更改。

运行：`npx e2e run tests/e2e/process-monitor.e2e.ts --output .e2e/tools/process-monitor`。
先构建 `src/MyPowerTools.Tests/MyPowerTools.Tests.csproj`，runner 使用当前 Debug 产物并校验 TRX 实际执行数、通过数与跳过数。

| 用户流程 | 实际验证 |
| --- | --- |
| 保存监控列表 | 真模块命令写入临时共享数据；去重、忽略空项 |
| 恢复监控列表 | 新模块初始化读取已保存列表 |
| Host 设置后通过命令更新 | 列表与设置立即读取新名称 |
| 清空 Host 列表 | 新模块恢复后保持空配置；状态为 degraded |
| 空命令保存、未知命令 | 校验错误码；已保存数据保持完整 |
| 检查正在运行的程序 | 复制 Windows ping 到唯一命名临时路径，无窗口启动；查询进程数 1 |
| 程序结束 | 仅结束测试自己启动的进程；确认 running=false |
| 订阅事件 | 活跃进程发布 process.started；实际 5 秒扫描周期检测退出并发布 watch.alert，序列号递增 |
| 空文件导入 | 真模块解析临时 processes.json；列表/摘要为空，提供可操作状态 |

本测试通过 tester-army/e2e 调用生产模块工作流，使用真实 Windows 进程枚举和磁盘读写。临时数据隔离于测试上下文，测试只结束自己持有的子进程。

范围限制：Avalonia 页面点击、通知展示、长时间扫描稳定性及 macOS/Linux 进程行为尚未验证。此文件未将代码文本匹配计入行为覆盖。

已发现配置问题：Host 设置应用后，watch.save 未更新当前内存列表；清空 Host 列表未将空数组写回磁盘。独立回归分别覆盖当前实例与新实例恢复行为。

2026-10-04 验证：修复前新增回归 2 失败、2 通过；修复后 e2e 两组全部通过，内部 5 条 .NET 回归通过、零跳过。报告位于 `.e2e/tools/process-monitor/report.json`，失败复现报告保留于 `.e2e/tools/process-monitor-baseline/report.json`。Dev overlay 与共享 adapter 上架由根任务统一执行。
