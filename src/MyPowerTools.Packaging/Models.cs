using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyPowerTools.Packaging;

public sealed class MptPackageManifest
{
    public string SchemaVersion { get; init; } = "";
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Version { get; init; } = "";
    public string? Publisher { get; init; }
    public string? MinHostVersion { get; init; }
    public List<string> Modules { get; init; } = [];
    public MptSharedManifest? Shared { get; init; }
    public string? Hashes { get; init; }
    public MptPackageTrustManifest? Trust { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptSharedManifest
{
    public List<MptSharedRuntimeManifest> Runtimes { get; init; } = [];
    public string? Assets { get; init; }
    public string? DataDir { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptSharedRuntimeManifest
{
    public string Id { get; init; } = "";
    public List<MptEntrypointManifest> Entrypoints { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptPackageTrustManifest
{
    public string Policy { get; init; } = "local";
    public MptPackageSignatureManifest? Signature { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptPackageSignatureManifest
{
    public string Path { get; init; } = "";
    public string Format { get; init; } = "mpt-signature-v1";
    public bool Required { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptModuleManifest
{
    public string SchemaVersion { get; init; } = "";
    public string Id { get; init; } = "";
    public string PackageId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Version { get; init; } = "";
    public string ModuleSdk { get; init; } = "";
    public List<MptEntrypointManifest> Entrypoints { get; init; } = [];
    public List<string> Capabilities { get; init; } = [];
    public List<MptRequirementManifest> Requires { get; init; } = [];
    public List<MptPermissionManifest> Permissions { get; init; } = [];
    public List<string> Surfaces { get; init; } = [];
    public List<string> UiSurfaces { get; init; } = [];
    public List<string> Tools { get; init; } = [];
    public List<MptHotkeyManifest> Hotkeys { get; init; } = [];
    public Dictionary<string, JsonElement>? StaticIndexes { get; init; }
    public MptRuntimePolicyManifest? RuntimePolicy { get; init; }
    public MptDevelopmentManifest? Development { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptToolManifest
{
    public string SchemaVersion { get; init; } = "";
    public string ToolId { get; init; } = "";
    public string OwnerModuleId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Category { get; init; } = "";
    public string Availability { get; init; } = "available";
    public string Type { get; init; } = "";
    public string PrimaryRouteId { get; init; } = "";
    public List<MptToolRouteManifest> Routes { get; init; } = [];
    public MptToolHomeCardManifest HomeCard { get; init; } = new();
    public MptToolRuntimeManifest? Runtime { get; init; }
    public MptToolSettingsManifest? Settings { get; init; }
    public List<MptToolCommandManifest> Commands { get; init; } = [];
    public List<MptPermissionManifest> Permissions { get; init; } = [];
    public List<string> DataRoots { get; init; } = [];
    public string DataRetention { get; init; } = "preserve";
    public MptToolSourceManifest? Source { get; init; }
    public MptToolDevelopmentManifest? Development { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptToolRouteManifest
{
    public string RouteId { get; init; } = "";
    public string SurfaceId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "";
    public MptToolSurfaceManifest? Surface { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptToolSurfaceManifest
{
    public string Kind { get; init; } = "";
    public string Source { get; init; } = "";
    public string StaticRoot { get; init; } = "";
    public string Assembly { get; init; } = "";
    public string Type { get; init; } = "";
    public bool OpenExternal { get; init; }
    public List<string> AllowedOrigins { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptToolRuntimeManifest
{
    public string Transport { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string Command { get; init; } = "";
    public List<string> Args { get; init; } = [];
    public string HealthPath { get; init; } = "/api/status";
    public string LogsPath { get; init; } = "";
    public int TimeoutMs { get; init; } = 5000;
    public bool Remote { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptToolSettingsManifest
{
    public string Schema { get; init; } = "settings.schema.json";
    public string Values { get; init; } = "settings.json";
    public List<string> Secrets { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptToolCommandManifest
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptToolSourceManifest
{
    public string Repository { get; init; } = "";
    public string Revision { get; init; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptToolDevelopmentManifest
{
    public bool Loose { get; init; } = true;
    public bool AutoRefresh { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptToolHomeCardManifest
{
    public string Summary { get; init; } = "";
    public string PrimaryActionLabel { get; init; } = "Open";
    public string StatusBinding { get; init; } = "";
    public int Order { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptEntrypointManifest
{
    public string Kind { get; init; } = "";
    public int Priority { get; init; }
    public List<string> Platforms { get; init; } = [];
    public string? Assembly { get; init; }
    public string? Type { get; init; }
    public string? Command { get; init; }
    public List<string> Args { get; init; } = [];
    public string? RuntimeId { get; init; }
    public string? Service { get; init; }
    public string? BaseUrl { get; init; }
    public IpcEndpointManifest? Windows { get; init; }
    public IpcEndpointManifest? Macos { get; init; }
    public IpcEndpointManifest? Linux { get; init; }
    public JsonElement? Health { get; init; }
    public bool Compat { get; init; }
    public int? StartupCost { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class IpcEndpointManifest
{
    public string? Transport { get; init; }
    public string? Name { get; init; }
    public string? Path { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptRuntimePolicyManifest
{
    public string Preferred { get; init; } = "";
    public bool AllowInProc { get; init; }
    public MptInProcRuntimeRulesManifest? InProcRules { get; init; }
    public MptSidecarRuntimeRulesManifest? SidecarRules { get; init; }
    public MptRuntimeOperationRulesManifest? OperationRules { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptRuntimeOperationRulesManifest
{
    public string? Status { get; init; }
    public string? Settings { get; init; }
    public string? CommandProvider { get; init; }
    public string? LongRunningCommand { get; init; }
    public string? SystemMutation { get; init; }
    public string? NativeHardware { get; init; }
    public string? ElevatedWrite { get; init; }
    public string? ExternalProcess { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptInProcRuntimeRulesManifest
{
    public int? MaxCallMs { get; init; }
    public bool AllowNativeDll { get; init; }
    public bool AllowWindow { get; init; }
    public bool AllowBackgroundThreads { get; init; }
    public string? LoadContext { get; init; }
    public bool? ShadowCopy { get; init; }
    public List<string> SharedAssemblies { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptSidecarRuntimeRulesManifest
{
    public int? ReadyTimeoutMs { get; init; }
    public int? RestartLimit { get; init; }
    public int? RestartWindowSeconds { get; init; }
    public bool? KillProcessTree { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptDevelopmentManifest
{
    public bool AllowAlreadyLoadedFallback { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = [];
}

public sealed class MptRequirementManifest
{
    public string Capability { get; init; } = "";
    public bool Required { get; init; }
    public string? Reason { get; init; }
}

public sealed class MptPermissionManifest
{
    public string Id { get; init; } = "";
    public string Level { get; init; } = "";
    public string? Capability { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class MptHotkeyManifest
{
    public string Id { get; init; } = "";
    public string Default { get; init; } = "";
    public string CommandId { get; init; } = "";
    public string Scope { get; init; } = "module";
    public string Reason { get; init; } = "";
    public bool EnabledByDefault { get; init; } = true;
}

public sealed class MptUiSurfaceManifest
{
    public string SchemaVersion { get; init; } = "";
    public string SurfaceId { get; init; } = "";
    public string ModuleId { get; init; } = "";
    public string Kind { get; init; } = "";
    public MptUiSurfaceLayout Layout { get; init; } = new();
    public List<string> Uses { get; init; } = [];
    public List<string> States { get; init; } = [];
}

public sealed class MptUiSurfaceLayout
{
    public string Mode { get; init; } = "";
    public int? PreferredWidth { get; init; }
    public int? PreferredHeight { get; init; }
    public int? MinWidth { get; init; }
    public int? MinHeight { get; init; }
}

public sealed record MptPackageDefinition(
    string Directory,
    MptPackageManifest Package,
    IReadOnlyList<MptModuleDefinition> Modules,
    bool IsDevelopmentTool = false);

public sealed record MptModuleDefinition(
    string Directory,
    string ManifestPath,
    MptModuleManifest Manifest);

public sealed record ValidationIssue(string Path, string Severity, string Message);

public sealed record PackageValidationReport(string PackageDirectory, IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != "error");
}
