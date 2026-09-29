# 共享会话：Tailnet 文件存储与公网按需转发

状态：实现和验收中。私聊继续使用独立 inbox 协议；本协议只用于同一共享会话的授权成员。

## 用户流程

用户在文件传输助手中添加文件并发送。客户端自动选择路径，不要求用户选择网络、升级邀请或判断对方是否加入 Tailnet。每条消息只有一个 ID；换路、重试和重启不生成重复消息。上传完成表示可领取，只有接收端保存成功后的回执才表示已接收。

## 路径选择

默认公网服务的 health 必须实际返回 `capabilities.sharedPayloadLocator = 1`，客户端才启用此优化。服务端功能默认关闭，由 `MPT_RELAY_TAIL_PAYLOAD_PROXY=1` 启用；本机 Tail relay 保持关闭，避免递归代理。

服务不支持此能力，或 Tail 存储不可用时，继续使用现有完整公网上传。用户配置的自定义存储保持原有行为，不能静默改用默认公网服务。

| 数据 | 位置 | 提交顺序 |
| --- | --- | --- |
| 文件内容 | Tail `assistant/C/M/payload` | 1 |
| 原 V1 manifest | Tail `assistant/C/M/manifest.json` | 2 |
| locator | 公网 `assistant-locator/C/M/manifest.json` | 3 |
| 原 V1 manifest | 公网 `assistant/C/M/manifest.json` | 4 |

C 是共享会话标识，M 是消息 ID。locator 结构固定为：

```json
{
  "version": 1,
  "message": {
    "version": 1,
    "id": "0123456789abcdef0123456789abcdef",
    "kind": "file",
    "name": "example.zip",
    "size": 1048576,
    "createdAt": "2026-09-29T04:00:00Z",
    "senderDeviceId": "pc-example",
    "senderName": "我的电脑"
  },
  "payloadRoute": "mpt-tail-relay-v1"
}
```

message 沿用并验证现有 V1 manifest；图片保留原有 image 类型。公网 locator 失败时，只重试小记录，不重复上传已提交的 Tail 文件。

## 新旧客户端兼容

新版接收端优先从 Tail 获取文件。旧版仍发现原 V1 manifest，并请求原公网 DAV payload 路径。公网服务先查本地完整文件；没有本地文件时，验证 locator 后从固定 Tail endpoint 流式转发。因此发送端关机后，只要公网服务仍能访问 Tail 副本，旧版和公网接收端仍能领取。

公网代理不保存完整副本、不将整个文件读入内存。公网接收者下载时会产生对应的公网流量；全部接收者直接读取 Tail 文件时，公网只有元数据和回执。这里统计的是应用层公网 relay 的文件流量；Tailscale 底层可能采用直连或 DERP，需另行测量物理出口流量，不能从公网 payload PUT/GET 为零推导全链路零公网流量。

## 故障恢复

消息状态与文件副本状态分开持久化。第一个成员的回执不能结束其他成员的可用性任务。

Tail 暂时不可达时，新客户端写入 `assistant-locator/C/M/requests/D.json`。公网代理也需要为不能写新协议请求的旧客户端创建同类请求，使用保留来源 `relay-public`，并触发 namespace changes。请求包含 version、itemId、deviceId、requestedAt、reason；它不是接收回执。

发送端监听现有 changes 长轮询，将同一消息的请求合并成一个持久复制任务。完整公网 payload 提交后写 `public-copy.json` 完成标记，避免重启后重复复制。原公网 manifest 此时早已可能用于代理下载，不能把其存在当作公网本地副本完整的证明。

发送端也离线且 Tail 不可达时，接收端保留等待状态；有副本的一方恢复后继续。不能把不可领取显示成已送达，也不能承诺所有存储端都离线时仍能取到内容。

## 安全边界

- 先执行现有 namespace 认证，再查 locator 或请求上游。
- locator 最大 16 KiB，必须匹配当前会话、消息 ID、共享 file 类型及长度；私聊 targetDeviceId 不得进入此路径。
- route 只接受 `mpt-tail-relay-v1`，服务端固定映射到 `http://mpt-relay.tail.lixinrui000.cn`；拒绝 locator 任意 URL、路径及跳转。
- 仅将当前已验证的 Basic 凭据转发给固定上游，不落盘、不写日志、不转发任意客户端头。
- GET 按块转发，HEAD 不发送内容；上游长度错误、提前断开和客户端断开都关闭流，不能伪造完整成功。
- 保留现有配额、认证、原子提交和真实回执机制，不新增内容哈希协议。

## 验收要求

1. 真实两台 relay：发送端只上传 Tail 文件，新端直接领取；旧端从公网原路径领取，公网无本地文件副本。
2. 发送端关闭后，公网代理仍能从 Tail 完成下载；HEAD、断流和大文件有独立检查。
3. 旧服务未声明能力时走原完整公网上传；自定义存储不被替换。
4. Tail 失败后请求持久化，发送端恢复执行公网复制，同一消息不重复、完成标记不冒充回执。
5. 跨 namespace、私聊 locator、错误 route、跳转和超限记录不得触发凭据外发。
6. Windows 与 Android 使用实际发送按钮验收；分别记录 Tail 成功和公网回退，不能以 health 或协议测试替代界面收发。

实现进度、测试结果和发布状态需另附证据，本文件本身不是验收通过声明。
