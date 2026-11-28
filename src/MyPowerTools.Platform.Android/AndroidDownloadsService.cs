using MyPowerTools.Platform.Abstractions;
using A = global::Android;

namespace MyPowerTools.Platform.Android;

/// <summary>
/// Publishes a completed private file into the user's authorized external directory (Storage Access
/// Framework tree URI when granted) or falls back to the system Downloads collection through
/// MediaStore (<c>Download/MPT</c>).
/// <para>
/// Display names are resolved against existing entries so nothing is overwritten, partial writes
/// never appear as completed files, and the private staging file is deleted only after the external
/// file has been committed cleanly.
/// </para>
/// </summary>
public sealed class AndroidDownloadsService : IDownloadsService
{
    public Task PublishAsync(string path, CancellationToken cancellationToken) =>
        PublishAndLocateAsync(path, cancellationToken);

    public async Task<string?> PublishAndLocateAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("要发布的文件不存在。", path);
        }

        var context = A.App.Application.Context;
        var resolver = context.ContentResolver
            ?? throw new InvalidOperationException("Android 内容服务不可用。");

        string? publishedUri = null;
        var config = AndroidExternalStoragePreferences.Load();
        if (config.Mode == AndroidExternalStorageConfig.ModeSaf &&
            config.TreeUri.Length > 0 &&
            HasPersistedWritePermission(context, config.TreeUri))
        {
            try
            {
                publishedUri = await PublishToSafTreeAsync(resolver, config.TreeUri, path, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                A.Util.Log.Warn("MyPowerTools", "写入已授权外部目录失败，回退系统下载目录: " + ex.Message);
            }
        }

        if (publishedUri is null)
        {
            publishedUri = await PublishToMediaStoreAsync(resolver, path, cancellationToken).ConfigureAwait(false);
        }

        // The external file is already committed; a staging copy that cannot be removed is a cache
        // leftover, not a publish failure.
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            A.Util.Log.Warn("MyPowerTools", "删除已发布的私有副本失败: " + ex.Message);
        }

        return publishedUri;
    }

    public static bool HasPersistedWritePermission(A.Content.Context context, string? treeUriString)
    {
        if (string.IsNullOrWhiteSpace(treeUriString))
        {
            return false;
        }

        var resolver = context.ContentResolver;
        if (resolver is null)
        {
            return false;
        }

        try
        {
            var permissions = resolver.PersistedUriPermissions;
            if (permissions is null)
            {
                return false;
            }

            foreach (var permission in permissions)
            {
                if (permission.IsReadPermission &&
                    permission.IsWritePermission &&
                    string.Equals(permission.Uri?.ToString(), treeUriString, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>Counts files and total bytes inside the user's authorized SAF directory (read-only).</summary>
    public static (int FileCount, long TotalBytes, bool Accessible) InspectSafTree(A.Content.Context context, string treeUriString)
    {
        if (!HasPersistedWritePermission(context, treeUriString))
        {
            return (0, 0, false);
        }

        var resolver = context.ContentResolver;
        if (resolver is null)
        {
            return (0, 0, false);
        }

        try
        {
            var treeUri = A.Net.Uri.Parse(treeUriString);
            if (treeUri is null)
            {
                return (0, 0, false);
            }

            var docId = A.Provider.DocumentsContract.GetTreeDocumentId(treeUri);
            if (string.IsNullOrWhiteSpace(docId))
            {
                return (0, 0, false);
            }

            var childrenUri = A.Provider.DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, docId);
            if (childrenUri is null)
            {
                return (0, 0, false);
            }

            var projection = new[]
            {
                A.Provider.DocumentsContract.Document.ColumnMimeType,
                A.Provider.DocumentsContract.Document.ColumnSize
            };
            using var cursor = resolver.Query(childrenUri, projection, null, null, null);
            if (cursor is null)
            {
                return (0, 0, false);
            }

            var mimeIndex = cursor.GetColumnIndex(A.Provider.DocumentsContract.Document.ColumnMimeType);
            var sizeIndex = cursor.GetColumnIndex(A.Provider.DocumentsContract.Document.ColumnSize);
            var count = 0;
            long totalBytes = 0;
            while (cursor.MoveToNext())
            {
                var mime = mimeIndex >= 0 ? cursor.GetString(mimeIndex) : null;
                if (string.Equals(mime, A.Provider.DocumentsContract.Document.MimeTypeDir, StringComparison.Ordinal))
                {
                    continue;
                }

                count++;
                if (sizeIndex >= 0 && !cursor.IsNull(sizeIndex))
                {
                    totalBytes += Math.Max(0, cursor.GetLong(sizeIndex));
                }
            }

            return (count, totalBytes, true);
        }
        catch
        {
            return (0, 0, false);
        }
    }

    /// <summary>Counts files and total bytes this app published into MediaStore <c>Download/MPT</c>.</summary>
    public static (int FileCount, long TotalBytes) InspectMediaStoreDownloads(A.Content.Context context)
    {
        var resolver = context.ContentResolver;
        if (resolver is null)
        {
            return (0, 0);
        }

        try
        {
            var projection = new[] { A.Provider.MediaStore.IMediaColumns.Size };
            using var cursor = resolver.Query(
                A.Provider.MediaStore.Downloads.ExternalContentUri!,
                projection,
                $"{A.Provider.MediaStore.IMediaColumns.RelativePath} LIKE ?",
                [DownloadPublication.RelativeDirectory + "/%"],
                null);
            if (cursor is null)
            {
                return (0, 0);
            }

            var sizeIndex = cursor.GetColumnIndex(A.Provider.MediaStore.IMediaColumns.Size);
            var count = 0;
            long totalBytes = 0;
            while (cursor.MoveToNext())
            {
                count++;
                if (sizeIndex >= 0 && !cursor.IsNull(sizeIndex))
                {
                    totalBytes += Math.Max(0, cursor.GetLong(sizeIndex));
                }
            }

            return (count, totalBytes);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static async Task<string> PublishToSafTreeAsync(
        A.Content.ContentResolver resolver,
        string treeUriString,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var treeUri = A.Net.Uri.Parse(treeUriString)
            ?? throw new IOException("已授权的外部目录 URI 无效。");
        var treeDocId = A.Provider.DocumentsContract.GetTreeDocumentId(treeUri)
            ?? throw new IOException("无法解析外部目录标识。");
        var parentDocUri = A.Provider.DocumentsContract.BuildDocumentUriUsingTree(treeUri, treeDocId)
            ?? throw new IOException("无法定位外部目录。");

        var existingNames = ExistingSafNames(resolver, treeUri, treeDocId);
        var displayName = DownloadPublication.UniqueDisplayName(
            DownloadPublication.SanitizeDisplayName(sourcePath),
            existingNames.Contains);
        var mimeType = DownloadPublication.MimeTypeFor(displayName, AndroidMimeType);

        A.Net.Uri? created = null;
        try
        {
            created = A.Provider.DocumentsContract.CreateDocument(resolver, parentDocUri, mimeType, displayName)
                ?? throw new IOException("无法在已授权目录中创建文件。");
            await using (var input = File.OpenRead(sourcePath))
            await using (var output = resolver.OpenOutputStream(created) ?? throw new IOException("无法写入外部目录文件。"))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return created.ToString() ?? throw new IOException("外部文件创建后未返回有效链接。");
        }
        catch
        {
            if (created is not null)
            {
                try { A.Provider.DocumentsContract.DeleteDocument(resolver, created); }
                catch { }
            }

            throw;
        }
    }

    private static HashSet<string> ExistingSafNames(A.Content.ContentResolver resolver, A.Net.Uri treeUri, string treeDocId)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var childrenUri = A.Provider.DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, treeDocId);
        if (childrenUri is null)
        {
            return names;
        }

        try
        {
            var projection = new[] { A.Provider.DocumentsContract.Document.ColumnDisplayName };
            using var cursor = resolver.Query(childrenUri, projection, null, null, null);
            if (cursor is null)
            {
                return names;
            }

            var nameIndex = cursor.GetColumnIndex(A.Provider.DocumentsContract.Document.ColumnDisplayName);
            while (cursor.MoveToNext())
            {
                if (nameIndex >= 0 && cursor.GetString(nameIndex) is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }
        }
        catch
        {
            // If listing fails, DocumentsContract.CreateDocument still deduplicates names on its own.
        }

        return names;
    }

    private static async Task<string> PublishToMediaStoreAsync(
        A.Content.ContentResolver resolver,
        string path,
        CancellationToken cancellationToken)
    {
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
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var ready = new A.Content.ContentValues();
            ready.Put(A.Provider.MediaStore.IMediaColumns.IsPending, 0);
            if (resolver.Update(uri, ready, null, null) != 1)
            {
                throw new IOException("无法完成下载文件发布。");
            }

            return uri.ToString() ?? throw new IOException("下载文件发布后未返回有效链接。");
        }
        catch
        {
            if (uri is not null)
            {
                TryDeletePendingRow(resolver, uri);
            }

            throw;
        }
    }

    private static string? AndroidMimeType(string extension) =>
        A.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension);

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
