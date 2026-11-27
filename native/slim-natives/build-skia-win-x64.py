#!/usr/bin/env python3
"""Cross-compile a slim, ABI-compatible win-x64 libSkiaSharp.dll (SkiaSharp v3.119.4)
using zig cc/c++ + ninja on Linux.

Keeps: CPU Raster, Ganesh OpenGL/WGL, DirectWrite fonts, PNG + JPEG codecs/encoders,
       SkSL, Effects/ImageFilters, full SkiaSharp C ABI.
Disables: Vulkan, Direct3D 12, Dawn, Graphite, PDF, XPS, SVG DOM, Skottie,
          WebP, Wuffs/GIF, DNG/RAW/PIEX, Expat/XML, ICU.
"""
from __future__ import annotations

import os
import re
import subprocess
import sys
from pathlib import Path

SKIA_DIR = Path(os.environ.get("SKIA_SRC_DIR", "/mnt/cache/slim-natives-build/SkiaSharp/externals/skia"))
BUILD_DIR = Path(os.environ.get("SKIA_BUILD_DIR", "/mnt/cache/slim-natives-build/skia-win-x64-build"))
REPO_ROOT = Path(__file__).resolve().parents[2]
OUT_DLL = Path(sys.argv[1]) if len(sys.argv) > 1 else (REPO_ROOT / "native/slim-natives/bin/win-x64/libSkiaSharp.dll")


def parse_gni_lists(gni_path: Path) -> dict[str, list[str]]:
    text = gni_path.read_text(encoding="utf-8")
    # Strip line comments
    lines = []
    for line in text.splitlines():
        if "#" in line:
            # Remove comments outside quotes
            in_q = False
            out = []
            for ch in line:
                if ch == '"':
                    in_q = not in_q
                if ch == "#" and not in_q:
                    break
                out.append(ch)
            line = "".join(out)
        lines.append(line)
    clean = "\n".join(lines)

    # Extract simple path vars like _src = get_path_info("../src", "abspath")
    var_map = {
        "$_src": "src",
        "$_include": "include",
        "$_modules": "modules",
        "$_tests": "tests",
        "$_third_party": "third_party",
    }

    lists: dict[str, list[str]] = {}
    # Match name = [ ... ] and name += [ ... ]
    pattern = re.compile(r"^([a-zA-Z0-9_]+)\s*(\+?=)\s*\[(.*?)\]", re.M | re.S)
    for m in pattern.finditer(clean):
        name, op, body = m.group(1), m.group(2), m.group(3)
        items = re.findall(r'"([^"]+)"', body)
        resolved = []
        for it in items:
            for k, v in var_map.items():
                if it.startswith(k + "/"):
                    it = v + it[len(k):]
            resolved.append(it)
        if op == "+=" and name in lists:
            lists[name].extend(resolved)
        else:
            lists[name] = resolved
    return lists


def filter_sources(items: list[str], exclude_substrings: tuple[str, ...] = ()) -> list[str]:
    out = []
    for it in items:
        if not it.endswith((".c", ".cc", ".cpp")):
            continue
        if any(ex in it for ex in exclude_substrings):
            continue
        if (SKIA_DIR / it).exists():
            out.append(it)
    return out


SLIM_C_STUBS = r'''
// Slim ABI stubs for disabled optional Skia modules (PDF, XPS, SVG, Skottie, SkResources, SkSG).
// Preserves 100% SkiaSharp 3.119.4 C ABI exports without linking PDF/XPS/SVG/Skottie/XML/Expat.
#include "include/c/sk_document.h"
#include "include/c/sk_svg.h"
#include "include/c/skottie_animation.h"
#include "include/c/skresources_resource_provider.h"
#include "include/c/sksg_invalidation_controller.h"

extern "C" {

void sk_document_unref(sk_document_t*) {}
sk_document_t* sk_document_create_pdf_from_stream(sk_wstream_t*) { return nullptr; }
sk_document_t* sk_document_create_pdf_from_stream_with_metadata(sk_wstream_t*, const sk_document_pdf_metadata_t*) { return nullptr; }
sk_document_t* sk_document_create_xps_from_stream(sk_wstream_t*, float) { return nullptr; }
sk_canvas_t* sk_document_begin_page(sk_document_t*, float, float, const sk_rect_t*) { return nullptr; }
void sk_document_end_page(sk_document_t*) {}
void sk_document_close(sk_document_t*) {}
void sk_document_abort(sk_document_t*) {}

sk_canvas_t* sk_svgcanvas_create_with_stream(const sk_rect_t*, sk_wstream_t*) { return nullptr; }

skottie_animation_t* skottie_animation_make_from_string(const char*, size_t) { return nullptr; }
skottie_animation_t* skottie_animation_make_from_data(const char*, size_t) { return nullptr; }
skottie_animation_t* skottie_animation_make_from_stream(sk_stream_t*) { return nullptr; }
skottie_animation_t* skottie_animation_make_from_file(const char*) { return nullptr; }
void skottie_animation_ref(skottie_animation_t*) {}
void skottie_animation_unref(skottie_animation_t*) {}
void skottie_animation_delete(skottie_animation_t*) {}
void skottie_animation_render(skottie_animation_t*, sk_canvas_t*, sk_rect_t*) {}
void skottie_animation_render_with_flags(skottie_animation_t*, sk_canvas_t*, sk_rect_t*, skottie_animation_renderflags_t) {}
void skottie_animation_seek(skottie_animation_t*, float, sksg_invalidation_controller_t*) {}
void skottie_animation_seek_frame(skottie_animation_t*, float, sksg_invalidation_controller_t*) {}
void skottie_animation_seek_frame_time(skottie_animation_t*, float, sksg_invalidation_controller_t*) {}
double skottie_animation_get_duration(skottie_animation_t*) { return 0.0; }
double skottie_animation_get_fps(skottie_animation_t*) { return 0.0; }
double skottie_animation_get_in_point(skottie_animation_t*) { return 0.0; }
double skottie_animation_get_out_point(skottie_animation_t*) { return 0.0; }
void skottie_animation_get_version(skottie_animation_t*, sk_string_t*) {}
void skottie_animation_get_size(skottie_animation_t*, sk_size_t* size) { if (size) { size->w = 0; size->h = 0; } }

skottie_animation_builder_t* skottie_animation_builder_new(skottie_animation_builder_flags_t) { return nullptr; }
void skottie_animation_builder_delete(skottie_animation_builder_t*) {}
void skottie_animation_builder_get_stats(skottie_animation_builder_t*, skottie_animation_builder_stats_t*) {}
void skottie_animation_builder_set_resource_provider(skottie_animation_builder_t*, skottie_resource_provider_t*) {}
void skottie_animation_builder_set_font_manager(skottie_animation_builder_t*, sk_fontmgr_t*) {}
skottie_animation_t* skottie_animation_builder_make_from_stream(skottie_animation_builder_t*, sk_stream_t*) { return nullptr; }
skottie_animation_t* skottie_animation_builder_make_from_file(skottie_animation_builder_t*, const char*) { return nullptr; }
skottie_animation_t* skottie_animation_builder_make_from_string(skottie_animation_builder_t*, const char*, size_t) { return nullptr; }
skottie_animation_t* skottie_animation_builder_make_from_data(skottie_animation_builder_t*, const char*, size_t) { return nullptr; }

void skresources_resource_provider_ref(skresources_resource_provider_t*) {}
void skresources_resource_provider_unref(skresources_resource_provider_t*) {}
void skresources_resource_provider_delete(skresources_resource_provider_t*) {}
sk_data_t* skresources_resource_provider_load(skresources_resource_provider_t*, const char*, const char*) { return nullptr; }
skresources_image_asset_t* skresources_resource_provider_load_image_asset(skresources_resource_provider_t*, const char*, const char*, const char*) { return nullptr; }
skresources_external_track_asset_t* skresources_resource_provider_load_audio_asset(skresources_resource_provider_t*, const char*, const char*, const char*) { return nullptr; }
sk_typeface_t* skresources_resource_provider_load_typeface(skresources_resource_provider_t*, const char*, const char*) { return nullptr; }
skresources_resource_provider_t* skresources_file_resource_provider_make(sk_string_t*, bool) { return nullptr; }
skresources_resource_provider_t* skresources_caching_resource_provider_proxy_make(skresources_resource_provider_t*) { return nullptr; }
skresources_resource_provider_t* skresources_data_uri_resource_provider_proxy_make(skresources_resource_provider_t*, bool) { return nullptr; }

sksg_invalidation_controller_t* sksg_invalidation_controller_new(void) { return nullptr; }
void sksg_invalidation_controller_delete(sksg_invalidation_controller_t*) {}
void sksg_invalidation_controller_inval(sksg_invalidation_controller_t*, sk_rect_t*, sk_matrix_t*) {}
void sksg_invalidation_controller_get_bounds(sksg_invalidation_controller_t*, sk_rect_t* bounds) { if (bounds) { bounds->left = bounds->top = bounds->right = bounds->bottom = 0; } }
void sksg_invalidation_controller_begin(sksg_invalidation_controller_t*) {}
void sksg_invalidation_controller_end(sksg_invalidation_controller_t*) {}
void sksg_invalidation_controller_reset(sksg_invalidation_controller_t*) {}

}
'''



def ensure_skia_sources() -> None:
    # Auto-fetch pinned mono/skia (SkiaSharp v3.119.4) + zlib/libpng/libjpeg-turbo if missing.
    def fetch_git(dest: Path, url: str, rev: str) -> None:
        if (dest / ".git").exists():
            return
        dest.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "-C", str(dest), "init", "-q"], check=True)
        subprocess.run(["git", "-C", str(dest), "remote", "add", "origin", url], check=True)
        subprocess.run(["git", "-C", str(dest), "fetch", "--depth", "1", "-q", "origin", rev], check=True)
        subprocess.run(["git", "-C", str(dest), "checkout", "-q", "FETCH_HEAD"], check=True)

    fetch_git(SKIA_DIR, "https://github.com/mono/skia.git", "7dbfc07dd33181f84e0958afb7ee805c6c769f0b")
    ext = SKIA_DIR / "third_party/externals"
    fetch_git(ext / "zlib", "https://chromium.googlesource.com/chromium/src/third_party/zlib", "e432200a7931de1b0b1b50201aac8a46d9380cb2")
    fetch_git(ext / "libpng", "https://skia.googlesource.com/third_party/libpng.git", "3061454d980de7d53608f594194cfac722721d2a")
    fetch_git(ext / "libjpeg-turbo", "https://chromium.googlesource.com/chromium/deps/libjpeg_turbo.git", "927aabfcd26897abb9776ecf2a6c38ea5bb52ab6")

def main() -> None:
    ensure_skia_sources()
    BUILD_DIR.mkdir(parents=True, exist_ok=True)
    OUT_DLL.parent.mkdir(parents=True, exist_ok=True)

    core = parse_gni_lists(SKIA_DIR / "gn/core.gni")
    effects = parse_gni_lists(SKIA_DIR / "gn/effects.gni")
    filters = parse_gni_lists(SKIA_DIR / "gn/effects_imagefilters.gni")
    utils = parse_gni_lists(SKIA_DIR / "gn/utils.gni")
    codec = parse_gni_lists(SKIA_DIR / "gn/codec.gni")
    sksl = parse_gni_lists(SKIA_DIR / "gn/sksl.gni")
    gpu = parse_gni_lists(SKIA_DIR / "gn/gpu.gni")

    disabled_c = (
        "src/c/sk_document.cpp",
        "src/c/sk_svg.cpp",
        "src/c/skottie_animation.cpp",
        "src/c/skresources_resource_provider.cpp",
        "src/c/sksg_invalidation_controller.cpp",
    )

    skia_srcs: list[str] = []
    for lst in [
        core.get("skia_core_sources", []),
        core.get("skia_pathops_sources", []),
        core.get("skia_encode_srcs", []),
        core.get("skia_encode_png_srcs", []),
        core.get("skia_encode_jpeg_srcs", []),
        core.get("skia_no_encode_webp_srcs", []),
        effects.get("skia_effects_sources", []),
        effects.get("skia_colorfilters_sources", []),
        filters.get("skia_effects_imagefilter_sources", []),
        utils.get("skia_utils_private", []),
        utils.get("skia_utils_chromium", []),
        utils.get("skia_utils_gpu", []),
        codec.get("skia_codec_core", []),
        codec.get("skia_codec_decode_bmp", []),
        sksl.get("skia_sksl_sources", []),
        sksl.get("skia_sksl_gpu_sources", []),
        gpu.get("skia_shared_gpu_sources", []),
        gpu.get("skia_ganesh_private", []),
        gpu.get("skia_gpu_gl_private", []),
    ]:
        skia_srcs.extend(filter_sources(lst, exclude_substrings=disabled_c))

    extra_skia = [
        # DirectWrite font manager
        "src/fonts/SkFontMgr_indirect.cpp",
        "src/ports/SkFontMgr_win_dw.cpp",
        "src/ports/SkScalerContext_win_dw.cpp",
        "src/ports/SkTypeface_win_dw.cpp",
        "src/ports/SkFontMgr_win_dw_factory.cpp",
        # PNG + JPEG decoders
        "src/codec/SkPngCodec.cpp",
        "src/codec/SkIcoCodec.cpp",
        "src/codec/SkJpegCodec.cpp",
        "src/codec/SkJpegDecoderMgr.cpp",
        "src/codec/SkJpegSourceMgr.cpp",
        "src/codec/SkJpegUtility.cpp",
        "src/codec/SkAndroidCodec.cpp",
        "src/codec/SkAndroidCodecAdapter.cpp",
        "src/codec/SkEncodedInfo.cpp",
        "src/codec/SkParseEncodedOrigin.cpp",
        "src/codec/SkSampledCodec.cpp",
        # Win32 GL + OS ports
        "src/gpu/ganesh/gl/win/GrGLMakeNativeInterface_win.cpp",
        "src/android/SkAndroidFrameworkUtils.cpp",
        "src/android/SkAnimatedImage.cpp",
        "src/ports/SkDiscardableMemory_none.cpp",
        "src/ports/SkGlobalInitialization_default.cpp",
        "src/ports/SkMemory_malloc.cpp",
        "src/ports/SkOSFile_stdio.cpp",
        "src/ports/SkDebug_win.cpp",
        "src/ports/SkOSFile_win.cpp",
        "src/ports/SkOSLibrary_win.cpp",
        "src/sfnt/SkOTTable_name.cpp",
        "src/sfnt/SkOTUtils.cpp",
        # skcms
        "modules/skcms/skcms.cc",
        # Xamarin bindings
        "src/xamarin/sk_compatpaint.cpp",
        "src/xamarin/sk_manageddrawable.cpp",
        "src/xamarin/sk_managedstream.cpp",
        "src/xamarin/sk_managedtracememorydump.cpp",
        "src/xamarin/sk_xamarin.cpp",
        "src/xamarin/SkiaKeeper.c",
        "src/xamarin/SkCompatPaint.cpp",
        "src/xamarin/SkManagedDrawable.cpp",
        "src/xamarin/SkManagedStream.cpp",
        "src/xamarin/SkManagedTraceMemoryDump.cpp",
    ]
    skia_srcs.extend(filter_sources(extra_skia))

    # Deduplicate preserving order
    seen = set()
    uniq_skia = []
    for s in skia_srcs:
        if s not in seen:
            seen.add(s)
            uniq_skia.append(s)

    # Third-party pure C libraries: zlib (no SIMD), libpng, libjpeg-turbo (no SIMD)
    zlib_srcs = [
        f"third_party/externals/zlib/{f}"
        for f in [
            "adler32.c", "compress.c", "cpu_features.c", "crc32.c", "deflate.c",
            "infback.c", "inffast.c", "inflate.c", "inftrees.c", "trees.c",
            "uncompr.c", "zutil.c",
        ]
    ]
    libpng_srcs = [
        f"third_party/externals/libpng/{f}"
        for f in [
            "png.c", "pngerror.c", "pngget.c", "pngmem.c", "pngpread.c",
            "pngread.c", "pngrio.c", "pngrtran.c", "pngrutil.c", "pngset.c",
            "pngtrans.c", "pngwio.c", "pngwrite.c", "pngwtran.c", "pngwutil.c",
            "intel/filter_sse2_intrinsics.c", "intel/intel_init.c",
        ]
    ]
    libjpeg_srcs = [
        f"third_party/externals/libjpeg-turbo/{f}"
        for f in [
            "jaricom.c", "jcapimin.c", "jcapistd.c", "jcarith.c", "jccoefct.c",
            "jccolor.c", "jcdctmgr.c", "jchuff.c", "jcinit.c", "jcmainct.c",
            "jcmarker.c", "jcmaster.c", "jcomapi.c", "jcparam.c", "jcphuff.c",
            "jcprepct.c", "jcsample.c", "jdapimin.c", "jdapistd.c", "jdarith.c",
            "jdcoefct.c", "jdcolor.c", "jddctmgr.c", "jdhuff.c", "jdinput.c",
            "jdmainct.c", "jdmarker.c", "jdmaster.c", "jdmerge.c", "jdphuff.c",
            "jdpostct.c", "jdsample.c", "jerror.c", "jfdctflt.c", "jfdctfst.c",
            "jfdctint.c", "jidctflt.c", "jidctfst.c", "jidctint.c", "jidctred.c",
            "jmemmgr.c", "jmemnobs.c", "jpeg_nbits_table.c", "jquant1.c",
            "jquant2.c", "jutils.c", "jsimd_none.c",
        ]
    ]

    stubs_cpp = BUILD_DIR / "slim_c_stubs.cpp"
    stubs_cpp.write_text(SLIM_C_STUBS, encoding="utf-8")

    # Write build.ninja
    common_flags = (
        "-target x86_64-windows-gnu -Oz -ffunction-sections -fdata-sections "
        "-Wno-nullability-completeness -w "
        "-DNDEBUG -DNOMINMAX -DWIN32_LEAN_AND_MEAN -DUNICODE -D_UNICODE"
    )
    skia_defines = (
        "-DSK_RELEASE -DSKIA_C_DLL -DSKIA_IMPLEMENTATION=1 "
        "-DSK_GANESH -DSK_GL "
        "-DSK_CODEC_DECODES_PNG -DSK_CODEC_DECODES_JPEG "
        "-DSK_TYPEFACE_FACTORY_DIRECTWRITE "
        "-DSK_GAMMA_APPLY_TO_A8 -DSK_ENABLE_OPTIMIZE_SIZE -DSK_FORCE_AAA "
        "-DSK_DISABLE_TRACING -DSK_DISABLE_EFFECT_DESERIALIZATION '-DSK_ALWAYS_INLINE=inline __attribute__((always_inline))' -fms-extensions"
    )
    skia_incs = (
        f"-I{SKIA_DIR} "
        f"-I{SKIA_DIR}/third_party/libpng -I{SKIA_DIR}/third_party/externals/libpng "
        f"-I{SKIA_DIR}/third_party/libjpeg-turbo -I{SKIA_DIR}/third_party/externals/libjpeg-turbo "
        f"-I{SKIA_DIR}/third_party/externals/zlib"
    )

    zig = os.path.expanduser("~/.local/bin/zig")
    ninja_lines = [
        f"zig = {zig}",
        "",
        "rule cxx",
        f"  command = $zig c++ {common_flags} -std=c++17 -fno-exceptions -fno-rtti -fno-threadsafe-statics {skia_defines} {skia_incs} $extra -c $in -o $out",
        "  description = CXX $out",
        "",
        "rule cc",
        f"  command = $zig cc {common_flags} {skia_defines} {skia_incs} $extra -c $in -o $out",
        "  description = CC $out",
        "",
        "rule third_party_cc",
        f"  command = $zig cc {common_flags} $extra -c $in -o $out",
        "  description = 3P_CC $out",
        "",
        "rule link_dll",
        f"  command = $zig c++ -target x86_64-windows-gnu -shared -s -Wl,--gc-sections -Wl,--strip-all @$out.rsp -o $out -lole32 -loleaut32 -luser32 -lgdi32 -lfontsub -lusp10",
        "  rspfile = $out.rsp",
        "  rspfile_content = $in",
        "  description = LINK $out",
        "",
    ]

    objs: list[str] = []

    def add_build(src_path: Path, rule: str, extra: str = "") -> None:
        rel = src_path.relative_to(SKIA_DIR) if str(src_path).startswith(str(SKIA_DIR)) else Path(src_path.name)
        obj = str(BUILD_DIR / "obj" / (str(rel).replace("/", "__") + ".o"))
        objs.append(obj)
        ninja_lines.append(f"build {obj}: {rule} {src_path}")
        if extra:
            ninja_lines.append(f"  extra = {extra}")

    for s in uniq_skia:
        p = SKIA_DIR / s
        rule = "cc" if s.endswith(".c") else "cxx"
        extra = ""
        if s.startswith("src/c/"):
            extra = "'-DSK_C_API=__declspec(dllexport)'"
        elif s == "modules/skcms/skcms.cc":
            extra = "-DSKCMS_PORTABLE"
        elif s == "src/ports/SkScalerContext_win_dw.cpp":
            # MinGW-w64 dwrite_3.h omits MSVC inline 1-arg IDWritePaintReader helper overloads;
            # disable Win11 ZN COLRv1 paint-tree block cleanly via wrapper TU (matches Win10 19041 SDK).
            dw_wrap = BUILD_DIR / "SkScalerContext_win_dw_mingw.cpp"
            dw_src = (SKIA_DIR / s).read_text(encoding="utf-8")
            dw_src = dw_src.replace(
                "#if DWRITE_CORE || (defined(NTDDI_WIN11_ZN) && NTDDI_VERSION >= NTDDI_WIN11_ZN)",
                "#if 0 /* MinGW dwrite_3.h compatibility (matches Win10 19041 SDK) */",
            )
            dw_src = dw_src.replace("fontFace4->GetGlyphImageFormats(", "fontFace4->GetGlyphImageFormats_(")
            dw_wrap.write_text(dw_src, encoding="utf-8")
            p = dw_wrap
        add_build(p, rule, extra)

    # Haswell opts
    add_build(SKIA_DIR / "src/opts/SkOpts_hsw.cpp", "cxx", "-march=haswell")
    # Slim C stubs
    add_build(stubs_cpp, "cxx", "'-DSK_C_API=__declspec(dllexport)'")

    # zlib
    zlib_extra = f"-DZLIB_IMPLEMENTATION -I{SKIA_DIR}/third_party/externals/zlib"
    for s in zlib_srcs:
        add_build(SKIA_DIR / s, "third_party_cc", zlib_extra)

    # libpng
    png_extra = f"-DPNG_INTEL_SSE_OPT=1 -I{SKIA_DIR}/third_party/libpng -I{SKIA_DIR}/third_party/externals/libpng -I{SKIA_DIR}/third_party/externals/zlib"
    for s in libpng_srcs:
        add_build(SKIA_DIR / s, "third_party_cc", png_extra)

    # libjpeg-turbo
    jpeg_extra = f"-DTURBO_FOR_WINDOWS -DC_ARITH_CODING_SUPPORTED=1 -DD_ARITH_CODING_SUPPORTED=1 -DUSE_CLZ_INTRINSIC -I{SKIA_DIR}/third_party/libjpeg-turbo -I{SKIA_DIR}/third_party/externals/libjpeg-turbo"
    for s in libjpeg_srcs:
        add_build(SKIA_DIR / s, "third_party_cc", jpeg_extra)

    ninja_lines.append("")
    ninja_lines.append(f"build {OUT_DLL}: link_dll {' '.join(objs)}")
    ninja_lines.append("")

    ninja_file = BUILD_DIR / "build.ninja"
    (BUILD_DIR / "obj").mkdir(parents=True, exist_ok=True)
    ninja_file.write_text("\n".join(ninja_lines), encoding="utf-8")
    print(f"Generated {ninja_file} with {len(objs)} translation units.")

    subprocess.run(["ninja", "-k", "20", "-f", str(ninja_file), "-j", str(os.cpu_count() or 8)], check=True)
    for stray in OUT_DLL.parent.glob("*.lib"):
        stray.unlink(missing_ok=True)
    size_mb = OUT_DLL.stat().st_size / (1024 * 1024)
    print(f"Built {OUT_DLL} ({size_mb:.2f} MB)")


if __name__ == "__main__":
    main()
