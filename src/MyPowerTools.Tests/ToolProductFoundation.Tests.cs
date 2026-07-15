using MyPowerTools.Abstractions;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.Navigation;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using System.Reflection;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed class ToolProductFoundationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Navigation_action_uses_explicit_tool_route_without_open_suffix_convention()
    {
        object action = new NavigationAction(
            "remote-notifications",
            "inbox");

        var navigation = Assert.IsType<NavigationAction>(action);
        Assert.Equal("remote-notifications", navigation.ToolId);
        Assert.Equal("inbox", navigation.RouteId);
        Assert.False(navigation.RouteId.EndsWith(".open", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Command_action_with_open_suffix_remains_a_command_action()
    {
        object action = new CommandAction("android-tools.notifications.open");

        var command = Assert.IsType<CommandAction>(action);
        Assert.Equal("android-tools.notifications.open", command.CommandId);
        Assert.False(action is NavigationAction);
    }

    [Fact]
    public async Task Command_palette_opens_tool_from_typed_navigation_target()
    {
        var openedToolId = "";
        var openedRouteId = "";
        var response = new HostProto.ListCommandsResponse();
        response.Commands.Add(new HostProto.CommandItem
        {
            CommandId = "android-tools.notifications.open-inbox",
            ModuleId = "android-tools.notifications",
            Title = "Open remote notification inbox",
            Execution = new Google.Protobuf.WellKnownTypes.Struct
            {
                Fields =
                {
                    ["type"] = Google.Protobuf.WellKnownTypes.Value.ForString("navigation"),
                    ["toolId"] = Google.Protobuf.WellKnownTypes.Value.ForString("remote-notifications"),
                    ["routeId"] = Google.Protobuf.WellKnownTypes.Value.ForString("inbox")
                }
            }
        });

        var palette = ShellPageViewModelFactory.FromCommands(
            "notifications",
            response,
            navigateTool: (toolId, routeId, _) =>
            {
                openedToolId = toolId;
                openedRouteId = routeId;
                return Task.CompletedTask;
            });

        var command = Assert.Single(palette.Commands);
        Assert.Equal("Open", command.ExecuteLabel);
        await command.ExecuteAsync();

        Assert.Equal("remote-notifications", openedToolId);
        Assert.Equal("inbox", openedRouteId);
        Assert.Equal("succeeded", command.ExecutionState);
    }

    [Fact]
    public async Task Command_palette_uses_declarative_activation_without_changing_runtime_execution()
    {
        var openedToolId = "";
        var openedRouteId = "";
        var runtimeExecution = new Google.Protobuf.WellKnownTypes.Struct
        {
            Fields =
            {
                ["type"] = Google.Protobuf.WellKnownTypes.Value.ForString("broker.request"),
                ["actionId"] = Google.Protobuf.WellKnownTypes.Value.ForString("network.portproxy.apply"),
                ["activation"] = Google.Protobuf.WellKnownTypes.Value.ForStruct(new Google.Protobuf.WellKnownTypes.Struct
                {
                    Fields =
                    {
                        ["type"] = Google.Protobuf.WellKnownTypes.Value.ForString("navigation"),
                        ["toolId"] = Google.Protobuf.WellKnownTypes.Value.ForString("fixture-tool"),
                        ["routeId"] = Google.Protobuf.WellKnownTypes.Value.ForString("review")
                    }
                })
            }
        };
        var response = new HostProto.ListCommandsResponse();
        response.Commands.Add(new HostProto.CommandItem
        {
            CommandId = "fixture.apply",
            ModuleId = "fixture",
            Title = "Review and apply",
            Execution = runtimeExecution
        });

        var palette = ShellPageViewModelFactory.FromCommands(
            "apply",
            response,
            navigateTool: (toolId, routeId, _) =>
            {
                openedToolId = toolId;
                openedRouteId = routeId;
                return Task.CompletedTask;
            });

        var command = Assert.Single(palette.Commands);
        await command.ExecuteAsync();

        Assert.Equal("fixture-tool", openedToolId);
        Assert.Equal("review", openedRouteId);
        Assert.Equal("broker.request", runtimeExecution.Fields["type"].StringValue);
        Assert.Equal("network.portproxy.apply", runtimeExecution.Fields["actionId"].StringValue);
    }

    [Fact]
    public void Catalog_marks_paused_and_planned_tools_as_non_actionable()
    {
        var processMonitor = CreateTool(
            "process-monitor",
            "android-tools.process-monitor",
            "Process Monitor",
            "watch",
            ("watch", "Watched processes"));
        processMonitor.Availability = "paused";
        var smartBird = CreateTool(
            "smartbird-thermostat",
            "smartbird-thermostat",
            "SmartBird Thermostat",
            "overview",
            ("overview", "Overview"));

        var processCard = ShellToolProductService.ToCard(processMonitor, _ => Task.CompletedTask, workspaceDelivered: false);
        var smartBirdCard = ShellToolProductService.ToCard(smartBird, _ => Task.CompletedTask, workspaceDelivered: false);

        Assert.Equal(ToolAvailability.Paused, processCard.Availability);
        Assert.Equal("Paused", processCard.StatusLabel);
        Assert.Equal("Paused", processCard.PrimaryActionLabel);
        Assert.False(processCard.CanOpen);
        Assert.False(processCard.OpenCommand.CanExecute(null));
        Assert.True(processCard.IsAttentionStatus);

        Assert.Equal(ToolAvailability.InDevelopment, smartBirdCard.Availability);
        Assert.Equal("Coming soon", smartBirdCard.StatusLabel);
        Assert.Equal("Coming soon", smartBirdCard.PrimaryActionLabel);
        Assert.False(smartBirdCard.CanOpen);
        Assert.False(smartBirdCard.OpenCommand.CanExecute(null));
    }

    [Fact]
    public void Shell_has_no_compiled_delivered_tool_registry()
    {
        var field = typeof(ShellWorkspaceController).GetField(
            "DeliveredToolIds",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.Null(field);
    }

    [Theory]
    [InlineData("doubao-agent", "Doubao Agent", "services")]
    [InlineData("smartbird-thermostat", "SmartBird Thermostat", "overview")]
    public void Newly_delivered_tool_cards_are_actionable(
        string toolId,
        string title,
        string primaryRouteId)
    {
        var descriptor = CreateTool(
            toolId,
            toolId,
            title,
            primaryRouteId,
            (primaryRouteId, "Overview"));

        var card = ShellToolProductService.ToCard(
            descriptor,
            _ => Task.CompletedTask,
            workspaceDelivered: true);

        Assert.Equal(ToolAvailability.Available, card.Availability);
        Assert.True(card.CanOpen);
        Assert.True(card.OpenCommand.CanExecute(null));
        Assert.DoesNotContain("Coming soon", card.PrimaryActionLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Global_search_only_returns_delivered_tool_workflows()
    {
        var response = new HostProto.ListToolsResponse();
        response.Tools.Add(CreateTool(
            "adb-forwarder",
            "adb-forwarder",
            "ADB Forwarder",
            "rules",
            ("rules", "Rules"),
            ("devices", "Devices"),
            ("activity", "Activity"),
            ("diagnostics", "Troubleshooting")));
        response.Tools.Add(CreateTool(
            "remote-notifications",
            "android-tools.notifications",
            "Remote Notifications",
            "inbox",
            ("inbox", "Notifications")));
        response.Tools.Add(CreateTool(
            "screenease",
            "screenease",
            "ScreenEase",
            "profiles",
            ("profiles", "Eye care")));
        response.Tools.Add(CreateTool(
            "process-monitor",
            "android-tools.process-monitor",
            "Process Monitor",
            "watch",
            ("watch", "Watched processes"),
            ("diagnostics", "Troubleshooting")));
        response.Tools.Add(CreateTool(
            "remote-commands",
            "android-tools.remote-commands",
            "Remote Commands",
            "catalog",
            ("catalog", "Catalog"),
            ("history", "History")));
        response.Tools.Add(CreateTool(
            "doubao-agent",
            "doubao-agent",
            "Doubao Agent",
            "services",
            ("services", "Services"),
            ("logs", "Logs"),
            ("settings", "Settings"),
            ("diagnostics", "Troubleshooting")));
        response.Tools.Add(CreateTool(
            "smartbird-thermostat",
            "smartbird-thermostat",
            "SmartBird Thermostat",
            "overview",
            ("overview", "Overview"),
            ("events", "Events"),
            ("configuration", "Configuration"),
            ("diagnostics", "Troubleshooting")));

        var delivered = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "adb-forwarder",
            "doubao-agent",
            "remote-notifications",
            "screenease",
            "smartbird-thermostat"
        };

        var quickAccess = ShellPageViewModelFactory.FromToolSearch("", response, delivered);
        Assert.Equal("Quick access", quickAccess.Title);
        Assert.Equal(
            ["ADB Forwarder", "Doubao Agent", "Remote Notifications", "ScreenEase", "SmartBird Thermostat"],
            quickAccess.Commands.Select(command => command.Title));
        Assert.DoesNotContain(quickAccess.Commands, command =>
            command.Title.Contains("Process Monitor", StringComparison.OrdinalIgnoreCase) ||
            command.Title.Contains("Remote Commands", StringComparison.OrdinalIgnoreCase));

        var openedToolId = "";
        var openedRouteId = "";
        var devices = ShellPageViewModelFactory.FromToolSearch(
            "adb devices",
            response,
            delivered,
            (toolId, routeId, _) =>
            {
                openedToolId = toolId;
                openedRouteId = routeId;
                return Task.CompletedTask;
            });
        var deviceResult = Assert.Single(devices.Commands);
        Assert.Equal("ADB Forwarder · Devices", deviceResult.Title);
        await deviceResult.ExecuteAsync();
        Assert.Equal("adb-forwarder", openedToolId);
        Assert.Equal("devices", openedRouteId);

        Assert.Equal(
            "Doubao Agent",
            Assert.Single(ShellPageViewModelFactory.FromToolSearch("doubao services", response, delivered).Commands).Title);
        Assert.Equal(
            "SmartBird Thermostat · Events",
            Assert.Single(ShellPageViewModelFactory.FromToolSearch("smartbird events", response, delivered).Commands).Title);

        Assert.Empty(ShellPageViewModelFactory.FromToolSearch("diagnostics", response, delivered).Commands);
        Assert.Empty(ShellPageViewModelFactory.FromToolSearch("portproxy", response, delivered).Commands);
        Assert.Empty(ShellPageViewModelFactory.FromToolSearch("NetworkBroker", response, delivered).Commands);
        Assert.Empty(ShellPageViewModelFactory.FromToolSearch("logs", response, delivered).Commands);
    }

    [Fact]
    public async Task Seven_production_tools_each_resolve_one_unique_primary_route()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "mpt-tool-product-foundation",
            Guid.NewGuid().ToString("N"));

        try
        {
            await using var runtime = new MptHostRuntime(
                new PackageReader(),
                PlatformId.Current(),
                RuntimePaths.Create(dataRoot));
            runtime.Load(Path.Combine(RepositoryRoot, "modules"));

            var tools = runtime.ListTools(includeDisabled: true);
            var expectedToolIds = new[]
            {
                "adb-forwarder",
                "doubao-agent",
                "process-monitor",
                "remote-commands",
                "remote-notifications",
                "screenease",
                "smartbird-thermostat"
            };

            Assert.Equal(
                expectedToolIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
                tools.Select(tool => tool.Descriptor.ToolId)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase));

            foreach (var snapshot in tools)
            {
                var descriptor = snapshot.Descriptor;
                Assert.False(string.IsNullOrWhiteSpace(descriptor.PrimaryRouteId));
                Assert.Equal(
                    descriptor.Routes.Count,
                    descriptor.Routes
                        .Select(route => route.RouteId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count());

                var primaryRoute = Assert.Single(descriptor.Routes.Where(route =>
                    string.Equals(
                        route.RouteId,
                        descriptor.PrimaryRouteId,
                        StringComparison.OrdinalIgnoreCase)));
                Assert.False(string.IsNullOrWhiteSpace(primaryRoute.SurfaceId));
            }

            Assert.Equal(
                "paused",
                tools.Single(tool => tool.Descriptor.ToolId == "process-monitor").Descriptor.Availability);
            Assert.Equal(
                "paused",
                tools.Single(tool => tool.Descriptor.ToolId == "remote-commands").Descriptor.Availability);
            Assert.All(
                tools.Where(tool => tool.Descriptor.ToolId is not ("process-monitor" or "remote-commands")),
                tool => Assert.Equal("available", tool.Descriptor.Availability));
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Module_diagnostics_route_is_separate_from_tool_product_route()
    {
        var toolTroubleshooting = ShellRoute.ForTool("adb-forwarder", "diagnostics");
        var moduleDiagnostics = ShellRoute.RuntimeHealth;

        Assert.Equal(ShellRouteKind.Tool, toolTroubleshooting.Kind);
        Assert.Equal(ShellRouteKind.RuntimeHealth, moduleDiagnostics.Kind);
        Assert.NotEqual(toolTroubleshooting, moduleDiagnostics);
        Assert.Equal("Tools", toolTroubleshooting.NavigationLabel);
        Assert.Equal("System", moduleDiagnostics.NavigationLabel);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MyPowerTools.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("MyPowerTools repository root was not found.");
    }

    private static HostProto.ToolDescriptor CreateTool(
        string toolId,
        string ownerModuleId,
        string title,
        string primaryRouteId,
        params (string RouteId, string Title)[] routes)
    {
        var descriptor = new HostProto.ToolDescriptor
        {
            ToolId = toolId,
            OwnerModuleId = ownerModuleId,
            Title = title,
            Description = $"Use {title} to complete its primary workflow.",
            Icon = $"tool.{toolId}",
            Category = "Tools",
            PrimaryRouteId = primaryRouteId,
            State = "running",
            StateSummary = "Ready",
            HomeCard = new HostProto.ToolHomeCard
            {
                Summary = $"Open the {title} workspace.",
                PrimaryActionLabel = $"Open {title}",
                Order = 10
            }
        };
        descriptor.Routes.AddRange(routes.Select(route => new HostProto.ToolRoute
        {
            RouteId = route.RouteId,
            SurfaceId = $"{toolId}.{route.RouteId}",
            Title = route.Title
        }));
        return descriptor;
    }
}
