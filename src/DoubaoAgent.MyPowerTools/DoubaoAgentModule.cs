using System.Text.Json.Nodes;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;

namespace DoubaoAgent.MyPowerTools;

public sealed class DoubaoAgentModule : IMptModule
{
    private static readonly DoubaoServiceEndpoint[] DefaultEndpoints =
    [
        new("planner", "Agent Planner", "http://127.0.0.1:38102", "/health"),
        new("tool", "Tool Runtime", "http://127.0.0.1:38080", "/health"),
        new("mcp", "MCP Bridge", "http://127.0.0.1:38189", "/health")
    ];

    private readonly HttpClient _httpClient;
    private ModuleContext? _context;

    public DoubaoAgentModule()
        : this(new HttpClient { Timeout = TimeSpan.FromMilliseconds(1200) })
    {
    }

    internal DoubaoAgentModule(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string Id => "doubao-agent";
    public string PackageId => "doubao-agent";
    public Version Version => new(0, 2, 0);

    private ModuleContext Context => _context ?? throw new InvalidOperationException("Doubao Agent was not initialized.");

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        Directory.CreateDirectory(context.DataDirectory);
        Directory.CreateDirectory(context.CacheDirectory);
        Directory.CreateDirectory(context.LogDirectory);
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["lifecycle", "status", "commands", "settings", "logs", "dashboardCard", "detailPage"]));
    }

    public async ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var checks = await CheckServicesAsync(DefaultEndpoints, cancellationToken);
        var running = checks.Count(check => check.Ok);
        var state = running == checks.Count ? "running" : running == 0 ? "degraded" : "degraded";
        var summary = running == checks.Count
            ? "Planner, tool runtime, and MCP bridge are reachable."
            : $"{running}/{checks.Count} Doubao runtime service(s) are reachable.";
        return new ModuleStatusSnapshot(Id, state, summary, DateTimeOffset.UtcNow, checks, 0);
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            Command("doubao-agent.status.summary", "Summarize Doubao runtime status", "Checks planner, tool runtime, and MCP bridge ports"),
            Command("doubao-agent.health.check", "Check all Doubao runtime services", "Queries planner, tool runtime, and MCP bridge health endpoints"),
            Command("doubao-agent.planner.health", "Check Doubao planner", "Queries the planner service on port 38102"),
            Command("doubao-agent.tool.health", "Check Doubao tool runtime", "Queries the tool runtime service on port 38080"),
            Command("doubao-agent.mcp.health", "Check Doubao MCP bridge", "Queries the MCP bridge service on port 38189"),
            Command("doubao-agent.self-test", "Run Doubao controller self-test", "Verifies data, cache, logs, settings, and redaction boundaries"),
            Command("doubao-agent.logs.summary", "Summarize Doubao module logs", "Reports Runner-managed log directory state")
        ];
        return ValueTask.FromResult(commands);
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return request.CommandId switch
        {
            "doubao-agent.status.summary" or "doubao-agent.health.check" => await StatusSummaryAsync(request, cancellationToken),
            "doubao-agent.planner.health" => await SingleServiceAsync(request, "planner", cancellationToken),
            "doubao-agent.tool.health" => await SingleServiceAsync(request, "tool", cancellationToken),
            "doubao-agent.mcp.health" => await SingleServiceAsync(request, "mcp", cancellationToken),
            "doubao-agent.self-test" => SelfTest(request),
            "doubao-agent.logs.summary" => LogsSummary(request),
            _ => Failed(request, MptErrorCodes.NotFound, $"Command '{request.CommandId}' is not implemented by Doubao Agent.")
        };
    }

    public IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, CancellationToken cancellationToken)
    {
        return EmptyAsyncEnumerable.Of<MptModuleEvent>(cancellationToken);
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """
        {
          "type": "object",
          "properties": {
            "plannerBaseUrl": { "type": "string", "default": "http://127.0.0.1:38102" },
            "toolBaseUrl": { "type": "string", "default": "http://127.0.0.1:38080" },
            "mcpBaseUrl": { "type": "string", "default": "http://127.0.0.1:38189" },
            "healthPath": { "type": "string", "default": "/health" },
            "redactSensitiveOutput": { "type": "boolean", "default": true }
          }
        }
        """));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSnapshotDocument(
            Id,
            1,
            new JsonObject
            {
                ["plannerBaseUrl"] = "http://127.0.0.1:38102",
                ["toolBaseUrl"] = "http://127.0.0.1:38080",
                ["mcpBaseUrl"] = "http://127.0.0.1:38189",
                ["healthPath"] = "/health",
                ["redactSensitiveOutput"] = true
            },
            DateTimeOffset.UtcNow));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        foreach (var key in new[] { "plannerBaseUrl", "toolBaseUrl", "mcpBaseUrl" })
        {
            if (patch.Patch.TryGetPropertyValue(key, out var node) && node is not null && !Uri.TryCreate(node.GetValue<string>(), UriKind.Absolute, out _))
            {
                messages.Add($"{key} must be an absolute URL.");
            }
        }

        if (patch.Patch.TryGetPropertyValue("healthPath", out var healthPath) && healthPath is not null)
        {
            var path = healthPath.GetValue<string>();
            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                messages.Add("healthPath must start with '/'.");
            }
        }

        return ValueTask.FromResult(new SettingsValidationResult(
            messages.Count == 0,
            messages,
            messages.Count == 0 ? null : new MptRuntimeError(MptErrorCodes.ValidationFailed, string.Join("; ", messages))));
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new("doubao-agent.dashboard", "dashboard-card", "Doubao Agent", new JsonObject { ["moduleId"] = Id }),
            new("doubao-agent.detail", "detail-page", "Doubao Agent Runtime", new JsonObject { ["moduleId"] = Id }),
            new("doubao-agent.settings", "settings", "Doubao Agent Settings", new JsonObject { ["moduleId"] = Id }),
            new("doubao-agent.logs", "logs", "Doubao Agent Logs", new JsonObject { ["moduleId"] = Id })
        ];
        return ValueTask.FromResult(surfaces);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    private async Task<CommandExecutionResult> StatusSummaryAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var endpoints = ResolveEndpoints(request.Args);
        var checks = await CheckServicesAsync(endpoints, cancellationToken);
        var services = new JsonArray(checks.Select(ToServiceJson).ToArray<JsonNode?>());
        var running = checks.Count(check => check.Ok);
        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["state"] = running == checks.Count ? "running" : "degraded",
            ["runningServices"] = running,
            ["totalServices"] = checks.Count,
            ["services"] = services,
            ["ports"] = new JsonObject
            {
                ["planner"] = ExtractPort(endpoints.First(endpoint => endpoint.Id == "planner").BaseUrl),
                ["tool"] = ExtractPort(endpoints.First(endpoint => endpoint.Id == "tool").BaseUrl),
                ["mcp"] = ExtractPort(endpoints.First(endpoint => endpoint.Id == "mcp").BaseUrl)
            }
        };

        return Succeeded(request, payload.ToJsonString());
    }

    private async Task<CommandExecutionResult> SingleServiceAsync(CommandRequest request, string serviceId, CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoints(request.Args).First(endpoint => endpoint.Id == serviceId);
        var check = await CheckServiceAsync(endpoint, cancellationToken);
        var payload = ToServiceJson(check);
        return check.Ok
            ? Succeeded(request, payload.ToJsonString())
            : Failed(request, MptErrorCodes.RuntimeUnavailable, check.Message, retryable: true, details: payload);
    }

    private CommandExecutionResult SelfTest(CommandRequest request)
    {
        var endpoints = ResolveEndpoints(request.Args);
        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["settingsSchema"] = "available",
            ["dataDirectory"] = RedactPath(Context.DataDirectory),
            ["cacheDirectory"] = RedactPath(Context.CacheDirectory),
            ["logDirectory"] = RedactPath(Context.LogDirectory),
            ["redaction"] = LogRouter.Redact("token=abc123 secret=hidden password=hunter2"),
            ["services"] = new JsonArray(endpoints.Select(endpoint => new JsonObject
            {
                ["id"] = endpoint.Id,
                ["label"] = endpoint.Label,
                ["baseUrl"] = LogRouter.Redact(endpoint.BaseUrl),
                ["healthPath"] = endpoint.HealthPath
            }).ToArray<JsonNode?>())
        };

        File.AppendAllText(Path.Combine(Context.LogDirectory, "doubao-agent.log"), $"{DateTimeOffset.UtcNow:O} self-test completed{Environment.NewLine}");
        return Succeeded(request, payload.ToJsonString());
    }

    private CommandExecutionResult LogsSummary(CommandRequest request)
    {
        var directory = new DirectoryInfo(Context.LogDirectory);
        var files = directory.Exists ? directory.GetFiles("*.log") : [];
        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["logDirectory"] = RedactPath(Context.LogDirectory),
            ["fileCount"] = files.Length,
            ["files"] = new JsonArray(files.Select(file => new JsonObject
            {
                ["name"] = file.Name,
                ["length"] = file.Length,
                ["updatedAt"] = file.LastWriteTimeUtc
            }).ToArray<JsonNode?>())
        };
        return Succeeded(request, payload.ToJsonString());
    }

    private async Task<IReadOnlyList<HealthCheckSnapshot>> CheckServicesAsync(IReadOnlyList<DoubaoServiceEndpoint> endpoints, CancellationToken cancellationToken)
    {
        var checks = new List<HealthCheckSnapshot>();
        foreach (var endpoint in endpoints)
        {
            checks.Add(await CheckServiceAsync(endpoint, cancellationToken));
        }

        return checks;
    }

    private async Task<HealthCheckSnapshot> CheckServiceAsync(DoubaoServiceEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1000));
            var uri = new Uri(new Uri(endpoint.BaseUrl.TrimEnd('/') + "/"), endpoint.HealthPath.TrimStart('/'));
            using var response = await _httpClient.GetAsync(uri, timeout.Token);
            var body = LogRouter.Redact(await response.Content.ReadAsStringAsync(timeout.Token));
            var message = response.IsSuccessStatusCode
                ? $"HTTP {(int)response.StatusCode}: {Trim(body)}"
                : $"HTTP {(int)response.StatusCode}: {Trim(body)}";
            return new HealthCheckSnapshot($"doubao.{endpoint.Id}", endpoint.Label, response.IsSuccessStatusCode, message);
        }
        catch (OperationCanceledException)
        {
            return new HealthCheckSnapshot($"doubao.{endpoint.Id}", endpoint.Label, false, $"Timed out while checking {endpoint.BaseUrl}.");
        }
        catch (Exception ex)
        {
            return new HealthCheckSnapshot($"doubao.{endpoint.Id}", endpoint.Label, false, LogRouter.Redact(ex.Message));
        }
    }

    private DoubaoServiceEndpoint[] ResolveEndpoints(JsonObject args)
    {
        var healthPath = ReadString(args, "healthPath") ?? "/health";
        return
        [
            new("planner", "Agent Planner", ReadString(args, "plannerBaseUrl") ?? DefaultEndpoints[0].BaseUrl, healthPath),
            new("tool", "Tool Runtime", ReadString(args, "toolBaseUrl") ?? DefaultEndpoints[1].BaseUrl, healthPath),
            new("mcp", "MCP Bridge", ReadString(args, "mcpBaseUrl") ?? DefaultEndpoints[2].BaseUrl, healthPath)
        ];
    }

    private static JsonObject ToServiceJson(HealthCheckSnapshot check)
    {
        var id = check.Id.StartsWith("doubao.", StringComparison.Ordinal) ? check.Id["doubao.".Length..] : check.Id;
        return new JsonObject
        {
            ["id"] = id,
            ["label"] = check.Label,
            ["ok"] = check.Ok,
            ["message"] = check.Message
        };
    }

    private static MptCommandDescriptor Command(string id, string title, string subtitle)
    {
        return new MptCommandDescriptor(
            id,
            "doubao-agent",
            title,
            subtitle,
            "action",
            Category: "Doubao Agent",
            TimeoutMs: 8000,
            Execution: new JsonObject { ["type"] = "module.execute" });
    }

    private static CommandExecutionResult Succeeded(CommandRequest request, string output)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
    }

    private static CommandExecutionResult Failed(CommandRequest request, string code, string message, bool retryable = false, JsonObject? details = null)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "failed", false, "", new MptRuntimeError(code, message, retryable, details));
    }

    private static string? ReadString(JsonObject args, string name)
    {
        return args.TryGetPropertyValue(name, out var node) && node is not null ? node.GetValue<string>() : null;
    }

    private static int ExtractPort(string baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Port : 0;
    }

    private static string RedactPath(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local)
            ? path
            : path.Replace(local, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 300 ? value : value[..300] + "...";
    }

    private sealed record DoubaoServiceEndpoint(string Id, string Label, string BaseUrl, string HealthPath);
}
