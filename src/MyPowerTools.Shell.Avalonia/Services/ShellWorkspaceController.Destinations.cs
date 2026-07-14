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
            "No user tool invocation has been recorded yet. Completed and running work will appear here as tools are delivered."));
        SetStatus("Activity is empty.");
    }

    private void LoadSystemPage()
    {
        var destinations = new[]
        {
            new SystemHubItemViewModel(ModulesPage, "Modules", "Enable, disable, and inspect runtime modules.", "MOD", "Maintenance", ShowPageAsync),
            new SystemHubItemViewModel(PackagesPage, "Packages & Updates", "Install, repair, roll back, and remove packages.", "PKG", "Maintenance", ShowPageAsync),
            new SystemHubItemViewModel(DiagnosticsPage, "Runtime Health", "Inspect transports, processes, policies, and runtime state.", "HLT", "Support", ShowPageAsync),
            new SystemHubItemViewModel(LogsPage, "Logs & Export", "Filter module logs and collect troubleshooting evidence.", "LOG", "Support", ShowPageAsync),
            new SystemHubItemViewModel(ServicesPage, "Services", "Start, stop, and inspect long-running Service Units across all tools.", "SVC", "Maintenance", ShowPageAsync)
        };
        var viewModel = new SystemHubViewModel(
            destinations,
            refresh: () =>
            {
                LoadSystemPage();
                return Task.CompletedTask;
            });
        SetOwnedContent(_contentHost, new SystemHubView { DataContext = viewModel });
        SetStatus("System & Maintenance opened.");
    }
}
