using MyPowerTools.HostControl;
using MyPowerTools.Shell.Avalonia.ViewModels;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class ShellToolProductService
{
    public async Task<IReadOnlyList<ToolCardViewModel>> LoadToolCardsAsync(
        Func<string, Task> openTool,
        IReadOnlySet<string>? deliveredToolIds = null,
        CancellationToken cancellationToken = default)
    {
        var descriptors = await LoadToolDescriptorsAsync(cancellationToken);
        return BuildToolCards(descriptors, openTool, deliveredToolIds);
    }

    public async Task<IReadOnlyList<HostProto.ToolDescriptor>> LoadToolDescriptorsAsync(
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var response = await client.ListToolsAsync(includeDisabled: true, cancellationToken);
        return response.Tools.ToArray();
    }

    public IReadOnlyList<ToolCardViewModel> BuildToolCards(
        IEnumerable<HostProto.ToolDescriptor> descriptors,
        Func<string, Task> openTool,
        IReadOnlySet<string>? deliveredToolIds = null)
    {
        return descriptors
            .Where(IsVisibleInProduct)
            .Select(tool => new
            {
                Descriptor = tool,
                Card = ToCard(
                    tool,
                    openTool,
                    deliveredToolIds?.Contains(tool.ToolId) == true || IsSdkTool(tool))
            })
            .OrderBy(item => AvailabilityOrder(item.Card.Availability))
            .ThenBy(item => item.Descriptor.HomeCard?.Order ?? 0)
            .ThenBy(item => item.Descriptor.Title, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Card)
            .ToArray();
    }

    public static bool IsVisibleInProduct(HostProto.ToolDescriptor tool)
    {
        return !string.Equals(tool.Availability, "paused", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<HostProto.ToolDescriptor> LoadToolAsync(string toolId, CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        return await client.GetToolAsync(toolId, cancellationToken);
    }

    public async Task RefreshToolsAsync(CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        await client.RefreshToolsAsync(cancellationToken);
    }

    public static bool IsSdkTool(HostProto.ToolDescriptor tool)
    {
        return tool.ToolType is "web-surface" or "dotnet-surface" or "native-tool" or "headless-tool";
    }

    public static ToolCardViewModel ToCard(
        HostProto.ToolDescriptor tool,
        Func<string, Task> openTool,
        bool workspaceDelivered)
    {
        var disabled = string.Equals(tool.State, "disabled", StringComparison.OrdinalIgnoreCase);
        var declaredAvailability = string.IsNullOrWhiteSpace(tool.Availability)
            ? "available"
            : tool.Availability;
        var availability = disabled
            ? ToolAvailability.Unavailable
            : string.Equals(declaredAvailability, "paused", StringComparison.OrdinalIgnoreCase)
                ? ToolAvailability.Paused
                : workspaceDelivered && string.Equals(declaredAvailability, "available", StringComparison.OrdinalIgnoreCase)
                ? ToolAvailability.Available
                : ToolAvailability.InDevelopment;
        var statusLabel = availability switch
        {
            ToolAvailability.Available => FriendlyState(tool.State),
            ToolAvailability.InDevelopment => "Coming soon",
            ToolAvailability.Paused => "Paused",
            _ => "Unavailable"
        };
        var statusDetail = availability switch
        {
            ToolAvailability.InDevelopment => "This tool is planned, and its workspace is still in development.",
            ToolAvailability.Paused => "Delivery is paused. The tool will stay unavailable until implementation resumes.",
            _ => tool.StateSummary
        };
        var primaryActionLabel = availability == ToolAvailability.Available
            ? tool.HomeCard?.PrimaryActionLabel ?? "Open tool"
            : statusLabel;

        return new ToolCardViewModel(
            tool.ToolId,
            tool.Title,
            tool.Description,
            tool.Category,
            IconGlyph(tool.Icon, tool.Title),
            statusLabel,
            statusDetail,
            availability,
            isFavorite: false,
            openTool,
            primaryActionLabel: primaryActionLabel,
            isWebSurface: tool.ToolType == "web-surface" ||
                          tool.Routes.Any(route => route.SurfaceKind == "web"));
    }

    public static IReadOnlyList<ToolWorkspaceViewModel> BuildPlaceholderWorkspaces(HostProto.ToolDescriptor tool)
    {
        return tool.Routes.Select(route => new ToolWorkspaceViewModel(
            route.RouteId,
            string.IsNullOrWhiteSpace(route.Title) ? route.RouteId : route.Title,
            string.IsNullOrWhiteSpace(route.Title) ? tool.Title : route.Title,
            tool.Title,
            "This route is registered, but its user-facing workspace is still under implementation.",
            [],
            [])).ToArray();
    }

    private static string FriendlyState(string state)
    {
        return state.ToLowerInvariant() switch
        {
            "running" or "ready" => "Ready",
            "degraded" => "Needs attention",
            "error" or "failed" => "Unavailable",
            _ => state
        };
    }

    private static int AvailabilityOrder(ToolAvailability availability)
    {
        return availability switch
        {
            ToolAvailability.Available => 0,
            ToolAvailability.InDevelopment => 1,
            ToolAvailability.Paused => 2,
            _ => 3
        };
    }

    private static string IconGlyph(string icon, string title)
    {
        return icon switch
        {
            "tool.adb-forwarder" => "ADB",
            "tool.remote-notifications" => "RN",
            "tool.remote-commands" => "RC",
            "tool.process-monitor" => "PM",
            "tool.screenease" => "SE",
            "tool.doubao-agent" => "DA",
            "tool.smartbird-thermostat" => "SB",
            _ => string.Concat(title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word => char.ToUpperInvariant(word[0])))
        };
    }
}
