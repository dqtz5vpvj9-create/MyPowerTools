using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace MyPowerTools.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class CodexQuotaIconRenderer
{
    private const int CanvasSize = 64;

    public static IntPtr CreateIcon(int remainingPercent)
    {
        remainingPercent = Math.Clamp(remainingPercent, 0, 100);
        using var bitmap = new Bitmap(CanvasSize, CanvasSize, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var accent = remainingPercent switch
        {
            >= 50 => Color.FromArgb(255, 25, 195, 125),
            >= 20 => Color.FromArgb(255, 250, 170, 45),
            _ => Color.FromArgb(255, 245, 75, 85)
        };
        var ringBounds = new RectangleF(6.5f, 6.5f, 51f, 51f);
        using var trackPen = new Pen(Color.FromArgb(210, 68, 75, 88), 7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var accentPen = new Pen(accent, 7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawEllipse(trackPen, ringBounds);
        if (remainingPercent > 0)
        {
            graphics.DrawArc(accentPen, ringBounds, -90f, remainingPercent * 3.6f);
        }

        var text = remainingPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var fontSize = remainingPercent >= 100 ? 19f : 24f;
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var shadowBrush = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
        using var textBrush = new SolidBrush(Color.White);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        var textBounds = new RectangleF(1f, 1f, CanvasSize - 2f, CanvasSize - 2f);
        graphics.DrawString(text, font, shadowBrush, textBounds with { X = 2f, Y = 2f }, format);
        graphics.DrawString(text, font, textBrush, textBounds, format);
        return bitmap.GetHicon();
    }
}
