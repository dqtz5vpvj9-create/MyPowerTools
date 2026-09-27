using MyPowerTools.Platform.Abstractions;
using A = global::Android;

namespace MyPowerTools.Platform.Android;

/// <summary>
/// Publishes a completed private file into the user's Downloads collection through MediaStore.
/// The pending flag keeps a partially copied file invisible to other apps, the display name is
/// resolved against existing downloads so nothing is overwritten, and the private source file is
/// removed only after the MediaStore row is finalised.
/// </summary>
public sealed class AndroidDownloadsService : IDownloadsService
{
    public async Task PublishAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("要发布的文件不存在。", path);
        }

        var resolver = A.App.Application.Context.ContentResolver
            ?? throw new InvalidOperationException("Android 内容服务不可用。");
        var existingNames = ExistingNames(resolver);
        var displayName = DownloadPublication.UniqueDisplayName(
            DownloadPublication.SanitizeDisplayName(path),
            existingNames.Contains);
        var values = new A.Content.ContentValues();
        values.Put(A.Provider.MediaStore.IMediaColumns.DisplayName, displayName);
        values.Put(A.Provider.MediaStore.IMediaColumns.MimeType, DownloadPublication.MimeTypeFor(displayName, AndroidMimeType));
        values.Put(A.Provider.MediaStore.IMediaColumns.RelativePath, DownloadPublication.RelativeDirectory);
        values.Put(A.Provider.MediaStore.IMediaColumns.IsPending, 1);

        A.Net.Uri? uri = null;
        try
        {
            uri = resolver.Insert(A.Provider.MediaStore.Downloads.ExternalContentUri!, values)
                ?? throw new IOException("无法创建下载文件。");
            await using (var input = File.OpenRead(path))
            await using (var output = resolver.OpenOutputStream(uri) ?? throw new IOException("无法写入下载文件。"))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var ready = new A.Content.ContentValues();
            ready.Put(A.Provider.MediaStore.IMediaColumns.IsPending, 0);
            if (resolver.Update(uri, ready, null, null) != 1)
            {
                throw new IOException("无法完成下载文件发布。");
            }
        }
        catch
        {
            // A cancelled or failed copy must not leave a pending MediaStore row behind. The private
            // source file is kept so the module can report the failure and the user can retry.
            if (uri is not null)
            {
                TryDeletePendingRow(resolver, uri);
            }

            throw;
        }

        // The download is already visible in MediaStore; a private copy that cannot be removed is a
        // cache leftover, not a publish failure.
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            A.Util.Log.Warn("MyPowerTools", "删除已发布的私有副本失败: " + ex.Message);
        }
    }

    private static string? AndroidMimeType(string extension) =>
        A.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension);

    /// <summary>
    /// Names already published under <see cref="DownloadPublication.RelativeDirectory"/>. Scoped
    /// storage only exposes this app's own contributions without a read permission, which is exactly
    /// the set that must not be overwritten; MediaStore still resolves collisions with files owned by
    /// other apps on its own.
    /// </summary>
    private static HashSet<string> ExistingNames(A.Content.ContentResolver resolver)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projection = new[] { A.Provider.MediaStore.IMediaColumns.DisplayName, A.Provider.MediaStore.IMediaColumns.RelativePath };
        using var cursor = resolver.Query(
            A.Provider.MediaStore.Downloads.ExternalContentUri!,
            projection,
            $"{A.Provider.MediaStore.IMediaColumns.RelativePath} LIKE ?",
            [DownloadPublication.RelativeDirectory + "/%"],
            null);
        if (cursor is null || !cursor.MoveToFirst())
        {
            return names;
        }

        var nameIndex = cursor.GetColumnIndex(A.Provider.MediaStore.IMediaColumns.DisplayName);
        do
        {
            if (nameIndex >= 0 && cursor.GetString(nameIndex) is { Length: > 0 } name)
            {
                names.Add(name);
            }
        }
        while (cursor.MoveToNext());

        return names;
    }

    private static void TryDeletePendingRow(A.Content.ContentResolver resolver, A.Net.Uri uri)
    {
        try
        {
            resolver.Delete(uri, null, null);
        }
        catch (Exception ex)
        {
            A.Util.Log.Warn("MyPowerTools", "清理未完成的下载记录失败: " + ex.Message);
        }
    }
}
