# Source Note

本计划包综合以下信息：

```text
PowerToys 当前公开源码和 devdocs
本地工具调研记录
MyPowerTools 多轮架构讨论结论
```

最终结论以本计划包 v3 为准：MyPowerTools 采用独立 Runner、独立 Avalonia Shell、Typed MPT Module Protocol、Typed Host Control Protocol、Transport-tiered Module Runtime、InProc trusted module path、gRPC over Native IPC sidecar、multi-module package、platform capability packs、privileged broker、UI design token system、UI surface guardrails、agent gate roadmap 的平台底座方案。

PowerToys 源码对照写入 `docs/15-powertoys-code-comparison.md`。UI 约束写入 `docs/16-ui-guardrails.md`。执行方式写入 `docs/17-agent-execution-model.md`。
