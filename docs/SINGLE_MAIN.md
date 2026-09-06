# Personal development: one main

Continue all product development on `main`. The September 6, 2026 consolidation includes the shortcut center, personal UX, macOS OTA/installation safety, production notifications, reply-first notification fixes, configurable Remote Commands and saved SSH hosts, explicit history actions, filtered notification export/batch-read, and reminder presets. Tool repositories remain pinned submodules; the three tool repositories changed during consolidation publish their integrated commits on `main`.

Older feature branch tips are retained as `archive/2026-09-06/<original-branch>` tags. Publication/review workspace branches contained intermediate patch payloads, not additional runtime features; they are archived instead of installing their obsolete publishing workflows. Existing release tags remain unchanged.

Local uncommitted macOS changes were saved before consolidation. Applicable launch, native identity/icon, font, titlebar, readiness-address and local service error changes were ported to the current architecture. Old flat-bundle renaming and the former notification HTML view were superseded by the current helper bundles and native WebView implementation.

## Build on Apple Silicon

```sh
git switch main
git pull --ff-only
git submodule update --init -- tools/adb-forwarder tools/doubao-computer-use tools/input-monitor tools/process-monitor tools/remote-commands tools/remote-notifications tools/screenease tools/smartbird-thermostat
pwsh -NoProfile -File scripts/build-sdk.ps1 -Configuration Release
pwsh -NoProfile -File scripts/publish-macos.ps1 -Architecture arm64 -Configuration Release -Channel local
```

The signed personal-use application is `artifacts/publish/macos-arm64/MyPowerTools.app`. The publisher also creates an archive and local OTA feed. Default signing uses the Mac's ad-hoc identity; no Developer ID certificate is required for this personal build.

Run builds and .NET tests sequentially: they share intermediate directories and differing debug-symbol settings.

```sh
dotnet test tests/ShortcutCenter.Tests/ShortcutCenter.Tests.csproj -c Release -p:BuildWindowsWebToolHost=false
dotnet test tests/PersonalUx.Tests/PersonalUx.Tests.csproj -c Release -p:BuildWindowsWebToolHost=false
node --test tests/ShortcutCenter.Tests/web-shortcuts.test.cjs
pwsh -NoProfile -File scripts/verify-macos-install-safety.ps1
pwsh -NoProfile -File scripts/verify-remote-notifications-macos-production.ps1
```
