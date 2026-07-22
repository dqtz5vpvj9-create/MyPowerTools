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

    internal void ShutdownFromPlatformTray() => ShutdownPermanently();

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
        await _workspaceOpened.Task.ConfigureAwait(true);
        if (Volatile.Read(ref _windowClosed) != 0)
        {
            return;
        }

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
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return handle != IntPtr.Zero && GetForegroundWindow() == handle;
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
