#!/bin/sh
# Install only the isolated connector; does not register a node or touch the public relay.
set -eu
[ "$(id -u)" = 0 ] || { echo 'Run as root' >&2; exit 1; }
SOURCE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
command -v tailscaled >/dev/null
command -v tailscale >/dev/null
if ! id -u mpt-tail-connector >/dev/null 2>&1; then
    useradd --system --user-group --home-dir /var/lib/mpt-tail-connector --shell /usr/sbin/nologin mpt-tail-connector
fi
for name in mpt-tail-connector.service mpt-tail-connect.socket mpt-tail-connect@.service; do
    install -m 0644 "$SOURCE_DIR/$name" "/etc/systemd/system/$name"
done
systemctl daemon-reload
systemctl enable --now mpt-tail-connector.service mpt-tail-connect.socket
