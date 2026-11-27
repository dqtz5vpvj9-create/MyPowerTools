# Slim Win-x64 & Android ARM64 Native Libraries (SkiaSharp & HarfBuzzSharp)

Reproducible cross-compilation scripts that build ABI-compatible, size-optimized native libraries for MyPowerTools using `zig cc`/`zig c++` (Windows) and Android NDK r27 (Android ARM64, 16 KB page-aligned) + `ninja` on Linux.

## Windows x64 (`win-x64`)

| Library | Official NuGet | Self-Compiled Slim | Delta | Exports Parity | External Runtime DLLs |
|---|---:|---:|---:|---|---|
| `libSkiaSharp.dll` (`v3.119.4`) | 11.09 MB | **4.10 MB** | **-63% (-7.0 MB)** | 924 / 924 (0 missing) | None (OS `KERNEL32`/`USER32`/UCRT only) |
| `libHarfBuzzSharp.dll` (`8.3.1`) | 1.73 MB | **774 KB** | **-56% (-1.0 MB)** | 319 / 319 (0 missing) | None (OS `KERNEL32`/UCRT only) |

```bash
./native/slim-natives/build-harfbuzz-win-x64.sh
python3 ./native/slim-natives/build-skia-win-x64.py
```

## Android ARM64 (`android-arm64`, 16 KB Page-Aligned)

| Library | Official NuGet | Self-Compiled Slim | Delta | Exports Parity | LOAD Align |
|---|---:|---:|---:|---|---|
| `libSkiaSharp.so` (`v3.119.4`) | 8.61 MiB | **5.19 MB** | **-42%** | 924 / 924 (0 missing) | `0x4000` (16 KB) |
| `libHarfBuzzSharp.so` (`8.3.1`) | 2.65 MiB | **800 KB** | **-71%** | 319 / 319 (0 missing) | `0x4000` (16 KB) |

```bash
./native/slim-natives/build-harfbuzz-android-arm64.sh
python3 ./native/slim-natives/build-skia-android-arm64.py
```
