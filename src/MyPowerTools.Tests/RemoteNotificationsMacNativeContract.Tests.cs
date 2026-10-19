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
