using System.Security.Cryptography;
using System.Text;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Linux;

namespace MyPowerTools.Tests;

public sealed class LinuxServiceSecretStoreTests : IDisposable
{
    private readonly string root = Path.Combine("/mnt/cache/data-cache", "mpt-service-vault-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("")]
    [InlineData("  中文 secret\n\n")]
    public async Task Persists_without_desktop_and_encrypts_values_with_private_permissions(string secret)
    {
        if (!OperatingSystem.IsLinux()) return;
        var store = new LinuxServiceSecretStore(root);
        var reference = await store.SaveAsync("file-transfer", "test", secret, default);
        Assert.Equal(secret, await new LinuxServiceSecretStore(root).ReadAsync(reference, default));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root) & (UnixFileMode)0x1FF);
        foreach (var path in Directory.GetFiles(root)) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        if (secret.Length > 0) Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(root, "*.enc").Single())));
        await store.DeleteAsync(reference, default);
        Assert.Null(await store.ReadAsync(reference, default));
    }
    [Fact]
    public async Task Concurrent_first_writes_share_one_complete_key()
    {
        if (!OperatingSystem.IsLinux()) return;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            var store = new LinuxServiceSecretStore(root);
            var reference = await store.SaveAsync("module", "key" + i, "value" + i, default);
            Assert.Equal("value" + i, await new LinuxServiceSecretStore(root).ReadAsync(reference, default));
        })));
    }
    [Fact]
    public async Task Corrupt_ciphertext_is_not_treated_as_missing_and_missing_key_is_not_recreated()
    {
        if (!OperatingSystem.IsLinux()) return;
        var store = new LinuxServiceSecretStore(root);
        var reference = await store.SaveAsync("module", "key", "value", default);
        var path = Directory.GetFiles(root, "*.enc").Single();
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.ReadAsync(reference, default));
        File.Delete(Path.Combine(root, "master.key"));
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync("module", "other", "new", default));
        Assert.False(File.Exists(Path.Combine(root, "master.key")));
    }
    [Fact]
    public async Task Dot_names_cannot_escape_vault_and_cross_entry_swap_fails_authentication()
    {
        if (!OperatingSystem.IsLinux()) return;
        var store = new LinuxServiceSecretStore(root);
        var first = await store.SaveAsync("..", "..", "one", default);
        var second = await store.SaveAsync("module", "other", "two", default);
        var a = Path.Combine(root, Convert.ToHexString(Encoding.UTF8.GetBytes(first.Uri)) + ".enc");
        var b = Path.Combine(root, Convert.ToHexString(Encoding.UTF8.GetBytes(second.Uri)) + ".enc");
        File.Copy(a, b, true);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.ReadAsync(second, default));
    }
    [Fact]
    public async Task Rejects_public_permissions_and_honors_cancellation()
    {
        if (!OperatingSystem.IsLinux()) return;
        var store = new LinuxServiceSecretStore(root);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync("module", "name", "secret", cancelled.Token));
        Assert.False(Directory.Exists(root));
        var reference = await store.SaveAsync("module", "name", "secret", default);
        File.SetUnixFileMode(Path.Combine(root, "master.key"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(reference, default));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
