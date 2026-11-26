"""Check Android size optimizations without changing module isolation or credentials."""
import argparse
import json
from pathlib import Path
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--apk', required=True, type=Path)
parser.add_argument('--assembly-manifest', required=True, type=Path)
args = parser.parse_args()
repo = Path(__file__).resolve().parent.parent
crypto = 'BouncyCastle.Cryptography.dll'
notification_crypto = repo / 'tools/remote-notifications/android-integration/artifacts/package/remote-notifications-android' / crypto
command_crypto = repo / 'tools/remote-commands/android-integration/artifacts/package/remote-commands-android' / crypto
# Byte comparison is deliberate: the modules must consume the same pinned library for deduplication.
assert notification_crypto.read_bytes() == command_crypto.read_bytes(), 'Module crypto dependencies differ; update the pins or stop deduplicating them.'
with zipfile.ZipFile(args.apk) as apk:
    names = set(apk.namelist())
    copies = [name for name in names if name.startswith('assets/modules/') and name.endswith('/' + crypto)]
    assert copies == ['assets/modules/remote-notifications-android/' + crypto], copies
    assert apk.read(copies[0]) == command_crypto.read_bytes(), 'Packaged shared crypto is stale.'
    assert not any(name.startswith('lib/') and not name.startswith('lib/arm64-v8a/') for name in names), 'Phone APK contains another ABI.'
    for module, surface in [('file-transfer', 'FileTransfer.Surface'), ('remote-notifications-android', 'MyPowerTools.MobileNotifications'), ('remote-commands-android', 'MyPowerTools.MobileRemoteCommands'), ('mobile-tool-control', 'MyPowerTools.MobileToolControl')]:
        assert f'assets/modules/{module}/module.json' in names, module
        assert f'assets/modules/{module}/ui/surface/{surface}.dll' in names, surface
    manifest = args.assembly_manifest.read_text()
    for desktop_backend in ['Avalonia.Win32.dll', 'Avalonia.X11.dll', 'Avalonia.Native.dll', 'Avalonia.FreeDesktop.dll', 'Avalonia.Desktop.dll']:
        assert desktop_backend not in manifest, desktop_backend
    for module in ['FileTransfer.Core', 'FileTransfer.MyPowerTools', 'FileTransfer.Surface', 'RemoteCommands.Android', 'RemoteNotifications.Android', 'BouncyCastle.Cryptography', 'Renci.SshNet']:
        assert module + '.dll' in manifest, 'Trimmer removed a dynamic dependency: ' + module
    sizes = {name: apk.getinfo(name).compress_size for name in ['lib/arm64-v8a/libopenlist.so', 'lib/arm64-v8a/libassemblies.arm64-v8a.blob.so']}
print(json.dumps({'apkBytes': args.apk.stat().st_size, 'apkMiB': round(args.apk.stat().st_size / 1048576, 2), 'compressedBytes': sizes, 'moduleCryptoDeduplicated': True, 'desktopBackendsAbsent': True, 'dynamicDependenciesPresent': True}))
