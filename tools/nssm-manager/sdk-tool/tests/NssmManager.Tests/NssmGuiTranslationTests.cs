using NssmManager.Tool;

namespace NssmManager.Tests;

public sealed class NssmGuiTranslationTests
{
    [Fact]
    public void frontend_rewrite_matches_gui_contract()
    {
        Assert.Equal("*.exe;*.bat;*.cmd", NssmManagerViewModel.browse_filter(0));
        Assert.Equal(".", NssmManagerViewModel.browse_filter(1));
        Assert.Equal("*.*", NssmManagerViewModel.browse_filter(99));
        Assert.True(NssmManagerViewModel.browse_hook("INIT"));
        Assert.False(NssmManagerViewModel.browse_hook("COMMAND"));
        Assert.Equal(19, NssmManagerViewModel.hook_env("Start", "Pre", 512, out var hookName));
        Assert.Equal("NSSM_HOOK_Start_Pre", hookName);
        Assert.Equal(-1, NssmManagerViewModel.hook_env("Start", "Pre", 5, out hookName));
        Assert.Empty(hookName);
        uint number = 1500;
        NssmManagerViewModel.check_number("2500", ref number);
        Assert.Equal(2500u, number);
        NssmManagerViewModel.check_number("invalid", ref number);
        Assert.Equal(2500u, number);
        Assert.Equal(@"C:\logs\stdout.log", NssmManagerViewModel.check_io("stdout", @"C:\logs\stdout.log", 32767));
        Assert.Throws<ArgumentException>(() => NssmManagerViewModel.check_io("stdout", "12345", 5));
    }
}
