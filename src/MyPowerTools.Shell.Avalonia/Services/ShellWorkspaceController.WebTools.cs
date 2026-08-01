using Avalonia.Controls;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.WebSurface.Avalonia;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private string? _activeWebToolKey;

    private sealed record CachedWebToolPage(
        string Key,
        string ToolId,
        string RouteId,
        ExternalSdkToolView View,
        ExternalSdkToolViewModel ViewModel,
        IMptWebSurfaceSession Session);

    private static string WebToolKey(string toolId, string routeId) => $"{toolId}\n{routeId}";

    private bool TryGetCachedWebTool(
        string toolId,
        string routeId,
        out CachedWebToolPage page) =>
        _cachedWebTools.TryGetValue(WebToolKey(toolId, routeId), out page!);

    private void CacheWebTool(
        string toolId,
        string routeId,
        ExternalSdkToolView view,
        ExternalSdkToolViewModel viewModel,
        IMptWebSurfaceSession session)
    {
        var key = WebToolKey(toolId, routeId);
        var page = new CachedWebToolPage(key, toolId, routeId, view, viewModel, session);
        _cachedWebTools.Add(key, page);
        SetWebSessionActive(session, false);
        view.IsVisible = false;
        _webSurfaceHost?.Children.Add(view);
        _chromeViewModel.SetWebToolOpenState(toolId, true);
    }

    private void ShowCachedWebTool(CachedWebToolPage page)
    {
        DeactivateCachedWebTools();
        SetOwnedContent(_contentHost, _webSurfaceHost is null ? page.View : null);
        _contentHost.IsVisible = _webSurfaceHost is null;
        page.View.IsVisible = true;
        if (_webSurfaceHost is not null)
        {
            _webSurfaceHost.IsVisible = true;
        }

        SetWebSessionActive(page.Session, true);
        _activeWebToolKey = page.Key;
        _chromeViewModel.HeaderContent = page.ViewModel;
        _chromeViewModel.SetWebToolOpenState(page.ToolId, true);
        _chromeViewModel.RenameOpenTool(page.ToolId, page.ViewModel.EditableTitle);
        SetStatus($"Opened {page.ViewModel.EditableTitle}.");
    }

    private void DeactivateCachedWebTools()
    {
        foreach (var page in _cachedWebTools.Values)
        {
            page.View.IsVisible = false;
            try
            {
                SetWebSessionActive(page.Session, false);
            }
            catch (ObjectDisposedException)
            {
            }
        }
        if (_webSurfaceHost is not null)
        {
            _webSurfaceHost.IsVisible = false;
        }
        _contentHost.IsVisible = true;
        _activeWebToolKey = null;
    }

    private bool IsCachedWebToolDataContext(object? dataContext) =>
        dataContext is not null &&
        _cachedWebTools.Values.Any(page => ReferenceEquals(page.ViewModel, dataContext));

    private bool HasOpenWebTool(string toolId) =>
        _cachedWebTools.Values.Any(page =>
            string.Equals(page.ToolId, toolId, StringComparison.OrdinalIgnoreCase));

    private string? ResolveOpenWebToolTitle(string toolId) =>
        _cachedWebTools.Values.FirstOrDefault(page =>
            string.Equals(page.ToolId, toolId, StringComparison.OrdinalIgnoreCase))?.ViewModel.EditableTitle;

    private async Task CloseWebToolAsync(string toolId)
    {
        var pages = _cachedWebTools.Values
            .Where(page => string.Equals(page.ToolId, toolId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (pages.Length == 0)
        {
            return;
        }

        var closedActivePage = pages.Any(page =>
            string.Equals(page.Key, _activeWebToolKey, StringComparison.OrdinalIgnoreCase));
        foreach (var page in pages)
        {
            _cachedWebTools.Remove(page.Key);
            try
            {
                SetWebSessionActive(page.Session, false);
            }
            catch (ObjectDisposedException)
            {
            }
            _webSurfaceHost?.Children.Remove(page.View);
            page.ViewModel.Dispose();
        }
        _chromeViewModel.SetWebToolOpenState(toolId, false);

        if (closedActivePage)
        {
            await ShowPageAsync(ToolsPage);
        }
        else
        {
            SetStatus($"Closed {toolId} web page.");
        }
    }

    private ExternalSdkToolView? GetCurrentExternalSdkToolView()
    {
        if (_activeWebToolKey is not null &&
            _cachedWebTools.TryGetValue(_activeWebToolKey, out var cached))
        {
            return cached.View;
        }
        return _contentHost.Content as ExternalSdkToolView;
    }

    private void DisposeCachedWebTools()
    {
        foreach (var page in _cachedWebTools.Values.ToArray())
        {
            try
            {
                SetWebSessionActive(page.Session, false);
            }
            catch (ObjectDisposedException)
            {
            }
            _webSurfaceHost?.Children.Remove(page.View);
            page.ViewModel.Dispose();
        }
        _cachedWebTools.Clear();
        _activeWebToolKey = null;
        if (_webSurfaceHost is not null)
        {
            _webSurfaceHost.Children.Clear();
            _webSurfaceHost.IsVisible = false;
        }
    }

    private static void SetWebSessionActive(IMptWebSurfaceSession session, bool active)
    {
        if (session is IPersistentWebSurfaceSession persistent)
        {
            persistent.SetActive(active);
        }
    }
}
