using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Android.Tests;

public sealed class AndroidUnsupportedServicesTests
{
    /// <summary>Provider names that belong to the desktop fallback packs, not to Android.</summary>
    private static readonly string[] DesktopOnlyTerms =
    [
        "linux", "wayland", "x11", "systemd", "polkit", "appindicator", "freedesktop",
        "nftables", "iptables", "procfs", "secret service", "webkitgtk", "ddc"
    ];

    [Fact]
    public async Task Unsupported_providers_describe_android_instead_of_desktop_fallbacks()
    {
        var messages = new List<string>();
        var display = AndroidUnsupportedServices.Display;
        messages.AddRange((await display.ListDisplaysAsync(CancellationToken.None)).Select(item => item.Detail));
        messages.Add((await display.GetWriterStatusAsync(CancellationToken.None)).Message);
        messages.Add((await display.ApplyProfileAsync(new DisplayProfileIntent("profile", "display", null, null, "test"), CancellationToken.None)).Message);
        messages.Add((await AndroidUnsupportedServices.Tray.StartAsync(
            new TrayOptions("mpt", "MyPowerTools", null, []), (_, _) => Task.CompletedTask, CancellationToken.None)).Message);
        messages.Add((await AndroidUnsupportedServices.KeyboardShortcuts.SendAsync("Ctrl+C", CancellationToken.None)).Message);
        messages.Add((await AndroidUnsupportedServices.Autostart.GetAsync("mpt", CancellationToken.None)).Detail);
        messages.Add((await AndroidUnsupportedServices.Autostart.EnableAsync("mpt", "command", CancellationToken.None)).Message);
        messages.Add((await AndroidUnsupportedServices.Services.GetStatusAsync("mpt", CancellationToken.None)).Detail);
        messages.Add((await AndroidUnsupportedServices.Network.ApplyPortProxyRuleAsync(new PortProxyRule("0.0.0.0", 1, "127.0.0.1", 2), CancellationToken.None)).Message);
        messages.Add((await AndroidUnsupportedServices.Hotkeys.RegisterAsync(new HotkeyRegistration("id", "Ctrl+Alt+M", "global", "test"), CancellationToken.None)).Message);
        messages.Add((await AndroidUnsupportedServices.Privileges.EvaluateAsync(new PrivilegeRequest("action", "elevated", "test"), CancellationToken.None)).Message);
        messages.AddRange((await AndroidUnsupportedServices.Processes.ListAsync(CancellationToken.None)).Select(item => item.Detail));

        Assert.NotEmpty(messages);
        Assert.All(messages, message =>
        {
            Assert.False(string.IsNullOrWhiteSpace(message));
            foreach (var term in DesktopOnlyTerms)
            {
                Assert.DoesNotContain(term, message, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    [Fact]
    public void Unsupported_providers_keep_the_mpt_state_contract()
    {
        Assert.Equal("unsupported", AndroidUnsupportedServices.Tray.State);
    }
}
