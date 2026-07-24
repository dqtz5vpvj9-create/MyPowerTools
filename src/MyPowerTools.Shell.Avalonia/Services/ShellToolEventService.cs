using MyPowerTools.HostControl;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class ShellToolEventService
{
    public async Task<ulong> PublishAsync(
        string toolId,
        string topic,
        string payload,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var published = await client.PublishToolEventAsync(toolId, topic, payload, cancellationToken);
        return published.EventSeq;
    }
}
