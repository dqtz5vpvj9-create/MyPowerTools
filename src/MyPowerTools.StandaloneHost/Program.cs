using System.Runtime.CompilerServices;

namespace MyPowerTools.StandaloneHost;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = StandaloneCliOptions.Parse(args);
        var initialRoot = StandaloneCliOptions.DetermineInitialToolRoot(options);
        SharedRuntimeAssemblyResolver.Install(initialRoot);

        return RunHost(options, args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunHost(StandaloneCliOptions options, string[] rawArgs)
    {
        return StandaloneBootstrap.Run(options, rawArgs);
    }
}
