# MyPowerTools 公网文件助手中转（`tools/file-transfer/relay/`）

`proxy.lixinrui000.cn` 上的默认公网收发与离线队列。没有 Tailscale、没有账号表单、没有网盘
授权也能收发：会话身份（`conversationId` + 64 hex `conversationKey`）本身就是凭据，
服务只按认证后的 namespace 隔离数据。

- **实现**：Python 3.10+ **仅标准库**（`http.server` / `sqlite3` / `hashlib` / `ssl` 由 nginx 负责），
  远端 Ubuntu 有 `python3` 就够，不需要 dotnet。
- **监听**：`127.0.0.1:18765`（回环，只由 nginx 暴露）。
- **对外前缀**：`/mpt/relay/`，nginx 原样转发，服务自己识别前缀。
- **契约**：见 [docs/PROTOCOL.md](docs/PROTOCOL.md)（端点、状态码、revision/长轮询语义、配额、
  以及第 6 节的设备配对收件箱 `inboxId`/`ownerKey`/`depositKey` 投递协议）。
- **客户端**：`src/FileTransfer.Core/PublicRelayClient.cs`（M4 维护，本目录不改动客户端、Surface、
  Module 或全局文档）。

## 目录

```
mpt_relay/            服务源码（无第三方依赖）
  config.py           环境变量 / EnvironmentFile / 命令行配置与校验
  store.py            SQLite 凭据摘要、revision、配额计量、会话私有根目录
  auth.py             Basic 解析与 conversationId/key 字段规则
  dav.py              WebDAV：MKCOL/PROPFIND/PUT/GET/HEAD/DELETE/OPTIONS，原子 PUT、路径校验
  inbox.py            设备配对收件箱：owner/deposit 双凭据、投递、待处理列表、真实回执
  fsutil.py           原子写（临时文件 + fsync + rename）等共享文件系统助手
  app.py              路由：health、注册、长轮询、DAV 认证与 namespace 隔离
  httpd.py            HTTP/1.1 传输：流式请求体、chunked、长轮询断线检测、连接上限
  logutil.py          日志脱敏（永不记录 Authorization / 正文 / 完整会话 id）
  service.py, __main__.py
tests/                真实 HTTP 集成测试（unittest，无第三方依赖）
  relay_testkit.py    起真实服务 + 真实 socket 的测试夹具
  openlist_client_semantics.py   OpenListClient/OpenListAssistantClient 线级行为的 Python 镜像
  test_relay_api.py        健康检查、注册、鉴权、限速、日志不泄漏凭据
  test_relay_dav.py        布局、方法语义、双会话隔离、路径穿越、原子 PUT/取消、配额、重启对账
  test_relay_longpoll.py   changes 语义、payload 静默、唤醒、跨会话隔离、重启保持 revision
  test_openlist_client_compat.py   现有客户端调用序列的端到端兼容
  test_relay_inbox.py      配对收件箱：deposit/owner 权限边界、幂等投递、回执篡改、未就绪重试、重启
  test_relay_process.py    真实子进程：CLI/ENV、SIGTERM、重启、deploy/smoke.py 自测
  run.py              运行入口，日志写到 artifacts/.tmp-android-verify/public-relay-server/
deploy/               部署模板（本目录不执行远端部署）
  mpt-relay.service   systemd 单元：mpt-relay 非 root 账户，数据 /var/lib/mpt-relay
  nginx-mpt-relay.conf  只新增 /mpt/relay/ 的 location 片段（不动现有 Headscale 配置）
  relay.env.example   /etc/mpt-relay/relay.env 示例（含全部默认值）
  install.sh          幂等安装/升级脚本（`--enable` 才启动）
  smoke.py            部署后验收：对公网 URL 跑 health/注册/DAV/隔离/长轮询
docs/PROTOCOL.md      固定协议（给 M4、GPT UI、主代理）
```

## 本地运行

```bash
cd tools/file-transfer/relay
python3 -m mpt_relay --print-config                 # 查看生效配置
python3 -m mpt_relay --data-dir /tmp/mpt-relay-demo # 默认 127.0.0.1:18765
curl -fsS http://127.0.0.1:18765/mpt/relay/health
```

## 跑测试

```bash
python3 tools/file-transfer/relay/tests/run.py                      # 全部
python3 tools/file-transfer/relay/tests/run.py test_relay_dav        # 只跑某个文件
python3 tools/file-transfer/relay/tests/run.py QuotaTests            # 只跑某个类
```

日志落在 `artifacts/.tmp-android-verify/public-relay-server/relay-tests-*.log`
（`scripts/artifacts-policy.json` 已把 `.tmp-*` 声明为 scratch，无需新增条目）。

测试覆盖（均为真实 HTTP/1.1 + 真实 socket，另加真实子进程重启）：当前 **162** 个用例
（`tests/run.py` 的实际 runner 计数，2026-09-28）：

| 文件 | 用例数 | 覆盖 |
| --- | --- | --- |
| `test_relay_inbox.py` | 35 | 配对收件箱：deposit 不能冒充 owner/不能改 owner 身份、deposit 不能列表/读 payload/写回执/删条目、跨 inbox 与跨凭据拒绝、itemId 随机性作为回执能力、篡改回执（bytes 越界/时间非法/itemId 不符/设备字段非法）被拒、重复投递幂等且不重复唤醒、**同 itemId 并发投递只创建一次/只计费一次/revision 只加一次、不同元信息并发返回 409 且不覆盖**、DELETE 与回执并发不留孤儿回执、owner 长轮询与真实回执闭环、未注册时 503 可重试后成功、配额/取消/重启/凭据摘要/日志脱敏 |
| `test_relay_dav.py` | 23 | 布局与方法语义（201/204/404/405/409/415/507）、双会话隔离、路径穿越、取消上传无可见残留、chunked、3 MiB 往返、会话/全局/单文件配额、重启对账 |
| `test_openlist_client_compat.py` | 22 | 现有客户端调用序列的**线级镜像**端到端（MKCOL 405 容忍、href 前缀解析、增量列举、发布顺序与幂等、回执按设备、非法 manifest 不毒化时间线、旧版 `android/<guid>` 布局） |
| `test_relay_api.py` | 17 | 健康检查、注册幂等/错 key/格式非法/chunked 非空 body 400、`dav_auto_register` 出厂默认 0、限速与 X-Real-IP 分桶、日志不泄漏凭据、出厂默认值断言 |
| `test_relay_chunked.py` | 17 | chunked 分段读取：巨型 chunk 声明下每次 read 请求 ≤64 KiB（reader spy，不分配 GB）、负数/非十六进制/超长/缺 CRLF 长度行、payload 尾 CRLF、trailers 有界、EOF 截断必须失败、合法 chunked 仍可用、413 由配额而非声明长度决定 |
| `test_relay_hardening.py` | 16 | **持续慢滴**（后台每 0.2s 1 字节，≥5s）必须在 1s 墙钟预算附近被切断（<2s），长度行/trailers 内的慢滴同样被切断，longpoll 不受预算影响；全局配额预留（并发跨会话只有一个通过、取消即释放、chunked 增量预留）、MKCOL 满额 507 而非 500、限速器键数硬上限与最旧淘汰 |
| `test_deploy_scripts.py` | 15 | 部署模板静态守卫：`install.sh` 语法 + 不含删除命令、升级用 rollback、`--enable` 校验 active 与 health 且能失败、unit/nginx/env 模板关键行与默认值一致 |
| `test_relay_longpoll.py` | 12 | changes 语义、payload 静默、manifest/receipt 唤醒、跨会话 revision 隔离、断线即结束、重启保持 revision+凭据+内容、DB 内无明文 key |
| `test_relay_process.py` | 5 | 真实子进程：CLI/ENV 配置、**真实 SIGTERM 后重启**、`deploy/smoke.py` 自测 |

`.NET` 侧的同一结论（真实 `PublicRelayClient`/`OpenListClient` 打同一服务）**不在本目录的测试范围内**：
`tests/openlist_client_semantics.py` 是那套 C# 调用的 Python 镜像，只能证明线级契约一致，
不能替代真实客户端运行；真实客户端验收由主代理/M4 执行。

> 计数口径：以 `python3 tests/run.py` 末尾的 `ran=N` 为准。此前报告出现的 75 是把 verbose 输出按
> 模块名 grep 得到的（漏掉了同模块内换行打印 docstring 的行），78 是当时 runner 的真实 `ran`；
> 两者都不是可用分母，现按 runner 计数逐文件列出。

## 部署（由主代理执行，本目录不自行部署）

1. 安装/升级代码与单元（幂等，数据目录不动）：

   ```bash
   sudo sh deploy/install.sh            # 建 mpt-relay 用户、/opt/mpt-relay、/var/lib/mpt-relay
   sudo sh deploy/install.sh --enable   # 再 enable + restart 并打印状态
   ```

   出厂 `MPT_RELAY_DAV_AUTO_REGISTER=0`：未注册的 DAV 请求不能凭空建 namespace，客户端必须先
   `POST /v1/conversations`（`PublicRelayClient.RegisterAsync` 已经这么做）。需要兼容“只配地址和
   账号密码”的纯 WebDAV 客户端时再设回 1。

   若不用脚本：`python3 -m compileall mpt_relay` → 拷到 `/opt/mpt-relay` →
   `install -m 0644 deploy/mpt-relay.service /etc/systemd/system/` →
   `install -m 0640 deploy/relay.env.example /etc/mpt-relay/relay.env` →
   `useradd --system --home /opt/mpt-relay --shell /usr/sbin/nologin mpt-relay`。

2. nginx：把 `deploy/nginx-mpt-relay.conf` 存成 `/etc/nginx/snippets/mpt-relay.conf`，
   在**已有的** `server { server_name proxy.lixinrui000.cn; … }` 里加一行
   `include /etc/nginx/snippets/mpt-relay.conf;`，然后 `nginx -t && systemctl reload nginx`。
   **不要重写、重排或替换现有 server 块**：同一台机器还承载 Headscale/Tailnet 端点。
   片段里已经包含：`proxy_request_buffering off`（流式 PUT）、`proxy_read_timeout 70s`
   （长轮询 >25s）、`proxy_buffering off`、`access_log off`（URL 里含会话 id）。

3. 验收（公网，真实 TLS+nginx+服务）：

   ```bash
   python3 deploy/smoke.py --base https://proxy.lixinrui000.cn
   ```

   它用一次性 `smoke-xxxxxxxx` namespace 跑 health → 注册 → DAV 往返 → 隔离 → 长轮询。

4. 运维：

   ```bash
   systemctl status mpt-relay        # journalctl -u mpt-relay -f
   curl -fsS http://127.0.0.1:18765/mpt/relay/health
   ```

## 安全与边界

- 凭据只存 PBKDF2-HMAC-SHA256 摘要（每会话随机 salt，默认 120k 次迭代），**从不存明文 key**；
  日志从不记录 `Authorization`、请求体；路径里的会话 id 默认脱敏（`self…3c4d`）。
- 认证失败按 IP 计数（默认 60 次/5 分钟）→ 429；PBKDF2 并发有闸门（默认 8），防止公网
  密码预言机烧 CPU；服务同时限制连接数（64）与请求体。
- 建 namespace 按 IP（5/小时）与全服（100/小时）限速，并有 `max_conversations` 上限。
- 越权、穿越、跨 namespace 探测一律拒绝且不触发磁盘操作；每个会话只能看到自己的树。
- `ReadWritePaths=/var/lib/mpt-relay`，`ProtectSystem=strict`、`NoNewPrivileges`、
  `PrivateTmp` 等 systemd 加固已写在单元里。

## 已知限制 / 待办（不是本服务的“已完成”声明）

1. **未部署**：本目录只提供源码、测试与模板；`proxy.lixinrui000.cn` 上的 systemd、nginx、
   TLS 与防火墙由主代理执行，公网 `smoke.py` 需要在部署后由主代理跑一次并回填结果。
2. **未做真实设备验收**：Windows Dev 与 Android 经公网收发、离线后自动领取属于 R2/R4 的
   端到端验收，必须由主代理在真实环境完成；本目录的 loopback 测试只证明协议与恢复逻辑。
3. **客户端文案**：`507/413/429` 目前会显示成 OpenList 网盘提示（见 PROTOCOL.md 第 5 节），
   需要 M4 在客户端侧改文案，本目录不改客户端。
4. **单机单进程**：revision 唤醒是进程内 `threading.Condition`，SQLite 也是单文件。若将来要多
   实例或迁移，需要换成共享存储 + 外部唤醒（当前部署形态不需要）。
   `install.sh` 升级时会先把旧代码树移动到 `/opt/mpt-relay/rollback/`（不删除），多次升级会累积，
   由运维按需自行清理。
5. **收件箱并发语义**：同一 namespace 的投递/回执/删除在同一把锁内串行（存在性、幂等/冲突判定、
   配额预留与写入是一笔事务）；同一 inbox 的并发投递会串行化，客户端多设备同时往一个 inbox 投递
   不同 itemId 时仍是逐个进行，这是有意的（配额计量精确优先）。
6. **收件箱与配对的边界**：收件箱只解决“把文件投给一台设备而不交出会话身份”，它不做设备身份、
   信任决策或配对码的展示逻辑（那些在客户端本地，不联网）；发送端在收件端注册前收到 503
   `inbox_not_ready`，必须自行退避重试。已消费的条目由 owner 用 `DELETE` 释放空间，服务不会自动删除。
7. **共享 IP 的限速副作用**：认证失败窗口按 IP 失败即封闭（fail-closed），CGNAT 下同 IP 的
   正常客户端会一起等窗口过期（默认 5 分钟，可用 `MPT_RELAY_AUTH_FAILURES_PER_IP=0` 放宽）。
8. **没有账号体系**：namespace 首次注册即绑定 key，服务不提供 key 轮换/找回（因此客户端必须
   在首次发送时立刻注册；设备身份与二维码生成仍然完全本地，不依赖本服务）。
9. **日志轮转**：服务写 journald（`journalctl -u mpt-relay`），由系统 journal 策略轮转；
   nginx 片段对该前缀关闭 access_log。若需要集中审计，按片段内注释启用“无凭据格式”。
