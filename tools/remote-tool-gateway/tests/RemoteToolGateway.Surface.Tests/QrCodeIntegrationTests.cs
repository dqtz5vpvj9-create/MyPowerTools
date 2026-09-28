using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.AvaloniaSdk.Controls;
using MyPowerTools.Platform.Abstractions;
using RemoteToolGateway.Core;
using RemoteToolGateway.Surface;
using ZXing;
using FormatException = System.FormatException;

namespace RemoteToolGateway.Surface.Tests;

/// <summary>
/// Headless entry point for the rendered-pixel test: real Avalonia rendering (Skia) instead of the
/// blank headless drawing surface, so the QR symbol the page produces can be decoded from actual
/// pixels the way a phone camera would see it.
/// </summary>
internal static class QrHeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}

/// <summary>
/// The connection code is a credential: the page shows it (as a scannable QR plus a copy fallback)
/// only after the user explicitly creates or reveals it, and drops it on hide, detach, revocation or
/// when the grant disappears. These tests drive the real view inside a headless window.
/// </summary>
public sealed class QrCodeIntegrationTests
{
    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(QrHeadlessApp));

    [Fact]
    public async Task RevealedConnectionCode_RendersADecodableQrSymbol_ThroughTheActualView()
    {
        var decoded = await Session.Dispatch(async () =>
        {
            var harness = await QrHarness.CreateAsync();
            var view = new RemoteToolGatewayView(harness.Context);
            var window = new Window { Width = 1200, Height = 2200, Content = view };
            try
            {
                window.Show();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                // Nothing is revealed before the user asks for it: the symbol is neither visible
                // nor encoding anything.
                var hidden = view.GetVisualDescendants().OfType<MptQrCode>().Single();
                Assert.False(hidden.IsEffectivelyVisible);
                Assert.True(string.IsNullOrEmpty(hidden.Value));

                view.ViewModel.NewDeviceName = "扫码测试手机";
                view.ViewModel.SelectCommands(["demo.status"]);
                await view.ViewModel.CreateGrantAsync();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                var code = view.ViewModel.CreatedCode;
                Assert.StartsWith("mpt://control/", code);
                Assert.True(view.ViewModel.HasCreatedCode);
                Assert.Contains("扫码测试手机", view.ViewModel.CreatedCodeContext);

                var qr = view.GetVisualDescendants().OfType<MptQrCode>().Single();
                // The control encodes the full code verbatim, and the page never shortens it.
                Assert.True(qr.IsEffectivelyVisible);
                Assert.Equal(code, qr.Value);

                return (Code: code, GrantId: view.ViewModel.Grants.Single().GrantId, Decoded: DecodeQr(window, qr));
            }
            finally
            {
                window.Close();
                await harness.DisposeAsync();
            }
        }, CancellationToken.None);

        // The pixels decode back to the exact string the page displayed, token included.
        Assert.NotNull(decoded.Decoded);
        Assert.StartsWith("mpt://control/", decoded.Decoded);
        Assert.Equal(decoded.Code, decoded.Decoded);

        // The payload is the real connection code for the synthetic grant. The strict Tailnet
        // validator cannot run here (the test transport is loopback), so the fields are checked
        // directly; ConnectionCode tests already cover the address policy.
        var payload = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(
            Base64Url.Decode(decoded.Decoded["mpt://control/".Length..])))!.AsObject();
        Assert.Equal("扫码测试手机", payload["deviceName"]!.GetValue<string>());
        Assert.Equal(decoded.GrantId, payload["grantId"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(payload["token"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ConnectionCode_IsClearedOnHideDetachRevokeAndGrantDisappearance()
    {
        var harness = await QrHarness.CreateAsync();
        try
        {
            var viewModel = harness.Host;
            await viewModel.LoadAsync();
            Assert.False(viewModel.HasCreatedCode);

            viewModel.NewDeviceName = "手机 A";
            viewModel.SelectCommands(["demo.status"]);
            await viewModel.CreateGrantAsync();
            Assert.True(viewModel.HasCreatedCode);
            var grantId = viewModel.Grants.Single().GrantId;

            // Hide: the string is dropped, not merely hidden.
            viewModel.ClearCreatedCode();
            Assert.False(viewModel.HasCreatedCode);
            Assert.Equal("", viewModel.CreatedCode);
            Assert.Equal("", viewModel.CreatedCodeContext);

            // Detach (leaving the page) clears it too.
            viewModel.Attach(_ => new NoopDisposable());
            await viewModel.ShowCodeAsync(grantId);
            Assert.True(viewModel.HasCreatedCode);
            viewModel.Detach();
            Assert.False(viewModel.HasCreatedCode);

            // Revocation clears the code that belonged to that grant.
            await viewModel.ShowCodeAsync(grantId);
            Assert.True(viewModel.HasCreatedCode);
            await viewModel.RevokeAsync(grantId);
            Assert.False(viewModel.HasCreatedCode);

            // A code whose grant disappeared elsewhere is dropped on the next refresh as well.
            viewModel.NewDeviceName = "手机 B";
            await viewModel.LoadAsync();
            viewModel.SelectCommands(["demo.status"]);
            await viewModel.CreateGrantAsync();
            var secondGrant = viewModel.Grants.Single(row => row.DeviceName == "手机 B").GrantId;
            await viewModel.ShowCodeAsync(secondGrant);
            Assert.True(viewModel.HasCreatedCode);
            await harness.Service.RevokeGrantAsync(secondGrant, CancellationToken.None);
            await viewModel.LoadAsync();
            Assert.False(viewModel.HasCreatedCode);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectionCode_IsNotExposedToAutomationOrLogs()
    {
        await Session.Dispatch(async () =>
        {
            var harness = await QrHarness.CreateAsync();
            var view = new RemoteToolGatewayView(harness.Context);
            var window = new Window { Width = 1200, Height = 2200, Content = view };
            try
            {
                window.Show();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                view.ViewModel.NewDeviceName = "隐私测试手机";
                view.ViewModel.SelectCommands(["demo.status"]);
                await view.ViewModel.CreateGrantAsync();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();

                var code = view.ViewModel.CreatedCode;
                Assert.NotEmpty(code);
                var qr = view.GetVisualDescendants().OfType<MptQrCode>().Single();

                // A credential never becomes an automation name, help text or window title.
                Assert.DoesNotContain(code, AutomationProperties.GetName(qr) ?? "");
                Assert.DoesNotContain(code, AutomationProperties.GetHelpText(qr) ?? "");
                Assert.DoesNotContain(code, window.Title ?? "");
                // The status line and the surface log stay code-free as well.
                Assert.DoesNotContain(code, view.ViewModel.Status);
                Assert.DoesNotContain(code, string.Join("\n", harness.LogEntries));
            }
            finally
            {
                window.Close();
                await harness.DisposeAsync();
            }
        }, CancellationToken.None);
    }

    /// <summary>Captures the rendered frame and decodes the QR symbol's own pixels.</summary>
    private static string? DecodeQr(Window window, MptQrCode qr)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var locked = frame!.Lock();
        var width = locked.Size.Width;
        var height = locked.Size.Height;
        var pixels = new byte[locked.RowBytes * height];
        Marshal.Copy(locked.Address, pixels, 0, pixels.Length);

        // The control draws its required quiet zone inside its own bounds, so cropping to those
        // bounds keeps the symbol intact and removes the surrounding page.
        var origin = qr.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);
        var x = Math.Max(0, (int)origin.X);
        var y = Math.Max(0, (int)origin.Y);
        var cropWidth = Math.Min((int)Math.Ceiling(qr.Bounds.Width), width - x);
        var cropHeight = Math.Min((int)Math.Ceiling(qr.Bounds.Height), height - y);
        Assert.True(cropWidth > 0 && cropHeight > 0, $"QR symbol is not inside the rendered frame ({origin}, {qr.Bounds}).");

        var tight = new byte[cropWidth * cropHeight * 4];
        for (var row = 0; row < cropHeight; row++)
        {
            Array.Copy(pixels, ((y + row) * locked.RowBytes) + (x * 4), tight, row * cropWidth * 4, cropWidth * 4);
        }

        var format = locked.Format == PixelFormats.Rgba8888
            ? RGBLuminanceSource.BitmapFormat.RGBA32
            : RGBLuminanceSource.BitmapFormat.BGRA32;
        var reader = new BarcodeReaderGeneric();
        return reader.Decode(tight, cropWidth, cropHeight, format)?.Text;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>A real gateway service behind a real surface context, with only the runtime faked.</summary>
    private sealed class QrHarness : IAsyncDisposable
    {
        private QrHarness(
            string directory,
            RemoteToolGatewayService service,
            MptAvaloniaSurfaceContext context,
            ControlSurfaceViewModel host,
            List<string> logEntries)
        {
            Directory = directory;
            Service = service;
            Context = context;
            Host = host;
            LogEntries = logEntries;
        }

        public string Directory { get; }
        public RemoteToolGatewayService Service { get; }
        public MptAvaloniaSurfaceContext Context { get; }
        public ControlSurfaceViewModel Host { get; }
        public List<string> LogEntries { get; }

        public static async Task<QrHarness> CreateAsync()
        {
            var directory = NewDirectory();
            var bridge = new QrBridge();
            var service = new RemoteToolGatewayService(directory, new InMemorySecretStore(), bridge,
                new RemoteToolGatewayOptions
                {
                    ModuleId = "remote-tool-gateway",
                    DeviceName = "测试电脑",
                    Platform = "linux",
                    AllowLoopbackTransport = true
                });
            await service.InitializeAsync(CancellationToken.None);
            await service.StartListenerAsync("127.0.0.1", 0, CancellationToken.None);

            var logEntries = new List<string>();
            var context = new MptAvaloniaSurfaceContext(
                "remote-tool-gateway",
                "main",
                directory,
                "light",
                async (commandId, args, cancellationToken) =>
                {
                    var payload = await ExecuteModuleCommandAsync(service, commandId, args ?? new JsonObject(), cancellationToken);
                    return new CommandExecutionResult(Guid.NewGuid().ToString("N"), commandId, "succeeded", true, payload.ToJsonString());
                },
                (_, _, _) => Task.CompletedTask,
                null!,
                entry => logEntries.Add(entry.Message + " " + entry.Properties?.ToJsonString()))
            {
                ExecuteCommandWithInvocationAsync = (invocationId, commandId, args, _) =>
                    Task.FromResult(new CommandExecutionResult(invocationId, commandId, "succeeded", true, "{}"))
            };
            return new QrHarness(directory, service, context, new ControlSurfaceViewModel(new MptAvaloniaSurfaceHost(context)), logEntries);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            TryDelete(Directory);
        }

        private static async Task<JsonObject> ExecuteModuleCommandAsync(
            RemoteToolGatewayService service,
            string commandId,
            JsonObject args,
            CancellationToken cancellationToken) => commandId switch
        {
            "remote-tool-gateway.inspect" => await service.DescribeAsync(listenerEnabled: true, includeCatalog: true, cancellationToken),
            "remote-tool-gateway.grant.create" => await service.CreateGrantAsync(
                ReadString(args, "deviceName"),
                ReadStrings(args, "commandIds"),
                ReadBool(args, "allowElevated"),
                cancellationToken),
            "remote-tool-gateway.grant.code" => await service.GetGrantCodeAsync(ReadString(args, "grantId"), cancellationToken),
            "remote-tool-gateway.grant.revoke" => await service.RevokeGrantAsync(ReadString(args, "grantId"), cancellationToken),
            _ => throw new NotSupportedException(commandId)
        };

        private static string ReadString(JsonObject json, string key)
        {
            try { return json[key]?.GetValue<string>() ?? ""; }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return ""; }
        }

        private static bool ReadBool(JsonObject json, string key)
        {
            try { return json[key]?.GetValue<bool>() ?? false; }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return false; }
        }

        private static IReadOnlyList<string> ReadStrings(JsonObject json, string key) =>
            (json[key] as JsonArray)?.Select(node => node?.GetValue<string>() ?? "").Where(value => value.Length > 0).ToArray() ?? [];
    }

    private sealed class QrBridge : IHostControlBridge
    {
        public Task<HostCatalog> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostCatalog([], [new HostCommandDescriptor(
                "demo.status", "input-monitor", "查看状态", "input-monitor", "", false, false, true, [], [], null)]));

        public async IAsyncEnumerable<HostExecutionEvent> ExecuteStreamAsync(
            string invocationId,
            string commandId,
            JsonObject args,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new HostExecutionEvent(invocationId, commandId, "succeeded", "完成。", true,
                new ControlInvocationResult(invocationId, "succeeded", "完成。", "", "", "", false, null));
        }

        public Task<HostCancellation> CancelAsync(string invocationId, CancellationToken cancellationToken) =>
            Task.FromResult(new HostCancellation(false, invocationId, "not-found", "没有该调用。"));

        public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static string NewDirectory()
    {
        var root = Environment.GetEnvironmentVariable("MPT_RTG_TEST_TMP");
        if (string.IsNullOrWhiteSpace(root))
        {
            const string cache = "/mnt/cache/data-cache/mpt-remote-tool-gateway-tests";
            root = IsWritable(cache)
                ? cache
                : Path.Combine(RepositoryRoot(), "artifacts", ".tmp-android-verify", "rtg-tests");
        }

        var directory = Path.Combine(root, "qr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "MyPowerTools.slnx"))) return directory;
            directory = Path.GetDirectoryName(directory);
        }

        return Directory.GetCurrentDirectory();
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
