using System.Runtime.InteropServices;

namespace MyPowerTools.Platform.Abstractions;

public sealed record PlatformId(string OperatingSystem, string Architecture)
{
    public string Rid => $"{OperatingSystem}-{Architecture}";

    public static PlatformId Current()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "macos"
                : "linux";

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        return new PlatformId(os, arch);
    }

    public bool Matches(string platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return true;
        }

        var normalized = platform.Trim().ToLowerInvariant();
        return normalized == OperatingSystem || normalized == Rid;
    }
}
