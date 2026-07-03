# ADR-0006 Runner 与 Shell 分进程

## 决策

MyPowerTools 使用 `Runner` 和 `Shell.Avalonia` 双进程控制面。

```text
Runner  长期常驻，管理 runtime、module host、tray、hotkey、settings、broker。
Shell   独立 Avalonia UI 进程，管理 Dashboard、Settings、Detail、Logs、Command Palette。
```

## 原因

PowerToys 的 Runner 与 Settings UI 分离，复杂 UI 崩溃不会直接破坏 Runner 控制面。MyPowerTools 采用同样的稳定性边界，并把通信升级为 typed Host Control Protocol。

## 影响

- Shell 可退出和重启。
- Runner 可无 Shell 常驻。
- Shell 只能通过 Host Control IPC 访问 Runtime。
- 模块 lifecycle 归 Runner 管。
- CommandIndex 常驻 Runner。
