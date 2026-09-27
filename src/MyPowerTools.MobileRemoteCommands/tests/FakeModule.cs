using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// Fake <c>remote-commands-android</c> module: answers the read commands with fixed payloads and records
/// every <c>settings.update</c> payload, so a test can assert exactly what a click sent.
/// </summary>
internal sealed class FakeModule
{
    public const string StatusCommand = "remote-commands-android.status";
    public const string CatalogCommand = "remote-commands-android.catalog";
    public const string SettingsCommand = "remote-commands-android.settings.update";

    public List<JsonObject> SettingsUpdates { get; } = [];

    public List<string> Commands { get; } = [];

    public string SettingsFailure { get; set; } = "";

    public string SettingsFailureCode { get; set; } = RemoteCommandsMobileContract.ErrorValidationFailed;

    public int Retention { get; set; } = 500;

    public string DefaultHost { get; set; } = "r743";

    public MptAvaloniaSurfaceContext CreateContext() => new(
        RemoteCommandsMobileContract.ToolId,
        "workspace",
        Path.Combine(Path.GetTempPath(), "mpt-rc-tests", Guid.NewGuid().ToString("N")),
        "light",
        (commandId, args, _) => Task.FromResult(Execute(commandId, args)),
        (_, _, _) => Task.CompletedTask,
        null!,
        _ => { });

    public CommandExecutionResult Execute(string commandId, JsonObject? args)
    {
        Commands.Add(commandId);
        var invocationId = Guid.NewGuid().ToString("N");

        switch (commandId)
        {
            case StatusCommand:
                return Success(invocationId, commandId, new JsonObject
                {
                    ["transportAvailable"] = true,
                    ["backgroundAvailable"] = true,
                    ["defaultHost"] = DefaultHost,
                    ["knownHosts"] = DefaultHost,
                    ["historyRetention"] = Retention,
                    ["condaExecutable"] = "/home/user/miniconda3/bin/conda",
                    ["commandTimeoutMinutes"] = 30,
                    ["commandCount"] = 0,
                    ["commandsPath"] = Path.Combine(Path.GetTempPath(), "mpt-rc-tests", "missing", "commands.yaml"),
                    ["dataDirectory"] = Path.Combine(Path.GetTempPath(), "mpt-rc-tests"),
                    ["hosts"] = new JsonArray(),
                    ["trustedHostKeys"] = new JsonArray()
                });

            case CatalogCommand:
                return Success(invocationId, commandId, new JsonObject
                {
                    ["commands"] = new JsonArray(),
                    ["error"] = "",
                    ["commandsPath"] = Path.Combine(Path.GetTempPath(), "mpt-rc-tests", "missing", "commands.yaml")
                });

            case "remote-commands-android.hosts.list":
                return Success(invocationId, commandId, new JsonObject
                {
                    ["hosts"] = new JsonArray(),
                    ["missingAliases"] = new JsonArray()
                });

            case "remote-commands-android.host-key.status":
                return Success(invocationId, commandId, new JsonObject { ["keys"] = new JsonArray() });

            case "remote-commands-android.history.summary":
                return Success(invocationId, commandId, new JsonObject { ["count"] = 0 });

            case SettingsCommand:
                var values = args?[RemoteCommandsMobileContract.ArgumentSettingsValues] as JsonObject ?? new JsonObject();
                SettingsUpdates.Add((JsonObject)values.DeepClone());
                if (SettingsFailure.Length > 0)
                {
                    return new CommandExecutionResult(
                        invocationId,
                        commandId,
                        "failed",
                        false,
                        "",
                        new MptRuntimeError(SettingsFailureCode, SettingsFailure));
                }

                // The module is the authority after a save; reflect it in the next status read.
                DefaultHost = RemoteCommandsMobileJson.Text(values, "defaultHost");
                Retention = RemoteCommandsMobileJson.Int(values, "historyRetention", Retention);
                return Success(invocationId, commandId, new JsonObject());

            default:
                return new CommandExecutionResult(
                    invocationId,
                    commandId,
                    "failed",
                    false,
                    "",
                    new MptRuntimeError(RemoteCommandsMobileContract.ErrorNotFound, $"未实现命令 '{commandId}'。"));
        }
    }

    private static CommandExecutionResult Success(string invocationId, string commandId, JsonObject payload) =>
        new(invocationId, commandId, "succeeded", true, payload.ToJsonString());
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

    public static SurfaceHarness Create(FakeModule? module = null)
    {
        module ??= new FakeModule();
        var view = new RemoteCommandsMobileView(module.CreateContext());
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

    public void Dispose() => _window.Close();
}
