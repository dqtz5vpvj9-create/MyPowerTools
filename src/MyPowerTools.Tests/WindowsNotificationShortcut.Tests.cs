using System.Runtime.InteropServices;
using MyPowerTools.Platform.Windows;

namespace MyPowerTools.Tests;

public sealed class WindowsNotificationShortcutTests
{
    [Fact]
    public void Registration_preserves_launch_target_and_repairs_missing_toast_activation_identity()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "mpt-shortcut-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Product.lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(path);
        dynamic explorer = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        try
        {
            link.TargetPath = Environment.ProcessPath!;
            link.Arguments = "--preserve-this-argument";
            link.Description = "Existing product shortcut";
            link.Save();
            WindowsNotificationShortcut.Register(path);
            WindowsNotificationShortcut.Register(path);

            dynamic folder = explorer.Namespace(directory);
            dynamic item = folder.ParseName("Product.lnk");
            try
            {
                Assert.Equal(WindowsNotificationShortcut.AppUserModelId,
                    (string)item.ExtendedProperty("System.AppUserModel.ID"));
                Assert.Equal(WindowsNotificationShortcut.ToastActivatorId,
                    Guid.Parse((string)item.ExtendedProperty("System.AppUserModel.ToastActivatorCLSID")));
            }
            finally
            {
                Marshal.FinalReleaseComObject(item);
                Marshal.FinalReleaseComObject(folder);
            }
            dynamic reopened = shell.CreateShortcut(path);
            try
            {
                Assert.Equal("--preserve-this-argument", (string)reopened.Arguments);
                Assert.Equal("Existing product shortcut", (string)reopened.Description);
                Assert.Equal(Environment.ProcessPath!, (string)reopened.TargetPath, ignoreCase: true);
            }
            finally { Marshal.FinalReleaseComObject(reopened); }
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
            Marshal.FinalReleaseComObject(explorer);
            Directory.Delete(directory, recursive: true);
        }
    }
}
