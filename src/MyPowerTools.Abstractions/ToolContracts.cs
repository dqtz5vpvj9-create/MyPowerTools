using System.Text.Json.Nodes;

namespace MyPowerTools.Abstractions;

/// <summary>
/// Describes a user-facing tool owned by a runtime module.
/// </summary>
public sealed record ToolDescriptor(
    string ToolId,
    string OwnerModuleId,
    string Title,
    string Description,
    string Icon,
    string Category,
    string PrimaryRouteId,
    IReadOnlyList<ToolRoute> Routes,
    ToolHomeCard HomeCard,
    string Availability = "available",
    string ToolType = "",
    string SourceDirectory = "",
    ToolRuntime? Runtime = null,
    ToolSettings? Settings = null,
    IReadOnlyList<ToolCommand>? Commands = null);

/// <summary>
/// Maps a stable route identifier to a surface supplied by the owning module.
/// </summary>
public sealed record ToolRoute(
    string RouteId,
    string SurfaceId,
    string Title = "",
    string Icon = "",
    string SurfaceKind = "",
    string Source = "",
    string StaticRoot = "",
    string Assembly = "",
    string Type = "",
    bool OpenExternal = false,
    IReadOnlyList<string>? AllowedOrigins = null);

/// <summary>
/// Supplies the user-facing summary and primary affordance shown on Home.
/// </summary>
public sealed record ToolHomeCard(
    string Summary,
    string PrimaryActionLabel = "Open",
    string StatusBinding = "",
    int Order = 0);

public sealed record ToolRuntime(
    string Transport,
    string Endpoint,
    string Command,
    IReadOnlyList<string> Args,
    string HealthPath,
    string LogsPath,
    int TimeoutMs,
    bool Remote);

public sealed record ToolSettings(
    string SchemaPath,
    string ValuesPath,
    IReadOnlyList<string> Secrets);

public sealed record ToolCommand(
    string Id,
    string Title,
    string Description,
    string Method,
    string Path);

/// <summary>
/// Navigates inside the product shell without invoking a module command.
/// </summary>
public sealed record NavigationAction(
    string ToolId,
    string RouteId,
    JsonObject? RouteArgs = null);

/// <summary>
/// Invokes a module command with optional arguments and result presentation metadata.
/// </summary>
public sealed record CommandAction(
    string CommandId,
    JsonObject? PresetArgs = null,
    string ResultPresentation = "inline");
