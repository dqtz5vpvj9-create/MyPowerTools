using System.Text.Json;
using System.Text.Json.Nodes;
using LocalLagCleaner.MyPowerTools;
using LocalLagCleaner.Tool;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.Tests;

public sealed class LocalLagCleanerRemediationTests
{
    private const ulong GiB = 1024UL * 1024 * 1024;
    private static PagingSample Sample(int second, double pages = 416, double? available = 28d * GiB,
        double? latency = 0.083, double? commit = 47.7) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(second), pages, 24, available, commit, latency);

    [Fact]
    public void Screenshot_like_paging_with_headroom_and_fast_storage_is_information_only()
    {
        var result = PagingAnalyzer.Analyze(Enumerable.Range(0, 15).Select(i => Sample(i, i == 0 ? 1658 : 328)).ToArray(), 64 * GiB, new());
        Assert.NotNull(result);
        Assert.Equal(LagSeverity.Info, result.Severity);
        Assert.Equal(0, result.Score);
        Assert.Contains("无法区分", result.CausalChain);
    }

    [Fact]
    public void Concurrent_sustained_memory_pressure_is_actionable_without_claiming_a_cause()
    {
        var result = PagingAnalyzer.Analyze(Enumerable.Range(0, 5).Select(i => Sample(i, available: 0.5 * GiB)).ToArray(), 64 * GiB, new());
        Assert.Equal(LagSeverity.Warning, result!.Severity);
        Assert.Equal(FindingConfidence.Medium, result.Confidence);
        Assert.False(result.CanClean);
        Assert.Contains("仍需归因", result.CausalChain);
    }

    [Fact]
    public void Concurrent_storage_pressure_is_actionable_even_with_free_memory()
    {
        var result = PagingAnalyzer.Analyze(Enumerable.Range(0, 5).Select(i => Sample(i, latency: 50)).ToArray(), 64 * GiB, new());
        Assert.Equal(LagSeverity.Warning, result!.Severity);
    }

    [Fact]
    public void Independent_paging_and_disk_peaks_do_not_imply_correlation()
    {
        PagingSample[] samples = [Sample(0, latency: 0.1), Sample(1, latency: 0.1), Sample(2, latency: 0.1),
            Sample(3, pages: 0, latency: 100), Sample(4, pages: 0, latency: 100)];
        Assert.Equal(LagSeverity.Info, PagingAnalyzer.Analyze(samples, 64 * GiB, new())!.Severity);
    }

    [Fact]
    public void Single_peak_missing_counters_and_sampling_gaps_do_not_prove_sustained_pressure()
    {
        Assert.Equal(LagSeverity.Info, PagingAnalyzer.Analyze([Sample(0, available: 0)], 64 * GiB, new())!.Severity);
        var missing = PagingAnalyzer.Analyze(Enumerable.Range(0, 5)
            .Select(i => Sample(i, available: null, latency: null, commit: null)).ToArray(), 64 * GiB, new());
        Assert.Equal(0, missing!.Score);
        Assert.Contains("不完整", missing.CausalChain);
        var gaps = PagingAnalyzer.Analyze([Sample(0, latency: 50), Sample(10, latency: 50), Sample(20, latency: 50)], 64 * GiB, new());
        Assert.Equal(LagSeverity.Info, gaps!.Severity);
        Assert.Null(PagingAnalyzer.Analyze([Sample(0, pages: 0)], 64 * GiB, new()));
    }

    [Theory]
    [InlineData("hard-paging", RemediationEntry.AutomaticMemory)]
    [InlineData("kernel-pool-warning", RemediationEntry.AutomaticKernel)]
    [InlineData("process-count-warning", RemediationEntry.AutomaticBackground)]
    [InlineData("system-drive-low-space", RemediationEntry.AutomaticStorage)]
    [InlineData("startup-density", RemediationEntry.DeepScan)]
    [InlineData("pending-reboot", RemediationEntry.DeepScan)]
    [InlineData("system-file-path-attribution-permission", RemediationEntry.ElevatedFileScan)]
    [InlineData("reliability-events", RemediationEntry.DeepScan)]
    [InlineData("disk-latency", RemediationEntry.DeepScan)]
    [InlineData("diagnostic-coverage-incomplete", RemediationEntry.DeepScan)]
    [InlineData("future-unknown-finding", RemediationEntry.DeepScan)]
    public void Every_finding_has_an_entry_steps_verification_and_recovery(string code, RemediationEntry entry)
    {
        var finding = new LagFinding(LagSeverity.Warning, code, "测试发现", "采样证据", "保留报告后复测。", false, "");
        var snapshot = EmptySnapshot() with { Findings = [finding] };
        var plan = Assert.Single(snapshot.Remediations);
        Assert.Equal(entry, plan.Entry);
        Assert.False(string.IsNullOrWhiteSpace(plan.Steps));
        Assert.False(string.IsNullOrWhiteSpace(plan.Verification));
        Assert.False(string.IsNullOrWhiteSpace(plan.Recovery));
        Assert.Contains(plan.Verification, LagReportWriter.ToMarkdown(snapshot));
        var roundTrip = JsonSerializer.Deserialize<LagDiagnosticSnapshot>(JsonSerializer.Serialize(snapshot, LagCleanerJson.Compact), LagCleanerJson.Compact);
        Assert.Equal(plan, Assert.Single(roundTrip!.Remediations));
    }

    [Fact]
    public void Cleanup_is_only_offered_for_a_supported_finding_with_cleanup_evidence()
    {
        var finding = new LagFinding(LagSeverity.Warning, "process-count-warning", "进程数量", "证据", "复测", false, "mcp-residue");
        Assert.Equal(RemediationEntry.AutomaticBackground, RemediationCatalog.Create(EmptySnapshot(), finding).Entry);
        Assert.Equal(RemediationEntry.McpCleanup, RemediationCatalog.Create(EmptySnapshot(), finding with { CanClean = true }).Entry);
    }

    [Fact]
    public async Task Finding_opens_its_own_solution_and_routes_the_action_to_the_matching_tool()
    {
        var snapshot = EmptySnapshot() with
        {
            Findings = [new(LagSeverity.Info, "hard-paging", "硬缺页", "样本", "复测", false, "")]
        };
        var commands = new List<string>();
        using var model = new LocalLagCleanerViewModel(new MptAvaloniaSurfaceContext(
            "local-lag-cleaner", "overview", Path.GetTempPath(), "light",
            (commandId, arguments, token) =>
            {
                commands.Add(commandId);
                var payload = commandId.Contains(".scan.", StringComparison.Ordinal)
                    ? new JsonObject { ["snapshot"] = JsonSerializer.SerializeToNode(snapshot, LagCleanerJson.Compact) }
                    : JsonSerializer.SerializeToNode(new CareResult("自动检查完成", [], snapshot, snapshot, false), LagCleanerJson.Compact)!.AsObject();
                return Task.FromResult(new CommandExecutionResult("test", commandId, "completed", true,
                    new JsonObject { ["result"] = new JsonObject { ["state"] = "ready", ["payload"] = payload } }.ToJsonString()));
            }, (_, _, _) => Task.CompletedTask, null!, _ => { }));

        await model.QuickScanCommand.ExecuteAsync();
        await Assert.Single(model.Findings).ShowSolutionCommand!.ExecuteAsync();
        Assert.Equal(2, model.SelectedTabIndex);
        Assert.Equal("hard-paging", model.SelectedRemediation!.Plan.FindingCode);
        await model.SelectedRemediation.PrimaryCommand.ExecuteAsync();
        Assert.Equal("local-lag-cleaner.care.memory", commands.Last());
        Assert.Equal("自动检查完成", model.CareSummary);
        Assert.Equal(0, model.SelectedTabIndex);
    }

    private static LagDiagnosticSnapshot EmptySnapshot() => new(
        DateTimeOffset.UtcNow, 5, 8, 1, 64 * GiB, 32 * GiB, 50,
        40 * GiB, 128 * GiB, 31.25, 0, GiB, GiB, 2 * GiB,
        10_000, 100_000, 200, 2_000, 1, [], [], [], [], [], [], [], []);
}
