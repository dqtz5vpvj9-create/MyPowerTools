namespace MyPowerTools.Tests;

public sealed class ShellResidentLifecycleTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Shell_close_hides_resident_state_and_activation_waits_for_presentation()
    {
        var lifecycle = Read("src", "MyPowerTools.Shell.Avalonia", "MainWindow.Lifecycle.cs");
        var shellProgram = Read("src", "MyPowerTools.Shell.Avalonia", "Program.cs");
        var activation = Read(
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Services",
            "ShellActivationService.cs");
        var launcher = Read("src", "MyPowerTools.App", "Program.cs");

        Assert.Contains("protected override void OnClosing", lifecycle, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true", lifecycle, StringComparison.Ordinal);
        Assert.Contains("HideForResidentActivation", lifecycle, StringComparison.Ordinal);
        Assert.Contains("HideNativeWindowImmediately", lifecycle, StringComparison.Ordinal);
        Assert.Contains("ShowWindow(handle, SwHide)", lifecycle, StringComparison.Ordinal);
        Assert.Contains("BringResidentWindowToForeground", lifecycle, StringComparison.Ordinal);
        Assert.Contains("SetForegroundWindow(handle)", lifecycle, StringComparison.Ordinal);
        Assert.Contains("GetForegroundWindow() == handle", lifecycle, StringComparison.Ordinal);
        Assert.Contains("ForceResidentWindowToFront", lifecycle, StringComparison.Ordinal);
        Assert.Contains("SetWindowPos(handle, HwndTopmost", lifecycle, StringComparison.Ordinal);
        Assert.Contains("SetWindowPos(handle, HwndNotTopmost", lifecycle, StringComparison.Ordinal);
        Assert.Contains("DwmFlush", lifecycle, StringComparison.Ordinal);
        Assert.Contains("ShutdownMode.OnExplicitShutdown", shellProgram, StringComparison.Ordinal);
        Assert.Contains("PipeDirection.InOut", activation, StringComparison.Ordinal);
        Assert.Contains("ActivationAcknowledged", activation, StringComparison.Ordinal);
        Assert.Contains("PipeDirection.InOut", launcher, StringComparison.Ordinal);
        Assert.Contains("ActivationAcknowledgementTimeoutMilliseconds", launcher, StringComparison.Ordinal);
        Assert.Contains("GetNamedPipeServerProcessId", launcher, StringComparison.Ordinal);
        Assert.Contains("AllowSetForegroundWindow(shellProcessId)", launcher, StringComparison.Ordinal);
        Assert.Contains("CreateToolActivationPayload", launcher, StringComparison.Ordinal);
        Assert.Contains("TransferForegroundPermission(shellProcess.Id)", launcher, StringComparison.Ordinal);
        Assert.Contains("TransferForegroundPermission(client)", activation, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_prewarms_shell_and_explicit_exit_requests_permanent_shutdown()
    {
        var runner = Read("src", "MyPowerTools.Runner", "Program.cs");
        var shellProgram = Read("src", "MyPowerTools.Shell.Avalonia", "Program.cs");

        Assert.Contains("TryPrewarmShell", runner, StringComparison.Ordinal);
        Assert.Contains("--prewarm", runner, StringComparison.Ordinal);
        Assert.Contains("--shutdown-shell", runner, StringComparison.Ordinal);
        Assert.Contains("--prewarm", shellProgram, StringComparison.Ordinal);
        Assert.Contains("--shutdown-shell", shellProgram, StringComparison.Ordinal);
        Assert.Contains("ShellActivationRequest.Shutdown", shellProgram, StringComparison.Ordinal);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([Root, .. segments]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the MyPowerTools repository root.");
    }
}
