using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using RemoteToolGateway.Core;

namespace MyPowerTools.MobileToolControl.Integration.Tests;

/// <summary>
/// The real combination, end to end: the desktop gateway's own listener and wire, the phone module's
/// own HTTP client and device store, and the page's own view model. Only the desktop command
/// execution (<see cref="IHostControlBridge"/>) is a test command.
/// </summary>
public sealed class RealCombinationTests
{
    private static FakeHostControlBridge Bridge()
    {
        var bridge = new FakeHostControlBridge();
        bridge.Catalog = new HostCatalog(
            [
                FakeHostControlBridge.Tool("input-monitor", "input-monitor", "输入监测", "查看使用习惯"),
                FakeHostControlBridge.Tool("paste-image", "paste-image", "图片快传", "上传剪贴板图片"),
                FakeHostControlBridge.Tool("screenease", "screenease", "屏幕舒适", "让屏幕适合此刻的光线"),
                FakeHostControlBridge.Tool("nssm-manager", "nssm-manager", "服务管理", "查看和管理 Windows 服务")
            ],
            [
                FakeHostControlBridge.Command("input-monitor.stats", "input-monitor", "查看使用统计", "统计今天的键鼠活动"),
                FakeHostControlBridge.Command(
                    "paste-image.upload",
                    "paste-image",
                    "上传图片",
                    "把手机图片送到电脑剪贴板",
                    parameters:
                    [
                        FakeHostControlBridge.Parameter("path", "图片路径", "string", true),
                        FakeHostControlBridge.Parameter("width", "宽度", "int", false, "1024"),
                        FakeHostControlBridge.Parameter("reuse", "复用远端路径", "bool", false, "true")
                    ]),
                FakeHostControlBridge.Command(
                    "nssm-manager.service.restart",
                    "nssm-manager",
                    "重启服务",
                    "需要管理员权限",
                    dangerLevel: "danger")
            ]);
        return bridge;
    }

    private static readonly string[] Granted =
    [
        "input-monitor.stats",
        "paste-image.upload",
        "nssm-manager.service.restart"
    ];

    private static (RealCombination Combination, FakeHostControlBridge Bridge) Start(FakeHostControlBridge bridge)
    {
        var setup = RealCombination.StartGateway(bridge, Granted);
        return (RealCombination.Create(setup, bridge), bridge);
    }

    [AvaloniaFact]
    public void RealCatalogReachesThePageAndATypedFormRunsTheRealWire()
    {
        var bridge = Bridge();
        bridge.Behave("paste-image.upload", new BridgeBehavior(BridgeExecution.Success, "已上传 1 张图片"));
        var (combination, _) = Start(bridge);
        using (combination)
        {
            combination.Import();
            var device = combination.Device();
            // The module's deviceId is the control grantId, and the token never lands in the file.
            Assert.Equal(combination.GrantId, device.DeviceId);
            var devicesFile = File.ReadAllText(combination.DevicesFile);
            Assert.Contains(combination.GrantId, devicesFile, StringComparison.Ordinal);
            Assert.DoesNotContain(combination.PhoneToken, devicesFile, StringComparison.Ordinal);

            IntegrationHarness.Complete(combination.Vm.OpenDeviceAsync(device));

            var actions = combination.Vm.PriorityActions.Select(row => row.Action).ToArray();
            Assert.Contains("查看使用统计", actions);
            Assert.Contains("上传图片", actions);
            Assert.Equal("尚未检查", device.StateLabel);

            var upload = combination.Vm.PriorityActions.Single(row => row.Command.CommandId == "paste-image.upload");
            combination.Vm.OpenCommand(upload);
            IntegrationHarness.Pump();
            Assert.Equal("上传图片", combination.Vm.ParameterSheetTitle);
            combination.Vm.ParameterFields[0].Value = "/sdcard/DCIM/a.png";
            combination.Vm.ParameterFields[1].Value = "800";
            combination.Vm.ParameterFields[2].BooleanValue = false;

            IntegrationHarness.Complete(((MptAsyncRelayCommand)combination.Vm.RunCommand).ExecuteAsync());
            IntegrationHarness.Pump();

            Assert.Equal("已完成", combination.Vm.InvocationStateText);
            Assert.Equal("已上传 1 张图片", combination.Vm.InvocationResultTitle);

            var execution = Assert.Single(bridge.Executions);
            Assert.Equal("paste-image.upload", execution.CommandId);
            Assert.Equal("/sdcard/DCIM/a.png", execution.Args["path"]!.GetValue<string>());
            // The declared types are respected: the int parameter is a JSON number, not a string.
            Assert.Equal(800L, execution.Args["width"]!.GetValue<long>());
            Assert.False(execution.Args["reuse"]!.GetValue<bool>());
        }
    }

    [AvaloniaFact]
    public void ARealHostFailureKeepsItsOwnCodeAndMessage()
    {
        var bridge = Bridge();
        bridge.Behave("input-monitor.stats", new BridgeBehavior(
            BridgeExecution.Failure,
            ErrorCode: "host.rejected",
            ErrorMessage: "电脑上的模块拒绝了这次读取。"));
        var (combination, _) = Start(bridge);
        using (combination)
        {
            combination.Import();
            IntegrationHarness.Complete(combination.Vm.OpenDeviceAsync(combination.Device()));

            var stats = combination.Vm.PriorityActions.Single(row => row.Command.CommandId == "input-monitor.stats");
            combination.Vm.OpenCommand(stats);
            IntegrationHarness.Pump();
            IntegrationHarness.Complete(((MptAsyncRelayCommand)combination.Vm.RunCommand).ExecuteAsync());
            IntegrationHarness.Pump();

            Assert.Equal("执行失败", combination.Vm.InvocationStateText);
            Assert.Equal("电脑上的模块拒绝了这次读取。", combination.Vm.InvocationResultTitle);
            Assert.Contains("host.rejected", combination.Vm.InvocationResultDetail, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void AwaitingConfirmationComesFromTheRealGatewayAndCancelIsAccepted()
    {
        var bridge = Bridge();
        bridge.Behave("nssm-manager.service.restart", new BridgeBehavior(
            BridgeExecution.UntilCancelled,
            Delay: TimeSpan.FromSeconds(10)));
        var (combination, _) = Start(bridge);
        using (combination)
        {
            combination.Import();
            IntegrationHarness.Complete(combination.Vm.OpenDeviceAsync(combination.Device()));

            var restart = combination.Vm.OtherGroups.SelectMany(group => group.Actions)
                .Single(row => row.Command.CommandId == "nssm-manager.service.restart");
            combination.Vm.OpenCommand(restart);
            IntegrationHarness.Pump();
            IntegrationHarness.Complete(((MptAsyncRelayCommand)combination.Vm.RunCommand).ExecuteAsync());
            IntegrationHarness.Pump();

            Assert.Equal("等待电脑确认", combination.Vm.InvocationStateText);
            Assert.True(combination.Vm.CanCancelInvocation);
            Assert.Empty(bridge.Executions);

            var live = combination.Gateway.Invocations.Find(combination.Vm.Invocation!.InvocationId);
            Assert.NotNull(live);
            Assert.Equal("awaiting-confirmation", live!.Snapshot().State);

            IntegrationHarness.Complete(((MptAsyncRelayCommand)combination.Vm.CancelInvocationCommand).ExecuteAsync());
            IntegrationHarness.Pump();

            // The gateway cancelled the pending confirmation locally and answered accepted + terminal;
            // the phone reports the computer's real answer instead of "already finished".
            Assert.True(combination.Vm.Invocation!.CancelAccepted);
            Assert.True(combination.Vm.Invocation!.Terminal);
            Assert.Equal("cancelled", combination.Vm.Invocation!.State);
            Assert.Equal("已取消", combination.Vm.InvocationStateText);
            Assert.Contains("电脑已取消这次调用", combination.Vm.InvocationCancelNote, StringComparison.Ordinal);
            Assert.Equal("cancelled", live.Snapshot().State);
            Assert.Empty(bridge.Executions);
        }
    }

    [AvaloniaFact]
    public void ACancelTheRuntimeRefusesIsNeverReportedAsCancelled()
    {
        var bridge = Bridge();
        bridge.Behave("input-monitor.stats", new BridgeBehavior(
            BridgeExecution.SlowSuccess,
            Summary: "统计完成",
            Delay: TimeSpan.FromSeconds(3),
            CancelAccepted: false,
            CancelMessage: "运行时拒绝了取消请求。"));
        var (combination, _) = Start(bridge);
        using (combination)
        {
            combination.Import();
            IntegrationHarness.Complete(combination.Vm.OpenDeviceAsync(combination.Device()));

            var stats = combination.Vm.PriorityActions.Single(row => row.Command.CommandId == "input-monitor.stats");
            combination.Vm.OpenCommand(stats);
            IntegrationHarness.Pump();
            IntegrationHarness.Complete(((MptAsyncRelayCommand)combination.Vm.RunCommand).ExecuteAsync());
            IntegrationHarness.Pump();
            Assert.False(combination.Vm.Invocation!.Terminal);

            IntegrationHarness.Complete(((MptAsyncRelayCommand)combination.Vm.CancelInvocationCommand).ExecuteAsync());
            IntegrationHarness.Pump();

            Assert.False(combination.Vm.Invocation!.CancelAccepted);
            Assert.Contains("没有接受取消请求", combination.Vm.InvocationCancelNote, StringComparison.Ordinal);
            Assert.NotEqual("已取消", combination.Vm.InvocationStateText);

            // The runtime keeps running and reports its real terminal result.
            IntegrationHarness.WaitFor(
                () =>
                {
                    IntegrationHarness.Complete(combination.Vm.PollOnceAsync());
                    return combination.Vm.Invocation?.Terminal ?? false;
                },
                "调用没有在预期时间内进入终态。");

            Assert.Equal("已完成", combination.Vm.InvocationStateText);
            Assert.Equal("统计完成", combination.Vm.InvocationResultTitle);
        }
    }

    [AvaloniaFact]
    public void AnAcceptedRuntimeCancelEndsInTheRealTerminalState()
    {
        var bridge = Bridge();
        bridge.Behave("input-monitor.stats", new BridgeBehavior(
            BridgeExecution.UntilCancelled,
            Delay: TimeSpan.FromSeconds(10),
            CancelAccepted: true));
        var (combination, _) = Start(bridge);
        using (combination)
        {
            combination.Import();
            IntegrationHarness.Complete(combination.Vm.OpenDeviceAsync(combination.Device()));

            var stats = combination.Vm.PriorityActions.Single(row => row.Command.CommandId == "input-monitor.stats");
            combination.Vm.OpenCommand(stats);
            IntegrationHarness.Pump();
            IntegrationHarness.Complete(((MptAsyncRelayCommand)combination.Vm.RunCommand).ExecuteAsync());
            IntegrationHarness.Pump();
            Assert.False(combination.Vm.Invocation!.Terminal);

            IntegrationHarness.Complete(((MptAsyncRelayCommand)combination.Vm.CancelInvocationCommand).ExecuteAsync());
            IntegrationHarness.Pump();
            Assert.True(combination.Vm.Invocation!.CancelAccepted);
            Assert.False(combination.Vm.Invocation!.Terminal);
            Assert.False(combination.Vm.CanCancelInvocation);

            // The runtime honors the cancel and reports the real terminal state; the page keeps
            // reading until that answer arrives.
            IntegrationHarness.WaitFor(
                () =>
                {
                    IntegrationHarness.Complete(combination.Vm.PollOnceAsync());
                    return combination.Vm.Invocation?.Terminal ?? false;
                },
                "取消后的真实终态没有到达。");

            Assert.Equal("已取消", combination.Vm.InvocationStateText);
            Assert.Equal("cancelled", combination.Vm.Invocation!.State);
        }
    }

    [AvaloniaFact]
    public void CancellingAnAlreadyFinishedInvocationSaysItAlreadyFinished()
    {
        var bridge = Bridge();
        bridge.Behave("input-monitor.stats", new BridgeBehavior(BridgeExecution.Success, "统计完成"));
        var (combination, _) = Start(bridge);
        using (combination)
        {
            combination.Import();
            IntegrationHarness.Complete(combination.Vm.OpenDeviceAsync(combination.Device()));

            var stats = combination.Vm.PriorityActions.Single(row => row.Command.CommandId == "input-monitor.stats");
            combination.Vm.OpenCommand(stats);
            IntegrationHarness.Pump();
            IntegrationHarness.Complete(((MptAsyncRelayCommand)combination.Vm.RunCommand).ExecuteAsync());
            IntegrationHarness.Pump();
            Assert.True(combination.Vm.Invocation!.Terminal);

            // The page does not offer a cancel for a finished call...
            Assert.False(combination.Vm.CanCancelInvocation);

            // ...and the computer's own answer for that call is a refusal, not a cancellation.
            var cancel = IntegrationHarness.Complete(combination.Module.ExecuteCommandAsync(
                new CommandRequest(
                    Guid.NewGuid().ToString("N"),
                    MobileToolControlContract.CommandInvokeCancel,
                    new JsonObject
                    {
                        [MobileToolControlContract.ArgumentDeviceId] = combination.GrantId,
                        [MobileToolControlContract.ArgumentInvocationId] = combination.Vm.Invocation!.InvocationId
                    }),
                CancellationToken.None).AsTask());
            Assert.True(cancel.Success);
            var payload = JsonNode.Parse(cancel.Output)!.AsObject();
            Assert.False(payload["cancelAccepted"]!.GetValue<bool>());
            Assert.True(payload["terminal"]!.GetValue<bool>());
            Assert.Contains("已经结束", payload["message"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void AToolOnlyActivationUsesTheCatalogModuleMappingAndNeverAutoRuns()
    {
        var bridge = new FakeHostControlBridge();
        // The tool id and the command's module id deliberately differ: the page must resolve the
        // relationship from the catalog instead of assuming ToolId == ModuleId.
        bridge.Catalog = new HostCatalog(
            [FakeHostControlBridge.Tool("input-monitor", "wellbeing-module", "输入监测", "查看使用习惯")],
            [
                FakeHostControlBridge.Command(
                    "input-monitor.stats",
                    "wellbeing-module",
                    "查看使用统计",
                    "统计今天的键鼠活动")
            ]);
        bridge.Behave("input-monitor.stats", new BridgeBehavior(BridgeExecution.Success, "统计完成"));
        var setup = RealCombination.StartGateway(bridge, ["input-monitor.stats"]);
        using var combination = RealCombination.Create(setup, bridge);

        combination.Import();

        var toolOnly = IntegrationHarness.Complete(combination.View.ActivateAsync(new ToolActivationRequest(
            MobileToolControlContract.ToolId,
            "workspace",
            $"mypowertools://device-tool?device={combination.GrantId}&tool=input-monitor")).AsTask());

        Assert.True(toolOnly);
        Assert.True(combination.Vm.IsToolSheetOpen);
        Assert.Equal("输入监测", combination.Vm.ToolSheetTitle);
        Assert.Single(combination.Vm.ToolSheetActions);

        // A deep link with run=1 must only prefill the form; the user has to press the page button.
        var withRun = IntegrationHarness.Complete(combination.View.ActivateAsync(new ToolActivationRequest(
            MobileToolControlContract.ToolId,
            "workspace",
            $"mypowertools://device-tool?device={combination.GrantId}&tool=input-monitor" +
            "&command=input-monitor.stats&run=1")).AsTask());

        Assert.True(withRun);
        Assert.True(combination.Vm.IsParameterSheetOpen);
        Assert.Equal("查看使用统计", combination.Vm.ParameterSheetTitle);
        Assert.Empty(bridge.Executions);
        Assert.False(combination.Vm.IsInvocationSheetOpen);

        // Only the user's own press executes on the real wire.
        IntegrationHarness.Complete(((MptAsyncRelayCommand)combination.Vm.RunCommand).ExecuteAsync());
        IntegrationHarness.Pump();
        Assert.Single(bridge.Executions);
        Assert.Equal("已完成", combination.Vm.InvocationStateText);
    }

    [AvaloniaFact]
    public void AControlCodeActivationOnlyOpensTheConfirmationSheet()
    {
        var bridge = Bridge();
        var (combination, _) = Start(bridge);
        using (combination)
        {
            var activated = IntegrationHarness.Complete(combination.View.ActivateAsync(new ToolActivationRequest(
                MobileToolControlContract.ToolId,
                "workspace",
                combination.Code)).AsTask());

            Assert.True(activated);
            Assert.True(combination.Vm.IsImportSheetOpen);
            Assert.True(combination.Vm.HasImportPreview);
            Assert.Equal("工作电脑", combination.Vm.ImportPreview!.DeviceName);
            // Nothing was stored and no computer call happened before the user confirms.
            Assert.Empty(combination.Vm.Devices);
            Assert.False(File.Exists(combination.DevicesFile));
            Assert.Empty(bridge.Executions);
        }
    }
}
