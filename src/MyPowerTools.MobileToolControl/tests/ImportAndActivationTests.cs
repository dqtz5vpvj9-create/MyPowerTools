using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileToolControl.Tests;

/// <summary>
/// Importing a connection code and the activation/back behaviour: the user confirms before anything is
/// saved, a refused code stays on screen with the module's reason, an external link never executes
/// anything, and an activation can only preselect what the computer's catalog really offers.
/// </summary>
public sealed class ImportAndActivationTests
{
    private const string SampleCode = "mpt://control/eyJ2ZXJzaW9uIjoxLCJlbmRwb2ludCI6Imh0dHA6Ly8xMDAuNjQuMC4yOjQ5NTQxIn0";

    [AvaloniaFact]
    public void PreviewThenConfirmSavesThroughTheModule()
    {
        var module = new FakeToolControlModule();
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        vm.OpenImportSheet();
        vm.ImportCode = SampleCode;
        SurfaceHarness.Pump();
        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.ImportCodeCommand).ExecuteAsync());
        SurfaceHarness.Pump();

        Assert.True(vm.HasImportPreview);
        Assert.Equal("是这台电脑吗？工作电脑", vm.ImportPreviewTitle);
        Assert.Contains("100.64.0.2:49541", vm.ImportPreviewDetail, StringComparison.Ordinal);
        Assert.Contains("凭据只会保存到本机系统凭据库", vm.ImportPreviewWarning, StringComparison.Ordinal);
        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandImportConfirm));

        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.ConfirmImportCommand).ExecuteAsync());
        SurfaceHarness.Pump();

        var args = module.LastArgs(MobileToolControlContract.CommandImportConfirm);
        Assert.NotNull(args);
        Assert.Equal(SampleCode, args![MobileToolControlContract.ArgumentCode]!.GetValue<string>());
        Assert.True(args[MobileToolControlContract.ArgumentAccepted]!.GetValue<bool>());
        Assert.False(vm.IsImportSheetOpen);
        Assert.Single(vm.Devices);
    }

    [AvaloniaFact]
    public void ARefusedCodeShowsTheReasonAndCannotBeConfirmed()
    {
        var module = new FakeToolControlModule
        {
            ImportPreview = new JsonObject
            {
                ["ok"] = false,
                ["error"] = "只允许 100.64.0.0/10 范围内的 Tailnet 地址，192.168.1.20 不在其中。"
            }
        };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        vm.OpenImportSheet();
        vm.ImportCode = "mpt://control/not-tailnet";
        SurfaceHarness.Pump();
        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.ImportCodeCommand).ExecuteAsync());
        SurfaceHarness.Pump();

        Assert.False(vm.HasImportPreview);
        Assert.True(vm.HasImportError);
        Assert.Contains("Tailnet", vm.ImportError, StringComparison.Ordinal);
        Assert.False(vm.CanConfirmImport);
        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandImportConfirm));
    }

    [AvaloniaFact]
    public void AnAuthorizationFailureOnTheCatalogIsExplained()
    {
        var module = new FakeToolControlModule();
        module.Failures[MobileToolControlContract.CommandCatalog] = new MptRuntimeError(
            MobileToolControlContract.ErrorUnauthorized,
            "授权已被电脑拒绝或撤销，请重新导入连接码。");
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        SurfaceHarness.Complete(vm.OpenDeviceAsync(new MobileToolDeviceItem(
            "grant-1", "工作电脑", "http://100.64.0.2:49541", "windows", "reachable", "已连接", true)));

        Assert.Contains("重新导入", vm.CatalogError, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void RemovingADeviceDropsItFromTheList()
    {
        var module = new FakeToolControlModule();
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;
        SurfaceHarness.Complete(vm.RefreshDevicesAsync(announce: false));
        SurfaceHarness.Complete(vm.OpenDeviceAsync(vm.Devices[0]));

        SurfaceHarness.Complete(vm.RemoveDeviceAsync());

        Assert.Equal(1, module.CountOf(MobileToolControlContract.CommandDevicesRemove));
        Assert.Empty(vm.Devices);
        Assert.False(vm.IsDevicePageOpen);
    }

    [Theory]
    [InlineData("mypowertools://device-tool?device=grant-1", "grant-1", "", "", false)]
    [InlineData("mypowertools://device-tool?device=grant-1&tool=input-monitor", "grant-1", "input-monitor", "", false)]
    [InlineData(
        "mypowertools://device-tool?device=grant-1&tool=input-monitor&command=input-monitor.rest&run=1",
        "grant-1",
        "input-monitor",
        "input-monitor.rest",
        true)]
    [InlineData("https://device-tool?device=grant-1", "", "", "", false)]
    [InlineData("mypowertools://device-tool", "", "", "", false)]
    public void ActivationUrisAreParsedStrictly(
        string uri,
        string device,
        string tool,
        string command,
        bool run)
    {
        var parsed = MobileToolControlView.ParseActivationUri(uri);
        if (device.Length == 0)
        {
            Assert.Null(parsed);
            return;
        }

        Assert.NotNull(parsed);
        Assert.Equal(device, parsed!.Value.DeviceId);
        Assert.Equal(tool, parsed.Value.ToolId);
        Assert.Equal(command, parsed.Value.CommandId);
        // `run` is still parsed for compatibility, and deliberately ignored when the link is applied.
        Assert.Equal(run, parsed.Value.Run);
    }

    [AvaloniaFact]
    public void ActivationOpensTheCommandFromTheRealCatalog()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        SurfaceHarness.Complete(view.ViewModel.RefreshDevicesAsync(announce: false));

        var activated = SurfaceHarness.Complete(view.ActivateAsync(new ToolActivationRequest(
            MobileToolControlContract.ToolId,
            "workspace",
            "mypowertools://device-tool?device=grant-1&tool=paste-image&command=paste-image.upload")).AsTask());

        Assert.True(activated);
        Assert.Equal("paste-image.upload", view.ViewModel.PendingCommand?.CommandId);
        Assert.True(view.ViewModel.IsParameterSheetOpen);
    }

    [AvaloniaFact]
    public void AnExternalLinkWithRunNeverExecutesAnything()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        SurfaceHarness.Complete(view.ViewModel.RefreshDevicesAsync(announce: false));

        var activated = SurfaceHarness.Complete(view.ActivateAsync(new ToolActivationRequest(
            MobileToolControlContract.ToolId,
            "workspace",
            "mypowertools://device-tool?device=grant-1&tool=input-monitor&command=input-monitor.rest&run=1")).AsTask());

        Assert.True(activated);
        // The command is preselected, but a web page or another app cannot press the button.
        Assert.Equal("input-monitor.rest", view.ViewModel.PendingCommand?.CommandId);
        Assert.True(view.ViewModel.IsParameterSheetOpen);
        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandInvoke));
        Assert.False(view.ViewModel.HasInvocation);
    }

    [AvaloniaFact]
    public void AToolOnlyActivationOpensTheToolDetailInsteadOfStoppingAtTheCatalog()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        SurfaceHarness.Complete(view.ViewModel.RefreshDevicesAsync(announce: false));

        var activated = SurfaceHarness.Complete(view.ActivateAsync(new ToolActivationRequest(
            MobileToolControlContract.ToolId,
            "workspace",
            "mypowertools://device-tool?device=grant-1&tool=screenease")).AsTask());

        Assert.True(activated);
        Assert.True(view.ViewModel.IsToolSheetOpen);
        Assert.Equal("屏幕舒适", view.ViewModel.ToolSheetTitle);
        Assert.NotEmpty(view.ViewModel.ToolSheetActions);
        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandInvoke));
    }

    [AvaloniaFact]
    public void AnActivationResolvesTheCatalogsToolToModuleRelationship()
    {
        // The catalog says the tool is owned by a module whose name differs from the tool id; the page
        // must follow that relationship instead of assuming ToolId == ModuleId.
        var module = new FakeToolControlModule
        {
            Catalog = new JsonObject
            {
                ["device"] = new JsonObject { ["name"] = "工作电脑", ["platform"] = "windows" },
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["toolId"] = "input-monitor",
                    ["moduleId"] = "wellbeing-module",
                    ["title"] = "输入监测",
                    ["description"] = "",
                    ["category"] = "Wellbeing",
                    ["state"] = "ready",
                    ["availability"] = "available"
                }),
                ["commands"] = new JsonArray(new JsonObject
                {
                    ["commandId"] = "input-monitor.stats",
                    ["moduleId"] = "wellbeing-module",
                    ["title"] = "查看使用统计",
                    ["subtitle"] = "",
                    ["dangerLevel"] = "",
                    ["requiresElevation"] = false,
                    ["supportsProgress"] = false,
                    ["supportsCancellation"] = true,
                    ["allowed"] = true,
                    ["notAllowedReason"] = "",
                    ["parameters"] = new JsonArray()
                })
            }
        };
        var (view, _) = SurfaceHarness.Create(module);
        SurfaceHarness.Complete(view.ViewModel.RefreshDevicesAsync(announce: false));

        var activated = SurfaceHarness.Complete(view.ActivateAsync(new ToolActivationRequest(
            MobileToolControlContract.ToolId,
            "workspace",
            "mypowertools://device-tool?device=grant-1&tool=input-monitor&command=input-monitor.stats")).AsTask());

        Assert.True(activated);
        Assert.Equal("input-monitor.stats", view.ViewModel.PendingCommand?.CommandId);
        Assert.True(view.ViewModel.IsParameterSheetOpen);
    }

    [AvaloniaFact]
    public void AControlCodeActivationOnlyOpensTheConfirmationSheet()
    {
        var module = new FakeToolControlModule();
        module.Devices.Clear();
        var (view, _) = SurfaceHarness.Create(module);

        var activated = SurfaceHarness.Complete(view.ActivateAsync(new ToolActivationRequest(
            MobileToolControlContract.ToolId,
            "workspace",
            SampleCode)).AsTask());

        Assert.True(activated);
        Assert.True(view.ViewModel.IsImportSheetOpen);
        Assert.True(view.ViewModel.HasImportPreview);
        Assert.False(string.IsNullOrWhiteSpace(view.ViewModel.ImportCode));
        // Nothing is saved and no confirm call is made before the user taps 确认导入.
        Assert.Empty(view.ViewModel.Devices);
        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandImportConfirm));
    }

    [AvaloniaFact]
    public void ScanningAConnectionCodeUsesTheSamePreviewAndConfirmPath()
    {
        var module = new FakeToolControlModule();
        module.Devices.Clear();
        var (view, _) = SurfaceHarness.Create(module, scan: _ => Task.FromResult<string?>(SampleCode));

        view.ViewModel.OpenImportSheet();
        SurfaceHarness.Pump();
        SurfaceHarness.Click(SurfaceHarness.FindButton(view, "扫描电脑上的二维码"));

        Assert.True(view.ViewModel.IsImportSheetOpen);
        Assert.True(view.ViewModel.HasImportPreview);
        Assert.Equal("工作电脑", view.ViewModel.ImportPreview!.DeviceName);
        // The scanned code prefills the same confirmation path; nothing is stored yet.
        Assert.Empty(view.ViewModel.Devices);
        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandImportConfirm));

        SurfaceHarness.Complete(((MptAsyncRelayCommand)view.ViewModel.ConfirmImportCommand).ExecuteAsync());
        SurfaceHarness.Pump();
        Assert.Single(view.ViewModel.Devices);
        Assert.Equal(1, module.CountOf(MobileToolControlContract.CommandImportConfirm));
    }

    [AvaloniaFact]
    public void AScanThatReturnsNothingShowsAnErrorAndStoresNothing()
    {
        var module = new FakeToolControlModule();
        module.Devices.Clear();
        var (view, _) = SurfaceHarness.Create(module, scan: _ => Task.FromResult<string?>(null));

        view.ViewModel.OpenImportSheet();
        SurfaceHarness.Pump();
        SurfaceHarness.Click(SurfaceHarness.FindButton(view, "扫描电脑上的二维码"));

        Assert.True(view.ViewModel.IsImportSheetOpen);
        Assert.True(view.ViewModel.HasImportError);
        Assert.False(view.ViewModel.HasImportPreview);
        Assert.Empty(view.ViewModel.Devices);
        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandImportConfirm));
    }

    [AvaloniaFact]
    public void TheViewExposesTheSharedActivationAndBackContracts()
    {
        var (view, _) = SurfaceHarness.Create();
        Assert.IsAssignableFrom<IMptAvaloniaSurfaceBackHandler>(view);
        Assert.IsAssignableFrom<IMptAvaloniaSurfaceActivationHandler>(view);
    }

    [AvaloniaFact]
    public void ActivationRefusesAnUnknownDeviceOrCommand()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        SurfaceHarness.Complete(view.ViewModel.RefreshDevicesAsync(announce: false));

        var unknownDevice = SurfaceHarness.Complete(view.ActivateAsync(new ToolActivationRequest(
            MobileToolControlContract.ToolId,
            "workspace",
            "mypowertools://device-tool?device=missing&command=input-monitor.rest")).AsTask());
        var unknownCommand = SurfaceHarness.Complete(view.ActivateAsync(new ToolActivationRequest(
            MobileToolControlContract.ToolId,
            "workspace",
            "mypowertools://device-tool?device=grant-1&command=input-monitor.invented")).AsTask());

        Assert.False(unknownDevice);
        Assert.False(unknownCommand);
        Assert.False(view.ViewModel.IsParameterSheetOpen);
    }

    [AvaloniaFact]
    public void BackClosesASheetBeforeItLeavesThePage()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;

        vm.OpenImportSheet();
        SurfaceHarness.Pump();
        Assert.True(view.TryHandleBack());
        Assert.False(vm.IsImportSheetOpen);

        SurfaceHarness.Complete(vm.OpenDeviceAsync(new MobileToolDeviceItem(
            "grant-1", "工作电脑", "http://100.64.0.2:49541", "windows", "reachable", "已连接", true)));
        Assert.True(view.TryHandleBack());
        Assert.True(vm.IsDeviceListPageOpen);

        Assert.False(view.TryHandleBack());
    }
}
