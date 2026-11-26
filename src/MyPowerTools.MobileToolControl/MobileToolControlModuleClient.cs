using System.Text.Json.Nodes;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileToolControl;

/// <summary>One failed module command. The module's own code is preserved for the page to branch on.</summary>
internal sealed class MobileToolControlModuleException(string code, string message, bool retryable = false)
    : Exception(message)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;

    /// <summary>True when the computer refused because the grant no longer authorizes the phone.</summary>
    public bool IsAuthorizationLost =>
        string.Equals(Code, MobileToolControlContract.ErrorUnauthorized, StringComparison.Ordinal);

    /// <summary>True when the computer could not be reached at all.</summary>
    public bool IsUnreachable =>
        string.Equals(Code, MobileToolControlContract.ErrorUnreachable, StringComparison.Ordinal) ||
        string.Equals(Code, MobileToolControlContract.ErrorTimeout, StringComparison.Ordinal) ||
        string.Equals(Code, MobileToolControlContract.ErrorUnavailable, StringComparison.Ordinal);
}

/// <summary>The result of parsing a connection code; nothing is saved at this point.</summary>
internal sealed record MobileToolImportPreview(
    bool Ok,
    string Error,
    string DeviceName,
    string Endpoint,
    string GrantId,
    bool AlreadyImported);

/// <summary>The result of an explicit connection check.</summary>
internal sealed record MobileToolDeviceCheck(bool Reachable, string Detail, string DeviceName, int CommandCount);

/// <summary>
/// Thin client for the <c>mobile-tool-control</c> module. Every effect is one module command: the page
/// opens no socket, reads no secret and writes no file, so the module stays the only owner of the
/// gateway client, the device records and the platform secret store.
/// </summary>
internal sealed class MobileToolControlModuleClient(MptAvaloniaSurfaceContext context)
{
    private readonly MptAvaloniaSurfaceContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async Task<IReadOnlyList<MobileToolDeviceItem>> DevicesAsync(CancellationToken cancellationToken) =>
        MobileToolControlJson.Devices(await CallAsync(
            MobileToolControlContract.CommandDevicesList,
            null,
            cancellationToken).ConfigureAwait(true));

    public async Task<MobileToolImportPreview> PreviewImportAsync(string code, CancellationToken cancellationToken)
    {
        var payload = await CallAsync(
            MobileToolControlContract.CommandImportPreview,
            new JsonObject { [MobileToolControlContract.ArgumentCode] = code },
            cancellationToken).ConfigureAwait(true);

        return new MobileToolImportPreview(
            MobileToolControlJson.Ok(payload),
            MobileToolControlJson.Text(payload, "error"),
            MobileToolControlJson.Text(payload, "deviceName"),
            MobileToolControlJson.Text(payload, "endpoint"),
            MobileToolControlJson.Text(payload, "grantId"),
            MobileToolControlJson.Flag(payload, "alreadyImported"));
    }

    public async Task<IReadOnlyList<MobileToolDeviceItem>> ConfirmImportAsync(
        string code,
        CancellationToken cancellationToken)
    {
        var payload = await CallAsync(
            MobileToolControlContract.CommandImportConfirm,
            new JsonObject
            {
                [MobileToolControlContract.ArgumentCode] = code,
                [MobileToolControlContract.ArgumentAccepted] = true
            },
            cancellationToken).ConfigureAwait(true);
        return MobileToolControlJson.Devices(payload);
    }

    public async Task<IReadOnlyList<MobileToolDeviceItem>> RemoveDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var payload = await CallAsync(
            MobileToolControlContract.CommandDevicesRemove,
            new JsonObject { [MobileToolControlContract.ArgumentDeviceId] = deviceId },
            cancellationToken).ConfigureAwait(true);
        return MobileToolControlJson.Devices(payload);
    }

    public async Task<MobileToolDeviceCheck> CheckDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var payload = await CallAsync(
            MobileToolControlContract.CommandDevicesCheck,
            new JsonObject { [MobileToolControlContract.ArgumentDeviceId] = deviceId },
            cancellationToken).ConfigureAwait(true);

        return new MobileToolDeviceCheck(
            MobileToolControlJson.Flag(payload, "reachable"),
            MobileToolControlJson.Text(payload, "detail"),
            MobileToolControlJson.Text(payload, "deviceName"),
            (int)Number(payload, "commandCount"));
    }

    public async Task<MobileToolCatalogSnapshot> CatalogAsync(
        string deviceId,
        string fallbackName,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var payload = await CallAsync(
            MobileToolControlContract.CommandCatalog,
            new JsonObject
            {
                [MobileToolControlContract.ArgumentDeviceId] = deviceId,
                [MobileToolControlContract.ArgumentRefresh] = refresh
            },
            cancellationToken).ConfigureAwait(true);
        return MobileToolControlJson.Catalog(deviceId, fallbackName, payload);
    }

    public async Task<MobileToolInvocationSnapshot> InvokeAsync(
        string deviceId,
        string commandId,
        JsonObject args,
        string invocationId,
        CancellationToken cancellationToken)
    {
        var payload = await CallAsync(
            MobileToolControlContract.CommandInvoke,
            new JsonObject
            {
                [MobileToolControlContract.ArgumentDeviceId] = deviceId,
                [MobileToolControlContract.ArgumentCommandId] = commandId,
                [MobileToolControlContract.ArgumentInvocationId] = invocationId,
                [MobileToolControlContract.ArgumentArgs] = args
            },
            cancellationToken).ConfigureAwait(true);
        return MobileToolControlJson.Invocation(deviceId, payload);
    }

    public async Task<MobileToolInvocationSnapshot> InvocationStatusAsync(
        string deviceId,
        string invocationId,
        CancellationToken cancellationToken)
    {
        var payload = await CallAsync(
            MobileToolControlContract.CommandInvocationStatus,
            new JsonObject
            {
                [MobileToolControlContract.ArgumentDeviceId] = deviceId,
                [MobileToolControlContract.ArgumentInvocationId] = invocationId
            },
            cancellationToken).ConfigureAwait(true);
        return MobileToolControlJson.Invocation(deviceId, payload);
    }

    public async Task<MobileToolInvocationSnapshot> CancelAsync(
        string deviceId,
        string invocationId,
        CancellationToken cancellationToken)
    {
        var payload = await CallAsync(
            MobileToolControlContract.CommandInvokeCancel,
            new JsonObject
            {
                [MobileToolControlContract.ArgumentDeviceId] = deviceId,
                [MobileToolControlContract.ArgumentInvocationId] = invocationId
            },
            cancellationToken).ConfigureAwait(true);
        return MobileToolControlJson.Invocation(deviceId, payload);
    }

    private async Task<JsonObject> CallAsync(
        string commandId,
        JsonObject? args,
        CancellationToken cancellationToken)
    {
        var result = await _context.ExecuteCommandAsync(commandId, args, cancellationToken).ConfigureAwait(true);
        var payload = Parse(result.Output);
        if (result.Success)
        {
            return payload;
        }

        var message = result.Error?.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "电脑工具模块命令执行失败。";
        }

        throw new MobileToolControlModuleException(
            result.Error?.Code ?? "",
            message!,
            result.Error?.Retryable ?? false);
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
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }

    private static double Number(JsonObject document, string name) =>
        document.TryGetPropertyValue(name, out var node) && node is JsonValue value &&
        value.TryGetValue<double>(out var number)
            ? number
            : 0d;
}
