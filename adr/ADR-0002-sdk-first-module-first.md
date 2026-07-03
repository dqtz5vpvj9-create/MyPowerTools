# ADR 0002 SDK-first 与 Module-first

## 决策

MyPowerTools 先定义 SDK、协议和 entrypoint 契约，工具主动实现模块协议。

## 背景

所有工具源码可控。继续让 Host 逐个适配工具会让 Host 长期堆积工具专属逻辑。

## 结果

Host 只认识模块契约。工具通过 package/module manifest、typed protocol、UI Surface、capability declaration 接入。

## 影响

```text
新增工具标准化
Host 可长期保持简洁
现有工具需要写模块层
不同语言通过 gRPC Native IPC 接入
可信 .NET 模块通过 InProc SDK 接入
```
