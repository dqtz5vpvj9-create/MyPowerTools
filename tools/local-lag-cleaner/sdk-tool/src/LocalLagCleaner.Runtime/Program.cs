using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalLagCleaner.MyPowerTools;
using LocalLagCleaner.Runtime;

return await LocalLagCleanerRuntime.RunAsync();

internal static class LocalLagCleanerRuntime
{
    private const string ToolId = "local-lag-cleaner";
    private const int MaximumRequestCharacters = 64 * 1024;
    private const int MaximumIdentifierCharacters = 128;
    private static readonly HashSet<string> RequestProperties =
        ["jsonrpc", "id", "method", "commandId", "args"];
    private static readonly HashSet<string> Commands =
    [
        "local-lag-cleaner.health",
        "local-lag-cleaner.scan.quick",
        "local-lag-cleaner.scan.deep",
        "local-lag-cleaner.scan.file-handles-elevated",
        "local-lag-cleaner.plan.mcp",
        "local-lag-cleaner.plan.weflow",
        "local-lag-cleaner.plan.delivery-optimization",
        "local-lag-cleaner.plan.nvidia-container",
        "local-lag-cleaner.plan.remote-desktop",
        "local-lag-cleaner.plan.windows-search",
        "local-lag-cleaner.cleanup.pending",
        "local-lag-cleaner.cleanup.apply"
    ];
    private static readonly HashSet<string> ApplyProperties =
    [
        "planId",
        "expectedAction",
        "confirmationToken",
        "allowDisconnect",
        "allowServiceRestart"
    ];

    public static async Task<int> RunAsync()
    {
        var requestId = "invalid";
        var commandId = "";
        try
        {
            var line = await ReadBoundedRequestAsync(Console.In).ConfigureAwait(false);
            var request = ParseRequest(line);
            requestId = request.RequestId;
            commandId = request.CommandId;
            ValidateArguments(commandId, request.Arguments);
            var payload = await ExecuteAsync(commandId, request.Arguments).ConfigureAwait(false);
            WriteResponse(requestId, "ready", payload, errorCode: null, errorMessage: null);
        }
        catch (Exception exception)
        {
            var error = MapError(exception, commandId);
            WriteResponse(requestId, "failed", null, error.Code, error.Message);
        }

        return 0;
    }

    private static async Task<string> ReadBoundedRequestAsync(TextReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    return builder.ToString().TrimEnd('\r');
                }

                if (builder.Length >= MaximumRequestCharacters)
                {
                    throw new RuntimeProtocolException(
                        "request.invalid",
                        $"JSON-RPC 请求超过 {MaximumRequestCharacters} 个字符上限。");
                }

                builder.Append(character);
            }
        }

        if (builder.Length == 0)
        {
            throw new RuntimeProtocolException("request.invalid", "缺少 JSON-RPC 请求。");
        }

        return builder.ToString().TrimEnd('\r');
    }

    private static RuntimeRequest ParseRequest(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                line,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
        }
        catch (JsonException exception)
        {
            throw new RuntimeProtocolException(
                "request.invalid",
                $"JSON-RPC 请求无法解析：{exception.Message}",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new RuntimeProtocolException(
                    "request.invalid",
                    "JSON-RPC 请求根节点必须为对象。");
            }

            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!observed.Add(property.Name))
                {
                    throw new RuntimeProtocolException(
                        "request.invalid",
                        $"JSON-RPC 请求包含重复字段 '{property.Name}'。");
                }

                if (!RequestProperties.Contains(property.Name))
                {
                    throw new RuntimeProtocolException(
                        "request.invalid",
                        $"JSON-RPC 请求包含未知字段 '{property.Name}'。");
                }
            }

            if (!TryReadBoundedString(root, "jsonrpc", 8, out var jsonRpc) ||
                !string.Equals(jsonRpc, "2.0", StringComparison.Ordinal))
            {
                throw new RuntimeProtocolException(
                    "request.invalid",
                    "jsonrpc 必须为字符串 '2.0'。");
            }

            if (!TryReadBoundedString(root, "method", 64, out var method) ||
                !string.Equals(method, "executeCommand", StringComparison.Ordinal))
            {
                throw new RuntimeProtocolException(
                    "request.invalid",
                    "method 必须为字符串 'executeCommand'。");
            }

            if (!TryReadBoundedString(
                    root,
                    "id",
                    MaximumIdentifierCharacters,
                    out var requestId) ||
                string.IsNullOrWhiteSpace(requestId))
            {
                throw new RuntimeProtocolException(
                    "request.invalid",
                    "id 必须为 1 至 128 个字符的字符串。");
            }

            if (!TryReadBoundedString(
                    root,
                    "commandId",
                    MaximumIdentifierCharacters,
                    out var commandId) ||
                string.IsNullOrWhiteSpace(commandId))
            {
                throw new RuntimeProtocolException(
                    "request.invalid",
                    "commandId 必须为 1 至 128 个字符的字符串。");
            }

            if (!root.TryGetProperty("args", out var argumentsElement) ||
                argumentsElement.ValueKind != JsonValueKind.Object)
            {
                throw new RuntimeProtocolException(
                    "request.invalid",
                    "args 必须为 JSON 对象。");
            }

            var argumentNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in argumentsElement.EnumerateObject())
            {
                if (!argumentNames.Add(property.Name))
                {
                    throw new RuntimeProtocolException(
                        "request.invalid",
                        $"args 包含重复字段 '{property.Name}'。");
                }
            }

            var arguments = JsonNode.Parse(argumentsElement.GetRawText())?.AsObject() ??
                            throw new RuntimeProtocolException(
                                "request.invalid",
                                "args 无法解析为 JSON 对象。");
            return new RuntimeRequest(requestId, commandId, arguments);
        }
    }

    private static bool TryReadBoundedString(
        JsonElement root,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = "";
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? "";
        return value.Length <= maximumLength;
    }

    private static void ValidateArguments(string commandId, JsonObject arguments)
    {
        if (!Commands.Contains(commandId))
        {
            throw new RuntimeProtocolException(
                "command.not-found",
                $"未知命令 '{commandId}'。");
        }

        if (!string.Equals(
                commandId,
                "local-lag-cleaner.cleanup.apply",
                StringComparison.Ordinal))
        {
            if (arguments.Count != 0)
            {
                throw new RuntimeProtocolException(
                    "validation.failed",
                    $"命令 '{commandId}' 不接受参数。");
            }

            return;
        }

        foreach (var property in arguments)
        {
            if (!ApplyProperties.Contains(property.Key))
            {
                throw new RuntimeProtocolException(
                    "validation.failed",
                    $"cleanup.apply 包含未知参数 '{property.Key}'。");
            }
        }

        var planId = ReadRequiredArgumentString(arguments, "planId", 32);
        if (planId.Length != 32 || !planId.All(Uri.IsHexDigit))
        {
            throw new RuntimeProtocolException(
                "validation.failed",
                "planId 必须为 32 位十六进制字符串。");
        }

        _ = ReadExpectedAction(arguments);
        var token = ReadRequiredArgumentString(arguments, "confirmationToken", 8);
        if (token.Length != 8 || !token.All(Uri.IsHexDigit))
        {
            throw new RuntimeProtocolException(
                "validation.failed",
                "confirmationToken 必须为 8 位十六进制字符串。");
        }

        ValidateOptionalBoolean(arguments, "allowDisconnect");
        ValidateOptionalBoolean(arguments, "allowServiceRestart");
    }

    private static string ReadRequiredArgumentString(
        JsonObject arguments,
        string name,
        int maximumLength)
    {
        if (arguments[name] is not JsonValue value ||
            !value.TryGetValue<string>(out var result) ||
            string.IsNullOrWhiteSpace(result) ||
            result.Length > maximumLength)
        {
            throw new RuntimeProtocolException(
                "validation.failed",
                $"{name} 必须为长度不超过 {maximumLength} 的非空字符串。");
        }

        return result;
    }

    private static CleanupAction ReadExpectedAction(JsonObject arguments)
    {
        if (arguments["expectedAction"] is null)
        {
            throw new RuntimeProtocolException(
                "validation.failed",
                "expectedAction 为必填参数。");
        }

        try
        {
            var action = arguments["expectedAction"]!.Deserialize<CleanupAction>(
                LagCleanerJson.Compact);
            if (!Enum.IsDefined(action))
            {
                throw new JsonException("枚举值超出范围。");
            }

            return action;
        }
        catch (JsonException exception)
        {
            throw new RuntimeProtocolException(
                "validation.failed",
                "expectedAction 必须为支持的字符串动作值。",
                exception);
        }
    }

    private static void ValidateOptionalBoolean(JsonObject arguments, string name)
    {
        if (arguments[name] is null)
        {
            return;
        }

        if (arguments[name] is not JsonValue value ||
            !value.TryGetValue<bool>(out _))
        {
            throw new RuntimeProtocolException(
                "validation.failed",
                $"{name} 必须为布尔值。");
        }
    }

    private static async Task<JsonNode?> ExecuteAsync(
        string commandId,
        JsonObject arguments)
    {
        var dataDirectory = ResolveDataDirectory();
        var reportsDirectory = Path.Combine(dataDirectory, "reports");
        var cleanup = new CleanupCoordinator(dataDirectory);

        return commandId switch
        {
            "local-lag-cleaner.health" => new JsonObject
            {
                ["toolId"] = ToolId,
                ["state"] = OperatingSystem.IsWindows() ? "ready" : "unsupported",
                ["runtimeVersion"] =
                    typeof(LocalLagCleanerRuntime).Assembly.GetName().Version?.ToString() ?? "unknown",
                ["time"] = DateTimeOffset.UtcNow
            },
            "local-lag-cleaner.scan.quick" =>
                await ScanAsync(LoadOptions(deep: false), reportsDirectory).ConfigureAwait(false),
            "local-lag-cleaner.scan.deep" =>
                await ScanAsync(LoadOptions(deep: true), reportsDirectory).ConfigureAwait(false),
            "local-lag-cleaner.scan.file-handles-elevated" =>
                await ScanElevatedFileHandlesAsync(reportsDirectory).ConfigureAwait(false),
            "local-lag-cleaner.plan.mcp" =>
                await CreatePlanAsync(
                    cleanup,
                    CleanupAction.McpResidue,
                    reportsDirectory).ConfigureAwait(false),
            "local-lag-cleaner.plan.weflow" =>
                await CreatePlanAsync(
                    cleanup,
                    CleanupAction.WeFlow,
                    reportsDirectory).ConfigureAwait(false),
            "local-lag-cleaner.plan.delivery-optimization" =>
                await CreatePlanAsync(
                    cleanup,
                    CleanupAction.DeliveryOptimization,
                    reportsDirectory).ConfigureAwait(false),
            "local-lag-cleaner.plan.nvidia-container" =>
                await CreatePlanAsync(
                    cleanup,
                    CleanupAction.NvidiaContainer,
                    reportsDirectory).ConfigureAwait(false),
            "local-lag-cleaner.plan.remote-desktop" =>
                await CreatePlanAsync(
                    cleanup,
                    CleanupAction.RemoteDesktop,
                    reportsDirectory).ConfigureAwait(false),
            "local-lag-cleaner.plan.windows-search" =>
                await CreatePlanAsync(
                    cleanup,
                    CleanupAction.WindowsSearch,
                    reportsDirectory).ConfigureAwait(false),
            "local-lag-cleaner.cleanup.pending" =>
                JsonSerializer.SerializeToNode(
                    cleanup.TryReadPendingPlan(),
                    LagCleanerJson.Compact),
            "local-lag-cleaner.cleanup.apply" =>
                await ApplyAsync(cleanup, arguments).ConfigureAwait(false),
            _ => throw new RuntimeProtocolException(
                "command.not-found",
                $"未知命令 '{commandId}'。")
        };
    }

    private static async Task<JsonNode?> ScanAsync(
        LagCleanerOptions options,
        string reportsDirectory)
    {
        var history = await ReadHistoryAsync(reportsDirectory).ConfigureAwait(false);
        var engine = new LagDiagnosticsEngine();
        var scanned = await engine.ScanAsync(options).ConfigureAwait(false);
        var snapshot = LagTrendAnalyzer.ApplyHistory(scanned, history);
        var paths = await LagReportWriter.WriteAsync(snapshot, reportsDirectory)
            .ConfigureAwait(false);
        return new JsonObject
        {
            ["snapshot"] = JsonSerializer.SerializeToNode(snapshot, LagCleanerJson.Compact),
            ["jsonReportPath"] = paths.JsonPath,
            ["markdownReportPath"] = paths.MarkdownPath,
            ["historyJsonPath"] = paths.HistoryJsonPath
        };
    }

    private static async Task<JsonNode?> ScanElevatedFileHandlesAsync(
        string reportsDirectory)
    {
        var latestPath = Path.Combine(reportsDirectory, "latest.json");
        if (!File.Exists(latestPath))
        {
            throw new RuntimeProtocolException(
                "diagnostic.baseline-required",
                "请先完成一次快速或深度扫描，再执行管理员 File 归因。");
        }

        var snapshot = JsonSerializer.Deserialize<LagDiagnosticSnapshot>(
                           await File.ReadAllTextAsync(latestPath).ConfigureAwait(false),
                           LagCleanerJson.Compact) ??
                       throw new RuntimeProtocolException(
                           "diagnostic.baseline-invalid",
                           "现有诊断快照无法解析。");
        var fileType = snapshot.SystemHandleTypes.FirstOrDefault(
            item => string.Equals(
                item.TypeName,
                "File",
                StringComparison.OrdinalIgnoreCase));
        if (fileType is null || fileType.SystemHandleCount == 0)
        {
            throw new RuntimeProtocolException(
                "diagnostic.file-handles-unavailable",
                "现有诊断快照没有 PID 4 File 句柄类型索引。");
        }

        var elevated = await ElevatedFileHandleDiagnosticClient.RunAsync(
                fileType.ObjectTypeIndex,
                fileType.SystemHandleCount,
                maximumSamples: 512)
            .ConfigureAwait(false);
        var findings = snapshot.Findings
            .Where(item =>
                !string.Equals(
                    item.Code,
                    "system-file-path-attribution-permission",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    item.Code,
                    "system-file-path-attribution",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    item.Code,
                    "system-file-filter-instances",
                    StringComparison.Ordinal))
            .ToList();
        findings.Add(new LagFinding(
            LagSeverity.Info,
            "system-file-path-attribution",
            elevated.Attribution.RequiresKernelDriver
                ? "管理员 Broker 已确认 PID 4 内核访问边界"
                : "管理员 Broker 已解析 System File 路径来源",
            elevated.PathGroups.Count == 0
                ? elevated.Attribution.Summary
                : string.Join(
                    "；",
                    elevated.PathGroups
                        .Take(10)
                        .Select(item =>
                            $"{item.PathGroup} [{item.FileKind}] {item.SampleCount}（{item.SampleSharePercent:n1}%）")),
            elevated.Attribution.RequiresKernelDriver
                ? "现存 PID 4 句柄的单体路径需要受信任内核驱动或内核调试器；继续使用卷实例、Pool Tag 与 Minifilter ETW 定位创建链。"
                : "将占比最高的卷和目录与过滤器实例、容器状态及文件 I/O 事件对照。",
            false,
            "")
        {
            Domain = DiagnosticDomain.KernelDrivers,
            Confidence = elevated.Attribution.ResolvedPathSamples > 0
                ? FindingConfidence.High
                : FindingConfidence.Medium,
            Score = 0,
            RemediationRisk = RemediationRisk.ReadOnly,
            CausalChain = elevated.Attribution.RequiresKernelDriver
                ? "管理员 Broker 启用 SeDebugPrivilege → Windows 仍拒绝 PID 4 PROCESS_DUP_HANDLE → 确认需要内核级现存句柄解析"
                : "管理员 Broker 启用 SeDebugPrivilege → 均匀抽样 PID 4 File 句柄 → DuplicateHandle → 按打开路径和设备类型聚合"
        });
        if (elevated.FilterInstances.Count > 0)
        {
            findings.Add(new LagFinding(
                LagSeverity.Info,
                "system-file-filter-instances",
                "管理员 Broker 已枚举卷上的 minifilter 实例",
                string.Join(
                    "；",
                    elevated.FilterInstances
                        .GroupBy(
                            item => item.FilterName,
                            StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(group => group.Count())
                        .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                        .Take(12)
                        .Select(group =>
                            $"{group.Key}：{group.Count()} 个卷实例（{string.Join(", ", group.Select(item => item.VolumeName).Distinct(StringComparer.OrdinalIgnoreCase).Take(4))}）")),
                "将卷实例与 WC*/FM* Pool Tag、容器运行状态和后续 Minifilter ETW 采样对照。",
                false,
                "")
            {
                Domain = DiagnosticDomain.KernelDrivers,
                Confidence = FindingConfidence.High,
                Score = 0,
                RemediationRisk = RemediationRisk.ReadOnly,
                CausalChain = "管理员 Broker → fltmc instances → 按过滤器、卷、Altitude 和实例名建立实际挂载关系"
            });
        }

        var coverage = snapshot.Coverage
            .Where(item => !string.Equals(
                item.Probe,
                "system-file-handle-paths",
                StringComparison.OrdinalIgnoreCase))
            .Append(new ProbeCoverage(
                "system-file-handle-paths",
                elevated.Attribution.ResolvedPathSamples > 0
                    ? MeasurementStatus.Available
                    : MeasurementStatus.Partial,
                elevated.Attribution.Summary))
            .ToArray();
        var updated = snapshot with
        {
            Findings = findings,
            Coverage = coverage,
            SystemFileAttribution = elevated.Attribution,
            SystemFilePathGroups = elevated.PathGroups,
            FileSystemFilterInstances = elevated.FilterInstances
        };
        var paths = await LagReportWriter.WriteAsync(
                updated,
                reportsDirectory)
            .ConfigureAwait(false);
        return new JsonObject
        {
            ["snapshot"] = JsonSerializer.SerializeToNode(
                updated,
                LagCleanerJson.Compact),
            ["jsonReportPath"] = paths.JsonPath,
            ["markdownReportPath"] = paths.MarkdownPath,
            ["historyJsonPath"] = paths.HistoryJsonPath,
            ["debugPrivilegeEnabled"] = elevated.DebugPrivilegeEnabled
        };
    }

    private static async Task<IReadOnlyList<LagDiagnosticSnapshot>> ReadHistoryAsync(
        string reportsDirectory)
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
                    await File.ReadAllTextAsync(path).ConfigureAwait(false),
                    LagCleanerJson.Compact);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // A damaged or concurrently replaced history item cannot invalidate the scan.
            }
        }

        return snapshots;
    }

    private static async Task<JsonNode?> CreatePlanAsync(
        CleanupCoordinator cleanup,
        CleanupAction action,
        string reportsDirectory)
    {
        var scanPayload = (await ScanAsync(
                    LoadOptions(deep: false),
                    reportsDirectory)
                .ConfigureAwait(false)) as JsonObject ??
            throw new InvalidOperationException("快速扫描未返回计划输入。");
        var snapshot = scanPayload["snapshot"]?.Deserialize<LagDiagnosticSnapshot>(
                           LagCleanerJson.Compact) ??
                       throw new InvalidOperationException("快速扫描结果无法读取。");
        var plan = cleanup.CreatePlan(action, snapshot);
        return JsonSerializer.SerializeToNode(plan, LagCleanerJson.Compact);
    }

    private static async Task<JsonNode?> ApplyAsync(
        CleanupCoordinator cleanup,
        JsonObject arguments)
    {
        var planId = ReadRequiredArgumentString(arguments, "planId", 32);
        var expectedAction = ReadExpectedAction(arguments);
        var token = ReadRequiredArgumentString(arguments, "confirmationToken", 8);
        var allowDisconnect = ReadOptionalBoolean(arguments, "allowDisconnect");
        var allowServiceRestart = ReadOptionalBoolean(arguments, "allowServiceRestart");
        var plan = cleanup.TryReadPendingPlan() ??
                   throw new InvalidOperationException("没有待执行的清理计划，请先生成计划。");
        if (plan.RequiresAdministrator)
        {
            var elevatedResult = await ElevatedCleanupClient.RunAsync(
                    cleanup.StateDirectory,
                    planId,
                    expectedAction,
                    token,
                    allowDisconnect,
                    allowServiceRestart)
                .ConfigureAwait(false);
            return JsonSerializer.SerializeToNode(elevatedResult, LagCleanerJson.Compact);
        }

        var result = await cleanup.ApplyPendingPlanAsync(
                planId,
                expectedAction,
                token,
                allowDisconnect,
                allowServiceRestart)
            .ConfigureAwait(false);
        return JsonSerializer.SerializeToNode(result, LagCleanerJson.Compact);
    }

    private static bool ReadOptionalBoolean(JsonObject arguments, string name)
    {
        return arguments[name] is JsonValue value &&
               value.TryGetValue<bool>(out var result) &&
               result;
    }

    private static string ResolveDataDirectory()
    {
        var dataRoot = Environment.GetEnvironmentVariable("MPT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyPowerTools");
        }

        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot));
        var dataDirectory = Path.GetFullPath(Path.Combine(root, "state", "tools", ToolId));
        var relative = Path.GetRelativePath(root, dataDirectory);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("Tool data directory escaped the MPT data root.");
        }

        Directory.CreateDirectory(dataDirectory);
        return dataDirectory;
    }

    private static LagCleanerOptions LoadOptions(bool deep)
    {
        var defaults = new LagCleanerOptions
        {
            SampleSeconds = deep ? 15 : 5
        };
        var toolRoot = FindToolRoot();
        if (toolRoot is null)
        {
            return defaults.Normalize();
        }

        var settingsPath = Path.Combine(toolRoot, "settings.json");
        if (!File.Exists(settingsPath))
        {
            return defaults.Normalize();
        }

        try
        {
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject();
            if (settings is null)
            {
                return defaults.Normalize();
            }

            return (defaults with
            {
                SampleSeconds = ReadInt(
                    settings,
                    deep ? "deepSampleSeconds" : "quickSampleSeconds",
                    defaults.SampleSeconds),
                SampleIntervalMilliseconds = ReadInt(
                    settings,
                    "sampleIntervalMilliseconds",
                    defaults.SampleIntervalMilliseconds),
                StaleMcpMinutes = ReadInt(
                    settings,
                    "staleMcpMinutes",
                    defaults.StaleMcpMinutes),
                PreserveNewestMcpGroups = ReadInt(
                    settings,
                    "preserveNewestMcpGroups",
                    defaults.PreserveNewestMcpGroups),
                IdleMcpCorePercentThreshold = ReadDouble(
                    settings,
                    "idleMcpCorePercentThreshold",
                    defaults.IdleMcpCorePercentThreshold),
                WeFlowCorePercentThreshold = ReadDouble(
                    settings,
                    "weFlowCorePercentThreshold",
                    defaults.WeFlowCorePercentThreshold),
                CpuSustainedWarningPercent = ReadDouble(
                    settings,
                    "cpuSustainedWarningPercent",
                    defaults.CpuSustainedWarningPercent),
                CpuSustainedCriticalPercent = ReadDouble(
                    settings,
                    "cpuSustainedCriticalPercent",
                    defaults.CpuSustainedCriticalPercent),
                DpcInterruptWarningPercent = ReadDouble(
                    settings,
                    "dpcInterruptWarningPercent",
                    defaults.DpcInterruptWarningPercent),
                DiskLatencyWarningMilliseconds = ReadDouble(
                    settings,
                    "diskLatencyWarningMilliseconds",
                    defaults.DiskLatencyWarningMilliseconds),
                DiskLatencyCriticalMilliseconds = ReadDouble(
                    settings,
                    "diskLatencyCriticalMilliseconds",
                    defaults.DiskLatencyCriticalMilliseconds),
                HardPagingWarningPagesPerSecond = ReadDouble(
                    settings,
                    "hardPagingWarningPagesPerSecond",
                    defaults.HardPagingWarningPagesPerSecond),
                SystemDriveFreeWarningPercent = ReadDouble(
                    settings,
                    "systemDriveFreeWarningPercent",
                    defaults.SystemDriveFreeWarningPercent),
                PagedPoolWarningBytes = ReadMiB(
                    settings,
                    "pagedPoolWarningMiB",
                    defaults.PagedPoolWarningBytes),
                NonPagedPoolWarningBytes = ReadMiB(
                    settings,
                    "nonPagedPoolWarningMiB",
                    defaults.NonPagedPoolWarningBytes),
                KernelPoolCriticalBytes = ReadMiB(
                    settings,
                    "kernelPoolCriticalMiB",
                    defaults.KernelPoolCriticalBytes),
                SystemHandleWarningCount = ReadUInt32(
                    settings,
                    "systemHandleWarningCount",
                    defaults.SystemHandleWarningCount),
                SystemHandleCriticalCount = ReadUInt32(
                    settings,
                    "systemHandleCriticalCount",
                    defaults.SystemHandleCriticalCount),
                ProcessWarningCount = ReadUInt32(
                    settings,
                    "processWarningCount",
                    defaults.ProcessWarningCount),
                ProcessCriticalCount = ReadUInt32(
                    settings,
                    "processCriticalCount",
                    defaults.ProcessCriticalCount)
            }).Normalize();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return defaults.Normalize();
        }
    }

    private static string? FindToolRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tool.json")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        return null;
    }

    private static int ReadInt(JsonObject settings, string name, int fallback)
    {
        return settings[name] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : fallback;
    }

    private static double ReadDouble(JsonObject settings, string name, double fallback)
    {
        return settings[name] is JsonValue value && value.TryGetValue<double>(out var result)
            ? result
            : fallback;
    }

    private static uint ReadUInt32(JsonObject settings, string name, uint fallback)
    {
        return settings[name] is JsonValue value && value.TryGetValue<uint>(out var result)
            ? result
            : fallback;
    }

    private static ulong ReadMiB(JsonObject settings, string name, ulong fallback)
    {
        return settings[name] is JsonValue value &&
               value.TryGetValue<ulong>(out var result) &&
               result <= ulong.MaxValue / (1024UL * 1024)
            ? result * 1024UL * 1024
            : fallback;
    }

    private static RuntimeError MapError(Exception exception, string commandId)
    {
        if (exception is RuntimeProtocolException protocol)
        {
            return new RuntimeError(protocol.Code, protocol.Message);
        }

        if (exception is OperationCanceledException or TimeoutException)
        {
            return new RuntimeError("operation.timeout", exception.Message);
        }

        if (exception is Win32Exception)
        {
            return new RuntimeError("windows.error", exception.Message);
        }

        if (exception is IOException)
        {
            return new RuntimeError("io.failed", exception.Message);
        }

        if (exception is UnauthorizedAccessException)
        {
            var code = commandId == "local-lag-cleaner.cleanup.apply" &&
                       exception.Message.Contains("令牌", StringComparison.Ordinal)
                ? "plan.rejected"
                : "permission.required";
            return new RuntimeError(code, exception.Message);
        }

        if (exception is JsonException or ArgumentException)
        {
            return new RuntimeError("validation.failed", exception.Message);
        }

        if (exception is InvalidOperationException &&
            (commandId.StartsWith("local-lag-cleaner.plan.", StringComparison.Ordinal) ||
             commandId == "local-lag-cleaner.cleanup.apply"))
        {
            return new RuntimeError("plan.rejected", exception.Message);
        }

        return new RuntimeError("runtime.failed", exception.Message);
    }

    private static void WriteResponse(
        string requestId,
        string state,
        JsonNode? payload,
        string? errorCode,
        string? errorMessage)
    {
        var error = errorCode is null
            ? null
            : new JsonObject
            {
                ["code"] = errorCode,
                ["message"] = errorMessage ?? ""
            };
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["result"] = new JsonObject
            {
                ["state"] = state,
                ["payload"] = payload,
                ["error"] = error
            }
        };
        Console.Out.WriteLine(response.ToJsonString(LagCleanerJson.Compact));
    }

    private sealed record RuntimeRequest(
        string RequestId,
        string CommandId,
        JsonObject Arguments);

    private sealed record RuntimeError(string Code, string Message);

    private sealed class RuntimeProtocolException : Exception
    {
        public RuntimeProtocolException(string code, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
