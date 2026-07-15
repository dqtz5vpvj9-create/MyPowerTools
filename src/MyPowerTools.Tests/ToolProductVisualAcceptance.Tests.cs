using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MyPowerTools.Shell.Avalonia;

namespace MyPowerTools.Tests;

public sealed class ToolProductVisualAcceptanceTests
{
    [Fact]
    public void Product_foundation_entry_renders_eight_real_avalonia_pages()
    {
        var output = Path.Combine(
            Path.GetTempPath(),
            "mpt-tool-product-screenshots",
            Guid.NewGuid().ToString("N"));

        try
        {
            var manifestPath = ShellRealScreenshotWriter.WriteProductFoundationSnapshotSet(
                output,
                "light",
                "1366x768",
                "normal");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var screenshots = manifest["screenshots"]!.AsArray();
            var expectedScreens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["home-ready"] = "shell.home",
                ["general-settings"] = "shell.general",
                ["tools-catalog"] = "shell.tools-catalog",
                ["adb-forwarder-forward"] = "adb-forwarder.forward",
                ["screenease-profiles"] = "screenease.profiles",
                ["doubao-agent-services"] = "doubao-agent.services",
                ["smartbird-thermostat-overview"] = "smartbird-thermostat.overview",
                ["remote-notifications-inbox"] = "android-tools.notifications.inbox"
            };

            Assert.Equal("real-avalonia-screenshot", manifest["artifactKind"]!.GetValue<string>());
            Assert.Equal("product-fixture", manifest["dataSource"]!.GetValue<string>());
            Assert.Equal(expectedScreens.Count, manifest["screenshotCount"]!.GetValue<int>());
            Assert.Equal(expectedScreens.Count, screenshots.Count);

            foreach (var expected in expectedScreens)
            {
                var screenshot = Assert.Single(screenshots.Where(item =>
                    string.Equals(
                        item!["screenId"]!.GetValue<string>(),
                        expected.Key,
                        StringComparison.OrdinalIgnoreCase)))!.AsObject();
                Assert.Equal(expected.Value, screenshot["surfaceId"]!.GetValue<string>());
                Assert.Equal("Avalonia.Headless", screenshot["renderer"]!.GetValue<string>());
                Assert.Equal(1366, screenshot["width"]!.GetValue<int>());
                Assert.Equal(768, screenshot["height"]!.GetValue<int>());

                var imagePath = screenshot["imagePath"]!.GetValue<string>();
                var bytes = File.ReadAllBytes(imagePath);
                Assert.True(bytes.Length > 1000, $"Real screenshot {imagePath} should contain rendered UI.");
                Assert.Equal(
                    new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
                    bytes.Take(8).ToArray());
                Assert.Equal(64, screenshot["sha256"]!.GetValue<string>().Length);
                AssertFrameEdgesAreRendered(imagePath);
            }

            Assert.Equal(
                expectedScreens.Count,
                screenshots
                    .Select(item => item!["sha256"]!.GetValue<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("shell.home", "home-ready")]
    [InlineData("shell.general", "general-settings")]
    public void Product_shell_wide_layout_renders_dashboard_and_general_at_2048(
        string surface,
        string expectedScreenId)
    {
        var output = Path.Combine(
            Path.GetTempPath(),
            "mpt-tool-product-wide-layout",
            Guid.NewGuid().ToString("N"));

        try
        {
            var manifestPath = ShellRealScreenshotWriter.WriteProductFoundationSnapshotSet(
                output,
                "light",
                "2048x1152",
                "normal",
                surface);
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var screenshot = Assert.Single(manifest["screenshots"]!.AsArray())!.AsObject();

            Assert.Equal(expectedScreenId, screenshot["screenId"]!.GetValue<string>());
            Assert.Equal(surface, screenshot["surfaceId"]!.GetValue<string>());
            Assert.Equal(2048, screenshot["width"]!.GetValue<int>());
            Assert.Equal(1152, screenshot["height"]!.GetValue<int>());

            var imagePath = screenshot["imagePath"]!.GetValue<string>();
            Assert.True(File.Exists(imagePath));
            AssertFrameEdgesAreRendered(imagePath);
            if (surface == "shell.home")
            {
                AssertPixelsDiffer(imagePath, (1800, 180), (1900, 900));
            }
            else
            {
                AssertPixelsDiffer(imagePath, (1600, 200), (1800, 200));
            }
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("scroll", "mouse-wheel:down:6")]
    [InlineData("filter", "mouse-click:label-chip:")]
    [InlineData("detail", "mouse-double-click:message:")]
    [InlineData("activation", "toast-launch:mypowertools://remote-notification?id=")]
    public void Remote_notifications_headless_scenarios_simulate_input_and_write_png(
        string scenario,
        string expectedStepPrefix)
    {
        var output = Path.Combine(
            Path.GetTempPath(),
            "mpt-tool-product-interactions",
            Guid.NewGuid().ToString("N"));

        try
        {
            var manifestPath = ShellRealScreenshotWriter.WriteProductFoundationSnapshotSet(
                output,
                "light",
                "1366x768",
                "normal",
                "android-tools.notifications.inbox",
                scenario);
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var screenshot = Assert.Single(manifest["screenshots"]!.AsArray())!.AsObject();
            var steps = screenshot["interactionSteps"]!.AsArray()
                .Select(item => item!.GetValue<string>())
                .ToArray();

            Assert.Equal(scenario, manifest["scenario"]!.GetValue<string>());
            Assert.Equal(scenario, screenshot["scenario"]!.GetValue<string>());
            Assert.Equal("remote-notifications-inbox", screenshot["screenId"]!.GetValue<string>());
            Assert.Equal("Avalonia.Headless", screenshot["renderer"]!.GetValue<string>());
            Assert.Contains(steps, step => step.StartsWith(expectedStepPrefix, StringComparison.Ordinal));

            var imagePath = screenshot["imagePath"]!.GetValue<string>();
            Assert.Contains($".{scenario}.", Path.GetFileName(imagePath), StringComparison.OrdinalIgnoreCase);
            var bytes = File.ReadAllBytes(imagePath);
            Assert.True(bytes.Length > 1000, $"Interaction screenshot {imagePath} should contain rendered UI.");
            Assert.Equal(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
                bytes.Take(8).ToArray());
            AssertFrameEdgesAreRendered(imagePath);
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public void Remote_notifications_wide_product_page_renders_at_2048_by_1152()
    {
        var output = Path.Combine(
            Path.GetTempPath(),
            "mpt-remote-notifications-wide",
            Guid.NewGuid().ToString("N"));

        try
        {
            var manifestPath = ShellRealScreenshotWriter.WriteProductFoundationSnapshotSet(
                output,
                "light",
                "2048x1152",
                "normal",
                "android-tools.notifications.inbox");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var screenshot = Assert.Single(manifest["screenshots"]!.AsArray())!.AsObject();

            Assert.Equal("remote-notifications-inbox", screenshot["screenId"]!.GetValue<string>());
            Assert.Equal(2048, screenshot["width"]!.GetValue<int>());
            Assert.Equal(1152, screenshot["height"]!.GetValue<int>());
            Assert.Equal("Avalonia.Headless", screenshot["renderer"]!.GetValue<string>());

            var imagePath = screenshot["imagePath"]!.GetValue<string>();
            Assert.True(File.ReadAllBytes(imagePath).Length > 1000);
            AssertFrameEdgesAreRendered(imagePath);
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    private static void AssertFrameEdgesAreRendered(string imagePath)
    {
        using var source = new Bitmap(imagePath);
        using var copy = new WriteableBitmap(
            source.PixelSize,
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using var framebuffer = copy.Lock();
        source.CopyPixels(framebuffer);

        var pixels = new byte[framebuffer.RowBytes * framebuffer.Size.Height];
        Marshal.Copy(framebuffer.Address, pixels, 0, pixels.Length);
        var blackEdgePixels = 0;
        var transparentEdgePixels = 0;
        var edgePixelCount = 0;

        void InspectPixel(int x, int y)
        {
            var offset = (y * framebuffer.RowBytes) + (x * 4);
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            var alpha = pixels[offset + 3];
            if (alpha < 250)
            {
                transparentEdgePixels++;
            }
            if (alpha > 0 && red < 8 && green < 8 && blue < 8)
            {
                blackEdgePixels++;
            }

            edgePixelCount++;
        }

        for (var x = 0; x < framebuffer.Size.Width; x++)
        {
            InspectPixel(x, 0);
            InspectPixel(x, framebuffer.Size.Height - 1);
        }

        for (var y = 1; y < framebuffer.Size.Height - 1; y++)
        {
            InspectPixel(0, y);
            InspectPixel(framebuffer.Size.Width - 1, y);
        }

        Assert.True(
            blackEdgePixels <= edgePixelCount / 100,
            $"Headless screenshot contains {blackEdgePixels} near-black edge pixels out of {edgePixelCount}; the frame may be a partial dirty render.");
        Assert.True(
            transparentEdgePixels <= edgePixelCount / 100,
            $"Headless screenshot contains {transparentEdgePixels} transparent edge pixels out of {edgePixelCount}; the frame is not an opaque acceptance artifact.");
    }

    private static void AssertPixelsDiffer(
        string imagePath,
        (int X, int Y) firstPoint,
        (int X, int Y) secondPoint)
    {
        using var source = new Bitmap(imagePath);
        using var copy = new WriteableBitmap(
            source.PixelSize,
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using var framebuffer = copy.Lock();
        source.CopyPixels(framebuffer);

        var pixels = new byte[framebuffer.RowBytes * framebuffer.Size.Height];
        Marshal.Copy(framebuffer.Address, pixels, 0, pixels.Length);

        (byte Blue, byte Green, byte Red, byte Alpha) ReadPixel((int X, int Y) point)
        {
            var offset = (point.Y * framebuffer.RowBytes) + (point.X * 4);
            return (pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
        }

        var first = ReadPixel(firstPoint);
        var second = ReadPixel(secondPoint);
        Assert.NotEqual(second, first);
    }
}
