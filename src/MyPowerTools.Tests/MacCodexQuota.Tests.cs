using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Mac;

namespace MyPowerTools.Tests;

public sealed class MacCodexQuotaTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Tooltip_includes_weekly_and_short_quota_with_reset_countdowns()
    {
        var now = new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero);
        var snapshot = new CodexQuotaSnapshot(
            new CodexQuotaWindow(25, 300, now.AddHours(2).AddMinutes(15)),
            new CodexQuotaWindow(32, 10080, now.AddDays(2).AddHours(3)),
            "app-server");

        var toolTip = MacStatusItemTrayService.BuildQuotaToolTip(
            "MyPowerTools",
            snapshot,
            now);

        Assert.Contains("Codex 7d 68% left · resets in 2d 3h", toolTip, StringComparison.Ordinal);
        Assert.Contains("Codex 5h 75% left · resets in 2h 15m", toolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Managed_status_item_monitor_keeps_refresh_retry_and_shutdown_contract()
    {
        var source = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Platform.Mac",
            "MacStatusItemTrayService.cs"));

        Assert.Contains("CodexQuotaReader.ReadAsync", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(5)", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(1)", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.DisplayWindow", source, StringComparison.Ordinal);
        Assert.Contains("MacNative.UpdateStatusItemQuota", source, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource", source, StringComparison.Ordinal);

        var awaitMonitor = source.IndexOf("await quotaMonitorTask", StringComparison.Ordinal);
        var destroyStatusItem = source.IndexOf(
            "MacNative.DestroyStatusItem(statusItemHandle)",
            awaitMonitor,
            StringComparison.Ordinal);
        Assert.True(awaitMonitor >= 0);
        Assert.True(destroyStatusItem > awaitMonitor);
    }

    [Fact]
    public void Native_status_item_uses_a_colored_retina_quota_image()
    {
        var source = File.ReadAllText(Path.Combine(
            Root,
            "native",
            "macos",
            "MptMacNative",
            "MptMacNative.mm"));

        Assert.Contains("mpt_status_item_update_quota", source, StringComparison.Ordinal);
        Assert.Contains("NSBitmapImageRep", source, StringComparison.Ordinal);
        Assert.Contains("canvasSize = 64", source, StringComparison.Ordinal);
        Assert.Contains("remainingPercent >= 50", source, StringComparison.Ordinal);
        Assert.Contains("remainingPercent >= 20", source, StringComparison.Ordinal);
        Assert.Contains("[image setTemplate:NO]", source, StringComparison.Ordinal);
        Assert.Contains("button.image = image", source, StringComparison.Ordinal);
        Assert.Contains("dispatch_sync(dispatch_get_main_queue(), updateBlock)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_reader_discovers_codex_on_macos_and_preserves_stdio_compatibility()
    {
        var source = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Platform.Abstractions",
            "CodexQuotaReader.cs"));

        Assert.Contains("\"MPT_CODEX_APP_SERVER_EXE\"", source, StringComparison.Ordinal);
        Assert.Contains("\"/opt/homebrew/bin/codex\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Codex.app\"", source, StringComparison.Ordinal);
        Assert.Contains("Path.PathSeparator", source, StringComparison.Ordinal);
        Assert.Contains("StandardInput.FlushAsync(timeout.Token)", source, StringComparison.Ordinal);
        Assert.Contains("ReadResponseLineAsync(process, \"mpt-init\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StandardInputEncoding", source, StringComparison.Ordinal);
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
