using Grpc.Core;
using MyPowerTools.Broker;
using MyPowerTools.HostControl;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed partial class RuntimeAcceptanceTests
{
    [Fact]
    public void Notification_center_updates_read_state_idempotently()
    {
        var center = new NotificationCenter();
        var notification = center.Publish("android-tools.notifications", "info", "Phone connected", "Bridge ready.");

        var alreadyUnread = center.SetReadState(notification.Id, false);
        var markedRead = center.SetReadState(notification.Id, true);
        var alreadyRead = center.SetReadState(notification.Id, true);

        Assert.NotNull(alreadyUnread);
        Assert.False(alreadyUnread!.Changed);
        Assert.False(alreadyUnread.Notification.IsRead);
        Assert.NotNull(markedRead);
        Assert.True(markedRead!.Changed);
        Assert.True(markedRead.Notification.IsRead);
        Assert.NotNull(alreadyRead);
        Assert.False(alreadyRead!.Changed);
        Assert.True(Assert.Single(center.List()).IsRead);
        Assert.Null(center.SetReadState("missing", true));
    }

    [Fact]
    public async Task HostControl_filters_notifications_and_sets_read_state()
    {
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-notification-read-state", Guid.NewGuid().ToString("N"))));
        var remote = runtime.PublishNotification(
            "android-tools.notifications",
            "info",
            "Remote message",
            "A new remote notification arrived.");
        runtime.PublishNotification(
            "smartbird-thermostat",
            "warning",
            "Temperature alert",
            "Target temperature exceeded.");
        var service = new HostControlGrpcService(
            runtime,
            new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-notification-read-state-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));
        var context = new TestServerCallContext();

        var initial = await service.ListNotifications(
            new HostProto.ListNotificationsRequest
            {
                ModuleId = "android-tools.notifications",
                ReadFilter = HostProto.NotificationReadFilter.Unread,
                Limit = 20
            },
            context);
        var markedRead = await service.SetNotificationReadState(
            new HostProto.SetNotificationReadStateRequest
            {
                NotificationId = remote.Id,
                IsRead = true
            },
            context);
        var repeated = await service.SetNotificationReadState(
            new HostProto.SetNotificationReadStateRequest
            {
                NotificationId = remote.Id,
                IsRead = true
            },
            context);
        var unread = await service.ListNotifications(
            new HostProto.ListNotificationsRequest
            {
                ModuleId = "android-tools.notifications",
                ReadFilter = HostProto.NotificationReadFilter.Unread,
                Limit = 20
            },
            context);
        var read = await service.ListNotifications(
            new HostProto.ListNotificationsRequest
            {
                ModuleId = "android-tools.notifications",
                ReadFilter = HostProto.NotificationReadFilter.Read,
                Limit = 20
            },
            context);

        Assert.Equal(1u, initial.TotalCount);
        Assert.Equal(1u, initial.UnreadCount);
        Assert.Equal(remote.Id, Assert.Single(initial.Notifications).Id);
        Assert.True(markedRead.Changed);
        Assert.True(markedRead.Notification.IsRead);
        Assert.False(repeated.Changed);
        Assert.Empty(unread.Notifications);
        Assert.Equal(1u, unread.TotalCount);
        Assert.Equal(0u, unread.UnreadCount);
        Assert.Equal(remote.Id, Assert.Single(read.Notifications).Id);
        Assert.Single(runtime.HostEventsSince(0).Where(evt => evt.Type == "notification.read-state-changed"));
    }

    [Fact]
    public async Task HostControl_rejects_unknown_notification_read_state_updates()
    {
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-notification-not-found", Guid.NewGuid().ToString("N"))));
        var service = new HostControlGrpcService(runtime);

        var exception = await Assert.ThrowsAsync<RpcException>(() => service.SetNotificationReadState(
            new HostProto.SetNotificationReadStateRequest
            {
                NotificationId = "missing-notification",
                IsRead = true
            },
            new TestServerCallContext()));

        Assert.Equal(StatusCode.NotFound, exception.StatusCode);
    }
}
