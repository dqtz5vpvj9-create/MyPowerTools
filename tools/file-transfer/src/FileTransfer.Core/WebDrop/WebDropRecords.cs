namespace FileTransfer.Core.WebDrop;

/// <summary>
/// Configuration limits and defaults for temporary browser download links.
/// </summary>
public static class WebDropDefaults
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultIdleStopDelay = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Immutable snapshot of one browser web-drop link.
/// </summary>
public sealed record WebDropInfo(
    string Id,
    string Token,
    string FilePath,
    string FileName,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int MaxDownloads,
    int DownloadCount,
    bool Revoked,
    bool Active,
    int Port,
    string PrimaryUrl,
    string DownloadUrl,
    IReadOnlyList<string> Urls);
