using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Platform;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.WebSurface.Avalonia;

public static class PlatformWebSurfaceService
{
    public static IMptWebSurfaceService Create(
        string applicationBaseDirectory,
        WebSurfaceOcclusionState occlusionState,
        Func<string, Task>? forwardShortcutAsync = null)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacWebSurfaceService(occlusionState, forwardShortcutAsync);
        }

        if (OperatingSystem.IsWindows())
        {
            return new AvaloniaWebSurfaceService(
                AvaloniaWebSurfaceService.ResolveDefaultHostPath(applicationBaseDirectory),
                occlusionState,
                forwardShortcutAsync);
        }

        return new UnavailableWebSurfaceService(
            "A native web surface provider is unavailable for this platform.");
    }
}

public sealed class MacWebSurfaceService : IMptWebSurfaceService, IConfigurableWebShortcuts
{
    private readonly WebShortcutConfiguration _shortcutConfiguration = new();
    public void UpdateShortcutBindings(IReadOnlyList<WebShortcutBinding> bindings) => _shortcutConfiguration.Update(bindings);

    private readonly WebSurfaceOcclusionState _occlusionState;
    private readonly Func<string, Task> _forwardShortcutAsync;

    public MacWebSurfaceService(
        WebSurfaceOcclusionState occlusionState,
        Func<string, Task>? forwardShortcutAsync = null)
    {
        _occlusionState = occlusionState ?? throw new ArgumentNullException(nameof(occlusionState));
        _forwardShortcutAsync = forwardShortcutAsync ?? (_ => Task.CompletedTask);
    }

    public IMptWebSurfaceSession CreateSession(MptWebSurfaceRequest request)
    {
        var normalized = WebSurfaceNavigationPolicy.Normalize(request);
        var control = new MacWebSurfaceControl(normalized, _occlusionState, _forwardShortcutAsync, _shortcutConfiguration);
        return new NativeWebSurfaceSession(control);
    }
}

internal sealed class NativeWebSurfaceSession : IMptWebSurfaceSession, IPersistentWebSurfaceSession
{
    private readonly MacWebSurfaceControl _control;
    private int _disposed;

    public NativeWebSurfaceSession(MacWebSurfaceControl control)
    {
        _control = control;
        State = control.CurrentState;
        _control.StateChanged += HandleStateChanged;
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
        _control.StateChanged -= HandleStateChanged;
        _control.Dispose();
        StateChanged = null;
    }

    private void HandleStateChanged(object? sender, MptWebSurfaceStateChangedEventArgs args)
    {
        State = args.State;
        StateChanged?.Invoke(this, args);
    }
}

internal sealed class MacWebSurfaceControl : NativeControlHost, IDisposable
{
    private static readonly MacWebViewNative.EventCallback NativeCallback = OnNativeEvent;
    private static readonly TimeSpan LoadingTimeout = TimeSpan.FromSeconds(12);

    private readonly WebShortcutConfiguration _shortcutConfiguration;
    private readonly MptWebSurfaceRequest _request;
    private readonly WebSurfaceOcclusionState _occlusionState;
    private readonly Func<string, Task> _forwardShortcutAsync;
    private GCHandle _selfHandle;
    private nint _nativeHandle;
    private CancellationTokenSource? _loadingCancellation;
    private int _state = (int)MptWebSurfaceState.Loading;
    private int _disposed;
    private bool _active = true;

    public MacWebSurfaceControl(
        MptWebSurfaceRequest request,
        WebSurfaceOcclusionState occlusionState,
        Func<string, Task> forwardShortcutAsync,
        WebShortcutConfiguration? shortcuts = null)
    {
        _shortcutConfiguration = shortcuts ?? new WebShortcutConfiguration();
        _shortcutConfiguration.Changed += SendShortcutBindings;
        _request = request;
        _occlusionState = occlusionState;
        _forwardShortcutAsync = forwardShortcutAsync;
        Focusable = true;
        _occlusionState.Changed += HandleOcclusionChanged;
    }

    public MptWebSurfaceState CurrentState => (MptWebSurfaceState)Volatile.Read(ref _state);
    public event EventHandler<MptWebSurfaceStateChangedEventArgs>? StateChanged;

    public void Reload()
    {
        if (_nativeHandle == 0 || Volatile.Read(ref _disposed) != 0)
        {
            NotifyState(MptWebSurfaceState.Loading, "The WKWebView surface will load when attached.");
            return;
        }
        BeginLoadingTimeout();
        MacWebViewNative.Reload(_nativeHandle);
    }

    public void Navigate(Uri source)
    {
        if (_nativeHandle == 0 ||
            Volatile.Read(ref _disposed) != 0 ||
            !WebSurfaceNavigationPolicy.IsSupportedWebUri(source))
        {
            return;
        }
        BeginLoadingTimeout();
        MacWebViewNative.Navigate(_nativeHandle, source.AbsoluteUri);
    }

    public void SetActive(bool active)
    {
        if (Volatile.Read(ref _disposed) != 0 || _active == active)
        {
            return;
        }
        _active = active;
        if (_nativeHandle != 0)
        {
            MacWebViewNative.SetVisible(_nativeHandle, active && !_occlusionState.IsOccluded);
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return base.CreateNativeControlCore(parent);
        }

        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        var originsJson = JsonSerializer.Serialize(_request.AllowedOrigins.Select(origin => origin.AbsoluteUri));
        try
        {
            _nativeHandle = MacWebViewNative.Create(
                _request.Source.AbsoluteUri,
                originsJson,
                NativeCallback,
                GCHandle.ToIntPtr(_selfHandle));
            if (_nativeHandle == 0)
            {
                throw new InvalidOperationException("WKWebView returned an empty NSView handle.");
            }
            MacWebViewNative.SetVisible(_nativeHandle, _active && !_occlusionState.IsOccluded);
            BeginLoadingTimeout();
            return new MacWebViewPlatformHandle(_nativeHandle, ReleaseNativeHandle);
        }
        catch (Exception ex)
        {
            ReleaseNativeHandle();
            NotifyState(MptWebSurfaceState.Unavailable, FriendlyNativeError(ex));
            return base.CreateNativeControlCore(parent);
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        base.DestroyNativeControlCore(control);
    }

    protected override void OnGotFocus(global::Avalonia.Input.FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (_nativeHandle != 0)
        {
            var direction = e.NavigationMethod == global::Avalonia.Input.NavigationMethod.Tab &&
                            e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Shift)
                ? -1
                : 1;
            MacWebViewNative.Focus(_nativeHandle, direction);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _shortcutConfiguration.Changed -= SendShortcutBindings;
        _occlusionState.Changed -= HandleOcclusionChanged;
        _loadingCancellation?.Cancel();
        _loadingCancellation?.Dispose();
        _loadingCancellation = null;
        ReleaseNativeHandle();
        StateChanged = null;
    }

    private void HandleOcclusionChanged(object? sender, EventArgs args)
    {
        if (_nativeHandle != 0)
        {
            MacWebViewNative.SetVisible(_nativeHandle, _active && !_occlusionState.IsOccluded);
        }
    }

    private void ReleaseNativeHandle()
    {
        var handle = Interlocked.Exchange(ref _nativeHandle, 0);
        if (handle != 0)
        {
            MacWebViewNative.Destroy(handle);
        }
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    private static void OnNativeEvent(nint context, int eventKind, nint payloadPointer)
    {
        if (context == 0)
        {
            return;
        }
        var target = GCHandle.FromIntPtr(context).Target as MacWebSurfaceControl;
        if (target is null || Volatile.Read(ref target._disposed) != 0)
        {
            return;
        }
        var payload = payloadPointer == 0 ? "" : Marshal.PtrToStringUTF8(payloadPointer) ?? "";
        Dispatcher.UIThread.Post(() => target.HandleNativeEvent(eventKind, payload));
    }

    private void HandleNativeEvent(int eventKind, string payload)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        switch (eventKind)
        {
            case MacWebViewNative.EventLoading:
                BeginLoadingTimeout();
                NotifyState(MptWebSurfaceState.Loading);
                break;
            case MacWebViewNative.EventReady:
                CancelLoadingTimeout();
                NotifyState(MptWebSurfaceState.Ready);
                SendShortcutBindings();
                break;
            case MacWebViewNative.EventFailed:
                CancelLoadingTimeout();
                NotifyState(MptWebSurfaceState.Failed, payload);
                break;
            case MacWebViewNative.EventBridgeRequest:
                _ = HandleBridgeRequestAsync(payload);
                break;
            case MacWebViewNative.EventShortcut:
                _ = ForwardShortcutAsync(payload);
                break;
        }
    }

    private void SendShortcutBindings()
    {
        if (_nativeHandle != 0 && Volatile.Read(ref _disposed) == 0)
            MacWebViewNative.SendBridgeResponse(_nativeHandle, _shortcutConfiguration.Json);
    }

    private async Task HandleBridgeRequestAsync(string requestJson)
    {
        if (requestJson.Length <= 4096)
        {
            try
            {
                using var message = JsonDocument.Parse(requestJson);
                if (message.RootElement.ValueKind == JsonValueKind.Object &&
                    message.RootElement.TryGetProperty("__mptShortcut", out var shortcut))
                {
                    await ForwardShortcutAsync(shortcut.GetRawText());
                    return;
                }
            }
            catch (JsonException) { }
        }
        if (_request.HandleBridgeRequestAsync is null || requestJson.Length > 16 * 1024)
        {
            return;
        }
        string response;
        try
        {
            response = await _request.HandleBridgeRequestAsync(requestJson, CancellationToken.None);
        }
        catch (Exception ex)
        {
            response = CreateBridgeFailure(requestJson, ex.GetBaseException().Message);
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_nativeHandle != 0 && response.Length <= 16 * 1024)
            {
                MacWebViewNative.SendBridgeResponse(_nativeHandle, response);
            }
        });
    }

    private async Task ForwardShortcutAsync(string gesture)
    {
        try
        {
            await _forwardShortcutAsync(gesture);
        }
        catch
        {
        }
    }

    private void BeginLoadingTimeout()
    {
        CancelLoadingTimeout();
        var cancellation = new CancellationTokenSource();
        _loadingCancellation = cancellation;
        _ = WatchLoadingAsync(cancellation);
    }

    private async Task WatchLoadingAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(LoadingTimeout, cancellation.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_loadingCancellation, cancellation) &&
                    CurrentState == MptWebSurfaceState.Loading)
                {
                    NotifyState(MptWebSurfaceState.Failed, "WKWebView loading timed out.");
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelLoadingTimeout()
    {
        var cancellation = Interlocked.Exchange(ref _loadingCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void NotifyState(MptWebSurfaceState state, string message = "")
    {
        Volatile.Write(ref _state, (int)state);
        StateChanged?.Invoke(this, new MptWebSurfaceStateChangedEventArgs(state, message));
    }

    private static string CreateBridgeFailure(string requestJson, string message)
    {
        string id = "";
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            if (document.RootElement.TryGetProperty("id", out var idNode))
            {
                id = idNode.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }
        return new JsonObject
        {
            ["version"] = "1.0",
            ["id"] = id,
            ["type"] = "bridge.result",
            ["error"] = new JsonObject
            {
                ["code"] = "bridge.request.failed",
                ["message"] = message
            }
        }.ToJsonString();
    }

    private static string FriendlyNativeError(Exception exception)
    {
        return exception is DllNotFoundException
            ? "The WKWebView native capability library is missing from the application bundle."
            : $"WKWebView initialization failed: {exception.GetBaseException().Message}";
    }
}

internal sealed class MacWebViewPlatformHandle : PlatformHandle, INativeControlHostDestroyableControlHandle
{
    private Action? _destroy;

    public MacWebViewPlatformHandle(nint handle, Action destroy)
        : base(handle, "NSView")
    {
        _destroy = destroy;
    }

    public void Destroy()
    {
        Interlocked.Exchange(ref _destroy, null)?.Invoke();
    }
}

internal static class MacWebViewNative
{
    public const int EventLoading = 0;
    public const int EventReady = 1;
    public const int EventFailed = 2;
    public const int EventBridgeRequest = 3;
    public const int EventShortcut = 4;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCallback(nint context, int eventKind, nint payload);

    [DllImport("MptMacNative", EntryPoint = "mpt_webview_create", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint Create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string allowedOriginsJson,
        EventCallback callback,
        nint context);

    [DllImport("MptMacNative", EntryPoint = "mpt_webview_reload", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Reload(nint handle);

    [DllImport("MptMacNative", EntryPoint = "mpt_webview_navigate", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Navigate(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source);

    [DllImport("MptMacNative", EntryPoint = "mpt_webview_send_bridge_response", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SendBridgeResponse(
        nint handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string responseJson);

    [DllImport("MptMacNative", EntryPoint = "mpt_webview_focus", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Focus(nint handle, int direction);

    [DllImport("MptMacNative", EntryPoint = "mpt_webview_set_visible", CallingConvention = CallingConvention.Cdecl)]
    public static extern void SetVisible(nint handle, [MarshalAs(UnmanagedType.Bool)] bool visible);

    [DllImport("MptMacNative", EntryPoint = "mpt_webview_destroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Destroy(nint handle);
}

internal sealed class UnavailableWebSurfaceService(string message) : IMptWebSurfaceService
{
    public IMptWebSurfaceSession CreateSession(MptWebSurfaceRequest request) =>
        new UnavailableWebSurfaceSession(message);
}

internal sealed class UnavailableWebSurfaceSession : IMptWebSurfaceSession, IPersistentWebSurfaceSession
{
    public UnavailableWebSurfaceSession(string message)
    {
        View = new Border
        {
            Padding = new global::Avalonia.Thickness(24),
            Child = new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap }
        };
    }

    public Control View { get; }
    public MptWebSurfaceState State => MptWebSurfaceState.Unavailable;
    public event EventHandler<MptWebSurfaceStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }
    public void Navigate(Uri source) { }
    public void Reload() { }
    public void SetActive(bool active) { }
    public void Dispose() { }
}
