using System.Security.Cryptography;
using System.Text;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Linux;

/// <summary>A service-owned encrypted vault; no desktop, D-Bus or unlock prompt.</summary>
public sealed class LinuxServiceSecretStore : ISecretStore
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string _root;
    public LinuxServiceSecretStore(string? root = null) => _root = Path.GetFullPath(root
        ?? Environment.GetEnvironmentVariable("MPT_SECRET_VAULT_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "secrets"));

    public async Task<SecretReference> SaveAsync(string moduleId, string name, string secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var reference = SecretReference.Create(moduleId, name);
        var path = EntryPath(reference);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory();
        var key = await LoadKeyAsync(create: true, cancellationToken);
        var plain = Encoding.UTF8.GetBytes(secret);
        var payload = new byte[1 + 12 + 16 + plain.Length];
        payload[0] = 1;
        RandomNumberGenerator.Fill(payload.AsSpan(1, 12));
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(payload.AsSpan(1, 12), plain, payload.AsSpan(29), payload.AsSpan(13, 16), Encoding.UTF8.GetBytes(reference.Uri));
            await WriteAtomicAsync(path, payload, overwrite: true, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
        return reference;
    }

    public async Task<string?> ReadAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        var path = EntryPath(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_root)) return null;
        EnsureDirectory();
        byte[] payload;
        try { CheckPrivateFile(path); payload = await File.ReadAllBytesAsync(path, cancellationToken); }
        catch (FileNotFoundException) { return null; }
        if (payload.Length < 29 || payload[0] != 1) throw new InvalidDataException("Invalid encrypted credential format.");
        var key = await LoadKeyAsync(create: false, cancellationToken);
        var plain = new byte[payload.Length - 29];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(payload.AsSpan(1, 12), payload.AsSpan(29), payload.AsSpan(13, 16), plain, Encoding.UTF8.GetBytes(reference.Uri));
            return Encoding.UTF8.GetString(plain);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }

    public Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        var path = EntryPath(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(_root)) { EnsureDirectory(); File.Delete(path); }
        return Task.CompletedTask;
    }

    private string EntryPath(SecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!reference.TryGetParts(out _, out _)) throw new ArgumentException("Invalid secret reference.", nameof(reference));
        // Reversible encoding avoids treating legal names such as '..' as filesystem paths.
        return Path.Combine(_root, Convert.ToHexString(Encoding.UTF8.GetBytes(reference.Uri)) + ".enc");
    }

    private void EnsureDirectory()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The service vault requires Linux file permissions.");
        Directory.CreateDirectory(_root, PrivateDirectory);
        if (new DirectoryInfo(_root).LinkTarget is not null) throw new IOException("Credential directory must not be a symbolic link.");
        if ((File.GetUnixFileMode(_root) & (UnixFileMode)0x1FF) != PrivateDirectory) throw new IOException("Credential directory must have mode 0700.");
    }

    private static void CheckPrivateFile(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Credential files must not be symbolic links.");
        if (File.GetUnixFileMode(path) != PrivateFile) throw new IOException("Credential files must have mode 0600.");
    }

    private async Task<byte[]> LoadKeyAsync(bool create, CancellationToken token)
    {
        var path = Path.Combine(_root, "master.key");
        if (create && !File.Exists(path))
        {
            if (Directory.EnumerateFiles(_root, "*.enc").Any())
                throw new IOException("Service vault key is missing; restore the key before writing credentials.");
            // Publish a complete key using a no-overwrite rename. Concurrent first
            // writers all use the winning key; no one reads a partially written key.
            var fresh = RandomNumberGenerator.GetBytes(32);
            try
            {
                try { await WriteAtomicAsync(path, fresh, overwrite: false, token); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { CryptographicOperations.ZeroMemory(fresh); }
        }
        CheckPrivateFile(path);
        var key = await File.ReadAllBytesAsync(path, token);
        if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw new InvalidDataException("Invalid service vault key."); }
        return key;
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, bool overwrite, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = PrivateFile, Options = FileOptions.Asynchronous
            }))
            {
                await stream.WriteAsync(bytes, token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite);
        }
        finally { File.Delete(temporary); }
    }
}
