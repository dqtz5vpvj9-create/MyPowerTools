# UI 约束

## 目标

MyPowerTools 的 UI 以 Shell 统一外观为准。模块贡献数据、动作、局部 surface。视觉系统由 token、组件、布局、状态和视觉回归共同约束。

## 技术基线

```text
Avalonia FluentTheme
Shell design tokens
Mpt component library
Mpt icon registry
Mpt surface schema
Visual regression gate
```

## Token 体系

所有颜色、字体、间距、圆角、阴影、动效都来自 `ui/design-tokens.json`。

| 类型 | 示例 |
|---|---|
| Color | `color.bg.app`、`color.text.primary`、`color.status.running` |
| Typography | `type.title.large`、`type.body`、`type.caption` |
| Spacing | `space.4`、`space.8`、`space.12`、`space.16`、`space.24` |
| Radius | `radius.sm`、`radius.md`、`radius.lg` |
| Elevation | `elevation.card`、`elevation.flyout` |
| Motion | `motion.fast`、`motion.normal` |

模块 XAML、schema、WebView surface 不直接写硬编码色值、字体名、任意 margin。例外项需要在 package manifest 中声明，并通过 UI gate。

## Shell 布局

| 区域 | 规则 |
|---|---|
| Navigation | 左侧一级导航，宽度使用 token，图标和文字对齐 |
| Top Bar | 全局搜索、命令面板入口、Runner 状态、用户动作 |
| Content | 统一 content margin，最大内容宽度由 Shell 控制 |
| Context Panel | 右侧可选面板，只用于诊断、日志、详情补充 |
| Status Bar | 最近错误、后台任务、broker 状态 |

Dashboard 卡片使用统一网格：

```text
最小卡片宽度 320
推荐卡片宽度 360
卡片间距使用 space.16
卡片内部间距使用 space.16 / space.12
主要按钮不超过 2 个
次要动作进入 overflow menu
```

## 组件库

模块 surface 只使用以下 Shell 组件：

```text
MptDashboardCard
MptModuleHeader
MptStatusPill
MptMetricGrid
MptActionBar
MptCommandButton
MptSettingsSection
MptSettingRow
MptDataTable
MptTimeline
MptLogViewer
MptEmptyState
MptErrorView
MptLoadingView
MptPermissionPrompt
MptDiagnosticPanel
MptOverflowMenu
```

组件职责：

| 组件 | 统一内容 |
|---|---|
| `MptStatusPill` | running、degraded、error、disabled、stopped 等状态颜色和文案 |
| `MptActionBar` | 主次按钮顺序、loading、disabled、danger 状态 |
| `MptSettingsSection` | 标题、说明、分组间距、保存状态 |
| `MptErrorView` | 错误图标、错误码、重试动作、日志入口 |
| `MptPermissionPrompt` | broker 权限描述、影响范围、审计入口 |
| `MptLogViewer` | tail、过滤、导出诊断包 |

## 模块 surface 禁区

```text
全局样式覆盖
自定义顶栏
自定义侧边栏
自定义窗口 chrome
自定义通知弹窗
自定义权限弹窗
自定义日志窗口
硬编码字体
硬编码主题色
无限制 WebView 自由布局
```

复杂 WebView surface 需要包在 `MptWebViewFrame` 中，并声明：

```text
preferredSize
minSize
allowedCommands
themeBridge
navigationPolicy
securityPolicy
fallbackSurface
```

## 状态约束

每个模块页面都要覆盖以下状态：

```text
loading
ready
empty
degraded
error
permission-required
offline
updating
disabled
```

状态渲染由 Shell 组件控制。模块只返回状态数据、错误码、建议动作和日志游标。

## 设置页约束

设置优先 schema 渲染。自定义设置 surface 只用于复杂交互，并保留统一保存、撤销、重置、冲突提示。

```text
Host 是设置单一写入方
设置项有 key、label、description、default、validation
secret 使用 SecretBroker
端口使用 PortSetting
路径使用 PathSetting
热键使用 HotkeySetting
危险动作使用 DangerZoneSection
```

## Command Palette 约束

```text
命令标题使用动词开头
命令副标题显示模块名和目标对象
危险命令带 danger 标记
需要权限的命令带 shield 标记
长任务命令返回 progress event
命令结果可跳转到 Detail 或 Logs
```

命令排序：

```text
固定命令
最近命令
模块常用命令
动态搜索结果
fallback 结果
```

## 可访问性

```text
全键盘可达
焦点可见
状态不能只靠颜色表达
所有图标有 label
错误和权限提示可被屏幕阅读器读取
动效可关闭
文本支持缩放
```

## 视觉回归

`mpt ui snapshot` 生成标准截图。`mpt ui check` 对比 baseline。

覆盖矩阵：

```text
Dashboard light
Dashboard dark
Dashboard compact
Module Detail ready
Module Detail degraded
Module Detail error
Settings light
Settings dark
Command Palette empty
Command Palette results
Logs Viewer
Permission Prompt
```

分辨率矩阵：

```text
1366x768
1440x900
1920x1080
2560x1440
```

平台矩阵：

```text
Windows
macOS
Linux
```

## UI gate

新模块进入 Dashboard 前，需要通过：

```text
schema validate
surface validate
token lint
component usage scan
visual regression
accessibility scan
empty/error/loading state scan
```

通过后才能进入默认启用列表。
