namespace MyPowerTools.Android.Pairing;

/// <summary>
/// How one system share becomes activations for the file assistant. Android-free so the mapping is
/// unit tested.
/// <para>
/// A share is one action by the user, so it produces one activation list for one surface: the shared
/// text first, then one activation per attachment, in the order the share delivered them. Nothing
/// here sends, stores or logs the text — the text only ever exists inside the escaped activation URI
/// that the assistant page reads into its composer.
/// </para>
/// </summary>
public static class ShareActivation
{
    /// <summary>What a shared text share activates: the assistant composer, never a device or a send.</summary>
    public const string AssistantUriPrefix = "mypowertools://file-assistant";

    /// <summary>The assistant's own connection link, owned by the same tool as the composer.</summary>
    public const string AssistantLinkPrefix = "mpt://assistant/";

    /// <summary>Query key the assistant page reads the shared text from.</summary>
    public const string TextParameter = "text";

    // A share can carry a long selection and is delivered as one URI. The ceiling bounds what the
    // host is willing to put in an extra; beyond it the share is refused rather than silently
    // truncated, because a half-delivered text with no notice is worse than an honest failure.
    public const int MaximumTextLength = 64 * 1024;

    /// <summary>True for the entry points the host resolves to the file assistant.</summary>
    public static bool IsAssistantEntryPoint(string? uri) =>
        uri?.StartsWith(AssistantUriPrefix, StringComparison.OrdinalIgnoreCase) == true ||
        uri?.StartsWith(AssistantLinkPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// The activation URI for shared text. The text is percent-escaped for a query value, so it
    /// cannot break out of the query or add a second parameter, and the result is safe to put in the
    /// normal activation path.
    /// </summary>
    public static string? TextActivation(string? text)
    {
        var value = text ?? "";
        if (value.Length == 0 || value.Length > MaximumTextLength) return null;
        return $"{AssistantUriPrefix}?{TextParameter}={Uri.EscapeDataString(value)}";
    }

    /// <summary>
    /// Reads the shared text back out of an activation URI. Used by tests and by any host-side
    /// consumer that needs to confirm what was delivered.
    /// </summary>
    public static string? ReadText(string? activationUri)
    {
        var value = activationUri?.Trim();
        if (value is null || !value.StartsWith(AssistantUriPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            if (!pair[..separator].Equals(TextParameter, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return null;
    }

    /// <summary>
    /// The activations for one share. <paramref name="attachmentUris"/> are the already-copied local
    /// file URIs, so a text-only share and a file share follow the same code path and a share that
    /// carries both keeps both.
    /// </summary>
    public static IReadOnlyList<string> BuildActivations(string? text, IReadOnlyList<string>? attachmentUris)
    {
        var activations = new List<string>();
        if (TextActivation(text) is { } textActivation)
        {
            activations.Add(textActivation);
        }

        if (attachmentUris is not null)
        {
            foreach (var attachment in attachmentUris)
            {
                if (!string.IsNullOrWhiteSpace(attachment)) activations.Add(attachment);
            }
        }

        return activations;
    }

    /// <summary>
    /// True when the share has nothing the assistant can use. The attachment count is passed
    /// separately because the host reads raw Android URIs, which are not strings yet.
    /// </summary>
    public static bool IsEmpty(string? text, int attachmentCount) =>
        string.IsNullOrEmpty(text) && attachmentCount <= 0;

    /// <summary>
    /// A log-safe summary of a share. The shared text is never included — not even truncated, because
    /// a share can be a password, an address or a message.
    /// </summary>
    public static string Describe(bool hasText, int attachmentCount, string? mimeType) =>
        $"text={(hasText ? "yes" : "no")} attachments={attachmentCount} type={mimeType ?? "(none)"}";
}
