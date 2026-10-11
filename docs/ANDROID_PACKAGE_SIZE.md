# Android 安装包体积

从 0.2.14 开始，默认 APK 仅包含 ARM64。x86_64 模拟器单独构建，不再把第二套 .NET 程序集、运行时、图形库和 OpenList 装进手机包。

完整构建入口：

```sh
pwsh scripts/build-android.ps1 -Configuration Release -RuntimeIdentifier android-arm64
```

模拟器使用 `-RuntimeIdentifier android-x64`。直接构建 csproj 时也可通过 `-p:RuntimeIdentifiers=android-x64` 选择模拟器；OpenList 原生库和自动准备步骤随目标架构选择。

Release 关闭调试符号和 CoreCLR 的 composite ReadyToRun，继续使用现有 CoreCLR、动态模块生命周期和内嵌 OpenList。0.2.14 未启用托管程序集裁剪；0.2.15 进一步裁剪了 Android 内嵌 OpenList，见下文。Release 配置不等同于商店正式签名；当前本机构建仍使用 Android 调试签名，可覆盖此前开发预览版。

实测安装包：

| 构建 | APK 大小 |
| --- | ---: |
| 0.2.13 Debug，ARM64 + x86_64 | 227.58 MiB |
| 0.2.14 Release，ARM64 | 111.71 MiB |
| 0.2.15 Release，ARM64，移动版 OpenList | 89.36 MiB |

减少约 51%。尚未达到 50 MB；后续缩减需要单独验证托管程序集裁剪以及 OpenList 依赖精简，不能直接删除动态插件需要的程序集。0.2.14 的 ARM64 OpenList 在 APK 内占约 45.3 MiB，0.2.15 降为 22.98 MiB。

包检查确认没有 x86_64 库，内嵌 OpenList 保留，并通过现有 ELF、16 KB 页对齐和 extractNativeLibs 校验。Pixel 4a 使用覆盖安装保留已有数据，安装和启动验收记录保存在 `/mnt/cache/data-cache/mpt-size-*`（临时证据按七天清理）。

## OpenList 移动构建（0.2.15）

`openlist-runtime.json` 固定 `mobile-quark-baidu-v1` 配置、四个显式驱动包以及同版本官方 lite 前端。应用使用的 Quark、BaiduNetdisk、本地文件和 WebDAV 保留。Quark/UC 共用一个驱动包；OpenList 的搜索代码会引入 OpenList 互联驱动，因此最终注册名称为 Quark、UC、BaiduNetdisk、Local、WebDav、OpenList。

驱动注册表之外，离线下载和种子接口也会间接引入迅雷、115 等驱动。`patches/mobile-runtime.patch` 对固定上游版本关闭这些 MPT 未使用的接口，返回明确的 501，而非空成功；离线工具列表为空。管理后台、鉴权、SQLite、文件存储、上传、下载和 WebDAV 的原实现保留。完整网页资源从约 56 MiB 降为官方 lite 的约 9.4 MiB；lite 前端不再内嵌完整的编辑/预览资源。

构建脚本使用 `git apply` 应用补丁，复用源码缓存时检查补丁已应用状态，并在前端资源版本变化时重新解包。每个 ABI 成功构建后写入 profile 标记，Android 构建看到旧的完整运行时没有该标记时会重建，避免悄悄继续打包旧的大文件。桌面的 OpenList 分发不受影响。

本次使用 Go 1.26.0（`-GoToolchain local`）和 NDK 27.0.12077973 构建。验证命令：

```sh
python3 tools/file-transfer/runtime/smoke-openlist-android.py \
  --device 10.33.0.155:5555 \
  --binary tools/file-transfer/runtime/stage/lib/arm64-v8a/libopenlist.so
```

该检查在 Android adb-shell 的独立临时实例中验证实际驱动集合、管理员登录、夸克/百度凭据字段、本地上传和 WebDAV 下载、管理网页，并清理实例。它不等同于真实网盘账号的上传下载验收。本轮没有重新登录真实网盘或执行真实夸克/百度上传。APK 另行通过原有内嵌运行时校验（ELF、16 KB 页对齐、extractNativeLibs）。
