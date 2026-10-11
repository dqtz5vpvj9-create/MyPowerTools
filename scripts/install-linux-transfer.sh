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
      printf 'Install MPT Linux transfer CLI + Runner.\nUsage: %s [--prefix PATH] [--data-root PATH] [--bin-dir PATH] [--configuration Release|Debug] [--start] [--with-mcp]\nRequires .NET 10 SDK and PowerShell. Credentials use the private service vault; no desktop unlock is needed.\n--with-mcp installs one shared HTTP MCP service; Python is used only for installation.\n' "$0"
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
command -v python3 >/dev/null
stage_directory="$(mktemp -d /mnt/cache/data-cache/mpt-linux-transfer-install.XXXXXX)"
trap 'rm -rf -- "${stage_directory:?}"' EXIT

export TMPDIR=/mnt/cache/data-cache
"$dotnet_command" publish "$repo_root/src/MyPowerTools.Runner/MyPowerTools.Runner.csproj" -c "$configuration" --self-contained false -p:NuGetAudit=false -o "$stage_directory/Runner"
"$dotnet_command" publish "$repo_root/src/MyPowerTools.Cli/MyPowerTools.Cli.csproj" -c "$configuration" --self-contained false -p:NuGetAudit=false -o "$stage_directory/CLI"
pwsh -NoLogo -NoProfile -NonInteractive -File "$repo_root/tools/file-transfer/build.ps1" -Configuration "$configuration" -NoMirror

# Migrate an existing desktop-backed installation before stopping its running
# process. A locked old keyring is not an empty store: never regenerate identity.
vault_root="${MPT_SECRET_VAULT_ROOT:-${XDG_DATA_HOME:-$HOME/.local/share}/MyPowerTools/secrets}"
# A key created by another module is not proof that transfer credentials migrated.
transfer_secret_name="$(python3 -c 'print("secret://file-transfer/receiver-token".encode().hex().upper() + ".enc")')"
export MPT_SECRET_VAULT_ROOT="$vault_root"
if [[ -f "$data_root/state/modules/file-transfer/data/preferences.json" && ! -f "$vault_root/$transfer_secret_name" ]]; then
  python3 - "$repo_root/scripts/migrate-linux-secrets.py" "$dotnet_command" "$stage_directory/CLI/MyPowerTools.Cli.dll" <<'PYMIGRATE'
import json, subprocess, sys
sys.exit(subprocess.run([sys.executable, sys.argv[1], '--cli-command-json', json.dumps(sys.argv[2:])]).returncode)
PYMIGRATE
  if [[ ! -f "$vault_root/master.key" || ! -f "$vault_root/$transfer_secret_name" ]]; then
    printf 'Existing transfer identity has no migrated credentials. Restore/import them before updating this installation.\n' >&2
    exit 1
  fi
fi

unit_directory="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
mkdir -p "$install_root/Runner" "$install_root/CLI" "$install_root/modules/file-transfer" "$install_root/schemas" "$bin_directory" "$data_root" "$unit_directory"
service_was_active=false
if systemctl --user is-active --quiet mpt-transfer.service; then
  service_was_active=true
  systemctl --user stop mpt-transfer.service
fi
cp -a "$stage_directory/Runner/." "$install_root/Runner/"
# Receipt waits may still be running while the service is updated. Replacing each
# file preserves their mapped assembly inode instead of overwriting its bytes.
python3 - "$stage_directory/CLI" "$install_root/CLI" <<'PYCLI'
import os, shutil, sys
from pathlib import Path
source, destination = map(Path, sys.argv[1:])
for entry in source.rglob('*'):
    target = destination / entry.relative_to(source)
    if entry.is_dir():
        target.mkdir(parents=True, exist_ok=True)
    else:
        staged = target.with_name(target.name + '.update')
        shutil.copy2(entry, staged)
        os.replace(staged, target)
PYCLI
cp -a "$repo_root/tools/file-transfer/artifacts/package/." "$install_root/modules/file-transfer/"
cp -a "$repo_root/schemas/." "$install_root/schemas/"

# %q quotes actual shell arguments, including installation paths with spaces.
{
  printf '#!/usr/bin/env bash\n'
  printf 'export MPT_DATA_ROOT=%q\n' "$data_root"
  printf 'export MPT_SECRET_VAULT_ROOT=%q\n' "$vault_root"
  printf 'export MPT_ENDPOINT_ADDRESS=%q\n' "$data_root/runner.sock"
  printf 'exec %q %q "$@"\n' "$dotnet_command" "$install_root/CLI/MyPowerTools.Cli.dll"
} > "$bin_directory/mpt"
chmod +x "$bin_directory/mpt"

python3 - "$dotnet_command" "$install_root" "$data_root" "$unit_directory/mpt-transfer.service" "$vault_root" <<'PY'
import json,sys
from pathlib import Path
dotnet,root,data,unit,vault=sys.argv[1:]
arguments=[dotnet,root+'/Runner/MyPowerTools.Runner.dll','--modules',root+'/modules',
           '--data-root',data,'--endpoint-address',data+'/runner.sock',
           '--default-enabled-module','file-transfer','--no-watch','--no-tray','--no-hotkeys']
command=' '.join(json.dumps(x,ensure_ascii=False).replace('%','%%') for x in arguments)
Path(unit).write_text('[Unit]\nDescription=MyPowerTools file transfer\n\n'
 '[Service]\nType=simple\nEnvironment='+json.dumps('MPT_SECRET_VAULT_ROOT='+vault).replace('%','%%')+'\nWorkingDirectory='+(root+'/Runner').replace('%','%%')+'\nExecStart='+command+'\nRestart=on-failure\nRestartSec=5\n\n'
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
  MPT_DOTNET="$dotnet_command" "$repo_root/scripts/install-linux-transfer-mcp.sh" --prefix "$install_root/mcp" --data-root "$data_root"
fi
