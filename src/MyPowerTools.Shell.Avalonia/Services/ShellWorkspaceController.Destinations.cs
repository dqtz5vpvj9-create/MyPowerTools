using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private static IReadOnlyList<ToolWorkspaceViewModel> BuildDeliveredToolWorkspaces(
        MyPowerTools.Protocol.HostControl.V1.ToolDescriptor descriptor)
    {
        return ShellToolProductService.BuildPlaceholderWorkspaces(descriptor);
    }

    private void LoadActivityPage()
    {
        SetOwnedContent(_contentHost, BuildUnavailablePage(
            "Activity",
            "Nothing here yet. Work you run in your tools will show up here."));
        SetStatus("Activity is empty.");
    }

    private void LoadSystemPage()
    {
        var destinations = new[]
        {
            new SystemHubItemViewModel(PackagesPage, "Updates", "Check for updates, or repair and remove installed packages.", "update", "", ShowPageAsync),
            new SystemHubItemViewModel(ModulesPage, "Components", "Turn the building blocks behind each tool on or off.", "toolkit", "", ShowPageAsync),
            new SystemHubItemViewModel(ServicesPage, "Background services", "Start, stop or restart work that keeps running in the background.", "service", "", ShowPageAsync),
            new SystemHubItemViewModel(DiagnosticsPage, "Health", "See whether everything behind MyPowerTools is running properly.", "performance", "", ShowPageAsync),
            new SystemHubItemViewModel(LogsPage, "Logs", "Read and export logs when reporting a problem.", "terminal", "", ShowPageAsync)
        };
        var viewModel = new SystemHubViewModel(
            destinations,
            refresh: () =>
            {
                LoadSystemPage();
                return Task.CompletedTask;
            });
        SetOwnedContent(_contentHost, new SystemHubView { DataContext = viewModel });
        SetStatus("Troubleshooting opened.");
    }
}
