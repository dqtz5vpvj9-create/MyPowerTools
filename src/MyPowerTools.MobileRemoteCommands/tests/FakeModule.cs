using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using RemoteCommands.Surface.Services;

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// Fake <c>remote-commands-android</c> module.
///
/// It is a contract fake, not a stub: the catalog commands come from the shipped parser
/// (<see cref="RemoteCommandsYaml"/>, the same source the module and the desktop compile), a
/// <c>catalog.save</c> is validated exactly like the module validates it, and the run stream publishes
/// the same <c>run.started</c>/<c>run.stage</c>/<c>command.output</c>/<c>run.finished</c> events the
/// runner publishes. Every call and its arguments are recorded, so a test can assert the exact payload
/// the page sent.
/// </summary>
internal sealed class FakeModule
{
    private readonly List<Action<MptSurfaceEvent>> _subscribers = [];
    private ulong _sequence;

    public FakeModule()
    {
        CatalogYaml = SampleCatalog;
        Hosts = [];
        TrustedKeys = [];
    }

    public const string StatusCommand = "remote-commands-android.status";
    public const string CatalogCommand = "remote-commands-android.catalog";
    public const string CatalogSaveCommand = "remote-commands-android.catalog.save";
    public const string RunCommand = "remote-commands-android.run";
    public const string CancelCommand = "remote-commands-android.cancel";
    public const string HostsCommand = "remote-commands-android.hosts.list";
    public const string HostAddCommand = "remote-commands-android.host.add";
    public const string HostRemoveCommand = "remote-commands-android.host.remove";
    public const string HostKeyStatusCommand = "remote-commands-android.host-key.status";
    public const string HostKeyAcceptCommand = "remote-commands-android.host-key.accept";
    public const string HostKeyRevokeCommand = "remote-commands-android.host-key.revoke";
    public const string HistoryCommand = "remote-commands-android.history.summary";
    public const string HistoryClearCommand = "remote-commands-android.history.clear";
    public const string SettingsCommand = "remote-commands-android.settings.update";

    /// <summary>A catalog document with a comment, three commands and the trailing types section.</summary>
    public const string SampleCatalog = """
        # Shared command catalog. Keep the comments: the phone editor replaces one entry at a time.
        commands:
          - id: server_status
            label: "查看服务器状态"
            command: "uptime"
            description: "只读查看负载与在线时长。"
            type: "shell"
            input1_label: "备注"
            input1_placeholder: "可留空"
          - id: disk_usage
            label: "查看磁盘空间"
            command: "df -h"
            description: "只读查看磁盘占用。"
            type: "shell"
          - id: remove_comments
            label: "移除 C++ 注释"
            command: "remove_cpp_comments"
            description: "在手机上本地转换，不经过 SSH。"
            type: "py"
            input1_label: "C++ 源码"
            input1_placeholder: "粘贴源码"
          - id: compare_files
            label: "对比两份输入"
            command: "diff_files"
            description: "需要两个输入。"
            type: "shell"
            show_second_input: true
            input1_label: "第一份"
            input1_placeholder: "第一个输入"
            input2_label: "第二份"
            input2_placeholder: "第二个输入"

        types:
          - shell
          - py
        """;

    public List<(string CommandId, JsonObject Args)> Calls { get; } = [];

    /// <summary>The invocation id the host put on the command envelope, per call (the module's own key).</summary>
    public List<(string CommandId, string EnvelopeInvocationId)> Envelopes { get; } = [];

    public List<(string ToolId, string RouteId)> Navigations { get; } = [];

    public List<MptSurfaceLogEntry> Logs { get; } = [];

    // ---------------------------------------------------------------- catalog

    public string CatalogYaml { get; private set; }

    public string CatalogError { get; set; } = "";

    /// <summary>Rejects the next <c>catalog.save</c>, the way the module rejects an invalid document.</summary>
    public string SaveFailure { get; set; } = "";

    /// <summary>Rejects the next <c>settings.update</c> with the module's validation message.</summary>
    public string SettingsFailure { get; set; } = "";

    public string CommandsPath { get; set; } = Path.Combine("mpt-rc-tests", "commands.yaml");

    public string DataDirectory { get; set; } = Path.Combine("mpt-rc-tests");

    public IReadOnlyList<MobileCommandDefinition> Commands => RemoteCommandsMobileJson.Commands(
        new JsonObject { ["commands"] = BuildCommandArray() });

    // ---------------------------------------------------------------- hosts / keys / history

    public List<MobileHostEntry> Hosts { get; }

    public List<MobileHostKeyEntry> TrustedKeys { get; }

    public List<JsonObject> HostAdds { get; } = [];

    public List<JsonObject> SettingsUpdates { get; } = [];

    public int HistoryCount { get; set; }

    public string HistoryLatestLabel { get; set; } = "";

    public string HistoryLatestTimestamp { get; set; } = "09:18";

    // ---------------------------------------------------------------- status

    public bool TransportAvailable { get; set; } = true;

    public bool BackgroundAvailable { get; set; } = true;

    public string DefaultHost { get; set; } = "lab-host";

    public string KnownHosts { get; set; } = "lab-host";

    public int Retention { get; set; } = 500;

    public int TimeoutMinutes { get; set; } = 30;

    public string CondaExecutable { get; set; } = "/opt/conda/bin/conda";

    public string ActiveInvocationId { get; set; } = "";

    public string ActiveStage { get; set; } = "";

    public string LastRunSummary { get; set; } = "";

    /// <summary>
    /// Mimics the Android HostControl trip, where a protobuf <c>Struct</c> has no integer type and the
    /// module therefore sees integral numbers as double-backed JSON values.
    /// </summary>
    public bool SimulateHostStructTrip { get; set; }

    // ---------------------------------------------------------------- run

    public string RunState { get; set; } = RemoteCommandsMobileContract.StateSucceeded;

    public int? RunExitCode { get; set; }

    public string RunMessage { get; set; } = "执行完成";

    public string RunOutput { get; set; } = "Uploading input files...\nExecuting remote command...\n09:18 up 12 days, 4:32\nload average: 0.21, 0.18, 0.15";

    public string RunAlias { get; set; } = "lab-host";

    public string RunResolvedHost { get; set; } = "192.168.22.24";

    public List<string> RunStreamLines { get; } = [];

    public bool RunStreamsStageEvents { get; set; } = true;

    public MobilePendingHostKey? PendingHostKey { get; set; }

    /// <summary>When set, the command fails before any run payload exists (host/broker level).</summary>
    public string? ModuleFailureMessage { get; set; }

    public string ModuleFailureCode { get; set; } = RemoteCommandsMobileContract.ErrorRuntimeUnavailable;

    public int RunCount { get; private set; }

    /// <summary>When set, a run publishes its start events and stays in flight until the test releases it.</summary>
    public TaskCompletionSource<bool>? RunHold { get; set; }

    // ---------------------------------------------------------------- events

    public MptAvaloniaSurfaceContext CreateContext(bool callerOwnedInvocationId = true) => new(
        RemoteCommandsMobileContract.ToolId,
        "workspace",
        DataDirectory,
        "light",
        (commandId, args, _) => ExecuteAsync(commandId, args, invocationId: null),
        (toolId, routeId, _) =>
        {
            Navigations.Add((toolId, routeId));
            return Task.CompletedTask;
        },
        null!,
        entry => Logs.Add(entry),
        handler =>
        {
            _subscribers.Add(handler);
            return new Subscription(() => _subscribers.Remove(handler));
        })
    {
        // Exactly what the Shell wires for a dotnet surface: the page may own the invocation id.
        ExecuteCommandWithInvocationAsync = callerOwnedInvocationId
            ? (invocationId, commandId, args, _) => ExecuteAsync(commandId, args, invocationId)
            : null
    };

    public void Publish(string type, JsonObject payload)
    {
        var surfaceEvent = new MptSurfaceEvent(
            ++_sequence,
            RemoteCommandsMobileContract.ToolId,
            type,
            DateTimeOffset.Now,
            payload);
        foreach (var subscriber in _subscribers.ToArray())
        {
            subscriber(surfaceEvent);
        }
    }

    /// <summary>
    /// Runs a command. A held run lets a test observe the in-flight state (progress, cancel) exactly as
    /// the device would show it, instead of only the terminal payload.
    /// </summary>
    public async Task<CommandExecutionResult> ExecuteAsync(
        string commandId,
        JsonObject? args,
        string? invocationId)
    {
        if (string.Equals(commandId, RunCommand, StringComparison.Ordinal) &&
            RunHold is { } hold &&
            ModuleFailureMessage is null)
        {
            args ??= new JsonObject();
            Record(commandId, args, invocationId);
            ActiveInvocationId = invocationId!;
            PublishRunStart(invocationId!, args);
            await hold.Task.ConfigureAwait(false);
            ActiveInvocationId = "";
            return FinishRun(invocationId!, commandId, args);
        }

        return Execute(commandId, args, invocationId);
    }

    public CommandExecutionResult Execute(string commandId, JsonObject? args) =>
        Execute(commandId, args, envelopeInvocationId: null);

    public CommandExecutionResult Execute(string commandId, JsonObject? args, string? envelopeInvocationId)
    {
        args ??= new JsonObject();
        // The module keys everything on the envelope id the host assigned, never on an argument. The raw
        // value is recorded as well, so a test can tell a page-owned id from a host-generated one.
        var invocationId = envelopeInvocationId ?? Guid.NewGuid().ToString("N");
        Record(commandId, args, envelopeInvocationId);

        switch (commandId)
        {
            case StatusCommand:
                return Success(invocationId, commandId, BuildStatus());

            case CatalogCommand:
                return CatalogError.Length > 0
                    ? Failure(invocationId, commandId, RemoteCommandsMobileContract.ErrorValidationFailed, CatalogError)
                    : Success(invocationId, commandId, BuildCatalog());

            case CatalogSaveCommand:
                return SaveCatalog(invocationId, commandId, args);

            case RunCommand:
                return Run(invocationId, commandId, args);

            case CancelCommand:
                var cancelled = ActiveInvocationId.Length > 0 &&
                                string.Equals(
                                    RemoteCommandsMobileJson.Text(args, "invocationId"),
                                    ActiveInvocationId,
                                    StringComparison.Ordinal);
                if (cancelled)
                {
                    Publish(RemoteCommandsMobileContract.EventRunFinished, new JsonObject
                    {
                        ["invocationId"] = ActiveInvocationId,
                        ["state"] = RemoteCommandsMobileContract.StateCancelled
                    });
                    ActiveInvocationId = "";
                }

                return Success(invocationId, commandId, new JsonObject { ["cancelled"] = cancelled });

            case HostsCommand:
                return Success(invocationId, commandId, BuildHosts());

            case HostAddCommand:
                HostAdds.Add((JsonObject)args.DeepClone());
                var alias = RemoteCommandsMobileJson.Text(args, "alias");
                var existing = Hosts.FindIndex(host => string.Equals(host.Alias, alias, StringComparison.OrdinalIgnoreCase));
                var entry = new MobileHostEntry(
                    alias,
                    RemoteCommandsMobileJson.Text(args, "hostName"),
                    RemoteCommandsMobileJson.Int(args, "port", RemoteCommandsMobileContract.DefaultPort),
                    RemoteCommandsMobileJson.Text(args, "username"),
                    RemoteCommandsMobileJson.Text(args, "auth"),
                    true);
                if (existing >= 0)
                {
                    Hosts[existing] = entry;
                }
                else
                {
                    Hosts.Add(entry);
                }

                Publish(RemoteCommandsMobileContract.EventHostUpdated, new JsonObject
                {
                    ["alias"] = alias,
                    ["message"] = $"主机 {alias} 已保存。"
                });
                return Success(invocationId, commandId, BuildHosts());

            case HostRemoveCommand:
                var removeAlias = RemoteCommandsMobileJson.Text(args, "alias");
                Hosts.RemoveAll(host => string.Equals(host.Alias, removeAlias, StringComparison.OrdinalIgnoreCase));
                TrustedKeys.RemoveAll(key => string.Equals(key.Host, removeAlias, StringComparison.OrdinalIgnoreCase));
                Publish(RemoteCommandsMobileContract.EventHostRemoved, new JsonObject
                {
                    ["alias"] = removeAlias,
                    ["message"] = $"主机 {removeAlias} 已删除。"
                });
                return Success(invocationId, commandId, new JsonObject());

            case HostKeyStatusCommand:
                return Success(invocationId, commandId, new JsonObject { ["keys"] = BuildKeyArray() });

            case HostKeyAcceptCommand:
                var accepted = new MobileHostKeyEntry(
                    RemoteCommandsMobileJson.Text(args, "hostName"),
                    RemoteCommandsMobileJson.Int(args, "port", RemoteCommandsMobileContract.DefaultPort),
                    RemoteCommandsMobileJson.Text(args, "hostKeyName"),
                    RemoteCommandsMobileJson.Text(args, "fingerprint"),
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"));
                TrustedKeys.RemoveAll(key => key.Host == accepted.Host && key.Port == accepted.Port);
                TrustedKeys.Add(accepted);
                Publish(RemoteCommandsMobileContract.EventHostKeyAccepted, new JsonObject
                {
                    ["message"] = $"已确认 {accepted.Host} 的指纹。"
                });
                return Success(invocationId, commandId, new JsonObject());

            case HostKeyRevokeCommand:
                var revokedHost = RemoteCommandsMobileJson.Text(args, "hostName");
                var revokedPort = RemoteCommandsMobileJson.Int(args, "port", RemoteCommandsMobileContract.DefaultPort);
                TrustedKeys.RemoveAll(key => key.Host == revokedHost && key.Port == revokedPort);
                Publish(RemoteCommandsMobileContract.EventHostKeyRevoked, new JsonObject
                {
                    ["message"] = $"已撤销 {revokedHost}:{revokedPort} 的指纹。"
                });
                return Success(invocationId, commandId, new JsonObject());

            case HistoryCommand:
                return Success(invocationId, commandId, new JsonObject
                {
                    ["count"] = HistoryCount,
                    ["latestTimestamp"] = HistoryLatestTimestamp,
                    ["latestLabel"] = HistoryLatestLabel,
                    ["latestHost"] = DefaultHost
                });

            case HistoryClearCommand:
                HistoryCount = 0;
                HistoryLatestLabel = "";
                Publish(RemoteCommandsMobileContract.EventHistoryCleared, new JsonObject
                {
                    ["message"] = "已清空执行历史。"
                });
                return Success(invocationId, commandId, new JsonObject());

            case SettingsCommand:
                return UpdateSettings(invocationId, commandId, args);

            default:
                return Failure(
                    invocationId,
                    commandId,
                    RemoteCommandsMobileContract.ErrorNotFound,
                    $"未实现命令 '{commandId}'。");
        }
    }

    // ---------------------------------------------------------------- payload builders

    private JsonObject BuildStatus()
    {
        var status = new JsonObject
        {
            ["transportAvailable"] = TransportAvailable,
            ["backgroundAvailable"] = BackgroundAvailable,
            ["defaultHost"] = DefaultHost,
            ["knownHosts"] = KnownHosts,
            ["historyRetention"] = Retention,
            ["condaExecutable"] = CondaExecutable,
            ["commandTimeoutMinutes"] = TimeoutMinutes,
            ["commandCount"] = Commands.Count,
            ["commandsPath"] = CommandsPath,
            ["commandsError"] = "",
            ["dataDirectory"] = DataDirectory,
            ["hosts"] = BuildHostArray(),
            ["trustedHostKeys"] = BuildKeyArray(),
            ["activeInvocationId"] = ActiveInvocationId,
            ["activeStage"] = ActiveStage,
            ["lastRunState"] = RunState,
            ["lastRunSummary"] = LastRunSummary
        };

        return SimulateHostStructTrip ? NormalizeNumbers(status) : status;
    }

    private JsonObject BuildCatalog() => new()
    {
        ["commands"] = BuildCommandArray(),
        ["error"] = CatalogError,
        ["commandsPath"] = CommandsPath,
        // The module may hand the document back; when it does not, the page reads the canonical path.
        ["yaml"] = CatalogYaml
    };

    private JsonObject BuildHosts() => new()
    {
        ["hosts"] = BuildHostArray(),
        ["missingAliases"] = new JsonArray(
            Commands
                .Select(command => command.Host)
                .Where(host => host.Length > 0 &&
                               Hosts.All(entry => !string.Equals(entry.Alias, host, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(host => JsonValue.Create(host))
                .ToArray()),
        ["catalogPath"] = CommandsPath,
        ["loadFailed"] = false
    };

    private JsonArray BuildCommandArray() => new(
        RemoteCommandsYaml.ParseCommands(CatalogYaml)
            .Select(command => (JsonNode)new JsonObject
            {
                ["id"] = command.Id,
                ["label"] = command.Label,
                ["command"] = command.Command,
                ["description"] = command.Description,
                ["type"] = command.Type,
                ["host"] = command.Host,
                ["input1Label"] = command.Input1Label,
                ["input1Placeholder"] = command.Input1Placeholder,
                ["input2Label"] = command.Input2Label,
                ["input2Placeholder"] = command.Input2Placeholder,
                ["showSecondInput"] = command.ShowSecondInput,
                ["usesRemoteHost"] = command.UsesRemoteHost
            })
            .ToArray());

    private JsonArray BuildHostArray() => new(
        Hosts.Select(host => (JsonNode)new JsonObject
        {
            ["alias"] = host.Alias,
            ["host"] = host.Host,
            ["port"] = host.Port,
            ["username"] = host.Username,
            ["auth"] = host.Auth,
            ["credentialConfigured"] = host.CredentialConfigured
        }).ToArray());

    private JsonArray BuildKeyArray() => new(
        TrustedKeys.Select(key => (JsonNode)new JsonObject
        {
            ["host"] = key.Host,
            ["port"] = key.Port,
            ["hostKeyName"] = key.HostKeyName,
            ["fingerprint"] = key.Fingerprint,
            ["addedAt"] = key.AddedAt
        }).ToArray());

    /// <summary>Replaces the shared document, as another editor (the desktop tool) could have done.</summary>
    public void SetCatalog(string yaml) => CatalogYaml = yaml;

    /// <summary>Adds a host mapping the way the desktop module would have it.</summary>
    public void AddHost(
        string alias,
        string host = "192.168.22.24",
        int port = 22,
        string username = "lixr",
        string auth = "password",
        bool credentialConfigured = true) =>
        Hosts.Add(new MobileHostEntry(alias, host, port, username, auth, credentialConfigured));

    // ---------------------------------------------------------------- command handlers

    private void Record(string commandId, JsonObject args, string? envelopeInvocationId)
    {
        Calls.Add((commandId, (JsonObject)args.DeepClone()));
        Envelopes.Add((commandId, envelopeInvocationId ?? ""));
    }

    private CommandExecutionResult SaveCatalog(string invocationId, string commandId, JsonObject args)
    {
        var content = RemoteCommandsMobileJson.Text(args, "content");
        string? error = null;
        if (SaveFailure.Length > 0 || !RemoteCommandsYaml.TryValidate(content, out error))
        {
            var message = SaveFailure.Length > 0 ? SaveFailure : error ?? "命令配置无效。";
            var payload = BuildCatalog();
            payload["saved"] = false;
            payload["saveError"] = message;
            return new CommandExecutionResult(
                invocationId,
                commandId,
                "failed",
                false,
                payload.ToJsonString(),
                new MptRuntimeError(RemoteCommandsMobileContract.ErrorValidationFailed, message));
        }

        CatalogYaml = content;
        Publish(RemoteCommandsMobileContract.EventCatalogSaved, new JsonObject
        {
            ["title"] = "命令目录已保存",
            ["message"] = $"{Commands.Count} 条命令已写入 commands.yaml。",
            ["commandCount"] = Commands.Count
        });
        var saved = BuildCatalog();
        saved["saved"] = true;
        return Success(invocationId, commandId, saved);
    }

    private CommandExecutionResult Run(string invocationId, string commandId, JsonObject args)
    {
        RunCount++;
        var commandIdValue = RemoteCommandsMobileJson.Text(args, "commandId");
        var command = Commands.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, commandIdValue, StringComparison.OrdinalIgnoreCase));

        if (ModuleFailureMessage is { Length: > 0 } failure)
        {
            return new CommandExecutionResult(
                invocationId,
                commandId,
                RemoteCommandsMobileContract.StatePermissionRequired,
                false,
                "",
                new MptRuntimeError(ModuleFailureCode, failure));
        }

        if (command is null)
        {
            return Failure(
                invocationId,
                commandId,
                RemoteCommandsMobileContract.ErrorNotFound,
                $"commands.yaml 中没有命令 '{commandIdValue}'。");
        }

        ActiveInvocationId = invocationId;
        PublishRunStart(invocationId, args);
        ActiveInvocationId = "";
        return FinishRun(invocationId, commandId, args);
    }

    private void PublishRunStart(string invocationId, JsonObject args)
    {
        var command = Commands.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, RemoteCommandsMobileJson.Text(args, "commandId"), StringComparison.OrdinalIgnoreCase));

        Publish(RemoteCommandsMobileContract.EventRunStarted, new JsonObject
        {
            ["invocationId"] = invocationId,
            ["commandId"] = command?.Id ?? "",
            ["label"] = command?.Label ?? "",
            ["host"] = RemoteCommandsMobileJson.Text(args, "host"),
            ["alias"] = RunAlias,
            ["resolvedHost"] = RunResolvedHost
        });

        if (command?.UsesRemoteHost != false && RunStreamsStageEvents)
        {
            Publish(RemoteCommandsMobileContract.EventRunStage, new JsonObject
            {
                ["invocationId"] = invocationId,
                ["stage"] = RemoteCommandsMobileContract.StageUploading
            });
            Publish(RemoteCommandsMobileContract.EventRunStage, new JsonObject
            {
                ["invocationId"] = invocationId,
                ["stage"] = RemoteCommandsMobileContract.StageRunning
            });
        }

        foreach (var line in RunStreamLines)
        {
            Publish(RemoteCommandsMobileContract.EventCommandOutput, new JsonObject
            {
                ["invocationId"] = invocationId,
                ["line"] = line
            });
        }
    }

    private CommandExecutionResult FinishRun(string invocationId, string commandId, JsonObject args)
    {
        var command = Commands.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, RemoteCommandsMobileJson.Text(args, "commandId"), StringComparison.OrdinalIgnoreCase));

        Publish(RemoteCommandsMobileContract.EventRunFinished, new JsonObject
        {
            ["invocationId"] = invocationId,
            ["commandId"] = command?.Id ?? "",
            ["state"] = RunState,
            ["exitCode"] = RunExitCode
        });

        HistoryCount++;
        HistoryLatestLabel = command?.Label ?? "";

        if (RunState == RemoteCommandsMobileContract.StateHostKeyRequired && PendingHostKey is { } pending)
        {
            Publish(RemoteCommandsMobileContract.EventHostKeyPending, new JsonObject
            {
                ["invocationId"] = invocationId,
                ["host"] = pending.Host,
                ["port"] = pending.Port,
                ["hostKeyName"] = pending.HostKeyName,
                ["fingerprint"] = pending.Fingerprint,
                ["firstUse"] = pending.FirstUse,
                ["message"] = pending.Message
            });
        }

        var payload = new JsonObject
        {
            ["state"] = RunState,
            ["message"] = RunMessage,
            ["exitCode"] = RunExitCode,
            ["host"] = RemoteCommandsMobileJson.Text(args, "host"),
            ["alias"] = RunAlias,
            ["resolvedHost"] = RunResolvedHost,
            ["output"] = RunOutput,
            ["transport"] = "managed-ssh"
        };

        if (RunState == RemoteCommandsMobileContract.StateHostKeyRequired && PendingHostKey is { } pendingKey)
        {
            payload["pendingHostKey"] = new JsonObject
            {
                ["host"] = pendingKey.Host,
                ["port"] = pendingKey.Port,
                ["hostKeyName"] = pendingKey.HostKeyName,
                ["fingerprint"] = pendingKey.Fingerprint,
                ["firstUse"] = pendingKey.FirstUse,
                ["message"] = pendingKey.Message
            };
        }

        var success = RunState == RemoteCommandsMobileContract.StateSucceeded;
        return new CommandExecutionResult(
            invocationId,
            commandId,
            RunState,
            success,
            payload.ToJsonString(),
            success
                ? null
                : new MptRuntimeError(
                    RunState == RemoteCommandsMobileContract.StateHostKeyRequired
                        ? RemoteCommandsMobileContract.ErrorPermissionRequired
                        : RemoteCommandsMobileContract.ErrorRuntimeUnavailable,
                    RunMessage));
    }

    private CommandExecutionResult UpdateSettings(string invocationId, string commandId, JsonObject args)
    {
        var values = args[RemoteCommandsMobileContract.ArgumentSettingsValues] as JsonObject ?? new JsonObject();
        SettingsUpdates.Add((JsonObject)values.DeepClone());

        if (SettingsFailure.Length > 0)
        {
            return new CommandExecutionResult(
                invocationId,
                commandId,
                "failed",
                false,
                "",
                new MptRuntimeError(RemoteCommandsMobileContract.ErrorValidationFailed, SettingsFailure));
        }

        if (SimulateHostStructTrip)
        {
            // The module receives what the host delivered; reading it back must still be exact.
            var normalized = NormalizeNumbers(values);
            Retention = RemoteCommandsMobileJson.Int(normalized, "historyRetention", Retention);
            TimeoutMinutes = RemoteCommandsMobileJson.Int(normalized, "commandTimeoutMinutes", TimeoutMinutes);
        }
        else
        {
            Retention = RemoteCommandsMobileJson.Int(values, "historyRetention", Retention);
            TimeoutMinutes = RemoteCommandsMobileJson.Int(values, "commandTimeoutMinutes", TimeoutMinutes);
        }

        DefaultHost = RemoteCommandsMobileJson.Text(values, "defaultHost");
        KnownHosts = RemoteCommandsMobileJson.Text(values, "knownHosts");
        CondaExecutable = RemoteCommandsMobileJson.Text(values, "condaExecutable");
        Publish(RemoteCommandsMobileContract.EventSettingsUpdated, new JsonObject
        {
            ["message"] = "设置已保存。"
        });
        return Success(invocationId, commandId, new JsonObject());
    }

    /// <summary>Rewrites every integral number as a double, the shape a protobuf Struct delivers.</summary>
    public static JsonObject NormalizeNumbers(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var (key, node) in source)
        {
            result[key] = Normalize(node);
        }

        return result;
    }

    private static JsonNode? Normalize(JsonNode? node) => node switch
    {
        JsonObject obj => NormalizeNumbers(obj),
        JsonArray array => new JsonArray(array.Select(Normalize).ToArray()),
        JsonValue value when value.TryGetValue<int>(out var number) => JsonValue.Create((double)number),
        JsonValue value when value.TryGetValue<long>(out var wide) => JsonValue.Create((double)wide),
        _ => node?.DeepClone()
    };

    private static CommandExecutionResult Success(string invocationId, string commandId, JsonObject payload) =>
        new(invocationId, commandId, "succeeded", true, payload.ToJsonString());

    private static CommandExecutionResult Failure(string invocationId, string commandId, string code, string message) =>
        new(invocationId, commandId, "failed", false, "", new MptRuntimeError(code, message));

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>Locates the repository root from the test binary, for evidence and policy checks.</summary>
internal static class RepoPaths
{
    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "artifacts")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}

/// <summary>Shared headless harness for the surface view.</summary>
internal sealed class SurfaceHarness : IDisposable
{
    private readonly Window _window;

    private SurfaceHarness(FakeModule module, RemoteCommandsMobileView view, Window window)
    {
        Module = module;
        View = view;
        _window = window;
    }

    public FakeModule Module { get; }

    public RemoteCommandsMobileView View { get; }

    public Window Window => _window;

    public static SurfaceHarness Create(FakeModule? module = null, bool callerOwnedInvocationId = true)
    {
        module ??= new FakeModule();

        // An older host offers no caller-owned invocation id; the page then cannot correlate streamed
        // events with its own run, which is exactly the fallback under test.
        var view = new RemoteCommandsMobileView(module.CreateContext(callerOwnedInvocationId));
        var window = new Window { Width = 380, Height = 900, Content = view };
        var harness = new SurfaceHarness(module, view, window);
        window.Show();
        harness.Pump();
        return harness;
    }

    /// <summary>Runs queued dispatcher work and a layout pass, the way the device would between input events.</summary>
    public void Pump()
    {
        for (var pass = 0; pass < 4; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            _window.UpdateLayout();
        }
    }

    /// <summary>Pumps until <paramref name="condition"/> holds, so async module reads are not raced.</summary>
    public void WaitFor(Func<bool> condition, string because)
    {
        for (var pass = 0; pass < 200 && !condition(); pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Pump();

            // A continuation that a module task resumed on a thread-pool thread is posted back to the UI
            // thread; give that thread a chance to run instead of spinning the dispatcher empty.
            if (!condition())
            {
                Thread.Sleep(2);
            }
        }

        Assert.True(condition(), because);
    }

    /// <summary>
    /// Drives the dispatcher until the click's async work finished. Pumping instead of blocking keeps the
    /// test on the headless UI thread without deadlocking on its own continuations.
    /// </summary>
    public void Complete(Task task)
    {
        for (var pass = 0; pass < 200 && !task.IsCompleted; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Pump();
            if (!task.IsCompleted)
            {
                Thread.Sleep(2);
            }
        }

        Assert.True(task.IsCompleted, "异步流程没有在预期内完成。");
        task.GetAwaiter().GetResult();
        Pump();
    }

    /// <summary>Clicks a button the way a finger does, including the async continuation it starts.</summary>
    public void Click(Button button)
    {
        Assert.True(button.IsEnabled, "按钮当前不可点击。");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    /// <summary>Sends a real key press through the headless input pipeline to the focused element.</summary>
    public void PressKey(Key key)
    {
        var physical = key switch
        {
            Key.Enter => PhysicalKey.Enter,
            Key.Escape => PhysicalKey.Escape,
            _ => PhysicalKey.None
        };
        Window.KeyPress(key, RawInputModifiers.None, physical, null);
        Pump();
    }

    public void Resize(double width, double height)
    {
        _window.Width = width;
        _window.Height = height;
        Pump();
    }

    public void Dispose() => _window.Close();
}
