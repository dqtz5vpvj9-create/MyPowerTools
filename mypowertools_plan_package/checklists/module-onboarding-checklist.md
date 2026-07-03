# 模块接入检查表

## Manifest

- [ ] `package.json` 通过 schema validation
- [ ] `module.json` 通过 schema validation
- [ ] module id 稳定且符合命名规则
- [ ] command id 全部带 module id 前缀
- [ ] platforms 声明完整
- [ ] entrypoints 至少一个可用
- [ ] capabilities 声明完整
- [ ] permissions 声明完整

## Transport

- [ ] T0 静态索引可读取
- [ ] T1 InProc 模块完成接口测试，或
- [ ] T2 gRPC Native IPC 完成连接测试，或
- [ ] T3 HTTP facade 完成 typed wrapper，或
- [ ] T4 stdio fallback 明确标注 compat
- [ ] TransportSelector 能选中最佳 entrypoint
- [ ] fallback 行为可预测

## Protocol

- [ ] `Initialize` 可用
- [ ] `GetStatus` 可用
- [ ] `ListCommands` 可用
- [ ] `ExecuteCommand` 可用
- [ ] `CancelCommand` 可用
- [ ] `GetSettingsSchema` 可用
- [ ] `ValidateSettings` 可用
- [ ] `SubscribeEvents` 可用
- [ ] `TailLogs` 可用
- [ ] `Dispose` 可用

## Consistency

- [ ] settings revision 冲突可检测
- [ ] command invocationId 幂等
- [ ] event seq 单调递增
- [ ] event stream 支持 lastEventSeq resume
- [ ] 长任务支持 progress 和 cancel

## UI Surface

- [ ] DashboardCard
- [ ] DetailPage
- [ ] SettingsSurface
- [ ] CommandProvider
- [ ] LogsProvider

## Security

- [ ] secret 不写入 module.json
- [ ] 高权限动作声明 permission
- [ ] 高权限动作通过 Broker
- [ ] 日志脱敏
- [ ] package hash 生成
