using NssmManager.Contracts;
using NssmManager.Supervisor;
using NssmManager.Windows;

namespace NssmManager.Tests;

public sealed class NssmConsoleTests
{
    [Fact]
    public void check_console_matches_console_owner_rule()
    {
        if (!OperatingSystem.IsWindows()) return;
        // check_console() detaches a console window this process owns so the CLI never
        // leaves a stray window behind.  Running that branch inside the test runner would
        // detach - and could close - the console the runner is using, so the owning case is
        // asserted through the ownership predicate and the detach branch is left to the CLI.
        if (NssmConsole.OwnsConsoleWindow())
        {
            Assert.True(NssmConsole.HasConsole());
            return;
        }
        Assert.Equal(NssmConsole.HasConsole(), NssmConsole.check_console());
    }

    [Fact]
    public void alloc_console_honours_app_no_console()
    {
        NssmConsole.alloc_console(new NssmServiceConfiguration { NoConsole = true });
        Assert.False(NssmConsole.ShouldAllocateConsole(new NssmServiceConfiguration { NoConsole = true }));
    }

    [Fact]
    public void alloc_console_never_targets_an_interactive_desktop()
    {
        // A session-0 service host keeps the upstream allocation; everywhere else the very
        // same AllocConsole() call would put a fresh console window on the user's desktop and
        // steal focus from whatever the user is doing, which is what the NSSM unit tests did.
        if (NssmConsole.IsServiceHost()) return;
        Assert.False(NssmConsole.ShouldAllocateConsole(new NssmServiceConfiguration { NoConsole = false }));
        Assert.False(NssmConsole.ShouldAllocateConsole(new NssmServiceConfiguration()));
    }

    [Fact]
    public void alloc_console_leaves_an_interactive_desktop_without_a_new_console()
    {
        if (!OperatingSystem.IsWindows() || NssmConsole.IsServiceHost()) return;
        var hadConsole = NssmConsole.HasConsole();
        try
        {
            NssmConsole.alloc_console(new NssmServiceConfiguration { NoConsole = false });
            Assert.Equal(hadConsole, NssmConsole.HasConsole());
        }
        finally
        {
            // Only clean up a console this test created; freeing the runner's own console
            // would detach it.
            if (!hadConsole && NssmConsole.HasConsole()) NssmConsole.free_console();
        }
    }

    [Fact]
    public void application_creation_flags_never_ask_for_a_visible_console_on_a_desktop()
    {
        Assert.Equal(0u, NativeChildProcess.ConsoleCreationFlags(new NssmServiceConfiguration { NoConsole = true }));
        var flags = NativeChildProcess.ConsoleCreationFlags(new NssmServiceConfiguration { NoConsole = false });
        if (NssmConsole.IsServiceHost())
        {
            // A session-0 service host keeps the upstream CREATE_NEW_CONSOLE contract; its
            // console window lives on an invisible window station.
            Assert.Equal(NativeChildProcess.CreateNewConsole, flags & NativeChildProcess.CreateNewConsole);
            return;
        }
        // An interactive host must never let CreateProcess paint a console window on the
        // user's desktop while the test suite is running.
        Assert.Equal(0u, flags & NativeChildProcess.CreateNewConsole);
        Assert.Equal(NativeChildProcess.CreateNoWindow, flags & NativeChildProcess.CreateNoWindow);
    }
}
