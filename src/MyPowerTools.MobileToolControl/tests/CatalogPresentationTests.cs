using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using MyPowerTools.Abstractions;

namespace MyPowerTools.MobileToolControl.Tests;

/// <summary>
/// What the page shows for a real catalog: understandable wording for the three priority tools, the
/// computer's own text for everything else, and no invented rows when the catalog is empty or fails.
/// </summary>
public sealed class CatalogPresentationTests
{
    [AvaloniaFact]
    public void PriorityActionsUseUnderstandableWording()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        SurfaceHarness.Complete(vm.OpenDeviceAsync(Device("grant-1")));

        var actions = vm.PriorityActions.Select(row => row.Action).ToArray();
        Assert.Contains("查看使用统计", actions);
        Assert.Contains("现在休息一下", actions);
        Assert.Contains("查看当前显示方案", actions);
        Assert.Contains("最近的图片", actions);

        // The wording is applied to real command ids from the shipped tool manifests.
        Assert.Equal(
            ["input-monitor.stats", "input-monitor.rest", "screenease.effect.status", "screenease.effect.toggle", "paste-image.history", "paste-image.upload"],
            vm.PriorityActions.Select(row => row.Command.CommandId).ToArray());
    }

    [AvaloniaFact]
    public void UnknownCommandsKeepTheComputersOwnText()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        SurfaceHarness.Complete(vm.OpenDeviceAsync(Device("grant-1")));

        var other = vm.OtherGroups.SelectMany(group => group.Actions).ToArray();
        var unknown = Assert.Single(other, row => row.Command.CommandId == "lorem.tool.magic");
        Assert.Equal("未知工具动作", unknown.Action);
        Assert.Equal("电脑自己声明的说明", unknown.Hint);
    }

    [AvaloniaFact]
    public void AnUnauthorizedCommandIsShownWithItsReasonAndCannotOpen()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        SurfaceHarness.Complete(vm.OpenDeviceAsync(Device("grant-1")));
        var row = vm.OtherGroups.SelectMany(group => group.Actions)
            .Single(action => action.Command.CommandId == "nssm-manager.service.restart");
        Assert.False(row.Command.Allowed);
        Assert.Equal("未授权", row.Badge);

        vm.OpenCommand(row);
        SurfaceHarness.Pump();

        Assert.False(vm.IsParameterSheetOpen);
        Assert.Contains("管理员权限", vm.FeedbackText, StringComparison.Ordinal);
        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandInvoke));
    }

    [AvaloniaFact]
    public void ToolRowsComeFromTheComputerCatalogOnly()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        SurfaceHarness.Complete(vm.OpenDeviceAsync(Device("grant-1")));

        Assert.Equal(5, vm.Tools.Count);
        var titles = vm.Tools.Select(tool => MobileToolControlSemantics.ToolTitle(tool.ToolId, tool.Title)).ToArray();
        Assert.Contains("输入监测", titles);
        Assert.Contains("屏幕舒适", titles);
        Assert.Contains("图片快传", titles);
        Assert.Contains("服务管理", titles);
    }

    [AvaloniaFact]
    public void AToolDetailSheetShowsTheRealToolFactsAndItsCommands()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        SurfaceHarness.Complete(vm.OpenDeviceAsync(Device("grant-1")));
        vm.OpenTool("screenease");
        SurfaceHarness.Pump();

        Assert.True(vm.IsToolSheetOpen);
        Assert.Equal("屏幕舒适", vm.ToolSheetTitle);
        Assert.Contains("让屏幕适合此刻的光线", vm.ToolSheetDetail, StringComparison.Ordinal);
        Assert.Contains("已授权 2/2 条命令", vm.ToolSheetDetail, StringComparison.Ordinal);
        Assert.Equal(
            ["screenease.effect.status", "screenease.effect.toggle"],
            vm.ToolSheetActions.Select(row => row.Command.CommandId).ToArray());

        // Tapping a read action opens its form and closes the tool sheet.
        vm.OpenCommand(vm.ToolSheetActions[0]);
        SurfaceHarness.Pump();
        Assert.False(vm.IsToolSheetOpen);
        Assert.True(vm.IsParameterSheetOpen);
        Assert.Equal("查看当前显示方案", vm.ParameterSheetTitle);
    }

    [AvaloniaFact]
    public void AToolDetailSheetReportsAnUnauthorizedCommandWithItsReason()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        SurfaceHarness.Complete(vm.OpenDeviceAsync(Device("grant-1")));
        vm.OpenTool("nssm-manager");
        SurfaceHarness.Pump();

        Assert.True(vm.IsToolSheetOpen);
        var row = Assert.Single(vm.ToolSheetActions);
        Assert.False(row.Command.Allowed);
        Assert.Contains("已授权 0/1 条命令", vm.ToolSheetDetail, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void AnEmptyCatalogShowsAnHonestEmptyState()
    {
        var module = new FakeToolControlModule
        {
            Catalog = new JsonObject
            {
                ["device"] = new JsonObject { ["name"] = "工作电脑", ["platform"] = "windows" },
                ["tools"] = new JsonArray(),
                ["commands"] = new JsonArray()
            }
        };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        SurfaceHarness.Complete(vm.OpenDeviceAsync(Device("grant-1")));

        Assert.True(vm.ShowCatalogEmpty);
        Assert.Empty(vm.PriorityActions);
        Assert.Empty(vm.OtherGroups);
        Assert.Empty(vm.Tools);
    }

    [AvaloniaFact]
    public void ACatalogFailureShowsTheModuleMessageAndNoRows()
    {
        var module = new FakeToolControlModule();
        module.Failures[MobileToolControlContract.CommandCatalog] = new MptRuntimeError(
            MobileToolControlContract.ErrorUnreachable,
            "无法连接电脑：Connection refused",
            Retryable: true);
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        SurfaceHarness.Complete(vm.OpenDeviceAsync(Device("grant-1")));

        Assert.Equal("无法连接电脑：Connection refused", vm.CatalogError);
        Assert.Empty(vm.PriorityActions);
        Assert.Empty(vm.OtherGroups);
        Assert.Null(vm.Catalog);
    }

    [AvaloniaFact]
    public void RefreshSendsAnExplicitRefreshRequest()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        SurfaceHarness.Complete(vm.OpenDeviceAsync(Device("grant-1")));
        SurfaceHarness.Complete(vm.LoadCatalogAsync(refresh: true));

        var calls = module.Calls.Where(call => call.CommandId == MobileToolControlContract.CommandCatalog).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.False(calls[0].Args[MobileToolControlContract.ArgumentRefresh]!.GetValue<bool>());
        Assert.True(calls[1].Args[MobileToolControlContract.ArgumentRefresh]!.GetValue<bool>());
    }

    [AvaloniaFact]
    public void TheDeviceStateIsNeverOnlineBeforeARealCheck()
    {
        var module = new FakeToolControlModule();
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        SurfaceHarness.Complete(vm.RefreshDevicesAsync(announce: false));
        var device = Assert.Single(vm.Devices);
        Assert.Equal("尚未检查", device.StateLabel);
        Assert.False(device.IsReachable);

        SurfaceHarness.Complete(vm.OpenDeviceAsync(device));
        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandDevicesCheck));

        SurfaceHarness.Complete(vm.CheckDeviceAsync());
        Assert.Equal(1, module.CountOf(MobileToolControlContract.CommandDevicesCheck));
    }

    private static MobileToolDeviceItem Device(string deviceId) => new(
        deviceId,
        "工作电脑",
        "http://100.64.0.2:49541",
        "windows",
        "imported",
        "尚未检查",
        true);
}
