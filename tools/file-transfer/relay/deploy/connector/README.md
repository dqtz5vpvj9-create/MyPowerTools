# Public relay to Tail relay connector

This connector supplies one fixed TCP destination without adding host routes, changing ACLs, enabling the default tailscaled, or interrupting the public relay. It uses the installed Tailscale CLI/daemon and systemd socket activation; there is no custom forwarding daemon.

## Installed layout

- `mpt-tail-connector.service`: dedicated `mpt-tail-connector` user, userspace networking, persistent `/var/lib/mpt-tail-connector/tailscaled.state`, private `/run/mpt-tail-connector/tailscaled.sock`.
- `mpt-tail-connect.socket`: only `127.0.0.1:18766`, maximum 128 simultaneous connections.
- `mpt-tail-connect@.service`: each accepted connection executes `tailscale --socket=/run/mpt-tail-connector/tailscaled.sock nc 100.64.0.1 80`.

`sudo sh install.sh` installs and enables these units. Registration is a separate administrator operation. Create a short-lived, single-use Headscale key carrying the existing `tag:mobile`, save it in a mode-0600 file owned by the connector user, and pass `--auth-key=file:<path>` to `tailscale up` through the dedicated socket. Use `--login-server=https://proxy.lixinrui000.cn --hostname=mpt-relay-connector --accept-dns=false --accept-routes=false --advertise-tags=`. The key supplies the tag; requesting it again through advertise-tags was rejected by this Headscale policy. Remove the key file immediately after registration. Never copy user-pasted credentials or print keys/state.

The existing `tag:mobile` grant permits `100.64.0.1:80`. No new grant is required. `tag:ow-service` does not have this outbound permission. The dedicated node does not advertise routes or accept DNS/routes. The tagged node identity is persistent; restart does not require another key.

## Python integration

Set the controlled upstream origin to `http://127.0.0.1:18766`. Every upstream request MUST explicitly send `Host: mpt-relay.tail.lixinrui000.cn`, because the fixed destination is nginx's shared port 80. Merely setting urllib opener `addheaders` is insufficient: urllib constructs its own Host first. Set Host on the individual `urllib.request.Request`.

Keep the original shared-conversation Authorization header and fixed `/mpt/relay/` path protocol. Do not expose this connector as a public arbitrary proxy. It only supplies network connectivity; the application's authentication, allowed-path rules, timeouts and streaming implementation remain mandatory. The public feature must stay disabled until its code has passed review and authenticated streaming tests.

## Acceptance and recovery

`sudo python3 smoke.py` uses the deployed `/opt/mpt-relay/deploy/smoke.py` protocol checker, creates a throwaway namespace (never prints its key), performs 1 MiB DAV roundtrip and wrong-key/namespace isolation, and verifies long-poll wakeup. It restarts only the new connector service/socket, reads the saved manifest and writes another file. It leaves the disposable test namespace, consistent with the existing smoke checker. Do not run it as a read-only diagnostic.

On proxy, 2026-09-29: node `mpt-relay-connector`, `100.64.0.9`, `tag:mobile`; all checks passed. Both units enabled/active. Public `mpt-relay.service` stayed active. Default `tailscaled` stayed inactive; `ip route get 100.64.0.1` still uses the pre-existing eth0 default route, proving this connector did not add a host route. Loopback listener confirmed by ss. Restart acceptance covers the service, not an actual host reboot.

To withdraw only this connector, disable and stop `mpt-tail-connect.socket`, then stop its active per-connection units and disable/stop `mpt-tail-connector.service`. Retain state and deployed unit files for recovery. Re-enable/start the two top-level units to restore. Do not delete the Headscale node or state unless permanent removal is explicitly intended; do not touch the public relay, default tailscaled, global ACL or routes.
