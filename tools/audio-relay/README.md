# AudioRelay

AudioRelay 以独立 Tool SDK 工具接入 MyPowerTools。页面检测官方桌面端的安装版本、路径和运行状态，并提供启动、下载以及两种官方配置向导入口。

这项集成不捆绑 AudioRelay，也不读取或伪造它没有公开提供的连接状态。音频源、接收设备、质量和延迟参数仍由 AudioRelay 自己管理。

## 构建与开发版

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File tools\audio-relay\build.ps1
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId audio-relay
```
