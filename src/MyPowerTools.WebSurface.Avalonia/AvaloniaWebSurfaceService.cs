using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.WebSurface.Avalonia;

/// <summary>
/// Shell-side implementation of the Avalonia SDK web-surface host capability.
/// </summary>
public sealed class AvaloniaWebSurfaceService : IMptWebSurfaceService
{
    private static readonly TimeSpan DefaultLoadingTimeout = TimeSpan.FromSeconds(12);
    private readonly string _hostExecutablePath;
    private readonly WebSurfaceOcclusionState _occlusionState;
    private readonly Func<string, Task> _forwardShortcutAsync;
    private readonly TimeSpan _loadingTimeout;

    public AvaloniaWebSurfaceService(
        string hostExecutablePath,
        WebSurfaceOcclusionState occlusionState,
        Func<string, Task>? forwardShortcutAsync = null,
        TimeSpan? loadingTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostExecutablePath);
        _hostExecutablePath = Path.GetFullPath(hostExecutablePath);
        _occlusionState = occlusionState ?? throw new ArgumentNullException(nameof(occlusionState));
        _forwardShortcutAsync = forwardShortcutAsync ?? (_ => Task.CompletedTask);
        _loadingTimeout = loadingTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : DefaultLoadingTimeout;
    }

    public IMptWebSurfaceSession CreateSession(MptWebSurfaceRequest request)
    {
        var normalized = WebSurfaceNavigationPolicy.Normalize(request);
        return new AvaloniaWebSurfaceSession(new WebSurfaceControl(
            normalized,
            _hostExecutablePath,
            _occlusionState,
            _forwardShortcutAsync,
            _loadingTimeout));
    }

    public static string ResolveDefaultHostPath(string applicationBaseDirectory)
    {
        return Path.Combine(
            Path.GetFullPath(applicationBaseDirectory),
            "WebToolHost",
            "MyPowerTools.WebToolHost.exe");
    }
}

internal static class WebSurfaceNavigationPolicy
{
    public static MptWebSurfaceRequest Normalize(MptWebSurfaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ToolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RouteId);
        if (!IsSupportedWebUri(request.Source))
        {
            throw new ArgumentException("Web surface source must be an absolute HTTP, HTTPS, or file URI without credentials.", nameof(request));
        }

        var sourceOrigin = NormalizeOrigin(request.Source);
        var origins = (request.AllowedOrigins ?? [])
            .Select(NormalizeOrigin)
            .DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (origins.Length == 0)
        {
            origins = [sourceOrigin];
        }
        else if (!origins.Any(origin =>
                     string.Equals(origin.AbsoluteUri, sourceOrigin.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
        {
            origins = [sourceOrigin, .. origins];
        }

        return request with { AllowedOrigins = origins };
    }

    public static bool IsSupportedWebUri(Uri? uri)
    {
        return uri is { IsAbsoluteUri: true } &&
               (uri.IsFile || uri.Scheme is "http" or "https") &&
               string.IsNullOrEmpty(uri.UserInfo);
    }

    public static Uri NormalizeOrigin(Uri uri)
    {
        if (!IsSupportedWebUri(uri))
        {
            throw new ArgumentException("Allowed web origins must use HTTP, HTTPS, or file without credentials.", nameof(uri));
        }

        if (uri.IsFile)
        {
            var path = Directory.Exists(uri.LocalPath)
                ? Path.GetFullPath(uri.LocalPath)
                : Path.GetDirectoryName(Path.GetFullPath(uri.LocalPath))
                  ?? throw new ArgumentException("File origin has no parent directory.", nameof(uri));
            return new Uri(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
        }

        return new UriBuilder(uri.Scheme, uri.Host, uri.Port, "/").Uri;
    }
}

internal sealed class WebSurfaceLoadingAttemptTracker
{
    private int _version;

    public int Begin() => Interlocked.Increment(ref _version);

    public void Invalidate() => Interlocked.Increment(ref _version);

    public bool IsCurrent(int version) => version == Volatile.Read(ref _version);

    public async Task<bool> WaitAsync(
        int version,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
            return IsCurrent(version);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

internal sealed class AvaloniaWebSurfaceSession : IMptWebSurfaceSession, IPersistentWebSurfaceSession
{
    private readonly WebSurfaceControl _control;
    private int _disposed;

    public AvaloniaWebSurfaceSession(WebSurfaceControl control)
    {
        _control = control;
        State = control.CurrentState;
        _control.StateChanged += OnStateChanged;
    }

    public Control View => _control;
    public MptWebSurfaceState State { get; private set; }
    public event EventHandler<MptWebSurfaceStateChangedEventArgs>? StateChanged;

    public void Navigate(Uri source)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _control.Navigate(source);
    }

    public void Reload()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _control.Reload();
    }

    public void SetActive(bool active)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _control.SetActive(active);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _control.StateChanged -= OnStateChanged;
        _control.Dispose();
        StateChanged = null;
    }

    private void OnStateChanged(object? sender, MptWebSurfaceStateChangedEventArgs eventArguments)
    {
        State = eventArguments.State;
        StateChanged?.Invoke(this, eventArguments);
    }
}

internal sealed class WebSurfaceControl : Control, IDisposable
{
    internal const int MaximumHostFrameLength = 16 * 1024;

    private readonly MptWebSurfaceRequest _request;
    private readonly string _hostExecutablePath;
    private readonly WebSurfaceOcclusionState _occlusionState;
    private readonly Func<string, Task> _forwardShortcutAsync;
    private readonly TimeSpan _loadingTimeout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly WebSurfaceLoadingAttemptTracker _loadingAttempts = new();
    private Process? _hostProcess;
    private StreamWriter? _hostInput;
    private CancellationTokenSource? _hostCancellation;
    private TopLevel? _topLevel;
    private HostBounds? _lastBounds;
    private int _generation;
    private int _currentState = (int)MptWebSurfaceState.Loading;
    private int _disposed;
    private bool _attached;
    private bool _active = true;
    private bool _terminalStateReported;

    public WebSurfaceControl(
        MptWebSurfaceRequest request,
        string hostExecutablePath,
        WebSurfaceOcclusionState occlusionState,
        Func<string, Task> forwardShortcutAsync,
        TimeSpan loadingTimeout)
    {
        _request = request;
        _hostExecutablePath = hostExecutablePath;
        _occlusionState = occlusionState;
        _forwardShortcutAsync = forwardShortcutAsync;
        _loadingTimeout = loadingTimeout;
        Focusable = true;
        LayoutUpdated += OnLayoutUpdated;
    }

    public event EventHandler<MptWebSurfaceStateChangedEventArgs>? StateChanged;

    public MptWebSurfaceState CurrentState => (MptWebSurfaceState)Volatile.Read(ref _currentState);

    public void Reload()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        if (!_attached || !IsVisible)
        {
            NotifyState(MptWebSurfaceState.Loading, "The web surface will reload when it becomes visible.");
            return;
        }
        if (_hostProcess is { HasExited: false } process)
        {
            var generation = _generation;
            var loadingAttempt = _loadingAttempts.Begin();
            NotifyState(MptWebSurfaceState.Loading);
            _ = SendCommandAsync(new { type = "reload" }, generation);
            _ = WatchHostLoadingAsync(
                process,
                generation,
                loadingAttempt,
                _hostCancellation?.Token ?? CancellationToken.None);
            return;
        }

        StartHost();
    }

    public void Navigate(Uri source)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !WebSurfaceNavigationPolicy.IsSupportedWebUri(source))
        {
            return;
        }
        if (_hostProcess is not { HasExited: false } process)
        {
            StartHost();
            return;
        }

        var generation = _generation;
        var loadingAttempt = _loadingAttempts.Begin();
        NotifyState(MptWebSurfaceState.Loading);
        _ = SendCommandAsync(new { type = "navigate", source = source.AbsoluteUri }, generation);
        _ = WatchHostLoadingAsync(
            process,
            generation,
            loadingAttempt,
            _hostCancellation?.Token ?? CancellationToken.None);
    }

    public void SetActive(bool active)
    {
        if (Volatile.Read(ref _disposed) != 0 || _active == active)
        {
            return;
        }

        _active = active;
        if (active)
        {
            StartHostIfEligible();
        }
        else
        {
            UpdateHostBounds();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        LayoutUpdated -= OnLayoutUpdated;
        if (_attached)
        {
            _occlusionState.Changed -= OnOcclusionChanged;
        }
        _attached = false;
        _topLevel = null;
        StopHost();
        StateChanged = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArguments)
    {
        base.OnAttachedToVisualTree(eventArguments);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        _attached = true;
        _topLevel = TopLevel.GetTopLevel(this);
        _occlusionState.Changed += OnOcclusionChanged;
        StartHostIfEligible();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArguments)
    {
        if (_attached)
        {
            _occlusionState.Changed -= OnOcclusionChanged;
            _ = SendCommandAsync(new
            {
                type = "bounds",
                x = 0,
                y = 0,
                width = 1,
                height = 1,
                clipX = 0,
                clipY = 0,
                clipWidth = 0,
                clipHeight = 0,
                visible = false
            }, _generation);
        }
        _attached = false;
        _topLevel = null;
        base.OnDetachedFromVisualTree(eventArguments);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        if (IsVisible)
        {
            StartHostIfEligible();
        }
        else
        {
            UpdateHostBounds();
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs eventArguments)
    {
        base.OnGotFocus(eventArguments);
        var direction = eventArguments.NavigationMethod == NavigationMethod.Tab
            ? eventArguments.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "previous" : "next"
            : "programmatic";
        _ = SendCommandAsync(new { type = "focus", direction }, _generation);
    }

    private void OnLayoutUpdated(object? sender, EventArgs eventArguments) => UpdateHostBounds();

    private void OnOcclusionChanged(object? sender, EventArgs eventArguments) => UpdateHostBounds();

    private void StartHostIfEligible()
    {
        if (!_attached || !_active || !IsVisible || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        if (_hostProcess is { HasExited: false })
        {
            UpdateHostBounds();
            return;
        }
        StartHost();
    }

    private void StartHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            NotifyState(MptWebSurfaceState.Unavailable, "Embedded web surfaces require Windows WebView2. Open this tool in the system browser.");
            return;
        }

        _topLevel ??= TopLevel.GetTopLevel(this);
        var platformHandle = _topLevel?.TryGetPlatformHandle();
        if (platformHandle is null ||
            !string.Equals(platformHandle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase) ||
            platformHandle.Handle == 0)
        {
            NotifyState(MptWebSurfaceState.Unavailable, "The Shell window handle is unavailable. Open this tool in the system browser.");
            return;
        }
        if (!File.Exists(_hostExecutablePath))
        {
            NotifyState(MptWebSurfaceState.Unavailable, "MyPowerTools WebToolHost is not installed. Open this tool in the system browser.");
            return;
        }

        StopHost();
        var generation = ++_generation;
        var cancellation = new CancellationTokenSource();
        var startInfo = BuildStartInfo(platformHandle.Handle);

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                cancellation.Dispose();
                NotifyState(MptWebSurfaceState.Failed, "MyPowerTools WebToolHost could not be started.");
                return;
            }

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.UIThread.Post(() => HandleHostExit(process, generation));
            _hostProcess = process;
            _hostInput = process.StandardInput;
            _hostCancellation = cancellation;
            _terminalStateReported = false;
            _lastBounds = null;
            var loadingAttempt = _loadingAttempts.Begin();
            NotifyState(MptWebSurfaceState.Loading);
            _ = ReadHostEventsAsync(process, generation, cancellation.Token);
            _ = DrainHostErrorsAsync(process, cancellation.Token);
            _ = WatchHostLoadingAsync(process, generation, loadingAttempt, cancellation.Token);
            UpdateHostBounds();
        }
        catch (Exception ex)
        {
            cancellation.Dispose();
            NotifyState(MptWebSurfaceState.Failed, FriendlyHostError(ex));
        }
    }

    private ProcessStartInfo BuildStartInfo(nint parentHandle) =>
        BuildStartInfo(_request, _hostExecutablePath, parentHandle, Environment.ProcessId);

    internal static ProcessStartInfo BuildStartInfo(
        MptWebSurfaceRequest request,
        string hostExecutablePath,
        nint parentHandle,
        int parentProcessId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = hostExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(hostExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--tool");
        startInfo.ArgumentList.Add(request.ToolId);
        startInfo.ArgumentList.Add("--parent-hwnd");
        startInfo.ArgumentList.Add(parentHandle.ToInt64().ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(parentProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add(request.Source.AbsoluteUri);
        foreach (var origin in request.AllowedOrigins)
        {
            startInfo.ArgumentList.Add("--allowed-origin");
            startInfo.ArgumentList.Add(origin.AbsoluteUri);
        }
        return startInfo;
    }

    private async Task WatchHostLoadingAsync(
        Process process,
        int generation,
        int loadingAttempt,
        CancellationToken cancellationToken)
    {
        if (await _loadingAttempts.WaitAsync(
                loadingAttempt,
                _loadingTimeout,
                cancellationToken).ConfigureAwait(false) &&
            generation == _generation &&
            ReferenceEquals(process, _hostProcess) &&
            !process.HasExited &&
            CurrentState == MptWebSurfaceState.Loading)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _generation &&
                    ReferenceEquals(process, _hostProcess) &&
                    _loadingAttempts.IsCurrent(loadingAttempt))
                {
                    _terminalStateReported = true;
                    NotifyState(MptWebSurfaceState.Failed, "Web tool loading timed out. Check the configured URL or open it in the system browser.");
                    StopHost();
                }
            });
        }
    }

    private async Task ReadHostEventsAsync(Process process, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in ReadBoundedFramesAsync(process.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false))
            {
                if (!TryReadHostEvent(line, process.Id, out var hostEvent))
                {
                    Dispatcher.UIThread.Post(() => HandleInvalidHostProtocol(process, generation));
                    return;
                }
                Dispatcher.UIThread.Post(() => HandleHostEvent(process, generation, hostEvent));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidDataException)
        {
            Dispatcher.UIThread.Post(() => HandleInvalidHostProtocol(process, generation));
        }
    }

    private static async Task DrainHostErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[4096];
            while (await process.StandardError.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void HandleHostEvent(Process process, int generation, HostProcessEvent hostEvent)
    {
        if (generation != _generation || !ReferenceEquals(process, _hostProcess))
        {
            return;
        }
        switch (hostEvent.Kind)
        {
            case HostProcessEventKind.Shortcut:
                _ = ForwardShortcutSafelyAsync(hostEvent.Value);
                return;
            case HostProcessEventKind.FocusMove:
                MoveFocusBackToShell(hostEvent.Value);
                return;
            case HostProcessEventKind.BridgeRequest:
                _ = HandleBridgeRequestSafelyAsync(hostEvent.Value, generation, _hostCancellation?.Token ?? CancellationToken.None);
                return;
        }
        if (hostEvent.State is MptWebSurfaceState.Failed or MptWebSurfaceState.Unavailable)
        {
            _terminalStateReported = true;
        }
        NotifyState(hostEvent.State, hostEvent.Message);
    }

    private async Task ForwardShortcutSafelyAsync(string gesture)
    {
        try
        {
            await _forwardShortcutAsync(gesture).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task HandleBridgeRequestSafelyAsync(string requestJson, int generation, CancellationToken cancellationToken)
    {
        if (_request.HandleBridgeRequestAsync is null)
        {
            return;
        }
        string response;
        try
        {
            response = await _request.HandleBridgeRequestAsync(requestJson, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            response = CreateBridgeFailure(requestJson, ex.GetBaseException().Message);
        }
        try
        {
            var payload = JsonNode.Parse(response) ?? throw new InvalidDataException("Bridge response is empty.");
            await SendCommandAsync(new { type = "bridge-response", payload }, generation).ConfigureAwait(false);
        }
        catch (JsonException)
        {
        }
    }

    private static string CreateBridgeFailure(string requestJson, string message)
    {
        var id = "";
        var type = "bridge";
        try
        {
            using var request = JsonDocument.Parse(requestJson);
            id = request.RootElement.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? "" : "";
            type = request.RootElement.TryGetProperty("type", out var typeNode) ? typeNode.GetString() ?? type : type;
        }
        catch (JsonException)
        {
        }
        return new JsonObject
        {
            ["version"] = "1.0",
            ["id"] = id,
            ["type"] = type + ".result",
            ["error"] = new JsonObject
            {
                ["code"] = "bridge.request.failed",
                ["message"] = message.Length <= 1024 ? message : message[..1024]
            }
        }.ToJsonString();
    }

    private void HandleInvalidHostProtocol(Process process, int generation)
    {
        if (generation != _generation || !ReferenceEquals(process, _hostProcess))
        {
            return;
        }
        _terminalStateReported = true;
        NotifyState(MptWebSurfaceState.Failed, "WebToolHost sent an invalid or oversized protocol frame.");
        StopHost();
    }

    private void MoveFocusBackToShell(string direction)
    {
        var navigationDirection = string.Equals(direction, "previous", StringComparison.Ordinal)
            ? NavigationDirection.Previous
            : NavigationDirection.Next;
        _topLevel?.FocusManager?.TryMoveFocus(
            navigationDirection,
            new FindNextElementOptions { FocusedElement = this });
    }

    private void HandleHostExit(Process process, int generation)
    {
        if (generation != _generation || !ReferenceEquals(process, _hostProcess))
        {
            return;
        }

        _hostProcess = null;
        _hostInput = null;
        _hostCancellation?.Cancel();
        _hostCancellation?.Dispose();
        _hostCancellation = null;
        _lastBounds = null;
        process.Dispose();
        if (_attached && IsVisible && !_terminalStateReported && Volatile.Read(ref _disposed) == 0)
        {
            NotifyState(MptWebSurfaceState.Failed, "WebToolHost exited unexpectedly. Retry or open the tool in the system browser.");
        }
    }

    private void UpdateHostBounds()
    {
        if (!_attached || _hostProcess is not { HasExited: false } || _topLevel is null)
        {
            return;
        }
        var origin = this.TranslatePoint(new Point(0, 0), _topLevel);
        if (origin is null)
        {
            return;
        }

        var controlRect = new Rect(origin.Value, Bounds.Size);
        var visibleRect = Intersect(controlRect, new Rect(0, 0, _topLevel.ClientSize.Width, _topLevel.ClientSize.Height));
        var ancestor = this.GetVisualParent();
        while (ancestor is not null && !ReferenceEquals(ancestor, _topLevel))
        {
            if (ancestor.ClipToBounds && ancestor.TranslatePoint(new Point(0, 0), _topLevel) is { } ancestorOrigin)
            {
                visibleRect = Intersect(visibleRect, new Rect(ancestorOrigin, ancestor.Bounds.Size));
            }
            if (ancestor.Clip is { } clip && ancestor.TranslatePoint(clip.Bounds.Position, _topLevel) is { } clipOrigin)
            {
                visibleRect = Intersect(visibleRect, new Rect(clipOrigin, clip.Bounds.Size));
            }
            ancestor = ancestor.GetVisualParent();
        }

        var scaling = _topLevel.RenderScaling;
        var bounds = new HostBounds(
            X: (int)Math.Round(controlRect.X * scaling),
            Y: (int)Math.Round(controlRect.Y * scaling),
            Width: Math.Max(1, (int)Math.Round(controlRect.Width * scaling)),
            Height: Math.Max(1, (int)Math.Round(controlRect.Height * scaling)),
            ClipX: Math.Max(0, (int)Math.Round((visibleRect.X - controlRect.X) * scaling)),
            ClipY: Math.Max(0, (int)Math.Round((visibleRect.Y - controlRect.Y) * scaling)),
            ClipWidth: Math.Max(0, (int)Math.Round(visibleRect.Width * scaling)),
            ClipHeight: Math.Max(0, (int)Math.Round(visibleRect.Height * scaling)),
            Visible: _active && IsVisible && !_occlusionState.IsOccluded && visibleRect.Width > 0 && visibleRect.Height > 0);
        if (bounds == _lastBounds)
        {
            return;
        }
        _lastBounds = bounds;
        _ = SendCommandAsync(new
        {
            type = "bounds",
            x = bounds.X,
            y = bounds.Y,
            width = bounds.Width,
            height = bounds.Height,
            clipX = bounds.ClipX,
            clipY = bounds.ClipY,
            clipWidth = bounds.ClipWidth,
            clipHeight = bounds.ClipHeight,
            visible = bounds.Visible
        }, _generation);
    }

    private async Task SendCommandAsync(object command, int generation)
    {
        var writer = _hostInput;
        if (writer is null || generation != _generation)
        {
            return;
        }
        var payload = JsonSerializer.Serialize(command);
        if (Encoding.UTF8.GetByteCount(payload) > MaximumHostFrameLength)
        {
            return;
        }
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation == _generation && ReferenceEquals(writer, _hostInput))
            {
                await writer.WriteLineAsync(payload).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void StopHost()
    {
        var process = _hostProcess;
        var writer = _hostInput;
        var cancellation = _hostCancellation;
        _generation++;
        _hostProcess = null;
        _hostInput = null;
        _hostCancellation = null;
        _terminalStateReported = false;
        _lastBounds = null;
        _loadingAttempts.Invalidate();
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (process is not null)
        {
            _ = StopHostAsync(process, writer);
        }
    }

    private async Task StopHostAsync(Process process, StreamWriter? writer)
    {
        try
        {
            if (!process.HasExited && writer is not null)
            {
                await _writeLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await writer.WriteLineAsync("{\"type\":\"shutdown\"}").ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                    writer.Close();
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            if (!process.HasExited)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            TryKill(process);
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
            TryKill(process);
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKill(process);
            }
            process.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    internal static async IAsyncEnumerable<string> ReadBoundedFramesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var readBuffer = new byte[1024];
        var frame = new List<byte>(1024);
        while (true)
        {
            var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                yield break;
            }
            for (var index = 0; index < bytesRead; index++)
            {
                var value = readBuffer[index];
                if (value == (byte)'\n')
                {
                    if (frame.Count > 0 && frame[^1] == (byte)'\r')
                    {
                        frame.RemoveAt(frame.Count - 1);
                    }
                    yield return Encoding.UTF8.GetString(frame.ToArray());
                    frame.Clear();
                    continue;
                }
                if (frame.Count >= MaximumHostFrameLength)
                {
                    throw new InvalidDataException("WebToolHost protocol frame exceeded its limit.");
                }
                frame.Add(value);
            }
        }
    }

    internal static bool TryReadHostEvent(string line, int expectedProcessId, out HostProcessEvent hostEvent)
    {
        hostEvent = new HostProcessEvent(HostProcessEventKind.State, MptWebSurfaceState.Loading, "", "");
        try
        {
            using var payload = JsonDocument.Parse(line);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeNode) || typeNode.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("protocolVersion", out var versionNode) || !versionNode.TryGetInt32(out var protocolVersion) || protocolVersion != 1 ||
                !root.TryGetProperty("pid", out var processNode) || !processNode.TryGetInt32(out var processId) || processId != expectedProcessId)
            {
                return false;
            }

            var type = typeNode.GetString();
            if (string.Equals(type, "shortcut", StringComparison.Ordinal) &&
                root.TryGetProperty("gesture", out var gestureNode) && gestureNode.ValueKind == JsonValueKind.String)
            {
                var gesture = gestureNode.GetString() ?? "";
                if (gesture.Length is > 0 and <= 32)
                {
                    hostEvent = new HostProcessEvent(HostProcessEventKind.Shortcut, MptWebSurfaceState.Ready, "", gesture);
                    return true;
                }
                return false;
            }
            if (string.Equals(type, "focusMove", StringComparison.Ordinal) &&
                root.TryGetProperty("direction", out var directionNode) && directionNode.ValueKind == JsonValueKind.String)
            {
                var direction = directionNode.GetString() ?? "";
                if (direction is "next" or "previous")
                {
                    hostEvent = new HostProcessEvent(HostProcessEventKind.FocusMove, MptWebSurfaceState.Ready, "", direction);
                    return true;
                }
                return false;
            }
            if (string.Equals(type, "bridgeRequest", StringComparison.Ordinal) && root.TryGetProperty("payload", out var bridgePayload))
            {
                var requestJson = bridgePayload.GetRawText();
                if (requestJson.Length is > 0 and <= MaximumHostFrameLength)
                {
                    hostEvent = new HostProcessEvent(HostProcessEventKind.BridgeRequest, MptWebSurfaceState.Ready, "", requestJson);
                    return true;
                }
                return false;
            }
            if (!string.Equals(type, "state", StringComparison.Ordinal) ||
                !root.TryGetProperty("state", out var stateNode) || stateNode.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            var state = stateNode.GetString() switch
            {
                "ready" => MptWebSurfaceState.Ready,
                "unavailable" => MptWebSurfaceState.Unavailable,
                "failed" => MptWebSurfaceState.Failed,
                "loading" => MptWebSurfaceState.Loading,
                _ => (MptWebSurfaceState?)null
            };
            if (state is null)
            {
                return false;
            }
            var message = "";
            if (root.TryGetProperty("message", out var messageNode))
            {
                if (messageNode.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                message = messageNode.GetString() ?? "";
            }
            if (message.Length > 1024)
            {
                return false;
            }
            hostEvent = new HostProcessEvent(HostProcessEventKind.State, state.Value, message, "");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void NotifyState(MptWebSurfaceState state, string message = "")
    {
        Interlocked.Exchange(ref _currentState, (int)state);
        void Raise() => StateChanged?.Invoke(this, new MptWebSurfaceStateChangedEventArgs(state, message));
        if (Dispatcher.UIThread.CheckAccess())
        {
            Raise();
        }
        else
        {
            Dispatcher.UIThread.Post(Raise);
        }
    }

    private static string FriendlyHostError(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => "MyPowerTools WebToolHost could not access its installation or data directory.",
            _ => "MyPowerTools WebToolHost failed to start. Retry or open the tool in the system browser."
        };
    }

    private static Rect Intersect(Rect left, Rect right)
    {
        var x = Math.Max(left.Left, right.Left);
        var y = Math.Max(left.Top, right.Top);
        var maxX = Math.Min(left.Right, right.Right);
        var maxY = Math.Min(left.Bottom, right.Bottom);
        return maxX <= x || maxY <= y ? new Rect(x, y, 0, 0) : new Rect(x, y, maxX - x, maxY - y);
    }

    internal enum HostProcessEventKind
    {
        State,
        Shortcut,
        FocusMove,
        BridgeRequest
    }

    internal sealed record HostProcessEvent(
        HostProcessEventKind Kind,
        MptWebSurfaceState State,
        string Message,
        string Value);

    private sealed record HostBounds(
        int X,
        int Y,
        int Width,
        int Height,
        int ClipX,
        int ClipY,
        int ClipWidth,
        int ClipHeight,
        bool Visible);
}
