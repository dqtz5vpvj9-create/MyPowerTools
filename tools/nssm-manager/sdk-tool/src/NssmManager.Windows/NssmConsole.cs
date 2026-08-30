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
        if (service.NoConsole || !OperatingSystem.IsWindows()) return;
        AllocConsole();
    }

    public static void free_console()
    {
        if (OperatingSystem.IsWindows()) _ = FreeConsole();
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
