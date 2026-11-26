using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Linux;
using MyPowerTools.Platform.Mac;
using MyPowerTools.Platform.Windows;

namespace MyPowerTools.Platform;

public static class PlatformPackFactory
{
    public static Func<IPlatformPack>? EmbeddedPlatformFactory { get; set; }
    public static IPlatformPack Create()
    {
        if (EmbeddedPlatformFactory is { } factory) return factory();
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPlatformPack();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacPlatformPack();
        }

        return new LinuxPlatformPack();
    }
}
