using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LocalLagCleaner.MyPowerTools;
using LocalLagCleaner.Tool;
using MyPowerTools.Abstractions;
using MyPowerTools.Broker;
using MyPowerTools.ModuleHost.StdioCompat;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.Services;
using LagProcessSnapshot = LocalLagCleaner.MyPowerTools.ProcessSnapshot;
using SdkCommandRequest = MyPowerTools.Abstractions.CommandRequest;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed class LocalLagCleanerProductTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string ToolRoot = Path.Combine(
        Root,
        "tools",
        "local-lag-cleaner",
        "sdk-tool");

    [Fact]
    public void Diagnostic_thresholds_normalize_ordered_ranges()
    {
        var normalized = new LagCleanerOptions
        {
            SampleIntervalMilliseconds = 100,
            CpuSustainedWarningPercent = 95,
            CpuSustainedCriticalPercent = 20,
            DiskLatencyWarningMilliseconds = 500,
            DiskLatencyCriticalMilliseconds = 10,
            PagedPoolWarningBytes = 4UL * 1024 * 1024 * 1024,
            NonPagedPoolWarningBytes = 6UL * 1024 * 1024 * 1024,
            KernelPoolCriticalBytes = 1,
            SystemHandleWarningCount = 2_000_000,
            SystemHandleCriticalCount = 1_000,
            ProcessWarningCount = 800,
            ProcessCriticalCount = 100
        }.Normalize();

        Assert.Equal(1_000, normalized.SampleIntervalMilliseconds);
        Assert.Equal(95, normalized.CpuSustainedCriticalPercent);
        Assert.Equal(500, normalized.DiskLatencyCriticalMilliseconds);
        Assert.Equal(6UL * 1024 * 1024 * 1024, normalized.KernelPoolCriticalBytes);
        Assert.Equal(2_000_000U, normalized.SystemHandleCriticalCount);
        Assert.Equal(800U, normalized.ProcessCriticalCount);
    }

    [Fact]
    public void Mcp_analyzer_preserves_new_sessions_and_only_selects_old_idle_groups()
    {
        var now = DateTimeOffset.Parse("2026-07-27T08:00:00Z");
        ProcessAnalysisInput[] processes =
        [
            new(10, 1, "mcp-server", now.AddMinutes(-5), 0, 20_000_000, 100, @"C:\tool\mcp-server.exe", "doubao-computer-use-local"),
            new(11, 10, "python", now.AddMinutes(-5), 0, 30_000_000, 120, @"C:\Python\python.exe", "python worker.py"),
            new(20, 1, "mcp-server", now.AddMinutes(-120), 0, 20_000_000, 100, @"C:\tool\mcp-server.exe", "doubao-computer-use-local"),
            new(21, 20, "python", now.AddMinutes(-120), 0, 30_000_000, 120, @"C:\Python\python.exe", "python worker.py"),
            new(30, 1, "mcp-server", now.AddMinutes(-180), 5, 20_000_000, 100, @"C:\tool\mcp-server.exe", "doubao-computer-use-local")
        ];
        var options = new LagCleanerOptions
        {
            PreserveNewestMcpGroups = 1,
            StaleMcpMinutes = 30,
            IdleMcpCorePercentThreshold = 2
        };

        var groups = McpResidueAnalyzer.Analyze(processes, options, now);

        Assert.Equal(3, groups.Count);
        Assert.False(groups.Single(item => item.RootProcessId == 10).IsCleanupCandidate);
        Assert.True(groups.Single(item => item.RootProcessId == 20).IsCleanupCandidate);
        Assert.False(groups.Single(item => item.RootProcessId == 30).IsCleanupCandidate);
        var candidate = groups.Single(item => item.RootProcessId == 20);
        Assert.Equal([20, 21], candidate.ProcessIds);
        Assert.Equal(McpCleanupEvidenceKind.OrphanedParent, candidate.CleanupEvidence);
        Assert.Equal(FindingConfidence.High, candidate.EvidenceConfidence);
        Assert.Collection(
            candidate.Members,
            member =>
            {
                Assert.Equal(20, member.ProcessId);
                Assert.Equal("mcp-server", member.Name);
                Assert.Equal(now.AddMinutes(-120), member.StartTimeUtc);
                Assert.True(member.IsDirectMatch);
            },
            member =>
            {
                Assert.Equal(21, member.ProcessId);
                Assert.Equal(20, member.ParentProcessId);
                Assert.False(member.IsDirectMatch);
            });
    }

    [Fact]
    public void Mcp_analyzer_offers_old_idle_group_replaced_under_the_same_trusted_parent()
    {
        var now = DateTimeOffset.Parse("2026-07-28T08:00:00Z");
        ProcessAnalysisInput[] processes =
        [
            new(1, 0, "codex", now.AddHours(-5), 0, 100_000_000, 100, @"C:\tool\codex.exe", "codex"),
            new(20, 1, "mcp-server", now.AddHours(-3), 0, 20_000_000, 100, @"C:\tool\mcp-server.exe", "doubao-computer-use-local"),
            new(21, 20, "python", now.AddHours(-3), 0, 30_000_000, 120, @"C:\Python\python.exe", "python worker.py"),
            new(30, 1, "mcp-server", now.AddMinutes(-5), 0, 20_000_000, 100, @"C:\tool\mcp-server.exe", "doubao-computer-use-local")
        ];

        var groups = McpResidueAnalyzer.Analyze(
            processes,
            new LagCleanerOptions
            {
                PreserveNewestMcpGroups = 1,
                StaleMcpMinutes = 30,
                IdleMcpCorePercentThreshold = 2
            },
            now);
        var group = groups.Single(item => item.RootProcessId == 20);

        Assert.True(group.IsCleanupCandidate);
        Assert.Equal(
            McpCleanupEvidenceKind.SupersededByNewerSameParent,
            group.CleanupEvidence);
        Assert.Equal(FindingConfidence.Medium, group.EvidenceConfidence);
        var replacement = Assert.Single(group.SupersedingSessions);
        Assert.Equal(30, replacement.RootProcessId);
        Assert.Equal(30, replacement.MarkerProcessId);
        Assert.Contains("同父", group.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Mcp_analyzer_rejects_same_parent_replacement_from_an_untrusted_launcher()
    {
        var now = DateTimeOffset.Parse("2026-07-28T08:00:00Z");
        ProcessAnalysisInput[] processes =
        [
            new(100, 0, "random-launcher", now.AddHours(-5), 0, 100_000_000, 100, @"C:\tool\random-launcher.exe", ""),
            new(20, 100, "mcp-server", now.AddHours(-3), 0, 20_000_000, 100, @"C:\tool\mcp-server.exe", "doubao-computer-use-local"),
            new(30, 100, "mcp-server", now.AddMinutes(-5), 0, 20_000_000, 100, @"C:\tool\mcp-server.exe", "doubao-computer-use-local")
        ];

        var groups = McpResidueAnalyzer.Analyze(
            processes,
            new LagCleanerOptions
            {
                PreserveNewestMcpGroups = 1,
                StaleMcpMinutes = 30,
                IdleMcpCorePercentThreshold = 2
            },
            now);
        var oldGroup = groups.Single(item => item.RootProcessId == 20);

        Assert.False(oldGroup.IsCleanupCandidate);
        Assert.Equal(McpCleanupEvidenceKind.None, oldGroup.CleanupEvidence);
        Assert.Contains("证据不足", oldGroup.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cleanup_plan_requires_a_matching_unexpired_confirmation_token_before_execution()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "mpt-local-lag-cleaner-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var coordinator = new CleanupCoordinator(stateDirectory);
            var plan = coordinator.CreatePlan(
                CleanupAction.DeliveryOptimization,
                EmptySnapshot());

            Assert.Matches("^[0-9A-F]{8}$", plan.ConfirmationToken);
            var tokenError = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => coordinator.ApplyPendingPlanAsync(
                    plan.ConfirmationToken + "X",
                    allowDisconnect: false,
                    allowServiceRestart: false));

            Assert.Contains("令牌", tokenError.Message, StringComparison.Ordinal);
            Assert.Equal(plan.PlanId, coordinator.TryReadPendingPlan()?.PlanId);

            var expired = coordinator.CreatePlan(
                CleanupAction.DeliveryOptimization,
                EmptySnapshot(),
                TimeSpan.FromSeconds(-1));
            var expiryError = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.ApplyPendingPlanAsync(
                    expired.ConfirmationToken,
                    allowDisconnect: false,
                    allowServiceRestart: false));

            Assert.Contains("过期", expiryError.Message, StringComparison.Ordinal);
            Assert.Equal(expired.PlanId, coordinator.TryReadPendingPlan()?.PlanId);
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Cleanup_apply_binds_confirmation_to_plan_id_and_expected_action()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "mpt-local-lag-cleaner-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var coordinator = new CleanupCoordinator(stateDirectory);
            var plan = coordinator.CreatePlan(
                CleanupAction.DeliveryOptimization,
                EmptySnapshot());

            var planIdError = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.ApplyPendingPlanAsync(
                    new string('A', 32),
                    plan.Action,
                    plan.ConfirmationToken,
                    allowDisconnect: false,
                    allowServiceRestart: false));
            Assert.Contains("ID", planIdError.Message, StringComparison.Ordinal);
            Assert.Equal(plan.PlanId, coordinator.TryReadPendingPlan()?.PlanId);

            var actionError = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.ApplyPendingPlanAsync(
                    plan.PlanId,
                    CleanupAction.NvidiaContainer,
                    plan.ConfirmationToken,
                    allowDisconnect: false,
                    allowServiceRestart: false));
            Assert.Contains("动作", actionError.Message, StringComparison.Ordinal);
            Assert.Equal(plan.PlanId, coordinator.TryReadPendingPlan()?.PlanId);
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Concurrent_plan_writers_leave_one_complete_pending_plan()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "mpt-local-lag-cleaner-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var plans = await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(index => Task.Run(() =>
                        new CleanupCoordinator(stateDirectory).CreatePlan(
                            index % 2 == 0
                                ? CleanupAction.DeliveryOptimization
                                : CleanupAction.WindowsSearch,
                            EmptySnapshot()))));
            var pending = new CleanupCoordinator(stateDirectory).TryReadPendingPlan();

            Assert.NotNull(pending);
            Assert.Contains(plans, item => item.PlanId == pending.PlanId);
            Assert.Matches("^[0-9A-F]{8}$", pending.ConfirmationToken);
            Assert.Empty(pending.Targets);
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Claimed_process_plan_is_consumed_when_live_identity_preflight_fails()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "mpt-local-lag-cleaner-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var missingProcessId = int.MaxValue - 10;
            var plan = new CleanupPlan(
                Guid.NewGuid().ToString("N"),
                "A1B2C3D4",
                CleanupAction.McpResidue,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddMinutes(10),
                [
                    new CleanupTarget(
                        missingProcessId,
                        DateTimeOffset.UtcNow.AddHours(-2).UtcTicks,
                        "mcp-server",
                        false)
                    {
                        ParentProcessId = missingProcessId - 1,
                        McpGroupRootProcessId = missingProcessId,
                        IsMcpDirectMatch = true
                    }
                ],
                "测试 MCP 目标",
                "测试",
                false,
                false)
            {
                McpEvidence =
                [
                    new McpCleanupGroupEvidence(
                        missingProcessId,
                        missingProcessId - 1,
                        McpCleanupEvidenceKind.OrphanedParent,
                        FindingConfidence.High,
                        [])
                ]
            };
            await File.WriteAllTextAsync(
                Path.Combine(stateDirectory, "pending-cleanup.json"),
                JsonSerializer.Serialize(plan, LagCleanerJson.Compact));

            var coordinator = new CleanupCoordinator(stateDirectory);
            var result = await coordinator.ApplyPendingPlanAsync(
                plan.PlanId,
                plan.Action,
                plan.ConfirmationToken,
                allowDisconnect: false,
                allowServiceRestart: false);

            Assert.False(result.Succeeded);
            Assert.Contains("已退出", Assert.Single(result.Items).Message, StringComparison.Ordinal);
            Assert.Null(coordinator.TryReadPendingPlan());
            Assert.Empty(Directory.EnumerateFiles(stateDirectory, "claimed-cleanup-*.json"));
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Cleanup_action_json_rejects_integer_enum_values()
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<CleanupAction>(
                "0",
                LagCleanerJson.Compact));
    }

    [Fact]
    public void Standalone_sdk_manifest_and_project_use_the_supported_dotnet_surface_contract()
    {
        var manifest = JsonNode.Parse(
            File.ReadAllText(Path.Combine(ToolRoot, "tool.json")))!.AsObject();
        var primaryRouteId = manifest["primaryRouteId"]!.GetValue<string>();
        var route = manifest["routes"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(item => item["routeId"]!.GetValue<string>() == primaryRouteId);
        var surface = route["surface"]!.AsObject();

        Assert.Equal("dotnet-surface", manifest["type"]!.GetValue<string>());
        Assert.Equal("dotnet", surface["kind"]!.GetValue<string>());
        Assert.Equal(typeof(LocalLagCleanerSurfaceFactory).FullName, surface["type"]!.GetValue<string>());
        Assert.Equal(
            "stdio-jsonrpc",
            manifest["runtime"]!["transport"]!.GetValue<string>());
        Assert.EndsWith(
            "LocalLagCleaner.Runtime.exe",
            manifest["runtime"]!["command"]!.GetValue<string>(),
            StringComparison.Ordinal);
        var commandIds = manifest["commands"]!.AsArray()
            .Select(item => item!["id"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("local-lag-cleaner.scan.quick", commandIds);
        Assert.Contains("local-lag-cleaner.scan.deep", commandIds);
        Assert.Contains(
            "local-lag-cleaner.scan.file-handles-elevated",
            commandIds);
        Assert.Contains("local-lag-cleaner.cleanup.apply", commandIds);
        Assert.Contains("local-lag-cleaner.plan.windows-search", commandIds);
        var permissionLevels = manifest["permissions"]!.AsArray()
            .Select(item => item!["level"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("user", permissionLevels);
        Assert.Contains("broker", permissionLevels);
        Assert.Contains("elevated", permissionLevels);
        Assert.Contains(
            manifest["permissions"]!.AsArray(),
            permission =>
                permission!["id"]!.GetValue<string>() ==
                "system-file-handle-paths" &&
                permission["level"]!.GetValue<string>() == "elevated" &&
                permission["capability"]!.GetValue<string>() ==
                "system.handle.path.read");

        var projectPath = Path.Combine(
            ToolRoot,
            "src",
            "LocalLagCleaner.Tool",
            "LocalLagCleaner.Tool.csproj");
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var project = XDocument.Load(projectPath);
        var packageReferences = project.Descendants("PackageReference")
            .Select(element => (
                Include: element.Attribute("Include")?.Value ?? "",
                Version: element.Attribute("Version")?.Value ?? ""))
            .ToArray();

        Assert.Equal(2, packageReferences.Length);
        Assert.Contains(
            packageReferences,
            reference => reference is { Include: "MyPowerTools.AvaloniaSdk", Version: "0.2.0" });
        Assert.Contains(
            packageReferences,
            reference => reference is { Include: "MyPowerTools.ToolSdk", Version: "0.2.0" });

        var projectReferences = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? "")
            .ToArray();
        Assert.Single(projectReferences);
        Assert.EndsWith(
            @"LocalLagCleaner.Core\LocalLagCleaner.Core.csproj",
            projectReferences[0],
            StringComparison.OrdinalIgnoreCase);

        var suiteSourcePrefix = Path.GetFullPath(Path.Combine(Root, "src")) +
            Path.DirectorySeparatorChar;
        Assert.DoesNotContain(
            projectReferences,
            reference => Path.GetFullPath(Path.Combine(projectDirectory, reference))
                .StartsWith(suiteSourcePrefix, StringComparison.OrdinalIgnoreCase));

        var runtimeProject = XDocument.Load(Path.Combine(
            ToolRoot,
            "src",
            "LocalLagCleaner.Runtime",
            "LocalLagCleaner.Runtime.csproj"));
        var runtimeReferences = runtimeProject.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? "")
            .ToArray();
        Assert.Single(runtimeReferences);
        Assert.EndsWith(
            @"LocalLagCleaner.Core\LocalLagCleaner.Core.csproj",
            runtimeReferences[0],
            StringComparison.OrdinalIgnoreCase);

        var settings = JsonNode.Parse(
            File.ReadAllText(Path.Combine(ToolRoot, "settings.json")))!.AsObject();
        Assert.Equal(5, settings["quickSampleSeconds"]!.GetValue<int>());
        Assert.Equal(15, settings["deepSampleSeconds"]!.GetValue<int>());
        Assert.Equal(1_000, settings["sampleIntervalMilliseconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task Elevated_file_handle_attribution_is_bounded_brokered_and_audited()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "mpt-local-lag-cleaner-elevated",
            Guid.NewGuid().ToString("N"));
        try
        {
            var audit = new AuditLog(Path.Combine(testRoot, "audit.jsonl"));
            var exitCode =
                await SystemFileHandleDiagnosticExecutor.ExecuteAsync([], audit);

            Assert.Equal(3, exitCode);
            var entry = Assert.Single(audit.ReadAll());
            Assert.Equal("local-lag-cleaner", entry.ModuleId);
            Assert.Equal("system-file-handle-path-sample", entry.ActionId);
            Assert.Equal("elevated", entry.PermissionLevel);
            Assert.Equal("rejected", entry.Result);

            var runtimeClient = File.ReadAllText(Path.Combine(
                ToolRoot,
                "src",
                "LocalLagCleaner.Runtime",
                "ElevatedFileHandleDiagnosticClient.cs"));
            var cleanupClient = File.ReadAllText(Path.Combine(
                ToolRoot,
                "src",
                "LocalLagCleaner.Runtime",
                "ElevatedCleanupClient.cs"));
            var brokerProbe = File.ReadAllText(Path.Combine(
                Root,
                "src",
                "MyPowerTools.Broker",
                "SystemFileHandleDiagnostics.cs"));
            var brokerProgram = File.ReadAllText(Path.Combine(
                Root,
                "src",
                "MyPowerTools.ElevatedBroker",
                "Program.cs"));
            var brokerCleanup = File.ReadAllText(Path.Combine(
                Root,
                "src",
                "MyPowerTools.ElevatedBroker",
                "LocalLagCleanerCleanupExecutor.cs"));
            var view = File.ReadAllText(Path.Combine(
                ToolRoot,
                "src",
                "LocalLagCleaner.Tool",
                "LocalLagCleanerView.axaml"));
            var coordinator = File.ReadAllText(Path.Combine(
                ToolRoot,
                "src",
                "LocalLagCleaner.Core",
                "CleanupCoordinator.cs"));

            Assert.Contains("Verb = \"runas\"", runtimeClient, StringComparison.Ordinal);
            Assert.Contains("maximumSamples is < 1 or > 512", runtimeClient, StringComparison.Ordinal);
            Assert.Contains("Verb = \"runas\"", cleanupClient, StringComparison.Ordinal);
            Assert.Contains("confirmationToken", cleanupClient, StringComparison.Ordinal);
            Assert.Contains("\"SeDebugPrivilege\"", brokerProbe, StringComparison.Ordinal);
            Assert.Contains("ProcessDuplicateHandle", brokerProbe, StringComparison.Ordinal);
            Assert.Contains("maximumSamples is < 1 or > 512", brokerProbe, StringComparison.Ordinal);
            Assert.Contains("\"diagnostics\"", brokerProgram, StringComparison.Ordinal);
            Assert.Contains("\"file-handles\"", brokerProgram, StringComparison.Ordinal);
            Assert.Contains("\"cleanup\"", brokerProgram, StringComparison.Ordinal);
            Assert.Contains("LocalLagCleanerCleanupExecutor", brokerProgram, StringComparison.Ordinal);
            Assert.Contains("ApplyPendingPlanAsync", brokerCleanup, StringComparison.Ordinal);
            Assert.Contains("expectedStateDirectory", brokerCleanup, StringComparison.Ordinal);
            Assert.Contains("EnumDependentServices", coordinator, StringComparison.Ordinal);
            Assert.Contains("ServiceEnumerateDependents", coordinator, StringComparison.Ordinal);
            Assert.Contains("TryEnsureNamedServicesAsync", coordinator, StringComparison.Ordinal);
            Assert.Contains("FormatWin32Failure", coordinator, StringComparison.Ordinal);
            Assert.Contains("点击执行将请求管理员权限", view, StringComparison.Ordinal);
            Assert.Contains("后台进程拆分（breakdown）", view, StringComparison.Ordinal);
            Assert.Contains("处理方法：{0}", view, StringComparison.Ordinal);
            Assert.Contains("管理员 File 归因", view, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Mpt_host_routes_health_from_development_tool_through_real_stdio_runtime()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "mpt-local-lag-cleaner-host-contract",
            Guid.NewGuid().ToString("N"));
        var modulesRoot = Path.Combine(testRoot, "modules");
        var developmentToolRoot = Path.Combine(testRoot, "local-lag-cleaner");
        var dataRoot = Path.Combine(testRoot, "data");
        Directory.CreateDirectory(modulesRoot);
        Directory.CreateDirectory(developmentToolRoot);

        var manifest = JsonNode.Parse(
            File.ReadAllText(Path.Combine(ToolRoot, "tool.json")))!.AsObject();
        manifest["runtime"]!["command"] = FindRuntimeExecutable();
        await File.WriteAllTextAsync(
            Path.Combine(developmentToolRoot, "tool.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var previousDataRoot = Environment.GetEnvironmentVariable("MPT_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("MPT_DATA_ROOT", dataRoot);
            var reportsDirectory = Path.Combine(
                dataRoot,
                "state",
                "tools",
                "local-lag-cleaner",
                "reports");
            Directory.CreateDirectory(reportsDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(reportsDirectory, "latest.json"),
                JsonSerializer.Serialize(EmptySnapshot(), LagCleanerJson.Compact));

            await using var runtime = new MptHostRuntime(
                new PackageReader(),
                PlatformId.Current(),
                RuntimePaths.Create(dataRoot),
                [new StdioCompatModuleHost()]);
            runtime.Load(modulesRoot, [developmentToolRoot]);
            await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

            var module = Assert.Single(runtime.Modules);
            Assert.Equal("jsonrpc-stdio", module.Entrypoint?.Kind);
            Assert.Equal("ready", module.Status.State);
            Assert.Equal(
                Path.GetFullPath(FindRuntimeExecutable()),
                module.Entrypoint?.Command);
            var tool = Assert.Single(runtime.ListTools(includeDisabled: true));
            Assert.Equal("local-lag-cleaner", tool.Descriptor.ToolId);
            Assert.Equal("ready", tool.State);

            var healthCommand = Assert.Single(
                runtime.ListCommands("")
                    .Where(command => command.Id == "local-lag-cleaner.health"));
            Assert.Equal("local-lag-cleaner", healthCommand.ModuleId);
            Assert.Equal(120_000, healthCommand.TimeoutMs);
            Assert.Equal(
                "tool.runtime",
                healthCommand.Execution?["type"]?.GetValue<string>());

            var quickScanCommand = Assert.Single(
                runtime.ListCommands("")
                    .Where(command => command.Id == "local-lag-cleaner.scan.quick"));
            Assert.Equal(120_000, quickScanCommand.TimeoutMs);
            Assert.Equal("action", quickScanCommand.Kind);
            Assert.False(quickScanCommand.RequiresElevation);
            Assert.Equal("low", quickScanCommand.DangerLevel);
            Assert.Equal("Diagnostics", quickScanCommand.Category);
            Assert.Equal(
                "module.execute",
                quickScanCommand.Execution?["type"]?.GetValue<string>());
            Assert.Empty(quickScanCommand.Constraints ?? []);
            Assert.False(quickScanCommand.SupportsProgress);
            Assert.True(quickScanCommand.SupportsCancellation);

            var applyCommand = Assert.Single(
                runtime.ListCommands("")
                    .Where(command => command.Id == "local-lag-cleaner.cleanup.apply"));
            Assert.Equal(120_000, applyCommand.TimeoutMs);
            Assert.False(applyCommand.RequiresElevation);
            Assert.Equal("high", applyCommand.DangerLevel);
            Assert.Equal("Cleanup", applyCommand.Category);
            Assert.Equal(
                "module.execute",
                applyCommand.Execution?["type"]?.GetValue<string>());
            Assert.True(
                applyCommand.Execution?["mutatesSystemState"]?.GetValue<bool>());
            Assert.True(
                applyCommand.Execution?["runsExternalProcesses"]?.GetValue<bool>());
            Assert.True(
                applyCommand.Execution?["brokerApprovalOnly"]?.GetValue<bool>());
            Assert.Equal(
                [
                    MptOperationConstraints.MutatesSystemState,
                    MptOperationConstraints.RunsExternalProcesses
                ],
                applyCommand.Constraints);
            Assert.False(applyCommand.SupportsProgress);
            Assert.True(applyCommand.SupportsCancellation);

            var invocationId = "local-lag-cleaner-host-health-" + Guid.NewGuid().ToString("N");
            var result = await runtime.ExecuteCommandAsync(
                new SdkCommandRequest(
                    invocationId,
                    "local-lag-cleaner.health",
                    new JsonObject()),
                CancellationToken.None);

            Assert.True(result.Success, result.Error?.Message);
            Assert.Equal("succeeded", result.State);
            var response = JsonNode.Parse(result.Output)!.AsObject();
            Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
            Assert.Equal(invocationId, response["id"]!.GetValue<string>());
            Assert.Equal("ready", response["result"]!["state"]!.GetValue<string>());
            Assert.Equal(
                "local-lag-cleaner",
                response["result"]!["payload"]!["toolId"]!.GetValue<string>());
            Assert.Equal(
                OperatingSystem.IsWindows() ? "ready" : "unsupported",
                response["result"]!["payload"]!["state"]!.GetValue<string>());

            var planInvocationId =
                "local-lag-cleaner-host-plan-" + Guid.NewGuid().ToString("N");
            var planResult = await runtime.ExecuteCommandAsync(
                new SdkCommandRequest(
                    planInvocationId,
                    "local-lag-cleaner.plan.delivery-optimization",
                    new JsonObject()),
                CancellationToken.None);

            Assert.True(planResult.Success, planResult.Error?.Message);
            var planResponse = JsonNode.Parse(planResult.Output)!.AsObject();
            var confirmationToken = planResponse["result"]!["payload"]![
                "confirmationToken"]!.GetValue<string>();
            Assert.Matches("^[0-9A-F]{8}$", confirmationToken);
            Assert.Contains(confirmationToken, planResult.Output, StringComparison.Ordinal);

            var logRecord = Assert.Single(
                runtime.TailLogs("local-lag-cleaner")
                    .Where(record => record.InvocationId == planInvocationId));
            Assert.DoesNotContain(confirmationToken, logRecord.Message, StringComparison.Ordinal);
            Assert.Contains(
                "\"confirmationToken\":\"****\"",
                logRecord.Message,
                StringComparison.Ordinal);

            var historyRecord = Assert.Single(
                runtime.ListCommandHistory("local-lag-cleaner")
                    .Where(record => record.InvocationId == planInvocationId));
            Assert.DoesNotContain(confirmationToken, historyRecord.Summary, StringComparison.Ordinal);
            Assert.Contains(
                "\"confirmationToken\":\"****\"",
                historyRecord.Summary,
                StringComparison.Ordinal);

            var surfaceStatusText = ShellCommandExecutionService.FormatStatusText(
                planResult.State,
                planResult.Output);
            Assert.DoesNotContain(
                confirmationToken,
                surfaceStatusText,
                StringComparison.Ordinal);
            var shellExecution = new ShellCommandExecutionResult(
                surfaceStatusText,
                new HostProto.CommandExecutionResponse
                {
                    State = planResult.State,
                    Summary = planResult.Output
                },
                RequiresPermissionPrompt: false);
            var surfaceStatus =
                ShellWorkspaceController.ToCommandExecutionStatus(shellExecution);
            var surfaceResult = ShellWorkspaceController.ToSurfaceCommandExecutionResult(
                planInvocationId,
                "local-lag-cleaner.plan.delivery-optimization",
                surfaceStatus);
            Assert.Contains(
                confirmationToken,
                surfaceResult.Output,
                StringComparison.Ordinal);
            Assert.Equal(
                confirmationToken,
                JsonNode.Parse(surfaceResult.Output)!["result"]!["payload"]![
                    "confirmationToken"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("MPT_DATA_ROOT", previousDataRoot);
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Stdio_runtime_rejects_unknown_fields_and_numeric_actions_with_fixed_codes()
    {
        var unknownArgument = await InvokeRuntimeAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "unknown-argument",
            ["method"] = "executeCommand",
            ["commandId"] = "local-lag-cleaner.health",
            ["args"] = new JsonObject
            {
                ["unexpected"] = true
            }
        });
        Assert.Equal(
            "validation.failed",
            unknownArgument["result"]!["error"]!["code"]!.GetValue<string>());

        var numericAction = await InvokeRuntimeAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "numeric-action",
            ["method"] = "executeCommand",
            ["commandId"] = "local-lag-cleaner.cleanup.apply",
            ["args"] = new JsonObject
            {
                ["planId"] = new string('A', 32),
                ["expectedAction"] = 0,
                ["confirmationToken"] = "A1B2C3D4"
            }
        });
        Assert.Equal(
            "validation.failed",
            numericAction["result"]!["error"]!["code"]!.GetValue<string>());

        var invalidEnvelope = await InvokeRuntimeAsync(new JsonObject
        {
            ["jsonrpc"] = "1.0",
            ["id"] = "invalid-envelope",
            ["method"] = "executeCommand",
            ["commandId"] = "local-lag-cleaner.health",
            ["args"] = new JsonObject()
        });
        Assert.Equal(
            "request.invalid",
            invalidEnvelope["result"]!["error"]!["code"]!.GetValue<string>());

        var invalidMethod = await InvokeRuntimeAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "invalid-method",
            ["method"] = "health",
            ["commandId"] = "local-lag-cleaner.health",
            ["args"] = new JsonObject()
        });
        Assert.Equal(
            "request.invalid",
            invalidMethod["result"]!["error"]!["code"]!.GetValue<string>());

        var unknownEnvelopeField = await InvokeRuntimeAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "unknown-envelope-field",
            ["method"] = "executeCommand",
            ["commandId"] = "local-lag-cleaner.health",
            ["args"] = new JsonObject(),
            ["extra"] = true
        });
        Assert.Equal(
            "request.invalid",
            unknownEnvelopeField["result"]!["error"]!["code"]!.GetValue<string>());

        var oversized = await InvokeRuntimeAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "oversized",
            ["method"] = "executeCommand",
            ["commandId"] = "local-lag-cleaner.health",
            ["args"] = new JsonObject(),
            ["padding"] = new string('X', 70_000)
        });
        Assert.Equal(
            "request.invalid",
            oversized["result"]!["error"]!["code"]!.GetValue<string>());

        var runtimeSource = File.ReadAllText(Path.Combine(
            ToolRoot,
            "src",
            "LocalLagCleaner.Runtime",
            "Program.cs"));
        Assert.DoesNotContain("ReadLatestSnapshot", runtimeSource, StringComparison.Ordinal);
        Assert.Contains(
            "await CreatePlanAsync(",
            runtimeSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Stdio_runtime_uses_the_host_command_timeout_and_keeps_explicit_timeout_for_tests()
    {
        var source = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.ModuleHost.StdioCompat",
            "StdioCompatModuleHost.cs"));

        Assert.Contains(
            "ExecuteAsync(module.Entrypoint!, request, Timeout.InfiniteTimeSpan, cancellationToken)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (timeout != Timeout.InfiniteTimeSpan)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ExecuteAsync(module.Entrypoint!, request, TimeSpan.FromSeconds(30), cancellationToken)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stdio_runtime_cancellation_terminates_the_isolated_process()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "mpt-stdio-cancellation",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var processIdPath = Path.Combine(testRoot, "runtime.pid");
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(File.Exists(powershellPath), $"PowerShell is missing: {powershellPath}");
        var escapedProcessIdPath = processIdPath.Replace(
            "'",
            "''",
            StringComparison.Ordinal);
        var script =
            $"[IO.File]::WriteAllText('{escapedProcessIdPath}', [string]$PID); " +
            "Start-Sleep -Seconds 60";
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var entrypoint = new SelectedEntrypoint(
            Kind: "jsonrpc-stdio",
            Priority: 0,
            RuntimeId: null,
            Command: powershellPath,
            Args: ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedScript],
            Assembly: null,
            Type: null,
            Service: null,
            EndpointTransport: null,
            EndpointAddress: null,
            HealthPath: null,
            SelectionReason: "test",
            SelectionDiagnostics: [],
            InProcMaxCallMs: null,
            SidecarReadyTimeoutMs: null,
            SidecarRestartLimit: null,
            SidecarRestartWindowSeconds: null,
            SidecarKillProcessTree: true);
        var runtime = new StdioCompatModuleHost();
        var request = new SdkCommandRequest(
            "stdio-timeout-" + Guid.NewGuid().ToString("N"),
            "test.wait",
            new JsonObject());
        var spawnedProcessId = 0;
        using var cancellation = new CancellationTokenSource();
        var executionTask = runtime.ExecuteAsync(
            entrypoint,
            request,
            Timeout.InfiniteTimeSpan,
            cancellation.Token);

        try
        {
            var startupDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (!File.Exists(processIdPath) &&
                   !executionTask.IsCompleted &&
                   DateTimeOffset.UtcNow < startupDeadline)
            {
                await Task.Delay(50);
            }

            Assert.True(
                File.Exists(processIdPath),
                "The isolated Runtime did not write its process identifier before cancellation.");
            spawnedProcessId = int.Parse(
                await File.ReadAllTextAsync(processIdPath),
                System.Globalization.CultureInfo.InvariantCulture);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => executionTask);
            AssertProcessHasExited(spawnedProcessId);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await executionTask;
            }
            catch (OperationCanceledException)
            {
            }
            EnsureSpawnedTestProcessIsStopped(spawnedProcessId);
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Dotnet_surface_command_contract_preserves_raw_json_rpc_output()
    {
        const string rawResponse =
            """{"jsonrpc":"2.0","id":"surface-1","result":{"state":"ready","payload":{"toolId":"local-lag-cleaner"}}}""";
        var response = new HostProto.CommandExecutionResponse
        {
            State = "succeeded",
            Summary = rawResponse
        };
        var statusText = ShellCommandExecutionService.FormatStatusText(
            "succeeded",
            rawResponse);
        var shellExecution = new ShellCommandExecutionResult(
            statusText,
            response,
            RequiresPermissionPrompt: false);

        var status = ShellWorkspaceController.ToCommandExecutionStatus(shellExecution);
        var surfaceResult = ShellWorkspaceController.ToSurfaceCommandExecutionResult(
            "surface-1",
            "local-lag-cleaner.health",
            status);

        Assert.StartsWith("succeeded: ", status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rawResponse, status.Message, StringComparison.Ordinal);
        Assert.True(status.Message.Length < 120);
        Assert.Equal(rawResponse, status.Output);
        Assert.Equal(rawResponse, surfaceResult.Output);
        Assert.False(
            surfaceResult.Output.StartsWith("succeeded:", StringComparison.OrdinalIgnoreCase));
        var parsed = JsonNode.Parse(surfaceResult.Output)!.AsObject();
        Assert.Equal("2.0", parsed["jsonrpc"]!.GetValue<string>());
        Assert.Equal(
            "local-lag-cleaner",
            parsed["result"]!["payload"]!["toolId"]!.GetValue<string>());
    }

    [Fact]
    public void Trend_analyzer_requires_the_same_boot_and_reports_kernel_growth()
    {
        var baseline = EmptySnapshot() with
        {
            CapturedAtUtc = DateTimeOffset.Parse("2026-07-28T00:00:00Z"),
            UptimeDays = 10,
            KernelTotalBytes = 2UL * 1024 * 1024 * 1024,
            SystemHandleCount = 100_000
        };
        var current = EmptySnapshot() with
        {
            CapturedAtUtc = baseline.CapturedAtUtc.AddHours(2),
            UptimeDays = baseline.UptimeDays + 2d / 24,
            KernelTotalBytes = baseline.KernelTotalBytes + 2UL * 1024 * 1024 * 1024,
            SystemHandleCount = baseline.SystemHandleCount + 80_000
        };

        var analyzed = LagTrendAnalyzer.Apply(current, baseline);

        Assert.True(analyzed.Trend?.Available);
        Assert.Contains(analyzed.Findings, item => item.Code == "kernel-growth-trend");
        Assert.Equal(LagSeverity.Critical, analyzed.OverallSeverity);
    }

    [Fact]
    public void Health_score_caps_domains_and_downweights_correlated_leak_cascade()
    {
        var correlatedFindings = new[]
        {
            new LagFinding(LagSeverity.Critical, "kernel-pool-critical", "内核池", "", "", false, "")
            {
                Domain = DiagnosticDomain.KernelDrivers,
                Score = 35
            },
            new LagFinding(LagSeverity.Critical, "system-handles-critical", "系统句柄", "", "", false, "")
            {
                Domain = DiagnosticDomain.KernelDrivers,
                Score = 30
            },
            new LagFinding(LagSeverity.Critical, "process-count-critical", "进程数", "", "", false, "")
            {
                Domain = DiagnosticDomain.BackgroundProcesses,
                Score = 30
            },
            new LagFinding(LagSeverity.Warning, "mcp-residue", "MCP 残留", "", "", false, "")
            {
                Domain = DiagnosticDomain.BackgroundProcesses,
                Score = 30
            }
        };
        var snapshot = EmptySnapshot() with
        {
            Findings = correlatedFindings
        };

        Assert.Equal(59, snapshot.HealthScore);
        Assert.Equal(
            25,
            snapshot.DomainHealth.Single(
                item => item.Domain == DiagnosticDomain.KernelDrivers).Score);
        Assert.Equal(
            16,
            snapshot.DomainHealth.Single(
                item => item.Domain == DiagnosticDomain.BackgroundProcesses).Score);
    }

    [Fact]
    public void Reports_exclude_command_lines_executable_paths_and_confirmation_material()
    {
        var snapshot = EmptySnapshot();
        var markdown = LagReportWriter.ToMarkdown(snapshot);
        var json = JsonSerializer.Serialize(snapshot, LagCleanerJson.Indented);
        var publicProcessProperties = typeof(LagProcessSnapshot)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("CommandLine", publicProcessProperties, StringComparer.Ordinal);
        Assert.DoesNotContain("ExecutablePath", publicProcessProperties, StringComparer.Ordinal);
        Assert.DoesNotContain("commandLine", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("executablePath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confirmationToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confirmationToken", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", markdown, StringComparison.OrdinalIgnoreCase);
    }

    private static LagDiagnosticSnapshot EmptySnapshot()
    {
        return new LagDiagnosticSnapshot(
            DateTimeOffset.UtcNow,
            1,
            8,
            1,
            64UL * 1024 * 1024 * 1024,
            16UL * 1024 * 1024 * 1024,
            25,
            20UL * 1024 * 1024 * 1024,
            128UL * 1024 * 1024 * 1024,
            15.625,
            0,
            128UL * 1024 * 1024,
            256UL * 1024 * 1024,
            384UL * 1024 * 1024,
            10_000,
            100_000,
            200,
            2_000,
            1,
            [],
            [],
            [],
            [],
            [],
            [],
            [new LagFinding(LagSeverity.Info, "baseline-healthy", "正常", "正常", "保留报告。", false, "")],
            ["保留报告。"]);
    }

    private static async Task<JsonObject> InvokeRuntimeAsync(JsonObject request)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FindRuntimeExecutable(),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        Assert.True(process.Start());
        await process.StandardInput.WriteLineAsync(
            request.ToJsonString(LagCleanerJson.Compact));
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, process.ExitCode);
        Assert.True(
            string.IsNullOrWhiteSpace(error),
            $"Runtime wrote unexpected stderr: {error}");
        return JsonNode.Parse(output)?.AsObject() ??
               throw new InvalidDataException($"Runtime output is invalid JSON: {output}");
    }

    private static void AssertProcessHasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(
                process.WaitForExit(1_000),
                $"The isolated Runtime process {processId} survived cancellation.");
        }
        catch (ArgumentException)
        {
        }
    }

    private static void EnsureSpawnedTestProcessIsStopped(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string FindRuntimeExecutable()
    {
        // Read the configuration MSBuild compiled this assembly with rather than
        // inferring it from the output directory layout, which differs between the
        // classic bin/<config>/<tfm> tree and the artifacts output layout used by src/.
        var configuration = typeof(LocalLagCleanerProductTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ??
            throw new DirectoryNotFoundException("Could not determine the test build configuration.");
        var executableName = OperatingSystem.IsWindows()
            ? "LocalLagCleaner.Runtime.exe"
            : "LocalLagCleaner.Runtime";
        var executablePath = Path.Combine(
            ToolRoot,
            "src",
            "LocalLagCleaner.Runtime",
            "bin",
            configuration,
            "net10.0",
            executableName);
        Assert.True(
            File.Exists(executablePath),
            $"Build output for the LocalLagCleaner Runtime is missing: {executablePath}");
        return executablePath;
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
