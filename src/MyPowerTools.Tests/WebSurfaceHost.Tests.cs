using System.Text;
using Avalonia.Controls;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.WebSurface.Avalonia;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed class WebSurfaceHostTests
{
    [Fact]
    public void Avalonia_context_keeps_its_existing_constructor_and_adds_an_optional_capability()
    {
        var constructor = Assert.Single(typeof(MptAvaloniaSurfaceContext).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Equal("SubscribeEvents", parameters[^1].Name, ignoreCase: true);
        Assert.DoesNotContain(parameters, parameter =>
            string.Equals(parameter.Name, "WebSurfaces", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(typeof(IMptWebSurfaceService),
            typeof(MptAvaloniaSurfaceContext).GetProperty(nameof(MptAvaloniaSurfaceContext.WebSurfaces))!.PropertyType);

        var context = new MptAvaloniaSurfaceContext(
            "example.tool",
            "main",
            Path.GetTempPath(),
            "system",
            null!,
            null!,
            null!,
            null!);
        Assert.Null(context.WebSurfaces);
    }

    [Fact]
    public void Service_defaults_the_bridge_allowlist_to_the_source_origin()
    {
        var hostPath = Path.Combine(Path.GetTempPath(), "missing-web-tool-host.exe");
        var request = WebSurfaceNavigationPolicy.Normalize(new MptWebSurfaceRequest(
            "example.tool",
            "main",
            new Uri("https://example.test:8443/panel/index.html"),
            []));
        var arguments = WebSurfaceControl.BuildStartInfo(
            request,
            hostPath,
            (nint)1234,
            4321).ArgumentList.ToArray();

        AssertArgumentValue(arguments, "--tool", "example.tool");
        AssertArgumentValue(arguments, "--parent-pid", "4321");
        AssertArgumentValue(arguments, "--source", "https://example.test:8443/panel/index.html");
        AssertArgumentValue(arguments, "--allowed-origin", "https://example.test:8443/");
    }

    [Fact]
    public void Explicit_origins_keep_the_source_origin_in_the_shared_allowlist()
    {
        var request = WebSurfaceNavigationPolicy.Normalize(new MptWebSurfaceRequest(
            "example.tool",
            "main",
            new Uri("https://panel.example.test/index.html"),
            [new Uri("https://api.example.test/v1/")]));

        Assert.Equal(
            ["https://panel.example.test/", "https://api.example.test/"],
            request.AllowedOrigins.Select(origin => origin.AbsoluteUri).ToArray());
    }

    [Theory]
    [InlineData("ftp://example.test/panel")]
    [InlineData("https://user:secret@example.test/panel")]
    [InlineData("relative/panel")]
    public void Service_rejects_sources_outside_the_web_surface_policy(string source)
    {
        var service = new AvaloniaWebSurfaceService(
            Path.Combine(Path.GetTempPath(), "missing-web-tool-host.exe"),
            new WebSurfaceOcclusionState());

        Assert.ThrowsAny<Exception>(() => service.CreateSession(new MptWebSurfaceRequest(
            "example.tool",
            "main",
            new Uri(source, UriKind.RelativeOrAbsolute),
            [])));
    }

    [Fact]
    public void Host_protocol_rejects_the_wrong_pid_and_accepts_a_bounded_state_frame()
    {
        const int expectedPid = 4217;
        var valid = $$"""{"type":"state","state":"ready","message":"ok","pid":{{expectedPid}},"protocolVersion":1}""";

        Assert.True(WebSurfaceControl.TryReadHostEvent(valid, expectedPid, out var hostEvent));
        Assert.Equal(MptWebSurfaceState.Ready, hostEvent.State);
        Assert.False(WebSurfaceControl.TryReadHostEvent(valid, expectedPid + 1, out _));
    }

    [Fact]
    public async Task Loading_timeout_accepts_only_the_latest_uncancelled_attempt()
    {
        var tracker = new WebSurfaceLoadingAttemptTracker();
        var staleAttempt = tracker.Begin();
        var currentAttempt = tracker.Begin();

        Assert.False(await tracker.WaitAsync(
            staleAttempt,
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None));
        Assert.True(await tracker.WaitAsync(
            currentAttempt,
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        var cancelledAttempt = tracker.Begin();
        cancellation.Cancel();
        Assert.False(await tracker.WaitAsync(
            cancelledAttempt,
            TimeSpan.FromSeconds(1),
            cancellation.Token));
    }

    [Theory]
    [InlineData("loading", MptWebSurfaceState.Loading)]
    [InlineData("ready", MptWebSurfaceState.Ready)]
    [InlineData("unavailable", MptWebSurfaceState.Unavailable)]
    [InlineData("failed", MptWebSurfaceState.Failed)]
    public void Host_protocol_maps_every_surface_lifecycle_state(
        string wireState,
        MptWebSurfaceState expectedState)
    {
        const int pid = 7521;
        var frame = $$"""{"type":"state","state":"{{wireState}}","message":"detail","pid":{{pid}},"protocolVersion":1}""";

        Assert.True(WebSurfaceControl.TryReadHostEvent(frame, pid, out var hostEvent));
        Assert.Equal(WebSurfaceControl.HostProcessEventKind.State, hostEvent.Kind);
        Assert.Equal(expectedState, hostEvent.State);
        Assert.Equal("detail", hostEvent.Message);
    }

    [Fact]
    public void Host_protocol_validates_shortcut_focus_and_bridge_events()
    {
        const int pid = 9142;

        Assert.True(WebSurfaceControl.TryReadHostEvent(
            $$"""{"type":"shortcut","gesture":"Ctrl+Shift+P","pid":{{pid}},"protocolVersion":1}""",
            pid,
            out var shortcut));
        Assert.Equal(WebSurfaceControl.HostProcessEventKind.Shortcut, shortcut.Kind);
        Assert.Equal("Ctrl+Shift+P", shortcut.Value);

        Assert.True(WebSurfaceControl.TryReadHostEvent(
            $$"""{"type":"focusMove","direction":"previous","pid":{{pid}},"protocolVersion":1}""",
            pid,
            out var focus));
        Assert.Equal(WebSurfaceControl.HostProcessEventKind.FocusMove, focus.Kind);
        Assert.Equal("previous", focus.Value);

        Assert.True(WebSurfaceControl.TryReadHostEvent(
            $$"""{"type":"bridgeRequest","payload":{"id":"req-1","type":"status"},"pid":{{pid}},"protocolVersion":1}""",
            pid,
            out var bridge));
        Assert.Equal(WebSurfaceControl.HostProcessEventKind.BridgeRequest, bridge.Kind);
        Assert.Contains("req-1", bridge.Value, StringComparison.Ordinal);

        Assert.False(WebSurfaceControl.TryReadHostEvent(
            $$"""{"type":"shortcut","gesture":"Ctrl+Shift+P","pid":{{pid}},"protocolVersion":2}""",
            pid,
            out _));
    }

    [Fact]
    public async Task Host_protocol_rejects_frames_larger_than_sixteen_kibibytes()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', WebSurfaceControl.MaximumHostFrameLength + 1) + "\n");
        await using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAllFramesAsync(stream));
    }

    [Fact]
    public void Occlusion_state_is_instance_scoped()
    {
        var first = new WebSurfaceOcclusionState();
        var second = new WebSurfaceOcclusionState();
        var notifications = 0;
        first.Changed += (_, _) => notifications++;

        first.SetOccluded(true);
        first.SetOccluded(true);

        Assert.True(first.IsOccluded);
        Assert.False(second.IsOccluded);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void External_tool_view_model_tracks_and_disposes_the_web_session()
    {
        var session = new FakeWebSurfaceSession();
        var viewModel = CreateExternalViewModel([]);

        viewModel.SetWebSurfaceSession(session);
        session.Raise(MptWebSurfaceState.Ready, "ready");

        Assert.True(viewModel.IsSurfaceReady);
        viewModel.Dispose();
        Assert.True(session.IsDisposed);
    }

    [Fact]
    public void Replacing_a_web_surface_session_disposes_the_previous_session()
    {
        var first = new FakeWebSurfaceSession();
        var second = new FakeWebSurfaceSession();
        using var viewModel = CreateExternalViewModel([]);

        viewModel.SetWebSurfaceSession(first);
        viewModel.SetWebSurfaceSession(second);
        first.Raise(MptWebSurfaceState.Ready, "stale");

        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        Assert.True(viewModel.IsSurfaceLoading);
    }

    [Fact]
    public async Task Shell_web_surface_request_preserves_manifest_source_origins_and_bridge()
    {
        var descriptor = new HostProto.ToolDescriptor
        {
            ToolId = "example.tool"
        };
        var route = new HostProto.ToolRoute
        {
            RouteId = "main"
        };
        route.AllowedOrigins.Add("https://api.example.test/v1/");
        var source = new Uri("https://panel.example.test/index.html");
        static Task<string> Bridge(string request, CancellationToken _) =>
            Task.FromResult("handled:" + request);

        var request = ShellWorkspaceController.CreateExternalWebSurfaceRequest(
            descriptor,
            route,
            source,
            Bridge);

        Assert.Equal("example.tool", request.ToolId);
        Assert.Equal("main", request.RouteId);
        Assert.Equal(source, request.Source);
        Assert.Equal(["https://api.example.test/v1/"],
            request.AllowedOrigins.Select(origin => origin.AbsoluteUri).ToArray());
        Assert.Equal("handled:payload",
            await request.HandleBridgeRequestAsync!("payload", CancellationToken.None));
    }

    [Fact]
    public async Task Refresh_reuses_the_existing_web_surface_session()
    {
        var session = new FakeWebSurfaceSession();
        using var viewModel = new ExternalSdkToolViewModel(
            "example.tool",
            "Example",
            "Example web surface",
            "web-surface",
            "Main",
            new Uri("https://example.test/"),
            true,
            [],
            null,
            request => Task.FromResult(request),
            () =>
            {
                session.Reload();
                return Task.CompletedTask;
            },
            () => Task.CompletedTask);
        viewModel.SetWebSurfaceSession(session);

        viewModel.RefreshCommand.Execute(null);
        await WaitUntilAsync(() => session.ReloadCount == 1);

        Assert.False(session.IsDisposed);
    }

    [Fact]
    public async Task Editable_web_title_and_address_update_the_open_page()
    {
        Uri? navigatedTo = null;
        string? renamedTo = null;
        using var viewModel = new ExternalSdkToolViewModel(
            "example.tool",
            "Example",
            "Example web surface",
            "web-surface",
            "Main",
            new Uri("https://example.test/"),
            true,
            [],
            null,
            request => Task.FromResult(request),
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            navigate: target =>
            {
                navigatedTo = target;
                return Task.CompletedTask;
            },
            titleChanged: title => renamedTo = title);

        viewModel.EditableTitle = "Operations";
        viewModel.Address = "status.example.test/health";
        viewModel.NavigateCommand.Execute(null);
        await WaitUntilAsync(() => navigatedTo is not null);

        Assert.Equal("Operations", viewModel.Title);
        Assert.Equal("Operations", renamedTo);
        Assert.Equal("https://status.example.test/health", navigatedTo!.AbsoluteUri);
        Assert.Equal(navigatedTo.AbsoluteUri, viewModel.Address);
        Assert.False(viewModel.HasAddressError);
    }

    [Fact]
    public async Task Open_web_tool_navigation_item_exposes_close_and_edited_title()
    {
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chrome = new ShellChromeViewModel(["Home", "Tools", "Settings", "System"]);
        var tool = new ToolCardViewModel(
            "example.tool",
            "Example",
            "Example web surface",
            "Web",
            "EX",
            "Ready",
            "Ready",
            ToolAvailability.Available,
            false,
            isWebSurface: true);

        chrome.SetDiscoveredTools(
            [tool],
            _ => Task.CompletedTask,
            toolId =>
            {
                closed.TrySetResult(toolId);
                return Task.CompletedTask;
            },
            _ => true,
            _ => "Operations");
        var item = Assert.Single(chrome.ToolNavigationItems.Where(candidate => candidate.CanClose));

        Assert.True(item.IsCloseButtonVisible);
        Assert.Equal("Operations", item.DisplayLabel);
        item.CloseCommand!.Execute(null);
        Assert.Equal("example.tool", await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        chrome.SetWebToolOpenState("example.tool", false);
        Assert.False(item.IsCloseButtonVisible);
    }

    [Fact]
    public async Task Command_failure_keeps_a_ready_surface_and_bounds_the_command_output()
    {
        var command = new ExternalToolCommandViewModel(
            "Tail logs",
            "Read logs",
            () => Task.FromException<string>(new HttpRequestException(new string('x', 70 * 1024))));
        using var viewModel = CreateExternalViewModel([command]);
        viewModel.ReportSurface("ready");

        command.ExecuteCommand.Execute(null);
        await WaitUntilAsync(() => command.State == "failed");

        Assert.True(viewModel.IsSurfaceReady);
        Assert.Equal("failed", command.State);
        Assert.True(command.Output.Length <= 64 * 1024 + 1);
    }

    private static ExternalSdkToolViewModel CreateExternalViewModel(
        IReadOnlyList<ExternalToolCommandViewModel> commands)
    {
        return new ExternalSdkToolViewModel(
            "example.tool",
            "Example",
            "Example web surface",
            "web-surface",
            "Main",
            new Uri("https://example.test/"),
            true,
            commands,
            null,
            request => Task.FromResult(request),
            () => Task.CompletedTask,
            () => Task.CompletedTask);
    }

    private static void AssertArgumentValue(string[] arguments, string name, string expected)
    {
        var index = Array.IndexOf(arguments, name);
        Assert.InRange(index, 0, arguments.Length - 2);
        Assert.Equal(expected, arguments[index + 1]);
    }

    private static async Task ReadAllFramesAsync(Stream stream)
    {
        await foreach (var _ in WebSurfaceControl.ReadBoundedFramesAsync(stream, CancellationToken.None))
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(predicate());
    }

    private sealed class FakeWebSurfaceSession : IMptWebSurfaceSession, IPersistentWebSurfaceSession
    {
        public Control View => null!;
        public MptWebSurfaceState State { get; private set; } = MptWebSurfaceState.Loading;
        public bool IsDisposed { get; private set; }
        public int ReloadCount { get; private set; }
        public Uri? NavigatedTo { get; private set; }
        public bool IsActive { get; private set; } = true;
        public event EventHandler<MptWebSurfaceStateChangedEventArgs>? StateChanged;

        public void Navigate(Uri source) => NavigatedTo = source;

        public void Reload()
        {
            ReloadCount++;
        }

        public void SetActive(bool active) => IsActive = active;

        public void Raise(MptWebSurfaceState state, string message)
        {
            State = state;
            StateChanged?.Invoke(this, new MptWebSurfaceStateChangedEventArgs(state, message));
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
