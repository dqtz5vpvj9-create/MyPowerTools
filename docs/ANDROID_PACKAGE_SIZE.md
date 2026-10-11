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
| 0.2.16 Release，ARM64，SDK 裁剪和移动 Shell | 50.98 MiB |

相对最初的双 ABI Debug 包减少约 78%。0.2.16 为约 53.46 MB，仍略高于严格的 50 MB；保留现有动态插件需要的程序集。0.2.14 的 ARM64 OpenList 在 APK 内占约 45.3 MiB，0.2.15 降为 22.98 MiB。

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


## 托管代码和 Shell（0.2.16）

Android 改为引用 `MyPowerTools.Shell.Mobile`：链接原 Shell 的视图、ViewModel、服务及 AXAML，保留同一程序集名称、资源 URI、宿主契约和动态模块生命周期。移动构建不引用 `Avalonia.Desktop`，不编译桌面 `Program.Main`；桌面 Shell 源码和项目保持原样。共享平台 API 中仍有桌面实现引用，本次移除的是 Android 无用的桌面渲染后端，未重写平台 API。

Release 使用 `PublishTrimmed=true` 和 `TrimMode=partial`，保留 JSON 反射序列化，并显式保留四个内置模块、手机 Surface、SSH.NET 和 BouncyCastle。以后增加动态模块时，应同步更新保留列表并重新执行手机验收；不能只以构建成功判断插件可用。Debug 继续不裁剪。`UseDefaultPublishRuntimeIdentifier=false` 防止启用裁剪后 .NET SDK 把构建主机的 linux-x64 自动填成 Android 的 RID；ARM64 默认值和 x64 模拟器选择仍有效。

两个手机模块固定使用同一 BouncyCastle 2.6.2。APK 仅保留通知模块中的一份；安装后的目录初始化在写 catalog stamp 前复制到 Remote Commands 目录，两个 collectible 加载上下文仍各自从模块目录加载。模块状态和秘密不在这个只读目录内。验收脚本用字节比较确认两个源包的库一致，无额外校验和机制。

OpenList 的 `mobile-quark-baidu-v2` 另行去掉 Bleve 和 Meilisearch 注册，保留数据库搜索、管理网页以及原有六个驱动。应用的聊天搜索和网盘目录浏览不依赖这两个全文检索引擎。内嵌运行时从 72.19 MiB 降为 65.44 MiB，APK 内压缩后约 20.74 MiB。

包结构验收：

```sh
python3 scripts/verify-android-package-size.py \
  --apk artifacts/build/bin/MyPowerTools.Android/release/com.mypowertools.android-Signed.apk \
  --assembly-manifest artifacts/build/obj/MyPowerTools.Android/release/app_shared_libraries/arm64-v8a/arm64-v8a/assemblies.arm64-v8a.blob.so.manifest

dotnet test tests/MobileLayout.Tests/MobileLayout.Tests.csproj -c Release -p:UseMobileShell=true
```

第二条命令用实际移动 Shell 构建运行既有界面套件。首轮 87 项中 81 项通过，6 项失败：桌面文件助手控件查找两项、手机首页旧文案两项、目录失败状态一项、后台目录刷新一项；原桌面 Shell 构建复现相同六项失败。不能将该套件报告为全绿。Pixel 4a 覆盖安装后已人工查看启动页、会话、历史图片、原有草稿和网盘设置。独立 Android OpenList 实例通过管理员登录、驱动集合、凭据字段、本地上传、WebDAV 下载和网页检查；本轮仍未重测真实夸克/百度账号的上传下载。
