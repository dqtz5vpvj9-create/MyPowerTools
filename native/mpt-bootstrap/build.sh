#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_DIR="$SCRIPT_DIR/build"
OUT_DIR="$SCRIPT_DIR/bin/win-x64"
OUT_EXE="$OUT_DIR/mpt-bootstrap.exe"
MAX_SIZE_BYTES=50000

mkdir -p "$BUILD_DIR" "$OUT_DIR"

# 1. Resolve Zig compiler
if [[ -n "${ZIG:-}" && -x "${ZIG}" ]]; then
    ZIG_BIN="${ZIG}"
elif [[ -x "/home/chris/.local/bin/zig" ]]; then
    ZIG_BIN="/home/chris/.local/bin/zig"
elif command -v zig >/dev/null 2>&1; then
    ZIG_BIN="$(command -v zig)"
else
    echo "ERROR: zig compiler not found." >&2
    exit 1
fi

# 2. Build and execute host-native Linux C unit tests
if command -v gcc >/dev/null 2>&1; then
    HOST_CC="gcc"
elif command -v clang >/dev/null 2>&1; then
    HOST_CC="clang"
else
    HOST_CC="$ZIG_BIN cc"
fi

echo "[1/4] Compiling and running host-native C unit tests ($HOST_CC)..."
$HOST_CC -std=c11 -O2 -Wall -Wextra -Werror \
    "$SCRIPT_DIR/tests/test_config_and_version.c" \
    -o "$BUILD_DIR/test_config_and_version"
"$BUILD_DIR/test_config_and_version"

# 3. Compile Win32 resource (.rc + manifest + icon) and C source via zig cc
echo "[2/4] Compiling Win32 resources and C source via $ZIG_BIN..."
(
    cd "$SCRIPT_DIR"
    "$ZIG_BIN" rc /c 65001 /fo "$BUILD_DIR/mpt_bootstrap.res" -- "mpt_bootstrap.rc"
)

"$ZIG_BIN" cc -target x86_64-windows-gnu -std=c11 -Os -Oz \
    -Wall -Wextra -Werror \
    -fno-inline -fno-vectorize -fno-slp-vectorize -fno-unroll-loops \
    -fno-stack-protector -fno-ident \
    -fno-unwind-tables -fno-asynchronous-unwind-tables \
    -ffunction-sections -fdata-sections \
    -c "$SCRIPT_DIR/mpt_bootstrap.c" \
    -o "$BUILD_DIR/mpt_bootstrap.obj"

"$ZIG_BIN" cc -target x86_64-windows-gnu -Os -s -nostdlib \
    -Wl,--subsystem,windows \
    -Wl,--gc-sections \
    -Wl,-e,WinMainCRTStartup \
    "$BUILD_DIR/mpt_bootstrap.obj" \
    "$BUILD_DIR/mpt_bootstrap.res" \
    -lkernel32 -luser32 -lgdi32 -lcomctl32 -lwinhttp -lshell32 -ladvapi32 \
    -o "$OUT_EXE"

# 4. Enforce <= 50,000 byte size gate
echo "[3/4] Checking binary size gate (<= ${MAX_SIZE_BYTES} bytes)..."
ACTUAL_SIZE="$(stat -c%s "$OUT_EXE")"
echo "      mpt-bootstrap.exe size: ${ACTUAL_SIZE} bytes"
if (( ACTUAL_SIZE > MAX_SIZE_BYTES )); then
    echo "ERROR: $OUT_EXE is ${ACTUAL_SIZE} bytes, exceeding ${MAX_SIZE_BYTES}-byte limit!" >&2
    exit 1
fi

# 5. Enforce built-in Windows OS DLL-only import gate
echo "[4/4] Auditing PE DLL imports..."
OBJDUMP_BIN=""
for candidate in /usr/bin/llvm-objdump /usr/bin/llvm-objdump-21 /usr/bin/llvm-objdump-20 /usr/bin/llvm-objdump-18 llvm-objdump x86_64-w64-mingw32-objdump objdump; do
    if command -v "$candidate" >/dev/null 2>&1; then
        OBJDUMP_BIN="$(command -v "$candidate")"
        break
    fi
done

if [[ -z "$OBJDUMP_BIN" ]]; then
    echo "ERROR: llvm-objdump / objdump not found for PE import verification." >&2
    exit 1
fi

DLL_IMPORTS="$("$OBJDUMP_BIN" -p "$OUT_EXE" | awk '/DLL Name:/ {print $3}')"
if [[ -z "$DLL_IMPORTS" ]]; then
    echo "ERROR: Failed to parse DLL imports from $OUT_EXE" >&2
    exit 1
fi

ALLOWED_REGEX='^(kernel32|user32|gdi32|winhttp|shell32|advapi32|comctl32|msvcrt)\.dll$'
while IFS= read -r dll; do
    [[ -z "$dll" ]] && continue
    lower_dll="$(echo "$dll" | tr '[:upper:]' '[:lower:]')"
    echo "      Imported DLL: $dll"
    if [[ ! "$lower_dll" =~ $ALLOWED_REGEX ]]; then
        echo "ERROR: Forbidden non-OS DLL dependency detected: $dll" >&2
        exit 1
    fi
done <<< "$DLL_IMPORTS"

echo "SUCCESS: $OUT_EXE (${ACTUAL_SIZE} bytes) passed all build, size, DLL, and unit test gates."
