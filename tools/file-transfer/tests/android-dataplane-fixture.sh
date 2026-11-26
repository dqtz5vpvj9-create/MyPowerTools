#!/usr/bin/env bash
# MyPowerTools file-transfer · Android dataplane fixture (fixture only, no production code paths).
#
# Starts an isolated official OpenList (Local driver) on 127.0.0.1 and prepares:
#   - a Local mount over  <root>/storage
#   - one dedicated relay account (throwaway local password, never a real credential)
#   - one inbound ready transfer for the phone to download
#   - the exact webdav URL + cloud/pair connection codes for the Android app
# Then prints <root>/handover.txt. The phone reaches the fixture through
#   adb -s <serial> reverse tcp:<port> tcp:<port>
# so no Tailscale account, no cloud credentials and no LAN exposure are involved.
#
# Usage:
#   bash tools/file-transfer/tests/android-dataplane-fixture.sh start     # start + prepare + print handover
#   bash tools/file-transfer/tests/android-dataplane-fixture.sh status
#   bash tools/file-transfer/tests/android-dataplane-fixture.sh stop [--purge]
#
# Environment (all optional except the binary path when it is not the default):
#   MPT_OPENLIST_TEST_BINARY  official openlist v4.2.6 binary
#   MPT_FIXTURE_ROOT          fixture state dir (default under /mnt/cache/data-cache)
#   MPT_FIXTURE_PORT          loopback port (default 15244; refuses a busy port)
#   DOTNET                    dotnet host (default /home/chris/.dotnet/dotnet)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
BINARY="${MPT_OPENLIST_TEST_BINARY:-/mnt/cache/data-cache/mpt-file-transfer/openlist-official/openlist}"
FIXTURE_ROOT="${MPT_FIXTURE_ROOT:-/mnt/cache/data-cache/mpt-file-transfer/android-fixture}"
PORT="${MPT_FIXTURE_PORT:-15244}"
DOTNET="${DOTNET:-/home/chris/.dotnet/dotnet}"
TESTS="$REPO_ROOT/tools/file-transfer/tests/FileTransfer.Core.Tests/FileTransfer.Core.Tests.csproj"
SERVER_DIR="$FIXTURE_ROOT/server"
EXE="$SERVER_DIR/v4.2.6/openlist"
DATA_DIR="$SERVER_DIR/data"
PID_FILE="$FIXTURE_ROOT/fixture.pid"
LOG_FILE="$FIXTURE_ROOT/server.log"
ADMIN_FILE="$FIXTURE_ROOT/admin-password.txt"

die() { printf 'fixture: %s\n' "$*" >&2; exit 1; }
note() { printf 'fixture: %s\n' "$*"; }

# Dependency-free HTTP status probe over bash /dev/tcp.
http_status() {
    local host="$1" port="$2" path="$3" line protocol status remainder
    exec 3<>"/dev/tcp/$host/$port" || return 1
    printf 'GET %s HTTP/1.0\r\nHost: %s\r\nConnection: close\r\n\r\n' "$path" "$host" >&3
    IFS= read -r -t 3 line <&3 || line=""
    exec 3>&- 3<&- || true
    read -r protocol status remainder <<< "$line"
    printf '%s' "$status"
}

port_busy() {
    (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null || return 1
    return 0
}

fixture_pid() {
    [ -f "$PID_FILE" ] || return 1
    local pid
    pid="$(cat "$PID_FILE")"
    [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null || return 1
    tr '\0' ' ' <"/proc/$pid/cmdline" 2>/dev/null | grep -q "$SERVER_DIR" || return 1
    printf '%s' "$pid"
}

start_server() {
    local pid status=""
    [ -x "$BINARY" ] || die "official OpenList binary not executable: $BINARY (set MPT_OPENLIST_TEST_BINARY)"
    if pid="$(fixture_pid)"; then
        note "server already running (pid $pid, port $PORT); reusing it"
        return
    fi
    if port_busy "$PORT"; then
        die "port $PORT is already in use by another process. Stop it, or run with MPT_FIXTURE_PORT=<free port> (the adb reverse command and handover follow the port)."
    fi
    mkdir -p "$SERVER_DIR/v4.2.6" "$FIXTURE_ROOT/storage"
    chmod 700 "$FIXTURE_ROOT"
    cp -f "$BINARY" "$EXE"
    chmod +x "$EXE"
    if [ ! -f "$DATA_DIR/data.db" ]; then
        note "initializing OpenList admin account in $DATA_DIR"
        local generated
        generated="$("$EXE" admin random --data "$DATA_DIR" | sed -n 's/^password:[[:space:]]*//p')"
        [ -n "$generated" ] || die "openlist admin random did not print a password"
        (umask 077; printf '%s\n' "$generated" >"$ADMIN_FILE")
    fi
    note "starting OpenList on 127.0.0.1:$PORT (log: $LOG_FILE)"
    # nohup (not setsid) so $! is the server pid itself and the log stays attached.
    OPENLIST_ADDR=127.0.0.1 OPENLIST_HTTP_PORT="$PORT" OPENLIST_LOG_ENABLE=false \
        nohup "$EXE" server --data "$DATA_DIR" >>"$LOG_FILE" 2>&1 &
    printf '%s\n' "$!" >"$PID_FILE"
    for _ in $(seq 1 60); do
        status="$(http_status 127.0.0.1 "$PORT" /ping || true)"
        [ "$status" = "200" ] && { note "server ready (pid $(cat "$PID_FILE"))"; return; }
        sleep 0.5
    done
    die "server did not answer /ping on 127.0.0.1:$PORT; see $LOG_FILE (last status: ${status:-none})"
}

prepare_fixture() {
    [ -f "$ADMIN_FILE" ] || die "missing $ADMIN_FILE; run 'start' first"
    # TMPDIR must be writable for MSBuild; /mnt/cache/data-cache is the canonical temp root on this host.
    note "preparing mount, relay account and inbound payload (re-runs are idempotent)"
    MPT_FIXTURE_ROOT="$FIXTURE_ROOT" \
    MPT_FIXTURE_ADMIN_PASSWORD="$(cat "$ADMIN_FILE")" \
    MPT_FIXTURE_PORT="$PORT" \
        "$DOTNET" test "$TESTS" --nologo -v minimal \
        --filter "FullyQualifiedName~AndroidDataplaneFixture"
    [ -f "$FIXTURE_ROOT/handover.txt" ] || die "fixture preparation did not produce handover.txt"
    printf '\n'
    cat "$FIXTURE_ROOT/handover.txt"
}

stop_server() {
    local purge="${1:-}" pid
    if pid="$(fixture_pid)"; then
        note "stopping pid $pid"
        kill -TERM "$pid" 2>/dev/null || true
        for _ in $(seq 1 20); do kill -0 "$pid" 2>/dev/null || break; sleep 0.25; done
        kill -0 "$pid" 2>/dev/null && kill -KILL "$pid" 2>/dev/null || true
    else
        note "no fixture server running"
    fi
    rm -f "$PID_FILE"
    if [ "$purge" = "--purge" ]; then
        note "removing $FIXTURE_ROOT"
        rm -rf "${FIXTURE_ROOT:?}"
    fi
}

status() {
    local pid
    if pid="$(fixture_pid)"; then note "running (pid $pid, port $PORT)"; else note "not running"; fi
    note "fixture root : $FIXTURE_ROOT"
    note "port state   : $(port_busy "$PORT" && echo busy || echo free)"
    [ -f "$FIXTURE_ROOT/handover.txt" ] && note "handover     : $FIXTURE_ROOT/handover.txt" || note "handover     : not prepared yet"
}

case "${1:-start}" in
    start) start_server; prepare_fixture ;;
    stop) stop_server "${2:-}" ;;
    status) status ;;
    *) die "usage: $0 {start|status|stop [--purge]}" ;;
esac
