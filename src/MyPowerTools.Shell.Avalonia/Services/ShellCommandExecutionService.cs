using MyPowerTools.HostControl;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class ShellCommandExecutionService
{
    public async Task<ShellCommandExecutionResult> ExecuteAsync(string commandId, CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var response = await client.ExecuteCommandAsync(commandId, cancellationToken);
        return new ShellCommandExecutionResult(
            $"{response.State}: {response.Summary}",
            response,
            string.Equals(response.State, "permission-required", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record ShellCommandExecutionResult(
    string StatusText,
    HostProto.CommandExecutionResponse Response,
    bool RequiresPermissionPrompt);
