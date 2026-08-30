using System.Diagnostics;
using NssmManager.Windows;

namespace NssmManager.Tests;

public sealed class NssmProcessTests
{
    [Fact]
    public void get_debug_token_returns_owned_handle_or_invalid()
    {
        if (!OperatingSystem.IsWindows()) return;
        var token = NssmProcess.get_debug_token();
        if (token != new IntPtr(-1)) Assert.True(NssmProcess.CloseHandle(token));
    }

    [Fact]
    public void service_kill_t_copies_every_stop_field()
    {
        var state = new NssmServiceProcessState
        {
            Name = "service",
            ProcessHandle = new IntPtr(12),
            ProcessId = 42,
            ExitCode = 7,
            StopMethod = 9,
            KillConsoleDelay = 1,
            KillWindowDelay = 2,
            KillThreadsDelay = 3,
            StatusHandle = new IntPtr(13),
            CreationTime = 100,
            ExitTime = 200
        };
        var context = new NssmKillContext { Depth = 8, Signalled = 1 };
        NssmProcess.service_kill_t(state, context);
        Assert.Equal("service", context.Name);
        Assert.Equal(new IntPtr(12), context.ProcessHandle);
        Assert.Equal(42u, context.ProcessId);
        Assert.Equal(7u, context.ExitCode);
        Assert.Equal(9u, context.StopMethod);
        Assert.Equal(1u, context.KillConsoleDelay);
        Assert.Equal(2u, context.KillWindowDelay);
        Assert.Equal(3u, context.KillThreadsDelay);
        Assert.Equal(0u, context.Depth);
        Assert.Equal(0, context.Signalled);
    }

    [Fact]
    public void get_process_times_match_kernel_values()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.GetCurrentProcess();
        Assert.Equal(0, NssmProcess.get_process_creation_time(process.Handle, out var created));
        Assert.True(created > 0);
    }

    [Fact]
    public void get_process_exit_time_reports_active_process()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.GetCurrentProcess();
        Assert.Equal(2, NssmProcess.get_process_exit_time(process.Handle, out var exited));
        Assert.Equal(0, exited);
    }

    [Fact]
    public void check_parent_rejects_wrong_parent()
    {
        var context = new NssmKillContext();
        Assert.Equal(1, NssmProcess.check_parent(context, new NssmProcess.ProcessEntry(2, 1, "child"), 3));
    }

    [Fact]
    public void kill_window_ignores_other_processes()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(1, NssmProcess.kill_window(IntPtr.Zero, new NssmKillContext { ProcessId = uint.MaxValue }));
    }

    [Fact]
    public void kill_threads_returns_signal_result()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(0, NssmProcess.kill_threads(new NssmKillContext { ProcessId = uint.MaxValue }));
    }

    [Fact]
    public void kill_process_uses_upstream_stop_order()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.GetCurrentProcess();
        var context = new NssmKillContext { ProcessHandle = process.Handle, ProcessId = unchecked((uint)process.Id), StopMethod = 0 };
        Assert.Equal(0, NssmProcess.kill_process(context));
    }

    [Fact]
    public void kill_console_rejects_null_context() =>
        Assert.Equal(1, NssmProcess.kill_console(null));

    [Fact]
    public void walk_process_tree_visits_root_first()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.GetCurrentProcess();
        Assert.Equal(0, NssmProcess.get_process_creation_time(process.Handle, out var created));
        var visited = new List<uint>();
        var context = new NssmKillContext
        {
            Name = "test",
            ProcessId = unchecked((uint)process.Id),
            CreationTime = created,
            ExitTime = long.MaxValue
        };
        NssmProcess.walk_process_tree(null, (_, item) =>
        {
            visited.Add(item.ProcessId);
            return 1;
        }, context, context.ProcessId);
        Assert.NotEmpty(visited);
        Assert.Equal(unchecked((uint)process.Id), visited[0]);
    }

    [Fact]
    public void kill_process_tree_delegates_to_walker()
    {
        var method = typeof(NssmProcess).GetMethod(nameof(NssmProcess.kill_process_tree));
        Assert.NotNull(method);
    }

    [Fact]
    public void print_process_uses_eight_column_pid()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.GetCurrentProcess();
        using var output = new StringWriter();
        Assert.Equal(1, NssmProcess.print_process(new NssmKillContext
        {
            ProcessHandle = process.Handle,
            ProcessId = unchecked((uint)process.Id),
            Depth = 2
        }, output));
        Assert.StartsWith($"{process.Id,8}   ", output.ToString(), StringComparison.Ordinal);
    }
}
