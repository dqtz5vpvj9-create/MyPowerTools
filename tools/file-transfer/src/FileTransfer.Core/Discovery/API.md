# F2 设备发现接口（给 M4）

命名空间 `FileTransfer.Core.Discovery`，目录 `tools/file-transfer/src/FileTransfer.Core/Discovery/`。
本目录只做发现与身份查询：不写模块、不写 Surface、不生成 token、不自动信任任何结果。

## M4 需要调用的两个入口

```csharp
// 1) 打开设备面板时的一次有界发现（不轮询，取消即停止）
var discovery = new DeviceDiscovery(options, probe: null, lanChannel: beacon.IsRunning ? beacon.Channel : null);
DiscoveryReport report = await discovery.DiscoverAsync(known, cancellationToken);

// 2) 用户开启“可被发现”时的被动服务（事件驱动，可随时关闭）
var beacon = new DiscoveryBeacon(options);
await beacon.StartAsync(localDevice, cancellationToken);   // false + Fault 表示明确失败
await beacon.StopAsync();                                   // 幂等；禁用后不残留 socket
```

`known` 用现有 peers 设置构造：`new DiscoveredDevice(peer.DeviceId, peer.Name, peer.Address, TransferFiles.Port, platform: "")`。
`localDevice` 用本机身份：`new DiscoveredDevice(Setting("deviceId"), DeviceName(), Setting("listenAddress"), TransferFiles.Port, 平台字符串)`；
`DiscoveryOptions.LocalDevice` 也要设置同一份（局域网 who-is 需要它）。

## 结果到 Surface 字段

```csharp
report.Devices  -> devices[]      // DiscoveryDeviceResult(Device, Paired, Available, Source, Message)
report.State    -> discoveryState // "completed" | "partial" | "unsupported"
report.Message  -> message?       // 首条诊断，列表为空时显示
```

`Device` 字段就是合同里的 `deviceId,name,address,port,platform`。
`Available` 只有在对方**真实应答了 v3 hello** 后才为 true；`Paired` 表示这是已记录设备。
`partial` = 有界窗口到期，结果是已完成部分的真实值；`unsupported` = 本平台没有可用来源（例如
Android 没有 Tailscale CLI/LocalAPI 且局域网也不可用）。M4 不得把 `unsupported` 显示成“没找到设备”。

## 关键类型

| 类型 | 说明 |
| --- | --- |
| `DiscoveredDevice(string DeviceId,string Name,string Address,int Port,string Platform)` | 合同记录；`Address` 只接受 Tailnet（100.64/10、fd7a:115c:a1e0::/48）或 loopback |
| `DiscoveryOptions` | `Window`(默认 5s)、`ProbeTimeout`(3s)、`ConnectTimeout`(2s)、`MaxConcurrency`(8)、`MaxCandidates`(64)、`Port`(=`TransferFiles.Port`)、`LanPort`(47166 会合端口)、`LanBindPort`(默认同 LanPort，仅测试用不同值)、`LocalDevice`、`AnnounceInterval`(默认 null=不周期广播) |
| `DeviceDiscovery` | `DiscoverAsync(IReadOnlyList<DiscoveredDevice> known, CancellationToken)`；构造可注入 `sources`、`probe`、`lanChannel` |
| `DiscoveryBeacon` | 被动广告服务；`StartAsync/StopAsync/IsRunning/Fault/Channel/PeerObserved/AnsweredSolicitations`。`PeerObserved` 是可选的被动入口：M4 收到事件后可用 `HelloIdentityProbe` 确认该设备再刷新列表 |
| `HelloProtocol` | v3 帧编解码（见下），M4 接收端复用 `IsHello` / `ReadRequestAsync` / `WriteReplyAsync` |
| `HelloIdentityProbe` | v3 hello 客户端（TCP），也可单独使用 |
| `LanDiscoveryChannel` | 真实 UDP 多播通道，beacon 与 discovery 共用同一实例 |
| `LanReplyLimiter` | 被动应答的每来源限流（默认每来源 1 秒 1 次，表上限 64） |
| `LanDiscoveryChannel.Describe()` / `DiscoveryBeacon.Describe()` | 给诊断/日志用的一行状态（是否在听、限流次数、错误） |
| `TailscalePeerSource` / `TailscaleStatusReader` / `TailscaleEnvironment` | Tailscale CLI 与 LocalAPI 候选来源 |

## M4 在现有 DirectReceiver 里要加的 v3 身份应答

帧格式与现有传输一致：int32 大端长度 + UTF-8 JSON。

```
请求（F2 客户端发出，无 token、无密钥）：{"version":3,"kind":"hello"}
应答：{"ok":true,"deviceId":"…","name":"…","address":"…","port":47165,"platform":"windows"}
```

接收端已有 `DirectTransfer.ReadJsonAsync<DirectTransfer.Offer>`，只需在版本分流处加一个分支：

```csharp
var offer = await DirectTransfer.ReadJsonAsync<DirectTransfer.Offer>(stream, handshake.Token);
if (HelloProtocol.IsHello(offer))            // offer.Version == 3
{
    await HelloProtocol.WriteReplyAsync(stream, new HelloProtocol.Reply(
        Ok: true, DeviceId: _deviceId, Name: _deviceName,
        Address: Setting("listenAddress"), Port: TransferFiles.Port, Platform: <平台>), token);
    return;                                   // 不落盘、不改收件箱、不需要密钥
}
```

注意：

- hello 与 v2 探测一样**不写任何文件**，但它是**无密钥**的，只暴露公开信息（设备名/设备 id/平台/端口/Tailnet 地址）。
  发现不是授权：文件传输仍必须校验接收密钥，F2 不生成也不传递 token。
- 现有接收循环只接受 Tailnet/loopback 来源，保持这一条即可。
- 应答必须带全部字段；缺字段的应答会被 F2 判为 `LegacyProtocol` 且不计入 available。
- 旧版接收器会对 v3 返回 `{ok:false,…}`，F2 记为 `Rejected`：已配对候选保留在列表里但 `Available=false`。
- 端口固定用 `TransferFiles.Port`（当前 47165，`DirectReceiver` 实际绑定的端口）。F2 不新猜端口。

## 局域网发现协议（UDP，仅公开信息）

- 组播组 `239.255.77.80`，UDP 端口 47166，TTL=1，绑定 `0.0.0.0:47166` 且 `SO_REUSEADDR`，
  在每个可用接口加入组播组（无接口时回退 loopback）。V4 only。
- 报文（UTF-8 JSON，≤512 字节）：
  `{"mpt":"mpt-discovery","v":1,"kind":"who-is"|"here","protocol":3,"deviceId","name","address","port","platform","nonce"}`
  `nonce` 为 16 位十六进制。`who-is` 发到组播组；`here` 只单播回该数据报的源端点并原样回填 `nonce`。
- 发送/接收都是真实 socket；接收循环阻塞在 `ReceiveFromAsync`，取消即退出，无轮询、无定时器
  （`AnnounceInterval` 默认关闭，只有用户/模块显式设置才会周期广播，最小 1 秒）。
- 校验：来源必须是 loopback/私网/链路本地/Tailnet；magic、版本、kind、nonce、deviceId、名字、
  端口、平台都要合法，且 `address` 必须是 Tailnet 地址——公网地址一律丢弃，绝不发起外连。
- 限流：同一来源 1 秒最多应答一次，避免被用作放大反射器；表大小有上限。
- 重入：`StartAsync` 二次调用抛 `InvalidOperationException`；`StopAsync` 幂等；停止后可再次启动并重新绑定。
  `DiscoveryBeacon.StartAsync` 同一身份重复调用是 no-op，身份变化则重启。
- 文件永远不走这条路：局域网报文只给 Tailnet 地址，真正的传输仍走 Tailnet + 接收密钥。

## Android（root/M7 需要接的原生部分）

F2 只有托管代码，Android 上要让多播真正收到报文，需要：

1. `AndroidManifest.xml` 增加 `android.permission.CHANGE_WIFI_MULTICAST_STATE`（normal 权限，安装即授予，
   无运行时弹窗）。这是**收到**组播的必需权限。
2. 要在持有 Wi-Fi 多播锁期间才能收到组播报文：
   `var wifi = (WifiManager)context.GetSystemService(Context.WifiService);`
   `_lock = wifi.CreateMulticastLock("mpt-discovery"); _lock.Acquire();`
   在 beacon 启动 / 一次发现开始时获取，结束或用户关闭“可被发现”时 `Release()`。不获取时通常仍能发送，
   但收不到 who-is/here。请让锁的生命周期与 `DiscoveryBeacon` 完全一致，禁用工具时必须释放。
3. `ACCESS_WIFI_STATE` 只在 M7 还想判断“当前是否为 Wi-Fi 链路”时需要（创建多播锁本身只依赖
   `CHANGE_WIFI_MULTICAST_STATE`）；Android 13+ 的 `NEARBY_WIFI_DEVICES` 不适用于 MulticastLock。
4. 若要支持息屏后仍可被发现，再按需持有 `WifiLock`（默认 `WIFI_MODE_FULL` 即可，不需要 HIGH_PERF），
   否则 Wi-Fi 省电会中断被动监听；不打算支持息屏发现就不要常驻持有。
4. Android 上 `TailscaleEnvironment.Detect()` 返回 `Supported=false`，F2 会给出明确诊断；不要试图在
   Android 里调用 tailscale CLI。发现只能靠局域网 + 已记录设备。

没有拿到多播锁时，`LanDiscoveryChannel.StartAsync` 可能仍返回 true 但收不到报文——这属于 M7 集成项，
不是 F2 的静默成功：`assistant.devices` 会显示“局域网发现没有收到应答”的诊断。

## 平台使用注意（macOS 低功耗门槛）

- `DiscoverAsync` 是一次性有界窗口，关闭面板即取消；没有后台轮询。
- `DiscoveryBeacon` 默认只在启动时发 1 个 who-is，其余时间阻塞在 socket 上等待事件；
  只有 `DiscoveryOptions.AnnounceInterval` 被显式设置时才周期广播（最小 1 秒）。工具被禁用时必须
  `StopAsync()`/`DisposeAsync()`，否则会残留 socket。
- Tailscale CLI 只在一次发现窗口内运行一次，超时 5 秒，输出上限 8 MB。

## 验证证据与未验证项（2026-09-27，Linux 主机）

已验证：

- 真实 `tailscale status --json`（`/usr/bin/tailscale`，1.102.2，BackendState=Running，7 个 peer）被解析为 7 个候选；
  LocalAPI 走真实 `/var/run/tailscale/tailscaled.sock` 返回同一份状态（10495 字节）；把 CLI 路径清空后仅靠 LocalAPI
  仍能读到 7 个 peer。
- 真实 Tailnet 地址（100.64.0.1，非 loopback）上的一次 v3 hello 往返：请求 `{"version":3,"kind":"hello"}`，
  应答被校验后设备成为 available；同一窗口内 7 个真实 peer 都没有应答，因此一个都没有出现在设备列表里
  （没有假可用），整个过程 5.5 秒（窗口 6 秒）。
- 真实 UDP 多播往返（loopback 接口）、被动应答、限流、禁用后不再监听、重复启动/停止/重启。
- `FileTransfer.Core.Tests` 全量 162 项：160 通过 / 2 跳过 / 0 失败；其中 Discovery 定向 47 项全部通过。
- `net10.0` 与 `net10.0-android`（SupportedOSPlatformVersion 29）两个目标框架均编译通过。

未验证（需要真实设备/平台，交给 root/M7 或后续验收）：

- Windows 命名管道 LocalAPI 与 macOS App Store 沙盒 socket 路径只做了编译验证，没有在真实 Windows/macOS 上运行过。
- Android 运行时的多播接收依赖 M7 的多播锁（见上）；没有真机证据前不能宣称 Android 局域网发现可用。
- 局域网发现协议为 IPv4-only；IPv6-only 局域网不在本次范围内。
