using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileToolControl.Tests;

/// <summary>
/// The parameter form is built from the real command parameters, and the invocation lifecycle reports
/// exactly what the computer returned: progress, result, cancel, and the waiting-for-confirmation state.
/// </summary>
public sealed class ParameterAndInvocationTests
{
    private static (MobileToolControlView View, FakeToolControlModule Module, MobileToolControlViewModel Vm) OpenUpload()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        // The page only refreshes a running invocation while it is visible; activating is what the
        // Shell does when the tool page is shown.
        vm.Activate();
        SurfaceHarness.Pump();
        SurfaceHarness.Complete(vm.OpenDeviceAsync(new MobileToolDeviceItem(
            "grant-1", "工作电脑", "http://100.64.0.2:49541", "windows", "reachable", "已连接 工作电脑", true)));
        var row = vm.PriorityActions.Single(action => action.Command.CommandId == "paste-image.upload");
        vm.OpenCommand(row);
        SurfaceHarness.Pump();
        return (view, module, vm);
    }

    [AvaloniaFact]
    public void TheFormUsesTheRealParametersAndDefaults()
    {
        var (_, _, vm) = OpenUpload();

        Assert.True(vm.IsParameterSheetOpen);
        Assert.Equal("上传图片", vm.ParameterSheetTitle);
        Assert.Equal(
            ["path", "width", "reuse"],
            vm.ParameterFields.Select(field => field.Id).ToArray());
        Assert.Equal("图片路径（必填）", vm.ParameterFields[0].Label);
        Assert.Equal("1024", vm.ParameterFields[1].Value);
        Assert.True(vm.ParameterFields[2].BooleanValue);
    }

    [AvaloniaFact]
    public void RunningSendsTheEditedValuesWithANewInvocationId()
    {
        var (_, module, vm) = OpenUpload();
        vm.ParameterFields[0].Value = "/sdcard/DCIM/a.png";
        vm.ParameterFields[1].Value = "800";
        SurfaceHarness.Pump();

        Run(vm);

        var args = module.LastArgs(MobileToolControlContract.CommandInvoke);
        Assert.NotNull(args);
        Assert.Equal("grant-1", args![MobileToolControlContract.ArgumentDeviceId]!.GetValue<string>());
        Assert.Equal("paste-image.upload", args[MobileToolControlContract.ArgumentCommandId]!.GetValue<string>());
        var invocationId = args[MobileToolControlContract.ArgumentInvocationId]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));
        var payload = args[MobileToolControlContract.ArgumentArgs]!.AsObject();
        Assert.Equal("/sdcard/DCIM/a.png", payload["path"]!.GetValue<string>());
        // The width parameter was declared as int, so it travels as a JSON number.
        Assert.Equal(800L, payload["width"]!.GetValue<long>());
        Assert.True(payload["reuse"]!.GetValue<bool>());

        Assert.True(vm.IsInvocationSheetOpen);
        Assert.Equal("电脑执行中", vm.InvocationStateText);
        Assert.True(vm.CanCancelInvocation);
    }

    [AvaloniaFact]
    public void AMissingRequiredParameterNeverReachesTheComputer()
    {
        var (_, module, vm) = OpenUpload();
        vm.ParameterFields[0].Value = "";
        SurfaceHarness.Pump();

        Run(vm);

        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandInvoke));
        Assert.True(vm.IsParameterSheetOpen);
        Assert.True(vm.ParameterFields[0].HasError);
        Assert.Contains("必填", vm.ParameterFields[0].Error, StringComparison.Ordinal);
        Assert.Contains("图片路径", vm.FeedbackText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheWaitingForConfirmationStateIsShownAndCancellationStops()
    {
        var (_, module, vm) = OpenUpload();
        module.Invocation = new JsonObject
        {
            ["invocationId"] = "inv-9",
            ["commandId"] = "paste-image.upload",
            ["state"] = "awaiting-confirmation",
            ["message"] = "等待电脑用户确认",
            ["terminal"] = false,
            ["result"] = null
        };

        vm.ParameterFields[0].Value = "a.png";
        SurfaceHarness.Pump();
        Run(vm);

        Assert.Equal("等待电脑确认", vm.InvocationStateText);
        Assert.Equal("等待电脑用户确认", vm.InvocationMessage);
        Assert.True(vm.CanCancelInvocation);
    }

    [AvaloniaFact]
    public void AFinishedInvocationShowsTheComputersResultAndStopsRefreshing()
    {
        var (_, module, vm) = OpenUpload();
        module.Invocation = new JsonObject
        {
            ["invocationId"] = "inv-10",
            ["commandId"] = "paste-image.upload",
            ["state"] = "succeeded",
            ["message"] = "已上传",
            ["terminal"] = true,
            ["result"] = new JsonObject
            {
                ["invocationId"] = "inv-10",
                ["state"] = "succeeded",
                ["summary"] = "已上传 1 张图片",
                ["errorCode"] = "",
                ["retryable"] = false
            }
        };

        vm.ParameterFields[0].Value = "a.png";
        SurfaceHarness.Pump();
        Run(vm);

        Assert.Equal("已完成", vm.InvocationStateText);
        Assert.Equal("已上传 1 张图片", vm.InvocationResultTitle);
        Assert.False(vm.CanCancelInvocation);
    }

    [AvaloniaFact]
    public void AnUnknownWireStateIsShownVerbatim()
    {
        var (_, module, vm) = OpenUpload();
        module.Invocation = new JsonObject
        {
            ["invocationId"] = "inv-11",
            ["commandId"] = "paste-image.upload",
            ["state"] = "banana",
            ["message"] = "",
            ["terminal"] = false,
            ["result"] = null
        };

        vm.ParameterFields[0].Value = "a.png";
        SurfaceHarness.Pump();
        Run(vm);

        Assert.Equal("banana", vm.InvocationStateText);
    }

    [AvaloniaFact]
    public void CancelUsesTheModuleCancelCommandAndShowsTheAcceptedAnswer()
    {
        var (_, module, vm) = OpenUpload();
        vm.ParameterFields[0].Value = "a.png";
        SurfaceHarness.Pump();
        Run(vm);
        Assert.True(vm.CanCancelInvocation);

        Cancel(vm);

        var args = module.LastArgs(MobileToolControlContract.CommandInvokeCancel);
        Assert.NotNull(args);
        Assert.Equal("grant-1", args![MobileToolControlContract.ArgumentDeviceId]!.GetValue<string>());
        Assert.Equal("inv-1", args[MobileToolControlContract.ArgumentInvocationId]!.GetValue<string>());
        // The computer accepted the cancel; the real outcome is still pending, so the page keeps the
        // call visible and stops offering a second cancel.
        Assert.True(vm.Invocation!.CancelAccepted);
        Assert.False(vm.Invocation!.Terminal);
        Assert.Equal("正在取消，等待最终结果", vm.InvocationStateText);
        Assert.False(vm.CanCancelInvocation);
        Assert.Contains("电脑已接受取消请求", vm.InvocationCancelNote, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ARunningInvocationIsReadBackOnlyThroughTheStatusCommand()
    {
        var (_, module, vm) = OpenUpload();
        module.Invocation = new JsonObject
        {
            ["invocationId"] = "inv-12",
            ["commandId"] = "paste-image.upload",
            ["state"] = "running",
            ["message"] = "",
            ["terminal"] = false,
            ["result"] = null
        };
        module.PollResponses.Enqueue(new JsonObject
        {
            ["invocationId"] = "inv-12",
            ["commandId"] = "paste-image.upload",
            ["state"] = "succeeded",
            ["message"] = "完成",
            ["terminal"] = true,
            ["result"] = new JsonObject { ["summary"] = "完成" }
        });

        vm.ParameterFields[0].Value = "a.png";
        SurfaceHarness.Pump();
        Run(vm);
        SurfaceHarness.Complete(vm.PollOnceAsync());

        Assert.Equal(1, module.CountOf(MobileToolControlContract.CommandInvocationStatus));
        Assert.Equal("已完成", vm.InvocationStateText);
        Assert.False(vm.CanCancelInvocation);
    }

    [AvaloniaFact]
    public void ATechnicalDetailViewShowsTheRawWirePayload()
    {
        var (_, _, vm) = OpenUpload();
        vm.ParameterFields[0].Value = "a.png";
        SurfaceHarness.Pump();
        Run(vm);

        vm.OpenDetails(new JsonObject { ["state"] = "running" }.ToJsonString());
        SurfaceHarness.Pump();

        Assert.True(vm.IsDetailsOpen);
        Assert.Contains("running", vm.DetailsText, StringComparison.Ordinal);
    }

    private static void Run(MobileToolControlViewModel vm)
    {
        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.RunCommand).ExecuteAsync());
        SurfaceHarness.Pump();
    }

    private static void Cancel(MobileToolControlViewModel vm)
    {
        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.CancelInvocationCommand).ExecuteAsync());
        SurfaceHarness.Pump();
    }
}
