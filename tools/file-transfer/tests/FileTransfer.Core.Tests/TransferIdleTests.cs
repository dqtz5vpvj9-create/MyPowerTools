using System.Diagnostics;
using FileTransfer.Core;

namespace FileTransfer.Tests;

[CollectionDefinition("Transfer idle timers", DisableParallelization = true)]
public sealed class TransferIdleTimerCollection;

[Collection("Transfer idle timers")]
public sealed class TransferIdleTests
{
    [Fact]
    public async Task HalfOpenReadReturnsToRetryInsteadOfHanging()
    {
        using var input = new PacedStream(Timeout.InfiniteTimeSpan);
        using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<IOException>(() => TransferFiles.CopyWithIdleTimeoutAsync(
            input, output, 1, null, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        Assert.Contains("没有进展", error.Message);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task ProgressRenewsBudgetRatherThanLimitingTotalDuration()
    {
        // Exercise the real cancellation timer without competing with the HTTP fixture suites.
        // Each read is comfortably inside its idle window, but the entire file must exceed it.
        var idleWindow = TimeSpan.FromSeconds(1);
        using var input = new PacedStream(TimeSpan.FromMilliseconds(125));
        using var output = new MemoryStream();
        var elapsed = Stopwatch.StartNew();
        await TransferFiles.CopyWithIdleTimeoutAsync(input, output, 12, null,
            idleWindow, CancellationToken.None);
        Assert.True(elapsed.Elapsed > idleWindow, "The file must outlast one idle window to prove the budget is renewed.");
        Assert.Equal(Enumerable.Repeat((byte)42, 12), output.ToArray());
    }

    [Fact]
    public async Task UserCancellationRemainsCancellation()
    {
        using var input = new PacedStream(Timeout.InfiniteTimeSpan);
        using var output = new MemoryStream();
        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TransferFiles.CopyWithIdleTimeoutAsync(
            input, output, 1, null, TimeSpan.FromSeconds(2), cancel.Token));
    }

    private sealed class PacedStream(TimeSpan delay) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            buffer.Span[0] = 42;
            return 1;
        }
    }
}
