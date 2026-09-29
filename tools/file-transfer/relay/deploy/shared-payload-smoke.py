#!/usr/bin/env python3
"""Live public/Tail proxy acceptance. Creates isolated test namespaces; prints no keys.

Run on the public server as root (disk absence check), after enabling the feature.
The upload subprocess exits before any public payload request is made.
"""
from __future__ import annotations

import argparse
import base64
import json
import multiprocessing
import os
from pathlib import Path
import urllib.error
import urllib.request

DAV = '/mpt/relay/dav/'
TAIL_HOST = 'mpt-relay.tail.lixinrui000.cn'


def request(base, method, path, credentials=None, body=None, tail=False):
    headers = {'Host': TAIL_HOST} if tail else {}
    if credentials:
        headers['Authorization'] = 'Basic ' + base64.b64encode(':'.join(credentials).encode()).decode()
    req = urllib.request.Request(base + path, data=body, headers=headers, method=method)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(req, timeout=45) as response:
            return response.status, dict(response.headers), response.read()
    except urllib.error.HTTPError as error:
        return error.code, dict(error.headers), error.read()


def require(ok, label):
    if not ok:
        raise AssertionError(label)


def seed(args, credentials, items):
    """A disposable sender, with no server responsibility after publishing."""
    conversation = credentials[0]
    for base, tail in [(args.public, False), (args.tail, True)]:
        code, _, _ = request(base, 'POST', '/mpt/relay/v1/conversations', credentials, tail=tail)
        require(code in (200, 201), 'conversation registration')
    for item, kind in items:
        manifest = {'version': 1, 'id': item, 'kind': kind, 'name': f'smoke-{kind}.bin',
                    'size': 1 << 20, 'createdAt': '2026-09-29T05:00:00Z',
                    'senderDeviceId': 'smoke-exited-sender', 'senderName': 'smoke'}
        normal = f'assistant/{conversation}/{item}/'
        locator = f'assistant-locator/{conversation}/{item}/'
        for base, tail, directories in [(args.tail, True, ['assistant/', f'assistant/{conversation}/', normal]),
                                       (args.public, False, ['assistant/', f'assistant/{conversation}/', normal,
                                        'assistant-locator/', f'assistant-locator/{conversation}/', locator])]:
            for directory in directories:
                code, _, _ = request(base, 'MKCOL', DAV + directory, credentials, tail=tail)
                require(code in (201, 405), 'DAV collection')
        payload = bytes(range(256)) * 4096
        code, _, _ = request(args.tail, 'PUT', DAV + normal + 'payload', credentials, payload, True)
        require(code == 201, 'Tail payload upload')
        raw = json.dumps(manifest).encode()
        for base, tail in [(args.tail, True)]:
            code, _, _ = request(base, 'PUT', DAV + normal + 'manifest.json', credentials, raw, tail)
            require(code == 201, 'V1 manifest publish')
        raw = json.dumps({'version': 1, 'message': manifest, 'payloadRoute': 'mpt-tail-relay-v1'}).encode()
        code, _, _ = request(args.public, 'PUT', DAV + locator + 'manifest.json', credentials, raw)
        require(code == 201, 'public locator publish')
        code, _, _ = request(args.public, 'PUT', DAV + normal + 'manifest.json', credentials, json.dumps(manifest).encode())
        require(code == 201, 'public V1 manifest publish')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--public', default='https://proxy.lixinrui000.cn')
    parser.add_argument('--tail', default='http://127.0.0.1:18766')
    parser.add_argument('--data-dir', type=Path, default=Path('/var/lib/mpt-relay'))
    args = parser.parse_args()
    credentials = ('smoke-stream-' + os.urandom(6).hex(), os.urandom(32).hex())
    items = [(os.urandom(16).hex(), kind) for kind in ('file', 'image')]
    code, _, body = request(args.public, 'GET', '/mpt/relay/health')
    health = json.loads(body)
    require(code == 200 and health.get('capabilities', {}).get('sharedPayloadLocator') == 1,
            'nested capability missing')
    require('sharedPayloadLocator' not in health, 'unexpected top-level capability')
    sender = multiprocessing.Process(target=seed, args=(args, credentials, items))
    sender.start()
    sender.join(timeout=180)
    if sender.is_alive():
        sender.terminate()
        sender.join()
        raise AssertionError('sender did not complete')
    require(sender.exitcode == 0, 'sender upload failed')
    print('PASS Tail-only uploads and public metadata; sender process exited', flush=True)

    other = ('smoke-other-' + os.urandom(6).hex(), os.urandom(32).hex())
    code, _, _ = request(args.public, 'POST', '/mpt/relay/v1/conversations', other)
    require(code in (200, 201), 'other namespace registration')
    for item, kind in items:
        path = f'assistant/{credentials[0]}/{item}/payload'
        disk = args.data_dir / 'conversations' / credentials[0] / path
        require((disk.parent / 'manifest.json').is_file(), 'public disk manifest missing at expected path')
        require(not disk.exists(), 'public payload existed before request')
        code, headers, body = request(args.public, 'HEAD', DAV + path, credentials)
        require(code == 200 and int(headers['Content-Length']) == 1 << 20 and not body,
                'legacy HEAD failed')
        code, _, body = request(args.public, 'GET', DAV + path, credentials)
        require(code == 200 and body == bytes(range(256)) * 4096, 'legacy GET bytes differ')
        require(not disk.exists(), 'proxy persisted public payload')
        code, _, _ = request(args.public, 'GET', DAV + path, (credentials[0], os.urandom(32).hex()))
        require(code in (401, 403), 'wrong credential accepted')
        code, _, _ = request(args.public, 'GET', DAV + path, other)
        require(code in (401, 403), 'cross-namespace accepted')
        print(f'PASS {kind}: legacy HEAD/GET exact 1MiB, no public payload, auth isolation', flush=True)

    item = items[0][0]
    path = DAV + f'assistant/{credentials[0]}/{item}/payload'
    local = b'public-local-payload-wins'
    code, _, _ = request(args.public, 'PUT', path, credentials, local)
    require(code == 201, 'public local payload upload')
    code, _, body = request(args.public, 'GET', path, credentials)
    require(code == 200 and body == local, 'public local payload did not take precedence')
    print('PASS existing public local payload precedence', flush=True)
    print('PASS live public/Tail shared payload acceptance; no service disruption injected', flush=True)


if __name__ == '__main__':
    main()
