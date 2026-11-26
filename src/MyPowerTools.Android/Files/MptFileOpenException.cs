namespace MyPowerTools.Android.Files;

/// <summary>Why a phone file could not be handed to a viewer.</summary>
public enum MptFileOpenFailure
{
    /// <summary>No path was supplied.</summary>
    EmptyPath,

    /// <summary>The value was not a usable absolute path or file URI.</summary>
    MalformedPath,

    /// <summary>The value was a URI whose scheme the host does not open.</summary>
    UnsupportedScheme,

    /// <summary>The file is outside every directory the host may share.</summary>
    OutsideAllowedRoots,

    /// <summary>The file does not exist.</summary>
    Missing,

    /// <summary>The path is a directory.</summary>
    NotAFile,

    /// <summary>The file exists but cannot be read by this process.</summary>
    Unreadable,

    /// <summary>The read-only provider is not registered, or its authority differs from the package id.</summary>
    ProviderMisconfigured,

    /// <summary>No installed activity can view this file.</summary>
    NoViewer,

    /// <summary>The system refused or could not start the viewer.</summary>
    LaunchFailed,
}

/// <summary>
/// A concrete, understandable failure instead of a bare <see langword="false"/>: the caller can show
/// <see cref="Exception.Message"/> to the user and branch on <see cref="Failure"/>. Neither the
/// message nor <see cref="Exception.Data"/> carries file content or credentials.
/// </summary>
public sealed class MptFileOpenException : Exception
{
    public MptFileOpenException(MptFileOpenFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>The machine-readable reason.</summary>
    public MptFileOpenFailure Failure { get; }

    /// <summary>
    /// The roots that were checked, set for <see cref="MptFileOpenFailure.OutsideAllowedRoots"/> so a
    /// host log can explain the refusal without echoing the rejected path.
    /// </summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];
}

/// <summary>Maps access verdicts to user-understandable failures. Android-free, so it is unit tested.</summary>
public static class MptFileOpenErrors
{
    /// <summary>Builds the failure for a rejected path. <see cref="SharedFileAccessVerdict.Allowed"/> is a caller bug.</summary>
    public static MptFileOpenException FromVerdict(
        SharedFileAccessVerdict verdict,
        IReadOnlyList<string>? allowedRoots = null)
    {
        var failure = verdict switch
        {
            SharedFileAccessVerdict.EmptyPath => MptFileOpenFailure.EmptyPath,
            SharedFileAccessVerdict.MalformedPath => MptFileOpenFailure.MalformedPath,
            SharedFileAccessVerdict.UnsupportedScheme => MptFileOpenFailure.UnsupportedScheme,
            SharedFileAccessVerdict.OutsideAllowedRoots => MptFileOpenFailure.OutsideAllowedRoots,
            SharedFileAccessVerdict.Missing => MptFileOpenFailure.Missing,
            SharedFileAccessVerdict.NotAFile => MptFileOpenFailure.NotAFile,
            SharedFileAccessVerdict.Unreadable => MptFileOpenFailure.Unreadable,
            _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "允许访问的判定没有失败原因。"),
        };

        var message = failure switch
        {
            MptFileOpenFailure.EmptyPath => "打开文件失败：没有提供文件路径。",
            MptFileOpenFailure.MalformedPath => "打开文件失败：路径无效或无法解析。",
            MptFileOpenFailure.UnsupportedScheme => "打开文件失败：只支持手机上的本地文件，不支持这种链接。",
            MptFileOpenFailure.OutsideAllowedRoots => "打开文件失败：该文件不在 MPT 的收件目录内，未打开。",
            MptFileOpenFailure.Missing => "打开文件失败：文件不存在或已被移除。",
            MptFileOpenFailure.NotAFile => "打开文件失败：该路径是文件夹，不是文件。",
            MptFileOpenFailure.Unreadable => "打开文件失败：没有读取该文件的权限。",
            _ => "打开文件失败。",
        };

        return new MptFileOpenException(failure, message)
        {
            AllowedRoots = verdict == SharedFileAccessVerdict.OutsideAllowedRoots ? allowedRoots ?? [] : [],
        };
    }

    /// <summary>The provider could not produce a shareable URI for the file.</summary>
    public static MptFileOpenException ProviderMisconfigured(string detail, Exception? innerException = null) =>
        new(MptFileOpenFailure.ProviderMisconfigured,
            "打开文件失败：只读文件提供者不可用（authority 或路径资源与运行目录不匹配）。" + detail,
            innerException);

    /// <summary>Nothing installed can view this file type.</summary>
    public static MptFileOpenException NoViewer(string? fileName, Exception? innerException = null)
    {
        var extension = SharedFileMime.Extension(fileName);
        var subject = extension is null ? "此文件" : $"此类型（.{extension}）文件";
        return new MptFileOpenException(
            MptFileOpenFailure.NoViewer,
            $"系统中没有可以打开{subject}的应用，请先安装对应的查看器。",
            innerException);
    }

    /// <summary>The viewer intent could not be handed to the system.</summary>
    public static MptFileOpenException LaunchFailed(string detail, Exception? innerException = null) =>
        new(MptFileOpenFailure.LaunchFailed, "打开文件失败：系统未接受查看器启动请求。" + detail, innerException);
}
