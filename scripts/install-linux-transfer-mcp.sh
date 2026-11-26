#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
install_root="${XDG_DATA_HOME:-$HOME/.local/share}/MyPowerTools/TransferMcp"
data_root="${XDG_DATA_HOME:-$HOME/.local/share}/MyPowerTools/headless-data"
port=17843
register=false
while (($#)); do
  case "$1" in
    --prefix) install_root="$2"; shift 2 ;;
    --data-root) data_root="$2"; shift 2 ;;
    --port) port="$2"; shift 2 ;;
    --register-codex) register=true; shift ;;
    *) printf 'Unknown option: %s\n' "$1" >&2; exit 2 ;;
  esac
done
dotnet_command="${MPT_DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$dotnet_command" ]]; then dotnet_command="$HOME/.dotnet/dotnet"; fi
stage_directory="$(mktemp -d /mnt/cache/data-cache/mpt-mcp-install.XXXXXX)"
trap 'rm -r -- "${stage_directory:?}"' EXIT
"$dotnet_command" publish "$repo_root/src/MyPowerTools.TransferMcp" -c Release --self-contained false -o "$stage_directory/server"
mkdir -p "$install_root/Server"
chmod 700 "$install_root"
python3 - "$install_root/http.token" <<'PY'
import os, secrets, sys
try:
    fd = os.open(sys.argv[1], os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
except FileExistsError:
    pass
else:
    with os.fdopen(fd, 'w') as stream: stream.write(secrets.token_hex(32))
PY
if systemctl --user is-active --quiet mpt-transfer-mcp.service; then systemctl --user stop mpt-transfer-mcp.service; fi
cp -a "$stage_directory/server/." "$install_root/Server/"
unit_directory="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
mkdir -p "$unit_directory"
python3 - "$dotnet_command" "$install_root" "$data_root" "$port" "$unit_directory/mpt-transfer-mcp.service" <<'PY'
import json,sys
from pathlib import Path
dotnet,root,data,port,unit=sys.argv[1:]
def quote(value): return json.dumps(value).replace('%','%%')
lines=['[Unit]','Description=MyPowerTools shared file transfer MCP','After=mpt-transfer.service','[Service]',
       'ExecStart='+quote(dotnet)+' '+quote(root+'/Server/MyPowerTools.TransferMcp.dll'),
       'Environment='+quote('MPT_DATA_ROOT='+data),
       'Environment='+quote('MPT_ENDPOINT_ADDRESS='+data+'/runner.sock'),
       'Environment='+quote('MPT_MCP_TOKEN_FILE='+root+'/http.token'),
       'Environment='+quote('MPT_MCP_PORT='+port),'Restart=on-failure','RestartSec=2','UMask=0077',
       '[Install]','WantedBy=default.target','']
Path(unit).write_text('\n'.join(lines))
PY
systemctl --user daemon-reload
systemctl --user enable --now mpt-transfer-mcp.service
python3 - "$install_root/http.token" "$port" <<'PY'
from pathlib import Path
import sys,time,json,urllib.request
headers={'Authorization':'Bearer '+Path(sys.argv[1]).read_text().strip()}
for attempt in range(30):
    try:
        with urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:'+sys.argv[2]+'/health',headers=headers),timeout=2) as response:
            assert json.load(response)['status']=='ready'
        break
    except Exception:
        if attempt==29: raise
        time.sleep(.2)
PY
config_args=(--token-file "$install_root/http.token" --output "$install_root/mcp.json" --port "$port")
if "$register"; then config_args+=(--register-codex); fi
python3 "$repo_root/integrations/file-transfer-mcp/configure_http.py" "${config_args[@]}"
python3 "$repo_root/integrations/file-transfer-mcp/install_skill.py"
