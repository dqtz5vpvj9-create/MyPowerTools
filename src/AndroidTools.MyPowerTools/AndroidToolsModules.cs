using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MyPowerTools.Protocol;
using MyPowerTools.Abstractions;

namespace AndroidTools.MyPowerTools;

public sealed class AndroidToolsRemoteCommandsModule : AndroidToolsModuleBase
{
    private JsonObject _settings = RemoteDefaultSettings();

    public override string Id => "android-tools.remote-commands";
    public override string DisplayName => "Remote Commands";

    public override ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var catalog = LoadCatalog();
        var history = Shared.LoadRemoteCommandHistorySummary();
        var checks = new[]
        {
            new HealthCheckSnapshot("powertool.commands", "commands.yaml", catalog.Commands.Count > 0, catalog.Summary),
            new HealthCheckSnapshot("powertool.command-tools", "C# command tools", catalog.PythonToolCount > 0, $"{catalog.PythonToolCount} py command tool(s) mapped."),
            new HealthCheckSnapshot("powertool.history", "Shared history", history.Available, history.Message)
        };

        return ValueTask.FromResult(Status(catalog.Commands.Count > 0 ? "running" : "degraded", catalog.Summary, checks));
    }

    public override ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        var catalog = LoadCatalog();
        var commands = new List<MptCommandDescriptor>
        {
            Command("android-tools.remote-commands.catalog.summary", "Summarize imported remote commands", "List commands imported from powertool commands.yaml"),
            Command("android-tools.remote-commands.history.summary", "Summarize remote command history", "Show MyPowerTools and legacy powertool history state")
        };

        foreach (var imported in catalog.Commands)
        {
            commands.Add(Command(
                $"android-tools.remote-commands.run.{imported.Id}",
                imported.Label,
                imported.Description,
                timeoutMs: imported.Type == "shell" ? 120000 : 30000,
                constraints: imported.Type == "shell"
                    ?
                    [
                        MptOperationConstraints.RunsExternalProcesses,
                        MptOperationConstraints.RequiresLongRunningLoop
                    ]
                    : null,
                parameters: string.Equals(imported.Type, "shell", StringComparison.OrdinalIgnoreCase)
                    ?
                    [
                        new CommandParameterDescriptor("execute", "Execute", "boolean", false, "false"),
                        new CommandParameterDescriptor("timeoutMs", "Timeout ms", "number", false, "120000")
                    ]
                    :
                    [
                        new CommandParameterDescriptor("input", "Input", "multiline", false, "")
                    ],
                execution: new JsonObject
                {
                    ["type"] = "module.execute",
                    ["source"] = "powertool.commands.yaml",
                    ["powertoolCommandId"] = imported.Id,
                    ["powertoolCommandType"] = imported.Type
                }));
        }

        return ValueTask.FromResult<IReadOnlyList<MptCommandDescriptor>>(commands);
    }

    public override async ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (request.CommandId == "android-tools.remote-commands.catalog.summary")
        {
            return Succeeded(request, LoadCatalog().ToJson().ToJsonString());
        }

        if (request.CommandId == "android-tools.remote-commands.history.summary")
        {
            return Succeeded(request, Shared.LoadRemoteCommandHistorySummary().ToJson().ToJsonString());
        }

        const string prefix = "android-tools.remote-commands.run.";
        if (!request.CommandId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(request);
        }

        var commandId = request.CommandId[prefix.Length..];
        var catalog = LoadCatalog();
        var command = catalog.Commands.FirstOrDefault(item => string.Equals(item.Id, commandId, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            return Failed(request, MptErrorCodes.NotFound, $"Powertool command '{commandId}' was not found in the imported catalog.");
        }

        var result = command.Type switch
        {
            "py" => ExecutePythonTool(request, command),
            "shell" => await ExecuteShellCommandAsync(request, command, cancellationToken),
            _ => Failed(request, MptErrorCodes.ValidationFailed, $"Unsupported powertool command type '{command.Type}'.")
        };

        Shared.AppendRemoteCommandHistory(command, result);
        return result;
    }

    public override async IAsyncEnumerable<CommandExecutionEvent> ExecuteCommandStreamAsync(CommandRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryResolveShellCommand(request, out var command, out var failed))
        {
            var result = failed ?? await ExecuteCommandAsync(request, cancellationToken);
            yield return FinalEvent(result);
            yield break;
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Max(1000, ReadInt(request.Args, "timeoutMs") ?? 120000));
        await foreach (var evt in Shared.RunShellCommandStreamAsync(request, command, timeout, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return evt;
        }
    }

    protected override async IAsyncEnumerable<MptModuleEvent> BuildModuleEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var seq = Math.Max(1UL, cursor.LastEventSeq);
        var catalog = LoadCatalog();
        var history = Shared.LoadRemoteCommandHistorySummary();
        var fingerprint = $"{catalog.Commands.Count}|{history.MyPowerToolsHistoryCount}|{catalog.SourceKind}";
        if (cursor.LastEventSeq < 1)
        {
            yield return new MptModuleEvent(
                Id,
                1,
                "command.finished",
                DateTimeOffset.UtcNow,
                new JsonObject
                {
                    ["title"] = "AndroidTools remote command catalog",
                    ["message"] = $"{catalog.Commands.Count} imported command(s); {history.MyPowerToolsHistoryCount} MyPowerTools history item(s).",
                    ["commandCount"] = catalog.Commands.Count,
                    ["historyCount"] = history.MyPowerToolsHistoryCount,
                    ["source"] = catalog.SourceKind
                });
        }

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            catalog = LoadCatalog();
            history = Shared.LoadRemoteCommandHistorySummary();
            var nextFingerprint = $"{catalog.Commands.Count}|{history.MyPowerToolsHistoryCount}|{catalog.SourceKind}";
            if (string.Equals(nextFingerprint, fingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            fingerprint = nextFingerprint;
            seq++;
            yield return new MptModuleEvent(
                Id,
                seq,
                "command.finished",
                DateTimeOffset.UtcNow,
                new JsonObject
                {
                    ["title"] = "AndroidTools remote command catalog",
                    ["message"] = $"{catalog.Commands.Count} imported command(s); {history.MyPowerToolsHistoryCount} MyPowerTools history item(s).",
                    ["commandCount"] = catalog.Commands.Count,
                    ["historyCount"] = history.MyPowerToolsHistoryCount,
                    ["source"] = catalog.SourceKind
                });
        }
    }

    public override ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """
        {
          "type": "object",
          "properties": {
            "commandsYamlPath": { "type": "string", "default": "auto" },
            "defaultHost": { "type": "string", "default": "r743" },
            "shellExecutionMode": { "type": "string", "enum": ["preview", "explicit"], "default": "explicit" },
            "historyRetention": { "type": "integer", "minimum": 10, "maximum": 5000, "default": 500 }
          }
        }
        """));
    }

    public override ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, (JsonObject)_settings.DeepClone(), DateTimeOffset.UtcNow));
    }

    public override ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        _settings = SettingsJson.Merge(RemoteDefaultSettings(), snapshot.Values);
        return ValueTask.FromResult(snapshot with { Values = (JsonObject)_settings.DeepClone() });
    }

    private CommandCatalog LoadCatalog()
    {
        var path = SettingsJson.ReadString(_settings, "commandsYamlPath");
        return Shared.LoadCommandCatalog(string.Equals(path, "auto", StringComparison.OrdinalIgnoreCase) ? null : path);
    }

    private CommandExecutionResult ExecutePythonTool(CommandRequest request, PowerToolCommand command)
    {
        var input = ReadString(request.Args, "input") ?? ReadString(request.Args, "text") ?? "";
        var output = command.Command switch
        {
            "replace_host_directory" => input.Replace("/home/lixr/aosp_host_working_dir/", "http://r743.ipads-lab.se.sjtu.edu.cn:7112/", StringComparison.Ordinal),
            "remove_cpp_comments" => AndroidToolsTextTransforms.RemoveCppComments(input),
            "remove_latex_comment_lines" => string.Concat(input.SplitLines(keepLineEndings: true).Where(line => !line.TrimStart().StartsWith('%'))),
            "format_latex_comma_period_lines" => AndroidToolsTextTransforms.FormatLatexCommaPeriodLines(input),
            "add_extract_result_prefix" => string.Join('\n', input.SplitLines().Select(line => "extract_result " + line)),
            "gen_rsync_from_folders" => AndroidToolsTextTransforms.GenerateRsyncCommands(input),
            _ => ""
        };

        if (output.Length == 0 && !KnownPythonTool(command.Command))
        {
            return Failed(request, MptErrorCodes.NotFound, $"Python command tool '{command.Command}' has no C# runtime mapping.");
        }

        return Succeeded(request, new JsonObject
        {
            ["commandId"] = command.Id,
            ["tool"] = command.Command,
            ["mode"] = "csharp-port",
            ["inputLength"] = input.Length,
            ["output"] = output
        }.ToJsonString());
    }

    private async Task<CommandExecutionResult> ExecuteShellCommandAsync(CommandRequest request, PowerToolCommand command, CancellationToken cancellationToken)
    {
        var execute = ReadBool(request.Args, "execute");
        if (!execute)
        {
            return Succeeded(request, new JsonObject
            {
                ["commandId"] = command.Id,
                ["mode"] = "preview",
                ["command"] = command.Command,
                ["message"] = "Pass execute=true to run this shell command through the module runtime."
            }.ToJsonString());
        }

        if (OperatingSystem.IsWindows() && command.Command.TrimStart().StartsWith('/'))
        {
            return Failed(
                request,
                MptErrorCodes.RuntimeUnavailable,
                "This imported shell command targets a Unix path and cannot run on the current Windows host.",
                retryable: false,
                details: new JsonObject
                {
                    ["commandId"] = command.Id,
                    ["command"] = command.Command,
                    ["platform"] = "windows"
                });
        }

        var run = await Shared.RunShellCommandAsync(command.Command, TimeSpan.FromMilliseconds(Math.Max(1000, ReadInt(request.Args, "timeoutMs") ?? 120000)), cancellationToken);
        var payload = new JsonObject
        {
            ["commandId"] = command.Id,
            ["exitCode"] = run.ExitCode,
            ["stdout"] = run.Stdout,
            ["stderr"] = run.Stderr,
            ["durationMs"] = run.DurationMs,
            ["truncated"] = run.OutputTruncated,
            ["stdoutBytes"] = run.StdoutBytes,
            ["stderrBytes"] = run.StderrBytes,
            ["stdoutLines"] = run.StdoutLines,
            ["stderrLines"] = run.StderrLines,
            ["maxOutputBytesPerStream"] = AndroidToolsSharedRuntime.MaxShellOutputBytesPerStream,
            ["maxOutputLineBytes"] = AndroidToolsSharedRuntime.MaxShellOutputLineBytes
        };

        return run.ExitCode == 0
            ? Succeeded(request, payload.ToJsonString())
            : Failed(request, MptErrorCodes.RuntimeUnavailable, $"Shell command exited with code {run.ExitCode}.", retryable: true, details: payload);
    }

    private bool TryResolveShellCommand(CommandRequest request, out PowerToolCommand command, out CommandExecutionResult? failed)
    {
        command = default!;
        failed = null;
        if (!ReadBool(request.Args, "execute"))
        {
            return false;
        }

        const string prefix = "android-tools.remote-commands.run.";
        if (!request.CommandId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commandId = request.CommandId[prefix.Length..];
        command = LoadCatalog().Commands.FirstOrDefault(item => string.Equals(item.Id, commandId, StringComparison.OrdinalIgnoreCase))!;
        if (command is null)
        {
            failed = Failed(request, MptErrorCodes.NotFound, $"Powertool command '{commandId}' was not found in the imported catalog.");
            return false;
        }

        if (!string.Equals(command.Type, "shell", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (OperatingSystem.IsWindows() && command.Command.TrimStart().StartsWith('/'))
        {
            failed = Failed(
                request,
                MptErrorCodes.RuntimeUnavailable,
                "This imported shell command targets a Unix path and cannot run on the current Windows host.",
                retryable: false,
                details: new JsonObject
                {
                    ["commandId"] = command.Id,
                    ["command"] = command.Command,
                    ["platform"] = "windows"
                });
            return false;
        }

        return true;
    }

    private static bool KnownPythonTool(string name)
    {
        return name is "replace_host_directory" or "remove_cpp_comments" or "remove_latex_comment_lines" or
            "format_latex_comma_period_lines" or "add_extract_result_prefix" or "gen_rsync_from_folders";
    }

    private static JsonObject RemoteDefaultSettings()
    {
        return new JsonObject
        {
            ["commandsYamlPath"] = "auto",
            ["defaultHost"] = "r743",
            ["shellExecutionMode"] = "explicit",
            ["historyRetention"] = 500
        };
    }
}

public sealed class AndroidToolsNotificationsModule : AndroidToolsModuleBase
{
    private JsonObject? _settings;

    public override string Id => "android-tools.notifications";
    public override string DisplayName => "Remote Notifications";

    public override ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var endpoint = CurrentEndpoint();
        var checks = new[]
        {
            new HealthCheckSnapshot("notification.config", "Notification endpoint config", endpoint.Found, endpoint.Message),
            new HealthCheckSnapshot("notification.secret", "SSH request signing", endpoint.Found && Shared.LegacySshKeyExists(), Shared.LegacySshKeyExists() ? "SSH signing key is available." : "SSH signing key was not found; server pull will report auth failure."),
            new HealthCheckSnapshot("notification.history", "Local notification history", Shared.NotificationHistoryExists(), Shared.NotificationHistoryExists() ? "Legacy notification history is available." : "No legacy notification history was discovered.")
        };

        return ValueTask.FromResult(Status(endpoint.Found ? "running" : "degraded", endpoint.Message, checks));
    }

    public override ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            Command("android-tools.notifications.server.check", "Check notification server", "Probe the configured simple HTTP notification endpoint", timeoutMs: 10000),
            Command("android-tools.notifications.inbox.summary", "Summarize notification inbox", "Show local history and endpoint metadata"),
            Command("android-tools.notifications.test-event", "Create test notification", "Emit a MyPowerTools notification event")
        ];
        return ValueTask.FromResult(commands);
    }

    public override async ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return request.CommandId switch
        {
            "android-tools.notifications.server.check" => Succeeded(request, (await Shared.CheckNotificationServerAsync(CurrentEndpoint(), cancellationToken)).ToJsonString()),
            "android-tools.notifications.inbox.summary" => Succeeded(request, Shared.NotificationInboxSummary(CurrentEndpoint()).ToJsonString()),
            "android-tools.notifications.test-event" => Succeeded(request, new JsonObject
            {
                ["moduleId"] = Id,
                ["level"] = "info",
                ["title"] = "AndroidTools notification test",
                ["message"] = "Notification module emitted a test event for the Shell Notification Center."
            }.ToJsonString()),
            _ => NotFound(request)
        };
    }

    protected override async IAsyncEnumerable<MptModuleEvent> BuildModuleEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var seq = Math.Max(1UL, cursor.LastEventSeq);
        var pollInterval = TimeSpan.FromSeconds(Math.Clamp(SettingsJson.ReadInt(CurrentSettings(), "pollIntervalSeconds") ?? 30, 5, 3600));
        var endpoint = CurrentEndpoint();
        var inbox = Shared.NotificationInboxSummary(endpoint);
        var fingerprint = $"{endpoint.Found}|{endpoint.Message}|{inbox.ToJsonString()}";
        if (cursor.LastEventSeq < 1)
        {
            yield return new MptModuleEvent(
                Id,
                1,
                endpoint.Found ? "message.received" : "server.disconnected",
                DateTimeOffset.UtcNow,
                new JsonObject
                {
                    ["title"] = "AndroidTools notification endpoint",
                    ["message"] = endpoint.Message,
                    ["endpointFound"] = endpoint.Found,
                    ["legacyHistory"] = inbox["legacyHistory"]!.DeepClone(),
                    ["sshSigningKey"] = inbox["sshSigningKey"]!.DeepClone()
                });
        }

        while (true)
        {
            await Task.Delay(pollInterval, cancellationToken);
            endpoint = CurrentEndpoint();
            inbox = Shared.NotificationInboxSummary(endpoint);
            var nextFingerprint = $"{endpoint.Found}|{endpoint.Message}|{inbox.ToJsonString()}";
            if (string.Equals(nextFingerprint, fingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            fingerprint = nextFingerprint;
            seq++;
            yield return new MptModuleEvent(
                Id,
                seq,
                endpoint.Found ? "message.received" : "server.disconnected",
                DateTimeOffset.UtcNow,
                new JsonObject
                {
                    ["title"] = "AndroidTools notification endpoint",
                    ["message"] = endpoint.Message,
                    ["endpointFound"] = endpoint.Found,
                    ["legacyHistory"] = inbox["legacyHistory"]!.DeepClone(),
                    ["sshSigningKey"] = inbox["sshSigningKey"]!.DeepClone()
                });
        }
    }

    public override ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """
        {
          "type": "object",
          "properties": {
            "enabled": { "type": "boolean", "default": true },
            "serverProtocol": { "type": "string", "enum": ["http", "https"], "default": "https" },
            "serverHost": { "type": "string" },
            "serverPort": { "type": "integer", "minimum": 1, "maximum": 65535 },
            "defaultChannel": { "type": "string", "default": "default" },
            "pollIntervalSeconds": { "type": "integer", "minimum": 5, "maximum": 3600, "default": 30 },
            "tagFilter": { "type": "array", "items": { "type": "string" } }
          }
        }
        """));
    }

    public override ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, (JsonObject)CurrentSettings().DeepClone(), DateTimeOffset.UtcNow));
    }

    public override ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        _settings = SettingsJson.Merge(DefaultNotificationSettings(Shared.LoadNotificationEndpoint()), snapshot.Values);
        return ValueTask.FromResult(snapshot with { Values = (JsonObject)_settings.DeepClone() });
    }

    private JsonObject CurrentSettings()
    {
        return _settings ?? DefaultNotificationSettings(Shared.LoadNotificationEndpoint());
    }

    private NotificationEndpoint CurrentEndpoint()
    {
        var settings = CurrentSettings();
        var protocol = SettingsJson.ReadString(settings, "serverProtocol") ?? "https";
        var host = SettingsJson.ReadString(settings, "serverHost") ?? "";
        var port = SettingsJson.ReadInt(settings, "serverPort") ?? 0;
        return string.IsNullOrWhiteSpace(host) || port <= 0
            ? NotificationEndpoint.Missing(SettingsJson.ReadString(settings, "configSourceMessage") ?? "Notification endpoint is not configured in Host settings or package-shared config.")
            : new NotificationEndpoint(true, protocol, host, port, $"Endpoint {protocol}://{host}:{port} imported from Host settings.");
    }

    private static JsonObject DefaultNotificationSettings(NotificationEndpoint endpoint)
    {
        return new JsonObject
        {
            ["enabled"] = true,
            ["serverProtocol"] = endpoint.Protocol,
            ["serverHost"] = endpoint.Host,
            ["serverPort"] = endpoint.Port,
            ["configSourceMessage"] = endpoint.Message,
            ["defaultChannel"] = "default",
            ["pollIntervalSeconds"] = 30,
            ["tagFilter"] = new JsonArray()
        };
    }
}

public sealed class AndroidToolsProcessMonitorModule : AndroidToolsModuleBase
{
    private JsonObject? _settings;

    public override string Id => "android-tools.process-monitor";
    public override string DisplayName => "Process Monitor";

    public override ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var processes = CurrentWatchList();
        var states = Shared.CheckProcesses(processes.Names);
        var checks = new[]
        {
            new HealthCheckSnapshot("process.config", "Monitored process list", processes.Names.Count > 0, processes.Message),
            new HealthCheckSnapshot("process.scan", "Process scan", true, $"{states.Count(item => item.Running)} of {states.Count} configured process name(s) currently have running instances.")
        };

        return ValueTask.FromResult(Status(processes.Names.Count > 0 ? "running" : "degraded", processes.Message, checks));
    }

    public override ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            Command("android-tools.process-monitor.status.summary", "Summarize monitored processes", "Scan configured process names and report current instance counts"),
            Command("android-tools.process-monitor.watch.list", "List process watch configuration", "Read imported or MyPowerTools process monitor configuration"),
            Command("android-tools.process-monitor.watch.save", "Save process watch configuration", "Persist process names to the shared AndroidTools runtime data directory")
        ];
        return ValueTask.FromResult(commands);
    }

    public override ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (request.CommandId == "android-tools.process-monitor.status.summary")
        {
            var processes = CurrentWatchList();
            return ValueTask.FromResult(Succeeded(request, new JsonObject
            {
                ["source"] = processes.SourceKind,
                ["configured"] = ToJsonArray(processes.Names),
                ["states"] = ToJsonArray(Shared.CheckProcesses(processes.Names), state => state.ToJson())
            }.ToJsonString()));
        }

        if (request.CommandId == "android-tools.process-monitor.watch.list")
        {
            return ValueTask.FromResult(Succeeded(request, CurrentWatchList().ToJson().ToJsonString()));
        }

        if (request.CommandId == "android-tools.process-monitor.watch.save")
        {
            var names = ReadStringArray(request.Args, "processes");
            if (names.Count == 0)
            {
                return ValueTask.FromResult(Failed(request, MptErrorCodes.ValidationFailed, "processes must contain at least one process name."));
            }

            Shared.SaveProcessWatchList(names);
            return ValueTask.FromResult(Succeeded(request, new JsonObject
            {
                ["saved"] = names.Count,
                ["processes"] = ToJsonArray(names)
            }.ToJsonString()));
        }

        return ValueTask.FromResult(NotFound(request));
    }

    protected override async IAsyncEnumerable<MptModuleEvent> BuildModuleEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var seq = Math.Max(1UL, cursor.LastEventSeq);
        var pollInterval = TimeSpan.FromSeconds(Math.Clamp(SettingsJson.ReadInt(CurrentSettings(), "scanIntervalSeconds") ?? 20, 5, 3600));
        var processes = CurrentWatchList();
        var states = Shared.CheckProcesses(processes.Names);
        var runningCount = states.Count(state => state.Running);
        var fingerprint = $"{processes.SourceKind}|{string.Join(",", states.Select(state => $"{state.Name}:{state.Running}"))}";
        if (cursor.LastEventSeq < 1)
        {
            yield return new MptModuleEvent(
                Id,
                1,
                runningCount > 0 ? "process.started" : "watch.alert",
                DateTimeOffset.UtcNow,
                new JsonObject
                {
                    ["title"] = "AndroidTools process watch",
                    ["message"] = processes.Names.Count == 0 ? processes.Message : $"{runningCount} of {states.Count} watched process name(s) are running.",
                    ["configuredCount"] = processes.Names.Count,
                    ["runningCount"] = runningCount,
                    ["source"] = processes.SourceKind
                });
        }

        while (true)
        {
            await Task.Delay(pollInterval, cancellationToken);
            processes = CurrentWatchList();
            states = Shared.CheckProcesses(processes.Names);
            runningCount = states.Count(state => state.Running);
            var nextFingerprint = $"{processes.SourceKind}|{string.Join(",", states.Select(state => $"{state.Name}:{state.Running}"))}";
            if (string.Equals(nextFingerprint, fingerprint, StringComparison.Ordinal))
            {
                continue;
            }

            fingerprint = nextFingerprint;
            seq++;
            yield return new MptModuleEvent(
                Id,
                seq,
                runningCount > 0 ? "process.started" : "watch.alert",
                DateTimeOffset.UtcNow,
                new JsonObject
                {
                    ["title"] = "AndroidTools process watch",
                    ["message"] = processes.Names.Count == 0 ? processes.Message : $"{runningCount} of {states.Count} watched process name(s) are running.",
                    ["configuredCount"] = processes.Names.Count,
                    ["runningCount"] = runningCount,
                    ["source"] = processes.SourceKind
                });
        }
    }

    public override ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """
        {
          "type": "object",
          "properties": {
            "enabled": { "type": "boolean", "default": true },
            "processes": { "type": "array", "items": { "type": "string" } },
            "scanIntervalSeconds": { "type": "integer", "minimum": 5, "maximum": 3600, "default": 20 },
            "alertWhenFound": { "type": "boolean", "default": true }
          }
        }
        """));
    }

    public override ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, (JsonObject)CurrentSettings().DeepClone(), DateTimeOffset.UtcNow));
    }

    public override ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        var merged = SettingsJson.Merge(DefaultProcessSettings(Shared.LoadProcessWatchList()), snapshot.Values);
        _settings = merged;
        var names = SettingsJson.ReadStringArray(merged, "processes");
        if (names.Count > 0)
        {
            Shared.SaveProcessWatchList(names);
        }

        return ValueTask.FromResult(snapshot with { Values = (JsonObject)merged.DeepClone() });
    }

    private ProcessWatchList CurrentWatchList()
    {
        if (_settings is null)
        {
            return Shared.LoadProcessWatchList();
        }

        var names = SettingsJson.ReadStringArray(_settings, "processes");
        return names.Count == 0
            ? new ProcessWatchList([], "host-settings", "No processes are configured in Host settings.")
            : new ProcessWatchList(names, "host-settings", $"{names.Count} process name(s) configured in Host settings.");
    }

    private JsonObject CurrentSettings()
    {
        return _settings ?? DefaultProcessSettings(Shared.LoadProcessWatchList());
    }

    private static JsonObject DefaultProcessSettings(ProcessWatchList list)
    {
        return new JsonObject
        {
            ["enabled"] = true,
            ["processes"] = ToJsonArray(list.Names),
            ["scanIntervalSeconds"] = 20,
            ["alertWhenFound"] = true
        };
    }
}

public abstract class AndroidToolsModuleBase : IMptModule
{
    private ModuleContext? _context;
    private AndroidToolsSharedRuntime? _shared;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public string PackageId => "android-tools-suite";
    public Version Version => new(0, 2, 0);

    protected AndroidToolsSharedRuntime Shared => _shared ?? throw new InvalidOperationException("Module was not initialized.");

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _shared = AndroidToolsSharedRuntime.Get(context);
        Directory.CreateDirectory(context.DataDirectory);
        Directory.CreateDirectory(context.CacheDirectory);
        Directory.CreateDirectory(context.LogDirectory);
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status", "commands", "settings", "logs", "dashboardCard", "detailPage"]));
    }

    public abstract ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken);
    public abstract ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken);
    public abstract ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken);

    public virtual async IAsyncEnumerable<CommandExecutionEvent> ExecuteCommandStreamAsync(CommandRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await ExecuteCommandAsync(request, cancellationToken);
        yield return FinalEvent(result);
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in BuildModuleEventsAsync(cursor, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return evt;
        }
    }

    protected virtual async IAsyncEnumerable<MptModuleEvent> BuildModuleEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (cursor.LastEventSeq >= 1)
        {
            yield break;
        }

        var status = await GetStatusAsync(cancellationToken);
        yield return new MptModuleEvent(
            Id,
            1,
            status.State == "running" ? "module.running" : "module.degraded",
            DateTimeOffset.UtcNow,
            new JsonObject
            {
                ["title"] = DisplayName,
                ["message"] = status.Summary,
                ["state"] = status.State
            });
    }

    public virtual ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """{"type":"object","properties":{"enabled":{"type":"boolean","default":true}}}"""));
    }

    public virtual ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, new JsonObject { ["enabled"] = true }, DateTimeOffset.UtcNow));
    }

    public virtual ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsValidationResult(true, []));
    }

    public virtual ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(snapshot);
    }

    public virtual ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new($"{Id}.dashboard", "dashboard-card", DisplayName, new JsonObject { ["moduleId"] = Id }),
            new($"{Id}.detail", "detail-page", DisplayName, new JsonObject { ["moduleId"] = Id })
        ];
        return ValueTask.FromResult(surfaces);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    protected ModuleStatusSnapshot Status(string state, string summary, IReadOnlyList<HealthCheckSnapshot> checks)
    {
        return new ModuleStatusSnapshot(Id, state, summary, DateTimeOffset.UtcNow, checks, 0);
    }

    protected MptCommandDescriptor Command(
        string id,
        string title,
        string subtitle,
        int timeoutMs = 30000,
        JsonObject? execution = null,
        IReadOnlyList<string>? constraints = null,
        IReadOnlyList<CommandParameterDescriptor>? parameters = null,
        bool supportsProgress = false,
        bool supportsCancellation = false)
    {
        var supportsStreaming = constraints?.Contains(MptOperationConstraints.RequiresLongRunningLoop) == true;
        return new MptCommandDescriptor(
            id,
            Id,
            title,
            subtitle,
            "action",
            Category: "Android Tools",
            TimeoutMs: timeoutMs,
            Execution: execution,
            Parameters: parameters,
            Constraints: constraints,
            SupportsProgress: supportsProgress || supportsStreaming,
            SupportsCancellation: supportsCancellation || supportsStreaming);
    }

    protected static CommandExecutionResult Succeeded(CommandRequest request, string output)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
    }

    protected static CommandExecutionEvent FinalEvent(CommandExecutionResult result, int sequence = 1)
    {
        return new CommandExecutionEvent(
            result.InvocationId,
            result.CommandId,
            result.State,
            result.Success ? result.Output : result.Error?.Message ?? "Command failed.",
            sequence,
            true,
            result);
    }

    protected static CommandExecutionResult NotFound(CommandRequest request)
    {
        return Failed(request, MptErrorCodes.NotFound, $"Command '{request.CommandId}' is not implemented by AndroidTools.");
    }

    protected static CommandExecutionResult Failed(CommandRequest request, string code, string message, bool retryable = false, JsonObject? details = null)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "failed", false, "", new MptRuntimeError(code, message, retryable, details));
    }

    protected static string? ReadString(JsonObject args, string key)
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

    protected static bool ReadBool(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return false;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    protected static int? ReadInt(JsonObject args, string key)
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
            catch (Exception)
            {
                return null;
            }
        }
    }

    protected static IReadOnlyList<string> ReadStringArray(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(item =>
            {
                try
                {
                    return item?.GetValue<string>() ?? "";
                }
                catch (InvalidOperationException)
                {
                    return "";
                }
            })
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    public static JsonArray ToJsonArray<T>(IEnumerable<T> values, Func<T, JsonNode> map)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(map(value));
        }

        return array;
    }
}

public sealed class AndroidToolsSharedRuntime
{
    internal const int ShellStreamChannelCapacity = 256;
    internal const int MaxShellStreamLineEvents = 1000;
    internal const int MaxShellOutputBytesPerStream = 256 * 1024;
    internal const int MaxShellOutputLineBytes = 4096;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, AndroidToolsSharedRuntime> Runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(4) };

    private AndroidToolsSharedRuntime(ModuleContext context)
    {
        PackageRoot = ResolvePackageRoot();
        SharedRoot = ResolveSharedStateRoot(context);
        DataRoot = Path.Combine(SharedRoot, "data");
        CacheRoot = Path.Combine(SharedRoot, "cache");
        LogRoot = Path.Combine(SharedRoot, "logs");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(LogRoot);
    }

    internal string PackageRoot { get; }
    internal string SharedRoot { get; }
    internal string DataRoot { get; }
    internal string CacheRoot { get; }
    internal string LogRoot { get; }

    internal static AndroidToolsSharedRuntime Get(ModuleContext context)
    {
        lock (Gate)
        {
            var packageRoot = ResolvePackageRoot();
            var sharedRoot = ResolveSharedStateRoot(context);
            var key = packageRoot + "|" + sharedRoot;
            if (!Runtimes.TryGetValue(key, out var runtime))
            {
                runtime = new AndroidToolsSharedRuntime(context);
                Runtimes[key] = runtime;
            }

            return runtime;
        }
    }

    internal CommandCatalog LoadCommandCatalog(string? configuredPath = null)
    {
        var source = FindFirstExisting(CommandsYamlCandidates(configuredPath));
        if (source.Path is null)
        {
            return new CommandCatalog([], "commands.yaml was not found in configured, package, or discovered legacy locations.", "missing");
        }

        try
        {
            var commands = NarrowYamlCommandParser.ParseCommands(File.ReadAllText(source.Path));
            var pyCount = commands.Count(command => command.Type == "py");
            return new CommandCatalog(
                commands,
                $"{commands.Count} command(s) imported from {source.SourceKind}.",
                source.SourceKind,
                pyCount);
        }
        catch (Exception ex)
        {
            return new CommandCatalog([], $"commands.yaml import failed: {MptLogRedactor.Redact(ex.Message)}", source.SourceKind);
        }
    }

    internal RemoteCommandHistorySummary LoadRemoteCommandHistorySummary()
    {
        var mptHistory = Path.Combine(DataRoot, "remote-command-history.jsonl");
        var legacy = FindFirstExisting(LegacyFileCandidates("powertool", "history.db"));
        var mptCount = File.Exists(mptHistory) ? File.ReadLines(mptHistory).Count() : 0;
        if (legacy.Path is null)
        {
            return new RemoteCommandHistorySummary(mptCount > 0, mptCount, false, "No legacy powertool history.db was discovered.");
        }

        var size = new FileInfo(legacy.Path).Length;
        return new RemoteCommandHistorySummary(true, mptCount, true, $"Legacy history.db discovered from {legacy.SourceKind}; {size} bytes.");
    }

    internal void AppendRemoteCommandHistory(PowerToolCommand command, CommandExecutionResult result)
    {
        var path = Path.Combine(DataRoot, "remote-command-history.jsonl");
        var entry = new JsonObject
        {
            ["time"] = DateTimeOffset.UtcNow.ToString("O"),
            ["commandId"] = command.Id,
            ["type"] = command.Type,
            ["success"] = result.Success,
            ["state"] = result.State,
            ["errorCode"] = result.Error?.Code ?? ""
        };

        File.AppendAllText(path, entry.ToJsonString() + Environment.NewLine);
    }

    internal NotificationEndpoint LoadNotificationEndpoint()
    {
        var source = FindFirstExisting(NotificationConfigCandidates());
        if (source.Path is null)
        {
            return NotificationEndpoint.Missing("simple_http_notification_conf.py was not discovered.");
        }

        var text = File.ReadAllText(source.Path);
        var protocol = MatchStringAssignment(text, "cloud_server_protocol") ?? "https";
        var host = MatchStringAssignment(text, "cloud_server_ip") ?? "";
        var port = MatchIntAssignment(text, "cloud_server_port") ?? 0;

        var userOverride = Path.Combine(Path.GetDirectoryName(source.Path)!, "simple_http_notification_conf_user.yaml");
        if (File.Exists(userOverride))
        {
            foreach (var pair in NarrowYamlCommandParser.ParseFlatMap(File.ReadAllText(userOverride)))
            {
                if (pair.Key == "cloud_server_protocol")
                {
                    protocol = pair.Value;
                }
                else if (pair.Key == "cloud_server_ip")
                {
                    host = pair.Value;
                }
                else if (pair.Key == "cloud_server_port" && int.TryParse(pair.Value, out var parsed))
                {
                    port = parsed;
                }
            }
        }

        return string.IsNullOrWhiteSpace(host) || port <= 0
            ? NotificationEndpoint.Missing("Notification endpoint config was parsed but host or port is missing.")
            : new NotificationEndpoint(true, protocol, host, port, $"Endpoint {protocol}://{host}:{port} imported from {source.SourceKind}.");
    }

    internal async Task<JsonObject> CheckNotificationServerAsync(CancellationToken cancellationToken)
    {
        return await CheckNotificationServerAsync(LoadNotificationEndpoint(), cancellationToken);
    }

    internal async Task<JsonObject> CheckNotificationServerAsync(NotificationEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (!endpoint.Found)
        {
            return endpoint.ToJson();
        }

        var uri = $"{endpoint.Protocol}://{endpoint.Host}:{endpoint.Port}/pull?channel=mpt-self-test";
        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            return new JsonObject
            {
                ["endpoint"] = endpoint.RedactedUri,
                ["httpStatus"] = (int)response.StatusCode,
                ["state"] = response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "reachable" : "degraded",
                ["message"] = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Server is reachable and requires signed pull authentication."
                    : $"Server returned {(int)response.StatusCode}."
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new JsonObject
            {
                ["endpoint"] = endpoint.RedactedUri,
                ["state"] = "degraded",
                ["message"] = MptLogRedactor.Redact(ex.Message)
            };
        }
    }

    internal JsonObject NotificationInboxSummary()
    {
        return NotificationInboxSummary(LoadNotificationEndpoint());
    }

    internal JsonObject NotificationInboxSummary(NotificationEndpoint endpoint)
    {
        return new JsonObject
        {
            ["endpoint"] = endpoint.ToJson(),
            ["sshSigningKey"] = LegacySshKeyExists() ? "available" : "missing",
            ["legacyHistory"] = NotificationHistoryExists() ? "available" : "missing"
        };
    }

    internal bool LegacySshKeyExists()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return File.Exists(Path.Combine(profile, ".ssh", "id_ed25519"));
    }

    internal bool NotificationHistoryExists()
    {
        return FindFirstExisting(LegacyFileCandidates("powertool", "history.db")).Path is not null;
    }

    internal ProcessWatchList LoadProcessWatchList()
    {
        var source = FindFirstExisting(ProcessListCandidates());
        if (source.Path is null)
        {
            return new ProcessWatchList([], "missing", "No processes.json was found. Save a watch list through android-tools.process-monitor.watch.save.");
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(source.Path));
            var names = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? "")
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
            return new ProcessWatchList(names, source.SourceKind, names.Length == 0 ? "processes.json is empty." : $"{names.Length} monitored process name(s) loaded from {source.SourceKind}.");
        }
        catch (Exception ex)
        {
            return new ProcessWatchList([], source.SourceKind, $"processes.json parse failed: {MptLogRedactor.Redact(ex.Message)}");
        }
    }

    internal void SaveProcessWatchList(IReadOnlyList<string> processNames)
    {
        var path = Path.Combine(DataRoot, "processes.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(processNames, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    internal IReadOnlyList<ProcessStateSnapshot> CheckProcesses(IReadOnlyList<string> names)
    {
        var states = new List<ProcessStateSnapshot>();
        foreach (var name in names)
        {
            var processName = Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrWhiteSpace(processName))
            {
                continue;
            }

            try
            {
                var count = Process.GetProcessesByName(processName).Length;
                states.Add(new ProcessStateSnapshot(name, count));
            }
            catch (Exception ex)
            {
                states.Add(new ProcessStateSnapshot(name, 0, MptLogRedactor.Redact(ex.Message)));
            }
        }

        return states;
    }

    internal async Task<ShellRunResult> RunShellCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        var stdout = new BoundedShellOutput(MaxShellOutputBytesPerStream);
        var stderr = new BoundedShellOutput(MaxShellOutputBytesPerStream);
        Task? stdoutTask = null;
        Task? stderrTask = null;

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "pwsh.exe" : "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command);
        }

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
            {
                return new ShellRunResult(-1, "", "Process could not be started.", started.ElapsedMilliseconds);
            }

            stdoutTask = PumpCapturedLinesAsync(process.StandardOutput, stdout, linked.Token);
            stderrTask = PumpCapturedLinesAsync(process.StandardError, stderr, linked.Token);
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            return new ShellRunResult(
                process.ExitCode,
                Trim(stdout.Text),
                Trim(stderr.Text),
                started.ElapsedMilliseconds,
                stdout.Truncated || stderr.Truncated,
                stdout.TotalBytes,
                stderr.TotalBytes,
                stdout.LineCount,
                stderr.LineCount);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return new ShellRunResult(-1, "", $"{psi.FileName} executable was not found on PATH.", started.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await ObserveCaptureCompletionAsync(stdoutTask, stderrTask);
            var message = cancellationToken.IsCancellationRequested
                ? "Shell command was cancelled."
                : $"Shell command timed out after {timeout.TotalSeconds:n0}s.";
            return new ShellRunResult(-1, "", message, started.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new ShellRunResult(-1, "", MptLogRedactor.Redact(ex.Message), started.ElapsedMilliseconds);
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal async IAsyncEnumerable<CommandExecutionEvent> RunShellCommandStreamAsync(
        CommandRequest request,
        PowerToolCommand command,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<CommandExecutionEvent>(new BoundedChannelOptions(ShellStreamChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _ = Task.Run(async () =>
        {
            var sequence = 0;
            var started = Stopwatch.StartNew();
            var stdout = new BoundedShellOutput(MaxShellOutputBytesPerStream);
            var stderr = new BoundedShellOutput(MaxShellOutputBytesPerStream);
            var eventLimiter = new ShellStreamEventLimiter(MaxShellStreamLineEvents);
            Process? process = null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);

            try
            {
                await channel.Writer.WriteAsync(new CommandExecutionEvent(
                    request.InvocationId,
                    request.CommandId,
                    "progress",
                    $"Starting shell command '{command.Id}'.",
                    NextSequence(),
                    false), cancellationToken);

                var psi = CreateShellProcessStartInfo(command.Command);
                process = Process.Start(psi);
                if (process is null)
                {
                    await WriteFinalAsync(new ShellRunResult(-1, "", "Process could not be started.", started.ElapsedMilliseconds));
                    return;
                }

                var stdoutTask = PumpLinesAsync(process.StandardOutput, "stdout", stdout, eventLimiter, channel.Writer, request, NextSequence, linked.Token);
                var stderrTask = PumpLinesAsync(process.StandardError, "stderr", stderr, eventLimiter, channel.Writer, request, NextSequence, linked.Token);
                await process.WaitForExitAsync(linked.Token);
                await Task.WhenAll(stdoutTask, stderrTask);
                await WriteFinalAsync(new ShellRunResult(
                    process.ExitCode,
                    Trim(stdout.Text),
                    Trim(stderr.Text),
                    started.ElapsedMilliseconds,
                    stdout.Truncated || stderr.Truncated || eventLimiter.Truncated,
                    stdout.TotalBytes,
                    stderr.TotalBytes,
                    stdout.LineCount,
                    stderr.LineCount));
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                await WriteFinalAsync(new ShellRunResult(-1, "", $"{ShellFileName()} executable was not found on PATH.", started.ElapsedMilliseconds));
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    await WriteCancelledAsync("Shell command was cancelled.");
                }
                else
                {
                    await WriteFinalAsync(new ShellRunResult(-1, "", $"Shell command timed out after {timeout.TotalSeconds:n0}s.", started.ElapsedMilliseconds));
                }
            }
            catch (Exception ex)
            {
                await WriteFinalAsync(new ShellRunResult(-1, "", MptLogRedactor.Redact(ex.Message), started.ElapsedMilliseconds));
            }
            finally
            {
                process?.Dispose();
                channel.Writer.TryComplete();
            }

            async Task WriteFinalAsync(ShellRunResult run)
            {
                var payload = new JsonObject
                {
                    ["commandId"] = command.Id,
                    ["exitCode"] = run.ExitCode,
                    ["stdout"] = run.Stdout,
                    ["stderr"] = run.Stderr,
                    ["durationMs"] = run.DurationMs,
                    ["truncated"] = run.OutputTruncated,
                    ["stdoutBytes"] = run.StdoutBytes,
                    ["stderrBytes"] = run.StderrBytes,
                    ["stdoutLines"] = run.StdoutLines,
                    ["stderrLines"] = run.StderrLines,
                    ["maxStreamLineEvents"] = MaxShellStreamLineEvents,
                    ["maxOutputBytesPerStream"] = MaxShellOutputBytesPerStream,
                    ["maxOutputLineBytes"] = MaxShellOutputLineBytes
                };
                var result = run.ExitCode == 0
                    ? new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, payload.ToJsonString())
                    : new CommandExecutionResult(
                        request.InvocationId,
                        request.CommandId,
                        "failed",
                        false,
                        "",
                        new MptRuntimeError(MptErrorCodes.RuntimeUnavailable, $"Shell command exited with code {run.ExitCode}.", true, payload));
                await channel.Writer.WriteAsync(new CommandExecutionEvent(
                    request.InvocationId,
                    request.CommandId,
                    result.State,
                    result.Success ? result.Output : result.Error?.Message ?? "Command failed.",
                    NextSequence(),
                    true,
                    result), CancellationToken.None);
            }

            async Task WriteCancelledAsync(string message)
            {
                var result = new CommandExecutionResult(
                    request.InvocationId,
                    request.CommandId,
                    "cancelled",
                    false,
                    "",
                    new MptRuntimeError(MptErrorCodes.CommandCancelled, message));
                await channel.Writer.WriteAsync(new CommandExecutionEvent(
                    request.InvocationId,
                    request.CommandId,
                    result.State,
                    message,
                    NextSequence(),
                    true,
                    result), CancellationToken.None);
            }

            int NextSequence()
            {
                return Interlocked.Increment(ref sequence);
            }
        }, CancellationToken.None);

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }

    private IEnumerable<DiscoveredFile> CommandsYamlCandidates(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return new DiscoveredFile(configuredPath, "host-settings");
        }

        var env = Environment.GetEnvironmentVariable("MPT_ANDROIDTOOLS_COMMANDS");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return new DiscoveredFile(env, "env:MPT_ANDROIDTOOLS_COMMANDS");
        }

        yield return new DiscoveredFile(Path.Combine(DataRoot, "commands.yaml"), "mpt-shared-data");
        yield return new DiscoveredFile(Path.Combine(PackageRoot, "shared", "powertool", "commands.yaml"), "package-shared");
        foreach (var file in LegacyFileCandidates("powertool", "commands.yaml"))
        {
            yield return file;
        }
    }

    private IEnumerable<DiscoveredFile> ProcessListCandidates()
    {
        var env = Environment.GetEnvironmentVariable("MPT_ANDROIDTOOLS_PROCESSES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return new DiscoveredFile(env, "env:MPT_ANDROIDTOOLS_PROCESSES");
        }

        yield return new DiscoveredFile(Path.Combine(DataRoot, "processes.json"), "mpt-shared-data");
        yield return new DiscoveredFile(Path.Combine(PackageRoot, "shared", "powertool", "processes.json"), "package-shared");
        foreach (var file in LegacyFileCandidates("powertool", "processes.json"))
        {
            yield return file;
        }
    }

    private IEnumerable<DiscoveredFile> NotificationConfigCandidates()
    {
        var env = Environment.GetEnvironmentVariable("MPT_ANDROIDTOOLS_NOTIFICATION_CONF");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return new DiscoveredFile(env, "env:MPT_ANDROIDTOOLS_NOTIFICATION_CONF");
        }

        yield return new DiscoveredFile(Path.Combine(PackageRoot, "shared", "powertool", "simple_http_notification_conf.py"), "package-shared");
        foreach (var file in LegacyFileCandidates("py_modules", "simple_http_notification_conf.py"))
        {
            yield return file;
        }
    }

    private IEnumerable<DiscoveredFile> LegacyFileCandidates(string relativeDirectory, string fileName)
    {
        var root = FindRepositoryRoot(PackageRoot);
        if (root is not null)
        {
            var repoParent = Directory.GetParent(root)?.FullName;
            if (!string.IsNullOrWhiteSpace(repoParent))
            {
                yield return new DiscoveredFile(Path.Combine(repoParent, "androidtools", relativeDirectory, fileName), "discovered-androidtools-repo");
            }
        }
    }

    private static DiscoveredFile FindFirstExisting(IEnumerable<DiscoveredFile> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Path) && File.Exists(candidate.Path))
            {
                return candidate;
            }
        }

        return new DiscoveredFile(null, "missing");
    }

    private static string ResolvePackageRoot()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(AndroidToolsSharedRuntime).Assembly.Location) ?? AppContext.BaseDirectory;
        var repoRoot = FindRepositoryRoot(assemblyDirectory);
        if (repoRoot is not null)
        {
            var packageRoot = Path.Combine(repoRoot, "modules", "android-tools-suite");
            if (File.Exists(Path.Combine(packageRoot, "package.json")))
            {
                return packageRoot;
            }
        }

        return assemblyDirectory;
    }

    private static string ResolveSharedStateRoot(ModuleContext context)
    {
        var data = new DirectoryInfo(context.DataDirectory);
        var moduleRoot = data.Parent;
        var modulesRoot = moduleRoot?.Parent;
        var stateRoot = modulesRoot?.Parent?.FullName;
        if (string.IsNullOrWhiteSpace(stateRoot))
        {
            stateRoot = Path.Combine(Path.GetTempPath(), "MyPowerTools", "state");
        }

        return Path.Combine(stateRoot, "packages", context.PackageId);
    }

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? MatchStringAssignment(string text, string key)
    {
        var match = Regex.Match(text, $@"{Regex.Escape(key)}\s*:\s*str\s*=\s*[""'](?<value>[^""']+)[""']", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static int? MatchIntAssignment(string text, string key)
    {
        var match = Regex.Match(text, $@"{Regex.Escape(key)}\s*:\s*int\s*=\s*(?<value>\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["value"].Value, out var value) ? value : null;
    }

    private static ProcessStartInfo CreateShellProcessStartInfo(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ShellFileName(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command);
        }

        return psi;
    }

    private static string ShellFileName()
    {
        return OperatingSystem.IsWindows() ? "pwsh.exe" : "/bin/sh";
    }

    private static void KillProcessTree(Process? process)
    {
        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception)
        {
            try
            {
                process.Kill();
            }
            catch
            {
            }
        }
    }

    private static async Task PumpLinesAsync(
        TextReader reader,
        string state,
        BoundedShellOutput sink,
        ShellStreamEventLimiter eventLimiter,
        ChannelWriter<CommandExecutionEvent> writer,
        CommandRequest request,
        Func<int> nextSequence,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var redacted = MptLogRedactor.Redact(line);
            var eventLine = sink.AppendLine(redacted, MaxShellOutputLineBytes, out var lineTruncated);
            if (eventLimiter.TryReserveLineEvent())
            {
                await writer.WriteAsync(new CommandExecutionEvent(
                    request.InvocationId,
                    request.CommandId,
                    state,
                    eventLine,
                    nextSequence(),
                    false), cancellationToken);
            }

            if ((lineTruncated || sink.Truncated || eventLimiter.Truncated) && eventLimiter.TryReserveTruncationEvent())
            {
                await writer.WriteAsync(new CommandExecutionEvent(
                    request.InvocationId,
                    request.CommandId,
                    "output.truncated",
                    "Shell command output exceeded the streaming capture limits; remaining output is drained and summarized in the final result.",
                    nextSequence(),
                    false), cancellationToken);
            }
        }
    }

    private static async Task PumpCapturedLinesAsync(
        TextReader reader,
        BoundedShellOutput sink,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            _ = sink.AppendLine(MptLogRedactor.Redact(line), MaxShellOutputLineBytes, out _);
        }
    }

    private static async Task ObserveCaptureCompletionAsync(params Task?[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }
    }

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 4000 ? value : value[..4000] + "...";
    }
}

internal static class NarrowYamlCommandParser
{
    public static IReadOnlyList<PowerToolCommand> ParseCommands(string text)
    {
        var commands = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        var inCommands = false;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (!char.IsWhiteSpace(raw[0]) && trimmed.EndsWith(':'))
            {
                var section = trimmed[..^1];
                inCommands = section == "commands";
                current = null;
                continue;
            }

            if (!inCommands)
            {
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                commands.Add(current);
                ParseKeyValue(trimmed[2..], current);
                continue;
            }

            if (current is not null)
            {
                ParseKeyValue(trimmed, current);
            }
        }

        return commands
            .Select(item => new PowerToolCommand(
                item.GetValueOrDefault("id", ""),
                item.GetValueOrDefault("label", item.GetValueOrDefault("id", "")),
                item.GetValueOrDefault("command", ""),
                item.GetValueOrDefault("description", ""),
                item.GetValueOrDefault("type", "shell")))
            .Where(command => !string.IsNullOrWhiteSpace(command.Id) && !string.IsNullOrWhiteSpace(command.Command))
            .ToArray();
    }

    public static IReadOnlyDictionary<string, string> ParseFlatMap(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = raw.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            ParseKeyValue(trimmed, map);
        }

        return map;
    }

    private static void ParseKeyValue(string text, Dictionary<string, string> target)
    {
        var index = text.IndexOf(':', StringComparison.Ordinal);
        if (index <= 0)
        {
            return;
        }

        var key = text[..index].Trim();
        var value = text[(index + 1)..].Trim();
        if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }

        target[key] = value;
    }
}

internal static class AndroidToolsTextTransforms
{
    private const string PostconditionsDbRsync =
        "rsync -avP r743-autodroid:/home/lxr2/repo/androidtools/AutoDroid/data/postconditions_db/ $AutoDroid/data/postconditions_db/";

    public static string GenerateRsyncCommands(string lines)
    {
        var rsync = string.Join('\n',
            lines.SplitLines()
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(line => "rsync -avP r743-autodroid:" + line + " $aosp_host_working_dir/"));
        return rsync + '\n' + PostconditionsDbRsync;
    }

    public static string FormatLatexCommaPeriodLines(string text)
    {
        var builder = new StringBuilder();
        var previousWasWhitespace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                previousWasWhitespace = true;
                continue;
            }

            if (previousWasWhitespace && builder.Length > 0 && builder[^1] != '\n')
            {
                builder.Append(' ');
            }

            previousWasWhitespace = false;
            builder.Append(ch);
            if (ch is ',' or '.')
            {
                while (builder.Length > 0 && builder[^1] == ' ')
                {
                    builder.Length--;
                }

                builder.Append('\n');
            }
        }

        var result = string.Join('\n', builder.ToString().SplitLines().Select(line => line.TrimEnd())).Trim();
        return result + (text.EndsWith('\n') ? "\n" : "");
    }

    public static string RemoveCppComments(string source)
    {
        var result = new StringBuilder();
        var state = "normal";
        var quote = '\0';

        for (var i = 0; i < source.Length;)
        {
            var ch = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (state == "normal")
            {
                if (ch is '"' or '\'')
                {
                    result.Append(ch);
                    quote = ch;
                    state = "string";
                    i++;
                }
                else if (ch == '/' && next == '/')
                {
                    state = "line-comment";
                    i += 2;
                }
                else if (ch == '/' && next == '*')
                {
                    state = "block-comment";
                    i += 2;
                }
                else
                {
                    result.Append(ch);
                    i++;
                }
            }
            else if (state == "string")
            {
                result.Append(ch);
                if (ch == '\\' && i + 1 < source.Length)
                {
                    result.Append(source[i + 1]);
                    i += 2;
                }
                else if (ch == quote)
                {
                    state = "normal";
                    i++;
                }
                else
                {
                    i++;
                }
            }
            else if (state == "line-comment")
            {
                if (ch == '\r' && next == '\n')
                {
                    result.Append("\r\n");
                    state = "normal";
                    i += 2;
                }
                else if (ch is '\r' or '\n')
                {
                    result.Append(ch);
                    state = "normal";
                    i++;
                }
                else
                {
                    i++;
                }
            }
            else if (state == "block-comment")
            {
                if (ch == '*' && next == '/')
                {
                    state = "normal";
                    i += 2;
                }
                else if (ch == '\r' && next == '\n')
                {
                    result.Append("\r\n");
                    i += 2;
                }
                else if (ch is '\r' or '\n')
                {
                    result.Append(ch);
                    i++;
                }
                else
                {
                    i++;
                }
            }
        }

        return result.ToString();
    }
}

internal sealed record PowerToolCommand(string Id, string Label, string Command, string Description, string Type);

internal sealed record CommandCatalog(
    IReadOnlyList<PowerToolCommand> Commands,
    string Summary,
    string SourceKind,
    int PythonToolCount = 0)
{
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["summary"] = Summary,
            ["source"] = SourceKind,
            ["count"] = Commands.Count,
            ["commands"] = AndroidToolsModuleBase.ToJsonArray(Commands, command => new JsonObject
            {
                ["id"] = command.Id,
                ["label"] = command.Label,
                ["description"] = command.Description,
                ["type"] = command.Type,
                ["command"] = command.Command
            })
        };
    }
}

internal sealed record RemoteCommandHistorySummary(bool Available, int MyPowerToolsHistoryCount, bool LegacyHistoryAvailable, string Message)
{
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["available"] = Available,
            ["myPowerToolsHistoryCount"] = MyPowerToolsHistoryCount,
            ["legacyHistoryAvailable"] = LegacyHistoryAvailable,
            ["message"] = Message
        };
    }
}

internal sealed record NotificationEndpoint(bool Found, string Protocol, string Host, int Port, string Message)
{
    public string RedactedUri => Found ? $"{Protocol}://{Host}:{Port}" : "";

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["found"] = Found,
            ["protocol"] = Protocol,
            ["host"] = Host,
            ["port"] = Port,
            ["message"] = Message
        };
    }

    public static NotificationEndpoint Missing(string message)
    {
        return new NotificationEndpoint(false, "https", "", 0, message);
    }
}

internal sealed record ProcessWatchList(IReadOnlyList<string> Names, string SourceKind, string Message)
{
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["source"] = SourceKind,
            ["message"] = Message,
            ["processes"] = AndroidToolsModuleBase.ToJsonArray(Names)
        };
    }
}

internal sealed record ProcessStateSnapshot(string Name, int InstanceCount, string Message = "")
{
    public bool Running => InstanceCount > 0;

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["name"] = Name,
            ["running"] = Running,
            ["instanceCount"] = InstanceCount,
            ["message"] = string.IsNullOrWhiteSpace(Message) ? (Running ? "running" : "not found") : Message
        };
    }
}

internal sealed record ShellRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    long DurationMs,
    bool OutputTruncated = false,
    int StdoutBytes = 0,
    int StderrBytes = 0,
    int StdoutLines = 0,
    int StderrLines = 0);

internal sealed class BoundedShellOutput
{
    private readonly StringBuilder _builder = new();
    private readonly int _maxBytes;
    private int _capturedBytes;

    public BoundedShellOutput(int maxBytes)
    {
        _maxBytes = maxBytes;
    }

    public bool Truncated { get; private set; }
    public int TotalBytes { get; private set; }
    public int LineCount { get; private set; }
    public string Text => _builder.ToString();

    public string AppendLine(string line, int maxLineBytes, out bool lineTruncated)
    {
        LineCount++;
        TotalBytes += Encoding.UTF8.GetByteCount(line + Environment.NewLine);
        var eventLine = LimitUtf8(line, maxLineBytes, out lineTruncated);
        if (lineTruncated)
        {
            Truncated = true;
        }

        AppendToBuffer(eventLine + Environment.NewLine);
        return eventLine;
    }

    private void AppendToBuffer(string value)
    {
        if (_capturedBytes >= _maxBytes)
        {
            Truncated = true;
            return;
        }

        var bytes = Encoding.UTF8.GetByteCount(value);
        if (_capturedBytes + bytes <= _maxBytes)
        {
            _builder.Append(value);
            _capturedBytes += bytes;
            return;
        }

        var remaining = _maxBytes - _capturedBytes;
        if (remaining > 0)
        {
            _builder.Append(LimitUtf8(value, remaining, out _));
            _capturedBytes = _maxBytes;
        }

        Truncated = true;
    }

    private static string LimitUtf8(string value, int maxBytes, out bool truncated)
    {
        truncated = false;
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        truncated = true;
        var builder = new StringBuilder();
        var used = 0;
        foreach (var ch in value)
        {
            var bytes = Encoding.UTF8.GetByteCount(new[] { ch });
            if (used + bytes > Math.Max(0, maxBytes - 16))
            {
                break;
            }

            builder.Append(ch);
            used += bytes;
        }

        builder.Append("...[truncated]");
        return builder.ToString();
    }
}

internal sealed class ShellStreamEventLimiter
{
    private readonly int _maxLineEvents;
    private int _lineEvents;
    private int _truncationEventEmitted;

    public ShellStreamEventLimiter(int maxLineEvents)
    {
        _maxLineEvents = maxLineEvents;
    }

    public bool Truncated => Volatile.Read(ref _lineEvents) >= _maxLineEvents;

    public bool TryReserveLineEvent()
    {
        while (true)
        {
            var current = Volatile.Read(ref _lineEvents);
            if (current >= _maxLineEvents)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lineEvents, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public bool TryReserveTruncationEvent()
    {
        return Interlocked.Exchange(ref _truncationEventEmitted, 1) == 0;
    }
}

internal sealed record DiscoveredFile(string? Path, string SourceKind);

internal static class StringLineExtensions
{
    public static IEnumerable<string> SplitLines(this string text, bool keepLineEndings = false)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            var length = keepLineEndings ? i - start + 1 : TrimLineEndingLength(text, start, i);
            yield return text.Substring(start, length);
            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..].TrimEnd('\r');
        }
    }

    private static int TrimLineEndingLength(string text, int start, int lfIndex)
    {
        var end = lfIndex;
        if (end > start && text[end - 1] == '\r')
        {
            end--;
        }

        return end - start;
    }
}

