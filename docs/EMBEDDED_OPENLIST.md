# 内嵌 OpenList 运行时（Windows / macOS / Android）

目标：**App 用户零安装**——不需要自己安装 OpenList，也不需要打开远程 OpenList 网页；
网盘授权（WebDAV/云盘账号）仍然由用户本人完成。

归属：运行时解析/生命周期只在
`tools/file-transfer/src/FileTransfer.Core/OpenListRuntime.cs`；Android 载荷的构建、打包与校验只在
`tools/file-transfer/runtime/`（`openlist-runtime.json`、`build-openlist-android-runtime.ps1`、
`package-openlist-android-runtime.ps1`、`verify-openlist-android-embed.ps1`、
`OpenListRuntimePackage.psm1`）；单测只在
`tools/file-transfer/tests/FileTransfer.Core.Tests/OpenListRuntimeTests.cs`。

## 1. 平台矩阵

| 平台 | 运行时来源 | 可执行文件位置 | 首次使用是否需要网络 |
| --- | --- | --- | --- |
| Windows | 官方 release 下载（或随模块打包的只读副本） | `{data}/openlist/v4.2.6/openlist.exe` | 需要（除非安装器已 stage） |
| macOS | 官方 release 下载（或随模块打包的只读副本） | `{data}/openlist/v4.2.6/openlist` | 需要（除非安装器已 stage） |
| Linux（开发/测试） | 官方 release 下载（或随模块打包的只读副本） | `{data}/openlist/v4.2.6/openlist` | 需要（除非已 stage） |
| Android | **只能内嵌**：固定 v4.2.6 源码构建产物，打包为 APK 的 `lib/<abi>/libopenlist.so` | `ApplicationInfo.nativeLibraryDir/libopenlist.so` | **永不下载** |

桌面端仍然使用官方 v4.2.6 release，`OpenListRuntime` 的桌面解析顺序不变；只有 Android 载荷改为
固定 tag 源码构建（原因见第 2 节）。Android 显式判定并优先于 Linux 兜底，永不下载可执行文件。

## 2. Android 载荷：为什么不能用官方 Android 资产

### 2.1 现象与证据

- 官方 v4.2.6 release 提供 `openlist-android-arm64.tar.gz`、`openlist-android-amd64.tar.gz`，
  是真正的 Bionic PIE（`interpreter /system/bin/linker64`），但在 **真实应用 UID** 下
  `openlist admin random --data …`（`OpenListRuntime.InitializeAdminAsync` 的首次初始化）会在
  `InitDB` 阶段被应用 seccomp 杀死，退出码 2。
- 脱敏证据 `/mnt/cache/data-cache/mpt-v3-android/admin-init-app-sanitized.log`：

  ```text
  SIGSYS: bad system call
  syscall.Syscall(0x6, …)                       # x86_64 lstat
  modernc.org/libc.Xlstat64                     # libc_linux_amd64.go:92
  modernc.org/sqlite/lib.appendOnePathElement   # unixFullPathname
  github.com/glebarez/go-sqlite.(*conn).openV2 → gorm Open → bootstrap.InitDB
  ```
- 根因：官方 `build.sh` 的 `BuildReleaseAndroid()` 只传 `-tags=jsoniter`。没有开
  `sqlite_cgo_compat` 时，`internal/bootstrap/sqlite_driver_glebarez.go` 生效，GORM 走
  `glebarez/sqlite → modernc.org/sqlite v1.23.1 → modernc.org/libc v1.22.5`；该 libc 直接发
  **裸 syscall** `lstat`（x86_64 编号 6）。Android 10+ 的应用 seccomp 策略不允许 stat/lstat/fstat
  这类旧 syscall，应用必须走 `fstatat/newfstatat`。
- `adb run-as`/CLI 成功不能作为证据：那不是应用 seccomp 域。修复是否生效必须由真实应用 UID 的
  端到端测试确认（见第 6、7 节）。

### 2.2 修复：上游自带的 `sqlite_cgo_compat` + NDK CGO SQLite

- OpenList 自身提供 driver 切换：
  `internal/bootstrap/sqlite_driver_gorm.go`（`//go:build sqlite_cgo_compat`）使用
  `gorm.io/driver/sqlite → github.com/mattn/go-sqlite3`（CGO）。
- 本仓库用官方同款 Android 构建方式编译该 tag：`GOOS=android`、`CGO_ENABLED=1`、
  NDK `*-linux-android24-clang`。SQLite 的 `stat/lstat/access/readlink` 全部进入 Bionic libc，
  而 Bionic 内部用 `fstatat` 实现，属于应用 seccomp 允许的 syscall。
- **不修改 OpenList 源码**，只改构建配置：多一个上游 tag、CGO 工具链、16 KB 页对齐、固定前端版本。
- 不放松任何安全设置：`targetSdk`、`extractNativeLibs=true`、应用 seccomp、打包策略都不动。
- 选择理由：`sqlite_cgo_compat` 是上游维护的分支（用于 mips/loong64/win7-386 等没有纯 Go
  SQLite 的目标），比在本仓库里给 `modernc.org/libc` 打补丁或做“有界升级试错”风险更低、可复现性更好。

### 2.3 Android 执行机制与页大小（平台事实，不是选择）

- Android 10（API 29）起，targetSdk ≥ 29 的应用不能 `exec()` 应用数据目录中的文件（W^X）。
- 因此运行时只能来自 APK 的 native library 目录：`lib/<abi>/libopenlist.so`，安装期解压到
  `ApplicationInfo.nativeLibraryDir`（要求 `android:extractNativeLibs="true"`，校验脚本会检查）。
- 本轮构建的载荷 **所有 PT_LOAD 段 `p_align = 0x4000`（16 KB）**，可用于 Android 15/16 的
  16 KB 内存页设备；官方资产是 `0x1000`，校验脚本会把 4 KB 载荷判为失败（`-Require16KbPages`）。

## 3. 运行时选择与生命周期（OpenListRuntime.cs，未改动）

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

## 4. 构建、打包与校验

固定清单 `tools/file-transfer/runtime/openlist-runtime.json`（schemaVersion 2）同时固定：
版本/tag、源码归档 URL、Git commit、Go toolchain、构建 tag、SQLite driver、前端 release、
NDK release/revision、16 KB 页大小，以及每个 ABI 的 `goArch`、`ndkTriple`、期望 ELF 机器。

### 4.1 生成可内嵌运行时（默认源码构建）

```powershell
pwsh -NoLogo -NoProfile -File tools/file-transfer/runtime/package-openlist-android-runtime.ps1 -Abi all
```

该命令默认转调 `build-openlist-android-runtime.ps1`，步骤：

1. 只从固定 tag 的官方 GitHub archive 经 HTTPS 下载源码（不额外引入校验和机制，与桌面下载
   路径同一信任边界：固定版本 + 官方来源）；
2. 下载固定 `OpenList-Frontend v4.2.6` 的 non-lite dist 放进 `public/dist`（官方 build.sh 在
   构建时取“最新前端 release”，这里改为固定版本；管理页 `@manage` 仍由内嵌前端提供）；
3. 定位 NDK：`-NdkRoot` → `ANDROID_NDK_ROOT/ANDROID_NDK_HOME/ANDROID_NDK` → SDK
   `ndk/<revision>` → 常见安装位置；只有显式 `-NdkRoot` 才接受非固定 revision。缺失时可用
   `-DownloadNdk` 拉取清单里固定的官方 NDK 归档；
4. `go build`：
   `GOOS=android`、`CGO_ENABLED=1`、`CC/CXX=<ndk>/*-linux-android24-clang(++)`、
   `-tags=jsoniter,sqlite_cgo_compat`、`-trimpath -buildvcs=false -buildmode=pie`、
   `CGO_LDFLAGS=-Wl,-z,max-page-size=16384`、`-ldflags "-w -s -X …"`（版本戳与官方一致）、
   Go toolchain 固定 `go1.27.0`（`-GoToolchain local` 可强制本机）；
5. 校验：64 位小端 PIE、ELF 机器与 ABI 匹配、包含 `/system/bin/linker64`、不含
   `/lib64/ld-linux`、`/lib/ld-linux`、`/lib/ld-musl`、**不含 `modernc.org/libc`**、
   PT_LOAD ≥ 16 KB；再用 `go version -m` 断言存在 `github.com/mattn/go-sqlite3`、
   不存在任何 `modernc.org/*`；然后用 NDK 的 `llvm-strip` 剥离并再次校验；
6. **所有 ABI 都构建并校验通过后**才原子替换 stage（`lib/<abi>/libopenlist.so` +
   `openlist-runtime-stage.json`），失败不会留下半成品 stage。

产出（git 忽略）：

```text
tools/file-transfer/runtime/stage/lib/arm64-v8a/libopenlist.so
tools/file-transfer/runtime/stage/lib/x86_64/libopenlist.so
tools/file-transfer/runtime/stage/openlist-runtime-stage.json
```

缓存目录默认 `/mnt/cache/data-cache/mpt-openlist-runtime`（可写时），否则回退到仓库已受治理的
scratch 类 `artifacts/.tmp-openlist-runtime`（`scripts/artifacts-policy.json` 的 `.tmp-*`）。
常用参数：`-Abi`、`-CacheDirectory`、`-OutputDirectory`、`-NdkRoot`、`-GoProxy`、
`-GoToolchain`、`-DownloadNdk`、`-Force`。

诊断用旧路径（**不要用于 APK**）：`-Source OfficialRelease` 会下载官方资产；结构校验现在会以
“链接了 modernc.org/libc”为由拒绝 stage，只有再加 `-AllowUnsafePayload` 才允许落到独立输出目录，
用于复现 SIGSYS 证据或做对比。

### 4.2 APK 接入（已由 root csproj 集成，未改动）

`src/MyPowerTools.Android/MyPowerTools.Android.csproj` 用 `AndroidNativeLibrary`
把上面两个 ABI 打进 APK 的 `lib/<abi>/libopenlist.so`，缺失时由
`StageEmbeddedOpenList` target 调用打包脚本补齐。标准构建即可，不需要任何构建后处理/重签步骤。

**构建机前置条件（新增）**：stage 缺失时，APK 构建会走源码构建，因此该机器需要
Go ≥ 1.25（会按 go.mod 拉取固定 go1.27.0 toolchain）、固定 revision 的 Android NDK，
以及首次构建时的外网（源码/前端/Go 模块）。只跑 APK 打包而不重建运行时的机器，应预先
stage 好 `tools/file-transfer/runtime/stage/`，或在有 Go/NDK 的机器上先跑一次 4.1。

### 4.3 校验已构建的 APK

```powershell
pwsh -NoLogo -NoProfile -File tools/file-transfer/runtime/verify-openlist-android-embed.ps1 `
  -Apk artifacts/build/bin/MyPowerTools.Android/debug/com.mypowertools.android-Signed.apk `
  -BuildToolsDirectory /android/sdk/build-tools/36.1.0 -Require16KbPages
```

检查：每个请求的 ABI 是否存在 `lib/<abi>/libopenlist.so`；载荷是否 Android + 正确 ABI +
非 glibc/musl + 不含 `modernc.org/libc`；`AndroidManifest.xml` 的 `extractNativeLibs` 是否为
`true`；PT_LOAD 是否 16 KB 对齐（`-Require16KbPages` 时不对齐判失败，否则仅警告）。
缺运行时默认判失败（`-AllowMissing` 降级为警告）。

## 5. 安全边界

- 下载只允许固定版本 + 官方 HTTPS 来源（OpenList tag archive、OpenList release、
  OpenList-Frontend release、Google NDK）；Android 一律不在设备上下载。
- 不新增 checksum/hash 校验门槛；完整性依赖固定版本、固定 tag/commit 与官方来源。
- 构建只改配置不改上游源码；`sqlite_cgo_compat` 是上游自带 tag。
- 不放松应用安全设置：targetSdk、seccomp、`extractNativeLibs`、签名流程全部不变。
- 运行日志（可能含初始凭据）继续丢弃，不进 MPT 日志。
- 不修改全局路由/网络/密钥；网盘授权仍由用户在既有流程完成。

## 6. 验证记录

本轮（2026-09-29，Linux x86_64 构建机，NDK 27.0.12077973，Go 1.27.0）：

| 项 | 命令/方式 | 结果 |
| --- | --- | --- |
| 源码构建两个 ABI | `package-openlist-android-runtime.ps1 -Abi all`（转调 build 脚本） | 成功；`lib/arm64-v8a` 136.3 MB、`lib/x86_64` 142.1 MB，PT_LOAD 均为 `0x4000` |
| 可复现性 | 同一命令连续重跑，比较 sha256 | 两次构建的 stage 载荷**字节完全一致** |
| ELF 事实 | `file` / `readelf -l/-d` | `ELF 64-bit LSB pie … interpreter /system/bin/linker64, for Android 24, built by NDK r27`；NEEDED 仅 `liblog.so`/`libdl.so`/`libc.so` |
| 结构校验 | `Test-OpenListAndroidElf -Require16KbPages` | 两个 ABI 通过（无 problem/warning） |
| 驱动证据 | `go version -m <payload>` | `go1.27.0`、`-tags=jsoniter,sqlite_cgo_compat`、`GOOS=android`、`CGO_ENABLED=1`、`github.com/mattn/go-sqlite3 v1.14.22` + `gorm.io/driver/sqlite v1.6.0`，**无任何 `modernc.org/*`** |
| 禁止代码路径 | `strings` 扫描 `modernc.org/libc` | 新载荷 0 次；同机扫旧官方载荷 269 次 |
| 版本戳 | `strings` 扫描 BuiltAt/GitCommit/v4.2.6 | 两个 ABI 都在 |
| 前端内嵌 | `strings` 扫描前端 dist 资源名 | 两个 ABI 都命中 |
| verifier 正路径 | 用新载荷构造 zip 后运行 verifier（`-Require16KbPages`） | 两个 ABI `ok`，exit 0 |
| verifier 负路径（缺运行时） | 空 zip | `MISSING` + 提示，exit 1 |
| verifier 负路径（旧 APK） | 对 2026-09-28 构建的旧 APK | 两个 ABI `INVALID`（modernc + 4 KB），exit 1，证明检查确实能拦住旧载荷 |
| 官方资产诊断路径 | `-Source OfficialRelease -AllowUnsafePayload -OutputDirectory <scratch>` | 仍可 stage 到独立目录并保留 warning，真实 stage 未被污染 |

**应用内验收补充（2026-09-29）**：专用 x86_64 Android 测试设备、MPT 0.2.5/code7、user10。
在没有 data.db 的前提下，通过 MPT 按钮首次启用，成功创建数据库并启动应用 UID 下的 libopenlist.so；
管理按钮打开本机 OpenList 登录页；停止按钮使子进程退出、15244 端口拒绝连接。新 APK 的双 ABI 载荷
也通过结构校验。证据：`/mnt/cache/data-cache/mpt-v3-android/v025-started.png`、
`v025-admin-settled.png` 和该目录的 QA 记录。此处是应用内测试，不是 run-as CLI 替代。

打包检查按所有 PT_LOAD 段中的最小对齐值判断，混合 4 KB/16 KB 段必须拒绝，不能只看最大值。
该规则依据 [Android 官方 ELF 对齐检查](https://developer.android.com/guide/practices/page-sizes#elf-alignment)。

## 7. 仍存在的打包/平台边界

1. arm64 真机与 16 KB 内存页真机未执行；OpenList ELF 对齐不等同于整个应用在这些设备上验收通过。
2. Windows 实际 UI 自动准备运行时、启用、管理页访问和停止通过；macOS 未完成实机与功耗验收。
3. 本仓库固定 NDK r27，官方 Android release 使用 r26b；构建差异记录在 stage manifest。
4. NDK 自动下载路径未实测，本机使用已安装的 NDK 27.0.12077973；本机构建采用
   `-GoProxy https://goproxy.cn,direct`，模块沿用上游 go.sum。
5. 桌面安装器尚未预置只读运行时副本，首次使用仍由 MPT 自动下载；不要求用户另外安装 OpenList。
6. 没有实际网盘账号授权证据；本次验证的是内嵌运行时生命周期和管理页面，不代表各网盘授权均已通过。
