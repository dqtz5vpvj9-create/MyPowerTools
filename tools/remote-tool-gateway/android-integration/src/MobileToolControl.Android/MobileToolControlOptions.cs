namespace MobileToolControl.Android;

/// <summary>
/// Stable identifiers shared by the Android module, the phone surface, the packaged manifests and
/// the tests. Keeping them in one file is what lets a test assert that <c>package/module.json</c>,
/// <c>package/ui/tool.json</c> and the command contract still agree after a rename.
/// </summary>
internal static class MobileToolControlOptions
{
    /// <summary>
    /// Module id, package id and tool id intentionally agree. The desktop gateway declares its own
    /// id on the computer, so the two assemblies can coexist in one catalog.
    /// </summary>
    public const string ModuleId = "mobile-tool-control";

    public const string PackageId = ModuleId;

    public const string ToolId = ModuleId;

    public const string DisplayName = "电脑工具";

    public const string SurfaceAssemblyFileName = "MyPowerTools.MobileToolControl.dll";

    public const string ModuleAssemblyFileName = "MobileToolControl.Android.dll";

    public const string SurfaceTypeName =
        "MyPowerTools.MobileToolControl.MobileToolControlSurfaceFactory";

    public const string ModuleTypeName = "MobileToolControl.Android.MobileToolControlModule";

    /// <summary>
    /// The phone stores one imported computer per file. The token never appears here: it only lives in
    /// the platform <c>secret.store</c> under <c>secret://mobile-tool-control/&lt;deviceId&gt;.token</c>.
    /// </summary>
    public const string DevicesFileName = "devices.json";

    public const string SecretNameSuffix = ".token";

    /// <summary>Redacted token stand-in used whenever a payload has to say a token is configured.</summary>
    public const string RedactedToken = "***";

    /// <summary>Wire prefix for every gateway endpoint.</summary>
    public const string WirePrefix = "/mpt-control/v1";

    /// <summary>Connection code scheme described by <c>REMOTE_CONTROL_CONTRACT.md</c>.</summary>
    public const string ConnectionCodeScheme = "mpt";

    public const string ConnectionCodeAuthority = "control";

    public const string ConnectionCodeVersion = "1";

    /// <summary>Tailscale IPv4 range (RFC 6598 CGNAT space used by Tailscale).</summary>
    public const string TailnetIpv4Prefix = "100.64.0.0/10";

    /// <summary>Tailscale IPv6 ULA prefix.</summary>
    public const string TailnetIpv6Prefix = "fd7a:115c:a1e0::/48";

    /// <summary>Only plain HTTP is accepted: the Tailscale tunnel is the transport encryption.</summary>
    public const string AllowedEndpointScheme = "http";

    /// <summary>How long a catalog read is reused before the next read goes back to the computer.</summary>
    public static readonly TimeSpan CatalogCacheLifetime = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan InvocationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for one response body; a gateway never legitimately returns more.</summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    // ------------------------------------------------------------------ command ids

    public const string CommandStatus = ModuleId + ".status";
    public const string CommandImportPreview = ModuleId + ".import.preview";
    public const string CommandImportConfirm = ModuleId + ".import.confirm";
    public const string CommandDevicesList = ModuleId + ".devices.list";
    public const string CommandDevicesCheck = ModuleId + ".devices.check";
    public const string CommandDevicesRemove = ModuleId + ".devices.remove";
    public const string CommandCatalog = ModuleId + ".catalog";
    public const string CommandInvoke = ModuleId + ".invoke";
    public const string CommandInvocationStatus = ModuleId + ".invocation.status";
    public const string CommandInvokeCancel = ModuleId + ".invoke.cancel";

    public static IReadOnlyList<string> CommandIds { get; } =
    [
        CommandStatus,
        CommandImportPreview,
        CommandImportConfirm,
        CommandDevicesList,
        CommandDevicesCheck,
        CommandDevicesRemove,
        CommandCatalog,
        CommandInvoke,
        CommandInvocationStatus,
        CommandInvokeCancel
    ];

    // ------------------------------------------------------------------ argument names

    public const string ArgumentCode = "code";
    public const string ArgumentAccepted = "accepted";
    public const string ArgumentDeviceId = "deviceId";
    public const string ArgumentCommandId = "commandId";
    public const string ArgumentInvocationId = "invocationId";
    public const string ArgumentArgs = "args";
    public const string ArgumentRefresh = "refresh";

    // ------------------------------------------------------------------ module events

    public const string EventDeviceImported = "device.imported";
    public const string EventDeviceRemoved = "device.removed";
    public const string EventCatalogRefreshed = "catalog.refreshed";
    public const string EventInvocationStarted = "invocation.started";
    public const string EventInvocationFinished = "invocation.finished";
    public const string EventInvocationCancelled = "invocation.cancelled";
}
