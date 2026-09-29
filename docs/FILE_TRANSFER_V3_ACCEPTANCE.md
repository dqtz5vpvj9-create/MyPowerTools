# 文件助手 V3 验收记录

日期：2026-09-29。当前是开发验收，尚未完成新版发布。下列结果对应各自测试时的开发包，最终统一包仍需复验。

## 已观察到的界面行为

Windows 使用完整安装布局上的 Dev overlay；Android 使用专用测试设备的 user10，未清空 owner0 数据。

- 私聊通过实际发送按钮双向收发，发送端无需离开页面即可收到真实“已送达”回执。
- 共享文字和 1.6 MB 图片在双端页面保持打开时自动到达；图片发送端从 0% 更新为“已同步”，接收端显示“已接收”。
- 私聊与共享草稿相互独立；退出进程后文字及分享加入的图片草稿恢复，分享不会自动发送。
- Windows 本机 OpenList 自动准备运行时、启动、管理页 HTTP 200、停止均已验证，原网盘配置逐字不变。
- Windows Input Monitor 保持原已安装版本，未覆盖为仓库中的旧模块。

证据目录：`/mnt/cache/data-cache/mpt-v3-windows/RESULT.md`、`/mnt/cache/data-cache/mpt-v3-android/QA-NOTES.md`。临时证据目录会自动清理，正式发布需归档最终验收证据。

## 网络结论

这轮手机私聊验证使用公网路径。测试 Android 设备能把 Tail relay 域名解析成 100.64.0.1，但 TCP 80 超时；Windows 对未注册的 Tail inbox 请求失败后自动走公网并取得回执。以上证明公网回退，不能作为 Tail 端到端成功证据。

用户实际手机的 Mihomo `type: tailscale` 是 userspace Tailnet 节点；是否能建立 MPT 直连仍要对具体设备、方向和端口探测。系统接口没有 Tail IP、域名可访问、节点在线都不能单独证明或否定该文件传输路径。

公网服务器原部署正常，但到 Tail relay 的网络路径缺失。独立 userspace connector 正在补齐；启用共享文件代理前须验证认证下载、固定目标转发和重启恢复。

## 当前发布阻碍

Android 官方 OpenList v4.2.6 x86_64 二进制在实际应用进程内初始化 SQLite 时触发 SIGSYS：modernc libc 使用了应用 seccomp 禁止的 lstat 系统调用。run-as CLI 成功不能代替应用内验证。兼容 runtime 重建和实际按钮启停验收尚未完成。

共享文件 Tail 存储与公网按需代理正在实现，需通过图片兼容、旧客户端恢复请求、发送端离线领取和最终双端界面验证。

## 最终统一包还需检查

1. Android 无诊断构建覆盖安装，实际启用、管理页访问、停止 OpenList；确认已保存自定义网盘配置不变。
2. 分享从高级页返回会话选择，私聊草稿恢复，短会话没有虚假的新消息提示。
3. 公网代理部署后，旧 V1 下载原路径仍能取到 Tail 文件；关闭发送端后继续可取。
4. 最终 Windows Dev overlay 启动，保留 Input Monitor 基线；Android 恢复 owner0 原草稿、停用 user10 临时辅助服务。
5. 提交并推送对应源代码，提升 Android 版本并发布可下载 APK，验证发布下载，再发送邮件通知。

本轮没有实际网盘账号授权测试、ARM64 真机 OpenList 运行证据或 macOS 功耗验收；不能据此声称这些项目通过。
