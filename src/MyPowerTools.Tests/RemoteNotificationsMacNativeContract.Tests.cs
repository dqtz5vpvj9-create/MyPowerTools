namespace MyPowerTools.Tests;

public sealed class RemoteNotificationsMacNativeContractTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Authorization_probe_installs_the_notification_click_delegate()
    {
        var native = File.ReadAllText(Path.Combine(
            Root, "native", "macos", "MptMacNative", "MptMacNative.mm"));
        var statusStart = native.IndexOf(
            "int mpt_notification_authorization_status(void)",
            StringComparison.Ordinal);
        var publishStart = native.IndexOf(
            "int mpt_notification_publish(",
            StringComparison.Ordinal);

        Assert.True(statusStart >= 0);
        Assert.True(publishStart > statusStart);
        var statusImplementation = native[statusStart..publishStart];
        Assert.Contains("MptNotificationDelegateInstance()", statusImplementation, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_gate_uses_the_shipped_native_bridge_and_installed_shape()
    {
        var gate = File.ReadAllText(Path.Combine(
            Root, "scripts", "verify-remote-notifications-macos-production.ps1"));

        Assert.Contains("libMptMacNative.dylib", gate, StringComparison.Ordinal);
        Assert.Contains("mpt_notification_authorization_status", gate, StringComparison.Ordinal);
        Assert.Contains("mpt_notification_publish", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("libRemoteNotificationsMac.dylib", gate, StringComparison.Ordinal);
        Assert.Contains("ServiceManager re-adoption", gate, StringComparison.Ordinal);
        Assert.Contains("worker-crash-recovered", gate, StringComparison.Ordinal);
        Assert.Contains("banner-targets-exact-message", gate, StringComparison.Ordinal);
        Assert.Contains("message-received-after-worker-restart", gate, StringComparison.Ordinal);
        Assert.Contains("restart-banner-targets-exact-message", gate, StringComparison.Ordinal);
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
}
