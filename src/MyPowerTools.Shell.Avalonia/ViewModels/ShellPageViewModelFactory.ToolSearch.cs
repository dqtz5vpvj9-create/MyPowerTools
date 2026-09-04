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
        var matches = new List<(HostProto.CommandItem Command, int Score)>();
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

            var score = Math.Max(
                ToolSearchMatcher.Score(normalizedQuery, tool.Title, tool.ToolId,
                    tool.Description, tool.Category, tool.HomeCard?.Summary ?? "", tool.HomeCard?.PrimaryActionLabel ?? ""),
                RouteSearchScore(tool, primaryRoute, normalizedQuery));
            if (score >= 0)
            {
                matches.Add((CreateToolSearchCommand(tool, primaryRoute, primary: true), score));
            }

            if (normalizedQuery.Length == 0)
            {
                continue;
            }

            foreach (var route in tool.Routes
                         .Where(route => !string.Equals(route.RouteId, primaryRoute.RouteId, StringComparison.OrdinalIgnoreCase))
                         .Where(route => !IsHiddenUserSearchRoute(route))
                         .Where(route => RouteSearchScore(tool, route, normalizedQuery) >= 0))
            {
                matches.Add((CreateToolSearchCommand(tool, route, primary: false), RouteSearchScore(tool, route, normalizedQuery)));
            }
        }

        commands.Commands.AddRange(matches.OrderByDescending(match => match.Score).Select(match => match.Command));
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

    private static int RouteSearchScore(HostProto.ToolDescriptor tool, HostProto.ToolRoute route, string query)
    {
        return ToolSearchMatcher.Score(query, route.Title, route.RouteId, tool.Title, tool.ToolId, tool.Category);
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
