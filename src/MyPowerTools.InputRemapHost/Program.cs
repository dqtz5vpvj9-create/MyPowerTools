using MyPowerTools.Broker;

var dataRoot = GetOption(args, "--data-root")
    ?? Environment.GetEnvironmentVariable("MPT_DATA_ROOT")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyPowerTools");

if (!OperatingSystem.IsWindows())
{
    return 0;
}

using var remapper = new WindowsWinSpaceShiftRemapper(dataRoot);
remapper.Start();
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;

static string? GetOption(string[] commandLine, string name)
{
    for (var index = 0; index + 1 < commandLine.Length; index++)
    {
        if (string.Equals(commandLine[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return commandLine[index + 1];
        }
    }

    return null;
}
