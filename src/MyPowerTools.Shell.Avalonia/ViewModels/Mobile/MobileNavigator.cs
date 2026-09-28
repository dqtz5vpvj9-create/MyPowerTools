namespace MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

/// <summary>Phone page keys owned by the mobile shell (not the desktop workspace page keys).</summary>
public static class MobilePageKeys
{
    public const string Home = "mobile.home";
    public const string Tools = "mobile.tools";
    public const string Devices = "mobile.devices";
    public const string Activity = "mobile.activity";
    public const string Settings = "mobile.settings";
    public const string ToolDetail = "mobile.tool";
    public const string DeviceDetail = "mobile.device";

    /// <summary>The four一级入口 in prototype order.</summary>
    public static IReadOnlyList<string> Tabs { get; } = [Home, Tools, Devices, Activity];
}

/// <summary>Bottom sheet keys; the shell owns their content and their back handling.</summary>
public static class MobileSheetKeys
{
    public const string PairDevice = "sheet.pair";
    public const string LocalPairingCode = "sheet.pairing-code";
    public const string Relay = "sheet.relay";
    public const string Permissions = "sheet.permissions";
    public const string Appearance = "sheet.appearance";
    public const string About = "sheet.about";
    public const string PeerActions = "sheet.peer";
}

/// <summary>
/// What a phone page or sheet may ask the shell to do. View models stay free of visual-tree types;
/// the shell owns navigation, sheets, toasts and the tool-surface host.
/// </summary>
public interface IMobileNavigator
{
    /// <summary>Opens a real tool surface through the shared workspace loader.</summary>
    Task OpenToolSurfaceAsync(string toolId);

    /// <summary>
    /// Loads a tool surface and delivers one platform activation to it (the control surface reads
    /// <c>mypowertools://device-tool?...</c> from here).
    /// </summary>
    Task ActivateToolAsync(string toolId, string routeId, string activationUri);

    /// <summary>Pushes the phone tool detail page (used by the library rows).</summary>
    Task ShowToolDetailAsync(string toolId);

    /// <summary>Pushes the phone device detail page.</summary>
    Task ShowDeviceDetailAsync(string deviceId);

    /// <summary>Pushes a page; the current page stays on the back stack.</summary>
    Task ShowPageAsync(string pageKey);

    /// <summary>Pops one phone page (used by the back button inside a page).</summary>
    Task GoBackPageAsync();

    /// <summary>Switches to a top-level tab and clears the back stack.</summary>
    Task ShowRootPageAsync(string pageKey);

    Task ShowSheetAsync(string sheetKey, string? argument = null);

    Task CloseSheetAsync();

    void ShowToast(string message);

    /// <summary>Reloads the data behind the current page from the live sources.</summary>
    Task RefreshAsync();
}
