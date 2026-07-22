# Web Surface and Web Bridge

A web surface may load an existing HTTP URL or `web/index.html` from the tool directory. `openExternal` enables an explicit browser action. `allowedOrigins` limits navigation, resource requests, and bridge messages. The host always includes the exact origin of `source`; an empty list therefore allows only that origin.

Install the browser API:

```powershell
npm install C:\src\MyPowerTools\artifacts\sdk\npm\mypowertools-web-bridge-0.2.0.tgz
```

```ts
import { mpt } from '@mypowertools/web-bridge';
const health = await mpt.commands.invoke('example.health');
await mpt.events.publish('example.updated', { value: 1 });
```

`MyPowerTools.WebToolHost.exe` owns WebView2 and supplies the process fault boundary. The Shell owns the client protocol, native child-window positioning, overlay occlusion, focus forwarding, restart, and disposal. Manifest web tools only declare `source`, `allowedOrigins`, and `openExternal`; they do not locate or launch WebToolHost.

Loading displays a progress state. Navigation or process failure displays a recovery page with **Try again** and **Open externally**. Refresh reloads the active session. Command failures remain inside the command result and do not replace a ready web surface. A remote panel can remain on Linux; Windows only stores its URL, timeout, refresh preference, and secret reference.

Dotnet surfaces with a custom surrounding UI can request the same host capability through `MptAvaloniaSurfaceContext.WebSurfaces`:

```csharp
using var session = context.WebSurfaces?.CreateSession(new MptWebSurfaceRequest(
    context.ToolId,
    context.RouteId,
    new Uri("http://127.0.0.1:43110/"),
    []));

var embeddedView = session?.View;
```

The capability is optional for compatibility with older hosts. A tool should expose its system-browser action when `WebSurfaces` is `null`. Tool assemblies must not contain WebToolHost process code, protocol copies, or Shell overlay coordination.

## Quick web panels (single-file tools)

A web-surface tool can be declared with a single file containing as little as a title and a URL. Drop the file into the default drop folder — `%LOCALAPPDATA%\MyPowerTools\custom-tools\` on Windows, `~/.local/share/MyPowerTools/custom-tools` elsewhere (or any directory registered through `MPT_TOOL_DIRS`, `--tool-dir`, or `settings/tool-directories.json`):

```jsonc
// custom-tools/grafana.mpt.json
{ "title": "Grafana", "url": "http://192.168.1.42:3000/" }
```

The Runner picks the file up without a restart (a file watcher refreshes the tool catalog), the Home/Tools pages show a card in the **Custom panels** category, and clicking it embeds the page through the same WebToolHost pipeline as any web-surface tool. Deleting the file removes the card; a malformed file shows an "Unavailable" card with the error instead of breaking other tools.

This is **not a separate mechanism**: the file is normalized into a regular web-surface `tool.json` manifest in memory (your file is never rewritten). Every omitted field below is the default of a real manifest field — write the real field back to override it and restore the full web-surface capability.

| Field | Default | Override by writing |
|---|---|---|
| `toolId` | `custom.<file-name-stem>` | `"toolId": "my.panel"` |
| `title` | humanized file name | `"title": "..."` |
| `description` | `Quick panel for <url>` | `"description": "..."` |
| `icon` | `tool.external` | `"icon": "..."` |
| `category` | `Custom panels` | `"category": "Monitoring"` |
| `type` | `web-surface` | — (quick path is always web) |
| `routes[0].surface.source` | the `url` value | a full `"routes"` array (taken over wholesale) |
| `routes[0].surface.openExternal` | `true` | full `"routes"` array |
| `routes[0].surface.allowedOrigins` | `[]` (host auto-allows the source origin) | full `"routes"` array |
| `homeCard` | `{ "summary": "Open <title>", "primaryActionLabel": "Open", "order": 500 }` | `"homeCard": { ... }` (merged field-by-field) |
| `commands` / `runtime` / `settings` | none | add the real blocks — the bridge (`mpt.invoke`, settings, secrets) then works exactly as for packaged tools |

Growth path: a panel that outgrows the single file can declare `commands`, `runtime`, and `settings` in place, or move into a folder with a full `tool.json` and ship with `mypowertools pack tool .` — no rewrite of the original content is needed. A `tool.json` (in a tool directory) written in the minimal `{ "title", "url" }` shape is normalized the same way.

The `url` must be an absolute `http(s)` URL. Any other manifest field present in the file is passed through unchanged; `url` itself is consumed into `routes[0].surface.source` and does not appear in the normalized manifest.
