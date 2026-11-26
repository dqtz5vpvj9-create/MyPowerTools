namespace MyPowerTools.Platform.Android.Tests;

public sealed class ForegroundActivityCoordinatorTests
{
    /// <summary>Records service transitions together with the number of live leases at that moment.</summary>
    private sealed class ServiceProbe
    {
        private ForegroundActivityCoordinator? _coordinator;

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public List<int> ActiveCountsAtStop { get; } = [];

        public ForegroundActivityCoordinator Create()
        {
            _coordinator = new ForegroundActivityCoordinator(
                () => Starts++,
                () => { Stops++; ActiveCountsAtStop.Add(_coordinator!.Count); });
            return _coordinator;
        }
    }

    [Fact]
    public void Parallel_activities_keep_the_service_alive_until_the_last_release()
    {
        var probe = new ServiceProbe();
        var coordinator = probe.Create();

        var transfer = coordinator.Add("file-transfer", "文件互传进行中", false);
        var notifications = coordinator.Add("remote-notifications", "通知轮询", false);

        Assert.Equal(2, probe.Starts);
        Assert.Equal(0, probe.Stops);
        Assert.Equal(2, coordinator.Count);

        Assert.True(coordinator.Remove(transfer.Id));
        Assert.Equal(0, probe.Stops);
        Assert.False(coordinator.Remove(transfer.Id));
        Assert.Equal(0, probe.Stops);

        Assert.True(coordinator.Remove(notifications.Id));
        Assert.Equal(1, probe.Stops);
        Assert.Equal(0, coordinator.Count);
    }

    [Fact]
    public void Stopping_one_module_keeps_other_modules_in_the_foreground()
    {
        var probe = new ServiceProbe();
        var coordinator = probe.Create();
        var transfer = coordinator.Add("file-transfer", "文件互传正在等待来件", true);
        coordinator.Add("paste-image", "处理图片", false);

        var stopped = coordinator.RemoveModule("file-transfer");

        Assert.Equal(new[] { "file-transfer" }, stopped);
        Assert.Equal(0, probe.Stops);
        Assert.False(coordinator.Remove(transfer.Id));
        var remaining = Assert.Single(coordinator.Snapshot());
        Assert.Equal("paste-image", remaining.ModuleId);
    }

    [Fact]
    public void Stopping_a_module_without_activities_changes_nothing()
    {
        var probe = new ServiceProbe();
        var coordinator = probe.Create();
        coordinator.Add("file-transfer", "文件互传进行中", false);

        Assert.Empty(coordinator.RemoveModule("paste-image"));
        Assert.Equal(0, probe.Stops);
        Assert.Single(coordinator.Snapshot());
    }

    [Fact]
    public void Removing_everything_stops_the_service_once()
    {
        var probe = new ServiceProbe();
        var coordinator = probe.Create();
        coordinator.Add("file-transfer", "文件互传进行中", false);
        coordinator.Add("remote-notifications", "通知轮询", false);

        Assert.Equal(new[] { "file-transfer", "remote-notifications" }, coordinator.RemoveAll());
        Assert.Equal(1, probe.Stops);
        Assert.Empty(coordinator.RemoveAll());
        Assert.Equal(1, probe.Stops);
    }

    [Fact]
    public void Waiting_for_peers_selects_the_connected_device_type()
    {
        var probe = new ServiceProbe();
        var coordinator = probe.Create();
        Assert.False(coordinator.RequiresConnectedDeviceType);

        coordinator.Add("file-transfer", "发送文件", false);
        Assert.False(coordinator.RequiresConnectedDeviceType);

        var receiver = coordinator.Add("file-transfer", "文件互传正在等待来件", true);
        Assert.True(coordinator.RequiresConnectedDeviceType);

        coordinator.Remove(receiver.Id);
        Assert.False(coordinator.RequiresConnectedDeviceType);
    }

    [Fact]
    public void A_rejected_service_start_rolls_back_the_activity()
    {
        var stops = 0;
        var coordinator = new ForegroundActivityCoordinator(
            () => throw new InvalidOperationException("后台启动被系统拒绝。"),
            () => stops++);

        Assert.Throws<InvalidOperationException>(() => coordinator.Add("file-transfer", "文件互传进行中", false));

        Assert.Equal(0, coordinator.Count);
        Assert.Equal(1, stops);
    }

    [Fact]
    public void Concurrent_add_and_release_never_stop_a_service_that_still_has_leases()
    {
        var stops = new List<int>();
        ForegroundActivityCoordinator? coordinator = null;
        coordinator = new ForegroundActivityCoordinator(
            () => { },
            () => stops.Add(coordinator!.Count));

        Parallel.For(0, 256, index =>
        {
            var activity = coordinator.Add("module-" + (index % 4), "任务", false);
            if ((index & 1) == 0)
            {
                coordinator.Remove(activity.Id);
            }

            Thread.SpinWait(64);
            coordinator.Remove(activity.Id);
        });

        Assert.Equal(0, coordinator.Count);
        Assert.NotEmpty(stops);
        Assert.All(stops, count => Assert.Equal(0, count));
    }
}
