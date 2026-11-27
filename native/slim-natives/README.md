# Slim Win-x64 Native Libraries (SkiaSharp & HarfBuzzSharp)

Reproducible cross-compilation scripts that build ABI-compatible, size-optimized `win-x64` native DLLs for MyPowerTools' composable `shared-ui/` using `zig cc`/`zig c++` (`-target x86_64-windows-gnu`) + `ninja` on Linux.

## Measured Results (`win-x64`)

| Library | Official NuGet | Self-Compiled Slim | Delta | Exports Parity | External Runtime DLLs |
|---|---:|---:|---:|---|---|
| `libSkiaSharp.dll` (`v3.119.4`) | 11.09 MB | **4.10 MB** | **-63% (-7.0 MB)** | 924 / 924 (0 missing) | None (OS `KERNEL32`/`USER32`/UCRT only) |
| `libHarfBuzzSharp.dll` (`8.3.1`) | 1.73 MB | **774 KB** | **-56% (-1.0 MB)** | 319 / 319 `HarfBuzzSharp.dll` P/Invokes (0 missing) | None (OS `KERNEL32`/UCRT only) |

### What Was Disabled vs. Kept

- **Skia (`build-skia-win-x64.py`)**:
  - **Kept**: CPU Rasterizer (`SK_ENABLE_OPTIMIZE_SIZE` + `SK_FORCE_AAA`), Ganesh OpenGL/WGL (`SK_GL`), DirectWrite font manager (`SK_TYPEFACE_FACTORY_DIRECTWRITE`), PNG + JPEG decode/encode (`libpng` + `libjpeg-turbo` + `zlib`), SkSL, Effects/ImageFilters, 100% of the `SkiaSharp 3.119.4` C ABI exports.
  - **Disabled**: Vulkan, Direct3D 12, Dawn/WebGPU, Graphite, PDF, XPS, SVG DOM, Skottie/Lottie, WebP, Wuffs/GIF, DNG/RAW/PIEX, Expat/XML, ICU.
- **HarfBuzz (`build-harfbuzz-win-x64.sh`)**:
  - **Kept**: Full OpenType shaping (`HAVE_OT`), Unicode/CJK/Latin/Color/Meta, 100% of `HarfBuzzSharp.dll` P/Invokes.
  - **Disabled**: Font subsetter (`harfbuzz-subset.cc`, `HB_NO_SUBSET_CFF`, `HB_NO_SUBSET_LAYOUT`), `HB_NO_DRAW`, `HB_NO_PAINT`, `HB_NO_STYLE`, `HB_NO_FALLBACK_SHAPE`.

## Usage

```bash
# 1. Build slim win-x64 DLLs into native/slim-natives/bin/win-x64/
./native/slim-natives/build-harfbuzz-win-x64.sh
python3 ./native/slim-natives/build-skia-win-x64.py

# 2. Package portable tools (auto-overlays native/slim-natives/bin/win-x64/ if present)
python3 scripts/build-portable-tools.py \
  --bootstrap-exe native/mpt-bootstrap/bin/win-x64/mpt-bootstrap.exe \
  --slim-natives-dir native/slim-natives/bin/win-x64 \
  --configuration Release --rid win-x64 --output-dir artifacts/portable
```
