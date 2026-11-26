using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;
using RemoteToolGateway.Core;

namespace MyPowerTools.MobileToolControl.Integration.Tests;

/// <summary>In-memory secret store; the module and the gateway both write their credentials here.</summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Values => _values;

    public Task<SecretReference> SaveAsync(string moduleId, string name, string secret, CancellationToken cancellationToken)
    {
        var reference = SecretReference.Create(moduleId, name);
        _values[reference.Uri] = secret;
        return Task.FromResult(reference);
    }

    public Task<string?> ReadAsync(SecretReference reference, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(reference.Uri, out var value) ? value : null);

    public Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        _values.Remove(reference.Uri);
        return Task.CompletedTask;
    }
}

internal enum BridgeExecution
{
    /// <summary>Returns a terminal success immediately.</summary>
    Success,

    /// <summary>Returns a terminal failure with a real error code and message.</summary>
    Failure,

    /// <summary>Runs for a while and then succeeds unless the runtime cancels it.</summary>
    SlowSuccess,

    /// <summary>Runs until cancelled, then reports the real terminal <c>cancelled</c> result.</summary>
    UntilCancelled
}

internal sealed record BridgeBehavior(
    BridgeExecution Execution,
    string Summary = "",
    string ErrorCode = "",
    string ErrorMessage = "",
    TimeSpan Delay = default,
    bool CancelAccepted = true,
    string CancelMessage = "");

/// <summary>
/// The only replaced piece of the real combination: the desktop-side command execution. Everything
/// else (the gateway listener and wire, the phone module and HTTP client, the page) is the shipped
/// implementation.
/// </summary>
internal sealed class FakeHostControlBridge : IHostControlBridge
{
    private readonly Dictionary<string, BridgeBehavior> _behaviors = new(StringComparer.Ordinal);

    public HostCatalog Catalog { get; set; } = new([], []);

    public List<(string InvocationId, string CommandId, JsonObject Args)> Executions { get; } = [];

    public List<string> CancelRequests { get; } = [];

    public void Behave(string commandId, BridgeBehavior behavior) => _behaviors[commandId] = behavior;

    public Task<HostCatalog> GetCatalogAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Catalog);

    public Task<HostCancellation> CancelAsync(string invocationId, CancellationToken cancellationToken)
    {
        CancelRequests.Add(invocationId);
        var behavior = _behaviors.Values.FirstOrDefault() ?? new BridgeBehavior(BridgeExecution.Success);
        foreach (var pair in _behaviors)
        {
            if (Executions.Any(execution =>
                    execution.InvocationId == invocationId && execution.CommandId == pair.Key))
            {
                behavior = pair.Value;
                break;
            }
        }

        return Task.FromResult(new HostCancellation(
            behavior.CancelAccepted,
            invocationId,
            behavior.CancelAccepted ? "cancelling" : "running",
            behavior.CancelMessage.Length > 0
                ? behavior.CancelMessage
                : behavior.CancelAccepted ? "已向运行时请求取消。" : "运行时拒绝了取消请求。"));
    }

    public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public async IAsyncEnumerable<HostExecutionEvent> ExecuteStreamAsync(
        string invocationId,
        string commandId,
        JsonObject args,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Executions.Add((invocationId, commandId, (JsonObject)args.DeepClone()));
        var behavior = _behaviors.GetValueOrDefault(commandId) ?? new BridgeBehavior(BridgeExecution.Success);
        switch (behavior.Execution)
        {
            case BridgeExecution.Failure:
                yield return Terminal(
                    invocationId,
                    commandId,
                    "failed",
                    behavior.ErrorMessage.Length > 0 ? behavior.ErrorMessage : "命令执行失败。",
                    behavior.ErrorCode.Length > 0 ? behavior.ErrorCode : "command.failed",
                    behavior.ErrorMessage);
                yield break;

            case BridgeExecution.SlowSuccess:
            case BridgeExecution.UntilCancelled:
                var cancelled = false;
                try
                {
                    var delay = behavior.Delay > TimeSpan.Zero ? behavior.Delay : TimeSpan.FromSeconds(10);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                if (cancelled && behavior.Execution == BridgeExecution.UntilCancelled)
                {
                    yield return Terminal(invocationId, commandId, "cancelled", "运行时已取消这次执行。", "cancelled", "");
                    yield break;
                }

                if (cancelled)
                {
                    // A stream that ends without a terminal event; the gateway reports it honestly.
                    yield break;
                }

                yield return Terminal(
                    invocationId,
                    commandId,
                    "succeeded",
                    behavior.Summary.Length > 0 ? behavior.Summary : "命令执行完成。",
                    "",
                    "");
                yield break;

            default:
                yield return Terminal(
                    invocationId,
                    commandId,
                    "succeeded",
                    behavior.Summary.Length > 0 ? behavior.Summary : "命令执行完成。",
                    "",
                    "");
                yield break;
        }
    }

    private static HostExecutionEvent Terminal(
        string invocationId,
        string commandId,
        string state,
        string summary,
        string errorCode,
        string errorMessage) =>
        new(
            invocationId,
            commandId,
            state,
            summary,
            true,
            new ControlInvocationResult(invocationId, state, summary, "", errorCode, errorMessage, false, null));

    public static HostCommandDescriptor Command(
        string commandId,
        string moduleId,
        string title,
        string subtitle = "",
        string dangerLevel = "",
        bool requiresElevation = false,
        IReadOnlyList<ControlCommandParameter>? parameters = null,
        JsonObject? execution = null) =>
        new(
            commandId,
            moduleId,
            title,
            subtitle,
            dangerLevel,
            requiresElevation,
            false,
            true,
            parameters ?? [],
            [],
            execution);

    public static HostToolDescriptor Tool(
        string toolId,
        string moduleId,
        string title,
        string description,
        string category = "Everyday") =>
        new(toolId, moduleId, title, description, category, "ready", "available");

    public static ControlCommandParameter Parameter(string id, string label, string type, bool required, string defaultValue = "") =>
        new(id, label, type, required, defaultValue);
}
