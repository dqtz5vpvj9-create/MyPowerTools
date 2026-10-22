using System.Runtime.InteropServices;
using NssmManager.Contracts;

namespace NssmManager.Windows;

/// <summary>Direct managed translation of console.cpp.</summary>
public static class NssmConsole
{
    [NssmUpstreamFunction("src/console.cpp", 4, "bool check_console()", "NssmConsoleTests.check_console_matches_console_owner_rule")]
    public static bool check_console()
    {
        if (!OperatingSystem.IsWindows()) return !Console.IsInputRedirected;
        var console = GetConsoleWindow();
        if (console == IntPtr.Zero) return false;
        if (GetWindowThreadProcessId(console, out var processId) == 0) return false;
        if (GetCurrentProcessId() != processId) return true;
        FreeConsole();
        return false;
    }

    [NssmUpstreamFunction("src/console.cpp", 24, "void alloc_console(nssm_service_t *service)", "NssmConsoleTests.alloc_console_honours_app_no_console")]
    public static void alloc_console(NssmServiceConfiguration service)
    {
        if (!ShouldAllocateConsole(service)) return;
        AllocConsole();
    }

    public static void free_console()
    {
        if (OperatingSystem.IsWindows()) _ = FreeConsole();
    }

    /// <summary>
    /// True when the upstream alloc_console() would run here. Upstream only ever calls it
    /// inside an SCM-hosted service, where the freshly allocated console lives on the
    /// invisible session-0 window station. In an interactive host the very same call would
    /// paint a console window on the user's desktop and steal focus - which is what the
    /// NSSM unit tests did - so console allocation is refused there and the application is
    /// given an own windowless console instead (NativeChildProcess.ConsoleCreationFlags).
    /// </summary>
    public static bool ShouldAllocateConsole(NssmServiceConfiguration service) =>
        !service.NoConsole && OperatingSystem.IsWindows() && IsServiceHost();

    /// <summary>
    /// True when this process runs on a non-interactive window station, i.e. the Service
    /// Control Manager started it. Upstream NSSM only reaches alloc_console() and
    /// CREATE_NEW_CONSOLE from such a host, where a console window can never be seen.
    /// Interactive hosts - the NSSM unit tests and the dev ServiceManager - must never
    /// open a console window on the user's desktop.
    /// </summary>
    public static bool IsServiceHost() => !Environment.UserInteractive;

    /// <summary>True when this process is already attached to a console, visible or not.</summary>
    public static bool HasConsole() => OperatingSystem.IsWindows() && GetConsoleWindow() != IntPtr.Zero;

    /// <summary>
    /// True when the console window this process is attached to belongs to this process.
    /// That is the case where the upstream check_console() detaches with FreeConsole().
    /// </summary>
    public static bool OwnsConsoleWindow()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var console = GetConsoleWindow();
        if (console == IntPtr.Zero) return false;
        return GetWindowThreadProcessId(console, out var processId) != 0 && processId == GetCurrentProcessId();
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}
