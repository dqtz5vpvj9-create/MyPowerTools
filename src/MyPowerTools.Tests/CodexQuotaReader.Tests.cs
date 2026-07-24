using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Tests;

public sealed class CodexQuotaReaderTests
{
    [Fact]
    public void ParseAppServerResponse_PrefersCodexTotalAndClassifiesWindows()
    {
        const string json = """
            {
              "id": "mpt-rate-limits",
              "result": {
                "rateLimits": {
                  "limitId": "codex",
                  "primary": { "usedPercent": 20, "windowDurationMins": 300, "resetsAt": 1780466783 },
                  "secondary": { "usedPercent": 40, "windowDurationMins": 10080, "resetsAt": 1781052383 }
                },
                "rateLimitsByLimitId": {
                  "codex_bengalfox": {
                    "limitId": "codex_bengalfox",
                    "primary": { "usedPercent": 0, "windowDurationMins": 300 }
                  },
                  "codex": {
                    "limitId": "codex",
                    "primary": { "usedPercent": 17, "windowDurationMins": 300, "resetsAt": 1780466783 },
                    "secondary": { "usedPercent": 72, "windowDurationMins": 10080, "resetsAt": 1781052383 }
                  }
                }
              }
            }
            """;

        var snapshot = CodexQuotaReader.ParseAppServerResponse(json);

        Assert.Equal("app-server", snapshot.Source);
        Assert.Equal(83, snapshot.ShortWindow?.RemainingPercent);
        Assert.Equal(28, snapshot.WeeklyWindow?.RemainingPercent);
        Assert.Same(snapshot.WeeklyWindow, snapshot.DisplayWindow);
    }

    [Fact]
    public void ParseAppServerResponse_SupportsWeeklyOnlyQuota()
    {
        const string json = """
            {
              "result": {
                "rateLimitsByLimitId": {
                  "codex": {
                    "limitId": "codex",
                    "primary": { "usedPercent": 32, "windowDurationMins": 10080, "resetsAt": 1781052383 },
                    "secondary": null
                  }
                }
              }
            }
            """;

        var snapshot = CodexQuotaReader.ParseAppServerResponse(json);

        Assert.Null(snapshot.ShortWindow);
        Assert.Equal(68, snapshot.WeeklyWindow?.RemainingPercent);
        Assert.Equal(68, snapshot.DisplayWindow?.RemainingPercent);
    }

    [Fact]
    public void ParseAppServerResponse_SupportsSnakeCaseAndSelectsCodexByLimitId()
    {
        const string json = """
            {
              "result": {
                "rate_limits_by_limit_id": {
                  "other": {
                    "limit_id": "codex_other",
                    "primary": { "used_percent": 1, "window_duration_mins": 300 }
                  },
                  "default": {
                    "limit_id": "codex",
                    "primary": { "used_percent": 12, "window_duration_mins": 300 },
                    "secondary": { "used_percent": 36, "window_duration_mins": 10080 }
                  }
                }
              }
            }
            """;

        var snapshot = CodexQuotaReader.ParseAppServerResponse(json);

        Assert.Equal(88, snapshot.ShortWindow?.RemainingPercent);
        Assert.Equal(64, snapshot.WeeklyWindow?.RemainingPercent);
        Assert.Equal(64, snapshot.DisplayWindow?.RemainingPercent);
    }

    [Fact]
    public void ParseSessionEvent_SupportsSnakeCasePayload()
    {
        const string json = """
            {
              "timestamp": "2026-06-03T01:00:00.000Z",
              "payload": {
                "type": "token_count",
                "rate_limits": {
                  "limit_id": "codex",
                  "primary": { "used_percent": 8, "window_minutes": 300, "resets_at": 1780466783 },
                  "secondary": { "used_percent": 77, "window_minutes": 10080, "resets_at": 1781052383 }
                }
              }
            }
            """;

        var snapshot = CodexQuotaReader.ParseSessionEvent(json);

        Assert.Equal("sessions", snapshot.Source);
        Assert.Equal(92, snapshot.ShortWindow?.RemainingPercent);
        Assert.Equal(23, snapshot.WeeklyWindow?.RemainingPercent);
    }

    [Fact]
    public void ParseSessionEvent_SupportsCamelCasePayload()
    {
        const string json = """
            {
              "payload": {
                "type": "tokenCount",
                "rateLimits": {
                  "limitId": "codex",
                  "primary": { "usedPercent": 14, "windowMinutes": 300 },
                  "secondary": { "usedPercent": 41, "windowDurationMins": 10080 }
                }
              }
            }
            """;

        var snapshot = CodexQuotaReader.ParseSessionEvent(json);

        Assert.Equal("sessions", snapshot.Source);
        Assert.Equal(86, snapshot.ShortWindow?.RemainingPercent);
        Assert.Equal(59, snapshot.WeeklyWindow?.RemainingPercent);
    }

    [Fact]
    public void AppServerProtocol_FlushesInitializationAndKeepsDefaultStreamEncoding()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MyPowerTools.Platform.Abstractions",
            "CodexQuotaReader.cs"));

        Assert.Contains("StandardInput.FlushAsync(timeout.Token)", source, StringComparison.Ordinal);
        Assert.Contains("ReadResponseLineAsync(process, \"mpt-init\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StandardInputEncoding", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayMonitor_KeepsRefreshRetryAndIconLifetimeContract()
    {
        var root = FindRepositoryRoot();
        var tray = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MyPowerTools.Platform.Windows",
            "WindowsTrayService.cs"));
        var renderer = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MyPowerTools.Platform.Windows",
            "CodexQuotaIconRenderer.cs"));

        Assert.Contains("TimeSpan.FromMinutes(5)", tray, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(1)", tray, StringComparison.Ordinal);
        Assert.Contains("await quotaMonitorTask", tray, StringComparison.Ordinal);
        Assert.Contains("DestroyIcon(previousIcon)", tray, StringComparison.Ordinal);
        Assert.Contains("if (!updated)", tray, StringComparison.Ordinal);
        Assert.Contains(">= 50 =>", renderer, StringComparison.Ordinal);
        Assert.Contains(">= 20 =>", renderer, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the MyPowerTools repository root.");
    }
}
