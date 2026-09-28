namespace MyPowerTools.Platform.Abstractions;

/// <summary>
/// What an <c>mpt://</c> link from outside the app turned out to be. The value says which owner is
/// allowed to import it; it is never a claim that the payload was validated — only the owning
/// module command can do that.
/// </summary>
public enum MobileQrCodeKind
{
    /// <summary>Nothing recognised; the caller keeps scanning and shows the rejection reason.</summary>
    Unsupported,

    /// <summary><c>mpt://pair/…</c> — a file-transfer device pairing code. Owned by file-transfer.</summary>
    Pairing,

    /// <summary><c>mpt://cloud/…</c> — a relay account code. Owned by file-transfer.</summary>
    CloudRelay,

    /// <summary><c>mpt://control/…</c> — a remote-tool-access grant code. Owned by the gateway module.</summary>
    RemoteControl,

    /// <summary>
    /// <c>mpt://assistant/…</c> — a file-assistant connection code ("connect my devices"). Owned by
    /// file-transfer; a scan only previews it, joining stays the user's confirmation.
    /// </summary>
    Assistant
}

/// <summary>Why a scan did not produce a code, so the caller can offer the matching next step.</summary>
public enum MobileQrScanError
{
    None = 0,
    Cancelled,

    /// <summary>The user refused the camera permission; manual entry stays available.</summary>
    PermissionDenied,

    /// <summary>No usable camera, or the camera could not be configured.</summary>
    Unavailable,

    /// <summary>The scan was replaced by a newer request or the activity was destroyed.</summary>
    Interrupted
}

/// <summary>
/// One classified connection code. <see cref="Value"/> is a credential-bearing string — a receiver
/// secret, a relay password or a control grant token — so it exists only to be handed to the owning
/// module's import command.
/// <para>
/// It must never be logged, rendered, put into a toast or an exception message, written to
/// preferences or exported with acceptance data. <see cref="Describe"/> is the only representation
/// intended for a log line, and it contains the kind and the payload length and nothing else.
/// </para>
/// </summary>
public sealed record MobileQrConnectionCode(
    MobileQrCodeKind Kind,
    string Value,
    string Summary,
    bool CarriesCredential)
{
    /// <summary>A log-safe label. Deliberately cannot contain any character of the payload.</summary>
    public string Describe() => MobileQrPayload.Describe(Kind, Value);

    /// <summary>
    /// The compiler-generated record text would print <see cref="Value"/>, which is the credential.
    /// A record reaches a log or an exception message by accident far more easily than by intent, so
    /// the only representation it can produce is the redacted one.
    /// </summary>
    public override string ToString() => Describe();
}

/// <summary>
/// The result of one scan attempt. <see cref="Code"/> is null unless a real internal link was
/// decoded; there is no simulated or placeholder success.
/// </summary>
public sealed record MobileQrScanResult(
    MobileQrConnectionCode? Code,
    bool OpenManualEntry,
    MobileQrScanError Error,
    string? Message)
{
    public bool Succeeded => Code is not null;

    /// <summary>The compiler-generated text would print the credential; see <see cref="MobileQrConnectionCode.ToString"/>.</summary>
    public override string ToString() =>
        Succeeded
            ? $"succeeded {Code!.Describe()}"
            : $"error={Error} message={Message ?? "(none)"}";

    public static MobileQrScanResult Success(MobileQrConnectionCode code) =>
        new(code, false, MobileQrScanError.None, null);

    public static MobileQrScanResult Cancelled(
        string message,
        bool openManualEntry = false,
        MobileQrScanError error = MobileQrScanError.Cancelled) =>
        new(null, openManualEntry, error, message);

    public static MobileQrScanResult Failed(
        string message,
        MobileQrScanError error = MobileQrScanError.Unavailable) =>
        new(null, false, error, message);
}

/// <summary>
/// Turns a scanned or pasted string into <see cref="MobileQrConnectionCode"/> without decoding it.
/// Shared by the Android scanner and by every caller that needs the same answer, so the accepted
/// link families and the redaction rule exist exactly once.
/// </summary>
public static class MobileQrPayload
{
    public const string PairPrefix = "mpt://pair/";
    public const string CloudPrefix = "mpt://cloud/";
    public const string ControlPrefix = "mpt://control/";
    public const string AssistantPrefix = "mpt://assistant/";

    // A real pairing link is around 200 characters, a control link under 400. The ceiling only stops
    // an oversized or hostile string from reaching a module decoder, which repeats its limit.
    public const int MaximumLength = 4096;

    private static readonly (string Prefix, MobileQrCodeKind Kind, string Label, bool Credential)[] Families =
    [
        (PairPrefix, MobileQrCodeKind.Pairing, "设备连接码", true),
        (CloudPrefix, MobileQrCodeKind.CloudRelay, "网盘连接码", true),
        (ControlPrefix, MobileQrCodeKind.RemoteControl, "远程工具连接码", true),
        (AssistantPrefix, MobileQrCodeKind.Assistant, "文件助手连接码", true)
    ];

    /// <summary>True when the link belongs to one of the internal families this app can hand on.</summary>
    public static bool IsRecognised(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaximumLength)
        {
            return false;
        }

        return Find(text).Prefix is not null;
    }

    /// <summary>
    /// Classifies a candidate without trusting it. <paramref name="rejectionReason"/> is written for
    /// the user and never repeats the scanned content.
    /// </summary>
    public static bool TryClassify(string? value, out MobileQrConnectionCode? code, out string? rejectionReason)
    {
        code = null;
        rejectionReason = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            rejectionReason = "没有扫描到内容，请把二维码放入取景框。";
            return false;
        }

        if (text.Length > MaximumLength)
        {
            rejectionReason = "二维码内容过长，不是 MPT 连接码。";
            return false;
        }

        var (prefix, kind, label, credential) = Find(text);
        if (prefix is null)
        {
            rejectionReason = text.StartsWith("mpt://", StringComparison.OrdinalIgnoreCase)
                ? "这是 MPT 的其他链接，不能在这里导入。"
                : "不是 MPT 连接码。请在电脑端 MPT 里打开对应的“添加设备”页面并扫描它的二维码。";
            return false;
        }

        var payload = text[prefix.Length..];
        if (payload.Length == 0)
        {
            rejectionReason = $"{label}是空的，请重新生成后再扫描。";
            return false;
        }

        if (!IsBase64Url(payload))
        {
            rejectionReason = $"{label}格式不正确，请重新扫描。";
            return false;
        }

        // The full link is what the owning module command validates, stores and confirms. It travels
        // only to that command; it is never logged, rendered or persisted here.
        code = new MobileQrConnectionCode(kind, text, $"已识别{label}", credential);
        return true;
    }

    /// <summary>
    /// A log-safe label. It contains the link kind and the payload length and deliberately cannot
    /// contain any character of the payload: an opaque base64url prefix decodes to readable bytes
    /// (a device id, an address, a name), so even a truncated prefix is a leak.
    /// </summary>
    public static string Describe(MobileQrCodeKind kind, string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            return "(空)";
        }

        foreach (var (prefix, family, label, _) in Families)
        {
            if (family != kind || !text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            return $"{label} · 内容已隐藏（长度 {text.Length - prefix.Length}）";
        }

        return "无法识别的连接码（内容已隐藏）";
    }

    private static (string? Prefix, MobileQrCodeKind Kind, string Label, bool Credential) Find(string text)
    {
        foreach (var family in Families)
        {
            if (text.StartsWith(family.Prefix, StringComparison.Ordinal))
            {
                return family;
            }
        }

        return (null, MobileQrCodeKind.Unsupported, "", false);
    }

    private static bool IsBase64Url(string value)
    {
        foreach (var character in value)
        {
            var allowed = character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One on-demand camera scan of a MyPowerTools connection-code QR code. A host that supports the
/// camera returns its implementation from a platform pack (Android wraps
/// <c>MyPowerTools.Android.MobileQrScan</c>), and the phone Shell asks for it when the user taps a
/// "扫码" action.
/// <para>
/// The scan only recognises and routes: the returned <see cref="MobileQrConnectionCode"/> goes to
/// the owning module's import command, which validates it, stores the credential in the platform
/// secret store and asks the user to confirm. A scan never grants permission, never imports on its
/// own and never fabricates a result.
/// </para>
/// </summary>
public interface IMobileQrScanner
{
    /// <summary>
    /// Opens the camera viewfinder and completes when it closes. UI thread only; the camera
    /// permission is requested inside the scan, never at application start.
    /// </summary>
    Task<MobileQrScanResult> ScanAsync(CancellationToken cancellationToken = default);
}
