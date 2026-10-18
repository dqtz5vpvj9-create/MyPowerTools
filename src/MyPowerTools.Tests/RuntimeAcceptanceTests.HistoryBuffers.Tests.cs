using System.Reflection;
using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MyPowerTools.Broker;
using MyPowerTools.HostControl;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.ViewModels;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed partial class RuntimeAcceptanceTests
{
    [Fact]
    public async Task History_rollover_does_not_remove_active_execution_cancellation()
    {
        var transport = new RecordingSettingsTransportRuntime("inproc-dotnet") { BlockCommandUntilCancelled = true };
        await using var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-history-active", Guid.NewGuid().ToString("N"))), [transport]);
        runtime.Load(Path.Combine(Root, "modules"));
        var request = new CommandRequest("active-history-rollover", "screenease.native-writer.status", new JsonObject());
        var execution = runtime.ExecuteCommandAsync(request, CancellationToken.None);
        await transport.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Fill only the diagnostic feed, not the execution machinery or real transports.
        var history = (CommandHistory)typeof(MptHostRuntime).GetField("_commandHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        for (var i = 0; i <= history.GetStats().MaxEntries; i++)
            history.Add(new CommandRequest($"synthetic-{i}", "synthetic", new JsonObject()), null, "accepted");
        Assert.DoesNotContain(runtime.ListCommandHistory(), entry => entry.InvocationId == request.InvocationId);
        var cancellation = runtime.CancelCommand(request.InvocationId);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancellation.Accepted);
        Assert.Equal("cancelled", result.State);
        Assert.False(result.Success);
        Assert.True(history.GetStats().UnretainedCompletionCount > 0);
    }

    [Fact]
    public async Task HostControl_reports_unread_rollover_and_record_truncation()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-history-protocol", Guid.NewGuid().ToString("N"));
        await using var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current(), RuntimePaths.Create(root));
        var old = runtime.PublishNotification("test", "info", "old unread", "body");
        for (var i = 0; i < 2100; i++) runtime.PublishNotification("test", "info", "row", "body");
        var large = runtime.PublishNotification("test", "warning", "large", new string('界', 20000));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(root, "audit.jsonl")));
        var response = await service.ListNotifications(new HostProto.ListNotificationsRequest { Limit = 200 }, new TestServerCallContext());
        Assert.NotNull(response.HistoryBuffer);
        Assert.True(response.HistoryBuffer.EvictedUnreadCount > 0);
        Assert.True(response.HistoryBuffer.EvictedCount >= response.HistoryBuffer.EvictedUnreadCount);
        Assert.Equal(response.TotalCount, response.UnreadCount);
        Assert.True(response.HistoryBuffer.RetainedBytes <= response.HistoryBuffer.MaxBytes);
        var item = Assert.Single(response.Notifications, item => item.Id == large.Id);
        Assert.True(item.IsTruncated);
        Assert.True(item.OriginalBytes > item.RetainedBytes);
        Assert.Null(runtime.SetNotificationReadState(old.Id, true));
        var view = ShellPageViewModelFactory.FromNotifications(response);
        Assert.True(view.HasHistoryNotice);
        Assert.Contains("were unread; they were not marked read", view.HistoryNotice);
        Assert.Contains("not recoverable", view.HistoryNotice);
        Assert.Contains(view.Notifications, item => item.Body.Contains("History text truncated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostControl_notification_filters_use_half_open_time_and_exact_level()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-history-filter", Guid.NewGuid().ToString("N"));
        await using var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current(), RuntimePaths.Create(root));
        var selected = runtime.PublishNotification("Test", "warning", "selected", "body");
        runtime.PublishNotification("test", "info", "other severity", "body");
        runtime.PublishNotification("other", "warning", "other module", "body");
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(root, "audit.jsonl")));
        var query = new HostProto.ListNotificationsRequest { ModuleId = "test", Level = "WARNING", Since = Timestamp.FromDateTimeOffset(selected.Time) };
        var result = await service.ListNotifications(query, new TestServerCallContext());
        Assert.Equal(selected.Id, Assert.Single(result.Notifications).Id);
        query.Since = Timestamp.FromDateTimeOffset(selected.Time.AddTicks(-1));
        query.Until = Timestamp.FromDateTimeOffset(selected.Time);
        Assert.Empty((await service.ListNotifications(query, new TestServerCallContext())).Notifications);
        query.Since = query.Until;
        var failure = await Assert.ThrowsAsync<RpcException>(() => service.ListNotifications(query, new TestServerCallContext()));
        Assert.Equal(StatusCode.InvalidArgument, failure.StatusCode);
    }

    [Fact]
    public void Older_notification_responses_have_no_fabricated_rollover_warning()
    {
        var view = ShellPageViewModelFactory.FromNotifications(new HostProto.ListNotificationsResponse());
        Assert.False(view.HasHistoryNotice);
        Assert.Empty(view.HistoryNotice);
    }
}
