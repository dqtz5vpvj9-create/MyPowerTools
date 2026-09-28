using Avalonia.Controls;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia.Services;

/// <summary>
/// Phone adaptation points for the shared workspace. The phone shell renders its own 常用/工具/设备/
/// 动态 pages and only borrows the workspace for the things that must not be duplicated: loading a
/// real tool surface through the existing catalog and surface loader, the permission prompt and
/// command palette overlays, the tool preferences store, and share activations.
/// </summary>
public sealed partial class ShellWorkspaceController
{
    /// <summary>The live catalog/preferences service the desktop pages already use.</summary>
    internal ShellToolProductService ToolProducts => _toolProducts;

    /// <summary>The live page data service (used for the real broker audit history on the phone).</summary>
    internal ShellPageDataService PageData => _pageData;

    /// <summary>The live appearance preference, so the phone theme switch persists like the desktop one.</summary>
    internal ShellAppearanceService Appearance => _appearance;

    /// <summary>
    /// The exact hosts the workspace writes pages and overlays into. The phone shell must render
    /// these instances; a second set of controls would stay blank while navigation "worked".
    /// </summary>
    internal ContentControl MobilePageHost => _contentHost;

    internal ContentControl MobileCommandPanel => _commandPanel;

    internal ContentControl MobilePermissionPanel => _permissionPanel;

    internal ContentControl MobileAuditPanel => _auditPanel;

    /// <summary>True while a tool surface owns the workspace content host (loading or loaded).</summary>
    public bool IsToolSurfaceActive => IsToolPageActive;

    /// <summary>
    /// Starts the workspace the way the phone shell needs it: catalogs, shortcuts and event monitors
    /// come up, but the desktop Home page is never loaded because the phone renders 常用 itself. The
    /// Tools page keeps the workspace on a page that only rebuilds a catalog view on a refresh.
    /// </summary>
    internal async Task OpenForMobileShellAsync()
    {
        _currentPage = ToolsPage;
        await OpenAsync();
    }

    /// <summary>
    /// Opens a real tool surface by tool id through the production loader (dotnet surface, web
    /// surface or generic host page). Used by the phone tool library and the 常用 hero action.
    /// </summary>
    internal async Task OpenToolSurfaceAsync(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return;
        }

        await ShowToolPageAsync(toolId);
    }

    /// <summary>
    /// Releases the tool surface so the phone can return to its own pages. The next open loads a
    /// fresh surface, and a later share activation loads one instead of forwarding into a surface the
    /// user already left.
    /// </summary>
    internal void CloseToolSurface()
    {
        if (IsDisposed)
        {
            return;
        }

        _activeSurfaceTargets.Clear();
        _currentToolId = "";
        _currentToolRouteId = "";
        SetOwnedContent(_contentHost, null);
    }
}
