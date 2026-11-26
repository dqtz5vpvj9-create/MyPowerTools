using System.Runtime.InteropServices;
using FileTransfer.Core;

namespace FileTransfer.Tests;

/// <summary>
/// Platform classification, runtime discovery and layout compatibility for <see cref="OpenListRuntime"/>.
///
/// These tests never download and never start a process: they exercise the pure selection helpers, plus one
/// install call that must short-circuit because a managed instance already has its runtime in place.
/// </summary>
public sealed class OpenListRuntimeTests
{
    [Theory]
    // Android is Linux-based, so the regression this pins is that it must not be classified as linux.
    [InlineData("embedded", "linux", "arm64", "android", "arm64", "openlist-android-arm64.tar.gz")]
    [InlineData("embedded", "linux", "x64", "android", "amd64", "openlist-android-amd64.tar.gz")]
    [InlineData("release", "linux", "arm64", "linux", "arm64", "openlist-linux-arm64.tar.gz")]
    [InlineData("release", "linux", "x64", "linux", "amd64", "openlist-linux-amd64.tar.gz")]
    [InlineData("release", "macos", "arm64", "darwin", "arm64", "openlist-darwin-arm64.tar.gz")]
    [InlineData("release", "macos", "x64", "darwin", "amd64", "openlist-darwin-amd64.tar.gz")]
    [InlineData("release", "windows", "x64", "windows", "amd64", "openlist-windows-amd64.zip")]
    [InlineData("release", "windows", "arm64", "windows", "arm64", "openlist-windows-arm64.zip")]
    public void OfficialAssetNameFollowsPlatformAndArchitecture(
        string origin, string desktop, string architecture,
        string expectedOs, string expectedArch, string expectedAsset)
    {
        var target = OpenListRuntime.DescribeTarget(ParseOrigin(origin), ParseDesktop(desktop), ParseArchitecture(architecture));

        Assert.Equal(expectedOs, target.AssetOperatingSystem);
        Assert.Equal(expectedArch, target.AssetArchitecture);
        Assert.Equal(expectedAsset, target.AssetName);
        Assert.Equal(origin == "embedded", target.IsAndroid);
    }

    private static OpenListRuntimeOrigin ParseOrigin(string value) =>
        value == "embedded" ? OpenListRuntimeOrigin.EmbeddedAndroidLibrary : OpenListRuntimeOrigin.ReleaseArchive;

    private static OpenListDesktopPlatform ParseDesktop(string value) => value switch
    {
        "windows" => OpenListDesktopPlatform.Windows,
        "macos" => OpenListDesktopPlatform.MacOS,
        _ => OpenListDesktopPlatform.Linux,
    };

    private static Architecture ParseArchitecture(string value) => value switch
    {
        "x64" => System.Runtime.InteropServices.Architecture.X64,
        _ => System.Runtime.InteropServices.Architecture.Arm64,
    };

    [Fact]
    public void AndroidNeverUsesTheReleaseArchive()
    {
        // Android downloads are not merely unnecessary: an executable written into app data cannot be
        // executed on API 29+, so the Android target must always demand the embedded library.
        var android = OpenListRuntime.DescribeTarget(OpenListRuntimeOrigin.EmbeddedAndroidLibrary, OpenListDesktopPlatform.Linux, Architecture.Arm64);
        var linux = OpenListRuntime.DescribeTarget(OpenListRuntimeOrigin.ReleaseArchive, OpenListDesktopPlatform.Linux, Architecture.Arm64);

        Assert.True(android.IsAndroid);
        Assert.False(linux.IsAndroid);
        Assert.NotEqual(linux.AssetName, android.AssetName);
    }

    [Fact]
    public void UnsupportedArchitectureIsRejected()
    {
        Assert.Throws<PlatformNotSupportedException>(() =>
            OpenListRuntime.DescribeTarget(OpenListRuntimeOrigin.ReleaseArchive, OpenListDesktopPlatform.Linux, Architecture.X86));
        Assert.Throws<PlatformNotSupportedException>(() =>
            OpenListRuntime.DescribeTarget(OpenListRuntimeOrigin.EmbeddedAndroidLibrary, OpenListDesktopPlatform.Linux, Architecture.Arm));
    }

    [Fact]
    public void WindowsTargetsKeepTheExeNameAndZipSuffix()
    {
        var windows = OpenListRuntime.DescribeTarget(OpenListRuntimeOrigin.ReleaseArchive, OpenListDesktopPlatform.Windows, Architecture.X64);
        var linux = OpenListRuntime.DescribeTarget(OpenListRuntimeOrigin.ReleaseArchive, OpenListDesktopPlatform.Linux, Architecture.X64);

        Assert.Equal("openlist.exe", windows.ExecutableName);
        Assert.Equal(".zip", windows.ArchiveSuffix);
        Assert.Equal("openlist", linux.ExecutableName);
        Assert.Equal(".tar.gz", linux.ArchiveSuffix);
    }

    [Fact]
    public void ManagedInstanceLayoutIsUnchanged()
    {
        var target = OpenListRuntime.DescribeTarget(OpenListRuntimeOrigin.ReleaseArchive, OpenListDesktopPlatform.Linux, Architecture.X64);

        // The fixture and every existing managed instance resolve "{data}/openlist/{version}/openlist".
        Assert.Equal(Path.Combine("/data/openlist", OpenListRuntime.Version, "openlist"), OpenListRuntime.LayoutExecutable("/data/openlist", target));
        Assert.Equal("v4.2.6", OpenListRuntime.Version);
    }

    [Fact]
    public void PackagedRuntimeSitsNextToTheModule()
    {
        var target = OpenListRuntime.DescribeTarget(OpenListRuntimeOrigin.ReleaseArchive, OpenListDesktopPlatform.Windows, Architecture.X64);

        Assert.Equal(
            Path.Combine("/modules/file-transfer", "runtime", "openlist", OpenListRuntime.Version, "openlist.exe"),
            OpenListRuntime.PackagedExecutable("/modules/file-transfer", target));
        // An unknown module location must not degrade into a relative path that could match by accident.
        Assert.Null(OpenListRuntime.PackagedExecutable(null, target));
        Assert.Null(OpenListRuntime.PackagedExecutable("", target));
    }

    [Fact]
    public void DesktopResolutionPrefersOverrideThenPackagedThenInstalled()
    {
        static Func<string, bool> Exists(params string[] paths) => path => paths.Contains(path, StringComparer.Ordinal);

        var installed = Path.Combine("/data", "openlist", "v4.2.6", "openlist");
        var packaged = Path.Combine("/modules", "runtime", "openlist", "v4.2.6", "openlist");
        var overridden = Path.Combine("/opt", "openlist-official");

        Assert.Equal(overridden, OpenListRuntime.SelectDesktopExecutable(overridden, packaged, installed, Exists(overridden, packaged)));
        Assert.Equal(packaged, OpenListRuntime.SelectDesktopExecutable(overridden, packaged, installed, Exists(packaged)));
        Assert.Equal(installed, OpenListRuntime.SelectDesktopExecutable(overridden, packaged, installed, Exists()));
        // A half-written override or package must never win over a usable install.
        Assert.Equal(installed, OpenListRuntime.SelectDesktopExecutable("/missing/openlist", "/missing/packaged", installed, Exists()));
        Assert.Equal(installed, OpenListRuntime.SelectDesktopExecutable(null, null, installed, Exists()));
    }

    [Fact]
    public void AndroidResolutionUsesOverrideOrNativeLibraryDirectory()
    {
        var nativeDirectory = "/data/app/~~abc==/com.mypowertools.android-~~def==/lib/arm64";
        var embedded = Path.Combine(nativeDirectory, OpenListRuntime.AndroidLibraryName);
        var overridden = "/data/local/tmp/openlist";

        Assert.Equal(overridden, OpenListRuntime.SelectAndroidExecutable(overridden, nativeDirectory, path => path == overridden));
        Assert.Equal(embedded, OpenListRuntime.SelectAndroidExecutable(null, nativeDirectory, path => path == embedded));
        // An override that does not exist must fall through to the embedded library.
        Assert.Equal(embedded, OpenListRuntime.SelectAndroidExecutable("/missing", nativeDirectory, path => path == embedded));
        Assert.Null(OpenListRuntime.SelectAndroidExecutable(null, nativeDirectory, _ => false));
        Assert.Null(OpenListRuntime.SelectAndroidExecutable(null, null, _ => true));
    }

    [Fact]
    public void AndroidWithoutEmbeddedRuntimeFailsWithAnActionableError()
    {
        var error = Assert.Throws<PlatformNotSupportedException>(() =>
            OpenListRuntime.RequireAndroidExecutable(null, "/data/app/~~abc==/com.mypowertools.android-~~def==/lib/arm64", _ => false));

        Assert.Contains("libopenlist.so", error.Message, StringComparison.Ordinal);
        Assert.Contains(OpenListRuntime.ExecutableVariable, error.Message, StringComparison.Ordinal);
        Assert.Contains("Android 10+", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MappedLibrariesYieldTheAndroidNativeLibraryDirectory()
    {
        const string maps = """
            7f8c1c0000-7f8c1c4000 r--p 00000000 fd:00 1234 /data/app/~~abc==/com.mypowertools.android-~~def==/lib/arm64/libSystem.Native.so
            7f8c200000-7f8c240000 r-xp 00000000 fd:00 1235 /data/app/~~abc==/com.mypowertools.android-~~def==/lib/arm64/libcoreclr.so
            ffffffffff600000-ffffffffff601000 --xp 00000000 00:00 0                  [vsyscall]
            """;

        Assert.Equal(Normalized("/data/app/~~abc==/com.mypowertools.android-~~def==/lib/arm64"), Normalized(OpenListRuntime.NativeLibraryDirectoryFromMaps(maps)!));
    }

    [Fact]
    public void MappedLibrariesSupportEveryDeviceAbiDirectory()
    {
        Assert.Equal(Normalized("/data/app/~~a==/pkg-~~b==/lib/x86_64"), Normalized(OpenListRuntime.NativeLibraryDirectoryFromMaps(
            "7f0-7f1 r-xp 0 fd:00 1 /data/app/~~a==/pkg-~~b==/lib/x86_64/libmonodroid.so")!));
        Assert.Equal(Normalized("/mnt/expand/123/app/~~a==/pkg-~~b==/lib/arm"), Normalized(OpenListRuntime.NativeLibraryDirectoryFromMaps(
            "7f0-7f1 r-xp 0 fd:00 1 /mnt/expand/123/app/~~a==/pkg-~~b==/lib/arm/libSystem.Native.so")!));
    }

    [Fact]
    public void MapsScanRejectsAppDataAndUnrelatedFiles()
    {
        const string maps = """
            7f0-7f1 r-xp 0 fd:00 1 /data/user/0/com.mypowertools.android/files/libopenlist.so
            7f2-7f3 r-xp 0 fd:00 2 /data/user/0/com.mypowertools.android/no-extension
            7f4-7f5 rw-p 0 fd:00 3 /data/user/0/com.mypowertools.android/files/notes.txt
            7f6-7f7 r-xp 0 fd:00 4 /apex/com.android.runtime/lib64/bionic/libc.so
            7f8-7f9 rw-p 0 fd:00 5 [anon:dalvik-main space]
            """;

        Assert.Null(OpenListRuntime.NativeLibraryDirectoryFromMaps(maps));
    }

    [Theory]
    [InlineData("[anon:dalvik-main space]", null)]
    [InlineData("", null)]
    [InlineData("7f0-7f1 r-xp 00000000 fd:00 1 /data/app/x/lib/arm64/libc.so", "/data/app/x/lib/arm64/libc.so")]
    [InlineData("7f0-7f1 r-xp 00000000 fd:00 1 /data/app/x/lib/arm64/libc.so\n", "/data/app/x/lib/arm64/libc.so")]
    public void MappedPathReadsThePathnameColumn(string line, string? expected) =>
        Assert.Equal(expected is null ? null : Normalized(expected), OpenListRuntime.MappedPath(line) is { } path ? Normalized(path) : null);

    /// <summary>Keeps the path assertions valid on Windows, where the path APIs may use a backslash.</summary>
    private static string Normalized(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    [Fact]
    public async Task InstallIsANoOpWhenTheManagedInstanceAlreadyHasItsRuntime()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-openlist-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var target = OpenListRuntime.DescribeTarget(OpenListRuntimeOrigin.ReleaseArchive, OpenListRuntime.DetectDesktopPlatform(), RuntimeInformation.ProcessArchitecture);
            var executable = OpenListRuntime.LayoutExecutable(root, target);
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllTextAsync(executable, "official openlist placeholder");

            await using var runtime = new OpenListRuntime(root);
            // No network call: the existing runtime file path is reused exactly as before.
            await runtime.InstallAsync(CancellationToken.None);

            Assert.True(File.Exists(executable));
            Assert.Equal("official openlist placeholder", await File.ReadAllTextAsync(executable));
            Assert.False(runtime.Running);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RealPlatformClassifiesAsADesktopOnBuildMachines()
    {
        // The build machine is not Android, so the runtime keeps downloading the official desktop asset.
        Assert.Equal(OpenListRuntimeOrigin.ReleaseArchive, OpenListRuntime.DetectOrigin());
    }
}
