namespace MyPowerTools.WebSurface.Avalonia;

/// <summary>
/// Instance-scoped visibility state shared by Shell overlays and native web child windows.
/// </summary>
public sealed class WebSurfaceOcclusionState
{
    private bool _isOccluded;

    public event EventHandler? Changed;

    public bool IsOccluded => _isOccluded;

    public void SetOccluded(bool value)
    {
        if (_isOccluded == value)
        {
            return;
        }

        _isOccluded = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
