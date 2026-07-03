using System.Text.Json.Nodes;

namespace AdbForwarder.MyPowerTools;

public sealed record AdbPortMapping(
    string Id,
    string Name,
    bool Enabled,
    string ListenAddress,
    int ListenPort,
    string ConnectAddress,
    int ConnectPort)
{
    public string ListenKey => $"{NormalizeAddress(ListenAddress)}:{ListenPort}";
    public string Scope => $"{ListenAddress}:{ListenPort}->{ConnectAddress}:{ConnectPort}";

    public PortProxyRuleSnapshot ToRule() => new(ListenAddress, ListenPort, ConnectAddress, ConnectPort);

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["enabled"] = Enabled,
            ["listenAddress"] = ListenAddress,
            ["listenPort"] = ListenPort,
            ["connectAddress"] = ConnectAddress,
            ["connectPort"] = ConnectPort
        };
    }

    public static string NormalizeAddress(string value) => value.Trim().ToLowerInvariant();
}

public sealed record PortProxyRuleSnapshot(string ListenAddress, int ListenPort, string ConnectAddress, int ConnectPort)
{
    public string ListenKey => $"{AdbPortMapping.NormalizeAddress(ListenAddress)}:{ListenPort}";
    public string Scope => $"{ListenAddress}:{ListenPort}->{ConnectAddress}:{ConnectPort}";

    public bool SameEndpoint(AdbPortMapping mapping)
    {
        return string.Equals(ListenKey, mapping.ListenKey, StringComparison.OrdinalIgnoreCase);
    }

    public bool SameRule(AdbPortMapping mapping)
    {
        return SameEndpoint(mapping) &&
            string.Equals(AdbPortMapping.NormalizeAddress(ConnectAddress), AdbPortMapping.NormalizeAddress(mapping.ConnectAddress), StringComparison.OrdinalIgnoreCase) &&
            ConnectPort == mapping.ConnectPort;
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["listenAddress"] = ListenAddress,
            ["listenPort"] = ListenPort,
            ["connectAddress"] = ConnectAddress,
            ["connectPort"] = ConnectPort
        };
    }
}

public sealed record PortProxyRollbackStep(string Operation, PortProxyRuleSnapshot Rule, string Reason)
{
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["operation"] = Operation,
            ["rule"] = Rule.ToJson(),
            ["reason"] = Reason
        };
    }
}

public sealed record AdbPortProxyPlan(
    IReadOnlyList<AdbPortMapping> DesiredMappings,
    IReadOnlyList<PortProxyRuleSnapshot> CurrentRules,
    IReadOnlyList<AdbPortMapping> ToApply,
    IReadOnlyList<PortProxyRuleSnapshot> ToRemove,
    IReadOnlyList<PortProxyRollbackStep> Rollback,
    IReadOnlyList<string> Warnings)
{
    public bool HasChanges => ToApply.Count > 0 || ToRemove.Count > 0;

    public string Scope => DesiredMappings.Count == 0
        ? "adb-forwarder portproxy rules"
        : string.Join(", ", DesiredMappings.Select(mapping => mapping.Scope));

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["desiredMappings"] = AdbPortProxyModel.ToJsonArray(DesiredMappings, mapping => mapping.ToJson()),
            ["currentRules"] = AdbPortProxyModel.ToJsonArray(CurrentRules, rule => rule.ToJson()),
            ["toApply"] = AdbPortProxyModel.ToJsonArray(ToApply, mapping => mapping.ToRule().ToJson()),
            ["toRemove"] = AdbPortProxyModel.ToJsonArray(ToRemove, rule => rule.ToJson()),
            ["rollback"] = AdbPortProxyModel.ToJsonArray(Rollback, step => step.ToJson()),
            ["warnings"] = AdbPortProxyModel.ToJsonArray(Warnings, warning => JsonValue.Create(warning)!),
            ["hasChanges"] = HasChanges
        };
    }

    public JsonObject ExpectedChangeJson()
    {
        return new JsonObject
        {
            ["apply"] = AdbPortProxyModel.ToJsonArray(ToApply, mapping => mapping.ToRule().ToJson()),
            ["remove"] = AdbPortProxyModel.ToJsonArray(ToRemove, rule => rule.ToJson())
        };
    }
}

public static class AdbPortProxyModel
{
    public static (IReadOnlyList<AdbPortMapping> Mappings, IReadOnlyList<string> Messages) ParseMappings(JsonObject source)
    {
        var messages = new List<string>();
        var mappings = new List<AdbPortMapping>();

        if (TryGetArray(source, "mappings", out var mappingsArray))
        {
            for (var i = 0; i < mappingsArray.Count; i++)
            {
                if (mappingsArray[i] is not JsonObject mappingObject)
                {
                    messages.Add($"mappings[{i}] must be an object.");
                    continue;
                }

                mappings.Add(ParseMapping(mappingObject, i));
            }
        }
        else if (TryGetObject(source, "mapping", out var mappingObject))
        {
            mappings.Add(ParseMapping(mappingObject, 0));
        }

        messages.AddRange(ValidateMappings(mappings));
        return (mappings, messages);
    }

    public static IReadOnlyList<PortProxyRuleSnapshot> ParseRules(JsonArray array)
    {
        var rules = new List<PortProxyRuleSnapshot>();
        foreach (var node in array)
        {
            if (node is not JsonObject rule)
            {
                continue;
            }

            rules.Add(new PortProxyRuleSnapshot(
                ReadString(rule, "listenAddress") ?? "",
                ReadInt(rule, "listenPort"),
                ReadString(rule, "connectAddress") ?? "",
                ReadInt(rule, "connectPort")));
        }

        return rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.ListenAddress) && !string.IsNullOrWhiteSpace(rule.ConnectAddress))
            .Where(rule => IsValidPort(rule.ListenPort) && IsValidPort(rule.ConnectPort))
            .ToArray();
    }

    public static IReadOnlyList<string> ValidateMappings(IReadOnlyList<AdbPortMapping> mappings)
    {
        var messages = new List<string>();
        for (var i = 0; i < mappings.Count; i++)
        {
            var mapping = mappings[i];
            if (string.IsNullOrWhiteSpace(mapping.Id))
            {
                messages.Add($"mappings[{i}].id is required.");
            }

            if (string.IsNullOrWhiteSpace(mapping.ListenAddress))
            {
                messages.Add($"mappings[{i}].listenAddress is required.");
            }

            if (string.IsNullOrWhiteSpace(mapping.ConnectAddress))
            {
                messages.Add($"mappings[{i}].connectAddress is required.");
            }

            if (!IsValidPort(mapping.ListenPort))
            {
                messages.Add($"mappings[{i}].listenPort must be between 1 and 65535.");
            }

            if (!IsValidPort(mapping.ConnectPort))
            {
                messages.Add($"mappings[{i}].connectPort must be between 1 and 65535.");
            }
        }

        var duplicateListenEndpoints = mappings
            .Where(mapping => mapping.Enabled)
            .GroupBy(mapping => mapping.ListenKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        foreach (var endpoint in duplicateListenEndpoints)
        {
            messages.Add($"enabled mappings contain duplicate listen endpoint {endpoint}.");
        }

        return messages;
    }

    public static JsonArray ToJsonArray<T>(IEnumerable<T> values, Func<T, JsonNode> convert)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(convert(value));
        }

        return array;
    }

    private static AdbPortMapping ParseMapping(JsonObject source, int index)
    {
        var listenAddress = ReadString(source, "listenAddress") ?? "";
        var listenPort = ReadInt(source, "listenPort");
        var connectAddress = ReadString(source, "connectAddress") ?? "";
        var connectPort = ReadInt(source, "connectPort");
        var id = ReadString(source, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = StableId(listenAddress, listenPort, connectAddress, connectPort, index);
        }

        var name = ReadString(source, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = id;
        }

        return new AdbPortMapping(
            id.Trim(),
            name.Trim(),
            ReadBool(source, "enabled", true),
            listenAddress.Trim(),
            listenPort,
            connectAddress.Trim(),
            connectPort);
    }

    private static string StableId(string listenAddress, int listenPort, string connectAddress, int connectPort, int index)
    {
        var material = $"{listenAddress}-{listenPort}-{connectAddress}-{connectPort}".Trim('-');
        if (string.IsNullOrWhiteSpace(material) || material == "0-0")
        {
            material = $"mapping-{index + 1}";
        }

        var chars = material
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        return string.Join("", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool TryGetArray(JsonObject source, string property, out JsonArray array)
    {
        if (source.TryGetPropertyValue(property, out var node) && node is JsonArray found)
        {
            array = found;
            return true;
        }

        array = [];
        return false;
    }

    private static bool TryGetObject(JsonObject source, string property, out JsonObject value)
    {
        if (source.TryGetPropertyValue(property, out var node) && node is JsonObject found)
        {
            value = found;
            return true;
        }

        value = [];
        return false;
    }

    private static string? ReadString(JsonObject source, string property)
    {
        if (!source.TryGetPropertyValue(property, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int ReadInt(JsonObject source, string property)
    {
        if (!source.TryGetPropertyValue(property, out var node) || node is null)
        {
            return 0;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            try
            {
                return checked((int)node.GetValue<long>());
            }
            catch (InvalidOperationException)
            {
            }
            catch (OverflowException)
            {
                return 0;
            }

            try
            {
                var number = node.GetValue<double>();
                return Math.Abs(number % 1) < double.Epsilon ? checked((int)number) : 0;
            }
            catch (InvalidOperationException)
            {
            }
            catch (OverflowException)
            {
                return 0;
            }

            var text = ReadString(source, property);
            return int.TryParse(text, out var parsed) ? parsed : 0;
        }
    }

    private static bool ReadBool(JsonObject source, string property, bool defaultValue)
    {
        if (!source.TryGetPropertyValue(property, out var node) || node is null)
        {
            return defaultValue;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (InvalidOperationException)
        {
            var text = ReadString(source, property);
            return bool.TryParse(text, out var parsed) ? parsed : defaultValue;
        }
    }

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;
}

public static class AdbPortProxyPlanner
{
    public static AdbPortProxyPlan CreateApplyPlan(
        IReadOnlyList<AdbPortMapping> mappings,
        IReadOnlyList<PortProxyRuleSnapshot> currentRules,
        IEnumerable<string>? warnings = null)
    {
        var enabledDesired = mappings.Where(mapping => mapping.Enabled).ToArray();
        var configuredListenKeys = mappings.Select(mapping => mapping.ListenKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentByListen = currentRules
            .GroupBy(rule => rule.ListenKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var toApply = enabledDesired
            .Where(mapping => !currentRules.Any(rule => rule.SameRule(mapping)))
            .ToArray();

        var toRemove = currentRules
            .Where(rule => configuredListenKeys.Contains(rule.ListenKey))
            .Where(rule => !enabledDesired.Any(rule.SameRule))
            .ToArray();

        var rollback = BuildApplyRollback(toApply, toRemove, currentByListen);
        var allWarnings = (warnings ?? [])
            .Concat(mappings.Count == 0 ? ["No configured mappings were supplied."] : [])
            .Concat(toApply.Length == 0 && toRemove.Length == 0 ? ["Current portproxy state already matches configured mappings."] : [])
            .ToArray();

        return new AdbPortProxyPlan(mappings, currentRules, toApply, toRemove, rollback, allWarnings);
    }

    public static AdbPortProxyPlan CreateRevertPlan(
        IReadOnlyList<AdbPortMapping> mappings,
        IReadOnlyList<PortProxyRuleSnapshot> currentRules,
        IEnumerable<string>? warnings = null)
    {
        var configuredListenKeys = mappings.Select(mapping => mapping.ListenKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toRemove = currentRules
            .Where(rule => configuredListenKeys.Contains(rule.ListenKey))
            .ToArray();
        var rollback = toRemove
            .Select(rule => new PortProxyRollbackStep("apply", rule, $"Restore removed rule {rule.Scope}."))
            .ToArray();
        var allWarnings = (warnings ?? [])
            .Concat(mappings.Count == 0 ? ["No configured mappings were supplied."] : [])
            .Concat(toRemove.Length == 0 ? ["No active portproxy rules match configured mappings."] : [])
            .ToArray();

        return new AdbPortProxyPlan(mappings, currentRules, [], toRemove, rollback, allWarnings);
    }

    private static IReadOnlyList<PortProxyRollbackStep> BuildApplyRollback(
        IReadOnlyList<AdbPortMapping> toApply,
        IReadOnlyList<PortProxyRuleSnapshot> toRemove,
        IReadOnlyDictionary<string, PortProxyRuleSnapshot> currentByListen)
    {
        var steps = new List<PortProxyRollbackStep>();
        foreach (var mapping in toApply)
        {
            if (currentByListen.TryGetValue(mapping.ListenKey, out var existing))
            {
                steps.Add(new PortProxyRollbackStep("apply", existing, $"Restore previous rule {existing.Scope}."));
            }
            else
            {
                steps.Add(new PortProxyRollbackStep("remove", mapping.ToRule(), $"Remove newly applied rule {mapping.Scope}."));
            }
        }

        foreach (var rule in toRemove)
        {
            if (toApply.Any(mapping => string.Equals(mapping.ListenKey, rule.ListenKey, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            steps.Add(new PortProxyRollbackStep("apply", rule, $"Restore removed rule {rule.Scope}."));
        }

        return steps;
    }
}
