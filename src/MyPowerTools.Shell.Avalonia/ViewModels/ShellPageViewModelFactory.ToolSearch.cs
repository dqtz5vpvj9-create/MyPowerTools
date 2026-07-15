using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public static partial class ShellPageViewModelFactory
{
    private static readonly IReadOnlySet<string> HiddenUserSearchRouteSegments =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "debug",
            "diagnostics",
            "logs",
            "maintenance",
            "raw",
            "settings",
            "troubleshooting"
        };

    public static CommandPaletteViewModel FromToolSearch(
        string query,
        HostProto.ListToolsResponse response,
        IReadOnlySet<string> searchableToolIds,
        Func<string, string, JsonObject?, Task>? navigateTool = null)
    {
        var commands = BuildToolSearchCommands(query, response, searchableToolIds);
        return FromCommands(query?.Trim() ?? "", commands, navigateTool: navigateTool);
    }

    public static HostProto.ListCommandsResponse BuildToolSearchCommands(
        string query,
        HostProto.ListToolsResponse response,
        IReadOnlySet<string> searchableToolIds)
    {
        var commands = new HostProto.ListCommandsResponse();
        var normalizedQuery = query?.Trim() ?? "";
        foreach (var tool in response.Tools
                     .Where(tool => searchableToolIds.Contains(tool.ToolId))
                     .Where(tool => !string.Equals(tool.State, "disabled", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(tool => tool.HomeCard?.Order ?? 0)
                     .ThenBy(tool => tool.Title, StringComparer.OrdinalIgnoreCase))
        {
            var primaryRoute = tool.Routes.FirstOrDefault(route => string.Equals(
                route.RouteId,
                tool.PrimaryRouteId,
                StringComparison.OrdinalIgnoreCase));
            if (primaryRoute is null || IsHiddenUserSearchRoute(primaryRoute))
            {
                continue;
            }

            if (normalizedQuery.Length == 0 ||
                MatchesTool(tool, normalizedQuery) ||
                MatchesRoute(tool, primaryRoute, normalizedQuery))
            {
                commands.Commands.Add(CreateToolSearchCommand(tool, primaryRoute, primary: true));
            }

            if (normalizedQuery.Length == 0)
            {
                continue;
            }

            foreach (var route in tool.Routes
                         .Where(route => !string.Equals(route.RouteId, primaryRoute.RouteId, StringComparison.OrdinalIgnoreCase))
                         .Where(route => !IsHiddenUserSearchRoute(route))
                         .Where(route => MatchesRoute(tool, route, normalizedQuery)))
            {
                commands.Commands.Add(CreateToolSearchCommand(tool, route, primary: false));
            }
        }

        return commands;
    }

    private static HostProto.CommandItem CreateToolSearchCommand(
        HostProto.ToolDescriptor tool,
        HostProto.ToolRoute route,
        bool primary)
    {
        var routeTitle = string.IsNullOrWhiteSpace(route.Title) ? route.RouteId : route.Title;
        return new HostProto.CommandItem
        {
            CommandId = $"tool.{tool.ToolId}.{route.RouteId}.open",
            ModuleId = tool.OwnerModuleId,
            Title = primary ? tool.Title : $"{tool.Title} · {routeTitle}",
            Subtitle = primary
                ? FirstNonEmpty(tool.HomeCard?.Summary ?? "", tool.Description, $"Open {tool.Title}")
                : $"Open {routeTitle} in {tool.Title}",
            Category = tool.Category,
            Icon = ToolSearchIcon(tool.Icon),
            Execution = new Struct
            {
                Fields =
                {
                    ["type"] = Value.ForString("navigation"),
                    ["toolId"] = Value.ForString(tool.ToolId),
                    ["routeId"] = Value.ForString(route.RouteId)
                }
            }
        };
    }

    private static bool MatchesTool(HostProto.ToolDescriptor tool, string query)
    {
        return ContainsAllTerms(
            query,
            tool.Title,
            tool.Description,
            tool.Category,
            tool.HomeCard?.Summary ?? "",
            tool.HomeCard?.PrimaryActionLabel ?? "");
    }

    private static bool MatchesRoute(HostProto.ToolDescriptor tool, HostProto.ToolRoute route, string query)
    {
        return ContainsAllTerms(query, tool.Title, tool.Category, route.Title, route.RouteId);
    }

    private static bool ContainsAllTerms(string query, params string[] values)
    {
        var haystack = string.Join(' ', values);
        return query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHiddenUserSearchRoute(HostProto.ToolRoute route)
    {
        var segments = route.RouteId.Split(
            ['.', '/', '-', '_'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Any(HiddenUserSearchRouteSegments.Contains) ||
               HiddenUserSearchRouteSegments.Any(segment =>
                   route.Title.Contains(segment, StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.First(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string ToolSearchIcon(string icon)
    {
        return icon.ToLowerInvariant() switch
        {
            "tool.adb-forwarder" => "network",
            "tool.remote-notifications" => "notifications",
            "tool.screenease" => "display",
            "tool.doubao-agent" => "services",
            "tool.smartbird-thermostat" => "temperature",
            _ => "navigation"
        };
    }
}
