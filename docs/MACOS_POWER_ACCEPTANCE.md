# macOS power acceptance

This is a default release requirement for the low-memory, battery-device edition. A successful build or unit-test run does not satisfy it.

## Required scenarios

Use the complete installed Release app on a real Mac. Record hardware, OS, app version, power source and enabled tools. Let initialization settle, close the main window to the menu bar, and leave the machine/application undisturbed during the observation. Keep every enabled service running. Profile separately from acceptance sampling; profilers and builds perturb CPU usage.

Run `python3 scripts/measure-macos-idle.py --output /tmp/mpt-idle.json`. Defaults are a 60-second warm-up, 120-second observation, five-second sample spacing, and a combined CPU budget of 1% of one core. The script includes processes in the app bundle and their descendants, rejects missing standard background hosts or process churn, and exits nonzero above budget. It measures CPU and RSS only; RSS is not physical footprint. Explicit shortened diagnostic runs do not qualify as release acceptance.

Also exercise opening/closing/minimizing/restoring the window and switching through tools. Hidden or detached controls must not retain busy animations. Confirm that active progress indicators resume and still follow binding changes. Repeat the idle check after exercising the UI.

Verify live notification delivery against a reachable endpoint, persistence/deduplication, historical message details, manual refresh and recovery after a temporary connection failure. If the endpoint is unavailable, mark live delivery unverified. Normal online polling latency must remain unchanged. On macOS, repeated offline notification failures may back off to one minute; manual polling must remain immediate.

Record physical footprint per process with `vmmap -summary`, and check for sustained growth across repeated navigation. Record timer wake-ups and disk/network activity with Instruments/System Trace or available system tooling. No watts, battery-hours or wake-up claim may be inferred from CPU alone. Missing energy/wake-up evidence must be recorded as an incomplete part of acceptance.

## Function-level diagnosis

Use Instruments CPU Profiler/Time Profiler to attribute active CPU cost and System Trace to investigate wake-ups. Full Xcode supplies Instruments; Command Line Tools alone do not. For managed functions, retain a dotnet-trace capture and a native sample capture as complementary evidence. Sampled thread time includes waiting and is not on-CPU attribution. Label observed call stacks separately from measured per-process CPU. Do not optimize semaphore waits merely because they dominate a thread-time report.

Report every result, including failed budgets and startup/activity-contaminated intervals. A package that fails any required scenario remains a development/acceptance candidate, not a power-qualified release. The measurement script is an executable CPU gate; the remaining scenarios require explicit evidence and cannot be satisfied by its exit code alone.
