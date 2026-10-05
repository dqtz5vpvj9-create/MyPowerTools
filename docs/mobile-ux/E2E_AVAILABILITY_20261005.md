# Android 真机 e2e 验证记录

框架：[tester-army/e2e](https://github.com/tester-army/e2e)。固定版本：e2e 0.16.0、@e2e-dev/mobile 0.9.2、agent-device 0.21.20。
手机为已发布的 0.2.9/code11（源码43ad044），对端由当前cb7fab8源码构建；新合入的Surface修复尚未打进手机APK。本轮不能作为当前全部源码的Android验收。

## 实际结果

**完整手机用例有效通过数为0；两次主要运行受到其他设备操作干扰，不能用于产品判断。高可用性验收未完成。**

- 10.33.0.155:5555：启动MPT并读取真实可访问性树；运行时前台切到com.readin.app。失败截图与控件树均为阅读应用。
- 10.33.0.156:5555：安装同一APK后，运行时前台切到com.readinlab.readify。还出现snapshot helper解析错误，不将驱动故障归因于MPT。
- 初版断言误以为首页直接进入输入框。实际先进入会话列表，已修正为“首页→会话列表→文件传输助手”。
- 首页按钮可访问性边界y=405..537，截图中的按钮向下偏约136像素。实际位置点击能打开会话列表；需独占设备复验其对自动化与辅助操作的影响。
- 隔离生产对端构建成功，assistant.inspect返回身份和消息数组；e2e list发现9个用例。这些属于设施验证，不计作手机传输通过。

原始报告保留在忽略提交的.e2e/mobile-availability、.e2e/mobile-availability-spare-smoke、.e2e/mobile-availability-semantic目录。包含截图、控件树、JUnit和JSON结果。初版错误断言及受干扰记录均不计作有效产品失败。原始材料可能含设备通知或连接预览，不提交GitHub。

框架记忆的bundle id不能证明前台身份。新脚本另外读取Android Activity状态，发现外部应用抢占时立即停止当前操作。

## 运行方式

使用不被其他自动化抢占的专用手机。完整传输套件会确认加入新的隔离会话，不能在日用手机上运行；不清除手机数据，不操作Windows桌面。

```sh
npm ci --ignore-scripts
/home/chris/.dotnet/dotnet build tests/e2e/MobilePeer/MobilePeer.csproj
MPT_E2E_SERIAL=10.33.0.156:5555 E2E_TELEMETRY_DISABLED=1 \
  TMPDIR=/mnt/cache/data-cache npm run test:mobile
```

MPT_DOTNET可覆盖dotnet路径。对端使用stdin命令管道，空闲时不轮询；每次创建独立临时数据目录，使用生产模块和正常中转协议。连接Intent中的凭据不进入框架操作日志；原始报告仍只留本地。

九个用例覆盖：语义点击进入聊天、确认连接并接收回执、手机发送且不重复、草稿重启、文件选择器取消、五轮后台接收和恢复、同名文件内容独立、删除测试缓存后原消息重新下载、应用IPv4断网后的自动恢复。

MPT_E2E_TOUCH_OFFSET_Y=136只用于诊断定位偏移；首页语义点击始终不补偿。补偿下通过不能说明定位问题已修复。

断网用例要求专用root测试机及MPT_E2E_NETWORK_FAULTS=1，只阻断应用UID的IPv4出站，并在finally恢复唯一命名的规则。不关闭TCP ADB使用的Wi-Fi。未启用则明确报未验证，不能计作通过；不代表IPv6故障覆盖。

## 未完成的验收

九个用例需要独占真机实跑。系统保存副本时扩展名前的重名后缀、接收失败文案、传输中途取消或进程死亡、大文件、真实夸克/百度授权失效及配额、私聊与共享隔离、锁屏省电模式、至少120秒Android闲置CPU，仍需独立证据。旧测试和对端测试不能替代这些手机场景。
