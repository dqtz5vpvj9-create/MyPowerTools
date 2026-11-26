namespace MyPowerTools.Android.Files;

/// <summary>
/// The bounded viewer plan for one file: which content type is tried first and the single generic
/// retry used when the system reports that nothing can open that type. At most
/// <see cref="MaxAttempts"/> launches are ever attempted, and the first attempt is never the
/// wildcard, so a real type is always preferred over "anything".
/// </summary>
public sealed record SharedFileViewPlan
{
    /// <summary>Hard cap on viewer launches for one open request.</summary>
    public const int MaxAttempts = 2;

    private SharedFileViewPlan(string primaryMimeType, string fallbackMimeType, SharedFileMimeSource source)
    {
        PrimaryMimeType = primaryMimeType;
        FallbackMimeType = fallbackMimeType;
        Source = source;
    }

    /// <summary>The content type derived from the file name.</summary>
    public string PrimaryMimeType { get; }

    /// <summary>The one generic retry, or the same type again when deduplication leaves one attempt.</summary>
    public string FallbackMimeType { get; }

    /// <summary>Where <see cref="PrimaryMimeType"/> came from.</summary>
    public SharedFileMimeSource Source { get; }

    /// <summary>The ordered, deduplicated list of types to try; never longer than <see cref="MaxAttempts"/>.</summary>
    public IReadOnlyList<string> MimeAttempts =>
        string.Equals(PrimaryMimeType, FallbackMimeType, StringComparison.Ordinal)
            ? [PrimaryMimeType]
            : [PrimaryMimeType, FallbackMimeType];

    /// <summary>Builds the plan for one file name.</summary>
    public static SharedFileViewPlan Create(string? fileName, Func<string, string?>? systemLookup = null)
    {
        var inferred = SharedFileMime.Infer(fileName, systemLookup);
        var fallback = inferred.MimeType == "application/vnd.android.package-archive" || string.Equals(inferred.MimeType, SharedFileMime.WildcardMimeType, StringComparison.Ordinal)
            ? inferred.MimeType
            : SharedFileMime.WildcardMimeType;
        return new SharedFileViewPlan(inferred.MimeType, fallback, inferred.Source);
    }
}
