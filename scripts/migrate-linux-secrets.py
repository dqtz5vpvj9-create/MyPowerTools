#!/usr/bin/env python3
"""One-time migration. Does not unlock or delete the desktop keyring."""
import argparse
import json
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--cli-command-json', required=True)
    args = parser.parse_args()
    command = json.loads(args.cli_command_json)
    if not isinstance(command, list) or not command or not all(isinstance(x, str) for x in command):
        raise ValueError('Invalid CLI command')
    from gi.repository import Gio, GLib
    bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    destination = 'org.freedesktop.secrets'
    service = '/org/freedesktop/secrets'
    def call(path, interface, method, values):
        return bus.call_sync(destination, path, interface, method, values, None,
                             Gio.DBusCallFlags.NONE, 5000, None).unpack()
    unlocked, locked = call(service, 'org.freedesktop.Secret.Service', 'SearchItems',
                            GLib.Variant('(a{ss})', ({'application':'com.mypowertools.secrets'},)))
    if locked:
        print('Legacy MPT credentials are locked. No unlock prompt was opened and no credentials were changed.', file=sys.stderr)
        return 3
    if not unlocked:
        print('No legacy MPT credentials found.')
        return 0
    _, session = call(service, 'org.freedesktop.Secret.Service', 'OpenSession',
                      GLib.Variant('(sv)', ('plain', GLib.Variant('s', ''))))
    try:
        secrets, = call(service, 'org.freedesktop.Secret.Service', 'GetSecrets',
                        GLib.Variant('(aoo)', (unlocked, session)))
        entries=[]
        for path in unlocked:
            attributes, = call(path, 'org.freedesktop.DBus.Properties', 'Get',
                               GLib.Variant('(ss)', ('org.freedesktop.Secret.Item', 'Attributes')))
            value=bytes(secrets[path][2]).decode('utf-8')
            entries.append({'moduleId':attributes['module'],'name':attributes['name'],'value':value})
        result=subprocess.run(command+['secrets','import','--stdin'], input=json.dumps(entries),
                              text=True, timeout=30, check=False)
        return result.returncode
    finally:
        call(session, 'org.freedesktop.Secret.Session', 'Close', None)

if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        print('Legacy credential migration failed; no desktop credentials were removed.', file=sys.stderr)
        sys.exit(1)
