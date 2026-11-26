#include "../mpt_bootstrap_core.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int g_failures = 0;
static int g_checks = 0;

#define CHECK(cond, msg)                                                           \
    do {                                                                           \
        g_checks++;                                                                \
        if (!(cond)) {                                                             \
            fprintf(stderr, "FAIL [%s:%d]: %s\n", __FILE__, __LINE__, (msg));      \
            g_failures++;                                                          \
        }                                                                          \
    } while (0)

static void test_boot_config_json_parser(void) {
    MptBootConfig cfg;
    const char *full_json =
        "\xEF\xBB\xBF{\n"
        "  \"title\": \"文件互传\",\n"
        "  \"entryDll\": \"app\\\\MptStandaloneHost.dll\",\n"
        "  \"probeDirs\": [\n"
        "    \"app\",\n"
        "    \".\",\n"
        "    \"..\\\\shared-ui\",\n"
        "    \"%LOCALAPPDATA%\\\\MyPowerTools\\\\shared-ui\",\n"
        "    \"%LOCALAPPDATA%\\\\Programs\\\\MyPowerTools\\\\Shell\"\n"
        "  ],\n"
        "  \"defaultArgs\": [\"--tool-root\", \"__EXE_DIR__\"],\n"
        "  \"minMajor\": 8,\n"
        "  \"requireAspNet\": true,\n"
        "  \"waitForChild\": true,\n"
        "  \"extraFutureObject\": {\"nested\": [1, -2.5e3, true, false, null, \"ok\"]}\n"
        "}\n";

    CHECK(mpt_parse_boot_config_utf8(full_json, strlen(full_json), &cfg), "parse full .mptboot.json with BOM");
    CHECK(cfg.has_title && strcmp(cfg.title, "文件互传") == 0, "UTF-8 Chinese title preserved");
    CHECK(cfg.has_entry_dll && strcmp(cfg.entry_dll, "app\\MptStandaloneHost.dll") == 0, "entryDll backslash unescaped");
    CHECK(cfg.has_probe_dirs && cfg.probe_dir_count == 5, "probeDirs count == 5");
    CHECK(strcmp(cfg.probe_dirs[2], "..\\shared-ui") == 0, "probeDirs[2] == ..\\shared-ui");
    CHECK(strcmp(cfg.probe_dirs[3], "%LOCALAPPDATA%\\MyPowerTools\\shared-ui") == 0, "probeDirs[3] preserved");
    CHECK(cfg.has_default_args && cfg.default_arg_count == 2, "defaultArgs count == 2");
    CHECK(strcmp(cfg.default_args[0], "--tool-root") == 0, "defaultArgs[0]");
    CHECK(strcmp(cfg.default_args[1], "__EXE_DIR__") == 0, "defaultArgs[1]");
    CHECK(cfg.min_major == 8, "minMajor == 8");
    CHECK(cfg.require_aspnet == true, "requireAspNet == true");
    CHECK(cfg.wait_for_child == true, "waitForChild == true");

    {
        const char *esc_json = "{\"title\": \"\\u6587\\u4ef6\\u4e92\\u4f20 \\uD83D\\uDE80\", \"minMajor\": 10}";
        CHECK(mpt_parse_boot_config_utf8(esc_json, strlen(esc_json), &cfg), "parse unicode escapes");
        CHECK(strcmp(cfg.title, "文件互传 \xF0\x9F\x9A\x80") == 0, "decoded \\uXXXX and surrogate pair");
        CHECK(cfg.min_major == 10, "minMajor == 10");
        CHECK(cfg.require_aspnet == false && cfg.wait_for_child == false, "defaults preserved");
    }

    {
        const char *empty_json = "  { } ";
        CHECK(mpt_parse_boot_config_utf8(empty_json, strlen(empty_json), &cfg), "parse empty JSON object");
        CHECK(!cfg.has_title && !cfg.has_entry_dll, "has_title and has_entry_dll false");
        CHECK(cfg.min_major == 8, "default minMajor == 8");
        CHECK(!cfg.require_aspnet && !cfg.wait_for_child, "default booleans false");
    }

    {
        const char *bad1 = "{\"title\": \"unterminated}";
        const char *bad2 = "{\"minMajor\": \"8\"}";
        const char *bad3 = "{\"requireAspNet\": 1}";
        const char *bad4 = "{\"minMajor\": 0}";
        const char *bad5 = "{\"title\": \"ok\"} trailing";
        CHECK(!mpt_parse_boot_config_utf8(bad1, strlen(bad1), &cfg), "reject unterminated string");
        CHECK(!mpt_parse_boot_config_utf8(bad2, strlen(bad2), &cfg), "reject string minMajor");
        CHECK(!mpt_parse_boot_config_utf8(bad3, strlen(bad3), &cfg), "reject numeric requireAspNet");
        CHECK(!mpt_parse_boot_config_utf8(bad4, strlen(bad4), &cfg), "reject minMajor < 1");
        CHECK(!mpt_parse_boot_config_utf8(bad5, strlen(bad5), &cfg), "reject trailing tokens");
    }
}

static void test_default_arg_token_expansion(void) {
    char buf[256];
    CHECK(mpt_expand_default_arg_utf8("__EXE_DIR__", "C:\\Tools\\Demo", buf, sizeof(buf)), "expand __EXE_DIR__");
    CHECK(strcmp(buf, "C:\\Tools\\Demo") == 0, "exact __EXE_DIR__ expanded");

    CHECK(mpt_expand_default_arg_utf8(".", "C:\\Tools\\Demo", buf, sizeof(buf)), "expand .");
    CHECK(strcmp(buf, "C:\\Tools\\Demo") == 0, "exact . expanded to ExeDir");

    CHECK(mpt_expand_default_arg_utf8("--config=__EXE_DIR__\\app.json", "D:\\My App", buf, sizeof(buf)), "expand embedded token");
    CHECK(strcmp(buf, "--config=D:\\My App\\app.json") == 0, "embedded __EXE_DIR__ expanded");
}

static void test_dotnet_version_parsing_and_comparison(void) {
    MptDotnetVersion v7, v8_0, v8_15, v8_rc, v9, v10, v11_prev;

    CHECK(mpt_parse_dotnet_version("7.0.20", &v7), "parse 7.0.20");
    CHECK(mpt_parse_dotnet_version("8.0.0", &v8_0), "parse 8.0.0");
    CHECK(mpt_parse_dotnet_version("8.0.15", &v8_15), "parse 8.0.15");
    CHECK(mpt_parse_dotnet_version("8.0.15-rc.2.24473.5", &v8_rc), "parse 8.0.15-rc.2");
    CHECK(mpt_parse_dotnet_version("9.0.4", &v9), "parse 9.0.4");
    CHECK(mpt_parse_dotnet_version("10.0.1", &v10), "parse 10.0.1");
    CHECK(mpt_parse_dotnet_version("11.0.0-preview.1.25080.2", &v11_prev), "parse 11.0.0-preview.1");

    CHECK(!mpt_version_meets_min_major(&v7, 8), "7.0.20 < 8");
    CHECK(mpt_version_meets_min_major(&v8_0, 8), "8.0.0 >= 8");
    CHECK(mpt_version_meets_min_major(&v8_15, 8), "8.0.15 >= 8");
    CHECK(mpt_version_meets_min_major(&v9, 8), "9.0.4 >= 8");
    CHECK(mpt_version_meets_min_major(&v10, 8), "10.0.1 >= 8");
    CHECK(mpt_version_meets_min_major(&v11_prev, 8), "11.0.0-preview.1 >= 8");

    CHECK(mpt_compare_dotnet_version(&v10, &v9) > 0, "10.0.1 > 9.0.4");
    CHECK(mpt_compare_dotnet_version(&v9, &v8_15) > 0, "9.0.4 > 8.0.15");
    CHECK(mpt_compare_dotnet_version(&v8_15, &v8_0) > 0, "8.0.15 > 8.0.0");
    CHECK(mpt_compare_dotnet_version(&v8_15, &v8_rc) > 0, "stable 8.0.15 > prerelease 8.0.15-rc.2");
    CHECK(mpt_compare_dotnet_version(&v8_rc, &v8_0) > 0, "8.0.15-rc.2 > 8.0.0");
    CHECK(mpt_compare_dotnet_version(&v11_prev, &v10) > 0, "11.0.0-preview.1 > 10.0.1");

    {
        MptDotnetVersion tmp;
        CHECK(!mpt_parse_dotnet_version("", &tmp), "reject empty");
        CHECK(!mpt_parse_dotnet_version(".", &tmp), "reject .");
        CHECK(!mpt_parse_dotnet_version("..", &tmp), "reject ..");
        CHECK(!mpt_parse_dotnet_version("8.0", &tmp), "reject 2-part version");
        CHECK(!mpt_parse_dotnet_version("NuGetFallbackFolder", &tmp), "reject non-version folder");
        CHECK(!mpt_parse_dotnet_version("8.0.15-", &tmp), "reject empty prerelease");
        CHECK(!mpt_parse_dotnet_version("8.0.15/../../evil", &tmp), "reject path traversal");
    }
}

static void test_release_metadata_parser(void) {
    const char *sample_metadata =
        "{\n"
        "  \"channel-version\": \"8.0\",\n"
        "  \"latest-release\": \"8.0.15\",\n"
        "  \"latest-release-date\": \"2025-04-08\",\n"
        "  \"security\": true,\n"
        "  \"latest-runtime\": \"8.0.15\",\n"
        "  \"latest-sdk\": \"8.0.408\",\n"
        "  \"releases\": [\n"
        "    {\n"
        "      \"release-version\": \"8.0.15\",\n"
        "      \"aspnetcore-runtime\": {\n"
        "        \"version\": \"8.0.15\",\n"
        "        \"version-display\": \"8.0.15\"\n"
        "      }\n"
        "    }\n"
        "  ]\n"
        "}\n";
    char rt_ver[64];
    char asp_ver[64];

    CHECK(
        mpt_parse_latest_runtime_version(
            sample_metadata, strlen(sample_metadata), rt_ver, sizeof(rt_ver), asp_ver, sizeof(asp_ver)),
        "parse official releases.json structure");
    CHECK(strcmp(rt_ver, "8.0.15") == 0, "latest-runtime == 8.0.15");
    CHECK(strcmp(asp_ver, "8.0.15") == 0, "aspnetcore-runtime version == 8.0.15");

    {
        const char *minimal_meta = "{ \"latest-runtime\": \"8.0.16\" }";
        CHECK(
            mpt_parse_latest_runtime_version(
                minimal_meta, strlen(minimal_meta), rt_ver, sizeof(rt_ver), asp_ver, sizeof(asp_ver)),
            "parse minimal releases.json");
        CHECK(strcmp(rt_ver, "8.0.16") == 0 && strcmp(asp_ver, "8.0.16") == 0, "aspnet falls back to latest-runtime");
    }

    {
        const char *bad_meta = "{ \"latest-runtime\": \"8.0.15/../../calc\" }";
        CHECK(
            !mpt_parse_latest_runtime_version(
                bad_meta, strlen(bad_meta), rt_ver, sizeof(rt_ver), asp_ver, sizeof(asp_ver)),
            "reject unsafe latest-runtime characters");
    }
}

static void test_win32_command_line_quoting(void) {
    char cmd[512];
    size_t len = 0;
    cmd[0] = '\0';

    CHECK(mpt_cmd_append_arg_utf8(cmd, sizeof(cmd), &len, "C:\\Program Files\\dotnet\\dotnet.exe"), "quote path with space");
    CHECK(mpt_cmd_append_arg_utf8(cmd, sizeof(cmd), &len, "exec"), "plain token");
    CHECK(mpt_cmd_append_arg_utf8(cmd, sizeof(cmd), &len, "C:\\My Tools\\app\\MptStandaloneHost.dll"), "quote dll path");
    CHECK(mpt_cmd_append_arg_utf8(cmd, sizeof(cmd), &len, "--tool-root"), "plain flag");
    CHECK(mpt_cmd_append_arg_utf8(cmd, sizeof(cmd), &len, "C:\\My Tools\\TrailingSlash\\"), "trailing backslash before quote");
    CHECK(mpt_cmd_append_arg_utf8(cmd, sizeof(cmd), &len, ""), "empty argument quoted");
    CHECK(mpt_cmd_append_arg_utf8(cmd, sizeof(cmd), &len, "a\\\"b"), "backslash + quote escaped");

    CHECK(
        strcmp(
            cmd,
            "\"C:\\Program Files\\dotnet\\dotnet.exe\" exec \"C:\\My Tools\\app\\MptStandaloneHost.dll\" "
            "--tool-root \"C:\\My Tools\\TrailingSlash\\\\\" \"\" \"a\\\\\\\"b\"") == 0,
        "exact CommandLineToArgvW-compatible command line");

    {
        uint16_t u16_cmd[512];
        size_t u16_len = 0;
        const uint16_t arg1[] = {'C', ':', '\\', 'M', 'y', ' ', 'D', 'i', 'r', '\\', 0};
        const uint16_t arg2[] = {'x', '"', 'y', 0};
        char narrowed[128];
        size_t k = 0;

        u16_cmd[0] = 0;
        CHECK(mpt_cmd_append_arg_u16(u16_cmd, 512, &u16_len, arg1), "u16 arg1");
        CHECK(mpt_cmd_append_arg_u16(u16_cmd, 512, &u16_len, arg2), "u16 arg2");
        for (k = 0; k <= u16_len; k++) {
            narrowed[k] = (char)u16_cmd[k];
        }
        CHECK(strcmp(narrowed, "\"C:\\My Dir\\\\\" \"x\\\"y\"") == 0, "u16 quoting matches expected");
    }
}

static void test_cli_flag_stripping_and_json_escape(void) {
    const char *argv[] = {
        "mpt-bootstrap.exe",
        "--mpt-test-no-dotnet",
        "--mpt-wait",
        "--mpt-probe-json",
        "C:\\Temp\\probe.json",
        "--mpt-test-ui-dump=C:\\Temp\\ui.json",
        "--mpt-test-autoclose-ms",
        "250",
        "--user-flag",
        "hello world",
        "--",
        "--mpt-literal-child-arg"
    };
    MptBootstrapCliOptions opts;
    char escaped[256];
    size_t esc_len = 0;

    CHECK(mpt_parse_cli_args_utf8((int)(sizeof(argv) / sizeof(argv[0])), argv, &opts), "parse CLI options");
    CHECK(opts.force_no_dotnet && opts.force_wait, "boolean --mpt-* flags parsed");
    CHECK(opts.has_probe_json && strcmp(opts.probe_json_path, "C:\\Temp\\probe.json") == 0, "probe-json parsed");
    CHECK(opts.has_ui_dump && strcmp(opts.ui_dump_path, "C:\\Temp\\ui.json") == 0, "ui-dump= parsed");
    CHECK(opts.has_autoclose_ms && opts.autoclose_ms == 250, "autoclose-ms parsed");
    CHECK(opts.forwarded_count == 4, "only non-bootstrapper args forwarded");
    CHECK(strcmp(argv[opts.forwarded_indices[0]], "--user-flag") == 0, "forwarded[0]");
    CHECK(strcmp(argv[opts.forwarded_indices[1]], "hello world") == 0, "forwarded[1]");
    CHECK(strcmp(argv[opts.forwarded_indices[2]], "--") == 0, "forwarded[2] == --");
    CHECK(strcmp(argv[opts.forwarded_indices[3]], "--mpt-literal-child-arg") == 0, "arg after -- preserved");

    escaped[0] = '\0';
    CHECK(mpt_append_json_escaped_utf8(escaped, sizeof(escaped), &esc_len, "C:\\Program Files\\\"文件互传\"\n"), "json escape");
    CHECK(strcmp(escaped, "C:\\\\Program Files\\\\\\\"文件互传\\\"\\n") == 0, "json escape output valid");
}

int main(void) {
    test_boot_config_json_parser();
    test_default_arg_token_expansion();
    test_dotnet_version_parsing_and_comparison();
    test_release_metadata_parser();
    test_win32_command_line_quoting();
    test_cli_flag_stripping_and_json_escape();

    if (g_failures != 0) {
        fprintf(stderr, "FAILED: %d/%d checks failed.\n", g_failures, g_checks);
        return 1;
    }
    printf("PASS: all %d unit checks succeeded.\n", g_checks);
    return 0;
}
