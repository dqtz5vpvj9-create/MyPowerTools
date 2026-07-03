namespace MyPowerTools.Platform.Abstractions;

public sealed record ServiceStatus(string Name, string State, string Detail);
public sealed record PortProxyRule(string ListenAddress, int ListenPort, string ConnectAddress, int ConnectPort);
public sealed record BrokerOperationResult(bool Success, string State, string Message, string? RollbackId = null);
public sealed record SecretReference(string Uri)
{
    public const string Scheme = "secret://";

    public static SecretReference Create(string moduleId, string name)
    {
        ValidatePart(moduleId, nameof(moduleId));
        ValidatePart(name, nameof(name));
        return new SecretReference($"{Scheme}{moduleId}/{name}");
    }

    public bool TryGetParts(out string moduleId, out string name)
    {
        moduleId = "";
        name = "";
        if (!Uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = Uri[Scheme.Length..].Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !IsSafePart(parts[0]) || !IsSafePart(parts[1]))
        {
            return false;
        }

        moduleId = parts[0];
        name = parts[1];
        return true;
    }

    public static void ValidatePart(string value, string parameterName)
    {
        if (!IsSafePart(value))
        {
            throw new ArgumentException("Secret names must contain only letters, digits, dot, dash or underscore.", parameterName);
        }
    }

    private static bool IsSafePart(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_');
    }
}
public sealed record ProcessSnapshot(int ProcessId, string Name, string State, string Detail);
public sealed record DisplaySnapshot(
    string Id,
    string Name,
    string State,
    int Width,
    int Height,
    int RefreshRateHz,
    string Orientation,
    bool Primary,
    string Detail);
public sealed record DisplayProfileIntent(
    string ProfileId,
    string DisplayId,
    int? Brightness,
    int? ColorTemperature,
    string Reason);
public sealed record DisplayWriterStatus(bool Available, string State, string Message);
public sealed record TrayMenuItem(
    string Id,
    string Label,
    bool IsDefault = false,
    bool SeparatorBefore = false);
public sealed record TrayOptions(
    string AppId,
    string ToolTip,
    string? IconPath,
    IReadOnlyList<TrayMenuItem> MenuItems);
public sealed record TrayActionInvocation(string ActionId, DateTimeOffset InvokedAt);
public sealed record TrayStartResult(bool Success, string State, string Message);

public interface ITrayService : IAsyncDisposable
{
    string State { get; }
    Task<TrayStartResult> StartAsync(
        TrayOptions options,
        Func<TrayActionInvocation, CancellationToken, Task> actionHandler,
        CancellationToken cancellationToken);
}

public sealed class UnsupportedTrayService : ITrayService
{
    private readonly string _provider;
    private readonly string _message;

    public UnsupportedTrayService(string provider, string message)
    {
        _provider = provider;
        _message = message;
    }

    public string State => "unsupported";

    public Task<TrayStartResult> StartAsync(
        TrayOptions options,
        Func<TrayActionInvocation, CancellationToken, Task> actionHandler,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new TrayStartResult(false, "unsupported", $"{_provider}: {_message}"));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

public interface INotificationService
{
    Task PublishAsync(string title, string body, CancellationToken cancellationToken);
}

public interface IAutostartService
{
    Task<ServiceStatus> GetAsync(string id, CancellationToken cancellationToken);
    Task<BrokerOperationResult> EnableAsync(string id, string command, CancellationToken cancellationToken);
    Task<BrokerOperationResult> DisableAsync(string id, CancellationToken cancellationToken);
}

public interface IServiceManager
{
    Task<ServiceStatus> GetStatusAsync(string serviceName, CancellationToken cancellationToken);
    Task<BrokerOperationResult> StartAsync(string serviceName, CancellationToken cancellationToken);
    Task<BrokerOperationResult> StopAsync(string serviceName, CancellationToken cancellationToken);
}

public interface INetworkBroker
{
    Task<IReadOnlyList<PortProxyRule>> ListPortProxyRulesAsync(CancellationToken cancellationToken);
    Task<BrokerOperationResult> ApplyPortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken);
    Task<BrokerOperationResult> RemovePortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken);
}

public interface ISecretStore
{
    Task<SecretReference> SaveAsync(string moduleId, string name, string secret, CancellationToken cancellationToken);
    Task<string?> ReadAsync(SecretReference reference, CancellationToken cancellationToken);
    Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken);
}

public interface IProcessService
{
    Task<IReadOnlyList<ProcessSnapshot>> ListAsync(CancellationToken cancellationToken);
}

public interface IDisplayService
{
    Task<IReadOnlyList<DisplaySnapshot>> ListDisplaysAsync(CancellationToken cancellationToken);
    Task<DisplayWriterStatus> GetWriterStatusAsync(CancellationToken cancellationToken);
    Task<BrokerOperationResult> ApplyProfileAsync(DisplayProfileIntent intent, CancellationToken cancellationToken);
}
