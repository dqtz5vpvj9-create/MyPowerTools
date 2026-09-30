using MyPowerTools.AvaloniaSdk;
using A = global::Android;

namespace MyPowerTools.Android.CloudAuthorization;

/// <summary>Login requests and credentials remain in process, never in an Android intent.</summary>
internal static class CloudLogin
{
    private static LoginRequest? _current;

    internal sealed class LoginRequest(string providerId)
    {
        internal string ProviderId { get; } = providerId;
        internal TaskCompletionSource<MptCloudAuthorizationResult?> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CloudLoginActivity? Activity { get; set; }
    }

    public static async Task<MptCloudAuthorizationResult?> AuthorizeAsync(string providerId, CancellationToken cancellationToken)
    {
        if (providerId is not ("quark" or "baidu")) throw new ArgumentException("暂不支持这个网盘。");
        cancellationToken.ThrowIfCancellationRequested();
        var activity = MainActivity.Current ?? throw new InvalidOperationException("请返回应用后重试登录。");
        var request = new LoginRequest(providerId);
        activity.RunOnUiThread(() =>
        {
            if (cancellationToken.IsCancellationRequested) { request.Result.TrySetResult(null); return; }
            if (_current is { } previous) Complete(previous, null);
            _current = request;
            try { activity.StartActivity(new A.Content.Intent(activity, typeof(CloudLoginActivity))); }
            catch { _current = null; request.Result.TrySetException(new InvalidOperationException("无法打开网盘登录页面，请稍后重试。")); }
        });
        using var registration = cancellationToken.Register(() => activity.RunOnUiThread(() => Complete(request, null)));
        return await request.Result.Task.ConfigureAwait(false);
    }

    internal static LoginRequest? Attach(CloudLoginActivity activity)
    {
        if (_current is not { } request) return null;
        request.Activity = activity;
        return request;
    }

    internal static bool CanClearSession(LoginRequest? request) => _current is null || ReferenceEquals(_current, request);

    internal static void Complete(LoginRequest request, MptCloudAuthorizationResult? result)
    {
        if (ReferenceEquals(_current, request)) _current = null;
        request.Result.TrySetResult(result);
        request.Activity?.Finish();
    }
}
