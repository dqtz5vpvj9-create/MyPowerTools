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
        using var centerBrush = new SolidBrush(Color.FromArgb(245, 17, 22, 29));
        graphics.FillEllipse(centerBrush, new RectangleF(10f, 10f, 44f, 44f));
        graphics.DrawEllipse(trackPen, ringBounds);
        if (remainingPercent > 0)
        {
            graphics.DrawArc(accentPen, ringBounds, -90f, remainingPercent * 3.6f);
        }

        var text = remainingPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var fontSize = text.Length switch
        {
            1 => 40f,
            2 => 36f,
            _ => 27f
        };
        using var fontFamily = new FontFamily("Segoe UI");
        using var textPath = new GraphicsPath();
        textPath.AddString(
            text,
            fontFamily,
            (int)FontStyle.Bold,
            fontSize,
            PointF.Empty,
            StringFormat.GenericTypographic);
        var pathBounds = textPath.GetBounds();
        using var transform = new Matrix();
        transform.Translate(
            (CanvasSize - pathBounds.Width) / 2f - pathBounds.X,
            (CanvasSize - pathBounds.Height) / 2f - pathBounds.Y - 1f);
        textPath.Transform(transform);

        using var outlinePen = new Pen(Color.FromArgb(255, 0, 0, 0), 2.5f)
        {
            LineJoin = LineJoin.Round
        };
        using var textBrush = new SolidBrush(Color.White);
        graphics.DrawPath(outlinePen, textPath);
        graphics.FillPath(textBrush, textPath);
        return bitmap.GetHicon();
    }
}
