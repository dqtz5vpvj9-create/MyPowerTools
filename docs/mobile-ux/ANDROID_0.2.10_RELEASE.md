# Android 0.2.10 发布验收

版本 0.2.10 / versionCode 12，GitHub tag `android-v0.2.10-preview.1`。沿用 Android 开发预览渠道及此前调试签名；不替换桌面 stable/OTA，不表示已完成所有 UI 与平台验收。

## 内容

包含 `da21fc4` 的夸克匿名分享领取、后台服务及网络恢复、停滞连接时限、单附件错误隔离、多设备回执有限分批扫描、手动刷新及一致状态快照修复。此发布提交只提升 Android 版本及记录验收，功能源码与上一轮通过的修复一致。

具体修复、真实夸克发送方退出后领取、389 项 Core 回归（386 通过、3 跳过）、0.267% 单核后台 CPU 测量及边界见 [修复验收报告](E2E_POWER_CLOUD_20261005.md)。CPU 测量来自本次同修复代码的 0.2.9 QA 包，不将其标成 0.2.10 新测量。

## 打包和升级

通过 `scripts/build-android.ps1` 完整重建，Android 主机测试 103/103 通过。APK 为 `MyPowerTools-Android-0.2.10-preview.apk`，238,552,264 字节，包含 arm64-v8a / x86_64 的 CoreCLR。apksigner 验证通过，与从 GitHub 下载的已发布 0.2.9 APK 签名相同。包内 Core/插件与本次 staging 相同；工作区未提交的桌面 DLL 没有打入 APK。

两台 Pixel 4a / Android 13 从上一轮 0.2.9/code11 QA 包，均通过 `adb install -r` 升级为 0.2.10/code12。08191 保留原 80 条记录，08111 保留原 55 条记录；设备/会话身份、草稿均保持，启动后前台接收服务正常。未清除应用数据或改动日用会话。

x64 仅校验打包，未做真机验证。百度免登录分享下载、既有无障碍定位及部分 UI 问题仍未完成。Windows/macOS 本次不发布。

非敏感原始证据位于 `/mnt/cache/data-cache/mpt-release-android-0.2.10/` 的 `package-evidence.json`、`upgrade-evidence.json`；构建日志为 `/mnt/cache/data-cache/mpt-android-0210-build.log`。不提交账号、连接码、QA 私有状态或邮件确认令牌。

发布 APK 的真机后台套件 4/4 通过（`.e2e/mobile-release-0210`，149.55 秒）：后台约 5.55 秒、锁屏约 5.35 秒收件；普通强制 Doze 内已收到；Doze+应用断网期间未收到，恢复后约 7.40 秒完成。均核对文件内容、完成进度、唯一消息及发送端实际回执。deep Doze、测试电池覆盖及 UID 断网规则已恢复原状，见 `restoration-evidence.json`。
