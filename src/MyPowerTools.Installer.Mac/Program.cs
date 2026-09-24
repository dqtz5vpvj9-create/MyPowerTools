using Avalonia;
using Avalonia.Media;

namespace MyPowerTools.Installer.Mac;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        App.Arguments = InstallerArguments.Parse(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        if (OperatingSystem.IsMacOS())
        {
            // Same choice as the Shell: the system Chinese UI font, no bundled font payload.
            builder = builder.With(new FontManagerOptions { DefaultFamilyName = "PingFang SC" });
        }

        return builder;
    }
}
