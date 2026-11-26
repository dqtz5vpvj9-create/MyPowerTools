#ifndef MPT_BOOTSTRAP_CORE_H
#define MPT_BOOTSTRAP_CORE_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(__GNUC__) || defined(__clang__)
#define MPT_CORE_FN static __attribute__((unused))
#else
#define MPT_CORE_FN static
#endif

#define MPT_MAX_STR 512
#define MPT_MAX_PROBE_DIRS 24
#define MPT_MAX_DEFAULT_ARGS 32
#define MPT_MAX_FORWARDED_ARGS 128
#define MPT_MAX_JSON_DEPTH 32

typedef struct MptBootConfig {
    char title[MPT_MAX_STR];
    bool has_title;

    char entry_dll[MPT_MAX_STR];
    bool has_entry_dll;

    char probe_dirs[MPT_MAX_PROBE_DIRS][MPT_MAX_STR];
    size_t probe_dir_count;
    bool has_probe_dirs;

    char default_args[MPT_MAX_DEFAULT_ARGS][MPT_MAX_STR];
    size_t default_arg_count;
    bool has_default_args;

    int min_major;
    bool require_aspnet;
    bool wait_for_child;
} MptBootConfig;

typedef struct MptDotnetVersion {
    int major;
    int minor;
    int patch;
    bool is_prerelease;
    char prerelease[64];
} MptDotnetVersion;

typedef struct MptBootstrapCliOptions {
    bool force_wait;
    bool force_no_dotnet;
    bool auto_install;

    bool has_probe_json;
    char probe_json_path[MPT_MAX_STR];

    bool has_ui_dump;
    char ui_dump_path[MPT_MAX_STR];

    bool has_autoclose_ms;
    int autoclose_ms;

    /* Indices into original argv[1..argc-1] for non-bootstrapper arguments */
    int forwarded_indices[MPT_MAX_FORWARDED_ARGS];
    size_t forwarded_count;
} MptBootstrapCliOptions;

/* -------------------------------------------------------------------------
 * Freestanding String & Formatting Utilities
 * ------------------------------------------------------------------------- */

MPT_CORE_FN size_t mpt_strlen(const char *s) {
    size_t n = 0;
    if (!s) {
        return 0;
    }
    while (s[n] != '\0') {
        n++;
    }
    return n;
}

MPT_CORE_FN size_t mpt_u16_len(const uint16_t *s) {
    size_t n = 0;
    if (!s) {
        return 0;
    }
    while (s[n] != 0) {
        n++;
    }
    return n;
}

MPT_CORE_FN bool mpt_streq(const char *a, const char *b) {
    size_t i = 0;
    if (!a || !b) {
        return a == b;
    }
    while (a[i] != '\0' && b[i] != '\0') {
        if (a[i] != b[i]) {
            return false;
        }
        i++;
    }
    return a[i] == b[i];
}

MPT_CORE_FN bool mpt_starts_with(const char *s, const char *prefix) {
    size_t i = 0;
    if (!s || !prefix) {
        return false;
    }
    while (prefix[i] != '\0') {
        if (s[i] != prefix[i]) {
            return false;
        }
        i++;
    }
    return true;
}

MPT_CORE_FN bool mpt_copy_str(char *dst, size_t cap, const char *src) {
    size_t i = 0;
    if (!dst || cap == 0) {
        return false;
    }
    if (!src) {
        dst[0] = '\0';
        return true;
    }
    while (src[i] != '\0') {
        if (i + 1 >= cap) {
            dst[0] = '\0';
            return false;
        }
        dst[i] = src[i];
        i++;
    }
    dst[i] = '\0';
    return true;
}

MPT_CORE_FN bool mpt_append_char(char *dst, size_t cap, size_t *io_len, char c) {
    if (!dst || !io_len || *io_len + 1 >= cap) {
        return false;
    }
    dst[*io_len] = c;
    (*io_len)++;
    dst[*io_len] = '\0';
    return true;
}

MPT_CORE_FN bool mpt_append_str(char *dst, size_t cap, size_t *io_len, const char *src) {
    size_t i = 0;
    if (!src) {
        return true;
    }
    while (src[i] != '\0') {
        if (!mpt_append_char(dst, cap, io_len, src[i])) {
            return false;
        }
        i++;
    }
    return true;
}

MPT_CORE_FN bool mpt_append_u64_dec(char *dst, size_t cap, size_t *io_len, uint64_t val) {
    char tmp[24];
    size_t n = 0;
    if (val == 0) {
        return mpt_append_char(dst, cap, io_len, '0');
    }
    while (val > 0) {
        tmp[n++] = (char)('0' + (val % 10));
        val /= 10;
    }
    while (n > 0) {
        if (!mpt_append_char(dst, cap, io_len, tmp[--n])) {
            return false;
        }
    }
    return true;
}

MPT_CORE_FN bool mpt_append_u16_char(uint16_t *dst, size_t cap, size_t *io_len, uint16_t ch) {
    if (!dst || !io_len || *io_len + 1 >= cap) {
        return false;
    }
    dst[*io_len] = ch;
    (*io_len)++;
    dst[*io_len] = 0;
    return true;
}

MPT_CORE_FN bool mpt_append_u16_str(uint16_t *dst, size_t cap, size_t *io_len, const uint16_t *src) {
    size_t i = 0;
    if (!src) {
        return true;
    }
    while (src[i] != 0) {
        if (!mpt_append_u16_char(dst, cap, io_len, src[i])) {
            return false;
        }
        i++;
    }
    return true;
}

/* -------------------------------------------------------------------------
 * Minimal Safe RFC 8259 JSON Parser for .mptboot.json
 * ------------------------------------------------------------------------- */

typedef struct MptJsonCursor {
    const char *src;
    size_t len;
    size_t pos;
} MptJsonCursor;

MPT_CORE_FN void mpt_boot_config_init(MptBootConfig *cfg) {
    size_t i = 0;
    if (!cfg) {
        return;
    }
    cfg->title[0] = '\0';
    cfg->has_title = false;
    cfg->entry_dll[0] = '\0';
    cfg->has_entry_dll = false;
    for (i = 0; i < MPT_MAX_PROBE_DIRS; i++) {
        cfg->probe_dirs[i][0] = '\0';
    }
    cfg->probe_dir_count = 0;
    cfg->has_probe_dirs = false;
    for (i = 0; i < MPT_MAX_DEFAULT_ARGS; i++) {
        cfg->default_args[i][0] = '\0';
    }
    cfg->default_arg_count = 0;
    cfg->has_default_args = false;
    cfg->min_major = 8;
    cfg->require_aspnet = false;
    cfg->wait_for_child = false;
}

MPT_CORE_FN void mpt_json_skip_ws(MptJsonCursor *cur) {
    while (cur->pos < cur->len) {
        char c = cur->src[cur->pos];
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n') {
            cur->pos++;
        } else {
            break;
        }
    }
}

MPT_CORE_FN int mpt_hex_digit(char c) {
    if (c >= '0' && c <= '9') {
        return c - '0';
    }
    if (c >= 'a' && c <= 'f') {
        return 10 + (c - 'a');
    }
    if (c >= 'A' && c <= 'F') {
        return 10 + (c - 'A');
    }
    return -1;
}

MPT_CORE_FN bool mpt_json_parse_hex4(MptJsonCursor *cur, uint16_t *out_val) {
    uint16_t v = 0;
    int i = 0;
    if (cur->pos + 4 > cur->len) {
        return false;
    }
    for (i = 0; i < 4; i++) {
        int d = mpt_hex_digit(cur->src[cur->pos++]);
        if (d < 0) {
            return false;
        }
        v = (uint16_t)((v << 4) | (uint16_t)d);
    }
    *out_val = v;
    return true;
}

MPT_CORE_FN bool mpt_append_utf8_codepoint(char *dst, size_t cap, size_t *io_len, uint32_t cp) {
    if (cp <= 0x7Fu) {
        return mpt_append_char(dst, cap, io_len, (char)cp);
    }
    if (cp <= 0x7FFu) {
        return mpt_append_char(dst, cap, io_len, (char)(0xC0u | (cp >> 6))) &&
               mpt_append_char(dst, cap, io_len, (char)(0x80u | (cp & 0x3Fu)));
    }
    if (cp <= 0xFFFFu) {
        if (cp >= 0xD800u && cp <= 0xDFFFu) {
            return false;
        }
        return mpt_append_char(dst, cap, io_len, (char)(0xE0u | (cp >> 12))) &&
               mpt_append_char(dst, cap, io_len, (char)(0x80u | ((cp >> 6) & 0x3Fu))) &&
               mpt_append_char(dst, cap, io_len, (char)(0x80u | (cp & 0x3Fu)));
    }
    if (cp <= 0x10FFFFu) {
        return mpt_append_char(dst, cap, io_len, (char)(0xF0u | (cp >> 18))) &&
               mpt_append_char(dst, cap, io_len, (char)(0x80u | ((cp >> 12) & 0x3Fu))) &&
               mpt_append_char(dst, cap, io_len, (char)(0x80u | ((cp >> 6) & 0x3Fu))) &&
               mpt_append_char(dst, cap, io_len, (char)(0x80u | (cp & 0x3Fu)));
    }
    return false;
}

MPT_CORE_FN bool mpt_json_parse_string(MptJsonCursor *cur, char *out_buf, size_t out_cap) {
    size_t out_len = 0;
    mpt_json_skip_ws(cur);
    if (cur->pos >= cur->len || cur->src[cur->pos] != '"') {
        return false;
    }
    cur->pos++;

    if (out_buf && out_cap > 0) {
        out_buf[0] = '\0';
    }

    while (cur->pos < cur->len) {
        unsigned char ch = (unsigned char)cur->src[cur->pos++];
        if (ch == '"') {
            return true;
        }
        if (ch < 0x20u) {
            return false;
        }
        if (ch == '\\') {
            char esc;
            if (cur->pos >= cur->len) {
                return false;
            }
            esc = cur->src[cur->pos++];
            switch (esc) {
                case '"':
                case '\\':
                case '/':
                    if (out_buf && !mpt_append_char(out_buf, out_cap, &out_len, esc)) {
                        return false;
                    }
                    break;
                case 'b':
                    if (out_buf && !mpt_append_char(out_buf, out_cap, &out_len, '\b')) {
                        return false;
                    }
                    break;
                case 'f':
                    if (out_buf && !mpt_append_char(out_buf, out_cap, &out_len, '\f')) {
                        return false;
                    }
                    break;
                case 'n':
                    if (out_buf && !mpt_append_char(out_buf, out_cap, &out_len, '\n')) {
                        return false;
                    }
                    break;
                case 'r':
                    if (out_buf && !mpt_append_char(out_buf, out_cap, &out_len, '\r')) {
                        return false;
                    }
                    break;
                case 't':
                    if (out_buf && !mpt_append_char(out_buf, out_cap, &out_len, '\t')) {
                        return false;
                    }
                    break;
                case 'u': {
                    uint16_t u1 = 0;
                    uint32_t cp = 0;
                    if (!mpt_json_parse_hex4(cur, &u1)) {
                        return false;
                    }
                    if (u1 >= 0xD800u && u1 <= 0xDBFFu) {
                        uint16_t u2 = 0;
                        if (cur->pos + 2 > cur->len ||
                            cur->src[cur->pos] != '\\' ||
                            cur->src[cur->pos + 1] != 'u') {
                            return false;
                        }
                        cur->pos += 2;
                        if (!mpt_json_parse_hex4(cur, &u2) || u2 < 0xDC00u || u2 > 0xDFFFu) {
                            return false;
                        }
                        cp = 0x10000u + (((uint32_t)(u1 - 0xD800u) << 10) | (uint32_t)(u2 - 0xDC00u));
                    } else if (u1 >= 0xDC00u && u1 <= 0xDFFFu) {
                        return false;
                    } else {
                        cp = (uint32_t)u1;
                    }
                    if (out_buf && !mpt_append_utf8_codepoint(out_buf, out_cap, &out_len, cp)) {
                        return false;
                    }
                    break;
                }
                default:
                    return false;
            }
        } else {
            if (out_buf && !mpt_append_char(out_buf, out_cap, &out_len, (char)ch)) {
                return false;
            }
        }
    }
    return false;
}

MPT_CORE_FN bool mpt_json_parse_bool(MptJsonCursor *cur, bool *out_val) {
    mpt_json_skip_ws(cur);
    if (cur->pos + 4 <= cur->len &&
        cur->src[cur->pos] == 't' &&
        cur->src[cur->pos + 1] == 'r' &&
        cur->src[cur->pos + 2] == 'u' &&
        cur->src[cur->pos + 3] == 'e') {
        cur->pos += 4;
        *out_val = true;
        return true;
    }
    if (cur->pos + 5 <= cur->len &&
        cur->src[cur->pos] == 'f' &&
        cur->src[cur->pos + 1] == 'a' &&
        cur->src[cur->pos + 2] == 'l' &&
        cur->src[cur->pos + 3] == 's' &&
        cur->src[cur->pos + 4] == 'e') {
        cur->pos += 5;
        *out_val = false;
        return true;
    }
    return false;
}

MPT_CORE_FN bool mpt_json_parse_int(MptJsonCursor *cur, int *out_val) {
    bool neg = false;
    int64_t val = 0;
    size_t digits = 0;
    mpt_json_skip_ws(cur);
    if (cur->pos < cur->len && cur->src[cur->pos] == '-') {
        neg = true;
        cur->pos++;
    }
    while (cur->pos < cur->len) {
        char c = cur->src[cur->pos];
        if (c >= '0' && c <= '9') {
            val = val * 10 + (c - '0');
            if (val > 1000000) {
                return false;
            }
            digits++;
            cur->pos++;
        } else {
            break;
        }
    }
    if (digits == 0) {
        return false;
    }
    if (cur->pos < cur->len && (cur->src[cur->pos] == '.' || cur->src[cur->pos] == 'e' || cur->src[cur->pos] == 'E')) {
        return false;
    }
    *out_val = neg ? (int)(-val) : (int)val;
    return true;
}

MPT_CORE_FN bool mpt_json_parse_string_array(
    MptJsonCursor *cur,
    char items[][MPT_MAX_STR],
    size_t max_items,
    size_t *out_count) {
    size_t count = 0;
    mpt_json_skip_ws(cur);
    if (cur->pos >= cur->len || cur->src[cur->pos] != '[') {
        return false;
    }
    cur->pos++;
    mpt_json_skip_ws(cur);
    if (cur->pos < cur->len && cur->src[cur->pos] == ']') {
        cur->pos++;
        *out_count = 0;
        return true;
    }

    for (;;) {
        if (count >= max_items) {
            return false;
        }
        if (!mpt_json_parse_string(cur, items[count], MPT_MAX_STR)) {
            return false;
        }
        count++;
        mpt_json_skip_ws(cur);
        if (cur->pos >= cur->len) {
            return false;
        }
        if (cur->src[cur->pos] == ']') {
            cur->pos++;
            *out_count = count;
            return true;
        }
        if (cur->src[cur->pos] != ',') {
            return false;
        }
        cur->pos++;
    }
}

MPT_CORE_FN bool mpt_json_skip_value(MptJsonCursor *cur, int depth) {
    char c;
    if (depth > MPT_MAX_JSON_DEPTH) {
        return false;
    }
    mpt_json_skip_ws(cur);
    if (cur->pos >= cur->len) {
        return false;
    }
    c = cur->src[cur->pos];
    if (c == '"') {
        return mpt_json_parse_string(cur, NULL, 0);
    }
    if (c == 't' || c == 'f') {
        bool dummy = false;
        return mpt_json_parse_bool(cur, &dummy);
    }
    if (c == 'n') {
        if (cur->pos + 4 <= cur->len &&
            cur->src[cur->pos + 1] == 'u' &&
            cur->src[cur->pos + 2] == 'l' &&
            cur->src[cur->pos + 3] == 'l') {
            cur->pos += 4;
            return true;
        }
        return false;
    }
    if (c == '-' || (c >= '0' && c <= '9')) {
        size_t digits = 0;
        if (c == '-') {
            cur->pos++;
        }
        while (cur->pos < cur->len && cur->src[cur->pos] >= '0' && cur->src[cur->pos] <= '9') {
            cur->pos++;
            digits++;
        }
        if (digits == 0) {
            return false;
        }
        if (cur->pos < cur->len && cur->src[cur->pos] == '.') {
            size_t frac_digits = 0;
            cur->pos++;
            while (cur->pos < cur->len && cur->src[cur->pos] >= '0' && cur->src[cur->pos] <= '9') {
                cur->pos++;
                frac_digits++;
            }
            if (frac_digits == 0) {
                return false;
            }
        }
        if (cur->pos < cur->len && (cur->src[cur->pos] == 'e' || cur->src[cur->pos] == 'E')) {
            size_t exp_digits = 0;
            cur->pos++;
            if (cur->pos < cur->len && (cur->src[cur->pos] == '+' || cur->src[cur->pos] == '-')) {
                cur->pos++;
            }
            while (cur->pos < cur->len && cur->src[cur->pos] >= '0' && cur->src[cur->pos] <= '9') {
                cur->pos++;
                exp_digits++;
            }
            if (exp_digits == 0) {
                return false;
            }
        }
        return true;
    }
    if (c == '[') {
        cur->pos++;
        mpt_json_skip_ws(cur);
        if (cur->pos < cur->len && cur->src[cur->pos] == ']') {
            cur->pos++;
            return true;
        }
        for (;;) {
            if (!mpt_json_skip_value(cur, depth + 1)) {
                return false;
            }
            mpt_json_skip_ws(cur);
            if (cur->pos >= cur->len) {
                return false;
            }
            if (cur->src[cur->pos] == ']') {
                cur->pos++;
                return true;
            }
            if (cur->src[cur->pos] != ',') {
                return false;
            }
            cur->pos++;
        }
    }
    if (c == '{') {
        cur->pos++;
        mpt_json_skip_ws(cur);
        if (cur->pos < cur->len && cur->src[cur->pos] == '}') {
            cur->pos++;
            return true;
        }
        for (;;) {
            if (!mpt_json_parse_string(cur, NULL, 0)) {
                return false;
            }
            mpt_json_skip_ws(cur);
            if (cur->pos >= cur->len || cur->src[cur->pos] != ':') {
                return false;
            }
            cur->pos++;
            if (!mpt_json_skip_value(cur, depth + 1)) {
                return false;
            }
            mpt_json_skip_ws(cur);
            if (cur->pos >= cur->len) {
                return false;
            }
            if (cur->src[cur->pos] == '}') {
                cur->pos++;
                return true;
            }
            if (cur->src[cur->pos] != ',') {
                return false;
            }
            cur->pos++;
        }
    }
    return false;
}

MPT_CORE_FN bool mpt_parse_boot_config_utf8(const char *json, size_t json_len, MptBootConfig *out_cfg) {
    MptJsonCursor cur;
    char key[128];

    if (!json || !out_cfg) {
        return false;
    }

    mpt_boot_config_init(out_cfg);
    cur.src = json;
    cur.len = json_len;
    cur.pos = 0;

    if (cur.len >= 3 &&
        (unsigned char)cur.src[0] == 0xEFu &&
        (unsigned char)cur.src[1] == 0xBBu &&
        (unsigned char)cur.src[2] == 0xBFu) {
        cur.pos = 3;
    }

    mpt_json_skip_ws(&cur);
    if (cur.pos >= cur.len || cur.src[cur.pos] != '{') {
        return false;
    }
    cur.pos++;

    mpt_json_skip_ws(&cur);
    if (cur.pos < cur.len && cur.src[cur.pos] == '}') {
        cur.pos++;
        mpt_json_skip_ws(&cur);
        return cur.pos == cur.len;
    }

    for (;;) {
        if (!mpt_json_parse_string(&cur, key, sizeof(key))) {
            return false;
        }
        mpt_json_skip_ws(&cur);
        if (cur.pos >= cur.len || cur.src[cur.pos] != ':') {
            return false;
        }
        cur.pos++;

        if (mpt_streq(key, "title")) {
            if (!mpt_json_parse_string(&cur, out_cfg->title, sizeof(out_cfg->title))) {
                return false;
            }
            out_cfg->has_title = true;
        } else if (mpt_streq(key, "entryDll")) {
            if (!mpt_json_parse_string(&cur, out_cfg->entry_dll, sizeof(out_cfg->entry_dll))) {
                return false;
            }
            out_cfg->has_entry_dll = true;
        } else if (mpt_streq(key, "probeDirs")) {
            if (!mpt_json_parse_string_array(&cur, out_cfg->probe_dirs, MPT_MAX_PROBE_DIRS, &out_cfg->probe_dir_count)) {
                return false;
            }
            out_cfg->has_probe_dirs = true;
        } else if (mpt_streq(key, "defaultArgs")) {
            if (!mpt_json_parse_string_array(&cur, out_cfg->default_args, MPT_MAX_DEFAULT_ARGS, &out_cfg->default_arg_count)) {
                return false;
            }
            out_cfg->has_default_args = true;
        } else if (mpt_streq(key, "minMajor")) {
            int val = 0;
            if (!mpt_json_parse_int(&cur, &val) || val < 1 || val > 999) {
                return false;
            }
            out_cfg->min_major = val;
        } else if (mpt_streq(key, "requireAspNet")) {
            if (!mpt_json_parse_bool(&cur, &out_cfg->require_aspnet)) {
                return false;
            }
        } else if (mpt_streq(key, "waitForChild")) {
            if (!mpt_json_parse_bool(&cur, &out_cfg->wait_for_child)) {
                return false;
            }
        } else {
            if (!mpt_json_skip_value(&cur, 1)) {
                return false;
            }
        }

        mpt_json_skip_ws(&cur);
        if (cur.pos >= cur.len) {
            return false;
        }
        if (cur.src[cur.pos] == '}') {
            cur.pos++;
            break;
        }
        if (cur.src[cur.pos] != ',') {
            return false;
        }
        cur.pos++;
    }

    mpt_json_skip_ws(&cur);
    return cur.pos == cur.len;
}

/* -------------------------------------------------------------------------
 * Token Expansion for defaultArgs ("__EXE_DIR__" and ".")
 * ------------------------------------------------------------------------- */

MPT_CORE_FN bool mpt_expand_default_arg_utf8(
    const char *raw_arg,
    const char *exe_dir,
    char *out_buf,
    size_t out_cap) {
    const char *token = "__EXE_DIR__";
    const size_t token_len = 11;
    size_t out_len = 0;
    size_t i = 0;

    if (!raw_arg || !exe_dir || !out_buf || out_cap == 0) {
        return false;
    }
    out_buf[0] = '\0';

    if (mpt_streq(raw_arg, ".")) {
        return mpt_copy_str(out_buf, out_cap, exe_dir);
    }

    while (raw_arg[i] != '\0') {
        if (mpt_starts_with(raw_arg + i, token)) {
            if (!mpt_append_str(out_buf, out_cap, &out_len, exe_dir)) {
                return false;
            }
            i += token_len;
        } else {
            if (!mpt_append_char(out_buf, out_cap, &out_len, raw_arg[i])) {
                return false;
            }
            i++;
        }
    }
    return true;
}

/* -------------------------------------------------------------------------
 * .NET Semantic Version Parsing & Comparison
 * ------------------------------------------------------------------------- */

MPT_CORE_FN bool mpt_parse_uint_part(const char *s, size_t *io_pos, int *out_num) {
    uint32_t val = 0;
    size_t digits = 0;
    while (s[*io_pos] >= '0' && s[*io_pos] <= '9') {
        val = val * 10u + (uint32_t)(s[*io_pos] - '0');
        if (val > 999999u) {
            return false;
        }
        (*io_pos)++;
        digits++;
    }
    if (digits == 0) {
        return false;
    }
    *out_num = (int)val;
    return true;
}

MPT_CORE_FN bool mpt_is_safe_version_char(char c) {
    return (c >= '0' && c <= '9') ||
           (c >= 'a' && c <= 'z') ||
           (c >= 'A' && c <= 'Z') ||
           c == '.' || c == '-';
}

MPT_CORE_FN bool mpt_parse_dotnet_version(const char *ver_str, MptDotnetVersion *out_ver) {
    size_t pos = 0;
    if (!ver_str || !out_ver) {
        return false;
    }
    out_ver->major = 0;
    out_ver->minor = 0;
    out_ver->patch = 0;
    out_ver->is_prerelease = false;
    out_ver->prerelease[0] = '\0';

    if (!mpt_parse_uint_part(ver_str, &pos, &out_ver->major) || ver_str[pos] != '.') {
        return false;
    }
    pos++;
    if (!mpt_parse_uint_part(ver_str, &pos, &out_ver->minor) || ver_str[pos] != '.') {
        return false;
    }
    pos++;
    if (!mpt_parse_uint_part(ver_str, &pos, &out_ver->patch)) {
        return false;
    }

    if (ver_str[pos] == '\0') {
        return true;
    }
    if (ver_str[pos] == '-') {
        size_t pre_len = 0;
        pos++;
        if (ver_str[pos] == '\0' || ver_str[pos] == '+') {
            return false;
        }
        while (ver_str[pos] != '\0' && ver_str[pos] != '+') {
            if (!mpt_is_safe_version_char(ver_str[pos])) {
                return false;
            }
            if (!mpt_append_char(out_ver->prerelease, sizeof(out_ver->prerelease), &pre_len, ver_str[pos])) {
                return false;
            }
            pos++;
        }
        out_ver->is_prerelease = true;
    }
    if (ver_str[pos] == '+') {
        pos++;
        if (ver_str[pos] == '\0') {
            return false;
        }
        while (ver_str[pos] != '\0') {
            if (!mpt_is_safe_version_char(ver_str[pos])) {
                return false;
            }
            pos++;
        }
    }
    return ver_str[pos] == '\0';
}

MPT_CORE_FN int mpt_compare_dotnet_version(const MptDotnetVersion *a, const MptDotnetVersion *b) {
    size_t i = 0;
    if (a->major != b->major) {
        return (a->major > b->major) ? 1 : -1;
    }
    if (a->minor != b->minor) {
        return (a->minor > b->minor) ? 1 : -1;
    }
    if (a->patch != b->patch) {
        return (a->patch > b->patch) ? 1 : -1;
    }
    if (a->is_prerelease != b->is_prerelease) {
        return a->is_prerelease ? -1 : 1;
    }
    if (!a->is_prerelease) {
        return 0;
    }
    while (a->prerelease[i] != '\0' && b->prerelease[i] != '\0') {
        if (a->prerelease[i] != b->prerelease[i]) {
            return ((unsigned char)a->prerelease[i] > (unsigned char)b->prerelease[i]) ? 1 : -1;
        }
        i++;
    }
    if (a->prerelease[i] == b->prerelease[i]) {
        return 0;
    }
    return (a->prerelease[i] != '\0') ? 1 : -1;
}

MPT_CORE_FN bool mpt_version_meets_min_major(const MptDotnetVersion *ver, int min_major) {
    return ver && ver->major >= min_major;
}

/* -------------------------------------------------------------------------
 * Microsoft Official releases.json Metadata Parser
 * ------------------------------------------------------------------------- */

MPT_CORE_FN bool mpt_is_safe_url_version_token(const char *s) {
    size_t i = 0;
    MptDotnetVersion parsed;
    if (!s || s[0] == '\0' || s[0] == '.' || s[0] == '-') {
        return false;
    }
    while (s[i] != '\0') {
        if (!mpt_is_safe_version_char(s[i])) {
            return false;
        }
        if (s[i] == '.' && s[i + 1] == '.') {
            return false;
        }
        i++;
    }
    return mpt_parse_dotnet_version(s, &parsed);
}

MPT_CORE_FN bool mpt_find_json_key_string_value(
    const char *json,
    size_t len,
    size_t start_pos,
    const char *target_key,
    char *out_val,
    size_t out_cap,
    size_t *out_after_pos) {
    MptJsonCursor cur;
    char token[128];

    cur.src = json;
    cur.len = len;
    cur.pos = start_pos;

    while (cur.pos < cur.len) {
        if (cur.src[cur.pos] == '"') {
            if (!mpt_json_parse_string(&cur, token, sizeof(token))) {
                while (cur.pos < cur.len && cur.src[cur.pos] != '"') {
                    if (cur.src[cur.pos] == '\\' && cur.pos + 1 < cur.len) {
                        cur.pos += 2;
                    } else {
                        cur.pos++;
                    }
                }
                if (cur.pos < cur.len) {
                    cur.pos++;
                }
                continue;
            }
            mpt_json_skip_ws(&cur);
            if (cur.pos < cur.len && cur.src[cur.pos] == ':' && mpt_streq(token, target_key)) {
                cur.pos++;
                mpt_json_skip_ws(&cur);
                if (cur.pos < cur.len && cur.src[cur.pos] == '"') {
                    if (mpt_json_parse_string(&cur, out_val, out_cap)) {
                        if (out_after_pos) {
                            *out_after_pos = cur.pos;
                        }
                        return true;
                    }
                } else if (cur.pos < cur.len && cur.src[cur.pos] == '{') {
                    if (out_after_pos) {
                        *out_after_pos = cur.pos;
                    }
                    if (out_val && out_cap > 0) {
                        out_val[0] = '\0';
                    }
                    return true;
                }
            }
        } else {
            cur.pos++;
        }
    }
    return false;
}

MPT_CORE_FN bool mpt_parse_latest_runtime_version(
    const char *json,
    size_t len,
    char *out_runtime_ver,
    size_t runtime_cap,
    char *out_aspnet_ver,
    size_t aspnet_cap) {
    size_t after_pos = 0;
    char candidate[64];

    if (!json || !out_runtime_ver || runtime_cap == 0) {
        return false;
    }
    out_runtime_ver[0] = '\0';
    if (out_aspnet_ver && aspnet_cap > 0) {
        out_aspnet_ver[0] = '\0';
    }

    if (!mpt_find_json_key_string_value(json, len, 0, "latest-runtime", candidate, sizeof(candidate), &after_pos)) {
        return false;
    }
    if (!mpt_is_safe_url_version_token(candidate)) {
        return false;
    }
    if (!mpt_copy_str(out_runtime_ver, runtime_cap, candidate)) {
        return false;
    }

    if (out_aspnet_ver && aspnet_cap > 0) {
        size_t asp_obj_pos = 0;
        char asp_candidate[64];
        asp_candidate[0] = '\0';
        if (mpt_find_json_key_string_value(json, len, 0, "aspnetcore-runtime", asp_candidate, sizeof(asp_candidate), &asp_obj_pos)) {
            if (asp_candidate[0] != '\0' && mpt_is_safe_url_version_token(asp_candidate)) {
                return mpt_copy_str(out_aspnet_ver, aspnet_cap, asp_candidate);
            }
            if (mpt_find_json_key_string_value(json, len, asp_obj_pos, "version", asp_candidate, sizeof(asp_candidate), NULL) &&
                mpt_is_safe_url_version_token(asp_candidate)) {
                return mpt_copy_str(out_aspnet_ver, aspnet_cap, asp_candidate);
            }
        }
        if (!mpt_copy_str(out_aspnet_ver, aspnet_cap, out_runtime_ver)) {
            return false;
        }
    }
    return true;
}

/* -------------------------------------------------------------------------
 * Win32 Command-Line Escaping (Inverse of CommandLineToArgvW)
 * ------------------------------------------------------------------------- */

MPT_CORE_FN bool mpt_arg_needs_quotes_u16(const uint16_t *arg) {
    size_t i = 0;
    if (!arg || arg[0] == 0) {
        return true;
    }
    while (arg[i] != 0) {
        uint16_t c = arg[i];
        if (c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '"') {
            return true;
        }
        i++;
    }
    return false;
}

MPT_CORE_FN bool mpt_append_win32_quoted_arg_u16(
    uint16_t *dst,
    size_t cap,
    size_t *io_len,
    const uint16_t *arg) {
    static const uint16_t empty_u16[1] = {0};
    size_t backslashes = 0;
    size_t i = 0;

    if (!dst || !io_len) {
        return false;
    }
    if (!arg) {
        arg = empty_u16;
    }

    if (!mpt_arg_needs_quotes_u16(arg)) {
        return mpt_append_u16_str(dst, cap, io_len, arg);
    }

    if (!mpt_append_u16_char(dst, cap, io_len, (uint16_t)'"')) {
        return false;
    }

    while (arg[i] != 0) {
        uint16_t c = arg[i++];
        if (c == (uint16_t)'\\') {
            backslashes++;
        } else if (c == (uint16_t)'"') {
            size_t k = 0;
            for (k = 0; k < backslashes * 2 + 1; k++) {
                if (!mpt_append_u16_char(dst, cap, io_len, (uint16_t)'\\')) {
                    return false;
                }
            }
            if (!mpt_append_u16_char(dst, cap, io_len, (uint16_t)'"')) {
                return false;
            }
            backslashes = 0;
        } else {
            size_t k = 0;
            for (k = 0; k < backslashes; k++) {
                if (!mpt_append_u16_char(dst, cap, io_len, (uint16_t)'\\')) {
                    return false;
                }
            }
            backslashes = 0;
            if (!mpt_append_u16_char(dst, cap, io_len, c)) {
                return false;
            }
        }
    }

    while (backslashes > 0) {
        if (!mpt_append_u16_char(dst, cap, io_len, (uint16_t)'\\') ||
            !mpt_append_u16_char(dst, cap, io_len, (uint16_t)'\\')) {
            return false;
        }
        backslashes--;
    }

    return mpt_append_u16_char(dst, cap, io_len, (uint16_t)'"');
}

MPT_CORE_FN bool mpt_cmd_append_arg_u16(
    uint16_t *dst,
    size_t cap,
    size_t *io_len,
    const uint16_t *arg) {
    if (*io_len > 0) {
        if (!mpt_append_u16_char(dst, cap, io_len, (uint16_t)' ')) {
            return false;
        }
    }
    return mpt_append_win32_quoted_arg_u16(dst, cap, io_len, arg);
}

MPT_CORE_FN bool mpt_arg_needs_quotes_utf8(const char *arg) {
    size_t i = 0;
    if (!arg || arg[0] == '\0') {
        return true;
    }
    while (arg[i] != '\0') {
        char c = arg[i];
        if (c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '"') {
            return true;
        }
        i++;
    }
    return false;
}

MPT_CORE_FN bool mpt_append_win32_quoted_arg_utf8(
    char *dst,
    size_t cap,
    size_t *io_len,
    const char *arg) {
    size_t backslashes = 0;
    size_t i = 0;

    if (!dst || !io_len) {
        return false;
    }
    if (!arg) {
        arg = "";
    }

    if (!mpt_arg_needs_quotes_utf8(arg)) {
        return mpt_append_str(dst, cap, io_len, arg);
    }

    if (!mpt_append_char(dst, cap, io_len, '"')) {
        return false;
    }

    while (arg[i] != '\0') {
        char c = arg[i++];
        if (c == '\\') {
            backslashes++;
        } else if (c == '"') {
            size_t k = 0;
            for (k = 0; k < backslashes * 2 + 1; k++) {
                if (!mpt_append_char(dst, cap, io_len, '\\')) {
                    return false;
                }
            }
            if (!mpt_append_char(dst, cap, io_len, '"')) {
                return false;
            }
            backslashes = 0;
        } else {
            size_t k = 0;
            for (k = 0; k < backslashes; k++) {
                if (!mpt_append_char(dst, cap, io_len, '\\')) {
                    return false;
                }
            }
            backslashes = 0;
            if (!mpt_append_char(dst, cap, io_len, c)) {
                return false;
            }
        }
    }

    while (backslashes > 0) {
        if (!mpt_append_char(dst, cap, io_len, '\\') ||
            !mpt_append_char(dst, cap, io_len, '\\')) {
            return false;
        }
        backslashes--;
    }

    return mpt_append_char(dst, cap, io_len, '"');
}

MPT_CORE_FN bool mpt_cmd_append_arg_utf8(char *dst, size_t cap, size_t *io_len, const char *arg) {
    if (*io_len > 0) {
        if (!mpt_append_char(dst, cap, io_len, ' ')) {
            return false;
        }
    }
    return mpt_append_win32_quoted_arg_utf8(dst, cap, io_len, arg);
}

/* -------------------------------------------------------------------------
 * JSON Diagnostic Output Escaping
 * ------------------------------------------------------------------------- */

MPT_CORE_FN bool mpt_append_json_escaped_utf8(
    char *dst,
    size_t cap,
    size_t *io_len,
    const char *raw) {
    static const char hex[] = "0123456789abcdef";
    size_t i = 0;
    if (!raw) {
        return true;
    }
    while (raw[i] != '\0') {
        unsigned char c = (unsigned char)raw[i++];
        switch (c) {
            case '"':
                if (!mpt_append_str(dst, cap, io_len, "\\\"")) {
                    return false;
                }
                break;
            case '\\':
                if (!mpt_append_str(dst, cap, io_len, "\\\\")) {
                    return false;
                }
                break;
            case '\b':
                if (!mpt_append_str(dst, cap, io_len, "\\b")) {
                    return false;
                }
                break;
            case '\f':
                if (!mpt_append_str(dst, cap, io_len, "\\f")) {
                    return false;
                }
                break;
            case '\n':
                if (!mpt_append_str(dst, cap, io_len, "\\n")) {
                    return false;
                }
                break;
            case '\r':
                if (!mpt_append_str(dst, cap, io_len, "\\r")) {
                    return false;
                }
                break;
            case '\t':
                if (!mpt_append_str(dst, cap, io_len, "\\t")) {
                    return false;
                }
                break;
            default:
                if (c < 0x20u) {
                    char esc[7];
                    esc[0] = '\\';
                    esc[1] = 'u';
                    esc[2] = '0';
                    esc[3] = '0';
                    esc[4] = hex[(c >> 4) & 0xFu];
                    esc[5] = hex[c & 0xFu];
                    esc[6] = '\0';
                    if (!mpt_append_str(dst, cap, io_len, esc)) {
                        return false;
                    }
                } else {
                    if (!mpt_append_char(dst, cap, io_len, (char)c)) {
                        return false;
                    }
                }
                break;
        }
    }
    return true;
}

/* -------------------------------------------------------------------------
 * Bootstrapper Internal CLI Flag Parser & Stripper (--mpt-*)
 * ------------------------------------------------------------------------- */

MPT_CORE_FN bool mpt_parse_nonneg_int_str(const char *s, int *out_val) {
    uint32_t v = 0;
    size_t i = 0;
    if (!s || s[0] == '\0') {
        return false;
    }
    while (s[i] != '\0') {
        if (s[i] < '0' || s[i] > '9') {
            return false;
        }
        v = v * 10u + (uint32_t)(s[i] - '0');
        if (v > 3600000u) {
            return false;
        }
        i++;
    }
    *out_val = (int)v;
    return true;
}

MPT_CORE_FN void mpt_cli_options_init(MptBootstrapCliOptions *opts) {
    if (!opts) {
        return;
    }
    opts->force_wait = false;
    opts->force_no_dotnet = false;
    opts->auto_install = false;
    opts->has_probe_json = false;
    opts->probe_json_path[0] = '\0';
    opts->has_ui_dump = false;
    opts->ui_dump_path[0] = '\0';
    opts->has_autoclose_ms = false;
    opts->autoclose_ms = 0;
    opts->forwarded_count = 0;
}

MPT_CORE_FN bool mpt_parse_cli_args_utf8(
    int argc,
    const char *const *argv,
    MptBootstrapCliOptions *out_opts) {
    int i = 1;
    bool passthrough_only = false;

    if (!out_opts) {
        return false;
    }
    mpt_cli_options_init(out_opts);

    while (i < argc) {
        const char *arg = argv[i];
        if (!arg) {
            i++;
            continue;
        }

        if (!passthrough_only && mpt_streq(arg, "--")) {
            passthrough_only = true;
            if (out_opts->forwarded_count < MPT_MAX_FORWARDED_ARGS) {
                out_opts->forwarded_indices[out_opts->forwarded_count++] = i;
            }
            i++;
            continue;
        }

        if (!passthrough_only && mpt_starts_with(arg, "--mpt-")) {
            if (mpt_streq(arg, "--mpt-wait")) {
                out_opts->force_wait = true;
                i++;
                continue;
            }
            if (mpt_streq(arg, "--mpt-test-no-dotnet")) {
                out_opts->force_no_dotnet = true;
                i++;
                continue;
            }
            if (mpt_streq(arg, "--mpt-auto-install")) {
                out_opts->auto_install = true;
                i++;
                continue;
            }
            if (mpt_streq(arg, "--mpt-probe-json")) {
                if (i + 1 >= argc || !mpt_copy_str(out_opts->probe_json_path, sizeof(out_opts->probe_json_path), argv[i + 1])) {
                    return false;
                }
                out_opts->has_probe_json = true;
                i += 2;
                continue;
            }
            if (mpt_starts_with(arg, "--mpt-probe-json=")) {
                if (!mpt_copy_str(out_opts->probe_json_path, sizeof(out_opts->probe_json_path), arg + 17)) {
                    return false;
                }
                out_opts->has_probe_json = true;
                i++;
                continue;
            }
            if (mpt_streq(arg, "--mpt-test-ui-dump")) {
                if (i + 1 >= argc || !mpt_copy_str(out_opts->ui_dump_path, sizeof(out_opts->ui_dump_path), argv[i + 1])) {
                    return false;
                }
                out_opts->has_ui_dump = true;
                i += 2;
                continue;
            }
            if (mpt_starts_with(arg, "--mpt-test-ui-dump=")) {
                if (!mpt_copy_str(out_opts->ui_dump_path, sizeof(out_opts->ui_dump_path), arg + 19)) {
                    return false;
                }
                out_opts->has_ui_dump = true;
                i++;
                continue;
            }
            if (mpt_streq(arg, "--mpt-test-autoclose-ms")) {
                if (i + 1 >= argc || !mpt_parse_nonneg_int_str(argv[i + 1], &out_opts->autoclose_ms)) {
                    return false;
                }
                out_opts->has_autoclose_ms = true;
                i += 2;
                continue;
            }
            if (mpt_starts_with(arg, "--mpt-test-autoclose-ms=")) {
                if (!mpt_parse_nonneg_int_str(arg + 24, &out_opts->autoclose_ms)) {
                    return false;
                }
                out_opts->has_autoclose_ms = true;
                i++;
                continue;
            }
            i++;
            continue;
        }

        if (out_opts->forwarded_count >= MPT_MAX_FORWARDED_ARGS) {
            return false;
        }
        out_opts->forwarded_indices[out_opts->forwarded_count++] = i;
        i++;
    }
    return true;
}

#ifdef __cplusplus
}
#endif

#endif /* MPT_BOOTSTRAP_CORE_H */
