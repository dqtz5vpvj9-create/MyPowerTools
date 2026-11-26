using MyPowerTools.Android.Pairing;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Android;

/// <summary>
/// The Android implementation of the platform scan contract. It is the one way the app opens the
/// camera: it starts <see cref="PairingQrScannerActivity"/>, which asks for
/// <c>android.permission.CAMERA</c> at that moment (never at launch) and returns one classified
/// <c>mpt://</c> link.
/// <para>
/// There is no second pairing protocol here. A scan produces exactly the string the user could paste
/// by hand — <c>mpt://pair/…</c>, <c>mpt://cloud/…</c> or <c>mpt://control/…</c> — and the owning
/// module command validates it, stores the credential and asks for confirmation. There is no
/// six-digit code, no shortened or re-encoded token, and no simulated result: a scan that does not
/// decode a real connection code reports an error.
/// </para>
/// <para>
/// Request bookkeeping (which scan a scanner activity belongs to, and which result resolves which
/// caller) lives in <see cref="ScanRequestRegistry"/> so it is unit tested without a camera.
/// </para>
/// </summary>
public sealed class MobileQrScan : IMobileQrScanner
{
    private readonly ScanRequestRegistry _requests = new();

    /// <summary>
    /// The prepared instance for the phone host. The Shell consumes <see cref="IMobileQrScanner"/>,
    /// so the Android host hands this instance to the Shell at construction or through the host's
    /// injection point; the Shell never references this assembly.
    /// </summary>
    public static MobileQrScan Default { get; } = new();

    public Task<MobileQrScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var activity = MainActivity.Current;
        if (activity is null)
        {
            return Task.FromResult(MobileQrScanResult.Failed(
                "应用界面还没有准备好，请稍后再试。", MobileQrScanError.Interrupted));
        }

        var request = _requests.Begin(out var result);

        try
        {
            PairingQrScannerActivity.Start(activity, request.Id);
        }
        catch (Exception ex)
        {
            AndroidStartupLog.Error("scan-start", ex);
            _requests.Complete(request.Id, MobileQrScanResult.Failed("无法打开相机页面。"));
        }

        return cancellationToken.CanBeCanceled
            ? WithCancellationAsync(request, cancellationToken)
            : result;
    }

    /// <summary>
    /// A cancelled token only resolves the caller: the camera closes through its own lifecycle (the
    /// user leaving the activity, or the activity being destroyed), so cancellation can never leave
    /// a camera running.
    /// </summary>
    private async Task<MobileQrScanResult> WithCancellationAsync(ScanRequest request, CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancelled.TrySetResult(true));
        var completed = await Task.WhenAny(request.Result, cancelled.Task).ConfigureAwait(true);
        if (completed == request.Result || request.Result.IsCompleted)
        {
            return await request.Result.ConfigureAwait(true);
        }

        if (_requests.IsCurrentRequest(request.Id))
        {
            _requests.Complete(request.Id, MobileQrScanResult.Cancelled("扫描已取消。"));
        }

        return await request.Result.ConfigureAwait(true);
    }

    internal TaskCompletionSource<bool>? BeginPermissionRequest(long requestId) =>
        _requests.BeginPermissionRequest(requestId);

    internal void CompletePermissionRequest(long requestId, bool granted) =>
        _requests.CompletePermissionRequest(requestId, granted);

    internal void Complete(long requestId, MobileQrScanResult result) =>
        _requests.Complete(requestId, result);
}
