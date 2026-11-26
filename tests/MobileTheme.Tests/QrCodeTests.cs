using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk.Controls;
using ZXing;
using ZXing.Common;

namespace MobileTheme.Tests;

/// <summary>
/// A QR control is only useful if a camera could read what it draws, so these tests render the
/// control through the headless Skia renderer, read the pixels back and decode them with the same
/// ZXing build the phone scanner uses. Nothing here reaches into the control's internals: every
/// assertion is about the symbol that reached the framebuffer.
/// </summary>
public sealed class QrCodeTests
{
    private const string Token = "s3cret-receiver-token-0123456789";

    // The page underneath the symbol in the dark-surface cases. It is neither black nor white, so
    // "what the control painted" can be told apart from "what the page painted".
    private static readonly Color DarkSurface = Color.Parse("#FF171B22");

    // The three connection codes the app hands around, built the way the sender builds them: the
    // payload keeps the whole secret, base64url encoded but never shortened.
    private static string PairCode { get; } = "mpt://pair/" + Base64Url(
        $$"""{"DeviceId":"dev-1","Name":"工作电脑","Address":"100.64.0.5","Token":"{{Token}}"}""");

    private static string CloudCode { get; } = "mpt://cloud/" + Base64Url(
        $$"""{"url":"https://dav.example/remote.php","username":"chris","password":"{{Token}}"}""");

    private static string ControlCode { get; } = "mpt://control/" + Base64Url(
        $$"""{"version":1,"endpoint":"http://100.64.0.2:49541","grantId":"grant-7","deviceName":"工作电脑","token":"{{Token}}"}""");

    private static string AssistantCode { get; } = "mpt://assistant/" + Base64Url(
        $$$"""{"version":1,"conversationId":"self-7","key":"{{{Token}}}","deviceId":"pc-1","name":"工作电脑","address":"100.64.0.5","port":47165,"relay":{"url":"https://dav.example/mpt","username":"mpt-self","password":"{{{Token}}}"}}""");

    public static TheoryData<string> Codes => new() { PairCode, CloudCode, ControlCode, AssistantCode };

    [AvaloniaTheory]
    [MemberData(nameof(Codes))]
    public void A_rendered_connection_code_decodes_back_to_the_whole_string(string code)
    {
        var (_, window) = ShowCode(code, controlSide: 240, surfaceSide: 320);
        try
        {
            Assert.Equal(code, Decode(Capture(window)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_symbol_keeps_a_full_quiet_zone_and_whole_pixel_modules()
    {
        var (qr, window) = ShowCode(PairCode, controlSide: 240, surfaceSide: 320);
        try
        {
            var pixels = Capture(window);
            var area = ControlArea(qr, window);

            // The quiet zone is the outermost ring of the painted square and it is always white,
            // so the white bounding box is exactly the square the control drew.
            var square = pixels.WhiteBounds(area);
            var dark = pixels.BlackBounds(area);
            Assert.True(square.Width > 0, "no symbol was drawn");
            Assert.True(dark.Width > 0, "the symbol has no modules");

            Assert.Equal(square.Width, square.Height);
            Assert.True(
                square.X >= area.X && square.Y >= area.Y && square.Right <= area.Right && square.Bottom <= area.Bottom,
                $"symbol {square} escaped its {area} container");

            // Every pixel of the symbol is either the module colour or the quiet zone. A smoothed
            // module edge would show up here as a grey pixel, and grey edges are what cost a
            // camera its lock.
            Assert.Equal(0, pixels.CountBlended(square));

            // The top-left finder pattern is seven modules wide, so the first black run on the
            // symbol's top row measures the module size without trusting the implementation.
            var finderRun = pixels.FirstBlackRun(dark.X, dark.Y, dark.Right);
            Assert.Equal(0, finderRun % 7);
            var modulePixels = finderRun / 7;
            Assert.True(modulePixels >= 1, $"module size {modulePixels}px");

            // Four modules of light margin on every side, exactly: the module edges are aligned to
            // device pixels, so no rounding slack is needed here.
            Assert.Equal(4 * modulePixels, dark.X - square.X);
            Assert.Equal(4 * modulePixels, dark.Y - square.Y);
            Assert.Equal(4 * modulePixels, square.Right - dark.Right);
            Assert.Equal(4 * modulePixels, square.Bottom - dark.Bottom);

            // A uniform module grid: the drawn symbol is a whole number of modules wide, and that
            // count plus the two quiet zones is a legal QR size (21 + 4k modules).
            Assert.Equal(0, dark.Width % modulePixels);
            Assert.Equal(0, dark.Height % modulePixels);
            Assert.Equal(1, ((dark.Width / modulePixels) + (2 * 4)) % 4);

            // The control centres the symbol inside the space it was given.
            Assert.InRange(Math.Abs((square.X - area.X) - (area.Right - square.Right)), 0, 1);
            Assert.InRange(Math.Abs((square.Y - area.Y) - (area.Bottom - square.Bottom)), 0, 1);

            Assert.Equal(PairCode, Decode(pixels));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_larger_container_draws_larger_modules_of_the_same_symbol()
    {
        var (small, smallWindow) = ShowCode(PairCode, controlSide: 240, surfaceSide: 320);
        var (large, largeWindow) = ShowCode(PairCode, controlSide: 380, surfaceSide: 420);
        try
        {
            var smallPixels = Capture(smallWindow);
            var largePixels = Capture(largeWindow);
            var smallSymbol = MeasureSymbol(smallPixels, ControlArea(small, smallWindow));
            var largeSymbol = MeasureSymbol(largePixels, ControlArea(large, largeWindow));

            Assert.True(
                largeSymbol.ModulePixels > smallSymbol.ModulePixels,
                $"module size stayed at {largeSymbol.ModulePixels}px");
            // Scaling the drawing must not change which symbol was encoded.
            Assert.Equal(smallSymbol.Modules, largeSymbol.Modules);

            Assert.Equal(PairCode, Decode(smallPixels));
            Assert.Equal(PairCode, Decode(largePixels));
        }
        finally
        {
            smallWindow.Close();
            largeWindow.Close();
        }
    }

    [AvaloniaFact]
    public void A_dark_page_still_gets_a_white_symbol_with_black_modules()
    {
        var (qr, window) = ShowCode(PairCode, controlSide: 240, surfaceSide: 320,
            surface: new SolidColorBrush(DarkSurface));
        try
        {
            var pixels = Capture(window);
            var area = ControlArea(qr, window);
            var square = pixels.WhiteBounds(area);
            var dark = pixels.BlackBounds(area);

            // The white really is painted by the control: just outside the square the page shows.
            Assert.True(square.X > area.X, "the symbol filled the whole control, nothing to compare against");
            Assert.Equal(DarkSurface, pixels.At(square.X - 1, square.Y));
            Assert.True(pixels.IsWhite(square.X, square.Y), "quiet zone is not white on a dark page");
            Assert.True(pixels.IsBlack(dark.X, dark.Y), "finder pattern is not black");
            Assert.Equal(PairCode, Decode(pixels));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Clearing_the_value_removes_the_symbol_and_unencodable_values_stay_blank()
    {
        var (qr, window) = ShowCode(PairCode, controlSide: 240, surfaceSide: 320,
            surface: new SolidColorBrush(DarkSurface));
        try
        {
            var area = ControlArea(qr, window);
            Assert.True(Capture(window).BlackBounds(area).Width > 0, "the first symbol was not drawn");

            qr.Value = null;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var cleared = Capture(window);
            Assert.Equal(0, cleared.BlackBounds(area).Width);
            Assert.Equal(0, cleared.WhiteBounds(area).Width);

            // A code too long for any QR version, and a valid code too dense for the space it was
            // given: both must leave a blank surface instead of an exception on a settings page.
            qr.Value = new string('x', 4000);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, Capture(window).BlackBounds(area).Width);

            var (dense, denseWindow) = ShowCode(new string('y', 1500), controlSide: 60, surfaceSide: 120);
            try
            {
                var denseArea = ControlArea(dense, denseWindow);
                Assert.Equal(0, Capture(denseWindow).BlackBounds(denseArea).Width);
            }
            finally
            {
                denseWindow.Close();
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_control_keeps_the_code_out_of_text_automation_and_diagnostics()
    {
        var (qr, window) = ShowCode(PairCode, controlSide: 240, surfaceSide: 320);
        try
        {
            Assert.Equal(PairCode, qr.Value);
            Assert.DoesNotContain(Token, qr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(PairCode, qr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Token, AutomationProperties.GetName(qr) ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, AutomationProperties.GetHelpText(qr) ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, AutomationProperties.GetItemStatus(qr) ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_control_adapts_to_its_container_and_never_asks_for_less_than_240()
    {
        Assert.True(MptQrCode.RecommendedDisplaySize >= 240, $"recommended size {MptQrCode.RecommendedDisplaySize}");

        // Fully unconstrained: the control asks for the recommended size.
        var floating = new MptQrCode();
        floating.Measure(Size.Infinity);
        Assert.Equal(MptQrCode.RecommendedDisplaySize, floating.DesiredSize.Width);
        Assert.Equal(MptQrCode.RecommendedDisplaySize, floating.DesiredSize.Height);

        // A container that constrains one axis still gets a square of that side.
        var stacked = new MptQrCode();
        var panelWindow = new Window { Width = 400, Height = 400, Content = new StackPanel { Children = { stacked } } };
        var cell = new MptQrCode { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var cellWindow = new Window
        {
            Width = 300,
            Height = 200,
            Content = new Grid { Children = { cell } }
        };
        try
        {
            panelWindow.Show();
            panelWindow.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(400d, stacked.Bounds.Width);
            Assert.Equal(400d, stacked.Bounds.Height);

            cellWindow.Show();
            cellWindow.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(200d, cell.Bounds.Width);
            Assert.Equal(200d, cell.Bounds.Height);
        }
        finally
        {
            panelWindow.Close();
            cellWindow.Close();
        }
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (MptQrCode Code, Window Window) ShowCode(
        string? code,
        double controlSide,
        double surfaceSide,
        IBrush? surface = null)
    {
        var qr = new MptQrCode
        {
            Value = code,
            Width = controlSide,
            Height = controlSide,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var window = new Window
        {
            Width = surfaceSide,
            Height = surfaceSide,
            Content = new Border { Background = surface ?? new SolidColorBrush(DarkSurface), Child = qr }
        };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(1d, window.RenderScaling);
        return (qr, window);
    }

    /// <summary>The control's rectangle in framebuffer pixels.</summary>
    private static PixelRect ControlArea(MptQrCode qr, Window window)
    {
        var origin = qr.TranslatePoint(default, window);
        Assert.NotNull(origin);
        var area = new PixelRect(
            (int)Math.Round(origin!.Value.X),
            (int)Math.Round(origin.Value.Y),
            (int)Math.Round(qr.Bounds.Width),
            (int)Math.Round(qr.Bounds.Height));
        Assert.True(area.Width > 0 && area.Height > 0, "the control was not laid out");
        return area;
    }

    private static (int ModulePixels, int Modules) MeasureSymbol(Pixels pixels, PixelRect area)
    {
        var dark = pixels.BlackBounds(area);
        var modulePixels = pixels.FirstBlackRun(dark.X, dark.Y, dark.Right) / 7;
        Assert.True(modulePixels >= 1, $"module size {modulePixels}px");
        return (modulePixels, dark.Width / modulePixels);
    }

    private static Pixels Capture(Window window)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var buffer = frame!.Lock();
        var height = buffer.Size.Height;
        var stride = buffer.RowBytes;
        var raw = new byte[stride * height];
        Marshal.Copy(buffer.Address, raw, 0, raw.Length);
        return new Pixels(raw, buffer.Size.Width, height, stride, buffer.Format);
    }

    private static string? Decode(Pixels pixels)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = new DecodingOptions
            {
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                TryHarder = true,
                CharacterSet = "UTF-8"
            }
        };
        var source = new PlanarYUVLuminanceSource(
            pixels.ToLuminance(), pixels.Width, pixels.Height, 0, 0, pixels.Width, pixels.Height, false);
        return reader.Decode(source)?.Text;
    }

    /// <summary>The framebuffer as pixels, so assertions can talk about the drawn symbol.</summary>
    private sealed class Pixels(byte[] raw, int width, int height, int stride, PixelFormat? format)
    {
        private readonly int _bytesPerPixel = format is not null && format.Value.BitsPerPixel >= 32 ? 4 : 3;
        private readonly int _redOffset = format == PixelFormats.Rgba8888 ? 0 : 2;
        private readonly int _blueOffset = format == PixelFormats.Rgba8888 ? 2 : 0;

        public int Width { get; } = width;

        public int Height { get; } = height;

        public Color At(int x, int y) => Color.FromRgb(Channel(x, y, 0), Channel(x, y, 1), Channel(x, y, 2));

        public bool IsBlack(int x, int y) => Channel(x, y, 0) == 0 && Channel(x, y, 1) == 0 && Channel(x, y, 2) == 0;

        public bool IsWhite(int x, int y) => Channel(x, y, 0) == 255 && Channel(x, y, 1) == 255 && Channel(x, y, 2) == 255;

        /// <summary>Bounding box of the strictly black pixels inside <paramref name="area"/>.</summary>
        public PixelRect BlackBounds(PixelRect area) => Bounds(area, IsBlack);

        /// <summary>Bounding box of the strictly white pixels inside <paramref name="area"/>.</summary>
        public PixelRect WhiteBounds(PixelRect area) => Bounds(area, IsWhite);

        /// <summary>Counts pixels inside <paramref name="area"/> that are neither black nor white.</summary>
        public int CountBlended(PixelRect area)
        {
            var blended = 0;
            for (var y = Math.Max(0, area.Y); y < Math.Min(Height, area.Bottom); y++)
            {
                for (var x = Math.Max(0, area.X); x < Math.Min(Width, area.Right); x++)
                {
                    if (!IsWhite(x, y) && !IsBlack(x, y))
                    {
                        blended++;
                    }
                }
            }

            return blended;
        }

        /// <summary>Length of the black run that starts at (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public int FirstBlackRun(int x, int y, int limit)
        {
            var length = 0;
            for (var column = x; column < Math.Min(Width, limit) && IsBlack(column, y); column++)
            {
                length++;
            }

            return length;
        }

        public byte[] ToLuminance()
        {
            var luminance = new byte[Width * Height];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    // The symbol is black or white, so any channel carries the same information.
                    luminance[(y * Width) + x] = Channel(x, y, 1);
                }
            }

            return luminance;
        }

        private PixelRect Bounds(PixelRect area, Func<int, int, bool> matches)
        {
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;
            for (var y = Math.Max(0, area.Y); y < Math.Min(Height, area.Bottom); y++)
            {
                for (var x = Math.Max(0, area.X); x < Math.Min(Width, area.Right); x++)
                {
                    if (!matches(x, y))
                    {
                        continue;
                    }

                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            return maxX < minX ? default : new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        private byte Channel(int x, int y, int channel)
        {
            var index = (y * stride) + (x * _bytesPerPixel);
            return channel switch
            {
                0 => raw[index + _redOffset],
                1 => raw[index + 1],
                _ => raw[index + _blueOffset]
            };
        }
    }
}
