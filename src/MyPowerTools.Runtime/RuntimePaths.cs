namespace MyPowerTools.Runtime;

public sealed record RuntimePaths(string Root, string Settings, string Logs, string State, string Packages)
{
    public static RuntimePaths CreateDefault()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");
        return Create(root);
    }

    public static RuntimePaths Create(string root)
    {
        root = Path.GetFullPath(root);
        var paths = new RuntimePaths(
            root,
            Path.Combine(root, "settings"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "state"),
            Path.Combine(root, "packages"));

        Directory.CreateDirectory(paths.Root);
        Directory.CreateDirectory(paths.Settings);
        Directory.CreateDirectory(paths.Logs);
        Directory.CreateDirectory(paths.State);
        Directory.CreateDirectory(paths.Packages);
        return paths;
    }
}
