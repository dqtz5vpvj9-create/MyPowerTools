"""Install the companion skill for the current user's Codex and Claude clients."""
import argparse
import os
from pathlib import Path
import shutil


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--codex-home', type=Path, default=Path(os.environ.get('CODEX_HOME') or Path.home() / '.codex'))
    parser.add_argument('--claude-home', type=Path, default=Path(os.environ.get('CLAUDE_CONFIG_DIR') or Path.home() / '.claude'))
    args = parser.parse_args()
    source = Path(__file__).resolve().parent / 'skills' / 'mpt-file-transfer'
    # Claude may already use the common ~/.agents/skills directory via a symlink.
    # Codex discovers that directory too; avoid registering the same skill twice.
    claude_skills = args.claude_home / 'skills'
    shared = Path.home() / '.agents' / 'skills'
    roots = [args.codex_home / 'skills', claude_skills]
    if claude_skills.resolve() == shared.resolve() and args.codex_home.resolve() == (Path.home() / '.codex').resolve():
        roots = [claude_skills]
    for root in roots:
        destination = root / source.name
        shutil.copytree(source, destination, dirs_exist_ok=True)
        print(f'Installed skill: {destination / "SKILL.md"}')
    print('New sessions can discover $mpt-file-transfer. Existing sessions may need to refresh skills.')


if __name__ == '__main__':
    main()
