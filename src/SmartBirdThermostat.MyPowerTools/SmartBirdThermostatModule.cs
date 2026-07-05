using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Protocol;
using MyPowerTools.Abstractions;

namespace SmartBirdThermostat.MyPowerTools;

public sealed class SmartBirdThermostatModule : IMptModule
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly HttpClient _httpClient;
    private ModuleContext? _context;
    private SmartBirdStore? _store;

    public SmartBirdThermostatModule()
        : this(new HttpClient { Timeout = TimeSpan.FromMilliseconds(1400) })
    {
    }

    internal SmartBirdThermostatModule(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string Id => "smartbird-thermostat";
    public string PackageId => "smartbird-thermostat";
    public Version Version => new(0, 2, 0);

    private ModuleContext Context => _context ?? throw new InvalidOperationException("SmartBird Thermostat was not initialized.");
    private SmartBirdStore Store => _store ?? throw new InvalidOperationException("SmartBird Thermostat was not initialized.");

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        Directory.CreateDirectory(context.DataDirectory);
        Directory.CreateDirectory(context.CacheDirectory);
        Directory.CreateDirectory(context.LogDirectory);
        _store = new SmartBirdStore(Path.Combine(context.DataDirectory, "smartbird-settings.json"));
        Store.EnsureDefaults();
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["lifecycle", "status", "commands", "settings", "logs", "dashboardCard", "detailPage", "notifications"]));
    }

    public async ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var options = Store.Load();
        var payload = await BuildStatusPayloadAsync(options, cancellationToken);
        var checks = ReadChecks(payload);
        var requiredOk = checks.Where(check => IsRequired(check.Id)).All(check => check.Ok);
        var state = requiredOk ? "running" : "degraded";
        var summary = requiredOk
            ? "SmartBird service and required hardware dependencies are reachable."
            : SummarizeDegraded(checks);
        return new ModuleStatusSnapshot(Id, state, summary, DateTimeOffset.UtcNow, checks, 0);
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        var facadeParameters = FacadeParameters();
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            Command("smartbird-thermostat.status.fetch", "Fetch thermostat status", "Queries status and dependency health through the SmartBird facade", parameters: facadeParameters),
            Command("smartbird-thermostat.status.summary", "Summarize thermostat status", "Reports SmartBird service, Energy Server, FNB-58, and ADB readiness", parameters: facadeParameters),
            Command("smartbird-thermostat.events.list", "List thermostat events", "Reads recent thermostat events or returns an actionable degraded event source", parameters: facadeParameters),
            Command("smartbird-thermostat.logs.summary", "Summarize thermostat logs", "Combines Runner-managed module logs with service log diagnostics", parameters: facadeParameters),
            Command("smartbird-thermostat.config.get", "Read thermostat config", "Reads local module policy plus optional service config", parameters: facadeParameters),
            Command("smartbird-thermostat.config.save", "Save thermostat config", "Validates and persists SmartBird facade policy settings", parameters: ConfigSaveParameters()),
            Command("smartbird-thermostat.hardware.diagnostics", "Check thermostat hardware dependencies", "Checks Energy Server, FNB-58, and ADB dependency readiness", parameters: facadeParameters),
            Command("smartbird-thermostat.self-test", "Run thermostat facade self-test", "Verifies paths, settings schema, endpoints, and redaction"),
            Command("smartbird-thermostat.service.restart", "Request thermostat service restart", "Builds a ServiceBroker restart request with audit details", requiresElevation: true, dangerLevel: "medium", parameters: RestartParameters())
        ];
        return ValueTask.FromResult(commands);
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return request.CommandId switch
        {
            "smartbird-thermostat.status.fetch" or "smartbird-thermostat.status.summary" => await StatusSummaryAsync(request, cancellationToken),
            "smartbird-thermostat.events.list" => await EventsListAsync(request, cancellationToken),
            "smartbird-thermostat.logs.summary" => await LogsSummaryAsync(request, cancellationToken),
            "smartbird-thermostat.config.get" => await ConfigGetAsync(request, cancellationToken),
            "smartbird-thermostat.config.save" => ConfigSave(request),
            "smartbird-thermostat.hardware.diagnostics" => await HardwareDiagnosticsAsync(request, cancellationToken),
            "smartbird-thermostat.self-test" => SelfTest(request),
            "smartbird-thermostat.service.restart" => RestartRequest(request),
            _ => Failed(request, MptErrorCodes.NotFound, $"Command '{request.CommandId}' is not implemented by SmartBird Thermostat.")
        };
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (cursor.LastEventSeq >= 1)
        {
            yield break;
        }

        var options = Store.Load();
        var payload = await BuildStatusPayloadAsync(options, cancellationToken);
        var checks = ReadChecks(payload);
        var requiredOk = checks.Where(check => IsRequired(check.Id)).All(check => check.Ok);
        yield return new MptModuleEvent(
            Id,
            1,
            requiredOk ? "policy.triggered" : "hardware.missing",
            DateTimeOffset.UtcNow,
            new JsonObject
            {
                ["title"] = "SmartBird hardware policy",
                ["message"] = requiredOk ? "SmartBird required hardware checks are reachable." : SummarizeDegraded(checks),
                ["targetTemperatureC"] = options.TargetTemperatureC,
                ["requiredOk"] = requiredOk,
                ["checkCount"] = checks.Count
            });
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """
        {
          "type": "object",
          "properties": {
            "baseUrl": { "type": "string", "default": "http://127.0.0.1:19002" },
            "statusPath": { "type": "string", "default": "/api/status" },
            "eventsPath": { "type": "string", "default": "/api/events" },
            "logsPath": { "type": "string", "default": "/api/logs" },
            "configPath": { "type": "string", "default": "/api/config" },
            "restartPath": { "type": "string", "default": "/api/restart" },
            "energyServerBaseUrl": { "type": "string", "default": "http://127.0.0.1:19003" },
            "energyServerStatusPath": { "type": "string", "default": "/api/status" },
            "adbPath": { "type": "string", "default": "adb" },
            "fnb58Port": { "type": "string", "default": "" },
            "targetTemperatureC": { "type": "number", "minimum": 0, "maximum": 120, "default": 45 },
            "pollIntervalSeconds": { "type": "integer", "minimum": 5, "maximum": 3600, "default": 30 },
            "eventLimit": { "type": "integer", "minimum": 1, "maximum": 500, "default": 25 },
            "notifyOnAlarm": { "type": "boolean", "default": true }
          }
        }
        """));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Store.Load().ToSettingsSnapshot(Id));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        var messages = ValidatePatch(patch.Patch);
        return ValueTask.FromResult(new SettingsValidationResult(
            messages.Count == 0,
            messages,
            messages.Count == 0 ? null : new MptRuntimeError(MptErrorCodes.ValidationFailed, string.Join("; ", messages))));
    }

    public ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        var updated = SmartBirdSettings.Default().Apply(snapshot.Values) with { UpdatedAt = DateTimeOffset.UtcNow };
        Store.Save(updated);
        return ValueTask.FromResult(updated.ToSettingsSnapshot(Id) with { Revision = snapshot.Revision });
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new("smartbird-thermostat.dashboard", "dashboard-card", "SmartBird Thermostat", new JsonObject { ["moduleId"] = Id }),
            new("smartbird-thermostat.detail", "detail-page", "SmartBird Thermostat", new JsonObject { ["moduleId"] = Id }),
            new("smartbird-thermostat.settings", "settings", "SmartBird Settings", new JsonObject { ["moduleId"] = Id }),
            new("smartbird-thermostat.logs", "logs", "SmartBird Logs", new JsonObject { ["moduleId"] = Id })
        ];
        return ValueTask.FromResult(surfaces);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    private async Task<CommandExecutionResult> StatusSummaryAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var options = ResolveOptions(request.Args);
        var payload = await BuildStatusPayloadAsync(options, cancellationToken);
        return Succeeded(request, payload.ToJsonString());
    }

    private async Task<CommandExecutionResult> EventsListAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var options = ResolveOptions(request.Args);
        var response = await GetServiceJsonAsync(options.BaseUrl, options.EventsPath, "events", cancellationToken);
        var (events, totalEvents, truncated) = LimitEvents(response.Json, options.EventLimit);
        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["source"] = response.ToJson(),
            ["events"] = events,
            ["totalEvents"] = totalEvents,
            ["eventLimit"] = options.EventLimit,
            ["truncated"] = truncated,
            ["state"] = response.Ok ? "ready" : "degraded",
            ["nextAction"] = response.Ok ? "" : "Expose SmartBird /api/events or keep the module in degraded event-source mode."
        };
        return Succeeded(request, payload.ToJsonString());
    }

    private async Task<CommandExecutionResult> LogsSummaryAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var options = ResolveOptions(request.Args);
        var service = await GetServiceJsonAsync(options.BaseUrl, options.LogsPath, "logs", cancellationToken);
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
            }).ToArray<JsonNode?>()),
            ["serviceLogs"] = service.ToJson(),
            ["state"] = service.Ok || files.Length > 0 ? "ready" : "degraded",
            ["nextAction"] = service.Ok || files.Length > 0 ? "" : "Run a SmartBird command or expose /api/logs on the thermostat service."
        };
        return Succeeded(request, payload.ToJsonString());
    }

    private async Task<CommandExecutionResult> ConfigGetAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var options = ResolveOptions(request.Args);
        var service = await GetServiceJsonAsync(options.BaseUrl, options.ConfigPath, "config", cancellationToken);
        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["localConfig"] = options.ToJson(),
            ["serviceConfig"] = service.Json?.DeepClone(),
            ["service"] = service.ToJson(),
            ["state"] = service.Ok ? "ready" : "degraded",
            ["nextAction"] = service.Ok ? "" : "Expose SmartBird /api/config or continue with module-local policy settings."
        };
        return Succeeded(request, payload.ToJsonString());
    }

    private CommandExecutionResult ConfigSave(CommandRequest request)
    {
        var messages = ValidatePatch(request.Args);
        if (messages.Count > 0)
        {
            return Failed(request, MptErrorCodes.ValidationFailed, string.Join("; ", messages));
        }

        var current = Store.Load();
        var updated = current.Apply(request.Args) with { UpdatedAt = DateTimeOffset.UtcNow };
        Store.Save(updated);
        File.AppendAllText(Path.Combine(Context.LogDirectory, "smartbird-thermostat.log"), $"{DateTimeOffset.UtcNow:O} config.save targetTemperatureC={updated.TargetTemperatureC}{Environment.NewLine}");
        return Succeeded(request, new JsonObject
        {
            ["moduleId"] = Id,
            ["saved"] = true,
            ["config"] = updated.ToJson()
        }.ToJsonString());
    }

    private async Task<CommandExecutionResult> HardwareDiagnosticsAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var options = ResolveOptions(request.Args);
        var checks = await CheckDependenciesAsync(options, cancellationToken);
        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["state"] = checks.Where(check => IsRequired(check.Id)).All(check => check.Ok) ? "ready" : "degraded",
            ["dependencies"] = ToChecksJson(checks),
            ["nextAction"] = "Connect SmartBird hardware, FNB-58, Energy Server, and ADB device services for full production validation."
        };
        return Succeeded(request, payload.ToJsonString());
    }

    private CommandExecutionResult SelfTest(CommandRequest request)
    {
        var options = ResolveOptions(request.Args);
        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["settingsSchema"] = "available",
            ["dataDirectory"] = RedactPath(Context.DataDirectory),
            ["cacheDirectory"] = RedactPath(Context.CacheDirectory),
            ["logDirectory"] = RedactPath(Context.LogDirectory),
            ["redaction"] = MptLogRedactor.Redact("token=abc123 secret=hidden password=hunter2 authorization=Bearer sample"),
            ["endpoints"] = new JsonObject
            {
                ["status"] = BuildUri(options.BaseUrl, options.StatusPath).ToString(),
                ["events"] = BuildUri(options.BaseUrl, options.EventsPath).ToString(),
                ["logs"] = BuildUri(options.BaseUrl, options.LogsPath).ToString(),
                ["config"] = BuildUri(options.BaseUrl, options.ConfigPath).ToString(),
                ["energyServer"] = BuildUri(options.EnergyServerBaseUrl, options.EnergyServerStatusPath).ToString()
            },
            ["hardware"] = new JsonObject
            {
                ["adbPath"] = options.AdbPath,
                ["fnb58Port"] = string.IsNullOrWhiteSpace(options.Fnb58Port) ? "not-configured" : options.Fnb58Port
            }
        };

        File.AppendAllText(Path.Combine(Context.LogDirectory, "smartbird-thermostat.log"), $"{DateTimeOffset.UtcNow:O} self-test completed{Environment.NewLine}");
        return Succeeded(request, payload.ToJsonString());
    }

    private CommandExecutionResult RestartRequest(CommandRequest request)
    {
        var options = ResolveOptions(request.Args);
        var details = new JsonObject
        {
            ["moduleId"] = Id,
            ["broker"] = "ServiceBroker",
            ["privilegedBroker"] = "PrivilegedBroker",
            ["actionId"] = "service.restart",
            ["permissionLevel"] = "serviceUser",
            ["requiresBroker"] = true,
            ["scope"] = "SmartBirdThermostat",
            ["reason"] = ReadString(request.Args, "reason") ?? "Restart SmartBird thermostat service after degraded HTTP or hardware dependency diagnostics.",
            ["expectedChange"] = new JsonObject
            {
                ["service"] = "SmartBirdThermostat",
                ["endpoint"] = BuildUri(options.BaseUrl, options.RestartPath).ToString(),
                ["operation"] = "restart"
            },
            ["rollback"] = new JsonArray(new JsonObject
            {
                ["operation"] = "status-check",
                ["endpoint"] = BuildUri(options.BaseUrl, options.StatusPath).ToString()
            }),
            ["audit"] = "ServiceBroker must record approval, execution result, and post-restart status."
        };

        return new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "permission-required",
            false,
            "",
            new MptRuntimeError(MptErrorCodes.PermissionRequired, "Broker approval required for service.restart.", false, details));
    }

    private async Task<JsonObject> BuildStatusPayloadAsync(SmartBirdSettings options, CancellationToken cancellationToken)
    {
        var service = await ProbeHttpAsync(options.BaseUrl, options.StatusPath, "smartbird.status", "SmartBird HTTP status", cancellationToken);
        var dependencies = await CheckDependenciesAsync(options, cancellationToken);
        var checks = new[] { service }.Concat(dependencies).ToArray();
        var requiredOk = checks.Where(check => IsRequired(check.Id)).All(check => check.Ok);
        return new JsonObject
        {
            ["moduleId"] = Id,
            ["state"] = requiredOk ? "running" : "degraded",
            ["summary"] = requiredOk ? "SmartBird service and required dependencies are reachable." : SummarizeDegraded(checks),
            ["targetTemperatureC"] = options.TargetTemperatureC,
            ["pollIntervalSeconds"] = options.PollIntervalSeconds,
            ["notifyOnAlarm"] = options.NotifyOnAlarm,
            ["checks"] = ToChecksJson(checks),
            ["service"] = service.ToJson(),
            ["dependencies"] = ToChecksJson(dependencies),
            ["nextAction"] = requiredOk ? "" : "Connect SmartBird hardware services or update settings with reachable endpoints."
        };
    }

    private async Task<IReadOnlyList<SmartBirdCheck>> CheckDependenciesAsync(SmartBirdSettings options, CancellationToken cancellationToken)
    {
        var energy = await ProbeHttpAsync(options.EnergyServerBaseUrl, options.EnergyServerStatusPath, "energy-server.status", "Energy Server", cancellationToken);
        var fnb58 = CheckFnb58(options);
        var adb = await CheckAdbAsync(options, cancellationToken);
        return [energy, fnb58, adb];
    }

    private async Task<SmartBirdCheck> ProbeHttpAsync(string baseUrl, string path, string id, string label, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new SmartBirdCheck(id, label, false, "Endpoint is not configured.", null, true);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1200));
            var uri = BuildUri(baseUrl, path);
            using var response = await _httpClient.GetAsync(uri, timeout.Token);
            var body = RedactSensitive(await response.Content.ReadAsStringAsync(timeout.Token));
            var message = $"HTTP {(int)response.StatusCode}: {Trim(body)}";
            return new SmartBirdCheck(id, label, response.IsSuccessStatusCode, message, uri.ToString(), true);
        }
        catch (OperationCanceledException)
        {
            return new SmartBirdCheck(id, label, false, $"Timed out while checking {MptLogRedactor.Redact(baseUrl)}.", BuildUri(baseUrl, path).ToString(), true);
        }
        catch (Exception ex)
        {
            return new SmartBirdCheck(id, label, false, MptLogRedactor.Redact(ex.Message), SafeUri(baseUrl, path), true);
        }
    }

    private static SmartBirdCheck CheckFnb58(SmartBirdSettings options)
    {
        if (string.IsNullOrWhiteSpace(options.Fnb58Port))
        {
            return new SmartBirdCheck("fnb58.power-meter", "FNB-58 power meter", false, "FNB-58 serial port is not configured.", null, true);
        }

        return new SmartBirdCheck("fnb58.power-meter", "FNB-58 power meter", false, $"FNB-58 native probe for {options.Fnb58Port} requires the production hardware reader.", options.Fnb58Port, true);
    }

    private static async Task<SmartBirdCheck> CheckAdbAsync(SmartBirdSettings options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.AdbPath))
        {
            return new SmartBirdCheck("adb.devices", "ADB devices", false, "ADB path is not configured.", null, true);
        }

        var result = await RunToolAsync(options.AdbPath, ["devices"], TimeSpan.FromSeconds(3), cancellationToken);
        return new SmartBirdCheck("adb.devices", "ADB devices", result.Available && result.ExitCode == 0, result.Message, options.AdbPath, true);
    }

    private async Task<ServiceJsonResult> GetServiceJsonAsync(string baseUrl, string path, string source, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1200));
            var uri = BuildUri(baseUrl, path);
            using var response = await _httpClient.GetAsync(uri, timeout.Token);
            var text = RedactSensitive(await response.Content.ReadAsStringAsync(timeout.Token));
            JsonNode? json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                json = new JsonObject { ["text"] = Trim(text) };
            }

            return new ServiceJsonResult(source, response.IsSuccessStatusCode, (int)response.StatusCode, uri.ToString(), Trim(text), json);
        }
        catch (OperationCanceledException)
        {
            return new ServiceJsonResult(source, false, 0, SafeUri(baseUrl, path), "Timed out while querying SmartBird service.", null);
        }
        catch (Exception ex)
        {
            return new ServiceJsonResult(source, false, 0, SafeUri(baseUrl, path), MptLogRedactor.Redact(ex.Message), null);
        }
    }

    private SmartBirdSettings ResolveOptions(JsonObject args)
    {
        return Store.Load().Apply(args);
    }

    private static IReadOnlyList<string> ValidatePatch(JsonObject patch)
    {
        var messages = new List<string>();
        foreach (var key in new[] { "baseUrl", "energyServerBaseUrl" })
        {
            if (patch.TryGetPropertyValue(key, out var node) && node is not null)
            {
                try
                {
                    if (!Uri.TryCreate(node.GetValue<string>(), UriKind.Absolute, out _))
                    {
                        messages.Add($"{key} must be an absolute URL.");
                    }
                }
                catch (InvalidOperationException)
                {
                    messages.Add($"{key} must be a string.");
                }
            }
        }

        foreach (var key in new[] { "statusPath", "eventsPath", "logsPath", "configPath", "restartPath", "energyServerStatusPath" })
        {
            if (patch.TryGetPropertyValue(key, out var node) && node is not null)
            {
                try
                {
                    var path = node.GetValue<string>();
                    if (!path.StartsWith("/", StringComparison.Ordinal))
                    {
                        messages.Add($"{key} must start with '/'.");
                    }
                }
                catch (InvalidOperationException)
                {
                    messages.Add($"{key} must be a string.");
                }
            }
        }

        if (ReadDouble(patch, "targetTemperatureC") is { } target && (target < 0 || target > 120))
        {
            messages.Add("targetTemperatureC must be between 0 and 120.");
        }

        if (ReadInt(patch, "pollIntervalSeconds") is { } interval && (interval < 5 || interval > 3600))
        {
            messages.Add("pollIntervalSeconds must be between 5 and 3600.");
        }

        if (ReadInt(patch, "eventLimit") is { } eventLimit && (eventLimit < 1 || eventLimit > 500))
        {
            messages.Add("eventLimit must be between 1 and 500.");
        }

        return messages;
    }

    private static (JsonArray Events, int TotalEvents, bool Truncated) LimitEvents(JsonNode? json, int limit)
    {
        var source = json switch
        {
            JsonArray array => array,
            JsonObject obj when obj["events"] is JsonArray events => events,
            _ => new JsonArray()
        };
        var total = source.Count;
        var start = Math.Max(0, total - limit);
        var limited = new JsonArray();
        for (var i = start; i < total; i++)
        {
            limited.Add(source[i]?.DeepClone());
        }

        return (limited, total, total > limited.Count);
    }

    private static IReadOnlyList<HealthCheckSnapshot> ReadChecks(JsonObject payload)
    {
        var checks = new List<HealthCheckSnapshot>();
        if (payload["checks"] is not JsonArray array)
        {
            return checks;
        }

        foreach (var item in array.OfType<JsonObject>())
        {
            checks.Add(new HealthCheckSnapshot(
                item["id"]?.GetValue<string>() ?? "unknown",
                item["label"]?.GetValue<string>() ?? "Unknown",
                item["ok"]?.GetValue<bool>() ?? false,
                item["message"]?.GetValue<string>() ?? ""));
        }

        return checks;
    }

    private static JsonArray ToChecksJson(IEnumerable<SmartBirdCheck> checks)
    {
        return new JsonArray(checks.Select(check => check.ToJson()).ToArray<JsonNode?>());
    }

    private static bool IsRequired(string id)
    {
        return id is "smartbird.status" or "energy-server.status" or "fnb58.power-meter" or "adb.devices";
    }

    private static Uri BuildUri(string baseUrl, string path)
    {
        return new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));
    }

    private static string SafeUri(string baseUrl, string path)
    {
        try
        {
            return BuildUri(baseUrl, path).ToString();
        }
        catch
        {
            return MptLogRedactor.Redact($"{baseUrl}{path}");
        }
    }

    private static MptCommandDescriptor Command(
        string id,
        string title,
        string subtitle,
        bool requiresElevation = false,
        string dangerLevel = "",
        IReadOnlyList<CommandParameterDescriptor>? parameters = null)
    {
        var execution = new JsonObject { ["type"] = "module.execute" };
        IReadOnlyList<string>? constraints = null;
        if (requiresElevation)
        {
            execution["brokerApprovalOnly"] = true;
            constraints =
            [
                MptOperationConstraints.MutatesSystemState,
                MptOperationConstraints.RequiresElevatedWrites
            ];
        }

        return new MptCommandDescriptor(
            id,
            "smartbird-thermostat",
            title,
            subtitle,
            "action",
            requiresElevation,
            DangerLevel: dangerLevel,
            Category: "SmartBird Thermostat",
            TimeoutMs: 10000,
            Execution: execution,
            Parameters: parameters,
            Constraints: constraints);
    }

    private static IReadOnlyList<CommandParameterDescriptor> FacadeParameters()
    {
        return
        [
            new CommandParameterDescriptor("baseUrl", "Base URL", "text", false, ""),
            new CommandParameterDescriptor("statusPath", "Status path", "text", false, "/status"),
            new CommandParameterDescriptor("eventsPath", "Events path", "text", false, "/events"),
            new CommandParameterDescriptor("energyServerBaseUrl", "Energy server URL", "text", false, "")
        ];
    }

    private static IReadOnlyList<CommandParameterDescriptor> ConfigSaveParameters()
    {
        return
        [
            new CommandParameterDescriptor("baseUrl", "Base URL", "text", false, ""),
            new CommandParameterDescriptor("statusPath", "Status path", "text", false, "/status"),
            new CommandParameterDescriptor("targetTemperatureC", "Target temperature C", "number", false, "22"),
            new CommandParameterDescriptor("pollIntervalSeconds", "Poll interval seconds", "number", false, "30")
        ];
    }

    private static IReadOnlyList<CommandParameterDescriptor> RestartParameters()
    {
        return
        [
            new CommandParameterDescriptor("reason", "Reason", "multiline", false, "Restart SmartBird thermostat service after degraded diagnostics.")
        ];
    }

    private static CommandExecutionResult Succeeded(CommandRequest request, string output)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
    }

    private static CommandExecutionResult Failed(CommandRequest request, string code, string message, bool retryable = false, JsonObject? details = null)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "failed", false, "", new MptRuntimeError(code, message, retryable, details));
    }

    private static string SummarizeDegraded(IEnumerable<SmartBirdCheck> checks)
    {
        var failed = checks.Where(check => !check.Ok && check.Required).Select(check => $"{check.Label}: {check.Message}").ToArray();
        return failed.Length == 0 ? "SmartBird dependencies are reachable." : string.Join("; ", failed);
    }

    private static string SummarizeDegraded(IEnumerable<HealthCheckSnapshot> checks)
    {
        var failed = checks.Where(check => !check.Ok && IsRequired(check.Id)).Select(check => $"{check.Label}: {check.Message}").ToArray();
        return failed.Length == 0 ? "SmartBird dependencies are reachable." : string.Join("; ", failed);
    }

    private static string Trim(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 600 ? value : value[..600] + "...";
    }

    private static string RedactPath(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local)
            ? path
            : path.Replace(local, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase);
    }

    private static string RedactSensitive(string value)
    {
        value = MptLogRedactor.Redact(value);
        var replacements = new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%")
        };

        foreach (var (path, token) in replacements.Where(item => !string.IsNullOrWhiteSpace(item.Item1)).OrderByDescending(item => item.Item1.Length))
        {
            value = value.Replace(path, token, StringComparison.OrdinalIgnoreCase);
            value = value.Replace(path.Replace("\\", "\\\\", StringComparison.Ordinal), token, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static string? ReadString(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            try
            {
                return checked((int)node.GetValue<long>());
            }
            catch
            {
                return null;
            }
        }
    }

    private static double? ReadDouble(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<double>();
        }
        catch (InvalidOperationException)
        {
            try
            {
                return node.GetValue<int>();
            }
            catch (InvalidOperationException)
            {
                try
                {
                    return node.GetValue<long>();
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }
    }

    private static bool? ReadBool(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<ToolResult> RunToolAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return ToolResult.Failed(fileName, -1, "Process could not be started.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var stdout = RedactAdbOutput(await stdoutTask);
            var stderr = MptLogRedactor.Redact(await stderrTask);
            return new ToolResult(fileName, true, process.ExitCode, Trim(stdout), Trim(stderr), DateTimeOffset.UtcNow - startedAt);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return ToolResult.Missing(fileName, $"{fileName} executable was not found on PATH.");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Failed(fileName, -1, $"{fileName} timed out after {timeout.TotalSeconds:n0}s.");
        }
        catch (Exception ex)
        {
            return ToolResult.Failed(fileName, -1, MptLogRedactor.Redact(ex.Message));
        }
    }

    private static string RedactAdbOutput(string value)
    {
        value = MptLogRedactor.Redact(value);
        var lines = value.Replace("\r\n", "\n").Split('\n');
        var deviceIndex = 1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var firstWhitespace = line.AsSpan().IndexOfAny(' ', '\t');
            if (firstWhitespace <= 0)
            {
                continue;
            }

            lines[i] = $"<adb-device-{deviceIndex++}>{line[firstWhitespace..]}";
        }

        return string.Join(Environment.NewLine, lines);
    }

    private sealed record ServiceJsonResult(string Source, bool Ok, int StatusCode, string Uri, string Message, JsonNode? Json)
    {
        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["source"] = Source,
                ["ok"] = Ok,
                ["statusCode"] = StatusCode,
                ["uri"] = Uri,
                ["message"] = Message
            };
        }
    }

    private sealed record SmartBirdCheck(string Id, string Label, bool Ok, string Message, string? Uri, bool Required)
    {
        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["id"] = Id,
                ["label"] = Label,
                ["ok"] = Ok,
                ["message"] = Message,
                ["uri"] = Uri,
                ["required"] = Required
            };
        }
    }

    private sealed record ToolResult(string Tool, bool Available, int ExitCode, string Stdout, string Stderr, TimeSpan Duration)
    {
        public string Message
        {
            get
            {
                if (!Available)
                {
                    return Stderr;
                }

                if (ExitCode == 0)
                {
                    return string.IsNullOrWhiteSpace(Stdout) ? "Command completed." : Stdout;
                }

                return string.IsNullOrWhiteSpace(Stderr) ? $"Exited with code {ExitCode}." : Stderr;
            }
        }

        public static ToolResult Missing(string tool, string message)
        {
            return new ToolResult(tool, false, -1, "", message, TimeSpan.Zero);
        }

        public static ToolResult Failed(string tool, int exitCode, string message)
        {
            return new ToolResult(tool, true, exitCode, "", message, TimeSpan.Zero);
        }
    }

    private sealed class SmartBirdStore
    {
        private readonly string _path;

        public SmartBirdStore(string path)
        {
            _path = path;
        }

        public void EnsureDefaults()
        {
            if (!File.Exists(_path))
            {
                Save(SmartBirdSettings.Default());
            }
        }

        public SmartBirdSettings Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return SmartBirdSettings.Default();
                }

                return (JsonSerializer.Deserialize<SmartBirdSettings>(File.ReadAllText(_path), JsonOptions) ?? SmartBirdSettings.Default()).Normalize();
            }
            catch
            {
                return SmartBirdSettings.Default();
            }
        }

        public void Save(SmartBirdSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private sealed record SmartBirdSettings(
        string BaseUrl,
        string StatusPath,
        string EventsPath,
        string LogsPath,
        string ConfigPath,
        string RestartPath,
        string EnergyServerBaseUrl,
        string EnergyServerStatusPath,
        string AdbPath,
        string Fnb58Port,
        double TargetTemperatureC,
        int PollIntervalSeconds,
        int EventLimit,
        bool NotifyOnAlarm,
        DateTimeOffset UpdatedAt)
    {
        public static SmartBirdSettings Default()
        {
            return new SmartBirdSettings(
                "http://127.0.0.1:19002",
                "/api/status",
                "/api/events",
                "/api/logs",
                "/api/config",
                "/api/restart",
                "http://127.0.0.1:19003",
                "/api/status",
                "adb",
                "",
                45,
                30,
                25,
                true,
                DateTimeOffset.UtcNow);
        }

        public SmartBirdSettings Apply(JsonObject patch)
        {
            return (this with
            {
                BaseUrl = ReadString(patch, "baseUrl") ?? BaseUrl,
                StatusPath = ReadString(patch, "statusPath") ?? StatusPath,
                EventsPath = ReadString(patch, "eventsPath") ?? EventsPath,
                LogsPath = ReadString(patch, "logsPath") ?? LogsPath,
                ConfigPath = ReadString(patch, "configPath") ?? ConfigPath,
                RestartPath = ReadString(patch, "restartPath") ?? RestartPath,
                EnergyServerBaseUrl = ReadString(patch, "energyServerBaseUrl") ?? EnergyServerBaseUrl,
                EnergyServerStatusPath = ReadString(patch, "energyServerStatusPath") ?? EnergyServerStatusPath,
                AdbPath = ReadString(patch, "adbPath") ?? AdbPath,
                Fnb58Port = ReadString(patch, "fnb58Port") ?? Fnb58Port,
                TargetTemperatureC = ReadDouble(patch, "targetTemperatureC") ?? TargetTemperatureC,
                PollIntervalSeconds = ReadInt(patch, "pollIntervalSeconds") ?? PollIntervalSeconds,
                EventLimit = ReadInt(patch, "eventLimit") ?? EventLimit,
                NotifyOnAlarm = ReadBool(patch, "notifyOnAlarm") ?? NotifyOnAlarm
            }).Normalize();
        }

        public SmartBirdSettings Normalize()
        {
            var defaults = Default();
            return this with
            {
                BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? defaults.BaseUrl : BaseUrl,
                StatusPath = NormalizePath(StatusPath, defaults.StatusPath),
                EventsPath = NormalizePath(EventsPath, defaults.EventsPath),
                LogsPath = NormalizePath(LogsPath, defaults.LogsPath),
                ConfigPath = NormalizePath(ConfigPath, defaults.ConfigPath),
                RestartPath = NormalizePath(RestartPath, defaults.RestartPath),
                EnergyServerBaseUrl = string.IsNullOrWhiteSpace(EnergyServerBaseUrl) ? defaults.EnergyServerBaseUrl : EnergyServerBaseUrl,
                EnergyServerStatusPath = NormalizePath(EnergyServerStatusPath, defaults.EnergyServerStatusPath),
                AdbPath = string.IsNullOrWhiteSpace(AdbPath) ? defaults.AdbPath : AdbPath,
                TargetTemperatureC = TargetTemperatureC is < 0 or > 120 ? defaults.TargetTemperatureC : TargetTemperatureC,
                PollIntervalSeconds = PollIntervalSeconds is < 5 or > 3600 ? defaults.PollIntervalSeconds : PollIntervalSeconds,
                EventLimit = EventLimit is < 1 or > 500 ? defaults.EventLimit : EventLimit
            };
        }

        public SettingsSnapshotDocument ToSettingsSnapshot(string moduleId)
        {
            return new SettingsSnapshotDocument(moduleId, 1, ToJson(), UpdatedAt);
        }

        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["baseUrl"] = BaseUrl,
                ["statusPath"] = StatusPath,
                ["eventsPath"] = EventsPath,
                ["logsPath"] = LogsPath,
                ["configPath"] = ConfigPath,
                ["restartPath"] = RestartPath,
                ["energyServerBaseUrl"] = EnergyServerBaseUrl,
                ["energyServerStatusPath"] = EnergyServerStatusPath,
                ["adbPath"] = AdbPath,
                ["fnb58Port"] = Fnb58Port,
                ["targetTemperatureC"] = TargetTemperatureC,
                ["pollIntervalSeconds"] = PollIntervalSeconds,
                ["eventLimit"] = EventLimit,
                ["notifyOnAlarm"] = NotifyOnAlarm,
                ["updatedAt"] = UpdatedAt
            };
        }

        private static string NormalizePath(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) || !value.StartsWith("/", StringComparison.Ordinal) ? fallback : value;
        }
    }
}

