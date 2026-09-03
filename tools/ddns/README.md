# MyPowerTools DDNS (腾讯云 / DNSPod)

轻量 DDNS 服务，初期只支持腾讯云 DNSPod（`dnsapi.cn`）。

以 MPT 第一方 Service Unit 随发布包分发：`service-units/ddns.service/` 由
ServiceManager 托管（`autostart: true`），安装新版后自动注册并常驻运行；
CLI 通过 `mpt ddns status|update|list|watch` 使用。

## 功能

- 从指定网卡（如 `Realtek USB 2.5GbE Family Controller`）读取 IPv4，或从公网
  服务获取出口 IP。
- 查询 DNSPod 中主域名/子域名的 A 记录；不存在则创建，存在且 IP 变化则修改。
- `clearSameNameRecords=true` 时，只有在记录真的被写入（创建或更新）的那一次才删除同名
  多余 A 记录；保留取值已等于当前 WAN IP 的那条，没有则保留第一条。示例配置默认关闭，
  IP 未变化的轮询不会动任何记录。
- 支持 `update`（执行一次）、`watch`（按 `checkIntervalMinutes` 循环）、
  `status`（查看最近一次状态）、`list`（只读列出同名记录）。
- 每次结果写入 `ddns-state.json`，并追加 `ddns.log`。

## 使用

```powershell
# 复制示例配置并填入 DNSPod 密钥
Copy-Item ddns-config.example.json ddns-config.json
# 编辑 ddns-config.json：secretId / secretToken / mainDomain / subDomain

# 立即更新一次
pwsh -File ddns.ps1 -Command update

# 查看最近状态
pwsh -File ddns.ps1 -Command status
```

## 注册为计划任务（每 N 分钟执行一次）

在目标 Windows 机器上（例如 dorm）执行：

```powershell
pwsh -File install-ddns-task.ps1
```

任务名 `MyPowerTools DDNS`，按配置中的 `checkIntervalMinutes` 周期执行
`update`。也可用 `watch` 常驻进程由 ServiceManager 托管。

## 安全说明

`ddns-config.json` 内含 DNSPod API Token（等同账号密码）。不要提交到仓库；
示例文件 `ddns-config.example.json` 使用占位符。生产建议改用 Tencent Cloud API
密钥并加密存储。
