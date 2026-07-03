# WebView Module 模板

用于复杂工具页面。模块仍必须通过标准协议提供 status、commands、settings、logs。WebView 只承载复杂业务页面。

## 约束

```text
不能绕过 MyPowerTools permission model
不能直接访问 secret 明文
不能直接写 Host 设置
必须支持主题 token
必须提供加载失败页面
必须提供超时机制
```

## Surface 示例

```json
{
  "id": "sample.webview.detail",
  "type": "webview",
  "title": "Sample WebView",
  "url": "http://127.0.0.1:41000/index.html",
  "requiresModuleRuntime": true
}
```
