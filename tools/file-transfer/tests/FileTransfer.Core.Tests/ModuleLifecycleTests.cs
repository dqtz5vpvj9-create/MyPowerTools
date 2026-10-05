using FileTransfer.MyPowerTools;

namespace FileTransfer.Tests;

public sealed class ModuleLifecycleTests
{
    [Fact]
    public async Task Host_can_dispose_module_before_initialization()
    {
        // The host unloads constructed instances even when initialization never completed.
        var module = new FileTransferModule();
        await module.DisposeAsync(CancellationToken.None);
    }
}
