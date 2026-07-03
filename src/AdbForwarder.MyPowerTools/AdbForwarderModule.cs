using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;

namespace AdbForwarder.MyPowerTools;

public sealed class AdbForwarderModule : IMptModule
{
    private ModuleContext? _context;

    public string Id => "adb-forwarder";
    public string PackageId => "adb-forwarder";
    public Version Version => new(0, 2, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        Directory.CreateDirectory(context.DataDirectory);
        Directory.CreateDirectory(context.CacheDirectory);
        Directory.CreateDirectory(context.LogDirectory);
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status", "commands", "settings", "logs", "dashboardCard", "detailPage"]));
    }

    public async ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var adb = await RunToolAsync("adb", ["version"], TimeSpan.FromSeconds(3), cancellationToken);
        var devices = adb.Available
            ? await RunToolAsync("adb", ["devices", "-l"], TimeSpan.FromSeconds(5), cancellationToken)
            : ToolResult.Missing("adb", "ADB executable was not found on PATH.");
        var portproxy = await ReadPortProxyAsync(cancellationToken);
        var portproxyRules = portproxy.Available && portproxy.ExitCode == 0
            ? PortProxyParser.Parse(portproxy.Stdout)
            : [];
        var checks = new[]
        {
            new HealthCheckSnapshot("adb.available", "ADB executable", adb.Available && adb.ExitCode == 0, adb.Message),
            new HealthCheckSnapshot("adb.devices", "ADB devices", devices.Available && devices.ExitCode == 0, devices.Message),
            new HealthCheckSnapshot("windows.portproxy", "Windows portproxy", portproxy.Available && portproxy.ExitCode == 0, portproxy.Message),
            new HealthCheckSnapshot("windows.portproxy.rules", "Portproxy rules", portproxy.Available && portproxy.ExitCode == 0, $"{portproxyRules.Count} v4tov4 rule(s) detected.")
        };

        var ok = checks.All(check => check.Ok);
        return new ModuleStatusSnapshot(
            Id,
            ok ? "running" : "degraded",
            ok ? "ADB and Windows portproxy diagnostics are available." : SummarizeDegraded(checks),
            DateTimeOffset.UtcNow,
            checks,
            0);
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            new("adb-forwarder.diagnostics.summary", Id, "Collect ADB forwarding diagnostics", "ADB devices and portproxy state", "action", Category: "AdbForwarder"),
            new("adb-forwarder.devices.scan", Id, "Scan ADB devices", "Run adb devices -l", "action", Category: "AdbForwarder"),
            new("adb-forwarder.portproxy.list", Id, "List Windows portproxy rules", "Read netsh interface portproxy state", "action", Category: "AdbForwarder"),
            new("adb-forwarder.portproxy.plan", Id, "Plan portproxy changes", "Compare configured ADB mappings with current Windows state", "action", Category: "AdbForwarder"),
            new("adb-forwarder.portproxy.apply", Id, "Request portproxy apply", "Create an audited NetworkBroker apply request", "action", true, DangerLevel: "medium", Category: "AdbForwarder"),
            new("adb-forwarder.portproxy.revert", Id, "Request portproxy revert", "Create an audited NetworkBroker revert request", "action", true, DangerLevel: "medium", Category: "AdbForwarder")
        ];
        return ValueTask.FromResult(commands);
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return request.CommandId switch
        {
            "adb-forwarder.diagnostics.summary" => Succeeded(request, await DiagnosticsSummaryAsync(cancellationToken)),
            "adb-forwarder.devices.scan" => Succeeded(request, ToJson(await RunToolAsync("adb", ["devices", "-l"], TimeSpan.FromSeconds(10), cancellationToken))),
            "adb-forwarder.portproxy.list" => Succeeded(request, ToJson(await ReadPortProxyAsync(cancellationToken), includePortProxyRules: true)),
            "adb-forwarder.portproxy.plan" => await PlanPortProxyAsync(request, cancellationToken),
            "adb-forwarder.portproxy.apply" => await RequestPortProxyApplyAsync(request, cancellationToken),
            "adb-forwarder.portproxy.revert" => await RequestPortProxyRevertAsync(request, cancellationToken),
            _ => new CommandExecutionResult(
                request.InvocationId,
                request.CommandId,
                "failed",
                false,
                "",
                new MptRuntimeError(MptErrorCodes.NotFound, $"Command '{request.CommandId}' is not implemented by AdbForwarder."))
        };
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """
        {
          "type": "object",
          "properties": {
            "adbPath": { "type": "string", "default": "adb" },
            "applyMode": { "type": "string", "enum": ["brokered"], "default": "brokered" },
            "mappings": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["listenAddress", "listenPort", "connectAddress", "connectPort"],
                "properties": {
                  "name": { "type": "string" },
                  "id": { "type": "string" },
                  "enabled": { "type": "boolean", "default": true },
                  "listenAddress": { "type": "string" },
                  "listenPort": { "type": "integer", "minimum": 1, "maximum": 65535 },
                  "connectAddress": { "type": "string" },
                  "connectPort": { "type": "integer", "minimum": 1, "maximum": 65535 }
                }
              }
            }
          }
        }
        """));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var values = new JsonObject
        {
            ["adbPath"] = "adb",
            ["applyMode"] = "brokered",
            ["mappings"] = new JsonArray()
        };
        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, values, DateTimeOffset.UtcNow));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        var (_, messages) = AdbPortProxyModel.ParseMappings(patch.Patch);
        return ValueTask.FromResult(new SettingsValidationResult(
            messages.Count == 0,
            messages,
            messages.Count == 0 ? null : new MptRuntimeError(MptErrorCodes.ValidationFailed, string.Join("; ", messages))));
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new("adb-forwarder.dashboard", "dashboard-card", "AdbForwarder", new JsonObject { ["state"] = "diagnostics-ready" }),
            new("adb-forwarder.detail", "detail-page", "ADB Forwarding Diagnostics", new JsonObject { ["moduleId"] = Id })
        ];
        return ValueTask.FromResult(surfaces);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    private async Task<string> DiagnosticsSummaryAsync(CancellationToken cancellationToken)
    {
        var adbVersion = await RunToolAsync("adb", ["version"], TimeSpan.FromSeconds(3), cancellationToken);
        var devices = adbVersion.Available
            ? await RunToolAsync("adb", ["devices", "-l"], TimeSpan.FromSeconds(10), cancellationToken)
            : ToolResult.Missing("adb", "ADB executable was not found on PATH.");
        var portproxy = await ReadPortProxyAsync(cancellationToken);
        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["dataDirectory"] = string.IsNullOrWhiteSpace(_context?.DataDirectory) ? "" : "<module-data-dir>",
            ["adbVersion"] = adbVersion.ToJson(),
            ["devices"] = devices.ToJson(),
            ["portproxy"] = portproxy.ToJson(includePortProxyRules: true)
        };
        return payload.ToJsonString();
    }

    private async Task<CommandExecutionResult> PlanPortProxyAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var planned = await BuildPortProxyPlanAsync(request.Args, revert: false, cancellationToken);
        if (planned.ValidationMessages.Count > 0)
        {
            return ValidationFailed(request, planned.ValidationMessages);
        }

        var payload = new JsonObject
        {
            ["moduleId"] = Id,
            ["plan"] = planned.Plan!.ToJson(),
            ["currentState"] = planned.CurrentState?.ToJson(includePortProxyRules: true)
        };
        return Succeeded(request, payload.ToJsonString());
    }

    private async Task<CommandExecutionResult> RequestPortProxyApplyAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var planned = await BuildPortProxyPlanAsync(request.Args, revert: false, cancellationToken);
        if (planned.ValidationMessages.Count > 0)
        {
            return ValidationFailed(request, planned.ValidationMessages);
        }

        return PermissionRequired(
            request,
            "network.portproxy.apply",
            planned.Plan!,
            ReadReason(request.Args, "Apply configured ADB port forwarding rules through NetworkBroker."));
    }

    private async Task<CommandExecutionResult> RequestPortProxyRevertAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var planned = await BuildPortProxyPlanAsync(request.Args, revert: true, cancellationToken);
        if (planned.ValidationMessages.Count > 0)
        {
            return ValidationFailed(request, planned.ValidationMessages);
        }

        return PermissionRequired(
            request,
            "network.portproxy.remove",
            planned.Plan!,
            ReadReason(request.Args, "Revert configured ADB port forwarding rules through NetworkBroker."));
    }

    private async Task<PortProxyPlanningResult> BuildPortProxyPlanAsync(JsonObject args, bool revert, CancellationToken cancellationToken)
    {
        var (mappings, validationMessages) = AdbPortProxyModel.ParseMappings(args);
        if (validationMessages.Count > 0)
        {
            return new PortProxyPlanningResult(null, null, validationMessages);
        }

        var warnings = new List<string>();
        var currentState = default(ToolResult?);
        var currentRules = TryReadCurrentRulesOverride(args);
        if (currentRules is null)
        {
            currentState = await ReadPortProxyAsync(cancellationToken);
            if (currentState.Available && currentState.ExitCode == 0)
            {
                currentRules = PortProxyParser.Parse(currentState.Stdout);
            }
            else
            {
                currentRules = [];
                warnings.Add(currentState.Message);
            }
        }

        var plan = revert
            ? AdbPortProxyPlanner.CreateRevertPlan(mappings, currentRules, warnings)
            : AdbPortProxyPlanner.CreateApplyPlan(mappings, currentRules, warnings);
        return new PortProxyPlanningResult(plan, currentState, []);
    }

    private static IReadOnlyList<PortProxyRuleSnapshot>? TryReadCurrentRulesOverride(JsonObject args)
    {
        if (args.TryGetPropertyValue("currentRules", out var currentRulesNode) && currentRulesNode is JsonArray currentRules)
        {
            return AdbPortProxyModel.ParseRules(currentRules);
        }

        if (args.TryGetPropertyValue("currentPortProxyText", out var textNode) && textNode is not null)
        {
            try
            {
                return PortProxyParser.Parse(textNode.GetValue<string>());
            }
            catch (InvalidOperationException)
            {
                return [];
            }
        }

        return null;
    }

    private CommandExecutionResult PermissionRequired(CommandRequest request, string actionId, AdbPortProxyPlan plan, string reason)
    {
        var details = new JsonObject
        {
            ["moduleId"] = Id,
            ["broker"] = "NetworkBroker",
            ["privilegedBroker"] = "PrivilegedBroker",
            ["actionId"] = actionId,
            ["permissionLevel"] = "elevated",
            ["requiresBroker"] = true,
            ["scope"] = plan.Scope,
            ["reason"] = reason,
            ["expectedChange"] = plan.ExpectedChangeJson(),
            ["rollback"] = AdbPortProxyModel.ToJsonArray(plan.Rollback, step => step.ToJson()),
            ["desiredMappings"] = AdbPortProxyModel.ToJsonArray(plan.DesiredMappings, mapping => mapping.ToJson()),
            ["currentRules"] = AdbPortProxyModel.ToJsonArray(plan.CurrentRules, rule => rule.ToJson()),
            ["warnings"] = AdbPortProxyModel.ToJsonArray(plan.Warnings, warning => JsonValue.Create(warning)!),
            ["partialFailurePolicy"] = "Execute rollback steps in order through NetworkBroker and append audit entries for each elevated action."
        };

        return new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "permission-required",
            false,
            "",
            new MptRuntimeError(MptErrorCodes.PermissionRequired, $"Broker approval required for {actionId}.", false, details));
    }

    private static CommandExecutionResult ValidationFailed(CommandRequest request, IReadOnlyList<string> messages)
    {
        return new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "failed",
            false,
            "",
            new MptRuntimeError(MptErrorCodes.ValidationFailed, string.Join("; ", messages)));
    }

    private static string ReadReason(JsonObject args, string fallback)
    {
        if (!args.TryGetPropertyValue("reason", out var node) || node is null)
        {
            return fallback;
        }

        try
        {
            var reason = node.GetValue<string>();
            return string.IsNullOrWhiteSpace(reason) ? fallback : reason;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static async Task<ToolResult> ReadPortProxyAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ToolResult.Unsupported("netsh", "Windows portproxy diagnostics are only available on Windows.");
        }

        return await RunToolAsync("netsh", ["interface", "portproxy", "show", "v4tov4"], TimeSpan.FromSeconds(5), cancellationToken);
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
            var stdout = RedactToolOutput(fileName, arguments, await stdoutTask);
            var stderr = LogRouter.Redact(await stderrTask);
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
            return ToolResult.Failed(fileName, -1, LogRouter.Redact(ex.Message));
        }
    }

    private static CommandExecutionResult Succeeded(CommandRequest request, string output)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
    }

    private static string ToJson(ToolResult result, bool includePortProxyRules = false)
    {
        return result.ToJson(includePortProxyRules).ToJsonString();
    }

    private static string SummarizeDegraded(IEnumerable<HealthCheckSnapshot> checks)
    {
        return string.Join("; ", checks.Where(check => !check.Ok).Select(check => $"{check.Label}: {check.Message}"));
    }

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 4000 ? value : value[..4000] + "...";
    }

    private static string RedactToolOutput(string fileName, IReadOnlyList<string> arguments, string value)
    {
        value = LogRouter.Redact(value);
        if (!string.Equals(fileName, "adb", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (arguments.Contains("devices", StringComparer.OrdinalIgnoreCase))
        {
            return RedactAdbDevices(value);
        }

        return RedactAdbVersion(value);
    }

    private static string RedactAdbVersion(string value)
    {
        var lines = value.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("Installed as ", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "Installed as <adb-path>";
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string RedactAdbDevices(string value)
    {
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

            var rest = line[firstWhitespace..];
            lines[i] = $"<adb-device-{deviceIndex++}>{rest}";
        }

        return string.Join(Environment.NewLine, lines);
    }

    private sealed record ToolResult(
        string Tool,
        bool Available,
        int ExitCode,
        string Stdout,
        string Stderr,
        TimeSpan Duration)
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

        public JsonObject ToJson(bool includePortProxyRules = false)
        {
            var payload = new JsonObject
            {
                ["tool"] = Tool,
                ["available"] = Available,
                ["exitCode"] = ExitCode,
                ["stdout"] = Stdout,
                ["stderr"] = Stderr,
                ["durationMs"] = (int)Duration.TotalMilliseconds
            };

            if (includePortProxyRules && string.Equals(Tool, "netsh", StringComparison.OrdinalIgnoreCase) && Available && ExitCode == 0)
            {
                payload["rules"] = AdbPortProxyModel.ToJsonArray(PortProxyParser.Parse(Stdout), rule => rule.ToJson());
            }

            return payload;
        }

        public static ToolResult Missing(string tool, string message)
        {
            return new ToolResult(tool, false, -1, "", message, TimeSpan.Zero);
        }

        public static ToolResult Unsupported(string tool, string message)
        {
            return new ToolResult(tool, false, -1, "", message, TimeSpan.Zero);
        }

        public static ToolResult Failed(string tool, int exitCode, string message)
        {
            return new ToolResult(tool, true, exitCode, "", message, TimeSpan.Zero);
        }
    }

    private sealed record PortProxyPlanningResult(AdbPortProxyPlan? Plan, ToolResult? CurrentState, IReadOnlyList<string> ValidationMessages);
}
