# Web Surface and Web Bridge

A web surface may load an existing HTTP URL or `web/index.html` from the tool directory. `openExternal` enables an explicit browser action. `allowedOrigins` limits bridge access.

Install the browser API:

```powershell
npm install C:\src\MyPowerTools\artifacts\sdk\npm\mypowertools-web-bridge-0.2.0.tgz
```

```ts
import { mpt } from '@mypowertools/web-bridge';
const health = await mpt.commands.invoke('example.health');
await mpt.events.publish('example.updated', { value: 1 });
```

`MyPowerTools.WebToolHost.exe` owns WebView2 and supplies the process fault boundary. Loading displays a progress state. Navigation or process failure displays a recovery page with **Try again** and **Open externally**. Refresh recreates the navigation. A remote panel can remain on Linux; Windows only stores its URL, timeout, refresh preference, and secret reference.
