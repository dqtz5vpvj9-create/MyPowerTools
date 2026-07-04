using System.Text.Json.Nodes;
using MyPowerTools.HostControl;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class ShellCommandExecutionService
{
    public async Task<ShellCommandExecutionResult> ExecuteAsync(string commandId, JsonObject? args = null, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(Guid.NewGuid().ToString("N"), commandId, args, cancellationToken);
    }

    public async Task<ShellCommandExecutionResult> ExecuteAsync(string invocationId, string commandId, JsonObject? args = null, CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var response = args is null
            ? await client.ExecuteCommandAsync(invocationId, commandId, new JsonObject(), cancellationToken)
            : await client.ExecuteCommandAsync(invocationId, commandId, args, cancellationToken);
        return new ShellCommandExecutionResult(
            $"{response.State}: {response.Summary}",
            response,
            string.Equals(response.State, "permission-required", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ShellCommandCancellationResult> CancelAsync(string invocationId, CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var response = await client.CancelCommandAsync(invocationId, cancellationToken);
        return new ShellCommandCancellationResult(
            response.Accepted,
            response.InvocationId,
            response.State,
            response.Message);
    }
}

public sealed record ShellCommandExecutionResult(
    string StatusText,
    HostProto.CommandExecutionResponse Response,
    bool RequiresPermissionPrompt);

public sealed record ShellCommandCancellationResult(
    bool Accepted,
    string InvocationId,
    string State,
    string Message);
