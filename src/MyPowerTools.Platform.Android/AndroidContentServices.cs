using MyPowerTools.Platform.Abstractions;
using A = global::Android;

namespace MyPowerTools.Platform.Android;

public sealed class AndroidNotificationService : INotificationService
{
    private static int _nextId;
    public Task PublishAsync(string title, string body, CancellationToken token) =>
        PublishAsync(new DesktopNotificationRequest(Guid.NewGuid().ToString("N"), title, body), token);

    public Task PublishAsync(DesktopNotificationRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var context = A.App.Application.Context;
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            context.CheckSelfPermission(A.Manifest.Permission.PostNotifications) != A.Content.PM.Permission.Granted)
            throw new UnauthorizedAccessException("请在 Android 设置中允许 MyPowerTools 发送通知。");
        var manager = (A.App.NotificationManager)context.GetSystemService(A.Content.Context.NotificationService)!;
        manager.CreateNotificationChannel(new A.App.NotificationChannel("mpt-tools", "工具通知", A.App.NotificationImportance.Default));
        var intent = context.PackageManager!.GetLaunchIntentForPackage(context.PackageName!)!;
        if (request.ActivationUri is { Length: > 0 } uri)
            intent.SetAction(A.Content.Intent.ActionView).SetData(A.Net.Uri.Parse(uri));
        var id = Interlocked.Increment(ref _nextId);
        var open = A.App.PendingIntent.GetActivity(context, id, intent, A.App.PendingIntentFlags.Immutable | A.App.PendingIntentFlags.UpdateCurrent);
        using var notification = new A.App.Notification.Builder(context, "mpt-tools")
            .SetSmallIcon(A.Resource.Drawable.StatNotifyMore)
            .SetContentTitle(request.Title).SetContentText(request.Body)
            .SetStyle(new A.App.Notification.BigTextStyle().BigText(request.Body))
            .SetContentIntent(open).SetAutoCancel(true).Build();
        manager.Notify(request.Id, 0, notification);
        return Task.CompletedTask;
    }
}

public sealed class AndroidClipboardService : IClipboardImageService
{
    public async Task<ClipboardImagePayload> ReadPngAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var context = A.App.Application.Context;
        var clipboard = (A.Content.ClipboardManager)context.GetSystemService(A.Content.Context.ClipboardService)!;
        var uri = clipboard.PrimaryClip?.GetItemAt(0)?.Uri
            ?? throw new InvalidOperationException("剪贴板中没有图片，请先复制图片或从系统分享菜单打开。");
        await using var input = context.ContentResolver!.OpenInputStream(uri)
            ?? throw new IOException("无法读取剪贴板图片。");
        using var bitmap = await A.Graphics.BitmapFactory.DecodeStreamAsync(input)
            ?? throw new InvalidDataException("剪贴板内容不是支持的图片。");
        await using var output = new MemoryStream();
        await bitmap.CompressAsync(A.Graphics.Bitmap.CompressFormat.Png!, 100, output);
        token.ThrowIfCancellationRequested();
        return new ClipboardImagePayload(output.ToArray(), bitmap.Width, bitmap.Height);
    }

    public Task WriteTextAsync(string value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var clipboard = (A.Content.ClipboardManager)A.App.Application.Context.GetSystemService(A.Content.Context.ClipboardService)!;
        clipboard.PrimaryClip = A.Content.ClipData.NewPlainText("MyPowerTools", value);
        return Task.CompletedTask;
    }
}
