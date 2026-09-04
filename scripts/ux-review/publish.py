"""Apply checksummed reviewed patches to exact baselines and publish new feature branches only."""
import base64
import gzip
import hashlib
import json
import os
import pathlib
import shutil
import subprocess

root = pathlib.Path.cwd()
temp = pathlib.Path(os.environ['RUNNER_TEMP']) / 'personal-ux-publication'
temp.mkdir(parents=True, exist_ok=True)
parts = []
for index in range(1, 4):
    path = root / f'scripts/ux-review/part{index}.b64'
    shutil.copy2(path, temp / path.name)
    parts.append(path.read_text().strip())
encoded = ''.join(parts)
# Repair a known transport transcription before verifying the original payload digest.
encoded = encoded.replace('K5fYymphRnli', 'K5fYprHymphRnli')
repair_path = root / 'scripts/ux-review/repairs.json'
if repair_path.exists():
    repairs = json.loads(repair_path.read_text())
    for old, new in repairs:
        if encoded.count(old) != 1:
            raise RuntimeError('A transport repair did not match exactly once.')
        encoded = encoded.replace(old, new)
payload = base64.b64decode(encoded, validate=True)
if hashlib.sha256(payload).hexdigest() != 'ee0f28a1afd06d716ff8ab0ab7f8825bd2a5ec12ffdfc237ee2b0c98a2d59e8e':
    raise RuntimeError('Patch payload checksum mismatch; no source branches have been modified.')
items = json.loads(gzip.decompress(payload))
manifest = []

def git(directory, *args, capture=False, data=None, env=None):
    command = ['git', '-C', str(directory), *args]
    result = subprocess.run(command, input=data, text=True, check=True,
                            stdout=subprocess.PIPE if capture else None, env=env)
    return result.stdout.strip() if capture else None

def publish(directory, branch):
    # No force pushes, branch resets, merges, releases or changes to pre-existing user branches.
    if git(directory, 'ls-remote', '--heads', 'origin', f'refs/heads/{branch}', capture=True):
        raise RuntimeError(f'Refusing to overwrite existing branch {branch}')
    token = os.environ.get('PUBLISH_TOKEN', '')
    if not token:
        raise RuntimeError('No publication token is available.')
    auth = base64.b64encode(('x-access-token:' + token).encode()).decode()
    env = dict(os.environ)
    env.update(GIT_CONFIG_COUNT='1', GIT_CONFIG_KEY_0='http.https://github.com/.extraheader',
               GIT_CONFIG_VALUE_0='AUTHORIZATION: basic ' + auth)
    git(directory, 'push', 'origin', f'HEAD:refs/heads/{branch}', env=env)

try:
    baseline = items[0]['base_sha']
    git(root, 'fetch', '--depth=1', 'origin', baseline)
    git(root, 'checkout', '--detach', baseline)
    tool_paths = ['tools/' + name for name in ['adb-forwarder', 'doubao-computer-use', 'input-monitor',
        'process-monitor', 'remote-commands', 'remote-notifications', 'screenease', 'smartbird-thermostat']]
    git(root, 'submodule', 'update', '--init', '--depth=1', '--', *tool_paths)
    for index, item in enumerate(items):
        directory = root / item['directory']
        parent = git(directory, 'rev-parse', 'HEAD', capture=True)
        if item['directory'] != '.' and parent != item['base_sha']:
            raise RuntimeError(f"Unexpected tool baseline: {item['repository']}")
        if index == 0 and parent != baseline:
            raise RuntimeError('Unexpected main baseline')
        git(directory, 'switch', '-c', item['branch'])
        git(directory, 'apply', '--check', '-', data=item['patch'])
        git(directory, 'apply', '-', data=item['patch'])
        git(directory, 'diff', '--check')
        git(directory, 'add', '--all')
        git(directory, '-c', 'user.name=Codex', '-c', 'user.email=codex@openai.com',
            'commit', '-m', item['message'])
        sha = git(directory, 'rev-parse', 'HEAD', capture=True)
        entry = {key: item[key] for key in ['repository', 'directory', 'base_branch', 'branch', 'message']}
        entry.update(sha=sha, parent_sha=parent,
                     changed_files=git(directory, 'diff-tree', '--no-commit-id', '--name-only', '-r', sha, capture=True).splitlines())
        try:
            publish(directory, item['branch'])
            entry['published'] = True
        except (subprocess.CalledProcessError, RuntimeError) as error:
            entry['published'] = False
            entry['publication_error'] = str(error)
        manifest.append(entry)
        (temp / 'manifest.json').write_text(json.dumps(manifest, indent=2))
        print(json.dumps({key: entry[key] for key in ['repository', 'branch', 'sha', 'published']}), flush=True)
    git(root, 'submodule', 'status')
finally:
    (temp / 'manifest.json').write_text(json.dumps(manifest, indent=2))
