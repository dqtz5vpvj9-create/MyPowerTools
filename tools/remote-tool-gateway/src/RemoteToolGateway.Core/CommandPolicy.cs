namespace RemoteToolGateway.Core;

public enum ConfirmationRequirement
{
    /// <summary>The command can run with the grant alone.</summary>
    None,

    /// <summary>The command must be confirmed on the desktop before it runs.</summary>
    Desktop,

    /// <summary>The command declares a confirmation mechanism the gateway cannot route; it fails explicitly.</summary>
    Unsupported
}

public sealed record ConfirmationDecision(ConfirmationRequirement Requirement, string Kind, string Reason)
{
    public static readonly ConfirmationDecision None = new(ConfirmationRequirement.None, "", "");
    public static readonly ConfirmationDecision Desktop = new(ConfirmationRequirement.Desktop, "desktop", "");
}

/// <summary>
/// Classifies catalog commands exactly once, before any HostControl call. It mirrors the fields
/// the Runner's RuntimeOperationPolicy and the Shell's dangerous-command gate already use; it does
/// not replace either of them, it only decides whether the desktop user must confirm a
/// remote-originated request first.
/// </summary>
public static class CommandPolicy
{
    private static readonly string[] SupportedApprovalKinds =
        ["none", "desktop", "danger", "elevated", "broker"];

    /// <summary>
    /// A command whose execution needs elevated rights or broker approval. An explicit
    /// <c>approval: "elevated"</c> is elevated too, so it is subject to the same allowElevated gate
    /// as <c>requiresElevation</c>.
    /// </summary>
    public static bool IsElevated(HostCommandDescriptor command)
    {
        if (command.RequiresElevation) return true;
        if (command.Constraints.Any(constraint =>
                string.Equals(constraint, "requiresElevatedWrites", StringComparison.OrdinalIgnoreCase))) return true;
        var execution = command.Execution;
        if (execution is null) return false;
        if (ReadBool(execution, "requiresElevatedWrites")) return true;
        if (string.Equals(ReadString(execution, "type"), "broker.request", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(ReadString(execution, "approval"), "elevated", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every approval kind the contract supports is handled explicitly. A supported kind always
    /// forces a desktop confirmation, an explicitly elevated approval is additionally gated by
    /// allowElevated, and an unknown kind fails closed instead of being treated as "no approval".
    /// </summary>
    public static ConfirmationDecision Evaluate(HostCommandDescriptor command)
    {
        var execution = command.Execution;
        var approval = ReadString(execution, "approval").Trim().ToLowerInvariant();
        switch (approval)
        {
            case "":
            case "none":
                break;
            case "desktop":
                return new ConfirmationDecision(ConfirmationRequirement.Desktop, "desktop", "此操作需要在电脑上确认。");
            case "danger":
                return new ConfirmationDecision(ConfirmationRequirement.Desktop, "danger", "此操作被标记为敏感操作，必须在电脑上确认。");
            case "elevated":
                return new ConfirmationDecision(ConfirmationRequirement.Desktop, "elevated", "此操作需要管理员权限，必须在电脑上确认。");
            case "broker":
                return new ConfirmationDecision(ConfirmationRequirement.Desktop, "broker", "此操作需要电脑端的权限确认。");
            default:
                return new ConfirmationDecision(
                    ConfirmationRequirement.Unsupported,
                    "unsupported:" + approval,
                    $"命令声明的确认方式（{approval}）无法由本机网关转发，已拒绝执行。");
        }

        if (string.Equals(ReadString(execution, "type"), "broker.request", StringComparison.OrdinalIgnoreCase) ||
            ReadBool(execution, "brokerApprovalOnly"))
        {
            return new ConfirmationDecision(ConfirmationRequirement.Desktop, "broker", "此操作需要电脑端的权限确认。");
        }

        if (IsElevated(command))
        {
            return new ConfirmationDecision(ConfirmationRequirement.Desktop, "elevated", "此操作需要管理员权限，必须在电脑上确认。");
        }

        if (IsDangerous(command.DangerLevel))
        {
            return new ConfirmationDecision(ConfirmationRequirement.Desktop, "danger", "此操作被标记为敏感操作，必须在电脑上确认。");
        }

        return ConfirmationDecision.None;
    }

    /// <summary>True when the catalog declares an approval kind the gateway knows how to route.</summary>
    public static bool IsSupportedApprovalKind(string approval) =>
        approval.Length == 0 || SupportedApprovalKinds.Contains(approval, StringComparer.OrdinalIgnoreCase);

    /// <summary>Same markers the Shell uses for its dangerous-command confirmation gate.</summary>
    public static bool IsDangerous(string dangerLevel) =>
        dangerLevel.Contains("danger", StringComparison.OrdinalIgnoreCase) ||
        dangerLevel.Contains("elevated", StringComparison.OrdinalIgnoreCase) ||
        dangerLevel.Contains("sensitive", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The gateway's own authorization and management commands are local-only: even a fully
    /// granted phone can never create grants, read tokens or resolve confirmations over HTTP.
    /// </summary>
    public static bool IsGatewayManagementCommand(string commandId, string moduleId, string gatewayModuleId) =>
        string.Equals(moduleId, gatewayModuleId, StringComparison.OrdinalIgnoreCase) ||
        commandId.StartsWith(gatewayModuleId + ".", StringComparison.OrdinalIgnoreCase);

    private static string ReadString(System.Text.Json.Nodes.JsonObject? json, string key)
    {
        if (json is null) return "";
        try { return json[key]?.GetValue<string>() ?? ""; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return ""; }
    }

    private static bool ReadBool(System.Text.Json.Nodes.JsonObject? json, string key)
    {
        if (json is null) return false;
        try { return json[key]?.GetValue<bool>() ?? false; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return false; }
    }
}
