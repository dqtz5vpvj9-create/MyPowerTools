using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Mac;

namespace MyPowerTools.Tests;

public sealed class MacGlobalHotkeyServiceTests
{
    [Fact]
    public async Task IdleEventLoopProcessesRegistrationAndShutdownWithoutKeyboardInput()
    {
        if (!OperatingSystem.IsMacOS()) return;

        await using var service = new MacGlobalHotkeyService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = new HotkeyRegistration("test.first", "Ctrl+Alt+F18", "global", "event loop regression");
        Assert.True((await service.RegisterAsync(first, timeout.Token)).Success);
        var conflict = await service.RegisterAsync(first with { Id = "test.second" }, timeout.Token);
        Assert.False(conflict.Success);
        Assert.Equal("conflict", conflict.State);
        Assert.True((await service.UnregisterAsync(first.Id, timeout.Token)).Success);
        Assert.True((await service.RegisterAsync(first with { Id = "test.second" }, timeout.Token)).Success);
        await service.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        var disposed = await service.RegisterAsync(first, timeout.Token);
        Assert.Equal("disposed", disposed.State);
    }
}
