# Remote Notifications deployment control

This directory deploys versioned server artifacts from
`MyPowerTools-remote-notifications` to the production host.

The server source remains in its product repository. Every deployment records
the product Git SHA and artifact SHA-256 in `versions.json`.

Example:

```bash
python ops/remote-notifications/deploy.py \
  --target proxy.lixinrui000.cn \
  --confirm-target proxy.lixinrui000.cn \
  --artifact remote-notifications-server.tar.gz \
  --version 0.3.0-session-metadata \
  --git-sha <full-sha>
```

Use `--dry-run` to print the validated deployment without changing the target.

Production currently runs from:

```text
/opt/remote-notifications/current
  -> /opt/remote-notifications/releases/0.3.0-session-metadata-4afb7675373b
```

Bootstrap files under `systemd/` make the notification service use the
versioned `current` link. The certificate renewal hook restarts `ntfy.service`
after Let's Encrypt updates the certificate used by UnifiedPush on port 8889.

The first production rollout was executed from the Windows operator host
because `r743-autodroid` currently cannot establish a direct TCP/22 connection
to `proxy.lixinrui000.cn`. Keep using a reviewed operator hop until that route
or an explicit SSH jump host is configured.
