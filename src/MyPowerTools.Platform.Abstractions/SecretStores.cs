using System.Collections.Concurrent;

namespace MyPowerTools.Platform.Abstractions;

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public Task<SecretReference> SaveAsync(string moduleId, string name, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(secret);

        var reference = SecretReference.Create(moduleId, name);
        _values[reference.Uri] = secret;
        return Task.FromResult(reference);
    }

    public Task<string?> ReadAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!reference.TryGetParts(out _, out _))
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(_values.TryGetValue(reference.Uri, out var value) ? value : null);
    }

    public Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.TryGetParts(out _, out _))
        {
            _values.TryRemove(reference.Uri, out _);
        }

        return Task.CompletedTask;
    }
}

public sealed class UnsupportedSecretStore : ISecretStore
{
    private readonly string _provider;
    private readonly string _message;

    public UnsupportedSecretStore(string provider, string message)
    {
        _provider = provider;
        _message = message;
    }

    public Task<SecretReference> SaveAsync(string moduleId, string name, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<SecretReference>(new PlatformNotSupportedException($"{_provider}: {_message}"));
    }

    public Task<string?> ReadAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
