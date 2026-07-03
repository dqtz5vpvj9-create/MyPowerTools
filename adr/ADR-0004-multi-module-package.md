# ADR 0004 Multi-module Package

## 决策

MyPowerTools 支持一个 package 导出多个 module。

## 背景

AndroidTools 当前实际包含 Notifications、Remote Commands、Process Monitor 三个独立能力。未来也会出现一个工具包包含多个用户可见工具的情况。

## 结果

Package 是安装和共享依赖单位，Module 是用户可见工具单位。

## 影响

```text
AndroidTools 可拆成三个模块
共享 sidecar 和数据目录
Dashboard 展示独立工具
更新和安装仍按 package 管理
```
