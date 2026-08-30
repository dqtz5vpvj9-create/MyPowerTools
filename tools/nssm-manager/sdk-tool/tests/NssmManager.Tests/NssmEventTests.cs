using NssmManager.Windows;

namespace NssmManager.Tests;

public sealed class NssmEventTests
{
    [Fact]
    public void error_string_formats_win32_error() =>
        Assert.False(string.IsNullOrWhiteSpace(NssmEvent.error_string(2)));

    [Fact]
    public void message_string_reads_compiled_mc_semantics()
    {
        var id = NssmEvent.message_id("NSSM_MESSAGE_USAGE");
        var message = NssmEvent.message_string(id);
        Assert.StartsWith("NSSM:", message, StringComparison.Ordinal);
        Assert.Contains("nssm install", message, StringComparison.Ordinal);
        Assert.EndsWith("\r\n", message, StringComparison.Ordinal);
    }

    [Fact]
    public void print_message_applies_printf_placeholders()
    {
        var id = NssmEvent.message_id("NSSM_MESSAGE_USAGE");
        using var output = new StringWriter();
        NssmEvent.print_message(output, id, "2.24-101", "64-bit", "2017-04-26");
        Assert.Contains("Version 2.24-101 64-bit, 2017-04-26", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void log_event_accepts_up_to_fifteen_insertions()
    {
        if (Environment.GetEnvironmentVariable("NSSM_MANAGER_RUN_EVENTLOG_TESTS") != "1") return;
        NssmEvent.log_event(4, NssmEvent.message_id("NSSM_MESSAGE_USAGE"), Enumerable.Range(0, 20).Select(index => index.ToString()).ToArray());
    }

    [Fact]
    public void popup_message_formats_without_display_in_test_mode()
    {
        var previous = Environment.GetEnvironmentVariable("NSSM_MANAGER_TEST_NO_UI");
        Environment.SetEnvironmentVariable("NSSM_MANAGER_TEST_NO_UI", "1");
        try
        {
            Assert.Equal(0, NssmEvent.popup_message(IntPtr.Zero, 0, NssmEvent.message_id("NSSM_MESSAGE_USAGE"), "2.24", "64-bit", "date"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NSSM_MANAGER_TEST_NO_UI", previous);
        }
    }
}
