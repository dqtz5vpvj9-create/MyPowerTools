using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI.Controls;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private Task LoadCommandsAsync(string query)
    {
        return LoadCommandsAsync(query, TimeSpan.Zero);
    }

    private void QueueCommandSearch(string query)
    {
        if (!_chromeViewModel.IsCommandPaletteOpen)
        {
            return;
        }

        _ = LoadCommandsAsync(query, TimeSpan.FromMilliseconds(150));
    }

    private async Task LoadCommandsAsync(string query, TimeSpan debounce)
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _commandSearchCancellation, cancellation);
        previous?.Cancel();
        var version = Interlocked.Increment(ref _commandSearchVersion);
        try
        {
            if (debounce > TimeSpan.Zero)
            {
                await Task.Delay(debounce, cancellation.Token);
            }

            var viewModel = await _pageData.LoadCommandsAsync(
                query,
                (commandId, args, invocationId, cancellationToken) => ExecuteCommandStreamAsync(commandId, args, invocationId, cancellationToken),
                invocationId => CancelCommandAsync(invocationId),
                (toolId, routeId, _) => ShowToolPageAsync(toolId, routeId),
                searchableToolIds: null,
                cancellation.Token);
            if (version != Volatile.Read(ref _commandSearchVersion) || cancellation.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _commandPaletteViewModel = viewModel;
                SetOwnedContent(_commandPanel, new CommandPaletteView
                {
                    DataContext = viewModel
                });
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (version == Volatile.Read(ref _commandSearchVersion))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _commandPaletteViewModel = null;
                    SetOwnedContent(_commandPanel, new MptErrorState(ex.Message));
                });
            }
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _commandSearchCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void OnCommandSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_chromeViewModel.IsCommandPaletteOpen || _commandPaletteViewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                _commandPaletteViewModel.MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                _commandPaletteViewModel.MoveSelection(-1);
                break;
            case Key.Enter:
                e.Handled = true;
                RunUiEvent(
                    _commandPaletteViewModel.ActivateSelectedAsync,
                    "Activate selected command palette item");
                break;
        }
    }

    private async Task LoadBrokerAuditAsync()
    {
        try
        {
            var viewModel = await _pageData.LoadBrokerAuditAsync();
            SetOwnedContent(_auditPanel, new BrokerAuditView
            {
                DataContext = viewModel
            });
        }
        catch (Exception ex)
        {
            SetOwnedContent(_auditPanel, new BrokerAuditView
            {
                DataContext = _pageData.CreateBrokerAuditError(ex.Message)
            });
        }
    }

    private Control BuildUnavailablePage(
        string title,
        string message,
        Func<Task>? retry = null,
        Func<Task>? returnToSafety = null)
    {
        return new UnavailablePageView
        {
            DataContext = new UnavailablePageViewModel(title, message, retry, returnToSafety)
        };
    }

    private async Task<CommandExecutionStatus> ExecuteCommandAsync(
        string commandId,
        JsonObject? args = null,
        string? invocationId = null,
        CancellationToken cancellationToken = default)
    {
        if (ShellCommandRouter.TryHandleShellCommand(
            commandId,
            moduleId => RefreshModuleStatusAsync(moduleId, cancellationToken),
            out var shellAction))
        {
            await shellAction;
            var message = ShellCommandRouter.SuccessMessage(commandId);
            SetStatus(message);
            return new CommandExecutionStatus("succeeded", message);
        }

        return await ExecuteRuntimeCommandAsync(commandId, args, invocationId, cancellationToken);
    }

    private async Task<CommandExecutionStatus> ExecuteRuntimeCommandAsync(
        string commandId,
        JsonObject? args = null,
        string? invocationId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = string.IsNullOrWhiteSpace(invocationId)
                ? await _commandExecutionService.ExecuteAsync(commandId, args, cancellationToken)
                : await _commandExecutionService.ExecuteAsync(invocationId, commandId, args, cancellationToken);
            SetStatus(result.StatusText);
            SetOwnedContent(_permissionPanel, null);
            _chromeViewModel.IsPermissionPromptOpen = false;
            if (result.RequiresPermissionPrompt)
            {
                SetOwnedContent(_permissionPanel, new PermissionPromptView
                {
                    DataContext = ShellPageViewModelFactory.FromPermissionPrompt(result.Response, LoadBrokerAuditAsync)
                });
                _chromeViewModel.IsPermissionPromptOpen = true;
            }

            await LoadBrokerAuditAsync();

            return ToCommandExecutionStatus(result);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            return new CommandExecutionStatus("failed", ex.Message);
        }
    }

    internal static CommandExecutionStatus ToCommandExecutionStatus(
        ShellCommandExecutionResult result)
    {
        return new CommandExecutionStatus(
            result.Response.State,
            result.StatusText,
            Output: result.Response.Summary);
    }

    private async IAsyncEnumerable<CommandExecutionStatus> ExecuteCommandStreamAsync(
        string commandId,
        JsonObject? args,
        string invocationId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (ShellCommandRouter.TryHandleShellCommandStream(
            commandId,
            moduleId => RefreshModuleStatusAsync(moduleId, cancellationToken),
            out var shellEvents))
        {
            await foreach (var evt in shellEvents.WithCancellation(cancellationToken))
            {
                SetStatus(evt.Message);
                yield return evt;
            }

            _chromeViewModel.IsCommandPaletteOpen = false;
            yield break;
        }

        await foreach (var result in _commandExecutionService.ExecuteStreamAsync(invocationId, commandId, args, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            SetStatus(result.StatusText);
            SetOwnedContent(_permissionPanel, null);
            _chromeViewModel.IsPermissionPromptOpen = false;
            if (result.RequiresPermissionPrompt && result.Event.FinalResponse is not null)
            {
                SetOwnedContent(_permissionPanel, new PermissionPromptView
                {
                    DataContext = ShellPageViewModelFactory.FromPermissionPrompt(result.Event.FinalResponse, LoadBrokerAuditAsync)
                });
                _chromeViewModel.IsPermissionPromptOpen = true;
            }

            yield return new CommandExecutionStatus(
                result.Event.State,
                result.StatusText,
                result.Event.Terminal,
                (int)result.Event.Sequence);
        }

        await LoadBrokerAuditAsync();
        if (_currentPage == NotificationsPage)
        {
            await LoadNotificationsPageAsync();
        }
    }

    private async Task<CommandCancellationStatus> CancelCommandAsync(string invocationId)
    {
        try
        {
            var result = await _commandExecutionService.CancelAsync(invocationId);
            SetStatus(result.Message);
            return new CommandCancellationStatus(result.Accepted, result.InvocationId, result.State, result.Message);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            return new CommandCancellationStatus(false, invocationId, "failed", ex.Message);
        }
    }

    private async Task RunPackageOperationAsync(string operation, string target)
    {
        try
        {
            var result = await _hostActions.RunPackageOperationAsync(operation, target);
            if (result.ShouldRefresh)
            {
                await LoadPackagesPageAsync();
                await LoadCommandsAsync(_searchBox.Text ?? "");
            }

            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async Task RestartRuntimeProcessAsync(string transportKind, string poolKey)
    {
        try
        {
            var result = await _hostActions.RestartRuntimeProcessAsync(transportKind, poolKey);
            SetStatus(result.StatusText);
            await LoadDiagnosticsPageAsync();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async Task SetRuntimeProcessRestartPolicyAsync(string transportKind, string poolKey, bool paused, DateTimeOffset? expiresAt = null, string? reason = null)
    {
        try
        {
            var result = await _hostActions.SetRuntimeProcessRestartPolicyAsync(transportKind, poolKey, paused, expiresAt, reason);
            SetStatus(result.StatusText);
            await LoadDiagnosticsPageAsync();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async Task SetModuleEnabledAsync(string moduleId, bool enabled, bool showDetail = false)
    {
        try
        {
            var result = await _hostActions.SetModuleEnabledAsync(moduleId, enabled);
            SetStatus(result.StatusText);
            await LoadCommandsAsync(_searchBox.Text ?? "");
            await LoadBrokerAuditAsync();
            if (showDetail)
            {
                await ShowModuleDetailPageAsync(moduleId);
            }
            else if (_currentPage == DashboardPage)
            {
                await LoadDashboardPageAsync();
            }
            else
            {
                await LoadModulesPageAsync();
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async Task RefreshModuleStatusAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        await ExecuteRuntimeCommandAsync($"{moduleId}.status.refresh", cancellationToken: cancellationToken);
        if (_currentPage == DashboardPage)
        {
            await LoadDashboardPageAsync();
        }
        else if (_currentPage == ModulesPage)
        {
            await ShowModuleDetailPageAsync(moduleId);
        }
    }
}
