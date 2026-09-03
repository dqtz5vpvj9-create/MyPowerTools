# 2026-09 macOS OTA 接手完成记录

工作分支：`codex/macos-ota-completion-2026-09`

## 已完成

macOS OTA 已形成从发布、发现、下载到事务替换的完整路径：

1. `scripts/publish-macos.ps1` 在原有应用布局上补入 CLI、更新器、安装器与签名公钥，再执行最终代码签名。
2. 发布资产采用 .NET RID 命名：`MyPowerTools-osx-arm64.zip`、`MyPowerTools-osx-x64.zip`，同时生成对应 manifest 与平台频道 feed。
3. `-Version` 会写入外层应用与三个 helper bundle 的 `CFBundleShortVersionString`、`CFBundleVersion`，避免 Git tag 与 `version.json` 暂时不一致时产生错误版本。
4. manifest 在最终 codesign 之后生成，记录实际发布字节；feed 绑定包哈希与 manifest 哈希。
5. bundle 内的公共入口为 `Contents/Resources/scripts/ota-update.ps1`，代码来源是 `scripts/ota-update-macos.ps1`。源码目录中的 CLI 会优先选择 macOS 专用入口。
6. 更新器根据已安装状态与 app Mach-O 架构选择 `osx-arm64` 或 `osx-x64`，不会因 Rosetta 下的 PowerShell 架构选错资产。
7. macOS 更新器只接受当前架构的完整包，拒绝 delta，并验证 Ed25519 feed、包 SHA-256、manifest SHA-256 与 manifest 版本。
8. 应用阶段委托给 `ota-apply-macos.ps1`，沿用 bundle 备份、LaunchAgent 维护模式、健康检查与失败回滚。
9. `install-macos.ps1` 支持 `-SkipOtaState`。OTA 事务完成后由更新器写入下载得到的发布 manifest；普通安装会生成初始状态，首次检查不会回落到 `0.0.0`。
10. 更新器在应用前复制到数据目录中的 bootstrap 位置，并通过排他文件锁阻止并发 apply。
11. 旧的 `MyPowerTools-macos-arm64.zip` 与 `MyPowerTools-macos-x64.zip` 暂时保留为兼容别名；OTA feed 始终引用规范的 `osx-*` 资产。

## 发布自动化

- `.github/workflows/macos-ota-validation.yml` 在 macOS runner 上执行 OTA 契约测试、构建 ad-hoc 签名候选、校验 bundle 与发布资产。
- `.github/workflows/macos-ota-release.yml` 在稳定版 tag 或人工触发时构建 arm64 与 x64，使用 `MPT_OTA_SIGNING_KEY_BASE64` 签署平台 feed，并把规范资产上传到已有 GitHub Release。
- `.github/workflows/handoff-windows-gates.yml` 恢复交接补丁中的五项 Windows 检查：Quick 与 Process 架构门禁、AndroidTools 运行时 staging、AndroidTools 正向测试、NSSM PowerShell 测试。
- 原交接补丁针对 `.github/workflows/ci.yml`。GitHub Actions 自带令牌缺少 workflow 文件写权限，因此这些检查以独立工作流落地，执行命令与交接补丁保持一致。
- 稳定频道缺少签名密钥时直接失败。nightly 可明确生成 unsigned feed，客户端仍需显式传入 `-AllowUnsigned`。

## 兼容性决定

macOS 只发布完整包。文件级替换无法可靠保存 bundle 中的执行位、符号链接与代码签名封装，因此 `invoke-ota-update.ps1` 继续专用于 Windows delta 事务。

Windows 的历史频道文件名保持不变。macOS 使用独立频道文件：

```text
channel-stable-osx-arm64.json
channel-nightly-osx-arm64.json
channel-stable-osx-x64.json
channel-nightly-osx-x64.json
```

## 自动验证范围

`MacOtaCompletionTests` 与 macOS workflow 检查以下契约：

- CLI 与 OTA 脚本进入 app bundle；
- 源码与安装包分别选择正确的 OTA 入口；
- arm64 与 x64 资产选择；
- macOS feed 拒绝 delta；
- apply 脚本使用安装器支持的 `-SkipOtaState`；
- manifest 在最终代码签名之后生成；
- archive、manifest 与频道 feed 使用统一 RID 名称；
- tag 版本写入外层 bundle 与 helper bundle；
- 完整 bundle 通过 `codesign --verify --deep --strict`。

Windows 门禁工作流检查以下交接项：

- A1、A2 架构门禁；
- A3、A4 进程与故障隔离门禁；
- AndroidTools 模块主机 staging 与正向测试；
- NSSM 原生资源与服务模式 smoke 测试。

## 仍需设备验证

以下项目依赖交互式 macOS 会话或真实显示设备：Carbon 全局热键投递、通知点击激活、`LSUIElement` 行为、内置屏幕 gamma，以及 Developer ID 与 notarization 条件下的 Gatekeeper 行为。代码路径与发布资产由 macOS CI runner 验证，交互行为仍按原交接文档中的真机步骤执行。

三项需要产品设计决定的安全边界保持原状，本次没有顺带改变命名管道授权、`input-remap` 提权协议或 ServiceManager 工具作用域。
