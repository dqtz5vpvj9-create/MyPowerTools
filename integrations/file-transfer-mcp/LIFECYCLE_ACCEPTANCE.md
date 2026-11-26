# 共享 MCP 生命周期改造与验收

验收日期：2026-10-10（洛杉矶），对应 2026-10-11 UTC。范围为 Windows / Linux 文件传输 MCP；未改主程序模块预加载、隐藏界面预热或主程序 GC。

## 为什么出现几十个进程

Windows 本次基线为 16 套 stdio MCP，共 32 个 `pythonw.exe`，私有提交合计 1,221,320,704 字节，约 1.14 GiB。此前审计的 17 套 / 34 个进程是不同时间的快照。

15 套属于同一个常驻 Codex app-server，另一套属于独立 HAPI Codex runtime。前者当时有 15 个 loaded thread：13 个 idle、2 个 active。大部分 MCP 进程在引擎启动后数秒内创建，已驻留约 22 小时。venv 启动器与实际 Python 解释器各占一个进程。每次工具调用还由 `server.py` 启动 CLI 子进程。

Windows HAPI 监督脚本保存 loaded root 清单，在引擎恢复时对这些历史聊天调用 `thread/resume`；旧 stdio 配置因此随聊天恢复再次启动。聊天 idle 不等于 MCP stdin 已关闭，所以这些不是仅靠补 EOF 清理就能解决的孤儿进程。没有删除聊天、取消模型任务或修改历史恢复策略。

## 实现

`src/MyPowerTools.TransferMcp` 使用官方 C# MCP SDK 1.4.1，以无会话状态的 Streamable HTTP 暴露原有 14 个工具。CLI 的发送、收件人选择、网盘策略、回执判定和错误结果在进程内复用，随后通过认证 HostControl IPC 直接调用 Runner，不启动外部 CLI。

默认只监听 `127.0.0.1:17843`，使用独立于 Runner 的 bearer token。未认证请求返回 401，浏览器 Origin 与非本机 Host 返回 403。Windows 安装目录限制为当前用户，Linux 目录 0700、凭据和客户端配置 0600。配置迁移保留其他 MCP 及原有工具禁用设置。

Windows 使用已有 ServiceManager 的 `mpt-transfer-mcp.service`；Linux 使用同名用户 systemd unit。Python 仅参与安装或验收，不是新服务运行依赖。Windows 服务以 `CreateNoWindow` 启动。

生命周期分为三个边界：

- 聊天连接不创建常驻会话对象或子进程。
- 回执等待订阅随 HTTP 断连取消并释放，已经接受的文件继续传输。
- 已交给 Runner 的短命令完成后再关闭其 IPC，断连后不再发起后续命令。设备查询实测约 3.8–5 秒结束。这避免调用方离开时把正在清理设备发现的模块误判为超出取消边界，并隔离整个文件传输模块。

压力测试还复现了 IPC 工厂的真实资源泄漏：`GrpcChannel` 默认不释放传入的 HTTP handler。已设置 `DisposeHttpClient=true`，让 channel 同时关闭它拥有的连接池。回归测试直接观察 Unix socket EOF；修改前 5 秒仍未关闭，修改后通过。没有靠强制 GC 或定期重启服务回收内存。

SDK 的 stateless 模式与断连语义参考[官方文档](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/stateless/stateless.html)，实际协议兼容性通过现有 Windows Codex 客户端验证。

## 实测

客户端通过 `config/mcpServer/reload` 热重载，旧 Python 进程自然退出。随后重新 resume 一个现有历史聊天并通过其 MCP 调用查询，仍只有一个共享服务进程。Runner 的后台恢复通过已有服务管理器完成，Shell PID 前后一致；没有打开桌面窗口。

最终边界修复版本在同一 Windows 服务 PID 下执行 3 轮，每轮 80 次连接，共 240 次；每次包含 initialize、tools/list 和实际工具调用，最多 8 个客户端并发。每轮还测试设备查询中断与真实回执订阅中断，并随后检查 Runner 可用。

| Windows 测量点 | 私有提交 | 句柄数 |
| --- | ---: | ---: |
| 第一轮结束，作为预热基线 | 54,026,240 B | 563 |
| 第二轮结束 | 77,430,784 B | 537 |
| 第三轮结束 | 56,999,936 B | 534 |
| 恢复历史聊天后 | 55,861,248 B | — |
| 再闲置 30 秒 | 55,078,912 B | 494 |

每轮结束 `activeCalls=0`、`activeSubscriptions=0`、`sessions=0`。进程数保持 1，回到约 53 MiB 的预热范围，相比初始 1.14 GiB 减少约 95%。30.08 秒闲置采样消耗 CPU 0.015625 秒，约为单核的 0.052%；这是短窗口测量，不是主程序或 macOS 的功耗验收。首次启动的 16–24 MiB 不作为预热后的内存基线；JIT、程序集和缓存会驻留。

进程启动事件审计中出现过其他聊天的 Python 进程，但父 PID 均不属于共享 MCP；文件传输 MCP 没有启动 Python / CLI 子进程。按实际 TransferMcp 路径重新枚举，旧 Python 实例为 0。

Linux 同样通过连接循环、鉴权拒绝、14 工具发现、设备查询中断、真实回执订阅释放和断连后的 Runner 调用验证。

通过 Windows 新 MCP 向公屏发送 `mpt-shared-mcp-20261011.txt`（52 字节），消息 `9383992209824d1ba8cbcbcfa3a51b6c` 收到 Ubuntu 的保存回执。Linux 消息为 `available`，读取接收文件与发送内容逐字节一致。

源码回归：27 项 .NET 测试通过，包括持久消息不因等待结束而取消、断连释放订阅、IPC 连接关闭；2 项配置迁移测试通过。HTTP 实测脚本同时验证枚举参数约束，避免无效网盘策略触发部分设置变更。

## 复验入口

```bash
python3 integrations/file-transfer-mcp/check_http_lifecycle.py \
  --token-file /private/path/http.token --rounds 3 --connections 40
```

该脚本只读现有状态，不发送文件或更改账号。若没有待接收的真实消息，订阅断连项会明确报告未覆盖，不能当作完整通过。Windows 使用隐藏 PowerShell 启动同一脚本即可；凭据不得打印到日志。

本轮没有验证 Windows 注销、整机重启或 macOS，也没有部署主程序 GC / 预加载改造。HTTP 服务基线为一个可等待请求的常驻进程，而不是每次连接后退出为零个进程。
