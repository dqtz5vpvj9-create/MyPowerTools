namespace FileTransfer.Core.Assistant;

public enum SharedRelayRoute { Public, Tail }

/// <summary>Small public discovery record. Only a fixed route id, never an address, travels on the wire.</summary>
public sealed record SharedLocator
{
    public const string TailPayloadRoute = "mpt-tail-relay-v1";
    public int Version { get; init; } = 1;
    public AssistantManifest Message { get; init; } = new();
    public string PayloadRoute { get; init; } = TailPayloadRoute;
}

/// <summary>Written only after the whole public payload has been accepted; never a delivery receipt.</summary>
internal sealed record SharedPublicCopyCompletion(int Version, string ItemId, DateTimeOffset CommittedAt);

public sealed record SharedPublicCopyRequest
{
    public int Version { get; init; } = 1;
    public string ItemId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public DateTimeOffset RequestedAt { get; init; }
    public string Reason { get; init; } = "tail-unreachable";
}

public sealed record SharedLocatorPage(IReadOnlyList<SharedLocator> Items,
    IReadOnlyList<string> InvalidItemIds, bool HasMore, string? NextCursor);
public sealed record SharedRequestPage(IReadOnlyList<SharedPublicCopyRequest> Items,
    IReadOnlyList<string> InvalidNames, bool LimitExceeded);
public sealed record SharedPublishResult(bool TailStored, bool LocatorPublished, bool PublicStored);
public sealed record SharedReceiveResult(string? Path, bool WaitingForPublicCopy, string? Error = null);


internal static class SharedLocatorRules
{
    public const int LocatorBytes = 16 * 1024;
    public const int RequestBytes = 2 * 1024;
    public const int ListingBytes = 2 * 1024 * 1024;

    public static AssistantManifest Message(AssistantManifest message, string? expectedId = null)
    {
        message = AssistantValidation.Manifest(message);
        if (message.TargetDeviceId is not null) throw new InvalidDataException("私聊条目不能发布到共享会话。");
        if (expectedId is not null && message.Id != expectedId) throw new InvalidDataException("共享记录与目录不一致。");
        return message;
    }

    public static SharedLocator Locator(SharedLocator locator, string? expectedId = null)
    {
        if (locator.Version != 1 || locator.PayloadRoute != SharedLocator.TailPayloadRoute)
            throw new InvalidDataException("共享附件使用了不支持的定位协议或路由。");
        var message = Message(locator.Message, expectedId);
        if (message.Kind == AssistantItemKind.Text) throw new InvalidDataException("文字消息不需要附件定位记录。");
        return locator with { Message = message };
    }

    public static SharedPublicCopyRequest Request(SharedPublicCopyRequest request, string expectedId, string expectedDevice)
    {
        AssistantValidation.ItemId(request.ItemId);
        AssistantValidation.DeviceId(request.DeviceId);
        if (request.Version != 1 || request.ItemId != expectedId || request.DeviceId != expectedDevice
            || request.RequestedAt == default || request.Reason is not ("tail-unreachable" or "tail-unavailable"))
            throw new InvalidDataException("共享附件请求与目录不一致或格式无效。");
        return request;
    }

    public static void SameMessage(AssistantManifest expected, AssistantManifest actual)
    {
        if (!AssistantValidation.SameMessage(expected, actual))
            throw new InvalidDataException("同一共享条目 id 对应了不同内容，已拒绝合并。");
    }
}
