# Log tail correctness and I/O regression

This dependency-free console harness source-links the production `LogRouter` and
`MptLogRedactor`. Run the executable explicitly; it is not an xUnit discovery count.
`--list` prints its named regression groups. The default run contains 24 groups,
including 48,848 additional reference-equivalence and deterministic race comparisons.

```sh
dotnet build tests/LogTail.Memory.Regression/LogTail.Memory.Regression.csproj \
  -c Release --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet tests/LogTail.Memory.Regression/bin/Release/net10.0/LogTail.Memory.Regression.dll
```

The tests compare selected physical lines before blank/null filtering and JSON
parsing. Coverage includes negative, zero, oversized and `int.MaxValue` limits;
missing files; malformed selected/unselected JSON; exhaustive small CR/LF layouts;
seeded arbitrary bytes and Unicode; UTF-8/16/32 BOMs; embedded U+FEFF; partial BOMs;
CRLF split across an 8 KiB reverse block; multibyte text at block edges; one-byte
short reads; a 2 MiB unbroken line; deterministic concurrent append/truncation;
real-file truncation; and rotation while retaining the opened handle.

The short-read oracle uses the same short-read stream shape and `Encoding.UTF8`
as the optimized reader's start-of-file fallback. This compares against standard
StreamReader behavior, without claiming additional decoding guarantees beyond it.
The public JSON-outcome cases also compare directly against `File.ReadLines`.

## Alternating-process benchmark

Use the phase-one fixture: 100,000 JSONL records numbered 0 through 99,999, each
with a 1,000-character message. Both modes assert that the result is exactly the
200 records numbered 99,800 through 99,999.

```sh
python tests/LogTail.Memory.Regression/measure_io.py \
  path/to/LogTail.Memory.Regression.dll fixture-directory output.json
```

The script runs five alternating process pairs. `full-scan` reproduces the former
sequential `ReadLine`/`TakeLast` selection; `seek-tail` calls the actual production
helper. Both share result materialization and JSON parsing. The full-scan mode
retains the original default 4 KiB FileStream and 1 KiB StreamReader buffers. The
new path has an 8 KiB reverse buffer, 4 KiB StreamReader buffer, and disables extra
FileStream buffering; measured timing includes these changes. JIT/startup effects
inside the measured operation and OS cache effects are not removed.

`actualBytesRead` counts bytes returned by the wrapped stream's Read methods.
`readCalls` counts those method calls, including EOF reads, not OS syscalls.
These are application read-I/O measurements, not physical disk traffic or page
cache misses. Peak working set is emitted for context only; this benchmark is
not a whole-app memory, idle-power, Windows GUI, or macOS acceptance test.

## Complexity and live-file limits

For unchanged UTF-8 files the reader locates the requested tail using backward
8 KiB blocks, then decodes only that suffix. Read volume scales with the bytes in
the requested physical lines, with one block of seek overscan and a four-byte BOM
probe; the suffix is generally read twice. This is **not a fixed-byte bound**:
large lines, requesting most/all lines, or continuous concurrent appends can still
require unbounded work. The 2 MiB line test verifies no arbitrary truncation.
UTF-16/32 BOMs retain a full streaming scan, preserving StreamReader decoding.

The reader keeps one shared read/write/delete file handle so rotation cannot mix
old and new paths. Appends encountered before EOF are included and re-tailed.
Observed shrinking invalidates the seek offset and causes sequential fallback.
There is no atomic snapshot guarantee: in-place rewrite or truncate-and-regrow
that evades the length checks can race, as with the old sequential live reader.
Nonpositive limits return empty without reading file bytes. Log history,
redaction, serialization, and selected malformed JSON exception behavior remain
unchanged. Allowing shared append/delete intentionally lets the reader coexist
with append and rotation rather than preventing those operations on Windows.
