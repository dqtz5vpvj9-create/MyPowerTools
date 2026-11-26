/*
 * MyPowerTools Pure Win32 Native Bootstrapper (mpt-bootstrap.exe)
 *
 * Zero-.NET, zero-CRT, <50 KB native Windows GUI launcher & one-click
 * user-local .NET 8.0+ runtime auto-installer.
 */

#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN

#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <winhttp.h>

#include "mpt_bootstrap_core.h"

#ifndef WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY
#define WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY 4
#endif

#ifndef DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
#define DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 ((HANDLE)(LONG_PTR)-4)
#endif

#ifndef WM_DPICHANGED
#define WM_DPICHANGED 0x02E0
#endif

#ifndef PBS_MARQUEE
#define PBS_MARQUEE 0x08
#endif

#ifndef PBM_SETMARQUEE
#define PBM_SETMARQUEE (WM_USER + 10)
#endif

#define IDI_MPT_APPICON 101

#define IDC_LBL_HEADING 1001
#define IDC_LBL_DESC    1002
#define IDC_PROGRESS    1003
#define IDC_LBL_STATUS  1004
#define IDC_BTN_INSTALL 1005
#define IDC_BTN_WEB     1006
#define IDC_BTN_CANCEL  1007

#define IDT_UI_READY    1
#define IDT_AUTOCLOSE   2

#define WM_MPT_STATUS       (WM_APP + 10)
#define WM_MPT_PROGRESS     (WM_APP + 11)
#define WM_MPT_INSTALL_DONE (WM_APP + 12)

#define MPT_PATH_CAP 4096
#define MPT_CMD_CAP  32768
#define MPT_STATUS_CAP 512

/* -------------------------------------------------------------------------
 * Freestanding Memory Primitives (Required by -nostdlib)
 * ------------------------------------------------------------------------- */

void *memset(void *dest, int c, size_t count) {
    unsigned char *p = (unsigned char *)dest;
    while (count-- > 0) {
        *p++ = (unsigned char)c;
        __asm__ volatile("" : : : "memory");
    }
    return dest;
}

void *memcpy(void *dest, const void *src, size_t count) {
    unsigned char *d = (unsigned char *)dest;
    const unsigned char *s = (const unsigned char *)src;
    while (count-- > 0) {
        *d++ = *s++;
        __asm__ volatile("" : : : "memory");
    }
    return dest;
}

void *memmove(void *dest, const void *src, size_t count) {
    unsigned char *d = (unsigned char *)dest;
    const unsigned char *s = (const unsigned char *)src;
    if (d < s || d >= s + count) {
        while (count-- > 0) {
            *d++ = *s++;
            __asm__ volatile("" : : : "memory");
        }
    } else {
        d += count;
        s += count;
        while (count-- > 0) {
            *--d = *--s;
            __asm__ volatile("" : : : "memory");
        }
    }
    return dest;
}

int memcmp(const void *lhs, const void *rhs, size_t count) {
    const unsigned char *a = (const unsigned char *)lhs;
    const unsigned char *b = (const unsigned char *)rhs;
    while (count-- > 0) {
        if (*a != *b) {
            return (*a < *b) ? -1 : 1;
        }
        a++;
        b++;
    }
    return 0;
}

size_t strlen(const char *s) {
    size_t n = 0;
    if (!s) {
        return 0;
    }
    while (s[n] != '\0') {
        n++;
        __asm__ volatile("" : : : "memory");
    }
    return n;
}

size_t wcslen(const WCHAR *s) {
    size_t n = 0;
    if (!s) {
        return 0;
    }
    while (s[n] != L'\0') {
        n++;
        __asm__ volatile("" : : : "memory");
    }
    return n;
}

/* -------------------------------------------------------------------------
 * Heap & Wide-String Helpers
 * ------------------------------------------------------------------------- */

static void *mpt_alloc(size_t bytes) {
    return HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, bytes > 0 ? bytes : 1);
}

static void mpt_free(void *ptr) {
    if (ptr) {
        HeapFree(GetProcessHeap(), 0, ptr);
    }
}

static WCHAR *mpt_alloc_wstr(size_t chars) {
    return (WCHAR *)mpt_alloc(chars * sizeof(WCHAR));
}

static size_t mpt_wlen(const WCHAR *s) {
    size_t n = 0;
    if (!s) {
        return 0;
    }
    while (s[n] != L'\0') {
        n++;
    }
    return n;
}

static WCHAR mpt_wlower(WCHAR c) {
    if (c >= L'A' && c <= L'Z') {
        return (WCHAR)(c + (L'a' - L'A'));
    }
    return c;
}

static bool mpt_wstreq_i(const WCHAR *a, const WCHAR *b) {
    size_t i = 0;
    if (!a || !b) {
        return a == b;
    }
    while (a[i] != L'\0' && b[i] != L'\0') {
        if (mpt_wlower(a[i]) != mpt_wlower(b[i])) {
            return false;
        }
        i++;
    }
    return a[i] == b[i];
}

static bool mpt_wcopy(WCHAR *dst, size_t cap, const WCHAR *src) {
    size_t i = 0;
    if (!dst || cap == 0) {
        return false;
    }
    if (!src) {
        dst[0] = L'\0';
        return true;
    }
    while (src[i] != L'\0') {
        if (i + 1 >= cap) {
            dst[0] = L'\0';
            return false;
        }
        dst[i] = src[i];
        i++;
    }
    dst[i] = L'\0';
    return true;
}

static bool mpt_wappend(WCHAR *dst, size_t cap, const WCHAR *src) {
    size_t len = mpt_wlen(dst);
    size_t i = 0;
    if (!src) {
        return true;
    }
    while (src[i] != L'\0') {
        if (len + 1 >= cap) {
            return false;
        }
        dst[len++] = src[i++];
    }
    dst[len] = L'\0';
    return true;
}

static bool mpt_wappend_u64(WCHAR *dst, size_t cap, uint64_t val) {
    char dec[32];
    size_t dec_len = 0;
    size_t i = 0;
    dec[0] = '\0';
    if (!mpt_append_u64_dec(dec, sizeof(dec), &dec_len, val)) {
        return false;
    }
    for (i = 0; i < dec_len; i++) {
        WCHAR tmp[2];
        tmp[0] = (WCHAR)dec[i];
        tmp[1] = L'\0';
        if (!mpt_wappend(dst, cap, tmp)) {
            return false;
        }
    }
    return true;
}

static bool mpt_utf8_to_wide_buf(const char *utf8, WCHAR *dst, size_t cap) {
    int written;
    if (!dst || cap == 0) {
        return false;
    }
    dst[0] = L'\0';
    if (!utf8 || utf8[0] == '\0') {
        return true;
    }
    written = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, dst, (int)cap);
    if (written <= 0) {
        dst[0] = L'\0';
        return false;
    }
    return true;
}

static WCHAR *mpt_utf8_to_wide_alloc(const char *utf8) {
    int needed;
    WCHAR *buf;
    if (!utf8) {
        return NULL;
    }
    needed = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, NULL, 0);
    if (needed <= 0) {
        return NULL;
    }
    buf = mpt_alloc_wstr((size_t)needed + 1);
    if (!buf) {
        return NULL;
    }
    if (MultiByteToWideChar(CP_UTF8, 0, utf8, -1, buf, needed) <= 0) {
        mpt_free(buf);
        return NULL;
    }
    return buf;
}

static bool mpt_wide_to_utf8_buf(const WCHAR *wide, char *dst, size_t cap) {
    int written;
    if (!dst || cap == 0) {
        return false;
    }
    dst[0] = '\0';
    if (!wide || wide[0] == L'\0') {
        return true;
    }
    written = WideCharToMultiByte(CP_UTF8, 0, wide, -1, dst, (int)cap, NULL, NULL);
    if (written <= 0) {
        dst[0] = '\0';
        return false;
    }
    return true;
}

static char *mpt_wide_to_utf8_alloc(const WCHAR *wide) {
    int needed;
    char *buf;
    if (!wide) {
        wide = L"";
    }
    needed = WideCharToMultiByte(CP_UTF8, 0, wide, -1, NULL, 0, NULL, NULL);
    if (needed <= 0) {
        return NULL;
    }
    buf = (char *)mpt_alloc((size_t)needed + 1);
    if (!buf) {
        return NULL;
    }
    if (WideCharToMultiByte(CP_UTF8, 0, wide, -1, buf, needed, NULL, NULL) <= 0) {
        mpt_free(buf);
        return NULL;
    }
    return buf;
}

/* -------------------------------------------------------------------------
 * Filesystem & Environment Helpers
 * ------------------------------------------------------------------------- */

static bool mpt_file_exists_w(const WCHAR *path) {
    DWORD attrs;
    if (!path || path[0] == L'\0') {
        return false;
    }
    attrs = GetFileAttributesW(path);
    return (attrs != INVALID_FILE_ATTRIBUTES) && ((attrs & FILE_ATTRIBUTE_DIRECTORY) == 0);
}

static bool mpt_dir_exists_w(const WCHAR *path) {
    DWORD attrs;
    if (!path || path[0] == L'\0') {
        return false;
    }
    attrs = GetFileAttributesW(path);
    return (attrs != INVALID_FILE_ATTRIBUTES) && ((attrs & FILE_ATTRIBUTE_DIRECTORY) != 0);
}

static bool mpt_is_rooted_path_w(const WCHAR *path) {
    if (!path || path[0] == L'\0') {
        return false;
    }
    if (path[0] == L'\\' || path[0] == L'/') {
        return true;
    }
    if (((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) &&
        path[1] == L':') {
        return true;
    }
    return false;
}

static void mpt_trim_trailing_slashes_w(WCHAR *path) {
    size_t len = mpt_wlen(path);
    while (len > 0 && (path[len - 1] == L'\\' || path[len - 1] == L'/')) {
        if (len == 3 && path[1] == L':') {
            break;
        }
        path[--len] = L'\0';
    }
}

static bool mpt_join_path_w(const WCHAR *base_dir, const WCHAR *child, WCHAR *out_path, size_t cap) {
    size_t len;
    if (!base_dir || !child || !out_path || cap == 0) {
        return false;
    }
    if (!mpt_wcopy(out_path, cap, base_dir)) {
        return false;
    }
    len = mpt_wlen(out_path);
    if (len > 0 && out_path[len - 1] != L'\\' && out_path[len - 1] != L'/') {
        if (!mpt_wappend(out_path, cap, L"\\")) {
            return false;
        }
    }
    while (*child == L'\\' || *child == L'/') {
        child++;
    }
    return mpt_wappend(out_path, cap, child);
}

static bool mpt_normalize_full_path_w(const WCHAR *raw_path, WCHAR *out_path, size_t cap) {
    DWORD n;
    if (!raw_path || raw_path[0] == L'\0') {
        return false;
    }
    n = GetFullPathNameW(raw_path, (DWORD)cap, out_path, NULL);
    if (n == 0 || n >= (DWORD)cap) {
        return mpt_wcopy(out_path, cap, raw_path);
    }
    return true;
}

static bool mpt_expand_env_w(const WCHAR *src, WCHAR *dst, size_t cap) {
    DWORD n;
    if (!src || !dst || cap == 0) {
        return false;
    }
    n = ExpandEnvironmentStringsW(src, dst, (DWORD)cap);
    if (n == 0 || n > (DWORD)cap) {
        return mpt_wcopy(dst, cap, src);
    }
    return true;
}

static WCHAR *mpt_get_env_alloc_w(const WCHAR *name) {
    DWORD needed = GetEnvironmentVariableW(name, NULL, 0);
    WCHAR *buf;
    if (needed == 0) {
        return NULL;
    }
    buf = mpt_alloc_wstr((size_t)needed + 1);
    if (!buf) {
        return NULL;
    }
    if (GetEnvironmentVariableW(name, buf, needed) == 0) {
        mpt_free(buf);
        return NULL;
    }
    return buf;
}

static bool mpt_ensure_dir_recursive_w(const WCHAR *dir_path) {
    WCHAR *buf;
    size_t len, i;
    if (!dir_path || dir_path[0] == L'\0') {
        return false;
    }
    if (mpt_dir_exists_w(dir_path)) {
        return true;
    }
    buf = mpt_alloc_wstr(MPT_PATH_CAP);
    if (!buf) {
        return false;
    }
    if (!mpt_normalize_full_path_w(dir_path, buf, MPT_PATH_CAP)) {
        mpt_free(buf);
        return false;
    }
    len = mpt_wlen(buf);
    for (i = 0; i < len; i++) {
        if ((buf[i] == L'\\' || buf[i] == L'/') && i > 0) {
            WCHAR saved = buf[i];
            if (i == 2 && buf[1] == L':') {
                continue;
            }
            buf[i] = L'\0';
            if (!mpt_dir_exists_w(buf)) {
                CreateDirectoryW(buf, NULL);
            }
            buf[i] = saved;
        }
    }
    if (!mpt_dir_exists_w(buf)) {
        CreateDirectoryW(buf, NULL);
    }
    len = mpt_dir_exists_w(buf) ? 1 : 0;
    mpt_free(buf);
    return len != 0;
}

static char *mpt_read_file_utf8_alloc(const WCHAR *path, size_t *out_len) {
    HANDLE hFile;
    LARGE_INTEGER sz;
    char *buf;
    DWORD bytes_read = 0;

    if (out_len) {
        *out_len = 0;
    }
    hFile = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE) {
        return NULL;
    }
    if (!GetFileSizeEx(hFile, &sz) || sz.QuadPart < 0 || sz.QuadPart > (1024 * 1024)) {
        CloseHandle(hFile);
        return NULL;
    }
    buf = (char *)mpt_alloc((size_t)sz.QuadPart + 1);
    if (!buf) {
        CloseHandle(hFile);
        return NULL;
    }
    if (sz.QuadPart > 0 && !ReadFile(hFile, buf, (DWORD)sz.QuadPart, &bytes_read, NULL)) {
        CloseHandle(hFile);
        mpt_free(buf);
        return NULL;
    }
    CloseHandle(hFile);
    buf[bytes_read] = '\0';
    if (out_len) {
        *out_len = (size_t)bytes_read;
    }
    return buf;
}

static bool mpt_write_file_utf8(const WCHAR *path, const char *data, size_t len) {
    WCHAR *parent;
    size_t i, last_sep = 0;
    HANDLE hFile;
    DWORD written = 0;

    parent = mpt_alloc_wstr(MPT_PATH_CAP);
    if (parent) {
        if (mpt_normalize_full_path_w(path, parent, MPT_PATH_CAP)) {
            for (i = 0; parent[i] != L'\0'; i++) {
                if (parent[i] == L'\\' || parent[i] == L'/') {
                    last_sep = i;
                }
            }
            if (last_sep > 0) {
                parent[last_sep] = L'\0';
                mpt_ensure_dir_recursive_w(parent);
            }
        }
        mpt_free(parent);
    }

    hFile = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE) {
        return false;
    }
    if (len > 0 && !WriteFile(hFile, data, (DWORD)len, &written, NULL)) {
        CloseHandle(hFile);
        return false;
    }
    CloseHandle(hFile);
    return written == (DWORD)len;
}

/* -------------------------------------------------------------------------
 * Application Context
 * ------------------------------------------------------------------------- */

typedef struct MptAppContext {
    HINSTANCE hInstance;

    WCHAR exe_path[MPT_PATH_CAP];
    WCHAR exe_dir[MPT_PATH_CAP];
    WCHAR exe_stem[260];
    char exe_dir_utf8[MPT_PATH_CAP];

    MptBootConfig boot_cfg;
    bool has_config_file;
    bool config_parse_failed;
    WCHAR config_path[MPT_PATH_CAP];

    WCHAR title[260];
    WCHAR entry_dll_display[MPT_PATH_CAP];
    WCHAR resolved_entry_dll[MPT_PATH_CAP];
    bool has_resolved_entry_dll;

    WCHAR default_args_w[MPT_MAX_DEFAULT_ARGS][MPT_MAX_STR];
    size_t default_arg_count;

    int min_major;
    bool require_aspnet;
    bool wait_for_child;

    int argc;
    WCHAR **argv_w;
    char **argv_utf8;
    MptBootstrapCliOptions cli;

    bool found_dotnet;
    WCHAR dotnet_root[MPT_PATH_CAP];
    MptDotnetVersion best_version;
    char best_version_str[64];

    /* GUI & Worker State */
    HWND hwnd;
    HWND lbl_heading;
    HWND lbl_desc;
    HWND progress_bar;
    HWND lbl_status;
    HWND btn_install;
    HWND btn_web;
    HWND btn_cancel;
    HFONT font_heading;
    HFONT font_body;
    HICON app_icon;
    UINT dpi;
    bool is_marquee;
    bool ui_dumped;
    bool install_running;
    int exit_code;

    HANDLE worker_thread;
    volatile LONG cancel_requested;
    CRITICAL_SECTION state_lock;
    HINTERNET active_session;
    HINTERNET active_connect;
    HINTERNET active_request;
    WCHAR status_text[MPT_STATUS_CAP];
    WCHAR error_detail[MPT_STATUS_CAP];
    WCHAR last_install_dir[MPT_PATH_CAP];
} MptAppContext;

static MptAppContext *g_ctx = NULL;

/* -------------------------------------------------------------------------
 * Executable Path & Config Resolution
 * ------------------------------------------------------------------------- */

static bool mpt_init_exe_paths(MptAppContext *ctx) {
    DWORD len = GetModuleFileNameW(NULL, ctx->exe_path, MPT_PATH_CAP);
    size_t i, last_sep = 0, stem_len = 0;
    const WCHAR *file_part;

    if (len == 0 || len >= MPT_PATH_CAP) {
        return false;
    }

    for (i = 0; i < len; i++) {
        if (ctx->exe_path[i] == L'\\' || ctx->exe_path[i] == L'/') {
            last_sep = i;
        }
    }

    if (last_sep == 0) {
        mpt_wcopy(ctx->exe_dir, MPT_PATH_CAP, L".");
        file_part = ctx->exe_path;
    } else {
        size_t dir_len = last_sep;
        if (dir_len == 2 && ctx->exe_path[1] == L':') {
            dir_len = 3;
        }
        if (dir_len >= MPT_PATH_CAP) {
            return false;
        }
        memcpy(ctx->exe_dir, ctx->exe_path, dir_len * sizeof(WCHAR));
        ctx->exe_dir[dir_len] = L'\0';
        mpt_trim_trailing_slashes_w(ctx->exe_dir);
        file_part = ctx->exe_path + last_sep + 1;
    }

    mpt_wcopy(ctx->exe_stem, 260, file_part);
    stem_len = mpt_wlen(ctx->exe_stem);
    if (stem_len > 4 && mpt_wstreq_i(ctx->exe_stem + stem_len - 4, L".exe")) {
        ctx->exe_stem[stem_len - 4] = L'\0';
    }

    mpt_wide_to_utf8_buf(ctx->exe_dir, ctx->exe_dir_utf8, sizeof(ctx->exe_dir_utf8));
    return true;
}

static bool mpt_parse_process_cli(MptAppContext *ctx) {
    int i;
    ctx->argv_w = CommandLineToArgvW(GetCommandLineW(), &ctx->argc);
    if (!ctx->argv_w || ctx->argc <= 0) {
        return false;
    }
    ctx->argv_utf8 = (char **)mpt_alloc((size_t)ctx->argc * sizeof(char *));
    if (!ctx->argv_utf8) {
        return false;
    }
    for (i = 0; i < ctx->argc; i++) {
        ctx->argv_utf8[i] = mpt_wide_to_utf8_alloc(ctx->argv_w[i]);
        if (!ctx->argv_utf8[i]) {
            return false;
        }
    }
    return mpt_parse_cli_args_utf8(ctx->argc, (const char *const *)ctx->argv_utf8, &ctx->cli);
}

static void mpt_apply_zero_config_convention(MptAppContext *ctx, bool populate_implicit_args) {
    WCHAR *cand = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR *expanded = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR stem_dll[280];

    if (!cand || !expanded) {
        mpt_free(cand);
        mpt_free(expanded);
        return;
    }

    mpt_wcopy(stem_dll, 280, ctx->exe_stem);
    mpt_wappend(stem_dll, 280, L".dll");
    if (ctx->entry_dll_display[0] == L'\0') {
        mpt_wcopy(ctx->entry_dll_display, MPT_PATH_CAP, stem_dll);
    }

    /* 1. <ExeDir>\<ExeStem>.dll */
    if (mpt_join_path_w(ctx->exe_dir, stem_dll, cand, MPT_PATH_CAP) && mpt_file_exists_w(cand)) {
        mpt_normalize_full_path_w(cand, ctx->resolved_entry_dll, MPT_PATH_CAP);
        mpt_wcopy(ctx->entry_dll_display, MPT_PATH_CAP, stem_dll);
        ctx->has_resolved_entry_dll = true;
        goto done;
    }

    /* 2. <ExeDir>\app\<ExeStem>.dll */
    if (mpt_join_path_w(ctx->exe_dir, L"app", expanded, MPT_PATH_CAP) &&
        mpt_join_path_w(expanded, stem_dll, cand, MPT_PATH_CAP) &&
        mpt_file_exists_w(cand)) {
        mpt_normalize_full_path_w(cand, ctx->resolved_entry_dll, MPT_PATH_CAP);
        mpt_wcopy(ctx->entry_dll_display, MPT_PATH_CAP, L"app\\");
        mpt_wappend(ctx->entry_dll_display, MPT_PATH_CAP, stem_dll);
        ctx->has_resolved_entry_dll = true;
        goto done;
    }

    /* 3. <ExeDir>\app\MptStandaloneHost.dll */
    if (mpt_join_path_w(ctx->exe_dir, L"app\\MptStandaloneHost.dll", cand, MPT_PATH_CAP) &&
        mpt_file_exists_w(cand)) {
        mpt_normalize_full_path_w(cand, ctx->resolved_entry_dll, MPT_PATH_CAP);
        mpt_wcopy(ctx->entry_dll_display, MPT_PATH_CAP, L"app\\MptStandaloneHost.dll");
        ctx->has_resolved_entry_dll = true;
        if (populate_implicit_args) {
            mpt_wcopy(ctx->default_args_w[0], MPT_MAX_STR, L"--tool-root");
            mpt_wcopy(ctx->default_args_w[1], MPT_MAX_STR, ctx->exe_dir);
            ctx->default_arg_count = 2;
        }
        goto done;
    }

    /* 4. <ExeDir>\..\shared-ui\MptStandaloneHost.dll */
    if (mpt_join_path_w(ctx->exe_dir, L"..\\shared-ui\\MptStandaloneHost.dll", cand, MPT_PATH_CAP) &&
        mpt_normalize_full_path_w(cand, expanded, MPT_PATH_CAP) &&
        mpt_file_exists_w(expanded)) {
        mpt_wcopy(ctx->resolved_entry_dll, MPT_PATH_CAP, expanded);
        mpt_wcopy(ctx->entry_dll_display, MPT_PATH_CAP, L"..\\shared-ui\\MptStandaloneHost.dll");
        ctx->has_resolved_entry_dll = true;
        if (populate_implicit_args) {
            mpt_wcopy(ctx->default_args_w[0], MPT_MAX_STR, L"--tool-root");
            mpt_wcopy(ctx->default_args_w[1], MPT_MAX_STR, ctx->exe_dir);
            ctx->default_arg_count = 2;
        }
        goto done;
    }

    /* 5. %LOCALAPPDATA%\MyPowerTools\shared-ui\MptStandaloneHost.dll */
    if (mpt_expand_env_w(L"%LOCALAPPDATA%\\MyPowerTools\\shared-ui\\MptStandaloneHost.dll", expanded, MPT_PATH_CAP) &&
        mpt_file_exists_w(expanded)) {
        mpt_normalize_full_path_w(expanded, ctx->resolved_entry_dll, MPT_PATH_CAP);
        mpt_wcopy(ctx->entry_dll_display, MPT_PATH_CAP, L"%LOCALAPPDATA%\\MyPowerTools\\shared-ui\\MptStandaloneHost.dll");
        ctx->has_resolved_entry_dll = true;
        if (populate_implicit_args) {
            mpt_wcopy(ctx->default_args_w[0], MPT_MAX_STR, L"--tool-root");
            mpt_wcopy(ctx->default_args_w[1], MPT_MAX_STR, ctx->exe_dir);
            ctx->default_arg_count = 2;
        }
        goto done;
    }

done:
    mpt_free(cand);
    mpt_free(expanded);
}

static void mpt_resolve_entry_from_config(MptAppContext *ctx) {
    WCHAR *entry_raw_w = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR *entry_exp_w = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR *probe_raw_w = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR *probe_exp_w = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR *probe_base_w = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR *cand_w = mpt_alloc_wstr(MPT_PATH_CAP);
    char *probe_tok_utf8 = (char *)mpt_alloc(MPT_PATH_CAP);
    size_t i;

    if (!entry_raw_w || !entry_exp_w || !probe_raw_w || !probe_exp_w || !probe_base_w || !cand_w || !probe_tok_utf8) {
        goto cleanup;
    }

    mpt_utf8_to_wide_buf(ctx->boot_cfg.entry_dll, entry_raw_w, MPT_PATH_CAP);
    mpt_wcopy(ctx->entry_dll_display, MPT_PATH_CAP, entry_raw_w);
    mpt_expand_env_w(entry_raw_w, entry_exp_w, MPT_PATH_CAP);

    if (mpt_is_rooted_path_w(entry_exp_w)) {
        if (mpt_file_exists_w(entry_exp_w)) {
            mpt_normalize_full_path_w(entry_exp_w, ctx->resolved_entry_dll, MPT_PATH_CAP);
            ctx->has_resolved_entry_dll = true;
            goto cleanup;
        }
    } else {
        if (mpt_join_path_w(ctx->exe_dir, entry_exp_w, cand_w, MPT_PATH_CAP) &&
            mpt_file_exists_w(cand_w)) {
            mpt_normalize_full_path_w(cand_w, ctx->resolved_entry_dll, MPT_PATH_CAP);
            ctx->has_resolved_entry_dll = true;
            goto cleanup;
        }
    }

    /* Search probeDirs in configured order */
    for (i = 0; i < ctx->boot_cfg.probe_dir_count; i++) {
        if (!mpt_expand_default_arg_utf8(ctx->boot_cfg.probe_dirs[i], ctx->exe_dir_utf8, probe_tok_utf8, MPT_PATH_CAP)) {
            continue;
        }
        mpt_utf8_to_wide_buf(probe_tok_utf8, probe_raw_w, MPT_PATH_CAP);
        mpt_expand_env_w(probe_raw_w, probe_exp_w, MPT_PATH_CAP);

        if (mpt_is_rooted_path_w(probe_exp_w)) {
            mpt_wcopy(probe_base_w, MPT_PATH_CAP, probe_exp_w);
        } else {
            mpt_join_path_w(ctx->exe_dir, probe_exp_w, probe_base_w, MPT_PATH_CAP);
        }

        if (mpt_join_path_w(probe_base_w, entry_exp_w, cand_w, MPT_PATH_CAP) &&
            mpt_file_exists_w(cand_w)) {
            mpt_normalize_full_path_w(cand_w, ctx->resolved_entry_dll, MPT_PATH_CAP);
            ctx->has_resolved_entry_dll = true;
            break;
        }
    }

cleanup:
    mpt_free(entry_raw_w);
    mpt_free(entry_exp_w);
    mpt_free(probe_raw_w);
    mpt_free(probe_exp_w);
    mpt_free(probe_base_w);
    mpt_free(cand_w);
    mpt_free(probe_tok_utf8);
}

static void mpt_resolve_configuration(MptAppContext *ctx) {
    WCHAR cfg_name[300];
    size_t i;

    mpt_boot_config_init(&ctx->boot_cfg);
    mpt_wcopy(ctx->title, 260, ctx->exe_stem);
    ctx->min_major = 8;
    ctx->require_aspnet = false;
    ctx->wait_for_child = ctx->cli.force_wait;
    ctx->has_resolved_entry_dll = false;
    ctx->resolved_entry_dll[0] = L'\0';
    ctx->default_arg_count = 0;

    mpt_wcopy(cfg_name, 300, ctx->exe_stem);
    mpt_wappend(cfg_name, 300, L".mptboot.json");
    mpt_join_path_w(ctx->exe_dir, cfg_name, ctx->config_path, MPT_PATH_CAP);

    if (mpt_file_exists_w(ctx->config_path)) {
        size_t json_len = 0;
        char *json_utf8 = mpt_read_file_utf8_alloc(ctx->config_path, &json_len);
        ctx->has_config_file = true;

        if (!json_utf8) {
            ctx->config_parse_failed = true;
            return;
        }
        /* Also convert UTF-8 -> UTF-16 as sanity check per spec */
        {
            WCHAR *wide_check = mpt_utf8_to_wide_alloc(json_utf8);
            if (!wide_check && json_len > 0) {
                mpt_free(json_utf8);
                ctx->config_parse_failed = true;
                return;
            }
            mpt_free(wide_check);
        }

        if (!mpt_parse_boot_config_utf8(json_utf8, json_len, &ctx->boot_cfg)) {
            mpt_free(json_utf8);
            ctx->config_parse_failed = true;
            return;
        }
        mpt_free(json_utf8);

        if (ctx->boot_cfg.has_title && ctx->boot_cfg.title[0] != '\0') {
            mpt_utf8_to_wide_buf(ctx->boot_cfg.title, ctx->title, 260);
        }
        ctx->min_major = ctx->boot_cfg.min_major;
        ctx->require_aspnet = ctx->boot_cfg.require_aspnet;
        ctx->wait_for_child = ctx->boot_cfg.wait_for_child || ctx->cli.force_wait;

        for (i = 0; i < ctx->boot_cfg.default_arg_count; i++) {
            char *expanded_utf8 = (char *)mpt_alloc(MPT_PATH_CAP);
            if (expanded_utf8) {
                if (mpt_expand_default_arg_utf8(
                        ctx->boot_cfg.default_args[i],
                        ctx->exe_dir_utf8,
                        expanded_utf8,
                        MPT_PATH_CAP)) {
                    mpt_utf8_to_wide_buf(expanded_utf8, ctx->default_args_w[ctx->default_arg_count], MPT_MAX_STR);
                    ctx->default_arg_count++;
                }
                mpt_free(expanded_utf8);
            }
        }

        if (ctx->boot_cfg.has_entry_dll && ctx->boot_cfg.entry_dll[0] != '\0') {
            mpt_resolve_entry_from_config(ctx);
        } else {
            mpt_apply_zero_config_convention(ctx, !ctx->boot_cfg.has_default_args);
        }
    } else {
        mpt_apply_zero_config_convention(ctx, true);
    }
}

/* -------------------------------------------------------------------------
 * .NET >= minMajor Runtime Discovery
 * ------------------------------------------------------------------------- */

static bool mpt_has_aspnet_major_w(const WCHAR *root_dir, int min_major) {
    WCHAR *pattern = mpt_alloc_wstr(MPT_PATH_CAP);
    WIN32_FIND_DATAW *fd = (WIN32_FIND_DATAW *)mpt_alloc(sizeof(WIN32_FIND_DATAW));
    HANDLE hFind;
    bool found = false;

    if (!pattern || !fd) {
        mpt_free(pattern);
        mpt_free(fd);
        return false;
    }

    if (!mpt_join_path_w(root_dir, L"shared\\Microsoft.AspNetCore.App\\*", pattern, MPT_PATH_CAP)) {
        mpt_free(pattern);
        mpt_free(fd);
        return false;
    }

    hFind = FindFirstFileW(pattern, fd);
    if (hFind != INVALID_HANDLE_VALUE) {
        do {
            if ((fd->dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
                char ver_utf8[128];
                MptDotnetVersion ver;
                if (fd->cFileName[0] == L'.') {
                    continue;
                }
                if (mpt_wide_to_utf8_buf(fd->cFileName, ver_utf8, sizeof(ver_utf8)) &&
                    mpt_parse_dotnet_version(ver_utf8, &ver) &&
                    mpt_version_meets_min_major(&ver, min_major)) {
                    found = true;
                    break;
                }
            }
        } while (FindNextFileW(hFind, fd));
        FindClose(hFind);
    }

    mpt_free(pattern);
    mpt_free(fd);
    return found;
}

static bool mpt_validate_dotnet_root_w(
    const WCHAR *raw_root,
    int min_major,
    bool require_aspnet,
    WCHAR *out_norm_root,
    MptDotnetVersion *out_best_ver,
    char *out_best_ver_str,
    size_t best_ver_cap) {
    WCHAR *norm = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR *check_path = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR *ver_dir = mpt_alloc_wstr(MPT_PATH_CAP);
    WIN32_FIND_DATAW *fd = (WIN32_FIND_DATAW *)mpt_alloc(sizeof(WIN32_FIND_DATAW));
    HANDLE hFind;
    bool has_valid = false;
    MptDotnetVersion best_ver;
    char best_str[64];

    if (!raw_root || raw_root[0] == L'\0' || !norm || !check_path || !ver_dir || !fd) {
        goto cleanup;
    }

    if (!mpt_normalize_full_path_w(raw_root, norm, MPT_PATH_CAP)) {
        goto cleanup;
    }
    mpt_trim_trailing_slashes_w(norm);

    if (!mpt_join_path_w(norm, L"dotnet.exe", check_path, MPT_PATH_CAP) ||
        !mpt_file_exists_w(check_path)) {
        goto cleanup;
    }

    if (!mpt_join_path_w(norm, L"shared\\Microsoft.NETCore.App\\*", check_path, MPT_PATH_CAP)) {
        goto cleanup;
    }

    memset(&best_ver, 0, sizeof(best_ver));
    best_str[0] = '\0';

    hFind = FindFirstFileW(check_path, fd);
    if (hFind == INVALID_HANDLE_VALUE) {
        goto cleanup;
    }

    do {
        if ((fd->dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
            char name_utf8[128];
            MptDotnetVersion cur_ver;
            if (fd->cFileName[0] == L'.') {
                continue;
            }
            if (!mpt_wide_to_utf8_buf(fd->cFileName, name_utf8, sizeof(name_utf8))) {
                continue;
            }
            if (!mpt_parse_dotnet_version(name_utf8, &cur_ver) ||
                !mpt_version_meets_min_major(&cur_ver, min_major)) {
                continue;
            }
            if (!mpt_join_path_w(norm, L"shared\\Microsoft.NETCore.App", ver_dir, MPT_PATH_CAP) ||
                !mpt_join_path_w(ver_dir, fd->cFileName, check_path, MPT_PATH_CAP) ||
                !mpt_join_path_w(check_path, L"coreclr.dll", ver_dir, MPT_PATH_CAP) ||
                !mpt_file_exists_w(ver_dir)) {
                continue;
            }
            if (!has_valid || mpt_compare_dotnet_version(&cur_ver, &best_ver) > 0) {
                has_valid = true;
                best_ver = cur_ver;
                mpt_copy_str(best_str, sizeof(best_str), name_utf8);
            }
        }
    } while (FindNextFileW(hFind, fd));
    FindClose(hFind);

    if (!has_valid) {
        goto cleanup;
    }
    if (require_aspnet && !mpt_has_aspnet_major_w(norm, min_major)) {
        has_valid = false;
        goto cleanup;
    }

    mpt_wcopy(out_norm_root, MPT_PATH_CAP, norm);
    *out_best_ver = best_ver;
    mpt_copy_str(out_best_ver_str, best_ver_cap, best_str);

cleanup:
    mpt_free(norm);
    mpt_free(check_path);
    mpt_free(ver_dir);
    mpt_free(fd);
    return has_valid;
}

static bool mpt_read_reg_dotnet_install_location(HKEY root_key, WCHAR *out_buf, size_t cap) {
    HKEY hKey = NULL;
    DWORD type = 0;
    DWORD cb = (DWORD)(cap * sizeof(WCHAR));
    LONG rc;

    out_buf[0] = L'\0';
    rc = RegOpenKeyExW(
        root_key,
        L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64",
        0,
        KEY_READ | KEY_WOW64_64KEY,
        &hKey);
    if (rc != ERROR_SUCCESS) {
        return false;
    }
    rc = RegQueryValueExW(hKey, L"InstallLocation", NULL, &type, (LPBYTE)out_buf, &cb);
    RegCloseKey(hKey);
    if (rc != ERROR_SUCCESS || (type != REG_SZ && type != REG_EXPAND_SZ)) {
        out_buf[0] = L'\0';
        return false;
    }
    out_buf[cap - 1] = L'\0';
    return out_buf[0] != L'\0';
}

typedef struct MptRuntimeSearchState {
    int min_major;
    bool require_aspnet;
    bool has_best;
    WCHAR best_root[MPT_PATH_CAP];
    MptDotnetVersion best_ver;
    char best_ver_str[64];
} MptRuntimeSearchState;

static void mpt_consider_candidate_root(MptRuntimeSearchState *st, const WCHAR *candidate) {
    WCHAR *norm_root;
    MptDotnetVersion ver;
    char ver_str[64];

    if (!candidate || candidate[0] == L'\0') {
        return;
    }
    norm_root = mpt_alloc_wstr(MPT_PATH_CAP);
    if (!norm_root) {
        return;
    }
    if (mpt_validate_dotnet_root_w(
            candidate,
            st->min_major,
            st->require_aspnet,
            norm_root,
            &ver,
            ver_str,
            sizeof(ver_str))) {
        if (!st->has_best || mpt_compare_dotnet_version(&ver, &st->best_ver) > 0) {
            st->has_best = true;
            mpt_wcopy(st->best_root, MPT_PATH_CAP, norm_root);
            st->best_ver = ver;
            mpt_copy_str(st->best_ver_str, sizeof(st->best_ver_str), ver_str);
        }
    }
    mpt_free(norm_root);
}

static void mpt_discover_dotnet_runtime(
    MptAppContext *ctx,
    bool ignore_force_missing,
    const WCHAR *preferred_install_dir) {
    MptRuntimeSearchState *st;
    WCHAR *cand;
    WCHAR *env_val;

    ctx->found_dotnet = false;
    ctx->dotnet_root[0] = L'\0';
    ctx->best_version_str[0] = '\0';
    memset(&ctx->best_version, 0, sizeof(ctx->best_version));

    if (!ignore_force_missing) {
        if (ctx->cli.force_no_dotnet) {
            return;
        }
        env_val = mpt_get_env_alloc_w(L"MPT_BOOTSTRAP_FORCE_MISSING_DOTNET");
        if (env_val) {
            bool forced = mpt_wstreq_i(env_val, L"1") || mpt_wstreq_i(env_val, L"true");
            mpt_free(env_val);
            if (forced) {
                return;
            }
        }
    }

    st = (MptRuntimeSearchState *)mpt_alloc(sizeof(MptRuntimeSearchState));
    cand = mpt_alloc_wstr(MPT_PATH_CAP);
    if (!st || !cand) {
        mpt_free(st);
        mpt_free(cand);
        return;
    }
    st->min_major = ctx->min_major;
    st->require_aspnet = ctx->require_aspnet;

    /* 0. Post-install explicit directory check first when provided */
    if (preferred_install_dir && preferred_install_dir[0] != L'\0') {
        mpt_consider_candidate_root(st, preferred_install_dir);
        if (st->has_best) {
            goto commit;
        }
    }

    /* 1. Private Runtime\dotnet next to or above executable (preferred immediately if valid) */
    if (mpt_join_path_w(ctx->exe_dir, L"Runtime\\dotnet", cand, MPT_PATH_CAP)) {
        mpt_consider_candidate_root(st, cand);
        if (st->has_best) {
            goto commit;
        }
    }
    if (mpt_join_path_w(ctx->exe_dir, L"..\\Runtime\\dotnet", cand, MPT_PATH_CAP)) {
        mpt_consider_candidate_root(st, cand);
        if (st->has_best) {
            goto commit;
        }
    }

    /* Optional isolated test install dir env */
    env_val = mpt_get_env_alloc_w(L"MPT_BOOTSTRAP_INSTALL_DIR");
    if (env_val) {
        mpt_consider_candidate_root(st, env_val);
        mpt_free(env_val);
    }

    /* 2. %DOTNET_ROOT% and %DOTNET_ROOT(x64)% */
    env_val = mpt_get_env_alloc_w(L"DOTNET_ROOT");
    if (env_val) {
        mpt_consider_candidate_root(st, env_val);
        mpt_free(env_val);
    }
    env_val = mpt_get_env_alloc_w(L"DOTNET_ROOT(x64)");
    if (env_val) {
        mpt_consider_candidate_root(st, env_val);
        mpt_free(env_val);
    }

    /* 3. %LOCALAPPDATA%\Microsoft\dotnet */
    if (mpt_expand_env_w(L"%LOCALAPPDATA%\\Microsoft\\dotnet", cand, MPT_PATH_CAP)) {
        mpt_consider_candidate_root(st, cand);
    }

    /* 4. %ProgramFiles%\dotnet and C:\Program Files\dotnet */
    if (mpt_expand_env_w(L"%ProgramFiles%\\dotnet", cand, MPT_PATH_CAP)) {
        mpt_consider_candidate_root(st, cand);
    }
    mpt_consider_candidate_root(st, L"C:\\Program Files\\dotnet");

    /* 5. Registry HKLM & HKCU InstalledVersions\x64 */
    if (mpt_read_reg_dotnet_install_location(HKEY_LOCAL_MACHINE, cand, MPT_PATH_CAP)) {
        mpt_consider_candidate_root(st, cand);
    }
    if (mpt_read_reg_dotnet_install_location(HKEY_CURRENT_USER, cand, MPT_PATH_CAP)) {
        mpt_consider_candidate_root(st, cand);
    }

    /* 6. Directories in %PATH% containing dotnet.exe */
    env_val = mpt_get_env_alloc_w(L"PATH");
    if (env_val) {
        size_t p = 0;
        while (env_val[p] != L'\0') {
            size_t seg_len = 0;
            while (env_val[p] == L';' || env_val[p] == L' ') {
                p++;
            }
            if (env_val[p] == L'\0') {
                break;
            }
            while (env_val[p] != L'\0' && env_val[p] != L';') {
                if (env_val[p] != L'"' && seg_len + 1 < MPT_PATH_CAP) {
                    cand[seg_len++] = env_val[p];
                }
                p++;
            }
            cand[seg_len] = L'\0';
            if (seg_len > 0) {
                mpt_consider_candidate_root(st, cand);
            }
        }
        mpt_free(env_val);
    }

commit:
    if (st->has_best) {
        ctx->found_dotnet = true;
        mpt_wcopy(ctx->dotnet_root, MPT_PATH_CAP, st->best_root);
        ctx->best_version = st->best_ver;
        mpt_copy_str(ctx->best_version_str, sizeof(ctx->best_version_str), st->best_ver_str);
    }
    mpt_free(st);
    mpt_free(cand);
}

/* -------------------------------------------------------------------------
 * Diagnostic JSON Probe (--mpt-probe-json) & UI Test Dump
 * ------------------------------------------------------------------------- */

static int mpt_write_probe_json_and_exit_code(MptAppContext *ctx) {
    WCHAR *out_path_w = mpt_utf8_to_wide_alloc(ctx->cli.probe_json_path);
    char *dotnet_root_utf8 = mpt_wide_to_utf8_alloc(ctx->dotnet_root);
    char *entry_utf8 = mpt_wide_to_utf8_alloc(ctx->entry_dll_display);
    char *resolved_utf8 = mpt_wide_to_utf8_alloc(ctx->resolved_entry_dll);
    char *json = (char *)mpt_alloc(16384);
    size_t len = 0;

    if (out_path_w && json) {
        mpt_append_str(json, 16384, &len, "{\n  \"foundDotnet\": ");
        mpt_append_str(json, 16384, &len, ctx->found_dotnet ? "true" : "false");
        mpt_append_str(json, 16384, &len, ",\n  \"dotnetRoot\": \"");
        mpt_append_json_escaped_utf8(json, 16384, &len, dotnet_root_utf8 ? dotnet_root_utf8 : "");
        mpt_append_str(json, 16384, &len, "\",\n  \"bestRuntimeVersion\": \"");
        mpt_append_json_escaped_utf8(json, 16384, &len, ctx->best_version_str);
        mpt_append_str(json, 16384, &len, "\",\n  \"entryDll\": \"");
        mpt_append_json_escaped_utf8(json, 16384, &len, entry_utf8 ? entry_utf8 : "");
        mpt_append_str(json, 16384, &len, "\",\n  \"resolvedEntryDll\": \"");
        mpt_append_json_escaped_utf8(json, 16384, &len, resolved_utf8 ? resolved_utf8 : "");
        mpt_append_str(json, 16384, &len, "\"\n}\n");
        mpt_write_file_utf8(out_path_w, json, len);
    }

    mpt_free(out_path_w);
    mpt_free(dotnet_root_utf8);
    mpt_free(entry_utf8);
    mpt_free(resolved_utf8);
    mpt_free(json);
    return ctx->found_dotnet ? 0 : 3;
}

static void mpt_write_ui_dump_json(MptAppContext *ctx) {
    WCHAR *out_path_w;
    WCHAR win_title_w[300];
    char *title_utf8;
    char *json;
    size_t len = 0;

    if (!ctx->cli.has_ui_dump || ctx->ui_dumped) {
        return;
    }
    ctx->ui_dumped = true;

    win_title_w[0] = L'\0';
    GetWindowTextW(ctx->hwnd, win_title_w, 300);

    out_path_w = mpt_utf8_to_wide_alloc(ctx->cli.ui_dump_path);
    title_utf8 = mpt_wide_to_utf8_alloc(win_title_w);
    json = (char *)mpt_alloc(4096);
    if (out_path_w && json) {
        mpt_append_str(json, 4096, &len, "{\n  \"windowCreated\": true,\n  \"title\": \"");
        mpt_append_json_escaped_utf8(json, 4096, &len, title_utf8 ? title_utf8 : "");
        mpt_append_str(json, 4096, &len, "\",\n  \"dpi\": ");
        mpt_append_u64_dec(json, 4096, &len, (uint64_t)ctx->dpi);
        mpt_append_str(json, 4096, &len, ",\n  \"minMajor\": ");
        mpt_append_u64_dec(json, 4096, &len, (uint64_t)ctx->min_major);
        mpt_append_str(json, 4096, &len, "\n}\n");
        mpt_write_file_utf8(out_path_w, json, len);
    }

    mpt_free(out_path_w);
    mpt_free(title_utf8);
    mpt_free(json);
}

/* -------------------------------------------------------------------------
 * Zero-Flash Fast Child Launch
 * ------------------------------------------------------------------------- */

static void mpt_show_missing_entry_dll_error(MptAppContext *ctx, HWND owner) {
    WCHAR *caption = mpt_alloc_wstr(320);
    WCHAR *body = mpt_alloc_wstr(2048);
    if (caption && body) {
        mpt_wcopy(caption, 320, ctx->title);
        mpt_wappend(caption, 320, L" - 启动失败");

        mpt_wcopy(body, 2048, L"未能找到工具入口程序集：\r\n");
        mpt_wappend(body, 2048, ctx->entry_dll_display);
        mpt_wappend(body, 2048, L"\r\n\r\n程序所在目录：\r\n");
        mpt_wappend(body, 2048, ctx->exe_dir);
        mpt_wappend(body, 2048, L"\r\n\r\n请确认工具目录完整（未单独移动 .exe 文件），或重新安装 MyPowerTools。");

        MessageBoxW(owner, body, caption, MB_OK | MB_ICONERROR);
    }
    mpt_free(caption);
    mpt_free(body);
}

static int mpt_launch_target_app(MptAppContext *ctx, HWND owner) {
    WCHAR *dotnet_exe = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR *cmd_line = mpt_alloc_wstr(MPT_CMD_CAP);
    WCHAR *old_path;
    size_t cmd_len = 0;
    size_t i;
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    int result_code = 0;

    if (!ctx->has_resolved_entry_dll || !mpt_file_exists_w(ctx->resolved_entry_dll)) {
        mpt_show_missing_entry_dll_error(ctx, owner);
        mpt_free(dotnet_exe);
        mpt_free(cmd_line);
        return 4;
    }

    if (!dotnet_exe || !cmd_line ||
        !mpt_join_path_w(ctx->dotnet_root, L"dotnet.exe", dotnet_exe, MPT_PATH_CAP)) {
        mpt_free(dotnet_exe);
        mpt_free(cmd_line);
        return 5;
    }

    /* Configure RollForward=Major & runtime environment for child inheritance */
    SetEnvironmentVariableW(L"DOTNET_ROOT", ctx->dotnet_root);
    SetEnvironmentVariableW(L"DOTNET_ROLL_FORWARD", L"Major");
    SetEnvironmentVariableW(L"DOTNET_ROLL_FORWARD_PRE_RELEASE", L"1");
    SetEnvironmentVariableW(L"MPT_LAUNCHER_EXE", ctx->exe_path);

    old_path = mpt_get_env_alloc_w(L"PATH");
    {
        size_t new_path_cap = mpt_wlen(ctx->dotnet_root) + (old_path ? mpt_wlen(old_path) : 0) + 4;
        WCHAR *new_path = mpt_alloc_wstr(new_path_cap);
        if (new_path) {
            mpt_wcopy(new_path, new_path_cap, ctx->dotnet_root);
            if (old_path && old_path[0] != L'\0') {
                mpt_wappend(new_path, new_path_cap, L";");
                mpt_wappend(new_path, new_path_cap, old_path);
            }
            SetEnvironmentVariableW(L"PATH", new_path);
            mpt_free(new_path);
        }
    }
    mpt_free(old_path);

    /* Build properly escaped command line: "<dotnet.exe>" exec "<entryDll>" [defaultArgs...] [userArgs...] */
    cmd_line[0] = L'\0';
    mpt_cmd_append_arg_u16((uint16_t *)cmd_line, MPT_CMD_CAP, &cmd_len, (const uint16_t *)dotnet_exe);
    mpt_cmd_append_arg_u16((uint16_t *)cmd_line, MPT_CMD_CAP, &cmd_len, (const uint16_t *)L"exec");
    mpt_cmd_append_arg_u16((uint16_t *)cmd_line, MPT_CMD_CAP, &cmd_len, (const uint16_t *)ctx->resolved_entry_dll);

    for (i = 0; i < ctx->default_arg_count; i++) {
        mpt_cmd_append_arg_u16((uint16_t *)cmd_line, MPT_CMD_CAP, &cmd_len, (const uint16_t *)ctx->default_args_w[i]);
    }
    for (i = 0; i < ctx->cli.forwarded_count; i++) {
        int idx = ctx->cli.forwarded_indices[i];
        if (idx >= 1 && idx < ctx->argc) {
            mpt_cmd_append_arg_u16((uint16_t *)cmd_line, MPT_CMD_CAP, &cmd_len, (const uint16_t *)ctx->argv_w[idx]);
        }
    }

    memset(&si, 0, sizeof(si));
    si.cb = sizeof(si);
    memset(&pi, 0, sizeof(pi));

    if (!CreateProcessW(
            dotnet_exe,
            cmd_line,
            NULL,
            NULL,
            FALSE,
            CREATE_NO_WINDOW,
            NULL,
            ctx->exe_dir,
            &si,
            &pi)) {
        WCHAR err_msg[512];
        DWORD err = GetLastError();
        mpt_wcopy(err_msg, 512, L"启动 .NET 宿主进程失败（错误码 ");
        mpt_wappend_u64(err_msg, 512, (uint64_t)err);
        mpt_wappend(err_msg, 512, L"）。");
        MessageBoxW(owner, err_msg, ctx->title, MB_OK | MB_ICONERROR);
        mpt_free(dotnet_exe);
        mpt_free(cmd_line);
        return 5;
    }

    AllowSetForegroundWindow(pi.dwProcessId);

    if (ctx->wait_for_child) {
        DWORD child_exit = 0;
        WaitForSingleObject(pi.hProcess, INFINITE);
        GetExitCodeProcess(pi.hProcess, &child_exit);
        result_code = (int)child_exit;
    } else {
        result_code = 0;
    }

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    mpt_free(dotnet_exe);
    mpt_free(cmd_line);
    return result_code;
}

/* -------------------------------------------------------------------------
 * Background One-Click Runtime Download & Deployment Worker
 * ------------------------------------------------------------------------- */

static void mpt_post_status(MptAppContext *ctx, const WCHAR *text) {
    EnterCriticalSection(&ctx->state_lock);
    mpt_wcopy(ctx->status_text, MPT_STATUS_CAP, text);
    LeaveCriticalSection(&ctx->state_lock);
    if (ctx->hwnd) {
        PostMessageW(ctx->hwnd, WM_MPT_STATUS, 0, 0);
    }
}

static void mpt_set_error_detail(MptAppContext *ctx, const WCHAR *text) {
    EnterCriticalSection(&ctx->state_lock);
    mpt_wcopy(ctx->error_detail, MPT_STATUS_CAP, text);
    LeaveCriticalSection(&ctx->state_lock);
}

static void mpt_post_progress(MptAppContext *ctx, int permille) {
    if (ctx->hwnd) {
        PostMessageW(ctx->hwnd, WM_MPT_PROGRESS, (WPARAM)permille, 0);
    }
}

static bool mpt_is_cancelled(MptAppContext *ctx) {
    return InterlockedCompareExchange(&ctx->cancel_requested, 0, 0) != 0;
}

static void mpt_abort_active_http(MptAppContext *ctx) {
    HINTERNET req = NULL, conn = NULL, sess = NULL;
    EnterCriticalSection(&ctx->state_lock);
    req = ctx->active_request;
    conn = ctx->active_connect;
    sess = ctx->active_session;
    ctx->active_request = NULL;
    ctx->active_connect = NULL;
    ctx->active_session = NULL;
    LeaveCriticalSection(&ctx->state_lock);

    if (req) {
        WinHttpCloseHandle(req);
    }
    if (conn) {
        WinHttpCloseHandle(conn);
    }
    if (sess) {
        WinHttpCloseHandle(sess);
    }
}

static HINTERNET mpt_open_winhttp_session(void) {
    HINTERNET hSession = WinHttpOpen(
        L"MyPowerTools-Bootstrap/1.0",
        WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
        WINHTTP_NO_PROXY_NAME,
        WINHTTP_NO_PROXY_BYPASS,
        0);
    if (!hSession) {
        hSession = WinHttpOpen(
            L"MyPowerTools-Bootstrap/1.0",
            WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
            WINHTTP_NO_PROXY_NAME,
            WINHTTP_NO_PROXY_BYPASS,
            0);
    }
    if (hSession) {
        WinHttpSetTimeouts(hSession, 15000, 20000, 30000, 60000);
    }
    return hSession;
}

static bool mpt_open_http_request(
    MptAppContext *ctx,
    const WCHAR *url,
    DWORD *out_status_code,
    uint64_t *out_content_len) {
    URL_COMPONENTS uc;
    WCHAR *host = mpt_alloc_wstr(512);
    WCHAR *url_path = mpt_alloc_wstr(2048);
    HINTERNET hSession = NULL, hConnect = NULL, hRequest = NULL;
    DWORD flags = 0;
    DWORD status_code = 0;
    DWORD status_sz = sizeof(status_code);
    WCHAR cl_buf[64];
    DWORD cl_sz = sizeof(cl_buf);
    bool registered = false;
    bool ok = false;

    if (!host || !url_path) {
        goto fail;
    }

    memset(&uc, 0, sizeof(uc));
    uc.dwStructSize = sizeof(uc);
    uc.lpszHostName = host;
    uc.dwHostNameLength = 512;
    uc.lpszUrlPath = url_path;
    uc.dwUrlPathLength = 2048;

    if (!WinHttpCrackUrl(url, 0, 0, &uc) || uc.dwHostNameLength == 0) {
        mpt_set_error_detail(ctx, L"下载地址格式无效。");
        goto fail;
    }

    if (uc.nScheme == INTERNET_SCHEME_HTTPS) {
        flags = WINHTTP_FLAG_SECURE;
    } else if (uc.nScheme != INTERNET_SCHEME_HTTP) {
        mpt_set_error_detail(ctx, L"不支持的下载协议（仅支持 HTTPS / HTTP）。");
        goto fail;
    }

    hSession = mpt_open_winhttp_session();
    if (!hSession) {
        mpt_set_error_detail(ctx, L"无法初始化系统网络组件（WinHTTP）。");
        goto fail;
    }
    hConnect = WinHttpConnect(hSession, host, uc.nPort, 0);
    if (!hConnect) {
        mpt_set_error_detail(ctx, L"无法连接到下载服务器，请检查网络连接。");
        goto fail;
    }
    hRequest = WinHttpOpenRequest(
        hConnect,
        L"GET",
        url_path[0] != L'\0' ? url_path : L"/",
        NULL,
        WINHTTP_NO_REFERER,
        WINHTTP_DEFAULT_ACCEPT_TYPES,
        flags);
    if (!hRequest) {
        mpt_set_error_detail(ctx, L"创建网络请求失败。");
        goto fail;
    }

    EnterCriticalSection(&ctx->state_lock);
    if (ctx->cancel_requested) {
        LeaveCriticalSection(&ctx->state_lock);
        goto fail;
    }
    ctx->active_session = hSession;
    ctx->active_connect = hConnect;
    ctx->active_request = hRequest;
    registered = true;
    LeaveCriticalSection(&ctx->state_lock);

    if (!WinHttpSendRequest(hRequest, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(hRequest, NULL)) {
        if (!mpt_is_cancelled(ctx)) {
            mpt_set_error_detail(ctx, L"连接微软官方下载源超时或网络中断。");
        }
        goto fail;
    }

    if (!WinHttpQueryHeaders(
            hRequest,
            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            WINHTTP_HEADER_NAME_BY_INDEX,
            &status_code,
            &status_sz,
            WINHTTP_NO_HEADER_INDEX) ||
        status_code < 200 || status_code >= 300) {
        WCHAR msg[128];
        mpt_wcopy(msg, 128, L"服务器返回异常状态码 HTTP ");
        mpt_wappend_u64(msg, 128, (uint64_t)status_code);
        mpt_set_error_detail(ctx, msg);
        goto fail;
    }

    *out_status_code = status_code;
    *out_content_len = 0;
    if (WinHttpQueryHeaders(
            hRequest,
            WINHTTP_QUERY_CONTENT_LENGTH,
            WINHTTP_HEADER_NAME_BY_INDEX,
            cl_buf,
            &cl_sz,
            WINHTTP_NO_HEADER_INDEX)) {
        uint64_t v = 0;
        size_t i = 0;
        while (cl_buf[i] >= L'0' && cl_buf[i] <= L'9') {
            v = v * 10u + (uint64_t)(cl_buf[i] - L'0');
            i++;
        }
        *out_content_len = v;
    }

    ok = true;
    mpt_free(host);
    mpt_free(url_path);
    return true;

fail:
    if (!ok) {
        if (registered) {
            mpt_abort_active_http(ctx);
        } else {
            if (hRequest) {
                WinHttpCloseHandle(hRequest);
            }
            if (hConnect) {
                WinHttpCloseHandle(hConnect);
            }
            if (hSession) {
                WinHttpCloseHandle(hSession);
            }
        }
    }
    mpt_free(host);
    mpt_free(url_path);
    return false;
}

static char *mpt_http_fetch_utf8(MptAppContext *ctx, const WCHAR *url, size_t *out_len) {
    DWORD status_code = 0;
    uint64_t content_len = 0;
    size_t cap = 65536;
    size_t total = 0;
    char *buf = NULL;
    const size_t max_meta_bytes = 4 * 1024 * 1024;

    if (out_len) {
        *out_len = 0;
    }
    if (!mpt_open_http_request(ctx, url, &status_code, &content_len)) {
        return NULL;
    }
    buf = (char *)mpt_alloc(cap);
    if (!buf) {
        mpt_abort_active_http(ctx);
        return NULL;
    }

    for (;;) {
        DWORD avail = 0;
        DWORD got = 0;
        HINTERNET req;

        if (mpt_is_cancelled(ctx)) {
            mpt_free(buf);
            mpt_abort_active_http(ctx);
            return NULL;
        }

        EnterCriticalSection(&ctx->state_lock);
        req = ctx->active_request;
        LeaveCriticalSection(&ctx->state_lock);
        if (!req || !WinHttpQueryDataAvailable(req, &avail)) {
            mpt_free(buf);
            mpt_abort_active_http(ctx);
            return NULL;
        }
        if (avail == 0) {
            break;
        }
        if (total + (size_t)avail + 1 > max_meta_bytes) {
            mpt_free(buf);
            mpt_abort_active_http(ctx);
            return NULL;
        }
        if (total + (size_t)avail + 1 > cap) {
            size_t new_cap = cap * 2;
            char *new_buf;
            while (new_cap < total + (size_t)avail + 1) {
                new_cap *= 2;
            }
            new_buf = (char *)mpt_alloc(new_cap);
            if (!new_buf) {
                mpt_free(buf);
                mpt_abort_active_http(ctx);
                return NULL;
            }
            memcpy(new_buf, buf, total);
            mpt_free(buf);
            buf = new_buf;
            cap = new_cap;
        }
        if (!WinHttpReadData(req, buf + total, avail, &got)) {
            mpt_free(buf);
            mpt_abort_active_http(ctx);
            return NULL;
        }
        if (got == 0) {
            break;
        }
        total += (size_t)got;
    }

    buf[total] = '\0';
    mpt_abort_active_http(ctx);
    if (out_len) {
        *out_len = total;
    }
    return buf;
}

static void mpt_format_mb_tenths(WCHAR *dst, size_t cap, uint64_t bytes) {
    uint64_t tenths = (bytes * 10u + 524288u) / 1048576u;
    mpt_wappend_u64(dst, cap, tenths / 10u);
    mpt_wappend(dst, cap, L".");
    mpt_wappend_u64(dst, cap, tenths % 10u);
}

static bool mpt_http_download_to_file(
    MptAppContext *ctx,
    const WCHAR *url,
    const WCHAR *dest_file,
    const WCHAR *label_prefix) {
    DWORD status_code = 0;
    uint64_t content_len = 0;
    uint64_t received = 0;
    ULONGLONG last_tick = 0;
    unsigned char *chunk = (unsigned char *)mpt_alloc(65536);
    HANDLE hFile = INVALID_HANDLE_VALUE;
    bool ok = false;

    if (!chunk) {
        return false;
    }
    if (!mpt_open_http_request(ctx, url, &status_code, &content_len)) {
        mpt_free(chunk);
        return false;
    }

    hFile = CreateFileW(dest_file, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE) {
        mpt_set_error_detail(ctx, L"无法在临时目录创建安装包文件。");
        mpt_abort_active_http(ctx);
        mpt_free(chunk);
        return false;
    }

    if (content_len == 0) {
        mpt_post_progress(ctx, -1);
    } else {
        mpt_post_progress(ctx, 0);
    }

    for (;;) {
        DWORD got = 0;
        DWORD written = 0;
        HINTERNET req;
        ULONGLONG now_tick;

        if (mpt_is_cancelled(ctx)) {
            goto cleanup;
        }

        EnterCriticalSection(&ctx->state_lock);
        req = ctx->active_request;
        LeaveCriticalSection(&ctx->state_lock);

        if (!req || !WinHttpReadData(req, chunk, 65536, &got)) {
            if (!mpt_is_cancelled(ctx)) {
                mpt_set_error_detail(ctx, L"下载过程中网络连接中断。");
            }
            goto cleanup;
        }
        if (got == 0) {
            break;
        }
        if (!WriteFile(hFile, chunk, got, &written, NULL) || written != got) {
            mpt_set_error_detail(ctx, L"写入临时下载文件失败（磁盘空间可能不足）。");
            goto cleanup;
        }

        received += (uint64_t)got;
        now_tick = GetTickCount64();
        if (now_tick - last_tick >= 80 || (content_len > 0 && received >= content_len)) {
            WCHAR st[256];
            last_tick = now_tick;
            mpt_wcopy(st, 256, label_prefix);
            mpt_format_mb_tenths(st, 256, received);
            if (content_len > 0) {
                uint64_t pct = (received * 100u) / content_len;
                int permille = (int)((received * 1000u) / content_len);
                if (pct > 100u) {
                    pct = 100u;
                }
                if (permille > 1000) {
                    permille = 1000;
                }
                mpt_wappend(st, 256, L" / ");
                mpt_format_mb_tenths(st, 256, content_len);
                mpt_wappend(st, 256, L" MB (");
                mpt_wappend_u64(st, 256, pct);
                mpt_wappend(st, 256, L"%)");
                mpt_post_progress(ctx, permille);
            } else {
                mpt_wappend(st, 256, L" MB");
            }
            mpt_post_status(ctx, st);
        }
    }

    ok = (received > 0);
    if (!ok && !mpt_is_cancelled(ctx)) {
        mpt_set_error_detail(ctx, L"下载的文件内容为空。");
    }

cleanup:
    if (hFile != INVALID_HANDLE_VALUE) {
        CloseHandle(hFile);
    }
    if (!ok) {
        DeleteFileW(dest_file);
    }
    mpt_abort_active_http(ctx);
    mpt_free(chunk);
    return ok;
}

static bool mpt_run_hidden_process_and_wait(
    MptAppContext *ctx,
    const WCHAR *app_path,
    WCHAR *mutable_cmd_line,
    DWORD *out_exit_code) {
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;

    memset(&si, 0, sizeof(si));
    si.cb = sizeof(si);
    memset(&pi, 0, sizeof(pi));

    if (!CreateProcessW(
            app_path,
            mutable_cmd_line,
            NULL,
            NULL,
            FALSE,
            CREATE_NO_WINDOW,
            NULL,
            NULL,
            &si,
            &pi)) {
        return false;
    }

    for (;;) {
        DWORD wr = WaitForSingleObject(pi.hProcess, 100);
        if (wr == WAIT_OBJECT_0) {
            break;
        }
        if (mpt_is_cancelled(ctx)) {
            TerminateProcess(pi.hProcess, 1);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return false;
        }
    }

    *out_exit_code = 1;
    GetExitCodeProcess(pi.hProcess, out_exit_code);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return true;
}

static void mpt_escape_ps_single_quoted(const WCHAR *src, WCHAR *dst, size_t cap) {
    size_t i = 0, out = 0;
    while (src[i] != L'\0' && out + 1 < cap) {
        if (src[i] == L'\'') {
            if (out + 2 >= cap) {
                break;
            }
            dst[out++] = L'\'';
            dst[out++] = L'\'';
        } else {
            dst[out++] = src[i];
        }
        i++;
    }
    dst[out] = L'\0';
}

static bool mpt_extract_zip_to_dir(MptAppContext *ctx, const WCHAR *zip_path, const WCHAR *install_dir) {
    WCHAR *tar_exe = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR *cmd = mpt_alloc_wstr(MPT_CMD_CAP);
    WCHAR *esc_zip = mpt_alloc_wstr(MPT_PATH_CAP * 2);
    WCHAR *esc_dir = mpt_alloc_wstr(MPT_PATH_CAP * 2);
    DWORD exit_code = 1;
    bool ok = false;

    if (!tar_exe || !cmd || !esc_zip || !esc_dir) {
        goto cleanup;
    }
    if (!mpt_ensure_dir_recursive_w(install_dir)) {
        mpt_set_error_detail(ctx, L"无法创建运行库安装目录。");
        goto cleanup;
    }

    mpt_post_progress(ctx, -1);
    mpt_post_status(ctx, L"正在解压部署运行环境，请稍候...");

    /* Attempt 1: C:\Windows\System32\tar.exe -xf "<zip>" -C "<installDir>" */
    if (!mpt_expand_env_w(L"%SystemRoot%\\System32\\tar.exe", tar_exe, MPT_PATH_CAP) ||
        !mpt_file_exists_w(tar_exe)) {
        mpt_wcopy(tar_exe, MPT_PATH_CAP, L"C:\\Windows\\System32\\tar.exe");
    }
    if (mpt_file_exists_w(tar_exe)) {
        size_t cmd_len = 0;
        cmd[0] = L'\0';
        mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)tar_exe);
        mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)L"-xf");
        mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)zip_path);
        mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)L"-C");
        mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)install_dir);
        if (mpt_run_hidden_process_and_wait(ctx, tar_exe, cmd, &exit_code) && exit_code == 0) {
            ok = true;
            goto cleanup;
        }
        if (mpt_is_cancelled(ctx)) {
            goto cleanup;
        }
    }

    /* Attempt 2: PowerShell Expand-Archive fallback */
    mpt_escape_ps_single_quoted(zip_path, esc_zip, MPT_PATH_CAP * 2);
    mpt_escape_ps_single_quoted(install_dir, esc_dir, MPT_PATH_CAP * 2);
    {
        WCHAR *ps_script = mpt_alloc_wstr(MPT_CMD_CAP);
        if (ps_script) {
            size_t cmd_len = 0;
            mpt_wcopy(ps_script, MPT_CMD_CAP, L"Expand-Archive -LiteralPath '");
            mpt_wappend(ps_script, MPT_CMD_CAP, esc_zip);
            mpt_wappend(ps_script, MPT_CMD_CAP, L"' -DestinationPath '");
            mpt_wappend(ps_script, MPT_CMD_CAP, esc_dir);
            mpt_wappend(ps_script, MPT_CMD_CAP, L"' -Force");

            cmd[0] = L'\0';
            mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)L"powershell.exe");
            mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)L"-NoProfile");
            mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)L"-NonInteractive");
            mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)L"-ExecutionPolicy");
            mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)L"Bypass");
            mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)L"-Command");
            mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)ps_script);

            if (mpt_run_hidden_process_and_wait(ctx, NULL, cmd, &exit_code) && exit_code == 0) {
                ok = true;
            } else if (!mpt_is_cancelled(ctx)) {
                mpt_set_error_detail(ctx, L"解压 .NET 运行库压缩包失败。");
            }
            mpt_free(ps_script);
        }
    }

cleanup:
    mpt_free(tar_exe);
    mpt_free(cmd);
    mpt_free(esc_zip);
    mpt_free(esc_dir);
    return ok;
}

static bool mpt_build_temp_file_path(const WCHAR *prefix, const WCHAR *ext, WCHAR *out_path, size_t cap) {
    WCHAR temp_dir[MAX_PATH + 16];
    WCHAR leaf[128];
    DWORD n = GetTempPathW(MAX_PATH + 1, temp_dir);
    if (n == 0 || n > MAX_PATH + 1) {
        mpt_expand_env_w(L"%TEMP%", temp_dir, MAX_PATH);
    }
    mpt_wcopy(leaf, 128, prefix);
    mpt_wappend_u64(leaf, 128, (uint64_t)GetCurrentProcessId());
    mpt_wappend(leaf, 128, ext);
    return mpt_join_path_w(temp_dir, leaf, out_path, cap);
}

static bool mpt_try_zip_install_flow(MptAppContext *ctx, const WCHAR *install_dir, bool *out_had_custom_url) {
    WCHAR *custom_url = mpt_get_env_alloc_w(L"MPT_BOOTSTRAP_RUNTIME_ZIP_URL");
    WCHAR *zip_path = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR *url_buf = mpt_alloc_wstr(1024);
    bool ok = false;

    *out_had_custom_url = (custom_url && custom_url[0] != L'\0');
    if (!zip_path || !url_buf || !mpt_build_temp_file_path(L"mpt-dotnet8-", L".zip", zip_path, MPT_PATH_CAP)) {
        goto cleanup;
    }

    if (*out_had_custom_url) {
        mpt_post_status(ctx, L"正在下载运行环境压缩包...");
        if (!mpt_http_download_to_file(ctx, custom_url, zip_path, L"正在下载运行库：")) {
            goto cleanup;
        }
        ok = mpt_extract_zip_to_dir(ctx, zip_path, install_dir);
        DeleteFileW(zip_path);
        goto cleanup;
    }

    /* Official Microsoft release-metadata lookup */
    {
        int channel_major = ctx->min_major > 8 ? ctx->min_major : 8;
        size_t meta_len = 0;
        char *meta_json;
        char rt_ver[64];
        char asp_ver[64];
        WCHAR rt_ver_w[64];
        WCHAR asp_ver_w[64];

        mpt_post_progress(ctx, -1);
        mpt_post_status(ctx, L"正在查询微软官方最新 .NET 安全更新版本...");

        mpt_wcopy(url_buf, 1024, L"https://builds.dotnet.microsoft.com/dotnet/release-metadata/");
        mpt_wappend_u64(url_buf, 1024, (uint64_t)channel_major);
        mpt_wappend(url_buf, 1024, L".0/releases.json");

        meta_json = mpt_http_fetch_utf8(ctx, url_buf, &meta_len);
        if (!meta_json) {
            goto cleanup;
        }
        if (!mpt_parse_latest_runtime_version(meta_json, meta_len, rt_ver, sizeof(rt_ver), asp_ver, sizeof(asp_ver))) {
            mpt_free(meta_json);
            mpt_set_error_detail(ctx, L"解析微软官方版本元数据失败。");
            goto cleanup;
        }
        mpt_free(meta_json);

        mpt_utf8_to_wide_buf(rt_ver, rt_ver_w, 64);
        mpt_utf8_to_wide_buf(asp_ver, asp_ver_w, 64);

        /* Download & extract Microsoft.NETCore.App runtime zip */
        mpt_wcopy(url_buf, 1024, L"https://builds.dotnet.microsoft.com/dotnet/Runtime/");
        mpt_wappend(url_buf, 1024, rt_ver_w);
        mpt_wappend(url_buf, 1024, L"/dotnet-runtime-");
        mpt_wappend(url_buf, 1024, rt_ver_w);
        mpt_wappend(url_buf, 1024, L"-win-x64.zip");

        if (!mpt_http_download_to_file(ctx, url_buf, zip_path, L"正在下载 .NET 运行库：")) {
            goto cleanup;
        }
        if (!mpt_extract_zip_to_dir(ctx, zip_path, install_dir)) {
            DeleteFileW(zip_path);
            goto cleanup;
        }
        DeleteFileW(zip_path);

        /* Also deploy Microsoft.AspNetCore.App zip when required */
        if (ctx->require_aspnet) {
            WCHAR *asp_zip = mpt_alloc_wstr(MPT_PATH_CAP);
            bool asp_ok = false;
            if (asp_zip && mpt_build_temp_file_path(L"mpt-aspnet8-", L".zip", asp_zip, MPT_PATH_CAP)) {
                mpt_wcopy(url_buf, 1024, L"https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/");
                mpt_wappend(url_buf, 1024, asp_ver_w);
                mpt_wappend(url_buf, 1024, L"/aspnetcore-runtime-");
                mpt_wappend(url_buf, 1024, asp_ver_w);
                mpt_wappend(url_buf, 1024, L"-win-x64.zip");

                if (mpt_http_download_to_file(ctx, url_buf, asp_zip, L"正在下载 ASP.NET 组件：") &&
                    mpt_extract_zip_to_dir(ctx, asp_zip, install_dir)) {
                    asp_ok = true;
                }
                DeleteFileW(asp_zip);
            }
            mpt_free(asp_zip);
            if (!asp_ok) {
                goto cleanup;
            }
        }
        ok = true;
    }

cleanup:
    if (zip_path && zip_path[0] != L'\0') {
        DeleteFileW(zip_path);
    }
    mpt_free(custom_url);
    mpt_free(zip_path);
    mpt_free(url_buf);
    return ok;
}

static bool mpt_try_fallback_exe_installer(MptAppContext *ctx) {
    WCHAR *exe_path = mpt_alloc_wstr(MPT_PATH_CAP);
    WCHAR *cmd = mpt_alloc_wstr(MPT_CMD_CAP);
    DWORD exit_code = 1;
    bool ok = false;

    if (!exe_path || !cmd ||
        !mpt_build_temp_file_path(L"mpt-dotnet8-installer-", L".exe", exe_path, MPT_PATH_CAP)) {
        goto cleanup;
    }

    mpt_post_status(ctx, L"正在通过微软备用通道下载官方安装程序...");
    if (!mpt_http_download_to_file(
            ctx,
            L"https://aka.ms/dotnet/8.0/dotnet-runtime-win-x64.exe",
            exe_path,
            L"正在下载官方安装器：")) {
        goto cleanup;
    }

    mpt_post_progress(ctx, -1);
    mpt_post_status(ctx, L"正在执行官方静默安装程序，请稍候...");
    {
        size_t cmd_len = 0;
        cmd[0] = L'\0';
        mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)exe_path);
        mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)L"/install");
        mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)L"/passive");
        mpt_cmd_append_arg_u16((uint16_t *)cmd, MPT_CMD_CAP, &cmd_len, (const uint16_t *)L"/norestart");

        if (mpt_run_hidden_process_and_wait(ctx, exe_path, cmd, &exit_code) &&
            (exit_code == 0 || exit_code == 3010)) {
            ok = true;
        } else if (!mpt_is_cancelled(ctx)) {
            mpt_set_error_detail(ctx, L"官方安装程序未完成安装。");
        }
    }

cleanup:
    if (exe_path && exe_path[0] != L'\0') {
        DeleteFileW(exe_path);
    }
    mpt_free(exe_path);
    mpt_free(cmd);
    return ok;
}

static DWORD WINAPI mpt_install_worker_proc(LPVOID param) {
    MptAppContext *ctx = (MptAppContext *)param;
    WCHAR *install_dir = mpt_get_env_alloc_w(L"MPT_BOOTSTRAP_INSTALL_DIR");
    bool had_custom_url = false;
    bool ok = false;

    if (!install_dir || install_dir[0] == L'\0') {
        mpt_free(install_dir);
        install_dir = mpt_alloc_wstr(MPT_PATH_CAP);
        if (install_dir) {
            mpt_expand_env_w(L"%LOCALAPPDATA%\\Microsoft\\dotnet", install_dir, MPT_PATH_CAP);
        }
    }

    if (install_dir) {
        EnterCriticalSection(&ctx->state_lock);
        mpt_wcopy(ctx->last_install_dir, MPT_PATH_CAP, install_dir);
        LeaveCriticalSection(&ctx->state_lock);

        ok = mpt_try_zip_install_flow(ctx, install_dir, &had_custom_url);
        if (!ok && !had_custom_url && !mpt_is_cancelled(ctx) && ctx->min_major <= 8 && !ctx->require_aspnet) {
            ok = mpt_try_fallback_exe_installer(ctx);
        }
    }

    mpt_free(install_dir);

    if (ctx->hwnd) {
        WPARAM result = mpt_is_cancelled(ctx) ? 2 : (ok ? 1 : 0);
        PostMessageW(ctx->hwnd, WM_MPT_INSTALL_DONE, result, 0);
    }
    return 0;
}

/* -------------------------------------------------------------------------
 * Per-Monitor DPI v2 & Modern Native Win32 Missing-Runtime Dialog
 * ------------------------------------------------------------------------- */

typedef BOOL(WINAPI *PfnSetProcessDpiAwarenessContext)(HANDLE);
typedef UINT(WINAPI *PfnGetDpiForWindow)(HWND);

static void mpt_enable_per_monitor_dpi_v2(void) {
    HMODULE hUser32 = GetModuleHandleW(L"user32.dll");
    if (hUser32) {
        PfnSetProcessDpiAwarenessContext pSetCtx =
            (PfnSetProcessDpiAwarenessContext)(void *)GetProcAddress(hUser32, "SetProcessDpiAwarenessContext");
        if (pSetCtx && pSetCtx(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) {
            return;
        }
    }
}

static UINT mpt_query_dpi(HWND hwnd) {
    HMODULE hUser32 = GetModuleHandleW(L"user32.dll");
    if (hUser32 && hwnd) {
        PfnGetDpiForWindow pGetDpi = (PfnGetDpiForWindow)(void *)GetProcAddress(hUser32, "GetDpiForWindow");
        if (pGetDpi) {
            UINT d = pGetDpi(hwnd);
            if (d >= 72 && d <= 480) {
                return d;
            }
        }
    }
    {
        HDC hdc = GetDC(hwnd);
        UINT d = 96;
        if (hdc) {
            int px = GetDeviceCaps(hdc, LOGPIXELSX);
            if (px >= 72 && px <= 480) {
                d = (UINT)px;
            }
            ReleaseDC(hwnd, hdc);
        }
        return d;
    }
}

static int mpt_scale(int px, UINT dpi) {
    return MulDiv(px, (int)dpi, 96);
}

static void mpt_rebuild_fonts_and_layout(MptAppContext *ctx, UINT dpi) {
    HFONT old_heading = ctx->font_heading;
    HFONT old_body = ctx->font_body;
    int margin, content_w, y, btn_h, btn_y, gap, w_install, w_web, w_cancel, x;

    ctx->dpi = dpi;
    ctx->font_heading = CreateFontW(
        -mpt_scale(17, dpi), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
        DEFAULT_PITCH | FF_DONTCARE, L"Microsoft YaHei UI");
    ctx->font_body = CreateFontW(
        -mpt_scale(13, dpi), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
        DEFAULT_PITCH | FF_DONTCARE, L"Microsoft YaHei UI");

    margin = mpt_scale(22, dpi);
    content_w = mpt_scale(484, dpi);
    y = mpt_scale(20, dpi);

    SetWindowPos(ctx->lbl_heading, NULL, margin, y, content_w, mpt_scale(26, dpi), SWP_NOZORDER);
    y += mpt_scale(34, dpi);

    SetWindowPos(ctx->lbl_desc, NULL, margin, y, content_w, mpt_scale(68, dpi), SWP_NOZORDER);
    y += mpt_scale(76, dpi);

    SetWindowPos(ctx->progress_bar, NULL, margin, y, content_w, mpt_scale(20, dpi), SWP_NOZORDER);
    y += mpt_scale(26, dpi);

    SetWindowPos(ctx->lbl_status, NULL, margin, y, content_w, mpt_scale(22, dpi), SWP_NOZORDER);

    btn_h = mpt_scale(32, dpi);
    btn_y = y + mpt_scale(32, dpi);
    gap = mpt_scale(10, dpi);
    w_install = mpt_scale(162, dpi);
    w_web = mpt_scale(162, dpi);
    w_cancel = mpt_scale(96, dpi);
    x = margin + content_w - (w_install + gap + w_web + gap + w_cancel);

    SetWindowPos(ctx->btn_install, NULL, x, btn_y, w_install, btn_h, SWP_NOZORDER);
    x += w_install + gap;
    SetWindowPos(ctx->btn_web, NULL, x, btn_y, w_web, btn_h, SWP_NOZORDER);
    x += w_web + gap;
    SetWindowPos(ctx->btn_cancel, NULL, x, btn_y, w_cancel, btn_h, SWP_NOZORDER);

    SendMessageW(ctx->lbl_heading, WM_SETFONT, (WPARAM)ctx->font_heading, TRUE);
    SendMessageW(ctx->lbl_desc, WM_SETFONT, (WPARAM)ctx->font_body, TRUE);
    SendMessageW(ctx->lbl_status, WM_SETFONT, (WPARAM)ctx->font_body, TRUE);
    SendMessageW(ctx->btn_install, WM_SETFONT, (WPARAM)ctx->font_body, TRUE);
    SendMessageW(ctx->btn_web, WM_SETFONT, (WPARAM)ctx->font_body, TRUE);
    SendMessageW(ctx->btn_cancel, WM_SETFONT, (WPARAM)ctx->font_body, TRUE);

    if (old_heading) {
        DeleteObject(old_heading);
    }
    if (old_body) {
        DeleteObject(old_body);
    }
    InvalidateRect(ctx->hwnd, NULL, TRUE);
}

static void mpt_set_progress_ui(MptAppContext *ctx, int permille) {
    LONG_PTR style = GetWindowLongPtrW(ctx->progress_bar, GWL_STYLE);
    if (permille < 0) {
        if (!ctx->is_marquee) {
            ctx->is_marquee = true;
            SetWindowLongPtrW(ctx->progress_bar, GWL_STYLE, style | PBS_MARQUEE);
            SendMessageW(ctx->progress_bar, PBM_SETMARQUEE, TRUE, 30);
        }
    } else {
        if (ctx->is_marquee) {
            ctx->is_marquee = false;
            SendMessageW(ctx->progress_bar, PBM_SETMARQUEE, FALSE, 0);
            SetWindowLongPtrW(ctx->progress_bar, GWL_STYLE, style & ~(LONG_PTR)PBS_MARQUEE);
            SendMessageW(ctx->progress_bar, PBM_SETRANGE32, 0, 1000);
        }
        SendMessageW(ctx->progress_bar, PBM_SETPOS, (WPARAM)permille, 0);
    }
}

static void mpt_open_official_download_page(MptAppContext *ctx) {
    WCHAR url[128];
    int major = ctx->min_major > 8 ? ctx->min_major : 8;
    mpt_wcopy(url, 128, L"https://dotnet.microsoft.com/download/dotnet/");
    mpt_wappend_u64(url, 128, (uint64_t)major);
    mpt_wappend(url, 128, L".0");
    ShellExecuteW(ctx->hwnd, L"open", url, NULL, NULL, SW_SHOWNORMAL);
}

static void mpt_start_install_worker(MptAppContext *ctx) {
    if (ctx->install_running) {
        return;
    }
    if (ctx->worker_thread) {
        CloseHandle(ctx->worker_thread);
        ctx->worker_thread = NULL;
    }

    ctx->install_running = true;
    InterlockedExchange(&ctx->cancel_requested, 0);
    mpt_set_error_detail(ctx, L"");

    EnableWindow(ctx->btn_install, FALSE);
    SetWindowTextW(ctx->lbl_status, L"正在连接微软官方下载源...");
    mpt_set_progress_ui(ctx, -1);

    ctx->worker_thread = CreateThread(NULL, 0, mpt_install_worker_proc, ctx, 0, NULL);
    if (!ctx->worker_thread) {
        ctx->install_running = false;
        mpt_set_progress_ui(ctx, 0);
        EnableWindow(ctx->btn_install, TRUE);
        SetWindowTextW(ctx->lbl_status, L"启动后台安装线程失败，请重试。");
    }
}

static void mpt_request_cancel_or_close(MptAppContext *ctx, int code) {
    ctx->exit_code = code;
    if (ctx->install_running) {
        if (InterlockedExchange(&ctx->cancel_requested, 1) == 0) {
            SetWindowTextW(ctx->lbl_status, L"正在取消下载...");
            mpt_abort_active_http(ctx);
            if (code == 42) {
                DestroyWindow(ctx->hwnd);
            }
            return;
        }
    }
    DestroyWindow(ctx->hwnd);
}

static LRESULT CALLBACK mpt_dialog_wndproc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    MptAppContext *ctx = g_ctx;

    switch (msg) {
        case WM_CREATE: {
            WCHAR heading_text[96];
            WCHAR *desc_text = mpt_alloc_wstr(512);
            int disp_major = ctx->min_major > 8 ? ctx->min_major : 8;

            ctx->hwnd = hwnd;
            mpt_wcopy(heading_text, 96, L"需要安装 .NET ");
            mpt_wappend_u64(heading_text, 96, (uint64_t)disp_major);
            mpt_wappend(heading_text, 96, L".0+ 运行环境");

            if (desc_text) {
                mpt_wcopy(desc_text, 512, L"当前电脑尚未检测到 .NET ");
                mpt_wappend_u64(desc_text, 512, (uint64_t)disp_major);
                mpt_wappend(
                    desc_text,
                    512,
                    L".0 或更高版本（支持 .NET 8 / 9 / 10+ 自动兼容）。\r\n"
                    L"点击【一键安装并启动】将从微软官方自动下载运行库并部署到当前用户目录"
                    L"（免管理员权限，装一次所有小工具通用），完成后自动打开本工具。");
            }

            ctx->lbl_heading = CreateWindowExW(
                0, L"STATIC", heading_text, WS_CHILD | WS_VISIBLE,
                0, 0, 10, 10, hwnd, (HMENU)(INT_PTR)IDC_LBL_HEADING, ctx->hInstance, NULL);
            ctx->lbl_desc = CreateWindowExW(
                0, L"STATIC", desc_text ? desc_text : L"", WS_CHILD | WS_VISIBLE,
                0, 0, 10, 10, hwnd, (HMENU)(INT_PTR)IDC_LBL_DESC, ctx->hInstance, NULL);
            mpt_free(desc_text);
            ctx->progress_bar = CreateWindowExW(
                0, PROGRESS_CLASSW, NULL, WS_CHILD | WS_VISIBLE | PBS_SMOOTH,
                0, 0, 10, 10, hwnd, (HMENU)(INT_PTR)IDC_PROGRESS, ctx->hInstance, NULL);
            SendMessageW(ctx->progress_bar, PBM_SETRANGE32, 0, 1000);

            ctx->lbl_status = CreateWindowExW(
                0, L"STATIC", L"准备就绪（下载约 30~55 MB）", WS_CHILD | WS_VISIBLE,
                0, 0, 10, 10, hwnd, (HMENU)(INT_PTR)IDC_LBL_STATUS, ctx->hInstance, NULL);
            ctx->btn_install = CreateWindowExW(
                0, L"BUTTON", L"【一键安装并启动】", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
                0, 0, 10, 10, hwnd, (HMENU)(INT_PTR)IDC_BTN_INSTALL, ctx->hInstance, NULL);
            ctx->btn_web = CreateWindowExW(
                0, L"BUTTON", L"【打开微软下载页】", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                0, 0, 10, 10, hwnd, (HMENU)(INT_PTR)IDC_BTN_WEB, ctx->hInstance, NULL);
            ctx->btn_cancel = CreateWindowExW(
                0, L"BUTTON", L"【取消】", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                0, 0, 10, 10, hwnd, (HMENU)(INT_PTR)IDC_BTN_CANCEL, ctx->hInstance, NULL);

            mpt_rebuild_fonts_and_layout(ctx, mpt_query_dpi(hwnd));
            SetFocus(ctx->btn_install);
            return 0;
        }

        case WM_SHOWWINDOW:
            if (wParam) {
                SetTimer(hwnd, IDT_UI_READY, 15, NULL);
            }
            break;

        case WM_TIMER:
            if (wParam == IDT_UI_READY) {
                WCHAR *zip_override;
                KillTimer(hwnd, IDT_UI_READY);
                mpt_write_ui_dump_json(ctx);

                if (ctx->cli.has_autoclose_ms) {
                    UINT delay = (UINT)(ctx->cli.autoclose_ms > 10 ? ctx->cli.autoclose_ms : 10);
                    SetTimer(hwnd, IDT_AUTOCLOSE, delay, NULL);
                }

                zip_override = mpt_get_env_alloc_w(L"MPT_BOOTSTRAP_RUNTIME_ZIP_URL");
                if (ctx->cli.auto_install || (zip_override && zip_override[0] != L'\0')) {
                    mpt_start_install_worker(ctx);
                }
                mpt_free(zip_override);
                return 0;
            }
            if (wParam == IDT_AUTOCLOSE) {
                KillTimer(hwnd, IDT_AUTOCLOSE);
                mpt_request_cancel_or_close(ctx, 42);
                return 0;
            }
            break;

        case WM_DPICHANGED: {
            UINT new_dpi = HIWORD(wParam);
            const RECT *suggested = (const RECT *)lParam;
            if (suggested) {
                SetWindowPos(
                    hwnd,
                    NULL,
                    suggested->left,
                    suggested->top,
                    suggested->right - suggested->left,
                    suggested->bottom - suggested->top,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
            mpt_rebuild_fonts_and_layout(ctx, new_dpi);
            return 0;
        }

        case WM_CTLCOLORSTATIC: {
            HDC hdc = (HDC)wParam;
            HWND ctrl = (HWND)lParam;
            SetBkMode(hdc, TRANSPARENT);
            if (ctrl == ctx->lbl_heading) {
                SetTextColor(hdc, RGB(20, 24, 31));
            } else {
                SetTextColor(hdc, RGB(60, 66, 75));
            }
            return (LRESULT)GetStockObject(WHITE_BRUSH);
        }

        case WM_COMMAND: {
            WORD id = LOWORD(wParam);
            if (id == IDC_BTN_INSTALL) {
                mpt_start_install_worker(ctx);
                return 0;
            }
            if (id == IDC_BTN_WEB) {
                mpt_open_official_download_page(ctx);
                return 0;
            }
            if (id == IDC_BTN_CANCEL || id == IDCANCEL) {
                mpt_request_cancel_or_close(ctx, 1);
                return 0;
            }
            break;
        }

        case WM_MPT_STATUS: {
            WCHAR *local_status = mpt_alloc_wstr(MPT_STATUS_CAP);
            if (local_status) {
                EnterCriticalSection(&ctx->state_lock);
                mpt_wcopy(local_status, MPT_STATUS_CAP, ctx->status_text);
                LeaveCriticalSection(&ctx->state_lock);
                SetWindowTextW(ctx->lbl_status, local_status);
                mpt_free(local_status);
            }
            return 0;
        }

        case WM_MPT_PROGRESS:
            mpt_set_progress_ui(ctx, (int)wParam);
            return 0;

        case WM_MPT_INSTALL_DONE: {
            ctx->install_running = false;
            if (wParam == 2) {
                DestroyWindow(hwnd);
                return 0;
            }
            if (wParam == 1) {
                WCHAR *preferred_dir = mpt_alloc_wstr(MPT_PATH_CAP);
                if (preferred_dir) {
                    EnterCriticalSection(&ctx->state_lock);
                    mpt_wcopy(preferred_dir, MPT_PATH_CAP, ctx->last_install_dir);
                    LeaveCriticalSection(&ctx->state_lock);

                    mpt_discover_dotnet_runtime(ctx, true, preferred_dir);
                    mpt_free(preferred_dir);
                }
                if (ctx->found_dotnet) {
                    mpt_set_progress_ui(ctx, 1000);
                    SetWindowTextW(ctx->lbl_status, L"安装完成，正在打开工具...");
                    UpdateWindow(hwnd);
                    ShowWindow(hwnd, SW_HIDE);
                    ctx->exit_code = mpt_launch_target_app(ctx, NULL);
                    DestroyWindow(hwnd);
                    return 0;
                }
                mpt_set_error_detail(ctx, L"运行库已解压，但未能检测到有效的 .NET 8.0+ CoreCLR 文件。");
            }

            /* Error recovery UI */
            {
                WCHAR *detail = mpt_alloc_wstr(MPT_STATUS_CAP);
                WCHAR *prompt = mpt_alloc_wstr(768);
                mpt_set_progress_ui(ctx, 0);
                EnableWindow(ctx->btn_install, TRUE);
                SetFocus(ctx->btn_install);

                if (detail && prompt) {
                    EnterCriticalSection(&ctx->state_lock);
                    mpt_wcopy(detail, MPT_STATUS_CAP, ctx->error_detail);
                    LeaveCriticalSection(&ctx->state_lock);
                    if (detail[0] == L'\0') {
                        mpt_wcopy(detail, MPT_STATUS_CAP, L"自动下载或部署 .NET 运行库失败。");
                    }

                    SetWindowTextW(ctx->lbl_status, detail);

                    mpt_wcopy(prompt, 768, L"自动安装未能完成：\r\n");
                    mpt_wappend(prompt, 768, detail);
                    mpt_wappend(prompt, 768, L"\r\n\r\n您可以点击【一键安装并启动】重试，或立即打开微软官方下载页面手动安装。\r\n是否现在打开微软官方下载页？");

                    if (MessageBoxW(hwnd, prompt, ctx->title, MB_YESNO | MB_ICONWARNING) == IDYES) {
                        mpt_open_official_download_page(ctx);
                    }
                }
                mpt_free(detail);
                mpt_free(prompt);
            }
            return 0;
        }

        case WM_CLOSE:
            mpt_request_cancel_or_close(ctx, 1);
            return 0;

        case WM_DESTROY:
            if (ctx->font_heading) {
                DeleteObject(ctx->font_heading);
                ctx->font_heading = NULL;
            }
            if (ctx->font_body) {
                DeleteObject(ctx->font_body);
                ctx->font_body = NULL;
            }
            ctx->hwnd = NULL;
            PostQuitMessage(ctx->exit_code);
            return 0;
    }

    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

static int mpt_run_missing_runtime_dialog(MptAppContext *ctx) {
    INITCOMMONCONTROLSEX icc;
    WNDCLASSEXW wc;
    WCHAR win_title[320];
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    UINT dpi;
    RECT rc;
    int win_w, win_h, pos_x, pos_y;
    POINT cursor_pt;
    HMONITOR hMon;
    MONITORINFO mi;
    MSG msg;

    mpt_enable_per_monitor_dpi_v2();

    icc.dwSize = sizeof(icc);
    icc.dwICC = ICC_PROGRESS_CLASS | ICC_STANDARD_CLASSES;
    InitCommonControlsEx(&icc);

    ctx->app_icon = LoadIconW(ctx->hInstance, MAKEINTRESOURCEW(IDI_MPT_APPICON));
    if (!ctx->app_icon) {
        ctx->app_icon = LoadIconW(NULL, IDI_APPLICATION);
    }

    memset(&wc, 0, sizeof(wc));
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = mpt_dialog_wndproc;
    wc.hInstance = ctx->hInstance;
    wc.hIcon = ctx->app_icon;
    wc.hIconSm = ctx->app_icon;
    wc.hCursor = LoadCursorW(NULL, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)GetStockObject(WHITE_BRUSH);
    wc.lpszClassName = L"MptNativeBootstrapDialogClass";
    RegisterClassExW(&wc);

    mpt_wcopy(win_title, 320, ctx->title);
    mpt_wappend(win_title, 320, L" - 运行环境准备");

    dpi = mpt_query_dpi(NULL);
    rc.left = 0;
    rc.top = 0;
    rc.right = mpt_scale(528, dpi);
    rc.bottom = mpt_scale(264, dpi);
    AdjustWindowRectEx(&rc, style, FALSE, 0);
    win_w = rc.right - rc.left;
    win_h = rc.bottom - rc.top;

    pos_x = CW_USEDEFAULT;
    pos_y = CW_USEDEFAULT;
    if (GetCursorPos(&cursor_pt)) {
        hMon = MonitorFromPoint(cursor_pt, MONITOR_DEFAULTTONEAREST);
        memset(&mi, 0, sizeof(mi));
        mi.cbSize = sizeof(mi);
        if (GetMonitorInfoW(hMon, &mi)) {
            int work_w = mi.rcWork.right - mi.rcWork.left;
            int work_h = mi.rcWork.bottom - mi.rcWork.top;
            pos_x = mi.rcWork.left + (work_w - win_w) / 2;
            pos_y = mi.rcWork.top + (work_h - win_h) / 2;
        }
    }

    ctx->exit_code = 1;
    ctx->hwnd = CreateWindowExW(
        0,
        wc.lpszClassName,
        win_title,
        style,
        pos_x,
        pos_y,
        win_w,
        win_h,
        NULL,
        NULL,
        ctx->hInstance,
        NULL);
    if (!ctx->hwnd) {
        return 1;
    }

    ShowWindow(ctx->hwnd, SW_SHOWNORMAL);
    UpdateWindow(ctx->hwnd);

    while (GetMessageW(&msg, NULL, 0, 0) > 0) {
        if (!IsDialogMessageW(ctx->hwnd, &msg)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    if (ctx->worker_thread) {
        InterlockedExchange(&ctx->cancel_requested, 1);
        mpt_abort_active_http(ctx);
        WaitForSingleObject(ctx->worker_thread, 3000);
        CloseHandle(ctx->worker_thread);
        ctx->worker_thread = NULL;
    }

    return ctx->exit_code;
}

/* -------------------------------------------------------------------------
 * Process Entry Point (WinMainCRTStartup)
 * ------------------------------------------------------------------------- */

static int mpt_main(void) {
    MptAppContext *ctx = (MptAppContext *)mpt_alloc(sizeof(MptAppContext));
    int exit_code = 0;

    if (!ctx) {
        return 1;
    }
    g_ctx = ctx;
    ctx->hInstance = GetModuleHandleW(NULL);
    InitializeCriticalSection(&ctx->state_lock);

    if (!mpt_init_exe_paths(ctx) || !mpt_parse_process_cli(ctx)) {
        DeleteCriticalSection(&ctx->state_lock);
        mpt_free(ctx);
        return 2;
    }

    mpt_resolve_configuration(ctx);
    if (ctx->config_parse_failed && !ctx->cli.has_probe_json) {
        WCHAR msg[768];
        mpt_wcopy(msg, 768, L"启动配置文件格式错误，无法解析 JSON：\r\n");
        mpt_wappend(msg, 768, ctx->config_path);
        MessageBoxW(NULL, msg, ctx->exe_stem, MB_OK | MB_ICONERROR);
        DeleteCriticalSection(&ctx->state_lock);
        mpt_free(ctx);
        return 2;
    }

    mpt_discover_dotnet_runtime(ctx, false, NULL);

    if (ctx->cli.has_probe_json) {
        exit_code = mpt_write_probe_json_and_exit_code(ctx);
        DeleteCriticalSection(&ctx->state_lock);
        mpt_free(ctx);
        return exit_code;
    }

    if (ctx->found_dotnet) {
        exit_code = mpt_launch_target_app(ctx, NULL);
    } else {
        exit_code = mpt_run_missing_runtime_dialog(ctx);
    }

    DeleteCriticalSection(&ctx->state_lock);
    mpt_free(ctx);
    return exit_code;
}

void WINAPI WinMainCRTStartup(void) {
    ExitProcess((UINT)mpt_main());
}
