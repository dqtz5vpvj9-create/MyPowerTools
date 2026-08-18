# macOS 输入活动与窗口活动监测调研报告

> 调研日期：2026-07-28
> 目标：在本机 macOS 上检测鼠标、键盘的操作次数、轨迹、频次、每次操作时间，并追踪前台应用窗口活动时长，全部数据本地保存、禁止联网。

---

## 1. 需求拆解

| 需求 | 指标 | 数据来源 |
| --- | --- | --- |
| 键盘操作次数 | keyDown 计数（剔除长按重复） | 全局键盘事件 |
| 键盘按住时长 | keyUp.timestamp - keyDown.timestamp（按 keyCode 配对） | 全局键盘事件 |
| 具体按键内容 | keyCode → 字符映射（可开关） | 全局键盘事件 + 键盘布局 |
| 鼠标点击次数 | 左/右键 down 计数 | 全局鼠标事件 |
| 鼠标轨迹/距离 | mouseMoved 采样点序列 + 位移累计 | 全局鼠标事件 |
| 滚轮次数 | scrollWheel 计数与行数 | 全局鼠标事件 |
| 操作频次 | 滑动窗口 events/min、按小时/天聚合 | 上述事件时间戳 |
| 屏幕活动（窗口维度） | 前台 App / 窗口标题、活动时长 | NSWorkspace + Accessibility |
| 疲劳提醒 | 活动时间累计、阈值触发、全屏提醒 | 上述活动时间并集 |

---

## 2. 输入事件监听方案对比

### 2.1 方案总览

| 维度 | CGEventTap（Quartz） | NSEvent 全局监听 | IOHIDManager（IOKit） |
| --- | --- | --- | --- |
| 系统层级 | WindowServer / Quartz 事件流 | AppKit 事件分发层 | 驱动层 HID 设备 |
| 键盘事件 | keyDown / keyUp / flagsChanged 完整 | keyDown 基本可用，keyUp / flagsChanged 部分场景缺失 | 原始 HID 报告，需自行解析 usage page |
| 鼠标事件 | 移动/点击/滚轮/拖拽完整 | 支持，但高频移动监听灵活性差 | 原始报告，需自行换算坐标 |
| 事件时间戳 | 纳秒级（mach absolute time） | 秒级（NSTimeInterval） | 设备时间戳 |
| 屏幕坐标 | 自带全局坐标 | 自带 | 需自行维护指针状态 |
| 实现复杂度 | 中（C 回调 + run loop source） | 低（Block API） | 高 |
| 权限要求 | 辅助功能（监听）+ 输入监控（macOS 10.15+，键盘） | 辅助功能 | 输入监控 |
| 拦截能力 | 可（listen-only 或过滤改写） | 否（纯被动观察） | 否 |

### 2.2 结论：采用 CGEventTap（listen-only）

理由：

1. **事件覆盖完整**：键盘（keyDown/keyUp/flagsChanged）、鼠标（mouseMoved/leftMouseDown/rightMouseDown/scrollWheel 等）全部类型都能拿到，满足"次数、轨迹、时长、频次"全部指标。
2. **数据精度高**：每个 `CGEvent` 自带纳秒级 `timestamp` 与全局屏幕 `location`，按键按住时长可通过 keyDown/keyUp 配对精确计算。
3. **成熟度与生态**：KeyCastr、Mac Mouse Fix 等开源项目均基于该机制；有官方文档与大量可参考实现。
4. NSEvent 方案事件覆盖不全，IOHIDManager 属驱动层过度设计（需要自行解析 HID usage page、维护指针状态），对本需求是杀鸡用牛刀。

### 2.3 CGEventTap 关键工程细节

- **创建方式**：`CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .listenOnly, eventsOfInterest: mask, callback: ..., userInfo: ...)`。
- **事件掩码**：`(1 << keyDown) | (1 << keyUp) | (1 << flagsChanged) | (1 << mouseMoved) | (1 << leftMouseDown) | (1 << leftMouseUp) | (1 << rightMouseDown) | (1 << rightMouseUp) | (1 << scrollWheel) | (1 << leftMouseDragged) | (1 << rightMouseDragged)`。
- **挂接 run loop**：创建返回 `CFMachPort`，需 `CFMachPortCreateRunLoopSource` 后 `CFRunLoopAddSource` 到主 run loop 的 `.commonModes`。
- **回调轻量化**：回调内禁止重活——只做事件字段拷贝并入串行 `DispatchQueue`，聚合与落库在后台执行，否则回调超时会触发系统禁用 tap。
- **自动重挂**：监听 `.tapDisabledByTimeout` 与 `.tapDisabledByUserInput` 事件，收到后调用 `CGEvent.tapEnable(tap:enable:)` 重新启用。
- **长按去重**：`CGEventGetIntegerValueField(.keyboardEventAutorepeat)` 为 1 的 keyDown 是长按重复，不计为新按键（但可计入活动心跳）。
- **键码映射字符**：`UCKeyTranslate`（Carbon TextInputSources）按当前键盘布局把 keyCode + modifier 映射为 Unicode 字符；需处理死键状态（本工具只统计用途，可忽略死键组合，直接映射基础字符）。

---

## 3. 屏幕活动（窗口维度）方案

> 按需求，不监控屏幕内容变化，只记录"什么应用的窗口在活动、窗口活动时长"。

### 3.1 候选方案

| 方案 | 能拿到什么 | 权限 | 结论 |
| --- | --- | --- | --- |
| ScreenCaptureKit 定时截屏 | 屏幕内容、变化率 | 屏幕录制 | **弃用**（需求明确不要内容监控） |
| CGWindowListCopyWindowInfo | 窗口列表快照（owner/title/层级） | 无（标题需屏幕录制权限在 10.15+ 受限） | 可作补充 |
| **NSWorkspace 通知 + AX API** | 前台 App 切换、聚焦窗口标题 | 辅助功能 | **采用** |

### 3.2 采用方案：NSWorkspace + Accessibility

1. **前台 App 追踪**：监听 `NSWorkspace.didActivateApplicationNotification`，拿到 `NSRunningApplication`（bundleIdentifier、localizedName、激活时间）。两次切换之间的区间即该 App 的前台时长。
2. **窗口标题**：对前台 App 的 `AXUIElementCreateApplication(pid)` 读取 `kAXFocusedWindowAttribute` → `kAXTitleAttribute`，即当前窗口标题（如浏览器标签页标题、文档名）。
3. **周期心跳兜底**：每 30s 记录一次当前前台 App + 窗口标题快照。作用：(a) 同一 App 内切换窗口（如浏览器换标签）能被感知；(b) 统计"窗口活动强度"（有心跳 = 用户在线）；(c) 锁屏/空闲检测（心跳配合 CGEventSource 空闲秒数）。
4. **会话模型**：`FrontAppSession(bundleID, appName, windowTitle, start, end)`，App 切换或标题变化时结束旧会话、开启新会话，批量落库。
5. **应用类型分类**：bundleID 关键词内置映射（开发/浏览器/办公/设计/社交/影音/其他），支持用户自定义覆盖。

**优势**：零截屏开销（比 ScreenCaptureKit 低两个数量级），复用辅助功能权限，**无需屏幕录制 TCC 权限**。

---

## 4. 权限模型（TCC）

| 权限 | 用途 | 检测/申请方式 |
| --- | --- | --- |
| 辅助功能（Accessibility） | CGEventTap 监听事件、AX 读取窗口标题 | `AXIsProcessTrustedWithOptions(kAXTrustedCheckOptionPrompt)` |
| 输入监控（Input Monitoring） | macOS 10.15+ 键盘事件监听 | 无直接查询 API；以"创建 tap 返回 nil / 收不到键盘事件"为未授权信号，引导跳转系统设置并轮询恢复 |

要点：

- TCC 授权记录绑定 **签名身份 + bundle id**。开发期必须固定 ad-hoc 签名与 bundle id，否则每次重打包都要重新授权。
- 权限变更后 App 通常需要重启才能生效，引导页需提供"重新检测"轮询与重启提示。
- 弹系统授权框由 App 主动触发（`AXIsProcessTrustedWithOptions` 传 prompt=true；输入监控则跳转 `x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent`）。

---

## 5. 隐私边界

1. **密码输入天然屏蔽**：任何应用调用 `EnableSecureEventInput`（如密码框聚焦、锁屏）期间，系统不向 CGEventTap 下发键盘事件——这是 macOS 的安全设计，恰好保证本工具不可能记录密码。
2. **按键内容可选**：提供"隐私模式"，开启后只落 keyCode 计数，不落字符；落库字符默认也仅为统计热区用途。
3. **窗口标题敏感**：标题可能含文档名/网页标题，仅存本地 SQLite，日志不输出明文。
4. **日志红线**：日志只输出运行状态（tap 创建/重挂、flush 条数、采样计数），禁止输出按键字符与窗口标题。

---

## 6. 禁联网设计与验证

设计：

- 零第三方依赖（SQLite 用系统 libsqlite3，图表用系统 SwiftUI Charts），无 URLSession / Network.framework / CFNetwork 任何调用。
- 打包 entitlements 不包含 `com.apple.security.network.client` / `network.server`。

验证方法（交付时自测）：

```bash
# 进程运行中检查是否有任何 socket 连接
lsof -i -n -P | grep InputMonitor   # 期望无输出
# 静态检查二进制未链接网络符号
nm -m InputMonitor.app/Contents/MacOS/InputMonitor | grep -iE "URLSession|NWConnection|connect\(" # 期望无输出
```

---

## 7. 现成工具对标

| 工具 | 类型 | 与本项目关系 |
| --- | --- | --- |
| KeyCastr | 开源，按键可视化 | 验证 CGEventTap 监听方案可行性 |
| WhatPulse | 商业，击键/点击/鼠标距离统计 | 功能对标（次数、距离），本工具增加轨迹/窗口/疲劳提醒 |
| ActivityWatch | 开源，时间追踪（窗口+AFK） | 验证窗口追踪方案可行性，但其无输入级指标 |
| KeyLog | 开源（Tauri），跨平台输入统计 | 交互参考 |

结论：无单一现成工具同时满足"输入级全指标 + 窗口时长 + 疲劳提醒 + 纯本地禁联网"，自研合理。

---

## 8. 指标实现路径（落地映射）

| 指标 | 实现 |
| --- | --- |
| 按键/点击/滚轮次数 | tap 回调按类型计数，keyDown 需 `ARepeat==0` |
| 按住时长 | 内存字典按 keyCode 存 keyDown 时间，keyUp 配对求差 |
| 每分钟频次 | 滑动窗口（环形数组按分钟 bucket）events/min |
| 鼠标轨迹 | mouseMoved 双阈值采样（距上点 ≥30px 或间隔 ≥50ms） |
| 鼠标移动距离 | 相邻采样点欧氏距离累加，按日聚合 |
| 按键内容 | UCKeyTranslate(keyCode, modifiers) → String，隐私模式下 nil |
| 窗口活动时长 | App 切换事件 + 30s 心跳生成会话区间 |
| 日/月/季/年看板 | daily_stats 预聚合表 + SQL group by |
| 疲劳值 | 输入活动时间与窗口活动时间并集累计，每分钟 +5 点（默认 20min=100，可配置换算） |

---

## 9. 可实施结论

1. **采集层**：CGEventTap（listen-only）全局监听键盘/鼠标；NSWorkspace 通知 + AX API + 30s 心跳追踪前台窗口。
2. **权限层**：辅助功能 + 输入监控两类，固定 ad-hoc 签名与 bundle id 避免重复授权。
3. **存储层**：系统 SQLite3，环形内存缓冲 + 批量事务（5s 或 500 条 flush），events / app_usage / daily_stats 三表。
4. **提醒层**：疲劳值状态机（100 触发 → 跳过则值 100、阈值 120 → 再触发；完成休息清零），全屏渐变白窗口 + 倒计时 + 跳过，结束淡出 + NSSound 提示音。
5. **展示层**：NSStatusItem 状态栏（疲劳值% + 活动时长 + 菜单），SwiftUI 统计面板（日/月/季/年热力、分应用类型），设置页全量配置。
6. **红线**：禁联网（零网络调用 + 无 network entitlement）、密码期间系统屏蔽、日志不落敏感明文。

方案风险低、全部为系统公开 API、可立即实施。
