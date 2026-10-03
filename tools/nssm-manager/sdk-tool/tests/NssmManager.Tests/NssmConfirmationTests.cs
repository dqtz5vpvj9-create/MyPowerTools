using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using NssmManager.Contracts;
using NssmManager.Tool;

namespace NssmManager.Tests;

/// <summary>
/// The high-risk operations must never reach the runtime on the first click: the first
/// click only renders the impact scope, and only an explicit confirmation enables the
/// submit path. These tests assert the observable command/gate behaviour, not just copy.
/// </summary>
public sealed class NssmConfirmationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void high_risk_configuration_changes_need_confirmation()
    {
        var before = new NssmServiceConfiguration
        {
            Name = "DemoSvc",
            Application = @"C:\demo.exe",
            ServiceAccount = "LocalSystem",
            StartupType = NssmStartupType.Automatic
        };

        Assert.False(NssmManagerViewModel.RequiresRiskConfirmation(before, before with { Description = "cosmetic" }, false));
        Assert.False(NssmManagerViewModel.RequiresRiskConfirmation(before, before with { RotateFiles = true, ThrottleDelayMilliseconds = 2000 }, false));
        Assert.False(NssmManagerViewModel.RequiresRiskConfirmation(before, before, false));

        Assert.True(NssmManagerViewModel.RequiresRiskConfirmation(before, before with { ServiceAccount = @"NT AUTHORITY\NetworkService" }, false));
        Assert.True(NssmManagerViewModel.RequiresRiskConfirmation(before, before with { Application = @"C:\other.exe" }, false));
        Assert.True(NssmManagerViewModel.RequiresRiskConfirmation(before, before with { AppDirectory = @"C:\other" }, false));
        Assert.True(NssmManagerViewModel.RequiresRiskConfirmation(before, before with { AppParameters = "--flag" }, false));
        Assert.True(NssmManagerViewModel.RequiresRiskConfirmation(before, before with { StartupType = NssmStartupType.Disabled }, false));
        Assert.True(NssmManagerViewModel.RequiresRiskConfirmation(before, before with { Interactive = true }, false));
        Assert.True(NssmManagerViewModel.RequiresRiskConfirmation(before, before, true));

        // A brand new service writes the ImagePath and the start type, so installing is high risk too.
        Assert.True(NssmManagerViewModel.RequiresRiskConfirmation(null, before, false));
    }

    [Fact]
    public async Task delete_stays_a_preview_until_the_impact_is_confirmed()
    {
        var calls = new List<string>();
        var viewModel = CreateViewModel(calls);

        Assert.True(await viewModel.ActivateAsync("remove", "DemoSvc", CancellationToken.None));
        Assert.True(viewModel.HasPendingConfirmation);
        Assert.False(viewModel.ImpactConfirmed);
        Assert.False(viewModel.CanConfirmPending);
        Assert.Contains("DemoSvc", viewModel.Impact, StringComparison.Ordinal);
        Assert.Contains("可回滚：否", viewModel.Impact, StringComparison.Ordinal);

        // First click only previews; the runtime must not see the removal.
        await viewModel.DeleteCommand.ExecuteAsync();
        Assert.DoesNotContain("nssm-manager.remove", calls);
        Assert.True(viewModel.HasPendingConfirmation);
        Assert.False(viewModel.CanConfirmPending);

        // The confirmation is a real precondition, not decoration.
        viewModel.ImpactConfirmed = true;
        Assert.True(viewModel.CanConfirmPending);
        Assert.True(viewModel.ConfirmPendingCommand.CanExecute(null));

        await viewModel.ConfirmPendingCommand.ExecuteAsync();
        Assert.Equal(1, calls.Count(id => id == "nssm-manager.remove"));
        Assert.False(viewModel.HasPendingConfirmation);
    }

    [Fact]
    public async Task cosmetic_apply_submits_directly_while_high_risk_apply_needs_confirmation()
    {
        var calls = new List<string>();
        var viewModel = CreateViewModel(calls);
        Assert.True(await viewModel.ActivateAsync("edit", "DemoSvc", CancellationToken.None));

        // Cosmetic change: one click applies, no confirmation involved.
        viewModel.Description = "更新后的描述";
        await viewModel.SaveCommand.ExecuteAsync();
        Assert.Equal(1, calls.Count(id => id == "nssm-manager.apply"));
        Assert.False(viewModel.HasPendingConfirmation);

        // Account change: high risk, first click only previews.
        viewModel.Account = @"NT AUTHORITY\NetworkService";
        await viewModel.SaveCommand.ExecuteAsync();
        Assert.Equal(1, calls.Count(id => id == "nssm-manager.apply"));
        Assert.True(viewModel.HasPendingConfirmation);
        Assert.False(viewModel.CanConfirmPending);

        // Editing an ImagePath field after the preview invalidates the confirmation.
        viewModel.Application = @"C:\moved.exe";
        viewModel.ImpactConfirmed = true;
        await viewModel.ConfirmPendingCommand.ExecuteAsync();
        Assert.Equal(1, calls.Count(id => id == "nssm-manager.apply"));
        Assert.False(viewModel.ImpactConfirmed);
        Assert.True(viewModel.HasPendingConfirmation);

        // Confirming the refreshed preview submits exactly once.
        viewModel.ImpactConfirmed = true;
        await viewModel.ConfirmPendingCommand.ExecuteAsync();
        Assert.Equal(2, calls.Count(id => id == "nssm-manager.apply"));
        Assert.False(viewModel.HasPendingConfirmation);
    }

    [Fact]
    public async Task refresh_preview_arms_only_high_risk_changes()
    {
        var calls = new List<string>();
        var viewModel = CreateViewModel(calls);
        Assert.True(await viewModel.ActivateAsync("edit", "DemoSvc", CancellationToken.None));

        // Cosmetic preview: shows the diff but arms nothing.
        viewModel.Description = "仅文案变更";
        await viewModel.PreviewCommand.ExecuteAsync();
        Assert.False(viewModel.HasPendingConfirmation);
        Assert.Contains("description", viewModel.Impact, StringComparison.Ordinal);

        viewModel.Account = @"NT AUTHORITY\NetworkService";
        await viewModel.PreviewCommand.ExecuteAsync();
        Assert.True(viewModel.HasPendingConfirmation);

        // Refreshing the preview clears an earlier confirmation, so the submit path is
        // blocked again until the refreshed impact is confirmed.
        viewModel.ImpactConfirmed = true;
        await viewModel.PreviewCommand.ExecuteAsync();
        Assert.True(viewModel.HasPendingConfirmation);
        Assert.False(viewModel.ImpactConfirmed);
        Assert.False(viewModel.CanConfirmPending);
    }

    [Fact]
    public async Task read_only_refresh_never_arms_a_confirmation()
    {
        var calls = new List<string>();
        var viewModel = CreateViewModel(calls);

        await viewModel.RefreshAsync();
        Assert.False(viewModel.HasPendingConfirmation);
        Assert.False(viewModel.CanConfirmPending);
        Assert.DoesNotContain("nssm-manager.remove", calls);
        Assert.DoesNotContain("nssm-manager.apply", calls);
    }

    private static NssmManagerViewModel CreateViewModel(List<string> calls) => new(new MptAvaloniaSurfaceContext(
        ToolId: "nssm-manager",
        RouteId: "services",
        DataDirectory: Path.GetTempPath(),
        Theme: "light",
        ExecuteCommandAsync: (commandId, _, _) =>
        {
            calls.Add(commandId);
            return Task.FromResult(new CommandExecutionResult("test", commandId, "ready", true, Payload(commandId)));
        },
        NavigateAsync: (_, _, _) => Task.CompletedTask,
        ServiceUnits: null!,
        Log: _ => { }));

    private static string Payload(string commandId) => commandId switch
    {
        "nssm-manager.list" => Envelope(JsonSerializer.SerializeToNode(new[] { Snapshot() }, Json)!),
        "nssm-manager.get" => Envelope(JsonSerializer.SerializeToNode(Configuration(), Json)!),
        _ => Envelope(new JsonObject())
    };

    private static string Envelope(JsonNode payload) => new JsonObject
    {
        ["result"] = new JsonObject { ["state"] = "ready", ["payload"] = payload }
    }.ToJsonString();

    private static NssmServiceSnapshot Snapshot() => new(
        "DemoSvc",
        "Demo 服务",
        "演示服务",
        @"C:\demo.exe",
        @"C:\host\nssm-manager.exe",
        NssmServiceState.Running,
        NssmStartupType.Automatic,
        42,
        true,
        false);

    private static NssmServiceConfiguration Configuration() => new()
    {
        Name = "DemoSvc",
        DisplayName = "Demo 服务",
        Description = "演示服务",
        Application = @"C:\demo.exe",
        ServiceAccount = "LocalSystem",
        StartupType = NssmStartupType.Automatic
    };
}
