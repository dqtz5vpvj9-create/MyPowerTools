using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Linux;

namespace MyPowerTools.Tests;

public sealed class LinuxSecretStoreTests
{
    [Theory]
    [InlineData("  secret $() ' \" Unicode 中文\n\n")]
    [InlineData("")]
    public async Task Helper_persists_across_instances_and_keeps_secret_out_of_arguments(string secret)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var helper = new Helper("""
            import json, pathlib, sys
            root = pathlib.Path(__file__).parent
            args = sys.argv[1:]
            (root / 'arguments').write_text(json.dumps(args))
            item = root / 'item'
            if args[0] == 'store': item.write_bytes(sys.stdin.buffer.read())
            elif args[0] == 'lookup':
                if not item.exists(): sys.exit(1)
                sys.stdout.buffer.write(item.read_bytes() + b'\n')
            elif args[0] == 'clear':
                if not item.exists(): sys.exit(1)
                item.unlink()
            """);
        ISecretStore store = new LinuxSecretStore(helper.Path);
        var reference = await store.SaveAsync("file-transfer", "identity", secret, CancellationToken.None);
        if (secret.Length > 0) Assert.DoesNotContain(secret, File.ReadAllText(System.IO.Path.Combine(helper.Directory, "arguments")));
        var fresh = new LinuxSecretStore(helper.Path);
        Assert.Equal(secret, await fresh.ReadAsync(reference, CancellationToken.None));
        await fresh.DeleteAsync(reference, CancellationToken.None);
        Assert.Null(await fresh.ReadAsync(reference, CancellationToken.None));
        await fresh.DeleteAsync(reference, CancellationToken.None);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Helper_errors_propagate_without_exposing_diagnostics(int exitCode)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var helper = new Helper($"import sys\nsys.stderr.write('sensitive diagnostic')\nsys.exit({exitCode})");
        var store = new LinuxSecretStore(helper.Path);
        var reference = SecretReference.Create("module", "name");
        var read = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(reference, CancellationToken.None));
        Assert.Contains($"exit status {exitCode}", read.Message);
        Assert.DoesNotContain("sensitive diagnostic", read.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync("module", "name", "secret", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(reference, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_helper_is_unsupported_and_invalid_references_are_rejected()
    {
        var store = new LinuxSecretStore(null);
        Assert.False(store.IsAvailable);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.SaveAsync("module", "name", "secret", CancellationToken.None));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.ReadAsync(SecretReference.Create("module", "name"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(new SecretReference("bad"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("module", "bad/name", "secret", CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_stops_running_helper_and_pre_cancelled_operations_never_start()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var helper = new Helper("import pathlib, time\n(pathlib.Path(__file__).parent / 'started').touch()\ntime.sleep(60)");
        var store = new LinuxSecretStore(helper.Path);
        var reference = SecretReference.Create("module", "name");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync("module", "name", "secret", cancelled.Token));
        Assert.False(File.Exists(System.IO.Path.Combine(helper.Directory, "started")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(reference, timeout.Token));
        Assert.True(File.Exists(System.IO.Path.Combine(helper.Directory, "started")));
    }

    private sealed class Helper : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine("/mnt/cache/data-cache", "mpt-secret-tests-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "secret-tool");

        public Helper(string source)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path, "#!/usr/bin/python3\n" + source + "\n");
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
