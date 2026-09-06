using MyPowerTools.ServiceManager.Client;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia.Services;

// Service-unit actions invoked from the Services page. Each one captures the workspace identity
// before its RPC so that an action the user kicked off and then navigated away from does not
// reload the Services page over whatever they opened next.
public sealed partial class ShellWorkspaceController
{
    private async Task TryStartServiceManagerThenLoadAsync()
    {
        // Spawns the ServiceManager process if it is not already reachable, then reloads the page.
        // If startup still fails, LoadServicesPageAsync re-enters its catch and re-renders the
        // unavailable page with the same Try-again affordance, so the user can retry repeatedly.
        var identity = _workspaceIdentity.Capture();
        var result = await ShellServiceManagerBootstrapper.EnsureStartedAsync();
        if (!_workspaceIdentity.IsCurrent(identity)) return;

        SetStatus(result.Message);
        await LoadServicesPageAsync();
    }

    private async Task TailServiceUnitLogsAsync(string unitId)
    {
        using var client = ServiceManagerAdminClient.ForDefaultEndpoint();
        var entries = await client.TailLogsAsync(unitId, 50);
        if (entries.Count == 0)
        {
            ShowInfoBar(InfoBarSeverity.Info, $"No recent log lines for {unitId}.");
        }
        else
        {
            var summary = string.Join("\n", entries.Take(20).Select(e => $"[{e.Level}] {e.Message}"));
            ShowInfoBar(InfoBarSeverity.Info, $"Recent logs for {unitId}:\n{summary}",
                actionLabel: "View full logs",
                action: () => ShowModuleLogsPageAsync(unitId));
        }
    }

    private async Task OpenToolFromServicesAsync(string toolId)
    {
        // Navigate to the owning tool's page if it is a known first-party tool.
        if (!string.IsNullOrEmpty(toolId))
        {
            await ShowToolPageAsync(toolId, "");
        }
    }

    private async Task ToggleServiceUnitAutostartAsync(string unitId)
    {
        var identity = _workspaceIdentity.Capture();
        // Autostart is a property of the unit manifest; toggling rewrites the deployed manifest and reloads.
        // For units whose manifest is managed by the ServiceManager deploy root, we update the file in place.
        try
        {
            await ToggleDeployedUnitAutostartAsync(unitId);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not toggle autostart for {unitId}: {ex.Message}");
            ShowInfoBar(InfoBarSeverity.Error, $"Could not toggle autostart for {unitId}: {ex.Message}");
        }

        if (!_workspaceIdentity.IsCurrent(identity)) return;

        await LoadServicesPageAsync();
    }

    private async Task ToggleDeployedUnitAutostartAsync(string unitId)
    {
        var dataRoot = Environment.GetEnvironmentVariable("MPT_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");
        var deployRoot = Path.Combine(dataRoot, "ServiceManager");
        var manifestPath = Path.Combine(deployRoot, "units", $"{unitId}.json");
        if (!File.Exists(manifestPath))
        {
            SetStatus($"Manifest for {unitId} not found in deploy root; cannot toggle autostart.");
            ShowInfoBar(InfoBarSeverity.Error, $"Manifest for {unitId} not found in deploy root; cannot toggle autostart.");
            return;
        }

        var json = await File.ReadAllTextAsync(manifestPath);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            var current = obj["autostart"]?.GetValue<bool>() ?? false;
            obj["autostart"] = !current;
            await File.WriteAllTextAsync(manifestPath, obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private async Task InvokeServiceUnitActionAsync(string unitId, ServiceUnitAction action)
    {
        var identity = _workspaceIdentity.Capture();
        using var client = ServiceManagerAdminClient.ForDefaultEndpoint();
        switch (action)
        {
            case ServiceUnitAction.Start:
                await client.StartAsync(unitId);
                break;
            case ServiceUnitAction.Stop:
                await client.StopAsync(unitId);
                break;
            case ServiceUnitAction.Restart:
                await client.RestartAsync(unitId);
                break;
        }

        if (!_workspaceIdentity.IsCurrent(identity)) return;

        await LoadServicesPageAsync();
    }

    private async Task ReloadServiceUnitsAsync()
    {
        var identity = _workspaceIdentity.Capture();
        using var client = ServiceManagerAdminClient.ForDefaultEndpoint();
        await client.ReloadAsync();
        if (!_workspaceIdentity.IsCurrent(identity)) return;

        await LoadServicesPageAsync();
    }

    private enum ServiceUnitAction { Start, Stop, Restart }
}
