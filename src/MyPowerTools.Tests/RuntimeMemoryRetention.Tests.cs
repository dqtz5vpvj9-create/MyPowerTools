using System.Runtime.CompilerServices;
using MyPowerTools.Runtime;
using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Tests;

public sealed class RuntimeMemoryRetentionTests
{
    [Fact]
    public void History_caps_previews_and_restores_full_unicode_output_on_demand()
    {
        var root = NewRoot();
        using var history = new CommandHistory(root, maxCount: 2, summaryLength: 64);
        var text = new string('x', 100_000) + "中文🙂\ud800";
        for (var i = 0; i < 3; i++)
        {
            history.Add(new Sdk.CommandRequest($"id-{i}", "command", new()), null, "running");
            history.Complete(Result($"id-{i}", text));
        }
        Assert.Equal(2, history.Count);
        Assert.All(history.List(includeFullOutput: false), item => Assert.True(item.Summary.Length <= 65));
        Assert.DoesNotContain(history.List(), item => item.InvocationId == "id-0");
        Assert.All(history.List(), item => Assert.Equal(text, item.Summary));
        Assert.Equal(2, Directory.GetFiles(root, "*.gz", SearchOption.AllDirectories).Length);
        history.Dispose();
        Assert.Empty(Directory.GetFiles(root, "*.gz", SearchOption.AllDirectories));
        Directory.Delete(root);
    }

    [Fact]
    public void History_releases_the_large_string_after_completion()
    {
        var root = NewRoot();
        using var history = new CommandHistory(root);
        var reference = CompleteHistory(history);
        AssertCollected(reference);
        Assert.Equal(200_000, Assert.Single(history.List()).Summary.Length);
        history.Dispose();
        Directory.Delete(root);
    }

    [Fact]
    public async Task Cache_preserves_live_task_identity_and_replays_after_large_result_is_collected()
    {
        var root = NewRoot();
        var now = DateTimeOffset.UtcNow;
        using var cache = new InvocationExecutionCache(3, TimeSpan.FromSeconds(5), () => now, root);
        var source = new TaskCompletionSource<Sdk.CommandExecutionResult>();
        var first = cache.GetOrAdd("pending", () => source.Task);
        Assert.Same(first, cache.GetOrAdd("pending", () => throw new Exception("Executed twice")));
        source.SetResult(Result("pending", new string('p', 200_000)));
        await first;
        Assert.Same(first, cache.GetOrAdd("pending", () => throw new Exception("Executed twice")));
        var reference = CompleteCache(cache);
        AssertCollected(reference);
        var replay = await cache.GetOrAdd("large", () => throw new Exception("Executed twice"));
        Assert.Equal(new string('q', 200_000), replay.Output);
        now = now.AddSeconds(6);
        cache.Cleanup();
        Assert.Equal(0, cache.Count);
        Assert.Empty(Directory.GetFiles(root, "*.gz", SearchOption.AllDirectories));
        cache.Dispose();
        Directory.Delete(root);
    }

    [Fact]
    public async Task Storage_failure_preserves_the_complete_cached_result()
    {
        var root = NewRoot();
        var blocked = Path.Combine(root, "file");
        File.WriteAllText(blocked, "blocks directory creation");
        using var cache = new InvocationExecutionCache(outputDirectory: blocked);
        var text = new string('z', 200_000);
        var first = cache.GetOrAdd("blocked", () => Task.FromResult(Result("blocked", text)));
        Assert.Equal(text, (await first).Output);
        Assert.Same(first, cache.GetOrAdd("blocked", () => throw new Exception("Executed twice")));
        cache.Dispose();
        Directory.Delete(root, recursive: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompleteHistory(CommandHistory history)
    {
        var text = new string('q', 200_000);
        history.Add(new Sdk.CommandRequest("history", "command", new()), null, "running");
        history.Complete(Result("history", text));
        return new WeakReference(text);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompleteCache(InvocationExecutionCache cache)
    {
        var text = new string('q', 200_000);
        cache.GetOrAdd("large", () => Task.FromResult(Result("large", text))).GetAwaiter().GetResult();
        return new WeakReference(text);
    }

    private static void AssertCollected(WeakReference reference)
    {
        for (var i = 0; i < 5 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(reference.IsAlive);
    }

    private static Sdk.CommandExecutionResult Result(string id, string output) => new(id, "command", "completed", true, output, null);
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
