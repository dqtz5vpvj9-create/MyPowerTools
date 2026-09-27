# Android 移植本轮验收说明（2026-09-27）

本文件只记录**本轮由构建/发布代理实际执行过的验证**。它不是发布声明：
Android 真机、macOS 实机与正式发布均未验收。

配套文档：`docs/ANDROID_PORT.md`。

## 1. 本轮独占改动

| 文件 | 改动 |
| --- | --- |
| `MyPowerTools.slnx` | 接入 `tools/file-transfer` 3 个项目、`FileTransfer.Core.Tests`、`FileTransfer.Surface.Tests`、`MobileLayout.Tests`、`RemoteNotifications.Android.Tests`、`RemoteCommands.Android.Tests`；删除重复的 `MobileLayout.Tests` 条目（现共 **78** 个唯一项目） |
| `MyPowerTools.Android.slnx` | 独立 Android solution（**12** 个项目）：应用、Platform.Android 及其 `net10.0` 纯逻辑测试、FileTransfer 3 个项目、`MyPowerTools.MobileNotifications` + `RemoteNotifications.Android` + 测试、`MyPowerTools.MobileRemoteCommands` + `RemoteCommands.Android` + 测试；只加已落盘的项目 |
| `scripts/build-android.ps1` | 重写：解析 dotnet（PATH → `DOTNET_ROOT` → `~/.dotnet/dotnet`）、校验 Android SDK/JDK、`$sdkContractProjects` 先 pack 进 repo-local feed 并清缓存、按 `$moduleStages` 逐个构建 Android 工具模块包（按 `Get-Command` 探测工具是否支持 `-Configuration`；产品构建不传 `-SkipSurface`/`-NoMirror`）、构建 Android solution、断言 APK 存在；每一步失败非 0 退出。`$moduleStages` 现有三个**必需**条目（`file-transfer`、`remote-notifications-android`、`remote-commands-android`，均 `Optional = $false`），每条带 `RequireFiles`（见 §2.3） |
| `.github/workflows/android-preview.yml` | 手动 `workflow_dispatch`（Debug/Release、可选 single-ABI），装 Android workload、构建、用 `unzip` 校验 ABI/CoreCLR/模块资产、上传 artifact；原 CI 不装 workload |
| `tools/file-transfer/build.ps1` | 先在 `artifacts/package.staging` 构建、成功后再原子替换 `artifacts/package` 并同步 `modules/file-transfer`；断言 `module.json`/适配器/Surface 存在；构建失败时**不**破坏上一次可用的包与仓库模块目录 |
| `tools/file-transfer/tool-release.json`、`source-map.json` | 未改内容（复核过与 `build-all-tools.ps1` 注册、`build-tool-packages.ps1` 契约一致） |
| `scripts/build-all-tools.ps1` | 已注册 `file-transfer`（version 0.1.0 / build.ps1 / Surface / stage 路径），供 `build-tool-packages.ps1` → Windows 模块暂存使用；新增 `Optional = $true` 语义：全量构建时跳过尚未落盘的可选工具并告警、写入 `source-manifest.json` 的 `skippedOptionalTools`，显式 `-ToolId` 请求仍硬失败 |
| `scripts/update-windows-dev.ps1` | 只在工具 `build.ps1` 声明了 `-Configuration` 时才传该参数（3 个旧工具没有该参数） |
| `scripts/publish-windows.ps1` | `$packageByTool` 增加 `file-transfer` |
| `scripts/publish-macos-base.ps1` | macOS 发布构建并签名 `modules/file-transfer`，构建失败即抛错，签名前去 pdb |
| `scripts/artifacts-policy.json` | 新增 `test-results/*`（evidence，14 天）声明；APK/包缓存沿用已有 `build`/`cache` 条目 |
| `docs/ANDROID_PORT.md`、本文件 | 新增 |

## 2. 已执行的验证与结果（并行阶段快照）

> **快照声明**：本节是 2026-09-27 07:00–07:25 并行开发阶段、由本代理在沙箱内跑出的中间
> 结果；其中测试数量会被后续提交刷新，**不是最终验收数据**。canonical 环境
> （`/mnt/cache/data-cache` 可用）由 root 复验，权威数字见 §2.2。

环境：Linux、`/home/chris/.dotnet/dotnet` 10.0.301、`/android/sdk`（android-36）、
`/usr/lib/jvm/java-17-openjdk-amd64`。沙箱把 `$HOME`、`TMPDIR` 设为只读，因此验证时用
`HOME=<repo>/artifacts/.tmp-home`、`TMPDIR=/tmp`；这是环境适配，不是脚本的一部分。

| 验证 | 命令 | 结果 |
| --- | --- | --- |
| solution 结构（三个 Android 模块全部接入后，静态核对） | XML 解析 + 每个 `Path` 存在性 + TFM 扫描 | `MyPowerTools.slnx` **78** 个项目、`MyPowerTools.Android.slnx` **12** 个项目，路径唯一且全部存在；桌面 solution 无 `net10.0-android`；新增项目 TFM 均为 `net10.0` |
| 可选工具跳过语义 | `$tool.PSObject.Properties['Optional'] -and [bool]$tool.Optional` + `ConvertTo-Json` 快照 | 有 `Optional` 属性=True、无属性=False；`skippedOptionalTools` 能正确序列化 |
| 脚本语法 | `[Parser]::ParseFile()` 检查改动脚本（含 `$moduleStages`/`RequireFiles` 循环） | 全部 OK |
| workflow YAML | Python `yaml.safe_load` | 解析 OK，7 步 |
| dev overlay 参数探测 | 对 12 个 `tools/*/build.ps1` 跑 `Get-Command <path>.Parameters.ContainsKey('Configuration')` | `adb-forwarder`/`doubao-computer-use`/`paste-image` = False（不传），其余 9 个 = True（传） |
| SDK 包闭环 | 全仓 99 个 csproj 扫描 `MyPowerTools.*` PackageReference | 生产/消费版本一致；`build-sdk.ps1` 覆盖全部被引用的包 |
| fresh checktout 复现（新代码） | 全新 feed（按 `build-sdk.ps1` 打包 13 个项目）+ 空 package cache，`dotnet restore FileTransfer.MyPowerTools --configfile <fresh> --source <fresh feed>` | **成功**（0.2.1 来自新 feed，未依赖旧缓存） |
| artifacts 治理 | `scripts/check-artifacts-governance.ps1 -Enforce -SkipBudget` | `Artifacts governance: OK`，exit 0 |
| Android 构建流水线 | `pwsh scripts/build-android.ps1 -AndroidSdkDirectory /android/sdk -JavaSdkDirectory /usr/lib/jvm/java-17-openjdk-amd64` | **成功**（第 5 次尝试，07:16）：3 个 SDK 包 pack ✅、`File Transfer staged at tools/file-transfer/artifacts/package` ✅、`dotnet build MyPowerTools.Android.slnx` ✅、输出 `Android development preview: .../com.mypowertools.android-Signed.apk`，`size: 125.2 MB` ✅ |
| Android 失败出口 | `build-android.ps1 -AndroidSdkDirectory /nonexistent-sdk`；`build-android.ps1`（JAVA_HOME 未设且未传 JDK） | 分别是 `Android SDK directory does not exist` / `JDK directory was not provided...`，**exit 1** |
| Android 模块 stage 注册 | 解析 `$moduleStages` + `RequireFiles` + stage 目录实查 | 三条均 `Optional = $false`（必需）：`file-transfer`、`remote-notifications-android`、`remote-commands-android`；每条带 `RequireFiles` 主机侧守卫（见 §2.3）。通知 stage 实查含 `module.json`、`RemoteNotifications.Android.dll`、`BouncyCastle.Cryptography.dll`、`ui/surface/MyPowerTools.MobileNotifications.dll`、`ui/*.json`（无 `MyPowerTools.*`、无 pdb，符合模块清单） |
| 发布契约一致性 | 用脚本核对 `tool-release.json` / `source-map.json` / `package/module.json` / `build-all-tools.ps1` 注册 | toolId、版本 0.1.0、packageId、适配器程序集、模板、buildScript、3 个 suiteProjectReference、5 个平台声明全部一致，0 问题 |
| 发布验收脚本集合 | 解析 `verify-release-candidate(.remote).ps1` 的 `$expectedTools` / A5.3 / A5.7 / A5.R4 | 12 / 9 / 10 / 7，断言数与列表长度一一对应；仍要求“缺一个就失败”（见 §3 B4） |
| Release 配置模块 | `dotnet build FileTransfer.Surface -c Release` | 成功（适配器 Release 中途被他人改成 CS1501，随后已修好） |
| SDK feed 刷新 | `ls -la artifacts/sdk/nuget` | `Platform.Abstractions.0.2.1`、`AvaloniaSdk.0.2.1`、`ToolSdk.0.2.0` 已按本轮时间重新 pack（**未升 0.2.2**）；`MyPowerTools.Ipc.AspNetCore` 由 `build-sdk.ps1` 负责，本代理未改动 |

### 2.2 canonical 复验（root，权威结果）

canonical 环境下 root 已完整跑通 `scripts/build-sdk.ps1`（含
`MyPowerTools.Ipc.AspNetCore`）。**权威测试数字以本节为准，其它章节不得再引用旧快照**：

| 测试项目 | 结果 |
| --- | --- |
| `tools/file-transfer/tests/FileTransfer.Core.Tests` | **55 passed** |
| `src/MyPowerTools.Platform.Android/tests/MyPowerTools.Platform.Android.Tests` | **16 passed** |
| `tools/file-transfer/tests/FileTransfer.Surface.Tests` | **20 passed** |
| `tools/remote-notifications/android-integration/tests/RemoteNotifications.Android.Tests` | **58 passed** |
| `tools/remote-commands/android-integration/tests/RemoteCommands.Android.Tests`（SSH） | **59 passed** |
| IPC 契约（root 报告口径） | **8 passed** |
| `tests/MobileLayout.Tests` | 曾达 **17 passed**，随后发现真实“默认导航”缺陷，正在修，配套新用例稍后补；**当前不得写成通过** |

### 2.3 模块 stage 的主机侧守卫（`RequireFiles`）与镜像策略

`scripts/build-android.ps1` 的产品构建**从不传** `-SkipSurface` 或 backend-only 模式，
并按 `RequireFiles` 逐个断言 stage 内容：

| 模块 | 必须存在的文件（相对 stage 根） |
| --- | --- |
| `file-transfer` | `ui/surface/FileTransfer.Surface.dll` |
| `remote-notifications-android` | `ui/surface/MyPowerTools.MobileNotifications.dll` |
| `remote-commands-android` | `RemoteCommands.Android.dll`、`Renci.SshNet.dll`、`BouncyCastle.Cryptography.dll`、`ui/surface/MyPowerTools.MobileRemoteCommands.dll` |

原因：这些模块的宿主契约程序集要从 stage 里剔除，但**通配符剔除不能连工具 Surface 一起删**——
Remote Commands 的 Surface 本身就叫 `MyPowerTools.MobileRemoteCommands.dll`。
root 已把 `$hostProvided` 从 `MyPowerTools.*.dll` 改为**精确的三个 SDK 程序集名**，
三个模块包在 canonical 环境下均已构建并通过 staging 校验；宿主 `AndroidAsset` 的
`Exclude` 也按同一原则点名排除，不再整类排除 `MyPowerTools.*`。

**镜像策略**：只有同时属于桌面目录的工具（`file-transfer`）才镜像到仓库
`modules/<id>`；Android-only 的 Remote Commands 包由 root 传 `-NoMirror` 构建——
它需要的元数据就在 APK asset 用的那个 stage 里（与通知模块一致），
仓库根的 `modules/remote-commands-android` 镜像已删除，避免 Windows 上出现重复的
Android 卡片。

**root 对 `build-android.ps1` 的后续改动**（本代理未参与）：pwsh 解析改为
`Get-Command ... | Select-Object -First 1`，避免 PATH 上存在多个 pwsh 时选错；
Remote Commands 条目按上文传 `-NoMirror`。

### 2.1 APK 核验状态（历史证据，非本轮验收）

**当前 3 模块 + 宿主接线后的最终 APK 尚未验证。** 最近一次两模块时期的 APK 是
07:42 的构建产物，**不是验收证据**，不得引用为“APK 已就绪”。

以下是**单模块时期**（07:16）的历史核验，仅用于说明 APK 的组装方式：

```
artifacts/build/bin/MyPowerTools.Android/debug/com.mypowertools.android-Signed.apk
mtime 2026-09-27 07:16，131,324,527 字节（125.2 MiB）
```

用 `unzip -Z1` 直接枚举 APK 内容（不是看中间产物）：

- ABIs：`lib/arm64-v8a/`、`lib/x86_64/`（universal），两边各有一份 `libcoreclr.so`
  → CoreCLR preview 生效；
- `assets/modules/*/module.json` 共 10 个，模块目录 8 个：
  `adb-forwarder`、`android-tools-suite`、`doubao-agent`、`file-transfer`、
  `input-monitor`、`paste-image`、`screenease`、`smartbird-thermostat`；
- `assets/modules/file-transfer/` 下 10 个条目，包含 `module.json`、
  `commands.index.json`、`FileTransfer.MyPowerTools.dll`、`FileTransfer.Core.dll`、
  `ui/tool.json`、`ui/surface/FileTransfer.Surface.dll`。

它证明的是“整个 MPT 目录被搬进 APK、工具以模块形式装载、运行时是 CoreCLR 双 ABI”，
**不证明真机可运行，也不证明当前三模块状态可用**（见第 5 节）。APK 使用本地 debug key 签名。

## 3. 并行开发期间观察到的问题（含当前状态）

| # | 位置 | 现象 | 状态 |
| --- | --- | --- | --- |
| B1 | `src/MyPowerTools.Android/AndroidStartupView.cs` | `error CS0104`：`ProgressBar`/`Button` 在 `Avalonia.Controls` 与 `Android.Widget`（`ImplicitUsings` 带入）之间二义 | **已由对应代理修好**（07:10:58 加 `using Button = Avalonia.Controls.Button;` / `using ProgressBar = Avalonia.Controls.ProgressBar;`） |
| B2 | `tools/remote-notifications/.../RemoteNotificationsView.axaml.cs` | 早期 `error CS1061: 'Window' ... 'SheetCompletion'`；现在代码改为 `dialog is not IMptSurfaceSheetDialog sheetDialog` | **已由对应代理修好** |
| B3 | `tests/MobileLayout.Tests/ScratchScreenEaseRepro.cs` | 临时诊断用例断言“布局必须抛异常”，一旦修好就会失败并进原 CI | **已删除** |
| B4 | `scripts/verify-release-candidate.ps1` / `.remote`（本轮获额外授权修改） | 发布契约里的工具集合仍是旧的 11 个，且 A5.3/A5.7/A5.R4 的期望数量没算 `file-transfer` | **已修**：`$expectedTools` 12 项（含 `file-transfer`）；A5.3 Surface 8→**9**；A5.7 Runner 本地发现 9→**10**；A5.R4 远端目录发现 6→**7**。判定仍是“数量相等 + 缺任何一个即失败”，并且**刻意不从候选自己的 source manifest 推导集合**（否则同一份构建产物既当答案又当考卷，掉工具也能通过） |
| B5 | 多个工具子模块 csproj（adb-forwarder、doubao-computer-use、screenease、smartbird-thermostat、paste-image）仍引用 `MyPowerTools.Platform.Abstractions 0.2.0`；`templates/tool-dotnet/TemplateDotnet.csproj` 仍引用 `MyPowerTools.AvaloniaSdk 0.2.0` | fresh feed 里只有 0.2.1，NuGet 报 `warning NU1603: ... 0.2.0 was not found. 0.2.1 was resolved instead.` | **不阻塞**（warning，不是 error）；建议各子模块与模板显式升到 0.2.1，避免“实际编译在哪个 SDK 上”被掩盖 |
| B6 | `tools/remote-commands/android-integration/build.ps1` 的 host-provided 过滤器 | 曾把 `MyPowerTools.*.dll` 同时从 stage 根**和 `$surfaceStage`** 删除，会连 Surface（`ui/surface/MyPowerTools.MobileRemoteCommands.dll`）一起删掉 | **已由 root 修**：`$hostProvided` 改为**精确的三个 SDK 程序集名**，不再用 `MyPowerTools.*.dll` 通配；三个模块包在 canonical 环境下均已构建并通过 staging 校验 |
| B7 | `src/MyPowerTools.Android/MyPowerTools.Android.csproj`（host 负责人） | 两个 Android 模块包缺 `ProjectReference` + `AndroidAsset` | **已由 root 接线**：精确指向各模块的 stage 路径，`Exclude` 逐项点名（不整类排除 `MyPowerTools.*`）；最终 APK 仍待验证 |

### 3.1 并行编辑造成的瞬时失败（不是本代理脚本缺陷）

反复重试 Android 构建期间，先后撞到 6 个不同来源的错误，全部位于本代理授权范围之外，
且都在对应代理下一次写入后消失：

| 文件 | 错误 | 现状 |
| --- | --- | --- |
| `src/MyPowerTools.AvaloniaSdk/MptAdaptiveLayout.cs` | CS0122 `Visual.VisualRoot` 不可访问 | 已修 |
| `tools/file-transfer/.../FileTransferModule.cs` | CS8978 / CS1503，随后又有 CS1501 `ConnectAsync` 参数个数 | 已修 |
| `src/MyPowerTools.Android/AndroidStartupView.cs` | CS0104 二义引用（B1） | 已修 |
| `tools/remote-notifications/.../RemoteNotificationsView.axaml.cs` | CS1061 `SheetCompletion`（B2） | 已修 |
| `src/MyPowerTools.Shell.Avalonia/Views/MobileShellView.cs` | CS1061 `ShellWorkspaceController.BindContentHost`（`ShellWorkspaceController` 正在拆 partial，属重构中间态） | 已修 |
| `tools/file-transfer/.../TransferView.Layout.cs` | CS1003/CS1002/CS1519/CS1022（文件写到一半） | 测试重试时仍在变动 |

另外还撞到一次 NuGet 并发写冲突：
`error : The file 'artifacts/build/obj/MyPowerTools.HostControl.Client/project.nuget.cache' already exists`
——两个代理同时对同一项目 restore 造成，重试即可，不代表配置错误。

结论：**共享目录处于多代理并行编辑中，任何一次失败都可能是别人的中间态。**
Android 构建在第 5 次尝试（07:16）取得完整成功；测试是否通过取决于最后一次源码状态。

## 4. 环境限制

- 本代理的沙箱不允许写 `/mnt/cache/data-cache`（`Read-only file system`），也不允许写
  `~/.dotnet`；因此临时目录使用 `/tmp` 与仓库内 `artifacts/.tmp-*`（已由
  `artifacts-policy.json` 的 `.tmp-*` scratch 条目覆盖）。父代理如需在
  `/mnt/cache/data-cache` 下做验证，需自行执行。
- 无 Android 真机/模拟器、无 Mac 实机、无官方 OpenList 二进制，因此相关验收项见下节。

## 5. 仍未验收（不得当作已通过）

1. Android 真机安装/启动、系统分享入口、`mpt://` 激活、返回键、通知权限、
   MediaStore 落盘、前台服务在真机与省电策略下的行为。
2. 真机 Tailscale 端到端直传与网盘中转；官方 OpenList 集成用例（默认跳过）。
3. Release 配置 APK 与发布签名（当前只有本地 debug key 的开发预览）。
4. iOS？无。macOS 低功耗 120 秒门槛（`docs/MACOS_POWER_ACCEPTANCE.md`）未测量，
   macOS 产物只能标注为“待验收开发包”。
5. Windows 安装包/OTA/发布流程的端到端验证（需 Windows 远端，由另一代理负责）。

## 6. 父代理收口清单

### 6.1 收口项状态

1. ~~**B4** 发布验收脚本的工具集合~~ —— **已修**（`$expectedTools` 12、A5.3 9、
   A5.7 10、A5.R4 7，判定未削弱）；建议在 Windows 远端重跑一次 A5 取证据。
2. ~~**B6/B7** 模块侧过滤器与宿主 asset 接线~~ —— **已由 root 闭合**（精确三个 SDK 程序集名；
   `ProjectReference` + `AndroidAsset` 指向精确 stage）；三个模块包 canonical staging 已通过。
3. 待办：**三模块 + 宿主接线后的最终 APK 验证**（当前无验收用 APK）；
   `tests/MobileLayout.Tests` 的默认导航缺陷修复与新增用例；
   A5 远端复验。
4. 若要让子模块与模板版本对齐，按 **B5** 把 `MyPowerTools.Platform.Abstractions` /
   `MyPowerTools.AvaloniaSdk` 的引用显式升到 0.2.1（warning 级，不阻塞）。

### 6.1b 移动端模块集成状态

**已集成**：

- Remote Notifications（Android），依据 `tools/remote-notifications/android-integration/PARENT_INTEGRATION.md`：
  `$moduleStages` 加**必需**条目（`remote-notifications-android`，`Optional = $false`）；
  Android solution 加 `MyPowerTools.MobileNotifications`、`RemoteNotifications.Android` 及其测试；
  主 solution 加该测试项目。
- Remote Commands（Android），依据 `tools/remote-commands/android-integration/README.md` §5：
  `$moduleStages` 加**必需**条目（`remote-commands-android`，`Optional = $false`，
  stage = `tools/remote-commands/android-integration/artifacts/package/remote-commands-android`）；
  不传 `-SkipSurface`，按 root 决定传 `-NoMirror`（§2.3）；Android solution 加
  `MyPowerTools.MobileRemoteCommands`、`RemoteCommands.Android` 及其测试；主 solution 加该测试项目。
- 三条 stage 都带 `RequireFiles`（§2.3），缺 Surface 或 SSH 依赖即构建失败。

**后续闭合（root 完成）**：host 侧的 `ProjectReference` + `AndroidAsset` 已按各模块
stage 精确接线（B7）；Remote Commands 的 host-provided 过滤器已改为精确 SDK 程序集名，
三个模块包在 canonical 环境下均已构建并通过 staging 校验（B6）。
`MyPowerTools.Ipc.AspNetCore` 已由 Windows 代理设为 0.2.0 并进入
`scripts/build-sdk.ps1` 打包列表，本代理未改动它。

**当前唯一未闭合项**：三模块 + 宿主接线后的**最终 APK 尚未验证**（§2.1），
因此现在只能说“源码与模块 stage 就绪”，不能说“Android 应用已可用”。
Windows 开发版（active Dev build）与 8 个工具子模块推送由 root/Windows 代理完成（root 报告）。

### 6.2 建议执行的真实验证（在本代理已验证之外）

```powershell
# Windows：原 CI 全量（会跑 FileTransfer.Core.Tests 与 MobileLayout.Tests）
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\ci-local\Invoke-WindowsCi.ps1
# 或直接
dotnet restore MyPowerTools.slnx
dotnet build MyPowerTools.slnx --no-restore --maxcpucount
dotnet test MyPowerTools.slnx --no-build
# Android（Linux/Windows + Android workload；$moduleStages 里的三个模块会先构建）
pwsh scripts/build-android.ps1 -AndroidSdkDirectory $env:ANDROID_HOME -JavaSdkDirectory $env:JAVA_HOME
# 单独验证两个 Android 模块 stage（各自含清单自校验；RC 传 -NoMirror，与产品构建一致）
pwsh tools/remote-notifications/android-integration/build.ps1 -MyPowerToolsRepoRoot (Get-Location) -Configuration Debug
pwsh tools/remote-commands/android-integration/build.ps1 -MyPowerToolsRepoRoot (Get-Location) -Configuration Debug -NoMirror
# Windows 模块暂存（验证 build-all-tools 注册）
pwsh scripts/build-all-tools.ps1 -ToolId file-transfer
```

GitHub 上手动跑 `.github/workflows/android-preview.yml`（Debug、universal），
确认 APK artifact 与 summary 中的 ABI/CoreCLR/模块计数。

### 6.3 本代理改动但未提交的文件

`MyPowerTools.slnx`、`MyPowerTools.Android.slnx`、`scripts/build-android.ps1`、
`scripts/build-all-tools.ps1`、`scripts/update-windows-dev.ps1`、`scripts/publish-windows.ps1`、
`scripts/publish-macos-base.ps1`、`scripts/artifacts-policy.json`、
`scripts/verify-release-candidate.ps1`、`scripts/verify-release-candidate.remote.ps1`
（后两者为 root 本轮额外授权，只改了 `file-transfer` 相关的期望集合与计数）、
`tools/file-transfer/build.ps1`、`tools/file-transfer/tool-release.json`、
`tools/file-transfer/source-map.json`、`tools/file-transfer/package/**`、
`tools/file-transfer/src/**`、`tools/file-transfer/tests/**`、
`modules/file-transfer/**`（构建产物，历史上与其它模块一样纳入版本库）、
`.github/workflows/android-preview.yml`、`docs/ANDROID_PORT.md`、本文件。

本代理的 solution / stage 改动**引用**了其他代理新建、需一并提交的文件：
`tests/MobileLayout.Tests/**`、`tools/file-transfer/tests/FileTransfer.Surface.Tests/**`、
`src/MyPowerTools.Platform.Android/tests/MyPowerTools.Platform.Android.Tests/**`、
`src/MyPowerTools.MobileNotifications/**`、
`src/MyPowerTools.MobileRemoteCommands/**`（`INTEGRATION.md`、`.csproj`、`*.cs`）、
`tools/remote-notifications/android-integration/**`（含 `PARENT_INTEGRATION.md`、
`build.ps1`、`src/RemoteNotifications.Android/**`、`tests/RemoteNotifications.Android.Tests/**`、
`package/**`、`manifest/**`）、
`tools/remote-commands/android-integration/**`（含 `README.md`、`build.ps1`、
`src/RemoteCommands.Android/**`、`tests/RemoteCommands.Android.Tests/**`、`package/**`、`manifest/**`）。
提交前请确认这些目录都在（缺任何一个，solution 会解析失败或 stage 断言会失败）。

`tools/remote-notifications/android-integration/artifacts/**`、
`tools/remote-commands/android-integration/artifacts/**`（含其 `.tmp/`、`.verify-feed/`）
与 `tools/file-transfer/artifacts/**` 属于工具内构建产物/临时目录，不入库；
`modules/file-transfer/**` 因历史惯例（其它模块同样提交）需要入库。
`modules/remote-commands-android/**` **不应存在**：Remote Commands 是 Android-only 模块，
产品构建传 `-NoMirror`，root 已删除该生成目录，避免 Windows 上出现重复的 Android 卡片。

### 6.4 本代理临时文件已清理

`artifacts/.tmp-home`、`artifacts/.tmp-dotnet-home`、`artifacts/.tmp-pwsh`、
`artifacts/.tmp-verify`、`artifacts/.tmp-sdk-fresh`、
`tools/file-transfer/artifacts/package.staging` 已删除。保留的是产物/证据：
`artifacts/build/bin/MyPowerTools.Android/**`（本轮 APK）、
`artifacts/sdk/nuget/**`（重 pack 后的 SDK feed）、
`artifacts/test-results/**`（他人创建的布局测试证据，已按 `test-results/*` 声明）。
`artifacts/.tmp-android-verify` 不是本代理创建，未删除。
