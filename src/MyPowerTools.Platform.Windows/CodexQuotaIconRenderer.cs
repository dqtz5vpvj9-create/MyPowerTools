using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MyPowerTools.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class CodexQuotaIconRenderer
{
    private const int CanvasSize = 64;
    private const int PixelFormat32bppArgb = 0x26200A;
    private const int SmoothingModeAntiAlias = 4;
    private const int TextRenderingHintAntiAliasGridFit = 3;
    private const int LineCapRound = 2;
    private const int LineJoinRound = 2;
    private const int FontStyleBold = 1;
    private const int FillModeAlternate = 0;

    private static readonly object GdiPlusSync = new();
    private static IntPtr _gdiPlusToken;

    public static IntPtr CreateIcon(int remainingPercent)
    {
        remainingPercent = Math.Clamp(remainingPercent, 0, 100);
        EnsureGdiPlusInitialized();

        IntPtr bitmap = IntPtr.Zero;
        IntPtr graphics = IntPtr.Zero;
        IntPtr centerBrush = IntPtr.Zero;
        IntPtr trackPen = IntPtr.Zero;
        IntPtr accentPen = IntPtr.Zero;
        IntPtr fontFamily = IntPtr.Zero;
        IntPtr genericTypographic = IntPtr.Zero;
        IntPtr textPath = IntPtr.Zero;
        IntPtr matrix = IntPtr.Zero;
        IntPtr outlinePen = IntPtr.Zero;
        IntPtr textBrush = IntPtr.Zero;

        try
        {
            Check(GdipCreateBitmapFromScan0(CanvasSize, CanvasSize, 0, PixelFormat32bppArgb, IntPtr.Zero, out bitmap));
            Check(GdipGetImageGraphicsContext(bitmap, out graphics));
            Check(GdipGraphicsClear(graphics, 0x00000000u));
            Check(GdipSetSmoothingMode(graphics, SmoothingModeAntiAlias));
            Check(GdipSetTextRenderingHint(graphics, TextRenderingHintAntiAliasGridFit));

            uint accent = remainingPercent switch
            {
                >= 50 => Argb(255, 25, 195, 125),
                >= 20 => Argb(255, 250, 170, 45),
                _ => Argb(255, 245, 75, 85)
            };

            Check(GdipCreateSolidFill(Argb(245, 17, 22, 29), out centerBrush));
            Check(GdipFillEllipse(graphics, centerBrush, 10f, 10f, 44f, 44f));

            Check(GdipCreatePen1(Argb(210, 68, 75, 88), 7f, 0, out trackPen));
            Check(GdipSetPenStartCap(trackPen, LineCapRound));
            Check(GdipSetPenEndCap(trackPen, LineCapRound));
            Check(GdipDrawEllipse(graphics, trackPen, 6.5f, 6.5f, 51f, 51f));

            if (remainingPercent > 0)
            {
                Check(GdipCreatePen1(accent, 7f, 0, out accentPen));
                Check(GdipSetPenStartCap(accentPen, LineCapRound));
                Check(GdipSetPenEndCap(accentPen, LineCapRound));
                Check(GdipDrawArc(graphics, accentPen, 6.5f, 6.5f, 51f, 51f, -90f, remainingPercent * 3.6f));
            }

            var text = remainingPercent.ToString(CultureInfo.InvariantCulture);
            float fontSize = text.Length switch
            {
                1 => 40f,
                2 => 36f,
                _ => 27f
            };

            if (GdipCreateFontFamilyFromName("Segoe UI", IntPtr.Zero, out fontFamily) != 0)
            {
                Check(GdipGetGenericFontFamilySansSerif(out fontFamily));
            }

            Check(GdipStringFormatGetGenericTypographic(out genericTypographic));
            Check(GdipCreatePath(FillModeAlternate, out textPath));
            var origin = new RectF { X = 0f, Y = 0f, Width = 0f, Height = 0f };
            Check(GdipAddPathString(textPath, text, text.Length, fontFamily, FontStyleBold, fontSize, ref origin, genericTypographic));

            Check(GdipGetPathWorldBounds(textPath, out RectF bounds, IntPtr.Zero, IntPtr.Zero));
            Check(GdipCreateMatrix(out matrix));
            Check(GdipTranslateMatrix(
                matrix,
                (CanvasSize - bounds.Width) / 2f - bounds.X,
                (CanvasSize - bounds.Height) / 2f - bounds.Y - 1f,
                0));
            Check(GdipTransformPath(textPath, matrix));

            Check(GdipCreatePen1(Argb(255, 0, 0, 0), 2.5f, 0, out outlinePen));
            Check(GdipSetPenLineJoin(outlinePen, LineJoinRound));
            Check(GdipCreateSolidFill(Argb(255, 255, 255, 255), out textBrush));
            Check(GdipDrawPath(graphics, outlinePen, textPath));
            Check(GdipFillPath(graphics, textBrush, textPath));

            Check(GdipCreateHICONFromBitmap(bitmap, out IntPtr hIcon));
            return hIcon;
        }
        finally
        {
            if (textBrush != IntPtr.Zero) GdipDeleteBrush(textBrush);
            if (outlinePen != IntPtr.Zero) GdipDeletePen(outlinePen);
            if (matrix != IntPtr.Zero) GdipDeleteMatrix(matrix);
            if (textPath != IntPtr.Zero) GdipDeletePath(textPath);
            if (genericTypographic != IntPtr.Zero) GdipDeleteStringFormat(genericTypographic);
            if (fontFamily != IntPtr.Zero) GdipDeleteFontFamily(fontFamily);
            if (accentPen != IntPtr.Zero) GdipDeletePen(accentPen);
            if (trackPen != IntPtr.Zero) GdipDeletePen(trackPen);
            if (centerBrush != IntPtr.Zero) GdipDeleteBrush(centerBrush);
            if (graphics != IntPtr.Zero) GdipDeleteGraphics(graphics);
            if (bitmap != IntPtr.Zero) GdipDisposeImage(bitmap);
        }
    }

    private static uint Argb(byte a, byte r, byte g, byte b) =>
        ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;

    internal static void EnsureGdiPlusInitialized()
    {
        if (_gdiPlusToken != IntPtr.Zero)
        {
            return;
        }

        lock (GdiPlusSync)
        {
            if (_gdiPlusToken != IntPtr.Zero)
            {
                return;
            }

            var input = new GdiplusStartupInput { GdiplusVersion = 1 };
            int status = GdiplusStartup(out var token, ref input, IntPtr.Zero);
            if (status != 0)
            {
                throw new InvalidOperationException($"GdiplusStartup failed with status {status}.");
            }

            _gdiPlusToken = token;
        }
    }

    private static void Check(int status)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"GDI+ call failed with status {status}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectF
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
    }

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr scan0, out IntPtr bitmap);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDisposeImage(IntPtr image);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteGraphics(IntPtr graphics);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGraphicsClear(IntPtr graphics, uint color);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetSmoothingMode(IntPtr graphics, int smoothingMode);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetTextRenderingHint(IntPtr graphics, int mode);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateSolidFill(uint color, out IntPtr brush);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteBrush(IntPtr brush);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreatePen1(uint color, float width, int unit, out IntPtr pen);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeletePen(IntPtr pen);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetPenStartCap(IntPtr pen, int startCap);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetPenEndCap(IntPtr pen, int endCap);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetPenLineJoin(IntPtr pen, int lineJoin);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipFillEllipse(IntPtr graphics, IntPtr brush, float x, float y, float width, float height);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDrawEllipse(IntPtr graphics, IntPtr pen, float x, float y, float width, float height);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDrawArc(IntPtr graphics, IntPtr pen, float x, float y, float width, float height, float startAngle, float sweepAngle);

    [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GdipCreateFontFamilyFromName(string name, IntPtr fontCollection, out IntPtr fontFamily);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGetGenericFontFamilySansSerif(out IntPtr fontFamily);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteFontFamily(IntPtr fontFamily);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipStringFormatGetGenericTypographic(out IntPtr format);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteStringFormat(IntPtr format);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreatePath(int fillMode, out IntPtr path);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeletePath(IntPtr path);

    [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GdipAddPathString(IntPtr path, string text, int length, IntPtr family, int style, float emSize, ref RectF layoutRect, IntPtr format);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGetPathWorldBounds(IntPtr path, out RectF bounds, IntPtr matrix, IntPtr pen);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateMatrix(out IntPtr matrix);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteMatrix(IntPtr matrix);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipTranslateMatrix(IntPtr matrix, float offsetX, float offsetY, int order);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipTransformPath(IntPtr path, IntPtr matrix);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDrawPath(IntPtr graphics, IntPtr pen, IntPtr path);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipFillPath(IntPtr graphics, IntPtr brush, IntPtr path);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateHICONFromBitmap(IntPtr bitmap, out IntPtr hIcon);
}
