namespace MyPowerTools.Tests;

public sealed partial class RuntimeAcceptanceTests
{
    [Fact]
    public void Windows_developer_update_overlays_the_complete_installed_runtime()
    {
        var script = File.ReadAllText(
            Path.Combine(Root, "scripts", "update-windows-dev.ps1"));

        Assert.Contains("ValidateSet('Core', 'Shell', 'Tools')", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NoUnmanagedConflict", script, StringComparison.Ordinal);
        Assert.Contains("Publish-ManagedComponent", script, StringComparison.Ordinal);
        Assert.Contains("Restore-OverlayTransaction", script, StringComparison.Ordinal);
        Assert.Contains("dev-update.manifest.json", script, StringComparison.Ordinal);
        Assert.Contains("installedModulesRoot", script, StringComparison.Ordinal);
        Assert.Contains("Get-InstalledLayoutInventory", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-InstalledHostControlSmoke", script, StringComparison.Ordinal);
        Assert.Contains("LoadedModuleCount", script, StringComparison.Ordinal);
        Assert.Contains("DashboardCardCount", script, StringComparison.Ordinal);
        Assert.Contains("'Runtimes'", script, StringComparison.Ordinal);
        Assert.Contains("'service-units'", script, StringComparison.Ordinal);
        Assert.Contains("'Broker'", script, StringComparison.Ordinal);
        Assert.Contains("Wait-ForInstalledProcess", script, StringComparison.Ordinal);
        Assert.Contains("'--isolation-probe'", script, StringComparison.Ordinal);
        Assert.Contains("'--smoke'", script, StringComparison.Ordinal);
        Assert.Contains("frameworkDependentManagedComponents", script, StringComparison.Ordinal);

        Assert.DoesNotContain("publish-windows.ps1", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Compress-Archive", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MyPowerTools.ElevatedBroker", script, StringComparison.Ordinal);
        Assert.DoesNotContain("shellOutputRoot", script, StringComparison.Ordinal);
        Assert.DoesNotContain("runnerOutputRoot", script, StringComparison.Ordinal);
    }
}
