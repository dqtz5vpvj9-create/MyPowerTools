# Input Monitor 流程验证

运行：`npx e2e run tests/e2e/input-monitor.e2e.ts --output .e2e/tools/input-monitor`

测试调用真实 `InputMonitorHost`、事件队列、SQLite repository、统计 payload 与疲劳引擎。每个新增场景使用临时数据库及合成输入适配器，测试结束释放自身 SQLite 连接池并清理目录。测试接入 tester-army/e2e 工具目标；TRX 验证实际执行数量、全通过、零跳过，排除源码字符串扫描。

2026-10-04 最终运行：5/5 e2e 场景通过，包含 25 个实际行为回归（8 个新增行为测试和 SQLite 版本检查），零跳过。完整报告位于 `.e2e/final/report.json`、`junit.xml` 与 `summary.md`。

| 用户流程 | 自动验证 |
|---|---|
| 开始、停止、再次开始采集 | 已接收事件完成落库，重启无重复，重新打开保留统计 |
| 暂停、恢复采集 | CaptureRunning 状态和采集适配器生命周期 |
| 清空采集数据 | 内存、数据库、重新打开后的历史归零 |
| 调整采样、休息时间、保留期 | 边界 clamp、采集适配器同步、写入后重新打开 |
| 应用分类覆盖与移除 | 大小写匹配、持久化、恢复默认分类 |
| 损坏设置恢复 | 原文件保存为 .corrupt，隐私默认开启，允许保存有效设置 |
| 历史月、季、年 | 2024 闰年 29/91/366 天完整序列，活动落在正确日期 |
| 数据保留期 | 过期事件、统计和轨迹同时删除，当前数据保留 |
| 日统计、热图、多显示器 | 真实 SQLite 读取、24 小时/7 日 payload、负坐标显示器分箱 |
| 隐私采集 | 合成键盘事件转换为类别，热图无原始字符 |
| 疲劳到期、暂停、手动休息、跳过 | 阈值、提醒次数、暂停、异常持久值恢复、overlay 关闭 |
| 键盘/鼠标采样与应用识别 | 自动重复过滤、按键时长、距离/时间采样、窗口识别降级 |

## 运行限制

此套件验证服务与统计行为。Avalonia 页面按钮、日期导航显示、键盘快捷键、真实 Windows 全局 hook、前台窗口识别权限、全屏休息 overlay、音效以及登录启动仍需开发版桌面验收。运行自动化时保持真实输入采集关闭，使用合成适配器。e2e 的 browser/mobile engine 当前未提供 Windows Avalonia 桌面驱动。

SQLite 依赖核查发现 `Microsoft.Data.Sqlite 9.0.8` 带入 `SQLitePCLRaw.lib.e_sqlite3 2.1.10`，处于 [CVE-2025-6965 官方 GitHub advisory](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) 的受影响范围。已显式升级 [SQLitePCLRaw.bundle_e_sqlite3 2.1.13](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/2.1.13)，使用 Microsoft dotnet-public 镜像完成本机恢复，并通过真实 `sqlite_version()` 验证版本至少为 3.50.2，全部统计与采集回归通过。

## 已复现并修复

- 停止采集时取消消费者过早，600 个已接受的合成点击落库为 0。关闭采集生产者后，消费者完成队列尾部，再执行最终 buffer flush 和统计 drain。
- 历史月、季、年统计始终使用当前日期生成查询范围，选择 2024-02-29 得到当前月 31 天、当前季 92 天和当前年 365 天。查询范围现由所选日期生成，并验证闰年 29/91/366 天及合成活动所在日期。
