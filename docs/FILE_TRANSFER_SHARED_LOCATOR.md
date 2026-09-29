# 共享会话 Tailnet 大文件与公网小信令协议

状态：待实现方案，2026-09-29。仅用于持有同一共享会话 key 的“文件传输助手”成员。普通通讯录私聊继续走独立 inbox owner/deposit 协议，不发布到此共享命名空间。

## 核心约束

公网负责让所有成员发现同一条消息、发布请求和汇总真实回执；大文件默认只存在本机 Tailnet relay。某个成员回执只能说明这个成员已有内容，不能结束其他成员的可用性任务或删除唯一副本。

这里同时存在两个独立状态：

- 消息状态：排队、上传、可供领取、已有成员接收。
- 内容可用性：哪些已提交的副本仍可下载、是否有人请求公网副本、该请求是否完成。

发送端即使已收到一个回执，仍监听该消息的后续请求。进程重启恢复这些任务。新加入的成员也必须能通过已有消息发现路径领取。没有完整成员名册时不能把“所有已知设备已收到”当作允许回收的依据。

## 最小存储布局：先复用现有 DAV，无服务 API 改动

所有路径都在认证后的 DAV namespace 内。此处 C 是 conversationId，M 是同一 messageId，D 是请求设备 deviceId；都沿用现有字段校验。

| 服务 | 相对 DAV 路径 | 含义 |
| --- | --- | --- |
| 公网 | `assistant-locator/C/M/manifest.json` | V2 小型发现记录：内含原 V1 message，引用 Tailnet 内容 |
| 公网 | `assistant-locator/C/M/requests/D.json` | D 请求可公开下载的副本，幂等写入 |
| 公网 | `assistant/C/M/receipts/D.json` | D 真正保存内容后的现有回执格式 |
| Tail relay | `assistant/C/M/payload` | 现有附件内容 |
| Tail relay | `assistant/C/M/manifest.json` | 原 V1 manifest，payload 原子完成后才发布 |
| 公网（按需） | `assistant/C/M/payload` | fallback 完整内容 |
| 公网（按需） | `assistant/C/M/manifest.json` | 原 V1 manifest，公网内容完成后才发布 |

现有服务对非 `payload` 文件提交自动增加 namespace revision；这些小信令可复用现有 `/changes` 长轮询，**不需要为 locator 或 request 新增忙轮询**。Tail、公网的 revision 分开持久化，不能共用一个游标。

Locator v1 数据示例（`message` 必须通过当前 V1 manifest 校验）：

```json
{
  "version": 1,
  "message": {
    "version": 1,
    "id": "0123456789abcdef0123456789abcdef",
    "kind": "file",
    "name": "example.zip",
    "size": 104857600,
    "createdAt": "2026-09-29T04:00:00Z",
    "senderDeviceId": "pc-example",
    "senderName": "我的电脑"
  },
  "payloadRoute": "mpt-tail-relay-v1"
}
```

`payloadRoute` 是客户端内注册的受信路由标识，**不是任意 URL**。客户端从受信配置得到 `http://mpt-relay.tail.lixinrui000.cn`，拼接由 C、M 推导的现有路径。不要接受 locator 任意 host/path，也不要把共享 Basic key 转发给 locator 提供的任意站点。公屏 manifest 的 TargetDeviceId 必须为空；私聊条目不能进入这条分支。

请求示例：

```json
{"version":1,"itemId":"0123456789abcdef0123456789abcdef","deviceId":"phone-example","requestedAt":"2026-09-29T04:00:03Z","reason":"tail-unreachable"}
```

文件名 D 与记录 deviceId、目录 M 与 itemId 必须一致。不包含密码、pair token、inbox ownerKey/depositKey、本机文件路径或用户网络配置。单个 locator/request 设小尺寸上限（建议 16 KiB / 2 KiB），requests 列举设数量上限（沿用64设备规模），非法记录跳过并留可诊断错误，不阻塞其他消息。

## 发送流程

1. 本地持久化消息与 outbox；分配一次 M，此后任何重试/跨路径使用相同 M。
2. 注册 Tail relay 的同一 C/key，上传 payload，最后发布 Tail V1 manifest。确认提交后，发布公网 locator；公网小记录失败时保留任务重试，不能显示所有成员可见。
3. UI 表示“已发送”或“可供领取”；传输详情区分上传完成与设备回执。不要因 Tail 上传成功就伪造所有设备已接收。
4. 监听公网 requests 与 receipts。某 D 请求 fallback 时持久化一个按 `(C,M)` 合并的公网复制任务；其他 D 请求复用它。
5. 复制优先使用仍存在的本机副本，否则从已提交 Tail 副本下载到受限临时文件后上传公网；不要求用户重新选择文件。公网 payload 完成后才发布原 V1 manifest，这是 fallback 完成的提交点。
6. 对已经完成的公网 V1 manifest先校验 `SameMessage`，相同即视为该任务已完成，不重复上传。公网 manifest存在而字段冲突则拒绝覆盖并报告。并发上传不是跨服务事务；同一消息重试必须使用同一不可变内容副本。
7. 不因第一个回执删除 outbox内容来源/停止后续请求处理。清理属于明确保留策略，不能混在 Delivered 状态转换里。

## 接收流程

1. 同步公网 V1 目录与新 locator 目录，按 `(C,M)` 合并到同一条消息；同M不同消息字段拒绝合并，保留诊断。
2. 优先检查本地是否已保存，再检查公网完整 manifest（若已fallback无需再试Tail），否则在短连接预算内尝试 locator 指定的受信 Tail 路由。
3. Tail连接失败、认证错误、payload缺失或长度与manifest不符时，不记已接收。写一次requests/D.json，然后显示“等待中转”并等待公网changes。认证错误应保留具体诊断，不能无限快速尝试。
4. 公网 V1 manifest出现后领取；写本地原子保存完成，再发布现有 receipt/D.json。若已有同M本地内容，只补缺失回执，消息/文件均不重复。
5. D已收不影响E继续请求。新成员发现旧locator仍执行同样流程。

## 旧版兼容必须显式处理

旧客户端只认识 `assistant/C/.../manifest.json` 的 Version=1，且假定同服务已有payload。不能在原目录先发布无payload的V1记录，也不能写Version=2期待旧端自动回退。

新协议使用独立 `assistant-locator` 根，旧端完全忽略。需要兼容旧端的会话在 Tail 发布后仍安排延迟公网完整fallback；不能等旧端发request，因为旧端不会发。该复制任务即使收到新版Tail回执也不能取消。

最小上线策略：既有会话默认保留旧版兼容fallback，新创建且明确启用新协议的共享会话才使用纯按需模式。当前项目没有可靠完整成员能力名册，因此**不能自动宣称所有成员均支持新协议并关闭兼容fallback**。第一版可把协议模式写入共享邀请的新版本字段，由接受方支持判断；旧版本邀请仍走兼容路径。不要仅根据最近在线设备推断全部成员能力。

此策略意味着旧会话的省公网流量效果有限，但不会默默让旧手机收不到文件；新协议会话才能在所有接收者均可达Tail时只传小信令。若产品不接受模式差异，应先完成成员能力协商再启用优化，不能省略兼容问题。

## 离线与可靠性边界

MVP把fallback任务放客户端持久outbox，发送端暂时离线时请求保留，恢复后继续。已经拿到文件的新版成员也可响应request，但需要单一复制者协调后才能避免重复大上传；第一版可只允许sender响应，以缩小实现范围。

“发送端永久离线且只有Tail保有文件、请求设备又不能访问Tail”这个组合，纯客户端MVP无法立即完成公网fallback。不能隐藏这条边界。若要求此时仍可领取，下一阶段增加本机relay的持久复制worker：客户端显式委托该消息的复制权限，worker在后台监听公网request并复制。现有relay仅保存key摘要，不能凭摘要向公网认证；不要把明文共享key塞进locator或日志。优先设计消息级、目标固定的复制授权，再实现受保护的服务端任务存储；这部分需要独立安全评审与凭据生命周期设计，不能伪称现有DAV已经提供了它。

若要立即提供发送端关机也必达的严格保证，MVP必须在兼容/保障策略下提前提交公网完整副本；“只在Tail存大文件”与“任何公网-only成员随时领取且所有桥接者离线”无法同时由当前架构保证。

## 可并发切分

1. **协议/客户端适配**：新增独立 `SharedLocatorClient`、记录校验、受信routeId解析和DAV目录方法；不改现有 V1 publisher 语义。用真实两个relay进程验证。
2. **同步/outbox**：按C/M持久化route状态、public copy任务与request处理；保留已Delivered消息的可用性任务；两个revision游标和按M去重。此分支独占 AssistantSync/Store 相关状态文件。
3. **UI**：只消费消息/可用性两类状态，显示等待中转/接收进度；不将backend路由变成用户发消息前的步骤。
4. **验证**：三个真实客户端角色A发送、B有Tail、C无Tail，用两台独立relay；先协议再Windows/Android UI。服务端第一阶段可不改。
5. **后续独立任务**：旧版本能力协商、持久relay复制worker；不要与第一阶段同时抢改协议核心。

## 必须通过的测试

- A上传Tail后B收到并写回执；C随后请求公网，A仍完成fallback，C自动领取，同M只一条消息。
- A已看到B回执后重启，再处理C已持久请求；不能丢任务。
- C在A离线时请求，保持等待；A恢复自动复制，无额外点击。明确记录等待边界。
- B和C并发请求只生成一个sender本地复制任务；复制重试不新建M。
- Tail提交成功但公网locator失败；恢复后只补小记录，不重复大上传。
- fallback上传中断不发布V1manifest；重启后完成再唤醒接收端。
- 双路径同时发现同M只落一条；旧V1manifest与locator字段冲突被拒。
- 在B已回执后，D新加入会话仍能请求/领取，不被“Delivered”短路。
- 普通pair私聊不生成locator/request，也不能用deposit凭据读公屏DAV。
- 恶意routeId/任意URL/跨C路径/超限requests不泄露key，不触发任意主机请求。
- 旧版客户端在兼容fallback后仍收到原V1内容；纯新协议模式不会错误承诺旧端可见。
- 字节统计分别量payload与信令：全Tail新版会话的公网大文件字节为0；mixed接收者触发后才计公网payload，不能只测health就宣称省流量。
