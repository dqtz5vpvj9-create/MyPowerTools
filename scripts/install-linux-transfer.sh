#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
install_root="${XDG_DATA_HOME:-$HOME/.local/share}/MyPowerTools/headless"
data_root="${XDG_DATA_HOME:-$HOME/.local/share}/MyPowerTools/headless-data"
bin_directory="$HOME/.local/bin"
configuration=Release
start_service=false
with_mcp=false
while (($#)); do
  case "$1" in
    --prefix) install_root="$2"; shift 2 ;;
    --data-root) data_root="$2"; shift 2 ;;
    --bin-dir) bin_directory="$2"; shift 2 ;;
    --configuration) configuration="$2"; shift 2 ;;
    --start) start_service=true; shift ;;
    --with-mcp) with_mcp=true; shift ;;
    --help)
      printf 'Install MPT Linux transfer CLI + Runner.\nUsage: %s [--prefix PATH] [--data-root PATH] [--bin-dir PATH] [--configuration Release|Debug] [--start] [--with-mcp]\nRequires .NET 10 SDK, PowerShell, libsecret-tools, and an unlocked user Secret Service.\n--with-mcp also requires Python venv/pip and installs the Agent MCP in a dedicated virtual environment.\n' "$0"
      exit 0 ;;
    *) printf 'Unknown option: %s\n' "$1" >&2; exit 2 ;;
  esac
done
dotnet_command="${MPT_DOTNET:-$(command -v dotnet || true)}"
if [[ -z "$dotnet_command" && -x "$HOME/.dotnet/dotnet" ]]; then
  dotnet_command="$HOME/.dotnet/dotnet"
fi
if [[ ! -x "$dotnet_command" ]]; then
  printf 'Install .NET 10 SDK or set MPT_DOTNET to its executable.\n' >&2
  exit 1
fi
command -v pwsh >/dev/null
command -v secret-tool >/dev/null
command -v python3 >/dev/null
stage_directory="$(mktemp -d /mnt/cache/data-cache/mpt-linux-transfer-install.XXXXXX)"
trap 'rm -rf -- "${stage_directory:?}"' EXIT

export TMPDIR=/mnt/cache/data-cache
"$dotnet_command" publish "$repo_root/src/MyPowerTools.Runner/MyPowerTools.Runner.csproj" -c "$configuration" --self-contained false -p:NuGetAudit=false -o "$stage_directory/Runner"
"$dotnet_command" publish "$repo_root/src/MyPowerTools.Cli/MyPowerTools.Cli.csproj" -c "$configuration" --self-contained false -p:NuGetAudit=false -o "$stage_directory/CLI"
pwsh -NoLogo -NoProfile -NonInteractive -File "$repo_root/tools/file-transfer/build.ps1" -Configuration "$configuration" -NoMirror

unit_directory="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
mkdir -p "$install_root/Runner" "$install_root/CLI" "$install_root/modules/file-transfer" "$install_root/schemas" "$bin_directory" "$data_root" "$unit_directory"
service_was_active=false
if systemctl --user is-active --quiet mpt-transfer.service; then
  service_was_active=true
  systemctl --user stop mpt-transfer.service
fi
cp -a "$stage_directory/Runner/." "$install_root/Runner/"
cp -a "$stage_directory/CLI/." "$install_root/CLI/"
cp -a "$repo_root/tools/file-transfer/artifacts/package/." "$install_root/modules/file-transfer/"
cp -a "$repo_root/schemas/." "$install_root/schemas/"

# %q quotes actual shell arguments, including installation paths with spaces.
{
  printf '#!/usr/bin/env bash\n'
  printf 'export MPT_DATA_ROOT=%q\n' "$data_root"
  printf 'export MPT_ENDPOINT_ADDRESS=%q\n' "$data_root/runner.sock"
  printf 'exec %q %q "$@"\n' "$dotnet_command" "$install_root/CLI/MyPowerTools.Cli.dll"
} > "$bin_directory/mpt"
chmod +x "$bin_directory/mpt"

python3 - "$dotnet_command" "$install_root" "$data_root" "$unit_directory/mpt-transfer.service" <<'PY'
import json,sys
from pathlib import Path
dotnet,root,data,unit=sys.argv[1:]
arguments=[dotnet,root+'/Runner/MyPowerTools.Runner.dll','--modules',root+'/modules',
           '--data-root',data,'--endpoint-address',data+'/runner.sock',
           '--default-enabled-module','file-transfer','--no-watch','--no-tray','--no-hotkeys']
command=' '.join(json.dumps(x,ensure_ascii=False).replace('%','%%') for x in arguments)
Path(unit).write_text('[Unit]\nDescription=MyPowerTools file transfer\n\n'
 '[Service]\nType=simple\nExecStart='+command+'\nRestart=on-failure\nRestartSec=5\n\n'
 '[Install]\nWantedBy=default.target\n')
PY
systemctl --user daemon-reload
if "$start_service"; then
  systemctl --user enable --now mpt-transfer.service
elif "$service_was_active"; then
  systemctl --user start mpt-transfer.service
fi
printf '\nInstalled CLI: %s/mpt\nRunner service: mpt-transfer.service\n' "$bin_directory"
printf 'Start: systemctl --user start mpt-transfer.service\nStatus: %s/mpt transfer status --json\n' "$bin_directory"

if "$with_mcp"; then
  mkdir -p "$install_root/mcp"
  cp "$repo_root/integrations/file-transfer-mcp/server.py" "$install_root/mcp/server.py"
  cp "$repo_root/integrations/file-transfer-mcp/requirements.txt" "$install_root/mcp/requirements.txt"
  python3 -m venv "$install_root/mcp/venv"
  "$install_root/mcp/venv/bin/python" -m pip install -r "$install_root/mcp/requirements.txt"
  python3 - "$install_root" "$bin_directory/mpt" <<'PYMCP'
import json, shlex, sys
root, wrapper = sys.argv[1:]
command = ["codex", "mcp", "add", "mypowertools-file-transfer", "--env",
           "MPT_COMMAND_JSON=" + json.dumps([wrapper], ensure_ascii=False), "--",
           root + "/mcp/venv/bin/python", root + "/mcp/server.py"]
print("\nRegister the installed Agent MCP with Codex:")
print(shlex.join(command))
PYMCP
fi
