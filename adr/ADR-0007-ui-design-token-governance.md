# ADR-0007 UI 设计约束

## 决策

MyPowerTools UI 采用 token、组件、surface schema、视觉回归共同治理。

## 规则

```text
颜色、字体、间距、圆角、阴影、动效来自 design tokens
模块 surface 使用 Shell components
模块不能注入全局样式
模块不能自建权限、通知、日志、设置外壳
所有默认启用模块通过 UI gate
```

## 原因

工具数量会持续增加。缺少 UI 约束时，各模块会逐渐形成割裂外观。Shell 需要成为唯一视觉系统来源。

## 影响

- 新模块需要通过 `mpt ui check`。
- 自定义 WebView surface 需要 `MptWebViewFrame`。
- 视觉回归成为 release gate。
