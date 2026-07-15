namespace MyPowerTools.Shell.Avalonia.Navigation;

public enum ShellRouteKind
{
    Home,
    Tools,
    Tool,
    Activity,
    Notifications,
    Settings,
    System,
    Modules,
    Packages,
    Logs,
    RuntimeHealth,
    PermissionsAudit
}

public sealed record ShellRoute(
    ShellRouteKind Kind,
    string ToolId = "",
    string ToolRouteId = "")
{
    public static ShellRoute Home { get; } = new(ShellRouteKind.Home);
    public static ShellRoute Tools { get; } = new(ShellRouteKind.Tools);
    public static ShellRoute Activity { get; } = new(ShellRouteKind.Activity);
    public static ShellRoute Notifications { get; } = new(ShellRouteKind.Notifications);
    public static ShellRoute Settings { get; } = new(ShellRouteKind.Settings);
    public static ShellRoute System { get; } = new(ShellRouteKind.System);
    public static ShellRoute Modules { get; } = new(ShellRouteKind.Modules);
    public static ShellRoute Packages { get; } = new(ShellRouteKind.Packages);
    public static ShellRoute Logs { get; } = new(ShellRouteKind.Logs);
    public static ShellRoute RuntimeHealth { get; } = new(ShellRouteKind.RuntimeHealth);
    public static ShellRoute PermissionsAudit { get; } = new(ShellRouteKind.PermissionsAudit);

    public static ShellRoute ForTool(string toolId, string routeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        return new ShellRoute(ShellRouteKind.Tool, toolId, routeId);
    }

    public string NavigationLabel => Kind switch
    {
        ShellRouteKind.Home => "Home",
        ShellRouteKind.Tools or ShellRouteKind.Tool => "Tools",
        ShellRouteKind.Activity => "Activity",
        ShellRouteKind.Notifications => "Notifications",
        ShellRouteKind.Settings => "Settings",
        _ => "System"
    };
}
