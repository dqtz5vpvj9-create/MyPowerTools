using MyPowerTools.HostControl;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class ShellHostActionService
{
    public async Task<ShellPackageActionResult> RunPackageOperationAsync(
        string operation,
        string target,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new ShellPackageActionResult($"{operation}: target is required.", ShouldRefresh: false);
        }

        using var client = HostControlClient.ForDefaultEndpoint();
        var result = operation switch
        {
            "install" => await client.InstallPackageAsync(target, cancellationToken),
            "rollback" => await client.RollbackPackageAsync(target, cancellationToken),
            "repair" => await client.RepairPackageAsync(target, cancellationToken),
            "uninstall" => await client.UninstallPackageAsync(target, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported package operation: {operation}")
        };

        return new ShellPackageActionResult(FormatPackageOperationStatus(result), ShouldRefresh: true);
    }

    public async Task<ShellActionResult> RestartRuntimeProcessAsync(
        string transportKind,
        string poolKey,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var result = await client.RestartRuntimeProcessAsync(transportKind, poolKey, cancellationToken);
        return new ShellActionResult($"{result.State}: {result.Message}");
    }

    public async Task<ShellActionResult> SetRuntimeProcessRestartPolicyAsync(
        string transportKind,
        string poolKey,
        bool paused,
        DateTimeOffset? expiresAt = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var result = await client.SetRuntimeProcessRestartPolicyAsync(
            transportKind,
            poolKey,
            paused,
            reason ?? "Shell Diagnostics action",
            cancellationToken,
            source: "shell",
            expiresAt: expiresAt);
        return new ShellActionResult($"{result.State}: {result.Message}");
    }

    public async Task<ShellActionResult> SetModuleEnabledAsync(
        string moduleId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var detail = await client.SetModuleEnabledAsync(moduleId, enabled, cancellationToken);
        return new ShellActionResult($"{detail.DisplayName} {(enabled ? "enabled" : "disabled")}");
    }

    private static string FormatPackageOperationStatus(HostProto.PackageOperationResult result)
    {
        if (result.Issues.Count > 0)
        {
            return $"{result.Operation} {result.PackageId}: {result.Issues[0].Severity}: {result.Issues[0].Message}";
        }

        return $"{result.Operation} {result.PackageId}: {result.Message}";
    }
}

public sealed record ShellActionResult(string StatusText);

public sealed record ShellPackageActionResult(string StatusText, bool ShouldRefresh);
