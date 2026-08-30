# 输入法管理器

独立管理当前 Windows 用户已启用的输入法，覆盖搜狗拼音「输入法管理器」里真正有用的部分：设默认、挑选常用项、调整切换顺序和语言/布局快捷键。不安装、不捆绑任何输入法厂商软件。

## 能力

- 列出当前用户已启用的输入法和本机已注册的文字服务 / 键盘布局。
- 添加或移除切换列表中的输入法，上移 / 下移改变 Win+空格 与 Ctrl+Shift 顺序。
- 设置默认输入法。
- 设置语言切换和布局切换快捷键（左 Alt+Shift、Ctrl+Shift、波浪号、未分配）。
- 所有写入都只改当前用户（HKCU / `input.dll`），不需要管理员权限，也不会卸载其它软件。

## Tool SDK 入口

`sdk-tool/` 是 MyPowerTools Tool SDK 交付：

- 根目录包含 `tool.json`。
- Surface 通过 NuGet 引用 `MyPowerTools.AvaloniaSdk` 与 `MyPowerTools.ToolSdk`。
- 读取和写入在隔离 `stdio-jsonrpc` Runtime 中执行。

## 构建

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\build.ps1
```

日常开发更新：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId ime-manager
```

## 安全边界

- 不安装或卸载输入法产品，只启用 / 停用当前用户已经注册的布局和文字服务。
- 拒绝写成空列表，避免系统没有可用输入法。
- 应用失败时尝试恢复应用前的列表。
