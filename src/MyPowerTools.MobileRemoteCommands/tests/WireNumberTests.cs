using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// Regression for the real device payload path (the 299/300 settings bug).
///
/// On Android the phone page never calls the module directly: arguments become a protobuf
/// <c>Struct</c>, which has no integer type, so the module sees integral numbers as double-backed JSON
/// values. The page must therefore send real JSON integers and must read integral doubles back exactly -
/// a truncating read turns 300 into 299.
/// </summary>
public sealed class WireNumberTests
{
    [AvaloniaFact]
    public void Settings_are_sent_as_json_integers_not_doubles_or_strings()
    {
        using var harness = Harness();
        var view = harness.View;

        view.SettingsRetentionFieldForTests.Text = "300";
        view.SettingsKnownHostsFieldForTests.Text = "lab-host";
        harness.Pump();
        harness.Complete(view.SaveSettingsForTestsAsync());

        var payload = Assert.Single(harness.Module.SettingsUpdates);
        var retention = payload["historyRetention"]!.AsValue();

        Assert.True(retention.TryGetValue<int>(out var number), "historyRetention 必须是 JSON 整数。");
        Assert.Equal(300, number);
        Assert.False(retention.TryGetValue<double>(out _), "historyRetention 不能以浮点发送。");
        Assert.False(retention.TryGetValue<string>(out _), "historyRetention 不能以字符串发送。");
        Assert.True(payload["commandTimeoutMinutes"]!.AsValue().TryGetValue<int>(out _));
    }

    [AvaloniaFact]
    public void A_status_payload_that_came_back_as_protobuf_doubles_is_read_exactly()
    {
        var module = new FakeModule
        {
            SimulateHostStructTrip = true,
            Retention = 300,
            TimeoutMinutes = 45
        };
        module.AddHost("lab-host", host: "192.168.22.24", port: 2222);

        using var harness = SurfaceHarness.Create(module);
        var view = harness.View;
        harness.WaitFor(() => view.ViewModel.Hosts.Count == 1, "主机没有加载。");

        // 300 (not 299), 45 (not 44) and port 2222 (not the default 22).
        Assert.Equal("300", view.ViewModel.SettingsRetention);
        Assert.Equal("45", view.ViewModel.SettingsTimeoutMinutes);
        Assert.Equal(2222, view.ViewModel.Hosts[0].Port);
        Assert.Contains("2222", view.ViewModel.Hosts[0].EndpointText);
        Assert.Equal("300", view.SettingsRetentionFieldForTests.Text);
    }

    [AvaloniaFact]
    public void Saving_after_a_double_backed_status_still_sends_the_integer()
    {
        var module = new FakeModule
        {
            SimulateHostStructTrip = true,
            Retention = 300,
            TimeoutMinutes = 45
        };
        module.AddHost("lab-host");

        using var harness = SurfaceHarness.Create(module);
        var view = harness.View;
        harness.WaitFor(() => view.ViewModel.SettingsRetention == "300", "设置草稿没有填充。");

        // The user keeps the value the module reported; the round trip must not drift by one.
        view.SettingsRetentionFieldForTests.Text = "300";
        harness.Pump();
        harness.Complete(view.SaveSettingsForTestsAsync());

        var payload = Assert.Single(module.SettingsUpdates);
        Assert.Equal(300, payload["historyRetention"]!.GetValue<int>());
        Assert.Equal(300, module.Retention);
        Assert.False(view.ViewModel.SettingsDirty);
        Assert.Contains("已保存", view.ViewModel.SettingsMessage);
    }

    [Fact]
    public void The_json_readers_accept_every_integral_shape_a_host_can_deliver()
    {
        var payload = new JsonObject
        {
            ["intValue"] = 300,
            ["longValue"] = 300L,
            ["doubleValue"] = 300d,
            ["stringValue"] = "300",
            ["fractional"] = 300.5,
            ["nan"] = double.NaN
        };

        Assert.Equal(300, RemoteCommandsMobileJson.Int(payload, "intValue"));
        Assert.Equal(300, RemoteCommandsMobileJson.Int(payload, "longValue"));
        Assert.Equal(300, RemoteCommandsMobileJson.Int(payload, "doubleValue"));
        Assert.Equal(300, RemoteCommandsMobileJson.Int(payload, "stringValue"));

        // A fractional or non-finite number is not silently rounded: the caller sees "no value".
        Assert.Null(RemoteCommandsMobileJson.NullableInt(payload, "fractional"));
        Assert.Null(RemoteCommandsMobileJson.NullableInt(payload, "nan"));
        Assert.Equal(7, RemoteCommandsMobileJson.Int(payload, "missing", 7));
    }

    private static SurfaceHarness Harness()
    {
        var module = new FakeModule();
        module.AddHost("lab-host");
        return SurfaceHarness.Create(module);
    }
}
