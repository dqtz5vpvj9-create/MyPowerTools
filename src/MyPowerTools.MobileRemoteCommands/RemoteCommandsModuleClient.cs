using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>
/// Thin client for the <c>remote-commands-android</c> module.
/// </summary>
/// <remarks>
/// The phone page owns no SSH implementation: every effect (reading the shared catalog, mapping a host,
/// confirming a fingerprint, running, cancelling, clearing history) is one module command. Secrets only
/// ever travel as <c>host.add</c> arguments; the module writes them to the platform secret store and the
/// page never renders them back.
///
/// Failures keep the module's error code: <see cref="MobileModuleException.PermissionRequired"/> lets the
/// page say "waiting for authorization" instead of reporting a generic error, without adding a command
/// or a permission system of its own.
/// </remarks>
internal sealed class RemoteCommandsModuleClient(MptAvaloniaSurfaceContext context)
{
    private readonly MptAvaloniaSurfaceContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>
    /// Executes a command with an invocation id the page owns
    /// (<see cref="MptAvaloniaSurfaceContext.ExecuteCommandWithInvocationAsync"/>). Older hosts leave the
    /// capability null; the plain call still runs the command, the page just cannot correlate the stream.
    /// </summary>
    private readonly Func<string, string, JsonObject?, CancellationToken, Task<CommandExecutionResult>>?
        _invocationExecutor = context?.ExecuteCommandWithInvocationAsync;

    public IDisposable? Subscribe(Action<MptSurfaceEvent> handler) => _context.SubscribeEvents?.Invoke(handler);

    /// <summary>
    /// True when the host lets the page own its command's invocation id, which is what the module keys its
    /// progress events and its precise cancellation on.
    /// </summary>
    public bool OwnsInvocationId => _invocationExecutor is not null;

    public async Task<MobileCatalogSnapshot> CatalogAsync(CancellationToken cancellationToken) =>
        RemoteCommandsMobileJson.Catalog(await CallAsync(
            RemoteCommandsMobileContract.CommandCatalog,
            null,
            cancellationToken).ConfigureAwait(true));

    /// <summary>
    /// Saves the shared commands.yaml. Validation happens in the module with the shipped parser; an
    /// invalid document comes back as a failed result whose message the page shows next to the editor.
    /// </summary>
    public Task SaveCatalogAsync(string content, CancellationToken cancellationToken) =>
        CallAsync(
            RemoteCommandsMobileContract.CommandCatalogSave,
            new JsonObject { [RemoteCommandsMobileContract.ArgumentContent] = content },
            cancellationToken);

    /// <summary>
    /// Saves the five settings through the module, which validates them with the same
    /// <c>UpdateSettingsAsync</c> implementation the Shell uses. A rejected value surfaces as the
    /// module's own message; the page never writes settings.json itself. Integers stay JSON integers:
    /// the Android HostControl trip turns them into protobuf doubles, and the module accepts an integral
    /// double as the same number (the 299/300 regression), while a fractional value is still rejected.
    /// </summary>
    public Task UpdateSettingsAsync(JsonObject values, CancellationToken cancellationToken) =>
        CallAsync(
            RemoteCommandsMobileContract.CommandSettingsUpdate,
            new JsonObject { [RemoteCommandsMobileContract.ArgumentSettingsValues] = values },
            cancellationToken);

    public async Task<MobileModuleState> StatusAsync(CancellationToken cancellationToken) =>
        RemoteCommandsMobileJson.State(await CallAsync(
            RemoteCommandsMobileContract.CommandStatus,
            null,
            cancellationToken).ConfigureAwait(true));

    public async Task<MobileHostsSnapshot> HostsAsync(CancellationToken cancellationToken) =>
        RemoteCommandsMobileJson.HostsSnapshot(await CallAsync(
            RemoteCommandsMobileContract.CommandHostsList,
            null,
            cancellationToken).ConfigureAwait(true));

    public async Task<IReadOnlyList<MobileHostKeyEntry>> HostKeysAsync(CancellationToken cancellationToken) =>
        RemoteCommandsMobileJson.HostKeys(await CallAsync(
            RemoteCommandsMobileContract.CommandHostKeyStatus,
            null,
            cancellationToken).ConfigureAwait(true));

    public async Task<MobileHistorySummary> HistoryAsync(CancellationToken cancellationToken) =>
        RemoteCommandsMobileJson.History(await CallAsync(
            RemoteCommandsMobileContract.CommandHistorySummary,
            null,
            cancellationToken).ConfigureAwait(true));

    public Task ClearHistoryAsync(CancellationToken cancellationToken) =>
        CallAsync(RemoteCommandsMobileContract.CommandHistoryClear, null, cancellationToken);

    public async Task<MobileHostsSnapshot> AddHostAsync(
        string alias,
        string host,
        int port,
        string username,
        string authKind,
        string? password,
        string? privateKey,
        string? passphrase,
        CancellationToken cancellationToken)
    {
        var args = new JsonObject
        {
            [RemoteCommandsMobileContract.ArgumentAlias] = alias,
            [RemoteCommandsMobileContract.ArgumentRealHost] = host,
            [RemoteCommandsMobileContract.ArgumentPort] = port,
            [RemoteCommandsMobileContract.ArgumentUsername] = username,
            [RemoteCommandsMobileContract.ArgumentAuthKind] = authKind
        };

        // Empty secret fields mean "keep what the secret store already has"; they are never sent.
        if (!string.IsNullOrEmpty(password))
        {
            args[RemoteCommandsMobileContract.ArgumentPassword] = password;
        }

        if (!string.IsNullOrWhiteSpace(privateKey))
        {
            args[RemoteCommandsMobileContract.ArgumentPrivateKey] = privateKey;
        }

        if (!string.IsNullOrEmpty(passphrase))
        {
            args[RemoteCommandsMobileContract.ArgumentPassphrase] = passphrase;
        }

        var payload = await CallAsync(RemoteCommandsMobileContract.CommandHostAdd, args, cancellationToken)
            .ConfigureAwait(true);
        return RemoteCommandsMobileJson.HostsSnapshot(payload);
    }

    public Task RemoveHostAsync(string alias, CancellationToken cancellationToken) =>
        CallAsync(
            RemoteCommandsMobileContract.CommandHostRemove,
            new JsonObject { [RemoteCommandsMobileContract.ArgumentAlias] = alias },
            cancellationToken);

    /// <summary>
    /// Records one exact fingerprint. There is deliberately no "trust all" call: the module verifies
    /// every later connection against this value.
    /// </summary>
    public Task AcceptHostKeyAsync(
        string host,
        int port,
        string hostKeyName,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var args = new JsonObject
        {
            [RemoteCommandsMobileContract.ArgumentRealHost] = host,
            [RemoteCommandsMobileContract.ArgumentPort] = port,
            [RemoteCommandsMobileContract.ArgumentFingerprint] = fingerprint
        };

        if (!string.IsNullOrWhiteSpace(hostKeyName))
        {
            args[RemoteCommandsMobileContract.ArgumentHostKeyName] = hostKeyName;
        }

        return CallAsync(RemoteCommandsMobileContract.CommandHostKeyAccept, args, cancellationToken);
    }

    public Task RevokeHostKeyAsync(string host, int port, CancellationToken cancellationToken) =>
        CallAsync(
            RemoteCommandsMobileContract.CommandHostKeyRevoke,
            new JsonObject
            {
                [RemoteCommandsMobileContract.ArgumentRealHost] = host,
                [RemoteCommandsMobileContract.ArgumentPort] = port
            },
            cancellationToken);

    public async Task<bool> CancelAsync(string? invocationId, CancellationToken cancellationToken)
    {
        var args = new JsonObject();

        // With a caller-owned id the cancel names exactly this run. Without one the page cannot know the
        // id the host assigned, and the module refuses a mismatched id, so the argument is omitted: the
        // runner then cancels its single active run, which is the one this page started.
        var precise = OwnsInvocationId && !string.IsNullOrWhiteSpace(invocationId);
        if (precise)
        {
            args[RemoteCommandsMobileContract.ArgumentInvocationId] = invocationId;
        }

        var payload = await CallAsync(
            RemoteCommandsMobileContract.CommandCancel,
            args,
            precise ? invocationId : null,
            cancellationToken).ConfigureAwait(true);
        return RemoteCommandsMobileJson.Flag(payload, "cancelled");
    }

    /// <summary>
    /// Runs one configured command. A run reports its product state in the payload even when the state
    /// is not success (<c>host-key-required</c>, <c>failed</c>, <c>cancelled</c>), so the payload is
    /// parsed first and the error object is only used when the module returned no run payload at all.
    ///
    /// The invocation id is the one the host puts on the command envelope: the module keys its
    /// <c>run.stage</c>/<c>command.output</c> events and its active-invocation cancellation on it, so the
    /// page must own it (<c>ExecuteCommandWithInvocationAsync</c>) for progress and cancel to line up.
    /// </summary>
    public async Task<MobileRunOutcome> RunAsync(
        string commandId,
        string host,
        string input1,
        string input2,
        bool secondInput,
        string invocationId,
        CancellationToken cancellationToken)
    {
        var args = new JsonObject
        {
            [RemoteCommandsMobileContract.ArgumentCommandId] = commandId,
            [RemoteCommandsMobileContract.ArgumentHost] = host,
            [RemoteCommandsMobileContract.ArgumentInput1] = input1,
            [RemoteCommandsMobileContract.ArgumentInput2] = input2,
            [RemoteCommandsMobileContract.ArgumentSecondInput] = secondInput,
            [RemoteCommandsMobileContract.ArgumentInvocationId] = invocationId
        };

        var (payload, error, errorCode) = await CallWithErrorAsync(
            RemoteCommandsMobileContract.CommandRun,
            args,
            invocationId,
            cancellationToken).ConfigureAwait(true);

        var outcome = RemoteCommandsMobileJson.Run(payload);
        if (string.IsNullOrWhiteSpace(outcome.State))
        {
            // No product payload: the command never reached the runner (broker approval, unknown id,
            // missing module). Report the module's own code and message instead of a made-up state.
            throw new MobileModuleException(
                string.IsNullOrWhiteSpace(error) ? "远程命令未返回执行状态。" : error!,
                errorCode ?? "",
                IsPermissionCode(errorCode));
        }

        return string.IsNullOrWhiteSpace(outcome.Message) && !string.IsNullOrWhiteSpace(error)
            ? outcome with { Message = error! }
            : outcome;
    }

    private static bool IsPermissionCode(string? code) =>
        string.Equals(code, RemoteCommandsMobileContract.ErrorPermissionRequired, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, RemoteCommandsMobileContract.StatePermissionRequired, StringComparison.OrdinalIgnoreCase);

    private async Task<JsonObject> CallAsync(string commandId, JsonObject? args, CancellationToken cancellationToken) =>
        await CallAsync(commandId, args, invocationId: null, cancellationToken).ConfigureAwait(true);

    private async Task<JsonObject> CallAsync(
        string commandId,
        JsonObject? args,
        string? invocationId,
        CancellationToken cancellationToken)
    {
        var (payload, error, errorCode) = await CallWithErrorAsync(commandId, args, invocationId, cancellationToken)
            .ConfigureAwait(true);
        return error is null ? payload : throw new MobileModuleException(error, errorCode ?? "", IsPermissionCode(errorCode));
    }

    private async Task<(JsonObject Payload, string? Error, string? ErrorCode)> CallWithErrorAsync(
        string commandId,
        JsonObject? args,
        string? invocationId,
        CancellationToken cancellationToken)
    {
        // Older hosts have no caller-owned invocation id; the plain call still works, the page just
        // cannot correlate streamed events with its own run.
        var result = _invocationExecutor is { } executeWithInvocation
            ? await executeWithInvocation(
                string.IsNullOrWhiteSpace(invocationId) ? Guid.NewGuid().ToString("N") : invocationId,
                commandId,
                args,
                cancellationToken).ConfigureAwait(true)
            : await _context.ExecuteCommandAsync(commandId, args, cancellationToken).ConfigureAwait(true);
        var payload = Parse(result.Output);
        if (result.Success)
        {
            return (payload, null, null);
        }

        var message = result.Error?.Message;
        if (string.IsNullOrWhiteSpace(message) && payload.Count == 0)
        {
            message = "模块命令执行失败。";
        }

        return (payload, message, result.Error?.Code);
    }

    private static JsonObject Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(output) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
