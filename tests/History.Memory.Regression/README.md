# Bounded runtime history regression

Standalone deterministic tests link `CommandHistory`, `NotificationCenter`, their
shared bounded store and real Abstractions contracts. No native services, devices,
accounts or endpoints are started. The executable supports the repository's
`MPT_TEST_CASES_V1` listing format.

```sh
AVALONIA_TELEMETRY_OPTOUT=1 POWERSHELL_TELEMETRY_OPTOUT=1 \
  dotnet run --project tests/History.Memory.Regression -c Release \
  --disable-build-servers -p:BuildInParallel=false -p:UseSharedCompilation=false
```

For serial builds use `dotnet build ... -m:1 --disable-build-servers` and then run
the compiled DLL. `--list` lists cases. `--benchmark` runs 5,000 distinct command
outputs and notification bodies per buffer, taking checkpoints. The bounded-only
`--benchmark-sustained` extends this to 50,000 pairs. The probe uses forced GC only
to compare retained live objects; product code never forces GC.

To compare an original source snapshot, set `-p:HistoryBaseline=true` and
`-p:HistorySource=/absolute/path/to/before`, providing the original
`CommandHistory.cs` and `NotificationCenter.cs` (including the allocation-free Count
accessors). The baseline binary supports benchmarking, not retention assertions.
Build baseline and after binaries to separate output directories, sequentially.

```sh
python3 tests/History.Memory.Regression/measure_memory.py \
  /path/to/History.Memory.Regression.dll /path/to/evidence/prefix
python3 tests/History.Memory.Regression/measure_memory.py \
  /path/to/bounded/History.Memory.Regression.dll /path/to/evidence/sustained --sustained
```

The sampler writes per-checkpoint `.jsonl`, independent `.proc.json` RSS samples,
and `.stderr.log`. It does not retain produced records outside the production
stores. Compare identical 5,000-pair runs before/after; report the bounded-only
50,000-pair result separately. This synthetic fixture verifies rolling retention,
not attribution of a real user's process memory growth. See
[`RUNTIME_HISTORY_RETENTION.md`](../../docs/RUNTIME_HISTORY_RETENTION.md) for limits,
accounting, unread behavior and the primary Android references.
