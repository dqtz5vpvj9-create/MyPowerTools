using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileToolControl.Tests;

/// <summary>
/// The page must report exactly what the computer answered to a cancel: accepted (still waiting),
/// refused (call keeps running) or already finished. A successful HTTP call is not an answer.
/// </summary>
public sealed class CancelAndConfirmationTests
{
    private static (FakeToolControlModule Module, MobileToolControlViewModel Vm) Open()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;
        vm.Activate();
        SurfaceHarness.Pump();
        SurfaceHarness.Complete(vm.OpenDeviceAsync(new MobileToolDeviceItem(
            "grant-1", "工作电脑", "http://100.64.0.2:49541", "windows", "reachable", "已连接", true)));
        var row = vm.PriorityActions.Single(action => action.Command.CommandId == "paste-image.upload");
        vm.OpenCommand(row);
        vm.ParameterFields[0].Value = "a.png";
        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.RunCommand).ExecuteAsync());
        SurfaceHarness.Pump();
        Assert.Equal(1, module.CountOf(MobileToolControlContract.CommandInvoke));
        Assert.NotNull(vm.Invocation);
        return (module, vm);
    }

    [AvaloniaFact]
    public void ARefusedCancelIsShownAsRefusedAndLeavesTheCallRunning()
    {
        var (module, vm) = Open();
        module.CancelAnswer = new JsonObject
        {
            ["invocationId"] = "inv-1",
            ["commandId"] = "paste-image.upload",
            ["state"] = "running",
            ["message"] = "运行时拒绝了取消请求。",
            ["terminal"] = false,
            ["accepted"] = false,
            ["cancelAccepted"] = false,
            ["result"] = null
        };

        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.CancelInvocationCommand).ExecuteAsync());
        SurfaceHarness.Pump();

        Assert.False(vm.Invocation!.CancelAccepted);
        Assert.False(vm.Invocation!.Terminal);
        Assert.Contains("电脑没有接受取消请求", vm.InvocationCancelNote, StringComparison.Ordinal);
        Assert.Contains("运行时拒绝了取消请求", vm.InvocationCancelNote, StringComparison.Ordinal);
        Assert.NotEqual("已取消", vm.InvocationStateText);
        Assert.True(vm.CanCancelInvocation);
    }

    [AvaloniaFact]
    public void CancellingAFinishedCallSaysItAlreadyFinished()
    {
        var (module, vm) = Open();
        module.CancelAnswer = new JsonObject
        {
            ["invocationId"] = "inv-1",
            ["commandId"] = "paste-image.upload",
            ["state"] = "succeeded",
            ["message"] = "调用已经结束。",
            ["terminal"] = true,
            ["accepted"] = false,
            ["cancelAccepted"] = false,
            ["result"] = new JsonObject { ["state"] = "succeeded", ["summary"] = "已上传 1 张图片" }
        };

        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.CancelInvocationCommand).ExecuteAsync());
        SurfaceHarness.Pump();

        Assert.False(vm.Invocation!.CancelAccepted);
        Assert.True(vm.Invocation!.Terminal);
        Assert.Contains("已经结束", vm.InvocationCancelNote, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void AnAcceptedCancelIsRenderedFromTheModulePayload()
    {
        var (module, vm) = Open();
        module.CancelAnswer = new JsonObject
        {
            ["invocationId"] = "inv-1",
            ["commandId"] = "paste-image.upload",
            ["state"] = "cancelling",
            ["message"] = "已请求取消。",
            ["terminal"] = false,
            ["accepted"] = true,
            ["cancelAccepted"] = true,
            ["result"] = null
        };
        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.CancelInvocationCommand).ExecuteAsync());
        SurfaceHarness.Pump();
        Assert.True(vm.Invocation!.CancelAccepted);

        // The module is the layer that keeps the last reported answer across status documents
        // (covered by the module suite); the page renders whatever the module reports.
        module.Invocation = new JsonObject
        {
            ["invocationId"] = "inv-1",
            ["commandId"] = "paste-image.upload",
            ["state"] = "cancelling",
            ["message"] = "仍在等待最终结果",
            ["terminal"] = false,
            ["cancelAccepted"] = true,
            ["result"] = null
        };
        SurfaceHarness.Complete(vm.PollOnceAsync());

        Assert.True(vm.Invocation!.CancelAccepted);
        Assert.Equal("cancelling", vm.Invocation!.State);
        Assert.Equal("正在取消，等待最终结果", vm.InvocationStateText);
    }

    [AvaloniaFact]
    public void AnAcceptedCancelThatIsAlreadyTerminalMeansTheComputerCancelledIt()
    {
        var (module, vm) = Open();
        // The gateway cancels an unclaimed pending confirmation locally and answers accepted=true with
        // a terminal state; that is a successful cancel, not an "already finished" answer.
        module.CancelAnswer = new JsonObject
        {
            ["invocationId"] = "inv-1",
            ["commandId"] = "paste-image.upload",
            ["state"] = "cancelled",
            ["message"] = "已取消等待电脑确认的调用。",
            ["terminal"] = true,
            ["accepted"] = true,
            ["cancelAccepted"] = true,
            ["result"] = new JsonObject { ["state"] = "cancelled", ["errorCode"] = "cancelled" }
        };

        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.CancelInvocationCommand).ExecuteAsync());
        SurfaceHarness.Pump();

        Assert.True(vm.Invocation!.CancelAccepted);
        Assert.True(vm.Invocation!.Terminal);
        Assert.Equal("已取消", vm.InvocationStateText);
        Assert.Contains("电脑已取消这次调用", vm.InvocationCancelNote, StringComparison.Ordinal);
        Assert.DoesNotContain("已经结束", vm.InvocationCancelNote, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheGatewaysConfirmationStateIsShownAsWaitingForTheComputer()
    {
        var (module, vm) = Open();
        var row = vm.PriorityActions.Single(action => action.Command.CommandId == "paste-image.upload");
        vm.OpenCommand(row);
        vm.ParameterFields[0].Value = "a.png";
        SurfaceHarness.Pump();
        module.Invocation = new JsonObject
        {
            ["invocationId"] = "inv-wait",
            ["commandId"] = "paste-image.upload",
            ["state"] = "awaiting-confirmation",
            ["message"] = "此操作需要在电脑上确认。",
            ["terminal"] = false,
            ["result"] = null
        };
        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.RunCommand).ExecuteAsync());
        SurfaceHarness.Pump();

        Assert.Equal("等待电脑确认", vm.InvocationStateText);
        Assert.Equal("此操作需要在电脑上确认。", vm.InvocationMessage);
        Assert.True(vm.CanCancelInvocation);
        Assert.False(vm.Invocation!.Terminal);
    }

    [AvaloniaFact]
    public void TheLegacyPendingConfirmationSpellingStillMeansTheSameThing()
    {
        Assert.Equal(
            "等待电脑确认",
            MobileToolControlViewModel.StateLabel(MobileToolControlContract.StatePendingConfirmation));
        Assert.Equal(
            "等待电脑确认",
            MobileToolControlViewModel.StateLabel(MobileToolControlContract.StateAwaitingConfirmation));
    }
}
