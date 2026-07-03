namespace MyPowerTools.Platform.Abstractions;

public sealed record CapabilityDescriptor(
    string Id,
    string PermissionLevel,
    bool Supported,
    string Provider,
    string Message);

public sealed record CapabilityRequest(string ModuleId, string Capability, bool Required, string Reason);

public sealed record CapabilityResolution(string ModuleId, string State, IReadOnlyList<CapabilityDescriptor> Capabilities, IReadOnlyList<string> Messages)
{
    public bool IsUsable => State is "ready" or "degraded";
}

public interface ICapabilityRegistry
{
    IReadOnlyList<CapabilityDescriptor> All { get; }
    CapabilityDescriptor Resolve(string capabilityId);
    CapabilityResolution ResolveForModule(string moduleId, IEnumerable<CapabilityRequest> requests);
}

public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly Dictionary<string, CapabilityDescriptor> _capabilities;

    public CapabilityRegistry(IEnumerable<CapabilityDescriptor> capabilities)
    {
        _capabilities = capabilities.ToDictionary(capability => capability.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CapabilityDescriptor> All => _capabilities.Values.OrderBy(capability => capability.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    public CapabilityDescriptor Resolve(string capabilityId)
    {
        return _capabilities.TryGetValue(capabilityId, out var descriptor)
            ? descriptor
            : new CapabilityDescriptor(capabilityId, "unknown", false, "none", "Capability is not registered on this platform.");
    }

    public CapabilityResolution ResolveForModule(string moduleId, IEnumerable<CapabilityRequest> requests)
    {
        var requestList = requests.ToArray();
        var capabilities = requestList.Select(request => Resolve(request.Capability)).ToArray();
        var messages = capabilities.Where(capability => !capability.Supported).Select(capability => $"{capability.Id}: {capability.Message}").ToArray();
        var hasMissingRequired = requestList.Any(request => request.Required && !Resolve(request.Capability).Supported);
        var hasMissingOptional = requestList.Any(request => !request.Required && !Resolve(request.Capability).Supported);
        var state = hasMissingRequired ? "unsupported" : hasMissingOptional ? "degraded" : "ready";
        return new CapabilityResolution(moduleId, state, capabilities, messages);
    }
}
