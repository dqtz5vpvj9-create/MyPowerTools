# ADR 0005 平台能力包

## 决策

系统能力集中在 Platform Packs 中实现。

## 背景

托盘、全局热键、通知、服务、自启动、显示控制、端口转发、提权、secret store 都存在平台差异。

## 结果

模块声明 capability，Runtime 根据当前平台选择 provider。高权限动作通过 Broker 执行。

## 影响

```text
跨平台边界清晰
模块不直接调用系统命令
缺失能力可显示降级状态
```
