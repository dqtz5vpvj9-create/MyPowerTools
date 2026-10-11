"""Register the shared local MCP endpoint without changing other MCP servers."""
import argparse
import json
import os
from pathlib import Path
import re
import tomllib

NAMES = {'mpt-file-transfer', 'mypowertools-file-transfer', 'mpt-transfer'}


def migrate_config(text, url, token):
    parsed = tomllib.loads(text)
    servers = parsed.get('mcp_servers', {})
    names = [name for name, entry in servers.items() if name in NAMES or
             ('file-transfer-mcp' in str(entry.get('args', '')) or
              'TransferMcp' in str(entry.get('command', '')))]
    name = names[0] if names else 'mpt-file-transfer'
    # Remove only selected table sections, preserving unrelated text and comments.
    chunks = re.split(r'(?m)^(?=\[)', text)
    kept = []
    for chunk in chunks:
        header = chunk.splitlines()[0] if chunk else ''
        remove = False
        if header.startswith('['):
            try:
                keys = tomllib.loads(header + '\n').get('mcp_servers', {})
                remove = any(key in names for key in keys)
            except tomllib.TOMLDecodeError:
                pass
        if not remove:
            kept.append(chunk)
    table = 'mcp_servers.' + json.dumps(name)
    result = ''.join(kept).rstrip() + '\n\n[' + table + ']\nurl = ' + json.dumps(url) + '\n'
    for key in ('enabled', 'required', 'enabled_tools', 'disabled_tools', 'startup_timeout_sec', 'tool_timeout_sec'):
        if key in servers.get(name, {}):
            result += key + ' = ' + json.dumps(servers[name][key]) + '\n'
    result += '[' + table + '.http_headers]\nAuthorization = ' + json.dumps('Bearer ' + token) + '\n'
    tomllib.loads(result)
    return result, name


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--token-file', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--port', type=int, default=17843)
    parser.add_argument('--register-codex', action='store_true')
    parser.add_argument('--codex-home', type=Path, default=Path(os.environ.get('CODEX_HOME') or Path.home() / '.codex'))
    args = parser.parse_args()
    token = args.token_file.read_text().strip()
    if len(token) < 32:
        raise ValueError('MCP token is missing or too short')
    url = f'http://127.0.0.1:{args.port}/mcp'
    entry = {'type': 'http', 'url': url, 'headers': {'Authorization': 'Bearer ' + token}}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    # Windows installer restricts the enclosing directory ACL before this write.
    with os.fdopen(os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), 'w') as stream:
        json.dump({'mcpServers': {'mpt-file-transfer': entry}}, stream, indent=2)
    if args.register_codex:
        config = args.codex_home / 'config.toml'
        config.parent.mkdir(parents=True, exist_ok=True)
        text = config.read_text(encoding='utf-8-sig') if config.exists() else ''
        replacement, name = migrate_config(text, url, token)
        staged = config.with_suffix('.toml.mpt-new')
        with os.fdopen(os.open(staged, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), 'w') as stream:
            stream.write(replacement)
        staged.replace(config)
        print(f'Registered {name} at {url}; reload MCP in running clients.')
    print(f'Private MCP client configuration: {args.output}')


if __name__ == '__main__':
    main()
