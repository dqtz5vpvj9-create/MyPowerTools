# MyPowerTools DDNS (腾讯云 / DNSPod)

轻量 DDNS 服务，初期只支持腾讯云 DNSPod（`dnsapi.cn`）。

## 功能

- 从指定网卡（如 `Realtek USB 2.5GbE Family Controller`）读取 IPv4，或从公网
  服务获取出口 IP。
- 查询 DNSPod 中主域名/子域名的 A 记录；不存在则创建，存在且 IP 变化则修改。
- `clearSameNameRecords=true` 时删除同名多余 A 记录，只保留一条（覆盖语义）。
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
