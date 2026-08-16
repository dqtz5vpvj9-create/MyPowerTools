# Remote Notifications Code Management Plan

## 1. 目标

建立 Remote Notifications 全链路的代码所有权、版本控制和部署边界，为后续 Session ID 协议改造提供稳定基础。

计划状态：Session Metadata v2 已于 2026-08-09 完成首轮生产发布和端到端验证；安装器与主仓子模块指针收口继续跟踪。

本计划覆盖五部分：

1. Codex Hook 包装器
2. 通知队列、发送器、服务器与 Windows 客户端
3. NotifyApp 手机客户端
4. 公网生产服务器部署
5. Windows `~/.codex/hooks` 部署副本

## 2. 总体治理原则

- 每份业务代码只能有一个权威 Git 仓库。
- 运行环境中的代码目录统一视为部署副本。
- 部署副本禁止承载长期源码修改。
- 生产发布使用明确的 Git commit、tag 或构建产物。
- 跨仓库协议通过版本化契约、兼容测试和发布顺序管理。
- 配置文件、密钥、设备 Token 和证书留在运行环境，不提交到 Git。
- 现有脏工作区先制作补丁和清单，再迁移到对应权威仓库。

## 3. 五部分管理设计

### 3.1 Codex Hook 包装器

| 项目 | 约定 |
| --- | --- |
| 权威仓库 | `https://github.com/dqtz5vpvj9-create/codex-tmux-integration.git` |
| 权威目录 | `features/external-notifications/adapters/androidtools/` |
| 当前运行目录 | `/home/chris/src/codex-tmux-integration/` |
| 安装入口 | `/home/chris/.local/bin/codex-tmux-notify-wrapper` 软链接 |
| 本机私有配置 | `/home/chris/.config/codex-tmux-integration/notifications.env` |
| 负责内容 | 捕获 Codex Hook JSON、保持原始字段、调用通知队列 |
| 禁止承载 | HTTP 协议定义、服务器业务、客户端展示逻辑 |

管理规则：

- Hook 包装器只负责无损传递事件。
- `session_id`、`turn_id` 等原始字段不得在包装层重新生成。
- 安装脚本负责创建 `~/.local/bin` 软链接和私有配置模板。
- `notifications.env` 只保存 Python 路径、队列路径和频道等机器配置。
- Hook 事件透传测试放在该仓库中。

现有工作区收口：

- [ ] 导出 `/home/chris/src/codex-tmux-integration` 的完整未提交补丁。
- [ ] 将补丁按功能拆分，区分通知相关改动和其他 tmux 功能改动。
- [ ] 为通知相关改动建立独立分支。
- [ ] 提交并推送通知相关改动。
- [ ] 验证安装器能够从干净 clone 恢复当前 Hook 包装器。

### 3.2 队列、发送器、服务器与 Windows 客户端

| 项目 | 约定 |
| --- | --- |
| 权威仓库 | `https://github.com/dqtz5vpvj9-create/MyPowerTools-remote-notifications.git` |
| MyPowerTools 挂载方式 | `tools/remote-notifications` Git submodule |
| 当前开发分支 | `feature/session-metadata-v2` |
| 原始 Python 链路 | `original-source/py_modules/` |
| Windows Service | `current-integration/src/RemoteNotifications.Service/` |
| Windows Surface | `current-integration/src/RemoteNotifications.Surface/` |
| 负责内容 | 队列、发送协议、服务端、共享通知契约、Windows 接收与展示 |

该仓库作为 Remote Notifications 产品主仓库，负责定义协议的唯一版本。

建议新增目录：

```text
contracts/
  notification-v1.schema.json
  notification-v2.schema.json
  examples/
    notification-v1.json
    notification-v2-codex.json
docs/
  protocol-versioning.md
  deployment.md
scripts/
  install-codex-hook-windows.ps1
  install-codex-hook-linux.sh
  deploy-server.sh
```

管理规则：

- HTTP 请求、Redis 表示、`/pull` 响应和推送 payload 共享同一契约定义。
- 新字段先设为可选，通过新协议版本逐步启用。
- Python 服务端读取旧格式与新格式。
- C# 客户端模型与协议测试跟随契约更新。
- `original-source` 中的 Python 文件保持可独立部署。
- MyPowerTools 主仓只记录子模块 commit，不复制子模块业务源码。

现有分支收口：

- [x] 审核本地领先远端的 1 个提交。
- [x] 保留该提交并从其建立 Session ID 功能分支。
- [x] 建立并推送 `feature/session-metadata-v2`。
- [x] 增加协议契约目录和兼容性测试。
- [ ] 更新 MyPowerTools 主仓的子模块指针。

### 3.3 NotifyApp 手机客户端

| 项目 | 约定 |
| --- | --- |
| 权威仓库 | `git@github.com:dqtz5vpvj9-create/NotifyApp.git` |
| 当前运行开发目录 | `/android/androidtools/NotifyApp` |
| 当前分支 | `master` |
| 负责内容 | UnifiedPush、FCM、手动 `/pull`、本地历史和手机 UI |
| 发布产物 | 签名 APK、版本清单、校验值 |

管理规则：

- NotifyApp 通过独立版本号发布。
- UnifiedPush、FCM 和 `/pull` 使用同一个通知数据模型。
- 数据库升级使用显式 migration。
- APK 禁止作为唯一源码载体。
- GitHub Release 或受控制品目录保存 APK、版本和 SHA-256。
- `google-services.json`、签名文件及凭据按私有配置管理。

现有工作区收口：

- [x] 导出 `/android/androidtools/NotifyApp` 的未提交补丁。
- [x] 将现有修改纳入可追踪功能分支基线。
- [x] 推送 `feature/session-metadata-v2`。
- [x] 从专用干净 worktree 验证测试与 Release 构建。
- [x] 建立 `feature/session-metadata-v2` 分支。
- [ ] 为旧数据库记录和缺少 Session ID 的消息增加兼容测试。

### 3.4 公网生产服务器部署

| 项目 | 约定 |
| --- | --- |
| 主机 | `proxy.lixinrui000.cn`，主机名 `ubuntu-proxy` |
| 生产服务 | `simple_http_notification_server_lxr.service` |
| 当前工作目录 | `/home/ubuntu/repo/androidtools_lxr/` |
| Redis | `redis_instance@28888.service` |
| 服务端口 | HTTPS `8888` |
| 当前域名 | `message.lixinrui000.cn` |
| 权威源码来源 | `MyPowerTools-remote-notifications` 的固定 commit/tag |
| 部署控制面仓库 | `dqtz5vpvj9-create/AutoDroid` |
| 部署控制机 | `r743-autodroid` |

生产目录定位为部署副本。旧 `androidtools_lxr` Git 工作区完成迁移后停止承担源码管理职责。

#### AutoDroid 收口边界

决策状态：**已批准**。

AutoDroid 管理公网部署控制面：

- 固定 `MyPowerTools-remote-notifications` 的发布 tag 和完整 Git SHA
- 构建或下载服务端发布包
- 校验发布包 SHA-256
- 通过 SSH 上传到公网服务器的新版本目录
- 执行部署前检查、原子切换、服务重启和健康检查
- 记录部署清单并执行版本回滚

服务器业务源码继续由 `MyPowerTools-remote-notifications` 管理。AutoDroid 中不复制 `simple_http_notification_server.py`、通知协议实现或 Windows 客户端代码。

批准采用的 AutoDroid 目录：

```text
ops/remote-notifications/
  README.md
  versions.json
  deploy.py
  rollback.py
  verify.py
  inventory.example.toml
  systemd/
    simple_http_notification_server_lxr.service
```

`versions.json` 示例：

```json
{
  "production": {
    "repository": "dqtz5vpvj9-create/MyPowerTools-remote-notifications",
    "tag": "remote-notifications-server-vX.Y.Z",
    "commit": "<full-git-sha>",
    "artifact_sha256": "<sha256>"
  }
}
```

`inventory.toml`、SSH 目标、用户名和凭据留在 r743 的私有配置目录，Git 只保存 `inventory.example.toml`。

#### r743 工作区要求

`r743-autodroid:repo/androidtools/AutoDroid` 当前实际映射为：

```text
host: 192.168.22.23
user: lxr2
path: /home/lxr2/repo/androidtools/AutoDroid
```

审计时该工作区存在 33 项变化，且 `demo_dag` 落后 `github/demo_dag` 112 个提交。部署控制面进入专用干净 worktree：

```text
/home/lxr2/worktrees/autodroid-remote-notifications-deploy
```

批准采用的工作分支：

```text
ops/remote-notifications-deploy
```

该 worktree 通过审核和合并后，r743 的部署任务从固定 commit 运行，避免依赖实验工作区状态。

批准的所有权边界：

| 内容 | 权威位置 |
| --- | --- |
| 服务端业务源码与协议 | `MyPowerTools-remote-notifications` |
| 部署版本清单、发布、验证和回滚 | `AutoDroid/ops/remote-notifications` |
| 生产私有配置与凭据 | r743 和 `ubuntu-proxy` 的系统配置目录 |
| 生产运行副本 | `ubuntu-proxy:/opt/remote-notifications/releases` |

AutoDroid 通过 Git tag、完整 commit SHA 和制品 SHA-256 引用服务端版本。两个仓库之间不复制业务源码。

建议部署布局：

```text
/opt/remote-notifications/
  releases/
    <version>-<git-sha>/
  current -> releases/<version>-<git-sha>/
  shared/
    env
    credentials/
    redis-dump/
```

建议 systemd 入口：

```text
WorkingDirectory=/opt/remote-notifications/current
EnvironmentFile=/etc/androidtools/notify_server_lxr.env
ExecStart=<venv>/python -m gunicorn ...
```

管理规则：

- 发布包由权威仓库的固定 commit 构建。
- 发布过程先上传新版本目录，再原子切换 `current`。
- 每次部署记录版本、Git SHA、时间和操作者。
- 保留前一个可运行版本，用于快速回滚。
- 环境变量文件、SSH authorized keys、FCM/ntfy 凭据留在 `/etc`。
- 部署前运行协议测试和 Redis 兼容性测试。
- 部署后运行 `/pull`、UnifiedPush 和 Windows 客户端烟雾测试。

现有生产工作区收口：

- [x] 在 r743 从 GitHub 最新基线创建 AutoDroid 专用干净 worktree。
- [x] 在 AutoDroid 新增 `ops/remote-notifications` 部署控制面。
- [x] 为部署脚本增加 dry-run、目标确认和产物哈希验证。
- [x] 备份 `/home/ubuntu/repo/androidtools_lxr` 的 Git diff、运行文件和服务配置。
- [x] 将通知相关生产改动与权威仓库逐文件比对。
- [x] 将 Session Metadata v2 改动提交到 `MyPowerTools-remote-notifications`。
- [x] 建立首次可追溯发布版本。
- [x] 部署到 `/opt/remote-notifications/releases/`。
- [x] 修改 systemd 指向版本化部署目录。
- [x] 保留 legacy release 和原工作区作为回滚来源。

### 3.5 Windows `~/.codex/hooks` 部署副本

| 项目 | 约定 |
| --- | --- |
| 当前目录 | `C:\Users\lixinrui\.codex\hooks` |
| Git 状态 | 无独立仓库 |
| 权威源码来源 | `MyPowerTools-remote-notifications/original-source/py_modules` |
| 本机入口 | `notify_launcher/publish/codex_hook_notify.exe` |
| 负责内容 | 本机运行和机器级配置 |

管理规则：

- 此目录保持部署副本属性。
- Python 业务文件由安装脚本从固定 Git commit 同步。
- 本机私有配置使用独立文件，安装时保留。
- 安装清单记录每个部署文件的 SHA-256 和来源 commit。
- 启动器源码进入权威仓库，发布目录只保留构建产物。
- 手工修改部署副本后，校验命令必须报告漂移。

建议安装清单：

```json
{
  "source_repository": "MyPowerTools-remote-notifications",
  "source_commit": "<git-sha>",
  "installed_at": "<utc-time>",
  "files": {
    "notification_queue.py": "<sha256>",
    "send_notification.py": "<sha256>",
    "simple_http_notification_sender.py": "<sha256>"
  }
}
```

收口步骤：

- [ ] 将 `notify_launcher` 源码移入 `MyPowerTools-remote-notifications`。
- [ ] 比对当前部署副本与权威源码。
- [ ] 将部署副本中特有的必要修改移植回权威仓库。
- [ ] 编写幂等安装脚本。
- [ ] 编写 `verify-install` 漂移检查。
- [ ] 用安装脚本重新生成本机部署副本。

## 4. 跨仓库协议管理

协议版本由 `MyPowerTools-remote-notifications/contracts` 定义。

兼容矩阵：

| 发送端 | 服务端 | 客户端 | 要求 |
| --- | --- | --- | --- |
| v1 | v1 | v1 | 当前行为 |
| v2 | v1 | 任意 | 发布流程禁止出现 |
| v1 | v2 | v1/v2 | 必须兼容 |
| v2 | v2 | v1 | 核心消息可读，Session 元数据被忽略 |
| v2 | v2 | v2 | 展示完整 Session 元数据 |

发布顺序固定为：

1. 兼容 v1/v2 的服务器
2. NotifyApp v2
3. MyPowerTools Windows 客户端 v2
4. Codex 发送端 v2
5. 清理与观测

## 5. 分支与版本策略

- `main` 或产品主分支保持可发布。
- 跨仓库功能使用相同分支名：`feature/session-metadata-v2`。
- 协议版本使用整数 `schema_version`。
- 服务端发布 tag：`remote-notifications-server-vX.Y.Z`。
- Windows 模块发布 tag：`remote-notifications-vX.Y.Z`。
- 手机发布 tag：`notifyapp-vX.Y.Z`。
- 部署记录引用完整 Git SHA。
- 紧急修复从当前生产 tag 建立 hotfix 分支。

## 6. 配置和秘密管理

提交到 Git：

- 配置模板
- 环境变量名称
- 示例域名和示例频道
- 安装与验证脚本

留在运行环境：

- SSH 私钥和 authorized keys
- TLS 私钥
- Firebase service account
- `google-services.json`
- ntfy Basic Auth
- FCM Token 与 UnifiedPush endpoint
- 用户频道和机器专用路径

每个仓库提供 `.example` 文件，并在 `.gitignore` 中覆盖真实配置。

## 7. 首轮治理交付物

- [ ] 三个权威 Git 仓库状态清单。
- [ ] `chris` 两个脏工作区的可恢复补丁。
- [ ] 公网生产目录的可恢复补丁和未跟踪文件归档。
- [ ] Remote Notifications v1/v2 协议契约。
- [ ] Windows Hook 安装与漂移检查脚本。
- [ ] Linux Hook 安装与漂移检查脚本。
- [ ] 生产服务器版本化部署与回滚脚本。
- [ ] NotifyApp 可复现构建说明。
- [ ] 全链路兼容矩阵测试。
- [ ] Session ID 功能实施计划。

## 8. 2026-08-09 首轮生产发布记录

| 组件 | 版本或提交 | 发布状态 |
| --- | --- | --- |
| 协议、服务器、Windows 客户端 | `4afb7675373ba9c0e1fe62d25124aaef54e6341c` | 已推送 `feature/session-metadata-v2` |
| NotifyApp | `7da9e42`，版本 `1.23.0`，versionCode `37` | 已推送、构建并安装到 `PJZ110` |
| AutoDroid 部署控制面 | `7aec04a` | 已推送 `ops/remote-notifications-deploy` |
| 服务端发布包 | `0.3.0-session-metadata-4afb7675373b` | 已部署并激活 |
| 公网运行目录 | `/opt/remote-notifications/current` | 指向版本化 release |

制品哈希：

- 服务端包：`2547c2e497cf4e3575711b7b54bdec3a344e28cba9e7ea351996c728c961d419`
- NotifyApp APK：`8f4782fef2c0a61b3c2047f3e6f002904e08b299ad1ed5f3b2969a60350b32c5`

生产备份与回滚点：

- chris 脏工作区备份：`/home/chris/.cache/remote-notifications-migration/20260809T102405Z/`
- 公网生产备份：`/var/backups/remote-notifications/20260809T102349Z/`
- 生产 legacy release：`/opt/remote-notifications/releases/legacy-20260809T102349Z`

## 9. 端到端验收结果

- Windows Codex Hook 经队列向生产服务器发送 v2 通知，FCM 返回 HTTP 200。
- chris Codex Hook 经同一队列链路发送 v2 通知，FCM 返回 HTTP 200。
- Redis 新记录保存 `schema_version`、`session_id`、`session_name` 和 `source_client`。
- Windows Service 从生产 `/pull` 拉取并持久化完整 Session Metadata。
- MyPowerTools 桌面列表显示会话标签和 Session ID 短值。
- NotifyApp 1.23.0 在手机列表显示会话标签和短 ID，详情页显示完整 UUID。
- ntfy/UnifiedPush 8889 已重新加载有效证书；证书续期后自动重启钩子已安装。
- 服务端 `/version` 返回 `1.23.0`，`/update.apk` 使用同一发布 APK。

验收截图位于 `artifacts/remote-notifications/`：

- `desktop-session-list.png`
- `notifyapp-session-list.png`
- `notifyapp-session-detail.png`

## 10. 后续治理项

- [x] 更新 MyPowerTools 主仓子模块指针并提交本计划。
- [ ] 将 Windows Hook 手工同步过程固化为幂等安装器和漂移检查。
- [ ] 将 Linux Hook 安装和漂移检查补入权威仓库。
- [ ] 整理并合并 chris 的 `codex-tmux-integration` 通知相关历史改动。
- [ ] 为 NotifyApp 旧本地记录增加独立的 migration/兼容回归测试。
- [ ] 为 r743 配置到公网服务器的直连路由或明确的 SSH jump host。

## 11. DeepSeek Harness（DSH）全链路接入

状态：2026-08-16 完成首轮实施，本机与 chris 均收到通知。

### 接入机制

- DSH 官方 hook 子系统通过 `@deepseek-ai/dsh-hooks-codex` 把 Codex `hooks.json`
  的受支持子集映射到 DSH 拦截点；本次只使用 `Stop` → `agent/turn-stopping`。
- Stop payload 携带 `session_id`、`transcript_path`、`cwd`、`turn_id` 和
  `model`，与现有 v2 通知契约兼容。
- hook 命令通过 DSH 的 shell seam 执行：Windows 是 `pwsh-sandbox`，Linux 是
  `bash-sandbox`。两端 profile 均显式覆盖 `sandbox-policy` 为
  `danger-full-access`，否则 hook 无法写通知队列缓存。

### 部署清单

| 位置 | 内容 |
| --- | --- |
| `tools/remote-notifications` | `feature/dsh-hooks`：`send_notification.py`/`notification_queue.py` 支持 `dsh` 客户端；`original-source/scripts/dsh_hook_notify.ps1` 为 Windows 包装器 |
| 本机 `~/.dsh/profiles/{web,headless}` | 安装 `dsh-hooks-codex` 与 `dsh-hook-protocol`，patch 指向 `C:/Users/lixinrui/.codex/dsh-hooks.json` |
| 本机 `~/.codex/dsh-hooks.json` | 只保留 Stop 通知 hook，调用 `dsh_hook_notify.ps1` |
| chris `~/.dsh/profiles/{web,dsh-tui,headless}` | 同样安装 hooks 插件，patch 指向 `/home/chris/.dsh/dsh-hooks.json` |
| chris `~/.dsh/dsh-hooks.json` | Stop hook 直接调用 `/android/androidtools/py_modules/notification_queue.py enqueue --client dsh` |
| chris 通知环境 | 安装 `zstandard`，用于读取 DSH zstd transcript |

### 端到端验证记录

- 本机 headless 会话 `session-a8c32dcc-6331-446a-8664-ca7efbf8bdc7`：
  Redis 记录 `session_name=local-dsh-final`、`source_client=dsh`。
- chris headless 会话 `session-2d52bd4d-46e4-4750-a09b-2f84658673a2`：
  Redis 记录 `session_name=Reply with exactly: chris-dsh-final-2. D`、
  `source_client=dsh`。
- MyPowerTools 桌面历史已持久化多条 `dsh` 记录。
- NotifyApp 1.23.0 手机列表顶部显示本机与 chris 的 DSH 会话标签和短 ID。
- 本地发送端 FCM 返回 HTTP 200（`token_count=2`）。

## 12. 长消息全文同步（截断修复）

状态：2026-08-16 修复并发布。

### 根因

- DSH 发送端 `_dsh_last_assistant_message` 曾把最后一条助手消息截到 2000 字符，
  截断后的正文直接写入服务器 Redis，所有客户端都只能拿到截断版。
- FCM data payload 有 4 KiB 上限；即使发送端不截断，超长正文也无法完整推送。

### 修复

- 发送端不再截断 DSH 最后消息，服务器和桌面端保存完整正文。
- `fcm_push.py` 对超长消息只推送 3 KiB 预览，并附带 `truncated=1` 标记。
- NotifyApp 1.23.1：收到 `truncated=1` 时不保存截断正文，后台从服务器按 ID
  拉取完整记录后精确替换；手动/下拉拉取也使用同一“服务器为准”的 upsert。
- 仓库实现：`NotificationRepository.upsertServerItem`、
  `NotificationServerSync.pull`；FCM/UnifiedPush 接收器共用同步路径。

### 发布记录

- Remote Notifications：`c88f9f7`（`feature/dsh-hooks`）
- NotifyApp：`04555b0`（`feature/session-metadata-v2`），versionCode 38、
  versionName 1.23.1
- 生产服务器 `/version` 返回 `1.23.1`，`/update.apk` 为新 APK
- APK SHA-256：`e567c5d3f18997e9ba7cb7c063edfc949dca68cb74cd24e14b2c5a7d45840aae`

### 验证

- 完整 Choreo 报告重新发送后，Redis 与桌面历史均保存完整正文
  （14115 字符 / 20985 字节，结尾为报告原文末尾）。
- NotifyApp 1.23.1 手机端拉取后列表显示完整报告，结尾标记完整。

## 13. 通知中引用触发请求

状态：2026-08-16 实现并推送。

- Stop 通知现在在开头以引用块形式附带触发本轮的用户请求，再接助手回复。
- DSH：从会话 transcript 的 `user/message` 事件提取（`source.kind=user`）。
- Codex：从 rollout 的 `response_item → role=user → input_text` 提取，
  优先使用 payload 自带的 `user_prompt`。
- 真实 DSH headless 会话验证：Redis 记录以
  `[会话名] > 回复：带引号请求测试。…` 开头，随后是完整助手回复。
- Remote Notifications 提交：`1a88925`（`feature/dsh-hooks`）。

### 顶行调整

- 用户要求引用块顶行：消息格式改为
  `> 用户请求` 在第一行，随后 `[会话名] 助手回复`。
- 桌面端 `ExtractLabel` 与 NotifyApp 标签提取同步改为全文搜索第一个
  `[label]`，会话分组不受影响。
- Remote Notifications：`36afb90`
- NotifyApp：`36af5c6`（versionCode 39 / 1.23.2）
- AutoDroid 部署控制面：`3ff3300`
- 真实 DSH 会话验证：Redis 记录首行为
  `> 顶行测试：回复确认即可，不要调用工具。`

### chris Codex 通道恢复

- 同步队列脚本时曾用权威版本覆盖了 chris 的本地队列，导致 Linux Codex
  包装器的 `--stdin-file` 参数不被识别，Codex 通知全部入队失败。
- 权威 `notification_queue.py` 已补回 `--stdin-file` 支持并重新同步 chris。
- Codex 请求提取优先使用 payload 自带的 `transcript_path`，避免按 session 全盘扫描。
- 验证：chris 上手动触发 Codex Stop 包装器，队列 `client=codex` 入队并发送成功，
  Redis 记录 `source_client=codex`。
- Remote Notifications：`659ddb5`

### 代码复审补充修复

- 服务器 `send_unifiedpush` 与服务器 FCM 路径同样使用 3 KiB 预览 + `truncated`
  标记，UnifiedPush 长消息也能在 App 端自动同步全文。
- 桌面与手机标签提取改为“行首 `[label]`”，引用块正文中的方括号不会误判为标签。
- Remote Notifications：`a36ab24`
- NotifyApp：`563f61d`（versionCode 40 / 1.23.3）
- AutoDroid 部署控制面：`0fde227`
- 生产服务目录：`/opt/remote-notifications/releases/0.3.1-full-sync-a36ab24`
