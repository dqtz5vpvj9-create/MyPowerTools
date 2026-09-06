#!/usr/bin/env python3
"""Measure the installed macOS process group; nonzero exit means CPU acceptance failed."""
import argparse
import json
from pathlib import Path
import platform
import plistlib
import subprocess
import time

REQUIRED = {'MyPowerTools.Shell.Avalonia', 'MyPowerTools.Runner', 'MyPowerTools.ServiceManager'}


def cpu_seconds(value):
    days, _, clock = value.rpartition('-')
    return (int(days) * 86400 if days else 0) + sum(
        float(part) * 60 ** index for index, part in enumerate(reversed(clock.split(':'))))


def snapshot(app):
    rows = {}
    output = subprocess.check_output(['ps', '-axo', 'pid,ppid,time,rss,comm'], text=True)
    for line in output.splitlines()[1:]:
        fields = line.strip().split(None, 4)
        if len(fields) != 5:
            continue
        pid, parent, cpu, rss, executable = fields
        rows[int(pid)] = dict(pid=int(pid), parent=int(parent), cpu_seconds=cpu_seconds(cpu),
                              rss_kib=int(rss), name=Path(executable).name,
                              in_bundle=executable.startswith(str(app) + '/'))
    selected = {pid for pid, row in rows.items() if row['in_bundle']}
    while True:
        expanded = selected | {pid for pid, row in rows.items() if row['parent'] in selected}
        if expanded == selected:
            return {pid: rows[pid] for pid in selected}
        selected = expanded


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, default=Path('/Applications/MyPowerTools.app'))
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--warmup', type=float, default=60)
    parser.add_argument('--duration', type=float, default=120)
    parser.add_argument('--interval', type=float, default=5)
    parser.add_argument('--budget', type=float, default=1.0)
    parser.add_argument('--required-host', action='append', default=[], help='Host required by an explicitly enabled background task')
    args = parser.parse_args()
    if platform.system() != 'Darwin':
        parser.error('Run this gate on the actual macOS device.')
    if min(args.duration, args.interval, args.budget) <= 0 or args.warmup < 0:
        parser.error('Duration, interval and budget must be positive; warm-up cannot be negative.')
    app = args.app.resolve()
    if not app.is_dir():
        parser.error('The complete installed app bundle is required.')
    with (app / 'Contents/Info.plist').open('rb') as file:
        app_version = plistlib.load(file).get('CFBundleShortVersionString', 'unknown')
    print(f'Warm-up {args.warmup:g}s, then observe {args.duration:g}s. Leave MPT idle.', flush=True)
    time.sleep(args.warmup)
    before = snapshot(app)
    started = time.monotonic()
    previous, previous_time = before, started
    intervals, issues = [], set()
    required = REQUIRED | set(args.required_host)
    for path in (app / 'Contents/MacOS/ServiceUnits/units').glob('*.json'):
        manifest = json.loads(path.read_text(encoding='utf-8-sig'))
        name = Path(manifest['exec']).name
        if manifest.get('autostart', False): required.add(name)
    running_names = {row['name'] for row in before.values()}
    missing = required - running_names
    if missing:
        issues.add('Missing required hosts: ' + ', '.join(sorted(missing)))
    after = before
    while time.monotonic() - started < args.duration:
        time.sleep(min(args.interval, max(0, args.duration - (time.monotonic() - started))))
        after, now = snapshot(app), time.monotonic()
        if set(after) != set(before):
            issues.add('Process group changed during observation; repeat after startup settles.')
        cpu = sum(max(0, row['cpu_seconds'] - previous[pid]['cpu_seconds'])
                  for pid, row in after.items() if pid in previous)
        intervals.append(dict(elapsed_seconds=round(now - started, 3),
                              cpu_percent=round(cpu / (now - previous_time) * 100, 3)))
        previous, previous_time = after, now
    elapsed = previous_time - started
    processes = [dict(pid=pid, name=row['name'], rss_kib=row['rss_kib'],
                      cpu_percent=round(max(0, row['cpu_seconds'] - before[pid]['cpu_seconds']) / elapsed * 100, 3))
                 for pid, row in after.items() if pid in before]
    total = sum(row['cpu_percent'] for row in processes)
    if total > args.budget:
        issues.add(f'Combined CPU {total:.3f}% exceeds the {args.budget:g}% single-core budget.')
    diagnostic = args.duration < 120 or args.warmup < 60 or args.budget > 1
    if diagnostic:
        issues.add('Diagnostic settings do not satisfy the default release observation requirements.')
    result = dict(schema_version=1, app_version=app_version, os=platform.mac_ver()[0], architecture=platform.machine(),
                  measured_at=time.strftime('%Y-%m-%dT%H:%M:%S%z'), elapsed_seconds=elapsed,
                  warmup_seconds=args.warmup, budget_percent=args.budget,
                  total_cpu_percent=round(total, 3), processes=processes, intervals=intervals,
                  cpu_gate_passed=not issues, issues=sorted(issues),
                  limitations='CPU/RSS only. Live delivery, physical footprint and energy/wake-up acceptance are separate.')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2), flush=True)
    return 0 if not issues else 1


if __name__ == '__main__':
    raise SystemExit(main())
