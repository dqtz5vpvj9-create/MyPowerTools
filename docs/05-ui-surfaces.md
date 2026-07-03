# UI Surface 规范

## UI 目标

MyPowerTools Shell 统一导航、主题、布局、通知、命令、设置。模块只贡献局部 surface。

```text
Shell 负责外壳
Module 负责内容
Runtime 负责数据绑定
Platform Pack 负责系统交互
UI System 负责视觉一致性
```

## Surface 类型

| Surface | 必需 | 说明 |
|---|---:|---|
| DashboardCard | 是 | 首页卡片 |
| DetailPage | 是 | 模块详情 |
| SettingsSurface | 是 | 设置中心页面 |
| CommandProvider | 是 | 命令面板动作 |
| LogsProvider | 建议 | 日志面板 |
| NotificationProvider | 可选 | 通知中心输入源 |
| WebViewSurface | 可选 | 复杂工具页面 |
| NativeWindow | 受限 | 平台专属窗口，需声明原因和降级方案 |

## Dashboard Card

数据模型：

```json
{
  "title": "Remote Notifications",
  "state": "running",
  "summary": "1086 messages · Persistent · Server online",
  "metrics": [
    { "label": "Messages", "value": "1086" },
    { "label": "Shown", "value": "0" }
  ],
  "actions": [
    { "id": "open", "title": "Open", "style": "primary" },
    { "id": "togglePersistent", "title": "Persistent", "style": "secondary" }
  ]
}
```

布局由 Shell 组件 `MptDashboardCard` 统一控制。模块只提供数据和动作。

```text
┌────────────────────────────────────┐
│ icon  Remote Notifications   state │
│       1086 messages · Persistent   │
│       Server online                │
│                                    │
│ Messages 1086      Shown 0         │
│                                    │
│ [Open] [Persistent]                │
└────────────────────────────────────┘
```

## Detail Page

Host 统一页面骨架：

```text
Header
  title, state, summary, primary actions

Body
  module-specific content
  metrics
  timeline
  diagnostics

Footer
  logs
  settings shortcut
  package info
```

模块只能填充业务区域。导航栏、标题栏、主题、窗口管理由 Shell 统一控制。

## Settings Surface

设置页用 schema 渲染，复杂模块可以追加受控自定义区域。

```json
{
  "sections": [
    {
      "id": "delivery",
      "title": "通知投递",
      "items": [
        {
          "type": "toggle",
          "key": "desktopToast",
          "label": "桌面通知",
          "default": true
        },
        {
          "type": "toggle",
          "key": "mobilePush",
          "label": "手机推送",
          "default": false
        }
      ]
    }
  ]
}
```

设置项使用 Shell 组件：

```text
MptSettingsSection
MptToggleSetting
MptTextSetting
MptNumberSetting
MptPathSetting
MptSecretSetting
MptPortSetting
MptHotkeySetting
MptEnumSetting
```

## Command Palette

命令面板聚合全部模块命令：

```text
Run Remote Command: Decode Kernel Stack
Open Remote Notifications
Mute Notification Tag: app
Apply ScreenEase Profile: Night
Restart Doubao Runtime
Restart SmartBird Service
Apply ADB Forwarding Rule
```

命令支持：

```text
静态命令
动态搜索命令
带参数命令
最近命令
收藏命令
快捷键绑定
进度显示
结果跳转
```

Command Palette 的 provider 模型遵循 PowerToys 的经验：顶层命令先可用，动态 provider 后台补充，慢 provider 不阻塞 Shell。

## Notification Center

通知中心是底座能力。模块可以作为输入源，也可以接收投递结果。

```text
Remote messages
Module events
Command results
Health alerts
Permission prompts
System diagnostics
```

事件格式：

```json
{
  "sourceModuleId": "android-tools.notifications",
  "level": "info",
  "title": "Codex hook message",
  "body": "Service reachable and SSE connected",
  "tags": ["lixinrui", "codex"],
  "actions": [
    { "id": "open", "title": "Open" },
    { "id": "mute-tag", "title": "Mute tag" }
  ]
}
```

## Logs Viewer

统一日志入口：

```text
按模块过滤
按级别过滤
实时 tail
命令执行日志
Broker 审计日志
导出诊断包
```

## UI 风格基线

```text
左侧一级模块导航
顶部全局搜索和命令面板入口
中部 Dashboard 或模块详情
右侧可选上下文面板
底部状态栏显示运行状态和最近错误
```

## 模块页面准入规则

| 规则 | 要求 |
|---|---|
| 主题 | 遵循 Shell theme token |
| 字体 | 使用 Shell typography token |
| 间距 | 使用 Shell spacing token |
| 圆角 | 使用 Shell radius token |
| 图标 | 使用 Shell icon registry |
| 权限提示 | 交给 Broker 和 Shell |
| 错误提示 | 使用统一 `MptErrorView` |
| 空状态 | 使用统一 `MptEmptyState` |
| 长任务 | 返回 progress event |
| 日志 | 进入 Logs Viewer |
| 设置 | 进入 Settings Center |
| 自定义窗口 | 默认不进入首批范围 |

详细视觉约束见 `docs/16-ui-guardrails.md`、`ui/design-tokens.json`、`ui/component-contracts.md`、`ui/visual-regression-matrix.md`。
