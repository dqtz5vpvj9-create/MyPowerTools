using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MyPowerTools.HostControl;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace RemoteToolGateway.Core;

public sealed record HostCommandDescriptor(
    string CommandId,
    string ModuleId,
    string Title,
    string Subtitle,
    string DangerLevel,
    bool RequiresElevation,
    bool SupportsProgress,
    bool SupportsCancellation,
    IReadOnlyList<ControlCommandParameter> Parameters,
    IReadOnlyList<string> Constraints,
    JsonObject? Execution);

public sealed record HostToolDescriptor(
    string ToolId,
    string ModuleId,
    string Title,
    string Description,
    string Category,
    string State,
    string Availability);

public sealed record HostCatalog(IReadOnlyList<HostToolDescriptor> Tools, IReadOnlyList<HostCommandDescriptor> Commands);

public sealed record HostExecutionEvent(
    string InvocationId,
    string CommandId,
    string State,
    string Message,
    bool Terminal,
    ControlInvocationResult? FinalResponse);

public sealed record HostCancellation(bool Accepted, string InvocationId, string State, string Message);

/// <summary>
/// The narrow boundary between the gateway and the local runtime. Only this interface is swapped
/// in tests; production always uses <see cref="HostControlClientBridge"/>, which speaks the same
/// local IPC as the Shell. The gateway never duplicates command execution, cancellation,
/// RuntimeOperationPolicy, module confirmation tokens, Broker or UAC handling.
/// </summary>
public interface IHostControlBridge
{
    Task<HostCatalog> GetCatalogAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<HostExecutionEvent> ExecuteStreamAsync(string invocationId, string commandId, JsonObject args, CancellationToken cancellationToken);
    Task<HostCancellation> CancelAsync(string invocationId, CancellationToken cancellationToken);
    Task<bool> PingAsync(CancellationToken cancellationToken);
}

public sealed class HostControlClientBridge : IHostControlBridge
{
    public async Task<HostCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var tools = await client.ListToolsAsync(includeDisabled: false, cancellationToken);
        var commands = await client.ListCommandsAsync("", cancellationToken);
        return new HostCatalog(
            tools.Tools.Select(MapTool).Take(ControlWire.MaxCatalogTools).ToArray(),
            commands.Commands.Select(MapCommand).Take(ControlWire.MaxCatalogCommands).ToArray());
    }

    public async IAsyncEnumerable<HostExecutionEvent> ExecuteStreamAsync(
        string invocationId,
        string commandId,
        JsonObject args,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        await foreach (var evt in client.ExecuteCommandStreamAsync(invocationId, commandId, args, cancellationToken))
        {
            yield return new HostExecutionEvent(
                evt.InvocationId,
                evt.CommandId,
                evt.State,
                ControlText.Bound(evt.Message, ControlWire.MaxMessageLength),
                evt.Terminal,
                evt.FinalResponse is null ? null : MapResponse(evt.FinalResponse));
        }
    }

    public async Task<HostCancellation> CancelAsync(string invocationId, CancellationToken cancellationToken)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var response = await client.CancelCommandAsync(invocationId, cancellationToken);
        return new HostCancellation(
            response.Accepted,
            ControlText.Bound(response.InvocationId, ControlWire.MaxInvocationIdLength),
            ControlText.Bound(response.State, ControlWire.MaxTextLength),
            ControlText.Bound(response.Message, ControlWire.MaxMessageLength));
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var response = await client.PingAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(response.State);
    }

    internal static ControlInvocationResult MapResponse(HostProto.CommandExecutionResponse response) => new(
        ControlText.Bound(response.InvocationId, ControlWire.MaxInvocationIdLength),
        ControlText.Bound(response.State, ControlWire.MaxTextLength),
        ControlText.Bound(response.Summary, ControlWire.MaxSummaryLength),
        ControlText.Bound(response.LogCursor, ControlWire.MaxTextLength),
        ControlText.Bound(response.ErrorCode, ControlWire.MaxTextLength),
        ControlText.Bound(response.ErrorMessage, ControlWire.MaxMessageLength),
        response.Retryable,
        ControlText.BoundDetails(JsonStructMapper.ToJsonObject(response.ErrorDetails)));

    private static HostToolDescriptor MapTool(HostProto.ToolDescriptor tool) => new(
        ControlText.Bound(tool.ToolId, ControlWire.MaxTextLength),
        ControlText.Bound(tool.OwnerModuleId, ControlWire.MaxTextLength),
        ControlText.Bound(tool.Title, ControlWire.MaxTextLength),
        ControlText.Bound(tool.Description, ControlWire.MaxSummaryLength),
        ControlText.Bound(tool.Category, ControlWire.MaxTextLength),
        ControlText.Bound(tool.State, ControlWire.MaxTextLength),
        ControlText.Bound(tool.Availability, ControlWire.MaxTextLength));

    private static HostCommandDescriptor MapCommand(HostProto.CommandItem command) => new(
        ControlText.Bound(command.CommandId, ControlWire.MaxCommandIdLength),
        ControlText.Bound(command.ModuleId, ControlWire.MaxTextLength),
        ControlText.Bound(command.Title, ControlWire.MaxTextLength),
        ControlText.Bound(command.Subtitle, ControlWire.MaxTextLength),
        ControlText.Bound(command.DangerLevel, ControlWire.MaxTextLength),
        command.RequiresElevation,
        command.SupportsProgress,
        command.SupportsCancellation,
        command.Parameters.Select(parameter => new ControlCommandParameter(
            ControlText.Bound(parameter.Id, ControlWire.MaxTextLength),
            ControlText.Bound(parameter.Label, ControlWire.MaxTextLength),
            ControlText.Bound(parameter.Type, ControlWire.MaxTextLength),
            parameter.Required,
            ControlText.Bound(parameter.DefaultValue, ControlWire.MaxTextLength))).ToArray(),
        command.Constraints.Select(constraint => ControlText.Bound(constraint, ControlWire.MaxTextLength)).ToArray(),
        command.Execution is null ? null : JsonStructMapper.ToJsonObject(command.Execution));
}
