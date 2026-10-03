using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Platform.Abstractions;
using PasteImage.MyPowerTools;
using PasteImage.Surface.ViewModels;

namespace MyPowerTools.Tests;

public sealed class PasteImageWorkflowTests
{
    [Theory]
    [InlineData("-V")]
    [InlineData("-oProxyCommand")]
    [InlineData("--")]
    public async Task Settings_reject_hosts_that_OpenSSH_treats_as_options(string host)
    {
        var result = await new PasteImageModule().ValidateSettingsAsync(
            new SettingsPatch("paste-image", 1, new JsonObject { ["remoteHost"] = host }), default);
        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData("/tmp/../etc")]
    [InlineData("/tmp/image folder")]
    [InlineData("/tmp;touch")]
    public async Task Settings_reject_unsafe_remote_directories(string directory)
    {
        var result = await new PasteImageModule().ValidateSettingsAsync(
            new SettingsPatch("paste-image", 1, new JsonObject { ["remoteDirectory"] = directory }), default);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Configured_upload_timeout_is_respected_by_command_deadline()
    {
        var module = new PasteImageModule();
        var initial = Assert.Single(await module.ListCommandsAsync(default), command => command.Id == "paste-image.upload");
        Assert.True(initial.TimeoutMs >= 300_000);
        await module.ApplySettingsAsync(new SettingsSnapshotDocument("paste-image", 1,
            new JsonObject { ["uploadTimeoutSeconds"] = 300 }, DateTimeOffset.UtcNow), default);
        var descriptor = Assert.Single(await module.ListCommandsAsync(default), command => command.Id == "paste-image.upload");
        Assert.True(descriptor.TimeoutMs >= 300_000);
    }

    [Fact]
    public async Task Configure_inspect_and_probe_use_platform_provider_without_writing_clipboard()
    {
        using var harness = new ModuleHarness();
        await harness.InitializeAsync();
        await harness.Module.ApplySettingsAsync(new SettingsSnapshotDocument("paste-image", 1,
            new JsonObject { ["remoteHost"] = "qa@example.test", ["remoteDirectory"] = "/tmp/mpt",
                ["uploadTimeoutSeconds"] = 60, ["afterUploadShortcut"] = "" }, DateTimeOffset.UtcNow), default);
        using var inspect = JsonDocument.Parse((await harness.ExecuteAsync("paste-image.inspect")).Output);
        Assert.Equal("qa@example.test", inspect.RootElement.GetProperty("remoteHost").GetString());
        Assert.Equal(60, inspect.RootElement.GetProperty("uploadTimeoutSeconds").GetInt32());
        Assert.Equal("", inspect.RootElement.GetProperty("afterUploadShortcut").GetString());
        using var probe = JsonDocument.Parse((await harness.ExecuteAsync("paste-image.clipboard.probe")).Output);
        Assert.Equal(7, probe.RootElement.GetProperty("Width").GetInt32());
        Assert.Equal(5, probe.RootElement.GetProperty("Height").GetInt32());
        Assert.Equal(3, probe.RootElement.GetProperty("SizeBytes").GetInt32());
        Assert.Equal(1, harness.Clipboard.ReadCount);
        Assert.Equal(0, harness.Clipboard.WriteCount);
    }

    [Fact]
    public async Task History_reads_existing_records_and_preserves_corrupt_file()
    {
        using var harness = new ModuleHarness();
        await harness.InitializeAsync();
        var path = Path.Combine(harness.Directory, "upload-history.json");
        var history = JsonSerializer.Serialize(new[] { new { RemotePath = "/tmp/image.png", LocalPreviewPath = "",
            UploadedAt = DateTimeOffset.UtcNow, Width = 7, Height = 5, SizeBytes = 3 } });
        await File.WriteAllTextAsync(path, history);
        using var result = JsonDocument.Parse((await harness.ExecuteAsync("paste-image.history")).Output);
        Assert.Equal("/tmp/image.png", result.RootElement.GetProperty("items")[0].GetProperty("RemotePath").GetString());
        await File.WriteAllTextAsync(path, "{broken");
        using var corrupt = JsonDocument.Parse((await harness.ExecuteAsync("paste-image.history")).Output);
        Assert.Equal(0, corrupt.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Missing_capabilities_fail_upload_and_notification_with_structured_results()
    {
        var module = new PasteImageModule();
        var upload = await module.ExecuteCommandAsync(new CommandRequest("test", "paste-image.upload", new()), default);
        Assert.False(upload.Success);
        Assert.Contains("clipboard", upload.Error!.Message);
        var notify = await module.ExecuteCommandAsync(new CommandRequest("test", "paste-image.notification.test", new()), default);
        Assert.False(notify.Success);
        var unknown = await module.ExecuteCommandAsync(new CommandRequest("test", "paste-image.unknown", new()), default);
        Assert.False(unknown.Success);
        await module.DisposeAsync(default);
        var events = new List<MptModuleEvent>();
        await foreach (var item in module.SubscribeEventsAsync(new EventCursor(0), default)) events.Add(item);
        Assert.Contains(events, item => item.Type == "upload.failed");
    }

    [Fact]
    public void Surface_loads_latest_five_records_and_copies_selected_path_to_injected_writer()
    {
        MyPowerTools.Shell.Avalonia.ShellRealScreenshotWriter.RunHeadlessCheck(() =>
        {
        // These command delegates return completed tasks. Keep their synchronous
        // assertions on the isolated UI thread owned by the headless renderer.
        using var harness = new SurfaceHarness();
        RequireSynchronous(harness.Model.InitializeAsync());
        Assert.Equal(5, harness.Model.History.Count);
        Assert.True(harness.Model.HasHistory);
        Assert.False(harness.Model.HasPreview);
        Assert.True(harness.Model.History[0].IsSelected);
        Assert.Contains("本地预览", harness.Model.PreviewPlaceholder);
        string? copied = null;
        harness.Model.ClipboardWriter = value => { copied = value; return Task.CompletedTask; };
        RequireSynchronous(Assert.IsType<MptAsyncRelayCommand>(harness.Model.History[2].CopyCommand).ExecuteAsync());
        Assert.Equal("/tmp/2.png", copied);
        Assert.Contains("路径已复制", harness.Model.Message);
        });
    }

    [Fact]
    public void Surface_reports_empty_clipboard_and_releases_busy_state()
    {
        MyPowerTools.Shell.Avalonia.ShellRealScreenshotWriter.RunHeadlessCheck(() =>
        {
        using var harness = new SurfaceHarness();
        RequireSynchronous(harness.Model.InitializeAsync());
        RequireSynchronous(Assert.IsType<MptAsyncRelayCommand>(harness.Model.UploadCommand).ExecuteAsync());
        Assert.Contains("请先复制一张图片", harness.Model.Message);
        Assert.False(harness.Model.IsBusy);
        Assert.True(harness.Model.CanUpload);
        });
    }

    [Fact]
    public async Task Corrupt_preview_is_optional_and_does_not_abort_history_loading()
    {
        MyPowerTools.Shell.Avalonia.ShellRealScreenshotWriter.RunHeadlessCheck(() => { });
        var directory = Path.Combine(Path.GetTempPath(), "mpt-paste-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "corrupt.png");
            await File.WriteAllTextAsync(path, "this is not a png");
            var method = typeof(PasteImageViewModel).GetMethod("LoadBitmapAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var validPath = Path.Combine(directory, "valid.png");
            MyPowerTools.Shell.Avalonia.ShellRealScreenshotWriter.RunHeadlessCheck(() =>
            {
                using var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(2, 3), new Avalonia.Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
                bitmap.Save(validPath);
            });
            using var decoded = await (Task<Avalonia.Media.Imaging.Bitmap?>)method.Invoke(null, [validPath, CancellationToken.None])!;
            Assert.NotNull(decoded);
            Assert.Equal(new Avalonia.PixelSize(2, 3), decoded.PixelSize);
            var task = (Task<Avalonia.Media.Imaging.Bitmap?>)method.Invoke(null, [path, CancellationToken.None])!;
            Assert.Null(await task);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void RequireSynchronous(Task task)
    {
        // The fake command transport and missing preview path complete inline.
        // Fail immediately if a future edit introduces work needing the UI pump.
        Assert.True(task.IsCompleted, "Headless fixture must complete inline; asynchronous work needs a pumped UI fixture.");
        task.GetAwaiter().GetResult();
    }

    private sealed class ModuleHarness : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "mpt-paste-e2e", Guid.NewGuid().ToString("N"));
        public PasteImageModule Module { get; } = new();
        public ClipboardFake Clipboard { get; } = new();
        public async Task InitializeAsync() => Assert.True((await Module.InitializeAsync(new ModuleContext("test", "1", "paste-image", "paste-image",
            Directory, Path.Combine(Directory, "cache"), Path.Combine(Directory, "logs"), "windows", ["clipboard.image"],
            new Dictionary<string, object> { ["clipboard.image"] = Clipboard }), default)).Ok);
        public async Task<CommandExecutionResult> ExecuteAsync(string id) => await Module.ExecuteCommandAsync(new("test", id, new()), default);
        public void Dispose()
        {
            Module.DisposeAsync(default).GetAwaiter().GetResult();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class ClipboardFake : IClipboardImageService
    {
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public Task<ClipboardImagePayload> ReadPngAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); ReadCount++; return Task.FromResult(new ClipboardImagePayload([1, 2, 3], 7, 5)); }
        public Task WriteTextAsync(string value, CancellationToken token)
        { token.ThrowIfCancellationRequested(); WriteCount++; return Task.CompletedTask; }
    }

    private sealed class SurfaceHarness : IDisposable
    {
        public PasteImageViewModel Model { get; }
        public SurfaceHarness()
        {
            Model = new(new MptAvaloniaSurfaceContext("paste-image", "main", "", "light", ExecuteAsync,
                (_, _, _) => Task.CompletedTask, null!, _ => { }));
        }
        private Task<CommandExecutionResult> ExecuteAsync(string command, JsonObject? args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (command == "paste-image.upload") return Task.FromResult(new CommandExecutionResult("test", command, "failed", false, "",
                new MptRuntimeError("test", "No image is available in the clipboard")));
            var output = command == "paste-image.inspect"
                ? JsonSerializer.Serialize(new { State = "running", remoteHost = "qa", remoteDirectory = "/tmp", Summary = "Ready" })
                : JsonSerializer.Serialize(new { items = Enumerable.Range(0, 7).Select(index => new { RemotePath = $"/tmp/{index}.png",
                    LocalPreviewPath = "", UploadedAt = DateTimeOffset.UtcNow, Width = 7, Height = 5, SizeBytes = 3 }) });
            return Task.FromResult(new CommandExecutionResult("test", command, "succeeded", true, output));
        }
        public void Dispose() => Model.Dispose();
    }
}
