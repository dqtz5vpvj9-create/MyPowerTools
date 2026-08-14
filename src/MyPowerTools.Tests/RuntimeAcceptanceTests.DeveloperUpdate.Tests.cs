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
        Assert.Contains("Add-ToolSurfaceToPackage", script, StringComparison.Ordinal);
        Assert.Contains("*.Surface.csproj", script, StringComparison.Ordinal);
        Assert.Contains("matchingToolManifests[0].Directory.FullName", script, StringComparison.Ordinal);
        Assert.Contains("'surface'", script, StringComparison.Ordinal);

        Assert.DoesNotContain("publish-windows.ps1", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Compress-Archive", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MyPowerTools.ElevatedBroker", script, StringComparison.Ordinal);
        Assert.DoesNotContain("shellOutputRoot", script, StringComparison.Ordinal);
        Assert.DoesNotContain("runnerOutputRoot", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_development_launcher_updates_and_starts_the_complete_installed_layout()
    {
        var launcher = File.ReadAllText(
            Path.Combine(Root, "scripts", "Start-MyPowerTools-Dev.ps1"));
        var shortcutInstaller = File.ReadAllText(
            Path.Combine(Root, "scripts", "install-windows-dev-shortcut.ps1"));

        Assert.Contains("update-windows-dev.ps1", launcher, StringComparison.Ordinal);
        Assert.Contains("Programs\\MyPowerTools", launcher, StringComparison.Ordinal);
        Assert.Contains("Runtimes", launcher, StringComparison.Ordinal);
        Assert.Contains("service-units", launcher, StringComparison.Ordinal);
        Assert.Contains("ServiceManager", launcher, StringComparison.Ordinal);
        Assert.Contains("Join-Path $env:LOCALAPPDATA 'MyPowerTools'", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("bin\\Debug", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LOCALAPPDATA 'MyPowerTools-Dev'", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$modulesRoot", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", launcher, StringComparison.Ordinal);

        Assert.Contains("Get-Command 'pwsh.exe'", shortcutInstaller, StringComparison.Ordinal);
        Assert.Contains("Start-MyPowerTools-Dev.ps1", shortcutInstaller, StringComparison.Ordinal);
        Assert.Contains("MyPowerTools 开发版.lnk", shortcutInstaller, StringComparison.Ordinal);
        Assert.DoesNotContain("powershell.exe", shortcutInstaller, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExecutionPolicy", shortcutInstaller, StringComparison.OrdinalIgnoreCase);
    }
}
