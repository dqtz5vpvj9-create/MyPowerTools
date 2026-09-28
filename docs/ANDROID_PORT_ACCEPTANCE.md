# Android 开发预览验收（2026-09-27）

实现与构建说明见 [ANDROID_PORT.md](ANDROID_PORT.md)。本次应用复用 MPT 共享底座，
完成手机 Shell、共享页面及 12 个桌面工具 Surface 的窄屏适配。Android 当前接入文件互传、
远程通知和 SSH 远程命令三个工具模块，其余工具按平台能力显示支持状态。

## 自动化验证

| 套件 | 结果 |
| --- | --- |
| Runtime 验收 | 52 通过；重复模块目录的诊断回归单独通过 |
| FileTransfer Core | 55 通过 |
| FileTransfer Surface | 24 通过 |
| MobileLayout | 24 通过 |
| Platform.Android 规则 | 16 通过 |
| Remote Notifications Android | 64 通过 |
| Remote Commands Android | 68 通过 |
| Remote Commands 手机设置 UI | 4 通过 |
| IPC 契约 | 8 通过 |

MobileLayout 覆盖 320/360/390/768 宽度、导航、连续分享、手动打开后分享、返回路径，
以及模块启动事件不会把工具页替换成工具目录、事件监听不捕获 UI 上下文的回归。
Shell 单元测试以托管控件验证页面行为；
实际工具程序集加载另由下述 Android 安装验证覆盖。

SSH 测试经过真实 HostControl `JsonStructMapper` 往返，验证整数变为 protobuf double 后仍能
保存设置和非默认端口。通知测试包含真实跨进程历史写入互斥。

A1 架构检查通过（600 个生产 C# 文件、8 处 Kestrel 管道注册），产物目录治理检查通过。
主 solution 声明 79 个项目，Android solution 声明 13 个项目；桌面构建不依赖 Android workload。

## Android 安装与操作验证

环境为专用 Android AVD（console 5682 / adb 5683）。安装包由完整构建脚本生成，
主体操作验证基于 `711902c`，后续 APK 修复连接码日志、文件名和快速传输状态；
最终 APK 对应代码版本 `50d08d9`，同时包含 arm64 与 x86_64，使用实验性 CoreCLR 和开发签名。
它是版本 0.1.0 的开发预览。

| 检查 | 结果 |
| --- | --- |
| 全新安装与启动 | 通过，三个 Android 模块可加载；日志无 EACCES、quarantined 或 FATAL EXCEPTION |
| 首页、工具、活动、设置导航 | 通过，实际页面和选中状态跟随点击 |
| 冷启动单文件、多文件分享 | 通过，直接进入文件互传并显示所分享文件 |
| 热启动连续分享 | 通过，同一页面保留并累加文件 |
| 系统返回键 | 通过，工具页 → 工具目录 → 首页 → 后台 |
| 设备与网盘连接码 | 通过，打开后预填，用户确认才导入 |
| 远程通知页面 | 通过；未配置签名密钥时说明所缺配置，开启后台接收会明确拒绝 |
| SSH 设置 | 非法值 9 被拒；有效值 299 保存到 JSON，正常重启后页面仍为 299 |
| OpenList 实际文件收发 | 同一 Local driver 服务中连续上传 alpha.txt、beta.txt 并下载 android-inbound.txt，文件名与内容正确 |
| 首次传输自动完成提示 | 最终 APK 冷启动后单次点击发送，随后不触屏；2 秒、5 秒、8 秒截图均显示已完成、进度条隐藏、发送恢复可用 |

截图在 `artifacts/.tmp-android-verify/shots/final-*.png`，本轮日志在
`artifacts/.tmp-android-verify/logs/`。SSH 的 wire 回归另覆盖数值 300；设备操作使用 299，
因为该 AVD 的 ADB 键盘注入把数字 0 错映射为 q。

## OpenList 可复现测试

使用官方 OpenList v4.2.6 的 Local driver、独立目录及一次性账号，不使用真实网盘凭据。
准备脚本已实际执行，账号能列举、上传和下载预置文件。手机实际下载了 41 字节的
`android-inbound.txt` 到系统“下载/MPT”，实际上传了 11 字节的测试文件到服务端，
两端内容均与源文件逐字节一致。最终包再次上传 `alpha.txt` 成功，单次点击后不再需要
滚动或其他输入才能显示完成。操作过程中实际授予了系统通知权限。
这验证的是官方 Local driver 与 Android 文件链路，国内网盘驱动尚未用真实账号验证。

```bash
TMPDIR=/mnt/cache/data-cache/mpt-file-transfer \
bash tools/file-transfer/tests/android-dataplane-fixture.sh start
```

此脚本输出手机连接码及验证步骤。跨主机测试需先把 localhost 端口转发至 ADB 所在机器，
再对测试设备使用 `adb reverse`。完成后运行同一脚本的 `stop` 命令。
普通 Core 测试中的 fixture 用例默认跳过，需脚本提供的环境变量才执行。

## Windows 与实际网络边界

Windows 使用完整安装目录上的 Dev overlay 验证，开发目录为独立源码副本。
2026-09-28 的回归排查确认，移动适配分支漏合入了 Windows 开发分支的改动，
使 Input Monitor 的休息提醒退回旧实现。现已合并原有的深色提醒、高 DPI 布局、
前台应用识别和提醒周期逻辑，并保留移动布局。Windows 工作目录中的未提交源码也已
纳入恢复合并，包括 Paste Image 的历史预览、NSSM 的服务操作界面和 Local Lag Cleaner
的一键操作；原 Windows 工作目录和用户数据保留。

Core、Input Monitor、NSSM Manager、Paste Image 和 Local Lag Cleaner 已通过官方 Dev
更新及启动检查，HostControl 的 8 项接口检查通过。Input Monitor、NSSM Manager 和
Paste Image 的安装包与构建产物逐字节一致；Local Lag Cleaner 的 Dev 元数据与暂存包
一致，其 SDK 工具程序集已重建并保留一键操作。后续工具更新重启 Runner 后，
Input Monitor 的安装包仍与恢复后的构建一致。更新验收曾因
仪表盘读取同步等待所有工具的健康检查而超时回滚；现在仪表盘读取缓存，显式刷新命令仍
执行健康检查。此修复通过 53 项 HostControl/InProc 回归检查，并用慢模块验证旧行为失败、
新行为通过。

恢复合并后的检查还包括 Input Monitor Core 20 项、父仓库 Input Monitor 12 项、
Local Lag Cleaner 与 SDK 契约 29 项、NSSM Broker 安全检查 15 项，以及所有工具在
320/360/390/768 四种宽度下的布局检查。NSSM 完整测试在 Linux 上仍有 22 项依赖
Windows 原生 API 的失败，与恢复前基线相同；没有把这些用例计为通过。

RDP 会话断开导致无法获取可用桌面截图，窗口句柄与进程响应也不等于视觉验收。
本次没有发布 Windows/macOS 安装包。

真实 Tailscale 跨设备直传、国内网盘账号中转、SSH 服务器连接、签名通知服务及厂商后台策略
尚未完成端到端验证。通知服务缺少签名凭据，因此尚未验证其前台服务和真实消息接收。
macOS 未进行实机 120 秒低功耗测量。
