namespace MyPowerTools.WebSurface.Avalonia;

/// <summary>
/// Optional Shell-side controls for keeping a web surface alive while its tool page is inactive.
/// Kept outside the public tool SDK session contract so existing surface modules stay binary-compatible.
/// </summary>
public interface IPersistentWebSurfaceSession
{
    void Navigate(Uri source);
    void SetActive(bool active);
}
