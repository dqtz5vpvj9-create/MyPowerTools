namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private static string ResolveExternalToolDataDirectory(string toolId)
    {
        var hostDataRoot = Environment.GetEnvironmentVariable("MPT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(hostDataRoot))
        {
            hostDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyPowerTools");
        }

        var dataDirectory = Path.Combine(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(hostDataRoot)),
            "state",
            "tools",
            toolId);
        Directory.CreateDirectory(dataDirectory);
        return dataDirectory;
    }
}
