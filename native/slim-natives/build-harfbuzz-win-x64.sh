#!/usr/bin/env bash
set -euo pipefail

export PATH="$HOME/.local/bin:$PATH"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="${HARFBUZZ_SRC_DIR:-/mnt/cache/slim-natives-build/harfbuzz}"
OUT_DLL="${1:-$SCRIPT_DIR/bin/win-x64/libHarfBuzzSharp.dll}"

if [ ! -f "$SRC_DIR/src/harfbuzz.cc" ]; then
  mkdir -p "$(dirname "$SRC_DIR")"
  git clone --depth 1 --branch 8.3.1 https://github.com/harfbuzz/harfbuzz.git "$SRC_DIR"
fi

mkdir -p "$(dirname "$OUT_DLL")"
zig c++ -target x86_64-windows-gnu -std=c++17 -Oz -ffunction-sections -fdata-sections \
  -fno-exceptions -fno-rtti -fno-threadsafe-statics -Wno-nullability-completeness \
  -DNDEBUG -DHAVE_OT \
  -DHB_EXTERN="__declspec(dllexport)" \
  -DHB_NO_FALLBACK_SHAPE \
  -DHB_NO_DRAW -DHB_NO_PAINT -DHB_NO_STYLE \
  -DHB_NO_SUBSET_CFF -DHB_NO_SUBSET_LAYOUT \
  -shared -s -Wl,--gc-sections -Wl,--strip-all \
  "$SRC_DIR/src/harfbuzz.cc" \
  -o "$OUT_DLL"

ls -lh "$OUT_DLL"
