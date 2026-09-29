# 内嵌 OpenList 运行时（Windows / macOS / Android）

目标：**App 用户零安装**——不需要自己安装 OpenList，也不需要打开远程 OpenList 网页；
网盘授权（WebDAV/云盘账号）仍然由用户本人完成。

归属：运行时解析/生命周期只在
`tools/file-transfer/src/FileTransfer.Core/OpenListRuntime.cs`；打包与校验只在
`tools/file-transfer/runtime/`；单测只在
`tools/file-transfer/tests/FileTransfer.Core.Tests/OpenListRuntimeTests.cs`。

## 1. 平台矩阵

| 平台 | 运行时来源 | 可执行文件位置 | 首次使用是否需要网络 |
| --- | --- | --- | --- |
| Windows | 官方 release 下载（或随模块打包的只读副本） | `{data}/openlist/v4.2.6/openlist.exe` | 需要（除非安装器已 stage） |
| macOS | 官方 release 下载（或随模块打包的只读副本） | `{data}/openlist/v4.2.6/openlist` | 需要（除非安装器已 stage） |
| Linux（开发/测试） | 官方 release 下载（或随模块打包的只读副本） | `{data}/openlist/v4.2.6/openlist` | 需要（除非已 stage） |
| Android | **只能内嵌**：APK 的 `lib/<abi>/libopenlist.so` | `ApplicationInfo.nativeLibraryDir/libopenlist.so` | **永不下载** |

Android 修复的问题：旧实现用 `IsWindows ? "windows" : IsMacOS ? "darwin" : "linux"`
选择下载资产，所以非 Windows/macOS 的 Android 落到了 `linux` 分支，会去下载
`openlist-linux-*.tar.gz`（glibc ELF）。现在 Android 显式判定并优先于 Linux 兜底，
且 Android 永不下载可执行文件。

## 2. 官方事实（v4.2.6）与 Android 执行机制

固定版本：`OpenListRuntime.Version = "v4.2.6"`，与 `openlist-runtime.json` 一致。

- 官方发布物含 `openlist-android-arm64.tar.gz`、`openlist-android-amd64.tar.gz`
  （`https://github.com/OpenListTeam/OpenList/releases/tag/v4.2.6`）。
- 官方 `build.sh` 的 `BuildReleaseAndroid()`：`GOOS=android`、
  `CC=<NDK r26b>/{aarch64,x86_64}-linux-android24-clang`、`CGO_ENABLED=1`、
  `-tags=jsoniter`、`llvm-strip`。
- 实测 `file(1)`：两者都是
  `ELF 64-bit LSB pie executable … interpreter /system/bin/linker64, for Android 24,
  built by NDK r26b`，即真正的 Bionic/Android 产物，不是 glibc/musl Linux ELF。
- 实测 `readelf -l`：**所有 PT_LOAD 段 `p_align = 0x1000`（4 KB）**。

Android 执行机制（平台事实，不是选择）：

- Android 10（API 29）起，targetSdk ≥ 29 的应用不能 `exec()` 应用数据目录中的文件（W^X）。
- 本实现使用 APK 的 native library 目录：`lib/<abi>/libopenlist.so`
  在安装期被解压到 `ApplicationInfo.nativeLibraryDir`，带执行权限。
- 这要求 `android:extractNativeLibs="true"`；验证脚本会检查，`false` 直接判失败。
- **16 KB 页警告**：官方 v4.2.6 Android 二进制是 4 KB 对齐，**不能声称兼容
  Android 15/16 的 16 KB 内存页设备**。打包脚本和验证脚本都会打印该警告；
  真正的兼容需要上游提供 16 KB 对齐的构建（或按官方 `build.sh` 用支持 16 KB
  的 NDK/链接参数自行构建），本项目不做无依据的兼容声明。

## 3. 运行时选择与生命周期（OpenListRuntime.cs）

平台判定：Windows→`windows`，macOS→`darwin`，**Android→`android`（内嵌来源）**，
其余→`linux`；架构 `X64→amd64`、`Arm64→arm64`，其它抛 `PlatformNotSupportedException`。

桌面解析顺序（`SelectDesktopExecutable`）：

1. `MPT_OPENLIST_RUNTIME`（显式覆盖，必须真实存在）；
2. 随模块打包的只读副本 `{module}/runtime/openlist/v4.2.6/openlist[.exe]`（可离线）；
3. 既有托管实例布局 `{data}/openlist/v4.2.6/openlist[.exe]`（**保持不变**）；
4. 都没有时从官方 HTTPS release 下载到 (3)。

Android 解析顺序（`RequireAndroidExecutable`）：

1. `MPT_OPENLIST_RUNTIME`；
2. `nativeLibraryDir/libopenlist.so`：目录用 Mono.Android 反射
   （`Android.App.Application.Context.ApplicationInfo.NativeLibraryDir`）获取，
   失败时回退解析 `/proc/self/maps` 中映射的 `lib<abi>/*.so`；
3. 都不存在时抛 `PlatformNotSupportedException`（提示内嵌运行时缺失或设置
   `MPT_OPENLIST_RUNTIME`）；不会下载，也不会使用应用数据目录里的可执行文件。

其它：管理员初始化（`openlist admin random --data …`）与 `openlist server --data …`
共用同一解析结果；stdout/stderr 继续丢弃、不写入 MPT 日志（初始日志可能含凭据）；
Android 子进程额外设置 `TMPDIR`/`TMP` 到实例数据目录；
Unix（含 Android）停止改用 libc `kill(pid, SIGTERM)`，不再依赖 `/bin/kill`，
5 秒未退出才升级为强杀。

兼容契约：公开成员、`Version`/`Port`(15244)、`Address`/`AdminUrl`、
`{data}/openlist/v4.2.6/openlist[.exe]` 布局以及测试用
`MPT_OPENLIST_TEST_BINARY` 全部保持不变。

## 4. 打包与校验（标准路径）

固定清单 `tools/file-transfer/runtime/openlist-runtime.json`：版本、官方下载基址、
ABI→资产名、期望 ELF 机器、NDK triple。

### 4.1 生成可内嵌运行时

```powershell
pwsh -NoLogo -NoProfile -File tools/file-transfer/runtime/package-openlist-android-runtime.ps1 -Abi all
```

1. 只从固定 tag 的官方 release 经 HTTPS 下载；不额外引入校验和机制
   （与桌面下载路径同一信任边界：固定版本 + 官方来源）；
2. 结构性校验载荷：64 位小端 PIE、ELF 机器与 ABI 匹配、包含
   `/system/bin/linker64`、不含 `/lib64/ld-linux`、`/lib/ld-linux`、`/lib/ld-musl`，
   并记录 PT_LOAD 对齐（4 KB 时打印 16 KB 页警告）；
3. 产出（git 忽略）：

   ```text
   tools/file-transfer/runtime/stage/lib/arm64-v8a/libopenlist.so
   tools/file-transfer/runtime/stage/lib/x86_64/libopenlist.so
   tools/file-transfer/runtime/stage/openlist-runtime-stage.json
   ```

缓存目录默认 `/mnt/cache/data-cache/mpt-openlist-runtime`（可写时），
否则回退到仓库已受治理的 scratch 类 `artifacts/.tmp-openlist-runtime/cache`
（`scripts/artifacts-policy.json` 的 `.tmp-*`），不新建未受治理路径。

### 4.2 APK 接入（已由 root csproj 集成）

`src/MyPowerTools.Android/MyPowerTools.Android.csproj` 用 `AndroidNativeLibrary`
把上面两个 ABI 打进 APK 的 `lib/<abi>/libopenlist.so`，缺失时由
`StageEmbeddedOpenList` target 调用打包脚本补齐。标准构建即可，
不再需要任何构建后处理/重签步骤。

### 4.3 校验已构建的 APK

```powershell
pwsh -NoLogo -NoProfile -File tools/file-transfer/runtime/verify-openlist-android-embed.ps1 `
  -Apk artifacts/build/bin/MyPowerTools.Android/debug/com.mypowertools.android-Signed.apk `
  -BuildToolsDirectory /android/sdk/build-tools/36.1.0
```

检查：每个请求的 ABI 是否存在 `lib/<abi>/libopenlist.so`；载荷是否 Android +
正确 ABI + 非 glibc/musl；`AndroidManifest.xml` 的 `extractNativeLibs` 是否为 `true`；
并打印 PT_LOAD 对齐的 16 KB 页警告。缺运行时默认判失败（`-AllowMissing` 降级为警告）。

## 5. 安全边界

- 下载只允许固定版本 + 官方 GitHub release 的 HTTPS 地址；Android 一律不下载。
- 不新增 checksum/hash 校验门槛；完整性依赖固定版本与官方来源。
- 运行日志（可能含初始凭据）继续丢弃，不进 MPT 日志。
- 不修改全局路由/网络/密钥；网盘授权仍由用户在既有流程完成。

## 6. 验证记录

自动化与原生执行验证（尚不替代应用界面验收）：

- `FileTransfer.Core.Tests`：**290 通过 / 0 失败 / 3 环境跳过**。
- `OpenListRuntimeTests`：**25 通过**。
- Android 集成验收（root csproj + host 侧执行）：APK `extractNativeLibs=true`，
  内嵌运行时在专用 AVD 上以应用 UID 运行，输出 `Android/amd64 v4.2.6`。

本次打包/校验脚本自身运行结果：

| 项 | 方式 | 结果 |
| --- | --- | --- |
| 资产与 staging | `package-openlist-android-runtime.ps1 -Abi all` | 两个 ABI 下载并 staging 成功，ELF/ABI 校验通过，打印 PT_LOAD `0x1000` 警告 |
| APK 内嵌校验 | `verify-openlist-android-embed.ps1`（对已构建 APK） | `lib/arm64-v8a`、`lib/x86_64` 均在、Android ELF/ABI 正确、`extractNativeLibs=true` |
| 负路径 | 对不含运行时的 zip 运行 verifier | `MISSING` + 处理提示，exit 1 |

## 7. 仍存在的打包/平台边界

1. **16 KB 内存页设备不支持**：官方 v4.2.6 二进制 PT_LOAD 为 4 KB 对齐，
   当前尚未验证兼容性；要支持需上游提供或自行构建 16 KB 对齐版本。
2. **arm64 真机未单独验证**：已验收的是 AVD（amd64）；arm64 资产同样内嵌并通过
   结构校验，但没有 arm64 设备执行记录。
3. **Windows / macOS 未实机运行**：桌面侧在本机（Linux）用官方 linux-amd64
   二进制跑通既有集成用例；Windows/macOS 只有编译期与资产命名/路径单测覆盖。
4. **桌面“随模块打包”目录尚未被安装器填充**：只读候选路径已支持，
   未 stage 时行为与以前一致（首次使用下载）。
