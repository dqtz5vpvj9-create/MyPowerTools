using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileToolControl.Tests;

/// <summary>
/// Status reads only run while the page is visible, never stack, never apply a late answer to a newer
/// invocation, and resume when the page comes back.
/// </summary>
public sealed class PollingTests
{
    private static (FakeToolControlModule Module, MobileToolControlViewModel Vm) Open(bool activate = true)
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;
        if (activate)
        {
            vm.Activate();
            SurfaceHarness.Pump();
        }

        SurfaceHarness.Complete(vm.OpenDeviceAsync(new MobileToolDeviceItem(
            "grant-1", "工作电脑", "http://100.64.0.2:49541", "windows", "reachable", "已连接", true)));
        return (module, vm);
    }

    private static void Invoke(MobileToolControlViewModel vm, string commandId = "paste-image.upload", string value = "a.png")
    {
        var row = vm.PriorityActions.Single(action => action.Command.CommandId == commandId);
        vm.OpenCommand(row);
        if (vm.ParameterFields.Count > 0)
        {
            vm.ParameterFields[0].Value = value;
        }

        SurfaceHarness.Pump();
        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.RunCommand).ExecuteAsync());
        SurfaceHarness.Pump();
    }

    [AvaloniaFact]
    public void ASlowStatusReadIsNotStackedByTheTimer()
    {
        var (module, vm) = Open();
        Invoke(vm, "input-monitor.stats", "x");
        module.StatusGate = new TaskCompletionSource();

        var first = vm.PollOnceAsync();
        Thread.Sleep(60);
        SurfaceHarness.Pump();
        var second = vm.PollOnceAsync();
        Thread.Sleep(60);
        SurfaceHarness.Pump();

        // Only one status request is in flight, however often the timer fires.
        Assert.Equal(1, module.CountOf(MobileToolControlContract.CommandInvocationStatus));

        module.StatusGate.SetResult();
        SurfaceHarness.Complete(first);
        SurfaceHarness.Complete(second);
        module.StatusGate = null;
    }

    [AvaloniaFact]
    public void ALateStatusAnswerNeverOverwritesANewerInvocation()
    {
        var (module, vm) = Open();
        Invoke(vm, "input-monitor.stats", "x");
        Assert.Equal("inv-1", vm.Invocation!.InvocationId);

        module.StatusGate = new TaskCompletionSource();
        module.PollResponses.Enqueue(new JsonObject
        {
            ["invocationId"] = "inv-1",
            ["commandId"] = "input-monitor.stats",
            ["state"] = "succeeded",
            ["message"] = "旧调用完成",
            ["terminal"] = true,
            ["result"] = new JsonObject { ["summary"] = "旧调用完成" }
        });
        var stale = vm.PollOnceAsync();
        Thread.Sleep(60);
        SurfaceHarness.Pump();

        // The user starts another call while the old status read is still in flight.
        module.Invocation = new JsonObject
        {
            ["invocationId"] = "inv-2",
            ["commandId"] = "paste-image.upload",
            ["state"] = "running",
            ["message"] = "新调用已提交",
            ["terminal"] = false,
            ["result"] = null
        };
        Invoke(vm, "paste-image.upload", "b.png");
        Assert.Equal("inv-2", vm.Invocation!.InvocationId);

        // The late answer for inv-1 must not overwrite the newer invocation.
        module.StatusGate.SetResult();
        SurfaceHarness.Complete(stale);
        module.StatusGate = null;
        SurfaceHarness.Pump();

        Assert.Equal("inv-2", vm.Invocation!.InvocationId);
        Assert.False(vm.Invocation!.Terminal);
        Assert.NotEqual("旧调用完成", vm.InvocationResultTitle);
    }

    [AvaloniaFact]
    public void LeavingThePageStopsPollingAndComingBackResumesIt()
    {
        var (module, vm) = Open();
        Invoke(vm, "input-monitor.stats", "x");
        Assert.False(vm.Invocation!.Terminal);
        var afterInvoke = module.CountOf(MobileToolControlContract.CommandInvocationStatus);

        vm.Deactivate();
        SurfaceHarness.Pump();
        SurfaceHarness.Complete(vm.PollOnceAsync());
        Assert.Equal(afterInvoke, module.CountOf(MobileToolControlContract.CommandInvocationStatus));

        // While away, the computer finishes; coming back reads the real terminal state.
        module.Invocation = new JsonObject
        {
            ["invocationId"] = "inv-1",
            ["commandId"] = "input-monitor.stats",
            ["state"] = "succeeded",
            ["message"] = "完成",
            ["terminal"] = true,
            ["result"] = new JsonObject { ["summary"] = "统计完成" }
        };
        vm.Activate();
        SurfaceHarness.Pump();
        SurfaceHarness.Complete(vm.PollOnceAsync());

        Assert.True(module.CountOf(MobileToolControlContract.CommandInvocationStatus) > afterInvoke);
        Assert.Equal("已完成", vm.InvocationStateText);
        Assert.Equal("统计完成", vm.InvocationResultTitle);
    }
}
