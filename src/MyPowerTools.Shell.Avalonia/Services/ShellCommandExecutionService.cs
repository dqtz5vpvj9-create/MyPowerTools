using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MyPowerTools.HostControl;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class ShellCommandExecutionService
{
    private const int MaxInlineStatusLength = 240;

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
            FormatStatusText(response.State, response.Summary),
            response,
            string.Equals(response.State, "permission-required", StringComparison.OrdinalIgnoreCase));
    }

    public async IAsyncEnumerable<ShellCommandExecutionEvent> ExecuteStreamAsync(string invocationId, string commandId, JsonObject? args = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        await foreach (var evt in client.ExecuteCommandStreamAsync(invocationId, commandId, args ?? new JsonObject(), cancellationToken))
        {
            var final = evt.FinalResponse;
            var statusText = FormatStatusText(evt.State, evt.Message);
            yield return new ShellCommandExecutionEvent(
                statusText,
                evt,
                final is not null && string.Equals(final.State, "permission-required", StringComparison.OrdinalIgnoreCase));
        }
    }

    internal static string FormatStatusText(string state, string summary)
    {
        var normalizedState = string.IsNullOrWhiteSpace(state) ? "unknown" : state.Trim();
        var normalizedSummary = summary?.Trim() ?? "";
        var isStructured = normalizedSummary.StartsWith('{') ||
                           normalizedSummary.StartsWith('[');
        if (isStructured || normalizedSummary.Length > MaxInlineStatusLength)
        {
            return $"{normalizedState}: command completed; output is available in the tool.";
        }

        return string.IsNullOrWhiteSpace(normalizedSummary)
            ? normalizedState
            : $"{normalizedState}: {normalizedSummary}";
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

public sealed record ShellCommandExecutionEvent(
    string StatusText,
    HostProto.CommandExecutionEvent Event,
    bool RequiresPermissionPrompt);

public sealed record ShellCommandCancellationResult(
    bool Accepted,
    string InvocationId,
    string State,
    string Message);
