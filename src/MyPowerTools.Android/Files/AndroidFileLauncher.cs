using A = global::Android;

namespace MyPowerTools.Android.Files;

/// <summary>
/// Opens a phone-local file in the system viewer. This is the Android implementation behind the
/// Shell's optional <c>MptAvaloniaSurfaceContext.OpenFileAsync</c> capability.
/// <para>
/// The path is checked against the host's own receive/staging roots (<see cref="SharedFileRootSpec"/>)
/// and then shared through the read-only <see cref="MptFileProvider"/> as a <c>content://</c> URI.
/// A bare <c>file://</c> URI is never handed to another app, nothing is copied into a public
/// directory and no path permission is widened: the viewer receives
/// <c>FLAG_GRANT_READ_URI_PERMISSION</c> for that single URI, carried in the intent and its
/// ClipData, for the duration of that one launch.
/// </para>
/// <para>
/// The launch is bounded: at most <see cref="SharedFileViewPlan.MaxAttempts"/> intents are started
/// (the inferred type, then one generic retry), on the UI thread, and no part of this class reads
/// file content.
/// </para>
/// </summary>
public static class AndroidFileLauncher
{
    private const string LogStage = "file-open";

    /// <summary>
    /// The delegate to assign to the surface context's <c>OpenFileAsync</c> capability. It is the
    /// public entry point with the exact signature the Shell expects.
    /// </summary>
    public static Func<string, CancellationToken, Task<bool>> CreateOpenFileDelegate() => OpenAsync;

    /// <summary>
    /// Opens <paramref name="path"/> in whatever activity the system resolves for its content type.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> once the system accepted the viewer intent — which means a viewer or
    /// the system's own "open with" picker was launched, never that the user read the file. A
    /// dismissal is not observable through this API. <see langword="false"/> only when the request
    /// was already cancelled before anything was launched; every other failure throws
    /// <see cref="MptFileOpenException"/> with a user-understandable message and a
    /// <see cref="MptFileOpenFailure"/> reason.
    /// </returns>
    /// <exception cref="MptFileOpenException">The file is missing, outside the shared roots, unreadable, or no viewer exists.</exception>
    public static async Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            AndroidStartupLog.Info(LogStage, "打开请求在启动查看器前被取消。");
            return false;
        }

        var context = A.App.Application.Context;
        if (context is null)
        {
            throw MptFileOpenErrors.LaunchFailed("应用上下文不可用。");
        }

        if (path.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
        {
            return await OpenContentUriAsync(context, path, cancellationToken).ConfigureAwait(false);
        }

        var policy = MptSharedFileRoots.Policy(context);
        var verdict = policy.Evaluate(path, out var file);
        if (verdict != SharedFileAccessVerdict.Allowed)
        {
            // The rejected path itself is never logged; the verdict and the root count are enough to
            // tell a missing file apart from an out-of-bounds request.
            AndroidStartupLog.Write("warn", LogStage,
                $"拒绝打开：{verdict}；允许目录数 {policy.AllowedRoots.Count}。");
            throw MptFileOpenErrors.FromVerdict(verdict, policy.AllowedRoots);
        }

        MptCurrentActivity.EnsureRegistered(context);
        var authority = MptSharedFileRoots.Authority(context);
        if (cancellationToken.IsCancellationRequested)
        {
            AndroidStartupLog.Info(LogStage, "打开请求在启动查看器前被取消。");
            return false;
        }

        var rootName = MptSharedFileRoots.RootName(context, file) ?? "unknown";
        var launched = await AndroidMainThread
            .InvokeAsync(() => StartViewer(context, authority, file), cancellationToken)
            .ConfigureAwait(false);
        if (launched)
        {
            AndroidStartupLog.Info(LogStage, $"已把文件交给系统查看器（root={rootName}）。");
        }

        return launched;
    }

    /// <summary>Runs on the UI thread. Returns true only after the system accepted the intent.</summary>
    private static bool StartViewer(A.Content.Context context, string authority, string file)
    {
        var uri = CreateShareUri(context, authority, file);
        var plan = SharedFileViewPlan.Create(file, SystemMimeType);
        var activity = MptCurrentActivity.Current;
        var host = (A.Content.Context?)activity ?? context;
        A.Content.ActivityNotFoundException? notFound = null;

        foreach (var mimeType in plan.MimeAttempts)
        {
            var isPackage = mimeType == "application/vnd.android.package-archive";
            // The package installer owns the unknown-sources consent and installation confirmation.
            var intent = new A.Content.Intent(isPackage ? A.Content.Intent.ActionInstallPackage : A.Content.Intent.ActionView);
            intent.SetDataAndType(uri, mimeType);

            // Read-only and per launch: the flag grants exactly this URI to whatever the system
            // resolves, and ClipData carries the same single URI so the resolver forwards the grant
            // to the app the user picks. No GrantUriPermission call is made, so nothing survives the
            // launch and no other URI of this provider is reachable.
            intent.AddFlags(A.Content.ActivityFlags.GrantReadUriPermission);
            intent.ClipData = A.Content.ClipData.NewRawUri(Path.GetFileName(file), uri);

            if (activity is null)
            {
                // Required when the intent is started from the application context.
                intent.AddFlags(A.Content.ActivityFlags.NewTask);
            }

            try
            {
                host.StartActivity(intent);
                return true;
            }
            catch (A.Content.ActivityNotFoundException ex)
            {
                // Bounded fallback: one generic retry, then the failure is reported.
                notFound = ex;
                AndroidStartupLog.Write("warn", LogStage, $"没有处理 {mimeType} 的应用，尝试下一个类型。");
            }
            catch (Java.Lang.SecurityException ex)
            {
                throw MptFileOpenErrors.LaunchFailed("系统拒绝了这次读取授权。", ex);
            }
            catch (Java.Lang.Exception ex)
            {
                throw MptFileOpenErrors.LaunchFailed(string.Empty, ex);
            }
        }

        throw MptFileOpenErrors.NoViewer(file, notFound);
    }

    /// <summary>Maps the checked file to the provider URI a viewer may read.</summary>
    private static A.Net.Uri CreateShareUri(A.Content.Context context, string authority, string file)
    {
        try
        {
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, authority, new Java.IO.File(file));
            if (uri is null)
            {
                throw MptFileOpenErrors.ProviderMisconfigured("提供者未返回 URI。");
            }

            return uri;
        }
        catch (Java.Lang.IllegalArgumentException ex)
        {
            // "Couldn't find meta-data for provider with authority ..." or "Failed to find
            // configured root that contains ...": the manifest registration, the paths resource and
            // the runtime directories do not agree.
            throw MptFileOpenErrors.ProviderMisconfigured(string.Empty, ex);
        }
        catch (Java.Lang.Exception ex)
        {
            throw MptFileOpenErrors.LaunchFailed("无法生成只读共享链接。", ex);
        }
    }

    /// <summary>The platform's extension map, used only when the built-in table has no answer.</summary>
    private static string? SystemMimeType(string extension) =>
        A.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(extension);

    /// <summary>
    /// Opens an already-exported external file (<c>content://</c> URI from SAF or MediaStore)
    /// directly in the system viewer / package installer after verifying read access.
    /// </summary>
    private static async Task<bool> OpenContentUriAsync(
        A.Content.Context context,
        string contentUriString,
        CancellationToken cancellationToken)
    {
        var uri = A.Net.Uri.Parse(contentUriString)
            ?? throw MptFileOpenErrors.LaunchFailed("外部文件链接格式无效。");
        var resolver = context.ContentResolver
            ?? throw MptFileOpenErrors.LaunchFailed("Android 内容服务不可用。");

        try
        {
            using var descriptor = resolver.OpenAssetFileDescriptor(uri, "r")
                ?? throw MptFileOpenErrors.LaunchFailed("文件已被外部应用移走或删除，请在消息菜单中重新下载。");
        }
        catch (MptFileOpenException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw MptFileOpenErrors.LaunchFailed("文件已不在外部目录或目录授权已失效，请重新下载或重新授权目录。", ex);
        }

        var displayName = ResolveContentDisplayName(resolver, uri) ?? "download";
        var resolvedMime = resolver.GetType(uri);
        MptCurrentActivity.EnsureRegistered(context);
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var launched = await AndroidMainThread
            .InvokeAsync(() => StartContentViewer(context, uri, displayName, resolvedMime), cancellationToken)
            .ConfigureAwait(false);
        if (launched)
        {
            AndroidStartupLog.Info(LogStage, "已把外部目录文件交给系统查看器（content-uri）。");
        }

        return launched;
    }

    private static string? ResolveContentDisplayName(A.Content.ContentResolver resolver, A.Net.Uri uri)
    {
        try
        {
            using var cursor = resolver.Query(uri, [A.Provider.IOpenableColumns.DisplayName], null, null, null);
            if (cursor?.MoveToFirst() == true)
            {
                return cursor.GetString(0);
            }
        }
        catch
        {
        }

        return uri.LastPathSegment;
    }

    private static bool StartContentViewer(
        A.Content.Context context,
        A.Net.Uri uri,
        string displayName,
        string? resolvedMime)
    {
        var plan = SharedFileViewPlan.Create(displayName, SystemMimeType);
        var attempts = new List<string>();
        if (!string.IsNullOrWhiteSpace(resolvedMime) &&
            !string.Equals(resolvedMime, "*/*", StringComparison.Ordinal) &&
            !string.Equals(resolvedMime, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            attempts.Add(resolvedMime);
        }

        foreach (var mime in plan.MimeAttempts)
        {
            if (!attempts.Contains(mime, StringComparer.OrdinalIgnoreCase))
            {
                attempts.Add(mime);
            }
        }

        var activity = MptCurrentActivity.Current;
        var host = (A.Content.Context?)activity ?? context;
        A.Content.ActivityNotFoundException? notFound = null;

        foreach (var mimeType in attempts)
        {
            var isPackage = mimeType == "application/vnd.android.package-archive";
#pragma warning disable CA1422
            var intent = new A.Content.Intent(isPackage ? A.Content.Intent.ActionInstallPackage : A.Content.Intent.ActionView);
#pragma warning restore CA1422
            intent.SetDataAndType(uri, mimeType);
            intent.AddFlags(A.Content.ActivityFlags.GrantReadUriPermission);
            intent.ClipData = A.Content.ClipData.NewRawUri(displayName, uri);
            if (activity is null)
            {
                intent.AddFlags(A.Content.ActivityFlags.NewTask);
            }

            try
            {
                host.StartActivity(intent);
                return true;
            }
            catch (A.Content.ActivityNotFoundException ex)
            {
                notFound = ex;
            }
            catch (Java.Lang.SecurityException ex)
            {
                throw MptFileOpenErrors.LaunchFailed("系统拒绝了外部文件读取授权。", ex);
            }
            catch (Java.Lang.Exception ex)
            {
                throw MptFileOpenErrors.LaunchFailed(string.Empty, ex);
            }
        }

        throw MptFileOpenErrors.NoViewer(displayName, notFound);
    }
}
