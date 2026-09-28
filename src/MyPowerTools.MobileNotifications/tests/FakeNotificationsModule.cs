using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.RemoteNotifications.Configuration;
using RemoteNotifications.Surface.Services;

namespace MyPowerTools.MobileNotifications.Tests;

/// <summary>
/// Fake <c>remote-notifications-android</c> module for the phone surface tests.
///
/// The history file is real - it is written and read through the shipped
/// <see cref="RemoteNotificationsLegacyStore"/> in a throw-away data root - while the module answers
/// commands from a mutable state object. Every command id and payload is recorded, so a test can
/// assert exactly what a tap sent, and failures can be injected per command to prove the page never
/// reports a state the module did not confirm.
/// </summary>
internal sealed class FakeNotificationsModule
{
    public const string StatusCommand = "remote-notifications-android.status";
    public const string SyncCommand = "remote-notifications-android.sync-now";
    public const string PollingStartCommand = "remote-notifications-android.polling.start";
    public const string PollingStopCommand = "remote-notifications-android.polling.stop";
    public const string ConfigureCommand = "remote-notifications-android.configure";
    public const string KeyImportCommand = "remote-notifications-android.signing-key.import";
    public const string KeyClearCommand = "remote-notifications-android.signing-key.clear";
    public const string InboxClearCommand = "remote-notifications-android.inbox.clear";

    private readonly RemoteNotificationSettingsStore _settingsStore;
    private readonly RemoteNotificationsLegacyStore _store;
    private Action<MptSurfaceEvent>? _eventSink;
    private ulong _sequence;

    public FakeNotificationsModule()
    {
        DataDirectory = TestEnvironment.NewDataDirectory();
        _settingsStore = new RemoteNotificationSettingsStore(Path.Combine(DataDirectory, "settings.json"));
        _store = new RemoteNotificationsLegacyStore(_settingsStore, DataDirectory);
        State = new JsonObject
        {
            ["moduleId"] = "remote-notifications-android",
            ["endpoint"] = $"{RemoteNotificationSettings.DefaultProtocol}://{RemoteNotificationSettings.DefaultHost}:{RemoteNotificationSettings.DefaultPort}",
            ["channel"] = RemoteNotificationSettings.DefaultChannel,
            ["pollIntervalSeconds"] = RemoteNotificationSettings.DefaultPollIntervalSeconds,
            ["connectionState"] = "idle",
            ["lastPoll"] = "2026/09/27 09:12:00",
            ["lastError"] = "",
            ["fetched"] = 0,
            ["accepted"] = 0,
            ["messageCount"] = 0,
            ["keyConfigured"] = false,
            ["backgroundAvailable"] = true,
            ["backgroundActive"] = false,
            ["backgroundPollingRequested"] = false,
            ["notificationAvailable"] = true
        };
    }

    public string DataDirectory { get; }

    public JsonObject State { get; }

    public List<string> Commands { get; } = [];

    public List<JsonObject?> Payloads { get; } = [];

    /// <summary>Injected failures keyed by command id: (error code, message).</summary>
    public Dictionary<string, (string Code, string Message)> Failures { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Command ids that fail with <c>Success=false</c> but <em>no</em> Error object, which real hosts
    /// can produce. The surface must treat these as failures.
    /// </summary>
    public HashSet<string> FailuresWithoutError { get; } = new(StringComparer.Ordinal);

    /// <summary>Payload returned by <see cref="FailuresWithoutError"/> commands; may look successful.</summary>
    public string FailureOutputWithoutError { get; set; } = "";

    /// <summary>Command ids that "succeed" with output that is not a JSON object.</summary>
    public HashSet<string> UnparseableOutputs { get; } = new(StringComparer.Ordinal);

    /// <summary>Messages the fake module "receives" on the next sync-now.</summary>
    public int AcceptedOnSync { get; set; }

    public MptAvaloniaSurfaceContext CreateContext() => new(
        "remote-notifications-android",
        "main",
        DataDirectory,
        "light",
        (commandId, args, _) => Task.FromResult(Execute(commandId, args)),
        (_, _, _) => Task.CompletedTask,
        null!,
        _ => { },
        callback =>
        {
            _eventSink = callback;
            return new Subscription(() => _eventSink = null);
        });

    public CommandExecutionResult Execute(string commandId, JsonObject? args)
    {
        Commands.Add(commandId);
        Payloads.Add(args?.DeepClone() as JsonObject);

        if (Failures.TryGetValue(commandId, out var failure))
        {
            return Failed(commandId, failure.Code, failure.Message);
        }

        if (FailuresWithoutError.Contains(commandId))
        {
            return new CommandExecutionResult(
                Guid.NewGuid().ToString("N"),
                commandId,
                "failed",
                false,
                FailureOutputWithoutError,
                null);
        }

        if (UnparseableOutputs.Contains(commandId))
        {
            return new CommandExecutionResult(
                Guid.NewGuid().ToString("N"),
                commandId,
                "succeeded",
                true,
                "this is not json",
                null);
        }

        switch (commandId)
        {
            case SyncCommand:
                State["accepted"] = AcceptedOnSync;
                State["fetched"] = AcceptedOnSync;
                State["connectionState"] = "ok";
                State["lastPoll"] = "2026/09/27 09:30:00";
                return Success(commandId, State);

            case PollingStartCommand:
                State["backgroundActive"] = true;
                State["backgroundPollingRequested"] = true;
                return Success(commandId, State);

            case PollingStopCommand:
                State["backgroundActive"] = false;
                State["backgroundPollingRequested"] = false;
                return Success(commandId, State);

            case ConfigureCommand:
                if (args is not null)
                {
                    foreach (var pair in args)
                    {
                        State[pair.Key] = pair.Value?.DeepClone();
                    }

                    State["endpoint"] = $"{args["protocol"]}://{args["host"]}:{args["port"]}";
                    // The real module persists the settings it was configured with, so the surface can
                    // reload the same values from the shared settings file.
                    if (args["host"]?.GetValue<string>() is { Length: > 0 } host)
                    {
                        var port = args["port"]?.GetValue<int>() ?? RemoteNotificationSettings.DefaultPort;
                        var channel = args["channel"]?.GetValue<string>() ?? RemoteNotificationSettings.DefaultChannel;
                        var interval = args["pollIntervalSeconds"]?.GetValue<int>()
                            ?? RemoteNotificationSettings.DefaultPollIntervalSeconds;
                        _settingsStore.Save(new RemoteNotificationSettings(
                            args["protocol"]?.GetValue<string>() ?? RemoteNotificationSettings.DefaultProtocol,
                            host,
                            port,
                            channel,
                            interval,
                            RemoteNotificationSettings.DefaultPrivateKeyPath,
                            RemoteNotificationSettings.Default.KeepWindowsBanners));
                    }
                }

                return Success(commandId, State);

            case KeyImportCommand:
                State["keyConfigured"] = true;
                return Success(commandId, State);

            case KeyClearCommand:
                State["keyConfigured"] = false;
                State["backgroundActive"] = false;
                State["backgroundPollingRequested"] = false;
                return Success(commandId, State);

            case InboxClearCommand:
                _store.ClearMessages();
                State["messageCount"] = 0;
                return Success(commandId, State);

            default:
                return Success(commandId, State);
        }
    }

    // ---------------------------------------------------------------- history

    public void Seed(params RemoteNotificationRecord[] messagesOldestFirst) =>
        _store.SaveMessages(messagesOldestFirst);

    public void Append(RemoteNotificationRecord message)
    {
        var existing = _store.Load().MessagesOldestFirst.ToList();
        existing.Add(message);
        _store.SaveMessages(existing);
    }

    public IReadOnlyList<RemoteNotificationRecord> History() => _store.Load().MessagesOldestFirst;

    /// <summary>Raises a real module event, the same way the Runner would deliver it.</summary>
    public void PublishEvent(string type)
    {
        _eventSink?.Invoke(new MptSurfaceEvent(
            ++_sequence,
            "remote-notifications-android",
            type,
            DateTimeOffset.Now,
            new JsonObject()));
    }

    // ---------------------------------------------------------------- helpers

    private static CommandExecutionResult Success(string commandId, JsonObject state) => new(
        Guid.NewGuid().ToString("N"),
        commandId,
        "succeeded",
        true,
        state.ToJsonString(),
        null);

    private static CommandExecutionResult Failed(string commandId, string code, string message) => new(
        Guid.NewGuid().ToString("N"),
        commandId,
        "failed",
        false,
        "",
        new MptRuntimeError(code, message));

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private readonly Action _dispose = dispose;

        public void Dispose() => _dispose();
    }
}

internal static class TestRecords
{
    public static RemoteNotificationRecord Build(
        string label,
        string body,
        DateTimeOffset timestamp,
        string id,
        string channel = "default",
        string icon = "info",
        string sessionId = "",
        string sessionName = "",
        string sourceClient = "MacBook Pro",
        string? quotedRequest = null)
    {
        // Exactly the shipped appendix shape the desktop publisher writes:
        //   [label] body -- --- -- "For reference:" -- "> quoted request"
        var message = quotedRequest is null
            ? $"[{label}] {body}"
            : $"[{label}] {body}\n\n---\n\nFor reference:\n\n> {quotedRequest}";
        // The display path parses the naive Timestamp as UTC, so the fixture writes UTC like a real
        // server would; the ISO ServerTimestamp keeps the original offset.
        return new RemoteNotificationRecord(
            id,
            channel,
            message,
            icon,
            timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            timestamp.ToString("O"),
            sessionId,
            sessionName,
            sourceClient);
    }
}
