using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace MyPowerTools.MobileNotifications.Tests;

/// <summary>
/// Failures that carry no <c>Error</c> object.
///
/// Hosts can report <c>Success=false</c> with a null error, and non-JSON output for a command whose
/// contract is a JSON object. Neither may be mistaken for a successful answer: the page has to show a
/// real failure, must not apply a payload it could not confirm, and must keep the list and the typed
/// settings drafts untouched.
/// </summary>
public sealed class MobileNotificationsFailureTests
{
    [AvaloniaFact]
    public void Failed_result_without_error_object_is_reported_and_never_applied()
    {
        var module = new FakeNotificationsModule();
        module.State["keyConfigured"] = true;
        module.Seed(TestRecords.Build("构建", "构建完成", new DateTimeOffset(DateTime.Today.AddHours(9)), "one"));

        // A lying payload: the host says the command failed, yet the output would look successful.
        module.FailuresWithoutError.Add(FakeNotificationsModule.SyncCommand);
        module.FailureOutputWithoutError =
            "{\"connectionState\":\"ok\",\"accepted\":3,\"fetched\":3,\"lastPoll\":\"2026/09/28 09:00:00\"}";

        using var host = MobileNotificationHost.Open(module);
        var viewModel = (MobileNotificationsViewModel)host.View.DataContext!;

        // Persist real settings first: a later failure must not roll them back or overwrite them with
        // the payload it was not allowed to apply.
        host.Click(host.Find<Button>("SettingsToggle")!);
        host.Settle();
        var hostBox = host.Find<Border>("SettingsPanel")!.GetVisualDescendants().OfType<TextBox>()
            .Single(box => box.PlaceholderText == "message.example.com");
        hostBox.Text = "draft.example.com";
        host.Click(host.Find<Button>("SaveServerSettings")!);
        host.Click(host.Find<Button>("SettingsBack")!);
        host.Settle();
        Assert.Equal("draft.example.com", viewModel.HostDraft);
        var savedEndpoint = viewModel.ServerText;

        viewModel.SyncCommand.Execute(null);
        host.Settle();

        // The failed command was sent, but its payload was not applied and no success was claimed.
        Assert.Contains(FakeNotificationsModule.SyncCommand, module.Commands);
        Assert.True(host.Find<Border>("ErrorCard")!.IsVisible);
        Assert.Contains("connectionState", host.AllText());
        Assert.DoesNotContain("新增 3 条", host.AllText());
        Assert.Equal("", viewModel.SyncResult);
        Assert.NotEqual("ok", viewModel.ConnectionState);

        // The real history list is still there.
        Assert.Single(host.Rows());
        Assert.Contains("构建完成", host.AllText());

        // The saved connection settings survived the failure untouched.
        Assert.Equal("draft.example.com", viewModel.HostDraft);
        Assert.Equal(savedEndpoint, viewModel.ServerText);
        Assert.Contains("draft.example.com", viewModel.EndpointText);
        host.Click(host.Find<Button>("SettingsToggle")!);
        host.Settle();
        Assert.Contains("draft.example.com", MobileNotificationHost.TextOf(host.Find<Border>("SettingsPanel")!));
    }

    [AvaloniaFact]
    public void Successful_command_with_unparseable_output_is_reported_as_a_failure()
    {
        var module = new FakeNotificationsModule();
        module.UnparseableOutputs.Add(FakeNotificationsModule.SyncCommand);
        using var host = MobileNotificationHost.Open(module);
        var viewModel = (MobileNotificationsViewModel)host.View.DataContext!;

        viewModel.SyncCommand.Execute(null);
        host.Settle();

        Assert.True(host.Find<Border>("ErrorCard")!.IsVisible);
        Assert.Contains("无法解析", host.AllText());
        Assert.Equal("", viewModel.SyncResult);
    }
}
