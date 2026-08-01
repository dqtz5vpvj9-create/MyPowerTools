using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.UI.Controls;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal void RunScopedUiEvent(Func<Task> action, string operation)
    {
        RunUiEvent(action, operation);
    }

    private void BeginWorkspace(bool rebindCurrentContent = false)
    {
        Interlocked.Exchange(ref _homeLoadDeferred, 0);
        _chromeViewModel.HeaderContent = null;
        _workspaceIdentity.BeginNavigation();
        _terminalFaultRecovery.Reset();
        lock (_handledFaultGate)
        {
            _handledFaultInvocations.Clear();
        }

        AttachCurrentFaultOwner(_chromeViewModel);
        if (rebindCurrentContent)
        {
            AttachCurrentFaultOwner((_contentHost.Content as Control)?.DataContext);
        }
    }

    private void AttachCurrentFaultOwner(object? value)
    {
        ShellCommandFaultOwnership.Attach(
            value,
            _faultSink,
            _workspaceIdentity.Capture());
    }

    private void SetOwnedContent(ContentControl host, Control? content)
    {
        if (IsDisposed)
        {
            DisposeControlDataContext(content);
            return;
        }

        var previous = host.Content as Control;
        if (ReferenceEquals(host, _contentHost))
        {
            DeactivateCachedWebTools();
        }
        if (content is not null)
        {
            AttachCurrentFaultOwner(content.DataContext);
        }
        host.Content = content;
        if (!ReferenceEquals(previous?.DataContext, content?.DataContext) &&
            !IsCachedWebToolDataContext(previous?.DataContext))
        {
            DisposeControlDataContext(previous);
        }
    }

    private void PostUiEvent(Func<Task> action, string operation)
    {
        if (IsDisposed)
        {
            return;
        }

        var context = _workspaceIdentity.Capture().BeginInvocation();
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsDisposed && _workspaceIdentity.IsCurrent(context))
            {
                RunUiEvent(action, operation, context);
            }
        });
    }

    private void RunUiEvent(
        Func<Task> action,
        string operation,
        ShellCommandFaultContext? context = null)
    {
        if (!IsDisposed)
        {
            _ = RunUiEventCoreAsync(
                action,
                operation,
                context ?? _workspaceIdentity.Capture().BeginInvocation());
        }
    }

    private async Task RunUiEventCoreAsync(
        Func<Task> action,
        string operation,
        ShellCommandFaultContext context)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _faultSink.Report(this, operation, ex, context);
        }
    }

    private void OnShellCommandFaulted(object? sender, ShellCommandFaultEventArgs fault)
    {
        if (IsDisposed ||
            !_workspaceIdentity.IsCurrent(fault.Context) ||
            string.IsNullOrWhiteSpace(fault.Context.InvocationId))
        {
            ShellCommandFaultLog.Write(fault.Operation, fault.Exception, "stale-workspace");
            return;
        }

        lock (_handledFaultGate)
        {
            if (!_handledFaultInvocations.Add(fault.Context.InvocationId))
            {
                return;
            }
        }

        var faultedToolId = _currentToolId;
        var faultedRouteId = _currentToolRouteId;
        var faultedPage = _currentPage;
        Dispatcher.UIThread.Post(() => RecoverWorkspaceFault(
            fault,
            faultedPage,
            faultedToolId,
            faultedRouteId));
    }

    private void RecoverWorkspaceFault(
        ShellCommandFaultEventArgs fault,
        string faultedPage,
        string faultedToolId,
        string faultedRouteId)
    {
        if (IsDisposed || !_workspaceIdentity.IsCurrent(fault.Context))
        {
            return;
        }

        var message = SafeFaultMessage(fault.Exception);
        _terminalFaultRecovery.TryRecover(
            () => !IsDisposed && _workspaceIdentity.IsCurrent(fault.Context),
            () =>
            {
                Func<Task> retry = string.IsNullOrWhiteSpace(faultedToolId)
                    ? () => ShowPageAsync(faultedPage)
                    : () => ShowToolPageAsync(faultedToolId, faultedRouteId);
                Func<Task> returnToSafety = () => ShowPageAsync(
                    string.IsNullOrWhiteSpace(faultedToolId) ? HomePage : ToolsPage);
                SetOwnedContent(
                    _contentHost,
                    BuildUnavailablePage(
                        "This workspace recovered from an action failure",
                        $"{fault.Operation} failed: {message}",
                        retry,
                        returnToSafety));
                SetStatus($"Recovered from {fault.Operation}: {message}");
            },
            () =>
            {
                DisposeControlDataContext(_contentHost.Content as Control);
                _contentHost.Content = new MptErrorState(
                    "This workspace stopped after a UI recovery failure. Open another tool or return Home.");
            });
    }

    private static string SafeFaultMessage(Exception exception)
    {
        var message = MptLogRedactor.Redact(exception.Message);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = exception.GetType().Name;
        }
        return message.Length <= 512 ? message : message[..512];
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs args)
    {
        if (!IsDisposed)
        {
            QueueCommandSearch(_searchBox.Text ?? "");
        }
    }

    internal void OnSearchGotFocus(object? sender, FocusChangedEventArgs? args)
    {
        if (!IsDisposed && !_chromeViewModel.IsCommandPaletteOpen)
        {
            RunUiEvent(
                () => OpenCommandPaletteAsync(focusSearch: false),
                "Open command palette from search focus");
        }
    }

    private void OnRunnerStatusChanged(string text)
    {
        if (!IsDisposed)
        {
            Dispatcher.UIThread.Post(() => SetStatus(text));
        }
    }

    private void OnRunnerStateChanged(string text)
    {
        if (!IsDisposed)
        {
            Dispatcher.UIThread.Post(() => SetRunnerStatus(text));
        }
    }

    private void OnRunnerRecovered()
    {
        PostUiEvent(() => RefreshShellDataAsync(), "Refresh after Runner recovery");
    }

    private void OnHostEventReceived(HostProto.HostEvent evt)
    {
        PostUiEvent(() => ApplyHostEventAsync(evt), $"Apply host event {evt.Type}");
    }

    private void OnUnitEventReceived(object? sender, MyPowerTools.Protocol.ServiceManager.V1.UnitEvent evt)
    {
        // Reactive update: when the Services page is visible, refresh it so the user sees state
        // changes (start/stop/restart/exit) without manual refresh. Tool Surface pages observe
        // their own units via the scoped IServiceUnitClient they own.
        if (string.Equals(_currentPage, ServicesPage, StringComparison.OrdinalIgnoreCase))
        {
            PostUiEvent(() => LoadServicesPageAsync(), $"Apply unit event {evt.Type} on {evt.UnitId}");
        }
    }

    private void OnUnitStreamFaulted(object? sender, Exception ex)
    {
        PostUiEvent(() =>
        {
            if (_contentHost.DataContext is ServicesViewModel vm)
            {
                vm.Disconnected = true;
            }

            return Task.CompletedTask;
        }, "ServiceManager stream faulted");
    }

    private void OnUnitStreamRecovered(object? sender, EventArgs e)
    {
        PostUiEvent(() =>
        {
            if (string.Equals(_currentPage, ServicesPage, StringComparison.OrdinalIgnoreCase))
            {
                return LoadServicesPageAsync();
            }

            if (_contentHost.DataContext is ServicesViewModel vm)
            {
                vm.Disconnected = false;
            }

            return Task.CompletedTask;
        }, "ServiceManager stream recovered");
    }

    private bool TryBeginDispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return false;
        }

        _workspaceIdentity.BeginNavigation();
        _terminalFaultRecovery.Reset();
        return true;
    }

    private void UnsubscribeShellEvents()
    {
        _searchBox.TextChanged -= _searchTextChangedHandler;
        _searchBox.KeyDown -= OnCommandSearchKeyDown;
        _searchBox.GotFocus -= _searchGotFocusHandler;
        if (Volatile.Read(ref _eventSubscriptionsAttached) != 0)
        {
            _runnerEvents.StatusChanged -= _runnerStatusChangedHandler;
            _runnerEvents.RunnerStatusChanged -= _runnerStateChangedHandler;
            _runnerEvents.RunnerRecovered -= _runnerRecoveredHandler;
            _runnerEvents.HostEventReceived -= _hostEventReceivedHandler;
            _unitEvents.UnitEventReceived -= _unitEventReceivedHandler;
            _unitEvents.StreamFaulted -= _unitStreamFaultedHandler;
            _unitEvents.StreamRecovered -= _unitStreamRecoveredHandler;
        }
        _faultSink.Faulted -= OnShellCommandFaulted;
    }

    private void DisposeHostedContent()
    {
        var disposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var host in new[] { _contentHost, _commandPanel, _permissionPanel, _auditPanel })
        {
            if (host.Content is Control control &&
                control.DataContext is IDisposable disposable &&
                disposed.Add(disposable))
            {
                TryDispose(disposable);
            }
            host.Content = null;
        }
    }

    private static void DisposeControlDataContext(Control? control)
    {
        if (control?.DataContext is IDisposable disposable)
        {
            TryDispose(disposable);
        }
    }

    private static void TryDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Dispose workspace view model", ex, "dispose");
        }
    }
}
