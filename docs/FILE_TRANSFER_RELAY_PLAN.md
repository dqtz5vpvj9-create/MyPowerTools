# 文件助手传输层：本机中转与内嵌 OpenList

2026-09-29。会话 UI 的“通讯录 → 私聊 / 文件传输助手公屏”与路由分离。设备身份和配对码由本地产生；生成身份、打开对话和排队发送都不要求 Tailscale、网盘或中转服务在线。

## 数据路径

| 路径 | 使用条件 | 内容经过哪里 |
| --- | --- | --- |
| 设备直连 | 本次发送端能访问收件端的传输端点且认证成功 | 两台设备之间；底层可能是 LAN、Tailscale 直连或 DERP |
| 本机 Tailnet 中转 | 两端能访问同一中转 URL，包括经 Clash 访问 | 本机磁盘持久队列，不经过公网 MPT 中转 |
| 网盘 | 用户已授权网盘，两端拥有同一挂载目录的适当权限 | 设备内嵌 OpenList 对接网盘 API，数据仍需上传网盘 |
| 公网中转 | 上述路径不可用或没有配置 | 现有 proxy.lixinrui000.cn，保留离线接收能力 |

“能访问 Tailnet 服务”不等于“设备自身有 Tailnet IP”，也不证明另一端能反向连接。Clash 可以把应用到 100.64.0.1 的连接经代理送入 Tailnet；应用应分别探测每条目标端点的可达性和认证结果。不得据本机未安装 Tailscale 或未发现网卡就宣布两个设备不能直连。这里的“设备直连”只表示应用直接访问另一设备的服务；是否底层 UDP 直连或 DERP 中继，须另看实际 Tailscale 状态，不能从 HTTP 成功推断。

默认顺序应优先可用的设备路径和本机中转；网盘是用户可选择的后台策略。中转 URL 是命名空间的一部分。不能只把发送端 URL 改成本机：收件端必须在相同服务注册收件箱并监听，公屏成员必须同步读取相同服务。不同服务的 revision 不可比较；跨服务重试保留 itemId，收件端去重，已上传的内容保持所在服务直至取得真实回执。不能将“某服务上传成功”冒充“收件设备已接收”。

## 本机服务实现

现场只读核实：本机 autodroid-gateway 的 Tailnet 地址为 100.64.0.1；没有现有 mpt-relay.service。本次增加独立 `mpt-tail-relay`，复用 `tools/file-transfer/relay/mpt_relay` 协议实现，不复制业务服务。

- 客户端入口：`http://mpt-relay.tail.lixinrui000.cn/mpt/relay/`。独立 nginx 虚拟主机复用现有 TCP/80 授权，后端 `http://100.64.0.1:18765/mpt/relay/` 只监听指定 Tailnet 接口。域名也匹配用户 Clash 的 `tail.lixinrui000.cn` 路由规则。
- 独立 systemd 用户、`/opt/mpt-tail-relay/releases/` 代码与 `/var/lib/mpt-tail-relay` 数据；不读写公网中转数据。
- 保留会话隔离、inbox owner/deposit 权限、原子文件提交、持久回执、配额和日志脱敏。仅信任本机 nginx 的代理 IP 头，nginx 以实际客户端地址覆盖头部；其他 Tailnet 节点伪造头部无效，避免所有客户端错误共用 nginx 的注册限额。
- systemd 故障重启，接口晚出现时继续重试；升级保留旧版本，启动/health 失败回退原代码。单机故障恢复不等于多机高可用；主机/磁盘故障时应由客户端保留队列并选择其他路径。
- 仅新增独立 nginx `mpt-tail-relay.conf`；未改 DNS、Headscale、全局路由、防火墙或其他站点。
- HTTP 沿用现有 Tailnet 地址传输约束。Tailnet 加密覆盖 Tailnet 跳；Clash 客户端至代理的安全性取决于该代理配置，不能声称是端到端 TLS。若该跳需要额外保护，应给此服务配置客户端可验证的 HTTPS，而不是关闭证书校验。

预览：

```sh
python3 tools/file-transfer/relay/deploy/install-tailnet.py --address 100.64.0.1
```

部署与协议验收：

```sh
sudo python3 tools/file-transfer/relay/deploy/install-tailnet.py --address 100.64.0.1 --apply --enable
python3 tools/file-transfer/relay/deploy/smoke.py --base http://100.64.0.1:18765
```

安装器要求目标地址属于实时 `tailscale status --json` 的本机地址。已有配置保持原样；不同数据根/端点要求先人工核对，避免错误升级到另一队列。回退可把 `/opt/mpt-tail-relay/current` 指回保留的上个 release，再重启此独立 unit；用户文件不随代码回退。下线只需停止并禁用该 unit，保留代码/配置/数据。

## 内嵌 OpenList

当前 `OpenListRuntime` 是桌面按需下载并管理 OpenList 进程，不是所有安装包离线自带；它还会把 Android 误归为 Linux。因此不能称现状已经满足每端内嵌。

官方移动实现提供真实 Go/JNI AAR 路径：[OpenList-Mobile 构建流程](https://github.com/OpenListTeam/OpenList-Mobile/blob/main/.github/workflows/build_openlist.yaml)、[gomobile 脚本](https://github.com/OpenListTeam/OpenList-Mobile/blob/main/openlist-lib/scripts/gobind.sh)、[Android 调用接口](https://github.com/OpenListTeam/OpenList-Mobile/blob/main/android/app/src/main/kotlin/com/openlist/mobile/model/openlist/OpenList.kt)。其接口有 `setConfigData`、`init`、`start`、`isRunning`、`shutdown`，不是在手机展示远程网页来模拟内嵌。

实现要求：

1. 固定 OpenList 后端、移动绑定和前端版本；打包桌面二进制及 Android AAR 的 arm64/x64 原生库，不让用户再装另一个 App。构建保留依赖许可证和相应源码信息（官方移动项目 AGPL-3.0）。
2. Android 用 .NET Android 绑定项目接入 AAR；数据放 App 私有目录，监听回环端口，由现有平台生命周期管理。桌面由现有进程管理接入安装包所带资源。两端统一通过本机 API/WebDAV 使用网盘。
3. 挂载、授权、选择目录由 MPT 内流程承载。首次使用某网盘仍须该网盘授权；内嵌不能绕过服务商账号或省去向网盘上传本身。
4. 仅使用网盘路径时启动内嵌服务，持续传输按 Android 前台任务规则存活；空闲不为了状态展示轮询。关闭网盘能力不影响私聊和 Tailnet 中转。
5. 管理员密码不写日志；传输账号仅授予用户选定目录权限；既有自定义网盘配置不自动迁移/覆盖。

## 完成标准

服务 health 或 Python 协议测试只证明服务边界。客户端接入后还需 Windows Dev 与手机 UI 真正双向发送，接收端离线后上线自动领取，并在发送中/中转重启/应用重启后确认同一 itemId 最终只出现一次。经 Clash 访问 Tailnet 的实机路径、设备直连候选探测、网盘真实上传下载、Android AAR 生命周期都要单独留证。目前这些新增路径尚不能宣称端到端完成。

## 现场验收与直连诊断（2026-09-29）

- `mpt-tail-relay.service` 已部署、启动并启用，监听 `100.64.0.1:18765`；`/etc/nginx/sites-enabled/mpt-tail-relay.conf` 已通过 `nginx -t` 并 reload。新域名被导航配置发现。
- 本机真实协议验收：注册、认证文件往返、namespace 隔离、长轮询全部通过；真实 `systemctl restart` 后原身份认证与文件内容仍然有效。
- Windows `LIS-IMAC` 实查 OS 为 Microsoft Windows 10.0.26200。新域名解析到 `100.64.0.1`，显式 Clash 代理和 TUN 两种模式都得到 health HTTP 200/JSON。Windows 原生 Python 经该域名完成注册、认证文件往返、namespace 隔离和长轮询，五项均通过（隔离测试身份 `smoke-80803d96`）。手机真实路径尚未执行。
- 实时 Headscale：`100.64.0.3`（Windows）、`.4`（Android）、`.6`（macOS）均在线，名称均来自 `android-mihomo` 系列；节点 2–7 的 `tags` 均为 `tag:mobile`。不能根据名字把 LIS-IMAC 当作 macOS。
- 当前 policy 的 `tag:mobile → 100.64.0.1` 仅允许指定端口（含 TCP/80），没有新中转 18765；也没有 `tag:mobile → tag:mobile` 的 TCP/47165 授权。新域名复用 80，未扩 ACL。这个结论限于这些 tag 的策略，不泛化到所有节点。
- Windows 已保存的两个 peer（`android`、`phone-35363a66`）其 `address` 都为空，因此目前也没有可尝试的保存直连候选。Mihomo userspace Tailnet 身份不能只靠应用网卡枚举发现。
- 后续客户端必须接入域名候选；当前 `PublicRelayClient` 仍固定公网，且 `OpenListClient.ValidateUrl` 的 HTTP 规则只接受 Tailnet 字面 IP/回环，域名支持需要明确受信入口策略，不能全局放开 HTTP。服务上线不代表 App 已经切换。

本次相关测试 44/44：部署边界 4、真实进程生命周期 5、inbox 权限/持久/并发 35。日志：`artifacts/.tmp-android-verify/public-relay-server/relay-tests-20260929T035435Z.log`。

域名代理限流补验：nginx覆盖X-Real-IP/X-Forwarded-For，服务仅信任本机100.64.0.1，相关API及配置测试21/21通过（与上面44项有4项重叠）。最终Windows协议五项再次通过，证据 `/mnt/cache/data-cache/mpt-tail-relay-windows-smoke.txt`；原始测试日志 `artifacts/.tmp-android-verify/public-relay-server/relay-tests-20260929T040029Z.log`。

## Mihomo 配置对客户端的影响

用户提供的是 Mihomo `type: tailscale` 出站，而不是普通 SOCKS 出站；认证密钥不记录在本文。该出站创建自己的 tsnet 节点。它有 Tailnet 身份，不代表 Android 应用能通过系统网卡枚举得到该地址，也不代表应用监听端口自动映射进该节点。

已知分流规则为 `DOMAIN-SUFFIX,tail.lixinrui000.cn,HEADSCALE`，且此域后缀不使用 Fake-IP。因此内置中转使用上述域名；不能根据这条域名规则推断直接访问 `100.x` 地址也会匹配 HEADSCALE。Mihomo 在首次匹配流量时初始化此出站，单次冷启动超时只能记录为本次不可达，不能持久标记整个设备不支持 Tailnet。

来源：[Mihomo Tailscale 配置](https://wiki.metacubex.one/config/proxies/tailscale/)、[Mihomo tsnet 出站实现](https://github.com/MetaCubeX/mihomo/blob/Meta/adapter/outbound/tailscale.go)。具体软件版本和入站转发配置仍应以设备实际状态为准。
