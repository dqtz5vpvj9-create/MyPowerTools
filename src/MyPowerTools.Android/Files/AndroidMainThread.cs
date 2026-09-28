using A = global::Android;

namespace MyPowerTools.Android.Files;

/// <summary>
/// Marshals one short UI operation onto the platform's main thread. Starting a viewer and reading
/// the provider's path configuration both touch framework state that belongs to the UI thread, so
/// the launcher runs there even when a surface calls it from a module thread. The delegate is
/// bounded: it is posted once and never retried, and cancellation is only observed before the post
/// because an already-submitted launch cannot be recalled.
/// </summary>
internal static class AndroidMainThread
{
    internal static Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var mainLooper = A.OS.Looper.MainLooper;
        if (mainLooper is null)
        {
            return Task.FromException<T>(MptFileOpenErrors.LaunchFailed("系统界面线程不可用。"));
        }

        if (A.OS.Looper.MyLooper() == mainLooper)
        {
            try
            {
                return Task.FromResult(action());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new A.OS.Handler(mainLooper);
        var posted = handler.Post(new Java.Lang.Runnable(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }));

        if (!posted)
        {
            completion.TrySetException(MptFileOpenErrors.LaunchFailed("无法把打开请求提交到界面线程。"));
        }

        return completion.Task;
    }
}
