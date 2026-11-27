#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NDK_BIN="${ANDROID_NDK_BIN:-/android/sdk/ndk/27.0.12077973/toolchains/llvm/prebuilt/linux-x86_64/bin}"
CXX="$NDK_BIN/aarch64-linux-android24-clang++"
SRC_DIR="${HARFBUZZ_SRC_DIR:-/mnt/cache/slim-natives-build/harfbuzz}"
OUT_SO="${1:-$SCRIPT_DIR/bin/android-arm64/libHarfBuzzSharp.so}"

if [ ! -f "$SRC_DIR/src/harfbuzz.cc" ]; then
  mkdir -p "$(dirname "$SRC_DIR")"
  git clone --depth 1 --branch 8.3.1 https://github.com/harfbuzz/harfbuzz.git "$SRC_DIR"
fi

mkdir -p "$(dirname "$OUT_SO")"
"$CXX" -std=c++17 -Oz -fPIC -fvisibility=hidden \
  -ffunction-sections -fdata-sections \
  -fno-exceptions -fno-rtti -fno-threadsafe-statics \
  -DNDEBUG -DHAVE_OT -DHAVE_PTHREAD \
  -DHB_EXTERN='__attribute__((visibility("default")))' \
  -DHB_NO_FALLBACK_SHAPE \
  -DHB_NO_DRAW -DHB_NO_PAINT -DHB_NO_STYLE \
  -DHB_NO_SUBSET_CFF -DHB_NO_SUBSET_LAYOUT \
  -shared -static-libstdc++ \
  -Wl,-soname,libHarfBuzzSharp.so \
  -Wl,-z,max-page-size=16384 -Wl,--gc-sections -Wl,--strip-all \
  "$SRC_DIR/src/harfbuzz.cc" \
  -lm -ldl \
  -o "$OUT_SO"

ls -lh "$OUT_SO"
