using System.Text.Json.Nodes;
using MyPowerTools.Packaging;
using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

internal static class RuntimeOperationPolicy
{
    internal const string Any = "any";
    internal const string InProcOrSidecar = "inproc-or-sidecar";
    internal const string SidecarRequired = "sidecar-required";
    internal const string ServiceRequired = "service-required";
    internal const string BrokerRequired = "broker-required";

    public static RuntimeOperationPolicyDecision Evaluate(
        Sdk.MptCommandDescriptor command,
        Sdk.CommandRequest request,
        RuntimeModuleRecord module)
    {
        return Evaluate(command, request, module, module.Entrypoint);
    }

    public static RuntimeOperationPolicyDecision Evaluate(
        Sdk.MptCommandDescriptor command,
        Sdk.CommandRequest request,
        RuntimeModuleRecord module,
        SelectedEntrypoint? entrypoint)
    {
        var constraints = ReadConstraints(command, request).ToArray();
        if (constraints.Length == 0 || entrypoint is null)
        {
            return RuntimeOperationPolicyDecision.Allowed(constraints);
        }

        var transport = entrypoint.Kind;
        var violations = new List<RuntimeOperationPolicyViolation>();
        foreach (var constraint in constraints)
        {
            var rule = ResolveRule(module.Module.Manifest.RuntimePolicy?.OperationRules, constraint);
            if (!IsAllowed(rule, transport, command))
            {
                violations.Add(new RuntimeOperationPolicyViolation(constraint, rule, RequiredRouteForRule(rule), transport));
            }
        }

        return violations.Count == 0
            ? RuntimeOperationPolicyDecision.Allowed(constraints)
            : RuntimeOperationPolicyDecision.Blocked(command, module, entrypoint, constraints, violations);
    }

    internal static IReadOnlyList<string> GetConstraints(Sdk.MptCommandDescriptor command, Sdk.CommandRequest request)
    {
        return ReadConstraints(command, request).ToArray();
    }

    private static IEnumerable<string> ReadConstraints(Sdk.MptCommandDescriptor command, Sdk.CommandRequest request)
    {
        var constraints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (command.Constraints is not null)
        {
            foreach (var constraint in command.Constraints.Where(IsKnownConstraint))
            {
                constraints.Add(constraint);
            }
        }

        AddExecutionConstraints(command.Execution, constraints);
        if (command.RequiresElevation)
        {
            constraints.Add(Sdk.MptOperationConstraints.RequiresElevatedWrites);
        }

        if (string.Equals(command.Execution?["type"]?.GetValue<string>(), "broker.request", StringComparison.OrdinalIgnoreCase))
        {
            constraints.Add(Sdk.MptOperationConstraints.MutatesSystemState);
            constraints.Add(Sdk.MptOperationConstraints.RequiresElevatedWrites);
        }

        if (command.TimeoutMs >= 60000)
        {
            constraints.Add(Sdk.MptOperationConstraints.RequiresLongRunningLoop);
        }

        if (request.Args["hardwareWrite"]?.GetValue<bool>() == true)
        {
            constraints.Add(Sdk.MptOperationConstraints.UsesNativeHardware);
        }

        return constraints.Order(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddExecutionConstraints(JsonObject? execution, ISet<string> constraints)
    {
        if (execution is null)
        {
            return;
        }

        if (execution["constraints"] is JsonArray values)
        {
            foreach (var value in values)
            {
                var constraint = value?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(constraint) && IsKnownConstraint(constraint))
                {
                    constraints.Add(constraint);
                }
            }
        }

        AddBoolConstraint(execution, "mutatesSystemState", Sdk.MptOperationConstraints.MutatesSystemState, constraints);
        AddBoolConstraint(execution, "requiresElevatedWrites", Sdk.MptOperationConstraints.RequiresElevatedWrites, constraints);
        AddBoolConstraint(execution, "usesNativeHardware", Sdk.MptOperationConstraints.UsesNativeHardware, constraints);
        AddBoolConstraint(execution, "runsExternalProcesses", Sdk.MptOperationConstraints.RunsExternalProcesses, constraints);
        AddBoolConstraint(execution, "requiresLongRunningLoop", Sdk.MptOperationConstraints.RequiresLongRunningLoop, constraints);
    }

    private static void AddBoolConstraint(JsonObject execution, string propertyName, string constraint, ISet<string> constraints)
    {
        if (execution[propertyName]?.GetValue<bool>() == true)
        {
            constraints.Add(constraint);
        }
    }

    private static bool IsKnownConstraint(string? constraint)
    {
        return constraint is
            Sdk.MptOperationConstraints.MutatesSystemState or
            Sdk.MptOperationConstraints.RequiresElevatedWrites or
            Sdk.MptOperationConstraints.UsesNativeHardware or
            Sdk.MptOperationConstraints.RunsExternalProcesses or
            Sdk.MptOperationConstraints.RequiresLongRunningLoop;
    }

    internal static string ResolveRule(MptRuntimeOperationRulesManifest? rules, string constraint)
    {
        return constraint switch
        {
            Sdk.MptOperationConstraints.MutatesSystemState => NormalizeRule(rules?.SystemMutation, BrokerRequired),
            Sdk.MptOperationConstraints.RequiresElevatedWrites => NormalizeRule(rules?.ElevatedWrite, BrokerRequired),
            Sdk.MptOperationConstraints.UsesNativeHardware => NormalizeRule(rules?.NativeHardware, SidecarRequired),
            Sdk.MptOperationConstraints.RunsExternalProcesses => NormalizeRule(rules?.ExternalProcess, SidecarRequired),
            Sdk.MptOperationConstraints.RequiresLongRunningLoop => NormalizeRule(rules?.LongRunningCommand, SidecarRequired),
            _ => Any
        };
    }

    private static string NormalizeRule(string? rule, string defaultRule)
    {
        return string.IsNullOrWhiteSpace(rule) ? defaultRule : rule;
    }

    internal static bool IsAllowedForTransport(string rule, string transport, Sdk.MptCommandDescriptor command)
    {
        return IsAllowed(rule, transport, command);
    }

    internal static string RequiredRouteForRule(string rule)
    {
        return rule switch
        {
            Any => "any",
            InProcOrSidecar => "inproc-or-sidecar",
            SidecarRequired => "sidecar-or-service",
            ServiceRequired => "service",
            BrokerRequired => "broker",
            _ => rule
        };
    }

    private static bool IsAllowed(string rule, string transport, Sdk.MptCommandDescriptor command)
    {
        return rule switch
        {
            Any => true,
            InProcOrSidecar => IsInProc(transport) || IsSidecar(transport),
            SidecarRequired => IsSidecar(transport) || IsService(transport),
            ServiceRequired => IsService(transport),
            BrokerRequired => IsBrokerApprovalOnly(command),
            _ => false
        };
    }

    private static bool IsBrokerApprovalOnly(Sdk.MptCommandDescriptor command)
    {
        var execution = command.Execution;
        if (execution is null)
        {
            return false;
        }

        return string.Equals(execution["type"]?.GetValue<string>(), "broker.request", StringComparison.OrdinalIgnoreCase) ||
               execution["brokerApprovalOnly"]?.GetValue<bool>() == true ||
               string.Equals(execution["approval"]?.GetValue<string>(), "broker", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInProc(string transport)
    {
        return string.Equals(transport, "inproc-dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSidecar(string transport)
    {
        return string.Equals(transport, "grpc-ipc", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(transport, "jsonrpc-stdio", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsService(string transport)
    {
        return string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(transport, "service", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record RuntimeOperationPolicyViolation(
    string Constraint,
    string RequiredRule,
    string RequiredRoute,
    string SelectedTransport);

internal sealed record RuntimeOperationPolicyDecision(
    bool IsAllowed,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<RuntimeOperationPolicyViolation> Violations,
    string Message,
    JsonObject Details)
{
    public static RuntimeOperationPolicyDecision Allowed(IReadOnlyList<string> constraints)
    {
        return new RuntimeOperationPolicyDecision(true, constraints, [], "", new JsonObject());
    }

    public static RuntimeOperationPolicyDecision Blocked(
        Sdk.MptCommandDescriptor command,
        RuntimeModuleRecord module,
        SelectedEntrypoint? entrypoint,
        IReadOnlyList<string> constraints,
        IReadOnlyList<RuntimeOperationPolicyViolation> violations,
        string? unavailableReason = null,
        IReadOnlyList<TransportSelectionDiagnostic>? routeDiagnostics = null)
    {
        var violationArray = new JsonArray();
        foreach (var violation in violations)
        {
            violationArray.Add(new JsonObject
            {
                ["constraint"] = violation.Constraint,
                ["requiredRule"] = violation.RequiredRule,
                ["requiredRoute"] = violation.RequiredRoute,
                ["selectedTransport"] = violation.SelectedTransport
            });
        }

        var first = violations[0];
        unavailableReason ??= $"Selected transport '{first.SelectedTransport}' does not satisfy required route '{first.RequiredRoute}'.";
        var details = new JsonObject
        {
            ["moduleId"] = module.Module.Manifest.Id,
            ["commandId"] = command.Id,
            ["selectedTransport"] = entrypoint?.Kind ?? "none",
            ["selectionReason"] = entrypoint?.SelectionReason ?? "",
            ["alternateRequiredRoute"] = first.RequiredRoute,
            ["unavailableReason"] = unavailableReason,
            ["constraints"] = ToJsonArray(constraints),
            ["violations"] = violationArray
        };

        if (routeDiagnostics is { Count: > 0 })
        {
            details["routeDiagnostics"] = ToJsonArray(routeDiagnostics.Select(item => $"{item.State}:{item.TransportKind}:{item.Reason}"));
        }

        var message = $"Command '{command.Id}' requires operation constraint '{first.Constraint}' with runtimePolicy rule '{first.RequiredRule}' ({first.RequiredRoute}), but selected transport is '{first.SelectedTransport}'. {unavailableReason}";
        return new RuntimeOperationPolicyDecision(false, constraints, violations, message, details);
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
