using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia;

public sealed partial class MainWindow
{
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private int _permanentCloseRequested;
    private WindowState _residentRestoreState = WindowState.Normal;
    private IActivatableLifetime? _platformActivation;

    private Task HandleShellActivationAsync(ShellActivationRequest request)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return ActivateShellAsync(request);
        }

        return Dispatcher.UIThread.InvokeAsync(() => ActivateShellAsync(request));
    }

    internal Task PresentFromPlatformTrayAsync() =>
        HandleShellActivationAsync(ShellActivationRequest.FocusShell);

    internal void ShutdownFromPlatformTray()
    {
        // The platform tray handler awaits the background-host shutdown before it gets here, so
        // this can arrive on a thread-pool continuation rather than the status item's main thread.
        if (Dispatcher.UIThread.CheckAccess())
        {
            ShutdownPermanently();
            return;
        }

        Dispatcher.UIThread.Post(ShutdownPermanently, DispatcherPriority.Send);
    }

    private async Task ActivateShellAsync(ShellActivationRequest request)
    {
        if (request.ShutdownShell)
        {
            AllowPermanentClose();
            return;
        }

        if (request.ShowShell)
        {
            await PresentResidentWindowAsync().ConfigureAwait(true);
        }

        if (request.ToolActivation is not null)
        {
            await _workspaceOpened.Task.ConfigureAwait(true);
            var workspace = _workspace;
            if (workspace is not null)
            {
                await workspace.ActivateToolAsync(request.ToolActivation).ConfigureAwait(true);
            }
        }
    }

    protected override void OnClosing(WindowClosingEventArgs args)
    {
        if (Volatile.Read(ref _permanentCloseRequested) != 0 ||
            args.CloseReason is not WindowCloseReason.WindowClosing and
                not WindowCloseReason.Undefined)
        {
            base.OnClosing(args);
            return;
        }

        if (!_trayAvailable)
        {
            // No tray icon on this platform -- let the close proceed as actual exit
            // so the user isn't left with an invisible, unreachable window.
            AllowPermanentClose();
            base.OnClosing(args);
            return;
        }

        args.Cancel = true;
        HideForResidentActivation();
        base.OnClosing(args);
        args.Cancel = true;
    }

    internal void AllowPermanentClose()
    {
        Interlocked.Exchange(ref _permanentCloseRequested, 1);
    }

    private void HideForResidentActivation()
    {
        if (WindowState is WindowState.Normal or WindowState.Maximized)
        {
            _residentRestoreState = WindowState;
        }

        HideNativeWindowImmediately();
        Hide();
        ShowInTaskbar = false;
        ShellStartupDiagnostics.Mark("resident-hidden");
    }

    private void HideNativeWindowImmediately()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            _ = ShowWindow(handle, SwHide);
        }
    }

    private async Task PresentResidentWindowAsync()
    {
        if (Volatile.Read(ref _windowClosed) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _suppressInitialPresentation, 0);
        ShowInTaskbar = true;
        if (!IsVisible)
        {
            WindowState = _residentRestoreState;
            Show();
        }
        else if (WindowState == WindowState.Minimized)
        {
            WindowState = _residentRestoreState;
        }

        Activate();
        if (!BringResidentWindowToForeground())
        {
            ForceResidentWindowToFront();
        }

        await WaitForPresentationBarrierAsync().ConfigureAwait(true);
        if (!IsResidentWindowForeground())
        {
            Activate();
            _ = BringResidentWindowToForeground();
        }

        ShellStartupDiagnostics.Mark("resident-presented");
    }

    private bool BringResidentWindowToForeground()
    {
        if (OperatingSystem.IsMacOS())
        {
            // Activate() raises the window within the app, but a Shell that was hidden to the
            // status item is not the frontmost application, so without this the window comes back
            // behind whatever the user was working in.
            return MacApplicationActivation.BringProcessToFront();
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        _ = ShowWindow(handle, SwShow);
        var foregroundSet = SetForegroundWindow(handle);
        _ = BringWindowToTop(handle);
        return foregroundSet;
    }

    private void ForceResidentWindowToFront()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        const uint flags = SwpNoMove | SwpNoSize | SwpShowWindow;
        _ = SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, flags);
        _ = SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0, flags);
        _ = SetForegroundWindow(handle);
        _ = BringWindowToTop(handle);
    }

    private bool IsResidentWindowForeground()
    {
        if (OperatingSystem.IsMacOS())
        {
            return IsActive && MacApplicationActivation.IsFrontmost();
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return handle != IntPtr.Zero && GetForegroundWindow() == handle;
    }

    /// <summary>
    /// AppKit activation for the resident Shell. Only the running application itself can pull the
    /// process in front of the app the user is currently in, and Avalonia exposes no API for that.
    /// </summary>
    private static class MacApplicationActivation
    {
        private const string ObjectiveCRuntime = "/usr/lib/libobjc.A.dylib";
        private const ulong ActivateAllWindows = 1UL << 0;
        private const ulong ActivateIgnoringOtherApps = 1UL << 1;

        internal static bool BringProcessToFront()
        {
            var application = CurrentApplication();
            return application != IntPtr.Zero &&
                SendActivateMessage(
                    application,
                    sel_registerName("activateWithOptions:"),
                    ActivateAllWindows | ActivateIgnoringOtherApps);
        }

        internal static bool IsFrontmost()
        {
            var application = CurrentApplication();
            return application != IntPtr.Zero &&
                SendBooleanMessage(application, sel_registerName("isActive"));
        }

        private static IntPtr CurrentApplication()
        {
            var runningApplication = objc_getClass("NSRunningApplication");
            return runningApplication == IntPtr.Zero
                ? IntPtr.Zero
                : SendMessage(runningApplication, sel_registerName("currentApplication"));
        }

        [DllImport(ObjectiveCRuntime)]
        private static extern IntPtr objc_getClass(string className);

        [DllImport(ObjectiveCRuntime)]
        private static extern IntPtr sel_registerName(string selectorName);

        [DllImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector);

        [DllImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SendBooleanMessage(IntPtr receiver, IntPtr selector);

        [DllImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SendActivateMessage(IntPtr receiver, IntPtr selector, ulong options);
    }

    private static Task WaitForPresentationBarrierAsync()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        _ = DwmFlush();
                    }
                }
                finally
                {
                    completion.TrySetResult(true);
                }
            },
            DispatcherPriority.Render);
        return completion.Task;
    }

    private void HandleShellActivationAcknowledged(ShellActivationRequest request)
    {
        if (!request.ShutdownShell)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            ShutdownPermanently,
            DispatcherPriority.Send);
    }

    private void ShutdownPermanently()
    {
        AllowPermanentClose();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
            return;
        }

        Close();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    internal void StartActivationListener(ShellActivationRequest? startupActivation)
    {
        if (_activationPipe is not null)
        {
            throw new InvalidOperationException("The Shell activation listener is already running.");
        }

        _startupActivation = startupActivation;
        if (startupActivation is { ShowShell: false })
        {
            Interlocked.Exchange(ref _suppressInitialPresentation, 1);
            Opacity = 0;
            ShowInTaskbar = false;
            ShowActivated = false;
        }
        _activationPipe = new ShellActivationPipe(
            HandleShellActivationAsync,
            afterAcknowledged: HandleShellActivationAcknowledged);
        _activationPipe.Start();
        _platformActivation = Application.Current?.TryGetFeature<IActivatableLifetime>();
        if (_platformActivation is not null)
        {
            _platformActivation.Activated += OnPlatformActivated;
        }
    }

    /// <summary>
    /// macOS answers a Dock or Finder launch of an app it already considers running with a reopen
    /// event instead of a second process, so the launcher never gets to forward an activation.
    /// The window has to come back from here on exactly the path the pipe would have taken.
    /// </summary>
    private void OnPlatformActivated(object? sender, ActivatedEventArgs args)
    {
        if (args.Kind != ActivationKind.Reopen)
        {
            return;
        }

        _ = HandleShellActivationAsync(ShellActivationRequest.FocusShell);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs args)
    {
        ApplyWindowsChrome();
    }

    private void OnWindowClosed(object? sender, EventArgs args)
    {
        Interlocked.Exchange(ref _windowClosed, 1);
        KeyDown -= OnShellKeyDown;
        Opened -= OnWindowOpened;
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        Closed -= OnWindowClosed;
        var platformActivation = Interlocked.Exchange(ref _platformActivation, null);
        if (platformActivation is not null)
        {
            platformActivation.Activated -= OnPlatformActivated;
        }
        var activationPipe = Interlocked.Exchange(ref _activationPipe, null);
        _workspaceOpened.TrySetResult(true);
        _ = DisposeWindowResourcesAsync(activationPipe);
    }

    private async Task DisposeWindowResourcesAsync(ShellActivationPipe? activationPipe)
    {
        try
        {
            if (activationPipe is not null)
            {
                await activationPipe.DisposeAsync();
            }

            var workspace = _workspace;
            if (workspace is not null)
            {
                await workspace.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Dispose Shell window resources", ex, "dispose");
        }
    }
}
