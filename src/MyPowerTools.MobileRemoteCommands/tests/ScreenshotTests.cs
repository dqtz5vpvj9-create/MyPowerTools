using Avalonia.Headless;
using Avalonia.Headless.XUnit;

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// Renders the page and writes the frames next to the build for visual review against the approved
/// prototype. It is a check, not a golden-file test: the assertions are about the page actually
/// rendering (non-empty frame, expected size), and the PNGs are the evidence a human compares.
///
/// Output goes to <c>artifacts/.tmp-android-verify/rc-shots/</c> (the registered temporary verification
/// area) and never into the repository source tree.
/// </summary>
public sealed class ScreenshotTests
{
    private const string OutputDirectory = "artifacts/.tmp-android-verify/rc-shots";

    [AvaloniaFact]
    public void The_phone_page_renders_at_the_reviewed_widths()
    {
        var module = new FakeModule();
        module.AddHost("lab-host", host: "192.168.22.24", port: 22);
        module.TrustedKeys.Add(new MobileHostKeyEntry(
            "192.168.22.24",
            22,
            "ssh-ed25519",
            "SHA256:9ZQm2Vr4Tz1k8pQxWn3sYb6Ld0Cf7Hg5Jk2Mn4Pq8Rs",
            "2025-09-27 09:10"));
        module.HistoryCount = 12;
        module.HistoryLatestLabel = "查看服务器状态";

        using var harness = SurfaceHarness.Create(module);
        var view = harness.View;
        harness.WaitFor(() => view.ViewModel.HasCommands && view.ViewModel.Hosts.Count == 1, "页面数据没有加载。");

        foreach (var (width, height, name) in new[]
                 {
                     (320d, 720d, "320-main"),
                     (360d, 800d, "360-main"),
                     (390d, 844d, "390-main")
                 })
        {
            harness.Resize(width, height);
            Save(harness, $"{name}.png");
        }

        harness.Resize(390, 844);

        // The run sheet after a finished run: progress, readable output, retry/edit/copy actions.
        module.RunOutput = "Uploading input files...\nExecuting remote command...\n09:18 up 12 days, 4:32\nload average: 0.21, 0.18, 0.15\nExecution complete";
        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");
        Save(harness, "run-sheet-result.png");

        // A failure keeps the retry path.
        module.RunState = RemoteCommandsMobileContract.StateFailed;
        module.RunMessage = "远端命令返回退出码 2";
        module.RunExitCode = 2;
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "第二次运行没有结束。");
        Save(harness, "run-sheet-failed.png");

        // The command editor and the connection sheet.
        harness.Click(view.EditCommandButtonForTests);
        Save(harness, "command-editor.png");

        harness.Click(view.ConnectionButtonForTests);
        Save(harness, "connection-sheet.png");

        harness.Click(view.CatalogButtonForTests);
        Save(harness, "catalog-sheet.png");

        // A 320 dp wide command editor is the tightest case for the form.
        harness.Resize(320, 720);
        harness.Click(view.AddCommandButtonForTests);
        Save(harness, "320-command-editor.png");
    }

    private static void Save(SurfaceHarness harness, string fileName)
    {
        var frame = harness.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 0 && frame.PixelSize.Height > 0, $"{fileName} 是空帧。");

        var directory = Path.Combine(RepoPaths.Root(), OutputDirectory);
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, fileName));
    }
}
