using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Broker;

public sealed class ServiceBroker
{
    private readonly IServiceManager _services;
    private readonly AuditLog _audit;

    public ServiceBroker(IServiceManager services, AuditLog audit)
    {
        _services = services;
        _audit = audit;
    }

    public async Task<BrokerOperationResult> RestartAsync(string moduleId, string serviceName, string reason, CancellationToken cancellationToken)
    {
        _audit.Append(NewEntry(moduleId, "service.restart", "serviceUser", serviceName, reason, true, "requested", $"start {serviceName}"));
        var stop = await _services.StopAsync(serviceName, cancellationToken);
        if (!stop.Success)
        {
            _audit.Append(NewEntry(moduleId, "service.restart", "serviceUser", serviceName, reason, true, stop.State, stop.Message));
            return stop;
        }

        var start = await _services.StartAsync(serviceName, cancellationToken);
        _audit.Append(NewEntry(moduleId, "service.restart", "serviceUser", serviceName, reason, true, start.State, start.Message));
        return start;
    }

    private static BrokerAuditEntry NewEntry(string moduleId, string actionId, string level, string scope, string reason, bool requiresBroker, string result, string rollback)
    {
        return new BrokerAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, moduleId, actionId, level, scope, reason, requiresBroker, result, rollback);
    }
}

public sealed class NetworkBroker
{
    private readonly INetworkBroker _network;
    private readonly AuditLog _audit;
    private readonly PrivilegedBroker _privileged;

    public NetworkBroker(INetworkBroker network, AuditLog audit)
    {
        _network = network;
        _audit = audit;
        _privileged = new PrivilegedBroker(audit);
    }

    public async Task<BrokerOperationResult> ApplyAsync(string moduleId, PortProxyRule rule, string reason, CancellationToken cancellationToken)
    {
        var scope = $"{rule.ListenAddress}:{rule.ListenPort}->{rule.ConnectAddress}:{rule.ConnectPort}";
        _privileged.Evaluate("network.portproxy.apply", "elevated", reason, moduleId, scope);
        _audit.Append(NewEntry(moduleId, "network.portproxy.apply", "elevated", scope, reason, true, "requested", $"remove {scope}"));
        var result = await _network.ApplyPortProxyRuleAsync(rule, cancellationToken);
        _audit.Append(NewEntry(moduleId, "network.portproxy.apply", "elevated", scope, reason, true, result.State, result.Message));
        return result;
    }

    public async Task<BrokerOperationResult> RemoveAsync(string moduleId, PortProxyRule rule, string reason, CancellationToken cancellationToken)
    {
        var scope = $"{rule.ListenAddress}:{rule.ListenPort}";
        _privileged.Evaluate("network.portproxy.remove", "elevated", reason, moduleId, scope);
        _audit.Append(NewEntry(moduleId, "network.portproxy.remove", "elevated", scope, reason, true, "requested", $"apply {scope}"));
        var result = await _network.RemovePortProxyRuleAsync(rule, cancellationToken);
        _audit.Append(NewEntry(moduleId, "network.portproxy.remove", "elevated", scope, reason, true, result.State, result.Message));
        return result;
    }

    public async Task<BrokerOperationResult> ApplyChangeSetAsync(string moduleId, PortProxyChangeSet changeSet, string reason, CancellationToken cancellationToken)
    {
        if (changeSet.IsEmpty)
        {
            _audit.Append(NewEntry(moduleId, "network.portproxy.changeset", "elevated", "no changes", reason, true, "skipped", ""));
            return new BrokerOperationResult(true, "noop", "No portproxy changes were required.");
        }

        var scope = changeSet.Scope;
        _privileged.Evaluate("network.portproxy.changeset", "elevated", reason, moduleId, scope);
        _audit.Append(NewEntry(moduleId, "network.portproxy.changeset", "elevated", scope, reason, true, "requested", changeSet.RollbackSummary));
        var rollback = new Stack<PortProxyRollbackAction>();

        foreach (var rule in changeSet.Remove)
        {
            var result = await RemoveAsync(moduleId, rule, reason, cancellationToken);
            if (!result.Success)
            {
                return await RollbackFailedChangeSetAsync(moduleId, reason, scope, result, rollback, cancellationToken);
            }

            rollback.Push(new PortProxyRollbackAction("apply", rule));
        }

        foreach (var rule in changeSet.Apply)
        {
            var result = await ApplyAsync(moduleId, rule, reason, cancellationToken);
            if (!result.Success)
            {
                return await RollbackFailedChangeSetAsync(moduleId, reason, scope, result, rollback, cancellationToken);
            }

            rollback.Push(new PortProxyRollbackAction("remove", rule));
        }

        _audit.Append(NewEntry(moduleId, "network.portproxy.changeset", "elevated", scope, reason, true, "success", changeSet.RollbackSummary));
        return new BrokerOperationResult(true, "success", $"Applied {changeSet.Remove.Count} remove and {changeSet.Apply.Count} apply portproxy operation(s).", Guid.NewGuid().ToString("N"));
    }

    private async Task<BrokerOperationResult> RollbackFailedChangeSetAsync(
        string moduleId,
        string reason,
        string scope,
        BrokerOperationResult failedOperation,
        Stack<PortProxyRollbackAction> rollback,
        CancellationToken cancellationToken)
    {
        var rollbackReason = $"Rollback after failed portproxy changeset: {reason}";
        _audit.Append(NewEntry(moduleId, "network.portproxy.changeset", "elevated", scope, reason, true, failedOperation.State, failedOperation.Message));

        if (rollback.Count == 0)
        {
            return new BrokerOperationResult(false, failedOperation.State, failedOperation.Message);
        }

        _audit.Append(NewEntry(moduleId, "network.portproxy.rollback", "elevated", scope, rollbackReason, true, "requested", ""));
        var rollbackFailures = new List<string>();
        while (rollback.Count > 0)
        {
            var action = rollback.Pop();
            var result = action.Operation == "apply"
                ? await ApplyAsync(moduleId, action.Rule, rollbackReason, cancellationToken)
                : await RemoveAsync(moduleId, action.Rule, rollbackReason, cancellationToken);

            if (!result.Success)
            {
                rollbackFailures.Add(result.Message);
            }
        }

        if (rollbackFailures.Count > 0)
        {
            var message = $"{failedOperation.Message}; rollback failed: {string.Join("; ", rollbackFailures)}";
            _audit.Append(NewEntry(moduleId, "network.portproxy.rollback", "elevated", scope, rollbackReason, true, "rollback-failed", message));
            return new BrokerOperationResult(false, "rollback-failed", message);
        }

        _audit.Append(NewEntry(moduleId, "network.portproxy.rollback", "elevated", scope, rollbackReason, true, "rolled-back", failedOperation.Message));
        return new BrokerOperationResult(false, "rolled-back", $"{failedOperation.Message}; completed rollback.");
    }

    private static BrokerAuditEntry NewEntry(string moduleId, string actionId, string level, string scope, string reason, bool requiresBroker, string result, string rollback)
    {
        return new BrokerAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, moduleId, actionId, level, scope, reason, requiresBroker, result, rollback);
    }
}

public sealed record PortProxyChangeSet(IReadOnlyList<PortProxyRule> Apply, IReadOnlyList<PortProxyRule> Remove)
{
    public bool IsEmpty => Apply.Count == 0 && Remove.Count == 0;

    public string Scope
    {
        get
        {
            var remove = Remove.Select(rule => $"remove {rule.ListenAddress}:{rule.ListenPort}");
            var apply = Apply.Select(rule => $"apply {rule.ListenAddress}:{rule.ListenPort}->{rule.ConnectAddress}:{rule.ConnectPort}");
            return string.Join("; ", remove.Concat(apply));
        }
    }

    public string RollbackSummary
    {
        get
        {
            var restoreRemoved = Remove.Select(rule => $"apply {rule.ListenAddress}:{rule.ListenPort}->{rule.ConnectAddress}:{rule.ConnectPort}");
            var undoApplied = Apply.Select(rule => $"remove {rule.ListenAddress}:{rule.ListenPort}");
            return string.Join("; ", undoApplied.Concat(restoreRemoved));
        }
    }
}

public sealed record PortProxyRollbackAction(string Operation, PortProxyRule Rule);

public sealed class SecretBroker
{
    private readonly ISecretStore _secrets;
    private readonly AuditLog _audit;

    public SecretBroker(ISecretStore secrets, AuditLog audit)
    {
        _secrets = secrets;
        _audit = audit;
    }

    public async Task<SecretReference> SaveAsync(string moduleId, string name, string secret, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var reference = await _secrets.SaveAsync(moduleId, name, secret, cancellationToken);
            _audit.Append(NewEntry(moduleId, "secret.save", reference.Uri, reason, "success", "delete secretRef"));
            return reference;
        }
        catch (Exception ex)
        {
            _audit.Append(NewEntry(moduleId, "secret.save", SafeScope(moduleId, name), reason, "failed", ex.Message));
            throw;
        }
    }

    public async Task<string?> ReadAsync(string moduleId, SecretReference reference, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var value = await _secrets.ReadAsync(reference, cancellationToken);
            _audit.Append(NewEntry(moduleId, "secret.read", reference.Uri, reason, value is null ? "missing" : "found", ""));
            return value;
        }
        catch (Exception ex)
        {
            _audit.Append(NewEntry(moduleId, "secret.read", reference.Uri, reason, "failed", ex.Message));
            throw;
        }
    }

    public async Task DeleteAsync(string moduleId, SecretReference reference, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await _secrets.DeleteAsync(reference, cancellationToken);
            _audit.Append(NewEntry(moduleId, "secret.delete", reference.Uri, reason, "success", ""));
        }
        catch (Exception ex)
        {
            _audit.Append(NewEntry(moduleId, "secret.delete", reference.Uri, reason, "failed", ex.Message));
            throw;
        }
    }

    private static BrokerAuditEntry NewEntry(string moduleId, string actionId, string scope, string reason, string result, string rollback)
    {
        return new BrokerAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, moduleId, actionId, "sensitive", scope, reason, true, result, rollback);
    }

    private static string SafeScope(string moduleId, string name)
    {
        try
        {
            return SecretReference.Create(moduleId, name).Uri;
        }
        catch
        {
            return "invalid secret reference";
        }
    }
}

public sealed class AutostartBroker
{
    private readonly IAutostartService _autostart;
    private readonly AuditLog _audit;

    public AutostartBroker(IAutostartService autostart, AuditLog audit)
    {
        _autostart = autostart;
        _audit = audit;
    }

    public async Task<ServiceStatus> GetAsync(string moduleId, string id, string reason, CancellationToken cancellationToken)
    {
        _audit.Append(new BrokerAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, moduleId, "autostart.status", "user", id, reason, false, "requested", ""));
        var status = await _autostart.GetAsync(id, cancellationToken);
        _audit.Append(new BrokerAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, moduleId, "autostart.status", "user", id, reason, false, status.State, status.Detail));
        return status;
    }

    public async Task<BrokerOperationResult> EnableAsync(string moduleId, string id, string command, string reason, CancellationToken cancellationToken)
    {
        _audit.Append(new BrokerAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, moduleId, "autostart.enable", "user", id, reason, false, "requested", $"disable {id}"));
        var result = await _autostart.EnableAsync(id, command, cancellationToken);
        _audit.Append(new BrokerAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, moduleId, "autostart.enable", "user", id, reason, false, result.State, result.Message));
        return result;
    }

    public async Task<BrokerOperationResult> DisableAsync(string moduleId, string id, string reason, CancellationToken cancellationToken)
    {
        _audit.Append(new BrokerAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, moduleId, "autostart.disable", "user", id, reason, false, "requested", ""));
        var result = await _autostart.DisableAsync(id, cancellationToken);
        _audit.Append(new BrokerAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, moduleId, "autostart.disable", "user", id, reason, false, result.State, result.Message));
        return result;
    }
}
