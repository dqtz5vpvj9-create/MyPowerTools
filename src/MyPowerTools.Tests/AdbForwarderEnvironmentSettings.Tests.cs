using AdbForwarder.Surface.Services;
using AdbForwarder.Surface.ViewModels;

namespace MyPowerTools.Tests;

public sealed class AdbForwarderEnvironmentSettingsTests
{
    [Fact]
    public async Task Devices_ini_is_validated_written_atomically_and_loaded_again()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mpt-adb-settings", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(directory, "devices.ini");
        var service = new AdbForwarderConfigurationService(configPath);
        var configuration = new AdbForwarderDeviceConfiguration(
            [new AdbForwarderForwardDeviceSetting("USB-SERIAL-1", 15555)],
            "WAKEUP-PAD",
            [new AdbForwarderWifiDeviceSetting("Pixel 9a", false, "USB-SERIAL-2", "10.33.0.243", 5555, 60)]);

        try
        {
            await service.SaveAsync(configuration);

            var text = await File.ReadAllTextAsync(configPath);
            Assert.Contains("[ForwardDevices]", text);
            Assert.Contains("USB-SERIAL-1=15555", text);
            Assert.Contains("[WifiAdb:Pixel 9a]", text);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

            var loaded = await service.LoadAsync([], [], CancellationToken.None);
            Assert.Equal("WAKEUP-PAD", loaded.WakeupPadDeviceId);
            Assert.Equal(15555, Assert.Single(loaded.ForwardDevices).Port);
            Assert.Equal("10.33.0.243", Assert.Single(loaded.WifiDevices).Host);
            Assert.Empty(loaded.Error);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Duplicate_device_ids_and_ports_are_rejected_before_write()
    {
        var configuration = new AdbForwarderDeviceConfiguration(
            [
                new AdbForwarderForwardDeviceSetting("same", 15555),
                new AdbForwarderForwardDeviceSetting("SAME", 15555)
            ],
            "",
            []);

        var error = Assert.Throws<ArgumentException>(() =>
            AdbForwarderConfigurationService.Validate(configuration));

        Assert.Contains("设备 ID 重复", error.Message);
        Assert.Contains("共享端口重复", error.Message);
    }

    [Fact]
    public void Product_settings_route_exposes_typed_wired_and_wifi_editors()
    {
        var snapshot = new AdbForwarderSnapshot(
            true,
            "adb",
            [],
            true,
            [],
            [],
            new AdbForwarderPlan([], [], [], [], false),
            [],
            1)
        {
            AdbPath = @"C:\Android\platform-tools\adb.exe",
            ConfiguredState = new AdbForwarderConfiguredState(
                @"C:\Users\test\AppData\Local\AdbForwarder\devices.ini",
                "WAKEUP",
                [new AdbConfiguredForwardDevice("USB-1", 15555, 30555, AdbConfiguredDeviceState.Disconnected, default, false)],
                [new AdbConfiguredWifiDevice("Pixel", true, "USB-2", "10.33.0.243", 5555, 60, false, true)],
                "")
        };

        var viewModel = new AdbForwarderViewModel(snapshot, "settings");

        Assert.True(viewModel.IsSettings);
        Assert.Equal(@"C:\Android\platform-tools\adb.exe", viewModel.AdbExecutablePath);
        Assert.Equal("WAKEUP", viewModel.WakeupPadDeviceId);
        Assert.Single(viewModel.ConfiguredForwardDeviceEditors);
        Assert.Single(viewModel.ConfiguredWifiDeviceEditors);
    }

    [Fact]
    public async Task Saving_an_unrelated_field_keeps_the_wifi_interval_and_wakeup_pad_from_the_service()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mpt-adb-settings", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(directory, "devices.ini");
        var configurationService = new AdbForwarderConfigurationService(configPath);
        var serviceState = new AdbForwarderServiceSnapshot(
            4242,
            DateTimeOffset.UtcNow,
            "active",
            "1/1 台有线设备在线",
            "adb",
            configPath,
            "WAKEUP-PAD",
            [new AdbForwarderServiceDevice("USB-SERIAL-1", 15555, 30555, "online", DateTimeOffset.UtcNow, true, true, "")],
            [new AdbForwarderServiceWifiDevice("Pixel 9a", false, "USB-SERIAL-2", "10.33.0.243", 5555, 900, "disabled", "")],
            [],
            []);
        var configuredState = AdbForwarderToolService.BuildConfiguredState(serviceState);

        Assert.Equal("WAKEUP-PAD", configuredState.WakeupPadDeviceId);
        Assert.Equal(900, Assert.Single(configuredState.WifiDevices).IntervalSeconds);

        var saved = new TaskCompletionSource<AdbForwarderEnvironmentSettings>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new AdbForwarderSnapshot(
            true,
            "adb",
            [],
            true,
            [],
            [],
            new AdbForwarderPlan([], [], [], [], false),
            [],
            1)
        {
            AdbPath = "adb",
            ConfiguredState = configuredState
        };
        var viewModel = new AdbForwarderViewModel(
            snapshot,
            "settings",
            saveEnvironment: settings =>
            {
                saved.TrySetResult(settings);
                return Task.CompletedTask;
            });

        try
        {
            viewModel.ConfiguredForwardDeviceEditors[0].Port = "15556";
            viewModel.SaveEnvironmentCommand.Execute(null);
            var environment = await saved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("WAKEUP-PAD", environment.Devices.WakeupPadDeviceId);
            Assert.Equal(900, Assert.Single(environment.Devices.WifiDevices).IntervalSeconds);

            await configurationService.SaveAsync(environment.Devices);
            var reloaded = await configurationService.LoadAsync([], [], CancellationToken.None);

            Assert.Equal("WAKEUP-PAD", reloaded.WakeupPadDeviceId);
            Assert.Equal(900, Assert.Single(reloaded.WifiDevices).IntervalSeconds);
            Assert.Equal(15556, Assert.Single(reloaded.ForwardDevices).Port);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
