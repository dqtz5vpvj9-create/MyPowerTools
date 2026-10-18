#!/usr/bin/env python3
"""Sample a real linked-production history process without native services/devices."""
import argparse
import json
import os
import subprocess
import time
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("assembly")
parser.add_argument("output_prefix")
parser.add_argument("--sustained", action="store_true")
args = parser.parse_args()
prefix = Path(args.output_prefix)
prefix.parent.mkdir(parents=True, exist_ok=True)
command = ["dotnet", str(Path(args.assembly).resolve()), "--benchmark-sustained" if args.sustained else "--benchmark"]
samples = []
with prefix.with_suffix(".jsonl").open("w") as output, prefix.with_suffix(".stderr.log").open("w") as errors:
    process = subprocess.Popen(command, stdout=output, stderr=errors, env=os.environ.copy())
    started = time.monotonic()
    while process.poll() is None:
        sample = {"elapsedSeconds": time.monotonic() - started, "pid": process.pid}
        try:
            for line in Path(f"/proc/{process.pid}/status").read_text().splitlines():
                key, _, value = line.partition(":")
                if key in {"VmRSS", "VmHWM", "RssAnon", "VmData", "Threads"}:
                    sample[key] = int(value.split()[0]) * (1024 if key != "Threads" else 1)
            samples.append(sample)
        except FileNotFoundError:
            pass
        time.sleep(0.02)
    code = process.wait()
summary = {"command": command, "exitCode": code, "samples": samples,
           "peakRssBytes": max((s.get("VmRSS", 0) for s in samples), default=0),
           "peakAnonymousRssBytes": max((s.get("RssAnon", 0) for s in samples), default=0)}
prefix.with_suffix(".proc.json").write_text(json.dumps(summary, indent=2))
print(json.dumps({k: v for k, v in summary.items() if k != "samples"}))
raise SystemExit(code)
