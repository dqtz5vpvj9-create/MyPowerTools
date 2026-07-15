using MyPowerTools.HostControl;

namespace MyPowerTools.Shell.Avalonia;

public sealed record ShellStartupOptions(
    bool FocusCommandPalette,
    string? ModulesRoot,
    string? DataRoot,
    bool RunnerBootstrap)
{
    public static ShellStartupOptions Default { get; } = new(false, null, null, true);

    public static ShellStartupOptions FromArgs(IEnumerable<string>? args)
    {
        var values = args?.ToArray() ?? [];
        var focusCommandPalette = values.Any(arg =>
            string.Equals(arg, "--command-palette", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--focus-command-palette", StringComparison.OrdinalIgnoreCase));
        var modulesRoot = GetOption(values, "--modules");
        var dataRoot = GetOption(values, "--data-root")
            ?? Environment.GetEnvironmentVariable(HostControlAuthTokenStore.DataRootEnvironmentVariable);
        var runnerBootstrap = !values.Any(arg =>
            string.Equals(arg, "--no-runner-bootstrap", StringComparison.OrdinalIgnoreCase));

        return new ShellStartupOptions(focusCommandPalette, modulesRoot, dataRoot, runnerBootstrap);
    }

    private static string? GetOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
