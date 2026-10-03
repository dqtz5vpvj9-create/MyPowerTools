# DDNS 流程覆盖

执行：`npx e2e run tests/e2e/ddns.e2e.ts`。

测试调用真实 `tools/ddns/ddns.ps1` 与 `install-ddns-task.ps1` 入口。配置与状态写入每次运行专属临时目录，完成后删除。DNSPod HTTP、计划任务注册与 watch 的等待由隔离进程中的替身处理；测试使用保留域名 example.test、文档地址 203.0.113.0/24 和虚构 token。

| 用户流程 | 自动验证 |
| --- | --- |
| 首次查看状态 | 无更新时间；零 API 调用 |
| 查看已有状态 | 原样读取持久状态；零 API 调用 |
| 无记录时更新 | Record.List → Record.Create；保存状态与请求值 |
| IP 变化时更新 | Record.Modify 更新正确记录 |
| IP 未变化 | 零写入；保留全部重复记录 |
| 强制更新和清理重复项 | 保留匹配 IP 的记录；只删除其他记录 |
| 列出记录 | 返回全部记录；状态文件保持不存在 |
| DNSPod 拒绝 | 错误包含供应商信息；零成功状态 |
| 非法 IPv4 | API 调用前拒绝；零成功状态 |
| watch 更新失败 | 写 ERROR 日志并进入下一次等待 |
| 注册计划任务 | 显式配置路径（含空格）进入任务参数；正确间隔 |
| 更新脚本分发 | 在隔离目录执行真实 build.ps1；package 与 service 脚本 SHA256 等于源脚本 |

本轮修复：更新入口接受任意 OverrideIp 字符串并发送为 A 记录；计划任务忽略调用者提供的 ConfigPath；DDNS 缺少工具构建入口，分发的 package/service 脚本滞后于源文件。三项均有行为回归测试。新增 build.ps1 为 Dev overlay 提供当前脚本与源 module.json。

待实际环境验收：缺配置时默认搜索路径、watch 缺配置后恢复与长期周期、物理网卡/CIM 回退、公网 IP 服务回退、真实 DNSPod 账户与网络错误、ServiceManager 启停及重启、Windows 计划任务实际权限和后台执行、SSH 部署和远程端首次更新、MPT CLI 路由及桌面状态展示。离线替身测试无法证明这些环境行为；测试没有修改生产 DNS 或注册真实任务。
