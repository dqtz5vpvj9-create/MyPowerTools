using MyPowerTools.Platform.Abstractions;
using A = global::Android;

namespace MyPowerTools.Platform.Android;

public sealed class AndroidBackgroundActivityService : IBackgroundActivityService
{
    internal const string ModuleIdExtra = "com.mypowertools.android.extra.MODULE_ID";

    internal static readonly ForegroundActivityCoordinator Coordinator = new(StartOrRefreshService, StopService);

    /// <summary>
    /// Raised with the modules whose foreground activity the user stopped from the notification.
    /// The payload contains only the touched module, so stopping one transfer never disables another.
    /// </summary>
    public static event Action<IReadOnlyList<string>>? StopRequested;

    public static Func<Task<bool>>? NotificationPermissionRequest { get; set; }

    public async Task<IDisposable> BeginAsync(string moduleId, string title, bool waitingForPeers, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        cancellationToken.ThrowIfCancellationRequested();
        if (NotificationPermissionRequest is { } request && !await request())
        {
            throw new UnauthorizedAccessException("请允许通知，以便在后台接收文件时显示状态和停止按钮。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var activity = Coordinator.Add(moduleId, title, waitingForPeers);
        return new Lease(activity.Id);
    }

    /// <summary>
    /// Stops the leases of one module, or all of them when <paramref name="moduleId"/> is null.
    /// The host subscriber disables exactly those modules; leases removed here are released as no-ops
    /// when the module unwinds its own work.
    /// </summary>
    internal static void RequestStop(string? moduleId)
    {
        var stopped = string.IsNullOrWhiteSpace(moduleId) ? Coordinator.RemoveAll() : Coordinator.RemoveModule(moduleId);
        RaiseStopRequested(stopped);
    }

    internal static void RaiseStopRequested(IReadOnlyList<string> moduleIds)
    {
        if (moduleIds.Count > 0)
        {
            StopRequested?.Invoke(moduleIds);
        }
    }

    private static void StartOrRefreshService()
    {
        var context = A.App.Application.Context;
        context.StartForegroundService(new A.Content.Intent(context, typeof(MptRuntimeService)));
    }

    private static void StopService()
    {
        try
        {
            var context = A.App.Application.Context;
            context.StopService(new A.Content.Intent(context, typeof(MptRuntimeService)));
        }
        catch (Exception ex)
        {
            A.Util.Log.Warn("MyPowerTools", "停止后台服务失败: " + ex.Message);
        }
    }

    private sealed class Lease(Guid id) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Coordinator.Remove(id);
            }
        }
    }
}

[A.App.Service(Name = "com.mypowertools.android.RuntimeService", Exported = false,
    ForegroundServiceType = A.Content.PM.ForegroundService.TypeConnectedDevice | A.Content.PM.ForegroundService.TypeDataSync)]
public sealed class MptRuntimeService : A.App.Service
{
    internal const string StopAction = "com.mypowertools.android.action.STOP";

    private const string ChannelId = "mpt-background";
    private const int NotificationId = 47;
    private const int StopRequestCodeBase = 100;

    // Notification.FOREGROUND_SERVICE_IMMEDIATE: show the foreground notification right away instead
    // of letting the platform defer it while the activity is visible.
    private const int ForegroundServiceImmediate = 1;

    public override A.OS.IBinder? OnBind(A.Content.Intent? intent) => null;

    public override A.App.StartCommandResult OnStartCommand(A.Content.Intent? intent, A.App.StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction)
        {
            AndroidBackgroundActivityService.RequestStop(intent.GetStringExtra(AndroidBackgroundActivityService.ModuleIdExtra));
        }

        var activities = AndroidBackgroundActivityService.Coordinator.Snapshot();
        if (activities.Count == 0)
        {
            StopForeground(A.App.StopForegroundFlags.Remove);
            StopSelf();
            return A.App.StartCommandResult.NotSticky;
        }

        using var notification = BuildNotification(activities);
        if (AndroidBackgroundActivityService.Coordinator.RequiresConnectedDeviceType)
        {
            StartForeground(NotificationId, notification, A.Content.PM.ForegroundService.TypeConnectedDevice);
        }
        else
        {
            StartForeground(NotificationId, notification, A.Content.PM.ForegroundService.TypeDataSync);
        }

        return A.App.StartCommandResult.NotSticky;
    }

    /// <summary>
    /// The platform (Android 15+ foreground-service timeout), the user or a process-level stop can
    /// destroy the service while modules still hold leases. Ending those modules here keeps status
    /// honest instead of leaving background work running without foreground protection.
    /// </summary>
    public override void OnDestroy()
    {
        var stopped = AndroidBackgroundActivityService.Coordinator.RemoveAll();
        if (stopped.Count > 0)
        {
            A.Util.Log.Warn("MyPowerTools", "后台服务已停止，正在结束模块: " + string.Join(", ", stopped));
            AndroidBackgroundActivityService.RaiseStopRequested(stopped);
            NotifyBackgroundWorkPaused();
        }

        base.OnDestroy();
    }

    private A.App.Notification BuildNotification(IReadOnlyList<ForegroundActivityCoordinator.Activity> activities)
    {
        var manager = (A.App.NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new A.App.NotificationChannel(ChannelId, "MPT 后台任务", A.App.NotificationImportance.Low));

        var summary = string.Join(" · ", activities.Select(activity => activity.Title).Distinct(StringComparer.Ordinal));
        var builder = new A.App.Notification.Builder(this, ChannelId)
            .SetSmallIcon(A.Resource.Drawable.StatSysUpload)
            .SetContentTitle("MyPowerTools 后台任务")
            .SetContentText(summary)
            .SetStyle(new A.App.Notification.BigTextStyle().BigText(summary))
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetShowWhen(false);
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            builder.SetForegroundServiceBehavior(ForegroundServiceImmediate);
        }

        if (PackageManager!.GetLaunchIntentForPackage(PackageName!) is { } launch)
        {
            builder.SetContentIntent(A.App.PendingIntent.GetActivity(this, 0, launch,
                A.App.PendingIntentFlags.Immutable | A.App.PendingIntentFlags.UpdateCurrent));
        }

        var requestCode = StopRequestCodeBase;
        foreach (var module in activities.GroupBy(activity => activity.ModuleId, StringComparer.Ordinal))
        {
            var stop = new A.Content.Intent(this, typeof(MptRuntimeService))
                .SetAction(StopAction)
                .PutExtra(AndroidBackgroundActivityService.ModuleIdExtra, module.Key);
            var pending = A.App.PendingIntent.GetService(this, requestCode++, stop,
                A.App.PendingIntentFlags.Immutable | A.App.PendingIntentFlags.UpdateCurrent);
            builder.AddAction(new A.App.Notification.Action.Builder(null, "停止 " + module.First().Title, pending).Build());
        }

        return builder.Build();
    }

    private void NotifyBackgroundWorkPaused()
    {
        try
        {
            var manager = (A.App.NotificationManager)GetSystemService(NotificationService)!;
            using var notification = new A.App.Notification.Builder(this, ChannelId)
                .SetSmallIcon(A.Resource.Drawable.StatNotifyMore)
                .SetContentTitle("后台任务已暂停")
                .SetContentText("系统已结束超时的后台任务，请重新打开 MyPowerTools 继续。")
                .SetAutoCancel(true)
                .Build();
            manager.Notify(NotificationId + 1, notification);
        }
        catch (Exception ex)
        {
            A.Util.Log.Warn("MyPowerTools", "发送后台暂停通知失败: " + ex.Message);
        }
    }
}
