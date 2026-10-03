# Paste Image 流程验证

运行：`npx e2e run tests/e2e/paste-image.e2e.ts --output .e2e/tools/paste-image`。
先运行根目录 `npm run test:e2e:prepare` 编译 Debug 回归测试。

该套件使用 tester-army/e2e 的 Windows tools-only target，驱动真实 .NET 模块和 Surface ViewModel 行为测试。每个分组验证 TRX 的执行、通过与跳过计数，源码扫描测试从流程通过计数中排除。

| 用户流程 | 自动验证 | 依赖边界 |
| --- | --- | --- |
| 设置 SSH host、目录、超时、上传后快捷键 | 应用后 inspect 返回新值；禁用上传后快捷键保留空值；命令 deadline 容纳 300 秒上传设置 | 独立模块实例 |
| 拒绝错误或危险配置 | SSH 选项前缀、父目录穿越、空格、shell 字符拒绝 | 实际 ValidateSettingsAsync |
| 剪贴板图像探测 | provider 调用、宽高、字节数返回；探测保留剪贴板 | 注入假剪贴板，真实用户剪贴板保持原状 |
| 查看历史 | 保存记录可读取；损坏历史文件保留原内容；Surface 最新五条 | 临时目录和实际模块文件读写 |
| 查看预览 | 缺失预览显示占位；损坏 PNG 返回空预览，保留历史与上传成功流程 | 临时损坏 PNG；直接调用实际解码方法 |
| 复制历史路径 | 选中记录复制准确路径，显示成功提示 | 注入 writer |
| 上传失败 | 缺失平台能力返回结构化错误并发布 upload.failed；空剪贴板显示中文指引；busy 释放 | 控制故障的命令响应；上传缺能力在 SSH 前失败 |
| 系统通知测试和未知命令 | 缺失通知能力/未知命令返回结构化失败 | 独立模块 |
| Ctrl+Alt+V / 点击上传 / 命令面板 | 入口统一到 paste-image.upload | 真实桌面入口待验收 |
| PNG 经 SSH 上传、路径回剪贴板、上传后快捷键 | 待真实远端与前台应用验收 | 需测试 SSH 账号、图像与专用剪贴板会话 |
| 成功 toast、实时事件推送、预览选择、刷新按钮、持久化性能记录 | 待真实桌面交互验收 | tools-only target 无 Windows 桌面 UI engine |
| macOS 权限与粘贴、全局快捷键 | 待 macOS 真机验收 | 当前 Windows 环境 |

本套件通过仅代表表内自动验证项。真实桌面 UI、真实 SSH 上传、系统剪贴板、全局快捷键和原生通知仍需要对应环境的验证记录。

测试使用唯一临时目录，只清理本次目录。测试不访问生产 SSH 账号，不覆盖用户剪贴板，不写真实远端文件。

## 2026-10-04 验证结果

最终执行：3 个 e2e 分组通过，实际运行 13 个 .NET 回归实例，全部通过、零跳过。报告位于 `.e2e/tools/paste-image/report.json` 与 `summary.md`。

修复前先运行回归，逐项确认失败；记录位于 `.e2e/tools/paste-image-before-fix` 和 `.e2e/tools/paste-image-before-preview-timeout-fix`。

- SSH host 校验接受 `-V`、`--` 与 `-oProxyCommand`；这些值可被 OpenSSH 解释为选项。修复后拒绝目标首字符 `-`，普通 SSH alias 和 `user@host` 保留。
- 真正初始化 Skia renderer 后，有效 2×3 PNG 正常解码，损坏 PNG 抛 `ArgumentException`。预览读取现在对文件、权限与解码错误返回空预览，让历史和成功上传事件继续处理。初始化 renderer 之前的首轮测试失败为测试环境问题，已纠正，未计入产品 bug。
- 上传配置支持 300 秒，模块命令截止时间固定 60 秒，Surface 固定 65 秒。两层现在预留 310 秒；传输仍执行用户配置超时。模块命令描述可缓存，预留上限同时覆盖设置变更场景。

Surface 测试在既有独立 headless UI 线程执行；假 transport 返回已完成任务，并在等待前断言完成状态，避免后续测试修改引入 UI 线程死锁。
