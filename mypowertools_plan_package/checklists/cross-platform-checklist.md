# 跨平台检查表

## 通用

- [ ] 不写死 Windows 路径
- [ ] 路径使用 platform path service
- [ ] 日志路径使用 Host 提供的 module log path
- [ ] secret 使用 SecretBroker
- [ ] 服务托管使用 ServiceBroker
- [ ] 自启动使用 AutostartBroker
- [ ] 系统通知使用 NotificationService
- [ ] module.json 提供平台 entrypoint
- [ ] TransportSelector 能在当前平台选择最佳通道

## IPC

- [ ] Windows gRPC IPC 使用 Named Pipes
- [ ] macOS gRPC IPC 使用 Unix Domain Socket
- [ ] Linux gRPC IPC 使用 Unix Domain Socket
- [ ] HTTP 只用于已有服务、远程服务或调试接口
- [ ] stdio 只作为 fallback

## Windows

- [ ] 普通权限启动
- [ ] UAC 动作走 Broker
- [ ] Windows 通知可用
- [ ] Task Scheduler 或 Service provider 可用
- [ ] DPAPI 或 Credential Manager 可用
- [ ] Named Pipe endpoint 命名稳定

## macOS

- [ ] launchd agent/provider 预留
- [ ] UserNotifications provider 预留
- [ ] Keychain provider 预留
- [ ] UDS path 使用 runtime directory
- [ ] privileged helper 预留

## Linux

- [ ] systemd user provider 预留
- [ ] freedesktop notification provider 预留
- [ ] Secret Service provider 预留
- [ ] UDS path 使用 XDG_RUNTIME_DIR
- [ ] polkit provider 预留
- [ ] Wayland/X11 差异有降级状态
