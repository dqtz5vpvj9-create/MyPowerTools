"""Read-only acceptance of the installed shared MCP: protocol, auth and disposal."""
import argparse
import concurrent.futures
import http.client
import json
from pathlib import Path
import socket
import time
from urllib.parse import urlsplit


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--token-file', type=Path, required=True)
    parser.add_argument('--url', default='http://127.0.0.1:17843')
    parser.add_argument('--rounds', type=int, default=3)
    parser.add_argument('--connections', type=int, default=20)
    args = parser.parse_args()
    target = urlsplit(args.url)
    token = args.token_file.read_text().strip()
    headers = {'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json',
               'Accept': 'application/json, text/event-stream'}

    def request(path, payload=None, custom=None):
        conn = http.client.HTTPConnection(target.hostname, target.port, timeout=30)
        try:
            conn.request('POST' if payload is not None else 'GET', path,
                         json.dumps(payload) if payload is not None else None,
                         headers if custom is None else custom)
            response = conn.getresponse()
            raw = response.read().decode()
            status = response.status
            if raw.startswith('event:'):
                raw = next(line[6:] for line in raw.splitlines() if line.startswith('data: '))
            return status, json.loads(raw) if raw else None
        finally:
            conn.close()

    def rpc(method, params):
        status, result = request('/mcp', {'jsonrpc': '2.0', 'id': 1, 'method': method, 'params': params})
        assert status == 200 and 'error' not in result, (status, result)
        return result['result']

    assert request('/health', custom={})[0] == 401
    assert request('/health', custom={**headers, 'Authorization': 'Bearer wrong'})[0] == 401
    assert request('/health', custom={**headers, 'Origin': 'https://example.com'})[0] == 403
    assert request('/health', custom={**headers, 'Host': 'attacker.example'})[0] == 403
    tools = rpc('tools/list', {})['tools']
    assert len(tools) == 14
    schemas = {tool['name']: tool['inputSchema']['properties'] for tool in tools}
    assert schemas['mpt_send_shared']['via']['enum'] == ['auto', 'quark']
    assert schemas['mpt_cloud_select']['mode']['enum'] == ['auto', 'cloud-only']
    invalid = rpc('tools/call', {'name': 'mpt_cloud_select', 'arguments': {'account_id': 'unused', 'mode': 123}})
    assert invalid.get('isError') is True
    before = request('/health')[1]
    assert before['activeCalls'] == 0 and before['sessions'] == 0

    def cycle(_):
        rpc('initialize', {'protocolVersion': '2025-03-26', 'capabilities': {},
                          'clientInfo': {'name': 'mpt-lifecycle-test', 'version': '1'}})
        assert len(rpc('tools/list', {})['tools']) == 14
        # A missing item is a meaningful tool error, not a protocol/transport failure.
        result = rpc('tools/call', {'name': 'mpt_receipts', 'arguments': {'item_ids': ['mpt-lifecycle-test-missing']}})
        assert result.get('isError') is True
        assert result['structuredContent']['error']['code'] == 'invalid_arguments'

    for round_number in range(args.rounds):
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            list(pool.map(cycle, range(args.connections)))
        health = request('/health')[1]
        assert health == before, (before, health)
        print(json.dumps({'round': round_number + 1, 'connections': args.connections, **health}), flush=True)

    # Disconnect during a short Runner command too. Its accepted execution must
    # finish without quarantining the module or admitting another command.
    payload = json.dumps({'jsonrpc': '2.0', 'id': 3, 'method': 'tools/call', 'params': {
        'name': 'mpt_devices', 'arguments': {}}}).encode()
    raw = socket.create_connection((target.hostname, target.port), timeout=5)
    wire_headers = {**headers, 'Host': target.netloc, 'Content-Length': str(len(payload))}
    raw.sendall(('POST /mcp HTTP/1.1\r\n' + ''.join(k + ': ' + v + '\r\n' for k, v in wire_headers.items()) + '\r\n').encode() + payload)
    try:
        for attempt in range(200):
            if request('/health')[1]['activeCalls'] == 1:
                break
            time.sleep(.05)
        else:
            raise AssertionError('Device query did not become active')
    finally:
        raw.shutdown(socket.SHUT_RDWR)
        raw.close()
    released_at = time.monotonic()
    for attempt in range(200):
        if request('/health')[1]['activeCalls'] == 0:
            break
        time.sleep(.05)
    else:
        raise AssertionError('Disconnected command did not finish')
    assert rpc('tools/call', {'name': 'mpt_status', 'arguments': {}})['structuredContent']['ok']
    print(json.dumps({'commandDisconnect': 'released', 'seconds': round(time.monotonic() - released_at, 3)}), flush=True)

    # Close a real pending receipt request. It must release its Runner subscription.
    status = rpc('tools/call', {'name': 'mpt_status', 'arguments': {}})['structuredContent']
    assert status['ok']
    devices = rpc('tools/call', {'name': 'mpt_devices', 'arguments': {}})['structuredContent']
    assert devices['ok']
    candidate = None
    for item in status['data'].get('items', []):
        if item.get('state') not in ('stored', 'queued', 'sending'):
            continue
        receipts = {r.get('deviceId') for r in item.get('receipts', [])}
        for device in devices['data'].get('devices', []):
            if device['deviceId'] not in receipts:
                candidate = (item['id'], device['deviceId'])
                break
        if candidate:
            break
    if candidate:
        payload = json.dumps({'jsonrpc': '2.0', 'id': 2, 'method': 'tools/call', 'params': {
            'name': 'mpt_wait_receipts', 'arguments': {'item_ids': [candidate[0]], 'receiver': candidate[1], 'timeout_seconds': 600}}}).encode()
        raw = socket.create_connection((target.hostname, target.port), timeout=5)
        wire_headers = {**headers, 'Host': target.netloc, 'Content-Length': str(len(payload))}
        raw.sendall(('POST /mcp HTTP/1.1\r\n' + ''.join(k + ': ' + v + '\r\n' for k, v in wire_headers.items()) + '\r\n').encode() + payload)
        try:
            for attempt in range(200):
                if request('/health')[1]['activeSubscriptions'] == 1:
                    break
                time.sleep(.05)
            else:
                raise AssertionError('Receipt wait did not become active')
        finally:
            raw.shutdown(socket.SHUT_RDWR)
            raw.close()
        for attempt in range(200):
            if request('/health')[1]['activeCalls'] == 0 and request('/health')[1]['activeSubscriptions'] == 0:
                break
            time.sleep(.05)
        else:
            raise AssertionError('Disconnected wait leaked an active call')
        print(json.dumps({'disconnect': 'released', 'activeCalls': 0}), flush=True)
    else:
        print(json.dumps({'disconnect': 'not exercised: no pending receipt candidate'}), flush=True)
    assert rpc('tools/call', {'name': 'mpt_status', 'arguments': {}})['structuredContent']['ok']
    print(json.dumps({'passed': True, 'tools': len(tools), **request('/health')[1]}), flush=True)


if __name__ == '__main__':
    main()
