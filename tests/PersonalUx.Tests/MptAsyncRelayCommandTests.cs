using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;

namespace PersonalUx.Tests;

public sealed class MptAsyncRelayCommandTests
{
    [AvaloniaFact]
    public async Task Background_notification_is_delivered_on_the_UI_thread()
    {
        var command = new MptAsyncRelayCommand(() => Task.CompletedTask);
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        command.CanExecuteChanged += (_, _) => delivered.TrySetResult(Dispatcher.UIThread.CheckAccess());
        await Task.Run(command.NotifyCanExecuteChanged);
        Assert.True(await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
