using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace FileTransfer.Surface;

/// <summary>What happened when the page asked the platform to open a real file.</summary>
internal enum FileOpenOutcome
{
    /// <summary>The platform viewer was launched. This is not proof the file was read.</summary>
    Opened,

    /// <summary>No platform viewer is available here.</summary>
    Unavailable,

    /// <summary>A viewer exists but refused or failed.</summary>
    Failed
}

internal sealed record FileOpenResult(FileOpenOutcome Outcome, string Message)
{
    public bool Opened => Outcome == FileOpenOutcome.Opened;
}

/// <summary>
/// Opens a local file with whatever the platform provides.
///
/// Mobile hosts grant the viewer temporary read access through their own delegate
/// (<c>MptAvaloniaSurfaceContext.OpenFileAsync</c>), which is authoritative when present. A desktop
/// host that leaves it null falls back to <see cref="TopLevel.Launcher"/>. There is deliberately no
/// "saved, therefore opened" path: if neither route can launch a viewer the caller is told so.
///
/// This lives outside the view so the outcome can be tested directly: the page only renders the
/// message, it never decides that opening succeeded.
/// </summary>
internal static class FileOpenStrategy
{
    public static async Task<FileOpenResult> OpenAsync(
        Func<string, CancellationToken, Task<bool>>? platformOpen,
        TopLevel? desktopHost,
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return new(FileOpenOutcome.Failed, "没有可打开的文件。");
        if (!File.Exists(path)) return new(FileOpenOutcome.Failed, "本地文件已不在设备上，请重新下载后打开。");

        if (platformOpen is not null)
        {
            try
            {
                return await platformOpen(path, cancellationToken)
                    ? new(FileOpenOutcome.Opened, "已用系统应用打开。")
                    : new(FileOpenOutcome.Failed, "系统没有能打开这个文件的应用，请安装相应的查看器或保存副本。");
            }
            catch (OperationCanceledException)
            {
                return new(FileOpenOutcome.Failed, "打开已取消。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                return new(FileOpenOutcome.Failed, "无法打开文件：" + ex.Message);
            }
        }

        var launcher = desktopHost?.Launcher;
        if (launcher is null) return new(FileOpenOutcome.Unavailable, "当前环境没有可用的文件打开方式，请保存副本后用其他应用打开。");
        try
        {
            return await launcher.LaunchFileInfoAsync(new FileInfo(path))
                ? new(FileOpenOutcome.Opened, "已用系统应用打开。")
                : new(FileOpenOutcome.Failed, "系统没有能打开这个文件的应用，请安装相应的查看器或保存副本。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return new(FileOpenOutcome.Failed, "无法打开文件：" + ex.Message);
        }
    }
}
