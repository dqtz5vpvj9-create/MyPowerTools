using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI.Controls;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private async Task LoadCommandsAsync(string query)
    {
        try
        {
            var viewModel = await _pageData.LoadCommandsAsync(
                query,
                (commandId, args, invocationId, cancellationToken) => ExecuteCommandStreamAsync(commandId, args, invocationId, cancellationToken),
                invocationId => CancelCommandAsync(invocationId));
            _commandPanel.Content = new CommandPaletteView
            {
                DataContext = viewModel
            };
        }
        catch (Exception ex)
        {
            _commandPanel.Content = new MptErrorState(ex.Message);
        }
    }

    private async Task LoadBrokerAuditAsync()
    {
        try
        {
            var viewModel = await _pageData.LoadBrokerAuditAsync();
            _auditPanel.Content = new BrokerAuditView
            {
                DataContext = viewModel
            };
        }
        catch (Exception ex)
        {
            _auditPanel.Content = new BrokerAuditView
            {
                DataContext = _pageData.CreateBrokerAuditError(ex.Message)
            };
        }
    }

    private Control BuildUnavailablePage(string title, string message)
    {
        return new UnavailablePageView
        {
            DataContext = new UnavailablePageViewModel(title, message)
        };
    }

    private async Task<CommandExecutionStatus> ExecuteCommandAsync(
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
            _permissionPanel.Content = null;
            _chromeViewModel.IsPermissionPromptOpen = false;
            if (result.RequiresPermissionPrompt)
            {
                _permissionPanel.Content = new PermissionPromptView
                {
                    DataContext = ShellPageViewModelFactory.FromPermissionPrompt(result.Response, LoadBrokerAuditAsync)
                };
                _chromeViewModel.IsPermissionPromptOpen = true;
            }

            await LoadBrokerAuditAsync();
            if (_currentPage == NotificationsPage)
            {
                await LoadNotificationsPageAsync();
            }

            return new CommandExecutionStatus(result.Response.State, result.StatusText);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            return new CommandExecutionStatus("failed", ex.Message);
        }
    }

    private async IAsyncEnumerable<CommandExecutionStatus> ExecuteCommandStreamAsync(
        string commandId,
        JsonObject? args,
        string invocationId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var result in _commandExecutionService.ExecuteStreamAsync(invocationId, commandId, args, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            SetStatus(result.StatusText);
            _permissionPanel.Content = null;
            _chromeViewModel.IsPermissionPromptOpen = false;
            if (result.RequiresPermissionPrompt && result.Event.FinalResponse is not null)
            {
                _permissionPanel.Content = new PermissionPromptView
                {
                    DataContext = ShellPageViewModelFactory.FromPermissionPrompt(result.Event.FinalResponse, LoadBrokerAuditAsync)
                };
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
}
