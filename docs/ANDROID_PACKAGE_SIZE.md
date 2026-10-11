# Android 安装包体积

从 0.2.14 开始，默认 APK 仅包含 ARM64。x86_64 模拟器单独构建，不再把第二套 .NET 程序集、运行时、图形库和 OpenList 装进手机包。

完整构建入口：

```sh
pwsh scripts/build-android.ps1 -Configuration Release -RuntimeIdentifier android-arm64
```

模拟器使用 `-RuntimeIdentifier android-x64`。直接构建 csproj 时也可通过 `-p:RuntimeIdentifiers=android-x64` 选择模拟器；OpenList 原生库和自动准备步骤随目标架构选择。

Release 关闭调试符号和 CoreCLR 的 composite ReadyToRun，继续使用现有 CoreCLR、动态模块生命周期和内嵌 OpenList。当前并未启用托管程序集裁剪，也没有移除网盘驱动。Release 配置不等同于商店正式签名；当前本机构建仍使用 Android 调试签名，可覆盖此前开发预览版。

实测安装包：

| 构建 | APK 大小 |
| --- | ---: |
| 0.2.13 Debug，ARM64 + x86_64 | 227.58 MiB |
| 0.2.14 Release，ARM64 | 111.71 MiB |

减少约 51%。尚未达到 50 MB；后续缩减需要单独验证托管程序集裁剪以及 OpenList 依赖精简，不能直接删除动态插件需要的程序集。当前 ARM64 OpenList 在 APK 内仍占约 45.3 MiB。

包检查确认没有 x86_64 库，内嵌 OpenList 保留，并通过现有 ELF、16 KB 页对齐和 extractNativeLibs 校验。Pixel 4a 使用覆盖安装保留已有数据，安装和启动验收记录保存在 `/mnt/cache/data-cache/mpt-size-*`（临时证据按七天清理）。
