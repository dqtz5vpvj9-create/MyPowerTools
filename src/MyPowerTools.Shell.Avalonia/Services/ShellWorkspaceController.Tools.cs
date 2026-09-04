using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Shell.Avalonia.Navigation;
using MyPowerTools.UI;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    // No first-party tool IDs, tool service fields, or delivered-tool-id set.
    // All tools (first-party and external) are loaded dynamically via the Tool Catalog
    // and the DotnetSurfaceLoader (dotnet-surface) or LoadExternalSdkToolAsync (web/native/headless).

    internal void ShowStartupPage()
    {
        ShowStartupPage(
            HomePage,
            "Loading your toolkit",
            "Loading Home");
    }

    internal void ShowCommandPaletteStartupPage()
    {
        ShowStartupPage(
            HomePage,
            "Start typing to search commands",
            "Loading commands");
        Interlocked.Exchange(ref _homeLoadDeferred, 1);
    }

    internal void ShowToolStartupPage(string toolId)
    {
        ShowStartupPage(
            ToolsPage,
            $"Opening {toolId}",
            $"Loading {toolId}");
    }

    private void ShowStartupPage(string page, string message, string status)
    {
        if (IsDisposed)
        {
            return;
        }

        BeginWorkspace();
        _currentPage = page;
        _currentToolId = "";
        _currentToolRouteId = "";
        _chromeViewModel.SelectPage(page);
        SetOwnedContent(_contentHost, BuildPageMessage(message));
        SetStatus(status);
    }

    internal static Control BuildPageMessage(string message)
    {
        return new TextBlock
        {
            Text = message,
            Margin = MptThemeTokens.PageMessageMargin,
            FontSize = MptThemeTokens.FontSizePageHeading,
            FontWeight = FontWeight.SemiBold
        };
    }

    private async Task LoadHomePageAsync()
    {
        var identity = _workspaceIdentity.Capture();
        try
        {
            var startupTools = Interlocked.Exchange(ref _startupToolDescriptors, null);
            var tools = startupTools is null
                ? await _toolProducts.LoadToolCardsAsync(ShowToolPageAsync, null)
                : _toolProducts.BuildToolCards(startupTools, ShowToolPageAsync, null);

            if (!_workspaceIdentity.IsCurrent(identity)) return;

            var viewModel = new HomeViewModel(
                favoriteTools: tools.Where(tool => tool.IsFavorite).ToArray(),
                recentTools: _toolProducts.RecentTools(tools),
                activities: [],
                totalToolCount: tools.Count,
                browseTools: () => ShowPageAsync(ToolsPage),
                openActivity: () => ShowPageAsync(ActivityPage),
                refresh: RefreshHomePageAsync,
                retry: LoadHomePageAsync,
                allTools: tools);
            SetOwnedContent(_contentHost, new HomeView { DataContext = viewModel });
            SetStatus($"{tools.Count} tools registered.");
            ShellStartupDiagnostics.Mark("home-content-bound");
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!IsDisposed)
                    {
                        SetDiscoveredTools(tools);
                    }
                },
                DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            if (IsStalePageFailure(nameof(LoadHomePageAsync), ex, identity)) return;

            var failure = ReportPageFailure(nameof(LoadHomePageAsync), ex);
            var viewModel = new HomeViewModel(
                [],
                [],
                [],
                0,
                ToolProductState.Failed,
                failure.Message,
                browseTools: () => ShowPageAsync(ToolsPage),
                refresh: RefreshHomePageAsync,
                retry: LoadHomePageAsync);
            SetOwnedContent(_contentHost, new HomeView { DataContext = viewModel });
        }
    }

    private async Task LoadToolsPageAsync()
    {
        var identity = _workspaceIdentity.Capture();
        try
        {
            var tools = await _toolProducts.LoadToolCardsAsync(ShowToolPageAsync, null);

            if (!_workspaceIdentity.IsCurrent(identity)) return;

            SetDiscoveredTools(tools);
            var viewModel = new ToolCatalogViewModel(
                tools,
                refresh: RefreshToolsPageAsync,
                retry: LoadToolsPageAsync);
            SetOwnedContent(_contentHost, new ToolCatalogView { DataContext = viewModel });
            SetStatus($"{tools.Count} tools registered.");
        }
        catch (Exception ex)
        {
            if (IsStalePageFailure(nameof(LoadToolsPageAsync), ex, identity)) return;

            var failure = ReportPageFailure(nameof(LoadToolsPageAsync), ex);
            var viewModel = new ToolCatalogViewModel(
                [],
                ToolProductState.Failed,
                failure.Message,
                refresh: RefreshToolsPageAsync,
                retry: LoadToolsPageAsync);
            SetOwnedContent(_contentHost, new ToolCatalogView { DataContext = viewModel });
        }
    }

    internal async Task ReconcileHomeToolsAsync(IReadOnlyList<HostProto.ToolDescriptor> tools)
    {
        if (IsDisposed)
        {
            return;
        }

        if (!string.Equals(_currentPage, HomePage, StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(_currentToolId))
        {
            SetDiscoveredTools(_toolProducts.BuildToolCards(tools, ShowToolPageAsync, null));
            return;
        }

        _startupToolDescriptors = tools;
        await ShowPageAsync(HomePage);
    }

    private void SetDiscoveredTools(IReadOnlyList<ToolCardViewModel> tools)
    {
        _chromeViewModel.SetDiscoveredTools(
            tools,
            ShowToolPageAsync,
            CloseWebToolAsync,
            HasOpenWebTool,
            ResolveOpenWebToolTitle);
    }

    private async Task RefreshHomePageAsync()
    {
        await _toolProducts.RefreshToolsAsync();
        await LoadHomePageAsync();
    }

    private async Task RefreshToolsPageAsync()
    {
        await _toolProducts.RefreshToolsAsync();
        await LoadToolsPageAsync();
    }

    private async Task RecordOpenedToolAsync(string toolId)
    {
        try
        {
            await _toolProducts.RecordOpenedAsync(toolId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only/full data directory must not prevent using the tool.
            SetStatus($"Tool opened; recent tools could not be saved: {ex.Message}");
        }
    }

    private async Task ShowToolPageAsync(string toolId)
    {
        if (IsDisposed)
        {
            return;
        }

        var descriptor = await TryLoadToolDescriptorAsync(toolId);
        if (descriptor is null)
        {
            return;
        }

        await ShowToolPageAsync(descriptor, descriptor.PrimaryRouteId);
    }

    internal async Task ActivateToolAsync(ToolActivationRequest activation)
    {
        ArgumentNullException.ThrowIfNull(activation);

        var descriptor = await TryLoadToolDescriptorAsync(activation.ToolId);
        if (descriptor is null)
        {
            return;
        }

        var routeId = string.IsNullOrWhiteSpace(activation.RouteId)
            ? descriptor.PrimaryRouteId
            : activation.RouteId;
        await ShowToolPageAsync(descriptor, routeId);
        if (GetCurrentExternalSdkToolView() is not { } externalView ||
            externalView.ManagedSurface is not IMptAvaloniaSurfaceActivationHandler handler)
        {
            SetStatus($"{activation.ToolId} does not handle external activations.");
            return;
        }

        var handled = await handler.ActivateAsync(activation);
        SetStatus(handled
            ? $"Activated {activation.ToolId}."
            : $"{activation.ToolId} could not resolve the activation target.");
    }

    /// <summary>
    /// Loads any tool (first-party or external) by its tool id. Navigation is fully dynamic — no
    /// hardcoded tool IDs, no switch on tool type. dotnet-surface tools load via
    /// <see cref="LoadExternalSdkToolAsync"/> using the DotnetSurfaceLoader; web/native/headless
    /// tools use the same external-SDK path with their respective surface handling.
    /// </summary>
    private async Task ShowToolPageAsync(string toolId, string routeId)
    {
        if (IsDisposed)
        {
            return;
        }

        var descriptor = await TryLoadToolDescriptorAsync(toolId);
        if (descriptor is null)
        {
            return;
        }

        await ShowToolPageAsync(descriptor, routeId);
    }

    private async Task<HostProto.ToolDescriptor?> TryLoadToolDescriptorAsync(string toolId)
    {
        var identity = _workspaceIdentity.Capture();
        try
        {
            return await _toolProducts.LoadToolAsync(toolId);
        }
        catch (Exception ex)
        {
            if (IsStalePageFailure(nameof(ShowToolPageAsync), ex, identity)) return null;

            var failure = ReportPageFailure(nameof(ShowToolPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage("Tool", failure.Message, retry: () => ShowToolPageAsync(toolId)));
            return null;
        }
    }

    private async Task ShowToolPageAsync(HostProto.ToolDescriptor descriptor, string routeId)
    {
        if (IsDisposed)
        {
            return;
        }

        var toolId = descriptor.ToolId;
        BeginWorkspace();
        _currentPage = ToolsPage;
        _currentToolId = toolId;
        _currentToolRouteId = routeId;
        _chromeViewModel.SelectTool(toolId);
        _navigation.Navigate(ShellRoute.ForTool(toolId, routeId), addToHistory: true);
        _chromeViewModel.IsCommandPaletteOpen = false;
        SetStatus($"Loading {toolId}");

        var identity = _workspaceIdentity.Capture();
        try
        {
            // All tools — first-party and external — go through the dynamic surface loader.
            if (ShellToolProductService.IsSdkTool(descriptor))
            {
                await LoadExternalSdkToolAsync(descriptor, routeId);
                await RecordOpenedToolAsync(toolId);
                return;
            }

            // Non-SDK tools (legacy descriptors without a surface kind) get a generic host page.
            var card = ShellToolProductService.ToCard(descriptor, ShowToolPageAsync, false);
            var workspaces = BuildDeliveredToolWorkspaces(descriptor);
            var viewModel = new ToolHostViewModel(
                card,
                workspaces,
                routeId,
                navigateRoute: (targetToolId, targetRouteId) =>
                {
                    BeginWorkspace(rebindCurrentContent: true);
                    _currentToolId = targetToolId;
                    _currentToolRouteId = targetRouteId;
                    _navigation.Navigate(ShellRoute.ForTool(targetToolId, targetRouteId), addToHistory: true);
                    SetStatus($"Opened {targetToolId} / {targetRouteId}");
                    return Task.CompletedTask;
                },
                browseAllTools: () => ShowPageAsync(ToolsPage),
                refresh: () => ShowToolPageAsync(toolId, _currentToolRouteId),
                retry: () => ShowToolPageAsync(toolId, _currentToolRouteId));
            SetOwnedContent(_contentHost, new ToolHostView { DataContext = viewModel });
            SetStatus($"{descriptor.Title} is registered.");
            await RecordOpenedToolAsync(toolId);
        }
        catch (Exception ex)
        {
            if (IsStalePageFailure(nameof(ShowToolPageAsync), ex, identity)) return;

            var failure = ReportPageFailure(nameof(ShowToolPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage("Tool", failure.Message, retry: () => ShowToolPageAsync(toolId, routeId)));
        }
    }
}
