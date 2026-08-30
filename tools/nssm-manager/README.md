# NSSM 服务管理器

`nssm-manager` 是 NSSM 2.24-101 的 C# 兼容迁移，并提供 MyPowerTools Avalonia 管理界面。

后端 `nssm-manager.exe` 同时提供命令行和 Windows SCM 服务入口。配置继续使用 NSSM 的 `HKLM\SYSTEM\CurrentControlSet\Services\<name>\Parameters` 布局。

## 构建

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File tools/nssm-manager/build.ps1
```

开发版更新：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId nssm-manager
```

## 上游

兼容基线：`nssm-2.24-101-g897c7ad`。NSSM 官网声明该项目属于 public domain。
