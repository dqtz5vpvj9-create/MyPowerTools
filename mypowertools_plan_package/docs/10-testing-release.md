# Testing and Release

## 测试层级

| 层级 | 内容 |
|---|---|
| Schema tests | package、module、command、status、settings、ui surface |
| Protocol tests | proto compatibility、typed errors、stream、cancellation |
| Runtime tests | registry、loader、supervisor、settings store、event bus |
| Transport tests | InProc、Named Pipe、UDS、HTTP facade、stdio compat |
| UI tests | tokens、surface schema、screenshot baseline、keyboard smoke |
| Platform tests | Windows provider、Linux provider、macOS provider |
| Broker tests | privileged action、secret、service、network、audit log |
| Module tests | 每个 module 的 status、commands、settings、logs |
| Package tests | install、update、hash、rollback |

## UI 验证

```bash
mpt validate ui ./modules/screenease
mpt ui snapshot --module screenease --theme light
mpt ui snapshot --module screenease --theme dark
mpt ui snapshot --module screenease --density compact
mpt ui check --module screenease
```

必须检查：

```text
无硬编码颜色
无散落 spacing
Dashboard 使用标准卡片
DetailPage 使用标准 scaffold
Settings 使用 schema renderer
日志使用 LogsViewer
命令使用 CommandProvider
权限提示走 Broker
light/dark/compact 截图基线存在
```

## 发布门

```text
schemas valid
package hashes valid
proto compatibility valid
transport smoke tests pass
UI quality gate pass
broker audit tests pass
rollback tests pass
```

## 诊断包

诊断包包含：

```text
Host logs
module logs
Broker audit log
package registry snapshot
module manifest snapshot
settings revision snapshot
runtime state snapshot
transport health snapshot
UI screenshot baseline diff
```

## 性能检查

| 项目 | 检查方式 |
|---|---|
| 首屏 | T0 static index 可渲染 Dashboard skeleton |
| 命令面板 | static commands 可先行，dynamic commands 后台补齐 |
| sidecar 数量 | package runtime pool 共享，避免按 module 线性增长 |
| 事件流 | stream 有 backpressure 和断线恢复 |
| 日志流 | cursor tail，不一次性读取大文件 |
| UI | 页面切换、卡片渲染、截图回归无明显退化 |
