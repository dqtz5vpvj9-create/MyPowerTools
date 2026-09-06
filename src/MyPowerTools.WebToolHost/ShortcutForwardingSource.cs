namespace MyPowerTools.WebToolHost;

internal static class ShortcutForwardingSource
{
    public static string Read()
    {
        using var stream = typeof(ShortcutForwardingSource).Assembly.GetManifestResourceStream("MptShortcutForwarding")
            ?? throw new InvalidOperationException("Shortcut forwarding resource is missing.");
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        const string prefix = "R\"MPTJS(";
        const string suffix = ")MPTJS\"";
        return source.Substring(prefix.Length, source.LastIndexOf(suffix, StringComparison.Ordinal) - prefix.Length);
    }
}
