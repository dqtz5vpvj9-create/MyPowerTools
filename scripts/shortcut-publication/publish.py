"""Publish reviewed patches on isolated branches after checking their transport digest."""
import base64
import hashlib
import json
import lzma
import os
from pathlib import Path
import shutil
import subprocess

here = Path.cwd()
evidence = Path(os.environ['RUNNER_TEMP']) / 'shortcut-publication-evidence'
evidence.mkdir(parents=True, exist_ok=True)
staging = here / 'scripts/shortcut-publication'
encoded = ''
for index in range(16):
    path = staging / f'xz-{index:02d}.txt'
    shutil.copy2(path, evidence / path.name)
    encoded += ''.join(path.read_text().split())
repair_file = staging / 'repairs.json'
if repair_file.exists():
    shutil.copy2(repair_file, evidence / repair_file.name)
    for old, new in json.loads(repair_file.read_text()):
        if encoded.count(old) != 1:
            raise RuntimeError('Transport repair must match exactly once')
        encoded = encoded.replace(old, new)
payload = base64.b64decode(encoded, validate=True)
digest = hashlib.sha256(payload).hexdigest()
(evidence / 'digest.txt').write_text(digest + '\n')
if digest != '0cda25bf9cfc29cb16413394620620febbefced0d90a94de66697cbf8ed76646':
    raise RuntimeError('Transport checksum mismatch; no feature branch was modified')
items = json.loads(lzma.decompress(payload))
main = items[-1]
assert main['directory'] == '.'
work = Path(os.environ['RUNNER_TEMP']) / 'shortcut-source-work'
manifest = []

def git(directory, *args, data=None, env=None):
    result = subprocess.run(['git', '-C', str(directory), *args], input=data, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
    if result.returncode:
        # Commands do not contain credentials. GitHub also masks the supplied secret.
        raise RuntimeError(f'git {args[0]} failed: {result.stderr}')
    return result.stdout.strip()

def publish(directory, item):
    branch = item['branch']
    tree = git(directory, 'write-tree')
    remote = git(directory, 'ls-remote', '--heads', 'origin', 'refs/heads/' + branch)
    existing = remote.split()[0] if remote else None
    if existing and existing != item['base_sha']:
        git(directory, 'fetch', '--depth=1', 'origin', existing)
        if git(directory, 'rev-parse', existing + '^{tree}') != tree:
            raise RuntimeError(f'Refusing to overwrite a changed feature branch: {item["repository"]}')
        return existing
    git(directory, '-c', 'user.name=Codex', '-c', 'user.email=codex@openai.com',
        'commit', '-m', item['message'])
    sha = git(directory, 'rev-parse', 'HEAD')
    token = os.environ['PUBLISH_TOKEN']
    auth = base64.b64encode(('x-access-token:' + token).encode()).decode()
    environment = dict(os.environ)
    environment.update(GIT_CONFIG_COUNT='1', GIT_CONFIG_KEY_0='http.https://github.com/.extraheader',
                       GIT_CONFIG_VALUE_0='AUTHORIZATION: basic ' + auth)
    git(directory, 'push', 'origin', f'HEAD:refs/heads/{branch}', env=environment)
    return sha

try:
    work.mkdir()
    git(work, 'init')
    git(work, 'remote', 'add', 'origin', 'https://github.com/' + main['repository'] + '.git')
    git(work, 'fetch', '--depth=1', 'origin', main['base_sha'])
    git(work, 'checkout', '--detach', main['base_sha'])
    tools = ['tools/' + name for name in ['adb-forwarder', 'doubao-computer-use', 'input-monitor',
             'process-monitor', 'remote-commands', 'remote-notifications', 'screenease', 'smartbird-thermostat']]
    git(work, 'submodule', 'update', '--init', '--depth=1', '--', *tools)
    for item in items:
        directory = work / item['directory']
        if git(directory, 'rev-parse', 'HEAD') != item['base_sha']:
            raise RuntimeError('Baseline mismatch for ' + item['repository'])
        git(directory, 'apply', '--check', '-', data=item['patch'])
        git(directory, 'apply', '-', data=item['patch'])
        git(directory, 'diff', '--check')
        if item['directory'] == '.':
            for child in manifest:
                git(work, 'update-index', '--cacheinfo', '160000', child['sha'], child['directory'])
            # Do not add submodule worktrees: their index must retain the published commit above.
            git(work, 'add', '--all', '--', '.', ':!tools')
        else:
            git(directory, 'add', '--all')
        sha = publish(directory, item)
        entry = {key: item[key] for key in ['repository', 'directory', 'base_sha', 'branch', 'message']}
        entry['sha'] = sha
        manifest.append(entry)
        (evidence / 'manifest.json').write_text(json.dumps(manifest, indent=2))
        print(json.dumps(entry), flush=True)
    (evidence / 'publication-complete.txt').write_text(manifest[-1]['sha'] + '\n')
finally:
    (evidence / 'manifest.json').write_text(json.dumps(manifest, indent=2))
