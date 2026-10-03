using AdbForwarder.Surface.Services;
using AdbForwarder.Surface.ViewModels;

namespace MyPowerTools.Tests;

public sealed class AdbForwarderPortBoundsTests
{
    [Theory]
    [InlineData(50536)]
    [InlineData(65535)]
    public async Task Shared_port_overflow_is_rejected_before_overwriting_saved_configuration(int port)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mpt-adb-port-bounds", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "devices.ini");
        var service = new AdbForwarderConfigurationService(path);
        var valid = new AdbForwarderDeviceConfiguration([new("USB-BOUNDARY", 50535)], "", []);
        try
        {
            await service.SaveAsync(valid);
            var saved = await File.ReadAllTextAsync(path);
            var invalid = valid with { ForwardDevices = [new("USB-BOUNDARY", port)] };
            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(invalid));
            Assert.Equal(saved, await File.ReadAllTextAsync(path));
            var loaded = await service.LoadAsync([], [], CancellationToken.None);
            Assert.Empty(loaded.Error);
            Assert.Equal(65535, Assert.Single(loaded.ForwardDevices).InternalPort);
            var editor = new AdbForwardDeviceSettingEditorViewModel(new("USB-BOUNDARY", port), _ => { });
            Assert.False(editor.TryBuild(out _));
            Assert.Contains("50535", editor.ValidationMessage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Manually_edited_out_of_range_shared_port_reports_error_without_exposing_invalid_endpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mpt-adb-port-bounds", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "devices.ini");
        try
        {
            await File.WriteAllTextAsync(path, "[ForwardDevices]\nUSB-BOUNDARY=50536\n");
            var state = await new AdbForwarderConfigurationService(path).LoadAsync([], [], CancellationToken.None);
            Assert.Empty(state.ForwardDevices);
            Assert.Contains("50535", state.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Wifi_port_keeps_the_full_tcp_range()
    {
        AdbForwarderConfigurationService.Validate(new([], "", [new("Wifi", false, "USB", "127.0.0.1", 65535, 60)]));
        var editor = new AdbWifiDeviceSettingEditorViewModel(new("Wifi", false, "USB", "127.0.0.1", 65535, 60), _ => { });
        Assert.True(editor.TryBuild(out var setting));
        Assert.Equal(65535, setting.Port);
    }
}
