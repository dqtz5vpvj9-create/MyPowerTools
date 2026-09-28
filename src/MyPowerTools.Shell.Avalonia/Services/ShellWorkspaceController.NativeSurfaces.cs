namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private Func<CancellationToken, Task<string?>>? _scanConnectionCodeAsync;
    private Func<string, CancellationToken, Task<bool>>? _openNativeFileAsync;

    /// <summary>
    /// Connects native services before the host opens a tool surface. The Android entry point owns
    /// camera permissions and file URI grants; the shared Shell only forwards these capabilities.
    /// </summary>
    public void SetNativeSurfaceServices(
        Func<CancellationToken, Task<string?>>? scanConnectionCodeAsync,
        Func<string, CancellationToken, Task<bool>>? openFileAsync)
    {
        _scanConnectionCodeAsync = scanConnectionCodeAsync;
        _openNativeFileAsync = openFileAsync;
    }
}
