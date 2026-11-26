namespace MyPowerTools.Shell.Avalonia.Services.Mobile;

/// <summary>
/// The services the phone shell needs. The default bundle reuses the live workspace instances
/// (catalog, page data, appearance) so the phone and the desktop shell share one tool catalog, one
/// preferences store and one theme preference; tests build the bundle with temporary paths.
/// </summary>
public sealed class MobileShellServices
{
    public MobileShellServices(
        ShellToolProductService toolProducts,
        IMobileDeviceService devices,
        ShellAppearanceService appearance,
        ShellPageDataService pageData,
        IMobileControlDeviceService? controlDevices = null)
    {
        ToolProducts = toolProducts ?? throw new ArgumentNullException(nameof(toolProducts));
        Devices = devices ?? throw new ArgumentNullException(nameof(devices));
        Appearance = appearance ?? throw new ArgumentNullException(nameof(appearance));
        PageData = pageData ?? throw new ArgumentNullException(nameof(pageData));
        ControlDevices = controlDevices ?? new MobileControlDeviceService();
        Catalog = new MobileToolCatalogService(ToolProducts);
    }

    public ShellToolProductService ToolProducts { get; }
    public IMobileDeviceService Devices { get; }
    public ShellAppearanceService Appearance { get; }
    public ShellPageDataService PageData { get; }

    /// <summary>Imported computers for phone-side tool control (G2); never a file-pairing device.</summary>
    public IMobileControlDeviceService ControlDevices { get; }

    public MobileToolCatalogService Catalog { get; }

    /// <summary>
    /// Reuses the workspace's own services and the file-transfer backed device service, so the phone
    /// and the desktop shell share one tool catalog, one preferences store and one device source.
    /// </summary>
    public static MobileShellServices CreateDefault(ShellWorkspaceController workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new MobileShellServices(
            workspace.ToolProducts,
            new MobileDeviceService(),
            workspace.Appearance,
            workspace.PageData);
    }
}
