using MyPowerTools.Android.Pairing;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Android.Tests;

/// <summary>
/// The rapid-tap rules behind the scanner. A second scan must not be cancelled by the first
/// scanner's shutdown, and one result must resolve exactly one caller.
/// </summary>
public sealed class ScanRequestRegistryTests
{
    private static MobileQrScanResult AnyResult() =>
        MobileQrScanResult.Cancelled("已取消扫描。");

    [Fact]
    public async Task A_single_request_is_resolved_by_its_own_token()
    {
        var registry = new ScanRequestRegistry();
        var request = registry.Begin(out var result);

        Assert.True(registry.IsCurrentRequest(request.Id));
        registry.Complete(request.Id, AnyResult());

        Assert.False(registry.IsCurrentRequest(request.Id));
        Assert.True(result.IsCompleted);
        await result;
    }

    [Fact]
    public async Task A_new_scan_supersedes_the_previous_one_instead_of_hanging_it()
    {
        var registry = new ScanRequestRegistry();
        var first = registry.Begin(out var firstResult);
        var second = registry.Begin(out var secondResult);

        var superseded = await firstResult;
        Assert.Equal(MobileQrScanError.Interrupted, superseded.Error);
        Assert.False(superseded.Succeeded);

        Assert.False(secondResult.IsCompleted);
        Assert.True(registry.IsCurrentRequest(second.Id));

        registry.Complete(second.Id, AnyResult());
        await secondResult;
    }

    [Fact]
    public async Task An_earlier_finished_request_cannot_complete_a_later_one()
    {
        // The old activity's OnDestroy runs after the user already started a new scan, and must
        // resolve nothing.
        var registry = new ScanRequestRegistry();
        var first = registry.Begin(out var firstResult);
        registry.Complete(first.Id, AnyResult());
        _ = await firstResult;

        var second = registry.Begin(out var secondResult);
        registry.Complete(first.Id, MobileQrScanResult.Cancelled("扫描已中断。"));

        Assert.False(secondResult.IsCompleted);
        Assert.True(registry.IsCurrentRequest(second.Id));

        var success = MobileQrScanResult.Success(
            new MobileQrConnectionCode(MobileQrCodeKind.Pairing, "mpt://pair/abc", "已识别设备连接码", true));
        registry.Complete(second.Id, success);

        var resolved = await secondResult;
        Assert.True(resolved.Succeeded);
        Assert.Equal(MobileQrCodeKind.Pairing, resolved.Code!.Kind);
    }

    [Fact]
    public async Task A_result_is_delivered_exactly_once()
    {
        var registry = new ScanRequestRegistry();
        var request = registry.Begin(out var result);

        registry.Complete(request.Id, MobileQrScanResult.Cancelled("第一次"));
        registry.Complete(request.Id, MobileQrScanResult.Failed("第二次"));

        var resolved = await result;
        Assert.Equal("第一次", resolved.Message);
        Assert.Equal(MobileQrScanError.Cancelled, resolved.Error);
    }

    [Fact]
    public async Task A_permission_answer_only_resolves_the_request_that_asked()
    {
        var registry = new ScanRequestRegistry();
        var first = registry.Begin(out _);
        var permission = registry.BeginPermissionRequest(first.Id);
        Assert.NotNull(permission);

        // The user tapped again while the dialog was open.
        var second = registry.Begin(out var secondResult);
        registry.CompletePermissionRequest(first.Id, granted: true);
        Assert.False(permission!.Task.IsCompleted);
        Assert.False(secondResult.IsCompleted);

        var secondPermission = registry.BeginPermissionRequest(second.Id);
        Assert.NotNull(secondPermission);
        registry.CompletePermissionRequest(second.Id, granted: false);

        Assert.False(await secondPermission!.Task);
        Assert.False(secondResult.IsCompleted);
    }

    [Fact]
    public void An_unknown_token_is_ignored_instead_of_throwing()
    {
        var registry = new ScanRequestRegistry();
        registry.Complete(42, AnyResult());
        Assert.Null(registry.BeginPermissionRequest(42));
        registry.CompletePermissionRequest(42, granted: true);
        Assert.False(registry.IsCurrentRequest(42));
    }

    [Fact]
    public async Task The_permission_wait_is_created_once_per_request()
    {
        var registry = new ScanRequestRegistry();
        var request = registry.Begin(out _);

        var first = registry.BeginPermissionRequest(request.Id);
        var second = registry.BeginPermissionRequest(request.Id);

        Assert.Same(first, second);
        registry.CompletePermissionRequest(request.Id, granted: true);
        Assert.True(await first!.Task);

        // A second answer for the same request has nothing left to complete.
        registry.CompletePermissionRequest(request.Id, granted: false);
    }
}
