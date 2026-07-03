# UI Component Contracts

## 组件原则

模块 surface 使用 Shell 组件，不直接控制窗口、导航、主题、通知、权限和日志。

## 必备组件

| 组件 | 输入 | 输出 |
|---|---|---|
| `MptDashboardCard` | title、state、summary、metrics、actions | 首页卡片 |
| `MptModuleHeader` | module id、title、state、primary actions | 详情页头部 |
| `MptStatusPill` | ToolState、summary | 统一状态标签 |
| `MptActionBar` | primary、secondary、overflow actions | 统一动作区 |
| `MptSettingsSection` | section schema | 设置分组 |
| `MptSettingRow` | setting item schema | 设置项 |
| `MptDataTable` | columns、rows、actions | 数据表 |
| `MptTimeline` | events | 时间线 |
| `MptLogViewer` | module id、cursor | 日志视图 |
| `MptEmptyState` | title、body、action | 空状态 |
| `MptErrorView` | error code、message、retry、log cursor | 错误状态 |
| `MptLoadingView` | message、progress | 加载状态 |
| `MptPermissionPrompt` | privileged action request | 权限提示 |

## 禁用行为

```text
模块 surface 不创建自定义 Window
模块 surface 不覆盖 Application styles
模块 surface 不写硬编码主题色
模块 surface 不自建日志面板
模块 surface 不自建权限对话框
模块 surface 不自建通知弹窗
```
