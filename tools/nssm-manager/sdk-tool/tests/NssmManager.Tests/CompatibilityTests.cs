using System.Text.Json;
using System.Runtime.Versioning;
using NssmManager.Compatibility;
using NssmManager.Contracts;
using NssmManager.Supervisor;
using NssmManager.Windows;

namespace NssmManager.Tests;

public sealed class CompatibilityTests
{
    [Fact]
    public void Settings_table_covers_upstream_descriptor_names()
    {
        var expected = new[]
        {
            "Application", "AppParameters", "AppDirectory", "AppExit", "AppEvents", "AppAffinity",
            "AppEnvironment", "AppEnvironmentExtra", "AppNoConsole", "AppPriority", "AppRestartDelay",
            "AppStdin", "AppStdinShareMode", "AppStdinCreationDisposition", "AppStdinFlagsAndAttributes",
            "AppStdout", "AppStdoutShareMode", "AppStdoutCreationDisposition", "AppStdoutFlagsAndAttributes",
            "AppStdoutCopyAndTruncate", "AppStderr", "AppStderrShareMode", "AppStderrCreationDisposition",
            "AppStderrFlagsAndAttributes", "AppStderrCopyAndTruncate", "AppStopMethodSkip", "AppStopMethodConsole",
            "AppStopMethodWindow", "AppStopMethodThreads", "AppKillProcessTree", "AppThrottle", "AppRedirectHook",
            "AppRotateFiles", "AppRotateOnline", "AppRotateSeconds", "AppRotateBytes", "AppRotateBytesHigh",
            "AppRotateDelay", "AppTimestampLog", "DependOnGroup", "DependOnService", "Description", "DisplayName",
            "Environment", "ImagePath", "ObjectName", "Name", "Start", "Type"
        };
        Assert.Equal(expected.Order(), NssmSettings.All.Select(item => item.Name).Order());
        Assert.Equal(expected.Length, NssmSettings.All.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("", 0UL)]
    [InlineData("All", 0UL)]
    [InlineData("Default", 0UL)]
    [InlineData("*", 0UL)]
    [InlineData("0", 1UL)]
    [InlineData("0-3", 15UL)]
    [InlineData("0,2,4-5", 53UL)]
    [InlineData("63", 0x8000000000000000UL)]
    public void Affinity_parser_matches_NSSM_notation(string text, ulong expected) => Assert.Equal(expected, ManagedServiceRuntime.ParseAffinity(text));

    [Theory]
    [InlineData("-1")]
    [InlineData("64")]
    [InlineData("3-1")]
    [InlineData("x")]
    public void Affinity_parser_rejects_invalid_values(string text) => Assert.Throws<ArgumentException>(() => ManagedServiceRuntime.ParseAffinity(text));

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a\0b")]
    public void Service_name_validation_rejects_registry_escape(string value) => Assert.Throws<ArgumentException>(() => NssmRegistryStore.ValidateServiceName(value));

    [Fact]
    public void Configuration_never_serializes_service_password()
    {
        var value = new NssmServiceConfiguration { Name = "test", Application = @"C:\test.exe", ServicePassword = "correct horse battery staple".ToCharArray() };
        var json = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("correct horse", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ServicePassword", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Defaults_match_NSSM_2_24_101()
    {
        var value = new NssmServiceConfiguration();
        Assert.Equal(1500u, value.ThrottleDelayMilliseconds);
        Assert.Equal(1500u, value.StopMethodConsoleMilliseconds);
        Assert.Equal(1500u, value.StopMethodWindowMilliseconds);
        Assert.Equal(1500u, value.StopMethodThreadsMilliseconds);
        Assert.True(value.KillProcessTree);
        Assert.Equal(NssmExitAction.Restart, value.DefaultExitAction);
        Assert.Equal("NORMAL_PRIORITY_CLASS", value.Priority);
        Assert.Equal("All", value.Affinity);
        Assert.Equal(2u, value.AppStdinShareMode);
    }

    [Fact]
    public void Source_map_records_official_archive_and_symbols()
    {
        var path = FindToolFile("source-map.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2.24-101-g897c7ad", document.RootElement.GetProperty("upstream").GetProperty("version").GetString());
        Assert.Equal("99F5045FFFBFFB745D67FE3A065A953C4A3D9C253B868892D9B685B0EE7D07B8", document.RootElement.GetProperty("upstream").GetProperty("archiveSha256").GetString());
        var mappings = document.RootElement.GetProperty("mappings");
        Assert.Equal(273, document.RootElement.GetProperty("upstream").GetProperty("functionDefinitions").GetInt32());
        Assert.Equal(273, mappings.GetArrayLength());
        foreach (var mapping in mappings.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(mapping.GetProperty("symbol").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(mapping.GetProperty("signature").GetString()));
            var status = mapping.GetProperty("status").GetString();
            Assert.Contains(status, new[] { "translated", "frontend-rewrite", "missing" });
            if (status is "translated" or "frontend-rewrite")
            {
                Assert.False(string.IsNullOrWhiteSpace(mapping.GetProperty("csharpFile").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(mapping.GetProperty("csharpType").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(mapping.GetProperty("csharpMethod").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(mapping.GetProperty("verification").GetString()));
            }
        }
        var summary = document.RootElement.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("invalid").GetInt32());
        Assert.Equal(0, summary.GetProperty("orphaned").GetInt32());
        Assert.Equal(273,
            summary.GetProperty("translated").GetInt32() +
            summary.GetProperty("frontendRewrite").GetInt32() +
            summary.GetProperty("missing").GetInt32() +
            summary.GetProperty("invalid").GetInt32());
        Assert.Equal(19, summary.GetProperty("commands").GetInt32());
        Assert.Equal(0, summary.GetProperty("missingCommands").GetInt32());
        Assert.Equal(49, summary.GetProperty("settings").GetInt32());
        Assert.Equal(0, summary.GetProperty("missingSettings").GetInt32());
        if (Environment.GetEnvironmentVariable("NSSM_MANAGER_REQUIRE_COMPLETE_TRANSLATION") == "1")
        {
            Assert.True(summary.GetProperty("complete").GetBoolean());
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Differential_matrix_is_opt_in_for_SCM_mutation()
    {
        var enabled = Environment.GetEnvironmentVariable("NSSM_MANAGER_RUN_SCM_TESTS");
        if (!string.Equals(enabled, "1", StringComparison.Ordinal)) return;
        Assert.True(OperatingSystem.IsWindows());
        Assert.True(System.Security.Principal.WindowsIdentity.GetCurrent().Owner is not null);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Native_service_settings_are_queried_from_SCM()
    {
        if (!OperatingSystem.IsWindows()) return;
        var services = new WindowsServiceManager();
        Assert.Equal("EventLog", services.GetNativeSetting("EventLog", "Name"));
        Assert.Equal("SERVICE_WIN32_SHARE_PROCESS", services.GetNativeSetting("EventLog", "Type"));
        Assert.Contains("svchost.exe", Assert.IsType<string>(services.GetNativeSetting("EventLog", "ImagePath")), StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(services.GetNativeSetting("EventLog", "DisplayName"))));
    }

    private static string FindToolFile(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, name);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(name);
    }
}
