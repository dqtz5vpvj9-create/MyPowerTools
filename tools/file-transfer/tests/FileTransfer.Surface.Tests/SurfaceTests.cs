using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace FileTransfer.Surface.Tests;

/// <summary>
/// Behavioral tests for the shared file-transfer surface. Every case drives the real control tree
/// with a scripted module: no device, no Tailscale and no OpenList server is required, and the
/// commands the surface issues are asserted instead of assumed.
/// </summary>
public sealed class SurfaceTests
{
    public static TheoryData<int> Widths => new() { 320, 360, 390, 768 };

    public static TheoryData<int, int> FirstScreenSizes => new() { { 320, 900 }, { 360, 800 } };

    // The harness hosts the surface directly in the window, so the window equals the surface
    // viewport. A real phone spends roughly this much more before the surface starts: ~24 Android
    // status bar + ~56 MPT mobile top bar + ~8 navigation hint.
    private const double SystemChromeAllowance = 88;

    [AvaloniaTheory]
    [MemberData(nameof(FirstScreenSizes))]
    public async Task Send_flow_is_reachable_on_the_first_screen(int width, int height)
    {
        await RunAsync(async scope =>
        {
            var fake = new Fake(scope.Root);
            var view = new TransferView(fake.Context());
            var host = new Border { Child = view, Padding = new Thickness(12) };
            var scroller = new ScrollViewer { Content = host, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var window = new Window { Width = width, Height = height, Content = scroller };
            try
            {
                window.Show();
                for (var pass = 0; pass < 6; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
                var limit = height - SystemChromeAllowance;
                var controls = new Control[]
                {
                    Named<ComboBox>(view, "DevicePicker"),
                    FindButton(view, "选择文件（可多选）"),
                    FindButton(view, "发送")
                };
                var offScreen = new List<string>();
                foreach (var control in controls)
                {
                    var origin = control.TranslatePoint(default, window);
                    if (origin is not { } point || point.Y < 0 || point.Y + control.Bounds.Height > limit)
                        offScreen.Add($"{control.Name ?? (control as Button)?.Content as string} at y={origin?.Y:0.0} height {control.Bounds.Height:0.0}");
                }
                Assert.True(offScreen.Count == 0,
                    $"{width}x{height}: send flow must fit {limit:0} (window minus {SystemChromeAllowance} chrome allowance); off screen: {string.Join("; ", offScreen)}");
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaTheory]
    [MemberData(nameof(Widths))]
    public async Task Surface_keeps_controls_inside_the_phone_viewport(int width)
    {
        await RunAsync(async scope =>
        {
            var fake = new Fake(scope.Root);
            var view = new TransferView(fake.Context());
            foreach (var expander in view.GetLogicalDescendants().OfType<Expander>()) expander.IsExpanded = true;
            var host = new Border { Child = view, Padding = new Thickness(12) };
            var scroller = new ScrollViewer { Content = host, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var window = new Window { Width = width, Height = 900, Content = scroller };
            try
            {
                window.Show();
                for (var pass = 0; pass < 6; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
                var escaped = new List<string>();
                var small = new List<string>();
                foreach (var control in view.GetVisualDescendants().OfType<Control>().Where(c => c is Button or TextBox or ComboBox))
                {
                    if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0 || !control.IsVisible) continue;
                    if (control.GetVisualAncestors().OfType<Control>().Any(c => !c.IsVisible)) continue;
                    if (control.GetVisualAncestors().OfType<ScrollViewer>().Any(s => s.HorizontalScrollBarVisibility is ScrollBarVisibility.Auto or ScrollBarVisibility.Visible)) continue;
                    var label = control switch { Button button => button.Content as string, TextBox box => box.PlaceholderText, ComboBox combo => combo.PlaceholderText, _ => null };
                    var origin = control.TranslatePoint(default, window);
                    if (origin is { } point && (point.X < -1 || point.X + control.Bounds.Width > width + 1))
                        escaped.Add($"{control.GetType().Name} '{label}' at {point.X:0.0} width {control.Bounds.Width:0.0}");
                    if (control.Bounds.Height < 44) small.Add($"{control.GetType().Name} '{label}' height {control.Bounds.Height:0.0}");
                }
                Assert.True(escaped.Count == 0, $"{width}: {string.Join("; ", escaped)}");
                Assert.True(small.Count == 0, $"{width} touch targets under 44: {string.Join("; ", small)}");
                var shots = Environment.GetEnvironmentVariable("MPT_FT_SHOTS");
                if (!string.IsNullOrEmpty(shots) && width <= 390)
                {
                    Directory.CreateDirectory(shots);
                    using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(shots, $"TransferView-{width}.png"));
                    scroller.Offset = new Vector(0, Math.Max(0, scroller.Extent.Height - 900));
                    window.UpdateLayout();
                    using (var bottom = window.CaptureRenderedFrame()) bottom?.Save(Path.Combine(shots, $"TransferView-bottom-{width}.png"));
                }
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Managed_relay_survives_reopen_without_asking_for_a_password()
    {
        await RunAsync(async scope =>
        {
            var fake = new Fake(scope.Root);
            fake.Settings["webDavUrl"] = "http://100.64.0.7:15244/dav/aliyun/MPT";
            fake.Settings["username"] = "mpt-relay123";
            fake.Inspect["openListRunning"] = true;
            fake.Inspect["adminUrl"] = "http://100.64.0.7:15244/@manage";
            var (window, view) = await OpenAsync(fake);
            try
            {
                await ClickAsync(view, "保存并测试");
                await WaitAsync(() => fake.Commands.Contains("file-transfer.openlist.connect"), "openlist.connect");
                Assert.Equal("http://100.64.0.7:15244/dav/aliyun/MPT", fake.Args("file-transfer.openlist.connect")?["url"]?.GetValue<string>());
                Assert.DoesNotContain("file-transfer.configure", fake.Commands);
                Assert.DoesNotContain("file-transfer.cloud.check", fake.Commands);
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Manual_webdav_configuration_is_preserved_not_hijacked_by_the_local_relay()
    {
        await RunAsync(async scope =>
        {
            var fake = new Fake(scope.Root);
            fake.Settings["webDavUrl"] = "https://openlist.example.test/dav/transfer";
            fake.Settings["username"] = "mobile";
            var (window, view) = await OpenAsync(fake);
            try
            {
                await ClickAsync(view, "保存并测试");
                await WaitAsync(() => fake.Commands.Contains("file-transfer.cloud.check"), "cloud.check");
                Assert.DoesNotContain("file-transfer.openlist.connect", fake.Commands);
                var args = fake.Args("file-transfer.configure");
                Assert.NotNull(args);
                Assert.Equal("https://openlist.example.test/dav/transfer", args!["webDavUrl"]?.GetValue<string>());
                Assert.Equal("mobile", args["username"]?.GetValue<string>());
                Assert.False(args.ContainsKey("password"));
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Multi_file_share_activation_accumulates_on_one_surface()
    {
        await RunAsync(async scope =>
        {
            var one = Path.Combine(scope.Root, "one.bin");
            var two = Path.Combine(scope.Root, "two.bin");
            await File.WriteAllTextAsync(one, "1");
            await File.WriteAllTextAsync(two, "22");
            var fake = new Fake(scope.Root);
            var (window, view) = await OpenAsync(fake);
            try
            {
                // Android delivers one activation per shared file to the same Surface instance.
                await view.ActivateAsync(new ToolActivationRequest("file-transfer", "main", new Uri(one).AbsoluteUri));
                Assert.Equal(1, PathCount(view));
                await view.ActivateAsync(new ToolActivationRequest("file-transfer", "main", new Uri(two).AbsoluteUri));
                Assert.Equal(2, PathCount(view));
                await ClickAsync(view, "发送");
                await WaitAsync(() => fake.Commands.Contains("file-transfer.send.direct"), "send.direct");
                var paths = fake.Args("file-transfer.send.direct")?["paths"]?.AsArray().Select(p => p!.GetValue<string>()).ToArray() ?? [];
                Assert.Contains(one, paths);
                Assert.Contains(two, paths);
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task A_new_surface_instance_starts_with_an_empty_selection()
    {
        await RunAsync(async scope =>
        {
            var one = Path.Combine(scope.Root, "one.bin");
            await File.WriteAllTextAsync(one, "1");
            var first = new TransferView(new Fake(scope.Root).Context());
            await first.ActivateAsync(new ToolActivationRequest("file-transfer", "main", new Uri(one).AbsoluteUri));
            Assert.Equal(1, PathCount(first));
            // No disk hand-off: a reopened page must not inherit the previous selection.
            var second = new TransferView(new Fake(scope.Root).Context());
            Assert.Equal(0, PathCount(second));
        });
    }

    [AvaloniaFact]
    public async Task Failed_send_can_be_retried_from_history()
    {
        await RunAsync(async scope =>
        {
            var fake = new Fake(scope.Root);
            var file = Path.Combine(scope.Root, "payload.bin");
            await File.WriteAllTextAsync(file, "data");
            var (window, view) = await OpenAsync(fake);
            try
            {
                await view.ActivateAsync(new ToolActivationRequest("file-transfer", "main", new Uri(file).AbsoluteUri));
                await ClickAsync(view, "发送");
                await WaitAsync(() => fake.Commands.Contains("file-transfer.send.direct"), "send.direct");
                fake.Raise("failed", "payload.bin", 0, 0, "网络中断");
                await PumpAsync(5);
                await ClickAsync(view, "重试");
                await WaitAsync(() => fake.Commands.Count(c => c == "file-transfer.send.direct") >= 2, "second send.direct");
                Assert.Equal(2, fake.Commands.Count(c => c == "file-transfer.send.direct"));
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Progress_cancel_receive_and_cloud_import_use_module_commands()
    {
        await RunAsync(async scope =>
        {
            var fake = new Fake(scope.Root);
            var (window, view) = await OpenAsync(fake);
            try
            {
                var progress = view.GetLogicalDescendants().OfType<ProgressBar>().Single();
                fake.Raise("sending", "payload.bin", 50, 100);
                await PumpAsync(5);
                Assert.True(progress.IsVisible);
                Assert.InRange(progress.Value, 49, 51);

                await ClickAsync(view, "取消传输");
                await WaitAsync(() => fake.Commands.Contains("file-transfer.cancel"), "cancel");

                await ClickAsync(view, "开启接收");
                await WaitAsync(() => fake.Commands.Contains("file-transfer.receive.start"), "receive.start");

                Named<TextBox>(view, "CloudCodeBox").Text = "mpt://cloud/demo-code";
                await ClickAsync(view, "导入连接码");
                await WaitAsync(() => fake.Commands.Contains("file-transfer.cloud.import"), "cloud.import");
                Assert.Equal("mpt://cloud/demo-code", fake.Args("file-transfer.cloud.import")?["code"]?.GetValue<string>());
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Codes_are_routed_to_the_matching_importer()
    {
        await RunAsync(async scope =>
        {
            var fake = new Fake(scope.Root);
            var (window, view) = await OpenAsync(fake);
            try
            {
                Named<TextBox>(view, "CloudCodeBox").Text = "mpt://pair/not-a-cloud-code";
                await ClickAsync(view, "导入连接码");
                await PumpAsync(5);
                Assert.DoesNotContain("file-transfer.cloud.import", fake.Commands);

                Named<TextBox>(view, "PairCodeBox").Text = "mpt://pair/device-code";
                await ClickAsync(view, "添加设备");
                await WaitAsync(() => fake.Commands.Contains("file-transfer.pair.import"), "pair.import");
                Assert.Equal("mpt://pair/device-code", fake.Args("file-transfer.pair.import")?["code"]?.GetValue<string>());
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Remembered_device_is_selected_and_marked()
    {
        await RunAsync(async scope =>
        {
            var fake = new Fake(scope.Root);
            fake.Settings["peers"] = new JsonArray(
                new JsonObject { ["name"] = "MacBook", ["deviceId"] = "mac", ["address"] = "100.64.0.9" },
                new JsonObject { ["name"] = "Windows desktop", ["deviceId"] = "desktop", ["address"] = "100.64.0.5" });
            fake.Settings["lastPeer"] = "desktop";
            var (window, view) = await OpenAsync(fake);
            try
            {
                var devices = Named<ComboBox>(view, "DevicePicker");
                Assert.Equal(1, devices.SelectedIndex);
                var items = devices.ItemsSource!.Cast<string>().ToArray();
                Assert.StartsWith("✓ ", items[1], StringComparison.Ordinal);
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Pair_code_preview_names_the_device_and_hides_the_token()
    {
        await RunAsync(async scope =>
        {
            const string token = "0123456789abcdefghijklmnop";
            var fake = new Fake(scope.Root);
            var (window, view) = await OpenAsync(fake);
            try
            {
                Named<TextBox>(view, "PairCodeBox").Text = Encode("mpt://pair/",
                    $"{{\"deviceId\":\"pc-kitchen\",\"name\":\"客厅电脑\",\"address\":\"100.64.0.7\",\"token\":\"{token}\"}}");
                await PumpAsync(3);
                var preview = Named<TextBlock>(view, "PairPreview");
                Assert.True(preview.IsVisible, "pairing preview must be visible before import");
                Assert.Contains("客厅电脑", preview.Text);
                Assert.Contains("100.64.0.7", preview.Text);
                Assert.DoesNotContain(token, preview.Text);
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Cloud_code_preview_names_the_url_and_account_and_hides_the_password()
    {
        await RunAsync(async scope =>
        {
            const string password = "sup3r-secret-value";
            var fake = new Fake(scope.Root);
            var (window, view) = await OpenAsync(fake);
            try
            {
                Named<TextBox>(view, "CloudCodeBox").Text = Encode("mpt://cloud/",
                    $"{{\"url\":\"https://openlist.example.test/dav/transfer\",\"username\":\"mpt-abc\",\"password\":\"{password}\"}}");
                await PumpAsync(3);
                var preview = Named<TextBlock>(view, "CloudPreview");
                Assert.True(preview.IsVisible, "cloud preview must be visible before import");
                Assert.Contains("https://openlist.example.test/dav/transfer", preview.Text);
                Assert.Contains("mpt-abc", preview.Text);
                Assert.DoesNotContain(password, preview.Text);
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Removing_a_staged_copy_reclaims_it_but_never_a_user_file()
    {
        await RunAsync(async scope =>
        {
            var staged = Path.Combine(scope.Root, "outbox", "stage-1", "picked.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            await File.WriteAllTextAsync(staged, "staged");
            var userFile = Path.Combine(scope.Root, "user", "own.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
            await File.WriteAllTextAsync(userFile, "mine");
            var fake = new Fake(scope.Root);
            var (window, view) = await OpenAsync(fake);
            try
            {
                await view.ActivateAsync(new ToolActivationRequest("file-transfer", "main", new Uri(staged).AbsoluteUri));
                await view.ActivateAsync(new ToolActivationRequest("file-transfer", "main", new Uri(userFile).AbsoluteUri));
                await ClickAsync(view, "移除");
                Assert.False(File.Exists(staged), "a removed staged copy should be reclaimed");
                Assert.True(File.Exists(userFile), "a removed user file must never be deleted");
                await ClickAsync(view, "移除");
                Assert.True(File.Exists(userFile), "a removed user file must never be deleted");
                Assert.Equal(0, PathCount(view));
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Staged_copies_are_kept_while_a_transfer_is_running()
    {
        await RunAsync(async scope =>
        {
            var staged = Path.Combine(scope.Root, "outbox", "stage-2", "busy.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            await File.WriteAllTextAsync(staged, "busy");
            var fake = new Fake(scope.Root);
            fake.Inspect["busy"] = true;
            var (window, view) = await OpenAsync(fake);
            try
            {
                await view.ActivateAsync(new ToolActivationRequest("file-transfer", "main", new Uri(staged).AbsoluteUri));
                await ClickAsync(view, "移除");
                Assert.True(File.Exists(staged), "a staged copy still in use by the module must stay");
                await ClickAsync(view, "清空");
                Assert.True(File.Exists(staged), "clearing the list while busy must not delete staged copies");
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Completed_send_reclaims_its_staged_copy_and_keeps_user_files()
    {
        await RunAsync(async scope =>
        {
            var staged = Path.Combine(scope.Root, "outbox", "stage-3", "picked.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            await File.WriteAllTextAsync(staged, "staged");
            var userFile = Path.Combine(scope.Root, "user", "own.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
            await File.WriteAllTextAsync(userFile, "mine");
            var fake = new Fake(scope.Root);
            var (window, view) = await OpenAsync(fake);
            try
            {
                await view.ActivateAsync(new ToolActivationRequest("file-transfer", "main", new Uri(staged).AbsoluteUri));
                await view.ActivateAsync(new ToolActivationRequest("file-transfer", "main", new Uri(userFile).AbsoluteUri));
                await ClickAsync(view, "发送");
                await WaitAsync(() => fake.Commands.Contains("file-transfer.send.direct"), "send.direct");
                fake.Raise("completed", "picked.bin", 6, 6);
                await WaitAsync(() => !File.Exists(staged), "staged copy reclaimed once the transfer completed");
                Assert.True(File.Exists(userFile), "a user file must never be deleted");
                Assert.Equal(1, PathCount(view));
            }
            finally { window.Close(); }
        });
    }

    [AvaloniaFact]
    public async Task Disposing_the_surface_reclaims_its_staged_copies()
    {
        await RunAsync(async scope =>
        {
            var staged = Path.Combine(scope.Root, "outbox", "stage-4", "detached.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            await File.WriteAllTextAsync(staged, "detached");
            var fake = new Fake(scope.Root);
            var (window, view) = await OpenAsync(fake);
            await view.ActivateAsync(new ToolActivationRequest("file-transfer", "main", new Uri(staged).AbsoluteUri));
            window.Close();
            await PumpAsync(3);
            Assert.False(File.Exists(staged), "a staged copy must not outlive the surface that created it");
        });
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static async Task RunAsync(Func<TempScope, Task> body)
    {
        using var scope = new TempScope();
        try { await body(scope); }
        catch { scope.Keep(); throw; }
    }

    private static string Encode(string prefix, string json) =>
        prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int PathCount(TransferView view)
    {
        var digits = new string((Named<TextBlock>(view, "FileSelectionHint").Text ?? "").SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return digits.Length == 0 ? 0 : int.Parse(digits);
    }

    private static async Task<(Window Window, TransferView View)> OpenAsync(Fake fake, int width = 400)
    {
        var view = new TransferView(fake.Context());
        var window = new Window { Width = width, Height = 800, Content = new ScrollViewer { Content = view } };
        window.Show();
        await PumpAsync(8);
        return (window, view);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => string.Equals(control.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"control '{name}' of type {typeof(T).Name} was not found");

    private static Button FindButton(Control root, string label) =>
        root.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => string.Equals(button.Content as string, label, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"button '{label}' not found; found: {string.Join(", ", root.GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string))}");

    private static async Task ClickAsync(Control root, string label)
    {
        FindButton(root, label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await PumpAsync(5);
    }

    private static async Task PumpAsync(int rounds)
    {
        for (var index = 0; index < rounds; index++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        for (var index = 0; index < 300; index++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail("timed out waiting for " + what);
    }

    /// <summary>Per-test data root under MPT_TEST_TEMP (defaults to the platform temp path).</summary>
    private sealed class TempScope : IDisposable
    {
        private bool _keep;
        public string Root { get; }

        public TempScope()
        {
            var parent = Environment.GetEnvironmentVariable("MPT_TEST_TEMP");
            if (string.IsNullOrWhiteSpace(parent)) parent = Path.GetTempPath();
            Root = Path.Combine(parent, "file-transfer-surface-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Keep() => _keep = true;

        public void Dispose()
        {
            if (_keep) return;
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Scripted module: records every command and answers like the real one for the state the page reads.</summary>
    private sealed class Fake(string dataDirectory)
    {
        public List<(string Command, JsonObject? Args)> Calls { get; } = [];
        public JsonObject Inspect { get; set; } = DefaultInspect();
        public Action<MptSurfaceEvent>? Events { get; private set; }
        public string DataDirectory { get; } = dataDirectory;
        public JsonObject Settings => (JsonObject)Inspect["settings"]!;
        public IEnumerable<string> Commands => Calls.Select(call => call.Command);
        public JsonObject? Args(string command) => Calls.LastOrDefault(call => call.Command == command).Args;

        public static JsonObject DefaultInspect() => new()
        {
            ["settings"] = new JsonObject
            {
                ["receiveDirectory"] = "Downloads/MyPowerTools",
                ["deviceId"] = "pc-demo",
                ["listenAddress"] = "100.64.0.2",
                ["webDavUrl"] = "",
                ["username"] = "",
                ["peers"] = new JsonArray(new JsonObject { ["name"] = "Windows desktop", ["deviceId"] = "desktop", ["address"] = "100.64.0.5" }),
                ["lastPeer"] = "desktop"
            },
            ["receiving"] = false,
            ["busy"] = false,
            ["openListRunning"] = false,
            ["adminUrl"] = "http://127.0.0.1:15244/@manage",
            ["history"] = new JsonArray()
        };

        public void Raise(string state, string name, long done = 0, long total = 0, string message = "")
        {
            var payload = new JsonObject { ["name"] = name, ["state"] = state, ["done"] = done, ["total"] = total, ["message"] = message };
            Events?.Invoke(new MptSurfaceEvent(1, "file-transfer", "transfer.changed", DateTimeOffset.UtcNow, payload));
        }

        public MptAvaloniaSurfaceContext Context() => new(
            "file-transfer", "main", DataDirectory, "light",
            ExecuteAsync,
            (_, _, _) => Task.CompletedTask,
            null!,
            _ => { },
            callback => { Events = callback; return new Subscription(); });

        private Task<CommandExecutionResult> ExecuteAsync(string command, JsonObject? args, CancellationToken token)
        {
            Calls.Add((command, args?.DeepClone().AsObject()));
            var json = command switch
            {
                "file-transfer.inspect" => Inspect.ToJsonString(),
                "file-transfer.pair.import" => "{\"paired\":\"Windows desktop\"}",
                "file-transfer.cloud.export" => "{\"code\":\"mpt://cloud/demo\"}",
                "file-transfer.cloud.list" => "[]",
                "file-transfer.openlist.start" => "{\"adminUrl\":\"http://100.64.0.7:15244/@manage\",\"password\":\"adminpw\"}",
                _ => "{}"
            };
            return Task.FromResult(new CommandExecutionResult("test", command, "succeeded", true, json));
        }

        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}
