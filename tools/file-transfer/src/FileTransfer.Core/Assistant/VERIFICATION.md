# F3 助手内核：验证记录

范围：`src/FileTransfer.Core/Assistant/`（记录、Store、OpenListClient partial、同步编排）与
`tests/FileTransfer.Core.Tests/Assistant/`（真实 HTTP WebDAV 测试服务 + 定向行为测试）。
不含模块命令、Surface、凭据与设备发现（M4/M3/F2）。

## 怎么跑

**首选（不占共享锁、不写共享输出）**：真实工程 + 全局 `ArtifactsPath` 私有重定向。
所有项目（测试工程、`FileTransfer.MyPowerTools` 模块、以及原本写 `artifacts/build` 的 `src/` 工程）
的 `bin/obj` 全部落到 `artifacts/.tmp-android-verify/f3-private-build/`，
用 `-getProperty` 先验证过三种工程的落点：

```bash
~/.dotnet/dotnet msbuild tools/file-transfer/tests/FileTransfer.Core.Tests/FileTransfer.Core.Tests.csproj \
  -p:ArtifactsPath=<private> -getProperty:ArtifactsPath,UseArtifactsOutput,BaseOutputPath,BaseIntermediateOutputPath
# → UseArtifactsOutput=true, BaseOutputPath/BaseIntermediateOutputPath = <private>/bin|obj/<ProjectName>/

artifacts/.tmp-android-verify/f3-verify/run-private.sh \
  --filter "FullyQualifiedName~AssistantStoreTests|FullyQualifiedName~AssistantRelayTests"
```

需要走仓库既有输出布局（会写 `artifacts/build` 等共享目录）时才用共享锁：

```bash
MPT_TEST_TEMP=<可写目录> \
flock artifacts/.tmp-android-verify/mobile-build.lock \
  ~/.dotnet/dotnet test tools/file-transfer/tests/FileTransfer.Core.Tests/FileTransfer.Core.Tests.csproj \
  -p:StageRepositoryModule=false --nologo --filter "FullyQualifiedName~Assistant"
```

共享工作树正在被其它并行任务改动、暂时编不过时，用独立验证工程（只读共享源码，
`obj/bin` 落在自己的 scratch 目录）：

```bash
F3_NO_LOCK=1 artifacts/.tmp-android-verify/f3-verify/run.sh --filter "FullyQualifiedName~Assistant"
```

`run.sh` 会把 `src/FileTransfer.Core/`（排除本任务自己的 `Assistant/`）整树快照到
`f3-verify/CoreSnapshot/`，本任务的源码与测试直接从真实路径编译。M4 已把根声明改成
`partial` 之后，快照与真实源码逐字节相同（`diff -r --exclude=obj --exclude=bin --exclude=Assistant`），
因此该方式的编译结果与真实工程一致。三次运行的日志都留在 `f3-verify/*.log`。

## 官方 OpenList 真实协议验收（v4.2.6，opt-in）

`OpenListIntegrationTests` 里的两个官方用例（`MPT_OPENLIST_TEST_BINARY` 指向
`/mnt/cache/data-cache/mpt-file-transfer/openlist-official/openlist`，只读复用、不下载）：

```bash
artifacts/.tmp-android-verify/f3-verify/run-openlist.sh     # 私有 ArtifactsPath=f3-openlist-build
→ Passed! - Failed: 0, Passed: 2, Skipped: 0, Duration: 17 s    （openlist2.log）
```

- 官方二进制：**v4.2.6**（Built At 2026-09-01 15:22:53 UTC，commit `2bdf16d`，go1.27.0 linux/amd64），
  与 `OpenListRuntime.Version` 固定版本一致。
- 共用 setup 抽到 `OfficialOpenListRelay`：只读复制官方二进制到本次测试自己的随机 temp root、
  `admin random` 初始化、启动 server、经 admin API 挂载 **Local 存储驱动**到 `/mpt`、
  用 `OpenListSetup` 建专用 WebDAV 账号；端口被占用时**报告并退出**，绝不去结束别人的进程。
- `OfficialServerInitializesUploadsListsDownloadsAndStops`（原有普通文件 roundtrip）保留，改用同一 helper。
- `OfficialServerCarriesTheAssistantConversationBetweenTwoDevices`（新增，真实协议端到端）：
  同一 conversation 下两个**独立数据根**；一次 compose（文字 + 中文名附件）+ 一个指定给手机的文件；
  receiver 离线时 sender 三条全部 `stored` 且无任何回执；receiver 上线后 pull 3 条、下载 2 个附件，
  文字无需下载，内容与字节逐一比对；中转磁盘上真实存在 `manifest.json`/`payload`/`receipts/<deviceId>.json`；
  真实回执让 sender 的定向条目变 `delivered`、自发条目保持 `stored` 并带上具体设备回执；
  receiver 重建 store 后内容仍能用 `assistant.open` 直接打开，再来一轮不重复 pull、不重复下载、无重复条目。
- 未覆盖：国内网盘账号（阿里云盘/夸克/115 等）的真实授权与云端驱动、跨机 Tailnet、
  Android/iOS 端运行——本次只覆盖官方服务 + Local 驱动的本机真实协议。

## 已验证（43 个用例全绿 + 2 个官方 opt-in）

真实工程（`tools/file-transfer/tests/FileTransfer.Core.Tests`，连同 M4 的 `FileTransfer.MyPowerTools`
模块一起编译）运行本任务的两个测试类：

```
dotnet test tools/file-transfer/tests/FileTransfer.Core.Tests/FileTransfer.Core.Tests.csproj \
  -p:StageRepositoryModule=false \
  --filter "FullyQualifiedName~AssistantStoreTests|FullyQualifiedName~AssistantRelayTests"
→ Passed! - Failed: 0, Passed: 43, Skipped: 0    （测试程序集构建于全部源码改动之后）
```

同一批用例在隔离工程（`F3_NO_LOCK=1 artifacts/.tmp-android-verify/f3-verify/run.sh`）里同样全绿；
共享锁被其它并行构建长时间占用时以隔离工程或私有 ArtifactsPath 为准
（Core 快照与真实源码逐字节相同，见上），三条路径互为独立证据。

保留的日志（`artifacts/.tmp-android-verify/f3-verify/`）：

| 日志 | 内容 |
| --- | --- |
| `private1.log` | 真实工程 + M4 模块，全局 `ArtifactsPath` 私有重定向（bin/obj 全部落在 `f3-private-build/`），43/43 |
| `build26.log` | 第三轮四项修复的隔离工程首跑，43/43 |
| `fixes.log` | DSH 重启后复跑（隔离工程，Core 快照与真实源码逐字节相同），43/43 |
| `private2.log` | 重启后的真实工程复跑：F3 的 `FileTransfer.Core` **编译通过**，但 M4 正在改的 `FileTransferModule.Assistant.cs` 引用尚未落地的 `MobileWifiMulticast` 而失败——并行任务未完成编译，不计入 F3 结果 |

第三轮补齐的行为：

| 用例 | 证明 |
| --- | --- |
| `FailedLiveSaveRestoresTheLastCommittedGeneration` | 调用方先改内存再 `SaveAsync`、写失败时：同实例与重启实例都回到磁盘上最后一次提交的那一代，无幽灵字段；解锁后同样的编辑可以正常提交 |
| `FailedLiveSaveAfterASyncKeepsTheCommittedGeneration` | 同步已提交 `stored` 之后再做一次失败的 live Save：同实例与重启实例都保持 `stored` |
| `CancellingTheInFlightUploadNeverReanimatesACancelledEntry` | 上传被阻塞时用户取消，随后 `CTS.Cancel()` 让网络调用失败：条目仍是 `cancelled`（内存与重启一致），后续轮次不再上传 |
| `CancellingASlowDownloadKeepsTheCancelledEntryAndNeverMarksItAvailable` | 下载被阻塞时取消 + `CTS.Cancel()`：不回到 `stored`、不变 `available`、`localPath` 为空，之后也不会再下载 |
| `EnsureLocalNeverOverwritesADirectReceiveThatWinsTheRace` | `assistant.open` 下载途中直传把同一条目升级为 `available`：取消令牌触发后该结果不被降级，`localPath` 保留直传文件 |
| `RelayFailureStopsEveryLaterStageInsteadOfOnlyTheUpload` | 预置“待查回执 + 待下载附件 + 待写回执”后让 PUT 失败：本轮请求**恰好只有第一次发布尝试**（3 MKCOL + 1 GET + 1 PUT），PROPFIND 为 0，之后不再发任何请求；本地状态保留（未假失败、未假下载、未假回执），中转恢复后一轮补齐 |

第二轮补齐的行为（同一套测试里的定向用例）：

| 用例 | 证明 |
| --- | --- |
| `MixedDraftSendsOneTextAndEveryAttachmentInOneAtomicBatch` | 一份 draft 同时带文字和附件 = 1 条文本 + 每个附件 1 条，全部持久后才返回，重启后 3 条都在 |
| `EnqueueRejectsEmptyTextAndMissingFilesWithoutLeavingAnythingBehind` | 混合发送中途失败不留下文本/半个附件，内存与磁盘都空，原件不动 |
| `FailedStateWriteRollsBackMemoryDiskAndRetriesExactlyOnce` | 状态文件写失败（临时路径被占）→ 同实例 inspect 与重启实例都是原内容、无幽灵条目、无残留 staging，重试只新增一次 |
| `FailedWriteRollsBackACancelToo` | 取消落盘失败时内存与磁盘都回到 `queued`，不会出现“界面已取消、磁盘还在排队” |
| `AdoptUpgradesAnEntryThatWasOnlyKnownFromTheRelay` | 只有 manifest 的条目不算已保存；直传补 payload 后**原地升级为 available**（不新增消息、不缺文件时直接拒绝），cancelled 不复活 |
| `DirectDeliveryUpgradesAKnownRelayEntryAndStillYieldsARealReceipt` | 中转已知条目 + 直传补文件 → 不再下载、写入真实回执、发送端拿到 delivered |
| `ReceiptChecksRotateFairlySoEveryConfirmedEntryEventuallyGetsItsReceipt` | 25 条自发消息全部被对端确认后，多轮有界同步（每轮 ≤ 12 次回执检查，总计 ≤ 36）让**每一条**都拿到真实回执并记录 `ReceiptCheckedAt` |
| `UnreachableRelayReportsRetryAfterAndStopsAfterTheFirstFailedRequest` | 中转 503 时本轮只发第一次发布尝试所需的请求就停（PUT=1、PROPFIND=0、总请求数=5，见第三轮的精确计数用例），`HasMore=false` + `RetryAfter` 非空；未尝试的条目保持 `queued` 而非假失败；恢复后 3 条一次发完且无重复 |
| `IncompletePublishNeverSpinsTheScheduler` | 只有 payload 没有 manifest 的目录：不算条目、`HasMore=false` 不空转，且每轮仍然只抓有界数量，晚到的 manifest 之后仍能取到 |
| `TwoSyncInstancesShareOneStoreWithoutLosingMessagesOrRevivingCancelledEntries` | 两个 `AssistantSync` 实例 + 慢上传 barrier：同步中并发 Enqueue/取消不丢新消息、不复活取消、不重复上传；第二个实例在 store 的同步锁上排队 |

真实 HTTP WebDAV 服务（`AssistantWebDavServer`，loopback + Basic 认证 + 真磁盘，不是 mock 客户端）：

| 用例 | 证明 |
| --- | --- |
| `PublishListAndDownloadRoundTripOverRealHttp` | PUT payload → PUT manifest 顺序；两台设备独立 store 经中转同步；回执字段 = 已保存字节数 |
| `InterruptedPayloadUploadNeverBecomesAReadableEntry` | payload PUT 失败即中止、远端无 manifest，对端看不到；重试后只有一条消息 |
| `InterruptedDownloadWritesNoReceiptAndNeverReportsDelivery` | 下载失败不落盘、不写回执、发送端不显示 delivered；恢复后就地重试才 delivered |
| `RePublishingTheSameIdAndRepeatedSyncsDoNotDuplicateAnything` | 同 id 重发只有 1 次 payload/manifest PUT；已记 id 不再重复 GET manifest |
| `ListingIsBoundedAndContinuesFromACursor` | `Limit`/`HasMore`/`NextCursor` 分页，3 条 = 3 次 manifest GET，无重复下载 |
| `SameAuthorityRedirectIsFollowedWithoutCredentials` | 跟随重定向不带凭据 |
| `ForeignPlainHttpRedirectIsRefusedBeforeAnyRequest` | 跨站明文 HTTP 重定向在发请求前拒绝 |
| `WebDavRequestsRequireTheAccountCredentials` | 服务端强制 Basic 认证；客户端每个 WebDAV 请求都带凭据 |
| `OfflineQueueSurvivesRestartAndPublishesWhenTheRelayReturns` | 断网入队失败不改状态语义、进程重启后待发副本仍在、网络恢复续发不重复 |
| `TextEntriesSyncWithoutDownloadingAnyPayload` | 文本条目对端直接 available，中转上没有 payload 对象，0 次 payload GET |
| `SameNameAttachmentsStaySeparateOnBothDevices` | 同名附件各自独立 itemId/目录，两端内容都对 |
| `TargetedSendBecomesDeliveredOnlyAfterTheTargetSavedIt` | `targetDeviceId` 保留；非目标设备只看到 stored、不下载不回执；目标保存后才 delivered |
| `EnsureLocalDownloadsAgainAfterTheLocalCopyWasRemoved` | `assistant.open` 对文本内联、对缺失文件按需下载 |
| `DirectlyReceivedEntryJoinsTheTimelineAndGetsItsReceiptOnTheNextSync` | M4 直传已保存的来件并入同一时间线、不重复下载、下一轮写出真实回执并让发送端 delivered |
| `CancelledEntriesAreNeverPublished` | 取消项不发布，中转无残留 |
| `InvalidManifestIsReportedWithoutPoisoningTheTimeline` | 非法 manifest 计入 `InvalidItemIds` 且不毒化其它条目，第二轮不再重复抓取 |

Store（无网络）：

| 用例 | 证明 |
| --- | --- |
| `EnqueueIsDurableBeforeItReturnsAndSurvivesRestart` | 返回即已落盘；重启后条目、payload 副本、原文件都在 |
| `DuplicateFileNamesNeverOverwriteEachOther` | 重复文件名不覆盖 |
| `RestartReturnsInFlightEntriesToRetryableStates` | `sending→queued`、`downloading→stored` |
| `CancelIsRefusedOnceADeviceConfirmedAndNeverDeletesThePayload` | 有回执/已送达不能取消；取消不删副本 |
| `ConversationIsNeverTrimmedToTheLegacyFiftyRecordCap` | 60 条全部保留，不被旧 50 条上限挤掉 |
| `ItemJsonMatchesTheContractAndTheManifestHidesLocalMetadata` | camelCase、小写枚举；manifest 不含 localPath/state/receipts/bytesDone/error |
| `CorruptConversationFileIsReportedAndNeverRewritten` | 损坏文件报错且原样保留，不静默丢弃 |
| `RestartSweepsOnlyTheToolsOwnPartialFiles` | 只清理本工具 `.mpt-*.part` |
| `OpenTargetReturnsInlineTextAndAsksToDownloadMissingFiles` | 打开语义 |
| `AdoptKeepsDirectlyReceivedEntriesAndDedupesById` | 直传来件按 id 去重、必须已保存、本机条目走 Enqueue |
| `StoreNeverTouchesUnrelatedFilesInItsDirectory` | 不动同目录下的 history.json/secret.json 等用户文件 |
| `EnqueueRejectsEmptyTextMissingFilesAndMixedDrafts` | 入队校验与整批回滚 |

## 未验证 / 剩余问题

- **M4 的 `AssistantModuleTests.AFirstContactFileWaitsForTheReceiverAndRemembersOnlyWhenAsked`
  在重复运行中出现过 1 次失败**（同一二进制、同一命令，另一次 40/40 全绿）：断言
  `pendingRequests` 时集合为空，看起来是直接通道后台循环与测试之间的时序竞争，不是助手内核的问题。
  建议 M4 用该文件已有的 `WaitForItemAsync` 同样方式等待 pending 请求出现。
- `FileTransfer.Surface/AssistantModels.cs` 里 M3 另有一份 internal 的 UI DTO，是**正确边界**：
  Surface 通过 M4 的命令 JSON 映射，不引用 Core 程序集；F3 只保证 API.md 里的 JSON 字段与状态语义，
  字段名变更必须同步 API.md，不做“让 Surface 依赖 Core 记录”的改动。
- 真实 OpenList、真实 Tailnet、Android/iOS 平台运行与国内网盘授权未在本次验证内（需要真机与账号，
  见 `docs/mobile-ux/FILE_ASSISTANT_CONTRACT.md` 验收 8）。
- 模块命令接线、事件发布、Surface 显示由 M4/M3 完成；F3 只保证这里列出的 API 与状态语义。
- `AssistantStore` 假定同一数据目录在单进程内只用一个实例（模块与后台调度共用），跨进程并发写不做锁。
- 目录 mtime 不可靠的网盘驱动下，分页顺序可能不是时间序；已知 id 集合保证不会重复下载，
  但首批历史追赶的先后顺序可能不是最新优先。
