using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Broker;
using MyPowerTools.HostControl;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.Packaging;
using MyPowerTools.Platform;
using MyPowerTools.Platform.Android;
using MyPowerTools.Runtime;
using A = global::Android;

namespace MyPowerTools.Android;

/// <summary>One reported initialization step, surfaced by the Android startup screen.</summary>
internal sealed record StartupProgress(string Stage, string Message, int Step, int TotalSteps);

/// <summary>
/// Owns the shared MyPowerTools runtime inside the Android process. Initialization is staged so
/// the activity can show real progress, never runs on the UI thread, and can be retried after a
/// failure without restarting the process.
/// </summary>
internal static class AndroidHost
{
    private static readonly object Gate = new();
    private static Task? _initialization;
    private static MptHostRuntime? _runtime;
    private static bool _stopRequestedHooked;
    private static Task? _commandRefresh;
    private static int _attempt;

    internal static MptHostRuntime Runtime =>
        _runtime ?? throw new InvalidOperationException("MyPowerTools 运行时尚未初始化。");

    internal static bool IsReady => _runtime is not null;

    internal static string PackageRoot { get; private set; } = "";

    internal static StartupProgress Current { get; private set; } = new("idle", "正在启动 MyPowerTools…", 0, 0);

    /// <summary>Raised for every stage change; the handler may run on any thread.</summary>
    internal static event Action<StartupProgress>? Progress;

    internal static Task InitializeAsync()
    {
        lock (Gate)
        {
            return _initialization ??= Task.Run(InitializeCoreAsync);
        }
    }

    /// <summary>
    /// Rebuilds the runtime after a failed launch. The previous attempt is disposed first so a
    /// retry cannot double-load modules into the process.
    /// </summary>
    internal static async Task RetryAsync()
    {
        Task? previous;
        lock (Gate)
        {
            previous = _initialization;
            _initialization = null;
            _attempt++;
        }

        if (previous is { IsFaulted: true })
        {
            // Observe the failure so the retry does not inherit an unobserved exception.
            try { await previous.ConfigureAwait(false); } catch { }
        }

        var stale = Interlocked.Exchange(ref _runtime, null);
        if (stale is not null)
        {
            try { await stale.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { AndroidStartupLog.Error("retry-dispose", ex); }
        }

        HostControlClient.EmbeddedInvoker = null;
        PlatformPackFactory.EmbeddedPlatformFactory = null;
        _commandRefresh = null;
        await InitializeAsync().ConfigureAwait(false);
    }

    private static void Report(string stage, string message, int step, int totalSteps)
    {
        var progress = new StartupProgress(stage, message, step, totalSteps);
        Current = progress;
        AndroidStartupLog.Info(stage, message);
        try { Progress?.Invoke(progress); } catch (Exception ex) { AndroidStartupLog.Error("progress-handler", ex); }
    }

    private static async Task InitializeCoreAsync()
    {
        const int totalSteps = 6;
        var step = 0;
        try
        {
            Report("prepare", "正在准备运行目录…", ++step, totalSteps);
            var context = A.App.Application.Context;
            AndroidStartupLog.UseRuntimeLogDirectory(null);

            var shares = Path.Combine(context.CacheDir!.AbsolutePath, "shares");
            if (Directory.Exists(shares))
            {
                foreach (var directory in Directory.EnumerateDirectories(shares))
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-7))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
            }

            var paths = RuntimePaths.CreateDefault();
            AndroidStartupLog.UseRuntimeLogDirectory(paths.Logs);
            PackageRoot = Path.Combine(paths.Root, "bundled-modules");

            Report("catalog", "正在释放内置模块目录…", ++step, totalSteps);
            await SyncBundledCatalogAsync(context, PackageRoot).ConfigureAwait(false);

            Report("platform", "正在初始化 Android 平台服务…", ++step, totalSteps);
            var platform = new AndroidPlatformPack();
            PlatformPackFactory.EmbeddedPlatformFactory = () => platform;

            Report("runtime", "正在加载模块、命令与权限…", ++step, totalSteps);
            var runtime = new MptHostRuntime(new PackageReader(), platform.Platform, paths,
                [new InProcDotNetModuleHost()], new Dictionary<string, object>
                {
                    ["secret.store"] = platform.Secrets,
                    ["notification.desktop"] = platform.Notifications,
                    ["clipboard.image"] = platform.ClipboardImages,
                    ["background.activity"] = platform.Background,
                    ["files.downloads"] = platform.Downloads
                });
            _runtime = runtime;
            runtime.Load(PackageRoot);
            runtime.StartModuleEventPump();

            Report("services", "正在连接主机控制与后台事件…", ++step, totalSteps);
            HostControlClient.EmbeddedInvoker = new EmbeddedHostControlInvoker(new HostControlGrpcService(
                runtime, new AuditLog(Path.Combine(paths.Logs, "broker-audit.jsonl"))));
            if (!_stopRequestedHooked)
            {
                AndroidBackgroundActivityService.StopRequested += modules => _ = StopModulesAsync(modules);
                _stopRequestedHooked = true;
            }

            Report("ready", "运行时就绪，正在构建界面…", ++step, totalSteps);
            _commandRefresh ??= RefreshCommandsAsync(runtime);
            AndroidStartupLog.Info("ready", $"Runtime ready (attempt {_attempt + 1})");
        }
        catch (Exception ex)
        {
            AndroidStartupLog.Error("initialize", ex);
            var failed = Interlocked.Exchange(ref _runtime, null);
            if (failed is not null)
            {
                try { await failed.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            HostControlClient.EmbeddedInvoker = null;
            throw;
        }
    }

    private static async Task RefreshCommandsAsync(MptHostRuntime runtime)
    {
        try
        {
            await runtime.RefreshDynamicCommandsAsync(CancellationToken.None).ConfigureAwait(false);
            AndroidStartupLog.Info("commands", "Dynamic command catalog refreshed");
        }
        catch (Exception error)
        {
            AndroidStartupLog.Error("commands", "Command catalog refresh: " + error.Message);
        }
    }

    private static async Task StopModulesAsync(IReadOnlyList<string> modules)
    {
        foreach (var module in modules)
        {
            await Runtime.SetModuleEnabledAsync(module, false).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copies the bundled catalog out of the APK once per installed build. Re-copying every
    /// launch was pure first-launch latency, and a half-finished copy now self-heals because the
    /// stamp is written last.
    /// </summary>
    private static async Task SyncBundledCatalogAsync(A.Content.Context context, string packageRoot)
    {
        var stamp = BundledCatalogStamp(context);
        var stampPath = Path.Combine(packageRoot, ".catalog-stamp");
        if (File.Exists(stampPath) && string.Equals(File.ReadAllText(stampPath).Trim(), stamp, StringComparison.Ordinal))
        {
            AndroidStartupLog.Info("catalog", "Bundled catalog already current for " + stamp);
            return;
        }

        var started = DateTimeOffset.UtcNow;
        if (Directory.Exists(packageRoot))
        {
            Directory.Delete(packageRoot, recursive: true);
        }

        Directory.CreateDirectory(packageRoot);
        await CopyAssetsAsync("modules", packageRoot).ConfigureAwait(false);
        PruneUnbundledModules(packageRoot);
        File.WriteAllText(stampPath, stamp);
        AndroidStartupLog.Info("catalog",
            $"Bundled catalog refreshed for {stamp} in {(DateTimeOffset.UtcNow - started).TotalMilliseconds:F0} ms");
    }

    /// <summary>
    /// The APK deliberately omits a few platform-specific sub-modules (for example the desktop
    /// AndroidTools notifications module that the Android Remote Notifications module replaces).
    /// package.json still enumerates them, and the packaging reader treats a listed-but-absent
    /// module as a hard failure that would abort the whole catalog load, so the copied manifest is
    /// rewritten to describe what is actually bundled.
    /// </summary>
    private static void PruneUnbundledModules(string packageRoot)
    {
        foreach (var manifestPath in Directory.EnumerateFiles(packageRoot, "package.json", SearchOption.AllDirectories))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject manifest ||
                    manifest["modules"] is not JsonArray modules)
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(manifestPath)!;
                var missing = modules
                    .Where(node => node?.GetValue<string>() is { } relative &&
                                   !File.Exists(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar))))
                    .ToArray();
                if (missing.Length == 0)
                {
                    continue;
                }

                foreach (var node in missing)
                {
                    modules.Remove(node);
                }

                File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                AndroidStartupLog.Info("catalog", $"Pruned {missing.Length} unbundled module(s) from {manifestPath}");
            }
            catch (Exception ex)
            {
                // A manifest this host cannot rewrite is reported, not fatal: the packaging reader
                // decides whether the catalog is loadable.
                AndroidStartupLog.Error("catalog-prune", manifestPath + ": " + ex.Message);
            }
        }
    }

    private static string BundledCatalogStamp(A.Content.Context context)
    {
        try
        {
            // The legacy int overload is the only one that accepts "no flags" on every supported API level.
#pragma warning disable CA1422
            var info = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!;
#pragma warning restore CA1422
            return $"{info.LongVersionCode}:{info.LastUpdateTime}";
        }
        catch (Exception ex)
        {
            AndroidStartupLog.Error("catalog-stamp", ex);
            // Fall back to the APK's own timestamp; a wrong stamp only costs one extra copy.
            try { return "apk:" + File.GetLastWriteTimeUtc(context.ApplicationInfo!.SourceDir!).Ticks; }
            catch { return "unknown"; }
        }
    }

    private static async Task CopyAssetsAsync(string assetPath, string destination)
    {
        var assets = A.App.Application.Context.Assets!;
        var entries = assets.List(assetPath)!;
        if (entries.Length == 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = assets.Open(assetPath);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output).ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var name in entries)
        {
            await CopyAssetsAsync(assetPath + "/" + name, Path.Combine(destination, name)).ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<(string Id, string Title)> ActivationTargets(string uri)
    {
        return ToolManifests().Where(tool =>
        {
            var prefixes = tool.Manifest?["activationUriPrefixes"]?.AsArray();
            return prefixes?.Any(p => p?.GetValue<string>() is { } prefix && uri.StartsWith(prefix, StringComparison.Ordinal)) == true;
        }).Select(tool => (tool.Id, tool.Title)).ToArray();
    }

    // Share destinations are declared by tools, not selected by a tool-specific switch in the host.
    internal static IReadOnlyList<(string Id, string Title)> ShareTargets(string mime)
    {
        return ToolManifests().Where(tool =>
        {
            var types = tool.Manifest?["shareMimeTypes"]?.AsArray();
            return types?.Any(t => t?.GetValue<string>() is { } type && (type == "*/*" || type == mime || type == mime.Split('/')[0] + "/*")) == true;
        }).Select(tool => (tool.Id, tool.Title)).ToArray();
    }

    private static IEnumerable<(string Id, string Title, JsonNode? Manifest)> ToolManifests()
    {
        if (_runtime is null)
        {
            return [];
        }

        return _runtime.ListTools()
            .Where(tool => tool.State != "unsupported")
            .Select(tool =>
            {
                var path = ToolManifestPath(tool.Descriptor.SourceDirectory);
                JsonNode? manifest = null;
                if (path is not null)
                {
                    try { manifest = JsonNode.Parse(File.ReadAllText(path)); }
                    catch (Exception ex) { AndroidStartupLog.Error("tool-manifest", path + ": " + ex.Message); }
                }

                return (tool.Descriptor.ToolId, tool.Descriptor.Title, manifest);
            })
            .Where(tool => tool.Item3 is not null);
    }

    /// <summary>
    /// <c>ToolDescriptor.SourceDirectory</c> is already the directory that holds <c>tool.json</c>;
    /// appending <c>ui</c> to it looked for <c>ui/ui/tool.json</c> and made every share and deep
    /// link report "no enabled tool" even though the tool was loaded. The nested location is still
    /// accepted so an older catalog layout keeps resolving.
    /// </summary>
    private static string? ToolManifestPath(string sourceDirectory)
    {
        var direct = Path.Combine(sourceDirectory, "tool.json");
        if (File.Exists(direct))
        {
            return direct;
        }

        var nested = Path.Combine(sourceDirectory, "ui", "tool.json");
        return File.Exists(nested) ? nested : null;
    }
}
