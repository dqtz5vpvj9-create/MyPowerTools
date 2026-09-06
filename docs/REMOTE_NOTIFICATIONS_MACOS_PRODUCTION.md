# Remote Notifications on macOS

## Runtime ownership

Remote Notifications uses one long-running Service Unit:

```text
MyPowerTools ServiceManager
  └─ MyPowerTools Remote Notifications.app
       └─ RemoteNotifications.Service
            ├─ signed HTTP pull
            ├─ deduplication
            ├─ persisted inbox
            ├─ notification authorization
            └─ Notification Center delivery
```

The Shell Surface renders the persisted inbox and observes the supervised service. It does not run a second network polling loop.

## Installed paths

The default installation uses:

```text
~/Applications/MyPowerTools.app
~/Library/Application Support/MyPowerTools/
~/Library/LaunchAgents/com.mypowertools.runner.plist
~/Library/LaunchAgents/com.mypowertools.servicemanager.plist
```

Remote Notifications state is shared by the worker and Surface:

```text
~/Library/Application Support/MyPowerTools/state/tools/remote-notifications/
├── settings.json
├── history.json
├── notification-delivery.json
├── remote-notifications.service.lock
└── seen-message-ids.json
```

The control socket is created beneath the current macOS temporary directory:

```text
$TMPDIR/mypowertools/remote-notifications.core.sock
```

The socket, lock and notification diagnostic files are owner-only. The state directory is created with user-only access.

## Configuration

The Settings page controls:

- `http` or `https`
- notification server host and port
- channel
- polling interval
- OpenSSH private-key path

The signing key must be an unencrypted OpenSSH Ed25519 private key. Only the path is stored in Remote Notifications settings.

The default key path is:

```text
~/.ssh/id_ed25519
```

The connection test verifies endpoint construction, private-key availability, signing and the pull protocol before the configuration is accepted.

## Notification permission

The worker posts through `UNUserNotificationCenter` under this helper bundle identifier:

```text
com.mypowertools.remote-notifications
```

The service distinguishes these states:

| State | Meaning |
|---|---|
| `ready` | Notification Center is available and authorization is resolved. |
| `permission-not-requested` | macOS has not completed the first authorization decision. |
| `permission-denied` | Notifications are disabled for the helper in System Settings. |
| `delivered` | Notification Center accepted the latest request. |
| `delivery-failed` | The latest message was stored, but native banner submission failed. |
| `unavailable` | The helper identity or native notification bridge is unavailable. |

The latest delivery state is written to `notification-delivery.json` and also appears in module health. A successful server pull therefore cannot conceal a failed desktop notification.

To review or change permission:

1. Open **System Settings**.
2. Open **Notifications**.
3. Select **MyPowerTools Remote Notifications**.
4. Enable **Allow notifications**.

## User interface

At ordinary desktop widths, project and session filters remain in one horizontal strip. Trackpad horizontal movement, pointer dragging and mouse-wheel translation move the strip left and right.

At compact widths, secondary header actions move into one overflow menu. Search, Settings, Claude Task and Clear all remain available without creating a vertical wall of controls.

## Service diagnosis

### launchd hosts

```bash
uid="$(id -u)"
launchctl print "gui/$uid/com.mypowertools.runner"
launchctl print "gui/$uid/com.mypowertools.servicemanager"
```

### Remote Notifications worker

```bash
ps -ww -axo pid=,command= | grep 'MyPowerTools Remote Notifications.app'
```

The command must point into:

```text
MyPowerTools.app/Contents/MacOS/Helpers/
MyPowerTools Remote Notifications.app/Contents/MacOS/
RemoteNotifications.Service
```

### Socket and permissions

```bash
socket="$TMPDIR/mypowertools/remote-notifications.core.sock"
stat -f '%Sp %Lp %N' "$socket"
```

Expected numeric mode:

```text
600
```

### Logs

The launchd host logs are stored beneath:

```text
~/Library/Logs/MyPowerTools/
```

The Service Unit state exposes:

- current process ID
- last poll timestamp
- last poll result
- fetched message count
- displayed notification count
- notification authorization
- notification delivery state
- latest delivery error

## Upgrade behavior

Installation and OTA replacement stop every process executing from the old application bundle before moving or replacing it. This includes Service Units that deliberately survive an ordinary ServiceManager restart for process re-adoption.

The replacement preserves:

- application signatures
- executable permissions
- symbolic links
- extended attributes
- Remote Notifications data and seen-message history

A failed OTA restores the previous application bundle and relaunches the background hosts.

## Production acceptance

The automated macOS gates cover:

- Apple Silicon execution
- Intel cross-publication
- helper bundle identity and code signature
- shared native notification symbols
- signed HTTP polling with a generated Ed25519 key
- persisted history and duplicate suppression
- exact-message activation URI generation
- ServiceManager restart and worker re-adoption
- worker crash recovery
- owner-only socket and lock permissions
- replacement installation while an old-bundle process is running
- launchd autostart
- compact and horizontal Surface layouts using Avalonia's layout engine

Release signing still requires the configured Developer ID identity and Apple notarization. The release checklist also includes one interactive Mac pass for the initial permission prompt, a visible Notification Center banner and clicking that banner to open the exact stored message.
