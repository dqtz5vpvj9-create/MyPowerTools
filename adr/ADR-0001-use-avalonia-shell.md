# ADR-0001 使用 Avalonia Shell

## 决策

MyPowerTools Shell 使用 Avalonia。Shell 作为独立 UI 进程运行，通过 Host Control IPC 与 Runner 通信。

## 原因

Avalonia 支持 Windows、macOS、Linux 桌面，符合 MyPowerTools 跨平台目标。Shell 独立进程可以降低 UI 崩溃对 Runner 和模块 runtime 的影响。

## 影响

- WPF/WinUI 不作为主 Shell。
- Shell UI 统一由 `MyPowerTools.UI` token 和组件治理。
- 平台差异进入 `MyPowerTools.Platform.*`。
- Shell 不直接访问模块进程和 broker。
