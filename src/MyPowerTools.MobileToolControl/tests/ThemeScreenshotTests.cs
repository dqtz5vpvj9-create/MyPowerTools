using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;

namespace MyPowerTools.MobileToolControl.Tests;

/// <summary>
/// Renders the reviewed frames through the shipped mobile theme with Skia and writes them to
/// <c>artifacts/.tmp-android-verify/g2-theme-shots/</c> for visual review against the approved
/// prototype. It is a check, not a golden-file test: the assertions are about the page really
/// rendering, following the theme variant, and keeping its touch targets unclipped.
/// </summary>
public sealed class ThemeScreenshotTests
{
    private static PhoneHost OpenDevicePage()
    {
        var module = new FakeToolControlModule { Catalog = FakeToolControlModule.SampleCatalog() };
        var host = PhoneHost.Create(module, width: 390, height: 844);
        host.Complete(host.Vm.RefreshDevicesAsync(announce: false));
        host.Complete(host.Vm.OpenDeviceAsync(host.Vm.Devices[0]));
        host.WaitFor(() => host.Vm.HasPriorityActions, "电脑目录没有加载。");
        return host;
    }

    [AvaloniaFact]
    public void TheDeviceToolPageRendersInLightAndDark()
    {
        using var host = OpenDevicePage();

        host.SetTheme(ThemeVariant.Light);
        var light = host.Save("390-device-tools-light.png");
        Assert.True(new FileInfo(light).Length > 5_000, "浅色帧过小，可能是空渲染。");

        host.SetTheme(ThemeVariant.Dark);
        var dark = host.Save("390-device-tools-dark.png");
        Assert.True(new FileInfo(dark).Length > 5_000, "深色帧过小，可能是空渲染。");
        Assert.NotEqual(new FileInfo(light).Length, new FileInfo(dark).Length);

        // The same page instance, back to light: the frame is the light one again.
        host.SetTheme(ThemeVariant.Light);
        var lightAgain = host.Save("390-device-tools-light-again.png");
        Assert.Equal(new FileInfo(light).Length, new FileInfo(lightAgain).Length);
    }

    [AvaloniaFact]
    public void TheParameterSheetFitsANarrowPhoneWithoutClippingItsActions()
    {
        using var host = OpenDevicePage();
        host.Resize(320, 720);

        var upload = host.Vm.PriorityActions.Single(row => row.Command.CommandId == "paste-image.upload");
        host.Vm.OpenCommand(upload);
        var single = host.Vm.ParameterFields[0];
        single.Value = "/sdcard/DCIM/photo.png";
        host.Pump();
        host.WaitFor(() => host.Vm.IsParameterSheetOpen, "参数面板没有打开。");
        Assert.True(single.HasError is false);

        var sheetPath = host.Save("320-parameter-sheet.png");
        Assert.True(new FileInfo(sheetPath).Length > 5_000, "参数面板帧过小。");

        var sheet = host.FindAll<Border>(border => border.Classes.Contains(MobileToolControlTheme.SheetClass))
            .FirstOrDefault(border => border.IsEffectivelyVisible);
        Assert.NotNull(sheet);
        Assert.True(sheet!.Bounds.Height > 0 && sheet.Bounds.Height <= 720, $"参数面板高度 {sheet.Bounds.Height} 超出屏幕。");

        Assert.True(MobileToolControlTheme.TryGetResource<double>(
            MobileToolControlTheme.TouchTargetMinKey, out var touchTarget));
        var runButton = host.FindAll<Button>(button => button.Classes.Contains(MobileToolControlTheme.PrimaryClass))
            .FirstOrDefault(button => button.IsEffectivelyVisible && button.Content as string == "发送到电脑");
        Assert.NotNull(runButton);
        Assert.True(
            runButton!.Bounds.Height >= touchTarget - 0.5,
            $"“发送到电脑”高度 {runButton.Bounds.Height} 小于 {touchTarget}，被裁切。");
        Assert.True(runButton.Bounds.Width > 0 && runButton.Bounds.Width <= 320, "按钮宽度超出屏幕。");
    }

    [AvaloniaFact]
    public void TheScannedImportConfirmationSheetRendersAt320()
    {
        const string code = "mpt://control/eyJ2ZXJzaW9uIjoxLCJlbmRwb2ludCI6Imh0dHA6Ly8xMDAuNjQuMC4yOjQ5NTQxIn0";
        var module = new FakeToolControlModule();
        module.Devices.Clear();
        using var host = PhoneHost.Create(module, width: 320, height: 720, scan: _ => Task.FromResult<string?>(code));
        host.Complete(host.Vm.RefreshDevicesAsync(announce: false));

        host.Vm.OpenImportSheet();
        host.Pump();
        host.Click(SurfaceHarness.FindButton(host.View, "扫描电脑上的二维码"));
        host.WaitFor(() => host.Vm.HasImportPreview, "扫码后没有出现确认信息。");

        var path = host.Save("320-import-confirm-sheet.png");
        Assert.True(new FileInfo(path).Length > 5_000, "导入确认帧过小。");

        Assert.True(MobileToolControlTheme.TryGetResource<double>(
            MobileToolControlTheme.PrimaryButtonMinHeightKey, out var minHeight));
        var confirm = host.FindAll<Button>(button => button.Classes.Contains(MobileToolControlTheme.PrimaryClass))
            .FirstOrDefault(button => button.IsEffectivelyVisible && button.Content as string == "确认导入");
        Assert.NotNull(confirm);
        Assert.True(confirm!.Bounds.Height >= minHeight - 0.5, "确认导入按钮被裁切。");

        // The action row stays reachable: the sheet scrolls instead of clipping it away.
        var sheet = host.FindAll<Border>(border => border.Classes.Contains(MobileToolControlTheme.SheetClass))
            .FirstOrDefault(border => border.IsEffectivelyVisible);
        Assert.NotNull(sheet);
        Assert.True(sheet!.Bounds.Height > 0 && sheet.Bounds.Height <= 720, $"导入面板高度 {sheet.Bounds.Height} 超出屏幕。");
    }
}
