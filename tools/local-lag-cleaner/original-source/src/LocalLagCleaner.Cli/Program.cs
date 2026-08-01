using System.Text.Json;
using LocalLagCleaner.MyPowerTools;

return await LocalLagCleanerCli.RunAsync(args);

internal static class LocalLagCleanerCli
{
    public static async Task<int> RunAsync(string[] commandLineArguments)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("本机卡顿专清目前支持 Windows。");
                return 2;
            }

            var parsed = CliArguments.Parse(commandLineArguments);
            if (parsed.Command is "help" or "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            var stateDirectory = parsed.GetValue("--state-dir") ??
                ResolveDefaultStateDirectory();
            var options = new LagCleanerOptions
            {
                SampleSeconds = parsed.GetInt("--seconds", 3),
                StaleMcpMinutes = parsed.GetInt("--stale-minutes", 30),
                PreserveNewestMcpGroups = parsed.GetInt("--preserve-groups", 2)
            }.Normalize();
            var engine = new LagDiagnosticsEngine();
            var coordinator = new CleanupCoordinator(stateDirectory);

            switch (parsed.Command)
            {
                case "scan":
                {
                    var reportsDirectory = Path.Combine(stateDirectory, "reports");
                    var history = ReadHistory(reportsDirectory);
                    var scanned = await engine.ScanAsync(options).ConfigureAwait(false);
                    var snapshot = LagTrendAnalyzer.ApplyHistory(scanned, history);
                    var paths = await LagReportWriter.WriteAsync(
                        snapshot,
                        reportsDirectory).ConfigureAwait(false);
                    if (parsed.HasFlag("--json"))
                    {
                        Console.WriteLine(JsonSerializer.Serialize(snapshot, LagCleanerJson.Indented));
                    }
                    else
                    {
                        PrintSummary(snapshot);
                        Console.WriteLine($"报告：{paths.MarkdownPath}");
                    }
                    return 0;
                }
                case "plan":
                {
                    var action = ParseAction(parsed.RequireValue("--action"));
                    var reportsDirectory = Path.Combine(stateDirectory, "reports");
                    var history = ReadHistory(reportsDirectory);
                    var scanned = await engine.ScanAsync(options).ConfigureAwait(false);
                    var snapshot = LagTrendAnalyzer.ApplyHistory(scanned, history);
                    await LagReportWriter.WriteAsync(
                        snapshot,
                        reportsDirectory).ConfigureAwait(false);
                    var plan = coordinator.CreatePlan(action, snapshot);
                    Console.WriteLine(JsonSerializer.Serialize(plan, LagCleanerJson.Indented));
                    Console.WriteLine();
                    Console.WriteLine($"确认令牌：{plan.ConfirmationToken}");
                    var serviceFlag = plan.RequiresAdministrator ? " --allow-service-restart" : "";
                    var disconnectFlag = plan.MayDisconnectSession ? " --allow-disconnect" : "";
                    Console.WriteLine(
                        $"十分钟内执行：local-lag-cleaner apply --token {plan.ConfirmationToken}{serviceFlag}{disconnectFlag}");
                    return 0;
                }
                case "apply":
                {
                    var token = parsed.RequireValue("--token");
                    var result = await coordinator.ApplyPendingPlanAsync(
                        token,
                        parsed.HasFlag("--allow-disconnect"),
                        parsed.HasFlag("--allow-service-restart")).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(result, LagCleanerJson.Indented));
                    return result.Succeeded ? 0 : 1;
                }
                case "report":
                {
                    var reportPath = Path.Combine(stateDirectory, "reports", "latest.md");
                    if (!File.Exists(reportPath))
                    {
                        Console.Error.WriteLine("尚无诊断报告，请先运行 scan。");
                        return 2;
                    }
                    Console.WriteLine(reportPath);
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"未知命令：{parsed.Command}");
                    PrintHelp();
                    return 2;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static CleanupAction ParseAction(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "mcp-residue" => CleanupAction.McpResidue,
            "weflow" => CleanupAction.WeFlow,
            "delivery-optimization" => CleanupAction.DeliveryOptimization,
            "nvidia-container" => CleanupAction.NvidiaContainer,
            "remote-desktop" => CleanupAction.RemoteDesktop,
            "windows-search" => CleanupAction.WindowsSearch,
            _ => throw new ArgumentException(
                "action 支持 mcp-residue、weflow、delivery-optimization、nvidia-container、remote-desktop、windows-search。")
        };
    }

    private static string ResolveDefaultStateDirectory()
    {
        var dataRoot = Environment.GetEnvironmentVariable("MPT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyPowerTools");
        }

        return Path.GetFullPath(Path.Combine(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot)),
            "state",
            "tools",
            "local-lag-cleaner"));
    }

    private static IReadOnlyList<LagDiagnosticSnapshot> ReadHistory(string reportsDirectory)
    {
        var candidates = new List<string>();
        var historyDirectory = Path.Combine(reportsDirectory, "history");
        if (Directory.Exists(historyDirectory))
        {
            candidates.AddRange(
                Directory.EnumerateFiles(historyDirectory, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                    .Take(32));
        }

        var latestPath = Path.Combine(reportsDirectory, "latest.json");
        if (File.Exists(latestPath))
        {
            candidates.Add(latestPath);
        }

        var snapshots = new List<LagDiagnosticSnapshot>(candidates.Count);
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<LagDiagnosticSnapshot>(
                    File.ReadAllText(path),
                    LagCleanerJson.Compact);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // Continue with the remaining immutable history items.
            }
        }

        return snapshots;
    }

    private static void PrintSummary(LagDiagnosticSnapshot snapshot)
    {
        Console.WriteLine($"风险：{snapshot.OverallSeverity}");
        Console.WriteLine($"CPU：{snapshot.TotalCpuPercent:n1}%");
        Console.WriteLine($"物理内存：{snapshot.PhysicalUsedPercent:n1}%");
        Console.WriteLine($"内存压缩：{LagDiagnosticsEngine.FormatBytes(snapshot.MemoryCompressionBytes)}");
        Console.WriteLine($"内核池：{LagDiagnosticsEngine.FormatBytes(snapshot.KernelTotalBytes)}");
        Console.WriteLine($"System 句柄：{snapshot.SystemHandleCount:n0}");
        Console.WriteLine($"进程 / 线程：{snapshot.ProcessCount:n0} / {snapshot.ThreadCount:n0}");
        Console.WriteLine($"MCP 清理候选：{snapshot.McpCleanupCandidateCount}");
        foreach (var finding in snapshot.Findings)
        {
            Console.WriteLine($"[{finding.Severity}] {finding.Title}：{finding.Evidence}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        本机卡顿专清

        local-lag-cleaner scan [--seconds 3] [--json]
        local-lag-cleaner plan --action mcp-residue
        local-lag-cleaner plan --action weflow
        local-lag-cleaner plan --action delivery-optimization
        local-lag-cleaner plan --action nvidia-container
        local-lag-cleaner plan --action remote-desktop
        local-lag-cleaner plan --action windows-search
        local-lag-cleaner apply --token XXXXXXXX
          [--allow-service-restart] [--allow-disconnect]
        local-lag-cleaner report

        所有修改均采用“计划 + 确认令牌”两阶段协议。服务重启还需要管理员终端和
        --allow-service-restart；远程桌面服务另需 --allow-disconnect。
        """);
    }
}

internal sealed class CliArguments
{
    private readonly IReadOnlyList<string> _values;

    private CliArguments(string command, IReadOnlyList<string> values)
    {
        Command = command;
        _values = values;
    }

    public string Command { get; }

    public static CliArguments Parse(string[] values)
    {
        return values.Length == 0
            ? new CliArguments("help", [])
            : new CliArguments(values[0].ToLowerInvariant(), values.Skip(1).ToArray());
    }

    public bool HasFlag(string name)
    {
        return _values.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public string? GetValue(string name)
    {
        for (var index = 0; index < _values.Count - 1; index++)
        {
            if (string.Equals(_values[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return _values[index + 1];
            }
        }
        return null;
    }

    public string RequireValue(string name)
    {
        return GetValue(name) ?? throw new ArgumentException($"缺少参数 {name}。");
    }

    public int GetInt(string name, int fallback)
    {
        var value = GetValue(name);
        return value is null
            ? fallback
            : int.TryParse(value, out var number)
                ? number
                : throw new ArgumentException($"{name} 必须是整数。");
    }
}
