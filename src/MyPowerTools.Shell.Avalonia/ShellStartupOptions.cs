namespace MyPowerTools.Shell.Avalonia;

public sealed record ShellStartupOptions(bool FocusCommandPalette)
{
    public static ShellStartupOptions Default { get; } = new(false);

    public static ShellStartupOptions FromArgs(IEnumerable<string>? args)
    {
        var focusCommandPalette = args?.Any(arg =>
            string.Equals(arg, "--command-palette", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--focus-command-palette", StringComparison.OrdinalIgnoreCase)) == true;

        return new ShellStartupOptions(focusCommandPalette);
    }
}
