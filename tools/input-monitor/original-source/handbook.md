# InputMonitor 开发手册

> 最后更新：2026-08-03
> 本文档面向后续维护者，涵盖：项目能力、架构设计思路、踩坑记录与工程化细节。
> 阅读建议：先读「二、支持的能力」建立全貌，再读「三、架构设计」理解数据流，「四、踩坑记录」是修改代码前必读。

---

## 一、项目概览

macOS 状态栏应用（`LSUIElement=true`，无 Dock 图标），在本机持续采集键盘/鼠标输入活动与前台窗口活动，本地统计分析并提供疲劳提醒。

- **技术栈**：Swift 5.9 + SwiftPM executable target，SwiftUI（面板）+ AppKit（状态栏/窗口）混合，SQLite3 裸 C API（无第三方依赖）
- **最低系统**：macOS 13
- **硬约束**：全部数据仅存本机（`~/Library/Application Support/InputMonitor/`），应用不联网
- **代码规模**：约 4900 行 Swift，33 个源文件
- **前置调研**：`docs/input-monitoring-research.md`（方案选型对比，推荐先读）

### 目录结构

```
Sources/InputMonitor/
├── InputMonitorApp.swift    # @main 入口 + AppDelegate + 权限引导视图
├── AppState.swift           # 中心协调器：装配全部模块、实时快照、窗口管理
├── Engine/                  # 采集层
│   ├── EventTapEngine.swift #   CGEventTap 全局低频事件采集（键盘/点击/滚轮）
│   ├── CursorPoller.swift   #   光标位置 30Hz 后台轮询（替代 mouseMoved 事件）
│   ├── EventSampler.swift   #   移动采样：距离/时间双阈值节流
│   └── KeyTranslator.swift  #   keyCode→字符（UCKeyTranslate，跟随键盘布局）
├── Pipeline/                # 管道层
│   ├── MetricsAggregator.swift # 内存聚合：当日累计 + 未落库增量 + 互动分钟桶
│   └── EventBuffer.swift    #   环形缓冲：500 条或 5s 触发批量落库
├── Activity/                # 窗口活动层
│   ├── FrontAppTracker.swift #  前台 App/窗口会话追踪 + 心跳
│   └── AppCategoryMap.swift #   bundleID→分类映射（内置规则+用户覆盖）
├── Fatigue/                 # 疲劳提醒层
│   ├── FatigueEngine.swift  #   疲劳值状态机（UserDefaults 持久化）
│   └── RestReminderController.swift # 全屏提醒窗口生命周期
├── Storage/                 # 存储层
│   ├── Database.swift       #   SQLite 连接/事务/schema 迁移
│   └── EventRepository.swift #  全部读写接口 + 统计查询（最大文件，673 行）
├── Permissions/
│   └── PermissionManager.swift # 辅助功能 + 输入监控双权限管理
├── UI/                      # 状态栏、主面板、统计图表、设置页、休息遮罩
├── Models/                  # InputEventRecord / FrontAppSession / AppCategory
└── Utils/                   # FileLogger（文件日志）/ ScreenState（锁屏判断）
Scripts/
├── bundle.sh                # 构建 + 组装 .app + 签名（release 默认，--debug/--run）
├── install.sh               # 一键：构建→签名→杀旧进程→装 /Applications→启动
└── setup-signing-cert.sh    # 创建自签名证书（TCC 权限跨构建不失效的关键）
```

---

## 二、支持的能力

### 1. 输入活动采集

| 能力 | 口径说明 |
|---|---|
| 键盘按键次数 | 仅计 `keyDown` 且剔除长按自动重复（`isAutoRepeat=0`）；修饰键 `flagsChanged` 计入活动但不计次数 |
| 按键按住时长 | 按 keyCode 配对 keyDown/keyUp 的纳秒时间戳差值；>10 分钟的异常配对（多为休眠干扰）丢弃 |
| 按键内容 | `UCKeyTranslate` 按当前键盘布局映射字符；纯控制字符不记录；**隐私模式下完全不映射不落字符**（密码输入期间系统也会自动屏蔽 event tap） |
| 鼠标点击 | 左/右键 down 计数（up 不计） |
| 滚轮 | 行滚动优先，像素滚动折算为 ±1 行 |
| 鼠标轨迹/距离 | 30Hz 后台轮询光标位置，采样器按「最小距离 30px 或最小间隔 50ms」节流，静止（<0.5px）不采样；位移距离累计不丢失 |

### 2. 前台窗口活动追踪

- `NSWorkspace.didActivateApplicationNotification` 感知 App 切换；心跳（默认 30s）感知同 App 内窗口标题变化（如浏览器换标签）
- 生成 `FrontAppSession`（bundleID/应用名/窗口标题/起止/分类），≥1s 的会话才落库
- **锁屏/熄屏立即结束会话并挂起追踪**，解锁恢复——锁屏时长不计入
- 应用分类：内置关键词规则（开发/浏览器/办公/设计/社交/影音/其他）+ 用户覆盖，覆盖持久化到 UserDefaults

### 3. 疲劳提醒

- 疲劳值状态机：勾选来源（键盘/鼠标/窗口）的活动每秒累计 `100/(提醒间隔×60)` 点；**连续 2 分钟无活动暂停累计**（离开电脑不长疲劳）
- 值 ≥100 触发全屏提醒（多屏全覆盖，渐变白遮罩 + 倒计时）
- **跳过** → 值=100、阈值升至 120，再达 120 再提醒（循环）；**休息完成** → 值=0、阈值恢复 100
- 手动休息：取消则恢复原值，完成则清零；暂停提醒：不弹窗但照常累计，恢复时可配置「已超阈值立即提醒」
- 疲劳值/阈值/暂停状态均持久化，重启不丢

### 4. 统计面板（主面板「活动统计」页）

- **粒度**：日 / 月 / 季 / 年；**维度**：键盘 / 鼠标 / 应用 / 所有；**应用类型筛选**：7 类 + 全部
- **日期筛选**：日粒度下控制条出现 DatePicker 可选具体日期（不可选未来），全部日粒度图表（分小时活动/频次/近7天热力/明细）跟随选中日期，近 7 天热力以选中日为终点；「回到今天」一键复位。**顶部概览卡在日粒度下跟随选中日期**（互动时长：今天用 AppState 实时值，历史日期查 DB 分钟桶去重值 `interactionSeconds(day:)`）；月/季/年粒度概览卡恒为今天
- 今日概览卡：键盘次数+按住时长、鼠标操作+移动距离、窗口活动时长（真实会话）、互动时长（去重并集）
- 图表：活动时长柱状图、操作频次曲线（日=10 分钟分桶，范围=按天）、近 7 天分小时热力、周网格热力（对齐周一）、按键 Top30、鼠标轨迹网格热力、应用使用排行、应用类型分布
- **全部图表悬浮出数值**：折线图 RuleMark annotation；三张热力图（分小时/周网格/轨迹）均为自绘气泡（`HeatmapHoverBadge`/`hoverBadge`），不用系统 `.help()`
- **互动时长口径**：分钟桶去重——键鼠事件 ∪ 轨迹采样 ∪ 应用会话，同一分钟任一来源有活动即计 60s
- **自动刷新**：面板可见期间每 30s 自动 `reload()`（与 daily_stats drain 节奏对齐）；互动时长卡另有 AppState 1s 实时快照驱动。关闭窗口/切到设置页即停，重开面板立即刷新一次

### 5. 设置页

通用（开机自启、数据保存周期）、疲劳提醒（间隔/休息秒数/来源开关/恢复提醒）、提示音（音效/音量/试听）、隐私模式、采集参数（轨迹采样距离、窗口心跳间隔）、应用分类配置。**全部设置即时生效**（热更新轮询器/定时器，无需重启）。

- **开机自启**：`SMAppService.mainApp` 注册/注销登录项（macOS 13+ API，与本项目最低系统一致）；开关初始值以系统登录项实际状态为准（用户可能在系统设置里改动），注册失败自动回滚并记日志
- **数据保存周期**：默认 365 天（1 年），天粒度 TextField+Stepper 输入（clamp 1–36500）；`EventRepository.purgeExpiredData` 按时间戳清 events/track_points/app_usage、按天字符串清 daily_stats，单事务删除后 `wal_checkpoint(TRUNCATE)` 收缩 WAL；**启动时 + 修改设置后**各触发一次（异步在 db 队列，不阻塞启动）

### 6. 状态栏

`疲劳% · X小时Y分`（互动时长），1s 刷新；菜单含：今日分小时活动折线图、手动休息、暂停提醒、活动统计、设置、退出。

---

## 三、架构设计思路

### 1. 分层与数据流

```
CGEventTap(主runloop回调)        NSWorkspace通知/心跳Timer
        │ 只拷贝原始字段                  │
        ▼                               ▼
  eventQueue(串行) ─────────── 统一 consumeEvent ──────────┐
        │                        │            │            │
        ▼                        ▼            ▼            ▼
  MetricsAggregator        EventBuffer   FatigueEngine  FrontAppTracker
  (当日累计+增量+分钟桶)     (500条/5s批刷)  (活动信号)     (会话≥1s落库)
        │ 每30s/退出drain        │ flush           │           │
        └────────┬───────────────┴─────────────────┴───────────┘
                 ▼
        EventRepository → Database(db.queue 串行) → SQLite(WAL)
                 ▲
        UI 查询：StatsViewModel 后台线程调同步查询 → 主线程发布
```

核心原则：

- **主线程零负担**：tap 回调只做值类型字段拷贝并 `eventQueue.async`，键码翻译、记录构建、聚合全部在后台串行队列。回调做重活会被系统禁用 tap，也会拖慢整个系统的事件分发
- **三条串行队列各司其职**：`eventQueue`（事件消费/聚合）、`buffer queue`（批量落库）、`db queue`（全部 SQL）。Repository 查询方法用 `db.queue.sync` 同步返回，**严禁在 db.queue 上再调同步查询**（死锁）
- **内存聚合 + 增量落库**：`MetricsAggregator` 维护当日累计（UI 实时展示）与未落库增量（`drainDelta` 取走后 upsert 进 `daily_stats`），磁盘 I/O 与事件频率解耦
- **双口径活动时间**：`activeInputSeconds`（事件间隔 ≤60s 累加，按 wallTime）用于「输入活动」；分钟桶并集用于「互动时长」。两者互补，避免心跳与事件重复累计

### 2. 关键设计决策

| 决策 | 理由 |
|---|---|
| tap 只监听低频事件，鼠标移动改 30Hz 轮询 | mouseMoved 高频回调会让系统鼠标卡顿漂移（真实踩坑，见下文） |
| 活动时间按 wallTime 计算间隔 | CGEvent.timestamp 是 mach 时间基，不同来源事件混算会出错 |
| 分类在**查询时**重映射 | `app_usage` 表存的是写入时的分类，但统计查询一律走 `AppCategoryMap.category(for:)` 实时映射——用户改分类后历史数据立即按新口径统计 |
| 互动时长用分钟桶去重 | 键鼠与窗口会话天然重叠，并集去重才能反映真实"在电脑前"时长；启动时从 DB 查当日基线 + 内存桶叠加，重启不清零 |
| `daily_stats` 增量 upsert | 日/月/季/年热力直接读预聚合表，避免扫全量 events（注意：`daySummary(for:)` 注释声称"无则从 events 现算兜底"，但实现并未兜底，无聚合记录的天返回 0——注释与实现不一致，勿依赖兜底） |
| 退出 `shutdown()` 同步屏障 | 停采集→buffer 同步 flush→`eventQueue.sync` drain→`barrierSync` 等 DB 队列清空，保证滞留数据全落库 |
| 禁用 App Nap | `beginActivity(.latencyCritical)` + `LSAppNapIsDisabled`，后台定时器（轮询/落库/刷新）不被系统挂起 |
| 配置即时生效 | 设置页改采样距离/心跳间隔/隐私模式，直接热更新运行中的引擎，不重启 |

### 3. 数据存储

`~/Library/Application Support/InputMonitor/input-monitor.db`（WAL 模式），四张表：

- `events`：键盘/点击/滚轮原始事件（**不含**轨迹采样点）
- `track_points`：轨迹采样点（x/y/move_delta）
- `app_usage`：前台会话（含写入时的分类快照）
- `daily_stats`：日聚合（upsert 累加）

配置类全部在 UserDefaults：设置项、疲劳值状态、应用分类覆盖（`appCategoryOverrides`）。
日志：`~/Library/Application Support/InputMonitor/debug.log`（>512KB 自动截断，启动/权限/tap/心跳全埋点，无控制台环境下诊断的唯一手段）。

---

## 四、踩坑记录（修改代码前必读）

按 git 历史顺序整理，每条都是真实事故：

### 1. 启动即崩溃：@StateObject 在 init() 中提前访问（cf65bcf）
SwiftUI `@StateObject` 未完成安装时就访问它，会创建临时实例，导致 `EventTapEngine` 的 `refcon`（`Unmanaged.passUnretained`）指向已释放对象→悬垂指针崩溃。
**现状**：`AppDelegate.applicationDidFinishLaunching` 里启动，`AppState.start()` 有 `hasStarted` 幂等保护。**新增启动逻辑时务必保持幂等**。

### 2. 独立运行不采集 / 授权反复失效（fbaad02）
- ad-hoc 签名每次构建 cdhash 都变 → TCC 认为应用变了 → 辅助功能/输入监控授权失效，表现为"权限开着但不采集"
- **解法**：`setup-signing-cert.sh` 创建稳定自签名证书 `InputMonitor Dev Cert`，`bundle.sh` 优先用它签名，覆盖安装授权不失效；权限引导窗提示用户"开关关掉再打开/移除重加"
- 键盘事件需要**双权限**：辅助功能（AX，事件+窗口标题）+ 输入监控（`CGPreflightListenEventAccess` 预检 / `CGRequestListenEventAccess` 登记）。启动预检 + 2s 轮询，全授权后自动开始采集

### 3. 系统级鼠标卡顿漂移（04de82e，最重要的性能教训）
最初 tap 监听包含 mouseMoved 的高频事件，且回调在主 runloop 做翻译/构建 → 整个系统的鼠标都卡。
**解法**（当前架构的三条腿）：① tap 掩码只留键盘/点击/滚轮；② 鼠标移动改 `CursorPoller` 30Hz 后台轮询 + 采样节流；③ `UCKeyTranslate` 移出主线程。**给 tap 加新事件类型前，先想清楚频率**。

### 4. 状态栏不刷新（04de82e）
SwiftUI `MenuBarExtra` 的 label 有不刷新的系统问题（一直显示 0小时0分）。**解法**：换成 AppKit `NSStatusItem + NSMenu`，1s Timer 驱动刷新 + 文本 diff 避免重复渲染。状态栏着色问题经历两轮：①曾用"烘焙白色像素进 NSImage"（`sourceAtop` 合成），但 2026-08-05 用户反馈**浅色菜单栏（亮壁纸）下白字不可见**；②最终方案（当前）：**模板图（`isTemplate = true`）+ 不带 foregroundColor 的 `attributedTitle`**，颜色完全交给系统按菜单栏明暗自适应。**修正旧认知**：系统忽略的只是自定义颜色，模板图机制仍生效——不设颜色恰恰就是要的自适应效果。勿再烘焙固定色像素。

### 5. 活动时间统计口径三连坑（04de82e / a1d69d1）
- 窗口心跳时长与输入活动时间重复累计 → 状态栏「活动时间」只取输入活动
- 「窗口活动」改用 `app_usage` 真实会话时长（而非心跳累加）
- 锁屏期间心跳照跑导致虚高 → 锁屏/熄屏通知挂起 tracker + `ScreenState.isLocked` 判断

### 6. 重启后互动时长清零（a1d69d1）
内存分钟桶只覆盖启动后时段 → 启动时异步查 DB 当日去重分钟数作基线（`interactionBaseline`）叠加；跨天基线清零重算。

### 7. 数据滞留内存丢失（04de82e）
空闲时 buffer 不到 500 条、drain 不触发 → 快照 Timer 每 30s 无条件 drain 一次；退出时 `shutdown()` 同步屏障落库全部滞留数据。

### 8. 休息时长精度与旧配置迁移（04de82e）
分钟级滑杆不够用 → 改秒级键盘输入（10–7200s）；旧分钟配置一次性迁移后删除；`didSet` 里 clamp（观察者内赋值不会递归触发）。

### 9. 休息遮罩打断用户工作（代码注释，RestReminderController）
遮罩窗用 `.screenSaver` 级别自然置顶，**刻意不调 `NSApp.activate`**——激活 app 会把统计/设置窗一并拉到前台。

### 10. event tap 被系统禁用（代码注释，EventTapEngine）
超时/用户输入会触发 `tapDisabledByTimeout/UserInput` → 回调里收到这两类事件立即 `tapEnable` 重挂。

### 11. 设置页分类 Picker 选择不生效（2829ab7）
手工 `Binding(get:set:)` 包一个非 `ObservableObject` 的单例 → SwiftUI 感知不到变化，Picker 显示值不更新（数据其实已保存）。**解法**：store 改 `ObservableObject` + `@Published`，视图 `@ObservedObject`。**教训：SwiftUI 中手工 Binding 的数据源必须可被观察。**

### 12. 月度热力图范围外日期显示 0（2026-08-03 修复）
月/季/年周网格按周一对齐后，首列会包含**范围外**的日期（如 8 月视图含 7.27–7.31），但 `reload()` 的数据查询范围只到当月 1 号 → 这些格子 `dayValues[key] ?? 0` 显示 0（实际有数据）。**解法**：热力网格数据源（`daySummaries`/`appSecondsByDay`/`perDayAppSeconds`/`perDayInteractionSeconds`）按 `weekGridParams().alignedStart` 对齐范围取数；柱状图/折线图仍保持当月范围（`fillDays(rangeStart…rangeEnd)`）。**教训：视图渲染范围与数据查询范围必须同源推导，网格对齐会放大渲染范围。**

### 13. 日期筛选后概览卡变 0（2026-08-05 修复）
新增日粒度日期筛选后，概览卡仍按固定键 `daySummaries[today]` 取值，而查询范围已变为选中日期 → 选中历史日期时三张卡显示 0（互动时长卡因取 AppState 实时值反而非 0，更迷惑）。**解法**：概览卡在日粒度下跟随 `selectedDay`（互动时长历史日期查 `interactionSeconds(day:)`，今天保留实时值），范围粒度恒为今天。**教训：给查询加筛选维度时，必须排查所有以"固定键/固定范围"取数的上游 UI。**

### 14. 热力图残影渲染到窗口顶部（2026-08-13 修复）
月/季/年视图滚动到底时，周网格格子残影出现在窗口顶部/标题栏区域。根因：**周网格的横向 ScrollView 嵌在外层纵向 ScrollView 内**，外层滚动时内层滚动视图内容层位置同步错乱（SwiftUI on macOS 嵌套滚动视图渲染缺陷）。**解法**：移除内嵌横向 ScrollView——月/季/年最多 53 列，最小窗宽（1080 - 侧栏 - padding）均可容纳；网格改 `GeometryReader` 宽度自适应（`cellSize` 上限 13，不足时按比例微缩，最小 6），顺带消除了年视图窄窗截断。**教训：macOS SwiftUI 中避免 ScrollView 嵌套，子视图宽度自适应是更稳的替代。**

### 15. 其他小坑（代码注释）
- 概览卡（OverviewCard）副标题单行 `.lineLimit(1)` 在窄窗口下会截断中文长文案 → 处理范式：**静态文案精简（如"前台应用活动时长（按会话统计）"→"按前台会话统计"）+ `minimumScaleFactor(0.7)` 缩放兜底**，与主数字的 `minimumScaleFactor(0.6)` 同策略，不改卡片固定高度（78pt）
- 面板窗口由 `NSWindowController` 常驻持有（`isReleasedWhenClosed = false`），SwiftUI 的 `onAppear`/`onDisappear` 在关窗/重开时**不可靠**（重开不触发 onAppear，会展示旧数据）→ 窗口级生命周期用 `NSWindow.willCloseNotification` / `didBecomeKeyNotification` 补齐（按 `identifier == "main"` 过滤），视图级 onAppear/onDisappear 只覆盖侧栏 Tab 切换

- `NSMenuItem` 内嵌 `NSHostingView` 必须显式指定 frame，否则尺寸为 0
- SQLite 绑文本用 `SQLITE_TRANSIENT` 让内部拷贝，防 Swift 字符串生命周期问题
- `dayFormatter` 用本地时区；所有「按天」统计都走 `dayRange` 的 `[00:00, 次日00:00)` 区间
- 周网格热力图的悬浮气泡画在内容顶部预留的 `tooltipSpace` 内（标签列同步 padding 对齐），避免首行气泡被外层 ScrollView 裁切；气泡与格子同坐标系。坐标换算用 `floor` 取整，直接 `Int()` 截断会让负偏移（顶部预留区）误判成第 0 行
- SQL `ORDER BY` 的结果**不能经字典中转**再返回（字典无序，排序丢失）——`appUsage` 曾因此导致「应用使用时长」列表乱序，GROUP BY 已保证唯一时直接按行序 append

### 16. 2026-08-18 全量 review 待修问题（未修，改动相关模块前先看）
- **高**：`AppCategoryMap.overrides` 主线程写、db.queue/全局队列并发读，Dictionary 非线程安全（改用锁或 confine 到单队列）
- **中**：①`MetricsAggregator.rotateIfNeeded` 跨天未重置 `delta*`，午夜后首次 drain 会把前一天增量并入新日聚合；②滚轮像素增量被当"次数"累加，与 SQL `COUNT(*)` 口径矛盾；③`KeyTranslator.keyboardLayout` 指向未 retain 的 CFData + 跨线程无同步；④`FatigueEngine.lastActivityDate` eventQueue 写/主线程读写竞争；⑤退出时最后会话经 `global().async` 二跳入库，`barrierSync` 罩不住，app_usage 尾段可能丢
- **低**：`dayRange` 固定 +86400 秒（DST 日差 1 小时，中国时区无影响）；`StatsViewModel.reload()` 并发无版本守卫可乱序；`buffer.stop()` 早于 tracker.stop() 收尾窗口丢毫秒级 events 尾巴

---

## 五、构建与发布

```bash
./Scripts/install.sh            # release 构建+签名+装 /Applications+启动（日常用这个）
./Scripts/install.sh --debug    # debug 构建
./Scripts/bundle.sh --run       # 只构建到 dist/ 并运行，不安装
./Scripts/setup-signing-cert.sh # 首次/换机时先跑：创建稳定签名证书
```

调试：`tail -f ~/Library/Application\ Support/InputMonitor/debug.log`。

排查进程是否存活：`pgrep -x InputMonitor` 在 macOS 上可能匹配不到（进程名长度截断），用 `ps aux | grep -i inputmonitor` 更可靠。

## 六、常见修改入口速查

| 需求 | 改哪里 |
|---|---|
| 加采集指标 | `InputEventRecord.Kind` → `MetricsAggregator` → `Database.migrate()`/`EventRepository` → `DaySummary` |
| 加应用分类关键词 | `AppCategoryMap.builtinRules`（小写包含匹配，先命中优先） |
| 加统计口径 | `EventRepository` 加查询 → `StatsViewModel.reload()` 接入 → `StatsWindowView` 展示 |
| 改疲劳规则 | `FatigueEngine`（状态机注释即规则文档） |
| 加设置项 | `SettingsStore`（Keys + @Published + didSet 持久化）→ `SettingsView` 对应卡片 |
| 改日期筛选/清理策略 | `StatsViewModel.selectedDate`（日粒度查询统一走 `selectedDay`）；`EventRepository.purgeExpiredData` |
| 排查不采集 | debug.log → 权限双开关 → 签名证书是否稳定 |
