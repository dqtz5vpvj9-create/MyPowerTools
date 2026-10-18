"""Run alternating isolated-process read-I/O benchmarks on the phase-one fixture.

Usage: python measure_io.py path/to/LogTail.Memory.Regression.dll fixture_dir output.json
Requires dotnet on PATH; each fixture must have 100,000 records numbered 0..99,999.
Counts bytes returned by Stream.Read, not physical disk traffic or OS page-cache misses.
"""
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys

binary, fixture, output = sys.argv[1:]
env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", AVALONIA_TELEMETRY_OPTOUT="1", POWERSHELL_TELEMETRY_OPTOUT="1")
samples = []
for mode in ["full-scan", "seek-tail"] * 5:
    result = subprocess.run(["dotnet", binary, "--benchmark-io", fixture, mode], env=env, text=True, capture_output=True, check=True)
    row = json.loads(result.stdout)
    samples.append(row)
    print(json.dumps(row), flush=True)
summary = {}
for mode in ["full-scan", "seek-tail"]:
    rows = [row for row in samples if row["mode"] == mode]
    summary[mode] = {"medianElapsedMs": statistics.median(row["elapsedMs"] for row in rows),
                     "actualBytesRead": sorted({row["actualBytesRead"] for row in rows}),
                     "fileBytes": rows[0]["fileBytes"]}
summary["readByteReductionPercent"] = 100 * (1 - summary["seek-tail"]["actualBytesRead"][0] / summary["full-scan"]["actualBytesRead"][0])
summary["medianTimeReductionPercent"] = 100 * (1 - summary["seek-tail"]["medianElapsedMs"] / summary["full-scan"]["medianElapsedMs"])
Path(output).write_text(json.dumps({"measurement": "Stream.Read return bytes; not physical disk traffic", "samples": samples, "summary": summary}, indent=2) + "\n")
print(json.dumps(summary, indent=2))
