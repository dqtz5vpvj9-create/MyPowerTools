using System.Runtime.InteropServices;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

public sealed class MacKeychainSecretStore : ISecretStore
{
    private const string ServiceName = "com.mypowertools.secrets";
    private const int ItemNotFound = -25300;

    public Task<SecretReference> SaveAsync(
        string moduleId,
        string name,
        string secret,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureMacOS();
        var reference = SecretReference.Create(moduleId, name);
        ThrowIfFailed(MacNative.SaveKeychainSecret(ServiceName, Account(moduleId, name), secret), "save");
        return Task.FromResult(reference);
    }

    public Task<string?> ReadAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureMacOS();
        if (!reference.TryGetParts(out var moduleId, out var name))
        {
            throw new ArgumentException("Secret reference is invalid.", nameof(reference));
        }

        var status = MacNative.ReadKeychainSecret(ServiceName, Account(moduleId, name), out var value);
        if (status == ItemNotFound)
        {
            return Task.FromResult<string?>(null);
        }
        ThrowIfFailed(status, "read");
        try
        {
            return Task.FromResult<string?>(Marshal.PtrToStringUTF8(value));
        }
        finally
        {
            if (value != 0)
            {
                MacNative.Free(value);
            }
        }
    }

    public Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureMacOS();
        if (!reference.TryGetParts(out var moduleId, out var name))
        {
            throw new ArgumentException("Secret reference is invalid.", nameof(reference));
        }

        var status = MacNative.DeleteKeychainSecret(ServiceName, Account(moduleId, name));
        if (status != ItemNotFound)
        {
            ThrowIfFailed(status, "delete");
        }
        return Task.CompletedTask;
    }

    private static string Account(string moduleId, string name) => $"{moduleId}/{name}";

    private static void EnsureMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Keychain requires macOS.");
        }
    }

    private static void ThrowIfFailed(int status, string operation)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"macOS Keychain {operation} failed with OSStatus {status}.");
        }
    }
}
