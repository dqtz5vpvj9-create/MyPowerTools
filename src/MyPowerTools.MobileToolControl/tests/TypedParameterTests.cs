using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileToolControl.Tests;

/// <summary>
/// Parameters are encoded with the type the computer declared: an <c>int</c> parameter must reach the
/// module as a JSON number, a <c>bool</c> as a boolean, and a value that does not match never leaves
/// the phone.
/// </summary>
public sealed class TypedParameterTests
{
    private static (FakeToolControlModule Module, MobileToolControlViewModel Vm) OpenUpload()
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
        SurfaceHarness.Pump();
        return (module, vm);
    }

    [AvaloniaFact]
    public void TheDeclaredTypesReachTheModuleAsJsonTypes()
    {
        var (module, vm) = OpenUpload();
        vm.ParameterFields[0].Value = "/sdcard/a.png";
        vm.ParameterFields[1].Value = "800";
        vm.ParameterFields[2].BooleanValue = false;
        SurfaceHarness.Pump();

        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.RunCommand).ExecuteAsync());
        SurfaceHarness.Pump();

        var args = module.LastArgs(MobileToolControlContract.CommandInvoke)!
            [MobileToolControlContract.ArgumentArgs]!.AsObject();
        Assert.Equal("/sdcard/a.png", args["path"]!.GetValue<string>());
        // GetValue<long> succeeds only because the value really is a JSON number (a string would throw).
        Assert.IsType<JsonValue>(args["width"], exactMatch: false);
        Assert.Equal(800L, args["width"]!.GetValue<long>());
        Assert.False(args["reuse"]!.GetValue<bool>());
        Assert.Equal("电脑执行中", vm.InvocationStateText);
    }

    [AvaloniaTheory]
    [InlineData("八百")]
    [InlineData("8.5")]
    public void AnInvalidIntegerIsRejectedOnTheFieldAndNeverSubmitted(string value)
    {
        var (module, vm) = OpenUpload();
        vm.ParameterFields[0].Value = "/sdcard/a.png";
        vm.ParameterFields[1].Value = value;
        SurfaceHarness.Pump();

        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.RunCommand).ExecuteAsync());
        SurfaceHarness.Pump();

        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandInvoke));
        Assert.True(vm.IsParameterSheetOpen);
        var field = vm.ParameterFields[1];
        Assert.True(field.HasError);
        Assert.Contains("整数", field.Error, StringComparison.Ordinal);
        Assert.Contains("宽度", vm.FeedbackText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void AnEmptyOptionalNumberIsOmittedInsteadOfSentAsAString()
    {
        var (module, vm) = OpenUpload();
        vm.ParameterFields[0].Value = "/sdcard/a.png";
        vm.ParameterFields[1].Value = "";
        SurfaceHarness.Pump();

        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.RunCommand).ExecuteAsync());
        SurfaceHarness.Pump();

        var args = module.LastArgs(MobileToolControlContract.CommandInvoke)!
            [MobileToolControlContract.ArgumentArgs]!.AsObject();
        Assert.False(args.ContainsKey("width"));
        Assert.Equal("/sdcard/a.png", args["path"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public void AMissingRequiredStringIsRejectedOnTheFieldAndNeverSubmitted()
    {
        var (module, vm) = OpenUpload();
        vm.ParameterFields[0].Value = "   ";
        SurfaceHarness.Pump();

        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.RunCommand).ExecuteAsync());
        SurfaceHarness.Pump();

        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandInvoke));
        Assert.True(vm.ParameterFields[0].HasError);
        Assert.Contains("必填", vm.ParameterFields[0].Error, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ObjectParametersUseAnAdvancedJsonFieldAndRejectNonJsonText()
    {
        var module = new FakeToolControlModule
        {
            Catalog = new JsonObject
            {
                ["device"] = new JsonObject { ["name"] = "工作电脑", ["platform"] = "windows" },
                ["tools"] = new JsonArray(),
                ["commands"] = new JsonArray(new JsonObject
                {
                    ["commandId"] = "nssm-manager.service.configure",
                    ["moduleId"] = "nssm-manager",
                    ["title"] = "配置服务",
                    ["subtitle"] = "",
                    ["dangerLevel"] = "",
                    ["requiresElevation"] = false,
                    ["supportsProgress"] = false,
                    ["supportsCancellation"] = true,
                    ["allowed"] = true,
                    ["notAllowedReason"] = "",
                    ["parameters"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "settings",
                        ["label"] = "设置",
                        ["type"] = "object",
                        ["required"] = true,
                        ["defaultValue"] = ""
                    })
                })
            }
        };
        var (view, _) = SurfaceHarness.Create(module);
        var vm = view.ViewModel;
        vm.Activate();
        SurfaceHarness.Pump();
        SurfaceHarness.Complete(vm.OpenDeviceAsync(new MobileToolDeviceItem(
            "grant-1", "工作电脑", "http://100.64.0.2:49541", "windows", "reachable", "已连接", true)));
        var row = vm.OtherGroups.SelectMany(group => group.Actions)
            .Single(action => action.Command.CommandId == "nssm-manager.service.configure");
        vm.OpenCommand(row);
        SurfaceHarness.Pump();

        Assert.True(vm.ParameterFields[0].IsJson);
        vm.ParameterFields[0].Value = "not json";
        SurfaceHarness.Pump();
        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.RunCommand).ExecuteAsync());
        SurfaceHarness.Pump();
        Assert.Equal(0, module.CountOf(MobileToolControlContract.CommandInvoke));
        Assert.Contains("JSON", vm.ParameterFields[0].Error, StringComparison.Ordinal);

        vm.ParameterFields[0].Value = "{\"start\":\"auto\"}";
        SurfaceHarness.Pump();
        SurfaceHarness.Complete(((MptAsyncRelayCommand)vm.RunCommand).ExecuteAsync());
        SurfaceHarness.Pump();
        var args = module.LastArgs(MobileToolControlContract.CommandInvoke)!
            [MobileToolControlContract.ArgumentArgs]!.AsObject();
        Assert.Equal("auto", args["settings"]!["start"]!.GetValue<string>());
    }
}
