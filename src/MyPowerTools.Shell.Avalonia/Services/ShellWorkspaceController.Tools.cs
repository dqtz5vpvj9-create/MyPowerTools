using Avalonia.Controls;
using MyPowerTools.Shell.Avalonia.Navigation;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    // No first-party tool IDs, tool service fields, or DeliveredToolIds set.
    // All tools (first-party and external) are loaded dynamically via the Tool Catalog
    // and the DotnetSurfaceLoader (dotnet-surface) or LoadExternalSdkToolAsync (web/native/headless).

    private async Task LoadHomePageAsync()
    {
        try
        {
            var tools = await _toolProducts.LoadToolCardsAsync(ShowToolPageAsync, null);
            var viewModel = new HomeViewModel(
                favoriteTools: [],
                recentTools: tools,
                activities: [],
                totalToolCount: tools.Count,
                browseTools: () => ShowPageAsync(ToolsPage),
                openActivity: () => ShowPageAsync(ActivityPage),
                refresh: RefreshHomePageAsync,
                retry: LoadHomePageAsync);
            SetOwnedContent(_contentHost, new HomeView { DataContext = viewModel });
            SetStatus($"{tools.Count} tools registered.");
        }
        catch (Exception ex)
        {
            var viewModel = new HomeViewModel(
                [],
                [],
                [],
                0,
                ToolProductState.Failed,
                ex.Message,
                browseTools: () => ShowPageAsync(ToolsPage),
                refresh: RefreshHomePageAsync,
                retry: LoadHomePageAsync);
            SetOwnedContent(_contentHost, new HomeView { DataContext = viewModel });
            SetStatus(ex.Message);
        }
    }

    private async Task LoadToolsPageAsync()
    {
        try
        {
            var tools = await _toolProducts.LoadToolCardsAsync(ShowToolPageAsync, null);
            var viewModel = new ToolCatalogViewModel(
                tools,
                refresh: RefreshToolsPageAsync,
                retry: LoadToolsPageAsync);
            SetOwnedContent(_contentHost, new ToolCatalogView { DataContext = viewModel });
            SetStatus($"{tools.Count} tools registered.");
        }
        catch (Exception ex)
        {
            var viewModel = new ToolCatalogViewModel(
                [],
                ToolProductState.Failed,
                ex.Message,
                refresh: RefreshToolsPageAsync,
                retry: LoadToolsPageAsync);
            SetOwnedContent(_contentHost, new ToolCatalogView { DataContext = viewModel });
            SetStatus(ex.Message);
        }
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

    private async Task ShowToolPageAsync(string toolId)
    {
        if (IsDisposed)
        {
            return;
        }

        var descriptor = await _toolProducts.LoadToolAsync(toolId);
        await ShowToolPageAsync(toolId, descriptor.PrimaryRouteId);
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

        BeginWorkspace();
        _currentPage = ToolsPage;
        _currentToolId = toolId;
        _currentToolRouteId = routeId;
        _chromeViewModel.SelectPage(ToolsPage);
        _navigation.Navigate(ShellRoute.ForTool(toolId, routeId), addToHistory: true);
        _chromeViewModel.IsCommandPaletteOpen = false;
        SetStatus($"Loading {toolId}");

        try
        {
            var descriptor = await _toolProducts.LoadToolAsync(toolId);

            // All tools — first-party and external — go through the dynamic surface loader.
            if (ShellToolProductService.IsSdkTool(descriptor))
            {
                await LoadExternalSdkToolAsync(descriptor, routeId);
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
        }
        catch (Exception ex)
        {
            SetOwnedContent(_contentHost, BuildUnavailablePage("Tool", ex.Message));
            SetStatus(ex.Message);
        }
    }
}
