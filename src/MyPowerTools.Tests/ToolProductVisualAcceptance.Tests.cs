using System.Text.Json.Nodes;
using MyPowerTools.Shell.Avalonia;
using SkiaSharp;

namespace MyPowerTools.Tests;

public sealed class ToolProductVisualAcceptanceTests
{
    [Fact]
    public void Product_foundation_entry_renders_three_real_shell_pages()
    {
        var output = Path.Combine(
            Path.GetTempPath(),
            "mpt-tool-product-screenshots",
            Guid.NewGuid().ToString("N"));

        try
        {
            var manifestPath = VisualTestProcess.WriteSnapshotSet(
                output,
                "light",
                "1366x768",
                "normal",
                "*",
                productFoundation: true);
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var screenshots = manifest["screenshots"]!.AsArray();
            var expectedScreens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["home-ready"] = "shell.home",
                ["general-settings"] = "shell.general",
                ["tools-catalog"] = "shell.tools-catalog"
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
            var manifestPath = VisualTestProcess.WriteSnapshotSet(
                output,
                "light",
                "2048x1152",
                "normal",
                surface,
                productFoundation: true);
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

    private static void AssertFrameEdgesAreRendered(string imagePath)
    {
        using var image = SKBitmap.Decode(imagePath);
        Assert.NotNull(image);
        var blackEdgePixels = 0;
        var transparentEdgePixels = 0;
        var edgePixelCount = 0;

        void InspectPixel(int x, int y)
        {
            var pixel = image.GetPixel(x, y);
            if (pixel.Alpha < 250)
            {
                transparentEdgePixels++;
            }
            if (pixel.Alpha > 0 && pixel.Red < 8 && pixel.Green < 8 && pixel.Blue < 8)
            {
                blackEdgePixels++;
            }

            edgePixelCount++;
        }

        for (var x = 0; x < image.Width; x++)
        {
            InspectPixel(x, 0);
            InspectPixel(x, image.Height - 1);
        }

        for (var y = 1; y < image.Height - 1; y++)
        {
            InspectPixel(0, y);
            InspectPixel(image.Width - 1, y);
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
        using var image = SKBitmap.Decode(imagePath);
        Assert.NotNull(image);

        (byte Blue, byte Green, byte Red, byte Alpha) ReadPixel((int X, int Y) point)
        {
            var pixel = image.GetPixel(point.X, point.Y);
            return (pixel.Blue, pixel.Green, pixel.Red, pixel.Alpha);
        }

        var first = ReadPixel(firstPoint);
        var second = ReadPixel(secondPoint);
        Assert.NotEqual(second, first);
    }
}
