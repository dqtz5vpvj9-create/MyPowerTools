namespace MyPowerTools.Shell.Avalonia;

// Shared App initialization also calls the desktop macOS integration hooks. Android owns
// startup in MainActivity and has neither a desktop entry point nor a macOS process/menu.
internal static class Program
{
    internal static void SetMacProcessName() { }
    internal static void SetMacApplicationIcon() { }
}
