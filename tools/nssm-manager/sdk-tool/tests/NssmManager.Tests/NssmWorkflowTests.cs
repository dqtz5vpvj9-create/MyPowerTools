using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using NssmManager.Contracts;
using NssmManager.Tool;

namespace NssmManager.Tests;

public sealed class NssmWorkflowTests
{
    [Fact]
    public async Task Search_keeps_editor_and_new_service_resets_selection()
    {
        var fake = new FakeRuntime();
        var vm = fake.ViewModel;
        Assert.True(await vm.ActivateAsync("edit", "DemoSvc", CancellationToken.None));
        vm.Description = "draft";
        vm.ServiceSearchText = "missing";
        Assert.Empty(vm.FilteredServices);
        vm.SelectedService = null;
        Assert.Equal("DemoSvc", vm.SelectedService?.Name);
        Assert.Equal("draft", vm.Description);
        vm.ServiceSearchText = "DEMO";
        Assert.Single(vm.FilteredServices);
        await vm.NewCommand.ExecuteAsync();
        Assert.True(vm.IsNew);
        Assert.Null(vm.SelectedService);
        Assert.Empty(vm.Name);
        Assert.False(vm.CanStop);
    }

    [Fact]
    public async Task Installing_requires_confirmation_then_validates_and_submits_exact_configuration()
    {
        var fake = new FakeRuntime();
        var vm = fake.ViewModel;
        await vm.ActivateAsync("install", "NewSvc", CancellationToken.None);
        vm.Application = @"C:\demo.exe";
        vm.EnvironmentText = " A=one\r\nB=two ";
        vm.ExitRulesText = "3=Ignore";
        vm.HooksText = "Start/Pre=echo ready";
        await vm.SaveCommand.ExecuteAsync();
        Assert.True(vm.HasPendingConfirmation);
        Assert.DoesNotContain(fake.Calls, call => call.Id == "nssm-manager.install");
        vm.ImpactConfirmed = true;
        await vm.ConfirmPendingCommand.ExecuteAsync();
        var install = Assert.Single(fake.Calls.Where(call => call.Id == "nssm-manager.install"));
        var config = install.Arguments!["configuration"]!.Deserialize<NssmServiceConfiguration>(FakeRuntime.Json)!;
        Assert.Equal("NewSvc", config.Name);
        Assert.Equal(new[] { "A=one", "B=two" }, config.EnvironmentExtra);
        Assert.Equal(NssmExitAction.Ignore, Assert.Single(config.ExitRules).Action);
        Assert.Equal("echo ready", Assert.Single(config.Hooks).Command);
        Assert.True(fake.Calls.FindIndex(call => call.Id == "nssm-manager.validate") < fake.Calls.FindIndex(call => call.Id == "nssm-manager.install"));
        Assert.False(vm.HasPendingConfirmation);
        Assert.False(vm.Busy);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("pause")]
    [InlineData("continue")]
    [InlineData("rotate")]
    public async Task Controls_target_loaded_service_and_refresh(string action)
    {
        var fake = new FakeRuntime();
        var vm = fake.ViewModel;
        await vm.ActivateAsync("edit", "DemoSvc", CancellationToken.None);
        var command = action switch
        {
            "start" => vm.StartCommand, "stop" => vm.StopCommand, "restart" => vm.RestartCommand,
            "pause" => vm.PauseCommand, "continue" => vm.ContinueCommand, _ => vm.RotateCommand
        };
        await command.ExecuteAsync();
        var control = Assert.Single(fake.Calls.Where(call => call.Id == "nssm-manager.control"));
        Assert.Equal(action, control.Arguments!["action"]!.GetValue<string>());
        Assert.Equal("DemoSvc", control.Arguments["serviceName"]!.GetValue<string>());
        Assert.Equal("nssm-manager.get", fake.Calls.Last().Id);
        Assert.False(vm.Busy);
        Assert.False(vm.PrivilegedOperation);
    }

    [Theory]
    [InlineData("migrate")]
    [InlineData("rollback")]
    public async Task Host_changes_require_explicit_confirmation(string action)
    {
        var fake = new FakeRuntime();
        var vm = fake.ViewModel;
        await vm.ActivateAsync("edit", "DemoSvc", CancellationToken.None);
        var command = action == "migrate" ? vm.MigrateCommand : vm.RollbackCommand;
        await command.ExecuteAsync();
        Assert.True(vm.HasPendingConfirmation);
        Assert.DoesNotContain(fake.Calls, call => call.Id == "nssm-manager." + action);
        vm.ImpactConfirmed = true;
        await vm.ConfirmPendingCommand.ExecuteAsync();
        var change = Assert.Single(fake.Calls.Where(call => call.Id == "nssm-manager." + action));
        Assert.Equal("DemoSvc", change.Arguments!["serviceName"]!.GetValue<string>());
        Assert.False(vm.HasPendingConfirmation);
    }

    [Fact]
    public async Task Invalid_exit_rules_and_hooks_do_not_submit()
    {
        var fake = new FakeRuntime();
        var vm = fake.ViewModel;
        await vm.ActivateAsync("edit", "DemoSvc", CancellationToken.None);
        foreach (var malformedHook in new[] { false, true })
        {
            vm.ExitRulesText = malformedHook ? "" : "bad";
            vm.HooksText = malformedHook ? "bad" : "";
            await vm.SaveCommand.ExecuteAsync();
            Assert.Contains("无效", vm.Status);
            Assert.False(vm.Busy);
        }
        Assert.DoesNotContain(fake.Calls, call => call.Id == "nssm-manager.apply");
    }

    [Fact]
    public async Task Cancelled_control_recovers_busy_state_and_retains_service()
    {
        var fake = new FakeRuntime { FailureCommand = "nssm-manager.control", FailureCode = "permission.cancelled" };
        var vm = fake.ViewModel;
        await vm.ActivateAsync("edit", "DemoSvc", CancellationToken.None);
        await vm.StopCommand.ExecuteAsync();
        Assert.Contains("已取消", vm.Status);
        Assert.False(vm.Busy);
        Assert.False(vm.PrivilegedOperation);
        Assert.True(vm.IsExisting);
    }

    [Fact]
    public async Task Switching_editor_and_creating_service_clear_unsubmitted_password()
    {
        var fake = new FakeRuntime();
        var vm = fake.ViewModel;
        await vm.ActivateAsync("edit", "DemoSvc", CancellationToken.None);
        vm.Password = "test-only-credential";
        await vm.RefreshAsync();
        Assert.Empty(vm.Password);
        vm.Password = "test-only-credential";
        await vm.NewCommand.ExecuteAsync();
        Assert.Empty(vm.Password);
    }

    [Fact]
    public async Task Failed_configuration_load_discards_previous_editor_and_approval()
    {
        var fake = new FakeRuntime();
        var vm = fake.ViewModel;
        await vm.ActivateAsync("remove", "DemoSvc", CancellationToken.None);
        Assert.True(vm.HasPendingConfirmation);
        fake.FailureCommand = "nssm-manager.get";
        Assert.False(await vm.ActivateAsync("edit", "DemoSvc", CancellationToken.None));
        Assert.False(vm.IsExisting);
        Assert.False(vm.CanEdit);
        Assert.False(vm.HasPendingConfirmation);
        await vm.StopCommand.ExecuteAsync();
        Assert.DoesNotContain(fake.Calls, call => call.Id == "nssm-manager.control");
    }

    private sealed class FakeRuntime
    {
        internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public List<(string Id, JsonObject? Arguments)> Calls { get; } = [];
        public string? FailureCommand { get; set; }
        public string FailureCode { get; set; } = "runtime.failed";
        public NssmManagerViewModel ViewModel { get; }

        public FakeRuntime() => ViewModel = new(new MptAvaloniaSurfaceContext(
            ToolId: "nssm-manager", RouteId: "services", DataDirectory: Path.GetTempPath(), Theme: "light",
            ExecuteCommandAsync: (id, arguments, _) =>
            {
                Calls.Add((id, arguments?.DeepClone().AsObject()));
                JsonNode payload = id switch
                {
                    "nssm-manager.list" => JsonSerializer.SerializeToNode(new[] { new NssmServiceSnapshot("DemoSvc", "Demo", "", @"C:\demo.exe", @"C:\host\nssm.exe", NssmServiceState.Running, NssmStartupType.Automatic, 42, true, false) }, Json)!,
                    "nssm-manager.get" => JsonSerializer.SerializeToNode(new NssmServiceConfiguration { Name = "DemoSvc", Application = @"C:\demo.exe" }, Json)!,
                    _ => new JsonObject()
                };
                var result = new JsonObject { ["state"] = id == FailureCommand ? "failed" : "ready", ["payload"] = payload };
                if (id == FailureCommand) result["error"] = new JsonObject { ["code"] = FailureCode, ["message"] = "isolated runtime failure" };
                return Task.FromResult(new CommandExecutionResult("workflow", id, "ready", true, new JsonObject { ["result"] = result }.ToJsonString()));
            }, NavigateAsync: (_, _, _) => Task.CompletedTask, ServiceUnits: null!, Log: _ => { }));
    }
}
