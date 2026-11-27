using System.Runtime.CompilerServices;
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
        if (OperatingSystem.IsWindows()) return CreateWindows();
        if (OperatingSystem.IsMacOS()) return CreateMac();
        return CreateLinux();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IPlatformPack CreateWindows() => new WindowsPlatformPack();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IPlatformPack CreateMac() => new MacPlatformPack();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IPlatformPack CreateLinux() => new LinuxPlatformPack();
}
