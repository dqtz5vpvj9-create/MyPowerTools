using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FileTransfer.Core;

/// <summary>Where a platform's OpenList runtime comes from.</summary>
internal enum OpenListRuntimeOrigin
{
    /// <summary>Official release archive downloaded into the tool data directory (Windows/macOS/Linux).</summary>
    ReleaseArchive,

    /// <summary>Native executable packaged inside the APK and extracted to the app's native library directory (Android).</summary>
    EmbeddedAndroidLibrary,
}

/// <summary>Desktop operating systems the official release archives are named after.</summary>
internal enum OpenListDesktopPlatform { Windows, MacOS, Linux }

/// <summary>
/// The resolved runtime target: which official asset a desktop host may download, or the fact that
/// Android must use the copy embedded in the APK. Pure data so tests can assert every combination
/// without touching the machine they run on.
/// </summary>
internal sealed record OpenListRuntimeTarget(OpenListRuntimeOrigin Origin, string AssetOperatingSystem, string AssetArchitecture)
{
    public bool IsAndroid => Origin == OpenListRuntimeOrigin.EmbeddedAndroidLibrary;
    public string ExecutableName => AssetOperatingSystem == "windows" ? "openlist.exe" : "openlist";
    public string ArchiveSuffix => AssetOperatingSystem == "windows" ? ".zip" : ".tar.gz";

    /// <summary>File name of the pinned official release asset, e.g. <c>openlist-android-arm64.tar.gz</c>.</summary>
    public string AssetName => $"openlist-{AssetOperatingSystem}-{AssetArchitecture}{ArchiveSuffix}";
}

/// <summary>Owns only the OpenList process started by this tool. Nothing starts on construction.</summary>
public sealed class OpenListRuntime(string directory) : IAsyncDisposable
{
    public const string Version = "v4.2.6";
    public const int Port = 15244;

    /// <summary>Explicit runtime executable path. Wins over every platform default; used by tests, CI and staged runtimes.</summary>
    public const string ExecutableVariable = "MPT_OPENLIST_RUNTIME";

    /// <summary>
    /// File name the Android host packages as <c>lib/&lt;abi&gt;/libopenlist.so</c>. Android extracts that
    /// APK entry into the app's native library directory, the only place an app may execute from on API 29+.
    /// </summary>
    public const string AndroidLibraryName = "libopenlist.so";

    private const int SigTerm = 15;

    /// <summary>Native library directory names used by Android when it extracts APK libraries at install time.</summary>
    private static readonly string[] AndroidAbiDirectories =
        ["arm64", "arm64-v8a", "arm", "armeabi", "armeabi-v7a", "x86", "x86_64"];

    private Process? _process;
    private Task? _stdout;
    private Task? _stderr;
    public bool Running => _process is { HasExited: false };
    public string Address { get; private set; } = "127.0.0.1";
    public string AdminUrl => $"http://{(Address.Contains(':') ? $"[{Address}]" : Address)}:{Port}/@manage";

    internal const string AndroidRuntimeMissingMessage =
        "Android 版必须使用随 MPT 打包的内嵌 OpenList 运行时（APK 内的 lib/<abi>/libopenlist.so）。" +
        "Android 10+ 不允许执行应用数据目录中的文件，所以这里不会下载或安装 Linux 可执行文件。" +
        "请安装包含该运行时的 MPT 版本，或用 MPT_OPENLIST_RUNTIME 指向一个可执行文件。";

    public async Task<string?> InitializeAdminAsync(CancellationToken token)
    {
        var executable = await ResolveExecutableAsync(token);
        if (File.Exists(Path.Combine(directory, "data", "data.db"))) return null;
        var info = CreateStartInfo(executable);
        foreach (var arg in new[] { "admin", "random", "--data", Path.Combine(directory, "data") }) info.ArgumentList.Add(arg);
        info.Environment["OPENLIST_LOG_ENABLE"] = "false";
        using var process = Process.Start(info) ?? throw new IOException("无法初始化 OpenList。");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var text = await output;
        await error;
        var match = Regex.Match(text, @"(?m)^password:\s*(\S+)");
        if (process.ExitCode != 0 || !match.Success) throw new IOException("OpenList 管理员初始化失败。");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Makes a runtime executable available in the platform's own layout. Desktop downloads the pinned
    /// official release; Android only validates the embedded copy because nothing in app data can be executed.
    /// </summary>
    public async Task InstallAsync(CancellationToken token)
    {
        var target = DetectTarget();
        if (target.IsAndroid)
        {
            // Never download on Android: the linux/android archive would land in app data, where exec() is denied.
            RequireAndroidExecutable(Environment.GetEnvironmentVariable(ExecutableVariable), AndroidNativeLibraryDirectory(), File.Exists);
            return;
        }

        var executable = LayoutExecutable(directory, target);
        if (File.Exists(executable)) return;
        await DownloadReleaseAsync(executable, target, token);
    }

    public async Task StartAsync(string address, CancellationToken token)
    {
        if (Running) return;
        DirectTransfer.RequirePrivateAddress(IPAddress.Parse(address));
        var executable = await ResolveExecutableAsync(token);
        Address = address;
        var info = CreateStartInfo(executable);
        info.ArgumentList.Add("server");
        info.ArgumentList.Add("--data");
        info.ArgumentList.Add(Path.Combine(directory, "data"));
        info.Environment["OPENLIST_ADDR"] = address;
        info.Environment["OPENLIST_HTTP_PORT"] = Port.ToString();
        info.Environment["OPENLIST_TLS_INSECURE_SKIP_VERIFY"] = "false";
        if (OperatingSystem.IsAndroid()) PrepareAndroidEnvironment(info);
        _process?.Dispose();
        _process = Process.Start(info) ?? throw new IOException("无法启动 OpenList。");
        // Initial OpenList logs can contain generated credentials. Never forward them into MPT logs.
        _stdout = _process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _stderr = _process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            for (var i = 0; i < 60; i++)
            {
                token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new IOException($"OpenList 启动失败，退出码 {_process.ExitCode}。");
                try
                {
                    using var response = await http.GetAsync(new Uri(new Uri(AdminUrl), "/ping"), token);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
                await Task.Delay(500, token);
            }
            throw new IOException("OpenList 启动超时。");
        }
        catch { await StopAsync(); throw; }
    }

    public async Task StopAsync()
    {
        if (_process is null) return;
        if (!_process.HasExited)
        {
            await RequestGracefulStopAsync(_process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!_process.HasExited) _process.Kill(true); }
            await _process.WaitForExitAsync();
        }
        if (_stdout is not null) await _stdout;
        if (_stderr is not null) await _stderr;
        _process.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task<string> ResolveExecutableAsync(CancellationToken token)
    {
        var target = DetectTarget();
        if (target.IsAndroid)
            return RequireAndroidExecutable(Environment.GetEnvironmentVariable(ExecutableVariable), AndroidNativeLibraryDirectory(), File.Exists);

        // A runtime shipped next to the module wins over one downloaded into the data directory, so an
        // offline installation never needs the network; managed instances keep their existing layout.
        var executable = SelectDesktopExecutable(
            Environment.GetEnvironmentVariable(ExecutableVariable),
            PackagedExecutable(ModuleDirectory(), target),
            LayoutExecutable(directory, target),
            File.Exists);
        if (!File.Exists(executable)) await InstallAsync(token);
        return executable;
    }

    /// <summary>
    /// Resolution order shared by desktop hosts: explicit override, runtime packaged next to the module,
    /// then the data-directory layout a managed instance already uses.
    /// </summary>
    internal static string SelectDesktopExecutable(string? overridePath, string? packagedPath, string installedPath, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && fileExists(overridePath)) return overridePath;
        if (!string.IsNullOrWhiteSpace(packagedPath) && fileExists(packagedPath)) return packagedPath;
        return installedPath;
    }

    /// <summary>Android resolution: the embedded library or an explicit override; never a data-directory binary.</summary>
    internal static string RequireAndroidExecutable(string? overridePath, string? nativeLibraryDirectory, Func<string, bool> fileExists) =>
        SelectAndroidExecutable(overridePath, nativeLibraryDirectory, fileExists) ?? throw new PlatformNotSupportedException(AndroidRuntimeMissingMessage);

    /// <summary>
    /// The data-directory layout every existing managed instance uses: <c>{directory}/{Version}/openlist[.exe]</c>.
    /// </summary>
    internal static string LayoutExecutable(string directory, OpenListRuntimeTarget target) =>
        Path.Combine(directory, Version, target.ExecutableName);

    /// <summary>
    /// A runtime staged next to the module by an installer: <c>{module}/runtime/openlist/{Version}/openlist[.exe]</c>.
    /// Null when the module location is unknown (single-file publish), so no relative path can be probed.
    /// </summary>
    internal static string? PackagedExecutable(string? moduleDirectory, OpenListRuntimeTarget target) =>
        string.IsNullOrEmpty(moduleDirectory)
            ? null
            : Path.Combine(moduleDirectory, "runtime", "openlist", Version, target.ExecutableName);

    /// <summary>
    /// Android can only execute the APK's own native libraries, so the runtime is taken from
    /// <c>nativeLibraryDir/libopenlist.so</c>; an explicit override wins for tests and staged runtimes.
    /// </summary>
    internal static string? SelectAndroidExecutable(string? overridePath, string? nativeLibraryDirectory, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && fileExists(overridePath)) return overridePath;
        if (!string.IsNullOrWhiteSpace(nativeLibraryDirectory))
        {
            var candidate = Path.Combine(nativeLibraryDirectory, AndroidLibraryName);
            if (fileExists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Resolves Android's native library directory without referencing Mono.Android from this assembly:
    /// the module is loaded into its own context, so the Android API is reached by reflection and any
    /// failure falls back to reading the mapped libraries of the current process.
    /// </summary>
    internal static string? AndroidNativeLibraryDirectory()
    {
        var reflected = AndroidNativeLibraryDirectoryFromApi();
        if (!string.IsNullOrWhiteSpace(reflected)) return reflected;
        try
        {
            return File.Exists("/proc/self/maps") ? NativeLibraryDirectoryFromMaps(File.ReadAllText("/proc/self/maps")) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? AndroidNativeLibraryDirectoryFromApi()
    {
        try
        {
            var application = Type.GetType("Android.App.Application, Mono.Android", throwOnError: false);
            var context = application?.GetProperty("Context", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var info = context?.GetType().GetProperty("ApplicationInfo", BindingFlags.Public | BindingFlags.Instance)?.GetValue(context);
            return info?.GetType().GetProperty("NativeLibraryDir", BindingFlags.Public | BindingFlags.Instance)?.GetValue(info) as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Derives the native library directory from <c>/proc/self/maps</c>. Only <c>lib&lt;abi&gt;</c> directories
    /// count: a mapped file under the app's own data directory is exactly the location Android refuses to execute.
    /// </summary>
    internal static string? NativeLibraryDirectoryFromMaps(string maps)
    {
        foreach (var line in maps.Split('\n'))
        {
            var path = MappedPath(line);
            if (path is null || !path.EndsWith(".so", StringComparison.Ordinal)) continue;
            if (!Path.GetFileName(path).StartsWith("lib", StringComparison.Ordinal)) continue;
            var parent = Path.GetDirectoryName(path);
            if (parent is null) continue;
            if (Array.IndexOf(AndroidAbiDirectories, Path.GetFileName(parent)) < 0) continue;
            return parent;
        }
        return null;
    }

    /// <summary>Extracts the pathname column of one <c>/proc/self/maps</c> line.</summary>
    internal static string? MappedPath(string line)
    {
        var marker = line.IndexOf(" /", StringComparison.Ordinal);
        if (marker < 0) return null;
        var path = line[(marker + 1)..].TrimEnd('\r', '\n', ' ', '\t');
        return path.StartsWith('/') ? path : null;
    }

    /// <summary>Classifies the current platform. Android is checked before the Linux fallback: it is Linux-based.</summary>
    internal static OpenListRuntimeOrigin DetectOrigin() =>
        OperatingSystem.IsAndroid() ? OpenListRuntimeOrigin.EmbeddedAndroidLibrary : OpenListRuntimeOrigin.ReleaseArchive;

    internal static OpenListDesktopPlatform DetectDesktopPlatform() =>
        OperatingSystem.IsWindows() ? OpenListDesktopPlatform.Windows :
        OperatingSystem.IsMacOS() ? OpenListDesktopPlatform.MacOS : OpenListDesktopPlatform.Linux;

    internal static OpenListRuntimeTarget DescribeTarget(OpenListRuntimeOrigin origin, OpenListDesktopPlatform desktop, Architecture architecture)
    {
        var arch = architecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(),
        };
        var os = origin == OpenListRuntimeOrigin.EmbeddedAndroidLibrary
            ? "android"
            : desktop switch
            {
                OpenListDesktopPlatform.Windows => "windows",
                OpenListDesktopPlatform.MacOS => "darwin",
                _ => "linux",
            };
        return new OpenListRuntimeTarget(origin, os, arch);
    }

    private static OpenListRuntimeTarget DetectTarget() =>
        DescribeTarget(DetectOrigin(), DetectDesktopPlatform(), RuntimeInformation.ProcessArchitecture);

    private static string? ModuleDirectory()
    {
        var location = typeof(OpenListRuntime).Assembly.Location;
        return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
    }

    private static ProcessStartInfo CreateStartInfo(string executable) =>
        new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };

    /// <summary>Android has no writable /tmp; point Go's temporary files at the app sandbox.</summary>
    private void PrepareAndroidEnvironment(ProcessStartInfo info)
    {
        var temp = Path.Combine(directory, "tmp");
        Directory.CreateDirectory(temp);
        info.Environment["TMPDIR"] = temp;
        info.Environment["TMP"] = temp;
    }

    private async Task DownloadReleaseAsync(string executable, OpenListRuntimeTarget target, CancellationToken token)
    {
        var uri = $"https://github.com/OpenListTeam/OpenList/releases/download/{Version}/{target.AssetName}";
        var targetDirectory = Path.GetDirectoryName(executable)!;
        Directory.CreateDirectory(targetDirectory);
        var archive = Path.Combine(targetDirectory, "download" + target.ArchiveSuffix);
        var staging = Path.Combine(targetDirectory, "extract");
        Directory.CreateDirectory(staging);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MyPowerTools-FileTransfer/0.1.0");
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                await response.Content.CopyToAsync(output, token);
            if (target.ArchiveSuffix == ".zip") ZipFile.ExtractToDirectory(archive, staging, true);
            else
            {
                await using var input = File.OpenRead(archive);
                await using var gzip = new GZipStream(input, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, staging, true, token);
            }
            var binary = Directory.GetFiles(staging, Path.GetFileName(executable), SearchOption.AllDirectories).Single();
            File.Move(binary, executable, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    /// <summary>OpenList shuts down on SIGTERM; Windows has no equivalent signal, so it is killed there.</summary>
    private static async Task RequestGracefulStopAsync(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            process.Kill(true);
            return;
        }

        try
        {
            // Plain libc, so the same code works on Linux, macOS and Android (no /bin/kill dependency).
            if (kill(process.Id, SigTerm) == 0) return;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }

        try
        {
            using var stop = Process.Start(new ProcessStartInfo("/bin/kill")
            { UseShellExecute = false, ArgumentList = { "-TERM", process.Id.ToString() } });
            if (stop is not null) { await stop.WaitForExitAsync(); return; }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { }

        if (!process.HasExited) process.Kill(true);
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
