namespace MyPowerTools.Shell.Avalonia.Views;

internal static class NativeWebSurfaceCoordinator
{
    private static bool _shellOverlayVisible;

    public static event EventHandler? VisibilityChanged;

    public static bool ShellOverlayVisible => _shellOverlayVisible;

    public static void SetShellOverlayVisible(bool visible)
    {
        if (_shellOverlayVisible == visible)
        {
            return;
        }
        _shellOverlayVisible = visible;
        VisibilityChanged?.Invoke(null, EventArgs.Empty);
    }
}
