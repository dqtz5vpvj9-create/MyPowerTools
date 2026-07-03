namespace AdbForwarder.MyPowerTools;

public static class PortProxyParser
{
    public static IReadOnlyList<PortProxyRuleSnapshot> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var rules = new List<PortProxyRuleSnapshot>();
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            var columns = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length != 4)
            {
                continue;
            }

            if (!LooksLikeAddress(columns[0]) || !LooksLikeAddress(columns[2]))
            {
                continue;
            }

            if (!int.TryParse(columns[1], out var listenPort) || !int.TryParse(columns[3], out var connectPort))
            {
                continue;
            }

            rules.Add(new PortProxyRuleSnapshot(columns[0], listenPort, columns[2], connectPort));
        }

        return rules;
    }

    private static bool LooksLikeAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Equals("Address", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Listen", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Connect", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        return value.Any(char.IsDigit) ||
            value.Contains('.') ||
            value.Contains(':') ||
            value.Equals("*", StringComparison.Ordinal) ||
            value.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
}
