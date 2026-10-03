# 文件互传 E2E 状态

本次检查时，主仓库 `tools/file-transfer` 目录缺失。`tmp/file-transfer-initial-import` 与 `tmp/MyPowerTools-ci-both-installers/tools/file-transfer` 中存在外部导入内容，导入仍待合入；本次任务保留这些内容，未复制或修改导入树。

`tests/e2e/file-transfer.e2e.ts` 明确记录此前置条件并跳过。跳过数量不能计入已验证流程；当前文件互传流程覆盖为 **0**，尚未完成此工具的缺陷排查与验证。

运行入口：

```powershell
npx e2e run tests/e2e/file-transfer.e2e.ts --output .e2e/tools/file-transfer
```

合入后需以主工具树的当前产品代码替换此前置用例，使用临时文件、回环地址与本机假服务覆盖实际行为：接收/发送与文件字节校验、权限与路径限制、取消/断线/重试、批量失败隔离、同名文件、发布确认与待发布恢复、历史与损坏配置保留、WebDAV/公开中继/反向流、云账号隔离和页面状态恢复。原生测试和中继测试需实际运行，并由 E2E 报告记录通过、失败及跳过项。

真实 Tailscale 跨设备传输、手机平台下载目录、系统选择器/拖放/剪贴板与外部网盘登录需各自的实际环境验收。现有导入内容中的测试说明和测试文件尚未在主工具树回归，不能作为本次成功证据。
