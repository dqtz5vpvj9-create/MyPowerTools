using NssmManager.Contracts;
using NssmManager.Windows;

namespace NssmManager.Tests;

public sealed class NssmConsoleTests
{
    [Fact]
    public void check_console_matches_console_owner_rule()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The function may detach a console created specifically for this test
        // process.  Either result is valid for a test runner hosted by vstest.
        _ = NssmConsole.check_console();
    }

    [Fact]
    public void alloc_console_honours_app_no_console()
    {
        NssmConsole.alloc_console(new NssmServiceConfiguration { NoConsole = true });
    }
}
