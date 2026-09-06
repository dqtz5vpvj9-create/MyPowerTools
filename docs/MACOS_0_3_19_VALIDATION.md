# macOS 0.3.19 local validation

Validated on Apple Silicon macOS on 2026-09-06 using the full installed application bundle.

## Corrected failures

- Opening a .NET tool Surface constructed Avalonia controls on a worker thread. Remote Notifications then aborted the whole Shell with a dispatcher ownership exception. Surface factories now run on the UI dispatcher, and the loader rejects background callers before constructing controls.
- macOS seeded a notifications-only module allowlist. New installations now enable all supported installed modules unless an explicit restriction is supplied. Existing user module preferences remain preserved; an existing notifications-only personal installation must enable its other modules in its saved policy.
- The Android Tools package advertised Remote Commands but omitted its Surface assembly. The macOS publisher now includes it and checks every declared .NET route before packaging.
- ADB Forwarder, ScreenEase and Doubao Controller services were omitted. Their portable executables and macOS paths now ship alongside the notification service. Service directories avoid the `.service` suffix, which macOS interprets as a service bundle.
- ScreenEase's sequential named-pipe accept loop dropped queued Unix clients when the last listener closed. The service keeps a replacement listener open while serving connected clients and serializes command execution to preserve settings revision ordering.

## Evidence

- Reproduced the original notification page crash from the installed app; opening the corrected page preserves and displays the existing inbox without aborting.
- Opened all seven visible tool workspaces during local diagnosis. Remote Commands loads its editor after packaging repair. ScreenEase loads profiles, brightness and reminder controls and reports connected after the pipe repair.
- ADB reports its missing device configuration rather than a missing Service Unit. Paste Image loads its upload workspace. Doubao and SmartBird load their configuration/offline workspaces.
- PersonalUx test suite: 62 passed, including a background Surface ownership regression.
- ScreenEase installed service: 120 concurrent read-only IPC calls passed with 12 clients. The committed isolated regression also passes 120/120 using a unique pipe, temporary data and logical-only display mode.
- All four Service Unit executable paths and all declared .NET Surface assemblies resolve in the package. Complete app passes `codesign --verify --deep --strict` after installation.

## Remaining configuration requirements

Device forwarding requires configured devices. Doubao task execution requires its model keys and computer-use runtime; packaging its controller does not supply those. SmartBird requires a reachable thermostat backend. Those external integrations were not claimed as end-to-end verified. No remote commands, uploads or model tasks were executed against user infrastructure during these checks.

## Reproduce

Build with `scripts/publish-macos.ps1 -Architecture arm64 -Configuration Release -Version 0.3.19 -Channel local` on macOS.
Run `dotnet test tests/PersonalUx.Tests/PersonalUx.Tests.csproj -c Release -p:BuildWindowsWebToolHost=false`.
Run `python3 tools/screenease/tests/verify_concurrent_pipe.py <full-path-to-published-ScreenEase.Service>` for the isolated IPC regression.
Start the outer `MyPowerTools.app` launcher so ServiceManager, services and Runner are initialized; do not validate by launching a bare Shell alone.
