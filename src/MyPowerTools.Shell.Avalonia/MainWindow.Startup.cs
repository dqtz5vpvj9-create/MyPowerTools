using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MyPowerTools.UI;
using MyPowerTools.UI.Controls;
using MyPowerTools.WebSurface.Avalonia;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia;

public sealed partial class MainWindow
{
    private async Task OpenShellAsync()
    {
        var workspace = _workspace;
        if (workspace is null)
        {
            _workspaceOpened.TrySetResult(true);
            return;
        }

        if (_startupActivation?.ToolActivation is not null)
        {
            await OpenStartupToolAsync(workspace);
            return;
        }

        if (_startupOptions.FocusCommandPalette)
        {
            await OpenStartupCommandPaletteAsync(workspace);
            return;
        }

        ShellHomeSnapshot? cachedSnapshot = null;
        if (!HasLiveStartupTools())
        {
            cachedSnapshot = await _cachedHomeSnapshotTask.ConfigureAwait(true);
        }

        if (cachedSnapshot is not null && !HasLiveStartupTools())
        {
            try
            {
                await OpenInitialWorkspaceAsync(workspace, cachedSnapshot.Tools);
            }
            finally
            {
                _workspaceOpened.TrySetResult(true);
            }

            RunWindowUiEvent(
                () => ReconcileLiveHomeAsync(workspace, cachedSnapshot),
                "Reconcile cached Home data");
            return;
        }

        ShellRunnerBootstrapResult? bootstrapResult = null;
        try
        {
            try
            {
                bootstrapResult = await _runnerBootstrapTask.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ShellCommandFaultLog.Write("Bootstrap Runner", ex, "startup");
            }

            await OpenInitialWorkspaceAsync(workspace, bootstrapResult?.StartupTools);
        }
        finally
        {
            _workspaceOpened.TrySetResult(true);
        }

        ShellStartupDiagnostics.Mark("home-live-complete");

        if (bootstrapResult?.StartupTools is not null)
        {
            PersistLiveHomeSnapshot(bootstrapResult.StartupTools);
        }
    }

    private async Task OpenInitialWorkspaceAsync(
        ShellWorkspaceController workspace,
        IReadOnlyList<MyPowerTools.Protocol.HostControl.V1.ToolDescriptor>? startupTools)
    {
        await workspace.OpenAsync(startupTools);
        (Application.Current as App)?.ScheduleDeferredStyles();
    }

    private async Task OpenStartupToolAsync(ShellWorkspaceController workspace)
    {
        try
        {
            (Application.Current as App)?.EnsureDeferredStyles();
            await WaitForRunnerAsync();
            var request = Interlocked.Exchange(ref _startupActivation, null);
            if (request?.ToolActivation is not null)
            {
                await workspace.ActivateToolAsync(request.ToolActivation);
            }
        }
        finally
        {
            workspace.CompleteStartup();
            _workspaceOpened.TrySetResult(true);
        }
    }

    private async Task OpenStartupCommandPaletteAsync(ShellWorkspaceController workspace)
    {
        try
        {
            (Application.Current as App)?.EnsureDeferredStyles();
            await WaitForRunnerAsync();
            await workspace.FocusCommandPaletteAsync();
        }
        finally
        {
            workspace.CompleteStartup();
            _workspaceOpened.TrySetResult(true);
        }
    }

    private async Task WaitForRunnerAsync()
    {
        try
        {
            await _runnerBootstrapTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Bootstrap Runner", ex, "startup");
        }
    }

    private async Task ReconcileLiveHomeAsync(
        ShellWorkspaceController workspace,
        ShellHomeSnapshot cachedSnapshot)
    {
        ShellRunnerBootstrapResult bootstrapResult;
        try
        {
            bootstrapResult = await _runnerBootstrapTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Reconcile cached Home data", ex, "startup-cache");
            ShellStartupDiagnostics.Mark("home-live-failed");
            return;
        }

        IReadOnlyList<MyPowerTools.Protocol.HostControl.V1.ToolDescriptor> liveTools;
        try
        {
            liveTools = bootstrapResult.StartupTools ??
                await new ShellToolProductService().LoadToolDescriptorsAsync();
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Refresh cached Home data", ex, "startup-cache");
            ShellStartupDiagnostics.Mark("home-live-failed");
            return;
        }

        var liveSnapshot = ShellHomeSnapshotCache.Create(liveTools);
        PersistLiveHomeSnapshot(liveSnapshot, cachedSnapshot.Fingerprint);
        if (!string.Equals(liveSnapshot.Fingerprint, cachedSnapshot.Fingerprint, StringComparison.Ordinal))
        {
            await workspace.ReconcileHomeToolsAsync(liveSnapshot.Tools);
        }

        ShellStartupDiagnostics.Mark("home-live-complete");
    }

    private void PersistLiveHomeSnapshot(
        IReadOnlyList<MyPowerTools.Protocol.HostControl.V1.ToolDescriptor> tools)
    {
        PersistLiveHomeSnapshot(ShellHomeSnapshotCache.Create(tools), null);
    }

    private void PersistLiveHomeSnapshot(ShellHomeSnapshot snapshot, string? cachedFingerprint)
    {
        if (string.Equals(snapshot.Fingerprint, cachedFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var cached = await _cachedHomeSnapshotTask.ConfigureAwait(false);
                if (string.Equals(snapshot.Fingerprint, cached?.Fingerprint, StringComparison.Ordinal))
                {
                    return;
                }

                await ShellHomeSnapshotCache.WriteAsync(snapshot, _startupOptions.DataRoot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ShellCommandFaultLog.Write("Persist Home startup snapshot", ex, "startup-cache");
            }
        });
    }

    private bool HasLiveStartupTools()
    {
        return _runnerBootstrapTask.IsCompletedSuccessfully &&
            _runnerBootstrapTask.Result.StartupTools is not null;
    }

    private void OnWindowOpened(object? sender, EventArgs args)
    {
        ShellStartupDiagnostics.Mark("window-opened");
        if (Interlocked.Exchange(ref _suppressInitialPresentation, 0) != 0)
        {
            HideForResidentActivation();
            Opacity = 1;
            ShowActivated = true;
        }

        ApplyWindowsChrome();
        Dispatcher.UIThread.Post(InitializeWorkspace, DispatcherPriority.Background);
    }

    private void InitializeWorkspace()
    {
        if (Volatile.Read(ref _windowClosed) != 0 ||
            Interlocked.Exchange(ref _workspaceInitializationStarted, 1) != 0)
        {
            return;
        }

        try
        {
            _workspace = new ShellWorkspaceController(
                _chromeViewModel,
                RequireControl<MptSearchBox>(_chrome, "SearchBox"),
                RequireControl<ContentControl>(_chrome, "ContentHost"),
                RequireControl<ContentControl>(_chrome, "CommandPanel"),
                RequireControl<ContentControl>(_chrome, "PermissionPanel"),
                RequireControl<ContentControl>(_chrome, "AuditPanel"),
                () => PlatformWebSurfaceService.Create(
                    AppContext.BaseDirectory,
                    _webSurfaceOcclusion,
                    HandleForwardedWebToolShortcutAsync),
                RequireControl<Grid>(_chrome, "WebSurfaceHost"));
            ShellStartupDiagnostics.Mark("workspace-created");
            var startupToolId = _startupActivation?.ToolActivation?.ToolId;
            if (!string.IsNullOrWhiteSpace(startupToolId))
            {
                _workspace.ShowToolStartupPage(startupToolId);
            }
            else if (_startupOptions.FocusCommandPalette)
            {
                _workspace.ShowCommandPaletteStartupPage();
            }
            else
            {
                var liveHomeReady = HasLiveStartupTools();
                var cachedHomeReady = !liveHomeReady &&
                    _cachedHomeSnapshotTask.IsCompletedSuccessfully &&
                    _cachedHomeSnapshotTask.Result is not null;
                if (!liveHomeReady && !cachedHomeReady)
                {
                    _workspace.ShowStartupPage();
                }
            }
            RunWindowUiEvent(OpenShellAsync, "Open Shell workspace");
        }
        catch (Exception ex)
        {
            _workspaceOpened.TrySetResult(true);
            ShellCommandFaultLog.Write("Initialize Shell workspace", ex, "startup");
            RequireControl<ContentControl>(_chrome, "ContentHost").Content = new MptErrorState(ex.Message);
        }
    }
}
