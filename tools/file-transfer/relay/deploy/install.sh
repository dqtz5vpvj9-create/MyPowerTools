#!/bin/sh
# Install (or upgrade) the MyPowerTools file relay on the proxy host.
#
#   sudo sh deploy/install.sh              # stage code, unit and env file; do not start
#   sudo sh deploy/install.sh --enable     # ...then enable, start and VERIFY active + health
#   sudo sh deploy/install.sh --uninstall  # stop, disable and remove the unit from the way
#
# What it does NOT do: it never edits nginx, never touches Headscale, never opens a firewall
# port and never deletes user data. The relay listens on 127.0.0.1 only; publishing it is an
# explicit nginx change made by the operator (see deploy/nginx-mpt-relay.conf).
#
# This script performs no deletions at all. An upgrade moves the previous code tree aside
# (a timestamped rollback copy) before moving the staged tree into place, so a mistake is
# recoverable and no literal path is ever removed by a wildcard. /var/lib/mpt-relay
# (credentials, revisions, user files) and /etc/mpt-relay are never in that tree.

set -eu

SERVICE_NAME=mpt-relay
SERVICE_USER=mpt-relay
SERVICE_GROUP=mpt-relay
CODE_DIR=/opt/mpt-relay
DATA_DIR=/var/lib/mpt-relay
ENV_DIR=/etc/mpt-relay
ENV_FILE=$ENV_DIR/relay.env
UNIT=/etc/systemd/system/mpt-relay.service
HOST=127.0.0.1
PORT=18765
BASE_PATH=/mpt/relay
SOURCE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)

ENABLE=0
UNINSTALL=0
for argument in "$@"; do
    case "$argument" in
        --enable) ENABLE=1 ;;
        --uninstall) UNINSTALL=1 ;;
        -h|--help) sed -n '2,18p' "$0"; exit 0 ;;
        *) echo "unknown argument: $argument" >&2; exit 2 ;;
    esac
done

if [ "$(id -u)" -ne 0 ]; then
    echo "run as root (sudo sh deploy/install.sh)" >&2
    exit 1
fi

command -v python3 >/dev/null 2>&1 || { echo "python3 is required" >&2; exit 1; }
python3 - <<'PY'
import sys
if sys.version_info < (3, 10):
    raise SystemExit(f"python3 >= 3.10 is required, found {sys.version.split()[0]}")
PY

have_systemctl() { command -v systemctl >/dev/null 2>&1; }

unit_is_active() {
    have_systemctl && systemctl is-active --quiet "$SERVICE_NAME"
}

stop_service() {
    if unit_is_active; then
        systemctl stop "$SERVICE_NAME"
    fi
}

# ---------------------------------------------------------------------------- uninstall
if [ "$UNINSTALL" -eq 1 ]; then
    stop_service
    if have_systemctl && [ -f "$UNIT" ]; then
        # Only called when the unit is actually installed, so a failure is a real failure.
        systemctl disable "$SERVICE_NAME"
    fi
    if [ -f "$UNIT" ]; then
        # Moved aside rather than deleted: systemd only loads *.service, so this is inert,
        # and the operator can restore or remove it deliberately.
        mv "$UNIT" "$UNIT.removed-$(date +%Y%m%d%H%M%S)"
    fi
    if have_systemctl; then
        systemctl daemon-reload
    fi
    if [ -d "$CODE_DIR" ]; then
        mv "$CODE_DIR" "$CODE_DIR.removed-$(date +%Y%m%d%H%M%S)"
    fi
    echo "unit and code moved aside; nothing was deleted."
    echo "kept on purpose: $DATA_DIR (credentials, revisions, user files) and $ENV_DIR."
    exit 0
fi

# ------------------------------------------------------------------------- user and dirs
if ! getent group "$SERVICE_GROUP" >/dev/null 2>&1; then
    groupadd --system "$SERVICE_GROUP"
fi
if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --gid "$SERVICE_GROUP" --home-dir "$CODE_DIR" --shell /usr/sbin/nologin "$SERVICE_USER"
fi
install -d -m 0755 -o root -g root "$CODE_DIR"
install -d -m 0750 -o "$SERVICE_USER" -g "$SERVICE_GROUP" "$DATA_DIR"
install -d -m 0755 -o root -g root "$ENV_DIR"

# ------------------------------------------------------------------------------ staging
STAMP=$(date +%Y%m%d%H%M%S)
STAGE=$CODE_DIR/.stage-$STAMP
install -d -m 0755 "$CODE_DIR/rollback"
install -d -m 0755 "$STAGE/mpt_relay" "$STAGE/deploy"
cp -R "$SOURCE_DIR/mpt_relay/." "$STAGE/mpt_relay/"
cp -R "$SOURCE_DIR/deploy/." "$STAGE/deploy/"
cp "$SOURCE_DIR/README.md" "$STAGE/README.md"
# Bytecode caches are never installed; the unit sets PYTHONDONTWRITEBYTECODE=1 anyway.
# They are moved aside (not deleted) so the staged tree is exactly the source tree.
for cache in "$STAGE/mpt_relay/__pycache__" "$STAGE/deploy/__pycache__"; do
    if [ -d "$cache" ]; then
        mv "$cache" "$CODE_DIR/rollback/discarded-pycache-$STAMP-$(basename "$(dirname "$cache")")"
    fi
done
chown -R root:root "$STAGE"
# Source checkouts can have a private umask; the unprivileged service must read code.
chmod -R u=rwX,go=rX "$STAGE"

# --------------------------------------------------------------------------- swap in
# Stop first so the next start definitely loads the new tree from the fixed paths.
stop_service
for fixed in mpt_relay deploy; do
    if [ -e "$CODE_DIR/$fixed" ]; then
        mv "$CODE_DIR/$fixed" "$CODE_DIR/rollback/$fixed-$STAMP"
    fi
done
mv "$STAGE/mpt_relay" "$CODE_DIR/mpt_relay"
mv "$STAGE/deploy" "$CODE_DIR/deploy"
mv "$STAGE/README.md" "$CODE_DIR/README.md"
rmdir "$STAGE"

if [ ! -f "$ENV_FILE" ]; then
    install -m 0640 -o root -g "$SERVICE_GROUP" "$SOURCE_DIR/deploy/relay.env.example" "$ENV_FILE"
    echo "wrote $ENV_FILE (defaults)"
fi
install -m 0644 -o root -g root "$SOURCE_DIR/deploy/mpt-relay.service" "$UNIT"
have_systemctl && systemctl daemon-reload

echo "code:  $CODE_DIR (previous tree kept at $CODE_DIR/rollback for rollback)"
echo "env:   $ENV_FILE"
echo "data:  $DATA_DIR (untouched)"

# ------------------------------------------------------------------------------ enable
if [ "$ENABLE" -eq 0 ]; then
    echo "installed but not started. Start it with:  systemctl enable --now $SERVICE_NAME"
    echo "nginx: add deploy/nginx-mpt-relay.conf to the existing server{} for the public host."
    exit 0
fi

if ! have_systemctl; then
    echo "--enable requires systemd (systemctl not found)" >&2
    exit 1
fi

systemctl enable "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"

# 1) the unit must actually be active; a failed start is a failed install.
ACTIVE=0
for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
    if unit_is_active; then ACTIVE=1; break; fi
    sleep 0.5
done
if [ "$ACTIVE" -ne 1 ]; then
    echo "$SERVICE_NAME did not become active" >&2
    systemctl --no-pager --full status "$SERVICE_NAME" >&2 || true
    journalctl -u "$SERVICE_NAME" -n 50 --no-pager >&2 || true
    exit 1
fi

# 2) the service must answer its own health endpoint on loopback.
if ! python3 - "$HOST" "$PORT" "$BASE_PATH" <<'PY'
import json, sys, time, urllib.error, urllib.request

host, port, base = sys.argv[1], sys.argv[2], sys.argv[3]
url = f"http://{host}:{port}{base}/health"
deadline = time.time() + 15
last = "no attempt"
while time.time() < deadline:
    try:
        with urllib.request.urlopen(url, timeout=2) as response:
            payload = json.loads(response.read().decode("utf-8"))
        if payload.get("ok") is True:
            print(f"health ok: {payload}")
            raise SystemExit(0)
        last = payload
    except Exception as error:  # noqa: BLE001 - the operator wants the real cause
        last = f"{type(error).__name__}: {error}"
    time.sleep(0.5)
print(f"health check failed for {url}: {last}", file=sys.stderr)
raise SystemExit(1)
PY
then
    echo "$SERVICE_NAME is active but its health endpoint failed" >&2
    journalctl -u "$SERVICE_NAME" -n 50 --no-pager >&2 || true
    exit 1
fi

echo
echo "$SERVICE_NAME is active and healthy."
echo "nginx: add deploy/nginx-mpt-relay.conf to the existing server{} for the public host, then:"
echo "  python3 $CODE_DIR/deploy/smoke.py --base https://<public-host>"
